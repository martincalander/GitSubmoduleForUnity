using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using NUnit.Framework;
using UnityEditor;

namespace MartinCalander.GitSubmoduleManager.Editor.Tests
{
    [Parallelizable(ParallelScope.None)]
    public sealed class PackageManagerGitHubDiscoveryTests
    {
        private ICommandRunner previousRunner;
        private bool ownsDiscovery;

        [SetUp]
        public void SetUp()
        {
            previousRunner = CliCommandRunner.CurrentRunner;
        }

        [TearDown]
        public void TearDown()
        {
            if (ownsDiscovery)
                PackageManagerGitHubDiscovery.Dispose();
            CliCommandRunner.CurrentRunner = previousRunner;
        }

        [Test]
        public void RepositoryRecord_IsAnImmutableCopy()
        {
            var source = new GitHubRepo
            {
                NodeId = "NODE-1",
                Owner = "owner",
                Name = "repository",
                Url = "https://github.com/owner/repository.git",
                DefaultBranch = "main",
                Description = "Original",
                DeclaredPackageName = "com.example.repository",
                DeclaredDisplayName = "Repository Package",
                DeclaredVersion = "1.2.3",
                ManifestState = PackageManifestState.Valid
            };

            var copy = new PackageManagerGitHubRepository(source);
            source.Name = "mutated";
            source.Description = "Changed";
            source.DeclaredPackageName = "com.example.changed";
            source.DeclaredDisplayName = "Changed Package";
            source.DeclaredVersion = "9.9.9";

            Assert.That(copy.Name, Is.EqualTo("repository"));
            Assert.That(copy.Description, Is.EqualTo("Original"));
            Assert.That(copy.PackageName, Is.EqualTo("com.example.repository"));
            Assert.That(copy.DisplayName, Is.EqualTo("Repository Package"));
            Assert.That(copy.Version, Is.EqualTo("1.2.3"));
        }

        [Test]
        public void Dispose_SynchronousSnapshotSubscriberCannotRestartCatalogue()
        {
            RetainIsolatedDiscoveryOrIgnore();

            void TryRestart() => PackageManagerGitHubDiscovery.EnsureStarted();
            PackageManagerGitHubDiscovery.SnapshotChanged += TryRestart;
            try
            {
                PackageManagerGitHubDiscovery.Dispose();
                Assert.That(PackageManagerGitHubDiscovery.IsStarted, Is.False,
                    "A synchronous Package Manager rebuild must not restart discovery during teardown.");
            }
            finally
            {
                PackageManagerGitHubDiscovery.SnapshotChanged -= TryRestart;
            }
        }

        [Test]
        public void Catalogue_AggregatesPersonalAndOrganizationPagesIncrementally()
        {
            RetainIsolatedDiscoveryOrIgnore();

            var runner = new CatalogueRunner();
            CliCommandRunner.CurrentRunner = runner;
            int changeCount = 0;
            void OnChanged() => changeCount++;
            PackageManagerGitHubDiscovery.SnapshotChanged += OnChanged;
            try
            {
                PackageManagerGitHubDiscovery.Refresh();
                Assert.That(PackageManagerGitHubDiscovery.Current.IsLoading, Is.True);
                Assert.That(
                    PackageManagerGitHubDiscovery.Current.StatusMessage,
                    Is.Not.Empty,
                    "Page activation needs a synchronous loading snapshot before background work starts.");
                WaitForCatalogue();

                PackageManagerGitHubDiscoverySnapshot snapshot =
                    PackageManagerGitHubDiscovery.Current;
                Assert.That(snapshot.IsLoading, Is.False);
                Assert.That(snapshot.ErrorMessage, Is.Empty);
                Assert.That(snapshot.CompletedOwners, Is.EqualTo(2));
                Assert.That(snapshot.TotalOwners, Is.EqualTo(2));
                Assert.That(snapshot.CompletedPages, Is.EqualTo(3));
                Assert.That(snapshot.Repositories, Has.Count.EqualTo(2),
                    "The repeated node on personal page two must be deduplicated.");
                Assert.That(
                    snapshot.Repositories.Select(repository => repository.PackageName),
                    Is.EqualTo(new[]
                    {
                        "com.example.organization",
                        "com.example.personal"
                    }));
                Assert.That(
                    snapshot.Repositories.Select(repository => repository.DisplayName),
                    Is.EqualTo(new[]
                    {
                        "Package NODE-ORGANIZATION",
                        "Package NODE-PERSONAL"
                    }));
                Assert.That(
                    snapshot.Repositories.All(repository => repository.Version == "1.0.0"),
                    Is.True);
                Assert.That(changeCount, Is.GreaterThan(2),
                    "Each settled page should publish incremental progress.");
                Assert.That(runner.PersonalPageCalls, Is.EqualTo(2));
                Assert.That(runner.OrganizationPageCalls, Is.EqualTo(1));
            }
            finally
            {
                PackageManagerGitHubDiscovery.SnapshotChanged -= OnChanged;
            }
        }

        private void RetainIsolatedDiscoveryOrIgnore()
        {
            if (PackageManagerGitHubDiscovery.IsStarted ||
                CliCommandRunner.HasActiveGitHubCommands ||
                CliCommandRunner.IsGitHubAuthenticationReserved ||
                CliCommandRunner.GitHubCommandRequiresEditorRestart ||
                AsyncCommandDrainRegistry.IsDraining)
            {
                Assert.Ignore(
                    "A live Package Manager host owns the shared GitHub discovery service.");
            }

            PackageManagerGitHubDiscovery.Dispose();
            ownsDiscovery = true;
        }

