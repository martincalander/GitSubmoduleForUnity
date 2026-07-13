using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using NUnit.Framework;
using UnityEngine;

namespace MartinCalander.GitPackageManager.Editor.Tests
{
    [Parallelizable(ParallelScope.None)]
    public sealed class GitPackageManagerUtilitiesTests
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

        [TestCase((int)GitPackageManagerWindow.Tab.Discover, true, true, (int)GitPackageManagerWindow.Tab.Installed)]
        [TestCase((int)GitPackageManagerWindow.Tab.Installed, true, true, (int)GitPackageManagerWindow.Tab.Discover)]
        [TestCase((int)GitPackageManagerWindow.Tab.Installed, true, false, (int)GitPackageManagerWindow.Tab.Installed)]
        [TestCase((int)GitPackageManagerWindow.Tab.Discover, false, true, (int)GitPackageManagerWindow.Tab.Discover)]
        public void ResolveRequestedTab_HandlesImguiToggleSelection(
            int current,
            bool installedSelected,
            bool discoverSelected,
            int expected)
        {
            Assert.That(
                (int)GitPackageManagerWindow.ResolveRequestedTab(
                    (GitPackageManagerWindow.Tab)current,
                    installedSelected,
                    discoverSelected),
                Is.EqualTo(expected));
        }

        [TestCase(true, true)]
        [TestCase(false, false)]
        public void PackageTabNavigation_DependsOnKnownGitAvailabilityNotRefreshState(
            bool gitAvailable,
            bool expected)
        {
            Assert.That(
                GitPackageManagerWindow.CanNavigatePackageTabs(gitAvailable),
                Is.EqualTo(expected));
            Assert.That(
                GitPackageManagerWindow.CanUseToolbarGitActions(
                    gitAvailable,
                    isLoading: true,
                    backgroundLoadsDraining: false),
                Is.False,
                "A refresh may block mutations without blocking navigation between tabs.");
        }

        [TestCase(true, false, false, false, false, true)]
        [TestCase(false, false, false, false, false, false)]
        [TestCase(true, true, false, false, false, false)]
        [TestCase(true, false, true, false, false, false)]
        [TestCase(true, false, false, true, false, false)]
        [TestCase(true, false, false, false, true, false)]
        public void InstalledRefresh_IsEnabledOnlyWhenItsHandlerCanStart(
            bool gitAvailable,
            bool installedLoading,
            bool backgroundLoadsDraining,
            bool operationBusy,
            bool recoveryRequiresReview,
            bool expected)
        {
            Assert.That(
                GitPackageManagerWindow.CanRefreshInstalledPackages(
                    gitAvailable,
                    installedLoading,
                    backgroundLoadsDraining,
                    operationBusy,
                    recoveryRequiresReview),
                Is.EqualTo(expected));
        }

        [TestCase(true, true, (int)GitPackageManagerWindow.Tab.Installed, false)]
        [TestCase(true, true, (int)GitPackageManagerWindow.Tab.Discover, true)]
        [TestCase(true, false, (int)GitPackageManagerWindow.Tab.Installed, true)]
        [TestCase(false, false, (int)GitPackageManagerWindow.Tab.Discover, false)]
        public void InitialDependencyLoad_BlocksOnlyUntilGitStageOrOnGitHubTab(
            bool isLoading,
            bool gitStageReady,
            int currentTab,
            bool expected)
        {
            Assert.That(
                GitPackageManagerWindow.ShouldBlockCurrentTabDuringInitialLoad(
                    isLoading,
                    gitStageReady,
                    (GitPackageManagerWindow.Tab)currentTab),
                Is.EqualTo(expected));
        }

        [TestCase(5, 5, 9L, 9L, true)]
        [TestCase(4, 5, 9L, 9L, false)]
        [TestCase(5, 5, 8L, 9L, false)]
        public void BackgroundLoadResult_IsAppliedOnlyForBothCurrentGenerations(
            int resultLoadGeneration,
            int currentLoadGeneration,
            long resultRepositoryGeneration,
            long currentRepositoryGeneration,
            bool expected)
        {
            Assert.That(
                GitPackageManagerWindow.IsBackgroundLoadResultCurrent(
                    resultLoadGeneration,
                    currentLoadGeneration,
                    resultRepositoryGeneration,
                    currentRepositoryGeneration),
                Is.EqualTo(expected));
        }

