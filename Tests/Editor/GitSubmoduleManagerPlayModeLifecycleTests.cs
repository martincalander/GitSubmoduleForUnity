using System.Collections;
using System.Diagnostics;
using NUnit.Framework;
using UnityEditor;
using UnityEngine.TestTools;

namespace MartinCalander.GitSubmoduleManager.Editor.Tests
{
    [Parallelizable(ParallelScope.None)]
    [Category("PlayModeLifecycle")]
    public sealed partial class GitSubmoduleManagerPlayModeLifecycleTests
    {
#if UNITY_6000_5_OR_NEWER
        [Unity.Scripting.LifecycleManagement.NoAutoStaticsCleanup]
#endif
        private static LifecycleProbe activeProbe;

        private bool settingsCaptured;
        private bool previousOptionsEnabled;
        private EnterPlayModeOptions previousOptions;

        [UnityTest]
        public IEnumerator DisabledDomainReload_PreservesEditorServicesAcrossTwoPlaySessions()
        {
            if (!PackageManagerUnityVersionSupport.IsCurrentVersionSupported)
            {
                Assert.Ignore(
                    "Package Manager lifecycle verification requires a supported Editor.");
            }

            if (GitOperationService.IsBusy ||
                PackageDependencyInstallPipeline.IsBusy ||
                PackageManagerReadOnlyGitInstallService.IsBusy ||
                PackageManagerProjectResolutionService.IsBusy ||
                PackageManagerNativeRemoveHandoffService.IsBusy)
            {
                Assert.Ignore(
                    "An active package operation must finish before the Play Mode test.");
            }

            Assert.That(EditorApplication.isPlaying, Is.False);
            Assert.That(
                GitSubmoduleManagerPackageManagerHost.TryPatch(out string hostError),
                Is.True,
                hostError);
            Assert.That(PackageManagerSubmoduleHarmonyPatch.TryPatch(), Is.True,
                PackageManagerSubmoduleHarmonyPatch.LastPatchError);

            previousOptionsEnabled = EditorSettings.enterPlayModeOptionsEnabled;
            previousOptions = EditorSettings.enterPlayModeOptions;
            settingsCaptured = true;
            EditorSettings.enterPlayModeOptions =
                EnterPlayModeOptions.DisableDomainReload;
            EditorSettings.enterPlayModeOptionsEnabled = true;

            var probe = new LifecycleProbe();
            activeProbe = probe;
#if !UNITY_7000_0_OR_NEWER
            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
            AssemblyReloadEvents.beforeAssemblyReload -= OnCodeUnloading;
            AssemblyReloadEvents.beforeAssemblyReload += OnCodeUnloading;
#endif

            GitSubmoduleManagerSetupProbe setupProbe =
                GitSubmoduleManagerSetupProbe.Shared;
            long repositoryGeneration = GitOperationService.RepositoryGeneration;
            string recoveryWarning = GitOperationService.RecoveryWarning;
            AssertEditorServicesSurvive(setupProbe, repositoryGeneration, recoveryWarning);

            for (int cycle = 1; cycle <= 2; cycle++)
            {
                var elapsed = Stopwatch.StartNew();
                yield return new EnterPlayMode(expectDomainReload: false);
                elapsed.Stop();
                TestContext.WriteLine(
                    $"Play session {cycle}: entered in {elapsed.Elapsed.TotalMilliseconds:F1} ms.");
                yield return null;

                Assert.That(EditorApplication.isPlaying, Is.True);
                Assert.That(activeProbe, Is.SameAs(probe),
                    "Entering Play Mode must preserve the existing static state.");
                Assert.That(probe.ExitingEditModeCount, Is.EqualTo(cycle));
                Assert.That(probe.EnteringPlayModeCount, Is.EqualTo(cycle));
                Assert.That(probe.ExitingPlayModeCount, Is.EqualTo(cycle - 1));
                Assert.That(probe.EnteringEditModeCount, Is.EqualTo(cycle - 1));
                Assert.That(probe.CodeUnloadingCount, Is.Zero,
                    "Entering Play Mode must not run code-unload cleanup.");
                AssertEditorServicesSurvive(setupProbe, repositoryGeneration, recoveryWarning);

                elapsed.Restart();
                yield return new ExitPlayMode();
                elapsed.Stop();
                TestContext.WriteLine(
                    $"Play session {cycle}: exited in {elapsed.Elapsed.TotalMilliseconds:F1} ms.");
                yield return null;

                Assert.That(EditorApplication.isPlaying, Is.False);
                Assert.That(activeProbe, Is.SameAs(probe),
                    "Returning to Edit Mode must preserve the existing static state.");
                Assert.That(probe.ExitingEditModeCount, Is.EqualTo(cycle));
                Assert.That(probe.EnteringPlayModeCount, Is.EqualTo(cycle));
                Assert.That(probe.ExitingPlayModeCount, Is.EqualTo(cycle));
                Assert.That(probe.EnteringEditModeCount, Is.EqualTo(cycle));
                Assert.That(probe.CodeUnloadingCount, Is.Zero,
                    "Returning to Edit Mode must not run code-unload cleanup.");
                AssertEditorServicesSurvive(setupProbe, repositoryGeneration, recoveryWarning);
            }
        }

