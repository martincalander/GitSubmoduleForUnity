using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using NUnit.Framework;
using UnityEditor;

namespace MartinCalander.GitSubmoduleManager.Editor.Tests
{
    [Parallelizable(ParallelScope.None)]
    public sealed class PackageDependencyInstallWorkflowTests
    {
        private const string RootInspectedCommit =
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        private const string DependencyInspectedCommit =
            "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

        [Test]
        public void PreflightRunner_IsSingleFlightAndCompletesFromManualTick()
        {
            var facade = new FakeResolutionFacade();
            using var runner = new PackageDependencyPreflightRunner(
                new PackageDependencyResolutionService(facade));
            PackageDependencyPreflightCompletion observed = null;
            PackageDependencyInstallRequest request = Request();

            Assert.That(
                runner.TryStart(
                    request,
                    completion => observed = completion,
                    out string startError),
                Is.True,
                startError);
            Assert.That(runner.IsBusy, Is.True);
            Assert.That(
                runner.TryStart(request, null, out string duplicateError),
                Is.False);
            Assert.That(duplicateError, Does.Contain("already running"));

            Assert.That(runner.Tick(), Is.True);
            Assert.That(runner.IsBusy, Is.False);
            Assert.That(observed, Is.Not.Null);
            Assert.That(observed.Success, Is.True);
            Assert.That(observed.Plan.IsComplete, Is.True);
            Assert.That(observed.Plan.Results, Is.Empty);
        }

        [Test]
        public void Prompt_ListsEverySourceAndSafePreferenceSkipsOnlyConfirmation()
        {
            PackageDependencyResolutionPlan plan = Plan(
                ResolvedGitHub(
                    "com.example.github",
                    "1.0.0",
                    "com.example.root",
                    "org/github"),
                ResolvedRegistry(
                    "com.unity.registry",
                    "2.0.0",
                    "com.example.root",
                    true,
                    "Unity Registry"));
            PackageDependencyPromptContent content =
                PackageDependencyInstallPrompt.BuildContent(Request(), plan);

            Assert.That(content.IsBlocking, Is.False);
            Assert.That(content.Message, Does.Contain("com.example.github"));
            Assert.That(content.Message, Does.Contain("GitHub (org/github)"));
            Assert.That(content.Message, Does.Contain("com.unity.registry"));
            Assert.That(content.Message, Does.Contain("Unity Registry"));
            Assert.That(content.Message, Does.Contain("remain transitive"));

            var prompted = new FakeDialog { ConfirmResult = true };
            Assert.That(
                PackageDependencyInstallPrompt.TryConfirm(
                    Request(),
                    plan,
                    false,
                    out string promptError,
                    prompted),
                Is.True,
                promptError);
            Assert.That(prompted.ConfirmCount, Is.EqualTo(1));

            var bypassed = new FakeDialog { ConfirmResult = false };
            Assert.That(
                PackageDependencyInstallPrompt.TryConfirm(
                    Request(),
                    plan,
                    true,
                    out string bypassError,
                    bypassed),
                Is.True,
                bypassError);
            Assert.That(bypassed.ConfirmCount, Is.Zero);
            Assert.That(bypassed.BlockingCount, Is.Zero);
        }

        [Test]
        public void Prompt_UnresolvedPlanBlocksEvenWhenPreferenceIsEnabled()
        {
            var unresolved = new PackageDependencyResolutionResult(
                new PackageDependencyRequirement(
                    "com.example.missing",
                    "1.0.0",
                    new[] { "com.example.root" }),
                PackageDependencyResolutionStatus.Unresolved,
                Array.Empty<PackageDependencyCandidate>(),
                "No safe source was found.");
            PackageDependencyResolutionPlan plan = Plan(unresolved);
            var dialog = new FakeDialog { ConfirmResult = true };

            Assert.That(
                PackageDependencyInstallPrompt.TryConfirm(
                    Request(),
                    plan,
                    true,
                    out string error,
                    dialog),
                Is.False);
            Assert.That(error, Does.Contain("unresolved or ambiguous"));
            Assert.That(dialog.ConfirmCount, Is.Zero);
            Assert.That(dialog.BlockingCount, Is.EqualTo(1));
            Assert.That(dialog.LastContent.Message,
                Does.Contain("com.example.missing"));
        }

        [Test]
        public void RecoveredCompletionDialog_IsSanitizedAndExplicitAboutReload()
        {
            var completion = new PackageDependencyInstallPipelineCompletion(
                true,
                false,
                "Installed from https://user:secret@github.com/org/root.git",
                "https://github.com/org/root.git",
                "main",
                "com.example.root",
                PackageManagerGitInstallMode.GitSubmodule);

            PackageDependencyInstallCompletionDialogContent content =
                PackageDependencyInstallPipeline
                    .BuildRecoveredCompletionDialogContent(completion);

            Assert.That(content, Is.Not.Null);
            Assert.That(content.Title, Is.EqualTo("Git Package Installed"));
            Assert.That(content.Message, Does.Contain("com.example.root"));
            Assert.That(content.Message, Does.Contain("reloaded scripts"));
            Assert.That(content.Message, Does.Not.Contain("secret"));
            Assert.That(content.Message, Does.Not.Contain("user:"));
            Assert.That(content.AcceptText, Is.EqualTo("OK"));
        }

        [Test]
        public void RecoveredCompletionDialog_UsesInjectedPresenter()
        {
            var completion = new PackageDependencyInstallPipelineCompletion(
                false,
                false,
                "Clone failed safely.",
                "https://github.com/org/root.git",
                "main",
                "com.example.root",
                PackageManagerGitInstallMode.GitSubmodule);
            var dialog = new FakeCompletionDialog();

            Assert.That(
                PackageDependencyInstallPipeline.TryPresentRecoveredCompletion(
                    completion,
                    dialog),
                Is.True);
            Assert.That(dialog.ShowCount, Is.EqualTo(1));
            Assert.That(dialog.LastContent.Title,
                Is.EqualTo("Git Package Install Failed"));
            Assert.That(dialog.LastContent.Message,
                Does.Contain("Clone failed safely."));
        }

        [TestCase(false, false, false)]
        [TestCase(false, true, false)]
        [TestCase(true, false, false)]
        [TestCase(true, true, true)]
        public void Pipeline_CoordinatorPollingRequiresPendingLifecycleWork(
            bool coordinatorIsBusy,
            bool coordinatorNeedsUpdate,
            bool expected)
        {
            Assert.That(
                PackageDependencyInstallPipeline
                    .ShouldSubscribeToCoordinatorUpdates(
                        coordinatorIsBusy,
                        coordinatorNeedsUpdate),
                Is.EqualTo(expected));
        }

        [Test]
        public void StepBuilder_OrdersGitHubLeavesFirstAndOmitsRegistries()
        {
            PackageDependencyResolutionPlan plan = Plan(
                ResolvedGitHub(
                    "com.example.a",
                    "1.0.0",
                    "com.example.root",
                    "org/a"),
                ResolvedRegistry(
                    "com.example.b",
                    "2.0.0",
                    "com.example.a",
                    false,
                    "Company Registry"),
                ResolvedGitHub(
                    "com.example.c",
                    "3.0.0",
                    "com.example.b",
                    "org/c"));

            Assert.That(
                PackageDependencyInstallCoordinatorCore.TryBuildSteps(
                    Request(
                        dependencies: new[]
                        {
                            new PackageManifestDependency(
                                "com.example.a",
                                "1.0.0")
                        }),
                    plan,
                    out IReadOnlyList<PackageDependencyInstallStep> steps,
                    out string error),
                Is.True,
                error);

            Assert.That(
                steps.Select(step => step.PackageName),
                Is.EqualTo(new[]
                {
                    "com.example.c",
                    "com.example.a",
                    "com.example.root"
                }));
            Assert.That(steps.Any(step =>
                step.PackageName == "com.example.b"), Is.False);
            Assert.That(
                steps.Take(2).All(step =>
                    step.PackageManifestMetaVerification ==
                        PackageManifestMetaVerification.Verified &&
                    step.PackageManifestMetaGuid ==
                        "0123456789abcdef0123456789abcdef" &&
                    step.InspectedCommit == DependencyInspectedCommit),
                Is.True);
            Assert.That(steps.Last().IsRoot, Is.True);
        }

        [Test]
        public void Coordinator_ForwardsExactVersionToPrimitiveStep()
        {
            const string expectedVersion = "1.2.3-preview.4+build.5";
            var executor = new FakeInstallExecutor();
            var coordinator = new PackageDependencyInstallCoordinatorCore(
                executor,
                new MemoryStateStore(),
                () => 100L);

            Assert.That(
                coordinator.TryStart(
                    Request(rootVersion: expectedVersion),
                    Plan(),
                    null,
                    out string error),
                Is.True,
                error);
            string operationId = coordinator.ActiveOperationId;
            Assert.That(coordinator.Tick(), Is.True);

            Assert.That(executor.Started, Has.Count.EqualTo(1));
            Assert.That(executor.StartedOperationIds,
                Is.EqualTo(new[] { operationId }));
            Assert.That(Guid.TryParseExact(operationId, "N", out _), Is.True);
            Assert.That(executor.Started[0].Version, Is.EqualTo(expectedVersion));
            Assert.That(
                executor.Started[0].DependencyFingerprint,
                Is.EqualTo(GitUtility.ComputePackageDependencyFingerprint(
                    Array.Empty<PackageManifestDependency>())));
        }

        [Test]
        public void Coordinator_PersistsVerifiedRootPackageManifestMetaEvidence()
        {
            const string expectedGuid =
                "0123456789abcdef0123456789abcdef";
            var store = new MemoryStateStore();
            var first = new PackageDependencyInstallCoordinatorCore(
                new FakeInstallExecutor(),
                store,
                () => 100L);

            Assert.That(
                first.TryStart(
                    Request(
                        packageManifestMetaVerification:
                            PackageManifestMetaVerification.Verified,
                        packageManifestMetaGuid: expectedGuid),
                    Plan(),
                    null,
                    out string startError),
                Is.True,
                startError);

            var resumed = new PackageDependencyInstallCoordinatorCore(
                new FakeInstallExecutor(),
                store,
                () => 200L);

            Assert.That(resumed.IsBusy, Is.True);
            Assert.That(
                resumed.ActiveStep.PackageManifestMetaVerification,
                Is.EqualTo(PackageManifestMetaVerification.Verified));
            Assert.That(
                resumed.ActiveStep.PackageManifestMetaGuid,
                Is.EqualTo(expectedGuid));
            Assert.That(
                resumed.ActiveStep.InspectedCommit,
                Is.EqualTo(RootInspectedCommit));
        }

        [TestCase((int)PackageManagerGitInstallMode.GitSubmodule)]
        [TestCase((int)PackageManagerGitInstallMode.ReadOnlyPackage)]
        public void Coordinator_ReloadRejectsDowngradedCommitEvidence(int modeValue)
        {
            var mode = (PackageManagerGitInstallMode)modeValue;
            var store = new MemoryStateStore();
            var first = new PackageDependencyInstallCoordinatorCore(
                new FakeInstallExecutor(),
                store,
                () => 100L);
            Assert.That(
                first.TryStart(
                    Request(mode),
                    Plan(),
                    null,
                    out string startError),
                Is.True,
                startError);
            Assert.That(store.Active,
                Does.Contain("\"RootInspectedCommit\":\"" +
                             RootInspectedCommit + "\""));

            store.Active = store.Active.Replace(
                "\"RootInspectedCommit\":\"" + RootInspectedCommit + "\"",
                "\"RootInspectedCommit\":\"\"");
            var resumedExecutor = new FakeInstallExecutor();
            var resumed = new PackageDependencyInstallCoordinatorCore(
                resumedExecutor,
                store,
                () => 200L);

            Assert.That(resumed.IsRecoveryBlocked, Is.True);
            Assert.That(resumedExecutor.Started, Is.Empty);
            Assert.That(resumed.ActiveRecoveryMessage,
                Does.Contain("persisted dependency install record is damaged"));
        }

