using System;
using UnityEditor;
using UnityEngine;

namespace Essentials.GitPackageManager.Editor
{
    public partial class GitPackageManagerWindow
    {
        private void DrawToolbar()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);

            Rect addButtonRect = GUILayoutUtility.GetRect(new GUIContent("+"), EditorStyles.toolbarDropDown, GUILayout.Width(24));
            if (EditorGUI.DropdownButton(addButtonRect, new GUIContent("+"), FocusType.Passive, EditorStyles.toolbarDropDown))
            {
                var menu = new GenericMenu();
                menu.AddItem(new GUIContent("Add Submodule..."), false, () => ShowAddFromUrlPopup(addButtonRect));
                menu.DropDown(addButtonRect);
            }

            GUILayout.Space(8);

            Tab previousTab = currentTab;
            if (GUILayout.Toggle(currentTab == Tab.Installed, "In Project", EditorStyles.toolbarButton))
                currentTab = Tab.Installed;
            if (GUILayout.Toggle(currentTab == Tab.Discover, "GitHub", EditorStyles.toolbarButton))
                currentTab = Tab.Discover;

            if (previousTab != currentTab)
            {
                RefreshCurrentTabIfStale();
                searchFilter = string.Empty;
                Repaint();
            }

            GUILayout.Space(8);

            if (currentTab == Tab.Discover)
            {
                string ownerLabel = string.IsNullOrEmpty(discoveryCoordinator.SelectedOwner)
                    ? "Owner"
                    : discoveryCoordinator.SelectedOwner;
                Rect ownerRect = GUILayoutUtility.GetRect(new GUIContent(ownerLabel), EditorStyles.toolbarDropDown, GUILayout.Width(140));
                if (EditorGUI.DropdownButton(ownerRect, new GUIContent(ownerLabel), FocusType.Passive, EditorStyles.toolbarDropDown))
                {
                    var menu = new GenericMenu();
                    string username = discoveryCoordinator.Username;
                    if (!string.IsNullOrEmpty(username))
                    {
                        bool isSelected = string.Equals(discoveryCoordinator.SelectedOwner, username, StringComparison.OrdinalIgnoreCase);
                        menu.AddItem(new GUIContent(username), isSelected, () => discoveryCoordinator.SetOwner(username));
                    }

                    if (discoveryCoordinator.Organizations.Count > 0)
                    {
                        menu.AddSeparator("");
                        foreach (string org in discoveryCoordinator.Organizations)
                        {
                            string orgCapture = org;
                            bool isSelected = string.Equals(discoveryCoordinator.SelectedOwner, org, StringComparison.OrdinalIgnoreCase);
                            menu.AddItem(new GUIContent(orgCapture), isSelected, () => discoveryCoordinator.SetOwner(orgCapture));
                        }
                    }
                    else if (!discoveryCoordinator.OrgsLoaded)
                    {
                        menu.AddDisabledItem(new GUIContent("Loading organizations..."));
                    }

                    menu.DropDown(ownerRect);
                }

                string filterLabel = GetFilterLabel();
                Rect filterRect = GUILayoutUtility.GetRect(new GUIContent(filterLabel), EditorStyles.toolbarDropDown, GUILayout.Width(120));
                if (EditorGUI.DropdownButton(filterRect, new GUIContent(filterLabel), FocusType.Passive, EditorStyles.toolbarDropDown))
                {
                    var menu = new GenericMenu();
                    menu.AddItem(new GUIContent("All Repositories"), currentFilter == FilterOption.All, () => { currentFilter = FilterOption.All; selectedRepoIndex = -1; Repaint(); });
                    menu.AddSeparator("");
                    menu.AddItem(new GUIContent("Public Only"), currentFilter == FilterOption.PublicOnly, () => { currentFilter = FilterOption.PublicOnly; selectedRepoIndex = -1; Repaint(); });
                    menu.AddItem(new GUIContent("Private Only"), currentFilter == FilterOption.PrivateOnly, () => { currentFilter = FilterOption.PrivateOnly; selectedRepoIndex = -1; Repaint(); });
                    menu.DropDown(filterRect);
                }

                string sortLabel = currentSort == SortOption.Name ? "Sort: Name" : "Sort: Updated";
                Rect sortRect = GUILayoutUtility.GetRect(new GUIContent(sortLabel), EditorStyles.toolbarDropDown, GUILayout.Width(100));
                if (EditorGUI.DropdownButton(sortRect, new GUIContent(sortLabel), FocusType.Passive, EditorStyles.toolbarDropDown))
                {
                    var menu = new GenericMenu();
                    menu.AddItem(new GUIContent("Name"), currentSort == SortOption.Name, () => { currentSort = SortOption.Name; SortRepos(); });
                    menu.AddItem(new GUIContent("Recently Updated"), currentSort == SortOption.RecentlyUpdated, () => { currentSort = SortOption.RecentlyUpdated; SortRepos(); });
                    menu.DropDown(sortRect);
                }
            }

