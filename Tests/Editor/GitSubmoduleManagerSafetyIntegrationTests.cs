using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using NUnit.Framework;

namespace MartinCalander.GitSubmoduleManager.Editor.Tests
{
    [Parallelizable(ParallelScope.None)]
    public sealed class GitSubmoduleManagerValidationContractTests
    {
        [TestCase("com.martincalander.gitsubmodulemanager")]
        [TestCase("com.kamgam.component-drag-helper")]
        [TestCase("com.mibdev.fullscreen_editor")]
        [TestCase("com.vendor.package-2d_tools")]
        public void PackageNames_AllowValidUpmSeparators(string packageName)
        {
            Assert.That(GitUtility.IsValidPackageName(packageName), Is.True);
            Assert.That(GitUtility.IsPackagePath("Packages/" + packageName), Is.True);
        }

        [TestCase("packages/com.vendor.package")]
        [TestCase("PACKAGES/com.vendor.package")]
        [TestCase("Packages/com.vendor.package/nested")]
        [TestCase("Packages/../com.vendor.package")]
        [TestCase("Packages/com.vendor")]
        [TestCase("Packages/vendor.package")]
        public void PackagePaths_RequireExactDirectPackagesDirectory(string path)
        {
            Assert.That(GitUtility.IsPackagePath(path), Is.False);
        }

        [TestCase("https://github.com/owner/repository.git")]
        [TestCase("ssh://git@github.com/owner/repository.git")]
        [TestCase("git@github.com:owner/repository.git")]
        [TestCase("file:///tmp/repository")]
        [TestCase("../local-repository")]
        public void RepositoryUrls_AllowKnownGitTransports(string url)
        {
            Assert.That(GitUtility.IsValidRepositoryUrl(url), Is.True);
        }

        [TestCase("ext::sh -c malicious")]
        [TestCase("ftp://example.com/repository.git")]
        [TestCase("custom-helper://example.com/repository.git")]
        [TestCase("http://example.com/repository.git")]
        [TestCase("git://github.com/owner/repository.git")]
        [TestCase("https://user:secret@example.com/repository.git")]
        [TestCase("ssh://git:secret@example.com/repository.git")]
        [TestCase("https://example.com/repository.git?token=secret")]
        [TestCase("https://example.com/repository.git#access_token=secret")]
        public void RepositoryUrls_RejectExecutableHelpersAndEmbeddedSecrets(string url)
        {
            Assert.That(GitUtility.IsValidRepositoryUrl(url), Is.False);
        }

        [TestCase(
            "ssh://alice@git.example.com/team/repository.git",
            "ssh://alice@GIT.EXAMPLE.COM/team/repository.git",
            true)]
        [TestCase(
            "ssh://alice@git.example.com/team/repository.git",
            "ssh://bob@git.example.com/team/repository.git",
            false)]
        [TestCase(
            "https://github.com/owner/repository.git",
            "git@github.com:owner/repository.git",
            true)]
        public void RepositoryUrlEquivalence_PreservesGenericSshUserIdentity(
            string first,
            string second,
            bool expected)
        {
            Assert.That(
                GitUtility.AreRepositoryUrlsEquivalent(first, second),
                Is.EqualTo(expected));
        }

        [TestCase(
            "https://github.com/owner/repository.git",
            true,
            "https://github.com/owner/repository")]
        [TestCase(
            "git@github.com:owner/repository.git",
            true,
            "https://github.com/owner/repository")]
        [TestCase("file:///tmp/repository", false, "")]
        [TestCase("http://github.com/owner/repository.git", false, "")]
        [TestCase("ext::open-something", false, "")]
        public void RepositoryWebUrls_OnlyExposeSafeBrowserTargets(
            string repositoryUrl,
            bool expectedSuccess,
            string expectedWebUrl)
        {
            bool success = GitUtility.TryGetRepositoryWebUrl(repositoryUrl, out string webUrl);

            Assert.That(success, Is.EqualTo(expectedSuccess));
            Assert.That(webUrl, Is.EqualTo(expectedWebUrl));
        }

        [Test]
        public void Quote_DoublesTrailingBackslashBeforeClosingQuote()
        {
            Assert.That(
                GitUtility.Quote(@"C:\Repos\Package\"),
                Is.EqualTo("\"C:\\Repos\\Package\\\\\""));
        }

        [Test]
        public void RedactCredentials_RemovesUriPasswordsAndTokenParameters()
        {
            const string diagnostic =
                "https://user:secret@example.com/repository.git?token=query-secret " +
                "ssh://git:ssh-secret@example.com/repository.git#access_token=fragment-secret";

            string redacted = GitUtility.RedactCredentials(diagnostic);

            Assert.That(redacted, Does.Not.Contain("secret"));
            Assert.That(redacted, Does.Contain("***"));
        }

        [Test]
        public void BranchValidation_AllowsGitSuperprojectBranchSentinel()
        {
            Assert.That(GitUtility.IsValidBranchName("."), Is.True);
        }

        [Test]
        public void RepositoryAndBranchValidation_RejectsOversizedProcessInputs()
        {
            Assert.That(
                GitUtility.IsValidBranchName(new string('b', 1025)),
                Is.False);
            Assert.That(
                GitUtility.IsValidBranchName(new string(' ', 1025)),
                Is.False);
            Assert.That(
                GitUtility.IsValidRepositoryUrl("https://example.com/" + new string('r', 4096)),
                Is.False);
            Assert.That(
                GitUtility.IsValidRepositoryUrl("../" + new string('p', 4096)),
                Is.False);
            Assert.That(
                GitUtility.IsPackagePath("Packages/" + new string('p', 215)),
                Is.False);
        }

        [Test]
        public void RepositoryUrlDisplay_RedactsFlattensAndCapsUntrustedValues()
        {
            string value =
                "https://user:secret@example.com/repository.git?token=query-secret\n" +
                new string('x', 300);

            string display = GitUtility.FormatRepositoryUrlForDisplay(value);

            Assert.That(display, Does.Not.Contain("secret"));
            Assert.That(display, Does.Not.Contain("\n"));
            Assert.That(display.Length, Is.LessThanOrEqualTo(160));
            Assert.That(display, Does.EndWith("…"));
        }

        [Test]
        public void ValidManifestFileReader_IsBoundedAndUsesFullUpmValidation()
        {
            string path = Path.Combine(Path.GetTempPath(), "GitPackageManifest-" + Guid.NewGuid().ToString("N") + ".json");
            try
            {
                File.WriteAllText(
                    path,
                    "{\"name\":\"com.example.valid-package\",\"version\":\"1.2.3\"}\n");
                Assert.That(
                    GitUtility.TryReadValidPackageManifest(path, out string packageName, out string validError),
                    Is.True,
                    validError);
                Assert.That(packageName, Is.EqualTo("com.example.valid-package"));

                File.WriteAllBytes(path, new byte[] { 0x7b, 0x22, 0xc3, 0x28, 0x22, 0x7d });
                Assert.That(
                    GitUtility.TryReadValidPackageManifest(path, out _, out string malformedEncodingError),
                    Is.False);
                Assert.That(malformedEncodingError, Does.Contain("valid UTF-8"));
                Assert.That(malformedEncodingError.Length, Is.LessThan(1024));

                File.WriteAllBytes(path, new byte[(1024 * 1024) + 1]);
                Assert.That(
                    GitUtility.TryReadValidPackageManifest(path, out _, out string oversizedError),
                    Is.False);
                Assert.That(oversizedError, Does.Contain("1 MiB"));
            }
            finally
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
        }

        [Test]
        public void ValidManifestFileReader_RejectsSymbolicLinkManifest()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                Assert.Ignore("Creating an unprivileged symbolic link is not portable on Windows test hosts.");

            string directory = Path.Combine(
                Path.GetTempPath(),
                "GitPackageManifestLink-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            string target = Path.Combine(directory, "outside.json");
            string link = Path.Combine(directory, "package.json");
            try
            {
                File.WriteAllText(
                    target,
                    "{\"name\":\"com.example.external\",\"version\":\"1.0.0\"}\n");
                CommandResult linkResult = CliCommandRunner.Run(
                    "/bin/ln",
                    "-s -- " + GitUtility.Quote(target) + " " + GitUtility.Quote(link),
                    directory,
                    5000);
                if (!linkResult.IsSuccess)
                    Assert.Ignore("The test host could not create a symbolic link: " + linkResult.StdErr);

                Assert.That(
                    GitUtility.TryReadValidPackageManifest(link, out _, out string error),
                    Is.False);
                Assert.That(error, Does.Contain("regular file"));
            }
            finally
            {
                if (File.Exists(link))
                    File.Delete(link);
                if (File.Exists(target))
                    File.Delete(target);
                if (Directory.Exists(directory))
                    Directory.Delete(directory);
            }
        }

        [Test]
        public void CancellationAwareGitReads_HonorPreCancelledToken()
        {
            using var cancellationSource = new CancellationTokenSource();
            cancellationSource.Cancel();

            Assert.Throws<OperationCanceledException>(() =>
                GitUtility.IsGitAvailable(out _, out _, cancellationSource.Token));
            Assert.Throws<OperationCanceledException>(() =>
                GitUtility.TryGetSubmodules(out _, out _, cancellationSource.Token));
            Assert.Throws<OperationCanceledException>(() =>
                GitUtility.TryPrepareAddSubmodule(
                    "https://github.com/example/package.git",
                    "Packages/com.example.package",
                    out AddSubmodulePlan _,
                    out _,
                    cancellationSource.Token));
            Assert.Throws<OperationCanceledException>(() =>
                GitUtility.TryResolveSubmoduleGitDir(
                    "Packages/com.example.package",
                    out _,
                    out _,
                    cancellationSource.Token));
        }

    }

    [Parallelizable(ParallelScope.None)]
    public sealed class CliCommandRunnerSafetyContractTests
    {
        [Test]
        public void ProcessEnvironment_RemovesGitOverridesAndUnitySecrets()
        {
            string oldGitDir = Environment.GetEnvironmentVariable("GIT_DIR");
            string oldGitConfig = Environment.GetEnvironmentVariable("GIT_CONFIG_COUNT");
            string oldUnityPassword = Environment.GetEnvironmentVariable("UNITY_PASSWORD");
            try
            {
                Environment.SetEnvironmentVariable("GIT_DIR", "/tmp/redirected-git-dir");
                Environment.SetEnvironmentVariable("GIT_CONFIG_COUNT", "1");
                Environment.SetEnvironmentVariable("UNITY_PASSWORD", "must-not-leak");

                var startInfo = ProcessCommandRunner.BuildProcessStartInfo(
                    "/usr/bin/git",
                    Array.Empty<string>(),
                    Environment.CurrentDirectory);

                Assert.That(startInfo.EnvironmentVariables["GIT_DIR"], Is.Null);
                Assert.That(startInfo.EnvironmentVariables["GIT_CONFIG_COUNT"], Is.Null);
                Assert.That(startInfo.EnvironmentVariables["UNITY_PASSWORD"], Is.Null);
                Assert.That(startInfo.EnvironmentVariables["GIT_TERMINAL_PROMPT"], Is.EqualTo("0"));
                Assert.That(startInfo.EnvironmentVariables["SSH_ASKPASS_REQUIRE"], Is.EqualTo("never"));
            }
            finally
            {
                Environment.SetEnvironmentVariable("GIT_DIR", oldGitDir);
                Environment.SetEnvironmentVariable("GIT_CONFIG_COUNT", oldGitConfig);
                Environment.SetEnvironmentVariable("UNITY_PASSWORD", oldUnityPassword);
            }
        }

        [Test]
        public void ArgumentEncoding_RoundTripsEmptyQuotesSpacesAndTrailingBackslashes()
        {
            IReadOnlyList<string> expected = new[]
            {
                string.Empty,
                "plain",
                @"C:\Repos\My Package\",
                "embedded\"quote",
                "two words"
            };

            string encoded = ProcessCommandRunner.EncodeArgumentList(expected);
            bool parsed = ProcessCommandRunner.TryTokenizeArguments(encoded, out IReadOnlyList<string> actual);

            Assert.That(parsed, Is.True);
            CollectionAssert.AreEqual(expected, actual);
        }

        [Test]
        public void BoundedOutput_PreservesRecentTailAndReportsTruncation()
        {
            var buffer = new BoundedTextBuffer(10);
            buffer.AppendLine("older");
            buffer.AppendLine("abcdefghij");

            string snapshot = buffer.GetSnapshot();

            Assert.That(buffer.IsTruncated, Is.True);
            Assert.That(snapshot, Does.Contain("output truncated"));
            Assert.That(snapshot, Does.EndWith("abcdefghij"));
            Assert.That(snapshot, Does.Not.Contain("older"));
        }

        [Test]
        public void StrictUtf8Output_ReportsInvalidGitBlobBytesWithoutReplacement()
        {
            byte[] prefix = Encoding.UTF8.GetBytes(
                "fileFormatVersion: 2\nguid: 0123456789abcdef0123456789abcdef\n");
            var contents = new byte[prefix.Length + 1];
            Buffer.BlockCopy(prefix, 0, contents, 0, prefix.Length);
            contents[contents.Length - 1] = 0x80;

            CommandResult read = ReadGitBlobWithStrictUtf8(contents);

            Assert.That(read.IsSuccess, Is.True, read.StdErr);
            Assert.That(read.StdOutInvalidUtf8, Is.True);
            Assert.That(read.StdOut, Does.Not.Contain("\uFFFD"));
        }

        [Test]
        public void StrictUtf8Output_RejectsUtf16LeBomEvenWhenDecodedTextWouldBeValid()
        {
            const string validMeta =
                "fileFormatVersion: 2\nguid: 0123456789abcdef0123456789abcdef\n";
            byte[] preamble = Encoding.Unicode.GetPreamble();
            byte[] text = Encoding.Unicode.GetBytes(validMeta);
            var contents = new byte[preamble.Length + text.Length];
            Buffer.BlockCopy(preamble, 0, contents, 0, preamble.Length);
            Buffer.BlockCopy(text, 0, contents, preamble.Length, text.Length);

            CommandResult read = ReadGitBlobWithStrictUtf8(contents);

            Assert.That(read.IsSuccess, Is.True, read.StdErr);
            Assert.That(read.StdOutInvalidUtf8, Is.True);
            Assert.That(read.StdOut, Does.Not.Contain(validMeta));
        }

        [Test]
        public void StrictUtf8Output_RejectsLeadingUtf16BomWithIncompleteCodeUnit()
        {
            CommandResult read = ReadGitBlobWithStrictUtf8(
                new byte[] { 0xff, 0xfe, 0xfd });

            Assert.That(read.IsSuccess, Is.True, read.StdErr);
            Assert.That(read.StdOutInvalidUtf8, Is.True);
            Assert.That(read.StdOut, Is.Empty);
        }

        [Test]
        public void StrictUtf8Output_RejectsIncompleteSequenceAtEndOfStream()
        {
            byte[] prefix = Encoding.UTF8.GetBytes("valid-prefix");
            var contents = new byte[prefix.Length + 1];
            Buffer.BlockCopy(prefix, 0, contents, 0, prefix.Length);
            contents[contents.Length - 1] = 0xc3;

            CommandResult read = ReadGitBlobWithStrictUtf8(contents);

            Assert.That(read.IsSuccess, Is.True, read.StdErr);
            Assert.That(read.StdOutInvalidUtf8, Is.True);
            Assert.That(read.StdOut, Is.EqualTo("valid-prefix"));
        }

        [Test]
        public void StrictUtf8Output_AcceptsAndStripsGenuineUtf8Bom()
        {
            const string validMeta =
                "fileFormatVersion: 2\nguid: 0123456789abcdef0123456789abcdef\n";
            byte[] preamble = new UTF8Encoding(true).GetPreamble();
            byte[] text = Encoding.UTF8.GetBytes(validMeta);
            var contents = new byte[preamble.Length + text.Length];
            Buffer.BlockCopy(preamble, 0, contents, 0, preamble.Length);
            Buffer.BlockCopy(text, 0, contents, preamble.Length, text.Length);

            CommandResult read = ReadGitBlobWithStrictUtf8(contents);

            Assert.That(read.IsSuccess, Is.True, read.StdErr);
            Assert.That(read.StdOutInvalidUtf8, Is.False);
            Assert.That(read.StdOut, Is.EqualTo(validMeta.TrimEnd('\n')));
        }

        [Test]
        public void StrictUtf8Output_PreservesBoundedCaptureForValidText()
        {
            var contents = new byte[
                CliCommandRunner.MaxCapturedCharactersPerStream + 1];
            for (int index = 0; index < contents.Length; index++)
                contents[index] = (byte)'a';

            CommandResult read = ReadGitBlobWithStrictUtf8(contents);

            Assert.That(read.IsSuccess, Is.True, read.StdErr);
            Assert.That(read.StdOutInvalidUtf8, Is.False);
            Assert.That(read.StdOutTruncated, Is.True);
            Assert.That(read.StdOut, Does.Contain("output truncated"));
        }

        [Test]
        public void ResolveGit_ReturnsCanonicalAbsoluteExecutable()
        {
            if (!ProcessCommandRunner.TryResolveCommand("git", out ExecutableResolution resolution))
                Assert.Ignore("Git is not installed or could not be resolved on this machine.");

            Assert.That(Path.IsPathRooted(resolution.ResolvedPath), Is.True);
            Assert.That(File.Exists(resolution.ResolvedPath), Is.True);
            Assert.That(Path.GetFullPath(resolution.ResolvedPath), Is.EqualTo(resolution.ResolvedPath));
        }

        private static CommandResult ReadGitBlobWithStrictUtf8(byte[] contents)
        {
            if (!ProcessCommandRunner.TryResolveCommand(
                    "git",
                    out ExecutableResolution git))
            {
                Assert.Ignore("Git is not installed or could not be resolved on this machine.");
            }

            string repository = Path.Combine(
                Path.GetTempPath(),
                "gsm-strict-utf8-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(repository);
            try
            {
                var runner = new ProcessCommandRunner();
                CommandResult init = runner.Run(new CommandSpec
                {
                    FileName = git.ResolvedPath,
                    ArgumentList = new[] { "init", "--quiet" },
                    WorkingDirectory = repository,
                    TimeoutMs = 5000
                });
                Assert.That(init.IsSuccess, Is.True, init.StdErr);

                string blobPath = Path.Combine(repository, "output-blob");
                File.WriteAllBytes(blobPath, contents);
                CommandResult hash = runner.Run(new CommandSpec
                {
                    FileName = git.ResolvedPath,
                    ArgumentList = new[] { "hash-object", "-w", "--", blobPath },
                    WorkingDirectory = repository,
                    TimeoutMs = 5000
                });
                Assert.That(hash.IsSuccess, Is.True, hash.StdErr);

                return runner.Run(new CommandSpec
                {
                    FileName = git.ResolvedPath,
                    ArgumentList = new[] { "cat-file", "blob", hash.StdOut.Trim() },
                    WorkingDirectory = repository,
                    TimeoutMs = 5000,
                    RequireStrictUtf8StdOut = true
                });
            }
            finally
            {
                if (Directory.Exists(repository))
                    Directory.Delete(repository, true);
            }
        }

        [Test]
        public void AsyncHandle_CancelPublishesCancelledTerminalResult()
        {
            var runner = new CancellationAwareRunner();
            var handle = new AsyncCommandHandle(runner, new CommandSpec
            {
                FileName = "unused",
                WorkingDirectory = Environment.CurrentDirectory,
                TimeoutMs = 5000
            });

            handle.Start();
            Assert.That(runner.Started.Wait(1000), Is.True, "The fake command did not start.");
            handle.Cancel();

            Assert.That(handle.WaitForCompletion(2000), Is.True, "The cancelled worker did not drain.");
            Assert.That(handle.IsComplete, Is.True);
            Assert.That(handle.Result, Is.Not.Null);
            Assert.That(handle.Result.Cancelled, Is.True);
            Assert.That(handle.Result.TerminationConfirmed, Is.True);
            Assert.That(handle.StatusMessage, Is.EqualTo("Cancelled"));
        }

        private sealed class CancellationAwareRunner : ICommandRunner
        {
            internal ManualResetEventSlim Started { get; } = new ManualResetEventSlim(false);

            public CommandResult Run(CommandSpec spec)
            {
                Started.Set();
                spec.CancellationToken.WaitHandle.WaitOne(1500);
                return new CommandResult
                {
                    ExitCode = -1,
                    StdOut = string.Empty,
                    StdErr = "cancelled by test",
                    Cancelled = spec.CancellationToken.IsCancellationRequested,
                    TerminationConfirmed = true
                };
            }
        }
    }

    [Parallelizable(ParallelScope.None)]
    public sealed class GitSubmoduleManagerSafetyIntegrationTests
    {
        private const string PackagePath = "Packages/com.example.integration-package";

        private IDisposable projectRootOverride;
        private ICommandRunner previousRunner;
        private string sandboxRoot;
        private string parentRoot;
        private string sourceRoot;

