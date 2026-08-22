using System;
using UnityEditor;
using UnityEngine;

namespace MartinCalander.GitSubmoduleManager.Editor
{
    internal partial class GitSubmoduleManagerView
    {
        private const string ValidPackageFilterTooltip =
            "Only show repositories whose root package.json declares a valid UPM package name and SemVer version.";

        internal static bool CanNavigatePackageTabs(bool gitAvailable)
        {
            return gitAvailable;
        }

        internal static bool CanUseToolbarGitActions(
            bool gitAvailable,
            bool isLoading,
            bool backgroundLoadsDraining)
        {
            return gitAvailable && !isLoading && !backgroundLoadsDraining;
        }

        internal static bool CanUseToolbarGitActions(
            bool gitAvailable,
            bool isInitialLoading,
            bool isGitStageReady,
            bool isInstalledLoading,
            bool backgroundLoadsDraining)
        {
            if (!gitAvailable || isInstalledLoading)
                return false;

            if (isInitialLoading)
                return isGitStageReady;

            return !backgroundLoadsDraining;
        }

        private void DrawToolbar()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);

            Rect addButtonRect = GUILayoutUtility.GetRect(new GUIContent("+"), EditorStyles.toolbarDropDown, GUILayout.Width(24));
            bool gitNavigationEnabled = CanNavigatePackageTabs(gitAvailable);
            bool gitActionsEnabled = CanUseToolbarGitActions(
                gitAvailable,
                isInitialLoading,
                initialGitStageReady,
                isInstalledLoading,
                AreBackgroundLoadsDraining);
            using (new EditorGUI.DisabledScope(!gitActionsEnabled || IsRepositoryOperationBusy))
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
            Tab requestedTab;
            using (new EditorGUI.DisabledScope(!gitNavigationEnabled))
            {
                bool installedSelected = GUILayout.Toggle(
                    currentTab == Tab.Installed,
                    "In Project",
                    EditorStyles.toolbarButton);
                bool discoverSelected = GUILayout.Toggle(
                    currentTab == Tab.Discover,
                    "GitHub",
                    EditorStyles.toolbarButton);
                requestedTab = ResolveRequestedTab(currentTab, installedSelected, discoverSelected);
            }

            if (previousTab != requestedTab)
            {
                if (previousTab == Tab.Installed)
                    installedSearchFilter = searchFilter;
                else
                    discoverSearchFilter = searchFilter;

                currentTab = requestedTab;
                listScroll = Vector2.zero;
                detailsScroll = Vector2.zero;
                searchFilter = currentTab == Tab.Installed
                    ? installedSearchFilter
                    : discoverSearchFilter;
                RefreshCurrentTabIfStale();
                Repaint();
            }

            GUILayout.Space(8);

            if (currentTab == Tab.Discover)
            {
                using (new EditorGUI.DisabledScope(
                           !ghAvailable || !ghAuthenticated || IsGitHubInteractionBusy))
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
                            menu.AddItem(
                                new GUIContent(username),
                                isSelected,
                                () => SelectDiscoveryOwner(username));
                        }

                        if (discoveryCoordinator.Organizations.Count > 0)
                        {
                            menu.AddSeparator("");
                            foreach (string org in discoveryCoordinator.Organizations)
                            {
                                string orgCapture = org;
                                bool isSelected = string.Equals(discoveryCoordinator.SelectedOwner, org, StringComparison.OrdinalIgnoreCase);
                                menu.AddItem(
                                    new GUIContent(orgCapture),
                                    isSelected,
                                    () => SelectDiscoveryOwner(orgCapture));
                            }
                        }
                        else if (!discoveryCoordinator.OrgsLoaded)
                        {
                            menu.AddDisabledItem(new GUIContent("Loading organizations..."));
                        }

