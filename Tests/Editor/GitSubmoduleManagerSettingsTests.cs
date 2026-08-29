using System;
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
        private const string TestProjectFingerprint =
            "AQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQE=";
        private const string TestUnityVersion = "6000.5.3f1";

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
        public void SetupSessionCache_RestoresHealthyStatusWithinOriginalLifetime()
        {
            var store = new MemorySessionStringStateStore();
            var cache = CreateSetupCache(store);

            Assert.That(cache.Save(HealthySetupSnapshot(), 100.125d), Is.True);
            Assert.That(
                CreateSetupCache(store).TryLoad(
                    129.999d,
                    out GitSubmoduleManagerSetupSnapshot restored,
                    out double completedAt),
                Is.True);
            Assert.That(completedAt, Is.EqualTo(100.125d).Within(0.001d));
            Assert.That(restored.IsChecking, Is.False);
            Assert.That(restored.GitAvailable, Is.True);
            Assert.That(restored.GitHubCliAvailable, Is.True);
            Assert.That(restored.GitHubAuthenticated, Is.True);

            Assert.That(
                CreateSetupCache(store).TryLoad(
                    130.125d,
                    out _,
                    out _),
                Is.False);
            Assert.That(store.Value, Is.Empty);
        }

        [Test]
        public void SetupSessionCache_DoesNotStoreInFlightOrUnhealthyStatus()
        {
            var store = new MemorySessionStringStateStore
            {
                Value = "existing"
            };
            var cache = CreateSetupCache(store);
            GitSubmoduleManagerSetupSnapshot checking =
                HealthySetupSnapshot().WithChecking(true);

            Assert.That(cache.Save(checking, 10d), Is.False);
            Assert.That(store.Value, Is.Empty);

            var failed = new GitSubmoduleManagerSetupSnapshot(
                false,
                true,
                "git version 2.50.1",
                string.Empty,
                true,
                "gh version 2.96.0",
                string.Empty,
                GitHubAuthenticationProbeStatus.Unauthenticated,
                "Not authenticated.",
                false);
            Assert.That(cache.Save(failed, 10d), Is.False);
            Assert.That(store.Value, Is.Empty);
        }

        [Test]
        public void SetupSessionCache_RejectsMalformedMismatchedAndOversizedPayloads()
        {
            var store = new MemorySessionStringStateStore();
            var cache = CreateSetupCache(store);
            Assert.That(cache.Save(HealthySetupSnapshot(), 10d), Is.True);

            store.Value = store.Value.Replace(
                "\"schemaVersion\":1",
                "\"schemaVersion\":1,\"schemaVersion\":1");
            Assert.That(cache.TryLoad(11d, out _, out _), Is.False);
            Assert.That(store.Value, Is.Empty);

            Assert.That(cache.Save(HealthySetupSnapshot(), 10d), Is.True);
            var otherProjectCache = new GitSubmoduleManagerSetupSessionCache(
                store,
                "AgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgI=",
                TestUnityVersion);
            Assert.That(otherProjectCache.TryLoad(11d, out _, out _), Is.False);
            Assert.That(store.Value, Is.Empty);

            store.Value = new string(
                'x',
                GitSubmoduleManagerSetupSessionCache.MaximumPayloadByteCount + 1);
            Assert.That(cache.TryLoad(11d, out _, out _), Is.False);
            Assert.That(store.Value, Is.Empty);
        }

        [Test]
        public void SetupSessionCache_RedactsCredentialsBeforePersistingVersions()
        {
            var store = new MemorySessionStringStateStore();
            var cache = CreateSetupCache(store);
            var snapshot = new GitSubmoduleManagerSetupSnapshot(
                false,
                true,
                "git version https://user:secret@example.com/tool",
                string.Empty,
                true,
                "gh version 2.96.0",
                string.Empty,
                GitHubAuthenticationProbeStatus.Authenticated,
                string.Empty,
                false);

            Assert.That(cache.Save(snapshot, 10d), Is.True);
            Assert.That(store.Value, Does.Not.Contain("secret"));
            Assert.That(store.Value, Does.Not.Contain("user:"));
        }

        [Test]
        public void SetupProbe_RestoredStatusRefreshesAtOriginalExpiryForActiveHost()
        {
            var store = new MemorySessionStringStateStore();
            GitSubmoduleManagerSetupSessionCache cache = CreateSetupCache(store);
            Assert.That(cache.Save(HealthySetupSnapshot(), 100d), Is.True);
            double currentTime = 110d;
            int launches = 0;
            bool cacheWasClearAtLaunch = false;
            using var probe = new GitSubmoduleManagerSetupProbe(
                cache,
                () => currentTime,
                _ =>
                {
                    cacheWasClearAtLaunch = string.IsNullOrEmpty(store.Value);
                    launches++;
                });

            Assert.That(probe.Current.GitVersion, Is.EqualTo("git version 2.50.1"));
            Assert.That(
                probe.Current.GitHubCliVersion,
                Is.EqualTo("gh version 2.96.0"));
            probe.EnsureStarted();
            Assert.That(launches, Is.EqualTo(0));

            currentTime = 129.999d;
            probe.EnsureStarted();
            Assert.That(launches, Is.EqualTo(0));

            currentTime = 130d;
            probe.EnsureStarted();
            Assert.That(launches, Is.EqualTo(1));
            Assert.That(cacheWasClearAtLaunch, Is.True);
            Assert.That(probe.Current.IsChecking, Is.True);
        }

        [Test]
        public void SetupProbe_ManualCheckClearsFreshReloadStatusBeforeLaunching()
        {
            var store = new MemorySessionStringStateStore();
            GitSubmoduleManagerSetupSessionCache cache = CreateSetupCache(store);
            Assert.That(cache.Save(HealthySetupSnapshot(), 100d), Is.True);
            int launches = 0;
            bool cacheWasClearAtLaunch = false;
            using var probe = new GitSubmoduleManagerSetupProbe(
                cache,
                () => 110d,
                _ =>
                {
                    cacheWasClearAtLaunch = string.IsNullOrEmpty(store.Value);
                    launches++;
                });

            probe.Start();

            Assert.That(launches, Is.EqualTo(1));
            Assert.That(cacheWasClearAtLaunch, Is.True);
            Assert.That(store.Value, Is.Empty);
        }

        [Test]
        public void SetupProbe_SubscriberFailureCannotInterruptProbeLaunch()
        {
            var store = new MemorySessionStringStateStore();
            int launches = 0;
            int healthyNotifications = 0;
            using var probe = new GitSubmoduleManagerSetupProbe(
                CreateSetupCache(store),
                () => 10d,
                _ => launches++);
            probe.Changed += () => throw new InvalidOperationException(
                "Broken presentation subscriber for the test.");
            probe.Changed += () => healthyNotifications++;

            Assert.DoesNotThrow(probe.EnsureStarted);

            Assert.That(launches, Is.EqualTo(1));
            Assert.That(healthyNotifications, Is.EqualTo(1));
            Assert.That(probe.Current.IsChecking, Is.True);
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

        private static GitSubmoduleManagerSetupSessionCache CreateSetupCache(
            ISessionStringStateStore store)
        {
            return new GitSubmoduleManagerSetupSessionCache(
                store,
                TestProjectFingerprint,
                TestUnityVersion);
        }

        private static GitSubmoduleManagerSetupSnapshot HealthySetupSnapshot()
        {
            return new GitSubmoduleManagerSetupSnapshot(
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
        }

        private sealed class MemorySessionStringStateStore :
            ISessionStringStateStore
        {
            internal string Value = string.Empty;

            public string Load()
            {
                return Value;
            }

            public void Save(string value)
            {
                Value = value ?? string.Empty;
            }

            public void Clear()
            {
                Value = string.Empty;
            }
        }
    }
}
