using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityEditor.PackageManager;

namespace MartinCalander.GitSubmoduleManager.Editor.Tests
{
    [Parallelizable(ParallelScope.None)]
    public sealed class PackageManagerProjectResolutionServiceTests
    {
        [Test]
        public void DetermineNextAction_WaitsForGitFinalizationBeforeResolve()
        {
            Assert.That(
                PackageManagerProjectResolutionService.DetermineNextAction(
                    false,
                    true,
                    true,
                    false,
                    false,
                    false),
                Is.EqualTo(PackageManagerResolutionNextAction.Wait));
            Assert.That(
                PackageManagerProjectResolutionService.DetermineNextAction(
                    false,
                    false,
                    true,
                    false,
                    false,
                    false),
                Is.EqualTo(PackageManagerResolutionNextAction.StartResolve));
        }

        [Test]
        public void DetermineNextAction_ObservesIssuedResolveUntilRetryDeadline()
        {
            Assert.That(
                PackageManagerProjectResolutionService.DetermineNextAction(
                    true,
                    false,
                    true,
                    false,
                    false,
                    false),
                Is.EqualTo(PackageManagerResolutionNextAction.Wait));
            Assert.That(
                PackageManagerProjectResolutionService.DetermineNextAction(
                    true,
                    false,
                    true,
                    true,
                    false,
                    false),
                Is.EqualTo(PackageManagerResolutionNextAction.Wait),
                "The package graph must remain reserved until Unity finishes its resolve/import cycle.");
            Assert.That(
                PackageManagerProjectResolutionService.DetermineNextAction(
                    true,
                    false,
                    true,
                    true,
                    true,
                    false),
                Is.EqualTo(PackageManagerResolutionNextAction.Complete));
            Assert.That(
                PackageManagerProjectResolutionService.DetermineNextAction(
                    false,
                    false,
                    true,
                    true,
                    false,
                    false),
                Is.EqualTo(PackageManagerResolutionNextAction.Complete));
        }

        [Test]
        public void DetermineNextAction_RetriesIssuedResolveAfterDeadline()
        {
            Assert.That(
                PackageManagerProjectResolutionService.DetermineNextAction(
                    true,
                    false,
                    true,
                    false,
                    false,
                    true,
                    false),
                Is.EqualTo(PackageManagerResolutionNextAction.StartResolve));
            Assert.That(
                PackageManagerProjectResolutionService.DetermineNextAction(
                    true,
                    true,
                    true,
                    false,
                    false,
                    true,
                    false),
                Is.EqualTo(PackageManagerResolutionNextAction.Wait),
                "A forced resolve retry must not race Git finalization.");
        }

        [Test]
        public void ResolveRetryDeadline_RequiresUnresolvedPersistedAttempt()
        {
            long now = TimeSpan.FromSeconds(30d).Ticks;
            long interval = TimeSpan.FromSeconds(15d).Ticks;

            Assert.That(
                PackageManagerProjectResolutionService.IsResolveRetryDue(
                    true,
                    false,
                    now - interval,
                    now,
                    interval),
                Is.True);
            Assert.That(
                PackageManagerProjectResolutionService.IsResolveRetryDue(
                    true,
                    true,
                    now - interval,
                    now,
                    interval),
                Is.False);
            Assert.That(
                PackageManagerProjectResolutionService.IsResolveRetryDue(
                    false,
                    false,
                    0L,
                    now,
                    interval),
                Is.False);
        }

        [Test]
        public void DetermineNextAction_TimesOutEvenWhenResolveStartKeepsFailing()
        {
            Assert.That(
                PackageManagerProjectResolutionService.DetermineNextAction(
                    true,
                    false,
                    true,
                    false,
                    false,
                    true),
                Is.EqualTo(PackageManagerResolutionNextAction.Timeout));
            Assert.That(
                PackageManagerProjectResolutionService.DetermineNextAction(
                    false,
                    false,
                    true,
                    false,
                    false,
                    true),
                Is.EqualTo(PackageManagerResolutionNextAction.Timeout));
        }

