using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEditor.PackageManager.Requests;
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
            UpmPackageInfo packageInfo)
        {
            Success = success;
            Message = message ?? string.Empty;
            PackageName = packageName ?? string.Empty;
            PackageInfo = packageInfo;
        }

        internal bool Success { get; }
        internal string Message { get; }
        internal string PackageName { get; }
        internal UpmPackageInfo PackageInfo { get; }
    }

    /// <summary>
    /// Starts ordinary Unity Package Manager Git installs through Client.Add.
    /// Intent is retained in SessionState across script reloads. A completed add
    /// is accepted only when Unity reports the expected direct Git package and
    /// the exact requested manifest entry. A newly-added mismatched package is
    /// removed with Client.Remove before terminal failure is reported.
    /// </summary>
    [InitializeOnLoad]
    internal static class PackageManagerReadOnlyGitInstallService
    {
        private const string ActiveStateKey =
            "MartinCalander.GitSubmoduleManager.ReadOnlyGitInstall.Active.v1";
        private const string CompletionStateKey =
            "MartinCalander.GitSubmoduleManager.ReadOnlyGitInstall.Completion.v1";
        private const string StageAdd = "add";
        private const string StageCleanup = "cleanup";
        private const double RecoveryTimeoutMinutes = 10d;

        private static PersistedInstallState activeState;
        private static AddRequest activeAddRequest;
        private static RemoveRequest activeRemoveRequest;
        private static Action<ReadOnlyGitPackageInstallCompletion> activeCallback;
        private static bool registeredPackagesChanged;

        internal static event Action<ReadOnlyGitPackageInstallCompletion> Completed;

        static PackageManagerReadOnlyGitInstallService()
        {
            activeState = LoadActiveState();
            EditorApplication.update += Update;
            Events.registeredPackages += OnRegisteredPackages;
        }

        internal static bool IsBusy => activeState != null;
        internal static string ActivePackageName =>
            activeState?.ExpectedPackageName ?? string.Empty;
        internal static string ActiveRepositoryUrl =>
            activeState?.RepositoryUrl ?? string.Empty;
        internal static string ActiveRevision =>
            activeState?.Revision ?? string.Empty;

        internal static string BuildUnavailableMessage()
        {
            if (IsBusy)
                return $"Installing {ActivePackageName} as a read-only package...";
            if (PackageManagerProjectResolutionService.IsBusy)
                return PackageManagerProjectResolutionService.BuildUnavailableMessage();
            if (GitOperationService.IsBusy)
                return "Wait for the current Git package operation to finish.";
            if (PackageManagerSubmoduleSnapshot.IsReaderActive ||
                GitSubmoduleInstallProbe.IsReaderActive ||
                GitSubmoduleManagerView.AreBackgroundLoadsDraining ||
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
            error = ValidateInput(repositoryUrl, revision, expectedPackageName);
            if (!string.IsNullOrEmpty(error))
                return false;

            if (!PackageManifestGitDependencyStore.TryBuildGitSpec(
                    repositoryUrl,
                    revision,
                    out string spec,
                    out error))
            {
                return false;
            }

            string[] directPackageNames;
            try
            {
                directPackageNames = ReadDirectPackageNames();
            }
            catch (Exception exception)
            {
                error = SanitizeMessage(
                    "Unity's direct package list could not be captured before installation: " +
                    exception.Message);
                return false;
            }

            activeState = new PersistedInstallState
            {
                Stage = StageAdd,
                RepositoryUrl = repositoryUrl.Trim(),
                Revision = revision?.Trim() ?? string.Empty,
                ExpectedPackageName = expectedPackageName,
                Spec = spec,
                DirectPackageNamesBefore = directPackageNames,
                StartedUtcTicks = DateTime.UtcNow.Ticks
            };
            activeCallback = onComplete;
            SaveActiveState();

            try
            {
                activeAddRequest = Client.Add(spec);
                if (activeAddRequest == null)
                    throw new InvalidOperationException("Unity did not create an add request.");
                return true;
            }
            catch (Exception exception)
            {
                error = SanitizeMessage(
                    "Unity Package Manager could not start the read-only Git install: " +
                    exception.Message);
                ClearActiveState();
                return false;
            }
        }

        internal static bool TryConsumeLastCompletion(
            out ReadOnlyGitPackageInstallCompletion completion)
        {
            completion = null;
            string json = SessionState.GetString(CompletionStateKey, string.Empty);
            if (string.IsNullOrWhiteSpace(json))
                return false;

            SessionState.EraseString(CompletionStateKey);
            try
            {
                PersistedCompletion persisted =
                    JsonUtility.FromJson<PersistedCompletion>(json);
                if (persisted == null)
                    return false;
                completion = new ReadOnlyGitPackageInstallCompletion(
                    persisted.Success,
                    persisted.Message,
                    persisted.PackageName,
                    FindRegisteredPackage(persisted.PackageName));
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

            if (string.Equals(activeState.Stage, StageCleanup, StringComparison.Ordinal))
            {
                UpdateCleanup();
                return;
            }

            if (activeAddRequest != null)
            {
                if (!activeAddRequest.IsCompleted)
                    return;
                CompleteAddRequest();
                return;
            }

            // A public UPM request object does not survive a domain reload. The
            // native operation does, so recover from authoritative registered
            // package state without issuing the add a second time.
            RecoverAddAfterReload();
        }

        private static void CompleteAddRequest()
        {
            AddRequest request = activeAddRequest;
            activeAddRequest = null;
            if (request.Status != StatusCode.Success)
            {
                string detail = request.Error?.message;
                Finish(false, activeState.ExpectedPackageName, null,
                    string.IsNullOrWhiteSpace(detail)
                        ? "Unity Package Manager could not install the read-only Git package."
                        : "Unity Package Manager could not install the read-only Git package: " + detail);
                return;
            }

            UpmPackageInfo result = request.Result;
            if (TryVerifyExpectedResult(result, out PackageManagerReadOnlyGitInfo info, out string error))
            {
                Finish(
                    true,
                    info.PackageName,
                    info.PackageInfo,
                    $"Installed {info.PackageName} as a read-only Git package.");
                return;
            }

            if (!TryStartMismatchCleanup(result, error))
            {
                Finish(
                    false,
                    activeState.ExpectedPackageName,
                    result,
                    error);
            }
        }

        private static void RecoverAddAfterReload()
        {
            if (!registeredPackagesChanged && !HasRecoveryTimedOut())
            {
                // Polling once per Editor update is still necessary because a
                // package registration event can occur before this static class
                // is reinitialized. The flag only avoids special event ordering.
            }

            registeredPackagesChanged = false;
            UpmPackageInfo exactSpecPackage = FindRegisteredPackageByExactSpec(activeState.Spec);
            if (exactSpecPackage != null)
            {
                if (TryVerifyExpectedResult(
                        exactSpecPackage,
                        out PackageManagerReadOnlyGitInfo info,
                        out string error))
                {
                    Finish(
                        true,
                        info.PackageName,
                        info.PackageInfo,
                        $"Installed {info.PackageName} as a read-only Git package.");
                    return;
                }

                if (!TryStartMismatchCleanup(exactSpecPackage, error))
                {
                    Finish(
                        false,
                        activeState.ExpectedPackageName,
                        exactSpecPackage,
                        error);
                }
                return;
            }

            if (HasRecoveryTimedOut())
            {
                Finish(
                    false,
                    activeState.ExpectedPackageName,
                    null,
                    "Unity Package Manager did not publish a terminal install result before the recovery timeout. Inspect the project manifest before retrying.");
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

            if (!string.Equals(
                    result.name,
                    activeState.ExpectedPackageName,
                    StringComparison.Ordinal))
            {
                error =
                    $"The repository declared {result.name ?? "an unknown package"}, not the expected package {activeState.ExpectedPackageName}.";
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

            return PackageManagerReadOnlyGitPackage.TryCreateInfo(
                result,
                out info,
                out error);
        }

        private static bool TryStartMismatchCleanup(
            UpmPackageInfo result,
            string verificationError)
        {
            if (result == null ||
                !GitUtility.IsValidUpmPackageName(result.name) ||
                !result.isDirectDependency ||
                WasDirectPackageBefore(result.name) ||
                !PackageManagerReadOnlyGitPackage.HasExactManifestSpec(
                    result,
                    activeState.Spec))
            {
                return false;
            }

            activeState.Stage = StageCleanup;
            activeState.CleanupPackageName = result.name;
            activeState.FailureMessage =
                (verificationError ?? "The installed package did not match the request.") +
                " The newly-added dependency is being removed.";
            SaveActiveState();
            try
            {
                activeRemoveRequest = Client.Remove(result.name);
                if (activeRemoveRequest == null)
                    throw new InvalidOperationException("Unity did not create a remove request.");
                return true;
            }
            catch (Exception exception)
            {
                activeState.FailureMessage = SanitizeMessage(
                    verificationError +
                    " Automatic removal could not start: " +
                    exception.Message +
                    ". Inspect Packages/manifest.json before retrying.");
                SaveActiveState();
                return false;
            }
        }

        private static void UpdateCleanup()
        {
            if (activeRemoveRequest != null)
            {
                if (!activeRemoveRequest.IsCompleted)
                    return;

                RemoveRequest request = activeRemoveRequest;
                activeRemoveRequest = null;
                if (request.Status == StatusCode.Success)
                {
                    FinishCleanupFailure();
                    return;
                }

                string detail = request.Error?.message;
                Finish(
                    false,
                    activeState.ExpectedPackageName,
                    FindRegisteredPackage(activeState.CleanupPackageName),
                    activeState.FailureMessage +
                    (string.IsNullOrWhiteSpace(detail)
                        ? " Automatic removal failed. Inspect Packages/manifest.json before retrying."
                        : " Automatic removal failed: " + detail));
                return;
            }

            // Recovery after a reload during Client.Remove. A package can remain
            // registered transitively; only a direct dependency means cleanup
            // is still pending.
            UpmPackageInfo cleanupPackage = FindRegisteredPackage(activeState.CleanupPackageName);
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
                packageInfo);
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

        private static void OnRegisteredPackages(PackageRegistrationEventArgs _)
        {
            registeredPackagesChanged = true;
        }

        private static bool WasDirectPackageBefore(string packageName)
        {
            string[] names = activeState?.DirectPackageNamesBefore;
            if (names == null)
                return false;
            foreach (string name in names)
            {
                if (string.Equals(name, packageName, StringComparison.Ordinal))
                    return true;
            }

            return false;
        }

        private static string[] ReadDirectPackageNames()
        {
            var names = new List<string>();
            UpmPackageInfo[] packages = UpmPackageInfo.GetAllRegisteredPackages();
            if (packages == null)
                return Array.Empty<string>();
            foreach (UpmPackageInfo package in packages)
            {
                if (package != null &&
                    package.isDirectDependency &&
                    GitUtility.IsValidUpmPackageName(package.name))
                {
                    names.Add(package.name);
                }
            }

            names.Sort(StringComparer.Ordinal);
            return names.ToArray();
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

        private static UpmPackageInfo FindRegisteredPackageByExactSpec(string spec)
        {
            try
            {
                UpmPackageInfo[] packages = UpmPackageInfo.GetAllRegisteredPackages();
                if (packages == null)
                    return null;
                foreach (UpmPackageInfo package in packages)
                {
                    if (package != null &&
                        package.isDirectDependency &&
                        package.source == PackageSource.Git &&
                        PackageManagerReadOnlyGitPackage.HasExactManifestSpec(
                            package,
                            spec))
                    {
                        return package;
                    }
                }
            }
            catch
            {
                // Retry after Unity finishes publishing registered packages.
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
                if (state == null ||
                    !GitUtility.IsValidUpmPackageName(state.ExpectedPackageName) ||
                    !PackageManifestGitDependencyStore.TryBuildGitSpec(
                        state.RepositoryUrl,
                        state.Revision,
                        out string expectedSpec,
                        out _) ||
                    !string.Equals(state.Spec, expectedSpec, StringComparison.Ordinal) ||
                    (state.Stage != StageAdd && state.Stage != StageCleanup))
                {
                    SessionState.EraseString(ActiveStateKey);
                    return null;
                }

                return state;
            }
            catch
            {
                SessionState.EraseString(ActiveStateKey);
                return null;
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
            SessionState.SetString(
                CompletionStateKey,
                JsonUtility.ToJson(new PersistedCompletion
                {
                    Success = completion.Success,
                    Message = completion.Message,
                    PackageName = completion.PackageName
                }));
        }

        private static void ClearActiveState()
        {
            activeState = null;
            activeAddRequest = null;
            activeRemoveRequest = null;
            activeCallback = null;
            registeredPackagesChanged = false;
            SessionState.EraseString(ActiveStateKey);
        }

        private static string SanitizeMessage(string message)
        {
            return GitHubUtility.SanitizeUiDiagnostic(
                GitUtility.RedactCredentials(message ?? string.Empty));
        }

        [Serializable]
        private sealed class PersistedInstallState
        {
            public string Stage = string.Empty;
            public string RepositoryUrl = string.Empty;
            public string Revision = string.Empty;
            public string ExpectedPackageName = string.Empty;
            public string Spec = string.Empty;
            public string[] DirectPackageNamesBefore = Array.Empty<string>();
            public string CleanupPackageName = string.Empty;
            public string FailureMessage = string.Empty;
            public long StartedUtcTicks;
        }

        [Serializable]
        private sealed class PersistedCompletion
        {
            public bool Success;
            public string Message = string.Empty;
            public string PackageName = string.Empty;
        }
    }
}
