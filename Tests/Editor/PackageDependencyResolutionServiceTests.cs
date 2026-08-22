using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;

namespace MartinCalander.GitSubmoduleManager.Editor.Tests
{
    public sealed class PackageDependencyResolutionServiceTests
    {
        [Test]
        public void RegisteredExactVersionAndSourcePackages_AreNotPlanned()
        {
            const string gitPackage = "com.example.installed-git";
            const string registryPackage = "com.unity.installed-registry";
            var facade = new FakeFacade
            {
                RegisteredPackages = new[]
                {
                    Registered(gitPackage, "1.0.0", "Git"),
                    Registered(registryPackage, "2.0.0", "Registry")
                },
                Snapshot = SuccessfulSnapshot(
                    Repository(
                        "owner",
                        "installed-git",
                        gitPackage,
                        "1.0.0"))
            };
            facade.Searches[registryPackage] = FakeSearch.Successful(
                RegistryPackage(
                    registryPackage,
                    "2.0.0",
                    true,
                    "Unity"));
            using var service = new PackageDependencyResolutionService(facade);

            Assert.That(service.TryStart(
                "com.example.root",
                new[]
                {
                    Dependency(registryPackage, "2.0.0"),
                    Dependency(gitPackage, "1.0.0")
                },
                out string error), Is.True, error);

            RunUntilComplete(service);
            Assert.That(service.Current.IsComplete, Is.True);
            Assert.That(service.Current.Results, Is.Empty);
            Assert.That(service.Current.HasMissingDependencies, Is.False);
            Assert.That(facade.SearchCalls, Is.Empty);
        }

        [Test]
        public void InstalledRegistrySourceAtExactVersion_IsAlreadySatisfied()
        {
            const string packageName = "com.example.installed-source-conflict";
            var facade = new FakeFacade
            {
                RegisteredPackages = new[]
                {
                    Registered(packageName, "1.0.0", "Registry")
                },
                Snapshot = SuccessfulSnapshot(
                    Repository("owner", "expected", packageName, "1.0.0"))
            };
            facade.Searches[packageName] = FakeSearch.Successful(
                RegistryPackage(
                    packageName,
                    "1.0.0",
                    false,
                    "Fallback Registry"));
            using var service = new PackageDependencyResolutionService(facade);

            Assert.That(service.TryStart(
                "com.example.root",
                new[] { Dependency(packageName, "1.0.0") },
                out string error), Is.True, error);
            RunUntilComplete(service);

            Assert.That(service.Current.Results, Is.Empty);
            Assert.That(service.Current.HasMissingDependencies, Is.False);
            Assert.That(facade.SearchCalls, Is.Empty);
        }

        [Test]
        public void InstalledGitSourceAtExactVersion_IsAlreadySatisfied()
        {
            const string packageName = "com.example.installed-git-conflict";
            var facade = new FakeFacade
            {
                RegisteredPackages = new[]
                {
                    Registered(packageName, "1.0.0", "Git")
                },
                Snapshot = SuccessfulSnapshot()
            };
            facade.Searches[packageName] = FakeSearch.Successful(
                RegistryPackage(
                    packageName,
                    "1.0.0",
                    false,
                    "Company Registry"));
            using var service = new PackageDependencyResolutionService(facade);

            Assert.That(service.TryStart(
                "com.example.root",
                new[] { Dependency(packageName, "1.0.0") },
                out string error), Is.True, error);
            RunUntilComplete(service);

            Assert.That(service.Current.Results, Is.Empty);
            Assert.That(service.Current.HasMissingDependencies, Is.False);
            Assert.That(facade.SearchCalls, Is.Empty);
        }

        [Test]
        public void RegisteredVersionMismatch_IsABlockingConflictWithoutSearch()
        {
            const string packageName = "com.example.installed";
            var facade = new FakeFacade
            {
                RegisteredPackages = new[]
                {
                    Registered(packageName, "2.0.0", "Registry")
                },
                Snapshot = SuccessfulSnapshot()
            };
            using var service = new PackageDependencyResolutionService(facade);

            Assert.That(service.TryStart(
                "com.example.root",
                new[] { Dependency(packageName, "1.0.0") },
                out string error), Is.True, error);
            Assert.That(service.Tick(), Is.True);

            PackageDependencyResolutionResult result = service.Current.Results.Single();
            Assert.That(service.Current.HasBlockingIssues, Is.True);
            Assert.That(result.Status,
                Is.EqualTo(PackageDependencyResolutionStatus.Unresolved));
            Assert.That(result.Message, Does.Contain("Installed"));
            Assert.That(result.Message, Does.Contain("2.0.0"));
            Assert.That(result.Message, Does.Contain("Registry"));
            Assert.That(result.Message, Does.Contain("required version 1.0.0"));
            Assert.That(facade.SearchCalls, Is.Empty);
        }

        [Test]
        public void RegisteredUnknownSourceAtExactVersion_IsAlreadySatisfied()
        {
            const string packageName = "com.example.unknown-source";
            var facade = new FakeFacade
            {
                RegisteredPackages = new[]
                {
                    Registered(packageName, "1.0.0", "Unknown")
                },
                Snapshot = SuccessfulSnapshot()
            };
            using var service = new PackageDependencyResolutionService(facade);

            Assert.That(service.TryStart(
                "com.example.root",
                new[] { Dependency(packageName, "1.0.0") },
                out string error), Is.True, error);
            Assert.That(service.Tick(), Is.True);

            Assert.That(service.Current.Results, Is.Empty);
            Assert.That(service.Current.HasMissingDependencies, Is.False);
            Assert.That(facade.SearchCalls, Is.Empty);
        }

