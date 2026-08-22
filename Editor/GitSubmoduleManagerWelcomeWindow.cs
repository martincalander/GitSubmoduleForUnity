using System;
using System.Threading;
using UnityEditor;
using UnityEngine;

namespace MartinCalander.GitSubmoduleManager.Editor
{
    /// <summary>
    /// Small, standalone setup surface retained after package management moved
    /// into Unity's native Package Manager. It performs command-line probes on a
    /// worker so opening Preferences or this window never blocks the Editor UI.
    /// </summary>
    internal sealed class GitSubmoduleManagerWelcomeWindow : EditorWindow
    {
        private const string DocumentationUrl =
            "https://github.com/martincalander/GitSubmoduleManager#usage";
        private const string ShownThisSessionKey =
            "MartinCalander.GitSubmoduleManager.WelcomeShownThisSession";

        private sealed class SetupProbeResult
        {
            internal int Generation;
            internal bool GitAvailable;
            internal string GitVersion = string.Empty;
            internal string GitError = string.Empty;
            internal bool GitHubCliAvailable;
            internal string GitHubCliVersion = string.Empty;
            internal string GitHubCliError = string.Empty;
            internal bool GitHubAuthenticated;
            internal string GitHubAuthenticationError = string.Empty;
            internal bool GitHubProbeDeferred;
        }

        private Vector2 scrollPosition;
        private CancellationTokenSource probeCancellationSource;
        private Thread probeThread;
        private volatile SetupProbeResult pendingProbeResult;
        private int probeGeneration;
        private bool isChecking;
        private bool gitAvailable;
        private string gitVersion = string.Empty;
        private string gitError = string.Empty;
        private bool gitHubCliAvailable;
        private string gitHubCliVersion = string.Empty;
        private string gitHubCliError = string.Empty;
        private bool gitHubAuthenticated;
        private string gitHubAuthenticationError = string.Empty;
        private bool gitHubProbeDeferred;
        private string settingsError = string.Empty;

        internal static void Open()
        {
            OpenWindow();
        }

        internal static void OpenWindow()
        {
            SessionState.SetBool(ShownThisSessionKey, true);
            GitSubmoduleManagerWelcomeWindow window =
                GetWindow<GitSubmoduleManagerWelcomeWindow>(
                    true,
                    "Git Submodule Manager",
                    true);
            window.minSize = new Vector2(520f, 430f);
            window.Show();
            window.Focus();
        }

        internal static void OpenIfNeeded()
        {
            if (Application.isBatchMode)
                return;

            GitSubmoduleManagerUserSettings settings =
                GitSubmoduleManagerUserSettings.Instance;
            bool shownThisSession =
                SessionState.GetBool(ShownThisSessionKey, false);
            if (!GitSubmoduleManagerUserSettings.ShouldShowWelcome(
                    settings.HasShownWelcome,
                    shownThisSession))
            {
                return;
            }

            OpenWindow();
        }

        private void OnEnable()
        {
            titleContent = new GUIContent(
                "Git Submodule Manager",
                GitSubmoduleManagerIcons.GitIcon);
            minSize = new Vector2(520f, 430f);
            if (!GitSubmoduleManagerUserSettings.Instance.TryMarkWelcomeShown(
                    out settingsError))
            {
                Debug.LogWarning(settingsError);
            }

            StartSetupProbe();
        }

        private void OnDisable()
        {
            Interlocked.Increment(ref probeGeneration);
            probeCancellationSource?.Cancel();
            probeCancellationSource = null;
            probeThread = null;
            pendingProbeResult = null;
            isChecking = false;
        }

        private void Update()
        {
            SetupProbeResult result = pendingProbeResult;
            if (result == null)
                return;

            pendingProbeResult = null;
            if (result.Generation != probeGeneration)
                return;

            probeCancellationSource = null;
            probeThread = null;
            isChecking = false;
            gitAvailable = result.GitAvailable;
            gitVersion = result.GitVersion;
            gitError = result.GitError;
            gitHubCliAvailable = result.GitHubCliAvailable;
            gitHubCliVersion = result.GitHubCliVersion;
            gitHubCliError = result.GitHubCliError;
            gitHubAuthenticated = result.GitHubAuthenticated;
            gitHubAuthenticationError = result.GitHubAuthenticationError;
            gitHubProbeDeferred = result.GitHubProbeDeferred;
            Repaint();
        }

