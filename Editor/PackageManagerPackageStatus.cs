using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.PackageManager;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;
using UnityEngine;
using static MartinCalander.GitSubmoduleManager.Editor.PackageManagerSubmoduleHarmonyPatch;

namespace MartinCalander.GitSubmoduleManager.Editor
{
    internal readonly struct PackageSubmoduleStatus
    {
        internal readonly string RepositoryUrl;
        internal readonly bool IsGitHub;

        internal PackageSubmoduleStatus(string repositoryUrl, bool isGitHub)
        {
            RepositoryUrl = repositoryUrl;
            IsGitHub = isGitHub;
        }
    }

#if UNITY_6000_5_OR_NEWER
    // These observations describe installed Editor packages, not a Play mode session.
    [Unity.Scripting.LifecycleManagement.NoAutoStaticsCleanup]
#else
    [InitializeOnLoad]
#endif
    internal static partial class PackageManagerPackageStatus
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
        private static int workerPackagesRevision;
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
            internal int PackagesRevision = -1;
            internal string IndexPath;
            internal string GitDirectory;
            internal string IndexFingerprint;
            internal string ModulesFingerprint;
            internal bool HasSharedIndex;
            internal string FilesStamp;
            internal string Error = string.Empty;
            private Dictionary<string, RepositorySource> repositorySources;

            internal RepositorySource GetRepositorySource(string url)
            {
                if (url == null)
                    return new RepositorySource(null);
                repositorySources ??= new Dictionary<string, RepositorySource>(StringComparer.Ordinal);
                if (repositorySources.TryGetValue(url, out RepositorySource source))
                    return source;
                // Accepted snapshots are immutable; bound retention if a caller edits a fixture snapshot.
                if (repositorySources.Count >= Math.Max(1, Repositories.Count))
                    repositorySources.Clear();
                source = new RepositorySource(url);
                repositorySources.Add(url, source);
                return source;
            }

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

        internal readonly struct RepositorySource
        {
            internal readonly bool IsGitHub;
            internal readonly string RepositoryUrl;

            internal RepositorySource(string url)
            {
                IsGitHub = IsGitHubUrl(url);
                RepositoryUrl = PackageSourceDetails.GetRepositoryUrl(url);
            }
        }

        internal readonly struct InstalledPackage : IEquatable<InstalledPackage>
        {
            internal readonly string ResolvedPath;
            internal readonly PackageSource Source;
            internal readonly string PackageId;
            private readonly string normalizedPath;
            private readonly GitSource gitSource;

            private sealed class GitSource
            {
                internal readonly int NameLength;
                internal readonly bool IsGitHub;
                internal readonly string RepositoryUrl;

                internal GitSource(string packageId, int nameLength)
                {
                    NameLength = nameLength;
                    string url = packageId.Substring(nameLength + 1);
                    IsGitHub = IsGitHubUrl(url);
                    RepositoryUrl = PackageSourceDetails.GetRepositoryUrl(url);
                }
            }

            internal InstalledPackage(string resolvedPath, PackageSource source, string packageId)
            {
                ResolvedPath = resolvedPath;
                Source = source;
                PackageId = packageId;
                normalizedPath = NormalizePath(resolvedPath);
                if (string.Equals(normalizedPath, resolvedPath, StringComparison.Ordinal))
                    normalizedPath = resolvedPath;
                int separator = source == PackageSource.Git && packageId != null ? packageId.IndexOf('@') : -1;
                gitSource = separator > 0 ? new GitSource(packageId, separator) : null;
            }

            internal bool MatchesPath(string path) => normalizedPath != null && !string.IsNullOrEmpty(path) &&
                (string.Equals(normalizedPath, path, PathComparison) ||
                 string.Equals(normalizedPath, NormalizePath(path), PathComparison));

            internal bool IsGitHubSource(string name) => gitSource != null && HasPackagePrefix(name, PackageId) &&
                (name.Length == gitSource.NameLength ? gitSource.IsGitHub : IsGitHubUrl(PackageUrl(name, PackageId)));

            internal string GetRepositoryUrl(string name)
            {
                if (gitSource == null || !HasPackagePrefix(name, PackageId))
                    return string.Empty;
                return name.Length == gitSource.NameLength ? gitSource.RepositoryUrl :
                    PackageSourceDetails.GetRepositoryUrl(PackageUrl(name, PackageId));
            }

            public bool Equals(InstalledPackage other) => ResolvedPath == other.ResolvedPath &&
                Source == other.Source && PackageId == other.PackageId;
        }

