using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading;

namespace MartinCalander.GitSubmoduleManager.Editor
{
    internal enum GitPackageConversionDirection
    {
        ReadOnlyToSubmodule,
        SubmoduleToReadOnly
    }

    internal sealed class GitPackageConversionCompletion
    {
        internal bool Success;
        internal string Message = string.Empty;
        internal GitOperationCompletionOutcome Outcome;
        internal GitPackageConversionDirection Direction;
        internal string PackageName = string.Empty;
    }

    internal sealed class GitPackageConversionTaskState
    {
        internal GitOperationCompletionOutcome Outcome =
            GitOperationCompletionOutcome.FailedUnsafe;
        internal string Message = string.Empty;
        internal bool ConvertedSuccessfully;
    }

    /// <summary>
    /// Converts between a normal UPM Git dependency and an editable package
    /// submodule. Each direction creates and verifies its target first, then
    /// removes the source. Therefore an interrupted operation can leave both
    /// representations present, but it never intentionally leaves neither.
    /// </summary>
    internal static class GitPackageConversionService
    {
        internal const string ManagerPackageName =
            "com.martincalander.gitsubmodulemanager";
        internal const string RootPackageRequiredMessage =
            "Only read-only Git packages whose package.json is at the repository root can be converted to a submodule.";
        internal const string LocalOnlyCommitReadOnlyMessage =
            "A submodule with commits that are not present on any remote cannot be converted to a read-only Git package. Push or otherwise publish those commits first.";

