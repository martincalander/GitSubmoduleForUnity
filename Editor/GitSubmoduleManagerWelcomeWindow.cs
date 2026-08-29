using System;
using System.Threading;
using UnityEditor;
using UnityEngine;

namespace MartinCalander.GitSubmoduleManager.Editor
{
    internal sealed class GitSubmoduleManagerSetupSnapshot
    {
        internal static readonly GitSubmoduleManagerSetupSnapshot Idle = new(
            false,
            false,
            string.Empty,
            string.Empty,
            false,
            string.Empty,
            string.Empty,
            GitHubAuthenticationProbeStatus.Unknown,
            string.Empty,
            false);

        internal GitSubmoduleManagerSetupSnapshot(
            bool isChecking,
            bool gitAvailable,
            string gitVersion,
            string gitError,
            bool gitHubCliAvailable,
            string gitHubCliVersion,
            string gitHubCliError,
            GitHubAuthenticationProbeStatus gitHubAuthenticationStatus,
            string gitHubAuthenticationError,
            bool gitHubProbeDeferred)
        {
            IsChecking = isChecking;
            GitAvailable = gitAvailable;
            GitVersion = gitVersion ?? string.Empty;
            GitError = gitError ?? string.Empty;
            GitHubCliAvailable = gitHubCliAvailable;
            GitHubCliVersion = gitHubCliVersion ?? string.Empty;
            GitHubCliError = gitHubCliError ?? string.Empty;
            GitHubAuthenticationStatus = gitHubAuthenticationStatus;
            GitHubAuthenticationError = gitHubAuthenticationError ?? string.Empty;
            GitHubProbeDeferred = gitHubProbeDeferred;
        }

        internal bool IsChecking { get; }
        internal bool GitAvailable { get; }
        internal string GitVersion { get; }
        internal string GitError { get; }
        internal bool GitHubCliAvailable { get; }
        internal string GitHubCliVersion { get; }
        internal string GitHubCliError { get; }
        internal GitHubAuthenticationProbeStatus GitHubAuthenticationStatus { get; }
        internal bool GitHubAuthenticated =>
            GitHubAuthenticationStatus ==
            GitHubAuthenticationProbeStatus.Authenticated;
        internal string GitHubAuthenticationError { get; }
        internal bool GitHubProbeDeferred { get; }

        internal GitSubmoduleManagerSetupSnapshot WithChecking(bool isChecking)
        {
            return new GitSubmoduleManagerSetupSnapshot(
                isChecking,
                GitAvailable,
                GitVersion,
                GitError,
                GitHubCliAvailable,
                GitHubCliVersion,
                GitHubCliError,
                GitHubAuthenticationStatus,
                GitHubAuthenticationError,
                GitHubProbeDeferred);
        }
    }

    /// <summary>
    /// Runs Git and GitHub CLI setup checks away from Unity's main thread. The
    /// Welcome window and Preferences share one probe and receive completed
    /// snapshots through normal Editor update callbacks.
    /// </summary>
    internal sealed class GitSubmoduleManagerSetupProbe : IDisposable
    {
        internal const double CacheLifetimeSeconds = 30d;
        internal static GitSubmoduleManagerSetupProbe Shared { get; } = new(
            GitSubmoduleManagerSetupSessionCache.CreateDefault());

        private sealed class SetupProbeResult
        {
            internal int Generation;
            internal bool GitAvailable;
            internal string GitVersion = string.Empty;
            internal string GitError = string.Empty;
            internal bool GitHubCliAvailable;
            internal string GitHubCliVersion = string.Empty;
            internal string GitHubCliError = string.Empty;
            internal GitHubAuthenticationProbeStatus GitHubAuthenticationStatus =
                GitHubAuthenticationProbeStatus.Unknown;
            internal string GitHubAuthenticationError = string.Empty;
            internal bool GitHubProbeDeferred;
        }

        private CancellationTokenSource cancellationSource;
        private Thread probeThread;
        private volatile SetupProbeResult pendingResult;
        private readonly GitSubmoduleManagerSetupSessionCache sessionCache;
        private readonly Func<double> currentTimeProvider;
        private readonly Action<CancellationToken> probeStartOverride;
        private int generation;
        private bool hasStarted;
        private bool updateSubscribed;
        private bool disposed;
        private double lastCompletedTime = double.NegativeInfinity;

        static GitSubmoduleManagerSetupProbe()
        {
            AssemblyReloadEvents.beforeAssemblyReload += DisposeShared;
            EditorApplication.quitting += DisposeSharedAndClearCache;
        }

