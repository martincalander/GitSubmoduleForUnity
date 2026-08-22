using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;

namespace MartinCalander.GitSubmoduleManager.Editor
{
    internal enum GitSubmoduleInstallProbeStatus
    {
        Idle,
        LoadingRemoteRefs,
        ReadingPackageManifest,
        Ready,
        Failed
    }

    /// <summary>
    /// Immutable, host-neutral result of inspecting a prospective Git package.
    /// A manifest failure is deliberately separate from a remote-ref failure:
    /// callers can still offer manual package-name entry when Git could list the
    /// repository but could not read a valid root package.json.
    /// </summary>
    internal sealed class GitSubmoduleInstallProbeSnapshot
    {
        private readonly ReadOnlyCollection<string> branches;

        internal GitSubmoduleInstallProbeSnapshot(
            int revision,
            string url,
            GitSubmoduleInstallProbeStatus status,
            IEnumerable<string> branches = null,
            string defaultBranch = "",
            string packageName = "",
            string displayName = "",
            string version = "",
            string errorMessage = "",
            string manifestMessage = "",
            bool requiresEditorRestart = false)
        {
            Revision = revision;
            Url = url ?? string.Empty;
            Status = status;
            this.branches = new ReadOnlyCollection<string>(
                new List<string>(branches ?? Enumerable.Empty<string>()));
            DefaultBranch = defaultBranch ?? string.Empty;
            PackageName = packageName ?? string.Empty;
            DisplayName = displayName ?? string.Empty;
            Version = version ?? string.Empty;
            ErrorMessage = errorMessage ?? string.Empty;
            ManifestMessage = manifestMessage ?? string.Empty;
            RequiresEditorRestart = requiresEditorRestart;
        }

        internal int Revision { get; }
        internal string Url { get; }
        internal GitSubmoduleInstallProbeStatus Status { get; }
        internal IReadOnlyList<string> Branches => branches;
        internal string DefaultBranch { get; }
        internal string PackageName { get; }
        internal string DisplayName { get; }
        internal string Version { get; }
        internal string ErrorMessage { get; }
        internal string ManifestMessage { get; }
        internal bool RequiresEditorRestart { get; }

        internal bool IsLoading =>
            Status == GitSubmoduleInstallProbeStatus.LoadingRemoteRefs ||
            Status == GitSubmoduleInstallProbeStatus.ReadingPackageManifest;

        internal bool IsComplete =>
            Status == GitSubmoduleInstallProbeStatus.Ready ||
            Status == GitSubmoduleInstallProbeStatus.Failed;
    }

    /// <summary>
    /// Performs a read-only, Git-only metadata probe for the Package Manager
    /// install form. The caller owns polling through <see cref="Tick"/> so no UI
    /// framework or synchronization context is required.
    /// </summary>
    internal sealed class GitSubmoduleInstallProbe : IDisposable
    {
        internal const int RemoteRefsTimeoutMs = 15000;
        internal const int PartialCloneTimeoutMs = 30000;
        internal const int ManifestReadTimeoutMs = 10000;
        internal const int MaximumBranchCount = 2048;

        private const int DiagnosticLimit = 640;
        private const int DeferredCleanupWaitMs = 60000;
        private const string HeadsPrefix = "refs/heads/";
        private const uint UnixOwnerDirectoryMode = 448; // 0700
        private const uint UnixPermissionBitsMask = 511; // 0777

        private static readonly object ReaderGate = new();
        private static int activeReaderCount;

        [DllImport("libc", EntryPoint = "chmod", SetLastError = true)]
        private static extern int Chmod(string path, uint mode);

        private enum ProbePhase
        {
            None,
            RemoteRefs,
            PartialClone,
            ReadManifest
        }

        private AsyncCommandHandle commandHandle;
        private ProbePhase phase;
        private string activeUrl = string.Empty;
        private string queuedUrl = string.Empty;
        private string temporaryClonePath = string.Empty;
        private List<string> activeBranches = new();
        private string activeDefaultBranch = string.Empty;
        private bool discardActiveResult;
        private bool disposed;
        private int revision;
        private ReaderLease readerLease;

