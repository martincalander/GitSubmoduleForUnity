using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace MartinCalander.GitSubmoduleManager.Editor
{
    internal sealed class DiscoveryCoordinator : IDisposable
    {
        private const int PageSize = 50;
        private const int InitialPackageManifestBatchSize = PageSize;
        internal const int MaximumPackageManifestRequestsPerValidation = 32;
        private const int MaximumPackageManifestBytes = 64 * 1024;
        private const int MaximumPackageManifestMetaBytes = 16 * 1024;
        private const int MaximumManifestCacheEntries = 2048;
        private const int MaximumPackageManifestResponseDepth = 32;
        private static readonly UTF8Encoding StrictUtf8Encoding =
            new(false, true);
        private const string PackageManifestRequestBudgetExhaustedMessage =
            "Package validation stopped after reaching the bounded GitHub request limit " +
            "for this page. Refresh the page to retry.";
        private const string RepositoryListProjection =
            "[.[] | {node_id, name, owner: {login: .owner.login}, clone_url, html_url, default_branch, private, description, updated_at}]";
        private const string PackageManifestQuery =
            "query($ids: [ID!]!) { nodes(ids: $ids) { ... on Repository { id defaultBranchRef { target { __typename oid ... on Commit { packageManifest: file(path: \"package.json\") { name mode type object { __typename oid ... on Blob { byteSize isBinary isTruncated text } } } packageManifestMeta: file(path: \"package.json.meta\") { name mode type object { __typename oid ... on Blob { byteSize isBinary isTruncated text } } } } } } } } rateLimit { remaining resetAt } }";

        [Serializable]
        private sealed class PackageManifestGraphQlResponse
        {
            public PackageManifestGraphQlData data;
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
            public PackageManifestDefaultBranchRef defaultBranchRef;
        }

        [Serializable]
        private sealed class PackageManifestDefaultBranchRef
        {
            public PackageManifestCommit target;
        }

        [Serializable]
        private sealed class PackageManifestCommit
        {
            public string __typename;
            public string oid;
            public PackageManifestTreeEntry packageManifest;
            public PackageManifestTreeEntry packageManifestMeta;
        }

        [Serializable]
        private sealed class PackageManifestTreeEntry
        {
            public string name;
            public int mode;
            public string type;
            public PackageManifestBlob @object;
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
            public int remaining;
            public string resetAt;
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
            public string DisplayName;
            public string Version;
            public string Description;
            public string MinimumUnityVersion;
            public string AuthorName;
            public string License;
            public string DocumentationUrl;
            public string ChangelogUrl;
            public string LicensesUrl;
            public PackageManifestDependency[] Dependencies;
            public string Message;
            public string PackageManifestMetaGuid;
        }

        private sealed class PageRequest
        {
            public string Arguments;
        }

        private string cachedUsername = string.Empty;
        private string cachedAccountId = string.Empty;
        private AsyncCommandHandle usernameHandle;
        private AsyncCommandHandle orgsHandle;
        private bool usernameRequested;
        private bool organizationsRequested;

        private AsyncCommandHandle pageHandle;
        private PageRequest pendingPageRequest;
        private int currentPage = 1;
        private string selectedOwner = string.Empty;

        private readonly Queue<PackageManifestBatch> pendingPackageManifestBatches = new();
        private readonly Dictionary<string, PackageManifestCacheEntry> packageManifestCache =
            new(StringComparer.Ordinal);
        private AsyncCommandHandle packageManifestBatchHandle;
        private PackageManifestBatch activePackageManifestBatch;
        private int packageManifestValidationGeneration;
        private int packageManifestRequestCount;

        internal bool IsLoading => pageHandle != null && !pageHandle.IsComplete;
        internal bool HasIncompleteCommands =>
            IsIncomplete(usernameHandle) ||
            IsIncomplete(orgsHandle) ||
            IsIncomplete(pageHandle) ||
            IsIncomplete(packageManifestBatchHandle);
        internal int CurrentPage => currentPage;
        internal string StatusMessage => pageHandle?.StatusMessage ?? string.Empty;
        internal string ErrorMessage { get; private set; } = string.Empty;
        internal string WarningMessage { get; private set; } = string.Empty;

        internal List<GitHubRepo> DisplayedRepos { get; private set; } = new();
        internal bool HasNextPage { get; private set; }
        internal bool PageChanged { get; private set; }
        internal bool IsValidatingPackageManifests =>
            packageManifestBatchHandle != null ||
            pendingPackageManifestBatches.Count > 0;
        internal int PackageManifestCheckTotal => DisplayedRepos.Count;
        internal int PackageManifestCheckCompleted =>
            CountPackageManifestStates(PackageManifestState.Valid) +
            CountPackageManifestStates(PackageManifestState.Missing) +
            CountPackageManifestStates(PackageManifestState.Invalid) +
            CountPackageManifestStates(PackageManifestState.Unavailable);
        internal int PackageManifestUnavailableCount =>
            CountPackageManifestStates(PackageManifestState.Unavailable);

        private static bool IsIncomplete(AsyncCommandHandle handle)
        {
            return handle != null && !handle.IsComplete;
        }

        internal string Username => cachedUsername;
        internal string AccountId => cachedAccountId;
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

        internal bool TryUseVerifiedAccountIdentity(
            string accountId,
            string accountLogin)
        {
            if (!GitHubUtility.TryNormalizeAccountIdentity(
                    accountId,
                    accountLogin,
                    out string normalizedAccountId,
                    out string normalizedAccountLogin))
            {
                return false;
            }

            cachedAccountId = normalizedAccountId;
            cachedUsername = normalizedAccountLogin;
            selectedOwner = normalizedAccountLogin;
            usernameRequested = false;
            if (!OrgsLoaded)
                organizationsRequested = true;
            return true;
        }

        private bool TryStartUsernameRequest()
        {
            if (!usernameRequested || usernameHandle != null || !CanStartGitHubCommandNow)
                return false;

            AsyncCommandHandle handle = CliCommandRunner.RunAsync(
                "gh",
                GitHubUtility.BuildAccountIdentityArguments(),
                GitUtility.ProjectRoot,
                requireStrictUtf8StdOut: true);
            usernameHandle = handle;
            usernameRequested = false;
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

            AsyncCommandHandle handle = CliCommandRunner.RunAsync(
                "gh",
                GitHubUtility.BuildApiArguments("user/orgs --paginate --jq \".[].login\""),
                GitUtility.ProjectRoot);
            orgsHandle = handle;
            organizationsRequested = false;
            return true;
        }

        internal void SetOwner(string owner)
        {
            if (string.Equals(selectedOwner, owner, StringComparison.OrdinalIgnoreCase))
                return;

            selectedOwner = owner ?? string.Empty;
            LoadInitialPage();
        }

        internal void LoadInitialPage()
        {
            currentPage = 1;
            FetchPage();
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

        internal bool Tick()
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
                bool usernameResolved =
                    GitHubUtility.TryReadCompleteAccountIdentity(
                        usernameResult,
                        out cachedAccountId,
                        out cachedUsername);
                if (usernameResolved && string.IsNullOrEmpty(selectedOwner))
                    selectedOwner = cachedUsername;

                usernameHandle = null;

                if (usernameResolved)
                {
                    if (!OrgsLoaded)
                        organizationsRequested = true;
                    TryStartOrganizationsRequest();
                }
                else
                {
                    ErrorMessage = GitHubUtility.BuildRepoListError(
                        "Could not identify the authenticated GitHub account; GitHub discovery could not continue",
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

            if (pageHandle != null && pageHandle.IsComplete)
            {
                var result = pageHandle.Result;
                pageHandle = null;

                // A queued owner/page request superseded this response. Start
                // the newest request without publishing stale results.
                if (pendingPageRequest != null)
                {
                    var nextRequest = pendingPageRequest;
                    pendingPageRequest = null;
                    if (!StartPageRequest(nextRequest))
                        pendingPageRequest = nextRequest;
                    return true;
                }

                if (result != null && result.IsSuccess && !result.StdOutTruncated)
                {
                    try
                    {
                        string json = (result.StdOut ?? string.Empty).Trim();
                        bool hasPaginationMetadata = TryExtractPaginationMetadata(
                            json,
                            out json,
                            out bool metadataHasNextPage);
                        List<GitHubRepo> repos = GitHubUtility.ParseRepoJson(json);
                        HasNextPage = hasPaginationMetadata
                            ? metadataHasNextPage
                            : repos != null && repos.Count == PageSize;

                        DisplayedRepos = repos ?? new List<GitHubRepo>();
                        ErrorMessage = string.Empty;
                        PageChanged = true;
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
            packageManifestRequestCount = 0;

            if (DisplayedRepos == null || DisplayedRepos.Count == 0)
                return;

            int generation = packageManifestValidationGeneration;
            PackageManifestBatch batch = null;
            foreach (GitHubRepo repo in DisplayedRepos)
            {
                if (repo == null || repo.PackageJsonChecked)
                    continue;

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

                if (batch.Repositories.Count >= InitialPackageManifestBatchSize)
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

                if (packageManifestRequestCount >=
                    MaximumPackageManifestRequestsPerValidation)
                {
                    MarkManifestBatchUnavailable(
                        batch,
                        PackageManifestRequestBudgetExhaustedMessage);
                    MarkPendingManifestBatchesUnavailable(
                        batch.Generation,
                        PackageManifestRequestBudgetExhaustedMessage);
                    return false;
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
                packageManifestRequestCount++;
                packageManifestBatchHandle = CliCommandRunner.RunAsync(
                    "gh",
                    arguments,
                    GitUtility.ProjectRoot,
                    requireStrictUtf8StdOut: true);
                return true;
            }

            return false;
        }

        private void ProcessPackageManifestBatch(PackageManifestBatch batch, CommandResult result)
        {
            if (result != null && result.StdOutTruncated)
            {
                if (result.IsSuccess && batch.Repositories.Count > 1)
                {
                    SplitAndPrependPackageManifestBatch(batch);
                    return;
                }

                MarkManifestBatchUnavailable(
                    batch,
                    "GitHub returned more package manifest data than can be safely inspected. " +
                    "Refresh to retry; unusually large manifests are not accepted by this filter.");
                if (!result.IsSuccess)
                    StopCurrentManifestValidationAfterFailure(batch.Generation);
                return;
            }

            if (!TryReadPackageManifestResponse(
                    batch,
                    result,
                    out PackageManifestGraphQlResponse response,
                    out string responseError))
            {
                MarkManifestBatchUnavailable(batch, responseError);
                StopCurrentManifestValidationAfterFailure(batch.Generation);
                return;
            }

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
                        ApplyPackageManifestNode(repo, node);
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

        private static bool TryReadPackageManifestResponse(
            PackageManifestBatch batch,
            CommandResult result,
            out PackageManifestGraphQlResponse response,
            out string error)
        {
            response = null;
            error = string.Empty;

            if (result == null)
            {
                error = BuildPackageManifestFailureMessage(null);
                return false;
            }

            if (result.StdOutInvalidUtf8)
            {
                error =
                    "GitHub returned package validation data that was not valid UTF-8. " +
                    "Refresh the page to retry.";
                return false;
            }

            if (result.TimedOut ||
                result.Cancelled ||
                !result.TerminationConfirmed ||
                result.BlockedByGitHubAuthentication ||
                result.StdOutTruncated ||
                result.StdErrTruncated ||
                !string.IsNullOrWhiteSpace(result.CompletionWarning))
            {
                error = BuildPackageManifestFailureMessage(result);
                return false;
            }

            bool mayBeExpectedPartialResponse =
                result.ExitCode == 1;
            if (!result.IsSuccess && !mayBeExpectedPartialResponse)
            {
                error = BuildPackageManifestFailureMessage(result);
                return false;
            }

            string json = (result.StdOut ?? string.Empty).Trim();
            if (json.Length < 2 || json[0] != '{' || json[json.Length - 1] != '}')
            {
                error =
                    "GitHub returned malformed package validation data: " +
                    "the response was not a JSON object.";
                return false;
            }

            JObject root;
            try
            {
                using (var stringReader = new StringReader(json))
                using (var jsonReader = new JsonTextReader(stringReader)
                       {
                           DateParseHandling = DateParseHandling.None,
                           MaxDepth = MaximumPackageManifestResponseDepth
                       })
                {
                    root = JObject.Load(
                        jsonReader,
                        new JsonLoadSettings
                        {
                            CommentHandling = CommentHandling.Ignore,
                            DuplicatePropertyNameHandling =
                                DuplicatePropertyNameHandling.Error,
                            LineInfoHandling = LineInfoHandling.Ignore
                        });

                    while (jsonReader.Read())
                    {
                        if (jsonReader.TokenType != JsonToken.Comment)
                        {
                            error =
                                "GitHub returned malformed package validation data: " +
                                "the response contained content after its root object.";
                            return false;
                        }
                    }
                }
            }
            catch (Exception exception)
            {
                error =
                    "GitHub returned malformed package validation data: " +
                    GitHubUtility.SanitizeUiDiagnostic(exception.Message);
                return false;
            }

            if (!(root?["data"] is JObject data) ||
                !(data["nodes"] is JArray nodes) ||
                nodes.Count != batch.Repositories.Count ||
                !(data["rateLimit"] is JObject rateLimit) ||
                rateLimit["remaining"]?.Type != JTokenType.Integer ||
                !IsRepresentableNonNegativeInt(rateLimit["remaining"]) ||
                rateLimit["resetAt"] == null ||
                rateLimit["resetAt"].Type != JTokenType.String &&
                rateLimit["resetAt"].Type != JTokenType.Null)
            {
                error =
                    "GitHub returned malformed package validation data: " +
                    "the response envelope was incomplete.";
                return false;
            }

            if (!TryValidatePackageManifestErrors(
                    root,
                    nodes,
                    batch,
                    out bool hasExpectedMissingFileErrors))
            {
                error =
                    "GitHub returned package validation errors that could not be " +
                    "safely matched to missing root package files. Refresh the page to retry.";
                return false;
            }

            if (!result.IsSuccess && !hasExpectedMissingFileErrors)
            {
                error = BuildPackageManifestFailureMessage(result);
                return false;
            }

            try
            {
                response = JsonUtility.FromJson<PackageManifestGraphQlResponse>(
                    root.ToString(Formatting.None));
            }
            catch (Exception exception)
            {
                error =
                    "GitHub returned malformed package validation data: " +
                    GitHubUtility.SanitizeUiDiagnostic(exception.Message);
                return false;
            }

            if (response?.data?.nodes == null ||
                response.data.nodes.Length != nodes.Count)
            {
                response = null;
                error =
                    "GitHub returned malformed package validation data: " +
                    "the repository node array could not be decoded completely.";
                return false;
            }

            return true;
        }

        private static bool TryValidatePackageManifestErrors(
            JObject root,
            JArray nodes,
            PackageManifestBatch batch,
            out bool hasExpectedMissingFileErrors)
        {
            hasExpectedMissingFileErrors = false;
            JToken errorsToken = root?["errors"];
            if (errorsToken == null || errorsToken.Type == JTokenType.Null)
                return true;
            if (!(errorsToken is JArray errors))
                return false;
            if (errors.Count == 0)
                return true;
            if (batch?.Repositories == null ||
                nodes == null ||
                nodes.Count != batch.Repositories.Count)
            {
                return false;
            }

            var reportedMissingFields = new HashSet<string>(StringComparer.Ordinal);
            var returnedMissingFields = new HashSet<string>(StringComparer.Ordinal);
            var expectedNodeIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (GitHubRepo repo in batch.Repositories)
            {
                if (repo == null ||
                    string.IsNullOrEmpty(repo.NodeId) ||
                    !expectedNodeIds.Add(repo.NodeId))
                {
                    return false;
                }
            }

            var returnedNodeIds = new HashSet<string>(StringComparer.Ordinal);
            for (int nodeIndex = 0; nodeIndex < nodes.Count; nodeIndex++)
            {
                if (!(nodes[nodeIndex] is JObject node) ||
                    node["id"]?.Type != JTokenType.String)
                {
                    return false;
                }

                string nodeId = node["id"].Value<string>();
                if (!expectedNodeIds.Contains(nodeId) ||
                    !returnedNodeIds.Add(nodeId))
                {
                    return false;
                }

                JProperty defaultBranchProperty = node.Property(
                    "defaultBranchRef",
                    StringComparison.Ordinal);
                if (defaultBranchProperty == null)
                    return false;
                if (defaultBranchProperty.Value.Type == JTokenType.Null)
                    continue;
                if (!(defaultBranchProperty.Value is JObject defaultBranchRef))
                    return false;

                JProperty targetProperty = defaultBranchRef.Property(
                    "target",
                    StringComparison.Ordinal);
                if (targetProperty == null)
                    return false;
                if (targetProperty.Value.Type == JTokenType.Null)
                    continue;
                if (!(targetProperty.Value is JObject target))
                    return false;
                if (!IsExactJsonString(target["__typename"], "Commit"))
                    continue;

                foreach (string fieldName in new[]
                         {
                             "packageManifest",
                             "packageManifestMeta"
                         })
                {
                    JProperty field = target.Property(
                        fieldName,
                        StringComparison.Ordinal);
                    if (field == null)
                        return false;
                    if (field.Value.Type == JTokenType.Null)
                    {
                        returnedMissingFields.Add(
                            BuildMissingFieldKey(nodeIndex, fieldName));
                    }
                }
            }

            if (!returnedNodeIds.SetEquals(expectedNodeIds))
                return false;

            foreach (JToken errorToken in errors)
            {
                if (!(errorToken is JObject graphQlError) ||
                    !IsExactJsonString(graphQlError["type"], "NOT_FOUND") ||
                    !(graphQlError["path"] is JArray path) ||
                    path.Count != 5 ||
                    !IsExactJsonString(path[0], "nodes") ||
                    path[1].Type != JTokenType.Integer ||
                    !IsExactJsonString(path[2], "defaultBranchRef") ||
                    !IsExactJsonString(path[3], "target"))
                {
                    return false;
                }

                long longNodeIndex;
                try
                {
                    longNodeIndex = path[1].Value<long>();
                }
                catch
                {
                    return false;
                }

                if (longNodeIndex < 0 || longNodeIndex >= nodes.Count)
                    return false;

                int nodeIndex = (int)longNodeIndex;
                string fieldName = path[4].Type == JTokenType.String
                    ? path[4].Value<string>()
                    : string.Empty;
                string missingFileName;
                if (string.Equals(
                        fieldName,
                        "packageManifest",
                        StringComparison.Ordinal))
                {
                    missingFileName = "package.json";
                }
                else if (string.Equals(
                             fieldName,
                             "packageManifestMeta",
                             StringComparison.Ordinal))
                {
                    missingFileName = "package.json.meta";
                }
                else
                {
                    return false;
                }

                if (!IsExactJsonString(
                        graphQlError["message"],
                        $"Could not resolve file for path '{missingFileName}'."))
                {
                    return false;
                }

                if (!(nodes[nodeIndex] is JObject node) ||
                    !(node["defaultBranchRef"] is JObject defaultBranchRef) ||
                    !(defaultBranchRef["target"] is JObject target) ||
                    !IsExactJsonString(target["__typename"], "Commit") ||
                    target.Property(fieldName, StringComparison.Ordinal)?.Value.Type !=
                    JTokenType.Null)
                {
                    return false;
                }

                string key = BuildMissingFieldKey(nodeIndex, fieldName);
                if (!reportedMissingFields.Add(key))
                    return false;
            }

            if (!reportedMissingFields.SetEquals(returnedMissingFields))
                return false;

            hasExpectedMissingFileErrors = true;
            return true;
        }

        private static bool IsExactJsonString(JToken token, string expected)
        {
            return token?.Type == JTokenType.String &&
                   string.Equals(
                       token.Value<string>(),
                       expected,
                       StringComparison.Ordinal);
        }

        private static string BuildMissingFieldKey(int nodeIndex, string fieldName)
        {
            return nodeIndex + ":" + fieldName;
        }

        private static bool IsRepresentableNonNegativeInt(JToken token)
        {
            try
            {
                long value = token.Value<long>();
                return value >= 0 && value <= int.MaxValue;
            }
            catch
            {
                return false;
            }
        }

        private void SplitAndPrependPackageManifestBatch(PackageManifestBatch batch)
        {
            int repositoryCount = batch?.Repositories.Count ?? 0;
            if (repositoryCount <= 1)
                return;

            int firstCount = repositoryCount / 2;
            var first = new PackageManifestBatch { Generation = batch.Generation };
            var second = new PackageManifestBatch { Generation = batch.Generation };
            for (int index = 0; index < repositoryCount; index++)
            {
                (index < firstCount ? first : second).Repositories.Add(
                    batch.Repositories[index]);
            }

            // Retry the smaller pieces before unrelated queued work. This keeps
            // the current page's progress deterministic while retaining the
            // bounded-output safety guarantee that motivated the old fixed-size
            // batches. Typical package.json files now need one request per page;
            // unusually large responses are isolated by repeated bisection.
            PackageManifestBatch[] queued = pendingPackageManifestBatches.ToArray();
            pendingPackageManifestBatches.Clear();
            pendingPackageManifestBatches.Enqueue(first);
            pendingPackageManifestBatches.Enqueue(second);
            foreach (PackageManifestBatch pending in queued)
                pendingPackageManifestBatches.Enqueue(pending);
        }

        private void ApplyPackageManifestNode(
            GitHubRepo repo,
            PackageManifestNode node)
        {
            PackageManifestCommit commit = node.defaultBranchRef?.target;
            // JsonUtility materializes default nested objects for JSON null on
            // some Unity versions. An absent typename therefore also means the
            // repository has no default-branch commit when GraphQL reported no
            // error.
            if (commit == null || string.IsNullOrEmpty(commit.__typename))
            {
                SetManifestState(
                    repo,
                    PackageManifestState.Missing,
                    "The repository has no default-branch commit to inspect.");
                return;
            }

            if (!string.Equals(commit.__typename, "Commit", StringComparison.Ordinal) ||
                !IsValidGitObjectId(commit.oid))
            {
                SetManifestState(
                    repo,
                    PackageManifestState.Unavailable,
                    "GitHub returned an invalid default-branch commit identity. Refresh to retry.");
                return;
            }

            repo.PackageManifestCommitOid = commit.oid.ToLowerInvariant();

            if (!TryGetValidatedManifestBlob(
                    repo,
                    commit.packageManifest,
                    "package.json",
                    MaximumPackageManifestBytes,
                    out PackageManifestBlob manifestBlob))
            {
                return;
            }

            if (!TryGetValidatedManifestBlob(
                    repo,
                    commit.packageManifestMeta,
                    "package.json.meta",
                    MaximumPackageManifestMetaBytes,
                    out PackageManifestBlob metaBlob))
            {
                return;
            }

            repo.PackageManifestBlobOid = manifestBlob.oid;
            repo.PackageManifestMetaBlobOid = metaBlob.oid;
            string cacheIdentity = BuildManifestCacheIdentity(
                manifestBlob.oid,
                metaBlob.oid);
            if (packageManifestCache.TryGetValue(
                    cacheIdentity,
                    out PackageManifestCacheEntry cached))
            {
                SetManifestState(
                    repo,
                    cached.State,
                    cached.Message,
                    cached.PackageName,
                    manifestBlob.oid,
                    cached.DisplayName,
                    cached.Version,
                    cached.Description,
                    cached.MinimumUnityVersion,
                    cached.AuthorName,
                    cached.License,
                    cached.DocumentationUrl,
                    cached.ChangelogUrl,
                    cached.LicensesUrl,
                    cached.Dependencies,
                    metaBlob.oid,
                    cached.PackageManifestMetaGuid);
                return;
            }

            if (manifestBlob.text == null)
            {
                SetManifestState(
                    repo,
                    PackageManifestState.Unavailable,
                    "GitHub did not return readable package.json content. Refresh to retry.");
                return;
            }

            if (metaBlob.text == null)
            {
                SetManifestState(
                    repo,
                    PackageManifestState.Unavailable,
                    "GitHub did not return readable package.json.meta content. Refresh to retry.");
                return;
            }

            if (!GitUtility.TryReadValidPackageManifestMetaFromText(
                    metaBlob.text,
                    out string metaGuid,
                    out string metaValidationError))
            {
                SetManifestState(
                    repo,
                    PackageManifestState.Invalid,
                    GitHubUtility.SanitizeUiDiagnostic(metaValidationError),
                    blobOid: manifestBlob.oid,
                    metaBlobOid: metaBlob.oid);
                CacheManifestResult(cacheIdentity, repo);
                return;
            }

            if (GitUtility.TryReadPackageManifestMetadataFromJson(
                    manifestBlob.text,
                    out PackageManifestMetadata metadata,
                    out string validationError))
            {
                SetManifestState(
                    repo,
                    PackageManifestState.Valid,
                    string.Empty,
                    metadata.PackageName,
                    manifestBlob.oid,
                    metadata.DisplayName,
                    metadata.Version,
                    metadata.Description,
                    metadata.MinimumUnityVersion,
                    metadata.AuthorName,
                    metadata.License,
                    metadata.DocumentationUrl,
                    metadata.ChangelogUrl,
                    metadata.LicensesUrl,
                    metadata.Dependencies,
                    metaBlob.oid,
                    metaGuid);
                CacheManifestResult(cacheIdentity, repo);
                return;
            }

            SetManifestState(
                repo,
                PackageManifestState.Invalid,
                GitHubUtility.SanitizeUiDiagnostic(validationError),
                blobOid: manifestBlob.oid,
                metaBlobOid: metaBlob.oid);
            CacheManifestResult(cacheIdentity, repo);
        }

        private static bool TryGetValidatedManifestBlob(
            GitHubRepo repo,
            PackageManifestTreeEntry entry,
            string fileName,
            int maximumBytes,
            out PackageManifestBlob blob)
        {
            blob = null;
            // JsonUtility can materialize a default object for a JSON null. All
            // entry identity fields being absent is therefore treated as the
            // queried root path being absent.
            if (entry == null ||
                string.IsNullOrEmpty(entry.name) &&
                string.IsNullOrEmpty(entry.type) &&
                entry.mode == 0 &&
                (entry.@object == null || string.IsNullOrEmpty(entry.@object.__typename)))
            {
                SetManifestState(
                    repo,
                    PackageManifestState.Missing,
                    $"No {fileName} was found at the repository root.");
                return false;
            }

            if (!string.Equals(entry.name, fileName, StringComparison.Ordinal) ||
                entry.mode == 0 ||
                string.IsNullOrEmpty(entry.type))
            {
                SetManifestState(
                    repo,
                    PackageManifestState.Unavailable,
                    $"GitHub returned incomplete {fileName} file identity. Refresh to retry.");
                return false;
            }

            blob = entry.@object;
            if (!string.Equals(entry.type, "blob", StringComparison.Ordinal) ||
                !IsRegularGitFileMode(entry.mode) ||
                blob == null ||
                !string.Equals(blob.__typename, "Blob", StringComparison.Ordinal))
            {
                SetManifestState(
                    repo,
                    PackageManifestState.Invalid,
                    $"The root {fileName} path is not a regular file.");
                return false;
            }

            if (blob.isBinary)
            {
                SetManifestState(
                    repo,
                    PackageManifestState.Invalid,
                    $"{fileName} is not a UTF-8 text file.");
                return false;
            }

            if (blob.isTruncated)
            {
                SetManifestState(
                    repo,
                    PackageManifestState.Invalid,
                    $"{fileName} could not be read completely.");
                return false;
            }

            if (blob.text != null)
            {
                int actualByteSize;
                try
                {
                    actualByteSize = StrictUtf8Encoding.GetByteCount(blob.text);
                }
                catch (EncoderFallbackException)
                {
                    SetManifestState(
                        repo,
                        PackageManifestState.Invalid,
                        $"{fileName} is not valid UTF-8 text.");
                    return false;
                }

                if (actualByteSize > maximumBytes)
                {
                    SetManifestState(
                        repo,
                        PackageManifestState.Invalid,
                        $"{fileName} exceeds the {maximumBytes / 1024} KiB validation limit.");
                    return false;
                }
            }

            if (blob.byteSize < 0 || blob.byteSize > maximumBytes)
            {
                SetManifestState(
                    repo,
                    PackageManifestState.Invalid,
                    $"{fileName} exceeds the {maximumBytes / 1024} KiB validation limit.");
                return false;
            }

            if (!IsValidGitObjectId(blob.oid))
            {
                SetManifestState(
                    repo,
                    PackageManifestState.Unavailable,
                    $"GitHub returned an invalid {fileName} identity. Refresh to retry.");
                return false;
            }

            return true;
        }

        private static bool IsRegularGitFileMode(int mode)
        {
            const int regularFileMode = 33188; // 0100644
            const int executableFileMode = 33261; // 0100755
            return mode == regularFileMode || mode == executableFileMode;
        }

        private static string BuildManifestCacheIdentity(
            string manifestOid,
            string metaOid)
        {
            return (manifestOid ?? string.Empty).ToLowerInvariant() + ":" +
                   (metaOid ?? string.Empty).ToLowerInvariant();
        }

        private void CacheManifestResult(string cacheIdentity, GitHubRepo repo)
        {
            if (string.IsNullOrEmpty(cacheIdentity) || repo == null)
                return;

            if (packageManifestCache.Count >= MaximumManifestCacheEntries)
                packageManifestCache.Clear();

            packageManifestCache[cacheIdentity] = new PackageManifestCacheEntry
            {
                State = repo.ManifestState,
                PackageName = repo.DeclaredPackageName,
                DisplayName = repo.DeclaredDisplayName,
                Version = repo.DeclaredVersion,
                Description = repo.DeclaredDescription,
                MinimumUnityVersion = repo.DeclaredMinimumUnityVersion,
                AuthorName = repo.DeclaredAuthorName,
                License = repo.DeclaredLicense,
                DocumentationUrl = repo.DeclaredDocumentationUrl,
                ChangelogUrl = repo.DeclaredChangelogUrl,
                LicensesUrl = repo.DeclaredLicensesUrl,
                Dependencies = CloneDependencies(repo.DeclaredDependencies),
                Message = repo.PackageManifestMessage,
                PackageManifestMetaGuid = repo.PackageManifestMetaGuid
            };
        }

        private static void SetManifestState(
            GitHubRepo repo,
            PackageManifestState state,
            string message,
            string packageName = "",
            string blobOid = "",
            string displayName = "",
            string version = "",
            string description = "",
            string minimumUnityVersion = "",
            string authorName = "",
            string license = "",
            string documentationUrl = "",
            string changelogUrl = "",
            string licensesUrl = "",
            IEnumerable<PackageManifestDependency> dependencies = null,
            string metaBlobOid = "",
            string metaGuid = "")
        {
            if (repo == null)
                return;

            repo.ManifestState = state;
            repo.PackageManifestMessage = message ?? string.Empty;
            repo.DeclaredPackageName = packageName ?? string.Empty;
            repo.DeclaredDisplayName = displayName ?? string.Empty;
            repo.DeclaredVersion = version ?? string.Empty;
            repo.DeclaredDescription = description ?? string.Empty;
            repo.DeclaredMinimumUnityVersion = minimumUnityVersion ?? string.Empty;
            repo.DeclaredAuthorName = authorName ?? string.Empty;
            repo.DeclaredLicense = license ?? string.Empty;
            repo.DeclaredDocumentationUrl = documentationUrl ?? string.Empty;
            repo.DeclaredChangelogUrl = changelogUrl ?? string.Empty;
            repo.DeclaredLicensesUrl = licensesUrl ?? string.Empty;
            repo.DeclaredDependencies = CloneDependencies(dependencies);
            if (!string.IsNullOrEmpty(blobOid))
                repo.PackageManifestBlobOid = blobOid;
            if (!string.IsNullOrEmpty(metaBlobOid))
                repo.PackageManifestMetaBlobOid = metaBlobOid;
            if (!string.IsNullOrEmpty(metaGuid))
                repo.PackageManifestMetaGuid = metaGuid;
        }

        private static PackageManifestDependency[] CloneDependencies(
            IEnumerable<PackageManifestDependency> dependencies)
        {
            if (dependencies == null)
                return Array.Empty<PackageManifestDependency>();

            var copies = new List<PackageManifestDependency>();
            foreach (PackageManifestDependency dependency in dependencies)
            {
                if (dependency != null)
                {
                    copies.Add(new PackageManifestDependency(
                        dependency.Name,
                        dependency.Version));
                }
            }

            return copies.ToArray();
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

            bool hasNonZero = false;
            foreach (char character in objectId)
            {
                bool isHex = character >= '0' && character <= '9' ||
                             character >= 'a' && character <= 'f' ||
                             character >= 'A' && character <= 'F';
                if (!isHex)
                    return false;
                if (character != '0')
                    hasNonZero = true;
            }

            return hasNonZero;
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
            packageManifestValidationGeneration++;
            pendingPackageManifestBatches.Clear();
            packageManifestRequestCount = 0;

            string owner = selectedOwner;
            bool isOwnRepos = string.IsNullOrEmpty(owner) ||
                string.Equals(owner, cachedUsername, StringComparison.OrdinalIgnoreCase);

            string args;
            if (isOwnRepos)
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
                Arguments = args
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
            pageHandle = CliCommandRunner.RunAsync("gh", request.Arguments, GitUtility.ProjectRoot);
            return true;
        }

        internal static bool CanStartGitHubCommand(
            bool sharedAuthenticationBlocked,
            bool commandsDraining)
        {
            return !sharedAuthenticationBlocked && !commandsDraining;
        }

        internal static bool CanStartGitHubCommandNow =>
            CanStartGitHubCommand(
                CliCommandRunner.IsGitHubAuthenticationReserved,
                AsyncCommandDrainRegistry.IsDraining ||
                CliCommandRunner.GitHubCommandRequiresEditorRestart);

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

            // A reset can happen while an earlier account still owns processes.
            // Retire every handle before dropping it so a replacement account
            // cannot start work until those commands have stopped safely. Once
            // detached here, their results can no longer publish into this state.
            AsyncCommandDrainRegistry.Retire(usernameHandle);
            AsyncCommandDrainRegistry.Retire(orgsHandle);
            AsyncCommandDrainRegistry.Retire(pageHandle);
            AsyncCommandDrainRegistry.Retire(packageManifestBatchHandle);
            usernameHandle = null;
            orgsHandle = null;
            pageHandle = null;
            packageManifestBatchHandle = null;
            activePackageManifestBatch = null;
            usernameRequested = false;
            organizationsRequested = false;

            packageManifestValidationGeneration++;
            pendingPackageManifestBatches.Clear();
            packageManifestRequestCount = 0;
            packageManifestCache.Clear();

            DisplayedRepos = new List<GitHubRepo>();
            cachedAccountId = string.Empty;
            cachedUsername = string.Empty;
            selectedOwner = string.Empty;
            Organizations = new List<string>();
            OrgsLoaded = false;
            currentPage = 1;
            HasNextPage = false;
            PageChanged = true;
            ErrorMessage = string.Empty;
            WarningMessage = string.Empty;
        }

        public void Dispose()
        {
            ResetGitHubIdentityState();
        }
    }
}
