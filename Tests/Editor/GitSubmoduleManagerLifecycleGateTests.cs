using System;
using System.Collections;
using System.Threading;
using NUnit.Framework;
using UnityEditor;
using UnityEngine.TestTools;

namespace MartinCalander.GitSubmoduleManager.Editor.Tests
{
    [Parallelizable(ParallelScope.None)]
    public sealed class GitSubmoduleManagerLifecycleGateTests
    {
        [TestCase(false, false, true)]
        [TestCase(true, false, false)]
        [TestCase(false, true, false)]
        [TestCase(true, true, false)]
        public void DiscoveryCommands_RespectAuthenticationAndDrainOwnership(
            bool sharedAuthenticationBlocked,
            bool commandsDraining,
            bool expected)
        {
            Assert.That(
                DiscoveryCoordinator.CanStartGitHubCommand(
                    sharedAuthenticationBlocked,
                    commandsDraining),
                Is.EqualTo(expected));
        }

        [Test]
        public void CliRunner_TracksActiveGitHubCommandsAcrossAsyncOwners()
        {
            IgnoreWhenLivePackageManagerDiscoveryOwnsGitHubCommands();

            ICommandRunner previousRunner = CliCommandRunner.CurrentRunner;
            using var started = new ManualResetEventSlim(false);
            using var release = new ManualResetEventSlim(false);
            AsyncCommandHandle handle = null;
            bool stopped = false;
            try
            {
                CliCommandRunner.CurrentRunner =
                    new BlockingGitHubRunner(started, release);
                handle = CliCommandRunner.RunAsync(
                    "gh",
                    new[] { "api", "user" },
                    ".");

                Assert.That(started.Wait(2000), Is.True);
                Assert.That(CliCommandRunner.HasActiveGitHubCommands, Is.True);
                Assert.That(
                    CliCommandRunner.TryReserveGitHubAuthentication(),
                    Is.False);
            }
            finally
            {
                release.Set();
                if (handle != null)
                    stopped = handle.WaitForCompletion(2000);
                CliCommandRunner.CurrentRunner = previousRunner;
            }

            Assert.That(stopped, Is.True);
            Assert.That(CliCommandRunner.HasActiveGitHubCommands, Is.False);
        }

        [Test]
        public void CliRunner_AuthenticationReservationIsAtomicWithGitHubCommandStarts()
        {
            IgnoreWhenLivePackageManagerDiscoveryOwnsGitHubCommands();

            Assert.That(
                CliCommandRunner.TryReserveGitHubAuthentication(),
                Is.True);
            try
            {
                CommandResult blocked = CliCommandRunner.Run(
                    "gh",
                    new[] { "api", "user" },
                    ".");

                Assert.That(blocked.IsSuccess, Is.False);
                Assert.That(blocked.TerminationConfirmed, Is.True);
                Assert.That(blocked.BlockedByGitHubAuthentication, Is.True);
                Assert.That(CliCommandRunner.HasActiveGitHubCommands, Is.False);

                bool available = GitHubUtility.IsGhAvailable(
                    CancellationToken.None,
                    out _,
                    out _,
                    out bool deferred);
                Assert.That(available, Is.False);
                Assert.That(deferred, Is.True);
            }
            finally
            {
                CliCommandRunner.ReleaseGitHubAuthenticationReservation();
            }
        }

        [Test]
        public void DiscoveryCoordinator_ObservesLiveAuthenticationReservation()
        {
            IgnoreWhenLivePackageManagerDiscoveryOwnsGitHubCommands();

            Assert.That(
                CliCommandRunner.TryReserveGitHubAuthentication(),
                Is.True);
            try
            {
                Assert.That(
                    DiscoveryCoordinator.CanStartGitHubCommandNow,
                    Is.False);
            }
            finally
            {
                CliCommandRunner.ReleaseGitHubAuthenticationReservation();
            }
        }

