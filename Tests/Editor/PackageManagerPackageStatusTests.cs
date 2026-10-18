using System;
using System.IO;
using System.Diagnostics;
using System.Threading;
using NUnit.Framework;

namespace MartinCalander.GitSubmoduleManager.Editor.Tests
{
    public sealed class PackageManagerPackageStatusTests
    {
        private const string Name = "com.example.package";
        private const string RelativePath = "Packages/" + Name;
        private const string Commit = "0123456789abcdef0123456789abcdef01234567";
        private string root;
        private string packagePath;

        [SetUp]
        public void SetUp()
        {
            root = Path.Combine(Path.GetTempPath(), "package-status-tests-" + Guid.NewGuid().ToString("N"));
            packagePath = Path.Combine(root, "Packages", Name);
            Directory.CreateDirectory(packagePath);
        }

        [TearDown]
        public void TearDown()
        {
            Directory.Delete(root, true);
        }

        [Test]
        public void ClassifiesOnlyMatchingRegisteredPathWithGitlinkAndRegistration()
        {
            var data = Snapshot();
            Assert.That(PackageManagerPackageStatus.TryClassify(root, Name, packagePath, data, out PackageSubmoduleStatus status), Is.True);
            Assert.That(status.Name, Is.EqualTo(Name));
            Assert.That(status.FullPath, Is.EqualTo(packagePath));
            Assert.That(status.RepositoryUrl, Is.EqualTo("https://github.com/example/package.git"));
            Assert.That(status.IsGitHub, Is.True);
        }

        [Test]
        public void ChildHeadIsNotRequiredToMatchParentGitlink()
        {
            Directory.CreateDirectory(Path.Combine(packagePath, ".git"));
            File.WriteAllText(Path.Combine(packagePath, ".git", "HEAD"), new string('a', 40));
            Assert.That(PackageManagerPackageStatus.TryClassify(root, Name, packagePath, Snapshot(), out _), Is.True);
        }

        [TestCase("com.example.other", "Packages/com.example.package")]
        [TestCase("com.example.package", "Packages/com.example.other")]
        [TestCase("com.example.package", "Library/PackageCache/com.example.package")]
        [TestCase("com.example.package", "Packages/group/com.example.package")]
        [TestCase("../com.example.package", "Packages/com.example.package")]
        [TestCase("package", "Packages/package")]
        public void RejectsMismatchedOrIndirectPackages(string name, string path)
        {
            Assert.That(PackageManagerPackageStatus.TryClassify(root, name, Path.Combine(root, path), Snapshot(), out _), Is.False);
        }

        [Test]
        public void RegistrationAloneDoesNotProveSubmodule()
        {
            var data = Snapshot();
            data.Gitlinks.Clear();
            Assert.That(PackageManagerPackageStatus.TryClassify(root, Name, packagePath, data, out _), Is.False);
            Assert.That(Block(data), Is.True);
        }

        [Test]
        public void GitlinkAloneDoesNotProveRegistrationButStillBlocksRawRemoval()
        {
            var data = Snapshot();
            data.Repositories.Clear();
            Assert.That(PackageManagerPackageStatus.TryClassify(root, Name, packagePath, data, out _), Is.False);
            Assert.That(Block(data), Is.True);
        }

        [Test]
        public void NonGitHubSubmodulesRemainSubmodules()
        {
            var data = Snapshot();
            data.Repositories[RelativePath] = "https://gitlab.com/example/package.git";
            Assert.That(PackageManagerPackageStatus.TryClassify(root, Name, packagePath, data, out PackageSubmoduleStatus status), Is.True);
            Assert.That(status.IsGitHub, Is.False);
        }

