using System;
using System.IO;
using System.Text;
using System.Threading;
using UnityEditor;
using UnityEngine;

namespace MartinCalander.GitSubmoduleManager.Editor
{
    internal enum GitOperationCompletionOutcome
    {
        Succeeded,
        FailedButRolledBack,
        FailedUnsafe
    }

    internal sealed class GitOperationMetadata
    {
        internal string PackagePath = string.Empty;
        internal string Phase = string.Empty;
        internal string StartCommit = string.Empty;
        internal string PackageName = string.Empty;
        internal PackageManagerResolutionExpectation PackageResolutionExpectation =
            PackageManagerResolutionExpectation.None;
    }

    [Serializable]
    internal sealed class GitOperationJournal
    {
        public int schemaVersion = 2;
        public string operationId = string.Empty;
        public string label = string.Empty;
        public string packagePath = string.Empty;
        public string phase = string.Empty;
        public string startCommit = string.Empty;
        public string state = string.Empty;
        public string startedUtc = string.Empty;
        public string updatedUtc = string.Empty;
        public bool autoRefreshSuppressed;
    }

    /// <summary>
    /// Owns repository mutations independently of any EditorWindow instance.
    /// It prevents overlapping index operations, balances AssetDatabase refresh,
    /// blocks assembly reload while a command is live, and leaves a recovery
    /// marker when Unity exits unexpectedly.
    /// </summary>
    [InitializeOnLoad]
    internal static class GitOperationService
    {
        private const int LifecycleDrainTimeoutMs = 10000;
        private const int MaximumLabelLength = 256;
        private const int MaximumPathLength = 1024;
        private const int MaximumPhaseLength = 128;
        private const int MaximumCommitLength = 128;
        private const long MaximumJournalBytes = 64 * 1024;
        private const string AutoRefreshSessionKey =
            "MartinCalander.GitSubmoduleManager.RecoveryOwnsAutoRefresh";
        private const string LegacyAutoRefreshSessionKey =
            "MartinCalander.GitPackageManager.RecoveryOwnsAutoRefresh";

        private static readonly object Gate = new object();
        private static readonly string CurrentJournalPath = Path.Combine(
            GitUtility.ProjectRoot,
            "Library",
            "GitSubmoduleManager",
            "active-operation.json");
        private static readonly string LegacyJournalPath = Path.Combine(
            GitUtility.ProjectRoot,
            "Library",
            "GitPackageManager",
            "active-operation.json");

        private static string JournalPath => ResolveJournalPath(CurrentJournalPath, LegacyJournalPath);

        private static bool HasConflictingJournalFiles =>
            HaveConflictingJournalFiles(CurrentJournalPath, LegacyJournalPath);

        private static AsyncCommandHandle commandHandle;
        private static Thread taskThread;
        private static CancellationTokenSource taskCancellationSource;
        private static CommandResult taskResult;
        private static int taskComplete;
        private static Func<CommandResult, GitOperationCompletionOutcome> outcomeResolver;
        private static Action<CommandResult, GitOperationCompletionOutcome> completionNotification;
        private static GitOperationJournal activeJournal;
        private static string activeLabel = string.Empty;
        private static string recoveryWarning = string.Empty;
        private static bool recoveryOwnsAutoRefresh;
        private static bool recoveryRequiresEditorRestart;
        private static bool controlsAutoRefresh;
        private static bool reloadLocked;
        private static bool polling;
        private static bool reserved;
        private static bool finalizing;
        private static bool journalOwnedByReservation;
        private static string packageResolutionPackageName = string.Empty;
        private static PackageManagerResolutionExpectation packageResolutionExpectation =
            PackageManagerResolutionExpectation.None;
        private static long repositoryGeneration;

        internal static long RepositoryGeneration => Interlocked.Read(ref repositoryGeneration);

        internal static string ResolveJournalPath(string currentPath, string legacyPath)
        {
            if (File.Exists(currentPath))
                return currentPath;
            return File.Exists(legacyPath) ? legacyPath : currentPath;
        }

        internal static bool HaveConflictingJournalFiles(string currentPath, string legacyPath)
        {
            return File.Exists(currentPath) && File.Exists(legacyPath);
        }

        internal static bool ShouldBlockMutationForReaders(
            bool managerReadersDraining,
            bool packageManagerSnapshotReaderActive,
            bool installProbeReaderActive,
            bool commandDrainActive)
        {
            return managerReadersDraining ||
                   packageManagerSnapshotReaderActive ||
                   installProbeReaderActive ||
                   commandDrainActive;
        }

        internal static string RecoveryWarning
        {
            get
            {
                lock (Gate)
                    return recoveryWarning;
            }
        }

        internal static bool IsBusy
        {
            get
            {
                lock (Gate)
                    return reserved || commandHandle != null || taskThread != null;
            }
        }

        internal static string ActiveLabel
        {
            get
            {
                lock (Gate)
                    return activeLabel;
            }
        }

        internal static float Progress
        {
            get
            {
                lock (Gate)
                    return commandHandle != null ? commandHandle.Progress : IsBusy ? 0.1f : 0f;
            }
        }

        static GitOperationService()
        {
            LoadRecoveryWarning();
            AssemblyReloadEvents.beforeAssemblyReload += HandleBeforeAssemblyReload;
            EditorApplication.quitting += HandleEditorQuitting;
        }

        internal static bool TryStartCommand(
            string label,
            string fileName,
            string arguments,
            int timeoutMs,
            bool suppressAutoRefresh,
            Func<CommandResult, GitOperationCompletionOutcome> onComplete,
            out string error,
            GitOperationMetadata metadata = null)
        {
            return TryStartCommandCore(
                label,
                fileName,
                arguments,
                timeoutMs,
                suppressAutoRefresh,
                onComplete,
                null,
                out error,
                metadata);
        }

        internal static bool TryStartCommand(
            string label,
            string fileName,
            string arguments,
            int timeoutMs,
            bool suppressAutoRefresh,
            Func<CommandResult, GitOperationCompletionOutcome> resolveOutcome,
            Action<CommandResult> notifyComplete,
            out string error,
            GitOperationMetadata metadata = null)
        {
            Action<CommandResult, GitOperationCompletionOutcome> effectiveNotification =
                notifyComplete == null
                    ? null
                    : (result, _) => notifyComplete(result);
            return TryStartCommandCore(
                label,
                fileName,
                arguments,
                timeoutMs,
                suppressAutoRefresh,
                resolveOutcome,
                effectiveNotification,
                out error,
                metadata);
        }

        internal static bool TryStartCommand(
            string label,
            string fileName,
            string arguments,
            int timeoutMs,
            bool suppressAutoRefresh,
            Func<CommandResult, GitOperationCompletionOutcome> resolveOutcome,
            Action<CommandResult, GitOperationCompletionOutcome> notifyComplete,
            out string error,
            GitOperationMetadata metadata = null)
        {
            return TryStartCommandCore(
                label,
                fileName,
                arguments,
                timeoutMs,
                suppressAutoRefresh,
                resolveOutcome,
                notifyComplete,
                out error,
                metadata);
        }

