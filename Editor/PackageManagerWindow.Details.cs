using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Essentials.GitPackageManager.Editor
{
    public partial class GitPackageManagerWindow
    {
        private void DrawDetailsPane()
        {
            EditorGUILayout.BeginVertical();
            detailsScroll = EditorGUILayout.BeginScrollView(detailsScroll, GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true));
            if (currentTab == Tab.Installed)
                DrawInstalledDetails();
            else
                DrawDiscoverDetails();
            EditorGUILayout.EndScrollView();
            EditorGUILayout.EndVertical();
        }

        private void DrawInstalledDetails()
        {
            if (installedPackages == null || selectedInstalledIndex < 0 || selectedInstalledIndex >= installedPackages.Count)
            {
                EditorGUILayout.Space(40);
                GUILayout.Label("Select a package to view details", EditorStyles.centeredGreyMiniLabel);
                return;
            }

            GitPackageInfo package = installedPackages[selectedInstalledIndex];
            if (lastInstalledIndex != selectedInstalledIndex)
            {
                installedBranchInput = string.IsNullOrWhiteSpace(package.Branch) ? "main" : package.Branch;
                installedActionStatus = string.Empty;
                installedActionStatusType = MessageType.None;
                lastInstalledIndex = selectedInstalledIndex;
            }

            EditorGUILayout.Space(8);
            string displayName = package.PackageName ?? package.Name;
            const string typeLabel = "Git Submodule";
            GUILayout.Label(displayName, Styles.TitleLabel);
            GUILayout.Label($"{(string.IsNullOrWhiteSpace(package.Branch) ? "main" : package.Branch)} · {typeLabel}", Styles.SubtitleLabel);
            EditorGUILayout.Space(4);

            EditorGUILayout.BeginHorizontal();
            if (!string.IsNullOrWhiteSpace(package.Url) && GUILayout.Button("Repository", Styles.LinkButton))
                Application.OpenURL(package.Url);
            if (GUILayout.Button("Show in Explorer", Styles.LinkButton))
                EditorUtility.RevealInFinder(Path.Combine(GitUtility.ProjectRoot, package.Path));
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(8);
            bool isOperationRunning = activeOperation != null;
            EditorGUILayout.BeginHorizontal();
            using (new EditorGUI.DisabledScope(!gitAvailable || isOperationRunning))
            {
                string updateLabel = package.IsInitialized ? "Update" : "Initialize";
                if (GUILayout.Button(updateLabel, GUILayout.Height(24)) &&
                    EditorUtility.DisplayDialog($"{updateLabel} Submodule", $"{updateLabel}:\n{package.Path}?", updateLabel, "Cancel"))
                {
                    StartAsyncOperation($"{updateLabel} submodule...", "git",
                        $"submodule update --init --remote --merge -- {package.Path}", () => OnUpdateComplete(package), 120000);
                }

                if (GUILayout.Button("Remove", GUILayout.Height(24)) &&
                    EditorUtility.DisplayDialog("Remove Package", $"Remove {typeLabel.ToLower()} at {package.Path}?", "Remove", "Cancel"))
                {
                    PerformRemove(package);
                }
            }
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(12);
            EditorGUILayout.BeginVertical(Styles.InfoBox);
            DrawInfoRow("Type", typeLabel);
            DrawInfoRow("Status", package.IsInitialized ? "Initialized" : "Not initialized");
            DrawInfoRow("Path", package.Path);
            DrawInfoRow("URL", package.Url);
            if (!string.IsNullOrWhiteSpace(package.Branch))
                DrawInfoRow("Branch", package.Branch);
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

        private void DrawDiscoverDetails()
        {
            var availableRepos = discoveryCoordinator.DisplayedRepos;
            if (availableRepos == null || selectedRepoIndex < 0 || selectedRepoIndex >= availableRepos.Count)
            {
                EditorGUILayout.Space(40);
                GUILayout.Label("Select a repository to view details", EditorStyles.centeredGreyMiniLabel);
                return;
            }

            GitHubRepo repo = availableRepos[selectedRepoIndex];
            discoveryCoordinator.CheckPackageJson(repo);

            EditorGUILayout.Space(8);
            GUILayout.Label(repo.Name, Styles.TitleLabel);
            GUILayout.Label(!string.IsNullOrWhiteSpace(repo.Description) ? repo.Description : $"Repository by {repo.Owner}", Styles.SubtitleLabel);
            EditorGUILayout.Space(4);

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("View on GitHub", Styles.LinkButton))
                Application.OpenURL(repo.Url);
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(8);
            EditorGUILayout.BeginVertical(Styles.InfoBox);
            DrawInfoRow("Owner", repo.Owner);
            DrawInfoRow("URL", repo.Url);
            if (!string.IsNullOrWhiteSpace(repo.DefaultBranch))
                DrawInfoRow("Default Branch", repo.DefaultBranch);
            DrawInfoRow("Visibility", repo.IsPrivate ? "Private" : "Public");
            DrawInfoRow("Unity Package", !repo.PackageJsonChecked ? "Checking..." : repo.HasPackageJson ? "Yes" : "No");
            EditorGUILayout.EndVertical();

            if (repo.PackageJsonChecked && !repo.HasPackageJson)
                EditorGUILayout.HelpBox("This repository does not contain a package.json at its root. It may not be a valid Unity package.", MessageType.Warning);
            else if (!string.IsNullOrWhiteSpace(repo.PackageJsonError))
                EditorGUILayout.HelpBox(repo.PackageJsonError + " Refresh the page to retry.", MessageType.Warning);
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

                string validationError = ValidatePackageInput(repo.Url, selectedRepoPackageName);
                if (!string.IsNullOrWhiteSpace(validationError))
                    EditorGUILayout.HelpBox(validationError, MessageType.Warning);
                else
                    GUILayout.Label(PackageNameRule, Styles.FooterLabel);

                EditorGUILayout.Space(8);
                bool isOperationRunning = activeOperation != null;
                using (new EditorGUI.DisabledScope(!string.IsNullOrWhiteSpace(validationError) || isOperationRunning))
                {
                    if (GUILayout.Button("Add Package", GUILayout.Height(28)))
                        TryAddSubmodule(repo.Url, selectedRepoBranch, selectedRepoPackageName);
                }
            }

            if (!string.IsNullOrWhiteSpace(addStatus))
                EditorGUILayout.HelpBox(addStatus, addStatusType);
        }

        private void DrawInfoRow(string label, string value)
        {
            EditorGUILayout.BeginHorizontal();
            GUILayout.Label(label, Styles.InfoLabel, GUILayout.Width(100));
            EditorGUILayout.SelectableLabel(value, Styles.InfoValue, GUILayout.Height(16));
            EditorGUILayout.EndHorizontal();
        }
    }
}
