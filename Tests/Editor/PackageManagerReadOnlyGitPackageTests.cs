using NUnit.Framework;

namespace MartinCalander.GitSubmoduleManager.Editor.Tests
{
    public sealed class PackageManagerReadOnlyGitPackageTests
    {
        [Test]
        public void ResolveSelectedPackageName_PrefersPrimaryVersionName()
        {
            var package = new FakePackage
            {
                name = "com.example.placeholder",
                versions = new FakeVersions
                {
                    primary = new FakeVersion
                    {
                        name = "com.example.readonly"
                    },
                    installed = new FakeVersion
                    {
                        name = "com.example.installed"
                    }
                }
            };

            Assert.That(
                PackageManagerReadOnlyGitPackage.TryResolveSelectedPackageName(
                    package,
                    out string packageName),
                Is.True);
            Assert.That(packageName, Is.EqualTo("com.example.readonly"));
        }

        [Test]
        public void ResolveSelectedPackageName_FallsBackToInstalledThenPackageIdentity()
        {
            var installedPackage = new FakePackage
            {
                versions = new FakeVersions
                {
                    installed = new FakeVersion
                    {
                        packageUniqueId = "com.example.installed@1.2.3"
                    }
                }
            };
            Assert.That(
                PackageManagerReadOnlyGitPackage.TryResolveSelectedPackageName(
                    installedPackage,
                    out string installedName),
                Is.True);
            Assert.That(installedName, Is.EqualTo("com.example.installed"));

            var packageIdentity = new FakePackage
            {
                uniqueId = "com.example.identity@https://github.com/example/identity.git"
            };
            Assert.That(
                PackageManagerReadOnlyGitPackage.TryResolveSelectedPackageName(
                    packageIdentity,
                    out string identityName),
                Is.True);
            Assert.That(identityName, Is.EqualTo("com.example.identity"));
        }

        [Test]
        public void ResolveSelectedPackageName_RejectsInvalidOrUnboundedReflectionValues()
        {
            Assert.That(
                PackageManagerReadOnlyGitPackage.TryResolveSelectedPackageName(
                    new FakePackage { name = "Not A Package" },
                    out _),
                Is.False);
            Assert.That(
                PackageManagerReadOnlyGitPackage.TryResolveSelectedPackageName(
                    new FakePackage { name = new string('a', 513) },
                    out _),
                Is.False);
            Assert.That(
                PackageManagerReadOnlyGitPackage.TryGetInfo(null, out _),
                Is.False);
        }

        private sealed class FakePackage
        {
            public string name;
            public string uniqueId;
            public FakeVersions versions;
        }

        private sealed class FakeVersions
        {
            public FakeVersion primary;
            public FakeVersion installed;
        }

        private sealed class FakeVersion
        {
            public string name;
            public string packageUniqueId;
        }
    }
}
