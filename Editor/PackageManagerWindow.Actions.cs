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
            operationStatus = string.Empty;
            operationStatusType = MessageType.None;
            if (!GitUtility.TryRemoveSubmodule(package.Path, out string error))
            {
                installedActionStatus = error;
                installedActionStatusType = MessageType.Error;
                operationStatus = error;
                operationStatusType = MessageType.Error;
                return;
            }

            if (string.IsNullOrWhiteSpace(error))
            {
                operationStatus = $"Removed {package.PackageName ?? package.Name}. Review and commit the parent repository changes.";
                operationStatusType = MessageType.Info;
            }
            else
            {
                operationStatus = error;
                operationStatusType = MessageType.Warning;
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
                    StartAsyncOperation("Updating submodule...", GitUtility.GitExecutable,
                        $"submodule update --init --remote --merge -- {package.Path}",
                        result => OnUpdateComplete(package, result),
                        120000,
                        true);
            }
        }

        private void OnUpdateComplete(GitPackageInfo package, CommandResult result)
        {
            if (result != null && result.IsSuccess)
            {
                installedActionStatus = "Updated successfully.";
                installedActionStatusType = MessageType.Info;
                RefreshInstalled();
            }
            else
            {
                string error = GitUtility.BuildCommandError("Git update failed", result);
                installedActionStatus = error;
                installedActionStatusType = MessageType.Error;
                operationStatus = error;
                operationStatusType = MessageType.Error;
            }
        }

        private void StartAsyncOperation(
            string label,
            string fileName,
            string arguments,
            Action<CommandResult> onComplete,
            int timeoutMs = CliCommandRunner.DefaultTimeoutMs,
            bool suppressAutoRefresh = false)
        {
            if (activeOperation != null)
                return;

            if (suppressAutoRefresh)
            {
                AssetDatabase.DisallowAutoRefresh();
                activeOperationSuppressesAutoRefresh = true;
            }

            activeOperationLabel = label;
            try
            {
                activeOperation = CliCommandRunner.RunAsync(fileName, arguments, GitUtility.ProjectRoot, timeoutMs);
                activeOperationOnComplete = onComplete;
                RegisterActiveOperationPolling();
                Repaint();
            }
            catch (Exception ex)
            {
                RestoreAutoRefreshIfNeeded();
                activeOperationLabel = string.Empty;
                activeOperation = null;
                activeOperationOnComplete = null;
                onComplete?.Invoke(new CommandResult
                {
                    ExitCode = -1,
                    StdOut = string.Empty,
                    StdErr = ex.Message
                });
                Repaint();
            }
        }

        private Action<CommandResult> activeOperationOnComplete;

        private void UpdateActiveOperation()
        {
            if (activeOperation == null)
            {
                return;
            }

            if (!activeOperation.IsComplete)
            {
                if (this != null)
                    Repaint();
                return;
            }

            var onComplete = activeOperationOnComplete;
            CommandResult result = activeOperation.Result;
            activeOperationOnComplete = null;
            activeOperation = null;
            activeOperationLabel = string.Empty;
            UnregisterActiveOperationPolling();
            try
            {
                onComplete?.Invoke(result);
            }
            finally
            {
                RestoreAutoRefreshIfNeeded();
            }
            if (this != null)
                Repaint();
        }

        private void RestoreAutoRefreshIfNeeded()
        {
            if (!activeOperationSuppressesAutoRefresh)
                return;

            activeOperationSuppressesAutoRefresh = false;
            AssetDatabase.AllowAutoRefresh();
            AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate);
        }

        private void RegisterActiveOperationPolling()
        {
            if (activeOperationPollingRegistered)
                return;

            EditorApplication.update += UpdateActiveOperation;
            activeOperationPollingRegistered = true;
        }

        private void UnregisterActiveOperationPolling()
        {
            if (!activeOperationPollingRegistered)
                return;

            EditorApplication.update -= UpdateActiveOperation;
            activeOperationPollingRegistered = false;
        }

        private void RefreshDependencies()
        {
            gitAvailable = GitUtility.IsGitAvailable(out gitVersion, out gitError);
            ghAvailable = GitHubUtility.IsGhAvailable(out ghVersion, out ghError);
            if (ghAvailable)
                ghAuthenticated = GitHubUtility.IsAuthenticated(out ghAuthError);
            else
            {
                ghAuthenticated = false;
                ghAuthError = string.Empty;
            }
        }

        private void StartCliInstall(ToolKind tool, string displayName)
        {
            if (cliInstallOperation != null || activeOperation != null)
                return;

            CliInstallPlan plan = CliInstaller.GetInstallPlan(tool);
            if (!plan.CanRunAutomatically)
            {
                installStatus = plan.AutomaticInstallUnavailableReason;
                installStatusType = MessageType.Warning;
                return;
            }

            string prompt =
                $"Git Package Manager can run this command to install {displayName}:\n\n" +
                $"{plan.DisplayCommand}\n\n" +
                "This changes software installed on your computer. The command will only run if you choose Install.";
            if (plan.OpensSystemInstaller)
                prompt += " The operating system will show its own installer confirmation.";
            else
                prompt += " Your operating system may also request permission.";

            if (!EditorUtility.DisplayDialog($"Install {displayName}?", prompt, "Install", "Cancel"))
            {
                installStatus = $"{displayName} installation was cancelled.";
                installStatusType = MessageType.Info;
                return;
            }

            activeCliInstallTool = tool;
            activeCliInstallPlan = plan;
            cliInstallOperation = CliCommandRunner.RunAsync(
                plan.FileName,
                plan.Arguments,
                GitUtility.ProjectRoot,
                15 * 60 * 1000);
            installStatus = $"Installing {displayName}...";
            installStatusType = MessageType.Info;
            Repaint();
        }

        private void UpdateCliInstallOperation()
        {
            if (cliInstallOperation == null)
                return;

            if (!cliInstallOperation.IsComplete)
            {
                Repaint();
                return;
            }

            CommandResult result = cliInstallOperation.Result;
            ToolKind tool = activeCliInstallTool;
            CliInstallPlan plan = activeCliInstallPlan;
            string displayName = tool == ToolKind.Git ? "Git" : "GitHub CLI";

            cliInstallOperation = null;
            activeCliInstallPlan = null;
            RefreshDependencies();

            bool isAvailable = tool == ToolKind.Git ? gitAvailable : ghAvailable;
            if (result != null && result.IsSuccess && isAvailable)
            {
                installStatus = $"{displayName} is installed and ready.";
                installStatusType = MessageType.Info;
                RefreshCurrentTab();
            }
            else if (result != null && result.IsSuccess && plan != null && plan.OpensSystemInstaller)
            {
                installStatus = $"The {displayName} system installer was opened. Complete it, then click Check again.";
                installStatusType = MessageType.Info;
            }
            else if (result != null && result.IsSuccess)
            {
                installStatus = $"The installer completed, but Unity cannot find {displayName} yet. Click Check again or restart Unity.";
                installStatusType = MessageType.Warning;
            }
            else
            {
                installStatus = BuildCliInstallFailureMessage(displayName, result);
                installStatusType = MessageType.Error;
            }

            Repaint();
        }

        internal static string BuildCliInstallFailureMessage(string displayName, CommandResult result)
        {
            if (result == null)
                return $"{displayName} installation failed because the installer returned no result. You can retry or use the official install guide.";

            string detail = string.IsNullOrWhiteSpace(result.StdErr) ? result.StdOut : result.StdErr;
            detail = GitUtility.RedactCredentials(detail).Trim();
            const int maxDetailLength = 1200;
            if (detail.Length > maxDetailLength)
                detail = detail.Substring(0, maxDetailLength) + "…";

            string exitDescription = result.ExitCode == 0 ? string.Empty : $" (exit code {result.ExitCode})";
            if (string.IsNullOrWhiteSpace(detail))
                detail = "The installer did not provide an error message.";

            return $"{displayName} installation failed{exitDescription}: {detail} You can retry or use the official install guide.";
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
            bool hasCachedBranches = repositoryCoordinator.TryGetCachedBranches(url, out List<string> branches);
            bool isLoading = repositoryCoordinator.IsFetchingBranches(url) && !hasCachedBranches;
            bool hasError = repositoryCoordinator.TryGetBranchError(url, out string branchError);

            string buttonLabel = isLoading
                ? "Loading branches..."
                : string.IsNullOrWhiteSpace(currentBranch) ? "Select branch..." : currentBranch;
            string tooltip = isLoading
                ? "Fetching branches from remote..."
                : hasError ? $"{FirstLine(branchError)} Click to retry." : "Click to load remote branches.";

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

        private string ValidatePackageInput(string url, string packageName, string branch)
        {
            if (string.IsNullOrWhiteSpace(url))
                return "Git URL is required.";

            if (!GitUtility.IsValidRepositoryUrl(url))
                return "Git URL contains unsupported characters.";

            if (!GitUtility.IsValidPackageName(packageName))
                return PackageNameRule;

            if (!GitUtility.IsValidBranchName(branch))
                return "Branch name is invalid. Leave it empty to use the repository default.";

            string path = GetPackagePath(packageName);

            foreach (var package in installedPackages)
            {
                if (string.Equals(package.Path, path, StringComparison.OrdinalIgnoreCase))
                    return "This package is already installed.";
            }

            string fullPath = Path.Combine(GitUtility.ProjectRoot, path);
            if (Directory.Exists(fullPath) || File.Exists(fullPath))
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
            operationStatus = string.Empty;
            operationStatusType = MessageType.None;

            if (activeOperation != null)
            {
                SetAddStatus("Another Git operation is already running.", MessageType.Warning);
                return;
            }

            string validationError = ValidatePackageInput(url, packageName, branch);
            if (!string.IsNullOrWhiteSpace(validationError))
            {
                SetAddStatus(validationError, MessageType.Error);
                return;
            }

            string path = GetPackagePath(packageName);

            if (!GitUtility.TryBuildAddSubmoduleArguments(url, path, branch, out string arguments, out string gitError))
            {
                SetAddStatus(gitError, MessageType.Error);
                return;
            }

            addStatus = $"Adding {packageName}...";
            addStatusType = MessageType.Info;
            StartAsyncOperation(
                $"Adding {packageName}...",
                GitUtility.GitExecutable,
                arguments,
                result => OnAddSubmoduleComplete(result, path, packageName),
                120000,
                true);
        }

        private void OnAddSubmoduleComplete(CommandResult result, string path, string packageName)
        {
            if (result == null || !result.IsSuccess)
            {
                string message = GitUtility.BuildCommandError("Failed to add submodule", result);
                if (!GitUtility.TryCleanupFailedAdd(path, out string cleanupWarning) && !string.IsNullOrWhiteSpace(cleanupWarning))
                    message += $" Cleanup warning: {cleanupWarning}";
                SetAddStatus(message, MessageType.Error);
                RefreshInstalled();
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
            operationStatus = $"Successfully added {packageName}.";
            operationStatusType = MessageType.Info;

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
                SetAddStatus($"{message} Failed to remove submodule: {error}", MessageType.Error);
                RefreshInstalled();
                return;
            }

            string rollbackMessage = string.IsNullOrWhiteSpace(error)
                ? $"{message} The incomplete submodule was rolled back."
                : $"{message} The Git registration was rolled back, with a cleanup warning: {error}";
            SetAddStatus(rollbackMessage, MessageType.Error);
            RefreshInstalled();
        }

        private void SetAddStatus(string message, MessageType type)
        {
            addStatus = message;
            addStatusType = type;
            operationStatus = message;
            operationStatusType = type;
        }

        private static string GetPackagePath(string packageName)
        {
            return $"Packages/{packageName}";
        }
    }
}
