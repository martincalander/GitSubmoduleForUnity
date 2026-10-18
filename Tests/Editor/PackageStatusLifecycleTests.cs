using System.Collections;
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
            PackageManagerPackageStatus.Retain();
            try
            {
                EditorSettings.enterPlayModeOptionsEnabled = true;
                EditorSettings.enterPlayModeOptions = EnterPlayModeOptions.DisableDomainReload;
                for (int cycle = 0; cycle < 2; cycle++)
                {
                    yield return new EnterPlayMode();
                    Assert.That(PackageManagerSubmoduleNativePage.IsSupportedContract(out string error), Is.True, error);
                    Assert.That(PackageManagerSubmoduleHarmonyPatch.IsPatched(
                        PackageManagerSubmoduleHarmonyPatch.FindMethod("PackageOperationDispatcher", "RemoveEmbedded", typeof(void), "IPackage")), Is.True);
                    yield return new ExitPlayMode();
                    Assert.That(PackageManagerSubmoduleNativePage.IsSupportedContract(out error), Is.True, error);
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
            }
        }
    }

    internal sealed class StatusHostTestWindow : EditorWindow { }
}
