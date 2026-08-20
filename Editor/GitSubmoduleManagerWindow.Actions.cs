using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using UnityEditor;
using UnityEngine;

namespace MartinCalander.GitSubmoduleManager.Editor
{
    public partial class GitSubmoduleManagerWindow
    {
        internal enum InitialLoadStage
        {
            Git,
            InstalledPackages,
            GitHub,
            GitHubAuthentication
        }

        internal sealed class InitialLoadResult
        {
            public int LoadGeneration;
            public long RepositoryGeneration;
            public bool GitAvailable;
            public string GitVersion = string.Empty;
            public string GitError = string.Empty;
            public bool GhAvailable;
            public string GhVersion = string.Empty;
            public string GhError = string.Empty;
            public bool GhAuthenticated;
            public string GhAuthError = string.Empty;
            public List<GitPackageInfo> Packages;
            public string PackagesError = string.Empty;
            public bool PackagesSuccess;
            public string FatalError = string.Empty;
            public bool IsFinal;
            public bool GitHubDeferred;
        }

        private sealed class InstalledLoadResult
        {
            public int LoadGeneration;
            public long RepositoryGeneration;
            public List<GitPackageInfo> Packages;
            public string Error = string.Empty;
            public bool Success;
        }

        private sealed class AddTaskState
        {
            public GitOperationCompletionOutcome Outcome = GitOperationCompletionOutcome.FailedUnsafe;
            public string Message = string.Empty;
            public bool AddedSuccessfully;
        }

        private sealed class UpdatePreviewTaskState
        {
            public SubmoduleUpdatePlan Plan;
            public string TargetCommit = string.Empty;
            public string TargetLabel = string.Empty;
            public string SafeError = string.Empty;
            public GitOperationCompletionOutcome Outcome = GitOperationCompletionOutcome.FailedUnsafe;
        }

        private sealed class UpdateApplyTaskState
        {
            public GitOperationCompletionOutcome Outcome = GitOperationCompletionOutcome.FailedUnsafe;
            public string Message = string.Empty;
            public bool UpdatedSuccessfully;
        }

        private sealed class InitializeTaskState
        {
            public string SafeError = string.Empty;
            public string Message = string.Empty;
            public GitOperationCompletionOutcome Outcome = GitOperationCompletionOutcome.FailedUnsafe;
            public bool InitializedSuccessfully;
        }

        private sealed class RemovalAssessmentTaskState
        {
            public SubmoduleRemovalAssessment Assessment;
            public string Error = string.Empty;
            public GitOperationCompletionOutcome Outcome = GitOperationCompletionOutcome.FailedUnsafe;
        }

        private sealed class RemovalTaskState
        {
            public GitOperationCompletionOutcome Outcome = GitOperationCompletionOutcome.FailedUnsafe;
            public string Message = string.Empty;
            public bool RemovedSuccessfully;
        }

        private sealed class BranchChangeTaskState
        {
            public GitOperationCompletionOutcome Outcome = GitOperationCompletionOutcome.FailedUnsafe;
            public string Message = string.Empty;
            public bool ChangedSuccessfully;
        }

        private void RunInitialLoad(
            int generation,
            long repositoryGeneration,
            CancellationToken cancellationToken)
        {
            var result = new InitialLoadResult
            {
                LoadGeneration = generation,
                RepositoryGeneration = repositoryGeneration
            };
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    result.GitAvailable = GitUtility.IsGitAvailable(
                        out var gitVersionResult,
                        out var gitErrorResult,
                        cancellationToken);
                    result.GitVersion = gitVersionResult;
                    result.GitError = gitErrorResult;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    RecordInitialLoadFailure(result, InitialLoadStage.Git, ex);
                }

                cancellationToken.ThrowIfCancellationRequested();
                if (result.GitAvailable)
                {
                    try
                    {
                        result.PackagesSuccess = GitUtility.TryGetSubmodules(
                            out var packages,
                            out var packagesError,
                            cancellationToken);
                        result.Packages = packages;
                        result.PackagesError = packagesError;
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        RecordInitialLoadFailure(result, InitialLoadStage.InstalledPackages, ex);
                    }
                }

                PublishInitialGitStageResult(result, generation);

                cancellationToken.ThrowIfCancellationRequested();
                if (!ShouldRunInitialGitHubStage(IsSharedGitHubAuthenticationBlocked))
                {
                    result.GitHubDeferred = true;
                    result.IsFinal = true;
                    PublishInitialLoadResult(result, generation);
                    return;
                }

                try
                {
                    result.GhAvailable = GitHubUtility.IsGhAvailable(
                        cancellationToken,
                        out var ghVersionResult,
                        out var ghErrorResult,
                        out bool ghProbeDeferred);
                    result.GhVersion = ghVersionResult;
                    result.GhError = ghErrorResult;
                    if (ghProbeDeferred)
                    {
                        result.GitHubDeferred = true;
                        result.IsFinal = true;
                        PublishInitialLoadResult(result, generation);
                        return;
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    RecordInitialLoadFailure(result, InitialLoadStage.GitHub, ex);
                }

                cancellationToken.ThrowIfCancellationRequested();
                if (result.GhAvailable)
                {
                    try
                    {
                        result.GhAuthenticated = GitHubUtility.IsAuthenticated(
                            cancellationToken,
                            out var ghAuthenticationError,
                            out bool authenticationProbeDeferred);
                        if (authenticationProbeDeferred)
                        {
                            result.GitHubDeferred = true;
                            result.IsFinal = true;
                            PublishInitialLoadResult(result, generation);
                            return;
                        }
                        result.GhAuthError = result.GhAuthenticated
                            ? string.Empty
                            : ghAuthenticationError;
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        RecordInitialLoadFailure(
                            result,
                            InitialLoadStage.GitHubAuthentication,
                            ex);
                    }
                }

                cancellationToken.ThrowIfCancellationRequested();
                result.IsFinal = true;
                PublishInitialLoadResult(result, generation);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Lifecycle teardown intentionally cancels repository reads.
            }
            catch (Exception ex)
            {
                if (generation != Volatile.Read(ref initialLoadGeneration) ||
                    cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                result.FatalError = BuildInitialLoadFailureMessage(
                    "The initial package scan failed unexpectedly",
                    ex);
                result.IsFinal = true;
                PublishInitialLoadResult(result, generation);
            }
        }

        private void PublishInitialGitStageResult(InitialLoadResult result, int generation)
        {
            if (result == null || generation != Volatile.Read(ref initialLoadGeneration))
                return;

            pendingInitialGitStageResult = new InitialLoadResult
            {
                LoadGeneration = generation,
                RepositoryGeneration = result.RepositoryGeneration,
                GitAvailable = result.GitAvailable,
                GitVersion = result.GitVersion,
                GitError = result.GitError,
                Packages = result.Packages,
                PackagesError = result.PackagesError,
                PackagesSuccess = result.PackagesSuccess,
                IsFinal = false
            };
        }

        private void PublishInitialLoadResult(InitialLoadResult result, int generation)
        {
            if (result == null)
                return;

            result.LoadGeneration = generation;
            if (generation == Volatile.Read(ref initialLoadGeneration))
                pendingLoadResult = result;
        }

        internal static bool ShouldRunInitialGitHubStage(bool sharedAuthenticationBlocked)
        {
            return !sharedAuthenticationBlocked;
        }

        internal static void RecordInitialLoadFailure(
            InitialLoadResult result,
            InitialLoadStage stage,
            Exception exception)
        {
            if (result == null)
                return;

            switch (stage)
            {
                case InitialLoadStage.Git:
                    result.GitAvailable = false;
                    result.GitVersion = string.Empty;
                    result.GitError = BuildInitialLoadFailureMessage(
                        "Git detection failed unexpectedly",
                        exception);
                    break;
                case InitialLoadStage.InstalledPackages:
                    result.PackagesSuccess = false;
                    result.Packages = new List<GitPackageInfo>();
                    result.PackagesError = BuildInitialLoadFailureMessage(
                        "The installed package scan failed unexpectedly",
                        exception);
                    break;
                case InitialLoadStage.GitHub:
                    result.GhAvailable = false;
                    result.GhAuthenticated = false;
                    result.GhVersion = string.Empty;
                    result.GhAuthError = string.Empty;
                    result.GhError = BuildInitialLoadFailureMessage(
                        "GitHub CLI detection failed unexpectedly",
                        exception);
                    break;
                case InitialLoadStage.GitHubAuthentication:
                    result.GhAuthenticated = false;
                    result.GhAuthError = BuildInitialLoadFailureMessage(
                        "GitHub authentication check failed unexpectedly",
                        exception);
                    break;
            }
        }

