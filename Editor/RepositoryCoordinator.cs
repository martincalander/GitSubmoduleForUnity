using System;
using System.Collections.Generic;

namespace MartinCalander.GitSubmoduleManager.Editor
{
    internal sealed class RepositoryCoordinator : IDisposable
    {
        internal const int MaximumRemoteBranchCount = 2048;

        private const int MaximumRemoteRefLineLength = 8192;
        private const string HeadsPrefix = "refs/heads/";

        private readonly Dictionary<string, List<string>> branchCache = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> defaultBranchCache =
            new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> branchErrors = new(StringComparer.Ordinal);
        private string branchFetchIdentity = string.Empty;
        private string pendingBranchFetchUrl = string.Empty;
        private AsyncCommandHandle branchFetchHandle;
        private bool discardBranchFetchResult;

        internal bool HasPendingBranchWork =>
            branchFetchHandle != null ||
            !string.IsNullOrWhiteSpace(pendingBranchFetchUrl);

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
                new[]
                {
                    "-c", "credential.interactive=false",
                    "ls-remote", "--symref", "--",
                    url,
                    "HEAD", "refs/heads/*"
                },
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
            string defaultBranch = string.Empty;
            string parseDiagnostic = string.Empty;
            bool outputComplete = result != null &&
                                  result.TerminationConfirmed &&
                                  !result.StdOutTruncated &&
                                  !result.StdErrTruncated;
            bool parsed = result != null &&
                          result.IsSuccess &&
                          outputComplete &&
                          TryParseRemoteBranchesAndDefault(
                              result.StdOut,
                              out branches,
                              out defaultBranch,
                              out parseDiagnostic);

