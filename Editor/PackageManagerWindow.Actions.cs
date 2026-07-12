using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using UnityEditor;
using UnityEngine;

namespace Essentials.GitPackageManager.Editor
{
    public partial class GitPackageManagerWindow
    {
        private sealed class InitialLoadResult
        {
            public bool GitAvailable;
            public string GitVersion = string.Empty;
            public string GitError = string.Empty;
            public bool GhAvailable;
            public string GhVersion = string.Empty;
            public string GhError = string.Empty;
            public bool GhAuthenticated;
            public string GhAuthError = string.Empty;
            public List<GitPackageInfo> Packages;
            public string PackagesError = string.Empty;
            public bool PackagesSuccess;
        }

        private void RunInitialLoad(int generation)
        {
            var result = new InitialLoadResult();
            result.GitAvailable = GitUtility.IsGitAvailable(out var gv, out var ge);
            result.GitVersion = gv;
            result.GitError = ge;
            result.GhAvailable = GitHubUtility.IsGhAvailable(out var ghv, out var ghe);
            result.GhVersion = ghv;
            result.GhError = ghe;
            if (result.GhAvailable)
            {
                result.GhAuthenticated = GitHubUtility.IsAuthenticated(out var ghae);
                result.GhAuthError = result.GhAuthenticated ? string.Empty : ghae;
            }

            if (result.GitAvailable)
            {
                result.PackagesSuccess = GitUtility.TryGetSubmodules(out var packages, out var packagesError);
                result.Packages = packages;
                result.PackagesError = packagesError;
            }

            if (generation == Volatile.Read(ref initialLoadGeneration))
                pendingLoadResult = result;
        }

        private void ApplyLoadResult(InitialLoadResult result)
        {
            gitAvailable = result.GitAvailable;
            gitVersion = result.GitVersion;
            gitError = result.GitError;
            ghAvailable = result.GhAvailable;
            ghVersion = result.GhVersion;
            ghError = result.GhError;
            ghAuthenticated = result.GhAuthenticated;
            ghAuthError = result.GhAuthError;

            if (result.GitAvailable)
            {
                if (result.PackagesSuccess)
                {
                    installedPackages = result.Packages ?? new List<GitPackageInfo>();
                }
                else
                {
                    installedStatus = result.PackagesError;
                    installedStatusType = MessageType.Error;
                    installedPackages = new List<GitPackageInfo>();
                }
            }
            else
            {
                installedStatus = "Git is required to list packages.";
                installedStatusType = MessageType.Warning;
            }

            selectedInstalledIndex = Mathf.Clamp(selectedInstalledIndex, -1, installedPackages.Count - 1);
            lastInstalledRefreshTime = EditorApplication.timeSinceStartup;
            lastRefreshDateTime = DateTime.Now;
        }

        private void PerformRemove(GitPackageInfo package)
        {
            if (!GitUtility.TryRemoveSubmodule(package.Path, out string error))
            {
                installedActionStatus = error;
                installedActionStatusType = MessageType.Error;
                return;
            }

            selectedInstalledIndex = -1;
            RefreshInstalled();
            AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate);
        }

        private void PerformBranchChange(GitPackageInfo package, string branch)
        {
            if (!GitUtility.TrySetSubmoduleBranch(package.Path, branch, out string error))
            {
                installedActionStatus = error;
                installedActionStatusType = MessageType.Error;
            }
            else
            {
                installedActionStatus = $"Branch set to {branch}.";
                installedActionStatusType = MessageType.Info;
                repositoryCoordinator.ClearBranchCache(package.Url);
                RefreshInstalled();

                if (EditorUtility.DisplayDialog("Update Package", "Update to the new branch now?", "Update", "Later"))
                    StartAsyncOperation("Updating submodule...", "git",
                        $"submodule update --init --remote --merge -- {package.Path}", () => OnUpdateComplete(package), 120000);
            }
        }

        private void OnUpdateComplete(GitPackageInfo package)
        {
            if (activeOperation != null && activeOperation.Result.IsSuccess)
            {
                installedActionStatus = "Updated successfully.";
                installedActionStatusType = MessageType.Info;
                RefreshInstalled();
            }
            else
            {
                string error = activeOperation?.Result?.StdErr ?? "Unknown error";
                installedActionStatus = $"Update failed: {error}";
                installedActionStatusType = MessageType.Error;
            }
        }

