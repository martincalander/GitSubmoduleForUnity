using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Reflection;
using NUnit.Framework;

namespace MartinCalander.GitSubmoduleManager.Editor.Tests
{
    [Parallelizable(ParallelScope.None)]
    public sealed class PackageManagerGitHubPackageProjectionTests
    {
        private const BindingFlags StaticMembers =
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        private const BindingFlags InstanceMembers =
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        private static readonly Type ProjectionType =
            typeof(PackageManagerGitHubPackageProjection);

        private readonly List<object> retainedHosts = new List<object>();
        private object reflectionContract;
        private object packageDatabase;
        private bool ownsIsolatedProjection;

        [SetUp]
        public void SetUp()
        {
            retainedHosts.Clear();
            reflectionContract = null;
            packageDatabase = null;
            ownsIsolatedProjection = false;
        }

        [TearDown]
        public void TearDown()
        {
            for (int index = retainedHosts.Count - 1; index >= 0; index--)
            {
                PackageManagerGitHubPackageProjection.ReleaseHost(
                    retainedHosts[index]);
            }
            retainedHosts.Clear();

            if (!ownsIsolatedProjection)
                return;

            if (packageDatabase != null)
            {
                PackageManagerGitHubPackageProjection.RemoveOwnedPackages(
                    packageDatabase);
            }
            PackageManagerGitHubDiscovery.Dispose();
        }

        [Test]
        public void PackageId_IsStableAndScopedToTheOwnedPrefix()
        {
            PackageManagerGitHubRepository original = CreateRepository(
                nodeId: "NODE-STABLE",
                owner: "owner",
                repositoryName: "repository",
                packageName: "com.example.repository",
                displayName: "Repository",
                version: "1.0.0");
            PackageManagerGitHubRepository changedMetadata = CreateRepository(
                nodeId: "NODE-STABLE",
                owner: "renamed-owner",
                repositoryName: "renamed-repository",
                packageName: "com.example.renamed",
                displayName: "Renamed",
                version: "9.9.9");

            string originalId = BuildPackageId(original);
            string changedMetadataId = BuildPackageId(changedMetadata);

            Assert.That(
                originalId,
                Is.EqualTo(
                    PackageManagerGitHubPackageProjection.ReservedPackageIdPrefix +
                    "e31b01ed4f26f4cfb2f4bbed8f99c66c1d887eda3a3f68e1a1e93787f91a1d98"));
            Assert.That(changedMetadataId, Is.EqualTo(originalId),
                "GitHub node identity, rather than mutable repository metadata, owns the projection ID.");

            PackageManagerGitHubRepository fallback = CreateRepository(
                nodeId: string.Empty,
                owner: "OWNER",
                repositoryName: "Repository",
                packageName: "com.example.repository",
                displayName: "Repository",
                version: "1.0.0");
            Assert.That(
                BuildPackageId(fallback),
                Is.EqualTo(
                    PackageManagerGitHubPackageProjection.ReservedPackageIdPrefix +
                    "21da1b566c9f5080441b32f57a37f33e921c5d8be8b686584f9ce819d66732fe"),
                "The owner/name fallback should be normalized and deterministic.");
        }