        /// <summary>
        /// True while any install probe owns a live read operation, including a
        /// naturally draining command from a popup that has already closed.
        /// Repository mutation must remain blocked until this becomes false.
        /// </summary>
        internal static bool IsReaderActive
        {
            get
            {
                lock (ReaderGate)
                    return activeReaderCount > 0;
            }
        }

        internal GitSubmoduleInstallProbeSnapshot Current { get; private set; } =
            new GitSubmoduleInstallProbeSnapshot(
                0,
                string.Empty,
                GitSubmoduleInstallProbeStatus.Idle);

        /// <summary>
        /// Requests metadata for a validated repository URL. A newer request
        /// supersedes any older request without starting overlapping Git
        /// processes. The older bounded command is allowed to finish normally,
        /// then its result is discarded and only the newest request is started.
        /// </summary>
        internal bool Request(string url)
        {
            if (disposed || !GitUtility.IsValidRepositoryUrl(url))
                return false;

            string normalizedUrl = url.Trim();
            if (commandHandle != null)
            {
                if (!discardActiveResult &&
                    string.Equals(activeUrl, normalizedUrl, StringComparison.Ordinal))
                {
                    return true;
                }

                queuedUrl = normalizedUrl;
                discardActiveResult = true;
                Publish(
                    normalizedUrl,
                    GitSubmoduleInstallProbeStatus.LoadingRemoteRefs);
                return true;
            }

            if (string.Equals(Current.Url, normalizedUrl, StringComparison.Ordinal) &&
                (Current.IsLoading || Current.IsComplete))
            {
                return true;
            }

            queuedUrl = normalizedUrl;
            Publish(
                normalizedUrl,
                GitSubmoduleInstallProbeStatus.LoadingRemoteRefs);
            TryStartQueuedRequest();
            return true;
        }

        /// <summary>
        /// Advances completed command phases. Returns true when the immutable
        /// snapshot revision changed during this call.
        /// </summary>
        internal bool Tick()
        {
            int startingRevision = Current.Revision;
            if (disposed)
                return false;

            if (commandHandle == null)
            {
                TryStartQueuedRequest();
                return Current.Revision != startingRevision;
            }

            if (!commandHandle.IsComplete)
                return false;

            AsyncCommandHandle completedHandle = commandHandle;
            CommandResult result = completedHandle.Result;
            ProbePhase completedPhase = phase;
            bool discard = discardActiveResult;
            commandHandle = null;
            phase = ProbePhase.None;

            bool terminationConfirmed = HasConfirmedTermination(result);
            if (!terminationConfirmed)
            {
                // Preserve the existing global drain barrier used by repository
                // discovery. Starting more Git work is unsafe when a process tree
                // may still be alive.
                AsyncCommandDrainRegistry.Retire(completedHandle);
            }

            if (discard)
            {
                bool replacementWasQueued = !string.IsNullOrWhiteSpace(queuedUrl);
                string replacementUrl = queuedUrl;
                ResetActiveState(cleanTemporaryClone: terminationConfirmed);
                if (!terminationConfirmed && replacementWasQueued)
                {
                    queuedUrl = string.Empty;
                    ReleaseReaderLease();
                    Publish(
                        replacementUrl,
                        GitSubmoduleInstallProbeStatus.Failed,
                        errorMessage:
                            "A previous Git process did not stop cleanly. " +
                            "Restart the Editor before probing this repository.",
                        requiresEditorRestart: true);
                    return true;
                }

                TryStartQueuedRequest();
                if (!replacementWasQueued)
                    ReleaseReaderLease();
                return Current.Revision != startingRevision;
            }

            if (!terminationConfirmed)
            {
                string failedUrl = activeUrl;
                ResetActiveState(cleanTemporaryClone: false);
                ReleaseReaderLease();
                Publish(
                    failedUrl,
                    GitSubmoduleInstallProbeStatus.Failed,
                    errorMessage:
                        "Git process termination could not be confirmed. " +
                        "Restart the Editor before probing another repository.",
                    requiresEditorRestart: true);
                return true;
            }

            switch (completedPhase)
            {
                case ProbePhase.RemoteRefs:
                    CompleteRemoteRefs(result);
                    break;
                case ProbePhase.PartialClone:
                    CompletePartialClone(result);
                    break;
                case ProbePhase.ReadManifest:
                    CompleteManifestRead(result);
                    break;
            }

            return Current.Revision != startingRevision;
        }

