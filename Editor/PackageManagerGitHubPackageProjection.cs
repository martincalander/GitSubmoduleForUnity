using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;

namespace MartinCalander.GitSubmoduleManager.Editor
{
    /// <summary>
    /// Projects GitHub discovery results into Package Manager's package database.
    ///
    /// Unity does not expose a public data-provider API for extension pages. This
    /// adapter therefore treats the internal Package Manager API as an optional,
    /// versioned contract: every member is validated before use and every mutation
    /// is scoped to non-discoverable placeholder packages in our reserved ID range.
    /// If the contract changes, GitHub discovery simply remains unavailable.
    /// </summary>
    [InitializeOnLoad]
    internal static class PackageManagerGitHubPackageProjection
    {
        internal const string ReservedPackageIdPrefix =
            "git-submodule-manager:github:";

        private const BindingFlags AnyInstance =
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private const BindingFlags AnyStatic =
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic |
            BindingFlags.FlattenHierarchy;

        private static readonly object Gate = new object();
        private static readonly HashSet<object> RetainedHosts =
            new HashSet<object>(ReferenceComparer.Instance);
        private static readonly Dictionary<string, PackageManagerGitHubRepository>
            RepositoryByPackageId =
                new Dictionary<string, PackageManagerGitHubRepository>(
                    StringComparer.Ordinal);

        private static ReflectionContract supportedContract;
        private static object lastPackageDatabase;
        private static IReadOnlyList<PackageManagerGitHubRepository>
            lastCompletedReconcileRepositories;
        private static IReadOnlyList<PackageManagerGitHubRepository>
            lastReconcileAttemptRepositories;
        private static bool lastReconcileUpdatedPackageDatabase;
        private static IReadOnlyList<PackageManagerGitHubRepository>
            pendingProjectionRetryRepositories;
        private static bool projectionRetryQueued;
        private static bool isProjectionRetryInProgress;
        private static bool isShuttingDown;
        private static int packageDatabaseUpdateDepth;

        // Deterministic failure seam for the retry contract tests. Production
        // code leaves this null and always uses the guarded reflection factory.
        internal static Func<string, PackageManagerGitHubRepository, bool>
            ProjectedPackageCreationGateForTests { get; set; }

        static PackageManagerGitHubPackageProjection()
        {
            PackageManagerGitHubDiscovery.SnapshotChanged += OnDiscoverySnapshotChanged;
            PackageManagerSubmoduleSnapshot.SnapshotChanged += OnSubmoduleSnapshotChanged;
            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;
            EditorApplication.quitting += OnEditorQuitting;
            EditorApplication.delayCall += PurgeStalePackagesOnStartup;
        }

        /// <summary>
        /// True only when the Package Manager implementation has the complete
        /// reflection shape required by this projection.
        /// </summary>
        internal static bool IsSupportedContract()
        {
            return TryGetContract(out _);
        }

        // PackageDatabase.UpdatePackages synchronously refreshes active pages.
        // The Harmony refresh hook uses this narrow guard to distinguish that
        // projection callback from an actual Package Manager refresh request.
        internal static bool IsUpdatingPackageDatabase =>
            packageDatabaseUpdateDepth > 0;

        /// <summary>
        /// Retains a Package Manager visual root by reference. Repeated retains of
        /// the same root are idempotent. The first live host restores the current
        /// catalogue but does not itself start GitHub discovery.
        /// </summary>
        internal static bool RetainHost(object packageManagerRoot)
        {
            if (packageManagerRoot == null)
                return false;

            bool firstHost;
            lock (Gate)
            {
                if (isShuttingDown)
                    return false;

                if (!RetainedHosts.Add(packageManagerRoot))
                    return true;

                firstHost = RetainedHosts.Count == 1;
            }

            if (!firstHost)
                return true;

            // Remove serialized placeholders left by an interrupted previous
            // domain, then rebuild only from the current immutable catalogue.
            PackageManagerSubmoduleSnapshot.Refresh();
            RemoveOwnedPackages();
            return Reconcile(PackageManagerGitHubDiscovery.Current);
        }

        /// <summary>
        /// Releases a Package Manager visual root by reference. When the final
        /// root closes, all projected packages and the discovery process retire.
        /// </summary>
        internal static bool ReleaseHost(object packageManagerRoot)
        {
            if (packageManagerRoot == null)
                return false;

            bool lastHost = false;
            lock (Gate)
            {
                if (RetainedHosts.Remove(packageManagerRoot))
                    lastHost = RetainedHosts.Count == 0;
            }

            if (!lastHost)
                return true;

            bool removed = RemoveOwnedPackages();
            try
            {
                PackageManagerGitHubDiscovery.Dispose();
            }
            catch
            {
                // A projection failure must never break Package Manager teardown.
                removed = false;
            }

            return removed;
        }

        internal static bool Reconcile(
            PackageManagerGitHubDiscoverySnapshot snapshot)
        {
            PackageManagerGitHubDiscoverySnapshot effectiveSnapshot =
                snapshot ?? PackageManagerGitHubDiscoverySnapshot.Empty;
            // Async add/import completions may arrive after the Package Manager
            // window has closed. Never resurrect projection data without a host.
            if (!HasRetainedHosts())
                return true;

            if (!TryGetContract(out ReflectionContract contract) ||
                !TryResolvePackageDatabase(contract, out object packageDatabase))
            {
                QueuePendingProjectionRetry(effectiveSnapshot.Repositories);
                return false;
            }

            return Reconcile(packageDatabase, effectiveSnapshot);
        }

        /// <summary>
        /// Reconciles against an explicitly supplied database. This overload keeps
        /// the reflection boundary deterministic for tests and host integrations.
        /// </summary>
        internal static bool Reconcile(
            object packageDatabase,
            PackageManagerGitHubDiscoverySnapshot snapshot)
        {
            PackageManagerGitHubDiscoverySnapshot effectiveSnapshot =
                snapshot ?? PackageManagerGitHubDiscoverySnapshot.Empty;
            try
            {
                if (!HasRetainedHosts())
                    return true;

                if (packageDatabase == null ||
                    !TryGetContract(out ReflectionContract contract) ||
                    !contract.PackageDatabaseType.IsInstanceOfType(packageDatabase))
                {
                    QueuePendingProjectionRetry(
                        effectiveSnapshot.Repositories);
                    return false;
                }

                lock (Gate)
                {
                    if (isShuttingDown)
                        return false;
                    lastPackageDatabase = packageDatabase;
                }

                bool reconciled = ReconcileCore(
                    contract,
                    packageDatabase,
                    effectiveSnapshot,
                    out bool packageDatabaseUpdated,
                    out bool allDesiredPackagesProjected);
                lock (Gate)
                {
                    lastReconcileAttemptRepositories =
                        effectiveSnapshot.Repositories;
                    lastReconcileUpdatedPackageDatabase =
                        reconciled && packageDatabaseUpdated;
                    if (reconciled && allDesiredPackagesProjected)
                    {
                        lastCompletedReconcileRepositories =
                            effectiveSnapshot.Repositories;
                    }
                    else if (ReferenceEquals(
                                 lastCompletedReconcileRepositories,
                                 effectiveSnapshot.Repositories))
                    {
                        // A package/submodule state change can require another
                        // projection attempt without changing discovery identity.
                        // Never let an older successful attempt suppress retry.
                        lastCompletedReconcileRepositories = null;
                    }
                }

                if (reconciled && allDesiredPackagesProjected)
                {
                    CancelPendingProjectionRetry(
                        effectiveSnapshot.Repositories);
                }
                else
                    QueuePendingProjectionRetry(effectiveSnapshot.Repositories);

                return reconciled;
            }
            catch
            {
                // Internal Package Manager changes fail open: native packages and
                // the rest of the Package Manager window remain untouched.
                QueuePendingProjectionRetry(effectiveSnapshot.Repositories);
                return false;
            }
        }