        internal GitSubmoduleManagerSetupProbe(
            GitSubmoduleManagerSetupSessionCache sessionCache,
            Func<double> currentTimeProvider = null,
            Action<CancellationToken> probeStartOverride = null)
        {
            this.sessionCache = sessionCache;
            this.currentTimeProvider = currentTimeProvider ??
                (() => EditorApplication.timeSinceStartup);
            this.probeStartOverride = probeStartOverride;
            if (sessionCache != null &&
                sessionCache.TryLoad(
                    CurrentTime,
                    out GitSubmoduleManagerSetupSnapshot cached,
                    out double completedAt))
            {
                Current = cached;
                lastCompletedTime = completedAt;
                hasStarted = true;
            }
        }

        internal event Action Changed;

        internal GitSubmoduleManagerSetupSnapshot Current { get; private set; } =
            GitSubmoduleManagerSetupSnapshot.Idle;

        internal void EnsureStarted()
        {
            double elapsedSinceCompletion =
                CurrentTime - lastCompletedTime;
            if (ShouldRefresh(
                    hasStarted,
                    Current.IsChecking,
                    elapsedSinceCompletion))
            {
                Start();
            }
        }

        internal static bool ShouldRefresh(
            bool alreadyStarted,
            bool isChecking,
            double elapsedSinceCompletion)
        {
            return !alreadyStarted ||
                   (!isChecking &&
                    elapsedSinceCompletion >= CacheLifetimeSeconds);
        }

        internal void Start()
        {
            if (disposed)
                throw new ObjectDisposedException(nameof(GitSubmoduleManagerSetupProbe));

            sessionCache?.Clear();
            CancellationTokenSource retiringCancellationSource =
                cancellationSource;
            Thread retiringThread = probeThread;
            RetireProbe(
                retiringCancellationSource,
                retiringThread,
                cancel: true);
            var nextCancellationSource = new CancellationTokenSource();
            int nextGeneration = Interlocked.Increment(ref generation);
            hasStarted = true;
            cancellationSource = nextCancellationSource;
            pendingResult = null;
            Current = Current.WithChecking(true);
            SubscribeToEditorUpdate();
            InvokeChanged();

            // Cache Unity-owned path state on the main thread before the worker
            // enters any Git helpers.
            _ = GitUtility.ProjectRoot;
            if (probeStartOverride != null)
            {
                try
                {
                    probeStartOverride(nextCancellationSource.Token);
                }
                catch (Exception exception)
                {
                    CompleteProbeStartFailure(
                        nextCancellationSource,
                        null,
                        exception);
                }

                return;
            }

            var thread = new Thread(
                () => Run(nextGeneration, nextCancellationSource.Token))
            {
                IsBackground = true,
                Name = "Git Submodule Manager setup probe"
            };
            probeThread = thread;
            try
            {
                thread.Start();
            }
            catch (Exception exception)
            {
                CompleteProbeStartFailure(
                    nextCancellationSource,
                    thread,
                    exception);
            }
        }

        internal bool TryConsumePendingResult()
        {
            SetupProbeResult result = pendingResult;
            if (result == null)
                return false;

            pendingResult = null;
            if (result.Generation != Volatile.Read(ref generation))
                return false;

            CancellationTokenSource completedCancellationSource =
                cancellationSource;
            Thread completedThread = probeThread;
            cancellationSource = null;
            probeThread = null;
            RetireProbe(
                completedCancellationSource,
                completedThread,
                cancel: false);
            Current = new GitSubmoduleManagerSetupSnapshot(
                false,
                result.GitAvailable,
                result.GitVersion,
                result.GitError,
                result.GitHubCliAvailable,
                result.GitHubCliVersion,
                result.GitHubCliError,
                result.GitHubAuthenticationStatus,
                result.GitHubAuthenticationError,
                result.GitHubProbeDeferred);
            lastCompletedTime = CurrentTime;
            sessionCache?.Save(Current, lastCompletedTime);
            return true;
        }

        private double CurrentTime => currentTimeProvider();

        private void CompleteProbeStartFailure(
            CancellationTokenSource source,
            Thread thread,
            Exception exception)
        {
            probeThread = null;
            cancellationSource = null;
            RetireProbe(source, thread, cancel: false);
            Current = new GitSubmoduleManagerSetupSnapshot(
                false,
                false,
                string.Empty,
                GitHubUtility.SanitizeUiDiagnostic(exception.Message),
                false,
                string.Empty,
                string.Empty,
                GitHubAuthenticationProbeStatus.Unknown,
                string.Empty,
                false);
            lastCompletedTime = CurrentTime;
            sessionCache?.Save(Current, lastCompletedTime);
            UnsubscribeFromEditorUpdate();
            InvokeChanged();
        }

