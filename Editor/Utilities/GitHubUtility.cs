using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using UnityEngine;

namespace Calander.SubmodulePackageManager.Editor
{
    internal sealed class GitHubRepo
    {
        public string Name;
        public string Owner;
        public string Url;
        public string DefaultBranch;
        public bool IsPrivate;
        public string Description;
        public bool IsInstalled;
        public bool HasPackageJson;
    }

    internal static class GitHubUtility
    {
        private static readonly Regex GitHubRepoRegex = new Regex(@"github\.com[:/](?<owner>[^/]+)/(?<repo>[^/]+?)(?:\.git)?/?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        internal static bool IsGhAvailable(out string version, out string error)
        {
            var result = CliCommandRunner.Run("gh", "--version", GitUtility.ProjectRoot);
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

        internal static bool IsAuthenticated(out string error)
        {
            var result = CliCommandRunner.Run("gh", "auth status -h github.com", GitUtility.ProjectRoot);
            if (result.IsSuccess)
            {
                error = string.Empty;
                return true;
            }

            error = string.IsNullOrWhiteSpace(result.StdErr) ? result.StdOut : result.StdErr;
            return false;
        }

        internal static bool TryListRepos(out List<GitHubRepo> repos, out string error)
        {
            repos = new List<GitHubRepo>();
            error = string.Empty;

            var result = CliCommandRunner.Run("gh", "repo list --limit 200 --json name,owner,url,defaultBranchRef,isPrivate,description", GitUtility.ProjectRoot);
            if (!result.IsSuccess)
            {
                error = BuildError("Failed to list GitHub repositories", result);
                return false;
            }

            string json = result.StdOut.Trim();
            if (string.IsNullOrEmpty(json))
            {
                return true;
            }

            var wrapper = JsonUtility.FromJson<RepoListWrapper>("{\"items\":" + json + "}");
            if (wrapper?.items == null)
            {
                return true;
            }

            foreach (var repoJson in wrapper.items)
            {
                repos.Add(new GitHubRepo
                {
                    Name = repoJson.name,
                    Owner = repoJson.owner != null ? repoJson.owner.login : string.Empty,
                    Url = repoJson.url,
                    DefaultBranch = repoJson.defaultBranchRef != null ? repoJson.defaultBranchRef.name : string.Empty,
                    IsPrivate = repoJson.isPrivate,
                    Description = repoJson.description
                });
            }

            return true;
        }

        internal static bool TryRepoHasPackageJson(string owner, string repo, out bool hasPackageJson, out string error)
        {
            hasPackageJson = false;
            error = string.Empty;

            var result = CliCommandRunner.Run("gh", $"api repos/{owner}/{repo}/contents/package.json", GitUtility.ProjectRoot);
            if (result.IsSuccess)
            {
                hasPackageJson = true;
                return true;
            }

            if (IsNotFound(result))
            {
                hasPackageJson = false;
                return true;
            }

            error = BuildError("Failed to query package.json from GitHub", result);
            return false;
        }

        internal static bool TryParseGitHubRepo(string url, out string owner, out string repo)
        {
            owner = string.Empty;
            repo = string.Empty;

            if (string.IsNullOrWhiteSpace(url))
            {
                return false;
            }

            var match = GitHubRepoRegex.Match(url.Trim());
            if (!match.Success)
            {
                return false;
            }

            owner = match.Groups["owner"].Value;
            repo = match.Groups["repo"].Value;
            return !string.IsNullOrEmpty(owner) && !string.IsNullOrEmpty(repo);
        }

        private static bool IsNotFound(CommandResult result)
        {
            string combined = $"{result.StdOut}\n{result.StdErr}".ToLowerInvariant();
            return combined.Contains("not found") || combined.Contains("404");
        }

        private static string BuildError(string message, CommandResult result)
        {
            string detail = string.IsNullOrWhiteSpace(result.StdErr) ? result.StdOut : result.StdErr;
            return string.IsNullOrWhiteSpace(detail) ? message : $"{message}: {detail}";
        }

        [Serializable]
        private sealed class RepoListWrapper
        {
            public RepoJson[] items;
        }

        [Serializable]
        private sealed class RepoJson
        {
            public string name;
            public OwnerJson owner;
            public string url;
            public DefaultBranchRefJson defaultBranchRef;
            public bool isPrivate;
            public string description;
        }

        [Serializable]
        private sealed class OwnerJson
        {
            public string login;
        }

        [Serializable]
        private sealed class DefaultBranchRefJson
        {
            public string name;
        }
    }
}
