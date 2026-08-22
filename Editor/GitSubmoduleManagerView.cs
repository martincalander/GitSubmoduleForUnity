using System;
using System.Collections.Generic;
using System.Threading;
using UnityEditor;
using UnityEngine;

namespace MartinCalander.GitSubmoduleManager.Editor
{
    internal sealed class DeferredRepositoryMutationQueue
    {
        private static int globalPendingCount;
        private Action pendingMutation;

        internal bool HasPending => pendingMutation != null;
        internal static bool HasAnyPending => Volatile.Read(ref globalPendingCount) != 0;
        internal string Label { get; private set; } = string.Empty;

        internal bool TryEnqueue(string label, Action mutation)
        {
            if (mutation == null || pendingMutation != null ||
                Interlocked.CompareExchange(ref globalPendingCount, 1, 0) != 0)
                return false;

            Label = string.IsNullOrWhiteSpace(label)
                ? "Repository operation"
                : label.Trim();
            pendingMutation = mutation;
            return true;
        }

        internal bool TryDequeueWhenReady(bool canStart, out Action mutation)
        {
            mutation = null;
            if (!canStart || pendingMutation == null)
                return false;

            mutation = pendingMutation;
            pendingMutation = null;
            Label = string.Empty;
            Interlocked.Exchange(ref globalPendingCount, 0);
            return true;
        }

        internal void Clear()
        {
            if (pendingMutation != null)
                Interlocked.Exchange(ref globalPendingCount, 0);
            pendingMutation = null;
            Label = string.Empty;
        }
    }

    internal partial class GitSubmoduleManagerView : ScriptableObject
    {
        internal enum Tab
        {
            Installed,
            Discover
        }

        private enum SortOption
        {
            Name,
            RecentlyUpdated
        }

        internal enum FilterOption
        {
            All,
            PublicOnly,
            PrivateOnly,
            ValidPackagesOnly
        }

        private const string PackageNameRule =
            "Use a lowercase reverse-domain UPM name (for example com.company.package); hyphens and underscores are supported.";
        internal const string CurrentPackageName = "com.martincalander.gitsubmodulemanager";
        internal const string CurrentPackagePath = "Packages/" + CurrentPackageName;
        internal const string LegacyPackageName = "com.martincalander.gitpackagemanager";
        internal const string LegacyPackagePath = "Packages/" + LegacyPackageName;
        private const int BackgroundLoadDrainTimeoutMs = 2000;
        private const float ListPaneWidth = 320f;

        private static int activeBackgroundLoadWorkers;

        private Action hostRepaint;
        private Action hostClose;
        private Rect position;
        private GUIContent titleContent;
        private Vector2 minSize;

        private readonly RepositoryCoordinator repositoryCoordinator = new();
        private readonly DiscoveryCoordinator discoveryCoordinator = new();

        private Tab currentTab = Tab.Installed;
        private Vector2 listScroll;
        private Vector2 detailsScroll;

        private List<GitPackageInfo> installedPackages = new();

        private int selectedInstalledIndex = -1;
        private int selectedRepoIndex = -1;

        private string gitVersion = string.Empty;
        private string ghVersion = string.Empty;
        private string gitError = string.Empty;
        private string ghError = string.Empty;
        private string ghAuthError = string.Empty;
        private bool gitAvailable;
        private bool ghAvailable;
        private bool ghAuthenticated;

        private string installStatus = string.Empty;
        private MessageType installStatusType = MessageType.None;
        private string installedStatus = string.Empty;
        private MessageType installedStatusType = MessageType.None;
        private string discoverStatus = string.Empty;
        private MessageType discoverStatusType = MessageType.None;
        private string operationStatus = string.Empty;
        private MessageType operationStatusType = MessageType.None;

        private string addUrl = string.Empty;
        private string addBranch = string.Empty;
        private string addPackageName = string.Empty;
        private string addStatus = string.Empty;
        private MessageType addStatusType = MessageType.None;

        private string searchFilter = string.Empty;
        private string installedSearchFilter = string.Empty;
        private string discoverSearchFilter = string.Empty;
        private string selectedRepoPackageName = string.Empty;
        private string selectedRepoBranch = string.Empty;
        private GitHubRepo selectedRepoManifestDefaultsSource;
        private bool selectedRepoDeclaredNameApplied;

        private SortOption currentSort = SortOption.Name;
        private FilterOption currentFilter = FilterOption.All;

        private double lastInstalledRefreshTime;
        private DateTime lastRefreshDateTime;

        private string installedBranchInput = string.Empty;
        private string installedActionStatus = string.Empty;
        private MessageType installedActionStatusType = MessageType.None;

