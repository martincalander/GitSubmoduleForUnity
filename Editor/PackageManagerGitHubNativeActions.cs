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
        internal const string PageManagerFieldName = "m_PageManager";
        internal const string PackageDatabaseFieldName = "m_PackageDatabase";
        internal const string PageManagerTypeName =
            "UnityEditor.PackageManager.UI.Internal.IPageManager";
        internal const string PageInterfaceTypeName =
            "UnityEditor.PackageManager.UI.Internal.IPage";
        internal const string PageSelectionTypeName =
            "UnityEditor.PackageManager.UI.Internal.PageSelection";
        internal const string PackageDatabaseTypeName =
            "UnityEditor.PackageManager.UI.Internal.IPackageDatabase";
        internal const string PackageInterfaceTypeName =
            "UnityEditor.PackageManager.UI.Internal.IPackage";
        internal const string PublicPackageInterfaceTypeName =
            "UnityEditor.PackageManager.UI.IPackage";
        private const BindingFlags AnyInstance =
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        private static readonly Dictionary<object, NativeActionEntry> EntriesByRoot =
            new(ReferenceComparer.Instance);
        private static readonly Dictionary<object, NativeActionEntry> EntriesByToolbar =
            new(ReferenceComparer.Instance);
        private static readonly Dictionary<string, string> ActiveInstallMessages =
            new(StringComparer.Ordinal);
        private static SelectionContract supportedSelectionContract;
        private static bool recoveredCompletionPresentationScheduled;

        static PackageManagerGitHubNativeActions()
        {
            PackageManagerReadOnlyGitInstallService.Completed +=
                OnReadOnlyInstallServiceCompleted;
            PackageDependencyInstallPipeline.Changed +=
                OnDependencyInstallPipelineChanged;
            PackageDependencyInstallPipeline.Completed +=
                OnDependencyInstallPipelineCompleted;
        }

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
                        current.ConversionDetails.EnsureControlsMounted();
                        RefreshForToolbar(toolbar, GetFieldValue(toolbar, "m_Package"));
                        TryScheduleRecoveredDependencyInstallCompletion();
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
                        (repository, branch, installMode) =>
                            OnInstallRequested(
                                toolbar,
                                details,
                                repository,
                                branch,
                                installMode),
                        Application.OpenURL,
                        true,
                        out details))
                {
                    return false;
                }
                details.InstallSelectionChanged += () =>
                    RefreshForToolbar(
                        toolbar,
                        GetFieldValue(toolbar, "m_Package"));

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

                PackageManagerPackageConversionDetails conversionDetails = null;
                if (!PackageManagerPackageConversionDetails.TryCreate(
                        primaryActions,
                        detailsLinks,
                        target => OnConversionRequested(
                            toolbar,
                            conversionDetails,
                            target),
                        out conversionDetails))
                {
                    removeDetails.Dispose();
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
                    conversionDetails,
                    detached);
                EntriesByRoot[root] = entry;
                EntriesByToolbar[toolbar] = entry;
                root.RegisterCallback(detached);
                RefreshForToolbar(toolbar, GetFieldValue(toolbar, "m_Package"));
                if (!TryScheduleRecoveredDependencyInstallCompletion() &&
                    PackageManagerReadOnlyGitInstallService.TryConsumeLastCompletion(
                        out ReadOnlyGitPackageInstallCompletion recoveredCompletion))
                {
                    OnReadOnlyInstallServiceCompleted(recoveredCompletion);
                }
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
            entry.ConversionDetails.Dispose();
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

                package = ResolvePackageForRefresh(toolbar, package);

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
                bool isInstalledReadOnlyGit =
                    PackageManagerReadOnlyGitPackage.TryGetInfo(
                        package,
                        out PackageManagerReadOnlyGitInfo readOnlyInfo);

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
                        ? "Uninstall this package through Git so its " +
                          "submodule registration and worktree stay consistent."
                        : string.IsNullOrWhiteSpace(removeValidationError)
                            ? GitSubmoduleRemoveService.BuildUnavailableMessage()
                            : removeValidationError;
                    entry.RemoveDetails.SetRemoveState(
                        removeEnabled,
                        removeTooltip);
                    PackageManagerPackageConversionTarget conversionTarget =
                        BuildConversionTarget(submoduleInfo);
                    entry.ConversionDetails.Refresh(conversionTarget);
                    string conversionError =
                        GitPackageConversionService.ValidateToReadOnly(
                            submoduleInfo);
                    bool conversionEnabled =
                        string.IsNullOrWhiteSpace(conversionError) &&
                        GitPackageConversionService.CanStart;
                    entry.ConversionDetails.SetActionState(
                        conversionTarget,
                        conversionEnabled,
                        conversionEnabled
                            ? "Convert this editable submodule to a normal " +
                              "read-only UPM Git dependency pinned to its current commit."
                            : BuildConversionDisabledTooltip(conversionError));
                    PackageManagerSubmoduleManageMenu.Apply(
                        entry.Toolbar,
                        true,
                        conversionEnabled,
                        conversionEnabled
                            ? "Convert this editable submodule to a normal " +
                              "read-only UPM Git dependency pinned to its current commit."
                            : BuildConversionDisabledTooltip(conversionError),
                        () => BeginConversionAssessment(
                            entry.Toolbar,
                            entry.ConversionDetails,
                            conversionTarget,
                            submoduleInfo),
                        removeEnabled,
                        removeTooltip,
                        () => BeginRemoveAssessment(
                            entry.Toolbar,
                            entry.RemoveDetails,
                            submoduleInfo));
                    if (entry.RemoveDetails.IsRemoving && GitOperationService.IsBusy)
                    {
                        entry.RemoveDetails.ShowRemoving(
                            "Removing the Git submodule and refreshing Unity...");
                    }
                    if (entry.ConversionDetails.IsConverting &&
                        GitOperationService.IsBusy)
                    {
                        entry.ConversionDetails.ShowProgress(
                            conversionTarget,
                            "Converting the submodule to a read-only Git package...");
                    }
                    return;
                }

                PackageManagerSubmoduleManageMenu.Apply(
                    entry.Toolbar,
                    false,
                    false,
                    string.Empty,
                    null,
                    false,
                    string.Empty,
                    null);

                entry.RemoveDetails.Refresh(null);
                entry.RemoveDetails.SetRemoveState(false, string.Empty);

                if (isInstalledReadOnlyGit)
                {
                    entry.Details.Refresh(null);
                    entry.Details.SetInstallState(false, false, string.Empty);
                    PackageManagerPackageConversionTarget conversionTarget =
                        BuildConversionTarget(readOnlyInfo);
                    entry.ConversionDetails.Refresh(conversionTarget);
                    string conversionError =
                        GitPackageConversionService.ValidateToSubmodule(
                            readOnlyInfo);
                    bool conversionEnabled =
                        string.IsNullOrWhiteSpace(conversionError) &&
                        GitPackageConversionService.CanStart;
                    entry.ConversionDetails.SetActionState(
                        conversionTarget,
                        conversionEnabled,
                        conversionEnabled
                            ? "Convert this normal read-only UPM Git dependency " +
                              "to an editable submodule at " +
                              conversionTarget.PackagePath + "."
                            : BuildConversionDisabledTooltip(conversionError));
                    if (entry.ConversionDetails.IsConverting &&
                        GitOperationService.IsBusy)
                    {
                        entry.ConversionDetails.ShowProgress(
                            conversionTarget,
                            "Converting the read-only Git package to a submodule...");
                    }
                    return;
                }

                entry.ConversionDetails.Refresh(null);
                entry.ConversionDetails.SetActionState(false, string.Empty);

                if (!isProjectedRepository)
                {
                    entry.Details.Refresh(null);
                    entry.Details.SetInstallState(false, false, string.Empty);
                    return;
                }

                entry.Details.Refresh(repository);
                if (PackageManagerReadOnlyGitInstallService.IsBusy &&
                    string.Equals(
                        PackageManagerReadOnlyGitInstallService.ActivePackageName,
                        repository.PackageName,
                        StringComparison.Ordinal) &&
                    GitUtility.AreRepositoryUrlsEquivalent(
                        PackageManagerReadOnlyGitInstallService.ActiveRepositoryUrl,
                        repository.Url))
                {
                    entry.Details.RestoreInstallMode(
                        PackageManagerGitInstallMode.ReadOnlyPackage);
                }
                PackageManagerGitInstallMode selectedInstallMode =
                    entry.Details.SelectedInstallMode;
                string selectedBranch = entry.Details.SelectedBranch;
                PackageDependencyInstallPipelineSnapshot pipeline =
                    PackageDependencyInstallPipeline.Current;
                if (MatchesPipelineInstall(
                        repository,
                        selectedBranch,
                        selectedInstallMode,
                        pipeline))
                {
                    string pipelineMessage = string.IsNullOrWhiteSpace(
                        pipeline.Message)
                        ? "Installing this package and its missing dependencies..."
                        : pipeline.Message;
                    entry.Details.ShowInstalling(pipelineMessage);
                    entry.Details.SetInstallState(
                        true,
                        false,
                        pipelineMessage);
                    return;
                }

                string installRepositoryIdentity =
                    BuildActiveInstallIdentity(
                        repository,
                        selectedBranch,
                        selectedInstallMode);
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

                if (selectedInstallMode ==
                        PackageManagerGitInstallMode.ReadOnlyPackage &&
                    PackageManagerReadOnlyGitInstallService.IsBusy &&
                    string.Equals(
                        PackageManagerReadOnlyGitInstallService.ActivePackageName,
                        repository.PackageName,
                        StringComparison.Ordinal) &&
                    GitUtility.AreRepositoryUrlsEquivalent(
                        PackageManagerReadOnlyGitInstallService.ActiveRepositoryUrl,
                        repository.Url))
                {
                    string activeMessage =
                        "Unity Package Manager is installing this read-only Git package...";
                    entry.Details.ShowInstalling(activeMessage);
                    entry.Details.SetInstallState(true, false, activeMessage);
                    return;
                }

                if (entry.Details.IsInstalling)
                {
                    if (GitOperationService.IsBusy ||
                        PackageManagerReadOnlyGitInstallService.IsBusy)
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

                string gitSubmoduleValidationError =
                    GitSubmoduleAddService.ValidateInput(
                        repository.Url,
                        repository.PackageName,
                        selectedBranch);
                string readOnlyPackageValidationError =
                    PackageManagerReadOnlyGitInstallService.ValidateInput(
                        repository.Url,
                        selectedBranch,
                        repository.PackageName);
                bool canStart = CanStartDependencyInstallPipeline();
                bool gitSubmoduleEnabled =
                    string.IsNullOrWhiteSpace(gitSubmoduleValidationError) &&
                    canStart;
                bool readOnlyPackageEnabled =
                    string.IsNullOrWhiteSpace(readOnlyPackageValidationError) &&
                    canStart;
                string gitSubmoduleTooltip = gitSubmoduleEnabled
                    ? BuildEnabledTooltip(
                        repository,
                        selectedBranch,
                        PackageManagerGitInstallMode.GitSubmodule)
                    : BuildDisabledTooltip(gitSubmoduleValidationError);
                string readOnlyPackageTooltip = readOnlyPackageEnabled
                    ? BuildEnabledTooltip(
                        repository,
                        selectedBranch,
                        PackageManagerGitInstallMode.ReadOnlyPackage)
                    : BuildDisabledTooltip(readOnlyPackageValidationError);
                entry.Details.SetInstallState(
                    true,
                    gitSubmoduleEnabled,
                    gitSubmoduleTooltip,
                    readOnlyPackageEnabled,
                    readOnlyPackageTooltip);
            }
            catch
            {
                entry.Details.Refresh(null);
                entry.Details.SetInstallState(false, false, string.Empty);
                entry.RemoveDetails.Refresh(null);
                entry.RemoveDetails.SetRemoveState(false, string.Empty);
                entry.ConversionDetails.Refresh(null);
                entry.ConversionDetails.SetActionState(false, string.Empty);
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
                VisualElement assessmentToolbar = null;
                PackageManagerSubmoduleRemoveDetails assessmentDetails = null;
                foreach (NativeActionEntry entry in EntriesByToolbar.Values)
                {
                    if (entry?.Toolbar == null || entry.RemoveDetails == null)
                        continue;

                    object selectedVersion =
                        TryGetAuthoritativeSelectedPackage(
                            entry.Toolbar,
                            out object selectedPackage)
                            ? PackageManagerSubmoduleNativePage.GetPrimaryVersion(
                                selectedPackage)
                            : null;
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
                            ? "Uninstall this installed package through Git."
                            : disabledMessage);
                    if (!enabled)
                    {
                        entry.RemoveDetails.ShowError(disabledMessage);
                        continue;
                    }

                    assessmentToolbar ??= entry.Toolbar;
                    assessmentDetails ??= entry.RemoveDetails;
                }

                if (matchedEntry)
                {
                    // IPackageVersion instances can be shared by Package Manager
                    // windows. Start one read-only assessment, then mirror its
                    // exact confirmation state to every matching details host.
                    actionResult = assessmentToolbar != null &&
                                   BeginRemoveAssessment(
                                       assessmentToolbar,
                                       assessmentDetails,
                                       info);
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
                       detailsLinksProperty.PropertyType == linksType &&
                       PackageManagerSubmoduleManageMenu.IsSupportedContract();
            }
            catch
            {
                return false;
            }
        }

        internal static bool HasSupportedSelectionContract()
        {
            Type toolbarType = PackageManagerSubmoduleHarmonyPatch.FindLoadedType(
                PackageToolbarTypeName);
            return TryGetSelectionContract(toolbarType, out _);
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

        private static bool BeginRemoveAssessment(
            VisualElement toolbar,
            PackageManagerSubmoduleRemoveDetails preferredDetails,
            PackageManagerSubmoduleInfo requestedInfo)
        {
            bool hasLivePackage = TryGetAuthoritativeSelectedPackage(
                toolbar,
                out object livePackage);
            object liveVersion =
                PackageManagerSubmoduleNativePage.GetPrimaryVersion(livePackage);
            if (toolbar == null ||
                requestedInfo == null ||
                !hasLivePackage ||
                !PackageManagerSubmodulePresentation.TryGetPresentation(
                    liveVersion,
                    out PackageManagerSubmoduleInfo liveInfo) ||
                !SameSubmodule(liveInfo, requestedInfo) ||
                !EntriesByToolbar.TryGetValue(
                    toolbar,
                    out NativeActionEntry entry) ||
                !SameSubmodule(entry.RemoveDetails.CurrentInfo, requestedInfo))
            {
                ReportRemoveError(
                    preferredDetails,
                    "The selected package changed before it could be inspected. " +
                    "Select the installed submodule and retry.");
                return false;
            }

            string validationError =
                GitSubmoduleRemoveService.ValidateInput(requestedInfo);
            if (!string.IsNullOrWhiteSpace(validationError) ||
                !GitSubmoduleRemoveService.CanStart)
            {
                ReportRemoveErrorForSubmodule(
                    preferredDetails,
                    requestedInfo,
                    string.IsNullOrWhiteSpace(validationError)
                        ? GitSubmoduleRemoveService.BuildUnavailableMessage()
                        : validationError);
                return false;
            }

            ApplyRemoveStateForSubmodule(
                preferredDetails,
                requestedInfo,
                details => details.ShowInspecting(
                    $"Inspecting {requestedInfo.PackageName} for local work..."));
            if (GitSubmoduleRemoveService.TryStartAssessment(
                    requestedInfo,
                    completion => OnRemoveAssessmentCompleted(
                        toolbar,
                        preferredDetails,
                        requestedInfo,
                        completion),
                    out string startError))
            {
                return true;
            }

            ReportRemoveErrorForSubmodule(
                preferredDetails,
                requestedInfo,
                string.IsNullOrWhiteSpace(startError)
                    ? "The Git submodule could not be inspected safely."
                    : startError);
            RefreshAllEntries();
            return false;
        }

        private static void OnRemoveAssessmentCompleted(
            VisualElement toolbar,
            PackageManagerSubmoduleRemoveDetails preferredDetails,
            PackageManagerSubmoduleInfo info,
            GitSubmoduleRemovalAssessmentCompletion completion)
        {
            if (completion == null ||
                !completion.Success ||
                completion.Assessment == null)
            {
                ReportRemoveErrorForSubmodule(
                    preferredDetails,
                    info,
                    string.IsNullOrWhiteSpace(completion?.Message)
                        ? "The Git submodule could not be inspected safely."
                        : completion.Message);
                RefreshAllEntries();
                return;
            }

            SubmoduleRemovalAssessment assessment = completion.Assessment;
            EditorApplication.delayCall += () =>
            {
                if (toolbar == null ||
                    !EntriesByToolbar.TryGetValue(
                        toolbar,
                        out NativeActionEntry entry) ||
                    !SameSubmodule(entry.RemoveDetails.CurrentInfo, info))
                {
                    ReportRemoveErrorForSubmodule(
                        preferredDetails,
                        info,
                        "The selected package changed while its Git state was " +
                        "being inspected. Select it again and retry.");
                    return;
                }

                if (!TryGetAuthoritativeSelectedPackage(
                        toolbar,
                        out object selectedPackage))
                {
                    ReportRemoveErrorForSubmodule(
                        preferredDetails,
                        info,
                        "The selected package could not be verified after its " +
                        "Git state was inspected. Select it again and retry.");
                    return;
                }

                object selectedVersion =
                    PackageManagerSubmoduleNativePage.GetPrimaryVersion(
                        selectedPackage);
                if (!PackageManagerSubmodulePresentation.TryGetPresentation(
                        selectedVersion,
                        out PackageManagerSubmoduleInfo selectedInfo) ||
                    !SameSubmodule(selectedInfo, info))
                {
                    ReportRemoveErrorForSubmodule(
                        preferredDetails,
                        info,
                        "The selected package changed while its Git state was " +
                        "being inspected. Select it again and retry.");
                    return;
                }

                PackageManagerSubmoduleConfirmationDecision decision =
                    PackageManagerSubmoduleConfirmationPolicy.Evaluate(
                        PackageManagerSubmoduleDestructiveAction.Uninstall,
                        info.PackageName,
                        info.PackagePath,
                        assessment,
                        GitSubmoduleManagerUserSettings.Instance
                            .SuppressRoutineSubmoduleRemovalConfirmations);
                if (decision.IsBlocked)
                {
                    ReportRemoveErrorForSubmodule(
                        preferredDetails,
                        info,
                        decision.Message);
                    return;
                }

                bool accepted = decision.CanProceedWithoutPrompt ||
                                (!Application.isBatchMode &&
                                 EditorUtility.DisplayDialog(
                                     decision.Title,
                                     decision.Message,
                                     decision.AcceptText,
                                     decision.CancelText));
                if (!accepted)
                {
                    ApplyRemoveStateForSubmodule(
                        preferredDetails,
                        info,
                        details => details.CancelInspection());
                    return;
                }

                if (!entry.RemoveDetails.TriggerAssessedRemoval(
                        assessment,
                        decision.DiscardLocalWorkIfAccepted))
                {
                    ReportRemoveErrorForSubmodule(
                        preferredDetails,
                        info,
                        "The inspected package state could not be bound to the " +
                        "uninstall action. Select it again and retry.");
                }
            };
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
                if (!TryGetAuthoritativeSelectedPackage(
                        toolbar,
                        out object selectedPackage))
                {
                    ReportRemoveError(
                        feedbackTarget,
                        "The selected package could not be verified before " +
                        "removal. Select the installed submodule and retry.");
                    RefreshAllEntries();
                    return;
                }

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
                        feedbackTarget.ConfirmedAssessment,
                        feedbackTarget.DiscardLocalWork,
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

        private static bool BeginConversionAssessment(
            VisualElement toolbar,
            PackageManagerPackageConversionDetails preferredDetails,
            PackageManagerPackageConversionTarget requestedTarget,
            PackageManagerSubmoduleInfo requestedInfo)
        {
            bool hasLivePackage = TryGetAuthoritativeSelectedPackage(
                toolbar,
                out object livePackage);
            object liveVersion =
                PackageManagerSubmoduleNativePage.GetPrimaryVersion(livePackage);
            if (toolbar == null ||
                requestedTarget == null ||
                requestedInfo == null ||
                !hasLivePackage ||
                requestedTarget.Direction !=
                GitPackageConversionDirection.SubmoduleToReadOnly ||
                !PackageManagerSubmodulePresentation.TryGetPresentation(
                    liveVersion,
                    out PackageManagerSubmoduleInfo liveInfo) ||
                !SameSubmodule(liveInfo, requestedInfo) ||
                !SameConversionTarget(
                    BuildConversionTarget(liveInfo),
                    requestedTarget) ||
                !EntriesByToolbar.TryGetValue(
                    toolbar,
                    out NativeActionEntry entry) ||
                !SameConversionTarget(
                    entry.ConversionDetails.CurrentTarget,
                    requestedTarget) ||
                !SameSubmodule(entry.RemoveDetails.CurrentInfo, requestedInfo))
            {
                ReportConversionError(
                    preferredDetails,
                    requestedTarget,
                    "The selected package changed before it could be inspected. " +
                    "Select the installed submodule and retry.");
                return false;
            }

            string validationError =
                GitPackageConversionService.ValidateToReadOnly(requestedInfo);
            if (!string.IsNullOrWhiteSpace(validationError) ||
                !GitPackageConversionService.CanStart)
            {
                ReportConversionError(
                    preferredDetails,
                    requestedTarget,
                    string.IsNullOrWhiteSpace(validationError)
                        ? GitPackageConversionService.BuildUnavailableMessage()
                        : validationError);
                return false;
            }

            ApplyConversionState(
                requestedTarget,
                details => details.ShowInspecting(
                    requestedTarget,
                    $"Inspecting {requestedTarget.PackageName} for local work..."));
            if (GitSubmoduleRemoveService.TryStartAssessment(
                    requestedInfo,
                    completion => OnConversionAssessmentCompleted(
                        toolbar,
                        preferredDetails,
                        requestedTarget,
                        completion),
                    out string startError))
            {
                return true;
            }

            ReportConversionError(
                preferredDetails,
                requestedTarget,
                string.IsNullOrWhiteSpace(startError)
                    ? "The submodule could not be inspected safely before conversion."
                    : startError);
            RefreshAllEntries();
            return false;
        }

        private static void OnConversionAssessmentCompleted(
            VisualElement toolbar,
            PackageManagerPackageConversionDetails preferredDetails,
            PackageManagerPackageConversionTarget target,
            GitSubmoduleRemovalAssessmentCompletion completion)
        {
            if (completion == null ||
                !completion.Success ||
                completion.Assessment == null)
            {
                ReportConversionError(
                    preferredDetails,
                    target,
                    string.IsNullOrWhiteSpace(completion?.Message)
                        ? "The submodule could not be inspected safely before conversion."
                        : completion.Message);
                RefreshAllEntries();
                return;
            }

            SubmoduleRemovalAssessment assessment = completion.Assessment;
            EditorApplication.delayCall += () =>
            {
                if (toolbar == null ||
                    !EntriesByToolbar.TryGetValue(
                        toolbar,
                        out NativeActionEntry entry) ||
                    !SameConversionTarget(
                        entry.ConversionDetails.CurrentTarget,
                        target))
                {
                    ReportConversionError(
                        preferredDetails,
                        target,
                        "The selected package changed while its Git state was " +
                        "being inspected. Select it again and retry.");
                    return;
                }

                if (!TryGetAuthoritativeSelectedPackage(
                        toolbar,
                        out object selectedPackage))
                {
                    ReportConversionError(
                        preferredDetails,
                        target,
                        "The selected package could not be verified after its " +
                        "Git state was inspected. Select it again and retry.");
                    return;
                }

                object selectedVersion =
                    PackageManagerSubmoduleNativePage.GetPrimaryVersion(
                        selectedPackage);
                if (!PackageManagerSubmodulePresentation.TryGetPresentation(
                        selectedVersion,
                        out PackageManagerSubmoduleInfo selectedInfo) ||
                    !SameConversionTarget(
                        BuildConversionTarget(selectedInfo),
                        target))
                {
                    ReportConversionError(
                        preferredDetails,
                        target,
                        "The selected package changed while its Git state was " +
                        "being inspected. Select it again and retry.");
                    return;
                }

                PackageManagerSubmoduleConfirmationDecision decision =
                    PackageManagerSubmoduleConfirmationPolicy.Evaluate(
                        PackageManagerSubmoduleDestructiveAction
                            .ConvertToReadOnly,
                        target.PackageName,
                        target.PackagePath,
                        assessment,
                        GitSubmoduleManagerUserSettings.Instance
                            .SuppressRoutineSubmoduleRemovalConfirmations);
                if (decision.IsBlocked)
                {
                    ReportConversionError(
                        preferredDetails,
                        target,
                        decision.Message);
                    return;
                }

                bool accepted = decision.CanProceedWithoutPrompt ||
                                (!Application.isBatchMode &&
                                 EditorUtility.DisplayDialog(
                                     decision.Title,
                                     decision.Message,
                                     decision.AcceptText,
                                     decision.CancelText));
                if (!accepted)
                {
                    ApplyConversionState(
                        target,
                        details => details.CancelInspection(target));
                    return;
                }

                if (!entry.ConversionDetails.TriggerAssessedConversion(
                        target,
                        assessment,
                        decision.DiscardLocalWorkIfAccepted))
                {
                    ReportConversionError(
                        preferredDetails,
                        target,
                        "The inspected package state could not be bound to the " +
                        "conversion action. Select it again and retry.");
                }
            };
        }

        private static void OnConversionRequested(
            VisualElement toolbar,
            PackageManagerPackageConversionDetails sourceDetails,
            PackageManagerPackageConversionTarget requestedTarget)
        {
            PackageManagerPackageConversionDetails feedbackTarget = sourceDetails;
            try
            {
                if (toolbar == null || requestedTarget == null ||
                    !EntriesByToolbar.TryGetValue(
                        toolbar,
                        out NativeActionEntry entry))
                {
                    ReportConversionError(
                        feedbackTarget,
                        requestedTarget,
                        "Package Manager refreshed before conversion could start. " +
                        "Select the package and retry.");
                    return;
                }

                feedbackTarget = entry.ConversionDetails;
                if (!SameConversionTarget(
                        requestedTarget,
                        feedbackTarget.CurrentTarget))
                {
                    ReportConversionError(
                        feedbackTarget,
                        requestedTarget,
                        "The selected package changed before conversion could start. " +
                        "Select it again and retry.");
                    RefreshAllEntries();
                    return;
                }

                if (!TryGetAuthoritativeSelectedPackage(
                        toolbar,
                        out object selectedPackage))
                {
                    ReportConversionError(
                        feedbackTarget,
                        requestedTarget,
                        "The selected package could not be verified before " +
                        "conversion. Select it again and retry.");
                    RefreshAllEntries();
                    return;
                }

                bool started;
                string startError;
                if (requestedTarget.Direction ==
                    GitPackageConversionDirection.ReadOnlyToSubmodule)
                {
                    if (!PackageManagerReadOnlyGitPackage.TryGetInfo(
                            selectedPackage,
                            out PackageManagerReadOnlyGitInfo readOnlyInfo) ||
                        !SameConversionTarget(
                            requestedTarget,
                            BuildConversionTarget(readOnlyInfo)))
                    {
                        ReportConversionError(
                            feedbackTarget,
                            requestedTarget,
                            "The selected read-only Git dependency no longer " +
                            "matches the confirmed package. Refresh and retry.");
                        RefreshAllEntries();
                        return;
                    }

                    started = GitPackageConversionService.TryStartToSubmodule(
                        readOnlyInfo,
                        completion => OnConversionCompleted(
                            requestedTarget,
                            completion),
                        out startError);
                }
                else
                {
                    object selectedVersion =
                        PackageManagerSubmoduleNativePage.GetPrimaryVersion(
                            selectedPackage);
                    if (!PackageManagerSubmodulePresentation.TryGetPresentation(
                            selectedVersion,
                            out PackageManagerSubmoduleInfo submoduleInfo) ||
                        !SameConversionTarget(
                            requestedTarget,
                            BuildConversionTarget(submoduleInfo)))
                    {
                        ReportConversionError(
                            feedbackTarget,
                            requestedTarget,
                            "The selected submodule no longer matches the " +
                            "confirmed package. Refresh and retry.");
                        RefreshAllEntries();
                        return;
                    }

                    started = GitPackageConversionService.TryStartToReadOnly(
                        submoduleInfo,
                        feedbackTarget.ConfirmedAssessment,
                        feedbackTarget.DiscardLocalWork,
                        completion => OnConversionCompleted(
                            requestedTarget,
                            completion),
                        out startError);
                }

                if (!started)
                {
                    ReportConversionError(
                        feedbackTarget,
                        requestedTarget,
                        string.IsNullOrWhiteSpace(startError)
                            ? "The package conversion could not be started safely."
                            : startError);
                    RefreshAllEntries();
                    return;
                }

                ApplyConversionState(
                    requestedTarget,
                    details => details.ShowProgress(
                        requestedTarget,
                        requestedTarget.Direction ==
                        GitPackageConversionDirection.ReadOnlyToSubmodule
                            ? "Creating and verifying the target submodule before " +
                              "removing the manifest dependency..."
                            : "Recording the read-only dependency before safely " +
                              "removing the submodule..."));
            }
            catch (Exception exception)
            {
                ReportConversionError(
                    feedbackTarget,
                    requestedTarget,
                    "The package conversion could not be started: " +
                    exception.Message);
                RefreshAllEntries();
            }
        }

        private static void OnConversionCompleted(
            PackageManagerPackageConversionTarget target,
            GitPackageConversionCompletion completion)
        {
            if (completion == null || !completion.Success)
            {
                ReportConversionError(
                    null,
                    target,
                    string.IsNullOrWhiteSpace(completion?.Message)
                        ? "The conversion did not complete safely; the original " +
                          "package form was preserved where recovery was verified."
                        : completion.Message);
                RefreshAllEntries();
                return;
            }

            ApplyConversionState(
                target,
                details => details.ShowCompleted(
                    target,
                    completion.Message +
                    " Unity is refreshing Package Manager; review and commit " +
                    "the parent repository changes."));
            PackageManagerSubmoduleSnapshot.Refresh();
            RefreshAllEntries();
        }

        private static void ApplyConversionState(
            PackageManagerPackageConversionTarget target,
            Action<PackageManagerPackageConversionDetails> apply)
        {
            if (target == null || apply == null)
                return;

            foreach (NativeActionEntry entry in EntriesByToolbar.Values)
            {
                PackageManagerPackageConversionDetails details =
                    entry?.ConversionDetails;
                if (details == null ||
                    !SameConversionTarget(details.CurrentTarget, target))
                {
                    continue;
                }

                try
                {
                    apply(details);
                }
                catch
                {
                    // Continue updating other Package Manager windows if one
                    // details hierarchy was recycled during completion.
                }
            }
        }

        private static void ReportConversionError(
            PackageManagerPackageConversionDetails preferredDetails,
            PackageManagerPackageConversionTarget target,
            string message)
        {
            string safeMessage = GitHubUtility.SanitizeUiDiagnostic(message);
            if (string.IsNullOrWhiteSpace(safeMessage))
                safeMessage = "The package could not be converted safely.";

            bool handledPreferred = false;
            foreach (NativeActionEntry entry in EntriesByToolbar.Values)
            {
                PackageManagerPackageConversionDetails details =
                    entry?.ConversionDetails;
                if (details == null ||
                    !SameConversionTarget(details.CurrentTarget, target))
                {
                    continue;
                }

                try
                {
                    details.ShowError(target, safeMessage);
                    handledPreferred |= ReferenceEquals(details, preferredDetails);
                }
                catch
                {
                    // The sanitized console diagnostic remains available.
                }
            }

            if (!handledPreferred && preferredDetails != null &&
                SameConversionTarget(preferredDetails.CurrentTarget, target))
            {
                try
                {
                    preferredDetails.ShowError(target, safeMessage);
                }
                catch
                {
                    // The visual tree may already have been recycled.
                }
            }

            Debug.LogWarning("[Git Submodule Manager] " + safeMessage);
        }

        private static void OnInstallRequested(
            VisualElement toolbar,
            PackageManagerGitHubDetails sourceDetails,
            PackageManagerGitHubRepository repository,
            string selectedBranch,
            PackageManagerGitInstallMode installMode)
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

                if (!TryGetAuthoritativeSelectedPackage(
                        toolbar,
                        out object selectedPackage))
                {
                    ReportInstallError(
                        feedbackTarget,
                        "Cannot Install Git Package",
                        "The selected repository could not be verified. " +
                        "Select it again in Sources > GitHub and retry.");
                    RefreshAllEntries();
                    return;
                }

                bool resolvedProjectedRepository =
                    PackageManagerGitHubPackageProjection.TryGetRepository(
                        selectedPackage,
                        out PackageManagerGitHubRepository projectedRepository);
                string requestedSelectionIdentity =
                    PackageManagerGitHubDetails.GetInstallSelectionIdentity(
                        repository,
                        selectedBranch,
                        installMode);
                string projectedSelectionIdentity =
                    PackageManagerGitHubDetails.GetInstallSelectionIdentity(
                        projectedRepository,
                        entry.Details.SelectedBranch,
                        entry.Details.SelectedInstallMode);
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
                string validationError = installMode ==
                                         PackageManagerGitInstallMode.ReadOnlyPackage
                    ? PackageManagerReadOnlyGitInstallService.ValidateInput(
                        repository.Url,
                        branch,
                        repository.PackageName)
                    : GitSubmoduleAddService.ValidateInput(
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

                bool canStart = CanStartDependencyInstallPipeline();
                if (!canStart)
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
                    BuildActiveInstallIdentity(repository, branch, installMode);
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

                string installMessage = feedbackTarget?.InstallFeedback?.text;
                if (string.IsNullOrWhiteSpace(installMessage))
                {
                    installMessage = "Installing " + repository.PackageName +
                                     (installMode ==
                                      PackageManagerGitInstallMode.ReadOnlyPackage
                                         ? " as a read-only Git package..."
                                         : " as a Git submodule...");
                }

                ActiveInstallMessages[activeInstallIdentity] = installMessage;
                bool started = PackageDependencyInstallPipeline.TryStart(
                    repository.Url,
                    branch,
                    repository.PackageName,
                    installMode,
                    null,
                    null,
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

        private static void OnDependencyInstallPipelineChanged(
            PackageDependencyInstallPipelineSnapshot snapshot)
        {
            if (snapshot?.IsBusy == true)
            {
                foreach (NativeActionEntry entry in EntriesByToolbar.Values)
                {
                    PackageManagerGitHubRepository repository =
                        entry?.Details?.CurrentRepository;
                    if (repository == null ||
                        !MatchesPipelineInstall(
                            repository,
                            entry.Details.SelectedBranch,
                            entry.Details.SelectedInstallMode,
                            snapshot))
                    {
                        continue;
                    }

                    string identity = BuildActiveInstallIdentity(
                        repository,
                        snapshot.Branch,
                        snapshot.InstallMode);
                    if (!string.IsNullOrEmpty(identity))
                    {
                        ActiveInstallMessages[identity] =
                            string.IsNullOrWhiteSpace(snapshot.Message)
                                ? "Installing this package and its missing dependencies..."
                                : snapshot.Message;
                    }
                }
            }

            RefreshAllEntries();
        }

        private static void OnDependencyInstallPipelineCompleted(
            PackageDependencyInstallPipelineCompletion completion)
        {
            bool displayed = PresentDependencyInstallPipelineCompletion(
                completion,
                false);
            bool retainedCompletionAvailable =
                PackageDependencyInstallPipeline.TryGetLastCompletion(out _);
            if (ShouldScheduleRecoveredCompletion(
                    completion,
                    displayed,
                    retainedCompletionAvailable))
            {
                TryScheduleRecoveredDependencyInstallCompletion();
            }
        }

        internal static bool ShouldScheduleRecoveredCompletion(
            PackageDependencyInstallPipelineCompletion completion,
            bool alreadyDisplayed,
            bool retainedCompletionAvailable = false)
        {
            return !alreadyDisplayed &&
                   completion != null &&
                   (completion.RecoveredAfterReload ||
                    retainedCompletionAvailable);
        }

        private static bool TryScheduleRecoveredDependencyInstallCompletion()
        {
            if (!PackageDependencyInstallPipeline.TryGetLastCompletion(out _))
            {
                return false;
            }

            if (!recoveredCompletionPresentationScheduled)
            {
                recoveredCompletionPresentationScheduled = true;
                EditorApplication.delayCall +=
                    PresentRecoveredDependencyInstallCompletion;
            }

            return true;
        }

        private static void PresentRecoveredDependencyInstallCompletion()
        {
            EditorApplication.delayCall -=
                PresentRecoveredDependencyInstallCompletion;
            recoveredCompletionPresentationScheduled = false;
            if (!PackageDependencyInstallPipeline.TryGetLastCompletion(
                    out PackageDependencyInstallPipelineCompletion completion))
            {
                return;
            }

            PresentDependencyInstallPipelineCompletion(completion, true);
        }

        private static bool PresentDependencyInstallPipelineCompletion(
            PackageDependencyInstallPipelineCompletion completion,
            bool allowRecoveredDialog)
        {
            if (completion == null)
                return false;

            ActiveInstallMessages.Clear();
            if (completion.Success &&
                completion.InstallMode ==
                    PackageManagerGitInstallMode.GitSubmodule)
            {
                try
                {
                    PackageManagerSubmoduleSnapshot.Refresh();
                }
                catch (Exception exception)
                {
                    Debug.LogWarning(
                        "[Git Submodule Manager] Installation succeeded, but " +
                        "the early Package Manager snapshot refresh failed. " +
                        "Unity's registered-packages event will retry it: " +
                        GitHubUtility.SanitizeUiDiagnostic(exception.Message));
                }
            }

            RefreshAllEntries();
            var matchingDetails = new List<PackageManagerGitHubDetails>();
            foreach (NativeActionEntry entry in EntriesByToolbar.Values)
            {
                PackageManagerGitHubDetails details = entry?.Details;
                PackageManagerGitHubRepository repository =
                    details?.CurrentRepository;
                if (details == null || details.IsDisposed ||
                    !MatchesDependencyInstallCompletion(
                        repository,
                        completion))
                {
                    continue;
                }

                matchingDetails.Add(details);
            }

            if (!completion.Success)
            {
                string failureMessage =
                    string.IsNullOrWhiteSpace(completion.Message)
                        ? "The dependency-aware Git package operation did not complete successfully."
                        : completion.Message;
                foreach (PackageManagerGitHubDetails details in matchingDetails)
                    details.ShowInstallError(failureMessage);
            }
            else
            {
                string completionMessage =
                    string.IsNullOrWhiteSpace(completion.Message)
                        ? "Package and missing dependencies installed successfully."
                        : completion.Message;
                foreach (PackageManagerGitHubDetails details in matchingDetails)
                    details.ShowInstallCompleted(completionMessage);
            }

            bool displayed = matchingDetails.Count > 0;
            if (!displayed && allowRecoveredDialog)
            {
                displayed = PackageDependencyInstallPipeline
                    .TryPresentRecoveredCompletion(completion);
            }

            bool shouldLogFailure = displayed ||
                                    (!allowRecoveredDialog &&
                                     !completion.RecoveredAfterReload);
            if (!completion.Success && !completion.Cancelled &&
                shouldLogFailure)
            {
                string failureMessage =
                    string.IsNullOrWhiteSpace(completion.Message)
                        ? "The dependency-aware Git package operation did not complete successfully."
                        : completion.Message;
                Debug.LogWarning(
                    "[Git Submodule Manager] " +
                    GitHubUtility.SanitizeUiDiagnostic(failureMessage));
            }

            if (displayed)
                PackageDependencyInstallPipeline.TryConsumeLastCompletion(completion);
            return displayed;
        }

        internal static bool MatchesDependencyInstallCompletion(
            PackageManagerGitHubRepository repository,
            PackageDependencyInstallPipelineCompletion completion)
        {
            return repository != null && completion != null &&
                   string.Equals(
                       repository.PackageName,
                       completion.PackageName,
                       StringComparison.Ordinal) &&
                   (string.IsNullOrWhiteSpace(completion.RepositoryUrl) ||
                    GitUtility.AreRepositoryUrlsEquivalent(
                        repository.Url,
                        completion.RepositoryUrl));
        }

        private static void OnReadOnlyInstallServiceCompleted(
            ReadOnlyGitPackageInstallCompletion completion)
        {
            if (completion == null)
                return;

            if (!ShouldPresentReadOnlyCompletionAsStandalone(
                    completion,
                    PackageDependencyInstallPipeline.IsBusy,
                    PackageDependencyInstallCoordinator.IsBusy))
            {
                return;
            }

            RemoveActiveReadOnlyInstallMessages(completion.PackageName);

            // Clear the reload-safe terminal record after the live event has
            // been observed. If no Package Manager window exists, the record is
            // consumed when a window is mounted later.
            PackageManagerReadOnlyGitInstallService.TryConsumeLastCompletion(out _);

            var matchingDetails = new List<PackageManagerGitHubDetails>();
            foreach (NativeActionEntry entry in EntriesByToolbar.Values)
            {
                PackageManagerGitHubDetails details = entry?.Details;
                PackageManagerGitHubRepository repository =
                    details?.CurrentRepository;
                if (details == null || details.IsDisposed || repository == null ||
                    !string.Equals(
                        repository.PackageName,
                        completion.PackageName,
                        StringComparison.Ordinal))
                {
                    continue;
                }

                matchingDetails.Add(details);
                ActiveInstallMessages.Remove(
                    BuildActiveInstallIdentity(
                        repository,
                        details.SelectedBranch,
                        PackageManagerGitInstallMode.ReadOnlyPackage));
            }

            if (completion.Success)
            {
                foreach (PackageManagerGitHubDetails details in matchingDetails)
                {
                    details.ShowInstallCompleted(
                        "Read-only Git package installed through Unity Package " +
                        "Manager. Unity will update the package list automatically.");
                }
            }
            else
            {
                string message = string.IsNullOrWhiteSpace(completion.Message)
                    ? "Unity Package Manager could not install the read-only Git package."
                    : completion.Message;
                foreach (PackageManagerGitHubDetails details in matchingDetails)
                    details.ShowInstallError(message);
                Debug.LogWarning(
                    "[Git Submodule Manager] " +
                    GitHubUtility.SanitizeUiDiagnostic(message));
            }

            RefreshAllEntries();
        }

        internal static bool ShouldPresentReadOnlyCompletionAsStandalone(
            ReadOnlyGitPackageInstallCompletion completion,
            bool dependencyPipelineIsBusy,
            bool dependencyCoordinatorIsBusy)
        {
            // Dependency-aware installs persist their coordinator operation ID
            // in the primitive itself. That ownership outlives callback order,
            // coordinator completion, and domain reload; a busy-only check does
            // not. Never present a dependency leaf or coordinated root as an
            // unrelated standalone Package Manager operation.
            return completion != null &&
                   !completion.IsDependencyInstallPrimitive &&
                   !dependencyPipelineIsBusy &&
                   !dependencyCoordinatorIsBusy;
        }

        private static void RemoveActiveReadOnlyInstallMessages(
            string packageName)
        {
            if (!GitUtility.IsValidUpmPackageName(packageName))
                return;

            string suffix = "\n" + packageName.Trim() + "\nmode:" +
                            PackageManagerGitInstallMode.ReadOnlyPackage;
            foreach (string key in new List<string>(ActiveInstallMessages.Keys))
            {
                bool currentFormat =
                    key.Contains(
                        "\n" + packageName.Trim() + "\n",
                        StringComparison.Ordinal) &&
                    key.EndsWith(
                        "\ninstall-mode:" +
                        PackageManagerGitInstallMode.ReadOnlyPackage,
                        StringComparison.Ordinal);
                if (currentFormat ||
                    key.EndsWith(suffix, StringComparison.Ordinal))
                {
                    ActiveInstallMessages.Remove(key);
                }
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
            string selectedBranch,
            PackageManagerGitInstallMode installMode)
        {
            string branch = string.IsNullOrWhiteSpace(selectedBranch)
                ? "the repository default branch"
                : $"branch {selectedBranch.Trim()}";
            if (installMode == PackageManagerGitInstallMode.ReadOnlyPackage)
            {
                return "Install this GitHub package as a normal read-only UPM " +
                       $"Git dependency from {branch}.";
            }

            string destination = GitSubmoduleAddService.GetPackagePath(
                repository?.PackageName ?? string.Empty);
            return string.IsNullOrWhiteSpace(destination)
                ? $"Install this GitHub package as a Git submodule from {branch}."
                : $"Install this GitHub package as a Git submodule from {branch} at {destination}.";
        }

        private static string BuildActiveInstallIdentity(
            PackageManagerGitHubRepository repository,
            string branch,
            PackageManagerGitInstallMode installMode)
        {
            return PackageManagerGitHubDetails.GetInstallSelectionIdentity(
                repository,
                branch,
                installMode);
        }

        private static bool MatchesPipelineInstall(
            PackageManagerGitHubRepository repository,
            string selectedBranch,
            PackageManagerGitInstallMode installMode,
            PackageDependencyInstallPipelineSnapshot pipeline)
        {
            if (repository == null || pipeline?.IsBusy != true ||
                installMode != pipeline.InstallMode ||
                !string.Equals(
                    repository.PackageName,
                    pipeline.PackageName,
                    StringComparison.Ordinal) ||
                !GitUtility.AreRepositoryUrlsEquivalent(
                    repository.Url,
                    pipeline.RepositoryUrl))
            {
                return false;
            }

            return string.IsNullOrWhiteSpace(pipeline.Branch) ||
                   string.Equals(
                       selectedBranch?.Trim(),
                       pipeline.Branch,
                       StringComparison.Ordinal);
        }

        private static string BuildDisabledTooltip(string validationError)
        {
            if (!string.IsNullOrWhiteSpace(validationError))
                return validationError.Trim();

            if (PackageDependencyInstallPipeline.IsBusy)
            {
                string message = PackageDependencyInstallPipeline.Current?.Message;
                return string.IsNullOrWhiteSpace(message)
                    ? "Wait for the current dependency-aware package install to finish."
                    : message.Trim();
            }

            if (PackageManagerProjectResolutionService.IsBusy)
                return PackageManagerProjectResolutionService.BuildUnavailableMessage();

            string recoveryWarning = GitOperationService.RecoveryWarning;
            if (!string.IsNullOrWhiteSpace(recoveryWarning))
                return recoveryWarning.Trim();

            string activeLabel = GitOperationService.ActiveLabel;
            if (GitOperationService.IsBusy && !string.IsNullOrWhiteSpace(activeLabel))
                return $"Wait for {activeLabel.Trim()} to finish.";

            return "Wait for current package scans and repository operations to finish.";
        }

        private static bool CanStartDependencyInstallPipeline()
        {
            return !PackageDependencyInstallPipeline.IsBusy &&
                   !GitOperationService.IsBusy &&
                   !PackageManagerReadOnlyGitInstallService.IsBusy &&
                   !PackageManagerProjectResolutionService.IsBusy &&
                   !AsyncCommandDrainRegistry.RequiresEditorRestart;
        }

        /// <summary>
        /// Unity can restore the Package Manager's active page selection before
        /// its recycled toolbar fields catch up after a package resolve or script
        /// reload. Prefer the exact active-page selection when that independent
        /// contract resolves. A present but inconsistent selection fails closed;
        /// only an unavailable optional contract preserves Harmony's explicit
        /// package argument for presentation.
        /// </summary>
        internal static object ResolvePackageForRefresh(
            object toolbar,
            object explicitPackage)
        {
            bool resolved = TryGetAuthoritativeSelectedPackage(
                toolbar,
                out object selectedPackage,
                out bool contractAvailable);
            return SelectPackageForRefresh(
                explicitPackage,
                contractAvailable,
                resolved,
                selectedPackage);
        }

        internal static object SelectPackageForRefresh(
            object explicitPackage,
            bool selectionContractAvailable,
            bool selectionResolved,
            object selectedPackage)
        {
            if (selectionResolved && selectedPackage != null)
                return selectedPackage;

            return selectionContractAvailable ? null : explicitPackage;
        }

        internal static bool TryGetAuthoritativeSelectedPackage(
            object toolbar,
            out object package)
        {
            return TryGetAuthoritativeSelectedPackage(
                toolbar,
                out package,
                out _);
        }

        private static bool TryGetAuthoritativeSelectedPackage(
            object toolbar,
            out object package,
            out bool contractAvailable)
        {
            package = null;
            contractAvailable = false;
            if (toolbar == null ||
                !TryGetSelectionContract(toolbar.GetType(), out SelectionContract contract))
            {
                return false;
            }

            contractAvailable = true;
            return contract.TryResolve(toolbar, out package);
        }

        internal static bool TryResolveExactSingleSelection(
            int reportedCount,
            IEnumerable<string> selectedIds,
            Func<string, object> packageLookup,
            Func<object, string> packageUniqueId,
            Func<object, string> packageName,
            out object package)
        {
            package = null;
            if (reportedCount != 1 ||
                selectedIds == null ||
                packageLookup == null ||
                packageUniqueId == null ||
                packageName == null)
            {
                return false;
            }

            try
            {
                using IEnumerator<string> enumerator = selectedIds.GetEnumerator();
                if (!enumerator.MoveNext() ||
                    string.IsNullOrEmpty(enumerator.Current))
                {
                    return false;
                }

                string selectedId = enumerator.Current;
                if (enumerator.MoveNext())
                    return false;

                object candidate = packageLookup(selectedId);
                if (candidate == null)
                    return false;

                string uniqueId = packageUniqueId(candidate);
                string name = packageName(candidate);
                if (!string.Equals(selectedId, uniqueId, StringComparison.Ordinal) &&
                    !string.Equals(selectedId, name, StringComparison.Ordinal))
                {
                    return false;
                }

                package = candidate;
                return true;
            }
            catch
            {
                package = null;
                return false;
            }
        }

        private static bool TryGetSelectionContract(
            Type toolbarType,
            out SelectionContract contract)
        {
            contract = supportedSelectionContract;
            if (contract != null && contract.ToolbarType == toolbarType)
                return true;

            SelectionContract candidate = SelectionContract.TryCreate(toolbarType);
            if (candidate == null)
            {
                contract = null;
                return false;
            }

            supportedSelectionContract = candidate;
            contract = candidate;
            return true;
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

        private static PackageManagerPackageConversionTarget BuildConversionTarget(
            PackageManagerSubmoduleInfo info)
        {
            return info == null
                ? null
                : new PackageManagerPackageConversionTarget(
                    GitPackageConversionDirection.SubmoduleToReadOnly,
                    info.PackageName,
                    info.PackagePath,
                    info.RepositoryUrl,
                    string.Empty);
        }

        private static PackageManagerPackageConversionTarget BuildConversionTarget(
            PackageManagerReadOnlyGitInfo info)
        {
            return info == null
                ? null
                : new PackageManagerPackageConversionTarget(
                    GitPackageConversionDirection.ReadOnlyToSubmodule,
                    info.PackageName,
                    GitSubmoduleAddService.GetPackagePath(info.PackageName),
                    info.RepositoryUrl,
                    (info.Revision ?? string.Empty) + "@" +
                    (info.ResolvedHash ?? string.Empty) + "|package-path:" +
                    (info.PackageSubfolder ?? string.Empty));
        }

        private static bool SameConversionTarget(
            PackageManagerPackageConversionTarget left,
            PackageManagerPackageConversionTarget right)
        {
            return left != null &&
                   right != null &&
                   string.Equals(
                       left.SelectionIdentity,
                       right.SelectionIdentity,
                       StringComparison.Ordinal);
        }

        private static string BuildConversionDisabledTooltip(string error)
        {
            return !string.IsNullOrWhiteSpace(error)
                ? error.Trim()
                : GitPackageConversionService.BuildUnavailableMessage();
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

        private sealed class SelectionContract
        {
            private SelectionContract(
                Type toolbarType,
                Type packageType,
                FieldInfo pageManagerField,
                FieldInfo packageDatabaseField,
                PropertyInfo activePageProperty,
                MethodInfo getSelectionMethod,
                PropertyInfo selectionCountProperty,
                MethodInfo getPackageMethod,
                PropertyInfo packageUniqueIdProperty,
                PropertyInfo packageNameProperty)
            {
                ToolbarType = toolbarType;
                PackageType = packageType;
                PageManagerField = pageManagerField;
                PackageDatabaseField = packageDatabaseField;
                ActivePageProperty = activePageProperty;
                GetSelectionMethod = getSelectionMethod;
                SelectionCountProperty = selectionCountProperty;
                GetPackageMethod = getPackageMethod;
                PackageUniqueIdProperty = packageUniqueIdProperty;
                PackageNameProperty = packageNameProperty;
            }

            internal Type ToolbarType { get; }
            private Type PackageType { get; }
            private FieldInfo PageManagerField { get; }
            private FieldInfo PackageDatabaseField { get; }
            private PropertyInfo ActivePageProperty { get; }
            private MethodInfo GetSelectionMethod { get; }
            private PropertyInfo SelectionCountProperty { get; }
            private MethodInfo GetPackageMethod { get; }
            private PropertyInfo PackageUniqueIdProperty { get; }
            private PropertyInfo PackageNameProperty { get; }

            internal static SelectionContract TryCreate(Type toolbarType)
            {
                try
                {
                    if (toolbarType == null ||
                        !string.Equals(
                            toolbarType.FullName,
                            PackageToolbarTypeName,
                            StringComparison.Ordinal))
                    {
                        return null;
                    }

                    Type pageManagerType =
                        PackageManagerSubmoduleHarmonyPatch.FindLoadedType(
                            PageManagerTypeName);
                    Type pageType =
                        PackageManagerSubmoduleHarmonyPatch.FindLoadedType(
                            PageInterfaceTypeName);
                    Type selectionType =
                        PackageManagerSubmoduleHarmonyPatch.FindLoadedType(
                            PageSelectionTypeName);
                    Type packageDatabaseType =
                        PackageManagerSubmoduleHarmonyPatch.FindLoadedType(
                            PackageDatabaseTypeName);
                    Type packageType =
                        PackageManagerSubmoduleHarmonyPatch.FindLoadedType(
                            PackageInterfaceTypeName);
                    Type publicPackageType =
                        PackageManagerSubmoduleHarmonyPatch.FindLoadedType(
                            PublicPackageInterfaceTypeName);
                    if (pageManagerType == null ||
                        pageType == null ||
                        selectionType == null ||
                        packageDatabaseType == null ||
                        packageType == null ||
                        publicPackageType == null ||
                        !typeof(IEnumerable<string>).IsAssignableFrom(selectionType) ||
                        !publicPackageType.IsAssignableFrom(packageType))
                    {
                        return null;
                    }

                    FieldInfo pageManagerField = toolbarType.GetField(
                        PageManagerFieldName,
                        AnyInstance);
                    FieldInfo packageDatabaseField = toolbarType.GetField(
                        PackageDatabaseFieldName,
                        AnyInstance);
                    PropertyInfo activePageProperty = pageManagerType.GetProperty(
                        "activePage",
                        AnyInstance);
                    MethodInfo getSelectionMethod = pageType.GetMethod(
                        "GetSelection",
                        AnyInstance,
                        null,
                        Type.EmptyTypes,
                        null);
                    PropertyInfo selectionCountProperty = selectionType.GetProperty(
                        "Count",
                        AnyInstance);
                    MethodInfo getPackageMethod = packageDatabaseType.GetMethod(
                        "GetPackage",
                        AnyInstance,
                        null,
                        new[] { typeof(string) },
                        null);
                    PropertyInfo packageUniqueIdProperty =
                        publicPackageType.GetProperty("uniqueId", AnyInstance);
                    PropertyInfo packageNameProperty =
                        publicPackageType.GetProperty("name", AnyInstance);

                    if (pageManagerField == null ||
                        pageManagerField.IsStatic ||
                        pageManagerField.FieldType != pageManagerType ||
                        packageDatabaseField == null ||
                        packageDatabaseField.IsStatic ||
                        packageDatabaseField.FieldType != packageDatabaseType ||
                        activePageProperty == null ||
                        activePageProperty.GetIndexParameters().Length != 0 ||
                        activePageProperty.PropertyType != pageType ||
                        activePageProperty.GetGetMethod(true)?.IsStatic != false ||
                        getSelectionMethod == null ||
                        getSelectionMethod.IsStatic ||
                        getSelectionMethod.ReturnType != selectionType ||
                        selectionCountProperty == null ||
                        selectionCountProperty.GetIndexParameters().Length != 0 ||
                        selectionCountProperty.PropertyType != typeof(int) ||
                        selectionCountProperty.GetGetMethod(true)?.IsStatic != false ||
                        getPackageMethod == null ||
                        getPackageMethod.IsStatic ||
                        getPackageMethod.ReturnType != packageType ||
                        packageUniqueIdProperty == null ||
                        packageUniqueIdProperty.GetIndexParameters().Length != 0 ||
                        packageUniqueIdProperty.PropertyType != typeof(string) ||
                        packageUniqueIdProperty.GetGetMethod(true)?.IsStatic != false ||
                        packageNameProperty == null ||
                        packageNameProperty.GetIndexParameters().Length != 0 ||
                        packageNameProperty.PropertyType != typeof(string) ||
                        packageNameProperty.GetGetMethod(true)?.IsStatic != false)
                    {
                        return null;
                    }

                    return new SelectionContract(
                        toolbarType,
                        packageType,
                        pageManagerField,
                        packageDatabaseField,
                        activePageProperty,
                        getSelectionMethod,
                        selectionCountProperty,
                        getPackageMethod,
                        packageUniqueIdProperty,
                        packageNameProperty);
                }
                catch
                {
                    return null;
                }
            }

            internal bool TryResolve(object toolbar, out object package)
            {
                package = null;
                if (toolbar == null || !ToolbarType.IsInstanceOfType(toolbar))
                    return false;

                try
                {
                    object pageManager = PageManagerField.GetValue(toolbar);
                    object packageDatabase = PackageDatabaseField.GetValue(toolbar);
                    object activePage = pageManager == null
                        ? null
                        : ActivePageProperty.GetValue(pageManager, null);
                    object selection = activePage == null
                        ? null
                        : GetSelectionMethod.Invoke(activePage, null);
                    if (packageDatabase == null ||
                        !(selection is IEnumerable<string> selectedIds) ||
                        !(SelectionCountProperty.GetValue(selection, null) is int count))
                    {
                        return false;
                    }

                    bool resolved = TryResolveExactSingleSelection(
                        count,
                        selectedIds,
                        id => GetPackageMethod.Invoke(
                            packageDatabase,
                            new object[] { id }),
                        candidate => PackageUniqueIdProperty.GetValue(
                            candidate,
                            null) as string,
                        candidate => PackageNameProperty.GetValue(
                            candidate,
                            null) as string,
                        out package);
                    if (!resolved || !PackageType.IsInstanceOfType(package))
                    {
                        package = null;
                        return false;
                    }

                    return true;
                }
                catch
                {
                    package = null;
                    return false;
                }
            }
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
                PackageManagerPackageConversionDetails conversionDetails,
                EventCallback<DetachFromPanelEvent> detachedCallback)
            {
                Root = root;
                Toolbar = toolbar;
                PrimaryActionsContainer = primaryActionsContainer;
                DetailsLinksContainer = detailsLinksContainer;
                Details = details;
                RemoveDetails = removeDetails;
                ConversionDetails = conversionDetails;
                DetachedCallback = detachedCallback;
            }

            internal VisualElement Root { get; }
            internal VisualElement Toolbar { get; }
            internal VisualElement PrimaryActionsContainer { get; }
            internal VisualElement DetailsLinksContainer { get; }
            internal PackageManagerGitHubDetails Details { get; }
            internal PackageManagerSubmoduleRemoveDetails RemoveDetails { get; }
            internal PackageManagerPackageConversionDetails ConversionDetails { get; }
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