        [UnityTearDown]
        public IEnumerator RestoreEditorSettings()
        {
            try
            {
                if (settingsCaptured && EditorApplication.isPlayingOrWillChangePlaymode)
                    yield return new ExitPlayMode();
            }
            finally
            {
                activeProbe = null;
#if !UNITY_7000_0_OR_NEWER
                EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
                AssemblyReloadEvents.beforeAssemblyReload -= OnCodeUnloading;
#endif
                if (settingsCaptured)
                {
                    EditorSettings.enterPlayModeOptions = previousOptions;
                    EditorSettings.enterPlayModeOptionsEnabled = previousOptionsEnabled;
                    settingsCaptured = false;
                }
            }
        }

        private static void AssertEditorServicesSurvive(
            GitSubmoduleManagerSetupProbe setupProbe,
            long repositoryGeneration,
            string recoveryWarning)
        {
            Assert.That(GitSubmoduleManagerSetupProbe.Shared, Is.SameAs(setupProbe));
            Assert.That(GitOperationService.RepositoryGeneration,
                Is.EqualTo(repositoryGeneration));
            Assert.That(GitOperationService.RecoveryWarning,
                Is.EqualTo(recoveryWarning));
            Assert.That(GitSubmoduleManagerPackageManagerHost.OpenGitHubSourceError,
                Is.Empty);
            Assert.That(
                GitSubmoduleManagerPackageManagerHost.AreLifecyclePatchesApplied(),
                Is.True,
                "Package Manager host hooks must survive without being reinstalled by the test.");
            Assert.That(PackageManagerSubmoduleHarmonyPatch.IsAnyTagPatchApplied(),
                Is.True);
            Assert.That(PackageManagerSubmoduleHarmonyPatch.NativeRemoveOverrideReady,
                Is.True);
            Assert.That(PackageManagerSubmoduleHarmonyPatch.IsRemoveActionPatchApplied(),
                Is.True);
            Assert.That(
                PackageManagerSubmoduleHarmonyPatch.IsRemoveActionCollectionPatchApplied(),
                Is.True);
            Assert.That(
                PackageManagerSubmoduleHarmonyPatch
                    .IsPackageOperationDispatcherRemoveEmbeddedPatchApplied(),
                Is.True,
                "Native removal safety must survive every Play Mode transition.");
        }

#if UNITY_7000_0_OR_NEWER
        [UnityEditor.Scripting.LifecycleManagement.OnExitingEditMode]
        private static void OnExitingEditMode()
        {
            if (activeProbe != null)
                activeProbe.ExitingEditModeCount++;
        }

        [UnityEngine.OnEnteringPlayMode]
        private static void OnEnteringPlayMode()
        {
            if (activeProbe != null)
                activeProbe.EnteringPlayModeCount++;
        }

        [UnityEngine.OnExitingPlayMode]
        private static void OnExitingPlayMode()
        {
            if (activeProbe != null)
                activeProbe.ExitingPlayModeCount++;
        }

        [UnityEditor.Scripting.LifecycleManagement.OnEnteringEditMode]
        private static void OnEnteringEditMode()
        {
            if (activeProbe != null)
                activeProbe.EnteringEditModeCount++;
        }

        [Unity.Scripting.LifecycleManagement.OnCodeUnloading]
#else
        private static void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            if (activeProbe == null)
                return;

            switch (state)
            {
                case PlayModeStateChange.ExitingEditMode:
                    activeProbe.ExitingEditModeCount++;
                    break;
                case PlayModeStateChange.EnteredPlayMode:
                    activeProbe.EnteringPlayModeCount++;
                    break;
                case PlayModeStateChange.ExitingPlayMode:
                    activeProbe.ExitingPlayModeCount++;
                    break;
                case PlayModeStateChange.EnteredEditMode:
                    activeProbe.EnteringEditModeCount++;
                    break;
            }
        }
#endif
        private static void OnCodeUnloading()
        {
            if (activeProbe != null)
                activeProbe.CodeUnloadingCount++;
        }

        private sealed class LifecycleProbe
        {
            internal int ExitingEditModeCount;
            internal int EnteringPlayModeCount;
            internal int ExitingPlayModeCount;
            internal int EnteringEditModeCount;
            internal int CodeUnloadingCount;
        }
    }
}
