using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using MartinCalander.GitSubmoduleManager.Editor;
using NUnit.Framework;
using UnityEngine;

namespace MartinCalander.GitSubmoduleManager.Editor.Tests
{
    /// <summary>
    /// Fast, category-filterable diagnostics for every reflected Package Manager
    /// and Harmony seam. Run this category first when validating another Unity
    /// version so contract drift is reported by type and method instead of as a
    /// missing or partially working UI feature.
    /// </summary>
    [TestFixture]
    [Category(CategoryName)]
    public sealed class PackageManagerCompatibilityContractTests
    {
        public const string CategoryName = "PackageManagerCompatibility";

        private const string ConcreteMenuTypeName =
            "UnityEditor.PackageManager.UI.Internal.ExtendableToolbarMenu";
        private const string ConcreteMenuItemTypeName =
            "UnityEditor.PackageManager.UI.Internal.MenuDropdownItem";

        [Test]
        public void RequiredHostTagAndInstallMenuContracts_AreHealthy()
        {
            var report = new CompatibilityReport("required Package Manager seams");

            Type windowType =
                GitSubmoduleManagerPackageManagerHost.GetPackageManagerWindowType();
            report.Require(
                windowType != null,
                "Missing required type " +
                GitSubmoduleManagerPackageManagerHost.PackageManagerWindowTypeName + ".");

            IReadOnlyList<MethodInfo> lifecycleMethods =
                GitSubmoduleManagerPackageManagerHost
                    .GetSupportedLifecycleMethods(windowType);
            report.Observe(
                "Host lifecycle",
                DescribeMethods(lifecycleMethods));
            report.Require(
                lifecycleMethods.Count == 1,
                "Expected exactly one supported PackageManagerWindow lifecycle " +
                "method (BuildGUI, CreateGUI, or OnEnable), found " +
                lifecycleMethods.Count + ".");

            bool hostPatched =
                GitSubmoduleManagerPackageManagerHost.TryPatch(out string hostError);
            report.Require(
                hostPatched,
                "Package Manager host Harmony patch failed: " +
                EmptyAsUnknown(hostError));
            report.Require(
                GitSubmoduleManagerPackageManagerHost
                    .AreLifecyclePatchesApplied(),
                "Package Manager lifecycle methods do not contain both the " +
                "expected prefix and postfix owned by " +
                GitSubmoduleManagerPackageManagerHost.HarmonyId + ".");

            IReadOnlyList<MethodInfo> tagTargets =
                PackageManagerSubmoduleHarmonyPatch.GetTagTargetMethods();
            report.Observe("Tag hooks", DescribeMethods(tagTargets));
            report.Require(
                tagTargets.Count > 0,
                "Neither PackageDynamicTagLabel.Refresh nor the legacy " +
                "PackageTagLabel hook matches the expected IPackageVersion shape.");
            foreach (MethodInfo target in tagTargets)
            {
                ParameterInfo[] parameters = target.GetParameters();
                report.Require(
                    parameters.Length >= 1 &&
                    parameters.Length <= 2 &&
                    string.Equals(
                        parameters[0].ParameterType.FullName,
                        PackageManagerSubmoduleHarmonyPatch
                            .PackageVersionInterfaceTypeName,
                        StringComparison.Ordinal) &&
                    (parameters.Length == 1 ||
                     parameters[1].ParameterType == typeof(bool)),
                    "Unexpected tag hook signature: " + DescribeMethod(target) + ".");
            }

            bool presentationPatched =
                PackageManagerSubmoduleHarmonyPatch.TryPatch();
            report.Require(
                presentationPatched,
                "Package presentation Harmony patch failed: " +
                EmptyAsUnknown(
                    PackageManagerSubmoduleHarmonyPatch.LastPatchError));
            foreach (MethodInfo target in tagTargets)
            {
                MethodInfo postfix = target.IsStatic
                    ? PackageManagerSubmoduleHarmonyPatch
                        .GetTagFactoryPostfixMethod()
                    : PackageManagerSubmoduleHarmonyPatch
                        .GetTagRefreshPostfixMethod();
                report.Require(
                    PackageManagerSubmoduleHarmonyPatch.IsPatchApplied(
                        target,
                        postfix),
                    "Tag hook is resolved but lacks the postfix owned by " +
                    PackageManagerSubmoduleHarmonyPatch.HarmonyId + ": " +
                    DescribeMethod(target) + ".");
            }

            Type rootType = PackageManagerSubmoduleHarmonyPatch.FindLoadedType(
                PackageManagerGitSubmoduleInstallMenu
                    .PackageManagerWindowRootTypeName);
            PropertyInfo addMenu = PackageManagerGitSubmoduleInstallMenu
                .FindAddMenuProperty(rootType);
            MethodInfo addDropdownItem =
                PackageManagerGitSubmoduleInstallMenu
                    .FindAddDropdownItemMethod(addMenu?.PropertyType);
            Type concreteMenuType =
                PackageManagerSubmoduleHarmonyPatch.FindLoadedType(
                    ConcreteMenuTypeName);
            Type concreteMenuItemType =
                PackageManagerSubmoduleHarmonyPatch.FindLoadedType(
                    ConcreteMenuItemTypeName);
            MethodInfo removeItem = PackageManagerGitSubmoduleInstallMenu
                .FindRemoveMethod(
                    concreteMenuType,
                    concreteMenuItemType);
            report.Observe(
                "Add menu",
                addMenu == null
                    ? "unresolved"
                    : addMenu.DeclaringType?.FullName + "." + addMenu.Name);
            report.Observe(
                "AddDropdownItem",
                DescribeMethod(addDropdownItem));
            report.Observe(
                "Concrete add-menu removal",
                DescribeMethod(removeItem));
            report.Require(
                PackageManagerGitSubmoduleInstallMenu.IsSupportedContract(),
                "The top-left add-menu contract is unavailable; the required " +
                "'Install package as Git Submodule...' item cannot be installed.");
            report.Require(
                removeItem != null,
                "The concrete add-menu cleanup seam " + ConcreteMenuTypeName +
                ".Remove(" + ConcreteMenuItemTypeName + ") was not resolved. " +
                "Window teardown would retain a hidden native menu item.");

            MethodInfo guiToScreenRect =
                GitSubmoduleInstallPopup.FindGuiToScreenRectMethod();
            report.Observe(
                "Popup screen conversion",
                DescribeMethod(guiToScreenRect));
            if (IsUnityVersionAtLeast(6000, 0))
            {
                report.Require(
                    guiToScreenRect != null,
                    "Unity 6 should expose EditorMenuExtensions.GUIToScreenRect" +
                    "(VisualElement, Rect); popup anchoring has fallen back to " +
                    "the less precise legacy GUI conversion.");
            }

            Complete(report);
        }