        [Test]
        public void LegacyNameOnlyInspection_CannotSilentlySkipInstalledPackage()
        {
            const string packageName = "com.example.name-only";
            var facade = new LegacyNameOnlyFacade(packageName);
            using var service = new PackageDependencyResolutionService(facade);

            Assert.That(service.TryStart(
                "com.example.root",
                new[] { Dependency(packageName, "1.0.0") },
                out string error), Is.True, error);
            Assert.That(service.Tick(), Is.True);

            PackageDependencyResolutionResult result = service.Current.Results.Single();
            Assert.That(result.Status,
                Is.EqualTo(PackageDependencyResolutionStatus.Unresolved));
            Assert.That(result.Message, Does.Contain("incomplete version"));
            Assert.That(facade.SearchCalls, Is.EqualTo(0));
        }

        [Test]
        public void UnityPackage_SearchesRegistryWithoutConsultingGitHubPriority()
        {
            const string packageName = "com.unity.example";
            var search = new FakeSearch();
            var facade = new FakeFacade
            {
                Snapshot = LoadingSnapshot(
                    Repository("unity-owner", "unity-repo", packageName, "1.0.0"))
            };
            facade.Searches[packageName] = search;
            using var service = new PackageDependencyResolutionService(facade);
            Assert.That(service.TryStart(
                "com.example.root",
                new[] { Dependency(packageName, "1.0.0") },
                out string error), Is.True, error);

            Assert.That(service.Tick(), Is.True);
            Assert.That(facade.SearchCalls, Is.EqualTo(new[] { packageName }));
            Assert.That(service.Current.IsComplete, Is.False);

            search.CompleteSuccess(RegistryPackage(
                packageName,
                "1.0.0",
                true,
                "Unity"));
            Assert.That(service.Tick(), Is.True);

            PackageDependencyResolutionResult result = service.Current.Results.Single();
            Assert.That(service.Current.IsComplete, Is.True);
            Assert.That(result.Status, Is.EqualTo(PackageDependencyResolutionStatus.Resolved));
            Assert.That(
                result.SelectedCandidate.Source,
                Is.EqualTo(PackageDependencyCandidateSource.UnityRegistry));
        }

        [Test]
        public void TransitiveCustomDependency_StartsGitHubDiscoveryLazily()
        {
            const string unityPackage = "com.unity.parent";
            const string customPackage = "com.example.transitive";
            var facade = new FakeFacade
            {
                Snapshot = LoadingSnapshot()
            };
            facade.Searches[unityPackage] = FakeSearch.Successful(
                RegistryPackageAt(
                    unityPackage,
                    "1.0.0",
                    true,
                    "Unity Registry",
                    "https://packages.unity.com",
                    Dependency(customPackage, "2.0.0")));
            using var service = new PackageDependencyResolutionService(facade);

            Assert.That(service.TryStart(
                "com.example.root",
                new[] { Dependency(unityPackage, "1.0.0") },
                out string error), Is.True, error);
            Assert.That(service.Tick(), Is.True);
            Assert.That(facade.EnsureGitHubDiscoveryCalls, Is.EqualTo(0));

            Assert.That(service.Tick(), Is.True);
            Assert.That(facade.EnsureGitHubDiscoveryCalls, Is.EqualTo(1));
            Assert.That(service.Current.IsComplete, Is.False);
            Assert.That(service.Current.Results.Single(result =>
                    result.Requirement.Name == customPackage).Status,
                Is.EqualTo(PackageDependencyResolutionStatus.Pending));
            Assert.That(facade.SearchCalls, Is.EqualTo(new[] { unityPackage }));
        }

        [Test]
        public void CustomGitHubMatch_WaitsForCompleteCatalogueBeforeResolving()
        {
            const string packageName = "com.example.shared";
            var facade = new FakeFacade
            {
                Snapshot = LoadingSnapshot(
                    Repository("owner", "shared", packageName, "1.2.3"))
            };
            using var service = new PackageDependencyResolutionService(facade);
            Assert.That(service.TryStart(
                "com.example.root",
                new[] { Dependency(packageName, "1.2.3") },
                out string error), Is.True, error);

            Assert.That(service.Tick(), Is.False);
            Assert.That(service.Current.IsComplete, Is.False);
            Assert.That(service.Current.Results.Single().Status,
                Is.EqualTo(PackageDependencyResolutionStatus.Pending));
            Assert.That(facade.SearchCalls, Is.Empty);

            facade.Snapshot = SuccessfulSnapshot(
                Repository("owner", "shared", packageName, "1.2.3"));
            Assert.That(service.Tick(), Is.True);

            PackageDependencyResolutionResult result = service.Current.Results.Single();
            Assert.That(service.Current.IsComplete, Is.True);
            Assert.That(result.Status, Is.EqualTo(PackageDependencyResolutionStatus.Resolved));
            Assert.That(
                result.SelectedCandidate.Source,
                Is.EqualTo(PackageDependencyCandidateSource.GitHub));
            Assert.That(result.SelectedCandidate.SourceName, Is.EqualTo("owner/shared"));
            Assert.That(
                result.SelectedCandidate.RepositoryUrl,
                Is.EqualTo("https://github.com/owner/shared.git"));
            Assert.That(result.SelectedCandidate.RepositoryBranch, Is.EqualTo("main"));
            Assert.That(facade.SearchCalls, Is.Empty);
        }

