using System;
using System.IO;
using System.Text;
using System.Threading;
using NUnit.Framework;

namespace MartinCalander.GitSubmoduleManager.Editor.Tests
{
    [Parallelizable(ParallelScope.None)]
    public sealed class GitPackageConversionServiceTests
    {
        private const string PackageName = "com.example.convert";
        private const string PackagePath = "Packages/" + PackageName;

        private string testRoot;
        private string projectRoot;
        private string remoteWorkRoot;
        private string remoteRoot;
        private string remoteUrl;
        private string remoteCommit;
        private IDisposable projectRootOverride;
        private ICommandRunner previousRunner;

        [SetUp]
        public void SetUp()
        {
            previousRunner = CliCommandRunner.CurrentRunner;
            testRoot = Path.Combine(
                Path.GetTempPath(),
                "GitPackageConversionServiceTests-" + Guid.NewGuid().ToString("N"));
            projectRoot = Path.Combine(testRoot, "Project");
            remoteRoot = Path.Combine(testRoot, "Remote.git");
            Directory.CreateDirectory(projectRoot);
            CreateRemotePackage();
            InitializeProject();
            projectRootOverride = GitUtility.OverrideProjectRootForTests(projectRoot);
        }

        [TearDown]
        public void TearDown()
        {
            CliCommandRunner.CurrentRunner = previousRunner;
            projectRootOverride?.Dispose();
            if (!string.IsNullOrWhiteSpace(testRoot) && Directory.Exists(testRoot))
                Directory.Delete(testRoot, true);
        }

        [Test]
        public void ReadOnlyToSubmodule_CreatesExactCommitBeforeRemovingManifestEntry()
        {
            string spec = remoteUrl + "#main";
            WriteManifest(spec);
            CommitProject("read-only source");
            WriteLockfileSentinel(out byte[] lockfileBefore);
            MoveRemoteDefaultBranchPackageIntoSubfolder();
            var info = new PackageManagerReadOnlyGitInfo(
                PackageName,
                remoteUrl,
                spec,
                "main",
                remoteCommit,
                null);
            var state = new GitPackageConversionTaskState();

            CommandResult result = GitPackageConversionService.RunToSubmoduleTask(
                info,
                PackagePath,
                state,
                CancellationToken.None);

            Assert.That(result.IsSuccess, Is.True, result.StdErr);
            Assert.That(state.ConvertedSuccessfully, Is.True);
            Assert.That(
                PackageManifestGitDependencyStore.TryGetProjectDependency(
                    PackageName,
                    out _,
                    out string missingError),
                Is.False);
            Assert.That(missingError, Does.Contain("not declared"));
            Assert.That(
                RunGit(projectRoot, $"-C {PackagePath} rev-parse --verify HEAD")
                    .StdOut.Trim(),
                Is.EqualTo(remoteCommit).IgnoreCase);
            Assert.That(
                RunGit(projectRoot, $"ls-files --stage -- {PackagePath}")
                    .StdOut,
                Does.StartWith("160000 "));
            AssertLockfileUnchanged(lockfileBefore);
        }

        [Test]
        public void ReadOnlySubfolderPackage_IsNotConvertibleAndLeavesProjectUntouched()
        {
            string spec = remoteUrl + "?path=/Nested#main";
            WriteManifest(spec);
            CommitProject("subfolder read-only source");
            byte[] manifestBefore = File.ReadAllBytes(
                Path.Combine(projectRoot, "Packages", "manifest.json"));
            WriteLockfileSentinel(out byte[] lockfileBefore);
            var info = new PackageManagerReadOnlyGitInfo(
                PackageName,
                remoteUrl,
                spec,
                "main",
                remoteCommit,
                "/Nested",
                null);

            Assert.That(
                GitPackageConversionService.ValidateToSubmodule(info),
                Is.EqualTo(GitPackageConversionService.RootPackageRequiredMessage));

            var state = new GitPackageConversionTaskState();
            CommandResult result = GitPackageConversionService.RunToSubmoduleTask(
                info,
                PackagePath,
                state,
                CancellationToken.None);

            Assert.That(result.IsSuccess, Is.False);
            Assert.That(state.ConvertedSuccessfully, Is.False);
            Assert.That(state.Message, Does.Contain("package.json is at the repository root"));
            CollectionAssert.AreEqual(
                manifestBefore,
                File.ReadAllBytes(
                    Path.Combine(projectRoot, "Packages", "manifest.json")));
            AssertLockfileUnchanged(lockfileBefore);
            Assert.That(File.Exists(Path.Combine(projectRoot, ".gitmodules")), Is.False);
            Assert.That(Directory.Exists(Path.Combine(projectRoot, PackagePath)), Is.False);
        }