        [SetUp]
        public void SetUp()
        {
            if (!CliCommandRunner.TryResolveCommand("git", out _))
                Assert.Ignore("Git is not installed or could not be resolved on this machine.");

            previousRunner = CliCommandRunner.CurrentRunner;
            CliCommandRunner.ResetRunner();

            sandboxRoot = Path.Combine(Path.GetTempPath(), "GitSubmoduleManagerTests-" + Guid.NewGuid().ToString("N"));
            parentRoot = Path.Combine(sandboxRoot, "parent");
            sourceRoot = Path.Combine(sandboxRoot, "source");
            Directory.CreateDirectory(parentRoot);
            Directory.CreateDirectory(sourceRoot);

            InitializeRepository(sourceRoot);
            File.WriteAllText(
                Path.Combine(sourceRoot, "package.json"),
                "{\"name\":\"com.example.integration-package\",\"version\":\"1.0.0\",\"displayName\":\"Integration Package\"}\n");
            ExpectGit(sourceRoot, "add -- package.json");
            ExpectGit(sourceRoot, "commit -m \"Initial package\"");

            InitializeRepository(parentRoot);
            File.WriteAllText(Path.Combine(parentRoot, "README.md"), "integration fixture\n");
            ExpectGit(parentRoot, "add -- README.md");
            ExpectGit(parentRoot, "commit -m \"Initial parent\"");

            RedirectProjectRoot(parentRoot);
            Assert.That(
                GitUtility.TryAddSubmodule(sourceRoot, PackagePath, string.Empty, out string addError),
                Is.True,
                addError);
            ExpectGit(parentRoot, "commit -am \"Add package submodule\"");
        }

        [TearDown]
        public void TearDown()
        {
            projectRootOverride?.Dispose();
            if (previousRunner != null)
                CliCommandRunner.CurrentRunner = previousRunner;

            DeleteDirectoryBestEffort(sandboxRoot);
        }

        [Test]
        public void Add_PostconditionsVerifyRegistrationGitlinkAndCleanWorktree()
        {
            var plan = new AddSubmodulePlan { Path = PackagePath };
            string inspectedCommit = ExpectGit(
                    Path.Combine(parentRoot, PackagePath),
                    "rev-parse --verify HEAD")
                .StdOut.Trim();

            bool verified = GitUtility.TryVerifyAddedSubmodule(
                plan,
                sourceRoot,
                string.Empty,
                inspectedCommit,
                out string error);

            Assert.That(verified, Is.True, error);
        }

        [Test]
        public void Add_PostconditionsRejectCleanGitlinkAtDifferentCommitThanInspected()
        {
            var plan = new AddSubmodulePlan { Path = PackagePath };
            File.WriteAllText(
                Path.Combine(sourceRoot, "README.md"),
                "later remote commit\n");
            ExpectGit(sourceRoot, "add -- README.md");
            ExpectGit(sourceRoot, "commit -m \"Later remote commit\"");
            string differentInspectedCommit = ExpectGit(
                    sourceRoot,
                    "rev-parse --verify HEAD")
                .StdOut.Trim();

            bool verified = GitUtility.TryVerifyAddedSubmodule(
                plan,
                sourceRoot,
                string.Empty,
                differentInspectedCommit,
                out string error);

            Assert.That(verified, Is.False);
            Assert.That(error, Does.Contain("exact inspected Git commit"));
        }

        [Test]
        public void Add_PostconditionsRejectStageOnlyGitmodulesRedirectAtTerminalBoundary()
        {
            var plan = new AddSubmodulePlan { Path = PackagePath };
            string inspectedCommit = ExpectGit(
                    Path.Combine(parentRoot, PackagePath),
                    "rev-parse --verify HEAD")
                .StdOut.Trim();
            string gitModulesPath = Path.Combine(parentRoot, ".gitmodules");
            string originalContents = File.ReadAllText(gitModulesPath);
            const string redirectedUrl =
                "https://example.invalid/redirected-package.git";
            string redirectedContents = originalContents.Replace(
                sourceRoot,
                redirectedUrl);
            Assert.That(
                redirectedContents,
                Is.Not.EqualTo(originalContents),
                "The fixture must replace the registered source URL.");
            string redirectedPath = Path.Combine(
                parentRoot,
                "redirected.gitmodules.fixture");
            File.WriteAllText(redirectedPath, redirectedContents);
            string redirectedBlob = ExpectGit(
                    parentRoot,
                    "hash-object -w --no-filters -- " +
                    GitUtility.Quote(redirectedPath))
                .StdOut.Trim();

            using (GitUtility.OverrideBeforeAddedSubmoduleTerminalProofForTests(
                       _ => ExpectGit(
                           parentRoot,
                           "update-index --cacheinfo 100644," +
                           redirectedBlob + ",.gitmodules")))
            {
                bool verified = GitUtility.TryVerifyAddedSubmodule(
                    plan,
                    sourceRoot,
                    string.Empty,
                    inspectedCommit,
                    out string error);

                Assert.That(verified, Is.False);
                Assert.That(error, Does.Contain("staged .gitmodules"));
            }

            Assert.That(
                ExpectGit(parentRoot, "show :.gitmodules").StdOut,
                Does.Contain(redirectedUrl),
                "Verification must preserve the concurrent staged redirect for review.");
            Assert.That(
                File.ReadAllText(gitModulesPath),
                Is.EqualTo(originalContents),
                "The stage-only redirect must not alter the worktree fixture.");
        }

        [Test]
        public void Add_PostconditionsRejectOriginOnlyRedirectAtTerminalBoundary()
        {
            var plan = new AddSubmodulePlan { Path = PackagePath };
            string inspectedCommit = ExpectGit(
                    Path.Combine(parentRoot, PackagePath),
                    "rev-parse --verify HEAD")
                .StdOut.Trim();
            const string redirectedUrl =
                "https://example.invalid/redirected-origin.git";

            using (GitUtility.OverrideBeforeAddedSubmoduleTerminalProofForTests(
                       path => ExpectGit(
                           Path.Combine(parentRoot, path),
                           "remote set-url origin " +
                           GitUtility.Quote(redirectedUrl))))
            {
                bool verified = GitUtility.TryVerifyAddedSubmodule(
                    plan,
                    sourceRoot,
                    string.Empty,
                    inspectedCommit,
                    out string error);

                Assert.That(verified, Is.False);
                Assert.That(error, Does.Contain("origin"));
            }

            Assert.That(
                ExpectGit(
                        Path.Combine(parentRoot, PackagePath),
                        "remote get-url origin")
                    .StdOut.Trim(),
                Is.EqualTo(redirectedUrl),
                "Verification must preserve the concurrent origin redirect for review.");
        }

        [Test]
        public void Add_PostconditionsRejectOriginSwapAfterTerminalOrigin()
        {
            var plan = new AddSubmodulePlan { Path = PackagePath };
            string inspectedCommit = ExpectGit(
                    Path.Combine(parentRoot, PackagePath),
                    "rev-parse --verify HEAD")
                .StdOut.Trim();
            const string redirectedUrl =
                "https://example.invalid/terminal-origin.git";

            using (GitUtility.OverrideBeforeAddedSubmoduleClosingProofForTests(
                       path => ExpectGit(
                           Path.Combine(parentRoot, path),
                           "remote set-url origin " +
                           GitUtility.Quote(redirectedUrl))))
            {
                bool verified = GitUtility.TryVerifyAddedSubmodule(
                    plan,
                    sourceRoot,
                    string.Empty,
                    inspectedCommit,
                    out string error);

                Assert.That(verified, Is.False);
                Assert.That(error, Does.Contain("origin"));
            }

            Assert.That(
                ExpectGit(
                        Path.Combine(parentRoot, PackagePath),
                        "remote get-url origin")
                    .StdOut.Trim(),
                Is.EqualTo(redirectedUrl));
        }

        [Test]
        public void Add_PostconditionsRejectHeadSwapAfterTerminalOrigin()
        {
            var plan = new AddSubmodulePlan { Path = PackagePath };
            string inspectedCommit = ExpectGit(
                    Path.Combine(parentRoot, PackagePath),
                    "rev-parse --verify HEAD")
                .StdOut.Trim();
            File.WriteAllText(
                Path.Combine(sourceRoot, "terminal-change.txt"),
                "terminal commit swap fixture\n");
            ExpectGit(sourceRoot, "add -- terminal-change.txt");
            ExpectGit(sourceRoot, "commit -m \"Add terminal commit fixture\"");
            string laterCommit = ExpectGit(sourceRoot, "rev-parse --verify HEAD")
                .StdOut.Trim();

            using (GitUtility.OverrideBeforeAddedSubmoduleClosingProofForTests(
                       path =>
                       {
                           string worktree = Path.Combine(parentRoot, path);
                           ExpectGit(worktree, "fetch --no-tags origin");
                           ExpectGit(
                               worktree,
                               "checkout --detach " +
                               GitUtility.Quote(laterCommit));
                       }))
            {
                bool verified = GitUtility.TryVerifyAddedSubmodule(
                    plan,
                    sourceRoot,
                    string.Empty,
                    inspectedCommit,
                    out string error);

                Assert.That(verified, Is.False);
                Assert.That(error, Does.Contain("HEAD"));
            }

            Assert.That(
                ExpectGit(
                        Path.Combine(parentRoot, PackagePath),
                        "rev-parse --verify HEAD")
                    .StdOut.Trim(),
                Is.EqualTo(laterCommit),
                "Verification must preserve the concurrent commit for review.");
            Assert.That(
                ExpectGit(
                        parentRoot,
                        "ls-files --stage -- " + GitUtility.Quote(PackagePath))
                    .StdOut,
                Does.Contain(inspectedCommit),
                "The staged gitlink must remain untouched by verification.");
        }

        [Test]
        public void Add_PostconditionsRejectLateUntrackedFileAtClosingBoundary()
        {
            var plan = new AddSubmodulePlan { Path = PackagePath };
            string inspectedCommit = ExpectGit(
                    Path.Combine(parentRoot, PackagePath),
                    "rev-parse --verify HEAD")
                .StdOut.Trim();
            string lateFile = Path.Combine(
                parentRoot,
                PackagePath,
                "late-untracked.txt");

            using (GitUtility.OverrideBeforeAddedSubmoduleClosingProofForTests(
                       _ => File.WriteAllText(
                           lateFile,
                           "concurrent untracked package data\n")))
            {
                bool verified = GitUtility.TryVerifyAddedSubmodule(
                    plan,
                    sourceRoot,
                    string.Empty,
                    inspectedCommit,
                    out string error);

                Assert.That(verified, Is.False);
                Assert.That(error, Does.Contain("local changes"));
            }

            Assert.That(
                File.ReadAllText(lateFile),
                Is.EqualTo("concurrent untracked package data\n"),
                "Verification must preserve late package data for review.");
        }

        [Test]
        public void Add_PostconditionsRejectGitmodulesSymlinkAtClosingBoundary()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                Assert.Ignore("Creating an unprivileged symbolic link is not portable on Windows test hosts.");

            var plan = new AddSubmodulePlan { Path = PackagePath };
            string inspectedCommit = ExpectGit(
                    Path.Combine(parentRoot, PackagePath),
                    "rev-parse --verify HEAD")
                .StdOut.Trim();
            string gitModulesPath = Path.Combine(parentRoot, ".gitmodules");
            string outsidePath = Path.Combine(
                sandboxRoot,
                "terminal-gitmodules-target");
            byte[] originalContents = File.ReadAllBytes(gitModulesPath);
            File.WriteAllBytes(outsidePath, originalContents);

            using (GitUtility.OverrideBeforeAddedSubmoduleClosingProofForTests(
                       _ =>
                       {
                           File.Delete(gitModulesPath);
                           CommandResult linkResult = CliCommandRunner.Run(
                               "/bin/ln",
                               "-s -- " + GitUtility.Quote(outsidePath) +
                               " .gitmodules",
                               parentRoot,
                               5000);
                           Assert.That(
                               linkResult.IsSuccess,
                               Is.True,
                               linkResult.StdErr);
                       }))
            {
                bool verified = GitUtility.TryVerifyAddedSubmodule(
                    plan,
                    sourceRoot,
                    string.Empty,
                    inspectedCommit,
                    out string error);

                Assert.That(verified, Is.False);
                Assert.That(error, Does.Contain("regular"));
            }

            Assert.That(
                (File.GetAttributes(gitModulesPath) &
                 FileAttributes.ReparsePoint) != 0,
                Is.True,
                "Verification must preserve the concurrent symlink for review.");
            Assert.That(File.ReadAllBytes(outsidePath), Is.EqualTo(originalContents));
        }

        [Test]
        public void AddService_DependencyDriftIsRolledBackBeforeSuccess()
        {
            const string packageName = "com.example.drifted-package";
            string cleanParent = CreateCleanParent("dependency-drift-parent");
            string source = CreateSourceRepository(
                "dependency-drift-source",
                packageName);
            File.WriteAllText(
                Path.Combine(source, "package.json"),
                "{\"name\":\"" + packageName + "\",\"version\":\"1.0.0\"," +
                "\"dependencies\":{\"com.example.child\":\"2.0.0\"}}\n");
            ExpectGit(source, "add -- package.json");
            ExpectGit(source, "commit -m \"Change dependency\"");
            RedirectProjectRoot(cleanParent);

            string expectedFingerprint =
                GitUtility.ComputePackageDependencyFingerprint(new[]
                {
                    new PackageManifestDependency(
                        "com.example.child",
                        "1.0.0")
                });
            string packagePath = "Packages/" + packageName;
            var state = new GitSubmoduleAddTaskState();

            CommandResult result = GitSubmoduleAddService.RunAddSubmoduleTask(
                source,
                string.Empty,
                packageName,
                "1.0.0",
                expectedFingerprint,
                packagePath,
                state,
                CancellationToken.None,
                inspectedCommit: ExpectGit(
                    source,
                    "rev-parse --verify HEAD^{commit}").StdOut.Trim());

            Assert.That(result.IsSuccess, Is.False);
            Assert.That(state.AddedSuccessfully, Is.False);
            Assert.That(
                state.Outcome,
                Is.EqualTo(GitOperationCompletionOutcome.FailedButRolledBack));
            Assert.That(state.Message, Does.Contain("dependencies changed"));
            Assert.That(
                Directory.Exists(Path.Combine(cleanParent, packagePath)),
                Is.False);
            Assert.That(
                Git(cleanParent, "diff --cached --name-only").StdOut.Trim(),
                Is.Empty,
                "A rejected manifest must not leave staged submodule state.");
            Assert.That(
                Git(cleanParent, "diff --name-only").StdOut.Trim(),
                Is.Empty,
                "A rejected manifest must restore tracked parent files.");
        }

        [Test]
        public void AddService_VerifiedPackageManifestMetaGuidDriftIsRolledBack()
        {
            const string packageName = "com.example.meta-drift";
            const string inspectedGuid =
                "0123456789abcdef0123456789abcdef";
            const string checkedOutGuid =
                "fedcba9876543210fedcba9876543210";
            string cleanParent = CreateCleanParent("meta-drift-parent");
            string source = CreateSourceRepository(
                "meta-drift-source",
                packageName);
            File.WriteAllText(
                Path.Combine(source, "package.json.meta"),
                "fileFormatVersion: 2\n" +
                "guid: " + checkedOutGuid + "\n" +
                "PackageManifestImporter:\n" +
                "  externalObjects: {}\n");
            ExpectGit(source, "add -- package.json.meta");
            ExpectGit(source, "commit -m \"Add package manifest meta\"");
            RedirectProjectRoot(cleanParent);

            string packagePath = "Packages/" + packageName;
            var state = new GitSubmoduleAddTaskState();
            CommandResult result = GitSubmoduleAddService.RunAddSubmoduleTask(
                source,
                string.Empty,
                packageName,
                "1.0.0",
                GitUtility.ComputePackageDependencyFingerprint(
                    Array.Empty<PackageManifestDependency>()),
                packagePath,
                state,
                CancellationToken.None,
                PackageManifestMetaVerification.Verified,
                inspectedGuid,
                ExpectGit(
                    source,
                    "rev-parse --verify HEAD^{commit}").StdOut.Trim());

            Assert.That(result.IsSuccess, Is.False);
            Assert.That(state.AddedSuccessfully, Is.False);
            Assert.That(
                state.Outcome,
                Is.EqualTo(GitOperationCompletionOutcome.FailedButRolledBack));
            Assert.That(state.Message, Does.Contain("package.json.meta changed"));
            Assert.That(
                Directory.Exists(Path.Combine(cleanParent, packagePath)),
                Is.False);
            Assert.That(
                Git(cleanParent, "diff --cached --name-only").StdOut.Trim(),
                Is.Empty);
            Assert.That(
                Git(cleanParent, "diff --name-only").StdOut.Trim(),
                Is.Empty);
        }

        [Test]
        public void AddService_VerifiedSymlinkedPackageManifestMetaIsRolledBack()
        {
            const string packageName = "com.example.meta-symlink";
            string cleanParent = CreateCleanParent("meta-symlink-parent");
            string source = CreateSourceRepository(
                "meta-symlink-source",
                packageName);
            string linkTarget = Path.Combine(source, "link-target.txt");
            File.WriteAllText(linkTarget, "package.json");
            string linkBlob = ExpectGit(
                source,
                "hash-object -w -- link-target.txt").StdOut.Trim();
            ExpectGit(
                source,
                "update-index --add --cacheinfo 120000," + linkBlob +
                ",package.json.meta");
            ExpectGit(source, "commit -m \"Add symlinked package manifest meta\"");
            RedirectProjectRoot(cleanParent);

            string packagePath = "Packages/" + packageName;
            var state = new GitSubmoduleAddTaskState();
            CommandResult result = GitSubmoduleAddService.RunAddSubmoduleTask(
                source,
                string.Empty,
                packageName,
                "1.0.0",
                GitUtility.ComputePackageDependencyFingerprint(
                    Array.Empty<PackageManifestDependency>()),
                packagePath,
                state,
                CancellationToken.None,
                PackageManifestMetaVerification.Verified,
                "0123456789abcdef0123456789abcdef",
                ExpectGit(
                    source,
                    "rev-parse --verify HEAD^{commit}").StdOut.Trim());

            Assert.That(result.IsSuccess, Is.False);
            Assert.That(state.AddedSuccessfully, Is.False);
            Assert.That(
                state.Outcome,
                Is.EqualTo(GitOperationCompletionOutcome.FailedButRolledBack));
            Assert.That(state.Message, Does.Contain("symbolic-link"));
            Assert.That(
                Directory.Exists(Path.Combine(cleanParent, packagePath)),
                Is.False);
            Assert.That(
                Git(cleanParent, "diff --cached --name-only").StdOut.Trim(),
                Is.Empty);
        }

        [Test]
        public void AddService_TreeModeCheckUsesImmutableInspectedCommitAcrossHeadSwap()
        {
            const string packageName = "com.example.tree-mode-race";
            string cleanParent = CreateCleanParent("tree-mode-race-parent");
            string source = CreateSourceRepository(
                "tree-mode-race-source",
                packageName);
            string regularCommit = ExpectGit(
                source,
                "rev-parse --verify HEAD^{commit}").StdOut.Trim();
            string manifestObjectId = ExpectGit(
                source,
                "hash-object -w -- package.json").StdOut.Trim();
            ExpectGit(
                source,
                "update-index --add --cacheinfo 120000," +
                manifestObjectId + ",package.json");
            ExpectGit(source, "commit -m \"Symlink package manifest\"");
            string inspectedCommit = ExpectGit(
                source,
                "rev-parse --verify HEAD^{commit}").StdOut.Trim();
            RedirectProjectRoot(cleanParent);

            string packagePath = "Packages/" + packageName;
            var swappingRunner = new AddTreeModeHeadSwapRunner(
                CliCommandRunner.CurrentRunner,
                cleanParent,
                packagePath,
                packageName,
                regularCommit,
                inspectedCommit);
            CliCommandRunner.CurrentRunner = swappingRunner;
            var state = new GitSubmoduleAddTaskState();

            CommandResult result = GitSubmoduleAddService.RunAddSubmoduleTask(
                source,
                string.Empty,
                packageName,
                "1.0.0",
                GitUtility.ComputePackageDependencyFingerprint(
                    Array.Empty<PackageManifestDependency>()),
                packagePath,
                state,
                CancellationToken.None,
                inspectedCommit: inspectedCommit);

            Assert.That(result.IsSuccess, Is.False);
            Assert.That(state.AddedSuccessfully, Is.False);
            Assert.That(
                state.Outcome,
                Is.EqualTo(GitOperationCompletionOutcome.FailedButRolledBack));
            Assert.That(
                swappingRunner.MaterializedRegularManifest,
                Is.True,
                "The fixture must expose the symlink blob as valid regular-looking worktree JSON under core.symlinks=false.");
            Assert.That(
                swappingRunner.SwappedHeadDuringTreeRead,
                Is.True,
                "The fixture must swap HEAD only while the tree-mode command runs.");
            Assert.That(
                swappingRunner.TreeCommandArguments,
                Does.Contain(inspectedCommit),
                "The tree-mode query must bind to the immutable inspected commit rather than mutable HEAD.");
            Assert.That(state.Message, Does.Contain("symbolic-link"));
            Assert.That(
                Directory.Exists(Path.Combine(cleanParent, packagePath)),
                Is.False);
            Assert.That(
                Git(cleanParent, "diff --cached --name-only").StdOut.Trim(),
                Is.Empty);
            Assert.That(
                Git(cleanParent, "diff --name-only").StdOut.Trim(),
                Is.Empty);
        }

