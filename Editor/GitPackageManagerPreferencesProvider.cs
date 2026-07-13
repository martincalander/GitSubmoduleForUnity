using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace MartinCalander.GitPackageManager.Editor
{
    internal sealed class GitPackageManagerPreferencesProvider : SettingsProvider
    {
        private string saveError = string.Empty;

        private static readonly string[] SearchKeywords =
        {
            "Git",
            "GitHub",
            "Package Manager",
            "Submodule",
            "Refresh",
            "Welcome",
            "Startup",
            "UPM"
        };

        private static readonly string[] StartupTabLabels =
        {
            "In Project",
            "GitHub"
        };

        private static readonly string[] GitHubFilterLabels =
        {
            "All Repositories",
            "Valid UPM Packages"
        };

        private GitPackageManagerPreferencesProvider()
            : base("Preferences/Git Package Manager", SettingsScope.User)
        {
            label = "Git Package Manager";
            keywords = new HashSet<string>(SearchKeywords, StringComparer.OrdinalIgnoreCase);
        }

        [SettingsProvider]
        internal static SettingsProvider CreateProvider()
        {
            return new GitPackageManagerPreferencesProvider();
        }

        public override void OnGUI(string searchContext)
        {
            GitPackageManagerUserSettings settings = GitPackageManagerUserSettings.instance;

            EditorGUILayout.LabelField("General", EditorStyles.boldLabel);
            EditorGUI.BeginChangeCheck();

            GitPackageManagerStartupTab startupTab =
                (GitPackageManagerStartupTab)EditorGUILayout.Popup(
                    new GUIContent(
                        "Startup Tab",
                        "The tab selected when the Git Package Manager window opens."),
                    (int)settings.StartupTab,
                    StartupTabLabels);

            bool refreshWhenRevisited = EditorGUILayout.Toggle(
                new GUIContent(
                    "Refresh In Project",
                    "Refresh installed submodules when returning to In Project after the configured interval."),
                settings.RefreshInProjectWhenRevisited);

            int intervalMinutes;
            using (new EditorGUI.DisabledScope(!refreshWhenRevisited))
            {
                intervalMinutes = EditorGUILayout.IntSlider(
                    new GUIContent(
                        "Refresh Interval (Minutes)",
                        "How stale the In Project list may be before returning to the tab refreshes it."),
                    settings.RefreshIntervalMinutes,
                    GitPackageManagerUserSettings.MinimumRefreshIntervalMinutes,
                    GitPackageManagerUserSettings.MaximumRefreshIntervalMinutes);
            }

            EditorGUILayout.Space(10f);
            EditorGUILayout.LabelField("GitHub", EditorStyles.boldLabel);
            GitPackageManagerDefaultGitHubFilter defaultFilter =
                (GitPackageManagerDefaultGitHubFilter)EditorGUILayout.Popup(
                    new GUIContent(
                        "Default Filter",
                        "The repository filter selected when the Git Package Manager window opens."),
                    (int)settings.DefaultGitHubFilter,
                    GitHubFilterLabels);
            EditorGUILayout.HelpBox(
                "Valid UPM Packages checks the root package.json files on the current GitHub page. " +
                "All Repositories starts faster and is recommended for very large accounts.",
                MessageType.Info);

            if (EditorGUI.EndChangeCheck())
            {
                settings.TryUpdatePreferences(
                    refreshWhenRevisited,
                    intervalMinutes,
                    startupTab,
                    defaultFilter,
                    out saveError);
            }

            if (!string.IsNullOrWhiteSpace(saveError))
                EditorGUILayout.HelpBox(saveError, MessageType.Error);

            EditorGUILayout.Space(10f);
            EditorGUILayout.LabelField("Setup", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "These preferences are stored per user and project in " +
                GitPackageManagerUserSettings.SettingsFilePath + ".",
                MessageType.None);

            if (GUILayout.Button("Open Welcome & Setup", GUILayout.Width(180f)))
                GitPackageManagerWindow.OpenWelcomeFromPreferences();
        }
    }
}
