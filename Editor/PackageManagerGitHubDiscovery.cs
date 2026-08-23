using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using UnityEditor;

namespace MartinCalander.GitSubmoduleManager.Editor
{
    /// <summary>
    /// Immutable copy of a valid root UPM package discovered on GitHub.
    /// The mutable <see cref="GitHubRepo"/> owned by DiscoveryCoordinator is
    /// deliberately never exposed to Package Manager UI code.
    /// </summary>
    internal sealed class PackageManagerGitHubRepository
    {
        internal PackageManagerGitHubRepository(GitHubRepo repository)
        {
            NodeId = repository?.NodeId ?? string.Empty;
            Name = repository?.Name ?? string.Empty;
            Owner = repository?.Owner ?? string.Empty;
            Url = repository?.Url ?? string.Empty;
            DefaultBranch = repository?.DefaultBranch ?? string.Empty;
            IsPrivate = repository != null && repository.IsPrivate;
            Description = repository?.Description ?? string.Empty;
            UpdatedAt = repository?.UpdatedAt ?? string.Empty;
            PackageName = repository?.DeclaredPackageName ?? string.Empty;
            DisplayName = repository?.DeclaredDisplayName ?? string.Empty;
            Version = repository?.DeclaredVersion ?? string.Empty;
            PackageDescription = repository?.DeclaredDescription ?? string.Empty;
            MinimumUnityVersion =
                repository?.DeclaredMinimumUnityVersion ?? string.Empty;
            AuthorName = repository?.DeclaredAuthorName ?? string.Empty;
            DocumentationUrl = repository?.DeclaredDocumentationUrl ?? string.Empty;
            ChangelogUrl = repository?.DeclaredChangelogUrl ?? string.Empty;
            LicensesUrl = repository?.DeclaredLicensesUrl ?? string.Empty;
            PackageManifestDependency[] dependencyCopies = repository?
                .DeclaredDependencies?
                .Where(dependency => dependency != null)
                .Select(dependency => new PackageManifestDependency(
                    dependency.Name,
                    dependency.Version))
                .ToArray() ?? Array.Empty<PackageManifestDependency>();
            Dependencies = new ReadOnlyCollection<PackageManifestDependency>(
                dependencyCopies);
            PackageManifestBlobOid = repository?.PackageManifestBlobOid ?? string.Empty;
        }

        internal string NodeId { get; }
        internal string Name { get; }
        internal string Owner { get; }
        internal string Url { get; }
        internal string DefaultBranch { get; }
        internal bool IsPrivate { get; }
        internal string Description { get; }
        internal string UpdatedAt { get; }
        internal string PackageName { get; }
        internal string DisplayName { get; }
        internal string Version { get; }
        internal string PackageDescription { get; }
        internal string MinimumUnityVersion { get; }
        internal string AuthorName { get; }
        internal string DocumentationUrl { get; }
        internal string ChangelogUrl { get; }
        internal string LicensesUrl { get; }
        internal IReadOnlyList<PackageManifestDependency> Dependencies { get; }
        internal string PackageManifestBlobOid { get; }

        internal bool HasSameContent(PackageManagerGitHubRepository other)
        {
            if (other == null ||
                !string.Equals(NodeId, other.NodeId, StringComparison.Ordinal) ||
                !string.Equals(Name, other.Name, StringComparison.Ordinal) ||
                !string.Equals(Owner, other.Owner, StringComparison.Ordinal) ||
                !string.Equals(Url, other.Url, StringComparison.Ordinal) ||
                !string.Equals(DefaultBranch, other.DefaultBranch, StringComparison.Ordinal) ||
                IsPrivate != other.IsPrivate ||
                !string.Equals(Description, other.Description, StringComparison.Ordinal) ||
                !string.Equals(UpdatedAt, other.UpdatedAt, StringComparison.Ordinal) ||
                !string.Equals(PackageName, other.PackageName, StringComparison.Ordinal) ||
                !string.Equals(DisplayName, other.DisplayName, StringComparison.Ordinal) ||
                !string.Equals(Version, other.Version, StringComparison.Ordinal) ||
                !string.Equals(PackageDescription, other.PackageDescription, StringComparison.Ordinal) ||
                !string.Equals(MinimumUnityVersion, other.MinimumUnityVersion, StringComparison.Ordinal) ||
                !string.Equals(AuthorName, other.AuthorName, StringComparison.Ordinal) ||
                !string.Equals(DocumentationUrl, other.DocumentationUrl, StringComparison.Ordinal) ||
                !string.Equals(ChangelogUrl, other.ChangelogUrl, StringComparison.Ordinal) ||
                !string.Equals(LicensesUrl, other.LicensesUrl, StringComparison.Ordinal) ||
                !string.Equals(
                    PackageManifestBlobOid,
                    other.PackageManifestBlobOid,
                    StringComparison.Ordinal) ||
                Dependencies.Count != other.Dependencies.Count)
            {
                return false;
            }

            for (int index = 0; index < Dependencies.Count; index++)
            {
                PackageManifestDependency left = Dependencies[index];
                PackageManifestDependency right = other.Dependencies[index];
                if (left == null || right == null ||
                    !string.Equals(left.Name, right.Name, StringComparison.Ordinal) ||
                    !string.Equals(left.Version, right.Version, StringComparison.Ordinal))
                {
                    return false;
                }
            }

            return true;
        }
    }