        /// <summary>
        /// Discards the current request. An already-running command is not killed
        /// merely because a text field changed; it finishes its bounded phase and
        /// is ignored, avoiding unsafe overlap with a replacement Git process.
        /// </summary>
        internal void Cancel()
        {
            if (disposed)
                return;

            queuedUrl = string.Empty;
            if (commandHandle != null)
                discardActiveResult = true;
            else
                ResetActiveState(cleanTemporaryClone: true);

            Publish(string.Empty, GitSubmoduleInstallProbeStatus.Idle);
        }

        public void Dispose()
        {
            if (disposed)
                return;

            disposed = true;
            queuedUrl = string.Empty;
            AsyncCommandHandle retiringHandle = commandHandle;
            string retiringTemporaryClone = temporaryClonePath;
            ReaderLease retiringReaderLease = readerLease;
            commandHandle = null;
            phase = ProbePhase.None;
            activeUrl = string.Empty;
            activeBranches.Clear();
            activeDefaultBranch = string.Empty;
            temporaryClonePath = string.Empty;
            discardActiveResult = false;
            readerLease = null;

            if (retiringHandle == null)
            {
                QueueTemporaryCloneCleanup(retiringTemporaryClone);
                retiringReaderLease?.Release();
                return;
            }

            // Do not force-cancel a CompleteProcessTree Git command merely
            // because its popup closed. The runner correctly treats forced
            // repository-command termination as unconfirmed, which would turn a
            // routine dismissal into an Editor-restart requirement. Keep the
            // global reader lease until this already-bounded phase exits.
            QueueRetiredReaderCompletion(
                retiringHandle,
                retiringTemporaryClone,
                retiringReaderLease);
        }