        [TestCase("", "main")]
        [TestCase("https://github.com/another/repository.git", "main")]
        [TestCase("https://github.com/owner/repository.git", "")]
        [TestCase("https://github.com/owner/repository.git", ".")]
        public void IncompleteGitHubInstallIdentity_IsUnresolvedWithoutRegistryFallback(
            string repositoryUrl,
            string defaultBranch)
        {
            const string packageName = "com.example.incomplete-github";
            var facade = new FakeFacade
            {
                Snapshot = SuccessfulSnapshot(
                    RepositoryAt(
                        "owner",
                        "repository",
                        packageName,
                        "1.0.0",
                        repositoryUrl,
                        defaultBranch))
            };
            facade.Searches[packageName] = FakeSearch.Successful(
                RegistryPackage(
                    packageName,
                    "1.0.0",
                    false,
                    "Fallback Registry"));
            using var service = new PackageDependencyResolutionService(facade);

            Assert.That(service.TryStart(
                "com.example.root",
                new[] { Dependency(packageName, "1.0.0") },
                out string error), Is.True, error);
            Assert.That(service.Tick(), Is.True);

            PackageDependencyResolutionResult result = service.Current.Results.Single();
            Assert.That(result.Status,
                Is.EqualTo(PackageDependencyResolutionStatus.Unresolved));
            Assert.That(result.Message, Does.Contain("exact repository URL"));
            Assert.That(result.Message, Does.Contain("explicit valid default branch"));
            Assert.That(result.Message, Does.Contain("Registry search was skipped"));
            Assert.That(facade.SearchCalls, Is.Empty);
        }

        [Test]
        public void CustomPackage_WaitsForDiscoveryBeforeProvenAbsenceStartsRegistrySearch()
        {
            const string packageName = "com.example.registry";
            var search = new FakeSearch();
            var facade = new FakeFacade
            {
                Snapshot = LoadingSnapshot()
            };
            facade.Searches[packageName] = search;
            using var service = new PackageDependencyResolutionService(facade);
            Assert.That(service.TryStart(
                "com.example.root",
                new[] { Dependency(packageName, "3.0.0") },
                out string error), Is.True, error);

            Assert.That(service.Tick(), Is.False);
            Assert.That(facade.SearchCalls, Is.Empty);
            Assert.That(service.Current.Results.Single().Status,
                Is.EqualTo(PackageDependencyResolutionStatus.Pending));

            facade.Snapshot = SuccessfulSnapshot();
            Assert.That(service.Tick(), Is.True);
            Assert.That(facade.SearchCalls, Is.EqualTo(new[] { packageName }));
            Assert.That(service.Current.IsComplete, Is.False);

            search.CompleteSuccess(RegistryPackage(
                packageName,
                "3.0.0",
                false,
                "Company Registry"));
            Assert.That(service.Tick(), Is.True);
            Assert.That(service.Current.IsComplete, Is.True);
        }

        [Test]
        public void FailedDiscovery_LeavesCustomDependencyUnresolvedWithoutRegistrySearch()
        {
            const string secret = "https://user:secret@example.com/catalogue";
            var facade = new FakeFacade
            {
                Snapshot = TerminalSnapshot(
                    Array.Empty<PackageManagerGitHubRepository>(),
                    secret,
                    completedOwners: 0,
                    totalOwners: 1)
            };
            using var service = new PackageDependencyResolutionService(facade);
            Assert.That(service.TryStart(
                "com.example.root",
                new[] { Dependency("com.example.missing", "1.0.0") },
                out string error), Is.True, error);

            Assert.That(service.Tick(), Is.True);

            PackageDependencyResolutionResult result = service.Current.Results.Single();
            Assert.That(result.Status, Is.EqualTo(PackageDependencyResolutionStatus.Unresolved));
            Assert.That(result.Message, Does.Not.Contain("secret"));
            Assert.That(result.Message, Does.Not.Contain("user:"));
            Assert.That(facade.SearchCalls, Is.Empty);
        }

        [Test]
        public void IncompleteDiscovery_LeavesCustomDependencyUnresolvedWithoutRegistrySearch()
        {
            var facade = new FakeFacade
            {
                Snapshot = PackageManagerGitHubDiscoverySnapshot.Empty
            };
            using var service = new PackageDependencyResolutionService(facade);
            Assert.That(service.TryStart(
                "com.example.root",
                new[] { Dependency("com.example.missing", "1.0.0") },
                out string error), Is.True, error);

            Assert.That(service.Tick(), Is.True);

            Assert.That(service.Current.Results.Single().Status,
                Is.EqualTo(PackageDependencyResolutionStatus.Unresolved));
            Assert.That(service.Current.Results.Single().Message,
                Does.Contain("absence was not proven"));
            Assert.That(facade.SearchCalls, Is.Empty);
        }

