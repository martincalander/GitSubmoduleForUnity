using NUnit.Framework;

namespace MartinCalander.GitSubmoduleManager.Editor.Tests
{
    [Category("PackageSourceFeatures")]
    public sealed class PackageSourceDetailsTests
    {
        private const string Commit = "0123456789abcdef0123456789abcdef01234567";

        [TestCase("https://github.com/example/package.git#main", "https://github.com/example/package")]
        [TestCase("git+https://github.com/example/package.git?path=/Packages/Child#main", "https://github.com/example/package")]
        [TestCase("git@github.com:example/package.git", "https://github.com/example/package")]
        [TestCase("ssh://git@gitlab.com/group/subgroup/package.git", "https://gitlab.com/group/subgroup/package")]
        [TestCase("git://bitbucket.org/example/package.git", "https://bitbucket.org/example/package")]
        [TestCase("https://code.example.invalid:8443/team/package.git", "https://code.example.invalid:8443/team/package")]
        [TestCase("http://code.example.invalid/team/package.git", "http://code.example.invalid/team/package")]
        public void OpensOnlyRepositoryLocationWithoutUpmPathOrRevision(string url, string browser)
        {
            var details = Read(url);
            Assert.That(details.SourceUrl, Is.EqualTo(url));
            Assert.That(details.RepositoryUrl, Is.EqualTo(browser));
        }

        [TestCase("git@code.example.invalid:example/package.git")]
        [TestCase("ssh://git@github.com:2222/example/package.git")]
        [TestCase("../sibling.git")]
        [TestCase("file:///tmp/example/package.git")]
        public void NonWebLocationsAreDisplayedWithoutInventingABrowserLocation(string url)
        {
            var details = Read(url);
            Assert.That(details.SourceUrl, Is.EqualTo(url));
            Assert.That(details.RepositoryUrl, Is.Empty);
        }

        [TestCase("https://user:secret@github.com/example/package.git#main", "https://github.com/example/package.git#main")]
        [TestCase("https://secret@github.com/example/package.git", "https://github.com/example/package.git")]
        [TestCase("ssh://user:secret@github.com/example/package.git", "ssh://github.com/example/package.git")]
        [TestCase("secret@github.com:example/package.git", "ssh://github.com/example/package.git")]
        [TestCase("https://github.com/example/package.git?token=secret#main", "https://github.com/example/package.git#main")]
        [TestCase("https://github.com/example/package.git?path=/Packages/Child&token=secret#main", "https://github.com/example/package.git?path=/Packages/Child#main")]
        [TestCase("git+https://user:secret@github.com/example/package.git?token=secret&path=/Packages/Child#main", "git+https://github.com/example/package.git?path=/Packages/Child#main")]
        public void SourceDisplayOmitsCredentialsAndUnknownQueryParameters(string url, string clean)
        {
            var details = Read(url);
            Assert.That(details.SourceUrl, Is.EqualTo(clean));
            Assert.That(details.SourceUrl, Does.Not.Contain("secret"));
            Assert.That(details.RepositoryUrl, Does.Not.Contain("secret"));
        }

        [TestCase("javascript:alert(1)")]
        [TestCase("data:text/html,example")]
        [TestCase("ftp://example.invalid/package.git")]
        [TestCase("https://github.com/example/package.git\nunsafe")]
        [TestCase("https://github.com/example/package.git\runsafe")]
        [TestCase("https://github.com/example/package.git\tunsafe")]
        [TestCase("")]
        [TestCase(null)]
        public void UnsupportedOrMalformedSourcesHaveNoActions(string url)
        {
            var details = Read(url);
            Assert.That(details.SourceUrl, Is.Empty);
            Assert.That(details.RepositoryUrl, Is.Empty);
        }

        [Test]
        public void SourceHostIsNeverReplacedWithAnUnrelatedTrustedHost()
        {
            var details = Read("https://github.com@other.invalid/example/package.git");
            Assert.That(details.SourceUrl, Is.EqualTo("https://other.invalid/example/package.git"));
            Assert.That(details.RepositoryUrl, Is.EqualTo("https://other.invalid/example/package"));
        }

        [TestCase("main")]
        [TestCase("v1.2.3")]
        [TestCase("feature/source-details")]
        [TestCase(Commit)]
        public void RequestedRefAndInstalledCommitAreSeparate(string revision)
        {
            var details = PackageSourceDetails.Create("/project/Library/PackageCache/example",
                "https://github.com/example/package.git#" + revision, revision, Commit, true);
            Assert.That(details.RequestedRevision, Is.EqualTo(revision));
            Assert.That(details.InstalledCommit, Is.EqualTo(Commit));
            Assert.That(details.RevisionDescription, Is.EqualTo(revision + " → 0123456"));
        }

        [Test]
        public void MissingRevisionUsesDefaultBranchWithoutGuessingItsName()
        {
            var details = Read("https://github.com/example/package.git");
            Assert.That(details.RequestedRevision, Is.EqualTo("default branch"));
            Assert.That(details.RevisionDescription, Is.EqualTo("default branch → 0123456"));
        }

        [Test]
        public void MissingGitRevisionMetadataFallsBackToRequestedPackageIdFragment()
        {
            Assert.That(Read("https://github.com/example/package.git#release").RequestedRevision, Is.EqualTo("release"));
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("not-a-hash")]
        [TestCase("0123456")]
        public void MissingOrMalformedHashDoesNotBecomeAnInstalledCommit(string hash)
        {
            var details = PackageSourceDetails.Create("/project/package", "https://github.com/example/package.git#main", "main", hash, true);
            Assert.That(details.InstalledCommit, Is.Empty);
            Assert.That(details.RevisionDescription, Is.EqualTo("main → installed commit unavailable"));
        }

        [Test]
        public void Sha256CommitKeepsItsFullValueForCopying()
        {
            string hash = new string('a', 64);
            var details = PackageSourceDetails.Create("/project/package", "https://github.com/example/package.git", null, hash, true);
            Assert.That(details.InstalledCommit, Is.EqualTo(hash));
            Assert.That(details.RevisionDescription, Is.EqualTo("default branch → aaaaaaa"));
        }

        [Test]
        public void NonGitPackageNeverReceivesRevisionOrCommitLabels()
        {
            var details = PackageSourceDetails.Create("/project/package", "https://github.com/example/package.git", "main", Commit, false);
            Assert.That(details.RequestedRevision, Is.Empty);
            Assert.That(details.InstalledCommit, Is.Empty);
            Assert.That(details.RevisionDescription, Is.Empty);
        }

        private static PackageSourceDetails Read(string url) => PackageSourceDetails.Create(
            "/project/Library/PackageCache/example", url, null, Commit, true);
    }
}