        [Test]
        public void SubmoduleToReadOnly_AddsPinnedDependencyBeforeSafeGitRemoval()
        {
            WriteManifest(null);
            CommitProject("empty manifest");
            CommandResult add = RunGit(
                projectRoot,
                $"-c protocol.file.allow=always submodule add -b main {remoteUrl} {PackagePath}");
            Assert.That(add.IsSuccess, Is.True, add.StdErr);
            CommitProject("add source submodule");
            WriteLockfileSentinel(out byte[] lockfileBefore);
            var info = new PackageManagerSubmoduleInfo(
                PackageName,
                PackagePath,
                Path.Combine(projectRoot, PackagePath),
                remoteUrl,
                false);
            var state = new GitPackageConversionTaskState();

            CommandResult result = GitPackageConversionService.RunToReadOnlyTask(
                info,
                state,
                CancellationToken.None);

            Assert.That(result.IsSuccess, Is.True, result.StdErr);
            Assert.That(state.ConvertedSuccessfully, Is.True);
            Assert.That(
                PackageManifestGitDependencyStore.TryGetProjectDependency(
                    PackageName,
                    out PackageManifestGitDependency dependency,
                    out string dependencyError),
                Is.True,
                dependencyError);
            Assert.That(dependency.Spec, Is.EqualTo(remoteUrl + "#" + remoteCommit));
            Assert.That(Directory.Exists(Path.Combine(projectRoot, PackagePath)), Is.False);
            Assert.That(
                RunGit(projectRoot, $"ls-files --error-unmatch -- {PackagePath}")
                    .ExitCode,
                Is.EqualTo(1));
            AssertLockfileUnchanged(lockfileBefore);
        }

        [Test]
        public void SubmoduleToReadOnly_DirtyWorktreeRequiresExactConfirmation()
        {
            PackageManagerSubmoduleInfo info = CreateInstalledSubmodule();
            string packageJson = Path.Combine(
                projectRoot,
                PackagePath,
                "package.json");
            File.AppendAllText(packageJson, "\n// local work confirmed for discard\n");

            var blockedState = new GitPackageConversionTaskState();
            CommandResult blocked = GitPackageConversionService.RunToReadOnlyTask(
                info,
                blockedState,
                CancellationToken.None);

            Assert.That(blocked.IsSuccess, Is.False);
            Assert.That(blockedState.Message, Does.Contain("explicitly confirmed"));
            Assert.That(File.Exists(packageJson), Is.True);
            Assert.That(
                PackageManifestGitDependencyStore.TryGetProjectDependency(
                    PackageName,
                    out _,
                    out _),
                Is.False);

            Assert.That(
                GitUtility.TryAssessSubmoduleRemoval(
                    PackagePath,
                    out SubmoduleRemovalAssessment confirmed,
                    out string assessmentError),
                Is.True,
                assessmentError);
            Assert.That(confirmed.IsSafe, Is.False);
            Assert.That(confirmed.HasLocalOnlyCommits, Is.False);

            var confirmedState = new GitPackageConversionTaskState();
            CommandResult converted = GitPackageConversionService.RunToReadOnlyTask(
                info,
                confirmed,
                true,
                confirmedState,
                CancellationToken.None);

            Assert.That(converted.IsSuccess, Is.True, converted.StdErr);
            Assert.That(confirmedState.ConvertedSuccessfully, Is.True);
            Assert.That(Directory.Exists(Path.Combine(projectRoot, PackagePath)), Is.False);
            Assert.That(
                PackageManifestGitDependencyStore.TryGetProjectDependency(
                    PackageName,
                    out PackageManifestGitDependency dependency,
                    out string dependencyError),
                Is.True,
                dependencyError);
            Assert.That(dependency.Spec, Is.EqualTo(remoteUrl + "#" + remoteCommit));
        }