        /// <summary>
        /// Resolves sidecar repository data for one of our projected Package
        /// Manager package or version objects.
        /// </summary>
        internal static bool TryGetRepository(
            object packageOrVersion,
            out PackageManagerGitHubRepository repository)
        {
            repository = null;
            if (packageOrVersion == null ||
                !TryGetContract(out ReflectionContract contract))
            {
                return false;
            }

            try
            {
                string packageId = contract.ReadUniqueId(packageOrVersion);
                if (!IsReservedPackageId(packageId))
                    return false;

                lock (Gate)
                {
                    return RepositoryByPackageId.TryGetValue(
                        packageId,
                        out repository);
                }
            }
            catch
            {
                repository = null;
                return false;
            }
        }

        internal static bool RemoveOwnedPackages()
        {
            if (!TryGetContract(out ReflectionContract contract))
                return false;

            object packageDatabase;
            lock (Gate)
                packageDatabase = lastPackageDatabase;

            if (packageDatabase == null ||
                !contract.PackageDatabaseType.IsInstanceOfType(packageDatabase))
            {
                if (!TryResolvePackageDatabase(contract, out packageDatabase))
                    return false;
            }

            return RemoveOwnedPackages(packageDatabase);
        }

        internal static bool RemoveOwnedPackages(object packageDatabase)
        {
            try
            {
                if (packageDatabase == null ||
                    !TryGetContract(out ReflectionContract contract) ||
                    !contract.PackageDatabaseType.IsInstanceOfType(packageDatabase))
                {
                    return false;
                }

                lock (Gate)
                    lastPackageDatabase = packageDatabase;

                List<string> ownedIds = GetOwnedPackageIds(
                    contract,
                    packageDatabase);
                if (ownedIds.Count != 0 &&
                    !UpdatePackageDatabase(
                        contract,
                        packageDatabase,
                        Array.Empty<object>(),
                        ownedIds))
                {
                    return false;
                }

                lock (Gate)
                {
                    RepositoryByPackageId.Clear();
                    lastCompletedReconcileRepositories = null;
                    lastReconcileAttemptRepositories = null;
                    lastReconcileUpdatedPackageDatabase = false;
                }
                CancelPendingProjectionRetry(null);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool ReconcileCore(
            ReflectionContract contract,
            object packageDatabase,
            PackageManagerGitHubDiscoverySnapshot snapshot,
            out bool packageDatabaseUpdated,
            out bool allDesiredPackagesProjected)
        {
            packageDatabaseUpdated = false;
            allDesiredPackagesProjected = false;
            if (!contract.TryGetAllPackages(
                    packageDatabase,
                    out List<object> allPackages))
            {
                return false;
            }

            var existingOwned = new Dictionary<string, object>(StringComparer.Ordinal);
            var reservedCollisions = new HashSet<string>(StringComparer.Ordinal);
            var installedPackageNames = new HashSet<string>(StringComparer.Ordinal);

            foreach (object package in allPackages)
            {
                if (package == null)
                    continue;

                string packageId = contract.ReadPackageUniqueId(package);
                if (IsReservedPackageId(packageId))
                {
                    if (contract.IsOwnedPlaceholderPackage(package))
                        existingOwned[packageId] = package;
                    else
                        reservedCollisions.Add(packageId);
                    continue;
                }

                object primary = contract.ReadPrimaryVersion(package);
                if (primary == null || !contract.ReadIsInstalled(primary))
                    continue;

                string installedName = contract.ReadVersionName(primary);
                if (string.IsNullOrWhiteSpace(installedName))
                    installedName = contract.ReadPackageName(package);
                if (!string.IsNullOrWhiteSpace(installedName))
                    installedPackageNames.Add(installedName.Trim());
            }

            var desired = new Dictionary<string, PackageManagerGitHubRepository>(
                StringComparer.Ordinal);
            IReadOnlyList<PackageManagerGitHubRepository> repositories =
                snapshot.Repositories;
            for (int index = 0; index < repositories.Count; index++)
            {
                PackageManagerGitHubRepository repository = repositories[index];
                if (!IsValidRepository(repository) ||
                    installedPackageNames.Contains(repository.PackageName.Trim()) ||
                    PackageManagerSubmoduleSnapshot.ContainsGitHubRepository(
                        repository.Owner,
                        repository.Name))
                {
                    continue;
                }

                string packageId = BuildPackageId(repository);
                if (string.IsNullOrEmpty(packageId) ||
                    reservedCollisions.Contains(packageId))
                {
                    continue;
                }

                if (!desired.ContainsKey(packageId))
                    desired.Add(packageId, repository);
            }

            var removeIds = new List<string>();
            foreach (KeyValuePair<string, object> pair in existingOwned)
            {
                if (!desired.ContainsKey(pair.Key))
                    removeIds.Add(pair.Key);
            }

            Dictionary<string, PackageManagerGitHubRepository> priorMap;
            lock (Gate)
            {
                priorMap = new Dictionary<string, PackageManagerGitHubRepository>(
                    RepositoryByPackageId,
                    StringComparer.Ordinal);
            }

            var addOrUpdate = new List<object>();
            var successfullyBuiltIds = new HashSet<string>(StringComparer.Ordinal);
            bool packageCreationFailed = false;
            foreach (KeyValuePair<string, PackageManagerGitHubRepository> pair in desired)
            {
                if (existingOwned.ContainsKey(pair.Key) &&
                    priorMap.TryGetValue(
                        pair.Key,
                        out PackageManagerGitHubRepository priorRepository) &&
                    RepositoryEquals(priorRepository, pair.Value))
                {
                    continue;
                }

                Func<string, PackageManagerGitHubRepository, bool> creationGate =
                    ProjectedPackageCreationGateForTests;
                if ((creationGate != null &&
                     !creationGate(pair.Key, pair.Value)) ||
                    !contract.TryCreateProjectedPackage(
                        pair.Key,
                        pair.Value,
                        out object projectedPackage))
                {
                    packageCreationFailed = true;
                    continue;
                }

                addOrUpdate.Add(projectedPackage);
                successfullyBuiltIds.Add(pair.Key);
            }

            var nextMap = new Dictionary<string, PackageManagerGitHubRepository>(
                StringComparer.Ordinal);
            foreach (KeyValuePair<string, PackageManagerGitHubRepository> pair in desired)
            {
                if (successfullyBuiltIds.Contains(pair.Key))
                {
                    nextMap[pair.Key] = pair.Value;
                }
                else if (existingOwned.ContainsKey(pair.Key) &&
                         priorMap.TryGetValue(
                             pair.Key,
                             out PackageManagerGitHubRepository priorRepository))
                {
                    // If refreshing an existing placeholder failed, retain the
                    // matching old sidecar data and leave this catalogue pending
                    // so the next status snapshot can retry the construction.
                    nextMap[pair.Key] = priorRepository;
                }
            }

            // PackageDatabase.UpdatePackages synchronously rebuilds active pages.
            // Publish the sidecar lookup first so ExtensionPage.filter can
            // recognize packages introduced by this same update. Restore the
            // previous lookup if Unity rejects or throws during the mutation.
            ReplaceRepositoryMap(nextMap);
            try
            {
                if ((addOrUpdate.Count != 0 || removeIds.Count != 0) &&
                    !UpdatePackageDatabase(
                        contract,
                        packageDatabase,
                        addOrUpdate,
                        removeIds))
                {
                    ReplaceRepositoryMap(priorMap);
                    return false;
                }

                packageDatabaseUpdated =
                    addOrUpdate.Count != 0 || removeIds.Count != 0;
                allDesiredPackagesProjected = !packageCreationFailed;
            }
            catch
            {
                ReplaceRepositoryMap(priorMap);
                throw;
            }

            return true;
        }

        private static bool UpdatePackageDatabase(
            ReflectionContract contract,
            object packageDatabase,
            IReadOnlyList<object> addOrUpdate,
            IReadOnlyList<string> removeIds)
        {
            packageDatabaseUpdateDepth++;
            try
            {
                return contract.UpdatePackages(
                    packageDatabase,
                    addOrUpdate,
                    removeIds);
            }
            finally
            {
                packageDatabaseUpdateDepth--;
            }
        }

        private static void ReplaceRepositoryMap(
            IReadOnlyDictionary<string, PackageManagerGitHubRepository> repositories)
        {
            lock (Gate)
            {
                RepositoryByPackageId.Clear();
                foreach (KeyValuePair<string, PackageManagerGitHubRepository> pair in repositories)
                    RepositoryByPackageId.Add(pair.Key, pair.Value);
            }
        }

        private static List<string> GetOwnedPackageIds(
            ReflectionContract contract,
            object packageDatabase)
        {
            var ownedIds = new List<string>();
            if (!contract.TryGetAllPackages(packageDatabase, out List<object> packages))
                return ownedIds;

            foreach (object package in packages)
            {
                if (!contract.IsOwnedPlaceholderPackage(package))
                    continue;

                string packageId = contract.ReadPackageUniqueId(package);
                if (IsReservedPackageId(packageId))
                    ownedIds.Add(packageId);
            }

            return ownedIds;
        }

        private static bool IsValidRepository(
            PackageManagerGitHubRepository repository)
        {
            return repository != null &&
                   !string.IsNullOrWhiteSpace(repository.Owner) &&
                   !string.IsNullOrWhiteSpace(repository.Name) &&
                   !string.IsNullOrWhiteSpace(repository.PackageName) &&
                   GitUtility.IsValidUpmPackageName(repository.PackageName.Trim()) &&
                   !string.IsNullOrWhiteSpace(repository.Version);
        }

        private static string BuildPackageId(
            PackageManagerGitHubRepository repository)
        {
            string identity = !string.IsNullOrWhiteSpace(repository.NodeId)
                ? repository.NodeId.Trim()
                : repository.Owner.Trim().ToLowerInvariant() + "/" +
                  repository.Name.Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(identity))
                return string.Empty;

            try
            {
                using (SHA256 sha256 = SHA256.Create())
                {
                    byte[] digest = sha256.ComputeHash(Encoding.UTF8.GetBytes(identity));
                    var builder = new StringBuilder(
                        ReservedPackageIdPrefix.Length + digest.Length * 2);
                    builder.Append(ReservedPackageIdPrefix);
                    for (int index = 0; index < digest.Length; index++)
                        builder.Append(digest[index].ToString("x2", CultureInfo.InvariantCulture));
                    return builder.ToString();
                }
            }
            catch
            {
                return string.Empty;
            }
        }

        private static bool IsReservedPackageId(string packageId)
        {
            return !string.IsNullOrEmpty(packageId) &&
                   packageId.StartsWith(
                       ReservedPackageIdPrefix,
                       StringComparison.Ordinal);
        }

        private static bool RepositoryEquals(
            PackageManagerGitHubRepository left,
            PackageManagerGitHubRepository right)
        {
            if (ReferenceEquals(left, right))
                return true;
            if (left == null || right == null)
                return false;

            return string.Equals(left.NodeId, right.NodeId, StringComparison.Ordinal) &&
                   string.Equals(left.Name, right.Name, StringComparison.Ordinal) &&
                   string.Equals(left.Owner, right.Owner, StringComparison.Ordinal) &&
                   string.Equals(left.Url, right.Url, StringComparison.Ordinal) &&
                   string.Equals(
                       left.DefaultBranch,
                       right.DefaultBranch,
                       StringComparison.Ordinal) &&
                   left.IsPrivate == right.IsPrivate &&
                   string.Equals(
                       left.Description,
                       right.Description,
                       StringComparison.Ordinal) &&
                   string.Equals(left.UpdatedAt, right.UpdatedAt, StringComparison.Ordinal) &&
                   string.Equals(
                       left.PackageName,
                       right.PackageName,
                       StringComparison.Ordinal) &&
                   string.Equals(
                       left.DisplayName,
                       right.DisplayName,
                       StringComparison.Ordinal) &&
                   string.Equals(left.Version, right.Version, StringComparison.Ordinal) &&
                   string.Equals(
                       left.PackageDescription,
                       right.PackageDescription,
                       StringComparison.Ordinal) &&
                   string.Equals(
                       left.MinimumUnityVersion,
                       right.MinimumUnityVersion,
                       StringComparison.Ordinal) &&
                   string.Equals(
                       left.AuthorName,
                       right.AuthorName,
                       StringComparison.Ordinal) &&
                   string.Equals(
                       left.DocumentationUrl,
                       right.DocumentationUrl,
                       StringComparison.Ordinal) &&
                   string.Equals(
                       left.ChangelogUrl,
                       right.ChangelogUrl,
                       StringComparison.Ordinal) &&
                   string.Equals(
                       left.LicensesUrl,
                       right.LicensesUrl,
                       StringComparison.Ordinal) &&
                   DependenciesEqual(left.Dependencies, right.Dependencies) &&
                   string.Equals(
                       left.PackageManifestBlobOid,
                       right.PackageManifestBlobOid,
                       StringComparison.Ordinal);
        }

        private static bool DependenciesEqual(
            IReadOnlyList<PackageManifestDependency> left,
            IReadOnlyList<PackageManifestDependency> right)
        {
            if (ReferenceEquals(left, right))
                return true;
            if (left == null || right == null || left.Count != right.Count)
                return false;

            for (int index = 0; index < left.Count; index++)
            {
                PackageManifestDependency leftDependency = left[index];
                PackageManifestDependency rightDependency = right[index];
                if (leftDependency == null || rightDependency == null ||
                    !string.Equals(
                        leftDependency.Name,
                        rightDependency.Name,
                        StringComparison.Ordinal) ||
                    !string.Equals(
                        leftDependency.Version,
                        rightDependency.Version,
                        StringComparison.Ordinal))
                {
                    return false;
                }
            }

            return true;
        }

        private static void OnDiscoverySnapshotChanged()
        {
            PackageManagerGitHubDiscoverySnapshot snapshot =
                PackageManagerGitHubDiscovery.Current;
            if (!HasRetainedHosts() ||
                !ShouldReconcileDiscoverySnapshot(snapshot))
            {
                return;
            }

            Reconcile(snapshot);
        }

        private static void OnSubmoduleSnapshotChanged()
        {
            if (HasRetainedHosts())
                Reconcile(PackageManagerGitHubDiscovery.Current);
        }

        internal static bool IsRepositoryCatalogueChanged(
            IReadOnlyList<PackageManagerGitHubRepository> previousRepositories,
            IReadOnlyList<PackageManagerGitHubRepository> currentRepositories)
        {
            return !ReferenceEquals(previousRepositories, currentRepositories);
        }

        internal static bool DidLastReconcileUpdatePackageDatabase(
            IReadOnlyList<PackageManagerGitHubRepository> repositories)
        {
            lock (Gate)
            {
                return !isShuttingDown &&
                       ReferenceEquals(
                           lastReconcileAttemptRepositories,
                           repositories) &&
                       lastReconcileUpdatedPackageDatabase;
            }
        }

        internal static bool ShouldReconcileDiscoverySnapshot(
            PackageManagerGitHubDiscoverySnapshot snapshot)
        {
            IReadOnlyList<PackageManagerGitHubRepository> repositories =
                snapshot?.Repositories ??
                PackageManagerGitHubDiscoverySnapshot.Empty.Repositories;
            lock (Gate)
            {
                return !isShuttingDown &&
                       IsRepositoryCatalogueChanged(
                           lastCompletedReconcileRepositories,
                           repositories);
            }
        }

        internal static bool ShouldRunPendingProjectionRetry(
            IReadOnlyList<PackageManagerGitHubRepository> pendingRepositories,
            IReadOnlyList<PackageManagerGitHubRepository> currentRepositories,
            bool hasRetainedHosts,
            bool shuttingDown,
            bool reconciliationPending)
        {
            return !shuttingDown &&
                   hasRetainedHosts &&
                   reconciliationPending &&
                   ReferenceEquals(
                       pendingRepositories,
                       currentRepositories);
        }

        internal static bool IsProjectionRetryQueuedForTests(
            IReadOnlyList<PackageManagerGitHubRepository> repositories)
        {
            lock (Gate)
            {
                return projectionRetryQueued &&
                       ReferenceEquals(
                           pendingProjectionRetryRepositories,
                           repositories);
            }
        }

        private static void QueuePendingProjectionRetry(
            IReadOnlyList<PackageManagerGitHubRepository> repositories)
        {
            bool queueDelayCall = false;
            lock (Gate)
            {
                if (isShuttingDown ||
                    RetainedHosts.Count == 0 ||
                    isProjectionRetryInProgress)
                {
                    return;
                }

                if (ReferenceEquals(
                        lastCompletedReconcileRepositories,
                        repositories))
                {
                    // An early reflection/database failure can happen after this
                    // same catalogue was previously projected for older package
                    // state. Make the delayed exact-collection retry eligible.
                    lastCompletedReconcileRepositories = null;
                }

                pendingProjectionRetryRepositories = repositories;
                if (!projectionRetryQueued)
                {
                    projectionRetryQueued = true;
                    queueDelayCall = true;
                }
            }

            if (queueDelayCall)
                EditorApplication.delayCall += RetryPendingProjection;
        }

        private static void CancelPendingProjectionRetry(
            IReadOnlyList<PackageManagerGitHubRepository> repositories)
        {
            bool removeDelayCall;
            lock (Gate)
            {
                if (repositories != null &&
                    !ReferenceEquals(
                        pendingProjectionRetryRepositories,
                        repositories))
                {
                    return;
                }

                removeDelayCall = projectionRetryQueued;
                projectionRetryQueued = false;
                pendingProjectionRetryRepositories = null;
            }

            if (removeDelayCall)
                EditorApplication.delayCall -= RetryPendingProjection;
        }

        private static void RetryPendingProjection()
        {
            EditorApplication.delayCall -= RetryPendingProjection;
            IReadOnlyList<PackageManagerGitHubRepository> pendingRepositories;
            bool hasRetainedHosts;
            bool shuttingDown;
            lock (Gate)
            {
                projectionRetryQueued = false;
                pendingRepositories = pendingProjectionRetryRepositories;
                pendingProjectionRetryRepositories = null;
                hasRetainedHosts = RetainedHosts.Count != 0;
                shuttingDown = isShuttingDown;
                isProjectionRetryInProgress = true;
            }

            try
            {
                PackageManagerGitHubDiscoverySnapshot snapshot =
                    PackageManagerGitHubDiscovery.Current;
                IReadOnlyList<PackageManagerGitHubRepository> currentRepositories =
                    snapshot?.Repositories ??
                    PackageManagerGitHubDiscoverySnapshot.Empty.Repositories;
                if (!ShouldRunPendingProjectionRetry(
                        pendingRepositories,
                        currentRepositories,
                        hasRetainedHosts,
                        shuttingDown,
                        ShouldReconcileDiscoverySnapshot(snapshot)))
                {
                    return;
                }

                Reconcile(snapshot);
            }
            finally
            {
                lock (Gate)
                    isProjectionRetryInProgress = false;
            }
        }

        private static bool HasRetainedHosts()
        {
            lock (Gate)
                return !isShuttingDown && RetainedHosts.Count != 0;
        }

        private static void PurgeStalePackagesOnStartup()
        {
            EditorApplication.delayCall -= PurgeStalePackagesOnStartup;
            if (isShuttingDown)
                return;

            RemoveOwnedPackages();
            if (HasRetainedHosts())
                Reconcile(PackageManagerGitHubDiscovery.Current);
        }

        private static void OnBeforeAssemblyReload()
        {
            Shutdown();
        }

        private static void OnEditorQuitting()
        {
            Shutdown();
        }

        private static void Shutdown()
        {
            lock (Gate)
            {
                if (isShuttingDown)
                    return;
            }

            // Avoid resolving and scanning Package Manager's database during a
            // reload when this domain never projected anything. A failure is
            // harmless: the next domain's delayed startup purge retries it.
            bool hasProjectedState;
            lock (Gate)
            {
                hasProjectedState = RetainedHosts.Count != 0 ||
                                    RepositoryByPackageId.Count != 0;
            }
            if (hasProjectedState)
                RemoveOwnedPackages();

            lock (Gate)
            {
                isShuttingDown = true;
                RetainedHosts.Clear();
                RepositoryByPackageId.Clear();
                lastPackageDatabase = null;
                lastCompletedReconcileRepositories = null;
                lastReconcileAttemptRepositories = null;
                lastReconcileUpdatedPackageDatabase = false;
                pendingProjectionRetryRepositories = null;
                projectionRetryQueued = false;
                isProjectionRetryInProgress = false;
                ProjectedPackageCreationGateForTests = null;
            }

            PackageManagerGitHubDiscovery.SnapshotChanged -= OnDiscoverySnapshotChanged;
            PackageManagerSubmoduleSnapshot.SnapshotChanged -= OnSubmoduleSnapshotChanged;
            AssemblyReloadEvents.beforeAssemblyReload -= OnBeforeAssemblyReload;
            EditorApplication.quitting -= OnEditorQuitting;
            EditorApplication.delayCall -= PurgeStalePackagesOnStartup;
            EditorApplication.delayCall -= RetryPendingProjection;
        }

        private static bool TryGetContract(out ReflectionContract contract)
        {
            lock (Gate)
            {
                contract = supportedContract;
                if (contract != null)
                    return true;
            }

            // Do not cache a failed probe. Package Manager assemblies can load
            // after this class during Editor startup.
            ReflectionContract candidate = ReflectionContract.TryCreate();
            if (candidate == null)
            {
                contract = null;
                return false;
            }

            lock (Gate)
            {
                if (supportedContract == null)
                    supportedContract = candidate;
                contract = supportedContract;
                return true;
            }
        }

        private static bool TryResolvePackageDatabase(
            ReflectionContract contract,
            out object packageDatabase)
        {
            packageDatabase = null;
            try
            {
                packageDatabase = contract.ResolveService(
                    contract.PackageDatabaseType);
                if (packageDatabase == null ||
                    !contract.PackageDatabaseType.IsInstanceOfType(packageDatabase))
                {
                    packageDatabase = null;
                    return false;
                }

                lock (Gate)
                    lastPackageDatabase = packageDatabase;
                return true;
            }
            catch
            {
                packageDatabase = null;
                return false;
            }
        }

        private sealed class ReferenceComparer : IEqualityComparer<object>
        {
            internal static ReferenceComparer Instance { get; } =
                new ReferenceComparer();

            public new bool Equals(object left, object right)
            {
                return ReferenceEquals(left, right);
            }

            public int GetHashCode(object value)
            {
                return RuntimeHelpers.GetHashCode(value);
            }
        }

        private sealed class ReflectionContract
        {
            private ReflectionContract(
                Type servicesContainerType,
                Type packageDatabaseType,
                Type packageFactoryType,
                Type packageType,
                Type packageInterfaceType,
                Type packageVersionInterfaceType,
                Type versionListInterfaceType,
                Type placeholderPackageVersionType,
                Type placeholderVersionListType,
                Type packageTagType,
                PropertyInfo servicesInstanceProperty,
                MethodInfo resolveMethod,
                PropertyInfo allPackagesProperty,
                MethodInfo updatePackagesMethod,
                object packagesChangedSourceOther,
                ConstructorInfo placeholderConstructor,
                ConstructorInfo placeholderVersionListConstructor,
                MethodInfo createPackageMethod,
                PropertyInfo packageNameProperty,
                PropertyInfo packageUniqueIdProperty,
                PropertyInfo packageVersionsProperty,
                PropertyInfo versionListPrimaryProperty,
                PropertyInfo versionNameProperty,
                PropertyInfo versionUniqueIdProperty,
                PropertyInfo versionIsInstalledProperty,
                MethodInfo versionHasTagMethod,
                object placeholderTag,
                object projectionTags,
                FieldInfo versionNameField,
                FieldInfo versionDisplayNameField,
                FieldInfo versionDescriptionField,
                FieldInfo versionMinimumUnityVersionField,
                FieldInfo versionStringField,
                FieldInfo publishedDateTicksField)
            {
                ServicesContainerType = servicesContainerType;
                PackageDatabaseType = packageDatabaseType;
                PackageFactoryType = packageFactoryType;
                PackageType = packageType;
                PackageInterfaceType = packageInterfaceType;
                PackageVersionInterfaceType = packageVersionInterfaceType;
                VersionListInterfaceType = versionListInterfaceType;
                PlaceholderPackageVersionType = placeholderPackageVersionType;
                PlaceholderVersionListType = placeholderVersionListType;
                PackageTagType = packageTagType;
                ServicesInstanceProperty = servicesInstanceProperty;
                ResolveMethod = resolveMethod;
                AllPackagesProperty = allPackagesProperty;
                UpdatePackagesMethod = updatePackagesMethod;
                PackagesChangedSourceOther = packagesChangedSourceOther;
                PlaceholderConstructor = placeholderConstructor;
                PlaceholderVersionListConstructor = placeholderVersionListConstructor;
                CreatePackageMethod = createPackageMethod;
                PackageNameProperty = packageNameProperty;
                PackageUniqueIdProperty = packageUniqueIdProperty;
                PackageVersionsProperty = packageVersionsProperty;
                VersionListPrimaryProperty = versionListPrimaryProperty;
                VersionNameProperty = versionNameProperty;
                VersionUniqueIdProperty = versionUniqueIdProperty;
                VersionIsInstalledProperty = versionIsInstalledProperty;
                VersionHasTagMethod = versionHasTagMethod;
                PlaceholderTag = placeholderTag;
                ProjectionTags = projectionTags;
                VersionNameField = versionNameField;
                VersionDisplayNameField = versionDisplayNameField;
                VersionDescriptionField = versionDescriptionField;
                VersionMinimumUnityVersionField =
                    versionMinimumUnityVersionField;
                VersionStringField = versionStringField;
                PublishedDateTicksField = publishedDateTicksField;
            }

            internal Type ServicesContainerType { get; }
            internal Type PackageDatabaseType { get; }
            internal Type PackageFactoryType { get; }
            internal Type PackageType { get; }
            internal Type PackageInterfaceType { get; }
            internal Type PackageVersionInterfaceType { get; }
            internal Type VersionListInterfaceType { get; }
            internal Type PlaceholderPackageVersionType { get; }
            internal Type PlaceholderVersionListType { get; }
            internal Type PackageTagType { get; }
            private PropertyInfo ServicesInstanceProperty { get; }
            private MethodInfo ResolveMethod { get; }
            private PropertyInfo AllPackagesProperty { get; }
            private MethodInfo UpdatePackagesMethod { get; }
            private object PackagesChangedSourceOther { get; }
            private ConstructorInfo PlaceholderConstructor { get; }
            private ConstructorInfo PlaceholderVersionListConstructor { get; }
            private MethodInfo CreatePackageMethod { get; }
            private PropertyInfo PackageNameProperty { get; }
            private PropertyInfo PackageUniqueIdProperty { get; }
            private PropertyInfo PackageVersionsProperty { get; }
            private PropertyInfo VersionListPrimaryProperty { get; }
            private PropertyInfo VersionNameProperty { get; }
            private PropertyInfo VersionUniqueIdProperty { get; }
            private PropertyInfo VersionIsInstalledProperty { get; }
            private MethodInfo VersionHasTagMethod { get; }
            private object PlaceholderTag { get; }
            private object ProjectionTags { get; }
            private FieldInfo VersionNameField { get; }
            private FieldInfo VersionDisplayNameField { get; }
            private FieldInfo VersionDescriptionField { get; }
            private FieldInfo VersionMinimumUnityVersionField { get; }
            private FieldInfo VersionStringField { get; }
            private FieldInfo PublishedDateTicksField { get; }

            internal static ReflectionContract TryCreate()
            {
                try
                {
                    const string Namespace =
                        "UnityEditor.PackageManager.UI.Internal.";
                    Type servicesContainerType = FindType(Namespace + "ServicesContainer");
                    Type packageDatabaseType = FindType(Namespace + "PackageDatabase");
                    Type packageFactoryType = FindType(Namespace + "PackageFactory");
                    Type packageType = FindType(Namespace + "Package");
                    Type packageInterfaceType = FindType(Namespace + "IPackage");
                    Type packageVersionInterfaceType =
                        FindType(Namespace + "IPackageVersion");
                    Type versionListInterfaceType = FindType(Namespace + "IVersionList");
                    Type placeholderPackageVersionType =
                        FindType(Namespace + "PlaceholderPackageVersion");
                    Type placeholderVersionListType =
                        FindType(Namespace + "PlaceholderVersionList");
                    Type packageTagType = FindType(Namespace + "PackageTag");
                    if (servicesContainerType == null ||
                        packageDatabaseType == null ||
                        packageFactoryType == null ||
                        packageType == null ||
                        packageInterfaceType == null ||
                        packageVersionInterfaceType == null ||
                        versionListInterfaceType == null ||
                        placeholderPackageVersionType == null ||
                        placeholderVersionListType == null ||
                        packageTagType == null ||
                        !packageInterfaceType.IsAssignableFrom(packageType) ||
                        !packageVersionInterfaceType.IsAssignableFrom(
                            placeholderPackageVersionType) ||
                        !versionListInterfaceType.IsAssignableFrom(
                            placeholderVersionListType) ||
                        !packageTagType.IsEnum)
                    {
                        return null;
                    }

                    PropertyInfo servicesInstanceProperty =
                        servicesContainerType.GetProperty("instance", AnyStatic);
                    MethodInfo resolveMethod = FindResolveMethod(servicesContainerType);
                    PropertyInfo allPackagesProperty =
                        packageDatabaseType.GetProperty("allPackages", AnyInstance);
                    MethodInfo updatePackagesMethod = FindUpdatePackagesMethod(
                        packageDatabaseType,
                        packageInterfaceType);
                    object packagesChangedSourceOther =
                        GetOtherChangedSource(updatePackagesMethod);
                    ConstructorInfo placeholderConstructor =
                        FindPlaceholderConstructor(
                            placeholderPackageVersionType,
                            packageTagType);
                    ConstructorInfo placeholderVersionListConstructor =
                        placeholderVersionListType.GetConstructor(
                            AnyInstance,
                            null,
                            new[] { placeholderPackageVersionType },
                            null);
                    MethodInfo createPackageMethod = FindCreatePackageMethod(
                        packageFactoryType,
                        packageInterfaceType,
                        placeholderVersionListType);

                    PropertyInfo packageNameProperty = FindPropertyInTypeOrInterfaces(
                        packageInterfaceType,
                        "name");
                    PropertyInfo packageUniqueIdProperty = FindPropertyInTypeOrInterfaces(
                        packageInterfaceType,
                        "uniqueId");
                    PropertyInfo packageVersionsProperty = FindPropertyInTypeOrInterfaces(
                        packageInterfaceType,
                        "versions");
                    PropertyInfo versionListPrimaryProperty = FindPropertyInTypeOrInterfaces(
                        versionListInterfaceType,
                        "primary");
                    PropertyInfo versionNameProperty = FindPropertyInTypeOrInterfaces(
                        packageVersionInterfaceType,
                        "name");
                    PropertyInfo versionUniqueIdProperty = FindPropertyInTypeOrInterfaces(
                        packageVersionInterfaceType,
                        "uniqueId");
                    PropertyInfo versionIsInstalledProperty = FindPropertyInTypeOrInterfaces(
                        packageVersionInterfaceType,
                        "isInstalled");
                    MethodInfo versionHasTagMethod =
                        packageVersionInterfaceType.GetMethod(
                            "HasTag",
                            AnyInstance,
                            null,
                            new[] { packageTagType },
                            null);

                    object placeholderTag = Enum.Parse(
                        packageTagType,
                        "Placeholder",
                        false);
                    ulong projectionTagBits = Convert.ToUInt64(
                        Enum.Parse(packageTagType, "Git", false),
                        CultureInfo.InvariantCulture) |
                        Convert.ToUInt64(
                            Enum.Parse(packageTagType, "UpmFormat", false),
                            CultureInfo.InvariantCulture);
                    object projectionTags = Enum.ToObject(
                        packageTagType,
                        projectionTagBits);

                    FieldInfo versionNameField = FindFieldInHierarchy(
                        placeholderPackageVersionType,
                        "m_Name");
                    FieldInfo versionDisplayNameField = FindFieldInHierarchy(
                        placeholderPackageVersionType,
                        "m_DisplayName");
                    FieldInfo versionDescriptionField = FindFieldInHierarchy(
                        placeholderPackageVersionType,
                        "m_Description");
                    FieldInfo versionMinimumUnityVersionField =
                        FindFieldInHierarchy(
                            placeholderPackageVersionType,
                            "m_MinimumUnityVersion");
                    FieldInfo versionStringField = FindFieldInHierarchy(
                        placeholderPackageVersionType,
                        "m_VersionString");
                    FieldInfo publishedDateTicksField = FindFieldInHierarchy(
                        placeholderPackageVersionType,
                        "m_PublishedDateTicks");

                    if (servicesInstanceProperty == null ||
                        !servicesContainerType.IsAssignableFrom(
                            servicesInstanceProperty.PropertyType) ||
                        resolveMethod == null ||
                        allPackagesProperty == null ||
                        updatePackagesMethod == null ||
                        (updatePackagesMethod.GetParameters().Length == 3 &&
                         packagesChangedSourceOther == null) ||
                        placeholderConstructor == null ||
                        placeholderVersionListConstructor == null ||
                        createPackageMethod == null ||
                        packageNameProperty == null ||
                        packageUniqueIdProperty == null ||
                        packageVersionsProperty == null ||
                        versionListPrimaryProperty == null ||
                        versionNameProperty == null ||
                        versionUniqueIdProperty == null ||
                        versionIsInstalledProperty == null ||
                        versionHasTagMethod == null ||
                        versionNameField == null ||
                        versionNameField.FieldType != typeof(string) ||
                        versionDisplayNameField == null ||
                        versionDisplayNameField.FieldType != typeof(string) ||
                        versionDescriptionField == null ||
                        versionDescriptionField.FieldType != typeof(string) ||
                        (versionMinimumUnityVersionField != null &&
                         versionMinimumUnityVersionField.FieldType != typeof(string)) ||
                        versionStringField == null ||
                        versionStringField.FieldType != typeof(string) ||
                        publishedDateTicksField == null ||
                        publishedDateTicksField.FieldType != typeof(long))
                    {
                        return null;
                    }

                    return new ReflectionContract(
                        servicesContainerType,
                        packageDatabaseType,
                        packageFactoryType,
                        packageType,
                        packageInterfaceType,
                        packageVersionInterfaceType,
                        versionListInterfaceType,
                        placeholderPackageVersionType,
                        placeholderVersionListType,
                        packageTagType,
                        servicesInstanceProperty,
                        resolveMethod,
                        allPackagesProperty,
                        updatePackagesMethod,
                        packagesChangedSourceOther,
                        placeholderConstructor,
                        placeholderVersionListConstructor,
                        createPackageMethod,
                        packageNameProperty,
                        packageUniqueIdProperty,
                        packageVersionsProperty,
                        versionListPrimaryProperty,
                        versionNameProperty,
                        versionUniqueIdProperty,
                        versionIsInstalledProperty,
                        versionHasTagMethod,
                        placeholderTag,
                        projectionTags,
                        versionNameField,
                        versionDisplayNameField,
                        versionDescriptionField,
                        versionMinimumUnityVersionField,
                        versionStringField,
                        publishedDateTicksField);
                }
                catch
                {
                    return null;
                }
            }

            internal object ResolveService(Type serviceType)
            {
                object services = ServicesInstanceProperty.GetValue(null, null);
                if (services == null ||
                    !ServicesContainerType.IsInstanceOfType(services))
                {
                    return null;
                }

                return ResolveMethod
                    .MakeGenericMethod(serviceType)
                    .Invoke(services, null);
            }

            internal bool TryGetAllPackages(
                object packageDatabase,
                out List<object> packages)
            {
                packages = new List<object>();
                try
                {
                    object value = AllPackagesProperty.GetValue(packageDatabase, null);
                    if (!(value is IEnumerable enumerable))
                        return false;

                    foreach (object package in enumerable)
                    {
                        if (package != null &&
                            PackageInterfaceType.IsInstanceOfType(package))
                        {
                            packages.Add(package);
                        }
                    }

                    return true;
                }
                catch
                {
                    packages.Clear();
                    return false;
                }
            }

            internal bool TryCreateProjectedPackage(
                string packageId,
                PackageManagerGitHubRepository repository,
                out object package)
            {
                package = null;
                try
                {
                    string displayName = !string.IsNullOrWhiteSpace(repository.DisplayName)
                        ? repository.DisplayName.Trim()
                        : !string.IsNullOrWhiteSpace(repository.Name)
                            ? repository.Name.Trim()
                            : repository.PackageName.Trim();
                    string packageName = repository.PackageName.Trim();
                    string version = repository.Version.Trim();
                    string description = !string.IsNullOrWhiteSpace(
                            repository.PackageDescription)
                        ? repository.PackageDescription.Trim()
                        : repository.Description ?? string.Empty;
                    string minimumUnityVersion =
                        repository.MinimumUnityVersion ?? string.Empty;
                    long publishedDateTicks = ParsePublishedDateTicks(
                        repository.UpdatedAt);

                    object placeholder = PlaceholderConstructor.Invoke(
                        new[]
                        {
                            packageId,
                            displayName,
                            version,
                            ProjectionTags,
                            null
                        });
                    VersionNameField.SetValue(placeholder, packageName);
                    VersionDisplayNameField.SetValue(placeholder, displayName);
                    VersionDescriptionField.SetValue(placeholder, description);
                    VersionMinimumUnityVersionField?.SetValue(
                        placeholder,
                        minimumUnityVersion);
                    VersionStringField.SetValue(placeholder, version);
                    PublishedDateTicksField.SetValue(placeholder, publishedDateTicks);

                    object versionList = PlaceholderVersionListConstructor.Invoke(
                        new[] { placeholder });
                    object packageFactory = ResolveService(PackageFactoryType);
                    if (packageFactory == null ||
                        !PackageFactoryType.IsInstanceOfType(packageFactory))
                    {
                        return false;
                    }

                    package = CreatePackageMethod.Invoke(
                        packageFactory,
                        new object[]
                        {
                            packageId,
                            versionList,
                            null,
                            false,
                            false,
                            null,
                            null
                        });
                    if (package == null ||
                        !PackageType.IsInstanceOfType(package) ||
                        !string.Equals(
                            ReadPackageName(package),
                            packageId,
                            StringComparison.Ordinal) ||
                        !string.Equals(
                            ReadPackageUniqueId(package),
                            packageId,
                            StringComparison.Ordinal) ||
                        !IsOwnedPlaceholderPackage(package))
                    {
                        package = null;
                        return false;
                    }

                    return true;
                }
                catch
                {
                    package = null;
                    return false;
                }
            }

            internal bool UpdatePackages(
                object packageDatabase,
                IReadOnlyList<object> addOrUpdate,
                IReadOnlyList<string> removeIds)
            {
                try
                {
                    Array additions = Array.CreateInstance(
                        PackageInterfaceType,
                        addOrUpdate?.Count ?? 0);
                    if (addOrUpdate != null)
                    {
                        for (int index = 0; index < addOrUpdate.Count; index++)
                        {
                            object package = addOrUpdate[index];
                            if (package == null ||
                                !PackageInterfaceType.IsInstanceOfType(package))
                            {
                                return false;
                            }
                            additions.SetValue(package, index);
                        }
                    }

                    string[] removals;
                    if (removeIds == null || removeIds.Count == 0)
                    {
                        removals = Array.Empty<string>();
                    }
                    else
                    {
                        removals = new string[removeIds.Count];
                        for (int index = 0; index < removeIds.Count; index++)
                            removals[index] = removeIds[index];
                    }

                    object[] arguments = UpdatePackagesMethod.GetParameters().Length == 3
                        ? new[]
                        {
                            (object)additions,
                            removals,
                            PackagesChangedSourceOther
                        }
                        : new object[] { additions, removals };
                    UpdatePackagesMethod.Invoke(packageDatabase, arguments);
                    return true;
                }
                catch
                {
                    return false;
                }
            }

            internal string ReadPackageName(object package)
            {
                return ReadString(PackageNameProperty, package);
            }

            internal string ReadPackageUniqueId(object package)
            {
                return ReadString(PackageUniqueIdProperty, package);
            }

            internal string ReadVersionName(object version)
            {
                return ReadString(VersionNameProperty, version);
            }

            internal string ReadUniqueId(object packageOrVersion)
            {
                if (PackageInterfaceType.IsInstanceOfType(packageOrVersion))
                    return ReadPackageUniqueId(packageOrVersion);
                if (PackageVersionInterfaceType.IsInstanceOfType(packageOrVersion))
                    return ReadString(VersionUniqueIdProperty, packageOrVersion);
                return string.Empty;
            }

            internal bool ReadIsInstalled(object version)
            {
                try
                {
                    return PackageVersionInterfaceType.IsInstanceOfType(version) &&
                           VersionIsInstalledProperty.GetValue(version, null) is bool value &&
                           value;
                }
                catch
                {
                    return false;
                }
            }

            internal object ReadPrimaryVersion(object package)
            {
                try
                {
                    object versions = PackageVersionsProperty.GetValue(package, null);
                    if (versions == null ||
                        !VersionListInterfaceType.IsInstanceOfType(versions))
                    {
                        return null;
                    }

                    object primary = VersionListPrimaryProperty.GetValue(versions, null);
                    return primary != null &&
                           PackageVersionInterfaceType.IsInstanceOfType(primary)
                        ? primary
                        : null;
                }
                catch
                {
                    return null;
                }
            }

            internal bool IsOwnedPlaceholderPackage(object package)
            {
                try
                {
                    if (package == null ||
                        !PackageInterfaceType.IsInstanceOfType(package))
                    {
                        return false;
                    }

                    string packageId = ReadPackageUniqueId(package);
                    if (!IsReservedPackageId(packageId) ||
                        !string.Equals(
                            ReadPackageName(package),
                            packageId,
                            StringComparison.Ordinal))
                    {
                        return false;
                    }

                    object primary = ReadPrimaryVersion(package);
                    if (primary == null ||
                        !PlaceholderPackageVersionType.IsInstanceOfType(primary))
                    {
                        return false;
                    }

                    object result = VersionHasTagMethod.Invoke(
                        primary,
                        new[] { PlaceholderTag });
                    return result is bool hasPlaceholderTag && hasPlaceholderTag;
                }
                catch
                {
                    return false;
                }
            }

            private static string ReadString(PropertyInfo property, object target)
            {
                if (property == null || target == null)
                    return string.Empty;
                try
                {
                    return property.GetValue(target, null) as string ?? string.Empty;
                }
                catch
                {
                    return string.Empty;
                }
            }

            private static long ParsePublishedDateTicks(string updatedAt)
            {
                if (string.IsNullOrWhiteSpace(updatedAt))
                    return 0L;

                return DateTimeOffset.TryParse(
                    updatedAt,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out DateTimeOffset value)
                    ? value.UtcDateTime.Ticks
                    : 0L;
            }

            private static Type FindType(string fullName)
            {
                Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
                for (int index = 0; index < assemblies.Length; index++)
                {
                    try
                    {
                        Type type = assemblies[index].GetType(fullName, false);
                        if (type != null)
                            return type;
                    }
                    catch
                    {
                        // Continue probing other loaded assemblies.
                    }
                }

                return null;
            }

            private static MethodInfo FindResolveMethod(Type servicesContainerType)
            {
                MethodInfo[] methods = servicesContainerType.GetMethods(AnyInstance);
                for (int index = 0; index < methods.Length; index++)
                {
                    MethodInfo method = methods[index];
                    if (string.Equals(method.Name, "Resolve", StringComparison.Ordinal) &&
                        method.IsGenericMethodDefinition &&
                        method.GetGenericArguments().Length == 1 &&
                        method.GetParameters().Length == 0)
                    {
                        return method;
                    }
                }

                return null;
            }

            private static MethodInfo FindUpdatePackagesMethod(
                Type packageDatabaseType,
                Type packageInterfaceType)
            {
                MethodInfo twoParameterShape = null;
                MethodInfo[] methods = packageDatabaseType.GetMethods(AnyInstance);
                for (int index = 0; index < methods.Length; index++)
                {
                    MethodInfo method = methods[index];
                    if (!string.Equals(
                            method.Name,
                            "UpdatePackages",
                            StringComparison.Ordinal))
                    {
                        continue;
                    }

                    ParameterInfo[] parameters = method.GetParameters();
                    if ((parameters.Length != 2 && parameters.Length != 3) ||
                        !IsReadOnlyCollectionOf(
                            parameters[0].ParameterType,
                            packageInterfaceType) ||
                        !IsReadOnlyCollectionOf(
                            parameters[1].ParameterType,
                            typeof(string)))
                    {
                        continue;
                    }

                    if (parameters.Length == 3)
                    {
                        if (parameters[2].ParameterType.IsEnum &&
                            Enum.IsDefined(parameters[2].ParameterType, "Other"))
                        {
                            return method;
                        }
                        continue;
                    }

                    twoParameterShape = method;
                }

                return twoParameterShape;
            }

            private static object GetOtherChangedSource(MethodInfo updateMethod)
            {
                if (updateMethod == null)
                    return null;
                ParameterInfo[] parameters = updateMethod.GetParameters();
                return parameters.Length == 3
                    ? Enum.Parse(parameters[2].ParameterType, "Other", false)
                    : null;
            }

            private static bool IsReadOnlyCollectionOf(
                Type candidate,
                Type elementType)
            {
                return candidate != null &&
                       candidate.IsGenericType &&
                       candidate.GetGenericTypeDefinition() ==
                           typeof(IReadOnlyCollection<>) &&
                       candidate.GetGenericArguments()[0] == elementType;
            }

            private static ConstructorInfo FindPlaceholderConstructor(
                Type placeholderType,
                Type packageTagType)
            {
                ConstructorInfo[] constructors = placeholderType.GetConstructors(
                    AnyInstance);
                for (int index = 0; index < constructors.Length; index++)
                {
                    ConstructorInfo constructor = constructors[index];
                    ParameterInfo[] parameters = constructor.GetParameters();
                    if (parameters.Length == 5 &&
                        parameters[0].ParameterType == typeof(string) &&
                        parameters[1].ParameterType == typeof(string) &&
                        parameters[2].ParameterType == typeof(string) &&
                        parameters[3].ParameterType == packageTagType &&
                        !parameters[4].ParameterType.IsValueType)
                    {
                        return constructor;
                    }
                }

                return null;
            }

            private static MethodInfo FindCreatePackageMethod(
                Type packageFactoryType,
                Type packageInterfaceType,
                Type placeholderVersionListType)
            {
                MethodInfo[] methods = packageFactoryType.GetMethods(AnyInstance);
                for (int index = 0; index < methods.Length; index++)
                {
                    MethodInfo method = methods[index];
                    if (!string.Equals(
                            method.Name,
                            "CreatePackage",
                            StringComparison.Ordinal) ||
                        !packageInterfaceType.IsAssignableFrom(method.ReturnType))
                    {
                        continue;
                    }

                    ParameterInfo[] parameters = method.GetParameters();
                    if (parameters.Length == 7 &&
                        parameters[0].ParameterType == typeof(string) &&
                        parameters[1].ParameterType.IsAssignableFrom(
                            placeholderVersionListType) &&
                        !parameters[2].ParameterType.IsValueType &&
                        parameters[3].ParameterType == typeof(bool) &&
                        parameters[4].ParameterType == typeof(bool) &&
                        parameters[5].ParameterType == typeof(string) &&
                        !parameters[6].ParameterType.IsValueType)
                    {
                        return method;
                    }
                }

                return null;
            }

            private static FieldInfo FindFieldInHierarchy(
                Type type,
                string fieldName)
            {
                for (Type current = type; current != null; current = current.BaseType)
                {
                    FieldInfo field = current.GetField(
                        fieldName,
                        AnyInstance | BindingFlags.DeclaredOnly);
                    if (field != null)
                        return field;
                }

                return null;
            }

            private static PropertyInfo FindPropertyInTypeOrInterfaces(
                Type type,
                string propertyName)
            {
                if (type == null)
                    return null;

                PropertyInfo property = type.GetProperty(propertyName, AnyInstance);
                if (property != null)
                    return property;

                Type[] interfaces = type.GetInterfaces();
                for (int index = 0; index < interfaces.Length; index++)
                {
                    property = interfaces[index].GetProperty(
                        propertyName,
                        AnyInstance);
                    if (property != null)
                        return property;
                }

                return null;
            }
        }
    }
}