        private static bool cliInstallInProgress;
        private static CliInstallPlan activeCliInstallPlan;
        private static ToolKind activeCliInstallTool;

        private readonly DeferredRepositoryMutationQueue deferredRepositoryMutation = new();
        private bool isWindowEnabled;

        internal static bool IsRepositoryOperationBusyState(
            bool operationExecutionBusy,
            bool deferredMutationPending)
        {
            return operationExecutionBusy || deferredMutationPending;
        }

        internal static bool IsGitHubInteractionBusyState(
            bool repositoryOperationBusy,
            bool authenticationInProgress)
        {
            return repositoryOperationBusy || authenticationInProgress;
        }

        internal static bool CanEnterDeferredWindowAction(
            UnityEngine.Object owner,
            bool windowEnabled)
        {
            return owner != null && windowEnabled;
        }

        private bool IsRepositoryOperationExecutionBusy =>
            GitOperationService.IsBusy || cliInstallInProgress;

        private bool IsRepositoryOperationBusy =>
            IsRepositoryOperationBusyState(
                IsRepositoryOperationExecutionBusy,
                DeferredRepositoryMutationQueue.HasAnyPending);

        private bool IsGitHubInteractionBusy =>
            IsGitHubInteractionBusyState(
                IsRepositoryOperationBusy,
                IsSharedGitHubAuthenticationBlocked);

        private AddFromUrlPopup activeAddPopup;

        private volatile InitialLoadResult pendingInitialGitStageResult;
        private volatile InitialLoadResult pendingLoadResult;
        private CancellationTokenSource initialLoadCancellationSource;
        private Thread initialLoadThread;
        private bool isInitialLoading;
        private bool initialGitStageReady;
        private int initialLoadGeneration;
        private bool dependencyCheckRequested;
        private bool dependencyCheckIncludesGitHub;
        private bool backgroundLoadDeferred;
        private volatile InstalledLoadResult pendingInstalledLoadResult;
        private CancellationTokenSource installedLoadCancellationSource;
        private Thread installedLoadThread;
        private bool isInstalledLoading;
        private int installedLoadGeneration;
        private double nextProgressRepaintTime;

        internal bool IsAttached => isWindowEnabled;
        internal GUIContent TitleContent => titleContent;
        internal Vector2 MinimumSize => minSize;

        internal void AttachToHost(
            Action repaint,
            Action close,
            Rect initialPosition,
            bool openWelcome)
        {
            hostRepaint = repaint;
            hostClose = close;
            position = initialPosition;
            if (isWindowEnabled)
            {
                if (openWelcome)
                    ShowWelcomeScreen();
                return;
            }

            isWindowEnabled = true;
            ApplyThemeIcon();
            ApplyStartupPreferences();
            InitializeWelcomeState();
            minSize = new Vector2(720f, 420f);
            // Cache ProjectRoot on main thread before background work
            _ = GitUtility.ProjectRoot;
            EnsureGitHubAuthenticationSafetyInitialized();
            discoveryCoordinator.SetValidPackageFilterEnabled(
                currentFilter == FilterOption.ValidPackagesOnly);

            BeginBackgroundLoad(false);

            if (!string.IsNullOrWhiteSpace(GitOperationService.RecoveryWarning))
            {
                operationStatus = GitOperationService.RecoveryWarning;
                operationStatusType = MessageType.Warning;
            }

            if (openWelcome)
                ShowWelcomeScreen();
        }

        private void ApplyStartupPreferences()
        {
            GitSubmoduleManagerUserSettings settings = GitSubmoduleManagerUserSettings.Instance;
            currentTab = settings.StartupTab == GitSubmoduleManagerStartupTab.GitHub
                ? Tab.Discover
                : Tab.Installed;
            currentFilter = settings.DefaultGitHubFilter ==
                GitSubmoduleManagerDefaultGitHubFilter.ValidUpmPackages
                ? FilterOption.ValidPackagesOnly
                : FilterOption.All;
        }

        private void OnFocus()
        {
            ApplyThemeIcon();
        }

        internal void ApplyThemeIcon()
        {
            titleContent = new GUIContent(
                "Git Submodule Manager",
                GitSubmoduleManagerIcons.GitIcon);
        }