        [Test]
        public void AddService_ExactInspectedCommitIsRequiredAndAccepted()
        {
            const string packageName = "com.example.commit-bound";
            string cleanParent = CreateCleanParent("commit-bound-parent");
            string source = CreateSourceRepository(
                "commit-bound-source",
                packageName);
            string inspectedCommit = ExpectGit(
                source,
                "rev-parse --verify HEAD^{commit}").StdOut.Trim();
            RedirectProjectRoot(cleanParent);

            string packagePath = "Packages/" + packageName;
            var state = new GitSubmoduleAddTaskState();
            CommandResult result = GitSubmoduleAddService.RunAddSubmoduleTask(
                source,
                string.Empty,
                packageName,
                "1.0.0",
                GitUtility.ComputePackageDependencyFingerprint(
                    Array.Empty<PackageManifestDependency>()),
                packagePath,
                state,
                CancellationToken.None,
                inspectedCommit: inspectedCommit);

            Assert.That(result.IsSuccess, Is.True, state.Message);
            Assert.That(state.AddedSuccessfully, Is.True);
            Assert.That(
                state.Outcome,
                Is.EqualTo(GitOperationCompletionOutcome.Succeeded));
            Assert.That(
                ExpectGit(
                    cleanParent,
                    GitUtility.BuildReadSubmoduleHeadCommitArguments(packagePath))
                    .StdOut.Trim(),
                Is.EqualTo(inspectedCommit).IgnoreCase);
        }

        [Test]
        public void AddService_CleanCommitSwapAfterInitialInspectionIsRejected()
        {
            const string packageName = "com.example.commit-race";
            string cleanParent = CreateCleanParent("commit-race-parent");
            string source = CreateSourceRepository(
                "commit-race-source",
                packageName);
            string inspectedCommit = ExpectGit(
                source,
                "rev-parse --verify HEAD^{commit}").StdOut.Trim();
            string defaultBranch = ExpectGit(
                source,
                "symbolic-ref --quiet --short HEAD").StdOut.Trim();
            ExpectGit(source, "checkout -b concurrent-clean-commit");
            ExpectGit(
                source,
                "commit --allow-empty -m \"Concurrent clean commit\"");
            string differentCommit = ExpectGit(
                source,
                "rev-parse --verify HEAD^{commit}").StdOut.Trim();
            ExpectGit(source, "checkout " + GitUtility.Quote(defaultBranch));
            RedirectProjectRoot(cleanParent);

            string packagePath = "Packages/" + packageName;
            string packageRoot = Path.Combine(cleanParent, packagePath);
            ICommandRunner inner = CliCommandRunner.CurrentRunner;
            CliCommandRunner.CurrentRunner = new SingleCommandMutationRunner(
                inner,
                spec => string.Equals(
                    spec.Arguments,
                    GitUtility.BuildReadSubmoduleHeadCommitArguments(packagePath),
                    StringComparison.Ordinal),
                (_, __) =>
                {
                    ExpectGit(
                        packageRoot,
                        "checkout --detach " + GitUtility.Quote(differentCommit));
                    ExpectGit(
                        cleanParent,
                        GitUtility.BuildStageSubmoduleArguments(packagePath));
                });
            var state = new GitSubmoduleAddTaskState();

            CommandResult result = GitSubmoduleAddService.RunAddSubmoduleTask(
                source,
                string.Empty,
                packageName,
                "1.0.0",
                GitUtility.ComputePackageDependencyFingerprint(
                    Array.Empty<PackageManifestDependency>()),
                packagePath,
                state,
                CancellationToken.None,
                inspectedCommit: inspectedCommit);

            Assert.That(result.IsSuccess, Is.False);
            Assert.That(state.AddedSuccessfully, Is.False);
            Assert.That(state.Message, Does.Contain("exact inspected Git commit"));
            Assert.That(
                state.Outcome,
                Is.Not.EqualTo(GitOperationCompletionOutcome.Succeeded));
        }

        [TestCase("")]
        [TestCase("0000000000000000000000000000000000000000")]
        [TestCase("not-a-commit")]
        public void AddService_InvalidInspectedCommitIsRejectedBeforeMutation(
            string inspectedCommit)
        {
            const string packageName = "com.example.invalid-commit";
            string cleanParent = CreateCleanParent("invalid-commit-parent");
            string source = CreateSourceRepository(
                "invalid-commit-source",
                packageName);
            RedirectProjectRoot(cleanParent);

            string packagePath = "Packages/" + packageName;
            var state = new GitSubmoduleAddTaskState();
            CommandResult result = GitSubmoduleAddService.RunAddSubmoduleTask(
                source,
                string.Empty,
                packageName,
                "1.0.0",
                GitUtility.ComputePackageDependencyFingerprint(
                    Array.Empty<PackageManifestDependency>()),
                packagePath,
                state,
                CancellationToken.None,
                inspectedCommit: inspectedCommit);

            Assert.That(result.IsSuccess, Is.False);
            Assert.That(state.AddedSuccessfully, Is.False);
            Assert.That(
                state.Outcome,
                Is.EqualTo(GitOperationCompletionOutcome.FailedButRolledBack));
            Assert.That(state.Message, Does.Contain("exact nonzero Git commit"));
            Assert.That(
                Directory.Exists(Path.Combine(cleanParent, packagePath)),
                Is.False);
            Assert.That(
                Git(cleanParent, "diff --cached --name-only").StdOut.Trim(),
                Is.Empty);
        }

        [Test]
        public void AddService_BranchMovementAfterInspectionFailsUnsafeAndPreservesState()
        {
            const string packageName = "com.example.moved-branch";
            string cleanParent = CreateCleanParent("moved-branch-parent");
            string source = CreateSourceRepository(
                "moved-branch-source",
                packageName);
            string inspectedCommit = ExpectGit(
                source,
                "rev-parse --verify HEAD^{commit}").StdOut.Trim();
            File.WriteAllText(
                Path.Combine(source, "after-inspection.txt"),
                "branch advanced\n");
            ExpectGit(source, "add -- after-inspection.txt");
            ExpectGit(source, "commit -m \"Advance after inspection\"");
            RedirectProjectRoot(cleanParent);

            string packagePath = "Packages/" + packageName;
            var state = new GitSubmoduleAddTaskState();
            CommandResult result = GitSubmoduleAddService.RunAddSubmoduleTask(
                source,
                string.Empty,
                packageName,
                "1.0.0",
                GitUtility.ComputePackageDependencyFingerprint(
                    Array.Empty<PackageManifestDependency>()),
                packagePath,
                state,
                CancellationToken.None,
                inspectedCommit: inspectedCommit);

            Assert.That(result.IsSuccess, Is.False);
            Assert.That(state.AddedSuccessfully, Is.False);
            Assert.That(
                state.Outcome,
                Is.EqualTo(GitOperationCompletionOutcome.FailedUnsafe));
            Assert.That(state.Message, Does.Contain("rollback evidence"));
            Assert.That(state.Message, Does.Contain("does not match"));
            Assert.That(
                Directory.Exists(Path.Combine(cleanParent, packagePath)),
                Is.True,
                "An unapproved checkout without exact cleanup ownership must be preserved for recovery.");
            Assert.That(
                Git(cleanParent, "diff --cached --name-only").StdOut.Trim(),
                Does.Contain(packagePath));
        }

        [Test]
        public void AddService_UnconfirmedCommitInspectionSkipsCleanupAndFailsUnsafe()
        {
            const string packageName = "com.example.unconfirmed-commit";
            string cleanParent = CreateCleanParent("unconfirmed-commit-parent");
            string source = CreateSourceRepository(
                "unconfirmed-commit-source",
                packageName);
            string inspectedCommit = ExpectGit(
                source,
                "rev-parse --verify HEAD^{commit}").StdOut.Trim();
            RedirectProjectRoot(cleanParent);

            string packagePath = "Packages/" + packageName;
            ICommandRunner inner = CliCommandRunner.CurrentRunner;
            CliCommandRunner.CurrentRunner = new SingleCommandMutationRunner(
                inner,
                spec => string.Equals(
                    spec.Arguments,
                    GitUtility.BuildReadSubmoduleHeadCommitArguments(packagePath),
                    StringComparison.Ordinal),
                (_, result) => result.TerminationConfirmed = false);
            var state = new GitSubmoduleAddTaskState();

            try
            {
                CommandResult result = GitSubmoduleAddService.RunAddSubmoduleTask(
                    source,
                    string.Empty,
                    packageName,
                    "1.0.0",
                    GitUtility.ComputePackageDependencyFingerprint(
                        Array.Empty<PackageManifestDependency>()),
                    packagePath,
                    state,
                    CancellationToken.None,
                    inspectedCommit: inspectedCommit);

                Assert.That(result.TerminationConfirmed, Is.False);
                Assert.That(state.AddedSuccessfully, Is.False);
                Assert.That(
                    state.Outcome,
                    Is.EqualTo(GitOperationCompletionOutcome.FailedUnsafe));
                Assert.That(state.Message, Does.Contain("cleanup was skipped"));
                Assert.That(
                    Directory.Exists(Path.Combine(cleanParent, packagePath)),
                    Is.True,
                    "Unconfirmed process termination must never authorize cleanup.");
            }
            finally
            {
                GitUtility.ResetCommandSafetyState();
            }
        }

        [Test]
        public void AddService_TruncatedCommitDiagnosticsAreRolledBack()
        {
            const string packageName = "com.example.truncated-commit";
            string cleanParent = CreateCleanParent("truncated-commit-parent");
            string source = CreateSourceRepository(
                "truncated-commit-source",
                packageName);
            string inspectedCommit = ExpectGit(
                source,
                "rev-parse --verify HEAD^{commit}").StdOut.Trim();
            RedirectProjectRoot(cleanParent);

            string packagePath = "Packages/" + packageName;
            ICommandRunner inner = CliCommandRunner.CurrentRunner;
            CliCommandRunner.CurrentRunner = new SingleCommandMutationRunner(
                inner,
                spec => string.Equals(
                    spec.Arguments,
                    GitUtility.BuildReadSubmoduleHeadCommitArguments(packagePath),
                    StringComparison.Ordinal),
                (_, result) => result.StdErrTruncated = true);
            var state = new GitSubmoduleAddTaskState();

            CommandResult result = GitSubmoduleAddService.RunAddSubmoduleTask(
                source,
                string.Empty,
                packageName,
                "1.0.0",
                GitUtility.ComputePackageDependencyFingerprint(
                    Array.Empty<PackageManifestDependency>()),
                packagePath,
                state,
                CancellationToken.None,
                inspectedCommit: inspectedCommit);

            Assert.That(result.IsSuccess, Is.False);
            Assert.That(state.AddedSuccessfully, Is.False);
            Assert.That(
                state.Outcome,
                Is.EqualTo(GitOperationCompletionOutcome.FailedButRolledBack));
            Assert.That(state.Message, Does.Contain("diagnostics were truncated"));
            Assert.That(state.Message, Does.Contain("rolled back"));
            Assert.That(
                Directory.Exists(Path.Combine(cleanParent, packagePath)),
                Is.False);
            Assert.That(
                Git(cleanParent, "diff --cached --name-only").StdOut.Trim(),
                Is.Empty);
        }

        [Test]
        public void GetSubmodules_ReadsPackageNameFromManifest()
        {
            bool loaded = GitUtility.TryGetSubmodules(out List<GitPackageInfo> packages, out string error);
            GitPackageInfo package = packages.Find(candidate => candidate.Path == PackagePath);

            Assert.That(loaded, Is.True, error);
            Assert.That(package, Is.Not.Null);
            Assert.That(package.PackageName, Is.EqualTo("com.example.integration-package"));
            Assert.That(
                GitUtility.IsValidGitObjectId(package.ResolvedCommit),
                Is.True);
            Assert.That(
                package.ResolvedCommit,
                Is.EqualTo(
                    ExpectGit(
                        parentRoot,
                        $"-C {GitUtility.Quote(PackagePath)} rev-parse --verify HEAD^{{commit}}")
                    .StdOut.Trim())
                .IgnoreCase);
        }

        [Test]
        public void Remove_RefusesTrackedChangesAndPreservesThem()
        {
            string packageJson = Path.Combine(parentRoot, PackagePath, "package.json");
            const string localChange = "\n// local change that must survive\n";
            File.AppendAllText(packageJson, localChange);

            Assert.That(
                GitUtility.TryAssessSubmoduleRemoval(PackagePath, out SubmoduleRemovalAssessment assessment, out string assessmentError),
                Is.True,
                assessmentError);
            Assert.That(assessment.HasWorkingTreeChanges, Is.True);
            Assert.That(assessment.IsSafe, Is.False);

            bool removed = GitUtility.TryRemoveSubmodule(
                PackagePath,
                out string error,
                out GitOperationCompletionOutcome outcome);

            Assert.That(removed, Is.False, "A dirty submodule must never be removed implicitly.");
            Assert.That(outcome, Is.EqualTo(GitOperationCompletionOutcome.FailedButRolledBack));
            Assert.That(error, Is.Not.Empty);
            Assert.That(File.ReadAllText(packageJson), Does.Contain(localChange.Trim()));
            Assert.That(Directory.Exists(Path.Combine(parentRoot, PackagePath)), Is.True);
        }

        [Test]
        public void Remove_RefusesUntrackedFilesAndPreservesThem()
        {
            string untrackedFile = Path.Combine(parentRoot, PackagePath, "local-notes.txt");
            File.WriteAllText(untrackedFile, "not committed and not disposable\n");

            Assert.That(
                GitUtility.TryAssessSubmoduleRemoval(PackagePath, out SubmoduleRemovalAssessment assessment, out string assessmentError),
                Is.True,
                assessmentError);
            Assert.That(assessment.HasWorkingTreeChanges, Is.True);
            Assert.That(assessment.IsSafe, Is.False);

            bool removed = GitUtility.TryRemoveSubmodule(PackagePath, out string error);

            Assert.That(removed, Is.False, "An untracked file must never be removed implicitly.");
            Assert.That(error, Is.Not.Empty);
            Assert.That(File.ReadAllText(untrackedFile), Is.EqualTo("not committed and not disposable\n"));
        }

        [Test]
        public void Remove_RejectsOversizedGitmodulesBeforeMutation()
        {
            string gitModulesPath = Path.Combine(parentRoot, ".gitmodules");
            byte[] originalContents = File.ReadAllBytes(gitModulesPath);
            byte[] oversizedContents = Encoding.UTF8.GetBytes(
                "# " + new string('x', (128 * 1024) + 1) + "\n" +
                Encoding.UTF8.GetString(originalContents));
            File.WriteAllBytes(gitModulesPath, oversizedContents);
            ExpectGit(parentRoot, "add -- .gitmodules");
            ExpectGit(parentRoot, "commit -m \"Commit oversized gitmodules fixture\"");
            string gitlinkBefore = ExpectGit(
                    parentRoot,
                    "rev-parse :" + PackagePath)
                .StdOut.Trim();

            bool removed = GitUtility.TryRemoveSubmodule(
                PackagePath,
                out string error,
                out GitOperationCompletionOutcome outcome);

            Assert.That(removed, Is.False);
            Assert.That(
                outcome,
                Is.EqualTo(GitOperationCompletionOutcome.FailedButRolledBack));
            Assert.That(error, Does.Contain("128 KiB safety limit"));
            Assert.That(File.ReadAllBytes(gitModulesPath), Is.EqualTo(oversizedContents));
            Assert.That(
                ExpectGit(parentRoot, "rev-parse :" + PackagePath).StdOut.Trim(),
                Is.EqualTo(gitlinkBefore));
            Assert.That(File.Exists(Path.Combine(parentRoot, PackagePath, "package.json")), Is.True);
        }

        [Test]
        public void Remove_RefusesIgnoredFilesAndPreservesThem()
        {
            Assert.That(
                GitUtility.TryResolveSubmoduleGitDir(PackagePath, out string gitDir, out string resolveError),
                Is.True,
                resolveError);
            string infoDirectory = Path.Combine(gitDir, "info");
            Directory.CreateDirectory(infoDirectory);
            File.AppendAllText(Path.Combine(infoDirectory, "exclude"), "\nlocal-cache.bin\n");
            string ignoredFile = Path.Combine(parentRoot, PackagePath, "local-cache.bin");
            File.WriteAllText(ignoredFile, "ignored but not disposable\n");

            Assert.That(
                GitUtility.TryAssessSubmoduleRemoval(PackagePath, out SubmoduleRemovalAssessment assessment, out string assessmentError),
                Is.True,
                assessmentError);
            Assert.That(assessment.HasWorkingTreeChanges, Is.True);
            Assert.That(assessment.WorktreeStatus, Does.Contain("local-cache.bin"));
            Assert.That(assessment.IsSafe, Is.False);

            bool removed = GitUtility.TryRemoveSubmodule(PackagePath, out string error);

            Assert.That(removed, Is.False, "An ignored file must never be removed implicitly.");
            Assert.That(error, Is.Not.Empty);
            Assert.That(File.ReadAllText(ignoredFile), Is.EqualTo("ignored but not disposable\n"));
        }

        [Test]
        public void Remove_RefusesLocalOnlyCommitAndPreservesHead()
        {
            string packageRoot = Path.Combine(parentRoot, PackagePath);
            ExpectGit(packageRoot, "checkout --detach");
            File.AppendAllText(Path.Combine(packageRoot, "package.json"), "\n");
            ExpectGit(packageRoot, "add -- package.json");
            ExpectGit(packageRoot, "commit -m \"Local-only package commit\"");
            string localHead = ExpectGit(packageRoot, "rev-parse HEAD").StdOut.Trim();
            ExpectGit(parentRoot, "add -- \"" + PackagePath + "\"");
            ExpectGit(parentRoot, "commit -m \"Pin local-only fixture commit\"");

            Assert.That(
                GitUtility.TryAssessSubmoduleRemoval(PackagePath, out SubmoduleRemovalAssessment assessment, out string assessmentError),
                Is.True,
                assessmentError);
            Assert.That(assessment.HasLocalOnlyCommits, Is.True);
            Assert.That(assessment.LocalOnlyCommitCount, Is.GreaterThanOrEqualTo(1));
            Assert.That(assessment.IsSafe, Is.False);

            bool removed = GitUtility.TryRemoveSubmodule(PackagePath, out string error);

            Assert.That(removed, Is.False, "A local-only commit must never be removed implicitly.");
            Assert.That(error, Is.Not.Empty);
            Assert.That(ExpectGit(packageRoot, "rev-parse HEAD").StdOut.Trim(), Is.EqualTo(localHead));
            Assert.That(Directory.Exists(packageRoot), Is.True);
        }

        [Test]
        public void Remove_UnpublishedCommitIsBlockedDespiteLocalRemoteTrackingRef()
        {
            string packageRoot = Path.Combine(parentRoot, PackagePath);
            File.AppendAllText(Path.Combine(packageRoot, "package.json"), "\n");
            ExpectGit(packageRoot, "add -- package.json");
            ExpectGit(packageRoot, "commit -m \"Unpublished package commit\"");
            string unpublishedHead = ExpectGit(
                packageRoot,
                "rev-parse HEAD").StdOut.Trim();
            ExpectGit(
                packageRoot,
                "update-ref refs/remotes/origin/fake HEAD");
            ExpectGit(parentRoot, "add -- \"" + PackagePath + "\"");
            ExpectGit(parentRoot, "commit -m \"Pin unpublished package commit\"");

            Assert.That(
                GitUtility.TryAssessSubmoduleRemoval(
                    PackagePath,
                    out SubmoduleRemovalAssessment assessment,
                    out string assessmentError),
                Is.True,
                assessmentError);
            Assert.That(
                assessment.HasLocalOnlyCommits,
                Is.False,
                "The fixture must demonstrate that a local remote-tracking ref can false-allow.");
            Assert.That(assessment.IsSafe, Is.True, assessment.BuildWarning());

            bool removed = GitUtility.TryRemoveSubmodule(
                PackagePath,
                out string error,
                out GitOperationCompletionOutcome outcome);

            Assert.That(removed, Is.False);
            Assert.That(
                outcome,
                Is.EqualTo(GitOperationCompletionOutcome.FailedButRolledBack));
            Assert.That(error, Does.Contain("remote-tracking refs do not prove"));
            Assert.That(error, Does.Contain("could not be proven reachable"));
            Assert.That(
                ExpectGit(packageRoot, "rev-parse HEAD").StdOut.Trim(),
                Is.EqualTo(unpublishedHead));
            Assert.That(Directory.Exists(packageRoot), Is.True);
            Assert.That(
                Git(parentRoot, "ls-files --error-unmatch -- \"" + PackagePath + "\"").IsSuccess,
                Is.True);
            Assert.That(
                File.ReadAllText(Path.Combine(parentRoot, ".gitmodules")),
                Does.Contain(PackagePath));
        }