        public void Dispose()
        {
            if (disposed)
                return;

            disposed = true;
            Interlocked.Increment(ref generation);
            CancellationTokenSource retiringCancellationSource =
                cancellationSource;
            Thread retiringThread = probeThread;
            cancellationSource = null;
            probeThread = null;
            pendingResult = null;
            UnsubscribeFromEditorUpdate();
            Changed = null;
            RetireProbe(
                retiringCancellationSource,
                retiringThread,
                cancel: true);
        }

        private static void DisposeShared()
        {
            Shared.Dispose();
        }

        private static void DisposeSharedAndClearCache()
        {
            Shared.sessionCache?.Clear();
            Shared.Dispose();
        }

        private static void RetireProbe(
            CancellationTokenSource source,
            Thread thread,
            bool cancel)
        {
            if (source == null)
                return;

            if (cancel)
                source.Cancel();

            if (thread == null || !thread.IsAlive)
            {
                source.Dispose();
                return;
            }

            ThreadPool.QueueUserWorkItem(_ =>
            {
                if (thread.Join(60000))
                    source.Dispose();
            });
        }

        private void SubscribeToEditorUpdate()
        {
            if (updateSubscribed)
                return;

            EditorApplication.update += Pump;
            updateSubscribed = true;
        }

        private void UnsubscribeFromEditorUpdate()
        {
            if (!updateSubscribed)
                return;

            EditorApplication.update -= Pump;
            updateSubscribed = false;
        }

        private void Pump()
        {
            if (TryConsumePendingResult())
            {
                UnsubscribeFromEditorUpdate();
                InvokeChanged();
                return;
            }

            if (!Current.IsChecking &&
                ShouldRefresh(
                    hasStarted,
                    false,
                    CurrentTime - lastCompletedTime))
            {
                Start();
            }
        }

        private void InvokeChanged()
        {
            Delegate[] subscribers = Changed?.GetInvocationList();
            if (subscribers == null)
                return;

            foreach (Delegate subscriber in subscribers)
            {
                try
                {
                    ((Action)subscriber).Invoke();
                }
                catch
                {
                    // A presentation callback must not interrupt probe lifecycle.
                }
            }
        }