        [Test]
        public void Coordinator_RejectsRegisteredReadOnlyPackageWithChangedMetaGuid()
        {
            const string expectedGuid =
                "0123456789abcdef0123456789abcdef";
            string resolvedPath = Path.Combine(
                Path.GetTempPath(),
                "GitSubmoduleManager-MetaMismatch-" +
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(resolvedPath);
            try
            {
                File.WriteAllText(
                    Path.Combine(resolvedPath, "package.json.meta"),
                    "fileFormatVersion: 2\n" +
                    "guid: fedcba9876543210fedcba9876543210\n");
                var executor = new FakeInstallExecutor();
                executor.Registered.Add(InstalledReadOnly(
                    "com.example.root",
                    "1.0.0",
                    resolvedPath: resolvedPath,
                    packageManifestMetaGuid:
                        "fedcba9876543210fedcba9876543210"));
                PackageDependencyInstallCompletion observed = null;
                var coordinator = new PackageDependencyInstallCoordinatorCore(
                    executor,
                    new MemoryStateStore(),
                    () => 100L,
                    completion => observed = completion);

                Assert.That(
                    coordinator.TryStart(
                        Request(
                            PackageManagerGitInstallMode.ReadOnlyPackage,
                            packageManifestMetaVerification:
                                PackageManifestMetaVerification.Verified,
                            packageManifestMetaGuid: expectedGuid),
                        Plan(),
                        null,
                        out string error),
                    Is.True,
                    error);
                Assert.That(coordinator.Tick(), Is.True);

                Assert.That(coordinator.IsBusy, Is.False);
                Assert.That(executor.Started, Is.Empty);
                Assert.That(observed, Is.Not.Null);
                Assert.That(observed.Success, Is.False);
                Assert.That(observed.Message, Does.Contain("package.json.meta GUID"));
            }
            finally
            {
                Directory.Delete(resolvedPath, true);
            }
        }

        [Test]
        public void StepBuilder_RejectsGitHubCandidateWithoutExplicitBranch()
        {
            PackageDependencyResolutionPlan plan = Plan(
                ResolvedGitHub(
                    "com.example.dependency",
                    "1.0.0",
                    "com.example.root",
                    "org/dependency",
                    string.Empty));

            Assert.That(
                PackageDependencyInstallCoordinatorCore.TryBuildSteps(
                    Request(
                        dependencies: new[]
                        {
                            new PackageManifestDependency(
                                "com.example.dependency",
                                "1.0.0")
                        }),
                    plan,
                    out _,
                    out string error),
                Is.False);
            Assert.That(error, Does.Contain("explicit valid repository branch"));
        }

        [Test]
        public void StepBuilder_RejectsGitHubCandidateWithoutVerifiedMetaEvidence()
        {
            const string packageName = "com.example.dependency";
            var plan = Plan(new PackageDependencyResolutionResult(
                new PackageDependencyRequirement(
                    packageName,
                    "1.0.0",
                    new[] { "com.example.root" }),
                PackageDependencyResolutionStatus.Resolved,
                new[]
                {
                    new PackageDependencyCandidate(
                        PackageDependencyCandidateSource.GitHub,
                        packageName,
                        "1.0.0",
                        "org/dependency",
                        "org",
                        "dependency",
                        "https://github.com/org/dependency.git",
                        "main",
                        dependencyFingerprint:
                            GitUtility.ComputePackageDependencyFingerprint(
                                Array.Empty<PackageManifestDependency>()))
                },
                string.Empty));

            Assert.That(
                PackageDependencyInstallCoordinatorCore.TryBuildSteps(
                    Request(
                        dependencies: new[]
                        {
                            new PackageManifestDependency(packageName, "1.0.0")
                        }),
                    plan,
                    out _,
                    out string error),
                Is.False);
            Assert.That(error, Does.Contain("package.json.meta GUID"));
        }

        [Test]
        public void StepBuilder_RejectsOrphanedPlanBeforeMutation()
        {
            PackageDependencyResolutionPlan plan = Plan(
                ResolvedGitHub(
                    "com.example.orphan",
                    "1.0.0",
                    "com.example.unknown-parent",
                    "org/orphan"));

            Assert.That(
                PackageDependencyInstallCoordinatorCore.TryBuildSteps(
                    Request(),
                    plan,
                    out _,
                    out string error),
                Is.False);
            Assert.That(error, Does.Contain("orphaned from the root install graph"));
        }

        [Test]
        public void StepBuilder_RejectsRootEdgeMissingFromRequest()
        {
            PackageDependencyResolutionPlan plan = Plan(
                ResolvedGitHub(
                    "com.example.injected",
                    "1.0.0",
                    "com.example.root",
                    "org/injected"));

            Assert.That(
                PackageDependencyInstallCoordinatorCore.TryBuildSteps(
                    Request(),
                    plan,
                    out _,
                    out string error),
                Is.False);
            Assert.That(error, Does.Contain("does not match the root install request"));
        }

        [Test]
        public void Coordinator_ReloadResumesFromRegisteredStateWithoutDuplicateStart()
        {
            var store = new MemoryStateStore();
            var firstExecutor = new FakeInstallExecutor();
            var first = new PackageDependencyInstallCoordinatorCore(
                firstExecutor,
                store,
                () => 100L);
            Assert.That(
                first.TryStart(
                    Request(),
                    Plan(),
                    null,
                    out string startError),
                Is.True,
                startError);

            Assert.That(first.Tick(), Is.True);
            Assert.That(firstExecutor.Started.Select(step => step.PackageName),
                Is.EqualTo(new[] { "com.example.root" }));
            Assert.That(store.Active, Is.Not.Empty);

            var resumedExecutor = new FakeInstallExecutor();
            resumedExecutor.Registered.Add(InstalledEmbedded(
                "com.example.root",
                "1.0.0"));
            PackageDependencyInstallCompletion observed = null;
            var resumed = new PackageDependencyInstallCoordinatorCore(
                resumedExecutor,
                store,
                () => 200L,
                completion => observed = completion);

            Assert.That(resumed.IsBusy, Is.True);
            Assert.That(resumed.ActiveOperationId, Has.Length.EqualTo(32));
            Assert.That(
                resumed.ActiveRootPackageName,
                Is.EqualTo("com.example.root"));
            Assert.That(
                resumed.ActiveRepositoryUrl,
                Is.EqualTo("https://github.com/org/root.git"));
            Assert.That(resumed.ActiveRevision, Is.EqualTo("main"));
            Assert.That(
                resumed.ActiveInstallMode,
                Is.EqualTo(PackageManagerGitInstallMode.GitSubmodule));
            Assert.That(resumed.ActiveStepIndex, Is.Zero);
            Assert.That(resumed.ActiveStepCount, Is.EqualTo(1));
            Assert.That(
                resumed.ActiveStepPackageName,
                Is.EqualTo("com.example.root"));
            Assert.That(resumed.ActiveStep.PackageName,
                Is.EqualTo("com.example.root"));
            Assert.That(resumed.Tick(), Is.True);
            Assert.That(resumed.IsBusy, Is.False);
            Assert.That(resumedExecutor.Started, Is.Empty);
            Assert.That(observed, Is.Not.Null);
            Assert.That(observed.Success, Is.True);
            Assert.That(
                resumed.TryGetLastCompletion(out var firstPeek),
                Is.True);
            Assert.That(firstPeek.RootPackageName,
                Is.EqualTo("com.example.root"));
            Assert.That(
                resumed.TryGetLastCompletion(out var secondPeek),
                Is.True,
                "Inspecting a recovered result must not discard it before a " +
                "presentation owner accepts it.");
            Assert.That(secondPeek.RootRepositoryUrl,
                Is.EqualTo("https://github.com/org/root.git"));
            Assert.That(
                resumed.TryConsumeLastCompletion(out var retained),
                Is.True);
            Assert.That(retained.Success, Is.True);
            Assert.That(retained.RootPackageName, Is.EqualTo("com.example.root"));
            Assert.That(
                retained.RootRepositoryUrl,
                Is.EqualTo("https://github.com/org/root.git"));
            Assert.That(retained.RootRevision, Is.EqualTo("main"));
            Assert.That(
                resumed.TryConsumeLastCompletion(out _),
                Is.False);
        }

        [Test]
        public void Coordinator_ReloadDoesNotAcceptMissingSubmoduleCommitEvidence()
        {
            long now = 100L;
            var store = new MemoryStateStore();
            var firstExecutor = new FakeInstallExecutor();
            var first = new PackageDependencyInstallCoordinatorCore(
                firstExecutor,
                store,
                () => now);
            Assert.That(
                first.TryStart(Request(), Plan(), null, out string startError),
                Is.True,
                startError);
            Assert.That(first.Tick(), Is.True);

            var resumedExecutor = new FakeInstallExecutor();
            resumedExecutor.Registered.Add(InstalledEmbedded(
                "com.example.root",
                "1.0.0",
                resolvedCommit: string.Empty));
            PackageDependencyInstallCompletion observed = null;
            var resumed = new PackageDependencyInstallCoordinatorCore(
                resumedExecutor,
                store,
                () => now,
                completion => observed = completion);

            Assert.That(resumed.Tick(), Is.False);
            Assert.That(resumed.IsBusy, Is.True);
            Assert.That(observed, Is.Null);
            Assert.That(resumedExecutor.Started, Is.Empty);

            now += TimeSpan.FromMinutes(10d).Ticks;
            Assert.That(resumed.Tick(), Is.True);
            Assert.That(resumed.IsBusy, Is.False);
            Assert.That(observed, Is.Not.Null);
            Assert.That(observed.Success, Is.False);
            Assert.That(observed.Message, Does.Contain("could not be verified"));
            Assert.That(resumedExecutor.Started, Is.Empty);
        }

        [Test]
        public void Coordinator_ReloadRejectsMismatchedSubmoduleCommit()
        {
            var store = new MemoryStateStore();
            var firstExecutor = new FakeInstallExecutor();
            var first = new PackageDependencyInstallCoordinatorCore(
                firstExecutor,
                store,
                () => 100L);
            Assert.That(
                first.TryStart(Request(), Plan(), null, out string startError),
                Is.True,
                startError);
            Assert.That(first.Tick(), Is.True);

            var resumedExecutor = new FakeInstallExecutor();
            resumedExecutor.Registered.Add(InstalledEmbedded(
                "com.example.root",
                "1.0.0",
                resolvedCommit:
                    "cccccccccccccccccccccccccccccccccccccccc"));
            PackageDependencyInstallCompletion observed = null;
            var resumed = new PackageDependencyInstallCoordinatorCore(
                resumedExecutor,
                store,
                () => 200L,
                completion => observed = completion);

            Assert.That(resumed.Tick(), Is.True);
            Assert.That(resumed.IsBusy, Is.False);
            Assert.That(resumedExecutor.Started, Is.Empty);
            Assert.That(observed, Is.Not.Null);
            Assert.That(observed.Success, Is.False);
            Assert.That(observed.Message, Does.Contain("unexpected source"));
        }

        [Test]
        public void Coordinator_FreshProofRejectsCachedCommitAfterHeadAndGitlinkMove()
        {
            using var fixture = new SubmoduleCommitFixture();
            long gitModulesWriteTicks = fixture.GitModulesWriteTicks;
            fixture.SetState(fixture.SecondCommit);
            Assert.That(
                fixture.GitModulesWriteTicks,
                Is.EqualTo(gitModulesWriteTicks),
                "The regression must not rely on .gitmodules invalidation.");

            using var verifier = new PackageDependencySubmoduleCommitVerifier(
                new ProcessCommandRunner(),
                fixture.ProjectRoot);
            var executor = new FakeInstallExecutor
            {
                SubmoduleCommitVerifier = verifier
            };
            // This is the stale PackageManagerSubmoduleSnapshot identity: A is
            // still cached even though both current Git identities are now B.
            executor.Registered.Add(InstalledEmbedded(
                "com.example.root",
                "1.0.0",
                resolvedCommit: fixture.FirstCommit));
            PackageDependencyInstallCompletion observed = null;
            var coordinator = new PackageDependencyInstallCoordinatorCore(
                executor,
                new MemoryStateStore(),
                () => 100L,
                completion => observed = completion);

            Assert.That(
                coordinator.TryStart(
                    Request(inspectedCommit: fixture.FirstCommit),
                    Plan(),
                    null,
                    out string startError),
                Is.True,
                startError);

            Assert.That(coordinator.Tick(), Is.False,
                "A fresh local Git proof is asynchronous and must be pending " +
                "instead of accepting cached commit A.");
            WaitForCoordinatorTerminal(coordinator);

            Assert.That(coordinator.IsBusy, Is.False);
            Assert.That(executor.Started, Is.Empty,
                "The already-present package must not trigger another mutation.");
            Assert.That(observed, Is.Not.Null);
            Assert.That(observed.Success, Is.False);
            Assert.That(observed.Message, Does.Contain("fresh parent gitlink"));
        }

        [Test]
        public void Coordinator_MutationBusyRetiresCompletedCommitProof()
        {
            using var fixture = new SubmoduleCommitFixture();
            using var verifier = new PackageDependencySubmoduleCommitVerifier(
                new ProcessCommandRunner(),
                fixture.ProjectRoot);
            var executor = new FakeInstallExecutor
            {
                SubmoduleCommitVerifier = verifier
            };
            executor.Registered.Add(InstalledEmbedded(
                "com.example.root",
                "1.0.0",
                resolvedCommit: fixture.FirstCommit));
            PackageDependencyInstallCompletion observed = null;
            var coordinator = new PackageDependencyInstallCoordinatorCore(
                executor,
                new MemoryStateStore(),
                () => 100L,
                completion => observed = completion);
            Assert.That(
                coordinator.TryStart(
                    Request(inspectedCommit: fixture.FirstCommit),
                    Plan(),
                    null,
                    out string startError),
                Is.True,
                startError);
            Assert.That(coordinator.Tick(), Is.False);

            Assert.That(
                WaitForCommitVerification(
                    verifier,
                    executor.LastVerificationScopeId,
                    executor.LastVerificationOperationId,
                    executor.LastVerificationStepIndex,
                    executor.LastVerificationStep,
                    out string firstError),
                Is.EqualTo(
                    PackageDependencySubmoduleCommitVerificationStatus.Expected),
                firstError);

            executor.MutationBusy = true;
            Assert.That(coordinator.Tick(), Is.False,
                "An intervening mutation must retire the completed proof.");
            fixture.SetState(fixture.SecondCommit);
            executor.MutationBusy = false;
            WaitForCoordinatorTerminal(coordinator);

            Assert.That(executor.Started, Is.Empty);
            Assert.That(observed, Is.Not.Null);
            Assert.That(observed.Success, Is.False,
                "The completed A proof must not be reused after mutation busy " +
                "and the staged/worktree move to B.");
        }

        [TestCase(false)]
        [TestCase(true)]
        public void Coordinator_FreshProofRejectsRedirectedGitModulesRegistration(
            bool stagedOnly)
        {
            using var fixture = new SubmoduleCommitFixture();
            if (stagedOnly)
            {
                fixture.RedirectStagedRegistrationOnly(
                    "https://github.com/other/redirected.git");
            }
            else
            {
                fixture.RedirectRegistration(
                    "https://github.com/other/redirected.git");
            }
            using var verifier = new PackageDependencySubmoduleCommitVerifier(
                new ProcessCommandRunner(),
                fixture.ProjectRoot);
            var executor = new FakeInstallExecutor
            {
                SubmoduleCommitVerifier = verifier
            };
            // Presentation still carries the prior validated registration.
            executor.Registered.Add(InstalledEmbedded(
                "com.example.root",
                "1.0.0",
                repositoryUrl: "https://github.com/org/root.git",
                resolvedCommit: fixture.FirstCommit));
            PackageDependencyInstallCompletion observed = null;
            var coordinator = new PackageDependencyInstallCoordinatorCore(
                executor,
                new MemoryStateStore(),
                () => 100L,
                completion => observed = completion);
            Assert.That(
                coordinator.TryStart(
                    Request(inspectedCommit: fixture.FirstCommit),
                    Plan(),
                    null,
                    out string startError),
                Is.True,
                startError);

            Assert.That(coordinator.Tick(), Is.False);
            WaitForCoordinatorTerminal(coordinator);

            Assert.That(executor.Started, Is.Empty);
            Assert.That(observed, Is.Not.Null);
            Assert.That(observed.Success, Is.False);
            Assert.That(observed.Message, Does.Contain(".gitmodules"));
        }

        [TestCase((int)
            PackageDependencySubmoduleCommitVerificationReadPoint
                .AfterFirstIndex)]
        [TestCase((int)
            PackageDependencySubmoduleCommitVerificationReadPoint
                .BeforeFinalIndex)]
        [TestCase((int)
            PackageDependencySubmoduleCommitVerificationReadPoint
                .BeforeTerminalOrigin)]
        public void SubmoduleCommitVerifier_RejectsSwapAcrossFreshReadSeams(
            int readPointValue)
        {
            using var fixture = new SubmoduleCommitFixture();
            int swapCount = 0;
            var swapReadPoint =
                (PackageDependencySubmoduleCommitVerificationReadPoint)
                readPointValue;
            using var verifier = new PackageDependencySubmoduleCommitVerifier(
                new ProcessCommandRunner(),
                fixture.ProjectRoot,
                (point, _) =>
                {
                    if (point == swapReadPoint &&
                        Interlocked.Exchange(ref swapCount, 1) == 0)
                    {
                        fixture.SetState(fixture.SecondCommit);
                    }
                });
            PackageDependencyInstallStep step = SubmoduleStep(
                fixture.FirstCommit);

            PackageDependencySubmoduleCommitVerificationStatus status =
                WaitForCommitVerification(
                    verifier,
                    Guid.NewGuid().ToString("N"),
                    Guid.NewGuid().ToString("N"),
                    0,
                    step,
                    out string error);

            Assert.That(swapCount, Is.EqualTo(1));
            Assert.That(
                status,
                Is.EqualTo(
                    PackageDependencySubmoduleCommitVerificationStatus
                        .Unexpected),
                error);
            Assert.That(error, Does.Contain("commit"));
        }

        [Test]
        public void SubmoduleCommitVerifier_RejectsOriginOnlySwapBeforeFinalIndex()
        {
            using var fixture = new SubmoduleCommitFixture();
            int swapCount = 0;
            using var verifier = new PackageDependencySubmoduleCommitVerifier(
                new ProcessCommandRunner(),
                fixture.ProjectRoot,
                (point, _) =>
                {
                    if (point ==
                            PackageDependencySubmoduleCommitVerificationReadPoint
                                .BeforeFinalIndex &&
                        Interlocked.Exchange(ref swapCount, 1) == 0)
                    {
                        fixture.RedirectOrigin(
                            "https://github.com/other/redirected.git");
                    }
                });

            PackageDependencySubmoduleCommitVerificationStatus status =
                WaitForCommitVerification(
                    verifier,
                    Guid.NewGuid().ToString("N"),
                    Guid.NewGuid().ToString("N"),
                    0,
                    SubmoduleStep(fixture.FirstCommit),
                    out string error);

            Assert.That(swapCount, Is.EqualTo(1));
            Assert.That(
                status,
                Is.EqualTo(
                    PackageDependencySubmoduleCommitVerificationStatus
                        .Unexpected),
                error);
            Assert.That(error, Does.Contain("origin"));
        }

        [TestCase("different-bytes")]
        [TestCase("oversized")]
        [TestCase("invalid-utf8")]
        public void SubmoduleCommitVerifier_RejectsLateWorktreeGitModulesReplacement(
            string replacementKind)
        {
            using var fixture = new SubmoduleCommitFixture();
            int swapCount = 0;
            using var verifier = new PackageDependencySubmoduleCommitVerifier(
                new ProcessCommandRunner(),
                fixture.ProjectRoot,
                (point, _) =>
                {
                    if (point !=
                            PackageDependencySubmoduleCommitVerificationReadPoint
                                .BeforeFinalIndex ||
                        Interlocked.Exchange(ref swapCount, 1) != 0)
                    {
                        return;
                    }

                    switch (replacementKind)
                    {
                        case "different-bytes":
                            fixture.AppendGitModulesComment(
                                "# same registration, different bytes\n");
                            break;
                        case "oversized":
                            fixture.WriteGitModulesBytes(
                                Enumerable.Repeat((byte)'#', (128 * 1024) + 1)
                                    .ToArray());
                            break;
                        case "invalid-utf8":
                            fixture.WriteGitModulesBytes(new byte[]
                            {
                                0x5b, 0x73, 0x75, 0x62, 0x6d, 0x6f, 0x64,
                                0x75, 0x6c, 0x65, 0x20, 0xc3, 0x28
                            });
                            break;
                    }
                });

            PackageDependencySubmoduleCommitVerificationStatus status =
                WaitForCommitVerification(
                    verifier,
                    Guid.NewGuid().ToString("N"),
                    Guid.NewGuid().ToString("N"),
                    0,
                    SubmoduleStep(fixture.FirstCommit),
                    out string error);

            Assert.That(swapCount, Is.EqualTo(1));
            Assert.That(
                status,
                Is.EqualTo(
                    PackageDependencySubmoduleCommitVerificationStatus
                        .Unverified),
                error);
            Assert.That(error, Does.Contain(".gitmodules"));
        }

        [Test]
        public void SubmoduleCommitVerifier_RejectsLateMatchingGitModulesSymlink()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                Assert.Ignore("Creating an unprivileged symbolic link is not portable on Windows test hosts.");

            using var fixture = new SubmoduleCommitFixture();
            int swapCount = 0;
            using var verifier = new PackageDependencySubmoduleCommitVerifier(
                new ProcessCommandRunner(),
                fixture.ProjectRoot,
                (point, _) =>
                {
                    if (point ==
                            PackageDependencySubmoduleCommitVerificationReadPoint
                                .BeforeFinalIndex &&
                        Interlocked.Exchange(ref swapCount, 1) == 0)
                    {
                        fixture.ReplaceGitModulesWithMatchingSymlink();
                    }
                });

            PackageDependencySubmoduleCommitVerificationStatus status =
                WaitForCommitVerification(
                    verifier,
                    Guid.NewGuid().ToString("N"),
                    Guid.NewGuid().ToString("N"),
                    0,
                    SubmoduleStep(fixture.FirstCommit),
                    out string error);

            Assert.That(swapCount, Is.EqualTo(1));
            Assert.That(
                status,
                Is.EqualTo(
                    PackageDependencySubmoduleCommitVerificationStatus
                        .Unverified),
                error);
            Assert.That(error, Does.Contain("non-symbolic-link"));
            Assert.That(fixture.MatchingSymlinkTargetContentsAreUnchanged, Is.True);
        }