        [Test]
        public void Remove_WorktreeChangeAfterPublicationQueryIsPreservedAndBlocksRemoval()
        {
            string packageRoot = Path.Combine(parentRoot, PackagePath);
            string lateFile = Path.Combine(
                packageRoot,
                "created-after-publication-query.txt");
            ICommandRunner inner = CliCommandRunner.CurrentRunner;
            CliCommandRunner.CurrentRunner = new SingleCommandMutationRunner(
                inner,
                spec => (spec.Arguments ?? string.Empty).IndexOf(
                    "ls-remote --heads --tags ",
                    StringComparison.Ordinal) >= 0,
                (_, __) => File.WriteAllText(
                    lateFile,
                    "work created while the remote query was active\n"));

            bool removed = GitUtility.TryRemoveSubmodule(
                PackagePath,
                out string error,
                out GitOperationCompletionOutcome outcome);

            Assert.That(removed, Is.False);
            Assert.That(
                outcome,
                Is.EqualTo(GitOperationCompletionOutcome.FailedButRolledBack));
            Assert.That(
                error,
                Does.Contain("changed during remote publication verification"));
            Assert.That(
                File.ReadAllText(lateFile),
                Is.EqualTo("work created while the remote query was active\n"));
            Assert.That(Directory.Exists(packageRoot), Is.True);
            Assert.That(
                Git(parentRoot, "ls-files --error-unmatch -- \"" + PackagePath + "\"").IsSuccess,
                Is.True);
            Assert.That(
                File.ReadAllText(Path.Combine(parentRoot, ".gitmodules")),
                Does.Contain(PackagePath));
        }

        [Test]
        public void Remove_ExplicitDiscardCanRemoveDirtyTemporaryFixture()
        {
            string packageJson = Path.Combine(parentRoot, PackagePath, "package.json");
            File.AppendAllText(packageJson, "\n// deliberately discarded by this test\n");

            Assert.That(
                GitUtility.TryAssessSubmoduleRemoval(
                    PackagePath,
                    out SubmoduleRemovalAssessment assessment,
                    out string assessmentError),
                Is.True,
                assessmentError);

            bool removed = GitUtility.TryRemoveSubmodule(
                PackagePath,
                assessment,
                true,
                out string error);

            Assert.That(removed, Is.True, error);
            Assert.That(Directory.Exists(Path.Combine(parentRoot, PackagePath)), Is.False);
        }

        [Test]
        public void Remove_LegacyDiscardFlagWithoutExactAssessmentRemainsSafe()
        {
            string packageJson = Path.Combine(parentRoot, PackagePath, "package.json");
            const string localChange = "\n// legacy overload must preserve this\n";
            File.AppendAllText(packageJson, localChange);

            bool removed = GitUtility.TryRemoveSubmodule(
                PackagePath,
                true,
                out string error);

            Assert.That(removed, Is.False);
            Assert.That(error, Does.Contain("blocked to protect your work"));
            Assert.That(File.ReadAllText(packageJson), Does.Contain(localChange.Trim()));
        }

        [Test]
        public void RemoveService_AssessmentTaskCapturesDirtyWarningState()
        {
            string packageJson = Path.Combine(parentRoot, PackagePath, "package.json");
            File.AppendAllText(packageJson, "\n// service assessment fixture\n");
            var state = new GitSubmoduleRemovalAssessmentTaskState();

            CommandResult result = GitSubmoduleRemoveService.RunAssessmentTask(
                PackagePath,
                state,
                CancellationToken.None);

            Assert.That(result.IsSuccess, Is.True, result.StdErr);
            Assert.That(state.Outcome, Is.EqualTo(GitOperationCompletionOutcome.Succeeded));
            Assert.That(state.Assessment, Is.Not.Null);
            Assert.That(state.Assessment.IsSafe, Is.False);
            Assert.That(state.Assessment.BuildWarning(), Does.Contain("modified, untracked, or ignored"));
        }

        [Test]
        public void Remove_ExplicitDiscardRefusesStateThatChangedAfterConfirmation()
        {
            string packageRoot = Path.Combine(parentRoot, PackagePath);
            string packageJson = Path.Combine(packageRoot, "package.json");
            File.AppendAllText(packageJson, "\n// change shown in warning\n");
            Assert.That(
                GitUtility.TryAssessSubmoduleRemoval(
                    PackagePath,
                    out SubmoduleRemovalAssessment confirmed,
                    out string assessmentError),
                Is.True,
                assessmentError);
            string lateFile = Path.Combine(packageRoot, "created-after-confirmation.txt");
            File.WriteAllText(lateFile, "new work\n");

            bool removed = GitUtility.TryRemoveSubmodule(
                PackagePath,
                confirmed,
                true,
                out string error);

            Assert.That(removed, Is.False);
            Assert.That(error, Does.Contain("changed after the removal warning"));
            Assert.That(File.Exists(packageJson), Is.True);
            Assert.That(File.ReadAllText(lateFile), Is.EqualTo("new work\n"));
        }

        [Test]
        public void Remove_CleanSubmoduleRemovesGitlinkAndWorktree()
        {
            Assert.That(
                GitUtility.TryAssessSubmoduleRemoval(PackagePath, out SubmoduleRemovalAssessment assessment, out string assessmentError),
                Is.True,
                assessmentError);
            Assert.That(assessment.IsSafe, Is.True, assessment.BuildWarning());

            bool removed = GitUtility.TryRemoveSubmodule(
                PackagePath,
                out string error,
                out GitOperationCompletionOutcome outcome);

            Assert.That(removed, Is.True, error);
            Assert.That(outcome, Is.EqualTo(GitOperationCompletionOutcome.Succeeded));
            Assert.That(Directory.Exists(Path.Combine(parentRoot, PackagePath)), Is.False);
            Assert.That(
                Git(parentRoot, "ls-files --error-unmatch -- \"" + PackagePath + "\"").IsSuccess,
                Is.False);
            Assert.That(File.ReadAllText(Path.Combine(parentRoot, ".gitmodules")), Does.Not.Contain(PackagePath));
        }

        [Test]
        public void Remove_AddThenRemoveBeforeCommitRestoresCleanParent()
        {
            string cleanParent = CreateCleanParent("add-then-remove-parent");
            RedirectProjectRoot(cleanParent);
            Assert.That(
                GitUtility.TryAddSubmodule(sourceRoot, PackagePath, string.Empty, out string addError),
                Is.True,
                addError);
            Assert.That(
                Git(cleanParent, "status --porcelain=v2 --untracked-files=all -- .gitmodules \"" + PackagePath + "\"").StdOut,
                Is.Not.Empty);

            Assert.That(
                GitUtility.TryAssessSubmoduleRemoval(
                    PackagePath,
                    out SubmoduleRemovalAssessment assessment,
                    out string assessmentError),
                Is.True,
                assessmentError);
            Assert.That(assessment.HasParentChanges, Is.True);
            Assert.That(assessment.IsSafe, Is.False);
            Assert.That(assessment.BuildWarning(), Does.Contain("uncommitted or staged"));

            bool removed = GitUtility.TryRemoveSubmodule(
                PackagePath,
                assessment,
                true,
                out string error);

            Assert.That(removed, Is.True, error);
            Assert.That(Directory.Exists(Path.Combine(cleanParent, PackagePath)), Is.False);
            Assert.That(File.Exists(Path.Combine(cleanParent, ".gitmodules")), Is.False);
            Assert.That(
                Git(cleanParent, "status --porcelain=v2 --untracked-files=all -- .gitmodules \"" + PackagePath + "\"").StdOut,
                Is.Empty);
        }

        [Test]
        public void Remove_RepairsStagedAddAfterWorktreeWasDeleted()
        {
            string cleanParent = CreateCleanParent("missing-staged-add-parent");
            RedirectProjectRoot(cleanParent);
            Assert.That(
                GitUtility.TryAddSubmodule(sourceRoot, PackagePath, string.Empty, out string addError),
                Is.True,
                addError);
            Directory.Delete(Path.Combine(cleanParent, PackagePath), true);
            string statusBefore = Git(
                cleanParent,
                "status --porcelain=v2 --untracked-files=all -- .gitmodules \"" + PackagePath + "\"").StdOut;
            Assert.That(statusBefore, Does.Contain("1 AD"));

            Assert.That(
                GitUtility.TryAssessSubmoduleRemoval(
                    PackagePath,
                    out SubmoduleRemovalAssessment assessment,
                    out string assessmentError),
                Is.True,
                assessmentError);
            Assert.That(assessment.IsSafe, Is.False);

            bool removed = GitUtility.TryRemoveSubmodule(
                PackagePath,
                assessment,
                true,
                out string error);

            Assert.That(removed, Is.True, error);
            Assert.That(File.Exists(Path.Combine(cleanParent, ".gitmodules")), Is.False);
            Assert.That(
                Git(cleanParent, "status --porcelain=v2 --untracked-files=all -- .gitmodules \"" + PackagePath + "\"").StdOut,
                Is.Empty);
        }

        [Test]
        public void Remove_WithStagedOnlyUnrelatedGitmodulesChange_PreservesIt()
        {
            string gitmodulesPath = Path.Combine(parentRoot, ".gitmodules");
            const string marker = "# staged-unrelated-marker\n";
            File.AppendAllText(gitmodulesPath, marker);
            ExpectGit(parentRoot, "add -- .gitmodules");

            bool removed = GitUtility.TryRemoveSubmodule(PackagePath, out string error);

            Assert.That(removed, Is.True, error);
            Assert.That(File.ReadAllText(gitmodulesPath), Does.Contain(marker.Trim()));
            string stagedGitmodules = ExpectGit(parentRoot, "show :.gitmodules").StdOut;
            Assert.That(stagedGitmodules, Does.Contain(marker.Trim()));
            Assert.That(stagedGitmodules, Does.Not.Contain(PackagePath));
            Assert.That(
                Git(parentRoot, "ls-files --error-unmatch -- \"" + PackagePath + "\"").IsSuccess,
                Is.False);
        }

        [Test]
        public void Remove_AssessmentMarksStagedTargetGitmodulesUrlAndBranchEditsDirty()
        {
            string replacementUrl = sourceRoot + "-replacement";
            ExpectGit(
                parentRoot,
                "config --file .gitmodules " +
                "submodule.\"" + PackagePath + "\".url " +
                GitUtility.Quote(replacementUrl));
            ExpectGit(
                parentRoot,
                "config --file .gitmodules " +
                "submodule.\"" + PackagePath + "\".branch main");
            ExpectGit(parentRoot, "add -- .gitmodules");

            bool assessed = GitUtility.TryAssessSubmoduleRemoval(
                PackagePath,
                out SubmoduleRemovalAssessment assessment,
                out string error);

            Assert.That(assessed, Is.True, error);
            Assert.That(assessment.SubmoduleName, Is.EqualTo(PackagePath));
            Assert.That(assessment.RepositoryUrl, Is.EqualTo(replacementUrl));
            Assert.That(assessment.ResolvedRepositoryUrl, Is.EqualTo(sourceRoot));
            Assert.That(assessment.GitModulesTargetFingerprint, Is.Not.Empty);
            Assert.That(assessment.GitModulesTargetStatus, Does.Contain("index:"));
            Assert.That(assessment.HasGitModulesTargetChanges, Is.True);
            Assert.That(assessment.HasParentChanges, Is.True);
            Assert.That(assessment.HasOnlyParentGitlinkChanges, Is.False);
            Assert.That(assessment.IsSafe, Is.False);
            Assert.That(assessment.BuildWarning(), Does.Contain(".gitmodules registration"));
        }

        [Test]
        public void Remove_ExplicitDiscardRefusesTargetGitmodulesEditAfterConfirmation()
        {
            ExpectGit(
                parentRoot,
                "config --file .gitmodules " +
                "submodule.\"" + PackagePath + "\".branch main");
            ExpectGit(parentRoot, "add -- .gitmodules");
            Assert.That(
                GitUtility.TryAssessSubmoduleRemoval(
                    PackagePath,
                    out SubmoduleRemovalAssessment confirmed,
                    out string assessmentError),
                Is.True,
                assessmentError);
            string confirmedFingerprint = confirmed.GitModulesTargetFingerprint;

            ExpectGit(
                parentRoot,
                "config --file .gitmodules " +
                "submodule.\"" + PackagePath + "\".branch release");
            ExpectGit(parentRoot, "add -- .gitmodules");

            bool removed = GitUtility.TryRemoveSubmodule(
                PackagePath,
                confirmed,
                true,
                out string error);

            Assert.That(removed, Is.False);
            Assert.That(error, Does.Contain("changed after the removal warning"));
            Assert.That(
                ExpectGit(
                    parentRoot,
                    "config --file .gitmodules --get " +
                    "submodule.\"" + PackagePath + "\".branch").StdOut.Trim(),
                Is.EqualTo("release"));
            Assert.That(
                GitUtility.TryAssessSubmoduleRemoval(
                    PackagePath,
                    out SubmoduleRemovalAssessment current,
                    out string currentError),
                Is.True,
                currentError);
            Assert.That(
                current.GitModulesTargetFingerprint,
                Is.Not.EqualTo(confirmedFingerprint));
            Assert.That(File.Exists(Path.Combine(parentRoot, PackagePath, "package.json")), Is.True);
        }

        [Test]
        public void Remove_AssessmentRejectsInitializedOriginThatDiffersFromResolvedParentUrl()
        {
            ExpectGit(
                Path.Combine(parentRoot, PackagePath),
                "remote set-url origin " + GitUtility.Quote(sourceRoot + "-different"));

            bool assessed = GitUtility.TryAssessSubmoduleRemoval(
                PackagePath,
                out _,
                out string error);

            Assert.That(assessed, Is.False);
            Assert.That(error, Does.Contain("origin URL does not match"));
            Assert.That(File.Exists(Path.Combine(parentRoot, PackagePath, "package.json")), Is.True);
        }

        [Test]
        public void Remove_TargetFirstBomCrLfGitmodules_PreservesPrefixCommentsAndOtherSectionBytes()
        {
            string gitmodulesPath = Path.Combine(parentRoot, ".gitmodules");
            string targetSection = File.ReadAllText(gitmodulesPath)
                .Replace("\r\n", "\n")
                .Replace("\r", "\n")
                .Replace("\n", "\r\n");
            const string preservedSuffix =
                "# preserved comment\r\n" +
                "[submodule \"Packages/com.example.other-package\"]\r\n" +
                "\tpath = Packages/com.example.other-package\r\n" +
                "\turl = https://example.invalid/other-package.git\r\n";
            byte[] source = EncodeUtf8WithBom(targetSection + preservedSuffix);
            Assert.That(source[0], Is.EqualTo(0xef));
            Assert.That(source[1], Is.EqualTo(0xbb));
            Assert.That(source[2], Is.EqualTo(0xbf));
            File.WriteAllBytes(gitmodulesPath, source);
            ExpectGit(parentRoot, "add -- .gitmodules");
            byte[] expected = EncodeUtf8WithBom(preservedSuffix);

            bool removed = GitUtility.TryRemoveSubmodule(PackagePath, out string error);

            Assert.That(removed, Is.True, error);
            CollectionAssert.AreEqual(expected, File.ReadAllBytes(gitmodulesPath));
            Assert.That(Git(parentRoot, "diff --quiet -- .gitmodules").IsSuccess, Is.True);
        }

        [Test]
        public void Remove_AssessmentReadsCommittedUtf8BomGitmodulesWithoutFinalNewline()
        {
            string gitmodulesPath = Path.Combine(parentRoot, ".gitmodules");
            string targetSection = File.ReadAllText(gitmodulesPath)
                .Replace("\r\n", "\n")
                .Replace("\r", "\n")
                .TrimEnd('\n');
            byte[] committedContents = EncodeUtf8WithBom(
                "# committed BOM fixture\n" + targetSection);
            File.WriteAllBytes(gitmodulesPath, committedContents);
            ExpectGit(parentRoot, "add -- .gitmodules");
            ExpectGit(parentRoot, "commit -m \"Commit BOM gitmodules fixture\"");

            bool assessed = GitUtility.TryAssessSubmoduleRemoval(
                PackagePath,
                out SubmoduleRemovalAssessment assessment,
                out string error);

            Assert.That(assessed, Is.True, error);
            Assert.That(assessment.RepositoryUrl, Is.EqualTo(sourceRoot));
            Assert.That(assessment.HasGitModulesTargetChanges, Is.False);
            Assert.That(assessment.IsSafe, Is.True);
        }

        [Test]
        public void Remove_MissingWorktreeRefusesNonGitlinkIndexEntry()
        {
            string cleanParent = CreateCleanParent("missing-non-gitlink-parent");
            string packageEntry = Path.Combine(cleanParent, PackagePath);
            Directory.CreateDirectory(Path.GetDirectoryName(packageEntry));
            File.WriteAllText(packageEntry, "ordinary tracked file\n");
            File.WriteAllText(
                Path.Combine(cleanParent, ".gitmodules"),
                "[submodule \"" + PackagePath + "\"]\n" +
                "\tpath = " + PackagePath + "\n" +
                "\turl = " + sourceRoot + "\n");
            ExpectGit(cleanParent, "add -- .gitmodules \"" + PackagePath + "\"");
            File.Delete(packageEntry);
            RedirectProjectRoot(cleanParent);
            string indexBefore = ExpectGit(cleanParent, "ls-files --stage -- \"" + PackagePath + "\"").StdOut;

            bool removed = GitUtility.TryRemoveSubmodule(PackagePath, out string error);

            Assert.That(removed, Is.False);
            Assert.That(error, Does.Contain("valid submodule gitlink"));
            Assert.That(
                ExpectGit(cleanParent, "ls-files --stage -- \"" + PackagePath + "\"").StdOut,
                Is.EqualTo(indexBefore));
            Assert.That(File.ReadAllText(Path.Combine(cleanParent, ".gitmodules")), Does.Contain(PackagePath));
        }

        [Test]
        public void Remove_MissingWorktreeRefusesAmbiguousGitmodulesRegistration()
        {
            string cleanParent = CreateCleanParent("missing-ambiguous-registration-parent");
            RedirectProjectRoot(cleanParent);
            Assert.That(
                GitUtility.TryAddSubmodule(sourceRoot, PackagePath, string.Empty, out string addError),
                Is.True,
                addError);
            Directory.Delete(Path.Combine(cleanParent, PackagePath), true);
            File.AppendAllText(
                Path.Combine(cleanParent, ".gitmodules"),
                "\n[submodule \"ambiguous-package\"]\n" +
                "\tpath = " + PackagePath + "\n" +
                "\turl = " + sourceRoot + "\n");
            ExpectGit(cleanParent, "add -- .gitmodules");
            string gitmodulesBefore = File.ReadAllText(Path.Combine(cleanParent, ".gitmodules"));

            bool removed = GitUtility.TryRemoveSubmodule(PackagePath, out string error);

            Assert.That(removed, Is.False);
            Assert.That(error, Does.Contain("more than one submodule"));
            Assert.That(File.ReadAllText(Path.Combine(cleanParent, ".gitmodules")), Is.EqualTo(gitmodulesBefore));
            Assert.That(
                Git(cleanParent, "ls-files --stage -- \"" + PackagePath + "\"").StdOut,
                Does.StartWith("160000 "));
        }

        [Test]
        public void Remove_WithStagedAndUnstagedGitmodulesChanges_PreservesTheirSplit()
        {
            string gitmodulesPath = Path.Combine(parentRoot, ".gitmodules");
            File.AppendAllText(gitmodulesPath, "# staged-marker\n");
            ExpectGit(parentRoot, "add -- .gitmodules");
            File.AppendAllText(gitmodulesPath, "# unstaged-marker\n");
            string indexBefore = ExpectGit(parentRoot, "show :.gitmodules").StdOut;
            string worktreeBefore = File.ReadAllText(gitmodulesPath);

            bool removed = GitUtility.TryRemoveSubmodule(PackagePath, out string error);

            Assert.That(removed, Is.False, "Unrelated staged/unstaged .gitmodules edits must block removal.");
            Assert.That(error, Is.Not.Empty);
            Assert.That(ExpectGit(parentRoot, "show :.gitmodules").StdOut, Is.EqualTo(indexBefore));
            Assert.That(File.ReadAllText(gitmodulesPath), Is.EqualTo(worktreeBefore));
            Assert.That(File.Exists(Path.Combine(parentRoot, PackagePath, "package.json")), Is.True);
        }

