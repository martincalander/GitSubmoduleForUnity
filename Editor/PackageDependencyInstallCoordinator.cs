using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEngine;
using UpmPackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace MartinCalander.GitSubmoduleManager.Editor
{
    internal enum PackageDependencyInstalledSource
    {
        Unknown,
        Embedded,
        Git,
        Other
    }

    internal sealed class PackageDependencyInstalledPackage
    {
        internal PackageDependencyInstalledPackage(
            string name,
            string version,
            PackageDependencyInstalledSource source,
            bool isDirectDependency,
            string resolvedPath,
            string repositoryUrl = "",
            string revision = "",
            bool hasVerifiedRepositoryIdentity = false)
        {
            Name = name?.Trim() ?? string.Empty;
            Version = version?.Trim() ?? string.Empty;
            Source = source;
            IsDirectDependency = isDirectDependency;
            ResolvedPath = resolvedPath?.Trim() ?? string.Empty;
            RepositoryUrl = repositoryUrl?.Trim() ?? string.Empty;
            Revision = revision?.Trim() ?? string.Empty;
            HasVerifiedRepositoryIdentity = hasVerifiedRepositoryIdentity;
        }

        internal string Name { get; }
        internal string Version { get; }
        internal PackageDependencyInstalledSource Source { get; }
        internal bool IsDirectDependency { get; }
        internal string ResolvedPath { get; }
        internal string RepositoryUrl { get; }
        internal string Revision { get; }
        internal bool HasVerifiedRepositoryIdentity { get; }
    }

    internal sealed class PackageDependencyInstallStep
    {
        internal PackageDependencyInstallStep(
            string packageName,
            string version,
            string repositoryUrl,
            string revision,
            bool isRoot,
            string dependencyFingerprint = "")
        {
            PackageName = packageName?.Trim() ?? string.Empty;
            Version = version?.Trim() ?? string.Empty;
            RepositoryUrl = repositoryUrl?.Trim() ?? string.Empty;
            Revision = revision?.Trim() ?? string.Empty;
            IsRoot = isRoot;
            DependencyFingerprint = dependencyFingerprint?.Trim() ?? string.Empty;
        }

        internal string PackageName { get; }
        internal string Version { get; }
        internal string RepositoryUrl { get; }
        internal string Revision { get; }
        internal bool IsRoot { get; }
        internal string DependencyFingerprint { get; }
    }

    internal sealed class PackageDependencyPrimitiveCompletion
    {
        internal PackageDependencyPrimitiveCompletion(
            bool success,
            string packageName,
            string message)
        {
            Success = success;
            PackageName = packageName?.Trim() ?? string.Empty;
            Message = message ?? string.Empty;
        }

        internal bool Success { get; }
        internal string PackageName { get; }
        internal string Message { get; }
    }

    internal sealed class PackageDependencyInstallCompletion
    {
        internal PackageDependencyInstallCompletion(
            bool success,
            string message,
            string rootPackageName,
            PackageManagerGitInstallMode installMode,
            string rootRepositoryUrl = "",
            string rootRevision = "")
        {
            Success = success;
            Message = message ?? string.Empty;
            RootPackageName = rootPackageName?.Trim() ?? string.Empty;
            InstallMode = installMode;
            RootRepositoryUrl = rootRepositoryUrl?.Trim() ?? string.Empty;
            RootRevision = rootRevision?.Trim() ?? string.Empty;
        }

        internal bool Success { get; }
        internal string Message { get; }
        internal string RootPackageName { get; }
        internal PackageManagerGitInstallMode InstallMode { get; }
        internal string RootRepositoryUrl { get; }
        internal string RootRevision { get; }
    }

    internal interface IPackageDependencyInstallExecutor
    {
        bool IsMutationBusy { get; }

        bool IsBusyFor(string packageName);

        bool TryInspectRegisteredPackages(
            out IReadOnlyList<PackageDependencyInstalledPackage> packages,
            out string error);

        bool TryStart(
            PackageDependencyInstallStep step,
            PackageManagerGitInstallMode mode,
            string dependencyInstallOperationId,
            Action<PackageDependencyPrimitiveCompletion> onComplete,
            out string error);
    }

    internal interface IPackageDependencyInstallStateStore
    {
        string LoadActive();
        void SaveActive(string json);
        void ClearActive();
        string LoadCompletion();
        void SaveCompletion(string json);
        void ClearCompletion();
    }

    internal sealed class UnityPackageDependencyInstallExecutor :
        IPackageDependencyInstallExecutor
    {
        internal static UnityPackageDependencyInstallExecutor Instance { get; } =
            new();

        private UnityPackageDependencyInstallExecutor()
        {
        }

        public bool IsMutationBusy =>
            GitOperationService.IsBusy ||
            PackageManagerProjectResolutionService.IsBusy ||
            PackageManagerReadOnlyGitInstallService.IsBusy ||
            PackageManagerSubmoduleSnapshot.IsReaderActive ||
            GitSubmoduleInstallProbe.IsReaderActive ||
            AsyncCommandDrainRegistry.IsDraining;

        public bool IsBusyFor(string packageName)
        {
            string expected = packageName?.Trim() ?? string.Empty;
            if (GitOperationService.IsBusy)
                return true;
            if (PackageManagerProjectResolutionService.IsBusy &&
                string.Equals(
                    PackageManagerProjectResolutionService.ActivePackageName,
                    expected,
                    StringComparison.Ordinal))
            {
                return true;
            }

            return PackageManagerReadOnlyGitInstallService.IsBusy &&
                   string.Equals(
                       PackageManagerReadOnlyGitInstallService.ActivePackageName,
                       expected,
                       StringComparison.Ordinal);
        }

        public bool TryInspectRegisteredPackages(
            out IReadOnlyList<PackageDependencyInstalledPackage> packages,
            out string error)
        {
            packages = Array.Empty<PackageDependencyInstalledPackage>();
            error = string.Empty;
            try
            {
                packages = new ReadOnlyCollection<PackageDependencyInstalledPackage>(
                    (UpmPackageInfo.GetAllRegisteredPackages() ??
                     Array.Empty<UpmPackageInfo>())
                    .Where(package => package != null &&
                                      GitUtility.IsValidUpmPackageName(package.name))
                    .Select(CreateInstalledPackage)
                    .OrderBy(package => package.Name, StringComparer.Ordinal)
                    .ToArray());
                return true;
            }
            catch (Exception exception)
            {
                error = Sanitize(
                    "Unity's registered package list could not be inspected: " +
                    exception.Message);
                return false;
            }
        }

        public bool TryStart(
            PackageDependencyInstallStep step,
            PackageManagerGitInstallMode mode,
            string dependencyInstallOperationId,
            Action<PackageDependencyPrimitiveCompletion> onComplete,
            out string error)
        {
            error = string.Empty;
            if (step == null)
            {
                error = "A dependency install step is required.";
                return false;
            }

            if (mode == PackageManagerGitInstallMode.GitSubmodule)
            {
                return GitSubmoduleAddService.TryStart(
                    step.RepositoryUrl,
                    step.Revision,
                    step.PackageName,
                    step.Version,
                    step.DependencyFingerprint,
                    completion => onComplete?.Invoke(
                        new PackageDependencyPrimitiveCompletion(
                            completion?.Success == true,
                            step.PackageName,
                            completion?.Message)),
                    out error);
            }

            if (mode == PackageManagerGitInstallMode.ReadOnlyPackage)
            {
                return PackageManagerReadOnlyGitInstallService.TryStart(
                    step.RepositoryUrl,
                    step.Revision,
                    step.PackageName,
                    step.Version,
                    step.DependencyFingerprint,
                    dependencyInstallOperationId,
                    completion =>
                    {
                        try
                        {
                            onComplete?.Invoke(
                                new PackageDependencyPrimitiveCompletion(
                                    completion?.Success == true,
                                    step.PackageName,
                                    completion?.Message));
                        }
                        finally
                        {
                            // The primitive's retained completion describes one
                            // dependency step, not the whole coordinated install.
                            PackageManagerReadOnlyGitInstallService
                                .TryConsumeLastCompletion(out _);
                        }
                    },
                    out error);
            }

            error = "The dependency install mode is invalid.";
            return false;
        }

        private static PackageDependencyInstalledSource ConvertSource(
            PackageSource source)
        {
            switch (source)
            {
                case PackageSource.Embedded:
                    return PackageDependencyInstalledSource.Embedded;
                case PackageSource.Git:
                    return PackageDependencyInstalledSource.Git;
                case PackageSource.Unknown:
                    return PackageDependencyInstalledSource.Unknown;
                default:
                    return PackageDependencyInstalledSource.Other;
            }
        }

        private static PackageDependencyInstalledPackage CreateInstalledPackage(
            UpmPackageInfo package)
        {
            string repositoryUrl = string.Empty;
            string revision = string.Empty;
            bool hasVerifiedRepositoryIdentity = false;
            if (package.source == PackageSource.Git &&
                PackageManagerReadOnlyGitPackage.TryCreateInfo(
                    package,
                    out PackageManagerReadOnlyGitInfo gitInfo,
                    out _) &&
                gitInfo.IsRepositoryRootPackage)
            {
                repositoryUrl = gitInfo.RepositoryUrl;
                revision = gitInfo.Revision;
                hasVerifiedRepositoryIdentity =
                    GitUtility.IsValidRepositoryUrl(repositoryUrl) &&
                    !string.IsNullOrWhiteSpace(revision) &&
                    !string.Equals(revision, ".", StringComparison.Ordinal) &&
                    GitUtility.IsValidBranchName(revision);
            }
            else if (package.source == PackageSource.Embedded &&
                     PackageManagerSubmoduleSnapshot.TryGet(
                         package.name,
                         package.resolvedPath,
                         true,
                         out PackageManagerSubmoduleInfo submoduleInfo))
            {
                repositoryUrl = submoduleInfo.RepositoryUrl;
                hasVerifiedRepositoryIdentity =
                    GitUtility.IsValidRepositoryUrl(repositoryUrl);
            }

            return new PackageDependencyInstalledPackage(
                package.name,
                package.version,
                ConvertSource(package.source),
                package.isDirectDependency,
                package.resolvedPath,
                repositoryUrl,
                revision,
                hasVerifiedRepositoryIdentity);
        }

        private static string Sanitize(string value)
        {
            return PackageDependencyResolutionService.SanitizeDiagnostic(value);
        }
    }

    internal sealed class SessionPackageDependencyInstallStateStore :
        IPackageDependencyInstallStateStore
    {
        private const string ActiveStateKey =
            "MartinCalander.GitSubmoduleManager.DependencyInstall.Active.v1";
        private const string CompletionStateKey =
            "MartinCalander.GitSubmoduleManager.DependencyInstall.Completion.v1";

        internal static SessionPackageDependencyInstallStateStore Instance
            { get; } = new();

        private SessionPackageDependencyInstallStateStore()
        {
        }

        public string LoadActive()
        {
            return SessionState.GetString(ActiveStateKey, string.Empty);
        }

        public void SaveActive(string json)
        {
            SessionState.SetString(ActiveStateKey, json ?? string.Empty);
        }

        public void ClearActive()
        {
            SessionState.EraseString(ActiveStateKey);
        }

        public string LoadCompletion()
        {
            return SessionState.GetString(CompletionStateKey, string.Empty);
        }

        public void SaveCompletion(string json)
        {
            SessionState.SetString(CompletionStateKey, json ?? string.Empty);
        }

        public void ClearCompletion()
        {
            SessionState.EraseString(CompletionStateKey);
        }
    }

    /// <summary>
    /// Manually ticked, reload-safe install state machine. Only resolved GitHub
    /// candidates become explicit operations. Registry candidates are omitted
    /// so Unity resolves them transitively from the root package graph.
    /// </summary>
    internal sealed class PackageDependencyInstallCoordinatorCore
    {
        private const int CurrentSchemaVersion = 2;
        private const string PhaseReady = "ready";
        private const string PhaseAttempted = "attempted";
        private const string PhaseWaiting = "waiting";
        private const string PhaseVerifying = "verifying";
        private static readonly long StepTimeoutTicks =
            TimeSpan.FromMinutes(10d).Ticks;

        private enum InstalledMatchStatus
        {
            Expected,
            Unverified,
            Unexpected
        }

        private readonly IPackageDependencyInstallExecutor executor;
        private readonly IPackageDependencyInstallStateStore store;
        private readonly Func<long> utcTicks;
        private readonly Action<PackageDependencyInstallCompletion> onCompleted;
        private PersistedInstallState activeState;
        private Action<PackageDependencyInstallCompletion> activeCallback;

        internal PackageDependencyInstallCoordinatorCore(
            IPackageDependencyInstallExecutor executor,
            IPackageDependencyInstallStateStore store,
            Func<long> utcTicks = null,
            Action<PackageDependencyInstallCompletion> onCompleted = null)
        {
            this.executor = executor ??
                            throw new ArgumentNullException(nameof(executor));
            this.store = store ?? throw new ArgumentNullException(nameof(store));
            this.utcTicks = utcTicks ?? (() => DateTime.UtcNow.Ticks);
            this.onCompleted = onCompleted;
            activeState = LoadActiveState();
        }

        internal bool IsBusy => activeState != null;
        internal string ActiveOperationId =>
            activeState?.OperationId ?? string.Empty;
        internal string ActiveRootPackageName =>
            activeState?.RootPackageName ?? string.Empty;
        internal string ActiveRepositoryUrl =>
            activeState?.RootRepositoryUrl ?? string.Empty;
        internal string ActiveRevision =>
            activeState?.RootRevision ?? string.Empty;
        internal PackageManagerGitInstallMode? ActiveInstallMode =>
            activeState?.InstallMode;
        internal int ActiveStepIndex => activeState?.StepIndex ?? -1;
        internal int ActiveStepCount => activeState?.Steps?.Length ?? 0;
        internal PackageDependencyInstallStep ActiveStep =>
            activeState == null
                ? null
                : FromPersistedStep(activeState.Steps[activeState.StepIndex]);
        internal string ActiveStepPackageName =>
            activeState == null
                ? string.Empty
                : activeState.Steps[activeState.StepIndex].PackageName;

        internal bool TryStart(
            PackageDependencyInstallRequest request,
            PackageDependencyResolutionPlan plan,
            Action<PackageDependencyInstallCompletion> callback,
            out string error)
        {
            error = string.Empty;
            if (IsBusy)
            {
                error = "Another dependency-aware package install is already running.";
                return false;
            }

            string requestError =
                PackageDependencyPreflightRunner.ValidateRequest(request);
            if (!string.IsNullOrEmpty(requestError))
            {
                error = Sanitize(requestError);
                return false;
            }

            if (!TryBuildSteps(request, plan, out var steps, out error))
            {
                error = Sanitize(error);
                return false;
            }

            long now = utcTicks();
            activeState = new PersistedInstallState
            {
                SchemaVersion = CurrentSchemaVersion,
                OperationId = Guid.NewGuid().ToString("N"),
                RootPackageName = request.RootPackageName,
                RootRepositoryUrl = request.RepositoryUrl,
                RootRevision = request.Revision,
                InstallMode = request.InstallMode,
                StepIndex = 0,
                Phase = PhaseReady,
                StartedUtcTicks = now,
                LastProgressUtcTicks = now,
                Steps = steps.Select(ToPersistedStep).ToArray()
            };
            activeCallback = callback;
            try
            {
                store.ClearCompletion();
                SaveActiveState();
                return true;
            }
            catch (Exception exception)
            {
                activeState = null;
                activeCallback = null;
                TryClearActive();
                error = Sanitize(
                    "The dependency install state could not be persisted: " +
                    exception.Message);
                return false;
            }
        }

        internal bool Tick()
        {
            if (activeState == null)
                return false;

            long now = utcTicks();
            PersistedInstallStep persistedStep =
                activeState.Steps[activeState.StepIndex];
            PackageDependencyInstallStep step = FromPersistedStep(persistedStep);
            bool ownsPrimitive =
                !string.Equals(
                    activeState.Phase,
                    PhaseReady,
                    StringComparison.Ordinal);
            bool ownedPrimitiveBusy =
                ownsPrimitive && executor.IsBusyFor(step.PackageName);
            bool timedOut = activeState.LastProgressUtcTicks <= 0L ||
                            (now >= activeState.LastProgressUtcTicks &&
                             now - activeState.LastProgressUtcTicks >=
                             StepTimeoutTicks);

            if (!executor.TryInspectRegisteredPackages(
                    out IReadOnlyList<PackageDependencyInstalledPackage> packages,
                    out string inspectionError))
            {
                if (ownedPrimitiveBusy)
                    return false;

                Finish(
                    false,
                    string.IsNullOrWhiteSpace(inspectionError)
                        ? "Unity's registered package state could not be inspected."
                        : inspectionError);
                return true;
            }

            if (packages == null)
            {
                if (ownedPrimitiveBusy)
                    return false;

                Finish(
                    false,
                    "Unity returned an invalid registered package state.");
                return true;
            }

            PackageDependencyInstalledPackage[] matchingPackages = packages
                .Where(package => package != null &&
                    string.Equals(
                        package.Name,
                        step.PackageName,
                        StringComparison.Ordinal))
                .Take(2)
                .ToArray();
            if (matchingPackages.Length > 1)
            {
                if (ownedPrimitiveBusy ||
                    (executor.IsMutationBusy && !timedOut))
                {
                    return false;
                }

                Finish(
                    false,
                    $"Unity registered {step.PackageName} more than once.");
                return true;
            }

            if (matchingPackages.Length == 1)
            {
                InstalledMatchStatus matchStatus = GetInstalledMatchStatus(
                    matchingPackages[0],
                    step,
                    activeState.InstallMode);
                if (matchStatus == InstalledMatchStatus.Expected)
                {
                    if (executor.IsMutationBusy ||
                        executor.IsBusyFor(step.PackageName))
                    {
                        return false;
                    }

                    Advance();
                    return true;
                }

                if (matchStatus == InstalledMatchStatus.Unverified &&
                    ownsPrimitive && !timedOut)
                {
                    return false;
                }

                if (ownedPrimitiveBusy ||
                    (executor.IsMutationBusy && !timedOut))
                {
                    return false;
                }

                Finish(
                    false,
                    matchStatus == InstalledMatchStatus.Unverified
                        ? $"The exact installed identity of {step.PackageName} could not be verified before the timeout."
                        : $"{step.PackageName} is registered with an unexpected source, version, or repository identity.");
                return true;
            }

            if (timedOut)
            {
                if (ownedPrimitiveBusy)
                    return false;

                Finish(false, "Dependency installation timed out.");
                return true;
            }

            if (string.Equals(activeState.Phase, PhaseReady, StringComparison.Ordinal))
            {
                if (executor.IsMutationBusy)
                    return false;

                activeState.Phase = PhaseAttempted;
                activeState.LastProgressUtcTicks = now;
                SaveActiveStateOrFinish();
                if (activeState == null)
                    return true;

                string operationId = activeState.OperationId;
                bool started = executor.TryStart(
                        step,
                        activeState.InstallMode,
                        activeState.OperationId,
                        NotifyPrimitiveCompletion,
                        out string startError);
                if (activeState == null ||
                    !string.Equals(
                        activeState.OperationId,
                        operationId,
                        StringComparison.Ordinal))
                {
                    return true;
                }

                if (!started)
                {
                    // A synchronous callback is authoritative. Do not replace a
                    // success callback's verifying state with a start failure.
                    if (string.Equals(
                            activeState.Phase,
                            PhaseAttempted,
                            StringComparison.Ordinal))
                    {
                        Finish(
                            false,
                            string.IsNullOrWhiteSpace(startError)
                                ? $"Installation of {step.PackageName} could not be started."
                                : startError);
                    }
                    return true;
                }

                // TryStart implementations may complete synchronously. Only the
                // untouched attempted marker may transition to waiting.
                if (string.Equals(
                        activeState.Phase,
                        PhaseAttempted,
                        StringComparison.Ordinal))
                {
                    activeState.Phase = PhaseWaiting;
                    SaveActiveStateOrFinish();
                }
                return true;
            }

            // Attempted, waiting, and verifying states are never reissued. A
            // callback or authoritative registered state can still arrive after
            // reload, so retain the single-flight marker until its deadline.
            return false;
        }

        internal void NotifyPrimitiveCompletion(
            PackageDependencyPrimitiveCompletion completion)
        {
            if (activeState == null || completion == null ||
                (!string.Equals(activeState.Phase, PhaseAttempted, StringComparison.Ordinal) &&
                 !string.Equals(activeState.Phase, PhaseWaiting, StringComparison.Ordinal)))
            {
                return;
            }

            PersistedInstallStep step = activeState.Steps[activeState.StepIndex];
            if (!string.Equals(
                    step.PackageName,
                    completion.PackageName,
                    StringComparison.Ordinal))
            {
                return;
            }

            if (!completion.Success)
            {
                Finish(
                    false,
                    string.IsNullOrWhiteSpace(completion.Message)
                        ? $"Installation of {step.PackageName} failed."
                        : completion.Message);
                return;
            }

            activeState.Phase = PhaseVerifying;
            activeState.LastProgressUtcTicks = utcTicks();
            SaveActiveStateOrFinish();
        }

        internal bool TryConsumeLastCompletion(
            out PackageDependencyInstallCompletion completion)
        {
            return TryReadLastCompletion(true, out completion);
        }

        internal bool TryGetLastCompletion(
            out PackageDependencyInstallCompletion completion)
        {
            return TryReadLastCompletion(false, out completion);
        }

        private bool TryReadLastCompletion(
            bool consume,
            out PackageDependencyInstallCompletion completion)
        {
            completion = null;
            string json;
            try
            {
                json = store.LoadCompletion();
            }
            catch
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(json))
                return false;

            try
            {
                PersistedCompletion persisted =
                    JsonUtility.FromJson<PersistedCompletion>(json);
                if (persisted == null ||
                    persisted.SchemaVersion != CurrentSchemaVersion ||
                    !GitUtility.IsValidUpmPackageName(persisted.RootPackageName) ||
                    !GitUtility.IsValidRepositoryUrl(
                        persisted.RootRepositoryUrl) ||
                    string.IsNullOrWhiteSpace(persisted.RootRevision) ||
                    string.Equals(
                        persisted.RootRevision,
                        ".",
                        StringComparison.Ordinal) ||
                    !GitUtility.IsValidBranchName(persisted.RootRevision) ||
                    !IsValidMode(persisted.InstallMode))
                {
                    TryClearCompletion();
                    return false;
                }

                if (consume)
                    store.ClearCompletion();

                completion = new PackageDependencyInstallCompletion(
                    persisted.Success,
                    Sanitize(persisted.Message),
                    persisted.RootPackageName,
                    persisted.InstallMode,
                    persisted.RootRepositoryUrl,
                    persisted.RootRevision);
                return true;
            }
            catch
            {
                TryClearCompletion();
                return false;
            }
        }

        internal static bool TryBuildSteps(
            PackageDependencyInstallRequest request,
            PackageDependencyResolutionPlan plan,
            out IReadOnlyList<PackageDependencyInstallStep> steps,
            out string error)
        {
            steps = Array.Empty<PackageDependencyInstallStep>();
            error = string.Empty;
            string requestError =
                PackageDependencyPreflightRunner.ValidateRequest(request);
            if (!string.IsNullOrWhiteSpace(requestError))
            {
                error = requestError;
                return false;
            }

            if (!GitUtility.IsValidSemanticVersion(request.RootVersion))
            {
                error = "The root package version is invalid.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(request.Revision) ||
                string.Equals(request.Revision, ".", StringComparison.Ordinal))
            {
                error = "The root install request must use an explicit Git revision.";
                return false;
            }

            if (plan == null || !plan.IsComplete ||
                plan.HasBlockingIssues || plan.Revision <= 0)
            {
                error = "A complete, unambiguous dependency plan is required.";
                return false;
            }

            var directRequirements =
                new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (PackageManifestDependency dependency in
                     request.Dependencies ?? Array.Empty<PackageManifestDependency>())
            {
                string name = dependency?.Name ?? string.Empty;
                string version = dependency?.Version ?? string.Empty;
                if (!GitUtility.IsValidUpmPackageName(name) ||
                    string.IsNullOrWhiteSpace(version) ||
                    !string.Equals(version, version.Trim(), StringComparison.Ordinal) ||
                    string.Equals(
                        name,
                        request.RootPackageName,
                        StringComparison.Ordinal) ||
                    directRequirements.ContainsKey(name))
                {
                    error =
                        "The root package dependency graph is invalid or contains a duplicate package.";
                    return false;
                }

                directRequirements.Add(name, version);
            }

            PackageDependencyResolutionResult[] results = plan.Results
                .Where(result => result?.Requirement != null)
                .OrderBy(result => result.Requirement.Name, StringComparer.Ordinal)
                .ToArray();
            if (results.Any(result =>
                    result.Status != PackageDependencyResolutionStatus.Resolved ||
                    result.SelectedCandidate == null))
            {
                error = "Every missing dependency must have exactly one resolved source.";
                return false;
            }

            var byName = new Dictionary<string, PackageDependencyResolutionResult>(
                StringComparer.Ordinal);
            var children = new Dictionary<string, SortedSet<string>>(
                StringComparer.Ordinal);
            foreach (PackageDependencyResolutionResult result in results)
            {
                string name = result.Requirement.Name;
                string version = result.Requirement.Version;
                if (!GitUtility.IsValidUpmPackageName(name) ||
                    string.Equals(
                        name,
                        request.RootPackageName,
                        StringComparison.Ordinal) ||
                    string.IsNullOrWhiteSpace(version) ||
                    !string.Equals(version, version.Trim(), StringComparison.Ordinal) ||
                    result.Requirement.RequestedBy == null ||
                    result.Requirement.RequestedBy.Count == 0 ||
                    byName.ContainsKey(name))
                {
                    error = "The dependency plan contains an invalid or duplicate package.";
                    return false;
                }

                PackageDependencyCandidate candidate = result.SelectedCandidate;
                if (!string.Equals(
                        candidate.PackageName,
                        name,
                        StringComparison.Ordinal) ||
                    !string.Equals(
                        candidate.Version,
                        version,
                        StringComparison.Ordinal))
                {
                    error =
                        $"The resolved source for {name} does not match the requested package identity.";
                    return false;
                }

                switch (candidate.Source)
                {
                    case PackageDependencyCandidateSource.GitHub:
                        if (!GitUtility.IsValidRepositoryUrl(
                                candidate.RepositoryUrl) ||
                            string.IsNullOrWhiteSpace(
                                candidate.RepositoryBranch) ||
                            string.Equals(
                                candidate.RepositoryBranch,
                                ".",
                                StringComparison.Ordinal) ||
                            !string.Equals(
                                candidate.RepositoryBranch,
                                candidate.RepositoryBranch.Trim(),
                                StringComparison.Ordinal) ||
                            !GitUtility.IsValidBranchName(
                                candidate.RepositoryBranch) ||
                            !GitUtility.IsValidPackageDependencyFingerprint(
                                candidate.DependencyFingerprint))
                        {
                            error =
                                $"The GitHub source for {name} must provide an explicit valid repository branch and dependency fingerprint.";
                            return false;
                        }
                        break;
                    case PackageDependencyCandidateSource.UnityRegistry:
                    case PackageDependencyCandidateSource.CustomRegistry:
                        if (string.IsNullOrWhiteSpace(candidate.SourceName))
                        {
                            error =
                                $"The registry source for {name} is incomplete.";
                            return false;
                        }
                        break;
                    default:
                        error = $"The resolved source for {name} is invalid.";
                        return false;
                }

                byName.Add(name, result);
            }

            foreach (PackageDependencyResolutionResult result in results)
            {
                string name = result.Requirement.Name;
                bool requestedByRoot = false;
                foreach (string requestedBy in result.Requirement.RequestedBy)
                {
                    if (!GitUtility.IsValidUpmPackageName(requestedBy) ||
                        string.Equals(requestedBy, name, StringComparison.Ordinal))
                    {
                        error =
                            "The dependency plan contains an invalid request edge.";
                        return false;
                    }

                    if (string.Equals(
                            requestedBy,
                            request.RootPackageName,
                            StringComparison.Ordinal))
                    {
                        requestedByRoot = true;
                        if (!directRequirements.TryGetValue(
                                name,
                                out string directVersion) ||
                            !string.Equals(
                                directVersion,
                                result.Requirement.Version,
                                StringComparison.Ordinal))
                        {
                            error =
                                $"The dependency plan for {name} does not match the root install request.";
                            return false;
                        }
                    }
                    else if (!byName.ContainsKey(requestedBy))
                    {
                        error =
                            $"The dependency plan for {name} is orphaned from the root install graph.";
                        return false;
                    }

                    if (!children.TryGetValue(requestedBy, out var values))
                    {
                        values = new SortedSet<string>(StringComparer.Ordinal);
                        children.Add(requestedBy, values);
                    }
                    values.Add(name);
                }

                if (directRequirements.ContainsKey(name) && !requestedByRoot)
                {
                    error =
                        $"The dependency plan for {name} omits its root request edge.";
                    return false;
                }
            }

            var ordered = new List<PackageDependencyInstallStep>();
            var visitState = new Dictionary<string, int>(StringComparer.Ordinal);
            string traversalError = string.Empty;
            bool Visit(string packageName)
            {
                if (visitState.TryGetValue(packageName, out int state))
                {
                    if (state == 1)
                    {
                        traversalError = "The dependency plan contains a cycle.";
                        return false;
                    }
                    return true;
                }

                visitState[packageName] = 1;
                if (children.TryGetValue(packageName, out var dependencies))
                {
                    foreach (string dependency in dependencies)
                    {
                        if (!Visit(dependency))
                            return false;
                    }
                }

                visitState[packageName] = 2;
                if (!byName.TryGetValue(packageName, out var result))
                    return true;

                PackageDependencyCandidate candidate = result.SelectedCandidate;
                if (candidate.Source != PackageDependencyCandidateSource.GitHub)
                    return true;

                ordered.Add(new PackageDependencyInstallStep(
                    packageName,
                    result.Requirement.Version,
                    candidate.RepositoryUrl,
                    candidate.RepositoryBranch,
                    false,
                    candidate.DependencyFingerprint));
                return true;
            }

            if (!Visit(request.RootPackageName))
            {
                error = traversalError;
                return false;
            }
            if (byName.Keys.Any(packageName =>
                    !visitState.TryGetValue(packageName, out int state) ||
                    state != 2))
            {
                error =
                    "The dependency plan contains a package orphaned from the root install graph.";
                return false;
            }

            ordered.Add(new PackageDependencyInstallStep(
                request.RootPackageName,
                request.RootVersion,
                request.RepositoryUrl,
                request.Revision,
                true,
                GitUtility.ComputePackageDependencyFingerprint(
                    request.Dependencies)));
            if (ordered.Count > PackageDependencyResolutionService.MaximumRequirementCount + 1)
            {
                error = "The dependency install plan exceeds the safety limit.";
                return false;
            }

            steps = new ReadOnlyCollection<PackageDependencyInstallStep>(
                ordered.ToArray());
            return true;
        }

        private void Advance()
        {
            activeState.StepIndex++;
            if (activeState.StepIndex >= activeState.Steps.Length)
            {
                Finish(
                    true,
                    $"Installed {activeState.RootPackageName} and its missing dependencies.");
                return;
            }

            activeState.Phase = PhaseReady;
            activeState.LastProgressUtcTicks = utcTicks();
            SaveActiveStateOrFinish();
        }

        private void Finish(bool success, string message)
        {
            if (activeState == null)
                return;

            PackageDependencyInstallCompletion completion =
                new PackageDependencyInstallCompletion(
                    success,
                    Sanitize(message),
                    activeState.RootPackageName,
                    activeState.InstallMode,
                    activeState.RootRepositoryUrl,
                    activeState.RootRevision);
            var persisted = new PersistedCompletion
            {
                SchemaVersion = CurrentSchemaVersion,
                Success = completion.Success,
                Message = completion.Message,
                RootPackageName = completion.RootPackageName,
                RootRepositoryUrl = completion.RootRepositoryUrl,
                RootRevision = completion.RootRevision,
                InstallMode = completion.InstallMode
            };
            Action<PackageDependencyInstallCompletion> callback = activeCallback;
            activeState = null;
            activeCallback = null;
            TryClearActive();
            try
            {
                store.SaveCompletion(JsonUtility.ToJson(persisted));
            }
            catch (Exception exception)
            {
                Debug.LogWarning(Sanitize(
                    "Dependency install completion could not be retained: " +
                    exception.Message));
            }

            Invoke(callback, completion);
            Invoke(onCompleted, completion);
        }

        private void SaveActiveStateOrFinish()
        {
            try
            {
                SaveActiveState();
            }
            catch (Exception exception)
            {
                Finish(
                    false,
                    "Dependency install progress could not be persisted: " +
                    exception.Message);
            }
        }

        private void SaveActiveState()
        {
            if (activeState == null)
                throw new InvalidOperationException("No active install state exists.");
            store.SaveActive(JsonUtility.ToJson(activeState));
        }

        private PersistedInstallState LoadActiveState()
        {
            string json;
            try
            {
                json = store.LoadActive();
            }
            catch
            {
                return null;
            }

            if (string.IsNullOrWhiteSpace(json))
                return null;
            try
            {
                PersistedInstallState state =
                    JsonUtility.FromJson<PersistedInstallState>(json);
                if (!IsValidState(state))
                {
                    store.ClearActive();
                    return null;
                }
                return state;
            }
            catch
            {
                TryClearActive();
                return null;
            }
        }

        private static bool IsValidState(PersistedInstallState state)
        {
            if (state == null ||
                state.SchemaVersion != CurrentSchemaVersion ||
                !Guid.TryParseExact(state.OperationId, "N", out _) ||
                !GitUtility.IsValidUpmPackageName(state.RootPackageName) ||
                !GitUtility.IsValidRepositoryUrl(state.RootRepositoryUrl) ||
                string.IsNullOrWhiteSpace(state.RootRevision) ||
                string.Equals(
                    state.RootRevision,
                    ".",
                    StringComparison.Ordinal) ||
                !GitUtility.IsValidBranchName(state.RootRevision) ||
                !IsValidMode(state.InstallMode) ||
                state.Steps == null ||
                state.Steps.Length == 0 ||
                state.Steps.Length >
                    PackageDependencyResolutionService.MaximumRequirementCount + 1 ||
                state.StepIndex < 0 ||
                state.StepIndex >= state.Steps.Length ||
                (state.Phase != PhaseReady &&
                 state.Phase != PhaseAttempted &&
                 state.Phase != PhaseWaiting &&
                 state.Phase != PhaseVerifying) ||
                state.StartedUtcTicks <= 0L ||
                state.LastProgressUtcTicks <= 0L)
            {
                return false;
            }

            int rootCount = 0;
            var packageNames = new HashSet<string>(StringComparer.Ordinal);
            for (int index = 0; index < state.Steps.Length; index++)
            {
                PersistedInstallStep step = state.Steps[index];
                if (step == null ||
                    !GitUtility.IsValidUpmPackageName(step.PackageName) ||
                    !packageNames.Add(step.PackageName) ||
                    !GitUtility.IsValidSemanticVersion(step.Version) ||
                    !GitUtility.IsValidPackageDependencyFingerprint(
                        step.DependencyFingerprint) ||
                    !GitUtility.IsValidRepositoryUrl(step.RepositoryUrl) ||
                    string.IsNullOrWhiteSpace(step.Revision) ||
                    string.Equals(step.Revision, ".", StringComparison.Ordinal) ||
                    !GitUtility.IsValidBranchName(step.Revision))
                {
                    return false;
                }
                if (step.IsRoot)
                    rootCount++;
                if (step.IsRoot != (index == state.Steps.Length - 1))
                    return false;
            }

            return rootCount == 1 &&
                   string.Equals(
                       state.Steps[state.Steps.Length - 1].PackageName,
                       state.RootPackageName,
                       StringComparison.Ordinal) &&
                   string.Equals(
                       state.Steps[state.Steps.Length - 1].RepositoryUrl,
                       state.RootRepositoryUrl,
                       StringComparison.Ordinal) &&
                   string.Equals(
                       state.Steps[state.Steps.Length - 1].Revision,
                       state.RootRevision,
                       StringComparison.Ordinal);
        }

        private static InstalledMatchStatus GetInstalledMatchStatus(
            PackageDependencyInstalledPackage installed,
            PackageDependencyInstallStep step,
            PackageManagerGitInstallMode mode)
        {
            if (installed == null || step == null)
                return InstalledMatchStatus.Unexpected;
            if (!string.Equals(
                    installed.Name,
                    step.PackageName,
                    StringComparison.Ordinal) ||
                string.IsNullOrWhiteSpace(step.Version) ||
                !string.Equals(
                    installed.Version,
                    step.Version,
                    StringComparison.Ordinal))
            {
                return InstalledMatchStatus.Unexpected;
            }

            if (mode == PackageManagerGitInstallMode.ReadOnlyPackage)
            {
                if (installed.Source != PackageDependencyInstalledSource.Git ||
                    !installed.IsDirectDependency)
                {
                    return InstalledMatchStatus.Unexpected;
                }

                if (!installed.HasVerifiedRepositoryIdentity)
                    return InstalledMatchStatus.Unverified;

                return GitUtility.AreRepositoryUrlsEquivalent(
                           installed.RepositoryUrl,
                           step.RepositoryUrl) &&
                       string.Equals(
                           installed.Revision,
                           step.Revision,
                           StringComparison.Ordinal)
                    ? InstalledMatchStatus.Expected
                    : InstalledMatchStatus.Unexpected;
            }

            if (mode != PackageManagerGitInstallMode.GitSubmodule ||
                installed.Source != PackageDependencyInstalledSource.Embedded ||
                !installed.IsDirectDependency)
            {
                return InstalledMatchStatus.Unexpected;
            }

            string expectedPath = Path.Combine(
                GitUtility.ProjectRoot,
                "Packages",
                step.PackageName);
            string expected =
                PackageManagerSubmoduleSnapshotData.NormalizeFullPath(expectedPath);
            string actual =
                PackageManagerSubmoduleSnapshotData.NormalizeFullPath(
                    installed.ResolvedPath);
            StringComparison comparison = Path.DirectorySeparatorChar == '\\'
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            if (string.IsNullOrEmpty(expected) ||
                !string.Equals(expected, actual, comparison))
            {
                return InstalledMatchStatus.Unexpected;
            }

            if (!installed.HasVerifiedRepositoryIdentity)
                return InstalledMatchStatus.Unverified;

            return GitUtility.AreRepositoryUrlsEquivalent(
                installed.RepositoryUrl,
                step.RepositoryUrl)
                ? InstalledMatchStatus.Expected
                : InstalledMatchStatus.Unexpected;
        }

        private static PersistedInstallStep ToPersistedStep(
            PackageDependencyInstallStep step)
        {
            return new PersistedInstallStep
            {
                PackageName = step.PackageName,
                Version = step.Version,
                RepositoryUrl = step.RepositoryUrl,
                Revision = step.Revision,
                IsRoot = step.IsRoot,
                DependencyFingerprint = step.DependencyFingerprint
            };
        }

        private static PackageDependencyInstallStep FromPersistedStep(
            PersistedInstallStep step)
        {
            return new PackageDependencyInstallStep(
                step.PackageName,
                step.Version,
                step.RepositoryUrl,
                step.Revision,
                step.IsRoot,
                step.DependencyFingerprint);
        }

        private static bool IsValidMode(PackageManagerGitInstallMode mode)
        {
            return mode == PackageManagerGitInstallMode.GitSubmodule ||
                   mode == PackageManagerGitInstallMode.ReadOnlyPackage;
        }

        private static string Sanitize(string value)
        {
            return PackageDependencyResolutionService.SanitizeDiagnostic(value);
        }

        private void TryClearActive()
        {
            try
            {
                store.ClearActive();
            }
            catch
            {
                // A retained state is safer than issuing the mutation again.
            }
        }

        private void TryClearCompletion()
        {
            try
            {
                store.ClearCompletion();
            }
            catch
            {
                // The next consume attempt can retry.
            }
        }

        private static void Invoke(
            Action<PackageDependencyInstallCompletion> handler,
            PackageDependencyInstallCompletion completion)
        {
            if (handler == null)
                return;
            try
            {
                handler(completion);
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
            }
        }

        [Serializable]
        private sealed class PersistedInstallState
        {
            public int SchemaVersion;
            public string OperationId = string.Empty;
            public string RootPackageName = string.Empty;
            public string RootRepositoryUrl = string.Empty;
            public string RootRevision = string.Empty;
            public PackageManagerGitInstallMode InstallMode;
            public int StepIndex;
            public string Phase = string.Empty;
            public long StartedUtcTicks;
            public long LastProgressUtcTicks;
            public PersistedInstallStep[] Steps =
                Array.Empty<PersistedInstallStep>();
        }

        [Serializable]
        private sealed class PersistedInstallStep
        {
            public string PackageName = string.Empty;
            public string Version = string.Empty;
            public string RepositoryUrl = string.Empty;
            public string Revision = string.Empty;
            public bool IsRoot;
            public string DependencyFingerprint = string.Empty;
        }

        [Serializable]
        private sealed class PersistedCompletion
        {
            public int SchemaVersion;
            public bool Success;
            public string Message = string.Empty;
            public string RootPackageName = string.Empty;
            public string RootRepositoryUrl = string.Empty;
            public string RootRevision = string.Empty;
            public PackageManagerGitInstallMode InstallMode;
        }
    }

    /// <summary>
    /// Session-persisted facade consumed by both Package Manager entry points.
    /// Its static update hook resumes the current step after script reloads and
    /// publishes one retained terminal completion.
    /// </summary>
    [InitializeOnLoad]
    internal static class PackageDependencyInstallCoordinator
    {
        private static readonly PackageDependencyInstallCoordinatorCore Core;
        private static bool updateSubscribed;
        private static bool readOnlyCompletionSubscribed;

        internal static event Action<PackageDependencyInstallCompletion> Completed;

        static PackageDependencyInstallCoordinator()
        {
            Core = new PackageDependencyInstallCoordinatorCore(
                UnityPackageDependencyInstallExecutor.Instance,
                SessionPackageDependencyInstallStateStore.Instance,
                null,
                OnCoreCompleted);
            SubscribeUpdateIfBusy();
            UpdateReadOnlyCompletionSubscription();
            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;
        }

        internal static bool IsBusy => Core.IsBusy;
        internal static string ActiveOperationId => Core.ActiveOperationId;
        internal static string ActiveRootPackageName =>
            Core.ActiveRootPackageName;
        internal static string ActiveRepositoryUrl => Core.ActiveRepositoryUrl;
        internal static string ActiveRevision => Core.ActiveRevision;
        internal static PackageManagerGitInstallMode? ActiveInstallMode =>
            Core.ActiveInstallMode;
        internal static int ActiveStepIndex => Core.ActiveStepIndex;
        internal static int ActiveStepCount => Core.ActiveStepCount;
        internal static PackageDependencyInstallStep ActiveStep =>
            Core.ActiveStep;
        internal static string ActiveStepPackageName =>
            Core.ActiveStepPackageName;

        internal static bool TryStart(
            PackageDependencyInstallRequest request,
            PackageDependencyResolutionPlan plan,
            Action<PackageDependencyInstallCompletion> onComplete,
            out string error)
        {
            if (!Core.TryStart(request, plan, onComplete, out error))
                return false;
            SubscribeUpdateIfBusy();
            UpdateReadOnlyCompletionSubscription();
            return true;
        }

        internal static bool TryConsumeLastCompletion(
            out PackageDependencyInstallCompletion completion)
        {
            return Core.TryConsumeLastCompletion(out completion);
        }

        internal static bool TryGetLastCompletion(
            out PackageDependencyInstallCompletion completion)
        {
            return Core.TryGetLastCompletion(out completion);
        }

        private static void Update()
        {
            if (Core.IsBusy)
                Core.Tick();
            if (!Core.IsBusy)
                UnsubscribeUpdate();
            UpdateReadOnlyCompletionSubscription();
        }

        private static void SubscribeUpdateIfBusy()
        {
            if (!Core.IsBusy || updateSubscribed)
                return;

            updateSubscribed = true;
            EditorApplication.update += Update;
        }

        private static void UnsubscribeUpdate()
        {
            if (!updateSubscribed)
                return;

            updateSubscribed = false;
            EditorApplication.update -= Update;
        }

        private static void UpdateReadOnlyCompletionSubscription()
        {
            bool shouldSubscribe = Core.IsBusy &&
                Core.ActiveInstallMode ==
                    PackageManagerGitInstallMode.ReadOnlyPackage;
            if (shouldSubscribe == readOnlyCompletionSubscribed)
                return;

            readOnlyCompletionSubscribed = shouldSubscribe;
            if (shouldSubscribe)
            {
                PackageManagerReadOnlyGitInstallService.Completed +=
                    OnReadOnlyInstallCompleted;
            }
            else
            {
                PackageManagerReadOnlyGitInstallService.Completed -=
                    OnReadOnlyInstallCompleted;
            }
        }

        private static void OnReadOnlyInstallCompleted(
            ReadOnlyGitPackageInstallCompletion completion)
        {
            bool ownsCompletion = completion != null &&
                Core.IsBusy &&
                Core.ActiveInstallMode ==
                    PackageManagerGitInstallMode.ReadOnlyPackage &&
                string.Equals(
                    Core.ActiveOperationId,
                    completion.DependencyInstallOperationId,
                    StringComparison.Ordinal) &&
                string.Equals(
                    Core.ActiveStepPackageName,
                    completion.PackageName,
                    StringComparison.Ordinal);
            if (!ownsCompletion)
                return;

            try
            {
                Core.NotifyPrimitiveCompletion(
                    new PackageDependencyPrimitiveCompletion(
                        completion.Success,
                        completion.PackageName,
                        completion.Message));
            }
            finally
            {
                // After a reload, the primitive's direct callback no longer
                // exists. Consume its step-scoped terminal record here instead.
                PackageManagerReadOnlyGitInstallService
                    .TryConsumeLastCompletion(out _);
            }
        }

        private static void OnCoreCompleted(
            PackageDependencyInstallCompletion completion)
        {
            UnsubscribeUpdate();
            UpdateReadOnlyCompletionSubscription();
            Invoke(Completed, completion);
        }

        private static void Invoke(
            Action<PackageDependencyInstallCompletion> handler,
            PackageDependencyInstallCompletion completion)
        {
            Delegate[] subscribers = handler?.GetInvocationList();
            if (subscribers == null)
                return;
            foreach (Delegate subscriber in subscribers)
            {
                try
                {
                    ((Action<PackageDependencyInstallCompletion>)subscriber)
                        .Invoke(completion);
                }
                catch (Exception exception)
                {
                    Debug.LogException(exception);
                }
            }
        }

        private static void OnBeforeAssemblyReload()
        {
            UnsubscribeUpdate();
            if (readOnlyCompletionSubscribed)
            {
                readOnlyCompletionSubscribed = false;
                PackageManagerReadOnlyGitInstallService.Completed -=
                    OnReadOnlyInstallCompleted;
            }
            AssemblyReloadEvents.beforeAssemblyReload -= OnBeforeAssemblyReload;
        }
    }
}