        private static string BuildInitialLoadFailureMessage(string summary, Exception exception)
        {
            string safeSummary = string.IsNullOrWhiteSpace(summary)
                ? "Dependency check failed unexpectedly"
                : summary.Trim();
            string detail = GitHubUtility.SanitizeUiDiagnostic(exception?.Message);
            return GitHubUtility.SanitizeUiDiagnostic(
                string.IsNullOrWhiteSpace(detail)
                    ? safeSummary + "."
                    : safeSummary + ": " + detail);
        }

        private bool ApplyLoadResult(InitialLoadResult result)
        {
            if (!IsBackgroundLoadResultCurrent(
                    result.LoadGeneration,
                    Volatile.Read(ref initialLoadGeneration),
                    result.RepositoryGeneration,
                    GitOperationService.RepositoryGeneration))
            {
                backgroundLoadDeferred = true;
                return false;
            }

            if (!string.IsNullOrWhiteSpace(result.FatalError))
            {
                gitAvailable = false;
                gitVersion = string.Empty;
                gitError = result.FatalError;
                installedPackages = new List<GitPackageInfo>();
                installedStatus = result.FatalError;
                installedStatusType = MessageType.Error;
                dependencyCheckRequested = false;
                dependencyCheckIncludesGitHub = false;
                lastInstalledRefreshTime = EditorApplication.timeSinceStartup;
                lastRefreshDateTime = DateTime.Now;
                return true;
            }

            gitAvailable = result.GitAvailable;
            gitVersion = result.GitVersion;
            gitError = result.GitError;
            if (result.IsFinal && !result.GitHubDeferred)
            {
                ghAvailable = result.GhAvailable;
                ghVersion = result.GhVersion;
                ghError = result.GhError;
                ghAuthenticated = result.GhAuthenticated;
                ghAuthError = result.GhAuthError;
            }

            if (result.GitAvailable)
            {
                if (result.PackagesSuccess)
                {
                    installedPackages = result.Packages ?? new List<GitPackageInfo>();
                    installedStatus = string.Empty;
                    installedStatusType = MessageType.None;
                }
                else
                {
                    installedStatus = result.PackagesError;
                    installedStatusType = MessageType.Error;
                    installedPackages = new List<GitPackageInfo>();
                }
            }
            else
            {
                installedPackages = new List<GitPackageInfo>();
                installedStatus = "Git is required to list packages.";
                installedStatusType = MessageType.Warning;
            }

            MarkInstalledRepos();
            selectedInstalledIndex = Mathf.Clamp(selectedInstalledIndex, -1, installedPackages.Count - 1);
            lastInstalledRefreshTime = EditorApplication.timeSinceStartup;
            lastRefreshDateTime = DateTime.Now;

            if (result.IsFinal && result.GitHubDeferred)
                backgroundLoadDeferred = true;

            if (result.IsFinal && !result.GitHubDeferred && dependencyCheckRequested)
            {
                bool shouldReportGitHub =
                    dependencyCheckIncludesGitHub ||
                    currentTab == Tab.Discover ||
                    showWelcomeScreen;
                if (shouldReportGitHub)
                {
                    discoveryCoordinator.ResetGitHubIdentityState();
                    selectedRepoIndex = -1;
                    listScroll = Vector2.zero;
                    detailsScroll = Vector2.zero;
                }

                dependencyCheckRequested = false;
                dependencyCheckIncludesGitHub = false;
                if (!gitAvailable)
                {
                    installStatus = "Git is still unavailable. Review the installer error or use the official download page.";
                    installStatusType = MessageType.Warning;
                }
                else if (shouldReportGitHub && !ghAvailable)
                {
                    installStatus = "GitHub CLI is still unavailable. Manual installation through the + button remains available.";
                    installStatusType = MessageType.Warning;
                }
                else if (shouldReportGitHub && !ghAuthenticated)
                {
                    installStatus = "GitHub CLI is installed but not authenticated. Run 'gh auth login' in a terminal.";
                    installStatusType = MessageType.Warning;
                }
                else
                {
                    installStatus = "Command-line tools checked successfully.";
                    installStatusType = MessageType.Info;
                }

                if (shouldReportGitHub &&
                    currentTab == Tab.Discover &&
                    ghAvailable &&
                    ghAuthenticated)
                {
                    RefreshAvailable();
                }
            }

            return true;
        }

        private void BeginRemove(GitPackageInfo package, string typeLabel)
        {
            if (!CanEnterDeferredWindowAction(this, isWindowEnabled) || package == null)
                return;

            operationStatus = string.Empty;
            operationStatusType = MessageType.None;
            if (IsRepositoryOperationBusy)
            {
                SetInstalledOperationError("Another repository operation is already running.");
                return;
            }

            var state = new RemovalAssessmentTaskState();
            StartTaskOperation(
                $"Inspecting {package.PackageName ?? package.Name} before removal...",
                cancellationToken =>
                {
                    bool success = GitUtility.TryAssessSubmoduleRemoval(
                        package.Path,
                        out SubmoduleRemovalAssessment assessment,
                        out string assessmentError,
                        cancellationToken);
                    state.Assessment = assessment;
                    state.Error = assessmentError;
                    state.Outcome = success && assessment != null
                        ? GitOperationCompletionOutcome.Succeeded
                        : GitOperationCompletionOutcome.FailedButRolledBack;
                    return success
                        ? BuildOperationResult(true, string.Empty)
                        : SafeTaskFailure(assessmentError);
                },
                _ => state.Outcome,
                (result, effectiveOutcome) =>
                    NotifyRemovalAssessmentComplete(package, typeLabel, state, result, effectiveOutcome),
                false,
                new GitOperationMetadata { PackagePath = package.Path, Phase = "inspect-before-remove" });
        }

        private void NotifyRemovalAssessmentComplete(
            GitPackageInfo package,
            string typeLabel,
            RemovalAssessmentTaskState state,
            CommandResult result,
            GitOperationCompletionOutcome effectiveOutcome)
        {
            if (!CanEnterDeferredWindowAction(this, isWindowEnabled))
                return;

            if (effectiveOutcome != GitOperationCompletionOutcome.Succeeded ||
                result == null ||
                !result.IsSuccess ||
                state?.Assessment == null)
            {
                SetInstalledOperationError(
                    BuildEffectiveCompletionError(
                        "Failed to inspect package before removal",
                        state?.Error,
                        result,
                        effectiveOutcome));
                return;
            }

            // End the operation reservation and release Unity's reload lock
            // before showing a modal that can remain open indefinitely.
            EditorApplication.delayCall += () =>
            {
                if (CanEnterDeferredWindowAction(this, isWindowEnabled))
                    ContinueRemovalAfterAssessment(package, typeLabel, state.Assessment);
            };
        }

