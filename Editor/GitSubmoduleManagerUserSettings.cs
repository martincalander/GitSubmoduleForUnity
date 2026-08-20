using System;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Scripting.APIUpdating;

namespace MartinCalander.GitSubmoduleManager.Editor
{
    internal enum GitSubmoduleManagerStartupTab
    {
        InProject,
        GitHub
    }

    internal enum GitSubmoduleManagerDefaultGitHubFilter
    {
        AllRepositories,
        ValidUpmPackages
    }

    [MovedFrom(
        true,
        sourceNamespace: "MartinCalander.GitPackageManager.Editor",
        sourceAssembly: "MartinCalander.GitPackageManager.Editor",
        sourceClassName: "GitPackageManagerUserSettings")]
    [FilePath(
        SettingsFilePath,
        FilePathAttribute.Location.ProjectFolder)]
    internal sealed class GitSubmoduleManagerUserSettings : ScriptableSingleton<GitSubmoduleManagerUserSettings>
    {
        internal const string SettingsFilePath =
            "UserSettings/GitSubmoduleManagerSettings.asset";
        internal const string LegacySettingsFilePath =
            "UserSettings/GitPackageManagerSettings.asset";
        internal const int DefaultRefreshIntervalMinutes = 5;
        internal const int MinimumRefreshIntervalMinutes = 1;
        internal const int MaximumRefreshIntervalMinutes = 60;

        private static bool settingsMigrationAttempted;

        internal static GitSubmoduleManagerUserSettings Instance
        {
            get
            {
                EnsureLegacySettingsMigrated();
                return instance;
            }
        }

        [SerializeField]
        private bool welcomeShown;

        [SerializeField]
        private bool refreshInProjectWhenRevisited = true;

        [SerializeField]
        private int refreshIntervalMinutes = DefaultRefreshIntervalMinutes;

        [SerializeField]
        private GitSubmoduleManagerStartupTab startupTab = GitSubmoduleManagerStartupTab.InProject;

        [SerializeField]
        private GitSubmoduleManagerDefaultGitHubFilter defaultGitHubFilter =
            GitSubmoduleManagerDefaultGitHubFilter.AllRepositories;

        internal bool HasShownWelcome => welcomeShown;
        internal bool RefreshInProjectWhenRevisited => refreshInProjectWhenRevisited;
        internal int RefreshIntervalMinutes => ClampRefreshIntervalMinutes(refreshIntervalMinutes);
        internal double RefreshIntervalSeconds => RefreshIntervalMinutes * 60.0;
        internal GitSubmoduleManagerStartupTab StartupTab => NormalizeStartupTab(startupTab);
        internal GitSubmoduleManagerDefaultGitHubFilter DefaultGitHubFilter =>
            NormalizeDefaultGitHubFilter(defaultGitHubFilter);

        internal static bool ShouldShowWelcome(bool hasShownWelcome, bool shownThisSession)
        {
            return !hasShownWelcome && !shownThisSession;
        }

        internal static bool TryMigrateLegacySettingsFile(string projectRoot, out string error)
        {
            error = string.Empty;
            string temporaryPath = string.Empty;
            if (string.IsNullOrWhiteSpace(projectRoot))
            {
                error = "The Unity project root was not provided.";
                return false;
            }

            try
            {
                string legacyPath = Path.GetFullPath(Path.Combine(projectRoot, LegacySettingsFilePath));
                string currentPath = Path.GetFullPath(Path.Combine(projectRoot, SettingsFilePath));
                if (File.Exists(currentPath) || !File.Exists(legacyPath))
                    return true;

                if ((File.GetAttributes(legacyPath) & FileAttributes.ReparsePoint) != 0)
                {
                    error = "The legacy settings file is a symbolic link or reparse point and was not copied.";
                    return false;
                }

                string settingsDirectory = Path.GetDirectoryName(currentPath);
                if (string.IsNullOrWhiteSpace(settingsDirectory))
                {
                    error = "The renamed settings directory could not be resolved.";
                    return false;
                }

                Directory.CreateDirectory(settingsDirectory);
                temporaryPath = currentPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                File.Copy(legacyPath, temporaryPath, false);
                File.Move(temporaryPath, currentPath);
                return true;
            }
            catch (Exception exception)
            {
                error = exception.Message;
                return false;
            }
            finally
            {
                try
                {
                    if (!string.IsNullOrWhiteSpace(temporaryPath) && File.Exists(temporaryPath))
                        File.Delete(temporaryPath);
                }
                catch
                {
                    // A same-directory temporary copy is harmless and must not
                    // hide the migration error or affect the intact legacy file.
                }
            }
        }

