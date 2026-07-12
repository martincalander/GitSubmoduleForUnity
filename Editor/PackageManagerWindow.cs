using System;
using System.Collections.Generic;
using System.Threading;
using UnityEditor;
using UnityEngine;

namespace Essentials.GitPackageManager.Editor
{
    public partial class GitPackageManagerWindow : EditorWindow
    {
        private enum Tab
        {
            Installed,
            Discover
        }

        private enum SortOption
        {
            Name,
            RecentlyUpdated
        }

        private enum FilterOption
        {
            All,
            PublicOnly,
            PrivateOnly
        }

        private const string PackageNameRule = "Package name must follow com.author.package (lowercase).";
        private const float ListPaneWidth = 320f;
        private const double AutoRefreshIntervalSeconds = 300.0;

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
        private string selectedRepoPackageName = string.Empty;
        private string selectedRepoBranch = string.Empty;

        private SortOption currentSort = SortOption.Name;
        private FilterOption currentFilter = FilterOption.All;

        private double lastInstalledRefreshTime;
        private DateTime lastRefreshDateTime;

        private int lastInstalledIndex = -1;
        private string installedBranchInput = string.Empty;
        private string installedActionStatus = string.Empty;
        private MessageType installedActionStatusType = MessageType.None;

        private AsyncCommandHandle activeOperation;
        private string activeOperationLabel = string.Empty;
        private bool activeOperationPollingRegistered;
        private bool activeOperationSuppressesAutoRefresh;
        private AsyncCommandHandle cliInstallOperation;
        private CliInstallPlan activeCliInstallPlan;
        private ToolKind activeCliInstallTool;

        private AddFromUrlPopup activeAddPopup;

        private volatile InitialLoadResult pendingLoadResult;
        private bool isInitialLoading;
        private int initialLoadGeneration;

        private void OnEnable()
        {
            // Rebuild the polling registration after reloads or window re-enable.
            EditorApplication.update -= UpdateActiveOperation;
            activeOperationPollingRegistered = false;
            ApplyThemeIcon();
            minSize = new Vector2(720f, 420f);
            // Cache ProjectRoot on main thread before background work
            _ = GitUtility.ProjectRoot;

            isInitialLoading = true;
            pendingLoadResult = null;
            int generation = Interlocked.Increment(ref initialLoadGeneration);
            new Thread(() => RunInitialLoad(generation)) { IsBackground = true }.Start();

            if (activeOperation != null)
                RegisterActiveOperationPolling();
        }

        private void OnFocus()
        {
            ApplyThemeIcon();
        }

        internal void ApplyThemeIcon()
        {
            var iconFileName = EditorGUIUtility.isProSkin
                ? "GitEditorWindowIcon.png"
                : "GitEditorWindowIconLight.png";
            var icon = AssetDatabase.LoadAssetAtPath<Texture2D>(
                $"Packages/com.essentials.gitpackagemanager/Editor/{iconFileName}");
            titleContent = new GUIContent("Git Package Manager", icon);
        }

        private void OnDisable()
        {
            Interlocked.Increment(ref initialLoadGeneration);
            pendingLoadResult = null;
            if (activeAddPopup != null)
            {
                activeAddPopup.ClosePopup();
                activeAddPopup = null;
            }

            repositoryCoordinator.Dispose();
            discoveryCoordinator.Dispose();
            if (activeOperation == null)
                UnregisterActiveOperationPolling();
        }

        private void Update()
        {
            if (isInitialLoading)
            {
                var result = pendingLoadResult;
                if (result != null)
                {
                    ApplyLoadResult(result);
                    pendingLoadResult = null;
                    isInitialLoading = false;
                }

                Repaint();
                return;
            }

            UpdateDiscovery();
            UpdateBranchFetching();
            UpdateCliInstallOperation();
            activeAddPopup?.RepaintPopup();
        }

        private void OnGUI()
        {
            Styles.Initialize();

            EditorGUILayout.BeginVertical();
            DrawToolbar();

            if (isInitialLoading)
            {
                GUILayout.FlexibleSpace();
                EditorGUILayout.LabelField("Loading packages...", Styles.LoadingLabel);
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

        internal void RefreshPackages()
        {
            if (isInitialLoading)
                return;

            RefreshDependencies();
            RefreshInstalled();
        }
    }
}
