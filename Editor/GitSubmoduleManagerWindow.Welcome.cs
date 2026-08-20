using System;
using UnityEditor;
using UnityEngine;

namespace MartinCalander.GitSubmoduleManager.Editor
{
    public partial class GitSubmoduleManagerWindow
    {
        private const float WelcomeMaximumWidth = 620f;
        private const float WelcomeStackActionsBelowWidth = 520f;
        private const string DocumentationUrl =
            "https://github.com/martincalander/GitSubmoduleManager#usage";

        private static bool welcomeShownThisSession;

        private Vector2 welcomeScroll;
        private bool showWelcomeScreen;
        private bool welcomePreferenceRecorded;
        private bool resetWelcomeScroll;

        internal enum WelcomeSetupState
        {
            Checking,
            GitMissing,
            GitHubCliMissing,
            GitHubAuthenticationMissing,
            Ready
        }

        internal enum WelcomeCheckStage
        {
            Git,
            GitHub,
            Complete
        }

        internal static WelcomeCheckStage GetWelcomeCheckStage(
            bool isInitialLoading,
            bool isGitStageReady)
        {
            if (!isInitialLoading)
                return WelcomeCheckStage.Complete;

            return isGitStageReady
                ? WelcomeCheckStage.GitHub
                : WelcomeCheckStage.Git;
        }

        internal static WelcomeSetupState GetWelcomeSetupState(
            bool isChecking,
            bool isGitAvailable,
            bool isGhAvailable,
            bool isGhAuthenticated)
        {
            if (isChecking)
                return WelcomeSetupState.Checking;
            if (!isGitAvailable)
                return WelcomeSetupState.GitMissing;
            if (!isGhAvailable)
                return WelcomeSetupState.GitHubCliMissing;
            return isGhAuthenticated
                ? WelcomeSetupState.Ready
                : WelcomeSetupState.GitHubAuthenticationMissing;
        }

        internal static bool CanFinishWelcome(bool isChecking, bool isGitAvailable)
        {
            return !isChecking && isGitAvailable;
        }

        internal static bool ShouldStackWelcomeActions(float contentWidth)
        {
            return contentWidth < WelcomeStackActionsBelowWidth;
        }

        internal static bool ShouldRecordWelcomeShown(
            bool isVisible,
            bool isRecorded,
            EventType eventType)
        {
            return isVisible && !isRecorded && eventType == EventType.Repaint;
        }

        internal static bool IsWelcomePreferenceAlreadyRecorded(
            bool persisted,
            bool shownThisSession)
        {
            return persisted || shownThisSession;
        }

        private void InitializeWelcomeState()
        {
            welcomeScroll = Vector2.zero;
            resetWelcomeScroll = true;
            bool persisted = GitSubmoduleManagerUserSettings.Instance.HasShownWelcome;
            showWelcomeScreen = GitSubmoduleManagerUserSettings.ShouldShowWelcome(
                persisted,
                welcomeShownThisSession);
            welcomePreferenceRecorded = IsWelcomePreferenceAlreadyRecorded(
                persisted,
                welcomeShownThisSession);
        }

        private void ShowWelcomeScreen()
        {
            welcomeScroll = Vector2.zero;
            resetWelcomeScroll = true;
            showWelcomeScreen = true;
            welcomePreferenceRecorded = IsWelcomePreferenceAlreadyRecorded(
                GitSubmoduleManagerUserSettings.Instance.HasShownWelcome,
                welcomeShownThisSession);
            BeginBackgroundLoad(false);
            Repaint();
        }

        internal static void OpenWelcomeFromPreferences()
        {
            var window = GetWindow<GitSubmoduleManagerWindow>("Git Submodule Manager");
            window.ApplyThemeIcon();
            window.Show();
            window.ShowWelcomeScreen();
            window.Focus();
        }

        private void RecordWelcomeShownIfNeeded()
        {
            if (!ShouldRecordWelcomeShown(
                    showWelcomeScreen,
                    welcomePreferenceRecorded,
                    Event.current.type))
            {
                return;
            }

            welcomeShownThisSession = true;
            welcomePreferenceRecorded = true;
            try
            {
                if (!GitSubmoduleManagerUserSettings.Instance.TryMarkWelcomeShown(out string error))
                    Debug.LogWarning(error);
            }
            catch (Exception exception)
            {
                Debug.LogWarning(
                    "Git Submodule Manager could not save its welcome-screen preference: " +
                    exception.Message);
            }
        }