        private static bool TryStartCommandCore(
            string label,
            string fileName,
            string arguments,
            int timeoutMs,
            bool suppressAutoRefresh,
            Func<CommandResult, GitOperationCompletionOutcome> resolveOutcome,
            Action<CommandResult, GitOperationCompletionOutcome> notifyComplete,
            out string error,
            GitOperationMetadata metadata)
        {
            error = string.Empty;
            if (!TryReserve(
                    label,
                    suppressAutoRefresh,
                    resolveOutcome,
                    notifyComplete,
                    metadata,
                    out error))
                return false;

            try
            {
                TryUpdateActiveJournalState("running");
                RegisterPolling();
                commandHandle = CliCommandRunner.RunAsync(fileName, arguments, GitUtility.ProjectRoot, timeoutMs);
                return true;
            }
            catch (Exception ex)
            {
                error = $"Failed to start the Git operation: {ex.Message}";
                FinalizeReservation(GitOperationCompletionOutcome.FailedButRolledBack);
                return false;
            }
        }

        internal static bool TryStartTask(
            string label,
            Func<CancellationToken, CommandResult> task,
            bool suppressAutoRefresh,
            Func<CommandResult, GitOperationCompletionOutcome> onComplete,
            out string error,
            GitOperationMetadata metadata = null)
        {
            return TryStartTaskCore(
                label,
                task,
                suppressAutoRefresh,
                onComplete,
                (Action<CommandResult, GitOperationCompletionOutcome>)null,
                out error,
                metadata);
        }

        internal static bool TryStartTask(
            string label,
            Func<CancellationToken, CommandResult> task,
            bool suppressAutoRefresh,
            Func<CommandResult, GitOperationCompletionOutcome> resolveOutcome,
            Action<CommandResult> notifyComplete,
            out string error,
            GitOperationMetadata metadata = null)
        {
            Action<CommandResult, GitOperationCompletionOutcome> effectiveNotification =
                notifyComplete == null
                    ? null
                    : (result, _) => notifyComplete(result);
            return TryStartTaskCore(
                label,
                task,
                suppressAutoRefresh,
                resolveOutcome,
                effectiveNotification,
                out error,
                metadata);
        }

        internal static bool TryStartTask(
            string label,
            Func<CancellationToken, CommandResult> task,
            bool suppressAutoRefresh,
            Func<CommandResult, GitOperationCompletionOutcome> resolveOutcome,
            Action<CommandResult, GitOperationCompletionOutcome> notifyComplete,
            out string error,
            GitOperationMetadata metadata = null)
        {
            return TryStartTaskCore(
                label,
                task,
                suppressAutoRefresh,
                resolveOutcome,
                notifyComplete,
                out error,
                metadata);
        }

        private static bool TryStartTaskCore(
            string label,
            Func<CancellationToken, CommandResult> task,
            bool suppressAutoRefresh,
            Func<CommandResult, GitOperationCompletionOutcome> resolveOutcome,
            Action<CommandResult, GitOperationCompletionOutcome> notifyComplete,
            out string error,
            GitOperationMetadata metadata)
        {
            error = string.Empty;
            if (task == null)
            {
                error = "The Git task was not provided.";
                return false;
            }

            if (!TryReserve(
                    label,
                    suppressAutoRefresh,
                    resolveOutcome,
                    notifyComplete,
                    metadata,
                    out error))
                return false;

            try
            {
                taskResult = null;
                Volatile.Write(ref taskComplete, 0);
                taskCancellationSource = new CancellationTokenSource();
                CancellationToken cancellationToken = taskCancellationSource.Token;
                taskThread = new Thread(() => RunTask(task, cancellationToken))
                {
                    IsBackground = true,
                    Name = "Git Submodule Manager operation"
                };
                TryUpdateActiveJournalState("running");
                RegisterPolling();
                taskThread.Start();
                return true;
            }
            catch (Exception ex)
            {
                error = $"Failed to start the Git task: {ex.Message}";
                FinalizeReservation(GitOperationCompletionOutcome.FailedButRolledBack);
                return false;
            }
        }

        internal static bool TryStartTask(
            string label,
            Func<CommandResult> task,
            bool suppressAutoRefresh,
            Func<CommandResult, GitOperationCompletionOutcome> onComplete,
            out string error,
            GitOperationMetadata metadata = null)
        {
            return TryStartTask(
                label,
                task,
                suppressAutoRefresh,
                onComplete,
                (Action<CommandResult, GitOperationCompletionOutcome>)null,
                out error,
                metadata);
        }

        internal static bool TryStartTask(
            string label,
            Func<CommandResult> task,
            bool suppressAutoRefresh,
            Func<CommandResult, GitOperationCompletionOutcome> resolveOutcome,
            Action<CommandResult> notifyComplete,
            out string error,
            GitOperationMetadata metadata = null)
        {
            if (task == null)
            {
                error = "The Git task was not provided.";
                return false;
            }

            return TryStartTask(
                label,
                cancellationToken =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return task();
                },
                suppressAutoRefresh,
                resolveOutcome,
                notifyComplete,
                out error,
                metadata);
        }