        private static readonly Regex CommitRegex = new Regex(
            "^[0-9a-fA-F]{40,64}$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        internal static bool CanStart =>
            !GitOperationService.IsBusy &&
            !PackageManagerProjectResolutionService.IsBusy &&
            !PackageManagerReadOnlyGitInstallService.IsBusy &&
            !PackageManagerSubmoduleSnapshot.IsReaderActive &&
            !GitSubmoduleInstallProbe.IsReaderActive &&
            !AsyncCommandDrainRegistry.IsDraining &&
            string.IsNullOrWhiteSpace(GitOperationService.RecoveryWarning);

        internal static string ValidateToSubmodule(
            PackageManagerReadOnlyGitInfo info)
        {
            if (info == null)
                return "The installed read-only Git package could not be identified.";
            if (!GitUtility.IsValidUpmPackageName(info.PackageName))
                return GitSubmoduleAddService.PackageNameRule;
            if (IsSelfConversion(info.PackageName))
                return BuildSelfConversionMessage();
            if (!info.IsRepositoryRootPackage)
                return RootPackageRequiredMessage;
            if (!GitUtility.IsValidRepositoryUrl(info.RepositoryUrl) ||
                info.RepositoryUrl.IndexOf('#') >= 0)
            {
                return "Conversion requires a secure root Git repository URL without an embedded revision.";
            }
            if (string.IsNullOrWhiteSpace(info.ManifestSpec))
                return "The exact direct Git dependency is missing from Packages/manifest.json.";
            if (!IsValidCommit(info.ResolvedHash))
                return "Unity has not resolved this Git dependency to a verifiable commit yet.";

            string addValidation = GitSubmoduleAddService.ValidateInput(
                info.RepositoryUrl,
                info.PackageName,
                string.Empty);
            return addValidation ?? string.Empty;
        }

        internal static string ValidateToReadOnly(PackageManagerSubmoduleInfo info)
        {
            string removeValidation = GitSubmoduleRemoveService.ValidateInput(info);
            if (!string.IsNullOrWhiteSpace(removeValidation))
                return removeValidation;
            if (IsSelfConversion(info.PackageName))
                return BuildSelfConversionMessage();
            if (!GitUtility.IsValidRepositoryUrl(info.RepositoryUrl) ||
                info.RepositoryUrl.IndexOf('#') >= 0)
            {
                return "Conversion requires a secure root Git repository URL without an embedded revision.";
            }

            if (PackageManifestGitDependencyStore.TryGetProjectDependency(
                    info.PackageName,
                    out _,
                    out string manifestError))
            {
                return "Packages/manifest.json already declares this package. Refresh Package Manager before converting.";
            }

            // A missing dependency is the expected source state. Parsing or
            // structural errors must still fail closed.
            if (!string.IsNullOrWhiteSpace(manifestError) &&
                manifestError.IndexOf("does not declare", StringComparison.OrdinalIgnoreCase) < 0 &&
                manifestError.IndexOf("not declared", StringComparison.OrdinalIgnoreCase) < 0 &&
                manifestError.IndexOf("not found", StringComparison.OrdinalIgnoreCase) < 0)
            {
                return manifestError;
            }

            return string.Empty;
        }

        internal static bool TryStartToSubmodule(
            PackageManagerReadOnlyGitInfo info,
            Action<GitPackageConversionCompletion> onComplete,
            out string error)
        {
            error = ValidateToSubmodule(info);
            if (!string.IsNullOrWhiteSpace(error))
                return false;
            if (!CanStart)
            {
                error = BuildUnavailableMessage();
                return false;
            }

            string packageName = info.PackageName.Trim();
            string path = GitSubmoduleAddService.GetPackagePath(packageName);
            var snapshot = new PackageManagerReadOnlyGitInfo(
                packageName,
                info.RepositoryUrl.Trim(),
                info.ManifestSpec,
                info.Revision ?? string.Empty,
                info.ResolvedHash.Trim(),
                info.PackageSubfolder,
                null);
            var state = new GitPackageConversionTaskState();
            return GitOperationService.TryStartTask(
                $"Converting {packageName} to a Git submodule...",
                cancellationToken => RunToSubmoduleTask(
                    snapshot,
                    path,
                    state,
                    cancellationToken),
                true,
                _ => state.Outcome,
                (result, effectiveOutcome) => NotifyCompletion(
                    packageName,
                    GitPackageConversionDirection.ReadOnlyToSubmodule,
                    state,
                    result,
                    effectiveOutcome,
                    onComplete),
                out error,
                new GitOperationMetadata
                {
                    PackagePath = path,
                    Phase = "convert-readonly-to-submodule-target-first",
                    PackageName = packageName,
                    PackageResolutionExpectation =
                        PackageManagerResolutionExpectation.Embedded
                });
        }

        internal static bool TryStartToReadOnly(
            PackageManagerSubmoduleInfo info,
            SubmoduleRemovalAssessment confirmedAssessment,
            bool discardLocalWork,
            Action<GitPackageConversionCompletion> onComplete,
            out string error)
        {
            error = ValidateToReadOnly(info);
            if (!string.IsNullOrWhiteSpace(error))
                return false;

            error = ValidateToReadOnlyConfirmation(
                info.PackagePath,
                confirmedAssessment,
                discardLocalWork);
            if (!string.IsNullOrWhiteSpace(error))
                return false;

            if (!CanStart)
            {
                error = BuildUnavailableMessage();
                return false;
            }

            var snapshot = new PackageManagerSubmoduleInfo(
                info.PackageName?.Trim(),
                GitUtility.NormalizePath(info.PackagePath),
                info.FullPackagePath,
                info.RepositoryUrl?.Trim(),
                info.IsGitHub);
            string packageName = snapshot.PackageName;
            SubmoduleRemovalAssessment confirmationSnapshot =
                confirmedAssessment?.CreateSnapshot();
            var state = new GitPackageConversionTaskState();
            return GitOperationService.TryStartTask(
                $"Converting {packageName} to a read-only Git package...",
                cancellationToken => RunToReadOnlyTask(
                    snapshot,
                    confirmationSnapshot,
                    discardLocalWork,
                    state,
                    cancellationToken),
                true,
                _ => state.Outcome,
                (result, effectiveOutcome) => NotifyCompletion(
                    packageName,
                    GitPackageConversionDirection.SubmoduleToReadOnly,
                    state,
                    result,
                    effectiveOutcome,
                    onComplete),
                out error,
                new GitOperationMetadata
                {
                    PackagePath = snapshot.PackagePath,
                    Phase = "convert-submodule-to-readonly-target-first",
                    PackageName = packageName,
                    PackageResolutionExpectation =
                        PackageManagerResolutionExpectation.Git
                });
        }

        internal static CommandResult RunToSubmoduleTask(
            PackageManagerReadOnlyGitInfo info,
            string path,
            GitPackageConversionTaskState state,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!PackageManifestGitDependencyStore.TryGetProjectDependency(
                    info.PackageName,
                    out PackageManifestGitDependency dependency,
                    out string dependencyError) ||
                !string.Equals(
                    dependency.Spec,
                    info.ManifestSpec,
                    StringComparison.Ordinal) ||
                !GitUtility.AreRepositoryUrlsEquivalent(
                    dependency.RepositoryUrl,
                    info.RepositoryUrl) ||
                !dependency.IsRepositoryRootPackage ||
                !info.IsRepositoryRootPackage)
            {
                state.Outcome = GitOperationCompletionOutcome.FailedButRolledBack;
                bool rootPackageViolation =
                    (dependency != null && !dependency.IsRepositoryRootPackage) ||
                    !info.IsRepositoryRootPackage;
                state.Message = !string.IsNullOrWhiteSpace(dependencyError)
                    ? dependencyError
                    : rootPackageViolation
                        ? RootPackageRequiredMessage + " Nothing was changed."
                        : "The direct Git dependency changed before conversion started. Nothing was changed.";
                return Failure(state.Message);
            }

            if (!GitUtility.TryPrepareAddSubmodule(
                    info.RepositoryUrl,
                    path,
                    out AddSubmodulePlan plan,
                    out string prepareError,
                    cancellationToken))
            {
                state.Outcome = GitOperationCompletionOutcome.FailedButRolledBack;
                state.Message = prepareError;
                return Failure(prepareError);
            }

            if (!GitUtility.TryBuildAddSubmoduleArguments(
                    info.RepositoryUrl,
                    path,
                    string.Empty,
                    plan.ReuseExistingMetadata,
                    out string addArguments,
                    out string argumentError))
            {
                state.Outcome = GitOperationCompletionOutcome.FailedButRolledBack;
                state.Message = argumentError;
                return Failure(argumentError);
            }

            plan.ExpectedBranch = string.Empty;
            CommandResult addResult = GitUtility.RunGit(
                addArguments,
                GitUtility.ProjectRoot,
                120000,
                cancellationToken);
            if (addResult == null || !addResult.IsSuccess)
            {
                return RollBackFailedAdd(
                    plan,
                    "Failed to create the target submodule. " +
                    GitUtility.BuildCommandError("Git submodule add failed", addResult),
                    addResult,
                    state,
                    cancellationToken);
            }

            CommandResult checkoutResult = GitUtility.RunGit(
                GitUtility.BuildCheckoutSubmoduleArguments(path, info.ResolvedHash),
                GitUtility.ProjectRoot,
                120000,
                cancellationToken);
            if (checkoutResult == null || !checkoutResult.IsSuccess)
            {
                CommandResult fetchResult = GitUtility.RunGit(
                    GitUtility.BuildFetchSubmoduleCommitArguments(
                        path,
                        info.ResolvedHash),
                    GitUtility.ProjectRoot,
                    120000,
                    cancellationToken);
                if (fetchResult != null && fetchResult.IsSuccess)
                {
                    checkoutResult = GitUtility.RunGit(
                        GitUtility.BuildCheckoutSubmoduleArguments(
                            path,
                            info.ResolvedHash),
                        GitUtility.ProjectRoot,
                        120000,
                        cancellationToken);
                }
            }

            if (checkoutResult == null || !checkoutResult.IsSuccess)
            {
                return RollBackFailedAdd(
                    plan,
                    "The submodule was added, but its previously resolved commit could not be checked out. " +
                    GitUtility.BuildCommandError("Git checkout failed", checkoutResult),
                    checkoutResult,
                    state,
                    cancellationToken);
            }

            string packageJsonPath = System.IO.Path.Combine(
                GitUtility.ProjectRoot,
                path,
                "package.json");
            if (!GitUtility.TryReadValidPackageManifest(
                    packageJsonPath,
                    out string declaredName,
                    out string packageError,
                    cancellationToken) ||
                !string.Equals(declaredName, info.PackageName, StringComparison.Ordinal))
            {
                string message = !string.IsNullOrWhiteSpace(packageError)
                    ? "The exact resolved commit does not contain a valid package.json at the repository root: " +
                      packageError
                    : $"Package name mismatch at the exact resolved commit. Expected {info.PackageName}, got {declaredName}.";
                return RollBackFailedAdd(
                    plan,
                    message,
                    checkoutResult,
                    state,
                    cancellationToken);
            }

            CommandResult stageResult = GitUtility.RunGit(
                GitUtility.BuildStageSubmoduleArguments(path),
                GitUtility.ProjectRoot,
                10000,
                cancellationToken);
            string verifyError = string.Empty;
            string headError = string.Empty;
            bool targetVerified =
                stageResult != null &&
                stageResult.IsSuccess &&
                GitUtility.TryVerifyAddedSubmodule(
                    plan,
                    info.RepositoryUrl,
                    string.Empty,
                    out verifyError,
                    cancellationToken) &&
                TryVerifySubmoduleHead(
                    path,
                    info.ResolvedHash,
                    out headError,
                    cancellationToken);
            if (!targetVerified)
            {
                string message = stageResult == null || !stageResult.IsSuccess
                    ? GitUtility.BuildCommandError(
                        "The exact converted commit could not be staged",
                        stageResult)
                    : !string.IsNullOrWhiteSpace(verifyError)
                        ? "The target submodule could not be verified: " + verifyError
                        : headError;
                return RollBackFailedAdd(
                    plan,
                    message,
                    stageResult,
                    state,
                    cancellationToken);
            }

            if (!PackageManifestGitDependencyStore.TryRemoveDependency(
                    info.PackageName,
                    info.ManifestSpec,
                    out _,
                    out string removeManifestError))
            {
                return RollBackFailedAdd(
                    plan,
                    "The verified submodule was rolled back because the original " +
                    "manifest dependency could not be removed safely. " +
                    removeManifestError,
                    stageResult,
                    state,
                    cancellationToken);
            }

            state.ConvertedSuccessfully = true;
            state.Outcome = GitOperationCompletionOutcome.Succeeded;
            state.Message = $"Converted {info.PackageName} to a Git submodule at {path}.";
            return Success(state.Message);
        }

