using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using UnityEditor;

namespace MartinCalander.GitSubmoduleManager.Editor
{
    [InitializeOnLoad]
    internal static class PackageManagerSubmoduleSnapshot
    {
        private const int MaximumAutomaticRetries = 3;
        private const int ReloadDrainTimeoutMilliseconds = 2000;

        private sealed class RefreshResult
        {
            internal int Generation;
            internal bool Success;
            internal List<GitPackageInfo> Packages;
            internal string Error;
        }

        private static readonly object Gate = new object();
        private static readonly string ProjectRoot;
        private static readonly string GitModulesPath;

        private static PackageManagerSubmoduleSnapshotData current =
            PackageManagerSubmoduleSnapshotData.Empty;
        private static volatile RefreshResult pendingResult;
        private static CancellationTokenSource refreshCancellationSource;
        private static Thread refreshThread;
        private static int requestedGeneration;
        private static int runningGeneration;
        private static long observedRepositoryGeneration;
        private static long observedGitModulesWriteTicks;
        private static double nextExternalChangeCheck;
        private static double retryNotBefore;
        private static int consecutiveFailures;
        private static int hostObserverCount;
        private static bool isReady;
        private static bool isListening;
        private static volatile bool isShuttingDown;
        private static string lastError = string.Empty;

        internal static event Action SnapshotChanged;

        static PackageManagerSubmoduleSnapshot()
        {
            // Unity properties are not thread-safe. Resolve and cache the project
            // root on the main thread before any background Git discovery begins.
            ProjectRoot = GitUtility.ProjectRoot;
            GitModulesPath = Path.Combine(ProjectRoot, ".gitmodules");
        }

        internal static bool IsReady
        {
            get
            {
                lock (Gate)
                    return isReady;
            }
        }

        /// <summary>
        /// True from before the snapshot worker starts until its terminal result
        /// is published. Repository mutations use this to avoid racing a Git read.
        /// </summary>
        internal static bool IsReaderActive
        {
            get
            {
                lock (Gate)
                    return refreshThread != null;
            }
        }

        internal static void RetainHostObserver()
        {
            lock (Gate)
            {
                if (isShuttingDown)
                    return;
                hostObserverCount = TransitionHostObserverCount(
                    hostObserverCount,
                    retain: true,
                    shuttingDown: false);
            }

            EnsureListening();
        }

        internal static void ReleaseHostObserver()
        {
            lock (Gate)
            {
                hostObserverCount = TransitionHostObserverCount(
                    hostObserverCount,
                    retain: false,
                    isShuttingDown);
            }

            StopListeningIfIdle();
        }

        internal static bool ShouldKeepListening(
            bool shuttingDown,
            int observerCount,
            bool readerActive,
            bool hasPendingResult,
            bool hasPendingRequest)
        {
            return !shuttingDown &&
                   (observerCount > 0 || readerActive || hasPendingResult ||
                    hasPendingRequest);
        }

        internal static int TransitionHostObserverCount(
            int observerCount,
            bool retain,
            bool shuttingDown)
        {
            if (shuttingDown)
                return 0;

            int normalizedCount = Math.Max(0, observerCount);
            if (!retain)
                return Math.Max(0, normalizedCount - 1);

            return normalizedCount == int.MaxValue
                ? int.MaxValue
                : normalizedCount + 1;
        }

        /// <summary>
        /// Requests a fresh asynchronous submodule snapshot. This method never
        /// runs Git synchronously and is safe for Package Manager host callbacks.
        /// </summary>
        internal static void Refresh()
        {
            EnsureListening();
            lock (Gate)
            {
                if (!isShuttingDown)
                {
                    consecutiveFailures = 0;
                    retryNotBefore = 0d;
                    requestedGeneration++;
                }
            }
        }

