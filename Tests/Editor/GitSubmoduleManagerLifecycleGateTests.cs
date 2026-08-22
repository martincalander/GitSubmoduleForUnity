using System.Threading;
using NUnit.Framework;

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
    }
}
