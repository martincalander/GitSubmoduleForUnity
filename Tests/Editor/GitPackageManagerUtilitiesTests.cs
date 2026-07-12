using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using NUnit.Framework;

namespace Essentials.GitPackageManager.Editor.Tests
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
                GitHubUtility.TryParseGitHubRepo("https://github.com/EssentialsForUnity/com.essentials.gitpackagemanager.git", out var httpsOwner, out var httpsRepo),
                Is.True);
            Assert.That(httpsOwner, Is.EqualTo("EssentialsForUnity"));
            Assert.That(httpsRepo, Is.EqualTo("com.essentials.gitpackagemanager"));

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
        public void IsValidBranchName_RejectsUnsafeRefs(string branch, bool expected)
        {
            Assert.That(GitUtility.IsValidBranchName(branch), Is.EqualTo(expected));
        }

        [TestCase("https://github.com/owner/repo.git", true)]
        [TestCase("git@github.com:owner/repo.git", true)]
        [TestCase("../Local Repo", true)]
        [TestCase("--upload-pack=malicious", false)]
        [TestCase("https://github.com/owner/repo.git\n--config=bad", false)]
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
                    return Success(BuildRepoJson(1, 50));
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

            var coordinator = new DiscoveryCoordinator();
            coordinator.LoadInitialPage();
            coordinator.SetSearchQuery("newest", 0);
            coordinator.Tick(1.0);

            WaitForDiscovery(coordinator, 2, 1.0);

            Assert.That(coordinator.DisplayedRepos, Has.Count.EqualTo(1));
            Assert.That(coordinator.DisplayedRepos[0].Name, Is.EqualTo("repo-100"));
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
