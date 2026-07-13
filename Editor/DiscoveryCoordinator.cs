using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace MartinCalander.GitPackageManager.Editor
{
    internal sealed class DiscoveryCoordinator : IDisposable
    {
        private const int PageSize = 50;
        private const int MaximumSearchResults = 1000;
        private const int PackageManifestBatchSize = 8;
        private const int MaximumPackageManifestBytes = 64 * 1024;
        private const int MaximumManifestCacheEntries = 2048;
        private const double SearchDebounceSeconds = 0.3;
        private const string RepositoryListProjection =
            "[.[] | {node_id, name, owner: {login: .owner.login}, clone_url, html_url, default_branch, private, description, updated_at}]";
        private const string RepositorySearchProjection =
            "{total_count, items: [.items[] | {node_id, name, owner: {login: .owner.login}, clone_url, html_url, default_branch, private, description, updated_at}]}";
        private const string PackageManifestQuery =
            "query($ids: [ID!]!) { nodes(ids: $ids) { ... on Repository { id packageManifest: object(expression: \"HEAD:package.json\") { __typename oid ... on Blob { byteSize isBinary isTruncated text } } } } rateLimit { cost remaining resetAt } }";

        [Serializable]
        private sealed class PackageManifestGraphQlResponse
        {
            public PackageManifestGraphQlData data;
            public PackageManifestGraphQlError[] errors;
        }

        [Serializable]
        private sealed class PackageManifestGraphQlData
        {
            public PackageManifestNode[] nodes;
            public PackageManifestRateLimit rateLimit;
        }

        [Serializable]
        private sealed class PackageManifestNode
        {
            public string id;
            public PackageManifestBlob packageManifest;
        }

        [Serializable]
        private sealed class PackageManifestBlob
        {
            public string __typename;
            public string oid;
            public int byteSize;
            public bool isBinary;
            public bool isTruncated;
            public string text;
        }

        [Serializable]
        private sealed class PackageManifestRateLimit
        {
            public int cost;
            public int remaining;
            public string resetAt;
        }

        [Serializable]
        private sealed class PackageManifestGraphQlError
        {
            public string message;
        }

        private sealed class PackageManifestBatch
        {
            public int Generation;
            public readonly List<GitHubRepo> Repositories = new();
        }

        private sealed class PackageManifestCacheEntry
        {
            public PackageManifestState State;
            public string PackageName;
            public string Message;
        }

        private sealed class PageRequest
        {
            public string Arguments;
            public bool IsSearch;
            public int Page;
        }

        private string cachedUsername = string.Empty;
        private AsyncCommandHandle usernameHandle;
        private AsyncCommandHandle orgsHandle;
        private bool usernameRequested;
        private bool organizationsRequested;

        private AsyncCommandHandle pageHandle;
        private PageRequest activePageRequest;
        private PageRequest pendingPageRequest;
        private bool discardActivePageResult;
        private bool pageFetchDeferredUntilOwnerKnown;
        private int currentPage = 1;
        private string currentSearchQuery = string.Empty;
        private bool isSearchMode;
        private string selectedOwner = string.Empty;

        private double pendingSearchTime;
        private string pendingSearchQuery;

        private AsyncCommandHandle packageJsonHandle;
        private GitHubRepo packageJsonTarget;
        private GitHubRepo pendingPackageJsonTarget;
        private bool discardActivePackageJsonResult;

        private readonly Queue<PackageManifestBatch> pendingPackageManifestBatches = new();
        private readonly Dictionary<string, PackageManifestCacheEntry> packageManifestCache =
            new(StringComparer.Ordinal);
        private AsyncCommandHandle packageManifestBatchHandle;
        private PackageManifestBatch activePackageManifestBatch;
        private int packageManifestValidationGeneration;
        private bool validPackageFilterEnabled;

        internal bool IsLoading =>
            pageFetchDeferredUntilOwnerKnown ||
            pageHandle != null && !pageHandle.IsComplete;
        internal bool IsCheckingPackageManifest =>
            packageJsonHandle != null || pendingPackageJsonTarget != null;
        internal int CurrentPage => currentPage;
        internal string StatusMessage => pageFetchDeferredUntilOwnerKnown
            ? "Identifying the authenticated GitHub account..."
            : pageHandle?.StatusMessage ?? string.Empty;
        internal string ErrorMessage { get; private set; } = string.Empty;
        internal string WarningMessage { get; private set; } = string.Empty;

        internal List<GitHubRepo> DisplayedRepos { get; private set; } = new();
        internal bool HasResults => DisplayedRepos.Count > 0;
        internal bool HasNextPage { get; private set; }
        internal bool HasPrevPage => currentPage > 1;
        internal bool PageChanged { get; private set; }
        internal bool IsValidatingPackageManifests =>
            validPackageFilterEnabled &&
            (packageManifestBatchHandle != null ||
             pendingPackageManifestBatches.Count > 0 ||
             packageJsonHandle != null && packageJsonTarget != null && DisplayedRepos.Contains(packageJsonTarget));
        internal int PackageManifestCheckTotal => validPackageFilterEnabled
            ? DisplayedRepos.Count
            : 0;
        internal int PackageManifestCheckCompleted => validPackageFilterEnabled
            ? CountPackageManifestStates(PackageManifestState.Valid) +
              CountPackageManifestStates(PackageManifestState.Missing) +
              CountPackageManifestStates(PackageManifestState.Invalid) +
              CountPackageManifestStates(PackageManifestState.Unavailable)
            : 0;
        internal int PackageManifestUnavailableCount =>
            CountPackageManifestStates(PackageManifestState.Unavailable);

        internal string Username => cachedUsername;
        internal string SelectedOwner => selectedOwner;
        internal List<string> Organizations { get; private set; } = new();
        internal bool OrgsLoaded { get; private set; }

        internal void EnsureUsername()
        {
            if (!string.IsNullOrEmpty(cachedUsername))
            {
                if ((!OrgsLoaded || !string.IsNullOrEmpty(WarningMessage)) && orgsHandle == null)
                {
                    WarningMessage = string.Empty;
                    OrgsLoaded = false;
                    organizationsRequested = true;
                    TryStartOrganizationsRequest();
                }
                return;
            }

            if (usernameHandle != null)
                return;

            usernameRequested = true;
            TryStartUsernameRequest();
        }

        private bool TryStartUsernameRequest()
        {
            if (!usernameRequested || usernameHandle != null || !CanStartGitHubCommandNow)
                return false;

            usernameRequested = false;
            usernameHandle = CliCommandRunner.RunAsync(
                "gh",
                GitHubUtility.BuildApiArguments("user --jq .login"),
                GitUtility.ProjectRoot);
            return true;
        }

        private bool TryStartOrganizationsRequest()
        {
            if (!organizationsRequested || OrgsLoaded || orgsHandle != null ||
                string.IsNullOrEmpty(cachedUsername) ||
                !CanStartGitHubCommandNow)
            {
                return false;
            }

            organizationsRequested = false;
            orgsHandle = CliCommandRunner.RunAsync(
                "gh",
                GitHubUtility.BuildApiArguments("user/orgs --paginate --jq \".[].login\""),
                GitUtility.ProjectRoot);
            return true;
        }

        internal void SetOwner(string owner)
        {
            SetOwner(owner, string.Empty);
        }

        internal void SetOwner(string owner, string searchQuery)
        {
            if (string.Equals(selectedOwner, owner, StringComparison.OrdinalIgnoreCase))
                return;

            selectedOwner = owner ?? string.Empty;
            if (string.IsNullOrWhiteSpace(searchQuery))
            {
                LoadInitialPage();
                return;
            }

            currentPage = 1;
            isSearchMode = true;
            currentSearchQuery = searchQuery;
            pendingSearchQuery = null;
            FetchPage();
        }

        internal void LoadInitialPage()
        {
            currentPage = 1;
            isSearchMode = false;
            currentSearchQuery = string.Empty;
            pendingSearchQuery = null;
            FetchPage();
        }

        internal void ReloadCurrentPage()
        {
            FetchPage();
        }

        internal void SetSearchQuery(string query, double currentTime)
        {
            if (string.Equals(query, pendingSearchQuery, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            pendingSearchQuery = query;
            pendingSearchTime = currentTime + SearchDebounceSeconds;
        }

        internal void NextPage()
        {
            if (!HasNextPage)
            {
                return;
            }

            currentPage++;
            FetchPage();
        }

        internal void PrevPage()
        {
            if (!HasPrevPage)
            {
                return;
            }

            currentPage--;
            FetchPage();
        }

        internal void CheckPackageJson(GitHubRepo repo)
        {
            if (repo == null || repo.ManifestState != PackageManifestState.Unknown)
                return;

            if (packageJsonHandle != null)
            {
                if (ReferenceEquals(packageJsonTarget, repo) && !discardActivePackageJsonResult)
                {
                    pendingPackageJsonTarget = null;
                    return;
                }

                // Detail selection can change every GUI frame. Keep one live gh
                // request and only remember the newest target.
                pendingPackageJsonTarget = repo;
                return;
            }

            if (!StartPackageJsonCheck(repo))
                pendingPackageJsonTarget = repo;
        }

        private bool StartPackageJsonCheck(GitHubRepo repo)
        {
            if (repo == null || !CanStartGitHubCommandNow)
                return false;

            packageJsonTarget = repo;
            discardActivePackageJsonResult = false;
            repo.ManifestState = PackageManifestState.Checking;
            repo.PackageManifestMessage = string.Empty;
            string owner = Uri.EscapeDataString(repo.Owner ?? string.Empty);
            string name = Uri.EscapeDataString(repo.Name ?? string.Empty);
            packageJsonHandle = CliCommandRunner.RunAsync("gh",
                GitHubUtility.BuildApiArguments(
                    $"repos/{owner}/{name}/contents/package.json --jq .content"),
                GitUtility.ProjectRoot);
            return true;
        }

        internal void SetValidPackageFilterEnabled(bool enabled)
        {
            if (validPackageFilterEnabled == enabled)
            {
                if (enabled && !IsValidatingPackageManifests)
                    SchedulePackageManifestValidation();
                return;
            }

            validPackageFilterEnabled = enabled;
            packageManifestValidationGeneration++;
            pendingPackageManifestBatches.Clear();

            foreach (GitHubRepo repo in DisplayedRepos)
            {
                if (repo.ManifestState == PackageManifestState.Checking &&
                    !ReferenceEquals(repo, packageJsonTarget))
                {
                    repo.ManifestState = PackageManifestState.Unknown;
                    repo.PackageManifestMessage = string.Empty;
                }
            }

            if (enabled)
                SchedulePackageManifestValidation();
        }

        internal bool Tick(double currentTime)
        {
            bool changed = false;
            PageChanged = false;

            TryStartUsernameRequest();
            TryStartOrganizationsRequest();
            if (pageHandle == null && pendingPageRequest != null && CanStartGitHubCommandNow)
            {
                PageRequest request = pendingPageRequest;
                pendingPageRequest = null;
                StartPageRequest(request);
                changed = true;
            }

            if (packageJsonHandle == null && pendingPackageJsonTarget != null &&
                CanStartGitHubCommandNow)
            {
                GitHubRepo target = pendingPackageJsonTarget;
                pendingPackageJsonTarget = null;
                if (target.ManifestState == PackageManifestState.Unknown)
                    StartPackageJsonCheck(target);
                changed = true;
            }

            if (packageManifestBatchHandle == null &&
                pendingPackageManifestBatches.Count > 0 &&
                CanStartGitHubCommandNow)
            {
                StartNextPackageManifestBatch();
                changed = true;
            }

            if (usernameHandle != null && usernameHandle.IsComplete)
            {
                CommandResult usernameResult = usernameHandle.Result;
                bool usernameResolved = usernameResult != null &&
                                        usernameResult.IsSuccess &&
                                        !usernameResult.StdOutTruncated;
                if (usernameResolved)
                {
                    cachedUsername = (usernameResult.StdOut ?? string.Empty).Trim();
                    usernameResolved = !string.IsNullOrEmpty(cachedUsername);
                    if (string.IsNullOrEmpty(selectedOwner))
                        selectedOwner = cachedUsername;
                }

                usernameHandle = null;

                if (usernameResolved)
                {
                    if (pageFetchDeferredUntilOwnerKnown)
                        FetchPage();

                    if (!OrgsLoaded)
                        organizationsRequested = true;
                    TryStartOrganizationsRequest();
                }
                else
                {
                    pageFetchDeferredUntilOwnerKnown = false;
                    ErrorMessage = GitHubUtility.BuildRepoListError(
                        "Could not identify the authenticated GitHub account; repository search was not started",
                        usernameResult);
                }

                changed = true;
            }

            if (orgsHandle != null && orgsHandle.IsComplete)
            {
                CommandResult organizationsResult = orgsHandle.Result;
                if (organizationsResult != null &&
                    organizationsResult.IsSuccess &&
                    !organizationsResult.StdOutTruncated)
                {
                    Organizations = new List<string>();
                    string output = (organizationsResult.StdOut ?? string.Empty).Trim();
                    if (!string.IsNullOrEmpty(output))
                    {
                        foreach (string line in output.Split('\n'))
                        {
                            string org = line.Trim();
                            if (!string.IsNullOrEmpty(org))
                                Organizations.Add(org);
                        }
                    }
                    OrgsLoaded = true;
                    WarningMessage = string.Empty;
                }
                else
                {
                    // Organization discovery is optional. Personal repositories
                    // remain usable even when this supplementary request fails.
                    OrgsLoaded = true;
                    WarningMessage = GitHubUtility.BuildRepoListError(
                        "Failed to load GitHub organizations; refresh to retry",
                        organizationsResult);
                }

                orgsHandle = null;
                changed = true;
            }

            if (pendingSearchQuery != null && currentTime >= pendingSearchTime)
            {
                string query = pendingSearchQuery;
                pendingSearchQuery = null;

                if (string.IsNullOrWhiteSpace(query))
                {
                    if (isSearchMode)
                    {
                        LoadInitialPage();
                    }
                }
                else
                {
                    currentPage = 1;
                    isSearchMode = true;
                    currentSearchQuery = query;
                    FetchPage();
                }
            }

            if (pageHandle != null && pageHandle.IsComplete)
            {
                var result = pageHandle.Result;
                bool discardResult = discardActivePageResult;
                pageHandle = null;
                var completedRequest = activePageRequest;
                activePageRequest = null;
                discardActivePageResult = false;

                // A search/owner/page change superseded this response. Start the
                // newest request without flashing stale results in the window.
                if (pendingPageRequest != null)
                {
                    var nextRequest = pendingPageRequest;
                    pendingPageRequest = null;
                    if (!StartPageRequest(nextRequest))
                        pendingPageRequest = nextRequest;
                    return true;
                }

                if (discardResult)
                    return true;

                pendingPackageJsonTarget = null;
                if (result != null && result.IsSuccess && !result.StdOutTruncated)
                {
                    try
                    {
                        string json = (result.StdOut ?? string.Empty).Trim();
                        List<GitHubRepo> repos;

                        if (completedRequest != null && completedRequest.IsSearch)
                        {
                            repos = GitHubUtility.ParseSearchJson(json);
                            int totalCount = GitHubUtility.ParseSearchTotalCount(json);
                            HasNextPage = CanLoadNextSearchPage(completedRequest.Page, totalCount);
                        }
                        else
                        {
                            bool hasPaginationMetadata = TryExtractPaginationMetadata(
                                json,
                                out json,
                                out bool metadataHasNextPage);
                            repos = GitHubUtility.ParseRepoJson(json);
                            HasNextPage = hasPaginationMetadata
                                ? metadataHasNextPage
                                : repos != null && repos.Count == PageSize;
                        }

                        DisplayedRepos = repos ?? new List<GitHubRepo>();
                        ErrorMessage = string.Empty;
                        PageChanged = true;
                        if (validPackageFilterEnabled)
                            SchedulePackageManifestValidation();
                    }
                    catch (Exception ex)
                    {
                        DisplayedRepos = new List<GitHubRepo>();
                        HasNextPage = false;
                        ErrorMessage = BuildMalformedRepositoryDataError(ex);
                        PageChanged = true;
                    }
                }
                else
                {
                    DisplayedRepos = new List<GitHubRepo>();
                    HasNextPage = false;
                    ErrorMessage = GitHubUtility.BuildRepoListError("Failed to load GitHub repositories", result);
                    PageChanged = true;
                }

                changed = true;
            }

            if (packageJsonHandle != null && packageJsonHandle.IsComplete)
            {
                var result = packageJsonHandle.Result;
                var target = packageJsonTarget;
                bool discardResult = discardActivePackageJsonResult;
                packageJsonHandle = null;
                packageJsonTarget = null;
                discardActivePackageJsonResult = false;

                if (!discardResult && target != null)
                {
                    if (result != null && result.IsSuccess && !result.StdOutTruncated)
                        ApplyEncodedPackageManifestResult(target, result.StdOut);
                    else if (GitHubUtility.IsNotFoundResult(result))
                    {
                        SetManifestState(
                            target,
                            PackageManifestState.Missing,
                            "No package.json was found at the repository root.");
                    }
                    else
                    {
                        SetManifestState(
                            target,
                            PackageManifestState.Unavailable,
                            BuildPackageManifestFailureMessage(result));
                    }
                }
                else if (target != null && target.ManifestState == PackageManifestState.Checking)
                {
                    target.ManifestState = PackageManifestState.Unknown;
                }

                GitHubRepo pendingTarget = pendingPackageJsonTarget;
                pendingPackageJsonTarget = null;
                if (pendingTarget != null &&
                    pendingTarget.ManifestState == PackageManifestState.Unknown)
                {
                    if (!StartPackageJsonCheck(pendingTarget))
                        pendingPackageJsonTarget = pendingTarget;
                }

                changed = true;
            }

            if (packageManifestBatchHandle != null && packageManifestBatchHandle.IsComplete)
            {
                CommandResult result = packageManifestBatchHandle.Result;
                PackageManifestBatch batch = activePackageManifestBatch;
                packageManifestBatchHandle = null;
                activePackageManifestBatch = null;

                if (batch != null && batch.Generation == packageManifestValidationGeneration)
                    ProcessPackageManifestBatch(batch, result);

                if (pendingPackageManifestBatches.Count > 0 &&
                    CanStartGitHubCommandNow)
                {
                    StartNextPackageManifestBatch();
                }

                changed = true;
            }

            return changed;
        }

        private void SchedulePackageManifestValidation()
        {
            packageManifestValidationGeneration++;
            pendingPackageManifestBatches.Clear();

            if (!validPackageFilterEnabled || DisplayedRepos == null || DisplayedRepos.Count == 0)
                return;

            int generation = packageManifestValidationGeneration;
            PackageManifestBatch batch = null;
            foreach (GitHubRepo repo in DisplayedRepos)
            {
                if (repo == null || repo.PackageJsonChecked)
                    continue;

                if (ReferenceEquals(repo, packageJsonTarget) &&
                    repo.ManifestState == PackageManifestState.Checking)
                {
                    continue;
                }

                if (repo.ManifestState == PackageManifestState.Unavailable ||
                    repo.ManifestState == PackageManifestState.Checking)
                {
                    repo.ManifestState = PackageManifestState.Unknown;
                    repo.PackageManifestMessage = string.Empty;
                }

                if (!IsValidGitHubNodeId(repo.NodeId))
                {
                    SetManifestState(
                        repo,
                        PackageManifestState.Unavailable,
                        "GitHub did not provide a valid repository identity; refresh the page to retry.");
                    continue;
                }

                repo.ManifestState = PackageManifestState.Checking;
                repo.PackageManifestMessage = string.Empty;
                batch ??= new PackageManifestBatch { Generation = generation };
                batch.Repositories.Add(repo);

                if (batch.Repositories.Count >= PackageManifestBatchSize)
                {
                    pendingPackageManifestBatches.Enqueue(batch);
                    batch = null;
                }
            }

            if (batch != null && batch.Repositories.Count > 0)
                pendingPackageManifestBatches.Enqueue(batch);

            if (packageManifestBatchHandle == null &&
                pendingPackageManifestBatches.Count > 0 &&
                !AsyncCommandDrainRegistry.IsDraining)
            {
                StartNextPackageManifestBatch();
            }
        }

        private bool StartNextPackageManifestBatch()
        {
            if (packageManifestBatchHandle != null || !CanStartGitHubCommandNow)
                return false;

            while (pendingPackageManifestBatches.Count > 0)
            {
                PackageManifestBatch batch = pendingPackageManifestBatches.Dequeue();
                if (batch == null ||
                    batch.Generation != packageManifestValidationGeneration ||
                    batch.Repositories.Count == 0)
                {
                    continue;
                }

                var arguments = new List<string>
                {
                    "api",
                    "graphql",
                    "--hostname",
                    GitHubUtility.GitHubHost,
                    "-f",
                    "query=" + PackageManifestQuery
                };

                foreach (GitHubRepo repo in batch.Repositories)
                {
                    arguments.Add("-f");
                    arguments.Add("ids[]=" + repo.NodeId);
                }

                activePackageManifestBatch = batch;
                packageManifestBatchHandle = CliCommandRunner.RunAsync(
                    "gh",
                    arguments,
                    GitUtility.ProjectRoot);
                return true;
            }

            return false;
        }

        private void ProcessPackageManifestBatch(PackageManifestBatch batch, CommandResult result)
        {
            if (result == null || !result.IsSuccess)
            {
                MarkManifestBatchUnavailable(batch, BuildPackageManifestFailureMessage(result));
                StopCurrentManifestValidationAfterFailure(batch.Generation);
                return;
            }

            if (result.StdOutTruncated)
            {
                MarkManifestBatchUnavailable(
                    batch,
                    "GitHub returned more package manifest data than can be safely inspected. " +
                    "Refresh to retry; unusually large manifests are not accepted by this filter.");
                StopCurrentManifestValidationAfterFailure(batch.Generation);
                return;
            }

            PackageManifestGraphQlResponse response;
            try
            {
                string json = (result.StdOut ?? string.Empty).Trim();
                if (json.Length < 2 || json[0] != '{' || json[json.Length - 1] != '}')
                    throw new FormatException("The response was not a JSON object.");

                response = JsonUtility.FromJson<PackageManifestGraphQlResponse>(json);
            }
            catch (Exception exception)
            {
                MarkManifestBatchUnavailable(
                    batch,
                    "GitHub returned malformed package validation data: " +
                    GitHubUtility.SanitizeUiDiagnostic(exception.Message));
                StopCurrentManifestValidationAfterFailure(batch.Generation);
                return;
            }

            bool hasGraphQlErrors = response?.errors != null && response.errors.Length > 0;
            var expectedByNodeId = new Dictionary<string, List<GitHubRepo>>(StringComparer.Ordinal);
            foreach (GitHubRepo repo in batch.Repositories)
            {
                if (!expectedByNodeId.TryGetValue(repo.NodeId, out List<GitHubRepo> matchingRepos))
                {
                    matchingRepos = new List<GitHubRepo>();
                    expectedByNodeId.Add(repo.NodeId, matchingRepos);
                }
                matchingRepos.Add(repo);
            }

            var processed = new HashSet<GitHubRepo>();
            PackageManifestNode[] nodes = response?.data?.nodes;
            if (nodes != null)
            {
                foreach (PackageManifestNode node in nodes)
                {
                    if (node == null || string.IsNullOrEmpty(node.id) ||
                        !expectedByNodeId.TryGetValue(node.id, out List<GitHubRepo> matchingRepos))
                    {
                        continue;
                    }

                    foreach (GitHubRepo repo in matchingRepos)
                    {
                        ApplyPackageManifestNode(repo, node, hasGraphQlErrors);
                        processed.Add(repo);
                    }
                }
            }

            foreach (GitHubRepo repo in batch.Repositories)
            {
                if (!processed.Contains(repo))
                {
                    SetManifestState(
                        repo,
                        PackageManifestState.Unavailable,
                        "GitHub did not return package manifest data for this repository. Refresh to retry.");
                }
            }

            PackageManifestRateLimit rateLimit = response?.data?.rateLimit;
            if (rateLimit != null && rateLimit.remaining <= 0 && pendingPackageManifestBatches.Count > 0)
            {
                string resetNotice = string.IsNullOrWhiteSpace(rateLimit.resetAt)
                    ? string.Empty
                    : " The limit resets at " + rateLimit.resetAt + ".";
                MarkPendingManifestBatchesUnavailable(
                    batch.Generation,
                    "GitHub API rate limit reached; package validation was paused." + resetNotice);
            }
        }

        private void ApplyPackageManifestNode(
            GitHubRepo repo,
            PackageManifestNode node,
            bool responseHasErrors)
        {
            PackageManifestBlob blob = node.packageManifest;
            // JsonUtility materializes a default nested object for a JSON null
            // on some Unity versions, so an absent typename is also a missing
            // Git object when GraphQL reported no error.
            if (blob == null || string.IsNullOrEmpty(blob.__typename))
            {
                SetManifestState(
                    repo,
                    responseHasErrors ? PackageManifestState.Unavailable : PackageManifestState.Missing,
                    responseHasErrors
                        ? "GitHub could not determine whether package.json exists. Refresh to retry."
                        : "No package.json was found at the repository root.");
                return;
            }

            if (!string.Equals(blob.__typename, "Blob", StringComparison.Ordinal))
            {
                SetManifestState(
                    repo,
                    PackageManifestState.Invalid,
                    "The root package.json path is not a regular file.");
                return;
            }

            if (blob.isBinary)
            {
                SetManifestState(repo, PackageManifestState.Invalid, "package.json is not a UTF-8 text file.");
                return;
            }

            if (blob.isTruncated)
            {
                SetManifestState(repo, PackageManifestState.Invalid, "package.json could not be read completely.");
                return;
            }

            if (blob.byteSize < 0 || blob.byteSize > MaximumPackageManifestBytes)
            {
                SetManifestState(
                    repo,
                    PackageManifestState.Invalid,
                    $"package.json exceeds the {MaximumPackageManifestBytes / 1024} KiB validation limit.");
                return;
            }

            if (!IsValidGitObjectId(blob.oid))
            {
                SetManifestState(
                    repo,
                    PackageManifestState.Unavailable,
                    "GitHub returned an invalid package manifest identity. Refresh to retry.");
                return;
            }

            repo.PackageManifestBlobOid = blob.oid;
            if (packageManifestCache.TryGetValue(blob.oid, out PackageManifestCacheEntry cached))
            {
                SetManifestState(repo, cached.State, cached.Message, cached.PackageName, blob.oid);
                return;
            }

            if (blob.text == null)
            {
                SetManifestState(
                    repo,
                    PackageManifestState.Unavailable,
                    "GitHub did not return readable package.json content. Refresh to retry.");
                return;
            }

            if (GitUtility.TryReadValidPackageManifestFromJson(
                    blob.text,
                    out string packageName,
                    out string validationError))
            {
                SetManifestState(repo, PackageManifestState.Valid, string.Empty, packageName, blob.oid);
                CacheManifestResult(blob.oid, repo);
                return;
            }

            SetManifestState(repo, PackageManifestState.Invalid, validationError, string.Empty, blob.oid);
            CacheManifestResult(blob.oid, repo);
        }

        private void ApplyEncodedPackageManifestResult(GitHubRepo repo, string encodedContent)
        {
            try
            {
                byte[] bytes = Convert.FromBase64String(encodedContent ?? string.Empty);
                if (bytes.Length > MaximumPackageManifestBytes)
                {
                    SetManifestState(
                        repo,
                        PackageManifestState.Invalid,
                        $"package.json exceeds the {MaximumPackageManifestBytes / 1024} KiB validation limit.");
                    return;
                }

                string content = new UTF8Encoding(false, true).GetString(bytes);
                if (GitUtility.TryReadValidPackageManifestFromJson(
                        content,
                        out string packageName,
                        out string validationError))
                {
                    SetManifestState(repo, PackageManifestState.Valid, string.Empty, packageName);
                }
                else
                {
                    SetManifestState(repo, PackageManifestState.Invalid, validationError);
                }
            }
            catch (FormatException)
            {
                SetManifestState(
                    repo,
                    PackageManifestState.Unavailable,
                    "GitHub returned malformed package.json content. Refresh to retry.");
            }
            catch (DecoderFallbackException)
            {
                SetManifestState(repo, PackageManifestState.Invalid, "package.json is not valid UTF-8 text.");
            }
        }

        private void CacheManifestResult(string oid, GitHubRepo repo)
        {
            if (string.IsNullOrEmpty(oid) || repo == null)
                return;

            if (packageManifestCache.Count >= MaximumManifestCacheEntries)
                packageManifestCache.Clear();

            packageManifestCache[oid] = new PackageManifestCacheEntry
            {
                State = repo.ManifestState,
                PackageName = repo.DeclaredPackageName,
                Message = repo.PackageManifestMessage
            };
        }

        private static void SetManifestState(
            GitHubRepo repo,
            PackageManifestState state,
            string message,
            string packageName = "",
            string blobOid = "")
        {
            if (repo == null)
                return;

            repo.ManifestState = state;
            repo.PackageManifestMessage = message ?? string.Empty;
            repo.DeclaredPackageName = packageName ?? string.Empty;
            if (!string.IsNullOrEmpty(blobOid))
                repo.PackageManifestBlobOid = blobOid;
        }

        private static bool IsValidGitHubNodeId(string nodeId)
        {
            if (string.IsNullOrEmpty(nodeId) || nodeId.Length > 256)
                return false;

            foreach (char character in nodeId)
            {
                if (char.IsControl(character) || char.IsWhiteSpace(character))
                    return false;
            }

            return true;
        }

        private static bool IsValidGitObjectId(string objectId)
        {
            if (string.IsNullOrEmpty(objectId) ||
                objectId.Length != 40 && objectId.Length != 64)
            {
                return false;
            }

            foreach (char character in objectId)
            {
                bool isHex = character >= '0' && character <= '9' ||
                             character >= 'a' && character <= 'f' ||
                             character >= 'A' && character <= 'F';
                if (!isHex)
                    return false;
            }

            return true;
        }

        private void MarkManifestBatchUnavailable(PackageManifestBatch batch, string message)
        {
            if (batch == null)
                return;

            foreach (GitHubRepo repo in batch.Repositories)
            {
                if (repo.ManifestState == PackageManifestState.Checking)
                    SetManifestState(repo, PackageManifestState.Unavailable, message);
            }
        }

        private void MarkPendingManifestBatchesUnavailable(int generation, string message)
        {
            while (pendingPackageManifestBatches.Count > 0)
            {
                PackageManifestBatch pending = pendingPackageManifestBatches.Dequeue();
                if (pending.Generation == generation)
                    MarkManifestBatchUnavailable(pending, message);
            }
        }

        private void StopCurrentManifestValidationAfterFailure(int generation)
        {
            MarkPendingManifestBatchesUnavailable(
                generation,
                "Package validation stopped because a GitHub validation response could not be used. " +
                "Refresh the page to retry.");
        }

        private static string BuildPackageManifestFailureMessage(CommandResult result)
        {
            const string summary = "Could not validate package.json through GitHub.";
            if (result == null || string.IsNullOrWhiteSpace(result.StdErr))
                return summary + " Refresh the page to retry.";

            string detail = GitHubUtility.SanitizeUiDiagnostic(result.StdErr);
            return string.IsNullOrWhiteSpace(detail)
                ? summary + " Refresh the page to retry."
                : summary + " " + detail;
        }

        private int CountPackageManifestStates(PackageManifestState state)
        {
            int count = 0;
            foreach (GitHubRepo repo in DisplayedRepos)
            {
                if (repo != null && repo.ManifestState == state)
                    count++;
            }
            return count;
        }

        internal static bool TryExtractPaginationMetadata(
            string response,
            out string jsonBody,
            out bool hasNextPage)
        {
            jsonBody = (response ?? string.Empty).Trim();
            hasNextPage = false;

            string remaining = jsonBody;
            string finalHeaders = null;
            while (remaining.StartsWith("HTTP/", StringComparison.OrdinalIgnoreCase))
            {
                int separatorIndex = remaining.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                int separatorLength = 4;
                if (separatorIndex < 0)
                {
                    separatorIndex = remaining.IndexOf("\n\n", StringComparison.Ordinal);
                    separatorLength = 2;
                }

                if (separatorIndex < 0)
                    return false;

                finalHeaders = remaining.Substring(0, separatorIndex);
                remaining = remaining.Substring(separatorIndex + separatorLength).TrimStart();
            }

            if (finalHeaders == null)
                return false;

            jsonBody = remaining.Trim();
            foreach (string line in finalHeaders.Split('\n'))
            {
                string header = line.Trim();
                if (!header.StartsWith("Link:", StringComparison.OrdinalIgnoreCase))
                    continue;

                hasNextPage = header.IndexOf("rel=\"next\"", StringComparison.OrdinalIgnoreCase) >= 0 ||
                              header.IndexOf("rel=next", StringComparison.OrdinalIgnoreCase) >= 0;
                break;
            }

            return true;
        }

        private void FetchPage()
        {
            if (packageJsonHandle != null)
                discardActivePackageJsonResult = true;
            pendingPackageJsonTarget = null;
            packageManifestValidationGeneration++;
            pendingPackageManifestBatches.Clear();

            if (isSearchMode &&
                !string.IsNullOrWhiteSpace(currentSearchQuery) &&
                string.IsNullOrWhiteSpace(selectedOwner))
            {
                // Search without an owner qualifier would become a global GitHub
                // search. Resolve the authenticated account first and fail closed
                // if that request cannot provide a trustworthy owner.
                pageFetchDeferredUntilOwnerKnown = true;
                EnsureUsername();
                return;
            }

            pageFetchDeferredUntilOwnerKnown = false;

            string owner = selectedOwner;
            bool isOwnRepos = string.IsNullOrEmpty(owner) ||
                string.Equals(owner, cachedUsername, StringComparison.OrdinalIgnoreCase);

            string args;
            if (isSearchMode && !string.IsNullOrWhiteSpace(currentSearchQuery))
            {
                string qualifier = !string.IsNullOrEmpty(owner)
                    ? (isOwnRepos ? $"user:{owner}+" : $"org:{owner}+")
                    : string.Empty;
                string encoded = Uri.EscapeDataString(currentSearchQuery);
                args = GitHubUtility.BuildApiArguments(
                    $"\"search/repositories?q={qualifier}{encoded}&per_page={PageSize}&page={currentPage}\" " +
                    $"--jq {GitUtility.Quote(RepositorySearchProjection)}");
            }
            else if (isOwnRepos)
            {
                args = GitHubUtility.BuildApiArguments(
                    $"user/repos?affiliation=owner&sort=updated&direction=desc&per_page={PageSize}&page={currentPage} --include " +
                    $"--jq {GitUtility.Quote(RepositoryListProjection)}");
            }
            else
            {
                args = GitHubUtility.BuildApiArguments(
                    $"orgs/{Uri.EscapeDataString(owner)}/repos?sort=updated&direction=desc&per_page={PageSize}&page={currentPage} --include " +
                    $"--jq {GitUtility.Quote(RepositoryListProjection)}");
            }

            var request = new PageRequest
            {
                Arguments = args,
                IsSearch = isSearchMode,
                Page = currentPage
            };

            // A completed handle still owns an unprocessed result. Queue behind
            // it so Tick can drain the handle instead of overwriting it.
            if (pageHandle != null || AsyncCommandDrainRegistry.IsDraining)
            {
                pendingPageRequest = request;
                return;
            }

            if (!StartPageRequest(request))
                pendingPageRequest = request;
        }

        private bool StartPageRequest(PageRequest request)
        {
            if (request == null || !CanStartGitHubCommandNow)
                return false;

            ErrorMessage = string.Empty;
            activePageRequest = request;
            discardActivePageResult = false;
            pageHandle = CliCommandRunner.RunAsync("gh", request.Arguments, GitUtility.ProjectRoot);
            return true;
        }

        internal static bool CanStartGitHubCommand(
            bool sharedAuthenticationBlocked,
            bool commandsDraining)
        {
            return !sharedAuthenticationBlocked && !commandsDraining;
        }

        private static bool CanStartGitHubCommandNow =>
            CanStartGitHubCommand(
                GitPackageManagerWindow.IsSharedGitHubAuthenticationBlocked,
                AsyncCommandDrainRegistry.IsDraining ||
                CliCommandRunner.GitHubCommandRequiresEditorRestart);

        internal static bool CanLoadNextSearchPage(int currentPage, int reportedTotalCount)
        {
            if (currentPage < 1 || reportedTotalCount <= 0)
                return false;

            int accessibleResultCount = Math.Min(reportedTotalCount, MaximumSearchResults);
            return (long)currentPage * PageSize < accessibleResultCount;
        }

        internal static string BuildMalformedRepositoryDataError(Exception exception)
        {
            const string summary = "GitHub returned malformed repository data";
            string detail = GitHubUtility.SanitizeUiDiagnostic(exception?.Message);
            return GitHubUtility.SanitizeUiDiagnostic(
                string.IsNullOrWhiteSpace(detail) ? summary + "." : summary + ": " + detail);
        }

        internal void ResetGitHubIdentityState()
        {
            pendingPageRequest = null;
            pendingPackageJsonTarget = null;
            pageFetchDeferredUntilOwnerKnown = false;

            // A reset can happen while an earlier account still owns processes.
            // Retire every handle before dropping it so a replacement account
            // cannot start work until those commands have stopped safely. Once
            // detached here, their results can no longer publish into this state.
            AsyncCommandDrainRegistry.Retire(usernameHandle);
            AsyncCommandDrainRegistry.Retire(orgsHandle);
            AsyncCommandDrainRegistry.Retire(pageHandle);
            AsyncCommandDrainRegistry.Retire(packageJsonHandle);
            AsyncCommandDrainRegistry.Retire(packageManifestBatchHandle);
            usernameHandle = null;
            orgsHandle = null;
            pageHandle = null;
            packageJsonHandle = null;
            packageManifestBatchHandle = null;
            activePageRequest = null;
            packageJsonTarget = null;
            activePackageManifestBatch = null;
            discardActivePageResult = false;
            discardActivePackageJsonResult = false;
            usernameRequested = false;
            organizationsRequested = false;

            packageManifestValidationGeneration++;
            pendingPackageManifestBatches.Clear();
            packageManifestCache.Clear();

            DisplayedRepos = new List<GitHubRepo>();
            cachedUsername = string.Empty;
            selectedOwner = string.Empty;
            Organizations = new List<string>();
            OrgsLoaded = false;
            currentPage = 1;
            HasNextPage = false;
            PageChanged = true;
            isSearchMode = false;
            currentSearchQuery = string.Empty;
            pendingSearchQuery = null;
            pendingSearchTime = 0d;
            ErrorMessage = string.Empty;
            WarningMessage = string.Empty;
        }

        public void Dispose()
        {
            ResetGitHubIdentityState();
            validPackageFilterEnabled = false;
        }
    }
}
