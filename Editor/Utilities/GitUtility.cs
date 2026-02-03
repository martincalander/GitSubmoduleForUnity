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

            string content = File.ReadAllText(packageJsonPath);
            var match = Regex.Match(content, "\"name\"\\s*:\\s*\"(?<name>[^\"]+)\"");
            if (!match.Success)
            {
                return false;
            }

            packageName = match.Groups["name"].Value.Trim();
            return !string.IsNullOrEmpty(packageName);
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

            var listResult = RunGit("config --file .gitmodules --name-only --get-regexp path", root);
            if (!listResult.IsSuccess)
            {
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

            string[] lines = result.StdOut.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (string line in lines)
            {
                // Format: <hash>\trefs/heads/<branch>
                int refsIndex = line.IndexOf("refs/heads/", StringComparison.Ordinal);
                if (refsIndex >= 0)
                {
                    string branch = line.Substring(refsIndex + "refs/heads/".Length).Trim();
                    if (!string.IsNullOrEmpty(branch))
                    {
                        branches.Add(branch);
                    }
                }
            }

            branches.Sort(StringComparer.OrdinalIgnoreCase);
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

            foreach (Match match in SubmoduleStatusRegex.Matches(statusResult.StdOut))
            {
                string commit = match.Groups[1].Value;
                string path = NormalizePath(match.Groups[2].Value);
                commits[path] = commit;
            }

            return commits;
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

        private static string NormalizePath(string path)
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
