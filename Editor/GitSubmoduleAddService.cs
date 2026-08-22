using System;
using System.IO;
using System.Threading;

namespace MartinCalander.GitSubmoduleManager.Editor
{
    internal sealed class GitSubmoduleAddTaskState
    {
        internal GitOperationCompletionOutcome Outcome =
            GitOperationCompletionOutcome.FailedUnsafe;
        internal string Message = string.Empty;
        internal bool AddedSuccessfully;
    }

    internal sealed class GitSubmoduleAddCompletion
    {
        internal bool Success;
        internal string Message = string.Empty;
        internal GitOperationCompletionOutcome Outcome;
        internal CommandResult CommandResult;
    }

    /// <summary>
    /// Host-neutral add workflow shared by the legacy manager view and the
    /// Package Manager discovery page. All repository mutation remains owned by
    /// GitOperationService, including journaling, reload locking, final refresh,
    /// postcondition checks, and rollback.
    /// </summary>
    internal static class GitSubmoduleAddService
    {
        internal const string PackageNameRule =
            "Use a lowercase reverse-domain UPM name (for example com.company.package); hyphens and underscores are supported.";

        internal static bool CanStart =>
            !GitOperationService.IsBusy &&
            !PackageManagerProjectResolutionService.IsBusy &&
            !PackageManagerReadOnlyGitInstallService.IsBusy &&
            !PackageManagerSubmoduleSnapshot.IsReaderActive &&
            !GitSubmoduleInstallProbe.IsReaderActive &&
            !AsyncCommandDrainRegistry.IsDraining &&
            string.IsNullOrWhiteSpace(GitOperationService.RecoveryWarning);

        internal static string ValidateInput(
            string url,
            string packageName,
            string branch)
        {
            if (string.IsNullOrWhiteSpace(url))
                return "Git URL is required.";

            if (!GitUtility.IsValidRepositoryUrl(url))
            {
                return "Use a secure HTTPS, SSH, or explicit local repository URL without embedded credentials. " +
                       "Plain HTTP and git:// transports are blocked.";
            }

            if (!GitUtility.IsValidUpmPackageName(packageName))
                return PackageNameRule;

            if (!GitUtility.IsValidBranchName(branch))
                return "Branch name is invalid. Leave it empty to use the repository default.";

            string path = GetPackagePath(packageName);
            string fullPath = Path.Combine(GitUtility.ProjectRoot, path);
            if (!GitUtility.TryInspectFileSystemEntryPresence(
                    fullPath,
                    out bool entryExists,
                    out string inspectionError,
                    CancellationToken.None))
            {
                return inspectionError;
            }

            if (entryExists)
                return $"Package path already exists: {path}";

            return string.Empty;
        }

        internal static bool TryStart(
            string url,
            string branch,
            string packageName,
            Action<GitSubmoduleAddCompletion> onComplete,
            out string error)
        {
            return TryStart(
                url,
                branch,
                packageName,
                string.Empty,
                onComplete,
                out error);
        }

        internal static bool TryStart(
            string url,
            string branch,
            string packageName,
            string expectedVersion,
            Action<GitSubmoduleAddCompletion> onComplete,
            out string error)
        {
            return TryStart(
                url,
                branch,
                packageName,
                expectedVersion,
                string.Empty,
                onComplete,
                out error);
        }