        private void ContinueRemovalAfterAssessment(
            GitPackageInfo package,
            string typeLabel,
            SubmoduleRemovalAssessment assessment)
        {
            if (!CanEnterDeferredWindowAction(this, isWindowEnabled) ||
                package == null ||
                assessment == null)
                return;

            if (assessment.HasUnverifiedWorktreeContents)
            {
                EditorUtility.DisplayDialog(
                    "Package Directory Must Be Emptied",
                    "The package directory contains files but is not an initialized submodule worktree. " +
                    "Move those files to safety and leave the directory empty before removing the gitlink. " +
                    "Git Submodule Manager will not discard unverified files.",
                    "OK");
                return;
            }

            if (!ConfirmPackageRemoval(package, typeLabel))
                return;

            bool discardLocalWork = false;
            if (!assessment.IsSafe)
            {
                string warning =
                    assessment.BuildWarning() + "\n\n" +
                    "Git Submodule Manager will preserve the submodule object database for recovery, but the package worktree and parent gitlink changes will be removed. " +
                    "This cannot be undone from the Unity UI.";
                if (!EditorUtility.DisplayDialog(
                        "Local Work Would Be Discarded",
                        warning,
                        "Discard Local Work and Remove",
                        "Keep Package"))
                    return;

                discardLocalWork = true;
            }

            string packagePath = package.Path;
            string displayName = package.PackageName ?? package.Name;
            bool isCurrentPackage = IsCurrentPackage(package);
            SubmoduleRemovalAssessment confirmedAssessment = assessment;
            bool confirmedDiscard = discardLocalWork;
            var state = new RemovalTaskState();
            StartTaskOperation(
                $"Removing {displayName}...",
                cancellationToken =>
                {
                    bool success = GitUtility.TryRemoveSubmodule(
                        packagePath,
                        confirmedAssessment,
                        confirmedDiscard,
                        out string removeError,
                        out GitOperationCompletionOutcome outcome,
                        cancellationToken);
                    state.Outcome = outcome;
                    state.Message = removeError;
                    state.RemovedSuccessfully = success &&
                                                outcome == GitOperationCompletionOutcome.Succeeded;
                    return BuildOperationResult(success, removeError);
                },
                _ => state.Outcome,
                (completed, effectiveOutcome) =>
                    NotifyRemoveComplete(displayName, isCurrentPackage, state, completed, effectiveOutcome),
                true,
                new GitOperationMetadata { PackagePath = packagePath, Phase = "remove" });
        }

        private void NotifyRemoveComplete(
            string displayName,
            bool isCurrentPackage,
            RemovalTaskState state,
            CommandResult result,
            GitOperationCompletionOutcome effectiveOutcome)
        {
            if (this == null)
                return;

            if (effectiveOutcome != GitOperationCompletionOutcome.Succeeded ||
                result == null ||
                !result.IsSuccess ||
                state?.RemovedSuccessfully != true)
            {
                SetInstalledOperationError(
                    BuildEffectiveCompletionError(
                        "Failed to remove package",
                        state?.Message,
                        result,
                        effectiveOutcome));
                return;
            }

            operationStatus = $"Removed {displayName}. Review and commit the parent repository changes.";
            operationStatusType = MessageType.Info;
            installedActionStatus = string.Empty;
            selectedInstalledIndex = -1;
            if (!isCurrentPackage)
                QueueInstalledRefreshAfterOperation();
            if (isCurrentPackage)
                Close();
        }

        private void BeginInitialize(GitPackageInfo package)
        {
            var state = new InitializeTaskState();
            StartTaskOperation(
                $"Initializing {package.PackageName ?? package.Name} at its pinned commit...",
                cancellationToken =>
                {
                    if (!GitUtility.TryPrepareSubmoduleInitialization(
                            package.Path,
                            package.Url,
                            out string prepareError,
                            cancellationToken))
                    {
                        state.SafeError = prepareError;
                        state.Outcome = GitOperationCompletionOutcome.FailedButRolledBack;
                        return SafeTaskFailure(prepareError);
                    }

                    cancellationToken.ThrowIfCancellationRequested();
                    CommandResult initializeResult = GitUtility.RunGit(
                        GitUtility.BuildInitializeSubmoduleArguments(package.Path),
                        GitUtility.ProjectRoot,
                        120000,
                        cancellationToken);
                    if (initializeResult == null || !initializeResult.IsSuccess)
                        return initializeResult;

                    cancellationToken.ThrowIfCancellationRequested();
                    if (!GitUtility.TryVerifyInitializedSubmodule(
                            package.Path,
                            package.Url,
                            out string verifyError,
                            cancellationToken))
                    {
                        state.Message = verifyError;
                        state.Outcome = GitOperationCompletionOutcome.FailedUnsafe;
                        return new CommandResult
                        {
                            ExitCode = -1,
                            StdOut = initializeResult.StdOut,
                            StdErr = verifyError,
                            TerminationConfirmed = !GitUtility.ConsumeUnconfirmedCommandTermination()
                        };
                    }

                    state.InitializedSuccessfully = true;
                    state.Outcome = GitOperationCompletionOutcome.Succeeded;
                    state.Message = "Initialized at the commit pinned by the parent repository.";
                    return initializeResult;
                },
                _ => state.Outcome,
                (result, effectiveOutcome) =>
                    NotifyInitializeComplete(package, state, result, effectiveOutcome),
                true,
                new GitOperationMetadata { PackagePath = package.Path, Phase = "initialize-pinned" });
        }

        private void NotifyInitializeComplete(
            GitPackageInfo package,
            InitializeTaskState state,
            CommandResult result,
            GitOperationCompletionOutcome effectiveOutcome)
        {
            if (this == null)
                return;

            if (effectiveOutcome != GitOperationCompletionOutcome.Succeeded ||
                result == null ||
                !result.IsSuccess ||
                state?.InitializedSuccessfully != true)
            {
                SetInstalledOperationError(
                    BuildEffectiveCompletionError(
                        "Git initialization failed",
                        !string.IsNullOrWhiteSpace(state?.SafeError)
                            ? state.SafeError
                            : state?.Message,
                        result,
                        effectiveOutcome));
                return;
            }

            installedActionStatus = state?.Message ?? "Initialized at the commit pinned by the parent repository.";
            installedActionStatusType = MessageType.Info;
            operationStatus = installedActionStatus;
            operationStatusType = MessageType.Info;
            QueueInstalledRefreshAfterOperation();
        }

        private void BeginUpdate(GitPackageInfo package)
        {
            var state = new UpdatePreviewTaskState();
            StartTaskOperation(
                $"Fetching update information for {package.PackageName ?? package.Name}...",
                cancellationToken => RunUpdatePreviewTask(package, state, cancellationToken),
                _ => state.Outcome,
                (result, effectiveOutcome) =>
                    NotifyUpdateFetchComplete(package, state, result, effectiveOutcome),
                false,
                new GitOperationMetadata { PackagePath = package.Path, Phase = "update-fetch-and-preview" });
        }

        private static CommandResult RunUpdatePreviewTask(
            GitPackageInfo package,
            UpdatePreviewTaskState state,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!GitUtility.TryPrepareSubmoduleUpdate(
                    package.Path,
                    package.Url,
                    package.Branch,
                    out SubmoduleUpdatePlan plan,
                    out string prepareError,
                    cancellationToken))
            {
                state.SafeError = prepareError;
                state.Outcome = GitOperationCompletionOutcome.FailedButRolledBack;
                return SafeTaskFailure(prepareError);
            }

            state.Plan = plan;
            CommandResult fetchResult = GitUtility.RunGit(
                GitUtility.BuildFetchSubmoduleArguments(package.Path),
                GitUtility.ProjectRoot,
                120000,
                cancellationToken);
            if (fetchResult == null || !fetchResult.IsSuccess)
            {
                state.Outcome = fetchResult != null && fetchResult.TerminationConfirmed
                    ? GitOperationCompletionOutcome.FailedButRolledBack
                    : GitOperationCompletionOutcome.FailedUnsafe;
                return fetchResult;
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (!GitUtility.TryResolveSubmoduleRemoteTarget(
                    package.Path,
                    package.Branch,
                    package.Url,
                    out string targetCommit,
                    out string targetLabel,
                    out string resolveError,
                    cancellationToken))
            {
                state.SafeError = resolveError;
                state.Outcome = GitOperationCompletionOutcome.FailedButRolledBack;
                return SafeTaskFailure(resolveError);
            }

            state.TargetCommit = targetCommit;
            state.TargetLabel = targetLabel;
            if (state.Plan == null || string.IsNullOrWhiteSpace(state.TargetCommit))
            {
                state.SafeError = "The update preview returned an incomplete safety plan.";
                state.Outcome = GitOperationCompletionOutcome.FailedButRolledBack;
                return SafeTaskFailure(state.SafeError);
            }

            state.Outcome = GitOperationCompletionOutcome.Succeeded;
            return fetchResult;
        }

