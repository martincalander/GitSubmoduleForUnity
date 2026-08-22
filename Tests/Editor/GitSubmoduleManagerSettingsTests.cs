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
        public void SetupPresentation_InstalledStatusShowsCheckmarkAndFirstVersionLine()
        {
            Assert.That(
                GitSubmoduleManagerSetupGUI.FormatInstalledMessage(
                    "git version 2.50.1\nplatform details"),
                Is.EqualTo("\u2713 Installed — git version 2.50.1"));
        }

        [Test]
        public void SetupPresentation_AuthenticatedStatusShowsCheckmarkAndGhVersion()
        {
            Assert.That(
                GitSubmoduleManagerSetupGUI.FormatAuthenticatedMessage(
                    "gh version 2.96.0 (2026-07-02)\r\nrelease notes"),
                Is.EqualTo(
                    "\u2713 Installed and authenticated — " +
                    "gh version 2.96.0 (2026-07-02)"));
        }

        [Test]
        public void SetupSnapshot_RefreshPreservesLastKnownToolDetails()
        {
            var ready = new GitSubmoduleManagerSetupSnapshot(
                false,
                true,
                "git version 2.50.1",
                string.Empty,
                true,
                "gh version 2.96.0",
                string.Empty,
                GitHubAuthenticationProbeStatus.Authenticated,
                string.Empty,
                false);

            GitSubmoduleManagerSetupSnapshot checking = ready.WithChecking(true);

            Assert.That(checking.IsChecking, Is.True);
            Assert.That(checking.GitAvailable, Is.True);
            Assert.That(checking.GitVersion, Is.EqualTo("git version 2.50.1"));
            Assert.That(checking.GitHubCliAvailable, Is.True);
            Assert.That(checking.GitHubCliVersion, Is.EqualTo("gh version 2.96.0"));
            Assert.That(checking.GitHubAuthenticated, Is.True);
        }

        [TestCase(false, false, 0d, true)]
        [TestCase(true, true, 300d, false)]
        [TestCase(true, false, 29.9d, false)]
        [TestCase(true, false, 30d, true)]
        public void SetupProbe_RefreshesOnlyWhenMissingOrCacheIsStale(
            bool alreadyStarted,
            bool isChecking,
            double elapsedSinceCompletion,
            bool expected)
        {
            Assert.That(
                GitSubmoduleManagerSetupProbe.ShouldRefresh(
                    alreadyStarted,
                    isChecking,
                    elapsedSinceCompletion),
                Is.EqualTo(expected));
        }

        [Test]
        public void AuthenticationClassification_RecognizesGhAuthenticationExitCode()
        {
            Assert.That(
                GitHubUtility.IsDefiniteAuthenticationFailure(
                    new CommandResult
                    {
                        ExitCode = 4,
                        StdOut = string.Empty,
                        StdErr = string.Empty
                    }),
                Is.True);
        }

        [Test]
        public void AuthenticationClassification_DoesNotCallNetworkFailureUnauthenticated()
        {
            Assert.That(
                GitHubUtility.IsDefiniteAuthenticationFailure(
                    new CommandResult
                    {
                        ExitCode = 1,
                        StdOut = string.Empty,
                        StdErr = "could not resolve api.github.com"
                    }),
                Is.False);
        }

        [Test]
        public void AuthenticationClassification_RecognizesInvalidCredentials()
        {
            Assert.That(
                GitHubUtility.IsDefiniteAuthenticationFailure(
                    new CommandResult
                    {
                        ExitCode = 1,
                        StdOut = string.Empty,
                        StdErr = "HTTP 401: Bad credentials"
                    }),
                Is.True);
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
