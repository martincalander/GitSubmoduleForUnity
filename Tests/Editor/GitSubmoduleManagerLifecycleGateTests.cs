using System.Threading;
using NUnit.Framework;
using UnityEngine;
using GitSubmoduleManagerView = MartinCalander.GitSubmoduleManager.Editor.GitSubmoduleManagerView;

namespace MartinCalander.GitSubmoduleManager.Editor.Tests
{
    [Parallelizable(ParallelScope.None)]
    public sealed class GitSubmoduleManagerLifecycleGateTests
    {
        [Test]
        public void GitHubAuthentication_BlocksGitHubInteractionsButNotRepositoryActions()
        {
            bool repositoryBusy = GitSubmoduleManagerView.IsRepositoryOperationBusyState(
                operationExecutionBusy: false,
                deferredMutationPending: false);
            bool gitHubBusy = GitSubmoduleManagerView.IsGitHubInteractionBusyState(
                repositoryBusy,
                authenticationInProgress: true);

            Assert.That(repositoryBusy, Is.False,
                "GitHub authentication must not disable Git-only package operations.");
            Assert.That(gitHubBusy, Is.True,
                "GitHub and authentication actions must remain mutually exclusive.");
        }

        [TestCase(false, false, false, false)]
        [TestCase(true, false, false, true)]
        [TestCase(false, true, false, true)]
        [TestCase(false, false, true, true)]
        [TestCase(true, true, true, true)]
        public void SharedGitHubAuthenticationGate_CoversEveryCrossWindowLifecycleState(
            bool activeOrAwaitingProcessing,
            bool retiredOrStopping,
            bool restartRequired,
            bool expected)
        {
            Assert.That(
                GitSubmoduleManagerView.IsSharedGitHubAuthenticationGateState(
                    activeOrAwaitingProcessing,
                    retiredOrStopping,
                    restartRequired),
                Is.EqualTo(expected));
        }

        [TestCase(true, false, false, false, true)]
        [TestCase(false, false, false, false, false)]
        [TestCase(true, true, false, false, false)]
        [TestCase(true, false, true, false, false)]
        [TestCase(true, false, false, true, false)]
        public void GitHubAuthenticationStart_WaitsForSharedStateAndBackgroundReaders(
            bool ghAvailable,
            bool repositoryOperationBusy,
            bool sharedAuthenticationBlocked,
            bool backgroundReadersActive,
            bool expected)
        {
            Assert.That(
                GitSubmoduleManagerView.CanStartGitHubAuthentication(
                    ghAvailable,
                    repositoryOperationBusy,
                    sharedAuthenticationBlocked,
                    backgroundReadersActive),
                Is.EqualTo(expected));
        }

        [Test]
        public void GitHubAuthenticationStart_WaitsForAnyActiveGitHubCliCommand()
        {
            Assert.That(
                GitSubmoduleManagerView.CanStartGitHubAuthentication(
                    ghAvailable: true,
                    repositoryOperationBusy: false,
                    sharedAuthenticationBlocked: false,
                    backgroundReadersActive: false,
                    gitHubCommandActive: true),
                Is.False);
        }

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
                CliCommandRunner.CurrentRunner = new BlockingGitHubRunner(started, release);
                handle = CliCommandRunner.RunAsync(
                    "gh",
                    new[] { "api", "user" },
                    ".");

                Assert.That(started.Wait(2000), Is.True, "The fake gh command did not start.");
                Assert.That(CliCommandRunner.HasActiveGitHubCommands, Is.True);
                Assert.That(CliCommandRunner.TryReserveGitHubAuthentication(), Is.False);
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

            Assert.That(CliCommandRunner.TryReserveGitHubAuthentication(), Is.True);
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

        private static void IgnoreWhenLivePackageManagerDiscoveryOwnsGitHubCommands()
        {
            if (PackageManagerGitHubDiscovery.IsStarted ||
                CliCommandRunner.HasActiveGitHubCommands ||
                CliCommandRunner.IsGitHubAuthenticationReserved ||
                CliCommandRunner.GitHubCommandRequiresEditorRestart ||
                AsyncCommandDrainRegistry.IsDraining)
            {
                Assert.Ignore(
                    "A live Package Manager GitHub discovery session owns the shared command gate.");
            }
        }

        [TestCase(false, true)]
        [TestCase(true, false)]
        public void InitialLoader_AlwaysRunsGitStageButDefersGitHubDuringSharedAuthentication(
            bool sharedAuthenticationBlocked,
            bool shouldRunGitHubStage)
        {
            Assert.That(
                GitSubmoduleManagerView.ShouldRunInitialGitHubStage(sharedAuthenticationBlocked),
                Is.EqualTo(shouldRunGitHubStage));
        }

        [TestCase(true, false, true)]
        [TestCase(false, true, true)]
        [TestCase(true, true, true)]
        [TestCase(false, false, false)]
        public void RepositoryOperationGate_PreservesWorkerMutualExclusion(
            bool operationExecutionBusy,
            bool deferredMutationPending,
            bool expected)
        {
            Assert.That(
                GitSubmoduleManagerView.IsRepositoryOperationBusyState(
                    operationExecutionBusy,
                    deferredMutationPending),
                Is.EqualTo(expected));
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
        public void DeferredWindowAction_RequiresAnEnabledLiveOwner()
        {
            var owner = new Texture2D(1, 1)
            {
                hideFlags = HideFlags.HideAndDontSave
            };
            try
            {
                Assert.That(
                    GitSubmoduleManagerView.CanEnterDeferredWindowAction(owner, windowEnabled: true),
                    Is.True);
                Assert.That(
                    GitSubmoduleManagerView.CanEnterDeferredWindowAction(owner, windowEnabled: false),
                    Is.False,
                    "OnDisable must invalidate callbacks before Unity destroys the window object.");

                Object.DestroyImmediate(owner);
                Assert.That(
                    GitSubmoduleManagerView.CanEnterDeferredWindowAction(owner, windowEnabled: true),
                    Is.False,
                    "Unity-destroyed owners must never enter delayed repository work.");
            }
            finally
            {
                if (owner != null)
                    Object.DestroyImmediate(owner);
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

    }
}
