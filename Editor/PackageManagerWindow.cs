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
            ValidPackagesOnly,
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

        private string addUrl = string.Empty;
        private string addBranch = "main";
        private string addPackageName = string.Empty;
        private string addStatus = string.Empty;
        private MessageType addStatusType = MessageType.None;
        private PackageSourceType addSourceType = PackageSourceType.Submodule;

        private string searchFilter = string.Empty;
        private string selectedRepoPackageName = string.Empty;
        private string selectedRepoBranch = string.Empty;
        private PackageSourceType selectedRepoSourceType = PackageSourceType.Submodule;

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

        private AddFromUrlPopup activeAddPopup;

        private volatile InitialLoadResult pendingLoadResult;
        private bool isInitialLoading;

        private void OnEnable()
        {
            // Cache ProjectRoot on main thread before background work
            _ = GitUtility.ProjectRoot;

            isInitialLoading = true;
            pendingLoadResult = null;
            new Thread(RunInitialLoad) { IsBackground = true }.Start();
        }

        private void OnDisable()
        {
            if (activeAddPopup != null)
            {
                activeAddPopup.ClosePopup();
                activeAddPopup = null;
            }

            repositoryCoordinator.Dispose();
            discoveryCoordinator.Dispose();
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
            UpdateActiveOperation();
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
            RefreshDependencies();
            RefreshInstalled();
        }
    }
}
