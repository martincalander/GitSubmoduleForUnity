using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace MartinCalander.GitSubmoduleManager.Editor
{
    /// <summary>
    /// Adds a discovered GitHub repository's controls to Unity's native primary
    /// Package Manager action area. The implementation deliberately avoids the
    /// PackageExtensionAction contract because Unity renders that contract inside
    /// the Extensions overflow menu. Native containers are resolved from their
    /// verified Package Manager seams at run time and the entire optional
    /// integration fails closed when they drift.
    /// </summary>
    internal static class PackageManagerGitHubNativeActions
    {
        internal const string PackageManagerWindowRootTypeName =
            "UnityEditor.PackageManager.UI.Internal.PackageManagerWindowRoot";
        internal const string PackageToolbarTypeName =
            "UnityEditor.PackageManager.UI.Internal.PackageToolbar";
        internal const string PackageDetailsLinksTypeName =
            "UnityEditor.PackageManager.UI.Internal.PackageDetailsLinks";
        internal const string PackageDetailsHeaderTypeName =
            "UnityEditor.PackageManager.UI.Internal.PackageDetailsHeader";
        internal const string BuiltInActionsFieldName =
            "m_BuiltInActionsContainer";
        internal const string DetailsLinksPropertyName = "detailsLinks";
        internal const string ActionButtonElementName =
            PackageManagerGitHubDetails.InstallActionElementName;

        private const BindingFlags AnyInstance =
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        private static readonly Dictionary<object, NativeActionEntry> EntriesByRoot =
            new(ReferenceComparer.Instance);
        private static readonly Dictionary<object, NativeActionEntry> EntriesByToolbar =
            new(ReferenceComparer.Instance);
        private static readonly Dictionary<string, string> ActiveInstallMessages =
            new(StringComparer.Ordinal);

        internal static int InstalledRootCount => EntriesByRoot.Count;

        internal static bool InstallForRoot(object packageManagerRoot)
        {
            if (!SupportsNativePageEditorVersion ||
                !HasSupportedLiveContract() ||
                !(packageManagerRoot is VisualElement root) ||
                !string.Equals(
                    root.GetType().FullName,
                    PackageManagerWindowRootTypeName,
                    StringComparison.Ordinal))
            {
                return false;
            }

            try
            {
                VisualElement toolbar = FindPackageToolbar(root);
                if (toolbar == null)
                    return false;

                VisualElement primaryActions = ResolvePrimaryActionsContainer(toolbar);
                VisualElement detailsLinks = FindNamedDescendant(
                    root,
                    PackageManagerGitHubDetails.NativeDetailsLinksContainerName);
                if (primaryActions == null || detailsLinks == null)
                    return false;

                if (EntriesByRoot.TryGetValue(root, out NativeActionEntry current))
                {
                    if (ReferenceEquals(current.Toolbar, toolbar) &&
                        ReferenceEquals(
                            current.PrimaryActionsContainer,
                            primaryActions) &&
                        ReferenceEquals(current.DetailsLinksContainer, detailsLinks))
                    {
                        current.Details.EnsurePrimaryControlsMounted();
                        RefreshForToolbar(toolbar, GetFieldValue(toolbar, "m_Package"));
                        return true;
                    }

                    ReleaseForRoot(root);
                }

                if (EntriesByToolbar.TryGetValue(
                        toolbar,
                        out NativeActionEntry previousToolbarEntry) &&
                    !ReferenceEquals(previousToolbarEntry.Root, root))
                {
                    ReleaseForRoot(previousToolbarEntry.Root);
                }

                PackageManagerGitHubDetails details = null;
                if (!PackageManagerGitHubDetails.TryCreate(
                        primaryActions,
                        detailsLinks,
                        (repository, branch) =>
                            OnInstallRequested(
                                toolbar,
                                details,
                                repository,
                                branch),
                        Application.OpenURL,
                        true,
                        out details))
                {
                    return false;
                }

                EventCallback<DetachFromPanelEvent> detached = _ =>
                    ReleaseForRoot(root);
                var entry = new NativeActionEntry(
                    root,
                    toolbar,
                    primaryActions,
                    detailsLinks,
                    details,
                    detached);
                EntriesByRoot[root] = entry;
                EntriesByToolbar[toolbar] = entry;
                root.RegisterCallback(detached);
                RefreshForToolbar(toolbar, GetFieldValue(toolbar, "m_Package"));
                return true;
            }
            catch
            {
                ReleaseForRoot(root);
                return false;
            }
        }

        internal static void ReleaseForRoot(object packageManagerRoot)
        {
            if (packageManagerRoot == null ||
                !EntriesByRoot.TryGetValue(
                    packageManagerRoot,
                    out NativeActionEntry entry))
            {
                return;
            }

            EntriesByRoot.Remove(packageManagerRoot);
            if (EntriesByToolbar.TryGetValue(
                    entry.Toolbar,
                    out NativeActionEntry toolbarEntry) &&
                ReferenceEquals(toolbarEntry, entry))
            {
                EntriesByToolbar.Remove(entry.Toolbar);
            }

            try
            {
                entry.Root.UnregisterCallback(entry.DetachedCallback);
            }
            catch
            {
                // The Package Manager tree may already be tearing down.
            }

            entry.Details.Dispose();
        }

        internal static void RefreshForToolbar(object toolbar, object package)
        {
            if (toolbar == null ||
                !EntriesByToolbar.TryGetValue(toolbar, out NativeActionEntry entry))
            {
                return;
            }

            try
            {
                VisualElement currentPrimaryActions =
                    ResolvePrimaryActionsContainer(entry.Toolbar);
                VisualElement currentDetailsLinks = FindNamedDescendant(
                    entry.Root,
                    PackageManagerGitHubDetails.NativeDetailsLinksContainerName);
                if (!ReferenceEquals(
                        entry.PrimaryActionsContainer,
                        currentPrimaryActions) ||
                    !ReferenceEquals(
                        entry.DetailsLinksContainer,
                        currentDetailsLinks))
                {
                    VisualElement root = entry.Root;
                    ReleaseForRoot(root);
                    InstallForRoot(root);
                    return;
                }

                PackageManagerGitHubRepository repository = null;
                bool isProjectedRepository =
                    IsGitHubPage(toolbar) &&
                    PackageManagerGitHubPackageProjection.TryGetRepository(
                        package,
                        out repository);
                if (!isProjectedRepository)
                {
                    entry.Details.Refresh(null);
                    entry.Details.SetInstallState(false, false, string.Empty);
                    return;
                }

                entry.Details.Refresh(repository);
                string installRepositoryIdentity =
                    PackageManagerGitHubDetails.GetInstallRepositoryIdentity(
                        repository);
                if (ActiveInstallMessages.TryGetValue(
                        installRepositoryIdentity,
                        out string activeInstallMessage))
                {
                    entry.Details.ShowInstalling(activeInstallMessage);
                    entry.Details.SetInstallState(
                        true,
                        false,
                        activeInstallMessage);
                    return;
                }

                if (entry.Details.IsInstalling)
                {
                    if (GitOperationService.IsBusy)
                    {
                        const string activeOperationMessage =
                            "A repository operation is still running. " +
                            "Package Manager will refresh when it finishes.";
                        entry.Details.ShowInstalling(activeOperationMessage);
                        entry.Details.SetInstallState(
                            true,
                            false,
                            activeOperationMessage);
                        return;
                    }

                    entry.Details.ShowInstallError(
                        "The install operation finished, but Package Manager " +
                        "could not match its final state. Use Refresh to retry.");
                }

                string selectedBranch = entry.Details.SelectedBranch;
                string validationError = GitSubmoduleAddService.ValidateInput(
                    repository.Url,
                    repository.PackageName,
                    selectedBranch);
                bool enabled =
                    string.IsNullOrWhiteSpace(validationError) &&
                    GitSubmoduleAddService.CanStart;
                string tooltip = enabled
                    ? BuildEnabledTooltip(repository, selectedBranch)
                    : BuildDisabledTooltip(validationError);
                entry.Details.SetInstallState(true, enabled, tooltip);
            }
            catch
            {
                entry.Details.Refresh(null);
                entry.Details.SetInstallState(false, false, string.Empty);
            }
        }

        internal static bool HasSupportedLiveContract()
        {
            try
            {
                Type rootType = PackageManagerSubmoduleHarmonyPatch.FindLoadedType(
                    PackageManagerWindowRootTypeName);
                Type toolbarType = PackageManagerSubmoduleHarmonyPatch.FindLoadedType(
                    PackageToolbarTypeName);
                Type linksType = PackageManagerSubmoduleHarmonyPatch.FindLoadedType(
                    PackageDetailsLinksTypeName);
                Type headerType = PackageManagerSubmoduleHarmonyPatch.FindLoadedType(
                    PackageDetailsHeaderTypeName);
                FieldInfo primaryActionsField = toolbarType?.GetField(
                    BuiltInActionsFieldName,
                    AnyInstance);
                PropertyInfo detailsLinksProperty = headerType?.GetProperty(
                    DetailsLinksPropertyName,
                    AnyInstance);
                return rootType != null &&
                       typeof(VisualElement).IsAssignableFrom(rootType) &&
                       toolbarType != null &&
                       typeof(VisualElement).IsAssignableFrom(toolbarType) &&
                       linksType != null &&
                       typeof(VisualElement).IsAssignableFrom(linksType) &&
                       headerType != null &&
                       typeof(VisualElement).IsAssignableFrom(headerType) &&
                       primaryActionsField != null &&
                       !primaryActionsField.IsStatic &&
                       typeof(VisualElement).IsAssignableFrom(
                           primaryActionsField.FieldType) &&
                       detailsLinksProperty != null &&
                       detailsLinksProperty.GetIndexParameters().Length == 0 &&
                       detailsLinksProperty.PropertyType == linksType;
            }
            catch
            {
                return false;
            }
        }

        private static bool SupportsNativePageEditorVersion
        {
            get
            {
#if UNITY_2023_2_OR_NEWER
                return true;
#else
                return false;
#endif
            }
        }

        internal static VisualElement ResolvePrimaryActionsContainer(
            object toolbar)
        {
            if (!(toolbar is VisualElement) ||
                !string.Equals(
                    toolbar.GetType().FullName,
                    PackageToolbarTypeName,
                    StringComparison.Ordinal))
            {
                return null;
            }

            try
            {
                FieldInfo field = toolbar.GetType().GetField(
                    BuiltInActionsFieldName,
                    AnyInstance);
                if (field == null ||
                    !string.Equals(
                        field.Name,
                        BuiltInActionsFieldName,
                        StringComparison.Ordinal))
                    return null;

                return ReadVerifiedPrimaryActionsContainer(toolbar, field);
            }
            catch
            {
                return null;
            }
        }

        internal static VisualElement ReadVerifiedPrimaryActionsContainer(
            object toolbar,
            FieldInfo primaryActionsField)
        {
            if (toolbar == null ||
                primaryActionsField == null ||
                primaryActionsField.IsStatic ||
                !string.Equals(
                    primaryActionsField.Name,
                    BuiltInActionsFieldName,
                    StringComparison.Ordinal) ||
                !typeof(VisualElement).IsAssignableFrom(
                    primaryActionsField.FieldType) ||
                primaryActionsField.DeclaringType == null ||
                !primaryActionsField.DeclaringType.IsInstanceOfType(toolbar))
            {
                return null;
            }

            try
            {
                return primaryActionsField.GetValue(toolbar) as VisualElement;
            }
            catch
            {
                return null;
            }
        }

        internal static VisualElement FindPackageToolbar(VisualElement root)
        {
            if (root == null)
                return null;
            if (string.Equals(
                    root.GetType().FullName,
                    PackageToolbarTypeName,
                    StringComparison.Ordinal))
            {
                return root;
            }

            foreach (VisualElement child in root.Children())
            {
                VisualElement match = FindPackageToolbar(child);
                if (match != null)
                    return match;
            }

            return null;
        }

        internal static VisualElement FindNamedDescendant(
            VisualElement root,
            string elementName)
        {
            if (root == null || string.IsNullOrEmpty(elementName))
                return null;
            if (string.Equals(root.name, elementName, StringComparison.Ordinal))
                return root;

            foreach (VisualElement child in root.Children())
            {
                VisualElement match = FindNamedDescendant(child, elementName);
                if (match != null)
                    return match;
            }

            return null;
        }

        private static void OnInstallRequested(
            VisualElement toolbar,
            PackageManagerGitHubDetails sourceDetails,
            PackageManagerGitHubRepository repository,
            string selectedBranch)
        {
            PackageManagerGitHubDetails feedbackTarget = sourceDetails;
            string activeInstallIdentity = string.Empty;
            bool operationStarted = false;
            try
            {
                if (toolbar == null || repository == null)
                {
                    ReportInstallError(
                        feedbackTarget,
                        "Cannot Install Git Package",
                        "The selected GitHub repository is no longer available. " +
                        "Select it again in Sources > GitHub and retry.");
                    return;
                }

                if (!EntriesByToolbar.TryGetValue(
                        toolbar,
                        out NativeActionEntry entry))
                {
                    ReportInstallError(
                        feedbackTarget,
                        "Cannot Install Git Package",
                        "Package Manager refreshed before the install request could be handled. " +
                        "Select the repository again and retry.");
                    return;
                }

                feedbackTarget = entry.Details;
                if (!IsGitHubPage(toolbar))
                {
                    ReportInstallError(
                        feedbackTarget,
                        "Cannot Install Git Package",
                        "Sources > GitHub is no longer active. Return to that source and retry.");
                    return;
                }

                string requestedIdentity =
                    PackageManagerGitHubDetails.GetRepositoryIdentity(repository);
                string currentIdentity =
                    PackageManagerGitHubDetails.GetRepositoryIdentity(
                        entry.Details.CurrentRepository);
                if (string.IsNullOrEmpty(requestedIdentity) ||
                    !string.Equals(
                        requestedIdentity,
                        currentIdentity,
                        StringComparison.Ordinal))
                {
                    ReportInstallError(
                        feedbackTarget,
                        "Cannot Install Git Package",
                        "The selected GitHub repository changed before the install request " +
                        "could be handled. Select it again and retry.");
                    return;
                }

                object selectedPackage = GetFieldValue(toolbar, "m_Package");
                bool resolvedProjectedRepository =
                    PackageManagerGitHubPackageProjection.TryGetRepository(
                        selectedPackage,
                        out PackageManagerGitHubRepository projectedRepository);
                string requestedSelectionIdentity =
                    PackageManagerGitHubDetails.GetInstallSelectionIdentity(
                        repository,
                        selectedBranch);
                string projectedSelectionIdentity =
                    PackageManagerGitHubDetails.GetInstallSelectionIdentity(
                        projectedRepository,
                        selectedBranch);
                if (!resolvedProjectedRepository ||
                    string.IsNullOrEmpty(requestedSelectionIdentity) ||
                    !string.Equals(
                        projectedSelectionIdentity,
                        requestedSelectionIdentity,
                        StringComparison.Ordinal))
                {
                    ReportInstallError(
                        feedbackTarget,
                        "Cannot Install Git Package",
                        "This discovered repository is no longer selected. " +
                        "Select it again in Sources > GitHub and retry.");
                    RefreshAllEntries();
                    return;
                }

                repository = projectedRepository;
                string branch = selectedBranch?.Trim() ?? string.Empty;
                string validationError = GitSubmoduleAddService.ValidateInput(
                    repository.Url,
                    repository.PackageName,
                    branch);
                if (!string.IsNullOrWhiteSpace(validationError))
                {
                    ReportInstallError(
                        feedbackTarget,
                        "Cannot Install Git Package",
                        validationError);
                    RefreshAllEntries();
                    return;
                }

                if (!GitSubmoduleAddService.CanStart)
                {
                    ReportInstallError(
                        feedbackTarget,
                        "Cannot Install Git Package",
                        BuildDisabledTooltip(string.Empty));
                    RefreshAllEntries();
                    return;
                }

                string installIdentity =
                    PackageManagerGitHubDetails.GetRepositoryIdentity(repository);
                activeInstallIdentity =
                    PackageManagerGitHubDetails.GetInstallRepositoryIdentity(
                        repository);
                if (string.IsNullOrEmpty(installIdentity) ||
                    string.IsNullOrEmpty(activeInstallIdentity))
                {
                    ReportInstallError(
                        feedbackTarget,
                        "Cannot Install Git Package",
                        "The selected repository could not be bound to a safe " +
                        "install operation. Select it again and retry.");
                    RefreshAllEntries();
                    return;
                }

                PackageManagerGitHubDetails completionTarget = feedbackTarget;
                string installMessage = feedbackTarget?.InstallFeedback?.text;
                if (string.IsNullOrWhiteSpace(installMessage))
                {
                    installMessage = "Installing " + repository.PackageName +
                                     " as a Git submodule...";
                }

                ActiveInstallMessages[activeInstallIdentity] = installMessage;
                bool started = GitSubmoduleAddService.TryStart(
                    repository.Url,
                    branch,
                    repository.PackageName,
                    completion => OnAddCompleted(
                        completionTarget,
                        installIdentity,
                        activeInstallIdentity,
                        completion),
                    out string startError);
                operationStarted = started;
                if (!started)
                {
                    ActiveInstallMessages.Remove(activeInstallIdentity);
                    ReportInstallError(
                        feedbackTarget,
                        "Could Not Start Install",
                        string.IsNullOrWhiteSpace(startError)
                            ? "The Git package operation could not be started."
                            : startError);
                }

                RefreshAllEntries();
            }
            catch (Exception exception)
            {
                if (operationStarted)
                {
                    Debug.LogWarning(
                        "[Git Submodule Manager] The Git package install started, " +
                        "but Package Manager could not refresh its inline status: " +
                        GitHubUtility.SanitizeUiDiagnostic(exception.Message));
                    return;
                }

                if (!string.IsNullOrEmpty(activeInstallIdentity))
                    ActiveInstallMessages.Remove(activeInstallIdentity);
                ReportInstallError(
                    feedbackTarget,
                    "Could Not Start Install",
                    "The Git package operation could not be started: " +
                    exception.Message);
                RefreshAllEntries();
            }
        }

        private static void OnAddCompleted(
            PackageManagerGitHubDetails preferredDetails,
            string repositoryIdentity,
            string activeInstallIdentity,
            GitSubmoduleAddCompletion completion)
        {
            if (!string.IsNullOrEmpty(activeInstallIdentity))
                ActiveInstallMessages.Remove(activeInstallIdentity);

            if (completion == null || !completion.Success)
            {
                string completionMessage =
                    string.IsNullOrWhiteSpace(completion?.Message)
                        ? "The Git package operation did not complete successfully."
                        : completion.Message;
                // The Package Manager may have recycled its details hierarchy
                // while Git was running. Refresh first, then resolve the current
                // controller so the useful failure remains visible inline.
                RefreshAllEntries();
                ReportInstallErrorForRepository(
                    preferredDetails,
                    repositoryIdentity,
                    activeInstallIdentity,
                    "Could Not Install Git Package",
                    completionMessage);
                return;
            }

            string terminalErrorTitle = string.Empty;
            string terminalErrorMessage = string.Empty;
            try
            {
                PackageManagerSubmoduleSnapshot.Refresh();
                bool reconciled = PackageManagerGitHubPackageProjection.Reconcile(
                    PackageManagerGitHubDiscovery.Current);
                PackageManagerSubmoduleHarmonyPatch.RefreshOpenPackageManagerWindows();
                if (!reconciled)
                {
                    terminalErrorTitle = "Package Manager Refresh Failed";
                    terminalErrorMessage =
                        "The package was installed, but Package Manager could not " +
                        "update its GitHub package list. Use Refresh to retry.";
                }
            }
            catch (Exception exception)
            {
                terminalErrorTitle = "Package Manager Refresh Failed";
                terminalErrorMessage =
                    "The package was installed, but Package Manager could not refresh: " +
                    exception.Message;
            }

            RefreshAllEntries();
            if (!string.IsNullOrEmpty(terminalErrorMessage))
            {
                ReportInstallErrorForRepository(
                    preferredDetails,
                    repositoryIdentity,
                    activeInstallIdentity,
                    terminalErrorTitle,
                    terminalErrorMessage);
                return;
            }

            foreach (PackageManagerGitHubDetails details in
                     FindDetailsForInstall(
                         preferredDetails,
                         repositoryIdentity,
                         activeInstallIdentity))
            {
                details.ShowInstallCompleted(
                    "Git submodule installed. Package Manager is up to date.");
            }
        }

        private static void RefreshAllEntries()
        {
            var entries = new List<NativeActionEntry>(EntriesByToolbar.Values);
            foreach (NativeActionEntry entry in entries)
            {
                if (entry?.Toolbar == null)
                    continue;

                RefreshForToolbar(
                    entry.Toolbar,
                    GetFieldValue(entry.Toolbar, "m_Package"));
            }
        }

        private static string BuildEnabledTooltip(
            PackageManagerGitHubRepository repository,
            string selectedBranch)
        {
            string destination = GitSubmoduleAddService.GetPackagePath(
                repository?.PackageName ?? string.Empty);
            string branch = string.IsNullOrWhiteSpace(selectedBranch)
                ? "the repository default branch"
                : $"branch {selectedBranch.Trim()}";
            return string.IsNullOrWhiteSpace(destination)
                ? $"Install this GitHub package as a Git submodule from {branch}."
                : $"Install this GitHub package as a Git submodule from {branch} at {destination}.";
        }

        private static string BuildDisabledTooltip(string validationError)
        {
            if (!string.IsNullOrWhiteSpace(validationError))
                return validationError.Trim();

            string recoveryWarning = GitOperationService.RecoveryWarning;
            if (!string.IsNullOrWhiteSpace(recoveryWarning))
                return recoveryWarning.Trim();

            string activeLabel = GitOperationService.ActiveLabel;
            if (GitOperationService.IsBusy && !string.IsNullOrWhiteSpace(activeLabel))
                return $"Wait for {activeLabel.Trim()} to finish.";

            return "Wait for current package scans and repository operations to finish.";
        }

        private static bool IsGitHubPage(object toolbar)
        {
            object pageManager = GetFieldValue(toolbar, "m_PageManager");
            object activePage = GetPropertyValue(pageManager, "activePage");
            return string.Equals(
                GetPropertyValue(activePage, "id") as string,
                PackageManagerSubmoduleNativePage.ExtensionPageId,
                StringComparison.Ordinal);
        }

        private static object GetFieldValue(object instance, string fieldName)
        {
            if (instance == null)
                return null;

            for (Type type = instance.GetType(); type != null; type = type.BaseType)
            {
                FieldInfo field = type.GetField(fieldName, AnyInstance);
                if (field != null)
                    return field.GetValue(instance);
            }

            return null;
        }

        private static object GetPropertyValue(object instance, string propertyName)
        {
            if (instance == null)
                return null;

            for (Type type = instance.GetType(); type != null; type = type.BaseType)
            {
                PropertyInfo property = type.GetProperty(propertyName, AnyInstance);
                if (property != null && property.GetIndexParameters().Length == 0)
                    return property.GetValue(instance, null);
            }

            return null;
        }

        private static List<PackageManagerGitHubDetails> FindDetailsForInstall(
            PackageManagerGitHubDetails preferredDetails,
            string repositoryIdentity,
            string installIdentity)
        {
            var matches = new List<PackageManagerGitHubDetails>();
            if (string.IsNullOrWhiteSpace(installIdentity) &&
                string.IsNullOrWhiteSpace(repositoryIdentity))
            {
                return matches;
            }

            AddDetailsIfMatching(
                matches,
                preferredDetails,
                repositoryIdentity,
                installIdentity);

            foreach (NativeActionEntry entry in EntriesByToolbar.Values)
            {
                AddDetailsIfMatching(
                    matches,
                    entry?.Details,
                    repositoryIdentity,
                    installIdentity);
            }

            return matches;
        }

        private static void AddDetailsIfMatching(
            List<PackageManagerGitHubDetails> matches,
            PackageManagerGitHubDetails details,
            string repositoryIdentity,
            string installIdentity)
        {
            if (details == null ||
                details.IsDisposed ||
                matches.Contains(details))
            {
                return;
            }

            bool matchesIdentity = !string.IsNullOrWhiteSpace(installIdentity)
                ? string.Equals(
                    installIdentity,
                    PackageManagerGitHubDetails.GetInstallRepositoryIdentity(
                        details.CurrentRepository),
                    StringComparison.Ordinal)
                : IsDetailsShowingRepository(
                    details,
                    repositoryIdentity);
            if (matchesIdentity)
                matches.Add(details);
        }

        private static bool IsDetailsShowingRepository(
            PackageManagerGitHubDetails details,
            string repositoryIdentity)
        {
            return details != null &&
                   !details.IsDisposed &&
                   string.Equals(
                       repositoryIdentity,
                       PackageManagerGitHubDetails.GetRepositoryIdentity(
                           details.CurrentRepository),
                       StringComparison.Ordinal);
        }

        private static void ReportInstallError(
            PackageManagerGitHubDetails details,
            string title,
            string message)
        {
            string safeTitle = GitHubUtility.SanitizeUiDiagnostic(title);
            string safeMessage = GitHubUtility.SanitizeUiDiagnostic(message);
            if (string.IsNullOrWhiteSpace(safeMessage))
                safeMessage = "The Git package operation could not be completed.";

            string diagnostic = string.IsNullOrWhiteSpace(safeTitle)
                ? safeMessage
                : safeTitle + ": " + safeMessage;
            try
            {
                if (details != null && !details.IsDisposed)
                    details.ShowInstallError(diagnostic);
            }
            catch
            {
                // Package Manager can recycle the details hierarchy while an
                // asynchronous Git callback is completing. The sanitized console
                // diagnostic remains available if inline feedback cannot mount.
            }

            Debug.LogWarning("[Git Submodule Manager] " + diagnostic);
        }

        private static void ReportInstallErrorForRepository(
            PackageManagerGitHubDetails preferredDetails,
            string repositoryIdentity,
            string installIdentity,
            string title,
            string message)
        {
            string safeTitle = GitHubUtility.SanitizeUiDiagnostic(title);
            string safeMessage = GitHubUtility.SanitizeUiDiagnostic(message);
            if (string.IsNullOrWhiteSpace(safeMessage))
                safeMessage = "The Git package operation could not be completed.";

            string diagnostic = string.IsNullOrWhiteSpace(safeTitle)
                ? safeMessage
                : safeTitle + ": " + safeMessage;
            foreach (PackageManagerGitHubDetails details in
                     FindDetailsForInstall(
                         preferredDetails,
                         repositoryIdentity,
                         installIdentity))
            {
                try
                {
                    details.ShowInstallError(diagnostic);
                }
                catch
                {
                    // Another Package Manager refresh can recycle one window
                    // while feedback is being applied to the others.
                }
            }

            Debug.LogWarning("[Git Submodule Manager] " + diagnostic);
        }

        private sealed class NativeActionEntry
        {
            internal NativeActionEntry(
                VisualElement root,
                VisualElement toolbar,
                VisualElement primaryActionsContainer,
                VisualElement detailsLinksContainer,
                PackageManagerGitHubDetails details,
                EventCallback<DetachFromPanelEvent> detachedCallback)
            {
                Root = root;
                Toolbar = toolbar;
                PrimaryActionsContainer = primaryActionsContainer;
                DetailsLinksContainer = detailsLinksContainer;
                Details = details;
                DetachedCallback = detachedCallback;
            }

            internal VisualElement Root { get; }
            internal VisualElement Toolbar { get; }
            internal VisualElement PrimaryActionsContainer { get; }
            internal VisualElement DetailsLinksContainer { get; }
            internal PackageManagerGitHubDetails Details { get; }
            internal EventCallback<DetachFromPanelEvent> DetachedCallback { get; }
        }

        private sealed class ReferenceComparer : IEqualityComparer<object>
        {
            internal static ReferenceComparer Instance { get; } = new();

            public new bool Equals(object left, object right)
            {
                return ReferenceEquals(left, right);
            }

            public int GetHashCode(object instance)
            {
                return instance == null
                    ? 0
                    : System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(instance);
            }
        }
    }
}