        internal static CommandResult RunToReadOnlyTask(
            PackageManagerSubmoduleInfo info,
            GitPackageConversionTaskState state,
            CancellationToken cancellationToken)
        {
            return RunToReadOnlyTask(
                info,
                null,
                false,
                state,
                cancellationToken);
        }

        internal static CommandResult RunToReadOnlyTask(
            PackageManagerSubmoduleInfo info,
            SubmoduleRemovalAssessment confirmedAssessment,
            bool discardLocalWork,
            GitPackageConversionTaskState state,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!GitUtility.TryAssessSubmoduleRemoval(
                    info.PackagePath,
                    out SubmoduleRemovalAssessment assessment,
                    out string assessmentError,
                    cancellationToken))
            {
                state.Outcome = GitOperationCompletionOutcome.FailedButRolledBack;
                state.Message = assessmentError;
                return Failure(assessmentError);
            }

            if (confirmedAssessment != null &&
                !GitUtility.RemovalAssessmentMatches(
                    confirmedAssessment,
                    assessment))
            {
                state.Outcome = GitOperationCompletionOutcome.FailedButRolledBack;
                state.Message =
                    "The package or parent repository changed after the conversion warning was shown. Nothing was changed; inspect the current state and confirm again.";
                return Failure(state.Message);
            }