        [Test]
        public void DetermineNextAction_RequestsResolveWhenPackageRegistryIsUnavailable()
        {
            Assert.That(
                PackageManagerProjectResolutionService.DetermineNextAction(
                    false,
                    false,
                    false,
                    false,
                    false,
                    false),
                Is.EqualTo(PackageManagerResolutionNextAction.StartResolve));
            Assert.That(
                PackageManagerProjectResolutionService.DetermineNextAction(
                    false,
                    true,
                    false,
                    false,
                    false,
                    false),
                Is.EqualTo(PackageManagerResolutionNextAction.Wait),
                "Resolution must still wait until the Git reservation is released.");
        }

        [Test]
        public void TryPersistThenRequestResolve_PersistsIntentFirst()
        {
            var state = new PackageManagerProjectResolutionState();
            var calls = new List<string>();

            bool started =
                PackageManagerProjectResolutionService.TryPersistThenRequestResolve(
                    state,
                    () =>
                    {
                        Assert.That(state.ResolveRequested, Is.True);
                        calls.Add("persist");
                    },
                    () => calls.Add("resolve"),
                    out string error);

            Assert.That(started, Is.True, error);
            Assert.That(calls, Is.EqualTo(new[] { "persist", "resolve" }));
            Assert.That(state.ResolveRequestedUtcTicks, Is.GreaterThan(0L));
        }

        [Test]
        public void TryPersistThenRequestResolve_ReportsImmediateResolveFailure()
        {
            var state = new PackageManagerProjectResolutionState();
            int persistCalls = 0;

            bool started =
                PackageManagerProjectResolutionService.TryPersistThenRequestResolve(
                    state,
                    () => persistCalls++,
                    () => throw new InvalidOperationException("resolve failed"),
                    out string error);

            Assert.That(started, Is.False);
            Assert.That(error, Does.Contain("resolve failed"));
            Assert.That(state.ResolveRequested, Is.False);
            Assert.That(state.ResolveRequestedUtcTicks, Is.Zero);
            Assert.That(persistCalls, Is.EqualTo(2),
                "The persisted in-flight marker must be reset so the operation can retry.");
        }

        [Test]
        public void TryPersistThenRequestResolve_FailsClosedWhenRetryStateCannotPersist()
        {
            var state = new PackageManagerProjectResolutionState();
            int persistCalls = 0;

            bool started =
                PackageManagerProjectResolutionService.TryPersistThenRequestResolve(
                    state,
                    () =>
                    {
                        persistCalls++;
                        if (persistCalls == 2)
                            throw new InvalidOperationException("rollback persist failed");
                    },
                    () => throw new InvalidOperationException("resolve failed"),
                    out string error);

            Assert.That(started, Is.False);
            Assert.That(error, Does.Contain("Automatic retry will resume"));
            Assert.That(state.ResolveRequested, Is.True);
            Assert.That(state.ResolveRequestedUtcTicks, Is.GreaterThan(0L));
        }

        [Test]
        public void ResolutionSettlement_RequiresProgressAndAFullQuietInterval()
        {
            long start = TimeSpan.FromSeconds(10d).Ticks;
            long interval = TimeSpan.FromSeconds(1d).Ticks;

            Assert.That(
                PackageManagerProjectResolutionService.IsResolutionSettled(
                    true,
                    true,
                    false,
                    false,
                    false,
                    start,
                    start + interval,
                    interval),
                Is.False,
                "Matching polls alone are not proof that native resolution progressed.");
            Assert.That(
                PackageManagerProjectResolutionService.IsResolutionSettled(
                    true,
                    true,
                    true,
                    false,
                    false,
                    start,
                    start + interval - 1L,
                    interval),
                Is.False);
            Assert.That(
                PackageManagerProjectResolutionService.IsResolutionSettled(
                    true,
                    true,
                    true,
                    false,
                    false,
                    start,
                    start + interval,
                    interval),
                Is.True);
        }

        [Test]
        public void ResolutionSettlement_EventBurstRestartsQuietInterval()
        {
            long interval = TimeSpan.FromSeconds(1d).Ticks;
            long firstEvent = TimeSpan.FromSeconds(10d).Ticks;
            long latestEvent = firstEvent + interval - 1L;

            Assert.That(
                PackageManagerProjectResolutionService.IsResolutionSettled(
                    true,
                    true,
                    true,
                    false,
                    false,
                    latestEvent,
                    latestEvent + interval - 1L,
                    interval),
                Is.False);
            Assert.That(
                PackageManagerProjectResolutionService.IsResolutionSettled(
                    true,
                    true,
                    true,
                    false,
                    false,
                    latestEvent,
                    latestEvent + interval,
                    interval),
                Is.True);
        }