    /// <summary>
    /// Atomic catalogue state consumed by Package Manager presentation code.
    /// </summary>
    internal sealed class PackageManagerGitHubDiscoverySnapshot
    {
        private static readonly IReadOnlyList<PackageManagerGitHubRepository>
            EmptyRepositories = new ReadOnlyCollection<PackageManagerGitHubRepository>(
                Array.Empty<PackageManagerGitHubRepository>());

        internal static PackageManagerGitHubDiscoverySnapshot Empty { get; } =
            new PackageManagerGitHubDiscoverySnapshot(
                EmptyRepositories,
                false,
                string.Empty,
                string.Empty,
                0,
                0,
                0,
                0,
                0);

        internal PackageManagerGitHubDiscoverySnapshot(
            IReadOnlyList<PackageManagerGitHubRepository> repositories,
            bool isLoading,
            string statusMessage,
            string errorMessage,
            int completedPages,
            int completedOwners,
            int totalOwners,
            int unavailableManifestCount,
            long revision,
            string coverageWarningMessage = "",
            bool isShowingRetainedRepositories = false)
        {
            Repositories = repositories ?? EmptyRepositories;
            IsLoading = isLoading;
            StatusMessage = statusMessage ?? string.Empty;
            ErrorMessage = errorMessage ?? string.Empty;
            CompletedPages = completedPages;
            CompletedOwners = completedOwners;
            TotalOwners = totalOwners;
            UnavailableManifestCount = unavailableManifestCount;
            Revision = revision;
            CoverageWarningMessage = coverageWarningMessage ?? string.Empty;
            IsShowingRetainedRepositories = isShowingRetainedRepositories;
        }

        internal IReadOnlyList<PackageManagerGitHubRepository> Repositories { get; }
        internal bool IsLoading { get; }
        internal string StatusMessage { get; }
        internal string ErrorMessage { get; }
        internal int CompletedPages { get; }
        internal int CompletedOwners { get; }
        internal int TotalOwners { get; }
        internal int UnavailableManifestCount { get; }
        internal long Revision { get; }
        /// <summary>
        /// True while a refresh presents the last completed catalogue instead
        /// of exposing an incomplete replacement catalogue.
        /// </summary>
        internal bool IsShowingRetainedRepositories { get; }

        /// <summary>
        /// A terminal discovery can still be usable for presentation while not
        /// being complete enough to prove that a package is absent. For example,
        /// organization enumeration can fail after personal repositories load.
        /// Dependency resolution treats this as fail-closed coverage metadata.
        /// </summary>
        internal string CoverageWarningMessage { get; }
    }

    /// <summary>
    /// Lazily discovers valid root UPM packages across the authenticated user's
    /// repositories and every organization returned by GitHub CLI. The service
    /// owns exactly one coordinator and publishes immutable page-sized progress.
    /// </summary>
    [InitializeOnLoad]
    internal static class PackageManagerGitHubDiscovery
    {
        // A failed or unusually slow refresh must not make installed packages
        // disappear immediately, but stale repository authorization and
        // metadata must not be presented indefinitely either.
        internal const double RetainedCatalogueDurationSeconds = 15d * 60d;