        [Test]
        public void Initialize_ChecksOutParentPinnedCommitInsteadOfLatestRemoteCommit()
        {
            string pinnedCommit = ExpectGit(Path.Combine(parentRoot, PackagePath), "rev-parse HEAD").StdOut.Trim();
            File.AppendAllText(Path.Combine(sourceRoot, "package.json"), "\n");
            ExpectGit(sourceRoot, "add -- package.json");
            ExpectGit(sourceRoot, "commit -m \"New remote package commit\"");
            string remoteCommit = ExpectGit(sourceRoot, "rev-parse HEAD").StdOut.Trim();
            Assert.That(remoteCommit, Is.Not.EqualTo(pinnedCommit));

            ExpectGit(parentRoot, "submodule deinit -f -- \"" + PackagePath + "\"");
            CommandResult initialize = Git(
                parentRoot,
                "-c protocol.file.allow=always " + GitUtility.BuildInitializeSubmoduleArguments(PackagePath));

            Assert.That(initialize.IsSuccess, Is.True, initialize.StdErr);
            string initializedHead = ExpectGit(Path.Combine(parentRoot, PackagePath), "rev-parse HEAD").StdOut.Trim();
            Assert.That(initializedHead, Is.EqualTo(pinnedCommit));
        }

        [Test]
        public void Update_PreflightRefusesCleanDetachedLocalOnlyCommit()
        {
            string packageRoot = Path.Combine(parentRoot, PackagePath);
            ExpectGit(packageRoot, "checkout --detach");
            File.AppendAllText(Path.Combine(packageRoot, "package.json"), "\n");
            ExpectGit(packageRoot, "add -- package.json");
            ExpectGit(packageRoot, "commit -m \"Detached local-only commit\"");
            string localHead = ExpectGit(packageRoot, "rev-parse HEAD").StdOut.Trim();
            ExpectGit(parentRoot, "add -- \"" + PackagePath + "\"");
            ExpectGit(parentRoot, "commit -m \"Pin detached local-only commit\"");

            bool prepared = GitUtility.TryPrepareSubmoduleUpdate(
                PackagePath,
                out SubmoduleUpdatePlan _,
                out string error);

            Assert.That(prepared, Is.False);
            Assert.That(error, Does.Contain("not reachable from any remote"));
            Assert.That(ExpectGit(packageRoot, "rev-parse HEAD").StdOut.Trim(), Is.EqualTo(localHead));
        }

        [Test]
        public void Update_PreflightRefusesExistingParentGitlinkChange()
        {
            string packageRoot = Path.Combine(parentRoot, PackagePath);
            File.AppendAllText(Path.Combine(sourceRoot, "package.json"), "\n");
            ExpectGit(sourceRoot, "add -- package.json");
            ExpectGit(sourceRoot, "commit -m \"Remote update for gitlink fixture\"");
            ExpectGit(packageRoot, "fetch origin");
            string remoteHead = ExpectGit(packageRoot, "rev-parse origin/HEAD").StdOut.Trim();
            ExpectGit(packageRoot, "checkout --detach \"" + remoteHead + "\"");

            bool prepared = GitUtility.TryPrepareSubmoduleUpdate(
                PackagePath,
                out SubmoduleUpdatePlan _,
                out string error);

            Assert.That(prepared, Is.False);
            Assert.That(error, Does.Contain("parent repository already has"));
            Assert.That(Git(parentRoot, "status --porcelain=v2 -- \"" + PackagePath + "\"").StdOut, Is.Not.Empty);
        }

        [Test]
        public void Update_RefusesIgnoredFilesBeforeCheckout()
        {
            Assert.That(
                GitUtility.TryResolveSubmoduleGitDir(PackagePath, out string gitDir, out string resolveError),
                Is.True,
                resolveError);
            Directory.CreateDirectory(Path.Combine(gitDir, "info"));
            File.AppendAllText(Path.Combine(gitDir, "info", "exclude"), "\nprecious-cache.txt\n");
            string preciousFile = Path.Combine(parentRoot, PackagePath, "precious-cache.txt");
            File.WriteAllText(preciousFile, "must survive\n");

            bool prepared = GitUtility.TryPrepareSubmoduleUpdate(
                PackagePath,
                sourceRoot,
                string.Empty,
                out _,
                out string error);

            Assert.That(prepared, Is.False);
            Assert.That(error, Does.Contain("ignored"));
            Assert.That(File.ReadAllText(preciousFile), Is.EqualTo("must survive\n"));
            Assert.That(
                GitUtility.BuildCheckoutSubmoduleArguments(PackagePath, new string('a', 40)),
                Does.Contain("--no-overwrite-ignore"));
        }

        [Test]
        public void Update_DeinitializedDirectoryCannotResolveToParentRepository()
        {
            string parentHead = ExpectGit(parentRoot, "rev-parse HEAD").StdOut.Trim();
            ExpectGit(parentRoot, "submodule deinit -f -- \"" + PackagePath + "\"");

            bool prepared = GitUtility.TryPrepareSubmoduleUpdate(
                PackagePath,
                sourceRoot,
                string.Empty,
                out _,
                out string error);

            Assert.That(prepared, Is.False);
            Assert.That(error, Does.Contain("exact package directory"));
            Assert.That(ExpectGit(parentRoot, "rev-parse HEAD").StdOut.Trim(), Is.EqualTo(parentHead));
        }

        [Test]
        public void Update_RefusesMismatchedChildOrigin()
        {
            string differentSource = CreateSourceRepository("different-update-source", "com.example.different");
            ExpectGit(Path.Combine(parentRoot, PackagePath), "remote set-url origin \"" + differentSource + "\"");

            bool prepared = GitUtility.TryPrepareSubmoduleUpdate(
                PackagePath,
                sourceRoot,
                string.Empty,
                out _,
                out string error);

            Assert.That(prepared, Is.False);
            Assert.That(error, Does.Contain("origin URL"));
        }

        [Test]
        public void Update_RefusesTrackedBranchChangedAfterPreview()
        {
            Assert.That(
                GitUtility.TryPrepareSubmoduleUpdate(
                    PackagePath,
                    sourceRoot,
                    string.Empty,
                    out SubmoduleUpdatePlan preview,
                    out string previewError),
                Is.True,
                previewError);
            ExpectGit(
                parentRoot,
                "config --file .gitmodules submodule.\"" + PackagePath + "\".branch main");
            ExpectGit(parentRoot, "add -- .gitmodules");
            ExpectGit(parentRoot, "commit -m \"Change tracked branch externally\"");

            bool prepared = GitUtility.TryPrepareSubmoduleUpdate(
                PackagePath,
                sourceRoot,
                preview.ExpectedBranch,
                out _,
                out string error);

            Assert.That(prepared, Is.False);
            Assert.That(error, Does.Contain("branch changed"));
        }

        [Test]
        public void Update_RecoveryPreservesPostPreflightTrackedEdit()
        {
            Assert.That(
                GitUtility.TryPrepareSubmoduleUpdate(PackagePath, out SubmoduleUpdatePlan plan, out string prepareError),
                Is.True,
                prepareError);
            string packageJson = Path.Combine(parentRoot, PackagePath, "package.json");
            const string concurrentEdit = "\n// edit created after update preflight\n";
            File.AppendAllText(packageJson, concurrentEdit);

            bool recovered = GitUtility.TryRecoverFailedSubmoduleUpdate(plan, out string recoveryError);

            Assert.That(recovered, Is.False);
            Assert.That(recoveryError, Does.Contain("changed after the update began"));
            Assert.That(File.ReadAllText(packageJson), Does.Contain(concurrentEdit.Trim()));
        }

        [Test]
        public void FailedAddRollback_RestoresAbsentGitmodulesAndQuarantinesWorktree()
        {
            string cleanParent = Path.Combine(sandboxRoot, "clean-parent");
            Directory.CreateDirectory(cleanParent);
            InitializeRepository(cleanParent);
            File.WriteAllText(Path.Combine(cleanParent, "README.md"), "clean parent\n");
            ExpectGit(cleanParent, "add -- README.md");
            ExpectGit(cleanParent, "commit -m \"Initial clean parent\"");
            RedirectProjectRoot(cleanParent);

            Assert.That(
                GitUtility.TryPrepareAddSubmodule(sourceRoot, PackagePath, out AddSubmodulePlan plan, out string prepareError),
                Is.True,
                prepareError);
            Assert.That(plan.GitModulesExisted, Is.False);
            Assert.That(
                GitUtility.TryBuildAddSubmoduleArguments(
                    sourceRoot,
                    PackagePath,
                    string.Empty,
                    plan.ReuseExistingMetadata,
                    out string arguments,
                    out string argumentError),
                Is.True,
                argumentError);
            Assert.That(Git(cleanParent, arguments).IsSuccess, Is.True);
            CaptureFailedAddRollbackEvidence(cleanParent, plan);
            string marker = Path.Combine(cleanParent, PackagePath, "late-file.txt");
            File.WriteAllText(marker, "must be preserved\n");

            bool cleaned = GitUtility.TryCleanupFailedAdd(plan, out string notice);

            Assert.That(cleaned, Is.True, notice);
            Assert.That(File.Exists(Path.Combine(cleanParent, ".gitmodules")), Is.False);
            Assert.That(Git(cleanParent, "status --porcelain=v2 -- .gitmodules \"" + PackagePath + "\"").StdOut, Is.Empty);
            string recoveryRoot = Path.Combine(cleanParent, "Library", "GitSubmoduleManager", "Recovery");
            string[] recoveredMarkers = Directory.GetFiles(recoveryRoot, "late-file.txt", SearchOption.AllDirectories);
            Assert.That(recoveredMarkers, Has.Length.EqualTo(1));
            Assert.That(File.ReadAllText(recoveredMarkers[0]), Is.EqualTo("must be preserved\n"));
            Assert.That(notice, Does.Contain("preserved"));
        }

        [Test]
        public void FailedAddRollback_WorktreeSwapAtQuarantineSeamIsPreserved()
        {
            string cleanParent = CreateCleanParent(
                "failed-add-gitmodules-quarantine-race-parent");
            RedirectProjectRoot(cleanParent);
            Assert.That(
                GitUtility.TryPrepareAddSubmodule(
                    sourceRoot,
                    PackagePath,
                    out AddSubmodulePlan plan,
                    out string prepareError),
                Is.True,
                prepareError);
            Assert.That(
                GitUtility.TryBuildAddSubmoduleArguments(
                    sourceRoot,
                    PackagePath,
                    string.Empty,
                    plan.ReuseExistingMetadata,
                    out string arguments,
                    out string argumentError),
                Is.True,
                argumentError);
            Assert.That(Git(cleanParent, arguments).IsSuccess, Is.True);
            CaptureFailedAddRollbackEvidence(cleanParent, plan);

            const string concurrentContents = " \t\n";
            using (GitUtility.OverrideBeforeGitModulesCleanupMoveForTests(path =>
                   {
                       if (File.Exists(path))
                           File.Delete(path);
                       File.WriteAllText(path, concurrentContents);
                   }))
            {
                bool cleaned = GitUtility.TryCleanupFailedAdd(
                    plan,
                    out string warning);

                Assert.That(cleaned, Is.False);
                Assert.That(warning, Does.Contain("concurrent data was preserved"));
            }

            string recoveryDirectory = Path.Combine(
                cleanParent,
                "Library",
                "GitSubmoduleManager",
                "Recovery",
                "GitModulesCleanup");
            string[] preservedFiles = Directory.GetFiles(
                recoveryDirectory,
                "*.gitmodules",
                SearchOption.TopDirectoryOnly);
            Assert.That(preservedFiles, Has.Length.EqualTo(1));
            Assert.That(
                File.ReadAllText(preservedFiles[0]),
                Is.EqualTo(concurrentContents));
            Assert.That(
                File.Exists(Path.Combine(cleanParent, ".gitmodules")),
                Is.False,
                "The concurrently replaced inode must be moved into recovery, never unlinked.");
        }

        [Test]
        public void FailedAddRollback_RecoveryRootSymlinkSwapDoesNotMoveMetadataOutsideProject()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                Assert.Ignore("Creating an unprivileged symbolic link is not portable on Windows test hosts.");

            string cleanParent = CreateCleanParent(
                "failed-add-metadata-recovery-race-parent");
            RedirectProjectRoot(cleanParent);
            Assert.That(
                GitUtility.TryPrepareAddSubmodule(
                    sourceRoot,
                    PackagePath,
                    out AddSubmodulePlan plan,
                    out string prepareError),
                Is.True,
                prepareError);
            Assert.That(
                GitUtility.TryBuildAddSubmoduleArguments(
                    sourceRoot,
                    PackagePath,
                    string.Empty,
                    plan.ReuseExistingMetadata,
                    out string arguments,
                    out string argumentError),
                Is.True,
                argumentError);
            Assert.That(Git(cleanParent, arguments).IsSuccess, Is.True);
            CaptureFailedAddRollbackEvidence(cleanParent, plan);
            Assert.That(
                GitUtility.TryResolveSubmoduleGitDir(
                    PackagePath,
                    out string moduleGitDir,
                    out string metadataError),
                Is.True,
                metadataError);
            string outsideRecovery = Path.Combine(
                sandboxRoot,
                "outside-metadata-recovery");
            Directory.CreateDirectory(outsideRecovery);
            string preservedRecovery = string.Empty;

            using (GitUtility.OverrideAfterSubmoduleMetadataRecoveryRootCreateForTests(
                       recoveryRoot =>
                       {
                           preservedRecovery = recoveryRoot + "-preserved";
                           Directory.Move(recoveryRoot, preservedRecovery);
                           CommandResult linkResult = CliCommandRunner.Run(
                               "/bin/ln",
                               "-s -- " + GitUtility.Quote(outsideRecovery) +
                               " " + GitUtility.Quote(recoveryRoot),
                               cleanParent,
                               5000);
                           Assert.That(
                               linkResult.IsSuccess,
                               Is.True,
                               linkResult.StdErr);
                       }))
            {
                bool cleaned = GitUtility.TryCleanupFailedAdd(
                    plan,
                    out string warning);

                Assert.That(cleaned, Is.False);
                Assert.That(warning, Does.Contain("symbolic link"));
            }

            Assert.That(
                Directory.Exists(moduleGitDir),
                Is.True,
                "The owned Git metadata must remain in Git's modules directory.");
            Assert.That(
                Directory.GetFileSystemEntries(outsideRecovery),
                Is.Empty,
                "No recovery data may be moved through the raced symbolic link.");
            Assert.That(
                Directory.Exists(preservedRecovery),
                Is.True,
                "Earlier recovery data must remain preserved inside the project.");
        }

        [Test]
        public void FailedAddRollback_LateRecoveryRootSymlinkSwapDoesNotMoveMetadataOutsideProject()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                Assert.Ignore("Creating an unprivileged symbolic link is not portable on Windows test hosts.");

            string cleanParent = CreateCleanParent(
                "failed-add-metadata-late-recovery-race-parent");
            RedirectProjectRoot(cleanParent);
            Assert.That(
                GitUtility.TryPrepareAddSubmodule(
                    sourceRoot,
                    PackagePath,
                    out AddSubmodulePlan plan,
                    out string prepareError),
                Is.True,
                prepareError);
            Assert.That(
                GitUtility.TryBuildAddSubmoduleArguments(
                    sourceRoot,
                    PackagePath,
                    string.Empty,
                    plan.ReuseExistingMetadata,
                    out string arguments,
                    out string argumentError),
                Is.True,
                argumentError);
            Assert.That(Git(cleanParent, arguments).IsSuccess, Is.True);
            CaptureFailedAddRollbackEvidence(cleanParent, plan);
            Assert.That(
                GitUtility.TryResolveSubmoduleGitDir(
                    PackagePath,
                    out string moduleGitDir,
                    out string metadataError),
                Is.True,
                metadataError);
            string outsideRecovery = Path.Combine(
                sandboxRoot,
                "outside-metadata-late-recovery");
            Directory.CreateDirectory(outsideRecovery);
            string preservedRecovery = string.Empty;

            using (GitUtility.OverrideBeforeSubmoduleMetadataMoveForTests(
                       recoveryRoot =>
                       {
                           preservedRecovery = recoveryRoot + "-preserved";
                           Directory.Move(recoveryRoot, preservedRecovery);
                           CommandResult linkResult = CliCommandRunner.Run(
                               "/bin/ln",
                               "-s -- " + GitUtility.Quote(outsideRecovery) +
                               " " + GitUtility.Quote(recoveryRoot),
                               cleanParent,
                               5000);
                           Assert.That(
                               linkResult.IsSuccess,
                               Is.True,
                               linkResult.StdErr);
                       }))
            {
                bool cleaned = GitUtility.TryCleanupFailedAdd(
                    plan,
                    out string warning);

                Assert.That(cleaned, Is.False);
                Assert.That(warning, Does.Contain("symbolic link"));
            }

            Assert.That(
                Directory.Exists(moduleGitDir),
                Is.True,
                "The owned Git metadata must remain in Git's modules directory.");
            Assert.That(
                Directory.GetFileSystemEntries(outsideRecovery),
                Is.Empty,
                "No recovery data may be moved through the late raced symbolic link.");
            Assert.That(
                Directory.Exists(preservedRecovery),
                Is.True,
                "Earlier recovery data must remain preserved inside the project.");
        }

        [Test]
        public void FailedAddRollback_ExactIndexCasPreservesConcurrentStagedGitmodules()
        {
            string cleanParent = CreateCleanParent(
                "failed-add-index-cas-race-parent");
            RedirectProjectRoot(cleanParent);
            Assert.That(
                GitUtility.TryPrepareAddSubmodule(
                    sourceRoot,
                    PackagePath,
                    out AddSubmodulePlan plan,
                    out string prepareError),
                Is.True,
                prepareError);
            Assert.That(
                GitUtility.TryBuildAddSubmoduleArguments(
                    sourceRoot,
                    PackagePath,
                    string.Empty,
                    plan.ReuseExistingMetadata,
                    out string arguments,
                    out string argumentError),
                Is.True,
                argumentError);
            Assert.That(Git(cleanParent, arguments).IsSuccess, Is.True);
            CaptureFailedAddRollbackEvidence(cleanParent, plan);

            const string concurrentContents =
                "# concurrent staged failed-add replacement\n";
            string concurrentSource = Path.Combine(
                sandboxRoot,
                "failed-add-concurrent.gitmodules");
            File.WriteAllText(concurrentSource, concurrentContents);
            string concurrentBlob = ExpectGit(
                    cleanParent,
                    "hash-object -w -- " + GitUtility.Quote(concurrentSource))
                .StdOut.Trim();

            using (GitUtility.OverrideBeforeGitModulesIndexCompareAndSwapForTests(
                       _ => ExpectGit(
                           cleanParent,
                           "update-index --add --cacheinfo 100644," +
                           concurrentBlob + ",.gitmodules")))
            {
                bool cleaned = GitUtility.TryCleanupFailedAdd(
                    plan,
                    out string warning);

                Assert.That(cleaned, Is.False);
                Assert.That(warning, Does.Contain("concurrent staged data"));
            }

            Assert.That(
                ExpectGit(cleanParent, "rev-parse :.gitmodules").StdOut.Trim(),
                Is.EqualTo(concurrentBlob));
            Assert.That(
                ExpectGit(cleanParent, "show :.gitmodules").StdOut,
                Is.EqualTo(concurrentContents.TrimEnd('\r', '\n')));
            Assert.That(
                File.Exists(Path.Combine(cleanParent, ".gitmodules")),
                Is.False,
                "The staged replacement must not be materialized or deleted through the worktree.");
        }

        [Test]
        public void FailedAddRollback_ExactIndexCasPreservesConcurrentGitlink()
        {
            string cleanParent = CreateCleanParent(
                "failed-add-gitlink-cas-race-parent");
            RedirectProjectRoot(cleanParent);
            Assert.That(
                GitUtility.TryPrepareAddSubmodule(
                    sourceRoot,
                    PackagePath,
                    out AddSubmodulePlan plan,
                    out string prepareError),
                Is.True,
                prepareError);
            Assert.That(
                GitUtility.TryBuildAddSubmoduleArguments(
                    sourceRoot,
                    PackagePath,
                    string.Empty,
                    plan.ReuseExistingMetadata,
                    out string arguments,
                    out string argumentError),
                Is.True,
                argumentError);
            Assert.That(Git(cleanParent, arguments).IsSuccess, Is.True);
            CaptureFailedAddRollbackEvidence(cleanParent, plan);

            string concurrentGitlink = ExpectGit(cleanParent, "rev-parse HEAD")
                .StdOut.Trim();
            string gitModulesBlob = ExpectGit(
                    cleanParent,
                    "rev-parse :.gitmodules")
                .StdOut.Trim();
            using (GitUtility.OverrideBeforeGitModulesIndexCompareAndSwapForTests(
                       _ => ExpectGit(
                           cleanParent,
                           "update-index --add --cacheinfo 160000," +
                           concurrentGitlink + "," + PackagePath)))
            {
                bool cleaned = GitUtility.TryCleanupFailedAdd(
                    plan,
                    out string warning);

                Assert.That(cleaned, Is.False);
                Assert.That(warning, Does.Contain("concurrent staged data"));
            }

            Assert.That(
                ExpectGit(
                    cleanParent,
                    "ls-files --stage -- \"" + PackagePath + "\"")
                .StdOut,
                Does.Contain(concurrentGitlink));
            Assert.That(
                ExpectGit(cleanParent, "rev-parse :.gitmodules").StdOut.Trim(),
                Is.EqualTo(gitModulesBlob),
                "A stale combined patch must not partially change .gitmodules.");
        }