        private void NotifyUpdateFetchComplete(
            GitPackageInfo package,
            UpdatePreviewTaskState state,
            CommandResult result,
            GitOperationCompletionOutcome effectiveOutcome)
        {
            if (this == null)
                return;

            if (effectiveOutcome != GitOperationCompletionOutcome.Succeeded ||
                result == null ||
                !result.IsSuccess)
            {
                string fetchError = BuildEffectiveCompletionError(
                    "Failed to fetch package updates",
                    state?.SafeError,
                    result,
                    effectiveOutcome);
                if (result == null || !result.TerminationConfirmed)
                {
                    SetInstalledOperationError(
                        fetchError +
                        " Process-tree termination could not be confirmed. Inspect running Git/SSH processes before acknowledging recovery.");
                    return;
                }

                SetInstalledOperationError(fetchError);
                return;
            }

            SubmoduleUpdatePlan plan = state?.Plan;
            string targetCommit = state?.TargetCommit ?? string.Empty;
            string targetLabel = state?.TargetLabel ?? string.Empty;
            if (plan == null || string.IsNullOrWhiteSpace(targetCommit))
            {
                SetInstalledOperationError("The update preview returned an incomplete safety plan.");
                return;
            }

            // Release the reload lock before any human confirmation. The apply
            // task revalidates every captured value after the dialog.
            EditorApplication.delayCall += () =>
            {
                if (this != null)
                    ContinueUpdateAfterPreview(package, plan, targetCommit, targetLabel);
            };
        }

        private void ContinueUpdateAfterPreview(
            GitPackageInfo package,
            SubmoduleUpdatePlan plan,
            string targetCommit,
            string targetLabel)
        {
            if (this == null || plan == null)
                return;

            if (string.Equals(plan.StartingCommit, targetCommit, StringComparison.OrdinalIgnoreCase))
            {
                installedActionStatus = $"Already up to date on {targetLabel} ({ShortCommit(targetCommit)}).";
                installedActionStatusType = MessageType.Info;
                operationStatus = installedActionStatus;
                operationStatusType = MessageType.Info;
                return;
            }

            plan.ExpectedTargetCommit = targetCommit;
            string displayName = package.PackageName ?? package.Name;
            bool confirmed = EditorUtility.DisplayDialog(
                "Update Git Package?",
                $"Package: {displayName}\n" +
                $"Path: {package.Path}\n" +
                $"Tracked branch: {targetLabel}\n\n" +
                $"Current commit: {ShortCommit(plan.StartingCommit)}\n" +
                $"Fetched commit: {ShortCommit(targetCommit)}\n\n" +
                "Updating changes the package code loaded by Unity. Only continue if you trust this repository and the fetched revision.",
                "Update to Fetched Commit",
                "Cancel");
            if (!confirmed)
            {
                installedActionStatus = "Update cancelled after fetching remote information; the package worktree was not changed.";
                installedActionStatusType = MessageType.Info;
                operationStatus = installedActionStatus;
                operationStatusType = MessageType.Info;
                return;
            }

            BeginConfirmedUpdate(package, plan);
        }

        private void BeginConfirmedUpdate(GitPackageInfo package, SubmoduleUpdatePlan previewPlan)
        {
            if (this == null)
                return;

            var state = new UpdateApplyTaskState();
            StartTaskOperation(
                $"Updating {package.PackageName ?? package.Name}...",
                cancellationToken => RunConfirmedUpdateTask(
                    package,
                    previewPlan,
                    state,
                    cancellationToken),
                _ => state.Outcome,
                (result, effectiveOutcome) =>
                    NotifyUpdateComplete(state, result, effectiveOutcome),
                true,
                new GitOperationMetadata
                {
                    PackagePath = package.Path,
                    Phase = "update-checkout-and-verify",
                    StartCommit = previewPlan.StartingCommit
                });
        }

        private static CommandResult RunConfirmedUpdateTask(
            GitPackageInfo package,
            SubmoduleUpdatePlan previewPlan,
            UpdateApplyTaskState state,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!GitUtility.TryPrepareSubmoduleUpdate(
                    package.Path,
                    package.Url,
                    previewPlan.ExpectedBranch,
                    out SubmoduleUpdatePlan currentPlan,
                    out string prepareError,
                    cancellationToken))
            {
                state.Outcome = GitOperationCompletionOutcome.FailedButRolledBack;
                state.Message =
                    "The package changed after the update preview, so the update was cancelled. " + prepareError;
                return SafeTaskFailure(state.Message);
            }

            if (!string.Equals(
                    currentPlan.StartingCommit,
                    previewPlan.StartingCommit,
                    StringComparison.OrdinalIgnoreCase))
            {
                state.Outcome = GitOperationCompletionOutcome.FailedButRolledBack;
                state.Message =
                    "The package HEAD changed after the update preview. The fetched commit was not checked out.";
                return SafeTaskFailure(state.Message);
            }

            if (!GitUtility.TryResolveSubmoduleRemoteTarget(
                    package.Path,
                    package.Branch,
                    package.Url,
                    out string currentTarget,
                    out _,
                    out string resolveError,
                    cancellationToken))
            {
                state.Outcome = GitOperationCompletionOutcome.FailedButRolledBack;
                state.Message = resolveError;
                return SafeTaskFailure(resolveError);
            }

            if (!string.Equals(
                    currentTarget,
                    previewPlan.ExpectedTargetCommit,
                    StringComparison.OrdinalIgnoreCase))
            {
                state.Outcome = GitOperationCompletionOutcome.FailedButRolledBack;
                state.Message =
                    "The fetched remote target changed after the update preview. Fetch and review the update again.";
                return SafeTaskFailure(state.Message);
            }

            currentPlan.ExpectedTargetCommit = currentTarget;
            cancellationToken.ThrowIfCancellationRequested();
            if (!GitUtility.TryValidateSubmoduleUpdateSource(
                    package.Path,
                    package.Url,
                    out string sourceError,
                    cancellationToken))
            {
                state.Outcome = GitOperationCompletionOutcome.FailedButRolledBack;
                state.Message = sourceError;
                return SafeTaskFailure(sourceError);
            }
            if (!GitUtility.TryValidateSubmoduleConfiguredBranch(
                    package.Path,
                    currentPlan.ExpectedBranch,
                    out _,
                    out string branchError,
                    cancellationToken))
            {
                state.Outcome = GitOperationCompletionOutcome.FailedButRolledBack;
                state.Message = branchError;
                return SafeTaskFailure(branchError);
            }
            CommandResult checkoutResult = GitUtility.RunGit(
                GitUtility.BuildCheckoutSubmoduleArguments(package.Path, currentTarget),
                GitUtility.ProjectRoot,
                120000,
                cancellationToken);