        [Test]
        public void SubmoduleCommitVerifier_DoesNotReusePriorRequestProof()
        {
            using var fixture = new SubmoduleCommitFixture();
            using var verifier = new PackageDependencySubmoduleCommitVerifier(
                new ProcessCommandRunner(),
                fixture.ProjectRoot);
            PackageDependencyInstallStep step = SubmoduleStep(
                fixture.FirstCommit);
            string firstScope = Guid.NewGuid().ToString("N");
            string firstOperation = Guid.NewGuid().ToString("N");

            Assert.That(
                WaitForCommitVerification(
                    verifier,
                    firstScope,
                    firstOperation,
                    0,
                    step,
                    out string firstError),
                Is.EqualTo(
                    PackageDependencySubmoduleCommitVerificationStatus.Expected),
                firstError);

            fixture.SetState(fixture.SecondCommit);
            string secondScope = Guid.NewGuid().ToString("N");
            string secondOperation = Guid.NewGuid().ToString("N");
            PackageDependencySubmoduleCommitVerificationStatus initialStatus =
                verifier.GetOrStart(
                    secondScope,
                    secondOperation,
                    0,
                    step,
                    out string initialError);
            Assert.That(
                initialStatus,
                Is.EqualTo(
                    PackageDependencySubmoduleCommitVerificationStatus.Pending),
                "A completed proof from another runtime/request scope must not " +
                "be returned for the current operation. " + initialError);

            Assert.That(
                WaitForCommitVerification(
                    verifier,
                    secondScope,
                    secondOperation,
                    0,
                    step,
                    out string secondError),
                Is.EqualTo(
                    PackageDependencySubmoduleCommitVerificationStatus.Unexpected),
                secondError);
        }

        [Test]
        public void Coordinator_ReloadRejectsMismatchedPersistedVersionWithoutDuplicateStart()
        {
            const string expectedVersion = "1.2.3-preview.4+build.5";
            var store = new MemoryStateStore();
            var firstExecutor = new FakeInstallExecutor();
            var first = new PackageDependencyInstallCoordinatorCore(
                firstExecutor,
                store,
                () => 100L);
            Assert.That(
                first.TryStart(
                    Request(rootVersion: expectedVersion),
                    Plan(),
                    null,
                    out string startError),
                Is.True,
                startError);
            Assert.That(first.Tick(), Is.True);
            Assert.That(firstExecutor.Started, Has.Count.EqualTo(1));
            Assert.That(
                firstExecutor.Started[0].Version,
                Is.EqualTo(expectedVersion));

            var resumedExecutor = new FakeInstallExecutor();
            resumedExecutor.Registered.Add(InstalledEmbedded(
                "com.example.root",
                "1.2.3-preview.4+build.6"));
            PackageDependencyInstallCompletion observed = null;
            var resumed = new PackageDependencyInstallCoordinatorCore(
                resumedExecutor,
                store,
                () => 200L,
                completion => observed = completion);

            Assert.That(resumed.IsBusy, Is.True);
            Assert.That(resumed.ActiveStep.Version, Is.EqualTo(expectedVersion));
            Assert.That(
                GitUtility.IsValidPackageDependencyFingerprint(
                    resumed.ActiveStep.DependencyFingerprint),
                Is.True);
            Assert.That(resumed.Tick(), Is.True);
            Assert.That(resumed.IsBusy, Is.False);
            Assert.That(resumedExecutor.Started, Is.Empty);
            Assert.That(observed, Is.Not.Null);
            Assert.That(observed.Success, Is.False);
            Assert.That(observed.Message, Does.Contain("unexpected source, version"));
        }

        [Test]
        public void Coordinator_ReloadDoesNotAcceptMissingDependencyFingerprint()
        {
            long now = 100L;
            var store = new MemoryStateStore();
            var first = new PackageDependencyInstallCoordinatorCore(
                new FakeInstallExecutor(),
                store,
                () => now);
            Assert.That(
                first.TryStart(
                    Request(PackageManagerGitInstallMode.ReadOnlyPackage),
                    Plan(),
                    null,
                    out string startError),
                Is.True,
                startError);
            Assert.That(first.Tick(), Is.True);

            var resumedExecutor = new FakeInstallExecutor();
            resumedExecutor.Registered.Add(InstalledReadOnly(
                "com.example.root",
                "1.0.0",
                dependencyFingerprint: string.Empty));
            PackageDependencyInstallCompletion observed = null;
            var resumed = new PackageDependencyInstallCoordinatorCore(
                resumedExecutor,
                store,
                () => now,
                completion => observed = completion);

            Assert.That(resumed.Tick(), Is.False,
                "A registered package without a verified manifest fingerprint " +
                "must not complete a reloaded operation.");
            Assert.That(resumed.IsBusy, Is.True);
            Assert.That(observed, Is.Null);
            Assert.That(resumedExecutor.Started, Is.Empty);

            now += TimeSpan.FromMinutes(10d).Ticks;
            Assert.That(resumed.Tick(), Is.True);
            Assert.That(resumed.IsBusy, Is.False);
            Assert.That(observed, Is.Not.Null);
            Assert.That(observed.Success, Is.False);
            Assert.That(observed.Message, Does.Contain("could not be verified"));
            Assert.That(resumedExecutor.Started, Is.Empty,
                "An attempted primitive must never be reissued after reload.");
        }