        private static void EnsureLegacySettingsMigrated()
        {
            if (settingsMigrationAttempted)
                return;

            settingsMigrationAttempted = true;
            string projectRoot = Directory.GetParent(Application.dataPath)?.FullName;
            if (!TryMigrateLegacySettingsFile(projectRoot, out string error))
            {
                Debug.LogWarning(
                    "[Git Submodule Manager] Saved preferences could not be copied from the legacy settings " +
                    "path. The original file was preserved: " + error);
            }
        }

        internal static int ClampRefreshIntervalMinutes(int minutes)
        {
            return Mathf.Clamp(
                minutes,
                MinimumRefreshIntervalMinutes,
                MaximumRefreshIntervalMinutes);
        }

        internal static GitSubmoduleManagerStartupTab NormalizeStartupTab(
            GitSubmoduleManagerStartupTab value)
        {
            return value == GitSubmoduleManagerStartupTab.InProject ||
                   value == GitSubmoduleManagerStartupTab.GitHub
                ? value
                : GitSubmoduleManagerStartupTab.InProject;
        }

        internal static GitSubmoduleManagerDefaultGitHubFilter NormalizeDefaultGitHubFilter(
            GitSubmoduleManagerDefaultGitHubFilter value)
        {
            return value == GitSubmoduleManagerDefaultGitHubFilter.AllRepositories ||
                   value == GitSubmoduleManagerDefaultGitHubFilter.ValidUpmPackages
                ? value
                : GitSubmoduleManagerDefaultGitHubFilter.AllRepositories;
        }

        internal bool TryUpdatePreferences(
            bool refreshWhenRevisited,
            int intervalMinutes,
            GitSubmoduleManagerStartupTab newStartupTab,
            GitSubmoduleManagerDefaultGitHubFilter newDefaultGitHubFilter,
            out string error)
        {
            error = string.Empty;
            int normalizedInterval = ClampRefreshIntervalMinutes(intervalMinutes);
            GitSubmoduleManagerStartupTab normalizedStartupTab = NormalizeStartupTab(newStartupTab);
            GitSubmoduleManagerDefaultGitHubFilter normalizedFilter =
                NormalizeDefaultGitHubFilter(newDefaultGitHubFilter);

            if (refreshInProjectWhenRevisited == refreshWhenRevisited &&
                refreshIntervalMinutes == normalizedInterval &&
                startupTab == normalizedStartupTab &&
                defaultGitHubFilter == normalizedFilter)
            {
                return true;
            }

            bool previousRefreshWhenRevisited = refreshInProjectWhenRevisited;
            int previousInterval = refreshIntervalMinutes;
            GitSubmoduleManagerStartupTab previousStartupTab = startupTab;
            GitSubmoduleManagerDefaultGitHubFilter previousFilter = defaultGitHubFilter;

            refreshInProjectWhenRevisited = refreshWhenRevisited;
            refreshIntervalMinutes = normalizedInterval;
            startupTab = normalizedStartupTab;
            defaultGitHubFilter = normalizedFilter;
            try
            {
                Save(true);
                return true;
            }
            catch (System.Exception exception)
            {
                refreshInProjectWhenRevisited = previousRefreshWhenRevisited;
                refreshIntervalMinutes = previousInterval;
                startupTab = previousStartupTab;
                defaultGitHubFilter = previousFilter;
                error = BuildSaveError(exception);
                return false;
            }
        }

        internal bool TryMarkWelcomeShown(out string error)
        {
            error = string.Empty;
            if (welcomeShown)
                return true;

            welcomeShown = true;
            try
            {
                Save(true);
                return true;
            }
            catch (System.Exception exception)
            {
                welcomeShown = false;
                error = BuildSaveError(exception);
                return false;
            }
        }

        private static string BuildSaveError(System.Exception exception)
        {
            string detail = exception == null || string.IsNullOrWhiteSpace(exception.Message)
                ? "Unity did not provide an error message."
                : exception.Message.Trim();
            return GitHubUtility.SanitizeUiDiagnostic(
                "Git Submodule Manager could not save its user settings: " + detail);
        }
    }
}
