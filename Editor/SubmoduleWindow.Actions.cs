using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Calander.SubmodulePackageManager.Editor
{
    public partial class GitSubmodulesWindow
    {
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
                repositoryCoordinator.ClearBranchCache(submodule.Url);
                RefreshInstalled();

                if (EditorUtility.DisplayDialog("Update Submodule", "Update to the new branch now?", "Update", "Later"))
                    PerformUpdate(submodule);
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

            if (CliInstaller.TryInstallGh(out _, out string error))
            {
                RefreshDependencies();

                if (ghAvailable)
                {
                    installStatus = "GitHub CLI installed successfully. Run 'gh auth login' in terminal to authenticate.";
                    installStatusType = MessageType.Info;
                }
                else
                {
                    installStatus = "GitHub CLI install finished but could not be detected. You may need to restart Unity.";
                    installStatusType = MessageType.Warning;
                }
            }
            else
            {
                installStatus = string.IsNullOrWhiteSpace(error) ? "GitHub CLI installation failed." : error.Trim();
                installStatusType = MessageType.Error;
                RefreshDependencies();
            }

            Repaint();
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
                        RefreshInstalled();
                    break;
                case Tab.Discover:
                    bool discoverNeedsRefresh = availableRepos.Count == 0 ||
                        (now - lastDiscoverRefreshTime) > AutoRefreshIntervalSeconds;
                    if (discoverNeedsRefresh && !repositoryCoordinator.IsLoadingRepos)
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
            lastInstalledRefreshTime = EditorApplication.timeSinceStartup;
            lastRefreshDateTime = DateTime.Now;
        }

        private void RefreshAvailable()
        {
            discoverStatus = string.Empty;
            discoverStatusType = MessageType.None;

            if (!ghAvailable || !ghAuthenticated)
            {
                availableRepos = new List<GitHubRepo>();
                return;
            }

            repositoryCoordinator.BeginRefreshAvailable();
        }

        private void UpdateRepoLoading()
        {
            if (repositoryCoordinator.TickRefreshAvailable(out List<GitHubRepo> repos, out string error))
            {
                if (!string.IsNullOrWhiteSpace(error))
                {
                    discoverStatus = error;
                    discoverStatusType = MessageType.Error;
                    availableRepos = new List<GitHubRepo>();
                    return;
                }

                availableRepos = repos;
                MarkInstalledRepos();
                SortRepos();
                selectedRepoIndex = Mathf.Clamp(selectedRepoIndex, -1, availableRepos.Count - 1);
                lastDiscoverRefreshTime = EditorApplication.timeSinceStartup;
                lastRefreshDateTime = DateTime.Now;
                StartPackageJsonChecking();
                Repaint();
                return;
            }

            if (repositoryCoordinator.IsLoadingRepos)
            {
                Repaint();
                return;
            }

            UpdatePackageJsonChecking();
        }

        private void StartPackageJsonChecking()
        {
            if (availableRepos == null || availableRepos.Count == 0)
                return;

            repositoryCoordinator.BeginPackageJsonChecks(availableRepos);
        }

        private void UpdatePackageJsonChecking()
        {
            if (repositoryCoordinator.TickPackageJsonChecks())
                Repaint();
        }

        private void FetchBranchesForUrl(string url)
        {
            repositoryCoordinator.RequestBranches(url);
        }

        private void UpdateBranchFetching()
        {
            if (repositoryCoordinator.TickBranchFetch())
                Repaint();
        }

        private void DrawBranchDropdown(string url, string currentBranch, Action<string> onBranchSelected)
        {
            FetchBranchesForUrl(url);

            bool hasCachedBranches = repositoryCoordinator.TryGetCachedBranches(url, out List<string> branches);
            bool isLoading = repositoryCoordinator.IsFetchingBranches(url) && !hasCachedBranches;

            string buttonLabel = string.IsNullOrWhiteSpace(currentBranch) ? "Select branch..." : currentBranch;
            string tooltip = isLoading ? "Fetching branches from remote..." : string.Empty;

            using (new EditorGUI.DisabledScope(isLoading))
            {
                Rect dropdownRect = GUILayoutUtility.GetRect(new GUIContent(buttonLabel), EditorStyles.popup, GUILayout.Height(20));
                if (!EditorGUI.DropdownButton(dropdownRect, new GUIContent(buttonLabel, tooltip), FocusType.Passive, EditorStyles.popup))
                    return;

                if (hasCachedBranches)
                {
                    var menu = new GenericMenu();
                    foreach (string branch in branches)
                    {
                        string branchCapture = branch;
                        bool isActive = string.Equals(branch, currentBranch, StringComparison.OrdinalIgnoreCase);
                        menu.AddItem(new GUIContent(branch), isActive, () =>
                        {
                            onBranchSelected?.Invoke(branchCapture);
                            Repaint();
                        });
                    }
                    menu.DropDown(dropdownRect);
                    return;
                }

                repositoryCoordinator.ClearBranchCache(url);
                FetchBranchesForUrl(url);
            }
        }

        private void MarkInstalledRepos()
        {
            var installedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var submodule in installedSubmodules)
            {
                if (GitHubUtility.TryParseGitHubRepo(submodule.Url, out string owner, out string repo))
                    installedIds.Add($"{owner}/{repo}");
            }

            foreach (var repo in availableRepos)
                repo.IsInstalled = installedIds.Contains($"{repo.Owner}/{repo.Name}");
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
                return "Git URL is required.";

            if (!GitUtility.IsValidPackageName(packageName))
                return PackageNameRule;

            string path = GetPackagePath(packageName);
            string fullPath = Path.Combine(GitUtility.ProjectRoot, path);
            if (Directory.Exists(fullPath))
                return $"Package path already exists: {path}";

            foreach (var submodule in installedSubmodules)
            {
                if (string.Equals(submodule.Path, path, StringComparison.OrdinalIgnoreCase))
                    return "A submodule already exists at this path.";
            }

            return string.Empty;
        }

        private bool TryDerivePackageNameFromUrl(string url, out string packageName)
        {
            packageName = string.Empty;
            if (!GitHubUtility.TryParseGitHubRepo(url, out string owner, out string repo))
                return false;

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

            addStatus = $"Successfully added {packageName}. Refreshing assets...";
            addStatusType = MessageType.Info;

            AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate);
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
    }
}
