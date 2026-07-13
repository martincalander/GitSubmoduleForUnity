using System;
using UnityEditor;
using UnityEngine;

namespace MartinCalander.GitPackageManager.Editor
{
    public partial class GitPackageManagerWindow
    {
        private void DrawToolbar()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);

            Rect addButtonRect = GUILayoutUtility.GetRect(new GUIContent("+"), EditorStyles.toolbarDropDown, GUILayout.Width(24));
            bool gitUiEnabled = !isInitialLoading && gitAvailable;
            using (new EditorGUI.DisabledScope(!gitUiEnabled || activeOperation != null))
            {
                if (EditorGUI.DropdownButton(addButtonRect, new GUIContent("+", "Add a Git submodule manually"), FocusType.Passive, EditorStyles.toolbarDropDown))
                {
                    var menu = new GenericMenu();
                    menu.AddItem(new GUIContent("Add Submodule..."), false, () => ShowAddFromUrlPopup(addButtonRect));
                    menu.DropDown(addButtonRect);
                }
            }

            GUILayout.Space(8);

            Tab previousTab = currentTab;
            using (new EditorGUI.DisabledScope(!gitUiEnabled))
            {
                if (GUILayout.Toggle(currentTab == Tab.Installed, "In Project", EditorStyles.toolbarButton))
                    currentTab = Tab.Installed;
                if (GUILayout.Toggle(currentTab == Tab.Discover, "GitHub", EditorStyles.toolbarButton))
                    currentTab = Tab.Discover;
            }

            if (previousTab != currentTab)
            {
                RefreshCurrentTabIfStale();
                searchFilter = string.Empty;
                Repaint();
            }

            GUILayout.Space(8);

            if (currentTab == Tab.Discover)
            {
                using (new EditorGUI.DisabledScope(!ghAvailable || !ghAuthenticated))
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
            }

            GUILayout.FlexibleSpace();

            Rect menuRect = GUILayoutUtility.GetRect(new GUIContent("..."), EditorStyles.toolbarButton, GUILayout.Width(24));
            if (GUI.Button(menuRect, ":", EditorStyles.toolbarButton))
            {
                var menu = new GenericMenu();
                if (gitUiEnabled)
                    menu.AddItem(new GUIContent("Refresh"), false, RefreshCurrentTab);
                else
                    menu.AddDisabledItem(new GUIContent("Refresh"));
                menu.AddSeparator("");
                menu.AddDisabledItem(new GUIContent(string.IsNullOrWhiteSpace(gitVersion) ? "Git unavailable" : FirstLine(gitVersion)));
                menu.AddDisabledItem(new GUIContent(!ghAvailable ? "GitHub CLI not installed" : FirstLine(ghVersion)));
                menu.AddSeparator("");
                if (gitUiEnabled)
                {
                    menu.AddItem(new GUIContent("Reset Window"), false, () =>
                    {
                        selectedInstalledIndex = -1;
                        selectedRepoIndex = -1;
                        searchFilter = string.Empty;
                        currentFilter = FilterOption.All;
                        currentSort = SortOption.Name;
                        operationStatus = string.Empty;
                        RefreshCurrentTab();
                    });
                }
                else
                {
                    menu.AddDisabledItem(new GUIContent("Reset Window"));
                }
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
            DependencyGateState gateState = GetDependencyGateState(
                gitAvailable,
                ghAvailable,
                ghAuthenticated,
                currentTab == Tab.Discover);

            if (gateState == DependencyGateState.GitMissing)
            {
                EditorGUILayout.Space(20);
                DrawDependencyCard("Git", gitError, ToolKind.Git);
                DrawDependencyMessages();
                return false;
            }

            if (gateState == DependencyGateState.GitHubCliMissing)
            {
                DrawGhInstallCard();
                DrawDependencyMessages();
                return false;
            }
            if (gateState == DependencyGateState.GitHubAuthenticationMissing)
            {
                DrawGhAuthenticationCard();
                DrawDependencyMessages();
                return false;
            }

            DrawDependencyMessages();
            return true;
        }

        internal static DependencyGateState GetDependencyGateState(
            bool isGitAvailable,
            bool isGhAvailable,
            bool isGhAuthenticated,
            bool isDiscoverTab)
        {
            if (!isGitAvailable)
                return DependencyGateState.GitMissing;
            if (!isDiscoverTab)
                return DependencyGateState.Ready;
            if (!isGhAvailable)
                return DependencyGateState.GitHubCliMissing;
            return isGhAuthenticated
                ? DependencyGateState.Ready
                : DependencyGateState.GitHubAuthenticationMissing;
        }

        private void DrawDependencyMessages()
        {
            if (!string.IsNullOrWhiteSpace(installStatus))
                EditorGUILayout.HelpBox(installStatus, installStatusType);

            if (!string.IsNullOrWhiteSpace(operationStatus))
                EditorGUILayout.HelpBox(operationStatus, operationStatusType);

            if (activeOperation != null)
                EditorGUILayout.HelpBox(activeOperationLabel, MessageType.Info);
        }