            string assessedRepositoryUrl =
                assessment.RepositoryUrl?.Trim() ?? string.Empty;
            string resolvedRepositoryUrl =
                assessment.ResolvedRepositoryUrl?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(assessment.SubmoduleName) ||
                !GitUtility.IsValidRepositoryUrl(assessedRepositoryUrl) ||
                !GitUtility.AreRepositoryUrlsEquivalent(
                    assessedRepositoryUrl,
                    info.RepositoryUrl))
            {
                state.Outcome = GitOperationCompletionOutcome.FailedButRolledBack;
                state.Message =
                    "The submodule's verified .gitmodules identity or repository URL no longer matches the selected package. Nothing was changed; refresh Package Manager and inspect it again.";
                return Failure(state.Message);
            }

            if (!GitUtility.IsValidRepositoryUrl(resolvedRepositoryUrl))
            {
                state.Outcome = GitOperationCompletionOutcome.FailedButRolledBack;
                state.Message =
                    "The initialized submodule's resolved repository URL could not be verified. Nothing was changed; run Git submodule sync and refresh Package Manager before retrying.";
                return Failure(state.Message);
            }

            if (!GitUtility.IsRelativeLocalRepositoryUrl(
                    assessedRepositoryUrl) &&
                !GitUtility.AreRepositoryUrlsEquivalent(
                    assessedRepositoryUrl,
                    resolvedRepositoryUrl))
            {
                state.Outcome = GitOperationCompletionOutcome.FailedButRolledBack;
                state.Message =
                    "The initialized submodule URL does not match its .gitmodules registration. Nothing was changed; run Git submodule sync and refresh Package Manager before converting.";
                return Failure(state.Message);
            }