            GUILayout.FlexibleSpace();

            Rect menuRect = GUILayoutUtility.GetRect(new GUIContent("..."), EditorStyles.toolbarButton, GUILayout.Width(24));
            if (GUI.Button(menuRect, ":", EditorStyles.toolbarButton))
            {
                var menu = new GenericMenu();
                menu.AddItem(new GUIContent("Refresh"), false, RefreshCurrentTab);
                menu.AddSeparator("");
                menu.AddDisabledItem(new GUIContent(string.IsNullOrWhiteSpace(gitVersion) ? "Git unavailable" : FirstLine(gitVersion)));
                menu.AddDisabledItem(new GUIContent(!ghAvailable ? "GitHub CLI not installed" : FirstLine(ghVersion)));
                menu.AddSeparator("");
                menu.AddItem(new GUIContent("Reset Window"), false, () =>
                {
                    selectedInstalledIndex = -1;
                    selectedRepoIndex = -1;
                    searchFilter = string.Empty;
                    currentFilter = FilterOption.All;
                    currentSort = SortOption.Name;
                    RefreshCurrentTab();
                });
                menu.DropDown(menuRect);
            }

            EditorGUILayout.EndHorizontal();
        }

        private string GetFilterLabel()
        {
            return currentFilter switch
            {
                FilterOption.PublicOnly => "Filter: Public",
                FilterOption.PrivateOnly => "Filter: Private",
                _ => "Filter: All"
            };
        }

        private void SortRepos()
        {
            var repos = discoveryCoordinator.DisplayedRepos;
            if (repos == null || repos.Count == 0)
                return;

            if (currentSort == SortOption.Name)
                repos.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            else
                repos.Sort((a, b) => string.Compare(b.UpdatedAt, a.UpdatedAt, StringComparison.Ordinal));

            selectedRepoIndex = -1;
            Repaint();
        }

        private static string FirstLine(string value)
        {
            int lineEnd = value.IndexOfAny(new[] { '\r', '\n' });
            return lineEnd < 0 ? value : value.Substring(0, lineEnd);
        }

        private bool DrawDependencyGate()
        {
            if (!gitAvailable)
            {
                EditorGUILayout.Space(20);
                DrawDependencyCard("Git", gitError, ToolKind.Git);
                if (!string.IsNullOrWhiteSpace(installStatus))
                    EditorGUILayout.HelpBox(installStatus, installStatusType);
                return false;
            }

            if (!ghAvailable && currentTab == Tab.Discover)
            {
                DrawGhInstallCard();
            }
            else if (ghAvailable && !ghAuthenticated && currentTab == Tab.Discover)
            {
                EditorGUILayout.HelpBox("GitHub CLI is not authenticated. Run 'gh auth login' in terminal to discover your repositories.", MessageType.Warning);
            }

            if (!string.IsNullOrWhiteSpace(installStatus))
                EditorGUILayout.HelpBox(installStatus, installStatusType);

            if (activeOperation != null)
            {
                EditorGUILayout.HelpBox(activeOperationLabel, MessageType.Info);
            }

            return true;
        }

        private void DrawDependencyCard(string title, string error, ToolKind tool)
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField($"{title} is required.", EditorStyles.boldLabel);
            if (!string.IsNullOrWhiteSpace(error))
                EditorGUILayout.HelpBox(error.Trim(), MessageType.Error);

            string hint = CliInstaller.GetInstallHint(tool);
            if (!string.IsNullOrWhiteSpace(hint))
            {
                EditorGUILayout.LabelField("Suggested install command:");
                EditorGUILayout.SelectableLabel(hint, EditorStyles.textField, GUILayout.Height(EditorGUIUtility.singleLineHeight));
            }

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Copy install command", GUILayout.Height(22)))
            {
                EditorGUIUtility.systemCopyBuffer = hint;
                installStatus = $"{title} install command copied to the clipboard.";
                installStatusType = MessageType.Info;
            }

            if (GUILayout.Button("Open download page", GUILayout.Height(22)))
                Application.OpenURL(CliInstaller.GetInstallUrl(tool));
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.EndVertical();
        }

        private void DrawGhInstallCard()
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField("GitHub CLI is not installed.", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("Install it to discover your repositories. You can still add packages manually via the + button.", EditorStyles.wordWrappedLabel);
            string hint = CliInstaller.GetInstallHint(ToolKind.GitHubCli);
            EditorGUILayout.SelectableLabel(hint, EditorStyles.textField, GUILayout.Height(EditorGUIUtility.singleLineHeight));
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Copy install command", GUILayout.Height(22)))
            {
                EditorGUIUtility.systemCopyBuffer = hint;
                installStatus = "GitHub CLI install command copied to the clipboard.";
                installStatusType = MessageType.Info;
            }

            if (GUILayout.Button("Open install guide", GUILayout.Height(22)))
                Application.OpenURL(CliInstaller.GetInstallUrl(ToolKind.GitHubCli));
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.EndVertical();
        }
    }
}
