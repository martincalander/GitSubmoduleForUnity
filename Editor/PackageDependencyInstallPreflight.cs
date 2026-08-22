using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using UnityEditor;

namespace MartinCalander.GitSubmoduleManager.Editor
{
    /// <summary>
    /// Immutable root install intent shared by Package Manager's GitHub action
    /// and its add-menu popup. Dependency metadata must describe the exact
    /// repository revision represented by <see cref="Revision"/>.
    /// </summary>
    internal sealed class PackageDependencyInstallRequest
    {
        internal PackageDependencyInstallRequest(
            string repositoryUrl,
            string revision,
            string rootPackageName,
            string rootVersion,
            PackageManagerGitInstallMode installMode,
            IEnumerable<PackageManifestDependency> dependencies)
        {
            RepositoryUrl = repositoryUrl?.Trim() ?? string.Empty;
            Revision = revision?.Trim() ?? string.Empty;
            RootPackageName = rootPackageName?.Trim() ?? string.Empty;
            RootVersion = rootVersion?.Trim() ?? string.Empty;
            InstallMode = installMode;
            Dependencies = new ReadOnlyCollection<PackageManifestDependency>(
                (dependencies ?? Array.Empty<PackageManifestDependency>())
                .Where(dependency => dependency != null)
                .Select(dependency => new PackageManifestDependency(
                    dependency.Name,
                    dependency.Version))
                .OrderBy(dependency => dependency.Name, StringComparer.Ordinal)
                .ToArray());
        }

        internal string RepositoryUrl { get; }
        internal string Revision { get; }
        internal string RootPackageName { get; }
        internal string RootVersion { get; }
        internal PackageManagerGitInstallMode InstallMode { get; }
        internal IReadOnlyList<PackageManifestDependency> Dependencies { get; }
    }

    /// <summary>
    /// Binds dependency metadata to the exact repository revision the user
    /// selected. Discovery metadata describes a repository's default branch and
    /// must never be reused for another branch.
    /// </summary>
    internal static class PackageDependencyInstallRequestFactory
    {
        internal static bool TryCreateFromProbe(
            GitSubmoduleInstallProbeSnapshot snapshot,
            string repositoryUrl,
            string selectedBranch,
            string expectedPackageName,
            PackageManagerGitInstallMode installMode,
            out PackageDependencyInstallRequest request,
            out string error)
        {
            request = null;
            error = string.Empty;
            string url = repositoryUrl?.Trim() ?? string.Empty;
            string branch = selectedBranch?.Trim() ?? string.Empty;
            string packageName = expectedPackageName?.Trim() ?? string.Empty;
            if (!GitUtility.IsValidRepositoryUrl(url))
            {
                error = "A valid repository URL is required.";
                return false;
            }

            if (string.IsNullOrEmpty(branch) ||
                string.Equals(branch, ".", StringComparison.Ordinal) ||
                !GitUtility.IsValidBranchName(branch))
            {
                error = "Select an explicit valid Git branch before installing.";
                return false;
            }

            if (!GitUtility.IsValidUpmPackageName(packageName))
            {
                error = "A valid root UPM package name is required.";
                return false;
            }

            if (snapshot == null ||
                snapshot.Status != GitSubmoduleInstallProbeStatus.Ready)
            {
                error = "The selected branch's root package.json has not been inspected successfully.";
                return false;
            }

            if (!string.IsNullOrWhiteSpace(snapshot.ErrorMessage) ||
                !string.IsNullOrWhiteSpace(snapshot.ManifestMessage))
            {
                error = PackageDependencyResolutionService.SanitizeDiagnostic(
                    string.IsNullOrWhiteSpace(snapshot.ManifestMessage)
                        ? snapshot.ErrorMessage
                        : snapshot.ManifestMessage);
                if (string.IsNullOrWhiteSpace(error))
                {
                    error = "The selected branch's root package.json could not be inspected safely.";
                }
                return false;
            }

            if (!GitUtility.AreRepositoryUrlsEquivalent(snapshot.Url, url))
            {
                error = "The inspected repository no longer matches the selected repository.";
                return false;
            }

            if (!string.Equals(
                    snapshot.RequestedBranch,
                    branch,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    snapshot.InspectedBranch,
                    branch,
                    StringComparison.Ordinal))
            {
                error = "The inspected package metadata belongs to another branch.";
                return false;
            }

            if (!GitUtility.IsValidUpmPackageName(snapshot.PackageName) ||
                !string.Equals(
                    snapshot.PackageName.Trim(),
                    packageName,
                    StringComparison.Ordinal))
            {
                error = "The selected branch's root package.json does not declare the expected package name.";
                return false;
            }

            if (!GitUtility.IsValidSemanticVersion(snapshot.Version))
            {
                error = "The selected branch's root package.json does not declare a valid package version.";
                return false;
            }

            request = new PackageDependencyInstallRequest(
                url,
                branch,
                packageName,
                snapshot.Version,
                installMode,
                snapshot.Dependencies);
            error = PackageDependencyPreflightRunner.ValidateRequest(request);
            if (!string.IsNullOrWhiteSpace(error))
            {
                request = null;
                return false;
            }

            return true;
        }
    }

