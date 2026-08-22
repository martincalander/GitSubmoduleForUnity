using System.Linq;
using System.Reflection;
using MartinCalander.GitSubmoduleManager.Editor;
using NUnit.Framework;
using UnityEditor;

namespace MartinCalander.GitSubmoduleManager.Editor.Tests
{
    [TestFixture]
    public sealed class GitSubmoduleManagerSettingsTests
    {
        [Test]
        public void NewSettings_DefaultToSafePromptsAndNativePackageManagerDefaults()
        {
            Assert.That(
                GitSubmoduleManagerUserSettings
                    .SafeDefaultSuppressRoutineSubmoduleRemovalConfirmations,
                Is.False);
            Assert.That(
                GitSubmoduleManagerUserSettings
                    .SafeDefaultInstallDependenciesWithoutPrompt,
                Is.False);
            Assert.That(
                GitSubmoduleManagerUserSettings.SafeDefaultGitHubVisibility,
                Is.EqualTo(GitSubmoduleManagerDefaultVisibility.All));
            Assert.That(
                GitSubmoduleManagerUserSettings.SafeDefaultGitHubOrganization,
                Is.Empty);
            Assert.That(
                GitSubmoduleManagerUserSettings.SafeDefaultInstallMode,
                Is.EqualTo(PackageManagerGitInstallMode.GitSubmodule));
        }

        [TestCase(-1, 0)]
        [TestCase(0, 0)]
        [TestCase(1, 1)]
        [TestCase(2, 2)]
        [TestCase(99, 0)]
        public void DefaultVisibility_NormalizesUnknownSerializedValues(
            int serializedValue,
            int expected)
        {
            Assert.That(
                (int)GitSubmoduleManagerUserSettings.NormalizeDefaultGitHubVisibility(
                    (GitSubmoduleManagerDefaultVisibility)serializedValue),
                Is.EqualTo(expected));
        }

        [TestCase(null, "")]
        [TestCase("   ", "")]
        [TestCase(" martincalander ", "martincalander")]
        [TestCase("Organization - Moonmilk-Games", "Moonmilk-Games")]
        [TestCase(" -unsafe org!  ", "unsafeorg")]
        public void DefaultOrganization_StoresOnlyNormalizedGitHubLogin(
            string value,
            string expected)
        {
            Assert.That(
                GitSubmoduleManagerUserSettings.NormalizeDefaultGitHubOrganization(value),
                Is.EqualTo(expected));
        }

        [Test]
        public void DefaultOrganization_IsBoundedToGitHubLoginLength()
        {
            string normalized =
                GitSubmoduleManagerUserSettings.NormalizeDefaultGitHubOrganization(
                    new string('a', 80));

            Assert.That(normalized, Has.Length.EqualTo(39));
        }

        [TestCase(-1, 0)]
        [TestCase(0, 0)]
        [TestCase(1, 1)]
        [TestCase(99, 0)]
        public void DefaultInstallMode_NormalizesUnknownSerializedValues(
            int serializedValue,
            int expected)
        {
            Assert.That(
                (int)GitSubmoduleManagerUserSettings.NormalizeDefaultInstallMode(
                    (PackageManagerGitInstallMode)serializedValue),
                Is.EqualTo(expected));
        }

        [Test]
        public void PreferencesProvider_RemainsUserScopedAtRequestedPath()
        {
            SettingsProvider provider =
                GitSubmoduleManagerPreferencesProvider.CreateProvider();

            Assert.That(
                provider.settingsPath,
                Is.EqualTo("Preferences/Git Submodule Manager"));
            Assert.That(provider.scope, Is.EqualTo(SettingsScope.User));
        }

        [Test]
        public void StandaloneWelcomeWindow_DoesNotRegisterAnEditorMenuItem()
        {
            bool hasMenuItem = typeof(GitSubmoduleManagerWelcomeWindow)
                .GetMethods(
                    BindingFlags.Static |
                    BindingFlags.Instance |
                    BindingFlags.Public |
                    BindingFlags.NonPublic |
                    BindingFlags.DeclaredOnly)
                .Any(method => method.GetCustomAttributes(
                        typeof(MenuItem),
                        false)
                    .Length > 0);

            Assert.That(hasMenuItem, Is.False);
        }
    }
}
