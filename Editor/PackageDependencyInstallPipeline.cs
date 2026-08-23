using System;
using UnityEditor;
using UnityEngine;

namespace MartinCalander.GitSubmoduleManager.Editor
{
    internal enum PackageDependencyInstallPipelineStage
    {
        Idle,
        InspectingPackage,
        ResolvingDependencies,
        Installing
    }

    internal sealed class PackageDependencyInstallPipelineSnapshot
    {
        internal PackageDependencyInstallPipelineSnapshot(
            int revision,
            PackageDependencyInstallPipelineStage stage,
            string repositoryUrl,
            string branch,
            string packageName,
            PackageManagerGitInstallMode installMode,
            string message)
        {
            Revision = revision;
            Stage = stage;
            RepositoryUrl = repositoryUrl?.Trim() ?? string.Empty;
            Branch = branch?.Trim() ?? string.Empty;
            PackageName = packageName?.Trim() ?? string.Empty;
            InstallMode = installMode;
            Message = PackageDependencyResolutionService.SanitizeDiagnostic(
                message);
        }

        internal int Revision { get; }
        internal PackageDependencyInstallPipelineStage Stage { get; }
        internal string RepositoryUrl { get; }
        internal string Branch { get; }
        internal string PackageName { get; }
        internal PackageManagerGitInstallMode InstallMode { get; }
        internal string Message { get; }
        internal bool IsBusy => Stage != PackageDependencyInstallPipelineStage.Idle;
    }

    internal sealed class PackageDependencyInstallPipelineCompletion
    {
        internal PackageDependencyInstallPipelineCompletion(
            bool success,
            bool cancelled,
            string message,
            string repositoryUrl,
            string branch,
            string packageName,
            PackageManagerGitInstallMode installMode,
            bool recoveredAfterReload = false)
        {
            Success = success;
            Cancelled = cancelled;
            Message = PackageDependencyResolutionService.SanitizeDiagnostic(
                message);
            RepositoryUrl = repositoryUrl?.Trim() ?? string.Empty;
            Branch = branch?.Trim() ?? string.Empty;
            PackageName = packageName?.Trim() ?? string.Empty;
            InstallMode = installMode;
            RecoveredAfterReload = recoveredAfterReload;
        }

        internal bool Success { get; }
        internal bool Cancelled { get; }
        internal string Message { get; }
        internal string RepositoryUrl { get; }
        internal string Branch { get; }
        internal string PackageName { get; }
        internal PackageManagerGitInstallMode InstallMode { get; }
        internal bool RecoveredAfterReload { get; }
    }

    internal sealed class PackageDependencyInstallCompletionDialogContent
    {
        internal PackageDependencyInstallCompletionDialogContent(
            string title,
            string message,
            string acceptText)
        {
            Title = title ?? string.Empty;
            Message = message ?? string.Empty;
            AcceptText = acceptText ?? string.Empty;
        }

        internal string Title { get; }
        internal string Message { get; }
        internal string AcceptText { get; }
    }

    internal interface IPackageDependencyInstallCompletionDialog
    {
        void Show(PackageDependencyInstallCompletionDialogContent content);
    }

    internal sealed class UnityPackageDependencyInstallCompletionDialog :
        IPackageDependencyInstallCompletionDialog
    {
        internal static UnityPackageDependencyInstallCompletionDialog Instance
            { get; } = new();

        private UnityPackageDependencyInstallCompletionDialog()
        {
        }

        public void Show(
            PackageDependencyInstallCompletionDialogContent content)
        {
            if (content == null)
                return;
            EditorUtility.DisplayDialog(
                content.Title,
                content.Message,
                content.AcceptText);
        }
    }

    /// <summary>
    /// Shared, single-flight front end for both native Package Manager install
    /// entry points. It performs an exact selected-branch manifest probe,
    /// dependency resolution and consent before handing the only mutation to
    /// the reload-safe coordinator.
    /// </summary>
    internal static class PackageDependencyInstallPipeline
    {
        private const double PreMutationTimeoutSeconds = 600d;