        [Test]
        public void UnavailableGitHubManifest_PreventsAbsenceProofAndRegistryFallback()
        {
            const string packageName = "com.example.coverage-gap";
            var facade = new FakeFacade
            {
                Snapshot = TerminalSnapshot(
                    Array.Empty<PackageManagerGitHubRepository>(),
                    string.Empty,
                    completedOwners: 1,
                    totalOwners: 1,
                    unavailableManifestCount: 1)
            };
            facade.Searches[packageName] = FakeSearch.Successful(
                RegistryPackage(packageName, "1.0.0", false, "Fallback Registry"));
            using var service = new PackageDependencyResolutionService(facade);

            Assert.That(service.TryStart(
                "com.example.root",
                new[] { Dependency(packageName, "1.0.0") },
                out string error), Is.True, error);
            Assert.That(service.Tick(), Is.True);

            PackageDependencyResolutionResult result = service.Current.Results.Single();
            Assert.That(result.Status,
                Is.EqualTo(PackageDependencyResolutionStatus.Unresolved));
            Assert.That(result.Message, Does.Contain("could not validate package.json"));
            Assert.That(result.Message, Does.Contain("absence was not proven"));
            Assert.That(facade.SearchCalls, Is.Empty);
        }

        [Test]
        public void OrganizationEnumerationWarning_PreventsGitHubChoiceAndRegistryFallback()
        {
            const string packageName = "com.example.partial-owner-catalogue";
            const string secret = "https://user:secret@example.com/orgs";
            var facade = new FakeFacade
            {
                Snapshot = TerminalSnapshot(
                    new[]
                    {
                        Repository("personal", "candidate", packageName, "1.0.0")
                    },
                    string.Empty,
                    completedOwners: 1,
                    totalOwners: 1,
                    coverageWarning: secret)
            };
            facade.Searches[packageName] = FakeSearch.Successful(
                RegistryPackage(packageName, "1.0.0", false, "Fallback Registry"));
            using var service = new PackageDependencyResolutionService(facade);

            Assert.That(service.TryStart(
                "com.example.root",
                new[] { Dependency(packageName, "1.0.0") },
                out string error), Is.True, error);
            Assert.That(service.Tick(), Is.True);

            PackageDependencyResolutionResult result = service.Current.Results.Single();
            Assert.That(result.Status,
                Is.EqualTo(PackageDependencyResolutionStatus.Unresolved));
            Assert.That(result.Message, Does.Contain("could not inspect every owner"));
            Assert.That(result.Message, Does.Contain("absence was not proven"));
            Assert.That(result.Message, Does.Not.Contain("secret"));
            Assert.That(result.Message, Does.Not.Contain("user:"));
            Assert.That(facade.SearchCalls, Is.Empty);
        }

        [Test]
        public void DuplicateGitHubPackageNames_AreAmbiguousAndNeverSearchRegistry()
        {
            const string packageName = "com.example.duplicate";
            var facade = new FakeFacade
            {
                Snapshot = SuccessfulSnapshot(
                    Repository("alpha", "first", packageName, "1.0.0"),
                    Repository("beta", "second", packageName, "1.0.0"))
            };
            using var service = new PackageDependencyResolutionService(facade);
            Assert.That(service.TryStart(
                "com.example.root",
                new[] { Dependency(packageName, "1.0.0") },
                out string error), Is.True, error);

            Assert.That(service.Tick(), Is.True);

            PackageDependencyResolutionResult result = service.Current.Results.Single();
            Assert.That(result.Status, Is.EqualTo(PackageDependencyResolutionStatus.Ambiguous));
            Assert.That(
                result.Candidates.Select(candidate => candidate.SourceName),
                Is.EqualTo(new[] { "alpha/first", "beta/second" }));
            Assert.That(facade.SearchCalls, Is.Empty);
        }

        [Test]
        public void GitHubVersionMismatch_IsUnresolvedAndDoesNotFallThroughToRegistry()
        {
            const string packageName = "com.example.mismatch";
            var facade = new FakeFacade
            {
                Snapshot = SuccessfulSnapshot(
                    Repository("owner", "repo", packageName, "2.0.0"))
            };
            using var service = new PackageDependencyResolutionService(facade);
            Assert.That(service.TryStart(
                "com.example.root",
                new[] { Dependency(packageName, "1.0.0") },
                out string error), Is.True, error);

            Assert.That(service.Tick(), Is.True);

            PackageDependencyResolutionResult result = service.Current.Results.Single();
            Assert.That(result.Status, Is.EqualTo(PackageDependencyResolutionStatus.Unresolved));
            Assert.That(result.Message, Does.Contain("Registry search was skipped"));
            Assert.That(result.Candidates, Has.Count.EqualTo(1));
            Assert.That(facade.SearchCalls, Is.Empty);
        }