        [Test]
        public void OptionalPresentationHooks_AreAtomicAndOwnedWhenPresent()
        {
            var report = new CompatibilityReport("optional presentation seams");
            PackageManagerSubmoduleHarmonyPatch.TryPatch();

            Type sourceType = PackageManagerSubmoduleHarmonyPatch.FindLoadedType(
                PackageManagerSubmoduleHarmonyPatch.SourceInfoCardTypeName);
            MethodInfo sourceTarget =
                PackageManagerSubmoduleHarmonyPatch.GetSourceTargetMethod();
            report.Observe(
                "SourceInfoCard",
                sourceType == null ? "type absent (supported fallback)" :
                DescribeMethod(sourceTarget));
            report.Require(
                sourceType == null || sourceTarget != null,
                "SourceInfoCard exists, but Refresh(IPackageVersion) no longer " +
                "matches. The GitHub Source card would silently disappear.");
            report.Require(
                sourceType != null || sourceTarget == null,
                "A SourceInfoCard hook resolved without its declaring type.");
            if (sourceTarget != null)
            {
                report.Require(
                    PackageManagerSubmoduleHarmonyPatch.IsPatchApplied(
                        sourceTarget,
                        PackageManagerSubmoduleHarmonyPatch
                            .GetSourceRefreshPostfixMethod()),
                    "SourceInfoCard hook lacks the postfix owned by " +
                    PackageManagerSubmoduleHarmonyPatch.HarmonyId + ".");
            }

            Type toolbarType = PackageManagerSubmoduleHarmonyPatch.FindLoadedType(
                PackageManagerSubmoduleHarmonyPatch.PackageToolbarTypeName);
            IReadOnlyList<MethodInfo> toolbarTargets =
                PackageManagerSubmoduleHarmonyPatch
                    .GetPackageToolbarTargetMethods();
            report.Observe("PackageToolbar hooks", DescribeMethods(toolbarTargets));
            report.Require(
                !IsUnityVersionAtLeast(2023, 2) ||
                toolbarType != null && toolbarTargets.Count > 0,
                "This Editor supports the native GitHub extension page, but no " +
                "PackageToolbar.Refresh(IPackage[, IPackageVersion]) method " +
                "resolves. GitHub install controls would not follow selection " +
                "changes.");
            foreach (MethodInfo target in toolbarTargets)
            {
                report.Require(
                    PackageManagerSubmoduleHarmonyPatch.IsPatchApplied(
                        target,
                        PackageManagerSubmoduleHarmonyPatch
                            .GetPackageToolbarRefreshPostfixMethod()),
                    "PackageToolbar hook lacks the postfix owned by " +
                    PackageManagerSubmoduleHarmonyPatch.HarmonyId + ": " +
                        DescribeMethod(target) + ".");
            }

            Type manageType = PackageManagerSubmoduleHarmonyPatch.FindLoadedType(
                PackageManagerSubmoduleManageMenu.ManageDropdownTypeName);
            report.Observe(
                "Manage dropdown",
                manageType == null ? "unresolved" : manageType.FullName);
            report.Require(
                !IsUnityVersionAtLeast(2023, 2) ||
                PackageManagerSubmoduleManageMenu.IsSupportedContract(),
                "The native Manage dropdown or its GenericDropdownMenu storage " +
                "could not be resolved. Submodule uninstall and conversion " +
                "actions would not be safely integrated.");

            Complete(report);
        }