        private static PendingInstall pending;
        private static int revision;
        private static bool updateSubscribed;

        internal static event Action<PackageDependencyInstallPipelineSnapshot>
            Changed;
        internal static event Action<PackageDependencyInstallPipelineCompletion>
            Completed;

        internal static PackageDependencyInstallPipelineSnapshot Current
            { get; private set; } = new(
                0,
                PackageDependencyInstallPipelineStage.Idle,
                string.Empty,
                string.Empty,
                string.Empty,
                PackageManagerGitInstallMode.GitSubmodule,
                string.Empty);

        static PackageDependencyInstallPipeline()
        {
            PackageDependencyInstallCoordinator.Completed +=
                OnCoordinatorCompleted;
            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;
            if (PackageDependencyInstallCoordinator.IsBusy)
            {
                PublishCoordinatorState();
                SubscribeUpdate();
            }
        }

        internal static bool IsBusy =>
            pending != null ||
            PackageDependencyPreflightService.IsBusy ||
            PackageDependencyInstallCoordinator.IsBusy;

        internal static bool TryStart(
            string repositoryUrl,
            string branch,
            string expectedPackageName,
            PackageManagerGitInstallMode installMode,
            GitSubmoduleInstallProbeSnapshot exactProbeSnapshot,
            Action<PackageDependencyInstallPipelineCompletion> onComplete,
            out string error)
        {
            error = string.Empty;
            if (IsBusy)
            {
                error =
                    "Another dependency-aware package install is already running.";
                return false;
            }

            string url = repositoryUrl?.Trim() ?? string.Empty;
            string selectedBranch = branch?.Trim() ?? string.Empty;
            string packageName = expectedPackageName?.Trim() ?? string.Empty;
            if (!GitUtility.IsValidRepositoryUrl(url))
            {
                error = "A valid repository URL is required.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(selectedBranch) ||
                string.Equals(selectedBranch, ".", StringComparison.Ordinal) ||
                !GitUtility.IsValidBranchName(selectedBranch))
            {
                error = "Select an explicit valid Git branch before installing.";
                return false;
            }

            if (!GitUtility.IsValidUpmPackageName(packageName))
            {
                error = "A valid root UPM package name is required.";
                return false;
            }

            if (installMode != PackageManagerGitInstallMode.GitSubmodule &&
                installMode != PackageManagerGitInstallMode.ReadOnlyPackage)
            {
                error = "The selected package install mode is invalid.";
                return false;
            }

            var operation = new PendingInstall(
                Guid.NewGuid().ToString("N"),
                url,
                selectedBranch,
                packageName,
                installMode,
                onComplete,
                EditorApplication.timeSinceStartup);
            pending = operation;

            if (PackageDependencyInstallRequestFactory.TryCreateFromProbe(
                    exactProbeSnapshot,
                    url,
                    selectedBranch,
                    packageName,
                    installMode,
                    out PackageDependencyInstallRequest request,
                    out _))
            {
                if (BeginPreflight(operation, request, out error))
                    return true;

                pending = null;
                PublishIdle();
                return false;
            }

            operation.Probe = new GitSubmoduleInstallProbe();
            if (!operation.Probe.Request(url, selectedBranch))
            {
                operation.Probe.Dispose();
                operation.Probe = null;
                pending = null;
                error = "The selected branch could not be queued for inspection.";
                PublishIdle();
                return false;
            }

            Publish(
                PackageDependencyInstallPipelineStage.InspectingPackage,
                url,
                selectedBranch,
                packageName,
                installMode,
                "Inspecting the selected branch's root package.json with Git...");
            SubscribeUpdate();
            return true;
        }

        internal static bool TryConsumeLastCompletion(
            out PackageDependencyInstallPipelineCompletion completion)
        {
            completion = null;
            if (!PackageDependencyInstallCoordinator.TryConsumeLastCompletion(
                    out PackageDependencyInstallCompletion retained) ||
                retained == null)
            {
                return false;
            }

            completion = FromCoordinatorCompletion(retained, true);
            return true;
        }