        internal static bool TryGet(
            string packageName,
            string localPath,
            bool isInstalled,
            out PackageManagerSubmoduleInfo info)
        {
            EnsureListening();
            PackageManagerSubmoduleSnapshotData snapshot;
            bool ready;
            lock (Gate)
            {
                snapshot = current;
                ready = isReady;
                if (!ready && requestedGeneration == 0 && !isShuttingDown)
                    requestedGeneration++;
            }

            info = null;
            bool found = ready &&
                         snapshot.TryGet(
                             packageName,
                             localPath,
                             isInstalled,
                             out info);
            StopListeningIfIdle();
            return found;
        }

        private static void EnsureListening()
        {
            if (isShuttingDown || isListening)
                return;

            observedRepositoryGeneration = GitOperationService.RepositoryGeneration;
            observedGitModulesWriteTicks = GetGitModulesWriteTicks();
            isListening = true;
            EditorApplication.update += Update;
            EditorApplication.projectChanged += Refresh;
            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;
        }

        internal static bool ContainsGitHubRepository(
            string owner,
            string repository)
        {
            lock (Gate)
            {
                return isReady &&
                       current.ContainsGitHubRepository(owner, repository);
            }
        }

        private static void Update()
        {
            if (isShuttingDown)
                return;

            PublishPendingResult();

            bool hasHostObservers;
            lock (Gate)
                hasHostObservers = hostObserverCount > 0;
            if (hasHostObservers &&
                EditorApplication.timeSinceStartup >= nextExternalChangeCheck)
            {
                nextExternalChangeCheck = EditorApplication.timeSinceStartup + 1d;
                long repositoryGeneration = GitOperationService.RepositoryGeneration;
                long gitModulesWriteTicks = GetGitModulesWriteTicks();
                if (repositoryGeneration != observedRepositoryGeneration ||
                    gitModulesWriteTicks != observedGitModulesWriteTicks)
                {
                    observedRepositoryGeneration = repositoryGeneration;
                    observedGitModulesWriteTicks = gitModulesWriteTicks;
                    Refresh();
                }
            }

            TryStartRefresh();
            StopListeningIfIdle();
        }

        private static void StopListeningIfIdle()
        {
            if (!isListening)
                return;

            bool keepListening;
            lock (Gate)
            {
                keepListening = ShouldKeepListening(
                    isShuttingDown,
                    hostObserverCount,
                    refreshThread != null,
                    pendingResult != null,
                    runningGeneration != requestedGeneration);
                if (!keepListening)
                    isListening = false;
            }

            if (keepListening)
                return;

            EditorApplication.update -= Update;
            EditorApplication.projectChanged -= Refresh;
            AssemblyReloadEvents.beforeAssemblyReload -= OnBeforeAssemblyReload;
        }

        private static void TryStartRefresh()
        {
            int generation;
            lock (Gate)
            {
                if (isShuttingDown ||
                    refreshThread != null ||
                    runningGeneration == requestedGeneration)
                {
                    return;
                }

                // Do not race the package tool's own readers or inspect a
                // repository while one of its mutations is in progress.
                if (GitOperationService.IsBusy ||
                    EditorApplication.timeSinceStartup < retryNotBefore)
                {
                    return;
                }

                generation = requestedGeneration;
                runningGeneration = generation;
                refreshCancellationSource = new CancellationTokenSource();
                CancellationToken cancellationToken = refreshCancellationSource.Token;
                refreshThread = new Thread(() => RunRefresh(generation, cancellationToken))
                {
                    IsBackground = true,
                    Name = "Git Submodule Manager Package Manager snapshot"
                };
            }

            try
            {
                refreshThread.Start();
            }
            catch (Exception exception)
            {
                lock (Gate)
                {
                    refreshCancellationSource?.Dispose();
                    refreshCancellationSource = null;
                    refreshThread = null;
                    lastError = GitHubUtility.SanitizeUiDiagnostic(exception.Message);
                    consecutiveFailures++;
                    retryNotBefore = EditorApplication.timeSinceStartup +
                                     Math.Min(30d, Math.Pow(2d, consecutiveFailures));
                    if (consecutiveFailures <= MaximumAutomaticRetries)
                        requestedGeneration++;
                }
            }
        }

