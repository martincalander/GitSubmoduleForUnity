using System;
using System.IO;
using System.Text;
using System.Threading;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEngine;
using UpmPackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace MartinCalander.GitSubmoduleManager.Editor
{
    internal sealed class ReadOnlyGitPackageInstallCompletion
    {
        internal ReadOnlyGitPackageInstallCompletion(
            bool success,
            string message,
            string packageName,
            UpmPackageInfo packageInfo,
            string dependencyInstallOperationId = "")
        {
            Success = success;
            Message = message ?? string.Empty;
            PackageName = packageName ?? string.Empty;
            PackageInfo = packageInfo;
            DependencyInstallOperationId =
                dependencyInstallOperationId?.Trim() ?? string.Empty;
        }

        internal bool Success { get; }
        internal string Message { get; }
        internal string PackageName { get; }
        internal UpmPackageInfo PackageInfo { get; }
        internal string DependencyInstallOperationId { get; }
        internal bool IsDependencyInstallPrimitive =>
            Guid.TryParseExact(
                DependencyInstallOperationId,
                "N",
                out _);
    }

    /// <summary>
    /// Starts ordinary Unity Package Manager Git installs by compare-and-swap
    /// insertion into the project manifest followed by Client.Resolve. Intent
    /// is retained in SessionState across script reloads. A completed install
    /// is accepted only when Unity reports the expected direct Git package, the
    /// exact commit whose package metadata was inspected, and the exact pinned
    /// manifest entry. A newly-added mismatched package is removed only by an
    /// exact manifest compare-and-swap, then handed to Unity Package Manager
    /// resolution before terminal failure is reported.
    /// </summary>
    [InitializeOnLoad]
    internal static class PackageManagerReadOnlyGitInstallService
    {
        private const string ActiveStateKey =
            "MartinCalander.GitSubmoduleManager.ReadOnlyGitInstall.Active.v1";
        private const string CompletionStateKey =
            "MartinCalander.GitSubmoduleManager.ReadOnlyGitInstall.Completion.v1";
        private const string RecoveryNotificationStateKey =
            "MartinCalander.GitSubmoduleManager.ReadOnlyGitInstall.RecoveryNotification.v1";
        private const int CurrentActiveStateSchemaVersion = 4;
        private const string StageAddPrepared = "add-prepared";
        private const string StageAdd = "add";
        private const string StageCleanupPrepared = "cleanup-prepared";
        private const string StageCleanup = "cleanup";
        private const string StageRecoveryBlocked = "recovery-blocked";
        private const double RecoveryTimeoutMinutes = 10d;

        private static PersistedInstallState activeState;
        private static Action<ReadOnlyGitPackageInstallCompletion> activeCallback;
        private static bool activeEventsSubscribed;

        internal static event Action<ReadOnlyGitPackageInstallCompletion> Completed;

        static PackageManagerReadOnlyGitInstallService()
        {
            activeState = LoadActiveState();
            SubscribeActiveEvents();
        }

        internal static bool IsBusy => activeState != null;
        internal static bool IsRecoveryBlocked =>
            string.Equals(
                activeState?.Stage,
                StageRecoveryBlocked,
                StringComparison.Ordinal);
        internal static string ActivePackageName =>
            activeState?.ExpectedPackageName ?? string.Empty;
        internal static string ActiveRepositoryUrl =>
            activeState?.RepositoryUrl ?? string.Empty;
        internal static string ActiveRevision =>
            activeState?.Revision ?? string.Empty;

        internal static string BuildUnavailableMessage()
        {
            if (IsRecoveryBlocked)
                return activeState.FailureMessage;
            if (IsBusy)
                return $"Installing {ActivePackageName} as a read-only package...";
            if (PackageManagerProjectResolutionService.IsBusy)
                return PackageManagerProjectResolutionService.BuildUnavailableMessage();
            if (GitOperationService.IsBusy)
                return "Wait for the current Git package operation to finish.";
            if (PackageManagerSubmoduleSnapshot.IsReaderActive ||
                GitSubmoduleInstallProbe.IsReaderActive ||
                AsyncCommandDrainRegistry.IsDraining)
            {
                return "Wait for the current package or repository inspection to finish.";
            }

            if (!string.IsNullOrWhiteSpace(GitOperationService.RecoveryWarning))
                return "Review the pending Git package recovery warning before installing another package.";
            return string.Empty;
        }

        internal static string ValidateInput(
            string repositoryUrl,
            string revision,
            string expectedPackageName)
        {
            string unavailable = BuildUnavailableMessage();
            if (!string.IsNullOrEmpty(unavailable))
                return unavailable;

            if (!GitUtility.IsValidUpmPackageName(expectedPackageName))
                return "A valid reverse-domain UPM package name is required.";

            if (!PackageManifestGitDependencyStore.TryBuildGitSpec(
                    repositoryUrl,
                    revision,
                    out _,
                    out string specError))
            {
                return specError;
            }

            string embeddedPath = Path.Combine(
                GitUtility.ProjectRoot,
                "Packages",
                expectedPackageName);
            if (!GitUtility.TryInspectFileSystemEntryPresence(
                    embeddedPath,
                    out bool embeddedEntryExists,
                    out string inspectionError,
                    CancellationToken.None))
            {
                return inspectionError;
            }

            if (embeddedEntryExists)
            {
                return
                    $"Packages/{expectedPackageName} already exists. Remove or convert the embedded package first.";
            }

            try
            {
                UpmPackageInfo[] registeredPackages = UpmPackageInfo.GetAllRegisteredPackages();
                if (registeredPackages != null)
                {
                    foreach (UpmPackageInfo package in registeredPackages)
                    {
                        if (package != null &&
                            package.isDirectDependency &&
                            string.Equals(
                                package.name,
                                expectedPackageName,
                                StringComparison.Ordinal))
                        {
                            return $"{expectedPackageName} is already a direct project dependency.";
                        }
                    }
                }
            }
            catch (Exception exception)
            {
                return SanitizeMessage(
                    "Unity's registered package list could not be inspected: " +
                    exception.Message);
            }

            return string.Empty;
        }

        internal static bool TryStart(
            string repositoryUrl,
            string revision,
            string expectedPackageName,
            Action<ReadOnlyGitPackageInstallCompletion> onComplete,
            out string error)
        {
            return TryStart(
                repositoryUrl,
                revision,
                expectedPackageName,
                string.Empty,
                onComplete,
                out error);
        }

        internal static bool TryStart(
            string repositoryUrl,
            string revision,
            string expectedPackageName,
            string expectedVersion,
            Action<ReadOnlyGitPackageInstallCompletion> onComplete,
            out string error)
        {
            return TryStart(
                repositoryUrl,
                revision,
                expectedPackageName,
                expectedVersion,
                string.Empty,
                onComplete,
                out error);
        }

        internal static bool TryStart(
            string repositoryUrl,
            string revision,
            string expectedPackageName,
            string expectedVersion,
            string expectedDependencyFingerprint,
            Action<ReadOnlyGitPackageInstallCompletion> onComplete,
            out string error)
        {
            return TryStart(
                repositoryUrl,
                revision,
                expectedPackageName,
                expectedVersion,
                expectedDependencyFingerprint,
                string.Empty,
                onComplete,
                out error);
        }

        internal static bool TryStart(
            string repositoryUrl,
            string revision,
            string expectedPackageName,
            string expectedVersion,
            string expectedDependencyFingerprint,
            string dependencyInstallOperationId,
            Action<ReadOnlyGitPackageInstallCompletion> onComplete,
            out string error)
        {
            return TryStart(
                repositoryUrl,
                revision,
                expectedPackageName,
                expectedVersion,
                expectedDependencyFingerprint,
                PackageManifestMetaVerification.Unverified,
                string.Empty,
                string.Empty,
                dependencyInstallOperationId,
                onComplete,
                out error);
        }

        internal static bool TryStart(
            string repositoryUrl,
            string revision,
            string expectedPackageName,
            string expectedVersion,
            string expectedDependencyFingerprint,
            PackageManifestMetaVerification packageManifestMetaVerification,
            string expectedPackageManifestMetaGuid,
            string dependencyInstallOperationId,
            Action<ReadOnlyGitPackageInstallCompletion> onComplete,
            out string error)
        {
            return TryStart(
                repositoryUrl,
                revision,
                expectedPackageName,
                expectedVersion,
                expectedDependencyFingerprint,
                packageManifestMetaVerification,
                expectedPackageManifestMetaGuid,
                string.Empty,
                dependencyInstallOperationId,
                onComplete,
                out error);
        }

        internal static bool TryStart(
            string repositoryUrl,
            string revision,
            string expectedPackageName,
            string expectedVersion,
            string expectedDependencyFingerprint,
            PackageManifestMetaVerification packageManifestMetaVerification,
            string expectedPackageManifestMetaGuid,
            string expectedInspectedCommit,
            string dependencyInstallOperationId,
            Action<ReadOnlyGitPackageInstallCompletion> onComplete,
            out string error)
        {
            error = ValidateInput(repositoryUrl, revision, expectedPackageName);
            if (!string.IsNullOrEmpty(error))
                return false;
            string version = expectedVersion?.Trim() ?? string.Empty;
            if (!string.IsNullOrEmpty(version) &&
                !GitUtility.IsValidSemanticVersion(version))
            {
                error = "The expected package version must be valid SemVer 2.0.";
                return false;
            }
            string dependencyFingerprint =
                expectedDependencyFingerprint?.Trim() ?? string.Empty;
            if (!string.IsNullOrEmpty(dependencyFingerprint) &&
                !GitUtility.IsValidPackageDependencyFingerprint(
                    dependencyFingerprint))
            {
                error = "The expected package dependency fingerprint is invalid.";
                return false;
            }
            string metaGuid = expectedPackageManifestMetaGuid?.Trim() ??
                              string.Empty;
            if (!TryValidatePackageManifestMetaEvidence(
                    packageManifestMetaVerification,
                    metaGuid,
                    out error))
            {
                return false;
            }
            string inspectedCommit = expectedInspectedCommit?.Trim() ??
                                     string.Empty;
            if (!GitUtility.IsValidGitObjectId(inspectedCommit))
            {
                error =
                    "Read-only Git installs require the exact inspected Git commit.";
                return false;
            }
            string operationId =
                dependencyInstallOperationId?.Trim() ?? string.Empty;
            if (!string.IsNullOrEmpty(operationId) &&
                !Guid.TryParseExact(operationId, "N", out _))
            {
                error = "The dependency install operation identity is invalid.";
                return false;
            }
            if (!string.IsNullOrEmpty(operationId) &&
                packageManifestMetaVerification !=
                    PackageManifestMetaVerification.Verified)
            {
                error =
                    "Coordinated read-only Git installs require verified package.json.meta evidence.";
                return false;
            }

            if (!PackageManifestGitDependencyStore.TryBuildGitSpec(
                    repositoryUrl,
                    inspectedCommit,
                    out string spec,
                    out error))
            {
                return false;
            }

            string installResolutionOperationId =
                Guid.NewGuid().ToString("N");

            activeState = new PersistedInstallState
            {
                SchemaVersion = CurrentActiveStateSchemaVersion,
                Stage = StageAddPrepared,
                RepositoryUrl = repositoryUrl.Trim(),
                Revision = revision?.Trim() ?? string.Empty,
                ExpectedPackageName = expectedPackageName,
                ExpectedVersion = version,
                ExpectedDependencyFingerprint = dependencyFingerprint,
                PackageManifestMetaVerification =
                    packageManifestMetaVerification,
                ExpectedPackageManifestMetaGuid = metaGuid,
                ExpectedInspectedCommit = inspectedCommit.ToLowerInvariant(),
                DependencyInstallOperationId = operationId,
                InstallResolutionOperationId =
                    installResolutionOperationId,
                Spec = spec,
                StartedUtcTicks = DateTime.UtcNow.Ticks
            };
            activeCallback = onComplete;

            PackageManifestDependencyMutation addMutation = null;
            bool resolutionPrepared = false;
            bool manifestEntryOwned = false;
            try
            {
                SessionState.SetBool(RecoveryNotificationStateKey, false);
                SubscribeActiveEvents();

                // Persist write-ahead intent before the manifest CAS. A reload
                // of this stage is recovery-blocked and never repeats insertion.
                SaveActiveState();

                if (!PackageManagerProjectResolutionService.TryPrepare(
                        installResolutionOperationId,
                        expectedPackageName,
                        PackageManagerResolutionExpectation.Git,
                        out error))
                {
                    ClearActiveState();
                    return false;
                }
                resolutionPrepared = true;

                if (!TryAcquireExactManifestDependencyAtPath(
                        PackageManifestGitDependencyStore.ManifestPath,
                        expectedPackageName,
                        spec,
                        out addMutation,
                        out error))
                {
                    PackageManagerProjectResolutionService.CancelPrepared(
                        installResolutionOperationId);
                    ClearActiveState();
                    return false;
                }

                manifestEntryOwned = true;
                activeState.OwnsManifestEntry = true;
                activeState.Stage = StageAdd;
                SaveActiveState();
                return true;
            }
            catch (Exception exception)
            {
                string rollbackError = string.Empty;
                bool rolledBack = !manifestEntryOwned ||
                                  addMutation?.TryRollback(
                                      out rollbackError) == true;
                if (rolledBack)
                {
                    if (resolutionPrepared)
                    {
                        PackageManagerProjectResolutionService.CancelPrepared(
                            installResolutionOperationId);
                    }
                    ClearActiveState();
                    error = SanitizeMessage(
                        "The read-only Git package manifest entry could not be retained safely: " +
                        exception.Message);
                    return false;
                }

                // A concurrent edit prevented exact rollback. Preserve it and
                // retain in-memory ownership/reconciliation. The persisted
                // write-ahead stage blocks safely if a reload occurs.
                activeState.Stage = StageAdd;
                activeState.OwnsManifestEntry = true;
                Debug.LogWarning(SanitizeMessage(
                    "The read-only Git package manifest entry was added, but its final recovery marker could not be persisted: " +
                    exception.Message + " " + rollbackError));
                error = SanitizeMessage(
                    string.Empty);
                return true;
            }
        }

        internal static bool TryAcquireExactManifestDependencyAtPath(
            string manifestPath,
            string packageName,
            string spec,
            out PackageManifestDependencyMutation mutation,
            out string error)
        {
            if (!PackageManifestGitDependencyStore.TryAddDependencyAtPath(
                    manifestPath,
                    packageName,
                    spec,
                    out mutation,
                    out error))
            {
                return false;
            }

            if (mutation?.Changed == true)
                return true;

            mutation = null;
            error =
                $"Packages/manifest.json already declares {packageName}; the existing entry was not claimed or changed.";
            return false;
        }

        internal static bool TryConsumeLastCompletion(
            out ReadOnlyGitPackageInstallCompletion completion)
        {
            return TryReadLastCompletion(true, out completion);
        }

        internal static bool TryGetLastCompletion(
            out ReadOnlyGitPackageInstallCompletion completion)
        {
            return TryReadLastCompletion(false, out completion);
        }

        private static bool TryReadLastCompletion(
            bool consume,
            out ReadOnlyGitPackageInstallCompletion completion)
        {
            completion = null;
            string json = SessionState.GetString(CompletionStateKey, string.Empty);
            if (string.IsNullOrWhiteSpace(json))
                return false;

            try
            {
                PersistedCompletion persisted =
                    JsonUtility.FromJson<PersistedCompletion>(json);
                if (persisted == null ||
                    (!string.IsNullOrWhiteSpace(
                         persisted.DependencyInstallOperationId) &&
                     !Guid.TryParseExact(
                         persisted.DependencyInstallOperationId,
                         "N",
                         out _)))
                {
                    return false;
                }
                if (persisted.IsRecovery &&
                    !SessionState.GetBool(
                        RecoveryNotificationStateKey,
                        false))
                {
                    // The retained value is the first half of recovery
                    // publication. It is not presentation-owned until the
                    // once-only marker commits successfully.
                    return false;
                }
                completion = new ReadOnlyGitPackageInstallCompletion(
                    persisted.Success,
                    persisted.Message,
                    persisted.PackageName,
                    FindRegisteredPackage(persisted.PackageName),
                    persisted.DependencyInstallOperationId);
                if (consume)
                    SessionState.EraseString(CompletionStateKey);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static void Update()
        {
            if (activeState == null)
                return;

            if (string.Equals(
                    activeState.Stage,
                    StageRecoveryBlocked,
                    StringComparison.Ordinal))
            {
                PublishRecoveryFailureOnce();
                if (activeState?.RecoveryFailurePublished == true)
                    UnsubscribeActiveEvents();
                return;
            }

            if (string.Equals(activeState.Stage, StageCleanup, StringComparison.Ordinal))
            {
                UpdateCleanup();
                return;
            }

            UpdateAddResolution();
        }

        private static void UpdateAddResolution()
        {
            // The manifest CAS owns installation. Wait until its Git resolution
            // intent reaches a terminal state before verification or cleanup so
            // Git and Absent expectations never overlap.
            if (PackageManagerProjectResolutionService.IsBusy)
                return;

            UpmPackageInfo result =
                FindRegisteredPackage(activeState.ExpectedPackageName);
            if (result != null)
            {
                if (TryVerifyExpectedResult(
                        result,
                        out PackageManagerReadOnlyGitInfo info,
                        out string verificationError))
                {
                    Finish(
                        true,
                        info.PackageName,
                        info.PackageInfo,
                        $"Installed {info.PackageName} as a read-only Git package.");
                    return;
                }

                if (!TryStartMismatchCleanup(
                        verificationError,
                        out string cleanupFailureMessage))
                {
                    Finish(
                        false,
                        activeState.ExpectedPackageName,
                        result,
                        cleanupFailureMessage);
                }
                return;
            }

            if (HasRecoveryTimedOut())
            {
                const string timeoutMessage =
                    "Unity Package Manager did not publish the expected read-only Git package before the recovery timeout.";
                if (!TryStartMismatchCleanup(
                        timeoutMessage,
                        out string cleanupFailureMessage))
                {
                    Finish(
                        false,
                        activeState.ExpectedPackageName,
                        null,
                        cleanupFailureMessage);
                }
                return;
            }

            // Restore a missing/damaged resolution handoff after reload without
            // repeating the manifest insertion.
            if (!PackageManagerProjectResolutionService.TryPrepare(
                    activeState.InstallResolutionOperationId,
                    activeState.ExpectedPackageName,
                    PackageManagerResolutionExpectation.Git,
                    out string resolutionError))
            {
                Debug.LogWarning(SanitizeMessage(
                    "The read-only Git install is waiting to restore Unity package resolution: " +
                    resolutionError));
            }
        }

        private static bool TryVerifyExpectedResult(
            UpmPackageInfo result,
            out PackageManagerReadOnlyGitInfo info,
            out string error)
        {
            info = null;
            error = string.Empty;
            if (result == null)
            {
                error = "Unity Package Manager reported success without returning package information.";
                return false;
            }

            string identityError = GitUtility.ValidateExpectedPackageIdentity(
                activeState.ExpectedPackageName,
                activeState.ExpectedVersion,
                result.name,
                result.version);
            if (!string.IsNullOrEmpty(identityError))
            {
                error = identityError;
                return false;
            }

            if (result.source != PackageSource.Git || !result.isDirectDependency)
            {
                error =
                    $"Unity did not register {activeState.ExpectedPackageName} as a direct read-only Git package.";
                return false;
            }

            if (!PackageManagerReadOnlyGitPackage.HasExactManifestSpec(
                    result,
                    activeState.Spec))
            {
                error =
                    $"Unity registered an unexpected manifest entry for {activeState.ExpectedPackageName}.";
                return false;
            }

            string resolvedCommit = result.git?.hash?.Trim() ?? string.Empty;
            if (!TryValidateResolvedCommit(
                    activeState.ExpectedInspectedCommit,
                    resolvedCommit,
                    out error))
            {
                return false;
            }

            string packageJsonPath;
            try
            {
                packageJsonPath = Path.Combine(
                    result.resolvedPath ?? string.Empty,
                    "package.json");
            }
            catch (Exception exception)
            {
                error = SanitizeMessage(
                    "The installed package path could not be inspected: " +
                    exception.Message);
                return false;
            }

            if (!GitUtility.TryReadPackageManifestMetadata(
                    packageJsonPath,
                    out PackageManifestMetadata metadata,
                    out string manifestError))
            {
                error = SanitizeMessage(
                    "The installed package.json could not be validated: " +
                    manifestError);
                return false;
            }

            string packageManifestError =
                GitUtility.ValidateExpectedPackageManifest(
                    activeState.ExpectedPackageName,
                    activeState.ExpectedVersion,
                    activeState.ExpectedDependencyFingerprint,
                    metadata);
            if (!string.IsNullOrEmpty(packageManifestError))
            {
                error = packageManifestError;
                return false;
            }

            if (activeState.PackageManifestMetaVerification ==
                    PackageManifestMetaVerification.Verified)
            {
                string packageManifestMetaPath;
                try
                {
                    packageManifestMetaPath = Path.Combine(
                        result.resolvedPath ?? string.Empty,
                        "package.json.meta");
                }
                catch (Exception exception)
                {
                    error = SanitizeMessage(
                        "The installed package.json.meta path could not be inspected: " +
                        exception.Message);
                    return false;
                }

                if (!GitUtility.TryReadValidPackageManifestMeta(
                        packageManifestMetaPath,
                        out string actualMetaGuid,
                        out string metaError))
                {
                    error = SanitizeMessage(
                        "The installed package.json.meta could not be validated: " +
                        metaError);
                    return false;
                }

                if (!string.Equals(
                        actualMetaGuid,
                        activeState.ExpectedPackageManifestMetaGuid,
                        StringComparison.OrdinalIgnoreCase))
                {
                    error =
                        "The installed package.json.meta changed after repository inspection.";
                    return false;
                }
            }

            return PackageManagerReadOnlyGitPackage.TryCreateInfo(
                result,
                out info,
                out error);
        }

        private static bool TryStartMismatchCleanup(
            string verificationError,
            out string cleanupFailureMessage)
        {
            cleanupFailureMessage = string.Empty;
            if (activeState?.OwnsManifestEntry != true ||
                !GitUtility.IsValidUpmPackageName(
                    activeState.ExpectedPackageName))
            {
                cleanupFailureMessage = BuildMismatchCleanupFailureMessage(
                    verificationError,
                    "cleanup ownership could not be proven safely");
                return false;
            }

            string cleanupResolutionOperationId =
                Guid.NewGuid().ToString("N");
            activeState.Stage = StageCleanupPrepared;
            activeState.CleanupPackageName =
                activeState.ExpectedPackageName;
            activeState.CleanupResolutionOperationId =
                cleanupResolutionOperationId;
            activeState.FailureMessage =
                (verificationError ?? "The installed package did not match the request.") +
                " The newly-added dependency is being removed.";
            bool resolutionPrepared = false;
            bool manifestRemoved = false;
            try
            {
                // Persist destructive intent before touching manifest.json. A
                // reload of this write-ahead stage is ambiguous and therefore
                // recovery-blocked; it never repeats the mutation.
                SaveActiveState();

                if (!PackageManagerProjectResolutionService.TryPrepare(
                        cleanupResolutionOperationId,
                        activeState.CleanupPackageName,
                        PackageManagerResolutionExpectation.Absent,
                        out string resolutionError))
                {
                    cleanupFailureMessage = BuildMismatchCleanupFailureMessage(
                        verificationError,
                        resolutionError);
                    return false;
                }
                resolutionPrepared = true;

                if (!TryRemoveExactMismatchDependencyAtPath(
                        PackageManifestGitDependencyStore.ManifestPath,
                        activeState.CleanupPackageName,
                        activeState.Spec,
                        out string removalError))
                {
                    PackageManagerProjectResolutionService.CancelPrepared(
                        cleanupResolutionOperationId);
                    cleanupFailureMessage = BuildMismatchCleanupFailureMessage(
                        verificationError,
                        removalError);
                    return false;
                }
                manifestRemoved = true;

                // The exact compare-and-swap is complete. Persist the recovery
                // stage before returning to Unity; reload now observes only the
                // resolution handoff and never issues manifest cleanup again.
                activeState.Stage = StageCleanup;
                SaveActiveState();
                return true;
            }
            catch (Exception exception)
            {
                if (manifestRemoved)
                {
                    // The exact owned key is already gone. Keep the prepared
                    // Absent resolution and in-memory recovery state; a reload
                    // sees the write-ahead stage and blocks rather than repeat.
                    activeState.Stage = StageCleanup;
                    Debug.LogWarning(SanitizeMessage(
                        "The mismatched read-only dependency was removed, but its final recovery marker could not be persisted: " +
                        exception.Message));
                    return true;
                }

                if (resolutionPrepared)
                {
                    PackageManagerProjectResolutionService.CancelPrepared(
                        cleanupResolutionOperationId);
                }
                cleanupFailureMessage = BuildMismatchCleanupFailureMessage(
                    verificationError,
                    "the exact manifest cleanup could not be completed safely: " +
                    exception.Message);
                return false;
            }
        }

        internal static bool TryRemoveExactMismatchDependencyAtPath(
            string manifestPath,
            string packageName,
            string expectedSpec,
            out string error)
        {
            return PackageManifestGitDependencyStore.TryRemoveDependencyAtPath(
                manifestPath,
                packageName,
                expectedSpec,
                out _,
                out error);
        }

        internal static string BuildMismatchCleanupFailureMessage(
            string verificationError,
            string cleanupFailure)
        {
            string mismatch = string.IsNullOrWhiteSpace(verificationError)
                ? "The installed package did not match the request."
                : verificationError.Trim();
            string detail = string.IsNullOrWhiteSpace(cleanupFailure)
                ? "automatic cleanup did not complete"
                : cleanupFailure.Trim().TrimEnd('.');
            string sanitized = SanitizeMessage(
                mismatch.TrimEnd() + " Automatic removal was not completed because " +
                detail + ". The mismatched dependency may remain in " +
                "Packages/manifest.json; inspect it before retrying.");
            var singleLine = new StringBuilder(sanitized.Length);
            foreach (char character in sanitized)
                singleLine.Append(char.IsControl(character) ? ' ' : character);
            return singleLine.ToString().Trim();
        }

        private static void UpdateCleanup()
        {
            // The exact manifest mutation completed before this stage was
            // persisted. Recovery may restore the UPM resolution handoff, but
            // it must never issue another manifest removal.
            if (PackageManagerProjectResolutionService.IsBusy)
                return;

            UpmPackageInfo cleanupPackage =
                FindRegisteredPackage(activeState.CleanupPackageName);
            if (cleanupPackage == null || !cleanupPackage.isDirectDependency)
            {
                FinishCleanupFailure();
                return;
            }

            if (HasRecoveryTimedOut())
            {
                Finish(
                    false,
                    activeState.ExpectedPackageName,
                    cleanupPackage,
                    activeState.FailureMessage +
                    " Automatic removal did not reach a terminal state. Inspect Packages/manifest.json before retrying.");
                return;
            }

            if (!PackageManagerProjectResolutionService.TryPrepare(
                    activeState.CleanupResolutionOperationId,
                    activeState.CleanupPackageName,
                    PackageManagerResolutionExpectation.Absent,
                    out string resolutionError))
            {
                Finish(
                    false,
                    activeState.ExpectedPackageName,
                    cleanupPackage,
                    BuildMismatchCleanupFailureMessage(
                        activeState.FailureMessage,
                        resolutionError));
            }
        }

        private static void FinishCleanupFailure()
        {
            string message = activeState.FailureMessage;
            const string suffix = " The newly-added dependency was removed.";
            if (message.EndsWith(
                    " The newly-added dependency is being removed.",
                    StringComparison.Ordinal))
            {
                message = message.Substring(
                              0,
                              message.Length -
                              " The newly-added dependency is being removed.".Length) +
                          suffix;
            }
            else
            {
                message += suffix;
            }

            Finish(false, activeState.ExpectedPackageName, null, message);
        }

        private static void Finish(
            bool success,
            string packageName,
            UpmPackageInfo packageInfo,
            string message)
        {
            var completion = new ReadOnlyGitPackageInstallCompletion(
                success,
                SanitizeMessage(message),
                packageName,
                packageInfo,
                activeState?.DependencyInstallOperationId);
            PersistCompletion(completion);

            Action<ReadOnlyGitPackageInstallCompletion> callback = activeCallback;
            ClearActiveState();
            Notify(callback, completion);
            Notify(Completed, completion);
        }

        private static void Notify(
            Action<ReadOnlyGitPackageInstallCompletion> handler,
            ReadOnlyGitPackageInstallCompletion completion)
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

        private static UpmPackageInfo FindRegisteredPackage(string packageName)
        {
            if (!GitUtility.IsValidUpmPackageName(packageName))
                return null;
            try
            {
                UpmPackageInfo[] packages = UpmPackageInfo.GetAllRegisteredPackages();
                if (packages == null)
                    return null;
                foreach (UpmPackageInfo package in packages)
                {
                    if (package != null &&
                        string.Equals(package.name, packageName, StringComparison.Ordinal))
                    {
                        return package;
                    }
                }
            }
            catch
            {
                // Package registration can be temporarily unavailable while
                // UPM or the Editor is reloading. The next update retries.
            }

            return null;
        }

        private static bool HasRecoveryTimedOut()
        {
            if (activeState == null || activeState.StartedUtcTicks <= 0)
                return true;
            long elapsedTicks = DateTime.UtcNow.Ticks - activeState.StartedUtcTicks;
            return elapsedTicks >= TimeSpan.FromMinutes(RecoveryTimeoutMinutes).Ticks;
        }

        private static PersistedInstallState LoadActiveState()
        {
            string json = SessionState.GetString(ActiveStateKey, string.Empty);
            if (string.IsNullOrWhiteSpace(json))
                return null;
            try
            {
                PersistedInstallState state =
                    JsonUtility.FromJson<PersistedInstallState>(json);
                if (!IsValidPersistedInstallState(state))
                    return CreateBlockedRecoveryState(
                        state,
                        SessionState.GetBool(
                            RecoveryNotificationStateKey,
                            false));

                return state;
            }
            catch
            {
                return CreateBlockedRecoveryState(
                    null,
                    SessionState.GetBool(
                        RecoveryNotificationStateKey,
                        false));
            }
        }

        internal static bool TryBuildInvalidActiveStateCompletion(
            string json,
            out ReadOnlyGitPackageInstallCompletion completion)
        {
            completion = null;
            if (string.IsNullOrWhiteSpace(json))
                return false;

            PersistedInstallState state;
            try
            {
                state = JsonUtility.FromJson<PersistedInstallState>(json);
            }
            catch
            {
                state = null;
            }

            if (IsValidPersistedInstallState(state))
                return false;

            PersistedInstallState blocked = CreateBlockedRecoveryState(
                state,
                false);
            completion = new ReadOnlyGitPackageInstallCompletion(
                false,
                blocked.FailureMessage,
                blocked.ExpectedPackageName,
                null,
                blocked.DependencyInstallOperationId);
            return true;
        }

        private static bool IsValidPersistedInstallState(
            PersistedInstallState state)
        {
            if (state == null ||
                state.SchemaVersion != CurrentActiveStateSchemaVersion ||
                !GitUtility.IsValidUpmPackageName(state.ExpectedPackageName) ||
                string.IsNullOrWhiteSpace(state.Revision) ||
                string.Equals(state.Revision, ".", StringComparison.Ordinal) ||
                !GitUtility.IsValidBranchName(state.Revision) ||
                (!string.IsNullOrWhiteSpace(state.ExpectedVersion) &&
                 !GitUtility.IsValidSemanticVersion(state.ExpectedVersion)) ||
                (!string.IsNullOrWhiteSpace(
                     state.ExpectedDependencyFingerprint) &&
                 !GitUtility.IsValidPackageDependencyFingerprint(
                     state.ExpectedDependencyFingerprint)) ||
                !IsValidPackageManifestMetaEvidence(
                    state.PackageManifestMetaVerification,
                    state.ExpectedPackageManifestMetaGuid) ||
                !GitUtility.IsValidGitObjectId(
                    state.ExpectedInspectedCommit) ||
                (!string.IsNullOrWhiteSpace(
                     state.DependencyInstallOperationId) &&
                 !Guid.TryParseExact(
                     state.DependencyInstallOperationId,
                     "N",
                     out _)) ||
                !Guid.TryParseExact(
                    state.InstallResolutionOperationId,
                    "N",
                    out _) ||
                !state.OwnsManifestEntry ||
                state.StartedUtcTicks <= 0L ||
                !PackageManifestGitDependencyStore.TryBuildGitSpec(
                    state.RepositoryUrl,
                    state.ExpectedInspectedCommit,
                    out string expectedSpec,
                    out _) ||
                !string.Equals(state.Spec, expectedSpec, StringComparison.Ordinal) ||
                (state.Stage != StageAdd && state.Stage != StageCleanup))
            {
                return false;
            }

            bool coordinatedInstall = !string.IsNullOrWhiteSpace(
                state.DependencyInstallOperationId);
            if (coordinatedInstall &&
                (state.PackageManifestMetaVerification !=
                     PackageManifestMetaVerification.Verified ||
                 string.IsNullOrWhiteSpace(state.ExpectedVersion) ||
                 !GitUtility.IsValidPackageDependencyFingerprint(
                     state.ExpectedDependencyFingerprint)))
            {
                return false;
            }

            if (state.Stage == StageAdd)
            {
                return string.IsNullOrWhiteSpace(state.CleanupPackageName) &&
                       string.IsNullOrWhiteSpace(
                           state.CleanupResolutionOperationId) &&
                       string.IsNullOrWhiteSpace(state.FailureMessage);
            }

            return string.Equals(
                       state.CleanupPackageName,
                       state.ExpectedPackageName,
                       StringComparison.Ordinal) &&
                   Guid.TryParseExact(
                       state.CleanupResolutionOperationId,
                       "N",
                       out _) &&
                   !string.IsNullOrWhiteSpace(state.FailureMessage);
        }

        private static bool TryValidatePackageManifestMetaEvidence(
            PackageManifestMetaVerification verification,
            string guid,
            out string error)
        {
            if (IsValidPackageManifestMetaEvidence(verification, guid))
            {
                error = string.Empty;
                return true;
            }

            error = verification == PackageManifestMetaVerification.Unverified
                ? "Read-only Git installs require verified package.json.meta evidence."
                : "Verified package.json.meta evidence requires a valid nonzero Unity GUID.";
            return false;
        }

        private static bool IsValidPackageManifestMetaEvidence(
            PackageManifestMetaVerification verification,
            string guid)
        {
            return verification == PackageManifestMetaVerification.Verified &&
                   GitSubmoduleInstallProbeSnapshot.IsValidMetaGuid(guid);
        }

        internal static bool TryValidateResolvedCommit(
            string expectedCommit,
            string actualCommit,
            out string error)
        {
            string expected = expectedCommit?.Trim() ?? string.Empty;
            string actual = actualCommit?.Trim() ?? string.Empty;
            if (!GitUtility.IsValidGitObjectId(expected))
            {
                error = "The inspected Git commit evidence is missing or invalid.";
                return false;
            }

            if (!GitUtility.IsValidGitObjectId(actual))
            {
                error =
                    "Unity did not report a verifiable resolved Git commit for the installed package.";
                return false;
            }

            if (!string.Equals(
                    expected,
                    actual,
                    StringComparison.OrdinalIgnoreCase))
            {
                error =
                    "Unity resolved a different Git commit than the one whose package metadata was inspected.";
                return false;
            }

            error = string.Empty;
            return true;
        }

        private static PersistedInstallState CreateBlockedRecoveryState(
            PersistedInstallState state,
            bool recoveryFailurePublished)
        {
            string packageName = GitUtility.IsValidUpmPackageName(
                state?.ExpectedPackageName)
                ? state.ExpectedPackageName.Trim()
                : string.Empty;
            string operationId = Guid.TryParseExact(
                state?.DependencyInstallOperationId,
                "N",
                out _)
                ? state.DependencyInstallOperationId.Trim()
                : string.Empty;
            return new PersistedInstallState
            {
                SchemaVersion = CurrentActiveStateSchemaVersion,
                Stage = StageRecoveryBlocked,
                ExpectedPackageName = packageName,
                DependencyInstallOperationId = operationId,
                StartedUtcTicks = DateTime.UtcNow.Ticks,
                RecoveryFailurePublished = recoveryFailurePublished,
                FailureMessage =
                    "A persisted read-only Git install record is damaged, so Unity cannot prove whether its exact " +
                    "manifest mutation completed. No package mutation was issued again and this project remains " +
                    "blocked from package mutations. Preserve and inspect Packages/manifest.json plus Unity's " +
                    "registered package state, repair or remove only state you can identify safely, then restart " +
                    "the Unity Editor to clear this session recovery block."
            };
        }

        private static void PublishRecoveryFailureOnce()
        {
            if (activeState == null ||
                activeState.RecoveryFailurePublished ||
                !string.Equals(
                    activeState.Stage,
                    StageRecoveryBlocked,
                    StringComparison.Ordinal))
            {
                return;
            }

            var completion = new ReadOnlyGitPackageInstallCompletion(
                false,
                SanitizeMessage(activeState.FailureMessage),
                activeState.ExpectedPackageName,
                null,
                activeState.DependencyInstallOperationId);
            if (!TryRetainRecoveryFailure(
                    completion,
                    PersistRecoveryCompletion,
                    () => SessionState.SetBool(
                        RecoveryNotificationStateKey,
                        true),
                    out string persistenceError))
            {
                Debug.LogWarning(SanitizeMessage(
                    "The read-only package recovery failure could not be retained: " +
                    persistenceError));
                return;
            }

            activeState.RecoveryFailurePublished = true;
            Notify(Completed, completion);
        }

        internal static bool TryRetainRecoveryFailure(
            ReadOnlyGitPackageInstallCompletion completion,
            Action<ReadOnlyGitPackageInstallCompletion> persistCompletion,
            Action persistNotificationOwnership,
            out string error)
        {
            if (completion == null)
                throw new ArgumentNullException(nameof(completion));
            if (persistCompletion == null)
                throw new ArgumentNullException(nameof(persistCompletion));
            if (persistNotificationOwnership == null)
                throw new ArgumentNullException(
                    nameof(persistNotificationOwnership));

            try
            {
                // A retained outcome must exist before the once-only marker can
                // suppress another publication after a domain reload.
                persistCompletion(completion);
                persistNotificationOwnership();
                error = string.Empty;
                return true;
            }
            catch (Exception exception)
            {
                error = exception.Message;
                return false;
            }
        }

        private static void SaveActiveState()
        {
            if (activeState == null)
            {
                SessionState.EraseString(ActiveStateKey);
                return;
            }

            SessionState.SetString(
                ActiveStateKey,
                JsonUtility.ToJson(activeState));
        }

        private static void PersistCompletion(
            ReadOnlyGitPackageInstallCompletion completion)
        {
            PersistCompletion(completion, false);
        }

        private static void PersistRecoveryCompletion(
            ReadOnlyGitPackageInstallCompletion completion)
        {
            PersistCompletion(completion, true);
        }

        private static void PersistCompletion(
            ReadOnlyGitPackageInstallCompletion completion,
            bool isRecovery)
        {
            SessionState.SetString(
                CompletionStateKey,
                JsonUtility.ToJson(new PersistedCompletion
                {
                    IsRecovery = isRecovery,
                    Success = completion.Success,
                    Message = completion.Message,
                    PackageName = completion.PackageName,
                    DependencyInstallOperationId =
                        completion.DependencyInstallOperationId
                }));
        }

        private static void ClearActiveState()
        {
            activeState = null;
            UnsubscribeActiveEvents();
            activeCallback = null;
            SessionState.EraseString(ActiveStateKey);
        }

        private static void SubscribeActiveEvents()
        {
            if (activeState == null || activeEventsSubscribed ||
                (IsRecoveryBlocked &&
                 activeState.RecoveryFailurePublished))
                return;

            activeEventsSubscribed = true;
            EditorApplication.update += Update;
        }

        private static void UnsubscribeActiveEvents()
        {
            if (!activeEventsSubscribed)
                return;

            activeEventsSubscribed = false;
            EditorApplication.update -= Update;
        }

        private static string SanitizeMessage(string message)
        {
            return GitHubUtility.SanitizeUiDiagnostic(
                GitUtility.RedactCredentials(message ?? string.Empty));
        }

        [Serializable]
        private sealed class PersistedInstallState
        {
            public int SchemaVersion;
            public string Stage = string.Empty;
            public string RepositoryUrl = string.Empty;
            public string Revision = string.Empty;
            public string ExpectedPackageName = string.Empty;
            public string ExpectedVersion = string.Empty;
            public string ExpectedDependencyFingerprint = string.Empty;
            public PackageManifestMetaVerification
                PackageManifestMetaVerification;
            public string ExpectedPackageManifestMetaGuid = string.Empty;
            public string ExpectedInspectedCommit = string.Empty;
            public string DependencyInstallOperationId = string.Empty;
            public string InstallResolutionOperationId = string.Empty;
            public bool OwnsManifestEntry;
            public string Spec = string.Empty;
            public string CleanupPackageName = string.Empty;
            public string CleanupResolutionOperationId = string.Empty;
            public string FailureMessage = string.Empty;
            public long StartedUtcTicks;
            [NonSerialized]
            public bool RecoveryFailurePublished;
        }

        [Serializable]
        private sealed class PersistedCompletion
        {
            public bool IsRecovery;
            public bool Success;
            public string Message = string.Empty;
            public string PackageName = string.Empty;
            public string DependencyInstallOperationId = string.Empty;
        }
    }
}
