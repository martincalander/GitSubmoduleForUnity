using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using UnityEditor.PackageManager;
using UnityEditor.PackageManager.Requests;
using UpmPackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace MartinCalander.GitSubmoduleManager.Editor
{
    internal interface IPackageDependencyRegistrySearch
    {
        bool IsCompleted { get; }

        bool TryGetResult(
            out IReadOnlyList<PackageDependencyRegistryPackage> packages,
            out string error);
    }

    internal interface IPackageDependencyResolutionFacade
    {
        bool TryGetRegisteredPackageNames(
            out IReadOnlyList<string> packageNames,
            out string error);

        PackageManagerGitHubDiscoverySnapshot GitHubSnapshot { get; }

        bool TryStartRegistrySearch(
            string packageName,
            out IPackageDependencyRegistrySearch search,
            out string error);
    }

    /// <summary>
    /// Additive facade capability for callers that can provide the complete
    /// registered-package identity. Legacy test facades remain source-compatible,
    /// but name-only entries are never silently treated as compatible.
    /// </summary>
    internal interface IPackageDependencyRegisteredPackageFacade
    {
        bool TryGetRegisteredPackages(
            out IReadOnlyList<PackageDependencyRegisteredPackage> packages,
            out string error);
    }

    /// <summary>
    /// Optional facade capability used when a custom dependency is first found.
    /// This keeps GitHub discovery lazy for all-Unity graphs while still covering
    /// custom dependencies introduced transitively by registry metadata.
    /// </summary>
    internal interface IPackageDependencyGitHubDiscoveryStarter
    {
        void EnsureGitHubDiscoveryStarted();
    }

    /// <summary>
    /// Immutable, test-friendly projection of a Unity registry search result.
    /// </summary>
    internal sealed class PackageDependencyRegistryPackage
    {
        internal PackageDependencyRegistryPackage(
            string name,
            string version,
            bool isDefaultRegistry,
            string registryName,
            IEnumerable<string> availableVersions,
            IEnumerable<PackageManifestDependency> dependencies,
            string registryUrl = "")
        {
            Name = name?.Trim() ?? string.Empty;
            Version = version?.Trim() ?? string.Empty;
            IsDefaultRegistry = isDefaultRegistry;
            RegistryName = registryName?.Trim() ?? string.Empty;
            RegistryUrl = registryUrl?.Trim() ?? string.Empty;
            AvailableVersions = new ReadOnlyCollection<string>(
                (availableVersions ?? Array.Empty<string>())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value.Trim())
                .Distinct(StringComparer.Ordinal)
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToArray());
            Dependencies = new ReadOnlyCollection<PackageManifestDependency>(
                (dependencies ?? Array.Empty<PackageManifestDependency>())
                .Where(dependency => dependency != null)
                .Select(dependency => new PackageManifestDependency(
                    dependency.Name,
                    dependency.Version))
                .OrderBy(dependency => dependency.Name, StringComparer.Ordinal)
                .ToArray());
        }

        internal string Name { get; }
        internal string Version { get; }
        internal bool IsDefaultRegistry { get; }
        internal string RegistryName { get; }
        internal string RegistryUrl { get; }
        internal IReadOnlyList<string> AvailableVersions { get; }
        internal IReadOnlyList<PackageManifestDependency> Dependencies { get; }
    }

    /// <summary>
    /// Public Unity Package Manager facade. Registry queries are deliberately
    /// routed through Client.Search so Unity owns scoped-registry selection,
    /// authentication, caching, and transport.
    /// </summary>
    internal sealed class UnityPackageDependencyResolutionFacade :
        IPackageDependencyResolutionFacade,
        IPackageDependencyRegisteredPackageFacade,
        IPackageDependencyGitHubDiscoveryStarter
    {
        internal static UnityPackageDependencyResolutionFacade Instance { get; } =
            new();

        private UnityPackageDependencyResolutionFacade()
        {
        }

        public PackageManagerGitHubDiscoverySnapshot GitHubSnapshot =>
            PackageManagerGitHubDiscovery.Current;

        public void EnsureGitHubDiscoveryStarted()
        {
            PackageManagerGitHubDiscovery.EnsureStarted();
        }

        public bool TryGetRegisteredPackageNames(
            out IReadOnlyList<string> packageNames,
            out string error)
        {
            packageNames = Array.Empty<string>();
            if (!TryGetRegisteredPackages(
                    out IReadOnlyList<PackageDependencyRegisteredPackage> packages,
                    out error))
            {
                return false;
            }

            packageNames = new ReadOnlyCollection<string>(
                packages
                .Select(package => package.Name)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray());
            return true;
        }

        public bool TryGetRegisteredPackages(
            out IReadOnlyList<PackageDependencyRegisteredPackage> packages,
            out string error)
        {
            packages = Array.Empty<PackageDependencyRegisteredPackage>();
            error = string.Empty;
            try
            {
                packages = new ReadOnlyCollection<PackageDependencyRegisteredPackage>(
                    (UpmPackageInfo.GetAllRegisteredPackages() ??
                     Array.Empty<UpmPackageInfo>())
                    .Where(package => package != null &&
                                      GitUtility.IsValidUpmPackageName(package.name))
                    .Select(package => new PackageDependencyRegisteredPackage(
                        package.name,
                        package.version,
                        package.source.ToString()))
                    .OrderBy(package => package.Name, StringComparer.Ordinal)
                    .ThenBy(package => package.Version, StringComparer.Ordinal)
                    .ThenBy(package => package.Source, StringComparer.Ordinal)
                    .ToArray());
                return true;
            }
            catch (Exception exception)
            {
                error = PackageDependencyResolutionService.SanitizeDiagnostic(
                    "Unity's registered package list could not be inspected: " +
                    exception.Message);
                return false;
            }
        }

        public bool TryStartRegistrySearch(
            string packageName,
            out IPackageDependencyRegistrySearch search,
            out string error)
        {
            search = null;
            error = string.Empty;
            if (!GitUtility.IsValidUpmPackageName(packageName))
            {
                error = "A valid UPM package name is required for registry search.";
                return false;
            }

            try
            {
                SearchRequest request = Client.Search(packageName.Trim(), false);
                if (request == null)
                {
                    error = "Unity Package Manager did not create a registry search request.";
                    return false;
                }

                search = new UnityPackageDependencyRegistrySearch(request);
                return true;
            }
            catch (Exception exception)
            {
                error = PackageDependencyResolutionService.SanitizeDiagnostic(
                    "Unity Package Manager could not start registry search: " +
                    exception.Message);
                return false;
            }
        }

        private sealed class UnityPackageDependencyRegistrySearch :
            IPackageDependencyRegistrySearch
        {
            private readonly SearchRequest request;

            internal UnityPackageDependencyRegistrySearch(SearchRequest request)
            {
                this.request = request;
            }

            public bool IsCompleted
            {
                get
                {
                    try
                    {
                        return request == null || request.IsCompleted;
                    }
                    catch
                    {
                        return true;
                    }
                }
            }

            public bool TryGetResult(
                out IReadOnlyList<PackageDependencyRegistryPackage> packages,
                out string error)
            {
                packages = Array.Empty<PackageDependencyRegistryPackage>();
                error = string.Empty;
                try
                {
                    if (request == null)
                    {
                        error = "Unity Package Manager registry search state is missing.";
                        return false;
                    }

                    if (!request.IsCompleted)
                    {
                        error = "Unity Package Manager registry search is still running.";
                        return false;
                    }

                    if (request.Status != StatusCode.Success)
                    {
                        error = PackageDependencyResolutionService.SanitizeDiagnostic(
                            string.IsNullOrWhiteSpace(request.Error?.message)
                                ? "Unity Package Manager registry search failed."
                                : "Unity Package Manager registry search failed: " +
                                  request.Error.message);
                        return false;
                    }

                    var results = new List<PackageDependencyRegistryPackage>();
                    foreach (UpmPackageInfo package in
                             request.Result ?? Array.Empty<UpmPackageInfo>())
                    {
                        if (package == null ||
                            !GitUtility.IsValidUpmPackageName(package.name))
                        {
                            continue;
                        }

                        RegistryInfo registry = package.registry;
                        var versions = new List<string>();
                        if (!string.IsNullOrWhiteSpace(package.version))
                            versions.Add(package.version);
                        if (package.versions?.all != null)
                            versions.AddRange(package.versions.all);
                        if (package.versions?.compatible != null)
                            versions.AddRange(package.versions.compatible);

                        var dependencies = new List<PackageManifestDependency>();
                        foreach (DependencyInfo dependency in
                                 package.dependencies ?? Array.Empty<DependencyInfo>())
                        {
                            if (GitUtility.IsValidUpmPackageName(dependency.name) &&
                                !string.IsNullOrWhiteSpace(dependency.version))
                            {
                                dependencies.Add(new PackageManifestDependency(
                                    dependency.name,
                                    dependency.version));
                            }
                        }

                        results.Add(new PackageDependencyRegistryPackage(
                            package.name,
                            package.version,
                            registry != null && registry.isDefault,
                            registry?.name,
                            versions,
                            dependencies,
                            registry?.url));
                    }

                    packages = new ReadOnlyCollection<PackageDependencyRegistryPackage>(
                        results
                        .OrderBy(package => package.Name, StringComparer.Ordinal)
                        .ThenBy(package => package.RegistryName, StringComparer.Ordinal)
                        .ThenBy(package => package.RegistryUrl, StringComparer.Ordinal)
                        .ThenBy(package => package.Version, StringComparer.Ordinal)
                        .ToArray());
                    return true;
                }
                catch (Exception exception)
                {
                    error = PackageDependencyResolutionService.SanitizeDiagnostic(
                        "Unity Package Manager registry search could not be read: " +
                        exception.Message);
                    return false;
                }
            }
        }
    }

    /// <summary>
    /// Manually ticked, read-only dependency resolver. It never mutates the
    /// project and starts at most one Unity registry search at a time. Callers
    /// own Editor update subscription and can unit-test every transition without
    /// clocks, sleeps, or a live Package Manager request.
    /// </summary>
    internal sealed class PackageDependencyResolutionService : IDisposable
    {
        internal const int MaximumRequirementCount = 512;
        private const string UnityPackagePrefix = "com.unity.";

        private readonly IPackageDependencyResolutionFacade facade;
        private readonly SortedDictionary<string, MutableRequirement> requirements =
            new(StringComparer.Ordinal);
        private readonly SortedDictionary<
            string,
            List<PackageDependencyRegisteredPackage>> registeredPackages =
            new(StringComparer.Ordinal);

        private IPackageDependencyRegistrySearch activeSearch;
        private MutableRequirement activeSearchRequirement;
        private bool activeSearchRequiresGitHubAbsenceProof;
        private long activeSearchGitHubAbsenceRevision;
        private string rootPackageName = string.Empty;
        private string terminalError = string.Empty;
        private bool gitHubDiscoveryRequested;
        private int revision;
        private bool isRunning;
        private bool disposed;

        internal PackageDependencyResolutionService(
            IPackageDependencyResolutionFacade facade = null)
        {
            this.facade = facade ?? UnityPackageDependencyResolutionFacade.Instance;
        }

        internal PackageDependencyResolutionPlan Current { get; private set; } =
            PackageDependencyResolutionPlan.Empty;

        internal bool IsRunning => isRunning;

        internal bool TryStart(
            string rootPackageName,
            IEnumerable<PackageManifestDependency> dependencies,
            out string error)
        {
            Reset();
            error = string.Empty;
            if (disposed)
            {
                error = "The dependency resolver has been disposed.";
                Publish(true, error);
                return false;
            }

            if (!GitUtility.IsValidUpmPackageName(rootPackageName))
            {
                error = "A valid root UPM package name is required.";
                Publish(true, error);
                return false;
            }

            if (!TryInspectRegisteredPackages(
                    out IReadOnlyList<PackageDependencyRegisteredPackage> registered,
                    out string inspectionError))
            {
                error = SanitizeDiagnostic(
                    string.IsNullOrWhiteSpace(inspectionError)
                        ? "Unity's registered package list could not be inspected."
                        : inspectionError);
                Publish(true, error);
                return false;
            }

            foreach (PackageDependencyRegisteredPackage package in
                     registered ?? Array.Empty<PackageDependencyRegisteredPackage>())
            {
                if (package == null ||
                    !GitUtility.IsValidUpmPackageName(package.Name))
                {
                    continue;
                }

                if (!registeredPackages.TryGetValue(
                        package.Name,
                        out List<PackageDependencyRegisteredPackage> matches))
                {
                    matches = new List<PackageDependencyRegisteredPackage>();
                    registeredPackages.Add(package.Name, matches);
                }

                matches.Add(new PackageDependencyRegisteredPackage(
                    package.Name,
                    package.Version,
                    package.Source));
            }

            foreach (List<PackageDependencyRegisteredPackage> matches in
                     registeredPackages.Values)
            {
                matches.Sort(CompareRegisteredPackages);
            }

            this.rootPackageName = rootPackageName.Trim();
            isRunning = true;
            foreach (PackageManifestDependency dependency in
                     dependencies ?? Array.Empty<PackageManifestDependency>())
            {
                AddRequirement(dependency, this.rootPackageName);
                if (!string.IsNullOrEmpty(terminalError))
                    break;
            }

            if (!string.IsNullOrEmpty(terminalError))
            {
                isRunning = false;
                error = terminalError;
                Publish(true, terminalError);
                return false;
            }

            Publish(false, string.Empty);
            return true;
        }

        /// <summary>
        /// Advances completed searches and all synchronously resolvable graph
        /// nodes. Returns true only when a new immutable plan revision is
        /// published or a new registry request is started.
        /// </summary>
        internal bool Tick()
        {
            if (disposed || !isRunning)
                return false;

            bool changed = false;
            if (activeSearch != null)
            {
                if (!activeSearch.IsCompleted)
                    return false;

                IPackageDependencyRegistrySearch completedSearch = activeSearch;
                MutableRequirement completedRequirement =
                    activeSearchRequirement;
                bool mayUseCompletedSearch =
                    HasCurrentGitHubAbsenceProofForActiveSearch();
                ClearActiveRegistrySearch();
                if (mayUseCompletedSearch)
                {
                    CompleteRegistrySearch(
                        completedRequirement,
                        completedSearch);
                }
                changed = true;
            }

            if (!string.IsNullOrEmpty(terminalError))
            {
                isRunning = false;
                Publish(true, terminalError);
                return true;
            }

            while (true)
            {
                MutableRequirement[] pending = requirements.Values
                    .Where(requirement =>
                        requirement.Status ==
                        PackageDependencyResolutionStatus.Pending)
                    .ToArray();
                if (pending.Length == 0)
                {
                    isRunning = false;
                    Publish(true, terminalError);
                    return true;
                }

                bool madeSynchronousProgress = false;
                foreach (MutableRequirement requirement in pending)
                {
                    if (requirement.Status !=
                        PackageDependencyResolutionStatus.Pending)
                    {
                        continue;
                    }

                    if (IsUnityPackage(requirement.Name))
                    {
                        if (!StartRegistrySearch(requirement, null))
                        {
                            changed = true;
                            madeSynchronousProgress = true;
                            continue;
                        }

                        Publish(false, terminalError);
                        return true;
                    }

                    EnsureGitHubDiscoveryStarted();
                    PackageManagerGitHubDiscoverySnapshot snapshot =
                        facade.GitHubSnapshot;

                    if (snapshot?.IsLoading == true)
                    {
                        continue;
                    }

                    if (!IsSuccessfulTerminalDiscovery(snapshot))
                    {
                        requirement.SetTerminal(
                            PackageDependencyResolutionStatus.Unresolved,
                            Array.Empty<PackageDependencyCandidate>(),
                            BuildIncompleteDiscoveryMessage(snapshot));
                        changed = true;
                        madeSynchronousProgress = true;
                        continue;
                    }

                    IReadOnlyList<PackageManagerGitHubRepository> matches =
                        FindGitHubMatches(requirement.Name, snapshot);
                    if (matches.Count != 0)
                    {
                        ResolveFromGitHub(requirement, matches);
                        changed = true;
                        madeSynchronousProgress = true;
                        if (!string.IsNullOrEmpty(terminalError))
                            break;
                        continue;
                    }

                    if (!StartRegistrySearch(requirement, snapshot))
                    {
                        changed = true;
                        madeSynchronousProgress = true;
                        continue;
                    }

                    Publish(false, terminalError);
                    return true;
                }

                if (!string.IsNullOrEmpty(terminalError))
                {
                    isRunning = false;
                    Publish(true, terminalError);
                    return true;
                }

                if (madeSynchronousProgress)
                    continue;

                if (changed)
                {
                    Publish(false, terminalError);
                    return true;
                }

                // Every remaining custom requirement is waiting for discovery.
                // A future caller-owned Tick observes the next immutable snapshot.
                return false;
            }
        }

        public void Dispose()
        {
            if (disposed)
                return;

            disposed = true;
            isRunning = false;
            ClearActiveRegistrySearch();
        }

        internal static bool IsSuccessfulTerminalDiscovery(
            PackageManagerGitHubDiscoverySnapshot snapshot)
        {
            return snapshot != null &&
                   !snapshot.IsLoading &&
                   string.IsNullOrWhiteSpace(snapshot.ErrorMessage) &&
                   string.IsNullOrWhiteSpace(snapshot.CoverageWarningMessage) &&
                   snapshot.UnavailableManifestCount == 0 &&
                   snapshot.TotalOwners > 0 &&
                   snapshot.CompletedOwners >= snapshot.TotalOwners;
        }

        private void EnsureGitHubDiscoveryStarted()
        {
            if (gitHubDiscoveryRequested)
                return;

            gitHubDiscoveryRequested = true;
            if (facade is IPackageDependencyGitHubDiscoveryStarter starter)
                starter.EnsureGitHubDiscoveryStarted();
        }

        internal static string SanitizeDiagnostic(string message)
        {
            return GitHubUtility.SanitizeUiDiagnostic(
                GitUtility.RedactCredentials(message ?? string.Empty));
        }

        private void Reset()
        {
            requirements.Clear();
            registeredPackages.Clear();
            ClearActiveRegistrySearch();
            rootPackageName = string.Empty;
            terminalError = string.Empty;
            gitHubDiscoveryRequested = false;
            isRunning = false;
            revision = 0;
            Current = PackageDependencyResolutionPlan.Empty;
        }

        private bool TryInspectRegisteredPackages(
            out IReadOnlyList<PackageDependencyRegisteredPackage> packages,
            out string error)
        {
            packages = Array.Empty<PackageDependencyRegisteredPackage>();
            error = string.Empty;
            if (facade == null)
            {
                error = "Unity's registered package list could not be inspected.";
                return false;
            }

            if (facade is IPackageDependencyRegisteredPackageFacade detailedFacade)
            {
                return detailedFacade.TryGetRegisteredPackages(
                    out packages,
                    out error);
            }

            if (!facade.TryGetRegisteredPackageNames(
                    out IReadOnlyList<string> packageNames,
                    out error))
            {
                return false;
            }

            // A legacy facade cannot prove version or source. Preserve those
            // names as incomplete identities so a matching dependency blocks
            // instead of being skipped by name alone.
            packages = new ReadOnlyCollection<PackageDependencyRegisteredPackage>(
                (packageNames ?? Array.Empty<string>())
                .Where(GitUtility.IsValidUpmPackageName)
                .Select(name => new PackageDependencyRegisteredPackage(
                    name,
                    string.Empty,
                    "Unknown"))
                .OrderBy(package => package.Name, StringComparer.Ordinal)
                .ToArray());
            return true;
        }

        private static int CompareRegisteredPackages(
            PackageDependencyRegisteredPackage left,
            PackageDependencyRegisteredPackage right)
        {
            int versionComparison = string.Compare(
                left?.Version,
                right?.Version,
                StringComparison.Ordinal);
            return versionComparison != 0
                ? versionComparison
                : string.Compare(
                    left?.Source,
                    right?.Source,
                    StringComparison.Ordinal);
        }

        private bool TryHandleRegisteredPackage(
            string name,
            string requiredVersion,
            string requestedBy)
        {
            if (!registeredPackages.TryGetValue(
                    name,
                    out List<PackageDependencyRegisteredPackage> matches))
            {
                return false;
            }

            if (!requirements.TryGetValue(name, out MutableRequirement requirement))
            {
                if (requirements.Count >= MaximumRequirementCount)
                {
                    terminalError =
                        $"The dependency graph exceeds the {MaximumRequirementCount}-package safety limit.";
                    return true;
                }

                requirement = new MutableRequirement(name, requiredVersion);
                requirements.Add(name, requirement);
            }

            requirement.RequestedBy.Add(requestedBy?.Trim() ?? string.Empty);
            if (!string.Equals(
                    requirement.Version,
                    requiredVersion,
                    StringComparison.Ordinal))
            {
                requirement.SetVersionConflict(requiredVersion);
                return true;
            }

            if (matches.Count == 1 &&
                matches[0].HasCompleteIdentity &&
                string.Equals(
                    matches[0].Version,
                    requiredVersion,
                    StringComparison.Ordinal))
            {
                requirement.SetRegisteredSatisfied(
                    $"Installed {name} {requiredVersion} already satisfies the dependency.");
                return true;
            }

            string message;
            if (matches.Count != 1)
            {
                message =
                    $"Unity reports multiple installed records for {name}; " +
                    "exact installed version compatibility could not be proven.";
            }
            else if (!matches[0].HasCompleteIdentity)
            {
                message =
                    $"The installed {name} package has incomplete version " +
                    "metadata; exact compatibility could not be proven.";
            }
            else
            {
                message =
                    $"Installed {name} {matches[0].Version} from source " +
                    $"{matches[0].Source} conflicts with required version " +
                    $"{requiredVersion}.";
            }

            requirement.SetTerminal(
                PackageDependencyResolutionStatus.Unresolved,
                Array.Empty<PackageDependencyCandidate>(),
                message);
            return true;
        }

        private void AddRequirement(
            PackageManifestDependency dependency,
            string requestedBy)
        {
            if (dependency == null)
                return;

            string name = dependency.Name?.Trim() ?? string.Empty;
            string version = dependency.Version?.Trim() ?? string.Empty;
            if (!GitUtility.IsValidUpmPackageName(name) ||
                string.IsNullOrWhiteSpace(version))
            {
                terminalError =
                    "The dependency graph contains an invalid package requirement.";
                return;
            }

            if (!string.Equals(name, rootPackageName, StringComparison.Ordinal) &&
                TryHandleRegisteredPackage(
                    name,
                    version,
                    requestedBy))
            {
                return;
            }

            if (!requirements.TryGetValue(name, out MutableRequirement existing))
            {
                if (requirements.Count >= MaximumRequirementCount)
                {
                    terminalError =
                        $"The dependency graph exceeds the {MaximumRequirementCount}-package safety limit.";
                    return;
                }

                existing = new MutableRequirement(name, version);
                requirements.Add(name, existing);
            }

            existing.RequestedBy.Add(requestedBy?.Trim() ?? string.Empty);
            if (!string.Equals(existing.Version, version, StringComparison.Ordinal))
            {
                existing.SetVersionConflict(version);
                return;
            }

            if (string.Equals(name, rootPackageName, StringComparison.Ordinal))
            {
                existing.SetTerminal(
                    PackageDependencyResolutionStatus.Unresolved,
                    Array.Empty<PackageDependencyCandidate>(),
                    $"{rootPackageName} declares a dependency cycle back to the root package.");
            }
        }

        private bool StartRegistrySearch(
            MutableRequirement requirement,
            PackageManagerGitHubDiscoverySnapshot gitHubAbsenceProof)
        {
            if (requirement == null)
                return false;

            bool requiresGitHubAbsenceProof =
                !IsUnityPackage(requirement.Name);
            if (requiresGitHubAbsenceProof &&
                (!IsSuccessfulTerminalDiscovery(gitHubAbsenceProof) ||
                 FindGitHubMatches(
                     requirement.Name,
                     gitHubAbsenceProof).Count != 0))
            {
                requirement.SetTerminal(
                    PackageDependencyResolutionStatus.Unresolved,
                    Array.Empty<PackageDependencyCandidate>(),
                    "Registry fallback was skipped because GitHub package " +
                    "absence was not bound to one complete catalogue revision.");
                return false;
            }

            if (facade.TryStartRegistrySearch(
                    requirement.Name,
                    out IPackageDependencyRegistrySearch search,
                    out string error) &&
                search != null)
            {
                activeSearch = search;
                activeSearchRequirement = requirement;
                activeSearchRequiresGitHubAbsenceProof =
                    requiresGitHubAbsenceProof;
                activeSearchGitHubAbsenceRevision =
                    requiresGitHubAbsenceProof
                        ? gitHubAbsenceProof.Revision
                        : 0L;
                return true;
            }

            requirement.SetTerminal(
                PackageDependencyResolutionStatus.Unresolved,
                Array.Empty<PackageDependencyCandidate>(),
                SanitizeDiagnostic(
                    string.IsNullOrWhiteSpace(error)
                        ? $"Registry search for {requirement.Name} could not be started."
                        : error));
            return false;
        }

        private bool HasCurrentGitHubAbsenceProofForActiveSearch()
        {
            if (!activeSearchRequiresGitHubAbsenceProof)
                return true;
            if (activeSearchRequirement == null)
                return false;

            PackageManagerGitHubDiscoverySnapshot currentSnapshot =
                facade?.GitHubSnapshot;
            return currentSnapshot != null &&
                   currentSnapshot.Revision ==
                       activeSearchGitHubAbsenceRevision &&
                   IsSuccessfulTerminalDiscovery(currentSnapshot) &&
                   FindGitHubMatches(
                       activeSearchRequirement.Name,
                       currentSnapshot).Count == 0;
        }

        private void ClearActiveRegistrySearch()
        {
            activeSearch = null;
            activeSearchRequirement = null;
            activeSearchRequiresGitHubAbsenceProof = false;
            activeSearchGitHubAbsenceRevision = 0L;
        }

        private void CompleteRegistrySearch(
            MutableRequirement requirement,
            IPackageDependencyRegistrySearch search)
        {
            if (requirement == null ||
                requirement.Status != PackageDependencyResolutionStatus.Pending)
            {
                return;
            }

            IReadOnlyList<PackageDependencyRegistryPackage> packages =
                Array.Empty<PackageDependencyRegistryPackage>();
            string searchError = string.Empty;
            if (search == null ||
                !search.TryGetResult(
                    out packages,
                    out searchError))
            {
                requirement.SetTerminal(
                    PackageDependencyResolutionStatus.Unresolved,
                    Array.Empty<PackageDependencyCandidate>(),
                    SanitizeDiagnostic(
                        string.IsNullOrWhiteSpace(searchError)
                            ? $"Registry search for {requirement.Name} failed."
                            : searchError));
                return;
            }

            PackageDependencyRegistryPackage[] exactNameMatches =
                (packages ?? Array.Empty<PackageDependencyRegistryPackage>())
                .Where(package => package != null &&
                                  string.Equals(
                                      package.Name,
                                      requirement.Name,
                                      StringComparison.Ordinal))
                .OrderBy(package => package.IsDefaultRegistry ? 0 : 1)
                .ThenBy(package => package.RegistryName, StringComparer.Ordinal)
                .ThenBy(package => package.RegistryUrl, StringComparer.Ordinal)
                .ThenBy(package => package.Version, StringComparer.Ordinal)
                .ToArray();
            PackageDependencyRegistryPackage[] compatible =
                SelectRegistryPackages(
                    exactNameMatches,
                    requirement.Version);

            if (compatible.Length == 0)
            {
                string message = exactNameMatches.Length == 0
                    ? $"{requirement.Name} was not found in a configured Unity package registry."
                    : $"No configured registry provides {requirement.Name} at requested version {requirement.Version}.";
                requirement.SetTerminal(
                    PackageDependencyResolutionStatus.Unresolved,
                    Array.Empty<PackageDependencyCandidate>(),
                    message);
                return;
            }

            var candidates = compatible
                .Select(package => CreateRegistryCandidate(
                    package,
                    requirement.Version))
                .OrderBy(candidate => candidate.Source)
                .ThenBy(candidate => candidate.SourceName, StringComparer.Ordinal)
                .ThenBy(candidate => candidate.SourceIdentity, StringComparer.Ordinal)
                .ToArray();
            if (compatible.Any(package =>
                    string.IsNullOrWhiteSpace(package.RegistryUrl)))
            {
                requirement.SetTerminal(
                    PackageDependencyResolutionStatus.Unresolved,
                    Array.Empty<PackageDependencyCandidate>(),
                    $"A registry result for {requirement.Name} did not expose a " +
                    "stable registry URL; its source identity could not be verified.");
                return;
            }

            if (candidates.Length > 1)
            {
                requirement.SetTerminal(
                    PackageDependencyResolutionStatus.Ambiguous,
                    candidates,
                    $"Multiple configured registries provide {requirement.Name} at {requirement.Version}.");
                return;
            }

            PackageDependencyRegistryPackage metadataPackage = compatible[0];
            if (!string.Equals(
                    metadataPackage.Version,
                    requirement.Version,
                    StringComparison.Ordinal))
            {
                string metadataVersion = string.IsNullOrWhiteSpace(
                    metadataPackage.Version)
                    ? "missing"
                    : metadataPackage.Version;
                requirement.SetTerminal(
                    PackageDependencyResolutionStatus.Unresolved,
                    candidates,
                    $"{candidates[0].SourceName} lists {requirement.Name} " +
                    $"at requested version {requirement.Version}, but returned " +
                    $"dependency metadata describes version {metadataVersion}. " +
                    "The mismatched dependency metadata was not expanded.");
                return;
            }

            requirement.SetTerminal(
                PackageDependencyResolutionStatus.Resolved,
                candidates,
                $"{requirement.Name} is available from {candidates[0].SourceName}.");
            ExpandDependencies(metadataPackage.Dependencies, requirement.Name);
        }

        private void ResolveFromGitHub(
            MutableRequirement requirement,
            IReadOnlyList<PackageManagerGitHubRepository> matches)
        {
            PackageManagerGitHubRepository[] ordered = matches
                .Where(repository => repository != null)
                .OrderBy(repository => repository.Owner, StringComparer.Ordinal)
                .ThenBy(repository => repository.Name, StringComparer.Ordinal)
                .ThenBy(repository => repository.Url, StringComparer.Ordinal)
                .ToArray();
            PackageDependencyCandidate[] candidates = ordered
                .Select(CreateGitHubCandidate)
                .ToArray();
            if (ordered.Length > 1)
            {
                requirement.SetTerminal(
                    PackageDependencyResolutionStatus.Ambiguous,
                    candidates,
                    $"Multiple GitHub repositories declare {requirement.Name}; choose a repository before installing dependencies.");
                return;
            }

            PackageManagerGitHubRepository repository = ordered[0];
            if (!HasCompleteGitHubInstallIdentity(repository))
            {
                requirement.SetTerminal(
                    PackageDependencyResolutionStatus.Unresolved,
                    candidates,
                    $"GitHub metadata for {requirement.Name} does not contain " +
                    "an exact repository URL, explicit valid default branch, " +
                    "exact inspected commit, and verified root package.json.meta GUID. " +
                    "Registry search was skipped because a GitHub package exists.");
                return;
            }

            if (!string.Equals(
                    repository.Version?.Trim(),
                    requirement.Version,
                    StringComparison.Ordinal))
            {
                requirement.SetTerminal(
                    PackageDependencyResolutionStatus.Unresolved,
                    candidates,
                    $"GitHub provides {requirement.Name} at {repository.Version}, " +
                    $"but {requirement.Version} is required. Registry search was skipped because a GitHub package exists.");
                return;
            }

            requirement.SetTerminal(
                PackageDependencyResolutionStatus.Resolved,
                candidates,
                $"{requirement.Name} is available from GitHub repository " +
                $"{repository.Owner}/{repository.Name}.");
            ExpandDependencies(repository.Dependencies, requirement.Name);
        }

        private void ExpandDependencies(
            IEnumerable<PackageManifestDependency> dependencies,
            string requestedBy)
        {
            foreach (PackageManifestDependency dependency in
                     dependencies ?? Array.Empty<PackageManifestDependency>())
            {
                AddRequirement(dependency, requestedBy);
                if (!string.IsNullOrEmpty(terminalError))
                    return;
            }
        }

        private void Publish(bool isComplete, string errorMessage)
        {
            var results = new List<PackageDependencyResolutionResult>(
                requirements.Count);
            foreach (MutableRequirement requirement in requirements.Values)
            {
                if (requirement.IsSatisfiedByRegisteredPackage)
                    continue;

                results.Add(new PackageDependencyResolutionResult(
                    new PackageDependencyRequirement(
                        requirement.Name,
                        requirement.Version,
                        requirement.RequestedBy),
                    requirement.Status,
                    requirement.Candidates,
                    SanitizeDiagnostic(requirement.Message)));
            }

            Current = new PackageDependencyResolutionPlan(
                results,
                isComplete,
                SanitizeDiagnostic(errorMessage),
                ++revision);
        }

        private static IReadOnlyList<PackageManagerGitHubRepository>
            FindGitHubMatches(
                string packageName,
                PackageManagerGitHubDiscoverySnapshot snapshot)
        {
            if (snapshot?.Repositories == null ||
                string.IsNullOrWhiteSpace(packageName))
            {
                return Array.Empty<PackageManagerGitHubRepository>();
            }

            return snapshot.Repositories
                .Where(repository => repository != null &&
                                     string.Equals(
                                         repository.PackageName,
                                         packageName,
                                         StringComparison.Ordinal))
                .OrderBy(repository => repository.Owner, StringComparer.Ordinal)
                .ThenBy(repository => repository.Name, StringComparer.Ordinal)
                .ThenBy(repository => repository.Url, StringComparer.Ordinal)
                .ToArray();
        }

        private static string BuildIncompleteDiscoveryMessage(
            PackageManagerGitHubDiscoverySnapshot snapshot)
        {
            if (!string.IsNullOrWhiteSpace(snapshot?.ErrorMessage))
            {
                return SanitizeDiagnostic(
                    "GitHub package discovery did not complete: " +
                    snapshot.ErrorMessage +
                    " Registry search was skipped because GitHub absence was not proven.");
            }

            if (!string.IsNullOrWhiteSpace(snapshot?.CoverageWarningMessage))
            {
                return SanitizeDiagnostic(
                    "GitHub package discovery could not inspect every owner: " +
                    snapshot.CoverageWarningMessage +
                    " Registry search was skipped because GitHub absence was not proven.");
            }

            if (snapshot?.UnavailableManifestCount > 0)
            {
                return
                    $"GitHub could not validate package.json in " +
                    $"{snapshot.UnavailableManifestCount} repositories. Registry " +
                    "search was skipped because GitHub absence was not proven.";
            }

            return
                "GitHub package discovery has not completed successfully. " +
                "Registry search was skipped because GitHub absence was not proven.";
        }

        private static bool IsUnityPackage(string packageName)
        {
            return packageName != null &&
                   packageName.StartsWith(
                       UnityPackagePrefix,
                       StringComparison.Ordinal);
        }

        private static bool IsRegistryVersionAvailable(
            PackageDependencyRegistryPackage package,
            string requiredVersion)
        {
            if (package == null || string.IsNullOrWhiteSpace(requiredVersion))
                return false;
            string normalized = requiredVersion.Trim();
            if (string.Equals(package.Version, normalized, StringComparison.Ordinal))
                return true;
            return package.AvailableVersions.Any(version =>
                string.Equals(version, normalized, StringComparison.Ordinal));
        }

        private static string GetRegistryIdentity(
            PackageDependencyRegistryPackage package,
            int missingIdentityIndex)
        {
            string registryUrl = package?.RegistryUrl?.Trim() ?? string.Empty;
            if (!string.IsNullOrEmpty(registryUrl))
            {
                return (package.IsDefaultRegistry
                    ? "default-url:"
                    : "custom-url:") + registryUrl;
            }

            // Display names are not identities. Without a URL, keep every
            // result distinct so two registries with the same label cannot be
            // collapsed into a single apparently safe candidate.
            return "missing-url:" + missingIdentityIndex.ToString("D8");
        }

        private static PackageDependencyRegistryPackage[] SelectRegistryPackages(
            IEnumerable<PackageDependencyRegistryPackage> packages,
            string requiredVersion)
        {
            RegistryPackageMatch[] matches =
                (packages ?? Array.Empty<PackageDependencyRegistryPackage>())
                .Where(package => IsRegistryVersionAvailable(
                    package,
                    requiredVersion))
                .Select((package, index) => new RegistryPackageMatch(
                    package,
                    GetRegistryIdentity(package, index)))
                .ToArray();
            var selected = new List<RegistryPackageMatch>();
            foreach (IGrouping<string, RegistryPackageMatch> registryGroup in
                     matches.GroupBy(
                         match => match.Identity,
                         StringComparer.Ordinal))
            {
                RegistryPackageMatch[] exactMetadata = registryGroup
                    .Where(match => string.Equals(
                        match.Package.Version,
                        requiredVersion,
                        StringComparison.Ordinal))
                    .ToArray();
                IEnumerable<RegistryPackageMatch> usableMetadata =
                    exactMetadata.Length == 0
                        ? registryGroup
                        : exactMetadata;

                // Identical duplicate records from one URL are one source.
                // Conflicting dependency metadata remains multiple candidates,
                // which makes the result ambiguous and prevents expansion.
                selected.AddRange(usableMetadata
                    .GroupBy(
                        match => GetRegistryMetadataIdentity(match.Package),
                        StringComparer.Ordinal)
                    .Select(group => group.First()));
            }

            return selected
                .OrderBy(match => match.Identity, StringComparer.Ordinal)
                .ThenBy(
                    match => GetRegistryMetadataIdentity(match.Package),
                    StringComparer.Ordinal)
                .Select(match => match.Package)
                .ToArray();
        }

        private static string GetRegistryMetadataIdentity(
            PackageDependencyRegistryPackage package)
        {
            string dependencies = string.Join(
                "\n",
                (package?.Dependencies ??
                 Array.Empty<PackageManifestDependency>())
                .OrderBy(dependency => dependency.Name, StringComparer.Ordinal)
                .ThenBy(dependency => dependency.Version, StringComparer.Ordinal)
                .Select(dependency =>
                    (dependency.Name ?? string.Empty) + "\0" +
                    (dependency.Version ?? string.Empty)));
            return (package?.Version ?? string.Empty) + "\n" + dependencies;
        }

        private static bool HasCompleteGitHubInstallIdentity(
            PackageManagerGitHubRepository repository)
        {
            if (repository == null)
                return false;

            string owner = repository.Owner?.Trim() ?? string.Empty;
            string name = repository.Name?.Trim() ?? string.Empty;
            string url = repository.Url?.Trim() ?? string.Empty;
            string branch = repository.DefaultBranch?.Trim() ?? string.Empty;
            if (string.IsNullOrEmpty(owner) ||
                string.IsNullOrEmpty(name) ||
                !GitUtility.IsValidRepositoryUrl(url) ||
                !GitHubUtility.TryParseGitHubRepo(
                    url,
                    out string urlOwner,
                    out string urlName) ||
                !string.Equals(owner, urlOwner, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(name, urlName, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return !string.IsNullOrEmpty(branch) &&
                   !string.Equals(branch, ".", StringComparison.Ordinal) &&
                   GitUtility.IsValidBranchName(branch) &&
                   GitUtility.IsValidGitObjectId(
                       repository.PackageManifestCommitOid) &&
                   GitSubmoduleInstallProbeSnapshot.IsValidMetaGuid(
                       repository.PackageManifestMetaGuid);
        }

        private static PackageDependencyCandidate CreateGitHubCandidate(
            PackageManagerGitHubRepository repository)
        {
            string owner = repository?.Owner?.Trim() ?? string.Empty;
            string name = repository?.Name?.Trim() ?? string.Empty;
            return new PackageDependencyCandidate(
                PackageDependencyCandidateSource.GitHub,
                repository?.PackageName,
                repository?.Version,
                string.IsNullOrEmpty(owner)
                    ? name
                    : owner + "/" + name,
                owner,
                name,
                repository?.Url,
                repository?.DefaultBranch,
                repository?.Url,
                GitUtility.ComputePackageDependencyFingerprint(
                    repository?.Dependencies),
                PackageManifestMetaVerification.Verified,
                repository?.PackageManifestMetaGuid,
                repository?.PackageManifestCommitOid);
        }

        private static PackageDependencyCandidate CreateRegistryCandidate(
            PackageDependencyRegistryPackage package,
            string requestedVersion)
        {
            bool isDefault = package != null && package.IsDefaultRegistry;
            string registryName = package?.RegistryName?.Trim() ?? string.Empty;
            if (string.IsNullOrEmpty(registryName))
            {
                registryName = isDefault
                    ? "Unity Registry"
                    : "Custom Registry";
            }

            return new PackageDependencyCandidate(
                isDefault
                    ? PackageDependencyCandidateSource.UnityRegistry
                    : PackageDependencyCandidateSource.CustomRegistry,
                package?.Name,
                requestedVersion,
                registryName,
                sourceIdentity: package?.RegistryUrl,
                dependencyFingerprint:
                    GitUtility.ComputePackageDependencyFingerprint(
                        package?.Dependencies));
        }

        private sealed class RegistryPackageMatch
        {
            internal RegistryPackageMatch(
                PackageDependencyRegistryPackage package,
                string identity)
            {
                Package = package;
                Identity = identity ?? string.Empty;
            }

            internal PackageDependencyRegistryPackage Package { get; }
            internal string Identity { get; }
        }

        private sealed class MutableRequirement
        {
            internal MutableRequirement(string name, string version)
            {
                Name = name;
                Version = version;
                requestedVersions.Add(version);
            }

            internal string Name { get; }
            internal string Version { get; private set; }
            internal SortedSet<string> RequestedBy { get; } =
                new(StringComparer.Ordinal);
            internal PackageDependencyResolutionStatus Status { get; private set; } =
                PackageDependencyResolutionStatus.Pending;
            internal IReadOnlyList<PackageDependencyCandidate> Candidates
                { get; private set; } = Array.Empty<PackageDependencyCandidate>();
            internal string Message { get; private set; } = string.Empty;
            internal bool IsSatisfiedByRegisteredPackage { get; private set; }
            private readonly SortedSet<string> requestedVersions =
                new(StringComparer.Ordinal);

            internal void SetRegisteredSatisfied(string message)
            {
                Status = PackageDependencyResolutionStatus.Resolved;
                Candidates = Array.Empty<PackageDependencyCandidate>();
                Message = message ?? string.Empty;
                IsSatisfiedByRegisteredPackage = true;
            }

            internal void SetVersionConflict(string conflictingVersion)
            {
                requestedVersions.Add(conflictingVersion ?? string.Empty);
                Version = requestedVersions.First();
                SetTerminal(
                    PackageDependencyResolutionStatus.Unresolved,
                    Array.Empty<PackageDependencyCandidate>(),
                    $"{Name} is requested with conflicting versions " +
                    string.Join(", ", requestedVersions) + ".");
            }

            internal void SetTerminal(
                PackageDependencyResolutionStatus status,
                IEnumerable<PackageDependencyCandidate> candidates,
                string message)
            {
                Status = status;
                IsSatisfiedByRegisteredPackage = false;
                Candidates = new ReadOnlyCollection<PackageDependencyCandidate>(
                    (candidates ?? Array.Empty<PackageDependencyCandidate>())
                    .Where(candidate => candidate != null)
                    .ToArray());
                Message = message ?? string.Empty;
            }
        }
    }
}