        internal static bool TryStartTask(
            string label,
            Func<CommandResult> task,
            bool suppressAutoRefresh,
            Func<CommandResult, GitOperationCompletionOutcome> resolveOutcome,
            Action<CommandResult, GitOperationCompletionOutcome> notifyComplete,
            out string error,
            GitOperationMetadata metadata = null)
        {
            if (task == null)
            {
                error = "The Git task was not provided.";
                return false;
            }

            return TryStartTask(
                label,
                cancellationToken =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return task();
                },
                suppressAutoRefresh,
                resolveOutcome,
                notifyComplete,
                out error,
                metadata);
        }

        internal static bool TryAcknowledgeRecoveryWarning(out string error)
        {
            lock (Gate)
            {
                if (reserved || commandHandle != null || taskThread != null)
                {
                    error = $"Another repository operation is already running: {activeLabel}";
                    return false;
                }
            }

            if (HasConflictingJournalFiles)
            {
                error =
                    "Both the current and legacy operation journals exist. Preserve and inspect both files, " +
                    "then remove only the obsolete marker before acknowledging recovery.";
                return false;
            }

            // The conflict warning deliberately does not pick either journal. Once the
            // user removes the obsolete copy, pin and reload the surviving journal so
            // auto-refresh ownership and cleanup refer to the same inspected file.
            string acknowledgementJournalPath = JournalPath;
            LoadRecoveryWarning(
                acknowledgementJournalPath,
                out bool journalExistedWhenInspected,
                out string inspectedOperationId);

            if (!IsAcknowledgementJournalStable(
                    acknowledgementJournalPath,
                    journalExistedWhenInspected,
                    out error))
            {
                return false;
            }

            if (journalExistedWhenInspected &&
                !IsValidJournalOperationId(inspectedOperationId))
            {
                error =
                    "The recovery journal has no valid operation identity and cannot be removed automatically. " +
                    "Preserve it for review, remove it manually only when safe, and restart the Unity Editor if " +
                    "the recovery warning reports uncertain auto-refresh ownership.";
                return false;
            }

            bool shouldRestoreAutoRefresh;
            lock (Gate)
            {
                if (reserved || commandHandle != null || taskThread != null)
                {
                    error = $"Another repository operation is already running: {activeLabel}";
                    return false;
                }

                if (string.IsNullOrWhiteSpace(recoveryWarning) && !journalExistedWhenInspected)
                {
                    error = string.Empty;
                    return true;
                }

                if (recoveryRequiresEditorRestart)
                {
                    error =
                        "Auto-refresh ownership cannot be proven because the recovery journal is missing. " +
                        "Restart the Unity Editor before starting another repository mutation.";
                    return false;
                }

                shouldRestoreAutoRefresh = recoveryOwnsAutoRefresh;
            }

            bool reloadLockAcquired = false;
            bool acknowledgementCompleted = false;
            bool unlockSucceeded = true;
            try
            {
                EditorApplication.LockReloadAssemblies();
                reloadLockAcquired = true;

                if (!IsAcknowledgementJournalStable(
                        acknowledgementJournalPath,
                        journalExistedWhenInspected,
                        out error))
                {
                    return false;
                }

                if (shouldRestoreAutoRefresh)
                {
                    AssetDatabase.AllowAutoRefresh();
                    lock (Gate)
                        recoveryOwnsAutoRefresh = false;
                    try
                    {
                        SetAutoRefreshSessionMarker(false);
                    }
                    catch (Exception ex)
                    {
                        Debug.LogWarning(
                            $"[Git Submodule Manager] Failed to clear the auto-refresh session marker: {ex.Message}");
                    }
                    TryUpdateRecoveryJournalAutoRefreshState(
                        acknowledgementJournalPath,
                        inspectedOperationId,
                        false);
                }

                // Unsafe package files are imported only after the user has
                // reviewed and explicitly acknowledged the recovery warning.
                AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate);

                if (!IsAcknowledgementJournalStable(
                        acknowledgementJournalPath,
                        journalExistedWhenInspected,
                        out error))
                {
                    return false;
                }

                acknowledgementCompleted = TryDeleteJournal(
                    acknowledgementJournalPath,
                    inspectedOperationId,
                    out error);
            }
            catch (Exception ex)
            {
                error = $"Recovery acknowledgement could not be completed: {ex.Message}";
            }
            finally
            {
                if (reloadLockAcquired)
                {
                    try
                    {
                        EditorApplication.UnlockReloadAssemblies();
                    }
                    catch (Exception ex)
                    {
                        unlockSucceeded = false;
                        error =
                            "Unity could not restore assembly reload after recovery review. " +
                            "Save your work and restart the Unity Editor before continuing: " + ex.Message;
                        var journal = CreateJournal(
                            "Recovery acknowledgement",
                            new GitOperationMetadata { Phase = "reload-unlock-failed" });
                        journal.state = "reload-unlock-failed";
                        journal.autoRefreshSuppressed = false;
                        try
                        {
                            if (!File.Exists(JournalPath))
                                WriteJournal(journal, false, string.Empty);
                        }
                        catch (Exception journalException)
                        {
                            error += " The recovery journal could not be recreated: " + journalException.Message;
                        }

                        lock (Gate)
                            recoveryRequiresEditorRestart = true;
                        SetRecoveryWarning(error);
                    }
                }
            }

            if (!acknowledgementCompleted || !unlockSucceeded)
                return false;

            lock (Gate)
            {
                recoveryOwnsAutoRefresh = false;
                recoveryRequiresEditorRestart = false;
            }
            SetRecoveryWarning(string.Empty);
            error = string.Empty;
            return true;
        }

        private static bool TryReserve(
            string label,
            bool suppressAutoRefresh,
            Func<CommandResult, GitOperationCompletionOutcome> resolveOutcome,
            Action<CommandResult, GitOperationCompletionOutcome> notifyComplete,
            GitOperationMetadata metadata,
            out string error)
        {
            if (PackageManagerProjectResolutionService.IsBusy)
            {
                error = PackageManagerProjectResolutionService.BuildUnavailableMessage();
                return false;
            }

            if (PackageManagerReadOnlyGitInstallService.IsBusy)
            {
                error =
                    "Wait for the current Unity Package Manager operation to finish.";
                return false;
            }

            if (ShouldBlockMutationForReaders(
                    false,
                    PackageManagerSubmoduleSnapshot.IsReaderActive,
                    GitSubmoduleInstallProbe.IsReaderActive,
                    AsyncCommandDrainRegistry.IsDraining))
            {
                error =
                    "A package scan is still running or stopping. Wait for it to finish before starting a repository mutation.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(RecoveryWarning) && File.Exists(JournalPath))
                LoadRecoveryWarning();

            lock (Gate)
            {
                if (reserved || commandHandle != null || taskThread != null)
                {
                    error = $"Another repository operation is already running: {activeLabel}";
                    return false;
                }

                if (!string.IsNullOrWhiteSpace(recoveryWarning) || File.Exists(JournalPath))
                {
                    error =
                        "A previous repository operation still needs review. Inspect the recovery warning and " +
                        "acknowledge it before starting another mutation.";
                    return false;
                }

                activeLabel = NormalizeJournalField(
                    string.IsNullOrWhiteSpace(label) ? "Running Git operation..." : label,
                    MaximumLabelLength);
                outcomeResolver = resolveOutcome;
                completionNotification = notifyComplete;
                activeJournal = CreateJournal(activeLabel, metadata);
                packageResolutionPackageName = metadata?.PackageName?.Trim() ??
                                               string.Empty;
                packageResolutionExpectation =
                    metadata?.PackageResolutionExpectation ??
                    PackageManagerResolutionExpectation.None;
                recoveryOwnsAutoRefresh = false;
                recoveryRequiresEditorRestart = false;
                journalOwnedByReservation = false;
                controlsAutoRefresh = false;
                reloadLocked = false;
                finalizing = false;
                reserved = true;
                Interlocked.Increment(ref repositoryGeneration);
            }

            try
            {
                WriteJournal(GetActiveJournalSnapshot(), false, string.Empty);
                lock (Gate)
                    journalOwnedByReservation = true;
                EditorApplication.LockReloadAssemblies();
                reloadLocked = true;
                if (suppressAutoRefresh)
                {
                    AssetDatabase.DisallowAutoRefresh();
                    controlsAutoRefresh = true;
                    SetAutoRefreshSessionMarker(true);
                    TryUpdateActiveJournalAutoRefreshState(true);
                }

                TryUpdateActiveJournalState("starting");
                error = string.Empty;
                return true;
            }
            catch (Exception ex)
            {
                error = $"The repository operation could not reserve Unity's reload state: {ex.Message}";
                FinalizeReservation(GitOperationCompletionOutcome.FailedButRolledBack);
                return false;
            }
        }

        private static void RunTask(
            Func<CancellationToken, CommandResult> task,
            CancellationToken cancellationToken)
        {
            try
            {
                GitUtility.ResetCommandSafetyState();
                taskResult = task(cancellationToken) ?? new CommandResult
                {
                    ExitCode = -1,
                    StdOut = string.Empty,
                    StdErr = "The Git task returned no result.",
                    TerminationConfirmed = false
                };
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                taskResult = new CommandResult
                {
                    ExitCode = -1,
                    StdOut = string.Empty,
                    StdErr = "The Git task was cancelled.",
                    Cancelled = true,
                    TerminationConfirmed = false
                };
            }
            catch (Exception ex)
            {
                taskResult = new CommandResult
                {
                    ExitCode = -1,
                    StdOut = string.Empty,
                    StdErr = ex.Message,
                    TerminationConfirmed = false
                };
            }
            finally
            {
                if (GitUtility.ConsumeUnconfirmedCommandTermination())
                {
                    if (taskResult == null)
                    {
                        taskResult = new CommandResult
                        {
                            ExitCode = -1,
                            StdOut = string.Empty,
                            StdErr = "A child Git process may still be running.",
                            TerminationConfirmed = false
                        };
                    }
                    else
                    {
                        taskResult.ExitCode = -1;
                        taskResult.TerminationConfirmed = false;
                        string suffix = "A child Git process may still be running; repository safety could not be confirmed.";
                        taskResult.StdErr = string.IsNullOrWhiteSpace(taskResult.StdErr)
                            ? suffix
                            : taskResult.StdErr.TrimEnd() + " " + suffix;
                    }
                }

                Volatile.Write(ref taskComplete, 1);
            }
        }

        private static void Tick()
        {
            CommandResult result;
            Func<CommandResult, GitOperationCompletionOutcome> resolver;
            Action<CommandResult, GitOperationCompletionOutcome> notification;
            AsyncCommandHandle completedCommand = null;
            Thread completedTask = null;

            lock (Gate)
            {
                if (finalizing)
                    return;

                if (commandHandle != null)
                {
                    if (!commandHandle.IsComplete)
                        return;
                    completedCommand = commandHandle;
                    result = commandHandle.Result;
                }
                else if (taskThread != null)
                {
                    if (Volatile.Read(ref taskComplete) == 0)
                        return;
                    completedTask = taskThread;
                    result = taskResult;
                }
                else
                {
                    UnregisterPolling();
                    return;
                }

                resolver = outcomeResolver;
                notification = completionNotification;
            }

            // Completion is published immediately before the worker returns. Do
            // not release repository ownership until the worker itself has ended.
            if (completedCommand != null && !completedCommand.WaitForCompletion(0))
                return;
            if (completedTask != null && completedTask.IsAlive)
                return;

            lock (Gate)
            {
                if (finalizing)
                    return;
                finalizing = true;
            }

            GitOperationCompletionOutcome resolvedOutcome = ResolveCompletionOutcome(result, resolver);

            // Repository safety and journal ownership are finalized before any
            // EditorWindow callback runs. A destroyed window, domain-specific
            // GUI state, or notification exception cannot change this decision.
            GitOperationCompletionOutcome effectiveOutcome = FinalizeReservation(
                resolvedOutcome,
                out string completionWarning);
            if (result != null && !string.IsNullOrWhiteSpace(completionWarning))
                result.CompletionWarning = completionWarning;
            NotifyCompletion(result, effectiveOutcome, notification);
        }

        internal static GitOperationCompletionOutcome ResolveCompletionOutcome(
            CommandResult result,
            Func<CommandResult, GitOperationCompletionOutcome> resolver)
        {
            GitOperationCompletionOutcome outcome;
            try
            {
                outcome = resolver?.Invoke(result) ??
                          (result != null && result.IsSuccess
                              ? GitOperationCompletionOutcome.Succeeded
                              : GitOperationCompletionOutcome.FailedUnsafe);
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
                outcome = GitOperationCompletionOutcome.FailedUnsafe;
            }

            // No resolver may classify a command as safe while its process tree
            // could still be mutating the repository.
            if (result == null || !result.TerminationConfirmed)
                return GitOperationCompletionOutcome.FailedUnsafe;

            return outcome;
        }

        internal static void NotifyCompletion(
            CommandResult result,
            Action<CommandResult> notification,
            Action<Exception> exceptionHandler = null)
        {
            if (notification == null)
                return;

            try
            {
                notification(result);
            }
            catch (Exception ex)
            {
                ReportNotificationException(ex, exceptionHandler);
            }
        }

        internal static void NotifyCompletion(
            CommandResult result,
            GitOperationCompletionOutcome effectiveOutcome,
            Action<CommandResult, GitOperationCompletionOutcome> notification,
            Action<Exception> exceptionHandler = null)
        {
            if (notification == null)
                return;

            try
            {
                CommandResult effectiveResult = BuildEffectiveCompletionResult(result, effectiveOutcome);
                notification(effectiveResult, effectiveOutcome);
            }
            catch (Exception ex)
            {
                ReportNotificationException(ex, exceptionHandler);
            }
        }

        private static void ReportNotificationException(
            Exception exception,
            Action<Exception> exceptionHandler)
        {
            if (exceptionHandler != null)
            {
                exceptionHandler(exception);
                return;
            }

            Debug.LogException(exception);
        }

        internal static GitOperationCompletionOutcome ApplyFinalizationSafety(
            GitOperationCompletionOutcome resolvedOutcome,
            bool finalizationSafe)
        {
            return finalizationSafe
                ? resolvedOutcome
                : GitOperationCompletionOutcome.FailedUnsafe;
        }

        internal static CommandResult BuildEffectiveCompletionResult(
            CommandResult result,
            GitOperationCompletionOutcome effectiveOutcome)
        {
            if (effectiveOutcome != GitOperationCompletionOutcome.FailedUnsafe)
                return result;

            string recovery = RecoveryWarning;
            if (string.IsNullOrWhiteSpace(recovery))
            {
                recovery =
                    "Inspect .gitmodules, the parent repository index, and the affected package before " +
                    "acknowledging the recovery warning or starting another repository operation.";
            }

            string existingError = result?.StdErr?.Trim() ?? string.Empty;
            string finalizationError =
                "The repository operation could not be finalized safely. " + recovery.Trim();
            if (!string.IsNullOrWhiteSpace(existingError) &&
                existingError.IndexOf(finalizationError, StringComparison.Ordinal) < 0)
            {
                finalizationError = existingError + " " + finalizationError;
            }

            return new CommandResult
            {
                ExitCode = -1,
                StdOut = result?.StdOut ?? string.Empty,
                StdErr = finalizationError,
                ResolvedExecutablePath = result?.ResolvedExecutablePath,
                TimedOut = result?.TimedOut ?? false,
                Cancelled = result?.Cancelled ?? false,
                TerminationConfirmed = result?.TerminationConfirmed ?? false,
                StdOutTruncated = result?.StdOutTruncated ?? false,
                StdErrTruncated = result?.StdErrTruncated ?? false,
                CompletionWarning = result?.CompletionWarning ?? string.Empty
            };
        }

        private static GitOperationCompletionOutcome FinalizeReservation(
            GitOperationCompletionOutcome outcome)
        {
            return FinalizeReservation(outcome, out _);
        }

        private static GitOperationCompletionOutcome FinalizeReservation(
            GitOperationCompletionOutcome outcome,
            out string completionWarning)
        {
            completionWarning = string.Empty;
            bool stateIsSafe =
                outcome == GitOperationCompletionOutcome.Succeeded ||
                outcome == GitOperationCompletionOutcome.FailedButRolledBack;
            bool shouldRefreshAssets;
            bool journalDeleted = false;
            bool packageResolutionPrepared = false;
            string packageResolutionOperationId;
            string packageNameToResolve;
            PackageManagerResolutionExpectation resolutionExpectation;
            CancellationTokenSource cancellationSourceToDispose;

            lock (Gate)
            {
                if (!reserved)
                    return outcome;
                finalizing = true;
                shouldRefreshAssets = controlsAutoRefresh && stateIsSafe;
                cancellationSourceToDispose = taskCancellationSource;
                packageResolutionOperationId = activeJournal?.operationId ??
                                               string.Empty;
                packageNameToResolve = packageResolutionPackageName;
                resolutionExpectation = packageResolutionExpectation;
            }

            try
            {
                if (stateIsSafe)
                {
                    TryUpdateActiveJournalState(
                        outcome == GitOperationCompletionOutcome.Succeeded
                            ? "finalizing-success"
                            : "finalizing-rolled-back");
                }
                else
                {
                    TryUpdateActiveJournalState("failed-unsafe");
                }

                if (controlsAutoRefresh && stateIsSafe)
                {
                    try
                    {
                        AssetDatabase.AllowAutoRefresh();
                        controlsAutoRefresh = false;
                        lock (Gate)
                            recoveryOwnsAutoRefresh = false;
                        TrySetAutoRefreshSessionMarker(false);
                        TryUpdateActiveJournalAutoRefreshState(false);
                    }
                    catch (Exception ex)
                    {
                        stateIsSafe = false;
                        shouldRefreshAssets = false;
                        Debug.LogWarning(
                            $"[Git Submodule Manager] Failed to restore AssetDatabase auto-refresh: {ex.Message}");
                    }
                }

                if (shouldRefreshAssets && stateIsSafe)
                {
                    try
                    {
                        AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate);
                    }
                    catch (Exception ex)
                    {
                        stateIsSafe = false;
                        Debug.LogWarning($"[Git Submodule Manager] Final AssetDatabase refresh failed: {ex.Message}");
                    }
                }

                if (stateIsSafe)
                {
                    journalDeleted = TryDeleteActiveJournal(out string deleteError);
                    if (!journalDeleted)
                    {
                        stateIsSafe = false;
                        Debug.LogWarning($"[Git Submodule Manager] {deleteError}");
                    }
                }

                if (!stateIsSafe)
                {
                    if (controlsAutoRefresh)
                    {
                        lock (Gate)
                            recoveryOwnsAutoRefresh = true;
                        TrySetAutoRefreshSessionMarker(true);
                        TryUpdateActiveJournalAutoRefreshState(true);
                    }

                    TryUpdateActiveJournalState("failed-unsafe");
                    LoadRecoveryWarning();
                    if (string.IsNullOrWhiteSpace(RecoveryWarning))
                    {
                        SetRecoveryWarning(
                            "A repository operation did not finish safely. Inspect .gitmodules, the parent index, " +
                            "and the affected package before acknowledging this warning.");
                    }
                }
                else if (journalDeleted)
                {
                    lock (Gate)
                    {
                        recoveryOwnsAutoRefresh = false;
                        recoveryRequiresEditorRestart = false;
                    }
                    SetRecoveryWarning(string.Empty);
                }
            }
            finally
            {
                // Each cleanup stage is independent, and the reload lock remains
                // held through journal handling and the final refresh. Unlock it
                // only after all other Unity-facing finalization has completed.
                try
                {
                    UnregisterPolling();
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[Git Submodule Manager] Failed to unregister operation polling: {ex.Message}");
                }

                try
                {
                    cancellationSourceToDispose?.Dispose();
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[Git Submodule Manager] Failed to dispose task cancellation state: {ex.Message}");
                }

                if (stateIsSafe &&
                    outcome == GitOperationCompletionOutcome.Succeeded &&
                    resolutionExpectation !=
                    PackageManagerResolutionExpectation.None)
                {
                    try
                    {
                        packageResolutionPrepared =
                            PackageManagerProjectResolutionService.TryPrepare(
                                packageResolutionOperationId,
                                packageNameToResolve,
                                resolutionExpectation,
                                out string resolutionError);
                        if (!packageResolutionPrepared)
                        {
                            completionWarning =
                                "The Git submodule was changed successfully, but " +
                                "Unity package resolution could not be prepared: " +
                                resolutionError;
                            Debug.LogWarning(
                                "[Git Submodule Manager] " + completionWarning);
                        }
                    }
                    catch (Exception ex)
                    {
                        completionWarning =
                            "The Git submodule was changed successfully, but " +
                            "Unity package resolution could not be prepared: " +
                            GitHubUtility.SanitizeUiDiagnostic(
                                GitUtility.RedactCredentials(ex.Message));
                        Debug.LogWarning(
                            "[Git Submodule Manager] " + completionWarning);
                    }
                }

                try
                {
                    if (reloadLocked)
                        EditorApplication.UnlockReloadAssemblies();
                }
                catch (Exception ex)
                {
                    stateIsSafe = false;
                    lock (Gate)
                        recoveryRequiresEditorRestart = true;

                    string recoveryMessage =
                        "Unity could not restore assembly reload after a repository operation. " +
                        "Save your work and restart the Unity Editor before continuing. " +
                        ex.Message;
                    try
                    {
                        GitOperationJournal snapshot = GetActiveJournalSnapshot() ??
                                                       CreateJournal(
                                                           "Repository operation recovery",
                                                           new GitOperationMetadata
                                                           {
                                                               Phase = "reload-unlock-failed"
                                                           });
                        snapshot.state = "reload-unlock-failed";
                        snapshot.updatedUtc = DateTime.UtcNow.ToString("O");
                        WriteJournal(snapshot, false, string.Empty);
                    }
                    catch (Exception journalException)
                    {
                        recoveryMessage +=
                            " The recovery journal could not be recreated: " + journalException.Message;
                    }

                    SetRecoveryWarning(recoveryMessage);
                }
                finally
                {
                    if (!stateIsSafe && packageResolutionPrepared)
                    {
                        try
                        {
                            PackageManagerProjectResolutionService.CancelPrepared(
                                packageResolutionOperationId);
                        }
                        catch (Exception ex)
                        {
                            Debug.LogWarning(
                                "[Git Submodule Manager] Failed to cancel a " +
                                "prepared package resolve after reload unlock " +
                                "failed: " + ex.Message);
                        }
                    }

                    reloadLocked = false;
                    ClearOwnership();
                }
            }

            return ApplyFinalizationSafety(outcome, stateIsSafe);
        }

        private static void ClearOwnership()
        {
            lock (Gate)
            {
                commandHandle = null;
                taskThread = null;
                taskCancellationSource = null;
                taskResult = null;
                Volatile.Write(ref taskComplete, 0);
                outcomeResolver = null;
                completionNotification = null;
                activeJournal = null;
                activeLabel = string.Empty;
                journalOwnedByReservation = false;
                packageResolutionPackageName = string.Empty;
                packageResolutionExpectation =
                    PackageManagerResolutionExpectation.None;
                controlsAutoRefresh = false;
                reserved = false;
                finalizing = false;
            }
        }

        private static void RegisterPolling()
        {
            if (polling)
                return;
            EditorApplication.update += Tick;
            polling = true;
        }

        private static void UnregisterPolling()
        {
            if (!polling)
                return;
            EditorApplication.update -= Tick;
            polling = false;
        }

        private static void HandleBeforeAssemblyReload()
        {
            CancelAndDrainForLifecycle("assembly reload");
        }

        private static void HandleEditorQuitting()
        {
            CancelAndDrainForLifecycle("Editor shutdown");
        }

        private static void CancelAndDrainForLifecycle(string lifecycleEvent)
        {
            AsyncCommandHandle activeCommand;
            Thread activeTask;
            CancellationTokenSource activeTaskCancellation;

            lock (Gate)
            {
                // UnlockReloadAssemblies may synchronously make a queued reload
                // eligible. Finalization already owns teardown; never recurse
                // into cancellation/finalization for the same reservation.
                if (finalizing)
                    return;

                if (!reserved && commandHandle == null && taskThread == null)
                    return;

                activeCommand = commandHandle;
                activeTask = taskThread;
                activeTaskCancellation = taskCancellationSource;
            }

            TryUpdateActiveJournalState("cancelling-for-" + lifecycleEvent.Replace(' ', '-'));

            try
            {
                activeCommand?.Cancel();
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Git Submodule Manager] Failed to request command cancellation: {ex.Message}");
            }

            try
            {
                activeTaskCancellation?.Cancel();
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Git Submodule Manager] Failed to request task cancellation: {ex.Message}");
            }

            bool commandTerminated = activeCommand == null ||
                                     activeCommand.WaitForCompletion(LifecycleDrainTimeoutMs);
            bool taskTerminated = activeTask == null ||
                                  !activeTask.IsAlive ||
                                  activeTask.Join(LifecycleDrainTimeoutMs);

            if (!commandTerminated || !taskTerminated)
            {
                TryUpdateActiveJournalState("cancellation-not-drained");
                SetRecoveryWarning(
                    $"Unity requested {lifecycleEvent}, but the active repository worker did not stop within " +
                    $"{LifecycleDrainTimeoutMs}ms. Ownership and the recovery journal were intentionally retained. " +
                    "Inspect running Git processes and the repository before continuing.");
                return;
            }

            // The completion callback is intentionally skipped during lifecycle
            // teardown. Even a successful process may still require its callback's
            // verification or rollback, so the conservative result is unsafe.
            FinalizeReservation(GitOperationCompletionOutcome.FailedUnsafe);
        }

        private static GitOperationJournal CreateJournal(string label, GitOperationMetadata metadata)
        {
            string timestamp = DateTime.UtcNow.ToString("O");
            return new GitOperationJournal
            {
                operationId = Guid.NewGuid().ToString("N"),
                label = NormalizeJournalField(label, MaximumLabelLength),
                packagePath = NormalizeJournalField(metadata?.PackagePath, MaximumPathLength),
                phase = NormalizeJournalField(
                    string.IsNullOrWhiteSpace(metadata?.Phase) ? "repository-mutation" : metadata.Phase,
                    MaximumPhaseLength),
                startCommit = NormalizeJournalField(metadata?.StartCommit, MaximumCommitLength),
                state = "reserved",
                startedUtc = timestamp,
                updatedUtc = timestamp,
                autoRefreshSuppressed = false
            };
        }

        private static GitOperationJournal GetActiveJournalSnapshot()
        {
            lock (Gate)
                return CloneJournal(activeJournal);
        }

        private static GitOperationJournal CloneJournal(GitOperationJournal source)
        {
            if (source == null)
                return null;

            return new GitOperationJournal
            {
                schemaVersion = source.schemaVersion,
                operationId = source.operationId,
                label = source.label,
                packagePath = source.packagePath,
                phase = source.phase,
                startCommit = source.startCommit,
                state = source.state,
                startedUtc = source.startedUtc,
                updatedUtc = source.updatedUtc,
                autoRefreshSuppressed = source.autoRefreshSuppressed
            };
        }

        private static void TryUpdateActiveJournalState(string state)
        {
            GitOperationJournal snapshot;
            lock (Gate)
            {
                if (activeJournal == null || !journalOwnedByReservation)
                    return;

                activeJournal.state = NormalizeJournalField(state, MaximumPhaseLength);
                activeJournal.updatedUtc = DateTime.UtcNow.ToString("O");
                snapshot = CloneJournal(activeJournal);
            }

            TryWriteJournalUpdate(snapshot, true);
        }

        private static void TryUpdateActiveJournalAutoRefreshState(bool isSuppressed)
        {
            GitOperationJournal snapshot;
            lock (Gate)
            {
                if (activeJournal == null || !journalOwnedByReservation)
                    return;

                activeJournal.autoRefreshSuppressed = isSuppressed;
                activeJournal.updatedUtc = DateTime.UtcNow.ToString("O");
                snapshot = CloneJournal(activeJournal);
            }

            TryWriteJournalUpdate(snapshot, true);
        }

        private static void TryUpdateRecoveryJournalAutoRefreshState(
            string journalPath,
            string expectedOperationId,
            bool isSuppressed)
        {
            try
            {
                if (!TryReadJournal(journalPath, out GitOperationJournal journal, out _))
                    return;

                if (!string.IsNullOrWhiteSpace(expectedOperationId) &&
                    !string.Equals(journal.operationId, expectedOperationId, StringComparison.Ordinal))
                {
                    Debug.LogWarning(
                        "[Git Submodule Manager] The recovery journal changed ownership and was not updated.");
                    return;
                }

                journal.autoRefreshSuppressed = isSuppressed;
                journal.updatedUtc = DateTime.UtcNow.ToString("O");
                WriteJournal(journalPath, journal, true, expectedOperationId);
            }
            catch (Exception ex)
            {
                Debug.LogWarning(
                    $"[Git Submodule Manager] Failed to update operation journal: {ex.Message}");
            }
        }

        private static void TryWriteJournalUpdate(
            GitOperationJournal journal,
            bool requireActiveOwnership)
        {
            try
            {
                if (requireActiveOwnership)
                {
                    lock (Gate)
                    {
                        if (!journalOwnedByReservation ||
                            activeJournal == null ||
                            !string.Equals(
                                activeJournal.operationId,
                                journal?.operationId,
                                StringComparison.Ordinal))
                        {
                            return;
                        }
                    }
                }

                WriteJournal(
                    journal,
                    true,
                    requireActiveOwnership ? journal.operationId : string.Empty);
            }
            catch (Exception ex)
            {
                // The original atomic journal remains intact when replacement
                // fails. The operation may continue, but recovery stays cautious.
                Debug.LogWarning($"[Git Submodule Manager] Failed to update operation journal: {ex.Message}");
            }
        }

        private static void WriteJournal(
            GitOperationJournal journal,
            bool replaceExisting,
            string expectedOperationId)
        {
            WriteJournal(JournalPath, journal, replaceExisting, expectedOperationId);
        }

        private static void WriteJournal(
            string journalPath,
            GitOperationJournal journal,
            bool replaceExisting,
            string expectedOperationId)
        {
            if (journal == null)
                throw new InvalidOperationException("The operation journal was not initialized.");

            if (!GitUtility.TryValidateProjectOwnedPath(journalPath, out string pathError))
                throw new IOException(pathError);

            string directory = Path.GetDirectoryName(journalPath);
            if (string.IsNullOrWhiteSpace(directory))
                throw new InvalidOperationException("The operation journal directory could not be resolved.");

            Directory.CreateDirectory(directory);
            string temporaryPath = Path.Combine(
                directory,
                Path.GetFileName(journalPath) + "." + Guid.NewGuid().ToString("N") + ".tmp");

            try
            {
                string json = JsonUtility.ToJson(journal, true);
                using (var stream = new FileStream(
                           temporaryPath,
                           FileMode.CreateNew,
                           FileAccess.Write,
                           FileShare.None))
                using (var writer = new StreamWriter(stream, new UTF8Encoding(false, true)))
                {
                    writer.Write(json);
                    writer.Flush();
                    stream.Flush(true);
                }

                if (replaceExisting)
                {
                    if (!File.Exists(journalPath))
                        throw new IOException("The operation journal disappeared before its atomic update.");

                    if (!string.IsNullOrWhiteSpace(expectedOperationId))
                    {
                        if (!TryReadJournal(
                                journalPath,
                                out GitOperationJournal existingJournal,
                                out string readError) ||
                            !string.Equals(
                                existingJournal.operationId,
                                expectedOperationId,
                                StringComparison.Ordinal))
                        {
                            throw new IOException(
                                "The operation journal is no longer owned by this reservation. " + readError);
                        }
                    }

                    File.Replace(temporaryPath, journalPath, null);
                }
                else
                {
                    // File.Move is an atomic create within this directory and
                    // fails rather than overwriting a recovery marker that raced
                    // with reservation.
                    File.Move(temporaryPath, journalPath);
                }
            }
            finally
            {
                try
                {
                    if (File.Exists(temporaryPath))
                        File.Delete(temporaryPath);
                }
                catch
                {
                    // A same-directory temporary journal is harmless and must not
                    // mask the original write/replace error.
                }
            }
        }

        private static void LoadRecoveryWarning()
        {
            if (HasConflictingJournalFiles)
            {
                lock (Gate)
                    recoveryRequiresEditorRestart = false;
                SetRecoveryWarning(
                    "Both the current and legacy operation journals exist. Repository mutations are blocked " +
                    "until both files have been preserved, inspected, and the obsolete marker is removed manually.");
                return;
            }

            LoadRecoveryWarning(JournalPath, out _, out _);
        }

        private static void LoadRecoveryWarning(
            string journalPath,
            out bool journalExists,
            out string operationId)
        {
            journalExists = File.Exists(journalPath);
            operationId = string.Empty;

            if (!GitUtility.TryValidateProjectOwnedPath(journalPath, out string pathError))
            {
                lock (Gate)
                    recoveryRequiresEditorRestart = true;
                SetRecoveryWarning(pathError);
                return;
            }

            if (!journalExists)
            {
                bool markerWasRead = TryGetAutoRefreshSessionMarker(out bool ownsOrphanedSuppression);
                lock (Gate)
                {
                    recoveryOwnsAutoRefresh = false;
                    recoveryRequiresEditorRestart = !markerWasRead || ownsOrphanedSuppression;
                }

                if (!markerWasRead)
                {
                    SetRecoveryWarning(
                        "The auto-refresh recovery marker could not be read. Restart the Unity Editor before " +
                        "starting another repository mutation.");
                }
                else if (ownsOrphanedSuppression)
                {
                    SetRecoveryWarning(
                        "AssetDatabase auto-refresh has an orphaned operation marker, but its recovery journal is " +
                        "missing. Restart the Unity Editor before starting another repository mutation.");
                }
                else
                {
                    SetRecoveryWarning(string.Empty);
                }
                return;
            }

            if (TryReadJournal(journalPath, out GitOperationJournal journal, out _))
            {
                operationId = journal?.operationId ?? string.Empty;
                string label = string.IsNullOrWhiteSpace(journal?.label)
                    ? "a repository operation"
                    : NormalizeJournalField(journal.label, MaximumLabelLength);
                string package = string.IsNullOrWhiteSpace(journal?.packagePath)
                    ? string.Empty
                    : $" for {NormalizeJournalField(journal.packagePath, MaximumPathLength)}";
                string phase = string.IsNullOrWhiteSpace(journal?.phase)
                    ? string.Empty
                    : $" during phase '{NormalizeJournalField(journal.phase, MaximumPhaseLength)}'";
                // SessionState survives a domain reload but not an Editor restart,
                // so it proves this service still owns a native suppression count.
                bool markerWasRead = TryGetAutoRefreshSessionMarker(out bool sessionMarker);
                bool ownsSuppression;
                bool requiresRestart;
                lock (Gate)
                {
                    if (markerWasRead)
                    {
                        ResolveRecoveryAutoRefreshState(
                            recoveryOwnsAutoRefresh,
                            journal != null && journal.autoRefreshSuppressed,
                            sessionMarker,
                            out ownsSuppression,
                            out requiresRestart);
                    }
                    else
                    {
                        ownsSuppression = false;
                        requiresRestart = true;
                    }
                    recoveryOwnsAutoRefresh = ownsSuppression;
                    recoveryRequiresEditorRestart = requiresRestart;
                }
                string refreshWarning = !markerWasRead
                    ? " Auto-refresh ownership could not be read; restart the Unity Editor before continuing."
                    : ownsSuppression
                    ? " AssetDatabase auto-refresh remains paused until you acknowledge this warning."
                    : requiresRestart
                        ? " Auto-refresh ownership is inconsistent; restart the Unity Editor before continuing."
                    : string.Empty;
                SetRecoveryWarning(
                    $"Unity previously stopped or became unsafe during {label}{package}{phase}. " +
                    "No automatic destructive cleanup was performed. Inspect .gitmodules, the parent index, " +
                    $"and the affected package before acknowledging this warning.{refreshWarning}");
            }
            else
            {
                bool markerWasRead = TryGetAutoRefreshSessionMarker(out bool sessionMarker);
                lock (Gate)
                {
                    recoveryOwnsAutoRefresh = false;
                    recoveryRequiresEditorRestart = !markerWasRead || sessionMarker;
                }
                SetRecoveryWarning(
                    !markerWasRead
                        ? "Unity previously stopped during a repository operation, and the auto-refresh recovery " +
                          "marker could not be read. Restart the Unity Editor before continuing."
                        : sessionMarker
                        ? "Unity previously stopped during a repository operation, but auto-refresh ownership cannot " +
                          "be proven from the damaged journal. Restart the Unity Editor before continuing."
                        : "Unity previously stopped during a repository operation. Inspect the parent repository " +
                          "before acknowledging this warning.");
            }
        }

        private static bool IsAcknowledgementJournalStable(
            string journalPath,
            bool expectedToExist,
            out string error)
        {
            if (HasConflictingJournalFiles)
            {
                error =
                    "Both the current and legacy operation journals exist. Preserve and inspect both files, " +
                    "then remove only the obsolete marker before acknowledging recovery.";
                return false;
            }

            if (!string.Equals(JournalPath, journalPath, StringComparison.Ordinal) ||
                File.Exists(journalPath) != expectedToExist)
            {
                error =
                    "The recovery journal changed while it was being acknowledged. Its files were preserved; " +
                    "review the recovery warning again before retrying.";
                return false;
            }

            error = string.Empty;
            return true;
        }

        internal static void ResolveRecoveryAutoRefreshState(
            bool alreadyOwnsSuppression,
            bool journalRecordsSuppression,
            bool sessionMarkerExists,
            out bool ownsSuppression,
            out bool requiresRestart)
        {
            ownsSuppression = alreadyOwnsSuppression ||
                              (journalRecordsSuppression && sessionMarkerExists);
            requiresRestart = sessionMarkerExists && !ownsSuppression;
        }

        internal static bool IsValidJournalOperationId(string operationId)
        {
            return Guid.TryParseExact(operationId, "N", out _);
        }

        private static bool TryGetAutoRefreshSessionMarker(out bool markerExists)
        {
            try
            {
                markerExists = SessionState.GetBool(AutoRefreshSessionKey, false) ||
                               SessionState.GetBool(LegacyAutoRefreshSessionKey, false);
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning(
                    $"[Git Submodule Manager] Failed to read the auto-refresh session marker: {ex.Message}");
                markerExists = false;
                return false;
            }
        }

        private static void TrySetAutoRefreshSessionMarker(bool value)
        {
            try
            {
                SetAutoRefreshSessionMarker(value);
            }
            catch (Exception ex)
            {
                Debug.LogWarning(
                    $"[Git Submodule Manager] Failed to update the auto-refresh session marker: {ex.Message}");
            }
        }

        private static void SetAutoRefreshSessionMarker(bool value)
        {
            SessionState.SetBool(AutoRefreshSessionKey, value);
            if (!value)
                SessionState.SetBool(LegacyAutoRefreshSessionKey, false);
        }

        private static bool TryReadJournal(out GitOperationJournal journal, out string error)
        {
            return TryReadJournal(JournalPath, out journal, out error);
        }

        private static bool TryReadJournal(
            string journalPath,
            out GitOperationJournal journal,
            out string error)
        {
            journal = null;
            try
            {
                if (!GitUtility.TryValidateProjectOwnedPath(journalPath, out error))
                    return false;

                if (!File.Exists(journalPath))
                {
                    error = "The operation journal does not exist.";
                    return false;
                }

                var journalFile = new FileInfo(journalPath);
                if (journalFile.Length > MaximumJournalBytes)
                    throw new InvalidDataException("The operation journal exceeds the safety size limit.");

                journal = JsonUtility.FromJson<GitOperationJournal>(File.ReadAllText(journalPath));
                if (journal == null)
                    throw new InvalidDataException("The operation journal is empty or invalid.");
                if (!IsValidJournalOperationId(journal.operationId))
                {
                    throw new InvalidDataException(
                        "The operation journal has no valid operation identity.");
                }

                error = string.Empty;
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                journal = null;
                return false;
            }
        }

        private static bool TryDeleteJournal(out string error)
        {
            return TryDeleteJournal(JournalPath, string.Empty, out error);
        }

        private static bool TryDeleteJournal(
            string journalPath,
            string expectedOperationId,
            out string error)
        {
            try
            {
                if (!GitUtility.TryValidateProjectOwnedPath(journalPath, out error))
                    return false;

                if (File.Exists(journalPath))
                {
                    if (!string.IsNullOrWhiteSpace(expectedOperationId))
                    {
                        if (!TryReadJournal(
                                journalPath,
                                out GitOperationJournal journal,
                                out string readError) ||
                            !string.Equals(
                                journal.operationId,
                                expectedOperationId,
                                StringComparison.Ordinal))
                        {
                            error =
                                "The recovery journal changed ownership and was preserved for review. " +
                                readError;
                            return false;
                        }
                    }

                    File.Delete(journalPath);
                }
                error = string.Empty;
                return true;
            }
            catch (Exception ex)
            {
                error = $"Failed to remove operation journal: {ex.Message}";
                return false;
            }
        }

        private static bool TryDeleteActiveJournal(out string error)
        {
            GitOperationJournal expectedJournal;
            lock (Gate)
            {
                if (!journalOwnedByReservation || activeJournal == null)
                {
                    error = "The active operation does not own the recovery journal; it was preserved for review.";
                    return false;
                }

                expectedJournal = CloneJournal(activeJournal);
            }

            string journalPath = JournalPath;
            if (!TryReadJournal(
                    journalPath,
                    out GitOperationJournal currentJournal,
                    out string readError))
            {
                error = "The active operation journal could not be verified before deletion: " + readError;
                return false;
            }

            if (!string.Equals(
                    currentJournal.operationId,
                    expectedJournal.operationId,
                    StringComparison.Ordinal))
            {
                error = "The operation journal changed ownership and was preserved for review.";
                return false;
            }

            return TryDeleteJournal(journalPath, expectedJournal.operationId, out error);
        }

        private static void SetRecoveryWarning(string warning)
        {
            string normalized = warning ?? string.Empty;
            bool changed;
            lock (Gate)
            {
                changed = !string.Equals(recoveryWarning, normalized, StringComparison.Ordinal);
                recoveryWarning = normalized;
            }

            // An unsafe operation can outlive or close its originating window.
            // Keep a persistent Console-visible signal in addition to the UI.
            if (changed && !string.IsNullOrWhiteSpace(normalized))
                Debug.LogError("[Git Submodule Manager] " + normalized);
        }

        private static string NormalizeJournalField(string value, int maximumLength)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;

            string normalized = value.Trim()
                .Replace('\r', ' ')
                .Replace('\n', ' ')
                .Replace('\0', ' ');
            return normalized.Length <= maximumLength
                ? normalized
                : normalized.Substring(0, maximumLength);
        }
    }
}
