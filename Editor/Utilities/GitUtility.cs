using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using UnityEngine;

namespace Calander.SubmodulePackageManager.Editor
{
    internal sealed class SubmoduleInfo
    {
        public string Name;
        public string Path;
        public string Url;
        public string Branch;
        public string CommitHash;
        public bool HasPackageJson;
        public string PackageName;
        public bool IsUnderPackages;
    }

    [Serializable]
    internal sealed class PackageJsonMetadata
    {
        public string name;
    }

    internal static class GitUtility
    {
        private static readonly Regex PackageNameRegex = new Regex(@"^com\.[a-z0-9]+(\.[a-z0-9]+)+$", RegexOptions.Compiled);
        private static readonly Regex SubmoduleStatusRegex = new Regex(@"^[ +-]?([0-9a-f]{7,40})\s+([^\s]+)", RegexOptions.Multiline | RegexOptions.Compiled);

        internal static string ProjectRoot
        {
            get
            {
                var parent = Directory.GetParent(Application.dataPath);
                return parent != null ? parent.FullName : Environment.CurrentDirectory;
            }
        }

        internal static bool IsValidPackageName(string packageName)
        {
            return !string.IsNullOrWhiteSpace(packageName) && PackageNameRegex.IsMatch(packageName);
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

        internal static bool TryGetSubmodules(out List<SubmoduleInfo> submodules, out string error)
        {
            submodules = new List<SubmoduleInfo>();
            error = string.Empty;

            string root = ProjectRoot;
            string gitModulesPath = Path.Combine(root, ".gitmodules");
            if (!File.Exists(gitModulesPath))
            {
                return true;
            }

            if (!TryEnsureSubmodulesInitialized(out _, out error))
            {
                return false;
            }

            var listResult = RunGit("config --file .gitmodules --name-only --get-regexp path", root);
            if (!listResult.IsSuccess)
            {
                // Exit code 1 with no stderr typically means no matches found (empty .gitmodules or no path entries)
                // This is not an error - it just means there are no submodules configured
                if (listResult.ExitCode == 1 && string.IsNullOrWhiteSpace(listResult.StdErr))
                {
                    return true;
                }

                error = BuildError("Failed to read .gitmodules", listResult);
                return false;
            }

            var commitMap = GetSubmoduleCommitMap(root);
            string[] lines = listResult.StdOut.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (string rawLine in lines)
            {
                string name = ExtractSubmoduleName(rawLine.Trim());
                if (string.IsNullOrEmpty(name))
                {
                    continue;
                }

                string path = RunGit($"config --file .gitmodules --get submodule.{name}.path", root).StdOut.Trim();
                string url = RunGit($"config --file .gitmodules --get submodule.{name}.url", root).StdOut.Trim();
                string branch = RunGit($"config --file .gitmodules --get submodule.{name}.branch", root).StdOut.Trim();
                path = NormalizePath(path);

                var info = new SubmoduleInfo
                {
                    Name = name,
                    Path = path,
                    Url = url,
                    Branch = branch,
                    CommitHash = commitMap.TryGetValue(path, out string commit) ? commit : string.Empty,
                    IsUnderPackages = path.Replace("\\", "/").StartsWith("Packages/", StringComparison.OrdinalIgnoreCase)
                };

                string packageJsonPath = Path.Combine(root, path, "package.json");
                info.HasPackageJson = File.Exists(packageJsonPath);
                if (info.HasPackageJson && TryReadPackageName(packageJsonPath, out string packageName))
                {
                    info.PackageName = packageName;
                }
                else if (info.IsUnderPackages)
                {
                    info.PackageName = Path.GetFileName(path);
                }

                submodules.Add(info);
            }

            return true;
        }

        internal static bool TryEnsureSubmodulesInitialized(out bool initializedAny, out string error)
        {
            initializedAny = false;
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

            if (!HasUninitializedSubmodules(statusResult.StdOut))
            {
                return true;
            }

            var initResult = RunGit("submodule update --init --recursive", root);
            if (!initResult.IsSuccess)
            {
                error = BuildError("Failed to initialize missing submodules", initResult);
                return false;
            }

            initializedAny = true;
            return true;
        }

        internal static bool TryAddSubmodule(string url, string path, string branch, out string error)
        {
            error = string.Empty;
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
            if (Directory.Exists(moduleMeta))
            {
                Directory.Delete(moduleMeta, true);
            }

            string submodulePath = Path.Combine(root, normalizedPath);
            if (Directory.Exists(submodulePath))
            {
                Directory.Delete(submodulePath, true);
            }

            return true;
        }

        internal static bool TryUpdateSubmodule(string path, out string error)
        {
            error = string.Empty;
            string root = ProjectRoot;
            string normalizedPath = NormalizePath(path);

            var result = RunGit($"submodule update --remote --merge -- {Quote(normalizedPath)}", root);
            if (!result.IsSuccess)
            {
                error = BuildError("Failed to update submodule", result);
                return false;
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

            var result = RunGit($"submodule set-branch --branch {Quote(trimmedBranch)} -- {Quote(normalizedPath)}", root);
            if (!result.IsSuccess)
            {
                error = BuildError("Failed to change submodule branch", result);
                return false;
            }

            return true;
        }

        internal static bool TryListRemoteBranches(string url, out List<string> branches, out string error)
        {
            branches = new List<string>();
            error = string.Empty;

            if (string.IsNullOrWhiteSpace(url))
            {
                error = "URL is required.";
                return false;
            }

            var result = RunGit($"ls-remote --heads {Quote(url)}", ProjectRoot);
            if (!result.IsSuccess)
            {
                error = BuildError("Failed to list remote branches", result);
                return false;
            }

            branches = ParseRemoteBranches(result.StdOut);
            return true;
        }

        internal static CommandResult RunGit(string arguments, string workingDir)
        {
            return CliCommandRunner.Run("git", arguments, workingDir);
        }

        private static Dictionary<string, string> GetSubmoduleCommitMap(string root)
        {
            var commits = new Dictionary<string, string>();
            var statusResult = RunGit("submodule status --recursive", root);
            if (!statusResult.IsSuccess)
            {
                return commits;
            }

            return ParseSubmoduleCommitMap(statusResult.StdOut);
        }

        private static bool HasUninitializedSubmodules(string submoduleStatusOutput)
        {
            if (string.IsNullOrWhiteSpace(submoduleStatusOutput))
            {
                return false;
            }

            string[] lines = submoduleStatusOutput.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (string line in lines)
            {
                if (!string.IsNullOrEmpty(line) && line[0] == '-')
                {
                    return true;
                }
            }

            return false;
        }

        private static string ExtractSubmoduleName(string line)
        {
            const string prefix = "submodule.";
            const string suffix = ".path";
            if (!line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return string.Empty;
            }

            if (!line.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                return string.Empty;
            }

            return line.Substring(prefix.Length, line.Length - prefix.Length - suffix.Length);
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

        private static string Quote(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return "\"\"";
            }

            return value.Contains(" ") ? $"\"{value}\"" : value;
        }

        private static string BuildError(string message, CommandResult result)
        {
            string detail = string.IsNullOrWhiteSpace(result.StdErr) ? result.StdOut : result.StdErr;
            return string.IsNullOrWhiteSpace(detail) ? message : $"{message}: {detail}";
        }
    }
}