        private void DrawDependencyCard(string title, string error, ToolKind tool)
        {
            CliInstallPlan plan = CliInstaller.GetInstallPlan(tool);
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField($"{title} is required.", EditorStyles.boldLabel);
            if (!string.IsNullOrWhiteSpace(error))
                EditorGUILayout.HelpBox(error.Trim(), MessageType.Error);

            string hint = plan.DisplayCommand;
            if (plan.CanCopyCommand && !string.IsNullOrWhiteSpace(hint))
            {
                EditorGUILayout.LabelField("Suggested install command:");
                EditorGUILayout.SelectableLabel(hint, EditorStyles.textField, GUILayout.Height(EditorGUIUtility.singleLineHeight));
            }

            EditorGUILayout.BeginHorizontal();
            DrawInstallButton(tool, title, plan);

            if (plan.CanCopyCommand && GUILayout.Button("Copy install command", GUILayout.Height(22)))
            {
                EditorGUIUtility.systemCopyBuffer = hint;
                installStatus = $"{title} install command copied to the clipboard.";
                installStatusType = MessageType.Info;
            }

            if (GUILayout.Button("Open download page", GUILayout.Height(22)))
                Application.OpenURL(plan.InstallUrl);

            using (new EditorGUI.DisabledScope(cliInstallOperation != null || activeOperation != null))
            {
                if (GUILayout.Button("Check again", GUILayout.Height(22)))
                    CheckDependenciesAgain();
            }
            EditorGUILayout.EndHorizontal();

            if (!plan.CanRunAutomatically && !string.IsNullOrWhiteSpace(plan.AutomaticInstallUnavailableReason))
                EditorGUILayout.LabelField(plan.AutomaticInstallUnavailableReason, EditorStyles.wordWrappedMiniLabel);

            EditorGUILayout.EndVertical();
        }

        private void DrawGhInstallCard()
        {
            CliInstallPlan plan = CliInstaller.GetInstallPlan(ToolKind.GitHubCli);
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField("GitHub CLI is not installed.", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("Install it to discover your repositories. You can still add packages manually via the + button.", EditorStyles.wordWrappedLabel);
            if (!string.IsNullOrWhiteSpace(ghError))
                EditorGUILayout.HelpBox(GitUtility.RedactCredentials(ghError.Trim()), MessageType.Warning);
            string hint = plan.DisplayCommand;
            if (plan.CanCopyCommand && !string.IsNullOrWhiteSpace(hint))
                EditorGUILayout.SelectableLabel(hint, EditorStyles.textField, GUILayout.Height(EditorGUIUtility.singleLineHeight));
            EditorGUILayout.BeginHorizontal();
            DrawInstallButton(ToolKind.GitHubCli, "GitHub CLI", plan);

            if (plan.CanCopyCommand && GUILayout.Button("Copy install command", GUILayout.Height(22)))
            {
                EditorGUIUtility.systemCopyBuffer = hint;
                installStatus = "GitHub CLI install command copied to the clipboard.";
                installStatusType = MessageType.Info;
            }

            if (GUILayout.Button("Open install guide", GUILayout.Height(22)))
                Application.OpenURL(plan.InstallUrl);

            using (new EditorGUI.DisabledScope(cliInstallOperation != null || activeOperation != null))
            {
                if (GUILayout.Button("Check again", GUILayout.Height(22)))
                    CheckDependenciesAgain();
            }
            EditorGUILayout.EndHorizontal();

            if (!plan.CanRunAutomatically && !string.IsNullOrWhiteSpace(plan.AutomaticInstallUnavailableReason))
                EditorGUILayout.LabelField(plan.AutomaticInstallUnavailableReason, EditorStyles.wordWrappedMiniLabel);

            EditorGUILayout.EndVertical();
        }

        private void DrawGhAuthenticationCard()
        {
            const string command = "gh auth login";
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField("GitHub CLI needs authentication.", EditorStyles.boldLabel);
            EditorGUILayout.LabelField(
                "Authenticate in a normal terminal before loading repositories. Manual submodule installation with the + button still uses Git only.",
                EditorStyles.wordWrappedLabel);
            if (!string.IsNullOrWhiteSpace(ghAuthError))
                EditorGUILayout.HelpBox(FirstLine(ghAuthError.Trim()), MessageType.Warning);
            EditorGUILayout.SelectableLabel(command, EditorStyles.textField, GUILayout.Height(EditorGUIUtility.singleLineHeight));

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Copy auth command", GUILayout.Height(22)))
            {
                EditorGUIUtility.systemCopyBuffer = command;
                installStatus = "GitHub CLI authentication command copied to the clipboard.";
                installStatusType = MessageType.Info;
            }

            if (GUILayout.Button("Open authentication guide", GUILayout.Height(22)))
                Application.OpenURL("https://cli.github.com/manual/gh_auth_login");
            if (GUILayout.Button("Check again", GUILayout.Height(22)))
                CheckDependenciesAgain();
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.EndVertical();
        }

        private void DrawInstallButton(ToolKind tool, string displayName, CliInstallPlan plan)
        {
            if (!plan.CanRunAutomatically)
                return;

            using (new EditorGUI.DisabledScope(cliInstallOperation != null || activeOperation != null))
            {
                string label = cliInstallOperation == null ? $"Install {displayName}..." : "Installing...";
                if (GUILayout.Button(label, GUILayout.Height(22)))
                    StartCliInstall(tool, displayName);
            }
        }

        private void CheckDependenciesAgain()
        {
            RefreshDependencies();
            if (!gitAvailable)
            {
                installStatus = "Git is still unavailable. Review the installer error or use the official download page.";
                installStatusType = MessageType.Warning;
            }
            else if (currentTab == Tab.Discover && !ghAvailable)
            {
                installStatus = "GitHub CLI is still unavailable. Manual installation through the + button remains available.";
                installStatusType = MessageType.Warning;
            }
            else if (currentTab == Tab.Discover && !ghAuthenticated)
            {
                installStatus = "GitHub CLI is installed but not authenticated. Run 'gh auth login' in a terminal.";
                installStatusType = MessageType.Warning;
            }
            else
            {
                installStatus = "Command-line tools checked successfully.";
                installStatusType = MessageType.Info;
                RefreshCurrentTab();
            }

            Repaint();
        }
    }

    internal enum DependencyGateState
    {
        Ready,
        GitMissing,
        GitHubCliMissing,
        GitHubAuthenticationMissing
    }
}
