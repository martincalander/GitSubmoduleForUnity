using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using UnityEngine.TestTools;

namespace MartinCalander.GitSubmoduleManager.Editor.Tests
{
    public sealed class PackageStatusLifecycleTests
    {
        [UnityTest]
        public IEnumerator DetachedRootsDoNotRetainStatusObserversOrScheduledWork()
        {
            int sessions = GitSubmoduleManagerPackageManagerHost.SessionCount;
            int pending = GitSubmoduleManagerPackageManagerHost.PendingAttachmentCount;
            var window = ScriptableObject.CreateInstance<StatusHostTestWindow>();
            var root = new VisualElement();
            try
            {
                GitSubmoduleManagerPackageManagerHost.ObserveRoot(root);
                Assert.That(GitSubmoduleManagerPackageManagerHost.SessionCount, Is.EqualTo(sessions));
                window.Show();
                window.rootVisualElement.Add(root);
                yield return null;
                GitSubmoduleManagerPackageManagerHost.ObserveRoot(root);
                GitSubmoduleManagerPackageManagerHost.ObserveRoot(root);
                Assert.That(GitSubmoduleManagerPackageManagerHost.SessionCount, Is.EqualTo(sessions + 1));
                Assert.That(GitSubmoduleManagerPackageManagerHost.PendingAttachmentCount, Is.EqualTo(pending + 1));

                root.RemoveFromHierarchy();
                Assert.That(GitSubmoduleManagerPackageManagerHost.SessionCount, Is.EqualTo(sessions));
                Assert.That(GitSubmoduleManagerPackageManagerHost.PendingAttachmentCount, Is.EqualTo(pending));

                window.rootVisualElement.Add(root);
                GitSubmoduleManagerPackageManagerHost.ObserveRoot(root);
                Assert.That(GitSubmoduleManagerPackageManagerHost.SessionCount, Is.EqualTo(sessions + 1));
                window.Close();
                Assert.That(GitSubmoduleManagerPackageManagerHost.SessionCount, Is.EqualTo(sessions));
                Assert.That(GitSubmoduleManagerPackageManagerHost.PendingAttachmentCount, Is.EqualTo(pending));
            }
            finally
            {
                root.RemoveFromHierarchy();
                if (window != null)
                    window.Close();
            }
        }

        [UnityTest]
        public IEnumerator FailedAttachmentStopsRetryingAndCanResumeOnALaterObservation()
        {
            int sessions = GitSubmoduleManagerPackageManagerHost.SessionCount;
            int pending = GitSubmoduleManagerPackageManagerHost.PendingAttachmentCount;
            var window = ScriptableObject.CreateInstance<StatusHostTestWindow>();
            var root = new VisualElement();
            try
            {
                window.Show();
                window.rootVisualElement.Add(root);
                yield return null;
                GitSubmoduleManagerPackageManagerHost.ObserveRoot(root);
                double deadline = EditorApplication.timeSinceStartup + 10;
                while (GitSubmoduleManagerPackageManagerHost.PendingAttachmentCount > pending &&
                       EditorApplication.timeSinceStartup < deadline)
                    yield return null;
                Assert.That(GitSubmoduleManagerPackageManagerHost.PendingAttachmentCount, Is.EqualTo(pending),
                    "Unavailable native UI must not cause permanent retries.");
                Assert.That(GitSubmoduleManagerPackageManagerHost.SessionCount, Is.EqualTo(sessions + 1));

                GitSubmoduleManagerPackageManagerHost.ObserveRoot(root);
                Assert.That(GitSubmoduleManagerPackageManagerHost.PendingAttachmentCount, Is.EqualTo(pending + 1),
                    "A later attachment observation must recover after a construction timeout.");
            }
            finally
            {
                root.RemoveFromHierarchy();
                if (window != null)
                    window.Close();
            }
            Assert.That(GitSubmoduleManagerPackageManagerHost.SessionCount, Is.EqualTo(sessions));
            Assert.That(GitSubmoduleManagerPackageManagerHost.PendingAttachmentCount, Is.EqualTo(pending));
        }

