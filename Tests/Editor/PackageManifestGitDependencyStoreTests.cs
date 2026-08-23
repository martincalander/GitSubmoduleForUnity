using System;
using System.IO;
using System.Linq;
using System.Text;
using NUnit.Framework;

namespace MartinCalander.GitSubmoduleManager.Editor.Tests
{
    [Parallelizable(ParallelScope.None)]
    public sealed class PackageManifestGitDependencyStoreTests
    {
        private string temporaryDirectory;
        private string manifestPath;
        private string lockPath;

        [SetUp]
        public void SetUp()
        {
            PackageManifestGitDependencyStore.BeforeInitialAtomicReplaceForTests = null;
            PackageManifestGitDependencyStore.ResetReadCacheForTests();
            temporaryDirectory = Path.Combine(
                Path.GetTempPath(),
                "GitSubmoduleManagerManifestTests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temporaryDirectory);
            manifestPath = Path.Combine(temporaryDirectory, "manifest.json");
            lockPath = Path.Combine(temporaryDirectory, "packages-lock.json");
        }

        [TearDown]
        public void TearDown()
        {
            PackageManifestGitDependencyStore.BeforeInitialAtomicReplaceForTests = null;
            PackageManifestGitDependencyStore.ResetReadCacheForTests();
            if (!string.IsNullOrEmpty(temporaryDirectory) &&
                Directory.Exists(temporaryDirectory))
            {
                Directory.Delete(temporaryDirectory, true);
            }
        }

        [Test]
        public void ReadIndex_ReusesOneManifestParseAcrossPackageLookups_AndReloadsOnChange()
        {
            File.WriteAllText(
                manifestPath,
                "{\n  \"dependencies\": {\n" +
                "    \"com.example.first\": \"https://github.com/example/first.git#main\",\n" +
                "    \"com.example.second\": \"https://github.com/example/second.git#main\"\n" +
                "  }\n}\n",
                new UTF8Encoding(false));
            DateTime firstStamp = DateTime.UtcNow.AddMinutes(-10);
            File.SetLastWriteTimeUtc(manifestPath, firstStamp);

            Assert.That(
                PackageManifestGitDependencyStore.TryGetCachedDependencyAtPath(
                    manifestPath,
                    "com.example.first",
                    out PackageManifestGitDependency first,
                    out string error),
                Is.True,
                error);
            Assert.That(
                PackageManifestGitDependencyStore.TryGetCachedDependencyAtPath(
                    manifestPath,
                    "com.example.second",
                    out PackageManifestGitDependency second,
                    out error),
                Is.True,
                error);
            Assert.That(first.Revision, Is.EqualTo("main"));
            Assert.That(second.Revision, Is.EqualTo("main"));
            Assert.That(
                PackageManifestGitDependencyStore.ReadIndexBuildCountForTests,
                Is.EqualTo(1));

            File.WriteAllText(
                manifestPath,
                "{\n  \"dependencies\": {\n" +
                "    \"com.example.first\": \"https://github.com/example/first.git#next\",\n" +
                "    \"com.example.second\": \"https://github.com/example/second.git#main\"\n" +
                "  }\n}\n",
                new UTF8Encoding(false));
            File.SetLastWriteTimeUtc(manifestPath, firstStamp.AddMinutes(5));

            Assert.That(
                PackageManifestGitDependencyStore.TryGetCachedDependencyAtPath(
                    manifestPath,
                    "com.example.first",
                    out PackageManifestGitDependency updated,
                    out error),
                Is.True,
                error);
            Assert.That(updated.Revision, Is.EqualTo("next"));
            Assert.That(
                PackageManifestGitDependencyStore.ReadIndexBuildCountForTests,
                Is.EqualTo(2));
        }

        [Test]
        public void ReadIndex_TransientReadFailureIsRetriedWithoutManifestStampChange()
        {
            File.WriteAllText(
                manifestPath,
                "{\n  \"dependencies\": {\n" +
                "    \"com.example.retry\": \"https://github.com/example/retry.git#main\"\n" +
                "  }\n}\n",
                new UTF8Encoding(false));
            DateTime unchangedStamp = DateTime.UtcNow.AddMinutes(-10);
            File.SetLastWriteTimeUtc(manifestPath, unchangedStamp);

            int readAttempts = 0;
            PackageManifestGitDependencyStore.CachedReadFailureForTests = _ =>
                ++readAttempts == 1
                    ? "Packages/manifest.json could not be read safely: simulated sharing violation."
                    : string.Empty;

            Assert.That(
                PackageManifestGitDependencyStore.TryGetCachedDependencyAtPath(
                    manifestPath,
                    "com.example.retry",
                    out _,
                    out string error),
                Is.False);
            Assert.That(error, Does.Contain("simulated sharing violation"));
            Assert.That(
                PackageManifestGitDependencyStore.ReadIndexBuildCountForTests,
                Is.Zero,
                "A failed presentation read must not become a cached index.");

            Assert.That(
                PackageManifestGitDependencyStore.TryGetCachedDependencyAtPath(
                    manifestPath,
                    "com.example.retry",
                    out PackageManifestGitDependency dependency,
                    out error),
                Is.True,
                error);
            Assert.That(dependency.Revision, Is.EqualTo("main"));
            Assert.That(readAttempts, Is.EqualTo(2));
            Assert.That(
                PackageManifestGitDependencyStore.ReadIndexBuildCountForTests,
                Is.EqualTo(1));

            Assert.That(
                PackageManifestGitDependencyStore.TryGetCachedDependencyAtPath(
                    manifestPath,
                    "com.example.retry",
                    out _,
                    out error),
                Is.True,
                error);
            Assert.That(readAttempts, Is.EqualTo(2),
                "The successful retry should populate the presentation cache.");
        }

        [Test]
        public void ReadIndex_ManifestMutationInvalidatesCachedDependencySpecs()
        {
            File.WriteAllText(
                manifestPath,
                "{\n  \"dependencies\": {\n" +
                "    \"com.example.existing\": \"https://github.com/example/existing.git#main\"\n" +
                "  }\n}\n",
                new UTF8Encoding(false));

            Assert.That(
                PackageManifestGitDependencyStore.TryGetCachedDependencyAtPath(
                    manifestPath,
                    "com.example.existing",
                    out _,
                    out string error),
                Is.True,
                error);
            Assert.That(
                PackageManifestGitDependencyStore.ReadIndexBuildCountForTests,
                Is.EqualTo(1));

            Assert.That(
                PackageManifestGitDependencyStore.TryAddDependencyAtPath(
                    manifestPath,
                    "com.example.added",
                    "https://github.com/example/added.git#main",
                    out _,
                    out error),
                Is.True,
                error);
            Assert.That(
                PackageManifestGitDependencyStore.TryGetCachedDependencyAtPath(
                    manifestPath,
                    "com.example.added",
                    out PackageManifestGitDependency added,
                    out error),
                Is.True,
                error);
            Assert.That(added.RepositoryUrl, Does.EndWith("/added.git"));
            Assert.That(
                PackageManifestGitDependencyStore.ReadIndexBuildCountForTests,
                Is.EqualTo(2));
        }

        [Test]
        public void ReadIndex_DoesNotCoerceNonStringDependencyValues()
        {
            File.WriteAllText(
                manifestPath,
                "{\n  \"dependencies\": {\n" +
                "    \"com.example.invalid\": 42,\n" +
                "    \"com.example.valid\": \"https://github.com/example/valid.git#main\"\n" +
                "  }\n}\n",
                new UTF8Encoding(false));

            Assert.That(
                PackageManifestGitDependencyStore.TryGetDependencyAtPath(
                    manifestPath,
                    "com.example.invalid",
                    out _,
                    out string error),
                Is.False);
            Assert.That(
                error,
                Is.EqualTo(
                    "The direct dependency entry for com.example.invalid is not a string."));
            Assert.That(
                PackageManifestGitDependencyStore.TryGetDependencyAtPath(
                    manifestPath,
                    "com.example.valid",
                    out _,
                    out error),
                Is.False);
            Assert.That(
                error,
                Is.EqualTo(
                    "Every Packages/manifest.json dependency value must be a string."));

            Assert.That(
                PackageManifestGitDependencyStore.TryGetCachedDependencyAtPath(
                    manifestPath,
                    "com.example.invalid",
                    out _,
                    out error),
                Is.False);
            Assert.That(
                error,
                Is.EqualTo(
                    "The direct dependency entry for com.example.invalid is not a string."));

            Assert.That(
                PackageManifestGitDependencyStore.TryGetCachedDependencyAtPath(
                    manifestPath,
                    "com.example.valid",
                    out _,
                    out error),
                Is.False);
            Assert.That(
                error,
                Is.EqualTo(
                    "Every Packages/manifest.json dependency value must be a string."));
            Assert.That(
                PackageManifestGitDependencyStore.ReadIndexBuildCountForTests,
                Is.EqualTo(1));
        }

        [Test]
        public void Add_PreservesUtf8BomCrlfAndFinalNewline_WithoutEditingLockfile()
        {
            byte[] original = WithUtf8Bom(
                "{\r\n" +
                "  \"dependencies\": {\r\n" +
                "    \"com.example.existing\": \"1.0.0\"\r\n" +
                "  }\r\n" +
                "}\r\n");
            File.WriteAllBytes(manifestPath, original);
            byte[] lockSentinel = Encoding.UTF8.GetBytes("lockfile sentinel\n");
            File.WriteAllBytes(lockPath, lockSentinel);

            bool success = PackageManifestGitDependencyStore.TryAddDependencyAtPath(
                manifestPath,
                "com.example.readonly",
                "https://github.com/example/readonly.git#main",
                out PackageManifestDependencyMutation mutation,
                out string error);

            Assert.That(success, Is.True, error);
            Assert.That(mutation, Is.Not.Null);
            Assert.That(mutation.Changed, Is.True);
            byte[] updated = File.ReadAllBytes(manifestPath);
            Assert.That(updated.Take(3).ToArray(), Is.EqualTo(new byte[] { 0xEF, 0xBB, 0xBF }));
            string text = Encoding.UTF8.GetString(updated, 3, updated.Length - 3);
            Assert.That(text, Does.Contain("\r\n"));
            Assert.That(text.Replace("\r\n", string.Empty), Does.Not.Contain("\n"));
            Assert.That(text, Does.EndWith("\r\n"));
            Assert.That(File.ReadAllBytes(lockPath), Is.EqualTo(lockSentinel));

            Assert.That(
                PackageManifestGitDependencyStore.TryGetDependencyAtPath(
                    manifestPath,
                    "com.example.readonly",
                    out PackageManifestGitDependency dependency,
                    out error),
                Is.True,
                error);
            Assert.That(dependency.Spec, Is.EqualTo("https://github.com/example/readonly.git#main"));
            Assert.That(dependency.RepositoryUrl, Is.EqualTo("https://github.com/example/readonly.git"));
            Assert.That(dependency.Revision, Is.EqualTo("main"));
            Assert.That(dependency.PackageSubfolder, Is.Empty);
            Assert.That(dependency.IsRepositoryRootPackage, Is.True);
        }

        [Test]
        public void MutationRollback_RestoresExactOriginalBytes()
        {
            byte[] original = Encoding.UTF8.GetBytes(
                "{\n  \"dependencies\": {\n    \"com.example.keep\": \"2.0.0\"\n  }\n}\n");
            File.WriteAllBytes(manifestPath, original);

            Assert.That(
                PackageManifestGitDependencyStore.TryAddDependencyAtPath(
                    manifestPath,
                    "com.example.readonly",
                    "git@github.com:example/readonly.git#main",
                    out PackageManifestDependencyMutation mutation,
                    out string error),
                Is.True,
                error);

            Assert.That(mutation.TryRollback(out error), Is.True, error);
            Assert.That(File.ReadAllBytes(manifestPath), Is.EqualTo(original));
            Assert.That(mutation.TryRollback(out error), Is.True, error);
        }

        [Test]
        public void MutationRollback_RefusesToOverwriteAConcurrentManifestEdit()
        {
            File.WriteAllText(
                manifestPath,
                "{\n  \"dependencies\": {}\n}\n",
                new UTF8Encoding(false));
            Assert.That(
                PackageManifestGitDependencyStore.TryAddDependencyAtPath(
                    manifestPath,
                    "com.example.readonly",
                    "https://github.com/example/readonly.git#main",
                    out PackageManifestDependencyMutation mutation,
                    out string error),
                Is.True,
                error);

            byte[] concurrentBytes = File.ReadAllBytes(manifestPath)
                .Concat(new byte[] { (byte)' ' })
                .ToArray();
            File.WriteAllBytes(manifestPath, concurrentBytes);

            Assert.That(mutation.TryRollback(out error), Is.False);
            Assert.That(error, Does.Contain("changed"));
            Assert.That(File.ReadAllBytes(manifestPath), Is.EqualTo(concurrentBytes));
        }

        [Test]
        public void Add_WhenManifestChangesAtAtomicSwapBoundary_RestoresExternalEditAndFails()
        {
            byte[] original = Encoding.UTF8.GetBytes(
                "{\n  \"dependencies\": {\n    \"com.example.keep\": \"1.0.0\"\n  }\n}\n");
            byte[] externalEdit = Encoding.UTF8.GetBytes(
                "{\n  \"dependencies\": {\n" +
                "    \"com.example.keep\": \"1.0.0\",\n" +
                "    \"com.example.external\": \"2.0.0\"\n" +
                "  }\n}\n");
            File.WriteAllBytes(manifestPath, original);

            int hookCallCount = 0;
            PackageManifestGitDependencyStore.BeforeInitialAtomicReplaceForTests = path =>
            {
                hookCallCount++;
                Assert.That(path, Is.EqualTo(Path.GetFullPath(manifestPath)));
                File.WriteAllBytes(path, externalEdit);
            };

            bool success;
            PackageManifestDependencyMutation mutation;
            string error;
            try
            {
                success = PackageManifestGitDependencyStore.TryAddDependencyAtPath(
                    manifestPath,
                    "com.example.readonly",
                    "https://github.com/example/readonly.git#main",
                    out mutation,
                    out error);
            }
            finally
            {
                PackageManifestGitDependencyStore.BeforeInitialAtomicReplaceForTests = null;
            }

            Assert.That(success, Is.False);
            Assert.That(mutation, Is.Null);
            Assert.That(error, Does.Contain("changed"));
            Assert.That(error, Does.Contain("restored"));
            Assert.That(hookCallCount, Is.EqualTo(1));
            Assert.That(File.ReadAllBytes(manifestPath), Is.EqualTo(externalEdit));
            Assert.That(
                Directory.GetFiles(temporaryDirectory).Select(Path.GetFileName),
                Is.EquivalentTo(new[] { "manifest.json" }));
        }

        [Test]
        public void Remove_RequiresExactExpectedSpec_AndCanRollback()
        {
            byte[] original = Encoding.UTF8.GetBytes(
                "{\n" +
                "  \"dependencies\": {\n" +
                "    \"com.example.readonly\": \"https://github.com/example/readonly.git#main\"\n" +
                "  }\n" +
                "}\n");
            File.WriteAllBytes(manifestPath, original);

            Assert.That(
                PackageManifestGitDependencyStore.TryRemoveDependencyAtPath(
                    manifestPath,
                    "com.example.readonly",
                    "https://github.com/example/readonly.git#other",
                    out _,
                    out string error),
                Is.False);
            Assert.That(error, Does.Contain("changed"));
            Assert.That(File.ReadAllBytes(manifestPath), Is.EqualTo(original));

            Assert.That(
                PackageManifestGitDependencyStore.TryRemoveDependencyAtPath(
                    manifestPath,
                    "com.example.readonly",
                    "https://github.com/example/readonly.git#main",
                    out PackageManifestDependencyMutation mutation,
                    out error),
                Is.True,
                error);
            Assert.That(
                PackageManifestGitDependencyStore.TryGetDependencyAtPath(
                    manifestPath,
                    "com.example.readonly",
                    out _,
                    out _),
                Is.False);
            Assert.That(mutation.TryRollback(out error), Is.True, error);
            Assert.That(File.ReadAllBytes(manifestPath), Is.EqualTo(original));
        }

        [Test]
        public void Parser_RejectsDuplicateDependenciesAndOversizedManifest()
        {
            File.WriteAllText(
                manifestPath,
                "{\"dependencies\":{},\"dependencies\":{}}",
                new UTF8Encoding(false));
            Assert.That(
                PackageManifestGitDependencyStore.TryGetDependencyAtPath(
                    manifestPath,
                    "com.example.readonly",
                    out _,
                    out string duplicateError),
                Is.False);
            Assert.That(duplicateError, Does.Contain("parsed"));

            File.WriteAllBytes(
                manifestPath,
                new byte[PackageManifestGitDependencyStore.MaximumManifestByteCount + 1]);
            Assert.That(
                PackageManifestGitDependencyStore.TryGetDependencyAtPath(
                    manifestPath,
                    "com.example.readonly",
                    out _,
                    out string oversizedError),
                Is.False);
            Assert.That(oversizedError, Does.Contain("2 MiB"));
        }

        [TestCase("https://github.com/example/repository.git", "", "https://github.com/example/repository.git")]
        [TestCase("https://github.com/example/repository.git", "main", "https://github.com/example/repository.git#main")]
        [TestCase("git@github.com:example/repository.git", "feature/work", "git@github.com:example/repository.git#feature/work")]
        public void BuildAndParseGitSpec_RoundTripsSupportedRootRepositories(
            string repositoryUrl,
            string revision,
            string expectedSpec)
        {
            Assert.That(
                PackageManifestGitDependencyStore.TryBuildGitSpec(
                    repositoryUrl,
                    revision,
                    out string spec,
                    out string error),
                Is.True,
                error);
            Assert.That(spec, Is.EqualTo(expectedSpec));
            Assert.That(
                PackageManifestGitDependencyStore.TryParseGitSpec(
                    spec,
                    out string parsedUrl,
                    out string parsedRevision,
                    out error),
                Is.True,
                error);
            Assert.That(parsedUrl, Is.EqualTo(repositoryUrl));
            Assert.That(parsedRevision, Is.EqualTo(revision));
        }

        [TestCase(
            "https://github.com/example/repository.git?path=/Packages/Runtime",
            "",
            "/Packages/Runtime")]
        [TestCase(
            "https://github.com/example/repository.git?path=/Packages/Runtime#main",
            "main",
            "/Packages/Runtime")]
        [TestCase(
            "git@github.com:example/repository.git?path=/Packages/Runtime#feature/work",
            "feature/work",
            "/Packages/Runtime")]
        public void ParseGitSpec_PreservesUnityPackageSubfolderSeparately(
            string spec,
            string expectedRevision,
            string expectedSubfolder)
        {
            Assert.That(
                PackageManifestGitDependencyStore.TryParseGitSpec(
                    spec,
                    out string repositoryUrl,
                    out string revision,
                    out string packageSubfolder,
                    out string error),
                Is.True,
                error);
            Assert.That(
                repositoryUrl,
                Is.EqualTo(spec.Substring(0, spec.IndexOf("?path=", StringComparison.Ordinal))));
            Assert.That(revision, Is.EqualTo(expectedRevision));
            Assert.That(packageSubfolder, Is.EqualTo(expectedSubfolder));

            File.WriteAllText(
                manifestPath,
                "{\n  \"dependencies\": {\n    \"com.example.readonly\": \"" +
                spec + "\"\n  }\n}\n",
                new UTF8Encoding(false));
            Assert.That(
                PackageManifestGitDependencyStore.TryGetDependencyAtPath(
                    manifestPath,
                    "com.example.readonly",
                    out PackageManifestGitDependency dependency,
                    out error),
                Is.True,
                error);
            Assert.That(dependency.PackageSubfolder, Is.EqualTo(expectedSubfolder));
            Assert.That(dependency.IsRepositoryRootPackage, Is.False);
        }

        [Test]
        public void ParseGitSpec_RedundantRootPathRemainsRootEligible()
        {
            Assert.That(
                PackageManifestGitDependencyStore.TryParseGitSpec(
                    "https://github.com/example/repository.git?path=/#main",
                    out _,
                    out string revision,
                    out string packageSubfolder,
                    out string error),
                Is.True,
                error);
            Assert.That(revision, Is.EqualTo("main"));
            Assert.That(packageSubfolder, Is.Empty);
        }

        [TestCase("http://github.com/example/repository.git")]
        [TestCase("https://token@github.com/example/repository.git")]
        [TestCase("https://github.com/example/repository.git?token=value")]
        [TestCase("https://github.com/example/repository.git?path=")]
        [TestCase("https://github.com/example/repository.git?path=Package")]
        [TestCase("https://github.com/example/repository.git?path=/../Package")]
        [TestCase("https://github.com/example/repository.git?path=/Package&token=value")]
        [TestCase("https://github.com/example/repository.git?path=/One?path=/Two")]
        [TestCase("https://github.com/example/repository.git#main?path=/Package")]
        [TestCase("https://github.com/example/repository.git#")]
        [TestCase("https://github.com/example/repository.git#bad..revision")]
        public void ParseGitSpec_RejectsUnsafeOrAmbiguousValues(string spec)
        {
            Assert.That(
                PackageManifestGitDependencyStore.TryParseGitSpec(
                    spec,
                    out _,
                    out _,
                    out _),
                Is.False);
        }

        private static byte[] WithUtf8Bom(string value)
        {
            byte[] content = Encoding.UTF8.GetBytes(value);
            var result = new byte[3 + content.Length];
            result[0] = 0xEF;
            result[1] = 0xBB;
            result[2] = 0xBF;
            Buffer.BlockCopy(content, 0, result, 3, content.Length);
            return result;
        }
    }
}