            string updateError = string.Empty;
            if (checkoutResult != null && checkoutResult.IsSuccess)
            {
                if (GitUtility.TryVerifySubmoduleClean(
                        package.Path,
                        out string verifyError,
                        cancellationToken))
                {
                    var headResult = GitUtility.RunGit(
                        $"-C {GitUtility.Quote(package.Path)} rev-parse --verify HEAD",
                        GitUtility.ProjectRoot,
                        5000,
                        cancellationToken);
                    if (headResult.IsSuccess &&
                        string.Equals(
                            headResult.StdOut.Trim(),
                            currentTarget,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        state.UpdatedSuccessfully = true;
                        state.Outcome = GitOperationCompletionOutcome.Succeeded;
                        state.Message =
                            $"Updated {ShortCommit(currentPlan.StartingCommit)} → {ShortCommit(currentTarget)}. " +
                            "Review and commit the parent gitlink change.";
                        return checkoutResult;
                    }

                    updateError = headResult.IsSuccess
                        ? "Git checkout did not finish at the exact fetched commit."
                        : GitUtility.BuildCommandError("Failed to verify the updated package commit", headResult);
                }
                else
                {
                    updateError = verifyError;
                }
            }
            else
            {
                updateError = GitUtility.BuildCommandError("Git update failed", checkoutResult);
            }

            if (checkoutResult == null || !checkoutResult.TerminationConfirmed)
            {
                state.Outcome = GitOperationCompletionOutcome.FailedUnsafe;
                state.Message =
                    updateError +
                    " Process-tree termination could not be confirmed, so automatic recovery was skipped. Inspect the package and running Git/SSH processes before acknowledging recovery.";
                return checkoutResult;
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (GitUtility.TryRecoverFailedSubmoduleUpdate(
                    currentPlan,
                    out string recoveryError,
                    cancellationToken))
            {
                state.Outcome = GitOperationCompletionOutcome.FailedButRolledBack;
                state.Message = updateError + " The package was restored to its starting commit.";
                return checkoutResult;
            }

            state.Outcome = GitOperationCompletionOutcome.FailedUnsafe;
            state.Message =
                updateError + " Automatic recovery also failed: " + recoveryError +
                " Unity asset refresh remains paused; inspect the package immediately.";
            return checkoutResult;
        }

        private void PerformBranchChange(GitPackageInfo package, string branch)
        {
            var state = new BranchChangeTaskState();
            StartTaskOperation(
                $"Changing {package.PackageName ?? package.Name} to {branch}...",
                cancellationToken =>
                {
                    bool success = GitUtility.TrySetSubmoduleBranch(
                        package.Path,
                        branch,
                        out string branchError,
                        out GitOperationCompletionOutcome outcome,
                        cancellationToken);
                    state.Outcome = outcome;
                    state.Message = branchError;
                    state.ChangedSuccessfully = success &&
                                                outcome == GitOperationCompletionOutcome.Succeeded;
                    return BuildOperationResult(success, branchError);
                },
                _ => state.Outcome,
                (result, effectiveOutcome) =>
                    NotifyBranchChangeComplete(package, branch, state, result, effectiveOutcome),
                false,
                new GitOperationMetadata { PackagePath = package.Path, Phase = "set-tracked-branch" });
        }

        private void NotifyBranchChangeComplete(
            GitPackageInfo package,
            string branch,
            BranchChangeTaskState state,
            CommandResult result,
            GitOperationCompletionOutcome effectiveOutcome)
        {
            if (this == null)
                return;

            if (effectiveOutcome != GitOperationCompletionOutcome.Succeeded ||
                result == null ||
                !result.IsSuccess ||
                state?.ChangedSuccessfully != true)
            {
                SetInstalledOperationError(
                    BuildEffectiveCompletionError(
                        "Failed to change the tracked branch",
                        state?.Message,
                        result,
                        effectiveOutcome));
                return;
            }

            installedActionStatus = $"Tracked branch set to {branch}.";
            installedActionStatusType = MessageType.Info;
            repositoryCoordinator.ClearBranchCache(package.Url);
            QueueInstalledRefreshAfterOperation();
        }

        private void NotifyUpdateComplete(
            UpdateApplyTaskState state,
            CommandResult result,
            GitOperationCompletionOutcome effectiveOutcome)
        {
            if (this == null)
                return;

            bool success = effectiveOutcome == GitOperationCompletionOutcome.Succeeded &&
                           result != null &&
                           result.IsSuccess &&
                           state != null &&
                           state.UpdatedSuccessfully &&
                           state.Outcome == GitOperationCompletionOutcome.Succeeded;
            if (success)
            {
                installedActionStatus = state.Message;
                installedActionStatusType = MessageType.Info;
                operationStatus = state.Message;
                operationStatusType = MessageType.Info;
            }
            else
            {
                SetInstalledOperationError(
                    BuildEffectiveCompletionError(
                        "Failed to update package",
                        state?.Message ?? "The update returned no final safety state.",
                        result,
                        effectiveOutcome));
            }

            if (result != null &&
                result.TerminationConfirmed &&
                effectiveOutcome != GitOperationCompletionOutcome.FailedUnsafe)
                QueueInstalledRefreshAfterOperation();
        }

        private void StartAsyncOperation(
            string label,
            string fileName,
            string arguments,
            Func<CommandResult, GitOperationCompletionOutcome> resolveOutcome,
            Action<CommandResult, GitOperationCompletionOutcome> notifyComplete,
            int timeoutMs = CliCommandRunner.DefaultTimeoutMs,
            bool suppressAutoRefresh = false,
            GitOperationMetadata metadata = null)
        {
            Action startOperation = () => StartAsyncOperationCore(
                label,
                fileName,
                arguments,
                resolveOutcome,
                notifyComplete,
                timeoutMs,
                suppressAutoRefresh,
                metadata);
            if (!TryPrepareRepositoryMutation(label, startOperation))
                return;

            startOperation();
        }

        private void StartAsyncOperationCore(
            string label,
            string fileName,
            string arguments,
            Func<CommandResult, GitOperationCompletionOutcome> resolveOutcome,
            Action<CommandResult, GitOperationCompletionOutcome> notifyComplete,
            int timeoutMs,
            bool suppressAutoRefresh,
            GitOperationMetadata metadata)
        {
            try
            {
                if (!GitOperationService.TryStartCommand(
                        label,
                        fileName,
                        arguments,
                        timeoutMs,
                        suppressAutoRefresh,
                        resolveOutcome,
                        notifyComplete,
                        out string error,
                        metadata))
                {
                    SetInstalledOperationError(error);
                }
            }
            catch (Exception ex)
            {
                SetInstalledOperationError(BuildInitialLoadFailureMessage(
                    "The repository operation could not start",
                    ex));
            }
            Repaint();
        }

        private void StartTaskOperation(
            string label,
            Func<CommandResult> task,
            Func<CommandResult, GitOperationCompletionOutcome> resolveOutcome,
            Action<CommandResult, GitOperationCompletionOutcome> notifyComplete,
            bool suppressAutoRefresh,
            GitOperationMetadata metadata = null)
        {
            StartTaskOperation(
                label,
                cancellationToken =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return task();
                },
                resolveOutcome,
                notifyComplete,
                suppressAutoRefresh,
                metadata);
        }

        private void StartTaskOperation(
            string label,
            Func<CancellationToken, CommandResult> task,
            Func<CommandResult, GitOperationCompletionOutcome> resolveOutcome,
            Action<CommandResult, GitOperationCompletionOutcome> notifyComplete,
            bool suppressAutoRefresh,
            GitOperationMetadata metadata = null)
        {
            Action startOperation = () => StartTaskOperationCore(
                label,
                task,
                resolveOutcome,
                notifyComplete,
                suppressAutoRefresh,
                metadata);
            if (!TryPrepareRepositoryMutation(label, startOperation))
                return;

            startOperation();
        }

        private void StartTaskOperationCore(
            string label,
            Func<CancellationToken, CommandResult> task,
            Func<CommandResult, GitOperationCompletionOutcome> resolveOutcome,
            Action<CommandResult, GitOperationCompletionOutcome> notifyComplete,
            bool suppressAutoRefresh,
            GitOperationMetadata metadata)
        {
            try
            {
                if (!GitOperationService.TryStartTask(
                        label,
                        task,
                        suppressAutoRefresh,
                        resolveOutcome,
                        notifyComplete,
                        out string error,
                        metadata))
                {
                    SetInstalledOperationError(error);
                }
            }
            catch (Exception ex)
            {
                SetInstalledOperationError(BuildInitialLoadFailureMessage(
                    "The repository operation could not start",
                    ex));
            }
            Repaint();
        }

        private bool TryPrepareRepositoryMutation(string label, Action startOperation)
        {
            if (startOperation == null)
            {
                SetInstalledOperationError("The repository operation was not provided.");
                Repaint();
                return false;
            }

            if (DeferredRepositoryMutationQueue.HasAnyPending || IsRepositoryOperationExecutionBusy)
            {
                SetInstalledOperationError("Another repository operation is already running or waiting to start.");
                Repaint();
                return false;
            }

            if (!string.IsNullOrWhiteSpace(GitOperationService.RecoveryWarning))
            {
                SetInstalledOperationError(GitOperationService.RecoveryWarning);
                Repaint();
                return false;
            }

            InvalidateRepositoryReadResultsForMutation();
            if (RequestRepositoryReadCancellation(out _))
                return true;

            if (!deferredRepositoryMutation.TryEnqueue(label, startOperation))
            {
                SetInstalledOperationError("Another repository operation is already waiting to start.");
                Repaint();
                return false;
            }

            string waitingStatus =
                "Waiting for the current package scan to stop safely. " +
                deferredRepositoryMutation.Label;
            installedActionStatus = waitingStatus;
            installedActionStatusType = MessageType.Info;
            operationStatus = waitingStatus;
            operationStatusType = MessageType.Info;
            Repaint();
            return false;
        }