        [Test]
        public void ResolutionSettlement_CompileOrImportRestartsQuietInterval()
        {
            long interval = TimeSpan.FromSeconds(1d).Ticks;
            long initialMatch = TimeSpan.FromSeconds(10d).Ticks;
            long busyAt = initialMatch + TimeSpan.FromMilliseconds(500d).Ticks;
            long idleAgainAt = initialMatch + interval;

            long quietSince =
                PackageManagerProjectResolutionService.UpdateQuietIntervalStart(
                    true,
                    true,
                    true,
                    false,
                    false,
                    0L,
                    initialMatch);
            quietSince =
                PackageManagerProjectResolutionService.UpdateQuietIntervalStart(
                    true,
                    true,
                    true,
                    false,
                    true,
                    quietSince,
                    busyAt);
            Assert.That(quietSince, Is.Zero);

            quietSince =
                PackageManagerProjectResolutionService.UpdateQuietIntervalStart(
                    true,
                    true,
                    true,
                    false,
                    false,
                    quietSince,
                    idleAgainAt);
            Assert.That(
                PackageManagerProjectResolutionService.IsResolutionSettled(
                    true,
                    true,
                    true,
                    false,
                    false,
                    quietSince,
                    idleAgainAt + interval - 1L,
                    interval),
                Is.False);
            Assert.That(
                PackageManagerProjectResolutionService.IsResolutionSettled(
                    true,
                    true,
                    true,
                    false,
                    false,
                    quietSince,
                    idleAgainAt + interval,
                    interval),
                Is.True);
        }

        [Test]
        public void EmbeddedExpectation_RequiresExactSourceAndPath()
        {
            string expected = Path.Combine(
                Path.GetTempPath(),
                "Project",
                "Packages",
                "com.example.package");

            Assert.That(
                PackageManagerProjectResolutionService.IsExpectationSatisfied(
                    PackageManagerResolutionExpectation.Embedded,
                    true,
                    true,
                    PackageSource.Embedded,
                    expected,
                    expected),
                Is.True);
            Assert.That(
                PackageManagerProjectResolutionService.IsExpectationSatisfied(
                    PackageManagerResolutionExpectation.Embedded,
                    true,
                    true,
                    PackageSource.Git,
                    expected,
                    expected),
                Is.False);
            Assert.That(
                PackageManagerProjectResolutionService.IsExpectationSatisfied(
                    PackageManagerResolutionExpectation.Embedded,
                    true,
                    true,
                    PackageSource.Embedded,
                    expected,
                    expected + "-other"),
                Is.False);
        }

        [Test]
        public void GitAndAbsentExpectations_RequireTheirAuthoritativeState()
        {
            Assert.That(
                PackageManagerProjectResolutionService.IsExpectationSatisfied(
                    PackageManagerResolutionExpectation.Git,
                    true,
                    true,
                    PackageSource.Git,
                    string.Empty,
                    string.Empty),
                Is.True);
            Assert.That(
                PackageManagerProjectResolutionService.IsExpectationSatisfied(
                    PackageManagerResolutionExpectation.Git,
                    true,
                    true,
                    PackageSource.Embedded,
                    string.Empty,
                    string.Empty),
                Is.False);
            Assert.That(
                PackageManagerProjectResolutionService.IsExpectationSatisfied(
                    PackageManagerResolutionExpectation.Absent,
                    false,
                    false,
                    PackageSource.Unknown,
                    string.Empty,
                    string.Empty),
                Is.True);
            Assert.That(
                PackageManagerProjectResolutionService.IsExpectationSatisfied(
                    PackageManagerResolutionExpectation.Absent,
                    true,
                    true,
                    PackageSource.Embedded,
                    string.Empty,
                    string.Empty),
                Is.False);
            Assert.That(
                PackageManagerProjectResolutionService.IsExpectationSatisfied(
                    PackageManagerResolutionExpectation.Absent,
                    true,
                    false,
                    PackageSource.Registry,
                    string.Empty,
                    string.Empty),
                Is.True,
                "A package that remains only as a transitive dependency is no " +
                "longer part of the direct Remove selection.");
        }

