using System;
using System.Threading;

namespace MartinCalander.GitSubmoduleManager.Editor
{
    internal sealed class GitSubmoduleRemoveTaskState
    {
        internal GitOperationCompletionOutcome Outcome =
            GitOperationCompletionOutcome.FailedUnsafe;
        internal string Message = string.Empty;
        internal bool RemovedSuccessfully;
    }

    internal sealed class GitSubmoduleRemovalAssessmentTaskState
    {
        internal GitOperationCompletionOutcome Outcome =
            GitOperationCompletionOutcome.FailedUnsafe;
        internal string Message = string.Empty;
        internal SubmoduleRemovalAssessment Assessment;
    }

    internal sealed class GitSubmoduleRemovalAssessmentCompletion
    {
        internal bool Success;
        internal string Message = string.Empty;
        internal GitOperationCompletionOutcome Outcome;
        internal SubmoduleRemovalAssessment Assessment;
        internal CommandResult CommandResult;
    }

    internal sealed class GitSubmoduleRemoveCompletion
    {
        internal bool Success;
        internal string Message = string.Empty;
        internal GitOperationCompletionOutcome Outcome;
        internal CommandResult CommandResult;
    }

    /// <summary>
    /// Host-neutral removal workflow for Package Manager's management surfaces.
    /// Unity's embedded-package removal deletes the directory directly, so every
    /// submodule removal must instead pass through GitOperationService and Git's
    /// canonical git-rm operation with its safety and postcondition checks.
    /// </summary>
    internal static class GitSubmoduleRemoveService
    {
        internal static bool CanStart =>
            !GitOperationService.IsBusy &&
            !PackageManagerProjectResolutionService.IsBusy &&
            !PackageManagerReadOnlyGitInstallService.IsBusy &&
            !PackageManagerSubmoduleSnapshot.IsReaderActive &&
            !GitSubmoduleInstallProbe.IsReaderActive &&
            !AsyncCommandDrainRegistry.IsDraining &&
            string.IsNullOrWhiteSpace(GitOperationService.RecoveryWarning);

        internal static string ValidateInput(PackageManagerSubmoduleInfo info)
        {
            if (info == null)
                return "The installed Git submodule could not be identified.";

            if (!GitUtility.IsPackagePath(info.PackagePath))
            {
                return "Removal is limited to an exact Packages/<package-name> " +
                       "submodule path.";
            }

            string expectedPath = GitSubmoduleAddService.GetPackagePath(
                info.PackageName);
            if (!string.Equals(
                    GitUtility.NormalizePath(info.PackagePath),
                    GitUtility.NormalizePath(expectedPath),
                    StringComparison.Ordinal))
            {
                return "The installed package identity no longer matches its " +
                       "registered submodule path. Refresh Package Manager and retry.";
            }

            return string.Empty;
        }

        internal static bool TryStart(
            PackageManagerSubmoduleInfo info,
            Action<GitSubmoduleRemoveCompletion> onComplete,
            out string error)
        {
            return TryStart(
                info,
                null,
                false,
                onComplete,
                out error);
        }

        internal static bool TryStartAssessment(
            PackageManagerSubmoduleInfo info,
            Action<GitSubmoduleRemovalAssessmentCompletion> onComplete,
            out string error)
        {
            error = ValidateInput(info);
            if (!string.IsNullOrEmpty(error))
                return false;

            if (!CanStart)
            {
                error = BuildUnavailableMessage();
                return false;
            }

            string path = GitUtility.NormalizePath(info.PackagePath);
            string packageName = info.PackageName?.Trim() ?? string.Empty;
            var state = new GitSubmoduleRemovalAssessmentTaskState();
            return GitOperationService.TryStartTask(
                $"Inspecting {packageName} before removal...",
                cancellationToken => RunAssessmentTask(
                    path,
                    state,
                    cancellationToken),
                false,
                _ => state.Outcome,
                (result, effectiveOutcome) =>
                {
                    bool success =
                        effectiveOutcome == GitOperationCompletionOutcome.Succeeded &&
                        result != null &&
                        result.IsSuccess &&
                        state.Assessment != null &&
                        state.Outcome == GitOperationCompletionOutcome.Succeeded;
                    string message = success
                        ? string.Empty
                        : BuildCompletionError(
                            state.Message,
                            result,
                            effectiveOutcome);

                    onComplete?.Invoke(new GitSubmoduleRemovalAssessmentCompletion
                    {
                        Success = success,
                        Message = message,
                        Outcome = effectiveOutcome,
                        Assessment = success
                            ? state.Assessment.CreateSnapshot()
                            : null,
                        CommandResult = result
                    });
                },
                out error,
                new GitOperationMetadata
                {
                    PackagePath = path,
                    Phase = "inspect-before-remove"
                });
        }

