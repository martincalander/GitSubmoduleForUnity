using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace MartinCalander.GitPackageManager.Editor
{
    internal sealed class CommandSpec
    {
        public string FileName;
        public string Arguments;
        public string WorkingDirectory;
        public int TimeoutMs = CliCommandRunner.DefaultTimeoutMs;
    }

    internal sealed class CommandResult
    {
        public int ExitCode;
        public string StdOut;
        public string StdErr;

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
        private Thread workerThread;

        public AsyncCommandHandle(ICommandRunner runner, CommandSpec spec)
        {
            this.runner = runner;
            this.spec = spec;
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

        private void RunCommand()
        {
            try
            {
                StatusMessage = "Running command...";
                Progress = 0.1f;

                Result = runner.Run(spec);

                Progress = 1f;
                StatusMessage = "Complete";
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
                // Publish Result and the final status before readers observe completion.
                Volatile.Write(ref isComplete, 1);
            }
        }
    }

    internal static class CliCommandRunner
    {
        internal const int DefaultTimeoutMs = 30000;
        private static ICommandRunner s_currentRunner = new ProcessCommandRunner();

        internal static ICommandRunner CurrentRunner
        {
            get => s_currentRunner;
            set => s_currentRunner = value ?? new ProcessCommandRunner();
        }

        internal static AsyncCommandHandle RunAsync(string fileName, string arguments, string workingDir, int timeoutMs = DefaultTimeoutMs)
        {
            var handle = new AsyncCommandHandle(CurrentRunner, new CommandSpec
            {
                FileName = fileName,
                Arguments = arguments,
                WorkingDirectory = workingDir,
                TimeoutMs = timeoutMs
            });
            handle.Start();
            return handle;
        }

        internal static CommandResult Run(string fileName, string arguments, string workingDir, int timeoutMs = DefaultTimeoutMs)
        {
            return CurrentRunner.Run(new CommandSpec
            {
                FileName = fileName,
                Arguments = arguments,
                WorkingDirectory = workingDir,
                TimeoutMs = timeoutMs
            });
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

    internal sealed class ProcessCommandRunner : ICommandRunner
    {
        public CommandResult Run(CommandSpec spec)
        {
            if (spec == null)
            {
                return new CommandResult
                {
                    ExitCode = -1,
                    StdOut = string.Empty,
                    StdErr = "Command specification was null."
                };
            }

            if (!TryResolveCommand(spec.FileName, out var resolvedPath))
            {
                return new CommandResult
                {
                    ExitCode = -1,
                    StdOut = string.Empty,
                    StdErr = $"Command not found: {spec.FileName}"
                };
            }

            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = resolvedPath,
                    Arguments = spec.Arguments,
                    WorkingDirectory = spec.WorkingDirectory,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                startInfo.EnvironmentVariables["GIT_TERMINAL_PROMPT"] = "0";
                startInfo.EnvironmentVariables["GCM_INTERACTIVE"] = "Never";
                startInfo.EnvironmentVariables["PATH"] = BuildSearchPath();

                using var process = new Process
                {
                    StartInfo = startInfo
                };

                var stdOut = new StringBuilder();
                var stdErr = new StringBuilder();
                using var stdOutCompleted = new ManualResetEventSlim(false);
                using var stdErrCompleted = new ManualResetEventSlim(false);

                process.OutputDataReceived += (_, args) =>
                {
                    if (args.Data == null)
                    {
                        stdOutCompleted.Set();
                        return;
                    }

                    lock (stdOut)
                    {
                        if (stdOut.Length > 0)
                            stdOut.AppendLine();
                        stdOut.Append(args.Data);
                    }
                };

                process.ErrorDataReceived += (_, args) =>
                {
                    if (args.Data == null)
                    {
                        stdErrCompleted.Set();
                        return;
                    }

                    lock (stdErr)
                    {
                        if (stdErr.Length > 0)
                            stdErr.AppendLine();
                        stdErr.Append(args.Data);
                    }
                };

                if (!process.Start())
                {
                    return new CommandResult
                    {
                        ExitCode = -1,
                        StdOut = string.Empty,
                        StdErr = $"Failed to start process: {spec.FileName}"
                    };
                }

                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                if (!process.WaitForExit(Math.Max(1000, spec.TimeoutMs)))
                {
                    TryKillProcess(process);
                    stdOutCompleted.Wait(250);
                    stdErrCompleted.Wait(250);

                    return new CommandResult
                    {
                        ExitCode = -1,
                        StdOut = stdOut.ToString(),
                        StdErr = $"Command timed out after {spec.TimeoutMs}ms: {spec.FileName} {spec.Arguments}".Trim()
                    };
                }

                process.WaitForExit();
                stdOutCompleted.Wait(250);
                stdErrCompleted.Wait(250);

                return new CommandResult
                {
                    ExitCode = process.ExitCode,
                    StdOut = stdOut.ToString(),
                    StdErr = stdErr.ToString()
                };
            }
            catch (Exception ex)
            {
                return new CommandResult
                {
                    ExitCode = -1,
                    StdOut = string.Empty,
                    StdErr = ex.Message
                };
            }
        }

        internal static bool IsCommandAvailable(string fileName)
        {
            return TryResolveCommand(fileName, out _);
        }

        private static void TryKillProcess(Process process)
        {
            try
            {
                if (!process.HasExited)
                    process.Kill();
            }
            catch
            {
                // Best effort only.
            }
        }

        internal static bool TryResolveCommand(string fileName, out string resolvedPath)
        {
            resolvedPath = string.Empty;
            if (string.IsNullOrWhiteSpace(fileName))
                return false;

            if (fileName.Contains("/") || fileName.Contains("\\") || Path.IsPathRooted(fileName))
            {
                if (File.Exists(fileName))
                {
                    resolvedPath = fileName;
                    return true;
                }

                return false;
            }

            foreach (var directory in GetSearchPaths())
            {
                foreach (var candidate in ExpandWithExtensions(Path.Combine(directory, fileName)))
                {
                    if (File.Exists(candidate))
                    {
                        resolvedPath = candidate;
                        return true;
                    }
                }
            }

            return false;
        }

        private static IEnumerable<string> GetSearchPaths()
        {
            var paths = new List<string>();
            var envPath = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            foreach (var entry in envPath.Split(Path.PathSeparator))
            {
                if (!string.IsNullOrWhiteSpace(entry))
                    paths.Add(entry.Trim().Trim('"'));
            }

            foreach (var extra in GetPlatformSearchPaths())
            {
                if (!string.IsNullOrWhiteSpace(extra))
                    paths.Add(extra);
            }

            return paths;
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
            foreach (var ext in pathext.Split(';'))
            {
                if (string.IsNullOrWhiteSpace(ext))
                    continue;

                yield return basePath + ext.ToLowerInvariant();
                yield return basePath + ext.ToUpperInvariant();
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
                return new[]
                {
                    @"C:\Program Files\Git\cmd",
                    @"C:\Program Files\GitHub CLI",
                    @"C:\Program Files (x86)\Git\cmd"
                };
            }

            return Array.Empty<string>();
        }

        private static string BuildSearchPath()
        {
            var merged = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in GetSearchPaths())
                merged.Add(path);

            return string.Join(Path.PathSeparator.ToString(), merged);
        }
    }
}