        [Test]
        public void FailedAddRollback_PreservesUnrelatedSectionStagedAfterEvidenceCapture()
        {
            string cleanParent = CreateCleanParent(
                "failed-add-unrelated-registration-parent");
            RedirectProjectRoot(cleanParent);
            Assert.That(
                GitUtility.TryPrepareAddSubmodule(
                    sourceRoot,
                    PackagePath,
                    out AddSubmodulePlan plan,
                    out string prepareError),
                Is.True,
                prepareError);
            Assert.That(
                GitUtility.TryBuildAddSubmoduleArguments(
                    sourceRoot,
                    PackagePath,
                    string.Empty,
                    plan.ReuseExistingMetadata,
                    out string arguments,
                    out string argumentError),
                Is.True,
                argumentError);
            Assert.That(Git(cleanParent, arguments).IsSuccess, Is.True);
            CaptureFailedAddRollbackEvidence(cleanParent, plan);

            string originalGitlink = ExpectGit(
                    cleanParent,
                    "rev-parse :" + PackagePath)
                .StdOut.Trim();
            const string unrelatedPath = "Packages/com.example.unrelated";
            const string unrelatedUrl =
                "https://example.invalid/team/unrelated.git";
            string gitModulesPath = Path.Combine(cleanParent, ".gitmodules");
            File.AppendAllText(
                gitModulesPath,
                "\n[submodule \"unrelated\"]\n" +
                "\tpath = " + unrelatedPath + "\n" +
                "\turl = " + unrelatedUrl + "\n");
            ExpectGit(cleanParent, "add -- .gitmodules");
            string stagedGitModules = ExpectGit(cleanParent, "show :.gitmodules")
                .StdOut;

            bool cleaned = GitUtility.TryCleanupFailedAdd(
                plan,
                out string warning);

            Assert.That(cleaned, Is.False);
            Assert.That(warning, Does.Contain("concurrent staged data"));
            Assert.That(
                ExpectGit(cleanParent, "show :.gitmodules").StdOut,
                Is.EqualTo(stagedGitModules));
            Assert.That(File.ReadAllText(gitModulesPath), Does.Contain(unrelatedUrl));
            Assert.That(
                ExpectGit(cleanParent, "rev-parse :" + PackagePath).StdOut.Trim(),
                Is.EqualTo(originalGitlink));
            Assert.That(
                File.Exists(Path.Combine(cleanParent, PackagePath, "package.json")),
                Is.True);
        }

        [Test]
        public void FailedAddRollback_PreservesSameOriginAlternateCleanCommit()
        {
            string cleanParent = CreateCleanParent(
                "failed-add-alternate-commit-parent");
            RedirectProjectRoot(cleanParent);
            Assert.That(
                GitUtility.TryPrepareAddSubmodule(
                    sourceRoot,
                    PackagePath,
                    out AddSubmodulePlan plan,
                    out string prepareError),
                Is.True,
                prepareError);
            Assert.That(
                GitUtility.TryBuildAddSubmoduleArguments(
                    sourceRoot,
                    PackagePath,
                    string.Empty,
                    plan.ReuseExistingMetadata,
                    out string arguments,
                    out string argumentError),
                Is.True,
                argumentError);
            Assert.That(Git(cleanParent, arguments).IsSuccess, Is.True);
            CaptureFailedAddRollbackEvidence(cleanParent, plan);
            string originalGitlink = ExpectGit(
                    cleanParent,
                    "rev-parse :" + PackagePath)
                .StdOut.Trim();

            File.WriteAllText(
                Path.Combine(sourceRoot, "alternate.txt"),
                "same-origin alternate commit\n");
            ExpectGit(sourceRoot, "add -- alternate.txt");
            ExpectGit(sourceRoot, "commit -m \"Create alternate cleanup commit\"");
            string alternateCommit = ExpectGit(
                    sourceRoot,
                    "rev-parse --verify HEAD^{commit}")
                .StdOut.Trim();
            string packageRoot = Path.Combine(cleanParent, PackagePath);
            ExpectGit(packageRoot, "fetch origin");
            ExpectGit(
                packageRoot,
                "checkout --detach " + GitUtility.Quote(alternateCommit));
            ExpectGit(cleanParent, "add -- " + GitUtility.Quote(PackagePath));

            bool cleaned = GitUtility.TryCleanupFailedAdd(
                plan,
                out string warning);

            Assert.That(cleaned, Is.False);
            Assert.That(warning, Does.Contain("gitlink changed"));
            Assert.That(alternateCommit, Is.Not.EqualTo(originalGitlink));
            Assert.That(
                ExpectGit(cleanParent, "rev-parse :" + PackagePath).StdOut.Trim(),
                Is.EqualTo(alternateCommit).IgnoreCase);
            Assert.That(
                ExpectGit(packageRoot, "rev-parse --verify HEAD^{commit}").StdOut.Trim(),
                Is.EqualTo(alternateCommit).IgnoreCase);
            Assert.That(File.Exists(Path.Combine(packageRoot, "package.json")), Is.True);
        }

        [Test]
        public void FailedAddRollback_CancellationAfterIndexMutationCompletesRecovery()
        {
            string cleanParent = CreateCleanParent(
                "failed-add-post-mutation-cancellation-parent");
            RedirectProjectRoot(cleanParent);
            Assert.That(
                GitUtility.TryPrepareAddSubmodule(
                    sourceRoot,
                    PackagePath,
                    out AddSubmodulePlan plan,
                    out string prepareError),
                Is.True,
                prepareError);
            Assert.That(
                GitUtility.TryBuildAddSubmoduleArguments(
                    sourceRoot,
                    PackagePath,
                    string.Empty,
                    plan.ReuseExistingMetadata,
                    out string arguments,
                    out string argumentError),
                Is.True,
                argumentError);
            Assert.That(Git(cleanParent, arguments).IsSuccess, Is.True);
            CaptureFailedAddRollbackEvidence(cleanParent, plan);

            using var cancellation = new CancellationTokenSource();
            using (GitUtility.OverrideAfterExactSubmoduleIndexApplyForTests(
                       _ => cancellation.Cancel()))
            {
                bool cleaned = GitUtility.TryCleanupFailedAdd(
                    plan,
                    out string notice,
                    cancellation.Token);

                Assert.That(cancellation.IsCancellationRequested, Is.True);
                Assert.That(cleaned, Is.True, notice);
                Assert.That(notice, Does.Contain("preserved at"));
                Assert.That(notice, Does.Contain("Recovery"));
            }

            Assert.That(File.Exists(Path.Combine(cleanParent, ".gitmodules")), Is.False);
            Assert.That(Directory.Exists(Path.Combine(cleanParent, PackagePath)), Is.False);
            Assert.That(
                Git(cleanParent, "ls-files --error-unmatch -- " +
                                 GitUtility.Quote(PackagePath)).IsSuccess,
                Is.False);
        }

        [Test]
        public void FailedAddCleanup_RefusesRegistrationOwnedByAnotherRepository()
        {
            string cleanParent = CreateCleanParent("failed-add-ownership-parent");
            string destination = "Packages/com.example.raced-package";
            RedirectProjectRoot(cleanParent);
            Assert.That(
                GitUtility.TryPrepareAddSubmodule(
                    sourceRoot,
                    destination,
                    out AddSubmodulePlan plan,
                    out string prepareError),
                Is.True,
                prepareError);
            plan.ExpectedBranch = string.Empty;
            string differentSource = CreateSourceRepository("raced-source", "com.example.raced-package");
            ExpectGit(
                cleanParent,
                "-c protocol.file.allow=always submodule add \"" + differentSource + "\" \"" + destination + "\"");
            CaptureFailedAddRollbackEvidence(cleanParent, plan);

            bool cleaned = GitUtility.TryCleanupFailedAdd(plan, out string warning);

            Assert.That(cleaned, Is.False);
            Assert.That(warning, Does.Contain("does not match"));
            Assert.That(File.Exists(Path.Combine(cleanParent, destination, "package.json")), Is.True);
            Assert.That(File.ReadAllText(Path.Combine(cleanParent, ".gitmodules")), Does.Contain(differentSource));
        }

        [Test]
        public void FailedAddCleanup_RefusesWorktreeOriginWithChangedGenericSshUser()
        {
            const string approvedUrl =
                "ssh://alice@git.example.com/team/integration-package.git";
            const string changedUserUrl =
                "ssh://bob@git.example.com/team/integration-package.git";
            string cleanParent = CreateCleanParent(
                "failed-add-changed-ssh-user-parent");
            RedirectProjectRoot(cleanParent);
            Assert.That(
                GitUtility.TryPrepareAddSubmodule(
                    approvedUrl,
                    PackagePath,
                    out AddSubmodulePlan plan,
                    out string prepareError),
                Is.True,
                prepareError);
            plan.ExpectedBranch = string.Empty;
            ExpectGit(
                cleanParent,
                "-c protocol.file.allow=always submodule add " +
                GitUtility.Quote(sourceRoot) + " " +
                GitUtility.Quote(PackagePath));
            ExpectGit(
                cleanParent,
                "config --file .gitmodules " +
                GitUtility.Quote("submodule." + PackagePath + ".url") + " " +
                GitUtility.Quote(approvedUrl));
            ExpectGit(cleanParent, "add -- .gitmodules");
            CaptureFailedAddRollbackEvidence(cleanParent, plan);
            ExpectGit(
                cleanParent,
                "-C " + GitUtility.Quote(PackagePath) +
                " remote set-url origin " + GitUtility.Quote(changedUserUrl));

            bool cleaned = GitUtility.TryCleanupFailedAdd(
                plan,
                out string warning);

            Assert.That(cleaned, Is.False);
            Assert.That(warning, Does.Contain("origin does not match"));
            Assert.That(
                File.Exists(Path.Combine(cleanParent, PackagePath, "package.json")),
                Is.True);
            Assert.That(
                ExpectGit(
                    cleanParent,
                    "ls-files --error-unmatch -- " + GitUtility.Quote(PackagePath))
                .IsSuccess,
                Is.True);
            Assert.That(
                File.ReadAllText(Path.Combine(cleanParent, ".gitmodules")),
                Does.Contain(approvedUrl));
        }

        [Test]
        public void Initialize_RefusesResidualFilesAndAlreadyInitializedWorktrees()
        {
            Assert.That(
                GitUtility.TryPrepareSubmoduleInitialization(PackagePath, sourceRoot, out string initializedError),
                Is.False);
            Assert.That(initializedError, Does.Contain("already an initialized"));

            ExpectGit(parentRoot, "submodule deinit -f -- \"" + PackagePath + "\"");
            string residual = Path.Combine(parentRoot, PackagePath, "precious.txt");
            File.WriteAllText(residual, "must survive\n");

            bool prepared = GitUtility.TryPrepareSubmoduleInitialization(
                PackagePath,
                sourceRoot,
                out string error);

            Assert.That(prepared, Is.False);
            Assert.That(error, Does.Contain("contains files"));
            Assert.That(File.ReadAllText(residual), Is.EqualTo("must survive\n"));
        }

        [Test]
        public void Add_NestedUnityProjectRefusesAncestorRepository()
        {
            string ancestor = CreateCleanParent("ancestor-parent");
            string nestedProject = Path.Combine(ancestor, "NestedUnityProject");
            Directory.CreateDirectory(Path.Combine(nestedProject, "Packages"));
            Directory.CreateDirectory(Path.Combine(nestedProject, "Assets"));
            RedirectProjectRoot(nestedProject);
            string statusBefore = Git(ancestor, "status --porcelain=v2").StdOut;

            bool prepared = GitUtility.TryPrepareAddSubmodule(
                sourceRoot,
                "Packages/com.example.nested",
                out AddSubmodulePlan _,
                out string error);

            Assert.That(prepared, Is.False);
            Assert.That(error, Does.Contain("repository root"));
            Assert.That(File.Exists(Path.Combine(ancestor, ".gitmodules")), Is.False);
            Assert.That(Git(ancestor, "status --porcelain=v2").StdOut, Is.EqualTo(statusBefore));
        }

        [Test]
        public void ParentCoreWorktreeRedirect_BlocksAddAndRemoveWithoutTouchingOutsideData()
        {
            string addParent = CreateCleanParent("redirected-add-parent");
            string addOutside = Path.Combine(sandboxRoot, "redirected-add-outside");
            Directory.CreateDirectory(addOutside);
            string addSentinel = Path.Combine(addOutside, "sentinel.txt");
            File.WriteAllText(addSentinel, "add outside data must survive\n");
            byte[] addIndexBefore = File.ReadAllBytes(Path.Combine(addParent, ".git", "index"));
            ExpectGit(addParent, "config core.worktree " + GitUtility.Quote(addOutside));
            RedirectProjectRoot(addParent);

            bool prepared = GitUtility.TryPrepareAddSubmodule(
                sourceRoot,
                "Packages/com.example.redirected",
                out AddSubmodulePlan _,
                out string addError);

            Assert.That(prepared, Is.False);
            Assert.That(addError, Does.Contain("outside"));
            Assert.That(File.ReadAllText(addSentinel), Is.EqualTo("add outside data must survive\n"));
            CollectionAssert.AreEqual(
                addIndexBefore,
                File.ReadAllBytes(Path.Combine(addParent, ".git", "index")));
            Assert.That(File.Exists(Path.Combine(addParent, ".gitmodules")), Is.False);

            string removeOutside = Path.Combine(sandboxRoot, "redirected-remove-outside");
            Directory.CreateDirectory(removeOutside);
            string removeSentinel = Path.Combine(removeOutside, "sentinel.txt");
            File.WriteAllText(removeSentinel, "remove outside data must survive\n");
            byte[] removeIndexBefore = File.ReadAllBytes(Path.Combine(parentRoot, ".git", "index"));
            string packageJson = Path.Combine(parentRoot, PackagePath, "package.json");
            string packageJsonBefore = File.ReadAllText(packageJson);
            ExpectGit(parentRoot, "config core.worktree " + GitUtility.Quote(removeOutside));
            RedirectProjectRoot(parentRoot);

            bool removed = GitUtility.TryRemoveSubmodule(PackagePath, out string removeError);

            Assert.That(removed, Is.False);
            Assert.That(removeError, Does.Contain("outside"));
            Assert.That(File.ReadAllText(removeSentinel), Is.EqualTo("remove outside data must survive\n"));
            Assert.That(File.ReadAllText(packageJson), Is.EqualTo(packageJsonBefore));
            CollectionAssert.AreEqual(
                removeIndexBefore,
                File.ReadAllBytes(Path.Combine(parentRoot, ".git", "index")));
        }

        [Test]
        public void SubmoduleCoreWorktreeRedirect_BlocksUpdateWithoutTouchingOutsideData()
        {
            string packageRoot = Path.Combine(parentRoot, PackagePath);
            Assert.That(
                GitUtility.TryResolveSubmoduleGitDir(PackagePath, out string moduleGitDir, out string resolveError),
                Is.True,
                resolveError);
            string moduleIndex = Path.Combine(moduleGitDir, "index");
            byte[] moduleIndexBefore = File.ReadAllBytes(moduleIndex);
            byte[] parentIndexBefore = File.ReadAllBytes(Path.Combine(parentRoot, ".git", "index"));
            string outside = Path.Combine(sandboxRoot, "redirected-submodule-outside");
            Directory.CreateDirectory(outside);
            string sentinel = Path.Combine(outside, "sentinel.txt");
            File.WriteAllText(sentinel, "submodule outside data must survive\n");
            ExpectGit(packageRoot, "config core.worktree " + GitUtility.Quote(outside));

            bool prepared = GitUtility.TryPrepareSubmoduleUpdate(
                PackagePath,
                out SubmoduleUpdatePlan _,
                out string error);

            Assert.That(prepared, Is.False);
            Assert.That(error, Does.Contain("outside"));
            Assert.That(File.ReadAllText(sentinel), Is.EqualTo("submodule outside data must survive\n"));
            CollectionAssert.AreEqual(moduleIndexBefore, File.ReadAllBytes(moduleIndex));
            CollectionAssert.AreEqual(
                parentIndexBefore,
                File.ReadAllBytes(Path.Combine(parentRoot, ".git", "index")));
        }

        [Test]
        public void ReplacedSubmoduleGitDirectory_BlocksUpdateWithoutTouchingExternalMetadata()
        {
            string packageRoot = Path.Combine(parentRoot, PackagePath);
            Assert.That(
                GitUtility.TryResolveSubmoduleGitDir(PackagePath, out string registeredGitDir, out string resolveError),
                Is.True,
                resolveError);
            string registeredIndex = Path.Combine(registeredGitDir, "index");
            byte[] registeredIndexBefore = File.ReadAllBytes(registeredIndex);
            byte[] parentIndexBefore = File.ReadAllBytes(Path.Combine(parentRoot, ".git", "index"));

            string externalClone = Path.Combine(sandboxRoot, "external-metadata-clone");
            ExpectGit(
                sandboxRoot,
                "-c protocol.file.allow=always clone " +
                GitUtility.Quote(sourceRoot) + " " + GitUtility.Quote(externalClone));
            string externalGitDir = Path.Combine(externalClone, ".git");
            ExpectGit(
                externalClone,
                "config core.worktree " + GitUtility.Quote(packageRoot));
            File.WriteAllText(
                Path.Combine(packageRoot, ".git"),
                "gitdir: " + GitUtility.NormalizePath(externalGitDir) + "\n");

            Assert.That(
                ExpectGit(packageRoot, "rev-parse --is-inside-work-tree").StdOut.Trim(),
                Is.EqualTo("true"),
                "The fixture must otherwise look like a valid worktree rooted at the package path.");
            Assert.That(
                ExpectGit(packageRoot, "rev-parse --show-prefix").StdOut.Trim(),
                Is.Empty);
            byte[] externalConfigBefore = File.ReadAllBytes(Path.Combine(externalGitDir, "config"));
            byte[] externalHeadBefore = File.ReadAllBytes(Path.Combine(externalGitDir, "HEAD"));
            string externalPackageBefore = File.ReadAllText(Path.Combine(externalClone, "package.json"));

            bool prepared = GitUtility.TryPrepareSubmoduleUpdate(
                PackagePath,
                out SubmoduleUpdatePlan _,
                out string error);

            Assert.That(prepared, Is.False);
            Assert.That(error, Does.Contain("registered submodule metadata"));
            CollectionAssert.AreEqual(
                externalConfigBefore,
                File.ReadAllBytes(Path.Combine(externalGitDir, "config")));
            CollectionAssert.AreEqual(
                externalHeadBefore,
                File.ReadAllBytes(Path.Combine(externalGitDir, "HEAD")));
            Assert.That(
                File.ReadAllText(Path.Combine(externalClone, "package.json")),
                Is.EqualTo(externalPackageBefore));
            CollectionAssert.AreEqual(registeredIndexBefore, File.ReadAllBytes(registeredIndex));
            CollectionAssert.AreEqual(
                parentIndexBefore,
                File.ReadAllBytes(Path.Combine(parentRoot, ".git", "index")));
        }

        [Test]
        public void Add_RefusesUntrackedGitmodulesAndPreservesIt()
        {
            string cleanParent = CreateCleanParent("untracked-gitmodules-parent");
            string gitModulesPath = Path.Combine(cleanParent, ".gitmodules");
            const string original = "# unrelated local file\n";
            File.WriteAllText(gitModulesPath, original);
            RedirectProjectRoot(cleanParent);

            bool prepared = GitUtility.TryPrepareAddSubmodule(
                sourceRoot,
                "Packages/com.example.second-package",
                out AddSubmodulePlan _,
                out string error);

            Assert.That(prepared, Is.False);
            Assert.That(error, Does.Contain("untracked"));
            Assert.That(File.ReadAllText(gitModulesPath), Is.EqualTo(original));
            Assert.That(Git(cleanParent, "status --porcelain=v2 --untracked-files=all -- .gitmodules").StdOut, Is.Not.Empty);
        }

