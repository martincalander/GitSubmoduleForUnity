using System;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Scripting.APIUpdating;

namespace MartinCalander.GitSubmoduleManager.Editor
{
    internal enum GitSubmoduleManagerDefaultVisibility
    {
        All,
        Public,
        Private
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
        internal const bool SafeDefaultSuppressRoutineSubmoduleRemovalConfirmations = false;
        internal const bool SafeDefaultInstallDependenciesWithoutPrompt = false;
        internal const GitSubmoduleManagerDefaultVisibility SafeDefaultGitHubVisibility =
            GitSubmoduleManagerDefaultVisibility.All;
        internal const string SafeDefaultGitHubOrganization = "";
        internal const PackageManagerGitInstallMode SafeDefaultInstallMode =
            PackageManagerGitInstallMode.GitSubmodule;
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

        // Negative, false-by-default switches preserve the safest behavior for
        // existing settings assets that predate these fields.
        [SerializeField]
        private bool suppressRoutineSubmoduleRemovalConfirmations =
            SafeDefaultSuppressRoutineSubmoduleRemovalConfirmations;

        [SerializeField]
        private bool installDependenciesWithoutPrompt =
            SafeDefaultInstallDependenciesWithoutPrompt;

        [SerializeField]
        private GitSubmoduleManagerDefaultVisibility defaultGitHubVisibility =
            SafeDefaultGitHubVisibility;

        [SerializeField]
        private string defaultGitHubOrganization = SafeDefaultGitHubOrganization;

        [SerializeField]
        private PackageManagerGitInstallMode defaultInstallMode =
            SafeDefaultInstallMode;

        internal bool HasShownWelcome => welcomeShown;
        internal bool SuppressRoutineSubmoduleRemovalConfirmations =>
            suppressRoutineSubmoduleRemovalConfirmations;
        internal bool InstallDependenciesWithoutPrompt =>
            installDependenciesWithoutPrompt;
        internal GitSubmoduleManagerDefaultVisibility DefaultGitHubVisibility =>
            NormalizeDefaultGitHubVisibility(defaultGitHubVisibility);
        internal string DefaultGitHubOrganization =>
            NormalizeDefaultGitHubOrganization(defaultGitHubOrganization);
        internal PackageManagerGitInstallMode DefaultInstallMode =>
            NormalizeDefaultInstallMode(defaultInstallMode);

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

        internal static GitSubmoduleManagerDefaultVisibility NormalizeDefaultGitHubVisibility(
            GitSubmoduleManagerDefaultVisibility value)
        {
            return value == GitSubmoduleManagerDefaultVisibility.All ||
                   value == GitSubmoduleManagerDefaultVisibility.Public ||
                   value == GitSubmoduleManagerDefaultVisibility.Private
                ? value
                : GitSubmoduleManagerDefaultVisibility.All;
        }

        internal static string NormalizeDefaultGitHubOrganization(string value)
        {
            string normalized = value?.Trim() ?? string.Empty;
            const string filterPrefix = "Organization - ";
            if (normalized.StartsWith(
                    filterPrefix,
                    StringComparison.OrdinalIgnoreCase))
            {
                normalized = normalized.Substring(filterPrefix.Length).Trim();
            }

            // GitHub organization logins use the same conservative character
            // set as account names. Store the login rather than a presentation
            // label so localization can never make the preference unusable.
            var builder = new StringBuilder(Math.Min(normalized.Length, 39));
            for (int index = 0; index < normalized.Length && builder.Length < 39; index++)
            {
                char character = normalized[index];
                if ((character >= 'a' && character <= 'z') ||
                    (character >= 'A' && character <= 'Z') ||
                    (character >= '0' && character <= '9') ||
                    character == '-')
                {
                    builder.Append(character);
                }
            }

            return builder.ToString().Trim('-');
        }

        internal static PackageManagerGitInstallMode NormalizeDefaultInstallMode(
            PackageManagerGitInstallMode value)
        {
            return value == PackageManagerGitInstallMode.GitSubmodule ||
                   value == PackageManagerGitInstallMode.ReadOnlyPackage
                ? value
                : PackageManagerGitInstallMode.GitSubmodule;
        }

        internal bool TryUpdatePreferences(
            bool suppressRoutineConfirmations,
            bool autoInstallDependencies,
            GitSubmoduleManagerDefaultVisibility newDefaultVisibility,
            string newDefaultOrganization,
            PackageManagerGitInstallMode newDefaultInstallMode,
            out string error)
        {
            error = string.Empty;
            GitSubmoduleManagerDefaultVisibility normalizedVisibility =
                NormalizeDefaultGitHubVisibility(newDefaultVisibility);
            string normalizedOrganization =
                NormalizeDefaultGitHubOrganization(newDefaultOrganization);
            PackageManagerGitInstallMode normalizedInstallMode =
                NormalizeDefaultInstallMode(newDefaultInstallMode);

            if (suppressRoutineSubmoduleRemovalConfirmations ==
                    suppressRoutineConfirmations &&
                installDependenciesWithoutPrompt == autoInstallDependencies &&
                defaultGitHubVisibility == normalizedVisibility &&
                string.Equals(
                    defaultGitHubOrganization,
                    normalizedOrganization,
                    StringComparison.Ordinal) &&
                defaultInstallMode == normalizedInstallMode)
            {
                return true;
            }

            bool previousSuppressConfirmations =
                suppressRoutineSubmoduleRemovalConfirmations;
            bool previousAutoInstallDependencies = installDependenciesWithoutPrompt;
            GitSubmoduleManagerDefaultVisibility previousVisibility =
                defaultGitHubVisibility;
            string previousOrganization = defaultGitHubOrganization;
            PackageManagerGitInstallMode previousInstallMode = defaultInstallMode;

            suppressRoutineSubmoduleRemovalConfirmations =
                suppressRoutineConfirmations;
            installDependenciesWithoutPrompt = autoInstallDependencies;
            defaultGitHubVisibility = normalizedVisibility;
            defaultGitHubOrganization = normalizedOrganization;
            defaultInstallMode = normalizedInstallMode;
            try
            {
                Save(true);
                return true;
            }
            catch (System.Exception exception)
            {
                suppressRoutineSubmoduleRemovalConfirmations =
                    previousSuppressConfirmations;
                installDependenciesWithoutPrompt = previousAutoInstallDependencies;
                defaultGitHubVisibility = previousVisibility;
                defaultGitHubOrganization = previousOrganization;
                defaultInstallMode = previousInstallMode;
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
