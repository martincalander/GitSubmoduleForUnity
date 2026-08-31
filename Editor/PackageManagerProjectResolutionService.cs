using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEditor.PackageManager.Requests;
using UnityEngine;
using UpmPackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace MartinCalander.GitSubmoduleManager.Editor
{
    internal enum PackageManagerResolutionExpectation
    {
        None,
        Absent,
        Embedded,
        Git,
        NotEmbedded
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
        public string[] PackageNames = Array.Empty<string>();
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
        private const double ResolveRetryIntervalSeconds = 15d;
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

        internal static bool ContainsPackage(string packageName)
        {
            if (activeState == null || string.IsNullOrWhiteSpace(packageName))
                return false;

            string normalizedPackageName = packageName.Trim();
            string[] packageNames = GetPackageNames(activeState);
            for (int index = 0; index < packageNames.Length; index++)
            {
                if (string.Equals(
                        packageNames[index],
                        normalizedPackageName,
                        StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        internal static string BuildUnavailableMessage()
        {
            return IsBusy
                ? $"Unity Package Manager is finishing resolution of " +
                  $"{BuildPackageDescription(activeState)} automatically."
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
            return TryPrepare(
                operationId,
                new[] { packageName },
                expectation,
                out error);
        }

        internal static bool TryPrepare(
            string operationId,
            IReadOnlyList<string> packageNames,
            PackageManagerResolutionExpectation expectation,
            out string error)
        {
            error = ValidateIntent(
                operationId,
                packageNames,
                expectation,
                out string[] normalizedPackageNames);
            if (!string.IsNullOrEmpty(error))
                return false;

            string normalizedPackageName = normalizedPackageNames[0];
            string normalizedOperationId = operationId.Trim();
            if (activeState != null)
            {
                if (string.Equals(
                        activeState.OperationId,
                        normalizedOperationId,
                        StringComparison.Ordinal) &&
                    SamePackageNames(
                        GetPackageNames(activeState),
                        normalizedPackageNames) &&
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
                PackageNames = normalizedPackageNames,
                ExpectedResolvedPath =
                    expectation == PackageManagerResolutionExpectation.Embedded ||
                    expectation == PackageManagerResolutionExpectation.NotEmbedded
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
            return DetermineNextAction(
                resolveRequested,
                gitOperationBusy,
                inspectionAvailable,
                expectationSatisfied,
                resolutionSettled,
                false,
                timedOut);
        }

        internal static PackageManagerResolutionNextAction DetermineNextAction(
            bool resolveRequested,
            bool gitOperationBusy,
            bool inspectionAvailable,
            bool expectationSatisfied,
            bool resolutionSettled,
            bool retryDue,
            bool timedOut)
        {
            if (!inspectionAvailable)
            {
                if (timedOut)
                    return PackageManagerResolutionNextAction.Timeout;
                if ((!resolveRequested || retryDue) && !gitOperationBusy)
                    return PackageManagerResolutionNextAction.StartResolve;
                return PackageManagerResolutionNextAction.Wait;
            }

            if (expectationSatisfied &&
                (!resolveRequested || resolutionSettled))
                return PackageManagerResolutionNextAction.Complete;

            if (timedOut)
                return PackageManagerResolutionNextAction.Timeout;

            if (!resolveRequested || retryDue)
            {
                return gitOperationBusy
                    ? PackageManagerResolutionNextAction.Wait
                    : PackageManagerResolutionNextAction.StartResolve;
            }

            return PackageManagerResolutionNextAction.Wait;
        }

        internal static bool IsResolveRetryDue(
            bool resolveRequested,
            bool expectationSatisfied,
            long resolveRequestedUtcTicks,
            long nowUtcTicks,
            long retryIntervalTicks)
        {
            return resolveRequested &&
                   !expectationSatisfied &&
                   resolveRequestedUtcTicks > 0L &&
                   retryIntervalTicks >= 0L &&
                   nowUtcTicks >= resolveRequestedUtcTicks &&
                   nowUtcTicks - resolveRequestedUtcTicks >= retryIntervalTicks;
        }

        internal static bool IsExpectationSatisfied(
            PackageManagerResolutionExpectation expectation,
            bool isRegistered,
            bool isDirectDependency,
            PackageSource source,
            string expectedResolvedPath,
            string actualResolvedPath)
        {
            switch (expectation)
            {
                case PackageManagerResolutionExpectation.Absent:
                    return !isRegistered || !isDirectDependency;
                case PackageManagerResolutionExpectation.NotEmbedded:
                    if (!isRegistered || source != PackageSource.Embedded)
                        return true;
                    string removedExpected =
                        PackageManagerSubmoduleSnapshotData.NormalizeFullPath(
                            expectedResolvedPath);
                    string removedActual =
                        PackageManagerSubmoduleSnapshotData.NormalizeFullPath(
                            actualResolvedPath);
                    StringComparison removedComparison =
                        Path.DirectorySeparatorChar == '\\'
                            ? StringComparison.OrdinalIgnoreCase
                            : StringComparison.Ordinal;
                    return !string.IsNullOrEmpty(removedExpected) &&
                           !string.IsNullOrEmpty(removedActual) &&
                           !string.Equals(
                               removedExpected,
                               removedActual,
                               removedComparison);
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

            bool previousResolveRequested = state.ResolveRequested;
            long previousResolveRequestedUtcTicks =
                state.ResolveRequestedUtcTicks;
            long requestedUtcTicks = DateTime.UtcNow.Ticks;
            state.ResolveRequested = true;
            state.ResolveRequestedUtcTicks = requestedUtcTicks;
            try
            {
                persist?.Invoke();
            }
            catch (Exception exception)
            {
                state.ResolveRequested = previousResolveRequested;
                state.ResolveRequestedUtcTicks =
                    previousResolveRequestedUtcTicks;
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
                    // flight. Match it in memory; forced Resolve is idempotent,
                    // so the normal persisted retry deadline remains safe.
                    state.ResolveRequested = true;
                    state.ResolveRequestedUtcTicks = requestedUtcTicks;
                    rollbackError =
                        " The retry state could not be persisted: " +
                        persistException.Message +
                        " Automatic retry will resume from the durable marker.";
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
            bool retryDue = IsResolveRetryDue(
                activeState.ResolveRequested,
                expectationSatisfied,
                activeState.ResolveRequestedUtcTicks,
                DateTime.UtcNow.Ticks,
                TimeSpan.FromSeconds(ResolveRetryIntervalSeconds).Ticks);

            PackageManagerResolutionNextAction action = DetermineNextAction(
                activeState.ResolveRequested,
                GitOperationService.IsBusy,
                inspectionAvailable,
                expectationSatisfied,
                resolutionSettled,
                retryDue,
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
                    string packageDescription =
                        BuildPackageDescription(activeState);
                    PackageManagerResolutionExpectation expectation =
                        activeState.Expectation;
                    ClearActiveState();
                    Debug.LogWarning(
                        "[Git Submodule Manager] Automatic Unity Package Manager " +
                        $"resolution did not publish the expected {expectation} " +
                        $"state for {packageDescription} after repeated attempts. Review " +
                        "Unity Package Manager diagnostics before another package " +
                        "mutation.");
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
                resolutionProgressObserved = true;
                expectationSatisfiedSinceUtcTicks = 0L;
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
                UpmPackageInfo[] packages = UpmPackageInfo.GetAllRegisteredPackages();
                if (packages == null)
                    return false;

                var matches = new Dictionary<string, UpmPackageInfo>(
                    StringComparer.Ordinal);
                foreach (UpmPackageInfo package in packages)
                {
                    if (package != null &&
                        !string.IsNullOrWhiteSpace(package.name))
                        matches[package.name] = package;
                }

                string[] packageNames = GetPackageNames(state);
                expectationSatisfied = packageNames.Length > 0;
                for (int index = 0; index < packageNames.Length; index++)
                {
                    matches.TryGetValue(packageNames[index], out UpmPackageInfo match);
                    string expectedResolvedPath =
                        state.Expectation ==
                            PackageManagerResolutionExpectation.Embedded ||
                        state.Expectation ==
                            PackageManagerResolutionExpectation.NotEmbedded
                            ? GetExpectedEmbeddedPath(packageNames[index])
                            : string.Empty;
                    if (IsExpectationSatisfied(
                            state.Expectation,
                            match != null,
                            match?.isDirectDependency ?? false,
                            match?.source ?? PackageSource.Unknown,
                            expectedResolvedPath,
                            match?.resolvedPath))
                    {
                        continue;
                    }

                    expectationSatisfied = false;
                    break;
                }
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
            return ValidateIntent(
                operationId,
                new[] { packageName },
                expectation,
                out _);
        }

        private static string ValidateIntent(
            string operationId,
            IReadOnlyList<string> packageNames,
            PackageManagerResolutionExpectation expectation,
            out string[] normalizedPackageNames)
        {
            normalizedPackageNames = Array.Empty<string>();
            if (string.IsNullOrWhiteSpace(operationId) ||
                operationId.Trim().Length > 128)
            {
                return "Unity package resolution requires a valid operation identifier.";
            }

            if (expectation == PackageManagerResolutionExpectation.None)
                return "Unity package resolution requires an expected package state.";

            if (packageNames == null || packageNames.Count == 0 ||
                packageNames.Count > PackageManifestGitDependencyStore
                    .MaximumDependencyCount)
            {
                return "Unity package resolution requires at least one bounded UPM package identity.";
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            var names = new string[packageNames.Count];
            for (int index = 0; index < packageNames.Count; index++)
            {
                string packageName = packageNames[index]?.Trim() ?? string.Empty;
                if (!GitUtility.IsValidUpmPackageName(packageName) ||
                    !seen.Add(packageName))
                {
                    return "Unity package resolution requires distinct, valid UPM package names.";
                }
                names[index] = packageName;
            }

            normalizedPackageNames = names;
            return string.Empty;
        }

        private static string[] GetPackageNames(
            PackageManagerProjectResolutionState state)
        {
            if (state?.PackageNames != null && state.PackageNames.Length > 0)
                return state.PackageNames;
            return string.IsNullOrWhiteSpace(state?.PackageName)
                ? Array.Empty<string>()
                : new[] { state.PackageName.Trim() };
        }

        private static bool SamePackageNames(
            IReadOnlyList<string> left,
            IReadOnlyList<string> right)
        {
            if (left == null || right == null || left.Count != right.Count)
                return false;
            for (int index = 0; index < left.Count; index++)
            {
                if (!string.Equals(left[index], right[index], StringComparison.Ordinal))
                    return false;
            }
            return true;
        }

        private static string BuildPackageDescription(
            PackageManagerProjectResolutionState state)
        {
            string[] names = GetPackageNames(state);
            if (names.Length == 0)
                return "the current packages";
            return names.Length == 1
                ? names[0]
                : names.Length + " packages";
        }

        internal static string GetExpectedEmbeddedPath(string packageName)
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
                string[] packageNames = GetPackageNames(state);
                if (state == null ||
                    state.SchemaVersion != CurrentSchemaVersion ||
                    !string.IsNullOrEmpty(ValidateIntent(
                        state.OperationId,
                        packageNames,
                        state.Expectation,
                        out string[] normalizedPackageNames)) ||
                    state.StartedUtcTicks <= 0)
                {
                    SessionState.EraseString(ActiveStateKey);
                    return null;
                }

                state.PackageNames = normalizedPackageNames;
                state.PackageName = normalizedPackageNames[0];

                if (state.ResolveRequested &&
                    state.ResolveRequestedUtcTicks <= 0)
                {
                    // State written by an earlier domain of this unreleased
                    // schema still has a safe lower bound for the settle delay.
                    state.ResolveRequestedUtcTicks = state.StartedUtcTicks;
                }

                if ((state.Expectation ==
                         PackageManagerResolutionExpectation.Embedded ||
                     state.Expectation ==
                         PackageManagerResolutionExpectation.NotEmbedded) &&
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

        internal static PackageManagerProjectResolutionState
            LoadPersistedStateForTests()
        {
            return LoadActiveState();
        }

        internal static void ClearStateForTests()
        {
            ClearActiveState();
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
                !ContainsPackageName(args, GetPackageNames(activeState)))
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
            IReadOnlyList<string> packageNames)
        {
            if (args == null || packageNames == null || packageNames.Count == 0)
                return false;
            for (int index = 0; index < packageNames.Count; index++)
            {
                if (ContainsPackageName(args.added, packageNames[index]) ||
                    ContainsPackageName(args.removed, packageNames[index]) ||
                    ContainsPackageName(args.changedFrom, packageNames[index]) ||
                    ContainsPackageName(args.changedTo, packageNames[index]))
                {
                    return true;
                }
            }
            return false;
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

    internal enum PackageManagerNativeRemoveHandoffAction
    {
        Wait,
        StartOrdinaryRemoval,
        Complete,
        Timeout
    }

    [Serializable]
    internal sealed class PackageManagerNativeRemoveHandoffState
    {
        public int SchemaVersion = 2;
        public string OperationId = string.Empty;
        public string[] RemovedSubmodulePackageNames = Array.Empty<string>();
        public string[] OrdinaryPackageNames = Array.Empty<string>();
        public string[] OrdinaryPackageSpecs = Array.Empty<string>();
        public long StartedUtcTicks;
        public long OrdinaryRemovalStartedUtcTicks;
        public bool RequestIssued;
        public long RequestIssuedUtcTicks;
        public int RequestAttemptCount;
        public bool AutomaticRetryAuthorized;
    }

    /// <summary>
    /// Resumes the ordinary Unity Package Manager portion of a mixed native
    /// Remove action after the Git batch and its package resolution have fully
    /// released repository and reload ownership. The write-ahead marker keeps a
    /// domain reload from issuing the ordinary removal more than once.
    /// </summary>
    [InitializeOnLoad]
    internal static class PackageManagerNativeRemoveHandoffService
    {
        private const int CurrentSchemaVersion = 2;
        private const int LegacySchemaVersion = 1;
        private const double HandoffTimeoutMinutes = 10d;
        private const double RequestRetryDelaySeconds = 5d;
        private const double ReloadRequestGraceSeconds = 15d;
        private const int MaximumAutomaticRequestAttempts = 5;
        private const string ActiveStateKey =
            "MartinCalander.GitSubmoduleManager.NativeRemoveHandoff.Active.v1";

        private static PackageManagerNativeRemoveHandoffState activeState;
        private static AddAndRemoveRequest activeRequest;
        private static bool updateSubscribed;
        private static bool isShuttingDown;
        private static double nextInspectionNotBefore;
        private static double nextRemovalAttemptNotBefore;

        static PackageManagerNativeRemoveHandoffService()
        {
            activeState = LoadActiveState();
            SubscribeUpdate();
        }

        internal static bool IsBusy => activeState != null;

        internal static string BuildUnavailableMessage()
        {
            return IsBusy
                ? "Unity Package Manager is finishing the current multi-package " +
                  "Remove action automatically."
                : string.Empty;
        }

        internal static bool ContainsPackage(string packageName)
        {
            if (activeState == null || string.IsNullOrWhiteSpace(packageName))
                return false;
            return Contains(activeState.RemovedSubmodulePackageNames, packageName) ||
                   Contains(activeState.OrdinaryPackageNames, packageName);
        }

        internal static void CancelPrepared(string operationId)
        {
            if (activeState == null || activeState.RequestIssued ||
                !string.Equals(
                    activeState.OperationId,
                    operationId?.Trim(),
                    StringComparison.Ordinal))
            {
                return;
            }

            ClearActiveState();
        }

        internal static bool TryPrepare(
            string operationId,
            IReadOnlyList<string> removedSubmodulePackageNames,
            IReadOnlyList<string> ordinaryPackageNames,
            IReadOnlyList<string> ordinaryPackageSpecs,
            out string error)
        {
            if (ordinaryPackageNames == null || ordinaryPackageNames.Count == 0)
            {
                error = string.Empty;
                return true;
            }

            if (string.IsNullOrWhiteSpace(operationId) ||
                operationId.Trim().Length > 128)
            {
                error = "The multi-package Remove handoff has no valid operation identity.";
                return false;
            }

            if (!TryCopyDistinctPackageNames(
                    removedSubmodulePackageNames,
                    out string[] submodules,
                    out error) ||
                submodules.Length == 0)
            {
                if (string.IsNullOrWhiteSpace(error))
                {
                    error = "The multi-package Remove handoff has no verified " +
                            "Git submodule identity.";
                }
                return false;
            }

            if (!TryCopyDistinctPackageNames(
                    ordinaryPackageNames,
                    out string[] ordinary,
                    out error))
            {
                return false;
            }
            if (!TryCopyDependencySpecs(
                    ordinaryPackageSpecs,
                    ordinary.Length,
                    out string[] specs,
                    out error))
            {
                return false;
            }

            var allNames = new HashSet<string>(submodules, StringComparer.Ordinal);
            for (int index = 0; index < ordinary.Length; index++)
            {
                if (!allNames.Add(ordinary[index]))
                {
                    error = "The multi-package Remove handoff contains a " +
                            "duplicate package identity.";
                    return false;
                }
            }

            string normalizedOperationId = operationId.Trim();
            if (activeState != null)
            {
                if (string.Equals(
                        activeState.OperationId,
                        normalizedOperationId,
                        StringComparison.Ordinal) &&
                    SamePackageNames(
                        activeState.RemovedSubmodulePackageNames,
                        submodules) &&
                    SamePackageNames(
                        activeState.OrdinaryPackageNames,
                        ordinary) &&
                    SamePackageNames(
                        activeState.OrdinaryPackageSpecs,
                        specs))
                {
                    error = string.Empty;
                    return true;
                }

                error = string.Equals(
                    activeState.OperationId,
                    normalizedOperationId,
                    StringComparison.Ordinal)
                    ? "The multi-package Remove handoff identity changed after " +
                      "it was persisted. The stored packages were preserved."
                    : BuildUnavailableMessage();
                return false;
            }

            var prepared = new PackageManagerNativeRemoveHandoffState
            {
                SchemaVersion = CurrentSchemaVersion,
                OperationId = normalizedOperationId,
                RemovedSubmodulePackageNames = submodules,
                OrdinaryPackageNames = ordinary,
                OrdinaryPackageSpecs = specs,
                StartedUtcTicks = DateTime.UtcNow.Ticks,
                OrdinaryRemovalStartedUtcTicks = 0L,
                RequestIssued = false,
                RequestIssuedUtcTicks = 0L,
                RequestAttemptCount = 0,
                AutomaticRetryAuthorized = true
            };

            try
            {
                SaveActiveState(prepared);
                activeState = prepared;
                nextInspectionNotBefore = 0d;
                nextRemovalAttemptNotBefore = 0d;
                SubscribeUpdate();
                error = string.Empty;
                return true;
            }
            catch (Exception exception)
            {
                error = SanitizeMessage(
                    "The ordinary-package Remove handoff could not be " +
                    "persisted: " + exception.Message);
                return false;
            }
        }

        internal static PackageManagerNativeRemoveHandoffAction
            DetermineNextAction(
                bool requestIssued,
                bool gitOperationBusy,
                bool projectResolutionBusy,
                bool editorBusy,
                bool inspectionAvailable,
                bool allSubmodulesAbsent,
                bool allOrdinaryPackagesAbsent,
                bool timedOut)
        {
            if (!inspectionAvailable)
            {
                return timedOut
                    ? PackageManagerNativeRemoveHandoffAction.Timeout
                    : PackageManagerNativeRemoveHandoffAction.Wait;
            }

            if (allSubmodulesAbsent && allOrdinaryPackagesAbsent)
                return PackageManagerNativeRemoveHandoffAction.Complete;
            if (timedOut)
                return PackageManagerNativeRemoveHandoffAction.Timeout;
            if (requestIssued || gitOperationBusy || projectResolutionBusy ||
                editorBusy || !allSubmodulesAbsent)
            {
                return PackageManagerNativeRemoveHandoffAction.Wait;
            }

            return PackageManagerNativeRemoveHandoffAction.StartOrdinaryRemoval;
        }

        internal static bool TryPersistThenRequestRemoval(
            PackageManagerNativeRemoveHandoffState state,
            Action persist,
            Action requestRemoval,
            out string error)
        {
            if (state == null)
            {
                error = "The ordinary-package Remove handoff state is missing.";
                return false;
            }

            bool previousRequestIssued = state.RequestIssued;
            long previousRequestIssuedUtcTicks = state.RequestIssuedUtcTicks;
            int previousRequestAttemptCount = state.RequestAttemptCount;
            long previousOrdinaryRemovalStartedUtcTicks =
                state.OrdinaryRemovalStartedUtcTicks;
            if (state.OrdinaryRemovalStartedUtcTicks <= 0L)
                state.OrdinaryRemovalStartedUtcTicks = DateTime.UtcNow.Ticks;
            state.RequestIssued = true;
            state.RequestIssuedUtcTicks = DateTime.UtcNow.Ticks;
            state.RequestAttemptCount = Math.Max(0, state.RequestAttemptCount) + 1;
            try
            {
                persist?.Invoke();
            }
            catch (Exception exception)
            {
                state.RequestIssued = previousRequestIssued;
                state.RequestIssuedUtcTicks = previousRequestIssuedUtcTicks;
                state.RequestAttemptCount = previousRequestAttemptCount;
                state.OrdinaryRemovalStartedUtcTicks =
                    previousOrdinaryRemovalStartedUtcTicks;
                error = SanitizeMessage(
                    "The ordinary-package Remove request was not started " +
                    "because its write-ahead marker could not be saved: " +
                    exception.Message);
                return false;
            }

            try
            {
                requestRemoval?.Invoke();
                error = string.Empty;
                return true;
            }
            catch (Exception exception)
            {
                // Keep the write-ahead marker set until the caller classifies
                // whether Unity definitively rejected the request. An uncertain
                // exception remains observation-only until the reload grace
                // period makes an exact-spec retry safe.
                error = SanitizeMessage(
                    "Unity Package Manager could not start removal of the " +
                    "ordinary selected packages: " + exception.Message);
                return false;
            }
        }

        private static void Update()
        {
            PackageManagerNativeRemoveHandoffState state = activeState;
            if (isShuttingDown || state == null)
                return;

            bool editorBusy =
                EditorApplication.isCompiling || EditorApplication.isUpdating;
            if (editorBusy)
                return;

            if (state.RequestIssued && activeRequest != null)
            {
                try
                {
                    if (!activeRequest.IsCompleted)
                        return;
                    if (activeRequest.Status == StatusCode.Failure)
                    {
                        string requestError = activeRequest.Error?.message ??
                                              "Unity rejected the package removal request.";
                        activeRequest = null;
                        ScheduleAutomaticRemovalRetry(state, requestError);
                        return;
                    }

                    activeRequest = null;
                }
                catch (Exception exception)
                {
                    activeRequest = null;
                    Debug.LogWarning(
                        "[Git Submodule Manager] The automatic package removal " +
                        "request could not be inspected. Its exact targets will " +
                        "be reconciled automatically: " +
                        SanitizeMessage(exception.Message));
                }
            }

            if (EditorApplication.timeSinceStartup < nextInspectionNotBefore)
                return;
            nextInspectionNotBefore = EditorApplication.timeSinceStartup + 1d;

            bool inspectionAvailable = TryInspectPackageGraph(
                out HashSet<string> directPackageNames,
                out HashSet<string> embeddedPackageNames);
            bool submodulesAbsent = inspectionAvailable &&
                                    AllAbsent(
                                        state.RemovedSubmodulePackageNames,
                                        embeddedPackageNames);
            string[] exactRemaining = Array.Empty<string>();
            if (inspectionAvailable && submodulesAbsent &&
                !TryFindExactRemainingPackageNames(
                    state,
                    out exactRemaining,
                    out string manifestError,
                    out bool retryUnsafe))
            {
                if (retryUnsafe)
                {
                    StopAutomaticRemoval(state, manifestError);
                    return;
                }

                inspectionAvailable = false;
            }

            bool ordinaryAbsent = inspectionAvailable &&
                                  submodulesAbsent &&
                                  AreOrdinaryTargetsAbsent(
                                      AllAbsent(
                                          state.OrdinaryPackageNames,
                                          directPackageNames),
                                      exactRemaining);
            bool timedOut = HasTimedOut(state);
            if (inspectionAvailable &&
                submodulesAbsent &&
                !ordinaryAbsent &&
                ShouldRecoverIssuedRequest(
                    state,
                    activeRequest != null,
                    DateTime.UtcNow.Ticks,
                    TimeSpan.FromSeconds(ReloadRequestGraceSeconds).Ticks,
                    timedOut))
            {
                if (exactRemaining.Length == 0)
                {
                    RequestAutomaticGraphResolve(state);
                    return;
                }

                if (state.RequestAttemptCount >=
                    MaximumAutomaticRequestAttempts)
                {
                    StopAutomaticRemoval(
                        state,
                        "Unity Package Manager did not remove the unchanged " +
                        "exact targets after repeated automatic attempts.");
                    return;
                }

                if (TryResetIssuedRequestForRetry(state, out string resetError))
                    return;

                StopAutomaticRemoval(state, resetError);
                return;
            }

            PackageManagerNativeRemoveHandoffAction action = DetermineNextAction(
                state.RequestIssued,
                GitOperationService.IsBusy,
                PackageManagerProjectResolutionService.IsBusy,
                editorBusy,
                inspectionAvailable,
                submodulesAbsent,
                ordinaryAbsent,
                timedOut);
            switch (action)
            {
                case PackageManagerNativeRemoveHandoffAction.StartOrdinaryRemoval:
                    StartOrdinaryRemoval(state, directPackageNames);
                    break;
                case PackageManagerNativeRemoveHandoffAction.Complete:
                    ClearActiveState();
                    PackageManagerSubmoduleSnapshot.Refresh();
                    break;
                case PackageManagerNativeRemoveHandoffAction.Timeout:
                    string pendingNames = string.Join(
                        ", ",
                        FindPendingPackageNames(
                            state,
                            embeddedPackageNames,
                            directPackageNames));
                    ClearActiveState();
                    Debug.LogWarning(
                        "[Git Submodule Manager] Automatic Unity Package Manager " +
                        "removal stopped after repeated attempts. " +
                        (string.IsNullOrWhiteSpace(pendingNames)
                            ? "Review Unity Package Manager diagnostics before another package mutation."
                            : "These exact targets remain unchanged: " + pendingNames + "."));
                    break;
            }
        }

        private static void StartOrdinaryRemoval(
            PackageManagerNativeRemoveHandoffState state,
            HashSet<string> directPackageNames)
        {
            double now = EditorApplication.timeSinceStartup;
            if (now < nextRemovalAttemptNotBefore)
                return;

            if (!TryEnsureOrdinaryRemovalStarted(state, out string phaseError))
            {
                StopAutomaticRemoval(state, phaseError);
                return;
            }

            if (!TryFindExactRemainingPackageNames(
                    state,
                    out string[] remaining,
                    out string identityError,
                    out bool retryUnsafe))
            {
                if (retryUnsafe)
                    StopAutomaticRemoval(state, identityError);
                else
                    nextRemovalAttemptNotBefore =
                        now + RequestRetryDelaySeconds;
                return;
            }

            if (remaining.Length == 0)
            {
                if (AllAbsent(state.OrdinaryPackageNames, directPackageNames))
                {
                    ClearActiveState();
                    PackageManagerSubmoduleSnapshot.Refresh();
                }
                else
                {
                    RequestAutomaticGraphResolve(state);
                }
                return;
            }

            bool definitiveRejection = false;
            AddAndRemoveRequest issuedRequest = null;
            bool started = TryPersistThenRequestRemoval(
                state,
                () => SaveActiveState(state),
                () =>
                {
                    issuedRequest = Client.AddAndRemove(
                        Array.Empty<string>(),
                        remaining);
                    if (issuedRequest == null)
                    {
                        definitiveRejection = true;
                        throw new InvalidOperationException(
                            "Unity did not create a package removal request.");
                    }

                    if (issuedRequest.Status == StatusCode.Failure)
                    {
                        definitiveRejection = true;
                        throw new InvalidOperationException(
                            issuedRequest.Error?.message ??
                            "Unity rejected the package removal request.");
                    }
                },
                out string error);
            if (started)
            {
                activeRequest = issuedRequest;
                nextRemovalAttemptNotBefore = 0d;
                return;
            }

            Debug.LogWarning("[Git Submodule Manager] " + error);
            if (definitiveRejection)
            {
                ScheduleAutomaticRemovalRetry(state, error);
                return;
            }

            if (!state.RequestIssued)
                nextRemovalAttemptNotBefore = now + RequestRetryDelaySeconds;
        }

        internal static bool ShouldRecoverIssuedRequest(
            PackageManagerNativeRemoveHandoffState state,
            bool hasLiveRequest,
            long nowUtcTicks,
            long graceTicks,
            bool timedOut)
        {
            return state?.AutomaticRetryAuthorized == true &&
                   state.RequestIssued &&
                   !hasLiveRequest &&
                   !timedOut &&
                   state.RequestIssuedUtcTicks > 0L &&
                   graceTicks >= 0L &&
                   nowUtcTicks >= state.RequestIssuedUtcTicks &&
                   nowUtcTicks - state.RequestIssuedUtcTicks >= graceTicks;
        }

        private static void ScheduleAutomaticRemovalRetry(
            PackageManagerNativeRemoveHandoffState state,
            string failure)
        {
            if (state == null)
                return;
            if (!state.AutomaticRetryAuthorized ||
                state.RequestAttemptCount >= MaximumAutomaticRequestAttempts ||
                HasTimedOut(state))
            {
                StopAutomaticRemoval(
                    state,
                    "Unity Package Manager rejected the automatic removal after " +
                    "repeated exact-target attempts. " + failure);
                return;
            }

            if (!TryResetIssuedRequestForRetry(state, out string resetError))
            {
                StopAutomaticRemoval(state, resetError);
                return;
            }

            Debug.LogWarning(
                "[Git Submodule Manager] Unity Package Manager did not complete " +
                "the ordinary-package portion of Remove. The unchanged exact " +
                "targets will retry automatically: " + SanitizeMessage(failure));
        }

        private static bool TryEnsureOrdinaryRemovalStarted(
            PackageManagerNativeRemoveHandoffState state,
            out string error)
        {
            if (state == null)
            {
                error = "The automatic ordinary-package removal state is missing.";
                return false;
            }
            if (state.OrdinaryRemovalStartedUtcTicks > 0L)
            {
                error = string.Empty;
                return true;
            }

            state.OrdinaryRemovalStartedUtcTicks = DateTime.UtcNow.Ticks;
            try
            {
                SaveActiveState(state);
                error = string.Empty;
                return true;
            }
            catch (Exception exception)
            {
                state.OrdinaryRemovalStartedUtcTicks = 0L;
                error = SanitizeMessage(
                    "The automatic ordinary-package removal phase could not be " +
                    "persisted: " + exception.Message);
                return false;
            }
        }

        private static bool TryResetIssuedRequestForRetry(
            PackageManagerNativeRemoveHandoffState state,
            out string error)
        {
            bool previousIssued = state.RequestIssued;
            long previousTicks = state.RequestIssuedUtcTicks;
            state.RequestIssued = false;
            state.RequestIssuedUtcTicks = 0L;
            try
            {
                SaveActiveState(state);
                activeRequest = null;
                nextRemovalAttemptNotBefore =
                    EditorApplication.timeSinceStartup + RequestRetryDelaySeconds;
                error = string.Empty;
                return true;
            }
            catch (Exception exception)
            {
                state.RequestIssued = previousIssued;
                state.RequestIssuedUtcTicks = previousTicks;
                error = SanitizeMessage(
                    "The automatic package removal retry could not be persisted: " +
                    exception.Message);
                return false;
            }
        }

        private static bool TryFindExactRemainingPackageNames(
            PackageManagerNativeRemoveHandoffState state,
            out string[] remaining,
            out string error,
            out bool retryUnsafe)
        {
            remaining = Array.Empty<string>();
            retryUnsafe = false;
            if (state?.AutomaticRetryAuthorized != true ||
                state.OrdinaryPackageNames == null ||
                state.OrdinaryPackageSpecs == null ||
                state.OrdinaryPackageNames.Length !=
                    state.OrdinaryPackageSpecs.Length)
            {
                retryUnsafe = true;
                error = "The automatic ordinary-package removal has no exact " +
                        "manifest identity binding.";
                return false;
            }

            var exact = new List<string>(state.OrdinaryPackageNames.Length);
            for (int index = 0; index < state.OrdinaryPackageNames.Length; index++)
            {
                string packageName = state.OrdinaryPackageNames[index];
                if (!PackageManifestGitDependencyStore.TryGetProjectDependencySpec(
                        packageName,
                        out bool exists,
                        out string currentSpec,
                        out string readError))
                {
                    error = readError;
                    return false;
                }

                if (!exists)
                    continue;
                if (!string.Equals(
                        currentSpec,
                        state.OrdinaryPackageSpecs[index],
                        StringComparison.Ordinal))
                {
                    retryUnsafe = true;
                    error = $"The direct dependency for {packageName} changed " +
                            "during automatic removal. It was preserved.";
                    return false;
                }

                exact.Add(packageName);
            }

            remaining = exact.ToArray();
            error = string.Empty;
            return true;
        }

        private static void RequestAutomaticGraphResolve(
            PackageManagerNativeRemoveHandoffState state)
        {
            long requestedUtcTicks = DateTime.UtcNow.Ticks;
            state.RequestIssued = true;
            state.RequestIssuedUtcTicks = requestedUtcTicks;
            try
            {
                SaveActiveState(state);
                Client.Resolve();
            }
            catch (Exception exception)
            {
                state.RequestIssued = false;
                state.RequestIssuedUtcTicks = 0L;
                try
                {
                    SaveActiveState(state);
                    nextRemovalAttemptNotBefore =
                        EditorApplication.timeSinceStartup +
                        RequestRetryDelaySeconds;
                }
                catch
                {
                    // The durable state still contains the fresh attempted
                    // marker. Keep matching it in memory so the reload grace
                    // bounds the next exact-target reconciliation.
                    state.RequestIssued = true;
                    state.RequestIssuedUtcTicks = requestedUtcTicks;
                }

                Debug.LogWarning(
                    "[Git Submodule Manager] Unity Package Manager could not " +
                    "refresh the automatically removed package graph. It will " +
                    "retry automatically: " + SanitizeMessage(exception.Message));
            }
        }

        private static void StopAutomaticRemoval(
            PackageManagerNativeRemoveHandoffState state,
            string reason)
        {
            string packageNames = state?.OrdinaryPackageNames == null
                ? string.Empty
                : string.Join(", ", state.OrdinaryPackageNames);
            ClearActiveState();
            Debug.LogWarning(
                "[Git Submodule Manager] Automatic ordinary-package removal " +
                "stopped safely" +
                (string.IsNullOrWhiteSpace(packageNames)
                    ? string.Empty
                    : " for " + packageNames) +
                ": " + SanitizeMessage(reason));
        }

        private static bool TryInspectPackageGraph(
            out HashSet<string> directPackageNames,
            out HashSet<string> embeddedPackageNames)
        {
            directPackageNames = null;
            embeddedPackageNames = null;
            try
            {
                UpmPackageInfo[] packages = UpmPackageInfo.GetAllRegisteredPackages();
                if (packages == null)
                    return false;

                directPackageNames = new HashSet<string>(StringComparer.Ordinal);
                embeddedPackageNames = new HashSet<string>(StringComparer.Ordinal);
                for (int index = 0; index < packages.Length; index++)
                {
                    UpmPackageInfo package = packages[index];
                    string packageName = package?.name;
                    if (string.IsNullOrWhiteSpace(packageName))
                        continue;
                    if (package.isDirectDependency)
                        directPackageNames.Add(packageName);
                    if (!PackageManagerProjectResolutionService
                            .IsExpectationSatisfied(
                                PackageManagerResolutionExpectation.NotEmbedded,
                                true,
                                package.isDirectDependency,
                                package.source,
                                PackageManagerProjectResolutionService
                                    .GetExpectedEmbeddedPath(packageName),
                                package.resolvedPath))
                    {
                        embeddedPackageNames.Add(packageName);
                    }
                }
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool AllAbsent(
            IReadOnlyList<string> packageNames,
            ISet<string> registeredPackageNames)
        {
            if (packageNames == null || registeredPackageNames == null)
                return false;
            for (int index = 0; index < packageNames.Count; index++)
            {
                if (registeredPackageNames.Contains(packageNames[index]))
                    return false;
            }
            return true;
        }

        internal static bool AreOrdinaryTargetsAbsent(
            bool registeredGraphAbsent,
            IReadOnlyList<string> exactManifestTargetsRemaining)
        {
            return registeredGraphAbsent &&
                   exactManifestTargetsRemaining != null &&
                   exactManifestTargetsRemaining.Count == 0;
        }

        internal static string[] FindPendingPackageNames(
            PackageManagerNativeRemoveHandoffState state,
            ISet<string> embeddedPackageNames,
            ISet<string> directPackageNames)
        {
            var present = new List<string>();
            AddPresent(
                state?.RemovedSubmodulePackageNames,
                embeddedPackageNames,
                present);
            AddPresent(
                state?.OrdinaryPackageNames,
                directPackageNames,
                present);
            return present.ToArray();
        }

        internal static string[] FindPresentPackageNames(
            IReadOnlyList<string> packageNames,
            ISet<string> registeredPackageNames)
        {
            var present = new List<string>();
            AddPresent(packageNames, registeredPackageNames, present);
            return present.ToArray();
        }

        internal static PackageManagerNativeRemoveHandoffState
            LoadPersistedStateForTests()
        {
            return LoadActiveState();
        }

        internal static void SavePersistedStateForTests(
            PackageManagerNativeRemoveHandoffState state)
        {
            SaveActiveState(state);
        }

        internal static void ClearStateForTests()
        {
            ClearActiveState();
        }

        private static void AddPresent(
            IReadOnlyList<string> packageNames,
            ISet<string> registeredPackageNames,
            ICollection<string> destination)
        {
            if (packageNames == null || registeredPackageNames == null)
                return;
            for (int index = 0; index < packageNames.Count; index++)
            {
                if (registeredPackageNames.Contains(packageNames[index]))
                    destination.Add(packageNames[index]);
            }
        }

        private static bool TryCopyDistinctPackageNames(
            IReadOnlyList<string> packageNames,
            out string[] copies,
            out string error)
        {
            copies = Array.Empty<string>();
            if (packageNames == null)
            {
                error = "The multi-package Remove handoff has no package list.";
                return false;
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            var result = new List<string>(packageNames.Count);
            for (int index = 0; index < packageNames.Count; index++)
            {
                string packageName = packageNames[index]?.Trim() ?? string.Empty;
                if (!GitUtility.IsValidUpmPackageName(packageName) ||
                    !seen.Add(packageName))
                {
                    error = "The multi-package Remove handoff contains an " +
                            "invalid or duplicate package identity.";
                    return false;
                }
                result.Add(packageName);
            }

            copies = result.ToArray();
            error = string.Empty;
            return true;
        }

        private static bool TryCopyDependencySpecs(
            IReadOnlyList<string> dependencySpecs,
            int expectedCount,
            out string[] copies,
            out string error)
        {
            copies = Array.Empty<string>();
            if (dependencySpecs == null ||
                dependencySpecs.Count != expectedCount ||
                expectedCount <= 0)
            {
                error = "The multi-package Remove handoff has no exact manifest " +
                        "dependency binding.";
                return false;
            }

            var result = new string[expectedCount];
            for (int index = 0; index < expectedCount; index++)
            {
                string spec = dependencySpecs[index] ?? string.Empty;
                if (string.IsNullOrWhiteSpace(spec) ||
                    spec.Length >
                    PackageManifestGitDependencyStore.MaximumGitSpecLength)
                {
                    error = "The multi-package Remove handoff contains an " +
                            "invalid dependency specification.";
                    return false;
                }
                result[index] = spec;
            }

            copies = result;
            error = string.Empty;
            return true;
        }

        private static bool Contains(
            IReadOnlyList<string> packageNames,
            string packageName)
        {
            if (packageNames == null)
                return false;
            for (int index = 0; index < packageNames.Count; index++)
            {
                if (string.Equals(
                        packageNames[index],
                        packageName,
                        StringComparison.Ordinal))
                {
                    return true;
                }
            }
            return false;
        }

        private static bool SamePackageNames(
            IReadOnlyList<string> left,
            IReadOnlyList<string> right)
        {
            if (left == null || right == null || left.Count != right.Count)
                return false;
            for (int index = 0; index < left.Count; index++)
            {
                if (!string.Equals(
                        left[index],
                        right[index],
                        StringComparison.Ordinal))
                {
                    return false;
                }
            }
            return true;
        }

        private static bool HasTimedOut(
            PackageManagerNativeRemoveHandoffState state)
        {
            if (state == null)
                return true;
            return IsHandoffPhaseTimedOut(
                state.StartedUtcTicks,
                state.OrdinaryRemovalStartedUtcTicks,
                DateTime.UtcNow.Ticks,
                TimeSpan.FromMinutes(HandoffTimeoutMinutes).Ticks);
        }

        internal static bool IsHandoffPhaseTimedOut(
            long preparedUtcTicks,
            long ordinaryRemovalStartedUtcTicks,
            long nowUtcTicks,
            long timeoutTicks)
        {
            long started = ordinaryRemovalStartedUtcTicks > 0L
                ? ordinaryRemovalStartedUtcTicks
                : preparedUtcTicks;
            if (started <= 0L)
                return true;
            return timeoutTicks >= 0L &&
                   nowUtcTicks >= started &&
                   nowUtcTicks - started >= timeoutTicks;
        }

        private static PackageManagerNativeRemoveHandoffState LoadActiveState()
        {
            string json = SessionState.GetString(ActiveStateKey, string.Empty);
            if (string.IsNullOrWhiteSpace(json))
                return null;

            try
            {
                PackageManagerNativeRemoveHandoffState state =
                    JsonUtility.FromJson<PackageManagerNativeRemoveHandoffState>(
                        json);
                if (state?.SchemaVersion == CurrentSchemaVersion &&
                    state.RequestIssued &&
                    state.OrdinaryRemovalStartedUtcTicks <= 0L)
                {
                    state.OrdinaryRemovalStartedUtcTicks =
                        state.RequestIssuedUtcTicks > 0L
                            ? state.RequestIssuedUtcTicks
                            : state.StartedUtcTicks;
                }
                if (!IsValidState(state))
                {
                    SessionState.EraseString(ActiveStateKey);
                    return null;
                }
                if (state.SchemaVersion == LegacySchemaVersion)
                {
                    state.AutomaticRetryAuthorized = false;
                    state.OrdinaryPackageSpecs = Array.Empty<string>();
                    state.OrdinaryRemovalStartedUtcTicks = state.StartedUtcTicks;
                    if (state.RequestIssued && state.RequestAttemptCount <= 0)
                        state.RequestAttemptCount = 1;
                }
                return state;
            }
            catch
            {
                SessionState.EraseString(ActiveStateKey);
                return null;
            }
        }

        private static bool IsValidState(
            PackageManagerNativeRemoveHandoffState state)
        {
            if (state == null ||
                (state.SchemaVersion != CurrentSchemaVersion &&
                 state.SchemaVersion != LegacySchemaVersion) ||
                string.IsNullOrWhiteSpace(state.OperationId) ||
                state.OperationId.Trim().Length > 128 ||
                state.StartedUtcTicks <= 0L ||
                state.OrdinaryRemovalStartedUtcTicks < 0L ||
                state.RequestAttemptCount < 0 ||
                (state.RequestIssued && state.RequestIssuedUtcTicks <= 0L) ||
                (state.SchemaVersion == CurrentSchemaVersion &&
                 (!state.AutomaticRetryAuthorized ||
                  (state.RequestIssued &&
                   state.OrdinaryRemovalStartedUtcTicks <= 0L))))
            {
                return false;
            }

            if (!TryCopyDistinctPackageNames(
                    state.RemovedSubmodulePackageNames,
                    out string[] submodules,
                    out _) ||
                submodules.Length == 0 ||
                !TryCopyDistinctPackageNames(
                    state.OrdinaryPackageNames,
                    out string[] ordinary,
                    out _) ||
                ordinary.Length == 0)
            {
                return false;
            }

            if (state.SchemaVersion == CurrentSchemaVersion &&
                !TryCopyDependencySpecs(
                    state.OrdinaryPackageSpecs,
                    ordinary.Length,
                    out _,
                    out _))
            {
                return false;
            }

            var allNames = new HashSet<string>(submodules, StringComparer.Ordinal);
            for (int index = 0; index < ordinary.Length; index++)
            {
                if (!allNames.Add(ordinary[index]))
                    return false;
            }
            return true;
        }

        private static void SaveActiveState(
            PackageManagerNativeRemoveHandoffState state)
        {
            SessionState.SetString(ActiveStateKey, JsonUtility.ToJson(state));
        }

        private static void ClearActiveState()
        {
            activeState = null;
            activeRequest = null;
            nextInspectionNotBefore = 0d;
            nextRemovalAttemptNotBefore = 0d;
            SessionState.EraseString(ActiveStateKey);
            UnsubscribeUpdate();
            if (!isShuttingDown)
            {
                try
                {
                    EditorApplication.delayCall +=
                        PackageManagerSubmoduleHarmonyPatch
                            .RefreshOpenPackageManagerWindows;
                }
                catch
                {
                    // A domain reload rebuilds Package Manager presentation.
                }
            }
        }

        private static void SubscribeUpdate()
        {
            if (activeState == null || updateSubscribed || isShuttingDown)
                return;
            updateSubscribed = true;
            EditorApplication.update += Update;
            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;
        }

        private static void UnsubscribeUpdate()
        {
            if (!updateSubscribed)
                return;
            updateSubscribed = false;
            EditorApplication.update -= Update;
            AssemblyReloadEvents.beforeAssemblyReload -= OnBeforeAssemblyReload;
        }

        private static void OnBeforeAssemblyReload()
        {
            isShuttingDown = true;
            UnsubscribeUpdate();
        }

        private static string SanitizeMessage(string message)
        {
            return GitHubUtility.SanitizeUiDiagnostic(
                GitUtility.RedactCredentials(message ?? string.Empty));
        }
    }
}
