using System;
using UnityEditor;
using UnityEngine;

namespace MartinCalander.GitPackageManager.Editor
{
    public partial class GitPackageManagerWindow
    {
        private const int GitHubAuthenticationTimeoutMs = 10 * 60 * 1000;
        private const double GitHubAuthenticationBrowserDelaySeconds = 1.5;
        private const string GitHubAuthenticationSessionKey =
            "MartinCalander.GitPackageManager.GitHubAuthenticationInFlight";

        private static readonly object GitHubAuthenticationGate = new object();
        private static AsyncCommandHandle retiredGhAuthenticationHandle;
        private static bool ghAuthenticationRestartRequired;
        private static bool ghAuthenticationSafetyInitialized;

        private AsyncCommandHandle ghAuthenticationHandle;
        private double ghAuthenticationDevicePageOpenAt = -1d;

        private bool IsGhAuthenticationInProgress =>
            ghAuthenticationHandle != null && !ghAuthenticationHandle.IsComplete;

        internal static bool IsSharedGitHubAuthenticationGateState(
            bool activeOrAwaitingProcessing,
            bool retiredOrStopping,
            bool restartRequired)
        {
            return activeOrAwaitingProcessing || retiredOrStopping || restartRequired;
        }

        internal static bool CanStartGitHubAuthentication(
            bool ghAvailable,
            bool repositoryOperationBusy,
            bool sharedAuthenticationBlocked,
            bool backgroundReadersActive,
            bool gitHubCommandActive = false)
        {
            return ghAvailable &&
                   !repositoryOperationBusy &&
                   !sharedAuthenticationBlocked &&
                   !backgroundReadersActive &&
                   !gitHubCommandActive;
        }

        internal static bool IsSharedGitHubAuthenticationBlocked
        {
            get
            {
                lock (GitHubAuthenticationGate)
                {
                    return IsSharedGitHubAuthenticationGateState(
                        CliCommandRunner.IsGitHubAuthenticationReserved,
                        retiredGhAuthenticationHandle != null,
                        ghAuthenticationRestartRequired ||
                        CliCommandRunner.GitHubCommandRequiresEditorRestart);
                }
            }
        }

