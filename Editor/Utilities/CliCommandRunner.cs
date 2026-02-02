using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Calander.SubmodulePackageManager.Editor
{
    internal sealed class CommandResult
    {
        public int ExitCode;
        public string StdOut;
        public string StdErr;

        public bool IsSuccess => ExitCode == 0;
    }

    internal sealed class AsyncCommandHandle
    {
        public bool IsComplete { get; private set; }
        public CommandResult Result { get; private set; }
        public float Progress { get; private set; }
        public string StatusMessage { get; private set; }

        private Thread workerThread;
        private readonly string fileName;
        private readonly string arguments;
        private readonly string workingDir;

        public AsyncCommandHandle(string fileName, string arguments, string workingDir)
        {
            this.fileName = fileName;
            this.arguments = arguments;
            this.workingDir = workingDir;
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
            StatusMessage = "Connecting to GitHub...";
            Progress = 0.1f;

            Result = CliCommandRunner.Run(fileName, arguments, workingDir);

            Progress = 1f;
            StatusMessage = "Complete";
            IsComplete = true;
        }
    }

    internal static class CliCommandRunner
    {
        internal static AsyncCommandHandle RunAsync(string fileName, string arguments, string workingDir)
        {
            var handle = new AsyncCommandHandle(fileName, arguments, workingDir);
            handle.Start();
            return handle;
        }

        internal static CommandResult Run(string fileName, string arguments, string workingDir)
        {
            if (!TryResolveCommand(fileName, out string resolvedPath))
            {
                return new CommandResult
                {
                    ExitCode = -1,
                    StdOut = string.Empty,
                    StdErr = $"Command not found: {fileName}"
                };
            }

            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = resolvedPath,
                    Arguments = arguments,
                    WorkingDirectory = workingDir,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                startInfo.EnvironmentVariables["PATH"] = BuildSearchPath();

                using (var process = Process.Start(startInfo))
                {
                    if (process == null)
                    {
                        return new CommandResult
                        {
                            ExitCode = -1,
                            StdOut = string.Empty,
                            StdErr = $"Failed to start process: {fileName}"
                        };
                    }

                    string output = process.StandardOutput.ReadToEnd();
                    string error = process.StandardError.ReadToEnd();
                    process.WaitForExit();

                    return new CommandResult
                    {
                        ExitCode = process.ExitCode,
                        StdOut = output ?? string.Empty,
                        StdErr = error ?? string.Empty
                    };
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"Command failed: {resolvedPath} {arguments}\n{ex.Message}");
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

        private static bool TryResolveCommand(string fileName, out string resolvedPath)
        {
            resolvedPath = string.Empty;
            if (string.IsNullOrWhiteSpace(fileName))
            {
                return false;
            }

            if (fileName.Contains("/") || fileName.Contains("\\") || Path.IsPathRooted(fileName))
            {
                if (File.Exists(fileName))
                {
                    resolvedPath = fileName;
                    return true;
                }

                return false;
            }

            foreach (string directory in GetSearchPaths())
            {
                foreach (string candidate in ExpandWithExtensions(Path.Combine(directory, fileName)))
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
            string envPath = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            foreach (string entry in envPath.Split(Path.PathSeparator))
            {
                if (!string.IsNullOrWhiteSpace(entry))
                {
                    paths.Add(entry.Trim());
                }
            }

            foreach (string extra in GetPlatformSearchPaths())
            {
                if (!string.IsNullOrWhiteSpace(extra))
                {
                    paths.Add(extra);
                }
            }

            return paths;
        }

        private static IEnumerable<string> ExpandWithExtensions(string basePath)
        {
            if (Application.platform != RuntimePlatform.WindowsEditor)
            {
                yield return basePath;
                yield break;
            }

            if (Path.HasExtension(basePath))
            {
                yield return basePath;
                yield break;
            }

            string pathext = Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT;.COM";
            foreach (string ext in pathext.Split(';'))
            {
                if (string.IsNullOrWhiteSpace(ext))
                {
                    continue;
                }

                yield return basePath + ext.ToLowerInvariant();
                yield return basePath + ext.ToUpperInvariant();
            }
        }

        private static IEnumerable<string> GetPlatformSearchPaths()
        {
            switch (Application.platform)
            {
                case RuntimePlatform.OSXEditor:
                    return new[]
                    {
                        "/opt/homebrew/bin",
                        "/usr/local/bin",
                        "/usr/bin",
                        "/bin",
                        "/usr/sbin",
                        "/sbin"
                    };
                case RuntimePlatform.LinuxEditor:
                    return new[]
                    {
                        "/usr/local/bin",
                        "/usr/bin",
                        "/bin",
                        "/snap/bin"
                    };
                case RuntimePlatform.WindowsEditor:
                    return new[]
                    {
                        @"C:\Program Files\Git\cmd",
                        @"C:\Program Files\GitHub CLI",
                        @"C:\Program Files (x86)\Git\cmd"
                    };
                default:
                    return Array.Empty<string>();
            }
        }

        private static string BuildSearchPath()
        {
            var merged = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string path in GetSearchPaths())
            {
                merged.Add(path);
            }

            return string.Join(Path.PathSeparator.ToString(), merged);
        }
    }
}