        private void StartAsyncOperation(string label, string fileName, string arguments, Action onComplete, int timeoutMs = CliCommandRunner.DefaultTimeoutMs)
        {
            activeOperationLabel = label;
            activeOperation = CliCommandRunner.RunAsync(fileName, arguments, GitUtility.ProjectRoot, timeoutMs);
            activeOperationOnComplete = onComplete;
            Repaint();
        }

        private Action activeOperationOnComplete;

        private void UpdateActiveOperation()
        {
            if (activeOperation == null)
            {
                return;
            }

            if (!activeOperation.IsComplete)
            {
                Repaint();
                return;
            }

            var onComplete = activeOperationOnComplete;
            activeOperationOnComplete = null;

            onComplete?.Invoke();

            activeOperation = null;
            activeOperationLabel = string.Empty;
            Repaint();
        }

        private void RefreshDependencies()
        {
            gitAvailable = GitUtility.IsGitAvailable(out gitVersion, out gitError);
            ghAvailable = GitHubUtility.IsGhAvailable(out ghVersion, out ghError);
            ghAuthenticated = ghAvailable && GitHubUtility.IsAuthenticated(out ghAuthError);
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
                    bool installedNeedsRefresh = installedPackages.Count == 0 ||
                        (now - lastInstalledRefreshTime) > AutoRefreshIntervalSeconds;
                    if (installedNeedsRefresh)
                        RefreshInstalled();
                    break;
                case Tab.Discover:
                    if (!discoveryCoordinator.HasResults && !discoveryCoordinator.IsLoading)
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
                installedStatus = "Git is required to list packages.";
                installedStatusType = MessageType.Warning;
                return;
            }

            if (!GitUtility.TryGetSubmodules(out installedPackages, out string error))
            {
                installedStatus = error;
                installedStatusType = MessageType.Error;
                installedPackages = new List<GitPackageInfo>();
            }

            selectedInstalledIndex = Mathf.Clamp(selectedInstalledIndex, -1, installedPackages.Count - 1);
            lastInstalledRefreshTime = EditorApplication.timeSinceStartup;
            lastRefreshDateTime = DateTime.Now;
        }

        private void RefreshAvailable()
        {
            discoverStatus = string.Empty;
            discoverStatusType = MessageType.None;

            if (!ghAvailable || !ghAuthenticated)
            {
                return;
            }

            discoveryCoordinator.EnsureUsername();
            discoveryCoordinator.LoadInitialPage();
        }

        private void UpdateDiscovery()
        {
            if (discoveryCoordinator.Tick(EditorApplication.timeSinceStartup))
            {
                if (discoveryCoordinator.PageChanged)
                {
                    MarkInstalledRepos();
                    SortRepos();
                }
                selectedRepoIndex = Mathf.Clamp(selectedRepoIndex, -1, discoveryCoordinator.DisplayedRepos.Count - 1);
                Repaint();
            }

            if (discoveryCoordinator.IsLoading)
            {
                Repaint();
            }
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
            if (installedPackages == null)
                return;
            foreach (var package in installedPackages)
            {
                if (GitHubUtility.TryParseGitHubRepo(package.Url, out string owner, out string repo))
                    installedIds.Add($"{owner}/{repo}");
            }

            foreach (var repo in discoveryCoordinator.DisplayedRepos)
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

            if (!GitUtility.IsValidRepositoryUrl(url))
                return "Git URL contains unsupported characters.";

            if (!GitUtility.IsValidPackageName(packageName))
                return PackageNameRule;

            string path = GetPackagePath(packageName);

            foreach (var package in installedPackages)
            {
                if (string.Equals(package.Path, path, StringComparison.OrdinalIgnoreCase))
                    return "This package is already installed.";
            }

            string fullPath = Path.Combine(GitUtility.ProjectRoot, path);
            if (Directory.Exists(fullPath))
                return $"Package path already exists: {path}";

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
                return;
            }

            addStatus = message;
            addStatusType = MessageType.Error;
            RefreshInstalled();
        }

        private static string GetPackagePath(string packageName)
        {
            return $"Packages/{packageName}";
        }
    }
}
