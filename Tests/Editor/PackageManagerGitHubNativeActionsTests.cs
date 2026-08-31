using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace MartinCalander.GitSubmoduleManager.Editor.Tests
{
    [Parallelizable(ParallelScope.None)]
    public sealed class PackageManagerGitHubNativeActionsTests
    {
        private const BindingFlags AnyInstance =
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        [Test]
        public void LiveContract_ResolvesNativePrimaryActionSurface()
        {
            if (!PackageManagerUnityVersionSupport.IsCurrentVersionSupported)
            {
                Assert.That(
                    PackageManagerGitHubNativeActions.HasSupportedLiveContract(),
                    Is.False);
                Assert.That(
                    PackageManagerGitHubNativeActions
                        .HasSupportedSelectionContract(),
                    Is.False);
                return;
            }

            Type rootType = PackageManagerSubmoduleHarmonyPatch.FindLoadedType(
                PackageManagerGitHubNativeActions.PackageManagerWindowRootTypeName);
            Type toolbarType = PackageManagerSubmoduleHarmonyPatch.FindLoadedType(
                PackageManagerGitHubNativeActions.PackageToolbarTypeName);
            Type linksType = PackageManagerSubmoduleHarmonyPatch.FindLoadedType(
                PackageManagerGitHubNativeActions.PackageDetailsLinksTypeName);
            Type headerType = PackageManagerSubmoduleHarmonyPatch.FindLoadedType(
                PackageManagerGitHubNativeActions.PackageDetailsHeaderTypeName);

            Assert.That(rootType, Is.Not.Null);
            Assert.That(typeof(VisualElement).IsAssignableFrom(rootType), Is.True);
            Assert.That(toolbarType, Is.Not.Null);
            Assert.That(typeof(VisualElement).IsAssignableFrom(toolbarType), Is.True);
            Assert.That(linksType, Is.Not.Null);
            Assert.That(typeof(VisualElement).IsAssignableFrom(linksType), Is.True);
            Assert.That(headerType, Is.Not.Null);
            Assert.That(typeof(VisualElement).IsAssignableFrom(headerType), Is.True);

            FieldInfo primaryActions = toolbarType.GetField(
                PackageManagerGitHubNativeActions.BuiltInActionsFieldName,
                AnyInstance);
            Assert.That(primaryActions, Is.Not.Null);
            Assert.That(primaryActions.IsStatic, Is.False);
            Assert.That(
                typeof(VisualElement).IsAssignableFrom(primaryActions.FieldType),
                Is.True);
            PropertyInfo detailsLinks = headerType.GetProperty(
                PackageManagerGitHubNativeActions.DetailsLinksPropertyName,
                AnyInstance);
            Assert.That(detailsLinks, Is.Not.Null);
            Assert.That(detailsLinks.GetIndexParameters(), Is.Empty);
            Assert.That(detailsLinks.PropertyType, Is.EqualTo(linksType));
            Assert.That(
                PackageManagerGitHubNativeActions.HasSupportedLiveContract(),
                Is.True);
            Assert.That(
                PackageManagerGitHubNativeActions.HasSupportedSelectionContract(),
                Is.True,
                "The active-page selection seam must resolve independently of " +
                "the primary-actions mounting contract.");
        }

        [Test]
        public void RefreshPackage_PrefersAuthoritativeSelectionAndFailsClosedOnInvalidSelection()
        {
            var staleToolbarPackage = new object();
            var authoritativePackage = new object();

            Assert.That(
                PackageManagerGitHubNativeActions.SelectPackageForRefresh(
                    staleToolbarPackage,
                    true,
                    true,
                    authoritativePackage),
                Is.SameAs(authoritativePackage));
            Assert.That(
                PackageManagerGitHubNativeActions.SelectPackageForRefresh(
                    staleToolbarPackage,
                    true,
                    false,
                    null),
                Is.Null,
                "An exact but zero/multi/missing selection must not reuse stale " +
                "toolbar state.");
            Assert.That(
                PackageManagerGitHubNativeActions.SelectPackageForRefresh(
                    staleToolbarPackage,
                    false,
                    false,
                    null),
                Is.SameAs(staleToolbarPackage),
                "Harmony's explicit package remains the presentation fallback " +
                "when the optional selection seam is unavailable.");
        }

        [Test]
        public void ReadOnlyManageConversion_RejectsStaleSelectionIdentity()
        {
            PackageManagerReadOnlyGitInfo requestedInfo = CreateReadOnlyInfo(
                "https://github.com/example/package.git",
                "main",
                "1111111111111111111111111111111111111111");
            PackageManagerPackageConversionTarget requestedTarget =
                CreateReadOnlyConversionTarget(requestedInfo);
            PackageManagerReadOnlyGitInfo changedLiveInfo = CreateReadOnlyInfo(
                "https://github.com/example/other.git",
                "main",
                "2222222222222222222222222222222222222222");

            Assert.That(
                PackageManagerGitHubNativeActions
                    .IsCurrentReadOnlyConversionSelection(
                        requestedTarget,
                        requestedInfo,
                        requestedInfo,
                        requestedTarget),
                Is.True);
            Assert.That(
                PackageManagerGitHubNativeActions
                    .IsCurrentReadOnlyConversionSelection(
                        requestedTarget,
                        requestedInfo,
                        changedLiveInfo,
                        requestedTarget),
                Is.False,
                "A recycled native selection must not convert the previous package.");
            Assert.That(
                PackageManagerGitHubNativeActions
                    .IsCurrentReadOnlyConversionSelection(
                        requestedTarget,
                        requestedInfo,
                        requestedInfo,
                        CreateReadOnlyConversionTarget(changedLiveInfo)),
                Is.False,
                "A stale Manage callback must match the currently mounted target.");
        }

        [TestCase(false, true, true)]
        [TestCase(false, false, false)]
        [TestCase(true, true, false)]
        [TestCase(true, false, false)]
        public void ReadOnlyManageConversion_ModalCancelAndBatchModeFailClosed(
            bool isBatchMode,
            bool promptAccepted,
            bool expected)
        {
            Assert.That(
                PackageManagerGitHubNativeActions
                    .ShouldProceedWithReadOnlyConversionPrompt(
                        isBatchMode,
                        promptAccepted),
                Is.EqualTo(expected));
        }

        [TestCase(false, false, false, false)]
        [TestCase(false, false, true, true)]
        [TestCase(false, true, true, false)]
        [TestCase(true, true, false, true)]
        public void NativeRemovePrompt_CancelAndBatchModeFailClosed(
            bool canProceedWithoutPrompt,
            bool isBatchMode,
            bool promptAccepted,
            bool expected)
        {
            Assert.That(
                PackageManagerGitHubNativeActions
                    .ShouldProceedWithNativeBatchRemovalPrompt(
                        canProceedWithoutPrompt,
                        isBatchMode,
                        promptAccepted),
                Is.EqualTo(expected));
        }

        [Test]
        public void NativeRemoveHandoff_StartsOnlyAfterSuccessfulGitBatch()
        {
            Assert.That(
                PackageManagerGitHubNativeActions
                    .ShouldPrepareNativeRemoveHandoff(
                        true,
                        GitOperationCompletionOutcome.Succeeded,
                        1),
                Is.True);
            Assert.That(
                PackageManagerGitHubNativeActions
                    .ShouldPrepareNativeRemoveHandoff(
                        false,
                        GitOperationCompletionOutcome.FailedButRolledBack,
                        1),
                Is.False,
                "A failed batch must leave ordinary packages untouched.");
            Assert.That(
                PackageManagerGitHubNativeActions
                    .ShouldPrepareNativeRemoveHandoff(
                        true,
                        GitOperationCompletionOutcome.Succeeded,
                        0),
                Is.False);
        }

        [Test]
        public void NativeRemoveBatch_PartialSafeFailurePreparesPackageResolution()
        {
            Assert.That(
                GitSubmoduleRemoveService.ShouldPreparePartialBatchResolution(
                    GitOperationCompletionOutcome.FailedButRolledBack,
                    true,
                    1,
                    2),
                Is.True);
            Assert.That(
                GitSubmoduleRemoveService.ShouldPreparePartialBatchResolution(
                    GitOperationCompletionOutcome.FailedUnsafe,
                    true,
                    1,
                    2),
                Is.False);
            Assert.That(
                GitSubmoduleRemoveService.ShouldPreparePartialBatchResolution(
                    GitOperationCompletionOutcome.FailedButRolledBack,
                    false,
                    1,
                    2),
                Is.False);
            Assert.That(
                GitSubmoduleRemoveService.ShouldPreparePartialBatchResolution(
                    GitOperationCompletionOutcome.Succeeded,
                    true,
                    2,
                    2),
                Is.False);
            Assert.That(
                GitSubmoduleRemoveService.ShouldCancelPartialBatchResolution(
                    true,
                    GitOperationCompletionOutcome.FailedUnsafe),
                Is.True,
                "An unlock failure must cancel the pre-unlock resolution state.");
            Assert.That(
                GitSubmoduleRemoveService.ShouldCancelPartialBatchResolution(
                    true,
                    GitOperationCompletionOutcome.FailedButRolledBack),
                Is.False);
        }

        [Test]
        public void ExactSingleSelection_ResolvesOnlyMatchingDatabasePackage()
        {
            var package = new SelectionPackageFixture(
                "com.example.package",
                "com.example.package");

            Assert.That(
                PackageManagerGitHubNativeActions.TryResolveExactSingleSelection(
                    1,
                    new[] { "com.example.package" },
                    id => string.Equals(
                            id,
                            package.UniqueId,
                            StringComparison.Ordinal)
                        ? package
                        : null,
                    candidate => ((SelectionPackageFixture)candidate).UniqueId,
                    candidate => ((SelectionPackageFixture)candidate).Name,
                    out object resolved),
                Is.True);
            Assert.That(resolved, Is.SameAs(package));
        }

        [Test]
        public void ExactSingleSelection_FailsClosedOnCountLookupAndIdentityDrift()
        {
            var package = new SelectionPackageFixture(
                "com.example.package",
                "com.example.package");
            Func<string, object> lookup = _ => package;
            Func<object, string> uniqueId = candidate =>
                ((SelectionPackageFixture)candidate).UniqueId;
            Func<object, string> name = candidate =>
                ((SelectionPackageFixture)candidate).Name;

            Assert.That(
                PackageManagerGitHubNativeActions.TryResolveExactSingleSelection(
                    0,
                    Array.Empty<string>(),
                    lookup,
                    uniqueId,
                    name,
                    out _),
                Is.False);
            Assert.That(
                PackageManagerGitHubNativeActions.TryResolveExactSingleSelection(
                    1,
                    new[] { "first", "second" },
                    lookup,
                    uniqueId,
                    name,
                    out _),
                Is.False,
                "The reported count and actual enumeration must agree exactly.");
            Assert.That(
                PackageManagerGitHubNativeActions.TryResolveExactSingleSelection(
                    1,
                    new[] { "com.example.package" },
                    _ => null,
                    uniqueId,
                    name,
                    out _),
                Is.False,
                "A package database miss must not fall back to toolbar state.");
            Assert.That(
                PackageManagerGitHubNativeActions.TryResolveExactSingleSelection(
                    1,
                    new[] { "com.example.other" },
                    lookup,
                    uniqueId,
                    name,
                    out _),
                Is.False,
                "The resolved package must retain the exact selected identity.");
            Assert.That(
                PackageManagerGitHubNativeActions.TryResolveExactSingleSelection(
                    1,
                    new[] { "com.example.package" },
                    _ => throw new InvalidOperationException("database drift"),
                    uniqueId,
                    name,
                    out _),
                Is.False,
                "Reflection or database failures must remain contained.");
        }

        [Test]
        public void NativeRemoveClassification_AllSubmodulesBuildsExactManagedPlan()
        {
            PackageManagerSubmoduleInfo firstInfo = CreateNativeRemoveSubmodule(
                "com.example.first");
            PackageManagerSubmoduleInfo secondInfo = CreateNativeRemoveSubmodule(
                "com.example.second");
            var first = new NativeRemovePackageFixture(
                firstInfo.PackageName,
                firstInfo);
            var second = new NativeRemovePackageFixture(
                secondInfo.PackageName,
                secondInfo);

            PackageManagerNativeRemoveSelectionRouting routing =
                ClassifyNativeRemoveSelection(
                    new[] { first, second },
                    snapshotReady: true,
                    out PackageManagerGitHubNativeActions.NativeRemovePlan plan,
                    out string error);

            Assert.That(
                routing,
                Is.EqualTo(PackageManagerNativeRemoveSelectionRouting.Managed));
            Assert.That(plan.Submodules, Is.EqualTo(new[] { firstInfo, secondInfo }));
            Assert.That(plan.OrdinaryPackageNames, Is.Empty);
            Assert.That(
                plan.AllPackageNames,
                Is.EqualTo(new[] { firstInfo.PackageName, secondInfo.PackageName }));
            Assert.That(error, Is.Empty);
        }

        [Test]
        public void NativeRemoveClassification_MixedSelectionBuildsOneManagedPlan()
        {
            PackageManagerSubmoduleInfo submoduleInfo =
                CreateNativeRemoveSubmodule("com.example.submodule");
            var submodule = new NativeRemovePackageFixture(
                submoduleInfo.PackageName,
                submoduleInfo);
            var ordinary = new NativeRemovePackageFixture("com.example.ordinary");

            PackageManagerNativeRemoveSelectionRouting routing =
                ClassifyNativeRemoveSelection(
                    new[] { submodule, ordinary },
                    snapshotReady: true,
                    out PackageManagerGitHubNativeActions.NativeRemovePlan plan,
                    out string error);

            Assert.That(
                routing,
                Is.EqualTo(PackageManagerNativeRemoveSelectionRouting.Managed));
            Assert.That(plan.Submodules, Is.EqualTo(new[] { submoduleInfo }));
            Assert.That(
                plan.OrdinaryPackageNames,
                Is.EqualTo(new[] { ordinary.PackageName }));
            Assert.That(
                plan.AllPackageNames,
                Is.EqualTo(new[]
                {
                    submoduleInfo.PackageName,
                    ordinary.PackageName
                }));
            Assert.That(error, Is.Empty);
        }

        [Test]
        public void NativeRemoveClassification_OrdinaryOnlyRemainsUnityOwned()
        {
            var first = new NativeRemovePackageFixture("com.example.first");
            var second = new NativeRemovePackageFixture("com.example.second");

            PackageManagerNativeRemoveSelectionRouting routing =
                ClassifyNativeRemoveSelection(
                    new[] { first, second },
                    snapshotReady: true,
                    out PackageManagerGitHubNativeActions.NativeRemovePlan plan,
                    out string error);

            Assert.That(
                routing,
                Is.EqualTo(PackageManagerNativeRemoveSelectionRouting.UnityOwned));
            Assert.That(plan.Submodules, Is.Empty);
            Assert.That(
                plan.OrdinaryPackageNames,
                Is.EqualTo(new[] { first.PackageName, second.PackageName }));
            Assert.That(
                plan.AllPackageNames,
                Is.EqualTo(new[] { first.PackageName, second.PackageName }));
            Assert.That(error, Is.Empty);
        }

        [Test]
        public void NativeRemoveClassification_UnreadyDirectCandidateFailsClosed()
        {
            var candidate = new NativeRemovePackageFixture(
                "com.example.candidate",
                isDirectInstalledPackagePath: true);

            PackageManagerNativeRemoveSelectionRouting routing =
                ClassifyNativeRemoveSelection(
                    new[] { candidate },
                    snapshotReady: false,
                    out PackageManagerGitHubNativeActions.NativeRemovePlan plan,
                    out string error);

            Assert.That(
                routing,
                Is.EqualTo(PackageManagerNativeRemoveSelectionRouting.Blocked));
            Assert.That(plan.Submodules, Is.Empty);
            Assert.That(plan.OrdinaryPackageNames, Is.Empty);
            Assert.That(
                plan.AllPackageNames,
                Is.EqualTo(new[] { candidate.PackageName }));
            Assert.That(plan.AllPackageVersions, Is.EqualTo(new[] { candidate.Version }));
            Assert.That(plan.AwaitingSnapshotClassification, Is.True);
            Assert.That(error, Does.Contain("still loading"));
        }

        [Test]
        public void NativeRemoveClassification_RetainedSubmoduleWaitsForCurrentSuccess()
        {
            PackageManagerSubmoduleInfo retainedInfo =
                CreateNativeRemoveSubmodule("com.example.retained");
            var retained = new NativeRemovePackageFixture(
                retainedInfo.PackageName,
                retainedInfo,
                isDirectInstalledPackagePath: true);

            PackageManagerNativeRemoveSelectionRouting routing =
                ClassifyNativeRemoveSelection(
                    new[] { retained },
                    snapshotReady: false,
                    out PackageManagerGitHubNativeActions.NativeRemovePlan plan,
                    out string error);

            Assert.That(
                routing,
                Is.EqualTo(PackageManagerNativeRemoveSelectionRouting.Blocked));
            Assert.That(plan.Submodules, Is.Empty);
            Assert.That(plan.OrdinaryPackageNames, Is.Empty);
            Assert.That(plan.AwaitingSnapshotClassification, Is.True);
            Assert.That(error, Does.Contain("still loading"));
        }

        [TestCase("com.example.package", true, true, true)]
        [TestCase("com.example.package", false, true, false)]
        [TestCase("com.example.package", false, false, true)]
        [TestCase("invalid package", true, false, false)]
        public void NativeEmbeddedRemovalGuard_RequiresCurrentSuccessfulSnapshot(
            string packageName,
            bool foundSubmodule,
            bool snapshotReady,
            bool expected)
        {
            Assert.That(
                PackageManagerGitHubNativeActions
                    .ShouldBlockNativeEmbeddedRemovalState(
                        packageName,
                        foundSubmodule,
                        snapshotReady),
                Is.EqualTo(expected));
        }

        [Test]
        public void QueuedNativeRemovePlan_ReclassifiesEverySelectedVersion()
        {
            object submoduleVersion = new object();
            object ordinaryVersion = new object();
            PackageManagerSubmoduleInfo submoduleInfo =
                CreateNativeRemoveSubmodule("com.example.submodule");
            var plan = new PackageManagerGitHubNativeActions.NativeRemovePlan
            {
                AwaitingSnapshotClassification = true
            };
            plan.AllPackageNames.Add("com.example.submodule");
            plan.AllPackageNames.Add("com.example.readonly");
            plan.AllPackageVersions.Add(submoduleVersion);
            plan.AllPackageVersions.Add(ordinaryVersion);

            bool refreshed = PackageManagerGitHubNativeActions
                .TryRefreshQueuedNativeRemovePlan(
                    plan,
                    version => ReferenceEquals(version, submoduleVersion)
                        ? new PackageManagerGitHubNativeActions
                            .NativeRemoveVersionIdentity(
                                "com.example.submodule",
                                submoduleInfo.FullPackagePath,
                                true)
                        : new PackageManagerGitHubNativeActions
                            .NativeRemoveVersionIdentity(
                                "com.example.readonly",
                                "/Library/PackageCache/com.example.readonly",
                                true),
                    (packageName, _) => string.Equals(
                            packageName,
                            submoduleInfo.PackageName,
                            StringComparison.Ordinal)
                        ? submoduleInfo
                        : null,
                    _ => string.Empty,
                    out string error);

            Assert.That(refreshed, Is.True, error);
            Assert.That(plan.Submodules, Is.EqualTo(new[] { submoduleInfo }));
            Assert.That(
                plan.OrdinaryPackageNames,
                Is.EqualTo(new[] { "com.example.readonly" }));
            Assert.That(plan.AwaitingSnapshotClassification, Is.False);
        }

        [Test]
        public void QueuedNativeRemovePlan_RejectsSelectionIdentityDrift()
        {
            object selectedVersion = new object();
            var plan = new PackageManagerGitHubNativeActions.NativeRemovePlan
            {
                AwaitingSnapshotClassification = true
            };
            plan.AllPackageNames.Add("com.example.original");
            plan.AllPackageVersions.Add(selectedVersion);

            bool refreshed = PackageManagerGitHubNativeActions
                .TryRefreshQueuedNativeRemovePlan(
                    plan,
                    _ => new PackageManagerGitHubNativeActions
                        .NativeRemoveVersionIdentity(
                            "com.example.changed",
                            "/Packages/com.example.changed",
                            true),
                    (_, __) => null,
                    _ => string.Empty,
                    out string error);

            Assert.That(refreshed, Is.False);
            Assert.That(error, Does.Contain("selected package list changed"));
            Assert.That(plan.Submodules, Is.Empty);
            Assert.That(plan.OrdinaryPackageNames, Is.Empty);
            Assert.That(plan.AwaitingSnapshotClassification, Is.True);
        }

        [Test]
        public void QueuedNativeRemovePlan_RequiresAConfirmedSubmodule()
        {
            object selectedVersion = new object();
            var plan = new PackageManagerGitHubNativeActions.NativeRemovePlan
            {
                AwaitingSnapshotClassification = true
            };
            plan.AllPackageNames.Add("com.example.candidate");
            plan.AllPackageVersions.Add(selectedVersion);

            bool refreshed = PackageManagerGitHubNativeActions
                .TryRefreshQueuedNativeRemovePlan(
                    plan,
                    _ => new PackageManagerGitHubNativeActions
                        .NativeRemoveVersionIdentity(
                            "com.example.candidate",
                            "/Packages/com.example.candidate",
                            true),
                    (_, __) => null,
                    _ => string.Empty,
                    out string error);

            Assert.That(refreshed, Is.False);
            Assert.That(error, Does.Contain("no longer include"));
            Assert.That(plan.AwaitingSnapshotClassification, Is.True);
        }

        [Test]
        public void QueuedNativeRemovePlan_HasBoundedScanWait()
        {
            long timeout = TimeSpan.FromMinutes(2d).Ticks;
            long start = TimeSpan.FromMinutes(1d).Ticks;

            Assert.That(
                PackageManagerGitHubNativeActions.IsQueuedNativeRemoveTimedOut(
                    start,
                    start + timeout - 1L,
                    timeout),
                Is.False);
            Assert.That(
                PackageManagerGitHubNativeActions.IsQueuedNativeRemoveTimedOut(
                    start,
                    start + timeout,
                    timeout),
                Is.True);
        }

        [Test]
        public void NativeRemoveClassification_InvalidAndDuplicateIdentityFailClosed()
        {
            PackageManagerSubmoduleInfo submoduleInfo =
                CreateNativeRemoveSubmodule("com.example.submodule");
            var submodule = new NativeRemovePackageFixture(
                submoduleInfo.PackageName,
                submoduleInfo);

            PackageManagerNativeRemoveSelectionRouting invalidRouting =
                ClassifyNativeRemoveSelection(
                    new[]
                    {
                        submodule,
                        new NativeRemovePackageFixture("invalid package name")
                    },
                    snapshotReady: true,
                    out PackageManagerGitHubNativeActions.NativeRemovePlan invalidPlan,
                    out string invalidError);
            PackageManagerNativeRemoveSelectionRouting duplicateRouting =
                ClassifyNativeRemoveSelection(
                    new[]
                    {
                        submodule,
                        new NativeRemovePackageFixture(submodule.PackageName)
                    },
                    snapshotReady: true,
                    out PackageManagerGitHubNativeActions.NativeRemovePlan duplicatePlan,
                    out string duplicateError);
            PackageManagerNativeRemoveSelectionRouting mismatchRouting =
                ClassifyNativeRemoveSelection(
                    new[]
                    {
                        new NativeRemovePackageFixture(
                            "com.example.selected",
                            CreateNativeRemoveSubmodule("com.example.other"))
                    },
                    snapshotReady: true,
                    out PackageManagerGitHubNativeActions.NativeRemovePlan mismatchPlan,
                    out string mismatchError);

            Assert.That(
                invalidRouting,
                Is.EqualTo(PackageManagerNativeRemoveSelectionRouting.Blocked));
            Assert.That(
                invalidPlan.Submodules,
                Is.EqualTo(new[] { submoduleInfo }));
            Assert.That(invalidPlan.OrdinaryPackageNames, Is.Empty);
            Assert.That(
                invalidPlan.AllPackageNames,
                Is.EqualTo(new[] { submodule.PackageName }));
            Assert.That(invalidError, Does.Contain("ambiguous identity"));
            Assert.That(
                duplicateRouting,
                Is.EqualTo(PackageManagerNativeRemoveSelectionRouting.Blocked));
            Assert.That(
                duplicatePlan.Submodules,
                Is.EqualTo(new[] { submoduleInfo }));
            Assert.That(duplicatePlan.OrdinaryPackageNames, Is.Empty);
            Assert.That(
                duplicatePlan.AllPackageNames,
                Is.EqualTo(new[] { submodule.PackageName }));
            Assert.That(duplicateError, Does.Contain("ambiguous identity"));
            Assert.That(
                mismatchRouting,
                Is.EqualTo(PackageManagerNativeRemoveSelectionRouting.Blocked));
            Assert.That(mismatchPlan.Submodules, Is.Empty);
            Assert.That(mismatchError, Does.Contain("ambiguous identity"));
        }

        [Test]
        public void NativeRemoveClassification_MaterializesSingleUseSelectionOnce()
        {
            PackageManagerSubmoduleInfo submoduleInfo =
                CreateNativeRemoveSubmodule("com.example.submodule");
            var selection = new SingleUseNativeRemoveSelection(new[]
            {
                new NativeRemovePackageFixture(
                    submoduleInfo.PackageName,
                    submoduleInfo),
                new NativeRemovePackageFixture("com.example.ordinary")
            });

            PackageManagerNativeRemoveSelectionRouting routing =
                ClassifyNativeRemoveSelection(
                    selection,
                    snapshotReady: true,
                    out PackageManagerGitHubNativeActions.NativeRemovePlan plan,
                    out string error);

            Assert.That(
                routing,
                Is.EqualTo(PackageManagerNativeRemoveSelectionRouting.Managed));
            Assert.That(selection.EnumerationCount, Is.EqualTo(1));
            Assert.That(plan.AllPackageNames, Has.Count.EqualTo(2));
            Assert.That(error, Is.Empty);
        }

        [Test]
        public void PrimaryActionsField_AcceptsUnnamedContainer()
        {
            var fixture = new PrimaryActionsFieldFixture();
            FieldInfo field = typeof(PrimaryActionsFieldFixture).GetField(
                PackageManagerGitHubNativeActions.BuiltInActionsFieldName,
                AnyInstance);

            VisualElement unnamedPrimaryActions =
                PackageManagerGitHubNativeActions
                    .ReadVerifiedPrimaryActionsContainer(fixture, field);

            Assert.That(unnamedPrimaryActions, Is.SameAs(fixture.PrimaryActions));
            Assert.That(unnamedPrimaryActions.name, Is.Null.Or.Empty);

            var correctLinks = new VisualElement
            {
                name = PackageManagerGitHubDetails.NativeDetailsLinksContainerName
            };

            using PackageManagerGitHubDetails details = CreateDetails(
                unnamedPrimaryActions,
                correctLinks);

            Assert.That(details.Controls.parent, Is.SameAs(unnamedPrimaryActions));
        }

        [Test]
        public void Create_RequiresExactDetailsLinksContainerAndFailsClosed()
        {
            var primaryActions = new VisualElement();
            var wrongLinks = new VisualElement { name = "details" };
            var correctLinks = new VisualElement
            {
                name = PackageManagerGitHubDetails.NativeDetailsLinksContainerName
            };

            Assert.That(
                PackageManagerGitHubDetails.TryCreate(
                    null,
                    correctLinks,
                    (_, _) => { },
                    _ => { },
                    false,
                    out _),
                Is.False);
            Assert.That(
                PackageManagerGitHubDetails.TryCreate(
                    primaryActions,
                    wrongLinks,
                    (_, _) => { },
                    _ => { },
                    false,
                    out _),
                Is.False);
        }

        [Test]
        public void LegacyTryCreate_RemainsSubmoduleOnlyAndInvokesOldCallback()
        {
            var primaryActions = new VisualElement();
            var detailsLinks = new VisualElement
            {
                name = PackageManagerGitHubDetails.NativeDetailsLinksContainerName
            };
            PackageManagerGitHubRepository installedRepository = null;
            string installedBranch = string.Empty;

            Assert.That(
                PackageManagerGitHubDetails.TryCreate(
                    primaryActions,
                    detailsLinks,
                    (repository, branch) =>
                    {
                        installedRepository = repository;
                        installedBranch = branch;
                    },
                    _ => { },
                    false,
                    out PackageManagerGitHubDetails details),
                Is.True);
            using (details)
            {
                PackageManagerGitHubRepository repository = CreateRepository(
                    "repository",
                    "main");
                details.Refresh(repository);
                details.SetInstallState(true, true, "Ready");

                Assert.That(
                    details.SelectedInstallMode,
                    Is.EqualTo(PackageManagerGitInstallMode.GitSubmodule));
                Assert.That(
                    details.InstallMenu.style.display.value,
                    Is.EqualTo(DisplayStyle.None));
                Assert.That(
                    details.InstallButton.style.display.value,
                    Is.EqualTo(DisplayStyle.Flex));
                Assert.That(
                    details.InstallButton.text,
                    Is.EqualTo(L10n.Tr(
                        PackageManagerGitHubDetails.InstallText)));

                details.TriggerInstall();
                details.TriggerInstall();

                Assert.That(installedRepository, Is.SameAs(repository));
                Assert.That(installedBranch, Is.EqualTo("main"));
            }
        }

        [Test]
        public void PrimaryActionsResolver_RejectsWrongToolbarType()
        {
            Assert.That(
                PackageManagerGitHubNativeActions.ResolvePrimaryActionsContainer(
                    new VisualElement()),
                Is.Null);

            var wrongFieldFixture = new WrongPrimaryActionsFieldFixture();
            FieldInfo wrongTypeField = typeof(WrongPrimaryActionsFieldFixture).GetField(
                PackageManagerGitHubNativeActions.BuiltInActionsFieldName,
                AnyInstance);
            Assert.That(
                PackageManagerGitHubNativeActions
                    .ReadVerifiedPrimaryActionsContainer(
                        wrongFieldFixture,
                        wrongTypeField),
                Is.Null);
            Assert.That(
                PackageManagerGitHubNativeActions
                    .ReadVerifiedPrimaryActionsContainer(
                        new PrimaryActionsFieldFixture(),
                        typeof(UnrelatedPrimaryActionsFieldFixture).GetField(
                            PackageManagerGitHubNativeActions.BuiltInActionsFieldName,
                            AnyInstance)),
                Is.Null);
        }

        [Test]
        public void PrimaryAction_IsSingleInstallDropdownAndNeverUsesExtensionsOverflow()
        {
            using PackageManagerGitHubDetails details = CreateDetails(
                out VisualElement primaryActions,
                out VisualElement extensionItems,
                out _,
                (_, _) => { },
                _ => { });
            details.Refresh(CreateRepository("alpha", "main"));
            details.SetInstallState(true, true, "Ready");

            Assert.That(PackageManagerGitHubDetails.InstallText, Is.EqualTo("Install"));
            Assert.That(
                details.InstallMenu.text,
                Is.EqualTo(PackageManagerGitHubDetails.InstallText));
            Assert.That(details.InstallMenu.parent, Is.SameAs(details.Controls));
            Assert.That(details.Controls.parent, Is.SameAs(primaryActions));
            var controlsInOrder = new List<VisualElement>(
                details.Controls.Children());
            Assert.That(
                controlsInOrder.IndexOf(details.BranchField),
                Is.LessThan(controlsInOrder.IndexOf(details.InstallMenu)));
            Assert.That(
                controlsInOrder.IndexOf(details.InstallMenu),
                Is.LessThan(controlsInOrder.IndexOf(details.InstallButton)));
            Assert.That(
                details.Controls.Query<DropdownField>().ToList().Count,
                Is.EqualTo(1));
            IReadOnlyList<DropdownMenuItem> menuItems =
                details.InstallMenu.menu.MenuItems();
            Assert.That(
                menuItems.Count,
                Is.EqualTo(2));
            var firstMenuAction = menuItems[0] as DropdownMenuAction;
            var secondMenuAction = menuItems[1] as DropdownMenuAction;
            Assert.That(firstMenuAction, Is.Not.Null);
            Assert.That(secondMenuAction, Is.Not.Null);
            firstMenuAction.UpdateActionStatus(null);
            secondMenuAction.UpdateActionStatus(null);
            Assert.That(
                new[] { firstMenuAction.name, secondMenuAction.name },
                Is.EqualTo(new[]
                {
                    L10n.Tr(PackageManagerGitHubDetails
                        .InstallAsGitSubmoduleText),
                    L10n.Tr(PackageManagerGitHubDetails
                        .InstallAsReadOnlyPackageText)
                }));
            Assert.That(
                firstMenuAction.status,
                Is.EqualTo(DropdownMenuAction.Status.Normal));
            Assert.That(
                secondMenuAction.status,
                Is.EqualTo(DropdownMenuAction.Status.Normal));
            Assert.That(
                details.SelectedInstallMode,
                Is.EqualTo(PackageManagerGitInstallMode.GitSubmodule));
            Assert.That(
                details.InstallMenu.style.display.value,
                Is.EqualTo(DisplayStyle.Flex));
            Assert.That(
                details.InstallButton.style.display.value,
                Is.EqualTo(DisplayStyle.None));
            Assert.That(
                primaryActions.Q<ToolbarMenu>(
                    PackageManagerGitHubDetails.InstallActionElementName),
                Is.SameAs(details.InstallMenu));
            Assert.That(
                extensionItems.Q<ToolbarMenu>(
                    PackageManagerGitHubDetails.InstallActionElementName),
                Is.Null);
            Assert.That(
                details.CancelInstallButton.style.display.value,
                Is.EqualTo(DisplayStyle.None));
            Assert.That(
                details.InstallFeedback.style.display.value,
                Is.EqualTo(DisplayStyle.None));
            Assert.That(
                details.InstallFeedback.parent?.name,
                Is.EqualTo(PackageManagerGitHubDetails.NativeHelpBoxContainerName));
        }

        [Test]
        public void InstallDropdown_DisablesOnlyTheUnavailableInstallMode()
        {
            int installCount = 0;
            using PackageManagerGitHubDetails details = CreateDetails(
                out _,
                out _,
                out _,
                (_, _, _) => installCount++,
                _ => { });
            details.Refresh(CreateRepository("repository", "main"));
            details.SetInstallState(
                true,
                false,
                "Git submodules are unavailable.",
                true,
                "Read-only install is ready.");

            Assert.That(details.InstallMenu.enabledSelf, Is.True);
            DropdownMenuAction gitSubmoduleAction = FindInstallMenuAction(
                details,
                PackageManagerGitInstallMode.GitSubmodule);
            DropdownMenuAction readOnlyPackageAction = FindInstallMenuAction(
                details,
                PackageManagerGitInstallMode.ReadOnlyPackage);
            gitSubmoduleAction.UpdateActionStatus(null);
            readOnlyPackageAction.UpdateActionStatus(null);

            Assert.That(
                gitSubmoduleAction.status,
                Is.EqualTo(DropdownMenuAction.Status.Disabled));
            Assert.That(
                readOnlyPackageAction.status,
                Is.EqualTo(DropdownMenuAction.Status.Normal));

            gitSubmoduleAction.Execute();

            Assert.That(installCount, Is.Zero);
            Assert.That(details.IsInstallConfirmationPending, Is.False);
            Assert.That(
                details.InstallFeedback.style.display.value,
                Is.EqualTo(DisplayStyle.None));

            readOnlyPackageAction.Execute();

            Assert.That(details.IsInstallConfirmationPending, Is.True);
            Assert.That(
                details.SelectedInstallMode,
                Is.EqualTo(PackageManagerGitInstallMode.ReadOnlyPackage));
            Assert.That(
                details.InstallFeedback.text,
                Does.Contain("read-only Package Manager Git dependency"));
        }

        [Test]
        public void InstallAuthority_RequiresEquivalentCurrentNonRetainedRepository()
        {
            PackageManagerGitHubRepository projected =
                CreateRepository("repository", "main");
            PackageManagerGitHubRepository equivalent =
                CreateRepository("repository", "main");
            PackageManagerGitHubRepository changed =
                CreateRepository("repository", "release");

            Assert.That(
                PackageManagerGitHubNativeActions
                    .CanInstallCurrentDiscoveryRepository(
                        CreateDiscoverySnapshot(equivalent, retained: false),
                        projected),
                Is.True,
                "Equivalent immutable copies from a live projection remain authoritative.");
            Assert.That(
                PackageManagerGitHubNativeActions
                    .CanInstallCurrentDiscoveryRepository(
                        CreateDiscoverySnapshot(equivalent, retained: true),
                        projected),
                Is.False,
                "Retained and reload-cached rows are presentation-only.");
            Assert.That(
                PackageManagerGitHubNativeActions
                    .CanInstallCurrentDiscoveryRepository(
                        CreateDiscoverySnapshot(changed, retained: false),
                        projected),
                Is.False,
                "A content-drifted projection must not authorize an install.");
            Assert.That(
                PackageManagerGitHubNativeActions
                    .CanInstallCurrentDiscoveryRepository(
                        PackageManagerGitHubDiscoverySnapshot.Empty,
                        projected),
                Is.False);
            Assert.That(
                PackageManagerGitHubNativeActions
                    .CanInstallCurrentDiscoveryRepository(
                        CreateDiscoverySnapshot(equivalent, retained: false),
                        null),
                Is.False);
        }

        [Test]
        public void PendingInstall_BecomesNonExecutableWhenLiveAuthorityIsLost()
        {
            int installCount = 0;
            using PackageManagerGitHubDetails details = CreateDetails(
                out _,
                out _,
                out _,
                (_, _, _) => installCount++,
                _ => { });
            details.Refresh(CreateRepository("repository", "main"));
            details.SetInstallState(true, true, "Ready");
            ExecuteInstallMenuAction(
                details,
                PackageManagerGitInstallMode.GitSubmodule);
            Assert.That(details.IsInstallConfirmationPending, Is.True);

            const string liveVerificationRequired =
                "Wait for live GitHub discovery to verify this repository.";
            details.SetInstallState(
                true,
                false,
                liveVerificationRequired,
                false,
                liveVerificationRequired);
            details.TriggerInstall();

            Assert.That(installCount, Is.Zero);
            Assert.That(details.InstallButton.enabledSelf, Is.False);
        }

        [TestCase(
            "git@github.com:example/repository.git",
            "https://github.com/example/repository")]
        [TestCase(
            "ssh://git@github.com/example/repository.git",
            "https://github.com/example/repository")]
        [TestCase(
            "https://github.com/example/repository.git",
            "https://github.com/example/repository")]
        public void RepositoryWebUrl_ConvertsSupportedCloneUrls(
            string cloneUrl,
            string expectedWebUrl)
        {
            Assert.That(
                GitUtility.TryGetRepositoryWebUrl(cloneUrl, out string webUrl),
                Is.True);
            Assert.That(webUrl, Is.EqualTo(expectedWebUrl));
        }

        [Test]
        public void RepositoryLink_IsVisibleAndOpensSanitizedWebsite()
        {
            string openedUrl = string.Empty;
            using PackageManagerGitHubDetails details = CreateDetails(
                out _,
                out _,
                out VisualElement links,
                (_, _) => { },
                url => openedUrl = url);
            details.Refresh(CreateRepository(
                "repository",
                "main",
                "git@github.com:example/repository.git"));

            Assert.That(
                PackageManagerGitHubDetails.RepositoryLinkText,
                Is.EqualTo("Repository"));
            Assert.That(
                details.RepositoryLinkButton.text,
                Is.EqualTo(L10n.Tr("Repository")));
            Assert.That(details.RepositoryLinkButton.ClassListContains("link"), Is.True);
            Assert.That(details.RepositoryLinkButton.parent, Is.Not.Null);
            Assert.That(links.Contains(details.RepositoryLinkButton), Is.True);
            Assert.That(
                links.style.display.value,
                Is.EqualTo(DisplayStyle.Flex));
            Assert.That(details.OpenRepositoryWebsite(), Is.True);
            Assert.That(
                openedUrl,
                Is.EqualTo("https://github.com/example/repository"));
        }

        [Test]
        public void RepositoryLink_ReappearsAfterNativeLinksAreRecycled()
        {
            using PackageManagerGitHubDetails details = CreateDetails(
                out _,
                out _,
                out VisualElement links,
                (_, _) => { },
                _ => { });
            PackageManagerGitHubRepository repository = CreateRepository(
                "repository",
                "main");
            details.Refresh(repository);
            Assert.That(links.Contains(details.RepositoryLinkButton), Is.True);

            links.Clear();
            Assert.That(links.Contains(details.RepositoryLinkButton), Is.False);
            details.Refresh(repository);

            Assert.That(links.Contains(details.RepositoryLinkButton), Is.True);
        }

        [Test]
        public void RepositoryLink_RecycleThenHideRestoresOriginalLinksDisplay()
        {
            using PackageManagerGitHubDetails details = CreateDetails(
                out _,
                out _,
                out VisualElement links,
                (_, _) => { },
                _ => { });
            links.style.display = DisplayStyle.None;
            PackageManagerGitHubRepository repository = CreateRepository(
                "repository",
                "main");

            details.Refresh(repository);
            Assert.That(links.style.display.value, Is.EqualTo(DisplayStyle.Flex));
            links.Clear();
            details.Refresh(repository);
            Assert.That(links.style.display.value, Is.EqualTo(DisplayStyle.Flex));

            details.Refresh(null);

            Assert.That(links.style.display.value, Is.EqualTo(DisplayStyle.None));
        }

        [Test]
        public void BranchChoices_PutMainBeforeRepositoryDefaultAndRejectInvalidOrDuplicateRefs()
        {
            List<string> choices = PackageManagerGitHubDetails.BuildBranchChoices(
                "agents/verdaccio",
                new[]
                {
                    "release",
                    " main ",
                    "agents/verdaccio",
                    "feature/new-ui",
                    "bad..branch",
                    string.Empty,
                    "release"
                });

            Assert.That(
                choices,
                Is.EqualTo(new[]
                {
                    "main",
                    "agents/verdaccio",
                    "release",
                    "feature/new-ui"
                }));
        }

        [Test]
        public void BranchSelector_RemainsUnresolvedBeforeDiscoveryCompletes()
        {
            Assert.That(
                PackageManagerGitHubDetails.BuildBranchChoices(
                    "agents/verdaccio",
                    null),
                Is.Empty,
                "Unknown branch state must not optimistically assume main or the default.");
        }

        [Test]
        public void BranchSelector_TransientListingFailureDisablesInstallWithoutFallback()
        {
            using PackageManagerGitHubDetails details = CreateDetails(
                out _,
                out _,
                out _,
                (_, _) => { },
                _ => { });
            details.Refresh(CreateRepository("repository", "trunk"));
            details.ApplyAvailableBranchesForTests(null);

            details.SetInstallState(true, true, "Ready");

            Assert.That(details.HasAuthoritativeBranchSelection, Is.False);
            Assert.That(details.SelectedBranch, Is.Empty);
            Assert.That(details.BranchField.choices, Is.Empty);
            Assert.That(details.InstallMenu.enabledSelf, Is.False);
        }

        [Test]
        public void BranchSelector_ExplicitRefreshRetriesFailureAndSelectsMain()
        {
            if (AsyncCommandDrainRegistry.IsDraining)
            {
                Assert.Ignore(
                    "The shared command drain is active; branch discovery cannot " +
                    "start until it completes.");
            }

            ICommandRunner previousRunner = CliCommandRunner.CurrentRunner;
            var runner = new BranchRefreshRunner(previousRunner);
            PackageManagerGitHubDetails details = null;
            try
            {
                CliCommandRunner.CurrentRunner = runner;
                details = CreateDetails(
                    out _,
                    out _,
                    out _,
                    (_, _) => { },
                    _ => { },
                    enableBranchDiscovery: true);
                details.InstallSelectionChanged += () =>
                    details.SetInstallState(true, true, "Ready");
                PackageManagerGitHubRepository repository = CreateRepository(
                    "branch-retry",
                    "trunk");

                details.Refresh(repository);
                details.SetInstallState(true, true, "Ready");
                Assert.That(
                    SpinWait.SpinUntil(
                        () => details.TickBranchDiscoveryForTests(),
                        TimeSpan.FromSeconds(3)),
                    Is.True,
                    "The first branch query did not publish its failure.");

                Assert.That(runner.BranchRequestCount, Is.EqualTo(1));
                Assert.That(details.HasAuthoritativeBranchSelection, Is.False);
                Assert.That(details.SelectedBranch, Is.Empty);
                Assert.That(details.InstallMenu.enabledSelf, Is.False);

                // Rebinding and selecting away/back are normal Package Manager
                // view lifecycles, not explicit retry gestures.
                details.Refresh(repository);
                details.Refresh(null);
                details.Refresh(repository);
                details.TickBranchDiscoveryForTests();
                Assert.That(runner.BranchRequestCount, Is.EqualTo(1));

                Assert.That(
                    details.RetryFailedBranchDiscoveryFromUserRefresh(),
                    Is.True);
                Assert.That(details.InstallMenu.enabledSelf, Is.False);
                Assert.That(
                    SpinWait.SpinUntil(
                        () => details.TickBranchDiscoveryForTests(),
                        TimeSpan.FromSeconds(3)),
                    Is.True,
                    "The explicit branch retry did not publish its result.");

                Assert.That(runner.BranchRequestCount, Is.EqualTo(2));
                Assert.That(details.HasAuthoritativeBranchSelection, Is.True);
                Assert.That(details.SelectedBranch, Is.EqualTo("main"));
                Assert.That(details.BranchField.value, Is.EqualTo("main"));
                Assert.That(details.InstallMenu.enabledSelf, Is.True);
                Assert.That(
                    details.RetryFailedBranchDiscoveryFromUserRefresh(),
                    Is.False,
                    "A successful branch cache must not be cleared by refresh.");
                Assert.That(runner.BranchRequestCount, Is.EqualTo(2));
            }
            finally
            {
                details?.Dispose();
                CliCommandRunner.CurrentRunner = previousRunner;
            }
        }

        [Test]
        public void BranchSelector_FallsBackWhenAuthoritativeBranchesDoNotContainMain()
        {
            using PackageManagerGitHubDetails details = CreateDetails(
                out _,
                out _,
                out _,
                (_, _) => { },
                _ => { });

            details.Refresh(CreateRepository("repository", "trunk"));
            Assert.That(details.SelectedBranch, Is.EqualTo("main"));

            details.ApplyAvailableBranchesForTests(
                new[] { "trunk", "release" });

            Assert.That(details.SelectedBranch, Is.EqualTo("trunk"));
            Assert.That(details.BranchField.value, Is.EqualTo("trunk"));
            Assert.That(
                details.BranchField.choices,
                Is.EqualTo(new[] { "trunk", "release" }));
        }

        [Test]
        public void BranchSelector_UsesGitDefaultInsteadOfCatalogueDefault()
        {
            using PackageManagerGitHubDetails details = CreateDetails(
                out _,
                out _,
                out _,
                (_, _) => { },
                _ => { });
            details.Refresh(CreateRepository("repository", "catalogue-default"));

            details.ApplyAvailableBranchesForTests(
                new[] { "release", "git-default" },
                "git-default",
                defaultBranchIsAuthoritative: true);

            Assert.That(details.SelectedBranch, Is.EqualTo("git-default"));
            Assert.That(details.BranchField.value, Is.EqualTo("git-default"));
            Assert.That(
                details.BranchField.choices,
                Is.EqualTo(new[] { "git-default", "release" }));
        }

        [Test]
        public void BranchSelector_InvalidGitHeadStillPrefersCompleteMain()
        {
            using PackageManagerGitHubDetails details = CreateDetails(
                out _,
                out _,
                out _,
                (_, _) => { },
                _ => { });
            details.InstallSelectionChanged += () =>
                details.SetInstallState(true, true, "Ready");
            details.Refresh(CreateRepository("repository", "catalogue-default"));

            details.ApplyAvailableBranchesForTests(
                new[] { "main", "release" },
                string.Empty,
                defaultBranchIsAuthoritative: false);
            details.SetInstallState(true, true, "Ready");

            Assert.That(details.SelectedBranch, Is.EqualTo("main"));
            Assert.That(details.BranchField.choices, Is.EqualTo(
                new[] { "main", "release" }));
            Assert.That(details.InstallMenu.enabledSelf, Is.True);
        }

        [UnityTest]
        public IEnumerator BranchSelector_InvalidGitHeadWithoutMainRequiresManualChoice()
        {
            PackageManagerGitHubDetails details = CreateDetails(
                out _,
                out VisualElement extensionItems,
                out _,
                (_, _) => { },
                _ => { });
            var host = ScriptableObject.CreateInstance<NativeActionsHostWindow>();
            try
            {
                VisualElement fixtureRoot = extensionItems.parent?.parent;
                Assert.That(fixtureRoot, Is.Not.Null);
                host.Show();
                host.rootVisualElement.Add(fixtureRoot);
                yield return null;

                details.InstallSelectionChanged += () =>
                    details.SetInstallState(true, true, "Ready");
                details.Refresh(CreateRepository("repository", "catalogue-default"));
                details.ApplyAvailableBranchesForTests(
                    new[] { "release", "develop" },
                    string.Empty,
                    defaultBranchIsAuthoritative: false);
                details.SetInstallState(true, true, "Ready");

                Assert.That(details.SelectedBranch, Is.Empty);
                Assert.That(details.InstallMenu.enabledSelf, Is.False);

                details.BranchField.value = "release";
                yield return null;

                Assert.That(details.SelectedBranch, Is.EqualTo("release"));
                Assert.That(details.InstallMenu.enabledSelf, Is.True);
            }
            finally
            {
                details.Dispose();
                if (host != null)
                    host.Close();
            }
        }

        [UnityTest]
        public IEnumerator BranchSelector_RequiresManualChoiceWhenMainAndDefaultAreAbsent()
        {
            PackageManagerGitHubDetails details = CreateDetails(
                out _,
                out VisualElement extensionItems,
                out _,
                (_, _) => { },
                _ => { });
            var host = ScriptableObject.CreateInstance<NativeActionsHostWindow>();
            try
            {
                VisualElement fixtureRoot = extensionItems.parent?.parent;
                Assert.That(fixtureRoot, Is.Not.Null);
                host.Show();
                host.rootVisualElement.Add(fixtureRoot);
                yield return null;

                details.InstallSelectionChanged += () =>
                    details.SetInstallState(true, true, "Ready");
                details.Refresh(CreateRepository("repository", "trunk"));
                details.ApplyAvailableBranchesForTests(
                    new[] { "release", "develop" });
                details.SetInstallState(true, true, "Ready");

                Assert.That(details.HasAuthoritativeBranchSelection, Is.False);
                Assert.That(details.SelectedBranch, Is.Empty);
                Assert.That(
                    details.BranchField.choices,
                    Is.EqualTo(new[] { "release", "develop" }));
                Assert.That(details.InstallMenu.enabledSelf, Is.False);

                details.BranchField.value = "release";
                yield return null;

                Assert.That(details.HasAuthoritativeBranchSelection, Is.True);
                Assert.That(details.SelectedBranch, Is.EqualTo("release"));
                Assert.That(details.InstallMenu.enabledSelf, Is.True);
            }
            finally
            {
                details.Dispose();
                if (host != null)
                    host.Close();
            }
        }

        [UnityTest]
        public IEnumerator InstallAction_PassesTheSelectedDiscoveredBranch()
        {
            PackageManagerGitHubRepository installedRepository = null;
            string installedBranch = string.Empty;
            PackageManagerGitHubDetails details = CreateDetails(
                out _,
                out VisualElement extensionItems,
                out _,
                (repository, branch) =>
                {
                    installedRepository = repository;
                    installedBranch = branch;
                },
                _ => { });
            var host = ScriptableObject.CreateInstance<NativeActionsHostWindow>();
            try
            {
                VisualElement fixtureRoot = extensionItems.parent?.parent;
                Assert.That(fixtureRoot, Is.Not.Null);
                host.position = new Rect(100f, 100f, 500f, 200f);
                host.Show();
                host.rootVisualElement.Add(fixtureRoot);
                yield return null;

                PackageManagerGitHubRepository repository = CreateRepository(
                    "repository",
                    "main");
                details.Refresh(repository);
                details.ApplyAvailableBranchesForTests(
                    new[] { "main", "release", "feature/native-ui" });
                details.SetInstallState(true, true, "Ready");

                details.BranchField.value = "release";
                host.Repaint();
                yield return null;

                Assert.That(details.SelectedBranch, Is.EqualTo("release"));
                details.InstallMenu.Focus();
                ExecuteInstallMenuAction(
                    details,
                    PackageManagerGitInstallMode.GitSubmodule);
                // Focus is verified on a later editor update after event
                // propagation, so allow the bounded retry to settle.
                for (int frame = 0;
                     frame < 10 &&
                     !ReferenceEquals(
                         host.rootVisualElement.focusController.focusedElement,
                         details.InstallButton);
                     frame++)
                {
                    yield return null;
                }

                Assert.That(installedRepository, Is.Null);
                Assert.That(installedBranch, Is.Empty);
                Assert.That(details.IsInstallConfirmationPending, Is.True);
                Assert.That(
                    details.InstallButton.text,
                    Is.EqualTo(L10n.Tr(
                        PackageManagerGitHubDetails.ConfirmInstallText)));
                Assert.That(
                    details.CancelInstallButton.style.display.value,
                    Is.EqualTo(DisplayStyle.Flex));
                Assert.That(
                    details.InstallFeedback.style.display.value,
                    Is.EqualTo(DisplayStyle.Flex));
                Assert.That(
                    details.InstallFeedback.messageType,
                    Is.EqualTo(HelpBoxMessageType.Warning));
                Label feedbackLabel = details.InstallFeedback.Q<Label>(
                    className: HelpBox.labelUssClassName);
                Assert.That(feedbackLabel, Is.Not.Null);
                Assert.That(feedbackLabel.enableRichText, Is.False);
                Assert.That(details.BranchField.enabledSelf, Is.False);
                Assert.That(
                    details.InstallMenu.style.display.value,
                    Is.EqualTo(DisplayStyle.None));
                Assert.That(
                    details.InstallButton.style.display.value,
                    Is.EqualTo(DisplayStyle.Flex));
                Assert.That(
                    host.rootVisualElement.focusController.focusedElement,
                    Is.SameAs(details.InstallButton));

                SendNavigationSubmit(details.InstallButton);

                Assert.That(installedRepository, Is.SameAs(repository));
                Assert.That(installedBranch, Is.EqualTo("release"));
                Assert.That(details.IsInstalling, Is.True);
                Assert.That(
                    details.InstallButton.text,
                    Is.EqualTo(L10n.Tr(
                        PackageManagerGitHubDetails.InstallingText)));
                Assert.That(details.InstallButton.enabledSelf, Is.False);
            }
            finally
            {
                details.Dispose();
                if (host != null)
                    host.Close();
            }
        }

        [Test]
        public void InstallAction_PassesReadOnlyModeAndUsesModeBoundCopy()
        {
            PackageManagerGitHubRepository installedRepository = null;
            string installedBranch = string.Empty;
            PackageManagerGitInstallMode installedMode =
                PackageManagerGitInstallMode.GitSubmodule;
            using PackageManagerGitHubDetails details = CreateDetails(
                out _,
                out _,
                out _,
                (repository, branch, installMode) =>
                {
                    installedRepository = repository;
                    installedBranch = branch;
                    installedMode = installMode;
                },
                _ => { });
            PackageManagerGitHubRepository repository = CreateRepository(
                "repository",
                "main");
            details.Refresh(repository);
            details.SetInstallState(true, true, "Ready");

            ExecuteInstallMenuAction(
                details,
                PackageManagerGitInstallMode.ReadOnlyPackage);

            Assert.That(
                details.SelectedInstallMode,
                Is.EqualTo(PackageManagerGitInstallMode.ReadOnlyPackage));

            Assert.That(details.IsInstallConfirmationPending, Is.True);
            Assert.That(
                details.InstallMenu.style.display.value,
                Is.EqualTo(DisplayStyle.None));
            Assert.That(
                details.InstallButton.style.display.value,
                Is.EqualTo(DisplayStyle.Flex));
            Assert.That(
                details.InstallFeedback.text,
                Does.Contain("read-only Package Manager Git dependency"));
            Assert.That(
                details.InstallFeedback.text,
                Does.Not.Contain("Packages/com.example.repository"));
            Assert.That(installedRepository, Is.Null);

            details.TriggerInstall();

            Assert.That(installedRepository, Is.SameAs(repository));
            Assert.That(installedBranch, Is.EqualTo("main"));
            Assert.That(
                installedMode,
                Is.EqualTo(PackageManagerGitInstallMode.ReadOnlyPackage));
            Assert.That(details.IsInstalling, Is.True);
            Assert.That(
                details.InstallFeedback.text,
                Does.Contain("as a read-only Package Manager package"));
            Assert.That(details.InstallMenu.enabledSelf, Is.False);

            details.ShowInstallCompleted(string.Empty);

            Assert.That(
                details.InstallFeedback.text,
                Does.Contain("Read-only package installed"));
        }

        [Test]
        public void InstallMode_DefaultsToSubmoduleForEachRepositorySelection()
        {
            using PackageManagerGitHubDetails details = CreateDetails(
                out _,
                out _,
                out _,
                (_, _, _) => { },
                _ => { });
            details.Refresh(CreateRepository("first", "main"));
            details.SetInstallState(true, true, "Ready");
            details.SelectInstallModeForTests(
                PackageManagerGitInstallMode.ReadOnlyPackage);
            Assert.That(
                details.SelectedInstallMode,
                Is.EqualTo(PackageManagerGitInstallMode.ReadOnlyPackage));

            details.Refresh(CreateRepository("second", "main"));

            Assert.That(
                details.SelectedInstallMode,
                Is.EqualTo(PackageManagerGitInstallMode.GitSubmodule));
            Assert.That(
                details.InstallMenu.style.display.value,
                Is.EqualTo(DisplayStyle.Flex));
            Assert.That(
                details.InstallMenu.text,
                Is.EqualTo(L10n.Tr(
                    PackageManagerGitHubDetails.InstallText)));
        }

        [UnityTest]
        public IEnumerator InstallConfirmation_CancelRestoresIdleWithoutInstalling()
        {
            int installCount = 0;
            PackageManagerGitHubDetails details = CreateDetails(
                out _,
                out VisualElement extensionItems,
                out _,
                (_, _) => installCount++,
                _ => { });
            var host = ScriptableObject.CreateInstance<NativeActionsHostWindow>();
            try
            {
                VisualElement fixtureRoot = extensionItems.parent?.parent;
                Assert.That(fixtureRoot, Is.Not.Null);
                host.position = new Rect(100f, 100f, 500f, 200f);
                host.Show();
                host.rootVisualElement.Add(fixtureRoot);
                yield return null;

                details.Refresh(CreateRepository("repository", "main"));
                details.SetInstallState(true, true, "Ready");
                ExecuteInstallMenuAction(
                    details,
                    PackageManagerGitInstallMode.GitSubmodule);
                yield return null;
                Assert.That(details.IsInstallConfirmationPending, Is.True);

                SendNavigationSubmit(details.CancelInstallButton);
                for (int frame = 0;
                     frame < 10 &&
                     !ReferenceEquals(
                         host.rootVisualElement.focusController.focusedElement,
                         details.InstallMenu);
                     frame++)
                {
                    yield return null;
                }

                Assert.That(installCount, Is.Zero);
                Assert.That(details.IsInstallConfirmationPending, Is.False);
                Assert.That(
                    details.InstallMenu.text,
                    Is.EqualTo(L10n.Tr(
                        PackageManagerGitHubDetails.InstallText)));
                Assert.That(details.InstallMenu.enabledSelf, Is.True);
                Assert.That(
                    details.InstallMenu.style.display.value,
                    Is.EqualTo(DisplayStyle.Flex));
                Assert.That(
                    details.InstallButton.style.display.value,
                    Is.EqualTo(DisplayStyle.None));
                Assert.That(details.BranchField.enabledSelf, Is.True);
                Assert.That(
                    details.CancelInstallButton.style.display.value,
                    Is.EqualTo(DisplayStyle.None));
                Assert.That(
                    details.InstallFeedback.style.display.value,
                    Is.EqualTo(DisplayStyle.None));
                Assert.That(
                    host.rootVisualElement.focusController.focusedElement,
                    Is.SameAs(details.InstallMenu));
            }
            finally
            {
                details.Dispose();
                if (host != null)
                    host.Close();
            }
        }

        [UnityTest]
        public IEnumerator DeferredFocus_DoesNotStealFromAnotherLiveControl()
        {
            PackageManagerGitHubDetails details = CreateDetails(
                out _,
                out VisualElement extensionItems,
                out _,
                (_, _) => { },
                _ => { });
            var host = ScriptableObject.CreateInstance<NativeActionsHostWindow>();
            try
            {
                VisualElement fixtureRoot = extensionItems.parent?.parent;
                Assert.That(fixtureRoot, Is.Not.Null);
                host.position = new Rect(100f, 100f, 500f, 200f);
                host.Show();
                host.rootVisualElement.Add(fixtureRoot);
                yield return null;

                details.Refresh(CreateRepository("repository", "main"));
                details.SetInstallState(true, true, "Ready");
                ExecuteInstallMenuAction(
                    details,
                    PackageManagerGitInstallMode.GitSubmodule);

                for (int frame = 0;
                     frame < 10 &&
                     !ReferenceEquals(
                         host.rootVisualElement.focusController.focusedElement,
                         details.InstallButton);
                     frame++)
                {
                    yield return null;
                }

                Assert.That(
                    host.rootVisualElement.focusController.focusedElement,
                    Is.SameAs(details.InstallButton));
                Assert.That(details.HasDeferredFocusRequest, Is.True);

                details.CancelInstallButton.Focus();
                Assert.That(
                    host.rootVisualElement.focusController.focusedElement,
                    Is.SameAs(details.CancelInstallButton));
                yield return null;

                Assert.That(
                    host.rootVisualElement.focusController.focusedElement,
                    Is.SameAs(details.CancelInstallButton));
                Assert.That(details.HasDeferredFocusRequest, Is.False);
            }
            finally
            {
                details.Dispose();
                if (host != null)
                    host.Close();
            }
        }

        [Test]
        public void RecycledSelection_ResetsToMainInsteadOfTheRepositoryDefault()
        {
            var installRequests = new List<string>();
            using PackageManagerGitHubDetails details = CreateDetails(
                out _,
                out _,
                out _,
                (repository, branch) =>
                    installRequests.Add(repository.Name + ":" + branch),
                _ => { });
            details.Refresh(CreateRepository("first", "main"));
            details.ApplyAvailableBranchesForTests(new[] { "main", "release" });
            details.SetInstallState(true, true, "Ready");
            details.BranchField.value = "release";
            details.TriggerInstall();

            Assert.That(details.IsInstallConfirmationPending, Is.True);
            Assert.That(installRequests, Is.Empty);

            PackageManagerGitHubRepository second = CreateRepository(
                "second",
                "trunk");
            details.Refresh(second);
            details.ApplyAvailableBranchesForTests(
                new[] { "trunk", "develop", "main" });
            details.SetInstallState(true, true, "Ready");

            Assert.That(details.IsInstallConfirmationPending, Is.False);
            Assert.That(
                details.InstallMenu.text,
                Is.EqualTo(L10n.Tr(PackageManagerGitHubDetails.InstallText)));
            Assert.That(
                details.InstallMenu.style.display.value,
                Is.EqualTo(DisplayStyle.Flex));
            Assert.That(installRequests, Is.Empty);

            details.TriggerInstall();
            details.TriggerInstall();

            Assert.That(details.CurrentRepository, Is.SameAs(second));
            Assert.That(details.SelectedBranch, Is.EqualTo("main"));
            Assert.That(installRequests, Is.EqualTo(new[] { "second:main" }));
        }

        [Test]
        public void Confirmation_ResetsWhenSameRepositoryChangesInstallInputs()
        {
            using PackageManagerGitHubDetails details = CreateDetails(
                out _,
                out _,
                out _,
                (_, _) => { },
                _ => { });
            details.Refresh(CreateRepository(
                "repository",
                "main",
                "https://github.com/example/repository.git"));
            details.SetInstallState(true, true, "Ready");
            details.TriggerInstall();
            Assert.That(details.IsInstallConfirmationPending, Is.True);

            details.Refresh(CreateRepository(
                "repository",
                "main",
                "https://github.com/example/repository-renamed.git"));

            Assert.That(details.IsInstallConfirmationPending, Is.False);
            Assert.That(
                details.InstallMenu.text,
                Is.EqualTo(L10n.Tr(PackageManagerGitHubDetails.InstallText)));
            Assert.That(
                details.InstallMenu.style.display.value,
                Is.EqualTo(DisplayStyle.Flex));
            Assert.That(
                details.InstallFeedback.style.display.value,
                Is.EqualTo(DisplayStyle.None));
        }

        [UnityTest]
        public IEnumerator Confirmation_ResetsWhenBranchDiscoveryChangesSelection()
        {
            PackageManagerGitHubDetails details = CreateDetails(
                out _,
                out VisualElement extensionItems,
                out _,
                (_, _) => { },
                _ => { });
            var host = ScriptableObject.CreateInstance<NativeActionsHostWindow>();
            try
            {
                VisualElement fixtureRoot = extensionItems.parent?.parent;
                Assert.That(fixtureRoot, Is.Not.Null);
                host.position = new Rect(100f, 100f, 500f, 200f);
                host.Show();
                host.rootVisualElement.Add(fixtureRoot);
                yield return null;

                details.Refresh(CreateRepository("repository", "main"));
                details.ApplyAvailableBranchesForTests(
                    new[] { "main", "release" });
                details.SetInstallState(true, true, "Ready");
                details.BranchField.value = "release";
                Assert.That(details.SelectedBranch, Is.EqualTo("release"));
                details.TriggerInstall();
                Assert.That(details.IsInstallConfirmationPending, Is.True);

                details.ApplyAvailableBranchesForTests(new[] { "main" });

                Assert.That(details.SelectedBranch, Is.EqualTo("main"));
                Assert.That(details.IsInstallConfirmationPending, Is.False);
                Assert.That(
                    details.InstallMenu.text,
                    Is.EqualTo(L10n.Tr(
                        PackageManagerGitHubDetails.InstallText)));
                Assert.That(
                    details.InstallMenu.style.display.value,
                    Is.EqualTo(DisplayStyle.Flex));
            }
            finally
            {
                details.Dispose();
                if (host != null)
                    host.Close();
            }
        }

        [Test]
        public void InstallConfirmation_UsesSanitizedRepositoryDetails()
        {
            PackageManagerGitHubRepository repository = CreateRepository(
                "repository",
                "main",
                "https://user:super-secret@github.com/example/repository.git");

            string message = PackageManagerGitHubDetails
                .BuildTrustConfirmationMessage(repository, "release");

            Assert.That(message, Does.Contain("github.com/example/repository.git"));
            Assert.That(message, Does.Contain("release"));
            Assert.That(message, Does.Contain("Packages/com.example.repository"));
            Assert.That(message, Does.Contain("Confirm Install"));
            Assert.That(message, Does.Not.Contain("super-secret"));
            Assert.That(message, Does.Not.Contain("user:"));
        }

        [Test]
        public void InstallConfirmation_FailsClosedWithoutRepositoryIdentity()
        {
            bool installRequested = false;
            using PackageManagerGitHubDetails details = CreateDetails(
                out _,
                out _,
                out _,
                (_, _) => installRequested = true,
                _ => { });
            details.Refresh(CreateRepository("repository", "main", string.Empty));
            details.SetInstallState(true, true, "Ready");

            details.TriggerInstall();
            details.TriggerInstall();

            Assert.That(details.IsInstallConfirmationPending, Is.False);
            Assert.That(installRequested, Is.False);
            Assert.That(
                details.InstallFeedback.messageType,
                Is.EqualTo(HelpBoxMessageType.Error));
            Assert.That(
                details.InstallFeedback.text,
                Does.Contain("safe confirmation"));
        }

        [Test]
        public void InstallIdentity_BindsRepositoryInputsAndBranchWithoutCredentials()
        {
            PackageManagerGitHubRepository repository = CreateRepository(
                "repository",
                "main",
                "https://user:super-secret@github.com/example/repository.git");
            PackageManagerGitHubRepository changedUrl = CreateRepository(
                "repository",
                "main",
                "https://github.com/example/other-repository.git");
            PackageManagerGitHubRepository changedSshUser = CreateRepository(
                "repository",
                "main",
                "ssh://deploy@github.com/example/repository.git");
            PackageManagerGitHubRepository originalSshUser = CreateRepository(
                "repository",
                "main",
                "ssh://git@github.com/example/repository.git");

            string repositoryIdentity = PackageManagerGitHubDetails
                .GetInstallRepositoryIdentity(repository);
            string mainSelection = PackageManagerGitHubDetails
                .GetInstallSelectionIdentity(repository, "main");
            string releaseSelection = PackageManagerGitHubDetails
                .GetInstallSelectionIdentity(repository, "release");
            string readOnlySelection = PackageManagerGitHubDetails
                .GetInstallSelectionIdentity(
                    repository,
                    "main",
                    PackageManagerGitInstallMode.ReadOnlyPackage);

            Assert.That(mainSelection, Does.StartWith(repositoryIdentity));
            Assert.That(mainSelection, Is.Not.EqualTo(releaseSelection));
            Assert.That(mainSelection, Is.Not.EqualTo(readOnlySelection));
            Assert.That(
                repositoryIdentity,
                Is.Not.EqualTo(PackageManagerGitHubDetails
                    .GetInstallRepositoryIdentity(changedUrl)));
            Assert.That(
                PackageManagerGitHubDetails.GetInstallRepositoryIdentity(
                    originalSshUser),
                Is.Not.EqualTo(
                    PackageManagerGitHubDetails.GetInstallRepositoryIdentity(
                        changedSshUser)));
            Assert.That(repositoryIdentity, Does.Not.Contain("super-secret"));
            Assert.That(repositoryIdentity, Does.Not.Contain("user:"));
        }

        [Test]
        public void RecoveredDependencyCompletion_MatchesOnlyItsNativeDetails()
        {
            PackageManagerGitHubRepository repository = CreateRepository(
                "repository",
                "main",
                "https://github.com/example/repository.git");
            var matching = new PackageDependencyInstallPipelineCompletion(
                true,
                false,
                "Installed.",
                "git@github.com:example/repository.git",
                "main",
                "com.example.repository",
                PackageManagerGitInstallMode.GitSubmodule);
            var wrongPackage = new PackageDependencyInstallPipelineCompletion(
                true,
                false,
                "Installed.",
                repository.Url,
                "main",
                "com.example.other",
                PackageManagerGitInstallMode.GitSubmodule);
            var wrongRepository = new PackageDependencyInstallPipelineCompletion(
                true,
                false,
                "Installed.",
                "https://github.com/example/other.git",
                "main",
                repository.PackageName,
                PackageManagerGitInstallMode.GitSubmodule);

            Assert.That(
                PackageManagerGitHubNativeActions
                    .MatchesDependencyInstallCompletion(repository, matching),
                Is.True);
            Assert.That(
                PackageManagerGitHubNativeActions
                    .MatchesDependencyInstallCompletion(repository, wrongPackage),
                Is.False);
            Assert.That(
                PackageManagerGitHubNativeActions
                    .MatchesDependencyInstallCompletion(repository, wrongRepository),
                Is.False);
            Assert.That(
                PackageManagerGitHubNativeActions
                    .MatchesDependencyInstallCompletion(null, matching),
                Is.False);
        }

        [Test]
        public void DependencyCompletion_SchedulesFallbackOnlyWhenRetainedAndUnshown()
        {
            var live = new PackageDependencyInstallPipelineCompletion(
                true,
                false,
                "Installed.",
                "https://github.com/example/repository.git",
                "main",
                "com.example.repository",
                PackageManagerGitInstallMode.GitSubmodule);
            var recovered = new PackageDependencyInstallPipelineCompletion(
                true,
                false,
                "Installed.",
                "https://github.com/example/repository.git",
                "main",
                "com.example.repository",
                PackageManagerGitInstallMode.GitSubmodule,
                true);

            Assert.That(
                PackageManagerGitHubNativeActions
                    .ShouldScheduleRecoveredCompletion(live, false, false),
                Is.False,
                "A live popup callback that consumed its result must not gain " +
                "a second global dialog.");
            Assert.That(
                PackageManagerGitHubNativeActions
                    .ShouldScheduleRecoveredCompletion(live, false, true),
                Is.True,
                "A closed popup leaves the retained result for immediate " +
                "global presentation even without an assembly reload.");
            Assert.That(
                PackageManagerGitHubNativeActions
                    .ShouldScheduleRecoveredCompletion(recovered, true),
                Is.False,
                "Matching native details already presented the result inline.");
            Assert.That(
                PackageManagerGitHubNativeActions
                    .ShouldScheduleRecoveredCompletion(recovered, false),
                Is.True,
                "A callback lost to reload needs the global recovery path.");
        }

        [Test]
        public void CoordinatedReadOnlyPrimitive_IsNeverPresentedAsStandaloneAfterCoordinatorClears()
        {
            string operationId = System.Guid.NewGuid().ToString("N");
            var leaf = new ReadOnlyGitPackageInstallCompletion(
                false,
                "Dependency failed.",
                "com.example.dependency",
                null,
                operationId);
            var root = new ReadOnlyGitPackageInstallCompletion(
                false,
                "Root failed.",
                "com.example.root",
                null,
                operationId);
            var standalone = new ReadOnlyGitPackageInstallCompletion(
                false,
                "Standalone failed.",
                "com.example.standalone",
                null);

            Assert.That(
                PackageManagerGitHubNativeActions
                    .ShouldPresentReadOnlyCompletionAsStandalone(
                        leaf,
                        false,
                        false),
                Is.False);
            Assert.That(
                PackageManagerGitHubNativeActions
                    .ShouldPresentReadOnlyCompletionAsStandalone(
                        root,
                        false,
                        false),
                Is.False);
            Assert.That(
                PackageManagerGitHubNativeActions
                    .ShouldPresentReadOnlyCompletionAsStandalone(
                        standalone,
                        false,
                        false),
                Is.True);
            Assert.That(
                PackageManagerGitHubNativeActions
                    .ShouldPresentReadOnlyCompletionAsStandalone(
                        standalone,
                        true,
                        false),
                Is.False);
        }

        [Test]
        public void InstallFeedback_ShowsProgressAndRecoverableInlineError()
        {
            using PackageManagerGitHubDetails details = CreateDetails(
                out _,
                out _,
                out _,
                (_, _) => { },
                _ => { });
            details.Refresh(CreateRepository("repository", "main"));
            details.SetInstallState(true, true, "Ready");

            details.ShowInstalling("Installing safely...");

            Assert.That(details.IsInstalling, Is.True);
            Assert.That(details.InstallFeedback.text, Is.EqualTo("Installing safely..."));
            Assert.That(
                details.InstallFeedback.style.display.value,
                Is.EqualTo(DisplayStyle.Flex));
            Assert.That(
                details.InstallFeedback.messageType,
                Is.EqualTo(HelpBoxMessageType.Info));
            Assert.That(details.InstallButton.enabledSelf, Is.False);
            Assert.That(details.BranchField.enabledSelf, Is.False);
            Assert.That(details.InstallMenu.enabledSelf, Is.False);
            Assert.That(
                details.InstallMenu.style.display.value,
                Is.EqualTo(DisplayStyle.None));
            Assert.That(
                details.InstallButton.style.display.value,
                Is.EqualTo(DisplayStyle.Flex));
            Assert.That(
                details.CancelInstallButton.style.display.value,
                Is.EqualTo(DisplayStyle.None));

            details.ShowInstallCompleted(
                "Git submodule installed. Refreshing Package Manager...");

            Assert.That(details.IsInstallCompleted, Is.True);
            Assert.That(
                details.InstallButton.text,
                Is.EqualTo(L10n.Tr(PackageManagerGitHubDetails.InstalledText)));
            Assert.That(details.InstallButton.enabledSelf, Is.False);
            Assert.That(details.BranchField.enabledSelf, Is.False);
            Assert.That(details.InstallMenu.enabledSelf, Is.False);
            Assert.That(
                details.InstallFeedback.messageType,
                Is.EqualTo(HelpBoxMessageType.Info));

            details.ShowInstallError(
                "Clone failed for file:///tmp/repository<size=0>.git");

            Assert.That(details.IsInstalling, Is.False);
            Label feedbackLabel = details.InstallFeedback.Q<Label>(
                className: HelpBox.labelUssClassName);
            Assert.That(feedbackLabel, Is.Not.Null);
            Assert.That(feedbackLabel.enableRichText, Is.False);
            Assert.That(details.InstallFeedback.text, Does.Contain("<size=0>"));
            Assert.That(
                details.InstallMenu.text,
                Is.EqualTo(L10n.Tr(PackageManagerGitHubDetails.RetryInstallText)));
            Assert.That(details.InstallMenu.enabledSelf, Is.True);
            Assert.That(
                details.InstallMenu.style.display.value,
                Is.EqualTo(DisplayStyle.Flex));
            Assert.That(
                details.InstallButton.style.display.value,
                Is.EqualTo(DisplayStyle.None));
            Assert.That(details.BranchField.enabledSelf, Is.True);
            Assert.That(
                details.InstallFeedback.messageType,
                Is.EqualTo(HelpBoxMessageType.Error));
            Assert.That(details.InstallFeedback.text, Does.Contain("Clone failed"));
            Assert.That(details.InstallFeedback.text, Does.Not.Contain("secret"));
            Assert.That(details.InstallFeedback.text, Does.Not.Contain("user:"));
        }

        [Test]
        public void InstallFeedback_ReappearsAfterNativeContainerIsRecycled()
        {
            using PackageManagerGitHubDetails details = CreateDetails(
                out _,
                out _,
                out _,
                (_, _) => { },
                _ => { });
            PackageManagerGitHubRepository repository = CreateRepository(
                "repository",
                "main");
            details.Refresh(repository);
            details.SetInstallState(true, true, "Ready");
            details.TriggerInstall();
            VisualElement feedbackContainer = details.InstallFeedback.parent;
            Assert.That(feedbackContainer, Is.Not.Null);

            feedbackContainer.Clear();
            Assert.That(details.InstallFeedback.parent, Is.Null);
            details.Refresh(repository);

            Assert.That(details.InstallFeedback.parent, Is.SameAs(feedbackContainer));
            Assert.That(details.IsInstallConfirmationPending, Is.True);
            Assert.That(
                details.InstallFeedback.style.display.value,
                Is.EqualTo(DisplayStyle.Flex));
        }

        [Test]
        public void HiddenSelection_RemovesOwnedLinkAndDisablesPrimaryAction()
        {
            using PackageManagerGitHubDetails details = CreateDetails(
                out _,
                out _,
                out VisualElement links,
                (_, _) => { },
                _ => { });
            details.Refresh(CreateRepository("repository", "main"));
            details.SetInstallState(true, true, "Ready");
            Assert.That(links.Contains(details.RepositoryLinkButton), Is.True);
            Assert.That(details.InstallMenu.enabledSelf, Is.True);

            details.Refresh(null);

            Assert.That(details.CurrentRepository, Is.Null);
            Assert.That(details.SelectedBranch, Is.Empty);
            Assert.That(details.RepositoryLinkButton.parent, Is.Null);
            Assert.That(details.InstallMenu.enabledSelf, Is.False);
            Assert.That(details.InstallButton.enabledSelf, Is.False);
            Assert.That(
                details.Controls.style.display.value,
                Is.EqualTo(DisplayStyle.None));
        }

        [Test]
        public void RemovedControls_AreRemountedWithoutDuplication()
        {
            using PackageManagerGitHubDetails details = CreateDetails(
                out VisualElement primaryActions,
                out _,
                out _,
                (_, _) => { },
                _ => { });
            details.Refresh(CreateRepository("repository", "main"));
            details.Controls.RemoveFromHierarchy();

            details.Refresh(CreateRepository("repository", "main"));

            Assert.That(details.Controls.parent, Is.SameAs(primaryActions));
            Assert.That(
                primaryActions.Query<VisualElement>(
                        name: PackageManagerGitHubDetails.ControlsElementName)
                    .ToList()
                    .Count,
                Is.EqualTo(1));
        }

        [Test]
        public void Dispose_RemovesControlsLinksAndPreventsStaleInstallCallbacks()
        {
            int installCount = 0;
            PackageManagerGitHubDetails details = CreateDetails(
                out _,
                out _,
                out _,
                (_, _) => installCount++,
                _ => { });
            details.Refresh(CreateRepository("repository", "main"));
            details.SetInstallState(true, true, "Ready");
            ExecuteInstallMenuAction(
                details,
                PackageManagerGitInstallMode.GitSubmodule);

            Assert.That(details.HasDeferredFocusRequest, Is.True);

            details.Dispose();
            details.Dispose();
            details.TriggerInstall();

            Assert.That(details.IsDisposed, Is.True);
            Assert.That(details.HasDeferredFocusRequest, Is.False);
            Assert.That(details.Controls.parent, Is.Null);
            Assert.That(details.RepositoryLinkButton.parent, Is.Null);
            Assert.That(installCount, Is.Zero);
        }

        [Test]
        public void UnsupportedRoot_InstallAndReleaseRemainIdempotentNoOps()
        {
            int before = PackageManagerGitHubNativeActions.InstalledRootCount;
            var unsupportedRoot = new VisualElement();

            Assert.That(
                PackageManagerGitHubNativeActions.InstallForRoot(unsupportedRoot),
                Is.False);
            Assert.That(
                PackageManagerGitHubNativeActions.InstallForRoot(unsupportedRoot),
                Is.False);
            PackageManagerGitHubNativeActions.ReleaseForRoot(unsupportedRoot);
            PackageManagerGitHubNativeActions.ReleaseForRoot(unsupportedRoot);

            Assert.That(
                PackageManagerGitHubNativeActions.IsInstalledForRoot(
                    unsupportedRoot),
                Is.False);
            Assert.That(
                PackageManagerGitHubNativeActions.InstalledRootCount,
                Is.EqualTo(before));
        }

        [Test]
        public void RemoveDetails_UsesInlineConfirmationBeforeStartingGit()
        {
            var root = new VisualElement();
            var primaryActions = new VisualElement();
            var detailsHeader = new VisualElement();
            var detailsLinks = new VisualElement
            {
                name = PackageManagerGitHubDetails.NativeDetailsLinksContainerName
            };
            var helpBoxes = new VisualElement
            {
                name = PackageManagerGitHubDetails.NativeHelpBoxContainerName
            };
            root.Add(primaryActions);
            detailsHeader.Add(detailsLinks);
            detailsHeader.Add(helpBoxes);
            root.Add(detailsHeader);

            int removeCount = 0;
            Assert.That(
                PackageManagerSubmoduleRemoveDetails.TryCreate(
                    primaryActions,
                    detailsLinks,
                    _ => removeCount++,
                    out PackageManagerSubmoduleRemoveDetails details),
                Is.True);
            using (details)
            {
                var info = new PackageManagerSubmoduleInfo(
                    "com.example.package",
                    "Packages/com.example.package",
                    "/project/Packages/com.example.package",
                    "https://github.com/example/package.git",
                    true);
                details.Refresh(info);
                details.SetRemoveState(true, "Ready");
                Assert.That(
                    details.RemoveButton.parent.style.display.value,
                    Is.EqualTo(DisplayStyle.None),
                    "Removal must start from Unity's native Remove action.");

                // Native PackageAction requests can be broadcast to more than
                // one Package Manager host. Mirroring confirmation must remain
                // idempotent and must never start the Git operation itself.
                details.ShowConfirmation();
                details.ShowConfirmation();

                Assert.That(details.IsConfirmationPending, Is.True);
                Assert.That(removeCount, Is.Zero);
                Assert.That(
                    details.RemoveButton.text,
                    Is.EqualTo(L10n.Tr(
                        PackageManagerSubmoduleRemoveDetails.ConfirmRemoveText)));
                Assert.That(
                    details.Feedback.messageType,
                    Is.EqualTo(HelpBoxMessageType.Warning));
                Assert.That(details.Feedback.text,
                    Does.Contain("state has not changed"));
                Assert.That(
                    details.RemoveButton.parent.style.display.value,
                    Is.EqualTo(DisplayStyle.Flex));

                details.TriggerRemove();

                Assert.That(removeCount, Is.EqualTo(1));
                Assert.That(details.IsRemoving, Is.True);
                Assert.That(details.RemoveButton.enabledSelf, Is.False);
            }
        }

        [Test]
        public void RemoveDetails_DirtyAssessmentRequiresExplicitDiscard()
        {
            var primaryActions = new VisualElement();
            var detailsHeader = new VisualElement();
            var detailsLinks = new VisualElement
            {
                name = PackageManagerGitHubDetails.NativeDetailsLinksContainerName
            };
            detailsHeader.Add(detailsLinks);
            detailsHeader.Add(new VisualElement
            {
                name = PackageManagerGitHubDetails.NativeHelpBoxContainerName
            });
            PackageManagerSubmoduleInfo requestedInfo = null;
            Assert.That(
                PackageManagerSubmoduleRemoveDetails.TryCreate(
                    primaryActions,
                    detailsLinks,
                    info => requestedInfo = info,
                    out PackageManagerSubmoduleRemoveDetails details),
                Is.True);
            using (details)
            {
                var info = new PackageManagerSubmoduleInfo(
                    "com.example.package",
                    "Packages/com.example.package",
                    "/project/Packages/com.example.package",
                    "https://github.com/example/package.git",
                    true);
                var assessment = new SubmoduleRemovalAssessment
                {
                    Path = info.PackagePath,
                    IsInitialized = true,
                    HasWorkingTreeChanges = true,
                    HeadCommit = new string('a', 40),
                    WorktreeStatus = "? local.txt\n"
                };
                details.Refresh(info);
                details.SetRemoveState(true, "Ready");

                Assert.That(details.ShowConfirmation(assessment), Is.True);

                Assert.That(details.ConfirmedAssessment, Is.Not.SameAs(assessment));
                Assert.That(
                    GitUtility.RemovalAssessmentMatches(
                        assessment,
                        details.ConfirmedAssessment),
                    Is.True);
                Assert.That(details.DiscardLocalWork, Is.True);
                Assert.That(
                    details.RemoveButton.text,
                    Is.EqualTo(L10n.Tr(
                        PackageManagerSubmoduleRemoveDetails.ConfirmDiscardText)));
                Assert.That(details.Feedback.text, Does.Contain("would discard"));
                Assert.That(requestedInfo, Is.Null);

                details.TriggerRemove();

                Assert.That(requestedInfo, Is.SameAs(info));
            }
        }

        [Test]
        public void RemoveDetails_DifferentSelectionClearsConfirmedAssessment()
        {
            var primaryActions = new VisualElement();
            var detailsHeader = new VisualElement();
            var detailsLinks = new VisualElement
            {
                name = PackageManagerGitHubDetails.NativeDetailsLinksContainerName
            };
            detailsHeader.Add(detailsLinks);
            detailsHeader.Add(new VisualElement
            {
                name = PackageManagerGitHubDetails.NativeHelpBoxContainerName
            });
            Assert.That(
                PackageManagerSubmoduleRemoveDetails.TryCreate(
                    primaryActions,
                    detailsLinks,
                    _ => { },
                    out PackageManagerSubmoduleRemoveDetails details),
                Is.True);
            using (details)
            {
                var first = new PackageManagerSubmoduleInfo(
                    "com.example.first",
                    "Packages/com.example.first",
                    "/project/Packages/com.example.first",
                    "https://github.com/example/first.git",
                    true);
                var second = new PackageManagerSubmoduleInfo(
                    "com.example.second",
                    "Packages/com.example.second",
                    "/project/Packages/com.example.second",
                    "https://github.com/example/second.git",
                    true);
                var assessment = new SubmoduleRemovalAssessment
                {
                    Path = first.PackagePath,
                    IsInitialized = true,
                    HasWorkingTreeChanges = true,
                    HeadCommit = new string('d', 40),
                    WorktreeStatus = "? local.txt\n"
                };
                details.Refresh(first);
                details.SetRemoveState(true, "Ready");
                Assert.That(details.ShowConfirmation(assessment), Is.True);
                Assert.That(details.ConfirmedAssessment, Is.Not.Null);
                Assert.That(details.DiscardLocalWork, Is.True);

                details.Refresh(second);

                Assert.That(details.CurrentInfo, Is.SameAs(second));
                Assert.That(details.IsConfirmationPending, Is.False);
                Assert.That(details.ConfirmedAssessment, Is.Null);
                Assert.That(details.DiscardLocalWork, Is.False);
            }
        }

        [Test]
        public void RemoveDetails_UnverifiedResidualContentsCannotBeConfirmed()
        {
            var primaryActions = new VisualElement();
            var detailsHeader = new VisualElement();
            var detailsLinks = new VisualElement
            {
                name = PackageManagerGitHubDetails.NativeDetailsLinksContainerName
            };
            detailsHeader.Add(detailsLinks);
            detailsHeader.Add(new VisualElement
            {
                name = PackageManagerGitHubDetails.NativeHelpBoxContainerName
            });
            Assert.That(
                PackageManagerSubmoduleRemoveDetails.TryCreate(
                    primaryActions,
                    detailsLinks,
                    _ => Assert.Fail("Unverified files must never be discarded."),
                    out PackageManagerSubmoduleRemoveDetails details),
                Is.True);
            using (details)
            {
                var info = new PackageManagerSubmoduleInfo(
                    "com.example.package",
                    "Packages/com.example.package",
                    "/project/Packages/com.example.package",
                    "https://github.com/example/package.git",
                    true);
                var assessment = new SubmoduleRemovalAssessment
                {
                    Path = info.PackagePath,
                    HasWorkingTreeChanges = true,
                    HasUnverifiedWorktreeContents = true,
                    WorktreeStatus = "orphaned.txt\n"
                };
                details.Refresh(info);
                details.SetRemoveState(true, "Ready");

                Assert.That(details.ShowConfirmation(assessment), Is.False);

                Assert.That(details.IsConfirmationPending, Is.False);
                Assert.That(details.ConfirmedAssessment, Is.Null);
                Assert.That(details.DiscardLocalWork, Is.False);
                Assert.That(
                    details.Feedback.messageType,
                    Is.EqualTo(HelpBoxMessageType.Error));
                Assert.That(details.Feedback.text, Does.Contain("unverified"));
            }
        }

        [UnityTest]
        public IEnumerator RemoveDetails_CancelAndErrorsRemainInlineAndSanitized()
        {
            var root = new VisualElement();
            var primaryActions = new VisualElement();
            var detailsHeader = new VisualElement();
            var detailsLinks = new VisualElement
            {
                name = PackageManagerGitHubDetails.NativeDetailsLinksContainerName
            };
            var helpBoxes = new VisualElement
            {
                name = PackageManagerGitHubDetails.NativeHelpBoxContainerName
            };
            root.Add(primaryActions);
            detailsHeader.Add(detailsLinks);
            detailsHeader.Add(helpBoxes);
            root.Add(detailsHeader);

            PackageManagerSubmoduleRemoveDetails details = null;
            var host = ScriptableObject.CreateInstance<NativeActionsHostWindow>();
            try
            {
                Assert.That(
                    PackageManagerSubmoduleRemoveDetails.TryCreate(
                        primaryActions,
                        detailsLinks,
                        _ => Assert.Fail("Removal should not start."),
                        out details),
                    Is.True);
                host.position = new Rect(100f, 100f, 500f, 200f);
                host.Show();
                host.rootVisualElement.Add(root);
                yield return null;

                details.Refresh(new PackageManagerSubmoduleInfo(
                    "com.example.package",
                    "Packages/com.example.package",
                    "/project/Packages/com.example.package",
                    "git@github.com:example/package.git",
                    true));
                details.SetRemoveState(true, "Ready");
                details.TriggerRemove();
                SendNavigationSubmit(details.CancelButton);

                Assert.That(details.IsConfirmationPending, Is.False);
                Assert.That(
                    details.RemoveButton.text,
                    Is.EqualTo(L10n.Tr(
                        PackageManagerSubmoduleRemoveDetails.RemoveText)));

                details.ShowError(
                    "Removal blocked for https://user:secret@example.com/repo.git");

                Assert.That(
                    details.Feedback.messageType,
                    Is.EqualTo(HelpBoxMessageType.Error));
                Assert.That(details.Feedback.text, Does.Not.Contain("secret"));
                Assert.That(details.Feedback.text, Does.Not.Contain("user:"));
                Assert.That(
                    details.RemoveButton.text,
                    Is.EqualTo(L10n.Tr(
                        PackageManagerSubmoduleRemoveDetails.RetryRemoveText)));
            }
            finally
            {
                details?.Dispose();
                if (host != null)
                    host.Close();
            }
        }

        [Test]
        public void RemoveService_RequiresExactPackageIdentityAndPath()
        {
            Assert.That(
                GitSubmoduleRemoveService.ValidateInput(
                    new PackageManagerSubmoduleInfo(
                        "com.example.package",
                        "Packages/com.example.package",
                        "/project/Packages/com.example.package",
                        "https://github.com/example/package.git",
                        true)),
                Is.Empty);
            Assert.That(
                GitSubmoduleRemoveService.ValidateInput(
                    new PackageManagerSubmoduleInfo(
                        "com.example.package",
                        "Packages/com.example.different",
                        "/project/Packages/com.example.different",
                        "https://github.com/example/package.git",
                        true)),
                Does.Contain("identity"));
        }

        private static PackageManagerGitHubDetails CreateDetails(
            VisualElement primaryActions,
            VisualElement detailsLinks)
        {
            Assert.That(
                PackageManagerGitHubDetails.TryCreate(
                    primaryActions,
                    detailsLinks,
                    (_, _, _) => { },
                    _ => { },
                    false,
                    out PackageManagerGitHubDetails details),
                Is.True);
            return details;
        }

        private static PackageManagerGitHubDetails CreateDetails(
            out VisualElement primaryActions,
            out VisualElement extensionItems,
            out VisualElement detailsLinks,
            Action<PackageManagerGitHubRepository, string> install,
            Action<string> openUrl,
            bool enableBranchDiscovery = false)
        {
            return CreateDetails(
                out primaryActions,
                out extensionItems,
                out detailsLinks,
                (repository, branch, _) => install(repository, branch),
                openUrl,
                enableBranchDiscovery);
        }

        private static PackageManagerGitHubDetails CreateDetails(
            out VisualElement primaryActions,
            out VisualElement extensionItems,
            out VisualElement detailsLinks,
            Action<PackageManagerGitHubRepository, string,
                PackageManagerGitInstallMode> install,
            Action<string> openUrl,
            bool enableBranchDiscovery = false)
        {
            var root = new VisualElement();
            var toolbar = new VisualElement();
            var detailsHeader = new VisualElement();
            extensionItems = new VisualElement
            {
                name = PackageManagerGitHubDetails.NativeExtensionActionsContainerName
            };
            primaryActions = new VisualElement();
            detailsLinks = new VisualElement
            {
                name = PackageManagerGitHubDetails.NativeDetailsLinksContainerName
            };
            var helpBoxContainer = new VisualElement
            {
                name = PackageManagerGitHubDetails.NativeHelpBoxContainerName
            };
            toolbar.Add(extensionItems);
            toolbar.Add(primaryActions);
            root.Add(toolbar);
            detailsHeader.Add(detailsLinks);
            detailsHeader.Add(helpBoxContainer);
            root.Add(detailsHeader);

            Assert.That(
                PackageManagerGitHubDetails.TryCreate(
                    primaryActions,
                    detailsLinks,
                    install,
                    openUrl,
                    enableBranchDiscovery,
                    out PackageManagerGitHubDetails details),
                Is.True);
            return details;
        }

        private sealed class BranchRefreshRunner : ICommandRunner
        {
            private readonly ICommandRunner fallback;
            private int branchRequestCount;

            internal BranchRefreshRunner(ICommandRunner fallback)
            {
                this.fallback = fallback;
            }

            internal int BranchRequestCount =>
                Volatile.Read(ref branchRequestCount);

            public CommandResult Run(CommandSpec spec)
            {
                IReadOnlyList<string> arguments = spec?.ArgumentList;
                if (arguments == null ||
                    !arguments.Contains("ls-remote") ||
                    !arguments.Contains("--symref"))
                {
                    return fallback.Run(spec);
                }

                int request = Interlocked.Increment(ref branchRequestCount);
                if (request == 1)
                {
                    return new CommandResult
                    {
                        ExitCode = 1,
                        StdOut = string.Empty,
                        StdErr = "Transient branch listing failure.",
                        TerminationConfirmed = true
                    };
                }

                return new CommandResult
                {
                    ExitCode = 0,
                    StdOut =
                        "ref: refs/heads/main\tHEAD\n" +
                        "2222222222222222222222222222222222222222\tHEAD\n" +
                        "1111111111111111111111111111111111111111\t" +
                        "refs/heads/release\n" +
                        "2222222222222222222222222222222222222222\t" +
                        "refs/heads/main\n",
                    StdErr = string.Empty,
                    TerminationConfirmed = true
                };
            }
        }

        private static void SendNavigationSubmit(VisualElement target)
        {
            NavigationSubmitEvent submit = NavigationSubmitEvent.GetPooled();
            try
            {
                submit.target = target;
                target.SendEvent(submit);
            }
            finally
            {
                submit.Dispose();
            }
        }

        private static void ExecuteInstallMenuAction(
            PackageManagerGitHubDetails details,
            PackageManagerGitInstallMode installMode)
        {
            FindInstallMenuAction(details, installMode).Execute();
        }

        private static DropdownMenuAction FindInstallMenuAction(
            PackageManagerGitHubDetails details,
            PackageManagerGitInstallMode installMode)
        {
            string actionName = PackageManagerGitHubDetails
                .GetInstallMenuActionText(installMode);
            DropdownMenuAction action = null;
            foreach (DropdownMenuItem item in details.InstallMenu.menu.MenuItems())
            {
                if (item is DropdownMenuAction candidate &&
                    string.Equals(
                        candidate.name,
                        actionName,
                        StringComparison.Ordinal))
                {
                    action = candidate;
                    break;
                }
            }

            Assert.That(action, Is.Not.Null);
            return action;
        }

        private static PackageManagerGitHubRepository CreateRepository(
            string name,
            string defaultBranch,
            string url = null)
        {
            return new PackageManagerGitHubRepository(new GitHubRepo
            {
                NodeId = "node-" + name,
                Name = name,
                Owner = "example",
                Url = url ?? $"https://github.com/example/{name}.git",
                DefaultBranch = defaultBranch,
                ManifestState = PackageManifestState.Valid,
                DeclaredPackageName = "com.example." + name.Replace('-', '.'),
                DeclaredDisplayName = name,
                DeclaredVersion = "1.0.0"
            });
        }

        private static PackageManagerGitHubDiscoverySnapshot CreateDiscoverySnapshot(
            PackageManagerGitHubRepository repository,
            bool retained)
        {
            return new PackageManagerGitHubDiscoverySnapshot(
                new[] { repository },
                isLoading: true,
                statusMessage: string.Empty,
                errorMessage: string.Empty,
                completedPages: 0,
                completedOwners: 0,
                totalOwners: 1,
                unavailableManifestCount: 0,
                revision: 1,
                isShowingRetainedRepositories: retained);
        }

        private static PackageManagerReadOnlyGitInfo CreateReadOnlyInfo(
            string repositoryUrl,
            string revision,
            string resolvedHash)
        {
            return new PackageManagerReadOnlyGitInfo(
                "com.example.package",
                repositoryUrl,
                repositoryUrl + "#" + revision,
                revision,
                resolvedHash,
                string.Empty,
                null);
        }

        private static PackageManagerNativeRemoveSelectionRouting
            ClassifyNativeRemoveSelection(
                object selection,
                bool snapshotReady,
                out PackageManagerGitHubNativeActions.NativeRemovePlan plan,
                out string error)
        {
            return PackageManagerGitHubNativeActions.ClassifyNativeRemoveSelection(
                selection,
                snapshotReady,
                package => ((NativeRemovePackageFixture)package).Version,
                package => ((NativeRemovePackageFixture)package).PackageName,
                version => snapshotReady
                    ? ((NativeRemoveVersionFixture)version).Submodule
                    : null,
                version => ((NativeRemoveVersionFixture)version)
                    .IsDirectInstalledPackagePath,
                out plan,
                out error);
        }

        private static PackageManagerSubmoduleInfo CreateNativeRemoveSubmodule(
            string packageName)
        {
            return new PackageManagerSubmoduleInfo(
                packageName,
                "Packages/" + packageName,
                "/project/Packages/" + packageName,
                "https://github.com/example/" + packageName + ".git",
                true);
        }

        private static PackageManagerPackageConversionTarget
            CreateReadOnlyConversionTarget(PackageManagerReadOnlyGitInfo info)
        {
            return new PackageManagerPackageConversionTarget(
                GitPackageConversionDirection.ReadOnlyToSubmodule,
                info.PackageName,
                GitSubmoduleAddService.GetPackagePath(info.PackageName),
                info.RepositoryUrl,
                info.Revision + "@" + info.ResolvedHash + "|package-path:" +
                info.PackageSubfolder);
        }

        private sealed class PrimaryActionsFieldFixture
        {
            private readonly VisualElement m_BuiltInActionsContainer = new();

            internal VisualElement PrimaryActions => m_BuiltInActionsContainer;
        }

        private sealed class WrongPrimaryActionsFieldFixture
        {
#pragma warning disable CS0414
            private readonly string m_BuiltInActionsContainer = string.Empty;
#pragma warning restore CS0414
        }

        private sealed class UnrelatedPrimaryActionsFieldFixture
        {
            private readonly VisualElement m_BuiltInActionsContainer = new();
        }

        private sealed class SelectionPackageFixture
        {
            internal SelectionPackageFixture(string uniqueId, string name)
            {
                UniqueId = uniqueId;
                Name = name;
            }

            internal string UniqueId { get; }
            internal string Name { get; }
        }

        private sealed class NativeRemovePackageFixture
        {
            internal NativeRemovePackageFixture(
                string packageName,
                PackageManagerSubmoduleInfo submodule = null,
                bool isDirectInstalledPackagePath = false)
            {
                PackageName = packageName;
                Version = new NativeRemoveVersionFixture(
                    submodule,
                    isDirectInstalledPackagePath);
            }

            internal string PackageName { get; }
            internal NativeRemoveVersionFixture Version { get; }
        }

        private sealed class NativeRemoveVersionFixture
        {
            internal NativeRemoveVersionFixture(
                PackageManagerSubmoduleInfo submodule,
                bool isDirectInstalledPackagePath)
            {
                Submodule = submodule;
                IsDirectInstalledPackagePath = isDirectInstalledPackagePath;
            }

            internal PackageManagerSubmoduleInfo Submodule { get; }
            internal bool IsDirectInstalledPackagePath { get; }
        }

        private sealed class SingleUseNativeRemoveSelection : IEnumerable
        {
            private readonly IEnumerable values;

            internal SingleUseNativeRemoveSelection(IEnumerable values)
            {
                this.values = values;
            }

            internal int EnumerationCount { get; private set; }

            public IEnumerator GetEnumerator()
            {
                EnumerationCount++;
                if (EnumerationCount > 1)
                {
                    throw new InvalidOperationException(
                        "The selection was enumerated more than once.");
                }

                return values.GetEnumerator();
            }
        }

        private sealed class NativeActionsHostWindow : EditorWindow
        {
        }
    }
}