        [Test]
        public void SubmoduleToReadOnly_LocalOnlyCommitCannotBeDiscardedByConfirmation()
        {
            PackageManagerSubmoduleInfo info = CreateInstalledSubmodule();
            string packageRoot = Path.Combine(projectRoot, PackagePath);
            RequireGit(packageRoot, "checkout --detach");
            File.AppendAllText(
                Path.Combine(packageRoot, "package.json"),
                "\n");
            RequireGit(packageRoot, "add -- package.json");
            RequireGit(packageRoot, "commit -m local-only");
            string localHead = RequireGit(packageRoot, "rev-parse HEAD")
                .StdOut.Trim();
            byte[] manifestBefore = File.ReadAllBytes(
                Path.Combine(projectRoot, "Packages", "manifest.json"));

            Assert.That(
                GitUtility.TryAssessSubmoduleRemoval(
                    PackagePath,
                    out SubmoduleRemovalAssessment confirmed,
                    out string assessmentError),
                Is.True,
                assessmentError);
            Assert.That(confirmed.HasLocalOnlyCommits, Is.True);

            var state = new GitPackageConversionTaskState();
            CommandResult result = GitPackageConversionService.RunToReadOnlyTask(
                info,
                confirmed,
                true,
                state,
                CancellationToken.None);

            Assert.That(result.IsSuccess, Is.False);
            Assert.That(
                state.Message,
                Does.StartWith(
                    GitPackageConversionService.LocalOnlyCommitReadOnlyMessage));
            CollectionAssert.AreEqual(
                manifestBefore,
                File.ReadAllBytes(
                    Path.Combine(projectRoot, "Packages", "manifest.json")));
            Assert.That(
                RequireGit(packageRoot, "rev-parse HEAD").StdOut.Trim(),
                Is.EqualTo(localHead));
        }

        [Test]
        public void SubmoduleToReadOnly_FetchableCommitIsAllowedDespiteStaleRemoteTrackingRefs()
        {
            PackageManagerSubmoduleInfo info = CreateInstalledSubmodule();
            string packageRoot = Path.Combine(projectRoot, PackagePath);
            ConfigureIdentity(packageRoot);
            File.AppendAllText(
                Path.Combine(packageRoot, "package.json"),
                "\n");
            RequireGit(packageRoot, "add -- package.json");
            RequireGit(packageRoot, "commit -m published-with-stale-tracking");
            string publishedHead = RequireGit(packageRoot, "rev-parse HEAD")
                .StdOut.Trim();
            RequireGit(packageRoot, "push origin HEAD:refs/heads/published");
            RequireGit(
                packageRoot,
                "update-ref -d refs/remotes/origin/published");

            Assert.That(
                GitUtility.TryAssessSubmoduleRemoval(
                    PackagePath,
                    out SubmoduleRemovalAssessment confirmed,
                    out string assessmentError),
                Is.True,
                assessmentError);
            Assert.That(
                confirmed.HasLocalOnlyCommits,
                Is.True,
                "The fixture must prove local remote-tracking refs are stale.");

            var state = new GitPackageConversionTaskState();
            CommandResult result = GitPackageConversionService.RunToReadOnlyTask(
                info,
                confirmed,
                true,
                state,
                CancellationToken.None);

            Assert.That(result.IsSuccess, Is.True, result.StdErr);
            Assert.That(
                PackageManifestGitDependencyStore.TryGetProjectDependency(
                    PackageName,
                    out PackageManifestGitDependency dependency,
                    out string dependencyError),
                Is.True,
                dependencyError);
            Assert.That(dependency.Spec, Is.EqualTo(remoteUrl + "#" + publishedHead));
        }

        [Test]
        public void SubmoduleToReadOnly_UnfetchableCommitIsBlockedDespiteLocalRemoteTrackingRef()
        {
            PackageManagerSubmoduleInfo info = CreateInstalledSubmodule();
            string packageRoot = Path.Combine(projectRoot, PackagePath);
            ConfigureIdentity(packageRoot);
            File.AppendAllText(
                Path.Combine(packageRoot, "package.json"),
                "\n");
            RequireGit(packageRoot, "add -- package.json");
            RequireGit(packageRoot, "commit -m unpublished-with-fake-tracking");
            RequireGit(
                packageRoot,
                "update-ref refs/remotes/origin/fake HEAD");
            byte[] manifestBefore = File.ReadAllBytes(
                Path.Combine(projectRoot, "Packages", "manifest.json"));

            Assert.That(
                GitUtility.TryAssessSubmoduleRemoval(
                    PackagePath,
                    out SubmoduleRemovalAssessment confirmed,
                    out string assessmentError),
                Is.True,
                assessmentError);
            Assert.That(
                confirmed.HasLocalOnlyCommits,
                Is.False,
                "The fixture must prove local remote-tracking refs can false-allow.");

            var state = new GitPackageConversionTaskState();
            CommandResult result = GitPackageConversionService.RunToReadOnlyTask(
                info,
                confirmed,
                true,
                state,
                CancellationToken.None);

            Assert.That(result.IsSuccess, Is.False);
            Assert.That(state.Message, Does.Contain("cannot be fetched"));
            CollectionAssert.AreEqual(
                manifestBefore,
                File.ReadAllBytes(
                    Path.Combine(projectRoot, "Packages", "manifest.json")));
            Assert.That(Directory.Exists(packageRoot), Is.True);
        }