        private static void RunRefresh(int generation, CancellationToken cancellationToken)
        {
            var result = new RefreshResult
            {
                Generation = generation,
                Packages = new List<GitPackageInfo>(),
                Error = string.Empty
            };

            try
            {
                result.Success = GitUtility.TryGetSubmodules(
                    out List<GitPackageInfo> packages,
                    out string error,
                    cancellationToken);
                result.Packages = packages ?? new List<GitPackageInfo>();
                result.Error = error ?? string.Empty;
            }
            catch (OperationCanceledException)
            {
                result.Success = false;
                result.Error = string.Empty;
            }
            catch (Exception exception)
            {
                result.Success = false;
                result.Error = GitHubUtility.SanitizeUiDiagnostic(exception.Message);
            }

            if (!isShuttingDown)
                pendingResult = result;
        }

        private static void PublishPendingResult()
        {
            RefreshResult result = pendingResult;
            if (result == null)
                return;

            pendingResult = null;
            bool changed = false;
            lock (Gate)
            {
                refreshCancellationSource?.Dispose();
                refreshCancellationSource = null;
                refreshThread = null;

                if (result.Generation != requestedGeneration)
                    return;

                if (result.Success)
                {
                    PackageManagerSubmoduleSnapshotData refreshed =
                        PackageManagerSubmoduleSnapshotData.Create(
                        result.Packages,
                        ProjectRoot);
                    changed = !isReady ||
                              !string.IsNullOrEmpty(lastError) ||
                              !current.HasSameContent(refreshed);
                    if (changed)
                        current = refreshed;
                    isReady = true;
                    lastError = string.Empty;
                    consecutiveFailures = 0;
                    retryNotBefore = 0d;
                }
                else if (!string.IsNullOrWhiteSpace(result.Error))
                {
                    // Retain the last known-good snapshot when a transient Git
                    // failure occurs. Presentation fails open before the first
                    // successful load and never blocks Package Manager itself.
                    lastError = result.Error;
                    consecutiveFailures++;
                    retryNotBefore = EditorApplication.timeSinceStartup +
                                     Math.Min(30d, Math.Pow(2d, consecutiveFailures));
                    if (consecutiveFailures <= MaximumAutomaticRetries)
                        requestedGeneration++;
                }
            }

            if (changed)
            {
                try
                {
                    SnapshotChanged?.Invoke();
                }
                catch
                {
                    // Snapshot consumers must not break the Editor update loop.
                }
            }
        }

        private static long GetGitModulesWriteTicks()
        {
            try
            {
                return File.Exists(GitModulesPath)
                    ? File.GetLastWriteTimeUtc(GitModulesPath).Ticks
                    : 0L;
            }
            catch
            {
                return -1L;
            }
        }

        private static void OnBeforeAssemblyReload()
        {
            isShuttingDown = true;
            if (isListening)
            {
                isListening = false;
                EditorApplication.update -= Update;
                EditorApplication.projectChanged -= Refresh;
                AssemblyReloadEvents.beforeAssemblyReload -= OnBeforeAssemblyReload;
            }

            Thread threadToDrain;
            CancellationTokenSource cancellationToDispose;
            lock (Gate)
            {
                hostObserverCount = TransitionHostObserverCount(
                    hostObserverCount,
                    retain: false,
                    shuttingDown: true);
                threadToDrain = refreshThread;
                cancellationToDispose = refreshCancellationSource;
                try
                {
                    cancellationToDispose?.Cancel();
                }
                catch
                {
                    // Unity is already tearing down the managed domain.
                }
            }

            bool stopped = threadToDrain == null;
            if (!stopped && threadToDrain != Thread.CurrentThread)
            {
                try
                {
                    stopped = threadToDrain.Join(ReloadDrainTimeoutMilliseconds);
                }
                catch
                {
                    stopped = false;
                }
            }

            if (!stopped)
                return;

            lock (Gate)
            {
                if (ReferenceEquals(refreshThread, threadToDrain))
                    refreshThread = null;
                if (ReferenceEquals(refreshCancellationSource, cancellationToDispose))
                    refreshCancellationSource = null;
            }

            cancellationToDispose?.Dispose();
        }
    }
}
