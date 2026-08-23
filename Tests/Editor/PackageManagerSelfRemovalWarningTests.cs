using System;
using System.Collections.Generic;
using NUnit.Framework;

namespace MartinCalander.GitSubmoduleManager.Editor.Tests
{
    public sealed class PackageManagerSelfRemovalWarningTests
    {
        private sealed class FakeVersion
        {
            public string name { get; set; }
        }

        private sealed class FakePackage
        {
            public FakeVersions versions { get; set; }
        }

        private sealed class FakeVersions
        {
            public FakeVersion primary { get; set; }
        }

        private sealed class FakePreferences
        {
            public bool skipRemoveConfirmation { get; set; }
            public bool skipMultiSelectRemoveConfirmation { get; set; }
        }

        private sealed class FakePackageDatabase
        {
            internal bool IsUsedByFeatureResult { get; set; }

            public bool IsUsedByFeature(FakeVersion version)
            {
                return version != null && IsUsedByFeatureResult;
            }
        }

        private sealed class FakeRemoveAction
        {
            private readonly FakePreferences m_PackageManagerPrefs;
            private readonly FakePackageDatabase m_PackageDatabase;

            internal FakeRemoveAction(
                bool skipSingleConfirmation,
                bool skipMultiConfirmation,
                bool isUsedByFeature = false)
            {
                m_PackageManagerPrefs = new FakePreferences
                {
                    skipRemoveConfirmation = skipSingleConfirmation,
                    skipMultiSelectRemoveConfirmation = skipMultiConfirmation
                };
                m_PackageDatabase = new FakePackageDatabase
                {
                    IsUsedByFeatureResult = isUsedByFeature
                };
            }
        }

        [TestCase("com.martincalander.gitsubmodulemanager", true)]
        [TestCase("com.martincalander.gitsubmodulemanager.extra", false)]
        [TestCase("com.martincalander.gitsubmodulemanage", false)]
        [TestCase("COM.MARTINCALANDER.GITSUBMODULEMANAGER", false)]
        [TestCase("com.example.package", false)]
        public void SelectionIdentity_MatchesOnlyExactManagerPackage(
            string packageName,
            bool expected)
        {
            Assert.That(
                PackageManagerSubmoduleHarmonyPatch.SelectionIncludesManager(
                    new FakeVersion { name = packageName }),
                Is.EqualTo(expected));
        }

        [Test]
        public void NativeSingleWarningEnabled_DoesNotDuplicateDialog()
        {
            var action = new FakeRemoveAction(false, false);
            var version = CreateManagerVersion();
            int fallbackCount = 0;
            bool actionResult = false;

            bool runOriginal =
                PackageManagerSubmoduleHarmonyPatch.ShouldRunReadOnlySelfRemoval(
                    action,
                    version,
                    false,
                    () =>
                    {
                        fallbackCount++;
                        return false;
                    },
                    ref actionResult);

            Assert.That(runOriginal, Is.True);
            Assert.That(fallbackCount, Is.Zero);
        }

        [Test]
        public void NativeSingleWarningSuppressed_CancelBlocksRemoval()
        {
            var action = new FakeRemoveAction(true, false);
            var version = CreateManagerVersion();
            int fallbackCount = 0;
            bool actionResult = true;

            bool runOriginal =
                PackageManagerSubmoduleHarmonyPatch.ShouldRunReadOnlySelfRemoval(
                    action,
                    version,
                    false,
                    () =>
                    {
                        fallbackCount++;
                        return false;
                    },
                    ref actionResult);

            Assert.That(runOriginal, Is.False);
            Assert.That(actionResult, Is.False);
            Assert.That(fallbackCount, Is.EqualTo(1));
        }

        [Test]
        public void NativeSingleWarningSuppressed_ConfirmKeepsUnityRemovalFlow()
        {
            var action = new FakeRemoveAction(true, false);
            var version = CreateManagerVersion();
            int fallbackCount = 0;
            bool actionResult = false;

            bool runOriginal =
                PackageManagerSubmoduleHarmonyPatch.ShouldRunReadOnlySelfRemoval(
                    action,
                    version,
                    false,
                    () =>
                    {
                        fallbackCount++;
                        return true;
                    },
                    ref actionResult);

            Assert.That(runOriginal, Is.True);
            Assert.That(actionResult, Is.False,
                "Unity must remain responsible for producing the action result.");
            Assert.That(fallbackCount, Is.EqualTo(1));
        }

        [Test]
        public void UnknownPreferenceContract_UsesFailSafeWarning()
        {
            var version = CreateManagerVersion();
            int fallbackCount = 0;
            bool actionResult = true;

            bool runOriginal =
                PackageManagerSubmoduleHarmonyPatch.ShouldRunReadOnlySelfRemoval(
                    new object(),
                    version,
                    false,
                    () =>
                    {
                        fallbackCount++;
                        return false;
                    },
                    ref actionResult);

            Assert.That(runOriginal, Is.False);
            Assert.That(actionResult, Is.False);
            Assert.That(fallbackCount, Is.EqualTo(1));
        }