        [Test]
        public void SubmoduleToReadOnly_StalePresentationUrlCannotOverrideAssessedRegistration()
        {
            PackageManagerSubmoduleInfo installed = CreateInstalledSubmodule();
            string staleUrl = new Uri(
                    remoteWorkRoot + Path.DirectorySeparatorChar)
                .AbsoluteUri.TrimEnd('/');
            var staleInfo = new PackageManagerSubmoduleInfo(
                installed.PackageName,
                installed.PackagePath,
                installed.FullPackagePath,
                staleUrl,
                false);
            byte[] manifestBefore = File.ReadAllBytes(
                Path.Combine(projectRoot, "Packages", "manifest.json"));
            Assert.That(
                GitUtility.TryAssessSubmoduleRemoval(
                    PackagePath,
                    out SubmoduleRemovalAssessment confirmed,
                    out string assessmentError),
                Is.True,
                assessmentError);
            Assert.That(
                GitUtility.AreRepositoryUrlsEquivalent(
                    confirmed.RepositoryUrl,
                    staleInfo.RepositoryUrl),
                Is.False);

            var state = new GitPackageConversionTaskState();
            CommandResult result = GitPackageConversionService.RunToReadOnlyTask(
                staleInfo,
                confirmed,
                false,
                state,
                CancellationToken.None);

            Assert.That(result.IsSuccess, Is.False);
            Assert.That(state.Message, Does.Contain("no longer matches"));
            CollectionAssert.AreEqual(
                manifestBefore,
                File.ReadAllBytes(
                    Path.Combine(projectRoot, "Packages", "manifest.json")));
            Assert.That(
                Directory.Exists(Path.Combine(projectRoot, PackagePath)),
                Is.True);
        }

        [Test]
        public void SubmoduleToReadOnly_RelativeRegistrationUsesVerifiedResolvedUrl()
        {
            PackageManagerSubmoduleInfo installed = CreateInstalledSubmodule();
            const string relativeUrl = "../Remote.git";
            RequireGit(
                projectRoot,
                "config --file .gitmodules " +
                "submodule.\"" + PackagePath + "\".url " +
                GitUtility.Quote(relativeUrl));
            RequireGit(projectRoot, "add -- .gitmodules");
            var relativeInfo = new PackageManagerSubmoduleInfo(
                installed.PackageName,
                installed.PackagePath,
                installed.FullPackagePath,
                relativeUrl,
                false);
            Assert.That(
                GitUtility.TryAssessSubmoduleRemoval(
                    PackagePath,
                    out SubmoduleRemovalAssessment confirmed,
                    out string assessmentError),
                Is.True,
                assessmentError);
            Assert.That(confirmed.HasGitModulesTargetChanges, Is.True);
            Assert.That(confirmed.RepositoryUrl, Is.EqualTo(relativeUrl));
            Assert.That(
                GitUtility.AreRepositoryUrlsEquivalent(
                    confirmed.ResolvedRepositoryUrl,
                    remoteUrl),
                Is.True);

            var state = new GitPackageConversionTaskState();
            CommandResult result = GitPackageConversionService.RunToReadOnlyTask(
                relativeInfo,
                confirmed,
                true,
                state,
                CancellationToken.None);

            Assert.That(result.IsSuccess, Is.True, result.StdErr);
            Assert.That(state.ConvertedSuccessfully, Is.True);
            Assert.That(
                PackageManifestGitDependencyStore.TryGetProjectDependency(
                    PackageName,
                    out PackageManifestGitDependency dependency,
                    out string dependencyError),
                Is.True,
                dependencyError);
            Assert.That(dependency.Spec, Is.EqualTo(remoteUrl + "#" + remoteCommit));
        }