        [Test]
        public void RemoveCustomAction_UsesExactOwnedFailSafePrefix()
        {
            var report = new CompatibilityReport(
                "embedded-package removal interception");
            Type actionType = PackageManagerSubmoduleHarmonyPatch.FindLoadedType(
                PackageManagerSubmoduleHarmonyPatch.RemoveCustomActionTypeName);
            MethodInfo target = PackageManagerSubmoduleHarmonyPatch
                .GetRemoveCustomActionTargetMethod();
            MethodInfo prefix = PackageManagerSubmoduleHarmonyPatch
                .GetRemoveCustomActionPrefixMethod();
            Type dispatcherType = PackageManagerSubmoduleHarmonyPatch.FindLoadedType(
                PackageManagerSubmoduleHarmonyPatch
                    .PackageOperationDispatcherTypeName);
            MethodInfo lowerTarget = PackageManagerSubmoduleHarmonyPatch
                .GetPackageOperationDispatcherRemoveEmbeddedTargetMethod();
            MethodInfo lowerPrefix = PackageManagerSubmoduleHarmonyPatch
                .GetPackageOperationDispatcherRemoveEmbeddedPrefixMethod();

            report.Observe(
                "RemoveCustomAction",
                actionType == null ? "unresolved" : actionType.FullName);
            report.Observe(
                "TriggerActionImplementation",
                DescribeMethod(target));
            report.Observe(
                "PackageOperationDispatcher.RemoveEmbedded",
                DescribeMethod(lowerTarget));
            report.Require(
                actionType != null,
                "Missing required type " +
                PackageManagerSubmoduleHarmonyPatch.RemoveCustomActionTypeName +
                "; Unity could delete an embedded submodule without cleaning " +
                "its parent-repository metadata.");
            report.Require(
                target != null,
                "RemoveCustomAction exists, but the exact " +
                "bool TriggerActionImplementation(IPackageVersion) override is " +
                "unavailable.");
            report.Require(
                prefix != null,
                "The submodule removal Harmony prefix could not be resolved.");
            if (prefix != null)
            {
                ParameterInfo[] parameters = prefix.GetParameters();
                report.Require(
                    prefix.ReturnType == typeof(bool) &&
                    parameters.Length == 2 &&
                    parameters[0].ParameterType == typeof(object) &&
                    parameters[1].ParameterType == typeof(bool).MakeByRefType(),
                    "The RemoveCustomAction prefix must return whether Unity " +
                    "should run and expose its bool result by reference.");
            }
            report.Require(
                dispatcherType != null,
                "Missing required lower-level type " +
                PackageManagerSubmoduleHarmonyPatch
                    .PackageOperationDispatcherTypeName + ".");
            report.Require(
                lowerTarget != null,
                "PackageOperationDispatcher exists, but the exact " +
                "void RemoveEmbedded(IPackage) method is unavailable.");
            report.Require(
                lowerPrefix != null,
                "The lower-level embedded-package deletion guard could not be " +
                "resolved.");
            if (lowerPrefix != null)
            {
                ParameterInfo[] parameters = lowerPrefix.GetParameters();
                report.Require(
                    lowerPrefix.ReturnType == typeof(bool) &&
                    parameters.Length == 1 &&
                    parameters[0].ParameterType == typeof(object),
                    "The PackageOperationDispatcher prefix must return whether " +
                    "Unity should run and accept the IPackage argument.");
            }

            if (target != null)
            {
                ParameterInfo[] parameters = target.GetParameters();
                report.Require(
                    target.DeclaringType == actionType &&
                    target.ReturnType == typeof(bool) &&
                    parameters.Length == 1 &&
                    string.Equals(
                        parameters[0].ParameterType.FullName,
                        PackageManagerSubmoduleHarmonyPatch
                            .PackageVersionInterfaceTypeName,
                        StringComparison.Ordinal),
                    "Unexpected embedded-package removal signature: " +
                    DescribeMethod(target) + ".");

                bool presentationPatched =
                    PackageManagerSubmoduleHarmonyPatch.TryPatch();
                report.Require(
                    presentationPatched,
                    "Package Manager Harmony registration failed: " +
                    EmptyAsUnknown(
                        PackageManagerSubmoduleHarmonyPatch.LastPatchError));
                report.Require(
                    PackageManagerSubmoduleHarmonyPatch
                        .IsRemoveCustomActionPatchApplied(),
                    "RemoveCustomAction.TriggerActionImplementation lacks the " +
                    "prefix owned by " +
                    PackageManagerSubmoduleHarmonyPatch.HarmonyId + ".");
            }

            if (lowerTarget != null)
            {
                ParameterInfo[] parameters = lowerTarget.GetParameters();
                report.Require(
                    lowerTarget.DeclaringType == dispatcherType &&
                    lowerTarget.ReturnType == typeof(void) &&
                    parameters.Length == 1 &&
                    string.Equals(
                        parameters[0].ParameterType.FullName,
                        PackageManagerSubmoduleHarmonyPatch
                            .PackageInterfaceTypeName,
                        StringComparison.Ordinal),
                    "Unexpected lower-level embedded-package removal signature: " +
                    DescribeMethod(lowerTarget) + ".");
                report.Require(
                    PackageManagerSubmoduleHarmonyPatch
                        .IsPackageOperationDispatcherRemoveEmbeddedPatchApplied(),
                    "PackageOperationDispatcher.RemoveEmbedded lacks the " +
                    "submodule fail-safe prefix owned by " +
                    PackageManagerSubmoduleHarmonyPatch.HarmonyId + ".");
            }

            Complete(report);
        }