        internal static bool TryParseRemoteRefs(
            string output,
            out List<string> branches,
            out string defaultBranch,
            out string error)
        {
            branches = new List<string>();
            defaultBranch = string.Empty;
            error = string.Empty;
            if (string.IsNullOrWhiteSpace(output))
            {
                error = "No remote branches were found.";
                return false;
            }

            var branchLines = new List<string>();
            var branchesByObjectId = new Dictionary<string, List<string>>(
                StringComparer.Ordinal);
            string headObjectId = string.Empty;
            string[] lines = output.Split(
                new[] { '\r', '\n' },
                StringSplitOptions.RemoveEmptyEntries);
            foreach (string rawLine in lines)
            {
                string line = rawLine.Trim();
                if (line.StartsWith("ref: ", StringComparison.Ordinal))
                {
                    int headSeparator = line.IndexOfAny(new[] { '\t', ' ' }, 5);
                    string symbolicRef = headSeparator < 0
                        ? line.Substring(5)
                        : line.Substring(5, headSeparator - 5);
                    if (symbolicRef.StartsWith(HeadsPrefix, StringComparison.Ordinal))
                    {
                        string candidate = symbolicRef.Substring(HeadsPrefix.Length);
                        if (GitUtility.IsValidBranchName(candidate) &&
                            !string.Equals(candidate, ".", StringComparison.Ordinal))
                        {
                            defaultBranch = candidate;
                        }
                    }

                    continue;
                }

                int tab = line.IndexOf('\t');
                if (tab < 0)
                    continue;

                string objectId = line.Substring(0, tab).Trim();
                string refName = line.Substring(tab + 1).Trim();
                if (string.Equals(refName, "HEAD", StringComparison.Ordinal))
                {
                    headObjectId = objectId;
                    continue;
                }

                if (refName.StartsWith(HeadsPrefix, StringComparison.Ordinal))
                {
                    branchLines.Add(line);
                    string candidate = refName.Substring(HeadsPrefix.Length);
                    if (!string.IsNullOrWhiteSpace(objectId) &&
                        GitUtility.IsValidBranchName(candidate) &&
                        !string.Equals(candidate, ".", StringComparison.Ordinal))
                    {
                        if (!branchesByObjectId.TryGetValue(
                                objectId,
                                out List<string> matchingBranches))
                        {
                            matchingBranches = new List<string>();
                            branchesByObjectId[objectId] = matchingBranches;
                        }

                        matchingBranches.Add(candidate);
                    }
                }
            }

            if (string.IsNullOrEmpty(defaultBranch) &&
                !string.IsNullOrWhiteSpace(headObjectId) &&
                branchesByObjectId.TryGetValue(
                    headObjectId,
                    out List<string> inferredBranches))
            {
                string[] distinctMatches = inferredBranches
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
                if (distinctMatches.Length == 1)
                    defaultBranch = distinctMatches[0];
            }

            // Reuse the package's established ls-remote parser, then apply the
            // stricter install-input branch validation and an explicit count cap.
            List<string> parsed = GitUtility.ParseRemoteBranches(
                string.Join("\n", branchLines));
            var unique = new HashSet<string>(StringComparer.Ordinal);
            foreach (string branch in parsed)
            {
                if (!GitUtility.IsValidBranchName(branch) ||
                    string.Equals(branch, ".", StringComparison.Ordinal) ||
                    !unique.Add(branch))
                {
                    continue;
                }

                if (unique.Count > MaximumBranchCount)
                {
                    branches.Clear();
                    defaultBranch = string.Empty;
                    error =
                        $"The repository exposes more than {MaximumBranchCount} branches; " +
                        "the partial branch list was discarded.";
                    return false;
                }
            }

            if (!string.IsNullOrEmpty(defaultBranch))
                unique.Add(defaultBranch);

            if (unique.Count > MaximumBranchCount)
            {
                defaultBranch = string.Empty;
                error =
                    $"The repository exposes more than {MaximumBranchCount} branches; " +
                    "the partial branch list was discarded.";
                return false;
            }

            branches = unique
                .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                .ThenBy(value => value, StringComparer.Ordinal)
                .ToList();
            if (branches.Count == 0)
            {
                error = "No remote branches were found.";
                return false;
            }

            return true;
        }

        internal static bool HasConfirmedTermination(CommandResult result)
        {
            return result?.TerminationConfirmed == true;
        }

        private void CompleteRemoteRefs(CommandResult result)
        {
            if (!TryRequireSuccessfulOutput(
                    result,
                    "Could not read remote branches",
                    out string commandError))
            {
                FinishFailure(commandError);
                return;
            }

            if (result.StdOutTruncated)
            {
                FinishFailure(
                    "Git returned more branch data than could be inspected safely. " +
                    "The partial branch list was discarded.");
                return;
            }

            if (!TryParseRemoteRefs(
                    result.StdOut,
                    out activeBranches,
                    out activeDefaultBranch,
                    out string parseError))
            {
                FinishFailure(parseError);
                return;
            }

            Publish(
                activeUrl,
                GitSubmoduleInstallProbeStatus.ReadingPackageManifest,
                activeBranches,
                activeDefaultBranch);

            try
            {
                temporaryClonePath = CreateTemporaryClonePath();
                var arguments = new List<string>
                {
                    "-c", "credential.interactive=false",
                    "-c", "protocol.version=2",
                    "clone",
                    "--quiet",
                    "--no-checkout",
                    "--no-tags",
                    "--depth=1",
                    "--single-branch",
                    "--filter=blob:none",
                    "--no-local"
                };
                if (!string.IsNullOrEmpty(activeDefaultBranch))
                {
                    arguments.Add("--branch");
                    arguments.Add(activeDefaultBranch);
                }

                arguments.Add("--");
                arguments.Add(activeUrl);
                arguments.Add(temporaryClonePath);
                StartCommand(arguments, PartialCloneTimeoutMs, ProbePhase.PartialClone);
            }
            catch (Exception exception)
            {
                FinishReadyWithManifestMessage(
                    "Could not create a temporary repository for package.json inspection: " +
                    SanitizeDiagnostic(exception.Message));
            }
        }

