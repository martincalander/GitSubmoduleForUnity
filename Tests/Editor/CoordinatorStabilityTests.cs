using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using NUnit.Framework;

[assembly: LevelOfParallelism(1)]

namespace MartinCalander.GitSubmoduleManager.Editor.Tests
{
    [Parallelizable(ParallelScope.None)]
    public sealed class CoordinatorStabilityTests
    {
        private ICommandRunner previousRunner;

        [SetUp]
        public void SetUp()
        {
            previousRunner = CliCommandRunner.CurrentRunner;
        }

        [TearDown]
        public void TearDown()
        {
            CliCommandRunner.CurrentRunner = previousRunner;
        }

        [Test]
        public void BuildApiArguments_AlwaysPinsPublicGitHubHost()
        {
            string arguments = GitHubUtility.BuildApiArguments("user --jq .login");

            Assert.That(arguments, Is.EqualTo("api user --jq .login --hostname github.com"));
        }

        [Test]
        public void PersonalRepositoryPage_OnlyRequestsRepositoriesOwnedByTheUser()
        {
            var runner = new RecordingRunner(_ => Success("[]"));
            CliCommandRunner.CurrentRunner = runner;
            using var coordinator = new DiscoveryCoordinator();

            coordinator.LoadInitialPage();
            TickUntil(coordinator, () => runner.CallCount == 1);

            string arguments = runner.SnapshotArguments().Single();
            Assert.That(arguments, Does.Contain("user/repos?affiliation=owner&"));
            Assert.That(arguments, Does.Contain("--hostname github.com"));
            coordinator.Dispose();
        }

        [Test]
        public void ExactFullRepositoryPage_UsesLinkMetadataInsteadOfOfferingAPhantomPage()
        {
            string response =
                "HTTP/2.0 200 OK\r\n" +
                "Content-Type: application/json\r\n" +
                "Link: <https://api.github.com/user/repos?page=1>; rel=\"first\"\r\n\r\n" +
                BuildRepositoryPageJson(50);
            var runner = new RecordingRunner(spec => IsGraphQlCall(spec)
                ? Success(BuildManifestGraphQlResponse(GetGraphQlNodeIds(spec)))
                : Success(response));
            CliCommandRunner.CurrentRunner = runner;
            using var coordinator = new DiscoveryCoordinator();

            coordinator.LoadInitialPage();
            TickUntil(coordinator, () => coordinator.DisplayedRepos.Count == 50);

            Assert.That(coordinator.HasNextPage, Is.False);
            Assert.That(
                runner.SnapshotArguments().Single(call => call.Contains("user/repos")),
                Does.Contain("--include"));
        }

        [Test]
        public void RepositoryPagination_LinkMetadataRecognizesANextPage()
        {
            const string response =
                "HTTP/2.0 200 OK\n" +
                "Link: <https://api.github.com/user/repos?page=2>; rel=\"next\"\n\n" +
                "[]";

            bool found = DiscoveryCoordinator.TryExtractPaginationMetadata(
                response,
                out string body,
                out bool hasNextPage);

            Assert.That(found, Is.True);
            Assert.That(body, Is.EqualTo("[]"));
            Assert.That(hasNextPage, Is.True);
        }

        [Test]
        public void IdentityReset_DiscardsInFlightAccountAndWaitsBeforeResolvingReplacement()
        {
            using var firstUsernameStarted = new ManualResetEventSlim(false);
            using var releaseFirstUsername = new ManualResetEventSlim(false);
            int usernameCalls = 0;
            var runner = new RecordingRunner(spec =>
            {
                string arguments = GetArguments(spec);
                if (arguments.Contains("api user --jq"))
                {
                    int call = Interlocked.Increment(ref usernameCalls);
                    if (call == 1)
                    {
                        firstUsernameStarted.Set();
                        releaseFirstUsername.Wait(TimeSpan.FromSeconds(2));
                        return Success("previous-account");
                    }

                    return Success("replacement-account");
                }

                if (arguments.Contains("user/orgs"))
                    return Success("replacement-org");

                return Success(string.Empty);
            });
            CliCommandRunner.CurrentRunner = runner;
            using var coordinator = new DiscoveryCoordinator();

            try
            {
                coordinator.EnsureUsername();
                Assert.That(firstUsernameStarted.Wait(TimeSpan.FromSeconds(2)), Is.True);

                coordinator.ResetGitHubIdentityState();
                coordinator.EnsureUsername();

                Assert.That(coordinator.Username, Is.Empty);
                Assert.That(coordinator.SelectedOwner, Is.Empty);
                Assert.That(coordinator.Organizations, Is.Empty);
                Assert.That(coordinator.DisplayedRepos, Is.Empty);
                Assert.That(coordinator.CurrentPage, Is.EqualTo(1));
                Assert.That(coordinator.HasNextPage, Is.False);
                Assert.That(Volatile.Read(ref usernameCalls), Is.EqualTo(1));

                releaseFirstUsername.Set();
                TickUntil(coordinator, () =>
                    coordinator.Username == "replacement-account" && coordinator.OrgsLoaded);

                Assert.That(coordinator.Username, Is.EqualTo("replacement-account"));
                Assert.That(coordinator.SelectedOwner, Is.EqualTo("replacement-account"));
                Assert.That(coordinator.Organizations, Is.EqualTo(new[] { "replacement-org" }));
                Assert.That(coordinator.Username, Is.Not.EqualTo("previous-account"));
                Assert.That(Volatile.Read(ref usernameCalls), Is.EqualTo(2));
            }
            finally
            {
                releaseFirstUsername.Set();
            }
        }

        [Test]
        public void IdentityReset_RestartsPackageManifestValidationForReplacementAccount()
        {
            int graphQlCalls = 0;
            var runner = new RecordingRunner(spec =>
            {
                string arguments = GetArguments(spec);
                if (arguments.Contains("user/repos"))
                    return Success(BuildRepositoryPageJson(1));
                if (IsGraphQlCall(spec))
                {
                    Interlocked.Increment(ref graphQlCalls);
                    return Success(BuildManifestGraphQlResponse(GetGraphQlNodeIds(spec)));
                }

                return Success(string.Empty);
            });
            CliCommandRunner.CurrentRunner = runner;
            using var coordinator = new DiscoveryCoordinator();

            coordinator.ResetGitHubIdentityState();
            coordinator.LoadInitialPage();
            TickUntil(coordinator, () =>
                coordinator.DisplayedRepos.Count == 1 &&
                !coordinator.IsValidatingPackageManifests);

            Assert.That(Volatile.Read(ref graphQlCalls), Is.EqualTo(1));
            Assert.That(
                coordinator.DisplayedRepos.Single().ManifestState,
                Is.EqualTo(PackageManifestState.Valid));
        }

        [Test]
        public void OrganizationFailure_IsAWarningAndPersonalRepositoriesRemainUsable()
        {
            var runner = new RecordingRunner(spec =>
            {
                string arguments = GetArguments(spec);
                if (arguments.Contains("api user --jq"))
                    return Success("authenticated-owner");
                if (arguments.Contains("user/orgs"))
                {
                    return new CommandResult
                    {
                        ExitCode = 1,
                        StdErr = "organization API unavailable",
                        TerminationConfirmed = true
                    };
                }
                if (arguments.Contains("user/repos"))
                    return Success(BuildRepositoryPageJson(1));

                return Success(string.Empty);
            });
            CliCommandRunner.CurrentRunner = runner;
            using var coordinator = new DiscoveryCoordinator();

            coordinator.EnsureUsername();
            coordinator.LoadInitialPage();
            TickUntil(
                coordinator,
                () => coordinator.DisplayedRepos.Count == 1 &&
                      !string.IsNullOrWhiteSpace(coordinator.WarningMessage));

            Assert.That(coordinator.ErrorMessage, Is.Empty);
            Assert.That(coordinator.WarningMessage, Does.Contain("Failed to load GitHub organizations"));
            Assert.That(coordinator.OrgsLoaded, Is.True);
        }

