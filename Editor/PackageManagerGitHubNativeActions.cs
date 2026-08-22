using System;
using System.Collections.Generic;
using System.IO;
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
                        current.RemoveDetails.EnsureControlsMounted();
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

                PackageManagerSubmoduleRemoveDetails removeDetails = null;
                if (!PackageManagerSubmoduleRemoveDetails.TryCreate(
                        primaryActions,
                        detailsLinks,
                        info => OnRemoveRequested(
                            toolbar,
                            removeDetails,
                            info),
                        out removeDetails))
                {
                    details.Dispose();
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
                    removeDetails,
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
            entry.RemoveDetails.Dispose();
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
                object primaryVersion =
                    PackageManagerSubmoduleNativePage.GetPrimaryVersion(package);
                bool isInstalledSubmodule =
                    PackageManagerSubmodulePresentation.TryGetPresentation(
                        primaryVersion,
                        out PackageManagerSubmoduleInfo submoduleInfo);

                if (isInstalledSubmodule)
                {
                    entry.Details.Refresh(null);
                    entry.Details.SetInstallState(false, false, string.Empty);
                    entry.RemoveDetails.Refresh(submoduleInfo);
                    string removeValidationError =
                        GitSubmoduleRemoveService.ValidateInput(submoduleInfo);
                    bool removeEnabled =
                        string.IsNullOrWhiteSpace(removeValidationError) &&
                        GitSubmoduleRemoveService.CanStart;
                    string removeTooltip = removeEnabled
                        ? "Remove this installed package through Git so its " +
                          "submodule registration and worktree stay consistent."
                        : string.IsNullOrWhiteSpace(removeValidationError)
                            ? GitSubmoduleRemoveService.BuildUnavailableMessage()
                            : removeValidationError;
                    entry.RemoveDetails.SetRemoveState(
                        removeEnabled,
                        removeTooltip);
                    if (entry.RemoveDetails.IsRemoving && GitOperationService.IsBusy)
                    {
                        entry.RemoveDetails.ShowRemoving(
                            "Removing the Git submodule and refreshing Unity...");
                    }
                    return;
                }

                entry.RemoveDetails.Refresh(null);
                entry.RemoveDetails.SetRemoveState(false, string.Empty);

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
                entry.RemoveDetails.Refresh(null);
                entry.RemoveDetails.SetRemoveState(false, string.Empty);
            }
        }

        /// <summary>
        /// Harmony entry point for Unity's embedded-package Remove action. True
        /// means the request was claimed and Unity's recursive directory delete
        /// must be skipped; actionResult is returned to Unity's PackageAction.
        /// </summary>
        internal static bool TryHandleRemoveCustomAction(
            object packageVersion,
            out bool actionResult)
        {
            actionResult = false;
            if (PackageManagerSubmodulePresentation.TryGetPresentation(
                    packageVersion,
                    out PackageManagerSubmoduleInfo info))
            {
                bool matchedEntry = false;
                bool confirmationAvailable = false;
                foreach (NativeActionEntry entry in EntriesByToolbar.Values)
                {
                    if (entry?.Toolbar == null || entry.RemoveDetails == null)
                        continue;

                    object selectedVersion = GetFieldValue(entry.Toolbar, "m_Version");
                    if (!ReferenceEquals(selectedVersion, packageVersion) &&
                        !SameSubmodule(entry.RemoveDetails.CurrentInfo, info))
                    {
                        continue;
                    }

                    matchedEntry = true;
                    entry.RemoveDetails.Refresh(info);
                    string validationError =
                        GitSubmoduleRemoveService.ValidateInput(info);
                    bool enabled =
                        string.IsNullOrWhiteSpace(validationError) &&
                        GitSubmoduleRemoveService.CanStart;
                    string disabledMessage = string.IsNullOrWhiteSpace(validationError)
                        ? GitSubmoduleRemoveService.BuildUnavailableMessage()
                        : validationError;
                    entry.RemoveDetails.SetRemoveState(
                        enabled,
                        enabled
                            ? "Remove this installed package through Git."
                            : disabledMessage);
                    if (!enabled)
                    {
                        entry.RemoveDetails.ShowError(disabledMessage);
                        continue;
                    }

                    // PackageAction does not expose the toolbar/window that
                    // originated the request, and IPackageVersion instances can
                    // be shared by multiple Package Manager windows. Mirror the
                    // confirmation to every matching native host so the clicked
                    // window can never appear inert because dictionary order
                    // selected another window.
                    entry.RemoveDetails.ShowConfirmation();
                    confirmationAvailable = true;
                }

                if (matchedEntry)
                {
                    actionResult = confirmationAvailable;
                    return true;
                }

                // A proven submodule must never fall through to Unity's raw
                // embedded-directory deletion merely because its visual tree was
                // recycled. Rebuild the window and leave the package intact.
                Debug.LogWarning(
                    "[Git Submodule Manager] Package Manager could not mount the " +
                    "safe submodule removal controls. The package was preserved; " +
                    "refresh Package Manager and retry.");
                PackageManagerSubmoduleHarmonyPatch.RefreshOpenPackageManagerWindows();
                return true;
            }

            // During the initial asynchronous snapshot, conservatively preserve
            // direct Packages/<name> embedded packages. Once ready, an ordinary
            // non-submodule is allowed to use Unity's native removal unchanged.
            if (!PackageManagerSubmoduleSnapshot.IsReady &&
                IsDirectInstalledPackagePath(packageVersion))
            {
                PackageManagerSubmoduleSnapshot.Refresh();
                Debug.LogWarning(
                    "[Git Submodule Manager] Submodule detection is still loading. " +
                    "The package was preserved; retry Remove after Package Manager refreshes.");
                return true;
            }

            return false;
        }

        internal static bool ShouldBlockNativeEmbeddedRemoval(string packageName)
        {
            if (string.IsNullOrWhiteSpace(packageName))
                return false;

            string normalizedName = packageName.Trim();
            if (PackageManagerSubmoduleSnapshot.TryGet(
                    normalizedName,
                    string.Empty,
                    true,
                    out _))
            {
                return true;
            }

            if (PackageManagerSubmoduleSnapshot.IsReady ||
                !GitUtility.IsValidUpmPackageName(normalizedName))
            {
                return false;
            }

            // Fail closed only during the short initial scan. This prevents a
            // lower-level embedded removal from bypassing the interactive
            // action guard before submodule identity is available.
            PackageManagerSubmoduleSnapshot.Refresh();
            return true;
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

        private static void OnRemoveRequested(
            VisualElement toolbar,
            PackageManagerSubmoduleRemoveDetails sourceDetails,
            PackageManagerSubmoduleInfo requestedInfo)
        {
            PackageManagerSubmoduleRemoveDetails feedbackTarget = sourceDetails;
            try
            {
                if (toolbar == null || requestedInfo == null ||
                    !EntriesByToolbar.TryGetValue(
                        toolbar,
                        out NativeActionEntry entry))
                {
                    ReportRemoveError(
                        feedbackTarget,
                        "Package Manager refreshed before the removal request " +
                        "could be handled. Select the package and retry.");
                    return;
                }

                feedbackTarget = entry.RemoveDetails;
                object selectedPackage = GetFieldValue(toolbar, "m_Package");
                object selectedVersion =
                    PackageManagerSubmoduleNativePage.GetPrimaryVersion(
                        selectedPackage);
                if (!PackageManagerSubmodulePresentation.TryGetPresentation(
                        selectedVersion,
                        out PackageManagerSubmoduleInfo selectedInfo) ||
                    !SameSubmodule(requestedInfo, selectedInfo) ||
                    !SameSubmodule(entry.RemoveDetails.CurrentInfo, selectedInfo))
                {
                    ReportRemoveError(
                        feedbackTarget,
                        "The selected package changed before removal could start. " +
                        "Select the installed submodule and retry.");
                    RefreshAllEntries();
                    return;
                }

                if (!GitSubmoduleRemoveService.TryStart(
                        selectedInfo,
                        completion => OnRemoveCompleted(
                            feedbackTarget,
                            selectedInfo,
                            completion),
                        out string startError))
                {
                    ReportRemoveError(
                        feedbackTarget,
                        string.IsNullOrWhiteSpace(startError)
                            ? "The Git submodule removal could not be started."
                            : startError);
                    RefreshAllEntries();
                    return;
                }

                ShowRemovingForSubmodule(selectedInfo);
            }
            catch (Exception exception)
            {
                ReportRemoveError(
                    feedbackTarget,
                    "The Git submodule removal could not be started: " +
                    exception.Message);
                RefreshAllEntries();
            }
        }

        private static void ShowRemovingForSubmodule(
            PackageManagerSubmoduleInfo info)
        {
            if (info == null)
                return;

            foreach (NativeActionEntry entry in EntriesByToolbar.Values)
            {
                PackageManagerSubmoduleRemoveDetails details =
                    entry?.RemoveDetails;
                if (details == null ||
                    !SameSubmodule(details.CurrentInfo, info))
                {
                    continue;
                }

                details.ShowRemoving(
                    $"Removing {info.PackageName} through Git and refreshing Unity...");
            }
        }

        private static void OnRemoveCompleted(
            PackageManagerSubmoduleRemoveDetails preferredDetails,
            PackageManagerSubmoduleInfo info,
            GitSubmoduleRemoveCompletion completion)
        {
            if (completion == null || !completion.Success)
            {
                ReportRemoveErrorForSubmodule(
                    preferredDetails,
                    info,
                    string.IsNullOrWhiteSpace(completion?.Message)
                        ? "The Git submodule was preserved because removal did " +
                          "not complete safely."
                        : completion.Message);
                return;
            }

            try
            {
                PackageManagerSubmoduleSnapshot.Refresh();
            }
            catch (Exception exception)
            {
                Debug.LogWarning(
                    "[Git Submodule Manager] The Git submodule was removed, " +
                    "but the early Package Manager snapshot request failed. " +
                    "Unity's package registration event will retry it: " +
                    GitHubUtility.SanitizeUiDiagnostic(exception.Message));
            }

            string packageName = info?.PackageName ?? "the package";
            ApplyRemoveStateForSubmodule(
                preferredDetails,
                info,
                details => details.ShowCompleted(
                    $"Removed {packageName} through Git. Unity is refreshing " +
                    "Package Manager; review and commit the parent repository changes."));
        }

        private static void ReportRemoveErrorForSubmodule(
            PackageManagerSubmoduleRemoveDetails preferredDetails,
            PackageManagerSubmoduleInfo info,
            string message)
        {
            string safeMessage = GitHubUtility.SanitizeUiDiagnostic(message);
            if (string.IsNullOrWhiteSpace(safeMessage))
                safeMessage = "The Git submodule could not be removed safely.";

            ApplyRemoveStateForSubmodule(
                preferredDetails,
                info,
                details => details.ShowError(safeMessage));
            Debug.LogWarning("[Git Submodule Manager] " + safeMessage);
        }

        private static void ApplyRemoveStateForSubmodule(
            PackageManagerSubmoduleRemoveDetails preferredDetails,
            PackageManagerSubmoduleInfo info,
            Action<PackageManagerSubmoduleRemoveDetails> apply)
        {
            if (apply == null)
                return;

            bool preferredHandled = false;
            foreach (NativeActionEntry entry in EntriesByToolbar.Values)
            {
                PackageManagerSubmoduleRemoveDetails details =
                    entry?.RemoveDetails;
                if (details == null ||
                    !SameSubmodule(details.CurrentInfo, info))
                {
                    continue;
                }

                try
                {
                    apply(details);
                    preferredHandled |= ReferenceEquals(details, preferredDetails);
                }
                catch
                {
                    // Continue updating other Package Manager windows when one
                    // visual tree is recycled during the terminal callback.
                }
            }

            if (preferredDetails == null ||
                preferredHandled ||
                !SameSubmodule(preferredDetails.CurrentInfo, info))
                return;

            try
            {
                apply(preferredDetails);
            }
            catch
            {
                // The details hierarchy was recycled; other matching windows
                // and the sanitized console diagnostic remain available.
            }
        }

        private static void ReportRemoveError(
            PackageManagerSubmoduleRemoveDetails details,
            string message)
        {
            string safeMessage = GitHubUtility.SanitizeUiDiagnostic(message);
            if (string.IsNullOrWhiteSpace(safeMessage))
                safeMessage = "The Git submodule could not be removed safely.";

            try
            {
                details?.ShowError(safeMessage);
            }
            catch
            {
                // The details hierarchy may have been recycled. The console
                // diagnostic remains available and the package stays intact.
            }

            Debug.LogWarning("[Git Submodule Manager] " + safeMessage);
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

            try
            {
                // A verified Git add is already a successful install. Unity can
                // register the embedded package, compile its scripts, and reload
                // the domain after this callback returns. The durable
                // registeredPackages/snapshot hooks reconcile the projection in
                // that new domain, so an immediate projection result must never
                // turn the successful Git operation into a UI error.
                PackageManagerSubmoduleSnapshot.Refresh();
            }
            catch (Exception exception)
            {
                Debug.LogWarning(
                    "[Git Submodule Manager] The Git submodule was installed, " +
                    "but the early Package Manager snapshot request failed. " +
                    "Unity's package registration event will retry it: " +
                    GitHubUtility.SanitizeUiDiagnostic(exception.Message));
            }

            RefreshAllEntries();
            foreach (PackageManagerGitHubDetails details in
                     FindDetailsForInstall(
                         preferredDetails,
                         repositoryIdentity,
                         activeInstallIdentity))
            {
                details.ShowInstallCompleted(
                    "Git submodule installed. Unity is importing and compiling " +
                    "the package; Package Manager will update automatically.");
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

        private static bool SameSubmodule(
            PackageManagerSubmoduleInfo left,
            PackageManagerSubmoduleInfo right)
        {
            return left != null &&
                   right != null &&
                   string.Equals(
                       left.PackageName?.Trim(),
                       right.PackageName?.Trim(),
                       StringComparison.Ordinal) &&
                   string.Equals(
                       GitUtility.NormalizePath(left.PackagePath),
                       GitUtility.NormalizePath(right.PackagePath),
                       StringComparison.Ordinal);
        }

        private static bool IsDirectInstalledPackagePath(object packageVersion)
        {
            if (!PackageManagerSubmodulePresentation.TryGetVersionIdentity(
                    packageVersion,
                    out string packageName,
                    out string localPath,
                    out bool isInstalled) ||
                !isInstalled ||
                !GitUtility.IsValidUpmPackageName(packageName))
            {
                return false;
            }

            string expectedPath = PackageManagerSubmoduleSnapshotData.NormalizeFullPath(
                Path.Combine(
                    GitUtility.ProjectRoot,
                    GitSubmoduleAddService.GetPackagePath(packageName)));
            string actualPath =
                PackageManagerSubmoduleSnapshotData.NormalizeFullPath(localPath);
            StringComparison comparison = Path.DirectorySeparatorChar == '\\'
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            return !string.IsNullOrEmpty(expectedPath) &&
                   string.Equals(expectedPath, actualPath, comparison);
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
                PackageManagerSubmoduleRemoveDetails removeDetails,
                EventCallback<DetachFromPanelEvent> detachedCallback)
            {
                Root = root;
                Toolbar = toolbar;
                PrimaryActionsContainer = primaryActionsContainer;
                DetailsLinksContainer = detailsLinksContainer;
                Details = details;
                RemoveDetails = removeDetails;
                DetachedCallback = detachedCallback;
            }

            internal VisualElement Root { get; }
            internal VisualElement Toolbar { get; }
            internal VisualElement PrimaryActionsContainer { get; }
            internal VisualElement DetailsLinksContainer { get; }
            internal PackageManagerGitHubDetails Details { get; }
            internal PackageManagerSubmoduleRemoveDetails RemoveDetails { get; }
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