        [Test]
        public void FeatureOwnedPackage_UsesUnityRequiredWarning()
        {
            var action = new FakeRemoveAction(true, false, true);
            var version = CreateManagerVersion();
            int fallbackCount = 0;
            bool actionResult = false;

            bool runOriginal =
                PackageManagerSubmoduleHarmonyPatch.ShouldRunReadOnlySelfRemoval(
                    action,
                    version,
                    false,
                    () =>
                    {
                        fallbackCount++;
                        return false;
                    },
                    ref actionResult);

            Assert.That(runOriginal, Is.True);
            Assert.That(fallbackCount, Is.Zero);
        }

        [Test]
        public void SuppressedMultiSelectWarning_ProtectsSelectionContainingManager()
        {
            var action = new FakeRemoveAction(false, true);
            var selection = new List<FakePackage>
            {
                CreatePackage("com.example.package"),
                CreatePackage(GitPackageConversionService.ManagerPackageName)
            };
            int fallbackCount = 0;
            bool actionResult = true;

            bool runOriginal =
                PackageManagerSubmoduleHarmonyPatch.ShouldRunReadOnlySelfRemoval(
                    action,
                    selection,
                    true,
                    () =>
                    {
                        fallbackCount++;
                        return false;
                    },
                    ref actionResult);

            Assert.That(runOriginal, Is.False);
            Assert.That(actionResult, Is.False);
            Assert.That(fallbackCount, Is.EqualTo(1));
        }

        [Test]
        public void MultiSelectWithoutManager_RemainsUnityOwned()
        {
            var action = new FakeRemoveAction(false, true);
            var selection = new List<FakePackage>
            {
                CreatePackage("com.example.one"),
                CreatePackage("com.example.two")
            };
            int fallbackCount = 0;
            bool actionResult = false;

            bool runOriginal =
                PackageManagerSubmoduleHarmonyPatch.ShouldRunReadOnlySelfRemoval(
                    action,
                    selection,
                    true,
                    () =>
                    {
                        fallbackCount++;
                        return false;
                    },
                    ref actionResult);

            Assert.That(runOriginal, Is.True);
            Assert.That(fallbackCount, Is.Zero);
        }

        [Test]
        public void NativeMultiSelectWarningEnabled_DoesNotDuplicateDialog()
        {
            var action = new FakeRemoveAction(false, false);
            var selection = new List<FakePackage>
            {
                CreatePackage("com.example.package"),
                CreatePackage(GitPackageConversionService.ManagerPackageName)
            };
            int fallbackCount = 0;
            bool actionResult = false;

            bool runOriginal =
                PackageManagerSubmoduleHarmonyPatch.ShouldRunReadOnlySelfRemoval(
                    action,
                    selection,
                    true,
                    () =>
                    {
                        fallbackCount++;
                        return false;
                    },
                    ref actionResult);

            Assert.That(runOriginal, Is.True);
            Assert.That(fallbackCount, Is.Zero);
        }

        [Test]
        public void NativeMultiSelectWarningSuppressed_ConfirmKeepsUnityRemovalFlow()
        {
            var action = new FakeRemoveAction(false, true);
            var selection = new List<FakePackage>
            {
                CreatePackage("com.example.package"),
                CreatePackage(GitPackageConversionService.ManagerPackageName)
            };
            int fallbackCount = 0;
            bool actionResult = false;

            bool runOriginal =
                PackageManagerSubmoduleHarmonyPatch.ShouldRunReadOnlySelfRemoval(
                    action,
                    selection,
                    true,
                    () =>
                    {
                        fallbackCount++;
                        return true;
                    },
                    ref actionResult);

            Assert.That(runOriginal, Is.True);
            Assert.That(actionResult, Is.False,
                "Unity must remain responsible for producing the action result.");
            Assert.That(fallbackCount, Is.EqualTo(1));
        }

        [Test]
        public void FallbackDialogFailure_PreservesManager()
        {
            var action = new FakeRemoveAction(true, true);
            bool actionResult = true;

            bool runOriginal =
                PackageManagerSubmoduleHarmonyPatch.ShouldRunReadOnlySelfRemoval(
                    action,
                    CreateManagerVersion(),
                    false,
                    () => throw new InvalidOperationException("dialog unavailable"),
                    ref actionResult);

            Assert.That(runOriginal, Is.False);
            Assert.That(actionResult, Is.False);
        }

        [Test]
        public void WarningCopy_ExplainsScopeAndPreservedPackages()
        {
            Assert.That(
                PackageManagerSubmoduleHarmonyPatch.SelfRemovalWarningTitle,
                Is.EqualTo("Remove Git Submodule Manager?"));
            Assert.That(
                PackageManagerSubmoduleHarmonyPatch.SelfRemovalWarningMessage,
                Does.Contain("GitHub Package Manager integration"));
            Assert.That(
                PackageManagerSubmoduleHarmonyPatch.SelfRemovalWarningMessage,
                Does.Contain("Existing packages and submodules will remain"));
            Assert.That(
                PackageManagerSubmoduleHarmonyPatch.SelfRemovalWarningAcceptText,
                Is.EqualTo("Remove"));
            Assert.That(
                PackageManagerSubmoduleHarmonyPatch.SelfRemovalWarningCancelText,
                Is.EqualTo("Cancel"));
        }

        private static FakeVersion CreateManagerVersion()
        {
            return new FakeVersion
            {
                name = GitPackageConversionService.ManagerPackageName
            };
        }

        private static FakePackage CreatePackage(string packageName)
        {
            return new FakePackage
            {
                versions = new FakeVersions
                {
                    primary = new FakeVersion { name = packageName }
                }
            };
        }
    }
}
