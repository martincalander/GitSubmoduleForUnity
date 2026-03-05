using System;
using System.Collections.Generic;

namespace Calander.SubmodulePackageManager.Editor
{
    internal sealed class SubmoduleRepositoryCoordinator
    {
        private RepoListHandle repoListHandle;
        private List<GitHubRepo> packageJsonRepos;
        private int packageJsonCheckIndex;
        private readonly Dictionary<string, List<string>> branchCache = new(StringComparer.OrdinalIgnoreCase);
        private string branchFetchUrl = string.Empty;
        private AsyncCommandHandle branchFetchHandle;

        internal bool IsLoadingRepos => repoListHandle != null;
        internal RepoListHandle RepoListHandle => repoListHandle;
        internal bool IsCheckingPackageJson => packageJsonRepos != null;
        internal int PackageJsonCheckIndex => packageJsonCheckIndex;
        internal int PackageJsonRepoCount => packageJsonRepos?.Count ?? 0;

        internal void BeginRefreshAvailable()
        {
            if (repoListHandle != null && !repoListHandle.IsComplete)
            {
                return;
            }

            repoListHandle = GitHubUtility.StartListReposAsync();
        }

        internal bool TickRefreshAvailable(out List<GitHubRepo> repos, out string error)
        {
            repos = null;
            error = string.Empty;

            if (repoListHandle == null)
            {
                return false;
            }

            repoListHandle.Update();
            if (!repoListHandle.IsComplete)
            {
                return false;
            }

            if (!repoListHandle.IsSuccess)
            {
                error = repoListHandle.Error;
                repoListHandle = null;
                repos = new List<GitHubRepo>();
                return true;
            }

            repos = repoListHandle.Repos ?? new List<GitHubRepo>();
            repoListHandle = null;
            return true;
        }

        internal void BeginPackageJsonChecks(List<GitHubRepo> repos)
        {
            packageJsonRepos = repos;
            packageJsonCheckIndex = 0;
        }

        internal bool TickPackageJsonChecks()
        {
            if (packageJsonRepos == null)
            {
                return false;
            }

            if (packageJsonCheckIndex >= packageJsonRepos.Count)
            {
                packageJsonRepos = null;
                return true;
            }

            var repo = packageJsonRepos[packageJsonCheckIndex];
            if (GitHubUtility.TryRepoHasPackageJson(repo.Owner, repo.Name, out bool hasPackageJson, out _))
            {
                repo.HasPackageJson = hasPackageJson;
            }

            repo.PackageJsonChecked = true;
            packageJsonCheckIndex++;

            if (packageJsonCheckIndex >= packageJsonRepos.Count)
            {
                packageJsonRepos = null;
                return true;
            }

            return false;
        }

        internal void RequestBranches(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                return;
            }

            if (branchCache.ContainsKey(url))
            {
                return;
            }

            if (branchFetchHandle != null)
            {
                if (string.Equals(branchFetchUrl, url, StringComparison.OrdinalIgnoreCase) || !branchFetchHandle.IsComplete)
                {
                    return;
                }
            }

            branchFetchUrl = url;
            branchFetchHandle = CliCommandRunner.RunAsync("git", $"ls-remote --heads {url}", GitUtility.ProjectRoot);
        }

        internal bool IsFetchingBranches(string url)
        {
            return branchFetchHandle != null &&
                   string.Equals(branchFetchUrl, url, StringComparison.OrdinalIgnoreCase) &&
                   !TryGetCachedBranches(url, out _);
        }

        internal bool TickBranchFetch()
        {
            if (branchFetchHandle == null)
            {
                return false;
            }

            if (!branchFetchHandle.IsComplete)
            {
                return false;
            }

            var branches = new List<string>();
            if (branchFetchHandle.Result.IsSuccess)
            {
                branches = GitUtility.ParseRemoteBranches(branchFetchHandle.Result.StdOut);
            }

            if (!string.IsNullOrWhiteSpace(branchFetchUrl))
            {
                branchCache[branchFetchUrl] = branches;
            }

            branchFetchHandle = null;
            branchFetchUrl = string.Empty;
            return true;
        }

        internal bool TryGetCachedBranches(string url, out List<string> branches)
        {
            branches = null;
            return !string.IsNullOrWhiteSpace(url) &&
                   branchCache.TryGetValue(url, out branches) &&
                   branches != null &&
                   branches.Count > 0;
        }

        internal void ClearBranchCache(string url)
        {
            if (!string.IsNullOrWhiteSpace(url))
            {
                branchCache.Remove(url);
            }
        }

        internal void Dispose()
        {
            repoListHandle = null;
            packageJsonRepos = null;
            packageJsonCheckIndex = 0;
            branchCache.Clear();
            branchFetchUrl = string.Empty;
            branchFetchHandle = null;
        }
    }
}
