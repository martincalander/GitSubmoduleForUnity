using System;
using System.Collections.Generic;

namespace Essentials.GitPackageManager.Editor
{
    internal sealed class RepositoryCoordinator
    {
        private readonly Dictionary<string, List<string>> branchCache = new(StringComparer.OrdinalIgnoreCase);
        private string branchFetchUrl = string.Empty;
        private AsyncCommandHandle branchFetchHandle;

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
            if (!GitUtility.IsValidRepositoryUrl(url))
                return;

            branchFetchHandle = CliCommandRunner.RunAsync("git", $"ls-remote --heads {GitUtility.Quote(url)}", GitUtility.ProjectRoot);
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
            branchCache.Clear();
            branchFetchUrl = string.Empty;
            branchFetchHandle = null;
        }
    }
}
