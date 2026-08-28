using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using NUnit.Framework;

namespace MartinCalander.GitSubmoduleManager.Editor.Tests
{
    [Parallelizable(ParallelScope.None)]
    public sealed class GitSubmoduleInstallProbeTests
    {
        private const string ManifestObjectId =
            "cccccccccccccccccccccccccccccccccccccccc";
        private const string MetaObjectId =
            "dddddddddddddddddddddddddddddddddddddddd";

        private ICommandRunner previousRunner;
        private GitSubmoduleInstallProbe probe;

        [SetUp]
        public void SetUp()
        {
            previousRunner = CliCommandRunner.CurrentRunner;
            probe = new GitSubmoduleInstallProbe();
        }

        [TearDown]
        public void TearDown()
        {
            probe?.Dispose();
            CliCommandRunner.CurrentRunner = previousRunner;
        }

        [Test]
        public void Snapshot_CopiesBranchesAndDependenciesIntoReadOnlyCollections()
        {
            var source = new List<string> { "main", "release" };
            var dependencies = new List<PackageManifestDependency>
            {
                new PackageManifestDependency("com.example.dependency", "2.0.0")
            };
            var snapshot = new GitSubmoduleInstallProbeSnapshot(
                7,
                "https://example.com/package.git",
                GitSubmoduleInstallProbeStatus.Ready,
                source,
                "main",
                "com.example.package",
                "Example Package",
                "1.2.3",
                requestedBranch: "main",
                inspectedBranch: "main",
                dependencies: dependencies);

            source[0] = "mutated";
            dependencies.Clear();

            Assert.That(snapshot.Revision, Is.EqualTo(7));
            Assert.That(snapshot.Branches, Is.EqualTo(new[] { "main", "release" }));
            Assert.That(snapshot.DefaultBranch, Is.EqualTo("main"));
            Assert.That(snapshot.PackageName, Is.EqualTo("com.example.package"));
            Assert.That(snapshot.RequestedBranch, Is.EqualTo("main"));
            Assert.That(snapshot.InspectedBranch, Is.EqualTo("main"));
            Assert.That(snapshot.Dependencies, Has.Count.EqualTo(1));
            Assert.That(
                snapshot.Dependencies[0].Name,
                Is.EqualTo("com.example.dependency"));
            Assert.That(snapshot.IsComplete, Is.True);
            Assert.Throws<NotSupportedException>(() =>
                ((IList<string>)snapshot.Branches).Add("not-allowed"));
        }

        [Test]
        public void NullCommandResult_NeverCountsAsConfirmedTermination()
        {
            Assert.That(
                GitSubmoduleInstallProbe.HasConfirmedTermination(null),
                Is.False);
            Assert.That(
                GitSubmoduleInstallProbe.HasConfirmedTermination(
                    new CommandResult { TerminationConfirmed = true }),
                Is.True);
        }

