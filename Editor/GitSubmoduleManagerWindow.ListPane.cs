using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace MartinCalander.GitSubmoduleManager.Editor
{
    public partial class GitSubmoduleManagerWindow
    {
        private void DrawListPane()
        {
            EditorGUILayout.BeginVertical(GUILayout.Width(ListPaneWidth));

            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
            EditorGUI.BeginChangeCheck();
            searchFilter = EditorGUILayout.TextField(searchFilter, EditorStyles.toolbarSearchField);
            if (EditorGUI.EndChangeCheck())
            {
                listScroll = Vector2.zero;
                if (currentTab == Tab.Discover)
                {
                    discoverSearchFilter = searchFilter;
                    discoveryCoordinator.SetSearchQuery(searchFilter, EditorApplication.timeSinceStartup);
                }
                else
                {
                    installedSearchFilter = searchFilter;
                }
            }
            if (GUILayout.Button(string.Empty, GUI.skin.FindStyle("ToolbarSearchCancelButton") ?? EditorStyles.toolbarButton, GUILayout.Width(18)))
            {
                searchFilter = string.Empty;
                listScroll = Vector2.zero;
                GUI.FocusControl(null);
                if (currentTab == Tab.Discover)
                {
                    discoverSearchFilter = string.Empty;
                    discoveryCoordinator.SetSearchQuery(string.Empty, EditorApplication.timeSinceStartup);
                }
                else
                {
                    installedSearchFilter = string.Empty;
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
            if (isInstalledLoading)
            {
                DrawLoadingState(
                    "Loading project packages...",
                    "Refreshing installed package submodules.",
                    topSpacing: 18f);
                return;
            }

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

            var visiblePackageIndices = new List<int>(installedPackages.Count);
            for (int i = 0; i < installedPackages.Count; i++)
            {
                GitPackageInfo package = installedPackages[i];
                string displayName = string.IsNullOrWhiteSpace(package.PackageName) ? package.Name : package.PackageName;
                if (!string.IsNullOrWhiteSpace(searchFilter) &&
                    displayName.IndexOf(searchFilter, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                visiblePackageIndices.Add(i);
            }

            const float rowHeight = 24f;
            GetVisibleRowRange(visiblePackageIndices.Count, rowHeight, out int firstRow, out int lastRow);
            if (firstRow > 0)
                GUILayout.Space(firstRow * rowHeight);

            for (int row = firstRow; row < lastRow; row++)
            {
                int i = visiblePackageIndices[row];
                GitPackageInfo package = installedPackages[i];
                string displayName = string.IsNullOrWhiteSpace(package.PackageName) ? package.Name : package.PackageName;

                bool isSelected = i == selectedInstalledIndex;
                string badge = package.IsInitialized ? "git" : "!";
                string versionText = GetInstalledBranchLabel(package.Branch);
                Rect itemRect = GUILayoutUtility.GetRect(GUIContent.none, EditorStyles.label, GUILayout.ExpandWidth(true), GUILayout.Height(24));

                if (Event.current.type == EventType.Repaint && isSelected)
                    EditorGUI.DrawRect(itemRect, Styles.SelectionColor);

                GUIStyle secondaryStyle = isSelected
                    ? Styles.SelectedSubtitleLabel
                    : Styles.SubtitleLabel;
                Rect badgeRect = new Rect(itemRect.x + 4, itemRect.y + 4, 28, itemRect.height - 8);
                GUI.Label(badgeRect, badge, secondaryStyle);

                const float branchLabelWidth = 112f;
                Rect nameRect = new Rect(
                    itemRect.x + 34,
                    itemRect.y + 4,
                    itemRect.width - branchLabelWidth - 46,
                    itemRect.height - 8);
                GUIStyle nameStyle = isSelected ? Styles.SelectedListItemLabel : Styles.ListItemLabel;
                GUI.Label(nameRect, displayName, nameStyle);

                Rect versionRect = new Rect(
                    itemRect.xMax - branchLabelWidth - 8,
                    itemRect.y + 4,
                    branchLabelWidth,
                    itemRect.height - 8);
                GUI.Label(versionRect, new GUIContent(versionText, versionText), secondaryStyle);

                if (Event.current.type == EventType.MouseDown && itemRect.Contains(Event.current.mousePosition))
                {
                    if (selectedInstalledIndex != i)
                        detailsScroll = Vector2.zero;
                    selectedInstalledIndex = i;
                    Event.current.Use();
                    Repaint();
                }
            }

            if (lastRow < visiblePackageIndices.Count)
                GUILayout.Space((visiblePackageIndices.Count - lastRow) * rowHeight);
        }

        private void DrawDiscoverList()
        {
            string drainStatus = AsyncCommandDrainRegistry.StatusMessage;
            if (!string.IsNullOrWhiteSpace(drainStatus))
            {
                bool requiresRestart = AsyncCommandDrainRegistry.RequiresEditorRestart;
                EditorGUILayout.HelpBox(
                    drainStatus,
                    GetDiscoveryDrainStatusType(requiresRestart));
                if (requiresRestart)
                    return;
            }

            if (discoveryCoordinator.IsLoading)
            {
                DrawLoadingState(
                    "Loading repositories...",
                    string.IsNullOrWhiteSpace(discoveryCoordinator.StatusMessage)
                        ? $"Fetching page {discoveryCoordinator.CurrentPage} from GitHub."
                        : discoveryCoordinator.StatusMessage,
                    topSpacing: 18f);
                return;
            }

            if (!string.IsNullOrWhiteSpace(discoverStatus))
            {
                EditorGUILayout.HelpBox(discoverStatus, discoverStatusType);
                return;
            }

            if (!string.IsNullOrWhiteSpace(discoveryCoordinator.WarningMessage))
                EditorGUILayout.HelpBox(discoveryCoordinator.WarningMessage, MessageType.Warning);

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

            var visibleRepositoryIndices = new List<int>(availableRepos.Count);
            for (int i = 0; i < availableRepos.Count; i++)
            {
                GitHubRepo repo = availableRepos[i];
                if (!PassesFilter(repo))
                    continue;

                visibleRepositoryIndices.Add(i);
            }

            if (selectedRepoIndex >= 0 &&
                (selectedRepoIndex >= availableRepos.Count || !PassesFilter(availableRepos[selectedRepoIndex])))
            {
                selectedRepoIndex = -1;
            }

            if (currentFilter == FilterOption.ValidPackagesOnly)
                DrawPackageManifestFilterStatus(visibleRepositoryIndices.Count);

            if (visibleRepositoryIndices.Count == 0)
                return;

            const float rowHeight = 36f;
            GetVisibleRowRange(visibleRepositoryIndices.Count, rowHeight, out int firstRow, out int lastRow);
            if (firstRow > 0)
                GUILayout.Space(firstRow * rowHeight);

            for (int row = firstRow; row < lastRow; row++)
            {
                int i = visibleRepositoryIndices[row];
                GitHubRepo repo = availableRepos[i];

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
                GUI.Label(
                    statusRect,
                    statusText,
                    isSelected ? Styles.SelectedSubtitleLabel : Styles.SubtitleLabel);

                if (Event.current.type == EventType.MouseDown && itemRect.Contains(Event.current.mousePosition))
                {
                    if (selectedRepoIndex != i)
                    {
                        detailsScroll = Vector2.zero;
                        selectedRepoIndex = i;
                        InitializeRepoDefaults(repo);
                    }

                    Event.current.Use();
                    Repaint();
                }
            }

            if (lastRow < visibleRepositoryIndices.Count)
                GUILayout.Space((visibleRepositoryIndices.Count - lastRow) * rowHeight);
        }

        internal static MessageType GetDiscoveryDrainStatusType(bool requiresEditorRestart)
        {
            return requiresEditorRestart ? MessageType.Error : MessageType.Warning;
        }

        private void GetVisibleRowRange(int rowCount, float rowHeight, out int firstRow, out int lastRow)
        {
            float viewportHeight = Mathf.Max(120f, position.height - 112f);
            listScroll.y = ClampVirtualizedScrollOffset(listScroll.y, rowCount, rowHeight);
            CalculateVisibleRowRange(
                listScroll.y,
                rowCount,
                rowHeight,
                viewportHeight,
                out firstRow,
                out lastRow);
        }

        internal static float ClampVirtualizedScrollOffset(float scrollOffset, int rowCount, float rowHeight)
        {
            if (float.IsNaN(scrollOffset) || float.IsInfinity(scrollOffset) ||
                rowCount <= 0 || rowHeight <= 0f)
            {
                return 0f;
            }

            return Mathf.Clamp(scrollOffset, 0f, rowCount * rowHeight);
        }

        internal static void CalculateVisibleRowRange(
            float scrollOffset,
            int rowCount,
            float rowHeight,
            float viewportHeight,
            out int firstRow,
            out int lastRow)
        {
            if (rowCount <= 0 || rowHeight <= 0f || viewportHeight <= 0f)
            {
                firstRow = 0;
                lastRow = 0;
                return;
            }

            float safeOffset = ClampVirtualizedScrollOffset(scrollOffset, rowCount, rowHeight);
            int visibleCount = Mathf.Min(
                rowCount,
                Mathf.CeilToInt(viewportHeight / rowHeight) + 3);
            int requestedFirstRow = Mathf.FloorToInt(safeOffset / rowHeight) - 1;
            int maximumFirstRow = Mathf.Max(0, rowCount - visibleCount);
            firstRow = Mathf.Clamp(requestedFirstRow, 0, maximumFirstRow);
            lastRow = Mathf.Min(rowCount, firstRow + visibleCount);
        }

        private bool PassesFilter(GitHubRepo repo)
        {
            return PassesFilter(repo, currentFilter);
        }

        internal static bool PassesFilter(GitHubRepo repo, FilterOption filter)
        {
            if (repo == null)
                return false;

            return filter switch
            {
                FilterOption.PublicOnly => !repo.IsPrivate,
                FilterOption.PrivateOnly => repo.IsPrivate,
                FilterOption.ValidPackagesOnly => repo.ManifestState == PackageManifestState.Valid,
                _ => true
            };
        }

        private void DrawPackageManifestFilterStatus(int visibleRepositoryCount)
        {
            int total = discoveryCoordinator.PackageManifestCheckTotal;
            int completed = discoveryCoordinator.PackageManifestCheckCompleted;
            int unavailable = discoveryCoordinator.PackageManifestUnavailableCount;
            bool isValidating = discoveryCoordinator.IsValidatingPackageManifests || completed < total;

            if (isValidating)
            {
                string unavailableSuffix = unavailable > 0
                    ? $" · {unavailable} unavailable"
                    : string.Empty;
                float progress = total > 0 ? Mathf.Clamp01((float)completed / total) : 0f;
                DrawLoadingState(
                    "Loading valid UPM packages...",
                    $"Checking root package.json files · {completed}/{total}{unavailableSuffix}",
                    progress,
                    topSpacing: 0f);
            }
            else if (unavailable > 0)
            {
                EditorGUILayout.HelpBox(
                    $"{unavailable} {(unavailable == 1 ? "repository could" : "repositories could")} not be checked. Refresh the page to retry.",
                    MessageType.Warning);
            }

            if (!isValidating && visibleRepositoryCount == 0)
                GUILayout.Label("No valid UPM packages on this page", EditorStyles.centeredGreyMiniLabel);
        }

        private void DrawListFooter()
        {
            Rect footerRect = GUILayoutUtility.GetRect(1, 1, GUILayout.ExpandWidth(true));
            EditorGUI.DrawRect(footerRect, Styles.SeparatorColor);

            EditorGUILayout.BeginHorizontal();

            if (currentTab == Tab.Discover)
            {
                bool discoveryCommandsBlocked =
                    discoveryCoordinator.IsLoading ||
                    AsyncCommandDrainRegistry.IsDraining;
                using (new EditorGUI.DisabledScope(
                           !discoveryCoordinator.HasPrevPage || discoveryCommandsBlocked))
                {
                    if (GUILayout.Button("< Prev", EditorStyles.miniButtonLeft, GUILayout.Width(50)))
                    {
                        selectedRepoIndex = -1;
                        listScroll = Vector2.zero;
                        detailsScroll = Vector2.zero;
                        discoveryCoordinator.PrevPage();
                    }
                }

                GUILayout.Label($"Page {discoveryCoordinator.CurrentPage}", Styles.FooterLabel, GUILayout.Width(50));

                using (new EditorGUI.DisabledScope(
                           !discoveryCoordinator.HasNextPage || discoveryCommandsBlocked))
                {
                    if (GUILayout.Button("Next >", EditorStyles.miniButtonRight, GUILayout.Width(50)))
                    {
                        selectedRepoIndex = -1;
                        listScroll = Vector2.zero;
                        detailsScroll = Vector2.zero;
                        discoveryCoordinator.NextPage();
                    }
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

            bool refreshBlocked = currentTab == Tab.Discover
                ? AsyncCommandDrainRegistry.IsDraining
                : !CanRefreshInstalledPackages(
                    gitAvailable,
                    isInstalledLoading,
                    AreBackgroundLoadsDraining,
                    IsRepositoryOperationBusy,
                    !string.IsNullOrWhiteSpace(GitOperationService.RecoveryWarning));
            using (new EditorGUI.DisabledScope(refreshBlocked))
            {
                if (GUILayout.Button(EditorGUIUtility.IconContent("Refresh"), EditorStyles.iconButton, GUILayout.Width(20), GUILayout.Height(20)))
                    RefreshCurrentTab();
            }

            EditorGUILayout.EndHorizontal();
        }

        internal static bool CanRefreshInstalledPackages(
            bool isGitAvailable,
            bool isInstalledLoading,
            bool backgroundLoadsDraining,
            bool isAnyOperationBusy,
            bool recoveryRequiresReview)
        {
            return isGitAvailable &&
                   !isInstalledLoading &&
                   !backgroundLoadsDraining &&
                   !isAnyOperationBusy &&
                   !recoveryRequiresReview;
        }
    }
}