        [Test]
        public void Coordinator_ReloadVerifiesFingerprintFromInstalledManifest()
        {
            string packageRoot = Path.Combine(
                Path.GetTempPath(),
                "GitSubmoduleManager-Coordinator-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(packageRoot);
                File.WriteAllText(
                    Path.Combine(packageRoot, "package.json"),
                    "{\"name\":\"com.example.root\",\"version\":\"1.0.0\"," +
                    "\"dependencies\":{}}");
                File.WriteAllText(
                    Path.Combine(packageRoot, "package.json.meta"),
                    "fileFormatVersion: 2\n" +
                    "guid: 0123456789abcdef0123456789abcdef\n" +
                    "PackageManifestImporter:\n" +
                    "  externalObjects: {}\n");

                var store = new MemoryStateStore();
                var first = new PackageDependencyInstallCoordinatorCore(
                    new FakeInstallExecutor(),
                    store,
                    () => 100L);
                Assert.That(
                    first.TryStart(
                        Request(PackageManagerGitInstallMode.ReadOnlyPackage),
                        Plan(),
                        null,
                        out string startError),
                    Is.True,
                    startError);
                Assert.That(first.Tick(), Is.True);

                var resumedExecutor = new FakeInstallExecutor();
                resumedExecutor.Registered.Add(
                    InstalledReadOnly(
                        "com.example.root",
                        "1.0.0",
                        resolvedPath: packageRoot));
                PackageDependencyInstallCompletion observed = null;
                var resumed = new PackageDependencyInstallCoordinatorCore(
                    resumedExecutor,
                    store,
                    () => 200L,
                    completion => observed = completion);

                Assert.That(resumed.Tick(), Is.True);
                Assert.That(resumed.IsBusy, Is.False);
                Assert.That(resumedExecutor.Started, Is.Empty);
                Assert.That(observed, Is.Not.Null);
                Assert.That(observed.Success, Is.True);
            }
            finally
            {
                if (Directory.Exists(packageRoot))
                    Directory.Delete(packageRoot, true);
            }
        }

        [Test]
        public void Coordinator_RejectsChangedDependencyFingerprint()
        {
            var executor = new FakeInstallExecutor();
            executor.Registered.Add(InstalledReadOnly(
                "com.example.root",
                "1.0.0",
                dependencyFingerprint:
                    GitUtility.ComputePackageDependencyFingerprint(new[]
                    {
                        new PackageManifestDependency(
                            "com.example.changed",
                            "9.9.9")
                    })));
            PackageDependencyInstallCompletion observed = null;
            var coordinator = new PackageDependencyInstallCoordinatorCore(
                executor,
                new MemoryStateStore(),
                () => 100L,
                completion => observed = completion);
            Assert.That(
                coordinator.TryStart(
                    Request(PackageManagerGitInstallMode.ReadOnlyPackage),
                    Plan(),
                    null,
                    out string error),
                Is.True,
                error);

            Assert.That(coordinator.Tick(), Is.True);
            Assert.That(coordinator.IsBusy, Is.False);
            Assert.That(executor.Started, Is.Empty);
            Assert.That(observed, Is.Not.Null);
            Assert.That(observed.Success, Is.False);
            Assert.That(observed.Message, Does.Contain("unexpected source, version"));
        }

        [Test]
        public void ReadOnlyPrimitive_InvalidCoordinatedStateRetainsTerminalFailure()
        {
            string operationId = Guid.NewGuid().ToString("N");
            Assert.That(
                PackageManifestGitDependencyStore.TryBuildGitSpec(
                    "https://github.com/org/root.git",
                    "main",
                    out string spec,
                    out string specError),
                Is.True,
                specError);
            string json =
                "{\"Stage\":\"add\",\"RepositoryUrl\":\"https://github.com/org/root.git\"," +
                "\"Revision\":\"main\",\"ExpectedPackageName\":\"com.example.root\"," +
                "\"ExpectedVersion\":\"1.0.0\",\"ExpectedDependencyFingerprint\":\"\"," +
                "\"DependencyInstallOperationId\":\"" + operationId + "\"," +
                "\"Spec\":\"" + spec + "\",\"DirectPackageNamesBefore\":[]," +
                "\"StartedUtcTicks\":100}";

            Assert.That(
                PackageManagerReadOnlyGitInstallService
                    .TryBuildInvalidActiveStateCompletion(
                        json,
                        out ReadOnlyGitPackageInstallCompletion completion),
                Is.True);
            Assert.That(completion, Is.Not.Null);
            Assert.That(completion.Success, Is.False);
            Assert.That(completion.PackageName, Is.EqualTo("com.example.root"));
            Assert.That(
                completion.DependencyInstallOperationId,
                Is.EqualTo(operationId));
            Assert.That(completion.Message, Does.Contain("issued again"));
            Assert.That(completion.Message, Does.Contain("manifest.json"));
        }

        [Test]
        public void ReadOnlyPrimitive_PersistedVerifiedMetaEvidenceFailsClosed()
        {
            string operationId = Guid.NewGuid().ToString("N");
            string valid = BuildPrimitiveStateJson(
                "add",
                operationId,
                true,
                string.Empty,
                PackageManifestMetaVerification.Verified,
                "0123456789abcdef0123456789abcdef");
            string invalid = BuildPrimitiveStateJson(
                "add",
                operationId,
                true,
                string.Empty,
                PackageManifestMetaVerification.Verified,
                "00000000000000000000000000000000");
            string downgraded = BuildPrimitiveStateJson(
                "add",
                operationId,
                true,
                string.Empty);
            string missingCommit = BuildPrimitiveStateJson(
                "add",
                operationId,
                true,
                string.Empty,
                PackageManifestMetaVerification.Verified,
                "0123456789abcdef0123456789abcdef",
                string.Empty);

            Assert.That(
                PackageManagerReadOnlyGitInstallService
                    .TryBuildInvalidActiveStateCompletion(valid, out _),
                Is.False);
            Assert.That(
                PackageManagerReadOnlyGitInstallService
                    .TryBuildInvalidActiveStateCompletion(
                        invalid,
                        out ReadOnlyGitPackageInstallCompletion completion),
                Is.True);
            Assert.That(
                PackageManagerReadOnlyGitInstallService
                    .TryBuildInvalidActiveStateCompletion(downgraded, out _),
                Is.True);
            Assert.That(
                PackageManagerReadOnlyGitInstallService
                    .TryBuildInvalidActiveStateCompletion(missingCommit, out _),
                Is.True);
            Assert.That(completion, Is.Not.Null);
            Assert.That(completion.Success, Is.False);
            Assert.That(completion.Message, Does.Contain("damaged"));
        }

        [Test]
        public void ReadOnlyPrimitive_ReloadNeverRepeatsWriteAheadManifestMutation()
        {
            string operationId = Guid.NewGuid().ToString("N");
            string addPrepared = BuildPrimitiveStateJson(
                "add-prepared",
                operationId,
                true,
                string.Empty,
                PackageManifestMetaVerification.Verified,
                "0123456789abcdef0123456789abcdef",
                ownsManifestEntry: false);
            string cleanupPrepared = BuildPrimitiveStateJson(
                "cleanup-prepared",
                operationId,
                true,
                "com.example.root",
                PackageManifestMetaVerification.Verified,
                "0123456789abcdef0123456789abcdef");
            string completedCleanup = BuildPrimitiveStateJson(
                "cleanup",
                operationId,
                true,
                "com.example.root",
                PackageManifestMetaVerification.Verified,
                "0123456789abcdef0123456789abcdef");

            Assert.That(
                PackageManagerReadOnlyGitInstallService
                    .TryBuildInvalidActiveStateCompletion(
                        addPrepared,
                        out ReadOnlyGitPackageInstallCompletion addBlocked),
                Is.True);
            Assert.That(addBlocked.Message,
                Does.Contain("No package mutation was issued again"));
            Assert.That(
                PackageManagerReadOnlyGitInstallService
                    .TryBuildInvalidActiveStateCompletion(
                        cleanupPrepared,
                        out ReadOnlyGitPackageInstallCompletion cleanupBlocked),
                Is.True);
            Assert.That(cleanupBlocked.Message,
                Does.Contain("No package mutation was issued again"));
            Assert.That(
                PackageManagerReadOnlyGitInstallService
                    .TryBuildInvalidActiveStateCompletion(
                        completedCleanup,
                        out _),
                Is.False,
                "A completed exact-CAS cleanup reload must resume only UPM resolution.");
        }

        [Test]
        public void ReadOnlyPrimitive_LegacyEntryPointsFailClosedWithoutEvidence()
        {
            if (PackageManagerReadOnlyGitInstallService.IsBusy)
                Assert.Ignore("A live read-only install owns the static service.");

            Assert.That(
                PackageManagerReadOnlyGitInstallService.TryStart(
                    "https://github.com/org/legacy-proof.git",
                    "main",
                    "com.example.legacyproof",
                    (Action<ReadOnlyGitPackageInstallCompletion>)null,
                    out string missingMetaError),
                Is.False);
            Assert.That(missingMetaError,
                Does.Contain("verified package.json.meta"));

            Assert.That(
                PackageManagerReadOnlyGitInstallService.TryStart(
                    "https://github.com/org/legacy-proof.git",
                    "main",
                    "com.example.legacyproof",
                    "1.0.0",
                    GitUtility.ComputePackageDependencyFingerprint(
                        Array.Empty<PackageManifestDependency>()),
                    PackageManifestMetaVerification.Verified,
                    "0123456789abcdef0123456789abcdef",
                    string.Empty,
                    null,
                    out string missingCommitError),
                Is.False);
            Assert.That(missingCommitError,
                Does.Contain("exact inspected Git commit"));
        }

        [Test]
        public void ReadOnlyPrimitive_ResolvedCommitMustMatchInspectedCommit()
        {
            Assert.That(
                PackageManagerReadOnlyGitInstallService.TryValidateResolvedCommit(
                    RootInspectedCommit,
                    RootInspectedCommit.ToUpperInvariant(),
                    out string matchingError),
                Is.True,
                matchingError);
            Assert.That(
                PackageManagerReadOnlyGitInstallService.TryValidateResolvedCommit(
                    RootInspectedCommit,
                    "3333333333333333333333333333333333333333",
                    out string mismatchError),
                Is.False);
            Assert.That(mismatchError, Does.Contain("different Git commit"));
            Assert.That(
                PackageManagerReadOnlyGitInstallService.TryValidateResolvedCommit(
                    RootInspectedCommit,
                    string.Empty,
                    out string missingError),
                Is.False);
            Assert.That(missingError, Does.Contain("verifiable resolved"));
        }

        [Test]
        public void Coordinator_RejectsReadOnlyPackageResolvedAtAnotherCommit()
        {
            var executor = new FakeInstallExecutor();
            executor.Registered.Add(InstalledReadOnly(
                "com.example.root",
                "1.0.0",
                resolvedCommit:
                    "3333333333333333333333333333333333333333"));
            PackageDependencyInstallCompletion observed = null;
            var coordinator = new PackageDependencyInstallCoordinatorCore(
                executor,
                new MemoryStateStore(),
                () => 100L,
                completion => observed = completion);

            Assert.That(
                coordinator.TryStart(
                    Request(PackageManagerGitInstallMode.ReadOnlyPackage),
                    Plan(),
                    null,
                    out string error),
                Is.True,
                error);
            Assert.That(coordinator.Tick(), Is.True);
            Assert.That(executor.Started, Is.Empty);
            Assert.That(observed, Is.Not.Null);
            Assert.That(observed.Success, Is.False);
            Assert.That(observed.Message, Does.Contain("repository identity"));
        }

        [Test]
        public void ReadOnlyPrimitive_CorruptAddStateStaysBlockedAndPublishesOnce()
        {
            string operationId = Guid.NewGuid().ToString("N");
            string json = BuildPrimitiveStateJson(
                "add",
                operationId,
                includeDependencyFingerprint: false,
                cleanupPackageName: string.Empty);

            AssertCorruptPrimitiveStateRemainsBlocked(json, operationId);
        }

        [Test]
        public void ReadOnlyPrimitive_CorruptCleanupStateStaysBlockedAndPublishesOnce()
        {
            string operationId = Guid.NewGuid().ToString("N");
            string json = BuildPrimitiveStateJson(
                "cleanup",
                operationId,
                includeDependencyFingerprint: true,
                cleanupPackageName: string.Empty);

            AssertCorruptPrimitiveStateRemainsBlocked(json, operationId);
        }

