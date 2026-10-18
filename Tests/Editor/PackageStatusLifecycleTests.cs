using System.Collections;
using NUnit.Framework;
using UnityEditor;
using UnityEngine.TestTools;

namespace MartinCalander.GitSubmoduleManager.Editor.Tests
{
    public sealed class PackageStatusLifecycleTests
    {
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
                    Assert.That(PackageManagerSubmoduleHarmonyPatch.IsPatched(
                        PackageManagerSubmoduleHarmonyPatch.FindMethod("PackageOperationDispatcher", "RemoveEmbedded", typeof(void), "IPackage")), Is.True);
                    yield return new ExitPlayMode();
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
}
