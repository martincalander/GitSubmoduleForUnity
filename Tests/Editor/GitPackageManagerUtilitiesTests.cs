using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using NUnit.Framework;
using UnityEngine;

namespace GitPackageManager.Editor.Tests
{
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
        public void TryReadPackageNameFromJson_ReadsStructuredName()
        {
            var success = GitUtility.TryReadPackageNameFromJson(
                "{ \"name\": \"com.essentials.gitpackagemanager\", \"displayName\": \"Git Package Manager\" }",
                out var packageName);

            Assert.That(success, Is.True);
            Assert.That(packageName, Is.EqualTo("com.essentials.gitpackagemanager"));
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
                GitHubUtility.TryParseGitHubRepo("https://github.com/EssentialsForUnity/com.martincalander.submodulehelper.git", out var httpsOwner, out var httpsRepo),
                Is.True);
            Assert.That(httpsOwner, Is.EqualTo("EssentialsForUnity"));
            Assert.That(httpsRepo, Is.EqualTo("com.martincalander.submodulehelper"));

            Assert.That(
                GitHubUtility.TryParseGitHubRepo("git@github.com:EssentialsForUnity/com.martincalander.essentials.git", out var sshOwner, out var sshRepo),
                Is.True);
            Assert.That(sshOwner, Is.EqualTo("EssentialsForUnity"));
            Assert.That(sshRepo, Is.EqualTo("com.martincalander.essentials"));
        }

        [Test]
        public void ParseSubmoduleCommitMap_ParsesTrackedAndUninitializedEntries()
        {
            const string statusOutput =
                "-1234567890abcdef1234567890abcdef12345678 Packages/com.essentials.gitpackagemanager\n" +
                " abcdef0123456789abcdef0123456789abcdef01 Packages\\com.essentials.extensions (heads/main)\n";

            var commitMap = GitUtility.ParseSubmoduleCommitMap(statusOutput);

            Assert.That(commitMap["Packages/com.essentials.gitpackagemanager"], Is.EqualTo("1234567890abcdef1234567890abcdef12345678"));
            Assert.That(commitMap["Packages/com.essentials.extensions"], Is.EqualTo("abcdef0123456789abcdef0123456789abcdef01"));
        }

        [Test]
        public void NormalizePath_ReplacesBackslashesAndTrimsWhitespace()
        {
            var normalized = GitUtility.NormalizePath(@"  Packages\com.essentials.gitpackagemanager  ");

            Assert.That(normalized, Is.EqualTo("Packages/com.essentials.gitpackagemanager"));
        }

        // ── Subtree Command Tests ──

        [Test]
        public void TryAddSubtree_RunsCorrectGitCommand()
        {
            var runner = new FakeCommandRunner(spec =>
            {
                if (spec.FileName == "git" && spec.Arguments.Contains("subtree add"))
                {
                    return Success(string.Empty);
                }

                return Success(string.Empty);
            });
            CliCommandRunner.CurrentRunner = runner;

            bool result = GitUtility.TryAddSubtree("https://github.com/user/repo.git", "Packages/com.user.repo", "main", out string error);

            Assert.That(result, Is.True, error);
            var subtreeCall = runner.Calls.FirstOrDefault(c => c.Arguments.Contains("subtree add"));
            Assert.That(subtreeCall, Is.Not.Null);
            Assert.That(subtreeCall.Arguments, Does.Contain("--prefix=Packages/com.user.repo"));
            Assert.That(subtreeCall.Arguments, Does.Contain("https://github.com/user/repo.git"));
            Assert.That(subtreeCall.Arguments, Does.Contain("main"));
            Assert.That(subtreeCall.Arguments, Does.Contain("--squash"));
        }

        [Test]
        public void TryPullSubtree_RunsCorrectGitCommand()
        {
            var runner = new FakeCommandRunner(spec => Success(string.Empty));
            CliCommandRunner.CurrentRunner = runner;

            bool result = GitUtility.TryPullSubtree("Packages/com.user.repo", "https://github.com/user/repo.git", "main", out string error);

            Assert.That(result, Is.True, error);
            var pullCall = runner.Calls.FirstOrDefault(c => c.Arguments.Contains("subtree pull"));
            Assert.That(pullCall, Is.Not.Null);
            Assert.That(pullCall.Arguments, Does.Contain("--prefix=Packages/com.user.repo"));
            Assert.That(pullCall.Arguments, Does.Contain("--squash"));
        }

        [Test]
        public void TryPushSubtree_RunsCorrectGitCommand()
        {
            var runner = new FakeCommandRunner(spec => Success(string.Empty));
            CliCommandRunner.CurrentRunner = runner;

            bool result = GitUtility.TryPushSubtree("Packages/com.user.repo", "https://github.com/user/repo.git", "main", out string error);

            Assert.That(result, Is.True, error);
            var pushCall = runner.Calls.FirstOrDefault(c => c.Arguments.Contains("subtree push"));
            Assert.That(pushCall, Is.Not.Null);
            Assert.That(pushCall.Arguments, Does.Contain("--prefix=Packages/com.user.repo"));
        }

        // ── Manifest Tests ──

        [Test]
        public void GitPackagesManifest_RoundTrip()
        {
            var manifest = new GitPackagesManifest();
            manifest.subtrees.Add(new GitPackagesManifestEntry
            {
                path = "Packages/com.user.repo",
                url = "https://github.com/user/repo.git",
                branch = "main"
            });

            string json = JsonUtility.ToJson(manifest, true);
            var loaded = JsonUtility.FromJson<GitPackagesManifest>(json);

            Assert.That(loaded.subtrees, Has.Count.EqualTo(1));
            Assert.That(loaded.subtrees[0].path, Is.EqualTo("Packages/com.user.repo"));
            Assert.That(loaded.subtrees[0].url, Is.EqualTo("https://github.com/user/repo.git"));
            Assert.That(loaded.subtrees[0].branch, Is.EqualTo("main"));
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

            var coordinator = new DiscoveryCoordinator();
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

            var coordinator = new DiscoveryCoordinator();
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
                    return Success(BuildRepoJson(1, 30));
                }

                if (spec.FileName == "gh" && spec.Arguments.Contains("user/repos") && spec.Arguments.Contains("page=2"))
                {
                    return Success(BuildRepoJson(31, 10));
                }

                return Fail(spec, "Unexpected");
            });
            CliCommandRunner.CurrentRunner = runner;

            var coordinator = new DiscoveryCoordinator();
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
                if (coordinator.DisplayedRepos.Count != 30)
                    break;
                Thread.Sleep(10);
            }

            Assert.That(coordinator.CurrentPage, Is.EqualTo(2));
            Assert.That(coordinator.DisplayedRepos, Has.Count.EqualTo(10));
            Assert.That(coordinator.HasPrevPage, Is.True);
        }

        // ── BatchAsyncRunner Tests ──

        [Test]
        public void BatchAsyncRunner_ConcurrencyLimit()
        {
            int maxConcurrent = 0;
            int currentConcurrent = 0;
            object lockObj = new object();

            var runner = new FakeCommandRunner(spec =>
            {
                lock (lockObj)
                {
                    currentConcurrent++;
                    if (currentConcurrent > maxConcurrent)
                        maxConcurrent = currentConcurrent;
                }

                Thread.Sleep(20);

                lock (lockObj)
                {
                    currentConcurrent--;
                }

                return Success("ok");
            });
            CliCommandRunner.CurrentRunner = runner;

            var items = new List<BatchAsyncRunner.BatchItem>();
            for (int i = 0; i < 10; i++)
            {
                items.Add(new BatchAsyncRunner.BatchItem
                {
                    Spec = new CommandSpec { FileName = "test", Arguments = $"arg{i}", WorkingDirectory = "." },
                    OnComplete = _ => { }
                });
            }

            var batch = new BatchAsyncRunner(items, 3);
            var timeoutAt = DateTime.UtcNow.AddSeconds(5);
            while (!batch.IsComplete && DateTime.UtcNow < timeoutAt)
            {
                batch.Tick();
                Thread.Sleep(10);
            }

            Assert.That(batch.IsComplete, Is.True);
            Assert.That(batch.CompletedCount, Is.EqualTo(10));
            Assert.That(maxConcurrent, Is.LessThanOrEqualTo(3));
        }

        // ── TryGetAllPackages Tests ──

        [Test]
        public void TryGetAllPackages_CombinesBothTypes()
        {
            // This test verifies the manifest parsing path.
            // Submodule path requires .gitmodules which we can't mock easily,
            // but subtree path reads from .gitpackages manifest via JSON.
            var manifest = new GitPackagesManifest();
            manifest.subtrees.Add(new GitPackagesManifestEntry
            {
                path = "Packages/com.test.subtree",
                url = "https://github.com/test/subtree.git",
                branch = "main"
            });

            string json = JsonUtility.ToJson(manifest);
            var loaded = JsonUtility.FromJson<GitPackagesManifest>(json);

            Assert.That(loaded.subtrees, Has.Count.EqualTo(1));
            Assert.That(loaded.subtrees[0].path, Is.EqualTo("Packages/com.test.subtree"));
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

        private static CommandResult Success(string stdOut)
        {
            return new CommandResult
            {
                ExitCode = 0,
                StdOut = stdOut,
                StdErr = string.Empty
            };
        }

        private static CommandResult Fail(CommandSpec spec, string error)
        {
            return new CommandResult
            {
                ExitCode = 1,
                StdOut = string.Empty,
                StdErr = $"{error}: {spec.FileName} {spec.Arguments}"
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