        [TestCase(false, false, false, false, false)]
        [TestCase(true, false, false, false, true)]
        [TestCase(false, true, false, false, true)]
        [TestCase(false, false, true, false, true)]
        [TestCase(false, false, false, true, true)]
        [TestCase(true, true, true, true, true)]
        public void RepositoryMutationGate_BlocksEveryPackageReaderOwner(
            bool managerReadersDraining,
            bool packageManagerSnapshotReaderActive,
            bool installProbeReaderActive,
            bool commandDrainActive,
            bool expected)
        {
            Assert.That(
                GitOperationService.ShouldBlockMutationForReaders(
                    managerReadersDraining,
                    packageManagerSnapshotReaderActive,
                    installProbeReaderActive,
                    commandDrainActive),
                Is.EqualTo(expected));
        }

        [Test]
        public void RepositoryGeneration_DefaultsToAdvancingOnlyForMutations()
        {
            GitOperationMetadata removalAssessment =
                GitSubmoduleRemoveService.CreateAssessmentOperationMetadata(
                    "Packages/com.example.package");

            Assert.That(
                GitOperationService.ShouldAdvanceRepositoryGeneration(
                    new GitOperationMetadata()),
                Is.True);
            Assert.That(
                GitOperationService.ShouldAdvanceRepositoryGeneration(null),
                Is.True);
            Assert.That(
                GitOperationService.ShouldAdvanceRepositoryGeneration(
                    removalAssessment),
                Is.False);
            Assert.That(
                removalAssessment.Phase,
                Is.EqualTo("inspect-before-remove"));
        }