        [Test]
        public void Add_RefusesPhysicalEmptyDestinationDirectory()
        {
            string cleanParent = CreateCleanParent("physical-destination-parent");
            string destination = "Packages/com.example.physical-destination";
            Directory.CreateDirectory(Path.Combine(cleanParent, destination));
            RedirectProjectRoot(cleanParent);

            bool prepared = GitUtility.TryPrepareAddSubmodule(
                sourceRoot,
                destination,
                out AddSubmodulePlan _,
                out string error);

            Assert.That(prepared, Is.False);
            Assert.That(error, Does.Contain("Package path already exists"));
            Assert.That(Directory.Exists(Path.Combine(cleanParent, destination)), Is.True);
            Assert.That(File.Exists(Path.Combine(cleanParent, ".gitmodules")), Is.False);
        }

        [Test]
        public void Add_RefusesSymlinkGitmodulesWithoutWritingThroughIt()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                Assert.Ignore("Creating an unprivileged symbolic link is not portable on Windows test hosts.");

            string cleanParent = CreateCleanParent("symlink-gitmodules-parent");
            string outsideFile = Path.Combine(sandboxRoot, "outside-gitmodules-target");
            const string original = "outside data must not change\n";
            File.WriteAllText(outsideFile, original);
            CommandResult linkResult = CliCommandRunner.Run(
                "/bin/ln",
                "-s -- " + GitUtility.Quote(outsideFile) + " .gitmodules",
                cleanParent,
                5000);
            if (!linkResult.IsSuccess)
                Assert.Ignore("The test host could not create a symbolic link: " + linkResult.StdErr);
            RedirectProjectRoot(cleanParent);

            bool prepared = GitUtility.TryPrepareAddSubmodule(
                sourceRoot,
                "Packages/com.example.second-package",
                out AddSubmodulePlan _,
                out string error);