        [Test]
        public void DeferredRepositoryMutationQueue_PreservesExactlyTheFirstOperationUntilReady()
        {
            var queue = new DeferredRepositoryMutationQueue();
            var competingWindowQueue = new DeferredRepositoryMutationQueue();
            int firstRuns = 0;
            int secondRuns = 0;

            Assert.That(queue.TryEnqueue("  First mutation  ", () => firstRuns++), Is.True);
            Assert.That(competingWindowQueue.TryEnqueue("Second mutation", () => secondRuns++), Is.False);
            Assert.That(queue.HasPending, Is.True);
            Assert.That(queue.Label, Is.EqualTo("First mutation"));
            Assert.That(queue.TryDequeueWhenReady(false, out _), Is.False);
            Assert.That(queue.HasPending, Is.True);

            Assert.That(queue.TryDequeueWhenReady(true, out Action queuedMutation), Is.True);
            queuedMutation();

            Assert.That(firstRuns, Is.EqualTo(1));
            Assert.That(secondRuns, Is.Zero);
            Assert.That(queue.HasPending, Is.False);
            Assert.That(queue.Label, Is.Empty);
            Assert.That(competingWindowQueue.TryEnqueue("Second mutation", () => secondRuns++), Is.True);
            competingWindowQueue.Clear();
        }

        [Test]
        public void DeferredRepositoryMutationQueue_ClearPreventsExecutionAfterWindowDisable()
        {
            var queue = new DeferredRepositoryMutationQueue();
            Assert.That(queue.TryEnqueue("Remove package", () => { }), Is.True);

            queue.Clear();

            Assert.That(queue.TryDequeueWhenReady(true, out _), Is.False);
            Assert.That(queue.HasPending, Is.False);
        }

        [Test]
        public void VirtualizedRows_ClampAStaleScrollOffsetAndKeepTailRowsVisible()
        {
            Assert.That(
                GitPackageManagerWindow.ClampVirtualizedScrollOffset(10000f, 5, 24f),
                Is.EqualTo(120f));
            Assert.That(
                GitPackageManagerWindow.ClampVirtualizedScrollOffset(float.NaN, 5, 24f),
                Is.Zero);

            GitPackageManagerWindow.CalculateVisibleRowRange(
                10000f,
                100,
                24f,
                120f,
                out int firstRow,
                out int lastRow);

            Assert.That(firstRow, Is.EqualTo(92));
            Assert.That(lastRow, Is.EqualTo(100));

            GitPackageManagerWindow.CalculateVisibleRowRange(
                10000f,
                5,
                24f,
                400f,
                out firstRow,
                out lastRow);

            Assert.That(firstRow, Is.Zero);
            Assert.That(lastRow, Is.EqualTo(5));
        }

        [Test]
        public void GitHubStageFailure_PreservesGitAndInstalledPackageState()
        {
            var packages = new List<GitPackageInfo> { new GitPackageInfo { Name = "package" } };
            var result = new GitPackageManagerWindow.InitialLoadResult
            {
                GitAvailable = true,
                GitVersion = "git version 2.50.0",
                PackagesSuccess = true,
                Packages = packages,
                GhAvailable = true,
                GhAuthenticated = true
            };

            GitPackageManagerWindow.RecordInitialLoadFailure(
                result,
                GitPackageManagerWindow.InitialLoadStage.GitHub,
                new InvalidOperationException("gh probe failed"));

            Assert.That(result.GitAvailable, Is.True);
            Assert.That(result.GitVersion, Is.EqualTo("git version 2.50.0"));
            Assert.That(result.PackagesSuccess, Is.True);
            Assert.That(result.Packages, Is.SameAs(packages));
            Assert.That(result.GhAvailable, Is.False);
            Assert.That(result.GhAuthenticated, Is.False);
            Assert.That(result.GhError, Does.Contain("gh probe failed"));
        }

