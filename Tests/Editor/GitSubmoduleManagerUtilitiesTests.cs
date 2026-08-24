using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UIElements;

namespace MartinCalander.GitSubmoduleManager.Editor.Tests
{
    [Parallelizable(ParallelScope.None)]
    public sealed class GitSubmoduleManagerUtilitiesTests
    {
        private ICommandRunner previousRunner;

        [SetUp]
        public void SetUp()
        {
            previousRunner = CliCommandRunner.CurrentRunner;
        }

        [TearDown]
        public void TearDown()
        {
            CliCommandRunner.CurrentRunner = previousRunner;
        }

        [Test]
        public void CompletionOutcome_UnconfirmedTerminationAlwaysRemainsUnsafe()
        {
            var result = new CommandResult
            {
                ExitCode = 0,
                TerminationConfirmed = false
            };

            GitOperationCompletionOutcome outcome = GitOperationService.ResolveCompletionOutcome(
                result,
                _ => GitOperationCompletionOutcome.Succeeded);

            Assert.That(outcome, Is.EqualTo(GitOperationCompletionOutcome.FailedUnsafe));
        }

        [Test]
        public void CompletionNotification_ExceptionCannotChangeResolvedSafetyOutcome()
        {
            var result = new CommandResult
            {
                ExitCode = 0,
                TerminationConfirmed = true
            };
            GitOperationCompletionOutcome outcome = GitOperationService.ResolveCompletionOutcome(
                result,
                _ => GitOperationCompletionOutcome.Succeeded);

            Exception notificationException = null;
            GitOperationService.NotifyCompletion(
                result,
                _ => throw new InvalidOperationException("simulated UI notification failure"),
                exception => notificationException = exception);

            Assert.That(outcome, Is.EqualTo(GitOperationCompletionOutcome.Succeeded));
            Assert.That(notificationException, Is.TypeOf<InvalidOperationException>());
            Assert.That(notificationException.Message, Is.EqualTo("simulated UI notification failure"));
        }

        [Test]
        public void TryReadPackageNameFromJson_ReadsStructuredName()
        {
            var success = GitUtility.TryReadPackageNameFromJson(
                "{ \"name\": \"com.martincalander.gitsubmodulemanager\", \"displayName\": \"Git Submodule Manager\" }",
                out var packageName);

            Assert.That(success, Is.True);
            Assert.That(packageName, Is.EqualTo("com.martincalander.gitsubmodulemanager"));
        }

        [Test]
        public void TryReadPackageNameFromJson_PreservesLenientLegacyBehavior()
        {
            var success = GitUtility.TryReadPackageNameFromJson(
                "{ \"name\": \" Not-A-Valid-UPM-Name \" }",
                out var packageName);

            Assert.That(success, Is.True);
            Assert.That(packageName, Is.EqualTo("Not-A-Valid-UPM-Name"));
        }

        [Test]
        public void TryReadValidPackageManifestFromJson_AcceptsValidUpmManifest()
        {
            var success = GitUtility.TryReadValidPackageManifestFromJson(
                "  { \"name\": \"com.example.valid-package\", \"version\": \"1.2.3-beta.1+build.001\", \"displayName\": \"Valid Package\" }  ",
                out var packageName,
                out var displayName,
                out var error);

            Assert.That(success, Is.True, error);
            Assert.That(packageName, Is.EqualTo("com.example.valid-package"));
            Assert.That(displayName, Is.EqualTo("Valid Package"));
            Assert.That(error, Is.Empty);
        }

        [Test]
        public void TryReadValidPackageManifestFromJson_ReadsNativeDetailsMetadata()
        {
            var success = GitUtility.TryReadValidPackageManifestFromJson(
                "{\"name\":\"com.example.details\",\"version\":\"1.0.0\"," +
                "\"displayName\":\"Details Package\"," +
                "\"description\":\"  Native details description.  \"," +
                "\"unity\":\"2021.3\",\"unityRelease\":\"0f1\"}",
                out var packageName,
                out var displayName,
                out var version,
                out var description,
                out var minimumUnityVersion,
                out var error);

            Assert.That(success, Is.True, error);
            Assert.That(packageName, Is.EqualTo("com.example.details"));
            Assert.That(displayName, Is.EqualTo("Details Package"));
            Assert.That(version, Is.EqualTo("1.0.0"));
            Assert.That(description, Is.EqualTo("Native details description."));
            Assert.That(minimumUnityVersion, Is.EqualTo("2021.3.0f1"));
            Assert.That(error, Is.Empty);
        }

        [Test]
        public void ValidateExpectedPackageIdentity_RequiresExactPinnedVersion()
        {
            Assert.That(
                GitUtility.ValidateExpectedPackageIdentity(
                    "com.example.package",
                    "1.2.3",
                    "com.example.package",
                    "1.2.3"),
                Is.Empty);
            Assert.That(
                GitUtility.ValidateExpectedPackageIdentity(
                    "com.example.package",
                    string.Empty,
                    "com.example.package",
                    "9.9.9"),
                Is.Empty,
                "Legacy callers without an expected version remain compatible.");

            Assert.That(
                GitUtility.ValidateExpectedPackageIdentity(
                    "com.example.package",
                    "1.2.3",
                    "com.example.other",
                    "1.2.3"),
                Does.Contain("Package name mismatch"));
            Assert.That(
                GitUtility.ValidateExpectedPackageIdentity(
                    "com.example.package",
                    "1.2.3",
                    "com.example.package",
                    "1.2.4"),
                Does.Contain("Package version mismatch"));
        }

        [Test]
        public void ValidateExpectedPackageIdentity_SanitizesReportedValues()
        {
            string error = GitUtility.ValidateExpectedPackageIdentity(
                "com.example.package",
                "1.2.3",
                "com.example.package",
                "1.2.4\nforged diagnostic");

            Assert.That(error, Does.Not.Contain("\n"));
            Assert.That(error, Does.Contain("Package version mismatch"));
        }

        [Test]
        public void PackageDependencyFingerprint_IsOrderIndependentAndDetectsDrift()
        {
            PackageManifestDependency[] expected =
            {
                new("com.example.zeta", "2.0.0"),
                new("com.example.alpha", "1.0.0")
            };
            PackageManifestDependency[] reordered =
            {
                new("com.example.alpha", "1.0.0"),
                new("com.example.zeta", "2.0.0")
            };
            PackageManifestDependency[] changed =
            {
                new("com.example.alpha", "1.0.1"),
                new("com.example.zeta", "2.0.0")
            };

            string fingerprint =
                GitUtility.ComputePackageDependencyFingerprint(expected);
            Assert.That(
                GitUtility.IsValidPackageDependencyFingerprint(fingerprint),
                Is.True);
            Assert.That(
                GitUtility.ComputePackageDependencyFingerprint(reordered),
                Is.EqualTo(fingerprint));
            Assert.That(
                GitUtility.ComputePackageDependencyFingerprint(changed),
                Is.Not.EqualTo(fingerprint));
        }