        private void StartGitHubAuthentication()
        {
            // Authentication has its own process lifecycle. Keep this explicit so
            // separating Git-only action gating can never permit a duplicate login.
            EnsureGitHubAuthenticationSafetyInitialized();
            UpdateRetiredGitHubAuthentication();
            bool sharedAuthenticationBlocked = IsSharedGitHubAuthenticationBlocked;
            bool backgroundReadersActive = AreBackgroundLoadsDraining;
            bool gitHubCommandActive = HasConflictingGitHubCommandActivity;
            if (!CanStartGitHubAuthentication(
                    ghAvailable,
                    IsRepositoryOperationBusy,
                    sharedAuthenticationBlocked,
                    backgroundReadersActive,
                    gitHubCommandActive))
            {
                if (sharedAuthenticationBlocked)
                {
                    installStatus = GetSharedGitHubAuthenticationBlockMessage();
                    installStatusType = MessageType.Warning;
                }
                else if (backgroundReadersActive)
                {
                    installStatus =
                        "Wait for the current package scan to finish before starting GitHub authentication.";
                    installStatusType = MessageType.Info;
                }
                else if (gitHubCommandActive)
                {
                    installStatus = AsyncCommandDrainRegistry.RequiresEditorRestart ||
                                    CliCommandRunner.GitHubCommandRequiresEditorRestart
                        ? "A previous GitHub CLI request did not confirm that it stopped. Restart Unity before authenticating."
                        : "Wait for the current GitHub CLI request to finish before starting authentication.";
                    installStatusType = MessageType.Info;
                }
                return;
            }

            string tokenEnvironmentVariable = GetGitHubTokenEnvironmentOverrideName();
            if (!string.IsNullOrEmpty(tokenEnvironmentVariable))
            {
                installStatus =
                    $"GitHub CLI is currently controlled by {tokenEnvironmentVariable} in Unity's environment. " +
                    "One-click login cannot replace that token. Remove or update the variable, restart Unity, " +
                    "then check again; alternatively authenticate in a terminal where the variable is unset.";
                installStatusType = MessageType.Warning;
                return;
            }

            if (!GitHubUtility.SupportsClipboardAuthentication(ghVersion))
            {
                installStatus =
                    "One-click authentication requires GitHub CLI 2.79.0 or newer. Update GitHub CLI, or run the displayed command in a visible terminal.";
                installStatusType = MessageType.Warning;
                return;
            }

            if (!CliCommandRunner.TryResolveCommand("gh", out string resolvedGhPath))
            {
                installStatus = "GitHub CLI could not be resolved from a trusted executable location. Check the installation and try again.";
                installStatusType = MessageType.Error;
                return;
            }

            string prompt =
                "Unity will start GitHub CLI's device flow, open github.com/login/device in your " +
                "browser, and ask GitHub CLI to copy the one-time code to the clipboard. Complete " +
                "the authorization in your browser, then return to Unity.\n\n" +
                GitHubUtility.AuthenticationDisplayCommand + "\n\n" +
                "If no code is available to paste, cancel and run the displayed command in a " +
                "visible terminal after cancellation finishes. Unity will tell you if a restart " +
                "is required first. The automated flow sets GitHub CLI's host-wide Git protocol " +
                "for github.com to HTTPS. Git Package Manager never receives or stores your GitHub token.";
            if (!EditorUtility.DisplayDialog(
                    "Authenticate with GitHub?",
                    prompt,
                    "Authenticate",
                    "Cancel"))
            {
                installStatus = "GitHub authentication was not started.";
                installStatusType = MessageType.Info;
                return;
            }

            // A modal dialog can run a nested Editor event loop. Recheck process
            // activity after it closes before claiming exclusive authentication.
            if (HasConflictingGitHubCommandActivity)
            {
                installStatus =
                    "A GitHub CLI request started while the confirmation dialog was open. Wait for it to finish and try again.";
                installStatusType = MessageType.Info;
                return;
            }

            if (!TryReserveSharedGitHubAuthentication())
            {
                installStatus = GetSharedGitHubAuthenticationBlockMessage();
                installStatusType = MessageType.Warning;
                return;
            }

            if (!TrySetGitHubAuthenticationSessionMarker(true, out string markerError))
            {
                RequireGitHubAuthenticationRestart();
                ReleaseSharedGitHubAuthenticationReservation();
                installStatus =
                    "GitHub authentication was not started because Unity could not record its process ownership. " +
                    markerError;
                installStatusType = MessageType.Error;
                return;
            }

            try
            {
                ghAuthenticationHandle = CliCommandRunner.RunAsync(
                    resolvedGhPath,
                    GitHubUtility.BuildAuthenticationArguments(),
                    GitUtility.ProjectRoot,
                    GitHubAuthenticationTimeoutMs,
                    CommandTerminationScope.RootProcess,
                    isGitHubAuthenticationCommand: true);
            }
            catch
            {
                ghAuthenticationHandle = null;
                if (!TrySetGitHubAuthenticationSessionMarker(false, out _))
                    RequireGitHubAuthenticationRestart();
                ReleaseSharedGitHubAuthenticationReservation();
                installStatus =
                    "GitHub authentication could not be started. Retry, or run the displayed command in a terminal.";
                installStatusType = MessageType.Error;
                Repaint();
                return;
            }

            ghAuthenticationDevicePageOpenAt =
                EditorApplication.timeSinceStartup + GitHubAuthenticationBrowserDelaySeconds;
            installStatus = "Starting GitHub's device flow. The device page will open shortly...";
            installStatusType = MessageType.Info;

            Repaint();
        }

        private bool TryOpenGitHubAuthenticationDevicePage()
        {
            try
            {
                Application.OpenURL(GitHubUtility.AuthenticationDeviceUrl);
                return true;
            }
            catch
            {
                installStatus =
                    "GitHub authentication started, but the device page could not be opened. Open https://github.com/login/device in your browser.";
                installStatusType = MessageType.Warning;
                return false;
            }
        }

        private void CancelGitHubAuthentication()
        {
            if (!IsGhAuthenticationInProgress)
                return;

            ghAuthenticationHandle.Cancel();
            ghAuthenticationDevicePageOpenAt = -1d;
            installStatus = "Cancelling GitHub authentication...";
            installStatusType = MessageType.Info;
            Repaint();
        }

