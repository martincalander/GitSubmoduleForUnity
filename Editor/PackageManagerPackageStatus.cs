using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.PackageManager;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;
using UnityEngine;

namespace MartinCalander.GitSubmoduleManager.Editor
{
    internal sealed class PackageSubmoduleStatus
    {
        internal string Name;
        internal string FullPath;
        internal string RepositoryUrl;
        internal bool IsGitHub;
    }

    [InitializeOnLoad]
#if UNITY_6000_5_OR_NEWER
    [Unity.Scripting.LifecycleManagement.NoAutoStaticsCleanup]
#endif
    internal static class PackageManagerPackageStatus
    {
        private const int MaximumOutput = 2 * 1024 * 1024;
        private const int CommandTimeoutMilliseconds = 5000;
        private static readonly string ProjectRoot = Path.GetDirectoryName(Application.dataPath);
        private static readonly StringComparison PathComparison =
            Path.DirectorySeparatorChar == '\\' ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        private static readonly Regex PackageName = new Regex(@"\A[a-z0-9][a-z0-9_-]*(\.[a-z0-9][a-z0-9_-]*)+\z");
        private static readonly StatusState State = new StatusState();
        private static StatusSnapshot snapshot => State.Snapshot;
        private static Dictionary<string, InstalledPackage> workerPackages;
        private static Task<StatusSnapshot> worker;
        private static CancellationTokenSource cancellation;
        private static int generation;
        private static int workerGeneration;
        private static int observers;
        private static bool listening;
        private static bool refreshPending;
        private static bool shuttingDown;
        private static double nextFileCheck;
        private static string observedFiles;

        internal sealed class StatusSnapshot
        {
            internal Dictionary<string, InstalledPackage> Packages = new Dictionary<string, InstalledPackage>(StringComparer.Ordinal);
            internal readonly Dictionary<string, string> Repositories = new Dictionary<string, string>(StringComparer.Ordinal);
            internal readonly HashSet<string> Gitlinks = new HashSet<string>(StringComparer.Ordinal);
            internal string IndexPath;
            internal string FilesStamp;
            internal string Error = string.Empty;

            internal bool HasSameContents(StatusSnapshot other)
            {
                if (Error != other.Error || Packages.Count != other.Packages.Count ||
                    Repositories.Count != other.Repositories.Count || !Gitlinks.SetEquals(other.Gitlinks))
                    return false;
                foreach (KeyValuePair<string, InstalledPackage> package in Packages)
                    if (!other.Packages.TryGetValue(package.Key, out InstalledPackage value) || !package.Value.Equals(value))
                        return false;
                foreach (KeyValuePair<string, string> repository in Repositories)
                    if (!other.Repositories.TryGetValue(repository.Key, out string url) || repository.Value != url)
                        return false;
                return true;
            }
        }

        internal readonly struct InstalledPackage : IEquatable<InstalledPackage>
        {
            internal readonly string ResolvedPath;
            internal readonly PackageSource Source;
            internal readonly string PackageId;

            internal InstalledPackage(string resolvedPath, PackageSource source, string packageId)
            {
                ResolvedPath = resolvedPath;
                Source = source;
                PackageId = packageId;
            }

            public bool Equals(InstalledPackage other) => ResolvedPath == other.ResolvedPath &&
                Source == other.Source && PackageId == other.PackageId;
        }

        internal sealed class StatusState
        {
            internal StatusSnapshot Snapshot { get; private set; } = new StatusSnapshot();
            internal bool IsReady { get; private set; }
            internal event Action Changed;
            internal event Action ReadinessChanged;

            internal void BeginRefresh()
            {
                bool wasReady = IsReady;
                IsReady = false;
                if (wasReady)
                    ReadinessChanged?.Invoke();
            }

            internal void CompleteRefresh(StatusSnapshot result)
            {
                bool changed = !Snapshot.HasSameContents(result);
                bool wasReady = IsReady;
                Snapshot = result;
                IsReady = true;
                if (changed)
                    Changed?.Invoke();
                if (!wasReady)
                    ReadinessChanged?.Invoke();
            }

            internal bool TryCompleteRefresh(string root, StatusSnapshot result)
            {
                if ((result.FilesStamp == null && string.IsNullOrEmpty(result.Error)) ||
                    (result.FilesStamp != null && result.FilesStamp != FileStamp(root, result.IndexPath)))
                {
                    BeginRefresh();
                    return false;
                }
                CompleteRefresh(result);
                return true;
            }