        [Test]
        public void InstalledPackageStageFailure_PreservesGitAndBoundsTheDiagnostic()
        {
            var result = new GitPackageManagerWindow.InitialLoadResult
            {
                GitAvailable = true,
                GitVersion = "git version 2.50.0",
                PackagesSuccess = true,
                Packages = new List<GitPackageInfo> { new GitPackageInfo { Name = "stale" } }
            };

            GitPackageManagerWindow.RecordInitialLoadFailure(
                result,
                GitPackageManagerWindow.InitialLoadStage.InstalledPackages,
                new InvalidOperationException(new string('x', 10000)));

            Assert.That(result.GitAvailable, Is.True);
            Assert.That(result.GitVersion, Is.EqualTo("git version 2.50.0"));
            Assert.That(result.PackagesSuccess, Is.False);
            Assert.That(result.Packages, Is.Empty);
            Assert.That(result.PackagesError.Length, Is.LessThanOrEqualTo(GitHubUtility.MaxUiDiagnosticCharacters));
            Assert.That(result.PackagesError, Does.Contain("installed package scan"));
        }

        [Test]
        public void ManualPackageName_IsNotReplacedWithoutAUrlEdit()
        {
            const string manualName = "my intentional draft";

            string resolved = GitPackageManagerWindow.ResolvePackageNameAfterUrlEdit(
                manualName,
                false,
                "com.example.previous",
                "com.example.automatic");

            Assert.That(resolved, Is.EqualTo(manualName));
        }

        [Test]
        public void UrlEdit_UpdatesThePackageNameSuggestionOnce()
        {
            string resolved = GitPackageManagerWindow.ResolvePackageNameAfterUrlEdit(
                "com.example.old",
                true,
                "com.example.old",
                "com.example.new");

            Assert.That(resolved, Is.EqualTo("com.example.new"));
        }

        [Test]
        public void UrlEdit_PreservesACustomPackageName()
        {
            const string customName = "my intentional draft";

            string resolved = GitPackageManagerWindow.ResolvePackageNameAfterUrlEdit(
                customName,
                true,
                "com.example.previous",
                "com.example.new");

            Assert.That(resolved, Is.EqualTo(customName));
        }

        [Test]
        public void AddFromUrlPopup_HasRoomForDiagnosticsAndScrollableControls()
        {
            Vector2 size = GitPackageManagerWindow.GetAddFromUrlPopupSize();

            Assert.That(size.x, Is.GreaterThanOrEqualTo(420f));
            Assert.That(size.y, Is.GreaterThanOrEqualTo(300f));
        }

        [TestCase(false, (int)UnityEditor.MessageType.Warning)]
        [TestCase(true, (int)UnityEditor.MessageType.Error)]
        public void DiscoveryDrainStatus_EscalatesRestartRequirement(
            bool requiresEditorRestart,
            int expectedMessageType)
        {
            Assert.That(
                (int)GitPackageManagerWindow.GetDiscoveryDrainStatusType(requiresEditorRestart),
                Is.EqualTo(expectedMessageType));
        }

        [Test]
        public void TryReadPackageNameFromJson_ReadsStructuredName()
        {
            var success = GitUtility.TryReadPackageNameFromJson(
                "{ \"name\": \"com.martincalander.gitpackagemanager\", \"displayName\": \"Git Package Manager\" }",
                out var packageName);

            Assert.That(success, Is.True);
            Assert.That(packageName, Is.EqualTo("com.martincalander.gitpackagemanager"));
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
                out var error);