        private void CompletePartialClone(CommandResult result)
        {
            if (!TryRequireSuccessfulOutput(
                    result,
                    "Could not inspect root package.json",
                    out string cloneError))
            {
                FinishReadyWithManifestMessage(cloneError);
                return;
            }

            if (!IsSafeOwnedTemporaryCloneDirectory(temporaryClonePath))
            {
                FinishReadyWithManifestMessage(
                    "The temporary repository changed while it was being inspected. " +
                    "Package metadata was discarded.");
                return;
            }

            try
            {
                StartCommand(
                    new[]
                    {
                        "-C", temporaryClonePath,
                        "-c", "credential.interactive=false",
                        "--no-pager",
                        "cat-file", "blob", "HEAD:package.json"
                    },
                    ManifestReadTimeoutMs,
                    ProbePhase.ReadManifest);
            }
            catch (Exception exception)
            {
                FinishReadyWithManifestMessage(
                    "Could not read root package.json: " +
                    SanitizeDiagnostic(exception.Message));
            }
        }

        private void CompleteManifestRead(CommandResult result)
        {
            string packageName = string.Empty;
            string displayName = string.Empty;
            string version = string.Empty;
            string manifestMessage = string.Empty;
            if (!TryRequireSuccessfulOutput(
                    result,
                    "Could not read root package.json",
                    out manifestMessage))
            {
                // The remote branch list remains useful when the repository has
                // no root package manifest or its server cannot serve the blob.
            }
            else if (result.StdOutTruncated)
            {
                manifestMessage =
                    "Root package.json exceeds the command output inspection limit.";
            }
            else if (!GitUtility.TryReadValidPackageManifestFromJson(
                         result.StdOut,
                         out packageName,
                         out displayName,
                         out version,
                         out string validationError))
            {
                manifestMessage = "Root package.json is not a valid UPM manifest: " + validationError;
            }

            string completedUrl = activeUrl;
            List<string> completedBranches = activeBranches;
            string completedDefaultBranch = activeDefaultBranch;
            ResetActiveState(cleanTemporaryClone: true);
            ReleaseReaderLease();
            Publish(
                completedUrl,
                GitSubmoduleInstallProbeStatus.Ready,
                completedBranches,
                completedDefaultBranch,
                packageName,
                displayName,
                version,
                manifestMessage: manifestMessage);
        }

        private void FinishFailure(string error)
        {
            string completedUrl = activeUrl;
            ResetActiveState(cleanTemporaryClone: true);
            ReleaseReaderLease();
            Publish(
                completedUrl,
                GitSubmoduleInstallProbeStatus.Failed,
                errorMessage: SanitizeDiagnostic(error));
        }

        private void FinishReadyWithManifestMessage(string message)
        {
            string completedUrl = activeUrl;
            List<string> completedBranches = activeBranches;
            string completedDefaultBranch = activeDefaultBranch;
            ResetActiveState(cleanTemporaryClone: true);
            ReleaseReaderLease();
            Publish(
                completedUrl,
                GitSubmoduleInstallProbeStatus.Ready,
                completedBranches,
                completedDefaultBranch,
                manifestMessage: SanitizeDiagnostic(message));
        }

