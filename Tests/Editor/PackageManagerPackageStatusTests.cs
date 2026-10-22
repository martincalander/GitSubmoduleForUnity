using System;
using System.Collections.Generic;
using System.IO;
using System.Diagnostics;
using System.Text;
using System.Threading;
using NUnit.Framework;
using UnityEditor.PackageManager;

namespace MartinCalander.GitSubmoduleManager.Editor.Tests
{
    [Category("PackageSourceFeatures")]
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

        [TestCase("")]
        [TestCase(".")]
        [TestCase("../" + Name)]
        public void ClassifiesOnlyMatchingRegisteredPathWithGitlinkAndRegistration(string suffix)
        {
            var data = Snapshot();
            Assert.That(PackageManagerPackageStatus.TryClassify(root, Name, Path.Combine(packagePath, suffix), data,
                out PackageSubmoduleStatus status), Is.True);
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

        [TestCase("", "")]
        [TestCase(".", "")]
        [TestCase("", ".")]
        [TestCase("../" + Name, "../" + Name)]
        public void InstalledSourceMatchesEquivalentPathsWithoutWeakeningIdentity(string registeredSuffix, string nativeSuffix)
        {
            string id = Name + "@https://github.com/example/package.git";
            var data = new PackageManagerPackageStatus.StatusSnapshot();
            data.Packages.Add(Name, new PackageManagerPackageStatus.InstalledPackage(
                Path.Combine(packagePath, registeredSuffix), PackageSource.Git, id));
            object version = NativeVersion(Path.Combine(packagePath, nativeSuffix), id);
            object native = new { versions = new { primary = version } };

            Assert.That(PackageManagerPackageStatus.IsGitHubPackage(root, native, data), Is.True);
            Assert.That(PackageManagerPackageStatus.GetRepositoryUrl(root, version, data),
                Is.EqualTo("https://github.com/example/package"));
            Assert.That(data.Packages[Name].MatchesPath(packagePath + "-other"), Is.False);
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("\0")]
        public void IdenticalInvalidRegisteredAndNativePathsRemainUnverified(string path)
        {
            string id = Name + "@https://github.com/example/package.git";
            var installed = new PackageManagerPackageStatus.InstalledPackage(path, PackageSource.Git, id);
            var data = new PackageManagerPackageStatus.StatusSnapshot();
            data.Packages.Add(Name, installed);
            object version = new { name = Name, isInstalled = true, localPath = path, packageId = id };
            object native = new { versions = new { primary = version } };

            Assert.That(installed.MatchesPath(path), Is.False);
            Assert.That(PackageManagerPackageStatus.IsGitHubPackage(root, native, data), Is.False);
            Assert.That(PackageManagerPackageStatus.GetRepositoryUrl(root, version, data), Is.Empty);
        }

        [Test]
        public void RegisteredFilesystemRootNeverMatchesAnEmptyNativePath()
        {
            string filesystemRoot = Path.GetPathRoot(packagePath);
            var installed = new PackageManagerPackageStatus.InstalledPackage(filesystemRoot, PackageSource.Git,
                Name + "@https://github.com/example/package.git");

            Assert.That(installed.MatchesPath(filesystemRoot), Is.True);
            Assert.That(installed.MatchesPath(string.Empty), Is.False);
            Assert.That(installed.MatchesPath(null), Is.False);
        }

        [Test]
        public void DerivedGitSourceStillRequiresTheRegisteredPackageNamePrefix()
        {
            string id = "com.example.other@https://github.com/example/package.git";
            var data = new PackageManagerPackageStatus.StatusSnapshot();
            data.Packages.Add(Name, new PackageManagerPackageStatus.InstalledPackage(packagePath, PackageSource.Git, id));
            object version = NativeVersion(packagePath, id);

            Assert.That(PackageManagerPackageStatus.IsGitHubPackage(root,
                new { versions = new { primary = version } }, data), Is.False);
            Assert.That(PackageManagerPackageStatus.GetRepositoryUrl(root, version, data), Is.Empty);
        }

        [Test]
        public void DerivedGitSourceChangesWithTheInstalledIdentityAndSanitizesItsLink()
        {
            string originalId = Name + "@git+https://user:secret@github.com/example/package.git?path=/Packages/test#main";
            string replacementId = Name + "@https://gitlab.com/example/replacement.git";
            var data = new PackageManagerPackageStatus.StatusSnapshot();
            data.Packages.Add(Name, new PackageManagerPackageStatus.InstalledPackage(packagePath, PackageSource.Git, originalId));
            object original = NativeVersion(packagePath, originalId);

            Assert.That(PackageManagerPackageStatus.IsGitHubPackage(root,
                new { versions = new { primary = original } }, data), Is.True);
            Assert.That(PackageManagerPackageStatus.GetRepositoryUrl(root, original, data),
                Is.EqualTo("https://github.com/example/package"));

            data.Packages[Name] = new PackageManagerPackageStatus.InstalledPackage(packagePath, PackageSource.Git, replacementId);
            object replacement = NativeVersion(packagePath, replacementId);
            Assert.That(PackageManagerPackageStatus.IsGitHubPackage(root,
                new { versions = new { primary = original } }, data), Is.False);
            Assert.That(PackageManagerPackageStatus.GetRepositoryUrl(root, original, data), Is.Empty);
            Assert.That(PackageManagerPackageStatus.IsGitHubPackage(root,
                new { versions = new { primary = replacement } }, data), Is.False);
            Assert.That(PackageManagerPackageStatus.GetRepositoryUrl(root, replacement, data),
                Is.EqualTo("https://gitlab.com/example/replacement"));
        }

        [TestCase("https://user:secret@github.com/example/replacement.git?path=/Package#main",
            "https://github.com/example/replacement", true)]
        [TestCase("https://gitlab.com/example/replacement.git", "https://gitlab.com/example/replacement", false)]
        [TestCase("file:///private/package.git", "", false)]
        [TestCase(null, "", false)]
        public void CachedSubmoduleSourceTracksExactRawUrlAndRetainsIdentityGuards(
            string changedUrl, string expectedUrl, bool github)
        {
            var data = InstalledSnapshot();
            object version = NativeVersion();
            object native = new { versions = new { primary = version } };
            string first = PackageManagerPackageStatus.GetRepositoryUrl(root, version, data);
            Assert.That(PackageManagerPackageStatus.GetRepositoryUrl(root, version, data), Is.SameAs(first));

            data.Repositories[RelativePath] = changedUrl;
            Assert.That(PackageManagerPackageStatus.IsGitHubPackage(root, native, data), Is.EqualTo(github));
            Assert.That(PackageManagerPackageStatus.GetRepositoryUrl(root, version, data), Is.EqualTo(expectedUrl));
            Assert.That(PackageManagerPackageStatus.GetRepositoryUrl(root, NativeVersion(packageId: "different"), data), Is.Empty);
            Assert.That(PackageManagerPackageStatus.GetRepositoryUrl(root, NativeVersion(path: packagePath + "-other"), data), Is.Empty);
            data.Gitlinks.Clear();
            Assert.That(PackageManagerPackageStatus.GetRepositoryUrl(root, version, data), Is.Empty);
            Assert.That(PackageManagerPackageStatus.IsGitHubPackage(root, native, data), Is.False);
            Assert.That(PackageManagerPackageStatus.TryClassify(root, Name, packagePath, data, out _), Is.False);
        }

        [Test]
        public void SubmoduleSourceCacheStaysBoundedWhenFixtureRegistrationsChange()
        {
            var data = InstalledSnapshot();
            for (int revision = 0; revision < 50; revision++)
            {
                string url = "https://github.com/example/package" + revision;
                data.Repositories[RelativePath] = url;
                Assert.That(PackageManagerPackageStatus.GetRepositoryUrl(root, NativeVersion(), data), Is.EqualTo(url));
                var cached = (System.Collections.IDictionary)PackageManagerSubmoduleHarmonyPatch.Read(data, "repositorySources");
                Assert.That(cached.Count, Is.LessThanOrEqualTo(data.Repositories.Count));
            }
        }

        [Test]
        public void DerivedSubmoduleSourceCacheDoesNotChangeSnapshotContentsOrNotifications()
        {
            var data = InstalledSnapshot();
            var state = new PackageManagerPackageStatus.StatusState();
            state.CompleteRefresh(data);
            int changes = 0;
            state.Changed += () => changes++;
            PackageManagerPackageStatus.GetRepositoryUrl(root, NativeVersion(), data);
            var next = InstalledSnapshot();
            Assert.That(data.HasSameContents(next), Is.True);
            Assert.That(next.HasSameContents(data), Is.True);
            state.CompleteRefresh(next);
            Assert.That(changes, Is.Zero);
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
            Assert.That(Block(ReadOrdinaryRepository()), Is.False);
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

        [TestCase("git-file")]
        [TestCase("git-directory")]
        [TestCase("gitlink")]
        [TestCase("registration")]
        [TestCase("pending")]
        [TestCase("error")]
        public void DifferentlyNamedEmbeddedFolderRetainsRemovalProtection(string evidence)
        {
            const string folder = "MyPackage";
            string path = Path.Combine(root, "Packages", folder);
            Directory.CreateDirectory(path);
            var data = ReadOrdinaryRepository();
            switch (evidence)
            {
                case "git-file": File.WriteAllText(Path.Combine(path, ".git"), "gitdir: ../../.git/modules/example"); break;
                case "git-directory": Directory.CreateDirectory(Path.Combine(path, ".git")); break;
                case "gitlink": data.Gitlinks.Add("Packages/" + folder); break;
                case "registration": data.Repositories.Add("Packages/" + folder, "https://github.com/example/package.git"); break;
                case "error": data.Error = "Git could not read this project's status."; break;
            }

            Assert.That(PackageManagerPackageStatus.ShouldBlockEmbeddedRemoval(root, Name, path,
                evidence != "pending", data), Is.True);
            Assert.That(PackageManagerPackageStatus.TryClassify(root, Name, path, data, out _), Is.False,
                "Broader removal protection does not broaden the classification contract.");
        }

        [Test]
        public void DifferentlyNamedOrdinaryEmbeddedFolderCanUseUnityRemoval()
        {
            string path = Path.Combine(root, "Packages", "MyPackage");
            Directory.CreateDirectory(path);
            Assert.That(PackageManagerPackageStatus.ShouldBlockEmbeddedRemoval(root, Name, path,
                true, ReadOrdinaryRepository()), Is.False);
        }

        [TestCase("./Packages/MyPackage", true)]
        [TestCase("Packages/../Packages/MyPackage", true)]
        [TestCase("Packages/MyPackage/", true)]
        [TestCase("./Packages/OtherPackage", false)]
        [TestCase("Packages/../OtherPackages/MyPackage", false)]
        [TestCase("Packages/group/MyPackage", false)]
        [TestCase("Packages/../../MyPackage", false)]
        public void RegistrationPathAliasesProtectOnlyTheMatchingDirectPackage(string registeredPath, bool blocked)
        {
            string path = Path.Combine(root, "Packages", "MyPackage");
            Directory.CreateDirectory(path);
            var data = ReadOrdinaryRepository();
            data.Repositories.Add(registeredPath, "https://github.com/example/package.git");

            Assert.That(PackageManagerPackageStatus.ShouldBlockEmbeddedRemoval(root, Name, path, true, data), Is.EqualTo(blocked));
        }

        [TestCase("Library/PackageCache/MyPackage")]
        [TestCase("OtherPackages/MyPackage")]
        [TestCase("Packages/group/MyPackage")]
        [TestCase("Packages/../MyPackage")]
        [TestCase("Packages")]
        public void RenamedFolderProtectionStaysWithinDirectPackagesChildren(string relativePath)
        {
            string path = Path.Combine(root, relativePath);
            Directory.CreateDirectory(Path.Combine(path, ".git"));
            Assert.That(PackageManagerPackageStatus.ShouldBlockEmbeddedRemoval(root, Name, path,
                false, new PackageManagerPackageStatus.StatusSnapshot { Error = "Unavailable" }), Is.False);
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
        public void CancelledReadDoesNotStartGitEvenWhenItsWorkingDirectoryIsMissing()
        {
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            var data = PackageManagerPackageStatus.ReadStatus(Path.Combine(root, "missing"), cancellation.Token);

            Assert.That(data.Error, Is.EqualTo("Local Git status was cancelled."));
            Assert.That(data.IndexPath, Is.Null);
            Assert.That(data.FilesStamp, Is.Null);
            Assert.That(Block(data), Is.True);
        }

        [TestCase("\n.git/index\n")]
        [TestCase("\r\n.git/index\r\n")]
        public void CombinedRepositoryLocationResolvesTheProjectIndex(string text)
        {
            Assert.That(PackageManagerPackageStatus.ParseRepositoryIndexPath(root, text),
                Is.EqualTo(Path.Combine(root, ".git", "index")));
        }

        [Test]
        public void CombinedRepositoryLocationPreservesAbsolutePathsWithSpaces()
        {
            string index = Path.Combine(root, "linked worktree", "index");
            Assert.That(PackageManagerPackageStatus.ParseRepositoryIndexPath(root, "\n" + index + "\n"), Is.EqualTo(index));
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("\n")]
        [TestCase("\n\n")]
        [TestCase("\n \n")]
        [TestCase("\n.git/index")]
        [TestCase("\n.git/index\nextra\n")]
        [TestCase("\n.git/index\n\n")]
        [TestCase("\n.git/in\0dex\n")]
        [TestCase("\n.git/index\r\r\n")]
        [TestCase("Child/\n../.git/index\n")]
        public void CombinedRepositoryLocationRejectsIncompleteOrAmbiguousOutput(string text)
        {
            Assert.Throws<InvalidDataException>(() => PackageManagerPackageStatus.ParseRepositoryIndexPath(root, text));
        }

        [Test]
        public void NestedUnityProjectCannotUseItsParentRepositoryMetadata()
        {
            RunGit("init --quiet");
            string nested = Path.Combine(root, "NestedProject");
            Directory.CreateDirectory(nested);

            var data = PackageManagerPackageStatus.ReadStatus(nested, CancellationToken.None);

            Assert.That(data.Error, Is.EqualTo("The Unity project is not the Git repository root."));
            Assert.That(data.IndexPath, Is.Null);
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

        [Test]
        public void LargeEmbeddedPackageContentsDoNotOverflowSubmoduleDiscovery()
        {
            RunGit("init --quiet");
            WriteRegistration();
            var entries = new StringBuilder("160000 " + Commit + " 0\t" + RelativePath + "\n");
            string nestedPath = "Packages/com.example.embedded/Editor/" + new string('x', 128);
            for (int index = 0; index < 12000; index++)
                entries.Append("100644 ").Append(Commit).Append(" 0\t").Append(nestedPath).Append(index).Append(".cs\n");
            Assert.That(entries.Length, Is.GreaterThan(2 * 1024 * 1024),
                "Reading all descendants would exceed the status output limit.");
            RunGit("update-index --index-info", input: entries.ToString());
            byte[] beforeIndex = File.ReadAllBytes(Path.Combine(root, ".git", "index"));

            var data = PackageManagerPackageStatus.ReadStatus(root, CancellationToken.None);

            Assert.That(data.Error, Is.Empty);
            Assert.That(data.Gitlinks, Is.EquivalentTo(new[] { RelativePath }));
            Assert.That(PackageManagerPackageStatus.TryClassify(root, Name, packagePath, data, out _), Is.True);
            Assert.That(File.ReadAllBytes(Path.Combine(root, ".git", "index")), Is.EqualTo(beforeIndex));
        }

        [TestCase("Packages/manifest.json", true)]
        [TestCase("Packages/com.example.embedded/Editor/Conflicted.cs", false)]
        public void IndexConflictValidationRemainsScopedToDirectPackageEntries(string conflictPath, bool fails)
        {
            RunGit("init --quiet");
            WriteRegistration();
            RunGit("update-index --index-info", input:
                "160000 " + Commit + " 0\t" + RelativePath + "\n" +
                "100644 " + Commit + " 1\t" + conflictPath + "\n" +
                "100644 " + new string('a', 40) + " 2\t" + conflictPath + "\n");
            byte[] beforeIndex = File.ReadAllBytes(Path.Combine(root, ".git", "index"));

            var data = PackageManagerPackageStatus.ReadStatus(root, CancellationToken.None);

            Assert.That(string.IsNullOrEmpty(data.Error), Is.EqualTo(!fails));
            Assert.That(PackageManagerPackageStatus.TryClassify(root, Name, packagePath, data, out _), Is.EqualTo(!fails));
            Assert.That(Block(data), Is.True);
            Assert.That(File.ReadAllBytes(Path.Combine(root, ".git", "index")), Is.EqualTo(beforeIndex));
        }

        [Test]
        public void ReadsLinkedWorktreeMetadataFromItsExternalIndex()
        {
            RunGit("init --quiet");
            RunGit("-c user.name=PackageTests -c user.email=package-tests@example.invalid -c commit.gpgsign=false " +
                "-c core.hooksPath=\"" + Path.Combine(root, "empty-hooks") + "\" commit --allow-empty --quiet -m Initial");
            string worktree = Path.Combine(root, "linked worktree");
            RunGit("worktree add --quiet --detach \"" + worktree + "\" HEAD");
            string linkedPackage = Path.Combine(worktree, "Packages", Name);
            Directory.CreateDirectory(linkedPackage);
            RunGit("update-index --add --cacheinfo 160000," + Commit + "," + RelativePath, worktree);
            File.WriteAllText(Path.Combine(worktree, ".gitmodules"), "[submodule \"example\"]\npath = " + RelativePath +
                "\nurl = https://github.com/example/package.git\n");

            var data = PackageManagerPackageStatus.ReadStatus(worktree, CancellationToken.None);

            Assert.That(File.Exists(Path.Combine(worktree, ".git")), Is.True);
            Assert.That(data.Error, Is.Empty);
            string gitDirectory = File.ReadAllText(Path.Combine(worktree, ".git")).Trim().Substring("gitdir: ".Length);
            Assert.That(data.IndexPath, Is.EqualTo(Path.GetFullPath(Path.Combine(gitDirectory, "index"))));
            Assert.That(File.Exists(data.IndexPath), Is.True);
            Assert.That(PackageManagerPackageStatus.TryClassify(worktree, Name, linkedPackage, data, out _), Is.True);
            Assert.That(new PackageManagerPackageStatus.StatusState().TryCompleteRefresh(worktree, data), Is.True);
        }

        [Test]
        public void LinkedModulesFileFailsClosedWithoutReadingItsTarget()
        {
            if (Path.DirectorySeparatorChar == '\\')
                Assert.Ignore("This fixture uses the Unix ln command to create a real file symlink.");
            RunGit("init --quiet");
            RunGit("update-index --add --cacheinfo 160000," + Commit + "," + RelativePath);
            string target = Path.Combine(root, "linked-modules.config");
            string modules = Path.Combine(root, ".gitmodules");
            string contents = "[submodule \"example\"]\npath = " + RelativePath +
                "\nurl = https://github.com/example/package.git\n";
            File.WriteAllText(target, contents);
            RunCommand("ln", "-s \"" + target + "\" \"" + modules + "\"");
            try
            {
                Assert.That(new FileInfo(modules).Attributes & FileAttributes.ReparsePoint, Is.EqualTo(FileAttributes.ReparsePoint));
                byte[] index = File.ReadAllBytes(Path.Combine(root, ".git", "index"));

                var data = PackageManagerPackageStatus.ReadStatus(root, CancellationToken.None);

                Assert.That(data.Error, Is.EqualTo("The .gitmodules file is linked or too large."));
                Assert.That(data.Repositories, Is.Empty);
                Assert.That(PackageManagerPackageStatus.TryClassify(root, Name, packagePath, data, out _), Is.False);
                Assert.That(Block(data), Is.True);
                Assert.That(File.ReadAllText(target), Is.EqualTo(contents));
                Assert.That(File.ReadAllBytes(Path.Combine(root, ".git", "index")), Is.EqualTo(index));
            }
            finally
            {
                File.Delete(modules);
            }
        }

        [Test]
        public void OversizedModulesFileFailsClosedAndRecoversAfterReplacement()
        {
            RunGit("init --quiet");
            RunGit("update-index --add --cacheinfo 160000," + Commit + "," + RelativePath);
            string modules = Path.Combine(root, ".gitmodules");
            string oversized = "#" + new string('x', 128 * 1024);
            File.WriteAllText(modules, oversized);
            byte[] index = File.ReadAllBytes(Path.Combine(root, ".git", "index"));

            var failed = PackageManagerPackageStatus.ReadStatus(root, CancellationToken.None);

            Assert.That(failed.Error, Is.EqualTo("The .gitmodules file is linked or too large."));
            Assert.That(PackageManagerPackageStatus.TryClassify(root, Name, packagePath, failed, out _), Is.False);
            Assert.That(Block(failed), Is.True);
            Assert.That(File.ReadAllText(modules), Is.EqualTo(oversized));
            Assert.That(File.ReadAllBytes(Path.Combine(root, ".git", "index")), Is.EqualTo(index));

            WriteRegistration();
            var recovered = PackageManagerPackageStatus.ReadStatus(root, CancellationToken.None);
            Assert.That(recovered.Error, Is.Empty);
            Assert.That(PackageManagerPackageStatus.TryClassify(root, Name, packagePath, recovered, out _), Is.True);
            Assert.That(Block(recovered), Is.True);
        }

        [Test]
        public void ModulesIncludesCannotSupplySubmoduleRegistrations()
        {
            RunGit("init --quiet");
            RunGit("update-index --add --cacheinfo 160000," + Commit + "," + RelativePath);
            string included = Path.Combine(root, "included.config");
            string modules = Path.Combine(root, ".gitmodules");
            string contents = "[submodule \"example\"]\npath = " + RelativePath +
                "\nurl = https://github.com/example/package.git\n";
            File.WriteAllText(included, contents);
            const string include = "[include]\npath = included.config\n";
            File.WriteAllText(modules, include);
            byte[] index = File.ReadAllBytes(Path.Combine(root, ".git", "index"));

            var data = PackageManagerPackageStatus.ReadStatus(root, CancellationToken.None);

            Assert.That(data.Error, Is.Empty);
            Assert.That(data.Repositories, Is.Empty);
            Assert.That(data.Gitlinks, Does.Contain(RelativePath));
            Assert.That(PackageManagerPackageStatus.TryClassify(root, Name, packagePath, data, out _), Is.False);
            Assert.That(Block(data), Is.True);
            Assert.That(File.ReadAllText(included), Is.EqualTo(contents));
            Assert.That(File.ReadAllText(modules), Is.EqualTo(include));
            Assert.That(File.ReadAllBytes(Path.Combine(root, ".git", "index")), Is.EqualTo(index));
        }

        [TestCase("GIT_DIR")]
        [TestCase("GIT_WORK_TREE")]
        [TestCase("GIT_INDEX_FILE")]
        [TestCase("GIT_CONFIG_COUNT")]
        public void InheritedGitEnvironmentCannotRedirectTheProjectScan(string variable)
        {
            RunGit("init --quiet");
            RunGit("update-index --add --cacheinfo 160000," + Commit + "," + RelativePath);
            WriteRegistration();
            byte[] index = File.ReadAllBytes(Path.Combine(root, ".git", "index"));
            byte[] modules = File.ReadAllBytes(Path.Combine(root, ".gitmodules"));
            string unrelated = Path.Combine(root, "unrelated");
            Directory.CreateDirectory(unrelated);
            var replacements = new Dictionary<string, string> { [variable] = unrelated };
            if (variable == "GIT_CONFIG_COUNT")
            {
                replacements[variable] = "1";
                replacements["GIT_CONFIG_KEY_0"] = "core.bare";
                replacements["GIT_CONFIG_VALUE_0"] = "true";
            }
            var saved = new Dictionary<string, string>();
            PackageManagerPackageStatus.StatusSnapshot data;
            try
            {
                foreach (KeyValuePair<string, string> entry in replacements)
                {
                    saved.Add(entry.Key, Environment.GetEnvironmentVariable(entry.Key));
                    Environment.SetEnvironmentVariable(entry.Key, entry.Value);
                }
                data = PackageManagerPackageStatus.ReadStatus(root, CancellationToken.None);
            }
            finally
            {
                foreach (KeyValuePair<string, string> entry in saved)
                    Environment.SetEnvironmentVariable(entry.Key, entry.Value);
            }

            Assert.That(data.Error, Is.Empty);
            Assert.That(data.IndexPath, Is.EqualTo(Path.Combine(root, ".git", "index")));
            Assert.That(PackageManagerPackageStatus.TryClassify(root, Name, packagePath, data, out _), Is.True);
            Assert.That(File.ReadAllBytes(Path.Combine(root, ".git", "index")), Is.EqualTo(index));
            Assert.That(File.ReadAllBytes(Path.Combine(root, ".gitmodules")), Is.EqualTo(modules));
            Assert.That(Directory.GetFileSystemEntries(unrelated), Is.Empty);
        }

        [Test]
        public void ReadsRealSha256GitlinksWhenGitSupportsThatObjectFormat()
        {
            int exit = ExecuteCommand("git", "init --quiet --object-format=sha256", out string error);
            if (exit != 0 && (error.IndexOf("unknown option", StringComparison.OrdinalIgnoreCase) >= 0 ||
                error.IndexOf("unknown hash algorithm", StringComparison.OrdinalIgnoreCase) >= 0))
                Assert.Ignore("This Git installation does not support SHA-256 repositories: " + error.Trim());
            Assert.That(exit, Is.Zero, error);
            RunGit("update-index --add --cacheinfo 160000," + new string('a', 64) + "," + RelativePath);
            WriteRegistration();
            byte[] index = File.ReadAllBytes(Path.Combine(root, ".git", "index"));

            var data = PackageManagerPackageStatus.ReadStatus(root, CancellationToken.None);

            Assert.That(data.Error, Is.Empty);
            Assert.That(data.Gitlinks, Is.EquivalentTo(new[] { RelativePath }));
            Assert.That(PackageManagerPackageStatus.TryClassify(root, Name, packagePath, data, out _), Is.True);
            Assert.That(File.ReadAllBytes(Path.Combine(root, ".git", "index")), Is.EqualTo(index));
        }

        [Test]
        public void RealConflictedGitlinkFailsClosedAndRecoversWhenResolved()
        {
            RunGit("init --quiet");
            WriteRegistration();
            RunGit("update-index --index-info", input:
                "160000 " + Commit + " 1\t" + RelativePath + "\n" +
                "160000 " + new string('a', 40) + " 2\t" + RelativePath + "\n" +
                "160000 " + new string('b', 40) + " 3\t" + RelativePath + "\n");
            byte[] index = File.ReadAllBytes(Path.Combine(root, ".git", "index"));

            var conflicted = PackageManagerPackageStatus.ReadStatus(root, CancellationToken.None);

            Assert.That(conflicted.Error, Is.EqualTo("The Git index contains malformed or conflicted package entries."));
            Assert.That(PackageManagerPackageStatus.TryClassify(root, Name, packagePath, conflicted, out _), Is.False);
            Assert.That(Block(conflicted), Is.True);
            Assert.That(File.ReadAllBytes(Path.Combine(root, ".git", "index")), Is.EqualTo(index));

            RunGit("update-index --force-remove -- " + RelativePath);
            RunGit("update-index --add --cacheinfo 160000," + Commit + "," + RelativePath);
            var resolved = PackageManagerPackageStatus.ReadStatus(root, CancellationToken.None);
            Assert.That(resolved.Error, Is.Empty);
            Assert.That(PackageManagerPackageStatus.TryClassify(root, Name, packagePath, resolved, out _), Is.True);
            Assert.That(Block(resolved), Is.True);
        }

        [TestCase("https://github.com/example/package.git", true)]
        [TestCase("https://gitlab.com/example/package.git", false)]
        [TestCase("ssh://git@example.invalid/example/package.git", false)]
        public void RegisteredSubmoduleSourceDeterminesClassificationAndGitHubMembership(string url, bool expected)
        {
            var data = InstalledSnapshot();
            data.Repositories[RelativePath] = url;
            object native = new { versions = new { primary = NativeVersion() } };

            Assert.That(PackageManagerPackageStatus.TryClassify(root, Name, packagePath, data,
                out PackageSubmoduleStatus status), Is.True);
            Assert.That(status.IsGitHub, Is.EqualTo(expected));
            Assert.That(PackageManagerPackageStatus.IsGitHubPackage(root, native, data), Is.EqualTo(expected));
            data.Repositories.Clear();
            Assert.That(PackageManagerPackageStatus.IsGitHubPackage(root, native, data), Is.False);
        }

        [TestCase("not-installed")]
        [TestCase("unregistered")]
        [TestCase("error")]
        [TestCase("malformed-native")]
        public void UnverifiedOrUninstalledSubmodulesHaveNoMembershipOrRepositoryLink(string reason)
        {
            var data = InstalledSnapshot();
            object version = new { name = Name,
                isInstalled = reason != "not-installed", localPath = packagePath,
                packageId = data.Packages[Name].PackageId };
            if (reason == "unregistered") data.Packages.Clear();
            if (reason == "error") data.Error = "Local Git status unavailable.";
            if (reason == "malformed-native") version = new object();

            object native = reason == "malformed-native" ? new object() : new { versions = new { primary = version } };
            Assert.That(PackageManagerPackageStatus.IsGitHubPackage(root, native, data), Is.False);
            Assert.That(PackageManagerPackageStatus.GetRepositoryUrl(root, version, data), Is.Empty);
        }

        [TestCase(false, "matching")]
        [TestCase(false, "missing")]
        [TestCase(false, "different")]
        [TestCase(true, "matching")]
        [TestCase(true, "missing")]
        [TestCase(true, "different")]
        public void MembershipAndRepositoryLinkRequireTheNativeInstalledPath(bool gitDependency, string nativePath)
        {
            var data = InstalledSnapshot();
            if (gitDependency)
            {
                data.Repositories.Clear();
                data.Gitlinks.Clear();
                data.Packages[Name] = new PackageManagerPackageStatus.InstalledPackage(packagePath, PackageSource.Git,
                    Name + "@https://github.com/example/package.git");
            }
            object version = new { name = Name, isInstalled = true,
                localPath = nativePath == "different" ? packagePath + "-other" : packagePath,
                packageId = data.Packages[Name].PackageId };
            if (nativePath == "missing") version = new { name = Name,
                isInstalled = true, packageId = data.Packages[Name].PackageId };

            object native = new { versions = new { primary = version } };
            Assert.That(PackageManagerPackageStatus.IsGitHubPackage(root, native, data), Is.EqualTo(nativePath == "matching"));
            Assert.That(PackageManagerPackageStatus.GetRepositoryUrl(root, version, data),
                Is.EqualTo(nativePath == "matching" ? "https://github.com/example/package" : string.Empty));
        }

        [TestCase(false, "matching")]
        [TestCase(false, "missing")]
        [TestCase(false, "different")]
        [TestCase(true, "matching")]
        [TestCase(true, "missing")]
        [TestCase(true, "different")]
        public void MembershipAndRepositoryLinkRequireTheNativeInstalledIdentity(bool gitDependency, string identity)
        {
            var data = InstalledSnapshot();
            if (gitDependency)
            {
                data.Repositories.Clear();
                data.Gitlinks.Clear();
                data.Packages[Name] = new PackageManagerPackageStatus.InstalledPackage(packagePath, PackageSource.Git,
                    Name + "@https://github.com/example/package.git");
            }
            string id = identity == "matching" ? data.Packages[Name].PackageId :
                identity == "missing" ? null : Name + "@https://gitlab.com/example/package.git";
            object version = new { name = Name, isInstalled = true, localPath = packagePath, packageId = id };
            object native = new { versions = new { primary = version } };
            var state = new PackageManagerPackageStatus.StatusState();
            state.CompleteRefresh(data);
            state.BeginRefresh();

            Assert.That(PackageManagerPackageStatus.IsGitHubPackage(root, native, state.Snapshot),
                Is.EqualTo(identity == "matching"),
                "Pending refresh may retain the published snapshot, but cannot apply its source to another installed identity.");
            Assert.That(PackageManagerPackageStatus.GetRepositoryUrl(root, version, state.Snapshot),
                Is.EqualTo(identity == "matching" ? "https://github.com/example/package" : string.Empty));
            Assert.That(state.Snapshot, Is.SameAs(data));
        }

        [TestCase("registration")]
        [TestCase("gitlink")]
        [TestCase("both")]
        [TestCase("alias")]
        [TestCase("renamed-folder")]
        public void UncertainCheckoutStillBlocksRemovalWithoutStatusDetailText(string missing)
        {
            var data = ReadOrdinaryRepository();
            string path = missing == "renamed-folder" ? Path.Combine(root, "Packages", "Renamed") : packagePath;
            Directory.CreateDirectory(Path.Combine(path, ".git"));
            data.Packages.Add(Name, new PackageManagerPackageStatus.InstalledPackage(path, PackageSource.Embedded, Name + "@file:" + path));
            if (missing == "registration") data.Gitlinks.Add(RelativePath);
            if (missing == "gitlink") data.Repositories.Add(RelativePath, "https://example.invalid/package.git");
            if (missing == "alias") data.Repositories.Add("./" + RelativePath, "https://example.invalid/package.git");

            Assert.That(PackageManagerPackageStatus.ShouldBlockEmbeddedRemoval(root, Name, path, true, data), Is.True);
            Assert.That(PackageManagerPackageStatus.GetRepositoryUrl(root, NativeVersion(path, Name + "@file:" + path), data), Is.Empty);
        }

        [Test]
        public void GitOnlyRefreshReusesAcceptedRegisteredPackages()
        {
            var state = new PackageManagerPackageStatus.StatusState();
            var initial = InstalledSnapshot();
            int reads = 0;
            Dictionary<string, PackageManagerPackageStatus.InstalledPackage> Read()
            {
                reads++;
                return initial.Packages;
            }
            state.BeginRefresh(true);
            initial.Packages = state.ReadRegisteredPackages(Read, out int revision);
            initial.PackagesRevision = revision;
            state.CompleteRefresh(initial);
            state.BeginRefresh();
            Assert.That(state.ReadRegisteredPackages(Read, out int reusedRevision), Is.SameAs(initial.Packages));
            Assert.That(reusedRevision, Is.EqualTo(revision));
            Assert.That(reads, Is.EqualTo(1));

            state.BeginRefresh(true);
            state.BeginRefresh();
            state.ReadRegisteredPackages(Read, out int changedRevision);
            Assert.That(changedRevision, Is.GreaterThan(revision));
            Assert.That(reads, Is.EqualTo(2), "A metadata refresh must not erase a pending package invalidation.");
        }

        [Test]
        public void ReusedRegisteredPackagesStillDetectChangedGitMetadata()
        {
            var initial = InstalledSnapshot();
            var refreshed = InstalledSnapshot();
            refreshed.Packages = initial.Packages;
            var state = new PackageManagerPackageStatus.StatusState();
            state.CompleteRefresh(initial);
            int changes = 0;
            state.Changed += () => changes++;

            state.CompleteRefresh(refreshed);
            Assert.That(changes, Is.Zero);
            var changed = InstalledSnapshot();
            changed.Packages = initial.Packages;
            changed.Repositories[RelativePath] = "https://gitlab.com/example/package.git";
            state.CompleteRefresh(changed);

            Assert.That(changes, Is.EqualTo(1));
            Assert.That(PackageManagerPackageStatus.IsGitHubPackage(root,
                new { versions = new { primary = NativeVersion() } }, state.Snapshot), Is.False);
            Assert.That(state.Snapshot.Packages, Is.SameAs(initial.Packages));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void UnacceptedOrFailedPackageCaptureCannotBeReused(bool failed)
        {
            var state = new PackageManagerPackageStatus.StatusState();
            int reads = 0;
            Dictionary<string, PackageManagerPackageStatus.InstalledPackage> Read()
            {
                reads++;
                return InstalledSnapshot().Packages;
            }
            state.BeginRefresh(true);
            var packages = state.ReadRegisteredPackages(Read, out int revision);
            if (failed)
                state.CompleteRefresh(new PackageManagerPackageStatus.StatusSnapshot
                    { Packages = packages, PackagesRevision = revision, Error = "Unreadable metadata" });
            state.BeginRefresh();
            state.ReadRegisteredPackages(Read, out _);
            Assert.That(reads, Is.EqualTo(2));
        }

        [Test]
        public void ReentrantPackageInvalidationIsNotLostWhenSnapshotPublishes()
        {
            var state = new PackageManagerPackageStatus.StatusState();
            state.BeginRefresh(true);
            var initial = InstalledSnapshot();
            initial.Packages = state.ReadRegisteredPackages(() => initial.Packages, out int revision);
            initial.PackagesRevision = revision;
            state.Changed += () => state.BeginRefresh(true);
            state.CompleteRefresh(initial);
            int reads = 0;
            state.ReadRegisteredPackages(() => { reads++; return initial.Packages; }, out int current);
            Assert.That(current, Is.GreaterThan(revision));
            Assert.That(reads, Is.EqualTo(1));
            Assert.That(state.IsReady, Is.False);
        }

        [Test]
        public void RegisteredPackageCaptureFailureRemainsInvalidated()
        {
            var state = new PackageManagerPackageStatus.StatusState();
            state.BeginRefresh(true);
            Assert.Throws<InvalidOperationException>(() => state.ReadRegisteredPackages(
                () => throw new InvalidOperationException("Unavailable"), out _));
            state.BeginRefresh();
            int reads = 0;
            state.ReadRegisteredPackages(() => { reads++; return InstalledSnapshot().Packages; }, out _);
            Assert.That(reads, Is.EqualTo(1));
        }

        [Test]
        public void UnchangedRefreshNotifiesReadinessWithoutRebuildingPackageViews()
        {
            var state = new PackageManagerPackageStatus.StatusState();
            state.CompleteRefresh(InstalledSnapshot());
            int changed = 0;
            var readiness = new List<bool>();
            state.Changed += () => changed++;
            state.ReadinessChanged += () => readiness.Add(state.IsReady);

            state.BeginRefresh();
            state.BeginRefresh();
            Assert.That(PackageManagerPackageStatus.ShouldBlockEmbeddedRemoval(root, Name, packagePath,
                state.IsReady, state.Snapshot), Is.True);
            var refreshed = InstalledSnapshot();
            refreshed.IndexPath = Path.Combine(root, ".git", "different-index");
            state.CompleteRefresh(refreshed);

            Assert.That(changed, Is.Zero);
            Assert.That(readiness, Is.EqualTo(new[] { false, true }));
            Assert.That(state.Snapshot, Is.SameAs(refreshed), "A new metadata watch path must still be retained.");
        }

        [TestCase("url")]
        [TestCase("gitlink")]
        [TestCase("registration")]
        [TestCase("error")]
        [TestCase("package-path")]
        [TestCase("package-source")]
        [TestCase("package-id")]
        [TestCase("package-added")]
        [TestCase("package-removed")]
        public void ChangedInstalledStatusNotifiesPackageViewsOnce(string change)
        {
            var state = new PackageManagerPackageStatus.StatusState();
            state.CompleteRefresh(InstalledSnapshot());
            int changed = 0;
            state.Changed += () => changed++;
            var refreshed = InstalledSnapshot();
            switch (change)
            {
                case "url": refreshed.Repositories[RelativePath] = "https://gitlab.com/example/package.git"; break;
                case "gitlink": refreshed.Gitlinks.Clear(); break;
                case "registration": refreshed.Repositories.Clear(); break;
                case "error": refreshed.Error = "Could not read local Git status."; break;
                case "package-path":
                    refreshed.Packages[Name] = new PackageManagerPackageStatus.InstalledPackage(
                        Path.Combine(root, "Library", "PackageCache", Name), PackageSource.Embedded, Name + "@file:" + packagePath);
                    break;
                case "package-source":
                    refreshed.Packages[Name] = new PackageManagerPackageStatus.InstalledPackage(
                        packagePath, PackageSource.Local, Name + "@file:" + packagePath);
                    break;
                case "package-id":
                    refreshed.Packages[Name] = new PackageManagerPackageStatus.InstalledPackage(
                        packagePath, PackageSource.Embedded, Name + "@https://github.com/example/package.git");
                    break;
                case "package-added":
                    refreshed.Packages.Add("com.example.other", new PackageManagerPackageStatus.InstalledPackage(
                        Path.Combine(root, "Packages", "com.example.other"), PackageSource.Embedded, "com.example.other@1.0.0"));
                    break;
                case "package-removed": refreshed.Packages.Clear(); break;
            }

            state.BeginRefresh();
            state.CompleteRefresh(refreshed);

            Assert.That(changed, Is.EqualTo(1));
            Assert.That(state.Snapshot, Is.SameAs(refreshed));
            Assert.That(state.IsReady, Is.True);
        }

        [Test]
        public void PendingRefreshKeepsThePublishedPackageAndGitSnapshotTogether()
        {
            var state = new PackageManagerPackageStatus.StatusState();
            var initial = InstalledSnapshot();
            state.CompleteRefresh(initial);
            state.BeginRefresh();
            var refreshed = new PackageManagerPackageStatus.StatusSnapshot();

            Assert.That(state.Snapshot.Packages.ContainsKey(Name), Is.True);
            Assert.That(PackageManagerPackageStatus.TryClassify(root, Name, packagePath, state.Snapshot, out _), Is.True);
            Assert.That(PackageManagerPackageStatus.ShouldBlockEmbeddedRemoval(root, Name, packagePath,
                state.IsReady, state.Snapshot), Is.True);
            state.CompleteRefresh(refreshed);
            Assert.That(state.Snapshot.Packages.ContainsKey(Name), Is.False);
            Assert.That(PackageManagerPackageStatus.TryClassify(root, Name, packagePath, state.Snapshot, out _), Is.False);
        }

        [Test]
        public void ReadinessNotificationsObserveCurrentOrdinaryPackageRemovalSafety()
        {
            var state = new PackageManagerPackageStatus.StatusState();
            var initial = ReadOrdinaryRepository();
            state.CompleteRefresh(initial);
            var blocked = new List<bool>();
            state.ReadinessChanged += () => blocked.Add(PackageManagerPackageStatus.ShouldBlockEmbeddedRemoval(
                root, Name, packagePath, state.IsReady, state.Snapshot));

            state.BeginRefresh();
            state.BeginRefresh();
            state.CompleteRefresh(PackageManagerPackageStatus.ReadStatus(root, CancellationToken.None));

            Assert.That(blocked, Is.EqualTo(new[] { true, false }));
        }

        [Test]
        public void ReadFailureAndRecoveryNotifyOnceAndPreserveRemovalProtection()
        {
            var state = new PackageManagerPackageStatus.StatusState();
            state.CompleteRefresh(ReadOrdinaryRepository());
            int changed = 0;
            state.Changed += () => changed++;

            state.BeginRefresh();
            state.CompleteRefresh(new PackageManagerPackageStatus.StatusSnapshot { Error = "Git is unavailable." });
            Assert.That(changed, Is.EqualTo(1));
            Assert.That(PackageManagerPackageStatus.ShouldBlockEmbeddedRemoval(root, Name, packagePath,
                state.IsReady, state.Snapshot), Is.True);

            state.BeginRefresh();
            state.CompleteRefresh(new PackageManagerPackageStatus.StatusSnapshot { Error = "Git is unavailable." });
            Assert.That(changed, Is.EqualTo(1));
            Assert.That(PackageManagerPackageStatus.ShouldBlockEmbeddedRemoval(root, Name, packagePath,
                state.IsReady, state.Snapshot), Is.True);

            state.BeginRefresh();
            state.CompleteRefresh(PackageManagerPackageStatus.ReadStatus(root, CancellationToken.None));
            Assert.That(changed, Is.EqualTo(2));
            Assert.That(PackageManagerPackageStatus.ShouldBlockEmbeddedRemoval(root, Name, packagePath,
                state.IsReady, state.Snapshot), Is.False);
        }

        [TestCase("index", 0)]
        [TestCase("modules", 0)]
        [TestCase("index", 1)]
        [TestCase("modules", 1)]
        public void MetadataChangeRejectsStaleNegativeStatusAndRetriesOnlyWhileObserved(string changedFile, int observers)
        {
            RunGit("init --quiet");
            var state = new PackageManagerPackageStatus.StatusState();
            var initial = PackageManagerPackageStatus.ReadStatus(root, CancellationToken.None);
            Assert.That(state.TryCompleteRefresh(root, initial), Is.True);
            Assert.That(PackageManagerPackageStatus.ShouldBlockEmbeddedRemoval(root, Name, packagePath,
                state.IsReady, state.Snapshot), Is.False);
            int changed = 0;
            state.Changed += () => changed++;
            state.BeginRefresh();
            var stale = PackageManagerPackageStatus.ReadStatus(root, CancellationToken.None);
            Assert.That(stale.Error, Is.Empty);

            if (changedFile == "index")
                RunGit("update-index --add --cacheinfo 160000," + Commit + "," + RelativePath);
            else
                File.WriteAllText(Path.Combine(root, ".gitmodules"), "[submodule \"example\"]\npath = " + RelativePath +
                    "\nurl = https://github.com/example/package.git\n");

            int retries = 0;
            Assert.That(state.TryCompleteRefresh(root, stale, observers, () => retries++), Is.False);
            Assert.That(retries, Is.EqualTo(observers));
            Assert.That(state.IsReady, Is.False);
            Assert.That(state.Snapshot, Is.SameAs(initial));
            Assert.That(changed, Is.Zero);
            Assert.That(PackageManagerPackageStatus.ShouldBlockEmbeddedRemoval(root, Name, packagePath,
                state.IsReady, state.Snapshot), Is.True);

            var fresh = PackageManagerPackageStatus.ReadStatus(root, CancellationToken.None);
            Assert.That(fresh.Error, Is.Empty);
            Assert.That(state.TryCompleteRefresh(root, fresh), Is.True);
            Assert.That(state.IsReady, Is.True);
            Assert.That(changed, Is.EqualTo(1));
            Assert.That(PackageManagerPackageStatus.ShouldBlockEmbeddedRemoval(root, Name, packagePath,
                state.IsReady, state.Snapshot), Is.True);
        }

        [Test]
        public void SuccessfulResultWithoutMetadataStampCannotPublishReadyStatus()
        {
            var state = new PackageManagerPackageStatus.StatusState();
            Assert.That(state.TryCompleteRefresh(root, new PackageManagerPackageStatus.StatusSnapshot()), Is.False);
            Assert.That(state.IsReady, Is.False);
            Assert.That(PackageManagerPackageStatus.ShouldBlockEmbeddedRemoval(root, Name, packagePath,
                state.IsReady, state.Snapshot), Is.True);
        }

        [Test]
        public void UnstampedReadySnapshotCannotAuthorizeEmbeddedRemoval()
        {
            Assert.That(PackageManagerPackageStatus.ShouldBlockEmbeddedRemoval(root, Name, packagePath,
                true, new PackageManagerPackageStatus.StatusSnapshot()), Is.True);
        }

        [TestCase("index")]
        [TestCase("modules")]
        public void MetadataChangeAfterPublicationBlocksRemovalWithoutAnObserverRefresh(string changedFile)
        {
            RunGit("init --quiet");
            var state = new PackageManagerPackageStatus.StatusState();
            var data = PackageManagerPackageStatus.ReadStatus(root, CancellationToken.None);
            Assert.That(data.Error, Is.Empty);
            Assert.That(state.TryCompleteRefresh(root, data), Is.True);
            Assert.That(PackageManagerPackageStatus.ShouldBlockEmbeddedRemoval(root, Name, packagePath,
                state.IsReady, state.Snapshot), Is.False);

            if (changedFile == "index")
                RunGit("update-index --add --cacheinfo 160000," + Commit + "," + RelativePath);
            else
                File.WriteAllText(Path.Combine(root, ".gitmodules"), "[submodule \"example\"]\npath = " + RelativePath +
                    "\nurl = https://github.com/example/package.git\n");

            Assert.That(state.IsReady, Is.True, "No observer or periodic refresh has invalidated the cached result.");
            Assert.That(PackageManagerPackageStatus.ShouldBlockEmbeddedRemoval(root, Name, packagePath,
                state.IsReady, state.Snapshot), Is.True, "Deletion must recheck metadata before using a negative cached result.");
        }

        [TestCase(false, false, 1)]
        [TestCase(true, false, 2)]
        [TestCase(false, true, 2)]
        [TestCase(true, true, 3)]
        public void ReusesOnlyUnchangedMetadataIncludingEqualLengthAndTimestampChanges(bool changeIndex, bool changeModules, int expectedCommands)
        {
            CreateMetadataFixture();
            var commands = new List<string>();
            var first = ReadMetadataFixture(null, commands);
            Assert.That(first.Error, Is.Empty);
            Assert.That(commands.Count, Is.EqualTo(3));
            if (changeIndex)
                ReplacePreservingTimestamp(Path.Combine(root, ".git", "index"), "160000", "100644");
            if (changeModules)
                ReplacePreservingTimestamp(Path.Combine(root, ".gitmodules"), "example/package.git", "example/changed.git");
            commands.Clear();
            var next = ReadMetadataFixture(first, commands);
            Assert.That(next.Error, Is.Empty);
            Assert.That(next.FilesStamp, Is.Not.Null);
            Assert.That(commands.Count, Is.EqualTo(expectedCommands));
            Assert.That(next.Gitlinks.Contains(RelativePath), Is.EqualTo(!changeIndex));
            Assert.That(next.Repositories[RelativePath], Is.EqualTo(changeModules ?
                "https://github.com/example/changed.git" : "https://github.com/example/package.git"));
            Assert.That(first.Gitlinks.Contains(RelativePath), Is.True);
            Assert.That(first.Repositories[RelativePath], Is.EqualTo("https://github.com/example/package.git"));
            next.Gitlinks.Clear();
            next.Repositories.Clear();
            Assert.That(first.Gitlinks, Is.Not.Empty, "Reused collections must be copied.");
            Assert.That(first.Repositories, Is.Not.Empty);
        }

        [Test]
        public void FailedMetadataSnapshotCannotSupplyCachedResults()
        {
            CreateMetadataFixture();
            var commands = new List<string>();
            var first = ReadMetadataFixture(null, commands);
            first.Error = "Unverified";
            commands.Clear();
            Assert.That(ReadMetadataFixture(first, commands).Error, Is.Empty);
            Assert.That(commands.Count, Is.EqualTo(3));
        }

        [Test]
        public void MissingMetadataFilesCanBeReusedWithoutInventingRegistrations()
        {
            Directory.CreateDirectory(Path.Combine(root, ".git"));
            var commands = new List<string>();
            var first = ReadMetadataFixture(null, commands);
            Assert.That(first.Error, Is.Empty);
            Assert.That(commands.Count, Is.EqualTo(2));
            commands.Clear();
            var next = ReadMetadataFixture(first, commands);
            Assert.That(next.Error, Is.Empty);
            Assert.That(commands.Count, Is.EqualTo(1));
            Assert.That(next.Gitlinks, Is.Empty);
            Assert.That(next.Repositories, Is.Empty);
        }

        [TestCase(true)]
        [TestCase(false)]
        public void SplitOrOversizedIndexAlwaysUsesGitValidation(bool splitIndex)
        {
            CreateMetadataFixture();
            if (!splitIndex)
                using (var stream = File.OpenWrite(Path.Combine(root, ".git", "index")))
                    stream.SetLength(4 * 1024 * 1024 + 1);
            var commands = new List<string>();
            var first = ReadMetadataFixture(null, commands, splitIndex, indexOutput: string.Empty);
            Assert.That(first.Error, Is.Empty);
            Assert.That(first.IndexFingerprint, Is.Null);
            commands.Clear();
            var next = ReadMetadataFixture(first, commands, splitIndex, indexOutput: string.Empty);
            Assert.That(next.Error, Is.Empty);
            Assert.That(commands.Count, Is.EqualTo(2));
            Assert.That(commands[1], Does.StartWith("ls-files"));
        }

        [TestCase("index")]
        [TestCase("modules")]
        public void ZeroLengthPipeMetadataDoesNotEnterFingerprintStreams(string metadata)
        {
            if (Path.DirectorySeparatorChar == '\\')
                Assert.Ignore("This fixture uses the Unix mkfifo command to create a real pipe.");
            CreateMetadataFixture();
            string path = metadata == "index" ? Path.Combine(root, ".git", "index") : Path.Combine(root, ".gitmodules");
            File.Delete(path);
            RunCommand("mkfifo", "\"" + path + "\"");
            // Keep both pipe ends open so a regression fails instead of leaving a blocked reader.
            using (var pipe = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete))
            {
                Assert.That(new FileInfo(path).Length, Is.Zero);
                var commands = new List<string>();
                var data = ReadMetadataFixture(null, commands,
                    indexOutput: "160000 " + Commit + " 0\t" + RelativePath + "\0", modulesOutput: string.Empty);

                Assert.That(data.Error, Is.Empty);
                Assert.That(metadata == "index" ? data.IndexFingerprint : data.ModulesFingerprint, Is.Null);
                Assert.That(commands.Count, Is.EqualTo(3), "Unfingerprinted metadata must still be read through Git.");
            }
        }

        [Test]
        public void MetadataCacheStillRejectsOversizedModulesAndCancellation()
        {
            CreateMetadataFixture();
            var commands = new List<string>();
            var first = ReadMetadataFixture(null, commands);
            File.WriteAllText(Path.Combine(root, ".gitmodules"), new string(' ', 128 * 1024 + 1));
            commands.Clear();
            Assert.That(ReadMetadataFixture(first, commands).Error, Does.Contain("too large"));
            Assert.That(commands.Count, Is.EqualTo(1));
            commands.Clear();
            using (var cancellation = new CancellationTokenSource())
            {
                cancellation.Cancel();
                Assert.That(ReadMetadataFixture(first, commands, token: cancellation.Token).Error, Does.Contain("cancelled"));
            }
            Assert.That(commands, Is.Empty);
        }

        [Test]
        public void MetadataChangedDuringReadCannotPublishEvenWithItsOriginalTimestamp()
        {
            CreateMetadataFixture();
            var commands = new List<string>();
            var first = ReadMetadataFixture(null, commands);
            ReplacePreservingTimestamp(Path.Combine(root, ".git", "index"), "160000", "100644");
            var next = ReadMetadataFixture(first, commands, beforeCommand: arguments =>
            {
                if (arguments.StartsWith("ls-files", StringComparison.Ordinal))
                    ReplacePreservingTimestamp(Path.Combine(root, ".gitmodules"), "example/package.git", "example/changed.git");
            });
            Assert.That(next.Error, Is.Empty);
            Assert.That(next.FilesStamp, Is.Null);
            Assert.That(new PackageManagerPackageStatus.StatusState().TryCompleteRefresh(root, next), Is.False);
        }

        [Test]
        public void ChangedGitDirectoryIdentityInvalidatesBothMetadataCaches()
        {
            CreateMetadataFixture();
            var commands = new List<string>();
            var first = ReadMetadataFixture(null, commands);
            commands.Clear();
            var next = ReadMetadataFixture(first, commands, gitDirectory: Path.Combine(root, "different.git"));
            Assert.That(next.Error, Is.Empty);
            Assert.That(commands.Count, Is.EqualTo(3));
        }

        [Test]
        public void RealSplitIndexRetainsGitValidationAndSubmoduleClassification()
        {
            RunGit("init --quiet");
            WriteRegistration();
            RunGit("update-index --add --cacheinfo 160000," + Commit + "," + RelativePath);
            RunGit("update-index --split-index");
            var first = PackageManagerPackageStatus.ReadStatus(root, CancellationToken.None);
            var next = PackageManagerPackageStatus.ReadStatus(root, CancellationToken.None, first);
            Assert.That(first.Error, Is.Empty);
            Assert.That(next.Error, Is.Empty);
            Assert.That(next.HasSharedIndex, Is.True);
            Assert.That(next.IndexFingerprint, Is.Null);
            Assert.That(next.Gitlinks.Contains(RelativePath), Is.True);
            Assert.That(next.Repositories[RelativePath], Is.EqualTo("https://github.com/example/package.git"));
        }

        [Test]
        public void CachedModulesAreRejectedWhenReplacedByASymlink()
        {
            if (Path.DirectorySeparatorChar == '\\')
                Assert.Ignore("This fixture uses the Unix ln command to create a real file symlink.");
            CreateMetadataFixture();
            var commands = new List<string>();
            var first = ReadMetadataFixture(null, commands);
            string modules = Path.Combine(root, ".gitmodules");
            string target = Path.Combine(root, "original-modules");
            File.Move(modules, target);
            RunCommand("ln", "-s \"" + target + "\" \"" + modules + "\"");
            try
            {
                commands.Clear();
                Assert.That(ReadMetadataFixture(first, commands).Error, Does.Contain("linked"));
                Assert.That(commands.Count, Is.EqualTo(1));
                Assert.That(File.ReadAllText(target), Does.Contain("https://github.com/example/package.git"));
            }
            finally { File.Delete(modules); }
        }

        [TestCase("relative.git", null)]
        [TestCase("/valid.git\r\r", null)]
        [TestCase("/valid.git", "\r\r")]
        [TestCase("/valid.git", "bad\0shared")]
        public void ReuseRequiresUnambiguousRepositoryIdentity(string directory, string sharedIndex)
        {
            var result = PackageManagerPackageStatus.ReadStatus(root, CancellationToken.None, null, (arguments, allowNoMatch) =>
                "\n.git/index\n" + directory + "\n" + (sharedIndex == null ? string.Empty : sharedIndex + "\n"));
            Assert.That(result.Error, Does.Contain("malformed"));
        }

        private void CreateMetadataFixture()
        {
            Directory.CreateDirectory(Path.Combine(root, ".git"));
            File.WriteAllText(Path.Combine(root, ".git", "index"), "160000 " + Commit + " 0\t" + RelativePath + "\0");
            File.WriteAllText(Path.Combine(root, ".gitmodules"), "submodule.example.path\n" + RelativePath + "\0" +
                "submodule.example.url\nhttps://github.com/example/package.git\0");
        }

        private PackageManagerPackageStatus.StatusSnapshot ReadMetadataFixture(PackageManagerPackageStatus.StatusSnapshot previous,
            List<string> commands, bool splitIndex = false, string gitDirectory = null, Action<string> beforeCommand = null,
            string indexOutput = null, CancellationToken token = default, string modulesOutput = null)
        {
            return PackageManagerPackageStatus.ReadStatus(root, token, previous, (arguments, allowNoMatch) =>
            {
                commands.Add(arguments);
                beforeCommand?.Invoke(arguments);
                if (arguments.StartsWith("rev-parse", StringComparison.Ordinal))
                    return "\n.git/index\n" + (gitDirectory ?? Path.Combine(root, ".git")) + "\n" +
                        (splitIndex ? ".git/sharedindex.fixture\n" : string.Empty);
                if (arguments.StartsWith("ls-files", StringComparison.Ordinal))
                    return indexOutput ?? (File.Exists(Path.Combine(root, ".git", "index")) ?
                        File.ReadAllText(Path.Combine(root, ".git", "index")) : string.Empty);
                Assert.That(allowNoMatch, Is.True);
                return modulesOutput ?? File.ReadAllText(Path.Combine(root, ".gitmodules"));
            });
        }

        private static void ReplacePreservingTimestamp(string path, string before, string after)
        {
            Assert.That(before.Length, Is.EqualTo(after.Length));
            DateTime timestamp = File.GetLastWriteTimeUtc(path);
            File.WriteAllText(path, File.ReadAllText(path).Replace(before, after));
            File.SetLastWriteTimeUtc(path, timestamp);
        }

        private PackageManagerPackageStatus.StatusSnapshot InstalledSnapshot()
        {
            var data = Snapshot();
            data.Packages.Add(Name, new PackageManagerPackageStatus.InstalledPackage(
                packagePath, PackageSource.Embedded, Name + "@file:" + packagePath));
            return data;
        }

        private object NativeVersion(string path = null, string packageId = null) => new
        {
            name = Name,
            isInstalled = true,
            localPath = path ?? packagePath,
            packageId = packageId ?? Name + "@file:" + packagePath
        };

        private PackageManagerPackageStatus.StatusSnapshot ReadOrdinaryRepository()
        {
            RunGit("init --quiet");
            var data = PackageManagerPackageStatus.ReadStatus(root, CancellationToken.None);
            Assert.That(data.Error, Is.Empty);
            return data;
        }

        private void WriteRegistration()
        {
            File.WriteAllText(Path.Combine(root, ".gitmodules"), "[submodule \"example\"]\npath = " + RelativePath +
                "\nurl = https://github.com/example/package.git\n");
        }

        private void RunGit(string arguments, string workingDirectory = null, string input = null) =>
            RunCommand("git", arguments, workingDirectory, input);

        private void RunCommand(string executable, string arguments, string workingDirectory = null, string input = null)
        {
            int exit = ExecuteCommand(executable, arguments, out string error, workingDirectory, input);
            Assert.That(exit, Is.Zero, error);
        }

        private int ExecuteCommand(string executable, string arguments, out string error,
            string workingDirectory = null, string input = null)
        {
            using (var process = Process.Start(new ProcessStartInfo(executable, arguments)
            {
                WorkingDirectory = workingDirectory ?? root, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = input != null
            }))
            {
                if (input != null)
                {
                    process.StandardInput.Write(input);
                    process.StandardInput.Close();
                }
                Assert.That(process.WaitForExit(5000), Is.True);
                error = process.StandardError.ReadToEnd();
                return process.ExitCode;
            }
        }

        private bool Block(PackageManagerPackageStatus.StatusSnapshot data)
        {
            if (data.FilesStamp == null && string.IsNullOrEmpty(data.Error))
            {
                var current = ReadOrdinaryRepository();
                data.IndexPath = current.IndexPath;
                data.FilesStamp = current.FilesStamp;
            }
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