        [Test]
        public void RegistryResults_DistinguishUnityDefaultAndCustomSources()
        {
            const string customName = "com.example.custom";
            const string unityName = "com.unity.feature";
            var customSearch = FakeSearch.Successful(RegistryPackage(
                customName,
                "1.0.0",
                false,
                "Acme Registry"));
            var unitySearch = FakeSearch.Successful(RegistryPackage(
                unityName,
                "2.0.0",
                true,
                "Unity"));
            var facade = new FakeFacade
            {
                Snapshot = SuccessfulSnapshot()
            };
            facade.Searches[customName] = customSearch;
            facade.Searches[unityName] = unitySearch;
            using var service = new PackageDependencyResolutionService(facade);
            Assert.That(service.TryStart(
                "com.example.root",
                new[]
                {
                    Dependency(unityName, "2.0.0"),
                    Dependency(customName, "1.0.0")
                },
                out string error), Is.True, error);

            RunUntilComplete(service);

            Assert.That(
                service.Current.Results.Select(result => result.Requirement.Name),
                Is.EqualTo(new[] { customName, unityName }));
            Assert.That(
                Find(service.Current, customName).SelectedCandidate.Source,
                Is.EqualTo(PackageDependencyCandidateSource.CustomRegistry));
            Assert.That(
                Find(service.Current, unityName).SelectedCandidate.Source,
                Is.EqualTo(PackageDependencyCandidateSource.UnityRegistry));
            Assert.That(
                facade.SearchCalls,
                Is.EqualTo(new[] { customName, unityName }));
        }

        [Test]
        public void RegistryVersionMismatch_IsUnresolved()
        {
            const string packageName = "com.unity.versioned";
            var facade = new FakeFacade
            {
                Snapshot = SuccessfulSnapshot()
            };
            facade.Searches[packageName] = FakeSearch.Successful(
                RegistryPackage(packageName, "2.0.0", true, "Unity"));
            using var service = new PackageDependencyResolutionService(facade);
            Assert.That(service.TryStart(
                "com.example.root",
                new[] { Dependency(packageName, "1.0.0") },
                out string error), Is.True, error);

            RunUntilComplete(service);

            PackageDependencyResolutionResult result = service.Current.Results.Single();
            Assert.That(result.Status, Is.EqualTo(PackageDependencyResolutionStatus.Unresolved));
            Assert.That(result.Message, Does.Contain("requested version 1.0.0"));
        }

        [Test]
        public void SameRegistryDisplayNameWithDifferentUrls_RemainsAmbiguous()
        {
            const string packageName = "com.example.registry-identity";
            const string firstUrl = "https://packages.alpha.example.com";
            const string secondUrl = "https://packages.beta.example.com";
            var facade = new FakeFacade
            {
                Snapshot = SuccessfulSnapshot()
            };
            facade.Searches[packageName] = FakeSearch.Successful(
                RegistryPackageAt(
                    packageName,
                    "1.0.0",
                    false,
                    "Company Registry",
                    secondUrl),
                RegistryPackageAt(
                    packageName,
                    "1.0.0",
                    false,
                    "Company Registry",
                    firstUrl));
            using var service = new PackageDependencyResolutionService(facade);

            Assert.That(service.TryStart(
                "com.example.root",
                new[] { Dependency(packageName, "1.0.0") },
                out string error), Is.True, error);
            RunUntilComplete(service);

            PackageDependencyResolutionResult result = service.Current.Results.Single();
            Assert.That(result.Status,
                Is.EqualTo(PackageDependencyResolutionStatus.Ambiguous));
            Assert.That(result.Candidates, Has.Count.EqualTo(2));
            Assert.That(
                result.Candidates.Select(candidate => candidate.SourceName),
                Is.EqualTo(new[] { "Company Registry", "Company Registry" }));
            Assert.That(
                result.Candidates.Select(candidate => candidate.SourceIdentity),
                Is.EqualTo(new[] { firstUrl, secondUrl }));
        }

        [Test]
        public void RegistryWithoutStableUrl_IsUnresolved()
        {
            const string packageName = "com.example.registry-without-url";
            var facade = new FakeFacade
            {
                Snapshot = SuccessfulSnapshot()
            };
            facade.Searches[packageName] = FakeSearch.Successful(
                RegistryPackageAt(
                    packageName,
                    "1.0.0",
                    false,
                    "Unnamed Identity",
                    string.Empty));
            using var service = new PackageDependencyResolutionService(facade);

            Assert.That(service.TryStart(
                "com.example.root",
                new[] { Dependency(packageName, "1.0.0") },
                out string error), Is.True, error);
            RunUntilComplete(service);

            PackageDependencyResolutionResult result = service.Current.Results.Single();
            Assert.That(result.Status,
                Is.EqualTo(PackageDependencyResolutionStatus.Unresolved));
            Assert.That(result.Message, Does.Contain("stable registry URL"));
            Assert.That(result.Message, Does.Contain("could not be verified"));
        }

        [Test]
        public void ConflictingMetadataFromSameRegistryUrl_IsAmbiguousAndNotExpanded()
        {
            const string packageName = "com.example.conflicting-metadata";
            const string firstDependency = "com.example.child-alpha";
            const string secondDependency = "com.example.child-beta";
            const string registryUrl = "https://packages.example.com";
            var facade = new FakeFacade
            {
                Snapshot = SuccessfulSnapshot()
            };
            facade.Searches[packageName] = FakeSearch.Successful(
                RegistryPackageAt(
                    packageName,
                    "1.0.0",
                    false,
                    "Company Registry",
                    registryUrl,
                    Dependency(secondDependency, "1.0.0")),
                RegistryPackageAt(
                    packageName,
                    "1.0.0",
                    false,
                    "Company Registry",
                    registryUrl,
                    Dependency(firstDependency, "1.0.0")));
            using var service = new PackageDependencyResolutionService(facade);

            Assert.That(service.TryStart(
                "com.example.root",
                new[] { Dependency(packageName, "1.0.0") },
                out string error), Is.True, error);
            RunUntilComplete(service);

            PackageDependencyResolutionResult result = service.Current.Results.Single();
            Assert.That(result.Status,
                Is.EqualTo(PackageDependencyResolutionStatus.Ambiguous));
            Assert.That(result.Candidates, Has.Count.EqualTo(2));
            Assert.That(service.Current.Results.Any(candidate =>
                candidate.Requirement.Name == firstDependency), Is.False);
            Assert.That(service.Current.Results.Any(candidate =>
                candidate.Requirement.Name == secondDependency), Is.False);
        }