        [Test]
        public void UiDiagnostics_AreRedactedAndCapped()
        {
            const string secret = "super-secret-token";
            var result = new CommandResult
            {
                ExitCode = 1,
                StdOut = string.Empty,
                StdErr = "https://user:" + secret + "@github.com/owner/repo.git " +
                         new string('x', 12000)
            };

            string error = GitHubUtility.BuildRepoListError("GitHub request failed", result);

            Assert.That(error.Length, Is.LessThanOrEqualTo(GitHubUtility.MaxUiDiagnosticCharacters));
            Assert.That(error, Does.Not.Contain(secret));
            Assert.That(error, Does.EndWith("… [truncated]"));
        }

        [Test]
        public void MalformedRepositoryDiagnostics_AreRedactedAndCapped()
        {
            const string secret = "malformed-secret-token";
            var exception = new FormatException(
                "https://user:" + secret + "@github.com/owner/repo.git " +
                new string('x', 12000));

            string error = DiscoveryCoordinator.BuildMalformedRepositoryDataError(exception);

            Assert.That(error, Does.StartWith("GitHub returned malformed repository data"));
            Assert.That(error.Length, Is.LessThanOrEqualTo(GitHubUtility.MaxUiDiagnosticCharacters));
            Assert.That(error, Does.Not.Contain(secret));
            Assert.That(error, Does.EndWith("… [truncated]"));
        }