        [Test]
        public void ReadOnlyRecoveryRetention_PersistsCompletionBeforeOwnershipMarker()
        {
            var completion = new ReadOnlyGitPackageInstallCompletion(
                false,
                "Recovery is blocked.",
                string.Empty,
                null);
            var calls = new List<string>();

            bool retained = PackageManagerReadOnlyGitInstallService
                .TryRetainRecoveryFailure(
                    completion,
                    _ => calls.Add("completion"),
                    () =>
                    {
                        calls.Add("marker");
                        throw new InvalidOperationException(
                            "marker persistence failed");
                    },
                    out string firstError);

            Assert.That(retained, Is.False);
            Assert.That(calls, Is.EqualTo(new[] { "completion", "marker" }));
            Assert.That(firstError, Does.Contain("marker persistence failed"));

            calls.Clear();
            Assert.That(
                PackageManagerReadOnlyGitInstallService
                    .TryRetainRecoveryFailure(
                        completion,
                        _ => calls.Add("completion"),
                        () => calls.Add("marker"),
                        out string retryError),
                Is.True,
                retryError);
            Assert.That(calls, Is.EqualTo(new[] { "completion", "marker" }));
        }

        [Test]
        public void ReadOnlyRecoveryRetention_DoesNotMarkUnretainedCompletion()
        {
            var completion = new ReadOnlyGitPackageInstallCompletion(
                false,
                "Recovery is blocked.",
                string.Empty,
                null);
            bool markerCalled = false;

            Assert.That(
                PackageManagerReadOnlyGitInstallService
                    .TryRetainRecoveryFailure(
                        completion,
                        _ => throw new InvalidOperationException(
                            "completion persistence failed"),
                        () => markerCalled = true,
                        out string error),
                Is.False);
            Assert.That(markerCalled, Is.False);
            Assert.That(error, Does.Contain("completion persistence failed"));
        }

        [Test]
        public void Coordinator_CorruptIssuedStatePreservesEvidenceAndBlocksDurably()
        {
            var store = new MemoryStateStore();
            var issuingExecutor = new FakeInstallExecutor();
            var issuing = new PackageDependencyInstallCoordinatorCore(
                issuingExecutor,
                store,
                () => 100L);
            Assert.That(
                issuing.TryStart(
                    Request(PackageManagerGitInstallMode.ReadOnlyPackage),
                    Plan(),
                    null,
                    out string startError),
                Is.True,
                startError);
            Assert.That(issuing.Tick(), Is.True);
            Assert.That(issuingExecutor.Started, Has.Count.EqualTo(1));

            string expectedFingerprint =
                GitUtility.ComputePackageDependencyFingerprint(
                    Array.Empty<PackageManifestDependency>());
            string corruptEvidence = store.Active.Replace(
                expectedFingerprint,
                string.Empty);
            Assert.That(corruptEvidence, Is.Not.EqualTo(store.Active));
            store.Active = corruptEvidence;

            var recoveryExecutor = new FakeInstallExecutor();
            int completionCount = 0;
            var recovered = new PackageDependencyInstallCoordinatorCore(
                recoveryExecutor,
                store,
                () => 200L,
                completion =>
                {
                    completionCount++;
                    Assert.That(completion.Success, Is.False);
                    Assert.That(completion.Message, Does.Contain("recovery evidence"));
                    Assert.That(completion.Message, Does.Contain("restart"));
                });

            Assert.That(recovered.IsBusy, Is.True);
            Assert.That(recovered.IsRecoveryBlocked, Is.True);
            Assert.That(recovered.ActiveStep, Is.Null);
            Assert.That(recovered.ActiveStepPackageName, Is.Empty);
            Assert.That(store.ClearActiveCount, Is.Zero);
            Assert.That(store.Active, Is.EqualTo(corruptEvidence));
            Assert.That(
                recovered.TryStart(Request(), Plan(), null, out string blockedError),
                Is.False);
            Assert.That(blockedError, Does.Contain("recovery evidence"));
            Assert.That(blockedError, Does.Contain("manifest.json"));
            Assert.That(blockedError, Does.Contain("restart"));
            Assert.That(recoveryExecutor.Started, Is.Empty);

            Assert.That(recovered.Tick(), Is.True);
            Assert.That(recovered.Tick(), Is.False);
            Assert.That(completionCount, Is.EqualTo(1));
            Assert.That(store.SaveRecoveryNotificationCount, Is.EqualTo(1));
            Assert.That(store.ClearActiveCount, Is.Zero);
            Assert.That(store.Active, Is.EqualTo(corruptEvidence));
            Assert.That(recoveryExecutor.Started, Is.Empty);

            var reloaded = new PackageDependencyInstallCoordinatorCore(
                recoveryExecutor,
                store,
                () => 300L,
                _ => completionCount++);
            Assert.That(reloaded.IsBusy, Is.True);
            Assert.That(reloaded.IsRecoveryBlocked, Is.True);
            Assert.That(reloaded.NeedsUpdate, Is.False);
            Assert.That(reloaded.Tick(), Is.False);
            Assert.That(completionCount, Is.EqualTo(1));
            Assert.That(store.ClearActiveCount, Is.Zero);
            Assert.That(store.Active, Is.EqualTo(corruptEvidence));
            Assert.That(recoveryExecutor.Started, Is.Empty);
        }

        [Test]
        public void Coordinator_CorruptRootIdentityRetainsRecoveryForExactlyOneConsumer()
        {
            var store = new MemoryStateStore();
            var issuingExecutor = new FakeInstallExecutor();
            var issuing = new PackageDependencyInstallCoordinatorCore(
                issuingExecutor,
                store,
                () => 100L);
            Assert.That(
                issuing.TryStart(
                    Request(PackageManagerGitInstallMode.ReadOnlyPackage),
                    Plan(),
                    null,
                    out string startError),
                Is.True,
                startError);
            Assert.That(issuing.Tick(), Is.True);

            string corruptEvidence = store.Active.Replace(
                "\"RootPackageName\":\"com.example.root\"",
                "\"RootPackageName\":\"\"");
            Assert.That(corruptEvidence, Is.Not.EqualTo(store.Active));
            store.Active = corruptEvidence;

            var recoveryExecutor = new FakeInstallExecutor();
            var recoveredWithoutObserver =
                new PackageDependencyInstallCoordinatorCore(
                    recoveryExecutor,
                    store,
                    () => 200L);
            Assert.That(recoveredWithoutObserver.IsRecoveryBlocked, Is.True);
            Assert.That(recoveredWithoutObserver.Tick(), Is.True);
            Assert.That(recoveredWithoutObserver.NeedsUpdate, Is.False);
            Assert.That(store.Active, Is.EqualTo(corruptEvidence));
            Assert.That(store.Completion, Is.Not.Empty);
            Assert.That(store.RecoveryNotification, Is.Not.Empty);
            Assert.That(recoveryExecutor.Started, Is.Empty);

            var reloaded = new PackageDependencyInstallCoordinatorCore(
                recoveryExecutor,
                store,
                () => 300L);
            Assert.That(reloaded.IsRecoveryBlocked, Is.True);
            Assert.That(reloaded.NeedsUpdate, Is.False);
            Assert.That(
                reloaded.TryGetLastCompletion(out var retained),
                Is.True);
            Assert.That(retained.Success, Is.False);
            Assert.That(retained.RootPackageName, Is.Empty);
            Assert.That(retained.Message, Does.Contain("recovery evidence"));
            Assert.That(
                reloaded.TryConsumeLastCompletion(out var consumed),
                Is.True);
            Assert.That(consumed.Message, Is.EqualTo(retained.Message));
            Assert.That(
                reloaded.TryConsumeLastCompletion(out _),
                Is.False,
                "A retained recovery outcome must have exactly one consumer.");
            Assert.That(store.Active, Is.EqualTo(corruptEvidence));
        }

        [Test]
        public void Coordinator_RecoveryPersistenceFailuresDoNotPublishOrMark()
        {
            var store = new MemoryStateStore();
            var issuing = new PackageDependencyInstallCoordinatorCore(
                new FakeInstallExecutor(),
                store,
                () => 100L);
            Assert.That(
                issuing.TryStart(
                    Request(PackageManagerGitInstallMode.ReadOnlyPackage),
                    Plan(),
                    null,
                    out string startError),
                Is.True,
                startError);
            Assert.That(issuing.Tick(), Is.True);
            string corruptEvidence = store.Active.Replace(
                "\"RootPackageName\":\"com.example.root\"",
                "\"RootPackageName\":\"\"");
            Assert.That(corruptEvidence, Is.Not.EqualTo(store.Active));
            store.Active = corruptEvidence;

            int callbackCount = 0;
            var recovered = new PackageDependencyInstallCoordinatorCore(
                new FakeInstallExecutor(),
                store,
                () => 200L,
                _ => callbackCount++);
            store.ThrowOnSaveCompletion = true;
            Assert.That(recovered.Tick(), Is.False);
            Assert.That(callbackCount, Is.Zero);
            Assert.That(store.SaveRecoveryNotificationCount, Is.Zero);
            Assert.That(store.RecoveryNotification, Is.Empty);
            Assert.That(recovered.NeedsUpdate, Is.True);

            store.ThrowOnSaveCompletion = false;
            store.ThrowOnSaveRecoveryNotification = true;
            Assert.That(recovered.Tick(), Is.False);
            Assert.That(callbackCount, Is.Zero);
            Assert.That(store.Completion, Is.Not.Empty);
            Assert.That(store.RecoveryNotification, Is.Empty);
            Assert.That(recovered.NeedsUpdate, Is.True);
            Assert.That(
                recovered.TryGetLastCompletion(out _),
                Is.False,
                "An uncommitted recovery completion must not acquire a " +
                "presentation owner before its once-only marker.");

            store.ThrowOnSaveRecoveryNotification = false;
            Assert.That(recovered.Tick(), Is.True);
            Assert.That(recovered.Tick(), Is.False);
            Assert.That(callbackCount, Is.EqualTo(1));
            Assert.That(store.RecoveryNotification, Is.Not.Empty);
            Assert.That(recovered.NeedsUpdate, Is.False);
            Assert.That(recovered.TryGetLastCompletion(out _), Is.True);
        }

        [Test]
        public void ReadOnlyPrimitiveCorrelation_RequiresExactPackageForExactResult()
        {
            string operationId = Guid.NewGuid().ToString("N");
            foreach ((bool success, string packageName,
                         ReadOnlyInstallCompletionCorrelation expected) value in
                     new[]
                     {
                         (false, string.Empty,
                             ReadOnlyInstallCompletionCorrelation.OperationIdentityOnly),
                         (false, "com.example.other",
                             ReadOnlyInstallCompletionCorrelation.OperationIdentityOnly),
                         (true, string.Empty,
                             ReadOnlyInstallCompletionCorrelation.OperationIdentityOnly),
                         (true, "com.example.other",
                             ReadOnlyInstallCompletionCorrelation.OperationIdentityOnly),
                         (false, "com.example.root",
                             ReadOnlyInstallCompletionCorrelation.Exact),
                         (true, "com.example.root",
                             ReadOnlyInstallCompletionCorrelation.Exact)
                     })
            {
                var completion = new ReadOnlyGitPackageInstallCompletion(
                    value.success,
                    "terminal",
                    value.packageName,
                    null,
                    operationId);

                Assert.That(
                    PackageDependencyInstallCoordinator.ClassifyReadOnlyCompletion(
                        true,
                        PackageManagerGitInstallMode.ReadOnlyPackage,
                        operationId,
                        "com.example.root",
                        completion),
                    Is.EqualTo(value.expected));
                Assert.That(
                    UnityPackageDependencyInstallExecutor
                        .HasExactReadOnlyCompletionIdentity(
                            "com.example.root",
                            operationId,
                            completion),
                    Is.EqualTo(
                        value.expected ==
                        ReadOnlyInstallCompletionCorrelation.Exact));
            }

            var unrelated = new ReadOnlyGitPackageInstallCompletion(
                false,
                "terminal",
                string.Empty,
                null,
                operationId);
            Assert.That(
                PackageDependencyInstallCoordinator.ClassifyReadOnlyCompletion(
                    true,
                    PackageManagerGitInstallMode.ReadOnlyPackage,
                    Guid.NewGuid().ToString("N"),
                    "com.example.root",
                    unrelated),
                Is.EqualTo(ReadOnlyInstallCompletionCorrelation.None));
            Assert.That(
                UnityPackageDependencyInstallExecutor
                    .HasExactReadOnlyCompletionIdentity(
                        "com.example.root",
                        Guid.NewGuid().ToString("N"),
                        unrelated),
                Is.False);
        }

