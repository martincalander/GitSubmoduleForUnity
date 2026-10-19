using System;
using System.Collections.Generic;
using System.IO;
using System.Diagnostics;
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

        [TestCase("https://github.com/example/package.git")]
        [TestCase("https://gitlab.com/example/package.git")]
        [TestCase("ssh://git@example.invalid/example/package.git")]
        public void SubmodulePageUsesCachedInstalledClassificationForEveryHost(string url)
        {
            var data = InstalledSnapshot();
            data.Repositories[RelativePath] = url;
            object native = new { versions = new { primary = new { name = Name, isInstalled = true, localPath = packagePath } } };

            Assert.That(PackageManagerPackageStatus.IsSubmodulePackage(root, native, data), Is.True);
            data.Repositories.Clear();
            Assert.That(PackageManagerPackageStatus.IsSubmodulePackage(root, native, data), Is.False);
        }

        [TestCase("not-installed")]
        [TestCase("unregistered")]
        [TestCase("different-path")]
        [TestCase("error")]
        [TestCase("malformed-native")]
        [TestCase("missing-native-path")]
        [TestCase("different-native-path")]
        public void SubmodulePageRejectsUnverifiedOrUninstalledPackages(string reason)
        {
            var data = InstalledSnapshot();
            object native = new { versions = new { primary = new { name = Name, isInstalled = reason != "not-installed",
                localPath = reason == "different-native-path" ? packagePath + "-other" : packagePath } } };
            if (reason == "unregistered") data.Packages.Clear();
            if (reason == "different-path") data.Packages[Name] = new PackageManagerPackageStatus.InstalledPackage(
                Path.Combine(root, "Library", "PackageCache", Name), PackageSource.Git, Name + "@https://github.com/example/package.git");
            if (reason == "error") data.Error = "Local Git status unavailable.";
            if (reason == "malformed-native") native = new object();
            if (reason == "missing-native-path") native = new { versions = new { primary = new { name = Name, isInstalled = true } } };

            Assert.That(PackageManagerPackageStatus.IsSubmodulePackage(root, native, data), Is.False);
        }

        [TestCase(false, "matching")]
        [TestCase(false, "missing")]
        [TestCase(false, "different")]
        [TestCase(true, "matching")]
        [TestCase(true, "missing")]
        [TestCase(true, "different")]
        public void GitHubPageRequiresTheNativeInstalledPathToMatchCachedRegistration(bool gitDependency, string nativePath)
        {
            var data = InstalledSnapshot();
            if (gitDependency)
            {
                data.Repositories.Clear();
                data.Gitlinks.Clear();
                data.Packages[Name] = new PackageManagerPackageStatus.InstalledPackage(packagePath, PackageSource.Git,
                    Name + "@https://github.com/example/package.git");
            }
            object native = new { versions = new { primary = new { name = Name, isInstalled = true,
                localPath = nativePath == "different" ? packagePath + "-other" : packagePath } } };
            if (nativePath == "missing") native = new { versions = new { primary = new { name = Name, isInstalled = true } } };

            Assert.That(PackageManagerPackageStatus.IsGitHubPackage(root, native, data), Is.EqualTo(nativePath == "matching"));
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
        public void UnreadableSubmoduleMetadataDoesNotExposeAnUnverifiedRepository()
        {
            var data = InstalledSnapshot();
            data.Error = "Submodule registrations are incomplete or duplicated.";

            Assert.That(PackageManagerPackageStatus.GetRepositoryUrl(root, NativeVersion(), data), Is.Empty);
            Assert.That(Block(data), Is.True);
        }

        [Test]
        public void RepositoryLinkUsesCachedInstalledGitSource()
        {
            string id = Name + "@https://github.com/example/package.git?path=/Packages/Child#release";
            var data = new PackageManagerPackageStatus.StatusSnapshot();
            data.Packages.Add(Name, new PackageManagerPackageStatus.InstalledPackage(packagePath, PackageSource.Git, id));

            Assert.That(PackageManagerPackageStatus.GetRepositoryUrl(root, NativeVersion(packagePath, id), data),
                Is.EqualTo("https://github.com/example/package"));
        }

        [TestCase("path")]
        [TestCase("package-id")]
        [TestCase("missing")]
        public void RepositoryLinkRejectsDifferentInstalledIdentity(string mismatch)
        {
            var data = InstalledSnapshot();
            if (mismatch == "missing") data.Packages.Clear();
            string path = mismatch == "path" ? packagePath + "-other" : packagePath;
            string id = Name + "@file:" + packagePath + (mismatch == "package-id" ? "-other" : "");

            Assert.That(PackageManagerPackageStatus.GetRepositoryUrl(root, NativeVersion(path, id), data), Is.Empty);
        }

        [Test]
        public void SubmoduleRepositoryRequiresItsRegistration()
        {
            var data = InstalledSnapshot();
            Assert.That(PackageManagerPackageStatus.GetRepositoryUrl(root, NativeVersion(), data),
                Is.EqualTo("https://github.com/example/package"));
            data.Repositories.Clear();
            Assert.That(PackageManagerPackageStatus.GetRepositoryUrl(root, NativeVersion(), data), Is.Empty);
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

        private void RunGit(string arguments, string workingDirectory = null)
        {
            using (var process = Process.Start(new ProcessStartInfo("git", arguments)
            {
                WorkingDirectory = workingDirectory ?? root, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true
            }))
            {
                Assert.That(process.WaitForExit(5000), Is.True);
                Assert.That(process.ExitCode, Is.Zero, process.StandardError.ReadToEnd());
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