        [Test]
        public void RemoveCustomActionPrefix_NullPackageVersion_FailsOpen()
        {
            MethodInfo prefix = PackageManagerSubmoduleHarmonyPatch
                .GetRemoveCustomActionPrefixMethod();
            Assert.That(prefix, Is.Not.Null);

            object[] arguments = { null, false };
            bool runOriginal = (bool)prefix.Invoke(null, arguments);

            Assert.That(runOriginal, Is.True,
                "A non-submodule Package Manager action must remain native.");
            Assert.That(arguments[1], Is.False,
                "Failing open must not manufacture a handled action result.");

            MethodInfo lowerPrefix = PackageManagerSubmoduleHarmonyPatch
                .GetPackageOperationDispatcherRemoveEmbeddedPrefixMethod();
            Assert.That(lowerPrefix, Is.Not.Null);
            Assert.That(
                (bool)lowerPrefix.Invoke(null, new object[] { null }),
                Is.True,
                "An unrecognized embedded package must remain native.");
        }

        [Test]
        public void NativeGitHubPageContract_IsCompleteOnSupportingEditors()
        {
            var report = new CompatibilityReport("native Sources/GitHub seams");
            bool expectsNativePage = IsUnityVersionAtLeast(2023, 2);
            bool nativePageSupported =
                PackageManagerSubmoduleNativePage.IsSupportedContract();
            report.Observe("Native page expected", expectsNativePage.ToString());
            report.Observe("Native page supported", nativePageSupported.ToString());

            if (expectsNativePage)
            {
                report.Require(
                    nativePageSupported,
                    "Unity 2023.2 and newer expose the extension-page generation " +
                    "used by Sources/GitHub, but the complete reflected contract " +
                    "did not resolve. Inspect the observations below for drift.");
                report.Require(
                    PackageManagerGitHubPackageProjection.IsSupportedContract(),
                    "The native page is expected, but the fail-closed package " +
                    "projection contract is incomplete.");
                report.Require(
                    PackageManagerGitHubNativeActions.HasSupportedLiveContract(),
                    "The native GitHub details/primary Install action reflection " +
                    "contract is incomplete.");
            }

            AddActivePageReflectionInventory(report, expectsNativePage);

            IReadOnlyList<MethodInfo> technicalTargets =
                PackageManagerGitHubNativePresentationPatch
                    .GetTechnicalNameTargets();
            IReadOnlyList<MethodInfo> authorTargets =
                PackageManagerGitHubNativePresentationPatch.GetAuthorTargets();
            IReadOnlyList<MethodInfo> refreshTargets =
                PackageManagerGitHubNativePresentationPatch
                    .GetPageRefreshTargets();
            IReadOnlyList<MethodInfo> activationTargets =
                PackageManagerGitHubNativePresentationPatch
                    .GetPageActivationTargets();
            IReadOnlyList<MethodInfo> loadingTargets =
                PackageManagerGitHubNativePresentationPatch
                    .GetPageLoadingTargets();
            MethodInfo repositoryFilterTarget =
                PackageManagerGitHubNativePresentationPatch
                    .GetPageVisibilityFilterTarget();
            MethodInfo supportedFiltersRefreshTarget =
                PackageManagerGitHubNativePresentationPatch
                    .GetPageSupportedFiltersRefreshTarget();
            PropertyInfo selectedCategories =
                PackageManagerGitHubNativePresentationPatch
                    .GetPageFilterCategoriesProperty();
            PropertyInfo supportedCategories =
                PackageManagerGitHubNativePresentationPatch
                    .GetPageFilterSupportedCategoriesProperty();
            Type technicalNameType =
                PackageManagerSubmoduleHarmonyPatch.FindLoadedType(
                    PackageManagerGitHubNativePresentationPatch
                        .TechnicalNameCardTypeName);
            Type authorType = PackageManagerSubmoduleHarmonyPatch.FindLoadedType(
                PackageManagerGitHubNativePresentationPatch
                    .PackageAuthorLabelTypeName);
            report.Observe("TechnicalNameCard hooks", DescribeMethods(technicalTargets));
            report.Observe("PackageAuthorLabel hooks", DescribeMethods(authorTargets));
            report.Observe("Page refresh hooks", DescribeMethods(refreshTargets));
            report.Observe("Page activation hooks", DescribeMethods(activationTargets));
            report.Observe("Page loading hooks", DescribeMethods(loadingTargets));
            report.Observe(
                "GitHub repository filter hook",
                DescribeMethod(repositoryFilterTarget));
            report.Observe(
                "Supported-label updater",
                DescribeMethod(
                    PackageManagerSubmoduleNativePage
                        .GetUpdateSupportedLabelsMethod()));
            report.Observe(
                "Supported-category updater",
                DescribeMethod(
                    PackageManagerSubmoduleNativePage
                        .GetUpdateSupportedCategoriesMethod()));
            report.Observe(
                "Selected categories",
                DescribeProperty(selectedCategories));
            report.Observe(
                "Supported categories",
                DescribeProperty(supportedCategories));
            report.Observe(
                "Supported-filter refresh hook",
                DescribeMethod(supportedFiltersRefreshTarget));
            report.Observe(
                "Status update",
                DescribeMethod(
                    PackageManagerGitHubNativePresentationPatch
                        .GetPackageStatusUpdateMethod()));
            report.Observe(
                "List rebuild",
                DescribeMethod(
                    PackageManagerGitHubNativePresentationPatch
                        .GetListAreaRebuildMethod()));

            if (nativePageSupported || expectsNativePage)
            {
                report.Require(
                    technicalNameType == null || technicalTargets.Count > 0,
                    "TechnicalNameInfoCard exists, but " +
                    "Refresh(IPackageVersion) is missing.");
                report.Require(
                    authorType == null || authorTargets.Count > 0,
                    "PackageAuthorLabel exists, but " +
                    "Refresh(IPackageVersion) is missing.");
                report.Require(refreshTargets.Count > 0,
                    "PageRefreshHandler.Refresh(IPage) is missing.");
                report.Require(activationTargets.Count > 0,
                    "PageRefreshHandler.OnActivePageChanged(IPage) is missing.");
                report.Require(loadingTargets.Count > 0,
                    "PageRefreshHandler.IsRefreshInProgress(IPage) is missing.");
                report.Require(
                    PackageManagerGitHubNativePresentationPatch
                        .HasPageVisibilityFilterContract(),
                    "The GitHub page repository filter contract is incomplete; " +
                    "the toolbar must not expose inert filters.");
                report.Require(
                    PackageManagerSubmoduleNativePage
                        .GetUpdateSupportedLabelsMethod() != null,
                    "BasePage.UpdateSupportedLabels(IReadOnlyList<string>, bool) " +
                    "is missing.");
                report.Require(
                    PackageManagerSubmoduleNativePage
                        .GetUpdateSupportedCategoriesMethod() != null,
                    "BasePage.UpdateSupportedCategories(" +
                    "IReadOnlyList<string>, bool) is missing.");
                report.Require(
                    selectedCategories != null,
                    "PageFilters.categories is missing or is no longer a " +
                    "readable IReadOnlyList<string>.");
                report.Require(
                    supportedCategories != null,
                    "PageFilters.supportedCategories is missing or is no " +
                    "longer a readable IReadOnlyList<string>.");
                report.Require(
                    supportedFiltersRefreshTarget != null,
                    "BasePage.UpdateSupportedFiltersAsync() is missing or no " +
                    "longer an instance void method declared by BasePage.");
                report.Require(
                    PackageManagerGitHubNativePresentationPatch
                        .HasRequiredDiscoveryLifecycleContract(),
                    "The discovery activation/refresh/loading/status contract is " +
                    "not complete and must fail over as one unit.");

                bool presentationPatched =
                    PackageManagerGitHubNativePresentationPatch.TryPatch();
                report.Require(presentationPatched,
                    "Native GitHub presentation Harmony registration failed.");
                RequireOwnedPostfixes(
                    report,
                    technicalTargets,
                    PackageManagerGitHubNativePresentationPatch
                        .GetTechnicalNamePostfix());
                RequireOwnedPostfixes(
                    report,
                    authorTargets,
                    PackageManagerGitHubNativePresentationPatch
                        .GetAuthorPostfix());
                RequireOwnedPostfixes(
                    report,
                    refreshTargets,
                    PackageManagerGitHubNativePresentationPatch
                        .GetPageRefreshPostfix());
                RequireOwnedPostfixes(
                    report,
                    activationTargets,
                    PackageManagerGitHubNativePresentationPatch
                        .GetPageActivationPostfix());
                RequireOwnedPostfixes(
                    report,
                    loadingTargets,
                    PackageManagerGitHubNativePresentationPatch
                        .GetPageLoadingPostfix());
                RequireOwnedPostfixes(
                    report,
                    new[] { repositoryFilterTarget },
                    PackageManagerGitHubNativePresentationPatch
                        .GetPageVisibilityFilterPostfix());
                RequireOwnedPostfixes(
                    report,
                    new[] { supportedFiltersRefreshTarget },
                    PackageManagerGitHubNativePresentationPatch
                        .GetPageSupportedFiltersRefreshPostfix());

                GitSubmoduleManagerPackageManagerHost.TryPatch(out _);
                report.Require(
                    GitSubmoduleManagerPackageManagerHost
                        .IsSidebarExtensionRefreshPatchApplied(),
                    "Sidebar.UpdateExtensionPageRelatedRows lacks the postfix " +
                    "owned by " + GitSubmoduleManagerPackageManagerHost.HarmonyId + ".");
            }

            Complete(report);
        }