        internal void DetachFromHost()
        {
            if (!isWindowEnabled)
            {
                hostRepaint = null;
                hostClose = null;
                return;
            }

            isWindowEnabled = false;
            deferredRepositoryMutation.Clear();
            ReleaseGitHubAuthentication();
            _ = CancelAndDrainBackgroundLoad(initialLoadCancellationSource, initialLoadThread);
            _ = CancelAndDrainBackgroundLoad(installedLoadCancellationSource, installedLoadThread);
            initialLoadCancellationSource = null;
            initialLoadThread = null;
            installedLoadCancellationSource = null;
            installedLoadThread = null;
            Interlocked.Increment(ref initialLoadGeneration);
            Interlocked.Increment(ref installedLoadGeneration);
            pendingInitialGitStageResult = null;
            pendingLoadResult = null;
            pendingInstalledLoadResult = null;
            isInitialLoading = false;
            initialGitStageReady = false;
            isInstalledLoading = false;
            dependencyCheckRequested = false;
            dependencyCheckIncludesGitHub = false;
            backgroundLoadDeferred = false;
            if (activeAddPopup != null)
            {
                activeAddPopup.ClosePopup();
                activeAddPopup = null;
            }

            repositoryCoordinator.Dispose();
            discoveryCoordinator.Dispose();
            hostRepaint = null;
            hostClose = null;
        }

        private void OnDisable()
        {
            DetachFromHost();
        }

        internal void Tick()
        {
            if (!isWindowEnabled)
                return;

            UpdateGitHubAuthentication();
            UpdateDeferredRepositoryMutation();

            if (backgroundLoadDeferred &&
                !IsGitHubInteractionBusy &&
                !AreBackgroundLoadsDraining &&
                !isInitialLoading &&
                !isInstalledLoading &&
                string.IsNullOrWhiteSpace(GitOperationService.RecoveryWarning))
            {
                backgroundLoadDeferred = false;
                BeginBackgroundLoad(dependencyCheckRequested);
            }

            if (isInitialLoading)
            {
                InitialLoadResult gitStageResult = pendingInitialGitStageResult;
                if (gitStageResult != null)
                {
                    pendingInitialGitStageResult = null;
                    if (ApplyLoadResult(gitStageResult))
                        initialGitStageReady = true;
                }

                InitialLoadResult finalResult = pendingLoadResult;
                if (finalResult != null)
                {
                    pendingLoadResult = null;
                    if (ApplyLoadResult(finalResult))
                        initialGitStageReady = true;
                    isInitialLoading = false;
                }

                if (isInitialLoading)
                {
                    RepaintProgress();
                    return;
                }
            }

            UpdateDiscovery();
            UpdateBranchFetching();
            UpdateInstalledRefresh();
            activeAddPopup?.RepaintPopup();
            if (IsGitHubInteractionBusy ||
                isInstalledLoading ||
                discoveryCoordinator.IsLoading ||
                discoveryCoordinator.IsCheckingPackageManifest ||
                discoveryCoordinator.IsValidatingPackageManifests ||
                IsGhAuthenticationInProgress)
            {
                RepaintProgress();
            }
        }

        internal void Render(Rect contentRect)
        {
            if (!isWindowEnabled)
                return;

            position = contentRect;
            Styles.Initialize();

            if (showWelcomeScreen)
            {
                DrawWelcomeScreen();
                return;
            }

            EditorGUILayout.BeginVertical();
            DrawToolbar();

            bool waitingForPreviousLoad =
                backgroundLoadDeferred &&
                !isInitialLoading &&
                AreBackgroundLoadsDraining;
            bool initialLoadBlocksCurrentTab = ShouldBlockCurrentTabDuringInitialLoad(
                isInitialLoading,
                initialGitStageReady,
                currentTab);
            if (initialLoadBlocksCurrentTab || waitingForPreviousLoad)
            {
                GUILayout.FlexibleSpace();
                DrawLoadingState(
                    deferredRepositoryMutation.HasPending
                        ? "Waiting for the package scan to stop..."
                        : isInitialLoading && initialGitStageReady && currentTab == Tab.Discover
                            ? "Checking GitHub CLI..."
                        : waitingForPreviousLoad
                        ? "Waiting for the previous package scan to stop..."
                        : "Loading project packages...",
                    deferredRepositoryMutation.HasPending
                        ? $"The queued operation will start automatically: {deferredRepositoryMutation.Label}"
                        : isInitialLoading && initialGitStageReady && currentTab == Tab.Discover
                            ? "In Project is ready while the optional GitHub dependency and authentication checks finish."
                        : waitingForPreviousLoad
                        ? "The new scan will start as soon as the earlier background command has drained safely."
                        : "Checking Git and scanning installed package submodules.",
                    topSpacing: 0f);
                GUILayout.FlexibleSpace();
                EditorGUILayout.EndVertical();
                return;
            }

            if (!DrawDependencyGate())
            {
                EditorGUILayout.EndVertical();
                return;
            }

            EditorGUILayout.BeginHorizontal();
            DrawListPane();
            DrawDetailsPane();
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.EndVertical();
        }

        private void Repaint()
        {
            hostRepaint?.Invoke();
        }