        private void InvalidateRepositoryReadResultsForMutation()
        {
            Interlocked.Increment(ref initialLoadGeneration);
            Interlocked.Increment(ref installedLoadGeneration);
            pendingInitialGitStageResult = null;
            pendingLoadResult = null;
            pendingInstalledLoadResult = null;
            isInitialLoading = false;
            isInstalledLoading = false;
            backgroundLoadDeferred = true;
        }

        private void UpdateDeferredRepositoryMutation()
        {
            if (!deferredRepositoryMutation.HasPending)
                return;

            if (!string.IsNullOrWhiteSpace(GitOperationService.RecoveryWarning))
            {
                deferredRepositoryMutation.Clear();
                SetInstalledOperationError(
                    "The queued operation was not started because repository recovery requires review. " +
                    GitOperationService.RecoveryWarning);
                Repaint();
                return;
            }

            bool readersStopped = RequestRepositoryReadCancellation(out _);
            bool canStart = readersStopped && !IsRepositoryOperationExecutionBusy;
            if (!deferredRepositoryMutation.TryDequeueWhenReady(canStart, out Action startOperation))
            {
                RepaintProgress();
                return;
            }

            operationStatus = "Starting the queued repository operation...";
            operationStatusType = MessageType.Info;
            startOperation();
        }

        private static CommandResult BuildOperationResult(bool success, string error)
        {
            bool terminationUnconfirmed = !success && GitUtility.ConsumeUnconfirmedCommandTermination();
            return new CommandResult
            {
                ExitCode = success ? 0 : -1,
                StdOut = string.Empty,
                StdErr = success ? string.Empty : error ?? "The Git operation failed.",
                TerminationConfirmed = success || !terminationUnconfirmed
            };
        }

        private static string BuildEffectiveCompletionError(
            string summary,
            string operationMessage,
            CommandResult result,
            GitOperationCompletionOutcome effectiveOutcome)
        {
            // Finalization owns the last word on repository safety. Its result is
            // deliberately failure-shaped and includes recovery instructions, so
            // a task-level success message must never hide a later downgrade.
            if (effectiveOutcome == GitOperationCompletionOutcome.FailedUnsafe)
                return GitUtility.BuildCommandError(summary, result);

            return string.IsNullOrWhiteSpace(operationMessage)
                ? GitUtility.BuildCommandError(summary, result)
                : operationMessage;
        }

        private void SetInstalledOperationError(string error)
        {
            installedActionStatus = error;
            installedActionStatusType = MessageType.Error;
            operationStatus = error;
            operationStatusType = MessageType.Error;
        }

        private static string ShortCommit(string commit)
        {
            if (string.IsNullOrWhiteSpace(commit))
                return "unknown";
            string value = commit.Trim();
            return value.Length > 7 ? value.Substring(0, 7) : value;
        }

        internal static bool CanStartCliInstall(
            bool isGitHubInteractionBusy,
            bool backgroundLoadsDraining,
            bool recoveryRequiresReview)
        {
            return !isGitHubInteractionBusy &&
                   !backgroundLoadsDraining &&
                   !recoveryRequiresReview;
        }

        private void StartCliInstall(ToolKind tool, string displayName)
        {
            bool backgroundLoadsDraining = AreBackgroundLoadsDraining;
            string recoveryWarning = GitOperationService.RecoveryWarning;
            if (!CanStartCliInstall(
                    IsGitHubInteractionBusy,
                    backgroundLoadsDraining,
                    !string.IsNullOrWhiteSpace(recoveryWarning)))
            {
                installStatus = backgroundLoadsDraining
                    ? "Wait for the current package scan to finish before starting an installer."
                    : !string.IsNullOrWhiteSpace(recoveryWarning)
                        ? recoveryWarning
                        : "Another command-line tool or repository operation is already running.";
                installStatusType = MessageType.Warning;
                Repaint();
                return;
            }

            CliInstallPlan plan = CliInstaller.GetInstallPlan(tool);
            if (!plan.CanRunAutomatically)
            {
                installStatus = plan.AutomaticInstallUnavailableReason;
                installStatusType = MessageType.Warning;
                return;
            }

            string prompt =
                $"Git Submodule Manager can run this command to install {displayName}:\n\n" +
                $"{plan.DisplayCommand}\n\n" +
                (string.IsNullOrWhiteSpace(plan.ExecutableLocationNotice)
                    ? string.Empty
                    : plan.ExecutableLocationNotice + "\n\n") +
                "This changes software installed on your computer. The command will only run if you choose Install.";
            if (plan.OpensSystemInstaller)
                prompt += " The operating system will show its own installer confirmation.";
            else
                prompt += " Your operating system may also request permission.";

            if (!EditorUtility.DisplayDialog($"Install {displayName}?", prompt, "Install", "Cancel"))
            {
                installStatus = $"{displayName} installation was cancelled.";
                installStatusType = MessageType.Info;
                return;
            }

            activeCliInstallTool = tool;
            activeCliInstallPlan = plan;
            cliInstallInProgress = true;
            installStatus = $"Installing {displayName}...";
            installStatusType = MessageType.Info;
            if (!GitOperationService.TryStartCommand(
                    $"Installing {displayName}...",
                    plan.FileName,
                    plan.Arguments,
                    15 * 60 * 1000,
                    false,
                    ResolveCliInstallOutcome,
                    (result, effectiveOutcome) =>
                        NotifyCliInstallComplete(tool, plan, result, effectiveOutcome),
                    out string startError,
                    new GitOperationMetadata { Phase = "install-command-line-tool" }))
            {
                cliInstallInProgress = false;
                activeCliInstallPlan = null;
                installStatus = startError;
                installStatusType = MessageType.Error;
            }
            Repaint();
        }

        private static GitOperationCompletionOutcome ResolveCliInstallOutcome(CommandResult result)
        {
            if (result != null && result.IsSuccess)
                return GitOperationCompletionOutcome.Succeeded;
            return result != null && result.TerminationConfirmed
                ? GitOperationCompletionOutcome.FailedButRolledBack
                : GitOperationCompletionOutcome.FailedUnsafe;
        }

        private void NotifyCliInstallComplete(
            ToolKind tool,
            CliInstallPlan plan,
            CommandResult result,
            GitOperationCompletionOutcome effectiveOutcome)
        {
            // These flags are process-wide and must be released even when the
            // window that started the installer was closed before completion.
            cliInstallInProgress = false;
            activeCliInstallPlan = null;
            if (this == null)
                return;

            string displayName = tool == ToolKind.Git ? "Git" : "GitHub CLI";

            if (effectiveOutcome == GitOperationCompletionOutcome.Succeeded &&
                result != null &&
                result.IsSuccess &&
                plan != null &&
                plan.OpensSystemInstaller)
            {
                installStatus = $"The {displayName} system installer was opened. Complete it, then click Check again.";
                installStatusType = MessageType.Info;
            }
            else if (effectiveOutcome == GitOperationCompletionOutcome.Succeeded &&
                     result != null &&
                     result.IsSuccess)
            {
                installStatus = $"The installer completed. Checking for {displayName}...";
                installStatusType = MessageType.Info;
                dependencyCheckIncludesGitHub |= tool == ToolKind.GitHubCli;
                BeginBackgroundLoad(true);
            }
            else
            {
                installStatus = BuildCliInstallFailureMessage(displayName, result);
                installStatusType = MessageType.Error;
            }

            Repaint();
        }

        internal static string BuildCliInstallFailureMessage(string displayName, CommandResult result)
        {
            if (result == null)
                return $"{displayName} installation failed because the installer returned no result. You can retry or use the official install guide.";

            string detail = string.IsNullOrWhiteSpace(result.StdErr) ? result.StdOut : result.StdErr;
            detail = GitUtility.RedactCredentials(detail).Trim();
            const int maxDetailLength = 1200;
            if (detail.Length > maxDetailLength)
                detail = detail.Substring(0, maxDetailLength) + "…";

            string exitDescription = result.ExitCode == 0 ? string.Empty : $" (exit code {result.ExitCode})";
            if (string.IsNullOrWhiteSpace(detail))
                detail = "The installer did not provide an error message.";

            return $"{displayName} installation failed{exitDescription}: {detail} You can retry or use the official install guide.";
        }

