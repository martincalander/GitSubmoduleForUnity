using NUnit.Framework;

namespace MartinCalander.GitSubmoduleManager.Editor.Tests
{
    public sealed class GitOperationServiceCompletionTests
    {
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
    }
}