            Assert.That(prepared, Is.False);
            Assert.That(error, Does.Contain(".gitmodules"));
            Assert.That(File.ReadAllText(outsideFile), Is.EqualTo(original));
        }

        [Test]
        public void Add_RefusesLinkedProspectiveMetadataAndPreservesOutsideData()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                Assert.Ignore("Creating an unprivileged symbolic link is not portable on Windows test hosts.");

            const string destination = "Packages/com.example.linked-metadata";
            string cleanParent = CreateCleanParent("linked-metadata-parent");
            string metadataParent = Path.Combine(
                cleanParent,
                ".git",
                "modules",
                "Packages");
            Directory.CreateDirectory(metadataParent);
            string outsideDirectory = Path.Combine(
                sandboxRoot,
                "outside-metadata-target");
            Directory.CreateDirectory(outsideDirectory);
            string sentinel = Path.Combine(outsideDirectory, "sentinel.txt");
            const string original = "outside metadata must not change\n";
            File.WriteAllText(sentinel, original);
            CommandResult linkResult = CliCommandRunner.Run(
                "/bin/ln",
                "-s -- " + GitUtility.Quote(outsideDirectory) + " " +
                GitUtility.Quote(Path.Combine(metadataParent, Path.GetFileName(destination))),
                cleanParent,
                5000);
            if (!linkResult.IsSuccess)
                Assert.Ignore("The test host could not create a symbolic link: " + linkResult.StdErr);
            RedirectProjectRoot(cleanParent);

            bool prepared = GitUtility.TryPrepareAddSubmodule(
                sourceRoot,
                destination,
                out AddSubmodulePlan _,
                out string error);

            Assert.That(prepared, Is.False);
            Assert.That(error, Does.Contain("metadata"));
            Assert.That(File.ReadAllText(sentinel), Is.EqualTo(original));
        }

        [Test]
        public void Add_RefusesProspectiveMetadataBelowExistingFile()
        {
            const string destination = "Packages/com.example.file-backed-metadata";
            string cleanParent = CreateCleanParent("file-backed-metadata-parent");
            string modulesPath = Path.Combine(cleanParent, ".git", "modules");
            const string original = "not a modules directory\n";
            File.WriteAllText(modulesPath, original);
            RedirectProjectRoot(cleanParent);

            bool prepared = GitUtility.TryPrepareAddSubmodule(
                sourceRoot,
                destination,
                out AddSubmodulePlan _,
                out string error);

            Assert.That(prepared, Is.False);
            Assert.That(error, Does.Contain("metadata"));
            Assert.That(File.ReadAllText(modulesPath), Is.EqualTo(original));
        }

        [Test]
        public void Remove_WhenParentIndexIsLocked_LeavesInitializedWorktreeIntact()
        {
            string indexLock = Path.Combine(parentRoot, ".git", "index.lock");
            File.WriteAllText(indexLock, "simulated concurrent Git operation\n");
            try
            {
                bool removed = GitUtility.TryRemoveSubmodule(PackagePath, out string error);

                Assert.That(removed, Is.False);
                Assert.That(error, Is.Not.Empty);
                Assert.That(File.Exists(Path.Combine(parentRoot, PackagePath, "package.json")), Is.True);
                Assert.That(Git(Path.Combine(parentRoot, PackagePath), "rev-parse --is-inside-work-tree").IsSuccess, Is.True);
            }
            finally
            {
                File.Delete(indexLock);
            }
        }

        [Test]
        public void LinkedWorktree_ResolvesRealGitDirAndCanRemoveThenReAdd()
        {
            string linkedRoot = Path.Combine(sandboxRoot, "linked");
            ExpectGit(parentRoot, "worktree add -b integration-linked \"" + linkedRoot + "\"");
            ExpectGit(
                linkedRoot,
                "-c protocol.file.allow=always submodule update --init --checkout -- \"" + PackagePath + "\"");
            RedirectProjectRoot(linkedRoot);

            Assert.That(
                GitUtility.TryResolveSubmoduleGitDir(PackagePath, out string gitDir, out string resolveError),
                Is.True,
                resolveError);
            Assert.That(Path.IsPathRooted(gitDir), Is.True);
            Assert.That(Directory.Exists(gitDir), Is.True);

            Assert.That(GitUtility.TryRemoveSubmodule(PackagePath, out string removeError), Is.True, removeError);
            ExpectGit(linkedRoot, "commit -am \"Remove package submodule\"");

            string differentSource = Path.Combine(sandboxRoot, "different-source");
            Directory.CreateDirectory(differentSource);
            InitializeRepository(differentSource);
            File.WriteAllText(Path.Combine(differentSource, "package.json"), "{\"name\":\"com.example.different\"}\n");
            ExpectGit(differentSource, "add -- package.json");
            ExpectGit(differentSource, "commit -m \"Different package\"");
            Assert.That(
                GitUtility.TryPrepareAddSubmodule(
                    differentSource,
                    PackagePath,
                    out bool _,
                    out string mismatchError),
                Is.False);
            Assert.That(mismatchError, Is.Not.Empty);

            Assert.That(
                GitUtility.TryPrepareAddSubmodule(
                    sourceRoot,
                    PackagePath,
                    out bool reuseExistingMetadata,
                    out string prepareError),
                Is.True,
                prepareError);
            Assert.That(reuseExistingMetadata, Is.True);
            Assert.That(
                GitUtility.TryBuildAddSubmoduleArguments(
                    sourceRoot,
                    PackagePath,
                    string.Empty,
                    reuseExistingMetadata,
                    out string addArguments,
                    out string addError),
                Is.True,
                addError);
            Assert.That(addArguments, Does.Contain("submodule add --force"));
            Assert.That(GitUtility.RunGit(addArguments, linkedRoot, 120000).IsSuccess, Is.True);
            Assert.That(File.Exists(Path.Combine(linkedRoot, PackagePath, "package.json")), Is.True);
        }

        [Test]
        public void Update_PreflightRejectsPlainHttpUrlFromGitmodules()
        {
            ExpectGit(
                parentRoot,
                "config --file .gitmodules submodule.\"" + PackagePath + "\".url http://example.invalid/package.git");
            ExpectGit(parentRoot, "add -- .gitmodules");
            ExpectGit(parentRoot, "commit -m \"Store unsafe submodule URL fixture\"");

            bool prepared = GitUtility.TryPrepareSubmoduleUpdate(
                PackagePath,
                out SubmoduleUpdatePlan _,
                out string error);

            Assert.That(prepared, Is.False);
            Assert.That(error, Does.Contain("plaintext HTTP and Git"));
            Assert.That(File.Exists(Path.Combine(parentRoot, PackagePath, "package.json")), Is.True);
        }

        [Test]
        public void Add_PreflightRejectsPlainHttpUrlFromStaleLocalSubmoduleConfig()
        {
            string cleanParent = CreateCleanParent("unsafe-local-add-parent");
            string destination = "Packages/com.example.unsafe-local-add";
            ExpectGit(
                cleanParent,
                "config submodule.\"" + destination + "\".url http://example.invalid/package.git");
            RedirectProjectRoot(cleanParent);

            bool prepared = GitUtility.TryPrepareAddSubmodule(
                sourceRoot,
                destination,
                out AddSubmodulePlan _,
                out string error);

            Assert.That(prepared, Is.False);
            Assert.That(error, Does.Contain("plaintext HTTP and Git"));
            Assert.That(File.Exists(Path.Combine(cleanParent, ".gitmodules")), Is.False);
            Assert.That(Directory.Exists(Path.Combine(cleanParent, destination)), Is.False);
        }

        [Test]
        public void Update_PreflightRejectsPlainHttpUrlFromLocalSubmoduleConfig()
        {
            ExpectGit(
                parentRoot,
                "config submodule.\"" + PackagePath + "\".url http://example.invalid/package.git");

            bool prepared = GitUtility.TryPrepareSubmoduleUpdate(
                PackagePath,
                out SubmoduleUpdatePlan _,
                out string error);

            Assert.That(prepared, Is.False);
            Assert.That(error, Does.Contain("plaintext HTTP and Git"));
            Assert.That(File.Exists(Path.Combine(parentRoot, PackagePath, "package.json")), Is.True);
        }

        [Test]
        public void Update_PreflightRejectsPlainHttpUrlFromWorktreeOrigin()
        {
            ExpectGit(
                Path.Combine(parentRoot, PackagePath),
                "remote set-url origin http://example.invalid/package.git");

            bool prepared = GitUtility.TryPrepareSubmoduleUpdate(
                PackagePath,
                out SubmoduleUpdatePlan _,
                out string error);

            Assert.That(prepared, Is.False);
            Assert.That(error, Does.Contain("plaintext HTTP and Git"));
            Assert.That(File.Exists(Path.Combine(parentRoot, PackagePath, "package.json")), Is.True);
        }

        [Test]
        public void Initialize_PreflightRejectsPlainHttpUrlBeforeGitCanClone()
        {
            ExpectGit(
                parentRoot,
                "config --file .gitmodules submodule.\"" + PackagePath + "\".url http://example.invalid/package.git");
            ExpectGit(parentRoot, "add -- .gitmodules");
            ExpectGit(parentRoot, "commit -m \"Store unsafe initialization URL fixture\"");
            ExpectGit(parentRoot, "submodule deinit -f -- \"" + PackagePath + "\"");

            bool prepared = GitUtility.TryPrepareSubmoduleInitialization(
                PackagePath,
                out string error);

            Assert.That(prepared, Is.False);
            Assert.That(error, Does.Contain("plaintext HTTP and Git"));
            string packageWorktree = Path.Combine(parentRoot, PackagePath);
            Assert.That(
                !Directory.Exists(packageWorktree) ||
                Directory.GetFileSystemEntries(packageWorktree).Length == 0,
                Is.True,
                "Initialization preflight must not populate the package worktree.");
        }

        [Test]
        public void AddVerification_EmptyExpectedBranchRejectsUnexpectedTrackedBranch()
        {
            ExpectGit(
                parentRoot,
                "config --file .gitmodules submodule.\"" + PackagePath + "\".branch main");
            ExpectGit(parentRoot, "add -- .gitmodules");
            var plan = new AddSubmodulePlan { Path = PackagePath };

            bool verified = GitUtility.TryVerifyAddedSubmodule(
                plan,
                sourceRoot,
                string.Empty,
                out string error);

            Assert.That(verified, Is.False);
            Assert.That(error, Does.Contain("unexpectedly registered a branch"));
        }

        [Test]
        public void Update_BranchDotTracksCurrentParentBranchSafely()
        {
            string parentBranch = ExpectGit(parentRoot, "symbolic-ref --quiet --short HEAD").StdOut.Trim();
            string sourceBranch = ExpectGit(sourceRoot, "symbolic-ref --quiet --short HEAD").StdOut.Trim();
            if (!string.Equals(sourceBranch, parentBranch, StringComparison.Ordinal))
                ExpectGit(sourceRoot, "branch -M " + GitUtility.Quote(parentBranch));

            string expectedTarget = ExpectGit(sourceRoot, "rev-parse HEAD").StdOut.Trim();
            ExpectGit(
                parentRoot,
                "config --file .gitmodules submodule.\"" + PackagePath + "\".branch .");
            ExpectGit(parentRoot, "add -- .gitmodules");
            ExpectGit(parentRoot, "commit -m \"Track the parent branch name\"");

            Assert.That(
                GitUtility.TryPrepareSubmoduleUpdate(
                    PackagePath,
                    sourceRoot,
                    ".",
                    out _,
                    out string prepareError),
                Is.True,
                prepareError);
            Assert.That(
                GitUtility.RunGit(
                    GitUtility.BuildFetchSubmoduleArguments(PackagePath),
                    parentRoot,
                    30000).IsSuccess,
                Is.True);

            bool resolved = GitUtility.TryResolveSubmoduleRemoteTarget(
                PackagePath,
                ".",
                sourceRoot,
                out string targetCommit,
                out string targetLabel,
                out string error);

            Assert.That(resolved, Is.True, error);
            Assert.That(targetLabel, Is.EqualTo(parentBranch));
            Assert.That(targetCommit, Is.EqualTo(expectedTarget));
        }

        [Test]
        public void Update_BranchDotPreciselyRejectsDetachedParent()
        {
            ExpectGit(
                parentRoot,
                "config --file .gitmodules submodule.\"" + PackagePath + "\".branch .");
            ExpectGit(parentRoot, "add -- .gitmodules");
            ExpectGit(parentRoot, "commit -m \"Track the parent branch name\"");
            ExpectGit(parentRoot, "checkout --detach");

            bool resolved = GitUtility.TryResolveSubmoduleRemoteTarget(
                PackagePath,
                ".",
                sourceRoot,
                out _,
                out _,
                out string error);

            Assert.That(resolved, Is.False);
            Assert.That(error, Does.Contain("branch = ."));
            Assert.That(error, Does.Contain("detached"));
        }

        [Test]
        public void Update_PreflightFailsClosedWhenGitmodulesOutputIsTruncated()
        {
            ICommandRunner inner = CliCommandRunner.CurrentRunner;
            CliCommandRunner.CurrentRunner = new SingleCommandMutationRunner(
                inner,
                spec => string.Equals(
                    spec.Arguments,
                    "config --no-includes --null --file .gitmodules --list",
                    StringComparison.Ordinal),
                (_, result) => result.StdOutTruncated = true);

            bool prepared = GitUtility.TryPrepareSubmoduleUpdate(
                PackagePath,
                out SubmoduleUpdatePlan _,
                out string error);

            Assert.That(prepared, Is.False);
            Assert.That(error, Does.Contain("truncated Git output"));
            Assert.That(File.Exists(Path.Combine(parentRoot, PackagePath, "package.json")), Is.True);
        }

        [Test]
        public void SetBranch_StructuredOutcomeSucceedsOnlyAfterVerifiedPostcondition()
        {
            bool changed = GitUtility.TrySetSubmoduleBranch(
                PackagePath,
                "release/test",
                out string error,
                out GitOperationCompletionOutcome outcome);

            Assert.That(changed, Is.True, error);
            Assert.That(outcome, Is.EqualTo(GitOperationCompletionOutcome.Succeeded));
            Assert.That(
                ExpectGit(
                    parentRoot,
                    "config --file .gitmodules --get submodule.\"" + PackagePath + "\".branch")
                    .StdOut
                    .Trim(),
                Is.EqualTo("release/test"));
        }

        [Test]
        public void SetBranch_PreflightFailureReportsRolledBackOutcome()
        {
            File.AppendAllText(Path.Combine(parentRoot, ".gitmodules"), "\n# unrelated local edit\n");

            bool changed = GitUtility.TrySetSubmoduleBranch(
                PackagePath,
                "release/test",
                out string error,
                out GitOperationCompletionOutcome outcome);

            Assert.That(changed, Is.False);
            Assert.That(error, Does.Contain("unrelated changes"));
            Assert.That(outcome, Is.EqualTo(GitOperationCompletionOutcome.FailedButRolledBack));
        }

        [Test]
        public void SetBranch_PostconditionFailureReportsUnsafeOutcome()
        {
            ICommandRunner inner = CliCommandRunner.CurrentRunner;
            CliCommandRunner.CurrentRunner = new SingleCommandMutationRunner(
                inner,
                spec => (spec.Arguments ?? string.Empty).StartsWith(
                    "config --file .gitmodules --get ",
                    StringComparison.Ordinal),
                (_, result) => result.StdOut = "different-branch\n");

            bool changed = GitUtility.TrySetSubmoduleBranch(
                PackagePath,
                "release/test",
                out string error,
                out GitOperationCompletionOutcome outcome);

            Assert.That(changed, Is.False);
            Assert.That(error, Does.Contain("postcondition"));
            Assert.That(outcome, Is.EqualTo(GitOperationCompletionOutcome.FailedUnsafe));
        }

        [Test]
        public void GetSubmodules_DoesNotMaterializePhantomRecordsFromNewlineValues()
        {
            const string phantomPath = "Packages/com.example.phantom-package";
            string gitmodulesPath = Path.Combine(parentRoot, ".gitmodules");
            File.AppendAllText(
                gitmodulesPath,
                "\n[submodule \"newline-carrier\"]\n" +
                "\tpath = \"ignored\\nsubmodule.phantom.path=" + phantomPath +
                "\\nsubmodule.phantom.url=https://example.invalid/phantom.git\"\n" +
                "\turl = https://example.invalid/carrier.git\n");

            bool loaded = GitUtility.TryGetSubmodules(out List<GitPackageInfo> packages, out string error);

            Assert.That(loaded, Is.True, error);
            Assert.That(packages.Exists(package => package.Path == phantomPath), Is.False);
            Assert.That(packages.Exists(package => package.Path == PackagePath), Is.True);
        }

        [Test]
        public void GetSubmodules_FailsClosedOnDuplicateStructuralKeys()
        {
            string gitmodulesPath = Path.Combine(parentRoot, ".gitmodules");
            File.AppendAllText(
                gitmodulesPath,
                "\n[submodule \"" + PackagePath + "\"]\n" +
                "\tpath = " + PackagePath + "\n");

            bool loaded = GitUtility.TryGetSubmodules(out _, out string error);

            Assert.That(loaded, Is.False);
            Assert.That(error, Does.Contain("duplicate configuration key"));
        }

        [Test]
        public void Update_PreflightFailsClosedOnAmbiguousDuplicatePackagePath()
        {
            string gitmodulesPath = Path.Combine(parentRoot, ".gitmodules");
            File.AppendAllText(
                gitmodulesPath,
                "\n[submodule \"duplicate-registration\"]\n" +
                "\tpath = " + PackagePath + "\n" +
                "\turl = https://example.invalid/duplicate.git\n");
            ExpectGit(parentRoot, "add -- .gitmodules");
            ExpectGit(parentRoot, "commit -m \"Add ambiguous duplicate registration\"");

            bool prepared = GitUtility.TryPrepareSubmoduleUpdate(
                PackagePath,
                out SubmoduleUpdatePlan _,
                out string error);

            Assert.That(prepared, Is.False);
            Assert.That(error, Does.Contain("more than one submodule"));
            Assert.That(File.Exists(Path.Combine(parentRoot, PackagePath, "package.json")), Is.True);
        }

        [Test]
        public void Remove_DoesNotReportSuccessWhenWorktreeStillExists()
        {
            string worktree = Path.Combine(parentRoot, PackagePath);
            bool removed;
            string error;
            GitOperationCompletionOutcome outcome;
            using (GitUtility.OverrideBeforeGitModulesIndexCompareAndSwapForTests(
                       _ => Directory.CreateDirectory(worktree)))
            {
                removed = GitUtility.TryRemoveSubmodule(
                    PackagePath,
                    out error,
                    out outcome);
            }

            Assert.That(removed, Is.False);
            Assert.That(outcome, Is.EqualTo(GitOperationCompletionOutcome.FailedUnsafe));
            Assert.That(error, Does.Contain("new filesystem entry"));
            Assert.That(Directory.Exists(worktree), Is.True);
        }

        [Test]
        public void Remove_ConcurrentGitmodulesRecreationIsPreservedAndStopsMutation()
        {
            string gitmodulesPath = Path.Combine(parentRoot, ".gitmodules");
            const string marker = "# concurrent recreated gitmodules\n";
            bool removed;
            string error;
            GitOperationCompletionOutcome outcome;
            using (GitUtility.OverrideBeforeGitModulesIndexCompareAndSwapForTests(
                       _ => File.WriteAllText(gitmodulesPath, marker)))
            {
                removed = GitUtility.TryRemoveSubmodule(
                    PackagePath,
                    out error,
                    out outcome);
            }

            Assert.That(removed, Is.False);
            Assert.That(outcome, Is.EqualTo(GitOperationCompletionOutcome.FailedUnsafe));
            Assert.That(error, Does.Contain("new .gitmodules filesystem entry"));
            Assert.That(File.ReadAllText(gitmodulesPath), Does.Contain(marker.Trim()));
        }

        [Test]
        public void Remove_GitmodulesReplacementBetweenVerificationAndCleanup_IsQuarantined()
        {
            string cleanParent = CreateCleanParent("gitmodules-cleanup-race-parent");
            RedirectProjectRoot(cleanParent);
            Assert.That(
                GitUtility.TryAddSubmodule(sourceRoot, PackagePath, string.Empty, out string addError),
                Is.True,
                addError);
            Assert.That(
                GitUtility.TryAssessSubmoduleRemoval(
                    PackagePath,
                    out SubmoduleRemovalAssessment confirmed,
                    out string assessmentError),
                Is.True,
                assessmentError);
            const string marker = "concurrent replacement bytes\n";
            using (GitUtility.OverrideBeforeGitModulesCleanupMoveForTests(path =>
                   {
                       if (File.Exists(path))
                           File.Delete(path);
                       File.WriteAllText(path, marker);
                   }))
            {
                bool removed = GitUtility.TryRemoveSubmodule(
                    PackagePath,
                    confirmed,
                    true,
                    out string error,
                    out GitOperationCompletionOutcome outcome);

                Assert.That(removed, Is.False);
                Assert.That(outcome, Is.EqualTo(GitOperationCompletionOutcome.FailedUnsafe));
                Assert.That(error, Does.Contain("preserved at"));
            }

            string recoveryDirectory = Path.Combine(
                cleanParent,
                "Library",
                "GitSubmoduleManager",
                "Recovery",
                "GitModulesCleanup");
            string[] preservedFiles = Directory.GetFiles(
                recoveryDirectory,
                "*.gitmodules",
                SearchOption.TopDirectoryOnly);
            Assert.That(preservedFiles, Has.Length.EqualTo(1));
            Assert.That(File.ReadAllText(preservedFiles[0]), Is.EqualTo(marker));
            Assert.That(File.Exists(Path.Combine(cleanParent, ".gitmodules")), Is.False);
        }

        [Test]
        public void Remove_LastSubmoduleExactIndexCasPreservesConcurrentStagedGitmodules()
        {
            string cleanParent = CreateCleanParent(
                "last-submodule-index-cas-race-parent");
            RedirectProjectRoot(cleanParent);
            Assert.That(
                GitUtility.TryAddSubmodule(
                    sourceRoot,
                    PackagePath,
                    string.Empty,
                    out string addError),
                Is.True,
                addError);
            Assert.That(
                GitUtility.TryAssessSubmoduleRemoval(
                    PackagePath,
                    out SubmoduleRemovalAssessment confirmed,
                    out string assessmentError),
                Is.True,
                assessmentError);

            const string concurrentContents =
                "# concurrent staged last-submodule replacement\n";
            string concurrentSource = Path.Combine(
                sandboxRoot,
                "last-submodule-concurrent.gitmodules");
            File.WriteAllText(concurrentSource, concurrentContents);
            string concurrentBlob = ExpectGit(
                    cleanParent,
                    "hash-object -w -- " + GitUtility.Quote(concurrentSource))
                .StdOut.Trim();

            using (GitUtility.OverrideBeforeGitModulesIndexCompareAndSwapForTests(
                       _ => ExpectGit(
                           cleanParent,
                           "update-index --add --cacheinfo 100644," +
                           concurrentBlob + ",.gitmodules")))
            {
                bool removed = GitUtility.TryRemoveSubmodule(
                    PackagePath,
                    confirmed,
                    true,
                    out string error,
                    out GitOperationCompletionOutcome outcome);

                Assert.That(removed, Is.False);
                Assert.That(
                    outcome,
                    Is.EqualTo(GitOperationCompletionOutcome.FailedUnsafe));
                Assert.That(error, Does.Contain("concurrent staged data"));
            }

            Assert.That(
                ExpectGit(cleanParent, "rev-parse :.gitmodules").StdOut.Trim(),
                Is.EqualTo(concurrentBlob));
            Assert.That(
                ExpectGit(cleanParent, "show :.gitmodules").StdOut,
                Is.EqualTo(concurrentContents.TrimEnd('\r', '\n')));
            Assert.That(
                File.Exists(Path.Combine(cleanParent, ".gitmodules")),
                Is.False,
                "The staged replacement must remain index-only after the verified worktree entry was quarantined.");
        }

        [Test]
        public void Remove_ExactIndexCasPreservesConcurrentGitlinkAtomically()
        {
            string originalGitModulesBlob = ExpectGit(
                    parentRoot,
                    "rev-parse :.gitmodules")
                .StdOut.Trim();
            string concurrentGitlink = ExpectGit(parentRoot, "rev-parse HEAD")
                .StdOut.Trim();

            using (GitUtility.OverrideBeforeGitModulesIndexCompareAndSwapForTests(
                       _ => ExpectGit(
                           parentRoot,
                           "update-index --add --cacheinfo 160000," +
                           concurrentGitlink + "," + PackagePath)))
            {
                bool removed = GitUtility.TryRemoveSubmodule(
                    PackagePath,
                    out string error,
                    out GitOperationCompletionOutcome outcome);

                Assert.That(removed, Is.False);
                Assert.That(
                    outcome,
                    Is.EqualTo(GitOperationCompletionOutcome.FailedUnsafe));
                Assert.That(error, Does.Contain("concurrent staged data"));
            }

            Assert.That(
                ExpectGit(
                    parentRoot,
                    "ls-files --stage -- \"" + PackagePath + "\"")
                .StdOut,
                Does.Contain(concurrentGitlink));
            Assert.That(
                ExpectGit(parentRoot, "rev-parse :.gitmodules").StdOut.Trim(),
                Is.EqualTo(originalGitModulesBlob),
                "The one-lock patch must reject both changes atomically.");
        }

        [Test]
        public void Remove_LatePackageWriterAfterQuarantineIsPreserved()
        {
            string packagePath = Path.Combine(parentRoot, PackagePath);
            string lateFile = Path.Combine(packagePath, "late-writer.txt");
            string originalGitlink = ExpectGit(
                    parentRoot,
                    "rev-parse :" + PackagePath)
                .StdOut.Trim();
            string originalGitModulesBlob = ExpectGit(
                    parentRoot,
                    "rev-parse :.gitmodules")
                .StdOut.Trim();

            using (GitUtility.OverrideBeforeGitModulesIndexCompareAndSwapForTests(
                       _ =>
                       {
                           Directory.CreateDirectory(packagePath);
                           File.WriteAllText(lateFile, "late data must survive\n");
                       }))
            {
                bool removed = GitUtility.TryRemoveSubmodule(
                    PackagePath,
                    out string error,
                    out GitOperationCompletionOutcome outcome);

                Assert.That(removed, Is.False);
                Assert.That(
                    outcome,
                    Is.EqualTo(GitOperationCompletionOutcome.FailedUnsafe));
                Assert.That(error, Does.Contain("new filesystem entry"));
            }

            Assert.That(File.ReadAllText(lateFile), Is.EqualTo("late data must survive\n"));
            Assert.That(
                ExpectGit(parentRoot, "rev-parse :" + PackagePath).StdOut.Trim(),
                Is.EqualTo(originalGitlink));
            Assert.That(
                ExpectGit(parentRoot, "rev-parse :.gitmodules").StdOut.Trim(),
                Is.EqualTo(originalGitModulesBlob));
            string recoveryRoot = Path.Combine(
                parentRoot,
                "Library",
                "GitSubmoduleManager",
                "Recovery");
            Assert.That(
                Directory.GetFiles(
                    recoveryRoot,
                    "package.json",
                    SearchOption.AllDirectories),
                Has.Length.EqualTo(1),
                "The original package worktree must remain in Recovery.");
        }

        [Test]
        public void Remove_CancellationAfterExactIndexApplyCompletesCriticalSection()
        {
            using var cancellation = new CancellationTokenSource();
            using (GitUtility.OverrideAfterExactSubmoduleIndexApplyForTests(
                       _ => cancellation.Cancel()))
            {
                bool removed = GitUtility.TryRemoveSubmodule(
                    PackagePath,
                    null,
                    false,
                    out string error,
                    out GitOperationCompletionOutcome outcome,
                    cancellation.Token);

                Assert.That(cancellation.IsCancellationRequested, Is.True);
                Assert.That(removed, Is.True, error);
                Assert.That(
                    outcome,
                    Is.EqualTo(GitOperationCompletionOutcome.Succeeded));
                Assert.That(error, Does.Contain("preserved at"));
            }

            Assert.That(
                Git(parentRoot, "ls-files --error-unmatch -- \"" + PackagePath + "\"")
                    .IsSuccess,
                Is.False);
            Assert.That(Directory.Exists(Path.Combine(parentRoot, PackagePath)), Is.False);
        }

        [Test]
        public void Remove_LatePostconditionFailureRetainsExactRecoveryInstructions()
        {
            string latePath = Path.Combine(parentRoot, PackagePath, "post-apply.txt");
            using (GitUtility.OverrideAfterExactSubmoduleRemovalForTests(path =>
                   {
                       Directory.CreateDirectory(path);
                       File.WriteAllText(latePath, "post-apply data\n");
                   }))
            {
                bool removed = GitUtility.TryRemoveSubmodule(
                    PackagePath,
                    out string error,
                    out GitOperationCompletionOutcome outcome);

                Assert.That(removed, Is.False);
                Assert.That(
                    outcome,
                    Is.EqualTo(GitOperationCompletionOutcome.FailedUnsafe));
                Assert.That(error, Does.Contain("preserved at"));
                Assert.That(error, Does.Contain("GitModulesCleanup"));
            }

            Assert.That(
                File.ReadAllText(latePath),
                Is.EqualTo("post-apply data\n"));
        }

        [Test]
        public void Remove_LateRegularGitmodulesWriterFailsClosingProofAndIsPreserved()
        {
            string gitModulesPath = Path.Combine(parentRoot, ".gitmodules");
            const string lateContents = "# late post-removal writer\n";
            using (GitUtility.OverrideAfterExactSubmoduleRemovalForTests(
                       _ => File.WriteAllText(gitModulesPath, lateContents)))
            {
                bool removed = GitUtility.TryRemoveSubmodule(
                    PackagePath,
                    out string error,
                    out GitOperationCompletionOutcome outcome);

                Assert.That(removed, Is.False);
                Assert.That(
                    outcome,
                    Is.EqualTo(GitOperationCompletionOutcome.FailedUnsafe));
                Assert.That(error, Does.Contain("preserved at"));
                Assert.That(error, Does.Contain("GitModulesCleanup"));
            }

            Assert.That(File.ReadAllText(gitModulesPath), Is.EqualTo(lateContents));
        }

        [Test]
        public void Remove_LateSymlinkedGitmodulesFailsClosingProofWithoutFollowingIt()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                Assert.Ignore("Creating an unprivileged symbolic link is not portable on Windows test hosts.");

            string gitModulesPath = Path.Combine(parentRoot, ".gitmodules");
            string externalPath = Path.Combine(
                sandboxRoot,
                "late-removal-external-gitmodules");
            const string externalContents = "# external registration-free bytes\n";
            File.WriteAllText(externalPath, externalContents);
            using (GitUtility.OverrideAfterExactSubmoduleRemovalForTests(_ =>
                   {
                       if (File.Exists(gitModulesPath))
                           File.Delete(gitModulesPath);
                       CommandResult linkResult = CliCommandRunner.Run(
                           "/bin/ln",
                           "-s -- " + GitUtility.Quote(externalPath) + " .gitmodules",
                           parentRoot,
                           5000);
                       Assert.That(linkResult.IsSuccess, Is.True, linkResult.StdErr);
                   }))
            {
                bool removed = GitUtility.TryRemoveSubmodule(
                    PackagePath,
                    out string error,
                    out GitOperationCompletionOutcome outcome);

                Assert.That(removed, Is.False);
                Assert.That(
                    outcome,
                    Is.EqualTo(GitOperationCompletionOutcome.FailedUnsafe));
                Assert.That(error, Does.Contain("regular, non-symbolic-link"));
                Assert.That(error, Does.Contain("preserved at"));
                Assert.That(error, Does.Contain("GitModulesCleanup"));
            }

            Assert.That(File.ReadAllText(externalPath), Is.EqualTo(externalContents));
            Assert.That(
                (File.GetAttributes(gitModulesPath) & FileAttributes.ReparsePoint) != 0,
                Is.True);
        }

        private sealed class AddTreeModeHeadSwapRunner : ICommandRunner
        {
            private readonly ICommandRunner inner;
            private readonly string parentRoot;
            private readonly string packagePath;
            private readonly string expectedPackageName;
            private readonly string regularCommit;
            private readonly string inspectedCommit;
            private bool inspectedAdd;

            internal AddTreeModeHeadSwapRunner(
                ICommandRunner inner,
                string parentRoot,
                string packagePath,
                string expectedPackageName,
                string regularCommit,
                string inspectedCommit)
            {
                this.inner = inner;
                this.parentRoot = parentRoot;
                this.packagePath = packagePath;
                this.expectedPackageName = expectedPackageName;
                this.regularCommit = regularCommit;
                this.inspectedCommit = inspectedCommit;
            }

            internal bool MaterializedRegularManifest { get; private set; }
            internal bool SwappedHeadDuringTreeRead { get; private set; }
            internal string TreeCommandArguments { get; private set; } = string.Empty;

            public CommandResult Run(CommandSpec spec)
            {
                string arguments = spec?.Arguments ?? string.Empty;
                if (!SwappedHeadDuringTreeRead &&
                    arguments.IndexOf(
                        "ls-tree -z --full-tree ",
                        StringComparison.Ordinal) >= 0 &&
                    arguments.IndexOf(
                        "-- package.json package.json.meta",
                        StringComparison.Ordinal) >= 0)
                {
                    CheckoutAndStage(regularCommit);
                    TreeCommandArguments = arguments;
                    CommandResult treeResult = inner.Run(spec);
                    CheckoutAndStage(inspectedCommit);
                    SwappedHeadDuringTreeRead = true;
                    return treeResult;
                }

                CommandResult result = inner.Run(spec);
                if (inspectedAdd || result == null || !result.IsSuccess ||
                    arguments.IndexOf(
                        "submodule add",
                        StringComparison.Ordinal) < 0)
                {
                    return result;
                }

                inspectedAdd = true;
                string packageRoot = Path.Combine(parentRoot, packagePath);
                CommandResult configResult = RunInnerGit(
                    packageRoot,
                    "config",
                    "core.symlinks",
                    "false");
                Assert.That(configResult.IsSuccess, Is.True, configResult.StdErr);

                string manifestPath = Path.Combine(packageRoot, "package.json");
                File.Delete(manifestPath);
                CommandResult checkoutResult = RunInnerGit(
                    packageRoot,
                    "checkout",
                    "--",
                    "package.json");
                Assert.That(checkoutResult.IsSuccess, Is.True, checkoutResult.StdErr);
                MaterializedRegularManifest =
                    File.Exists(manifestPath) &&
                    (File.GetAttributes(manifestPath) &
                     FileAttributes.ReparsePoint) == 0 &&
                    GitUtility.TryReadValidPackageManifest(
                        manifestPath,
                        out string declaredName,
                        out _) &&
                    string.Equals(
                        declaredName,
                        expectedPackageName,
                        StringComparison.Ordinal);
                return result;
            }

            private void CheckoutAndStage(string commit)
            {
                string packageRoot = Path.Combine(parentRoot, packagePath);
                CommandResult checkoutResult = RunInnerGit(
                    packageRoot,
                    "checkout",
                    "--detach",
                    commit);
                Assert.That(checkoutResult.IsSuccess, Is.True, checkoutResult.StdErr);
                CommandResult stageResult = RunInnerGit(
                    parentRoot,
                    "add",
                    "--",
                    packagePath);
                Assert.That(stageResult.IsSuccess, Is.True, stageResult.StdErr);
            }

            private CommandResult RunInnerGit(
                string workingDirectory,
                params string[] arguments)
            {
                return inner.Run(new CommandSpec
                {
                    FileName = GitUtility.GitExecutable,
                    ArgumentList = arguments,
                    WorkingDirectory = workingDirectory,
                    TimeoutMs = 30000,
                    TerminationScope = CommandTerminationScope.CompleteProcessTree
                });
            }
        }

        private sealed class SingleCommandMutationRunner : ICommandRunner
        {
            private readonly ICommandRunner inner;
            private readonly Func<CommandSpec, bool> predicate;
            private readonly Action<CommandSpec, CommandResult> mutation;
            private bool mutated;

            internal SingleCommandMutationRunner(
                ICommandRunner inner,
                Func<CommandSpec, bool> predicate,
                Action<CommandSpec, CommandResult> mutation)
            {
                this.inner = inner;
                this.predicate = predicate;
                this.mutation = mutation;
            }

            public CommandResult Run(CommandSpec spec)
            {
                CommandResult result = inner.Run(spec);
                if (!mutated && result != null && result.IsSuccess && predicate(spec))
                {
                    mutated = true;
                    mutation(spec, result);
                }

                return result;
            }
        }

        private void InitializeRepository(string path)
        {
            ExpectGit(path, "init");
            ExpectGit(path, "config user.name \"Git Submodule Manager Tests\"");
            ExpectGit(path, "config user.email \"tests@example.invalid\"");
        }

        private static byte[] EncodeUtf8WithBom(string value)
        {
            var encoding = new UTF8Encoding(true);
            byte[] preamble = encoding.GetPreamble();
            byte[] contents = encoding.GetBytes(value ?? string.Empty);
            var result = new byte[preamble.Length + contents.Length];
            Buffer.BlockCopy(preamble, 0, result, 0, preamble.Length);
            Buffer.BlockCopy(contents, 0, result, preamble.Length, contents.Length);
            return result;
        }

        private string CreateCleanParent(string directoryName)
        {
            string cleanParent = Path.Combine(sandboxRoot, directoryName);
            Directory.CreateDirectory(cleanParent);
            InitializeRepository(cleanParent);
            File.WriteAllText(Path.Combine(cleanParent, "README.md"), "clean parent\n");
            ExpectGit(cleanParent, "add -- README.md");
            ExpectGit(cleanParent, "commit -m \"Initial clean parent\"");
            return cleanParent;
        }

        private string CreateSourceRepository(string directoryName, string packageName)
        {
            string repository = Path.Combine(sandboxRoot, directoryName);
            Directory.CreateDirectory(repository);
            InitializeRepository(repository);
            File.WriteAllText(
                Path.Combine(repository, "package.json"),
                "{\"name\":\"" + packageName + "\",\"version\":\"1.0.0\"}\n");
            ExpectGit(repository, "add -- package.json");
            ExpectGit(repository, "commit -m \"Initial package\"");
            return repository;
        }

        private static void CaptureFailedAddRollbackEvidence(
            string repositoryRoot,
            AddSubmodulePlan plan)
        {
            string expectedGitlink = ExpectGit(
                    repositoryRoot,
                    "rev-parse :" + GitUtility.Quote(plan.Path))
                .StdOut.Trim();
            Assert.That(
                GitUtility.TryCaptureFailedAddRollbackEvidence(
                    plan,
                    expectedGitlink,
                    out string evidenceError),
                Is.True,
                evidenceError);
        }

        private void RedirectProjectRoot(string root)
        {
            projectRootOverride?.Dispose();
            projectRootOverride = GitUtility.OverrideProjectRootForTests(root);
        }

        private static CommandResult ExpectGit(string workingDirectory, string arguments)
        {
            CommandResult result = Git(workingDirectory, arguments);
            Assert.That(
                result.IsSuccess,
                Is.True,
                "git " + arguments + " failed:\n" + result.StdErr + "\n" + result.StdOut);
            return result;
        }

        private static CommandResult Git(string workingDirectory, string arguments)
        {
            return CliCommandRunner.Run("git", arguments, workingDirectory, 30000);
        }

        private static void DeleteDirectoryBestEffort(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
                return;

            try
            {
                foreach (string file in Directory.GetFiles(path, "*", SearchOption.AllDirectories))
                    File.SetAttributes(file, FileAttributes.Normal);
                Directory.Delete(path, true);
            }
            catch
            {
                // Test failures should report the operation under test, not cleanup noise.
            }
        }
    }
}
