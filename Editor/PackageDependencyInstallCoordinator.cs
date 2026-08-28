using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEngine;
using UpmPackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace MartinCalander.GitSubmoduleManager.Editor
{
    internal enum PackageDependencyInstalledSource
    {
        Unknown,
        Embedded,
        Git,
        Other
    }

    internal sealed class PackageDependencyInstalledPackage
    {
        internal PackageDependencyInstalledPackage(
            string name,
            string version,
            PackageDependencyInstalledSource source,
            bool isDirectDependency,
            string resolvedPath,
            string repositoryUrl = "",
            string revision = "",
            bool hasVerifiedRepositoryIdentity = false,
            string dependencyFingerprint = "",
            PackageManifestMetaVerification packageManifestMetaVerification =
                PackageManifestMetaVerification.Unverified,
            string packageManifestMetaGuid = "",
            string resolvedCommit = "")
        {
            Name = name?.Trim() ?? string.Empty;
            Version = version?.Trim() ?? string.Empty;
            Source = source;
            IsDirectDependency = isDirectDependency;
            ResolvedPath = resolvedPath?.Trim() ?? string.Empty;
            RepositoryUrl = repositoryUrl?.Trim() ?? string.Empty;
            Revision = revision?.Trim() ?? string.Empty;
            HasVerifiedRepositoryIdentity = hasVerifiedRepositoryIdentity;
            DependencyFingerprint = dependencyFingerprint?.Trim() ?? string.Empty;
            PackageManifestMetaVerification = packageManifestMetaVerification;
            PackageManifestMetaGuid = packageManifestMetaGuid?.Trim() ?? string.Empty;
            ResolvedCommit = resolvedCommit?.Trim() ?? string.Empty;
        }

        internal string Name { get; }
        internal string Version { get; }
        internal PackageDependencyInstalledSource Source { get; }
        internal bool IsDirectDependency { get; }
        internal string ResolvedPath { get; }
        internal string RepositoryUrl { get; }
        internal string Revision { get; }
        internal bool HasVerifiedRepositoryIdentity { get; }
        internal string DependencyFingerprint { get; }
        internal PackageManifestMetaVerification PackageManifestMetaVerification
            { get; }
        internal string PackageManifestMetaGuid { get; }
        internal string ResolvedCommit { get; }
    }

    internal sealed class PackageDependencyInstallStep
    {
        internal PackageDependencyInstallStep(
            string packageName,
            string version,
            string repositoryUrl,
            string revision,
            bool isRoot,
            string dependencyFingerprint = "",
            PackageManifestMetaVerification packageManifestMetaVerification =
                PackageManifestMetaVerification.Unverified,
            string packageManifestMetaGuid = "",
            string inspectedCommit = "")
        {
            PackageName = packageName?.Trim() ?? string.Empty;
            Version = version?.Trim() ?? string.Empty;
            RepositoryUrl = repositoryUrl?.Trim() ?? string.Empty;
            Revision = revision?.Trim() ?? string.Empty;
            IsRoot = isRoot;
            DependencyFingerprint = dependencyFingerprint?.Trim() ?? string.Empty;
            PackageManifestMetaVerification = packageManifestMetaVerification;
            PackageManifestMetaGuid = packageManifestMetaGuid?.Trim() ?? string.Empty;
            InspectedCommit = inspectedCommit?.Trim() ?? string.Empty;
        }

        internal string PackageName { get; }
        internal string Version { get; }
        internal string RepositoryUrl { get; }
        internal string Revision { get; }
        internal bool IsRoot { get; }
        internal string DependencyFingerprint { get; }
        internal PackageManifestMetaVerification PackageManifestMetaVerification
            { get; }
        internal string PackageManifestMetaGuid { get; }
        internal string InspectedCommit { get; }
    }

    internal sealed class PackageDependencyPrimitiveCompletion
    {
        internal PackageDependencyPrimitiveCompletion(
            bool success,
            string packageName,
            string message)
        {
            Success = success;
            PackageName = packageName?.Trim() ?? string.Empty;
            Message = message ?? string.Empty;
        }

        internal bool Success { get; }
        internal string PackageName { get; }
        internal string Message { get; }
    }

    internal sealed class PackageDependencyInstallCompletion
    {
        internal PackageDependencyInstallCompletion(
            bool success,
            string message,
            string rootPackageName,
            PackageManagerGitInstallMode installMode,
            string rootRepositoryUrl = "",
            string rootRevision = "")
        {
            Success = success;
            Message = message ?? string.Empty;
            RootPackageName = rootPackageName?.Trim() ?? string.Empty;
            InstallMode = installMode;
            RootRepositoryUrl = rootRepositoryUrl?.Trim() ?? string.Empty;
            RootRevision = rootRevision?.Trim() ?? string.Empty;
        }

        internal bool Success { get; }
        internal string Message { get; }
        internal string RootPackageName { get; }
        internal PackageManagerGitInstallMode InstallMode { get; }
        internal string RootRepositoryUrl { get; }
        internal string RootRevision { get; }
    }

    internal enum ReadOnlyInstallCompletionCorrelation
    {
        None,
        Exact,
        OperationIdentityOnly
    }

    internal enum PackageDependencySubmoduleCommitVerificationStatus
    {
        Pending,
        Expected,
        Unverified,
        Unexpected
    }

    internal enum PackageDependencySubmoduleCommitVerificationReadPoint
    {
        AfterFirstIndex,
        AfterFirstPass,
        BeforeFinalIndex,
        AfterSecondIndex,
        BeforeTerminalOrigin
    }

    internal interface IPackageDependencyInstallExecutor
    {
        bool IsMutationBusy { get; }

        bool IsBusyFor(string packageName);

        bool TryInspectRegisteredPackages(
            out IReadOnlyList<PackageDependencyInstalledPackage> packages,
            out string error);

        PackageDependencySubmoduleCommitVerificationStatus
            GetSubmoduleCommitVerification(
                string verificationScopeId,
                string operationId,
                int stepIndex,
                PackageDependencyInstallStep step,
                out string error);

        void CancelSubmoduleCommitVerification(string verificationScopeId);

        bool TryStart(
            PackageDependencyInstallStep step,
            PackageManagerGitInstallMode mode,
            string dependencyInstallOperationId,
            Action<PackageDependencyPrimitiveCompletion> onComplete,
            out string error);
    }

    internal interface IPackageDependencyInstallStateStore
    {
        string LoadActive();
        void SaveActive(string json);
        void ClearActive();
        string LoadCompletion();
        void SaveCompletion(string json);
        void ClearCompletion();
        string LoadRecoveryNotification();
        void SaveRecoveryNotification(string value);
        void ClearRecoveryNotification();
    }

    /// <summary>
    /// Produces a fresh step-scoped proof of the exact submodule commit.
    /// Git runs only on this verifier's worker thread. A second index/HEAD pass
    /// closes the seam between the parent gitlink and initialized worktree reads.
    /// </summary>
    internal sealed class PackageDependencySubmoduleCommitVerifier : IDisposable
    {
        private const int CommandTimeoutMilliseconds = 5000;
        private const int ReloadDrainTimeoutMilliseconds = 2000;
        private const int MaxGitModulesBytes = 128 * 1024;
        private static readonly Encoding StrictUtf8Encoding =
            new UTF8Encoding(false, true);

        private sealed class VerificationRequest
        {
            internal string ScopeId;
            internal string OperationId;
            internal int StepIndex;
            internal string PackageName;
            internal string RelativePath;
            internal string RepositoryUrl;
            internal string Revision;
            internal string ExpectedCommit;
            internal long RepositoryGeneration;

            internal bool Matches(VerificationRequest other)
            {
                return other != null &&
                       string.Equals(ScopeId, other.ScopeId, StringComparison.Ordinal) &&
                       string.Equals(OperationId, other.OperationId, StringComparison.Ordinal) &&
                       StepIndex == other.StepIndex &&
                       string.Equals(PackageName, other.PackageName, StringComparison.Ordinal) &&
                       string.Equals(RelativePath, other.RelativePath, StringComparison.Ordinal) &&
                       string.Equals(
                           RepositoryUrl,
                           other.RepositoryUrl,
                           StringComparison.Ordinal) &&
                       string.Equals(Revision, other.Revision, StringComparison.Ordinal) &&
                       string.Equals(
                           ExpectedCommit,
                           other.ExpectedCommit,
                           StringComparison.OrdinalIgnoreCase) &&
                       RepositoryGeneration == other.RepositoryGeneration;
            }
        }

        private sealed class VerificationOutcome
        {
            internal PackageDependencySubmoduleCommitVerificationStatus Status;
            internal string Error;
        }

        private sealed class SubmoduleRegistration
        {
            internal string Name;
            internal string Path;
            internal string Url;
            internal string Branch;
        }

        private sealed class IndexEntry
        {
            internal string Mode;
            internal string ObjectId;
            internal int Stage;
            internal string Path;
        }

        private readonly object gate = new();
        private readonly ICommandRunner runner;
        private readonly string projectRoot;
        private readonly Action<
            PackageDependencySubmoduleCommitVerificationReadPoint,
            string> readPointForTests;
        private VerificationRequest activeRequest;
        private VerificationRequest completedRequest;
        private VerificationOutcome completedOutcome;
        private CancellationTokenSource cancellationSource;
        private Thread workerThread;
        private int generation;
        private bool stopping;

        internal PackageDependencySubmoduleCommitVerifier(
            ICommandRunner runner = null,
            string projectRoot = null,
            Action<PackageDependencySubmoduleCommitVerificationReadPoint, string>
                readPointForTests = null)
        {
            this.runner = runner ?? CliCommandRunner.CurrentRunner;
            this.projectRoot = Path.GetFullPath(
                string.IsNullOrWhiteSpace(projectRoot)
                    ? GitUtility.ProjectRoot
                    : projectRoot);
            this.readPointForTests = readPointForTests;
        }

        internal PackageDependencySubmoduleCommitVerificationStatus GetOrStart(
            string verificationScopeId,
            string operationId,
            int stepIndex,
            PackageDependencyInstallStep step,
            out string error)
        {
            error = string.Empty;
            if (!TryCreateRequest(
                    verificationScopeId,
                    operationId,
                    stepIndex,
                    step,
                    out VerificationRequest request,
                    out error))
            {
                return PackageDependencySubmoduleCommitVerificationStatus
                    .Unexpected;
            }

            lock (gate)
            {
                if (stopping)
                {
                    error =
                        "Fresh submodule commit verification is stopping for script reload.";
                    return PackageDependencySubmoduleCommitVerificationStatus
                        .Unverified;
                }

                if (completedRequest?.Matches(request) == true &&
                    completedOutcome != null)
                {
                    error = completedOutcome.Error ?? string.Empty;
                    return completedOutcome.Status;
                }

                if (activeRequest?.Matches(request) == true &&
                    workerThread != null)
                {
                    return PackageDependencySubmoduleCommitVerificationStatus
                        .Pending;
                }

                // A proof for another runtime scope, persisted operation, or
                // step can never satisfy this request. Retire its read first;
                // the correct request starts only after that worker terminates.
                if (workerThread != null)
                {
                    completedRequest = null;
                    completedOutcome = null;
                    try
                    {
                        cancellationSource?.Cancel();
                    }
                    catch
                    {
                        // The worker will still be ignored by its generation.
                    }
                    generation++;
                    activeRequest = null;
                    return PackageDependencySubmoduleCommitVerificationStatus
                        .Pending;
                }

                completedRequest = null;
                completedOutcome = null;
                activeRequest = request;
                int requestGeneration = ++generation;
                var requestCancellationSource = new CancellationTokenSource();
                cancellationSource = requestCancellationSource;
                workerThread = new Thread(() => RunVerification(
                    request,
                    requestGeneration,
                    requestCancellationSource))
                {
                    IsBackground = true,
                    Name = "Git Submodule Manager dependency commit verifier"
                };

                try
                {
                    workerThread.Start();
                }
                catch (Exception exception)
                {
                    workerThread = null;
                    activeRequest = null;
                    cancellationSource = null;
                    requestCancellationSource.Dispose();
                    error = Sanitize(
                        "Fresh submodule commit verification could not start: " +
                        exception.Message);
                    completedRequest = request;
                    completedOutcome = Unverified(error);
                    return PackageDependencySubmoduleCommitVerificationStatus
                        .Unverified;
                }

                return PackageDependencySubmoduleCommitVerificationStatus.Pending;
            }
        }

        internal void Cancel(string verificationScopeId)
        {
            string expectedScope = verificationScopeId?.Trim() ?? string.Empty;
            if (!Guid.TryParseExact(expectedScope, "N", out _))
                return;

            lock (gate)
            {
                if (string.Equals(
                        completedRequest?.ScopeId,
                        expectedScope,
                        StringComparison.Ordinal))
                {
                    completedRequest = null;
                    completedOutcome = null;
                }

                if (!string.Equals(
                        activeRequest?.ScopeId,
                        expectedScope,
                        StringComparison.Ordinal))
                {
                    return;
                }

                generation++;
                activeRequest = null;
                try
                {
                    cancellationSource?.Cancel();
                }
                catch
                {
                    // Cancellation is best effort; the generation blocks reuse.
                }
            }
        }

        internal void StopForReload()
        {
            Thread threadToDrain;
            lock (gate)
            {
                if (stopping)
                    return;

                stopping = true;
                generation++;
                activeRequest = null;
                completedRequest = null;
                completedOutcome = null;
                threadToDrain = workerThread;
                try
                {
                    cancellationSource?.Cancel();
                }
                catch
                {
                    // Unity is already tearing down the managed domain.
                }
            }

            if (threadToDrain == null ||
                ReferenceEquals(threadToDrain, Thread.CurrentThread))
            {
                return;
            }

            try
            {
                threadToDrain.Join(ReloadDrainTimeoutMilliseconds);
            }
            catch
            {
                // The verifier is read-only and its result was already retired.
            }
        }

        public void Dispose()
        {
            StopForReload();
        }

        private bool TryCreateRequest(
            string verificationScopeId,
            string operationId,
            int stepIndex,
            PackageDependencyInstallStep step,
            out VerificationRequest request,
            out string error)
        {
            request = null;
            error = string.Empty;
            string scope = verificationScopeId?.Trim() ?? string.Empty;
            string operation = operationId?.Trim() ?? string.Empty;
            if (!Guid.TryParseExact(scope, "N", out _) ||
                !Guid.TryParseExact(operation, "N", out _) ||
                stepIndex < 0 ||
                step == null ||
                !GitUtility.IsValidUpmPackageName(step.PackageName) ||
                !GitUtility.IsValidRepositoryUrl(step.RepositoryUrl) ||
                string.IsNullOrWhiteSpace(step.Revision) ||
                string.Equals(step.Revision, ".", StringComparison.Ordinal) ||
                !GitUtility.IsValidBranchName(step.Revision) ||
                !GitUtility.IsValidGitObjectId(step.InspectedCommit))
            {
                error =
                    "The fresh submodule commit verification identity is invalid.";
                return false;
            }

            string packageName = step.PackageName.Trim();
            string relativePath = GitUtility.NormalizePath(
                Path.Combine("Packages", packageName));
            string expectedRelativePath = "Packages/" + packageName;
            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(Path.Combine(
                    projectRoot,
                    relativePath.Replace('/', Path.DirectorySeparatorChar)));
            }
            catch
            {
                error =
                    "The fresh submodule commit verification path is invalid.";
                return false;
            }

            string expectedFullPath = Path.GetFullPath(Path.Combine(
                projectRoot,
                "Packages",
                packageName));
            StringComparison pathComparison = Path.DirectorySeparatorChar == '\\'
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            if (!GitUtility.IsPackagePath(relativePath) ||
                !string.Equals(
                    relativePath,
                    expectedRelativePath,
                    StringComparison.Ordinal) ||
                !string.Equals(fullPath, expectedFullPath, pathComparison))
            {
                error =
                    "The fresh submodule commit verification path is not the exact managed package path.";
                return false;
            }

            request = new VerificationRequest
            {
                ScopeId = scope,
                OperationId = operation,
                StepIndex = stepIndex,
                PackageName = packageName,
                RelativePath = relativePath,
                RepositoryUrl = step.RepositoryUrl.Trim(),
                Revision = step.Revision.Trim(),
                ExpectedCommit = step.InspectedCommit.Trim().ToLowerInvariant(),
                RepositoryGeneration =
                    GitOperationService.RepositoryGeneration
            };
            return true;
        }

        private void RunVerification(
            VerificationRequest request,
            int requestGeneration,
            CancellationTokenSource requestCancellationSource)
        {
            VerificationOutcome outcome;
            try
            {
                outcome = Verify(request, requestCancellationSource.Token);
            }
            catch (OperationCanceledException)
            {
                outcome = Unverified(
                    "Fresh submodule commit verification was cancelled.");
            }
            catch (Exception exception)
            {
                outcome = Unverified(
                    "Fresh submodule commit verification failed: " +
                    Sanitize(exception.Message));
            }

            lock (gate)
            {
                if (!stopping &&
                    requestGeneration == generation &&
                    activeRequest?.Matches(request) == true)
                {
                    completedRequest = request;
                    completedOutcome = outcome;
                    activeRequest = null;
                }

                if (ReferenceEquals(workerThread, Thread.CurrentThread))
                    workerThread = null;
                if (ReferenceEquals(
                        cancellationSource,
                        requestCancellationSource))
                {
                    cancellationSource = null;
                }
            }

            requestCancellationSource.Dispose();
        }

        private VerificationOutcome Verify(
            VerificationRequest request,
            CancellationToken cancellationToken)
        {
            CommandResult firstIndex = RunGit(
                new[]
                {
                    "ls-files", "--stage", "--", request.RelativePath
                },
                cancellationToken);
            if (!TryRequireSafeCommandResult(
                    firstIndex,
                    "the first parent index gitlink read",
                    out string error))
            {
                return Unverified(error);
            }

            InvokeReadPoint(
                PackageDependencySubmoduleCommitVerificationReadPoint
                    .AfterFirstIndex,
                request.RelativePath,
                cancellationToken);
            CommandResult firstHead = RunGit(
                new[]
                {
                    "-C", request.RelativePath, "rev-parse", "--verify",
                    "HEAD^{commit}"
                },
                cancellationToken);
            if (!TryRequireSafeCommandResult(
                    firstHead,
                    "the first initialized submodule HEAD read",
                    out error))
            {
                return Unverified(error);
            }

            if (!TryRequireExpectedPair(
                    firstIndex,
                    firstHead,
                    request,
                    "first",
                    out error))
            {
                return Unexpected(error);
            }

            CommandResult firstOrigin = RunGit(
                new[]
                {
                    "-C", request.RelativePath, "remote", "get-url", "origin"
                },
                cancellationToken);
            if (!TryRequireSafeCommandResult(
                    firstOrigin,
                    "the first initialized submodule origin read",
                    out error))
            {
                return Unverified(error);
            }
            if (!TryRequireExpectedOrigin(firstOrigin, request, out error))
                return Unexpected(error);

            VerificationOutcome firstRegistrationFailure =
                VerifyRegistrationPass(
                    request,
                    "first",
                    cancellationToken,
                    out _);
            if (firstRegistrationFailure != null)
                return firstRegistrationFailure;

            InvokeReadPoint(
                PackageDependencySubmoduleCommitVerificationReadPoint
                    .AfterFirstPass,
                request.RelativePath,
                cancellationToken);
            CommandResult secondOrigin = RunGit(
                new[]
                {
                    "-C", request.RelativePath, "remote", "get-url", "origin"
                },
                cancellationToken);
            if (!TryRequireSafeCommandResult(
                    secondOrigin,
                    "the second initialized submodule origin read",
                    out error))
            {
                return Unverified(error);
            }
            if (!TryRequireExpectedOrigin(secondOrigin, request, out error))
                return Unexpected(error);

            VerificationOutcome secondRegistrationFailure =
                VerifyRegistrationPass(
                    request,
                    "second",
                    cancellationToken,
                    out string secondGitModulesObjectId);
            if (secondRegistrationFailure != null)
                return secondRegistrationFailure;

            InvokeReadPoint(
                PackageDependencySubmoduleCommitVerificationReadPoint
                    .BeforeFinalIndex,
                request.RelativePath,
                cancellationToken);
            CommandResult secondIndex = RunGit(
                new[]
                {
                    "ls-files", "--stage", "-z", "--", ".gitmodules",
                    request.RelativePath
                },
                cancellationToken);
            if (!TryRequireSafeCommandResult(
                    secondIndex,
                    "the second parent index gitlink read",
                    out error))
            {
                return Unverified(error);
            }
            if (!TryRequireFinalIndexState(
                    secondIndex,
                    request,
                    secondGitModulesObjectId,
                    out error))
            {
                return Unexpected(error);
            }

            InvokeReadPoint(
                PackageDependencySubmoduleCommitVerificationReadPoint
                    .AfterSecondIndex,
                request.RelativePath,
                cancellationToken);
            CommandResult secondHead = RunGit(
                new[]
                {
                    "-C", request.RelativePath, "rev-parse", "--verify",
                    "HEAD^{commit}"
                },
                cancellationToken);
            if (!TryRequireSafeCommandResult(
                    secondHead,
                    "the second initialized submodule HEAD read",
                    out error))
            {
                return Unverified(error);
            }

            string secondHeadCommit = secondHead.StdOut?.Trim() ?? string.Empty;
            if (!GitUtility.IsValidGitObjectId(secondHeadCommit) ||
                !string.Equals(
                    secondHeadCommit,
                    request.ExpectedCommit,
                    StringComparison.OrdinalIgnoreCase))
            {
                return Unexpected(
                    "The final fresh initialized submodule HEAD did not match the inspected commit.");
            }

            InvokeReadPoint(
                PackageDependencySubmoduleCommitVerificationReadPoint
                    .BeforeTerminalOrigin,
                request.RelativePath,
                cancellationToken);
            CommandResult terminalOrigin = RunGit(
                new[]
                {
                    "-C", request.RelativePath, "remote", "get-url", "origin"
                },
                cancellationToken);
            if (!TryRequireSafeCommandResult(
                    terminalOrigin,
                    "the terminal initialized submodule origin read",
                    out error))
            {
                return Unverified(error);
            }
            if (!TryRequireExpectedOrigin(terminalOrigin, request, out error))
                return Unexpected(error);

            if (!TryRequireExactWorktreeGitModulesBlob(
                    secondGitModulesObjectId,
                    "the terminal worktree .gitmodules snapshot",
                    out error,
                    cancellationToken))
            {
                return Unverified(error);
            }

            CommandResult terminalIndex = RunGit(
                new[]
                {
                    "ls-files", "--stage", "-z", "--", ".gitmodules",
                    request.RelativePath
                },
                cancellationToken);
            if (!TryRequireSafeCommandResult(
                    terminalIndex,
                    "the terminal parent index gitlink read",
                    out error))
            {
                return Unverified(error);
            }
            if (!TryRequireFinalIndexState(
                    terminalIndex,
                    request,
                    secondGitModulesObjectId,
                    out error))
            {
                return Unexpected(error);
            }

            CommandResult terminalHead = RunGit(
                new[]
                {
                    "-C", request.RelativePath, "rev-parse", "--verify",
                    "HEAD^{commit}"
                },
                cancellationToken);
            if (!TryRequireSafeCommandResult(
                    terminalHead,
                    "the terminal initialized submodule HEAD read",
                    out error))
            {
                return Unverified(error);
            }

            string terminalHeadCommit =
                terminalHead.StdOut?.Trim() ?? string.Empty;
            if (!GitUtility.IsValidGitObjectId(terminalHeadCommit) ||
                !string.Equals(
                    terminalHeadCommit,
                    request.ExpectedCommit,
                    StringComparison.OrdinalIgnoreCase))
            {
                return Unexpected(
                    "The terminal fresh initialized submodule HEAD did not match the inspected commit.");
            }

            return new VerificationOutcome
            {
                Status =
                    PackageDependencySubmoduleCommitVerificationStatus.Expected,
                Error = string.Empty
            };
        }

        private VerificationOutcome VerifyRegistrationPass(
            VerificationRequest request,
            string passName,
            CancellationToken cancellationToken,
            out string gitModulesObjectId)
        {
            gitModulesObjectId = string.Empty;
            CommandResult gitModulesIndex = RunGit(
                new[]
                {
                    "ls-files", "--stage", "-z", "--", ".gitmodules"
                },
                cancellationToken);
            if (!TryRequireSafeCommandResult(
                    gitModulesIndex,
                    "the " + passName + " staged .gitmodules identity read",
                    out string error))
            {
                return Unverified(error);
            }
            if (!TryParseIndexEntries(
                    gitModulesIndex.StdOut,
                    out List<IndexEntry> gitModulesEntries,
                    out error) ||
                gitModulesEntries.Count != 1 ||
                !string.Equals(
                    gitModulesEntries[0].Path,
                    ".gitmodules",
                    StringComparison.Ordinal) ||
                gitModulesEntries[0].Stage != 0 ||
                (gitModulesEntries[0].Mode != "100644" &&
                 gitModulesEntries[0].Mode != "100755"))
            {
                return Unexpected(
                    string.IsNullOrWhiteSpace(error)
                        ? "The " + passName +
                          " staged .gitmodules entry is missing, conflicted, or not a regular stage-0 blob."
                        : error);
            }

            gitModulesObjectId = gitModulesEntries[0].ObjectId;
            if (!TryRequireExactWorktreeGitModulesBlob(
                    gitModulesObjectId,
                    "the " + passName + " worktree .gitmodules snapshot",
                    out error,
                    cancellationToken))
            {
                return Unverified(error);
            }

            CommandResult stagedConfig = RunGit(
                new[]
                {
                    "config", "--no-includes", "--null", "--blob",
                    gitModulesObjectId, "--list"
                },
                cancellationToken);
            if (!TryRequireSafeCommandResult(
                    stagedConfig,
                    "the " + passName + " staged .gitmodules blob read",
                    out error))
            {
                return Unverified(error);
            }

            if (!TryParseTargetRegistration(
                    stagedConfig.StdOut,
                    request,
                    out SubmoduleRegistration stagedRegistration,
                    out error))
            {
                return Unexpected(
                    "The " + passName +
                    " staged .gitmodules registration is invalid: " + error);
            }

            return null;
        }

        private bool TryRequireExactWorktreeGitModulesBlob(
            string expectedObjectId,
            string description,
            out string error,
            CancellationToken cancellationToken)
        {
            error = string.Empty;
            if (!GitUtility.IsValidGitObjectId(expectedObjectId))
            {
                error = "The expected staged .gitmodules blob identity is invalid.";
                return false;
            }

            string path = Path.Combine(projectRoot, ".gitmodules");
            byte[] contents;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var fileInfo = new FileInfo(path);
                fileInfo.Refresh();
                if (!fileInfo.Exists ||
                    (fileInfo.Attributes &
                     (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                {
                    error =
                        description +
                        " is not one regular, non-symbolic-link file.";
                    return false;
                }

                if (fileInfo.Length > MaxGitModulesBytes)
                {
                    error =
                        description + " exceeds the " +
                        (MaxGitModulesBytes / 1024) + " KiB safety limit.";
                    return false;
                }

                var buffer = new byte[MaxGitModulesBytes + 1];
                int count = 0;
                using (var stream = new FileStream(
                           path,
                           FileMode.Open,
                           FileAccess.Read,
                           FileShare.Read))
                {
                    while (count < buffer.Length)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        int read = stream.Read(
                            buffer,
                            count,
                            buffer.Length - count);
                        if (read <= 0)
                            break;
                        count += read;
                    }
                }

                if (count > MaxGitModulesBytes)
                {
                    error =
                        description + " grew beyond the " +
                        (MaxGitModulesBytes / 1024) +
                        " KiB safety limit while it was read.";
                    return false;
                }

                fileInfo.Refresh();
                if (!fileInfo.Exists ||
                    (fileInfo.Attributes &
                     (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0 ||
                    fileInfo.Length != count)
                {
                    error = description + " changed type or length while it was read.";
                    return false;
                }

                contents = new byte[count];
                Buffer.BlockCopy(buffer, 0, contents, 0, count);
                StrictUtf8Encoding.GetString(contents);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (DecoderFallbackException exception)
            {
                error =
                    description + " is not valid UTF-8: " +
                    Sanitize(exception.Message);
                return false;
            }
            catch (Exception exception)
            {
                error =
                    description + " could not be read safely: " +
                    Sanitize(exception.Message);
                return false;
            }

            string header = "blob " + contents.Length + "\0";
            byte[] headerBytes = Encoding.ASCII.GetBytes(header);
            var objectBytes = new byte[headerBytes.Length + contents.Length];
            Buffer.BlockCopy(
                headerBytes,
                0,
                objectBytes,
                0,
                headerBytes.Length);
            Buffer.BlockCopy(
                contents,
                0,
                objectBytes,
                headerBytes.Length,
                contents.Length);

            byte[] digest;
            using (HashAlgorithm hash = expectedObjectId.Length == 40
                       ? (HashAlgorithm)SHA1.Create()
                       : SHA256.Create())
            {
                digest = hash.ComputeHash(objectBytes);
            }

            string actualObjectId = BitConverter.ToString(digest)
                .Replace("-", string.Empty)
                .ToLowerInvariant();
            if (!string.Equals(
                    actualObjectId,
                    expectedObjectId,
                    StringComparison.OrdinalIgnoreCase))
            {
                error =
                    description +
                    " does not have the exact raw blob identity staged in the parent index.";
                return false;
            }

            return true;
        }

        private static bool TryParseTargetRegistration(
            string output,
            VerificationRequest request,
            out SubmoduleRegistration target,
            out string error)
        {
            target = null;
            error = string.Empty;
            string value = output ?? string.Empty;
            if (value.Length == 0 || value[value.Length - 1] != '\0')
            {
                error = ".gitmodules did not return a complete NUL-delimited configuration.";
                return false;
            }

            var registrations =
                new Dictionary<string, SubmoduleRegistration>(
                    StringComparer.Ordinal);
            string[] records = value.Split('\0');
            for (int index = 0; index < records.Length - 1; index++)
            {
                string record = records[index];
                int separator = record.IndexOf('\n');
                if (separator <= 0)
                {
                    error = ".gitmodules returned a malformed configuration record.";
                    return false;
                }

                string key = record.Substring(0, separator);
                string configValue = record.Substring(separator + 1);
                const string prefix = "submodule.";
                if (!key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    continue;

                string property;
                string suffix;
                if (key.EndsWith(".path", StringComparison.OrdinalIgnoreCase))
                {
                    property = "path";
                    suffix = ".path";
                }
                else if (key.EndsWith(
                             ".url",
                             StringComparison.OrdinalIgnoreCase))
                {
                    property = "url";
                    suffix = ".url";
                }
                else if (key.EndsWith(
                             ".branch",
                             StringComparison.OrdinalIgnoreCase))
                {
                    property = "branch";
                    suffix = ".branch";
                }
                else
                {
                    continue;
                }

                string name = key.Substring(
                    prefix.Length,
                    key.Length - prefix.Length - suffix.Length);
                if (string.IsNullOrWhiteSpace(name))
                {
                    error = ".gitmodules contains an empty target section name.";
                    return false;
                }
                if (!registrations.TryGetValue(
                        name,
                        out SubmoduleRegistration registration))
                {
                    registration = new SubmoduleRegistration { Name = name };
                    registrations.Add(name, registration);
                }

                bool duplicate;
                switch (property)
                {
                    case "path":
                        duplicate = registration.Path != null;
                        registration.Path = configValue;
                        break;
                    case "url":
                        duplicate = registration.Url != null;
                        registration.Url = configValue;
                        break;
                    default:
                        duplicate = registration.Branch != null;
                        registration.Branch = configValue;
                        break;
                }
                if (duplicate)
                {
                    error =
                        ".gitmodules contains duplicate target registration fields.";
                    return false;
                }
            }

            SubmoduleRegistration[] matches = registrations.Values
                .Where(registration => string.Equals(
                    registration.Path,
                    request.RelativePath,
                    StringComparison.Ordinal))
                .Take(2)
                .ToArray();
            if (matches.Length != 1)
            {
                error =
                    ".gitmodules must contain exactly one section for the managed package path.";
                return false;
            }

            target = matches[0];
            if (!GitUtility.IsValidRepositoryUrl(target.Url) ||
                !GitUtility.AreRepositoryUrlsEquivalent(
                    target.Url,
                    request.RepositoryUrl) ||
                string.IsNullOrWhiteSpace(target.Branch) ||
                !GitUtility.IsValidBranchName(target.Branch) ||
                !string.Equals(
                    target.Branch,
                    request.Revision,
                    StringComparison.Ordinal))
            {
                target = null;
                error =
                    ".gitmodules does not retain the exact inspected repository URL and branch.";
                return false;
            }

            return true;
        }

        private static bool TryRequireFinalIndexState(
            CommandResult finalIndex,
            VerificationRequest request,
            string expectedGitModulesObjectId,
            out string error)
        {
            error = string.Empty;
            if (!TryParseIndexEntries(
                    finalIndex?.StdOut,
                    out List<IndexEntry> entries,
                    out error))
            {
                return false;
            }

            IndexEntry[] gitModulesEntries = entries.Where(entry =>
                string.Equals(
                    entry.Path,
                    ".gitmodules",
                    StringComparison.Ordinal)).ToArray();
            IndexEntry[] packageEntries = entries.Where(entry =>
                string.Equals(
                    entry.Path,
                    request.RelativePath,
                    StringComparison.Ordinal)).ToArray();
            if (entries.Count != 2 ||
                gitModulesEntries.Length != 1 ||
                packageEntries.Length != 1)
            {
                error =
                    "The final parent index does not contain exactly one .gitmodules blob and one package gitlink.";
                return false;
            }

            IndexEntry gitModules = gitModulesEntries[0];
            IndexEntry package = packageEntries[0];
            if (gitModules.Stage != 0 ||
                (gitModules.Mode != "100644" &&
                 gitModules.Mode != "100755") ||
                !string.Equals(
                    gitModules.ObjectId,
                    expectedGitModulesObjectId,
                    StringComparison.OrdinalIgnoreCase))
            {
                error =
                    "The final staged .gitmodules blob changed after its target registration was verified.";
                return false;
            }
            if (package.Stage != 0 ||
                package.Mode != "160000" ||
                !string.Equals(
                    package.ObjectId,
                    request.ExpectedCommit,
                    StringComparison.OrdinalIgnoreCase))
            {
                error =
                    "The final parent stage-0 package gitlink did not match the inspected commit.";
                return false;
            }

            return true;
        }

        private static bool TryParseIndexEntries(
            string output,
            out List<IndexEntry> entries,
            out string error)
        {
            entries = new List<IndexEntry>();
            error = string.Empty;
            string value = output ?? string.Empty;
            if (value.Length > 0 && value[value.Length - 1] != '\0')
            {
                error = "Git returned an incomplete NUL-delimited index record.";
                return false;
            }

            string[] records = value.Split('\0');
            for (int index = 0; index < records.Length - 1; index++)
            {
                string record = records[index];
                int separator = record.IndexOf('\t');
                if (separator <= 0 || separator >= record.Length - 1)
                {
                    error = "Git returned a malformed parent index record.";
                    entries.Clear();
                    return false;
                }

                string[] fields = record.Substring(0, separator).Split(
                    new[] { ' ' },
                    StringSplitOptions.RemoveEmptyEntries);
                if (fields.Length != 3 ||
                    fields[0].Length != 6 ||
                    !fields[0].All(character => character >= '0' &&
                                                    character <= '7') ||
                    !GitUtility.IsValidGitObjectId(fields[1]) ||
                    !int.TryParse(fields[2], out int stage) ||
                    stage < 0 ||
                    stage > 3)
                {
                    error = "Git returned an invalid parent index identity.";
                    entries.Clear();
                    return false;
                }

                string path = record.Substring(separator + 1);
                if (string.IsNullOrEmpty(path) ||
                    path.Any(character => char.IsControl(character)))
                {
                    error = "Git returned an invalid parent index path.";
                    entries.Clear();
                    return false;
                }

                entries.Add(new IndexEntry
                {
                    Mode = fields[0],
                    ObjectId = fields[1].ToLowerInvariant(),
                    Stage = stage,
                    Path = path
                });
            }

            return true;
        }

        private CommandResult RunGit(
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return runner.Run(new CommandSpec
            {
                FileName = GitUtility.GitExecutable,
                ArgumentList = arguments,
                WorkingDirectory = projectRoot,
                TimeoutMs = CommandTimeoutMilliseconds,
                CancellationToken = cancellationToken,
                TerminationScope = CommandTerminationScope.CompleteProcessTree,
                RequireStrictUtf8StdOut = true
            });
        }

        private void InvokeReadPoint(
            PackageDependencySubmoduleCommitVerificationReadPoint point,
            string relativePath,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            readPointForTests?.Invoke(point, relativePath);
            cancellationToken.ThrowIfCancellationRequested();
        }

        private static bool TryRequireSafeCommandResult(
            CommandResult result,
            string description,
            out string error)
        {
            error = string.Empty;
            if (result == null)
            {
                error = "Git returned no result for " + description + ".";
                return false;
            }
            if (!result.TerminationConfirmed)
            {
                error = "Git process termination was not confirmed after " +
                        description + ".";
                return false;
            }
            if (result.TimedOut || result.Cancelled)
            {
                error = "Git did not complete " + description + ".";
                return false;
            }
            if (result.StdOutTruncated || result.StdErrTruncated)
            {
                error = "Git returned truncated output for " + description +
                        ".";
                return false;
            }
            if (result.StdOutInvalidUtf8)
            {
                error = "Git returned invalid UTF-8 for " + description + ".";
                return false;
            }
            if (!result.IsSuccess)
            {
                error = "Git could not complete " + description + ".";
                return false;
            }

            return true;
        }

        private static bool TryRequireExpectedPair(
            CommandResult indexResult,
            CommandResult headResult,
            VerificationRequest request,
            string passName,
            out string error)
        {
            if (!GitUtility.TryResolveExactSubmoduleCommit(
                    indexResult,
                    headResult,
                    request.RelativePath,
                    out string resolvedCommit,
                    out string resolutionError))
            {
                error = "The " + passName +
                        " fresh submodule commit read was inconsistent: " +
                        Sanitize(resolutionError);
                return false;
            }

            if (!string.Equals(
                    resolvedCommit,
                    request.ExpectedCommit,
                    StringComparison.OrdinalIgnoreCase))
            {
                error = "The " + passName +
                        " fresh submodule commit read did not match the inspected commit.";
                return false;
            }

            error = string.Empty;
            return true;
        }

        private static bool TryRequireExpectedOrigin(
            CommandResult originResult,
            VerificationRequest request,
            out string error)
        {
            string origin = originResult?.StdOut?.Trim() ?? string.Empty;
            if (!GitUtility.IsValidRepositoryUrl(origin) ||
                !GitUtility.AreRepositoryUrlsEquivalent(
                    origin,
                    request.RepositoryUrl))
            {
                error =
                    "The fresh initialized submodule origin did not match the inspected repository URL.";
                return false;
            }

            error = string.Empty;
            return true;
        }

        private static VerificationOutcome Unverified(string error)
        {
            return new VerificationOutcome
            {
                Status = PackageDependencySubmoduleCommitVerificationStatus
                    .Unverified,
                Error = Sanitize(error)
            };
        }

        private static VerificationOutcome Unexpected(string error)
        {
            return new VerificationOutcome
            {
                Status = PackageDependencySubmoduleCommitVerificationStatus
                    .Unexpected,
                Error = Sanitize(error)
            };
        }

        private static string Sanitize(string value)
        {
            return PackageDependencyResolutionService.SanitizeDiagnostic(value);
        }
    }

    internal sealed class UnityPackageDependencyInstallExecutor :
        IPackageDependencyInstallExecutor
    {
        internal static UnityPackageDependencyInstallExecutor Instance { get; } =
            new();

        private readonly PackageDependencySubmoduleCommitVerifier
            submoduleCommitVerifier = new();

        private UnityPackageDependencyInstallExecutor()
        {
        }

        public bool IsMutationBusy =>
            GitOperationService.IsBusy ||
            PackageManagerProjectResolutionService.IsBusy ||
            PackageManagerReadOnlyGitInstallService.IsBusy ||
            PackageManagerSubmoduleSnapshot.IsReaderActive ||
            GitSubmoduleInstallProbe.IsReaderActive ||
            AsyncCommandDrainRegistry.IsDraining;

        public bool IsBusyFor(string packageName)
        {
            string expected = packageName?.Trim() ?? string.Empty;
            if (GitOperationService.IsBusy &&
                string.Equals(
                    GitOperationService.ActivePackageName,
                    expected,
                    StringComparison.Ordinal))
            {
                return true;
            }
            if (PackageManagerProjectResolutionService.IsBusy &&
                string.Equals(
                    PackageManagerProjectResolutionService.ActivePackageName,
                    expected,
                    StringComparison.Ordinal))
            {
                return true;
            }

            return PackageManagerReadOnlyGitInstallService.IsBusy &&
                   string.Equals(
                       PackageManagerReadOnlyGitInstallService.ActivePackageName,
                       expected,
                       StringComparison.Ordinal);
        }

        public bool TryInspectRegisteredPackages(
            out IReadOnlyList<PackageDependencyInstalledPackage> packages,
            out string error)
        {
            packages = Array.Empty<PackageDependencyInstalledPackage>();
            error = string.Empty;
            try
            {
                packages = new ReadOnlyCollection<PackageDependencyInstalledPackage>(
                    (UpmPackageInfo.GetAllRegisteredPackages() ??
                     Array.Empty<UpmPackageInfo>())
                    .Where(package => package != null &&
                                      GitUtility.IsValidUpmPackageName(package.name))
                    .Select(CreateInstalledPackage)
                    .OrderBy(package => package.Name, StringComparer.Ordinal)
                    .ToArray());
                return true;
            }
            catch (Exception exception)
            {
                error = Sanitize(
                    "Unity's registered package list could not be inspected: " +
                    exception.Message);
                return false;
            }
        }

        public PackageDependencySubmoduleCommitVerificationStatus
            GetSubmoduleCommitVerification(
                string verificationScopeId,
                string operationId,
                int stepIndex,
                PackageDependencyInstallStep step,
                out string error)
        {
            return submoduleCommitVerifier.GetOrStart(
                verificationScopeId,
                operationId,
                stepIndex,
                step,
                out error);
        }

        public void CancelSubmoduleCommitVerification(
            string verificationScopeId)
        {
            submoduleCommitVerifier.Cancel(verificationScopeId);
        }

        internal void StopSubmoduleCommitVerificationForReload()
        {
            submoduleCommitVerifier.StopForReload();
        }

        public bool TryStart(
            PackageDependencyInstallStep step,
            PackageManagerGitInstallMode mode,
            string dependencyInstallOperationId,
            Action<PackageDependencyPrimitiveCompletion> onComplete,
            out string error)
        {
            error = string.Empty;
            if (step == null)
            {
                error = "A dependency install step is required.";
                return false;
            }

            if (mode == PackageManagerGitInstallMode.GitSubmodule)
            {
                return GitSubmoduleAddService.TryStart(
                    step.RepositoryUrl,
                    step.Revision,
                    step.PackageName,
                    step.Version,
                    step.DependencyFingerprint,
                    step.PackageManifestMetaVerification,
                    step.PackageManifestMetaGuid,
                    step.InspectedCommit,
                    completion => onComplete?.Invoke(
                        new PackageDependencyPrimitiveCompletion(
                            completion?.Success == true,
                            step.PackageName,
                            completion?.Message)),
                    out error);
            }

            if (mode == PackageManagerGitInstallMode.ReadOnlyPackage)
            {
                return PackageManagerReadOnlyGitInstallService.TryStart(
                    step.RepositoryUrl,
                    step.Revision,
                    step.PackageName,
                    step.Version,
                    step.DependencyFingerprint,
                    step.PackageManifestMetaVerification,
                    step.PackageManifestMetaGuid,
                    step.InspectedCommit,
                    dependencyInstallOperationId,
                    completion =>
                    {
                        if (!HasExactReadOnlyCompletionIdentity(
                                step.PackageName,
                                dependencyInstallOperationId,
                                completion))
                        {
                            // The service-level Completed event retains the raw
                            // operation identity and package name. Leave its
                            // completion record intact so the coordinator can
                            // enter a durable recovery block instead of turning
                            // damaged correlation into an ordinary step result.
                            return;
                        }

                        try
                        {
                            onComplete?.Invoke(
                                new PackageDependencyPrimitiveCompletion(
                                    completion?.Success == true,
                                    completion.PackageName,
                                    completion?.Message));
                        }
                        finally
                        {
                            // The primitive's retained completion describes one
                            // dependency step, not the whole coordinated install.
                            PackageManagerReadOnlyGitInstallService
                                .TryConsumeLastCompletion(out _);
                        }
                    },
                    out error);
            }

            error = "The dependency install mode is invalid.";
            return false;
        }

        internal static bool HasExactReadOnlyCompletionIdentity(
            string expectedPackageName,
            string expectedOperationId,
            ReadOnlyGitPackageInstallCompletion completion)
        {
            return completion != null &&
                   Guid.TryParseExact(expectedOperationId, "N", out _) &&
                   string.Equals(
                       expectedOperationId,
                       completion.DependencyInstallOperationId,
                       StringComparison.Ordinal) &&
                   string.Equals(
                       expectedPackageName,
                       completion.PackageName,
                       StringComparison.Ordinal);
        }

        private static PackageDependencyInstalledSource ConvertSource(
            PackageSource source)
        {
            switch (source)
            {
                case PackageSource.Embedded:
                    return PackageDependencyInstalledSource.Embedded;
                case PackageSource.Git:
                    return PackageDependencyInstalledSource.Git;
                case PackageSource.Unknown:
                    return PackageDependencyInstalledSource.Unknown;
                default:
                    return PackageDependencyInstalledSource.Other;
            }
        }

        private static PackageDependencyInstalledPackage CreateInstalledPackage(
            UpmPackageInfo package)
        {
            string repositoryUrl = string.Empty;
            string revision = string.Empty;
            string resolvedCommit = string.Empty;
            bool hasVerifiedRepositoryIdentity = false;
            if (package.source == PackageSource.Git &&
                PackageManagerReadOnlyGitPackage.TryCreateInfo(
                    package,
                    out PackageManagerReadOnlyGitInfo gitInfo,
                    out _) &&
                gitInfo.IsRepositoryRootPackage)
            {
                repositoryUrl = gitInfo.RepositoryUrl;
                revision = gitInfo.Revision;
                resolvedCommit = gitInfo.ResolvedHash;
                hasVerifiedRepositoryIdentity =
                    GitUtility.IsValidRepositoryUrl(repositoryUrl) &&
                    !string.IsNullOrWhiteSpace(revision) &&
                    !string.Equals(revision, ".", StringComparison.Ordinal) &&
                    GitUtility.IsValidBranchName(revision) &&
                    GitUtility.IsValidGitObjectId(resolvedCommit);
            }
            else if (package.source == PackageSource.Embedded &&
                     PackageManagerSubmoduleSnapshot.TryGet(
                         package.name,
                         package.resolvedPath,
                         true,
                         out PackageManagerSubmoduleInfo submoduleInfo))
            {
                repositoryUrl = submoduleInfo.RepositoryUrl;
                resolvedCommit = submoduleInfo.ResolvedCommit;
                hasVerifiedRepositoryIdentity =
                    GitUtility.IsValidRepositoryUrl(repositoryUrl);
            }

            return new PackageDependencyInstalledPackage(
                package.name,
                package.version,
                ConvertSource(package.source),
                package.isDirectDependency,
                package.resolvedPath,
                repositoryUrl,
                revision,
                hasVerifiedRepositoryIdentity,
                resolvedCommit: resolvedCommit);
        }

        private static string Sanitize(string value)
        {
            return PackageDependencyResolutionService.SanitizeDiagnostic(value);
        }
    }

    internal sealed class SessionPackageDependencyInstallStateStore :
        IPackageDependencyInstallStateStore
    {
        private const string ActiveStateKey =
            "MartinCalander.GitSubmoduleManager.DependencyInstall.Active.v1";
        private const string CompletionStateKey =
            "MartinCalander.GitSubmoduleManager.DependencyInstall.Completion.v1";
        private const string RecoveryNotificationStateKey =
            "MartinCalander.GitSubmoduleManager.DependencyInstall.RecoveryNotification.v1";

        internal static SessionPackageDependencyInstallStateStore Instance
            { get; } = new();

        private SessionPackageDependencyInstallStateStore()
        {
        }

        public string LoadActive()
        {
            return SessionState.GetString(ActiveStateKey, string.Empty);
        }

        public void SaveActive(string json)
        {
            SessionState.SetString(ActiveStateKey, json ?? string.Empty);
        }

        public void ClearActive()
        {
            SessionState.EraseString(ActiveStateKey);
        }

        public string LoadCompletion()
        {
            return SessionState.GetString(CompletionStateKey, string.Empty);
        }

        public void SaveCompletion(string json)
        {
            SessionState.SetString(CompletionStateKey, json ?? string.Empty);
        }

        public void ClearCompletion()
        {
            SessionState.EraseString(CompletionStateKey);
        }

        public string LoadRecoveryNotification()
        {
            return SessionState.GetString(
                RecoveryNotificationStateKey,
                string.Empty);
        }

        public void SaveRecoveryNotification(string value)
        {
            SessionState.SetString(
                RecoveryNotificationStateKey,
                value ?? string.Empty);
        }

        public void ClearRecoveryNotification()
        {
            SessionState.EraseString(RecoveryNotificationStateKey);
        }
    }

    /// <summary>
    /// Manually ticked, reload-safe install state machine. Only resolved GitHub
    /// candidates become explicit operations. Registry candidates are omitted
    /// so Unity resolves them transitively from the root package graph.
    /// </summary>
    internal sealed class PackageDependencyInstallCoordinatorCore
    {
        private const int CurrentSchemaVersion = 4;
        private const string PhaseReady = "ready";
        private const string PhaseAttempted = "attempted";
        private const string PhaseWaiting = "waiting";
        private const string PhaseVerifying = "verifying";
        private const string PhaseRecoveryBlocked = "recovery-blocked";
        private static readonly long StepTimeoutTicks =
            TimeSpan.FromMinutes(10d).Ticks;

        private enum InstalledMatchStatus
        {
            Expected,
            Unverified,
            Unexpected
        }

        private readonly IPackageDependencyInstallExecutor executor;
        private readonly IPackageDependencyInstallStateStore store;
        private readonly Func<long> utcTicks;
        private readonly Action<PackageDependencyInstallCompletion> onCompleted;
        private readonly string submoduleCommitVerificationScopeId =
            Guid.NewGuid().ToString("N");
        private PersistedInstallState activeState;
        private Action<PackageDependencyInstallCompletion> activeCallback;

        internal PackageDependencyInstallCoordinatorCore(
            IPackageDependencyInstallExecutor executor,
            IPackageDependencyInstallStateStore store,
            Func<long> utcTicks = null,
            Action<PackageDependencyInstallCompletion> onCompleted = null)
        {
            this.executor = executor ??
                            throw new ArgumentNullException(nameof(executor));
            this.store = store ?? throw new ArgumentNullException(nameof(store));
            this.utcTicks = utcTicks ?? (() => DateTime.UtcNow.Ticks);
            this.onCompleted = onCompleted;
            activeState = LoadActiveState();
        }

        internal bool IsBusy => activeState != null;
        internal bool IsRecoveryBlocked => IsRecoveryBlockedState(activeState);
        internal bool NeedsUpdate =>
            activeState != null &&
            (!IsRecoveryBlocked ||
             !activeState.RecoveryNotificationPublished);
        internal string ActiveRecoveryMessage =>
            IsRecoveryBlocked
                ? activeState.RecoveryMessage
                : string.Empty;
        internal string ActiveOperationId =>
            activeState?.OperationId ?? string.Empty;
        internal string ActiveRootPackageName =>
            activeState?.RootPackageName ?? string.Empty;
        internal string ActiveRepositoryUrl =>
            activeState?.RootRepositoryUrl ?? string.Empty;
        internal string ActiveRevision =>
            activeState?.RootRevision ?? string.Empty;
        internal PackageManagerGitInstallMode? ActiveInstallMode =>
            IsRecoveryBlocked
                ? null
                : activeState?.InstallMode;
        internal int ActiveStepIndex => activeState?.StepIndex ?? -1;
        internal int ActiveStepCount => activeState?.Steps?.Length ?? 0;
        internal PackageDependencyInstallStep ActiveStep =>
            activeState == null || IsRecoveryBlocked ||
            activeState.Steps == null ||
            activeState.StepIndex < 0 ||
            activeState.StepIndex >= activeState.Steps.Length
                ? null
                : FromPersistedStep(activeState.Steps[activeState.StepIndex]);
        internal string ActiveStepPackageName =>
            activeState == null || IsRecoveryBlocked ||
            activeState.Steps == null ||
            activeState.StepIndex < 0 ||
            activeState.StepIndex >= activeState.Steps.Length
                ? string.Empty
                : activeState.Steps[activeState.StepIndex].PackageName;

        internal bool TryStart(
            PackageDependencyInstallRequest request,
            PackageDependencyResolutionPlan plan,
            Action<PackageDependencyInstallCompletion> callback,
            out string error)
        {
            error = string.Empty;
            if (IsBusy)
            {
                error = IsRecoveryBlocked
                    ? activeState.RecoveryMessage
                    : "Another dependency-aware package install is already running.";
                return false;
            }

            string requestError =
                PackageDependencyPreflightRunner.ValidateRequest(request);
            if (!string.IsNullOrEmpty(requestError))
            {
                error = Sanitize(requestError);
                return false;
            }

            if (!TryBuildSteps(request, plan, out var steps, out error))
            {
                error = Sanitize(error);
                return false;
            }

            long now = utcTicks();
            activeState = new PersistedInstallState
            {
                SchemaVersion = CurrentSchemaVersion,
                OperationId = Guid.NewGuid().ToString("N"),
                RootPackageName = request.RootPackageName,
                RootRepositoryUrl = request.RepositoryUrl,
                RootRevision = request.Revision,
                RootInspectedCommit = request.InspectedCommit,
                InstallMode = request.InstallMode,
                AllowsUnverifiedRootPackageManifestMeta =
                    request.PackageManifestMetaPolicy ==
                    PackageManifestMetaPolicy.AllowUnverifiedWithWarning,
                StepIndex = 0,
                Phase = PhaseReady,
                StartedUtcTicks = now,
                LastProgressUtcTicks = now,
                Steps = steps.Select(ToPersistedStep).ToArray()
            };
            activeCallback = callback;
            try
            {
                store.ClearCompletion();
                store.ClearRecoveryNotification();
                SaveActiveState();
                return true;
            }
            catch (Exception exception)
            {
                activeState = null;
                activeCallback = null;
                TryClearActive();
                error = Sanitize(
                    "The dependency install state could not be persisted: " +
                    exception.Message);
                return false;
            }
        }

        internal bool Tick()
        {
            if (activeState == null)
                return false;

            if (IsRecoveryBlocked)
                return PublishRecoveryFailureOnce();

            long now = utcTicks();
            PersistedInstallStep persistedStep =
                activeState.Steps[activeState.StepIndex];
            PackageDependencyInstallStep step = FromPersistedStep(persistedStep);
            bool ownsPrimitive =
                !string.Equals(
                    activeState.Phase,
                    PhaseReady,
                    StringComparison.Ordinal);
            bool ownedPrimitiveBusy =
                ownsPrimitive && executor.IsBusyFor(step.PackageName);
            bool timedOut = activeState.LastProgressUtcTicks <= 0L ||
                            (now >= activeState.LastProgressUtcTicks &&
                             now - activeState.LastProgressUtcTicks >=
                             StepTimeoutTicks);

            if (!executor.TryInspectRegisteredPackages(
                    out IReadOnlyList<PackageDependencyInstalledPackage> packages,
                    out string inspectionError))
            {
                if (ownedPrimitiveBusy)
                {
                    executor.CancelSubmoduleCommitVerification(
                        submoduleCommitVerificationScopeId);
                    return false;
                }

                Finish(
                    false,
                    string.IsNullOrWhiteSpace(inspectionError)
                        ? "Unity's registered package state could not be inspected."
                        : inspectionError);
                return true;
            }

            if (packages == null)
            {
                if (ownedPrimitiveBusy)
                {
                    executor.CancelSubmoduleCommitVerification(
                        submoduleCommitVerificationScopeId);
                    return false;
                }

                Finish(
                    false,
                    "Unity returned an invalid registered package state.");
                return true;
            }

            if (timedOut && !ownedPrimitiveBusy && executor.IsMutationBusy)
            {
                Finish(
                    false,
                    "Dependency installation timed out while unrelated package " +
                    "mutation activity prevented terminal verification.");
                return true;
            }

            PackageDependencyInstalledPackage[] matchingPackages = packages
                .Where(package => package != null &&
                    string.Equals(
                        package.Name,
                        step.PackageName,
                        StringComparison.Ordinal))
                .Take(2)
                .ToArray();
            if (matchingPackages.Length > 1)
            {
                if (ownedPrimitiveBusy ||
                    (executor.IsMutationBusy && !timedOut))
                {
                    executor.CancelSubmoduleCommitVerification(
                        submoduleCommitVerificationScopeId);
                    return false;
                }

                Finish(
                    false,
                    $"Unity registered {step.PackageName} more than once.");
                return true;
            }

            if (matchingPackages.Length == 1)
            {
                if (ownedPrimitiveBusy ||
                    (executor.IsMutationBusy && !timedOut))
                {
                    executor.CancelSubmoduleCommitVerification(
                        submoduleCommitVerificationScopeId);
                    return false;
                }

                InstalledMatchStatus matchStatus = GetInstalledMatchStatus(
                    matchingPackages[0],
                    step,
                    activeState.InstallMode);
                if (matchStatus == InstalledMatchStatus.Expected)
                {
                    if (activeState.InstallMode ==
                            PackageManagerGitInstallMode.GitSubmodule)
                    {
                        PackageDependencySubmoduleCommitVerificationStatus
                            commitStatus = executor
                                .GetSubmoduleCommitVerification(
                                    submoduleCommitVerificationScopeId,
                                    activeState.OperationId,
                                    activeState.StepIndex,
                                    step,
                                    out string commitError);
                        if (commitStatus ==
                            PackageDependencySubmoduleCommitVerificationStatus
                                .Pending)
                        {
                            if (!timedOut)
                                return false;

                            Finish(
                                false,
                                $"The fresh parent gitlink and initialized submodule HEAD proof for {step.PackageName} did not complete before the timeout.");
                            return true;
                        }

                        if (commitStatus ==
                            PackageDependencySubmoduleCommitVerificationStatus
                                .Expected)
                        {
                            Advance();
                            return true;
                        }

                        if (commitStatus ==
                                PackageDependencySubmoduleCommitVerificationStatus
                                    .Unverified &&
                            ownsPrimitive &&
                            !timedOut)
                        {
                            return false;
                        }

                        string commitDetail = string.IsNullOrWhiteSpace(
                            commitError)
                            ? string.Empty
                            : " " + Sanitize(commitError);
                        Finish(
                            false,
                            commitStatus ==
                                PackageDependencySubmoduleCommitVerificationStatus
                                    .Unverified
                                ? $"The fresh parent gitlink, initialized submodule HEAD, and repository identity of {step.PackageName} could not be verified before the timeout.{commitDetail}"
                                : $"{step.PackageName} failed fresh parent gitlink, initialized submodule HEAD, or repository identity verification.{commitDetail}");
                        return true;
                    }

                    Advance();
                    return true;
                }

                if (matchStatus == InstalledMatchStatus.Unverified &&
                    ownsPrimitive && !timedOut)
                {
                    return false;
                }

                Finish(
                    false,
                    matchStatus == InstalledMatchStatus.Unverified
                        ? $"The exact installed identity and package.json.meta evidence of {step.PackageName} could not be verified before the timeout."
                        : $"{step.PackageName} is registered with an unexpected source, version, repository identity, or package.json.meta GUID.");
                return true;
            }

            executor.CancelSubmoduleCommitVerification(
                submoduleCommitVerificationScopeId);

            if (timedOut)
            {
                if (ownedPrimitiveBusy)
                {
                    executor.CancelSubmoduleCommitVerification(
                        submoduleCommitVerificationScopeId);
                    return false;
                }

                Finish(false, "Dependency installation timed out.");
                return true;
            }

            if (string.Equals(activeState.Phase, PhaseReady, StringComparison.Ordinal))
            {
                if (executor.IsMutationBusy)
                    return false;

                activeState.Phase = PhaseAttempted;
                activeState.LastProgressUtcTicks = now;
                SaveActiveStateOrFinish();
                if (activeState == null)
                    return true;

                string operationId = activeState.OperationId;
                bool started = executor.TryStart(
                        step,
                        activeState.InstallMode,
                        activeState.OperationId,
                        NotifyPrimitiveCompletion,
                        out string startError);
                if (activeState == null ||
                    !string.Equals(
                        activeState.OperationId,
                        operationId,
                        StringComparison.Ordinal))
                {
                    return true;
                }

                if (!started)
                {
                    // A synchronous callback is authoritative. Do not replace a
                    // success callback's verifying state with a start failure.
                    if (string.Equals(
                            activeState.Phase,
                            PhaseAttempted,
                            StringComparison.Ordinal))
                    {
                        Finish(
                            false,
                            string.IsNullOrWhiteSpace(startError)
                                ? $"Installation of {step.PackageName} could not be started."
                                : startError);
                    }
                    return true;
                }

                // TryStart implementations may complete synchronously. Only the
                // untouched attempted marker may transition to waiting.
                if (string.Equals(
                        activeState.Phase,
                        PhaseAttempted,
                        StringComparison.Ordinal))
                {
                    activeState.Phase = PhaseWaiting;
                    SaveActiveStateOrFinish();
                }
                return true;
            }

            // Attempted, waiting, and verifying states are never reissued. A
            // callback or authoritative registered state can still arrive after
            // reload, so retain the single-flight marker until its deadline.
            return false;
        }

        internal void NotifyPrimitiveCompletion(
            PackageDependencyPrimitiveCompletion completion)
        {
            if (activeState == null || completion == null ||
                (!string.Equals(activeState.Phase, PhaseAttempted, StringComparison.Ordinal) &&
                 !string.Equals(activeState.Phase, PhaseWaiting, StringComparison.Ordinal)))
            {
                return;
            }

            PersistedInstallStep step = activeState.Steps[activeState.StepIndex];
            if (!string.Equals(
                    step.PackageName,
                    completion.PackageName,
                    StringComparison.Ordinal))
            {
                return;
            }

            if (!completion.Success)
            {
                Finish(
                    false,
                    string.IsNullOrWhiteSpace(completion.Message)
                        ? $"Installation of {step.PackageName} failed."
                        : completion.Message);
                return;
            }

            activeState.Phase = PhaseVerifying;
            activeState.LastProgressUtcTicks = utcTicks();
            SaveActiveStateOrFinish();
        }

        internal bool TryBlockForPrimitiveCorrelationFailure(
            string message,
            out string error)
        {
            error = string.Empty;
            if (activeState == null)
            {
                error = "No dependency install operation is active.";
                return false;
            }

            if (IsRecoveryBlocked)
                return true;

            string previousPhase = activeState.Phase;
            string previousRecoveryMessage = activeState.RecoveryMessage;
            long previousProgress = activeState.LastProgressUtcTicks;
            activeState.Phase = PhaseRecoveryBlocked;
            activeState.RecoveryMessage = Sanitize(
                string.IsNullOrWhiteSpace(message)
                    ? "The read-only package completion could not be correlated safely."
                    : message);
            activeState.LastProgressUtcTicks = utcTicks();
            try
            {
                store.ClearRecoveryNotification();
                SaveActiveState();
                return true;
            }
            catch (Exception exception)
            {
                activeState.Phase = previousPhase;
                activeState.RecoveryMessage = previousRecoveryMessage;
                activeState.LastProgressUtcTicks = previousProgress;
                error = Sanitize(
                    "The dependency install recovery block could not be persisted: " +
                    exception.Message);
                return false;
            }
        }

        internal bool TryConsumeLastCompletion(
            out PackageDependencyInstallCompletion completion)
        {
            return TryReadLastCompletion(true, out completion);
        }

        internal bool TryGetLastCompletion(
            out PackageDependencyInstallCompletion completion)
        {
            return TryReadLastCompletion(false, out completion);
        }

        private bool TryReadLastCompletion(
            bool consume,
            out PackageDependencyInstallCompletion completion)
        {
            completion = null;
            string json;
            try
            {
                json = store.LoadCompletion();
            }
            catch
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(json))
                return false;

            try
            {
                PersistedCompletion persisted =
                    JsonUtility.FromJson<PersistedCompletion>(json);
                bool hasValidIdentity = persisted != null &&
                    GitUtility.IsValidUpmPackageName(
                        persisted.RootPackageName) &&
                    GitUtility.IsValidRepositoryUrl(
                        persisted.RootRepositoryUrl) &&
                    !string.IsNullOrWhiteSpace(persisted.RootRevision) &&
                    !string.Equals(
                        persisted.RootRevision,
                        ".",
                        StringComparison.Ordinal) &&
                    GitUtility.IsValidBranchName(persisted.RootRevision);
                if (persisted == null ||
                    persisted.SchemaVersion != CurrentSchemaVersion ||
                    !IsValidMode(persisted.InstallMode) ||
                    (persisted.IsRecovery
                        ? persisted.Success ||
                          string.IsNullOrWhiteSpace(persisted.Message)
                        : !hasValidIdentity))
                {
                    TryClearCompletion();
                    return false;
                }
                if (persisted.IsRecovery &&
                    !IsRecoveryNotificationPublished())
                {
                    // Completion persistence is phase one of recovery
                    // publication. Do not expose it until marker ownership has
                    // committed successfully.
                    return false;
                }

                if (consume)
                    store.ClearCompletion();

                completion = new PackageDependencyInstallCompletion(
                    persisted.Success,
                    Sanitize(persisted.Message),
                    persisted.RootPackageName,
                    persisted.InstallMode,
                    persisted.RootRepositoryUrl,
                    persisted.RootRevision);
                return true;
            }
            catch
            {
                TryClearCompletion();
                return false;
            }
        }

        internal static bool TryBuildSteps(
            PackageDependencyInstallRequest request,
            PackageDependencyResolutionPlan plan,
            out IReadOnlyList<PackageDependencyInstallStep> steps,
            out string error)
        {
            steps = Array.Empty<PackageDependencyInstallStep>();
            error = string.Empty;
            string requestError =
                PackageDependencyPreflightRunner.ValidateRequest(request);
            if (!string.IsNullOrWhiteSpace(requestError))
            {
                error = requestError;
                return false;
            }

            if (!GitUtility.IsValidSemanticVersion(request.RootVersion))
            {
                error = "The root package version is invalid.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(request.Revision) ||
                string.Equals(request.Revision, ".", StringComparison.Ordinal))
            {
                error = "The root install request must use an explicit Git revision.";
                return false;
            }

            if (plan == null || !plan.IsComplete ||
                plan.HasBlockingIssues || plan.Revision <= 0)
            {
                error = "A complete, unambiguous dependency plan is required.";
                return false;
            }

            var directRequirements =
                new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (PackageManifestDependency dependency in
                     request.Dependencies ?? Array.Empty<PackageManifestDependency>())
            {
                string name = dependency?.Name ?? string.Empty;
                string version = dependency?.Version ?? string.Empty;
                if (!GitUtility.IsValidUpmPackageName(name) ||
                    string.IsNullOrWhiteSpace(version) ||
                    !string.Equals(version, version.Trim(), StringComparison.Ordinal) ||
                    string.Equals(
                        name,
                        request.RootPackageName,
                        StringComparison.Ordinal) ||
                    directRequirements.ContainsKey(name))
                {
                    error =
                        "The root package dependency graph is invalid or contains a duplicate package.";
                    return false;
                }

                directRequirements.Add(name, version);
            }

            PackageDependencyResolutionResult[] results = plan.Results
                .Where(result => result?.Requirement != null)
                .OrderBy(result => result.Requirement.Name, StringComparer.Ordinal)
                .ToArray();
            if (results.Any(result =>
                    result.Status != PackageDependencyResolutionStatus.Resolved ||
                    result.SelectedCandidate == null))
            {
                error = "Every missing dependency must have exactly one resolved source.";
                return false;
            }

            var byName = new Dictionary<string, PackageDependencyResolutionResult>(
                StringComparer.Ordinal);
            var children = new Dictionary<string, SortedSet<string>>(
                StringComparer.Ordinal);
            foreach (PackageDependencyResolutionResult result in results)
            {
                string name = result.Requirement.Name;
                string version = result.Requirement.Version;
                if (!GitUtility.IsValidUpmPackageName(name) ||
                    string.Equals(
                        name,
                        request.RootPackageName,
                        StringComparison.Ordinal) ||
                    string.IsNullOrWhiteSpace(version) ||
                    !string.Equals(version, version.Trim(), StringComparison.Ordinal) ||
                    result.Requirement.RequestedBy == null ||
                    result.Requirement.RequestedBy.Count == 0 ||
                    byName.ContainsKey(name))
                {
                    error = "The dependency plan contains an invalid or duplicate package.";
                    return false;
                }

                PackageDependencyCandidate candidate = result.SelectedCandidate;
                if (!string.Equals(
                        candidate.PackageName,
                        name,
                        StringComparison.Ordinal) ||
                    !string.Equals(
                        candidate.Version,
                        version,
                        StringComparison.Ordinal))
                {
                    error =
                        $"The resolved source for {name} does not match the requested package identity.";
                    return false;
                }

                switch (candidate.Source)
                {
                    case PackageDependencyCandidateSource.GitHub:
                        if (!GitUtility.IsValidRepositoryUrl(
                                candidate.RepositoryUrl) ||
                            string.IsNullOrWhiteSpace(
                                candidate.RepositoryBranch) ||
                            string.Equals(
                                candidate.RepositoryBranch,
                                ".",
                                StringComparison.Ordinal) ||
                            !string.Equals(
                                candidate.RepositoryBranch,
                                candidate.RepositoryBranch.Trim(),
                                StringComparison.Ordinal) ||
                            !GitUtility.IsValidBranchName(
                                candidate.RepositoryBranch) ||
                            !GitUtility.IsValidPackageDependencyFingerprint(
                                candidate.DependencyFingerprint) ||
                            candidate.PackageManifestMetaVerification !=
                                PackageManifestMetaVerification.Verified ||
                            !GitSubmoduleInstallProbeSnapshot.IsValidMetaGuid(
                                candidate.PackageManifestMetaGuid) ||
                            !GitUtility.IsValidGitObjectId(
                                candidate.RepositoryCommit))
                        {
                            error =
                                $"The GitHub source for {name} must provide an explicit valid repository branch and inspected commit, dependency fingerprint, and verified root package.json.meta GUID.";
                            return false;
                        }
                        break;
                    case PackageDependencyCandidateSource.UnityRegistry:
                    case PackageDependencyCandidateSource.CustomRegistry:
                        if (string.IsNullOrWhiteSpace(candidate.SourceName))
                        {
                            error =
                                $"The registry source for {name} is incomplete.";
                            return false;
                        }
                        break;
                    default:
                        error = $"The resolved source for {name} is invalid.";
                        return false;
                }

                byName.Add(name, result);
            }

            foreach (PackageDependencyResolutionResult result in results)
            {
                string name = result.Requirement.Name;
                bool requestedByRoot = false;
                foreach (string requestedBy in result.Requirement.RequestedBy)
                {
                    if (!GitUtility.IsValidUpmPackageName(requestedBy) ||
                        string.Equals(requestedBy, name, StringComparison.Ordinal))
                    {
                        error =
                            "The dependency plan contains an invalid request edge.";
                        return false;
                    }

                    if (string.Equals(
                            requestedBy,
                            request.RootPackageName,
                            StringComparison.Ordinal))
                    {
                        requestedByRoot = true;
                        if (!directRequirements.TryGetValue(
                                name,
                                out string directVersion) ||
                            !string.Equals(
                                directVersion,
                                result.Requirement.Version,
                                StringComparison.Ordinal))
                        {
                            error =
                                $"The dependency plan for {name} does not match the root install request.";
                            return false;
                        }
                    }
                    else if (!byName.ContainsKey(requestedBy))
                    {
                        error =
                            $"The dependency plan for {name} is orphaned from the root install graph.";
                        return false;
                    }

                    if (!children.TryGetValue(requestedBy, out var values))
                    {
                        values = new SortedSet<string>(StringComparer.Ordinal);
                        children.Add(requestedBy, values);
                    }
                    values.Add(name);
                }

                if (directRequirements.ContainsKey(name) && !requestedByRoot)
                {
                    error =
                        $"The dependency plan for {name} omits its root request edge.";
                    return false;
                }
            }

            var ordered = new List<PackageDependencyInstallStep>();
            var visitState = new Dictionary<string, int>(StringComparer.Ordinal);
            string traversalError = string.Empty;
            bool Visit(string packageName)
            {
                if (visitState.TryGetValue(packageName, out int state))
                {
                    if (state == 1)
                    {
                        traversalError = "The dependency plan contains a cycle.";
                        return false;
                    }
                    return true;
                }

                visitState[packageName] = 1;
                if (children.TryGetValue(packageName, out var dependencies))
                {
                    foreach (string dependency in dependencies)
                    {
                        if (!Visit(dependency))
                            return false;
                    }
                }

                visitState[packageName] = 2;
                if (!byName.TryGetValue(packageName, out var result))
                    return true;

                PackageDependencyCandidate candidate = result.SelectedCandidate;
                if (candidate.Source != PackageDependencyCandidateSource.GitHub)
                    return true;

                ordered.Add(new PackageDependencyInstallStep(
                    packageName,
                    result.Requirement.Version,
                    candidate.RepositoryUrl,
                    candidate.RepositoryBranch,
                    false,
                    candidate.DependencyFingerprint,
                    candidate.PackageManifestMetaVerification,
                    candidate.PackageManifestMetaGuid,
                    candidate.RepositoryCommit));
                return true;
            }

            if (!Visit(request.RootPackageName))
            {
                error = traversalError;
                return false;
            }
            if (byName.Keys.Any(packageName =>
                    !visitState.TryGetValue(packageName, out int state) ||
                    state != 2))
            {
                error =
                    "The dependency plan contains a package orphaned from the root install graph.";
                return false;
            }

            ordered.Add(new PackageDependencyInstallStep(
                request.RootPackageName,
                request.RootVersion,
                request.RepositoryUrl,
                request.Revision,
                true,
                GitUtility.ComputePackageDependencyFingerprint(
                    request.Dependencies),
                request.PackageManifestMetaVerification,
                request.PackageManifestMetaGuid,
                request.InspectedCommit));
            if (ordered.Count > PackageDependencyResolutionService.MaximumRequirementCount + 1)
            {
                error = "The dependency install plan exceeds the safety limit.";
                return false;
            }

            steps = new ReadOnlyCollection<PackageDependencyInstallStep>(
                ordered.ToArray());
            return true;
        }

        private void Advance()
        {
            executor.CancelSubmoduleCommitVerification(
                submoduleCommitVerificationScopeId);
            activeState.StepIndex++;
            if (activeState.StepIndex >= activeState.Steps.Length)
            {
                Finish(
                    true,
                    $"Installed {activeState.RootPackageName} and its missing dependencies.");
                return;
            }

            activeState.Phase = PhaseReady;
            activeState.LastProgressUtcTicks = utcTicks();
            SaveActiveStateOrFinish();
        }

        private void Finish(bool success, string message)
        {
            if (activeState == null)
                return;

            executor.CancelSubmoduleCommitVerification(
                submoduleCommitVerificationScopeId);

            PackageDependencyInstallCompletion completion =
                new PackageDependencyInstallCompletion(
                    success,
                    Sanitize(message),
                    activeState.RootPackageName,
                    activeState.InstallMode,
                    activeState.RootRepositoryUrl,
                    activeState.RootRevision);
            var persisted = new PersistedCompletion
            {
                SchemaVersion = CurrentSchemaVersion,
                Success = completion.Success,
                Message = completion.Message,
                RootPackageName = completion.RootPackageName,
                RootRepositoryUrl = completion.RootRepositoryUrl,
                RootRevision = completion.RootRevision,
                InstallMode = completion.InstallMode
            };
            Action<PackageDependencyInstallCompletion> callback = activeCallback;
            activeState = null;
            activeCallback = null;
            TryClearActive();
            TryClearRecoveryNotification();
            try
            {
                store.SaveCompletion(JsonUtility.ToJson(persisted));
            }
            catch (Exception exception)
            {
                Debug.LogWarning(Sanitize(
                    "Dependency install completion could not be retained: " +
                    exception.Message));
            }

            Invoke(callback, completion);
            Invoke(onCompleted, completion);
        }

        private void SaveActiveStateOrFinish()
        {
            try
            {
                SaveActiveState();
            }
            catch (Exception exception)
            {
                Finish(
                    false,
                    "Dependency install progress could not be persisted: " +
                    exception.Message);
            }
        }

        private void SaveActiveState()
        {
            if (activeState == null)
                throw new InvalidOperationException("No active install state exists.");
            store.SaveActive(JsonUtility.ToJson(activeState));
        }

        private PersistedInstallState LoadActiveState()
        {
            string json;
            try
            {
                json = store.LoadActive();
            }
            catch (Exception exception)
            {
                return CreateRecoveryBlockedState(
                    null,
                    "The persisted dependency install state could not be read: " +
                    exception.Message);
            }

            if (string.IsNullOrWhiteSpace(json))
                return null;
            try
            {
                PersistedInstallState state =
                    JsonUtility.FromJson<PersistedInstallState>(json);
                if (!IsValidState(state))
                    return CreateRecoveryBlockedState(state, string.Empty);
                if (IsRecoveryBlockedState(state))
                    state.RecoveryNotificationPublished =
                        IsRecoveryNotificationPublished();
                return state;
            }
            catch (Exception exception)
            {
                return CreateRecoveryBlockedState(
                    null,
                    "The persisted dependency install state is malformed: " +
                    exception.Message);
            }
        }

        private PersistedInstallState CreateRecoveryBlockedState(
            PersistedInstallState parsedState,
            string detail)
        {
            bool notificationPublished = IsRecoveryNotificationPublished();

            string packageName = GitUtility.IsValidUpmPackageName(
                parsedState?.RootPackageName)
                ? parsedState.RootPackageName.Trim()
                : string.Empty;
            string repositoryUrl = GitUtility.IsValidRepositoryUrl(
                parsedState?.RootRepositoryUrl)
                ? parsedState.RootRepositoryUrl.Trim()
                : string.Empty;
            string revision = !string.IsNullOrWhiteSpace(
                                  parsedState?.RootRevision) &&
                              !string.Equals(
                                  parsedState.RootRevision,
                                  ".",
                                  StringComparison.Ordinal) &&
                              GitUtility.IsValidBranchName(
                                  parsedState.RootRevision)
                ? parsedState.RootRevision.Trim()
                : string.Empty;
            string operationId = Guid.TryParseExact(
                parsedState?.OperationId,
                "N",
                out _)
                ? parsedState.OperationId.Trim()
                : string.Empty;
            PackageManagerGitInstallMode mode =
                parsedState != null && IsValidMode(parsedState.InstallMode)
                    ? parsedState.InstallMode
                    : PackageManagerGitInstallMode.GitSubmodule;
            string safeDetail = string.IsNullOrWhiteSpace(detail)
                ? string.Empty
                : " " + Sanitize(detail).Trim();
            return new PersistedInstallState
            {
                SchemaVersion = CurrentSchemaVersion,
                OperationId = operationId,
                RootPackageName = packageName,
                RootRepositoryUrl = repositoryUrl,
                RootRevision = revision,
                InstallMode = mode,
                StepIndex = -1,
                Phase = PhaseRecoveryBlocked,
                StartedUtcTicks = parsedState?.StartedUtcTicks ?? utcTicks(),
                LastProgressUtcTicks = utcTicks(),
                Steps = Array.Empty<PersistedInstallStep>(),
                RecoveryNotificationPublished = notificationPublished,
                RecoveryMessage =
                    "A persisted dependency install record is damaged and a step may already have issued a package " +
                    "mutation. No step was issued again, the original persisted value was preserved as recovery " +
                    "evidence, and this project remains blocked from package mutations. Inspect " +
                    "Packages/manifest.json, Packages/, .gitmodules, and the parent Git index; repair only state " +
                    "whose ownership you can prove, then restart the Unity Editor to clear this session recovery " +
                    "block." + safeDetail
            };
        }

        private bool IsRecoveryNotificationPublished()
        {
            try
            {
                return !string.IsNullOrWhiteSpace(
                    store.LoadRecoveryNotification());
            }
            catch
            {
                // If publication ownership cannot be read, suppress another
                // callback rather than risking duplicate terminal presentation.
                return true;
            }
        }

        private bool PublishRecoveryFailureOnce()
        {
            if (!IsRecoveryBlocked || activeState.RecoveryNotificationPublished)
                return false;

            var completion = new PackageDependencyInstallCompletion(
                false,
                activeState.RecoveryMessage,
                activeState.RootPackageName,
                activeState.InstallMode,
                activeState.RootRepositoryUrl,
                activeState.RootRevision);
            try
            {
                // Retain the terminal outcome before recording notification
                // ownership. If completion persistence fails, the notification
                // remains eligible for a later safe retry.
                store.SaveCompletion(JsonUtility.ToJson(new PersistedCompletion
                {
                    SchemaVersion = CurrentSchemaVersion,
                    IsRecovery = true,
                    Success = false,
                    Message = completion.Message,
                    RootPackageName = completion.RootPackageName,
                    RootRepositoryUrl = completion.RootRepositoryUrl,
                    RootRevision = completion.RootRevision,
                    InstallMode = completion.InstallMode
                }));
            }
            catch (Exception exception)
            {
                Debug.LogWarning(Sanitize(
                    "Dependency install recovery completion could not be retained: " +
                    exception.Message));
                return false;
            }

            try
            {
                // Record ownership before invoking any observer. The active
                // state remains in place and is never consumed automatically.
                store.SaveRecoveryNotification(
                    string.IsNullOrWhiteSpace(activeState.OperationId)
                        ? "published"
                        : activeState.OperationId);
                activeState.RecoveryNotificationPublished = true;
            }
            catch (Exception exception)
            {
                Debug.LogWarning(Sanitize(
                    "Dependency install recovery notification ownership could not be retained: " +
                    exception.Message));
                return false;
            }

            Action<PackageDependencyInstallCompletion> callback = activeCallback;
            activeCallback = null;
            Invoke(callback, completion);
            Invoke(onCompleted, completion);
            return true;
        }

        private static bool IsValidState(PersistedInstallState state)
        {
            if (state == null ||
                state.SchemaVersion != CurrentSchemaVersion ||
                !Guid.TryParseExact(state.OperationId, "N", out _) ||
                !GitUtility.IsValidUpmPackageName(state.RootPackageName) ||
                !GitUtility.IsValidRepositoryUrl(state.RootRepositoryUrl) ||
                string.IsNullOrWhiteSpace(state.RootRevision) ||
                string.Equals(
                    state.RootRevision,
                    ".",
                    StringComparison.Ordinal) ||
                !GitUtility.IsValidBranchName(state.RootRevision) ||
                !IsValidMode(state.InstallMode) ||
                (state.InstallMode ==
                     PackageManagerGitInstallMode.ReadOnlyPackage &&
                 state.AllowsUnverifiedRootPackageManifestMeta) ||
                !GitUtility.IsValidGitObjectId(
                    state.RootInspectedCommit) ||
                state.Steps == null ||
                state.Steps.Length == 0 ||
                state.Steps.Length >
                    PackageDependencyResolutionService.MaximumRequirementCount + 1 ||
                state.StepIndex < 0 ||
                state.StepIndex >= state.Steps.Length ||
                (state.Phase != PhaseReady &&
                 state.Phase != PhaseAttempted &&
                 state.Phase != PhaseWaiting &&
                 state.Phase != PhaseVerifying &&
                 state.Phase != PhaseRecoveryBlocked) ||
                state.StartedUtcTicks <= 0L ||
                state.LastProgressUtcTicks <= 0L)
            {
                return false;
            }

            if (state.Phase == PhaseRecoveryBlocked)
            {
                return !string.IsNullOrWhiteSpace(state.RecoveryMessage);
            }

            int rootCount = 0;
            var packageNames = new HashSet<string>(StringComparer.Ordinal);
            for (int index = 0; index < state.Steps.Length; index++)
            {
                PersistedInstallStep step = state.Steps[index];
                if (step == null ||
                    !GitUtility.IsValidUpmPackageName(step.PackageName) ||
                    !packageNames.Add(step.PackageName) ||
                    !GitUtility.IsValidSemanticVersion(step.Version) ||
                    !GitUtility.IsValidPackageDependencyFingerprint(
                        step.DependencyFingerprint) ||
                    !IsValidPackageManifestMetaEvidence(
                        step.PackageManifestMetaVerification,
                        step.PackageManifestMetaGuid) ||
                    ((!step.IsRoot ||
                      !state.AllowsUnverifiedRootPackageManifestMeta) &&
                     step.PackageManifestMetaVerification !=
                         PackageManifestMetaVerification.Verified) ||
                    !GitUtility.IsValidRepositoryUrl(step.RepositoryUrl) ||
                    string.IsNullOrWhiteSpace(step.Revision) ||
                    string.Equals(step.Revision, ".", StringComparison.Ordinal) ||
                    !GitUtility.IsValidBranchName(step.Revision) ||
                    !GitUtility.IsValidGitObjectId(step.InspectedCommit))
                {
                    return false;
                }
                if (step.IsRoot)
                    rootCount++;
                if (step.IsRoot != (index == state.Steps.Length - 1))
                    return false;
            }

            return rootCount == 1 &&
                   string.Equals(
                       state.Steps[state.Steps.Length - 1].PackageName,
                       state.RootPackageName,
                       StringComparison.Ordinal) &&
                   string.Equals(
                       state.Steps[state.Steps.Length - 1].RepositoryUrl,
                       state.RootRepositoryUrl,
                       StringComparison.Ordinal) &&
                   string.Equals(
                       state.Steps[state.Steps.Length - 1].Revision,
                       state.RootRevision,
                       StringComparison.Ordinal) &&
                   string.Equals(
                       state.Steps[state.Steps.Length - 1].InspectedCommit,
                       state.RootInspectedCommit,
                       StringComparison.OrdinalIgnoreCase);
        }

        private static InstalledMatchStatus GetInstalledMatchStatus(
            PackageDependencyInstalledPackage installed,
            PackageDependencyInstallStep step,
            PackageManagerGitInstallMode mode)
        {
            if (installed == null || step == null)
                return InstalledMatchStatus.Unexpected;
            if (!string.Equals(
                    installed.Name,
                    step.PackageName,
                    StringComparison.Ordinal) ||
                string.IsNullOrWhiteSpace(step.Version) ||
                !string.Equals(
                    installed.Version,
                    step.Version,
                    StringComparison.Ordinal))
            {
                return InstalledMatchStatus.Unexpected;
            }

            string installedDependencyFingerprint =
                ResolveInstalledDependencyFingerprint(installed);
            if (!GitUtility.IsValidPackageDependencyFingerprint(
                    installedDependencyFingerprint))
            {
                return InstalledMatchStatus.Unverified;
            }

            if (!string.Equals(
                    installedDependencyFingerprint,
                    step.DependencyFingerprint,
                    StringComparison.Ordinal))
            {
                return InstalledMatchStatus.Unexpected;
            }

            InstalledMatchStatus metaMatchStatus =
                GetInstalledPackageManifestMetaMatchStatus(installed, step);
            if (metaMatchStatus != InstalledMatchStatus.Expected)
                return metaMatchStatus;

            if (mode == PackageManagerGitInstallMode.ReadOnlyPackage)
            {
                if (installed.Source != PackageDependencyInstalledSource.Git ||
                    !installed.IsDirectDependency)
                {
                    return InstalledMatchStatus.Unexpected;
                }

                if (!installed.HasVerifiedRepositoryIdentity)
                    return InstalledMatchStatus.Unverified;

                if (!GitUtility.IsValidGitObjectId(step.InspectedCommit) ||
                    !GitUtility.IsValidGitObjectId(installed.ResolvedCommit))
                {
                    return InstalledMatchStatus.Unverified;
                }

                return GitUtility.AreRepositoryUrlsEquivalent(
                           installed.RepositoryUrl,
                           step.RepositoryUrl) &&
                       string.Equals(
                           installed.Revision,
                           step.InspectedCommit,
                           StringComparison.OrdinalIgnoreCase) &&
                       string.Equals(
                           installed.ResolvedCommit,
                           step.InspectedCommit,
                           StringComparison.OrdinalIgnoreCase)
                    ? InstalledMatchStatus.Expected
                    : InstalledMatchStatus.Unexpected;
            }

            if (mode != PackageManagerGitInstallMode.GitSubmodule ||
                installed.Source != PackageDependencyInstalledSource.Embedded ||
                !installed.IsDirectDependency)
            {
                return InstalledMatchStatus.Unexpected;
            }

            string expectedPath = Path.Combine(
                GitUtility.ProjectRoot,
                "Packages",
                step.PackageName);
            string expected =
                PackageManagerSubmoduleSnapshotData.NormalizeFullPath(expectedPath);
            string actual =
                PackageManagerSubmoduleSnapshotData.NormalizeFullPath(
                    installed.ResolvedPath);
            StringComparison comparison = Path.DirectorySeparatorChar == '\\'
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            if (string.IsNullOrEmpty(expected) ||
                !string.Equals(expected, actual, comparison))
            {
                return InstalledMatchStatus.Unexpected;
            }

            if (!installed.HasVerifiedRepositoryIdentity)
                return InstalledMatchStatus.Unverified;

            if (!GitUtility.IsValidGitObjectId(step.InspectedCommit) ||
                !GitUtility.IsValidGitObjectId(installed.ResolvedCommit))
            {
                return InstalledMatchStatus.Unverified;
            }

            // The asynchronous submodule snapshot accepts a commit only when
            // the worktree HEAD and the parent index gitlink match exactly.
            return GitUtility.AreRepositoryUrlsEquivalent(
                       installed.RepositoryUrl,
                       step.RepositoryUrl) &&
                   string.Equals(
                       installed.ResolvedCommit,
                       step.InspectedCommit,
                       StringComparison.OrdinalIgnoreCase)
                ? InstalledMatchStatus.Expected
                : InstalledMatchStatus.Unexpected;
        }

        private static InstalledMatchStatus
            GetInstalledPackageManifestMetaMatchStatus(
                PackageDependencyInstalledPackage installed,
                PackageDependencyInstallStep step)
        {
            if (step.PackageManifestMetaVerification ==
                    PackageManifestMetaVerification.Unverified)
            {
                return string.IsNullOrWhiteSpace(step.PackageManifestMetaGuid)
                    ? InstalledMatchStatus.Expected
                    : InstalledMatchStatus.Unexpected;
            }

            if (step.PackageManifestMetaVerification !=
                    PackageManifestMetaVerification.Verified ||
                !GitSubmoduleInstallProbeSnapshot.IsValidMetaGuid(
                    step.PackageManifestMetaGuid))
            {
                return InstalledMatchStatus.Unexpected;
            }

            if (installed?.PackageManifestMetaVerification ==
                    PackageManifestMetaVerification.Verified &&
                GitSubmoduleInstallProbeSnapshot.IsValidMetaGuid(
                    installed.PackageManifestMetaGuid))
            {
                return string.Equals(
                    installed.PackageManifestMetaGuid,
                    step.PackageManifestMetaGuid,
                    StringComparison.OrdinalIgnoreCase)
                    ? InstalledMatchStatus.Expected
                    : InstalledMatchStatus.Unexpected;
            }

            string resolvedPath = installed?.ResolvedPath?.Trim() ?? string.Empty;
            if (string.IsNullOrEmpty(resolvedPath))
                return InstalledMatchStatus.Unverified;

            try
            {
                if (!GitUtility.TryReadValidPackageManifestMeta(
                        Path.Combine(resolvedPath, "package.json.meta"),
                        out string actualGuid,
                        out _))
                {
                    return InstalledMatchStatus.Unverified;
                }

                return string.Equals(
                    actualGuid,
                    step.PackageManifestMetaGuid,
                    StringComparison.OrdinalIgnoreCase)
                    ? InstalledMatchStatus.Expected
                    : InstalledMatchStatus.Unexpected;
            }
            catch
            {
                return InstalledMatchStatus.Unverified;
            }
        }

        private static string ResolveInstalledDependencyFingerprint(
            PackageDependencyInstalledPackage installed)
        {
            if (GitUtility.IsValidPackageDependencyFingerprint(
                    installed?.DependencyFingerprint))
            {
                return installed.DependencyFingerprint;
            }

            string resolvedPath = installed?.ResolvedPath?.Trim() ?? string.Empty;
            if (string.IsNullOrEmpty(resolvedPath))
                return string.Empty;

            try
            {
                if (!GitUtility.TryReadPackageManifestMetadata(
                        Path.Combine(resolvedPath, "package.json"),
                        out PackageManifestMetadata metadata,
                        out _) ||
                    !string.IsNullOrEmpty(
                        GitUtility.ValidateExpectedPackageIdentity(
                            installed.Name,
                            installed.Version,
                            metadata.PackageName,
                            metadata.Version)))
                {
                    return string.Empty;
                }

                return GitUtility.ComputePackageDependencyFingerprint(
                    metadata.Dependencies);
            }
            catch
            {
                // The caller classifies an unreadable or invalid manifest as
                // unverified and never accepts the registered package.
                return string.Empty;
            }
        }

        private static PersistedInstallStep ToPersistedStep(
            PackageDependencyInstallStep step)
        {
            return new PersistedInstallStep
            {
                PackageName = step.PackageName,
                Version = step.Version,
                RepositoryUrl = step.RepositoryUrl,
                Revision = step.Revision,
                IsRoot = step.IsRoot,
                DependencyFingerprint = step.DependencyFingerprint,
                PackageManifestMetaVerification =
                    step.PackageManifestMetaVerification,
                PackageManifestMetaGuid = step.PackageManifestMetaGuid,
                InspectedCommit = step.InspectedCommit
            };
        }

        private static PackageDependencyInstallStep FromPersistedStep(
            PersistedInstallStep step)
        {
            return new PackageDependencyInstallStep(
                step.PackageName,
                step.Version,
                step.RepositoryUrl,
                step.Revision,
                step.IsRoot,
                step.DependencyFingerprint,
                step.PackageManifestMetaVerification,
                step.PackageManifestMetaGuid,
                step.InspectedCommit);
        }

        private static bool IsValidMode(PackageManagerGitInstallMode mode)
        {
            return mode == PackageManagerGitInstallMode.GitSubmodule ||
                   mode == PackageManagerGitInstallMode.ReadOnlyPackage;
        }

        private static bool IsValidPackageManifestMetaEvidence(
            PackageManifestMetaVerification verification,
            string guid)
        {
            if (verification == PackageManifestMetaVerification.Unverified)
                return string.IsNullOrWhiteSpace(guid);

            return verification == PackageManifestMetaVerification.Verified &&
                   GitSubmoduleInstallProbeSnapshot.IsValidMetaGuid(guid);
        }

        private static bool IsRecoveryBlockedState(PersistedInstallState state)
        {
            return string.Equals(
                state?.Phase,
                PhaseRecoveryBlocked,
                StringComparison.Ordinal);
        }

        private static string Sanitize(string value)
        {
            return PackageDependencyResolutionService.SanitizeDiagnostic(value);
        }

        private void TryClearActive()
        {
            try
            {
                store.ClearActive();
            }
            catch
            {
                // A retained state is safer than issuing the mutation again.
            }
        }

        private void TryClearCompletion()
        {
            try
            {
                store.ClearCompletion();
            }
            catch
            {
                // The next consume attempt can retry.
            }
        }

        private void TryClearRecoveryNotification()
        {
            try
            {
                store.ClearRecoveryNotification();
            }
            catch
            {
                // A stale marker suppresses duplicate presentation and does not
                // authorize another mutation.
            }
        }

        private static void Invoke(
            Action<PackageDependencyInstallCompletion> handler,
            PackageDependencyInstallCompletion completion)
        {
            if (handler == null)
                return;
            try
            {
                handler(completion);
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
            }
        }

        [Serializable]
        private sealed class PersistedInstallState
        {
            public int SchemaVersion;
            public string OperationId = string.Empty;
            public string RootPackageName = string.Empty;
            public string RootRepositoryUrl = string.Empty;
            public string RootRevision = string.Empty;
            public string RootInspectedCommit = string.Empty;
            public PackageManagerGitInstallMode InstallMode;
            public bool AllowsUnverifiedRootPackageManifestMeta;
            public int StepIndex;
            public string Phase = string.Empty;
            public long StartedUtcTicks;
            public long LastProgressUtcTicks;
            public PersistedInstallStep[] Steps =
                Array.Empty<PersistedInstallStep>();
            public string RecoveryMessage = string.Empty;
            [NonSerialized]
            public bool RecoveryNotificationPublished;
        }

        [Serializable]
        private sealed class PersistedInstallStep
        {
            public string PackageName = string.Empty;
            public string Version = string.Empty;
            public string RepositoryUrl = string.Empty;
            public string Revision = string.Empty;
            public bool IsRoot;
            public string DependencyFingerprint = string.Empty;
            public PackageManifestMetaVerification
                PackageManifestMetaVerification;
            public string PackageManifestMetaGuid = string.Empty;
            public string InspectedCommit = string.Empty;
        }

        [Serializable]
        private sealed class PersistedCompletion
        {
            public int SchemaVersion;
            public bool IsRecovery;
            public bool Success;
            public string Message = string.Empty;
            public string RootPackageName = string.Empty;
            public string RootRepositoryUrl = string.Empty;
            public string RootRevision = string.Empty;
            public PackageManagerGitInstallMode InstallMode;
        }
    }

    /// <summary>
    /// Session-persisted facade consumed by both Package Manager entry points.
    /// Its static update hook resumes the current step after script reloads and
    /// publishes one retained terminal completion.
    /// </summary>
    [InitializeOnLoad]
    internal static class PackageDependencyInstallCoordinator
    {
        private static readonly PackageDependencyInstallCoordinatorCore Core;
        private static bool updateSubscribed;
        private static bool readOnlyCompletionSubscribed;

        internal static event Action<PackageDependencyInstallCompletion> Completed;

        static PackageDependencyInstallCoordinator()
        {
            Core = new PackageDependencyInstallCoordinatorCore(
                UnityPackageDependencyInstallExecutor.Instance,
                SessionPackageDependencyInstallStateStore.Instance,
                null,
                OnCoreCompleted);
            SubscribeUpdateIfBusy();
            UpdateReadOnlyCompletionSubscription();
            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;
        }

        internal static bool IsBusy => Core.IsBusy;
        internal static bool IsRecoveryBlocked => Core.IsRecoveryBlocked;
        internal static bool NeedsUpdate => Core.NeedsUpdate;
        internal static string ActiveRecoveryMessage =>
            Core.ActiveRecoveryMessage;
        internal static string ActiveOperationId => Core.ActiveOperationId;
        internal static string ActiveRootPackageName =>
            Core.ActiveRootPackageName;
        internal static string ActiveRepositoryUrl => Core.ActiveRepositoryUrl;
        internal static string ActiveRevision => Core.ActiveRevision;
        internal static PackageManagerGitInstallMode? ActiveInstallMode =>
            Core.ActiveInstallMode;
        internal static int ActiveStepIndex => Core.ActiveStepIndex;
        internal static int ActiveStepCount => Core.ActiveStepCount;
        internal static PackageDependencyInstallStep ActiveStep =>
            Core.ActiveStep;
        internal static string ActiveStepPackageName =>
            Core.ActiveStepPackageName;

        internal static bool TryStart(
            PackageDependencyInstallRequest request,
            PackageDependencyResolutionPlan plan,
            Action<PackageDependencyInstallCompletion> onComplete,
            out string error)
        {
            if (!Core.TryStart(request, plan, onComplete, out error))
                return false;
            SubscribeUpdateIfBusy();
            UpdateReadOnlyCompletionSubscription();
            return true;
        }

        internal static bool TryConsumeLastCompletion(
            out PackageDependencyInstallCompletion completion)
        {
            return Core.TryConsumeLastCompletion(out completion);
        }

        internal static bool TryGetLastCompletion(
            out PackageDependencyInstallCompletion completion)
        {
            return Core.TryGetLastCompletion(out completion);
        }

        private static void Update()
        {
            ProcessRetainedReadOnlyCompletion();
            if (Core.NeedsUpdate)
                Core.Tick();
            if (!Core.NeedsUpdate)
                UnsubscribeUpdate();
            UpdateReadOnlyCompletionSubscription();
        }

        private static void SubscribeUpdateIfBusy()
        {
            if (!Core.NeedsUpdate || updateSubscribed)
                return;

            updateSubscribed = true;
            EditorApplication.update += Update;
        }

        private static void UnsubscribeUpdate()
        {
            if (!updateSubscribed)
                return;

            updateSubscribed = false;
            EditorApplication.update -= Update;
        }

        private static void UpdateReadOnlyCompletionSubscription()
        {
            bool shouldSubscribe = Core.IsBusy &&
                Core.ActiveInstallMode ==
                    PackageManagerGitInstallMode.ReadOnlyPackage;
            if (shouldSubscribe == readOnlyCompletionSubscribed)
                return;

            readOnlyCompletionSubscribed = shouldSubscribe;
            if (shouldSubscribe)
            {
                PackageManagerReadOnlyGitInstallService.Completed +=
                    OnReadOnlyInstallCompleted;
            }
            else
            {
                PackageManagerReadOnlyGitInstallService.Completed -=
                    OnReadOnlyInstallCompleted;
            }
        }

        private static void OnReadOnlyInstallCompleted(
            ReadOnlyGitPackageInstallCompletion completion)
        {
            HandleReadOnlyCompletion(completion);
        }

        private static void ProcessRetainedReadOnlyCompletion()
        {
            if (!Core.IsBusy ||
                Core.ActiveInstallMode !=
                    PackageManagerGitInstallMode.ReadOnlyPackage ||
                !PackageManagerReadOnlyGitInstallService.TryGetLastCompletion(
                    out ReadOnlyGitPackageInstallCompletion completion))
            {
                return;
            }

            HandleReadOnlyCompletion(completion);
        }

        private static void HandleReadOnlyCompletion(
            ReadOnlyGitPackageInstallCompletion completion)
        {
            ReadOnlyInstallCompletionCorrelation correlation =
                ClassifyReadOnlyCompletion(
                    Core.IsBusy,
                    Core.ActiveInstallMode,
                    Core.ActiveOperationId,
                    Core.ActiveStepPackageName,
                    completion);
            if (correlation == ReadOnlyInstallCompletionCorrelation.None)
                return;
            if (correlation == ReadOnlyInstallCompletionCorrelation.Exact)
            {
                PublishReadOnlyCompletion(completion);
                return;
            }

            string reportedPackage = string.IsNullOrWhiteSpace(
                completion?.PackageName)
                ? "a missing package name"
                : "a different package name";
            string reportedOutcome = completion?.Success == true
                ? "success"
                : "failure";
            string recoveryMessage =
                "The read-only package primitive reported " + reportedOutcome +
                " for the active operation but with " + reportedPackage +
                ". No dependency step was issued again and the operation is " +
                "blocked for recovery. Inspect Packages/manifest.json and " +
                "Unity's registered package state, repair only state whose " +
                "ownership you can prove, then restart the Unity Editor.";
            if (!Core.TryBlockForPrimitiveCorrelationFailure(
                    recoveryMessage,
                    out string blockError))
            {
                Debug.LogWarning(
                    "[Git Submodule Manager] " + blockError);
                return;
            }

            PackageManagerReadOnlyGitInstallService
                .TryConsumeLastCompletion(out _);
            UpdateReadOnlyCompletionSubscription();
        }

        internal static ReadOnlyInstallCompletionCorrelation
            ClassifyReadOnlyCompletion(
                bool coordinatorBusy,
                PackageManagerGitInstallMode? installMode,
                string activeOperationId,
                string activePackageName,
                ReadOnlyGitPackageInstallCompletion completion)
        {
            if (!coordinatorBusy ||
                installMode != PackageManagerGitInstallMode.ReadOnlyPackage ||
                completion == null ||
                !Guid.TryParseExact(activeOperationId, "N", out _) ||
                !string.Equals(
                    activeOperationId,
                    completion.DependencyInstallOperationId,
                    StringComparison.Ordinal))
            {
                return ReadOnlyInstallCompletionCorrelation.None;
            }

            return string.Equals(
                activePackageName,
                completion.PackageName,
                StringComparison.Ordinal)
                ? ReadOnlyInstallCompletionCorrelation.Exact
                : ReadOnlyInstallCompletionCorrelation.OperationIdentityOnly;
        }

        private static void PublishReadOnlyCompletion(
            ReadOnlyGitPackageInstallCompletion completion)
        {
            try
            {
                Core.NotifyPrimitiveCompletion(
                    new PackageDependencyPrimitiveCompletion(
                        completion.Success,
                        completion.PackageName,
                        completion.Message));
            }
            finally
            {
                // After a reload, the primitive's direct callback no longer
                // exists. Consume its step-scoped terminal record here instead.
                PackageManagerReadOnlyGitInstallService
                    .TryConsumeLastCompletion(out _);
            }
        }

        private static void OnCoreCompleted(
            PackageDependencyInstallCompletion completion)
        {
            UnsubscribeUpdate();
            UpdateReadOnlyCompletionSubscription();
            Invoke(Completed, completion);
        }

        private static void Invoke(
            Action<PackageDependencyInstallCompletion> handler,
            PackageDependencyInstallCompletion completion)
        {
            Delegate[] subscribers = handler?.GetInvocationList();
            if (subscribers == null)
                return;
            foreach (Delegate subscriber in subscribers)
            {
                try
                {
                    ((Action<PackageDependencyInstallCompletion>)subscriber)
                        .Invoke(completion);
                }
                catch (Exception exception)
                {
                    Debug.LogException(exception);
                }
            }
        }

        private static void OnBeforeAssemblyReload()
        {
            UnsubscribeUpdate();
            UnityPackageDependencyInstallExecutor.Instance
                .StopSubmoduleCommitVerificationForReload();
            if (readOnlyCompletionSubscribed)
            {
                readOnlyCompletionSubscribed = false;
                PackageManagerReadOnlyGitInstallService.Completed -=
                    OnReadOnlyInstallCompleted;
            }
            AssemblyReloadEvents.beforeAssemblyReload -= OnBeforeAssemblyReload;
        }
    }
}