        internal static bool TryGetLastCompletion(
            out PackageDependencyInstallPipelineCompletion completion)
        {
            completion = null;
            if (!PackageDependencyInstallCoordinator.TryGetLastCompletion(
                    out PackageDependencyInstallCompletion retained) ||
                retained == null)
            {
                return false;
            }

            completion = FromCoordinatorCompletion(retained, true);
            return true;
        }

        internal static bool TryConsumeLastCompletion(
            PackageDependencyInstallPipelineCompletion expected)
        {
            if (expected == null ||
                !TryGetLastCompletion(
                    out PackageDependencyInstallPipelineCompletion retained) ||
                !AreSameCompletion(expected, retained))
            {
                return false;
            }

            return TryConsumeLastCompletion(
                       out PackageDependencyInstallPipelineCompletion consumed) &&
                   AreSameCompletion(expected, consumed);
        }

        internal static PackageDependencyInstallCompletionDialogContent
            BuildRecoveredCompletionDialogContent(
                PackageDependencyInstallPipelineCompletion completion)
        {
            if (completion == null)
                return null;

            string packageName = GitHubUtility.SanitizeUiDiagnostic(
                completion.PackageName);
            if (!GitUtility.IsValidUpmPackageName(packageName))
                packageName = "the requested package";
            string message = PackageDependencyResolutionService
                .SanitizeDiagnostic(completion.Message);
            if (string.IsNullOrWhiteSpace(message))
            {
                message = completion.Success
                    ? "The package and its missing dependencies were installed successfully."
                    : "The dependency-aware Git package operation did not complete successfully.";
            }

            return new PackageDependencyInstallCompletionDialogContent(
                completion.Success
                    ? "Git Package Installed"
                    : "Git Package Install Failed",
                "Package: " + packageName + "\n\n" + message +
                "\n\nThis result was recovered after Unity reloaded scripts.",
                "OK");
        }

        internal static bool TryPresentRecoveredCompletion(
            PackageDependencyInstallPipelineCompletion completion,
            IPackageDependencyInstallCompletionDialog dialog = null)
        {
            PackageDependencyInstallCompletionDialogContent content =
                BuildRecoveredCompletionDialogContent(completion);
            if (content == null || (dialog == null && Application.isBatchMode))
                return false;

            try
            {
                (dialog ?? UnityPackageDependencyInstallCompletionDialog.Instance)
                    .Show(content);
                return true;
            }
            catch (Exception exception)
            {
                Debug.LogWarning(
                    "[Git Submodule Manager] A recovered package install " +
                    "completion could not be displayed: " +
                    GitHubUtility.SanitizeUiDiagnostic(exception.Message));
                return false;
            }
        }

        private static void Update()
        {
            PendingInstall operation = pending;
            if (operation == null)
            {
                if (PackageDependencyInstallCoordinator.IsBusy)
                {
                    PublishCoordinatorStateIfChanged();
                    SubscribeUpdate();
                }
                else
                {
                    UnsubscribeUpdate();
                }
                return;
            }

            if (operation.Stage !=
                    PackageDependencyInstallPipelineStage.Installing &&
                EditorApplication.timeSinceStartup - operation.StartedAt >=
                    PreMutationTimeoutSeconds)
            {
                Fail(
                    operation,
                    "Dependency inspection timed out before any package mutation started.",
                    false);
                return;
            }

            if (operation.Stage ==
                    PackageDependencyInstallPipelineStage.Installing)
            {
                if (PackageDependencyInstallCoordinator.IsBusy)
                    PublishCoordinatorStateIfChanged();
                return;
            }

            if (operation.Probe == null)
                return;

            operation.Probe.Tick();
            GitSubmoduleInstallProbeSnapshot snapshot = operation.Probe.Current;
            if (snapshot == null || !snapshot.IsComplete)
                return;

            if (!PackageDependencyInstallRequestFactory.TryCreateFromProbe(
                    snapshot,
                    operation.RepositoryUrl,
                    operation.Branch,
                    operation.PackageName,
                    operation.InstallMode,
                    out PackageDependencyInstallRequest request,
                    out string error))
            {
                Fail(operation, error, false);
                return;
            }

            operation.Probe.Dispose();
            operation.Probe = null;
            if (!BeginPreflight(operation, request, out string startError))
                Fail(operation, startError, false);
        }