        private void DrawWelcomeScreen()
        {
            RecordWelcomeShownIfNeeded();

            if (resetWelcomeScroll)
            {
                welcomeScroll = Vector2.zero;
                GUI.FocusControl(null);
                if (Event.current.type == EventType.Repaint)
                    resetWelcomeScroll = false;
            }

            float contentWidth = Mathf.Min(
                WelcomeMaximumWidth,
                Mathf.Max(280f, position.width - 48f));
            bool stackActions = ShouldStackWelcomeActions(contentWidth);
            WelcomeCheckStage checkStage = GetWelcomeCheckStage(
                isInitialLoading,
                initialGitStageReady);

            welcomeScroll = EditorGUILayout.BeginScrollView(welcomeScroll);
            EditorGUILayout.Space(20f);
            EditorGUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            EditorGUILayout.BeginVertical(GUILayout.Width(contentWidth));

            DrawWelcomeHero();
            EditorGUILayout.Space(12f);
            DrawWelcomeLocationCard();
            EditorGUILayout.Space(8f);
            EditorGUILayout.LabelField("Before you start", Styles.SectionHeader);
            DrawWelcomeGitCard(stackActions, checkStage);
            DrawWelcomeGitHubCard(stackActions, checkStage);
            DrawDependencyMessages();

            EditorGUILayout.Space(8f);
            EditorGUILayout.LabelField(
                "GitHub CLI is optional. You can always add a repository directly with Git from the + menu.",
                EditorStyles.wordWrappedMiniLabel);
            EditorGUILayout.Space(10f);

            bool gitCheckPending = checkStage == WelcomeCheckStage.Git;
            using (new EditorGUI.DisabledScope(!CanFinishWelcome(gitCheckPending, gitAvailable)))
            {
                if (GUILayout.Button("Start managing packages", GUILayout.Height(30f)))
                {
                    showWelcomeScreen = false;
                    if (currentTab == Tab.Installed)
                        RefreshInstalled();
                    Repaint();
                }
            }

            if (GUILayout.Button("Open documentation", Styles.LinkButton))
                Application.OpenURL(DocumentationUrl);

            EditorGUILayout.Space(20f);
            EditorGUILayout.EndVertical();
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.EndScrollView();
        }

        private void DrawWelcomeHero()
        {
            EditorGUILayout.BeginHorizontal();
            if (titleContent?.image != null)
            {
                GUILayout.Label(
                    titleContent.image,
                    GUILayout.Width(44f),
                    GUILayout.Height(44f));
                GUILayout.Space(12f);
            }

            EditorGUILayout.BeginVertical();
            EditorGUILayout.LabelField("Welcome to Git Submodule Manager", Styles.TitleLabel);
            EditorGUILayout.LabelField(
                "Manage Git-backed Unity packages as submodules under Packages/.",
                Styles.SubtitleLabel);
            EditorGUILayout.EndVertical();
            EditorGUILayout.EndHorizontal();
        }

        private static void DrawWelcomeLocationCard()
        {
            EditorGUILayout.BeginVertical(Styles.InfoBox);
            EditorGUILayout.LabelField("Open this window later from", Styles.InfoLabel);
            EditorGUILayout.LabelField(MenuPath.DisplayPath, Styles.InfoValue);
            EditorGUILayout.EndVertical();
        }

        private void DrawWelcomeGitCard(bool stackActions, WelcomeCheckStage checkStage)
        {
            bool gitCheckPending = checkStage == WelcomeCheckStage.Git;
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            DrawWelcomeCardHeader(
                "Git",
                "Required",
                gitCheckPending ? WelcomeCardIcon.Checking :
                gitAvailable ? WelcomeCardIcon.Ready : WelcomeCardIcon.Error);

            if (gitCheckPending)
            {
                DrawLoadingState(
                    "Checking Git...",
                    "Looking for the Git executable available to this Unity Editor.",
                    topSpacing: 2f);
            }
            else if (gitAvailable)
            {
                EditorGUILayout.LabelField(
                    "Ready — " + FirstLine(gitVersion),
                    EditorStyles.wordWrappedLabel);
            }
            else
            {
                EditorGUILayout.LabelField(
                    "Not detected by Unity. Git is required for every package operation.",
                    EditorStyles.wordWrappedLabel);
                if (!string.IsNullOrWhiteSpace(gitError))
                {
                    EditorGUILayout.HelpBox(
                        GitUtility.RedactCredentials(gitError.Trim()),
                        MessageType.Error);
                }

                DrawWelcomeInstallActions(
                    ToolKind.Git,
                    "Git",
                    "Open download page",
                    stackActions,
                    false);
            }

            EditorGUILayout.EndVertical();
        }