        [Test]
        public void RequestedVersionInVersionListWithDifferentMetadata_IsUnresolvedAndNotExpanded()
        {
            const string packageName = "com.example.metadata-version";
            const string incorrectDependency = "com.example.must-not-expand";
            var facade = new FakeFacade
            {
                Snapshot = SuccessfulSnapshot()
            };
            facade.Searches[packageName] = FakeSearch.Successful(
                new PackageDependencyRegistryPackage(
                    packageName,
                    "2.0.0",
                    false,
                    "Company Registry",
                    new[] { "1.0.0", "2.0.0" },
                    new[] { Dependency(incorrectDependency, "9.0.0") },
                    "https://packages.example.com"));
            using var service = new PackageDependencyResolutionService(facade);

            Assert.That(service.TryStart(
                "com.example.root",
                new[] { Dependency(packageName, "1.0.0") },
                out string error), Is.True, error);
            RunUntilComplete(service);

            Assert.That(service.Current.Results, Has.Count.EqualTo(1));
            Assert.That(service.Current.Results.Any(result =>
                result.Requirement.Name == incorrectDependency), Is.False);
            PackageDependencyResolutionResult result = service.Current.Results.Single();
            Assert.That(result.Status,
                Is.EqualTo(PackageDependencyResolutionStatus.Unresolved));
            Assert.That(result.Message, Does.Contain("requested version 1.0.0"));
            Assert.That(result.Message, Does.Contain("metadata describes version 2.0.0"));
            Assert.That(result.Message, Does.Contain("was not expanded"));
        }

        [Test]
        public void SearchFailure_IsSanitizedAndTerminal()
        {
            const string packageName = "com.example.failure";
            var search = new FakeSearch();
            var facade = new FakeFacade
            {
                Snapshot = SuccessfulSnapshot()
            };
            facade.Searches[packageName] = search;
            using var service = new PackageDependencyResolutionService(facade);
            Assert.That(service.TryStart(
                "com.example.root",
                new[] { Dependency(packageName, "1.0.0") },
                out string error), Is.True, error);

            Assert.That(service.Tick(), Is.True);
            search.CompleteFailure(
                "https://user:secret@example.com/feed?access_token=hidden");
            Assert.That(service.Tick(), Is.True);

            PackageDependencyResolutionResult result = service.Current.Results.Single();
            Assert.That(result.Status, Is.EqualTo(PackageDependencyResolutionStatus.Unresolved));
            Assert.That(result.Message, Does.Not.Contain("secret"));
            Assert.That(result.Message, Does.Not.Contain("hidden"));
            Assert.That(result.Message, Does.Not.Contain("user:"));
        }

        [Test]
        public void TransitiveGitHubCycle_IsDeduplicatedAndDeterministicallyOrdered()
        {
            const string packageA = "com.example.alpha";
            const string packageB = "com.example.beta";
            var facade = new FakeFacade
            {
                Snapshot = SuccessfulSnapshot(
                    Repository(
                        "owner",
                        "beta",
                        packageB,
                        "1.0.0",
                        Dependency(packageA, "1.0.0")),
                    Repository(
                        "owner",
                        "alpha",
                        packageA,
                        "1.0.0",
                        Dependency(packageB, "1.0.0")))
            };
            using var service = new PackageDependencyResolutionService(facade);
            Assert.That(service.TryStart(
                "com.example.root",
                new[]
                {
                    Dependency(packageB, "1.0.0"),
                    Dependency(packageB, "1.0.0")
                },
                out string error), Is.True, error);

            Assert.That(service.Tick(), Is.True);

            Assert.That(service.Current.IsComplete, Is.True);
            Assert.That(
                service.Current.Results.Select(result => result.Requirement.Name),
                Is.EqualTo(new[] { packageA, packageB }));
            Assert.That(service.Current.Results.All(result =>
                result.Status == PackageDependencyResolutionStatus.Resolved), Is.True);
            Assert.That(
                Find(service.Current, packageB).Requirement.RequestedBy,
                Is.EqualTo(new[] { "com.example.alpha", "com.example.root" }));
            Assert.That(facade.SearchCalls, Is.Empty);
        }

