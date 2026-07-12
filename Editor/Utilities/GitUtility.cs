using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using UnityEngine;

namespace Essentials.GitPackageManager.Editor
{
    [Serializable]
    internal sealed class PackageJsonMetadata
    {
        public string name;
    }

    internal static class GitUtility
    {
        private static readonly Regex PackageNameRegex = new Regex(@"^com\.[a-z0-9]+(\.[a-z0-9]+)+$", RegexOptions.Compiled);
        private static readonly Regex BranchNameRegex = new Regex(@"^[A-Za-z0-9][A-Za-z0-9._/-]*$", RegexOptions.Compiled);
        private static readonly Regex SubmoduleStatusRegex = new Regex(@"^[ +-]?([0-9a-f]{7,40})\s+([^\s]+)", RegexOptions.Multiline | RegexOptions.Compiled);

        private static string cachedProjectRoot;

        internal static string ProjectRoot
        {
            get
            {
                if (cachedProjectRoot == null)
                {
                    var parent = Directory.GetParent(Application.dataPath);
                    cachedProjectRoot = parent != null ? parent.FullName : Environment.CurrentDirectory;
                }

                return cachedProjectRoot;
            }
        }

        internal static bool IsValidPackageName(string packageName)
        {
            return !string.IsNullOrWhiteSpace(packageName) && PackageNameRegex.IsMatch(packageName);
        }

        internal static bool IsValidBranchName(string branch)
        {
            if (string.IsNullOrWhiteSpace(branch))
                return true;

            string value = branch.Trim();
            return BranchNameRegex.IsMatch(value) &&
                   !value.Contains("..") &&
                   !value.Contains("@{") &&
                   !value.Contains("//") &&
                   !value.EndsWith(".", StringComparison.Ordinal) &&
                   !value.EndsWith("/", StringComparison.Ordinal) &&
                   !value.EndsWith(".lock", StringComparison.OrdinalIgnoreCase);
        }

        internal static bool IsValidRepositoryUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
                return false;

            string value = url.Trim();
            return !value.StartsWith("-", StringComparison.Ordinal) &&
                   value.IndexOfAny(new[] { '\0', '\r', '\n', '"' }) < 0;
        }

        internal static bool IsPackagePath(string path)
        {
            string normalized = NormalizePath(path);
            if (!normalized.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase))
                return false;

