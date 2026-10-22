using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

namespace MartinCalander.GitSubmoduleManager.Editor.Tests
{
    [Category("SubmodulePackageOperations")]
    public sealed class SubmodulePackageOperationsTests
    {
        private const string Name = "com.example.package";
        private const string RelativePath = "Packages/" + Name;
        private const string PackageJson = "{\"name\":\"" + Name + "\",\"version\":\"1.2.3\",\"dependencies\":{\"com.example.dependency\":\"2.0.0\"}}";
        private const string ManifestJson = "{\n  \"enableLockFile\": false,\n  \"dependencies\": {\n    \"com.example.keep\": \"1.0.0\"\n  }\n}\n";
        private string sandbox;
        private string project;
        private string source;
        private string remote;
        private string packagePath;
        private string manifestPath;
        private SubmodulePackageOperations operations;

        [SetUp]
        public void SetUp()
        {
            string temporary = Path.GetTempPath();
            if (Path.DirectorySeparatorChar != '\\')
                temporary = Run(temporary, "pwd", "-P").Trim();
            sandbox = Path.Combine(temporary, "submodule package tests " + Guid.NewGuid().ToString("N"));
            project = Path.Combine(sandbox, "project");
            source = Path.Combine(sandbox, "source");
            remote = Path.Combine(sandbox, "remote.git").Replace('\\', '/');
            packagePath = Path.Combine(project, "Packages", Name);
            manifestPath = Path.Combine(project, "Packages", "manifest.json");
            Directory.CreateDirectory(Path.GetDirectoryName(manifestPath));
            Directory.CreateDirectory(source);
            Init(project);
            File.WriteAllText(manifestPath, ManifestJson);
            CommitAll(project);
            Init(source);
            File.WriteAllText(Path.Combine(source, "package.json"), PackageJson);
            File.WriteAllText(Path.Combine(source, "content.txt"), "Published package content\n");
            CommitAll(source);
            Git(sandbox, "clone", "--quiet", "--bare", source, remote);
            operations = new SubmodulePackageOperations(project, allowLocalSources: true);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(sandbox))
                Directory.Delete(sandbox, true);
        }

        [Test]
        public void InstallCreatesRootPackageRegistrationAndGitlinkWithoutManifestDependency()
        {
            string originalManifest = File.ReadAllText(manifestPath);
            string name = operations.Install(remote);

            Assert.That(name, Is.EqualTo(Name));
            Assert.That(File.ReadAllText(Path.Combine(packagePath, "package.json")), Is.EqualTo(PackageJson));
            Assert.That(Git(project, "ls-files", "--stage", "--", RelativePath), Does.StartWith("160000 "));
            Assert.That(Git(project, "config", "--file", ".gitmodules", "--get-regexp", "^submodule\\..*\\.path$"),
                Does.Contain(RelativePath));
            Assert.That(Git(packagePath, "rev-parse", "HEAD"), Is.EqualTo(Git(source, "rev-parse", "HEAD")));
            Assert.That(File.ReadAllText(manifestPath), Is.EqualTo(originalManifest));
        }

        [Test]
        public void InstallUsesTheLatestCommitOnTheRequestedBranch()
        {
            Git(source, "checkout", "--quiet", "-b", "upm");
            File.WriteAllText(Path.Combine(source, "content.txt"), "New distribution branch content\n");
            CommitAll(source);
            Git(source, "push", "--quiet", remote, "upm");

            operations.Install(remote + "#upm");

            Assert.That(Git(packagePath, "rev-parse", "HEAD"), Is.EqualTo(Git(source, "rev-parse", "HEAD")));
            Assert.That(File.ReadAllText(Path.Combine(packagePath, "content.txt")), Does.Contain("New distribution"));
            Assert.That(File.ReadAllText(Path.Combine(project, ".gitmodules")), Does.Contain("upm"));
        }

        [TestCase("{not json}")]
        [TestCase("{}")]
        [TestCase("{\"name\":\"com.example.package\",\"name\":\"com.example.other\",\"version\":\"1.0.0\"}")]
        [TestCase("{\"name\":\"com.example.package\",\"version\":\"1.0.0\"} trailing data")]
        [TestCase("{\"name\":\"../outside\",\"version\":\"1.0.0\"}")]
        [TestCase("{\"name\":\"com.Example.package\",\"version\":\"1.0.0\"}")]
        [TestCase("{\"name\":\"package\",\"version\":\"1.0.0\"}")]
        [TestCase("{\"name\":\"com.example.package\",\"version\":\"latest\"}")]
        [TestCase("{\"name\":\"com.example.package\",\"version\":\"1.0.0-01\"}")]
        [TestCase("{\"name\":\"com.example.package\",\"version\":\"1.0.0\",\"dependencies\":{\"com.example.other\":\"file:../other\"}}")]
        [TestCase("{\"name\":\"com.example.package\",\"version\":\"1.0.0\",\"dependencies\":{\"com.example.other\":\"https://example.com/other.git\"}}")]
        [TestCase("{\"name\":\"com.example.package\",\"version\":\"1.0.0\",\"dependencies\":[]}")]
        [TestCase("{\"name\":\"com.example.package\",\"version\":\"1.0.0\",\"dependencies\":{\"com.example.other\":42}}")]
        [TestCase("{\"name\":\"com.example.package\",\"version\":\"1.0.0\",\"dependencies\":{\"../outside\":\"1.0.0\"}}")]
        public void InvalidPackageMetadataDoesNotChangeProjectRegistration(string json)
        {
            File.WriteAllText(Path.Combine(source, "package.json"), json);
            PublishSource();
            byte[] indexBefore = File.ReadAllBytes(Path.Combine(project, ".git", "index"));

            Assert.Catch(() => operations.Install(remote));

            Assert.That(Directory.Exists(packagePath), Is.False);
            Assert.That(File.Exists(Path.Combine(project, ".gitmodules")), Is.False);
            Assert.That(File.ReadAllBytes(Path.Combine(project, ".git", "index")), Is.EqualTo(indexBefore));
            Assert.That(File.ReadAllText(manifestPath), Is.EqualTo(ManifestJson));
        }