        private bool TryStartQueuedRequest()
        {
            if (disposed ||
                commandHandle != null ||
                string.IsNullOrWhiteSpace(queuedUrl))
            {
                return false;
            }

            if (HasOtherReaderActive())
                return false;

            if (AsyncCommandDrainRegistry.RequiresEditorRestart)
            {
                string failedUrl = queuedUrl;
                queuedUrl = string.Empty;
                Publish(
                    failedUrl,
                    GitSubmoduleInstallProbeStatus.Failed,
                    errorMessage:
                        string.IsNullOrWhiteSpace(AsyncCommandDrainRegistry.StatusMessage)
                            ? "A previous process did not stop safely. Restart the Editor before inspecting another repository."
                            : AsyncCommandDrainRegistry.StatusMessage,
                    requiresEditorRestart: true);
                return false;
            }

            if (AsyncCommandDrainRegistry.IsDraining)
                return false;

            activeUrl = queuedUrl;
            queuedUrl = string.Empty;
            activeBranches = new List<string>();
            activeDefaultBranch = string.Empty;
            temporaryClonePath = string.Empty;
            discardActiveResult = false;
            Publish(
                activeUrl,
                GitSubmoduleInstallProbeStatus.LoadingRemoteRefs);
            AcquireReaderLease();

            try
            {
                StartCommand(
                    new[]
                    {
                        "-c", "credential.interactive=false",
                        "ls-remote", "--symref",
                        "--",
                        activeUrl,
                        "HEAD", "refs/heads/*"
                    },
                    RemoteRefsTimeoutMs,
                    ProbePhase.RemoteRefs);
                return true;
            }
            catch (Exception exception)
            {
                FinishFailure(
                    "Could not start Git remote inspection: " + exception.Message);
                return false;
            }
        }

        private void StartCommand(
            IReadOnlyList<string> arguments,
            int timeoutMs,
            ProbePhase nextPhase)
        {
            commandHandle = CliCommandRunner.RunAsync(
                GitUtility.GitExecutable,
                arguments,
                GitUtility.ProjectRoot,
                timeoutMs,
                CommandTerminationScope.CompleteProcessTree);
            phase = nextPhase;
        }

        private void ResetActiveState(bool cleanTemporaryClone)
        {
            string clonePath = temporaryClonePath;
            activeUrl = string.Empty;
            activeBranches = new List<string>();
            activeDefaultBranch = string.Empty;
            temporaryClonePath = string.Empty;
            discardActiveResult = false;
            phase = ProbePhase.None;
            if (cleanTemporaryClone)
                QueueTemporaryCloneCleanup(clonePath);
        }

        private void Publish(
            string url,
            GitSubmoduleInstallProbeStatus status,
            IEnumerable<string> branches = null,
            string defaultBranch = "",
            string packageName = "",
            string displayName = "",
            string version = "",
            string errorMessage = "",
            string manifestMessage = "",
            bool requiresEditorRestart = false)
        {
            revision++;
            Current = new GitSubmoduleInstallProbeSnapshot(
                revision,
                url,
                status,
                branches,
                defaultBranch,
                packageName,
                displayName,
                version,
                errorMessage,
                manifestMessage,
                requiresEditorRestart);
        }

        private static bool TryRequireSuccessfulOutput(
            CommandResult result,
            string summary,
            out string error)
        {
            error = string.Empty;
            if (result != null && result.IsSuccess)
                return true;

            string detail = result == null
                ? "No result was returned."
                : string.IsNullOrWhiteSpace(result.StdErr)
                    ? result.StdOut
                    : result.StdErr;
            error = SanitizeDiagnostic(
                string.IsNullOrWhiteSpace(detail)
                    ? summary
                    : summary + ": " + detail);
            return false;
        }

        private static string SanitizeDiagnostic(string value)
        {
            string safe = GitUtility.RedactCredentials(value ?? string.Empty).Trim();
            if (safe.Length <= DiagnosticLimit)
                return safe;

            return safe.Substring(0, DiagnosticLimit - 16) + "... [truncated]";
        }