        private void Close()
        {
            hostClose?.Invoke();
        }

        internal void RefreshPackages()
        {
            BeginBackgroundLoad(true);
        }

        private void BeginBackgroundLoad(bool isDependencyCheck)
        {
            dependencyCheckRequested |= isDependencyCheck;
            // Authentication blocks only the optional gh stage. The required Git
            // and installed-package stage must still initialize this window.
            if (IsRepositoryOperationBusy ||
                AreBackgroundLoadsDraining ||
                !string.IsNullOrWhiteSpace(GitOperationService.RecoveryWarning))
            {
                backgroundLoadDeferred = true;
                return;
            }

            if (isInitialLoading)
                return;

            backgroundLoadDeferred = false;
            isInitialLoading = true;
            initialGitStageReady = false;
            pendingInitialGitStageResult = null;
            pendingLoadResult = null;
            int generation = Interlocked.Increment(ref initialLoadGeneration);
            long repositoryGeneration = GitOperationService.RepositoryGeneration;
            var cancellationSource = new CancellationTokenSource();
            var thread = new Thread(() =>
            {
                try
                {
                    RunInitialLoad(generation, repositoryGeneration, cancellationSource.Token);
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
                Name = "Git Submodule Manager initial load"
            };

            initialLoadCancellationSource = cancellationSource;
            initialLoadThread = thread;
            Interlocked.Increment(ref activeBackgroundLoadWorkers);
            try
            {
                thread.Start();
            }
            catch (Exception ex)
            {
                Interlocked.Decrement(ref activeBackgroundLoadWorkers);
                cancellationSource.Dispose();
                initialLoadCancellationSource = null;
                initialLoadThread = null;
                isInitialLoading = false;
                installedStatus = BuildInitialLoadFailureMessage(
                    "The package scan could not start",
                    ex);
                installedStatusType = MessageType.Error;
            }
        }

        internal static bool AreBackgroundLoadsDraining =>
            Volatile.Read(ref activeBackgroundLoadWorkers) > 0;

        private bool RequestRepositoryReadCancellation(out string error)
        {
            RequestBackgroundLoadCancellation(initialLoadCancellationSource);
            RequestBackgroundLoadCancellation(installedLoadCancellationSource);

            bool initialStopped = initialLoadThread == null || !initialLoadThread.IsAlive;
            bool installedStopped = installedLoadThread == null || !installedLoadThread.IsAlive;

            if (initialStopped)
            {
                initialLoadCancellationSource = null;
                initialLoadThread = null;
            }

            if (installedStopped)
            {
                installedLoadCancellationSource = null;
                installedLoadThread = null;
            }

            if (initialStopped && installedStopped && !AreBackgroundLoadsDraining)
            {
                error = string.Empty;
                return true;
            }

            error =
                "A package scan is still stopping safely.";
            return false;
        }

        internal static bool IsBackgroundLoadResultCurrent(
            int resultLoadGeneration,
            int currentLoadGeneration,
            long resultRepositoryGeneration,
            long currentRepositoryGeneration)
        {
            return resultLoadGeneration == currentLoadGeneration &&
                   resultRepositoryGeneration == currentRepositoryGeneration;
        }

        internal static bool ShouldBlockCurrentTabDuringInitialLoad(
            bool isLoading,
            bool gitStageReady,
            Tab currentTab)
        {
            return isLoading && (!gitStageReady || currentTab == Tab.Discover);
        }

        private static void RequestBackgroundLoadCancellation(
            CancellationTokenSource cancellationSource)
        {
            try
            {
                if (cancellationSource != null)
                {
                    lock (cancellationSource)
                        cancellationSource.Cancel();
                }
            }
            catch (ObjectDisposedException)
            {
                // The worker completed between the lifecycle callback and cancellation.
            }
            catch (AggregateException)
            {
                // Cancellation is still requested even if an external callback failed.
                // The drain barrier remains in place until the worker actually exits.
            }
        }

        private static bool CancelAndDrainBackgroundLoad(
            CancellationTokenSource cancellationSource,
            Thread thread)
        {
            if (cancellationSource == null && thread == null)
                return true;

            RequestBackgroundLoadCancellation(cancellationSource);

            if (thread == null ||
                !thread.IsAlive ||
                ReferenceEquals(Thread.CurrentThread, thread))
            {
                return thread == null || !thread.IsAlive;
            }

            return thread.Join(BackgroundLoadDrainTimeoutMs);
        }

        private void RepaintProgress()
        {
            double now = EditorApplication.timeSinceStartup;
            if (now < nextProgressRepaintTime)
                return;

            nextProgressRepaintTime = now + 0.1;
            Repaint();
        }
    }
}
