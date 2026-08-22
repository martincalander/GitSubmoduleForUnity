using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using UnityEditor;
using UnityEditor.PackageManager;

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
        private static bool isReady;
        private static volatile bool isShuttingDown;
        private static string lastError = string.Empty;

        internal static event Action SnapshotChanged;

        static PackageManagerSubmoduleSnapshot()
        {
            // Unity properties are not thread-safe. Resolve and cache the project
            // root on the main thread before any background Git discovery begins.
            ProjectRoot = GitUtility.ProjectRoot;
            GitModulesPath = Path.Combine(ProjectRoot, ".gitmodules");
            observedRepositoryGeneration = GitOperationService.RepositoryGeneration;
            observedGitModulesWriteTicks = GetGitModulesWriteTicks();

            EditorApplication.update += Update;
            EditorApplication.projectChanged += Refresh;
            Events.registeredPackages += OnRegisteredPackages;
            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;
            Refresh();
        }

        internal static bool IsReady
        {
            get
            {
                lock (Gate)
                    return isReady;
            }
        }

        internal static int Count
        {
            get
            {
                lock (Gate)
                    return current.Count;
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

        internal static string LastError
        {
            get
            {
                lock (Gate)
                    return lastError;
            }
        }

        /// <summary>
        /// Requests a fresh asynchronous submodule snapshot. This method never
        /// runs Git synchronously and is safe for Package Manager host callbacks.
        /// </summary>
        internal static void Refresh()
        {
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
            return ready && snapshot.TryGet(packageName, localPath, isInstalled, out info);
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

        private static void OnRegisteredPackages(PackageRegistrationEventArgs _)
        {
            Refresh();
        }

        private static void Update()
        {
            if (isShuttingDown)
                return;

            PublishPendingResult();

            if (EditorApplication.timeSinceStartup >= nextExternalChangeCheck)
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
                    current = PackageManagerSubmoduleSnapshotData.Create(
                        result.Packages,
                        ProjectRoot);
                    isReady = true;
                    lastError = string.Empty;
                    consecutiveFailures = 0;
                    retryNotBefore = 0d;
                    changed = true;
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
            EditorApplication.update -= Update;
            EditorApplication.projectChanged -= Refresh;
            Events.registeredPackages -= OnRegisteredPackages;
            AssemblyReloadEvents.beforeAssemblyReload -= OnBeforeAssemblyReload;

            Thread threadToDrain;
            CancellationTokenSource cancellationToDispose;
            lock (Gate)
            {
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