        [UnityTest]
        public IEnumerator AssessedMutationHandoff_DefersQueuedRefreshUntilReservation()
        {
            IDisposable refreshDeferral = null;
            IDisposable secondRefreshDeferral = null;
            double availabilityDeadline =
                EditorApplication.timeSinceStartup + 10d;
            while (!GitSubmoduleRemoveService.CanStart)
            {
                Assert.That(
                    EditorApplication.timeSinceStartup,
                    Is.LessThan(availabilityDeadline),
                    "The repository assessment gate did not become available in time.");
                yield return null;
            }

            const string packageName = "com.example.handoff-generation-test";
            var info = new PackageManagerSubmoduleInfo(
                packageName,
                "Packages/" + packageName,
                GitUtility.ProjectRoot + "/Packages/" + packageName,
                "https://github.com/example/handoff-generation-test.git",
                true);
            long generationBeforeAssessment =
                GitOperationService.RepositoryGeneration;
            bool assessmentCompleted = false;

            bool assessmentStarted = PackageManagerGitHubNativeActions
                .TryStartMutationHandoffAssessment(
                    info,
                    (deferral, _) =>
                    {
                        refreshDeferral = deferral;
                        assessmentCompleted = true;
                    },
                    out string assessmentStartError);
            Assert.That(assessmentStarted, Is.True, assessmentStartError);

            try
            {
                double assessmentDeadline =
                    EditorApplication.timeSinceStartup + 10d;
                while (!assessmentCompleted)
                {
                    Assert.That(
                        EditorApplication.timeSinceStartup,
                        Is.LessThan(assessmentDeadline),
                        "The controlled removal assessment did not finish in time.");
                    yield return null;
                }

                Assert.That(refreshDeferral, Is.Not.Null);
                Assert.That(
                    PackageManagerSubmoduleSnapshot
                        .MutationHandoffRefreshDeferralCountForTests,
                    Is.EqualTo(1));
                Assert.That(GitOperationService.IsBusy, Is.False);
                Assert.That(
                    GitOperationService.RepositoryGeneration,
                    Is.EqualTo(generationBeforeAssessment),
                    "A read-only assessment must not announce a repository mutation.");

                Assert.That(
                    PackageManagerSubmoduleSnapshot
                        .TryDeferRefreshForMutationHandoff(
                            out secondRefreshDeferral),
                    Is.True);
                Assert.That(
                    PackageManagerSubmoduleSnapshot
                        .MutationHandoffRefreshDeferralCountForTests,
                    Is.EqualTo(2));

                PackageManagerSubmoduleSnapshot.Refresh();
                Assert.That(
                    PackageManagerSubmoduleSnapshot
                        .HasPendingRefreshRequestForTests,
                    Is.True);

                bool continuationRan = false;
                bool mutationStarted = false;
                bool mutationCompleted = false;
                bool deferralHeldInsideContinuation = false;
                bool refreshPendingAfterReservation = false;
                string mutationStartError = string.Empty;
                PackageManagerGitHubNativeActions.ScheduleMutationHandoff(
                    () =>
                    {
                        continuationRan = true;
                        deferralHeldInsideContinuation =
                            PackageManagerSubmoduleSnapshot
                                .MutationHandoffRefreshDeferralCountForTests == 2;
                        mutationStarted = GitOperationService.TryStartTask(
                            "Testing confirmed mutation handoff...",
                            _ => new CommandResult
                            {
                                ExitCode = 0,
                                StdOut = string.Empty,
                                StdErr = string.Empty,
                                TerminationConfirmed = true
                            },
                            false,
                            _ => GitOperationCompletionOutcome.Succeeded,
                            (_, __) => mutationCompleted = true,
                            out mutationStartError,
                            new GitOperationMetadata
                            {
                                PackagePath = info.PackagePath,
                                Phase = "test-confirmed-mutation"
                            });
                        refreshPendingAfterReservation =
                            PackageManagerSubmoduleSnapshot
                                .HasPendingRefreshRequestForTests;
                    },
                    refreshDeferral);

                double continuationDeadline =
                    EditorApplication.timeSinceStartup + 10d;
                while (!continuationRan)
                {
                    Assert.That(
                        EditorApplication.timeSinceStartup,
                        Is.LessThan(continuationDeadline),
                        "The delayed mutation handoff did not run in time.");
                    yield return null;
                }

                Assert.That(deferralHeldInsideContinuation, Is.True);
                Assert.That(mutationStarted, Is.True, mutationStartError);
                Assert.That(
                    PackageManagerSubmoduleSnapshot
                        .MutationHandoffRefreshDeferralCountForTests,
                    Is.EqualTo(1),
                    "The handoff must release its token immediately after reservation.");
                refreshDeferral.Dispose();
                Assert.That(
                    PackageManagerSubmoduleSnapshot
                        .MutationHandoffRefreshDeferralCountForTests,
                    Is.EqualTo(1),
                    "Disposing one handoff token twice must not release another token.");
                Assert.That(
                    GitOperationService.RepositoryGeneration,
                    Is.EqualTo(unchecked(generationBeforeAssessment + 1L)),
                    "The confirmed mutation must announce its reservation.");
                Assert.That(
                    refreshPendingAfterReservation,
                    Is.True,
                    "The queued refresh must remain blocked while the mutation owns the repository.");

                double completionDeadline =
                    EditorApplication.timeSinceStartup + 10d;
                while (!mutationCompleted || GitOperationService.IsBusy)
                {
                    Assert.That(
                        EditorApplication.timeSinceStartup,
                        Is.LessThan(completionDeadline),
                        "The controlled mutation did not finish in time.");
                    yield return null;
                }

                Assert.That(GitOperationService.IsBusy, Is.False);
                Assert.That(
                    PackageManagerSubmoduleSnapshot
                        .HasPendingRefreshRequestForTests,
                    Is.True,
                    "The second token must keep the queued refresh deferred.");
                Assert.That(
                    PackageManagerSubmoduleSnapshot.IsReaderActive,
                    Is.False);

                secondRefreshDeferral.Dispose();
                Assert.That(
                    PackageManagerSubmoduleSnapshot
                        .MutationHandoffRefreshDeferralCountForTests,
                    Is.Zero);

                double refreshDeadline =
                    EditorApplication.timeSinceStartup + 10d;
                while (PackageManagerSubmoduleSnapshot.IsReaderActive ||
                       PackageManagerSubmoduleSnapshot
                           .HasPendingRefreshRequestForTests)
                {
                    Assert.That(
                        EditorApplication.timeSinceStartup,
                        Is.LessThan(refreshDeadline),
                        "The queued snapshot refresh did not finish in time.");
                    yield return null;
                }
            }
            finally
            {
                refreshDeferral?.Dispose();
                secondRefreshDeferral?.Dispose();
            }
        }