            if (assessment.HasUnverifiedWorktreeContents)
            {
                state.Outcome = GitOperationCompletionOutcome.FailedButRolledBack;
                state.Message =
                    "The package directory contains unverified files that the Unity UI will never discard. Move them to safety and leave the directory empty before converting.";
                return Failure(state.Message);
            }

            if (!assessment.IsSafe &&
                (!discardLocalWork || confirmedAssessment == null))
            {
                state.Outcome = GitOperationCompletionOutcome.FailedButRolledBack;
                state.Message = assessment.BuildWarning() +
                    " Conversion was blocked until that exact state is explicitly confirmed for discard.";
                return Failure(state.Message);
            }

            if (!IsValidCommit(assessment.HeadCommit))
            {
                state.Outcome = GitOperationCompletionOutcome.FailedButRolledBack;
                state.Message =
                    "The submodule HEAD could not be captured as a verifiable Git commit.";
                return Failure(state.Message);
            }

            if (!GitUtility.TryVerifyRepositoryCommitFetchable(
                    resolvedRepositoryUrl,
                    assessment.HeadCommit,
                    out string fetchError,
                    cancellationToken))
            {
                state.Outcome = GitOperationCompletionOutcome.FailedButRolledBack;
                state.Message = LocalOnlyCommitReadOnlyMessage + " " + fetchError;
                return Failure(state.Message);
            }

            if (!PackageManifestGitDependencyStore.TryBuildGitSpec(
                    resolvedRepositoryUrl,
                    assessment.HeadCommit,
                    out string spec,
                    out string specError))
            {
                state.Outcome = GitOperationCompletionOutcome.FailedButRolledBack;
                state.Message = specError;
                return Failure(specError);
            }

            if (!PackageManifestGitDependencyStore.TryAddDependency(
                    info.PackageName,
                    spec,
                    out PackageManifestDependencyMutation manifestMutation,
                    out string manifestError))
            {
                state.Outcome = GitOperationCompletionOutcome.FailedButRolledBack;
                state.Message = manifestError;
                return Failure(manifestError);
            }

            if (!TryVerifyExactPinnedDependency(
                    info.PackageName,
                    spec,
                    out string preRemovalTargetError))
            {
                bool rolledBack = manifestMutation.TryRollback(
                    out string preRemovalRollbackError);
                state.Outcome = GitOperationCompletionOutcome.FailedButRolledBack;
                state.Message =
                    "The source submodule was preserved because the exact pinned " +
                    "manifest dependency could not be verified immediately before removal. " +
                    preRemovalTargetError +
                    (rolledBack || string.IsNullOrWhiteSpace(preRemovalRollbackError)
                        ? string.Empty
                        : " A concurrent manifest edit was preserved: " +
                          preRemovalRollbackError);
                return Failure(state.Message);
            }

            bool removed = GitUtility.TryRemoveSubmodule(
                info.PackagePath,
                assessment,
                discardLocalWork,
                out string removeError,
                out GitOperationCompletionOutcome removeOutcome,
                cancellationToken);
            if (!removed || removeOutcome != GitOperationCompletionOutcome.Succeeded)
            {
                if (removeOutcome == GitOperationCompletionOutcome.FailedUnsafe)
                {
                    state.Outcome = GitOperationCompletionOutcome.FailedUnsafe;
                    if (!TryEnsureExactPinnedDependency(
                            info.PackageName,
                            spec,
                            out bool restoredTarget,
                            out string targetRepairError))
                    {
                        state.Message =
                            "Submodule removal entered an uncertain state and the " +
                            "exact pinned read-only dependency is missing. It could " +
                            "not be restored without overwriting the current manifest: " +
                            targetRepairError + " " +
                            (removeError ?? string.Empty);
                        return Failure(state.Message);
                    }

                    state.Message = restoredTarget
                        ? "Submodule removal entered an uncertain state, so the missing pinned read-only dependency was safely restored. " +
                          (removeError ?? string.Empty)
                        : "The pinned read-only dependency was retained because submodule removal entered an uncertain state. " +
                          (removeError ?? string.Empty);
                    return Failure(state.Message);
                }

                if (!manifestMutation.TryRollback(out string rollbackError))
                {
                    state.Outcome = GitOperationCompletionOutcome.FailedUnsafe;
                    state.Message =
                        "Submodule removal was rolled back, but the temporary " +
                        "read-only dependency could not be removed without overwriting " +
                        "a concurrent manifest edit. " + rollbackError;
                    return Failure(state.Message);
                }

                state.Outcome = GitOperationCompletionOutcome.FailedButRolledBack;
                state.Message =
                    "The temporary read-only dependency was rolled back because " +
                    (string.IsNullOrWhiteSpace(removeError)
                        ? "the submodule could not be removed safely."
                        : removeError);
                return Failure(state.Message);
            }