        [Test]
        public void ValidateExpectedPackageManifest_RejectsDependencyDrift()
        {
            var metadata = new PackageManifestMetadata(
                "com.example.package",
                "Package",
                "1.2.3",
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                new[]
                {
                    new PackageManifestDependency("com.example.child", "2.0.0")
                });
            string expectedFingerprint =
                GitUtility.ComputePackageDependencyFingerprint(new[]
                {
                    new PackageManifestDependency("com.example.child", "1.0.0")
                });

            string error = GitUtility.ValidateExpectedPackageManifest(
                "com.example.package",
                "1.2.3",
                expectedFingerprint,
                metadata);

            Assert.That(error, Does.Contain("dependencies changed"));
        }

        [Test]
        public void ReadOnlyMismatchCleanupFailure_WarnsThatDependencyMayRemain()
        {
            string message = PackageManagerReadOnlyGitInstallService
                .BuildMismatchCleanupFailureMessage(
                    "Package version mismatch. Expected 1.2.3, got 1.2.4.",
                    "Unity Package Manager could not start automatic removal: " +
                    "https://user:secret@example.com/failure\nforged");

            Assert.That(message, Does.Contain("Package version mismatch"));
            Assert.That(message, Does.Contain("may remain"));
            Assert.That(message, Does.Contain("Packages/manifest.json"));
            Assert.That(message, Does.Not.Contain("secret"));
            Assert.That(message, Does.Not.Contain("\n"));
        }

        [Test]
        public void TryReadValidPackageManifestFromJson_ReadsImmutableManifestDetailsAndDependencies()
        {
            var success = GitUtility.TryReadPackageManifestMetadataFromJson(
                "{\"name\":\"com.example.details\",\"version\":\"1.0.0\"," +
                "\"author\":{\"name\":\"Package Author\"}," +
                "\"license\":\"  See LICENSE.md file  \"," +
                "\"documentationUrl\":\"https://example.com/docs\"," +
                "\"changelogUrl\":\"https://example.com/changelog\"," +
                "\"licensesUrl\":\"https://example.com/license\"," +
                "\"dependencies\":{" +
                "\"com.example.zeta\":\"2.0.0\"," +
                "\"com.example.alpha\":\"1.0.0\"}}",
                out PackageManifestMetadata metadata,
                out var error);

            Assert.That(success, Is.True, error);
            Assert.That(metadata, Is.Not.Null);
            Assert.That(metadata.AuthorName, Is.EqualTo("Package Author"));
            Assert.That(metadata.License, Is.EqualTo("See LICENSE.md file"));
            Assert.That(metadata.DocumentationUrl, Is.EqualTo("https://example.com/docs"));
            Assert.That(metadata.ChangelogUrl, Is.EqualTo("https://example.com/changelog"));
            Assert.That(metadata.LicensesUrl, Is.EqualTo("https://example.com/license"));
            Assert.That(metadata.Dependencies, Has.Count.EqualTo(2));
            Assert.That(metadata.Dependencies[0].Name, Is.EqualTo("com.example.alpha"));
            Assert.That(metadata.Dependencies[0].Version, Is.EqualTo("1.0.0"));
            Assert.That(metadata.Dependencies[1].Name, Is.EqualTo("com.example.zeta"));
            Assert.That(metadata.Dependencies[1].Version, Is.EqualTo("2.0.0"));
        }

        [Test]
        public void TryReadValidPackageManifestFromJson_IgnoresUnsafeOptionalLicense()
        {
            const string controlCharacterJson =
                "{\"name\":\"com.example.package\",\"version\":\"1.0.0\"," +
                "\"license\":\"MIT\\nInjected\"}";
            bool controlCharacterSuccess =
                GitUtility.TryReadPackageManifestMetadataFromJson(
                    controlCharacterJson,
                    out PackageManifestMetadata controlCharacterMetadata,
                    out string controlCharacterError);

            Assert.That(controlCharacterSuccess, Is.True, controlCharacterError);
            Assert.That(controlCharacterMetadata.License, Is.Empty);

            string oversizedJson =
                "{\"name\":\"com.example.package\",\"version\":\"1.0.0\"," +
                "\"license\":\"" + new string('x', 257) + "\"}";
            bool oversizedSuccess = GitUtility.TryReadPackageManifestMetadataFromJson(
                oversizedJson,
                out PackageManifestMetadata oversizedMetadata,
                out string oversizedError);

            Assert.That(oversizedSuccess, Is.True, oversizedError);
            Assert.That(oversizedMetadata.License, Is.Empty);
        }

        [TestCase("[]", "JSON object")]
        [TestCase("{\"com.example.dependency\":1}", "versions must be strings")]
        [TestCase("{\"invalid\":\"1.0.0\"}", "invalid UPM package name")]
        [TestCase("{\"com.example.dependency\":\"\"}", "empty or oversized")]
        [TestCase("{\"com.example.dependency\":\"https://user:secret@example.com/repo.git\"}", "embedded credentials")]
        public void TryReadValidPackageManifestFromJson_RejectsUnsafeDependencyMaps(
            string dependenciesJson,
            string expectedError)
        {
            string json = "{\"name\":\"com.example.package\",\"version\":\"1.0.0\"," +
                          "\"dependencies\":" + dependenciesJson + "}";

            bool success = GitUtility.TryReadPackageManifestMetadataFromJson(
                json,
                out PackageManifestMetadata metadata,
                out string error);

            Assert.That(success, Is.False);
            Assert.That(metadata, Is.Null);
            Assert.That(error, Does.Contain(expectedError));
        }

        [Test]
        public void TryReadValidPackageManifestFromJson_RejectsDuplicateDependencyNames()
        {
            const string json =
                "{\"name\":\"com.example.package\",\"version\":\"1.0.0\"," +
                "\"dependencies\":{" +
                "\"com.example.dependency\":\"1.0.0\"," +
                "\"com.example.dependency\":\"2.0.0\"}}";

            bool success = GitUtility.TryReadPackageManifestMetadataFromJson(
                json,
                out PackageManifestMetadata metadata,
                out string error);

            Assert.That(success, Is.False);
            Assert.That(metadata, Is.Null);
            Assert.That(error, Does.Contain("could not be parsed"));
        }

        [Test]
        public void TryReadValidPackageManifestFromJson_RejectsExcessiveDependencyCount()
        {
            var json = new StringBuilder(
                "{\"name\":\"com.example.package\",\"version\":\"1.0.0\"," +
                "\"dependencies\":{");
            for (int index = 0; index < 513; index++)
            {
                if (index > 0)
                    json.Append(',');
                json.Append("\"com.example.dependency")
                    .Append(index)
                    .Append("\":\"1.0.0\"");
            }
            json.Append("}}");

            bool success = GitUtility.TryReadPackageManifestMetadataFromJson(
                json.ToString(),
                out PackageManifestMetadata metadata,
                out string error);

            Assert.That(success, Is.False);
            Assert.That(metadata, Is.Null);
            Assert.That(error, Does.Contain("512-entry"));
        }