            Assert.That(success, Is.True, error);
            Assert.That(packageName, Is.EqualTo("com.example.valid-package"));
            Assert.That(error, Is.Empty);
        }

        [TestCase(0.0, 0)]
        [TestCase(0.099, 0)]
        [TestCase(0.101, 1)]
        [TestCase(1.101, 11)]
        [TestCase(1.201, 0)]
        [TestCase(-1.0, 0)]
        public void LoadingSpinnerFrameIndex_AdvancesAtTenFramesPerSecond(double timeSeconds, int expectedFrame)
        {
            Assert.That(GitPackageManagerWindow.GetLoadingSpinnerFrameIndex(timeSeconds), Is.EqualTo(expectedFrame));
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
                GitHubUtility.TryParseGitHubRepo("https://github.com/martincalander/com.martincalander.gitpackagemanager.git", out var httpsOwner, out var httpsRepo),
                Is.True);
            Assert.That(httpsOwner, Is.EqualTo("martincalander"));
            Assert.That(httpsRepo, Is.EqualTo("com.martincalander.gitpackagemanager"));

            Assert.That(
                GitHubUtility.TryParseGitHubRepo("git@github.com:EssentialsForUnity/com.essentials.extensions.git", out var sshOwner, out var sshRepo),
                Is.True);
            Assert.That(sshOwner, Is.EqualTo("EssentialsForUnity"));
            Assert.That(sshRepo, Is.EqualTo("com.essentials.extensions"));
        }

        [Test]
        public void ParseSubmoduleCommitMap_ParsesTrackedAndUninitializedEntries()
        {
            const string statusOutput =
                "-1234567890abcdef1234567890abcdef12345678 Packages/com.martincalander.gitpackagemanager\n" +
                " abcdef0123456789abcdef0123456789abcdef01 Packages\\com.essentials.extensions (heads/main)\n";

            var commitMap = GitUtility.ParseSubmoduleCommitMap(statusOutput);

            Assert.That(commitMap["Packages/com.martincalander.gitpackagemanager"], Is.EqualTo("1234567890abcdef1234567890abcdef12345678"));
            Assert.That(commitMap["Packages/com.essentials.extensions"], Is.EqualTo("abcdef0123456789abcdef0123456789abcdef01"));
        }

        [Test]
        public void NormalizePath_ReplacesBackslashesAndTrimsWhitespace()
        {
            var normalized = GitUtility.NormalizePath(@"  Packages\com.martincalander.gitpackagemanager  ");

            Assert.That(normalized, Is.EqualTo("Packages/com.martincalander.gitpackagemanager"));
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

        [Test]
        public void CliInstaller_MacGit_UsesSystemInstallerWhenAvailable()
        {
            var plan = CliInstaller.GetInstallPlan(
                ToolKind.Git,
                RuntimePlatform.OSXEditor,
                command => command == "xcode-select");

            Assert.That(plan.CanRunAutomatically, Is.True);
            Assert.That(plan.OpensSystemInstaller, Is.True);
            Assert.That(plan.FileName, Is.EqualTo("xcode-select"));
            Assert.That(plan.Arguments, Is.EqualTo("--install"));
            Assert.That(plan.DisplayCommand, Is.EqualTo("xcode-select --install"));
        }

        [Test]
        public void CliInstaller_WindowsGh_UsesWingetWithExplicitAgreements()
        {
            var plan = CliInstaller.GetInstallPlan(
                ToolKind.GitHubCli,
                RuntimePlatform.WindowsEditor,
                command => command == "winget");

            Assert.That(plan.CanRunAutomatically, Is.True);
            Assert.That(plan.FileName, Is.EqualTo("winget"));
            Assert.That(plan.Arguments, Does.Contain("--id GitHub.cli -e"));
            Assert.That(plan.Arguments, Does.Contain("--accept-source-agreements"));
            Assert.That(plan.Arguments, Does.Contain("--accept-package-agreements"));
            Assert.That(plan.DisplayCommand, Is.EqualTo($"winget {plan.Arguments}"));
        }

        [Test]
        public void CliInstaller_Linux_LeavesPrivilegePromptInTerminal()
        {
            var plan = CliInstaller.GetInstallPlan(
                ToolKind.Git,
                RuntimePlatform.LinuxEditor,
                command => command == "apt-get");

            Assert.That(plan.CanRunAutomatically, Is.False);
            Assert.That(plan.CanCopyCommand, Is.True);
            Assert.That(plan.DisplayCommand, Is.EqualTo("sudo apt-get install git"));
            Assert.That(plan.AutomaticInstallUnavailableReason, Does.Contain("terminal"));
        }

        [Test]
        public void CliInstaller_LinuxGit_SelectsDetectedPackageManager()
        {
            var plan = CliInstaller.GetInstallPlan(
                ToolKind.Git,
                RuntimePlatform.LinuxEditor,
                command => command == "dnf");

            Assert.That(plan.DisplayCommand, Is.EqualTo("sudo dnf install git"));
        }

        [Test]
        public void CliInstaller_MissingPackageManager_FallsBackToOfficialGuide()
        {
            var plan = CliInstaller.GetInstallPlan(
                ToolKind.GitHubCli,
                RuntimePlatform.OSXEditor,
                _ => false);

            Assert.That(plan.CanRunAutomatically, Is.False);
            Assert.That(plan.InstallUrl, Is.EqualTo("https://cli.github.com/"));
            Assert.That(plan.AutomaticInstallUnavailableReason, Does.Contain("Homebrew"));
        }

        [Test]
        public void DependencyGate_GitLocksEverythingButGhOnlyLocksDiscovery()
        {
            Assert.That(
                GitPackageManagerWindow.GetDependencyGateState(false, true, true, false),
                Is.EqualTo(DependencyGateState.GitMissing));
            Assert.That(
                GitPackageManagerWindow.GetDependencyGateState(true, false, false, false),
                Is.EqualTo(DependencyGateState.Ready));
            Assert.That(
                GitPackageManagerWindow.GetDependencyGateState(true, false, false, true),
                Is.EqualTo(DependencyGateState.GitHubCliMissing));
            Assert.That(
                GitPackageManagerWindow.GetDependencyGateState(true, true, false, true),
                Is.EqualTo(DependencyGateState.GitHubAuthenticationMissing));
            Assert.That(
                GitPackageManagerWindow.GetDependencyGateState(true, true, true, true),
                Is.EqualTo(DependencyGateState.Ready));
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
                GitPackageManagerUserSettings.ShouldShowWelcome(persisted, shownThisSession),
                Is.EqualTo(expected));
        }

        [TestCase(-10, GitPackageManagerUserSettings.MinimumRefreshIntervalMinutes)]
        [TestCase(1, 1)]
        [TestCase(17, 17)]
        [TestCase(60, 60)]
        [TestCase(500, GitPackageManagerUserSettings.MaximumRefreshIntervalMinutes)]
        public void UserSettings_ClampRefreshInterval(int input, int expected)
        {
            Assert.That(
                GitPackageManagerUserSettings.ClampRefreshIntervalMinutes(input),
                Is.EqualTo(expected));
        }

        [Test]
        public void UserSettings_UseProjectLocalUserSettingsPath()
        {
            Assert.That(
                GitPackageManagerUserSettings.SettingsFilePath,
                Is.EqualTo("UserSettings/GitPackageManagerSettings.asset"));
        }

        [TestCase(-1, (int)GitPackageManagerStartupTab.InProject)]
        [TestCase(0, (int)GitPackageManagerStartupTab.InProject)]
        [TestCase(1, (int)GitPackageManagerStartupTab.GitHub)]
        [TestCase(99, (int)GitPackageManagerStartupTab.InProject)]
        public void UserSettings_NormalizeStartupTab(int input, int expected)
        {
            Assert.That(
                (int)GitPackageManagerUserSettings.NormalizeStartupTab(
                    (GitPackageManagerStartupTab)input),
                Is.EqualTo(expected));
        }

        [TestCase(-1, (int)GitPackageManagerDefaultGitHubFilter.AllRepositories)]
        [TestCase(0, (int)GitPackageManagerDefaultGitHubFilter.AllRepositories)]
        [TestCase(1, (int)GitPackageManagerDefaultGitHubFilter.ValidUpmPackages)]
        [TestCase(99, (int)GitPackageManagerDefaultGitHubFilter.AllRepositories)]
        public void UserSettings_NormalizeDefaultGitHubFilter(int input, int expected)
        {
            Assert.That(
                (int)GitPackageManagerUserSettings.NormalizeDefaultGitHubFilter(
                    (GitPackageManagerDefaultGitHubFilter)input),
                Is.EqualTo(expected));
        }

        [TestCase(false, 3600.0, 300.0, false)]
        [TestCase(true, 299.0, 300.0, false)]
        [TestCase(true, 300.0, 300.0, false)]
        [TestCase(true, 301.0, 300.0, true)]
        [TestCase(true, 1.0, -1.0, true)]
        public void InProjectRefresh_RespectsPreferenceAndInterval(
            bool enabled,
            double elapsedSeconds,
            double intervalSeconds,
            bool expected)
        {
            Assert.That(
                GitPackageManagerWindow.ShouldRefreshInstalledPackagesOnReturn(
                    enabled,
                    elapsedSeconds,
                    intervalSeconds),
                Is.EqualTo(expected));
        }

        [Test]
        public void WelcomePresentation_IsRecordedOnlyAfterARepaint()
        {
            Assert.That(
                GitPackageManagerWindow.ShouldRecordWelcomeShown(true, false, EventType.Layout),
                Is.False);
            Assert.That(
                GitPackageManagerWindow.ShouldRecordWelcomeShown(true, false, EventType.Repaint),
                Is.True);
            Assert.That(
                GitPackageManagerWindow.ShouldRecordWelcomeShown(false, false, EventType.Repaint),
                Is.False);
            Assert.That(
                GitPackageManagerWindow.ShouldRecordWelcomeShown(true, true, EventType.Repaint),
                Is.False);
        }

        [TestCase(false, false, false)]
        [TestCase(false, true, true)]
        [TestCase(true, false, true)]
        [TestCase(true, true, true)]
        public void WelcomeReopen_PreservesWhetherThePreferenceWasActuallyRecorded(
            bool persisted,
            bool shownThisSession,
            bool expected)
        {
            Assert.That(
                GitPackageManagerWindow.IsWelcomePreferenceAlreadyRecorded(
                    persisted,
                    shownThisSession),
                Is.EqualTo(expected));
        }

        [TestCase(true, true, true, true, (int)GitPackageManagerWindow.WelcomeSetupState.Checking)]
        [TestCase(false, false, true, true, (int)GitPackageManagerWindow.WelcomeSetupState.GitMissing)]
        [TestCase(false, true, false, false, (int)GitPackageManagerWindow.WelcomeSetupState.GitHubCliMissing)]
        [TestCase(false, true, true, false, (int)GitPackageManagerWindow.WelcomeSetupState.GitHubAuthenticationMissing)]
        [TestCase(false, true, true, true, (int)GitPackageManagerWindow.WelcomeSetupState.Ready)]
        public void WelcomeSetupState_UsesStableDependencyPrecedence(
            bool checking,
            bool git,
            bool gh,
            bool authenticated,
            int expected)
        {
            Assert.That(
                (int)GitPackageManagerWindow.GetWelcomeSetupState(checking, git, gh, authenticated),
                Is.EqualTo(expected));
        }

        [TestCase(true, true, false)]
        [TestCase(false, false, false)]
        [TestCase(false, true, true)]
        public void WelcomeCanFinish_RequiresCompletedGitCheck(
            bool checking,
            bool git,
            bool expected)
        {
            Assert.That(GitPackageManagerWindow.CanFinishWelcome(checking, git), Is.EqualTo(expected));
        }

        [TestCase(519f, true)]
        [TestCase(520f, false)]
        [TestCase(620f, false)]
        public void WelcomeActions_StackOnlyAtNarrowWidths(float width, bool expected)
        {
            Assert.That(GitPackageManagerWindow.ShouldStackWelcomeActions(width), Is.EqualTo(expected));
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
        public void GitHubAuthenticationFailure_DoesNotExposeCommandOutput()
        {
            var result = new CommandResult
            {
                ExitCode = 1,
                StdOut = "one-time-code-SECRET",
                StdErr = "https://user:secret@github.com/login/device",
                TerminationConfirmed = true
            };

            string message = GitPackageManagerWindow.BuildGitHubAuthenticationFailureMessage(result);

            Assert.That(message, Does.Contain("exit code 1"));
            Assert.That(message, Does.Not.Contain("one-time-code-SECRET"));
            Assert.That(message, Does.Not.Contain("user:secret"));
        }

        [Test]
        public void GitHubAuthenticationFailure_UnconfirmedTerminationRequiresRestart()
        {
            var result = new CommandResult
            {
                ExitCode = -1,
                Cancelled = true,
                TerminationConfirmed = false
            };

            string message = GitPackageManagerWindow.BuildGitHubAuthenticationFailureMessage(result);

            Assert.That(message, Does.Contain("could not confirm"));
            Assert.That(message, Does.Contain("Restart Unity"));
        }

        [Test]
        public void IsCurrentPackage_DetectsPackageIdOrInstalledPath()
        {
            Assert.That(
                GitPackageManagerWindow.IsCurrentPackage(new GitPackageInfo
                {
                    PackageName = "com.martincalander.gitpackagemanager",
                    Path = "Packages/com.example.renamedfolder"
                }),
                Is.True);
            Assert.That(
                GitPackageManagerWindow.IsCurrentPackage(new GitPackageInfo
                {
                    PackageName = null,
                    Path = @"Packages\com.martincalander.gitpackagemanager"
                }),
                Is.True);
            Assert.That(
                GitPackageManagerWindow.IsCurrentPackage(new GitPackageInfo
                {
                    PackageName = "com.example.otherpackage",
                    Path = "Packages/com.example.otherpackage"
                }),
                Is.False);
            Assert.That(GitPackageManagerWindow.IsCurrentPackage(null), Is.False);
        }

        [Test]
        public void BuildSelfRemovalWarning_ExplainsImpactAndRecovery()
        {
            string warning = GitPackageManagerWindow.BuildSelfRemovalWarning(
                "Packages/com.martincalander.gitpackagemanager");

            Assert.That(warning, Does.Contain("Git Package Manager itself"));
            Assert.That(warning, Does.Contain("window will close"));
            Assert.That(warning, Does.Contain("reinstall"));
            Assert.That(warning, Does.Contain("UPM"));
            Assert.That(warning, Does.Contain("reviewed and committed"));
        }

        [Test]
        public void BuildCliInstallFailureMessage_PreservesActionableErrorAndRedactsCredentials()
        {
            var result = new CommandResult
            {
                ExitCode = 23,
                StdOut = string.Empty,
                StdErr = "download failed for https://user:secret@example.com/tool"
            };

            string message = GitPackageManagerWindow.BuildCliInstallFailureMessage("Git", result);

            Assert.That(message, Does.Contain("exit code 23"));
            Assert.That(message, Does.Contain("download failed"));
            Assert.That(message, Does.Contain("retry"));
            Assert.That(message, Does.Not.Contain("user:secret"));
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
            WaitForBranchFetch(coordinator);

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
        public void DiscoveryCoordinator_SearchUsesSearchApi()
        {
            var runner = new FakeCommandRunner(spec =>
            {
                if (spec.FileName == "gh" && spec.Arguments.Contains("api user --jq"))
                {
                    return Success("EssentialsForUnity");
                }

                if (spec.FileName == "gh" && spec.Arguments.Contains("search/repositories"))
                {
                    return Success("{\"total_count\":1,\"items\":" + BuildRepoJson(1, 1) + "}");
                }

                return Fail(spec, "Unexpected");
            });
            CliCommandRunner.CurrentRunner = runner;

            using var coordinator = new DiscoveryCoordinator();
            coordinator.EnsureUsername();

            // Wait for username to resolve
            WaitForDiscovery(coordinator, 2);

            coordinator.SetSearchQuery("test", 0);
            coordinator.Tick(1.0); // past debounce — triggers search fetch

            WaitForDiscovery(coordinator, 2, 1.0);

            Assert.That(coordinator.DisplayedRepos, Has.Count.EqualTo(1));
            var searchCall = runner.Calls.FirstOrDefault(c => c.Arguments.Contains("search/repositories"));
            Assert.That(searchCall, Is.Not.Null);
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
                coordinator.Tick(0);
                if (coordinator.DisplayedRepos.Count != 50)
                    break;
                Thread.Sleep(10);
            }

            Assert.That(coordinator.CurrentPage, Is.EqualTo(2));
            Assert.That(coordinator.DisplayedRepos, Has.Count.EqualTo(10));
            Assert.That(coordinator.HasPrevPage, Is.True);
        }

        [Test]
        public void DiscoveryCoordinator_NewerSearchSupersedesInFlightPage()
        {
            var runner = new FakeCommandRunner(spec =>
            {
                if (spec.Arguments.Contains("api user --jq"))
                    return Success("owner");

                if (spec.Arguments.Contains("user/repos"))
                {
                    Thread.Sleep(30);
                    return Success(BuildRepoJson(1, 50));
                }

                if (spec.Arguments.Contains("search/repositories"))
                    return Success("{\"total_count\":1,\"items\":" + BuildRepoJson(100, 1) + "}");

                return Fail(spec, "Unexpected");
            });
            CliCommandRunner.CurrentRunner = runner;

            using var coordinator = new DiscoveryCoordinator();
            coordinator.LoadInitialPage();
            coordinator.SetSearchQuery("newest", 0);
            coordinator.Tick(1.0);

            WaitForDiscovery(coordinator, 2, 1.0);

            Assert.That(coordinator.DisplayedRepos, Has.Count.EqualTo(1));
            Assert.That(coordinator.DisplayedRepos[0].Name, Is.EqualTo("repo-100"));
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
                coordinator.Tick(0);
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

        // ── Helpers ──

        private static void WaitForDiscovery(DiscoveryCoordinator coordinator, int timeoutSeconds, double tickTime = 0)
        {
            var timeoutAt = DateTime.UtcNow.AddSeconds(timeoutSeconds);
            bool gotResults = false;
            while (DateTime.UtcNow < timeoutAt)
            {
                bool changed = coordinator.Tick(tickTime);
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
                    coordinator.Tick(tickTime);
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
