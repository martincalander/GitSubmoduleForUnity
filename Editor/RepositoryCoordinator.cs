using System;
using System.Collections.Generic;

namespace Essentials.GitPackageManager.Editor
{
    internal sealed class RepositoryCoordinator
    {
        private readonly Dictionary<string, List<string>> branchCache = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> branchErrors = new(StringComparer.OrdinalIgnoreCase);
        private string branchFetchUrl = string.Empty;
        private string pendingBranchFetchUrl = string.Empty;
        private AsyncCommandHandle branchFetchHandle;

        internal void RequestBranches(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                return;
            }

            if (branchCache.ContainsKey(url) || branchErrors.ContainsKey(url))
            {
                return;
            }

            if (branchFetchHandle != null)
            {
                if (string.Equals(branchFetchUrl, url, StringComparison.OrdinalIgnoreCase))
                    return;

                pendingBranchFetchUrl = url;
                return;
            }

            if (!GitUtility.IsValidRepositoryUrl(url))
                return;

            StartBranchFetch(url);
        }

        private void StartBranchFetch(string url)
        {
            branchFetchUrl = url;
            branchErrors.Remove(url);
            branchFetchHandle = CliCommandRunner.RunAsync(
                GitUtility.GitExecutable,
                $"ls-remote --heads {GitUtility.Quote(url)}",
                GitUtility.ProjectRoot);
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

            CommandResult result = branchFetchHandle.Result;
            var branches = new List<string>();
            if (result != null && result.IsSuccess)
            {
                branches = GitUtility.ParseRemoteBranches(result.StdOut);
            }

            if (!string.IsNullOrWhiteSpace(branchFetchUrl))
            {
                branchCache[branchFetchUrl] = branches;
                if (result == null || !result.IsSuccess)
                {
                    string detail = result == null
                        ? "No result was returned."
                        : string.IsNullOrWhiteSpace(result.StdErr) ? result.StdOut : result.StdErr;
                    branchErrors[branchFetchUrl] = string.IsNullOrWhiteSpace(detail)
                        ? "Failed to load remote branches."
                        : GitUtility.RedactCredentials(detail.Trim());
                }
                else if (branches.Count == 0)
                {
                    branchErrors[branchFetchUrl] = "No remote branches were found.";
                }
            }

            branchFetchHandle = null;
            branchFetchUrl = string.Empty;

            if (!string.IsNullOrWhiteSpace(pendingBranchFetchUrl))
            {
                string pendingUrl = pendingBranchFetchUrl;
                pendingBranchFetchUrl = string.Empty;
                if (!branchCache.ContainsKey(pendingUrl) && !branchErrors.ContainsKey(pendingUrl))
                    StartBranchFetch(pendingUrl);
            }

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
                branchErrors.Remove(url);
            }
        }

        internal bool TryGetBranchError(string url, out string error)
        {
            error = string.Empty;
            return !string.IsNullOrWhiteSpace(url) && branchErrors.TryGetValue(url, out error);
        }

        internal void Dispose()
        {
            branchCache.Clear();
            branchErrors.Clear();
            branchFetchUrl = string.Empty;
            pendingBranchFetchUrl = string.Empty;
            branchFetchHandle = null;
        }
    }
}