        private enum CataloguePhase
        {
            Idle,
            PersonalRepositories,
            WaitingForOrganizations,
            OrganizationRepositories,
            Complete,
            Failed
        }

        private static readonly List<PackageManagerGitHubRepository> Repositories = new();
        private static readonly Dictionary<string, int> IndexByNodeId =
            new(StringComparer.Ordinal);
        private static readonly Dictionary<string, int> IndexByRepository =
            new(StringComparer.OrdinalIgnoreCase);
        private static readonly Queue<string> PendingOrganizations = new();
        private static readonly HashSet<string> QueuedOrganizations =
            new(StringComparer.OrdinalIgnoreCase);

        private static DiscoveryCoordinator coordinator;
        private static PackageManagerGitHubDiscoverySnapshot current =
            PackageManagerGitHubDiscoverySnapshot.Empty;
        private static CataloguePhase phase;
        private static string activeOwner = string.Empty;
        private static string statusMessage = string.Empty;
        private static string errorMessage = string.Empty;
        private static string coverageWarningMessage = string.Empty;
        private static bool isStarted;
        private static bool isLoading;
        private static bool awaitingPageResult;
        private static bool pageProcessed;
        private static bool organizationsQueued;
        private static bool isShuttingDown;
        private static bool isStopping;
        private static int completedPages;
        private static int completedOwners;
        private static int totalOwners;
        private static int unavailableManifestCount;
        private static long revision;
        // Downstream projection treats collection identity as the catalogue
        // revision. Status-only snapshots reuse this immutable instance so a
        // spinner/status update cannot trigger a full Package Manager rebuild.
        private static IReadOnlyList<PackageManagerGitHubRepository>
            publishedRepositories = PackageManagerGitHubDiscoverySnapshot.Empty.Repositories;
        private static IReadOnlyList<PackageManagerGitHubRepository>
            lastSuccessfulRepositories = PackageManagerGitHubDiscoverySnapshot.Empty.Repositories;
        private static bool repositoriesDirty;
        private static bool isShowingRetainedRepositories;
        private static double retainedCatalogueExpiresAt;

        internal static event Action SnapshotChanged;

        static PackageManagerGitHubDiscovery()
        {
            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;
            EditorApplication.quitting += OnEditorQuitting;
        }

        internal static PackageManagerGitHubDiscoverySnapshot Current => current;

        internal static IReadOnlyList<PackageManagerGitHubRepository> Results =>
            current.Repositories;

        internal static bool IsLoading => current.IsLoading;

        internal static string StatusMessage => current.StatusMessage;

        internal static string ErrorMessage => current.ErrorMessage;

        internal static bool IsStarted => isStarted;

        internal static void EnsureStarted()
        {
            if (isStarted || isShuttingDown || isStopping)
                return;

            isStarted = true;
            EditorApplication.update += Update;
            BeginRefresh();
        }

        internal static void Refresh()
        {
            if (isShuttingDown || isStopping)
                return;

            if (!isStarted)
            {
                EnsureStarted();
                return;
            }

            BeginRefresh();
        }

        internal static void Dispose()
        {
            if (isStopping)
                return;

            isStopping = true;
            try
            {
                Stop(publishEmptySnapshot: true);
            }
            finally
            {
                isStopping = false;
            }
        }

        private static void BeginRefresh()
        {
            double currentTime = EditorApplication.timeSinceStartup;
            if (isShowingRetainedRepositories &&
                currentTime >= retainedCatalogueExpiresAt)
            {
                isShowingRetainedRepositories = false;
                retainedCatalogueExpiresAt = 0d;
            }

            bool canRetainPublishedCatalogue =
                publishedRepositories.Count > 0 &&
                ReferenceEquals(publishedRepositories, lastSuccessfulRepositories) &&
                (isShowingRetainedRepositories ||
                 (phase == CataloguePhase.Complete && HasCompleteCoverage()));

            if (canRetainPublishedCatalogue)
            {
                if (!isShowingRetainedRepositories ||
                    retainedCatalogueExpiresAt <= currentTime)
                {
                    retainedCatalogueExpiresAt =
                        currentTime + RetainedCatalogueDurationSeconds;
                }

                isShowingRetainedRepositories = true;
            }
            else
            {
                isShowingRetainedRepositories = false;
                retainedCatalogueExpiresAt = 0d;
            }

            DisposeCoordinator();
            ResetAggregation();

            coordinator = new DiscoveryCoordinator();
            coordinator.SetValidPackageFilterEnabled(true);
            coordinator.EnsureUsername();
            coordinator.LoadInitialPage();

            phase = CataloguePhase.PersonalRepositories;
            isLoading = true;
            awaitingPageResult = true;
            pageProcessed = false;
            totalOwners = 1;
            statusMessage = WithRetentionNotice(
                "Loading repositories for the authenticated GitHub account...");
            PublishSnapshot();
        }