        private void RefreshCurrentTab()
        {
            repositoryCoordinator.ClearAllBranchCaches();
            switch (currentTab)
            {
                case Tab.Installed:
                    RefreshInstalled();
                    break;
                case Tab.Discover:
                    RefreshAvailable();
                    break;
            }
        }

        private void RefreshCurrentTabIfStale()
        {
            double now = EditorApplication.timeSinceStartup;

            switch (currentTab)
            {
                case Tab.Installed:
                    GitSubmoduleManagerUserSettings settings =
                        GitSubmoduleManagerUserSettings.Instance;
                    bool installedNeedsRefresh = installedPackages.Count == 0 ||
                        ShouldRefreshInstalledPackagesOnReturn(
                            settings.RefreshInProjectWhenRevisited,
                            now - lastInstalledRefreshTime,
                            settings.RefreshIntervalSeconds);
                    if (installedNeedsRefresh)
                        RefreshInstalled();
                    break;
                case Tab.Discover:
                    if (!discoveryCoordinator.HasResults && !discoveryCoordinator.IsLoading)
                        RefreshAvailable();
                    break;
            }
        }

        internal static bool ShouldRefreshInstalledPackagesOnReturn(
            bool refreshEnabled,
            double elapsedSeconds,
            double refreshIntervalSeconds)
        {
            return refreshEnabled &&
                elapsedSeconds > Math.Max(0.0, refreshIntervalSeconds);
        }

        private void RefreshInstalled()
        {
            if (!gitAvailable)
            {
                installedStatus = "Git is required to list packages.";
                installedStatusType = MessageType.Warning;
                return;
            }

            if (isInstalledLoading ||
                AreBackgroundLoadsDraining ||
                IsRepositoryOperationBusy ||
                !string.IsNullOrWhiteSpace(GitOperationService.RecoveryWarning))
                return;

            installedStatus = "Refreshing packages...";
            installedStatusType = MessageType.Info;
            isInstalledLoading = true;
            pendingInstalledLoadResult = null;
            int generation = Interlocked.Increment(ref installedLoadGeneration);
            long repositoryGeneration = GitOperationService.RepositoryGeneration;
            var cancellationSource = new CancellationTokenSource();
            var thread = new Thread(() =>
            {
                try
                {
                    var result = new InstalledLoadResult
                    {
                        LoadGeneration = generation,
                        RepositoryGeneration = repositoryGeneration
                    };
                    result.Success = GitUtility.TryGetSubmodules(
                        out List<GitPackageInfo> packages,
                        out string error,
                        cancellationSource.Token);
                    result.Packages = packages;
                    result.Error = error;
                    cancellationSource.Token.ThrowIfCancellationRequested();
                    if (generation == Volatile.Read(ref installedLoadGeneration))
                        pendingInstalledLoadResult = result;
                }
                catch (OperationCanceledException)
                {
                    // Lifecycle teardown intentionally cancels repository reads.
                }
                catch (Exception ex)
                {
                    if (generation == Volatile.Read(ref installedLoadGeneration) &&
                        !cancellationSource.IsCancellationRequested)
                    {
                        pendingInstalledLoadResult = new InstalledLoadResult
                        {
                            LoadGeneration = generation,
                            RepositoryGeneration = repositoryGeneration,
                            Success = false,
                            Error = BuildInitialLoadFailureMessage(
                                "The package refresh failed unexpectedly",
                                ex)
                        };
                    }
                }
                finally
                {
                    lock (cancellationSource)
                        cancellationSource.Dispose();
                    Interlocked.Decrement(ref activeBackgroundLoadWorkers);
                }
            })
            {
                IsBackground = true,
                Name = "Git Submodule Manager refresh"
            };

            installedLoadCancellationSource = cancellationSource;
            installedLoadThread = thread;
            Interlocked.Increment(ref activeBackgroundLoadWorkers);
            try
            {
                thread.Start();
            }
            catch (Exception ex)
            {
                Interlocked.Decrement(ref activeBackgroundLoadWorkers);
                lock (cancellationSource)
                    cancellationSource.Dispose();
                installedLoadCancellationSource = null;
                installedLoadThread = null;
                isInstalledLoading = false;
                installedStatus = BuildInitialLoadFailureMessage(
                    "The package refresh could not start",
                    ex);
                installedStatusType = MessageType.Error;
            }
        }

        private void UpdateInstalledRefresh()
        {
            if (!isInstalledLoading)
                return;

            InstalledLoadResult result = pendingInstalledLoadResult;
            if (result == null)
                return;

            pendingInstalledLoadResult = null;
            isInstalledLoading = false;
            if (!IsBackgroundLoadResultCurrent(
                    result.LoadGeneration,
                    Volatile.Read(ref installedLoadGeneration),
                    result.RepositoryGeneration,
                    GitOperationService.RepositoryGeneration))
            {
                backgroundLoadDeferred = true;
                return;
            }

            if (result.Success)
            {
                installedPackages = result.Packages ?? new List<GitPackageInfo>();
                installedStatus = string.Empty;
                installedStatusType = MessageType.None;
            }
            else
            {
                installedPackages = new List<GitPackageInfo>();
                installedStatus = result.Error;
                installedStatusType = MessageType.Error;
            }

            MarkInstalledRepos();
            selectedInstalledIndex = Mathf.Clamp(selectedInstalledIndex, -1, installedPackages.Count - 1);
            lastInstalledRefreshTime = EditorApplication.timeSinceStartup;
            lastRefreshDateTime = DateTime.Now;
            Repaint();
        }

        private void QueueInstalledRefreshAfterOperation()
        {
            EditorApplication.delayCall += () =>
            {
                if (CanEnterDeferredWindowAction(this, isWindowEnabled) &&
                    !IsRepositoryOperationBusy &&
                    string.IsNullOrWhiteSpace(GitOperationService.RecoveryWarning))
                    RefreshInstalled();
            };
        }

        private void RefreshAvailable()
        {
            discoverStatus = string.Empty;
            discoverStatusType = MessageType.None;

            if (!ghAvailable || !ghAuthenticated || IsGitHubInteractionBusy)
            {
                return;
            }

            discoveryCoordinator.EnsureUsername();
            discoveryCoordinator.ReloadCurrentPage();
        }

        private void UpdateDiscovery()
        {
            // Preserve completed handles without consuming or chaining more gh
            // work until the process-wide authentication lifecycle is settled.
            if (IsSharedGitHubAuthenticationBlocked)
                return;

            if (discoveryCoordinator.Tick(EditorApplication.timeSinceStartup))
            {
                if (discoveryCoordinator.PageChanged)
                {
                    MarkInstalledRepos();
                    SortRepos();
                }
                selectedRepoIndex = Mathf.Clamp(selectedRepoIndex, -1, discoveryCoordinator.DisplayedRepos.Count - 1);
                Repaint();
            }

            if (discoveryCoordinator.IsLoading)
            {
                RepaintProgress();
            }
        }

        private void FetchBranchesForUrl(string url)
        {
            repositoryCoordinator.RequestBranches(url);
        }

        private void UpdateBranchFetching()
        {
            if (repositoryCoordinator.TickBranchFetch())
                Repaint();
        }

        private void DrawBranchDropdown(string url, string currentBranch, Action<string> onBranchSelected)
        {
            bool hasCachedBranches = repositoryCoordinator.TryGetCachedBranches(url, out List<string> branches);
            bool isLoading = repositoryCoordinator.IsFetchingBranches(url) && !hasCachedBranches;
            bool hasError = repositoryCoordinator.TryGetBranchError(url, out string branchError);

            string buttonLabel = isLoading
                ? "Loading branches..."
                : string.IsNullOrWhiteSpace(currentBranch) ? "Select branch..." : currentBranch;
            string tooltip = isLoading
                ? "Fetching branches from remote..."
                : hasError ? $"{FirstLine(branchError)} Click to retry." : "Click to load remote branches.";

            using (new EditorGUI.DisabledScope(isLoading))
            {
                Rect dropdownRect = GUILayoutUtility.GetRect(new GUIContent(buttonLabel), EditorStyles.popup, GUILayout.Height(20));
                if (!EditorGUI.DropdownButton(dropdownRect, new GUIContent(buttonLabel, tooltip), FocusType.Passive, EditorStyles.popup))
                    return;

                if (hasCachedBranches)
                {
                    var menu = new GenericMenu();
                    foreach (string branch in branches)
                    {
                        string branchCapture = branch;
                        bool isActive = string.Equals(branch, currentBranch, StringComparison.OrdinalIgnoreCase);
                        menu.AddItem(new GUIContent(branch), isActive, () =>
                        {
                            onBranchSelected?.Invoke(branchCapture);
                            Repaint();
                        });
                    }
                    menu.DropDown(dropdownRect);
                    return;
                }

                repositoryCoordinator.ClearBranchCache(url);
                FetchBranchesForUrl(url);
            }

        }