        [TestCase(false, "")]
        [TestCase(false, "com.example.other")]
        [TestCase(true, "")]
        [TestCase(true, "com.example.other")]
        public void Coordinator_CorrelationDamageCreatesDurableRecoveryBlock(
            bool primitiveSuccess,
            string primitivePackageName)
        {
            var store = new MemoryStateStore();
            var executor = new FakeInstallExecutor();
            int completionCount = 0;
            var coordinator = new PackageDependencyInstallCoordinatorCore(
                executor,
                store,
                () => 100L,
                completion =>
                {
                    completionCount++;
                    Assert.That(completion.Success, Is.False);
                });
            Assert.That(
                coordinator.TryStart(
                    Request(PackageManagerGitInstallMode.ReadOnlyPackage),
                    Plan(),
                    null,
                    out string startError),
                Is.True,
                startError);
            Assert.That(coordinator.Tick(), Is.True);
            Assert.That(executor.Started, Has.Count.EqualTo(1));

            var primitiveCompletion = new ReadOnlyGitPackageInstallCompletion(
                primitiveSuccess,
                "terminal",
                primitivePackageName,
                null,
                coordinator.ActiveOperationId);
            Assert.That(
                PackageDependencyInstallCoordinator.ClassifyReadOnlyCompletion(
                    coordinator.IsBusy,
                    coordinator.ActiveInstallMode,
                    coordinator.ActiveOperationId,
                    coordinator.ActiveStepPackageName,
                    primitiveCompletion),
                Is.EqualTo(
                    ReadOnlyInstallCompletionCorrelation.OperationIdentityOnly));

            Assert.That(
                coordinator.TryBlockForPrimitiveCorrelationFailure(
                    "Completion package identity was damaged. Inspect manifest.json and restart.",
                    out string blockError),
                Is.True,
                blockError);
            Assert.That(coordinator.IsBusy, Is.True);
            Assert.That(coordinator.IsRecoveryBlocked, Is.True);
            Assert.That(coordinator.ActiveStep, Is.Null);
            Assert.That(coordinator.Tick(), Is.True);
            Assert.That(coordinator.Tick(), Is.False);
            Assert.That(completionCount, Is.EqualTo(1));
            Assert.That(executor.Started, Has.Count.EqualTo(1));

            var reloaded = new PackageDependencyInstallCoordinatorCore(
                executor,
                store,
                () => 200L,
                _ => completionCount++);
            Assert.That(reloaded.IsRecoveryBlocked, Is.True);
            Assert.That(reloaded.Tick(), Is.False);
            Assert.That(completionCount, Is.EqualTo(1));
            Assert.That(executor.Started, Has.Count.EqualTo(1));
        }

        [Test]
        public void Coordinator_VerifyingStateSurvivesReloadAndRegistrationLag()
        {
            long now = 100L;
            var store = new MemoryStateStore();
            var firstExecutor = new FakeInstallExecutor();
            var first = new PackageDependencyInstallCoordinatorCore(
                firstExecutor,
                store,
                () => now);
            Assert.That(
                first.TryStart(Request(), Plan(), null, out string startError),
                Is.True,
                startError);
            Assert.That(first.Tick(), Is.True);
            firstExecutor.Complete(true, "Primitive completed.");

            Assert.That(first.IsBusy, Is.True);
            Assert.That(first.Tick(), Is.False,
                "Registration may legitimately lag a successful primitive.");
            Assert.That(firstExecutor.Started.Count, Is.EqualTo(1));

            var resumedExecutor = new FakeInstallExecutor();
            PackageDependencyInstallCompletion observed = null;
            var resumed = new PackageDependencyInstallCoordinatorCore(
                resumedExecutor,
                store,
                () => now,
                completion => observed = completion);

            Assert.That(resumed.IsBusy, Is.True);
            Assert.That(resumed.Tick(), Is.False);
            Assert.That(resumedExecutor.Started, Is.Empty,
                "A persisted attempted step must never be issued twice.");

            resumedExecutor.Registered.Add(InstalledEmbedded(
                "com.example.root",
                "1.0.0"));
            Assert.That(resumed.Tick(), Is.True);
            Assert.That(resumed.IsBusy, Is.False);
            Assert.That(observed, Is.Not.Null);
            Assert.That(observed.Success, Is.True);
        }

        [Test]
        public void Coordinator_SynchronousAndDuplicateSuccessCallbacksAreIdempotent()
        {
            var store = new MemoryStateStore();
            var executor = new FakeInstallExecutor
            {
                CompleteSynchronously = true
            };
            int completionCount = 0;
            var coordinator = new PackageDependencyInstallCoordinatorCore(
                executor,
                store,
                () => 100L,
                _ => completionCount++);
            Assert.That(
                coordinator.TryStart(
                    Request(PackageManagerGitInstallMode.ReadOnlyPackage),
                    Plan(),
                    null,
                    out string startError),
                Is.True,
                startError);

            Assert.That(coordinator.Tick(), Is.True);
            Assert.That(coordinator.IsBusy, Is.True);
            Assert.That(executor.Started.Count, Is.EqualTo(1));
            int saveCountAfterSuccess = store.SaveActiveCount;

            executor.LastCallback.Invoke(
                new PackageDependencyPrimitiveCompletion(
                    true,
                    "com.example.root",
                    "Duplicate service event."));
            Assert.That(store.SaveActiveCount, Is.EqualTo(saveCountAfterSuccess));
            Assert.That(completionCount, Is.Zero);

            executor.Registered.Add(InstalledReadOnly(
                "com.example.root",
                "1.0.0"));
            Assert.That(coordinator.Tick(), Is.True);
            Assert.That(coordinator.IsBusy, Is.False);
            Assert.That(completionCount, Is.EqualTo(1));
        }

        [Test]
        public void Coordinator_DoesNotTimeoutWhileOwnedPrimitiveIsBusy()
        {
            long now = 100L;
            var executor = new FakeInstallExecutor();
            var coordinator = new PackageDependencyInstallCoordinatorCore(
                executor,
                new MemoryStateStore(),
                () => now);
            Assert.That(
                coordinator.TryStart(Request(), Plan(), null, out string error),
                Is.True,
                error);
            Assert.That(coordinator.Tick(), Is.True);

            now += TimeSpan.FromMinutes(10d).Ticks;
            Assert.That(coordinator.Tick(), Is.False);
            Assert.That(coordinator.IsBusy, Is.True);
            Assert.That(executor.Started.Count, Is.EqualTo(1));

            executor.Complete(true, "Primitive completed.");
            executor.Registered.Add(InstalledEmbedded(
                "com.example.root",
                "1.0.0"));
            Assert.That(coordinator.Tick(), Is.True);
            Assert.That(coordinator.IsBusy, Is.False);
        }

        [Test]
        public void Coordinator_MatchingPackageTimeoutIsNotHeldByUnrelatedMutation()
        {
            long now = 100L;
            var store = new MemoryStateStore();
            var issuing = new PackageDependencyInstallCoordinatorCore(
                new FakeInstallExecutor(),
                store,
                () => now);
            Assert.That(
                issuing.TryStart(
                    Request(PackageManagerGitInstallMode.ReadOnlyPackage),
                    Plan(),
                    null,
                    out string startError),
                Is.True,
                startError);
            Assert.That(issuing.Tick(), Is.True);

            var resumedExecutor = new FakeInstallExecutor
            {
                MutationBusy = true
            };
            resumedExecutor.Registered.Add(InstalledReadOnly(
                "com.example.root",
                "1.0.0"));
            PackageDependencyInstallCompletion observed = null;
            var resumed = new PackageDependencyInstallCoordinatorCore(
                resumedExecutor,
                store,
                () => now,
                completion => observed = completion);

            Assert.That(resumed.Tick(), Is.False);
            Assert.That(resumed.IsBusy, Is.True);
            now += TimeSpan.FromMinutes(10d).Ticks;
            Assert.That(resumed.Tick(), Is.True,
                "An unrelated global mutation may defer verification only until " +
                "the owned step reaches its recovery deadline.");
            Assert.That(resumed.IsBusy, Is.False);
            Assert.That(observed, Is.Not.Null);
            Assert.That(observed.Success, Is.False);
            Assert.That(observed.Message, Does.Contain("timed out"));
            Assert.That(resumedExecutor.Started, Is.Empty);
        }

        [Test]
        public void Coordinator_RejectsDuplicateRegisteredIdentityWithoutMutation()
        {
            var executor = new FakeInstallExecutor();
            executor.Registered.Add(InstalledEmbedded(
                "com.example.root",
                "1.0.0"));
            executor.Registered.Add(InstalledEmbedded(
                "com.example.root",
                "1.0.0"));
            PackageDependencyInstallCompletion observed = null;
            var coordinator = new PackageDependencyInstallCoordinatorCore(
                executor,
                new MemoryStateStore(),
                () => 100L,
                completion => observed = completion);
            Assert.That(
                coordinator.TryStart(Request(), Plan(), null, out string error),
                Is.True,
                error);

            Assert.That(coordinator.Tick(), Is.True);
            Assert.That(coordinator.IsBusy, Is.False);
            Assert.That(executor.Started, Is.Empty);
            Assert.That(observed, Is.Not.Null);
            Assert.That(observed.Success, Is.False);
            Assert.That(observed.Message, Does.Contain("more than once"));
        }

        [TestCase("https://github.com/org/other.git", "main")]
        [TestCase("https://github.com/org/root.git", "release")]
        public void Coordinator_RejectsMismatchedInstalledRepositoryIdentity(
            string repositoryUrl,
            string revision)
        {
            var executor = new FakeInstallExecutor();
            executor.Registered.Add(InstalledReadOnly(
                "com.example.root",
                "1.0.0",
                repositoryUrl,
                revision));
            PackageDependencyInstallCompletion observed = null;
            var coordinator = new PackageDependencyInstallCoordinatorCore(
                executor,
                new MemoryStateStore(),
                () => 100L,
                completion => observed = completion);
            Assert.That(
                coordinator.TryStart(
                    Request(PackageManagerGitInstallMode.ReadOnlyPackage),
                    Plan(),
                    null,
                    out string error),
                Is.True,
                error);

            Assert.That(coordinator.Tick(), Is.True);
            Assert.That(coordinator.IsBusy, Is.False);
            Assert.That(executor.Started, Is.Empty);
            Assert.That(observed, Is.Not.Null);
            Assert.That(observed.Success, Is.False);
            Assert.That(observed.Message,
                Does.Contain("repository identity"));
        }

        [Test]
        public void Coordinator_PrimitiveFailureIsSanitizedAndRetained()
        {
            var store = new MemoryStateStore();
            var executor = new FakeInstallExecutor();
            PackageDependencyInstallCompletion observed = null;
            var coordinator = new PackageDependencyInstallCoordinatorCore(
                executor,
                store,
                () => 100L,
                completion => observed = completion);
            Assert.That(
                coordinator.TryStart(
                    Request(PackageManagerGitInstallMode.ReadOnlyPackage),
                    Plan(),
                    null,
                    out string startError),
                Is.True,
                startError);
            Assert.That(coordinator.Tick(), Is.True);

            executor.LastCallback.Invoke(
                new PackageDependencyPrimitiveCompletion(
                    false,
                    "com.example.root",
                    "failed https://user:secret@github.com/org/root.git"));

            Assert.That(coordinator.IsBusy, Is.False);
            Assert.That(observed, Is.Not.Null);
            Assert.That(observed.Success, Is.False);
            Assert.That(observed.Message, Does.Not.Contain("secret"));
            Assert.That(
                coordinator.TryConsumeLastCompletion(out var retained),
                Is.True);
            Assert.That(retained.Success, Is.False);
            Assert.That(retained.Message, Does.Not.Contain("secret"));
        }

        [Test]
        public void Coordinator_RejectsBlockingPlanBeforeMutation()
        {
            var unresolved = new PackageDependencyResolutionResult(
                new PackageDependencyRequirement(
                    "com.example.missing",
                    "1.0.0",
                    new[] { "com.example.root" }),
                PackageDependencyResolutionStatus.Ambiguous,
                new[]
                {
                    GitHubCandidate(
                        "com.example.missing",
                        "1.0.0",
                        "org/one"),
                    GitHubCandidate(
                        "com.example.missing",
                        "1.0.0",
                        "org/two")
                },
                "Multiple sources were found.");
            var executor = new FakeInstallExecutor();
            var coordinator = new PackageDependencyInstallCoordinatorCore(
                executor,
                new MemoryStateStore(),
                () => 100L);

            Assert.That(
                coordinator.TryStart(
                    Request(),
                    Plan(unresolved),
                    null,
                    out string error),
                Is.False);
            Assert.That(error, Does.Contain("complete, unambiguous"));
            Assert.That(executor.Started, Is.Empty);
        }

        private static string BuildPrimitiveStateJson(
            string stage,
            string operationId,
            bool includeDependencyFingerprint,
            string cleanupPackageName,
            PackageManifestMetaVerification packageManifestMetaVerification =
                PackageManifestMetaVerification.Unverified,
            string packageManifestMetaGuid = "",
            string inspectedCommit = RootInspectedCommit,
            bool ownsManifestEntry = true)
        {
            Assert.That(
                PackageManifestGitDependencyStore.TryBuildGitSpec(
                    "https://github.com/org/root.git",
                    inspectedCommit,
                    out string spec,
                    out string specError),
                Is.True,
                specError);
            string fingerprint = includeDependencyFingerprint
                ? GitUtility.ComputePackageDependencyFingerprint(
                    Array.Empty<PackageManifestDependency>())
                : string.Empty;
            string installResolutionOperationId =
                Guid.NewGuid().ToString("N");
            string cleanupResolutionOperationId =
                stage == "cleanup" || stage == "cleanup-prepared"
                    ? Guid.NewGuid().ToString("N")
                    : string.Empty;
            string failureMessage =
                stage == "cleanup" || stage == "cleanup-prepared"
                    ? "Mismatch cleanup was pending."
                    : string.Empty;
            return
                "{\"SchemaVersion\":4,\"Stage\":\"" + stage +
                "\",\"RepositoryUrl\":\"https://github.com/org/root.git\"," +
                "\"Revision\":\"main\",\"ExpectedPackageName\":\"com.example.root\"," +
                "\"ExpectedVersion\":\"1.0.0\",\"ExpectedDependencyFingerprint\":\"" +
                fingerprint + "\",\"PackageManifestMetaVerification\":" +
                (int)packageManifestMetaVerification +
                ",\"ExpectedPackageManifestMetaGuid\":\"" +
                packageManifestMetaGuid +
                "\",\"ExpectedInspectedCommit\":\"" +
                inspectedCommit +
                "\",\"DependencyInstallOperationId\":\"" +
                operationId + "\",\"InstallResolutionOperationId\":\"" +
                installResolutionOperationId +
                "\",\"OwnsManifestEntry\":" +
                (ownsManifestEntry ? "true" : "false") +
                ",\"Spec\":\"" + spec +
                "\",\"CleanupPackageName\":\"" +
                cleanupPackageName +
                "\",\"CleanupResolutionOperationId\":\"" +
                cleanupResolutionOperationId +
                "\",\"FailureMessage\":\"" + failureMessage + "\"," +
                "\"StartedUtcTicks\":100}";
        }

