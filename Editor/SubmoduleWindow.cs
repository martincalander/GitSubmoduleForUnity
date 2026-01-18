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

        private const string PackageNameRule = "Package name must follow com.author.package (lowercase).";
        private const float ListPaneRatio = 0.35f;

        private Tab currentTab = Tab.Installed;

        private Vector2 installedScroll;
        private Vector2 discoverScroll;
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

        private string repoSearch = string.Empty;
        private string selectedRepoPackageName = string.Empty;
        private string selectedRepoBranch = string.Empty;

        private void OnEnable()
        {
            RefreshDependencies();
            RefreshInstalled();
        }

        private bool showAddFromUrl = true;

        private void OnGUI()
        {
            DrawToolbar();
            DrawDependencyGate();

            switch (currentTab)
            {
                case Tab.Installed:
                    DrawInstalledTab();
                    break;
                case Tab.Discover:
                    DrawDiscoverTab();
                    break;
            }
        }

        internal void RefreshSubmodules()
        {
            RefreshDependencies();
            RefreshInstalled();
        }

        private void DrawToolbar()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);

            Tab previousTab = currentTab;
            int selected = GUILayout.Toolbar((int)currentTab, new[] { "Installed", "Discover" }, EditorStyles.toolbarButton);
            currentTab = (Tab)selected;

            if (previousTab != currentTab)
            {
                RefreshCurrentTab();
                Repaint();
            }

            GUILayout.FlexibleSpace();

            if (currentTab == Tab.Discover)
            {
                if (GUILayout.Button("+ Add from URL", EditorStyles.toolbarButton))
                {
                    showAddFromUrl = !showAddFromUrl;
                    Repaint();
                }
            }

            if (GUILayout.Button("Refresh", EditorStyles.toolbarButton))
            {
                RefreshCurrentTab();
            }

            EditorGUILayout.EndHorizontal();
        }

        private bool DrawDependencyGate()
        {
            if (gitAvailable && ghAvailable)
            {
                EditorGUILayout.BeginHorizontal(EditorStyles.helpBox);
                EditorGUILayout.LabelField($"Git: {gitVersion}", EditorStyles.miniLabel);
                EditorGUILayout.LabelField($"GitHub CLI: {ghVersion}", EditorStyles.miniLabel);
                EditorGUILayout.EndHorizontal();

                if (!ghAuthenticated)
                {
                    EditorGUILayout.HelpBox("GitHub CLI is installed but not authenticated. Run `gh auth login` to list your repositories.", MessageType.Warning);
                }

                if (!string.IsNullOrWhiteSpace(installStatus))
                {
                    EditorGUILayout.HelpBox(installStatus, installStatusType);
                }

                return true;
            }

            EditorGUILayout.Space();

            if (!gitAvailable)
            {
                DrawDependencyCard("Git", gitError, ToolKind.Git, TryInstallGit);
            }

            if (!ghAvailable)
            {
                DrawDependencyCard("GitHub CLI", ghError, ToolKind.GitHubCli, TryInstallGh);
            }

            if (!string.IsNullOrWhiteSpace(installStatus))
            {
                EditorGUILayout.HelpBox(installStatus, installStatusType);
            }

            return false;
        }

        private void DrawDependencyCard(string title, string error, ToolKind tool, Action installAction)
        {
            EditorGUILayout.BeginVertical("box");
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
        }

        private void RefreshAvailable()
        {
            discoverStatus = string.Empty;
            discoverStatusType = MessageType.None;

            if (!ghAvailable)
            {
                discoverStatus = "GitHub CLI is required to list your repositories.";
                discoverStatusType = MessageType.Warning;
                return;
            }

            if (!ghAuthenticated)
            {
                discoverStatus = string.IsNullOrWhiteSpace(ghAuthError) ? "GitHub CLI is not authenticated." : ghAuthError.Trim();
                discoverStatusType = MessageType.Warning;
                availableRepos = new List<GitHubRepo>();
                return;
            }

            if (!GitHubUtility.TryListRepos(out availableRepos, out string error))
            {
                discoverStatus = error;
                discoverStatusType = MessageType.Error;
                availableRepos = new List<GitHubRepo>();
                return;
            }

            MarkInstalledRepos();
            selectedRepoIndex = Mathf.Clamp(selectedRepoIndex, -1, availableRepos.Count - 1);
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

        private void DrawInstalledTab()
        {
            EditorGUILayout.Space();

            if (!string.IsNullOrWhiteSpace(installedStatus))
            {
                EditorGUILayout.HelpBox(installedStatus, installedStatusType);
            }

            if (installedSubmodules == null || installedSubmodules.Count == 0)
            {
                EditorGUILayout.HelpBox("No git submodules found.", MessageType.Info);
                return;
            }

            EditorGUILayout.BeginHorizontal();
            DrawInstalledList();
            DrawInstalledDetails();
            EditorGUILayout.EndHorizontal();
        }

        private void DrawInstalledList()
        {
            float listWidth = Mathf.Max(220f, position.width * ListPaneRatio);
            EditorGUILayout.BeginVertical(GUILayout.Width(listWidth));

            installedScroll = EditorGUILayout.BeginScrollView(installedScroll);
            for (int i = 0; i < installedSubmodules.Count; i++)
            {
                SubmoduleInfo submodule = installedSubmodules[i];
                string label = string.IsNullOrWhiteSpace(submodule.PackageName) ? submodule.Name : submodule.PackageName;
                string suffix = submodule.HasPackageJson ? string.Empty : " (missing package.json)";
                bool selected = i == selectedInstalledIndex;

                if (GUILayout.Toggle(selected, label + suffix, "Button"))
                {
                    selectedInstalledIndex = i;
                }
            }
            EditorGUILayout.EndScrollView();

            EditorGUILayout.EndVertical();
        }

        private void DrawInstalledDetails()
        {
            EditorGUILayout.BeginVertical("box");

            if (selectedInstalledIndex < 0 || selectedInstalledIndex >= installedSubmodules.Count)
            {
                EditorGUILayout.LabelField("Select a submodule to view details.");
                EditorGUILayout.EndVertical();
                return;
            }

            SubmoduleInfo submodule = installedSubmodules[selectedInstalledIndex];
            detailsScroll = EditorGUILayout.BeginScrollView(detailsScroll);

            EditorGUILayout.LabelField(submodule.PackageName ?? submodule.Name, EditorStyles.boldLabel);
            EditorGUILayout.Space();

            EditorGUILayout.LabelField("Path", submodule.Path);
            EditorGUILayout.LabelField("URL", submodule.Url);
            if (!string.IsNullOrWhiteSpace(submodule.Branch))
            {
                EditorGUILayout.LabelField("Branch", submodule.Branch);
            }
            if (!string.IsNullOrWhiteSpace(submodule.CommitHash))
            {
                EditorGUILayout.LabelField("Commit", submodule.CommitHash);
            }

            if (!submodule.HasPackageJson)
            {
                EditorGUILayout.HelpBox("This submodule does not contain a package.json at its root.", MessageType.Warning);
            }

            EditorGUILayout.EndScrollView();
            GUILayout.FlexibleSpace();

            using (new EditorGUI.DisabledScope(!gitAvailable))
            {
                if (GUILayout.Button("Remove Submodule"))
                {
                    if (EditorUtility.DisplayDialog("Remove Submodule", $"Remove submodule at {submodule.Path}?", "Remove", "Cancel"))
                    {
                        if (!GitUtility.TryRemoveSubmodule(submodule.Path, out string error))
                        {
                            installedStatus = error;
                            installedStatusType = MessageType.Error;
                        }
                        RefreshInstalled();
                        RefreshAvailable();
                    }
                }
            }

            EditorGUILayout.EndVertical();
        }

        private void DrawDiscoverTab()
        {
            if (showAddFromUrl)
            {
                DrawAddByUrl();
                EditorGUILayout.Space();
            }

            if (!string.IsNullOrWhiteSpace(discoverStatus))
            {
                EditorGUILayout.HelpBox(discoverStatus, discoverStatusType);
            }

            if (availableRepos == null || availableRepos.Count == 0)
            {
                EditorGUILayout.HelpBox("No repositories loaded. Press Refresh to fetch your GitHub repos.", MessageType.Info);
            }

            DrawRepoSearch();
            EditorGUILayout.BeginHorizontal();
            DrawRepoList();
            DrawRepoDetails();
            EditorGUILayout.EndHorizontal();
        }

        private void DrawAddByUrl()
        {
            EditorGUILayout.BeginVertical("box");
            EditorGUILayout.LabelField("Add package from Git URL", EditorStyles.boldLabel);

            EditorGUI.BeginChangeCheck();
            addUrl = EditorGUILayout.TextField("Git URL", addUrl);
            if (EditorGUI.EndChangeCheck())
            {
                if (TryDerivePackageNameFromUrl(addUrl, out string derivedName))
                {
                    addPackageName = derivedName;
                }
            }

            addBranch = EditorGUILayout.TextField("Branch", addBranch);
            addPackageName = EditorGUILayout.TextField("Package Name", addPackageName);

            string validationError = ValidatePackageInput(addUrl, addPackageName);
            if (!string.IsNullOrWhiteSpace(validationError))
            {
                EditorGUILayout.HelpBox(validationError, MessageType.Warning);
            }
            else
            {
                EditorGUILayout.LabelField(PackageNameRule, EditorStyles.miniLabel);
            }

            if (!string.IsNullOrWhiteSpace(addStatus))
            {
                EditorGUILayout.HelpBox(addStatus, addStatusType);
            }

            using (new EditorGUI.DisabledScope(!gitAvailable || !string.IsNullOrWhiteSpace(validationError)))
            {
                if (GUILayout.Button("Add Package"))
                {
                    TryAddSubmodule(addUrl, addBranch, addPackageName);
                }
            }

            EditorGUILayout.EndVertical();
        }

        private void DrawRepoSearch()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
            GUILayout.Label("My Repositories", EditorStyles.boldLabel);
            GUILayout.FlexibleSpace();
            repoSearch = EditorGUILayout.TextField(repoSearch, EditorStyles.toolbarSearchField, GUILayout.MaxWidth(200f));
            if (GUILayout.Button("x", EditorStyles.toolbarButton, GUILayout.Width(20f)))
            {
                repoSearch = string.Empty;
                GUI.FocusControl(null);
            }
            EditorGUILayout.EndHorizontal();
        }

        private void DrawRepoList()
        {
            float listWidth = Mathf.Max(220f, position.width * ListPaneRatio);
            EditorGUILayout.BeginVertical(GUILayout.Width(listWidth));

            discoverScroll = EditorGUILayout.BeginScrollView(discoverScroll);
            for (int i = 0; i < availableRepos.Count; i++)
            {
                GitHubRepo repo = availableRepos[i];
                if (!IsRepoVisible(repo))
                {
                    continue;
                }

                string label = $"{repo.Owner}/{repo.Name}";
                if (repo.IsPrivate)
                {
                    label += " (private)";
                }
                else if (repo.IsInstalled)
                {
                    label += " (installed)";
                }

                bool selected = i == selectedRepoIndex;
                if (GUILayout.Toggle(selected, label, "Button"))
                {
                    if (selectedRepoIndex != i)
                    {
                        selectedRepoIndex = i;
                        InitializeRepoDefaults(repo);
                    }
                }
            }
            EditorGUILayout.EndScrollView();

            EditorGUILayout.EndVertical();
        }

        private void DrawRepoDetails()
        {
            EditorGUILayout.BeginVertical("box");

            if (selectedRepoIndex < 0 || selectedRepoIndex >= availableRepos.Count)
            {
                EditorGUILayout.LabelField("Select a repository to view details.");
                EditorGUILayout.EndVertical();
                return;
            }

            GitHubRepo repo = availableRepos[selectedRepoIndex];
            detailsScroll = EditorGUILayout.BeginScrollView(detailsScroll);

            EditorGUILayout.LabelField($"{repo.Owner}/{repo.Name}", EditorStyles.boldLabel);
            if (!string.IsNullOrWhiteSpace(repo.Description))
            {
                EditorGUILayout.LabelField(repo.Description, EditorStyles.wordWrappedLabel);
                EditorGUILayout.Space();
            }

            EditorGUILayout.LabelField("URL", repo.Url);
            if (!string.IsNullOrWhiteSpace(repo.DefaultBranch))
            {
                EditorGUILayout.LabelField("Default Branch", repo.DefaultBranch);
            }

            if (repo.IsPrivate)
            {
                EditorGUILayout.HelpBox("Private repositories are not supported for public submodule installs.", MessageType.Info);
            }

            selectedRepoPackageName = EditorGUILayout.TextField("Package Name", selectedRepoPackageName);
            selectedRepoBranch = EditorGUILayout.TextField("Branch", selectedRepoBranch);

            string validationError = ValidatePackageInput(repo.Url, selectedRepoPackageName);
            if (!string.IsNullOrWhiteSpace(validationError))
            {
                EditorGUILayout.HelpBox(validationError, MessageType.Warning);
            }
            else
            {
                EditorGUILayout.LabelField(PackageNameRule, EditorStyles.miniLabel);
            }

            EditorGUILayout.EndScrollView();
            GUILayout.FlexibleSpace();

            bool canAdd = !repo.IsPrivate && !repo.IsInstalled && string.IsNullOrWhiteSpace(validationError);
            using (new EditorGUI.DisabledScope(!canAdd))
            {
                if (GUILayout.Button("Add Package"))
                {
                    TryAddSubmodule(repo.Url, selectedRepoBranch, selectedRepoPackageName);
                }
            }

            EditorGUILayout.EndVertical();
        }

        private bool IsRepoVisible(GitHubRepo repo)
        {
            if (string.IsNullOrWhiteSpace(repoSearch))
            {
                return true;
            }

            string needle = repoSearch.Trim();
            string haystack = $"{repo.Owner}/{repo.Name}";
            return haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private void InitializeRepoDefaults(GitHubRepo repo)
        {
            if (GitUtility.IsValidPackageName(repo.Name))
            {
                selectedRepoPackageName = repo.Name;
            }
            else
            {
                selectedRepoPackageName = string.Empty;
            }

            selectedRepoBranch = string.IsNullOrWhiteSpace(repo.DefaultBranch) ? "main" : repo.DefaultBranch;
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
            if (!GitHubUtility.TryParseGitHubRepo(url, out _, out string repo))
            {
                return false;
            }

            if (GitUtility.IsValidPackageName(repo))
            {
                packageName = repo;
                return true;
            }

            return false;
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

            addStatus = $"Added {packageName} to {path}.";
            addStatusType = MessageType.Info;
            RefreshInstalled();
            RefreshAvailable();
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
    }
}