        [Test]
        public void Reconcile_ProjectsImmutableRepositoryAndPackageMetadata()
        {
            RetainIsolatedHostOrIgnore();

            var source = new GitHubRepo
            {
                NodeId = "NODE-METADATA",
                Owner = "projection-tests",
                Name = "metadata-package",
                Url = "https://github.com/projection-tests/metadata-package.git",
                DefaultBranch = "release",
                IsPrivate = true,
                Description = "Projected package description",
                UpdatedAt = "2026-08-21T10:15:30Z",
                DeclaredPackageName = "com.example.projectionmetadata",
                DeclaredDisplayName = "Projection Metadata",
                DeclaredVersion = "2.3.4",
                PackageManifestBlobOid = "BLOB-METADATA",
                ManifestState = PackageManifestState.Valid
            };
            var immutableRepository = new PackageManagerGitHubRepository(source);
            source.Name = "mutated-after-copy";
            source.Description = "mutated-after-copy";
            source.DeclaredDisplayName = "Mutated After Copy";
            source.DeclaredVersion = "99.0.0";

            Assert.That(
                PackageManagerGitHubPackageProjection.Reconcile(
                    packageDatabase,
                    CreateSnapshot(immutableRepository)),
                Is.True);

            string packageId = BuildPackageId(immutableRepository);
            object projectedPackage = FindPackage(packageId);
            Assert.That(projectedPackage, Is.Not.Null);
            Assert.That(CountPackages(packageId), Is.EqualTo(1));
            Assert.That(
                PackageManagerGitHubPackageProjection.TryGetRepository(
                    projectedPackage,
                    out PackageManagerGitHubRepository mappedRepository),
                Is.True);
            Assert.That(mappedRepository, Is.SameAs(immutableRepository));
            AssertRepositoryMetadata(mappedRepository);
            Assert.That(
                PackageManagerSubmoduleNativePage.GetGroupName(projectedPackage),
                Is.EqualTo("Organization - projection-tests"));

            object primaryVersion = InvokeContract<object>(
                "ReadPrimaryVersion",
                projectedPackage);
            Assert.That(primaryVersion, Is.Not.Null);
            Assert.That(
                PackageManagerGitHubPackageProjection.TryGetRepository(
                    primaryVersion,
                    out PackageManagerGitHubRepository mappedFromVersion),
                Is.True);
            Assert.That(mappedFromVersion, Is.SameAs(immutableRepository));
            Assert.That(
                InvokeContract<bool>("ReadIsInstalled", primaryVersion),
                Is.False);
            Assert.That(
                ReadProjectedField<string>("VersionNameField", primaryVersion),
                Is.EqualTo("com.example.projectionmetadata"));
            Assert.That(
                ReadProjectedField<string>("VersionDisplayNameField", primaryVersion),
                Is.EqualTo("Projection Metadata"));
            Assert.That(
                ReadProjectedField<string>("VersionDescriptionField", primaryVersion),
                Is.EqualTo("Projected package description"));
            Assert.That(
                ReadProjectedField<string>("VersionStringField", primaryVersion),
                Is.EqualTo("2.3.4"));
            Assert.That(
                ReadProjectedField<long>("PublishedDateTicksField", primaryVersion),
                Is.EqualTo(
                    DateTimeOffset.Parse("2026-08-21T10:15:30Z")
                        .UtcDateTime
                        .Ticks));

            Assert.That(
                PackageManagerGitHubPackageProjection.Reconcile(
                    packageDatabase,
                    CreateSnapshot(immutableRepository)),
                Is.True);
            Assert.That(CountPackages(packageId), Is.EqualTo(1),
                "Replaying the same immutable record must not duplicate the owned package.");
        }

        [Test]
        public void Reconcile_ExcludesAnAlreadyInstalledPackageName()
        {
            RetainIsolatedHostOrIgnore();
            string installedName = FindInstalledPackageName();
            if (string.IsNullOrWhiteSpace(installedName))
            {
                Assert.Ignore(
                    "The Package Manager database did not expose an installed UPM package.");
                return;
            }

            PackageManagerGitHubRepository repository = CreateRepository(
                nodeId: "NODE-INSTALLED-EXCLUSION",
                owner: "projection-tests",
                repositoryName: "installed-exclusion",
                packageName: installedName,
                displayName: "Installed Exclusion",
                version: "9.9.9");
            string packageId = BuildPackageId(repository);

            Assert.That(
                PackageManagerGitHubPackageProjection.Reconcile(
                    packageDatabase,
                    CreateSnapshot(repository)),
                Is.True);
            Assert.That(FindPackage(packageId), Is.Null,
                "Discovery must not shadow an installed package with a placeholder.");
            Assert.That(CountOwnedPackages(), Is.EqualTo(0));
        }

        [Test]
        public void ReleaseHost_FinalHostRemovesOwnedPackagesAndSidecarMapping()
        {
            object firstHost = RetainIsolatedHostOrIgnore();
            object secondHost = new object();
            Assert.That(
                PackageManagerGitHubPackageProjection.RetainHost(secondHost),
                Is.True);
            retainedHosts.Add(secondHost);

            PackageManagerGitHubRepository repository = CreateRepository(
                nodeId: "NODE-RELEASE-CLEANUP",
                owner: "projection-tests",
                repositoryName: "release-cleanup",
                packageName: "com.example.projectioncleanup",
                displayName: "Projection Cleanup",
                version: "1.2.3");
            Assert.That(
                PackageManagerGitHubPackageProjection.Reconcile(
                    packageDatabase,
                    CreateSnapshot(repository)),
                Is.True);

            string packageId = BuildPackageId(repository);
            object projectedPackage = FindPackage(packageId);
            Assert.That(projectedPackage, Is.Not.Null);
            string installedPackageId = FindInstalledPackageId();

            Assert.That(
                PackageManagerGitHubPackageProjection.ReleaseHost(firstHost),
                Is.True);
            retainedHosts.Remove(firstHost);
            Assert.That(FindPackage(packageId), Is.Not.Null,
                "A non-final host release must preserve the shared projection.");

            Assert.That(
                PackageManagerGitHubPackageProjection.ReleaseHost(secondHost),
                Is.True);
            retainedHosts.Remove(secondHost);
            Assert.That(FindPackage(packageId), Is.Null);
            Assert.That(CountOwnedPackages(), Is.EqualTo(0));
            Assert.That(
                PackageManagerGitHubPackageProjection.TryGetRepository(
                    projectedPackage,
                    out _),
                Is.False,
                "Final release must retire the sidecar repository mapping.");
            Assert.That(GetRetainedHostCount(), Is.EqualTo(0));
            if (!string.IsNullOrWhiteSpace(installedPackageId))
            {
                Assert.That(FindPackage(installedPackageId), Is.Not.Null,
                    "Owned cleanup must not remove an ordinary installed package.");
            }
        }

