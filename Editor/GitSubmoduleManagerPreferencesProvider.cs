using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace MartinCalander.GitSubmoduleManager.Editor
{
    internal sealed class GitSubmoduleManagerPreferencesProvider : SettingsProvider
    {
        private string saveError = string.Empty;
        private string recoveryError = string.Empty;

        private static readonly string[] SearchKeywords =
        {
            "Git",
            "GitHub",
            "Package Manager",
            "Submodule",
            "Read-Only",
            "Dependencies",
            "Confirmation",
            "Filters",
            "Organization",
            "Public",
            "Private",
            "Welcome",
            "Install",
            "Recovery"
        };

        private static readonly string[] VisibilityLabels =
        {
            "All Repositories",
            "Public Only",
            "Private Only"
        };

        private static readonly string[] InstallModeLabels =
        {
            "Git Submodule",
            "Read-Only Package"
        };

        private GitSubmoduleManagerPreferencesProvider()
            : base("Preferences/Git Submodule Manager", SettingsScope.User)
        {
            label = "Git Submodule Manager";
            keywords = new HashSet<string>(SearchKeywords, StringComparer.OrdinalIgnoreCase);
        }

        [SettingsProvider]
        internal static SettingsProvider CreateProvider()
        {
            return new GitSubmoduleManagerPreferencesProvider();
        }

        public override void OnGUI(string searchContext)
        {
            GitSubmoduleManagerUserSettings settings = GitSubmoduleManagerUserSettings.Instance;

            EditorGUILayout.LabelField("Confirmations", EditorStyles.boldLabel);
            EditorGUI.BeginChangeCheck();

            bool suppressRoutineConfirmations = EditorGUILayout.Toggle(
                new GUIContent(
                    "Skip Routine Confirmation",
                    "Skip the second confirmation only after Git verifies that a " +
                    "submodule removal or conversion is clean and routine."),
                settings.SuppressRoutineSubmoduleRemovalConfirmations);
            EditorGUILayout.HelpBox(
                "Warnings for uncommitted, unpushed, changed, or unverified " +
                "work are safety checks and can never be suppressed.",
                MessageType.Warning);

            bool installDependenciesWithoutPrompt = EditorGUILayout.Toggle(
                new GUIContent(
                    "Install Dependencies Automatically",
                    "Install a complete, unambiguous dependency plan without showing its confirmation first."),
                settings.InstallDependenciesWithoutPrompt);
            EditorGUILayout.HelpBox(
                "Automatic dependency installation applies only when every " +
                "missing dependency has been resolved safely. Ambiguous or " +
                "unresolved dependencies still stop the install and require attention.",
                MessageType.Info);

            EditorGUILayout.Space(10f);
            EditorGUILayout.LabelField("Package Manager Defaults", EditorStyles.boldLabel);
            GitSubmoduleManagerDefaultVisibility defaultVisibility =
                (GitSubmoduleManagerDefaultVisibility)EditorGUILayout.Popup(
                    new GUIContent(
                        "Visibility",
                        "The visibility filter used when Sources > GitHub opens without an existing filter selection."),
                    (int)settings.DefaultGitHubVisibility,
                    VisibilityLabels);
            string defaultOrganization = EditorGUILayout.TextField(
                new GUIContent(
                    "Organization",
                    "A GitHub organization login to select by default. Leave empty to show every organization."),
                settings.DefaultGitHubOrganization);
            PackageManagerGitInstallMode defaultInstallMode =
                (PackageManagerGitInstallMode)EditorGUILayout.Popup(
                    new GUIContent(
                        "Install Mode",
                        "The initially selected install mode for a discovered GitHub package."),
                    (int)settings.DefaultInstallMode,
                    InstallModeLabels);
            EditorGUILayout.HelpBox(
                "Leave Organization empty for all organizations. Defaults are " +
                "only applied when the native GitHub page has no existing " +
                "filter selection.",
                MessageType.None);

            if (EditorGUI.EndChangeCheck())
            {
                settings.TryUpdatePreferences(
                    suppressRoutineConfirmations,
                    installDependenciesWithoutPrompt,
                    defaultVisibility,
                    defaultOrganization,
                    defaultInstallMode,
                    out saveError);
            }

            if (!string.IsNullOrWhiteSpace(saveError))
                EditorGUILayout.HelpBox(saveError, MessageType.Error);

            EditorGUILayout.Space(10f);
            EditorGUILayout.LabelField("Setup", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "These preferences are stored per user and project in " +
                GitSubmoduleManagerUserSettings.SettingsFilePath + ".",
                MessageType.None);

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Show Welcome", GUILayout.Width(150f)))
                GitSubmoduleManagerWelcomeWindow.Open();
            if (GUILayout.Button("Open Sources > GitHub", GUILayout.Width(180f)))
                GitSubmoduleManagerPackageManagerHost.OpenGitHubSource();
            EditorGUILayout.EndHorizontal();

            DrawRecoverySection();
        }

        private void DrawRecoverySection()
        {
            string recoveryWarning = GitOperationService.RecoveryWarning;
            if (string.IsNullOrWhiteSpace(recoveryWarning))
                return;

            EditorGUILayout.Space(10f);
            EditorGUILayout.LabelField("Repository Recovery", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                recoveryWarning,
                MessageType.Error);
            EditorGUILayout.HelpBox(
                "Inspect git status, .gitmodules, the affected package path, " +
                "and any running Git processes before acknowledging. This " +
                "safety warning cannot be suppressed by the routine-confirmation setting.",
                MessageType.Warning);

            using (new EditorGUI.DisabledScope(GitOperationService.IsBusy))
            {
                if (GUILayout.Button(
                        "Acknowledge Inspected Recovery State...",
                        GUILayout.Width(280f)) &&
                    EditorUtility.DisplayDialog(
                        "Acknowledge Repository Recovery State?",
                        "Only continue after you have inspected the parent " +
                        "repository, .gitmodules, and the affected package path. " +
                        "Acknowledging clears the retained recovery journal and " +
                        "allows repository mutations again.",
                        "I Inspected It — Acknowledge",
                        "Cancel"))
                {
                    if (!GitOperationService.TryAcknowledgeRecoveryWarning(
                            out recoveryError))
                    {
                        recoveryError = GitHubUtility.SanitizeUiDiagnostic(
                            recoveryError);
                    }
                    else
                    {
                        recoveryError = string.Empty;
                    }
                }
            }

            if (!string.IsNullOrWhiteSpace(recoveryError))
                EditorGUILayout.HelpBox(recoveryError, MessageType.Error);
        }
    }
}