            string packageName = normalized.Substring("Packages/".Length);
            return packageName.IndexOf('/') < 0 && IsValidPackageName(packageName);
        }

        internal static bool TryReadPackageName(string packageJsonPath, out string packageName)
        {
            packageName = string.Empty;
            if (!File.Exists(packageJsonPath))
            {
                return false;
            }

            try
            {
                string content = File.ReadAllText(packageJsonPath);
                return TryReadPackageNameFromJson(content, out packageName);
            }
            catch
            {
                return false;
            }
        }

        internal static bool TryReadPackageNameFromJson(string packageJsonContent, out string packageName)
        {
            packageName = string.Empty;
            if (string.IsNullOrWhiteSpace(packageJsonContent))
            {
                return false;
            }

            try
            {
                var metadata = JsonUtility.FromJson<PackageJsonMetadata>(packageJsonContent);
                if (metadata == null || string.IsNullOrWhiteSpace(metadata.name))
                {
                    return false;
                }

                packageName = metadata.name.Trim();
                return !string.IsNullOrEmpty(packageName);
            }
            catch
            {
                return false;
            }
        }

        internal static bool IsGitAvailable(out string version, out string error)
        {
            var result = RunGit("--version", ProjectRoot);
            if (result.IsSuccess)
            {
                version = result.StdOut.Trim();
                error = string.Empty;
                return true;
            }

            version = string.Empty;
            error = string.IsNullOrWhiteSpace(result.StdErr) ? result.StdOut : result.StdErr;
            return false;
        }

        // ── Submodule Operations ──

        internal static bool TryGetSubmodules(out List<GitPackageInfo> submodules, out string error)
        {
            submodules = new List<GitPackageInfo>();
            error = string.Empty;

            string root = ProjectRoot;
            string gitModulesPath = Path.Combine(root, ".gitmodules");
            if (!File.Exists(gitModulesPath))
            {
                return true;
            }

            var statusResult = RunGit("submodule status --recursive", root);
            if (!statusResult.IsSuccess)
            {
                error = BuildError("Failed to read submodule status", statusResult);
                return false;
            }

            string statusOutput = statusResult.StdOut;

            var configResult = RunGit("config --file .gitmodules --list", root);
            if (!configResult.IsSuccess)
            {
                if (configResult.ExitCode == 1 && string.IsNullOrWhiteSpace(configResult.StdErr))
                {
                    return true;
                }

                error = BuildError("Failed to read .gitmodules", configResult);
                return false;
            }

            var config = ParseConfigList(configResult.StdOut);
            var commitMap = ParseSubmoduleCommitMap(statusOutput);
            var names = ExtractSubmoduleNamesFromConfig(config);

            foreach (string name in names)
            {
                config.TryGetValue($"submodule.{name}.path", out string rawPath);
                config.TryGetValue($"submodule.{name}.url", out string url);
                config.TryGetValue($"submodule.{name}.branch", out string branch);
                string path = NormalizePath(rawPath ?? string.Empty);
                if (!IsPackagePath(path))
                    continue;

                var info = new GitPackageInfo
                {
                    Name = name,
                    Path = path,
                    Url = url ?? string.Empty,
                    Branch = branch ?? string.Empty,
                    CommitHash = commitMap.TryGetValue(path, out string commit) ? commit : string.Empty,
                    IsInitialized = IsSubmoduleInitialized(statusOutput, path)
                };

                string packageJsonPath = Path.Combine(root, path, "package.json");
                info.HasPackageJson = File.Exists(packageJsonPath);
                if (info.HasPackageJson && TryReadPackageName(packageJsonPath, out string packageName))
                {
                    info.PackageName = packageName;
                }
                else if (path.Replace("\\", "/").StartsWith("Packages/", StringComparison.OrdinalIgnoreCase))
                {
                    info.PackageName = Path.GetFileName(path);
                }

                submodules.Add(info);
            }

            return true;
        }

        internal static bool TryAddSubmodule(string url, string path, string branch, out string error)
        {
            error = string.Empty;
            if (!IsValidRepositoryUrl(url))
            {
                error = "Repository URL is invalid.";
                return false;
            }

            if (!IsPackagePath(path))
            {
                error = "Submodules managed by this tool must use a valid Packages/com.author.package path.";
                return false;
            }

            if (!IsValidBranchName(branch))
            {
                error = "Branch name is invalid.";
                return false;
            }

            string root = ProjectRoot;
            string args = string.IsNullOrWhiteSpace(branch)
                ? $"submodule add {Quote(url)} {Quote(path)}"
                : $"submodule add -b {Quote(branch)} {Quote(url)} {Quote(path)}";

            var result = RunGit(args, root);
            if (!result.IsSuccess)
            {
                error = BuildError("Failed to add submodule", result);
                return false;
            }

            return true;
        }

        internal static bool TryRemoveSubmodule(string path, out string error)
        {
            error = string.Empty;
            string root = ProjectRoot;
            string normalizedPath = NormalizePath(path);
            if (!IsPackagePath(normalizedPath))
            {
                error = "Refusing to remove a path outside Packages/com.author.package.";
                return false;
            }

            var deinitResult = RunGit($"submodule deinit -f -- {Quote(normalizedPath)}", root);
            if (!deinitResult.IsSuccess)
            {
                error = BuildError("Failed to deinit submodule", deinitResult);
                return false;
            }

            var rmResult = RunGit($"rm -f -- {Quote(normalizedPath)}", root);
            if (!rmResult.IsSuccess)
            {
                error = BuildError("Failed to remove submodule from git", rmResult);
                return false;
            }

            string moduleMeta = Path.Combine(root, ".git/modules", normalizedPath);
            string submodulePath = Path.Combine(root, normalizedPath);
            try
            {
                if (Directory.Exists(moduleMeta))
                    Directory.Delete(moduleMeta, true);
                if (Directory.Exists(submodulePath))
                    Directory.Delete(submodulePath, true);
            }
            catch (Exception ex)
            {
                // `git rm` already removed the tracked package. A locked metadata
                // directory should not turn that successful mutation into a lie.
                Debug.LogWarning($"[Git Package Manager] Submodule was removed, but local metadata cleanup was incomplete: {ex.Message}");
            }

            return true;
        }

        internal static bool TrySetSubmoduleBranch(string path, string branch, out string error)
        {
            error = string.Empty;
            if (string.IsNullOrWhiteSpace(branch))
            {
                error = "Branch name is required.";
                return false;
            }

            string root = ProjectRoot;
            string normalizedPath = NormalizePath(path);
            string trimmedBranch = branch.Trim();
            if (!IsPackagePath(normalizedPath) || !IsValidBranchName(trimmedBranch))
            {
                error = "Package path or branch name is invalid.";
                return false;
            }

            var result = RunGit($"submodule set-branch --branch {Quote(trimmedBranch)} -- {Quote(normalizedPath)}", root);
            if (!result.IsSuccess)
            {
                error = BuildError("Failed to change submodule branch", result);
                return false;
            }

            return true;
        }

        // ── Internals ──

        internal static CommandResult RunGit(string arguments, string workingDir, int timeoutMs = CliCommandRunner.DefaultTimeoutMs)
        {
            return CliCommandRunner.Run("git", arguments, workingDir, timeoutMs);
        }

        private static Dictionary<string, string> ParseConfigList(string output)
        {
            var config = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(output))
                return config;

            foreach (string line in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                int eqIndex = line.IndexOf('=');
                if (eqIndex <= 0)
                    continue;

                string key = line.Substring(0, eqIndex).Trim();
                string value = line.Substring(eqIndex + 1).Trim();
                config[key] = value;
            }

            return config;
        }

        private static List<string> ExtractSubmoduleNamesFromConfig(Dictionary<string, string> config)
        {
            var names = new List<string>();
            const string prefix = "submodule.";
            const string suffix = ".path";

            foreach (var key in config.Keys)
            {
                if (key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                    key.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                {
                    string name = key.Substring(prefix.Length, key.Length - prefix.Length - suffix.Length);
                    if (!string.IsNullOrEmpty(name))
                        names.Add(name);
                }
            }

            return names;
        }

        internal static Dictionary<string, string> ParseSubmoduleCommitMap(string submoduleStatusOutput)
        {
            var commits = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(submoduleStatusOutput))
            {
                return commits;
            }

            foreach (Match match in SubmoduleStatusRegex.Matches(submoduleStatusOutput))
            {
                string commit = match.Groups[1].Value;
                string path = NormalizePath(match.Groups[2].Value);
                if (!string.IsNullOrEmpty(path))
                {
                    commits[path] = commit;
                }
            }

            return commits;
        }

        internal static List<string> ParseRemoteBranches(string lsRemoteOutput)
        {
            var branches = new List<string>();
            if (string.IsNullOrWhiteSpace(lsRemoteOutput))
            {
                return branches;
            }

            string[] lines = lsRemoteOutput.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (string line in lines)
            {
                int refsIndex = line.IndexOf("refs/heads/", StringComparison.Ordinal);
                if (refsIndex < 0)
                {
                    continue;
                }

                string branch = line.Substring(refsIndex + "refs/heads/".Length).Trim();
                if (!string.IsNullOrEmpty(branch))
                {
                    branches.Add(branch);
                }
            }

            branches.Sort(StringComparer.OrdinalIgnoreCase);
            return branches;
        }

        internal static string NormalizePath(string path)
        {
            return (path ?? string.Empty).Replace("\\", "/").Trim();
        }

        private static bool IsSubmoduleInitialized(string statusOutput, string path)
        {
            if (string.IsNullOrWhiteSpace(statusOutput))
                return false;

            string normalizedPath = NormalizePath(path);
            string[] lines = statusOutput.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (string line in lines)
            {
                string trimmed = line.TrimStart(' ', '+', '-');
                int separator = trimmed.IndexOf(' ');
                if (separator < 0)
                    continue;

                string remainder = trimmed.Substring(separator + 1);
                int pathEnd = remainder.IndexOf(' ');
                string candidatePath = NormalizePath(pathEnd >= 0 ? remainder.Substring(0, pathEnd) : remainder);
                if (string.Equals(candidatePath, normalizedPath, StringComparison.OrdinalIgnoreCase))
                    return line.Length > 0 && line[0] != '-';
            }

            return false;
        }

        internal static string Quote(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return "\"\"";
            }

            string escaped = value.Replace("\"", "\\\"");
            return $"\"{escaped}\"";
        }

        private static string BuildError(string message, CommandResult result)
        {
            string detail = string.IsNullOrWhiteSpace(result.StdErr) ? result.StdOut : result.StdErr;
            return string.IsNullOrWhiteSpace(detail) ? message : $"{message}: {detail}";
        }
    }
}