            if (!discardResult && !string.IsNullOrWhiteSpace(completedIdentity))
            {
                if (result == null || !result.IsSuccess)
                {
                    string detail = result == null
                        ? "No result was returned."
                        : string.IsNullOrWhiteSpace(result.StdErr) ? result.StdOut : result.StdErr;
                    branchErrors[completedIdentity] = string.IsNullOrWhiteSpace(detail)
                        ? "Failed to load remote branches."
                        : GitHubUtility.SanitizeUiDiagnostic(detail);
                }
                else if (!result.TerminationConfirmed)
                {
                    branchErrors[completedIdentity] =
                        "Git process termination could not be confirmed. The remote branch response was discarded.";
                }
                else if (result.StdOutTruncated)
                {
                    branchErrors[completedIdentity] =
                        "Git returned more branch data than could be inspected safely. " +
                        "The partial branch list was discarded; narrow the repository or retry from a terminal.";
                }
                else if (result.StdErrTruncated)
                {
                    branchErrors[completedIdentity] =
                        "Git returned more diagnostic data than could be inspected safely. The remote branch response was discarded.";
                }
                else if (!parsed)
                {
                    branchErrors[completedIdentity] = string.IsNullOrWhiteSpace(
                            parseDiagnostic)
                        ? "The remote branch response could not be verified."
                        : parseDiagnostic;
                }
                else
                {
                    branchCache[completedIdentity] = branches;
                    defaultBranchCache[completedIdentity] = defaultBranch;
                    if (!string.IsNullOrWhiteSpace(parseDiagnostic))
                        branchErrors[completedIdentity] = parseDiagnostic;
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
            return TryGetCachedBranches(
                url,
                out branches,
                out _);
        }

        internal bool TryGetCachedBranches(
            string url,
            out List<string> branches,
            out string defaultBranch)
        {
            branches = null;
            defaultBranch = string.Empty;
            string identity = GitHubUtility.GetRepositoryCacheIdentity(url);
            if (string.IsNullOrEmpty(identity) ||
                !branchCache.TryGetValue(identity, out branches) ||
                branches == null ||
                branches.Count == 0)
            {
                return false;
            }

            defaultBranchCache.TryGetValue(identity, out defaultBranch);
            defaultBranch ??= string.Empty;
            return true;
        }

        internal void ClearBranchCache(string url)
        {
            if (!string.IsNullOrWhiteSpace(url))
            {
                string identity = GitHubUtility.GetRepositoryCacheIdentity(url);
                branchCache.Remove(identity);
                defaultBranchCache.Remove(identity);
                branchErrors.Remove(identity);
            }
        }

        internal void ClearAllBranchCaches()
        {
            branchCache.Clear();
            defaultBranchCache.Clear();
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

        internal static bool TryParseRemoteBranchesAndDefault(
            string output,
            out List<string> branches,
            out string defaultBranch,
            out string diagnostic)
        {
            branches = new List<string>();
            defaultBranch = string.Empty;
            diagnostic = string.Empty;
            if (string.IsNullOrWhiteSpace(output))
            {
                diagnostic = "No remote branches were found.";
                return false;
            }

            var branchObjectIds = new Dictionary<string, string>(
                StringComparer.Ordinal);
            string symbolicDefault = string.Empty;
            string headObjectId = string.Empty;
            int symbolicHeadCount = 0;
            bool malformedSymbolicHead = false;
            string[] lines = output.Split(new[] { '\n' }, StringSplitOptions.None);
            for (int lineIndex = 0; lineIndex < lines.Length; lineIndex++)
            {
                string line = lines[lineIndex];
                if (line.EndsWith("\r", StringComparison.Ordinal))
                    line = line.Substring(0, line.Length - 1);
                if (line.IndexOf('\r') >= 0)
                {
                    branches.Clear();
                    diagnostic =
                        "Git returned malformed line endings; the branch list was discarded.";
                    return false;
                }

                if (line.Length == 0)
                {
                    if (lineIndex == lines.Length - 1)
                        continue;

                    branches.Clear();
                    diagnostic =
                        "Git returned an unexpected blank remote-ref line; the branch list was discarded.";
                    return false;
                }

                if (line.Length == 0 || line.Length > MaximumRemoteRefLineLength)
                {
                    branches.Clear();
                    diagnostic =
                        "Git returned a remote-ref line that could not be inspected safely.";
                    return false;
                }

                if (line.StartsWith("ref:", StringComparison.Ordinal))
                {
                    int separator = line.IndexOf('\t');
                    if (separator <= 5 ||
                        separator != line.LastIndexOf('\t') ||
                        separator + 1 >= line.Length)
                    {
                        branches.Clear();
                        diagnostic =
                            "Git returned malformed symbolic-ref data; the branch list was discarded.";
                        return false;
                    }

                    string target = separator > 5
                        ? line.Substring(5, separator - 5)
                        : string.Empty;
                    string name = separator >= 0 && separator + 1 < line.Length
                        ? line.Substring(separator + 1)
                        : string.Empty;
                    if (!string.Equals(name, "HEAD", StringComparison.Ordinal))
                    {
                        if (!IsSafeRemoteRefName(target) ||
                            !IsSafeRemoteRefName(name))
                        {
                            branches.Clear();
                            diagnostic =
                                "Git returned malformed symbolic-ref data; the branch list was discarded.";
                            return false;
                        }

                        // The tail-pattern HEAD also matches refs such as
                        // refs/pull/1/HEAD and refs/remotes/origin/HEAD. They are
                        // valid advertisements, but only the exact HEAD
                        // pseudoref is authoritative for the remote default.
                        continue;
                    }

                    bool valid = target.StartsWith(
                        HeadsPrefix,
                        StringComparison.Ordinal);
                    string candidate = valid
                        ? target.Substring(HeadsPrefix.Length)
                        : string.Empty;
                    valid = valid && IsExactRemoteBranchName(candidate);
                    if (!valid)
                    {
                        malformedSymbolicHead = true;
                        continue;
                    }

                    symbolicHeadCount++;
                    if (symbolicHeadCount == 1)
                        symbolicDefault = candidate;
                    else if (!string.Equals(
                                 symbolicDefault,
                                 candidate,
                                 StringComparison.Ordinal))
                        malformedSymbolicHead = true;
                    continue;
                }

                int tab = line.IndexOf('\t');
                if (tab <= 0 ||
                    tab != line.LastIndexOf('\t') ||
                    tab + 1 >= line.Length)
                {
                    branches.Clear();
                    diagnostic =
                        "Git returned malformed remote-ref data; the branch list was discarded.";
                    return false;
                }

                string objectId = line.Substring(0, tab);
                string refName = line.Substring(tab + 1);
                if (!IsGitObjectId(objectId))
                {
                    branches.Clear();
                    diagnostic =
                        "Git returned an invalid remote object ID; the branch list was discarded.";
                    return false;
                }

                if (string.Equals(refName, "HEAD", StringComparison.Ordinal))
                {
                    if (string.IsNullOrEmpty(headObjectId))
                        headObjectId = objectId;
                    else if (!string.Equals(
                                 headObjectId,
                                 objectId,
                                 StringComparison.OrdinalIgnoreCase))
                    {
                        branches.Clear();
                        diagnostic =
                            "Git returned conflicting HEAD object IDs; the branch list was discarded.";
                        return false;
                    }

                    continue;
                }
                if (!refName.StartsWith(HeadsPrefix, StringComparison.Ordinal))
                {
                    if (!IsSafeRemoteRefName(refName))
                    {
                        branches.Clear();
                        diagnostic =
                            "Git returned an invalid remote ref; the branch list was discarded.";
                        return false;
                    }

                    // Validate but ignore well-formed refs returned because the
                    // HEAD pattern matches the final path component.
                    continue;
                }

                string branch = refName.Substring(HeadsPrefix.Length);
                if (!IsExactRemoteBranchName(branch))
                {
                    branches.Clear();
                    diagnostic =
                        "Git returned an invalid remote branch; the branch list was discarded.";
                    return false;
                }

                if (branchObjectIds.TryGetValue(
                        branch,
                        out string existingObjectId))
                {
                    if (!string.Equals(
                            existingObjectId,
                            objectId,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        branches.Clear();
                        diagnostic =
                            "Git returned conflicting duplicate branch refs; the branch list was discarded.";
                        return false;
                    }

                    continue;
                }

                branchObjectIds.Add(branch, objectId);
                if (branchObjectIds.Count > MaximumRemoteBranchCount)
                {
                    branches.Clear();
                    diagnostic =
                        $"The repository exposes more than {MaximumRemoteBranchCount} branches; " +
                        "the partial branch list was discarded.";
                    return false;
                }

                branches.Add(branch);
            }

            if (branches.Count == 0)
            {
                diagnostic = "No remote branches were found.";
                return false;
            }

            bool hasOneValidSymbolicHead = symbolicHeadCount == 1 &&
                                           !malformedSymbolicHead;
            if (!hasOneValidSymbolicHead)
            {
                diagnostic =
                    "Git did not return one valid HEAD symbolic ref. Select a branch explicitly or refresh to retry.";
                return true;
            }

            if (!branchObjectIds.TryGetValue(
                    symbolicDefault,
                    out string defaultObjectId))
            {
                diagnostic =
                    "Git's HEAD symbolic ref was not present in the complete remote branch list. Select a branch explicitly or refresh to retry.";
                return true;
            }

            if (string.IsNullOrEmpty(headObjectId) ||
                !string.Equals(
                    headObjectId,
                    defaultObjectId,
                    StringComparison.OrdinalIgnoreCase))
            {
                diagnostic =
                    "Git's HEAD object ID did not match its target branch. Select a branch explicitly or refresh to retry.";
                return true;
            }

            defaultBranch = symbolicDefault;
            return true;
        }

        private static bool IsGitObjectId(string value)
        {
            return GitUtility.IsValidGitObjectId(value);
        }

        private static bool IsExactRemoteBranchName(string value)
        {
            // GitUtility.IsValidBranchName intentionally accepts an empty value
            // for optional user input and validates the trimmed representation.
            // Remote advertisements are structural data: accepting surrounding
            // whitespace here would let the UI normalize a different branch name
            // into an apparently authoritative choice.
            return !string.IsNullOrWhiteSpace(value) &&
                   string.Equals(value, value.Trim(), StringComparison.Ordinal) &&
                   !string.Equals(value, ".", StringComparison.Ordinal) &&
                   GitUtility.IsValidBranchName(value);
        }

        private static bool IsSafeRemoteRefName(string value)
        {
            if (string.IsNullOrWhiteSpace(value) ||
                !string.Equals(value, value.Trim(), StringComparison.Ordinal) ||
                !value.StartsWith("refs/", StringComparison.Ordinal) ||
                value.EndsWith("/", StringComparison.Ordinal) ||
                value.EndsWith(".", StringComparison.Ordinal) ||
                value.Contains("..") ||
                value.Contains("@{") ||
                value.Contains("//") ||
                value.IndexOfAny(new[]
                {
                    '~', '^', ':', '?', '*', '[', '\\'
                }) >= 0)
            {
                return false;
            }

            foreach (char character in value)
            {
                if (character <= ' ' || character == 0x7f)
                    return false;
            }

            foreach (string segment in value.Split('/'))
            {
                if (string.IsNullOrEmpty(segment) ||
                    string.Equals(segment, ".", StringComparison.Ordinal) ||
                    string.Equals(segment, "..", StringComparison.Ordinal) ||
                    segment.EndsWith(".lock", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }

            return true;
        }

        public void Dispose()
        {
            branchCache.Clear();
            defaultBranchCache.Clear();
            branchErrors.Clear();
            pendingBranchFetchUrl = string.Empty;
            AsyncCommandDrainRegistry.Retire(branchFetchHandle);
            branchFetchHandle = null;
            branchFetchIdentity = string.Empty;
            discardBranchFetchResult = false;
        }
    }
}