        [Test]
        public void ConflictingDuplicateRequirements_AreOneBlockingResult()
        {
            const string packageName = "com.example.conflict";
            var forwardFacade = new FakeFacade
            {
                Snapshot = SuccessfulSnapshot()
            };
            var reverseFacade = new FakeFacade
            {
                Snapshot = SuccessfulSnapshot()
            };
            using var forward = new PackageDependencyResolutionService(
                forwardFacade);
            using var reverse = new PackageDependencyResolutionService(
                reverseFacade);
            Assert.That(forward.TryStart(
                "com.example.root",
                new[]
                {
                    Dependency(packageName, "1.0.0"),
                    Dependency(packageName, "2.0.0")
                },
                out string error), Is.True, error);
            Assert.That(reverse.TryStart(
                "com.example.root",
                new[]
                {
                    Dependency(packageName, "2.0.0"),
                    Dependency(packageName, "1.0.0")
                },
                out error), Is.True, error);

            Assert.That(forward.Tick(), Is.True);
            Assert.That(reverse.Tick(), Is.True);

            Assert.That(forward.Current.Results, Has.Count.EqualTo(1));
            Assert.That(reverse.Current.Results, Has.Count.EqualTo(1));
            Assert.That(forward.Current.Results[0].Status,
                Is.EqualTo(PackageDependencyResolutionStatus.Unresolved));
            Assert.That(reverse.Current.Results[0].Status,
                Is.EqualTo(PackageDependencyResolutionStatus.Unresolved));
            Assert.That(forward.Current.Results[0].Requirement.Version,
                Is.EqualTo("1.0.0"));
            Assert.That(reverse.Current.Results[0].Requirement.Version,
                Is.EqualTo("1.0.0"));
            Assert.That(forward.Current.Results[0].Message,
                Does.Contain("conflicting versions"));
            Assert.That(reverse.Current.Results[0].Message,
                Is.EqualTo(forward.Current.Results[0].Message));
            Assert.That(forwardFacade.SearchCalls, Is.Empty);
            Assert.That(reverseFacade.SearchCalls, Is.Empty);
        }

        [Test]
        public void RequirementGraph_StopsAtDeterministicSafetyBound()
        {
            var dependencies = Enumerable.Range(
                    0,
                    PackageDependencyResolutionService.MaximumRequirementCount + 1)
                .Select(index => Dependency(
                    $"com.example.dependency{index:D3}",
                    "1.0.0"))
                .ToArray();
            var facade = new FakeFacade
            {
                Snapshot = SuccessfulSnapshot()
            };
            using var service = new PackageDependencyResolutionService(facade);

            Assert.That(service.TryStart(
                "com.example.root",
                dependencies,
                out string error), Is.False);

            Assert.That(error, Does.Contain("512-package"));
            Assert.That(service.Current.IsComplete, Is.True);
            Assert.That(service.Current.Results, Has.Count.EqualTo(
                PackageDependencyResolutionService.MaximumRequirementCount));
            Assert.That(service.Current.ErrorMessage, Does.Contain("512-package"));
            Assert.That(facade.SearchCalls, Is.Empty);
        }

        private static void RunUntilComplete(
            PackageDependencyResolutionService service)
        {
            for (int iteration = 0;
                 iteration < 32 && !service.Current.IsComplete;
                 iteration++)
            {
                service.Tick();
            }

            Assert.That(service.Current.IsComplete, Is.True,
                "The manually ticked resolver did not reach a terminal plan.");
        }

        private static PackageDependencyResolutionResult Find(
            PackageDependencyResolutionPlan plan,
            string packageName)
        {
            return plan.Results.Single(result =>
                result.Requirement.Name == packageName);
        }

        private static PackageManifestDependency Dependency(
            string name,
            string version)
        {
            return new PackageManifestDependency(name, version);
        }

        private static PackageDependencyRegisteredPackage Registered(
            string name,
            string version,
            string source)
        {
            return new PackageDependencyRegisteredPackage(name, version, source);
        }

        private static PackageManagerGitHubRepository Repository(
            string owner,
            string repositoryName,
            string packageName,
            string version,
            params PackageManifestDependency[] dependencies)
        {
            return RepositoryAt(
                owner,
                repositoryName,
                packageName,
                version,
                $"https://github.com/{owner}/{repositoryName}.git",
                "main",
                dependencies);
        }

        private static PackageManagerGitHubRepository RepositoryAt(
            string owner,
            string repositoryName,
            string packageName,
            string version,
            string repositoryUrl,
            string defaultBranch,
            params PackageManifestDependency[] dependencies)
        {
            return new PackageManagerGitHubRepository(new GitHubRepo
            {
                NodeId = owner + "-" + repositoryName,
                Owner = owner,
                Name = repositoryName,
                Url = repositoryUrl,
                DefaultBranch = defaultBranch,
                DeclaredPackageName = packageName,
                DeclaredVersion = version,
                DeclaredDependencies = dependencies ??
                                       Array.Empty<PackageManifestDependency>(),
                ManifestState = PackageManifestState.Valid
            });
        }

        private static PackageManagerGitHubDiscoverySnapshot LoadingSnapshot(
            params PackageManagerGitHubRepository[] repositories)
        {
            return new PackageManagerGitHubDiscoverySnapshot(
                repositories,
                true,
                "Loading GitHub packages...",
                string.Empty,
                0,
                0,
                1,
                0,
                1);
        }

        private static PackageManagerGitHubDiscoverySnapshot SuccessfulSnapshot(
            params PackageManagerGitHubRepository[] repositories)
        {
            return TerminalSnapshot(
                repositories,
                string.Empty,
                completedOwners: 1,
                totalOwners: 1);
        }

