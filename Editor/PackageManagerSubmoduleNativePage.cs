using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace MartinCalander.GitSubmoduleManager.Editor
{
    /// <summary>
    /// Bridges discovered GitHub packages and installed Git packages into
    /// Unity's internal extension-page contract. The package deliberately
    /// targets the declared Unity 6000.5 Package Manager contract.
    /// </summary>
    internal static class PackageManagerSubmoduleNativePage
    {
        internal const string ExtensionPageName = "git-submodule-manager";
        internal const string ExtensionPageId = "Extension/" + ExtensionPageName;
        internal const string ExtensionPageDisplayName = "GitHub";
        internal const string NativeSidebarRowElementName =
            "git-submodule-manager-native-sidebar-row";
        internal const string ExtensionPageArgsTypeName =
            "UnityEditor.PackageManager.UI.Internal.ExtensionPageArgs";
        internal const string PageManagerTypeName =
            "UnityEditor.PackageManager.UI.Internal.PageManager";
        internal const string ServicesContainerTypeName =
            "UnityEditor.PackageManager.UI.Internal.ServicesContainer";
        internal const string BasePageTypeName =
            "UnityEditor.PackageManager.UI.Internal.BasePage";
        internal const string DownloadedFilterStatusName = "Downloaded";
        internal const string SidebarTypeName =
            "UnityEditor.PackageManager.UI.Internal.Sidebar";
        internal const string SidebarExtensionRowsUpdateMethodName =
            "UpdateExtensionPageRelatedRows";
        private const string OrganizationFilterFormat =
            "Organization - {0}";

        private const string SidebarRowTypeName =
            "UnityEditor.PackageManager.UI.Internal.SidebarRow";
        private const string LegacySidebarPageId =
            "GitSubmoduleManager.GitHub";
        private const string MyAssetsPageId = "MyAssets";
        private const string AddExtensionPageMethodName = "AddExtensionPage";
        private const string GetPageMethodName = "GetPage";
        private const string UpdateSupportedLabelsMethodName =
            "UpdateSupportedLabels";
        private const string UpdateSupportedCategoriesMethodName =
            "UpdateSupportedCategories";

        private static readonly IReadOnlyList<string> SupportedVisibilityLabels =
            Array.AsReadOnly(new[]
            {
                PackageManagerSubmodulePresentation.PublicRepositoryTagLabel,
                PackageManagerSubmodulePresentation.PrivateRepositoryTagLabel
            });

        private sealed class DefaultFilterMarker
        {
            internal string PreferenceSignature = string.Empty;
        }

        private static readonly ConditionalWeakTable<object, DefaultFilterMarker>
            DefaultFiltersApplied = new();

        private const BindingFlags AnyInstance =
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private const BindingFlags AnyStatic =
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic |
            BindingFlags.FlattenHierarchy;

        internal static bool IsSupportedContract()
        {
            Type argsType = FindLoadedType(ExtensionPageArgsTypeName);
            Type pageManagerType = FindLoadedType(PageManagerTypeName);
            Type sidebarType = FindLoadedType(SidebarTypeName);
            Type sidebarRowType = FindLoadedType(SidebarRowTypeName);
            if (argsType == null || pageManagerType == null ||
                sidebarType == null || sidebarRowType == null)
                return false;

            return FindAddExtensionPageMethod(pageManagerType, argsType) != null &&
                   FindGetPageMethod(pageManagerType) != null &&
                   pageManagerType.GetProperty("activePage", AnyInstance) != null &&
                   FindSidebarGetRowMethod(sidebarType) != null &&
                   FindSidebarExtensionRowsUpdateMethod(sidebarType) != null &&
                   typeof(VisualElement).IsAssignableFrom(sidebarRowType) &&
                   HasRequiredArgsFields(argsType) &&
                   GetUpdateSupportedLabelsMethod() != null &&
                   GetUpdateSupportedCategoriesMethod() != null &&
                   PackageManagerGitHubNativePresentationPatch
                       .HasRequiredDiscoveryLifecycleContract();
        }

        internal static bool TryRegisterFromServices(
            out object pageManager,
            out object page)
        {
            pageManager = null;
            page = null;
            if (!IsSupportedContract())
                return false;

            try
            {
                Type servicesType = FindLoadedType(ServicesContainerTypeName);
                Type pageManagerType = FindLoadedType(PageManagerTypeName);
                if (servicesType == null || pageManagerType == null)
                    return false;

                PropertyInfo instanceProperty = servicesType.GetProperty(
                    "instance",
                    AnyStatic);
                object services = instanceProperty?.GetValue(null, null);
                if (services == null)
                    return false;

                MethodInfo resolve = FindResolveMethod(servicesType);
                if (resolve == null)
                    return false;

                pageManager = resolve.MakeGenericMethod(pageManagerType)
                    .Invoke(services, null);
                return TryRegister(pageManager, out page);
            }
            catch
            {
                pageManager = null;
                page = null;
                return false;
            }
        }

        internal static bool TryRegister(
            object pageManager,
            out object page)
        {
            page = null;
            if (pageManager == null || !IsSupportedContract())
                return false;

            try
            {
                // Unity keeps cached pages after clearing its ordered extension
                // arguments on window teardown. Only the ordered collection
                // proves that the current window lifecycle is registered.
                page = FindRegisteredPage(pageManager);
                if (page != null)
                    return TryConfigureFilters(page);

                if (!TryCreateExtensionPageArgs(out object args))
                    return false;

                MethodInfo addPage = FindAddExtensionPageMethod(
                    pageManager.GetType(),
                    args.GetType());
                if (addPage == null)
                    return false;

                addPage.Invoke(pageManager, new[] { args });
                page = FindRegisteredPage(pageManager) ??
                       FindPageById(pageManager, ExtensionPageId);
                return page != null && TryConfigureFilters(page);
            }
            catch
            {
                page = null;
                return false;
            }
        }

        internal static bool TryCreateExtensionPageArgs(out object args)
        {
            args = null;
            if (!IsSupportedContract())
                return false;

            Type argsType = FindLoadedType(ExtensionPageArgsTypeName);
            if (argsType == null)
                return false;

            try
            {
                args = Activator.CreateInstance(argsType, true);
                SetField(args, "name", ExtensionPageName);
                SetField(args, "displayName", ExtensionPageDisplayName);
                SetField(args, "priority", 100);
                SetEnumField(args, "icon", "None");
                SetEnumField(args, "capability", "None");
                SetEnumFlagsField(
                    args,
                    "refreshOptions",
                    "UpmList",
                    "LocalInfo",
                    "ImportedAssets",
                    "ImportedSamples");
                SetEnumArray(
                    args,
                    "supportedStatusFilters",
                    DownloadedFilterStatusName);
                SetEnumArray(
                    args,
                    "supportedSortOptions",
                    "NameAsc",
                    "NameDesc");

                // Func<T, TResult> is contravariant in T, so these object-based
                // delegates can safely back Unity's internal IPackage delegates.
                SetField(args, "filter", new Func<object, bool>(ShouldIncludePackage));
                SetField(args, "getGroupName", new Func<object, string>(GetGroupName));
                SetField(
                    args,
                    "compareGroup",
                    new Func<string, string, int>(CompareGroupNames));
                return true;
            }
            catch
            {
                args = null;
                return false;
            }
        }

        internal static object GetPageManager(object packageManagerRoot)
        {
            return GetFieldValue(packageManagerRoot, "m_PageManager") ??
                   GetPropertyValue(packageManagerRoot, "pageManager");
        }

        internal static IReadOnlyList<string> GetSupportedVisibilityLabels()
        {
            return SupportedVisibilityLabels;
        }

        internal static MethodInfo GetUpdateSupportedLabelsMethod()
        {
            Type basePageType = FindLoadedType(BasePageTypeName);
            MethodInfo method = basePageType?.GetMethod(
                UpdateSupportedLabelsMethodName,
                AnyInstance,
                null,
                new[] { typeof(IReadOnlyList<string>), typeof(bool) },
                null);
            return method != null &&
                   !method.IsStatic &&
                   method.ReturnType == typeof(bool)
                ? method
                : null;
        }

        internal static MethodInfo GetUpdateSupportedCategoriesMethod()
        {
            Type basePageType = FindLoadedType(BasePageTypeName);
            MethodInfo method = basePageType?.GetMethod(
                UpdateSupportedCategoriesMethodName,
                AnyInstance,
                null,
                new[] { typeof(IReadOnlyList<string>), typeof(bool) },
                null);
            return method != null &&
                   !method.IsStatic &&
                   method.ReturnType == typeof(bool)
                ? method
                : null;
        }

        internal static string CreateOrganizationFilterLabel(string owner)
        {
            string normalizedOwner = owner?.Trim() ?? string.Empty;
            return string.IsNullOrEmpty(normalizedOwner)
                ? string.Empty
                : string.Format(
                    GetLocalizedOrganizationFilterFormat(),
                    normalizedOwner);
        }

        internal static bool IsOrganizationFilterLabel(string label)
        {
            return TryGetOrganizationFilterOwner(label, out _);
        }

        internal static bool TryGetOrganizationFilterOwner(
            string label,
            out string owner)
        {
            owner = string.Empty;
            if (string.IsNullOrWhiteSpace(label))
                return false;

            string format = GetLocalizedOrganizationFilterFormat();
            int placeholderIndex = format.IndexOf("{0}", StringComparison.Ordinal);
            if (placeholderIndex < 0)
                return false;

            string prefix = format.Substring(0, placeholderIndex);
            string suffix = format.Substring(placeholderIndex + 3);
            if (!label.StartsWith(prefix, StringComparison.Ordinal) ||
                !label.EndsWith(suffix, StringComparison.Ordinal) ||
                label.Length < prefix.Length + suffix.Length)
            {
                return false;
            }

            owner = label.Substring(
                    prefix.Length,
                    label.Length - prefix.Length - suffix.Length)
                .Trim();
            return !string.IsNullOrEmpty(owner);
        }

        private static string GetLocalizedOrganizationFilterFormat()
        {
            string localized = L10n.Tr(OrganizationFilterFormat);
            if (string.IsNullOrEmpty(localized) ||
                localized.IndexOf("{0}", StringComparison.Ordinal) < 0)
            {
                return OrganizationFilterFormat;
            }

            try
            {
                string.Format(localized, "owner");
                return localized;
            }
            catch (FormatException)
            {
                return OrganizationFilterFormat;
            }
        }

        internal static IReadOnlyList<string> BuildOrganizationFilterLabels(
            IEnumerable<string> owners)
        {
            var normalizedOwners =
                new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            if (owners != null)
            {
                foreach (string owner in owners)
                {
                    string normalizedOwner = owner?.Trim();
                    if (!string.IsNullOrEmpty(normalizedOwner))
                        normalizedOwners.Add(normalizedOwner);
                }
            }

            var labels = new List<string>(normalizedOwners.Count);
            foreach (string owner in normalizedOwners)
                labels.Add(CreateOrganizationFilterLabel(owner));
            return labels;
        }

        internal static bool IsSuccessfulCompleteOrganizationCatalogue(
            PackageManagerGitHubDiscoverySnapshot snapshot)
        {
            return snapshot != null &&
                   !snapshot.IsLoading &&
                   string.IsNullOrWhiteSpace(snapshot.ErrorMessage) &&
                   string.IsNullOrWhiteSpace(snapshot.CoverageWarningMessage) &&
                   snapshot.UnavailableManifestCount == 0 &&
                   snapshot.TotalOwners > 0 &&
                   snapshot.CompletedOwners >= snapshot.TotalOwners;
        }

        internal static bool ShouldPreserveOrganizationFilterState(
            PackageManagerGitHubDiscoverySnapshot snapshot)
        {
            // Native PageFilters removes selected values whenever their supported
            // category disappears. Until a complete catalogue proves an owner is
            // stale, retain the prior values across loading and recoverable gaps.
            return !IsSuccessfulCompleteOrganizationCatalogue(snapshot);
        }

        internal static IReadOnlyList<string> GetSupportedOrganizationFilters(
            object page)
        {
            var owners = new List<string>();
            PackageManagerGitHubDiscoverySnapshot snapshot =
                PackageManagerGitHubDiscovery.Current;
            IReadOnlyList<PackageManagerGitHubRepository> repositories =
                snapshot?.Repositories;
            if (repositories != null)
            {
                for (int index = 0; index < repositories.Count; index++)
                    owners.Add(repositories[index]?.Owner);
            }

            if (PackageManagerGitHubNativePresentationPatch.TryGetPageGroupNames(
                    page,
                    out IReadOnlyList<string> groupNames))
            {
                for (int index = 0; index < groupNames.Count; index++)
                {
                    if (TryGetOrganizationFilterOwner(
                            groupNames[index],
                            out string owner))
                    {
                        owners.Add(owner);
                    }
                }
            }

            if (ShouldPreserveOrganizationFilterState(snapshot))
            {
                AddOrganizationFilterOwners(
                    page,
                    PackageManagerGitHubNativePresentationPatch
                        .TryGetSupportedCategories,
                    owners);
                AddOrganizationFilterOwners(
                    page,
                    PackageManagerGitHubNativePresentationPatch
                        .TryGetSelectedCategories,
                    owners);
            }

            return BuildOrganizationFilterLabels(owners);
        }

        private static void AddOrganizationFilterOwners(
            object page,
            TryGetStringList tryGetValues,
            ICollection<string> owners)
        {
            if (tryGetValues == null ||
                owners == null ||
                !tryGetValues(page, out IReadOnlyList<string> values))
            {
                return;
            }

            for (int index = 0; index < values.Count; index++)
            {
                if (TryGetOrganizationFilterOwner(values[index], out string owner))
                    owners.Add(owner);
            }
        }

        private delegate bool TryGetStringList(
            object page,
            out IReadOnlyList<string> values);

        internal static bool TryConfigureFilters(object page)
        {
            if (page == null ||
                !string.Equals(
                    GetPropertyValue(page, "id") as string,
                    ExtensionPageId,
                    StringComparison.Ordinal))
            {
                return false;
            }

            try
            {
                MethodInfo filterTarget =
                    PackageManagerGitHubNativePresentationPatch
                        .GetPageVisibilityFilterTarget();
                MethodInfo filterPostfix =
                    PackageManagerGitHubNativePresentationPatch
                        .GetPageVisibilityFilterPostfix();
                MethodInfo supportedFiltersTarget =
                    PackageManagerGitHubNativePresentationPatch
                        .GetPageSupportedFiltersRefreshTarget();
                MethodInfo supportedFiltersPostfix =
                    PackageManagerGitHubNativePresentationPatch
                        .GetPageSupportedFiltersRefreshPostfix();
                if (filterTarget == null ||
                    filterPostfix == null ||
                    supportedFiltersTarget == null ||
                    supportedFiltersPostfix == null)
                {
                    return false;
                }

                bool areFiltersPatched =
                    PackageManagerGitHubNativePresentationPatch.IsPatchApplied(
                        filterTarget,
                        filterPostfix) &&
                    PackageManagerGitHubNativePresentationPatch.IsPatchApplied(
                        supportedFiltersTarget,
                        supportedFiltersPostfix);
                if (!areFiltersPatched)
                {
                    areFiltersPatched =
                        PackageManagerGitHubNativePresentationPatch.TryPatch() &&
                        PackageManagerGitHubNativePresentationPatch.IsPatchApplied(
                            filterTarget,
                            filterPostfix) &&
                        PackageManagerGitHubNativePresentationPatch.IsPatchApplied(
                            supportedFiltersTarget,
                            supportedFiltersPostfix);
                }
                if (!areFiltersPatched)
                    return false;

                MethodInfo updateSupportedLabels =
                    GetUpdateSupportedLabelsMethod();
                MethodInfo updateSupportedCategories =
                    GetUpdateSupportedCategoriesMethod();
                if (updateSupportedLabels == null ||
                    updateSupportedCategories == null ||
                    !updateSupportedLabels.DeclaringType.IsInstanceOfType(page) ||
                    !updateSupportedCategories.DeclaringType.IsInstanceOfType(page))
                {
                    return false;
                }

                updateSupportedLabels.Invoke(
                    page,
                    new object[] { SupportedVisibilityLabels, true });
                updateSupportedCategories.Invoke(
                    page,
                    new object[]
                    {
                        GetSupportedOrganizationFilters(page),
                        true
                    });
                TryApplyDefaultFilters(page);
                return true;
            }
            catch
            {
                return false;
            }
        }

        internal static bool TryApplyDefaultFilters(object page)
        {
            if (page == null)
                return false;

            try
            {
                GitSubmoduleManagerUserSettings settings =
                    GitSubmoduleManagerUserSettings.Instance;
                string preferenceSignature = BuildDefaultFilterPreferenceSignature(
                    settings.DefaultGitHubVisibility,
                    settings.DefaultGitHubOrganization);
                if (DefaultFiltersApplied.TryGetValue(
                        page,
                        out DefaultFilterMarker appliedMarker) &&
                    string.Equals(
                        appliedMarker.PreferenceSignature,
                        preferenceSignature,
                        StringComparison.Ordinal))
                {
                    return true;
                }

                PropertyInfo filtersProperty =
                    PackageManagerGitHubNativePresentationPatch
                        .GetPageFiltersProperty();
                object currentFilters = filtersProperty?.GetValue(page, null);
                if (currentFilters == null)
                    return false;

                PropertyInfo isFilterSetProperty = currentFilters.GetType()
                    .GetProperty("isFilterSet", AnyInstance);
                if (!(isFilterSetProperty?.GetValue(currentFilters, null) is
                      bool isFilterSet))
                {
                    return false;
                }

                if (isFilterSet)
                {
                    // A user/native selection always wins when Preferences
                    // change. Remember the new signature without rewriting it.
                    MarkDefaultFiltersApplied(page, preferenceSignature);
                    return true;
                }

                IReadOnlyList<string> supportedOrganizations =
                    string.IsNullOrEmpty(settings.DefaultGitHubOrganization)
                        ? Array.Empty<string>()
                        : GetSupportedOrganizationFilters(page);
                if (!TryResolveDefaultFilterSelection(
                        settings.DefaultGitHubVisibility,
                        settings.DefaultGitHubOrganization,
                        supportedOrganizations,
                        PackageManagerGitHubDiscovery.Current,
                        out string visibilityLabel,
                        out string organizationLabel))
                {
                    return false;
                }

                if (string.IsNullOrEmpty(visibilityLabel) &&
                    string.IsNullOrEmpty(organizationLabel))
                {
                    MarkDefaultFiltersApplied(page, preferenceSignature);
                    return true;
                }

                Type filtersType = currentFilters.GetType();
                ConstructorInfo copyConstructor = null;
                foreach (ConstructorInfo constructor in filtersType.GetConstructors(
                             AnyInstance))
                {
                    ParameterInfo[] parameters = constructor.GetParameters();
                    if (parameters.Length == 1 &&
                        parameters[0].ParameterType.IsInstanceOfType(
                            currentFilters))
                    {
                        copyConstructor = constructor;
                        break;
                    }
                }

                object nextFilters = copyConstructor?.Invoke(
                    new[] { currentFilters });
                if (nextFilters == null)
                    return false;

                MethodInfo updateLabels = filtersType.GetMethod(
                    "UpdateLabels",
                    AnyInstance,
                    null,
                    new[] { typeof(IReadOnlyList<string>) },
                    null);
                MethodInfo updateCategories = filtersType.GetMethod(
                    "UpdateCategories",
                    AnyInstance,
                    null,
                    new[] { typeof(IReadOnlyList<string>) },
                    null);
                if (updateLabels == null || updateCategories == null)
                    return false;

                updateLabels.Invoke(
                    nextFilters,
                    new object[]
                    {
                        string.IsNullOrEmpty(visibilityLabel)
                            ? Array.Empty<string>()
                            : new[] { visibilityLabel }
                    });
                updateCategories.Invoke(
                    nextFilters,
                    new object[]
                    {
                        string.IsNullOrEmpty(organizationLabel)
                            ? Array.Empty<string>()
                            : new[] { organizationLabel }
                    });

                MethodInfo updateFilters = null;
                for (Type type = page.GetType();
                     type != null && updateFilters == null;
                     type = type.BaseType)
                {
                    foreach (MethodInfo method in type.GetMethods(
                                 AnyInstance | BindingFlags.DeclaredOnly))
                    {
                        ParameterInfo[] parameters = method.GetParameters();
                        if (string.Equals(
                                method.Name,
                                "UpdateFilters",
                                StringComparison.Ordinal) &&
                            parameters.Length == 1 &&
                            parameters[0].ParameterType.IsInstanceOfType(
                                nextFilters))
                        {
                            updateFilters = method;
                            break;
                        }
                    }
                }

                if (updateFilters == null)
                    return false;

                updateFilters.Invoke(page, new[] { nextFilters });
                MarkDefaultFiltersApplied(page, preferenceSignature);
                return true;
            }
            catch
            {
                return false;
            }
        }

        internal static string GetDefaultVisibilityLabel(
            GitSubmoduleManagerDefaultVisibility visibility)
        {
            switch (GitSubmoduleManagerUserSettings
                        .NormalizeDefaultGitHubVisibility(visibility))
            {
                case GitSubmoduleManagerDefaultVisibility.Public:
                    return PackageManagerSubmodulePresentation
                        .PublicRepositoryTagLabel;
                case GitSubmoduleManagerDefaultVisibility.Private:
                    return PackageManagerSubmodulePresentation
                        .PrivateRepositoryTagLabel;
                default:
                    return string.Empty;
            }
        }

        internal static string BuildDefaultFilterPreferenceSignature(
            GitSubmoduleManagerDefaultVisibility visibility,
            string organization)
        {
            GitSubmoduleManagerDefaultVisibility normalizedVisibility =
                GitSubmoduleManagerUserSettings
                    .NormalizeDefaultGitHubVisibility(visibility);
            string normalizedOrganization = GitSubmoduleManagerUserSettings
                .NormalizeDefaultGitHubOrganization(organization)
                .ToLowerInvariant();
            return normalizedVisibility + "\n" + normalizedOrganization;
        }

        internal static bool TryResolveDefaultFilterSelection(
            GitSubmoduleManagerDefaultVisibility visibility,
            string organization,
            IReadOnlyList<string> supportedOrganizations,
            PackageManagerGitHubDiscoverySnapshot snapshot,
            out string visibilityLabel,
            out string organizationLabel)
        {
            visibilityLabel = string.Empty;
            organizationLabel = string.Empty;
            string normalizedOrganization = GitSubmoduleManagerUserSettings
                .NormalizeDefaultGitHubOrganization(organization);
            if (string.IsNullOrEmpty(normalizedOrganization))
            {
                visibilityLabel = GetDefaultVisibilityLabel(visibility);
                return true;
            }

            // An organization default is meaningful only after a complete scan.
            // Loading or incomplete/error snapshots cannot prove either presence
            // or absence, so defer both facets until a later successful terminal
            // snapshot. This avoids a partial visibility default becoming sticky.
            if (!IsSuccessfulCompleteOrganizationCatalogue(snapshot))
                return false;

            visibilityLabel = GetDefaultVisibilityLabel(visibility);

            if (supportedOrganizations != null)
            {
                for (int index = 0; index < supportedOrganizations.Count; index++)
                {
                    if (TryGetOrganizationFilterOwner(
                            supportedOrganizations[index],
                            out string owner) &&
                        string.Equals(
                            owner,
                            normalizedOrganization,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        organizationLabel = supportedOrganizations[index];
                        break;
                    }
                }
            }

            // A complete catalogue can prove that the configured organization has
            // no current packages. In that case the independent visibility default
            // remains useful and the unavailable organization is left unselected.
            return true;
        }

        private static void MarkDefaultFiltersApplied(
            object page,
            string preferenceSignature)
        {
            if (DefaultFiltersApplied.TryGetValue(
                    page,
                    out DefaultFilterMarker existingMarker))
            {
                existingMarker.PreferenceSignature = preferenceSignature;
                return;
            }

            try
            {
                DefaultFiltersApplied.Add(
                    page,
                    new DefaultFilterMarker
                    {
                        PreferenceSignature = preferenceSignature
                    });
            }
            catch (ArgumentException)
            {
                // Another registration path marked the same page first. Preserve
                // the newest normalized Preferences signature in that marker.
                if (DefaultFiltersApplied.TryGetValue(
                        page,
                        out existingMarker))
                {
                    existingMarker.PreferenceSignature = preferenceSignature;
                }
            }
        }

        internal static bool TryRegisterForRoot(
            object packageManagerRoot,
            out object pageManager,
            out object page)
        {
            pageManager = GetPageManager(packageManagerRoot);
            return TryRegister(pageManager, out page);
        }

        internal static bool TryActivate(object pageManager, object page = null)
        {
            if (pageManager == null)
                return false;

            try
            {
                page ??= FindRegisteredPage(pageManager) ??
                         FindPageById(pageManager, ExtensionPageId);
                PropertyInfo activePage = pageManager.GetType().GetProperty(
                    "activePage",
                    AnyInstance);
                if (page == null || activePage == null || !activePage.CanWrite)
                    return false;

                activePage.SetValue(pageManager, page, null);
                return ReferenceEquals(activePage.GetValue(pageManager, null), page);
            }
            catch
            {
                return false;
            }
        }

        internal static bool TryRelocateSidebarRow(
            VisualElement sidebar,
            out VisualElement nativeRow)
        {
            nativeRow = null;
            if (sidebar == null)
                return false;

            try
            {
                MethodInfo getRow = FindSidebarGetRowMethod(sidebar.GetType());
                if (getRow == null)
                    return false;

                VisualElement myAssetsRow = getRow.Invoke(
                    sidebar,
                    new object[] { MyAssetsPageId }) as VisualElement;
                VisualElement sourcesContainer = myAssetsRow?.parent;
                if (sourcesContainer == null)
                    return false;

                List<VisualElement> matchingRows = FindSidebarRows(
                    sidebar,
                    ExtensionPageId);
                nativeRow = ChooseCanonicalSidebarRow(
                    matchingRows,
                    sourcesContainer);
                if (nativeRow == null)
                    return false;

                foreach (VisualElement row in matchingRows)
                {
                    if (!ReferenceEquals(row, nativeRow))
                        row.RemoveFromHierarchy();
                }

                if (!ReferenceEquals(nativeRow.parent, sourcesContainer))
                    sourcesContainer.Add(nativeRow);

                nativeRow.name = NativeSidebarRowElementName;
                nativeRow.tooltip =
                    "Discover valid GitHub UPM packages and manage installed package submodules";
                ApplySidebarIcon(nativeRow);

                // A stale host row can survive a visual-tree rebuild until the
                // previous host session disposes. Keep only Unity's real page row.
                foreach (VisualElement legacyRow in FindSidebarRows(
                             sidebar,
                             LegacySidebarPageId))
                {
                    legacyRow.RemoveFromHierarchy();
                }

                return true;
            }
            catch
            {
                nativeRow = null;
                return false;
            }
        }

        internal static object GetPrimaryVersion(object package)
        {
            object versions = GetPropertyValue(package, "versions");
            return GetPropertyValue(versions, "primary");
        }

        internal static bool ShouldIncludePackage(object package)
        {
            if (PackageManagerGitHubPackageProjection.TryGetRepository(
                    package,
                    out _))
            {
                return true;
            }

            object primaryVersion = GetPrimaryVersion(package);
            if (PackageManagerSubmodulePresentation.TryGetPresentation(
                    primaryVersion,
                    out PackageManagerSubmoduleInfo info) &&
                info.IsGitHub)
            {
                return true;
            }

            return PackageManagerReadOnlyGitPackage.TryGetInfo(
                       package,
                       out PackageManagerReadOnlyGitInfo readOnlyInfo) &&
                   GitHubUtility.TryParseGitHubRepo(
                       readOnlyInfo.RepositoryUrl,
                       out _,
                       out _);
        }

        internal static bool IsDownloadedPackage(object package)
        {
            if (package == null ||
                PackageManagerGitHubPackageProjection.TryGetRepository(
                    package,
                    out _))
            {
                return false;
            }

            // Page membership has already admitted only exact GitHub submodules
            // and direct read-only Git dependencies. The primary-version flag is
            // therefore the cheapest authoritative distinction from projected
            // discovery placeholders.
            return PackageManagerSubmodulePresentation.TryGetVersionIdentity(
                       GetPrimaryVersion(package),
                       out _,
                       out _,
                       out bool isInstalled) &&
                   isInstalled;
        }

        internal static string GetGroupName(object package)
        {
            if (PackageManagerGitHubPackageProjection.TryGetRepository(
                    package,
                    out PackageManagerGitHubRepository repository))
            {
                return string.IsNullOrWhiteSpace(repository.Owner)
                    ? L10n.Tr("Organization")
                    : CreateOrganizationFilterLabel(repository.Owner);
            }

            object primaryVersion = GetPrimaryVersion(package);
            if (PackageManagerSubmodulePresentation.TryGetPresentation(
                    primaryVersion,
                    out PackageManagerSubmoduleInfo submoduleInfo))
            {
                string repositoryOwner = GetGitHubRepositoryOwner(submoduleInfo);
                if (!string.IsNullOrWhiteSpace(repositoryOwner))
                    return CreateOrganizationFilterLabel(repositoryOwner);
            }

            if (PackageManagerReadOnlyGitPackage.TryGetInfo(
                    package,
                    out PackageManagerReadOnlyGitInfo readOnlyInfo) &&
                GitHubUtility.TryParseGitHubRepo(
                    readOnlyInfo.RepositoryUrl,
                    out string readOnlyOwner,
                    out _))
            {
                return string.IsNullOrWhiteSpace(readOnlyOwner)
                    ? L10n.Tr("Organization")
                    : CreateOrganizationFilterLabel(readOnlyOwner);
            }

            object author = GetPropertyValue(primaryVersion, "author");
            string authorName = GetPropertyValue(author, "name") as string;
            return string.IsNullOrWhiteSpace(authorName)
                ? L10n.Tr("Organization")
                : CreateOrganizationFilterLabel(authorName);
        }

        internal static string GetGitHubRepositoryOwner(
            PackageManagerSubmoduleInfo submoduleInfo)
        {
            if (submoduleInfo == null ||
                !submoduleInfo.IsGitHub ||
                !GitHubUtility.TryParseGitHubRepo(
                    submoduleInfo.RepositoryUrl,
                    out string repositoryOwner,
                    out _))
            {
                return string.Empty;
            }

            return repositoryOwner?.Trim() ?? string.Empty;
        }

        internal static string GetGitHubRepositoryOwner(object package)
        {
            if (PackageManagerGitHubPackageProjection.TryGetRepository(
                    package,
                    out PackageManagerGitHubRepository repository))
            {
                return repository.Owner?.Trim() ?? string.Empty;
            }

            object primaryVersion = GetPrimaryVersion(package);
            if (PackageManagerSubmodulePresentation.TryGetPresentation(
                    primaryVersion,
                    out PackageManagerSubmoduleInfo submoduleInfo))
            {
                string submoduleOwner =
                    GetGitHubRepositoryOwner(submoduleInfo);
                if (!string.IsNullOrEmpty(submoduleOwner))
                    return submoduleOwner;
            }

            if (PackageManagerReadOnlyGitPackage.TryGetInfo(
                    package,
                    out PackageManagerReadOnlyGitInfo readOnlyInfo) &&
                GitHubUtility.TryParseGitHubRepo(
                    readOnlyInfo.RepositoryUrl,
                    out string readOnlyOwner,
                    out _))
            {
                return readOnlyOwner?.Trim() ?? string.Empty;
            }

            return string.Empty;
        }

        internal static Type FindLoadedType(string fullName)
        {
            if (string.IsNullOrWhiteSpace(fullName))
                return null;

            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    Type type = assembly.GetType(fullName, false);
                    if (type != null)
                        return type;
                }
                catch
                {
                    // Optional Editor modules can be partially loaded.
                }
            }

            return null;
        }

        private static bool HasRequiredArgsFields(Type argsType)
        {
            string[] requiredFields =
            {
                "name",
                "displayName",
                "icon",
                "priority",
                "refreshOptions",
                "capability",
                "supportedStatusFilters",
                "supportedSortOptions",
                "filter",
                "getGroupName",
                "compareGroup"
            };
            foreach (string fieldName in requiredFields)
            {
                if (argsType.GetField(fieldName, AnyInstance) == null)
                    return false;
            }

            FieldInfo supportedStatusFilters = argsType.GetField(
                "supportedStatusFilters",
                AnyInstance);
            Type statusType = supportedStatusFilters?.FieldType.GetElementType();
            return statusType != null &&
                   statusType.IsEnum &&
                   Enum.IsDefined(statusType, DownloadedFilterStatusName);
        }

        private static MethodInfo FindResolveMethod(Type servicesType)
        {
            foreach (MethodInfo method in servicesType.GetMethods(AnyInstance))
            {
                if (method.Name == "Resolve" &&
                    method.IsGenericMethodDefinition &&
                    method.GetGenericArguments().Length == 1 &&
                    method.GetParameters().Length == 0)
                {
                    return method;
                }
            }

            return null;
        }

        private static MethodInfo FindAddExtensionPageMethod(
            Type pageManagerType,
            Type argsType)
        {
            foreach (MethodInfo method in pageManagerType.GetMethods(AnyInstance))
            {
                ParameterInfo[] parameters = method.GetParameters();
                if (method.Name == AddExtensionPageMethodName &&
                    method.ReturnType == typeof(void) &&
                    parameters.Length == 1 &&
                    parameters[0].ParameterType == argsType)
                {
                    return method;
                }
            }

            return null;
        }

        private static MethodInfo FindGetPageMethod(Type pageManagerType)
        {
            if (pageManagerType == null)
                return null;

            foreach (MethodInfo method in pageManagerType.GetMethods(AnyInstance))
            {
                ParameterInfo[] parameters = method.GetParameters();
                if (method.Name == GetPageMethodName &&
                    !method.IsGenericMethod &&
                    parameters.Length == 1 &&
                    parameters[0].ParameterType == typeof(string))
                {
                    return method;
                }
            }

            return null;
        }

        internal static MethodInfo FindSidebarExtensionRowsUpdateMethod(
            Type sidebarType)
        {
            return sidebarType?.GetMethod(
                SidebarExtensionRowsUpdateMethodName,
                AnyInstance,
                null,
                Type.EmptyTypes,
                null);
        }

        internal static VisualElement ChooseCanonicalSidebarRow(
            IReadOnlyList<VisualElement> rows,
            VisualElement preferredParent)
        {
            if (rows == null || rows.Count == 0)
                return null;

            foreach (VisualElement row in rows)
            {
                if (row != null && ReferenceEquals(row.parent, preferredParent))
                    return row;
            }

            foreach (VisualElement row in rows)
            {
                if (row != null)
                    return row;
            }

            return null;
        }

        private static MethodInfo FindSidebarGetRowMethod(Type sidebarType)
        {
            return sidebarType?.GetMethod(
                "GetRow",
                AnyInstance,
                null,
                new[] { typeof(string) },
                null);
        }

        private static List<VisualElement> FindSidebarRows(
            VisualElement root,
            string pageId)
        {
            var result = new List<VisualElement>();
            if (root == null || string.IsNullOrEmpty(pageId))
                return result;

            var stack = new Stack<VisualElement>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                VisualElement element = stack.Pop();
                if (string.Equals(
                        element.GetType().FullName,
                        SidebarRowTypeName,
                        StringComparison.Ordinal) &&
                    string.Equals(
                        GetPropertyValue(element, "pageId") as string,
                        pageId,
                        StringComparison.Ordinal))
                {
                    result.Add(element);
                }

                for (int index = 0; index < element.childCount; index++)
                    stack.Push(element[index]);
            }

            return result;
        }

        private static object FindRegisteredPage(object pageManager)
        {
            if (!(GetPropertyValue(pageManager, "orderedExtensionPages") is
                  IEnumerable pages))
            {
                return null;
            }

            foreach (object page in pages)
            {
                if (string.Equals(
                        GetPropertyValue(page, "id") as string,
                        ExtensionPageId,
                        StringComparison.Ordinal))
                {
                    return page;
                }
            }

            return null;
        }

        private static object FindPageById(object pageManager, string pageId)
        {
            MethodInfo getPage = FindGetPageMethod(pageManager?.GetType());
            return getPage?.Invoke(pageManager, new object[] { pageId });
        }

        private static void SetField(object instance, string fieldName, object value)
        {
            FieldInfo field = instance.GetType().GetField(fieldName, AnyInstance);
            if (field == null || (value != null && !field.FieldType.IsInstanceOfType(value)))
            {
                throw new MissingFieldException(instance.GetType().FullName, fieldName);
            }

            field.SetValue(instance, value);
        }

        private static void SetEnumField(
            object instance,
            string fieldName,
            string valueName)
        {
            FieldInfo field = instance.GetType().GetField(fieldName, AnyInstance);
            if (field == null || !field.FieldType.IsEnum)
                throw new MissingFieldException(instance.GetType().FullName, fieldName);

            field.SetValue(instance, Enum.Parse(field.FieldType, valueName));
        }

        private static void SetEnumFlagsField(
            object instance,
            string fieldName,
            params string[] valueNames)
        {
            FieldInfo field = instance.GetType().GetField(fieldName, AnyInstance);
            if (field == null || !field.FieldType.IsEnum)
                throw new MissingFieldException(instance.GetType().FullName, fieldName);

            ulong value = 0;
            foreach (string valueName in valueNames)
            {
                if (Enum.IsDefined(field.FieldType, valueName))
                {
                    value |= Convert.ToUInt64(
                        Enum.Parse(field.FieldType, valueName));
                }
            }

            field.SetValue(instance, Enum.ToObject(field.FieldType, value));
        }

        private static void SetEnumArray(
            object instance,
            string fieldName,
            params string[] valueNames)
        {
            FieldInfo field = instance.GetType().GetField(fieldName, AnyInstance);
            Type elementType = field?.FieldType.GetElementType();
            if (field == null || elementType == null || !elementType.IsEnum)
                throw new MissingFieldException(instance.GetType().FullName, fieldName);

            Array values = Array.CreateInstance(elementType, valueNames.Length);
            for (int index = 0; index < valueNames.Length; index++)
                values.SetValue(Enum.Parse(elementType, valueNames[index]), index);
            field.SetValue(instance, values);
        }

        private static int CompareGroupNames(string left, string right)
        {
            return string.Compare(left, right, StringComparison.OrdinalIgnoreCase);
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

        private static void ApplySidebarIcon(VisualElement row)
        {
            VisualElement iconElement = FindElementByName(row, "sidebarIcon") ??
                                        FindElementByClass(row, "sidebarIcon");
            Texture2D icon = GitSubmoduleManagerIcons.GitIcon;
            if (iconElement != null && icon != null)
                iconElement.style.backgroundImage = new StyleBackground(icon);
        }

        private static VisualElement FindElementByName(
            VisualElement root,
            string elementName)
        {
            if (root == null)
                return null;
            if (string.Equals(root.name, elementName, StringComparison.Ordinal))
                return root;

            foreach (VisualElement child in root.Children())
            {
                VisualElement match = FindElementByName(child, elementName);
                if (match != null)
                    return match;
            }

            return null;
        }

        private static VisualElement FindElementByClass(
            VisualElement root,
            string className)
        {
            if (root == null)
                return null;
            if (root.ClassListContains(className))
                return root;

            foreach (VisualElement child in root.Children())
            {
                VisualElement match = FindElementByClass(child, className);
                if (match != null)
                    return match;
            }

            return null;
        }
    }
}