        [Test]
        public void NotEmbeddedExpectation_AllowsManifestReplacementWithSameName()
        {
            string expected = PackageManagerProjectResolutionService
                .GetExpectedEmbeddedPath("com.example.package");

            Assert.That(
                PackageManagerProjectResolutionService.IsExpectationSatisfied(
                    PackageManagerResolutionExpectation.NotEmbedded,
                    true,
                    true,
                    PackageSource.Embedded,
                    expected,
                    expected),
                Is.False,
                "The original embedded package is still registered.");
            Assert.That(
                PackageManagerProjectResolutionService.IsExpectationSatisfied(
                    PackageManagerResolutionExpectation.NotEmbedded,
                    true,
                    true,
                    PackageSource.Registry,
                    expected,
                    string.Empty),
                Is.True,
                "A direct manifest dependency may replace the removed submodule.");
            Assert.That(
                PackageManagerProjectResolutionService.IsExpectationSatisfied(
                    PackageManagerResolutionExpectation.NotEmbedded,
                    true,
                    true,
                    PackageSource.Embedded,
                    expected,
                    expected + "-other"),
                Is.True);
            Assert.That(
                PackageManagerProjectResolutionService.IsExpectationSatisfied(
                    PackageManagerResolutionExpectation.NotEmbedded,
                    true,
                    true,
                    PackageSource.Embedded,
                    expected,
                    string.Empty),
                Is.False,
                "Missing embedded-path evidence must fail closed.");
            Assert.That(
                PackageManagerProjectResolutionService.IsExpectationSatisfied(
                    PackageManagerResolutionExpectation.NotEmbedded,
                    false,
                    false,
                    PackageSource.Unknown,
                    expected,
                    string.Empty),
                Is.True);
        }

        [Test]
        public void ProjectResolution_PersistsEveryBatchTargetAcrossReload()
        {
            PackageManagerProjectResolutionService.ClearStateForTests();
            string operationId = Guid.NewGuid().ToString("N");
            try
            {
                Assert.That(
                    PackageManagerProjectResolutionService.TryPrepare(
                        operationId,
                        new[]
                        {
                            "com.example.first",
                            "com.example.second"
                        },
                        PackageManagerResolutionExpectation.NotEmbedded,
                        out string error),
                    Is.True,
                    error);

                PackageManagerProjectResolutionState restored =
                    PackageManagerProjectResolutionService
                        .LoadPersistedStateForTests();
                Assert.That(restored, Is.Not.Null);
                Assert.That(
                    restored.PackageNames,
                    Is.EqualTo(new[]
                    {
                        "com.example.first",
                        "com.example.second"
                    }));
                Assert.That(restored.PackageName, Is.EqualTo("com.example.first"));
                Assert.That(
                    PackageManagerProjectResolutionService.ContainsPackage(
                        "com.example.first"),
                    Is.True);
                Assert.That(
                    PackageManagerGitHubNativeActions
                        .IsNativeRemovePackageNameInProgress(
                            "com.example.second"),
                    Is.True,
                    "Persisted project resolution must keep Unity's Remove " +
                    "progress active for every batch target.");
                Assert.That(
                    PackageManagerProjectResolutionService.ContainsPackage(
                        "com.example.other"),
                    Is.False);
            }
            finally
            {
                PackageManagerProjectResolutionService.ClearStateForTests();
            }
        }

        [Test]
        public void NativeRemoveHandoff_WaitsForGitAndSubmoduleResolution()
        {
            Assert.That(
                PackageManagerNativeRemoveHandoffService.DetermineNextAction(
                    false,
                    true,
                    true,
                    false,
                    true,
                    false,
                    false,
                    false),
                Is.EqualTo(PackageManagerNativeRemoveHandoffAction.Wait));
            Assert.That(
                PackageManagerNativeRemoveHandoffService.DetermineNextAction(
                    false,
                    false,
                    false,
                    false,
                    true,
                    false,
                    false,
                    false),
                Is.EqualTo(PackageManagerNativeRemoveHandoffAction.Wait));
            Assert.That(
                PackageManagerNativeRemoveHandoffService.DetermineNextAction(
                    false,
                    false,
                    false,
                    false,
                    true,
                    true,
                    false,
                    false),
                Is.EqualTo(
                    PackageManagerNativeRemoveHandoffAction
                        .StartOrdinaryRemoval));
        }