            if (!TryEnsureExactPinnedDependency(
                    info.PackageName,
                    spec,
                    out bool repairedTarget,
                    out string postRemovalTargetError))
            {
                state.Outcome = GitOperationCompletionOutcome.FailedUnsafe;
                state.Message =
                    "The submodule was removed, but the exact pinned read-only " +
                    "dependency is missing and could not be restored without " +
                    "overwriting the current manifest: " +
                    postRemovalTargetError;
                return Failure(state.Message);
            }

            state.ConvertedSuccessfully = true;
            state.Outcome = GitOperationCompletionOutcome.Succeeded;
            state.Message =
                $"Converted {info.PackageName} to a read-only Git package pinned to {assessment.HeadCommit}." +
                (repairedTarget
                    ? " The missing manifest target was safely restored after removal."
                    : string.Empty);
            return Success(state.Message);
        }

        private static CommandResult RollBackFailedAdd(
            AddSubmodulePlan plan,
            string message,
            CommandResult commandResult,
            GitPackageConversionTaskState state,
            CancellationToken cancellationToken)
        {
            if (commandResult != null && !commandResult.TerminationConfirmed)
            {
                state.Outcome = GitOperationCompletionOutcome.FailedUnsafe;
                state.Message = message +
                    " Process-tree termination could not be confirmed, so cleanup was skipped.";
                return commandResult;
            }

            bool rolledBack = GitUtility.TryCleanupFailedAdd(
                plan,
                out string cleanupMessage,
                cancellationToken);
            state.Outcome = rolledBack
                ? GitOperationCompletionOutcome.FailedButRolledBack
                : GitOperationCompletionOutcome.FailedUnsafe;
            state.Message = message + (string.IsNullOrWhiteSpace(cleanupMessage)
                ? rolledBack ? " The original read-only package was preserved." : string.Empty
                : " " + cleanupMessage);
            return Failure(state.Message);
        }

        private static string ValidateToReadOnlyConfirmation(
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
                    GitUtility.NormalizePath(path),
                    StringComparison.Ordinal))
            {
                return "The conversion assessment belongs to a different package. Inspect the selected submodule again.";
            }

            if (confirmedAssessment.HasUnverifiedWorktreeContents)
            {
                return "The package directory contains unverified files that the Unity UI will never discard. " +
                       "Move them to safety and leave the directory empty before converting.";
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

        private static bool TryVerifyExactPinnedDependency(
            string packageName,
            string expectedSpec,
            out string error)
        {
            if (!PackageManifestGitDependencyStore.TryGetProjectDependency(
                    packageName,
                    out PackageManifestGitDependency dependency,
                    out error))
            {
                return false;
            }

            if (!string.Equals(
                    dependency.Spec,
                    expectedSpec,
                    StringComparison.Ordinal))
            {
                error =
                    $"The direct dependency for {packageName} no longer matches " +
                    "the exact commit selected for conversion.";
                return false;
            }

            error = string.Empty;
            return true;
        }

        private static bool TryEnsureExactPinnedDependency(
            string packageName,
            string expectedSpec,
            out bool restored,
            out string error)
        {
            restored = false;
            if (TryVerifyExactPinnedDependency(
                    packageName,
                    expectedSpec,
                    out error))
            {
                return true;
            }

            string verificationError = error;
            if (!PackageManifestGitDependencyStore.TryAddDependency(
                    packageName,
                    expectedSpec,
                    out PackageManifestDependencyMutation repairMutation,
                    out string repairError))
            {
                error = verificationError + " " + repairError;
                return false;
            }

            if (!TryVerifyExactPinnedDependency(
                    packageName,
                    expectedSpec,
                    out string repairedVerificationError))
            {
                error = repairedVerificationError;
                return false;
            }

            restored = repairMutation?.Changed == true;
            error = string.Empty;
            return true;
        }

        private static bool TryVerifySubmoduleHead(
            string path,
            string expectedCommit,
            out string error,
            CancellationToken cancellationToken)
        {
            CommandResult result = GitUtility.RunGit(
                GitUtility.BuildReadSubmoduleHeadArguments(path),
                GitUtility.ProjectRoot,
                5000,
                cancellationToken);
            if (result == null || !result.IsSuccess ||
                !string.Equals(
                    result.StdOut?.Trim(),
                    expectedCommit?.Trim(),
                    StringComparison.OrdinalIgnoreCase))
            {
                error = result == null || !result.IsSuccess
                    ? GitUtility.BuildCommandError(
                        "The converted submodule HEAD could not be verified",
                        result)
                    : "The converted submodule HEAD does not match Unity's previously resolved commit.";
                return false;
            }

            error = string.Empty;
            return true;
        }

        private static void NotifyCompletion(
            string packageName,
            GitPackageConversionDirection direction,
            GitPackageConversionTaskState state,
            CommandResult result,
            GitOperationCompletionOutcome outcome,
            Action<GitPackageConversionCompletion> onComplete)
        {
            bool success = outcome == GitOperationCompletionOutcome.Succeeded &&
                           result != null &&
                           result.IsSuccess &&
                           state.ConvertedSuccessfully;
            string message = string.IsNullOrWhiteSpace(state.Message)
                ? result?.StdErr ?? "The conversion did not complete successfully."
                : state.Message;
            if (success && !string.IsNullOrWhiteSpace(result.CompletionWarning))
            {
                message = string.IsNullOrWhiteSpace(message)
                    ? result.CompletionWarning
                    : message.TrimEnd() + " " +
                      result.CompletionWarning.Trim();
            }
            if (result != null && result.TerminationConfirmed &&
                outcome != GitOperationCompletionOutcome.FailedUnsafe)
            {
                PackageManagerSubmoduleSnapshot.Refresh();
            }

            onComplete?.Invoke(new GitPackageConversionCompletion
            {
                Success = success,
                Message = message,
                Outcome = outcome,
                Direction = direction,
                PackageName = packageName
            });
        }

        internal static string BuildUnavailableMessage()
        {
            if (PackageManagerProjectResolutionService.IsBusy)
                return PackageManagerProjectResolutionService.BuildUnavailableMessage();
            if (PackageManagerReadOnlyGitInstallService.IsBusy)
                return "Wait for the current Unity Package Manager operation to finish.";
            if (!string.IsNullOrWhiteSpace(GitOperationService.RecoveryWarning))
                return GitOperationService.RecoveryWarning.Trim();
            if (GitOperationService.IsBusy &&
                !string.IsNullOrWhiteSpace(GitOperationService.ActiveLabel))
            {
                return $"Wait for {GitOperationService.ActiveLabel.Trim()} to finish.";
            }

            return "Wait for current package scans and repository operations to finish.";
        }

        private static bool IsSelfConversion(string packageName)
        {
            return string.Equals(
                packageName?.Trim(),
                ManagerPackageName,
                StringComparison.Ordinal);
        }

        private static string BuildSelfConversionMessage()
        {
            return "This package cannot convert itself while its Editor code owns " +
                   "the recovery workflow. Install it from another project or use Git manually.";
        }

        private static bool IsValidCommit(string commit)
        {
            return !string.IsNullOrWhiteSpace(commit) &&
                   CommitRegex.IsMatch(commit.Trim());
        }

        private static CommandResult Success(string message)
        {
            return new CommandResult
            {
                ExitCode = 0,
                StdOut = message ?? string.Empty,
                StdErr = string.Empty,
                TerminationConfirmed = true
            };
        }

        private static CommandResult Failure(string message)
        {
            bool terminationUnconfirmed =
                GitUtility.ConsumeUnconfirmedCommandTermination();
            return new CommandResult
            {
                ExitCode = -1,
                StdOut = string.Empty,
                StdErr = message ?? "The conversion failed safely.",
                TerminationConfirmed = !terminationUnconfirmed
            };
        }
    }
}
