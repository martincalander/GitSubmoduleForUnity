using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using NUnit.Framework;
using UnityEngine;

namespace MartinCalander.GitSubmoduleManager.Editor.Tests
{
    public sealed class GitOperationServiceCompletionTests
    {
        private string journalTestDirectory;

        [SetUp]
        public void SetUp()
        {
            journalTestDirectory = Path.Combine(
                GitUtility.ProjectRoot,
                "Library",
                "GitSubmoduleManager",
                "JournalTests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(journalTestDirectory);
        }

        [TearDown]
        public void TearDown()
        {
            if (!string.IsNullOrWhiteSpace(journalTestDirectory) &&
                Directory.Exists(journalTestDirectory))
            {
                Directory.Delete(journalTestDirectory, true);
            }
        }

        [Test]
        public void ApplyFinalizationSafety_PreservesOutcomeWhenFinalizationIsSafe()
        {
            foreach (GitOperationCompletionOutcome resolvedOutcome in new[]
                     {
                         GitOperationCompletionOutcome.Succeeded,
                         GitOperationCompletionOutcome.FailedButRolledBack,
                         GitOperationCompletionOutcome.FailedUnsafe
                     })
            {
                Assert.That(
                    GitOperationService.ApplyFinalizationSafety(resolvedOutcome, true),
                    Is.EqualTo(resolvedOutcome));
            }
        }

        [Test]
        public void ApplyFinalizationSafety_DowngradesOtherwiseSafeOutcome()
        {
            foreach (GitOperationCompletionOutcome resolvedOutcome in new[]
                     {
                         GitOperationCompletionOutcome.Succeeded,
                         GitOperationCompletionOutcome.FailedButRolledBack
                     })
            {
                Assert.That(
                    GitOperationService.ApplyFinalizationSafety(resolvedOutcome, false),
                    Is.EqualTo(GitOperationCompletionOutcome.FailedUnsafe));
            }
        }

        [Test]
        public void AutoRefreshFinalization_AlwaysRestoresOwnedSuppression()
        {
            foreach ((GitOperationCompletionOutcome outcome, bool expectedRefresh) in
                     new[]
                     {
                         (GitOperationCompletionOutcome.Succeeded, true),
                         (GitOperationCompletionOutcome.FailedButRolledBack, true),
                         (GitOperationCompletionOutcome.FailedUnsafe, false)
                     })
            {
                GitOperationService.GetAutoRefreshFinalizationPlan(
                    true,
                    outcome,
                    out bool shouldRestore,
                    out bool shouldRefresh);

                Assert.That(shouldRestore, Is.True,
                    "Every terminal outcome must balance AssetDatabase suppression.");
                Assert.That(shouldRefresh, Is.EqualTo(expectedRefresh),
                    "Only verified safe outcomes may force an asset import.");
            }
        }

        [Test]
        public void AutoRefreshFinalization_DoesNotRestoreUnownedSuppression()
        {
            GitOperationService.GetAutoRefreshFinalizationPlan(
                false,
                GitOperationCompletionOutcome.FailedUnsafe,
                out bool shouldRestore,
                out bool shouldRefresh);

            Assert.That(shouldRestore, Is.False);
            Assert.That(shouldRefresh, Is.False);
        }

        [Test]
        public void AutoRefreshFinalization_SuccessBalancesOnceAndClearsRecoveryState()
        {
            int allowCount = 0;
            var markerStates = new List<bool>();
            var journalStates = new List<bool>();

            bool safe = GitOperationService.TryFinalizeAutoRefreshSuppression(
                () => allowCount++,
                markerStates.Add,
                journalStates.Add,
                out bool restored,
                out string error);

            Assert.That(safe, Is.True, error);
            Assert.That(restored, Is.True);
            Assert.That(allowCount, Is.EqualTo(1));
            Assert.That(markerStates, Is.EqualTo(new[] { false }));
            Assert.That(journalStates, Is.EqualTo(new[] { false }));
        }

        [Test]
        public void AutoRefreshFinalization_ExceptionCallsOnceAndRetainsRecoveryState()
        {
            int allowCount = 0;
            var markerStates = new List<bool>();
            var journalStates = new List<bool>();

            bool safe = GitOperationService.TryFinalizeAutoRefreshSuppression(
                () =>
                {
                    allowCount++;
                    throw new InvalidOperationException("allow failed");
                },
                markerStates.Add,
                journalStates.Add,
                out bool restored,
                out string error);

            Assert.That(safe, Is.False);
            Assert.That(restored, Is.False);
            Assert.That(allowCount, Is.EqualTo(1));
            Assert.That(markerStates, Is.EqualTo(new[] { true }));
            Assert.That(journalStates, Is.EqualTo(new[] { true }));
            Assert.That(error, Does.Contain("allow failed"));
        }

        [Test]
        public void AutoRefreshFinalization_MarkerFailureDowngradesAndStillUpdatesJournal()
        {
            int allowCount = 0;
            var journalStates = new List<bool>();

            bool safe = GitOperationService.TryFinalizeAutoRefreshSuppression(
                () => allowCount++,
                _ => throw new InvalidOperationException("marker write failed"),
                journalStates.Add,
                out bool restored,
                out string error);

            Assert.That(safe, Is.False);
            Assert.That(restored, Is.True,
                "The native suppression was balanced even though its recovery bookkeeping failed.");
            Assert.That(allowCount, Is.EqualTo(1));
            Assert.That(journalStates, Is.EqualTo(new[] { false }));
            Assert.That(error, Does.Contain("marker write failed"));
        }

        [Test]
        public void AutoRefreshFinalization_JournalFailureDowngradesAndClearsMarker()
        {
            int allowCount = 0;
            var markerStates = new List<bool>();

            bool safe = GitOperationService.TryFinalizeAutoRefreshSuppression(
                () => allowCount++,
                markerStates.Add,
                _ => throw new InvalidOperationException("journal write failed"),
                out bool restored,
                out string error);

            Assert.That(safe, Is.False);
            Assert.That(restored, Is.True,
                "The native suppression was balanced even though its recovery bookkeeping failed.");
            Assert.That(allowCount, Is.EqualTo(1));
            Assert.That(markerStates, Is.EqualTo(new[] { false }));
            Assert.That(error, Does.Contain("journal write failed"));
        }

        [Test]
        public void BuildEffectiveCompletionResult_UnsafeOutcomeCannotLookSuccessful()
        {
            var original = new CommandResult
            {
                ExitCode = 0,
                StdOut = "completed",
                CompletionWarning = "package resolve pending",
                TerminationConfirmed = true
            };

            CommandResult effective = GitOperationService.BuildEffectiveCompletionResult(
                original,
                GitOperationCompletionOutcome.FailedUnsafe);

            Assert.That(effective, Is.Not.SameAs(original));
            Assert.That(effective.IsSuccess, Is.False);
            Assert.That(effective.TerminationConfirmed, Is.True);
            Assert.That(
                effective.CompletionWarning,
                Is.EqualTo(original.CompletionWarning));
            Assert.That(effective.StdErr, Does.Contain("could not be finalized safely"));
            Assert.That(
                effective.StdErr.Length,
                Is.GreaterThan("The repository operation could not be finalized safely.".Length));
        }

        [Test]
        public void NotifyCompletion_PublishesEffectiveFailureResultAndOutcome()
        {
            CommandResult receivedResult = null;
            GitOperationCompletionOutcome receivedOutcome = GitOperationCompletionOutcome.Succeeded;
            var original = new CommandResult { ExitCode = 0, TerminationConfirmed = true };

            GitOperationService.NotifyCompletion(
                original,
                GitOperationCompletionOutcome.FailedUnsafe,
                (result, outcome) =>
                {
                    receivedResult = result;
                    receivedOutcome = outcome;
                });

            Assert.That(receivedResult, Is.Not.SameAs(original));
            Assert.That(receivedResult.IsSuccess, Is.False);
            Assert.That(receivedOutcome, Is.EqualTo(GitOperationCompletionOutcome.FailedUnsafe));
        }

        [Test]
        public void JournalSnapshot_ReadsOneBoundedRegularStrictUtf8File()
        {
            string path = JournalPath("valid");
            GitOperationJournal expected = CreateJournal("valid");
            WriteJournal(path, expected);

            bool read = GitOperationService.TryReadJournalForTests(
                path,
                out GitOperationJournal actual,
                out string error);

            Assert.That(read, Is.True, error);
            Assert.That(actual.operationId, Is.EqualTo(expected.operationId));
            Assert.That(actual.state, Is.EqualTo(expected.state));
        }

        [Test]
        public void JournalSnapshot_RejectsInvalidUtf8()
        {
            string path = JournalPath("invalid-utf8");
            File.WriteAllBytes(path, new byte[] { 0x7b, 0x22, 0xff, 0x22, 0x7d });

            bool read = GitOperationService.TryReadJournalForTests(
                path,
                out _,
                out string error);

            Assert.That(read, Is.False);
            Assert.That(error, Does.Contain("valid UTF-8"));
        }

        [Test]
        public void JournalSnapshot_RejectsMoreThanSixtyFourKibibytes()
        {
            string path = JournalPath("oversized");
            File.WriteAllBytes(path, new byte[(64 * 1024) + 1]);

            bool read = GitOperationService.TryReadJournalForTests(
                path,
                out _,
                out string error);

            Assert.That(read, Is.False);
            Assert.That(error, Does.Contain("safety size limit"));
        }

        [Test]
        public void JournalSnapshot_RejectsSymbolicLink()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                Assert.Ignore(
                    "Creating an unprivileged symbolic link is not portable on Windows test hosts.");
            }