        [TestCase("https://github.com/example/package.git", true)]
        [TestCase("git@github.com:example/package.git", true)]
        [TestCase("ssh://git@github.com/example/package.git", true)]
        [TestCase("git+https://github.com/example/package.git?path=/Packages/package#main", true)]
        [TestCase("https://github.com.evil.test/example/package.git", false)]
        [TestCase("https://github.com@evil.test/example/package.git", false)]
        [TestCase("https://gitlab.com/example/package.git", false)]
        [TestCase("../package.git", false)]
        [TestCase("https://github.com/example", false)]
        public void GitHubClassificationUsesRepositoryHost(string url, bool expected)
        {
            Assert.That(PackageManagerPackageStatus.IsGitHubUrl(url), Is.EqualTo(expected));
        }

        [Test]
        public void ParsesNulDelimitedGitlinksAndIgnoresOrdinaryFiles()
        {
            var data = new PackageManagerPackageStatus.StatusSnapshot();
            PackageManagerPackageStatus.ParseIndex("160000 " + Commit + " 0\t" + RelativePath + "\0" +
                "100644 " + Commit + " 0\tPackages/manifest.json\0", data);
            Assert.That(data.Gitlinks, Is.EquivalentTo(new[] { RelativePath }));
        }

        [TestCase("160000 0123456789abcdef0123456789abcdef01234567 0\tPackages/com.example.package")]
        [TestCase("160000 invalid 0\tPackages/com.example.package\0")]
        [TestCase("160000 0123456789abcdef0123456789abcdef01234567 2\tPackages/com.example.package\0")]
        [TestCase("malformed\0")]
        [TestCase("\0")]
        public void RejectsIncompleteMalformedOrConflictedIndex(string text)
        {
            Assert.Throws<InvalidDataException>(() => PackageManagerPackageStatus.ParseIndex(text, new PackageManagerPackageStatus.StatusSnapshot()));
        }

        [Test]
        public void RejectsDuplicateIndexPaths()
        {
            string entry = "160000 " + Commit + " 0\t" + RelativePath + "\0";
            Assert.Throws<InvalidDataException>(() => PackageManagerPackageStatus.ParseIndex(entry + entry, new PackageManagerPackageStatus.StatusSnapshot()));
        }

        [Test]
        public void ParsesSectionsWhoseNamesContainDotsAndSpaces()
        {
            var data = new PackageManagerPackageStatus.StatusSnapshot();
            PackageManagerPackageStatus.ParseModules("submodule.a.b and c.path\n" + RelativePath + "\0" +
                "submodule.a.b and c.url\ngit@github.com:example/package.git\0", data);
            Assert.That(data.Repositories[RelativePath], Is.EqualTo("git@github.com:example/package.git"));
        }

        [TestCase("submodule.example.path\nPackages/com.example.package")]
        [TestCase("submodule.example.path\nPackages/com.example.package\0")]
        [TestCase("submodule.example.url\nhttps://github.com/example/package.git\0")]
        [TestCase("submodule.example.path\nPackages/com.example.package\0submodule.example.path\nPackages/com.example.package\0")]
        [TestCase("submodule.example.path\nPackages/com.example.package\0submodule.example.url\nhttps://github.com/example/\npackage.git\0")]
        [TestCase("invalid\0")]
        public void RejectsIncompleteDuplicateOrMalformedRegistrations(string text)
        {
            Assert.Throws<InvalidDataException>(() => PackageManagerPackageStatus.ParseModules(text, new PackageManagerPackageStatus.StatusSnapshot()));
        }

        [Test]
        public void RejectsDifferentSectionsRegisteringSamePath()
        {
            string first = "submodule.first.path\n" + RelativePath + "\0submodule.first.url\nhttps://example.com/repo\0";
            string second = "submodule.second.path\n" + RelativePath + "\0submodule.second.url\nhttps://example.com/repo\0";
            Assert.Throws<InvalidDataException>(() => PackageManagerPackageStatus.ParseModules(first + second, new PackageManagerPackageStatus.StatusSnapshot()));
        }

