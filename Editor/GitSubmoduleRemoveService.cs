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

    internal sealed class GitSubmoduleRemoveCompletion
    {
        internal bool Success;
        internal string Message = string.Empty;
        internal GitOperationCompletionOutcome Outcome;
        internal CommandResult CommandResult;
    }

    /// <summary>
    /// Host-neutral removal workflow for Package Manager and the legacy window.
    /// Unity's embedded-package removal deletes the directory directly, so every
    /// submodule removal must instead pass through GitOperationService and Git's
    /// canonical git-rm operation with its safety and postcondition checks.
    /// </summary>
    internal static class GitSubmoduleRemoveService
    {
        internal static bool CanStart =>
            !GitOperationService.IsBusy &&
            !PackageManagerSubmoduleSnapshot.IsReaderActive &&
            !GitSubmoduleInstallProbe.IsReaderActive &&
            !AsyncCommandDrainRegistry.IsDraining &&
            !GitSubmoduleManagerView.AreBackgroundLoadsDraining &&
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
            var state = new GitSubmoduleRemoveTaskState();
            return GitOperationService.TryStartTask(
                $"Removing {packageName}...",
                cancellationToken => RunRemoveSubmoduleTask(
                    path,
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
                    Phase = "remove"
                });
        }

        internal static CommandResult RunRemoveSubmoduleTask(
            string path,
            GitSubmoduleRemoveTaskState state,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            bool removed = GitUtility.TryRemoveSubmodule(
                path,
                null,
                false,
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
            string recoveryWarning = GitOperationService.RecoveryWarning;
            if (!string.IsNullOrWhiteSpace(recoveryWarning))
                return recoveryWarning.Trim();

            string activeLabel = GitOperationService.ActiveLabel;
            if (GitOperationService.IsBusy && !string.IsNullOrWhiteSpace(activeLabel))
                return $"Wait for {activeLabel.Trim()} to finish.";

            return "Wait for the current package scan or repository operation to finish.";
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