        private static PackageManagerGitHubDiscoverySnapshot TerminalSnapshot(
            IReadOnlyList<PackageManagerGitHubRepository> repositories,
            string error,
            int completedOwners,
            int totalOwners,
            int unavailableManifestCount = 0,
            string coverageWarning = "")
        {
            return new PackageManagerGitHubDiscoverySnapshot(
                repositories,
                false,
                string.IsNullOrEmpty(error)
                    ? "GitHub package discovery complete."
                    : "GitHub package discovery failed.",
                error,
                1,
                completedOwners,
                totalOwners,
                unavailableManifestCount,
                2,
                coverageWarning);
        }

        private static PackageDependencyRegistryPackage RegistryPackage(
            string packageName,
            string version,
            bool isDefault,
            string registryName,
            params PackageManifestDependency[] dependencies)
        {
            string registryKey = registryName
                .ToLowerInvariant()
                .Replace(" ", "-");
            return RegistryPackageAt(
                packageName,
                version,
                isDefault,
                registryName,
                $"https://{registryKey}.example.com",
                dependencies);
        }

        private static PackageDependencyRegistryPackage RegistryPackageAt(
            string packageName,
            string version,
            bool isDefault,
            string registryName,
            string registryUrl,
            params PackageManifestDependency[] dependencies)
        {
            return new PackageDependencyRegistryPackage(
                packageName,
                version,
                isDefault,
                registryName,
                new[] { version },
                dependencies,
                registryUrl);
        }

        private sealed class FakeFacade :
            IPackageDependencyResolutionFacade,
            IPackageDependencyRegisteredPackageFacade,
            IPackageDependencyGitHubDiscoveryStarter
        {
            internal IReadOnlyList<PackageDependencyRegisteredPackage>
                RegisteredPackages =
                    Array.Empty<PackageDependencyRegisteredPackage>();
            internal PackageManagerGitHubDiscoverySnapshot Snapshot =
                PackageManagerGitHubDiscoverySnapshot.Empty;
            internal readonly Dictionary<string, FakeSearch> Searches =
                new(StringComparer.Ordinal);
            internal readonly List<string> SearchCalls = new();
            internal string RegisteredInspectionError = string.Empty;
            internal int EnsureGitHubDiscoveryCalls;

            public PackageManagerGitHubDiscoverySnapshot GitHubSnapshot => Snapshot;

            public void EnsureGitHubDiscoveryStarted()
            {
                EnsureGitHubDiscoveryCalls++;
            }

            public bool TryGetRegisteredPackageNames(
                out IReadOnlyList<string> packageNames,
                out string error)
            {
                packageNames = RegisteredPackages
                    .Select(package => package.Name)
                    .ToArray();
                error = RegisteredInspectionError;
                return string.IsNullOrEmpty(error);
            }

            public bool TryGetRegisteredPackages(
                out IReadOnlyList<PackageDependencyRegisteredPackage> packages,
                out string error)
            {
                packages = RegisteredPackages;
                error = RegisteredInspectionError;
                return string.IsNullOrEmpty(error);
            }

            public bool TryStartRegistrySearch(
                string packageName,
                out IPackageDependencyRegistrySearch search,
                out string error)
            {
                SearchCalls.Add(packageName);
                error = string.Empty;
                if (!Searches.TryGetValue(packageName, out FakeSearch fake))
                {
                    search = null;
                    error = "No fake registry search was configured for " +
                            packageName + ".";
                    return false;
                }

                search = fake;
                return true;
            }
        }

        private sealed class LegacyNameOnlyFacade :
            IPackageDependencyResolutionFacade
        {
            private readonly string packageName;

            internal LegacyNameOnlyFacade(string packageName)
            {
                this.packageName = packageName;
            }

            internal int SearchCalls { get; private set; }

            public PackageManagerGitHubDiscoverySnapshot GitHubSnapshot =>
                SuccessfulSnapshot();

            public bool TryGetRegisteredPackageNames(
                out IReadOnlyList<string> packageNames,
                out string error)
            {
                packageNames = new[] { packageName };
                error = string.Empty;
                return true;
            }

            public bool TryStartRegistrySearch(
                string requestedPackageName,
                out IPackageDependencyRegistrySearch search,
                out string error)
            {
                SearchCalls++;
                search = null;
                error = "Unexpected registry search for " +
                        requestedPackageName + ".";
                return false;
            }
        }

        private sealed class FakeSearch : IPackageDependencyRegistrySearch
        {
            private IReadOnlyList<PackageDependencyRegistryPackage> packages =
                Array.Empty<PackageDependencyRegistryPackage>();
            private string error = string.Empty;
            private bool success;

            internal static FakeSearch Successful(
                params PackageDependencyRegistryPackage[] packages)
            {
                var search = new FakeSearch();
                search.CompleteSuccess(packages);
                return search;
            }

            public bool IsCompleted { get; private set; }

            internal void CompleteSuccess(
                params PackageDependencyRegistryPackage[] results)
            {
                packages = results ??
                           Array.Empty<PackageDependencyRegistryPackage>();
                error = string.Empty;
                success = true;
                IsCompleted = true;
            }

            internal void CompleteFailure(string message)
            {
                packages = Array.Empty<PackageDependencyRegistryPackage>();
                error = message;
                success = false;
                IsCompleted = true;
            }

            public bool TryGetResult(
                out IReadOnlyList<PackageDependencyRegistryPackage> results,
                out string message)
            {
                results = packages;
                message = error;
                return IsCompleted && success;
            }
        }
    }
}
