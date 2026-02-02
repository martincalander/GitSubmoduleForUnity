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

    internal sealed class RepoListHandle
    {
        public bool IsComplete { get; private set; }
        public bool IsSuccess { get; private set; }
        public string Error { get; private set; }
        public List<GitHubRepo> Repos { get; private set; }
        public float Progress { get; private set; }
        public string StatusMessage { get; private set; }

        private AsyncCommandHandle commandHandle;
        private int currentPage;
        private int totalPages;
        private const int PageSize = 30;
        private const int MaxRepos = 200;

        public RepoListHandle()
        {
            Repos = new List<GitHubRepo>();
            StatusMessage = "Initializing...";
        }

        public void Start()
        {
            currentPage = 1;
            totalPages = (MaxRepos + PageSize - 1) / PageSize;
            FetchNextPage();
        }

        private void FetchNextPage()
        {
            StatusMessage = $"Fetching repositories (page {currentPage})...";
            Progress = (float)(currentPage - 1) / totalPages * 0.9f;

            string args = $"repo list --limit {PageSize} --json name,owner,url,defaultBranchRef,isPrivate,description";
            if (currentPage > 1)
            {
                args += $" --jq '.[{(currentPage - 1) * PageSize}:{currentPage * PageSize}]'";
            }

            commandHandle = CliCommandRunner.RunAsync("gh",
                $"repo list --limit {PageSize} --json name,owner,url,defaultBranchRef,isPrivate,description",
                GitUtility.ProjectRoot);
        }

        public void Update()
        {
            if (IsComplete || commandHandle == null)
            {
                return;
            }

            if (!commandHandle.IsComplete)
            {
                return;
            }

            var result = commandHandle.Result;
            if (!result.IsSuccess)
            {
                Error = GitHubUtility.BuildRepoListError("Failed to list GitHub repositories", result);
                IsSuccess = false;
                IsComplete = true;
                return;
            }

            string json = result.StdOut.Trim();
            if (!string.IsNullOrEmpty(json))
            {
                var pageRepos = GitHubUtility.ParseRepoJson(json);
                if (pageRepos != null && pageRepos.Count > 0)
                {
                    Repos.AddRange(pageRepos);
                }
            }

            Progress = 1f;
            StatusMessage = $"Loaded {Repos.Count} repositories";
            IsSuccess = true;
            IsComplete = true;
        }
    }

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

        internal static RepoListHandle StartListReposAsync()
        {
            var handle = new RepoListHandle();
            handle.Start();
            return handle;
        }

        internal static bool TryListRepos(out List<GitHubRepo> repos, out string error)
        {
            repos = new List<GitHubRepo>();
            error = string.Empty;

            var result = CliCommandRunner.Run("gh", "repo list --limit 200 --json name,owner,url,defaultBranchRef,isPrivate,description", GitUtility.ProjectRoot);
            if (!result.IsSuccess)
            {
                error = BuildRepoListError("Failed to list GitHub repositories", result);
                return false;
            }

            repos = ParseRepoJson(result.StdOut.Trim());
            return true;
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
                    Url = repoJson.url,
                    DefaultBranch = repoJson.defaultBranchRef != null ? repoJson.defaultBranchRef.name : string.Empty,
                    IsPrivate = repoJson.isPrivate,
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

        internal static string DerivePackageNameSuggestion(string owner, string repoName)
        {
            if (string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(repoName))
            {
                return string.Empty;
            }

            // If the repo name already matches the package name format, use it
            if (GitUtility.IsValidPackageName(repoName))
            {
                return repoName;
            }

            // Sanitize owner and repo: lowercase, replace non-alphanumeric with empty
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