        private static void Update()
        {
            Tick(EditorApplication.timeSinceStartup);
        }

        /// <summary>
        /// Kept internal so deterministic EditMode tests can advance the same
        /// state machine without waiting for an Editor repaint loop.
        /// </summary>
        internal static void Tick(double currentTime)
        {
            if (!isStarted || isShuttingDown)
                return;

            if (ReleaseRetainedCatalogueIfExpired(currentTime))
                PublishSnapshot();

            if (!isLoading || coordinator == null)
                return;

            try
            {
                bool changed = coordinator.Tick(currentTime);
                if (coordinator.PageChanged)
                {
                    awaitingPageResult = false;
                    pageProcessed = false;
                    changed = true;
                }

                if (!string.IsNullOrWhiteSpace(coordinator.ErrorMessage))
                {
                    Fail(coordinator.ErrorMessage);
                    return;
                }

                // GraphQL validation completes in bounded batches. Publish each
                // newly confirmed package instead of waiting for all batches on
                // the current page to finish.
                if (changed && !awaitingPageResult)
                    changed |= AddValidRepositories(coordinator.DisplayedRepos);

                if (phase == CataloguePhase.WaitingForOrganizations &&
                    coordinator.OrgsLoaded)
                {
                    QueueOrganizationsAndContinue();
                    return;
                }

                if (!awaitingPageResult &&
                    !pageProcessed &&
                    !coordinator.IsLoading &&
                    !coordinator.IsValidatingPackageManifests)
                {
                    ConsumeSettledPage();
                    return;
                }

                string nextStatus = BuildStatusMessage();
                if (!string.Equals(statusMessage, nextStatus, StringComparison.Ordinal))
                {
                    statusMessage = nextStatus;
                    changed = true;
                }

                if (changed)
                    PublishSnapshot();
            }
            catch (Exception exception)
            {
                Fail(
                    "GitHub package discovery failed unexpectedly: " +
                    GitHubUtility.SanitizeUiDiagnostic(exception.Message));
            }
        }

        private static void ConsumeSettledPage()
        {
            pageProcessed = true;
            completedPages++;
            unavailableManifestCount += coordinator.PackageManifestUnavailableCount;
            AddValidRepositories(coordinator.DisplayedRepos);

            if (coordinator.HasNextPage)
            {
                coordinator.NextPage();
                awaitingPageResult = true;
                pageProcessed = false;
                statusMessage = BuildStatusMessage();
                PublishSnapshot();
                return;
            }

            completedOwners++;
            if (phase == CataloguePhase.PersonalRepositories)
            {
                phase = CataloguePhase.WaitingForOrganizations;
                activeOwner = coordinator.Username;
                if (coordinator.OrgsLoaded)
                    QueueOrganizationsAndContinue();
                else
                {
                    statusMessage = "Loading GitHub organizations...";
                    PublishSnapshot();
                }
                return;
            }

            StartNextOrganizationOrComplete();
        }

        private static void QueueOrganizationsAndContinue()
        {
            if (!organizationsQueued)
            {
                organizationsQueued = true;
                string username = coordinator.Username;
                foreach (string organization in coordinator.Organizations)
                {
                    string normalized = organization?.Trim() ?? string.Empty;
                    if (string.IsNullOrEmpty(normalized) ||
                        string.Equals(normalized, username, StringComparison.OrdinalIgnoreCase) ||
                        !QueuedOrganizations.Add(normalized))
                    {
                        continue;
                    }

                    PendingOrganizations.Enqueue(normalized);
                }

                totalOwners = 1 + PendingOrganizations.Count;
            }

            StartNextOrganizationOrComplete();
        }