            internal bool TryCompleteRefresh(string root, StatusSnapshot result, int observerCount, Action retry)
            {
                bool complete = TryCompleteRefresh(root, result);
                if (!complete && observerCount > 0)
                    retry();
                return complete;
            }
        }

        internal static event Action Changed
        {
            add => State.Changed += value;
            remove => State.Changed -= value;
        }
        internal static event Action ReadinessChanged
        {
            add => State.ReadinessChanged += value;
            remove => State.ReadinessChanged -= value;
        }
        internal static bool IsReady => State.IsReady;
        internal static string Error => snapshot.Error;

        static PackageManagerPackageStatus()
        {
            AssemblyReloadEvents.beforeAssemblyReload += Shutdown;
            EditorApplication.quitting += Shutdown;
        }

        internal static void Resume()
        {
            if (!shuttingDown)
                return;
            shuttingDown = false;
            listening = false;
            generation++;
        }

        internal static void Retain()
        {
            if (shuttingDown)
                return;
            observers++;
            if (observers == 1)
            {
                Events.registeredPackages += OnRegisteredPackages;
                Refresh();
            }
        }

        internal static void Release()
        {
            if (observers == 0)
                return;
            observers--;
            if (observers == 0)
            {
                Events.registeredPackages -= OnRegisteredPackages;
                refreshPending = false;
                StopListeningIfIdle();
            }
        }

        private static void OnRegisteredPackages(PackageRegistrationEventArgs args) => Refresh();

        internal static void Refresh()
        {
            if (shuttingDown)
                return;
            generation++;
            refreshPending = true;
            if (!listening)
            {
                listening = true;
                EditorApplication.update += Update;
            }
            State.BeginRefresh();
        }

        private static void Update()
        {
            if (worker != null && worker.IsCompleted)
            {
                StatusSnapshot result = worker.IsFaulted || worker.IsCanceled
                    ? new StatusSnapshot { Error = "Could not read local Git status." }
                    : worker.Result;
                worker = null;
                cancellation.Dispose();
                cancellation = null;
                if (workerGeneration == generation)
                {
                    result.Packages = workerPackages;
                    if (State.TryCompleteRefresh(ProjectRoot, result, observers, Refresh))
                        observedFiles = result.FilesStamp ?? FileStamp();
                }
                workerPackages = null;
            }

            if (refreshPending && worker == null)
            {
                refreshPending = false;
                try
                {
                    workerPackages = new Dictionary<string, InstalledPackage>(StringComparer.Ordinal);
                    foreach (PackageInfo package in PackageInfo.GetAllRegisteredPackages())
                        if (package != null && !string.IsNullOrEmpty(package.name))
                            workerPackages[package.name] = new InstalledPackage(package.resolvedPath, package.source, package.packageId);
                    workerGeneration = generation;
                    cancellation = new CancellationTokenSource();
                    CancellationToken token = cancellation.Token;
                    worker = Task.Run(() => ReadStatus(ProjectRoot, token));
                }
                catch (Exception)
                {
                    workerPackages = null;
                    State.CompleteRefresh(new StatusSnapshot { Error = "Unity's registered packages are unavailable." });
                }
            }

            if (observers > 0 && worker == null && EditorApplication.timeSinceStartup >= nextFileCheck)
            {
                nextFileCheck = EditorApplication.timeSinceStartup + 2;
                string files = FileStamp();
                if (observedFiles != null && files != observedFiles)
                    Refresh();
                observedFiles = files;
            }
            StopListeningIfIdle();
        }

        private static void StopListeningIfIdle()
        {
            if (listening && observers == 0 && worker == null && !refreshPending)
            {
                EditorApplication.update -= Update;
                listening = false;
            }
        }

        private static string FileStamp()
        {
            return FileStamp(ProjectRoot, snapshot.IndexPath);
        }

        private static string FileStamp(string root, string indexPath)
        {
            return Stamp(Path.Combine(root, ".gitmodules")) + ":" + Stamp(indexPath);
        }

