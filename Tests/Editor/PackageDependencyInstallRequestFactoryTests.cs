using System;
using NUnit.Framework;

namespace MartinCalander.GitSubmoduleManager.Editor.Tests
{
    public sealed class PackageDependencyInstallRequestFactoryTests
    {
        private const string Url =
            "https://github.com/example/root-package.git";
        private const string Branch = "main";
        private const string PackageName = "com.example.root";

        [Test]
        public void ExactReadySnapshot_CreatesBranchBoundRequest()
        {
            var dependency = new PackageManifestDependency(
                "com.example.dependency",
                "2.0.0");

            Assert.That(
                PackageDependencyInstallRequestFactory.TryCreateFromProbe(
                    Snapshot(dependencies: new[] { dependency }),
                    Url,
                    Branch,
                    PackageName,
                    PackageManagerGitInstallMode.GitSubmodule,
                    out PackageDependencyInstallRequest request,
                    out string error),
                Is.True,
                error);

            Assert.That(request.RepositoryUrl, Is.EqualTo(Url));
            Assert.That(request.Revision, Is.EqualTo(Branch));
            Assert.That(request.RootPackageName, Is.EqualTo(PackageName));
            Assert.That(request.RootVersion, Is.EqualTo("1.2.3"));
            Assert.That(request.Dependencies, Has.Count.EqualTo(1));
            Assert.That(
                request.Dependencies[0].Name,
                Is.EqualTo("com.example.dependency"));
        }

        [TestCase((int)GitSubmoduleInstallProbeStatus.Idle)]
        [TestCase((int)GitSubmoduleInstallProbeStatus.LoadingRemoteRefs)]
        [TestCase((int)GitSubmoduleInstallProbeStatus.ReadingPackageManifest)]
        [TestCase((int)GitSubmoduleInstallProbeStatus.Failed)]
        public void NonReadySnapshot_IsRejected(int status)
        {
            AssertRejected(Snapshot(
                status: (GitSubmoduleInstallProbeStatus)status));
        }

        [Test]
        public void ManifestDiagnostic_IsRejected()
        {
            AssertRejected(Snapshot(manifestMessage: "missing package.json"));
        }

        [Test]
        public void DifferentRepository_IsRejected()
        {
            AssertRejected(Snapshot(url: "https://github.com/example/other.git"));
        }

        [TestCase("develop", "main")]
        [TestCase("main", "develop")]
        [TestCase("", "main")]
        [TestCase("main", "")]
        public void DifferentRequestedOrInspectedBranch_IsRejected(
            string requestedBranch,
            string inspectedBranch)
        {
            AssertRejected(Snapshot(
                requestedBranch: requestedBranch,
                inspectedBranch: inspectedBranch));
        }

        [Test]
        public void DifferentPackageName_IsRejected()
        {
            AssertRejected(Snapshot(packageName: "com.example.other"));
        }

        [TestCase("")]
        [TestCase("latest")]
        [TestCase("1.0")]
        public void InvalidManifestVersion_IsRejected(string version)
        {
            AssertRejected(Snapshot(version: version));
        }

        private static void AssertRejected(
            GitSubmoduleInstallProbeSnapshot snapshot)
        {
            Assert.That(
                PackageDependencyInstallRequestFactory.TryCreateFromProbe(
                    snapshot,
                    Url,
                    Branch,
                    PackageName,
                    PackageManagerGitInstallMode.GitSubmodule,
                    out PackageDependencyInstallRequest request,
                    out string error),
                Is.False);
            Assert.That(request, Is.Null);
            Assert.That(error, Is.Not.Empty);
        }

        private static GitSubmoduleInstallProbeSnapshot Snapshot(
            GitSubmoduleInstallProbeStatus status =
                GitSubmoduleInstallProbeStatus.Ready,
            string url = Url,
            string requestedBranch = Branch,
            string inspectedBranch = Branch,
            string packageName = PackageName,
            string version = "1.2.3",
            string manifestMessage = "",
            PackageManifestDependency[] dependencies = null)
        {
            return new GitSubmoduleInstallProbeSnapshot(
                1,
                url,
                status,
                new[] { Branch },
                Branch,
                packageName,
                "Root Package",
                version,
                manifestMessage: manifestMessage,
                requestedBranch: requestedBranch,
                inspectedBranch: inspectedBranch,
                dependencies: dependencies ??
                              Array.Empty<PackageManifestDependency>());
        }
    }
}
