using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace MartinCalander.GitSubmoduleManager.Editor
{
    internal enum PackageDependencyResolutionStatus
    {
        Pending,
        Resolved,
        Unresolved,
        Ambiguous
    }

    internal enum PackageDependencyCandidateSource
    {
        GitHub,
        UnityRegistry,
        CustomRegistry
    }

    /// <summary>
    /// Immutable registered-package metadata used during graph resolution.
    /// Source is retained for diagnostics. UPM dependency declarations identify
    /// packages by name and version, so an exact installed name/version already
    /// satisfies the requirement regardless of how it was installed.
    /// </summary>
    internal sealed class PackageDependencyRegisteredPackage
    {
        internal PackageDependencyRegisteredPackage(
            string name,
            string version,
            string source)
        {
            Name = name?.Trim() ?? string.Empty;
            Version = version?.Trim() ?? string.Empty;
            Source = source?.Trim() ?? string.Empty;
        }

        internal string Name { get; }
        internal string Version { get; }
        internal string Source { get; }

        internal bool HasCompleteIdentity =>
            GitUtility.IsValidUpmPackageName(Name) &&
            !string.IsNullOrWhiteSpace(Version);
    }

    /// <summary>
    /// One normalized dependency requirement in a prospective install graph.
    /// Multiple parents requesting the same package are represented by the
    /// immutable, deterministically ordered <see cref="RequestedBy"/> list.
    /// </summary>
    internal sealed class PackageDependencyRequirement
    {
        internal PackageDependencyRequirement(
            string name,
            string version,
            IEnumerable<string> requestedBy)
        {
            Name = name?.Trim() ?? string.Empty;
            Version = version?.Trim() ?? string.Empty;
            RequestedBy = new ReadOnlyCollection<string>(
                (requestedBy ?? Array.Empty<string>())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value.Trim())
                .Distinct(StringComparer.Ordinal)
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToArray());
        }

        internal string Name { get; }
        internal string Version { get; }
        internal IReadOnlyList<string> RequestedBy { get; }
    }

    /// <summary>
    /// A source that can satisfy one missing dependency. Repository and
    /// registry identities are copied as strings so a plan never retains a
    /// mutable Package Manager or discovery object.
    /// </summary>
    internal sealed class PackageDependencyCandidate
    {
        internal PackageDependencyCandidate(
            PackageDependencyCandidateSource source,
            string packageName,
            string version,
            string sourceName,
            string repositoryOwner = "",
            string repositoryName = "",
            string repositoryUrl = "",
            string repositoryBranch = "",
            string sourceIdentity = "",
            string dependencyFingerprint = "")
        {
            Source = source;
            PackageName = packageName?.Trim() ?? string.Empty;
            Version = version?.Trim() ?? string.Empty;
            SourceName = sourceName?.Trim() ?? string.Empty;
            RepositoryOwner = repositoryOwner?.Trim() ?? string.Empty;
            RepositoryName = repositoryName?.Trim() ?? string.Empty;
            RepositoryUrl = repositoryUrl?.Trim() ?? string.Empty;
            RepositoryBranch = repositoryBranch?.Trim() ?? string.Empty;
            SourceIdentity = sourceIdentity?.Trim() ?? string.Empty;
            DependencyFingerprint = dependencyFingerprint?.Trim() ?? string.Empty;
        }

        internal PackageDependencyCandidateSource Source { get; }
        internal string PackageName { get; }
        internal string Version { get; }
        internal string SourceName { get; }
        internal string RepositoryOwner { get; }
        internal string RepositoryName { get; }
        internal string RepositoryUrl { get; }
        internal string RepositoryBranch { get; }
        internal string SourceIdentity { get; }
        internal string DependencyFingerprint { get; }
    }

    internal sealed class PackageDependencyResolutionResult
    {
        internal PackageDependencyResolutionResult(
            PackageDependencyRequirement requirement,
            PackageDependencyResolutionStatus status,
            IEnumerable<PackageDependencyCandidate> candidates,
            string message)
        {
            Requirement = requirement;
            Status = status;
            Candidates = new ReadOnlyCollection<PackageDependencyCandidate>(
                (candidates ?? Array.Empty<PackageDependencyCandidate>())
                .Where(candidate => candidate != null)
                .ToArray());
            Message = message ?? string.Empty;
        }

        internal PackageDependencyRequirement Requirement { get; }
        internal PackageDependencyResolutionStatus Status { get; }
        internal IReadOnlyList<PackageDependencyCandidate> Candidates { get; }
        internal string Message { get; }

        internal PackageDependencyCandidate SelectedCandidate =>
            Status == PackageDependencyResolutionStatus.Resolved &&
            Candidates.Count == 1
                ? Candidates[0]
                : null;
    }

    /// <summary>
    /// Immutable publication from the manually ticked resolver. Results are
    /// always ordered by exact package name, independent of discovery, request,
    /// or dependency declaration order.
    /// </summary>
    internal sealed class PackageDependencyResolutionPlan
    {
        private static readonly IReadOnlyList<PackageDependencyResolutionResult>
            EmptyResults = new ReadOnlyCollection<PackageDependencyResolutionResult>(
                Array.Empty<PackageDependencyResolutionResult>());

        internal static PackageDependencyResolutionPlan Empty { get; } =
            new PackageDependencyResolutionPlan(
                EmptyResults,
                false,
                string.Empty,
                0);

        internal PackageDependencyResolutionPlan(
            IEnumerable<PackageDependencyResolutionResult> results,
            bool isComplete,
            string errorMessage,
            int revision)
        {
            Results = new ReadOnlyCollection<PackageDependencyResolutionResult>(
                (results ?? Array.Empty<PackageDependencyResolutionResult>())
                .Where(result => result?.Requirement != null)
                .OrderBy(
                    result => result.Requirement.Name,
                    StringComparer.Ordinal)
                .ToArray());
            IsComplete = isComplete;
            ErrorMessage = errorMessage ?? string.Empty;
            Revision = revision;
        }

        internal IReadOnlyList<PackageDependencyResolutionResult> Results { get; }
        internal bool IsComplete { get; }
        internal string ErrorMessage { get; }
        internal int Revision { get; }

        internal bool HasBlockingIssues =>
            !string.IsNullOrWhiteSpace(ErrorMessage) ||
            Results.Any(result =>
                result.Status == PackageDependencyResolutionStatus.Unresolved ||
                result.Status == PackageDependencyResolutionStatus.Ambiguous);

        internal bool HasMissingDependencies => Results.Count != 0;
    }
}