        private static void StartNextOrganizationOrComplete()
        {
            if (PendingOrganizations.Count == 0)
            {
                Complete();
                return;
            }

            activeOwner = PendingOrganizations.Dequeue();
            phase = CataloguePhase.OrganizationRepositories;
            coordinator.SetOwner(activeOwner);
            awaitingPageResult = true;
            pageProcessed = false;
            statusMessage = BuildStatusMessage();
            PublishSnapshot();
        }

        private static void Complete()
        {
            phase = CataloguePhase.Complete;
            isLoading = false;
            activeOwner = string.Empty;

            string unavailableSuffix = unavailableManifestCount > 0
                ? $" {unavailableManifestCount} repositories could not be validated."
                : string.Empty;
            string organizationWarning = coordinator?.WarningMessage;
            coverageWarningMessage = organizationWarning ?? string.Empty;
            bool hasCompleteCoverage = HasCompleteCoverage();
            bool canKeepRetainedCatalogue =
                !hasCompleteCoverage &&
                isShowingRetainedRepositories &&
                EditorApplication.timeSinceStartup < retainedCatalogueExpiresAt &&
                ReferenceEquals(publishedRepositories, lastSuccessfulRepositories);
            if (!canKeepRetainedCatalogue)
            {
                isShowingRetainedRepositories = false;
                retainedCatalogueExpiresAt = 0d;
            }

            string warningSuffix = string.IsNullOrWhiteSpace(organizationWarning)
                ? string.Empty
                : " " + organizationWarning.Trim();
            statusMessage =
                $"Found {Repositories.Count} valid UPM packages across " +
                $"{completedOwners} GitHub owners and {completedPages} pages." +
                unavailableSuffix + warningSuffix;
            if (canKeepRetainedCatalogue)
            {
                statusMessage +=
                    " Previously loaded packages remain available temporarily " +
                    "because refresh coverage was incomplete.";
            }

            // Only a warning-free catalogue with complete owner and manifest
            // coverage may become the next stale-while-revalidate baseline.
            PublishSnapshot(markSuccessfulCatalogue: hasCompleteCoverage);
        }

        private static bool HasCompleteCoverage()
        {
            return unavailableManifestCount == 0 &&
                   string.IsNullOrWhiteSpace(coverageWarningMessage) &&
                   totalOwners > 0 &&
                   completedOwners >= totalOwners;
        }

        private static void Fail(string message)
        {
            phase = CataloguePhase.Failed;
            isLoading = false;
            errorMessage = GitHubUtility.SanitizeUiDiagnostic(message);
            statusMessage = isShowingRetainedRepositories
                ? "Repository refresh stopped; previously loaded packages remain " +
                  "available temporarily."
                : Repositories.Count == 0
                ? "GitHub package discovery did not complete."
                : $"Discovery stopped after finding {Repositories.Count} valid UPM packages.";
            DisposeCoordinator();
            PublishSnapshot();
        }

        private static bool ReleaseRetainedCatalogueIfExpired(double currentTime)
        {
            if (!isShowingRetainedRepositories ||
                currentTime < retainedCatalogueExpiresAt)
            {
                return false;
            }

            isShowingRetainedRepositories = false;
            retainedCatalogueExpiresAt = 0d;
            if (phase == CataloguePhase.Failed)
            {
                statusMessage = Repositories.Count == 0
                    ? "Repository refresh failed; previously loaded packages expired."
                    : $"Repository refresh failed after validating {Repositories.Count} packages.";
            }
            else if (phase == CataloguePhase.Complete &&
                     (!string.IsNullOrWhiteSpace(coverageWarningMessage) ||
                      unavailableManifestCount > 0))
            {
                statusMessage =
                    "Repository refresh completed with incomplete coverage; " +
                    $"retained packages expired. Showing {Repositories.Count} validated packages.";
            }

            return true;
        }