        private object RetainIsolatedHostOrIgnore()
        {
            if (GetRetainedHostCount() != 0)
            {
                Assert.Ignore(
                    "A live Package Manager host already owns the shared projection.");
                return null;
            }

            if (!TryResolveProjectionServices(
                    out reflectionContract,
                    out packageDatabase))
            {
                Assert.Ignore(
                    "This Unity version does not expose the guarded Package Manager projection contract.");
                return null;
            }

            PackageManagerGitHubDiscovery.Dispose();
            if (!PackageManagerGitHubPackageProjection.RemoveOwnedPackages(
                    packageDatabase))
            {
                Assert.Ignore(
                    "The Package Manager database was not ready for isolated projection tests.");
                return null;
            }

            ownsIsolatedProjection = true;
            object host = new object();
            bool retained = PackageManagerGitHubPackageProjection.RetainHost(host);
            retainedHosts.Add(host);
            if (!retained)
            {
                Assert.Ignore(
                    "The guarded projection contract could not retain an isolated test host.");
                return null;
            }

            return host;
        }

        private static bool TryResolveProjectionServices(
            out object contract,
            out object database)
        {
            contract = null;
            database = null;
            MethodInfo tryGetContract = ProjectionType.GetMethod(
                "TryGetContract",
                StaticMembers);
            if (tryGetContract == null)
                return false;

            object[] contractArguments = { null };
            if (!(tryGetContract.Invoke(null, contractArguments) is bool supported) ||
                !supported ||
                contractArguments[0] == null)
            {
                return false;
            }

            contract = contractArguments[0];
            PropertyInfo databaseTypeProperty = contract.GetType().GetProperty(
                "PackageDatabaseType",
                InstanceMembers);
            MethodInfo resolveService = contract.GetType().GetMethod(
                "ResolveService",
                InstanceMembers);
            if (!(databaseTypeProperty?.GetValue(contract, null) is Type databaseType) ||
                resolveService == null)
            {
                contract = null;
                return false;
            }

            database = resolveService.Invoke(contract, new object[] { databaseType });
            if (database == null || !databaseType.IsInstanceOfType(database))
            {
                contract = null;
                database = null;
                return false;
            }

            return true;
        }

        private List<object> GetAllPackages()
        {
            MethodInfo method = GetContractMethod("TryGetAllPackages");
            object[] arguments = { packageDatabase, null };
            if (!(method.Invoke(reflectionContract, arguments) is bool succeeded) ||
                !succeeded ||
                !(arguments[1] is IEnumerable packages))
            {
                throw new AssertionException(
                    "The Package Manager database could not enumerate packages.");
            }

            var result = new List<object>();
            foreach (object package in packages)
                result.Add(package);
            return result;
        }

        private object FindPackage(string packageId)
        {
            foreach (object package in GetAllPackages())
            {
                if (string.Equals(
                        InvokeContract<string>("ReadPackageUniqueId", package),
                        packageId,
                        StringComparison.Ordinal))
                {
                    return package;
                }
            }
            return null;
        }

        private int CountPackages(string packageId)
        {
            int count = 0;
            foreach (object package in GetAllPackages())
            {
                if (string.Equals(
                        InvokeContract<string>("ReadPackageUniqueId", package),
                        packageId,
                        StringComparison.Ordinal))
                {
                    count++;
                }
            }
            return count;
        }

        private int CountOwnedPackages()
        {
            int count = 0;
            foreach (object package in GetAllPackages())
            {
                if (InvokeContract<bool>("IsOwnedPlaceholderPackage", package))
                    count++;
            }
            return count;
        }

        private string FindInstalledPackageName()
        {
            foreach (object package in GetAllPackages())
            {
                object primary = InvokeContract<object>(
                    "ReadPrimaryVersion",
                    package);
                if (primary == null ||
                    !InvokeContract<bool>("ReadIsInstalled", primary))
                {
                    continue;
                }

                string packageName = InvokeContract<string>(
                    "ReadVersionName",
                    primary);
                if (GitUtility.IsValidUpmPackageName(packageName))
                    return packageName;
            }
            return string.Empty;
        }