        [Test]
        public void RepositoryFetchProbe_UnconfirmedFetchPreservesExactProbeForRecovery()
        {
            CliCommandRunner.CurrentRunner = new UnconfirmedFetchRunner(
                previousRunner);
            try
            {
                bool fetched = GitUtility.TryVerifyRepositoryCommitFetchable(
                    remoteUrl,
                    remoteCommit,
                    out string error,
                    CancellationToken.None);

                Assert.That(fetched, Is.False);
                Assert.That(error, Does.Contain("preserved under Library"));
                string probesRoot = Path.Combine(
                    projectRoot,
                    "Library",
                    "GitSubmoduleManager",
                    "FetchProbes");
                Assert.That(Directory.Exists(probesRoot), Is.True);
                Assert.That(
                    Directory.GetDirectories(probesRoot, "*.git"),
                    Has.Length.EqualTo(1));
            }
            finally
            {
                Assert.That(
                    GitUtility.ConsumeUnconfirmedCommandTermination(),
                    Is.True);
            }
        }

        [Test]
        public void SubmoduleToReadOnly_ChangedConfirmedStateLeavesManifestUntouched()
        {
            PackageManagerSubmoduleInfo info = CreateInstalledSubmodule();
            string packageRoot = Path.Combine(projectRoot, PackagePath);
            File.AppendAllText(
                Path.Combine(packageRoot, "package.json"),
                "\n// state shown to the user\n");
            Assert.That(
                GitUtility.TryAssessSubmoduleRemoval(
                    PackagePath,
                    out SubmoduleRemovalAssessment confirmed,
                    out string assessmentError),
                Is.True,
                assessmentError);
            File.WriteAllText(
                Path.Combine(packageRoot, "created-after-confirmation.txt"),
                "late work\n");
            byte[] manifestBefore = File.ReadAllBytes(
                Path.Combine(projectRoot, "Packages", "manifest.json"));

            var state = new GitPackageConversionTaskState();
            CommandResult result = GitPackageConversionService.RunToReadOnlyTask(
                info,
                confirmed,
                true,
                state,
                CancellationToken.None);

            Assert.That(result.IsSuccess, Is.False);
            Assert.That(state.Message, Does.Contain("changed after the conversion warning"));
            CollectionAssert.AreEqual(
                manifestBefore,
                File.ReadAllBytes(
                    Path.Combine(projectRoot, "Packages", "manifest.json")));
            Assert.That(
                File.Exists(
                    Path.Combine(packageRoot, "created-after-confirmation.txt")),
                Is.True);
        }

        [Test]
        public void SubmoduleToReadOnly_RemovalRaceRollsBackTemporaryManifestDependency()
        {
            PackageManagerSubmoduleInfo info = CreateInstalledSubmodule();
            byte[] manifestBefore = File.ReadAllBytes(
                Path.Combine(projectRoot, "Packages", "manifest.json"));
            Assert.That(
                GitUtility.TryAssessSubmoduleRemoval(
                    PackagePath,
                    out SubmoduleRemovalAssessment confirmed,
                    out string assessmentError),
                Is.True,
                assessmentError);
            string lateFile = Path.Combine(
                projectRoot,
                PackagePath,
                "created-during-removal.txt");
            CliCommandRunner.CurrentRunner = new SecondParentStatusMutationRunner(
                previousRunner,
                () => File.WriteAllText(lateFile, "late work\n"));

            var state = new GitPackageConversionTaskState();
            CommandResult result = GitPackageConversionService.RunToReadOnlyTask(
                info,
                confirmed,
                false,
                state,
                CancellationToken.None);

            Assert.That(result.IsSuccess, Is.False);
            Assert.That(state.Message, Does.Contain("temporary read-only dependency was rolled back"));
            CollectionAssert.AreEqual(
                manifestBefore,
                File.ReadAllBytes(
                    Path.Combine(projectRoot, "Packages", "manifest.json")));
            Assert.That(Directory.Exists(Path.Combine(projectRoot, PackagePath)), Is.True);
            Assert.That(File.Exists(lateFile), Is.True);
        }