        [UnityTest]
        public IEnumerator RepositoryFollowUp_RunsBeforeReloadUnlockAndCompletion()
        {
            double availabilityDeadline =
                EditorApplication.timeSinceStartup + 10d;
            while (!GitSubmoduleRemoveService.CanStart)
            {
                Assert.That(
                    EditorApplication.timeSinceStartup,
                    Is.LessThan(availabilityDeadline),
                    "The repository operation gate did not become available in time.");
                yield return null;
            }

            int sequence = 0;
            int followUpSequence = 0;
            int completionSequence = 0;
            bool busyDuringFollowUp = false;
            bool busyDuringCompletion = true;
            GitOperationCompletionOutcome followUpOutcome =
                GitOperationCompletionOutcome.FailedUnsafe;
            GitOperationCompletionOutcome completionOutcome =
                GitOperationCompletionOutcome.FailedUnsafe;

            bool started = GitOperationService.TryStartTask(
                "Testing the pre-reload follow-up...",
                _ => new CommandResult
                {
                    ExitCode = 0,
                    StdOut = string.Empty,
                    StdErr = string.Empty,
                    TerminationConfirmed = true
                },
                false,
                _ => GitOperationCompletionOutcome.Succeeded,
                (_, outcome) =>
                {
                    completionSequence = ++sequence;
                    busyDuringCompletion = GitOperationService.IsBusy;
                    completionOutcome = outcome;
                },
                out string startError,
                new GitOperationMetadata
                {
                    PackagePath = "Packages/com.example.pre-reload-follow-up",
                    Phase = "test-pre-reload-follow-up",
                    MayChangeRepository = false
                },
                (_, outcome) =>
                {
                    followUpSequence = ++sequence;
                    busyDuringFollowUp = GitOperationService.IsBusy;
                    followUpOutcome = outcome;
                });
            Assert.That(started, Is.True, startError);

            double completionDeadline =
                EditorApplication.timeSinceStartup + 10d;
            while (completionSequence == 0 || GitOperationService.IsBusy)
            {
                Assert.That(
                    EditorApplication.timeSinceStartup,
                    Is.LessThan(completionDeadline),
                    "The pre-reload follow-up operation did not finish in time.");
                yield return null;
            }

            Assert.That(followUpSequence, Is.EqualTo(1));
            Assert.That(completionSequence, Is.EqualTo(2));
            Assert.That(busyDuringFollowUp, Is.True);
            Assert.That(busyDuringCompletion, Is.False);
            Assert.That(
                followUpOutcome,
                Is.EqualTo(GitOperationCompletionOutcome.Succeeded));
            Assert.That(
                completionOutcome,
                Is.EqualTo(GitOperationCompletionOutcome.Succeeded));
        }

        [Test]
        public void MutationHandoff_ReleasesDeferralWhenContinuationThrows()
        {
            var deferral = new RecordingDisposable();

            Assert.Throws<InvalidOperationException>(
                () => PackageManagerGitHubNativeActions.RunMutationHandoff(
                    () => throw new InvalidOperationException("handoff failed"),
                    deferral));

            Assert.That(deferral.DisposeCount, Is.EqualTo(1));
        }

        [TestCase(false, false, true, false, false, true, true)]
        [TestCase(false, false, true, false, true, true, false)]
        [TestCase(false, false, true, true, false, true, false)]
        [TestCase(false, true, true, false, false, true, false)]
        [TestCase(false, false, false, false, false, true, false)]
        [TestCase(false, false, true, false, false, false, false)]
        [TestCase(true, false, true, false, false, true, false)]
        public void PackageManagerSnapshot_StartsOnlyOutsideMutationHandoff(
            bool shuttingDown,
            bool readerActive,
            bool hasPendingRequest,
            bool repositoryOperationBusy,
            bool mutationHandoffPending,
            bool retryDelayElapsed,
            bool expected)
        {
            Assert.That(
                PackageManagerSubmoduleSnapshot.ShouldStartRefresh(
                    shuttingDown,
                    readerActive,
                    hasPendingRequest,
                    repositoryOperationBusy,
                    mutationHandoffPending,
                    retryDelayElapsed),
                Is.EqualTo(expected));
        }