        private static string Stamp(string path)
        {
            if (string.IsNullOrEmpty(path))
                return string.Empty;
            try
            {
                var file = new FileInfo(path);
                return file.Exists ? file.LastWriteTimeUtc.Ticks + "/" + file.Length : "missing";
            }
            catch (Exception) { return "unavailable"; }
        }

        private static void Shutdown()
        {
            shuttingDown = true;
            cancellation?.Cancel();
            Events.registeredPackages -= OnRegisteredPackages;
            EditorApplication.update -= Update;
            try { worker?.Wait(1500); }
            catch (Exception) { }
        }

        internal static bool TryGetSubmodule(string name, string resolvedPath, out PackageSubmoduleStatus status)
        {
            status = null;
            return string.IsNullOrEmpty(Error) &&
                   snapshot.Packages.TryGetValue(name ?? string.Empty, out InstalledPackage registered) &&
                   SamePath(registered.ResolvedPath, resolvedPath) &&
                   TryClassify(ProjectRoot, name, resolvedPath, snapshot, out status);
        }

        internal static bool TryClassify(string projectRoot, string name, string resolvedPath,
            StatusSnapshot data, out PackageSubmoduleStatus status)
        {
            status = null;
            if (!string.IsNullOrEmpty(data.Error) || !IsDirectPackage(projectRoot, name, resolvedPath) ||
                !data.Gitlinks.Contains("Packages/" + name) ||
                !data.Repositories.TryGetValue("Packages/" + name, out string url))
                return false;
            status = new PackageSubmoduleStatus
            {
                Name = name,
                FullPath = Path.GetFullPath(resolvedPath),
                RepositoryUrl = url,
                IsGitHub = IsGitHubUrl(url)
            };
            return true;
        }

        internal static bool ShouldBlockEmbeddedRemoval(string name, string resolvedPath)
        {
            return ShouldBlockEmbeddedRemoval(ProjectRoot, name, resolvedPath, IsReady, snapshot);
        }

        internal static bool ShouldBlockEmbeddedRemoval(string projectRoot, string name, string resolvedPath,
            bool ready, StatusSnapshot data)
        {
            if (!IsDirectPackage(projectRoot, name, resolvedPath))
                return false;
            return !ready || !string.IsNullOrEmpty(data.Error) ||
                   data.Gitlinks.Contains("Packages/" + name) ||
                   data.Repositories.ContainsKey("Packages/" + name) || HasLocalGit(resolvedPath);
        }

        private static bool HasLocalGit(string path)
        {
            try { return File.Exists(Path.Combine(path, ".git")) || Directory.Exists(Path.Combine(path, ".git")); }
            catch (Exception) { return true; }
        }

        internal static bool IsGitHubPackage(object nativePackage)
        {
            object version = Property(Property(nativePackage, "versions"), "primary");
            string name = Property(version, "name") as string;
            if (!(Property(version, "isInstalled") is bool installed) || !installed ||
                string.IsNullOrEmpty(name) || !snapshot.Packages.TryGetValue(name, out InstalledPackage info))
                return false;
            if (TryGetSubmodule(name, info.ResolvedPath, out PackageSubmoduleStatus status))
                return status.IsGitHub;
            return info.Source == PackageSource.Git && IsGitHubUrl(PackageUrl(name, info.PackageId));
        }

        internal static string Describe(PackageInfo info)
        {
            if (info == null)
                return "Select an installed package to see its source.";
            if (!snapshot.Packages.TryGetValue(info.name ?? string.Empty, out InstalledPackage installed) ||
                !SamePath(installed.ResolvedPath, info.resolvedPath))
                return "Not installed in this project.";
            if (TryGetSubmodule(info.name, info.resolvedPath, out PackageSubmoduleStatus status))
                return "Git submodule (editable checkout)";
            if (IsDirectPackage(ProjectRoot, info.name, info.resolvedPath))
            {
                if (!IsReady)
                    return "Checking submodule status...";
                if (!string.IsNullOrEmpty(Error))
                    return "Submodule status unavailable. " + Error;
                if (ShouldBlockEmbeddedRemoval(info.name, info.resolvedPath))
                    return "Git checkout. Its submodule registration could not be verified.";
            }
            return info.source == PackageSource.Git
                ? "Git package (not a submodule)"
                : info.source + " package (not a submodule)";
        }