        [Test]
        public void SubmoduleToReadOnly_UnsafePostRemovalFailureRetainsPinnedManifestTarget()
        {
            PackageManagerSubmoduleInfo info = CreateInstalledSubmodule();
            Assert.That(
                GitUtility.TryAssessSubmoduleRemoval(
                    PackagePath,
                    out SubmoduleRemovalAssessment confirmed,
                    out string assessmentError),
                Is.True,
                assessmentError);
            string removedWorktree = Path.Combine(projectRoot, PackagePath);
            CliCommandRunner.CurrentRunner = new SuccessfulGitRmMutationRunner(
                previousRunner,
                () => Directory.CreateDirectory(removedWorktree));

            var state = new GitPackageConversionTaskState();
            CommandResult result = GitPackageConversionService.RunToReadOnlyTask(
                info,
                confirmed,
                false,
                state,
                CancellationToken.None);

            Assert.That(result.IsSuccess, Is.False);
            Assert.That(state.Outcome, Is.EqualTo(
                GitOperationCompletionOutcome.FailedUnsafe));
            Assert.That(state.Message, Does.Contain("pinned read-only dependency was retained"));
            Assert.That(
                PackageManifestGitDependencyStore.TryGetProjectDependency(
                    PackageName,
                    out PackageManifestGitDependency dependency,
                    out string dependencyError),
                Is.True,
                dependencyError);
            Assert.That(dependency.Spec, Is.EqualTo(remoteUrl + "#" + remoteCommit));
            Assert.That(
                RunGit(projectRoot, $"ls-files --error-unmatch -- {PackagePath}")
                    .ExitCode,
                Is.EqualTo(1));
        }

        [Test]
        public void SubmoduleToReadOnly_PostRemovalManifestDeletionIsSafelyRepaired()
        {
            PackageManagerSubmoduleInfo info = CreateInstalledSubmodule();
            Assert.That(
                GitUtility.TryAssessSubmoduleRemoval(
                    PackagePath,
                    out SubmoduleRemovalAssessment confirmed,
                    out string assessmentError),
                Is.True,
                assessmentError);
            string spec = remoteUrl + "#" + remoteCommit;
            bool removedPinnedTarget = false;
            string targetRemovalError = string.Empty;
            CliCommandRunner.CurrentRunner = new SuccessfulGitRmMutationRunner(
                previousRunner,
                () => removedPinnedTarget =
                    PackageManifestGitDependencyStore.TryRemoveDependency(
                        PackageName,
                        spec,
                        out _,
                        out targetRemovalError));

            var state = new GitPackageConversionTaskState();
            CommandResult result = GitPackageConversionService.RunToReadOnlyTask(
                info,
                confirmed,
                false,
                state,
                CancellationToken.None);

            Assert.That(removedPinnedTarget, Is.True, targetRemovalError);
            Assert.That(result.IsSuccess, Is.True, result.StdErr);
            Assert.That(state.Message, Does.Contain("safely restored after removal"));
            Assert.That(
                PackageManifestGitDependencyStore.TryGetProjectDependency(
                    PackageName,
                    out PackageManifestGitDependency dependency,
                    out string dependencyError),
                Is.True,
                dependencyError);
            Assert.That(dependency.Spec, Is.EqualTo(spec));
        }

        [Test]
        public void SubmoduleToReadOnly_PostRemovalDifferentDependencyIsNotOverwritten()
        {
            PackageManagerSubmoduleInfo info = CreateInstalledSubmodule();
            Assert.That(
                GitUtility.TryAssessSubmoduleRemoval(
                    PackagePath,
                    out SubmoduleRemovalAssessment confirmed,
                    out string assessmentError),
                Is.True,
                assessmentError);
            string expectedSpec = remoteUrl + "#" + remoteCommit;
            string concurrentSpec = remoteUrl + "#main";
            bool concurrentEditApplied = false;
            string concurrentEditError = string.Empty;
            CliCommandRunner.CurrentRunner = new SuccessfulGitRmMutationRunner(
                previousRunner,
                () =>
                {
                    if (!PackageManifestGitDependencyStore.TryRemoveDependency(
                            PackageName,
                            expectedSpec,
                            out _,
                            out concurrentEditError))
                    {
                        return;
                    }

                    concurrentEditApplied =
                        PackageManifestGitDependencyStore.TryAddDependency(
                            PackageName,
                            concurrentSpec,
                            out _,
                            out concurrentEditError);
                });

            var state = new GitPackageConversionTaskState();
            CommandResult result = GitPackageConversionService.RunToReadOnlyTask(
                info,
                confirmed,
                false,
                state,
                CancellationToken.None);

            Assert.That(concurrentEditApplied, Is.True, concurrentEditError);
            Assert.That(result.IsSuccess, Is.False);
            Assert.That(state.Outcome, Is.EqualTo(
                GitOperationCompletionOutcome.FailedUnsafe));
            Assert.That(state.Message, Does.Contain("without overwriting"));
            Assert.That(
                PackageManifestGitDependencyStore.TryGetProjectDependency(
                    PackageName,
                    out PackageManifestGitDependency dependency,
                    out string dependencyError),
                Is.True,
                dependencyError);
            Assert.That(dependency.Spec, Is.EqualTo(concurrentSpec));
        }

