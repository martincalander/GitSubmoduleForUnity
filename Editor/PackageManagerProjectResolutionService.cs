using System;
using System.IO;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEngine;
using UpmPackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace MartinCalander.GitSubmoduleManager.Editor
{
    internal enum PackageManagerResolutionExpectation
    {
        None,
        Absent,
        Embedded,
        Git
    }

    internal enum PackageManagerResolutionNextAction
    {
        Wait,
        StartResolve,
        Complete,
        Timeout
    }

    [Serializable]
    internal sealed class PackageManagerProjectResolutionState
    {
        public int SchemaVersion = 1;
        public string OperationId = string.Empty;
        public string PackageName = string.Empty;
        public string ExpectedResolvedPath = string.Empty;
        public PackageManagerResolutionExpectation Expectation;
        public long StartedUtcTicks;
        public bool ResolveRequested;
        public long ResolveRequestedUtcTicks;
    }

    /// <summary>
    /// Bridges a completed Git filesystem mutation into Unity Package Manager's
    /// own package graph. The intent is persisted before assembly reloads are
    /// unlocked, while Client.Resolve is deferred until GitOperationService has
    /// released repository and reload ownership. Authoritative package state is
    /// then reconciled across the domain reload that resolution can trigger.
    /// </summary>
    [InitializeOnLoad]
    internal static class PackageManagerProjectResolutionService
    {
        private const int CurrentSchemaVersion = 1;
        private const double ResolutionTimeoutMinutes = 10d;
        private const double ResolveRetryDelaySeconds = 5d;
        private const double ResolveWarningIntervalSeconds = 60d;
        private static readonly long ResolutionQuietIntervalTicks =
            TimeSpan.FromSeconds(1d).Ticks;
        private const string ActiveStateKey =
            "MartinCalander.GitSubmoduleManager.ProjectResolution.Active.v1";

        private static PackageManagerProjectResolutionState activeState;
        private static bool registeredPackagesChanged;
        private static bool resolutionProgressObserved;
        private static bool activeEventsSubscribed;
        private static bool isShuttingDown;
        private static double nextInspectionNotBefore;
        private static double nextResolveAttemptNotBefore;
        private static double nextResolveWarningNotBefore;
        private static long expectationSatisfiedSinceUtcTicks;

        static PackageManagerProjectResolutionService()
        {
            activeState = LoadActiveState();
            resolutionProgressObserved = activeState?.ResolveRequested == true;
            SubscribeActiveEvents();
        }

        internal static bool IsBusy => activeState != null;

        internal static string ActivePackageName =>
            activeState?.PackageName ?? string.Empty;

        internal static string BuildUnavailableMessage()
        {
            return IsBusy
                ? $"Wait for Unity Package Manager to finish resolving {ActivePackageName}."
                : string.Empty;
        }

        /// <summary>
        /// Persists a successful Git mutation's expected UPM result. This method
        /// deliberately does not call Client.Resolve because it runs while
        /// GitOperationService still owns Unity's assembly reload lock.
        /// </summary>
        internal static bool TryPrepare(
            string operationId,
            string packageName,
            PackageManagerResolutionExpectation expectation,
            out string error)
        {
            error = ValidateIntent(operationId, packageName, expectation);
            if (!string.IsNullOrEmpty(error))
                return false;

            string normalizedPackageName = packageName.Trim();
            string normalizedOperationId = operationId.Trim();
            if (activeState != null)
            {
                if (string.Equals(
                        activeState.OperationId,
                        normalizedOperationId,
                        StringComparison.Ordinal) &&
                    string.Equals(
                        activeState.PackageName,
                        normalizedPackageName,
                        StringComparison.Ordinal) &&
                    activeState.Expectation == expectation)
                {
                    return true;
                }

                error = BuildUnavailableMessage();
                return false;
            }

            var prepared = new PackageManagerProjectResolutionState
            {
                SchemaVersion = CurrentSchemaVersion,
                OperationId = normalizedOperationId,
                PackageName = normalizedPackageName,
                ExpectedResolvedPath = expectation == PackageManagerResolutionExpectation.Embedded
                    ? GetExpectedEmbeddedPath(normalizedPackageName)
                    : string.Empty,
                Expectation = expectation,
                StartedUtcTicks = DateTime.UtcNow.Ticks,
                ResolveRequested = false,
                ResolveRequestedUtcTicks = 0L
            };

            try
            {
                SaveActiveState(prepared);
                activeState = prepared;
                SubscribeActiveEvents();
                registeredPackagesChanged = false;
                resolutionProgressObserved = false;
                nextResolveAttemptNotBefore = 0d;
                nextResolveWarningNotBefore = 0d;
                expectationSatisfiedSinceUtcTicks = 0L;
                return true;
            }
            catch (Exception exception)
            {
                error = SanitizeMessage(
                    "Unity package resolution state could not be persisted: " +
                    exception.Message);
                return false;
            }
        }

        internal static void CancelPrepared(string operationId)
        {
            if (activeState == null || activeState.ResolveRequested ||
                !string.Equals(
                    activeState.OperationId,
                    operationId?.Trim(),
                    StringComparison.Ordinal))
            {
                return;
            }

            ClearActiveState();
        }

        internal static PackageManagerResolutionNextAction DetermineNextAction(
            bool resolveRequested,
            bool gitOperationBusy,
            bool inspectionAvailable,
            bool expectationSatisfied,
            bool resolutionSettled,
            bool timedOut)
        {
            if (!inspectionAvailable)
            {
                return timedOut
                    ? PackageManagerResolutionNextAction.Timeout
                    : PackageManagerResolutionNextAction.Wait;
            }

            if (expectationSatisfied &&
                (!resolveRequested || resolutionSettled))
                return PackageManagerResolutionNextAction.Complete;

            if (timedOut)
                return PackageManagerResolutionNextAction.Timeout;

            if (!resolveRequested)
            {
                return gitOperationBusy
                    ? PackageManagerResolutionNextAction.Wait
                    : PackageManagerResolutionNextAction.StartResolve;
            }

            return PackageManagerResolutionNextAction.Wait;
        }

        internal static bool IsExpectationSatisfied(
            PackageManagerResolutionExpectation expectation,
            bool isRegistered,
            PackageSource source,
            string expectedResolvedPath,
            string actualResolvedPath)
        {
            switch (expectation)
            {
                case PackageManagerResolutionExpectation.Absent:
                    return !isRegistered;
                case PackageManagerResolutionExpectation.Git:
                    return isRegistered && source == PackageSource.Git;
                case PackageManagerResolutionExpectation.Embedded:
                    if (!isRegistered || source != PackageSource.Embedded)
                        return false;
                    string expected =
                        PackageManagerSubmoduleSnapshotData.NormalizeFullPath(
                            expectedResolvedPath);
                    string actual =
                        PackageManagerSubmoduleSnapshotData.NormalizeFullPath(
                            actualResolvedPath);
                    StringComparison comparison = Path.DirectorySeparatorChar == '\\'
                        ? StringComparison.OrdinalIgnoreCase
                        : StringComparison.Ordinal;
                    return !string.IsNullOrEmpty(expected) &&
                           string.Equals(expected, actual, comparison);
                default:
                    return false;
            }
        }

        internal static bool TryPersistThenRequestResolve(
            PackageManagerProjectResolutionState state,
            Action persist,
            Action requestResolve,
            out string error)
        {
            if (state == null)
            {
                error = "Unity package resolution state is missing.";
                return false;
            }

            long requestedUtcTicks = DateTime.UtcNow.Ticks;
            state.ResolveRequested = true;
            state.ResolveRequestedUtcTicks = requestedUtcTicks;
            try
            {
                persist?.Invoke();
            }
            catch (Exception exception)
            {
                state.ResolveRequested = false;
                state.ResolveRequestedUtcTicks = 0L;
                error = SanitizeMessage(
                    "Unity package resolution state could not be persisted: " +
                    exception.Message);
                return false;
            }

            try
            {
                requestResolve?.Invoke();
                error = string.Empty;
                return true;
            }
            catch (Exception exception)
            {
                state.ResolveRequested = false;
                state.ResolveRequestedUtcTicks = 0L;
                string rollbackError = string.Empty;
                try
                {
                    // A failed native call is retryable. Persist the reset before
                    // returning so a coincidental reload cannot mistake it for an
                    // in-flight resolve that should only be observed.
                    persist?.Invoke();
                }
                catch (Exception persistException)
                {
                    // The durable marker may still say that a resolve is in
                    // flight. Match that fail-closed state in memory rather than
                    // risk issuing a second native resolve after a reload.
                    state.ResolveRequested = true;
                    state.ResolveRequestedUtcTicks = requestedUtcTicks;
                    rollbackError =
                        " The retry state could not be persisted: " +
                        persistException.Message +
                        " Automatic retry is disabled for this operation.";
                }

                error = SanitizeMessage(
                    "Unity Package Manager could not start package resolution: " +
                    exception.Message + rollbackError);
                return false;
            }
        }

        private static void Update()
        {
            if (isShuttingDown || activeState == null)
                return;

            // Observe these flags every Editor frame. A short import or compile
            // can start and finish inside the PackageInfo inspection throttle;
            // it must still restart the full quiet interval.
            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
            {
                expectationSatisfiedSinceUtcTicks = 0L;
                return;
            }

            if (activeState.ResolveRequested &&
                !registeredPackagesChanged &&
                EditorApplication.timeSinceStartup < nextInspectionNotBefore)
            {
                return;
            }

            nextInspectionNotBefore =
                EditorApplication.timeSinceStartup + 1d;
            bool inspectionAvailable = TryInspectExpectation(
                activeState,
                out bool expectationSatisfied);
            bool resolutionSettled = UpdateResolutionSettlement(
                activeState,
                inspectionAvailable && expectationSatisfied);

            PackageManagerResolutionNextAction action = DetermineNextAction(
                activeState.ResolveRequested,
                GitOperationService.IsBusy,
                inspectionAvailable,
                expectationSatisfied,
                resolutionSettled,
                HasTimedOut(activeState));
            switch (action)
            {
                case PackageManagerResolutionNextAction.StartResolve:
                    StartResolve();
                    break;
                case PackageManagerResolutionNextAction.Complete:
                    CompleteResolution();
                    break;
                case PackageManagerResolutionNextAction.Timeout:
                    string packageName = activeState.PackageName;
                    PackageManagerResolutionExpectation expectation =
                        activeState.Expectation;
                    ClearActiveState();
                    Debug.LogWarning(
                        "[Git Submodule Manager] Unity Package Manager did not " +
                        $"publish the expected {expectation} state for " +
                        $"{packageName} before the resolution timeout. " +
                        "Use Package Manager's refresh action or restart the Editor.");
                    break;
            }

            registeredPackagesChanged = false;
        }

        private static void StartResolve()
        {
            PackageManagerProjectResolutionState state = activeState;
            if (state == null)
                return;

            double now = EditorApplication.timeSinceStartup;
            if (now < nextResolveAttemptNotBefore)
                return;
            nextResolveAttemptNotBefore = now + ResolveRetryDelaySeconds;

            bool started = TryPersistThenRequestResolve(
                state,
                () => SaveActiveState(state),
                Client.Resolve,
                out string error);
            if (started)
            {
                nextResolveAttemptNotBefore = 0d;
                nextResolveWarningNotBefore = 0d;
                return;
            }

            if (now >= nextResolveWarningNotBefore)
            {
                nextResolveWarningNotBefore =
                    now + ResolveWarningIntervalSeconds;
                Debug.LogWarning(
                    "[Git Submodule Manager] " + error +
                    (state.ResolveRequested
                        ? " Wait for the current recovery timeout before trying another package mutation."
                        : " Unity will retry automatically."));
            }
        }

        private static bool UpdateResolutionSettlement(
            PackageManagerProjectResolutionState state,
            bool expectationSatisfied)
        {
            bool compiling = EditorApplication.isCompiling;
            bool updating = EditorApplication.isUpdating;
            long nowUtcTicks = DateTime.UtcNow.Ticks;
            expectationSatisfiedSinceUtcTicks = UpdateQuietIntervalStart(
                state?.ResolveRequested == true,
                expectationSatisfied,
                resolutionProgressObserved,
                compiling,
                updating,
                expectationSatisfiedSinceUtcTicks,
                nowUtcTicks);
            if (expectationSatisfiedSinceUtcTicks <= 0L)
                return false;

            return IsResolutionSettled(
                state?.ResolveRequested == true,
                expectationSatisfied,
                resolutionProgressObserved,
                compiling,
                updating,
                expectationSatisfiedSinceUtcTicks,
                nowUtcTicks,
                ResolutionQuietIntervalTicks);
        }

        internal static long UpdateQuietIntervalStart(
            bool resolveRequested,
            bool expectationSatisfied,
            bool progressObserved,
            bool isCompiling,
            bool isUpdating,
            long currentStartUtcTicks,
            long nowUtcTicks)
        {
            if (!resolveRequested || !expectationSatisfied ||
                !progressObserved || isCompiling || isUpdating)
            {
                return 0L;
            }

            return currentStartUtcTicks > 0L
                ? currentStartUtcTicks
                : nowUtcTicks;
        }

        internal static bool IsResolutionSettled(
            bool resolveRequested,
            bool expectationSatisfied,
            bool progressObserved,
            bool isCompiling,
            bool isUpdating,
            long expectationSatisfiedSinceUtcTicks,
            long nowUtcTicks,
            long quietIntervalTicks)
        {
            return resolveRequested && expectationSatisfied && progressObserved &&
                   !isCompiling && !isUpdating &&
                   expectationSatisfiedSinceUtcTicks > 0L &&
                   quietIntervalTicks >= 0L &&
                   nowUtcTicks >= expectationSatisfiedSinceUtcTicks &&
                   nowUtcTicks - expectationSatisfiedSinceUtcTicks >=
                   quietIntervalTicks;
        }

        private static bool TryInspectExpectation(
            PackageManagerProjectResolutionState state,
            out bool expectationSatisfied)
        {
            expectationSatisfied = false;
            try
            {
                UpmPackageInfo match = null;
                UpmPackageInfo[] packages = UpmPackageInfo.GetAllRegisteredPackages();
                if (packages == null)
                    return false;
                foreach (UpmPackageInfo package in packages)
                {
                    if (package != null && string.Equals(
                            package.name,
                            state.PackageName,
                            StringComparison.Ordinal))
                    {
                        match = package;
                        break;
                    }
                }

                expectationSatisfied = IsExpectationSatisfied(
                    state.Expectation,
                    match != null,
                    match?.source ?? PackageSource.Unknown,
                    state.ExpectedResolvedPath,
                    match?.resolvedPath);
                return true;
            }
            catch
            {
                // Registered packages are temporarily unavailable while UPM is
                // resolving or Unity is reloading. The next update retries.
                return false;
            }
        }

        private static void CompleteResolution()
        {
            ClearActiveState();
            try
            {
                PackageManagerSubmoduleSnapshot.Refresh();
            }
            catch (Exception exception)
            {
                Debug.LogWarning(
                    "[Git Submodule Manager] Package resolution completed, but " +
                    "the submodule snapshot could not refresh: " +
                    SanitizeMessage(exception.Message));
            }
        }

        private static bool HasTimedOut(
            PackageManagerProjectResolutionState state)
        {
            if (state == null || state.StartedUtcTicks <= 0)
                return true;
            long elapsedTicks = DateTime.UtcNow.Ticks - state.StartedUtcTicks;
            return elapsedTicks >=
                   TimeSpan.FromMinutes(ResolutionTimeoutMinutes).Ticks;
        }

        private static string ValidateIntent(
            string operationId,
            string packageName,
            PackageManagerResolutionExpectation expectation)
        {
            if (string.IsNullOrWhiteSpace(operationId) ||
                operationId.Trim().Length > 128)
            {
                return "Unity package resolution requires a valid operation identifier.";
            }

            if (!GitUtility.IsValidUpmPackageName(packageName))
                return "Unity package resolution requires a valid UPM package name.";
            if (expectation == PackageManagerResolutionExpectation.None)
                return "Unity package resolution requires an expected package state.";
            return string.Empty;
        }

        private static string GetExpectedEmbeddedPath(string packageName)
        {
            return PackageManagerSubmoduleSnapshotData.NormalizeFullPath(
                Path.Combine(
                    GitUtility.ProjectRoot,
                    GitSubmoduleAddService.GetPackagePath(packageName)));
        }

        private static PackageManagerProjectResolutionState LoadActiveState()
        {
            string json = SessionState.GetString(ActiveStateKey, string.Empty);
            if (string.IsNullOrWhiteSpace(json))
                return null;

            try
            {
                PackageManagerProjectResolutionState state =
                    JsonUtility.FromJson<PackageManagerProjectResolutionState>(json);
                if (state == null ||
                    state.SchemaVersion != CurrentSchemaVersion ||
                    !string.IsNullOrEmpty(ValidateIntent(
                        state.OperationId,
                        state.PackageName,
                        state.Expectation)) ||
                    state.StartedUtcTicks <= 0)
                {
                    SessionState.EraseString(ActiveStateKey);
                    return null;
                }

                if (state.ResolveRequested &&
                    state.ResolveRequestedUtcTicks <= 0)
                {
                    // State written by an earlier domain of this unreleased
                    // schema still has a safe lower bound for the settle delay.
                    state.ResolveRequestedUtcTicks = state.StartedUtcTicks;
                }

                if (state.Expectation == PackageManagerResolutionExpectation.Embedded &&
                    !string.Equals(
                        PackageManagerSubmoduleSnapshotData.NormalizeFullPath(
                            state.ExpectedResolvedPath),
                        GetExpectedEmbeddedPath(state.PackageName),
                        Path.DirectorySeparatorChar == '\\'
                            ? StringComparison.OrdinalIgnoreCase
                            : StringComparison.Ordinal))
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

        private static void SaveActiveState(
            PackageManagerProjectResolutionState state)
        {
            SessionState.SetString(
                ActiveStateKey,
                JsonUtility.ToJson(state));
        }

        private static void ClearActiveState()
        {
            activeState = null;
            UnsubscribeActiveEvents();
            registeredPackagesChanged = false;
            resolutionProgressObserved = false;
            nextInspectionNotBefore = 0d;
            nextResolveAttemptNotBefore = 0d;
            nextResolveWarningNotBefore = 0d;
            expectationSatisfiedSinceUtcTicks = 0L;
            SessionState.EraseString(ActiveStateKey);
        }

        private static void SubscribeActiveEvents()
        {
            if (activeState == null || activeEventsSubscribed || isShuttingDown)
                return;

            activeEventsSubscribed = true;
            EditorApplication.update += Update;
            Events.registeredPackages += OnRegisteredPackages;
            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;
        }

        private static void UnsubscribeActiveEvents()
        {
            if (!activeEventsSubscribed)
                return;

            activeEventsSubscribed = false;
            EditorApplication.update -= Update;
            Events.registeredPackages -= OnRegisteredPackages;
            AssemblyReloadEvents.beforeAssemblyReload -= OnBeforeAssemblyReload;
        }

        private static void OnRegisteredPackages(PackageRegistrationEventArgs args)
        {
            if (activeState?.ResolveRequested != true ||
                !ContainsPackageName(args, activeState.PackageName))
            {
                return;
            }

            registeredPackagesChanged = true;
            resolutionProgressObserved = true;
            // Each relevant event restarts the quiet interval, so an event burst
            // cannot make two adjacent Editor frames look like a settled graph.
            expectationSatisfiedSinceUtcTicks = 0L;
        }

        private static bool ContainsPackageName(
            PackageRegistrationEventArgs args,
            string packageName)
        {
            if (args == null || string.IsNullOrEmpty(packageName))
                return false;
            return ContainsPackageName(args.added, packageName) ||
                   ContainsPackageName(args.removed, packageName) ||
                   ContainsPackageName(args.changedFrom, packageName) ||
                   ContainsPackageName(args.changedTo, packageName);
        }

        private static bool ContainsPackageName(
            System.Collections.Generic.IEnumerable<UpmPackageInfo> packages,
            string packageName)
        {
            if (packages == null)
                return false;
            foreach (UpmPackageInfo package in packages)
            {
                if (package != null && string.Equals(
                        package.name,
                        packageName,
                        StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        private static void OnBeforeAssemblyReload()
        {
            isShuttingDown = true;
            UnsubscribeActiveEvents();
        }

        private static string SanitizeMessage(string message)
        {
            return GitHubUtility.SanitizeUiDiagnostic(
                GitUtility.RedactCredentials(message ?? string.Empty));
        }
    }
}
