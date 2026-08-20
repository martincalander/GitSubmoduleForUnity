using System;
using System.Collections.Generic;

namespace MartinCalander.GitSubmoduleManager.Editor
{
    internal sealed class RepositoryCoordinator : IDisposable
    {
        private readonly Dictionary<string, List<string>> branchCache = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> branchErrors = new(StringComparer.Ordinal);
        private string branchFetchIdentity = string.Empty;
        private string pendingBranchFetchUrl = string.Empty;
        private AsyncCommandHandle branchFetchHandle;
        private bool discardBranchFetchResult;

        internal void RequestBranches(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                return;
            }

            if (!GitUtility.IsValidRepositoryUrl(url))
                return;

            string identity = GitHubUtility.GetRepositoryCacheIdentity(url);
            if (string.IsNullOrEmpty(identity) ||
                branchCache.ContainsKey(identity) ||
                branchErrors.ContainsKey(identity))
            {
                return;
            }

            if (branchFetchHandle != null)
            {
                if (string.Equals(branchFetchIdentity, identity, StringComparison.Ordinal))
                {
                    if (discardBranchFetchResult)
                        pendingBranchFetchUrl = url;
                    return;
                }

                // Only the newest request is useful. Keep one live process and
                // replace the queued request instead of spawning more workers.
                pendingBranchFetchUrl = url;
                return;
            }

            if (AsyncCommandDrainRegistry.IsDraining)
            {
                pendingBranchFetchUrl = url;
                return;
            }

            StartBranchFetch(url, identity);
        }

        private bool StartBranchFetch(string url, string identity)
        {
            if (string.IsNullOrWhiteSpace(url) || string.IsNullOrEmpty(identity) ||
                AsyncCommandDrainRegistry.IsDraining)
            {
                return false;
            }

            branchFetchIdentity = identity;
            discardBranchFetchResult = false;
            branchErrors.Remove(identity);
            branchFetchHandle = CliCommandRunner.RunAsync(
                GitUtility.GitExecutable,
                $"ls-remote --heads {GitUtility.Quote(url)}",
                GitUtility.ProjectRoot);
            return true;
        }

        internal bool IsFetchingBranches(string url)
        {
            string identity = GitHubUtility.GetRepositoryCacheIdentity(url);
            return !string.IsNullOrEmpty(identity) &&
                   branchFetchHandle != null &&
                   string.Equals(branchFetchIdentity, identity, StringComparison.Ordinal) &&
                   !TryGetCachedBranches(url, out _);
        }

        internal bool TickBranchFetch()
        {
            if (branchFetchHandle == null)
            {
                if (!string.IsNullOrWhiteSpace(pendingBranchFetchUrl) &&
                    !AsyncCommandDrainRegistry.IsDraining)
                {
                    string pendingUrl = pendingBranchFetchUrl;
                    string pendingIdentity = GitHubUtility.GetRepositoryCacheIdentity(pendingUrl);
                    if (!string.IsNullOrEmpty(pendingIdentity) &&
                        !branchCache.ContainsKey(pendingIdentity) &&
                        !branchErrors.ContainsKey(pendingIdentity) &&
                        GitUtility.IsValidRepositoryUrl(pendingUrl) &&
                        StartBranchFetch(pendingUrl, pendingIdentity))
                    {
                        pendingBranchFetchUrl = string.Empty;
                        return true;
                    }
                }

                return false;
            }

            if (!branchFetchHandle.IsComplete)
            {
                return false;
            }

            CommandResult result = branchFetchHandle.Result;
            string completedIdentity = branchFetchIdentity;
            bool discardResult = discardBranchFetchResult;
            var branches = new List<string>();
            bool outputComplete = result != null && !result.StdOutTruncated;
            if (result != null && result.IsSuccess && outputComplete)
            {
                branches = GitUtility.ParseRemoteBranches(result.StdOut);
            }

            if (!discardResult && !string.IsNullOrWhiteSpace(completedIdentity))
            {
                branchCache[completedIdentity] = branches;
                if (result != null && result.IsSuccess && !outputComplete)
                {
                    branchErrors[completedIdentity] =
                        "Git returned more branch data than could be inspected safely. " +
                        "The partial branch list was discarded; narrow the repository or retry from a terminal.";
                }
                else if (result == null || !result.IsSuccess)
                {
                    string detail = result == null
                        ? "No result was returned."
                        : string.IsNullOrWhiteSpace(result.StdErr) ? result.StdOut : result.StdErr;
                    branchErrors[completedIdentity] = string.IsNullOrWhiteSpace(detail)
                        ? "Failed to load remote branches."
                        : GitHubUtility.SanitizeUiDiagnostic(detail);
                }
                else if (branches.Count == 0)
                {
                    branchErrors[completedIdentity] = "No remote branches were found.";
                }
            }

            branchFetchHandle = null;
            branchFetchIdentity = string.Empty;
            discardBranchFetchResult = false;

            if (!string.IsNullOrWhiteSpace(pendingBranchFetchUrl))
            {
                string pendingUrl = pendingBranchFetchUrl;
                pendingBranchFetchUrl = string.Empty;
                string pendingIdentity = GitHubUtility.GetRepositoryCacheIdentity(pendingUrl);
                if (!string.IsNullOrEmpty(pendingIdentity) &&
                    !branchCache.ContainsKey(pendingIdentity) &&
                    !branchErrors.ContainsKey(pendingIdentity) &&
                    GitUtility.IsValidRepositoryUrl(pendingUrl))
                {
                    if (!StartBranchFetch(pendingUrl, pendingIdentity))
                        pendingBranchFetchUrl = pendingUrl;
                }
            }

            return true;
        }

        internal bool TryGetCachedBranches(string url, out List<string> branches)
        {
            branches = null;
            string identity = GitHubUtility.GetRepositoryCacheIdentity(url);
            return !string.IsNullOrEmpty(identity) &&
                   branchCache.TryGetValue(identity, out branches) &&
                   branches != null &&
                   branches.Count > 0;
        }

        internal void ClearBranchCache(string url)
        {
            if (!string.IsNullOrWhiteSpace(url))
            {
                string identity = GitHubUtility.GetRepositoryCacheIdentity(url);
                branchCache.Remove(identity);
                branchErrors.Remove(identity);
            }
        }

        internal void ClearAllBranchCaches()
        {
            branchCache.Clear();
            branchErrors.Clear();
            pendingBranchFetchUrl = string.Empty;
            if (branchFetchHandle != null)
                discardBranchFetchResult = true;
        }

        internal bool TryGetBranchError(string url, out string error)
        {
            error = string.Empty;
            string identity = GitHubUtility.GetRepositoryCacheIdentity(url);
            return !string.IsNullOrEmpty(identity) && branchErrors.TryGetValue(identity, out error);
        }

        public void Dispose()
        {
            branchCache.Clear();
            branchErrors.Clear();
            pendingBranchFetchUrl = string.Empty;
            AsyncCommandDrainRegistry.Retire(branchFetchHandle);
            branchFetchHandle = null;
            branchFetchIdentity = string.Empty;
            discardBranchFetchResult = false;
        }
    }
}
