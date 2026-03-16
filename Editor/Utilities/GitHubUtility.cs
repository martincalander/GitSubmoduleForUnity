using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using UnityEngine;

namespace GitPackageManager.Editor
{
    internal static class GitHubUtility
    {
        private static readonly Regex GitHubRepoRegex = new Regex(@"github\.com[:/](?<owner>[^/]+)/(?<repo>[^/]+?)(?:\.git)?(?=/|$)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

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

        internal static string GetAuthenticatedUsername()
        {
            var result = CliCommandRunner.Run("gh", "api user --jq .login", GitUtility.ProjectRoot);
            return result.IsSuccess ? result.StdOut.Trim() : string.Empty;
        }

        internal static List<GitHubRepo> ParseRepoJson(string json)
        {
            var repos = new List<GitHubRepo>();
            if (string.IsNullOrEmpty(json))
            {
                return repos;
            }

            var wrapper = JsonUtility.FromJson<RepoListWrapper>("{\"items\":" + json + "}");
            if (wrapper?.items == null)
            {
                return repos;
            }

            foreach (var repoJson in wrapper.items)
            {
                repos.Add(new GitHubRepo
                {
                    Name = repoJson.name,
                    Owner = repoJson.owner != null ? repoJson.owner.login : string.Empty,
                    Url = string.IsNullOrWhiteSpace(repoJson.url) ? repoJson.html_url : repoJson.url,
                    DefaultBranch = repoJson.defaultBranchRef != null ? repoJson.defaultBranchRef.name : repoJson.default_branch,
                    IsPrivate = repoJson.isPrivate || repoJson.@private,
                    Description = repoJson.description
                });
            }

            return repos;
        }

        internal static List<GitHubRepo> ParseSearchJson(string json)
        {
            var repos = new List<GitHubRepo>();
            if (string.IsNullOrEmpty(json))
            {
                return repos;
            }

            var wrapper = JsonUtility.FromJson<SearchResultWrapper>(json);
            if (wrapper?.items == null)
            {
                return repos;
            }

            foreach (var repoJson in wrapper.items)
            {
                repos.Add(new GitHubRepo
                {
                    Name = repoJson.name,
                    Owner = repoJson.owner != null ? repoJson.owner.login : string.Empty,
                    Url = string.IsNullOrWhiteSpace(repoJson.url) ? repoJson.html_url : repoJson.url,
                    DefaultBranch = repoJson.default_branch,
                    IsPrivate = repoJson.@private,
                    Description = repoJson.description
                });
            }

            return repos;
        }

        internal static string BuildRepoListError(string message, CommandResult result)
        {
            return BuildError(message, result);
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

            if (IsNotFoundResult(result))
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

        internal static string DerivePackageNameSuggestion(string owner, string repoName)
        {
            if (string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(repoName))
            {
                return string.Empty;
            }

            if (GitUtility.IsValidPackageName(repoName))
            {
                return repoName;
            }

            string sanitizedOwner = SanitizeForPackageName(owner);
            string sanitizedRepo = SanitizeForPackageName(repoName);

            if (string.IsNullOrEmpty(sanitizedOwner) || string.IsNullOrEmpty(sanitizedRepo))
            {
                return string.Empty;
            }

            return $"com.{sanitizedOwner}.{sanitizedRepo}";
        }

        private static string SanitizeForPackageName(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
            {
                return string.Empty;
            }

            var sb = new System.Text.StringBuilder();
            foreach (char c in input.ToLowerInvariant())
            {
                if (c >= 'a' && c <= 'z' || c >= '0' && c <= '9')
                {
                    sb.Append(c);
                }
            }
            return sb.ToString();
        }

        internal static bool IsNotFoundResult(CommandResult result)
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
        private sealed class SearchResultWrapper
        {
            public int total_count;
            public RepoJson[] items;
        }

        [Serializable]
        private sealed class RepoJson
        {
            public string name;
            public OwnerJson owner;
            public string url;
            public string html_url;
            public DefaultBranchRefJson defaultBranchRef;
            public bool isPrivate;
            public bool @private;
            public string default_branch;
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