        [TestCase("oversize")]
        [TestCase("invalid-utf8")]
        [TestCase("utf16")]
        public void InvalidManifestEncodingOrSizeIsRejectedBeforeRegistration(string kind)
        {
            string path = Path.Combine(source, "package.json");
            if (kind == "oversize")
                File.WriteAllText(path, PackageJson.Substring(0, PackageJson.Length - 1) + ",\"description\":\"" + new string('x', 1024 * 1024) + "\"}");
            else if (kind == "invalid-utf8")
                File.WriteAllBytes(path, new byte[] { 0x7b, 0x22, 0xff, 0x22, 0x3a, 0x30, 0x7d });
            else
                File.WriteAllBytes(path, Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(PackageJson)).ToArray());
            PublishSource();
            byte[] indexBefore = File.ReadAllBytes(Path.Combine(project, ".git", "index"));

            Assert.Catch(() => operations.Install(remote));

            Assert.That(Directory.Exists(packagePath), Is.False);
            Assert.That(File.Exists(Path.Combine(project, ".gitmodules")), Is.False);
            Assert.That(File.ReadAllBytes(Path.Combine(project, ".git", "index")), Is.EqualTo(indexBefore));
            Assert.That(File.ReadAllText(manifestPath), Is.EqualTo(ManifestJson));
        }

        [Test]
        public void MetadataAcceptsSemanticPrereleaseVersionsButRejectsExcessiveJsonNesting()
        {
            string prerelease = PackageJson.Replace("1.2.3", "1.2.3-preview.2+build.04").Replace("2.0.0", "2.0.0-rc.1");
            Assert.That(new PackageManifestDocument(prerelease).ValidatePackage(), Is.EqualTo(Name));
            string deep = "{\"extra\":" + new string('[', 70) + "0" + new string(']', 70) + "}";
            Assert.Throws<InvalidDataException>(() => new PackageManifestDocument(deep));
        }

        [TestCase("{\"dependencies\":{\"com.example.package\":\"1.0.0\"},\"other\":true}",
            "{\"dependencies\":{},\"other\":true}")]
        [TestCase("{\"dependencies\":{\"com.example.package\":\"1.0.0\",\"com.example.keep\":\"2.0.0\"},\"other\":true}",
            "{\"dependencies\":{\"com.example.keep\":\"2.0.0\"},\"other\":true}")]
        [TestCase("{\"dependencies\":{\"com.example.keep\":\"2.0.0\",\"com.example.package\":\"1.0.0\"},\"other\":true}",
            "{\"dependencies\":{\"com.example.keep\":\"2.0.0\"},\"other\":true}")]
        [TestCase("{\"dependencies\":{\"com.example.first\":\"2.0.0\",\"com.example.package\":\"1.0.0\",\"com.example.last\":\"3.0.0\"},\"other\":true}",
            "{\"dependencies\":{\"com.example.first\":\"2.0.0\",\"com.example.last\":\"3.0.0\"},\"other\":true}")]
        public void RemovingDependencyKeepsValidJsonAndUnrelatedFields(string before, string expected)
        {
            string after = new PackageManifestDocument(before).WithoutDependency(Name);

            Assert.That(after, Is.EqualTo(expected));
            Assert.That(new PackageManifestDocument(after).HasDependency(Name), Is.False);
        }

        [Test]
        public void RemovingDependencyPreservesLineEndingsEscapesAndUnrelatedSettings()
        {
            string removedLine = "    \"" + Name + "\": \"file:../package\",\r\n";
            string before = "{\r\n  \"dependencies\": {\r\n" + removedLine +
                "    \"com.example.keep\": \"https://example.com/repository.git#main\"\r\n  },\r\n" +
                "  \"testables\": [\"" + Name + "\"],\r\n  \"note\": \"Unicode å and \\\"escaped\\\" text\",\r\n" +
                "  \"scopedRegistries\": [{\"name\":\"Example\",\"url\":\"https://example.com\",\"scopes\":[\"com.example\"]}]\r\n}\r\n";

            string after = new PackageManifestDocument(before).WithoutDependency(Name);

            Assert.That(after, Is.EqualTo(before.Replace(removedLine, string.Empty)));
            Assert.That(new PackageManifestDocument(after).HasDependency("com.example.keep"), Is.True);
        }

        [Test]
        public void RepositoryWithOnlyANestedPackageIsRejected()
        {
            string nested = Path.Combine(source, "nested");
            Directory.CreateDirectory(nested);
            File.Move(Path.Combine(source, "package.json"), Path.Combine(nested, "package.json"));
            PublishSource();

            Assert.Catch(() => operations.Install(remote));

            Assert.That(Directory.Exists(packagePath), Is.False);
            Assert.That(File.Exists(Path.Combine(project, ".gitmodules")), Is.False);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void ExistingFolderOrManifestDependencyIsPreserved(bool manifestDependency)
        {
            if (manifestDependency)
                AddManifestDependency();
            else
            {
                Directory.CreateDirectory(packagePath);
                File.WriteAllText(Path.Combine(packagePath, "keep.txt"), "Existing local work");
            }
            string before = File.ReadAllText(manifestPath);

            Assert.Catch(() => operations.Install(remote));

            Assert.That(File.ReadAllText(manifestPath), Is.EqualTo(before));
            Assert.That(File.Exists(Path.Combine(project, ".gitmodules")), Is.False);
            if (!manifestDependency)
                Assert.That(File.ReadAllText(Path.Combine(packagePath, "keep.txt")), Is.EqualTo("Existing local work"));
        }

        [Test]
        public void InstalledPackageIdentityBlocksADuplicateWithoutAnEmbeddedFolderOrManifestEntry()
        {
            var service = new SubmodulePackageOperations(project, allowLocalSources: true, installedPackageNames: new[] { Name });

            Assert.Catch(() => service.Install(remote));

            Assert.That(Directory.Exists(packagePath), Is.False);
            Assert.That(File.Exists(Path.Combine(project, ".gitmodules")), Is.False);
            Assert.That(File.ReadAllText(manifestPath), Is.EqualTo(ManifestJson));
        }

        [Test]
        public void UnrelatedEmbeddedPackageDependencyErrorsDoNotBlockAValidNewPackage()
        {
            string other = Path.Combine(project, "Packages", "com.example.unrelated");
            Directory.CreateDirectory(other);
            string json = "{\"name\":\"com.example.unrelated\",\"version\":\"not-a-version\",\"dependencies\":[]}";
            File.WriteAllText(Path.Combine(other, "package.json"), json);

            Assert.That(operations.Install(remote), Is.EqualTo(Name));

            Assert.That(File.ReadAllText(Path.Combine(other, "package.json")), Is.EqualTo(json));
            Assert.That(Git(project, "ls-files", "--stage", "--", RelativePath), Does.StartWith("160000 "));
        }

        [TestCase("missing-branch")]
        [TestCase("release-tag")]
        public void InstallRefusesMissingBranchesAndTagPinsWithoutRegistration(string revision)
        {
            Git(source, "tag", "release-tag");
            Git(source, "push", "--quiet", remote, "refs/tags/release-tag");

            Assert.Catch(() => operations.Install(remote + "#" + revision));

            Assert.That(Directory.Exists(packagePath), Is.False);
            Assert.That(File.Exists(Path.Combine(project, ".gitmodules")), Is.False);
        }

        [Test]
        public void ProductionSourceValidationRejectsLocalPathsBeforeMutation()
        {
            Assert.Catch(() => new SubmodulePackageOperations(project).Install(remote));
            Assert.That(Directory.Exists(packagePath), Is.False);
            Assert.That(File.Exists(Path.Combine(project, ".gitmodules")), Is.False);
        }

        [TestCase("-git@example.com:repo")]
        [TestCase("git@-example.com:repo")]
        [TestCase("ssh://-example.com/repo")]
        [TestCase("ssh://-git@example.com/repo")]
        public void SourceValidationRejectsOptionShapedSshEndpointsBeforeClone(string value)
        {
            Exception failure = Assert.Catch(() => new SubmodulePackageOperations(project).Install(value));

            Assert.That(failure.Message, Does.Contain("HTTPS"));
            Assert.That(Directory.Exists(Path.Combine(project, "Library", "SubmodulePackageOperations")), Is.False);
            Assert.That(File.Exists(Path.Combine(project, ".gitmodules")), Is.False);
        }

        [Test]
        public void CancelledInstallDoesNotChangeProjectRegistration()
        {
            using (var cancellation = new CancellationTokenSource())
            {
                cancellation.Cancel();
                Assert.Catch(() => operations.Install(remote, cancellation.Token));
            }
            Assert.That(Directory.Exists(packagePath), Is.False);
            Assert.That(File.Exists(Path.Combine(project, ".gitmodules")), Is.False);
            Assert.That(File.ReadAllText(manifestPath), Is.EqualTo(ManifestJson));
        }

        [Test]
        public void FailedInstallRetainsItsCheckoutWithoutOverwritingItOnRetry()
        {
            var failing = new SubmodulePackageOperations(project, allowLocalSources: true, beforeStep: step =>
            {
                if (step == "install.before-registration")
                    throw new IOException("Simulated registration failure");
            });

            Exception failure = Assert.Catch(() => failing.Install(remote));

            Assert.That(failure.Message, Does.Contain(packagePath));
            Assert.That(File.ReadAllText(Path.Combine(packagePath, "package.json")), Is.EqualTo(PackageJson));
            Assert.That(Git(packagePath, "rev-parse", "HEAD"), Is.EqualTo(Git(source, "rev-parse", "HEAD")));
            Assert.That(Git(project, "ls-files", "--stage", "--", RelativePath), Is.Empty);
            Assert.Catch(() => operations.Install(remote));
            Assert.That(File.ReadAllText(Path.Combine(packagePath, "package.json")), Is.EqualTo(PackageJson));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void RemoveDeletesThePackageRegistrationAndGitlinkAndAnyFallbackDependency(bool fallbackDependency)
        {
            InstallAndCommit();
            if (fallbackDependency)
            {
                AddManifestDependency();
                File.WriteAllText(manifestPath, File.ReadAllText(manifestPath), new UTF8Encoding(true));
                CommitAll(project);
            }
            byte[] manifestBefore = File.ReadAllBytes(manifestPath);
            byte[] expectedManifest = (fallbackDependency ? new UTF8Encoding(true).GetPreamble() : Array.Empty<byte>())
                .Concat(Encoding.UTF8.GetBytes(ManifestJson)).ToArray();
            string gitDirectory = Git(packagePath, "rev-parse", "--absolute-git-dir").Trim();
            string head = Git(packagePath, "rev-parse", "HEAD").Trim();
            File.WriteAllText(Path.Combine(project, "unrelated.txt"), "Keep this unrelated work");

            operations.ValidateRemoval(Name, packagePath);
            operations.Remove(Name, packagePath);

            Assert.That(Directory.Exists(packagePath), Is.False);
            Assert.That(Git(project, "ls-files", "--stage", "--", RelativePath), Is.Empty);
            string modules = Path.Combine(project, ".gitmodules");
            Assert.That(File.Exists(modules) ? File.ReadAllText(modules) : string.Empty, Does.Not.Contain(RelativePath));
            Assert.That(File.ReadAllText(manifestPath), Does.Not.Contain("\"" + Name + "\""));
            Assert.That(File.ReadAllText(manifestPath), Does.Contain("\"com.example.keep\": \"1.0.0\""));
            Assert.That(File.ReadAllBytes(manifestPath), Is.EqualTo(expectedManifest), "Manifest formatting and its UTF-8 BOM must be preserved.");
            Assert.That(File.ReadAllText(Path.Combine(project, "unrelated.txt")), Is.EqualTo("Keep this unrelated work"));
            Assert.That(Directory.Exists(gitDirectory), Is.True, "Git history remains available for recovery.");
            Assert.That(Git(project, "--git-dir", gitDirectory, "--work-tree", project, "cat-file", "-t", head).Trim(), Is.EqualTo("commit"));
            string[] backups = Directory.GetDirectories(Path.Combine(project, "Library", "SubmodulePackageOperations"));
            Assert.That(backups, Has.Length.EqualTo(1));
            Assert.That(File.ReadAllBytes(Path.Combine(backups[0], "checkout", "package.json")), Is.EqualTo(Encoding.UTF8.GetBytes(PackageJson)));
            Assert.That(File.ReadAllText(Path.Combine(backups[0], ".gitmodules.before")), Does.Contain(RelativePath));
            Assert.That(File.ReadAllBytes(Path.Combine(backups[0], "manifest.json.before")), Is.EqualTo(manifestBefore));
        }

        [Test]
        public void RemovedPackageCanBeInstalledAgainWhileRetainingItsPreviousGitHistory()
        {
            InstallAndCommit();
            string previousGitDirectory = Git(packagePath, "rev-parse", "--absolute-git-dir").Trim();
            operations.Remove(Name, packagePath);

            Assert.That(operations.Install(remote), Is.EqualTo(Name));

            Assert.That(Git(project, "ls-files", "--stage", "--", RelativePath), Does.StartWith("160000 "));
            Assert.That(Directory.Exists(previousGitDirectory), Is.True);
            Assert.That(Git(packagePath, "rev-parse", "--absolute-git-dir").Trim(), Is.Not.EqualTo(previousGitDirectory));
            operations.ValidateRemoval(Name, packagePath);
        }

        [Test]
        public void PackageCanBeRemovedBeforeItsInstallationIsCommittedInTheParent()
        {
            Assert.That(operations.Install(remote), Is.EqualTo(Name));
            File.WriteAllText(Path.Combine(project, "unrelated.txt"), "Preserve this staged change");
            Git(project, "add", "--", "unrelated.txt");
            string unrelatedIndex = Git(project, "ls-files", "--stage", "--", "unrelated.txt");

            operations.Remove(Name, packagePath);

            Assert.That(Directory.Exists(packagePath), Is.False);
            Assert.That(Git(project, "ls-files", "--stage", "--", RelativePath), Is.Empty);
            string modules = Path.Combine(project, ".gitmodules");
            Assert.That(File.Exists(modules) ? File.ReadAllText(modules) : string.Empty, Does.Not.Contain(RelativePath));
            Assert.That(Git(project, "ls-files", "--stage", "--", "unrelated.txt"), Is.EqualTo(unrelatedIndex));
            Assert.That(File.ReadAllText(Path.Combine(project, "unrelated.txt")), Is.EqualTo("Preserve this staged change"));
        }

        [Test]
        public void PackageCanBeRemovedAfterStagingAPublishedRevisionWithoutCommittingTheParent()
        {
            InstallAndCommit();
            File.WriteAllText(Path.Combine(source, "content.txt"), "Published updated content\n");
            PublishSource();
            Git(packagePath, "fetch", "--quiet", "origin");
            Git(packagePath, "merge", "--ff-only", "origin/main");
            Git(project, "add", "--", RelativePath);

            operations.Remove(Name, packagePath);

            Assert.That(Directory.Exists(packagePath), Is.False);
            Assert.That(Git(project, "ls-files", "--stage", "--", RelativePath), Is.Empty);
        }

        [TestCase("remove.before-git-rm", true)]
        [TestCase("remove.after-git-rm", false)]
        public void FailedRemovalPreservesRecoveryEvidenceAndKeepsFallbackDependencyConsistent(string failedStep, bool checkoutRemains)
        {
            InstallAndCommit();
            AddManifestDependency();
            CommitAll(project);
            string manifestBefore = File.ReadAllText(manifestPath);
            string gitDirectory = Git(packagePath, "rev-parse", "--absolute-git-dir").Trim();
            string head = Git(packagePath, "rev-parse", "HEAD").Trim();
            var failing = new SubmodulePackageOperations(project, allowLocalSources: true, beforeStep: step =>
            {
                if (step == failedStep)
                    throw new IOException("Simulated removal failure");
            });

            Exception failure = Assert.Catch(() => failing.Remove(Name, packagePath));

            Assert.That(failure.Message, Does.Contain("Recovery"));
            Assert.That(Directory.Exists(packagePath), Is.EqualTo(checkoutRemains));
            if (checkoutRemains)
                Assert.That(File.ReadAllText(manifestPath), Is.EqualTo(manifestBefore));
            else
                Assert.That(File.ReadAllText(manifestPath), Does.Not.Contain("\"" + Name + "\""));
            string[] backups = Directory.GetFiles(Path.Combine(project, "Library", "SubmodulePackageOperations"),
                "manifest.json.before", SearchOption.AllDirectories);
            Assert.That(backups, Has.Length.EqualTo(1));
            Assert.That(File.ReadAllText(backups[0]), Is.EqualTo(manifestBefore));
            Assert.That(Git(project, "--git-dir", gitDirectory, "--work-tree", project, "cat-file", "-t", head).Trim(), Is.EqualTo("commit"));
        }

        [TestCase("manifest")]
        [TestCase("modules")]
        [TestCase("gitlink")]
        [TestCase("checkout")]
        public void ConcurrentEditsArePreservedAndStopRemovalBeforeTheCheckoutMoves(string changed)
        {
            InstallAndCommit();
            AddManifestDependency();
            CommitAll(project);
            string originalManifest = File.ReadAllText(manifestPath);
            string originalIndex = Git(project, "ls-files", "--stage", "--", RelativePath);
            string externalManifest = null;
            string externalModules = null;
            string externalIndex = null;
            bool injected = false;
            var service = new SubmodulePackageOperations(project, allowLocalSources: true, beforeStep: step =>
            {
                string boundary = changed == "manifest" ? "remove.before-manifest-write" : "remove.before-git-rm";
                if (step != boundary || injected)
                    return;
                injected = true;
                if (changed == "manifest")
                {
                    externalManifest = originalManifest.Replace("\"com.example.keep\"", "\"com.example.external\": \"2.0.0\",\n    \"com.example.keep\"");
                    File.WriteAllText(manifestPath, externalManifest);
                }
                else if (changed == "modules")
                {
                    string path = Path.Combine(project, ".gitmodules");
                    File.AppendAllText(path, "\n# Concurrent registration edit\n");
                    externalModules = File.ReadAllText(path);
                }
                else if (changed == "gitlink")
                {
                    Git(project, "update-index", "--cacheinfo", "160000," + new string('a', 40) + "," + RelativePath);
                    externalIndex = Git(project, "ls-files", "--stage", "--", RelativePath);
                }
                else
                    File.WriteAllText(Path.Combine(packagePath, "late.txt"), "Work created after validation");
            });

            Assert.Catch(() => service.Remove(Name, packagePath));

            Assert.That(injected, Is.True);
            Assert.That(Directory.Exists(packagePath), Is.True);
            Assert.That(File.ReadAllText(manifestPath), Is.EqualTo(externalManifest ?? originalManifest));
            Assert.That(Git(project, "ls-files", "--stage", "--", RelativePath), Is.EqualTo(externalIndex ?? originalIndex));
            if (externalModules != null)
                Assert.That(File.ReadAllText(Path.Combine(project, ".gitmodules")), Is.EqualTo(externalModules));
            if (changed == "checkout")
                Assert.That(File.ReadAllText(Path.Combine(packagePath, "late.txt")), Is.EqualTo("Work created after validation"));
        }

        [TestCase("remove.before-manifest-write", true)]
        [TestCase("remove.before-git-rm", true)]
        [TestCase("remove.after-quarantine", false)]
        public void CancellationAtMutationBoundariesPreservesCheckoutDataAndParentGitlink(string boundary, bool originalCheckoutRemains)
        {
            InstallAndCommit();
            AddManifestDependency();
            CommitAll(project);
            string originalManifest = File.ReadAllText(manifestPath);
            string originalIndex = Git(project, "ls-files", "--stage", "--", RelativePath);
            using (var cancellation = new CancellationTokenSource())
            {
                var service = new SubmodulePackageOperations(project, allowLocalSources: true, beforeStep: step =>
                {
                    if (step == boundary)
                        cancellation.Cancel();
                });

                Assert.Catch(() => service.Remove(Name, packagePath, cancellation.Token));

                Assert.That(cancellation.IsCancellationRequested, Is.True);
            }
            Assert.That(Directory.Exists(packagePath), Is.EqualTo(originalCheckoutRemains));
            Assert.That(Git(project, "ls-files", "--stage", "--", RelativePath), Is.EqualTo(originalIndex));
            if (originalCheckoutRemains)
            {
                Assert.That(File.ReadAllText(Path.Combine(packagePath, "package.json")), Is.EqualTo(PackageJson));
                Assert.That(File.ReadAllText(manifestPath), Is.EqualTo(originalManifest));
            }
            else
            {
                string[] preserved = Directory.GetFiles(Path.Combine(project, "Library", "SubmodulePackageOperations"), "package.json", SearchOption.AllDirectories);
                Assert.That(preserved, Has.Length.EqualTo(1));
                Assert.That(File.ReadAllText(preserved[0]), Is.EqualTo(PackageJson));
            }
        }

        [Test]
        public void CancellationAfterAnInstallCheckoutMovesDoesNotRegisterOrDeleteIt()
        {
            using (var cancellation = new CancellationTokenSource())
            {
                var service = new SubmodulePackageOperations(project, allowLocalSources: true, beforeStep: step =>
                {
                    if (step == "install.before-registration")
                        cancellation.Cancel();
                });

                Assert.Catch(() => service.Install(remote, cancellation.Token));

                Assert.That(cancellation.IsCancellationRequested, Is.True);
            }
            Assert.That(File.ReadAllText(Path.Combine(packagePath, "package.json")), Is.EqualTo(PackageJson));
            Assert.That(Git(project, "ls-files", "--stage", "--", RelativePath), Is.Empty);
            Assert.That(File.Exists(Path.Combine(project, ".gitmodules")), Is.False);
        }

        [TestCase("tracked")]
        [TestCase("untracked")]
        [TestCase("ignored")]
        public void RemovalRefusesLocalFilesWithoutChangingThePackageOrParent(string kind)
        {
            File.WriteAllText(Path.Combine(source, ".gitignore"), "ignored.txt\n");
            PublishSource();
            InstallAndCommit();
            string file = Path.Combine(packagePath, kind == "tracked" ? "content.txt" : kind + ".txt");
            File.WriteAllText(file, "Local work must survive");
            string modulesBefore = File.ReadAllText(Path.Combine(project, ".gitmodules"));
            byte[] indexBefore = File.ReadAllBytes(Path.Combine(project, ".git", "index"));

            Assert.Catch(() => operations.Remove(Name, packagePath));

            Assert.That(File.ReadAllText(file), Is.EqualTo("Local work must survive"));
            Assert.That(File.ReadAllText(Path.Combine(project, ".gitmodules")), Is.EqualTo(modulesBefore));
            Assert.That(File.ReadAllBytes(Path.Combine(project, ".git", "index")), Is.EqualTo(indexBefore));
        }

        [TestCase("--assume-unchanged")]
        [TestCase("--skip-worktree")]
        public void RemovalRefusesIndexFlagsThatHideModifiedWorkingFiles(string flag)
        {
            InstallAndCommit();
            Git(packagePath, "update-index", flag, "--", "content.txt");
            string file = Path.Combine(packagePath, "content.txt");
            File.WriteAllText(file, "Hidden local work must survive");
            Assert.That(Git(packagePath, "status", "--porcelain=v1"), Is.Empty,
                "The fixture must reproduce edits hidden from ordinary Git status.");

            Assert.Catch(() => operations.Remove(Name, packagePath));

            Assert.That(File.ReadAllText(file), Is.EqualTo("Hidden local work must survive"));
            Assert.That(Git(project, "ls-files", "--stage", "--", RelativePath), Does.StartWith("160000 "));
        }

        [Test]
        public void RemovalRefusesARepositoryWithAnotherWorktree()
        {
            InstallAndCommit();
            string other = Path.Combine(sandbox, "other worktree");
            Git(packagePath, "worktree", "add", "--quiet", "--detach", other, "HEAD");

            Assert.Catch(() => operations.Remove(Name, packagePath));

            Assert.That(File.ReadAllText(Path.Combine(packagePath, "package.json")), Is.EqualTo(PackageJson));
            Assert.That(File.ReadAllText(Path.Combine(other, "package.json")), Is.EqualTo(PackageJson));
            Assert.That(Git(project, "ls-files", "--stage", "--", RelativePath), Does.StartWith("160000 "));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void RemovalRefusesUnpublishedCommitsIncludingOtherLocalBranches(bool otherBranch)
        {
            InstallAndCommit();
            string published = Git(packagePath, "rev-parse", "HEAD").Trim();
            if (otherBranch)
                Git(packagePath, "checkout", "--quiet", "-b", "local-work");
            File.WriteAllText(Path.Combine(packagePath, "content.txt"), "Unpublished work\n");
            CommitAll(packagePath);
            string unpublished = Git(packagePath, "rev-parse", "HEAD").Trim();
            if (otherBranch)
                Git(packagePath, "checkout", "--quiet", "--detach", published);
            else
                Git(project, "add", "--", RelativePath);

            Assert.Catch(() => operations.Remove(Name, packagePath));

            Assert.That(Directory.Exists(packagePath), Is.True);
            Assert.That(Git(packagePath, "cat-file", "-t", unpublished).Trim(), Is.EqualTo("commit"));
            Assert.That(Git(project, "ls-files", "--stage", "--", RelativePath), Does.StartWith("160000 "));
        }

        [TestCase("stash")]
        [TestCase("tag")]
        public void RemovalRefusesUnpublishedHistoryOutsideLocalBranches(string kind)
        {
            InstallAndCommit();
            string published = Git(packagePath, "rev-parse", "HEAD").Trim();
            File.WriteAllText(Path.Combine(packagePath, "content.txt"), "Unpublished retained history\n");
            string reference;
            if (kind == "stash")
            {
                Git(packagePath, "-c", "user.name=Package Tests", "-c", "user.email=package-tests@example.invalid", "stash", "push", "--quiet");
                reference = "refs/stash";
            }
            else
            {
                CommitAll(packagePath);
                Git(packagePath, "tag", "local-history");
                Git(packagePath, "reset", "--hard", published);
                reference = "refs/tags/local-history";
            }
            string preserved = Git(packagePath, "rev-parse", reference).Trim();
            Assert.That(Git(packagePath, "status", "--porcelain=v1"), Is.Empty);

            Assert.Catch(() => operations.Remove(Name, packagePath));

            Assert.That(Git(packagePath, "rev-parse", reference).Trim(), Is.EqualTo(preserved));
            Assert.That(Git(project, "ls-files", "--stage", "--", RelativePath), Does.StartWith("160000 "));
        }

        [Test]
        public void RemovalDoesNotTrustRemoteTrackingRefsAfterTheRemoteBranchWasDeleted()
        {
            InstallAndCommit();
            string published = Git(packagePath, "rev-parse", "refs/remotes/origin/main").Trim();
            Git(sandbox, "--git-dir", remote, "update-ref", "-d", "refs/heads/main");

            Assert.Catch(() => operations.Remove(Name, packagePath));

            Assert.That(Directory.Exists(packagePath), Is.True);
            Assert.That(Git(packagePath, "rev-parse", "HEAD").Trim(), Is.EqualTo(published));
            Assert.That(Git(project, "ls-files", "--stage", "--", RelativePath), Does.StartWith("160000 "));
        }

        [Test]
        public void RemovalRefusesAnOriginThatDiffersFromItsRegisteredRepository()
        {
            InstallAndCommit();
            Git(packagePath, "remote", "set-url", "origin", source);

            Assert.Catch(() => operations.Remove(Name, packagePath));

            Assert.That(Git(packagePath, "remote", "get-url", "origin").Trim(), Is.EqualTo(source));
            Assert.That(File.ReadAllText(Path.Combine(packagePath, "package.json")), Is.EqualTo(PackageJson));
        }

        [Test]
        public void RemovalRefusesAnUnreachableRemoteAndPreservesTheCheckout()
        {
            InstallAndCommit();
            Directory.Move(remote, remote + ".unavailable");

            Assert.Catch(() => operations.Remove(Name, packagePath));

            Assert.That(File.ReadAllText(Path.Combine(packagePath, "package.json")), Is.EqualTo(PackageJson));
            Assert.That(Git(project, "ls-files", "--stage", "--", RelativePath), Does.StartWith("160000 "));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void RemovingOnePackagePreservesSiblingSubmoduleRegistrationAndCheckout(bool commitInstallations)
        {
            Assert.That(operations.Install(remote), Is.EqualTo(Name));
            if (commitInstallations)
                CommitAll(project);
            const string siblingName = "com.example.sibling";
            string siblingPath = "Packages/" + siblingName;
            File.WriteAllText(Path.Combine(source, "package.json"), PackageJson.Replace(Name, siblingName));
            CommitAll(source);
            string siblingRemote = Path.Combine(sandbox, "sibling.git").Replace('\\', '/');
            Git(sandbox, "clone", "--quiet", "--bare", source, siblingRemote);
            Assert.That(operations.Install(siblingRemote), Is.EqualTo(siblingName));
            if (commitInstallations)
                CommitAll(project);
            string siblingIndex = Git(project, "ls-files", "--stage", "--", siblingPath);

            operations.Remove(Name, packagePath);

            Assert.That(Git(project, "ls-files", "--stage", "--", siblingPath), Is.EqualTo(siblingIndex));
            Assert.That(File.ReadAllText(Path.Combine(project, ".gitmodules")), Does.Contain(siblingPath));
            Assert.That(File.ReadAllText(Path.Combine(project, siblingPath, "package.json")), Does.Contain(siblingName));
            Assert.That(Directory.Exists(packagePath), Is.False);
        }

        [Test]
        public void RemovalRefusesPackagesContainingNestedSubmodules()
        {
            Git(source, "submodule", "add", "--quiet", remote, "vendor/nested");
            PublishSource();
            Git(project, "submodule", "add", "--quiet", remote, RelativePath);
            CommitAll(project);

            Assert.Catch(() => operations.Remove(Name, packagePath));

            Assert.That(File.ReadAllText(Path.Combine(packagePath, "package.json")), Is.EqualTo(PackageJson));
            Assert.That(Git(packagePath, "ls-files", "--stage", "--", "vendor/nested"), Does.StartWith("160000 "));
            Assert.That(Git(project, "ls-files", "--stage", "--", RelativePath), Does.StartWith("160000 "));
        }

        [Test]
        public void InstallRefusesASymlinkedPackagesDirectoryWithoutWritingThroughIt()
        {
            if (Path.DirectorySeparatorChar == '\\')
                Assert.Ignore("This fixture uses the Unix ln command to create a directory symlink.");
            string packages = Path.Combine(project, "Packages");
            string target = Path.Combine(sandbox, "linked packages");
            Directory.Move(packages, target);
            Run(project, "ln", "-s", target, packages);
            try
            {
                Assert.Catch(() => operations.Install(remote));
                Assert.That(Directory.Exists(Path.Combine(target, Name)), Is.False);
                Assert.That(File.ReadAllText(Path.Combine(target, "manifest.json")), Is.EqualTo(ManifestJson));
                Assert.That(File.Exists(Path.Combine(project, ".gitmodules")), Is.False);
            }
            finally
            {
                File.Delete(packages);
            }
        }

        [TestCase("package-manifest")]
        [TestCase("project-manifest")]
        [TestCase("registration")]
        public void RemovalRefusesLinkedMetadataWithoutChangingItsTarget(string kind)
        {
            if (Path.DirectorySeparatorChar == '\\')
                Assert.Ignore("This fixture uses the Unix ln command to create a file symlink.");
            InstallAndCommit();
            string path = kind == "package-manifest" ? Path.Combine(packagePath, "package.json") :
                kind == "project-manifest" ? manifestPath : Path.Combine(project, ".gitmodules");
            string target = Path.Combine(sandbox, "preserved metadata");
            File.Move(path, target);
            byte[] before = File.ReadAllBytes(target);
            Run(project, "ln", "-s", target, path);
            try
            {
                Assert.Catch(() => operations.Remove(Name, packagePath));

                Assert.That(Directory.Exists(packagePath), Is.True);
                Assert.That(File.ReadAllBytes(target), Is.EqualTo(before));
                Assert.That(Git(project, "ls-files", "--stage", "--", RelativePath), Does.StartWith("160000 "));
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Test]
        public void InstallationDoesNotStageAnExistingUntrackedModulesFile()
        {
            string modules = Path.Combine(project, ".gitmodules");
            const string contents = "# Untracked local configuration\n[submodule \"other\"]\npath = Packages/com.example.other\nurl = https://example.com/other.git\n";
            File.WriteAllText(modules, contents);

            Assert.Catch(() => operations.Install(remote));

            Assert.That(File.ReadAllText(modules), Is.EqualTo(contents));
            Assert.That(Git(project, "ls-files", "--stage", "--", ".gitmodules"), Is.Empty);
            Assert.That(Directory.Exists(packagePath), Is.False);
        }

        [TestCase("--assume-unchanged")]
        [TestCase("--skip-worktree")]
        public void RemovalRefusesHiddenEditsToTheModulesFile(string flag)
        {
            InstallAndCommit();
            Git(project, "update-index", flag, "--", ".gitmodules");
            string modules = Path.Combine(project, ".gitmodules");
            File.AppendAllText(modules, "\n# Hidden local registration edit\n");
            string before = File.ReadAllText(modules);
            Assert.That(Git(project, "diff", "--", ".gitmodules"), Is.Empty);

            Assert.Catch(() => operations.Remove(Name, packagePath));

            Assert.That(File.ReadAllText(modules), Is.EqualTo(before));
            Assert.That(File.ReadAllText(Path.Combine(packagePath, "package.json")), Is.EqualTo(PackageJson));
        }

        [TestCase("name")]
        [TestCase("path")]
        [TestCase("registration")]
        [TestCase("gitlink")]
        public void RemovalRefusesMismatchedIdentityOrRegistration(string mismatch)
        {
            InstallAndCommit();
            string requestedName = mismatch == "name" ? "com.example.other" : Name;
            string requestedPath = mismatch == "path" ? Path.Combine(project, "Assets", Name) : packagePath;
            if (mismatch == "registration")
                File.Delete(Path.Combine(project, ".gitmodules"));
            if (mismatch == "gitlink")
                Git(project, "update-index", "--force-remove", "--", RelativePath);

            Assert.Catch(() => operations.Remove(requestedName, requestedPath));

            Assert.That(File.ReadAllText(Path.Combine(packagePath, "package.json")), Is.EqualTo(PackageJson));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void RemovalRefusesDuplicateRegistrationPathsIncludingCaseAliases(bool caseAlias)
        {
            InstallAndCommit();
            Git(project, "config", "--file", ".gitmodules", "submodule.alias.path", caseAlias ? RelativePath.ToUpperInvariant() : RelativePath);
            Git(project, "config", "--file", ".gitmodules", "submodule.alias.url", remote);
            Git(project, "add", "--", ".gitmodules");
            string modules = File.ReadAllText(Path.Combine(project, ".gitmodules"));

            Assert.Catch(() => operations.Remove(Name, packagePath));

            Assert.That(File.ReadAllText(Path.Combine(project, ".gitmodules")), Is.EqualTo(modules));
            Assert.That(File.ReadAllText(Path.Combine(packagePath, "package.json")), Is.EqualTo(PackageJson));
        }

        [Test]
        public void MultipleParentWorktreesPreventMutationsInEitherCheckout()
        {
            InstallAndCommit();
            string linked = Path.Combine(sandbox, "linked parent checkout");
            Git(project, "worktree", "add", "--quiet", "--detach", linked, "HEAD");
            string index = Git(project, "ls-files", "--stage");
            string modules = File.ReadAllText(Path.Combine(project, ".gitmodules"));
            string linkedIndex = Git(linked, "ls-files", "--stage");
            var linkedOperations = new SubmodulePackageOperations(linked, allowLocalSources: true);

            Assert.Catch(() => operations.Remove(Name, packagePath));
            Assert.Catch(() => linkedOperations.Install(remote));

            Assert.That(Git(project, "ls-files", "--stage"), Is.EqualTo(index));
            Assert.That(Git(linked, "ls-files", "--stage"), Is.EqualTo(linkedIndex));
            Assert.That(File.ReadAllText(Path.Combine(project, ".gitmodules")), Is.EqualTo(modules));
            Assert.That(File.ReadAllText(Path.Combine(linked, ".gitmodules")), Is.EqualTo(modules));
            Assert.That(File.ReadAllText(Path.Combine(packagePath, "package.json")), Is.EqualTo(PackageJson));
            Assert.That(File.ReadAllText(Path.Combine(linked, "Packages", "manifest.json")), Is.EqualTo(ManifestJson));
        }

        [Test]
        public void GitOutputLimitFailsPromptlyAndLeavesSubsequentCommandsUsable()
        {
            string large = Path.Combine(sandbox, "large blob.txt");
            File.WriteAllText(large, new string('x', 3 * 1024 * 1024));
            string hash = Git(project, "hash-object", "-w", large).Trim();
            var timer = Stopwatch.StartNew();

            Exception failure = Assert.Catch(() => InvokeGit(CancellationToken.None, "cat-file", "blob", hash));

            Assert.That(failure.ToString(), Does.Contain("output"));
            Assert.That(timer.Elapsed.TotalSeconds, Is.LessThan(10), "Oversize Git output must not wait for the command deadline.");
            Assert.That(InvokeGit(CancellationToken.None, "rev-parse", "--show-toplevel").Trim(), Is.EqualTo(project.Replace('\\', '/')));
        }

        [Test]
        public void CancellingGitStopsItsOwnedChildAndReleasesOutputReaders()
        {
            if (Path.DirectorySeparatorChar == '\\')
                Assert.Ignore("This fixture uses an owned Unix shell helper to create a Git child process.");
            string script = Path.Combine(sandbox, "owned child helper.sh");
            string pidFile = Path.Combine(sandbox, "owned child.pid");
            File.WriteAllText(script, "#!/bin/sh\nsleep 60 &\nchild=$!\nprintf '%s\\n' \"$child\" > \"$1\"\nwait \"$child\"\n");
            Git(project, "config", "alias.package-test-child", "!sh '" + script.Replace("'", "'\"'\"'") + "'");
            Process child = null;
            Task<string> running = null;
            using (var cancellation = new CancellationTokenSource())
            {
                try
                {
                    running = Task.Run(() => InvokeGit(cancellation.Token, "package-test-child", pidFile));
                    Assert.That(SpinWait.SpinUntil(() => File.Exists(pidFile) && new FileInfo(pidFile).Length > 0, 5000), Is.True,
                        "The owned Git child did not start.");
                    int pid = int.Parse(File.ReadAllText(pidFile).Trim());
                    child = Process.GetProcessById(pid);
                    Assert.That(child.HasExited, Is.False);
                    var timer = Stopwatch.StartNew();

                    cancellation.Cancel();

                    Assert.That(SpinWait.SpinUntil(() => running.IsCompleted, 5000), Is.True,
                        "Cancellation did not release the Git operation and its output readers.");
                    Assert.Catch<OperationCanceledException>(() => running.GetAwaiter().GetResult());
                    Assert.That(child.WaitForExit(2000), Is.True, "The owned Git child survived cancellation.");
                    Assert.That(timer.Elapsed.TotalSeconds, Is.LessThan(7));
                    Assert.That(InvokeGit(CancellationToken.None, "rev-parse", "--show-toplevel").Trim(), Is.EqualTo(project.Replace('\\', '/')));
                }
                finally
                {
                    cancellation.Cancel();
                    if (child != null)
                    {
                        if (!child.HasExited)
                        {
                            child.Kill();
                            child.WaitForExit(2000);
                        }
                        child.Dispose();
                    }
                    if (running != null)
                    {
                        SpinWait.SpinUntil(() => running.IsCompleted, 2000);
                        if (running.IsFaulted)
                            _ = running.Exception;
                    }
                }
            }
        }

        [Test]
        public void DetachedGitHelperFailsWithinTheDeadlineWithAnUnverifiedCleanupError()
        {
            if (Path.DirectorySeparatorChar == '\\')
                Assert.Ignore("This fixture uses an owned Unix shell helper to retain a Git output pipe.");
            string script = Path.Combine(sandbox, "owned detached helper.sh");
            string pidFile = Path.Combine(sandbox, "owned detached.pid");
            File.WriteAllText(script, "#!/bin/sh\nsleep 60 &\nchild=$!\nprintf '%s\\n' \"$child\" > \"$1\"\nexit 0\n");
            Git(project, "config", "alias.package-test-detached", "!sh '" + script.Replace("'", "'\"'\"'") + "'");
            Process child = null;
            Task<string> running = null;
            using (var cancellation = new CancellationTokenSource())
            {
                try
                {
                    running = Task.Run(() => InvokeGit(cancellation.Token, "package-test-detached", pidFile));
                    Assert.That(SpinWait.SpinUntil(() => File.Exists(pidFile) && new FileInfo(pidFile).Length > 0, 5000), Is.True);
                    child = Process.GetProcessById(int.Parse(File.ReadAllText(pidFile).Trim()));

                    Assert.That(SpinWait.SpinUntil(() => running.IsCompleted, 5000), Is.True,
                        "An inherited pipe must not let an exited Git process exceed the output deadline.");
                    Exception failure = Assert.Catch(() => running.GetAwaiter().GetResult());
                    Assert.That(failure.Message, Does.Contain("cleanup could not be confirmed"));
                    Assert.That(failure.Message, Does.Contain("output pipes"));
                    Assert.That(InvokeGit(CancellationToken.None, "rev-parse", "--show-toplevel").Trim(), Is.EqualTo(project.Replace('\\', '/')));
                }
                finally
                {
                    cancellation.Cancel();
                    if (child != null)
                    {
                        if (!child.HasExited)
                        {
                            child.Kill();
                            child.WaitForExit(2000);
                        }
                        child.Dispose();
                    }
                    if (running != null)
                    {
                        SpinWait.SpinUntil(() => running.IsCompleted, 2000);
                        if (running.IsFaulted)
                            _ = running.Exception;
                    }
                }
            }
        }

        private string InvokeGit(CancellationToken token, params string[] arguments)
        {
            MethodInfo method = typeof(SubmodulePackageOperations).GetMethod("RunGit", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null);
            try
            {
                return (string)method.Invoke(operations, new object[] { project, token, false, arguments });
            }
            catch (TargetInvocationException exception) when (exception.InnerException != null)
            {
                ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
                throw;
            }
        }

        private void InstallAndCommit()
        {
            Assert.That(operations.Install(remote), Is.EqualTo(Name));
            CommitAll(project);
        }

        private void AddManifestDependency()
        {
            File.WriteAllText(manifestPath, ManifestJson.Replace("\"com.example.keep\"",
                "\"" + Name + "\": \"https://example.com/package.git\",\n    \"com.example.keep\""));
        }

        private void PublishSource()
        {
            CommitAll(source);
            Git(source, "push", "--quiet", remote, "main");
        }

        private static void Init(string path)
        {
            Git(path, "init", "--quiet", "-b", "main");
        }

        private static void CommitAll(string path)
        {
            Git(path, "add", "--all");
            Git(path, "-c", "user.name=Package Tests", "-c", "user.email=package-tests@example.invalid",
                "-c", "commit.gpgsign=false", "commit", "--quiet", "-m", "Prepare test repository");
        }

        private static string Git(string path, params string[] arguments)
        {
            return Run(path, "git", new[]
            {
                "-c", "core.hooksPath=" + Path.Combine(Path.GetTempPath(), "unused-package-test-hooks"),
                "-c", "protocol.file.allow=always"
            }.Concat(arguments).ToArray());
        }

        private static string Run(string path, string executable, params string[] arguments)
        {
            var start = new ProcessStartInfo(executable)
            {
                Arguments = string.Join(" ", arguments.Select(Quote)),
                WorkingDirectory = path,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            start.EnvironmentVariables["GIT_TERMINAL_PROMPT"] = "0";
            using (var process = Process.Start(start))
            {
                Task<string> output = Task.Run(() => process.StandardOutput.ReadToEnd());
                Task<string> error = Task.Run(() => process.StandardError.ReadToEnd());
                if (!process.WaitForExit(15000))
                {
                    process.Kill();
                    Assert.Fail("Test command timed out: " + executable + " " + string.Join(" ", arguments));
                }
                Assert.That(Task.WaitAll(new Task[] { output, error }, 5000), Is.True, "Test command output did not finish.");
                Assert.That(process.ExitCode, Is.Zero, error.GetAwaiter().GetResult());
                return output.GetAwaiter().GetResult();
            }
        }

        private static string Quote(string value)
        {
            var quoted = new StringBuilder("\"");
            int slashes = 0;
            foreach (char character in value)
            {
                if (character == '\\')
                {
                    slashes++;
                    continue;
                }
                quoted.Append('\\', character == '\"' ? slashes * 2 + 1 : slashes);
                quoted.Append(character);
                slashes = 0;
            }
            quoted.Append('\\', slashes * 2);
            return quoted.Append('\"').ToString();
        }
    }
}