                        menu.DropDown(ownerRect);
                    }

                    string filterLabel = GetFilterLabel();
                    string filterTooltip = currentFilter == FilterOption.ValidPackagesOnly
                        ? ValidPackageFilterTooltip
                        : "Filter the repositories shown on this page.";
                    var filterContent = new GUIContent(filterLabel, filterTooltip);
                    Rect filterRect = GUILayoutUtility.GetRect(filterContent, EditorStyles.toolbarDropDown, GUILayout.Width(120));
                    if (EditorGUI.DropdownButton(filterRect, filterContent, FocusType.Passive, EditorStyles.toolbarDropDown))
                    {
                        var menu = new GenericMenu();
                        menu.AddItem(new GUIContent("All Repositories"), currentFilter == FilterOption.All, () => SetFilter(FilterOption.All));
                        menu.AddSeparator("");
                        menu.AddItem(new GUIContent("Public Only"), currentFilter == FilterOption.PublicOnly, () => SetFilter(FilterOption.PublicOnly));
                        menu.AddItem(new GUIContent("Private Only"), currentFilter == FilterOption.PrivateOnly, () => SetFilter(FilterOption.PrivateOnly));
                        menu.AddSeparator("");
                        menu.AddItem(
                            new GUIContent("Valid UPM Packages", ValidPackageFilterTooltip),
                            currentFilter == FilterOption.ValidPackagesOnly,
                            () => SetFilter(FilterOption.ValidPackagesOnly));
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

            GUIContent menuContent = EditorGUIUtility.IconContent("_Menu");
            menuContent.tooltip = "More options";
            Rect menuRect = GUILayoutUtility.GetRect(menuContent, EditorStyles.toolbarButton, GUILayout.Width(24));
            if (GUI.Button(menuRect, menuContent, EditorStyles.toolbarButton))
            {
                var menu = new GenericMenu();
                bool canRefreshCurrentTab = gitActionsEnabled &&
                    (currentTab == Tab.Installed
                        ? !IsRepositoryOperationBusy
                        : !IsGitHubInteractionBusy);
                if (canRefreshCurrentTab)
                    menu.AddItem(new GUIContent("Refresh"), false, RefreshCurrentTab);
                else
                    menu.AddDisabledItem(new GUIContent("Refresh"));
                menu.AddSeparator("");
                menu.AddDisabledItem(new GUIContent(string.IsNullOrWhiteSpace(gitVersion) ? "Git unavailable" : FirstLine(gitVersion)));
                menu.AddDisabledItem(new GUIContent(!ghAvailable ? "GitHub CLI not installed" : FirstLine(ghVersion)));
                menu.AddSeparator("");
                menu.AddItem(new GUIContent("Welcome & Setup..."), false, ShowWelcomeScreen);
                menu.AddSeparator("");
                if (gitActionsEnabled)
                {
                    menu.AddItem(new GUIContent("Reset Window"), false, () =>
                    {
                        selectedInstalledIndex = -1;
                        selectedRepoIndex = -1;
                        listScroll = Vector2.zero;
                        detailsScroll = Vector2.zero;
                        searchFilter = string.Empty;
                        installedSearchFilter = string.Empty;
                        discoverSearchFilter = string.Empty;
                        SetFilter(FilterOption.All);
                        currentSort = SortOption.Name;
                        operationStatus = string.Empty;
                        discoverStatus = string.Empty;
                        discoverStatusType = MessageType.None;

                        if (ghAvailable && ghAuthenticated)
                        {
                            discoveryCoordinator.EnsureUsername();
                            discoveryCoordinator.LoadInitialPage();
                        }
                        else
                        {
                            discoveryCoordinator.Dispose();
                        }

                        if (currentTab == Tab.Installed)
                            RefreshInstalled();
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

        internal static Tab ResolveRequestedTab(Tab current, bool installedSelected, bool discoverSelected)
        {
            // On a tab click, IMGUI can report both the old and new toggles as selected.
            if (current != Tab.Installed && installedSelected)
                return Tab.Installed;
            if (current != Tab.Discover && discoverSelected)
                return Tab.Discover;

            return current;
        }

        private string GetFilterLabel()
        {
            return currentFilter switch
            {
                FilterOption.PublicOnly => "Filter: Public",
                FilterOption.PrivateOnly => "Filter: Private",
                FilterOption.ValidPackagesOnly => "Filter: UPM",
                _ => "Filter: All"
            };
        }

        private void SetFilter(FilterOption filter)
        {
            currentFilter = filter;
            listScroll = Vector2.zero;
            discoveryCoordinator.SetValidPackageFilterEnabled(filter == FilterOption.ValidPackagesOnly);

            var repos = discoveryCoordinator.DisplayedRepos;
            if (selectedRepoIndex >= 0 &&
                (repos == null || selectedRepoIndex >= repos.Count || !PassesFilter(repos[selectedRepoIndex])))
            {
                selectedRepoIndex = -1;
                detailsScroll = Vector2.zero;
            }

            Repaint();
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
            listScroll = Vector2.zero;
            detailsScroll = Vector2.zero;
            Repaint();
        }

        private void SelectDiscoveryOwner(string owner)
        {
            selectedRepoIndex = -1;
            listScroll = Vector2.zero;
            detailsScroll = Vector2.zero;
            discoveryCoordinator.SetOwner(owner, discoverSearchFilter);
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

            if (GitOperationService.IsBusy)
                EditorGUILayout.HelpBox(GitOperationService.ActiveLabel, MessageType.Info);

            if (PackageManagerProjectResolutionService.IsBusy)
            {
                EditorGUILayout.HelpBox(
                    PackageManagerProjectResolutionService.BuildUnavailableMessage(),
                    MessageType.Info);
            }

            string recoveryWarning = GitOperationService.RecoveryWarning;
            if (!string.IsNullOrWhiteSpace(recoveryWarning))
            {
                EditorGUILayout.HelpBox(recoveryWarning, MessageType.Error);
                if (GUILayout.Button("I reviewed the repository state", GUILayout.Height(20)))
                {
                    if (GitOperationService.TryAcknowledgeRecoveryWarning(out string recoveryError))
                    {
                        if (string.Equals(operationStatus, recoveryWarning, StringComparison.Ordinal))
                            operationStatus = string.Empty;
                    }
                    else
                    {
                        operationStatus = recoveryError;
                        operationStatusType = MessageType.Error;
                    }
                }
            }
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

            using (new EditorGUI.DisabledScope(IsGitHubInteractionBusy))
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

            using (new EditorGUI.DisabledScope(IsGitHubInteractionBusy))
            {
                if (GUILayout.Button("Check again", GUILayout.Height(22)))
                    CheckDependenciesAgain(true);
            }
            EditorGUILayout.EndHorizontal();

            if (!plan.CanRunAutomatically && !string.IsNullOrWhiteSpace(plan.AutomaticInstallUnavailableReason))
                EditorGUILayout.LabelField(plan.AutomaticInstallUnavailableReason, EditorStyles.wordWrappedMiniLabel);

            EditorGUILayout.EndVertical();
        }

        private void DrawGhAuthenticationCard()
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField("GitHub CLI needs authentication.", EditorStyles.boldLabel);
            EditorGUILayout.LabelField(
                "Authenticate with github.com before loading repositories. Manual submodule installation with the + button still uses Git only.",
                EditorStyles.wordWrappedLabel);
            if (!string.IsNullOrWhiteSpace(ghAuthError))
            {
                string safeError = GitUtility.RedactCredentials(ghAuthError.Trim());
                EditorGUILayout.HelpBox(FirstLine(safeError), MessageType.Warning);
            }
            EditorGUILayout.SelectableLabel(
                GitHubUtility.AuthenticationTerminalDisplayCommand,
                EditorStyles.textField,
                GUILayout.Height(EditorGUIUtility.singleLineHeight));

            if (IsGhAuthenticationInProgress)
            {
                DrawLoadingState(
                    "Waiting for GitHub authentication...",
                    "Wait for GitHub CLI to copy the code, then paste it on GitHub's device page. If no code appears, cancel; after cancellation finishes, use the terminal command. Unity will warn if a restart is required first.",
                    topSpacing: 2f);
                if (GUILayout.Button("Open GitHub device page", GUILayout.Height(22)))
                    TryOpenGitHubAuthenticationDevicePage();
                if (GUILayout.Button("Cancel authentication", GUILayout.Height(22)))
                    CancelGitHubAuthentication();
                EditorGUILayout.EndVertical();
                return;
            }

            if (DrawGitHubAuthenticationLifecycleNotice())
            {
                if (GUILayout.Button("Check authentication again", GUILayout.Height(22)))
                    CheckDependenciesAgain(true);
                EditorGUILayout.EndVertical();
                return;
            }

            if (!GitHubUtility.SupportsClipboardAuthentication(ghVersion))
            {
                EditorGUILayout.HelpBox(
                    "One-click authentication requires GitHub CLI 2.79.0 or newer. Update GitHub CLI, or copy the command above and run it in a visible terminal.",
                    MessageType.Warning);
                EditorGUILayout.BeginHorizontal();
                if (GUILayout.Button("Copy terminal command", GUILayout.Height(22)))
                {
                    EditorGUIUtility.systemCopyBuffer = GitHubUtility.AuthenticationTerminalDisplayCommand;
                    installStatus = "Compatible GitHub CLI authentication command copied to the clipboard.";
                    installStatusType = MessageType.Info;
                }
                if (GUILayout.Button("Open update guide", GUILayout.Height(22)))
                    Application.OpenURL(CliInstaller.GetInstallUrl(ToolKind.GitHubCli));
                if (GUILayout.Button("Check again", GUILayout.Height(22)))
                    CheckDependenciesAgain(true);
                EditorGUILayout.EndHorizontal();
                EditorGUILayout.EndVertical();
                return;
            }

            EditorGUILayout.BeginHorizontal();
            using (new EditorGUI.DisabledScope(IsGitHubInteractionBusy))
            {
                if (GUILayout.Button("Authenticate with GitHub...", GUILayout.Height(22)))
                    StartGitHubAuthentication();
            }

            if (GUILayout.Button("Copy auth command", GUILayout.Height(22)))
            {
                EditorGUIUtility.systemCopyBuffer = GitHubUtility.AuthenticationTerminalDisplayCommand;
                installStatus = "GitHub CLI authentication command copied to the clipboard.";
                installStatusType = MessageType.Info;
            }
            if (GUILayout.Button("Open authentication guide", GUILayout.Height(22)))
                Application.OpenURL(GitHubUtility.AuthenticationGuideUrl);
            using (new EditorGUI.DisabledScope(IsGitHubInteractionBusy))
            {
                if (GUILayout.Button("Check again", GUILayout.Height(22)))
                    CheckDependenciesAgain(true);
            }
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.EndVertical();
        }

        private void DrawInstallButton(ToolKind tool, string displayName, CliInstallPlan plan)
        {
            if (!plan.CanRunAutomatically)
                return;

            bool canStart = CanStartCliInstall(
                IsGitHubInteractionBusy,
                AreBackgroundLoadsDraining,
                !string.IsNullOrWhiteSpace(GitOperationService.RecoveryWarning));
            using (new EditorGUI.DisabledScope(!canStart))
            {
                string label = !cliInstallInProgress ? $"Install {displayName}..." : "Installing...";
                if (GUILayout.Button(label, GUILayout.Height(22)))
                    StartCliInstall(tool, displayName);
            }
        }

        private void CheckDependenciesAgain(bool includeGitHub = false)
        {
            dependencyCheckIncludesGitHub |= includeGitHub;
            installStatus = "Checking command-line tools...";
            installStatusType = MessageType.Info;
            BeginBackgroundLoad(true);
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