        private static bool BeginPreflight(
            PendingInstall operation,
            PackageDependencyInstallRequest request,
            out string error)
        {
            error = string.Empty;
            if (!ReferenceEquals(pending, operation))
            {
                error = "The package install request is no longer current.";
                return false;
            }

            operation.Request = request;
            operation.Stage =
                PackageDependencyInstallPipelineStage.ResolvingDependencies;
            Publish(
                operation.Stage,
                operation.RepositoryUrl,
                operation.Branch,
                operation.PackageName,
                operation.InstallMode,
                "Checking installed packages, GitHub, and configured registries for dependencies...");
            SubscribeUpdate();
            string operationId = operation.OperationId;
            if (PackageDependencyPreflightService.TryStart(
                    request,
                    completion => OnPreflightCompleted(
                        operationId,
                        completion),
                    out error))
            {
                return true;
            }

            error = PackageDependencyResolutionService.SanitizeDiagnostic(error);
            return false;
        }

        private static void OnPreflightCompleted(
            string operationId,
            PackageDependencyPreflightCompletion completion)
        {
            PendingInstall operation = pending;
            if (operation == null ||
                !string.Equals(
                    operation.OperationId,
                    operationId,
                    StringComparison.Ordinal))
            {
                return;
            }

            PackageDependencyResolutionPlan plan = completion?.Plan;
            bool canInstall = PackageDependencyInstallPrompt.CanInstall(plan);
            if (!PackageDependencyInstallPrompt.TryConfirm(
                    operation.Request,
                    plan,
                    GitSubmoduleManagerUserSettings.Instance
                        .InstallDependenciesWithoutPrompt,
                    out string confirmationError))
            {
                Fail(
                    operation,
                    confirmationError,
                    canInstall);
                return;
            }

            if (!PackageDependencyInstallCoordinator.TryStart(
                    operation.Request,
                    plan,
                    null,
                    out string startError))
            {
                Fail(operation, startError, false);
                return;
            }

            operation.Stage = PackageDependencyInstallPipelineStage.Installing;
            Publish(
                operation.Stage,
                operation.RepositoryUrl,
                operation.Branch,
                operation.PackageName,
                operation.InstallMode,
                "Installing missing GitHub dependencies before the selected package...");
            SubscribeUpdate();
        }

        private static void OnCoordinatorCompleted(
            PackageDependencyInstallCompletion completion)
        {
            PendingInstall operation = pending;
            PackageDependencyInstallPipelineCompletion result =
                FromCoordinatorCompletion(completion, operation == null);
            if (operation != null &&
                MatchesCompletion(operation, completion))
            {
                result = new PackageDependencyInstallPipelineCompletion(
                    completion.Success,
                    false,
                    completion.Message,
                    operation.RepositoryUrl,
                    operation.Branch,
                    operation.PackageName,
                    operation.InstallMode);
                Finish(operation, result);
                return;
            }

            if (operation != null)
            {
                Fail(
                    operation,
                    "The dependency-aware installer completed with an " +
                    "unexpected root package identity. Refresh Package Manager " +
                    "before trying again.",
                    false);
                return;
            }

            PublishIdle();
            Invoke(Completed, result);
        }