        [Test]
        public void Conversion_RefusesToConvertItsOwnRecoveryOwner()
        {
            var readOnly = new PackageManagerReadOnlyGitInfo(
                GitPackageConversionService.ManagerPackageName,
                remoteUrl,
                remoteUrl + "#main",
                "main",
                remoteCommit,
                null);
            var submodule = new PackageManagerSubmoduleInfo(
                GitPackageConversionService.ManagerPackageName,
                "Packages/" + GitPackageConversionService.ManagerPackageName,
                Path.Combine(
                    projectRoot,
                    "Packages",
                    GitPackageConversionService.ManagerPackageName),
                remoteUrl,
                false);

            Assert.That(
                GitPackageConversionService.ValidateToSubmodule(readOnly),
                Does.Contain("cannot convert itself"));
            Assert.That(
                GitPackageConversionService.ValidateToReadOnly(submodule),
                Does.Contain("cannot convert itself"));
        }

        private void CreateRemotePackage()
        {
            remoteWorkRoot = Path.Combine(testRoot, "RemoteWork");
            Directory.CreateDirectory(remoteWorkRoot);
            RequireGit(remoteWorkRoot, "init -b main");
            ConfigureIdentity(remoteWorkRoot);
            File.WriteAllText(
                Path.Combine(remoteWorkRoot, "package.json"),
                "{\n  \"name\": \"" + PackageName + "\",\n  \"version\": \"1.0.0\"\n}\n",
                new UTF8Encoding(false));
            RequireGit(remoteWorkRoot, "add -- package.json");
            RequireGit(remoteWorkRoot, "commit -m initial");
            remoteCommit = RequireGit(remoteWorkRoot, "rev-parse HEAD").StdOut.Trim();
            Directory.CreateDirectory(remoteRoot);
            RequireGit(remoteRoot, "init --bare");
            remoteUrl = new Uri(remoteRoot + Path.DirectorySeparatorChar).AbsoluteUri
                .TrimEnd('/');
            RequireGit(remoteWorkRoot, "remote add origin " + remoteUrl);
            RequireGit(remoteWorkRoot, "push -u origin main");
            RequireGit(remoteRoot, "symbolic-ref HEAD refs/heads/main");
        }

        private void MoveRemoteDefaultBranchPackageIntoSubfolder()
        {
            string nestedDirectory = Path.Combine(remoteWorkRoot, "Nested");
            Directory.CreateDirectory(nestedDirectory);
            File.Move(
                Path.Combine(remoteWorkRoot, "package.json"),
                Path.Combine(nestedDirectory, "package.json"));
            RequireGit(remoteWorkRoot, "add -A -- package.json Nested/package.json");
            RequireGit(remoteWorkRoot, "commit -m move-package-into-subfolder");
            RequireGit(remoteWorkRoot, "push origin main");
        }

        private void InitializeProject()
        {
            RequireGit(projectRoot, "init -b main");
            ConfigureIdentity(projectRoot);
            Directory.CreateDirectory(Path.Combine(projectRoot, "Packages"));
        }

        private PackageManagerSubmoduleInfo CreateInstalledSubmodule()
        {
            WriteManifest(null);
            CommitProject("empty manifest");
            CommandResult add = RunGit(
                projectRoot,
                $"-c protocol.file.allow=always submodule add -b main {remoteUrl} {PackagePath}");
            Assert.That(add.IsSuccess, Is.True, add.StdErr);
            CommitProject("add source submodule");
            return new PackageManagerSubmoduleInfo(
                PackageName,
                PackagePath,
                Path.Combine(projectRoot, PackagePath),
                remoteUrl,
                false);
        }