        private void MarkInstalledRepos()
        {
            var installedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (installedPackages != null)
            {
                foreach (var package in installedPackages)
                {
                    if (GitHubUtility.TryParseGitHubRepo(package.Url, out string owner, out string repo))
                        installedIds.Add($"{owner}/{repo}");
                }
            }

            foreach (var repo in discoveryCoordinator.DisplayedRepos)
                repo.IsInstalled = installedIds.Contains($"{repo.Owner}/{repo.Name}");
        }

        private void InitializeRepoDefaults(GitHubRepo repo)
        {
            selectedRepoPackageName = GitHubUtility.DerivePackageNameSuggestion(repo.Owner, repo.Name);
            selectedRepoBranch = repo.DefaultBranch?.Trim() ?? string.Empty;
            addStatus = string.Empty;
        }

        private string ValidatePackageInput(string url, string packageName, string branch)
        {
            if (string.IsNullOrWhiteSpace(url))
                return "Git URL is required.";

            if (!GitUtility.IsValidRepositoryUrl(url))
                return "Use a secure HTTPS, SSH, or explicit local repository URL without embedded credentials. Plain HTTP and git:// transports are blocked.";

            if (!GitUtility.IsValidUpmPackageName(packageName))
                return PackageNameRule;

            if (!GitUtility.IsValidBranchName(branch))
                return "Branch name is invalid. Leave it empty to use the repository default.";

            string path = GetPackagePath(packageName);

            foreach (var package in installedPackages)
            {
                if (string.Equals(package.Path, path, StringComparison.Ordinal))
                    return "This package is already installed.";
            }

            string fullPath = Path.Combine(GitUtility.ProjectRoot, path);
            if (Directory.Exists(fullPath) || File.Exists(fullPath))
                return $"Package path already exists: {path}";

            return string.Empty;
        }

        private bool TryDerivePackageNameFromUrl(string url, out string packageName)
        {
            packageName = string.Empty;
            if (!GitHubUtility.TryParseGitHubRepo(url, out string owner, out string repo))
                return false;

            packageName = GitHubUtility.DerivePackageNameSuggestion(owner, repo);
            return !string.IsNullOrEmpty(packageName);
        }

        private void TryAddSubmodule(string url, string branch, string packageName)
        {
            addStatus = string.Empty;
            addStatusType = MessageType.None;
            operationStatus = string.Empty;
            operationStatusType = MessageType.None;

            if (IsRepositoryOperationBusy)
            {
                SetAddStatus("Another Git operation is already running.", MessageType.Warning);
                return;
            }

            string validationError = ValidatePackageInput(url, packageName, branch);
            if (!string.IsNullOrWhiteSpace(validationError))
            {
                SetAddStatus(validationError, MessageType.Error);
                return;
            }

            string path = GetPackagePath(packageName);

            string branchDescription = string.IsNullOrWhiteSpace(branch) ? "the repository default" : branch.Trim();
            if (!EditorUtility.DisplayDialog(
                    "Add Git Package?",
                    $"Repository:\n{GitUtility.RedactCredentials(url.Trim())}\n\n" +
                    $"Branch: {branchDescription}\nDestination: {path}\n\n" +
                    "Unity packages can contain Editor code that executes inside the Unity Editor. Only install repositories you trust.",
                    "Clone and Add",
                    "Cancel"))
                return;

            addStatus = $"Adding {packageName}...";
            addStatusType = MessageType.Info;
            var state = new AddTaskState();
            StartTaskOperation(
                $"Adding {packageName}...",
                cancellationToken => RunAddSubmoduleTask(
                    url,
                    branch,
                    packageName,
                    path,
                    state,
                    cancellationToken),
                _ => state.Outcome,
                (result, effectiveOutcome) =>
                    NotifyAddSubmoduleComplete(state, result, effectiveOutcome),
                true,
                new GitOperationMetadata { PackagePath = path, Phase = "add-validate-or-rollback" });
        }

        private static CommandResult RunAddSubmoduleTask(
            string url,
            string branch,
            string packageName,
            string path,
            AddTaskState state,
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
                string message = GitUtility.BuildCommandError("Failed to add submodule", addResult);
                if (addResult == null || !addResult.TerminationConfirmed)
                {
                    state.Outcome = GitOperationCompletionOutcome.FailedUnsafe;
                    state.Message =
                        message +
                        " Process-tree termination could not be confirmed, so automatic cleanup was skipped. Inspect the destination and running Git/SSH processes before acknowledging recovery.";
                    return addResult;
                }

                cancellationToken.ThrowIfCancellationRequested();
                bool cleanupSucceeded = GitUtility.TryCleanupFailedAdd(
                    plan,
                    out string cleanupWarning,
                    cancellationToken);
                if (!string.IsNullOrWhiteSpace(cleanupWarning))
                    message += cleanupSucceeded
                        ? $" Recovery details: {cleanupWarning}"
                        : $" Cleanup warning: {cleanupWarning}";
                state.Message = message;
                state.Outcome = cleanupSucceeded
                    ? GitOperationCompletionOutcome.FailedButRolledBack
                    : GitOperationCompletionOutcome.FailedUnsafe;
                return addResult;
            }

            string packageJsonPath = Path.Combine(GitUtility.ProjectRoot, path, "package.json");
            string validationError = string.Empty;
            if (!GitUtility.TryReadValidPackageManifest(
                    packageJsonPath,
                    out string declaredName,
                    out string manifestError,
                    cancellationToken))
                validationError = "Added submodule package.json is invalid: " + manifestError;
            else if (!string.Equals(declaredName, packageName, StringComparison.Ordinal))
                validationError = $"Package name mismatch. Expected {packageName}, got {declaredName}.";
            else if (!GitUtility.TryVerifyAddedSubmodule(
                         plan,
                         url,
                         branch,
                         out string postconditionError,
                         cancellationToken))
                validationError = "Git reported success, but add verification failed: " + postconditionError;

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
                        : validationError + " The Git registration was rolled back. " + cleanupNotice
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

        private void NotifyAddSubmoduleComplete(
            AddTaskState state,
            CommandResult result,
            GitOperationCompletionOutcome effectiveOutcome)
        {
            if (this == null)
                return;

            bool success = effectiveOutcome == GitOperationCompletionOutcome.Succeeded &&
                           result != null &&
                           result.IsSuccess &&
                           state != null &&
                           state.AddedSuccessfully &&
                           state.Outcome == GitOperationCompletionOutcome.Succeeded;
            SetAddStatus(
                success
                    ? state.Message
                    : BuildEffectiveCompletionError(
                        "Failed to add package",
                        state?.Message ?? "The add operation returned no final state.",
                        result,
                        effectiveOutcome),
                success ? MessageType.Info : MessageType.Error);
            if (result != null &&
                result.TerminationConfirmed &&
                effectiveOutcome != GitOperationCompletionOutcome.FailedUnsafe)
                QueueInstalledRefreshAfterOperation();

            if (success && activeAddPopup != null)
            {
                activeAddPopup.ClosePopup();
                activeAddPopup = null;
            }
        }

        private static CommandResult SafeTaskFailure(string error)
        {
            bool terminationUnconfirmed = GitUtility.ConsumeUnconfirmedCommandTermination();
            return new CommandResult
            {
                ExitCode = -1,
                StdOut = string.Empty,
                StdErr = error ?? "The Git operation failed before mutating the repository.",
                TerminationConfirmed = !terminationUnconfirmed
            };
        }

        private void SetAddStatus(string message, MessageType type)
        {
            addStatus = message;
            addStatusType = type;
            operationStatus = message;
            operationStatusType = type;
        }

        private static string GetPackagePath(string packageName)
        {
            return $"Packages/{packageName}";
        }
    }
}
