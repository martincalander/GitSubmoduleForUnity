using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;

namespace MartinCalander.GitSubmoduleManager.Editor.Tests
{
    public sealed class PackageDependencyInstallWorkflowTests
    {
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

        private static PackageDependencyInstallRequest Request(
            PackageManagerGitInstallMode mode =
                PackageManagerGitInstallMode.GitSubmodule,
            IEnumerable<PackageManifestDependency> dependencies = null,
            string rootVersion = "1.0.0")
        {
            return new PackageDependencyInstallRequest(
                "https://github.com/org/root.git",
                "main",
                "com.example.root",
                rootVersion,
                mode,
                dependencies ?? Array.Empty<PackageManifestDependency>());
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
                        Array.Empty<PackageManifestDependency>()));
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
            bool hasVerifiedRepositoryIdentity = true)
        {
            return new PackageDependencyInstalledPackage(
                packageName,
                version,
                PackageDependencyInstalledSource.Embedded,
                true,
                Path.Combine(GitUtility.ProjectRoot, "Packages", packageName),
                repositoryUrl,
                string.Empty,
                hasVerifiedRepositoryIdentity);
        }

        private static PackageDependencyInstalledPackage InstalledReadOnly(
            string packageName,
            string version,
            string repositoryUrl = "https://github.com/org/root.git",
            string revision = "main",
            bool hasVerifiedRepositoryIdentity = true)
        {
            return new PackageDependencyInstalledPackage(
                packageName,
                version,
                PackageDependencyInstalledSource.Git,
                true,
                string.Empty,
                repositoryUrl,
                revision,
                hasVerifiedRepositoryIdentity);
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
            internal int SaveActiveCount;

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
                Active = string.Empty;
            }

            public string LoadCompletion()
            {
                return Completion;
            }

            public void SaveCompletion(string json)
            {
                Completion = json ?? string.Empty;
            }

            public void ClearCompletion()
            {
                Completion = string.Empty;
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
