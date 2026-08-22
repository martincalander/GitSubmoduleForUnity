using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace MartinCalander.GitSubmoduleManager.Editor
{
    internal sealed class GitSubmoduleManagerPreferencesProvider : SettingsProvider
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

            EditorGUILayout.LabelField("General", EditorStyles.boldLabel);
            EditorGUI.BeginChangeCheck();

            GitSubmoduleManagerStartupTab startupTab =
                (GitSubmoduleManagerStartupTab)EditorGUILayout.Popup(
                    new GUIContent(
                        "Startup Tab",
                        "The tab selected when Git Submodule Manager opens inside Package Manager."),
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
                    GitSubmoduleManagerUserSettings.MinimumRefreshIntervalMinutes,
                    GitSubmoduleManagerUserSettings.MaximumRefreshIntervalMinutes);
            }

            EditorGUILayout.Space(10f);
            EditorGUILayout.LabelField("GitHub", EditorStyles.boldLabel);
            GitSubmoduleManagerDefaultGitHubFilter defaultFilter =
                (GitSubmoduleManagerDefaultGitHubFilter)EditorGUILayout.Popup(
                    new GUIContent(
                        "Default Filter",
                        "The repository filter selected when the GitHub source opens in Package Manager."),
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
                GitSubmoduleManagerUserSettings.SettingsFilePath + ".",
                MessageType.None);

            if (GUILayout.Button("Open Welcome & Setup", GUILayout.Width(180f)))
                GitSubmoduleManagerPackageManagerHost.Open(openWelcome: true);
        }
    }
}