        private void Run(int resultGeneration, CancellationToken cancellationToken)
        {
            var result = new SetupProbeResult { Generation = resultGeneration };
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
                    result.GitHubAuthenticationStatus =
                        GitHubUtility.ProbeAuthentication(
                            cancellationToken,
                            out string authenticationError,
                            out bool authenticationProbeDeferred);
                    result.GitHubAuthenticationError =
                        authenticationError ?? string.Empty;
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
                resultGeneration == Volatile.Read(ref generation))
            {
                pendingResult = result;
            }
        }
    }

    /// <summary>
    /// Shared IMGUI presentation for the Welcome window and Preferences page,
    /// keeping setup status and recovery actions consistent in both places.
    /// </summary>
    internal static class GitSubmoduleManagerSetupGUI
    {
        internal static void Draw(GitSubmoduleManagerSetupSnapshot snapshot)
        {
            snapshot ??= GitSubmoduleManagerSetupSnapshot.Idle;
            DrawGitStatus(snapshot);
            EditorGUILayout.Space(5f);
            DrawGitHubStatus(snapshot);
        }

        internal static string FormatInstalledMessage(string version)
        {
            return "\u2713 Installed — " + FirstLine(version);
        }

        internal static string FormatAuthenticatedMessage(string version)
        {
            return "\u2713 Installed and authenticated — " + FirstLine(version);
        }

        internal static string FirstLine(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return "version unavailable";

            string trimmed = value.Trim();
            int lineBreak = trimmed.IndexOfAny(new[] { '\r', '\n' });
            return lineBreak < 0 ? trimmed : trimmed.Substring(0, lineBreak);
        }

        private static void DrawGitStatus(GitSubmoduleManagerSetupSnapshot snapshot)
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField("Git — Required", EditorStyles.boldLabel);
            if (snapshot.IsChecking)
            {
                EditorGUILayout.LabelField(
                    "Checking Git...",
                    EditorStyles.wordWrappedLabel);
            }
            else if (snapshot.GitAvailable)
            {
                EditorGUILayout.HelpBox(
                    FormatInstalledMessage(snapshot.GitVersion),
                    MessageType.Info);
            }
            else
            {
                EditorGUILayout.HelpBox(
                    "Git was not detected. Package installation, conversion, and removal require Git.",
                    MessageType.Error);
                DrawSafeDiagnostic(snapshot.GitError, MessageType.Error);
                if (GUILayout.Button("Open Git Download Page"))
                    Application.OpenURL(CliInstaller.GetInstallUrl(ToolKind.Git));
            }

            EditorGUILayout.EndVertical();
        }

        private static void DrawGitHubStatus(
            GitSubmoduleManagerSetupSnapshot snapshot)
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField(
                "GitHub CLI — Recommended",
                EditorStyles.boldLabel);
            if (snapshot.IsChecking)
            {
                EditorGUILayout.LabelField(
                    "Checking GitHub CLI and authentication...",
                    EditorStyles.wordWrappedLabel);
            }
            else if (snapshot.GitHubProbeDeferred)
            {
                EditorGUILayout.HelpBox(
                    "A GitHub CLI operation is already active. Check again after it finishes.",
                    MessageType.Info);
            }
            else if (!snapshot.GitHubCliAvailable)
            {
                EditorGUILayout.HelpBox(
                    "GitHub CLI is optional for direct URL installs, but it is " +
                    "required to discover repositories visible to your account.",
                    MessageType.Warning);
                DrawSafeDiagnostic(snapshot.GitHubCliError, MessageType.Warning);
                if (GUILayout.Button("Open GitHub CLI Install Guide"))
                {
                    Application.OpenURL(
                        CliInstaller.GetInstallUrl(ToolKind.GitHubCli));
                }
            }
            else if (snapshot.GitHubAuthenticationStatus ==
                     GitHubAuthenticationProbeStatus.Unauthenticated)
            {
                EditorGUILayout.HelpBox(
                    FormatInstalledMessage(snapshot.GitHubCliVersion),
                    MessageType.Info);
                EditorGUILayout.HelpBox(
                    "GitHub CLI is not authenticated for github.com.",
                    MessageType.Warning);
                DrawSafeDiagnostic(
                    snapshot.GitHubAuthenticationError,
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
            else if (snapshot.GitHubAuthenticationStatus !=
                     GitHubAuthenticationProbeStatus.Authenticated)
            {
                EditorGUILayout.HelpBox(
                    FormatInstalledMessage(snapshot.GitHubCliVersion),
                    MessageType.Info);
                EditorGUILayout.HelpBox(
                    "GitHub authentication could not be verified. Check your " +
                    "network connection and choose Check Again.",
                    MessageType.Warning);
                DrawSafeDiagnostic(
                    snapshot.GitHubAuthenticationError,
                    MessageType.Warning);
                if (GUILayout.Button("Open Authentication Guide"))
                    Application.OpenURL(GitHubUtility.AuthenticationGuideUrl);
            }
            else
            {
                EditorGUILayout.HelpBox(
                    FormatAuthenticatedMessage(snapshot.GitHubCliVersion),
                    MessageType.Info);
            }

            EditorGUILayout.EndVertical();
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
    }

    /// <summary>
    /// Small, standalone setup surface retained after package management moved
    /// into Unity's native Package Manager. It performs command-line probes on a
    /// worker so opening Preferences or this window never blocks the Editor UI.
    /// </summary>
    internal sealed class GitSubmoduleManagerWelcomeWindow : EditorWindow
    {
        private const string DocumentationUrl =
            "https://github.com/martincalander/GitSubmoduleForUnity#quick-start";
        private const string ShownThisSessionKey =
            "MartinCalander.GitSubmoduleManager.WelcomeShownThisSession";

        private Vector2 scrollPosition;
        private GitSubmoduleManagerSetupProbe setupProbe;
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

            setupProbe = GitSubmoduleManagerSetupProbe.Shared;
            setupProbe.Changed -= OnSetupProbeChanged;
            setupProbe.Changed += OnSetupProbeChanged;
            setupProbe.EnsureStarted();
        }

        private void OnDisable()
        {
            if (setupProbe != null)
                setupProbe.Changed -= OnSetupProbeChanged;
            setupProbe = null;
        }

        private void Update()
        {
            setupProbe?.EnsureStarted();
        }

        private void OnSetupProbeChanged()
        {
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
            GitSubmoduleManagerSetupSnapshot setup =
                setupProbe?.Current ?? GitSubmoduleManagerSetupSnapshot.Idle;
            GitSubmoduleManagerSetupGUI.Draw(setup);

            if (!string.IsNullOrWhiteSpace(settingsError))
            {
                EditorGUILayout.Space(6f);
                EditorGUILayout.HelpBox(settingsError, MessageType.Error);
            }

            EditorGUILayout.Space(10f);
            using (new EditorGUI.DisabledScope(setup.IsChecking))
            {
                if (GUILayout.Button("Check Again", GUILayout.Height(24f)))
                    setupProbe?.Start();
            }

            EditorGUILayout.Space(10f);
            if (GUILayout.Button(
                    "Open GitHub Package Manager",
                    GUILayout.Height(30f)))
            {
                GitSubmoduleManagerPackageManagerHost.OpenGitHubSource();
                Close();
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
    }
}
