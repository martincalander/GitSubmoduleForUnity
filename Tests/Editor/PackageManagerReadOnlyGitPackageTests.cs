using System;
using System.Reflection;
using NUnit.Framework;
using UnityEditor.PackageManager;
using UpmPackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace MartinCalander.GitSubmoduleManager.Editor.Tests
{
    [Parallelizable(ParallelScope.None)]
    public sealed class PackageManagerReadOnlyGitPackageTests
    {
        [SetUp]
        public void SetUp()
        {
            PackageManagerReadOnlyGitPackage.ResetRegisteredPackageIndexForTests();
        }

        [TearDown]
        public void TearDown()
        {
            PackageManagerReadOnlyGitPackage.ResetRegisteredPackageIndexForTests();
        }

        [Test]
        public void RegisteredPackageIndex_ReusesOneUnitySnapshotAcrossNameLookups()
        {
            UpmPackageInfo first = CreatePackageInfo("com.example.first");
            UpmPackageInfo second = CreatePackageInfo("com.example.second");
            int providerCalls = 0;
            PackageManagerReadOnlyGitPackage.RegisteredPackagesProviderForTests = () =>
            {
                providerCalls++;
                return new[] { first, second };
            };

            Assert.That(
                PackageManagerReadOnlyGitPackage.TryGetRegisteredPackage(
                    first.name,
                    out UpmPackageInfo foundFirst,
                    out string error),
                Is.True,
                error);
            Assert.That(
                PackageManagerReadOnlyGitPackage.TryGetRegisteredPackage(
                    second.name,
                    out UpmPackageInfo foundSecond,
                    out error),
                Is.True,
                error);

            Assert.That(foundFirst, Is.SameAs(first));
            Assert.That(foundSecond, Is.SameAs(second));
            Assert.That(providerCalls, Is.EqualTo(1));
            Assert.That(
                PackageManagerReadOnlyGitPackage.RegisteredPackageSnapshotReadCountForTests,
                Is.EqualTo(1));
        }

        [Test]
        public void RegisteredPackageIndex_InvalidationReloadsUnitySnapshot()
        {
            UpmPackageInfo first = CreatePackageInfo("com.example.first");
            UpmPackageInfo second = CreatePackageInfo("com.example.second");
            UpmPackageInfo[] snapshot = { first };
            PackageManagerReadOnlyGitPackage.RegisteredPackagesProviderForTests = () => snapshot;

            Assert.That(
                PackageManagerReadOnlyGitPackage.TryGetRegisteredPackage(
                    first.name,
                    out _,
                    out string error),
                Is.True,
                error);

            snapshot = new[] { second };
            PackageManagerReadOnlyGitPackage.InvalidateRegisteredPackageIndex();

            Assert.That(
                PackageManagerReadOnlyGitPackage.TryGetRegisteredPackage(
                    second.name,
                    out UpmPackageInfo foundSecond,
                    out error),
                Is.True,
                error);
            Assert.That(foundSecond, Is.SameAs(second));
            Assert.That(
                PackageManagerReadOnlyGitPackage.RegisteredPackageSnapshotReadCountForTests,
                Is.EqualTo(2));
        }

        [Test]
        public void CreateInfo_RejectsIndirectAndNonGitPackagesBeforeManifestLookup()
        {
            UpmPackageInfo indirectGit = CreatePackageInfo(
                "com.example.indirect",
                PackageSource.Git,
                false);
            Assert.That(
                PackageManagerReadOnlyGitPackage.TryCreateInfo(
                    indirectGit,
                    out _,
                    out string error),
                Is.False);
            Assert.That(error, Does.Contain("not a direct read-only Git dependency"));

            UpmPackageInfo directRegistry = CreatePackageInfo(
                "com.example.registry",
                PackageSource.Registry,
                true);
            Assert.That(
                PackageManagerReadOnlyGitPackage.TryCreateInfo(
                    directRegistry,
                    out _,
                    out error),
                Is.False);
            Assert.That(error, Does.Contain("not a direct read-only Git dependency"));
        }

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

        private static UpmPackageInfo CreatePackageInfo(
            string name,
            PackageSource source = PackageSource.Git,
            bool isDirectDependency = true)
        {
            var packageInfo = (UpmPackageInfo)Activator.CreateInstance(
                typeof(UpmPackageInfo),
                true);
            SetPrivateField(packageInfo, "m_Name", name);
            SetPrivateField(packageInfo, "m_Source", source);
            SetPrivateField(packageInfo, "m_IsDirectDependency", isDirectDependency);
            return packageInfo;
        }

        private static void SetPrivateField<T>(
            UpmPackageInfo packageInfo,
            string fieldName,
            T value)
        {
            FieldInfo field = typeof(UpmPackageInfo).GetField(
                fieldName,
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null, $"Unity PackageInfo no longer exposes {fieldName}.");
            field.SetValue(packageInfo, value);
        }
    }
}