        internal sealed class StatusState
        {
            internal StatusSnapshot Snapshot { get; private set; } = new StatusSnapshot();
            internal bool IsReady { get; private set; }
            internal int PackagesRevision { get; private set; }
            internal event Action Changed;
            internal event Action ReadinessChanged;

            internal Dictionary<string, InstalledPackage> ReadRegisteredPackages(
                Func<Dictionary<string, InstalledPackage>> read, out int revision)
            {
                revision = PackagesRevision;
                return Snapshot.PackagesRevision == revision && string.IsNullOrEmpty(Snapshot.Error)
                    ? Snapshot.Packages : read();
            }

            internal void BeginRefresh(bool packagesChanged = false)
            {
                if (packagesChanged)
                    PackagesRevision++;
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
        internal static bool IsReady => State.IsReady;
        internal static string Error => snapshot.Error;

#if UNITY_6000_5_OR_NEWER
        [Unity.Scripting.LifecycleManagement.OnCodeInitializing]
#else
        static PackageManagerPackageStatus() => Initialize();
#endif
        private static void Initialize()
        {
#if !UNITY_6000_5_OR_NEWER
            AssemblyReloadEvents.beforeAssemblyReload -= Shutdown;
            AssemblyReloadEvents.beforeAssemblyReload += Shutdown;
#endif
            EditorApplication.quitting -= Shutdown;
            EditorApplication.quitting += Shutdown;
        }

        internal static void Resume()
        {
            Initialize();
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
                generation++;
                cancellation?.Cancel();
                StopListeningIfIdle();
            }
        }

        private static void OnRegisteredPackages(PackageRegistrationEventArgs args) => Refresh();

        internal static void Refresh() => QueueRefresh(true);

        private static void RefreshGitMetadata() => QueueRefresh(false);