        [Test]
        public void TryReadValidPackageManifestFromJson_RejectsExcessiveJsonDepth()
        {
            var json = new StringBuilder(
                "{\"name\":\"com.example.package\",\"version\":\"1.0.0\"," +
                "\"metadata\":");
            json.Append('[', 40).Append('0').Append(']', 40).Append('}');

            bool success = GitUtility.TryReadPackageManifestMetadataFromJson(
                json.ToString(),
                out PackageManifestMetadata metadata,
                out string error);

            Assert.That(success, Is.False);
            Assert.That(metadata, Is.Null);
            Assert.That(error, Does.Contain("could not be parsed"));
        }

        [Test]
        public void TryReadValidPackageManifestFromJson_IgnoresUnsafeOptionalLinks()
        {
            const string json =
                "{\"name\":\"com.example.package\",\"version\":\"1.0.0\"," +
                "\"documentationUrl\":\"http://example.com/docs\"," +
                "\"changelogUrl\":\"https://user:secret@example.com/changelog\"," +
                "\"licensesUrl\":\"https://example.com/license?access_token=secret\"}";

            bool success = GitUtility.TryReadPackageManifestMetadataFromJson(
                json,
                out PackageManifestMetadata metadata,
                out string error);

            Assert.That(success, Is.True, error);
            Assert.That(metadata.DocumentationUrl, Is.Empty);
            Assert.That(metadata.ChangelogUrl, Is.Empty);
            Assert.That(metadata.LicensesUrl, Is.Empty);
            Assert.That(metadata.Dependencies, Is.Empty);
        }

        [TestCase("com.example.package")]
        [TestCase("org.example.my-package")]
        [TestCase("uk.co.example.package")]
        public void IsValidUpmPackageName_AcceptsReverseDomainNames(string packageName)
        {
            Assert.That(GitUtility.IsValidUpmPackageName(packageName), Is.True);
        }

        [TestCase("my-package")]
        [TestCase("some_package")]
        [TestCase("example.package")]
        [TestCase("com.package")]
        public void IsValidUpmPackageName_RejectsNamesWithoutFullReverseDomainNotation(string packageName)
        {
            Assert.That(GitUtility.IsValidUpmPackageName(packageName), Is.False);
        }

        [TestCase(null, "empty")]
        [TestCase("", "empty")]
        [TestCase("   \r\n\t", "empty")]
        [TestCase("[]", "JSON object")]
        [TestCase("\"package\"", "JSON object")]
        [TestCase("{ \"name\": \"com.example.package\", \"version\": ", "JSON object")]
        public void TryReadValidPackageManifestFromJson_RejectsInvalidInput(string json, string expectedError)
        {
            var success = GitUtility.TryReadValidPackageManifestFromJson(json, out var packageName, out var error);

            Assert.That(success, Is.False);
            Assert.That(packageName, Is.Empty);
            Assert.That(error, Does.Contain(expectedError));
        }

        [Test]
        public void TryReadValidPackageManifestFromJson_RejectsOversizedInput()
        {
            string json = "{\"name\":\"com.example.package\",\"version\":\"1.0.0\",\"padding\":\"" +
                          new string('a', 1024 * 1024) +
                          "\"}";

            var success = GitUtility.TryReadValidPackageManifestFromJson(json, out var packageName, out var error);

            Assert.That(success, Is.False);
            Assert.That(packageName, Is.Empty);
            Assert.That(error, Does.Contain("1 MiB"));
        }

        [TestCase("{ \"version\": \"1.0.0\" }", "UPM package name")]
        [TestCase("{ \"name\": \"Com.Example.Package\", \"version\": \"1.0.0\" }", "UPM package name")]
        [TestCase("{ \"name\": \"my-package\", \"version\": \"1.0.0\" }", "UPM package name")]
        [TestCase("{ \"name\": \"example.package\", \"version\": \"1.0.0\" }", "UPM package name")]
        [TestCase("{ \"name\": \"com.example.package\" }", "SemVer 2.0")]
        [TestCase("{ \"name\": \"com.example.package\", \"version\": \"01.0.0\" }", "SemVer 2.0")]
        public void TryReadValidPackageManifestFromJson_RejectsInvalidRequiredFields(string json, string expectedError)
        {
            var success = GitUtility.TryReadValidPackageManifestFromJson(json, out var packageName, out var error);

            Assert.That(success, Is.False);
            Assert.That(packageName, Is.Empty);
            Assert.That(error, Does.Contain(expectedError));
        }

        [TestCase("0.0.0")]
        [TestCase("1.2.3")]
        [TestCase("10.20.30-alpha")]
        [TestCase("1.0.0-alpha.1")]
        [TestCase("1.0.0-0.3.7")]
        [TestCase("1.0.0-x.7.z.92")]
        [TestCase("1.0.0-x-y-z.--")]
        [TestCase("1.0.0+20130313144700")]
        [TestCase("1.0.0-beta+exp.sha.5114f85")]
        [TestCase("1.0.0+001")]
        [TestCase("999999999999999999999999.0.1")]
        public void IsValidSemanticVersion_AcceptsSemVer2Versions(string version)
        {
            Assert.That(GitUtility.IsValidSemanticVersion(version), Is.True);
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase(" 1.0.0")]
        [TestCase("1.0.0 ")]
        [TestCase("v1.0.0")]
        [TestCase("1.0")]
        [TestCase("1.0.0.0")]
        [TestCase("01.0.0")]
        [TestCase("1.01.0")]
        [TestCase("1.0.01")]
        [TestCase("1.0.0-")]
        [TestCase("1.0.0-alpha..1")]
        [TestCase("1.0.0-01")]
        [TestCase("1.0.0-alpha_1")]
        [TestCase("1.0.0+")]
        [TestCase("1.0.0+build..1")]
        [TestCase("1.0.0+build_1")]
        [TestCase("1.0.0+build+other")]
        [TestCase("1.0.0-alpha+build+other")]
        [TestCase("1.0.0-α")]
        public void IsValidSemanticVersion_RejectsNonSemVer2Versions(string version)
        {
            Assert.That(GitUtility.IsValidSemanticVersion(version), Is.False);
        }

        [Test]
        public void DerivePackageNameSuggestion_StripsNonAlphanumericCharacters()
        {
            var suggestion = GitHubUtility.DerivePackageNameSuggestion("Essentials-ForUnity", "My.Helper-Package");

            Assert.That(suggestion, Is.EqualTo("com.essentialsforunity.myhelperpackage"));
        }

        [Test]
        public void TryParseGitHubRepo_ParsesCommonGitHubUrls()
        {
            Assert.That(
                GitHubUtility.TryParseGitHubRepo(
                    "https://github.com/example/SomeRepository.git",
                    out var httpsOwner,
                    out var httpsRepo),
                Is.True);
            Assert.That(httpsOwner, Is.EqualTo("example"));
            Assert.That(httpsRepo, Is.EqualTo("SomeRepository"));

            Assert.That(
                GitHubUtility.TryParseGitHubRepo("git@github.com:EssentialsForUnity/com.essentials.extensions.git", out var sshOwner, out var sshRepo),
                Is.True);
            Assert.That(sshOwner, Is.EqualTo("EssentialsForUnity"));
            Assert.That(sshRepo, Is.EqualTo("com.essentials.extensions"));
        }