        private static void AssertCorruptPrimitiveStateRemainsBlocked(
            string json,
            string operationId)
        {
            const string activeKey =
                "MartinCalander.GitSubmoduleManager.ReadOnlyGitInstall.Active.v1";
            const string completionKey =
                "MartinCalander.GitSubmoduleManager.ReadOnlyGitInstall.Completion.v1";
            const string notificationKey =
                "MartinCalander.GitSubmoduleManager.ReadOnlyGitInstall.RecoveryNotification.v1";
            if (PackageManagerReadOnlyGitInstallService.IsBusy)
            {
                Assert.Ignore(
                    "A live read-only package operation owns the static recovery state.");
            }

            Type serviceType = typeof(PackageManagerReadOnlyGitInstallService);
            const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Static;
            FieldInfo activeStateField = serviceType.GetField("activeState", flags);
            MethodInfo loadMethod = serviceType.GetMethod("LoadActiveState", flags);
            MethodInfo updateMethod = serviceType.GetMethod("Update", flags);
            Assert.That(activeStateField, Is.Not.Null);
            Assert.That(loadMethod, Is.Not.Null);
            Assert.That(updateMethod, Is.Not.Null);

            string previousActive = SessionState.GetString(activeKey, string.Empty);
            string previousCompletion = SessionState.GetString(
                completionKey,
                string.Empty);
            bool previousNotification = SessionState.GetBool(
                notificationKey,
                false);
            object previousRuntimeState = activeStateField.GetValue(null);
            int completionCount = 0;
            ReadOnlyGitPackageInstallCompletion observed = null;
            void Handler(ReadOnlyGitPackageInstallCompletion completion)
            {
                completionCount++;
                observed = completion;
            }

            PackageManagerReadOnlyGitInstallService.Completed += Handler;
            try
            {
                SessionState.SetString(activeKey, json);
                SessionState.EraseString(completionKey);
                SessionState.SetBool(notificationKey, false);
                activeStateField.SetValue(null, loadMethod.Invoke(null, null));

                Assert.That(PackageManagerReadOnlyGitInstallService.IsBusy, Is.True);
                Assert.That(
                    PackageManagerReadOnlyGitInstallService.IsRecoveryBlocked,
                    Is.True);
                Assert.That(SessionState.GetString(activeKey, string.Empty),
                    Is.EqualTo(json));
                Assert.That(
                    PackageManagerReadOnlyGitInstallService.TryStart(
                        "https://github.com/org/root.git",
                        "main",
                        "com.example.root",
                        null,
                        out string blockedError),
                    Is.False);
                Assert.That(blockedError, Does.Contain("blocked"));
                Assert.That(blockedError, Does.Contain("manifest.json"));
                Assert.That(blockedError, Does.Contain("restart"));

                updateMethod.Invoke(null, null);
                updateMethod.Invoke(null, null);
                Assert.That(completionCount, Is.EqualTo(1));
                Assert.That(observed, Is.Not.Null);
                Assert.That(observed.Success, Is.False);
                Assert.That(observed.DependencyInstallOperationId,
                    Is.EqualTo(operationId));
                Assert.That(observed.Message,
                    Does.Contain("No package mutation was issued again"));
                Assert.That(observed.Message, Does.Contain("restart"));
                Assert.That(PackageManagerReadOnlyGitInstallService.IsBusy, Is.True);
                Assert.That(SessionState.GetString(activeKey, string.Empty),
                    Is.EqualTo(json));

                // Simulate another script reload. The durable marker prevents a
                // second terminal callback while the raw evidence stays active.
                activeStateField.SetValue(null, loadMethod.Invoke(null, null));
                updateMethod.Invoke(null, null);
                Assert.That(completionCount, Is.EqualTo(1));
                Assert.That(PackageManagerReadOnlyGitInstallService.IsBusy, Is.True);
                Assert.That(SessionState.GetString(activeKey, string.Empty),
                    Is.EqualTo(json));
            }
            finally
            {
                PackageManagerReadOnlyGitInstallService.Completed -= Handler;
                activeStateField.SetValue(null, previousRuntimeState);
                if (string.IsNullOrEmpty(previousActive))
                    SessionState.EraseString(activeKey);
                else
                    SessionState.SetString(activeKey, previousActive);
                if (string.IsNullOrEmpty(previousCompletion))
                    SessionState.EraseString(completionKey);
                else
                    SessionState.SetString(completionKey, previousCompletion);
                SessionState.SetBool(notificationKey, previousNotification);
            }
        }

        private static PackageDependencyInstallStep SubmoduleStep(
            string inspectedCommit)
        {
            return new PackageDependencyInstallStep(
                "com.example.root",
                "1.0.0",
                "https://github.com/org/root.git",
                "main",
                true,
                GitUtility.ComputePackageDependencyFingerprint(
                    Array.Empty<PackageManifestDependency>()),
                PackageManifestMetaVerification.Unverified,
                string.Empty,
                inspectedCommit);
        }

        private static PackageDependencySubmoduleCommitVerificationStatus
            WaitForCommitVerification(
                PackageDependencySubmoduleCommitVerifier verifier,
                string verificationScopeId,
                string operationId,
                int stepIndex,
                PackageDependencyInstallStep step,
                out string error)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(10d);
            PackageDependencySubmoduleCommitVerificationStatus status;
            do
            {
                status = verifier.GetOrStart(
                    verificationScopeId,
                    operationId,
                    stepIndex,
                    step,
                    out error);
                if (status !=
                    PackageDependencySubmoduleCommitVerificationStatus.Pending)
                {
                    return status;
                }

                Thread.Sleep(10);
            }
            while (DateTime.UtcNow < deadline);

            error = "Fresh submodule commit verification did not finish in 10 seconds.";
            Assert.Fail(error);
            return PackageDependencySubmoduleCommitVerificationStatus.Unverified;
        }

        private static void WaitForCoordinatorTerminal(
            PackageDependencyInstallCoordinatorCore coordinator)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(10d);
            while (coordinator.IsBusy && DateTime.UtcNow < deadline)
            {
                coordinator.Tick();
                if (coordinator.IsBusy)
                    Thread.Sleep(10);
            }