        [TestCase(false, 0, false, false, false, false, false)]
        [TestCase(false, 1, false, false, false, false, true)]
        [TestCase(false, 0, true, false, false, false, true)]
        [TestCase(false, 0, false, true, false, false, true)]
        [TestCase(false, 0, false, false, true, false, true)]
        [TestCase(false, 0, false, false, false, true, true)]
        [TestCase(true, 1, true, true, true, true, false)]
        public void PackageManagerSnapshot_UpdatesOnlyForHostsOrActiveWork(
            bool shuttingDown,
            int observerCount,
            bool readerActive,
            bool hasPendingResult,
            bool hasPendingRequest,
            bool mutationHandoffPending,
            bool expected)
        {
            Assert.That(
                PackageManagerSubmoduleSnapshot.ShouldKeepListening(
                    shuttingDown,
                    observerCount,
                    readerActive,
                    hasPendingResult,
                    hasPendingRequest,
                    mutationHandoffPending),
                Is.EqualTo(expected));
        }

        [TestCase(true, false, false, false, false, true)]
        [TestCase(false, false, false, false, false, false)]
        [TestCase(true, true, false, false, false, false)]
        [TestCase(true, false, true, false, false, false)]
        [TestCase(true, false, false, true, false, false)]
        [TestCase(true, false, false, false, true, false)]
        public void PackageManagerSnapshot_DestructiveContinuationRequiresCurrentSuccess(
            bool ready,
            bool readerActive,
            bool hasPendingResult,
            bool hasPendingRequest,
            bool hasError,
            bool expected)
        {
            Assert.That(
                PackageManagerSubmoduleSnapshot
                    .IsCurrentSuccessfulSnapshotState(
                        ready,
                        readerActive,
                        hasPendingResult,
                        hasPendingRequest,
                        hasError),
                Is.EqualTo(expected));
        }

        [TestCase(0, true, false, 1)]
        [TestCase(1, true, false, 2)]
        [TestCase(int.MaxValue, true, false, int.MaxValue)]
        [TestCase(2, false, false, 1)]
        [TestCase(1, false, false, 0)]
        [TestCase(0, false, false, 0)]
        [TestCase(4, true, true, 0)]
        [TestCase(4, false, true, 0)]
        public void PackageManagerSnapshot_ObserverRetainReleaseAndShutdownAreBounded(
            int observerCount,
            bool retain,
            bool shuttingDown,
            int expected)
        {
            Assert.That(
                PackageManagerSubmoduleSnapshot.TransitionHostObserverCount(
                    observerCount,
                    retain,
                    shuttingDown),
                Is.EqualTo(expected));
        }

        private static void IgnoreWhenLivePackageManagerDiscoveryOwnsGitHubCommands()
        {
            if (PackageManagerGitHubDiscovery.IsStarted ||
                CliCommandRunner.HasActiveGitHubCommands ||
                CliCommandRunner.IsGitHubAuthenticationReserved ||
                CliCommandRunner.GitHubCommandRequiresEditorRestart ||
                AsyncCommandDrainRegistry.IsDraining)
            {
                Assert.Ignore(
                    "A live Package Manager GitHub discovery session owns the " +
                    "shared command gate.");
            }
        }

        private sealed class BlockingGitHubRunner : ICommandRunner
        {
            private readonly ManualResetEventSlim started;
            private readonly ManualResetEventSlim release;

            internal BlockingGitHubRunner(
                ManualResetEventSlim started,
                ManualResetEventSlim release)
            {
                this.started = started;
                this.release = release;
            }

            public CommandResult Run(CommandSpec spec)
            {
                started.Set();
                release.Wait(2000);
                return new CommandResult
                {
                    ExitCode = 0,
                    StdOut = "{}",
                    StdErr = string.Empty,
                    TerminationConfirmed = true
                };
            }
        }

        private sealed class RecordingDisposable : IDisposable
        {
            internal int DisposeCount { get; private set; }

            public void Dispose()
            {
                DisposeCount++;
            }
        }
    }
}
