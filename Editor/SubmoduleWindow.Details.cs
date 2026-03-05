using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Calander.SubmodulePackageManager.Editor
{
    public partial class GitSubmodulesWindow
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
            string displayName = submodule.PackageName ?? submodule.Name;
            GUILayout.Label(displayName, Styles.TitleLabel);
            GUILayout.Label($"{(string.IsNullOrWhiteSpace(submodule.Branch) ? "main" : submodule.Branch)} · Git Submodule", Styles.SubtitleLabel);
            EditorGUILayout.Space(4);

            EditorGUILayout.BeginHorizontal();
            if (!string.IsNullOrWhiteSpace(submodule.Url) && GUILayout.Button("Repository", Styles.LinkButton))
                Application.OpenURL(submodule.Url);
            if (GUILayout.Button("Show in Explorer", Styles.LinkButton))
                EditorUtility.RevealInFinder(Path.Combine(GitUtility.ProjectRoot, submodule.Path));
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(8);
            EditorGUILayout.BeginHorizontal();
            using (new EditorGUI.DisabledScope(!gitAvailable))
            {
                if (GUILayout.Button("Update", GUILayout.Height(24)) &&
                    EditorUtility.DisplayDialog("Update Submodule", $"Fetch and update:\n{submodule.Path}?", "Update", "Cancel"))
                {
                    PerformUpdate(submodule);
                }

                if (GUILayout.Button("Remove", GUILayout.Height(24)) &&
                    EditorUtility.DisplayDialog("Remove Submodule", $"Remove submodule at {submodule.Path}?", "Remove", "Cancel"))
                {
                    PerformRemove(submodule);
                }
            }
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(12);
            EditorGUILayout.BeginVertical(Styles.InfoBox);
            DrawInfoRow("Path", submodule.Path);
            DrawInfoRow("URL", submodule.Url);
            if (!string.IsNullOrWhiteSpace(submodule.Branch))
                DrawInfoRow("Branch", submodule.Branch);
            if (!string.IsNullOrWhiteSpace(submodule.CommitHash))
                DrawInfoRow("Commit", submodule.CommitHash.Length > 7 ? submodule.CommitHash.Substring(0, 7) : submodule.CommitHash);
            EditorGUILayout.EndVertical();

            if (!submodule.HasPackageJson)
                EditorGUILayout.HelpBox("This submodule does not contain a package.json at its root.", MessageType.Warning);
            if (!string.IsNullOrWhiteSpace(installedActionStatus))
                EditorGUILayout.HelpBox(installedActionStatus, installedActionStatusType);

            EditorGUILayout.Space(12);
            GUILayout.Label("Change Branch", Styles.SectionHeader);
            EditorGUILayout.BeginHorizontal();
            DrawBranchDropdown(submodule.Url, installedBranchInput, branch => installedBranchInput = branch);
            using (new EditorGUI.DisabledScope(!gitAvailable || string.IsNullOrWhiteSpace(installedBranchInput)))
            {
                if (GUILayout.Button("Apply", GUILayout.Width(60), GUILayout.Height(20)))
                    PerformBranchChange(submodule, installedBranchInput.Trim());
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
            if (repo.IsPrivate)
                EditorGUILayout.HelpBox("Private repository. Collaborators will need access to clone this submodule.", MessageType.Warning);
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
                using (new EditorGUI.DisabledScope(!string.IsNullOrWhiteSpace(validationError)))
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