        private void DrawWelcomeGitHubCard(bool stackActions, WelcomeCheckStage checkStage)
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            WelcomeCardIcon icon = checkStage != WelcomeCheckStage.Complete
                ? WelcomeCardIcon.Checking
                : ghAvailable && ghAuthenticated
                    ? WelcomeCardIcon.Ready
                    : WelcomeCardIcon.Warning;
            DrawWelcomeCardHeader("GitHub CLI", "Recommended", icon);

            if (checkStage == WelcomeCheckStage.Git)
            {
                DrawLoadingState(
                    "Waiting to check GitHub CLI...",
                    "The required Git and installed-package checks run first.",
                    topSpacing: 2f);
            }
            else if (checkStage == WelcomeCheckStage.GitHub)
            {
                DrawLoadingState(
                    "Checking GitHub CLI...",
                    "Checking installation and authentication for github.com.",
                    topSpacing: 2f);
            }
            else if (!ghAvailable)
            {
                EditorGUILayout.LabelField(
                    "Not installed. Install it to browse your repositories; direct URL installation remains available.",
                    EditorStyles.wordWrappedLabel);
                if (!string.IsNullOrWhiteSpace(ghError))
                {
                    EditorGUILayout.HelpBox(
                        GitUtility.RedactCredentials(ghError.Trim()),
                        MessageType.Warning);
                }

                DrawWelcomeInstallActions(
                    ToolKind.GitHubCli,
                    "GitHub CLI",
                    "Open install guide",
                    stackActions,
                    true);
            }
            else if (!ghAuthenticated)
            {
                EditorGUILayout.LabelField(
                    "Installed — authenticate to browse repositories visible to your GitHub account.",
                    EditorStyles.wordWrappedLabel);
                if (!string.IsNullOrWhiteSpace(ghAuthError))
                {
                    string safeError = GitUtility.RedactCredentials(ghAuthError.Trim());
                    EditorGUILayout.HelpBox(FirstLine(safeError), MessageType.Warning);
                }

                DrawWelcomeAuthenticationActions(stackActions);
            }
            else
            {
                EditorGUILayout.LabelField(
                    "Ready — installed and authenticated for github.com.",
                    EditorStyles.wordWrappedLabel);
                if (!string.IsNullOrWhiteSpace(ghVersion))
                    EditorGUILayout.LabelField(FirstLine(ghVersion), EditorStyles.miniLabel);
            }

            EditorGUILayout.EndVertical();
        }

        private enum WelcomeCardIcon
        {
            Checking,
            Ready,
            Warning,
            Error
        }

        private static void DrawWelcomeCardHeader(
            string title,
            string importance,
            WelcomeCardIcon icon)
        {
            EditorGUILayout.BeginHorizontal();
            Texture iconTexture = GetWelcomeCardIcon(icon);
            if (iconTexture != null)
            {
                GUILayout.Label(iconTexture, GUILayout.Width(18f), GUILayout.Height(18f));
                GUILayout.Space(4f);
            }

            EditorGUILayout.LabelField(title, EditorStyles.boldLabel);
            GUILayout.FlexibleSpace();
            EditorGUILayout.LabelField(importance, EditorStyles.miniLabel, GUILayout.Width(88f));
            EditorGUILayout.EndHorizontal();
        }

        private static Texture GetWelcomeCardIcon(WelcomeCardIcon icon)
        {
            if (icon == WelcomeCardIcon.Checking)
                return null;

            string iconName = icon switch
            {
                WelcomeCardIcon.Ready => "TestPassed",
                WelcomeCardIcon.Error => "console.erroricon.sml",
                WelcomeCardIcon.Warning => "console.warnicon.sml",
                _ => null
            };
            return string.IsNullOrEmpty(iconName)
                ? null
                : EditorGUIUtility.IconContent(iconName)?.image;
        }