        internal static string CreateTemporaryClonePath()
        {
            string parent = GetTemporaryCloneParent();
            EnsurePrivateTemporaryCloneParent(parent);

            for (int attempt = 0; attempt < 8; attempt++)
            {
                string path = Path.Combine(parent, Guid.NewGuid().ToString("N"));
                if (Directory.Exists(path) || File.Exists(path))
                    continue;

                try
                {
                    Directory.CreateDirectory(path);
                    if (!IsSafeOwnedTemporaryCloneDirectory(path))
                        throw new IOException(
                            "The temporary repository directory is not a normal owned directory.");

                    RestrictDirectoryToCurrentUser(path);
                    if (!IsSafeOwnedTemporaryCloneDirectory(path))
                        throw new IOException(
                            "The temporary repository directory changed during creation.");

                    return path;
                }
                catch
                {
                    TryDeleteTemporaryClone(path);
                    throw;
                }
            }

            throw new IOException("Could not reserve a unique temporary repository directory.");
        }

        private static string GetTemporaryCloneParent()
        {
            return Path.GetFullPath(Path.Combine(
                GitUtility.ProjectRoot,
                "Library",
                "GitSubmoduleManager",
                "InstallProbe"));
        }

        private static void EnsurePrivateTemporaryCloneParent(string parent)
        {
            string library = Path.GetFullPath(Path.Combine(GitUtility.ProjectRoot, "Library"));
            string manager = Path.Combine(library, "GitSubmoduleManager");
            Directory.CreateDirectory(parent);

            foreach (string path in new[] { library, manager, parent })
            {
                if (!IsNormalDirectory(path))
                    throw new IOException(
                        "The project temporary repository path contains a symbolic link, " +
                        "junction, or other reparse point.");
            }

            RestrictDirectoryToCurrentUser(manager);
            RestrictDirectoryToCurrentUser(parent);
            if (!IsNormalDirectory(manager) || !IsNormalDirectory(parent))
                throw new IOException("The project temporary repository path changed during creation.");
        }

        private static void RestrictDirectoryToCurrentUser(string path)
        {
            if (Path.DirectorySeparatorChar == '\\')
                return;

            if (Chmod(path, UnixOwnerDirectoryMode) != 0)
            {
                throw new UnauthorizedAccessException(
                    "Could not restrict temporary repository directory permissions.");
            }

            if (!HasPrivateDirectoryPermissions(path))
            {
                throw new UnauthorizedAccessException(
                    "The temporary repository filesystem did not enforce owner-only permissions.");
            }
        }

        internal static bool HasPrivateDirectoryPermissions(string path)
        {
            if (Path.DirectorySeparatorChar == '\\')
                return true;

            try
            {
                Type syscallType = Type.GetType(
                    "Mono.Unix.Native.Syscall, Mono.Posix",
                    throwOnError: false);
                Type statType = Type.GetType(
                    "Mono.Unix.Native.Stat, Mono.Posix",
                    throwOnError: false);
                if (syscallType == null || statType == null)
                    return false;

                MethodInfo statMethod = syscallType.GetMethod(
                    "stat",
                    BindingFlags.Public | BindingFlags.Static,
                    binder: null,
                    types: new[] { typeof(string), statType.MakeByRefType() },
                    modifiers: null);
                FieldInfo modeField = statType.GetField(
                    "st_mode",
                    BindingFlags.Public | BindingFlags.Instance);
                if (statMethod == null || modeField == null)
                    return false;

                object[] arguments = { path, Activator.CreateInstance(statType) };
                if (!(statMethod.Invoke(null, arguments) is int result) || result != 0)
                    return false;

                uint mode = Convert.ToUInt32(modeField.GetValue(arguments[1]));
                return (mode & UnixPermissionBitsMask) == UnixOwnerDirectoryMode;
            }
            catch
            {
                return false;
            }
        }

