using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityEditor.PackageManager;

namespace MartinCalander.GitSubmoduleManager.Editor.Tests
{
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
        public void DetermineNextAction_DoesNotRequestResolveTwiceAfterReload()
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
        public void DetermineNextAction_WaitsWhilePackageRegistryIsUnavailable()
        {
            Assert.That(
                PackageManagerProjectResolutionService.DetermineNextAction(
                    false,
                    false,
                    false,
                    false,
                    false,
                    false),
                Is.EqualTo(PackageManagerResolutionNextAction.Wait));
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
            Assert.That(error, Does.Contain("Automatic retry is disabled"));
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
                    PackageSource.Embedded,
                    expected,
                    expected),
                Is.True);
            Assert.That(
                PackageManagerProjectResolutionService.IsExpectationSatisfied(
                    PackageManagerResolutionExpectation.Embedded,
                    true,
                    PackageSource.Git,
                    expected,
                    expected),
                Is.False);
            Assert.That(
                PackageManagerProjectResolutionService.IsExpectationSatisfied(
                    PackageManagerResolutionExpectation.Embedded,
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
                    PackageSource.Git,
                    string.Empty,
                    string.Empty),
                Is.True);
            Assert.That(
                PackageManagerProjectResolutionService.IsExpectationSatisfied(
                    PackageManagerResolutionExpectation.Git,
                    true,
                    PackageSource.Embedded,
                    string.Empty,
                    string.Empty),
                Is.False);
            Assert.That(
                PackageManagerProjectResolutionService.IsExpectationSatisfied(
                    PackageManagerResolutionExpectation.Absent,
                    false,
                    PackageSource.Unknown,
                    string.Empty,
                    string.Empty),
                Is.True);
            Assert.That(
                PackageManagerProjectResolutionService.IsExpectationSatisfied(
                    PackageManagerResolutionExpectation.Absent,
                    true,
                    PackageSource.Embedded,
                    string.Empty,
                    string.Empty),
                Is.False);
        }
    }
}