        [Test]
        public void NativeRemoveHandoff_ObservesIssuedRequestBeforeRetryGrace()
        {
            Assert.That(
                PackageManagerNativeRemoveHandoffService.DetermineNextAction(
                    true,
                    false,
                    false,
                    false,
                    true,
                    true,
                    false,
                    false),
                Is.EqualTo(PackageManagerNativeRemoveHandoffAction.Wait));
            Assert.That(
                PackageManagerNativeRemoveHandoffService.DetermineNextAction(
                    true,
                    false,
                    false,
                    false,
                    true,
                    true,
                    true,
                    false),
                Is.EqualTo(PackageManagerNativeRemoveHandoffAction.Complete));
            Assert.That(
                PackageManagerNativeRemoveHandoffService.DetermineNextAction(
                    true,
                    false,
                    false,
                    false,
                    true,
                    true,
                    false,
                    true),
                Is.EqualTo(PackageManagerNativeRemoveHandoffAction.Timeout));
        }

        [Test]
        public void NativeRemoveHandoff_RequiresManifestAndRegisteredGraphAbsence()
        {
            Assert.That(
                PackageManagerNativeRemoveHandoffService.AreOrdinaryTargetsAbsent(
                    true,
                    Array.Empty<string>()),
                Is.True);
            Assert.That(
                PackageManagerNativeRemoveHandoffService.AreOrdinaryTargetsAbsent(
                    true,
                    new[] { "com.example.still-direct" }),
                Is.False,
                "A stale registered graph must not hide an unchanged manifest target.");
            Assert.That(
                PackageManagerNativeRemoveHandoffService.AreOrdinaryTargetsAbsent(
                    false,
                    Array.Empty<string>()),
                Is.False,
                "A stale registered package must be resolved after its manifest entry is gone.");
        }

        [Test]
        public void NativeRemoveHandoff_OrdinaryPhaseReceivesItsOwnTimeoutWindow()
        {
            long timeout = TimeSpan.FromMinutes(10d).Ticks;
            long now = TimeSpan.FromMinutes(19d).Ticks;

            Assert.That(
                PackageManagerNativeRemoveHandoffService.IsHandoffPhaseTimedOut(
                    TimeSpan.FromMinutes(1d).Ticks,
                    TimeSpan.FromMinutes(11d).Ticks,
                    now,
                    timeout),
                Is.False,
                "Time spent resolving submodules must not consume the ordinary removal window.");
            Assert.That(
                PackageManagerNativeRemoveHandoffService.IsHandoffPhaseTimedOut(
                    TimeSpan.FromMinutes(1d).Ticks,
                    0L,
                    TimeSpan.FromMinutes(11d).Ticks,
                    timeout),
                Is.True,
                "The prerequisite phase remains bounded before ordinary removal begins.");
        }

        [Test]
        public void NativeRemoveHandoff_PersistsWriteAheadMarkerBeforeRequest()
        {
            var state = new PackageManagerNativeRemoveHandoffState();
            var calls = new List<string>();

            bool started = PackageManagerNativeRemoveHandoffService
                .TryPersistThenRequestRemoval(
                    state,
                    () =>
                    {
                        Assert.That(state.RequestIssued, Is.True);
                        calls.Add("persist");
                    },
                    () => calls.Add("remove"),
                    out string error);

            Assert.That(started, Is.True, error);
            Assert.That(calls, Is.EqualTo(new[] { "persist", "remove" }));
            Assert.That(state.RequestIssuedUtcTicks, Is.GreaterThan(0L));
        }

        [Test]
        public void NativeRemoveHandoff_DoesNotIssueWithoutDurableMarker()
        {
            var state = new PackageManagerNativeRemoveHandoffState();
            bool requestCalled = false;

            bool started = PackageManagerNativeRemoveHandoffService
                .TryPersistThenRequestRemoval(
                    state,
                    () => throw new InvalidOperationException("persist failed"),
                    () => requestCalled = true,
                    out string error);

            Assert.That(started, Is.False);
            Assert.That(error, Does.Contain("persist failed"));
            Assert.That(requestCalled, Is.False);
            Assert.That(state.RequestIssued, Is.False);
            Assert.That(state.RequestIssuedUtcTicks, Is.Zero);
        }