    internal sealed class PackageDependencyPreflightCompletion
    {
        internal PackageDependencyPreflightCompletion(
            bool success,
            PackageDependencyInstallRequest request,
            PackageDependencyResolutionPlan plan,
            string message)
        {
            Success = success;
            Request = request;
            Plan = plan ?? PackageDependencyResolutionPlan.Empty;
            Message = message ?? string.Empty;
        }

        internal bool Success { get; }
        internal PackageDependencyInstallRequest Request { get; }
        internal PackageDependencyResolutionPlan Plan { get; }
        internal string Message { get; }
    }

    /// <summary>
    /// Instance core used by the Editor-update wrapper and deterministic tests.
    /// </summary>
    internal sealed class PackageDependencyPreflightRunner : IDisposable
    {
        private readonly PackageDependencyResolutionService resolver;
        private Action<PackageDependencyPreflightCompletion> callback;
        private PackageDependencyInstallRequest request;
        private bool disposed;

        internal PackageDependencyPreflightRunner(
            PackageDependencyResolutionService resolver)
        {
            this.resolver = resolver ??
                            throw new ArgumentNullException(nameof(resolver));
        }

        internal bool IsBusy => request != null && resolver.IsRunning;

        internal bool TryStart(
            PackageDependencyInstallRequest installRequest,
            Action<PackageDependencyPreflightCompletion> onComplete,
            out string error)
        {
            error = ValidateRequest(installRequest);
            if (!string.IsNullOrEmpty(error))
                return false;
            if (disposed)
            {
                error = "The dependency preflight runner has been disposed.";
                return false;
            }
            if (request != null)
            {
                error = "Another dependency preflight is already running.";
                return false;
            }

            if (!resolver.TryStart(
                    installRequest.RootPackageName,
                    installRequest.Dependencies,
                    out error))
            {
                error = PackageDependencyResolutionService.SanitizeDiagnostic(
                    error);
                return false;
            }

            request = installRequest;
            callback = onComplete;
            return true;
        }

        internal bool Tick()
        {
            if (disposed || request == null)
                return false;

            bool changed = resolver.Tick();
            PackageDependencyResolutionPlan plan = resolver.Current;
            if (!plan.IsComplete)
                return changed;

            PackageDependencyInstallRequest completedRequest = request;
            Action<PackageDependencyPreflightCompletion> completedCallback =
                callback;
            request = null;
            callback = null;
            var completion = new PackageDependencyPreflightCompletion(
                string.IsNullOrWhiteSpace(plan.ErrorMessage),
                completedRequest,
                plan,
                plan.ErrorMessage);
            try
            {
                completedCallback?.Invoke(completion);
            }
            catch (Exception exception)
            {
                UnityEngine.Debug.LogException(exception);
            }

            return true;
        }