        internal static bool TryStart(
            PackageManagerSubmoduleInfo info,
            SubmoduleRemovalAssessment confirmedAssessment,
            bool discardLocalWork,
            Action<GitSubmoduleRemoveCompletion> onComplete,
            out string error)
        {
            error = ValidateInput(info);
            if (!string.IsNullOrEmpty(error))
                return false;

            string path = GitUtility.NormalizePath(info.PackagePath);
            error = ValidateConfirmation(
                path,
                confirmedAssessment,
                discardLocalWork);
            if (!string.IsNullOrEmpty(error))
                return false;

            if (!CanStart)
            {
                error = BuildUnavailableMessage();
                return false;
            }

            string packageName = info.PackageName?.Trim() ?? string.Empty;
            SubmoduleRemovalAssessment confirmationSnapshot =
                confirmedAssessment?.CreateSnapshot();
            var state = new GitSubmoduleRemoveTaskState();
            return GitOperationService.TryStartTask(
                $"Removing {packageName}...",
                cancellationToken => RunRemoveSubmoduleTask(
                    path,
                    confirmationSnapshot,
                    discardLocalWork,
                    state,
                    cancellationToken),
                true,
                _ => state.Outcome,
                (result, effectiveOutcome) =>
                {
                    bool success =
                        effectiveOutcome == GitOperationCompletionOutcome.Succeeded &&
                        result != null &&
                        result.IsSuccess &&
                        state.RemovedSuccessfully &&
                        state.Outcome == GitOperationCompletionOutcome.Succeeded;
                    string message = success
                        ? state.Message
                        : BuildCompletionError(
                            state.Message,
                            result,
                            effectiveOutcome);
                    if (success &&
                        !string.IsNullOrWhiteSpace(result?.CompletionWarning))
                    {
                        message = string.IsNullOrWhiteSpace(message)
                            ? result.CompletionWarning
                            : message.TrimEnd() + " " +
                              result.CompletionWarning.Trim();
                    }

                    if (result != null &&
                        result.TerminationConfirmed &&
                        effectiveOutcome != GitOperationCompletionOutcome.FailedUnsafe)
                    {
                        PackageManagerSubmoduleSnapshot.Refresh();
                    }

                    onComplete?.Invoke(new GitSubmoduleRemoveCompletion
                    {
                        Success = success,
                        Message = message,
                        Outcome = effectiveOutcome,
                        CommandResult = result
                    });
                },
                out error,
                new GitOperationMetadata
                {
                    PackagePath = path,
                    Phase = "remove",
                    PackageName = packageName,
                    PackageResolutionExpectation =
                        PackageManagerResolutionExpectation.Absent
                });
        }

        internal static CommandResult RunRemoveSubmoduleTask(
            string path,
            GitSubmoduleRemoveTaskState state,
            CancellationToken cancellationToken)
        {
            return RunRemoveSubmoduleTask(
                path,
                null,
                false,
                state,
                cancellationToken);
        }

        internal static CommandResult RunAssessmentTask(
            string path,
            GitSubmoduleRemovalAssessmentTaskState state,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            bool assessed = GitUtility.TryAssessSubmoduleRemoval(
                path,
                out SubmoduleRemovalAssessment assessment,
                out string assessmentError,
                cancellationToken);
            state.Assessment = assessed ? assessment?.CreateSnapshot() : null;
            state.Outcome = assessed && state.Assessment != null
                ? GitOperationCompletionOutcome.Succeeded
                : GitOperationCompletionOutcome.FailedButRolledBack;
            state.Message = assessmentError ?? string.Empty;

            bool terminationUnconfirmed =
                GitUtility.ConsumeUnconfirmedCommandTermination();
            return new CommandResult
            {
                ExitCode = assessed && state.Assessment != null ? 0 : -1,
                StdOut = string.Empty,
                StdErr = assessed && state.Assessment != null
                    ? string.Empty
                    : string.IsNullOrWhiteSpace(assessmentError)
                        ? "The Git submodule could not be inspected safely."
                        : assessmentError,
                TerminationConfirmed = !terminationUnconfirmed
            };
        }

