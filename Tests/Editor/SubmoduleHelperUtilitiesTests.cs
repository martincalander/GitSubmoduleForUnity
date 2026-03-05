using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using NUnit.Framework;

namespace Calander.SubmodulePackageManager.Editor.Tests
{
    public sealed class SubmoduleHelperUtilitiesTests
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
                "{ \"name\": \"com.martincalander.submodulehelper\", \"displayName\": \"Submodule Helper\" }",
                out var packageName);

            Assert.That(success, Is.True);
            Assert.That(packageName, Is.EqualTo("com.martincalander.submodulehelper"));
        }

        [Test]
        public void DerivePackageNameSuggestion_StripsNonAlphanumericCharacters()
        {
            var suggestion = GitHubUtility.DerivePackageNameSuggestion("Martin-Calander", "My.Helper-Package");

            Assert.That(suggestion, Is.EqualTo("com.martincalander.myhelperpackage"));
        }

        [Test]
        public void TryParseGitHubRepo_ParsesCommonGitHubUrls()
        {
            Assert.That(
                GitHubUtility.TryParseGitHubRepo("https://github.com/martincalander/com.martincalander.submodulehelper.git", out var httpsOwner, out var httpsRepo),
                Is.True);
            Assert.That(httpsOwner, Is.EqualTo("martincalander"));
            Assert.That(httpsRepo, Is.EqualTo("com.martincalander.submodulehelper"));

            Assert.That(
                GitHubUtility.TryParseGitHubRepo("git@github.com:martincalander/com.martincalander.essentials.git", out var sshOwner, out var sshRepo),
                Is.True);
            Assert.That(sshOwner, Is.EqualTo("martincalander"));
            Assert.That(sshRepo, Is.EqualTo("com.martincalander.essentials"));
        }

        [Test]
        public void ParseSubmoduleCommitMap_ParsesTrackedAndUninitializedEntries()
        {
            const string statusOutput =
                "-1234567890abcdef1234567890abcdef12345678 Packages/com.martincalander.submodulehelper\n" +
                " abcdef0123456789abcdef0123456789abcdef01 Packages\\com.martincalander.essentials (heads/main)\n";

            var commitMap = GitUtility.ParseSubmoduleCommitMap(statusOutput);

            Assert.That(commitMap["Packages/com.martincalander.submodulehelper"], Is.EqualTo("1234567890abcdef1234567890abcdef12345678"));
            Assert.That(commitMap["Packages/com.martincalander.essentials"], Is.EqualTo("abcdef0123456789abcdef0123456789abcdef01"));
        }

        [Test]
        public void NormalizePath_ReplacesBackslashesAndTrimsWhitespace()
        {
            var normalized = GitUtility.NormalizePath(@"  Packages\com.martincalander.submodulehelper  ");

            Assert.That(normalized, Is.EqualTo("Packages/com.martincalander.submodulehelper"));
        }

        [Test]
        public void StartListReposAsync_LoadsAllPagesUntilShortPage()
        {
            var runner = new FakeCommandRunner(spec =>
            {
                if (spec.FileName != "gh")
                {
                    return Fail(spec, "Unexpected command");
                }

                if (spec.Arguments.Contains("page=1", StringComparison.Ordinal))
                {
                    return Success(BuildRepoJson(1, 30));
                }

                if (spec.Arguments.Contains("page=2", StringComparison.Ordinal))
                {
                    return Success(BuildRepoJson(31, 2));
                }

                return Success("[]");
            });

            CliCommandRunner.CurrentRunner = runner;

            var handle = GitHubUtility.StartListReposAsync();
            var timeoutAt = DateTime.UtcNow.AddSeconds(2);

            while (!handle.IsComplete && DateTime.UtcNow < timeoutAt)
            {
                handle.Update();
                Thread.Sleep(10);
            }

            Assert.That(handle.IsComplete, Is.True);
            Assert.That(handle.IsSuccess, Is.True);
            Assert.That(handle.Repos, Has.Count.EqualTo(32));
            Assert.That(handle.Repos.First().Name, Is.EqualTo("repo-1"));
            Assert.That(handle.Repos.Last().Name, Is.EqualTo("repo-32"));
            Assert.That(runner.Calls.Select(call => call.Arguments), Contains.Item("api user/repos?sort=updated&direction=desc&per_page=30&page=1"));
            Assert.That(runner.Calls.Select(call => call.Arguments), Contains.Item("api user/repos?sort=updated&direction=desc&per_page=30&page=2"));
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
                    "\"owner\":{\"login\":\"martincalander\"}," +
                    $"\"html_url\":\"https://github.com/martincalander/repo-{index}\"," +
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