        private void OnGUI()
        {
            scrollPosition = EditorGUILayout.BeginScrollView(scrollPosition);
            EditorGUILayout.Space(16f);
            EditorGUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            EditorGUILayout.BeginVertical(GUILayout.MaxWidth(620f));

            DrawHeader();
            EditorGUILayout.Space(12f);
            EditorGUILayout.HelpBox(
                "Package discovery and management now live in Window > Package Manager > Sources > GitHub.",
                MessageType.Info);
            EditorGUILayout.Space(8f);

            EditorGUILayout.LabelField("Setup", EditorStyles.boldLabel);
            DrawGitStatus();
            EditorGUILayout.Space(5f);
            DrawGitHubStatus();

            if (!string.IsNullOrWhiteSpace(settingsError))
            {
                EditorGUILayout.Space(6f);
                EditorGUILayout.HelpBox(settingsError, MessageType.Error);
            }

            EditorGUILayout.Space(10f);
            using (new EditorGUI.DisabledScope(isChecking))
            {
                if (GUILayout.Button("Check Again", GUILayout.Height(24f)))
                    StartSetupProbe();
            }

            EditorGUILayout.Space(10f);
            using (new EditorGUI.DisabledScope(isChecking || !gitAvailable))
            {
                if (GUILayout.Button(
                        "Open Sources > GitHub",
                        GUILayout.Height(30f)))
                {
                    GitSubmoduleManagerPackageManagerHost.OpenGitHubSource();
                    Close();
                }
            }

            if (GUILayout.Button("Open Documentation", GUILayout.Height(22f)))
                Application.OpenURL(DocumentationUrl);

            EditorGUILayout.Space(16f);
            EditorGUILayout.EndVertical();
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.EndScrollView();
        }

        private void DrawHeader()
        {
            EditorGUILayout.BeginHorizontal();
            Texture icon = GitSubmoduleManagerIcons.GitIcon;
            if (icon != null)
            {
                GUILayout.Label(icon, GUILayout.Width(42f), GUILayout.Height(42f));
                GUILayout.Space(10f);
            }

            EditorGUILayout.BeginVertical();
            EditorGUILayout.LabelField(
                "Welcome to Git Submodule Manager",
                EditorStyles.largeLabel);
            EditorGUILayout.LabelField(
                "Discover GitHub UPM packages and install them as editable submodules or read-only Git dependencies.",
                EditorStyles.wordWrappedLabel);
            EditorGUILayout.EndVertical();
            EditorGUILayout.EndHorizontal();
        }

        private void DrawGitStatus()
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField("Git — Required", EditorStyles.boldLabel);
            if (isChecking)
            {
                EditorGUILayout.LabelField("Checking Git...", EditorStyles.wordWrappedLabel);
            }
            else if (gitAvailable)
            {
                EditorGUILayout.HelpBox(
                    "Ready — " + FirstLine(gitVersion),
                    MessageType.Info);
            }
            else
            {
                EditorGUILayout.HelpBox(
                    "Git was not detected. Package installation, conversion, and removal require Git.",
                    MessageType.Error);
                DrawSafeDiagnostic(gitError, MessageType.Error);
                if (GUILayout.Button("Open Git Download Page"))
                    Application.OpenURL(CliInstaller.GetInstallUrl(ToolKind.Git));
            }

            EditorGUILayout.EndVertical();
        }

        private void DrawGitHubStatus()
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField("GitHub CLI — Recommended", EditorStyles.boldLabel);
            if (isChecking)
            {
                EditorGUILayout.LabelField(
                    "Checking GitHub CLI and authentication...",
                    EditorStyles.wordWrappedLabel);
            }
            else if (gitHubProbeDeferred)
            {
                EditorGUILayout.HelpBox(
                    "A GitHub CLI operation is already active. Check again after it finishes.",
                    MessageType.Info);
            }
            else if (!gitHubCliAvailable)
            {
                EditorGUILayout.HelpBox(
                    "GitHub CLI is optional for direct URL installs, but it is " +
                    "required to discover repositories visible to your account.",
                    MessageType.Warning);
                DrawSafeDiagnostic(gitHubCliError, MessageType.Warning);
                if (GUILayout.Button("Open GitHub CLI Install Guide"))
                    Application.OpenURL(CliInstaller.GetInstallUrl(ToolKind.GitHubCli));
            }
            else if (!gitHubAuthenticated)
            {
                EditorGUILayout.HelpBox(
                    "GitHub CLI is installed but is not authenticated for github.com.",
                    MessageType.Warning);
                if (!string.IsNullOrWhiteSpace(gitHubCliVersion))
                    EditorGUILayout.LabelField(FirstLine(gitHubCliVersion), EditorStyles.miniLabel);
                DrawSafeDiagnostic(
                    gitHubAuthenticationError,
                    MessageType.Warning);
                EditorGUILayout.SelectableLabel(
                    GitHubUtility.AuthenticationTerminalDisplayCommand,
                    EditorStyles.textField,
                    GUILayout.Height(EditorGUIUtility.singleLineHeight));
                EditorGUILayout.BeginHorizontal();
                if (GUILayout.Button("Copy Authentication Command"))
                {
                    EditorGUIUtility.systemCopyBuffer =
                        GitHubUtility.AuthenticationTerminalDisplayCommand;
                }
                if (GUILayout.Button("Open Authentication Guide"))
                    Application.OpenURL(GitHubUtility.AuthenticationGuideUrl);
                EditorGUILayout.EndHorizontal();
            }
            else
            {
                EditorGUILayout.HelpBox(
                    "Ready — GitHub CLI is installed and authenticated.",
                    MessageType.Info);
                if (!string.IsNullOrWhiteSpace(gitHubCliVersion))
                    EditorGUILayout.LabelField(FirstLine(gitHubCliVersion), EditorStyles.miniLabel);
            }