        internal static CommandResult RunRemoveSubmoduleTask(
            string path,
            SubmoduleRemovalAssessment confirmedAssessment,
            bool discardLocalWork,
            GitSubmoduleRemoveTaskState state,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            bool removed = GitUtility.TryRemoveSubmodule(
                path,
                confirmedAssessment,
                discardLocalWork,
                out string removeError,
                out GitOperationCompletionOutcome outcome,
                cancellationToken);
            state.Outcome = outcome;
            state.Message = removeError ?? string.Empty;
            state.RemovedSuccessfully =
                removed && outcome == GitOperationCompletionOutcome.Succeeded;

            bool terminationUnconfirmed =
                GitUtility.ConsumeUnconfirmedCommandTermination();
            if (state.RemovedSuccessfully)
            {
                state.Message = "Git submodule removed. Review and commit the " +
                                "parent repository changes.";
                return new CommandResult
                {
                    ExitCode = 0,
                    StdOut = state.Message,
                    StdErr = string.Empty,
                    TerminationConfirmed = !terminationUnconfirmed
                };
            }

            return new CommandResult
            {
                ExitCode = -1,
                StdOut = string.Empty,
                StdErr = string.IsNullOrWhiteSpace(removeError)
                    ? "The Git submodule could not be removed safely."
                    : removeError,
                TerminationConfirmed = !terminationUnconfirmed
            };
        }

        internal static string BuildUnavailableMessage()
        {
            if (PackageManagerProjectResolutionService.IsBusy)
                return PackageManagerProjectResolutionService.BuildUnavailableMessage();

            string recoveryWarning = GitOperationService.RecoveryWarning;
            if (!string.IsNullOrWhiteSpace(recoveryWarning))
                return recoveryWarning.Trim();

            string activeLabel = GitOperationService.ActiveLabel;
            if (GitOperationService.IsBusy && !string.IsNullOrWhiteSpace(activeLabel))
                return $"Wait for {activeLabel.Trim()} to finish.";

            return "Wait for the current package scan or repository operation to finish.";
        }

        private static string ValidateConfirmation(
            string path,
            SubmoduleRemovalAssessment confirmedAssessment,
            bool discardLocalWork)
        {
            if (confirmedAssessment == null)
            {
                return discardLocalWork
                    ? "Inspect the submodule and explicitly confirm that exact assessment before discarding local work."
                    : string.Empty;
            }

            if (!string.Equals(
                    GitUtility.NormalizePath(confirmedAssessment.Path),
                    path,
                    StringComparison.Ordinal))
            {
                return "The removal assessment belongs to a different package. Inspect the selected submodule again.";
            }

            if (confirmedAssessment.HasUnverifiedWorktreeContents)
            {
                return "The package directory contains unverified files that the Unity UI will never discard. " +
                       "Move them to safety and leave the package directory empty before removing the gitlink.";
            }

            if (!confirmedAssessment.IsSafe && !discardLocalWork)
            {
                string warning = confirmedAssessment.BuildWarning();
                return (string.IsNullOrWhiteSpace(warning)
                            ? "The submodule contains uncommitted work."
                            : warning) +
                       " Explicit confirmation is required before discarding it.";
            }

            return string.Empty;
        }

        private static string BuildCompletionError(
            string stateMessage,
            CommandResult result,
            GitOperationCompletionOutcome outcome)
        {
            string message = !string.IsNullOrWhiteSpace(stateMessage)
                ? stateMessage.Trim()
                : result?.StdErr?.Trim();
            if (string.IsNullOrWhiteSpace(message))
                message = "The remove operation did not complete successfully.";

            if (outcome == GitOperationCompletionOutcome.FailedUnsafe &&
                message.IndexOf("recovery", StringComparison.OrdinalIgnoreCase) < 0)
            {
                message += " Inspect the recovery warning before starting " +
                           "another repository operation.";
            }

            return message;
        }
    }
}