        private static string BuildStatusMessage()
        {
            if (coordinator == null)
                return statusMessage;

            string owner = !string.IsNullOrWhiteSpace(activeOwner)
                ? activeOwner
                : !string.IsNullOrWhiteSpace(coordinator.SelectedOwner)
                    ? coordinator.SelectedOwner
                    : "the authenticated account";

            if (phase == CataloguePhase.WaitingForOrganizations)
                return WithRetentionNotice("Loading GitHub organizations...");

            if (coordinator.IsLoading || awaitingPageResult)
            {
                string commandStatus = coordinator.StatusMessage;
                return WithRetentionNotice(string.IsNullOrWhiteSpace(commandStatus)
                    ? $"Loading {owner} repositories, page {coordinator.CurrentPage}..."
                    : $"Loading {owner} repositories, page {coordinator.CurrentPage}: {commandStatus}");
            }

            if (coordinator.IsValidatingPackageManifests)
            {
                return WithRetentionNotice(
                    $"Validating UPM packages for {owner}, page {coordinator.CurrentPage} " +
                    $"({coordinator.PackageManifestCheckCompleted}/" +
                    $"{coordinator.PackageManifestCheckTotal})...");
            }

            return WithRetentionNotice(
                $"Processing {owner} repositories, page {coordinator.CurrentPage}...");
        }

        private static string WithRetentionNotice(string message)
        {
            return isShowingRetainedRepositories
                ? message + " Previously loaded packages remain available."
                : message;
        }

        private static bool AddValidRepositories(IEnumerable<GitHubRepo> repositories)
        {
            if (repositories == null)
                return false;

            bool changed = false;

            foreach (GitHubRepo repository in repositories)
            {
                if (repository == null ||
                    repository.ManifestState != PackageManifestState.Valid ||
                    !GitUtility.IsValidUpmPackageName(repository.DeclaredPackageName) ||
                    string.IsNullOrWhiteSpace(repository.Owner) ||
                    string.IsNullOrWhiteSpace(repository.Name))
                {
                    continue;
                }

                var copy = new PackageManagerGitHubRepository(repository);
                string repositoryIdentity = copy.Owner.Trim() + "/" + copy.Name.Trim();
                int index;
                bool hasNodeIdentity = !string.IsNullOrWhiteSpace(copy.NodeId);
                if (hasNodeIdentity && IndexByNodeId.TryGetValue(copy.NodeId, out index))
                {
                    if (Repositories[index].HasSameContent(copy))
                        continue;

                    ReplaceRepository(index, repositoryIdentity, copy);
                    changed = true;
                }
                else if (IndexByRepository.TryGetValue(repositoryIdentity, out index))
                {
                    if (Repositories[index].HasSameContent(copy))
                        continue;

                    ReplaceRepository(index, repositoryIdentity, copy);
                    changed = true;
                }
                else
                {
                    index = Repositories.Count;
                    Repositories.Add(copy);
                    IndexByRepository[repositoryIdentity] = index;
                    if (hasNodeIdentity)
                        IndexByNodeId[copy.NodeId] = index;
                    changed = true;
                }
            }

            repositoriesDirty |= changed;

            return changed;
        }

        private static void ReplaceRepository(
            int index,
            string repositoryIdentity,
            PackageManagerGitHubRepository replacement)
        {
            PackageManagerGitHubRepository previous = Repositories[index];
            string previousRepositoryIdentity =
                previous.Owner.Trim() + "/" + previous.Name.Trim();
            if (!string.Equals(
                    previousRepositoryIdentity,
                    repositoryIdentity,
                    StringComparison.OrdinalIgnoreCase) &&
                IndexByRepository.TryGetValue(
                    previousRepositoryIdentity,
                    out int previousRepositoryIndex) &&
                previousRepositoryIndex == index)
            {
                IndexByRepository.Remove(previousRepositoryIdentity);
            }

            if (!string.IsNullOrWhiteSpace(previous.NodeId) &&
                !string.Equals(previous.NodeId, replacement.NodeId, StringComparison.Ordinal) &&
                IndexByNodeId.TryGetValue(previous.NodeId, out int previousIndex) &&
                previousIndex == index)
            {
                IndexByNodeId.Remove(previous.NodeId);
            }

            Repositories[index] = replacement;
            IndexByRepository[repositoryIdentity] = index;
            if (!string.IsNullOrWhiteSpace(replacement.NodeId))
                IndexByNodeId[replacement.NodeId] = index;
        }