        internal static void TryDeleteTemporaryClone(string path)
        {
            if (!IsSafeOwnedTemporaryCloneDirectory(path))
                return;

            try
            {
                if (Directory.Exists(path))
                    Directory.Delete(path, true);
            }
            catch
            {
                // A failed cleanup must not turn read-only metadata discovery
                // into a destructive retry. The project Library directory can
                // be reclaimed later.
            }
        }

        internal static bool IsOwnedTemporaryClonePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return false;

            try
            {
                string fullPath = Path.GetFullPath(path)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string parent = GetTemporaryCloneParent()
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                StringComparison comparison = Path.DirectorySeparatorChar == '\\'
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal;
                if (!string.Equals(
                        Path.GetDirectoryName(fullPath),
                        parent,
                        comparison))
                {
                    return false;
                }

                return Guid.TryParseExact(Path.GetFileName(fullPath), "N", out _);
            }
            catch
            {
                return false;
            }
        }

        internal static bool IsSafeOwnedTemporaryCloneDirectory(string path)
        {
            if (!IsOwnedTemporaryClonePath(path) || !IsNormalDirectory(path))
                return false;

            try
            {
                string library = Path.GetFullPath(Path.Combine(GitUtility.ProjectRoot, "Library"));
                string manager = Path.Combine(library, "GitSubmoduleManager");
                string parent = GetTemporaryCloneParent();
                return IsNormalDirectory(library) &&
                       IsNormalDirectory(manager) &&
                       IsNormalDirectory(parent);
            }
            catch
            {
                return false;
            }
        }

        private static bool IsNormalDirectory(string path)
        {
            try
            {
                FileAttributes attributes = File.GetAttributes(path);
                return (attributes & FileAttributes.Directory) != 0 &&
                       (attributes & FileAttributes.ReparsePoint) == 0;
            }
            catch
            {
                return false;
            }
        }

        private void AcquireReaderLease()
        {
            if (readerLease != null && !readerLease.IsReleased)
                return;

            readerLease = new ReaderLease();
        }

        private void ReleaseReaderLease()
        {
            ReaderLease lease = readerLease;
            readerLease = null;
            lease?.Release();
        }

        private bool HasOtherReaderActive()
        {
            lock (ReaderGate)
            {
                int ownLease = readerLease != null && !readerLease.IsReleased ? 1 : 0;
                return activeReaderCount > ownLease;
            }
        }

        private static void QueueTemporaryCloneCleanup(string path)
        {
            if (!IsOwnedTemporaryClonePath(path))
                return;

            ThreadPool.QueueUserWorkItem(_ => TryDeleteTemporaryClone(path));
        }

        private static void QueueRetiredReaderCompletion(
            AsyncCommandHandle handle,
            string path,
            ReaderLease lease)
        {
            if (handle == null)
            {
                QueueTemporaryCloneCleanup(path);
                lease?.Release();
                return;
            }

            ThreadPool.QueueUserWorkItem(_ =>
            {
                while (!handle.WaitForCompletion(DeferredCleanupWaitMs))
                {
                    // Real commands are bounded. Keep the reader lease if a
                    // custom runner outlives that bound rather than allowing a
                    // repository mutation to overlap an unknown reader.
                }

                if (handle.Result?.TerminationConfirmed == true)
                {
                    TryDeleteTemporaryClone(path);
                }
                else
                {
                    AsyncCommandDrainRegistry.Retire(handle);
                }

                lease?.Release();
            });
        }

        private sealed class ReaderLease
        {
            private int released;

            internal ReaderLease()
            {
                lock (ReaderGate)
                    activeReaderCount++;
            }

            internal bool IsReleased => Volatile.Read(ref released) != 0;

            internal void Release()
            {
                if (Interlocked.Exchange(ref released, 1) != 0)
                    return;

                lock (ReaderGate)
                    activeReaderCount = Math.Max(0, activeReaderCount - 1);
            }
        }
    }
}