        [Test]
        public void FailedReadsNeverBecomeNegativeSubmoduleEvidence()
        {
            var data = Snapshot();
            data.Error = "Git could not read this project's status.";
            Assert.That(PackageManagerPackageStatus.TryClassify(root, Name, packagePath, data, out _), Is.False);
            Assert.That(Block(data), Is.True);
            Assert.That(PackageManagerPackageStatus.ShouldBlockEmbeddedRemoval(root, Name, packagePath, false,
                new PackageManagerPackageStatus.StatusSnapshot()), Is.True);
        }

        [Test]
        public void SuccessfulOrdinaryEmbeddedPackageCanUseUnityRemoval()
        {
            Assert.That(Block(new PackageManagerPackageStatus.StatusSnapshot()), Is.False);
        }

        [TestCase(true)]
        [TestCase(false)]
        public void LocalGitCheckoutBlocksRawRemovalEvenWithoutParentRegistration(bool directory)
        {
            string git = Path.Combine(packagePath, ".git");
            if (directory)
                Directory.CreateDirectory(git);
            else
                File.WriteAllText(git, "gitdir: ../../.git/modules/example");
            Assert.That(Block(new PackageManagerPackageStatus.StatusSnapshot()), Is.True);
        }

        [Test]
        public void PendingScanDoesNotBlockUnrelatedPackagePaths()
        {
            Assert.That(PackageManagerPackageStatus.ShouldBlockEmbeddedRemoval(root, Name,
                Path.Combine(root, "Library", "PackageCache", Name), false, new PackageManagerPackageStatus.StatusSnapshot()), Is.False);
        }

        [Test]
        public void RepositoryReadFailureIsExplicit()
        {
            var data = PackageManagerPackageStatus.ReadStatus(root, CancellationToken.None);
            Assert.That(data.Error, Is.Not.Empty);
            Assert.That(Block(data), Is.True);
        }

        [Test]
        public void ReadsRealGitMetadataWithoutChangingRepositoryFiles()
        {
            RunGit("init --quiet");
            RunGit("update-index --add --cacheinfo 160000," + Commit + "," + RelativePath);
            string modules = Path.Combine(root, ".gitmodules");
            File.WriteAllText(modules, "[submodule \"example\"]\npath = " + RelativePath +
                "\nurl = https://github.com/example/package.git\n");
            byte[] beforeIndex = File.ReadAllBytes(Path.Combine(root, ".git", "index"));
            byte[] beforeModules = File.ReadAllBytes(modules);
            var data = PackageManagerPackageStatus.ReadStatus(root, CancellationToken.None);
            Assert.That(data.Error, Is.Empty);
            Assert.That(PackageManagerPackageStatus.TryClassify(root, Name, packagePath, data, out _), Is.True);
            Assert.That(File.ReadAllBytes(Path.Combine(root, ".git", "index")), Is.EqualTo(beforeIndex));
            Assert.That(File.ReadAllBytes(modules), Is.EqualTo(beforeModules));
        }

        private void RunGit(string arguments)
        {
            using (var process = Process.Start(new ProcessStartInfo("git", arguments)
            {
                WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true
            }))
            {
                Assert.That(process.WaitForExit(5000), Is.True);
                Assert.That(process.ExitCode, Is.Zero, process.StandardError.ReadToEnd());
            }
        }

        private bool Block(PackageManagerPackageStatus.StatusSnapshot data)
        {
            return PackageManagerPackageStatus.ShouldBlockEmbeddedRemoval(root, Name, packagePath, true, data);
        }

        private static PackageManagerPackageStatus.StatusSnapshot Snapshot()
        {
            var data = new PackageManagerPackageStatus.StatusSnapshot();
            PackageManagerPackageStatus.ParseIndex("160000 " + Commit + " 0\t" + RelativePath + "\0", data);
            PackageManagerPackageStatus.ParseModules("submodule.example.path\n" + RelativePath + "\0" +
                "submodule.example.url\nhttps://github.com/example/package.git\0", data);
            return data;
        }
    }
}