        private void DrawWelcomeInstallActions(
            ToolKind tool,
            string displayName,
            string guideLabel,
            bool stackActions,
            bool includeGitHub)
        {
            CliInstallPlan plan = CliInstaller.GetInstallPlan(tool);
            if (plan.CanCopyCommand && !string.IsNullOrWhiteSpace(plan.DisplayCommand))
            {
                EditorGUILayout.SelectableLabel(
                    plan.DisplayCommand,
                    EditorStyles.textField,
                    GUILayout.Height(EditorGUIUtility.singleLineHeight));
            }

            if (!stackActions)
                EditorGUILayout.BeginHorizontal();

            if (plan.CanRunAutomatically)
            {
                DrawInstallButton(tool, displayName, plan);
            }
            else if (plan.CanCopyCommand && GUILayout.Button("Copy install command", GUILayout.Height(22f)))
            {
                EditorGUIUtility.systemCopyBuffer = plan.DisplayCommand;
                installStatus = $"{displayName} install command copied to the clipboard.";
                installStatusType = MessageType.Info;
            }

            if (GUILayout.Button(guideLabel, GUILayout.Height(22f)))
                Application.OpenURL(plan.InstallUrl);

            using (new EditorGUI.DisabledScope(IsGitHubInteractionBusy))
            {
                if (GUILayout.Button("Check again", GUILayout.Height(22f)))
                    CheckDependenciesAgain(includeGitHub);
            }

            if (!stackActions)
                EditorGUILayout.EndHorizontal();

            if (!plan.CanRunAutomatically &&
                !string.IsNullOrWhiteSpace(plan.AutomaticInstallUnavailableReason))
            {
                EditorGUILayout.LabelField(
                    plan.AutomaticInstallUnavailableReason,
                    EditorStyles.wordWrappedMiniLabel);
            }
        }

        private void DrawWelcomeAuthenticationActions(bool stackActions)
        {
            EditorGUILayout.SelectableLabel(
                GitHubUtility.AuthenticationTerminalDisplayCommand,
                EditorStyles.textField,
                GUILayout.Height(EditorGUIUtility.singleLineHeight));

            if (IsGhAuthenticationInProgress)
            {
                DrawLoadingState(
                    "Waiting for GitHub authentication...",
                    "Wait for GitHub CLI to copy the code, then paste it on GitHub's device page. If no code appears, cancel; after cancellation finishes, use the terminal command. Unity will warn if a restart is required first.",
                    topSpacing: 2f);
                if (GUILayout.Button("Open GitHub device page", GUILayout.Height(22f)))
                    TryOpenGitHubAuthenticationDevicePage();
                if (GUILayout.Button("Cancel authentication", GUILayout.Height(22f)))
                    CancelGitHubAuthentication();
                return;
            }

            if (DrawGitHubAuthenticationLifecycleNotice())
            {
                if (GUILayout.Button("Check authentication again", GUILayout.Height(22f)))
                    CheckDependenciesAgain(true);
                return;
            }

            if (!GitHubUtility.SupportsClipboardAuthentication(ghVersion))
            {
                EditorGUILayout.HelpBox(
                    "One-click authentication requires GitHub CLI 2.79.0 or newer. Update GitHub CLI, or copy the command above and run it in a visible terminal.",
                    MessageType.Warning);

                if (!stackActions)
                    EditorGUILayout.BeginHorizontal();
                if (GUILayout.Button("Copy terminal command", GUILayout.Height(22f)))
                {
                    EditorGUIUtility.systemCopyBuffer = GitHubUtility.AuthenticationTerminalDisplayCommand;
                    installStatus = "Compatible GitHub CLI authentication command copied to the clipboard.";
                    installStatusType = MessageType.Info;
                }
                if (GUILayout.Button("Open update guide", GUILayout.Height(22f)))
                    Application.OpenURL(CliInstaller.GetInstallUrl(ToolKind.GitHubCli));
                if (GUILayout.Button("Check again", GUILayout.Height(22f)))
                    CheckDependenciesAgain(true);
                if (!stackActions)
                    EditorGUILayout.EndHorizontal();
                return;
            }

            if (!stackActions)
                EditorGUILayout.BeginHorizontal();

            using (new EditorGUI.DisabledScope(IsGitHubInteractionBusy))
            {
                if (GUILayout.Button("Authenticate with GitHub...", GUILayout.Height(22f)))
                    StartGitHubAuthentication();
            }

            if (GUILayout.Button("Open authentication guide", GUILayout.Height(22f)))
                Application.OpenURL(GitHubUtility.AuthenticationGuideUrl);

            using (new EditorGUI.DisabledScope(IsGitHubInteractionBusy))
            {
                if (GUILayout.Button("Check again", GUILayout.Height(22f)))
                    CheckDependenciesAgain(true);
            }

            if (!stackActions)
                EditorGUILayout.EndHorizontal();
        }
    }
}