        public void Dispose()
        {
            if (disposed)
                return;
            disposed = true;
            request = null;
            callback = null;
            resolver.Dispose();
        }

        internal static string ValidateRequest(
            PackageDependencyInstallRequest request)
        {
            if (request == null)
                return "A package install request is required.";
            if (!GitUtility.IsValidRepositoryUrl(request.RepositoryUrl))
                return "A valid repository URL is required.";
            if (!GitUtility.IsValidUpmPackageName(request.RootPackageName))
                return "A valid root UPM package name is required.";
            if (string.IsNullOrWhiteSpace(request.Revision) ||
                string.Equals(request.Revision, ".", StringComparison.Ordinal) ||
                !GitUtility.IsValidBranchName(request.Revision))
                return "The requested Git revision is invalid.";
            if (!GitUtility.IsValidSemanticVersion(request.RootVersion))
                return "The root package must declare a valid semantic version.";
            if (request.InstallMode != PackageManagerGitInstallMode.GitSubmodule &&
                request.InstallMode != PackageManagerGitInstallMode.ReadOnlyPackage)
            {
                return "The requested package install mode is invalid.";
            }

            return string.Empty;
        }
    }

    /// <summary>
    /// Main-thread, single-flight wrapper. It owns only read-only discovery and
    /// registry searches; project mutation starts later in the coordinator.
    /// </summary>
    [InitializeOnLoad]
    internal static class PackageDependencyPreflightService
    {
        private static PackageDependencyPreflightRunner runner;

        internal static event Action<PackageDependencyPreflightCompletion> Completed;

        static PackageDependencyPreflightService()
        {
            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;
        }

        internal static bool IsBusy => runner?.IsBusy == true;

        internal static bool TryStart(
            PackageDependencyInstallRequest request,
            Action<PackageDependencyPreflightCompletion> onComplete,
            out string error)
        {
            if (IsBusy)
            {
                error = "Another dependency preflight is already running.";
                return false;
            }

            if (request?.Dependencies.Any(dependency =>
                    dependency != null &&
                    !dependency.Name.StartsWith(
                        "com.unity.",
                        StringComparison.Ordinal)) == true)
            {
                PackageManagerGitHubDiscovery.EnsureStarted();
            }

            runner?.Dispose();
            runner = new PackageDependencyPreflightRunner(
                new PackageDependencyResolutionService());
            if (!runner.TryStart(
                    request,
                    completion => NotifyCompletion(onComplete, completion),
                    out error))
            {
                runner.Dispose();
                runner = null;
                return false;
            }

            EditorApplication.update -= Update;
            EditorApplication.update += Update;
            return true;
        }

        internal static void Cancel()
        {
            EditorApplication.update -= Update;
            runner?.Dispose();
            runner = null;
        }

        private static void Update()
        {
            if (runner?.IsBusy == true)
            {
                runner.Tick();
                return;
            }

            EditorApplication.update -= Update;
            runner?.Dispose();
            runner = null;
        }

        private static void NotifyCompletion(
            Action<PackageDependencyPreflightCompletion> callback,
            PackageDependencyPreflightCompletion completion)
        {
            Invoke(callback, completion);
            Invoke(Completed, completion);
        }

        private static void Invoke(
            Action<PackageDependencyPreflightCompletion> handler,
            PackageDependencyPreflightCompletion completion)
        {
            Delegate[] subscribers = handler?.GetInvocationList();
            if (subscribers == null)
                return;
            foreach (Delegate subscriber in subscribers)
            {
                try
                {
                    ((Action<PackageDependencyPreflightCompletion>)subscriber)
                        .Invoke(completion);
                }
                catch (Exception exception)
                {
                    UnityEngine.Debug.LogException(exception);
                }
            }
        }

        private static void OnBeforeAssemblyReload()
        {
            Cancel();
            AssemblyReloadEvents.beforeAssemblyReload -= OnBeforeAssemblyReload;
        }
    }
}
