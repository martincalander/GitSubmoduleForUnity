using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor.PackageManager;
using UpmPackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace MartinCalander.GitSubmoduleManager.Editor
{
    /// <summary>
    /// Verified state for a direct, read-only UPM Git package. Repository and
    /// requested-revision data comes from the exact project-manifest entry;
    /// resolved commit data comes from Unity's registered PackageInfo.
    /// </summary>
    internal sealed class PackageManagerReadOnlyGitInfo
    {
        internal PackageManagerReadOnlyGitInfo(
            string packageName,
            string repositoryUrl,
            string manifestSpec,
            string revision,
            string resolvedHash,
            UpmPackageInfo packageInfo)
            : this(
                packageName,
                repositoryUrl,
                manifestSpec,
                revision,
                resolvedHash,
                string.Empty,
                packageInfo)
        {
        }

        internal PackageManagerReadOnlyGitInfo(
            string packageName,
            string repositoryUrl,
            string manifestSpec,
            string revision,
            string resolvedHash,
            string packageSubfolder,
            UpmPackageInfo packageInfo)
        {
            PackageName = packageName ?? string.Empty;
            RepositoryUrl = repositoryUrl ?? string.Empty;
            ManifestSpec = manifestSpec ?? string.Empty;
            Revision = revision ?? string.Empty;
            ResolvedHash = resolvedHash ?? string.Empty;
            PackageSubfolder = packageSubfolder ?? string.Empty;
            PackageInfo = packageInfo;
        }

        internal string PackageName { get; }
        internal string RepositoryUrl { get; }
        internal string ManifestSpec { get; }
        internal string Revision { get; }
        internal string ResolvedHash { get; }
        internal string PackageSubfolder { get; }
        internal bool IsRepositoryRootPackage =>
            string.IsNullOrEmpty(PackageSubfolder);
        internal UpmPackageInfo PackageInfo { get; }
    }

    /// <summary>
    /// Maps Package Manager's selected internal package model to Unity's public
    /// registered PackageInfo, then classifies only exact direct Git entries.
    /// Reflection is limited to a small set of property names on Unity-owned UI
    /// models; all authoritative source checks use public Package Manager APIs.
    /// </summary>
    internal static class PackageManagerReadOnlyGitPackage
    {
        private const int MaximumCandidateNameLength = 512;
        private const BindingFlags InstanceMemberFlags =
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private static readonly object RegisteredPackageIndexGate = new object();

        private sealed class RegisteredPackageIndex
        {
            internal readonly Dictionary<string, UpmPackageInfo> ByName =
                new Dictionary<string, UpmPackageInfo>(StringComparer.Ordinal);
            internal readonly HashSet<string> DuplicateNames =
                new HashSet<string>(StringComparer.Ordinal);
        }

        private static RegisteredPackageIndex registeredPackageIndex;
        private static int registeredPackageSnapshotReadCount;
        private static int registeredPackageGeneration;

        // A deterministic seam for focused cache tests. Production code always
        // uses PackageInfo.GetAllRegisteredPackages.
        internal static Func<UpmPackageInfo[]> RegisteredPackagesProviderForTests { get; set; }

        static PackageManagerReadOnlyGitPackage()
        {
            Events.registeredPackages += OnRegisteredPackages;
        }

        internal static int RegisteredPackageSnapshotReadCountForTests
        {
            get
            {
                lock (RegisteredPackageIndexGate)
                    return registeredPackageSnapshotReadCount;
            }
        }

        internal static void ResetRegisteredPackageIndexForTests()
        {
            lock (RegisteredPackageIndexGate)
            {
                registeredPackageIndex = null;
                registeredPackageSnapshotReadCount = 0;
                registeredPackageGeneration++;
                RegisteredPackagesProviderForTests = null;
            }
        }

        internal static void InvalidateRegisteredPackageIndex()
        {
            lock (RegisteredPackageIndexGate)
            {
                registeredPackageIndex = null;
                registeredPackageGeneration++;
            }
        }

        internal static bool TryGetInfo(
            object package,
            out PackageManagerReadOnlyGitInfo info)
        {
            return TryGetInfo(package, out info, out _);
        }

        internal static bool TryGetInfo(
            object package,
            out PackageManagerReadOnlyGitInfo info,
            out string error)
        {
            info = null;
            error = string.Empty;
            if (package == null)
            {
                error = "No Package Manager package is selected.";
                return false;
            }

            if (package is UpmPackageInfo publicPackageInfo)
            {
                return TryCreateInfo(
                    publicPackageInfo,
                    true,
                    out info,
                    out error);
            }

            if (!TryResolveSelectedPackageName(package, out string packageName))
            {
                error = "The selected Package Manager package name could not be resolved.";
                return false;
            }

            return TryGetInfoByPackageName(packageName, out info, out error);
        }

        internal static bool TryGetInfoByPackageName(
            string packageName,
            out PackageManagerReadOnlyGitInfo info)
        {
            return TryGetInfoByPackageName(packageName, out info, out _);
        }

        internal static bool TryGetInfoByPackageName(
            string packageName,
            out PackageManagerReadOnlyGitInfo info,
            out string error)
        {
            info = null;
            error = string.Empty;
            string normalizedName = NormalizePackageName(packageName);
            if (!GitUtility.IsValidUpmPackageName(normalizedName))
            {
                error = "The selected package does not have a valid UPM package name.";
                return false;
            }

            if (!TryGetRegisteredPackage(
                    normalizedName,
                    out UpmPackageInfo matchingPackage,
                    out error))
            {
                return false;
            }

            return TryCreateInfo(
                matchingPackage,
                true,
                out info,
                out error);
        }

        internal static bool TryGetRegisteredPackage(
            string packageName,
            out UpmPackageInfo packageInfo,
            out string error)
        {
            packageInfo = null;
            error = string.Empty;
            string normalizedName = NormalizePackageName(packageName);
            if (!GitUtility.IsValidUpmPackageName(normalizedName))
            {
                error = "The selected package does not have a valid UPM package name.";
                return false;
            }

            if (!TryGetRegisteredPackageIndex(
                    out RegisteredPackageIndex index,
                    out error))
            {
                return false;
            }

            if (index.DuplicateNames.Contains(normalizedName))
            {
                error =
                    "Unity reported more than one registered package with the selected name.";
                return false;
            }

            if (!index.ByName.TryGetValue(normalizedName, out packageInfo) ||
                packageInfo == null)
            {
                packageInfo = null;
                error =
                    $"{normalizedName} is not currently registered by Unity Package Manager.";
                return false;
            }

            return true;
        }

        private static bool TryGetRegisteredPackageIndex(
            out RegisteredPackageIndex index,
            out string error)
        {
            lock (RegisteredPackageIndexGate)
            {
                index = registeredPackageIndex;
                if (index != null)
                {
                    error = string.Empty;
                    return true;
                }
            }

            UpmPackageInfo[] registeredPackages;
            int generation;
            try
            {
                Func<UpmPackageInfo[]> provider;
                lock (RegisteredPackageIndexGate)
                {
                    provider = RegisteredPackagesProviderForTests;
                    registeredPackageSnapshotReadCount++;
                    generation = registeredPackageGeneration;
                }

                registeredPackages = provider != null
                    ? provider()
                    : UpmPackageInfo.GetAllRegisteredPackages();
            }
            catch (Exception exception)
            {
                index = null;
                error = GitHubUtility.SanitizeUiDiagnostic(
                    "Unity's registered package list could not be read: " +
                    exception.Message);
                return false;
            }

            if (registeredPackages == null)
            {
                index = null;
                error = "Unity's registered package list is not ready.";
                return false;
            }

            var candidateIndex = new RegisteredPackageIndex();
            foreach (UpmPackageInfo candidate in registeredPackages)
            {
                string candidateName = candidate?.name;
                if (string.IsNullOrEmpty(candidateName) ||
                    candidateIndex.DuplicateNames.Contains(candidateName))
                {
                    continue;
                }

                if (candidateIndex.ByName.ContainsKey(candidateName))
                {
                    candidateIndex.ByName.Remove(candidateName);
                    candidateIndex.DuplicateNames.Add(candidateName);
                    continue;
                }

                candidateIndex.ByName.Add(candidateName, candidate);
            }

            lock (RegisteredPackageIndexGate)
            {
                if (generation != registeredPackageGeneration)
                {
                    index = null;
                    error =
                        "Unity's registered package list changed while it was being indexed.";
                    return false;
                }

                if (registeredPackageIndex == null)
                    registeredPackageIndex = candidateIndex;
                index = registeredPackageIndex;
            }

            error = string.Empty;
            return true;
        }

        private static void OnRegisteredPackages(PackageRegistrationEventArgs _)
        {
            InvalidateRegisteredPackageIndex();
            PackageManifestGitDependencyStore.InvalidateProjectReadCache();
            PackageManagerGitHubNativePresentationPatch
                .QueueForcedPackageStateRefresh();
        }

        internal static bool TryCreateInfo(
            UpmPackageInfo packageInfo,
            out PackageManagerReadOnlyGitInfo info,
            out string error)
        {
            return TryCreateInfo(
                packageInfo,
                false,
                out info,
                out error);
        }

        private static bool TryCreateInfo(
            UpmPackageInfo packageInfo,
            bool usePresentationManifestCache,
            out PackageManagerReadOnlyGitInfo info,
            out string error)
        {
            info = null;
            error = string.Empty;
            if (packageInfo == null ||
                !GitUtility.IsValidUpmPackageName(packageInfo.name))
            {
                error = "Unity returned an invalid registered package.";
                return false;
            }

            if (packageInfo.source != PackageSource.Git || !packageInfo.isDirectDependency)
            {
                error =
                    $"{packageInfo.name} is not a direct read-only Git dependency of this project.";
                return false;
            }

            PackageManifestGitDependency dependency;
            bool foundDependency = usePresentationManifestCache
                ? PackageManifestGitDependencyStore.TryGetProjectDependencyForPresentation(
                    packageInfo.name,
                    out dependency,
                    out error)
                : PackageManifestGitDependencyStore.TryGetProjectDependency(
                    packageInfo.name,
                    out dependency,
                    out error);
            if (!foundDependency)
            {
                return false;
            }

            if (TryReadMember(
                    packageInfo,
                    "projectDependenciesEntry",
                    out object projectEntryValue) &&
                projectEntryValue is string projectEntry &&
                !string.IsNullOrEmpty(projectEntry) &&
                !string.Equals(projectEntry, dependency.Spec, StringComparison.Ordinal))
            {
                error =
                    $"Unity's registered dependency entry for {packageInfo.name} no longer matches Packages/manifest.json.";
                return false;
            }

            string resolvedHash = packageInfo.git?.hash ?? string.Empty;
            info = new PackageManagerReadOnlyGitInfo(
                packageInfo.name,
                dependency.RepositoryUrl,
                dependency.Spec,
                dependency.Revision,
                resolvedHash,
                dependency.PackageSubfolder,
                packageInfo);
            return true;
        }

        internal static bool HasExactManifestSpec(
            UpmPackageInfo packageInfo,
            string expectedSpec)
        {
            if (packageInfo == null ||
                !packageInfo.isDirectDependency ||
                !GitUtility.IsValidUpmPackageName(packageInfo.name) ||
                !PackageManifestGitDependencyStore.TryGetProjectDependency(
                    packageInfo.name,
                    out PackageManifestGitDependency dependency,
                    out _) ||
                !string.Equals(dependency.Spec, expectedSpec, StringComparison.Ordinal))
            {
                return false;
            }

            // projectDependenciesEntry exists in Unity 2021.3 but is absent in
            // newer public PackageInfo surfaces. When available it is an extra
            // exact-match assertion; otherwise the bounded manifest read above
            // remains the authoritative direct dependency check.
            if (TryReadMember(
                    packageInfo,
                    "projectDependenciesEntry",
                    out object projectEntryValue) &&
                projectEntryValue is string projectEntry &&
                !string.IsNullOrEmpty(projectEntry))
            {
                return string.Equals(projectEntry, expectedSpec, StringComparison.Ordinal);
            }

            return true;
        }

        internal static bool TryResolveSelectedPackageName(
            object package,
            out string packageName)
        {
            packageName = string.Empty;
            if (package == null)
                return false;

            // The native Package Manager selection normally exposes
            // package.versions.primary.name. Prefer that selected version over
            // package-level identifiers, which can be placeholder unique IDs.
            if (TryReadMember(package, "versions", out object versions))
            {
                if (TryReadMember(versions, "primary", out object primary) &&
                    TryReadPackageName(primary, out packageName))
                {
                    return true;
                }

                if (TryReadMember(versions, "installed", out object installed) &&
                    TryReadPackageName(installed, out packageName))
                {
                    return true;
                }
            }

            return TryReadPackageName(package, out packageName);
        }

        private static bool TryReadPackageName(object value, out string packageName)
        {
            packageName = string.Empty;
            if (value == null)
                return false;

            string[] memberNames = { "name", "packageUniqueId", "uniqueId" };
            foreach (string memberName in memberNames)
            {
                if (!TryReadMember(value, memberName, out object candidate) ||
                    !(candidate is string candidateText))
                {
                    continue;
                }

                string normalized = NormalizePackageName(candidateText);
                if (GitUtility.IsValidUpmPackageName(normalized))
                {
                    packageName = normalized;
                    return true;
                }
            }

            return false;
        }

        private static bool TryReadMember(
            object instance,
            string memberName,
            out object value)
        {
            value = null;
            if (instance == null || string.IsNullOrEmpty(memberName))
                return false;

            try
            {
                Type type = instance.GetType();
                PropertyInfo property = type.GetProperty(memberName, InstanceMemberFlags);
                if (property != null && property.GetIndexParameters().Length == 0)
                {
                    value = property.GetValue(instance, null);
                    return value != null;
                }

                FieldInfo field = type.GetField(memberName, InstanceMemberFlags);
                if (field != null)
                {
                    value = field.GetValue(instance);
                    return value != null;
                }
            }
            catch
            {
                // A Unity UI model can disappear during a Package Manager
                // refresh. Classification simply fails closed and retries on
                // the next native refresh callback.
            }

            return false;
        }

        private static string NormalizePackageName(string value)
        {
            if (string.IsNullOrWhiteSpace(value) ||
                value.Length > MaximumCandidateNameLength)
            {
                return string.Empty;
            }

            string candidate = value.Trim();
            int atIndex = candidate.IndexOf('@');
            if (atIndex > 0)
                candidate = candidate.Substring(0, atIndex);
            return candidate;
        }
    }
}