        private static PackageDependencyInstallPipelineCompletion
            FromCoordinatorCompletion(
                PackageDependencyInstallCompletion completion,
                bool recoveredAfterReload = false)
        {
            return new PackageDependencyInstallPipelineCompletion(
                completion?.Success == true,
                false,
                string.IsNullOrWhiteSpace(completion?.Message)
                    ? "The dependency-aware package install did not complete successfully."
                    : completion.Message,
                completion?.RootRepositoryUrl,
                completion?.RootRevision,
                completion?.RootPackageName,
                completion?.InstallMode ??
                    PackageManagerGitInstallMode.GitSubmodule,
                recoveredAfterReload);
        }

        private static bool MatchesCompletion(
            PendingInstall operation,
            PackageDependencyInstallCompletion completion)
        {
            return operation != null && completion != null &&
                   string.Equals(
                       operation.PackageName,
                       completion.RootPackageName,
                       StringComparison.Ordinal) &&
                   operation.InstallMode == completion.InstallMode &&
                   GitUtility.AreRepositoryUrlsEquivalent(
                       operation.RepositoryUrl,
                       completion.RootRepositoryUrl) &&
                   string.Equals(
                       operation.Branch,
                       completion.RootRevision,
                       StringComparison.Ordinal);
        }

        private static bool AreSameCompletion(
            PackageDependencyInstallPipelineCompletion left,
            PackageDependencyInstallPipelineCompletion right)
        {
            return left != null && right != null &&
                   left.Success == right.Success &&
                   left.Cancelled == right.Cancelled &&
                   left.InstallMode == right.InstallMode &&
                   string.Equals(
                       left.Message,
                       right.Message,
                       StringComparison.Ordinal) &&
                   string.Equals(
                       left.PackageName,
                       right.PackageName,
                       StringComparison.Ordinal) &&
                   GitUtility.AreRepositoryUrlsEquivalent(
                       left.RepositoryUrl,
                       right.RepositoryUrl) &&
                   string.Equals(
                       left.Branch,
                       right.Branch,
                       StringComparison.Ordinal);
        }

        private static void PublishCoordinatorStateIfChanged()
        {
            PackageManagerGitInstallMode mode =
                PackageDependencyInstallCoordinator.ActiveInstallMode ??
                PackageManagerGitInstallMode.GitSubmodule;
            string message = BuildCoordinatorStatusMessage();
            PackageDependencyInstallPipelineSnapshot current = Current;
            if (current != null &&
                current.Stage == PackageDependencyInstallPipelineStage.Installing &&
                current.InstallMode == mode &&
                string.Equals(
                    current.RepositoryUrl,
                    PackageDependencyInstallCoordinator.ActiveRepositoryUrl,
                    StringComparison.Ordinal) &&
                string.Equals(
                    current.Branch,
                    PackageDependencyInstallCoordinator.ActiveRevision,
                    StringComparison.Ordinal) &&
                string.Equals(
                    current.PackageName,
                    PackageDependencyInstallCoordinator.ActiveRootPackageName,
                    StringComparison.Ordinal) &&
                string.Equals(current.Message, message, StringComparison.Ordinal))
            {
                return;
            }

            PublishCoordinatorState();
        }

        private static void PublishCoordinatorState()
        {
            Publish(
                PackageDependencyInstallPipelineStage.Installing,
                PackageDependencyInstallCoordinator.ActiveRepositoryUrl,
                PackageDependencyInstallCoordinator.ActiveRevision,
                PackageDependencyInstallCoordinator.ActiveRootPackageName,
                PackageDependencyInstallCoordinator.ActiveInstallMode ??
                    PackageManagerGitInstallMode.GitSubmodule,
                BuildCoordinatorStatusMessage());
        }

        internal static string BuildCoordinatorStatusMessage()
        {
            int count = PackageDependencyInstallCoordinator.ActiveStepCount;
            int index = PackageDependencyInstallCoordinator.ActiveStepIndex;
            string packageName =
                PackageDependencyInstallCoordinator.ActiveStepPackageName;
            if (count > 0 && index >= 0 && index < count &&
                GitUtility.IsValidUpmPackageName(packageName))
            {
                return $"Installing package {index + 1} of {count}: " +
                       packageName + "...";
            }

            return "Resuming dependency-aware package installation...";
        }