        private static void WaitForCatalogue()
        {
            // Keep asynchronous coordinator phases bounded without relying on
            // Editor frame timing in the deterministic test runner.
            DateTime timeout = DateTime.UtcNow.AddSeconds(15);
            while (DateTime.UtcNow < timeout)
            {
                PackageManagerGitHubDiscovery.Tick(
                    EditorApplication.timeSinceStartup + 1d);
                if (!PackageManagerGitHubDiscovery.IsLoading)
                    return;
                Thread.Sleep(5);
            }

            Assert.Fail(
                "GitHub catalogue did not finish: " +
                PackageManagerGitHubDiscovery.StatusMessage + " " +
                PackageManagerGitHubDiscovery.ErrorMessage);
        }

        private sealed class CatalogueRunner : ICommandRunner
        {
            internal int PersonalPageCalls;
            internal int OrganizationPageCalls;

            public CommandResult Run(CommandSpec spec)
            {
                string arguments = GetArguments(spec);
                if (arguments.Contains("api user --jq .login"))
                    return Success("personal-owner");

                if (arguments.Contains("user/orgs"))
                    return Success("example-org\nexample-org");

                if (arguments.Contains("user/repos") && arguments.Contains("page=1"))
                {
                    Interlocked.Increment(ref PersonalPageCalls);
                    return Success(
                        "HTTP/2.0 200 OK\r\n" +
                        "Link: <https://api.github.com/user/repos?page=2>; rel=\"next\"\r\n\r\n" +
                        RepositoryJson(
                            "NODE-PERSONAL",
                            "personal-owner",
                            "personal-package"));
                }

                if (arguments.Contains("user/repos") && arguments.Contains("page=2"))
                {
                    Interlocked.Increment(ref PersonalPageCalls);
                    return Success(RepositoryJson(
                        "NODE-PERSONAL",
                        "personal-owner",
                        "personal-package"));
                }

                if (arguments.Contains("orgs/example-org/repos"))
                {
                    Interlocked.Increment(ref OrganizationPageCalls);
                    return Success(RepositoryJson(
                        "NODE-ORGANIZATION",
                        "example-org",
                        "organization-package"));
                }

                if (arguments.Contains("graphql"))
                    return Success(ManifestResponse(spec));

                return new CommandResult
                {
                    ExitCode = 1,
                    StdErr = "Unexpected command: " + arguments,
                    TerminationConfirmed = true
                };
            }

            private static string ManifestResponse(CommandSpec spec)
            {
                var nodes = new List<string>();
                foreach (string argument in spec.ArgumentList ?? Array.Empty<string>())
                {
                    if (!argument.StartsWith("ids[]=", StringComparison.Ordinal))
                        continue;

                    string nodeId = argument.Substring("ids[]=".Length);
                    string packageName = nodeId == "NODE-ORGANIZATION"
                        ? "com.example.organization"
                        : "com.example.personal";
                    string manifest =
                        "{\"name\":\"" + packageName +
                        "\",\"displayName\":\"Package " + nodeId +
                        "\",\"version\":\"1.0.0\"}";
                    nodes.Add(
                        "{\"id\":\"" + nodeId +
                        "\",\"packageManifest\":{" +
                        "\"__typename\":\"Blob\"," +
                        "\"oid\":\"" + new string(
                            nodeId == "NODE-ORGANIZATION" ? 'b' : 'a',
                            40) + "\"," +
                        "\"byteSize\":" + Encoding.UTF8.GetByteCount(manifest) + "," +
                        "\"isBinary\":false," +
                        "\"isTruncated\":false," +
                        "\"text\":" + QuoteJson(manifest) + "}}"
                    );
                }

                return
                    "{\"data\":{\"nodes\":[" + string.Join(",", nodes) +
                    "],\"rateLimit\":{\"cost\":1,\"remaining\":100," +
                    "\"resetAt\":\"\"}},\"errors\":[]}";
            }

            private static string RepositoryJson(
                string nodeId,
                string owner,
                string name)
            {
                return
                    "[{\"node_id\":\"" + nodeId +
                    "\",\"name\":\"" + name +
                    "\",\"owner\":{\"login\":\"" + owner +
                    "\"},\"clone_url\":\"https://github.com/" + owner + "/" + name +
                    ".git\",\"html_url\":\"https://github.com/" + owner + "/" + name +
                    "\",\"default_branch\":\"main\",\"private\":false," +
                    "\"description\":\"Package\",\"updated_at\":\"2026-01-01\"}]";
            }

            private static string QuoteJson(string value)
            {
                return "\"" + value
                    .Replace("\\", "\\\\")
                    .Replace("\"", "\\\"") + "\"";
            }

            private static string GetArguments(CommandSpec spec)
            {
                return spec.Arguments ?? string.Join(" ", spec.ArgumentList ?? Array.Empty<string>());
            }

            private static CommandResult Success(string output)
            {
                return new CommandResult
                {
                    ExitCode = 0,
                    StdOut = output,
                    StdErr = string.Empty,
                    TerminationConfirmed = true
                };
            }
        }
    }
}