        private static void QueueRefresh(bool packagesChanged)
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
            State.BeginRefresh(packagesChanged);
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
                    result.PackagesRevision = workerPackagesRevision;
                    if (State.TryCompleteRefresh(ProjectRoot, result, observers, RefreshGitMetadata))
                        observedFiles = result.FilesStamp ?? FileStamp();
                }
                workerPackages = null;
            }

            if (refreshPending && worker == null)
            {
                refreshPending = false;
                try
                {
                    workerGeneration = generation;
                    workerPackages = State.ReadRegisteredPackages(ReadRegisteredPackages, out workerPackagesRevision);
                    cancellation = new CancellationTokenSource();
                    CancellationToken token = cancellation.Token;
                    StatusSnapshot previous = snapshot;
                    worker = Task.Run(() => ReadStatus(ProjectRoot, token, previous));
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
                    RefreshGitMetadata();
                observedFiles = files;
            }
            StopListeningIfIdle();
        }

        private static Dictionary<string, InstalledPackage> ReadRegisteredPackages()
        {
            var packages = new Dictionary<string, InstalledPackage>(StringComparer.Ordinal);
            foreach (PackageInfo package in PackageInfo.GetAllRegisteredPackages())
                if (package != null && !string.IsNullOrEmpty(package.name))
                    packages[package.name] = new InstalledPackage(package.resolvedPath, package.source, package.packageId);
            return packages;
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

#if UNITY_6000_5_OR_NEWER
        [Unity.Scripting.LifecycleManagement.OnCodeUnloading]
#endif
        private static void Shutdown()
        {
            shuttingDown = true;
            generation++;
            refreshPending = false;
            cancellation?.Cancel();
            Events.registeredPackages -= OnRegisteredPackages;
            EditorApplication.update -= Update;
            listening = false;
#if !UNITY_6000_5_OR_NEWER
            AssemblyReloadEvents.beforeAssemblyReload -= Shutdown;
#endif
            EditorApplication.quitting -= Shutdown;
            try { worker?.Wait(1500); }
            catch (Exception) { }
        }

        internal static bool TryGetSubmodule(string name, string resolvedPath, out PackageSubmoduleStatus status)
        {
            status = default;
            return string.IsNullOrEmpty(Error) &&
                   snapshot.Packages.TryGetValue(name ?? string.Empty, out InstalledPackage registered) &&
                   registered.MatchesPath(resolvedPath) &&
                   TryClassifyInstalled(ProjectRoot, name, registered, snapshot, out status);
        }

        internal static bool TryClassify(string projectRoot, string name, string resolvedPath,
            StatusSnapshot data, out PackageSubmoduleStatus status)
        {
            status = default;
            if (!TryGetSubmoduleRepository(name, data, out string url) ||
                !IsDirectPackage(projectRoot, name, resolvedPath))
                return false;
            status = new PackageSubmoduleStatus(url, data.GetRepositorySource(url).IsGitHub);
            return true;
        }

        private static bool TryClassifyInstalled(string projectRoot, string name, InstalledPackage installed,
            StatusSnapshot data, out PackageSubmoduleStatus status)
        {
            status = default;
            if (!TryGetInstalledSubmoduleRepository(projectRoot, name, installed, data, out string url))
                return false;
            status = new PackageSubmoduleStatus(url, data.GetRepositorySource(url).IsGitHub);
            return true;
        }

        private static bool TryGetInstalledSubmoduleRepository(string projectRoot, string name, InstalledPackage installed,
            StatusSnapshot data, out string url)
        {
            return TryGetSubmoduleRepository(name, data, out url) && PackageName.IsMatch(name) &&
                   installed.MatchesPath(Path.Combine(projectRoot, "Packages", name));
        }

        private static bool TryGetSubmoduleRepository(string name, StatusSnapshot data, out string url)
        {
            url = null;
            if (!string.IsNullOrEmpty(data.Error) || string.IsNullOrEmpty(name))
                return false;
            string path = "Packages/" + name;
            return data.Gitlinks.Contains(path) && data.Repositories.TryGetValue(path, out url);
        }

        internal static bool ShouldBlockEmbeddedRemoval(string name, string resolvedPath)
        {
            bool blocked = ShouldBlockEmbeddedRemoval(ProjectRoot, name, resolvedPath, IsReady, snapshot, out bool refreshRequired);
            if (refreshRequired)
                RefreshGitMetadata();
            return blocked;
        }

        internal static bool ShouldBlockEmbeddedRemoval(string projectRoot, string name, string resolvedPath,
            bool ready, StatusSnapshot data)
        {
            return ShouldBlockEmbeddedRemoval(projectRoot, name, resolvedPath, ready, data, out _);
        }

        private static bool ShouldBlockEmbeddedRemoval(string projectRoot, string name, string resolvedPath,
            bool ready, StatusSnapshot data, out bool refreshRequired)
        {
            refreshRequired = false;
            if (!TryGetDirectPackagePath(projectRoot, resolvedPath, out string relativePath))
                return false;
            if (!ready || !string.IsNullOrEmpty(data.Error))
                return true;
            refreshRequired = data.FilesStamp == null || data.FilesStamp != FileStamp(projectRoot, data.IndexPath);
            return refreshRequired || ContainsPath(projectRoot, data.Gitlinks, relativePath) ||
                   ContainsPath(projectRoot, data.Repositories.Keys, relativePath) || HasLocalGit(resolvedPath);
        }

        private static bool TryGetDirectPackagePath(string projectRoot, string resolvedPath, out string relativePath)
        {
            relativePath = null;
            if (string.IsNullOrEmpty(resolvedPath))
                return false;
            try
            {
                string fullPath = Path.GetFullPath(resolvedPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (!SamePath(Path.Combine(projectRoot, "Packages"), Path.GetDirectoryName(fullPath)))
                    return false;
                relativePath = "Packages/" + Path.GetFileName(fullPath);
                return true;
            }
            catch (Exception) { return false; }
        }

        private static bool ContainsPath(string projectRoot, IEnumerable<string> paths, string relativePath)
        {
            foreach (string path in paths)
                if (string.Equals(path, relativePath, PathComparison) ||
                    TryGetDirectPackagePath(projectRoot, Path.Combine(projectRoot, path), out string normalized) &&
                    string.Equals(normalized, relativePath, PathComparison))
                    return true;
            return false;
        }

        private static bool HasLocalGit(string path)
        {
            try { return File.Exists(Path.Combine(path, ".git")) || Directory.Exists(Path.Combine(path, ".git")); }
            catch (Exception) { return true; }
        }

        internal static bool IsGitHubPackage(object nativePackage) => IsGitHubPackage(ProjectRoot, nativePackage, snapshot);

        internal static bool IsGitHubPackage(string projectRoot, object nativePackage, StatusSnapshot data)
        {
            if (!TryGetNativeInstalledPackage(nativePackage, data, out string name, out InstalledPackage info))
                return false;
            if (TryClassifyInstalled(projectRoot, name, info, data, out PackageSubmoduleStatus status))
                return status.IsGitHub;
            return info.IsGitHubSource(name);
        }

        private static bool TryGetNativeInstalledPackage(object nativePackage, StatusSnapshot data,
            out string name, out InstalledPackage info)
        {
            object version = ReadProperty(ReadProperty(nativePackage, "versions"), "primary");
            name = ReadProperty(version, "name") as string;
            info = default;
            return ReadProperty(version, "isInstalled") is bool installed && installed &&
                   !string.IsNullOrEmpty(name) && data.Packages.TryGetValue(name, out info) &&
                   info.MatchesPath(ReadProperty(version, "localPath") as string) &&
                   (ReadProperty(version, "packageId") as string) == info.PackageId;
        }

        internal static string GetRepositoryUrl(object version) => GetRepositoryUrl(ProjectRoot, version, snapshot);

        internal static string GetRepositoryUrl(string projectRoot, object version, StatusSnapshot data)
        {
            string name = ReadProperty(version, "name") as string;
            if (ReadProperty(version, "isInstalled") is not true || string.IsNullOrEmpty(name) ||
                !data.Packages.TryGetValue(name, out InstalledPackage installed) ||
                !installed.MatchesPath(ReadProperty(version, "localPath") as string) ||
                (ReadProperty(version, "packageId") as string) != installed.PackageId)
                return string.Empty;
            return TryGetInstalledSubmoduleRepository(projectRoot, name, installed, data, out string url)
                ? data.GetRepositorySource(url).RepositoryUrl : installed.GetRepositoryUrl(name);
        }

        private static string PackageUrl(string name, string packageId)
        {
            return HasPackagePrefix(name, packageId) ? packageId.Substring(name.Length + 1) : string.Empty;
        }

        private static bool HasPackagePrefix(string name, string packageId) =>
            !string.IsNullOrEmpty(name) && packageId != null && packageId.Length > name.Length &&
            packageId[name.Length] == '@' && packageId.StartsWith(name, StringComparison.Ordinal);

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

        private static string NormalizePath(string path)
        {
            if (string.IsNullOrEmpty(path))
                return null;
            try
            {
                string fullPath = Path.GetFullPath(path);
                string normalized = fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string root = Path.GetPathRoot(fullPath);
                return normalized.Length < root.Length ? root : normalized;
            }
            catch (Exception) { return null; }
        }

        internal static StatusSnapshot ReadStatus(string root, CancellationToken token) => ReadStatus(root, token, null);

        internal static StatusSnapshot ReadStatus(string root, CancellationToken token, StatusSnapshot previous) =>
            ReadStatus(root, token, previous, null);

        internal static StatusSnapshot ReadStatus(string root, CancellationToken token, StatusSnapshot previous,
            Func<string, bool, string> executeGit)
        {
            var result = new StatusSnapshot();
            try
            {
                string ReadGit(string arguments, bool allowNoMatch = false)
                {
                    token.ThrowIfCancellationRequested();
                    return executeGit != null ? executeGit(arguments, allowNoMatch) : Git(root, arguments, token, allowNoMatch);
                }

                ParseRepositoryLocation(root, ReadGit("rev-parse --show-prefix --git-path index --absolute-git-dir --shared-index-path"), result);
                result.FilesStamp = FileStamp(root, result.IndexPath);
                if (result.FilesStamp.IndexOf("unavailable", StringComparison.Ordinal) >= 0)
                    throw new InvalidDataException("Could not verify local Git metadata.");
                string modules = Path.Combine(root, ".gitmodules");
                bool hasModules = ValidateModulesFile(modules);
                result.IndexFingerprint = result.HasSharedIndex ? null : Fingerprint(result.IndexPath, 4 * 1024 * 1024, token);
                result.ModulesFingerprint = Fingerprint(modules, 128 * 1024, token);
                bool canReuse = previous != null && string.IsNullOrEmpty(previous.Error) && previous.FilesStamp != null &&
                    string.Equals(previous.GitDirectory, result.GitDirectory, PathComparison) && SamePath(previous.IndexPath, result.IndexPath);
                if (canReuse && !previous.HasSharedIndex && result.IndexFingerprint != null && result.IndexFingerprint == previous.IndexFingerprint)
                    result.Gitlinks.UnionWith(previous.Gitlinks);
                else
                    ParseIndex(ReadGit("ls-files --stage -z -- Packages/"), result);
                if (hasModules)
                {
                    if (canReuse && result.ModulesFingerprint != null && result.ModulesFingerprint == previous.ModulesFingerprint)
                        foreach (KeyValuePair<string, string> repository in previous.Repositories)
                            result.Repositories.Add(repository.Key, repository.Value);
                    else
                        ParseModules(ReadGit("config --no-includes -z --file .gitmodules --get-regexp ^submodule\\..*\\.(path|url)$", true), result);
                }
                token.ThrowIfCancellationRequested();
                if (hasModules != ValidateModulesFile(modules) ||
                    result.IndexFingerprint != null && result.IndexFingerprint != Fingerprint(result.IndexPath, 4 * 1024 * 1024, token) ||
                    result.ModulesFingerprint != null && result.ModulesFingerprint != Fingerprint(modules, 128 * 1024, token))
                    result.FilesStamp = null;
                token.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException) { result.Error = "Local Git status was cancelled."; }
            catch (Exception ex) { result.Error = ex is InvalidDataException ? ex.Message : "Could not read local Git status. Check that Git is installed and this project is a Git repository."; }
            return result;
        }

        private static bool ValidateModulesFile(string path)
        {
            try
            {
                FileAttributes attributes = File.GetAttributes(path);
                if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0 || new FileInfo(path).Length > 128 * 1024)
                    throw new InvalidDataException("The .gitmodules file is linked or too large.");
                return true;
            }
            catch (FileNotFoundException) { return false; }
            catch (DirectoryNotFoundException) { return false; }
        }

        private static string Fingerprint(string path, int maximumBytes, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                if ((File.GetAttributes(path) & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
                    return null;
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var hash = SHA256.Create())
                {
                    if (stream.Length > maximumBytes)
                        return null;
                    var buffer = new byte[8192];
                    int total = 0;
                    int count;
                    while ((count = stream.Read(buffer, 0, buffer.Length)) != 0)
                    {
                        token.ThrowIfCancellationRequested();
                        total += count;
                        if (total > maximumBytes)
                            return null;
                        hash.TransformBlock(buffer, 0, count, buffer, 0);
                    }
                    hash.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                    return Convert.ToBase64String(hash.Hash);
                }
            }
            catch (FileNotFoundException) { return "missing"; }
            catch (DirectoryNotFoundException) { return "missing"; }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }

        private static void ParseRepositoryLocation(string root, string text, StatusSnapshot result)
        {
            string[] lines = text?.Split('\n');
            if (lines == null || (lines.Length != 4 && lines.Length != 5) || lines[lines.Length - 1].Length != 0)
                throw new InvalidDataException("Git returned malformed repository location data.");
            result.IndexPath = ParseRepositoryIndexPath(root, lines[0] + "\n" + lines[1] + "\n");
            string directory = lines[2].EndsWith("\r", StringComparison.Ordinal) ? lines[2].Substring(0, lines[2].Length - 1) : lines[2];
            if (!Path.IsPathRooted(directory) || directory.IndexOf('\0') >= 0 || directory.IndexOf('\r') >= 0)
                throw new InvalidDataException("Git returned malformed repository location data.");
            result.GitDirectory = Path.GetFullPath(directory);
            result.HasSharedIndex = lines.Length == 5;
            if (result.HasSharedIndex)
            {
                string shared = lines[3].EndsWith("\r", StringComparison.Ordinal) ? lines[3].Substring(0, lines[3].Length - 1) : lines[3];
                if (string.IsNullOrWhiteSpace(shared) || shared.IndexOf('\0') >= 0 || shared.IndexOf('\r') >= 0)
                    throw new InvalidDataException("Git returned malformed shared index data.");
            }
        }

        internal static string ParseRepositoryIndexPath(string root, string text)
        {
            string[] lines = text?.Split('\n');
            if (lines == null || lines.Length != 3 || lines[2].Length != 0)
                throw new InvalidDataException("Git returned malformed repository location data.");
            if (lines[0].Length != 0 && lines[0] != "\r")
                throw new InvalidDataException("The Unity project is not the Git repository root.");
            string index = lines[1].EndsWith("\r", StringComparison.Ordinal)
                ? lines[1].Substring(0, lines[1].Length - 1) : lines[1];
            if (string.IsNullOrWhiteSpace(index) || index.IndexOf('\0') >= 0 || index.IndexOf('\r') >= 0)
                throw new InvalidDataException("Git returned malformed repository location data.");
            return Path.GetFullPath(Path.IsPathRooted(index) ? index : Path.Combine(root, index));
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
            token.ThrowIfCancellationRequested();
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
                token.ThrowIfCancellationRequested();
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