        private string FindInstalledPackageId()
        {
            foreach (object package in GetAllPackages())
            {
                object primary = InvokeContract<object>(
                    "ReadPrimaryVersion",
                    package);
                if (primary != null &&
                    InvokeContract<bool>("ReadIsInstalled", primary))
                {
                    return InvokeContract<string>(
                        "ReadPackageUniqueId",
                        package);
                }
            }
            return string.Empty;
        }

        private T InvokeContract<T>(string methodName, params object[] arguments)
        {
            object value = GetContractMethod(methodName).Invoke(
                reflectionContract,
                arguments);
            return value is T typed ? typed : default;
        }

        private MethodInfo GetContractMethod(string methodName)
        {
            MethodInfo method = reflectionContract?.GetType().GetMethod(
                methodName,
                InstanceMembers);
            if (method == null)
            {
                throw new AssertionException(
                    "Missing projection contract method: " + methodName);
            }
            return method;
        }

        private T ReadProjectedField<T>(string propertyName, object version)
        {
            PropertyInfo property = reflectionContract?.GetType().GetProperty(
                propertyName,
                InstanceMembers);
            if (!(property?.GetValue(reflectionContract, null) is FieldInfo field))
            {
                throw new AssertionException(
                    "Missing projection metadata field: " + propertyName);
            }

            object value = field.GetValue(version);
            return value is T typed ? typed : default;
        }

        private static string BuildPackageId(
            PackageManagerGitHubRepository repository)
        {
            MethodInfo method = ProjectionType.GetMethod(
                "BuildPackageId",
                StaticMembers);
            if (method == null)
                throw new AssertionException("Missing package projection ID builder.");
            return method.Invoke(null, new object[] { repository }) as string ??
                   string.Empty;
        }

        private static int GetRetainedHostCount()
        {
            FieldInfo field = ProjectionType.GetField(
                "RetainedHosts",
                StaticMembers);
            object retained = field?.GetValue(null);
            PropertyInfo countProperty = retained?.GetType().GetProperty(
                "Count",
                InstanceMembers);
            return countProperty?.GetValue(retained, null) is int count
                ? count
                : -1;
        }

        private static PackageManagerGitHubDiscoverySnapshot CreateSnapshot(
            params PackageManagerGitHubRepository[] repositories)
        {
            return new PackageManagerGitHubDiscoverySnapshot(
                new ReadOnlyCollection<PackageManagerGitHubRepository>(
                    repositories ?? Array.Empty<PackageManagerGitHubRepository>()),
                false,
                "Projection test snapshot",
                string.Empty,
                1,
                1,
                1,
                0,
                1);
        }

        private static PackageManagerGitHubRepository CreateRepository(
            string nodeId,
            string owner,
            string repositoryName,
            string packageName,
            string displayName,
            string version)
        {
            return new PackageManagerGitHubRepository(new GitHubRepo
            {
                NodeId = nodeId,
                Owner = owner,
                Name = repositoryName,
                Url = "https://github.com/" + owner + "/" + repositoryName + ".git",
                DefaultBranch = "main",
                Description = "Projection test repository",
                UpdatedAt = "2026-08-21T00:00:00Z",
                DeclaredPackageName = packageName,
                DeclaredDisplayName = displayName,
                DeclaredVersion = version,
                PackageManifestBlobOid = "BLOB-" + nodeId,
                ManifestState = PackageManifestState.Valid
            });
        }

        private static void AssertRepositoryMetadata(
            PackageManagerGitHubRepository repository)
        {
            Assert.That(repository.NodeId, Is.EqualTo("NODE-METADATA"));
            Assert.That(repository.Owner, Is.EqualTo("projection-tests"));
            Assert.That(repository.Name, Is.EqualTo("metadata-package"));
            Assert.That(
                repository.Url,
                Is.EqualTo(
                    "https://github.com/projection-tests/metadata-package.git"));
            Assert.That(repository.DefaultBranch, Is.EqualTo("release"));
            Assert.That(repository.IsPrivate, Is.True);
            Assert.That(
                repository.Description,
                Is.EqualTo("Projected package description"));
            Assert.That(repository.UpdatedAt, Is.EqualTo("2026-08-21T10:15:30Z"));
            Assert.That(
                repository.PackageName,
                Is.EqualTo("com.example.projectionmetadata"));
            Assert.That(repository.DisplayName, Is.EqualTo("Projection Metadata"));
            Assert.That(repository.Version, Is.EqualTo("2.3.4"));
            Assert.That(repository.PackageManifestBlobOid, Is.EqualTo("BLOB-METADATA"));
        }
    }
}