        private static string PackageUrl(string name, string packageId)
        {
            string prefix = name + "@";
            return packageId != null && packageId.StartsWith(prefix, StringComparison.Ordinal)
                ? packageId.Substring(prefix.Length) : string.Empty;
        }

        private static object Property(object value, string name)
        {
            try { return value?.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(value); }
            catch (Exception) { return null; }
        }

        internal static bool IsGitHubUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
                return false;
            if (url.StartsWith("git@github.com:", StringComparison.OrdinalIgnoreCase))
                url = "ssh://git@github.com/" + url.Substring("git@github.com:".Length);
            if (url.StartsWith("git+", StringComparison.OrdinalIgnoreCase))
                url = url.Substring(4);
            return Uri.TryCreate(url, UriKind.Absolute, out Uri uri) &&
                   (uri.Scheme == "https" || uri.Scheme == "ssh" || uri.Scheme == "git" || uri.Scheme == "http") &&
                   string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase) &&
                   uri.AbsolutePath.Trim('/').Split('/').Length >= 2;
        }

        internal static bool IsDirectPackage(string projectRoot, string name, string resolvedPath)
        {
            return !string.IsNullOrEmpty(name) && PackageName.IsMatch(name) &&
                   SamePath(Path.Combine(projectRoot, "Packages", name), resolvedPath);
        }

        private static bool SamePath(string left, string right)
        {
            if (string.IsNullOrEmpty(left) || string.IsNullOrEmpty(right))
                return false;
            try
            {
                return string.Equals(Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), PathComparison);
            }
            catch (Exception) { return false; }
        }

        internal static StatusSnapshot ReadStatus(string root, CancellationToken token)
        {
            var result = new StatusSnapshot();
            try
            {
                string prefix = Git(root, "rev-parse --show-prefix", token).TrimEnd('\r', '\n');
                if (prefix.Length != 0)
                    throw new InvalidDataException("The Unity project is not the Git repository root.");
                string index = Git(root, "rev-parse --git-path index", token).TrimEnd('\r', '\n');
                result.IndexPath = Path.GetFullPath(Path.IsPathRooted(index) ? index : Path.Combine(root, index));
                result.FilesStamp = FileStamp(root, result.IndexPath);
                if (result.FilesStamp.IndexOf("unavailable", StringComparison.Ordinal) >= 0)
                    throw new InvalidDataException("Could not verify local Git metadata.");
                ParseIndex(Git(root, "ls-files --stage -z -- Packages/", token), result);
                string modules = Path.Combine(root, ".gitmodules");
                if (File.Exists(modules))
                {
                    var file = new FileInfo(modules);
                    if ((file.Attributes & FileAttributes.ReparsePoint) != 0 || file.Length > 128 * 1024)
                        throw new InvalidDataException("The .gitmodules file is linked or too large.");
                    ParseModules(Git(root, "config --no-includes -z --file .gitmodules --get-regexp ^submodule\\..*\\.(path|url)$", token, true), result);
                }
            }
            catch (OperationCanceledException) { result.Error = "Local Git status was cancelled."; }
            catch (Exception ex) { result.Error = ex is InvalidDataException ? ex.Message : "Could not read local Git status. Check that Git is installed and this project is a Git repository."; }
            return result;
        }

        internal static void ParseIndex(string text, StatusSnapshot result)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (string record in Records(text))
            {
                int tab = record.IndexOf('\t');
                if (tab < 0)
                    throw new InvalidDataException("Git returned malformed index data.");
                string[] header = record.Substring(0, tab).Split(' ');
                string path = record.Substring(tab + 1);
                if (header.Length != 3 || !Regex.IsMatch(header[0], "^[0-7]{6}$") ||
                    !Regex.IsMatch(header[1], "^([0-9a-f]{40}|[0-9a-f]{64})$") ||
                    header[2] != "0" || string.IsNullOrEmpty(path) || !seen.Add(path))
                    throw new InvalidDataException("The Git index contains malformed or conflicted package entries.");
                if (header[0] == "160000")
                    result.Gitlinks.Add(path);
            }
        }

        internal static void ParseModules(string text, StatusSnapshot result)
        {
            var sections = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
            foreach (string record in Records(text))
            {
                int newline = record.IndexOf('\n');
                int dot = newline < 0 ? -1 : record.LastIndexOf('.', newline);
                if (dot < "submodule.".Length || !record.StartsWith("submodule.", StringComparison.Ordinal))
                    throw new InvalidDataException("Git returned malformed submodule registrations.");
                string section = record.Substring(0, dot);
                string key = record.Substring(dot + 1, newline - dot - 1);
                string value = record.Substring(newline + 1);
                if ((key != "path" && key != "url") || string.IsNullOrWhiteSpace(value) || value.IndexOf('\n') >= 0 || value.IndexOf('\r') >= 0)
                    throw new InvalidDataException("A submodule registration is malformed.");
                if (!sections.TryGetValue(section, out Dictionary<string, string> values))
                    sections.Add(section, values = new Dictionary<string, string>(StringComparer.Ordinal));
                if (values.ContainsKey(key))
                    throw new InvalidDataException("A submodule registration contains duplicate values.");
                values.Add(key, value);
            }
            foreach (Dictionary<string, string> values in sections.Values)
            {
                if (!values.TryGetValue("path", out string path) || !values.TryGetValue("url", out string url) || result.Repositories.ContainsKey(path))
                    throw new InvalidDataException("Submodule registrations are incomplete or duplicated.");
                result.Repositories.Add(path, url);
            }
        }

        private static IEnumerable<string> Records(string text)
        {
            if (string.IsNullOrEmpty(text))
                yield break;
            if (text[text.Length - 1] != '\0')
                throw new InvalidDataException("Git returned incomplete status data.");
            string[] records = text.Split('\0');
            for (int i = 0; i < records.Length - 1; i++)
            {
                if (records[i].Length == 0)
                    throw new InvalidDataException("Git returned an empty status record.");
                yield return records[i];
            }
        }

        private static string Git(string root, string arguments, CancellationToken token, bool allowNoMatch = false)
        {
            using (var process = new Process())
            {
                process.StartInfo = new ProcessStartInfo("git", "--no-optional-locks " + arguments)
                {
                    WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardOutput = true, RedirectStandardError = true,
                    StandardOutputEncoding = new UTF8Encoding(false, true),
                    StandardErrorEncoding = new UTF8Encoding(false, true)
                };
                var inheritedGitVariables = new List<string>();
                foreach (string key in process.StartInfo.EnvironmentVariables.Keys)
                    if (key.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase))
                        inheritedGitVariables.Add(key);
                foreach (string key in inheritedGitVariables)
                    process.StartInfo.EnvironmentVariables.Remove(key);
                process.StartInfo.EnvironmentVariables["GIT_TERMINAL_PROMPT"] = "0";
                process.StartInfo.EnvironmentVariables["GIT_OPTIONAL_LOCKS"] = "0";
                process.Start();
                Task<string> output = Task.Run(() => ReadBounded(process.StandardOutput));
                Task<string> error = Task.Run(() => ReadBounded(process.StandardError));
                var timer = Stopwatch.StartNew();
                try
                {
                    while (!process.WaitForExit(50))
                    {
                        token.ThrowIfCancellationRequested();
                        if (timer.ElapsedMilliseconds >= CommandTimeoutMilliseconds || output.IsFaulted || error.IsFaulted)
                            throw new InvalidDataException("Reading local Git status timed out or exceeded its output limit.");
                    }
                    token.ThrowIfCancellationRequested();
                    if (!Task.WaitAll(new Task[] { output, error }, 1000))
                        throw new InvalidDataException("Git did not finish writing its status output.");
                    if (process.ExitCode != 0 && !(allowNoMatch && process.ExitCode == 1 && output.Result.Length == 0 && error.Result.Length == 0))
                        throw new InvalidDataException("Git could not read this project's status. Check the repository and its .gitmodules file.");
                    return output.Result;
                }
                finally
                {
                    if (!process.HasExited)
                    {
                        try { process.Kill(); process.WaitForExit(1000); }
                        catch (Exception) { }
                    }
                }
            }
        }

        private static string ReadBounded(StreamReader reader)
        {
            var value = new StringBuilder();
            var buffer = new char[4096];
            int count;
            while ((count = reader.Read(buffer, 0, buffer.Length)) != 0)
            {
                if (value.Length + count > MaximumOutput)
                    throw new InvalidDataException("Git status output exceeded its limit.");
                value.Append(buffer, 0, count);
            }
            return value.ToString();
        }
    }
}
