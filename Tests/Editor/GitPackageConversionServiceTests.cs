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
        private const string PackageManifestMetaGuid =
            "0123456789abcdef0123456789abcdef";
        private const string ValidPackageManifestMeta =
            "fileFormatVersion: 2\nguid: " + PackageManifestMetaGuid + "\n";

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
        public void ReadOnlyToSubmodule_SymlinkedResolvedManifestUnderCoreSymlinksFalsePreservesSourceAndRollsBackTarget()
        {
            PublishSymlinkedRootPackageManifest();
            string spec = remoteUrl + "#main";
            WriteManifest(spec);
            CommitProject("read-only symlink manifest source");
            string manifestPath = Path.Combine(
                projectRoot,
                "Packages",
                "manifest.json");
            byte[] manifestBefore = File.ReadAllBytes(manifestPath);
            WriteLockfileSentinel(out byte[] lockfileBefore);
            var info = new PackageManagerReadOnlyGitInfo(
                PackageName,
                remoteUrl,
                spec,
                "main",
                remoteCommit,
                null);
            var materializingRunner = new MaterializeManifestSymlinkRunner(
                CliCommandRunner.CurrentRunner,
                projectRoot,
                PackagePath,
                PackageName);
            CliCommandRunner.CurrentRunner = materializingRunner;
            var state = new GitPackageConversionTaskState();

            CommandResult result = GitPackageConversionService.RunToSubmoduleTask(
                info,
                PackagePath,
                state,
                CancellationToken.None);

            Assert.That(result.IsSuccess, Is.False);
            Assert.That(state.ConvertedSuccessfully, Is.False);
            Assert.That(
                state.Outcome,
                Is.EqualTo(GitOperationCompletionOutcome.FailedButRolledBack));
            Assert.That(
                materializingRunner.MaterializedRegularManifest,
                Is.True,
                "The fixture must prove core.symlinks=false exposed the symlink blob as a valid regular-looking worktree manifest.");
            Assert.That(state.Message, Does.Contain("symbolic-link entry"));
            CollectionAssert.AreEqual(
                manifestBefore,
                File.ReadAllBytes(manifestPath));
            Assert.That(
                PackageManifestGitDependencyStore.TryGetProjectDependency(
                    PackageName,
                    out PackageManifestGitDependency preservedDependency,
                    out string dependencyError),
                Is.True,
                dependencyError);
            Assert.That(preservedDependency.Spec, Is.EqualTo(spec));
            Assert.That(
                Directory.Exists(Path.Combine(projectRoot, PackagePath)),
                Is.False,
                "The rejected target submodule must be rolled back.");
            Assert.That(
                File.Exists(Path.Combine(projectRoot, ".gitmodules")),
                Is.False);
            Assert.That(
                RunGit(projectRoot, $"ls-files --error-unmatch -- {PackagePath}")
                    .IsSuccess,
                Is.False);
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
        public void ReadOnlyToSubmodule_RejectsNonCanonicalOrZeroResolvedCommit()
        {
            string[] invalidCommits =
            {
                new string('a', 39),
                new string('a', 41),
                new string('0', 40),
                new string('0', 64)
            };

            foreach (string invalidCommit in invalidCommits)
            {
                var info = new PackageManagerReadOnlyGitInfo(
                    PackageName,
                    remoteUrl,
                    remoteUrl + "#main",
                    "main",
                    invalidCommit,
                    null);

                Assert.That(
                    GitPackageConversionService.ValidateToSubmodule(info),
                    Does.Contain("verifiable commit"),
                    invalidCommit);
            }
        }

        [TestCase("file:///tmp/repository.git?path=/Nested")]
        [TestCase("git@example.com:owner/repository.git?path=/Nested")]
        [TestCase("git@example.com:owner/repository.git#main")]
        public void SubmoduleToReadOnlyValidator_RejectsGitDependencyDelimiters(
            string repositoryUrl)
        {
            PackageManagerSubmoduleInfo installed = CreateInstalledSubmodule();
            var selected = new PackageManagerSubmoduleInfo(
                installed.PackageName,
                installed.PackagePath,
                installed.FullPackagePath,
                repositoryUrl,
                false);

            Assert.That(
                GitPackageConversionService.ValidateToReadOnly(selected),
                Does.Contain("without an embedded query or revision"));
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
        public void SubmoduleToReadOnly_MissingCommittedRootManifestLeavesSourceUntouched()
        {
            PublishRootPackageManifest(
                null,
                "remove-root-package-manifest");
            PackageManagerSubmoduleInfo info = CreateInstalledSubmodule();

            AssertCommittedManifestRejectionLeavesSourceUntouched(
                info,
                "does not contain a valid regular root package.json");
        }

        [Test]
        public void SubmoduleToReadOnly_InvalidCommittedRootManifestLeavesSourceUntouched()
        {
            PublishRootPackageManifest(
                "{\n  \"name\": \"" + PackageName + "\",\n",
                "invalidate-root-package-manifest");
            PackageManagerSubmoduleInfo info = CreateInstalledSubmodule();

            AssertCommittedManifestRejectionLeavesSourceUntouched(
                info,
                "root package.json is not a valid UPM manifest");
        }

        [Test]
        public void SubmoduleToReadOnly_MismatchedCommittedPackageNameLeavesSourceUntouched()
        {
            const string otherPackageName = "com.example.other";
            PublishRootPackageManifest(
                "{\n  \"name\": \"" + otherPackageName +
                "\",\n  \"version\": \"1.0.0\"\n}\n",
                "mismatch-root-package-name");
            PackageManagerSubmoduleInfo info = CreateInstalledSubmodule();

            AssertCommittedManifestRejectionLeavesSourceUntouched(
                info,
                "declares " + otherPackageName);
        }

        [Test]
        public void SubmoduleToReadOnly_MissingCommittedMetaLeavesSourceUntouched()
        {
            PublishRootPackageManifestMeta(
                null,
                "remove-root-package-manifest-meta");
            PackageManagerSubmoduleInfo info = CreateInstalledSubmodule();

            AssertCommittedManifestRejectionLeavesSourceUntouched(
                info,
                "valid regular root package.json.meta");
        }

        [Test]
        public void SubmoduleToReadOnly_SymlinkedCommittedMetaLeavesSourceUntouched()
        {
            PublishSymlinkedRootPackageManifestMeta();
            PackageManagerSubmoduleInfo info = CreateInstalledSubmodule();
            MaterializeTrackedMetaSymlinkAsRegularFile();

            AssertCommittedManifestRejectionLeavesSourceUntouched(
                info,
                "valid regular root package.json.meta");
        }

        [Test]
        public void SubmoduleToReadOnly_InvalidUtf8CommittedMetaLeavesSourceUntouched()
        {
            PublishRootPackageManifestMeta(
                new byte[] { 0xff, 0xfe, 0xfd },
                "invalidate-root-package-manifest-meta-utf8");
            PackageManagerSubmoduleInfo info = CreateInstalledSubmodule();

            AssertCommittedManifestRejectionLeavesSourceUntouched(
                info,
                "must contain valid UTF-8 text");
        }

        [Test]
        public void SubmoduleToReadOnly_OversizedCommittedMetaLeavesSourceUntouched()
        {
            PublishRootPackageManifestMeta(
                Encoding.UTF8.GetBytes(
                    ValidPackageManifestMeta + new string('x', 17 * 1024)),
                "oversize-root-package-manifest-meta");
            PackageManagerSubmoduleInfo info = CreateInstalledSubmodule();

            AssertCommittedManifestRejectionLeavesSourceUntouched(
                info,
                "exceeds the 16 KiB validation limit");
        }

        [Test]
        public void SubmoduleToReadOnly_MalformedCommittedMetaLeavesSourceUntouched()
        {
            PublishRootPackageManifestMeta(
                Encoding.UTF8.GetBytes(
                    "fileFormatVersion: 2\nguid: not-a-guid\n"),
                "malform-root-package-manifest-meta");
            PackageManagerSubmoduleInfo info = CreateInstalledSubmodule();

            AssertCommittedManifestRejectionLeavesSourceUntouched(
                info,
                "not a valid Unity package marker");
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
        public void SubmoduleToReadOnly_PublishedCommitIsAllowedDespiteStaleRemoteTrackingRefs()
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

            var publicationQueries = new PublicationQueryCountingRunner(
                CliCommandRunner.CurrentRunner);
            CliCommandRunner.CurrentRunner = publicationQueries;
            var state = new GitPackageConversionTaskState();
            CommandResult result = GitPackageConversionService.RunToReadOnlyTask(
                info,
                confirmed,
                true,
                state,
                CancellationToken.None);

            Assert.That(result.IsSuccess, Is.True, result.StdErr);
            Assert.That(
                publicationQueries.AdvertisementCount,
                Is.EqualTo(1),
                "Conversion must pass its exact publication proof into removal instead of repeating the advertisement query.");
            Assert.That(
                publicationQueries.NegotiationCount,
                Is.EqualTo(0),
                "An exact advertised HEAD must not require a second protocol negotiation.");
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
        public void SubmoduleToReadOnly_UnpublishedCommitIsBlockedDespiteLocalRemoteTrackingRef()
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
            Assert.That(
                state.Message,
                Does.Contain("could not be proven reachable"));
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
        public void RepositoryPublicationProof_TruncatedRemoteAdvertisementFailsClosed()
        {
            CliCommandRunner.CurrentRunner = new TruncatedPublicationQueryRunner(
                previousRunner);
            CreateInstalledSubmodule();

            bool published = GitUtility.TryVerifyRepositoryCommitPublished(
                PackagePath,
                remoteUrl,
                remoteCommit,
                out GitUtility.RepositoryCommitPublicationProof proof,
                out string error,
                CancellationToken.None);

            Assert.That(published, Is.False);
            Assert.That(proof, Is.Null);
            Assert.That(error, Does.Contain("truncated Git output"));
        }

        [Test]
        public void RepositoryPublicationProof_CustomSubmoduleSectionUsesActualWorktreeGitDirectory()
        {
            const string customSectionName = "custom-conversion-registration";
            WriteManifest(null);
            CommitProject("empty manifest");
            CommandResult add = RunGit(
                projectRoot,
                "-c protocol.file.allow=always submodule add --name " +
                GitUtility.Quote(customSectionName) + " -b main " +
                GitUtility.Quote(remoteUrl) + " " +
                GitUtility.Quote(PackagePath));
            Assert.That(add.IsSuccess, Is.True, add.StdErr);
            CommitProject("add custom-named source submodule");
            string packageRoot = Path.Combine(projectRoot, PackagePath);
            string actualGitDir = RequireGit(
                    packageRoot,
                    "rev-parse --absolute-git-dir")
                .StdOut.TrimEnd('\r', '\n');
            Assert.That(
                actualGitDir,
                Does.Contain(customSectionName),
                "The fixture must use metadata named independently from the package path.");

            bool published = GitUtility.TryVerifyRepositoryCommitPublished(
                PackagePath,
                remoteUrl,
                remoteCommit,
                out GitUtility.RepositoryCommitPublicationProof proof,
                out string error,
                CancellationToken.None);

            Assert.That(published, Is.True, error);
            Assert.That(proof, Is.Not.Null);
        }

        [Test]
        public void RepositoryPublicationProof_ExactAdvertisedTipSkipsUnsupportedNegotiation()
        {
            CreateInstalledSubmodule();
            var negotiationRunner = new FailingNegotiationRunner(
                CliCommandRunner.CurrentRunner);
            CliCommandRunner.CurrentRunner = negotiationRunner;

            bool published = GitUtility.TryVerifyRepositoryCommitPublished(
                PackagePath,
                remoteUrl,
                remoteCommit,
                out GitUtility.RepositoryCommitPublicationProof proof,
                out string error,
                CancellationToken.None);

            Assert.That(published, Is.True, error);
            Assert.That(proof, Is.Not.Null);
            Assert.That(
                negotiationRunner.AttemptCount,
                Is.EqualTo(0),
                "The runner would fail every negotiate-only call; exact advertised tips must use the advertisement directly.");
        }

        [Test]
        public void RepositoryPublicationProof_ReplaceRefCannotAuthorizeUnpublishedHead()
        {
            CreateInstalledSubmodule();
            string packageRoot = Path.Combine(projectRoot, PackagePath);
            ConfigureIdentity(packageRoot);
            File.AppendAllText(Path.Combine(packageRoot, "package.json"), "\n");
            RequireGit(packageRoot, "add -- package.json");
            RequireGit(packageRoot, "commit -m unpublished-replace-ref-head");
            string unpublishedHead = RequireGit(packageRoot, "rev-parse HEAD")
                .StdOut.Trim();
            string tree = RequireGit(packageRoot, "rev-parse HEAD^{tree}")
                .StdOut.Trim();
            string replacementCommit = RequireGit(
                    packageRoot,
                    $"commit-tree {tree} -p {unpublishedHead} -m replacement-ancestry")
                .StdOut.Trim();
            RequireGit(
                packageRoot,
                $"replace {remoteCommit} {replacementCommit}");

            CommandResult unprotectedContainment = RequireGit(
                packageRoot,
                $"for-each-ref --contains={unpublishedHead} " +
                $"--format={GitUtility.Quote("%(objectname)")} refs/remotes/origin/main");
            Assert.That(
                unprotectedContainment.StdOut,
                Does.Contain(remoteCommit),
                "The fixture must demonstrate that replacement ancestry can falsify an unprotected containment query.");

            bool published = GitUtility.TryVerifyRepositoryCommitPublished(
                PackagePath,
                remoteUrl,
                unpublishedHead,
                out GitUtility.RepositoryCommitPublicationProof proof,
                out string error,
                CancellationToken.None);

            Assert.That(published, Is.False);
            Assert.That(proof, Is.Null);
            Assert.That(error, Does.Contain("could not be proven reachable"));
        }

        [Test]
        public void RepositoryPublicationProof_GraftCannotAuthorizeUnpublishedHead()
        {
            CreateInstalledSubmodule();
            string packageRoot = Path.Combine(projectRoot, PackagePath);
            ConfigureIdentity(packageRoot);
            File.AppendAllText(Path.Combine(packageRoot, "package.json"), "\n");
            RequireGit(packageRoot, "add -- package.json");
            RequireGit(packageRoot, "commit -m unpublished-grafted-head");
            string unpublishedHead = RequireGit(packageRoot, "rev-parse HEAD")
                .StdOut.Trim();
            string graftPathValue = RequireGit(
                    packageRoot,
                    "rev-parse --git-path info/grafts")
                .StdOut.TrimEnd('\r', '\n');
            string graftPath = Path.GetFullPath(
                Path.IsPathRooted(graftPathValue)
                    ? graftPathValue
                    : Path.Combine(packageRoot, graftPathValue));
            Directory.CreateDirectory(Path.GetDirectoryName(graftPath));
            File.WriteAllText(
                graftPath,
                remoteCommit + " " + unpublishedHead + "\n",
                new UTF8Encoding(false));

            CommandResult unprotectedContainment = RequireGit(
                packageRoot,
                $"for-each-ref --contains={unpublishedHead} " +
                $"--format={GitUtility.Quote("%(objectname)")} refs/remotes/origin/main");
            Assert.That(
                unprotectedContainment.StdOut,
                Does.Contain(remoteCommit),
                "The fixture must demonstrate that legacy graft ancestry can falsify an unprotected containment query.");

            bool published = GitUtility.TryVerifyRepositoryCommitPublished(
                PackagePath,
                remoteUrl,
                unpublishedHead,
                out GitUtility.RepositoryCommitPublicationProof proof,
                out string error,
                CancellationToken.None);

            Assert.That(published, Is.False);
            Assert.That(proof, Is.Null);
            Assert.That(error, Does.Contain(".git/info/grafts"));
        }

        [Test]
        public void RepositoryPublicationProof_ShallowRemoteTipCannotProveMissingAncestorWithoutLocalMutation()
        {
            CreateInstalledSubmodule();
            string packageRoot = Path.Combine(projectRoot, PackagePath);
            ConfigureIdentity(remoteWorkRoot);
            RequireGit(remoteWorkRoot, "commit --allow-empty -m published-ancestor");
            string ancestorCommit = RequireGit(remoteWorkRoot, "rev-parse HEAD")
                .StdOut.Trim();
            RequireGit(remoteWorkRoot, "commit --allow-empty -m shallow-tip");
            string shallowTip = RequireGit(remoteWorkRoot, "rev-parse HEAD")
                .StdOut.Trim();
            RequireGit(remoteWorkRoot, "push origin main");
            RequireGit(
                packageRoot,
                "fetch origin +main:refs/remotes/origin/main");
            RequireGit(packageRoot, $"checkout --detach {ancestorCommit}");

            CommandResult localContainment = RequireGit(
                packageRoot,
                $"for-each-ref --contains={ancestorCommit} " +
                $"--format={GitUtility.Quote("%(objectname)")} refs/remotes/origin/main");
            Assert.That(localContainment.StdOut, Does.Contain(shallowTip));

            string shallowRemoteRoot = Path.Combine(testRoot, "ShallowRemote.git");
            string shallowRemoteUrl = new Uri(
                    shallowRemoteRoot + Path.DirectorySeparatorChar)
                .AbsoluteUri.TrimEnd('/');
            RequireGit(
                testRoot,
                "-c protocol.file.allow=always clone --bare --depth=1 " +
                GitUtility.Quote(remoteUrl) + " " +
                GitUtility.Quote(shallowRemoteRoot));

            string refsBefore = RequireGit(
                packageRoot,
                $"for-each-ref --format={GitUtility.Quote("%(refname)%09%(objectname)")}")
                .StdOut;
            string objectsBefore = RequireGit(
                packageRoot,
                $"cat-file --batch-all-objects --batch-check={GitUtility.Quote("%(objectname)")}")
                .StdOut;
            string headBefore = RequireGit(packageRoot, "rev-parse HEAD").StdOut;
            string statusBefore = RequireGit(
                packageRoot,
                "status --porcelain=v2 --untracked-files=all").StdOut;
            string fetchHeadPathValue = RequireGit(
                    packageRoot,
                    "rev-parse --git-path FETCH_HEAD")
                .StdOut.TrimEnd('\r', '\n');
            string fetchHeadPath = Path.GetFullPath(
                Path.IsPathRooted(fetchHeadPathValue)
                    ? fetchHeadPathValue
                    : Path.Combine(packageRoot, fetchHeadPathValue));
            bool fetchHeadExisted = File.Exists(fetchHeadPath);
            byte[] fetchHeadBefore = fetchHeadExisted
                ? File.ReadAllBytes(fetchHeadPath)
                : Array.Empty<byte>();
            var publicationQueries = new PublicationQueryCountingRunner(
                CliCommandRunner.CurrentRunner);
            CliCommandRunner.CurrentRunner = publicationQueries;

            bool published = GitUtility.TryVerifyRepositoryCommitPublished(
                PackagePath,
                shallowRemoteUrl,
                ancestorCommit,
                out GitUtility.RepositoryCommitPublicationProof proof,
                out string error,
                CancellationToken.None);

            Assert.That(published, Is.False);
            Assert.That(proof, Is.Null);
            Assert.That(error, Does.Contain("shallow history"));
            Assert.That(publicationQueries.AdvertisementCount, Is.EqualTo(1));
            Assert.That(
                publicationQueries.NegotiationCount,
                Is.EqualTo(1),
                "An advertised descendant still requires exact no-download presence negotiation.");
            Assert.That(
                RequireGit(
                    packageRoot,
                    $"for-each-ref --format={GitUtility.Quote("%(refname)%09%(objectname)")}").StdOut,
                Is.EqualTo(refsBefore));
            Assert.That(
                RequireGit(
                    packageRoot,
                    $"cat-file --batch-all-objects --batch-check={GitUtility.Quote("%(objectname)")}").StdOut,
                Is.EqualTo(objectsBefore),
                "No object or pack may be downloaded by the exact-presence negotiation.");
            Assert.That(RequireGit(packageRoot, "rev-parse HEAD").StdOut, Is.EqualTo(headBefore));
            Assert.That(
                RequireGit(packageRoot, "status --porcelain=v2 --untracked-files=all").StdOut,
                Is.EqualTo(statusBefore));
            Assert.That(File.Exists(fetchHeadPath), Is.EqualTo(fetchHeadExisted));
            if (fetchHeadExisted)
            {
                Assert.That(
                    File.ReadAllBytes(fetchHeadPath),
                    Is.EqualTo(fetchHeadBefore),
                    "The no-download proof must not rewrite FETCH_HEAD.");
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
            var state = new GitPackageConversionTaskState();
            CommandResult result;
            using (GitUtility.OverrideAfterExactSubmoduleRemovalForTests(
                       ignoredPath => Directory.CreateDirectory(removedWorktree)))
            {
                result = GitPackageConversionService.RunToReadOnlyTask(
                    info,
                    confirmed,
                    false,
                    state,
                    CancellationToken.None);
            }

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
            var state = new GitPackageConversionTaskState();
            CommandResult result;
            using (GitUtility.OverrideAfterExactSubmoduleRemovalForTests(
                       ignoredPath => removedPinnedTarget =
                           PackageManifestGitDependencyStore.TryRemoveDependency(
                               PackageName,
                               spec,
                               out _,
                               out targetRemovalError)))
            {
                result = GitPackageConversionService.RunToReadOnlyTask(
                    info,
                    confirmed,
                    false,
                    state,
                    CancellationToken.None);
            }

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
            var state = new GitPackageConversionTaskState();
            CommandResult result;
            using (GitUtility.OverrideAfterExactSubmoduleRemovalForTests(
                       ignoredPath =>
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
                       }))
            {
                result = GitPackageConversionService.RunToReadOnlyTask(
                    info,
                    confirmed,
                    false,
                    state,
                    CancellationToken.None);
            }

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
            File.WriteAllText(
                Path.Combine(remoteWorkRoot, "package.json.meta"),
                ValidPackageManifestMeta,
                new UTF8Encoding(false));
            RequireGit(
                remoteWorkRoot,
                "add -- package.json package.json.meta");
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
            File.Move(
                Path.Combine(remoteWorkRoot, "package.json.meta"),
                Path.Combine(nestedDirectory, "package.json.meta"));
            RequireGit(
                remoteWorkRoot,
                "add -A -- package.json package.json.meta Nested/package.json Nested/package.json.meta");
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

        private void PublishRootPackageManifest(
            string contents,
            string commitMessage)
        {
            string manifestPath = Path.Combine(remoteWorkRoot, "package.json");
            if (contents == null)
                File.Delete(manifestPath);
            else
                File.WriteAllText(manifestPath, contents, new UTF8Encoding(false));

            RequireGit(remoteWorkRoot, "add -A -- package.json");
            RequireGit(remoteWorkRoot, "commit -m " + commitMessage);
            remoteCommit = RequireGit(remoteWorkRoot, "rev-parse HEAD")
                .StdOut.Trim();
            RequireGit(remoteWorkRoot, "push origin main");
        }

        private void PublishSymlinkedRootPackageManifest()
        {
            string objectId = RequireGit(
                    remoteWorkRoot,
                    "hash-object -w -- package.json")
                .StdOut.Trim();
            RequireGit(
                remoteWorkRoot,
                "update-index --add --cacheinfo 120000," + objectId +
                ",package.json");
            RequireGit(remoteWorkRoot, "commit -m symlink-root-package-manifest");
            remoteCommit = RequireGit(remoteWorkRoot, "rev-parse HEAD")
                .StdOut.Trim();
            RequireGit(remoteWorkRoot, "push origin main");
        }

        private void PublishRootPackageManifestMeta(
            byte[] contents,
            string commitMessage)
        {
            string metaPath = Path.Combine(remoteWorkRoot, "package.json.meta");
            if (contents == null)
                File.Delete(metaPath);
            else
                File.WriteAllBytes(metaPath, contents);

            RequireGit(remoteWorkRoot, "add -A -- package.json.meta");
            RequireGit(remoteWorkRoot, "commit -m " + commitMessage);
            remoteCommit = RequireGit(remoteWorkRoot, "rev-parse HEAD")
                .StdOut.Trim();
            RequireGit(remoteWorkRoot, "push origin main");
        }

        private void PublishSymlinkedRootPackageManifestMeta()
        {
            string metaPath = Path.Combine(remoteWorkRoot, "package.json.meta");
            File.WriteAllText(
                metaPath,
                "package-manifest-meta-target",
                new UTF8Encoding(false));
            string objectId = RequireGit(
                    remoteWorkRoot,
                    "hash-object -w package.json.meta")
                .StdOut.Trim();
            RequireGit(
                remoteWorkRoot,
                "update-index --add --cacheinfo 120000," + objectId +
                ",package.json.meta");
            RequireGit(remoteWorkRoot, "commit -m symlink-root-package-manifest-meta");
            remoteCommit = RequireGit(remoteWorkRoot, "rev-parse HEAD")
                .StdOut.Trim();
            RequireGit(remoteWorkRoot, "push origin main");
        }

        private void MaterializeTrackedMetaSymlinkAsRegularFile()
        {
            string packageRoot = Path.Combine(projectRoot, PackagePath);
            string metaPath = Path.Combine(packageRoot, "package.json.meta");
            RequireGit(packageRoot, "config core.symlinks false");
            File.Delete(metaPath);
            RequireGit(packageRoot, "checkout -- package.json.meta");
            Assert.That(
                File.GetAttributes(metaPath) & FileAttributes.ReparsePoint,
                Is.EqualTo((FileAttributes)0));
        }

        private void AssertCommittedManifestRejectionLeavesSourceUntouched(
            PackageManagerSubmoduleInfo info,
            string expectedMessage)
        {
            string manifestPath = Path.Combine(
                projectRoot,
                "Packages",
                "manifest.json");
            byte[] manifestBefore = File.ReadAllBytes(manifestPath);
            WriteLockfileSentinel(out byte[] lockfileBefore);
            var state = new GitPackageConversionTaskState();

            CommandResult result = GitPackageConversionService.RunToReadOnlyTask(
                info,
                state,
                CancellationToken.None);

            Assert.That(result.IsSuccess, Is.False);
            Assert.That(state.ConvertedSuccessfully, Is.False);
            Assert.That(
                state.Outcome,
                Is.EqualTo(GitOperationCompletionOutcome.FailedButRolledBack));
            Assert.That(state.Message, Does.Contain(expectedMessage));
            CollectionAssert.AreEqual(
                manifestBefore,
                File.ReadAllBytes(manifestPath));
            Assert.That(
                Directory.Exists(Path.Combine(projectRoot, PackagePath)),
                Is.True);
            Assert.That(
                RunGit(projectRoot, $"ls-files --stage -- {PackagePath}")
                    .StdOut,
                Does.StartWith("160000 "));
            AssertLockfileUnchanged(lockfileBefore);
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

        private sealed class MaterializeManifestSymlinkRunner : ICommandRunner
        {
            private readonly ICommandRunner inner;
            private readonly string projectRoot;
            private readonly string packagePath;
            private readonly string expectedPackageName;
            private bool inspectedAdd;

            internal MaterializeManifestSymlinkRunner(
                ICommandRunner inner,
                string projectRoot,
                string packagePath,
                string expectedPackageName)
            {
                this.inner = inner;
                this.projectRoot = projectRoot;
                this.packagePath = packagePath;
                this.expectedPackageName = expectedPackageName;
            }

            internal bool MaterializedRegularManifest { get; private set; }

            public CommandResult Run(CommandSpec spec)
            {
                CommandResult result = inner.Run(spec);
                string arguments = spec?.Arguments ?? string.Empty;
                if (inspectedAdd || result == null || !result.IsSuccess ||
                    arguments.IndexOf(
                        "submodule add",
                        StringComparison.Ordinal) < 0)
                {
                    return result;
                }

                inspectedAdd = true;
                string packageRoot = Path.Combine(projectRoot, packagePath);
                CommandResult configResult = RunInnerGit(
                    packageRoot,
                    "config",
                    "core.symlinks",
                    "false");
                Assert.That(configResult.IsSuccess, Is.True, configResult.StdErr);

                string manifestPath = Path.Combine(packageRoot, "package.json");
                File.Delete(manifestPath);
                CommandResult checkoutResult = RunInnerGit(
                    packageRoot,
                    "checkout",
                    "--",
                    "package.json");
                Assert.That(checkoutResult.IsSuccess, Is.True, checkoutResult.StdErr);

                MaterializedRegularManifest =
                    File.Exists(manifestPath) &&
                    (File.GetAttributes(manifestPath) &
                     FileAttributes.ReparsePoint) == 0 &&
                    GitUtility.TryReadValidPackageManifest(
                        manifestPath,
                        out string declaredName,
                        out _) &&
                    string.Equals(
                        declaredName,
                        expectedPackageName,
                        StringComparison.Ordinal);
                return result;
            }

            private CommandResult RunInnerGit(
                string workingDirectory,
                params string[] arguments)
            {
                return inner.Run(new CommandSpec
                {
                    FileName = GitUtility.GitExecutable,
                    ArgumentList = arguments,
                    WorkingDirectory = workingDirectory,
                    TimeoutMs = 30000,
                    TerminationScope = CommandTerminationScope.CompleteProcessTree
                });
            }
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

        private sealed class PublicationQueryCountingRunner : ICommandRunner
        {
            private readonly ICommandRunner inner;

            internal PublicationQueryCountingRunner(ICommandRunner inner)
            {
                this.inner = inner;
            }

            internal int AdvertisementCount { get; private set; }
            internal int NegotiationCount { get; private set; }

            public CommandResult Run(CommandSpec spec)
            {
                if ((spec.Arguments ?? string.Empty).IndexOf(
                        "ls-remote --heads --tags ",
                        StringComparison.Ordinal) >= 0)
                {
                    AdvertisementCount++;
                }

                if ((spec.Arguments ?? string.Empty).IndexOf(
                        "fetch --no-write-fetch-head --no-recurse-submodules --negotiate-only ",
                        StringComparison.Ordinal) >= 0)
                {
                    NegotiationCount++;
                }

                return inner.Run(spec);
            }
        }

        private sealed class FailingNegotiationRunner : ICommandRunner
        {
            private readonly ICommandRunner inner;

            internal FailingNegotiationRunner(ICommandRunner inner)
            {
                this.inner = inner;
            }

            internal int AttemptCount { get; private set; }

            public CommandResult Run(CommandSpec spec)
            {
                if ((spec.Arguments ?? string.Empty).IndexOf(
                        "fetch --no-write-fetch-head --no-recurse-submodules --negotiate-only ",
                        StringComparison.Ordinal) >= 0)
                {
                    AttemptCount++;
                    return new CommandResult
                    {
                        ExitCode = 1,
                        StdOut = string.Empty,
                        StdErr = "negotiate-only is unsupported by this test server",
                        TerminationConfirmed = true
                    };
                }

                return inner.Run(spec);
            }
        }

        private sealed class TruncatedPublicationQueryRunner : ICommandRunner
        {
            private readonly ICommandRunner inner;

            internal TruncatedPublicationQueryRunner(ICommandRunner inner)
            {
                this.inner = inner;
            }

            public CommandResult Run(CommandSpec spec)
            {
                if ((spec.Arguments ?? string.Empty).IndexOf(
                        "ls-remote --heads --tags ",
                        StringComparison.Ordinal) >= 0)
                {
                    return new CommandResult
                    {
                        ExitCode = 0,
                        StdOut = RemoteCommitForTruncatedOutput +
                                 "\trefs/heads/main\n",
                        StdErr = string.Empty,
                        StdOutTruncated = true,
                        TerminationConfirmed = true
                    };
                }

                return inner.Run(spec);
            }

            private const string RemoteCommitForTruncatedOutput =
                "0123456789abcdef0123456789abcdef01234567";
        }
    }
}
