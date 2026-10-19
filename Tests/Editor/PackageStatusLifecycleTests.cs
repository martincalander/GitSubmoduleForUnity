using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEngine;
using UnityEngine.UIElements;
using UnityEngine.TestTools;

namespace MartinCalander.GitSubmoduleManager.Editor.Tests
{
    public sealed class PackageStatusLifecycleTests
    {
        [TestCase(1)]
        [TestCase(2)]
        [Category("PackageSourceFeatures")]
        public void FinalReleaseCancelsStaleWorkAndRapidReopenQueuesAFreshGeneration(int observerCount)
        {
            const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic;
            Type statusType = typeof(PackageManagerPackageStatus);
            var saved = new Dictionary<FieldInfo, object>();
            foreach (string name in new[] { "observers", "generation", "workerGeneration", "worker", "workerPackages",
                         "cancellation", "refreshPending", "listening", "shuttingDown" })
            {
                FieldInfo field = statusType.GetField(name, flags);
                Assert.That(field, Is.Not.Null, name);
                saved.Add(field, field.GetValue(null));
            }
            var state = (PackageManagerPackageStatus.StatusState)statusType.GetField("State", flags).GetValue(null);
            FieldInfo readiness = typeof(PackageManagerPackageStatus.StatusState)
                .GetField("<IsReady>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic);
            bool wasReady = state.IsReady;
            var published = state.Snapshot;
            var pending = new TaskCompletionSource<PackageManagerPackageStatus.StatusSnapshot>();
            using var cancellation = new CancellationTokenSource();
            var update = (EditorApplication.CallbackFunction)Delegate.CreateDelegate(typeof(EditorApplication.CallbackFunction),
                statusType.GetMethod("Update", flags));
            var registered = (Action<PackageRegistrationEventArgs>)Delegate.CreateDelegate(typeof(Action<PackageRegistrationEventArgs>),
                statusType.GetMethod("OnRegisteredPackages", flags));
            object Get(string name) => statusType.GetField(name, flags).GetValue(null);
            void Set(string name, object value) => statusType.GetField(name, flags).SetValue(null, value);

            // Stay synchronous so the Editor cannot observe the isolated worker state.
            try
            {
                Set("observers", observerCount);
                Set("generation", 100);
                Set("workerGeneration", 100);
                Set("worker", pending.Task);
                Set("cancellation", cancellation);
                Set("refreshPending", true);
                Set("listening", true);
                Set("shuttingDown", false);

                PackageManagerPackageStatus.Release();
                if (observerCount == 2)
                {
                    Assert.That(cancellation.IsCancellationRequested, Is.False,
                        "A remaining Package Manager session still owns the scan.");
                    Assert.That(Get("generation"), Is.EqualTo(100));
                    Assert.That(Get("refreshPending"), Is.True);
                    PackageManagerPackageStatus.Release();
                }
                Assert.That(Get("observers"), Is.EqualTo(0));
                Assert.That(cancellation.IsCancellationRequested, Is.True);
                Assert.That(Get("generation"), Is.EqualTo(101));
                Assert.That(Get("refreshPending"), Is.False);

                PackageManagerPackageStatus.Retain();
                Assert.That(Get("observers"), Is.EqualTo(1));
                Assert.That(Get("generation"), Is.EqualTo(102));
                Assert.That(Get("refreshPending"), Is.True);
                Assert.That(Get("worker"), Is.SameAs(pending.Task), "Reopening must not overlap two Git scans.");
                Assert.That(state.IsReady, Is.False);

                PackageManagerPackageStatus.Release();
                pending.SetResult(new PackageManagerPackageStatus.StatusSnapshot { Error = "Discard this cancelled generation." });
                update();

                Assert.That(state.Snapshot, Is.SameAs(published), "A cancelled worker must not publish stale status.");
                Assert.That(Get("worker"), Is.Null);
                Assert.That(Get("cancellation"), Is.Null);
                Assert.That(Get("refreshPending"), Is.False);
                Assert.That(Get("listening"), Is.False, "The final completed worker must release its Editor update callback.");
            }
            finally
            {
                foreach (KeyValuePair<FieldInfo, object> entry in saved)
                    entry.Key.SetValue(null, entry.Value);
                readiness.SetValue(state, wasReady);
                typeof(PackageManagerPackageStatus.StatusState)
                    .GetField("<Snapshot>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(state, published);
                EditorApplication.update -= update;
                if ((bool)Get("listening"))
                    EditorApplication.update += update;
                Events.registeredPackages -= registered;
                if ((int)Get("observers") > 0 && !(bool)Get("shuttingDown"))
                    Events.registeredPackages += registered;
            }
        }

        [Test]
        [Category("PackageManagerCompatibility")]
        public void NativeIntegration_DoesNotRegisterALegacyDetailsExtension()
        {
            AssertNoLegacyDetailsRegistration();
        }

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
                    Assert.That(root.Q("git-submodule-manager-submodules-only"), Is.Null,
                        "Play Mode must not recreate the removed custom filter.");
                    Assert.That(root.Q("git-submodule-manager-submodules-sidebar-row"), Is.Null,
                        "Play Mode must not recreate the former Submodules sidebar row.");
                    Assert.That(root.Q("git-submodule-package-details"), Is.Null,
                        "Play Mode must not recreate the removed custom details panel.");
                    Assert.That(root.Q("git-submodule-open-repository"), Is.Null);
                    Assert.That(root.Q("git-submodule-reveal-folder"), Is.Null);
                    Assert.That(root.Q("git-submodule-copy-commit"), Is.Null);
                    Assert.That(root.Q("git-submodule-copy-source"), Is.Null);
                    AssertNoLegacyDetailsRegistration();
                    Assert.That(PackageManagerSubmoduleHarmonyPatch.IsPatched(
                        PackageRepositoryLink.GetRefreshTarget()), Is.True,
                        "The native Repository link must survive disabled-domain-reload Play Mode.");
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

        private static void AssertNoLegacyDetailsRegistration()
        {
            PropertyInfo registry = typeof(UnityEditor.PackageManager.UI.PackageManagerExtensions)
                .GetProperty("Extensions", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.That(registry, Is.Not.Null);
            var extensions = registry.GetValue(null) as IEnumerable;
            Assert.That(extensions, Is.Not.Null);
            foreach (object extension in extensions)
                Assert.That(extension?.GetType().Assembly,
                    Is.Not.SameAs(typeof(GitSubmoduleManagerPackageManagerHost).Assembly),
                    "Native integration must not register a package-owned legacy details extension.");
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