        [Test]
        public void HarmonyBridgeMethods_KeepRequiredSpecialArgumentNames()
        {
            var report = new CompatibilityReport("Harmony bridge signatures");
            RequireParameterNames(
                report,
                PackageManagerSubmoduleHarmonyPatch.GetTagRefreshPostfixMethod(),
                "__instance",
                "__0");
            RequireParameterNames(
                report,
                PackageManagerSubmoduleHarmonyPatch.GetTagFactoryPostfixMethod(),
                "__0",
                "__result");
            RequireParameterNames(
                report,
                PackageManagerSubmoduleHarmonyPatch.GetSourceRefreshPostfixMethod(),
                "__instance",
                "__0");
            RequireParameterNames(
                report,
                PackageManagerSubmoduleHarmonyPatch
                    .GetPackageToolbarRefreshPostfixMethod(),
                "__instance",
                "__0");
            RequireParameterNames(
                report,
                PackageManagerSubmoduleHarmonyPatch
                    .GetRemoveCustomActionPrefixMethod(),
                "__0",
                "__result");
            RequireParameterNames(
                report,
                PackageManagerSubmoduleHarmonyPatch
                    .GetPackageOperationDispatcherRemoveEmbeddedPrefixMethod(),
                "__0");
            RequireParameterNames(
                report,
                PackageManagerGitHubNativePresentationPatch
                    .GetPageActivationPostfix(),
                "__0");
            RequireParameterNames(
                report,
                PackageManagerGitHubNativePresentationPatch
                    .GetPageLoadingPostfix(),
                "__0",
                "__result");
            RequireParameterNames(
                report,
                PackageManagerGitHubNativePresentationPatch
                    .GetPageVisibilityFilterPostfix(),
                "__instance",
                "__0",
                "__result");
            RequireParameterNames(
                report,
                PackageManagerGitHubNativePresentationPatch
                    .GetPageSupportedFiltersRefreshPostfix(),
                "__instance");

            Complete(report);
        }

