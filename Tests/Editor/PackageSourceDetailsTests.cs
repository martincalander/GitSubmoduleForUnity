using NUnit.Framework;

namespace MartinCalander.GitSubmoduleManager.Editor.Tests
{
    [Category("PackageSourceFeatures")]
    public sealed class PackageSourceDetailsTests
    {
        [TestCase("https://github.com/example/package.git#main", "https://github.com/example/package")]
        [TestCase("git+https://github.com/example/package.git?path=/Packages/Child#main", "https://github.com/example/package")]
        [TestCase("git@github.com:example/package.git", "https://github.com/example/package")]
        [TestCase("ssh://git@gitlab.com/group/subgroup/package.git", "https://gitlab.com/group/subgroup/package")]
        [TestCase("git://bitbucket.org/example/package.git", "https://bitbucket.org/example/package")]
        [TestCase("https://code.example.invalid:8443/team/package.git", "https://code.example.invalid:8443/team/package")]
        [TestCase("http://code.example.invalid/team/package.git", "http://code.example.invalid/team/package")]
        [TestCase("GIT+HTTPS://GitHub.com/example/package.GIT/?path=/Child#release", "https://github.com/example/package")]
        [TestCase("ssh://git@github.com/example/package.git?path=/Child#release", "https://github.com/example/package")]
        public void OpensOnlyRepositoryLocationWithoutUpmPathOrRevision(string url, string browser)
        {
            Assert.That(PackageSourceDetails.GetRepositoryUrl(url), Is.EqualTo(browser));
        }

        [TestCase("git@code.example.invalid:example/package.git")]
        [TestCase("ssh://git@github.com:2222/example/package.git")]
        [TestCase("git://github.com:1234/example/package.git")]
        [TestCase("../sibling.git")]
        [TestCase("./sibling.git")]
        [TestCase("file:///tmp/example/package.git")]
        [TestCase("file://github.com/example/package.git")]
        public void NonWebLocationsDoNotInventABrowserLocation(string url)
        {
            Assert.That(PackageSourceDetails.GetRepositoryUrl(url), Is.Empty);
        }

        [TestCase("https://user:secret@github.com/example/package.git#main")]
        [TestCase("https://secret@github.com/example/package.git")]
        [TestCase("ssh://user:secret@github.com/example/package.git")]
        [TestCase("secret@github.com:example/package.git")]
        [TestCase("https://github.com/example/package.git?token=secret#main")]
        [TestCase("https://github.com/example/package.git?path=/Packages/Child&token=secret#main")]
        [TestCase("git+https://user:secret@github.com/example/package.git?token=secret&path=/Packages/Child#main")]
        [TestCase("https://github.com/example/package.git?path=%0A&token=secret#secret")]
        public void RepositoryLinkOmitsCredentialsQueryAndFragment(string url)
        {
            Assert.That(PackageSourceDetails.GetRepositoryUrl(url), Is.EqualTo("https://github.com/example/package"));
        }

        [TestCase("javascript:alert(1)")]
        [TestCase("data:text/html,example")]
        [TestCase("ftp://example.invalid/package.git")]
        [TestCase("https://github.com/example/package.git\nunsafe")]
        [TestCase("https://github.com/example/package.git\runsafe")]
        [TestCase("https://github.com/example/package.git\tunsafe")]
        [TestCase("https://github.com/example/\0package.git")]
        [TestCase("https://[invalid/example/package.git")]
        [TestCase("https://")]
        [TestCase("")]
        [TestCase(" ")]
        [TestCase(null)]
        public void UnsupportedOrMalformedSourcesHaveNoLink(string url)
        {
            Assert.That(PackageSourceDetails.GetRepositoryUrl(url), Is.Empty);
        }

        [Test]
        public void SourceHostIsNeverReplacedWithAnUnrelatedTrustedHost()
        {
            Assert.That(PackageSourceDetails.GetRepositoryUrl("https://github.com@other.invalid/example/package.git"),
                Is.EqualTo("https://other.invalid/example/package"));
        }

        [TestCase("ssh://git@github.com.other.invalid/example/package.git")]
        [TestCase("git@othergithub.com:example/package.git")]
        [TestCase("ssh://github.com@other.invalid/example/package.git")]
        public void SshHostMappingRequiresAnExactRecognizedHost(string url)
        {
            Assert.That(PackageSourceDetails.GetRepositoryUrl(url), Is.Empty);
        }

        [Test]
        public void OversizedSourceHasNoLink()
        {
            const string prefix = "https://github.com/";
            string url = prefix + new string('a', 4097 - prefix.Length);
            Assert.That(PackageSourceDetails.GetRepositoryUrl(url), Is.Empty);
        }
    }
}
