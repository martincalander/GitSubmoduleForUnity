using System;
using UnityEditor;
using UnityEngine;

namespace Calander.SubmodulePackageManager.Editor
{
    public partial class GitSubmodulesWindow
    {
        private void DrawListPane()
        {
            EditorGUILayout.BeginVertical(GUILayout.Width(ListPaneWidth));

            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
            searchFilter = EditorGUILayout.TextField(searchFilter, EditorStyles.toolbarSearchField);
            if (GUILayout.Button(string.Empty, GUI.skin.FindStyle("ToolbarSearchCancelButton") ?? EditorStyles.toolbarButton, GUILayout.Width(18)))
            {
                searchFilter = string.Empty;
                GUI.FocusControl(null);
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            GUILayout.Label(currentTab == Tab.Installed ? "Packages" : "Repositories", Styles.HeaderLabel);
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();

            Rect lineRect = GUILayoutUtility.GetRect(1, 1, GUILayout.ExpandWidth(true));
            EditorGUI.DrawRect(lineRect, new Color(0.15f, 0.15f, 0.15f));

            listScroll = EditorGUILayout.BeginScrollView(listScroll, GUILayout.ExpandHeight(true));
            if (currentTab == Tab.Installed)
                DrawInstalledList();
            else
                DrawDiscoverList();
            EditorGUILayout.EndScrollView();

            DrawListFooter();
            EditorGUILayout.EndVertical();
        }

        private void DrawInstalledList()
        {
            if (!string.IsNullOrWhiteSpace(installedStatus))
            {
                EditorGUILayout.HelpBox(installedStatus, installedStatusType);
                return;
            }

            if (installedSubmodules == null || installedSubmodules.Count == 0)
            {
                GUILayout.Label("No packages installed via git submodules.", EditorStyles.centeredGreyMiniLabel);
                return;
            }

            for (int i = 0; i < installedSubmodules.Count; i++)
            {
                SubmoduleInfo submodule = installedSubmodules[i];
                string displayName = string.IsNullOrWhiteSpace(submodule.PackageName) ? submodule.Name : submodule.PackageName;
                if (!string.IsNullOrWhiteSpace(searchFilter) &&
                    displayName.IndexOf(searchFilter, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                bool isSelected = i == selectedInstalledIndex;
                string versionText = !string.IsNullOrWhiteSpace(submodule.Branch) ? submodule.Branch : "main";
                Rect itemRect = GUILayoutUtility.GetRect(GUIContent.none, EditorStyles.label, GUILayout.ExpandWidth(true), GUILayout.Height(24));

                if (Event.current.type == EventType.Repaint && isSelected)
                    EditorGUI.DrawRect(itemRect, new Color(0.17f, 0.36f, 0.53f, 1f));

                Rect nameRect = new Rect(itemRect.x + 8, itemRect.y + 4, itemRect.width - 70, itemRect.height - 8);
                var nameStyle = new GUIStyle(EditorStyles.label);
                if (isSelected)
                    nameStyle.normal.textColor = Color.white;
                GUI.Label(nameRect, displayName, nameStyle);

                Rect versionRect = new Rect(itemRect.xMax - 60, itemRect.y + 4, 52, itemRect.height - 8);
                GUI.Label(versionRect, versionText, Styles.SubtitleLabel);

                if (Event.current.type == EventType.MouseDown && itemRect.Contains(Event.current.mousePosition))
                {
                    selectedInstalledIndex = i;
                    Event.current.Use();
                    Repaint();
                }
            }
        }

        private void DrawDiscoverList()
        {
            if (repositoryCoordinator.IsLoadingRepos && repositoryCoordinator.RepoListHandle != null)
            {
                EditorGUILayout.Space(20);
                EditorGUILayout.LabelField(repositoryCoordinator.RepoListHandle.StatusMessage, EditorStyles.centeredGreyMiniLabel);
                Rect progressRect = GUILayoutUtility.GetRect(0, 4, GUILayout.ExpandWidth(true));
                EditorGUI.ProgressBar(progressRect, repositoryCoordinator.RepoListHandle.Progress, string.Empty);
                return;
            }

            if (repositoryCoordinator.IsCheckingPackageJson)
            {
                EditorGUILayout.Space(20);
                string checkMsg = $"Checking package.json ({repositoryCoordinator.PackageJsonCheckIndex}/{repositoryCoordinator.PackageJsonRepoCount})...";
                EditorGUILayout.LabelField(checkMsg, EditorStyles.centeredGreyMiniLabel);
                float checkProgress = repositoryCoordinator.PackageJsonRepoCount > 0
                    ? (float)repositoryCoordinator.PackageJsonCheckIndex / repositoryCoordinator.PackageJsonRepoCount
                    : 0f;
                Rect progressRect = GUILayoutUtility.GetRect(0, 4, GUILayout.ExpandWidth(true));
                EditorGUI.ProgressBar(progressRect, checkProgress, string.Empty);
                return;
            }

            if (!string.IsNullOrWhiteSpace(discoverStatus))
            {
                EditorGUILayout.HelpBox(discoverStatus, discoverStatusType);
                return;
            }

            if (availableRepos == null || availableRepos.Count == 0)
            {
                if (!ghAvailable)
                    GUILayout.Label("Install GitHub CLI to discover repositories.", EditorStyles.centeredGreyMiniLabel);
                else if (!ghAuthenticated)
                    GUILayout.Label("Authenticate GitHub CLI to discover repositories.", EditorStyles.centeredGreyMiniLabel);
                else
                    GUILayout.Label("No repositories found. Click refresh to load.", EditorStyles.centeredGreyMiniLabel);
                return;
            }

            for (int i = 0; i < availableRepos.Count; i++)
            {
                GitHubRepo repo = availableRepos[i];
                if (!PassesFilter(repo))
                    continue;

                if (!string.IsNullOrWhiteSpace(searchFilter) &&
                    repo.Name.IndexOf(searchFilter, StringComparison.OrdinalIgnoreCase) < 0 &&
                    repo.Owner.IndexOf(searchFilter, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                bool isSelected = i == selectedRepoIndex;
                bool isInvalidPackage = repo.PackageJsonChecked && !repo.HasPackageJson;
                string statusText = repo.IsInstalled ? "(installed)" :
                    isInvalidPackage ? "(no package.json)" :
                    !repo.PackageJsonChecked ? "(checking...)" :
                    repo.IsPrivate ? "(private)" : string.Empty;

                Rect itemRect = GUILayoutUtility.GetRect(GUIContent.none, EditorStyles.label, GUILayout.ExpandWidth(true), GUILayout.Height(36));
                if (Event.current.type == EventType.Repaint && isSelected)
                    EditorGUI.DrawRect(itemRect, new Color(0.17f, 0.36f, 0.53f, 1f));

                Rect nameRect = new Rect(itemRect.x + 8, itemRect.y + 2, itemRect.width - 16, 16);
                var nameStyle = new GUIStyle(EditorStyles.label);
                if (isSelected)
                    nameStyle.normal.textColor = Color.white;
                else if (isInvalidPackage)
                    nameStyle.normal.textColor = new Color(0.5f, 0.5f, 0.5f);
                GUI.Label(nameRect, repo.Name, nameStyle);

                Rect statusRect = new Rect(itemRect.x + 8, itemRect.y + 18, itemRect.width - 16, 14);
                GUI.Label(statusRect, statusText, Styles.SubtitleLabel);

                if (Event.current.type == EventType.MouseDown && itemRect.Contains(Event.current.mousePosition))
                {
                    if (selectedRepoIndex != i)
                    {
                        selectedRepoIndex = i;
                        InitializeRepoDefaults(repo);
                    }

                    Event.current.Use();
                    Repaint();
                }
            }
        }

        private bool PassesFilter(GitHubRepo repo)
        {
            return currentFilter switch
            {
                FilterOption.ValidPackagesOnly => !repo.PackageJsonChecked || repo.HasPackageJson,
                FilterOption.PublicOnly => !repo.IsPrivate,
                FilterOption.PrivateOnly => repo.IsPrivate,
                _ => true
            };
        }

        private void DrawListFooter()
        {
            Rect footerRect = GUILayoutUtility.GetRect(1, 1, GUILayout.ExpandWidth(true));
            EditorGUI.DrawRect(footerRect, new Color(0.15f, 0.15f, 0.15f));

            EditorGUILayout.BeginHorizontal();
            string refreshText = lastRefreshDateTime != default
                ? $"Last refresh {lastRefreshDateTime:MMM d, HH:mm}"
                : "Not refreshed";
            GUILayout.Label(refreshText, Styles.FooterLabel);

            GUILayout.FlexibleSpace();

            if (GUILayout.Button(EditorGUIUtility.IconContent("Refresh"), EditorStyles.iconButton, GUILayout.Width(20), GUILayout.Height(20)))
                RefreshCurrentTab();

            EditorGUILayout.EndHorizontal();
        }
    }
}
