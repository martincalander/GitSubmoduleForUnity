using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Calander.SubmodulePackageManager.Editor
{
    public partial class GitSubmodulesWindow : EditorWindow
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

        private readonly SubmoduleRepositoryCoordinator repositoryCoordinator = new();

        private Tab currentTab = Tab.Installed;
        private Vector2 listScroll;
        private Vector2 detailsScroll;

        private List<SubmoduleInfo> installedSubmodules = new();
        private List<GitHubRepo> availableRepos = new();

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

        private string searchFilter = string.Empty;
        private string selectedRepoPackageName = string.Empty;
        private string selectedRepoBranch = string.Empty;

        private SortOption currentSort = SortOption.Name;
        private FilterOption currentFilter = FilterOption.All;

        private double lastInstalledRefreshTime;
        private double lastDiscoverRefreshTime;
        private DateTime lastRefreshDateTime;

        private int lastInstalledIndex = -1;
        private string installedBranchInput = string.Empty;
        private string installedActionStatus = string.Empty;
        private MessageType installedActionStatusType = MessageType.None;

        private AddFromUrlPopup activeAddPopup;

        private void OnEnable()
        {
            RefreshDependencies();
            RefreshCurrentTab();
        }

        private void OnDisable()
        {
            repositoryCoordinator.Dispose();
        }

        private void OnGUI()
        {
            Styles.Initialize();
            UpdateRepoLoading();
            UpdateBranchFetching();

            EditorGUILayout.BeginVertical();
            DrawToolbar();

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

        internal void RefreshSubmodules()
        {
            RefreshDependencies();
            RefreshInstalled();
        }
    }
}