        private static void RequireOwnedPostfixes(
            CompatibilityReport report,
            IEnumerable<MethodInfo> targets,
            MethodInfo postfix)
        {
            foreach (MethodInfo target in targets)
            {
                report.Require(
                    PackageManagerGitHubNativePresentationPatch.IsPatchApplied(
                        target,
                        postfix),
                    "Resolved hook lacks the postfix owned by " +
                    PackageManagerGitHubNativePresentationPatch.HarmonyId + ": " +
                    DescribeMethod(target) + ".");
            }
        }

        private static void AddActivePageReflectionInventory(
            CompatibilityReport report,
            bool required)
        {
            Type windowType =
                GitSubmoduleManagerPackageManagerHost.GetPackageManagerWindowType();
            FieldInfo rootField = FindFieldInHierarchy(windowType, "m_Root");
            Type rootType = rootField?.FieldType ??
                PackageManagerSubmoduleHarmonyPatch.FindLoadedType(
                    PackageManagerGitHubNativePresentationPatch
                        .PackageManagerWindowRootTypeName);
            FieldInfo pageManagerField =
                FindFieldInHierarchy(rootType, "m_PageManager");
            PropertyInfo pageManagerProperty =
                FindPropertyInHierarchy(rootType, "pageManager");
            Type pageManagerType = pageManagerField?.FieldType ??
                pageManagerProperty?.PropertyType ??
                PackageManagerSubmoduleHarmonyPatch.FindLoadedType(
                    PackageManagerSubmoduleNativePage.PageManagerTypeName);
            PropertyInfo activePage =
                FindPropertyInHierarchy(pageManagerType, "activePage");
            Type pageType = PackageManagerSubmoduleHarmonyPatch.FindLoadedType(
                PackageManagerGitHubNativePresentationPatch.PageInterfaceTypeName);
            PropertyInfo pageId = FindPropertyInHierarchy(pageType, "id");

            report.Observe(
                "Window root field",
                rootField == null
                    ? "unresolved"
                    : rootField.DeclaringType?.FullName + "." + rootField.Name);
            report.Observe(
                "Root page manager",
                pageManagerField != null
                    ? pageManagerField.DeclaringType?.FullName + "." +
                      pageManagerField.Name
                    : pageManagerProperty == null
                        ? "unresolved"
                        : pageManagerProperty.DeclaringType?.FullName + "." +
                          pageManagerProperty.Name);
            report.Observe(
                "PageManager.activePage",
                activePage == null
                    ? "unresolved"
                    : activePage.DeclaringType?.FullName + "." + activePage.Name);
            report.Observe(
                "IPage.id",
                pageId == null
                    ? "unresolved"
                    : pageId.DeclaringType?.FullName + "." + pageId.Name);

            if (!required)
                return;

            report.Require(rootField != null,
                "PackageManagerWindow.m_Root is missing; tag page scoping cannot " +
                "resolve the containing window's PackageManagerWindowRoot.");
            report.Require(pageManagerField != null || pageManagerProperty != null,
                "PackageManagerWindowRoot exposes neither m_PageManager nor " +
                "pageManager; tag page scoping cannot resolve activePage.");
            report.Require(activePage != null && activePage.CanRead,
                "PageManager.activePage is missing or unreadable.");
            report.Require(
                pageId != null &&
                pageId.CanRead &&
                pageId.PropertyType == typeof(string),
                "IPage.id is missing or is no longer a readable string.");
        }

