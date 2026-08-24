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
                DeclaredLicense = "MIT",
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
            source.DeclaredLicense = "Apache-2.0";
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
            Assert.That(copy.License, Is.EqualTo("MIT"));
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
        public void Suspend_PreservesCompletedCatalogueAcrossHostReactivation()
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

            var lifecycleSnapshots =
                new List<PackageManagerGitHubDiscoverySnapshot>();
            void CaptureSnapshot() => lifecycleSnapshots.Add(
                PackageManagerGitHubDiscovery.Current);
            PackageManagerGitHubDiscovery.SnapshotChanged += CaptureSnapshot;
            try
            {
                PackageManagerGitHubDiscovery.Suspend();

                PackageManagerGitHubDiscoverySnapshot suspended =
                    PackageManagerGitHubDiscovery.Current;
                Assert.That(PackageManagerGitHubDiscovery.IsStarted, Is.False);
                Assert.That(suspended.IsLoading, Is.False);
                Assert.That(suspended.IsShowingRetainedRepositories, Is.True);
                Assert.That(suspended.Repositories, Is.SameAs(completedRepositories));

                PackageManagerGitHubDiscovery.EnsureStarted();

                PackageManagerGitHubDiscoverySnapshot reactivated =
                    PackageManagerGitHubDiscovery.Current;
                Assert.That(reactivated.IsLoading, Is.True);
                Assert.That(reactivated.IsShowingRetainedRepositories, Is.True);
                Assert.That(reactivated.Repositories, Is.SameAs(completedRepositories));
                WaitForCatalogue();
            }
            finally
            {
                PackageManagerGitHubDiscovery.SnapshotChanged -= CaptureSnapshot;
            }

            Assert.That(
                PackageManagerGitHubDiscovery.Current.Repositories,
                Is.SameAs(completedRepositories),
                "An identical refresh after reactivation should reuse the catalogue.");
            Assert.That(lifecycleSnapshots, Is.Not.Empty);
            Assert.That(
                lifecycleSnapshots.All(snapshot =>
                    ReferenceEquals(snapshot.Repositories, completedRepositories)),
                Is.True,
                "Suspension and reactivation must never publish an empty catalogue.");

            PackageManagerGitHubDiscovery.Suspend();
            PackageManagerGitHubDiscovery.PrepareForHost(
                EditorApplication.timeSinceStartup +
                PackageManagerGitHubDiscovery.RetainedCatalogueDurationSeconds +
                1d);
            Assert.That(
                PackageManagerGitHubDiscovery.Current.Repositories,
                Is.Empty,
                "An expired suspended catalogue must be cleared before a new host projects it.");
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
        public void Catalogue_BoundsParallelOrganizationLanesAndRefillsFreedLane()
        {
            RetainIsolatedDiscoveryOrIgnore();

            var runner = new ParallelOrganizationCatalogueRunner();
            CliCommandRunner.CurrentRunner = runner;
            try
            {
                PackageManagerGitHubDiscovery.Refresh();
                WaitForCondition(
                    () => runner.StartedOrganizationCount >=
                          PackageManagerGitHubDiscovery.MaximumConcurrentOrganizationLanes,
                    "The initial organization lanes did not start.");

                Assert.That(
                    PackageManagerGitHubDiscovery.MaximumConcurrentOrganizationLanes,
                    Is.EqualTo(2),
                    "Keep the initial concurrency cap conservative and explicit.");
                Assert.That(runner.ActiveOrganizationRequests, Is.EqualTo(2));
                Assert.That(runner.MaximumActiveOrganizationRequests, Is.EqualTo(2));
                Assert.That(runner.HasStartedOrganization("org-alpha"), Is.True);
                Assert.That(runner.HasStartedOrganization("org-beta"), Is.True);
                Assert.That(runner.HasStartedOrganization("org-gamma"), Is.False,
                    "An organization beyond the lane cap must remain queued.");
                Assert.That(runner.OrganizationStartedBeforePersonalValidation, Is.False,
                    "Personal repositories must finish before organization lanes start.");

                runner.ReleaseOrganization("org-beta");
                WaitForCondition(
                    () => runner.HasStartedOrganization("org-gamma"),
                    "A queued organization did not start when a lane became free.");

                Assert.That(runner.ActiveOrganizationRequests, Is.EqualTo(2),
                    "The scheduler should refill one freed lane without exceeding the cap.");
                Assert.That(runner.MaximumActiveOrganizationRequests, Is.EqualTo(2));

                runner.ReleaseAllOrganizations();
                WaitForCatalogue();

                PackageManagerGitHubDiscoverySnapshot snapshot =
                    PackageManagerGitHubDiscovery.Current;
                Assert.That(snapshot.IsLoading, Is.False);
                Assert.That(snapshot.ErrorMessage, Is.Empty);
                Assert.That(snapshot.CompletedOwners, Is.EqualTo(4));
                Assert.That(snapshot.TotalOwners, Is.EqualTo(4));
                Assert.That(snapshot.CompletedPages, Is.EqualTo(5));
                Assert.That(snapshot.Repositories, Has.Count.EqualTo(5));
                Assert.That(
                    snapshot.Repositories.Select(repository => repository.PackageName),
                    Is.EqualTo(new[]
                    {
                        "com.parallel.alpha-first",
                        "com.parallel.alpha-second",
                        "com.parallel.beta",
                        "com.parallel.gamma",
                        "com.parallel.personal"
                    }),
                    "Parallel completion order must not affect catalogue ordering.");
                Assert.That(runner.OrganizationRepositoryPageCalls, Is.EqualTo(4));
                Assert.That(runner.MaximumRequestsForOwner("org-alpha"), Is.EqualTo(1),
                    "Pages for one owner must remain serialized within its lane.");
                Assert.That(runner.MaximumRequestsForOwner("org-beta"), Is.EqualTo(1));
                Assert.That(runner.MaximumRequestsForOwner("org-gamma"), Is.EqualTo(1));
            }
            finally
            {
                runner.ReleaseAllOrganizations();
            }
        }

        [Test]
        public void Refresh_CoalescesQueuedAndTerminalSubscriberIntoOneReplacementScan()
        {
            RetainIsolatedDiscoveryOrIgnore();

            var runner = new ParallelOrganizationCatalogueRunner();
            CliCommandRunner.CurrentRunner = runner;
            PackageManagerGitHubDiscovery.Refresh();
            WaitForCondition(
                () => runner.StartedOrganizationCount >=
                      PackageManagerGitHubDiscovery.MaximumConcurrentOrganizationLanes,
                "The initial organization lanes did not start.");

            bool terminalRefreshRequested = false;
            bool replacementScanObserved = false;
            void RefreshFromTerminalSnapshot()
            {
                if (terminalRefreshRequested ||
                    PackageManagerGitHubDiscovery.Current.IsLoading)
                {
                    return;
                }

                terminalRefreshRequested = true;
                PackageManagerGitHubDiscovery.Refresh();
                replacementScanObserved =
                    runner.WaitForReplacementPersonalRepositoryPage();
            }

            PackageManagerGitHubDiscovery.SnapshotChanged +=
                RefreshFromTerminalSnapshot;
            try
            {
                PackageManagerGitHubDiscovery.Refresh();
                PackageManagerGitHubDiscovery.Refresh();
                runner.ReleaseAllOrganizations();
                WaitForCatalogue();
            }
            finally
            {
                runner.ReleaseAllOrganizations();
                PackageManagerGitHubDiscovery.SnapshotChanged -=
                    RefreshFromTerminalSnapshot;
            }

            Assert.That(terminalRefreshRequested, Is.True);
            Assert.That(replacementScanObserved, Is.True,
                "The terminal callback must start the coalesced replacement scan.");
            Assert.That(runner.PersonalRepositoryPageCalls, Is.EqualTo(2),
                "The queued request and synchronous terminal callback must share one replacement scan.");
            Assert.That(runner.OrganizationRepositoryPageCalls, Is.EqualTo(8));
            Assert.That(PackageManagerGitHubDiscovery.Current.IsLoading, Is.False);
            Assert.That(PackageManagerGitHubDiscovery.Current.ErrorMessage, Is.Empty);
        }

        [Test]
        public void Suspend_LetsActiveOrganizationReadsDrainWithoutCancellation()
        {
            RetainIsolatedDiscoveryOrIgnore();

            var runner = new ParallelOrganizationCatalogueRunner();
            CliCommandRunner.CurrentRunner = runner;
            PackageManagerGitHubDiscovery.Refresh();
            WaitForCondition(
                () => runner.StartedOrganizationCount >=
                      PackageManagerGitHubDiscovery.MaximumConcurrentOrganizationLanes,
                "The organization reads did not reach the active lane cap.");

            try
            {
                PackageManagerGitHubDiscovery.Suspend();

                Assert.That(PackageManagerGitHubDiscovery.IsStarted, Is.True,
                    "An ordinary close should retain ownership until active reads settle.");
                Assert.That(PackageManagerGitHubDiscovery.IsUpdateSubscribed, Is.True,
                    "Natural draining needs a temporary update observer.");
                Assert.That(runner.AnyOrganizationCancellationRequested, Is.False,
                    "Closing Package Manager must not force-cancel ordinary GitHub reads.");
                Assert.That(
                    CliCommandRunner.GitHubCommandRequiresEditorRestart,
                    Is.False);

                // Reopen and close while the same reads are draining. The most
                // recent host state must win, so completion must still stop.
                PackageManagerGitHubDiscovery.EnsureStarted();
                PackageManagerGitHubDiscovery.Suspend();

                runner.ReleaseAllOrganizations();
                WaitForCondition(
                    () => !PackageManagerGitHubDiscovery.IsStarted,
                    "Discovery did not retire after its active reads drained.");

                Assert.That(runner.AnyOrganizationCancellationRequested, Is.False);
                Assert.That(PackageManagerGitHubDiscovery.IsUpdateSubscribed, Is.False);
                Assert.That(CliCommandRunner.HasActiveGitHubCommands, Is.False);
                Assert.That(
                    CliCommandRunner.GitHubCommandRequiresEditorRestart,
                    Is.False);
                Assert.That(runner.PersonalRepositoryPageCalls, Is.EqualTo(1),
                    "A final close during drain must suppress the queued restart.");
            }
            finally
            {
                runner.ReleaseAllOrganizations();
            }
        }

        [Test]
        public void PersonalFailure_DrainsOrganizationListingBeforePublishingFailure()
        {
            RetainIsolatedDiscoveryOrIgnore();

            var runner = new PersonalFailureDrainRunner();
            CliCommandRunner.CurrentRunner = runner;
            try
            {
                PackageManagerGitHubDiscovery.Refresh();
                WaitForCondition(
                    () => PackageManagerGitHubDiscovery.IsFailureDrainPending,
                    "The personal repository failure was not consumed.");

                Assert.That(runner.OrganizationRequestStarted, Is.True);
                Assert.That(runner.OrganizationCancellationRequested, Is.False);
                Assert.That(PackageManagerGitHubDiscovery.Current.IsLoading, Is.True,
                    "Terminal failure must wait for the organization command to finish naturally.");
                Assert.That(PackageManagerGitHubDiscovery.Current.ErrorMessage, Is.Empty);
                Assert.That(
                    CliCommandRunner.GitHubCommandRequiresEditorRestart,
                    Is.False);

                runner.ReleaseOrganizationRequest();
                WaitForCatalogue();

                Assert.That(PackageManagerGitHubDiscovery.IsFailureDrainPending, Is.False);
                Assert.That(PackageManagerGitHubDiscovery.Current.IsLoading, Is.False);
                Assert.That(PackageManagerGitHubDiscovery.Current.ErrorMessage, Is.Not.Empty);
                Assert.That(runner.OrganizationCancellationRequested, Is.False);
                Assert.That(
                    CliCommandRunner.GitHubCommandRequiresEditorRestart,
                    Is.False);
            }
            finally
            {
                runner.ReleaseOrganizationRequest();
            }
        }

        [Test]
        public void RestartRequiredCommand_BlocksQueuedAndManualReplacementScans()
        {
            RetainIsolatedDiscoveryOrIgnore();

            var runner = new RestartRequiredFailureRunner();
            CliCommandRunner.CurrentRunner = runner;
            try
            {
                PackageManagerGitHubDiscovery.Refresh();
                WaitForCondition(
                    () => runner.PersonalRepositoryRequestStarted,
                    "The personal repository request did not start.");

                PackageManagerGitHubDiscovery.Refresh();
                runner.ReleasePersonalRepositoryRequest();
                WaitForCatalogue();

                AssertRestartRequiredTerminalState(runner);

                PackageManagerGitHubDiscovery.Refresh();

                AssertRestartRequiredTerminalState(runner);
            }
            finally
            {
                runner.ReleasePersonalRepositoryRequest();
                StopDiscoveryAndResetRestartRequirementForTests();
            }
        }

        [Test]
        public void RestartRequiredCommand_BlocksGracefulDrainRestart()
        {
            RetainIsolatedDiscoveryOrIgnore();

            var runner = new RestartRequiredFailureRunner();
            CliCommandRunner.CurrentRunner = runner;
            try
            {
                PackageManagerGitHubDiscovery.Refresh();
                WaitForCondition(
                    () => runner.PersonalRepositoryRequestStarted,
                    "The personal repository request did not start.");

                PackageManagerGitHubDiscovery.Suspend();
                PackageManagerGitHubDiscovery.EnsureStarted();
                runner.ReleasePersonalRepositoryRequest();
                WaitForCatalogue();

                AssertRestartRequiredTerminalState(runner);
                Assert.That(runner.PersonalRepositoryCancellationRequested, Is.False,
                    "Graceful draining must not manufacture the restart requirement through cancellation.");
            }
            finally
            {
                runner.ReleasePersonalRepositoryRequest();
                StopDiscoveryAndResetRestartRequirementForTests();
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
            Assert.That(PackageManagerGitHubDiscovery.IsUpdateSubscribed, Is.True,
                "Retained results need a bounded expiry observer.");

            PackageManagerGitHubDiscovery.ProcessEditorUpdate(
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
            Assert.That(PackageManagerGitHubDiscovery.IsUpdateSubscribed, Is.False,
                "The expiry observer should retire after retained results expire.");

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

            PackageManagerGitHubDiscovery.ProcessEditorUpdate(
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

        private static void WaitForCondition(Func<bool> condition, string failureMessage)
        {
            DateTime timeout = DateTime.UtcNow.AddSeconds(15);
            while (DateTime.UtcNow < timeout)
            {
                PackageManagerGitHubDiscovery.Tick(
                    EditorApplication.timeSinceStartup + 1d);
                if (condition())
                    return;
                Thread.Sleep(5);
            }

            Assert.Fail(failureMessage + " " +
                        PackageManagerGitHubDiscovery.StatusMessage + " " +
                        PackageManagerGitHubDiscovery.ErrorMessage);
        }

        private static void AssertRestartRequiredTerminalState(
            RestartRequiredFailureRunner runner)
        {
            Assert.That(CliCommandRunner.GitHubCommandRequiresEditorRestart, Is.True);
            Assert.That(PackageManagerGitHubDiscovery.Current.IsLoading, Is.False,
                "A restart-required command gate must not enter an unstartable loading scan.");
            Assert.That(
                PackageManagerGitHubDiscovery.Current.ErrorMessage,
                Is.EqualTo(
                    PackageManagerGitHubDiscovery.GitHubCommandRestartRequiredMessage));
            Assert.That(runner.PersonalRepositoryPageCalls, Is.EqualTo(1));
        }

        private static void StopDiscoveryAndResetRestartRequirementForTests()
        {
            PackageManagerGitHubDiscovery.Dispose();
            DateTime timeout = DateTime.UtcNow.AddSeconds(15);
            while (DateTime.UtcNow < timeout &&
                   (PackageManagerGitHubDiscovery.IsStarted ||
                    CliCommandRunner.HasActiveGitHubCommands))
            {
                PackageManagerGitHubDiscovery.Tick(
                    EditorApplication.timeSinceStartup + 1d);
                Thread.Sleep(5);
            }

            CliCommandRunner.ResetGitHubCommandRestartRequirementForTests();
        }

        private sealed class PersonalFailureDrainRunner : ICommandRunner
        {
            private readonly ManualResetEventSlim organizationStarted = new(false);
            private readonly ManualResetEventSlim releaseOrganization = new(false);
            private readonly object syncRoot = new();
            private CancellationToken organizationCancellationToken;

            internal bool OrganizationRequestStarted => organizationStarted.IsSet;

            internal bool OrganizationCancellationRequested
            {
                get
                {
                    lock (syncRoot)
                        return organizationCancellationToken.IsCancellationRequested;
                }
            }

            internal void ReleaseOrganizationRequest()
            {
                releaseOrganization.Set();
            }

            public CommandResult Run(CommandSpec spec)
            {
                string arguments = GetArguments(spec);
                if (arguments.Contains("api user --jq .login"))
                    return Success("personal-owner");

                if (arguments.Contains("user/orgs"))
                {
                    lock (syncRoot)
                        organizationCancellationToken = spec.CancellationToken;
                    organizationStarted.Set();
                    return WaitForRelease(
                        releaseOrganization,
                        spec.CancellationToken,
                        string.Empty);
                }

                if (arguments.Contains("user/repos"))
                {
                    while (!organizationStarted.Wait(10))
                    {
                        if (spec.CancellationToken.IsCancellationRequested)
                            return Cancelled();
                    }

                    return Failure(
                        "Personal repository listing failed for the test.",
                        terminationConfirmed: true);
                }

                return Failure("Unexpected command: " + arguments, true);
            }
        }

        private sealed class RestartRequiredFailureRunner : ICommandRunner
        {
            private readonly ManualResetEventSlim personalRepositoryStarted =
                new(false);
            private readonly ManualResetEventSlim releasePersonalRepository =
                new(false);
            private readonly object syncRoot = new();
            private CancellationToken personalRepositoryCancellationToken;
            private int personalRepositoryPageCalls;

            internal bool PersonalRepositoryRequestStarted =>
                personalRepositoryStarted.IsSet;
            internal int PersonalRepositoryPageCalls =>
                Volatile.Read(ref personalRepositoryPageCalls);

            internal bool PersonalRepositoryCancellationRequested
            {
                get
                {
                    lock (syncRoot)
                        return personalRepositoryCancellationToken.IsCancellationRequested;
                }
            }

            internal void ReleasePersonalRepositoryRequest()
            {
                releasePersonalRepository.Set();
            }

            public CommandResult Run(CommandSpec spec)
            {
                string arguments = GetArguments(spec);
                if (arguments.Contains("api user --jq .login"))
                    return Success("personal-owner");
                if (arguments.Contains("user/orgs"))
                    return Success(string.Empty);

                if (arguments.Contains("user/repos"))
                {
                    Interlocked.Increment(ref personalRepositoryPageCalls);
                    lock (syncRoot)
                        personalRepositoryCancellationToken = spec.CancellationToken;
                    personalRepositoryStarted.Set();
                    return WaitForRelease(
                        releasePersonalRepository,
                        spec.CancellationToken,
                        "Repository process ownership could not be confirmed.",
                        terminationConfirmed: false);
                }

                return Failure("Unexpected command: " + arguments, true);
            }
        }

        private static CommandResult WaitForRelease(
            ManualResetEventSlim release,
            CancellationToken cancellationToken,
            string output,
            bool terminationConfirmed = true)
        {
            while (!release.Wait(10))
            {
                if (cancellationToken.IsCancellationRequested)
                    return Cancelled();
            }

            return terminationConfirmed
                ? Success(output)
                : Failure(output, terminationConfirmed: false);
        }

        private static CommandResult Cancelled()
        {
            return new CommandResult
            {
                ExitCode = -1,
                Cancelled = true,
                TerminationConfirmed = true
            };
        }

        private static CommandResult Failure(
            string error,
            bool terminationConfirmed)
        {
            return new CommandResult
            {
                ExitCode = 1,
                StdErr = error,
                TerminationConfirmed = terminationConfirmed
            };
        }

        private static string GetArguments(CommandSpec spec)
        {
            return spec.Arguments ??
                   string.Join(" ", spec.ArgumentList ?? Array.Empty<string>());
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

        private sealed class ParallelOrganizationCatalogueRunner : ICommandRunner
        {
            private static readonly string[] Organizations =
            {
                "org-alpha",
                "org-beta",
                "org-gamma"
            };

            private readonly object syncRoot = new();
            private readonly Dictionary<string, ManualResetEventSlim> organizationGates =
                Organizations.ToDictionary(
                    organization => organization,
                    _ => new ManualResetEventSlim(false),
                    StringComparer.Ordinal);
            private readonly HashSet<string> startedOrganizations =
                new(StringComparer.Ordinal);
            private readonly Dictionary<string, int> activeRequestsByOwner =
                new(StringComparer.Ordinal);
            private readonly Dictionary<string, int> maximumRequestsByOwner =
                new(StringComparer.Ordinal);
            private readonly List<CancellationToken> organizationCancellationTokens =
                new();
            private readonly ManualResetEventSlim replacementPersonalPageStarted =
                new(false);

            private int activeOrganizationRequests;
            private int maximumActiveOrganizationRequests;
            private int organizationRepositoryPageCalls;
            private int personalRepositoryPageCalls;
            private int personalValidationCompleted;
            private bool organizationStartedBeforePersonalValidation;

            internal int ActiveOrganizationRequests
            {
                get
                {
                    lock (syncRoot)
                        return activeOrganizationRequests;
                }
            }

            internal int MaximumActiveOrganizationRequests
            {
                get
                {
                    lock (syncRoot)
                        return maximumActiveOrganizationRequests;
                }
            }

            internal int OrganizationRepositoryPageCalls
            {
                get
                {
                    lock (syncRoot)
                        return organizationRepositoryPageCalls;
                }
            }

            internal int PersonalRepositoryPageCalls =>
                Volatile.Read(ref personalRepositoryPageCalls);

            internal int StartedOrganizationCount
            {
                get
                {
                    lock (syncRoot)
                        return startedOrganizations.Count;
                }
            }

            internal bool OrganizationStartedBeforePersonalValidation
            {
                get
                {
                    lock (syncRoot)
                        return organizationStartedBeforePersonalValidation;
                }
            }

            internal bool AnyOrganizationCancellationRequested
            {
                get
                {
                    lock (syncRoot)
                    {
                        return organizationCancellationTokens.Any(
                            token => token.IsCancellationRequested);
                    }
                }
            }

            internal bool HasStartedOrganization(string owner)
            {
                lock (syncRoot)
                    return startedOrganizations.Contains(owner);
            }

            internal int MaximumRequestsForOwner(string owner)
            {
                lock (syncRoot)
                {
                    return maximumRequestsByOwner.TryGetValue(owner, out int maximum)
                        ? maximum
                        : 0;
                }
            }

            internal void ReleaseOrganization(string owner)
            {
                if (organizationGates.TryGetValue(owner, out ManualResetEventSlim gate))
                    gate.Set();
            }

            internal void ReleaseAllOrganizations()
            {
                foreach (ManualResetEventSlim gate in organizationGates.Values)
                    gate.Set();
            }

            internal bool WaitForReplacementPersonalRepositoryPage()
            {
                return replacementPersonalPageStarted.Wait(
                    TimeSpan.FromSeconds(5));
            }

            public CommandResult Run(CommandSpec spec)
            {
                string arguments = GetArguments(spec);
                if (arguments.Contains("api user --jq .login"))
                    return Success("personal-owner");

                if (arguments.Contains("user/orgs"))
                    return Success(string.Join("\n", Organizations));

                if (arguments.Contains("user/repos"))
                {
                    if (Interlocked.Increment(ref personalRepositoryPageCalls) == 2)
                        replacementPersonalPageStarted.Set();
                    return Success(RepositoryJson(
                        "NODE-PERSONAL",
                        "personal-owner",
                        "personal"));
                }

                foreach (string organization in Organizations)
                {
                    if (arguments.Contains("orgs/" + organization + "/repos"))
                        return RunOrganizationPage(spec, arguments, organization);
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

            private CommandResult RunOrganizationPage(
                CommandSpec spec,
                string arguments,
                string owner)
            {
                lock (syncRoot)
                {
                    startedOrganizations.Add(owner);
                    organizationCancellationTokens.Add(spec.CancellationToken);
                    organizationRepositoryPageCalls++;
                    activeOrganizationRequests++;
                    maximumActiveOrganizationRequests = Math.Max(
                        maximumActiveOrganizationRequests,
                        activeOrganizationRequests);
                    activeRequestsByOwner.TryGetValue(owner, out int activeForOwner);
                    activeForOwner++;
                    activeRequestsByOwner[owner] = activeForOwner;
                    maximumRequestsByOwner.TryGetValue(owner, out int maximumForOwner);
                    maximumRequestsByOwner[owner] = Math.Max(
                        maximumForOwner,
                        activeForOwner);
                    if (Volatile.Read(ref personalValidationCompleted) == 0)
                        organizationStartedBeforePersonalValidation = true;
                }

                try
                {
                    ManualResetEventSlim gate = organizationGates[owner];
                    while (!gate.Wait(10))
                    {
                        if (spec.CancellationToken.IsCancellationRequested)
                        {
                            return new CommandResult
                            {
                                ExitCode = -1,
                                Cancelled = true,
                                TerminationConfirmed = true
                            };
                        }
                    }

                    bool secondPage = arguments.Contains("page=2");
                    if (owner == "org-alpha" && !secondPage)
                    {
                        return Success(
                            "HTTP/2.0 200 OK\r\n" +
                            "Link: <https://api.github.com/orgs/org-alpha/repos?page=2>; rel=\"next\"\r\n\r\n" +
                            RepositoryJson(
                                "NODE-ALPHA-FIRST",
                                owner,
                                "alpha-first"));
                    }

                    string suffix = owner.Substring("org-".Length);
                    return Success(RepositoryJson(
                        owner == "org-alpha"
                            ? "NODE-ALPHA-SECOND"
                            : "NODE-" + suffix.ToUpperInvariant(),
                        owner,
                        owner == "org-alpha" ? "alpha-second" : suffix));
                }
                finally
                {
                    lock (syncRoot)
                    {
                        activeOrganizationRequests--;
                        activeRequestsByOwner[owner]--;
                    }
                }
            }

            private string ManifestResponse(CommandSpec spec)
            {
                var nodes = new List<string>();
                foreach (string argument in spec.ArgumentList ?? Array.Empty<string>())
                {
                    if (!argument.StartsWith("ids[]=", StringComparison.Ordinal))
                        continue;

                    string nodeId = argument.Substring("ids[]=".Length);
                    string packageName = nodeId switch
                    {
                        "NODE-PERSONAL" => "com.parallel.personal",
                        "NODE-ALPHA-FIRST" => "com.parallel.alpha-first",
                        "NODE-ALPHA-SECOND" => "com.parallel.alpha-second",
                        "NODE-BETA" => "com.parallel.beta",
                        "NODE-GAMMA" => "com.parallel.gamma",
                        _ => string.Empty
                    };
                    string manifest =
                        "{\"name\":\"" + packageName +
                        "\",\"displayName\":\"" + packageName +
                        "\",\"version\":\"1.0.0\"}";
                    char manifestOidSeed = nodeId switch
                    {
                        "NODE-PERSONAL" => 'a',
                        "NODE-ALPHA-FIRST" => 'b',
                        "NODE-ALPHA-SECOND" => 'c',
                        "NODE-BETA" => 'd',
                        "NODE-GAMMA" => 'e',
                        _ => 'f'
                    };
                    nodes.Add(
                        "{\"id\":\"" + nodeId +
                        "\",\"packageManifest\":{" +
                        "\"__typename\":\"Blob\"," +
                        "\"oid\":\"" + new string(manifestOidSeed, 40) + "\"," +
                        "\"byteSize\":" + Encoding.UTF8.GetByteCount(manifest) + "," +
                        "\"isBinary\":false," +
                        "\"isTruncated\":false," +
                        "\"text\":" + QuoteJson(manifest) + "}}"
                    );

                    if (nodeId == "NODE-PERSONAL")
                        Volatile.Write(ref personalValidationCompleted, 1);
                }

                return
                    "{\"data\":{\"nodes\":[" + string.Join(",", nodes) +
                    "],\"rateLimit\":{\"remaining\":100," +
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
                return spec.Arguments ??
                       string.Join(" ", spec.ArgumentList ?? Array.Empty<string>());
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
