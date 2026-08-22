using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace MartinCalander.GitSubmoduleManager.Editor
{
    internal enum CommandTerminationScope
    {
        CompleteProcessTree,
        RootProcess
    }

    internal sealed class CommandSpec
    {
        public string FileName;
        public string Arguments;
        public IReadOnlyList<string> ArgumentList;
        public string WorkingDirectory;
        public int TimeoutMs = CliCommandRunner.DefaultTimeoutMs;
        public CancellationToken CancellationToken;
        public CommandTerminationScope TerminationScope = CommandTerminationScope.CompleteProcessTree;
    }

    internal sealed class CommandResult
    {
        public int ExitCode;
        public string StdOut;
        public string StdErr;
        public string ResolvedExecutablePath;
        public bool TimedOut;
        public bool Cancelled;
        public bool TerminationConfirmed;
        public bool StdOutTruncated;
        public bool StdErrTruncated;
        public bool BlockedByGitHubAuthentication;
        public string CompletionWarning;

        public bool IsSuccess => ExitCode == 0;
    }

    internal interface ICommandRunner
    {
        CommandResult Run(CommandSpec spec);
    }

    internal sealed class AsyncCommandHandle
    {
        private int isComplete;

        public bool IsComplete => Volatile.Read(ref isComplete) != 0;
        public CommandResult Result { get; private set; }
        public float Progress { get; private set; }
        public string StatusMessage { get; private set; }

        private readonly ICommandRunner runner;
        private readonly CommandSpec spec;
        private readonly Action<AsyncCommandHandle> onCompleted;
        private readonly CancellationTokenSource cancellationSource = new();
        private Thread workerThread;

        public AsyncCommandHandle(
            ICommandRunner runner,
            CommandSpec spec,
            Action<AsyncCommandHandle> onCompleted = null)
        {
            this.runner = runner;
            this.spec = spec;
            this.onCompleted = onCompleted;
            this.spec.CancellationToken = cancellationSource.Token;
            StatusMessage = "Starting...";
        }

        public void Start()
        {
            workerThread = new Thread(RunCommand)
            {
                IsBackground = true
            };
            workerThread.Start();
        }

        public void Cancel()
        {
            if (IsComplete || cancellationSource.IsCancellationRequested)
                return;

            StatusMessage = "Cancelling...";
            cancellationSource.Cancel();
        }

        public bool WaitForCompletion(int timeoutMs)
        {
            Thread worker = workerThread;
            if (worker == null)
                return IsComplete;
            if (ReferenceEquals(Thread.CurrentThread, worker))
                return IsComplete;

            return worker.Join(Math.Max(0, timeoutMs));
        }

        private void RunCommand()
        {
            try
            {
                StatusMessage = "Running command...";
                Progress = 0.1f;

                Result = runner.Run(spec);

                Progress = 1f;
                StatusMessage = Result.Cancelled ? "Cancelled" : Result.IsSuccess ? "Complete" : "Failed";
            }
            catch (Exception ex)
            {
                Result = new CommandResult
                {
                    ExitCode = -1,
                    StdOut = string.Empty,
                    StdErr = ex.Message
                };
                Progress = 1f;
                StatusMessage = "Failed";
            }
            finally
            {
                try
                {
                    onCompleted?.Invoke(this);
                }
                catch
                {
                    // Completion bookkeeping must never crash a worker thread.
                }
                // Publish Result, final status, and ownership bookkeeping before
                // readers can act on the completed command.
                Volatile.Write(ref isComplete, 1);
            }
        }
    }

    /// <summary>
    /// Keeps canceled coordinator commands owned until their worker threads have
    /// actually reached a terminal state. This prevents a newly opened package
    /// manager window from starting replacement discovery work while commands
    /// from a disposed window are still running.
    /// </summary>
    internal static class AsyncCommandDrainRegistry
    {
        private static readonly object Gate = new();
        private static readonly List<AsyncCommandHandle> RetiredHandles = new();

        internal static bool IsDraining
        {
            get
            {
                lock (Gate)
                {
                    RemoveCompletedHandles();
                    return RetiredHandles.Count > 0;
                }
            }
        }

        internal static bool RequiresEditorRestart
        {
            get
            {
                lock (Gate)
                {
                    RemoveCompletedHandles();
                    return RetiredHandles.Any(handle =>
                        handle != null &&
                        handle.IsComplete &&
                        (handle.Result == null ||
                         !handle.Result.TerminationConfirmed));
                }
            }
        }

        internal static string StatusMessage
        {
            get
            {
                lock (Gate)
                {
                    RemoveCompletedHandles();
                    if (RetiredHandles.Any(handle =>
                            handle != null &&
                            handle.IsComplete &&
                            (handle.Result == null ||
                             !handle.Result.TerminationConfirmed)))
                    {
                        return "A previous command could not confirm that every process stopped. " +
                               "Save your work and restart Unity before retrying repository discovery.";
                    }

                    return RetiredHandles.Count > 0
                        ? "Waiting for a previous command to stop safely..."
                        : string.Empty;
                }
            }
        }

        internal static void Retire(AsyncCommandHandle handle)
        {
            if (handle == null)
                return;

            lock (Gate)
            {
                RemoveCompletedHandles();
                if ((!handle.IsComplete || handle.Result == null || !handle.Result.TerminationConfirmed) &&
                    !RetiredHandles.Contains(handle))
                    RetiredHandles.Add(handle);
            }

            handle.Cancel();
        }

        private static void RemoveCompletedHandles()
        {
            // A completed worker is not enough after forced cancellation: a
            // detached descendant may still be alive. Keep the global drain
            // barrier until a confirmed result or an Editor/domain restart.
            RetiredHandles.RemoveAll(handle =>
                handle == null ||
                (handle.IsComplete &&
                 handle.Result != null &&
                 handle.Result.TerminationConfirmed));
        }
    }

    internal static class CliCommandRunner
    {
        internal const int DefaultTimeoutMs = 30000;
        internal const int MaxCapturedCharactersPerStream = 512 * 1024;
        private static readonly object GitHubCommandGate = new();
        private static ICommandRunner s_currentRunner = new ProcessCommandRunner();
        private static int s_activeGitHubCommandCount;
        private static bool s_gitHubAuthenticationReserved;
        private static bool s_gitHubCommandRestartRequired;

        internal static bool HasActiveGitHubCommands
        {
            get
            {
                lock (GitHubCommandGate)
                    return s_activeGitHubCommandCount > 0;
            }
        }

        internal static bool IsGitHubAuthenticationReserved
        {
            get
            {
                lock (GitHubCommandGate)
                    return s_gitHubAuthenticationReserved;
            }
        }

        internal static bool GitHubCommandRequiresEditorRestart
        {
            get
            {
                lock (GitHubCommandGate)
                    return s_gitHubCommandRestartRequired;
            }
        }

        internal static bool TryReserveGitHubAuthentication()
        {
            lock (GitHubCommandGate)
            {
                if (s_gitHubAuthenticationReserved ||
                    s_activeGitHubCommandCount > 0 ||
                    s_gitHubCommandRestartRequired)
                    return false;

                s_gitHubAuthenticationReserved = true;
                return true;
            }
        }

        internal static void ReleaseGitHubAuthenticationReservation()
        {
            lock (GitHubCommandGate)
                s_gitHubAuthenticationReserved = false;
        }

        internal static ICommandRunner CurrentRunner
        {
            get => s_currentRunner;
            set => s_currentRunner = value ?? new ProcessCommandRunner();
        }

        internal static AsyncCommandHandle RunAsync(
            string fileName,
            string arguments,
            string workingDir,
            int timeoutMs = DefaultTimeoutMs,
            CommandTerminationScope terminationScope = CommandTerminationScope.CompleteProcessTree,
            bool isGitHubAuthenticationCommand = false)
        {
            if (!TryBeginGitHubCommand(
                    fileName,
                    isGitHubAuthenticationCommand,
                    out bool tracksGitHubCommand))
            {
                throw new InvalidOperationException(
                    "GitHub CLI discovery is blocked while authentication owns the process gate.");
            }

            var handle = new AsyncCommandHandle(CurrentRunner, new CommandSpec
            {
                FileName = fileName,
                Arguments = arguments,
                WorkingDirectory = workingDir,
                TimeoutMs = timeoutMs,
                TerminationScope = terminationScope
            }, tracksGitHubCommand ? (Action<AsyncCommandHandle>)CompleteGitHubCommand : null);
            try
            {
                handle.Start();
                return handle;
            }
            catch
            {
                if (tracksGitHubCommand)
                    CompleteGitHubCommand(terminationConfirmed: true);
                throw;
            }
        }

        internal static AsyncCommandHandle RunAsync(
            string fileName,
            IReadOnlyList<string> arguments,
            string workingDir,
            int timeoutMs = DefaultTimeoutMs,
            CommandTerminationScope terminationScope = CommandTerminationScope.CompleteProcessTree,
            bool isGitHubAuthenticationCommand = false)
        {
            if (!TryBeginGitHubCommand(
                    fileName,
                    isGitHubAuthenticationCommand,
                    out bool tracksGitHubCommand))
            {
                throw new InvalidOperationException(
                    "GitHub CLI discovery is blocked while authentication owns the process gate.");
            }

            var handle = new AsyncCommandHandle(CurrentRunner, new CommandSpec
            {
                FileName = fileName,
                ArgumentList = arguments,
                WorkingDirectory = workingDir,
                TimeoutMs = timeoutMs,
                TerminationScope = terminationScope
            }, tracksGitHubCommand ? (Action<AsyncCommandHandle>)CompleteGitHubCommand : null);
            try
            {
                handle.Start();
                return handle;
            }
            catch
            {
                if (tracksGitHubCommand)
                    CompleteGitHubCommand(terminationConfirmed: true);
                throw;
            }
        }

        internal static CommandResult Run(string fileName, string arguments, string workingDir, int timeoutMs = DefaultTimeoutMs)
        {
            return Run(fileName, arguments, workingDir, timeoutMs, CancellationToken.None);
        }

        internal static CommandResult Run(
            string fileName,
            string arguments,
            string workingDir,
            int timeoutMs,
            CancellationToken cancellationToken)
        {
            if (!TryBeginGitHubCommand(
                    fileName,
                    isGitHubAuthenticationCommand: false,
                    out bool tracksGitHubCommand))
            {
                return CreateBlockedGitHubCommandResult();
            }
            CommandResult result = null;
            try
            {
                result = CurrentRunner.Run(new CommandSpec
                {
                    FileName = fileName,
                    Arguments = arguments,
                    WorkingDirectory = workingDir,
                    TimeoutMs = timeoutMs,
                    CancellationToken = cancellationToken
                });
                return result;
            }
            finally
            {
                if (tracksGitHubCommand)
                    CompleteGitHubCommand(result?.TerminationConfirmed == true);
            }
        }

        internal static CommandResult Run(
            string fileName,
            IReadOnlyList<string> arguments,
            string workingDir,
            int timeoutMs = DefaultTimeoutMs)
        {
            return Run(fileName, arguments, workingDir, timeoutMs, CancellationToken.None);
        }

        internal static CommandResult Run(
            string fileName,
            IReadOnlyList<string> arguments,
            string workingDir,
            int timeoutMs,
            CancellationToken cancellationToken)
        {
            if (!TryBeginGitHubCommand(
                    fileName,
                    isGitHubAuthenticationCommand: false,
                    out bool tracksGitHubCommand))
            {
                return CreateBlockedGitHubCommandResult();
            }
            CommandResult result = null;
            try
            {
                result = CurrentRunner.Run(new CommandSpec
                {
                    FileName = fileName,
                    ArgumentList = arguments,
                    WorkingDirectory = workingDir,
                    TimeoutMs = timeoutMs,
                    CancellationToken = cancellationToken
                });
                return result;
            }
            finally
            {
                if (tracksGitHubCommand)
                    CompleteGitHubCommand(result?.TerminationConfirmed == true);
            }
        }

        private static bool IsGitHubCliCommand(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName))
                return false;

            string executableName = Path.GetFileNameWithoutExtension(fileName.Trim());
            return string.Equals(executableName, "gh", StringComparison.OrdinalIgnoreCase);
        }

        private static void CompleteGitHubCommand(AsyncCommandHandle handle)
        {
            CompleteGitHubCommand(handle?.Result?.TerminationConfirmed == true);
        }

        private static void CompleteGitHubCommand(bool terminationConfirmed)
        {
            lock (GitHubCommandGate)
            {
                if (!terminationConfirmed)
                    s_gitHubCommandRestartRequired = true;
                s_activeGitHubCommandCount--;
            }
        }

        private static bool TryBeginGitHubCommand(
            string fileName,
            bool isGitHubAuthenticationCommand,
            out bool tracksGitHubCommand)
        {
            tracksGitHubCommand = IsGitHubCliCommand(fileName);
            if (!tracksGitHubCommand)
                return true;

            lock (GitHubCommandGate)
            {
                if (s_gitHubCommandRestartRequired)
                    return false;

                if (isGitHubAuthenticationCommand)
                {
                    if (!s_gitHubAuthenticationReserved)
                        return false;
                }
                else if (s_gitHubAuthenticationReserved)
                {
                    return false;
                }

                s_activeGitHubCommandCount++;
                return true;
            }
        }

        private static CommandResult CreateBlockedGitHubCommandResult()
        {
            return new CommandResult
            {
                ExitCode = -1,
                StdOut = string.Empty,
                StdErr = "GitHub CLI authentication is active; this request was deferred.",
                TerminationConfirmed = true,
                BlockedByGitHubAuthentication = true
            };
        }

        internal static bool IsCommandAvailable(string fileName)
        {
            return ProcessCommandRunner.IsCommandAvailable(fileName);
        }

        internal static bool TryResolveCommand(string fileName, out string resolvedPath)
        {
            return ProcessCommandRunner.TryResolveCommand(fileName, out resolvedPath);
        }

        internal static void ResetRunner()
        {
            CurrentRunner = new ProcessCommandRunner();
        }
    }

    internal enum ExecutableResolutionSource
    {
        ExplicitPath,
        EnvironmentPath,
        PlatformSearchPath
    }

    internal sealed class ExecutableResolution
    {
        internal string RequestedName = string.Empty;
        internal string ResolvedPath = string.Empty;
        internal ExecutableResolutionSource Source;
        internal bool IsKnownPlatformLocation;
    }

    internal sealed class BoundedTextBuffer
    {
        private const string TruncationNotice = "[output truncated; showing the most recent data]";
        private readonly object syncRoot = new();
        private readonly char[] buffer;
        private readonly int maximumCharacters;
        private int startIndex;
        private int characterCount;
        private bool hasData;
        private bool isTruncated;

        internal BoundedTextBuffer(int maximumCharacters)
        {
            if (maximumCharacters <= 0)
                throw new ArgumentOutOfRangeException(nameof(maximumCharacters));

            this.maximumCharacters = maximumCharacters;
            buffer = new char[maximumCharacters];
        }

        internal bool IsTruncated
        {
            get
            {
                lock (syncRoot)
                    return isTruncated;
            }
        }

        internal void AppendLine(string value)
        {
            if (value == null)
                return;

            lock (syncRoot)
            {
                if (hasData)
                    AppendFragment(Environment.NewLine);

                AppendFragment(value);
                hasData = true;
            }
        }

        internal void Append(string value)
        {
            if (string.IsNullOrEmpty(value))
                return;

            lock (syncRoot)
            {
                AppendFragment(value);
                hasData = true;
            }
        }

        internal string GetSnapshot()
        {
            lock (syncRoot)
            {
                string snapshot = GetBufferContents();
                if (!isTruncated)
                    return snapshot;

                return TruncationNotice + Environment.NewLine + snapshot;
            }
        }

        internal void MarkTruncated()
        {
            lock (syncRoot)
                isTruncated = true;
        }

        private void AppendFragment(string value)
        {
            if (string.IsNullOrEmpty(value))
                return;

            if (value.Length >= maximumCharacters)
            {
                value.CopyTo(
                    value.Length - maximumCharacters,
                    buffer,
                    0,
                    maximumCharacters);
                startIndex = 0;
                characterCount = maximumCharacters;
                isTruncated = true;
                return;
            }

            int overflow = characterCount + value.Length - maximumCharacters;
            if (overflow > 0)
            {
                startIndex = (startIndex + overflow) % maximumCharacters;
                characterCount -= overflow;
                isTruncated = true;
            }

            int writeIndex = (startIndex + characterCount) % maximumCharacters;
            int firstCopyLength = Math.Min(value.Length, maximumCharacters - writeIndex);
            value.CopyTo(0, buffer, writeIndex, firstCopyLength);
            int remaining = value.Length - firstCopyLength;
            if (remaining > 0)
                value.CopyTo(firstCopyLength, buffer, 0, remaining);
            characterCount += value.Length;
        }

        private string GetBufferContents()
        {
            if (characterCount == 0)
                return string.Empty;

            int firstLength = Math.Min(characterCount, maximumCharacters - startIndex);
            if (firstLength == characterCount)
                return new string(buffer, startIndex, characterCount);

            return new string(buffer, startIndex, firstLength) +
                   new string(buffer, 0, characterCount - firstLength);
        }
    }

    internal sealed class ProcessCommandRunner : ICommandRunner
    {
        private const int WaitPollIntervalMs = 50;
        private const int ProcessTreeTerminationTimeoutMs = 5000;
        private const int OutputDrainTimeoutMs = 1000;
        private const int MaximumEnumeratedProcesses = 100000;

        public CommandResult Run(CommandSpec spec)
        {
            if (spec == null)
            {
                return Failure("Command specification was null.");
            }

            if (spec.CancellationToken.IsCancellationRequested)
            {
                return new CommandResult
                {
                    ExitCode = -1,
                    StdOut = string.Empty,
                    StdErr = "Command was cancelled before it started.",
                    Cancelled = true,
                    TerminationConfirmed = true
                };
            }

            if (!TryResolveCommand(spec.FileName, out ExecutableResolution resolution))
            {
                return Failure($"Command not found: {spec.FileName}");
            }

            if (!TryGetArguments(spec, out IReadOnlyList<string> arguments, out string argumentError))
            {
                return Failure(argumentError, resolution.ResolvedPath);
            }

            Process process = null;
            bool processStarted = false;
            try
            {
                var startInfo = BuildProcessStartInfo(
                    resolution.ResolvedPath,
                    arguments,
                    spec.WorkingDirectory);
                process = new Process { StartInfo = startInfo };
                var stdOut = new BoundedTextBuffer(CliCommandRunner.MaxCapturedCharactersPerStream);
                var stdErr = new BoundedTextBuffer(CliCommandRunner.MaxCapturedCharactersPerStream);
                var stdOutCompleted = new ManualResetEventSlim(false);
                var stdErrCompleted = new ManualResetEventSlim(false);

                if (!process.Start())
                    return Failure($"Failed to start process: {spec.FileName}", resolution.ResolvedPath);
                processStarted = true;
                Thread stdOutReader = StartBoundedReader(
                    process.StandardOutput,
                    stdOut,
                    stdOutCompleted,
                    "Git Submodule Manager stdout reader");
                Thread stdErrReader = StartBoundedReader(
                    process.StandardError,
                    stdErr,
                    stdErrCompleted,
                    "Git Submodule Manager stderr reader");

                CommandEndReason endReason = WaitForCommand(process, spec.TimeoutMs, spec.CancellationToken);
                if (endReason == CommandEndReason.Exited)
                {
                    DrainRedirectedOutput(
                        process,
                        stdOut,
                        stdErr,
                        stdOutReader,
                        stdErrReader,
                        stdOutCompleted,
                        stdErrCompleted);
                    DisposeCompletedEvent(stdOutCompleted);
                    DisposeCompletedEvent(stdErrCompleted);

                    return CreateResult(
                        process.ExitCode,
                        stdOut,
                        stdErr,
                        resolution.ResolvedPath,
                        false,
                        false,
                        true,
                        null);
                }

                bool rootProcessTerminationConfirmed = TerminateAndWaitForProcessTree(process);
                DrainRedirectedOutput(
                    process,
                    stdOut,
                    stdErr,
                    stdOutReader,
                    stdErrReader,
                    stdOutCompleted,
                    stdErrCompleted);
                DisposeCompletedEvent(stdOutCompleted);
                DisposeCompletedEvent(stdErrCompleted);

                // Repository commands require proof for the complete process tree,
                // which the current cross-platform runner cannot provide after a
                // forced stop. Narrow non-repository commands may explicitly accept
                // confirmed root-process exit as their safe retry boundary.
                bool terminationConfirmed =
                    spec.TerminationScope == CommandTerminationScope.RootProcess &&
                    rootProcessTerminationConfirmed;
                bool cancelled = endReason == CommandEndReason.Cancelled;
                string message = cancelled
                    ? $"Command was cancelled: {Path.GetFileName(resolution.ResolvedPath)}."
                    : $"Command timed out after {Math.Max(1, spec.TimeoutMs)}ms: {Path.GetFileName(resolution.ResolvedPath)}.";
                if (!terminationConfirmed)
                {
                    message += spec.TerminationScope == CommandTerminationScope.RootProcess
                        ? " Process termination could not be confirmed."
                        : " Process-tree termination could not be confirmed.";
                }

                return CreateResult(
                    -1,
                    stdOut,
                    stdErr,
                    resolution.ResolvedPath,
                    !cancelled,
                    cancelled,
                    terminationConfirmed,
                    message);
            }
            catch (Exception ex)
            {
                bool terminationConfirmed = !processStarted;
                if (processStarted)
                {
                    bool rootProcessTerminationConfirmed = TerminateAndWaitForProcessTree(process);
                    terminationConfirmed =
                        spec.TerminationScope == CommandTerminationScope.RootProcess &&
                        rootProcessTerminationConfirmed;
                }
                CommandResult failure = Failure(ex.Message, resolution.ResolvedPath);
                failure.TerminationConfirmed = terminationConfirmed;
                if (!terminationConfirmed)
                {
                    failure.StdErr += spec.TerminationScope == CommandTerminationScope.RootProcess
                        ? " Process termination could not be confirmed."
                        : " Process-tree termination could not be confirmed.";
                }
                return failure;
            }
            finally
            {
                process?.Dispose();
            }
        }

        internal static ProcessStartInfo BuildProcessStartInfo(
            string resolvedExecutablePath,
            IReadOnlyList<string> arguments,
            string workingDirectory)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = resolvedExecutablePath,
                WorkingDirectory = string.IsNullOrWhiteSpace(workingDirectory)
                    ? Environment.CurrentDirectory
                    : Path.GetFullPath(workingDirectory),
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            SanitizeInheritedEnvironment(startInfo, resolvedExecutablePath);
            startInfo.EnvironmentVariables["GIT_TERMINAL_PROMPT"] = "0";
            startInfo.EnvironmentVariables["GCM_INTERACTIVE"] = "Never";
            startInfo.EnvironmentVariables["GIT_PAGER"] = "cat";
            startInfo.EnvironmentVariables["GIT_EDITOR"] = "true";
            startInfo.EnvironmentVariables["GIT_SEQUENCE_EDITOR"] = "true";
            startInfo.EnvironmentVariables["SSH_ASKPASS_REQUIRE"] = "never";
            startInfo.EnvironmentVariables["PATH"] = BuildSearchPath();

            // Unity 2021's Mono surface exposes ProcessStartInfo.ArgumentList but
            // silently ignores it at process launch. Encode the already-tokenized
            // argv explicitly so the package's minimum supported Unity version
            // receives the same arguments as newer Editors.
            startInfo.Arguments = EncodeArgumentList(arguments);

            return startInfo;
        }

        private static void SanitizeInheritedEnvironment(
            ProcessStartInfo startInfo,
            string resolvedExecutablePath)
        {
            string executableName = Path.GetFileNameWithoutExtension(resolvedExecutablePath ?? string.Empty);
            bool isGitHubCli = string.Equals(executableName, "gh", StringComparison.OrdinalIgnoreCase);
            var keys = startInfo.EnvironmentVariables.Keys.Cast<string>().ToArray();
            foreach (string key in keys)
            {
                if (string.IsNullOrWhiteSpace(key))
                    continue;

                string upper = key.ToUpperInvariant();
                bool isGitOverride = upper.StartsWith("GIT_", StringComparison.Ordinal) ||
                                     upper.StartsWith("GCM_", StringComparison.Ordinal);
                bool isAskPassCommand = upper == "SSH_ASKPASS" || upper == "SSH_ASKPASS_REQUIRE";
                bool isKnownUnitySecret = upper == "UNITY_EMAIL" ||
                                          upper == "UNITY_PASSWORD" ||
                                          upper == "UNITY_SERIAL" ||
                                          upper == "UNITY_LICENSE";
                bool isCloudOrApiSecret = upper.StartsWith("AWS_", StringComparison.Ordinal) ||
                                          upper.StartsWith("AZURE_", StringComparison.Ordinal) ||
                                          upper.StartsWith("OPENAI_", StringComparison.Ordinal) ||
                                          upper.StartsWith("ANTHROPIC_", StringComparison.Ordinal) ||
                                          upper == "GOOGLE_APPLICATION_CREDENTIALS" ||
                                          upper.Contains("PRIVATE_KEY") ||
                                          upper.EndsWith("_API_KEY", StringComparison.Ordinal) ||
                                          upper.EndsWith("_PASSWORD", StringComparison.Ordinal) ||
                                          upper.EndsWith("_SECRET", StringComparison.Ordinal) ||
                                          upper.EndsWith("_TOKEN", StringComparison.Ordinal);
                bool isIntentionalGitHubCredential = isGitHubCli &&
                                                     (upper == "GH_TOKEN" ||
                                                      upper == "GITHUB_TOKEN" ||
                                                      upper == "GH_ENTERPRISE_TOKEN");

                if (isGitOverride ||
                    isAskPassCommand ||
                    isKnownUnitySecret ||
                    (isCloudOrApiSecret && !isIntentionalGitHubCredential))
                {
                    startInfo.EnvironmentVariables.Remove(key);
                }
            }
        }

        internal static bool IsCommandAvailable(string fileName)
        {
            return TryResolveCommand(fileName, out ExecutableResolution _);
        }

        internal static bool TryResolveCommand(string fileName, out string resolvedPath)
        {
            if (TryResolveCommand(fileName, out ExecutableResolution resolution))
            {
                resolvedPath = resolution.ResolvedPath;
                return true;
            }

            resolvedPath = string.Empty;
            return false;
        }

        internal static bool TryResolveCommand(string fileName, out ExecutableResolution resolution)
        {
            resolution = null;
            if (string.IsNullOrWhiteSpace(fileName))
                return false;

            string requestedName = fileName.Trim();
            if (requestedName.Contains("/") || requestedName.Contains("\\") || Path.IsPathRooted(requestedName))
            {
                if (!TryCanonicalizeExecutable(requestedName, out string canonicalPath))
                    return false;

                resolution = new ExecutableResolution
                {
                    RequestedName = requestedName,
                    ResolvedPath = canonicalPath,
                    Source = ExecutableResolutionSource.ExplicitPath,
                    IsKnownPlatformLocation = IsKnownPlatformExecutablePath(canonicalPath)
                };
                return true;
            }

            foreach (string directory in GetSearchPaths())
            {
                foreach (string candidate in ExpandWithExtensions(Path.Combine(directory, requestedName)))
                {
                    if (!TryCanonicalizeExecutable(candidate, out string canonicalPath))
                        continue;

                    bool knownLocation = IsKnownPlatformExecutablePath(canonicalPath);
                    resolution = new ExecutableResolution
                    {
                        RequestedName = requestedName,
                        ResolvedPath = canonicalPath,
                        Source = knownLocation
                            ? ExecutableResolutionSource.PlatformSearchPath
                            : ExecutableResolutionSource.EnvironmentPath,
                        IsKnownPlatformLocation = knownLocation
                    };
                    return true;
                }
            }

            return false;
        }

        internal static bool IsKnownPlatformExecutablePath(string executablePath)
        {
            if (string.IsNullOrWhiteSpace(executablePath))
                return false;

            string canonicalPath;
            try
            {
                canonicalPath = Path.GetFullPath(executablePath);
            }
            catch
            {
                return false;
            }

            StringComparison comparison = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            foreach (string platformPath in GetPlatformSearchPaths())
            {
                if (string.IsNullOrWhiteSpace(platformPath))
                    continue;

                string fullPlatformPath;
                try
                {
                    fullPlatformPath = Path.GetFullPath(platformPath)
                        .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                }
                catch
                {
                    continue;
                }

                string prefix = fullPlatformPath + Path.DirectorySeparatorChar;
                if (canonicalPath.StartsWith(prefix, comparison))
                    return true;
            }

            return false;
        }

        internal static bool TryTokenizeArguments(string commandLine, out IReadOnlyList<string> arguments)
        {
            var result = new List<string>();
            arguments = result;
            if (string.IsNullOrWhiteSpace(commandLine))
                return true;

            int index = 0;
            while (index < commandLine.Length)
            {
                while (index < commandLine.Length && char.IsWhiteSpace(commandLine[index]))
                    index++;

                if (index >= commandLine.Length)
                    break;

                var argument = new StringBuilder();
                bool inQuotes = false;
                bool tokenStarted = false;
                while (index < commandLine.Length)
                {
                    char current = commandLine[index];
                    if (!inQuotes && char.IsWhiteSpace(current))
                        break;

                    if (current == '"')
                    {
                        inQuotes = !inQuotes;
                        tokenStarted = true;
                        index++;
                        continue;
                    }

                    if (current == '\\')
                    {
                        int backslashStart = index;
                        while (index < commandLine.Length && commandLine[index] == '\\')
                            index++;

                        int backslashCount = index - backslashStart;
                        if (index < commandLine.Length && commandLine[index] == '"')
                        {
                            bool quoteEndsArgument = inQuotes &&
                                (index + 1 >= commandLine.Length || char.IsWhiteSpace(commandLine[index + 1]));

                            if (backslashCount % 2 == 0)
                            {
                                argument.Append('\\', backslashCount / 2);
                                inQuotes = !inQuotes;
                                tokenStarted = true;
                                index++;
                                continue;
                            }

                            // GitUtility historically emitted a single trailing
                            // backslash before its closing quote. Accept that legacy
                            // shape while the encoder uses the standard doubled form.
                            if (backslashCount == 1 && quoteEndsArgument)
                            {
                                argument.Append('\\');
                                inQuotes = false;
                                tokenStarted = true;
                                index++;
                                continue;
                            }

                            argument.Append('\\', backslashCount / 2);
                            argument.Append('"');
                            tokenStarted = true;
                            index++;
                            continue;
                        }

                        argument.Append('\\', backslashCount);
                        tokenStarted = true;
                        continue;
                    }

                    argument.Append(current);
                    tokenStarted = true;
                    index++;
                }

                if (inQuotes)
                    return false;

                if (tokenStarted)
                    result.Add(argument.ToString());

                while (index < commandLine.Length && char.IsWhiteSpace(commandLine[index]))
                    index++;
            }

            return true;
        }

        internal static string EncodeArgumentList(IReadOnlyList<string> arguments)
        {
            if (arguments == null || arguments.Count == 0)
                return string.Empty;

            var encoded = new StringBuilder();
            for (int i = 0; i < arguments.Count; i++)
            {
                if (i > 0)
                    encoded.Append(' ');

                encoded.Append(EncodeArgument(arguments[i] ?? string.Empty));
            }

            return encoded.ToString();
        }

        private static string EncodeArgument(string argument)
        {
            if (argument.Length > 0 && argument.IndexOfAny(new[] { ' ', '\t', '\r', '\n', '"' }) < 0)
                return argument;

            var encoded = new StringBuilder(argument.Length + 2);
            encoded.Append('"');
            int backslashCount = 0;
            foreach (char character in argument)
            {
                if (character == '\\')
                {
                    backslashCount++;
                    continue;
                }

                if (character == '"')
                {
                    encoded.Append('\\', backslashCount * 2 + 1);
                    encoded.Append('"');
                    backslashCount = 0;
                    continue;
                }

                if (backslashCount > 0)
                {
                    encoded.Append('\\', backslashCount);
                    backslashCount = 0;
                }

                encoded.Append(character);
            }

            if (backslashCount > 0)
                encoded.Append('\\', backslashCount * 2);

            encoded.Append('"');
            return encoded.ToString();
        }

        private static bool TryGetArguments(
            CommandSpec spec,
            out IReadOnlyList<string> arguments,
            out string error)
        {
            error = string.Empty;
            if (spec.ArgumentList != null)
            {
                arguments = new List<string>(spec.ArgumentList);
                return true;
            }

            if (TryTokenizeArguments(spec.Arguments, out arguments))
                return true;

            error = "Command arguments contain an unterminated quoted value.";
            return false;
        }

        private static CommandEndReason WaitForCommand(
            Process process,
            int timeoutMs,
            CancellationToken cancellationToken)
        {
            int effectiveTimeoutMs = Math.Max(1, timeoutMs);
            var stopwatch = Stopwatch.StartNew();
            while (true)
            {
                if (cancellationToken.IsCancellationRequested)
                    return CommandEndReason.Cancelled;

                int remaining = effectiveTimeoutMs - (int)Math.Min(int.MaxValue, stopwatch.ElapsedMilliseconds);
                if (remaining <= 0)
                    return CommandEndReason.TimedOut;

                if (process.WaitForExit(Math.Min(WaitPollIntervalMs, remaining)))
                    return CommandEndReason.Exited;
            }
        }

        private static bool TerminateAndWaitForProcessTree(Process process)
        {
            try
            {
                if (process.HasExited)
                    return true;
            }
            catch
            {
                return false;
            }

            if (TryKillEntireProcessTreeWithRuntime(process))
                return TryWaitForExit(process, ProcessTreeTerminationTimeoutMs);

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                bool taskKillSucceeded = TryRunTaskKill(process.Id);
                bool rootExited = TryWaitForExit(process, ProcessTreeTerminationTimeoutMs);
                if (taskKillSucceeded && rootExited)
                    return true;
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ||
                     RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                if (TryTerminateUnixProcessTree(process))
                    return true;
            }

            TryKillRootProcess(process);
            TryWaitForExit(process, ProcessTreeTerminationTimeoutMs);
            return false;
        }

        private static bool TryKillEntireProcessTreeWithRuntime(Process process)
        {
            MethodInfo killTreeMethod = typeof(Process).GetMethod(
                "Kill",
                BindingFlags.Instance | BindingFlags.Public,
                null,
                new[] { typeof(bool) },
                null);
            if (killTreeMethod == null)
                return false;

            try
            {
                killTreeMethod.Invoke(process, new object[] { true });
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryTerminateUnixProcessTree(Process process)
        {
            int rootProcessId = process.Id;
            int stopSignal = RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? 17 : 19;
            if (NativeKill(rootProcessId, stopSignal) != 0)
                return false;

            var trackedProcessIds = new HashSet<int> { rootProcessId };
            bool enumerationSucceeded = true;
            for (int pass = 0; pass < 4; pass++)
            {
                if (!TryGetDescendantProcessIds(rootProcessId, out List<int> descendants))
                {
                    enumerationSucceeded = false;
                    break;
                }

                bool foundNewProcess = false;
                foreach (int processId in descendants)
                {
                    if (!trackedProcessIds.Add(processId))
                        continue;

                    foundNewProcess = true;
                    NativeKill(processId, stopSignal);
                }

                if (!foundNewProcess)
                    break;

                Thread.Sleep(20);
            }

            foreach (int processId in trackedProcessIds)
            {
                if (processId != rootProcessId)
                    NativeKill(processId, 9);
            }
            NativeKill(rootProcessId, 9);

            return enumerationSucceeded &&
                WaitForProcessIdsToExit(trackedProcessIds, ProcessTreeTerminationTimeoutMs);
        }

        private static bool TryGetDescendantProcessIds(int rootProcessId, out List<int> descendants)
        {
            descendants = new List<int>();
            string psPath = File.Exists("/bin/ps") ? "/bin/ps" : "/usr/bin/ps";
            if (!File.Exists(psPath))
                return false;

            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = psPath,
                    Arguments = "-axo pid=,ppid=",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var psProcess = Process.Start(startInfo);
                if (psProcess == null)
                    return false;

                string output = psProcess.StandardOutput.ReadToEnd();
                if (!psProcess.WaitForExit(2000) || psProcess.ExitCode != 0)
                    return false;

                var childrenByParent = new Dictionary<int, List<int>>();
                string[] lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                if (lines.Length > MaximumEnumeratedProcesses)
                    return false;

                foreach (string line in lines)
                {
                    string[] fields = line.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                    if (fields.Length < 2 ||
                        !int.TryParse(fields[0], out int processId) ||
                        !int.TryParse(fields[1], out int parentProcessId) ||
                        processId <= 1 || processId == Process.GetCurrentProcess().Id)
                    {
                        continue;
                    }

                    if (!childrenByParent.TryGetValue(parentProcessId, out List<int> children))
                    {
                        children = new List<int>();
                        childrenByParent[parentProcessId] = children;
                    }
                    children.Add(processId);
                }

                var pending = new Stack<int>();
                pending.Push(rootProcessId);
                var seen = new HashSet<int> { rootProcessId };
                while (pending.Count > 0)
                {
                    int parentProcessId = pending.Pop();
                    if (!childrenByParent.TryGetValue(parentProcessId, out List<int> children))
                        continue;

                    foreach (int childProcessId in children)
                    {
                        if (!seen.Add(childProcessId))
                            continue;

                        descendants.Add(childProcessId);
                        pending.Push(childProcessId);
                    }
                }

                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool WaitForProcessIdsToExit(IEnumerable<int> processIds, int timeoutMs)
        {
            var pending = new HashSet<int>(processIds);
            var stopwatch = Stopwatch.StartNew();
            while (pending.Count > 0 && stopwatch.ElapsedMilliseconds < timeoutMs)
            {
                pending.RemoveWhere(processId => !IsProcessRunning(processId));
                if (pending.Count > 0)
                    Thread.Sleep(WaitPollIntervalMs);
            }

            pending.RemoveWhere(processId => !IsProcessRunning(processId));
            return pending.Count == 0;
        }

        private static bool IsProcessRunning(int processId)
        {
            try
            {
                using Process process = Process.GetProcessById(processId);
                return !process.HasExited;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryRunTaskKill(int processId)
        {
            try
            {
                string windowsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
                string taskKillPath = Path.Combine(windowsDirectory, "System32", "taskkill.exe");
                if (!File.Exists(taskKillPath))
                    return false;

                var startInfo = new ProcessStartInfo
                {
                    FileName = taskKillPath,
                    Arguments = $"/PID {processId} /T /F",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var taskKill = Process.Start(startInfo);
                if (taskKill == null)
                    return false;

                if (!taskKill.WaitForExit(ProcessTreeTerminationTimeoutMs))
                {
                    TryKillRootProcess(taskKill);
                    return false;
                }

                return taskKill.ExitCode == 0;
            }
            catch
            {
                return false;
            }
        }

        private static void TryKillRootProcess(Process process)
        {
            try
            {
                if (!process.HasExited)
                    process.Kill();
            }
            catch
            {
                // The caller reports that tree termination could not be confirmed.
            }
        }

        private static bool TryWaitForExit(Process process, int timeoutMs)
        {
            try
            {
                return process.HasExited || process.WaitForExit(timeoutMs);
            }
            catch
            {
                return false;
            }
        }

        private static Thread StartBoundedReader(
            StreamReader reader,
            BoundedTextBuffer destination,
            ManualResetEventSlim completed,
            string threadName)
        {
            var thread = new Thread(() =>
            {
                var chunk = new char[4096];
                try
                {
                    while (true)
                    {
                        int charactersRead = reader.Read(chunk, 0, chunk.Length);
                        if (charactersRead <= 0)
                            break;

                        destination.Append(new string(chunk, 0, charactersRead));
                    }
                }
                catch (IOException)
                {
                    // Expected when a timed-out process has its pipe closed.
                }
                catch (ObjectDisposedException)
                {
                    // Expected when a timed-out process has its pipe closed.
                }
                finally
                {
                    completed.Set();
                }
            })
            {
                IsBackground = true,
                Name = threadName
            };
            thread.Start();
            return thread;
        }

        private static void DrainRedirectedOutput(
            Process process,
            BoundedTextBuffer stdOut,
            BoundedTextBuffer stdErr,
            Thread stdOutReader,
            Thread stdErrReader,
            ManualResetEventSlim stdOutCompleted,
            ManualResetEventSlim stdErrCompleted)
        {
            if (!stdOutCompleted.Wait(OutputDrainTimeoutMs))
            {
                // A successful root-process exit is not proof that every byte
                // reached the reader. Structural callers must fail closed when
                // the pipe could not be drained completely.
                stdOut.MarkTruncated();
                try
                {
                    process.StandardOutput.Close();
                }
                catch
                {
                    // The process may have closed the stream concurrently.
                }
                stdOutReader.Join(250);
            }

            if (!stdErrCompleted.Wait(OutputDrainTimeoutMs))
            {
                stdErr.MarkTruncated();
                try
                {
                    process.StandardError.Close();
                }
                catch
                {
                    // The process may have closed the stream concurrently.
                }
                stdErrReader.Join(250);
            }
        }

        private static void DisposeCompletedEvent(ManualResetEventSlim completedEvent)
        {
            // Do not dispose an event that an asynchronous stream callback may still signal.
            if (completedEvent.IsSet)
                completedEvent.Dispose();
        }

        private static CommandResult CreateResult(
            int exitCode,
            BoundedTextBuffer stdOut,
            BoundedTextBuffer stdErr,
            string resolvedExecutablePath,
            bool timedOut,
            bool cancelled,
            bool terminationConfirmed,
            string leadingError)
        {
            string capturedOutput = GetProcessOutputSnapshot(stdOut);
            string capturedError = GetProcessOutputSnapshot(stdErr);
            string error = string.IsNullOrWhiteSpace(leadingError)
                ? capturedError
                : string.IsNullOrWhiteSpace(capturedError)
                    ? leadingError
                    : leadingError + Environment.NewLine + capturedError;
            return new CommandResult
            {
                ExitCode = exitCode,
                StdOut = capturedOutput,
                StdErr = error,
                ResolvedExecutablePath = resolvedExecutablePath ?? string.Empty,
                TimedOut = timedOut,
                Cancelled = cancelled,
                TerminationConfirmed = terminationConfirmed,
                StdOutTruncated = stdOut.IsTruncated,
                StdErrTruncated = stdErr.IsTruncated
            };
        }

        private static string GetProcessOutputSnapshot(BoundedTextBuffer buffer)
        {
            string snapshot = buffer.GetSnapshot();
            if (snapshot.EndsWith("\r\n", StringComparison.Ordinal))
                return snapshot.Substring(0, snapshot.Length - 2);
            if (snapshot.EndsWith("\n", StringComparison.Ordinal) ||
                snapshot.EndsWith("\r", StringComparison.Ordinal))
            {
                return snapshot.Substring(0, snapshot.Length - 1);
            }

            return snapshot;
        }

        private static CommandResult Failure(string error, string resolvedExecutablePath = "")
        {
            return new CommandResult
            {
                ExitCode = -1,
                StdOut = string.Empty,
                StdErr = error ?? "Command failed.",
                ResolvedExecutablePath = resolvedExecutablePath ?? string.Empty,
                TerminationConfirmed = true
            };
        }

        private static bool TryCanonicalizeExecutable(string path, out string canonicalPath)
        {
            canonicalPath = string.Empty;
            try
            {
                string fullPath = Path.GetFullPath(path);
                if (!File.Exists(fullPath) || !IsExecutableFile(fullPath))
                    return false;

                canonicalPath = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                    ? TryCanonicalizeWindowsPath(fullPath)
                    : TryCanonicalizeUnixPath(fullPath);
                if (string.IsNullOrWhiteSpace(canonicalPath))
                    canonicalPath = fullPath;

                return Path.IsPathRooted(canonicalPath);
            }
            catch
            {
                return false;
            }
        }

        internal static bool TryCanonicalizeExistingPath(string path, out string canonicalPath)
        {
            canonicalPath = string.Empty;
            try
            {
                string fullPath = Path.GetFullPath(path);
                bool isFile = File.Exists(fullPath);
                bool isDirectory = Directory.Exists(fullPath);
                if (!isFile && !isDirectory)
                    return false;

                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    // The existing safe-handle helper opens regular files. For
                    // directories, normalized case-insensitive comparison is
                    // fail-closed for aliases without introducing a directory
                    // handle with broader sharing semantics.
                    canonicalPath = isFile ? TryCanonicalizeWindowsPath(fullPath) : fullPath;
                }
                else
                {
                    canonicalPath = TryCanonicalizeUnixPath(fullPath);
                }

                if (string.IsNullOrWhiteSpace(canonicalPath))
                    canonicalPath = fullPath;
                return Path.IsPathRooted(canonicalPath);
            }
            catch
            {
                canonicalPath = string.Empty;
                return false;
            }
        }

        private static bool IsExecutableFile(string path)
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                return File.Exists(path);

            return NativeAccess(path, 1) == 0;
        }

        private static string TryCanonicalizeUnixPath(string path)
        {
            IntPtr resolved = IntPtr.Zero;
            try
            {
                resolved = NativeRealPath(path, IntPtr.Zero);
                return resolved == IntPtr.Zero ? path : Marshal.PtrToStringAnsi(resolved);
            }
            catch
            {
                return path;
            }
            finally
            {
                if (resolved != IntPtr.Zero)
                    NativeFree(resolved);
            }
        }

        private static string TryCanonicalizeWindowsPath(string path)
        {
            try
            {
                using var stream = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                var buffer = new StringBuilder(1024);
                uint length = GetFinalPathNameByHandle(stream.SafeFileHandle, buffer, buffer.Capacity, 0);
                if (length == 0)
                    return path;

                if (length >= buffer.Capacity)
                {
                    buffer.Capacity = checked((int)length + 1);
                    length = GetFinalPathNameByHandle(stream.SafeFileHandle, buffer, buffer.Capacity, 0);
                    if (length == 0)
                        return path;
                }

                string resolvedPath = buffer.ToString();
                if (resolvedPath.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
                    return @"\\" + resolvedPath.Substring(8);
                if (resolvedPath.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase))
                    return resolvedPath.Substring(4);

                return resolvedPath;
            }
            catch
            {
                return path;
            }
        }

        private static IEnumerable<string> GetSearchPaths()
        {
            return BuildSearchPaths(
                Environment.GetEnvironmentVariable("PATH") ?? string.Empty,
                GetPlatformSearchPaths());
        }

        internal static IReadOnlyList<string> BuildSearchPaths(
            string inheritedPath,
            IEnumerable<string> platformSearchPaths)
        {
            var paths = new List<string>();
            var seen = new HashSet<string>(RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal);

            // Prefer known platform install locations over the inherited PATH.
            // Unity is frequently launched from a GUI with a surprising PATH,
            // and an attacker-controlled entry must not shadow the system Git.
            if (platformSearchPaths != null)
            {
                foreach (string trustedPath in platformSearchPaths)
                    AddSearchPath(trustedPath, paths, seen);
            }

            foreach (string entry in (inheritedPath ?? string.Empty).Split(Path.PathSeparator))
                AddSearchPath(entry, paths, seen);

            return paths;
        }

        private static void AddSearchPath(string path, List<string> paths, HashSet<string> seen)
        {
            if (string.IsNullOrWhiteSpace(path))
                return;

            try
            {
                string candidate = path.Trim().Trim('"');
                // Empty PATH segments and relative entries both mean "the current
                // directory" on common shells. Never inherit that implicit search.
                if (string.IsNullOrWhiteSpace(candidate) || !Path.IsPathRooted(candidate))
                    return;

                string fullPath = Path.GetFullPath(candidate);
                if (seen.Add(fullPath))
                    paths.Add(fullPath);
            }
            catch
            {
                // Ignore malformed PATH entries rather than passing them to child processes.
            }
        }

        private static IEnumerable<string> ExpandWithExtensions(string basePath)
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                yield return basePath;
                yield break;
            }

            if (Path.HasExtension(basePath))
            {
                yield return basePath;
                yield break;
            }

            var pathext = Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT;.COM";
            foreach (string extension in pathext.Split(';'))
            {
                string normalizedExtension = extension.Trim();
                if (string.IsNullOrWhiteSpace(normalizedExtension))
                    continue;

                if (!normalizedExtension.StartsWith(".", StringComparison.Ordinal))
                    normalizedExtension = "." + normalizedExtension;
                if (normalizedExtension.Length > 16 ||
                    normalizedExtension.IndexOfAny(new[] { '/', '\\', ':', '"' }) >= 0)
                {
                    continue;
                }
                yield return basePath + normalizedExtension;
            }
        }

        private static IEnumerable<string> GetPlatformSearchPaths()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                return new[]
                {
                    "/opt/homebrew/bin",
                    "/usr/local/bin",
                    "/usr/bin",
                    "/bin",
                    "/usr/sbin",
                    "/sbin"
                };
            }

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                return new[]
                {
                    "/usr/local/bin",
                    "/usr/bin",
                    "/bin",
                    "/snap/bin"
                };
            }

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
                string programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
                string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                string systemDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System);
                return new[]
                {
                    CombineIfRooted(programFiles, "Git", "cmd"),
                    CombineIfRooted(programFiles, "GitHub CLI"),
                    CombineIfRooted(programFilesX86, "Git", "cmd"),
                    CombineIfRooted(localAppData, "Microsoft", "WindowsApps"),
                    systemDirectory
                };
            }

            return Array.Empty<string>();
        }

        private static string CombineIfRooted(string root, params string[] segments)
        {
            if (string.IsNullOrWhiteSpace(root) || !Path.IsPathRooted(root))
                return string.Empty;

            string result = root;
            foreach (string segment in segments)
                result = Path.Combine(result, segment);
            return result;
        }

        private static string BuildSearchPath()
        {
            return string.Join(Path.PathSeparator.ToString(), GetSearchPaths());
        }

        private enum CommandEndReason
        {
            Exited,
            TimedOut,
            Cancelled
        }

        [DllImport("libc", EntryPoint = "kill", SetLastError = true)]
        private static extern int NativeKill(int processId, int signal);

        [DllImport("libc", EntryPoint = "access", SetLastError = true)]
        private static extern int NativeAccess(string path, int mode);

        [DllImport("libc", EntryPoint = "realpath", SetLastError = true)]
        private static extern IntPtr NativeRealPath(string path, IntPtr resolvedPath);

        [DllImport("libc", EntryPoint = "free")]
        private static extern void NativeFree(IntPtr pointer);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint GetFinalPathNameByHandle(
            SafeFileHandle fileHandle,
            [Out] StringBuilder filePath,
            int filePathLength,
            uint flags);
    }
}