        private void WriteManifest(string spec)
        {
            string dependency = string.IsNullOrEmpty(spec)
                ? string.Empty
                : "\n    \"" + PackageName + "\": \"" + spec + "\"\n  ";
            File.WriteAllText(
                Path.Combine(projectRoot, "Packages", "manifest.json"),
                "{\n  \"dependencies\": {" + dependency + "}\n}\n",
                new UTF8Encoding(false));
        }

        private void WriteLockfileSentinel(out byte[] contents)
        {
            contents = Encoding.UTF8.GetBytes("{\n  \"lock\": \"unity-owned\"\n}\n");
            File.WriteAllBytes(
                Path.Combine(projectRoot, "Packages", "packages-lock.json"),
                contents);
        }

        private void AssertLockfileUnchanged(byte[] expected)
        {
            CollectionAssert.AreEqual(
                expected,
                File.ReadAllBytes(
                    Path.Combine(projectRoot, "Packages", "packages-lock.json")));
        }

        private void CommitProject(string message)
        {
            RequireGit(projectRoot, "add -- Packages/manifest.json");
            RequireGit(projectRoot, "commit -m " + message.Replace(' ', '-'));
        }

        private static void ConfigureIdentity(string repository)
        {
            RequireGit(repository, "config user.email tests@example.invalid");
            RequireGit(repository, "config user.name Tests");
        }

        private static CommandResult RequireGit(string workingDirectory, string arguments)
        {
            CommandResult result = RunGit(workingDirectory, arguments);
            Assert.That(result, Is.Not.Null);
            Assert.That(result.IsSuccess, Is.True, result.StdErr);
            return result;
        }

        private static CommandResult RunGit(string workingDirectory, string arguments)
        {
            return GitUtility.RunGit(arguments, workingDirectory, 30000);
        }

        private sealed class SecondParentStatusMutationRunner : ICommandRunner
        {
            private readonly ICommandRunner inner;
            private readonly Action mutation;
            private int matchingStatusCount;

            internal SecondParentStatusMutationRunner(
                ICommandRunner inner,
                Action mutation)
            {
                this.inner = inner;
                this.mutation = mutation;
            }

            public CommandResult Run(CommandSpec spec)
            {
                CommandResult result = inner.Run(spec);
                string arguments = spec.Arguments ?? string.Empty;
                if (result != null &&
                    result.IsSuccess &&
                    arguments.StartsWith(
                        "status --porcelain=v2 --untracked-files=all -- ",
                        StringComparison.Ordinal) &&
                    arguments.IndexOf(PackagePath, StringComparison.Ordinal) >= 0 &&
                    ++matchingStatusCount == 2)
                {
                    mutation();
                }

                return result;
            }
        }

        private sealed class SuccessfulGitRmMutationRunner : ICommandRunner
        {
            private readonly ICommandRunner inner;
            private readonly Action mutation;
            private bool mutated;

            internal SuccessfulGitRmMutationRunner(
                ICommandRunner inner,
                Action mutation)
            {
                this.inner = inner;
                this.mutation = mutation;
            }

            public CommandResult Run(CommandSpec spec)
            {
                CommandResult result = inner.Run(spec);
                if (!mutated &&
                    result != null &&
                    result.IsSuccess &&
                    (spec.Arguments ?? string.Empty).StartsWith(
                        "rm ",
                        StringComparison.Ordinal))
                {
                    mutated = true;
                    mutation();
                }

                return result;
            }
        }

        private sealed class UnconfirmedFetchRunner : ICommandRunner
        {
            private readonly ICommandRunner inner;

            internal UnconfirmedFetchRunner(ICommandRunner inner)
            {
                this.inner = inner;
            }

            public CommandResult Run(CommandSpec spec)
            {
                if ((spec.Arguments ?? string.Empty).IndexOf(
                        " fetch --no-tags --depth=1 ",
                        StringComparison.Ordinal) >= 0)
                {
                    return new CommandResult
                    {
                        ExitCode = -1,
                        StdOut = string.Empty,
                        StdErr = "simulated unconfirmed fetch timeout",
                        TimedOut = true,
                        TerminationConfirmed = false
                    };
                }

                return inner.Run(spec);
            }
        }
    }
}
