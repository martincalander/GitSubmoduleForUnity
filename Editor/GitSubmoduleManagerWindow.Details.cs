using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace MartinCalander.GitSubmoduleManager.Editor
{
    public partial class GitSubmoduleManagerWindow
    {
        private string installedDetailsIdentity = string.Empty;
        private string installedDetailsSourceBranch = string.Empty;
        private string detailsScrollIdentity = string.Empty;

        private void DrawDetailsPane()
        {
            string nextScrollIdentity = GetDetailsScrollIdentity();
            if (!string.Equals(detailsScrollIdentity, nextScrollIdentity, StringComparison.Ordinal))
            {
                detailsScroll = Vector2.zero;
                detailsScrollIdentity = nextScrollIdentity;
            }

            EditorGUILayout.BeginVertical();
            detailsScroll = EditorGUILayout.BeginScrollView(detailsScroll, GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true));
            if (currentTab == Tab.Installed)
                DrawInstalledDetails();
            else
                DrawDiscoverDetails();
            EditorGUILayout.EndScrollView();
            EditorGUILayout.EndVertical();
        }

        private string GetDetailsScrollIdentity()
        {
            if (currentTab == Tab.Installed)
            {
                if (installedPackages == null ||
                    selectedInstalledIndex < 0 ||
                    selectedInstalledIndex >= installedPackages.Count)
                {
                    return "installed:none";
                }

                return "installed:" + BuildInstalledPackageIdentity(installedPackages[selectedInstalledIndex]);
            }

            var repositories = discoveryCoordinator.DisplayedRepos;
            if (repositories == null || selectedRepoIndex < 0 || selectedRepoIndex >= repositories.Count)
                return "discover:none";

            GitHubRepo repository = repositories[selectedRepoIndex];
            return "discover:" +
                   (repository?.Url ??
                    ((repository?.Owner ?? string.Empty) + "/" + (repository?.Name ?? string.Empty)));
        }

        private void DrawInstalledDetails()
        {
            if (installedPackages == null || selectedInstalledIndex < 0 || selectedInstalledIndex >= installedPackages.Count)
            {
                installedDetailsIdentity = string.Empty;
                installedDetailsSourceBranch = string.Empty;
                EditorGUILayout.Space(40);
                GUILayout.Label("Select a package to view details", EditorStyles.centeredGreyMiniLabel);
                return;
            }

            GitPackageInfo package = installedPackages[selectedInstalledIndex];
            string packageIdentity = BuildInstalledPackageIdentity(package);
            string sourceBranch = NormalizeInstalledBranch(package.Branch);
            if (!string.Equals(installedDetailsIdentity, packageIdentity, StringComparison.Ordinal) ||
                !string.Equals(installedDetailsSourceBranch, sourceBranch, StringComparison.Ordinal))
            {
                installedBranchInput = sourceBranch;
                installedActionStatus = string.Empty;
                installedActionStatusType = MessageType.None;
                installedDetailsIdentity = packageIdentity;
                installedDetailsSourceBranch = sourceBranch;
            }

            EditorGUILayout.Space(8);
            string displayName = package.PackageName ?? package.Name;
            const string typeLabel = "Git Submodule";
            string branchLabel = GetInstalledBranchLabel(package.Branch);
            GUILayout.Label(displayName, Styles.TitleLabel);
            GUILayout.Label($"{branchLabel} · {typeLabel}", Styles.SubtitleLabel);
            EditorGUILayout.Space(4);

            EditorGUILayout.BeginHorizontal();
            if (GitUtility.TryGetRepositoryWebUrl(package.Url, out string repositoryWebUrl) &&
                GUILayout.Button("Repository", Styles.LinkButton))
                Application.OpenURL(repositoryWebUrl);
            if (GUILayout.Button("Show in Explorer", Styles.LinkButton))
                EditorUtility.RevealInFinder(Path.Combine(GitUtility.ProjectRoot, package.Path));
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(8);
            bool isOperationRunning = IsRepositoryOperationBusy;
            bool isCurrentPackage = IsCurrentPackage(package);
            EditorGUILayout.BeginHorizontal();
            using (new EditorGUI.DisabledScope(!gitAvailable || isOperationRunning))
            {
                string updateLabel = package.IsInitialized ? "Update" : "Initialize";
                if (GUILayout.Button(updateLabel, GUILayout.Height(24)))
                {
                    if (package.IsInitialized)
                        BeginUpdate(package);
                    else if (EditorUtility.DisplayDialog(
                                 "Initialize Submodule",
                                 $"Initialize {package.Path} at the exact commit pinned by the parent repository?",
                                 "Initialize",
                                 "Cancel"))
                        BeginInitialize(package);
                }

                if (GUILayout.Button("Remove", GUILayout.Height(24)))
                {
                    GitPackageInfo capturedPackage = package;
                    EditorApplication.delayCall += () => BeginRemove(capturedPackage, typeLabel);
                }
            }
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();

            if (isCurrentPackage)
            {
                EditorGUILayout.Space(6);
                EditorGUILayout.HelpBox(
                    "This is Git Submodule Manager itself. Removing it will unload this editor tool and close this window after Unity refreshes. You will need to reinstall it through UPM to use it again.",
                    MessageType.Warning);
            }

            EditorGUILayout.Space(12);
            EditorGUILayout.BeginVertical(Styles.InfoBox);
            DrawInfoRow("Type", typeLabel);
            DrawInfoRow("Status", package.IsInitialized ? "Initialized" : "Not initialized");
            DrawInfoRow("Path", package.Path);
            DrawInfoRow("URL", GitUtility.FormatRepositoryUrlForDisplay(package.Url));
            DrawInfoRow("Branch", branchLabel);
            if (!string.IsNullOrWhiteSpace(package.CommitHash))
                DrawInfoRow("Commit", package.CommitHash.Length > 7 ? package.CommitHash.Substring(0, 7) : package.CommitHash);
            EditorGUILayout.EndVertical();

            if (!package.HasPackageJson)
                EditorGUILayout.HelpBox(package.IsInitialized
                    ? "This package does not contain a package.json at its root."
                    : "This submodule has not been initialized yet.", MessageType.Warning);
            if (!string.IsNullOrWhiteSpace(installedActionStatus))
                EditorGUILayout.HelpBox(installedActionStatus, installedActionStatusType);

            EditorGUILayout.Space(12);
            GUILayout.Label("Change Branch", Styles.SectionHeader);
            EditorGUILayout.BeginHorizontal();
            DrawBranchDropdown(package.Url, installedBranchInput, branch => installedBranchInput = branch);
            using (new EditorGUI.DisabledScope(!gitAvailable || string.IsNullOrWhiteSpace(installedBranchInput) || isOperationRunning))
            {
                if (GUILayout.Button("Apply", GUILayout.Width(60), GUILayout.Height(20)))
                    PerformBranchChange(package, installedBranchInput.Trim());
            }
            EditorGUILayout.EndHorizontal();
        }

        internal static bool IsCurrentPackage(GitPackageInfo package)
        {
            if (package == null)
                return false;

            string packageName = package.PackageName?.Trim();
            string packagePath = GitUtility.NormalizePath(package.Path);
            string installedPath = string.Empty;
            try
            {
                installedPath = GitUtility.NormalizePath(
                    UnityEditor.PackageManager.PackageInfo.FindForAssembly(
                        typeof(GitSubmoduleManagerWindow).Assembly)?.assetPath);
            }
            catch
            {
                // The canonical and legacy identities below remain safe fallbacks
                // if Package Manager cannot resolve the currently loaded assembly.
            }

            return string.Equals(packageName, CurrentPackageName, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(packageName, LegacyPackageName, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(packagePath, CurrentPackagePath, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(packagePath, LegacyPackagePath, StringComparison.OrdinalIgnoreCase) ||
                   (!string.IsNullOrWhiteSpace(installedPath) &&
                    string.Equals(packagePath, installedPath, StringComparison.OrdinalIgnoreCase));
        }

        internal static string NormalizeInstalledBranch(string branch)
        {
            return string.IsNullOrWhiteSpace(branch) ? string.Empty : branch.Trim();
        }

        internal static string GetInstalledBranchLabel(string branch)
        {
            string normalizedBranch = NormalizeInstalledBranch(branch);
            return string.IsNullOrEmpty(normalizedBranch) ? "repository default" : normalizedBranch;
        }

        internal static string BuildInstalledPackageIdentity(GitPackageInfo package)
        {
            if (package == null)
                return string.Empty;

            string normalizedPath = GitUtility.NormalizePath(package.Path);
            string packageUrl = package.Url ?? string.Empty;
            string repositoryIdentity;
            if (!GitUtility.IsValidRepositoryUrl(packageUrl))
            {
                repositoryIdentity = "invalid:" + GitUtility.FormatRepositoryUrlForDisplay(packageUrl);
            }
            else if (GitHubUtility.TryParseGitHubRepo(packageUrl, out string owner, out string repository))
            {
                repositoryIdentity =
                    "github:" + owner.ToLowerInvariant() + "/" + repository.ToLowerInvariant();
            }
            else
            {
                repositoryIdentity = GitHubUtility.GetRepositoryCacheIdentity(packageUrl);
            }
            if (string.IsNullOrWhiteSpace(repositoryIdentity))
                repositoryIdentity = GitUtility.FormatRepositoryUrlForDisplay(packageUrl);

            return normalizedPath + "\n" + repositoryIdentity;
        }

        internal static string BuildSelfRemovalWarning(string path)
        {
            string packagePath = string.IsNullOrWhiteSpace(path)
                ? CurrentPackagePath
                : GitUtility.NormalizePath(path);
            return
                $"You are about to remove Git Submodule Manager itself at:\n{packagePath}\n\n" +
                "This editor tool will be unloaded and this window will close when Unity refreshes. " +
                "You will need to reinstall the package through UPM to manage submodules with it again.\n\n" +
                "The parent repository changes will still need to be reviewed and committed.\n\n" +
                "Are you absolutely sure?";
        }

        private static bool ConfirmPackageRemoval(GitPackageInfo package, string typeLabel)
        {
            if (!EditorUtility.DisplayDialog(
                    "Remove Package",
                    $"Remove {typeLabel.ToLower()} at {package.Path}?",
                    "Remove",
                    "Cancel"))
            {
                return false;
            }

            return !IsCurrentPackage(package) || EditorUtility.DisplayDialog(
                "Remove Git Submodule Manager Itself?",
                BuildSelfRemovalWarning(package.Path),
                "Remove This Tool",
                "Keep Installed");
        }

        private void DrawDiscoverDetails()
        {
            var availableRepos = discoveryCoordinator.DisplayedRepos;
            if (availableRepos == null || selectedRepoIndex < 0 || selectedRepoIndex >= availableRepos.Count)
            {
                selectedRepoManifestDefaultsSource = null;
                selectedRepoDeclaredNameApplied = false;
                EditorGUILayout.Space(40);
                GUILayout.Label("Select a repository to view details", EditorStyles.centeredGreyMiniLabel);
                return;
            }

            GitHubRepo repo = availableRepos[selectedRepoIndex];
            discoveryCoordinator.CheckPackageJson(repo);
            ApplyDeclaredPackageName(repo);

            EditorGUILayout.Space(8);
            GUILayout.Label(repo.Name, Styles.TitleLabel);
            GUILayout.Label(!string.IsNullOrWhiteSpace(repo.Description) ? repo.Description : $"Repository by {repo.Owner}", Styles.SubtitleLabel);
            EditorGUILayout.Space(4);

            EditorGUILayout.BeginHorizontal();
            if (GitUtility.TryGetRepositoryWebUrl(repo.Url, out string githubWebUrl) &&
                GUILayout.Button("View on GitHub", Styles.LinkButton))
                Application.OpenURL(githubWebUrl);
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(8);
            EditorGUILayout.BeginVertical(Styles.InfoBox);
            DrawInfoRow("Owner", repo.Owner);
            DrawInfoRow("URL", repo.Url);
            if (!string.IsNullOrWhiteSpace(repo.DefaultBranch))
                DrawInfoRow("Default Branch", repo.DefaultBranch);
            DrawInfoRow("Visibility", repo.IsPrivate ? "Private" : "Public");
            DrawInfoRow("Unity Package", GetPackageManifestStatus(repo.ManifestState));
            if (repo.ManifestState == PackageManifestState.Valid &&
                !string.IsNullOrWhiteSpace(repo.DeclaredPackageName))
            {
                DrawInfoRow("Package Name", repo.DeclaredPackageName);
            }
            EditorGUILayout.EndVertical();

            DrawPackageManifestMessage(repo);
            if (repo.IsPrivate)
                EditorGUILayout.HelpBox("Private repository. Collaborators will need access to clone this package.", MessageType.Warning);
            if (repo.IsInstalled)
                EditorGUILayout.HelpBox("This repository is already installed.", MessageType.Info);

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
                DrawBranchDropdown(repo.Url, selectedRepoBranch, branch => selectedRepoBranch = branch);
                EditorGUILayout.EndHorizontal();
                EditorGUILayout.EndVertical();

                string validationError = ValidatePackageInput(repo.Url, selectedRepoPackageName, selectedRepoBranch);
                if (!string.IsNullOrWhiteSpace(validationError))
                    EditorGUILayout.HelpBox(validationError, MessageType.Warning);
                else
                    GUILayout.Label(PackageNameRule, Styles.FooterLabel);

                EditorGUILayout.Space(8);
                bool isOperationRunning = IsRepositoryOperationBusy;
                using (new EditorGUI.DisabledScope(!string.IsNullOrWhiteSpace(validationError) || isOperationRunning))
                {
                    if (GUILayout.Button("Add Package", GUILayout.Height(28)))
                        TryAddSubmodule(repo.Url, selectedRepoBranch, selectedRepoPackageName);
                }
            }

            if (!string.IsNullOrWhiteSpace(addStatus))
                EditorGUILayout.HelpBox(addStatus, addStatusType);
        }

        private void ApplyDeclaredPackageName(GitHubRepo repo)
        {
            if (!ReferenceEquals(selectedRepoManifestDefaultsSource, repo))
            {
                selectedRepoManifestDefaultsSource = repo;
                selectedRepoDeclaredNameApplied = false;
            }

            if (selectedRepoDeclaredNameApplied || string.IsNullOrWhiteSpace(repo.DeclaredPackageName))
                return;

            string suggestedPackageName = GitHubUtility.DerivePackageNameSuggestion(repo.Owner, repo.Name);
            if (string.IsNullOrWhiteSpace(selectedRepoPackageName) ||
                string.Equals(selectedRepoPackageName, suggestedPackageName, StringComparison.OrdinalIgnoreCase))
            {
                selectedRepoPackageName = repo.DeclaredPackageName.Trim();
            }

            selectedRepoDeclaredNameApplied = true;
        }

        private static string GetPackageManifestStatus(PackageManifestState state)
        {
            return state switch
            {
                PackageManifestState.Checking => "Checking...",
                PackageManifestState.Valid => "Valid",
                PackageManifestState.Missing => "Missing",
                PackageManifestState.Invalid => "Invalid",
                PackageManifestState.Unavailable => "Unavailable",
                _ => "Not checked"
            };
        }

        private void DrawPackageManifestMessage(GitHubRepo repo)
        {
            switch (repo.ManifestState)
            {
                case PackageManifestState.Checking:
                    DrawLoadingState(
                        "Loading package manifest...",
                        "Checking the repository's root package.json.",
                        topSpacing: 0f);
                    break;
                case PackageManifestState.Missing:
                    EditorGUILayout.HelpBox(
                        string.IsNullOrWhiteSpace(repo.PackageManifestMessage)
                            ? "This repository does not contain a package.json at its root."
                            : repo.PackageManifestMessage,
                        MessageType.Warning);
                    break;
                case PackageManifestState.Invalid:
                    EditorGUILayout.HelpBox(
                        string.IsNullOrWhiteSpace(repo.PackageManifestMessage)
                            ? "The root package.json is not a valid UPM package manifest."
                            : repo.PackageManifestMessage,
                        MessageType.Warning);
                    break;
                case PackageManifestState.Unavailable:
                    string message = string.IsNullOrWhiteSpace(repo.PackageManifestMessage)
                        ? "The root package.json could not be checked."
                        : repo.PackageManifestMessage;
                    if (message.IndexOf("retry", StringComparison.OrdinalIgnoreCase) < 0)
                        message += " Refresh the page to retry.";
                    EditorGUILayout.HelpBox(message, MessageType.Warning);
                    break;
            }
        }

        private void DrawInfoRow(string label, string value)
        {
            const float labelWidth = 100f;
            const float detailsChromeWidth = 48f;
            float valueWidth = Mathf.Max(
                80f,
                position.width - ListPaneWidth - labelWidth - detailsChromeWidth);
            float rowHeight = Mathf.Max(
                EditorGUIUtility.singleLineHeight,
                Styles.InfoValue.CalcHeight(new GUIContent(value ?? string.Empty), valueWidth));

            EditorGUILayout.BeginHorizontal();
            GUILayout.Label(label, Styles.InfoLabel, GUILayout.Width(labelWidth), GUILayout.Height(rowHeight));
            EditorGUILayout.SelectableLabel(
                value ?? string.Empty,
                Styles.InfoValue,
                GUILayout.Height(rowHeight));
            EditorGUILayout.EndHorizontal();
        }
    }
}
