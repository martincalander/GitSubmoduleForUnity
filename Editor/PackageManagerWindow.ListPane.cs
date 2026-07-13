using System;
using UnityEditor;
using UnityEngine;

namespace MartinCalander.GitPackageManager.Editor
{
    public partial class GitPackageManagerWindow
    {
        private void DrawListPane()
        {
            EditorGUILayout.BeginVertical(GUILayout.Width(ListPaneWidth));

            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
            EditorGUI.BeginChangeCheck();
            searchFilter = EditorGUILayout.TextField(searchFilter, EditorStyles.toolbarSearchField);
            if (EditorGUI.EndChangeCheck() && currentTab == Tab.Discover)
            {
                discoveryCoordinator.SetSearchQuery(searchFilter, EditorApplication.timeSinceStartup);
            }
            if (GUILayout.Button(string.Empty, GUI.skin.FindStyle("ToolbarSearchCancelButton") ?? EditorStyles.toolbarButton, GUILayout.Width(18)))
            {
                searchFilter = string.Empty;
                GUI.FocusControl(null);
                if (currentTab == Tab.Discover)
                {
                    discoveryCoordinator.SetSearchQuery(string.Empty, EditorApplication.timeSinceStartup);
                }
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            GUILayout.Label(currentTab == Tab.Installed ? "Packages" : "Repositories", Styles.HeaderLabel);
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();

            Rect lineRect = GUILayoutUtility.GetRect(1, 1, GUILayout.ExpandWidth(true));
            EditorGUI.DrawRect(lineRect, Styles.SeparatorColor);

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

            if (installedPackages == null || installedPackages.Count == 0)
            {
                GUILayout.Label("No packages installed via git.", EditorStyles.centeredGreyMiniLabel);
                return;
            }

            for (int i = 0; i < installedPackages.Count; i++)
            {
                GitPackageInfo package = installedPackages[i];
                string displayName = string.IsNullOrWhiteSpace(package.PackageName) ? package.Name : package.PackageName;
                if (!string.IsNullOrWhiteSpace(searchFilter) &&
                    displayName.IndexOf(searchFilter, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                bool isSelected = i == selectedInstalledIndex;
                string badge = package.IsInitialized ? "git" : "!";
                string versionText = !string.IsNullOrWhiteSpace(package.Branch) ? package.Branch : "main";
                Rect itemRect = GUILayoutUtility.GetRect(GUIContent.none, EditorStyles.label, GUILayout.ExpandWidth(true), GUILayout.Height(24));

                if (Event.current.type == EventType.Repaint && isSelected)
                    EditorGUI.DrawRect(itemRect, Styles.SelectionColor);

                Rect badgeRect = new Rect(itemRect.x + 4, itemRect.y + 4, 28, itemRect.height - 8);
                GUI.Label(badgeRect, badge, Styles.SubtitleLabel);

                Rect nameRect = new Rect(itemRect.x + 34, itemRect.y + 4, itemRect.width - 96, itemRect.height - 8);
                GUIStyle nameStyle = isSelected ? Styles.SelectedListItemLabel : Styles.ListItemLabel;
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
            if (discoveryCoordinator.IsLoading)
            {
                EditorGUILayout.Space(20);
                EditorGUILayout.LabelField(discoveryCoordinator.StatusMessage, EditorStyles.centeredGreyMiniLabel);
                Rect progressRect = GUILayoutUtility.GetRect(0, 4, GUILayout.ExpandWidth(true));
                EditorGUI.ProgressBar(progressRect, 0.5f, string.Empty);
                return;
            }

            if (!string.IsNullOrWhiteSpace(discoverStatus))
            {
                EditorGUILayout.HelpBox(discoverStatus, discoverStatusType);
                return;
            }

            if (!string.IsNullOrWhiteSpace(discoveryCoordinator.ErrorMessage))
            {
                EditorGUILayout.HelpBox(discoveryCoordinator.ErrorMessage, MessageType.Error);
                return;
            }

            var availableRepos = discoveryCoordinator.DisplayedRepos;
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

                bool isSelected = i == selectedRepoIndex;
                string statusText = repo.IsInstalled ? "(installed)" :
                    repo.IsPrivate ? "(private)" : string.Empty;

                Rect itemRect = GUILayoutUtility.GetRect(GUIContent.none, EditorStyles.label, GUILayout.ExpandWidth(true), GUILayout.Height(36));
                if (Event.current.type == EventType.Repaint && isSelected)
                    EditorGUI.DrawRect(itemRect, Styles.SelectionColor);

                Rect nameRect = new Rect(itemRect.x + 8, itemRect.y + 2, itemRect.width - 16, 16);
                GUIStyle nameStyle = isSelected ? Styles.SelectedListItemLabel : Styles.ListItemLabel;
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
                FilterOption.PublicOnly => !repo.IsPrivate,
                FilterOption.PrivateOnly => repo.IsPrivate,
                _ => true
            };
        }

        private void DrawListFooter()
        {
            Rect footerRect = GUILayoutUtility.GetRect(1, 1, GUILayout.ExpandWidth(true));
            EditorGUI.DrawRect(footerRect, Styles.SeparatorColor);

            EditorGUILayout.BeginHorizontal();

            if (currentTab == Tab.Discover)
            {
                using (new EditorGUI.DisabledScope(!discoveryCoordinator.HasPrevPage || discoveryCoordinator.IsLoading))
                {
                    if (GUILayout.Button("< Prev", EditorStyles.miniButtonLeft, GUILayout.Width(50)))
                        discoveryCoordinator.PrevPage();
                }

                GUILayout.Label($"Page {discoveryCoordinator.CurrentPage}", Styles.FooterLabel, GUILayout.Width(50));

                using (new EditorGUI.DisabledScope(!discoveryCoordinator.HasNextPage || discoveryCoordinator.IsLoading))
                {
                    if (GUILayout.Button("Next >", EditorStyles.miniButtonRight, GUILayout.Width(50)))
                        discoveryCoordinator.NextPage();
                }

                GUILayout.FlexibleSpace();
            }
            else
            {
                string refreshText = lastRefreshDateTime != default
                    ? $"Last refresh {lastRefreshDateTime:MMM d, HH:mm}"
                    : "Not refreshed";
                GUILayout.Label(refreshText, Styles.FooterLabel);
                GUILayout.FlexibleSpace();
            }

            if (GUILayout.Button(EditorGUIUtility.IconContent("Refresh"), EditorStyles.iconButton, GUILayout.Width(20), GUILayout.Height(20)))
                RefreshCurrentTab();

            EditorGUILayout.EndHorizontal();
        }
    }
}