        private static void Fail(
            PendingInstall operation,
            string message,
            bool cancelled)
        {
            if (!ReferenceEquals(pending, operation))
                return;

            if (operation.Stage ==
                PackageDependencyInstallPipelineStage.ResolvingDependencies)
            {
                PackageDependencyPreflightService.Cancel();
            }

            operation.Probe?.Dispose();
            operation.Probe = null;
            var completion = new PackageDependencyInstallPipelineCompletion(
                false,
                cancelled,
                string.IsNullOrWhiteSpace(message)
                    ? "The dependency-aware package install could not continue safely."
                    : message,
                operation.RepositoryUrl,
                operation.Branch,
                operation.PackageName,
                operation.InstallMode);
            Finish(operation, completion);
        }

        private static void Finish(
            PendingInstall operation,
            PackageDependencyInstallPipelineCompletion completion)
        {
            Action<PackageDependencyInstallPipelineCompletion> callback =
                operation?.Callback;
            if (ReferenceEquals(pending, operation))
                pending = null;
            PublishIdle();
            Invoke(callback, completion);
            Invoke(Completed, completion);
        }

        private static void Publish(
            PackageDependencyInstallPipelineStage stage,
            string repositoryUrl,
            string branch,
            string packageName,
            PackageManagerGitInstallMode installMode,
            string message)
        {
            if (pending != null)
                pending.Stage = stage;
            Current = new PackageDependencyInstallPipelineSnapshot(
                ++revision,
                stage,
                repositoryUrl,
                branch,
                packageName,
                installMode,
                message);
            Invoke(Changed, Current);
        }

        private static void PublishIdle()
        {
            Current = new PackageDependencyInstallPipelineSnapshot(
                ++revision,
                PackageDependencyInstallPipelineStage.Idle,
                string.Empty,
                string.Empty,
                string.Empty,
                PackageManagerGitInstallMode.GitSubmodule,
                string.Empty);
            Invoke(Changed, Current);
            if (!PackageDependencyInstallCoordinator.IsBusy)
                UnsubscribeUpdate();
        }

        private static void SubscribeUpdate()
        {
            if (updateSubscribed)
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

        private static void Invoke<T>(Action<T> handler, T value)
        {
            Delegate[] subscribers = handler?.GetInvocationList();
            if (subscribers == null)
                return;
            foreach (Delegate subscriber in subscribers)
            {
                try
                {
                    ((Action<T>)subscriber).Invoke(value);
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
            if (pending != null &&
                pending.Stage !=
                    PackageDependencyInstallPipelineStage.Installing)
            {
                PackageDependencyPreflightService.Cancel();
            }
            pending?.Probe?.Dispose();
            pending = null;
            PackageDependencyInstallCoordinator.Completed -=
                OnCoordinatorCompleted;
            AssemblyReloadEvents.beforeAssemblyReload -= OnBeforeAssemblyReload;
        }

        private sealed class PendingInstall
        {
            internal PendingInstall(
                string operationId,
                string repositoryUrl,
                string branch,
                string packageName,
                PackageManagerGitInstallMode installMode,
                Action<PackageDependencyInstallPipelineCompletion> callback,
                double startedAt)
            {
                OperationId = operationId;
                RepositoryUrl = repositoryUrl;
                Branch = branch;
                PackageName = packageName;
                InstallMode = installMode;
                Callback = callback;
                StartedAt = startedAt;
            }

            internal string OperationId { get; }
            internal string RepositoryUrl { get; }
            internal string Branch { get; }
            internal string PackageName { get; }
            internal PackageManagerGitInstallMode InstallMode { get; }
            internal Action<PackageDependencyInstallPipelineCompletion> Callback
                { get; }
            internal double StartedAt { get; }
            internal GitSubmoduleInstallProbe Probe { get; set; }
            internal PackageDependencyInstallRequest Request { get; set; }
            internal PackageDependencyInstallPipelineStage Stage { get; set; }
        }
    }
}