        [Test]
        public void SearchPaths_PreferTrustedAbsolutePathsAndRejectImplicitCurrentDirectory()
        {
            string trusted = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "git-submodule-manager-trusted-bin"));
            string inherited = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "git-submodule-manager-inherited-bin"));
            string inheritedPath = string.Join(
                Path.PathSeparator.ToString(),
                new[] { "relative-bin", string.Empty, inherited, "." });

            IReadOnlyList<string> paths = ProcessCommandRunner.BuildSearchPaths(
                inheritedPath,
                new[] { string.Empty, "relative-trusted-bin", trusted });

            CollectionAssert.AreEqual(new[] { trusted, inherited }, paths);
        }

        [Test]
        public void BoundedOutput_FloodKeepsRecentTailWithoutGrowing()
        {
            var buffer = new BoundedTextBuffer(128);
            for (int index = 0; index < 10000; index++)
                buffer.Append(index.ToString("D5") + "|");

            string snapshot = buffer.GetSnapshot();

            Assert.That(buffer.IsTruncated, Is.True);
            Assert.That(snapshot, Does.EndWith("09999|"));
            Assert.That(snapshot.Length, Is.LessThan(256));
        }

        [Test]
        public void ForcedTimeout_DoesNotClaimProcessTreeTerminationWasProven()
        {
            string executable;
            IReadOnlyList<string> arguments;
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                executable = Environment.GetEnvironmentVariable("ComSpec") ??
                             Path.Combine(
                                 Environment.GetFolderPath(Environment.SpecialFolder.System),
                                 "cmd.exe");
                arguments = new[] { "/d", "/c", "ping 127.0.0.1 -n 3 > nul" };
            }
            else
            {
                executable = "/bin/sh";
                arguments = new[] { "-c", "sleep 2" };
            }

            var result = new ProcessCommandRunner().Run(new CommandSpec
            {
                FileName = executable,
                ArgumentList = arguments,
                WorkingDirectory = Environment.CurrentDirectory,
                TimeoutMs = 50
            });

            Assert.That(result.TimedOut, Is.True, result.StdErr);
            Assert.That(result.TerminationConfirmed, Is.False);
            Assert.That(result.StdErr, Does.Contain("could not be confirmed"));
        }

        [Test]
        public void RootProcessTerminationScope_AllowsNonRepositoryRetryAfterRootStops()
        {
            string executable;
            IReadOnlyList<string> arguments;
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                executable = Environment.GetEnvironmentVariable("ComSpec") ??
                             Path.Combine(
                                 Environment.GetFolderPath(Environment.SpecialFolder.System),
                                 "cmd.exe");
                arguments = new[] { "/d", "/c", "ping 127.0.0.1 -n 3 > nul" };
            }
            else
            {
                executable = "/bin/sh";
                arguments = new[] { "-c", "sleep 2" };
            }

            var result = new ProcessCommandRunner().Run(new CommandSpec
            {
                FileName = executable,
                ArgumentList = arguments,
                WorkingDirectory = Environment.CurrentDirectory,
                TimeoutMs = 50,
                TerminationScope = CommandTerminationScope.RootProcess
            });

            Assert.That(result.TimedOut, Is.True, result.StdErr);
            Assert.That(result.TerminationConfirmed, Is.True, result.StdErr);
            Assert.That(result.StdErr, Does.Not.Contain("could not be confirmed"));
        }

        [Test]
        public void SynchronousRun_ForwardsCallerCancellationToken()
        {
            bool observedCancellation = false;
            var runner = new RecordingRunner(spec =>
            {
                observedCancellation = spec.CancellationToken.IsCancellationRequested;
                return Success(string.Empty);
            });
            CliCommandRunner.CurrentRunner = runner;
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            CliCommandRunner.Run(
                "unused",
                Array.Empty<string>(),
                Environment.CurrentDirectory,
                1000,
                cancellation.Token);

            Assert.That(observedCancellation, Is.True);
        }

        [Test]
        public void RepositoryCacheIdentity_NormalizesUriSchemeAndHostButNotPath()
        {
            string mixedCaseHost = GitHubUtility.GetRepositoryCacheIdentity(
                "HTTPS://GITHUB.COM/Owner/Repo.git");
            string normalizedHost = GitHubUtility.GetRepositoryCacheIdentity(
                "https://github.com/Owner/Repo.git");
            string differentPath = GitHubUtility.GetRepositoryCacheIdentity(
                "https://github.com/owner/Repo.git");

            Assert.That(mixedCaseHost, Is.EqualTo(normalizedHost));
            Assert.That(differentPath, Is.Not.EqualTo(normalizedHost));
        }

        [Test]
        public void RepositoryCacheIdentity_PreservesLocalPathCaseOnCaseSensitivePlatforms()
        {
            string parent = Path.Combine(Path.GetTempPath(), "git-submodule-manager-cache-identity");
            string upper = GitHubUtility.GetRepositoryCacheIdentity(Path.Combine(parent, "Repo"));
            string lower = GitHubUtility.GetRepositoryCacheIdentity(Path.Combine(parent, "repo"));

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                Assert.That(upper, Is.EqualTo(lower));
            else
                Assert.That(upper, Is.Not.EqualTo(lower));
        }

        [Test]
        public void PackageManifestValidation_UsesSingleFullPageBatchAndClassifiesManifestStates()
        {
            var graphQlCalls = new List<CommandSpec>();
            var runner = new RecordingRunner(spec =>
            {
                string arguments = GetArguments(spec);
                if (arguments.Contains("user/repos"))
                    return Success(BuildRepositoryPageJson(50));

                if (IsGraphQlCall(spec))
                {
                    lock (graphQlCalls)
                        graphQlCalls.Add(spec);
                    return Success(BuildManifestGraphQlResponse(GetGraphQlNodeIds(spec)));
                }

                return Success(string.Empty);
            });
            CliCommandRunner.CurrentRunner = runner;
            using var coordinator = new DiscoveryCoordinator();

            coordinator.LoadInitialPage();
            TickUntil(coordinator, () => coordinator.DisplayedRepos.Count == 50);
            TickUntil(coordinator, () => !coordinator.IsValidatingPackageManifests);

            CommandSpec[] calls;
            lock (graphQlCalls)
                calls = graphQlCalls.ToArray();

            Assert.That(calls, Has.Length.EqualTo(1),
                "A normal full repository page should use one GraphQL request.");
            Assert.That(calls.All(call => call.Arguments == null), Is.True, "GraphQL requests must use tokenized argv.");
            Assert.That(calls.All(call => call.RequireStrictUtf8StdOut), Is.True,
                "GraphQL package data must be decoded as strict UTF-8.");
            Assert.That(calls.All(call => GetArguments(call).Contains("--hostname github.com")), Is.True);
            Assert.That(GetGraphQlNodeIds(calls.Single()), Has.Length.EqualTo(50));
            string queryArgument = calls.Single().ArgumentList.Single(argument =>
                argument.StartsWith("query=", StringComparison.Ordinal));
            Assert.That(queryArgument, Does.Contain("defaultBranchRef"));
            Assert.That(queryArgument, Does.Contain("file(path: \"package.json\")"));
            Assert.That(queryArgument, Does.Contain("file(path: \"package.json.meta\")"));
            Assert.That(queryArgument, Does.Not.Contain("HEAD:package.json"),
                "Both files must be read from one captured default-branch commit.");
            Assert.That(coordinator.PackageManifestCheckTotal, Is.EqualTo(50));
            Assert.That(coordinator.PackageManifestCheckCompleted, Is.EqualTo(50));
            Assert.That(coordinator.DisplayedRepos.Count(repo => repo.ManifestState == PackageManifestState.Valid), Is.EqualTo(17));
            Assert.That(coordinator.DisplayedRepos.Count(repo => repo.ManifestState == PackageManifestState.Missing), Is.EqualTo(17));
            Assert.That(coordinator.DisplayedRepos.Count(repo => repo.ManifestState == PackageManifestState.Invalid), Is.EqualTo(16));
            Assert.That(coordinator.DisplayedRepos.Where(repo => repo.ManifestState == PackageManifestState.Valid)
                .All(repo => repo.DeclaredPackageName.StartsWith("com.example.repo", StringComparison.Ordinal)), Is.True);
            Assert.That(coordinator.DisplayedRepos.Where(repo => repo.ManifestState == PackageManifestState.Valid)
                .All(repo => repo.DeclaredLicense == "MIT"), Is.True);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void PackageManifestValidation_ExpectedMissingFileErrorsPreserveValidSibling(
            bool missingRepositoryHasManifest)
        {
            var runner = new RecordingRunner(spec =>
            {
                string arguments = GetArguments(spec);
                if (arguments.Contains("user/repos"))
                    return Success(BuildRepositoryPageJson(2));

                if (IsGraphQlCall(spec))
                {
                    return new CommandResult
                    {
                        ExitCode = 1,
                        StdOut = BuildExpectedMissingFilePartialGraphQlResponse(
                            new[] { "R_repo_1", "R_repo_0" },
                            0,
                            missingRepositoryHasManifest),
                        StdErr = "gh: Could not resolve a queried package file.",
                        TerminationConfirmed = true
                    };
                }

                return Success(string.Empty);
            });
            CliCommandRunner.CurrentRunner = runner;
            using var coordinator = new DiscoveryCoordinator();

            coordinator.LoadInitialPage();
            TickUntil(coordinator, () => coordinator.DisplayedRepos.Count == 2);
            TickUntil(coordinator, () => !coordinator.IsValidatingPackageManifests);

            GitHubRepo valid = coordinator.DisplayedRepos.Single(repo => repo.NodeId == "R_repo_0");
            GitHubRepo missing = coordinator.DisplayedRepos.Single(repo => repo.NodeId == "R_repo_1");
            Assert.That(valid.ManifestState, Is.EqualTo(PackageManifestState.Valid));
            Assert.That(missing.ManifestState, Is.EqualTo(PackageManifestState.Missing));
            Assert.That(coordinator.PackageManifestCheckCompleted, Is.EqualTo(2));
            Assert.That(coordinator.PackageManifestUnavailableCount, Is.Zero);
        }

        [Test]
        public void PackageManifestValidation_MixedExpectedAndUnexpectedErrorsFailClosed()
        {
            string[] nodeIds = Enumerable.Range(0, 50)
                .Select(index => "R_repo_" + index)
                .ToArray();
            string response = BuildExpectedMissingFilePartialGraphQlResponse(
                    nodeIds,
                    1,
                    true);
            string unexpectedError = BuildMissingFileGraphQlError(
                    1,
                    "packageManifestMeta",
                    "package.json.meta")
                .Replace("\"NOT_FOUND\"", "\"FORBIDDEN\"");
            response = response.Replace(
                "\"errors\":[",
                "\"errors\":[" + unexpectedError + ",");

            AssertUnusableManifestResponseStopsQueuedWork(() => new CommandResult
            {
                ExitCode = 1,
                StdOut = response,
                StdErr = "permission denied",
                TerminationConfirmed = true
            });
        }

        [Test]
        public void PackageManifestValidation_UnreportedNullFieldFailsClosed()
        {
            string[] nodeIds = Enumerable.Range(0, 50)
                .Select(index => "R_repo_" + index)
                .ToArray();
            string response = BuildExpectedMissingFilePartialGraphQlResponse(
                nodeIds,
                1,
                false);
            string omittedError = BuildMissingFileGraphQlError(
                1,
                "packageManifest",
                "package.json");
            response = response.Replace(omittedError + ",", string.Empty);

            AssertUnusableManifestResponseStopsQueuedWork(() => new CommandResult
            {
                ExitCode = 1,
                StdOut = response,
                StdErr = "gh: partial GraphQL response",
                TerminationConfirmed = true
            });
        }

        [Test]
        public void PackageManifestValidation_ExpectedPartialResponseContinuesQueuedBatch()
        {
            int graphQlCallCount = 0;
            var runner = new RecordingRunner(spec =>
            {
                string arguments = GetArguments(spec);
                if (arguments.Contains("user/repos"))
                    return Success(BuildRepositoryPageJson(51));

                if (IsGraphQlCall(spec))
                {
                    string[] nodeIds = GetGraphQlNodeIds(spec);
                    if (Interlocked.Increment(ref graphQlCallCount) == 1)
                    {
                        return new CommandResult
                        {
                            ExitCode = 1,
                            StdOut = BuildExpectedMissingFilePartialGraphQlResponse(
                                nodeIds,
                                1,
                                true),
                            StdErr = "gh: Could not resolve file for path 'package.json.meta'.",
                            TerminationConfirmed = true
                        };
                    }

                    return Success(BuildManifestGraphQlResponse(nodeIds));
                }

                return Success(string.Empty);
            });
            CliCommandRunner.CurrentRunner = runner;
            using var coordinator = new DiscoveryCoordinator();

            coordinator.LoadInitialPage();
            TickUntil(coordinator, () => coordinator.DisplayedRepos.Count == 51);
            TickUntil(coordinator, () => !coordinator.IsValidatingPackageManifests);

            Assert.That(graphQlCallCount, Is.EqualTo(2));
            Assert.That(coordinator.PackageManifestCheckCompleted, Is.EqualTo(51));
            Assert.That(coordinator.PackageManifestUnavailableCount, Is.Zero);
            Assert.That(
                coordinator.DisplayedRepos.Single(repo => repo.NodeId == "R_repo_1")
                    .ManifestState,
                Is.EqualTo(PackageManifestState.Missing));
        }

        [Test]
        public void PackageManifestValidation_ExitOneWithoutExpectedErrorsFailsClosed()
        {
            string[] nodeIds = Enumerable.Range(0, 50)
                .Select(index => "R_repo_" + index)
                .ToArray();

            AssertUnusableManifestResponseStopsQueuedWork(() => new CommandResult
            {
                ExitCode = 1,
                StdOut = BuildManifestGraphQlResponse(nodeIds),
                StdErr = "network unavailable",
                TerminationConfirmed = true
            });
        }

        [TestCase("timed-out")]
        [TestCase("cancelled")]
        [TestCase("authentication")]
        [TestCase("unconfirmed")]
        [TestCase("invalid-utf8")]
        [TestCase("stderr-truncated")]
        [TestCase("completion-warning")]
        public void PackageManifestValidation_UnsafePartialResponseCompletionFailsClosed(
            string scenario)
        {
            CommandResult result = BuildExpectedMissingFilePartialResult();
            switch (scenario)
            {
                case "timed-out":
                    result.TimedOut = true;
                    break;
                case "cancelled":
                    result.Cancelled = true;
                    break;
                case "authentication":
                    result.BlockedByGitHubAuthentication = true;
                    break;
                case "unconfirmed":
                    result.TerminationConfirmed = false;
                    break;
                case "invalid-utf8":
                    result.StdOutInvalidUtf8 = true;
                    break;
                case "stderr-truncated":
                    result.StdErrTruncated = true;
                    break;
                case "completion-warning":
                    result.CompletionWarning = "output reader did not finish";
                    break;
                default:
                    Assert.Fail("Unknown completion scenario: " + scenario);
                    break;
            }

            try
            {
                AssertUnusableManifestResponseStopsQueuedWork(() => result);
            }
            finally
            {
                if (string.Equals(scenario, "unconfirmed", StringComparison.Ordinal))
                    CliCommandRunner.ResetGitHubCommandRestartRequirementForTests();
            }
        }

        [TestCase(-1)]
        [TestCase(2)]
        [TestCase(4)]
        public void PackageManifestValidation_NonGraphQlFailureExitCodesFailClosed(int exitCode)
        {
            CommandResult result = BuildExpectedMissingFilePartialResult();
            result.ExitCode = exitCode;

            AssertUnusableManifestResponseStopsQueuedWork(() => result);
        }

        [TestCase(40, "valid", true)]
        [TestCase(64, "valid", true)]
        [TestCase(40, "zero", false)]
        [TestCase(64, "zero", false)]
        [TestCase(40, "invalid-hex", false)]
        [TestCase(64, "invalid-hex", false)]
        [TestCase(39, "valid", false)]
        [TestCase(65, "valid", false)]
        public void PackageManifestValidation_RejectsInvalidCommitObjectIds(
            int length,
            string scenario,
            bool expectedValid)
        {
            string objectId = new string(
                string.Equals(scenario, "zero", StringComparison.Ordinal) ? '0' : 'a',
                length);
            if (string.Equals(scenario, "invalid-hex", StringComparison.Ordinal))
                objectId = objectId.Substring(0, objectId.Length - 1) + "g";

            var runner = new RecordingRunner(spec =>
            {
                if (GetArguments(spec).Contains("user/repos"))
                    return Success(BuildRepositoryPageJson(1));

                if (IsGraphQlCall(spec))
                {
                    string node = BuildManifestCommitNode(
                        "R_repo_0",
                        BuildManifestTreeEntry(
                            "package.json",
                            new string('a', 40),
                            "{\"name\":\"com.example.repo0\",\"version\":\"1.0.0\"}"),
                        BuildManifestTreeEntry(
                            "package.json.meta",
                            new string('e', 40),
                            ValidPackageManifestMeta),
                        objectId);
                    return Success(
                        "{\"data\":{\"nodes\":[" + node +
                        "],\"rateLimit\":{\"remaining\":100,\"resetAt\":\"\"}}}");
                }

                return Success(string.Empty);
            });
            CliCommandRunner.CurrentRunner = runner;
            using var coordinator = new DiscoveryCoordinator();

            coordinator.LoadInitialPage();
            TickUntil(coordinator, () => coordinator.DisplayedRepos.Count == 1);
            TickUntil(coordinator, () => !coordinator.IsValidatingPackageManifests);

            GitHubRepo repository = coordinator.DisplayedRepos.Single();
            Assert.That(
                repository.ManifestState,
                Is.EqualTo(expectedValid
                    ? PackageManifestState.Valid
                    : PackageManifestState.Unavailable),
                repository.PackageManifestMessage);
        }

        [Test]
        public void PackageManifestValidation_CachedManifestRetainsLicenseMetadata()
        {
            int graphQlCalls = 0;
            var runner = new RecordingRunner(spec =>
            {
                string arguments = GetArguments(spec);
                if (arguments.Contains("user/repos"))
                    return Success(BuildRepositoryPageJson(1));

                if (IsGraphQlCall(spec))
                {
                    int call = Interlocked.Increment(ref graphQlCalls);
                    return call == 1
                        ? Success(BuildManifestGraphQlResponse(GetGraphQlNodeIds(spec)))
                        : Success(BuildManifestGraphQlResponseWithoutText("R_repo_0"));
                }

                return Success(string.Empty);
            });
            CliCommandRunner.CurrentRunner = runner;
            using var coordinator = new DiscoveryCoordinator();

            coordinator.LoadInitialPage();
            TickUntil(coordinator, () => coordinator.DisplayedRepos.Count == 1);
            TickUntil(coordinator, () => !coordinator.IsValidatingPackageManifests);

            Assert.That(coordinator.DisplayedRepos.Single().DeclaredLicense, Is.EqualTo("MIT"));
            Assert.That(
                coordinator.DisplayedRepos.Single().PackageManifestMetaGuid,
                Is.EqualTo("0123456789abcdef0123456789abcdef"));
            Assert.That(
                coordinator.DisplayedRepos.Single().PackageManifestCommitOid,
                Is.EqualTo(new string('c', 40)));

            coordinator.LoadInitialPage();
            TickUntil(
                coordinator,
                () => Volatile.Read(ref graphQlCalls) == 2 &&
                      !coordinator.IsValidatingPackageManifests &&
                      coordinator.DisplayedRepos.Single().ManifestState ==
                      PackageManifestState.Valid);

            Assert.That(coordinator.DisplayedRepos.Single().DeclaredLicense, Is.EqualTo("MIT"));
            Assert.That(
                coordinator.DisplayedRepos.Single().PackageManifestMetaGuid,
                Is.EqualTo("0123456789abcdef0123456789abcdef"),
                "The composite cache must retain the validated Unity meta GUID.");
            Assert.That(
                coordinator.DisplayedRepos.Single().PackageManifestCommitOid,
                Is.EqualTo(new string('c', 40)),
                "The cache must not replace the current response's commit identity.");
        }

        [TestCase("missing")]
        [TestCase("tree")]
        [TestCase("binary")]
        [TestCase("truncated")]
        [TestCase("oversized")]
        [TestCase("actual-oversized")]
        [TestCase("invalid")]
        [TestCase("symlink")]
        public void PackageManifestValidation_InvalidOrMissingMetaPreventsEligibility(
            string scenario)
        {
            PackageManifestState expectedState = scenario == "missing"
                ? PackageManifestState.Missing
                : PackageManifestState.Invalid;
            var runner = new RecordingRunner(spec =>
            {
                if (GetArguments(spec).Contains("user/repos"))
                    return Success(BuildRepositoryPageJson(1));

                if (IsGraphQlCall(spec))
                {
                    const string manifest =
                        "{\"name\":\"com.example.repo0\",\"version\":\"1.0.0\"}";
                    string metaEntry = scenario switch
                    {
                        "missing" => "null",
                        "tree" =>
                            "{\"name\":\"package.json.meta\",\"mode\":16384," +
                            "\"type\":\"tree\",\"object\":{\"__typename\":\"Tree\"," +
                            $"\"oid\":\"{new string('e', 40)}\"" + "}}",
                        "binary" => BuildManifestTreeEntry(
                                "package.json.meta",
                                new string('e', 40),
                                ValidPackageManifestMeta)
                            .Replace("\"isBinary\":false", "\"isBinary\":true"),
                        "truncated" => BuildManifestTreeEntry(
                                "package.json.meta",
                                new string('e', 40),
                                ValidPackageManifestMeta)
                            .Replace("\"isTruncated\":false", "\"isTruncated\":true"),
                        "oversized" => BuildManifestTreeEntry(
                            "package.json.meta",
                            new string('e', 40),
                            ValidPackageManifestMeta,
                            16 * 1024 + 1),
                        "actual-oversized" => BuildManifestTreeEntry(
                            "package.json.meta",
                            new string('e', 40),
                            ValidPackageManifestMeta +
                            "userData: " + new string('x', 16 * 1024),
                            1),
                        "symlink" => BuildManifestTreeEntry(
                                "package.json.meta",
                                new string('e', 40),
                                ValidPackageManifestMeta)
                            .Replace("\"mode\":33188", "\"mode\":40960"),
                        _ => BuildManifestTreeEntry(
                            "package.json.meta",
                            new string('e', 40),
                            "fileFormatVersion: 2\nguid: invalid\nPackageManifestImporter:\n")
                    };
                    string node = BuildManifestCommitNode(
                        "R_repo_0",
                        BuildManifestTreeEntry(
                            "package.json",
                            new string('a', 40),
                            manifest),
                        metaEntry);
                    return Success(
                        "{\"data\":{\"nodes\":[" + node +
                        "],\"rateLimit\":{\"remaining\":100,\"resetAt\":\"\"}}}");
                }

                return Success(string.Empty);
            });
            CliCommandRunner.CurrentRunner = runner;
            using var coordinator = new DiscoveryCoordinator();

            coordinator.LoadInitialPage();
            TickUntil(coordinator, () => coordinator.DisplayedRepos.Count == 1);
            TickUntil(coordinator, () => !coordinator.IsValidatingPackageManifests);

            GitHubRepo repository = coordinator.DisplayedRepos.Single();
            Assert.That(
                repository.ManifestState,
                Is.EqualTo(expectedState),
                repository.PackageManifestMessage);
            Assert.That(repository.ManifestState, Is.Not.EqualTo(PackageManifestState.Valid));
            Assert.That(repository.PackageManifestMessage, Does.Contain("package.json.meta"));
        }

        [Test]
        public void PackageManifestValidation_CacheIdentityIncludesMetaBlob()
        {
            int graphQlCalls = 0;
            var runner = new RecordingRunner(spec =>
            {
                if (GetArguments(spec).Contains("user/repos"))
                    return Success(BuildRepositoryPageJson(1));

                if (IsGraphQlCall(spec))
                {
                    const string manifest =
                        "{\"name\":\"com.example.repo0\",\"version\":\"1.0.0\"}";
                    int call = Interlocked.Increment(ref graphQlCalls);
                    string meta = call == 1
                        ? ValidPackageManifestMeta
                        : "fileFormatVersion: 2\nguid: invalid\nPackageManifestImporter:\n";
                    string node = BuildManifestCommitNode(
                        "R_repo_0",
                        BuildManifestTreeEntry(
                            "package.json",
                            new string('a', 40),
                            manifest,
                            System.Text.Encoding.UTF8.GetByteCount(manifest)),
                        BuildManifestTreeEntry(
                            "package.json.meta",
                            new string(call == 1 ? 'e' : 'd', 40),
                            meta));
                    return Success(
                        "{\"data\":{\"nodes\":[" + node +
                        "],\"rateLimit\":{\"remaining\":100,\"resetAt\":\"\"}}}");
                }

                return Success(string.Empty);
            });
            CliCommandRunner.CurrentRunner = runner;
            using var coordinator = new DiscoveryCoordinator();

            coordinator.LoadInitialPage();
            TickUntil(coordinator, () => coordinator.DisplayedRepos.Count == 1);
            TickUntil(coordinator, () => !coordinator.IsValidatingPackageManifests);
            Assert.That(coordinator.DisplayedRepos.Single().ManifestState,
                Is.EqualTo(PackageManifestState.Valid));

            coordinator.LoadInitialPage();
            TickUntil(coordinator, () => Volatile.Read(ref graphQlCalls) == 2 &&
                                         !coordinator.IsValidatingPackageManifests);

            Assert.That(coordinator.DisplayedRepos.Single().ManifestState,
                Is.EqualTo(PackageManifestState.Invalid),
                "A different meta blob must not reuse the package-only cache entry.");
        }

        [Test]
        public void PackageManifestValidation_UsesActualPackageTextSizeNotClaimedBlobSize()
        {
            var runner = new RecordingRunner(spec =>
            {
                if (GetArguments(spec).Contains("user/repos"))
                    return Success(BuildRepositoryPageJson(1));

                if (IsGraphQlCall(spec))
                {
                    string oversizedManifest =
                        "{\"name\":\"com.example.repo0\",\"version\":\"1.0.0\"," +
                        "\"description\":\"" + new string('x', 64 * 1024) + "\"}";
                    string node = BuildManifestCommitNode(
                        "R_repo_0",
                        BuildManifestTreeEntry(
                            "package.json",
                            new string('a', 40),
                            oversizedManifest,
                            1),
                        BuildManifestTreeEntry(
                            "package.json.meta",
                            new string('e', 40),
                            ValidPackageManifestMeta));
                    return Success(
                        "{\"data\":{\"nodes\":[" + node +
                        "],\"rateLimit\":{\"remaining\":100,\"resetAt\":\"\"}}}");
                }

                return Success(string.Empty);
            });
            CliCommandRunner.CurrentRunner = runner;
            using var coordinator = new DiscoveryCoordinator();

            coordinator.LoadInitialPage();
            TickUntil(coordinator, () => coordinator.DisplayedRepos.Count == 1);
            TickUntil(coordinator, () => !coordinator.IsValidatingPackageManifests);

            GitHubRepo repository = coordinator.DisplayedRepos.Single();
            Assert.That(repository.ManifestState, Is.EqualTo(PackageManifestState.Invalid));
            Assert.That(repository.PackageManifestMessage, Does.Contain("64 KiB"));
        }

        [Test]
        public void PackageManifestValidation_TruncatedBatchBisectsUntilResponsesFit()
        {
            var graphQlBatchSizes = new List<int>();
            var runner = new RecordingRunner(spec =>
            {
                string arguments = GetArguments(spec);
                if (arguments.Contains("user/repos"))
                    return Success(BuildRepositoryPageJson(10));

                if (IsGraphQlCall(spec))
                {
                    string[] nodeIds = GetGraphQlNodeIds(spec);
                    lock (graphQlBatchSizes)
                        graphQlBatchSizes.Add(nodeIds.Length);
                    if (nodeIds.Length > 3)
                    {
                        CommandResult truncated = Success("truncated manifest response");
                        truncated.StdOutTruncated = true;
                        return truncated;
                    }

                    return Success(BuildManifestGraphQlResponse(nodeIds));
                }

                return Success(string.Empty);
            });
            CliCommandRunner.CurrentRunner = runner;
            using var coordinator = new DiscoveryCoordinator();

            coordinator.LoadInitialPage();
            TickUntil(coordinator, () => coordinator.DisplayedRepos.Count == 10);
            TickUntil(coordinator, () => !coordinator.IsValidatingPackageManifests);

            int[] sizes;
            lock (graphQlBatchSizes)
                sizes = graphQlBatchSizes.ToArray();

            Assert.That(sizes, Is.EqualTo(new[] { 10, 5, 2, 3, 5, 2, 3 }),
                "Only oversized responses should be retried, in deterministic halves.");
            Assert.That(coordinator.PackageManifestCheckCompleted, Is.EqualTo(10));
            Assert.That(coordinator.PackageManifestUnavailableCount, Is.Zero);
            Assert.That(coordinator.DisplayedRepos.All(repo => repo.PackageJsonChecked), Is.True);
        }

        [Test]
        public void PackageManifestValidation_RequestFailureStopsRemainingBatchesAndFailsClosed()
        {
            int graphQlCallCount = 0;
            var runner = new RecordingRunner(spec =>
            {
                string arguments = GetArguments(spec);
                if (arguments.Contains("user/repos"))
                    return Success(BuildRepositoryPageJson(51));

                if (IsGraphQlCall(spec))
                {
                    Interlocked.Increment(ref graphQlCallCount);
                    return new CommandResult
                    {
                        ExitCode = 1,
                        StdOut = "private manifest content must not be shown",
                        StdErr = "network unavailable",
                        TerminationConfirmed = true
                    };
                }

                return Success(string.Empty);
            });
            CliCommandRunner.CurrentRunner = runner;
            using var coordinator = new DiscoveryCoordinator();

            coordinator.LoadInitialPage();
            TickUntil(coordinator, () => coordinator.DisplayedRepos.Count == 51);
            TickUntil(coordinator, () => !coordinator.IsValidatingPackageManifests);

            Assert.That(graphQlCallCount, Is.EqualTo(1), "A failed request must stop queued GitHub work.");
            Assert.That(coordinator.PackageManifestUnavailableCount, Is.EqualTo(51));
            Assert.That(coordinator.DisplayedRepos.All(repo =>
                repo.PackageManifestMessage.Contains("private manifest content") == false), Is.True);
            Assert.That(coordinator.DisplayedRepos.All(repo => repo.ManifestState != PackageManifestState.Valid), Is.True);
        }

        [Test]
        public void PackageManifestValidation_SingleRepositoryTruncationIsolatedWhileSiblingsContinue()
        {
            var graphQlBatches = new List<string[]>();
            var runner = new RecordingRunner(spec =>
            {
                string arguments = GetArguments(spec);
                if (arguments.Contains("user/repos"))
                    return Success(BuildRepositoryPageJson(5));

                if (IsGraphQlCall(spec))
                {
                    string[] nodeIds = GetGraphQlNodeIds(spec);
                    lock (graphQlBatches)
                        graphQlBatches.Add(nodeIds);
                    if (nodeIds.Contains("R_repo_0"))
                    {
                        CommandResult truncated = Success(
                            "truncated private manifest content");
                        truncated.StdOutTruncated = true;
                        return truncated;
                    }

                    return Success(BuildManifestGraphQlResponse(nodeIds));
                }

                return Success(string.Empty);
            });
            CliCommandRunner.CurrentRunner = runner;
            using var coordinator = new DiscoveryCoordinator();

            coordinator.LoadInitialPage();
            TickUntil(coordinator, () => coordinator.DisplayedRepos.Count == 5);
            TickUntil(coordinator, () => !coordinator.IsValidatingPackageManifests);

            string[][] batches;
            lock (graphQlBatches)
                batches = graphQlBatches.ToArray();

            Assert.That(
                batches.Select(batch => batch.Length),
                Is.EqualTo(new[] { 5, 2, 1, 1, 3 }),
                "The oversized repository should be isolated before queued siblings continue.");
            Assert.That(coordinator.PackageManifestUnavailableCount, Is.EqualTo(1));
            Assert.That(
                coordinator.DisplayedRepos.Single(repo => repo.NodeId == "R_repo_0")
                    .ManifestState,
                Is.EqualTo(PackageManifestState.Unavailable));
            Assert.That(coordinator.DisplayedRepos
                .Where(repo => repo.NodeId != "R_repo_0")
                .All(repo => repo.ManifestState != PackageManifestState.Unavailable), Is.True);
            Assert.That(coordinator.DisplayedRepos.All(repo =>
                !repo.PackageManifestMessage.Contains("private manifest content")), Is.True);
        }

        [Test]
        public void PackageManifestValidation_TruncationRetriesStopAtBoundedRequestBudget()
        {
            int graphQlCallCount = 0;
            var runner = new RecordingRunner(spec =>
            {
                string arguments = GetArguments(spec);
                if (arguments.Contains("user/repos"))
                    return Success(BuildRepositoryPageJson(50));

                if (IsGraphQlCall(spec))
                {
                    Interlocked.Increment(ref graphQlCallCount);
                    CommandResult truncated = Success("truncated manifest response");
                    truncated.StdOutTruncated = true;
                    return truncated;
                }

                return Success(string.Empty);
            });
            CliCommandRunner.CurrentRunner = runner;
            using var coordinator = new DiscoveryCoordinator();

            coordinator.LoadInitialPage();
            TickUntil(coordinator, () => coordinator.DisplayedRepos.Count == 50);
            TickUntil(coordinator, () => !coordinator.IsValidatingPackageManifests);

            Assert.That(
                graphQlCallCount,
                Is.EqualTo(
                    DiscoveryCoordinator.MaximumPackageManifestRequestsPerValidation),
                "Truncated retries must consume the same bounded request budget as normal calls.");
            Assert.That(coordinator.PackageManifestUnavailableCount, Is.EqualTo(50));
            Assert.That(
                coordinator.DisplayedRepos.All(repo =>
                    repo.ManifestState == PackageManifestState.Unavailable),
                Is.True,
                "Every repository left behind by the exhausted budget must reach a fail-closed terminal state.");
            Assert.That(
                coordinator.DisplayedRepos.Any(repo =>
                    repo.PackageManifestMessage.Contains("bounded GitHub request limit")),
                Is.True,
                "Queued repositories should explain that validation stopped at the request ceiling.");

            for (int index = 0; index < 10; index++)
                coordinator.Tick();
            Assert.That(
                graphQlCallCount,
                Is.EqualTo(
                    DiscoveryCoordinator.MaximumPackageManifestRequestsPerValidation),
                "No command may start after the validation budget is exhausted.");
        }

        [Test]
        public void PackageManifestValidation_MalformedSuccessfulResponseStopsRemainingBatchesAndFailsClosed()
        {
            AssertUnusableManifestResponseStopsQueuedWork(() => Success("not valid GraphQL JSON"));
        }

        [Test]
        public void PackageManifestValidation_AcceptsOpaquePunctuationNodeIdsUsingRawStringFields()
        {
            const string nodeId = "R:future.v2@repo?x";
            CommandSpec graphQlCall = null;
            var runner = new RecordingRunner(spec =>
            {
                string arguments = GetArguments(spec);
                if (arguments.Contains("user/repos"))
                    return Success(BuildSingleRepositoryPageJson(nodeId));

                if (IsGraphQlCall(spec))
                {
                    graphQlCall = spec;
                    return Success(BuildManifestGraphQlResponse(GetGraphQlNodeIds(spec)));
                }

                return Success(string.Empty);
            });
            CliCommandRunner.CurrentRunner = runner;
            using var coordinator = new DiscoveryCoordinator();

            coordinator.LoadInitialPage();
            TickUntil(coordinator, () => coordinator.DisplayedRepos.Count == 1);
            TickUntil(coordinator, () => !coordinator.IsValidatingPackageManifests);

            Assert.That(graphQlCall, Is.Not.Null);
            Assert.That(graphQlCall.ArgumentList, Does.Contain("ids[]=" + nodeId));
            Assert.That(graphQlCall.ArgumentList, Does.Contain("-f"));
            Assert.That(graphQlCall.ArgumentList, Does.Not.Contain("-F"));
            Assert.That(coordinator.DisplayedRepos.Single().ManifestState, Is.EqualTo(PackageManifestState.Valid));
        }

        [Test]
        public void PackageManifestValidation_RejectsNodeIdsContainingControlWhitespace()
        {
            const string nodeId = "unsafe\nidentity";
            var runner = new RecordingRunner(spec =>
            {
                if (GetArguments(spec).Contains("user/repos"))
                    return Success(BuildSingleRepositoryPageJson(nodeId));

                return Success(string.Empty);
            });
            CliCommandRunner.CurrentRunner = runner;
            using var coordinator = new DiscoveryCoordinator();

            coordinator.LoadInitialPage();
            TickUntil(coordinator, () => coordinator.DisplayedRepos.Count == 1);

            Assert.That(runner.SnapshotArguments().Count(call => call.Contains("graphql")), Is.EqualTo(0));
            Assert.That(coordinator.DisplayedRepos.Single().ManifestState, Is.EqualTo(PackageManifestState.Unavailable));
        }

        [Test]
        public void DiscoveryCoordinator_DisposeCancelsLiveManifestBatchAndAllowsFreshPage()
        {
            using var firstStarted = new ManualResetEventSlim(false);
            using var releaseFirst = new ManualResetEventSlim(false);
            int graphQlCalls = 0;
            var runner = new RecordingRunner(spec =>
            {
                string arguments = GetArguments(spec);
                if (arguments.Contains("user/repos"))
                    return Success(BuildRepositoryPageJson(1));

                if (IsGraphQlCall(spec))
                {
                    int call = Interlocked.Increment(ref graphQlCalls);
                    if (call == 1)
                    {
                        firstStarted.Set();
                        releaseFirst.Wait(TimeSpan.FromSeconds(2));
                    }

                    return Success(BuildManifestGraphQlResponse(GetGraphQlNodeIds(spec)));
                }

                return Success(string.Empty);
            });
            CliCommandRunner.CurrentRunner = runner;
            using var coordinator = new DiscoveryCoordinator();

            try
            {
                coordinator.LoadInitialPage();
                TickUntil(coordinator, () => firstStarted.IsSet);

                coordinator.Dispose();
                coordinator.LoadInitialPage();
                Assert.That(
                    Volatile.Read(ref graphQlCalls),
                    Is.EqualTo(1),
                    "Replacement work must wait until the canceled worker is terminal.");

                releaseFirst.Set();
                TickUntil(coordinator, () =>
                    Volatile.Read(ref graphQlCalls) == 2 &&
                    coordinator.DisplayedRepos.Count == 1 &&
                    !coordinator.IsValidatingPackageManifests);

                Assert.That(Volatile.Read(ref graphQlCalls), Is.EqualTo(2));
                Assert.That(
                    coordinator.DisplayedRepos.Single().ManifestState,
                    Is.EqualTo(PackageManifestState.Valid));
            }
            finally
            {
                releaseFirst.Set();
            }
        }

        [Test]
        public void RepositoryCoordinator_DisposeCancelsLiveFetchBeforeNewestRequest()
        {
            using var firstStarted = new ManualResetEventSlim(false);
            using var releaseFirst = new ManualResetEventSlim(false);
            const string firstUrl = "https://github.com/owner/first.git";
            const string newestUrl = "https://github.com/owner/newest.git";
            var runner = new RecordingRunner(spec =>
            {
                if (GetArguments(spec).Contains(firstUrl))
                {
                    firstStarted.Set();
                    releaseFirst.Wait(TimeSpan.FromSeconds(2));
                }

                return Success(
                    "ref: refs/heads/main\tHEAD\n" +
                    "0123456789abcdef0123456789abcdef01234567\tHEAD\n" +
                    "0123456789abcdef0123456789abcdef01234567\trefs/heads/main\n");
            });
            CliCommandRunner.CurrentRunner = runner;
            using var coordinator = new RepositoryCoordinator();

            try
            {
                coordinator.RequestBranches(firstUrl);
                Assert.That(firstStarted.Wait(TimeSpan.FromSeconds(2)), Is.True);

                coordinator.Dispose();
                coordinator.RequestBranches(newestUrl);
                Assert.That(
                    runner.CallCount,
                    Is.EqualTo(1),
                    "Replacement work must wait until the canceled worker is terminal.");

                releaseFirst.Set();
                TickUntil(coordinator, () => coordinator.TryGetCachedBranches(newestUrl, out _));

                Assert.That(runner.CallCount, Is.EqualTo(2));
                Assert.That(coordinator.TryGetCachedBranches(firstUrl, out _), Is.False);
                Assert.That(coordinator.TryGetCachedBranches(newestUrl, out List<string> branches), Is.True);
                Assert.That(branches, Is.EqualTo(new[] { "main" }));
            }
            finally
            {
                releaseFirst.Set();
            }
        }

        private static bool IsGraphQlCall(CommandSpec spec)
        {
            IReadOnlyList<string> arguments = spec?.ArgumentList;
            return arguments != null &&
                   arguments.Count >= 2 &&
                   arguments[0] == "api" &&
                   arguments[1] == "graphql";
        }

        private static string GetArguments(CommandSpec spec)
        {
            if (!string.IsNullOrEmpty(spec?.Arguments))
                return spec.Arguments;
            return spec?.ArgumentList == null
                ? string.Empty
                : string.Join(" ", spec.ArgumentList);
        }

        private static string[] GetGraphQlNodeIds(CommandSpec spec)
        {
            return spec.ArgumentList
                .Where(argument => argument != null && argument.StartsWith("ids[]=", StringComparison.Ordinal))
                .Select(argument => argument.Substring("ids[]=".Length))
                .ToArray();
        }

        private static string BuildRepositoryPageJson(int count)
        {
            var repositories = new List<string>(count);
            for (int index = 0; index < count; index++)
            {
                repositories.Add(
                    "{" +
                    $"\"node_id\":\"R_repo_{index}\"," +
                    $"\"name\":\"repo-{index}\"," +
                    "\"owner\":{\"login\":\"owner\"}," +
                    $"\"clone_url\":\"https://github.com/owner/repo-{index}.git\"," +
                    "\"default_branch\":\"main\"," +
                    "\"private\":false," +
                    $"\"updated_at\":\"2026-07-13T00:00:{index:D2}Z\"" +
                    "}");
            }

            return "[" + string.Join(",", repositories) + "]";
        }

        private static string BuildSingleRepositoryPageJson(string nodeId)
        {
            string escapedNodeId = nodeId
                .Replace("\\", "\\\\")
                .Replace("\"", "\\\"")
                .Replace("\r", "\\r")
                .Replace("\n", "\\n")
                .Replace("\t", "\\t");
            return "[{" +
                   $"\"node_id\":\"{escapedNodeId}\"," +
                   "\"name\":\"future-repo\"," +
                   "\"owner\":{\"login\":\"owner\"}," +
                   "\"clone_url\":\"https://github.com/owner/future-repo.git\"," +
                   "\"default_branch\":\"main\"," +
                   "\"private\":false," +
                   "\"updated_at\":\"2026-07-13T00:00:00Z\"" +
                   "}]";
        }

        private static string BuildManifestGraphQlResponse(IEnumerable<string> nodeIds)
        {
            var nodes = new List<string>();
            foreach (string nodeId in nodeIds)
            {
                int separator = nodeId.LastIndexOf('_');
                int index = separator >= 0 && int.TryParse(nodeId.Substring(separator + 1), out int parsed)
                    ? parsed
                    : 0;

                if (index % 3 == 1)
                {
                    nodes.Add(BuildManifestCommitNode(
                        nodeId,
                        "null",
                        BuildManifestTreeEntry(
                            "package.json.meta",
                            new string('f', 40),
                            ValidPackageManifestMeta)));
                    continue;
                }

                string manifest = index % 3 == 2
                    ? $"{{\"name\":\"com.example.repo{index}\",\"version\":\"01.0.0\"}}"
                    : $"{{\"name\":\"com.example.repo{index}\",\"version\":\"1.0.{index}\",\"license\":\"MIT\"}}";
                string oid = (index + 1).ToString("x40");
                nodes.Add(BuildManifestCommitNode(
                    nodeId,
                    BuildManifestTreeEntry("package.json", oid, manifest),
                    BuildManifestTreeEntry(
                        "package.json.meta",
                        new string('f', 40),
                        ValidPackageManifestMeta)));
            }

            return "{\"data\":{\"nodes\":[" + string.Join(",", nodes) +
                   "],\"rateLimit\":{\"cost\":1,\"remaining\":4999,\"resetAt\":\"2026-07-13T01:00:00Z\"}}}";
        }

        private static CommandResult BuildExpectedMissingFilePartialResult()
        {
            string[] nodeIds = Enumerable.Range(0, 50)
                .Select(index => "R_repo_" + index)
                .ToArray();
            return new CommandResult
            {
                ExitCode = 1,
                StdOut = BuildExpectedMissingFilePartialGraphQlResponse(
                    nodeIds,
                    1,
                    true),
                StdErr = "gh: Could not resolve file for path 'package.json.meta'.",
                TerminationConfirmed = true
            };
        }

        private static string BuildExpectedMissingFilePartialGraphQlResponse(
            IEnumerable<string> responseNodeIds,
            int missingNodeIndex,
            bool missingRepositoryHasManifest)
        {
            string[] nodeIds = responseNodeIds.ToArray();
            if (missingNodeIndex < 0 || missingNodeIndex >= nodeIds.Length)
                throw new ArgumentOutOfRangeException(nameof(missingNodeIndex));

            var nodes = new List<string>(nodeIds.Length);
            var errors = new List<string>();
            for (int responseIndex = 0; responseIndex < nodeIds.Length; responseIndex++)
            {
                string nodeId = nodeIds[responseIndex];
                int separator = nodeId.LastIndexOf('_');
                int repositoryIndex = separator >= 0 &&
                                      int.TryParse(
                                          nodeId.Substring(separator + 1),
                                          out int parsed)
                    ? parsed
                    : responseIndex;
                string manifest =
                    $"{{\"name\":\"com.example.repo{repositoryIndex}\",\"version\":\"1.0.0\"}}";
                string manifestEntry = BuildManifestTreeEntry(
                    "package.json",
                    (repositoryIndex + 1).ToString("x40"),
                    manifest);

                if (responseIndex != missingNodeIndex)
                {
                    nodes.Add(BuildManifestCommitNode(
                        nodeId,
                        manifestEntry,
                        BuildManifestTreeEntry(
                            "package.json.meta",
                            new string('f', 40),
                            ValidPackageManifestMeta)));
                    continue;
                }

                nodes.Add(BuildManifestCommitNode(
                    nodeId,
                    missingRepositoryHasManifest ? manifestEntry : "null",
                    "null"));
                if (!missingRepositoryHasManifest)
                {
                    errors.Add(BuildMissingFileGraphQlError(
                        responseIndex,
                        "packageManifest",
                        "package.json"));
                }

                errors.Add(BuildMissingFileGraphQlError(
                    responseIndex,
                    "packageManifestMeta",
                    "package.json.meta"));
            }

            return "{\"data\":{\"nodes\":[" + string.Join(",", nodes) +
                   "],\"rateLimit\":{\"remaining\":4999," +
                   "\"resetAt\":\"2026-07-13T01:00:00Z\"}}," +
                   "\"errors\":[" + string.Join(",", errors) + "]}";
        }

        private static string BuildMissingFileGraphQlError(
            int nodeIndex,
            string fieldName,
            string fileName)
        {
            return "{" +
                   "\"type\":\"NOT_FOUND\"," +
                   "\"path\":[\"nodes\"," + nodeIndex +
                   ",\"defaultBranchRef\",\"target\"," + QuoteJson(fieldName) + "]," +
                   "\"locations\":[{\"line\":1,\"column\":1}]," +
                   "\"message\":" +
                   QuoteJson($"Could not resolve file for path '{fileName}'.") +
                   "}";
        }

        private static string BuildManifestGraphQlResponseWithoutText(string nodeId)
        {
            string node = BuildManifestCommitNode(
                nodeId,
                BuildManifestTreeEntry(
                    "package.json",
                    1.ToString("x40"),
                    null,
                    70),
                BuildManifestTreeEntry(
                    "package.json.meta",
                    new string('f', 40),
                    null,
                    System.Text.Encoding.UTF8.GetByteCount(
                        ValidPackageManifestMeta)));
            return "{\"data\":{\"nodes\":[" + node + "]," +
                   "\"rateLimit\":{\"cost\":1,\"remaining\":4999," +
                   "\"resetAt\":\"2026-07-13T01:00:00Z\"}}}";
        }

        private const string ValidPackageManifestMeta =
            "fileFormatVersion: 2\n" +
            "guid: 0123456789abcdef0123456789abcdef\n" +
            "PackageManifestImporter:\n" +
            "  externalObjects: {}\n" +
            "  userData: \n" +
            "  assetBundleName: \n" +
            "  assetBundleVariant: \n";

        private static string BuildManifestCommitNode(
            string nodeId,
            string packageManifestEntry,
            string packageManifestMetaEntry,
            string commitObjectId = null)
        {
            return "{" +
                   $"\"id\":{QuoteJson(nodeId)}," +
                   "\"defaultBranchRef\":{\"target\":{" +
                   "\"__typename\":\"Commit\"," +
                   $"\"oid\":{QuoteJson(commitObjectId ?? new string('c', 40))}," +
                   $"\"packageManifest\":{packageManifestEntry}," +
                   $"\"packageManifestMeta\":{packageManifestMetaEntry}" +
                   "}}}";
        }

        private static string BuildManifestTreeEntry(
            string fileName,
            string oid,
            string text,
            int? byteSize = null)
        {
            return "{" +
                   $"\"name\":{QuoteJson(fileName)}," +
                   "\"mode\":33188," +
                   "\"type\":\"blob\"," +
                   "\"object\":{" +
                   "\"__typename\":\"Blob\"," +
                   $"\"oid\":{QuoteJson(oid)}," +
                   $"\"byteSize\":{byteSize ?? System.Text.Encoding.UTF8.GetByteCount(text ?? string.Empty)}," +
                   "\"isBinary\":false," +
                   "\"isTruncated\":false," +
                   $"\"text\":{(text == null ? "null" : QuoteJson(text))}" +
                   "}}";
        }

        private static string QuoteJson(string value)
        {
            return "\"" + (value ?? string.Empty)
                .Replace("\\", "\\\\")
                .Replace("\"", "\\\"")
                .Replace("\r", "\\r")
                .Replace("\n", "\\n")
                .Replace("\t", "\\t") + "\"";
        }

        private static void TickUntil(DiscoveryCoordinator coordinator, Func<bool> condition)
        {
            DateTime timeoutAt = DateTime.UtcNow.AddSeconds(2);
            while (DateTime.UtcNow < timeoutAt)
            {
                coordinator.Tick();
                if (condition())
                    return;
                Thread.Sleep(10);
            }

            Assert.Fail("Timed out waiting for discovery work to complete.");
        }

        private static void TickUntil(RepositoryCoordinator coordinator, Func<bool> condition)
        {
            DateTime timeoutAt = DateTime.UtcNow.AddSeconds(2);
            while (DateTime.UtcNow < timeoutAt)
            {
                coordinator.TickBranchFetch();
                if (condition())
                    return;
                Thread.Sleep(10);
            }

            Assert.Fail("Timed out waiting for repository work to complete.");
        }

        private static void AssertUnusableManifestResponseStopsQueuedWork(
            Func<CommandResult> manifestResultFactory)
        {
            int graphQlCallCount = 0;
            var runner = new RecordingRunner(spec =>
            {
                string arguments = GetArguments(spec);
                if (arguments.Contains("user/repos"))
                    return Success(BuildRepositoryPageJson(51));

                if (IsGraphQlCall(spec))
                {
                    Interlocked.Increment(ref graphQlCallCount);
                    return manifestResultFactory();
                }

                return Success(string.Empty);
            });
            CliCommandRunner.CurrentRunner = runner;
            using var coordinator = new DiscoveryCoordinator();

            coordinator.LoadInitialPage();
            TickUntil(coordinator, () => coordinator.DisplayedRepos.Count == 51);
            TickUntil(coordinator, () => !coordinator.IsValidatingPackageManifests);

            Assert.That(graphQlCallCount, Is.EqualTo(1), "An unusable response must stop queued GitHub work.");
            Assert.That(coordinator.PackageManifestUnavailableCount, Is.EqualTo(51));
            Assert.That(coordinator.DisplayedRepos.All(repo => repo.ManifestState != PackageManifestState.Valid), Is.True);
        }

        private static CommandResult Success(string output)
        {
            return new CommandResult
            {
                ExitCode = 0,
                StdOut = output,
                StdErr = string.Empty,
                TerminationConfirmed = true
            };
        }

        private sealed class RecordingRunner : ICommandRunner
        {
            private readonly object gate = new();
            private readonly List<CommandSpec> calls = new();
            private readonly Func<CommandSpec, CommandResult> execute;

            internal RecordingRunner(Func<CommandSpec, CommandResult> execute)
            {
                this.execute = execute;
            }

            internal int CallCount
            {
                get
                {
                    lock (gate)
                        return calls.Count;
                }
            }

            public CommandResult Run(CommandSpec spec)
            {
                lock (gate)
                    calls.Add(spec);
                return execute(spec);
            }

            internal string[] SnapshotArguments()
            {
                lock (gate)
                    return calls.Select(GetArguments).ToArray();
            }
        }
    }
}