        internal static bool TryStart(
            string url,
            string branch,
            string packageName,
            string expectedVersion,
            string expectedDependencyFingerprint,
            Action<GitSubmoduleAddCompletion> onComplete,
            out string error)
        {
            error = ValidateInput(url, packageName, branch);
            if (!string.IsNullOrEmpty(error))
                return false;
            string version = expectedVersion?.Trim() ?? string.Empty;
            if (!string.IsNullOrEmpty(version) &&
                !GitUtility.IsValidSemanticVersion(version))
            {
                error = "The expected package version must be valid SemVer 2.0.";
                return false;
            }
            string dependencyFingerprint =
                expectedDependencyFingerprint?.Trim() ?? string.Empty;
            if (!string.IsNullOrEmpty(dependencyFingerprint) &&
                !GitUtility.IsValidPackageDependencyFingerprint(
                    dependencyFingerprint))
            {
                error = "The expected package dependency fingerprint is invalid.";
                return false;
            }

            string path = GetPackagePath(packageName);
            var state = new GitSubmoduleAddTaskState();
            return GitOperationService.TryStartTask(
                $"Adding {packageName}...",
                cancellationToken => RunAddSubmoduleTask(
                    url,
                    branch,
                    packageName,
                    version,
                    dependencyFingerprint,
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
                        state.AddedSuccessfully &&
                        state.Outcome == GitOperationCompletionOutcome.Succeeded;
                    string message = success
                        ? state.Message
                        : BuildCompletionError(state.Message, result, effectiveOutcome);
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

                    onComplete?.Invoke(new GitSubmoduleAddCompletion
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
                    Phase = "add-validate-or-rollback",
                    PackageName = packageName,
                    PackageResolutionExpectation =
                        PackageManagerResolutionExpectation.Embedded
                });
        }

        internal static CommandResult RunAddSubmoduleTask(
            string url,
            string branch,
            string packageName,
            string path,
            GitSubmoduleAddTaskState state,
            CancellationToken cancellationToken)
        {
            return RunAddSubmoduleTask(
                url,
                branch,
                packageName,
                string.Empty,
                path,
                state,
                cancellationToken);
        }

        internal static CommandResult RunAddSubmoduleTask(
            string url,
            string branch,
            string packageName,
            string expectedVersion,
            string path,
            GitSubmoduleAddTaskState state,
            CancellationToken cancellationToken)
        {
            return RunAddSubmoduleTask(
                url,
                branch,
                packageName,
                expectedVersion,
                string.Empty,
                path,
                state,
                cancellationToken);
        }

        internal static CommandResult RunAddSubmoduleTask(
            string url,
            string branch,
            string packageName,
            string expectedVersion,
            string expectedDependencyFingerprint,
            string path,
            GitSubmoduleAddTaskState state,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!GitUtility.TryPrepareAddSubmodule(
                    url,
                    path,
                    out AddSubmodulePlan plan,
                    out string prepareError,
                    cancellationToken))
            {
                state.Outcome = GitOperationCompletionOutcome.FailedButRolledBack;
                state.Message = prepareError;
                return SafeTaskFailure(prepareError);
            }

            if (!GitUtility.TryBuildAddSubmoduleArguments(
                    url,
                    path,
                    branch,
                    plan.ReuseExistingMetadata,
                    out string arguments,
                    out string argumentError))
            {
                state.Outcome = GitOperationCompletionOutcome.FailedButRolledBack;
                state.Message = argumentError;
                return SafeTaskFailure(argumentError);
            }

            plan.ExpectedBranch = branch?.Trim() ?? string.Empty;

            cancellationToken.ThrowIfCancellationRequested();
            CommandResult addResult = GitUtility.RunGit(
                arguments,
                GitUtility.ProjectRoot,
                120000,
                cancellationToken);
            if (addResult == null || !addResult.IsSuccess)
            {
                string message = GitUtility.BuildCommandError(
                    "Failed to add submodule",
                    addResult);
                if (addResult == null || !addResult.TerminationConfirmed)
                {
                    state.Outcome = GitOperationCompletionOutcome.FailedUnsafe;
                    state.Message =
                        message +
                        " Process-tree termination could not be confirmed, so automatic cleanup was skipped. " +
                        "Inspect the destination and running Git/SSH processes before acknowledging recovery.";
                    return addResult;
                }

                cancellationToken.ThrowIfCancellationRequested();
                bool cleanupSucceeded = GitUtility.TryCleanupFailedAdd(
                    plan,
                    out string cleanupWarning,
                    cancellationToken);
                if (!string.IsNullOrWhiteSpace(cleanupWarning))
                {
                    message += cleanupSucceeded
                        ? $" Recovery details: {cleanupWarning}"
                        : $" Cleanup warning: {cleanupWarning}";
                }

                state.Message = message;
                state.Outcome = cleanupSucceeded
                    ? GitOperationCompletionOutcome.FailedButRolledBack
                    : GitOperationCompletionOutcome.FailedUnsafe;
                return addResult;
            }

            string packageJsonPath = Path.Combine(
                GitUtility.ProjectRoot,
                path,
                "package.json");
            string validationError = string.Empty;
            if (!GitUtility.TryReadPackageManifestMetadata(
                    packageJsonPath,
                    out PackageManifestMetadata metadata,
                    out string manifestError,
                    cancellationToken))
            {
                validationError =
                    "Added submodule package.json is invalid: " + manifestError;
            }
            else
            {
                validationError = GitUtility.ValidateExpectedPackageManifest(
                    packageName,
                    expectedVersion,
                    expectedDependencyFingerprint,
                    metadata);
            }

            if (string.IsNullOrEmpty(validationError) &&
                !GitUtility.TryVerifyAddedSubmodule(
                         plan,
                         url,
                         branch,
                         out string postconditionError,
                         cancellationToken))
            {
                validationError =
                    "Git reported success, but add verification failed: " +
                    postconditionError;
            }

            if (!string.IsNullOrEmpty(validationError))
            {
                cancellationToken.ThrowIfCancellationRequested();
                bool cleanupSucceeded = GitUtility.TryCleanupFailedAdd(
                    plan,
                    out string cleanupNotice,
                    cancellationToken);
                state.Message = cleanupSucceeded
                    ? string.IsNullOrWhiteSpace(cleanupNotice)
                        ? validationError + " The incomplete submodule was rolled back."
                        : validationError +
                          " The Git registration was rolled back. " +
                          cleanupNotice
                    : validationError + " Rollback failed: " + cleanupNotice;
                state.Outcome = cleanupSucceeded
                    ? GitOperationCompletionOutcome.FailedButRolledBack
                    : GitOperationCompletionOutcome.FailedUnsafe;
                return new CommandResult
                {
                    ExitCode = -1,
                    StdOut = addResult.StdOut,
                    StdErr = state.Message,
                    TerminationConfirmed = true
                };
            }

            state.AddedSuccessfully = true;
            state.Outcome = GitOperationCompletionOutcome.Succeeded;
            state.Message = $"Successfully added {packageName}.";
            return addResult;
        }

        internal static string GetPackagePath(string packageName)
        {
            return $"Packages/{packageName}";
        }

        private static CommandResult SafeTaskFailure(string error)
        {
            bool terminationUnconfirmed =
                GitUtility.ConsumeUnconfirmedCommandTermination();
            return new CommandResult
            {
                ExitCode = -1,
                StdOut = string.Empty,
                StdErr = error ??
                         "The Git operation failed before mutating the repository.",
                TerminationConfirmed = !terminationUnconfirmed
            };
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
                message = "The add operation did not complete successfully.";

            if (outcome == GitOperationCompletionOutcome.FailedUnsafe &&
                message.IndexOf("recovery", StringComparison.OrdinalIgnoreCase) < 0)
            {
                message +=
                    " Inspect the recovery warning before starting another repository operation.";
            }

            return message;
        }
    }
}
