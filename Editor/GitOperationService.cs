using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
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
        internal string[] PackageNames = Array.Empty<string>();
        // Unknown operations are treated conservatively as mutations.
        internal bool MayChangeRepository = true;
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

        private static readonly UTF8Encoding StrictUtf8Encoding =
            new UTF8Encoding(false, true);

        private sealed class JournalFileSnapshot
        {
            internal byte[] Contents = Array.Empty<byte>();
            internal GitOperationJournal Journal;
        }

        [DllImport("libc", EntryPoint = "open", SetLastError = true)]
        private static extern int OpenUnixFileNoFollow(
            string path,
            int flags);

        [DllImport("libc", EntryPoint = "read", SetLastError = true)]
        private static extern IntPtr ReadUnixFile(
            int descriptor,
            byte[] buffer,
            UIntPtr count);

        [DllImport("libc", EntryPoint = "lseek", SetLastError = true)]
        private static extern long SeekUnixFile(
            int descriptor,
            long offset,
            int origin);

        [DllImport("libc", EntryPoint = "close", SetLastError = true)]
        private static extern int CloseUnixFile(int descriptor);

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

        private static Thread taskThread;
        private static CancellationTokenSource taskCancellationSource;
        private static CommandResult taskResult;
        private static int taskComplete;
        private static Func<CommandResult, GitOperationCompletionOutcome> outcomeResolver;
        private static Action<CommandResult, GitOperationCompletionOutcome>
            beforeReloadUnlockNotification;
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
        private static string[] packageResolutionPackageNames = Array.Empty<string>();
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

        internal static bool ShouldAdvanceRepositoryGeneration(
            GitOperationMetadata metadata)
        {
            return metadata == null || metadata.MayChangeRepository;
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
                    return reserved || taskThread != null;
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

        internal static string ActivePackageName
        {
            get
            {
                lock (Gate)
                    return reserved
                        ? packageResolutionPackageName
                        : string.Empty;
            }
        }

        static GitOperationService()
        {
            LoadRecoveryWarning();
            AssemblyReloadEvents.beforeAssemblyReload += HandleBeforeAssemblyReload;
            EditorApplication.quitting += HandleEditorQuitting;
        }

        internal static bool TryStartTask(
            string label,
            Func<CancellationToken, CommandResult> task,
            bool suppressAutoRefresh,
            Func<CommandResult, GitOperationCompletionOutcome> resolveOutcome,
            Action<CommandResult, GitOperationCompletionOutcome> notifyComplete,
            out string error,
            GitOperationMetadata metadata = null,
            Action<CommandResult, GitOperationCompletionOutcome>
                notifyBeforeReloadUnlock = null)
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
                    notifyBeforeReloadUnlock,
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

        internal static bool TryAcknowledgeRecoveryWarning(out string error)
        {
            lock (Gate)
            {
                if (reserved || taskThread != null)
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
                if (reserved || taskThread != null)
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
            Action<CommandResult, GitOperationCompletionOutcome>
                notifyBeforeReloadUnlock,
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

            if (PackageManagerNativeRemoveHandoffService.IsBusy)
            {
                error = PackageManagerNativeRemoveHandoffService
                    .BuildUnavailableMessage();
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
                if (reserved || taskThread != null)
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
                beforeReloadUnlockNotification = notifyBeforeReloadUnlock;
                completionNotification = notifyComplete;
                activeJournal = CreateJournal(activeLabel, metadata);
                packageResolutionPackageName = metadata?.PackageName?.Trim() ??
                                               string.Empty;
                packageResolutionPackageNames = CopyPackageResolutionNames(
                    metadata);
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
                if (ShouldAdvanceRepositoryGeneration(metadata))
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
            Action<CommandResult, GitOperationCompletionOutcome>
                beforeReloadUnlock;
            Thread completedTask;

            lock (Gate)
            {
                if (finalizing)
                    return;

                if (taskThread != null)
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
                beforeReloadUnlock = beforeReloadUnlockNotification;
                notification = completionNotification;
            }

            // Completion is published immediately before the worker returns. Do
            // not release repository ownership until the worker itself has ended.
            if (completedTask.IsAlive)
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
                result,
                beforeReloadUnlock,
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

        internal static void GetAutoRefreshFinalizationPlan(
            bool ownsSuppression,
            GitOperationCompletionOutcome outcome,
            out bool shouldRestoreAutoRefresh,
            out bool shouldRefreshAssets)
        {
            shouldRestoreAutoRefresh = ownsSuppression;
            shouldRefreshAssets = ownsSuppression &&
                                  (outcome == GitOperationCompletionOutcome.Succeeded ||
                                   outcome == GitOperationCompletionOutcome.FailedButRolledBack);
        }

        internal static bool TryFinalizeAutoRefreshSuppression(
            Action allowAutoRefresh,
            Action<bool> setSessionMarker,
            Action<bool> updateJournal,
            out bool autoRefreshRestored,
            out string error)
        {
            if (allowAutoRefresh == null)
                throw new ArgumentNullException(nameof(allowAutoRefresh));
            if (setSessionMarker == null)
                throw new ArgumentNullException(nameof(setSessionMarker));
            if (updateJournal == null)
                throw new ArgumentNullException(nameof(updateJournal));

            error = string.Empty;
            try
            {
                // This delegate is intentionally invoked exactly once. Retrying
                // an unknown native suppression count could over-balance Unity.
                allowAutoRefresh();
                autoRefreshRestored = true;
            }
            catch (Exception exception)
            {
                autoRefreshRestored = false;
                error = exception.Message;
            }

            bool suppressionMayRemain = !autoRefreshRestored;
            try
            {
                setSessionMarker(suppressionMayRemain);
            }
            catch (Exception exception)
            {
                error = string.IsNullOrWhiteSpace(error)
                    ? exception.Message
                    : error + " " + exception.Message;
            }

            try
            {
                updateJournal(suppressionMayRemain);
            }
            catch (Exception exception)
            {
                error = string.IsNullOrWhiteSpace(error)
                    ? exception.Message
                    : error + " " + exception.Message;
            }

            return autoRefreshRestored && string.IsNullOrWhiteSpace(error);
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
            return FinalizeReservation(outcome, null, null, out _);
        }

        private static GitOperationCompletionOutcome FinalizeReservation(
            GitOperationCompletionOutcome outcome,
            CommandResult result,
            Action<CommandResult, GitOperationCompletionOutcome>
                notifyBeforeReloadUnlock,
            out string completionWarning)
        {
            completionWarning = string.Empty;
            bool stateIsSafe =
                outcome == GitOperationCompletionOutcome.Succeeded ||
                outcome == GitOperationCompletionOutcome.FailedButRolledBack;
            bool shouldRestoreAutoRefresh;
            bool shouldRefreshAssets;
            bool journalDeleted = false;
            bool packageResolutionPrepared = false;
            string packageResolutionOperationId;
            string packageNameToResolve;
            string[] packageNamesToResolve;
            PackageManagerResolutionExpectation resolutionExpectation;
            CancellationTokenSource cancellationSourceToDispose;

            lock (Gate)
            {
                if (!reserved)
                    return outcome;
                finalizing = true;
                GetAutoRefreshFinalizationPlan(
                    controlsAutoRefresh,
                    outcome,
                    out shouldRestoreAutoRefresh,
                    out shouldRefreshAssets);
                cancellationSourceToDispose = taskCancellationSource;
                packageResolutionOperationId = activeJournal?.operationId ??
                                               string.Empty;
                packageNameToResolve = packageResolutionPackageName;
                packageNamesToResolve = packageResolutionPackageNames;
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

            }
            finally
            {
                // Each cleanup stage is independent, and the reload lock remains
                // held through journal handling and the final refresh. Unlock it
                // only after all other Unity-facing finalization has completed.
                if (shouldRestoreAutoRefresh)
                {
                    bool finalizationSafe =
                        TryFinalizeAutoRefreshSuppression(
                            AssetDatabase.AllowAutoRefresh,
                            SetAutoRefreshSessionMarker,
                            UpdateActiveJournalAutoRefreshState,
                            out bool autoRefreshRestored,
                            out string restoreError);
                    if (autoRefreshRestored)
                    {
                        controlsAutoRefresh = false;
                        lock (Gate)
                            recoveryOwnsAutoRefresh = false;
                    }
                    else
                    {
                        lock (Gate)
                        {
                            recoveryOwnsAutoRefresh = true;
                            recoveryRequiresEditorRestart = true;
                        }
                    }

                    if (!finalizationSafe)
                    {
                        stateIsSafe = false;
                        shouldRefreshAssets = false;
                        Debug.LogWarning(
                            "[Git Submodule Manager] Failed to restore AssetDatabase auto-refresh safely: " +
                            restoreError);
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
                        string resolutionError;
                        packageResolutionPrepared =
                            packageNamesToResolve.Length > 0
                                ? PackageManagerProjectResolutionService.TryPrepare(
                                    packageResolutionOperationId,
                                    packageNamesToResolve,
                                    resolutionExpectation,
                                    out resolutionError)
                                : PackageManagerProjectResolutionService.TryPrepare(
                                    packageResolutionOperationId,
                                    packageNameToResolve,
                                    resolutionExpectation,
                                    out resolutionError);
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

                // The Git task, final AssetDatabase refresh, journal cleanup, and
                // package-resolution handoff are now known to be safe. Run any
                // coordinated Unity-owned follow-up before unlocking assembly
                // reload, because that unlock can replace this managed domain.
                NotifyCompletion(
                    result,
                    ApplyFinalizationSafety(outcome, stateIsSafe),
                    notifyBeforeReloadUnlock);

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
                taskThread = null;
                taskCancellationSource = null;
                taskResult = null;
                Volatile.Write(ref taskComplete, 0);
                outcomeResolver = null;
                beforeReloadUnlockNotification = null;
                completionNotification = null;
                activeJournal = null;
                activeLabel = string.Empty;
                journalOwnedByReservation = false;
                packageResolutionPackageName = string.Empty;
                packageResolutionPackageNames = Array.Empty<string>();
                packageResolutionExpectation =
                    PackageManagerResolutionExpectation.None;
                controlsAutoRefresh = false;
                reserved = false;
                finalizing = false;
            }
        }

        private static string[] CopyPackageResolutionNames(
            GitOperationMetadata metadata)
        {
            if (metadata?.PackageNames != null && metadata.PackageNames.Length > 0)
            {
                var copies = new string[metadata.PackageNames.Length];
                for (int index = 0; index < copies.Length; index++)
                    copies[index] = metadata.PackageNames[index]?.Trim() ?? string.Empty;
                return copies;
            }

            string packageName = metadata?.PackageName?.Trim() ?? string.Empty;
            return string.IsNullOrEmpty(packageName)
                ? Array.Empty<string>()
                : new[] { packageName };
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
            Thread activeTask;
            CancellationTokenSource activeTaskCancellation;

            lock (Gate)
            {
                // UnlockReloadAssemblies may synchronously make a queued reload
                // eligible. Finalization already owns teardown; never recurse
                // into cancellation/finalization for the same reservation.
                if (finalizing)
                    return;

                if (!reserved && taskThread == null)
                    return;

                activeTask = taskThread;
                activeTaskCancellation = taskCancellationSource;
            }

            TryUpdateActiveJournalState("cancelling-for-" + lifecycleEvent.Replace(' ', '-'));

            try
            {
                activeTaskCancellation?.Cancel();
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Git Submodule Manager] Failed to request task cancellation: {ex.Message}");
            }

            bool taskTerminated = activeTask == null ||
                                  !activeTask.IsAlive ||
                                  activeTask.Join(LifecycleDrainTimeoutMs);

            if (!taskTerminated)
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
            try
            {
                UpdateActiveJournalAutoRefreshState(isSuppressed);
            }
            catch (Exception ex)
            {
                Debug.LogWarning(
                    $"[Git Submodule Manager] Failed to update operation journal: {ex.Message}");
            }
        }

        private static void UpdateActiveJournalAutoRefreshState(bool isSuppressed)
        {
            GitOperationJournal snapshot;
            lock (Gate)
            {
                if (activeJournal == null || !journalOwnedByReservation)
                {
                    throw new InvalidOperationException(
                        "The active operation journal is no longer owned by this reservation.");
                }

                activeJournal.autoRefreshSuppressed = isSuppressed;
                activeJournal.updatedUtc = DateTime.UtcNow.ToString("O");
                snapshot = CloneJournal(activeJournal);
            }

            lock (Gate)
            {
                if (!journalOwnedByReservation ||
                    activeJournal == null ||
                    !string.Equals(
                        activeJournal.operationId,
                        snapshot.operationId,
                        StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        "The active operation journal changed ownership before its update.");
                }
            }

            WriteJournal(snapshot, true, snapshot.operationId);
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
            string expectedOperationId,
            Action beforeReplaceForTests = null)
        {
            if (journal == null)
                throw new InvalidOperationException("The operation journal was not initialized.");

            if (!GitUtility.TryValidateProjectOwnedPath(journalPath, out string pathError))
                throw new IOException(pathError);

            string directory = Path.GetDirectoryName(journalPath);
            if (string.IsNullOrWhiteSpace(directory))
                throw new InvalidOperationException("The operation journal directory could not be resolved.");

            Directory.CreateDirectory(directory);
            if (!GitUtility.TryValidateProjectOwnedPath(journalPath, out pathError))
                throw new IOException(pathError);

            string temporaryPath = Path.Combine(
                directory,
                Path.GetFileName(journalPath) + "." + Guid.NewGuid().ToString("N") + ".tmp");
            string recoveryPath = string.Empty;

            try
            {
                string json = JsonUtility.ToJson(journal, true);
                byte[] desiredContents = StrictUtf8Encoding.GetBytes(json);
                if (desiredContents.Length == 0 ||
                    desiredContents.LongLength > MaximumJournalBytes)
                {
                    throw new InvalidDataException(
                        "The operation journal exceeds the safety size limit.");
                }

                if (!GitUtility.TryValidateProjectOwnedPath(temporaryPath, out pathError))
                    throw new IOException(pathError);
                using (var stream = new FileStream(
                           temporaryPath,
                           FileMode.CreateNew,
                           FileAccess.Write,
                           FileShare.None))
                {
                    stream.Write(desiredContents, 0, desiredContents.Length);
                    stream.Flush(true);
                }

                if (replaceExisting)
                {
                    if (!IsValidJournalOperationId(expectedOperationId))
                    {
                        throw new IOException(
                            "An exact operation identity is required to replace the recovery journal.");
                    }

                    if (!TryReadJournalSnapshot(
                            journalPath,
                            out JournalFileSnapshot expectedSnapshot,
                            out string readError) ||
                        !string.Equals(
                            expectedSnapshot.Journal.operationId,
                            expectedOperationId,
                            StringComparison.Ordinal))
                    {
                        throw new IOException(
                            "The operation journal is no longer owned by this reservation. " + readError);
                    }

                    beforeReplaceForTests?.Invoke();

                    recoveryPath = BuildJournalRecoveryPath(journalPath, "replaced");
                    if (!GitUtility.TryValidateProjectOwnedPath(recoveryPath, out pathError))
                        throw new IOException(pathError);

                    // File.Replace is the atomic publication boundary. Its backup
                    // captures the exact inode displaced at that boundary, so a
                    // writer that races the precondition is preserved instead of
                    // being silently overwritten.
                    File.Replace(temporaryPath, journalPath, recoveryPath);
                    temporaryPath = string.Empty;

                    if (!TryReadJournalSnapshot(
                            recoveryPath,
                            out JournalFileSnapshot displacedSnapshot,
                            out string displacedError) ||
                        !JournalSnapshotsEqual(expectedSnapshot, displacedSnapshot))
                    {
                        throw new IOException(
                            "The operation journal changed at its atomic replacement boundary. " +
                            "The displaced data was preserved at " + recoveryPath + ". " +
                            displacedError);
                    }

                    if (!TryReadJournalSnapshot(
                            journalPath,
                            out JournalFileSnapshot publishedSnapshot,
                            out string publishedError) ||
                        !ByteArraysEqual(desiredContents, publishedSnapshot.Contents) ||
                        !string.Equals(
                            publishedSnapshot.Journal.operationId,
                            journal.operationId,
                            StringComparison.Ordinal))
                    {
                        throw new IOException(
                            "The replacement recovery journal could not be verified exactly. " +
                            "The prior journal was preserved at " + recoveryPath + ". " +
                            publishedError);
                    }

                    // Keep the exact displaced snapshot in Library. A writer
                    // may still hold the moved inode after our verification;
                    // unlinking it here could discard bytes written at the last
                    // instant. Retention is recovery-safe and bounded per file.
                    recoveryPath = string.Empty;
                }
                else
                {
                    // File.Move is an atomic create within this directory and
                    // fails rather than overwriting a recovery marker that raced
                    // with reservation.
                    File.Move(temporaryPath, journalPath);
                    temporaryPath = string.Empty;
                    if (!TryReadJournalSnapshot(
                            journalPath,
                            out JournalFileSnapshot createdSnapshot,
                            out string createdError) ||
                        !ByteArraysEqual(desiredContents, createdSnapshot.Contents))
                    {
                        throw new IOException(
                            "The newly created operation journal could not be verified exactly. " +
                            createdError);
                    }
                }
            }
            finally
            {
                // File.Move/File.Replace consumes the generated temporary file
                // on success. On failure, retain it: even a unique operation path
                // could have been replaced or written through an already-open
                // handle, so a best-effort unlink would reopen a data-loss race.
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

        private static void SetAutoRefreshSessionMarker(bool value)
        {
            SessionState.SetBool(AutoRefreshSessionKey, value);
            if (!value)
                SessionState.SetBool(LegacyAutoRefreshSessionKey, false);
        }

        private static bool TryReadJournal(
            string journalPath,
            out GitOperationJournal journal,
            out string error)
        {
            if (TryReadJournalSnapshot(
                    journalPath,
                    out JournalFileSnapshot snapshot,
                    out error))
            {
                journal = snapshot.Journal;
                return true;
            }

            journal = null;
            return false;
        }

        private static bool TryReadJournalSnapshot(
            string journalPath,
            out JournalFileSnapshot snapshot,
            out string error)
        {
            snapshot = null;
            try
            {
                if (!GitUtility.TryValidateProjectOwnedPath(journalPath, out error))
                    return false;

                if (!TryInspectJournalEntry(
                        journalPath,
                        out bool exists,
                        out FileAttributes initialAttributes,
                        out error))
                {
                    return false;
                }

                if (!exists)
                {
                    error = "The operation journal does not exist.";
                    return false;
                }

                if ((initialAttributes &
                     (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                {
                    error =
                        "The operation journal must be one regular, non-symbolic-link file.";
                    return false;
                }

                var initialFile = new FileInfo(journalPath);
                initialFile.Refresh();
                if (!initialFile.Exists)
                {
                    error = "The operation journal disappeared before it could be read.";
                    return false;
                }

                long initialLength = initialFile.Length;
                long initialLastWriteTicks = initialFile.LastWriteTimeUtc.Ticks;
                long initialCreationTicks = initialFile.CreationTimeUtc.Ticks;
                if (initialLength > MaximumJournalBytes)
                {
                    error = "The operation journal exceeds the safety size limit.";
                    return false;
                }

                var buffer = new byte[(int)MaximumJournalBytes + 1];
                if (!TryReadRegularJournalBytes(
                        journalPath,
                        buffer,
                        out int count,
                        out error))
                {
                    return false;
                }

                if (count > MaximumJournalBytes)
                {
                    error =
                        "The operation journal grew beyond the safety size limit while it was being read.";
                    return false;
                }

                if (!GitUtility.TryValidateProjectOwnedPath(journalPath, out error))
                    return false;
                if (!TryInspectJournalEntry(
                        journalPath,
                        out exists,
                        out FileAttributes finalAttributes,
                        out error) ||
                    !exists ||
                    (finalAttributes &
                     (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                {
                    if (string.IsNullOrWhiteSpace(error))
                    {
                        error =
                            "The operation journal changed type while it was being read.";
                    }
                    return false;
                }

                var finalFile = new FileInfo(journalPath);
                finalFile.Refresh();
                if (!finalFile.Exists ||
                    finalFile.Length != count ||
                    finalFile.Length != initialLength ||
                    finalFile.LastWriteTimeUtc.Ticks != initialLastWriteTicks ||
                    finalFile.CreationTimeUtc.Ticks != initialCreationTicks)
                {
                    error =
                        "The operation journal changed identity or length while it was being read.";
                    return false;
                }

                var contents = new byte[count];
                Buffer.BlockCopy(buffer, 0, contents, 0, count);
                string json;
                try
                {
                    json = StrictUtf8Encoding.GetString(contents);
                }
                catch (DecoderFallbackException exception)
                {
                    error =
                        "The operation journal must contain valid UTF-8 text: " +
                        exception.Message;
                    return false;
                }

                GitOperationJournal journal =
                    JsonUtility.FromJson<GitOperationJournal>(json);
                if (journal == null)
                    throw new InvalidDataException("The operation journal is empty or invalid.");
                if (!IsValidJournalOperationId(journal.operationId))
                {
                    throw new InvalidDataException(
                        "The operation journal has no valid operation identity.");
                }

                snapshot = new JournalFileSnapshot
                {
                    Contents = contents,
                    Journal = journal
                };
                error = string.Empty;
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                snapshot = null;
                return false;
            }
        }

        private static bool TryReadRegularJournalBytes(
            string journalPath,
            byte[] buffer,
            out int count,
            out string error)
        {
            count = 0;
            error = string.Empty;
            try
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    using (var stream = new FileStream(
                               journalPath,
                               FileMode.Open,
                               FileAccess.Read,
                               FileShare.Read))
                    {
                        if (!stream.CanSeek || stream.Length > MaximumJournalBytes)
                        {
                            error = !stream.CanSeek
                                ? "The operation journal must be one seekable regular file."
                                : "The operation journal exceeds the safety size limit.";
                            return false;
                        }

                        long openedLength = stream.Length;
                        while (count < buffer.Length)
                        {
                            int read = stream.Read(
                                buffer,
                                count,
                                buffer.Length - count);
                            if (read <= 0)
                                break;
                            count += read;
                        }

                        if (openedLength != count || stream.Length != count)
                        {
                            error =
                                "The operation journal changed length while it was being read.";
                            return false;
                        }
                    }
                    return true;
                }

                int flags;
                if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                {
                    const int darwinNonBlock = 0x0004;
                    const int darwinNoFollow = 0x0100;
                    const int darwinCloseOnExec = 0x01000000;
                    flags = darwinNonBlock | darwinNoFollow | darwinCloseOnExec;
                }
                else
                {
                    const int linuxNonBlock = 0x00000800;
                    const int linuxNoFollow = 0x00020000;
                    const int linuxCloseOnExec = 0x00080000;
                    flags = linuxNonBlock | linuxNoFollow | linuxCloseOnExec;
                }

                int descriptor = OpenUnixFileNoFollow(journalPath, flags);
                if (descriptor < 0)
                {
                    throw new IOException(
                        "The operation journal could not be opened without following links: " +
                        new Win32Exception(Marshal.GetLastWin32Error()).Message);
                }

                try
                {
                    const int seekCurrent = 1;
                    const int seekEnd = 2;
                    if (SeekUnixFile(descriptor, 0, seekCurrent) < 0)
                    {
                        throw new IOException(
                            "The operation journal must be one seekable regular file: " +
                            new Win32Exception(Marshal.GetLastWin32Error()).Message);
                    }

                    var chunk = new byte[8192];
                    while (count < buffer.Length)
                    {
                        int requested = Math.Min(
                            chunk.Length,
                            buffer.Length - count);
                        long read;
                        do
                        {
                            read = ReadUnixFile(
                                    descriptor,
                                    chunk,
                                    new UIntPtr((uint)requested))
                                .ToInt64();
                        }
                        while (read < 0 && Marshal.GetLastWin32Error() == 4);

                        if (read < 0)
                        {
                            throw new IOException(
                                "The operation journal could not be read safely: " +
                                new Win32Exception(Marshal.GetLastWin32Error()).Message);
                        }
                        if (read == 0)
                            break;

                        Buffer.BlockCopy(
                            chunk,
                            0,
                            buffer,
                            count,
                            (int)read);
                        count += (int)read;
                    }

                    long openedLength = SeekUnixFile(descriptor, 0, seekEnd);
                    if (openedLength < 0)
                    {
                        throw new IOException(
                            "The operation journal must be one seekable regular file: " +
                            new Win32Exception(Marshal.GetLastWin32Error()).Message);
                    }
                    if (openedLength != count)
                    {
                        error =
                            "The operation journal changed length while it was being read.";
                        return false;
                    }

                    return true;
                }
                finally
                {
                    CloseUnixFile(descriptor);
                }
            }
            catch (Exception exception)
            {
                error =
                    "The operation journal is not a safely readable regular file: " +
                    exception.Message;
                return false;
            }
        }

        private static bool TryInspectJournalEntry(
            string journalPath,
            out bool exists,
            out FileAttributes attributes,
            out string error)
        {
            exists = false;
            attributes = 0;
            error = string.Empty;
            try
            {
                attributes = File.GetAttributes(journalPath);
                exists = true;
                return true;
            }
            catch (FileNotFoundException)
            {
                return true;
            }
            catch (DirectoryNotFoundException)
            {
                return true;
            }
            catch (Exception exception)
            {
                error =
                    "The operation journal filesystem entry could not be inspected: " +
                    exception.Message;
                return false;
            }
        }

        private static bool JournalSnapshotsEqual(
            JournalFileSnapshot expected,
            JournalFileSnapshot actual)
        {
            return expected != null &&
                   actual != null &&
                   ByteArraysEqual(expected.Contents, actual.Contents);
        }

        private static bool ByteArraysEqual(byte[] first, byte[] second)
        {
            if (ReferenceEquals(first, second))
                return true;
            if (first == null || second == null || first.Length != second.Length)
                return false;

            for (int index = 0; index < first.Length; index++)
            {
                if (first[index] != second[index])
                    return false;
            }

            return true;
        }

        private static string BuildJournalRecoveryPath(
            string journalPath,
            string reason)
        {
            string directory = Path.GetDirectoryName(journalPath) ?? string.Empty;
            return Path.Combine(
                directory,
                Path.GetFileName(journalPath) + "." +
                DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff") + "-" +
                Guid.NewGuid().ToString("N") + "." + reason + ".recovery");
        }

        private static bool TryRestoreQuarantinedJournal(
            string recoveryPath,
            string journalPath,
            out string notice)
        {
            notice = string.Empty;
            try
            {
                if (!TryInspectJournalEntry(
                        journalPath,
                        out bool journalExists,
                        out _,
                        out string inspectError))
                {
                    notice = inspectError +
                             " The raced journal remains preserved at " +
                             recoveryPath + ".";
                    return false;
                }

                if (journalExists)
                {
                    notice =
                        "A new journal already occupies the canonical path. " +
                        "The raced journal remains preserved at " + recoveryPath + ".";
                    return false;
                }

                File.Move(recoveryPath, journalPath);
                notice =
                    "The raced journal was restored at its canonical path and preserved for review.";
                return true;
            }
            catch (Exception exception)
            {
                notice =
                    "The raced journal remains preserved at " + recoveryPath +
                    " because it could not be restored automatically: " +
                    exception.Message;
                return false;
            }
        }

        internal static bool TryReadJournalForTests(
            string journalPath,
            out GitOperationJournal journal,
            out string error)
        {
            return TryReadJournal(journalPath, out journal, out error);
        }

        internal static bool TryReplaceJournalForTests(
            string journalPath,
            GitOperationJournal journal,
            string expectedOperationId,
            Action beforeReplace,
            out string error)
        {
            try
            {
                WriteJournal(
                    journalPath,
                    journal,
                    true,
                    expectedOperationId,
                    beforeReplace);
                error = string.Empty;
                return true;
            }
            catch (Exception exception)
            {
                error = exception.Message;
                return false;
            }
        }

        internal static bool TryDeleteJournalForTests(
            string journalPath,
            string expectedOperationId,
            Action beforeDelete,
            out string error)
        {
            return TryDeleteJournal(
                journalPath,
                expectedOperationId,
                out error,
                beforeDelete);
        }

        internal static bool TryDeleteJournalAtClosingBoundaryForTests(
            string journalPath,
            string expectedOperationId,
            Action<string> afterQuarantineVerified,
            out string error)
        {
            return TryDeleteJournal(
                journalPath,
                expectedOperationId,
                out error,
                null,
                afterQuarantineVerified);
        }

        private static bool TryDeleteJournal(
            string journalPath,
            string expectedOperationId,
            out string error,
            Action beforeDeleteForTests = null,
            Action<string> afterQuarantineVerifiedForTests = null)
        {
            string recoveryPath = string.Empty;
            bool movedToRecovery = false;
            try
            {
                if (!GitUtility.TryValidateProjectOwnedPath(journalPath, out error))
                    return false;

                if (!TryInspectJournalEntry(
                        journalPath,
                        out bool exists,
                        out _,
                        out error))
                {
                    return false;
                }

                if (!exists)
                {
                    error = string.Empty;
                    return true;
                }

                if (!IsValidJournalOperationId(expectedOperationId))
                {
                    error =
                        "An exact operation identity is required to remove the recovery journal.";
                    return false;
                }

                if (!TryReadJournalSnapshot(
                        journalPath,
                        out JournalFileSnapshot expectedSnapshot,
                        out string readError) ||
                    !string.Equals(
                        expectedSnapshot.Journal.operationId,
                        expectedOperationId,
                        StringComparison.Ordinal))
                {
                    error =
                        "The recovery journal changed ownership and was preserved for review. " +
                        readError;
                    return false;
                }

                beforeDeleteForTests?.Invoke();

                recoveryPath = BuildJournalRecoveryPath(journalPath, "deleted");
                if (!GitUtility.TryValidateProjectOwnedPath(recoveryPath, out error))
                    return false;

                // Move first: unlike File.Delete, this preserves whatever inode
                // actually occupied the path at the atomic mutation boundary.
                File.Move(journalPath, recoveryPath);
                movedToRecovery = true;

                if (!TryReadJournalSnapshot(
                        recoveryPath,
                        out JournalFileSnapshot movedSnapshot,
                        out string movedError) ||
                    !JournalSnapshotsEqual(expectedSnapshot, movedSnapshot))
                {
                    TryRestoreQuarantinedJournal(
                        recoveryPath,
                        journalPath,
                        out string restoreNotice);
                    movedToRecovery = File.Exists(recoveryPath);
                    error =
                        "The recovery journal changed at its atomic removal boundary. " +
                        "No raced data was deleted. " + movedError + " " +
                        restoreNotice;
                    return false;
                }

                if (!TryInspectJournalEntry(
                        journalPath,
                        out bool replacementExists,
                        out _,
                        out string closingError))
                {
                    error = closingError +
                            " The removed journal remains preserved at " +
                            recoveryPath + ".";
                    return false;
                }
                if (replacementExists)
                {
                    error =
                        "A new recovery journal appeared during removal. Both it and the removed journal at " +
                        recoveryPath + " were preserved for review.";
                    return false;
                }

                afterQuarantineVerifiedForTests?.Invoke(recoveryPath);
                if (!TryReadJournalSnapshot(
                        recoveryPath,
                        out JournalFileSnapshot closingSnapshot,
                        out string recoveryClosingError) ||
                    !JournalSnapshotsEqual(expectedSnapshot, closingSnapshot))
                {
                    error =
                        "The quarantined recovery journal changed at the closing removal boundary. " +
                        "The late data remains preserved at " + recoveryPath + ". " +
                        recoveryClosingError;
                    return false;
                }

                if (!TryInspectJournalEntry(
                        journalPath,
                        out replacementExists,
                        out _,
                        out closingError) ||
                    replacementExists)
                {
                    error = string.IsNullOrWhiteSpace(closingError)
                        ? "A new recovery journal appeared at the closing removal boundary. " +
                          "Both it and the quarantined journal at " + recoveryPath +
                          " were preserved for review."
                        : closingError + " The quarantined journal remains preserved at " +
                          recoveryPath + ".";
                    return false;
                }

                // Keep the exact removed snapshot in Library. A writer may
                // still hold the moved inode after verification, so deleting it
                // here would reopen the late-writer data-loss seam that the
                // quarantine move is intended to close.
                movedToRecovery = false;
                error = string.Empty;
                return true;
            }
            catch (Exception ex)
            {
                error = $"Failed to remove operation journal: {ex.Message}";
                if (movedToRecovery && File.Exists(recoveryPath))
                {
                    error += " The journal remains preserved at " +
                             recoveryPath + ".";
                }
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