            Assert.That(
                coordinator.IsBusy,
                Is.False,
                "The coordinator did not publish a terminal result in 10 seconds.");
        }

        private static PackageDependencyInstallRequest Request(
            PackageManagerGitInstallMode mode =
                PackageManagerGitInstallMode.GitSubmodule,
            IEnumerable<PackageManifestDependency> dependencies = null,
            string rootVersion = "1.0.0",
            PackageManifestMetaVerification packageManifestMetaVerification =
                PackageManifestMetaVerification.Unverified,
            string packageManifestMetaGuid = "",
            PackageManifestMetaPolicy? packageManifestMetaPolicy = null,
            string inspectedCommit = RootInspectedCommit)
        {
            PackageManifestMetaPolicy resolvedPolicy =
                packageManifestMetaPolicy ??
                (mode == PackageManagerGitInstallMode.ReadOnlyPackage
                    ? PackageManifestMetaPolicy.RequireVerified
                    : PackageManifestMetaPolicy.AllowUnverifiedWithWarning);
            if (mode == PackageManagerGitInstallMode.ReadOnlyPackage &&
                packageManifestMetaVerification ==
                    PackageManifestMetaVerification.Unverified &&
                string.IsNullOrEmpty(packageManifestMetaGuid))
            {
                packageManifestMetaVerification =
                    PackageManifestMetaVerification.Verified;
                packageManifestMetaGuid =
                    "0123456789abcdef0123456789abcdef";
            }

            return new PackageDependencyInstallRequest(
                "https://github.com/org/root.git",
                "main",
                "com.example.root",
                rootVersion,
                mode,
                dependencies ?? Array.Empty<PackageManifestDependency>(),
                packageManifestMetaVerification,
                packageManifestMetaGuid,
                resolvedPolicy,
                inspectedCommit);
        }

        private static PackageDependencyResolutionPlan Plan(
            params PackageDependencyResolutionResult[] results)
        {
            return new PackageDependencyResolutionPlan(
                results,
                true,
                string.Empty,
                1);
        }

        private static PackageDependencyResolutionResult ResolvedGitHub(
            string packageName,
            string version,
            string requestedBy,
            string sourceName,
            string branch = "main")
        {
            return new PackageDependencyResolutionResult(
                new PackageDependencyRequirement(
                    packageName,
                    version,
                    new[] { requestedBy }),
                PackageDependencyResolutionStatus.Resolved,
                new[]
                {
                    GitHubCandidate(
                        packageName,
                        version,
                        sourceName,
                        branch)
                },
                string.Empty);
        }

        private static PackageDependencyCandidate GitHubCandidate(
            string packageName,
            string version,
            string sourceName,
            string branch = "main")
        {
            string repositoryName = sourceName.Split('/').Last();
            string owner = sourceName.Split('/').First();
            return new PackageDependencyCandidate(
                PackageDependencyCandidateSource.GitHub,
                packageName,
                version,
                sourceName,
                owner,
                repositoryName,
                $"https://github.com/{sourceName}.git",
                branch,
                dependencyFingerprint:
                    GitUtility.ComputePackageDependencyFingerprint(
                        Array.Empty<PackageManifestDependency>()),
                packageManifestMetaVerification:
                    PackageManifestMetaVerification.Verified,
                packageManifestMetaGuid:
                    "0123456789abcdef0123456789abcdef",
                repositoryCommit: DependencyInspectedCommit);
        }

        private static PackageDependencyResolutionResult ResolvedRegistry(
            string packageName,
            string version,
            string requestedBy,
            bool isUnity,
            string sourceName)
        {
            return new PackageDependencyResolutionResult(
                new PackageDependencyRequirement(
                    packageName,
                    version,
                    new[] { requestedBy }),
                PackageDependencyResolutionStatus.Resolved,
                new[]
                {
                    new PackageDependencyCandidate(
                        isUnity
                            ? PackageDependencyCandidateSource.UnityRegistry
                            : PackageDependencyCandidateSource.CustomRegistry,
                        packageName,
                        version,
                        sourceName)
                },
                string.Empty);
        }

        private static PackageDependencyInstalledPackage InstalledEmbedded(
            string packageName,
            string version,
            string repositoryUrl = "https://github.com/org/root.git",
            bool hasVerifiedRepositoryIdentity = true,
            string dependencyFingerprint = null,
            string resolvedCommit = RootInspectedCommit)
        {
            dependencyFingerprint ??=
                GitUtility.ComputePackageDependencyFingerprint(
                    Array.Empty<PackageManifestDependency>());
            return new PackageDependencyInstalledPackage(
                packageName,
                version,
                PackageDependencyInstalledSource.Embedded,
                true,
                Path.Combine(GitUtility.ProjectRoot, "Packages", packageName),
                repositoryUrl,
                string.Empty,
                hasVerifiedRepositoryIdentity,
                dependencyFingerprint,
                PackageManifestMetaVerification.Verified,
                "0123456789abcdef0123456789abcdef",
                resolvedCommit);
        }

        private static PackageDependencyInstalledPackage InstalledReadOnly(
            string packageName,
            string version,
            string repositoryUrl = "https://github.com/org/root.git",
            string revision = RootInspectedCommit,
            bool hasVerifiedRepositoryIdentity = true,
            string dependencyFingerprint = null,
            string resolvedPath = "",
            string packageManifestMetaGuid =
                "0123456789abcdef0123456789abcdef",
            string resolvedCommit = RootInspectedCommit)
        {
            dependencyFingerprint ??=
                GitUtility.ComputePackageDependencyFingerprint(
                    Array.Empty<PackageManifestDependency>());
            return new PackageDependencyInstalledPackage(
                packageName,
                version,
                PackageDependencyInstalledSource.Git,
                true,
                resolvedPath,
                repositoryUrl,
                revision,
                hasVerifiedRepositoryIdentity,
                dependencyFingerprint,
                PackageManifestMetaVerification.Verified,
                packageManifestMetaGuid,
                resolvedCommit);
        }

        private sealed class FakeResolutionFacade :
            IPackageDependencyResolutionFacade
        {
            public PackageManagerGitHubDiscoverySnapshot GitHubSnapshot { get; } =
                PackageManagerGitHubDiscoverySnapshot.Empty;

            public bool TryGetRegisteredPackageNames(
                out IReadOnlyList<string> packageNames,
                out string error)
            {
                packageNames = Array.Empty<string>();
                error = string.Empty;
                return true;
            }

            public bool TryStartRegistrySearch(
                string packageName,
                out IPackageDependencyRegistrySearch search,
                out string error)
            {
                search = null;
                error = "Unexpected registry search.";
                return false;
            }
        }

        private sealed class SubmoduleCommitFixture : IDisposable
        {
            private const string PackageRelativePath =
                "Packages/com.example.root";
            private readonly ProcessCommandRunner runner = new();
            private string matchingSymlinkTarget;
            private byte[] matchingSymlinkTargetContents = Array.Empty<byte>();

            internal SubmoduleCommitFixture()
            {
                ProjectRoot = Path.Combine(
                    Path.GetTempPath(),
                    "GitSubmoduleManager-FreshCommit-" +
                    Guid.NewGuid().ToString("N"));
                PackageRoot = Path.Combine(
                    ProjectRoot,
                    "Packages",
                    "com.example.root");
                Directory.CreateDirectory(PackageRoot);

                Run(ProjectRoot, "init", "--quiet");
                Run(PackageRoot, "init", "--quiet");
                Run(PackageRoot, "config", "user.name", "Test User");
                Run(
                    PackageRoot,
                    "config",
                    "user.email",
                    "test@example.invalid");
                Run(
                    PackageRoot,
                    "remote",
                    "add",
                    "origin",
                    "https://github.com/org/root.git");

                string payloadPath = Path.Combine(PackageRoot, "payload.txt");
                File.WriteAllText(payloadPath, "first\n");
                Run(PackageRoot, "add", "--", "payload.txt");
                Run(PackageRoot, "commit", "--quiet", "-m", "first");
                FirstCommit = Run(
                    PackageRoot,
                    "rev-parse",
                    "--verify",
                    "HEAD^{commit}").Trim();

                File.WriteAllText(payloadPath, "second\n");
                Run(PackageRoot, "add", "--", "payload.txt");
                Run(PackageRoot, "commit", "--quiet", "-m", "second");
                SecondCommit = Run(
                    PackageRoot,
                    "rev-parse",
                    "--verify",
                    "HEAD^{commit}").Trim();

                WriteGitModules("https://github.com/org/root.git");
                Run(ProjectRoot, "add", "--", ".gitmodules");
                SetState(FirstCommit);
            }

            internal string ProjectRoot { get; }
            internal string PackageRoot { get; }
            internal string FirstCommit { get; }
            internal string SecondCommit { get; }
            internal bool MatchingSymlinkTargetContentsAreUnchanged =>
                !string.IsNullOrWhiteSpace(matchingSymlinkTarget) &&
                File.Exists(matchingSymlinkTarget) &&
                File.ReadAllBytes(matchingSymlinkTarget)
                    .SequenceEqual(matchingSymlinkTargetContents);
            internal long GitModulesWriteTicks
            {
                get
                {
                    string path = Path.Combine(ProjectRoot, ".gitmodules");
                    return File.Exists(path)
                        ? File.GetLastWriteTimeUtc(path).Ticks
                        : 0L;
                }
            }

            internal void SetState(string commit)
            {
                Run(
                    PackageRoot,
                    "checkout",
                    "--quiet",
                    "--detach",
                    commit);
                Run(
                    ProjectRoot,
                    "update-index",
                    "--add",
                    "--cacheinfo",
                    "160000," + commit + "," + PackageRelativePath);
            }

            internal void RedirectRegistration(string repositoryUrl)
            {
                WriteGitModules(repositoryUrl);
                Run(ProjectRoot, "add", "--", ".gitmodules");
            }

            internal void RedirectOrigin(string repositoryUrl)
            {
                Run(
                    PackageRoot,
                    "remote",
                    "set-url",
                    "origin",
                    repositoryUrl);
            }

            internal void RedirectStagedRegistrationOnly(string repositoryUrl)
            {
                WriteGitModules(repositoryUrl);
                Run(ProjectRoot, "add", "--", ".gitmodules");
                WriteGitModules("https://github.com/org/root.git");
            }

            internal void AppendGitModulesComment(string comment)
            {
                File.AppendAllText(
                    Path.Combine(ProjectRoot, ".gitmodules"),
                    comment ?? string.Empty);
            }

            internal void WriteGitModulesBytes(byte[] contents)
            {
                File.WriteAllBytes(
                    Path.Combine(ProjectRoot, ".gitmodules"),
                    contents ?? Array.Empty<byte>());
            }

            internal void ReplaceGitModulesWithMatchingSymlink()
            {
                string gitModulesPath = Path.Combine(ProjectRoot, ".gitmodules");
                matchingSymlinkTargetContents = File.ReadAllBytes(gitModulesPath);
                matchingSymlinkTarget =
                    ProjectRoot + "-external-matching-gitmodules";
                File.WriteAllBytes(
                    matchingSymlinkTarget,
                    matchingSymlinkTargetContents);
                File.Delete(gitModulesPath);

                CommandResult linkResult = runner.Run(new CommandSpec
                {
                    FileName = "/bin/ln",
                    ArgumentList = new[]
                    {
                        "-s", "--", matchingSymlinkTarget, gitModulesPath
                    },
                    WorkingDirectory = ProjectRoot,
                    TimeoutMs = 10000,
                    TerminationScope = CommandTerminationScope.CompleteProcessTree,
                    RequireStrictUtf8StdOut = true
                });
                if (linkResult == null ||
                    !linkResult.TerminationConfirmed ||
                    !linkResult.IsSuccess)
                {
                    throw new InvalidOperationException(
                        "Could not create the matching .gitmodules symlink fixture: " +
                        (linkResult?.StdErr ?? string.Empty));
                }
            }

            public void Dispose()
            {
                try
                {
                    if (Directory.Exists(ProjectRoot))
                        Directory.Delete(ProjectRoot, true);
                }
                catch
                {
                    // A failing assertion should retain the original failure.
                }

                try
                {
                    if (!string.IsNullOrWhiteSpace(matchingSymlinkTarget) &&
                        File.Exists(matchingSymlinkTarget))
                    {
                        File.Delete(matchingSymlinkTarget);
                    }
                }
                catch
                {
                    // A failing assertion should retain the original failure.
                }
            }

            private string Run(string workingDirectory, params string[] arguments)
            {
                CommandResult result = runner.Run(new CommandSpec
                {
                    FileName = GitUtility.GitExecutable,
                    ArgumentList = arguments,
                    WorkingDirectory = workingDirectory,
                    TimeoutMs = 10000,
                    TerminationScope = CommandTerminationScope.CompleteProcessTree,
                    RequireStrictUtf8StdOut = true
                });
                if (result == null ||
                    !result.TerminationConfirmed ||
                    result.StdOutTruncated ||
                    result.StdErrTruncated ||
                    result.StdOutInvalidUtf8 ||
                    !result.IsSuccess)
                {
                    throw new InvalidOperationException(
                        "Git fixture command failed: " +
                        string.Join(" ", arguments) + " " +
                        (result?.StdErr ?? string.Empty));
                }

                return result.StdOut ?? string.Empty;
            }

            private void WriteGitModules(string repositoryUrl)
            {
                File.WriteAllText(
                    Path.Combine(ProjectRoot, ".gitmodules"),
                    "[submodule \"" + PackageRelativePath + "\"]\n" +
                    "\tpath = " + PackageRelativePath + "\n" +
                    "\turl = " + repositoryUrl + "\n" +
                    "\tbranch = main\n");
            }
        }

        private sealed class FakeDialog : IPackageDependencyModalDialog
        {
            internal bool ConfirmResult;
            internal int ConfirmCount;
            internal int BlockingCount;
            internal PackageDependencyPromptContent LastContent;

            public bool Confirm(PackageDependencyPromptContent content)
            {
                ConfirmCount++;
                LastContent = content;
                return ConfirmResult;
            }

            public void ShowBlocking(PackageDependencyPromptContent content)
            {
                BlockingCount++;
                LastContent = content;
            }
        }

        private sealed class FakeInstallExecutor :
            IPackageDependencyInstallExecutor
        {
            internal readonly List<PackageDependencyInstalledPackage> Registered =
                new();
            internal readonly List<PackageDependencyInstallStep> Started = new();
            internal readonly List<string> StartedOperationIds = new();
            internal Action<PackageDependencyPrimitiveCompletion> LastCallback;
            internal bool MutationBusy;
            internal bool CompleteSynchronously;
            internal readonly HashSet<string> BusyPackages =
                new(StringComparer.Ordinal);
            internal PackageDependencySubmoduleCommitVerifier
                SubmoduleCommitVerifier;
            internal PackageDependencySubmoduleCommitVerificationStatus
                SubmoduleCommitVerificationStatus =
                    PackageDependencySubmoduleCommitVerificationStatus.Expected;
            internal string SubmoduleCommitVerificationError = string.Empty;
            internal string LastVerificationScopeId = string.Empty;
            internal string LastVerificationOperationId = string.Empty;
            internal int LastVerificationStepIndex = -1;
            internal PackageDependencyInstallStep LastVerificationStep;

            public bool IsMutationBusy => MutationBusy;

            public bool IsBusyFor(string packageName)
            {
                return BusyPackages.Contains(packageName ?? string.Empty);
            }

            public bool TryInspectRegisteredPackages(
                out IReadOnlyList<PackageDependencyInstalledPackage> packages,
                out string error)
            {
                packages = Registered.ToArray();
                error = string.Empty;
                return true;
            }

            public PackageDependencySubmoduleCommitVerificationStatus
                GetSubmoduleCommitVerification(
                    string verificationScopeId,
                    string operationId,
                    int stepIndex,
                    PackageDependencyInstallStep step,
                    out string error)
            {
                LastVerificationScopeId = verificationScopeId;
                LastVerificationOperationId = operationId;
                LastVerificationStepIndex = stepIndex;
                LastVerificationStep = step;
                if (SubmoduleCommitVerifier != null)
                {
                    return SubmoduleCommitVerifier.GetOrStart(
                        verificationScopeId,
                        operationId,
                        stepIndex,
                        step,
                        out error);
                }

                error = SubmoduleCommitVerificationError;
                return SubmoduleCommitVerificationStatus;
            }

            public void CancelSubmoduleCommitVerification(
                string verificationScopeId)
            {
                SubmoduleCommitVerifier?.Cancel(verificationScopeId);
            }

            public bool TryStart(
                PackageDependencyInstallStep step,
                PackageManagerGitInstallMode mode,
                string dependencyInstallOperationId,
                Action<PackageDependencyPrimitiveCompletion> onComplete,
                out string error)
            {
                Started.Add(step);
                StartedOperationIds.Add(dependencyInstallOperationId);
                LastCallback = onComplete;
                MutationBusy = true;
                BusyPackages.Add(step.PackageName);
                error = string.Empty;
                if (CompleteSynchronously)
                    Complete(true, "Primitive completed synchronously.");
                return true;
            }

            internal void Complete(bool success, string message)
            {
                PackageDependencyInstallStep step = Started.Last();
                MutationBusy = false;
                BusyPackages.Remove(step.PackageName);
                LastCallback?.Invoke(
                    new PackageDependencyPrimitiveCompletion(
                        success,
                        step.PackageName,
                        message));
            }
        }

        private sealed class MemoryStateStore :
            IPackageDependencyInstallStateStore
        {
            internal string Active = string.Empty;
            internal string Completion = string.Empty;
            internal string RecoveryNotification = string.Empty;
            internal int SaveActiveCount;
            internal int ClearActiveCount;
            internal int SaveCompletionCount;
            internal int SaveRecoveryNotificationCount;
            internal bool ThrowOnSaveCompletion;
            internal bool ThrowOnSaveRecoveryNotification;

            public string LoadActive()
            {
                return Active;
            }

            public void SaveActive(string json)
            {
                SaveActiveCount++;
                Active = json ?? string.Empty;
            }

            public void ClearActive()
            {
                ClearActiveCount++;
                Active = string.Empty;
            }

            public string LoadCompletion()
            {
                return Completion;
            }

            public void SaveCompletion(string json)
            {
                SaveCompletionCount++;
                if (ThrowOnSaveCompletion)
                {
                    throw new InvalidOperationException(
                        "completion persistence failed");
                }
                Completion = json ?? string.Empty;
            }

            public void ClearCompletion()
            {
                Completion = string.Empty;
            }

            public string LoadRecoveryNotification()
            {
                return RecoveryNotification;
            }

            public void SaveRecoveryNotification(string value)
            {
                SaveRecoveryNotificationCount++;
                if (ThrowOnSaveRecoveryNotification)
                {
                    throw new InvalidOperationException(
                        "marker persistence failed");
                }
                RecoveryNotification = value ?? string.Empty;
            }

            public void ClearRecoveryNotification()
            {
                RecoveryNotification = string.Empty;
            }
        }

        private sealed class FakeCompletionDialog :
            IPackageDependencyInstallCompletionDialog
        {
            internal int ShowCount;
            internal PackageDependencyInstallCompletionDialogContent LastContent;

            public void Show(
                PackageDependencyInstallCompletionDialogContent content)
            {
                ShowCount++;
                LastContent = content;
            }
        }
    }
}