            string target = JournalPath("symlink-target");
            string link = JournalPath("symlink");
            WriteJournal(target, CreateJournal("symlink-target"));
            CommandResult linkResult = CliCommandRunner.Run(
                "/bin/ln",
                "-s -- " + GitUtility.Quote(target) + " " + GitUtility.Quote(link),
                journalTestDirectory,
                5000);
            if (!linkResult.IsSuccess)
                Assert.Ignore("The test host could not create a symbolic link: " + linkResult.StdErr);

            bool read = GitOperationService.TryReadJournalForTests(
                link,
                out _,
                out string error);

            Assert.That(read, Is.False);
            Assert.That(error, Does.Contain("symbolic link"));
            Assert.That(File.Exists(target), Is.True);
        }

        [Test]
        public void JournalSnapshot_RejectsNamedPipeWithoutBlocking()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                Assert.Ignore(
                    "Portable unprivileged named-pipe filesystem entries are not available on Windows test hosts.");
            }

            string path = JournalPath("named-pipe");
            CommandResult pipeResult = CliCommandRunner.Run(
                "/usr/bin/mkfifo",
                "-- " + GitUtility.Quote(path),
                journalTestDirectory,
                5000);
            if (!pipeResult.IsSuccess)
                Assert.Ignore("The test host could not create a named pipe: " + pipeResult.StdErr);

            bool read = GitOperationService.TryReadJournalForTests(
                path,
                out _,
                out string error);

            Assert.That(read, Is.False);
            Assert.That(error, Does.Contain("regular file"));
        }

        [Test]
        public void JournalReplacement_QuarantinesSameIdentityContentRace()
        {
            string path = JournalPath("replace-race");
            string operationId = Guid.NewGuid().ToString("N");
            GitOperationJournal initial = CreateJournal("initial", operationId);
            GitOperationJournal raced = CreateJournal("raced", operationId);
            GitOperationJournal replacement = CreateJournal("replacement", operationId);
            WriteJournal(path, initial);
            byte[] racedContents = SerializeJournal(raced);

            bool replaced = GitOperationService.TryReplaceJournalForTests(
                path,
                replacement,
                operationId,
                () => File.WriteAllBytes(path, racedContents),
                out string error);

            Assert.That(replaced, Is.False);
            Assert.That(error, Does.Contain("changed at its atomic replacement boundary"));
            string[] recoveryFiles = Directory.GetFiles(
                journalTestDirectory,
                "*.replaced.recovery");
            Assert.That(recoveryFiles, Has.Length.EqualTo(1));
            Assert.That(File.ReadAllBytes(recoveryFiles[0]), Is.EqualTo(racedContents));
        }

        [Test]
        public void JournalDeletion_RestoresSameIdentityContentRace()
        {
            string path = JournalPath("delete-race");
            string operationId = Guid.NewGuid().ToString("N");
            GitOperationJournal initial = CreateJournal("initial", operationId);
            GitOperationJournal raced = CreateJournal("raced", operationId);
            WriteJournal(path, initial);
            byte[] racedContents = SerializeJournal(raced);

            bool deleted = GitOperationService.TryDeleteJournalForTests(
                path,
                operationId,
                () => File.WriteAllBytes(path, racedContents),
                out string error);

            Assert.That(deleted, Is.False);
            Assert.That(error, Does.Contain("changed at its atomic removal boundary"));
            Assert.That(File.ReadAllBytes(path), Is.EqualTo(racedContents));
            Assert.That(error, Does.Contain("restored"));
        }

        [Test]
        public void JournalDeletion_RestoresOversizedRaceWithoutReadingPastBound()
        {
            string path = JournalPath("delete-growth-race");
            GitOperationJournal initial = CreateJournal("initial");
            WriteJournal(path, initial);
            byte[] racedContents = new byte[(64 * 1024) + 1];

            bool deleted = GitOperationService.TryDeleteJournalForTests(
                path,
                initial.operationId,
                () => File.WriteAllBytes(path, racedContents),
                out string error);

            Assert.That(deleted, Is.False);
            Assert.That(error, Does.Contain("safety size limit"));
            Assert.That(new FileInfo(path).Length, Is.EqualTo(racedContents.Length));
        }

        [Test]
        public void JournalDeletion_RemovesOnlyExactSnapshot()
        {
            string path = JournalPath("delete-exact");
            GitOperationJournal initial = CreateJournal("initial");
            WriteJournal(path, initial);
            byte[] initialContents = File.ReadAllBytes(path);

            bool deleted = GitOperationService.TryDeleteJournalForTests(
                path,
                initial.operationId,
                null,
                out string error);

            Assert.That(deleted, Is.True, error);
            Assert.That(File.Exists(path), Is.False);
            string[] recoveryFiles = Directory.GetFiles(
                journalTestDirectory,
                "*.deleted.recovery");
            Assert.That(recoveryFiles, Has.Length.EqualTo(1));
            Assert.That(File.ReadAllBytes(recoveryFiles[0]), Is.EqualTo(initialContents));
        }

        [Test]
        public void JournalDeletion_PreservesLateWriterAtClosingBoundary()
        {
            string path = JournalPath("delete-closing-race");
            GitOperationJournal initial = CreateJournal("initial");
            WriteJournal(path, initial);
            byte[] lateContents = SerializeJournal(
                CreateJournal("late-writer", initial.operationId));
            string quarantinedPath = string.Empty;

            bool deleted =
                GitOperationService.TryDeleteJournalAtClosingBoundaryForTests(
                    path,
                    initial.operationId,
                    recoveryPath =>
                    {
                        quarantinedPath = recoveryPath;
                        File.WriteAllBytes(recoveryPath, lateContents);
                    },
                    out string error);

            Assert.That(deleted, Is.False);
            Assert.That(error, Does.Contain("closing removal boundary"));
            Assert.That(error, Does.Contain("remains preserved"));
            Assert.That(File.Exists(path), Is.False);
            Assert.That(File.ReadAllBytes(quarantinedPath), Is.EqualTo(lateContents));
        }

        private string JournalPath(string name)
        {
            return Path.Combine(journalTestDirectory, name + ".json");
        }

        private static GitOperationJournal CreateJournal(
            string state,
            string operationId = null)
        {
            string timestamp = DateTime.UtcNow.ToString("O");
            return new GitOperationJournal
            {
                operationId = operationId ?? Guid.NewGuid().ToString("N"),
                label = "Journal safety test",
                packagePath = "Packages/com.example.journal-test",
                phase = "test",
                startCommit = string.Empty,
                state = state,
                startedUtc = timestamp,
                updatedUtc = timestamp,
                autoRefreshSuppressed = false
            };
        }

        private static void WriteJournal(
            string path,
            GitOperationJournal journal)
        {
            File.WriteAllBytes(path, SerializeJournal(journal));
        }

        private static byte[] SerializeJournal(GitOperationJournal journal)
        {
            return new UTF8Encoding(false, true).GetBytes(
                JsonUtility.ToJson(journal, true));
        }
    }
}
