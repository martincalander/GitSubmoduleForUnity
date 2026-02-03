using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Calander.SubmodulePackageManager.Editor
{
    public class GitSubmodulesWindow : EditorWindow
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

        private static class Styles
        {
            public static GUIStyle ListItem;
            public static GUIStyle ListItemSelected;
            public static GUIStyle HeaderLabel;
            public static GUIStyle TitleLabel;
            public static GUIStyle SubtitleLabel;
            public static GUIStyle DescriptionLabel;
            public static GUIStyle InfoBox;
            public static GUIStyle InfoLabel;
            public static GUIStyle InfoValue;
            public static GUIStyle FooterLabel;
            public static GUIStyle LinkButton;
            public static GUIStyle SectionHeader;
            public static GUIStyle DisabledLabel;
            public static bool Initialized;

            public static void Initialize()
            {
                if (Initialized) return;

                ListItem = new GUIStyle(EditorStyles.label)
                {
                    padding = new RectOffset(8, 8, 6, 6),
                    margin = new RectOffset(0, 0, 0, 0),
                    fixedHeight = 0,
                    stretchWidth = true
                };

                ListItemSelected = new GUIStyle(ListItem);
                ListItemSelected.normal.background = CreateColorTexture(new Color(0.17f, 0.36f, 0.53f, 1f));
                ListItemSelected.normal.textColor = Color.white;

                HeaderLabel = new GUIStyle(EditorStyles.boldLabel)
                {
                    fontSize = 11,
                    padding = new RectOffset(4, 4, 4, 4)
                };

                TitleLabel = new GUIStyle(EditorStyles.largeLabel)
                {
                    fontSize = 18,
                    fontStyle = FontStyle.Bold,
                    wordWrap = true,
                    margin = new RectOffset(0, 0, 0, 4)
                };

                SubtitleLabel = new GUIStyle(EditorStyles.label)
                {
                    fontSize = 11,
                    normal = { textColor = new Color(0.6f, 0.6f, 0.6f) },
                    margin = new RectOffset(0, 0, 0, 2)
                };

                DescriptionLabel = new GUIStyle(EditorStyles.label)
                {
                    wordWrap = true,
                    fontSize = 12,
                    margin = new RectOffset(0, 0, 8, 8)
                };

                InfoBox = new GUIStyle()
                {
                    padding = new RectOffset(12, 12, 10, 10),
                    margin = new RectOffset(0, 0, 8, 8)
                };
                InfoBox.normal.background = CreateColorTexture(new Color(0.22f, 0.22f, 0.22f, 1f));

                InfoLabel = new GUIStyle(EditorStyles.label)
                {
                    fontSize = 11,
                    normal = { textColor = new Color(0.65f, 0.65f, 0.65f) },
                    alignment = TextAnchor.MiddleLeft
                };

                InfoValue = new GUIStyle(EditorStyles.label)
                {
                    fontSize = 11,
                    alignment = TextAnchor.MiddleLeft,
                    wordWrap = true
                };

                FooterLabel = new GUIStyle(EditorStyles.miniLabel)
                {
                    fontSize = 10,
                    normal = { textColor = new Color(0.5f, 0.5f, 0.5f) },
                    padding = new RectOffset(8, 8, 4, 4)
                };

                LinkButton = new GUIStyle(EditorStyles.linkLabel)
                {
                    fontSize = 11,
                    margin = new RectOffset(0, 12, 0, 0)
                };

                SectionHeader = new GUIStyle(EditorStyles.label)
                {
                    fontSize = 11,
                    fontStyle = FontStyle.Bold,
                    padding = new RectOffset(0, 0, 8, 4)
                };

                DisabledLabel = new GUIStyle(EditorStyles.label)
                {
                    normal = { textColor = new Color(0.5f, 0.5f, 0.5f) }
                };

                Initialized = true;
            }

            private static Texture2D CreateColorTexture(Color color)
            {
                var tex = new Texture2D(1, 1);
                tex.SetPixel(0, 0, color);
                tex.Apply();
                return tex;
            }
        }

        private Tab currentTab = Tab.Installed;

        private Vector2 listScroll;
        private Vector2 detailsScroll;

        private List<SubmoduleInfo> installedSubmodules = new List<SubmoduleInfo>();
        private List<GitHubRepo> availableRepos = new List<GitHubRepo>();

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

        private RepoListHandle repoListHandle;
        private bool isLoadingRepos;
        private bool isCheckingPackageJson;
        private int packageJsonCheckIndex;

        private double lastInstalledRefreshTime;
        private double lastDiscoverRefreshTime;
        private DateTime lastRefreshDateTime;

        private int lastInstalledIndex = -1;
        private string installedBranchInput = string.Empty;
        private string installedActionStatus = string.Empty;
        private MessageType installedActionStatusType = MessageType.None;

        // Branch cache: keyed by repo URL
        private Dictionary<string, List<string>> branchCache = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        private string branchFetchUrl = string.Empty;
        private bool isFetchingBranches;
        private AsyncCommandHandle branchFetchHandle;

        private AddFromUrlPopup activeAddPopup;

        private void OnEnable()
        {
            RefreshDependencies();
            RefreshCurrentTab();
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

        private void DrawToolbar()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);

            // Plus dropdown button
            Rect addButtonRect = GUILayoutUtility.GetRect(new GUIContent("+"), EditorStyles.toolbarDropDown, GUILayout.Width(24));
            if (EditorGUI.DropdownButton(addButtonRect, new GUIContent("+"), FocusType.Passive, EditorStyles.toolbarDropDown))
            {
                var menu = new GenericMenu();
                menu.AddItem(new GUIContent("Add package from git URL..."), false, () => ShowAddFromUrlPopup(addButtonRect));
                menu.DropDown(addButtonRect);
            }

            GUILayout.Space(8);

            // Tab selector styled like filters
            EditorGUI.BeginChangeCheck();
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

            // Sort dropdown (GitHub tab only)
            if (currentTab == Tab.Discover)
            {
                string sortLabel = currentSort == SortOption.Name ? "Sort: Name" : "Sort: Recent";
                Rect sortRect = GUILayoutUtility.GetRect(new GUIContent(sortLabel), EditorStyles.toolbarDropDown, GUILayout.Width(90));
                if (EditorGUI.DropdownButton(sortRect, new GUIContent(sortLabel), FocusType.Passive, EditorStyles.toolbarDropDown))
                {
                    var menu = new GenericMenu();
                    menu.AddItem(new GUIContent("Name"), currentSort == SortOption.Name, () => { currentSort = SortOption.Name; SortRepos(); });
                    menu.AddItem(new GUIContent("Recently Updated"), currentSort == SortOption.RecentlyUpdated, () => { currentSort = SortOption.RecentlyUpdated; SortRepos(); });
                    menu.DropDown(sortRect);
                }

                // Filter dropdown
                string filterLabel = GetFilterLabel();
                Rect filterRect = GUILayoutUtility.GetRect(new GUIContent(filterLabel), EditorStyles.toolbarDropDown, GUILayout.Width(120));
                if (EditorGUI.DropdownButton(filterRect, new GUIContent(filterLabel), FocusType.Passive, EditorStyles.toolbarDropDown))
                {
                    var menu = new GenericMenu();
                    menu.AddItem(new GUIContent("All Repositories"), currentFilter == FilterOption.All, () => { currentFilter = FilterOption.All; Repaint(); });
                    menu.AddItem(new GUIContent("Valid Packages Only"), currentFilter == FilterOption.ValidPackagesOnly, () => { currentFilter = FilterOption.ValidPackagesOnly; Repaint(); });
                    menu.AddSeparator("");
                    menu.AddItem(new GUIContent("Public Only"), currentFilter == FilterOption.PublicOnly, () => { currentFilter = FilterOption.PublicOnly; Repaint(); });
                    menu.AddItem(new GUIContent("Private Only"), currentFilter == FilterOption.PrivateOnly, () => { currentFilter = FilterOption.PrivateOnly; Repaint(); });
                    menu.DropDown(filterRect);
                }
            }

            GUILayout.FlexibleSpace();

            // Kebab menu
            Rect menuRect = GUILayoutUtility.GetRect(new GUIContent("..."), EditorStyles.toolbarButton, GUILayout.Width(24));
            if (GUI.Button(menuRect, ":", EditorStyles.toolbarButton))
            {
                var menu = new GenericMenu();
                menu.AddItem(new GUIContent("Refresh"), false, RefreshCurrentTab);
                menu.AddSeparator("");
                menu.AddItem(new GUIContent("Reset Window"), false, () => {
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
            switch (currentFilter)
            {
                case FilterOption.ValidPackagesOnly: return "Filter: Packages";
                case FilterOption.PublicOnly: return "Filter: Public";
                case FilterOption.PrivateOnly: return "Filter: Private";
                default: return "Filter: All";
            }
        }

        private void SortRepos()
        {
            if (availableRepos == null || availableRepos.Count == 0) return;

            switch (currentSort)
            {
                case SortOption.Name:
                    availableRepos.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
                    break;
                case SortOption.RecentlyUpdated:
                    // Keep original order from GitHub API (already sorted by recent)
                    break;
            }

            selectedRepoIndex = -1;
            Repaint();
        }

        private bool DrawDependencyGate()
        {
            if (!gitAvailable)
            {
                EditorGUILayout.Space(20);
                DrawDependencyCard("Git", gitError, ToolKind.Git, TryInstallGit);

                if (!string.IsNullOrWhiteSpace(installStatus))
                {
                    EditorGUILayout.HelpBox(installStatus, installStatusType);
                }

                return false;
            }

            // Git is available — show gh warnings inline but don't block the UI
            if (!ghAvailable && currentTab == Tab.Discover)
            {
                EditorGUILayout.HelpBox(
                    "GitHub CLI is not installed. Install it to discover your repositories.\n" +
                    "You can still add packages manually via the + button using a git URL.",
                    MessageType.Info);
            }
            else if (ghAvailable && !ghAuthenticated && currentTab == Tab.Discover)
            {
                EditorGUILayout.HelpBox("GitHub CLI is not authenticated. Run 'gh auth login' in terminal to discover your repositories.", MessageType.Warning);
            }

            if (!string.IsNullOrWhiteSpace(installStatus))
            {
                EditorGUILayout.HelpBox(installStatus, installStatusType);
            }

            return true;
        }

        private void DrawDependencyCard(string title, string error, ToolKind tool, Action installAction)
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField($"{title} is required.", EditorStyles.boldLabel);
            if (!string.IsNullOrWhiteSpace(error))
            {
                EditorGUILayout.HelpBox(error.Trim(), MessageType.Error);
            }

            string hint = CliInstaller.GetInstallHint(tool);
            if (!string.IsNullOrWhiteSpace(hint))
            {
                EditorGUILayout.LabelField("Suggested install command:");
                EditorGUILayout.SelectableLabel(hint, EditorStyles.textField, GUILayout.Height(EditorGUIUtility.singleLineHeight));
            }

            if (GUILayout.Button($"Install {title}"))
            {
                if (EditorUtility.DisplayDialog($"Install {title}", $"Allow this tool to install {title} using your system package manager?", "Install", "Cancel"))
                {
                    installAction?.Invoke();
                }
            }

            EditorGUILayout.EndVertical();
        }

        private void DrawListPane()
        {
            EditorGUILayout.BeginVertical(GUILayout.Width(ListPaneWidth));

            // Search bar
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);

            EditorGUI.BeginChangeCheck();
            searchFilter = EditorGUILayout.TextField(searchFilter, EditorStyles.toolbarSearchField);
            if (EditorGUI.EndChangeCheck())
            {
                Repaint();
            }

            if (GUILayout.Button("", GUI.skin.FindStyle("ToolbarSearchCancelButton") ?? EditorStyles.toolbarButton, GUILayout.Width(18)))
            {
                searchFilter = string.Empty;
                GUI.FocusControl(null);
            }
            EditorGUILayout.EndHorizontal();

            // Header
            EditorGUILayout.BeginHorizontal();
            GUILayout.Label(currentTab == Tab.Installed ? "Packages" : "Repositories", Styles.HeaderLabel);
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();

            // Separator line
            Rect lineRect = GUILayoutUtility.GetRect(1, 1, GUILayout.ExpandWidth(true));
            EditorGUI.DrawRect(lineRect, new Color(0.15f, 0.15f, 0.15f));

            // List content
            listScroll = EditorGUILayout.BeginScrollView(listScroll, GUILayout.ExpandHeight(true));

            if (currentTab == Tab.Installed)
            {
                DrawInstalledList();
            }
            else
            {
                DrawDiscoverList();
            }

            EditorGUILayout.EndScrollView();

            // Footer with last refresh time
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

                // Draw selection background
                if (Event.current.type == EventType.Repaint)
                {
                    if (isSelected)
                    {
                        EditorGUI.DrawRect(itemRect, new Color(0.17f, 0.36f, 0.53f, 1f));
                    }
                }

                // Package name on left
                Rect nameRect = new Rect(itemRect.x + 8, itemRect.y + 4, itemRect.width - 70, itemRect.height - 8);
                var nameStyle = new GUIStyle(EditorStyles.label);
                if (isSelected) nameStyle.normal.textColor = Color.white;
                GUI.Label(nameRect, displayName, nameStyle);

                // Branch on right
                Rect versionRect = new Rect(itemRect.xMax - 60, itemRect.y + 4, 52, itemRect.height - 8);
                GUI.Label(versionRect, versionText, Styles.SubtitleLabel);

                // Handle click
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
            if (isLoadingRepos && repoListHandle != null)
            {
                EditorGUILayout.Space(20);
                EditorGUILayout.LabelField(repoListHandle.StatusMessage, EditorStyles.centeredGreyMiniLabel);
                Rect progressRect = GUILayoutUtility.GetRect(0, 4, GUILayout.ExpandWidth(true));
                EditorGUI.ProgressBar(progressRect, repoListHandle.Progress, "");
                return;
            }

            if (isCheckingPackageJson)
            {
                EditorGUILayout.Space(20);
                string checkMsg = $"Checking package.json ({packageJsonCheckIndex}/{availableRepos.Count})...";
                EditorGUILayout.LabelField(checkMsg, EditorStyles.centeredGreyMiniLabel);
                float checkProgress = availableRepos.Count > 0 ? (float)packageJsonCheckIndex / availableRepos.Count : 0f;
                Rect progressRect = GUILayoutUtility.GetRect(0, 4, GUILayout.ExpandWidth(true));
                EditorGUI.ProgressBar(progressRect, checkProgress, "");
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
                {
                    GUILayout.Label("Install GitHub CLI to discover repositories.", EditorStyles.centeredGreyMiniLabel);
                }
                else if (!ghAuthenticated)
                {
                    GUILayout.Label("Authenticate GitHub CLI to discover repositories.", EditorStyles.centeredGreyMiniLabel);
                }
                else
                {
                    GUILayout.Label("No repositories found. Click refresh to load.", EditorStyles.centeredGreyMiniLabel);
                }
                return;
            }

            for (int i = 0; i < availableRepos.Count; i++)
            {
                GitHubRepo repo = availableRepos[i];

                // Apply filter
                if (!PassesFilter(repo))
                {
                    continue;
                }

                string displayName = repo.Name;

                if (!string.IsNullOrWhiteSpace(searchFilter) &&
                    displayName.IndexOf(searchFilter, StringComparison.OrdinalIgnoreCase) < 0 &&
                    repo.Owner.IndexOf(searchFilter, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                bool isSelected = i == selectedRepoIndex;
                bool isValidPackage = repo.PackageJsonChecked && repo.HasPackageJson;
                bool isInvalidPackage = repo.PackageJsonChecked && !repo.HasPackageJson;
                string statusText = repo.IsInstalled ? "(installed)" :
                                   isInvalidPackage ? "(no package.json)" :
                                   !repo.PackageJsonChecked ? "(checking...)" :
                                   repo.IsPrivate ? "(private)" : "";

                Rect itemRect = GUILayoutUtility.GetRect(GUIContent.none, EditorStyles.label, GUILayout.ExpandWidth(true), GUILayout.Height(36));

                // Draw selection background
                if (Event.current.type == EventType.Repaint && isSelected)
                {
                    EditorGUI.DrawRect(itemRect, new Color(0.17f, 0.36f, 0.53f, 1f));
                }

                // Repo name on first line - grey if no package.json
                Rect nameRect = new Rect(itemRect.x + 8, itemRect.y + 2, itemRect.width - 16, 16);
                var nameStyle = new GUIStyle(EditorStyles.label);
                if (isSelected)
                {
                    nameStyle.normal.textColor = Color.white;
                }
                else if (isInvalidPackage)
                {
                    nameStyle.normal.textColor = new Color(0.5f, 0.5f, 0.5f);
                }
                GUI.Label(nameRect, displayName, nameStyle);

                // Status on second line
                Rect statusRect = new Rect(itemRect.x + 8, itemRect.y + 18, itemRect.width - 16, 14);
                GUI.Label(statusRect, statusText, Styles.SubtitleLabel);

                // Handle click
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
            switch (currentFilter)
            {
                case FilterOption.ValidPackagesOnly:
                    // If not yet checked, show it (will be filtered later once checked)
                    return !repo.PackageJsonChecked || repo.HasPackageJson;
                case FilterOption.PublicOnly:
                    return !repo.IsPrivate;
                case FilterOption.PrivateOnly:
                    return repo.IsPrivate;
                default:
                    return true;
            }
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
            {
                RefreshCurrentTab();
            }

            EditorGUILayout.EndHorizontal();
        }

        private void DrawDetailsPane()
        {
            EditorGUILayout.BeginVertical();

            detailsScroll = EditorGUILayout.BeginScrollView(detailsScroll, GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true));

            if (currentTab == Tab.Installed)
            {
                DrawInstalledDetails();
            }
            else
            {
                DrawDiscoverDetails();
            }

            EditorGUILayout.EndScrollView();

            EditorGUILayout.EndVertical();
        }

        private void DrawInstalledDetails()
        {
            if (selectedInstalledIndex < 0 || selectedInstalledIndex >= installedSubmodules.Count)
            {
                EditorGUILayout.Space(40);
                GUILayout.Label("Select a package to view details", EditorStyles.centeredGreyMiniLabel);
                return;
            }

            SubmoduleInfo submodule = installedSubmodules[selectedInstalledIndex];
            if (lastInstalledIndex != selectedInstalledIndex)
            {
                installedBranchInput = string.IsNullOrWhiteSpace(submodule.Branch) ? "main" : submodule.Branch;
                installedActionStatus = string.Empty;
                installedActionStatusType = MessageType.None;
                lastInstalledIndex = selectedInstalledIndex;
            }

            EditorGUILayout.Space(8);

            // Title
            string displayName = submodule.PackageName ?? submodule.Name;
            GUILayout.Label(displayName, Styles.TitleLabel);

            // Subtitle
            string branchInfo = !string.IsNullOrWhiteSpace(submodule.Branch) ? submodule.Branch : "main";
            GUILayout.Label($"{branchInfo} · Git Submodule", Styles.SubtitleLabel);

            EditorGUILayout.Space(4);

            // Link buttons
            EditorGUILayout.BeginHorizontal();
            if (!string.IsNullOrWhiteSpace(submodule.Url) && GUILayout.Button("Repository", Styles.LinkButton))
            {
                Application.OpenURL(submodule.Url);
            }
            if (GUILayout.Button("Show in Explorer", Styles.LinkButton))
            {
                string fullPath = Path.Combine(GitUtility.ProjectRoot, submodule.Path);
                EditorUtility.RevealInFinder(fullPath);
            }
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(8);

            // Action buttons
            EditorGUILayout.BeginHorizontal();
            using (new EditorGUI.DisabledScope(!gitAvailable))
            {
                if (GUILayout.Button("Update", GUILayout.Height(24)))
                {
                    if (EditorUtility.DisplayDialog("Update Submodule", $"Fetch and update:\n{submodule.Path}?", "Update", "Cancel"))
                    {
                        PerformUpdate(submodule);
                    }
                }

                if (GUILayout.Button("Remove", GUILayout.Height(24)))
                {
                    if (EditorUtility.DisplayDialog("Remove Submodule", $"Remove submodule at {submodule.Path}?", "Remove", "Cancel"))
                    {
                        PerformRemove(submodule);
                    }
                }
            }
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(12);

            // Info box
            EditorGUILayout.BeginVertical(Styles.InfoBox);
            DrawInfoRow("Path", submodule.Path);
            DrawInfoRow("URL", submodule.Url);
            if (!string.IsNullOrWhiteSpace(submodule.Branch))
            {
                DrawInfoRow("Branch", submodule.Branch);
            }
            if (!string.IsNullOrWhiteSpace(submodule.CommitHash))
            {
                DrawInfoRow("Commit", submodule.CommitHash.Length > 7 ? submodule.CommitHash.Substring(0, 7) : submodule.CommitHash);
            }
            EditorGUILayout.EndVertical();

            // Warnings
            if (!submodule.HasPackageJson)
            {
                EditorGUILayout.HelpBox("This submodule does not contain a package.json at its root.", MessageType.Warning);
            }

            if (!string.IsNullOrWhiteSpace(installedActionStatus))
            {
                EditorGUILayout.HelpBox(installedActionStatus, installedActionStatusType);
            }

            // Branch change section
            EditorGUILayout.Space(12);
            GUILayout.Label("Change Branch", Styles.SectionHeader);
            EditorGUILayout.BeginHorizontal();
            DrawBranchDropdown(submodule.Url, installedBranchInput, branch => { installedBranchInput = branch; });
            using (new EditorGUI.DisabledScope(!gitAvailable || string.IsNullOrWhiteSpace(installedBranchInput)))
            {
                if (GUILayout.Button("Apply", GUILayout.Width(60), GUILayout.Height(20)))
                {
                    PerformBranchChange(submodule, installedBranchInput.Trim());
                }
            }
            EditorGUILayout.EndHorizontal();
        }

        private void DrawDiscoverDetails()
        {
            if (selectedRepoIndex < 0 || selectedRepoIndex >= availableRepos.Count)
            {
                EditorGUILayout.Space(40);
                GUILayout.Label("Select a repository to view details", EditorStyles.centeredGreyMiniLabel);
                return;
            }

            GitHubRepo repo = availableRepos[selectedRepoIndex];

            EditorGUILayout.Space(8);

            // Title
            GUILayout.Label(repo.Name, Styles.TitleLabel);

            // Subtitle
            string subtitle = !string.IsNullOrWhiteSpace(repo.Description) ? repo.Description : $"Repository by {repo.Owner}";
            GUILayout.Label(subtitle, Styles.SubtitleLabel);

            EditorGUILayout.Space(4);

            // Link buttons
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("View on GitHub", Styles.LinkButton))
            {
                Application.OpenURL(repo.Url);
            }
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(8);

            // Info box
            EditorGUILayout.BeginVertical(Styles.InfoBox);
            DrawInfoRow("Owner", repo.Owner);
            DrawInfoRow("URL", repo.Url);
            if (!string.IsNullOrWhiteSpace(repo.DefaultBranch))
            {
                DrawInfoRow("Default Branch", repo.DefaultBranch);
            }
            DrawInfoRow("Visibility", repo.IsPrivate ? "Private" : "Public");
            string packageStatus = !repo.PackageJsonChecked ? "Checking..." : repo.HasPackageJson ? "Yes" : "No";
            DrawInfoRow("Unity Package", packageStatus);
            EditorGUILayout.EndVertical();

            // Warning for repos without package.json
            if (repo.PackageJsonChecked && !repo.HasPackageJson)
            {
                EditorGUILayout.HelpBox("This repository does not contain a package.json at its root. It may not be a valid Unity package.", MessageType.Warning);
            }

            // Warning for private repos
            if (repo.IsPrivate)
            {
                EditorGUILayout.HelpBox("Private repository. Collaborators will need access to clone this submodule.", MessageType.Warning);
            }

            if (repo.IsInstalled)
            {
                EditorGUILayout.HelpBox("This repository is already installed.", MessageType.Info);
            }

            // Add package section
            if (!repo.IsInstalled)
            {
                EditorGUILayout.Space(12);
                GUILayout.Label("Add as Package", Styles.SectionHeader);

                EditorGUILayout.BeginVertical(Styles.InfoBox);

                EditorGUILayout.BeginHorizontal();
                GUILayout.Label("Package Name", Styles.InfoLabel, GUILayout.Width(100));
                selectedRepoPackageName = EditorGUILayout.TextField(selectedRepoPackageName);
                EditorGUILayout.EndHorizontal();

                EditorGUILayout.BeginHorizontal();
                GUILayout.Label("Branch", Styles.InfoLabel, GUILayout.Width(100));
                DrawBranchDropdown(repo.Url, selectedRepoBranch, branch => { selectedRepoBranch = branch; });
                EditorGUILayout.EndHorizontal();

                EditorGUILayout.EndVertical();

                string validationError = ValidatePackageInput(repo.Url, selectedRepoPackageName);
                if (!string.IsNullOrWhiteSpace(validationError))
                {
                    EditorGUILayout.HelpBox(validationError, MessageType.Warning);
                }
                else
                {
                    GUILayout.Label(PackageNameRule, Styles.FooterLabel);
                }

                EditorGUILayout.Space(8);

                using (new EditorGUI.DisabledScope(!string.IsNullOrWhiteSpace(validationError)))
                {
                    if (GUILayout.Button("Add Package", GUILayout.Height(28)))
                    {
                        TryAddSubmodule(repo.Url, selectedRepoBranch, selectedRepoPackageName);
                    }
                }
            }

            if (!string.IsNullOrWhiteSpace(addStatus))
            {
                EditorGUILayout.HelpBox(addStatus, addStatusType);
            }
        }

        private void DrawInfoRow(string label, string value)
        {
            EditorGUILayout.BeginHorizontal();
            GUILayout.Label(label, Styles.InfoLabel, GUILayout.Width(100));
            EditorGUILayout.SelectableLabel(value, Styles.InfoValue, GUILayout.Height(16));
            EditorGUILayout.EndHorizontal();
        }

        private void PerformUpdate(SubmoduleInfo submodule)
        {
            if (!GitUtility.TryUpdateSubmodule(submodule.Path, out string error))
            {
                installedActionStatus = error;
                installedActionStatusType = MessageType.Error;
            }
            else
            {
                installedActionStatus = "Submodule updated successfully.";
                installedActionStatusType = MessageType.Info;
                RefreshInstalled();
            }
        }

        private void PerformRemove(SubmoduleInfo submodule)
        {
            if (!GitUtility.TryRemoveSubmodule(submodule.Path, out string error))
            {
                installedStatus = error;
                installedStatusType = MessageType.Error;
            }
            else
            {
                selectedInstalledIndex = -1;
            }
            RefreshInstalled();
            RefreshAvailable();
        }

        private void PerformBranchChange(SubmoduleInfo submodule, string branch)
        {
            if (!GitUtility.TrySetSubmoduleBranch(submodule.Path, branch, out string error))
            {
                installedActionStatus = error;
                installedActionStatusType = MessageType.Error;
            }
            else
            {
                installedActionStatus = $"Branch set to {branch}.";
                installedActionStatusType = MessageType.Info;
                RefreshInstalled();

                if (EditorUtility.DisplayDialog("Update Submodule", "Update to the new branch now?", "Update", "Later"))
                {
                    PerformUpdate(submodule);
                }
            }
        }

        private void RefreshDependencies()
        {
            gitAvailable = GitUtility.IsGitAvailable(out gitVersion, out gitError);
            ghAvailable = GitHubUtility.IsGhAvailable(out ghVersion, out ghError);
            ghAuthenticated = ghAvailable && GitHubUtility.IsAuthenticated(out ghAuthError);
        }

        private void TryInstallGit()
        {
            installStatus = string.Empty;
            installStatusType = MessageType.None;

            if (CliInstaller.TryInstallGit(out string output, out string error))
            {
                installStatus = string.IsNullOrWhiteSpace(output) ? "Git installation completed." : output.Trim();
                installStatusType = MessageType.Info;
            }
            else
            {
                installStatus = string.IsNullOrWhiteSpace(error) ? "Git installation failed." : error.Trim();
                installStatusType = MessageType.Error;
            }

            RefreshDependencies();
        }

        private void TryInstallGh()
        {
            installStatus = string.Empty;
            installStatusType = MessageType.None;

            if (CliInstaller.TryInstallGh(out string output, out string error))
            {
                installStatus = string.IsNullOrWhiteSpace(output) ? "GitHub CLI installation completed." : output.Trim();
                installStatusType = MessageType.Info;
            }
            else
            {
                installStatus = string.IsNullOrWhiteSpace(error) ? "GitHub CLI installation failed." : error.Trim();
                installStatusType = MessageType.Error;
            }

            RefreshDependencies();
        }

        private void RefreshCurrentTab()
        {
            switch (currentTab)
            {
                case Tab.Installed:
                    RefreshInstalled();
                    break;
                case Tab.Discover:
                    RefreshAvailable();
                    break;
            }
        }

        private void RefreshCurrentTabIfStale()
        {
            double now = EditorApplication.timeSinceStartup;

            switch (currentTab)
            {
                case Tab.Installed:
                    bool installedNeedsRefresh = installedSubmodules.Count == 0 ||
                        (now - lastInstalledRefreshTime) > AutoRefreshIntervalSeconds;
                    if (installedNeedsRefresh)
                    {
                        RefreshInstalled();
                    }
                    break;
                case Tab.Discover:
                    bool discoverNeedsRefresh = availableRepos.Count == 0 ||
                        (now - lastDiscoverRefreshTime) > AutoRefreshIntervalSeconds;
                    if (discoverNeedsRefresh && !isLoadingRepos)
                    {
                        RefreshAvailable();
                    }
                    break;
            }
        }

        private void RefreshInstalled()
        {
            installedStatus = string.Empty;
            installedStatusType = MessageType.None;

            if (!gitAvailable)
            {
                installedStatus = "Git is required to list submodules.";
                installedStatusType = MessageType.Warning;
                return;
            }

            if (!GitUtility.TryGetSubmodules(out installedSubmodules, out string error))
            {
                installedStatus = error;
                installedStatusType = MessageType.Error;
                installedSubmodules = new List<SubmoduleInfo>();
            }

            selectedInstalledIndex = Mathf.Clamp(selectedInstalledIndex, -1, installedSubmodules.Count - 1);
            lastInstalledRefreshTime = EditorApplication.timeSinceStartup;
            lastRefreshDateTime = DateTime.Now;
        }

        private void RefreshAvailable()
        {
            discoverStatus = string.Empty;
            discoverStatusType = MessageType.None;

            if (!ghAvailable)
            {
                availableRepos = new List<GitHubRepo>();
                return;
            }

            if (!ghAuthenticated)
            {
                availableRepos = new List<GitHubRepo>();
                return;
            }

            isLoadingRepos = true;
            repoListHandle = GitHubUtility.StartListReposAsync();
        }

        private void UpdateRepoLoading()
        {
            if (!isLoadingRepos || repoListHandle == null)
            {
                UpdatePackageJsonChecking();
                return;
            }

            repoListHandle.Update();

            if (!repoListHandle.IsComplete)
            {
                Repaint();
                return;
            }

            isLoadingRepos = false;

            if (!repoListHandle.IsSuccess)
            {
                discoverStatus = repoListHandle.Error;
                discoverStatusType = MessageType.Error;
                availableRepos = new List<GitHubRepo>();
                repoListHandle = null;
                return;
            }

            availableRepos = repoListHandle.Repos;
            MarkInstalledRepos();
            SortRepos();
            selectedRepoIndex = Mathf.Clamp(selectedRepoIndex, -1, availableRepos.Count - 1);
            repoListHandle = null;
            lastDiscoverRefreshTime = EditorApplication.timeSinceStartup;
            lastRefreshDateTime = DateTime.Now;

            // Start checking package.json for each repo
            StartPackageJsonChecking();

            Repaint();
        }

        private void StartPackageJsonChecking()
        {
            if (availableRepos == null || availableRepos.Count == 0)
            {
                return;
            }

            isCheckingPackageJson = true;
            packageJsonCheckIndex = 0;
            EditorApplication.update += ProcessNextPackageJsonCheck;
        }

        private void ProcessNextPackageJsonCheck()
        {
            if (!isCheckingPackageJson || availableRepos == null)
            {
                EditorApplication.update -= ProcessNextPackageJsonCheck;
                isCheckingPackageJson = false;
                return;
            }

            if (packageJsonCheckIndex >= availableRepos.Count)
            {
                EditorApplication.update -= ProcessNextPackageJsonCheck;
                isCheckingPackageJson = false;
                Repaint();
                return;
            }

            var repo = availableRepos[packageJsonCheckIndex];

            // Check if repo has package.json via GitHub API
            if (GitHubUtility.TryRepoHasPackageJson(repo.Owner, repo.Name, out bool hasPackageJson, out _))
            {
                repo.HasPackageJson = hasPackageJson;
            }
            repo.PackageJsonChecked = true;

            packageJsonCheckIndex++;
            Repaint();
        }

        private void UpdatePackageJsonChecking()
        {
            // This is handled by EditorApplication.update callback
        }

        private void FetchBranchesForUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                return;
            }

            // Already cached
            if (branchCache.ContainsKey(url))
            {
                return;
            }

            // Already fetching this URL
            if (isFetchingBranches && string.Equals(branchFetchUrl, url, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            // Cancel any previous fetch
            isFetchingBranches = true;
            branchFetchUrl = url;
            branchFetchHandle = CliCommandRunner.RunAsync("git", $"ls-remote --heads {url}", GitUtility.ProjectRoot);
        }

        private void UpdateBranchFetching()
        {
            if (!isFetchingBranches || branchFetchHandle == null)
            {
                return;
            }

            if (!branchFetchHandle.IsComplete)
            {
                return;
            }

            var result = branchFetchHandle.Result;
            var branches = new List<string>();

            if (result.IsSuccess)
            {
                string[] lines = result.StdOut.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (string line in lines)
                {
                    int refsIndex = line.IndexOf("refs/heads/", StringComparison.Ordinal);
                    if (refsIndex >= 0)
                    {
                        string branch = line.Substring(refsIndex + "refs/heads/".Length).Trim();
                        if (!string.IsNullOrEmpty(branch))
                        {
                            branches.Add(branch);
                        }
                    }
                }
                branches.Sort(StringComparer.OrdinalIgnoreCase);
            }

            if (!string.IsNullOrWhiteSpace(branchFetchUrl))
            {
                branchCache[branchFetchUrl] = branches;
            }

            branchFetchHandle = null;
            isFetchingBranches = false;
            Repaint();
        }

        private void DrawBranchDropdown(string url, string currentBranch, System.Action<string> onBranchSelected)
        {
            // Trigger fetch if not cached
            FetchBranchesForUrl(url);

            List<string> branches = null;
            bool hasCachedBranches = !string.IsNullOrWhiteSpace(url) && branchCache.TryGetValue(url, out branches) && branches != null && branches.Count > 0;
            bool isFetchingThisUrl = isFetchingBranches && string.Equals(branchFetchUrl, url, StringComparison.OrdinalIgnoreCase);
            bool isLoading = isFetchingThisUrl && !hasCachedBranches;

            string buttonLabel = string.IsNullOrWhiteSpace(currentBranch) ? "Select branch..." : currentBranch;
            string tooltip = isLoading ? "Fetching branches from remote..." : "";

            using (new EditorGUI.DisabledScope(isLoading))
            {
                Rect dropdownRect = GUILayoutUtility.GetRect(new GUIContent(buttonLabel), EditorStyles.popup, GUILayout.Height(20));
                if (EditorGUI.DropdownButton(dropdownRect, new GUIContent(buttonLabel, tooltip), FocusType.Passive, EditorStyles.popup))
                {
                    if (hasCachedBranches)
                    {
                        var menu = new GenericMenu();
                        foreach (string branch in branches)
                        {
                            bool isActive = string.Equals(branch, currentBranch, StringComparison.OrdinalIgnoreCase);
                            string branchCapture = branch;
                            menu.AddItem(new GUIContent(branch), isActive, () =>
                            {
                                onBranchSelected?.Invoke(branchCapture);
                                Repaint();
                            });
                        }
                        menu.DropDown(dropdownRect);
                    }
                    else
                    {
                        // Force re-fetch
                        if (!string.IsNullOrWhiteSpace(url))
                        {
                            branchCache.Remove(url);
                        }
                        FetchBranchesForUrl(url);
                    }
                }
            }
        }

        private void MarkInstalledRepos()
        {
            var installedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var submodule in installedSubmodules)
            {
                if (GitHubUtility.TryParseGitHubRepo(submodule.Url, out string owner, out string repo))
                {
                    installedIds.Add($"{owner}/{repo}");
                }
            }

            foreach (var repo in availableRepos)
            {
                repo.IsInstalled = installedIds.Contains($"{repo.Owner}/{repo.Name}");
            }
        }

        private void InitializeRepoDefaults(GitHubRepo repo)
        {
            selectedRepoPackageName = GitHubUtility.DerivePackageNameSuggestion(repo.Owner, repo.Name);
            selectedRepoBranch = string.IsNullOrWhiteSpace(repo.DefaultBranch) ? "main" : repo.DefaultBranch;
            addStatus = string.Empty;
        }

        private string ValidatePackageInput(string url, string packageName)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                return "Git URL is required.";
            }

            if (!GitUtility.IsValidPackageName(packageName))
            {
                return PackageNameRule;
            }

            string path = GetPackagePath(packageName);
            string fullPath = Path.Combine(GitUtility.ProjectRoot, path);
            if (Directory.Exists(fullPath))
            {
                return $"Package path already exists: {path}";
            }

            foreach (var submodule in installedSubmodules)
            {
                if (string.Equals(submodule.Path, path, StringComparison.OrdinalIgnoreCase))
                {
                    return "A submodule already exists at this path.";
                }
            }

            return string.Empty;
        }

        private bool TryDerivePackageNameFromUrl(string url, out string packageName)
        {
            packageName = string.Empty;
            if (!GitHubUtility.TryParseGitHubRepo(url, out string owner, out string repo))
            {
                return false;
            }

            packageName = GitHubUtility.DerivePackageNameSuggestion(owner, repo);
            return !string.IsNullOrEmpty(packageName);
        }

        private void TryAddSubmodule(string url, string branch, string packageName)
        {
            addStatus = string.Empty;
            addStatusType = MessageType.None;

            string validationError = ValidatePackageInput(url, packageName);
            if (!string.IsNullOrWhiteSpace(validationError))
            {
                addStatus = validationError;
                addStatusType = MessageType.Error;
                return;
            }

            string path = GetPackagePath(packageName);

            if (ghAuthenticated && GitHubUtility.TryParseGitHubRepo(url, out string owner, out string repo))
            {
                if (!GitHubUtility.TryRepoHasPackageJson(owner, repo, out bool hasPackageJson, out string error))
                {
                    addStatus = error;
                    addStatusType = MessageType.Error;
                    return;
                }

                if (!hasPackageJson)
                {
                    addStatus = "Repository does not contain a package.json at its root.";
                    addStatusType = MessageType.Error;
                    return;
                }
            }

            if (!GitUtility.TryAddSubmodule(url, path, branch, out string gitError))
            {
                addStatus = gitError;
                addStatusType = MessageType.Error;
                return;
            }

            string packageJsonPath = Path.Combine(GitUtility.ProjectRoot, path, "package.json");
            if (!File.Exists(packageJsonPath))
            {
                RollbackSubmodule(path, "Added submodule does not contain a package.json at its root.");
                return;
            }

            if (!GitUtility.TryReadPackageName(packageJsonPath, out string declaredName))
            {
                RollbackSubmodule(path, "Failed to read package name from package.json.");
                return;
            }

            if (!string.Equals(declaredName, packageName, StringComparison.Ordinal))
            {
                RollbackSubmodule(path, $"Package name mismatch. Expected {packageName}, got {declaredName}.");
                return;
            }

            addStatus = $"Successfully added {packageName}.";
            addStatusType = MessageType.Info;
            RefreshInstalled();
            RefreshAvailable();

            if (activeAddPopup != null)
            {
                activeAddPopup.ClosePopup();
                activeAddPopup = null;
            }
        }

        private void RollbackSubmodule(string path, string message)
        {
            if (!GitUtility.TryRemoveSubmodule(path, out string error))
            {
                addStatus = $"{message} Failed to remove submodule: {error}";
                addStatusType = MessageType.Error;
                RefreshInstalled();
                RefreshAvailable();
                return;
            }

            addStatus = message;
            addStatusType = MessageType.Error;
            RefreshInstalled();
            RefreshAvailable();
        }

        private static string GetPackagePath(string packageName)
        {
            return $"Packages/{packageName}";
        }

        private void ShowAddFromUrlPopup(Rect buttonRect)
        {
            addStatus = string.Empty;
            addStatusType = MessageType.None;
            activeAddPopup = new AddFromUrlPopup(this);
            PopupWindow.Show(buttonRect, activeAddPopup);
        }

        private void DrawAddByUrl()
        {
            EditorGUILayout.Space(8);
            GUILayout.Label("Add package from git URL", Styles.TitleLabel);
            EditorGUILayout.Space(8);

            EditorGUI.BeginChangeCheck();
            EditorGUILayout.BeginHorizontal();
            GUILayout.Label("URL", Styles.InfoLabel, GUILayout.Width(80));
            addUrl = EditorGUILayout.TextField(addUrl);
            EditorGUILayout.EndHorizontal();
            if (EditorGUI.EndChangeCheck())
            {
                if (TryDerivePackageNameFromUrl(addUrl, out string derivedName))
                {
                    addPackageName = derivedName;
                }
            }

            EditorGUILayout.BeginHorizontal();
            GUILayout.Label("Branch", Styles.InfoLabel, GUILayout.Width(80));
            addBranch = EditorGUILayout.TextField(addBranch);
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            GUILayout.Label("Package Name", Styles.InfoLabel, GUILayout.Width(80));
            addPackageName = EditorGUILayout.TextField(addPackageName);
            EditorGUILayout.EndHorizontal();

            if (TryDerivePackageNameFromUrl(addUrl, out string autoName))
            {
                if (string.IsNullOrWhiteSpace(addPackageName) || !GitUtility.IsValidPackageName(addPackageName))
                {
                    addPackageName = autoName;
                }
            }

            EditorGUILayout.Space(8);

            string validationError = ValidatePackageInput(addUrl, addPackageName);
            if (!string.IsNullOrWhiteSpace(validationError))
            {
                EditorGUILayout.HelpBox(validationError, MessageType.Warning);
            }
            else
            {
                GUILayout.Label(PackageNameRule, Styles.FooterLabel);
            }

            if (!string.IsNullOrWhiteSpace(addStatus))
            {
                EditorGUILayout.HelpBox(addStatus, addStatusType);
            }

            EditorGUILayout.Space(8);

            using (new EditorGUI.DisabledScope(!gitAvailable || !string.IsNullOrWhiteSpace(validationError)))
            {
                if (GUILayout.Button("Add", GUILayout.Height(24)))
                {
                    TryAddSubmodule(addUrl, addBranch, addPackageName);
                }
            }
        }

        private sealed class AddFromUrlPopup : PopupWindowContent
        {
            private readonly GitSubmodulesWindow owner;

            public AddFromUrlPopup(GitSubmodulesWindow owner)
            {
                this.owner = owner;
            }

            public override Vector2 GetWindowSize()
            {
                return new Vector2(400f, 220f);
            }

            public override void OnGUI(Rect rect)
            {
                Styles.Initialize();
                owner.DrawAddByUrl();
            }

            public override void OnClose()
            {
                owner.activeAddPopup = null;
            }

            public void ClosePopup()
            {
                editorWindow?.Close();
            }
        }
    }
}
