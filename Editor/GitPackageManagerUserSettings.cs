using UnityEditor;
using UnityEngine;

namespace MartinCalander.GitPackageManager.Editor
{
    internal enum GitPackageManagerStartupTab
    {
        InProject,
        GitHub
    }

    internal enum GitPackageManagerDefaultGitHubFilter
    {
        AllRepositories,
        ValidUpmPackages
    }

    [FilePath(
        SettingsFilePath,
        FilePathAttribute.Location.ProjectFolder)]
    internal sealed class GitPackageManagerUserSettings : ScriptableSingleton<GitPackageManagerUserSettings>
    {
        internal const string SettingsFilePath =
            "UserSettings/GitPackageManagerSettings.asset";
        internal const int DefaultRefreshIntervalMinutes = 5;
        internal const int MinimumRefreshIntervalMinutes = 1;
        internal const int MaximumRefreshIntervalMinutes = 60;

        [SerializeField]
        private bool welcomeShown;

        [SerializeField]
        private bool refreshInProjectWhenRevisited = true;

        [SerializeField]
        private int refreshIntervalMinutes = DefaultRefreshIntervalMinutes;

        [SerializeField]
        private GitPackageManagerStartupTab startupTab = GitPackageManagerStartupTab.InProject;

        [SerializeField]
        private GitPackageManagerDefaultGitHubFilter defaultGitHubFilter =
            GitPackageManagerDefaultGitHubFilter.AllRepositories;

        internal bool HasShownWelcome => welcomeShown;
        internal bool RefreshInProjectWhenRevisited => refreshInProjectWhenRevisited;
        internal int RefreshIntervalMinutes => ClampRefreshIntervalMinutes(refreshIntervalMinutes);
        internal double RefreshIntervalSeconds => RefreshIntervalMinutes * 60.0;
        internal GitPackageManagerStartupTab StartupTab => NormalizeStartupTab(startupTab);
        internal GitPackageManagerDefaultGitHubFilter DefaultGitHubFilter =>
            NormalizeDefaultGitHubFilter(defaultGitHubFilter);

        internal static bool ShouldShowWelcome(bool hasShownWelcome, bool shownThisSession)
        {
            return !hasShownWelcome && !shownThisSession;
        }

        internal static int ClampRefreshIntervalMinutes(int minutes)
        {
            return Mathf.Clamp(
                minutes,
                MinimumRefreshIntervalMinutes,
                MaximumRefreshIntervalMinutes);
        }

        internal static GitPackageManagerStartupTab NormalizeStartupTab(
            GitPackageManagerStartupTab value)
        {
            return value == GitPackageManagerStartupTab.InProject ||
                   value == GitPackageManagerStartupTab.GitHub
                ? value
                : GitPackageManagerStartupTab.InProject;
        }

        internal static GitPackageManagerDefaultGitHubFilter NormalizeDefaultGitHubFilter(
            GitPackageManagerDefaultGitHubFilter value)
        {
            return value == GitPackageManagerDefaultGitHubFilter.AllRepositories ||
                   value == GitPackageManagerDefaultGitHubFilter.ValidUpmPackages
                ? value
                : GitPackageManagerDefaultGitHubFilter.AllRepositories;
        }

        internal bool TryUpdatePreferences(
            bool refreshWhenRevisited,
            int intervalMinutes,
            GitPackageManagerStartupTab newStartupTab,
            GitPackageManagerDefaultGitHubFilter newDefaultGitHubFilter,
            out string error)
        {
            error = string.Empty;
            int normalizedInterval = ClampRefreshIntervalMinutes(intervalMinutes);
            GitPackageManagerStartupTab normalizedStartupTab = NormalizeStartupTab(newStartupTab);
            GitPackageManagerDefaultGitHubFilter normalizedFilter =
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
            GitPackageManagerStartupTab previousStartupTab = startupTab;
            GitPackageManagerDefaultGitHubFilter previousFilter = defaultGitHubFilter;

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
                "Git Package Manager could not save its user settings: " + detail);
        }
    }
}
