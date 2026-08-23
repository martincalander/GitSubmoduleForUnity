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
                DeclaredDescription = "Package description",
                DeclaredMinimumUnityVersion = "2021.3.0f1",
                DeclaredAuthorName = "Package Author",
                DeclaredDocumentationUrl = "https://example.com/docs",
                DeclaredChangelogUrl = "https://example.com/changelog",
                DeclaredLicensesUrl = "https://example.com/license",
                DeclaredDependencies = new[]
                {
                    new PackageManifestDependency(
                        "com.example.dependency",
                        "4.5.6")
                },
                ManifestState = PackageManifestState.Valid
            };

            var copy = new PackageManagerGitHubRepository(source);
            var equivalent = new PackageManagerGitHubRepository(source);
            source.Name = "mutated";
            source.Description = "Changed";
            source.DeclaredPackageName = "com.example.changed";
            source.DeclaredDisplayName = "Changed Package";
            source.DeclaredVersion = "9.9.9";
            source.DeclaredDescription = "Changed description";
            source.DeclaredMinimumUnityVersion = "6000.0.0f1";
            source.DeclaredAuthorName = "Changed Author";
            source.DeclaredDocumentationUrl = "https://changed.example.com/docs";
            source.DeclaredChangelogUrl = "https://changed.example.com/changelog";
            source.DeclaredLicensesUrl = "https://changed.example.com/license";
            source.DeclaredDependencies = Array.Empty<PackageManifestDependency>();

            Assert.That(copy.Name, Is.EqualTo("repository"));
            Assert.That(copy.Description, Is.EqualTo("Original"));
            Assert.That(copy.PackageName, Is.EqualTo("com.example.repository"));
            Assert.That(copy.DisplayName, Is.EqualTo("Repository Package"));
            Assert.That(copy.Version, Is.EqualTo("1.2.3"));
            Assert.That(copy.PackageDescription, Is.EqualTo("Package description"));
            Assert.That(copy.MinimumUnityVersion, Is.EqualTo("2021.3.0f1"));
            Assert.That(copy.AuthorName, Is.EqualTo("Package Author"));
            Assert.That(copy.DocumentationUrl, Is.EqualTo("https://example.com/docs"));
            Assert.That(copy.ChangelogUrl, Is.EqualTo("https://example.com/changelog"));
            Assert.That(copy.LicensesUrl, Is.EqualTo("https://example.com/license"));
            Assert.That(copy.Dependencies, Has.Count.EqualTo(1));
            Assert.That(copy.Dependencies[0].Name, Is.EqualTo("com.example.dependency"));
            Assert.That(copy.Dependencies[0].Version, Is.EqualTo("4.5.6"));
            Assert.That(copy.HasSameContent(equivalent), Is.True);
            Assert.That(
                copy.HasSameContent(new PackageManagerGitHubRepository(source)),
                Is.False,
                "A changed immutable record must replace the published catalogue collection.");
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
            var oneRepositorySnapshots =
                new List<IReadOnlyList<PackageManagerGitHubRepository>>();
            void CaptureSnapshot()
            {
                changeCount++;
                IReadOnlyList<PackageManagerGitHubRepository> repositories =
                    PackageManagerGitHubDiscovery.Current.Repositories;
                if (repositories.Count == 1)
                    oneRepositorySnapshots.Add(repositories);
            }
            PackageManagerGitHubDiscovery.SnapshotChanged += CaptureSnapshot;
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
                Assert.That(
                    snapshot.Repositories.Select(
                        repository => repository.PackageDescription),
                    Is.EqualTo(new[]
                    {
                        "Manifest NODE-ORGANIZATION",
                        "Manifest NODE-PERSONAL"
                    }));
                Assert.That(
                    snapshot.Repositories.All(
                        repository =>
                            repository.MinimumUnityVersion == "2021.3.0f1"),
                    Is.True);
                Assert.That(
                    snapshot.Repositories.All(
                        repository => repository.AuthorName == "Package Author"),
                    Is.True);
                Assert.That(
                    snapshot.Repositories.All(
                        repository => repository.Dependencies.Count == 1 &&
                                      repository.Dependencies[0].Name ==
                                      "com.example.shared" &&
                                      repository.Dependencies[0].Version == "3.2.1"),
                    Is.True);
                Assert.That(changeCount, Is.GreaterThan(2),
                    "Each settled page should publish incremental progress.");
                Assert.That(oneRepositorySnapshots, Has.Count.GreaterThan(1),
                    "Progress should publish while the one-package catalogue is unchanged.");
                Assert.That(
                    oneRepositorySnapshots.All(snapshotRepositories =>
                        ReferenceEquals(
                            snapshotRepositories,
                            oneRepositorySnapshots[0])),
                    Is.True,
                    "Status-only progress must reuse the immutable repository collection.");
                Assert.That(runner.PersonalPageCalls, Is.EqualTo(2));
                Assert.That(runner.OrganizationPageCalls, Is.EqualTo(1));
            }
            finally
            {
                PackageManagerGitHubDiscovery.SnapshotChanged -= CaptureSnapshot;
            }
        }

        [Test]
        public void Refresh_RetainsLastCompletedCatalogueThroughBoundedFailureWindow()
        {
            RetainIsolatedDiscoveryOrIgnore();

            var runner = new CatalogueRunner();
            CliCommandRunner.CurrentRunner = runner;
            PackageManagerGitHubDiscovery.Refresh();
            WaitForCatalogue();

            IReadOnlyList<PackageManagerGitHubRepository> completedRepositories =
                PackageManagerGitHubDiscovery.Current.Repositories;
            Assert.That(completedRepositories, Has.Count.EqualTo(2));

            runner.FailOrganizationRequests = true;
            var refreshRepositories =
                new List<IReadOnlyList<PackageManagerGitHubRepository>>();
            void CaptureRefresh() => refreshRepositories.Add(
                PackageManagerGitHubDiscovery.Current.Repositories);
            PackageManagerGitHubDiscovery.SnapshotChanged += CaptureRefresh;
            try
            {
                PackageManagerGitHubDiscovery.Refresh();

                PackageManagerGitHubDiscoverySnapshot refreshing =
                    PackageManagerGitHubDiscovery.Current;
                Assert.That(refreshing.IsLoading, Is.True);
                Assert.That(refreshing.IsShowingRetainedRepositories, Is.True);
                Assert.That(
                    refreshing.Repositories,
                    Is.SameAs(completedRepositories),
                    "Starting a refresh must not withdraw installed-package actions from the UI.");
                Assert.That(refreshing.StatusMessage, Does.Contain("remain available"));

                WaitForCatalogue();
            }
            finally
            {
                PackageManagerGitHubDiscovery.SnapshotChanged -= CaptureRefresh;
            }

            PackageManagerGitHubDiscoverySnapshot failed =
                PackageManagerGitHubDiscovery.Current;
            Assert.That(failed.IsLoading, Is.False);
            Assert.That(failed.ErrorMessage, Is.Not.Empty);
            Assert.That(failed.IsShowingRetainedRepositories, Is.True);
            Assert.That(failed.Repositories, Is.SameAs(completedRepositories));
            Assert.That(refreshRepositories, Is.Not.Empty);
            Assert.That(
                refreshRepositories.All(repositories =>
                    ReferenceEquals(repositories, completedRepositories)),
                Is.True,
                "A partial replacement must stay staged until refresh completes.");

            PackageManagerGitHubDiscovery.Tick(
                EditorApplication.timeSinceStartup +
                PackageManagerGitHubDiscovery.RetainedCatalogueDurationSeconds +
                1d);

            PackageManagerGitHubDiscoverySnapshot expired =
                PackageManagerGitHubDiscovery.Current;
            Assert.That(expired.IsShowingRetainedRepositories, Is.False);
            Assert.That(
                expired.Repositories.Select(repository => repository.PackageName),
                Is.EqualTo(new[] { "com.example.personal" }),
                "After the safety window, only packages validated by the failed refresh may remain.");
            Assert.That(expired.StatusMessage, Does.Contain("validating 1"));
            Assert.That(expired.ErrorMessage, Is.EqualTo(failed.ErrorMessage));

            PackageManagerGitHubDiscovery.Refresh();
            PackageManagerGitHubDiscoverySnapshot retryAfterExpiry =
                PackageManagerGitHubDiscovery.Current;
            Assert.That(retryAfterExpiry.IsShowingRetainedRepositories, Is.False);
            Assert.That(
                retryAfterExpiry.Repositories,
                Is.Empty,
                "Retrying must not extend or resurrect an expired successful catalogue.");
        }

        [Test]
        public void Refresh_RetainsCompletedCatalogueWhenCoverageIsIncomplete()
        {
            RetainIsolatedDiscoveryOrIgnore();

            var runner = new CatalogueRunner
            {
                ReturnNoOrganizations = true
            };
            CliCommandRunner.CurrentRunner = runner;
            PackageManagerGitHubDiscovery.Refresh();
            WaitForCatalogue();

            IReadOnlyList<PackageManagerGitHubRepository> completedRepositories =
                PackageManagerGitHubDiscovery.Current.Repositories;
            Assert.That(completedRepositories, Has.Count.EqualTo(1));

            runner.FailOrganizationListing = true;
            PackageManagerGitHubDiscovery.Refresh();
            WaitForCatalogue();

            PackageManagerGitHubDiscoverySnapshot incomplete =
                PackageManagerGitHubDiscovery.Current;
            Assert.That(incomplete.IsLoading, Is.False);
            Assert.That(incomplete.ErrorMessage, Is.Empty);
            Assert.That(incomplete.CoverageWarningMessage, Is.Not.Empty);
            Assert.That(incomplete.IsShowingRetainedRepositories, Is.True);
            Assert.That(incomplete.Repositories, Is.SameAs(completedRepositories));
            Assert.That(incomplete.StatusMessage, Does.Contain("coverage was incomplete"));

            PackageManagerGitHubDiscovery.Tick(
                EditorApplication.timeSinceStartup +
                PackageManagerGitHubDiscovery.RetainedCatalogueDurationSeconds +
                1d);

            PackageManagerGitHubDiscoverySnapshot expired =
                PackageManagerGitHubDiscovery.Current;
            Assert.That(expired.IsShowingRetainedRepositories, Is.False);
            Assert.That(
                expired.Repositories.Select(repository => repository.PackageName),
                Is.EqualTo(new[] { "com.example.personal" }));
            Assert.That(expired.StatusMessage, Does.Contain("incomplete coverage"));

            PackageManagerGitHubDiscovery.Refresh();
            PackageManagerGitHubDiscoverySnapshot retryAfterExpiry =
                PackageManagerGitHubDiscovery.Current;
            Assert.That(retryAfterExpiry.IsShowingRetainedRepositories, Is.False);
            Assert.That(
                retryAfterExpiry.Repositories,
                Is.Empty,
                "Equal partial contents must not grant a fresh retention window.");
        }

        [Test]
        public void Refresh_SwapsChangedCatalogueAtomicallyAndReusesIdenticalSuccessIdentity()
        {
            RetainIsolatedDiscoveryOrIgnore();

            var runner = new CatalogueRunner();
            CliCommandRunner.CurrentRunner = runner;
            PackageManagerGitHubDiscovery.Refresh();
            WaitForCatalogue();

            IReadOnlyList<PackageManagerGitHubRepository> initialRepositories =
                PackageManagerGitHubDiscovery.Current.Repositories;
            Assert.That(initialRepositories, Has.Count.EqualTo(2));

            runner.UseChangedCatalogue = true;
            var changedRefreshSnapshots =
                new List<PackageManagerGitHubDiscoverySnapshot>();
            void CaptureChangedRefresh() => changedRefreshSnapshots.Add(
                PackageManagerGitHubDiscovery.Current);
            PackageManagerGitHubDiscovery.SnapshotChanged += CaptureChangedRefresh;
            try
            {
                PackageManagerGitHubDiscovery.Refresh();
                WaitForCatalogue();
            }
            finally
            {
                PackageManagerGitHubDiscovery.SnapshotChanged -= CaptureChangedRefresh;
            }

            PackageManagerGitHubDiscoverySnapshot changed =
                PackageManagerGitHubDiscovery.Current;
            Assert.That(changed.IsLoading, Is.False);
            Assert.That(changed.ErrorMessage, Is.Empty);
            Assert.That(changed.Repositories, Is.Not.SameAs(initialRepositories));
            Assert.That(
                changed.Repositories.Select(repository => repository.PackageName),
                Is.EqualTo(new[]
                {
                    "com.example.replacement",
                    "com.example.personal"
                }));
            Assert.That(
                changed.Repositories.All(repository => repository.Version == "2.0.0"),
                Is.True);
            Assert.That(
                changedRefreshSnapshots.Where(snapshot => snapshot.IsLoading),
                Is.Not.Empty);
            Assert.That(
                changedRefreshSnapshots
                    .Where(snapshot => snapshot.IsLoading)
                    .All(snapshot =>
                        snapshot.IsShowingRetainedRepositories &&
                        ReferenceEquals(snapshot.Repositories, initialRepositories)),
                Is.True,
                "Every in-progress snapshot must retain the exact completed catalogue.");

            IReadOnlyList<PackageManagerGitHubRepository> changedRepositories =
                changed.Repositories;
            var identicalRefreshSnapshots =
                new List<PackageManagerGitHubDiscoverySnapshot>();
            void CaptureIdenticalRefresh() => identicalRefreshSnapshots.Add(
                PackageManagerGitHubDiscovery.Current);
            PackageManagerGitHubDiscovery.SnapshotChanged += CaptureIdenticalRefresh;
            try
            {
                PackageManagerGitHubDiscovery.Refresh();
                WaitForCatalogue();
            }
            finally
            {
                PackageManagerGitHubDiscovery.SnapshotChanged -= CaptureIdenticalRefresh;
            }

            PackageManagerGitHubDiscoverySnapshot identical =
                PackageManagerGitHubDiscovery.Current;
            Assert.That(identical.IsLoading, Is.False);
            Assert.That(identical.ErrorMessage, Is.Empty);
            Assert.That(
                identical.Repositories,
                Is.SameAs(changedRepositories),
                "An identical successful refresh must not create a new catalogue revision.");
            Assert.That(
                identicalRefreshSnapshots.Where(snapshot => snapshot.IsLoading),
                Is.Not.Empty);
            Assert.That(
                identicalRefreshSnapshots.All(snapshot =>
                    ReferenceEquals(snapshot.Repositories, changedRepositories)),
                Is.True);
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
            internal bool FailOrganizationRequests;
            internal bool FailOrganizationListing;
            internal bool ReturnNoOrganizations;
            internal bool UseChangedCatalogue;

            public CommandResult Run(CommandSpec spec)
            {
                string arguments = GetArguments(spec);
                if (arguments.Contains("api user --jq .login"))
                    return Success("personal-owner");

                if (FailOrganizationListing && arguments.Contains("user/orgs"))
                {
                    return new CommandResult
                    {
                        ExitCode = 1,
                        StdErr = "Organization listing failed for the test.",
                        TerminationConfirmed = true
                    };
                }

                if (arguments.Contains("user/orgs"))
                {
                    return ReturnNoOrganizations
                        ? Success(string.Empty)
                        : Success("example-org\nexample-org");
                }

                if (FailOrganizationRequests &&
                    arguments.Contains("orgs/example-org/repos"))
                {
                    return new CommandResult
                    {
                        ExitCode = 1,
                        StdErr = "Repository refresh failed for the test.",
                        TerminationConfirmed = true
                    };
                }

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
                        UseChangedCatalogue
                            ? "NODE-REPLACEMENT"
                            : "NODE-ORGANIZATION",
                        "example-org",
                        UseChangedCatalogue
                            ? "replacement-package"
                            : "organization-package"));
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

            private string ManifestResponse(CommandSpec spec)
            {
                var nodes = new List<string>();
                foreach (string argument in spec.ArgumentList ?? Array.Empty<string>())
                {
                    if (!argument.StartsWith("ids[]=", StringComparison.Ordinal))
                        continue;

                    string nodeId = argument.Substring("ids[]=".Length);
                    string packageName = nodeId == "NODE-ORGANIZATION"
                        ? "com.example.organization"
                        : nodeId == "NODE-REPLACEMENT"
                            ? "com.example.replacement"
                            : "com.example.personal";
                    string manifest =
                        "{\"name\":\"" + packageName +
                        "\",\"displayName\":\"Package " + nodeId +
                        "\",\"version\":\"" +
                        (UseChangedCatalogue ? "2.0.0" : "1.0.0") + "\"," +
                        "\"description\":\"Manifest " + nodeId + "\"," +
                        "\"unity\":\"2021.3\"," +
                        "\"unityRelease\":\"0f1\"," +
                        "\"author\":{\"name\":\"Package Author\"}," +
                        "\"documentationUrl\":\"https://example.com/docs\"," +
                        "\"changelogUrl\":\"https://example.com/changelog\"," +
                        "\"licensesUrl\":\"https://example.com/license\"," +
                        "\"dependencies\":{\"com.example.shared\":\"3.2.1\"}}";
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