        [Test]
        public void NormalizePath_ReplacesBackslashesAndTrimsWhitespace()
        {
            var normalized = GitUtility.NormalizePath(@"  Packages\com.martincalander.gitsubmodulemanager  ");

            Assert.That(normalized, Is.EqualTo("Packages/com.martincalander.gitsubmodulemanager"));
        }

        [TestCase("Packages/com.user.repo", true)]
        [TestCase("Packages/com.user.repo/nested", false)]
        [TestCase("Assets/com.user.repo", false)]
        [TestCase("Packages/../ProjectSettings", false)]
        public void IsPackagePath_OnlyAllowsDirectUnityPackages(string path, bool expected)
        {
            Assert.That(GitUtility.IsPackagePath(path), Is.EqualTo(expected));
        }

        [TestCase("main", true)]
        [TestCase("feature/reliable-discovery", true)]
        [TestCase("--upload-pack=bad", false)]
        [TestCase("bad..branch", false)]
        [TestCase("bad branch", false)]
        [TestCase("feature/.hidden", false)]
        [TestCase("feature/release.lock", false)]
        public void IsValidBranchName_RejectsUnsafeRefs(string branch, bool expected)
        {
            Assert.That(GitUtility.IsValidBranchName(branch), Is.EqualTo(expected));
        }

        [TestCase("https://github.com/owner/repo.git", true)]
        [TestCase("git@github.com:owner/repo.git", true)]
        [TestCase("../Local Repo", true)]
        [TestCase("--upload-pack=malicious", false)]
        [TestCase("https://github.com/owner/repo.git\n--config=bad", false)]
        [TestCase("https://token@github.com/owner/repo.git", false)]
        public void IsValidRepositoryUrl_RejectsOptionAndControlCharacterInjection(string url, bool expected)
        {
            Assert.That(GitUtility.IsValidRepositoryUrl(url), Is.EqualTo(expected));
        }

        [Test]
        public void Quote_PreservesWindowsBackslashes()
        {
            Assert.That(GitUtility.Quote(@"C:\Repos\My Package"), Is.EqualTo("\"C:\\Repos\\My Package\""));
        }

        [Test]
        public void TryBuildAddSubmoduleArguments_LocalRepository_AllowsFileTransportForThatCommand()
        {
            bool success = GitUtility.TryBuildAddSubmoduleArguments(
                "/tmp/My Local Package",
                "Packages/com.example.localpackage",
                string.Empty,
                out string arguments,
                out string error);

            Assert.That(success, Is.True, error);
            Assert.That(arguments, Does.StartWith("-c protocol.file.allow=always submodule add"));
            Assert.That(arguments, Does.Contain("\"/tmp/My Local Package\""));
            Assert.That(arguments, Does.Not.Contain(" -b "));
        }

        [Test]
        public void TryBuildAddSubmoduleArguments_RemoteRepository_DoesNotEnableFileTransport()
        {
            bool success = GitUtility.TryBuildAddSubmoduleArguments(
                "https://github.com/owner/repo.git",
                "Packages/com.example.remote",
                " main ",
                out string arguments,
                out string error);

            Assert.That(success, Is.True, error);
            Assert.That(arguments, Does.StartWith("submodule add -b \"main\""));
            Assert.That(arguments, Does.Not.Contain("protocol.file.allow"));
        }

        [Test]
        public void RedactCredentials_RemovesHttpUserInfoFromErrors()
        {
            string redacted = GitUtility.RedactCredentials(
                "fatal: unable to access 'https://user:secret@example.com/repo.git/'");

            Assert.That(redacted, Does.Not.Contain("user:secret"));
            Assert.That(redacted, Does.Contain("https://***@example.com"));
        }

        [TestCase(false, false, true)]
        [TestCase(false, true, false)]
        [TestCase(true, false, false)]
        [TestCase(true, true, false)]
        public void WelcomeSettings_ShowOnlyOnce(
            bool persisted,
            bool shownThisSession,
            bool expected)
        {
            Assert.That(
                GitSubmoduleManagerUserSettings.ShouldShowWelcome(persisted, shownThisSession),
                Is.EqualTo(expected));
        }

        [Test]
        public void UserSettings_UseProjectLocalUserSettingsPath()
        {
            Assert.That(
                GitSubmoduleManagerUserSettings.SettingsFilePath,
                Is.EqualTo("UserSettings/GitSubmoduleManagerSettings.asset"));
        }

        [Test]
        public void UserSettings_MigrationCopiesLegacyFileAndPreservesOriginal()
        {
            string projectRoot = Path.Combine(
                Path.GetTempPath(),
                "GitSubmoduleManagerSettings-" + Guid.NewGuid().ToString("N"));
            string legacyPath = Path.Combine(
                projectRoot,
                GitSubmoduleManagerUserSettings.LegacySettingsFilePath);
            string currentPath = Path.Combine(
                projectRoot,
                GitSubmoduleManagerUserSettings.SettingsFilePath);

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(legacyPath));
                File.WriteAllText(legacyPath, "legacy preferences");