        [UnityTest]
        [Category("PlayModeLifecycle")]
        public IEnumerator StatusAndRemovalGuardsSurviveTwoPlayModeCycles()
        {
            bool enabled = EditorSettings.enterPlayModeOptionsEnabled;
            EnterPlayModeOptions options = EditorSettings.enterPlayModeOptions;
            Type windowType = typeof(UnityEditor.PackageManager.UI.IPackageManagerExtension).Assembly
                .GetType(GitSubmoduleManagerPackageManagerHost.WindowTypeName);
            EditorWindow window = null;
            foreach (EditorWindow candidate in Resources.FindObjectsOfTypeAll<EditorWindow>())
                if (candidate.GetType() == windowType)
                    window = candidate;
            bool ownsWindow = window == null;
            window = window != null ? window : EditorWindow.GetWindow(windowType);
            PackageManagerPackageStatus.Retain();
            try
            {
                EditorSettings.enterPlayModeOptionsEnabled = true;
                EditorSettings.enterPlayModeOptions = EnterPlayModeOptions.DisableDomainReload;
                yield return null;
                var initialRoot = PackageManagerSubmoduleHarmonyPatch.Read(window, "m_Root") as VisualElement;
                Assert.That(initialRoot, Is.Not.Null);
                GitSubmoduleManagerPackageManagerHost.ObserveRoot(initialRoot);
                for (int cycle = 0; cycle < 2; cycle++)
                {
                    yield return new EnterPlayMode();
                    Assert.That(PackageManagerSubmoduleNativePage.IsSupportedContract(out string error), Is.True, error);
                    Assert.That(PackageManagerSubmoduleHarmonyPatch.IsPatched(
                        PackageManagerSubmoduleHarmonyPatch.FindMethod("PackageOperationDispatcher", "RemoveEmbedded", typeof(void), "IPackage")), Is.True);
                    yield return new ExitPlayMode();
                    Assert.That(PackageManagerSubmoduleNativePage.IsSupportedContract(out error), Is.True, error);
                    Assert.That(PackageManagerSubmoduleHarmonyPatch.IsSidebarRepairInstalled, Is.True);
                    var root = PackageManagerSubmoduleHarmonyPatch.Read(window, "m_Root") as VisualElement;
                    VisualElement sidebar = FindSidebar(root);
                    Assert.That(sidebar, Is.Not.Null);
                    PackageManagerSubmoduleNativePage.GetSidebarRefreshTarget().Invoke(sidebar, null);
                    double repairDeadline = EditorApplication.timeSinceStartup + 10;
                    while (GitSubmoduleManagerPackageManagerHost.PendingAttachmentCount > 0 &&
                           EditorApplication.timeSinceStartup < repairDeadline)
                        yield return null;
                    Assert.That(GitSubmoduleManagerPackageManagerHost.PendingAttachmentCount, Is.Zero);
                    var rows = new List<VisualElement>();
                    CollectGitHubRows(sidebar, rows);
                    Assert.That(rows, Has.Count.EqualTo(1), "Native sidebar repair must survive disabled-domain-reload Play Mode.");
                    var sources = (sidebar.GetType().GetMethod("GetRow",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                        null, new[] { typeof(string) }, null).Invoke(sidebar, new object[] { "MyAssets" }) as VisualElement)?.parent;
                    Assert.That(rows[0].parent, Is.SameAs(sources));
                    Assert.That(root.Q<Toggle>(PackageManagerSubmoduleFilter.ToggleElementName), Is.Not.Null,
                        "The Submodules only filter must survive disabled-domain-reload Play Mode.");
                    Assert.That(root.Q("git-submodule-manager-submodules-sidebar-row"), Is.Null,
                        "Submodules filtering must not recreate the former sidebar row.");
                    Assert.That(PackageSourceDetails.Unavailable, Is.Not.Null);
                    var details = new PackageStatusDetailsView();
                    details.Refresh(null);
                    Assert.That(details.Q<Button>("git-submodule-reveal-folder").enabledSelf, Is.False);
                    PackageManagerPackageStatus.Refresh();
                    double deadline = EditorApplication.timeSinceStartup + 20;
                    while (!PackageManagerPackageStatus.IsReady && EditorApplication.timeSinceStartup < deadline)
                        yield return null;
                    Assert.That(PackageManagerPackageStatus.IsReady, Is.True);
                    Assert.That(PackageManagerSubmoduleHarmonyPatch.IsPatched(
                        PackageManagerSubmoduleHarmonyPatch.FindMethod("PackageOperationDispatcher", "RemoveEmbedded", typeof(void), "IPackage")), Is.True);
                }
            }
            finally
            {
                EditorSettings.enterPlayModeOptionsEnabled = enabled;
                EditorSettings.enterPlayModeOptions = options;
                PackageManagerPackageStatus.Release();
                if (ownsWindow && window != null)
                    window.Close();
            }
        }

        private static VisualElement FindSidebar(VisualElement element)
        {
            if (element == null || element.GetType().FullName == PackageManagerSubmoduleNativePage.SidebarTypeName)
                return element;
            foreach (VisualElement child in element.Children())
            {
                VisualElement sidebar = FindSidebar(child);
                if (sidebar != null)
                    return sidebar;
            }
            return null;
        }

        private static void CollectGitHubRows(VisualElement element, List<VisualElement> rows)
        {
            if (element.GetType().FullName == PackageManagerSubmoduleNativePage.SidebarRowTypeName &&
                (string)PackageManagerSubmoduleHarmonyPatch.Read(element, "pageId") == PackageManagerSubmoduleNativePage.ExtensionPageId)
                rows.Add(element);
            foreach (VisualElement child in element.Children())
                CollectGitHubRows(child, rows);
        }
    }

    internal sealed class StatusHostTestWindow : EditorWindow { }
}