        private void UpdateGitHubAuthentication()
        {
            EnsureGitHubAuthenticationSafetyInitialized();
            UpdateRetiredGitHubAuthentication();
            AsyncCommandHandle handle = ghAuthenticationHandle;
            if (handle == null)
                return;

            if (!handle.IsComplete)
            {
                if (ghAuthenticationDevicePageOpenAt >= 0d &&
                    EditorApplication.timeSinceStartup >= ghAuthenticationDevicePageOpenAt)
                {
                    ghAuthenticationDevicePageOpenAt = -1d;
                    if (TryOpenGitHubAuthenticationDevicePage())
                    {
                        installStatus =
                            "GitHub's device page was opened. Wait for GitHub CLI to copy the code, then paste it from your clipboard.";
                        installStatusType = MessageType.Info;
                    }
                }

                return;
            }

            CommandResult result = handle.Result;
            ghAuthenticationDevicePageOpenAt = -1d;
            if (result == null || !result.TerminationConfirmed)
            {
                RequireGitHubAuthenticationRestart();
            }
            else if (!TrySetGitHubAuthenticationSessionMarker(false, out _))
            {
                RequireGitHubAuthenticationRestart();
            }
            ghAuthenticationHandle = null;
            ReleaseSharedGitHubAuthenticationReservation();

            if (result != null && result.IsSuccess && result.TerminationConfirmed)
            {
                installStatus = "GitHub authentication completed. Verifying the active account...";
                installStatusType = MessageType.Info;
                dependencyCheckIncludesGitHub = true;
                BeginBackgroundLoad(true);
            }
            else
            {
                installStatus = BuildGitHubAuthenticationFailureMessage(result);
                installStatusType = result != null && result.Cancelled
                    ? MessageType.Info
                    : MessageType.Warning;
            }

            Repaint();
        }

        private void ReleaseGitHubAuthentication()
        {
            if (ghAuthenticationHandle == null)
                return;

            ghAuthenticationHandle.Cancel();
            ghAuthenticationDevicePageOpenAt = -1d;
            lock (GitHubAuthenticationGate)
            {
                if (retiredGhAuthenticationHandle != null &&
                    !ReferenceEquals(retiredGhAuthenticationHandle, ghAuthenticationHandle))
                {
                    ghAuthenticationRestartRequired = true;
                }

                retiredGhAuthenticationHandle = ghAuthenticationHandle;
            }

            ghAuthenticationHandle = null;
        }

        private static void UpdateRetiredGitHubAuthentication()
        {
            EnsureGitHubAuthenticationSafetyInitialized();
            AsyncCommandHandle handle;
            lock (GitHubAuthenticationGate)
                handle = retiredGhAuthenticationHandle;
            if (handle == null || !handle.IsComplete)
                return;

            CommandResult result = handle.Result;
            if (result == null || !result.TerminationConfirmed)
            {
                RequireGitHubAuthenticationRestart();
            }
            else if (!TrySetGitHubAuthenticationSessionMarker(false, out _))
            {
                RequireGitHubAuthenticationRestart();
            }
            bool releaseReservation = false;
            lock (GitHubAuthenticationGate)
            {
                if (ReferenceEquals(retiredGhAuthenticationHandle, handle))
                {
                    retiredGhAuthenticationHandle = null;
                    releaseReservation = true;
                }
            }
            if (releaseReservation)
                ReleaseSharedGitHubAuthenticationReservation();
        }

        private bool DrawGitHubAuthenticationLifecycleNotice()
        {
            EnsureGitHubAuthenticationSafetyInitialized();
            UpdateRetiredGitHubAuthentication();
            bool hasRetiredAuthentication;
            bool restartRequired;
            bool reserved;
            lock (GitHubAuthenticationGate)
            {
                hasRetiredAuthentication = retiredGhAuthenticationHandle != null;
                restartRequired = ghAuthenticationRestartRequired ||
                                  CliCommandRunner.GitHubCommandRequiresEditorRestart;
                reserved = CliCommandRunner.IsGitHubAuthenticationReserved;
            }

            if (hasRetiredAuthentication)
            {
                EditorGUILayout.HelpBox(
                    "The previous GitHub authentication attempt is still stopping. Package management remains available while you wait.",
                    MessageType.Info);
                return true;
            }

            if (restartRequired)
            {
                EditorGUILayout.HelpBox(
                    "Unity could not confirm that the previous GitHub CLI authentication process stopped. Restart Unity before starting another login. Other package operations remain available.",
                    MessageType.Warning);
                return true;
            }

            if (!reserved)
                return false;

            EditorGUILayout.HelpBox(
                ghAuthenticationHandle != null
                    ? "GitHub authentication is finishing. Git-only package operations remain available."
                    : "GitHub authentication is active in another Git Package Manager window. Git-only package operations remain available.",
                MessageType.Info);
            return true;
        }