        [Test]
        public void TemporaryCloneOwnership_AcceptsOnlyDirectGuidChildrenOfTheProbeDirectory()
        {
            string testProject = Path.Combine(
                Path.GetTempPath(),
                "gsm-probe-ownership-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(testProject);
            try
            {
                using (GitUtility.OverrideProjectRootForTests(testProject))
                {
                    string parent = Path.Combine(
                        testProject,
                        "Library",
                        "GitSubmoduleManager",
                        "InstallProbe");
                    string owned = Path.Combine(parent, Guid.NewGuid().ToString("N"));

                    Assert.That(
                        GitSubmoduleInstallProbe.IsOwnedTemporaryClonePath(owned),
                        Is.True);
                    Assert.That(
                        GitSubmoduleInstallProbe.IsOwnedTemporaryClonePath(
                            Path.Combine(owned, Guid.NewGuid().ToString("N"))),
                        Is.False,
                        "Nested directories must never broaden recursive cleanup scope.");
                    Assert.That(
                        GitSubmoduleInstallProbe.IsOwnedTemporaryClonePath(
                            Path.Combine(testProject, Guid.NewGuid().ToString("N"))),
                        Is.False);
                    Assert.That(
                        GitSubmoduleInstallProbe.IsOwnedTemporaryClonePath(
                            Path.Combine(parent, "not-a-guid")),
                        Is.False);
                }
            }
            finally
            {
                if (Directory.Exists(testProject))
                    Directory.Delete(testProject, true);
            }
        }

        [Test]
        public void TemporaryCloneCreation_UsesProjectLibraryAndDeletesOnlyItsOwnedDirectory()
        {
            string testProject = Path.Combine(
                Path.GetTempPath(),
                "gsm-probe-cleanup-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(testProject);
            try
            {
                using (GitUtility.OverrideProjectRootForTests(testProject))
                {
                    string clonePath = GitSubmoduleInstallProbe.CreateTemporaryClonePath();
                    string expectedParent = Path.Combine(
                        testProject,
                        "Library",
                        "GitSubmoduleManager",
                        "InstallProbe");

                    Assert.That(Path.GetDirectoryName(clonePath), Is.EqualTo(expectedParent));
                    Assert.That(Directory.Exists(clonePath), Is.True);
                    Assert.That(
                        (File.GetAttributes(clonePath) & FileAttributes.ReparsePoint) != 0,
                        Is.False);
                    Assert.That(
                        GitSubmoduleInstallProbe.IsSafeOwnedTemporaryCloneDirectory(clonePath),
                        Is.True);
                    Assert.That(
                        GitSubmoduleInstallProbe.HasPrivateDirectoryPermissions(clonePath),
                        Is.True);

                    File.WriteAllText(Path.Combine(clonePath, "probe.txt"), "owned");
                    GitSubmoduleInstallProbe.TryDeleteTemporaryClone(clonePath);
                    Assert.That(Directory.Exists(clonePath), Is.False);
                    Assert.That(Directory.Exists(expectedParent), Is.True,
                        "Cleanup must not remove the shared probe parent.");
                }
            }
            finally
            {
                if (Directory.Exists(testProject))
                    Directory.Delete(testProject, true);
            }
        }

        [Test]
        public void RemoteRefParser_SeparatesDefaultBranchAndValidBranches()
        {
            const string output =
                "ref: refs/heads/main\tHEAD\n" +
                "1111111111111111111111111111111111111111\tHEAD\n" +
                "1111111111111111111111111111111111111111\trefs/heads/main\n" +
                "2222222222222222222222222222222222222222\trefs/heads/feature/install-ui\n";

            bool parsed = GitSubmoduleInstallProbe.TryParseRemoteRefs(
                output,
                out List<string> branches,
                out string defaultBranch,
                out Dictionary<string, string> branchObjectIds,
                out string error);

            Assert.That(parsed, Is.True, error);
            Assert.That(defaultBranch, Is.EqualTo("main"));
            Assert.That(branches, Is.EqualTo(new[] { "feature/install-ui", "main" }));
            Assert.That(branches, Has.None.EqualTo("main\tHEAD"));
            Assert.That(
                branchObjectIds["main"],
                Is.EqualTo("1111111111111111111111111111111111111111"));
            Assert.That(
                branchObjectIds["feature/install-ui"],
                Is.EqualTo("2222222222222222222222222222222222222222"));
        }

        [Test]
        public void RemoteRefParser_InfersDefaultOnlyFromAUniqueHeadObjectMatch()
        {
            const string output =
                "1111111111111111111111111111111111111111\tHEAD\n" +
                "1111111111111111111111111111111111111111\trefs/heads/trunk\n" +
                "2222222222222222222222222222222222222222\trefs/heads/release\n";

            bool parsed = GitSubmoduleInstallProbe.TryParseRemoteRefs(
                output,
                out _,
                out string defaultBranch,
                out string error);

            Assert.That(parsed, Is.True, error);
            Assert.That(defaultBranch, Is.EqualTo("trunk"));
        }

        [Test]
        public void RemoteRefParser_RejectsBranchWithoutExactCommitIdentity()
        {
            const string output =
                "not-an-object-id\trefs/heads/main\n";

            Assert.That(
                GitSubmoduleInstallProbe.TryParseRemoteRefs(
                    output,
                    out _,
                    out _,
                    out string error),
                Is.False);
            Assert.That(error, Does.Contain("commit identity"));
        }

        [Test]
        public void RootTreeParser_AcceptsOnlyExactRegularRootBlobs()
        {
            Assert.That(
                GitSubmoduleInstallProbe.TryParseRootPackageTree(
                    RootPackageTree(manifestMode: "100755"),
                    out string manifestObjectId,
                    out string metaObjectId,
                    out string metaMessage,
                    out string error),
                Is.True,
                error);
            Assert.That(manifestObjectId, Is.EqualTo(ManifestObjectId));
            Assert.That(metaObjectId, Is.EqualTo(MetaObjectId));
            Assert.That(metaMessage, Is.Empty);
        }

        [Test]
        public void RootTreeParser_RejectsSymlinkedPackageManifest()
        {
            Assert.That(
                GitSubmoduleInstallProbe.TryParseRootPackageTree(
                    RootPackageTree(manifestMode: "120000"),
                    out _,
                    out _,
                    out _,
                    out string error),
                Is.False);
            Assert.That(error, Does.Contain("symbolic-link"));
        }

        [Test]
        public void RootTreeParser_TreatsSymlinkedMetaAsUnverified()
        {
            Assert.That(
                GitSubmoduleInstallProbe.TryParseRootPackageTree(
                    RootPackageTree(metaMode: "120000"),
                    out string manifestObjectId,
                    out string metaObjectId,
                    out string metaMessage,
                    out string error),
                Is.True,
                error);
            Assert.That(manifestObjectId, Is.EqualTo(ManifestObjectId));
            Assert.That(metaObjectId, Is.Empty);
            Assert.That(metaMessage, Does.Contain("symbolic-link"));
            Assert.That(metaMessage, Does.Contain("unverified"));
        }

        [Test]
        public void RootTreeParser_RejectsUnsupportedObjectIdLength()
        {
            string unsupportedObjectId = new string('a', 41);
            string output = "100644 blob " + unsupportedObjectId +
                            "\tpackage.json\0";

            Assert.That(
                GitSubmoduleInstallProbe.TryParseRootPackageTree(
                    output,
                    out _,
                    out _,
                    out _,
                    out string error),
                Is.False);
            Assert.That(error, Does.Contain("malformed"));
        }

        [Test]
        public void RootTreeParser_AcceptsSha256ObjectIds()
        {
            string manifestObjectId = new string('a', 64);
            string metaObjectId = new string('b', 64);
            string output =
                "100644 blob " + manifestObjectId + "\tpackage.json\0" +
                "100644 blob " + metaObjectId + "\tpackage.json.meta\0";

            Assert.That(
                GitSubmoduleInstallProbe.TryParseRootPackageTree(
                    output,
                    out string parsedManifest,
                    out string parsedMeta,
                    out _,
                    out string error),
                Is.True,
                error);
            Assert.That(parsedManifest, Is.EqualTo(manifestObjectId));
            Assert.That(parsedMeta, Is.EqualTo(metaObjectId));
        }

        [Test]
        public void Request_UsesOnlyGitAndPublishesBranchesAndManifestMetadata()
        {
            var runner = new RecordingRunner(spec =>
            {
                if (HasArgument(spec, "ls-remote"))
                {
                    return Success(
                        "ref: refs/heads/main\tHEAD\n" +
                        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\tHEAD\n" +
                        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\trefs/heads/main\n" +
                        "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\trefs/heads/develop\n");
                }

                if (HasArgument(spec, "clone"))
                    return Success(string.Empty);

                if (HasArgument(spec, "ls-tree"))
                    return Success(RootPackageTree());

                if (HasArgument(spec, "cat-file"))
                {
                    if (HasArgument(spec, MetaObjectId))
                    {
                        return Success(
                            "fileFormatVersion: 2\n" +
                            "guid: 0123456789abcdef0123456789abcdef\n" +
                            "PackageManifestImporter:\n" +
                            "  externalObjects: {}\n");
                    }

                    return Success(
                        "{\"name\":\"com.example.package\",\"version\":\"1.2.3\"," +
                        "\"displayName\":\"Example Package\",\"dependencies\":{" +
                        "\"com.example.dependency\":\"2.0.0\"}}");
                }

                return Failure("Unexpected command.");
            });
            CliCommandRunner.CurrentRunner = runner;

            Assert.That(
                probe.Request("https://example.com/owner/package.git"),
                Is.True);
            WaitForCompletion(probe);

            GitSubmoduleInstallProbeSnapshot snapshot = probe.Current;
            Assert.That(snapshot.Status, Is.EqualTo(GitSubmoduleInstallProbeStatus.Ready));
            Assert.That(snapshot.DefaultBranch, Is.EqualTo("main"));
            Assert.That(snapshot.Branches, Is.EqualTo(new[] { "develop", "main" }));
            Assert.That(snapshot.PackageName, Is.EqualTo("com.example.package"));
            Assert.That(snapshot.DisplayName, Is.EqualTo("Example Package"));
            Assert.That(snapshot.Version, Is.EqualTo("1.2.3"));
            Assert.That(snapshot.InspectedBranch, Is.EqualTo("main"));
            Assert.That(
                snapshot.InspectedCommit,
                Is.EqualTo("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"));
            Assert.That(snapshot.Dependencies, Has.Count.EqualTo(1));
            Assert.That(
                snapshot.Dependencies[0].Name,
                Is.EqualTo("com.example.dependency"));
            Assert.That(snapshot.ErrorMessage, Is.Empty);
            Assert.That(snapshot.ManifestMessage, Is.Empty);
            Assert.That(
                snapshot.PackageManifestMetaVerification,
                Is.EqualTo(PackageManifestMetaVerification.Verified));
            Assert.That(
                snapshot.PackageManifestMetaGuid,
                Is.EqualTo("0123456789abcdef0123456789abcdef"));
            Assert.That(snapshot.PackageManifestMetaMessage, Is.Empty);

            Assert.That(runner.Calls, Has.Count.EqualTo(5));
            Assert.That(runner.Calls.All(call => call.FileName == GitUtility.GitExecutable), Is.True);
            Assert.That(runner.Calls.All(call => call.FileName != "gh"), Is.True);
            Assert.That(
                runner.Calls.Select(call => call.TimeoutMs),
                Is.EqualTo(new[]
                {
                    GitSubmoduleInstallProbe.RemoteRefsTimeoutMs,
                    GitSubmoduleInstallProbe.PartialCloneTimeoutMs,
                    GitSubmoduleInstallProbe.ManifestReadTimeoutMs,
                    GitSubmoduleInstallProbe.ManifestReadTimeoutMs,
                    GitSubmoduleInstallProbe.ManifestReadTimeoutMs
                }));
            Assert.That(
                runner.Calls.SelectMany(call => call.ArgumentList ?? Array.Empty<string>()),
                Does.Contain("--no-checkout"));
            Assert.That(
                runner.Calls.SelectMany(call => call.ArgumentList ?? Array.Empty<string>()),
                Does.Contain("--filter=blob:none"));
            Assert.That(
                runner.Calls.SelectMany(call => call.ArgumentList ?? Array.Empty<string>()),
                Does.Contain("--no-local"));
            Assert.That(
                runner.Calls.SelectMany(call => call.ArgumentList ?? Array.Empty<string>()),
                Does.Contain("cat-file"));
            CommandSpec treeRead = runner.Calls.Single(call =>
                HasArgument(call, "ls-tree"));
            Assert.That(treeRead.ArgumentList, Does.Contain("-z"));
            Assert.That(
                treeRead.ArgumentList,
                Does.Contain("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"));
            Assert.That(treeRead.ArgumentList, Does.Not.Contain("HEAD"));
            Assert.That(treeRead.ArgumentList, Does.Contain("package.json"));
            Assert.That(treeRead.ArgumentList, Does.Contain("package.json.meta"));
            Assert.That(
                runner.Calls.SelectMany(call =>
                    call.ArgumentList ?? Array.Empty<string>()),
                Does.Contain(ManifestObjectId));
            Assert.That(
                runner.Calls.SelectMany(call =>
                    call.ArgumentList ?? Array.Empty<string>()),
                Does.Contain(MetaObjectId));
            Assert.That(
                runner.Calls.SelectMany(call =>
                    call.ArgumentList ?? Array.Empty<string>()),
                Has.None.EqualTo("HEAD:package.json"));
            Assert.That(
                runner.Calls.SelectMany(call =>
                    call.ArgumentList ?? Array.Empty<string>()),
                Has.None.EqualTo("HEAD:package.json.meta"));
        }

        [Test]
        public void RequestedBranch_BindsCloneAndManifestToThatBranch()
        {
            var runner = new RecordingRunner(spec =>
            {
                if (HasArgument(spec, "ls-remote"))
                {
                    return Success(
                        "ref: refs/heads/agents/verdaccio\tHEAD\n" +
                        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\tHEAD\n" +
                        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\trefs/heads/agents/verdaccio\n" +
                        "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\trefs/heads/main\n");
                }

                if (HasArgument(spec, "clone"))
                    return Success(string.Empty);
                if (HasArgument(spec, "ls-tree"))
                    return Success(RootPackageTree());
                if (HasArgument(spec, "cat-file"))
                {
                    return Success(
                        "{\"name\":\"com.example.package\",\"version\":\"1.0.0\"}");
                }

                return Failure("Unexpected command.");
            });
            CliCommandRunner.CurrentRunner = runner;

            Assert.That(
                probe.Request(
                    "https://example.com/owner/package.git",
                    "main"),
                Is.True);
            WaitForCompletion(probe);

            GitSubmoduleInstallProbeSnapshot snapshot = probe.Current;
            Assert.That(snapshot.DefaultBranch, Is.EqualTo("agents/verdaccio"));
            Assert.That(snapshot.RequestedBranch, Is.EqualTo("main"));
            Assert.That(snapshot.InspectedBranch, Is.EqualTo("main"));
            CommandSpec clone = runner.Calls.Single(call =>
                HasArgument(call, "clone"));
            int branchIndex = clone.ArgumentList.ToList().IndexOf("--branch");
            Assert.That(branchIndex, Is.GreaterThanOrEqualTo(0));
            Assert.That(clone.ArgumentList[branchIndex + 1], Is.EqualTo("main"));
        }

        [Test]
        public void InvalidUrl_DoesNotStartACommand()
        {
            var runner = new RecordingRunner(_ => Success(string.Empty));
            CliCommandRunner.CurrentRunner = runner;

            Assert.That(probe.Request("--upload-pack=malicious"), Is.False);
            Thread.Sleep(20);

            Assert.That(runner.Calls, Is.Empty);
            Assert.That(probe.Current.Status, Is.EqualTo(GitSubmoduleInstallProbeStatus.Idle));
        }

        [Test]
        public void MissingManifest_IsNonfatalAndPreservesRemoteBranches()
        {
            var runner = new RecordingRunner(spec =>
            {
                if (HasArgument(spec, "ls-remote"))
                {
                    return Success(
                        "ref: refs/heads/trunk\tHEAD\n" +
                        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\trefs/heads/trunk\n");
                }

                if (HasArgument(spec, "clone"))
                    return Success(string.Empty);

                if (HasArgument(spec, "ls-tree"))
                    return Success(string.Empty);

                return Failure("fatal: path 'package.json' does not exist in 'HEAD'");
            });
            CliCommandRunner.CurrentRunner = runner;

            probe.Request("ssh://git@example.com/owner/package.git");
            WaitForCompletion(probe);

            Assert.That(probe.Current.Status, Is.EqualTo(GitSubmoduleInstallProbeStatus.Ready));
            Assert.That(probe.Current.Branches, Is.EqualTo(new[] { "trunk" }));
            Assert.That(probe.Current.DefaultBranch, Is.EqualTo("trunk"));
            Assert.That(probe.Current.PackageName, Is.Empty);
            Assert.That(probe.Current.ErrorMessage, Is.Empty);
            Assert.That(probe.Current.ManifestMessage, Does.Contain("package.json"));
        }

        [Test]
        public void MissingPackageManifestMeta_IsNonfatalButLeavesUnityIntentUnverified()
        {
            var runner = new RecordingRunner(spec =>
            {
                if (HasArgument(spec, "ls-remote"))
                {
                    return Success(
                        "ref: refs/heads/main\tHEAD\n" +
                        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\trefs/heads/main\n");
                }

                if (HasArgument(spec, "clone"))
                    return Success(string.Empty);
                if (HasArgument(spec, "ls-tree"))
                    return Success(RootPackageTree(includeMeta: false));

                return Success(
                    "{\"name\":\"com.example.package\",\"version\":\"1.0.0\"}");
            });
            CliCommandRunner.CurrentRunner = runner;

            probe.Request("https://example.com/owner/package.git", "main");
            WaitForCompletion(probe);

            Assert.That(probe.Current.Status, Is.EqualTo(GitSubmoduleInstallProbeStatus.Ready));
            Assert.That(probe.Current.ManifestMessage, Is.Empty);
            Assert.That(
                probe.Current.PackageManifestMetaVerification,
                Is.EqualTo(PackageManifestMetaVerification.Unverified));
            Assert.That(probe.Current.PackageManifestMetaGuid, Is.Empty);
            Assert.That(
                probe.Current.PackageManifestMetaMessage,
                Does.Contain("Unity package intent is unverified"));
            Assert.That(
                probe.Current.PackageManifestMetaMessage,
                Does.Contain("package.json.meta"));
        }

        [Test]
        public void InvalidUtf8PackageManifest_IsNotEligibleAndMetaIsNotRead()
        {
            var runner = new RecordingRunner(spec =>
            {
                if (HasArgument(spec, "ls-remote"))
                {
                    return Success(
                        "ref: refs/heads/main\tHEAD\n" +
                        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\trefs/heads/main\n");
                }

                if (HasArgument(spec, "clone"))
                    return Success(string.Empty);
                if (HasArgument(spec, "ls-tree"))
                    return Success(RootPackageTree());
                if (HasArgument(spec, ManifestObjectId))
                {
                    return new CommandResult
                    {
                        ExitCode = 0,
                        StdOut =
                            "{\"name\":\"com.example.package\",\"version\":\"1.0.0\"}",
                        StdErr = string.Empty,
                        StdOutInvalidUtf8 = true,
                        TerminationConfirmed = true
                    };
                }

                return Failure("Meta must not be read after invalid manifest encoding.");
            });
            CliCommandRunner.CurrentRunner = runner;

            probe.Request("https://example.com/owner/package.git", "main");
            WaitForCompletion(probe);

            Assert.That(probe.Current.Status, Is.EqualTo(GitSubmoduleInstallProbeStatus.Ready));
            Assert.That(probe.Current.PackageName, Is.Empty);
            Assert.That(probe.Current.ManifestMessage, Does.Contain("valid UTF-8"));
            Assert.That(
                probe.Current.PackageManifestMetaVerification,
                Is.EqualTo(PackageManifestMetaVerification.Unverified));
            Assert.That(
                runner.Calls.Any(call => HasArgument(call, MetaObjectId)),
                Is.False);
            Assert.That(
                runner.Calls.Single(call => HasArgument(call, ManifestObjectId))
                    .RequireStrictUtf8StdOut,
                Is.True);
        }

        [Test]
        public void InvalidUtf8PackageManifestMeta_IsNeverAcceptedAsVerified()
        {
            var runner = new RecordingRunner(spec =>
            {
                if (HasArgument(spec, "ls-remote"))
                {
                    return Success(
                        "ref: refs/heads/main\tHEAD\n" +
                        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\trefs/heads/main\n");
                }

                if (HasArgument(spec, "clone"))
                    return Success(string.Empty);
                if (HasArgument(spec, "ls-tree"))
                    return Success(RootPackageTree());
                if (HasArgument(spec, MetaObjectId))
                {
                    return new CommandResult
                    {
                        ExitCode = 0,
                        StdOut =
                            "fileFormatVersion: 2\n" +
                            "guid: 0123456789abcdef0123456789abcdef\n",
                        StdErr = string.Empty,
                        StdOutInvalidUtf8 = true,
                        TerminationConfirmed = true
                    };
                }

                return Success(
                    "{\"name\":\"com.example.package\",\"version\":\"1.0.0\"}");
            });
            CliCommandRunner.CurrentRunner = runner;

            probe.Request("https://example.com/owner/package.git", "main");
            WaitForCompletion(probe);

            Assert.That(probe.Current.PackageName, Is.EqualTo("com.example.package"));
            Assert.That(
                probe.Current.PackageManifestMetaVerification,
                Is.EqualTo(PackageManifestMetaVerification.Unverified));
            Assert.That(probe.Current.PackageManifestMetaGuid, Is.Empty);
            Assert.That(
                probe.Current.PackageManifestMetaMessage,
                Does.Contain("valid UTF-8"));

            CommandSpec manifestRead = runner.Calls.Single(call =>
                HasArgument(call, ManifestObjectId));
            CommandSpec metaRead = runner.Calls.Single(call =>
                HasArgument(call, MetaObjectId));
            Assert.That(manifestRead.RequireStrictUtf8StdOut, Is.True);
            Assert.That(metaRead.RequireStrictUtf8StdOut, Is.True);
        }

        [Test]
        public void SymlinkedPackageManifest_IsRejectedBeforeBlobContentIsRead()
        {
            var runner = new RecordingRunner(spec =>
            {
                if (HasArgument(spec, "ls-remote"))
                {
                    return Success(
                        "ref: refs/heads/main\tHEAD\n" +
                        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\trefs/heads/main\n");
                }

                if (HasArgument(spec, "clone"))
                    return Success(string.Empty);
                if (HasArgument(spec, "ls-tree"))
                {
                    return Success(
                        RootPackageTree(manifestMode: "120000"));
                }

                return Failure("Blob content must not be read.");
            });
            CliCommandRunner.CurrentRunner = runner;

            probe.Request("https://example.com/owner/package.git", "main");
            WaitForCompletion(probe);

            Assert.That(probe.Current.Status, Is.EqualTo(GitSubmoduleInstallProbeStatus.Ready));
            Assert.That(probe.Current.PackageName, Is.Empty);
            Assert.That(probe.Current.ManifestMessage, Does.Contain("symbolic-link"));
            Assert.That(
                runner.Calls.Any(call => HasArgument(call, "cat-file")),
                Is.False);
        }

        [Test]
        public void SymlinkedPackageManifestMeta_IsUnverifiedAndNeverReadAsContent()
        {
            var runner = new RecordingRunner(spec =>
            {
                if (HasArgument(spec, "ls-remote"))
                {
                    return Success(
                        "ref: refs/heads/main\tHEAD\n" +
                        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\trefs/heads/main\n");
                }

                if (HasArgument(spec, "clone"))
                    return Success(string.Empty);
                if (HasArgument(spec, "ls-tree"))
                    return Success(RootPackageTree(metaMode: "120000"));
                if (HasArgument(spec, ManifestObjectId))
                {
                    return Success(
                        "{\"name\":\"com.example.package\",\"version\":\"1.0.0\"}");
                }

                return Failure("Meta blob content must not be read.");
            });
            CliCommandRunner.CurrentRunner = runner;

            probe.Request("https://example.com/owner/package.git", "main");
            WaitForCompletion(probe);

            Assert.That(probe.Current.PackageName, Is.EqualTo("com.example.package"));
            Assert.That(
                probe.Current.PackageManifestMetaVerification,
                Is.EqualTo(PackageManifestMetaVerification.Unverified));
            Assert.That(
                probe.Current.PackageManifestMetaMessage,
                Does.Contain("symbolic-link"));
            Assert.That(
                runner.Calls.Any(call => HasArgument(call, MetaObjectId)),
                Is.False);
        }

        [Test]
        public void Snapshot_AllZeroMetaGuidFailsClosedToUnverified()
        {
            var snapshot = new GitSubmoduleInstallProbeSnapshot(
                1,
                "https://example.com/package.git",
                GitSubmoduleInstallProbeStatus.Ready,
                packageManifestMetaVerification:
                    PackageManifestMetaVerification.Verified,
                packageManifestMetaGuid:
                    "00000000000000000000000000000000");

            Assert.That(
                snapshot.PackageManifestMetaVerification,
                Is.EqualTo(PackageManifestMetaVerification.Unverified));
            Assert.That(snapshot.PackageManifestMetaGuid, Is.Empty);
        }

        [Test]
        public void TruncatedRemoteRefs_AreRejectedWithoutCloning()
        {
            var runner = new RecordingRunner(_ => new CommandResult
            {
                ExitCode = 0,
                StdOut = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\trefs/heads/main\n",
                StdErr = string.Empty,
                StdOutTruncated = true,
                TerminationConfirmed = true
            });
            CliCommandRunner.CurrentRunner = runner;

            probe.Request("https://example.com/owner/package.git");
            WaitForCompletion(probe);

            Assert.That(probe.Current.Status, Is.EqualTo(GitSubmoduleInstallProbeStatus.Failed));
            Assert.That(probe.Current.ErrorMessage, Does.Contain("partial branch list"));
            Assert.That(runner.Calls, Has.Count.EqualTo(1));
        }

        [Test]
        public void UnconfirmedTermination_RequiresRestartAndReleasesTheReaderLease()
        {
            var unsafeResult = new CommandResult
            {
                ExitCode = -1,
                StdOut = string.Empty,
                StdErr = "Process-tree termination could not be confirmed.",
                TerminationConfirmed = false
            };
            CliCommandRunner.CurrentRunner = new RecordingRunner(_ => unsafeResult);

            try
            {
                probe.Request("https://example.com/owner/package.git");
                WaitForCompletion(probe);

                Assert.That(
                    probe.Current.Status,
                    Is.EqualTo(GitSubmoduleInstallProbeStatus.Failed));
                Assert.That(probe.Current.RequiresEditorRestart, Is.True);
                Assert.That(probe.Current.ErrorMessage, Does.Contain("Restart"));
                Assert.That(GitSubmoduleInstallProbe.IsReaderActive, Is.False);
                Assert.That(AsyncCommandDrainRegistry.RequiresEditorRestart, Is.True);
            }
            finally
            {
                // The drain registry intentionally retains an unconfirmed
                // result for the rest of the Editor session. Restore this fake
                // result to confirmed so this regression remains test-isolated.
                unsafeResult.TerminationConfirmed = true;
                _ = AsyncCommandDrainRegistry.IsDraining;
            }

            Assert.That(AsyncCommandDrainRegistry.IsDraining, Is.False);
        }

        [Test]
        public void NewestRequestSupersedesAnInFlightRequestWithoutOverlap()
        {
            using var firstStarted = new ManualResetEventSlim(false);
            using var releaseFirst = new ManualResetEventSlim(false);
            var runner = new RecordingRunner(spec =>
            {
                string url = GetRepositoryArgument(spec);
                if (HasArgument(spec, "ls-remote") && url.Contains("first.git"))
                {
                    firstStarted.Set();
                    releaseFirst.Wait(TimeSpan.FromSeconds(2));
                    return Success(
                        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\trefs/heads/obsolete\n");
                }

                if (HasArgument(spec, "ls-remote"))
                {
                    return Success(
                        "ref: refs/heads/main\tHEAD\n" +
                        "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\trefs/heads/main\n");
                }

                if (HasArgument(spec, "clone"))
                    return Success(string.Empty);

                if (HasArgument(spec, "ls-tree"))
                    return Success(RootPackageTree());

                if (HasArgument(spec, MetaObjectId))
                {
                    return Success(
                        "fileFormatVersion: 2\n" +
                        "guid: 0123456789abcdef0123456789abcdef\n");
                }

                return Success(
                    "{\"name\":\"com.example.second\",\"version\":\"2.0.0\"}");
            });
            CliCommandRunner.CurrentRunner = runner;

            probe.Request("https://example.com/first.git");
            Assert.That(firstStarted.Wait(TimeSpan.FromSeconds(2)), Is.True);
            probe.Request("https://example.com/second.git");

            Assert.That(probe.Current.Url, Is.EqualTo("https://example.com/second.git"));
            Assert.That(runner.MaximumConcurrentCalls, Is.EqualTo(1));
            releaseFirst.Set();
            WaitForCompletion(probe);

            Assert.That(probe.Current.Url, Is.EqualTo("https://example.com/second.git"));
            Assert.That(probe.Current.PackageName, Is.EqualTo("com.example.second"));
            Assert.That(probe.Current.Branches, Has.None.EqualTo("obsolete"));
            Assert.That(runner.MaximumConcurrentCalls, Is.EqualTo(1));
        }

        [Test]
        public void Dispose_NaturallyDrainsTheActiveGitCommandWithoutCancellation()
        {
            using var started = new ManualResetEventSlim(false);
            using var release = new ManualResetEventSlim(false);
            var cancellationObserved = 0;
            var runner = new RecordingRunner(spec =>
            {
                started.Set();
                release.Wait(TimeSpan.FromSeconds(2));
                if (spec.CancellationToken.IsCancellationRequested)
                    Interlocked.Exchange(ref cancellationObserved, 1);
                return Success(
                    "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\trefs/heads/main\n");
            });
            CliCommandRunner.CurrentRunner = runner;

            probe.Request("https://example.com/owner/package.git");
            Assert.That(started.Wait(TimeSpan.FromSeconds(2)), Is.True);

            probe.Dispose();
            probe = null;

            Assert.That(GitSubmoduleInstallProbe.IsReaderActive, Is.True);
            Assert.That(Volatile.Read(ref cancellationObserved), Is.Zero);
            release.Set();

            DateTime drainTimeout = DateTime.UtcNow.AddSeconds(2);
            while (GitSubmoduleInstallProbe.IsReaderActive &&
                   DateTime.UtcNow < drainTimeout)
            {
                Thread.Sleep(5);
            }

            Assert.That(Volatile.Read(ref cancellationObserved), Is.Zero,
                "Closing a popup must not force-cancel a repository command.");
            Assert.That(GitSubmoduleInstallProbe.IsReaderActive, Is.False);
            Assert.That(AsyncCommandDrainRegistry.RequiresEditorRestart, Is.False);
        }

        private static void WaitForCompletion(GitSubmoduleInstallProbe target)
        {
            DateTime timeoutAt = DateTime.UtcNow.AddSeconds(3);
            while (DateTime.UtcNow < timeoutAt)
            {
                target.Tick();
                if (target.Current.IsComplete)
                    return;
                Thread.Sleep(5);
            }

            Assert.Fail(
                "Timed out waiting for install probe. Last status: " +
                target.Current.Status);
        }

        private static bool HasArgument(CommandSpec spec, string argument)
        {
            return spec.ArgumentList != null && spec.ArgumentList.Contains(argument);
        }

        private static string GetRepositoryArgument(CommandSpec spec)
        {
            if (spec.ArgumentList == null)
                return string.Empty;

            return spec.ArgumentList.FirstOrDefault(value =>
                       value != null && value.Contains(".git")) ??
                   string.Empty;
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

        private static CommandResult Failure(string error)
        {
            return new CommandResult
            {
                ExitCode = 1,
                StdOut = string.Empty,
                StdErr = error,
                TerminationConfirmed = true
            };
        }

        private static string RootPackageTree(
            bool includeMeta = true,
            string manifestMode = "100644",
            string metaMode = "100644")
        {
            string output = manifestMode + " blob " + ManifestObjectId +
                            "\tpackage.json\0";
            if (includeMeta)
            {
                output += metaMode + " blob " + MetaObjectId +
                          "\tpackage.json.meta\0";
            }

            return output;
        }

        private sealed class RecordingRunner : ICommandRunner
        {
            private readonly Func<CommandSpec, CommandResult> handler;
            private int activeCalls;
            private int maximumConcurrentCalls;

            internal RecordingRunner(Func<CommandSpec, CommandResult> handler)
            {
                this.handler = handler;
            }

            internal List<CommandSpec> Calls { get; } = new();
            internal int MaximumConcurrentCalls => Volatile.Read(ref maximumConcurrentCalls);

            public CommandResult Run(CommandSpec spec)
            {
                int concurrency = Interlocked.Increment(ref activeCalls);
                UpdateMaximumConcurrency(concurrency);
                lock (Calls)
                {
                    Calls.Add(new CommandSpec
                    {
                        FileName = spec.FileName,
                        Arguments = spec.Arguments,
                        ArgumentList = spec.ArgumentList == null
                            ? null
                            : new List<string>(spec.ArgumentList),
                        WorkingDirectory = spec.WorkingDirectory,
                        TimeoutMs = spec.TimeoutMs,
                        CancellationToken = spec.CancellationToken,
                        TerminationScope = spec.TerminationScope,
                        RequireStrictUtf8StdOut = spec.RequireStrictUtf8StdOut
                    });
                }

                try
                {
                    return handler(spec);
                }
                finally
                {
                    Interlocked.Decrement(ref activeCalls);
                }
            }

            private void UpdateMaximumConcurrency(int value)
            {
                while (true)
                {
                    int current = Volatile.Read(ref maximumConcurrentCalls);
                    if (value <= current ||
                        Interlocked.CompareExchange(
                            ref maximumConcurrentCalls,
                            value,
                            current) == current)
                    {
                        return;
                    }
                }
            }
        }
    }
}