        private static void RequireParameterNames(
            CompatibilityReport report,
            MethodInfo method,
            params string[] expected)
        {
            if (method == null)
            {
                report.Require(false,
                    "Harmony bridge method could not be resolved.");
                return;
            }

            string[] actual = method.GetParameters()
                .Select(parameter => parameter.Name)
                .ToArray();
            report.Require(
                actual.SequenceEqual(expected),
                DescribeMethod(method) + " must keep Harmony special argument " +
                "names [" + string.Join(", ", expected) + "], found [" +
                string.Join(", ", actual) + "].");
        }

        private static bool IsUnityVersionAtLeast(int requiredMajor, int requiredMinor)
        {
            string[] parts = (Application.unityVersion ?? string.Empty).Split('.');
            if (parts.Length < 2 ||
                !int.TryParse(parts[0], out int major) ||
                !int.TryParse(parts[1], out int minor))
            {
                return false;
            }

            return major > requiredMajor ||
                   major == requiredMajor && minor >= requiredMinor;
        }

        private static string DescribeMethods(IEnumerable<MethodInfo> methods)
        {
            if (methods == null)
                return "unresolved";

            string[] descriptions = methods
                .Where(method => method != null)
                .Select(DescribeMethod)
                .ToArray();
            return descriptions.Length == 0
                ? "none"
                : string.Join("; ", descriptions);
        }