        private static void EnsureGitHubAuthenticationSafetyInitialized()
        {
            lock (GitHubAuthenticationGate)
            {
                if (ghAuthenticationSafetyInitialized)
                    return;
                ghAuthenticationSafetyInitialized = true;
            }
            try
            {
                // SessionState survives domain reloads but is cleared by a full
                // Editor restart. A surviving marker means the previous domain
                // lost the only handle capable of proving process termination.
                if (SessionState.GetBool(GitHubAuthenticationSessionKey, false))
                    RequireGitHubAuthenticationRestart();
            }
            catch (Exception exception)
            {
                RequireGitHubAuthenticationRestart();
                Debug.LogWarning(
                    "[Git Package Manager] GitHub authentication ownership could not be read: " +
                    exception.Message);
            }
        }

        private static bool TryReserveSharedGitHubAuthentication()
        {
            EnsureGitHubAuthenticationSafetyInitialized();
            UpdateRetiredGitHubAuthentication();
            lock (GitHubAuthenticationGate)
            {
                if (IsSharedGitHubAuthenticationGateState(
                        CliCommandRunner.IsGitHubAuthenticationReserved,
                        retiredGhAuthenticationHandle != null,
                        ghAuthenticationRestartRequired ||
                        CliCommandRunner.GitHubCommandRequiresEditorRestart))
                {
                    return false;
                }

                return CliCommandRunner.TryReserveGitHubAuthentication();
            }
        }

        private static void ReleaseSharedGitHubAuthenticationReservation()
        {
            CliCommandRunner.ReleaseGitHubAuthenticationReservation();
        }

        private static void RequireGitHubAuthenticationRestart()
        {
            lock (GitHubAuthenticationGate)
                ghAuthenticationRestartRequired = true;
        }

        private static bool HasConflictingGitHubCommandActivity =>
            CliCommandRunner.HasActiveGitHubCommands ||
            CliCommandRunner.GitHubCommandRequiresEditorRestart ||
            AsyncCommandDrainRegistry.IsDraining;

        private static string GetSharedGitHubAuthenticationBlockMessage()
        {
            lock (GitHubAuthenticationGate)
            {
                if (ghAuthenticationRestartRequired ||
                    CliCommandRunner.GitHubCommandRequiresEditorRestart)
                    return "Restart Unity before starting another GitHub authentication attempt.";
                if (retiredGhAuthenticationHandle != null)
                    return "The previous GitHub authentication attempt is still stopping. Wait and try again.";
                return "GitHub authentication is already active or awaiting completion in another window.";
            }
        }

        private static bool TrySetGitHubAuthenticationSessionMarker(
            bool value,
            out string error)
        {
            try
            {
                SessionState.SetBool(GitHubAuthenticationSessionKey, value);
                error = string.Empty;
                return true;
            }
            catch (Exception exception)
            {
                error = "Save your work and restart Unity before retrying. " +
                        GitHubUtility.SanitizeUiDiagnostic(exception.Message);
                return false;
            }
        }

        internal static string GetGitHubTokenEnvironmentOverrideName(
            Func<string, string> getEnvironmentVariable = null)
        {
            getEnvironmentVariable ??= Environment.GetEnvironmentVariable;
            foreach (string variableName in new[] { "GH_TOKEN", "GITHUB_TOKEN" })
            {
                if (!string.IsNullOrEmpty(getEnvironmentVariable(variableName)))
                    return variableName;
            }

            return string.Empty;
        }

        internal static string BuildGitHubAuthenticationFailureMessage(CommandResult result)
        {
            if (result == null)
            {
                return "GitHub authentication returned no result. Retry, or run 'gh auth login' in a terminal.";
            }

            if (result.Cancelled)
            {
                return result.TerminationConfirmed
                    ? "GitHub authentication was cancelled. Your existing GitHub CLI credentials were not changed."
                    : "GitHub authentication cancellation was requested, but Unity could not confirm that the GitHub CLI process stopped. Restart Unity before retrying.";
            }
            if (result.TimedOut)
            {
                if (!result.TerminationConfirmed)
                {
                    return "GitHub authentication timed out, and Unity could not confirm that the GitHub CLI process stopped. Restart Unity before retrying.";
                }

                return "GitHub authentication timed out. Retry and finish the browser authorization, or run 'gh auth login' in a terminal.";
            }

            if (!result.TerminationConfirmed)
            {
                return "GitHub authentication ended, but Unity could not confirm that the GitHub CLI process stopped. Restart Unity before retrying.";
            }

            string exit = result.ExitCode == 0 ? string.Empty : $" (exit code {result.ExitCode})";
            return
                $"GitHub authentication did not complete{exit}. Retry, or run 'gh auth login' in a terminal. " +
                "No token or device code was stored by Git Package Manager.";
        }
    }
}
