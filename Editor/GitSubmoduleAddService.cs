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
    /// Host-neutral add workflow shared by Package Manager's install surfaces.
    /// All repository mutation remains owned by
    /// GitOperationService, including journaling, reload locking, final refresh,
    /// postcondition checks, and rollback.
    /// </summary>
    internal static class GitSubmoduleAddService
    {
        internal const string PackageNameRule =
            "Use a lowercase reverse-domain UPM name (for example com.company.package); hyphens and underscores are supported.";

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
            string expectedVersion,
            string expectedDependencyFingerprint,
            PackageManifestMetaVerification packageManifestMetaVerification,
            string expectedPackageManifestMetaGuid,
            string inspectedCommit,
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
            string metaGuid = expectedPackageManifestMetaGuid?.Trim() ?? string.Empty;
            if (!TryValidatePackageManifestMetaEvidence(
                    packageManifestMetaVerification,
                    metaGuid,
                    out error))
            {
                return false;
            }
            string expectedCommit = inspectedCommit?.Trim() ?? string.Empty;
            if (!GitUtility.IsValidGitObjectId(expectedCommit))
            {
                error =
                    "Submodule installs require the exact nonzero Git commit whose root package metadata was inspected.";
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
                    cancellationToken,
                    packageManifestMetaVerification,
                    metaGuid,
                    expectedCommit),
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
            string expectedVersion,
            string expectedDependencyFingerprint,
            string path,
            GitSubmoduleAddTaskState state,
            CancellationToken cancellationToken,
            PackageManifestMetaVerification packageManifestMetaVerification =
                PackageManifestMetaVerification.Unverified,
            string expectedPackageManifestMetaGuid = "",
            string inspectedCommit = "")
        {
            cancellationToken.ThrowIfCancellationRequested();
            string metaGuid = expectedPackageManifestMetaGuid?.Trim() ?? string.Empty;
            if (!TryValidatePackageManifestMetaEvidence(
                    packageManifestMetaVerification,
                    metaGuid,
                    out string metaEvidenceError))
            {
                state.Outcome = GitOperationCompletionOutcome.FailedButRolledBack;
                state.Message = metaEvidenceError;
                return SafeTaskFailure(metaEvidenceError);
            }
            string expectedCommit = inspectedCommit?.Trim() ?? string.Empty;
            if (!GitUtility.IsValidGitObjectId(expectedCommit))
            {
                const string commitEvidenceError =
                    "Submodule installs require the exact nonzero Git commit whose root package metadata was inspected.";
                state.Outcome = GitOperationCompletionOutcome.FailedButRolledBack;
                state.Message = commitEvidenceError;
                return SafeTaskFailure(commitEvidenceError);
            }

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
            CancellationToken postAddToken = CancellationToken.None;
            string rollbackEvidenceError = string.Empty;
            bool rollbackEvidenceCaptured =
                addResult != null &&
                addResult.TerminationConfirmed &&
                GitUtility.TryCaptureFailedAddRollbackEvidence(
                    plan,
                    expectedCommit,
                    out rollbackEvidenceError,
                    postAddToken);
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

                bool cleanupSucceeded = GitUtility.TryCleanupFailedAdd(
                    plan,
                    out string cleanupWarning,
                    postAddToken);
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

            if (!rollbackEvidenceCaptured)
            {
                string evidenceMessage =
                    "Git reported success, but exact add-produced rollback evidence could not be captured: " +
                    rollbackEvidenceError;
                bool cleanupSucceeded = GitUtility.TryCleanupFailedAdd(
                    plan,
                    out string cleanupWarning,
                    postAddToken);
                state.Outcome = cleanupSucceeded
                    ? GitOperationCompletionOutcome.FailedButRolledBack
                    : GitOperationCompletionOutcome.FailedUnsafe;
                state.Message = evidenceMessage +
                    (string.IsNullOrWhiteSpace(cleanupWarning)
                        ? string.Empty
                        : " " + cleanupWarning);
                return new CommandResult
                {
                    ExitCode = -1,
                    StdOut = addResult.StdOut,
                    StdErr = state.Message,
                    TerminationConfirmed = true
                };
            }

            CommandResult headResult = GitUtility.RunGit(
                GitUtility.BuildReadSubmoduleHeadCommitArguments(path),
                GitUtility.ProjectRoot,
                5000,
                postAddToken);
            if (headResult == null || !headResult.TerminationConfirmed)
            {
                state.Outcome = GitOperationCompletionOutcome.FailedUnsafe;
                state.Message =
                    GitUtility.BuildCommandError(
                        "Could not verify the checked-out submodule commit",
                        headResult) +
                    " Process-tree termination could not be confirmed, so automatic cleanup was skipped. Inspect the new submodule and running Git processes before acknowledging recovery.";
                return headResult ?? new CommandResult
                {
                    ExitCode = -1,
                    StdErr = state.Message,
                    TerminationConfirmed = false
                };
            }

            string validationError = string.Empty;
            string actualCommit = headResult.StdOut?.Trim() ?? string.Empty;
            if (!headResult.IsSuccess)
            {
                validationError = GitUtility.BuildCommandError(
                    "Could not verify the checked-out submodule commit",
                    headResult);
            }
            else if (headResult.StdOutTruncated || headResult.StdErrTruncated)
            {
                validationError =
                    "Checked-out submodule commit output or diagnostics were truncated; exact commit verification was blocked.";
            }
            else if (!GitUtility.IsValidGitObjectId(actualCommit))
            {
                validationError =
                    "Git returned an invalid checked-out submodule commit during exact verification.";
            }
            else if (!string.Equals(
                         actualCommit,
                         expectedCommit,
                         StringComparison.OrdinalIgnoreCase))
            {
                validationError =
                    "The selected branch changed after package inspection; the checked-out submodule commit does not match the exact inspected commit.";
            }

            string packageJsonPath = Path.Combine(
                GitUtility.ProjectRoot,
                path,
                "package.json");
            PackageManifestMetadata metadata = null;
            if (string.IsNullOrEmpty(validationError) &&
                !GitUtility.TryReadPackageManifestMetadata(
                    packageJsonPath,
                    out metadata,
                    out string manifestError,
                    postAddToken))
            {
                validationError =
                    "Added submodule package.json is invalid: " + manifestError;
            }
            else if (string.IsNullOrEmpty(validationError))
            {
                validationError = GitUtility.ValidateExpectedPackageManifest(
                    packageName,
                    expectedVersion,
                    expectedDependencyFingerprint,
                    metadata);
            }

            if (string.IsNullOrEmpty(validationError))
            {
                string packageRoot = Path.Combine(
                    GitUtility.ProjectRoot,
                    path);
                CommandResult treeResult = GitUtility.RunGit(
                    "--no-pager ls-tree -z --full-tree " +
                    GitUtility.Quote(expectedCommit) +
                    " -- package.json package.json.meta",
                    packageRoot,
                    10000,
                    postAddToken);
                if (treeResult == null || !treeResult.TerminationConfirmed)
                {
                    state.Outcome = GitOperationCompletionOutcome.FailedUnsafe;
                    state.Message =
                        GitUtility.BuildCommandError(
                            "Could not verify exact inspected package tree modes",
                            treeResult) +
                        " Process-tree termination could not be confirmed, so automatic cleanup was skipped. Inspect the new submodule and running Git processes before acknowledging recovery.";
                    return treeResult ?? new CommandResult
                    {
                        ExitCode = -1,
                        StdErr = state.Message,
                        TerminationConfirmed = false
                    };
                }

                if (!treeResult.IsSuccess)
                {
                    validationError = GitUtility.BuildCommandError(
                        "Could not verify exact inspected package tree modes",
                        treeResult);
                }
                else if (treeResult.StdOutTruncated)
                {
                    validationError =
                        "Checked-out package tree mode output was truncated; package.json.meta verification was blocked.";
                }
                else if (!GitSubmoduleInstallProbe.TryParseRootPackageTree(
                             treeResult.StdOut,
                             out string packageManifestObjectId,
                             out string packageManifestMetaObjectId,
                             out string packageManifestMetaTreeMessage,
                             out string treeError))
                {
                    validationError =
                        "Checked-out package tree verification failed: " + treeError;
                }
                else if (!GitUtility.IsValidGitObjectId(
                             packageManifestObjectId))
                {
                    validationError =
                        "The exact inspected package.json is not a regular Git blob with a verifiable identity.";
                }
                else if (packageManifestMetaVerification ==
                             PackageManifestMetaVerification.Verified &&
                         !GitUtility.IsValidGitObjectId(
                             packageManifestMetaObjectId))
                {
                    validationError = string.IsNullOrWhiteSpace(
                        packageManifestMetaTreeMessage)
                        ? "The exact inspected package.json.meta is not a regular Git blob with a verifiable identity."
                        : packageManifestMetaTreeMessage;
                }

                if (string.IsNullOrEmpty(validationError) &&
                    packageManifestMetaVerification ==
                        PackageManifestMetaVerification.Verified)
                {
                    string packageManifestMetaPath = Path.Combine(
                        packageRoot,
                        "package.json.meta");
                    string actualMetaGuid = string.Empty;
                    if (!GitUtility.TryReadValidPackageManifestMeta(
                            packageManifestMetaPath,
                            out actualMetaGuid,
                            out string metaError,
                            postAddToken))
                    {
                        validationError =
                            "Added submodule package.json.meta is invalid: " + metaError;
                    }
                    else if (!string.Equals(
                                 actualMetaGuid,
                                 metaGuid,
                                 StringComparison.OrdinalIgnoreCase))
                    {
                        validationError =
                            "Added submodule package.json.meta changed after repository inspection.";
                    }
                }
            }

            if (string.IsNullOrEmpty(validationError) &&
                !GitUtility.TryVerifyAddedSubmodule(
                         plan,
                         url,
                         branch,
                         expectedCommit,
                         out string postconditionError,
                         postAddToken))
            {
                validationError =
                    "Git reported success, but add verification failed: " +
                    postconditionError;
            }

            if (!string.IsNullOrEmpty(validationError))
            {
                bool cleanupSucceeded = GitUtility.TryCleanupFailedAdd(
                    plan,
                    out string cleanupNotice,
                    postAddToken);
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

        private static bool TryValidatePackageManifestMetaEvidence(
            PackageManifestMetaVerification verification,
            string guid,
            out string error)
        {
            error = string.Empty;
            if (verification == PackageManifestMetaVerification.Unverified)
            {
                if (string.IsNullOrWhiteSpace(guid))
                    return true;

                error =
                    "Unverified package.json.meta evidence cannot carry a trusted GUID.";
                return false;
            }

            if (verification != PackageManifestMetaVerification.Verified ||
                !GitSubmoduleInstallProbeSnapshot.IsValidMetaGuid(guid))
            {
                error =
                    "Verified package.json.meta evidence requires a valid nonzero Unity GUID.";
                return false;
            }

            return true;
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