        [Test]
        public void NativeRemoveHandoff_RequestFailureRetainsWriteAheadAttempt()
        {
            var state = new PackageManagerNativeRemoveHandoffState();

            bool started = PackageManagerNativeRemoveHandoffService
                .TryPersistThenRequestRemoval(
                    state,
                    () => { },
                    () => throw new InvalidOperationException("remove failed"),
                    out string error);

            Assert.That(started, Is.False);
            Assert.That(error, Does.Contain("remove failed"));
            Assert.That(state.RequestIssued, Is.True);
            Assert.That(state.RequestIssuedUtcTicks, Is.GreaterThan(0L));
            Assert.That(state.RequestAttemptCount, Is.EqualTo(1));
        }

        [Test]
        public void NativeRemoveHandoff_RecoversOnlyExactRetryAuthorizedRequest()
        {
            long now = TimeSpan.FromSeconds(30d).Ticks;
            long grace = TimeSpan.FromSeconds(15d).Ticks;
            var state = new PackageManagerNativeRemoveHandoffState
            {
                RequestIssued = true,
                RequestIssuedUtcTicks = now - grace,
                RequestAttemptCount = 1,
                AutomaticRetryAuthorized = true
            };

            Assert.That(
                PackageManagerNativeRemoveHandoffService
                    .ShouldRecoverIssuedRequest(
                        state,
                        false,
                        now,
                        grace,
                        false),
                Is.True,
                "A lost live request may retry only after its persisted grace period.");
            Assert.That(
                PackageManagerNativeRemoveHandoffService
                    .ShouldRecoverIssuedRequest(
                        state,
                        true,
                        now,
                        grace,
                        false),
                Is.False,
                "A live request must never be duplicated.");
            state.AutomaticRetryAuthorized = false;
            Assert.That(
                PackageManagerNativeRemoveHandoffService
                    .ShouldRecoverIssuedRequest(
                        state,
                        false,
                        now,
                        grace,
                        false),
                Is.False,
                "Legacy state without exact manifest specs remains observe-only.");
            state.AutomaticRetryAuthorized = true;
            Assert.That(
                PackageManagerNativeRemoveHandoffService
                    .ShouldRecoverIssuedRequest(
                        state,
                        false,
                        now,
                        grace,
                        true),
                Is.False,
                "The overall terminal timeout bounds automatic retries.");
        }

        [Test]
        public void NativeRemoveHandoff_SubmitsOnlyStillDirectOrdinaryPackages()
        {
            Assert.That(
                PackageManagerNativeRemoveHandoffService.FindPresentPackageNames(
                    new[]
                    {
                        "com.example.already-transitive",
                        "com.example.still-direct"
                    },
                    new HashSet<string>(StringComparer.Ordinal)
                    {
                        "com.example.still-direct",
                        "com.example.unrelated"
                    }),
                Is.EqualTo(new[] { "com.example.still-direct" }));
        }

        [Test]
        public void NativeRemoveHandoff_DoesNotTreatManifestReplacementAsSubmodule()
        {
            var state = new PackageManagerNativeRemoveHandoffState
            {
                RemovedSubmodulePackageNames =
                    new[] { "com.example.replaced" },
                OrdinaryPackageNames =
                    new[] { "com.example.ordinary" }
            };

            Assert.That(
                PackageManagerNativeRemoveHandoffService
                    .FindPendingPackageNames(
                        state,
                        new HashSet<string>(StringComparer.Ordinal),
                        new HashSet<string>(StringComparer.Ordinal)
                        {
                            "com.example.replaced",
                            "com.example.ordinary"
                        }),
                Is.EqualTo(new[] { "com.example.ordinary" }),
                "The replacement remains direct, but only the ordinary " +
                "selection is still pending removal.");
        }