        private static void PublishSnapshot(bool markSuccessfulCatalogue = false)
        {
            if (repositoriesDirty && !isShowingRetainedRepositories)
            {
                var repositoryCopies = Repositories.ToArray();
                Array.Sort(
                    repositoryCopies,
                    CompareRepositories);
                if (!HasSameCatalogueContent(publishedRepositories, repositoryCopies))
                {
                    publishedRepositories =
                        new ReadOnlyCollection<PackageManagerGitHubRepository>(repositoryCopies);
                }
                repositoriesDirty = false;
            }

            // Record success before notifying synchronous Package Manager
            // subscribers so a refresh requested from a rebuild callback can
            // retain this exact completed catalogue.
            if (markSuccessfulCatalogue)
                lastSuccessfulRepositories = publishedRepositories;

            current = new PackageManagerGitHubDiscoverySnapshot(
                publishedRepositories,
                isLoading,
                statusMessage,
                errorMessage,
                completedPages,
                completedOwners,
                totalOwners,
                unavailableManifestCount,
                ++revision,
                coverageWarningMessage,
                isShowingRetainedRepositories);
            InvokeSnapshotChanged();
        }

        private static bool HasSameCatalogueContent(
            IReadOnlyList<PackageManagerGitHubRepository> left,
            IReadOnlyList<PackageManagerGitHubRepository> right)
        {
            if (left == null || right == null || left.Count != right.Count)
                return false;

            for (int index = 0; index < left.Count; index++)
            {
                if (left[index] == null || !left[index].HasSameContent(right[index]))
                    return false;
            }

            return true;
        }

        private static int CompareRepositories(
            PackageManagerGitHubRepository left,
            PackageManagerGitHubRepository right)
        {
            int ownerComparison = string.Compare(
                left?.Owner,
                right?.Owner,
                StringComparison.OrdinalIgnoreCase);
            if (ownerComparison != 0)
                return ownerComparison;

            int nameComparison = string.Compare(
                left?.Name,
                right?.Name,
                StringComparison.OrdinalIgnoreCase);
            return nameComparison != 0
                ? nameComparison
                : string.Compare(
                    left?.PackageName,
                    right?.PackageName,
                    StringComparison.Ordinal);
        }

        private static void InvokeSnapshotChanged()
        {
            Delegate[] subscribers = SnapshotChanged?.GetInvocationList();
            if (subscribers == null)
                return;

            foreach (Delegate subscriber in subscribers)
            {
                try
                {
                    ((Action)subscriber).Invoke();
                }
                catch
                {
                    // A presentation subscriber must not stop catalogue progress.
                }
            }
        }

        private static void ResetAggregation()
        {
            if (Repositories.Count > 0 || publishedRepositories.Count > 0)
                repositoriesDirty = true;

            Repositories.Clear();
            IndexByNodeId.Clear();
            IndexByRepository.Clear();
            PendingOrganizations.Clear();
            QueuedOrganizations.Clear();
            phase = CataloguePhase.Idle;
            activeOwner = string.Empty;
            statusMessage = string.Empty;
            errorMessage = string.Empty;
            coverageWarningMessage = string.Empty;
            isLoading = false;
            awaitingPageResult = false;
            pageProcessed = false;
            organizationsQueued = false;
            completedPages = 0;
            completedOwners = 0;
            totalOwners = 0;
            unavailableManifestCount = 0;
        }

        private static void DisposeCoordinator()
        {
            coordinator?.Dispose();
            coordinator = null;
        }

        private static void Stop(bool publishEmptySnapshot)
        {
            if (isStarted)
                EditorApplication.update -= Update;
            isStarted = false;
            DisposeCoordinator();
            ResetAggregation();
            isShowingRetainedRepositories = false;
            retainedCatalogueExpiresAt = 0d;
            lastSuccessfulRepositories = PackageManagerGitHubDiscoverySnapshot.Empty.Repositories;

            if (publishEmptySnapshot && !isShuttingDown)
                PublishSnapshot();
            else
            {
                current = PackageManagerGitHubDiscoverySnapshot.Empty;
                publishedRepositories = current.Repositories;
                repositoriesDirty = false;
            }
        }

        private static void OnBeforeAssemblyReload()
        {
            isShuttingDown = true;
            Stop(publishEmptySnapshot: false);
            AssemblyReloadEvents.beforeAssemblyReload -= OnBeforeAssemblyReload;
            EditorApplication.quitting -= OnEditorQuitting;
        }

        private static void OnEditorQuitting()
        {
            isShuttingDown = true;
            Stop(publishEmptySnapshot: false);
        }
    }
}