                Assert.That(
                    GitSubmoduleManagerUserSettings.TryMigrateLegacySettingsFile(
                        projectRoot,
                        out string error),
                    Is.True,
                    error);
                Assert.That(File.ReadAllText(currentPath), Is.EqualTo("legacy preferences"));
                Assert.That(File.ReadAllText(legacyPath), Is.EqualTo("legacy preferences"));
                Assert.That(
                    Directory.GetFiles(
                        Path.GetDirectoryName(currentPath),
                        "GitSubmoduleManagerSettings.asset.*.tmp"),
                    Is.Empty);
            }
            finally
            {
                if (Directory.Exists(projectRoot))
                    Directory.Delete(projectRoot, true);
            }
        }

        [Test]
        public void UserSettings_MigrationDoesNotOverwriteRenamedFile()
        {
            string projectRoot = Path.Combine(
                Path.GetTempPath(),
                "GitSubmoduleManagerSettings-" + Guid.NewGuid().ToString("N"));
            string legacyPath = Path.Combine(
                projectRoot,
                GitSubmoduleManagerUserSettings.LegacySettingsFilePath);
            string currentPath = Path.Combine(
                projectRoot,
                GitSubmoduleManagerUserSettings.SettingsFilePath);

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(legacyPath));
                File.WriteAllText(legacyPath, "legacy preferences");
                File.WriteAllText(currentPath, "renamed preferences");

                Assert.That(
                    GitSubmoduleManagerUserSettings.TryMigrateLegacySettingsFile(
                        projectRoot,
                        out string error),
                    Is.True,
                    error);
                Assert.That(File.ReadAllText(currentPath), Is.EqualTo("renamed preferences"));
                Assert.That(File.ReadAllText(legacyPath), Is.EqualTo("legacy preferences"));
            }
            finally
            {
                if (Directory.Exists(projectRoot))
                    Directory.Delete(projectRoot, true);
            }
        }

        [Test]
        public void RecoveryPaths_ContinueLegacyStateAndDetectJournalConflicts()
        {
            string projectRoot = Path.Combine(
                Path.GetTempPath(),
                "GitSubmoduleManagerRecovery-" + Guid.NewGuid().ToString("N"));
            string currentJournal = Path.Combine(
                projectRoot,
                "Library",
                "GitSubmoduleManager",
                "active-operation.json");
            string legacyJournal = Path.Combine(
                projectRoot,
                "Library",
                "GitPackageManager",
                "active-operation.json");

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(legacyJournal));
                Directory.CreateDirectory(
                    Path.Combine(projectRoot, "Library", "GitPackageManager", "Recovery"));
                File.WriteAllText(legacyJournal, "{}");

                Assert.That(
                    GitOperationService.ResolveJournalPath(currentJournal, legacyJournal),
                    Is.EqualTo(legacyJournal));
                Assert.That(
                    GitOperationService.HaveConflictingJournalFiles(currentJournal, legacyJournal),
                    Is.False);
                Assert.That(
                    GitUtility.ResolveRecoveryRoot(projectRoot),
                    Is.EqualTo(Path.Combine(projectRoot, "Library", "GitPackageManager", "Recovery")));

                Directory.CreateDirectory(Path.GetDirectoryName(currentJournal));
                Directory.CreateDirectory(
                    Path.Combine(projectRoot, "Library", "GitSubmoduleManager", "Recovery"));
                File.WriteAllText(currentJournal, "{}");

                Assert.That(
                    GitOperationService.ResolveJournalPath(currentJournal, legacyJournal),
                    Is.EqualTo(currentJournal));
                Assert.That(
                    GitOperationService.HaveConflictingJournalFiles(currentJournal, legacyJournal),
                    Is.True);
                Assert.That(
                    GitUtility.ResolveRecoveryRoot(projectRoot),
                    Is.EqualTo(Path.Combine(projectRoot, "Library", "GitSubmoduleManager", "Recovery")));
            }
            finally
            {
                if (Directory.Exists(projectRoot))
                    Directory.Delete(projectRoot, true);
            }
        }

        [Test]
        public void RecoveryState_ReestablishesAutoRefreshOwnershipAfterJournalConflictResolves()
        {
            GitOperationService.ResolveRecoveryAutoRefreshState(
                false,
                true,
                true,
                out bool ownsSuppression,
                out bool requiresRestart);

            Assert.That(ownsSuppression, Is.True);
            Assert.That(requiresRestart, Is.False);
        }

        [Test]
        public void RecoveryJournalIdentity_RejectsMissingOrMalformedValues()
        {
            Assert.That(GitOperationService.IsValidJournalOperationId(null), Is.False);
            Assert.That(GitOperationService.IsValidJournalOperationId(string.Empty), Is.False);
            Assert.That(GitOperationService.IsValidJournalOperationId("operation-id"), Is.False);
            Assert.That(
                GitOperationService.IsValidJournalOperationId(Guid.NewGuid().ToString("N")),
                Is.True);
        }

        [Test]
        public void GitHubAuthenticationPlan_UsesFixedBrowserFlowWithoutTokens()
        {
            IReadOnlyList<string> arguments = GitHubUtility.BuildAuthenticationArguments();

            Assert.That(arguments, Is.EqualTo(new[]
            {
                "auth",
                "login",
                "--hostname",
                "github.com",
                "--git-protocol",
                "https",
                "--web",
                "--clipboard"
            }));
            Assert.That(arguments, Does.Not.Contain("--with-token"));
            Assert.That(GitHubUtility.AuthenticationDisplayCommand, Does.Not.Contain("token"));
            Assert.That(GitHubUtility.AuthenticationTerminalDisplayCommand, Does.Not.Contain("--clipboard"));
            Assert.That(GitHubUtility.AuthenticationTerminalDisplayCommand, Does.Not.Contain("--git-protocol"));
            Assert.That(GitHubUtility.BuildAuthenticationStatusArguments(), Is.EqualTo(new[]
            {
                "api",
                "user",
                "--hostname",
                "github.com",
                "--jq",
                ".login"
            }));
            Assert.That(
                GitHubUtility.AuthenticationDeviceUrl,
                Is.EqualTo("https://github.com/login/device"));
        }

        [TestCase("gh version 2.78.0 (2025-08-01)", false)]
        [TestCase("gh version 2.79.0 (2025-09-09)", true)]
        [TestCase("gh version 2.96.0 (2026-07-02)", true)]
        [TestCase("unexpected output", false)]
        [TestCase(null, false)]
        public void GitHubAuthenticationCompatibility_RequiresClipboardCapableVersion(
            string versionOutput,
            bool expected)
        {
            Assert.That(
                GitHubUtility.SupportsClipboardAuthentication(versionOutput),
                Is.EqualTo(expected));
        }

        [Test]
        public void ParseRepoJson_PrefersCloneUrlOverApiUrl()
        {
            const string json = "[{\"name\":\"repo\",\"owner\":{\"login\":\"owner\"}," +
                                "\"url\":\"https://api.github.com/repos/owner/repo\"," +
                                "\"html_url\":\"https://github.com/owner/repo\"," +
                                "\"clone_url\":\"https://github.com/owner/repo.git\"}]";

            var repos = GitHubUtility.ParseRepoJson(json);

            Assert.That(repos, Has.Count.EqualTo(1));
            Assert.That(repos[0].Url, Is.EqualTo("https://github.com/owner/repo.git"));
        }

        [Test]
        public void TryParseGitHubRepo_RejectsLookalikeHostsAndExtraPathSegments()
        {
            Assert.That(
                GitHubUtility.TryParseGitHubRepo("https://notgithub.com/owner/repo.git", out _, out _),
                Is.False);
            Assert.That(
                GitHubUtility.TryParseGitHubRepo("https://github.com/owner/repo/tree/main", out _, out _),
                Is.False);
        }

        [Test]
        public void RepositoryCoordinator_FailedBranchLoadCanBeRetried()
        {
            var runner = new FakeCommandRunner(spec => Fail(spec, "network unavailable"));
            CliCommandRunner.CurrentRunner = runner;
            using var coordinator = new RepositoryCoordinator();
            const string url = "https://github.com/owner/repo.git";

            coordinator.RequestBranches(url);
            Assert.That(coordinator.HasPendingBranchWork, Is.True);
            WaitForBranchFetch(coordinator);

            Assert.That(coordinator.HasPendingBranchWork, Is.False);
            Assert.That(coordinator.TryGetBranchError(url, out string error), Is.True);
            Assert.That(error, Does.Contain("network unavailable"));

            coordinator.ClearBranchCache(url);
            coordinator.RequestBranches(url);
            WaitForBranchFetch(coordinator);

            Assert.That(runner.Calls.Count, Is.EqualTo(2));
        }

        // ── Discovery Coordinator Tests ──

        [Test]
        public void DiscoveryCoordinator_InitialLoadFetchesOnePage()
        {
            var runner = new FakeCommandRunner(spec =>
            {
                if (spec.FileName == "gh" && spec.Arguments.Contains("api user --jq"))
                {
                    return Success("EssentialsForUnity");
                }

                if (spec.FileName == "gh" && spec.Arguments.Contains("user/repos"))
                {
                    return Success(BuildRepoJson(1, 5));
                }

                return Fail(spec, "Unexpected");
            });
            CliCommandRunner.CurrentRunner = runner;

            using var coordinator = new DiscoveryCoordinator();
            coordinator.EnsureUsername();
            coordinator.LoadInitialPage();

            WaitForDiscovery(coordinator, 2);

            Assert.That(coordinator.DisplayedRepos, Has.Count.EqualTo(5));
            Assert.That(coordinator.HasNextPage, Is.False);
        }

        [Test]
        public void DiscoveryCoordinator_PaginationWorks()
        {
            var runner = new FakeCommandRunner(spec =>
            {
                if (spec.FileName == "gh" && spec.Arguments.Contains("user/repos") && spec.Arguments.Contains("page=1"))
                {
                    return Success(BuildRepoJson(1, 50));
                }

                if (spec.FileName == "gh" && spec.Arguments.Contains("user/repos") && spec.Arguments.Contains("page=2"))
                {
                    return Success(BuildRepoJson(31, 10));
                }

                return Fail(spec, "Unexpected");
            });
            CliCommandRunner.CurrentRunner = runner;

            using var coordinator = new DiscoveryCoordinator();
            coordinator.LoadInitialPage();

            WaitForDiscovery(coordinator, 2);

            Assert.That(coordinator.HasNextPage, Is.True);
            Assert.That(coordinator.CurrentPage, Is.EqualTo(1));

            coordinator.NextPage();

            // The async handle completes near-instantly with FakeCommandRunner.
            // We must tick until the page handle is processed.
            Thread.Sleep(50);
            var timeoutAt = DateTime.UtcNow.AddSeconds(2);
            while (DateTime.UtcNow < timeoutAt)
            {
                coordinator.Tick();
                if (coordinator.DisplayedRepos.Count != 50)
                    break;
                Thread.Sleep(10);
            }

            Assert.That(coordinator.CurrentPage, Is.EqualTo(2));
            Assert.That(coordinator.DisplayedRepos, Has.Count.EqualTo(10));
        }

        [Test]
        public void DiscoveryCoordinator_DisposeClearsAuthenticatedAccountState()
        {
            var runner = new FakeCommandRunner(spec =>
            {
                if (spec.Arguments.Contains("api user --jq"))
                    return Success("signed-in-user");
                if (spec.Arguments.Contains("user/orgs"))
                    return Success("example-org");
                return Fail(spec, "Unexpected");
            });
            CliCommandRunner.CurrentRunner = runner;

            var coordinator = new DiscoveryCoordinator();
            coordinator.EnsureUsername();
            var timeoutAt = DateTime.UtcNow.AddSeconds(2);
            while (DateTime.UtcNow < timeoutAt &&
                   (string.IsNullOrEmpty(coordinator.Username) || !coordinator.OrgsLoaded))
            {
                coordinator.Tick();
                Thread.Sleep(10);
            }

            Assert.That(coordinator.Username, Is.EqualTo("signed-in-user"));
            Assert.That(coordinator.SelectedOwner, Is.EqualTo("signed-in-user"));
            Assert.That(coordinator.Organizations, Does.Contain("example-org"));

            coordinator.Dispose();

            Assert.That(coordinator.Username, Is.Empty);
            Assert.That(coordinator.SelectedOwner, Is.Empty);
            Assert.That(coordinator.Organizations, Is.Empty);
            Assert.That(coordinator.OrgsLoaded, Is.False);
            Assert.That(coordinator.HasNextPage, Is.False);
        }

        [Test]
        public void PackageManagerHostLifecycle_PrefersSingleBestSupportedHook()
        {
            Assert.That(
                GitSubmoduleManagerPackageManagerHost
                    .GetSupportedLifecycleMethods(typeof(HostWithAllLifecycleMethods))
                    .Select(method => method.Name),
                Is.EqualTo(new[] { "BuildGUI" }));
            Assert.That(
                GitSubmoduleManagerPackageManagerHost
                    .GetSupportedLifecycleMethods(typeof(HostWithCreateGui))
                    .Select(method => method.Name),
                Is.EqualTo(new[] { "CreateGUI" }));
            Assert.That(
                GitSubmoduleManagerPackageManagerHost
                    .GetSupportedLifecycleMethods(typeof(HostWithOnEnable))
                    .Select(method => method.Name),
                Is.EqualTo(new[] { "OnEnable" }));
        }

        [Test]
        public void PackageManagerHostLifecycle_RearmsAfterScriptsReload()
        {
            MethodInfo callback = typeof(GitSubmoduleManagerPackageManagerHost)
                .GetMethod(
                    "AfterScriptsReloaded",
                    BindingFlags.Static | BindingFlags.NonPublic);

            Assert.That(callback, Is.Not.Null);
            Assert.That(callback.ReturnType, Is.EqualTo(typeof(void)));
            Assert.That(callback.GetParameters(), Is.Empty);
            Assert.That(
                callback.IsDefined(
                    typeof(UnityEditor.Callbacks.DidReloadScripts),
                    inherit: false),
                Is.True,
                "An already-open Package Manager must be remounted after both " +
                "normal domain reload and Unity's in-place script reload path.");
        }

        [Test]
        public void PackageManagerHostLifecycle_LivePanelDetachRequestsRepairInsteadOfRelease()
        {
            Assert.That(
                GitSubmoduleManagerPackageManagerHost.GetDetachedWindowAction(
                    windowAlive: true),
                Is.EqualTo(
                    GitSubmoduleManagerPackageManagerHost.DetachedWindowAction.Repair));
            Assert.That(
                GitSubmoduleManagerPackageManagerHost.GetDetachedWindowAction(
                    windowAlive: false),
                Is.EqualTo(
                    GitSubmoduleManagerPackageManagerHost.DetachedWindowAction.Release));

            Assert.That(
                GitSubmoduleManagerPackageManagerHost.ShouldPollHostSession(
                    isDisposed: false,
                    windowAlive: true,
                    panelAttached: false,
                    isAttached: false,
                    withinRepairWindow: false),
                Is.True,
                "A long-inactive detached tab still needs a low-frequency close observer.");
            Assert.That(
                GitSubmoduleManagerPackageManagerHost.ShouldPollHostSession(
                    isDisposed: false,
                    windowAlive: false,
                    panelAttached: false,
                    isAttached: false,
                    withinRepairWindow: false),
                Is.True,
                "A destroyed Unity object needs one final poll for dictionary cleanup.");
            Assert.That(
                GitSubmoduleManagerPackageManagerHost.ShouldPollHostSession(
                    isDisposed: false,
                    windowAlive: true,
                    panelAttached: true,
                    isAttached: true,
                    withinRepairWindow: false),
                Is.False);
        }

        [Test]
        public void PackageManagerSourcesLookup_RejectsLegacyFoldoutsWithoutSidebar()
        {
            var root = new VisualElement();
            root.Add(new Foldout { text = "Details" });
            root.Add(new Foldout { text = "Sources" });

            Foldout result = GitSubmoduleManagerPackageManagerHost.FindSourcesFoldout(root);

            Assert.That(result, Is.Null);
        }

        [Test]
        public void PackageManagerHostDescendantCheck_WalksVisualTreeParents()
        {
            var root = new VisualElement();
            var parent = new VisualElement();
            var child = new VisualElement();
            root.Add(parent);
            parent.Add(child);

            Assert.That(
                GitSubmoduleManagerPackageManagerHost.IsDescendantOrSelf(root, child),
                Is.True);
            Assert.That(
                GitSubmoduleManagerPackageManagerHost.IsDescendantOrSelf(parent, parent),
                Is.True);
            Assert.That(
                GitSubmoduleManagerPackageManagerHost.IsDescendantOrSelf(child, root),
                Is.False);
        }

        [Test]
        public void PackageManagerNativePage_UsesExtensionContractWhenAvailable()
        {
            bool supported = PackageManagerSubmoduleNativePage.IsSupportedContract();
            bool created = PackageManagerSubmoduleNativePage
                .TryCreateExtensionPageArgs(out object args);

            Assert.That(created, Is.EqualTo(supported));
            if (!supported)
            {
                Assert.That(args, Is.Null,
                    "Older Package Manager layouts must fail open to the compatibility host.");
                return;
            }

            Assert.That(args, Is.Not.Null);
            Assert.That(ReadField(args, "name"),
                Is.EqualTo(PackageManagerSubmoduleNativePage.ExtensionPageName));
            Assert.That(ReadField(args, "displayName"), Is.EqualTo("GitHub"));
            Assert.That(ReadField(args, "filter"), Is.InstanceOf<Delegate>());
            Assert.That(ReadField(args, "getGroupName"), Is.InstanceOf<Delegate>());
            Array supportedStatuses = ReadField(
                args,
                "supportedStatusFilters") as Array;
            Assert.That(supportedStatuses, Has.Length.EqualTo(1));
            Assert.That(
                supportedStatuses?.GetValue(0)?.ToString(),
                Is.EqualTo(PackageManagerSubmoduleNativePage
                    .DownloadedFilterStatusName));
            Assert.That(ReadField(args, "supportedSortOptions") as Array,
                Has.Length.EqualTo(2));
            Assert.That(
                PackageManagerSubmoduleNativePage.GetSupportedVisibilityLabels(),
                Is.EqualTo(new[] { "Public", "Private" }));
            MethodInfo updateSupportedLabels =
                PackageManagerSubmoduleNativePage
                    .GetUpdateSupportedLabelsMethod();
            MethodInfo updateSupportedCategories =
                PackageManagerSubmoduleNativePage
                    .GetUpdateSupportedCategoriesMethod();
            if (updateSupportedLabels == null ||
                updateSupportedCategories == null)
            {
                Assert.That(updateSupportedLabels, Is.Null);
                Assert.That(updateSupportedCategories, Is.Null);
                Assert.That(
                    PackageManagerGitHubNativePresentationPatch
                        .HasRequiredLegacyDiscoveryLifecycleContract(),
                    Is.True);
                Assert.That(
                    PackageManagerGitHubNativePresentationPatch
                        .GetLegacyPageVisibilityFilterTarget(),
                    Is.Not.Null);
                Assert.That(
                    PackageManagerGitHubNativePresentationPatch
                        .GetLegacyFiltersDisplayTarget(),
                    Is.Not.Null);
                Assert.That(
                    PackageManagerGitHubNativePresentationPatch
                        .GetLegacyFiltersSizeTarget(),
                    Is.Not.Null);
                return;
            }

            Assert.That(updateSupportedLabels.ReturnType, Is.EqualTo(typeof(bool)));
            Assert.That(
                updateSupportedLabels.GetParameters()
                    .Select(parameter => parameter.ParameterType),
                Is.EqualTo(new[]
                {
                    typeof(IReadOnlyList<string>),
                    typeof(bool)
                }));
            Assert.That(
                updateSupportedCategories.ReturnType,
                Is.EqualTo(typeof(bool)));
            Assert.That(
                updateSupportedCategories.GetParameters()
                    .Select(parameter => parameter.ParameterType),
                Is.EqualTo(new[]
                {
                    typeof(IReadOnlyList<string>),
                    typeof(bool)
                }));
        }

        [Test]
        public void PackageManagerNativePage_LegacyDefaultsRemainBestEffort()
        {
            var incompletePage = new object();

            Assert.That(
                PackageManagerSubmoduleNativePage.TryApplyDefaultFilters(
                    incompletePage),
                Is.False);
            Assert.That(
                PackageManagerSubmoduleNativePage
                    .ApplyLegacyDefaultFiltersBestEffort(incompletePage),
                Is.True,
                "A verified legacy filter contract must remain usable while " +
                "default organization discovery is incomplete.");
        }

        [Test]
        public void PackageManagerNativePage_OrganizationFiltersNormalizeSortAndDeduplicate()
        {
            IReadOnlyList<string> filters = PackageManagerSubmoduleNativePage
                .BuildOrganizationFilterLabels(new[]
                {
                    "  zeta  ",
                    "Alpha",
                    "alpha",
                    null,
                    string.Empty,
                    "   ",
                    "Beta"
                });

            Assert.That(filters, Is.EqualTo(new[]
            {
                "Organization - Alpha",
                "Organization - Beta",
                "Organization - zeta"
            }));
            Assert.That(
                PackageManagerSubmoduleNativePage
                    .BuildOrganizationFilterLabels(null),
                Is.Empty);
        }

        [Test]
        public void PackageManagerNativePage_OrganizationFilterParsingIsUnambiguous()
        {
            string filter = PackageManagerSubmoduleNativePage
                .CreateOrganizationFilterLabel("  Public  ");

            Assert.That(filter, Is.EqualTo("Organization - Public"));
            Assert.That(
                PackageManagerSubmoduleNativePage.IsOrganizationFilterLabel(filter),
                Is.True);
            Assert.That(
                PackageManagerSubmoduleNativePage.TryGetOrganizationFilterOwner(
                    filter,
                    out string owner),
                Is.True);
            Assert.That(owner, Is.EqualTo("Public"));
            Assert.That(
                PackageManagerSubmoduleNativePage.IsOrganizationFilterLabel(
                    PackageManagerSubmodulePresentation.PublicRepositoryTagLabel),
                Is.False,
                "The Public visibility value must not be parsed as an organization.");
            Assert.That(
                PackageManagerSubmoduleNativePage.IsOrganizationFilterLabel(
                    "Organization -   "),
                Is.False);
            Assert.That(
                PackageManagerSubmoduleNativePage.CreateOrganizationFilterLabel(
                    "   "),
                Is.Empty);
        }

        [Test]
        public void PackageManagerNativePage_ReadsPrimaryVersionAndNativeGroupName()
        {
            var version = new NativePageVersionStub
            {
                author = new NativePageAuthorStub { name = "  Example Author  " }
            };
            var package = new NativePagePackageStub
            {
                versions = new NativePageVersionsStub { primary = version }
            };

            Assert.That(
                PackageManagerSubmoduleNativePage.GetPrimaryVersion(package),
                Is.SameAs(version));
            Assert.That(
                PackageManagerSubmoduleNativePage.GetGroupName(package),
                Is.EqualTo("Organization - Example Author"));
            version.author = null;
            Assert.That(
                PackageManagerSubmoduleNativePage.GetGroupName(package),
                Is.EqualTo("Organization"));
            Assert.That(
                PackageManagerSubmoduleNativePage.GetPrimaryVersion(new object()),
                Is.Null);
        }

        [Test]
        public void PackageManagerNativePage_InstalledGitHubOwnerComesFromSubmoduleRemote()
        {
            var info = new PackageManagerSubmoduleInfo(
                "com.example.package",
                "Packages/com.example.package",
                "/project/Packages/com.example.package",
                "git@github.com:martincalander/example-package.git",
                true);

            Assert.That(
                PackageManagerSubmoduleNativePage.GetGitHubRepositoryOwner(info),
                Is.EqualTo("martincalander"));
            Assert.That(
                PackageManagerSubmoduleNativePage.GetGitHubRepositoryOwner(
                    new PackageManagerSubmoduleInfo(
                        "com.example.package",
                        "Packages/com.example.package",
                        "/project/Packages/com.example.package",
                        "ssh://git@git.example.com/team/example-package.git",
                        false)),
                Is.Empty);
        }

        [Test]
        public void PackageManagerNativePage_RuntimeHooksAreRegisteredWhenSupported()
        {
            Assert.That(GitSubmoduleManagerPackageManagerHost.TryPatch(), Is.True);
            if (!PackageManagerSubmoduleNativePage.IsSupportedContract())
                return;

            Assert.That(
                GitSubmoduleManagerPackageManagerHost
                    .IsSidebarExtensionRefreshPatchApplied(),
                Is.True,
                "Unity can rebuild extension rows, so the native GitHub row needs a relocation postfix.");
        }

        [Test]
        public void PackageManagerNativePage_PrefersExistingSourcesRowWhenUnityRebuildsCloudRow()
        {
            var sources = new VisualElement();
            var cloud = new VisualElement();
            var sourcesRow = new VisualElement();
            var rebuiltCloudRow = new VisualElement();
            sources.Add(sourcesRow);
            cloud.Add(rebuiltCloudRow);

            VisualElement result = PackageManagerSubmoduleNativePage
                .ChooseCanonicalSidebarRow(
                    new[] { rebuiltCloudRow, sourcesRow },
                    sources);

            Assert.That(result, Is.SameAs(sourcesRow));
        }

        // ── Helpers ──

        private sealed class HostWithAllLifecycleMethods
        {
            private void BuildGUI() { }
            private void CreateGUI() { }
            private void OnEnable() { }
        }

        private sealed class HostWithCreateGui
        {
            private void CreateGUI() { }
            private void OnEnable() { }
        }

        private sealed class HostWithOnEnable
        {
            private void OnEnable() { }
        }

        private sealed class NativePagePackageStub
        {
            public NativePageVersionsStub versions { get; set; }
        }

        private sealed class NativePageVersionsStub
        {
            public NativePageVersionStub primary { get; set; }
        }

        private sealed class NativePageVersionStub
        {
            public NativePageAuthorStub author { get; set; }
        }

        private sealed class NativePageAuthorStub
        {
            public string name { get; set; }
        }

        private static object ReadField(object instance, string fieldName)
        {
            return instance?.GetType().GetField(
                fieldName,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                ?.GetValue(instance);
        }

        private static void WaitForDiscovery(DiscoveryCoordinator coordinator, int timeoutSeconds)
        {
            var timeoutAt = DateTime.UtcNow.AddSeconds(timeoutSeconds);
            bool gotResults = false;
            while (DateTime.UtcNow < timeoutAt)
            {
                bool changed = coordinator.Tick();
                if (changed && coordinator.DisplayedRepos.Count > 0)
                {
                    gotResults = true;
                }

                if (gotResults && !coordinator.IsLoading)
                {
                    break;
                }

                if (!coordinator.IsLoading && !gotResults)
                {
                    // Still might need one more tick to process completed handle
                    Thread.Sleep(10);
                    coordinator.Tick();
                    break;
                }

                Thread.Sleep(10);
            }
        }

        private static void WaitForBranchFetch(RepositoryCoordinator coordinator)
        {
            var timeoutAt = DateTime.UtcNow.AddSeconds(2);
            while (DateTime.UtcNow < timeoutAt)
            {
                if (coordinator.TickBranchFetch())
                    return;
                Thread.Sleep(10);
            }

            Assert.Fail("Timed out waiting for branch fetch.");
        }

        private static CommandResult Success(string stdOut)
        {
            return new CommandResult
            {
                ExitCode = 0,
                StdOut = stdOut,
                StdErr = string.Empty,
                TerminationConfirmed = true
            };
        }

        private static CommandResult Fail(CommandSpec spec, string error)
        {
            return new CommandResult
            {
                ExitCode = 1,
                StdOut = string.Empty,
                StdErr = $"{error}: {spec.FileName} {spec.Arguments}",
                TerminationConfirmed = true
            };
        }

        private static string BuildRepoJson(int startIndex, int count)
        {
            var items = new List<string>(count);
            for (var i = 0; i < count; i++)
            {
                var index = startIndex + i;
                items.Add(
                    "{" +
                    $"\"name\":\"repo-{index}\"," +
                    "\"owner\":{\"login\":\"EssentialsForUnity\"}," +
                    $"\"html_url\":\"https://github.com/EssentialsForUnity/repo-{index}\"," +
                    "\"default_branch\":\"main\"," +
                    "\"private\":false" +
                    "}");
            }

            return "[" + string.Join(",", items) + "]";
        }

        private sealed class FakeCommandRunner : ICommandRunner
        {
            private readonly Func<CommandSpec, CommandResult> handler;

            internal FakeCommandRunner(Func<CommandSpec, CommandResult> handler)
            {
                this.handler = handler;
            }

            internal List<CommandSpec> Calls { get; } = new();

            public CommandResult Run(CommandSpec spec)
            {
                lock (Calls)
                {
                    Calls.Add(new CommandSpec
                    {
                        FileName = spec.FileName,
                        Arguments = spec.Arguments,
                        WorkingDirectory = spec.WorkingDirectory,
                        TimeoutMs = spec.TimeoutMs
                    });
                }

                return handler(spec);
            }
        }
    }
}