        [Test]
        public void NativeRemoveHandoff_PersistedAttemptSurvivesReloadRoundTrip()
        {
            PackageManagerNativeRemoveHandoffService.ClearStateForTests();
            string operationId = Guid.NewGuid().ToString("N");
            try
            {
                Assert.That(
                    PackageManagerNativeRemoveHandoffService.TryPrepare(
                        operationId,
                        new[] { "com.example.submodule" },
                        new[] { "com.example.registry" },
                        new[] { "1.0.0" },
                        out string prepareError),
                    Is.True,
                    prepareError);

                PackageManagerNativeRemoveHandoffState restored =
                    PackageManagerNativeRemoveHandoffService
                        .LoadPersistedStateForTests();
                Assert.That(restored, Is.Not.Null);
                Assert.That(restored.OperationId, Is.EqualTo(operationId));
                Assert.That(
                    restored.RemovedSubmodulePackageNames,
                    Is.EqualTo(new[] { "com.example.submodule" }));
                Assert.That(
                    restored.OrdinaryPackageNames,
                    Is.EqualTo(new[] { "com.example.registry" }));
                Assert.That(
                    restored.OrdinaryPackageSpecs,
                    Is.EqualTo(new[] { "1.0.0" }));
                Assert.That(restored.AutomaticRetryAuthorized, Is.True);

                Assert.That(
                    PackageManagerNativeRemoveHandoffService
                        .TryPersistThenRequestRemoval(
                            restored,
                            () => PackageManagerNativeRemoveHandoffService
                                .SavePersistedStateForTests(restored),
                            () => { },
                            out string requestError),
                    Is.True,
                    requestError);

                PackageManagerNativeRemoveHandoffState reloaded =
                    PackageManagerNativeRemoveHandoffService
                        .LoadPersistedStateForTests();
                Assert.That(reloaded, Is.Not.Null);
                Assert.That(reloaded.RequestIssued, Is.True);
                Assert.That(reloaded.RequestIssuedUtcTicks, Is.GreaterThan(0L));
                Assert.That(
                    reloaded.OrdinaryRemovalStartedUtcTicks,
                    Is.GreaterThan(0L));
                Assert.That(
                    PackageManagerNativeRemoveHandoffService.DetermineNextAction(
                        reloaded.RequestIssued,
                        false,
                        false,
                        false,
                        true,
                        true,
                        false,
                        false),
                    Is.EqualTo(PackageManagerNativeRemoveHandoffAction.Wait),
                    "A restored write-ahead marker must observe Unity before its exact-spec retry grace.");
            }
            finally
            {
                PackageManagerNativeRemoveHandoffService.ClearStateForTests();
            }
        }

        [Test]
        public void NativeRemoveHandoff_CancelPreparedMatchesOnlyUnissuedOperation()
        {
            PackageManagerNativeRemoveHandoffService.ClearStateForTests();
            string operationId = Guid.NewGuid().ToString("N");
            try
            {
                Assert.That(
                    PackageManagerNativeRemoveHandoffService.TryPrepare(
                        operationId,
                        new[] { "com.example.submodule" },
                        new[] { "com.example.registry" },
                        new[] { "1.0.0" },
                        out string error),
                    Is.True,
                    error);

                PackageManagerNativeRemoveHandoffService.CancelPrepared(
                    "different-operation");
                Assert.That(
                    PackageManagerNativeRemoveHandoffService.IsBusy,
                    Is.True);

                PackageManagerNativeRemoveHandoffService.CancelPrepared(
                    operationId);
                Assert.That(
                    PackageManagerNativeRemoveHandoffService.IsBusy,
                    Is.False);
            }
            finally
            {
                PackageManagerNativeRemoveHandoffService.ClearStateForTests();
            }
        }

        [Test]
        public void NativeRemoveHandoff_SameOperationCannotChangePersistedTargets()
        {
            PackageManagerNativeRemoveHandoffService.ClearStateForTests();
            string operationId = Guid.NewGuid().ToString("N");
            try
            {
                Assert.That(
                    PackageManagerNativeRemoveHandoffService.TryPrepare(
                        operationId,
                        new[] { "com.example.submodule" },
                        new[] { "com.example.first" },
                        new[] { "1.0.0" },
                        out string firstError),
                    Is.True,
                    firstError);
                Assert.That(
                    PackageManagerNativeRemoveHandoffService.TryPrepare(
                        operationId,
                        new[] { "com.example.submodule" },
                        new[] { "com.example.second" },
                        new[] { "2.0.0" },
                        out string changedError),
                    Is.False);
                Assert.That(changedError, Does.Contain("identity changed"));

                PackageManagerNativeRemoveHandoffState persisted =
                    PackageManagerNativeRemoveHandoffService
                        .LoadPersistedStateForTests();
                Assert.That(
                    persisted.OrdinaryPackageNames,
                    Is.EqualTo(new[] { "com.example.first" }));
            }
            finally
            {
                PackageManagerNativeRemoveHandoffService.ClearStateForTests();
            }
        }
    }
}
