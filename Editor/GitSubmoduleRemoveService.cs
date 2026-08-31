using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using UnityEngine;

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

    internal sealed class GitSubmoduleBatchRemovalItem
    {
        internal PackageManagerSubmoduleInfo Info;
        internal SubmoduleRemovalAssessment ConfirmedAssessment;
        internal bool DiscardLocalWork;
    }

    internal sealed class GitSubmoduleBatchAssessmentTaskState
    {
        internal GitOperationCompletionOutcome Outcome =
            GitOperationCompletionOutcome.FailedUnsafe;
        internal string Message = string.Empty;
        internal readonly List<SubmoduleRemovalAssessment> Assessments =
            new List<SubmoduleRemovalAssessment>();
    }

    internal sealed class GitSubmoduleBatchAssessmentCompletion
    {
        internal bool Success;
        internal string Message = string.Empty;
        internal GitOperationCompletionOutcome Outcome;
        internal IReadOnlyList<SubmoduleRemovalAssessment> Assessments =
            Array.Empty<SubmoduleRemovalAssessment>();
        internal CommandResult CommandResult;
    }

    internal sealed class GitSubmoduleBatchRemoveTaskState
    {
        internal GitOperationCompletionOutcome Outcome =
            GitOperationCompletionOutcome.FailedUnsafe;
        internal string Message = string.Empty;
        internal int RemovedCount;
        internal readonly List<string> RemovedPackageNames = new List<string>();
    }

    internal sealed class GitSubmoduleBatchRemoveCompletion
    {
        internal bool Success;
        internal string Message = string.Empty;
        internal GitOperationCompletionOutcome Outcome;
        internal int RemovedCount;
        internal IReadOnlyList<string> RemovedPackageNames = Array.Empty<string>();
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
            CanStartAfterPackageSnapshot &&
            !PackageManagerSubmoduleSnapshot.IsReaderActive;

        internal static bool CanStartAfterPackageSnapshot =>
            !GitOperationService.IsBusy &&
            !PackageManagerProjectResolutionService.IsBusy &&
            !PackageManagerReadOnlyGitInstallService.IsBusy &&
            !PackageManagerNativeRemoveHandoffService.IsBusy &&
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

        internal static GitOperationMetadata CreateAssessmentOperationMetadata(
            string path)
        {
            return new GitOperationMetadata
            {
                PackagePath = path,
                Phase = "inspect-before-remove",
                MayChangeRepository = false
            };
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
                CreateAssessmentOperationMetadata(path));
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
                        PackageManagerResolutionExpectation.NotEmbedded
                });
        }

        internal static bool TryStartBatchAssessment(
            IReadOnlyList<PackageManagerSubmoduleInfo> infos,
            Action<GitSubmoduleBatchAssessmentCompletion> onComplete,
            out string error)
        {
            error = ValidateBatchInfos(infos);
            if (!string.IsNullOrEmpty(error))
                return false;

            if (!CanStart)
            {
                error = BuildUnavailableMessage();
                return false;
            }

            List<PackageManagerSubmoduleInfo> snapshots = CopyInfos(infos);
            var state = new GitSubmoduleBatchAssessmentTaskState();
            return GitOperationService.TryStartTask(
                $"Inspecting {snapshots.Count} submodules before removal...",
                cancellationToken => RunBatchAssessmentTask(
                    snapshots,
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
                        state.Outcome == GitOperationCompletionOutcome.Succeeded &&
                        state.Assessments.Count == snapshots.Count;
                    onComplete?.Invoke(new GitSubmoduleBatchAssessmentCompletion
                    {
                        Success = success,
                        Message = success
                            ? string.Empty
                            : BuildCompletionError(
                                state.Message,
                                result,
                                effectiveOutcome),
                        Outcome = effectiveOutcome,
                        Assessments = success
                            ? CopyAssessments(state.Assessments)
                            : Array.Empty<SubmoduleRemovalAssessment>(),
                        CommandResult = result
                    });
                },
                out error,
                CreateBatchOperationMetadata(
                    snapshots,
                    "inspect-before-remove-batch",
                    false));
        }

        internal static bool TryStartBatch(
            IReadOnlyList<GitSubmoduleBatchRemovalItem> items,
            Action<CommandResult, GitOperationCompletionOutcome>
                onBeforeReloadUnlock,
            Action<GitSubmoduleBatchRemoveCompletion> onComplete,
            out string error)
        {
            error = ValidateBatchItems(items);
            if (!string.IsNullOrEmpty(error))
                return false;

            if (!CanStart)
            {
                error = BuildUnavailableMessage();
                return false;
            }

            List<GitSubmoduleBatchRemovalItem> snapshots = CopyItems(items);
            var state = new GitSubmoduleBatchRemoveTaskState();
            string partialResolutionOperationId = Guid.NewGuid().ToString("N");
            bool partialResolutionPrepared = false;
            return GitOperationService.TryStartTask(
                $"Removing {snapshots.Count} Git submodules...",
                cancellationToken => RunBatchRemoveTask(
                    snapshots,
                    state,
                    cancellationToken),
                true,
                _ => state.Outcome,
                (result, effectiveOutcome) =>
                {
                    if (ShouldCancelPartialBatchResolution(
                            partialResolutionPrepared,
                            effectiveOutcome))
                    {
                        PackageManagerProjectResolutionService.CancelPrepared(
                            partialResolutionOperationId);
                    }

                    bool success =
                        effectiveOutcome == GitOperationCompletionOutcome.Succeeded &&
                        result != null &&
                        result.IsSuccess &&
                        state.Outcome == GitOperationCompletionOutcome.Succeeded &&
                        state.RemovedCount == snapshots.Count;
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

                    onComplete?.Invoke(new GitSubmoduleBatchRemoveCompletion
                    {
                        Success = success,
                        Message = message,
                        Outcome = effectiveOutcome,
                        RemovedCount = state.RemovedCount,
                        RemovedPackageNames = state.RemovedPackageNames.ToArray(),
                        CommandResult = result
                    });
                },
                out error,
                CreateBatchOperationMetadata(snapshots),
                (result, effectiveOutcome) =>
                {
                    if (ShouldPreparePartialBatchResolution(
                            effectiveOutcome,
                            result?.TerminationConfirmed == true,
                            state.RemovedCount,
                            snapshots.Count))
                    {
                        partialResolutionPrepared =
                            PackageManagerProjectResolutionService.TryPrepare(
                                partialResolutionOperationId,
                                state.RemovedPackageNames,
                                PackageManagerResolutionExpectation.NotEmbedded,
                                out string resolutionError);
                        if (!partialResolutionPrepared)
                        {
                            string warning =
                                "The removed prefix could not start Unity package " +
                                "resolution: " + resolutionError;
                            state.Message = string.IsNullOrWhiteSpace(state.Message)
                                ? warning
                                : state.Message.TrimEnd() + " " + warning;
                            Debug.LogWarning(
                                "[Git Submodule Manager] " + warning);
                        }
                    }

                    onBeforeReloadUnlock?.Invoke(result, effectiveOutcome);
                });
        }

        internal static bool ShouldPreparePartialBatchResolution(
            GitOperationCompletionOutcome outcome,
            bool terminationConfirmed,
            int removedCount,
            int totalCount)
        {
            return outcome == GitOperationCompletionOutcome.FailedButRolledBack &&
                   terminationConfirmed &&
                   removedCount > 0 &&
                   removedCount < totalCount;
        }

        internal static bool ShouldCancelPartialBatchResolution(
            bool prepared,
            GitOperationCompletionOutcome outcome)
        {
            return prepared &&
                   outcome == GitOperationCompletionOutcome.FailedUnsafe;
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
                    CompletionWarning = removeError ?? string.Empty,
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

        internal static CommandResult RunBatchAssessmentTask(
            IReadOnlyList<PackageManagerSubmoduleInfo> infos,
            GitSubmoduleBatchAssessmentTaskState state,
            CancellationToken cancellationToken)
        {
            if (state == null)
                throw new ArgumentNullException(nameof(state));

            state.Assessments.Clear();
            state.Message = string.Empty;
            state.Outcome = GitOperationCompletionOutcome.FailedButRolledBack;
            for (int index = 0; index < infos.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                PackageManagerSubmoduleInfo info = infos[index];
                bool assessed = GitUtility.TryAssessSubmoduleRemoval(
                    info.PackagePath,
                    out SubmoduleRemovalAssessment assessment,
                    out string assessmentError,
                    cancellationToken);
                bool terminationUnconfirmed =
                    GitUtility.ConsumeUnconfirmedCommandTermination();
                if (terminationUnconfirmed)
                {
                    state.Outcome = GitOperationCompletionOutcome.FailedUnsafe;
                    state.Message =
                        $"Git process termination could not be confirmed while " +
                        $"inspecting {info.PackageName}.";
                    return FailedCommand(state.Message, false);
                }

                if (!assessed || assessment == null)
                {
                    state.Message =
                        $"{info.PackageName} could not be inspected safely: " +
                        (string.IsNullOrWhiteSpace(assessmentError)
                            ? "The Git submodule state was unavailable."
                            : assessmentError.Trim());
                    return FailedCommand(state.Message, true);
                }

                state.Assessments.Add(assessment.CreateSnapshot());
            }

            state.Outcome = GitOperationCompletionOutcome.Succeeded;
            return new CommandResult
            {
                ExitCode = 0,
                StdOut = $"Inspected {infos.Count} Git submodules.",
                StdErr = string.Empty,
                TerminationConfirmed = true
            };
        }

        internal static CommandResult RunBatchRemoveTask(
            IReadOnlyList<GitSubmoduleBatchRemovalItem> items,
            GitSubmoduleBatchRemoveTaskState state,
            CancellationToken cancellationToken)
        {
            if (state == null)
                throw new ArgumentNullException(nameof(state));

            state.RemovedCount = 0;
            state.RemovedPackageNames.Clear();
            state.Message = string.Empty;
            state.Outcome = GitOperationCompletionOutcome.FailedButRolledBack;
            var completionWarnings = new List<string>();
            for (int index = 0; index < items.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                GitSubmoduleBatchRemovalItem item = items[index];
                PackageManagerSubmoduleInfo info = item.Info;
                bool removed = GitUtility.TryRemoveSubmodule(
                    info.PackagePath,
                    item.ConfirmedAssessment,
                    item.DiscardLocalWork,
                    out string removeError,
                    out GitOperationCompletionOutcome outcome,
                    cancellationToken);
                bool terminationUnconfirmed =
                    GitUtility.ConsumeUnconfirmedCommandTermination();
                if (terminationUnconfirmed)
                    outcome = GitOperationCompletionOutcome.FailedUnsafe;

                if (!removed || outcome != GitOperationCompletionOutcome.Succeeded)
                {
                    state.Outcome = outcome;
                    state.Message = BuildBatchFailureMessage(
                        info.PackageName,
                        state.RemovedCount,
                        items.Count,
                        removeError,
                        terminationUnconfirmed);
                    return FailedCommand(
                        state.Message,
                        !terminationUnconfirmed);
                }

                state.RemovedCount++;
                state.RemovedPackageNames.Add(info.PackageName);
                if (!string.IsNullOrWhiteSpace(removeError))
                    completionWarnings.Add(removeError.Trim());
            }

            state.Outcome = GitOperationCompletionOutcome.Succeeded;
            state.Message =
                $"Removed {state.RemovedCount} Git submodules. Review and commit " +
                "the parent repository changes.";
            return new CommandResult
            {
                ExitCode = 0,
                StdOut = state.Message,
                StdErr = string.Empty,
                CompletionWarning = string.Join(" ", completionWarnings),
                TerminationConfirmed = true
            };
        }

        internal static string BuildUnavailableMessage()
        {
            if (PackageManagerProjectResolutionService.IsBusy)
                return PackageManagerProjectResolutionService.BuildUnavailableMessage();

            if (PackageManagerNativeRemoveHandoffService.IsBusy)
            {
                return PackageManagerNativeRemoveHandoffService
                    .BuildUnavailableMessage();
            }

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

        private static string ValidateBatchInfos(
            IReadOnlyList<PackageManagerSubmoduleInfo> infos)
        {
            if (infos == null || infos.Count == 0)
                return "No Git submodules were selected for removal.";

            var packageNames = new HashSet<string>(StringComparer.Ordinal);
            var packagePaths = new HashSet<string>(StringComparer.Ordinal);
            for (int index = 0; index < infos.Count; index++)
            {
                PackageManagerSubmoduleInfo info = infos[index];
                string error = ValidateInput(info);
                if (!string.IsNullOrEmpty(error))
                    return error;
                if (!packageNames.Add(info.PackageName) ||
                    !packagePaths.Add(GitUtility.NormalizePath(info.PackagePath)))
                {
                    return "The selected Git submodule list contains a duplicate package identity.";
                }
            }

            return string.Empty;
        }

        private static string ValidateBatchItems(
            IReadOnlyList<GitSubmoduleBatchRemovalItem> items)
        {
            if (items == null || items.Count == 0)
                return "No assessed Git submodules were provided for removal.";

            var infos = new List<PackageManagerSubmoduleInfo>(items.Count);
            for (int index = 0; index < items.Count; index++)
            {
                GitSubmoduleBatchRemovalItem item = items[index];
                if (item?.Info == null || item.ConfirmedAssessment == null)
                {
                    return "Every selected Git submodule must carry its exact confirmed assessment.";
                }

                string path = GitUtility.NormalizePath(item.Info.PackagePath);
                string confirmationError = ValidateConfirmation(
                    path,
                    item.ConfirmedAssessment,
                    item.DiscardLocalWork);
                if (!string.IsNullOrEmpty(confirmationError))
                    return confirmationError;
                infos.Add(item.Info);
            }

            return ValidateBatchInfos(infos);
        }

        private static List<PackageManagerSubmoduleInfo> CopyInfos(
            IReadOnlyList<PackageManagerSubmoduleInfo> infos)
        {
            var result = new List<PackageManagerSubmoduleInfo>(infos.Count);
            for (int index = 0; index < infos.Count; index++)
            {
                PackageManagerSubmoduleInfo info = infos[index];
                result.Add(new PackageManagerSubmoduleInfo(
                    info.PackageName,
                    info.PackagePath,
                    info.FullPackagePath,
                    info.RepositoryUrl,
                    info.IsGitHub,
                    info.ResolvedCommit));
            }

            return result;
        }

        private static List<GitSubmoduleBatchRemovalItem> CopyItems(
            IReadOnlyList<GitSubmoduleBatchRemovalItem> items)
        {
            var infos = new List<PackageManagerSubmoduleInfo>(items.Count);
            for (int index = 0; index < items.Count; index++)
                infos.Add(items[index].Info);
            List<PackageManagerSubmoduleInfo> copiedInfos = CopyInfos(infos);

            var result = new List<GitSubmoduleBatchRemovalItem>(items.Count);
            for (int index = 0; index < items.Count; index++)
            {
                result.Add(new GitSubmoduleBatchRemovalItem
                {
                    Info = copiedInfos[index],
                    ConfirmedAssessment =
                        items[index].ConfirmedAssessment.CreateSnapshot(),
                    DiscardLocalWork = items[index].DiscardLocalWork
                });
            }

            return result;
        }

        private static IReadOnlyList<SubmoduleRemovalAssessment> CopyAssessments(
            IReadOnlyList<SubmoduleRemovalAssessment> assessments)
        {
            var result = new SubmoduleRemovalAssessment[assessments.Count];
            for (int index = 0; index < assessments.Count; index++)
                result[index] = assessments[index]?.CreateSnapshot();
            return result;
        }

        private static GitOperationMetadata CreateBatchOperationMetadata(
            IReadOnlyList<PackageManagerSubmoduleInfo> infos,
            string phase,
            bool mayChangeRepository)
        {
            return new GitOperationMetadata
            {
                PackagePath = BuildBatchJournalPath(infos),
                Phase = phase,
                MayChangeRepository = mayChangeRepository
            };
        }

        private static GitOperationMetadata CreateBatchOperationMetadata(
            IReadOnlyList<GitSubmoduleBatchRemovalItem> items)
        {
            var infos = new List<PackageManagerSubmoduleInfo>(items.Count);
            for (int index = 0; index < items.Count; index++)
                infos.Add(items[index].Info);

            GitOperationMetadata metadata = CreateBatchOperationMetadata(
                infos,
                "remove-batch",
                true);
            metadata.PackageName = infos[0].PackageName;
            metadata.PackageNames = new string[infos.Count];
            for (int index = 0; index < infos.Count; index++)
                metadata.PackageNames[index] = infos[index].PackageName;
            metadata.PackageResolutionExpectation =
                PackageManagerResolutionExpectation.NotEmbedded;
            return metadata;
        }

        private static string BuildBatchJournalPath(
            IReadOnlyList<PackageManagerSubmoduleInfo> infos)
        {
            var builder = new StringBuilder();
            for (int index = 0; index < infos.Count; index++)
            {
                if (index > 0)
                    builder.Append(", ");
                builder.Append(GitUtility.NormalizePath(infos[index].PackagePath));
            }

            return builder.ToString();
        }

        private static CommandResult FailedCommand(
            string message,
            bool terminationConfirmed)
        {
            return new CommandResult
            {
                ExitCode = -1,
                StdOut = string.Empty,
                StdErr = message ?? string.Empty,
                TerminationConfirmed = terminationConfirmed
            };
        }

        private static string BuildBatchFailureMessage(
            string packageName,
            int removedCount,
            int totalCount,
            string removeError,
            bool terminationUnconfirmed)
        {
            string detail = terminationUnconfirmed
                ? "A child Git process may still be running."
                : string.IsNullOrWhiteSpace(removeError)
                    ? "The Git submodule could not be removed safely."
                    : removeError.Trim();
            if (removedCount == 0)
            {
                return $"No packages were removed because {packageName} could " +
                       $"not be removed safely. {detail}";
            }

            return $"Removed {removedCount} of {totalCount} Git submodules, then " +
                   $"stopped at {packageName}. The remaining selected packages " +
                   $"were preserved. {detail}";
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