            EditorGUILayout.EndVertical();
        }

        private void StartSetupProbe()
        {
            probeCancellationSource?.Cancel();
            CancellationTokenSource cancellationSource = new();
            int generation = Interlocked.Increment(ref probeGeneration);
            probeCancellationSource = cancellationSource;
            pendingProbeResult = null;
            isChecking = true;

            // Cache Unity-owned path state on the main thread before the worker
            // enters any Git helpers.
            _ = GitUtility.ProjectRoot;
            var thread = new Thread(
                () => RunSetupProbe(generation, cancellationSource.Token))
            {
                IsBackground = true,
                Name = "Git Submodule Manager welcome setup probe"
            };
            probeThread = thread;
            try
            {
                thread.Start();
            }
            catch (Exception exception)
            {
                probeThread = null;
                probeCancellationSource = null;
                isChecking = false;
                gitAvailable = false;
                gitError = GitHubUtility.SanitizeUiDiagnostic(exception.Message);
            }

            Repaint();
        }

        private void RunSetupProbe(int generation, CancellationToken cancellationToken)
        {
            var result = new SetupProbeResult { Generation = generation };
            try
            {
                result.GitAvailable = GitUtility.IsGitAvailable(
                    out string detectedGitVersion,
                    out string detectedGitError,
                    cancellationToken);
                result.GitVersion = detectedGitVersion ?? string.Empty;
                result.GitError = detectedGitError ?? string.Empty;

                cancellationToken.ThrowIfCancellationRequested();
                result.GitHubCliAvailable = GitHubUtility.IsGhAvailable(
                    cancellationToken,
                    out string detectedGitHubVersion,
                    out string detectedGitHubError,
                    out bool versionProbeDeferred);
                result.GitHubCliVersion = detectedGitHubVersion ?? string.Empty;
                result.GitHubCliError = detectedGitHubError ?? string.Empty;
                result.GitHubProbeDeferred = versionProbeDeferred;

                if (result.GitHubCliAvailable && !versionProbeDeferred)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    result.GitHubAuthenticated = GitHubUtility.IsAuthenticated(
                        cancellationToken,
                        out string authenticationError,
                        out bool authenticationProbeDeferred);
                    result.GitHubAuthenticationError = authenticationError ?? string.Empty;
                    result.GitHubProbeDeferred = authenticationProbeDeferred;
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception exception)
            {
                string error = GitHubUtility.SanitizeUiDiagnostic(exception.Message);
                if (!result.GitAvailable)
                    result.GitError = error;
                else if (!result.GitHubCliAvailable)
                    result.GitHubCliError = error;
                else
                    result.GitHubAuthenticationError = error;
            }

            if (!cancellationToken.IsCancellationRequested &&
                generation == Volatile.Read(ref probeGeneration))
            {
                pendingProbeResult = result;
            }
        }

        private static void DrawSafeDiagnostic(string diagnostic, MessageType type)
        {
            if (string.IsNullOrWhiteSpace(diagnostic))
                return;

            EditorGUILayout.HelpBox(
                GitUtility.RedactCredentials(
                    GitHubUtility.SanitizeUiDiagnostic(diagnostic)),
                type);
        }

        private static string FirstLine(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return "detected";

            string trimmed = value.Trim();
            int lineBreak = trimmed.IndexOfAny(new[] { '\r', '\n' });
            return lineBreak < 0 ? trimmed : trimmed.Substring(0, lineBreak);
        }
    }
}