        private static FieldInfo FindFieldInHierarchy(Type type, string name)
        {
            const BindingFlags Flags =
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            for (Type current = type; current != null; current = current.BaseType)
            {
                FieldInfo field = current.GetField(name, Flags);
                if (field != null)
                    return field;
            }

            return null;
        }

        private static PropertyInfo FindPropertyInHierarchy(Type type, string name)
        {
            const BindingFlags Flags =
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            for (Type current = type; current != null; current = current.BaseType)
            {
                PropertyInfo property = current.GetProperty(name, Flags);
                if (property != null && property.GetIndexParameters().Length == 0)
                    return property;
            }

            return null;
        }

        private static string DescribeMethod(MethodInfo method)
        {
            if (method == null)
                return "unresolved";

            return (method.DeclaringType?.FullName ?? "<unknown-type>") + "." +
                   method.Name + "(" +
                   string.Join(
                       ", ",
                       method.GetParameters()
                           .Select(parameter =>
                               parameter.ParameterType.FullName ??
                               parameter.ParameterType.Name)) + ")";
        }

        private static string DescribeProperty(PropertyInfo property)
        {
            if (property == null)
                return "unresolved";

            return (property.DeclaringType?.FullName ?? "<unknown-type>") + "." +
                   property.Name + " : " +
                   (property.PropertyType.FullName ?? property.PropertyType.Name);
        }

        private static string EmptyAsUnknown(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? "no diagnostic" : value;
        }

        private static void Complete(CompatibilityReport report)
        {
            TestContext.Out.WriteLine(report.FormatObservations());
            Assert.That(
                report.Failures,
                Is.Empty,
                report.FormatFailure());
        }

        private sealed class CompatibilityReport
        {
            private readonly string scope;
            private readonly List<string> observations = new List<string>();

            internal CompatibilityReport(string scope)
            {
                this.scope = scope;
            }

            internal List<string> Failures { get; } = new List<string>();

            internal void Require(bool condition, string failure)
            {
                if (!condition)
                    Failures.Add(failure);
            }

            internal void Observe(string label, string value)
            {
                observations.Add(label + ": " + value);
            }

            internal string FormatObservations()
            {
                var builder = new StringBuilder();
                builder.Append("Package Manager compatibility inventory (")
                    .Append(RuntimeContext)
                    .AppendLine("):");
                foreach (string observation in observations)
                    builder.Append("- ").AppendLine(observation);
                return builder.ToString();
            }

            internal string FormatFailure()
            {
                var builder = new StringBuilder();
                builder.Append("Package Manager compatibility failure in ")
                    .Append(scope)
                    .Append(" (")
                    .Append(RuntimeContext)
                    .AppendLine(").");
                for (int index = 0; index < Failures.Count; index++)
                {
                    builder.Append(index + 1)
                        .Append(". ")
                        .AppendLine(Failures[index]);
                }
                builder.AppendLine("Resolved inventory:")
                    .Append(FormatObservations());
                return builder.ToString();
            }
        }

        private static string RuntimeContext =>
            "Unity " + Application.unityVersion + ", " + Application.platform;
    }
}
