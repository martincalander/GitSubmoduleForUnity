using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEngine;
using UnityEngine.UIElements;

namespace MartinCalander.GitSubmoduleManager.Editor
{
    /// <summary>
    /// Optional Unity-6 native details, refresh, and filter hooks for discovered
    /// package placeholders. These are kept separate from installed-submodule
    /// labeling so older Package Manager versions can omit them independently.
    /// </summary>
    [InitializeOnLoad]
    internal static class PackageManagerGitHubNativePresentationPatch
    {
        internal const string HarmonyId =
            "com.martincalander.gitsubmodulemanager.package-manager-discovery";
        internal const string TechnicalNameCardTypeName =
            "UnityEditor.PackageManager.UI.Internal.TechnicalNameInfoCard";
        internal const string PackageAuthorLabelTypeName =
            "UnityEditor.PackageManager.UI.Internal.PackageAuthorLabel";
        internal const string BasePackageVersionTypeName =
            "UnityEditor.PackageManager.UI.Internal.BasePackageVersion";
        internal const string PackageDetailsDependenciesTabTypeName =
            "UnityEditor.PackageManager.UI.Internal.PackageDetailsDependenciesTab";
        internal const string PackageDetailsDetailsTabTypeName =
            "UnityEditor.PackageManager.UI.Internal.PackageDetailsDetailsTab";
        internal const string PackageLinkFactoryTypeName =
            "UnityEditor.PackageManager.UI.Internal.PackageLinkFactory";
        internal const string PackageLinkTypeName =
            "UnityEditor.PackageManager.UI.Internal.PackageLink";
        internal const string PageRefreshHandlerTypeName =
            "UnityEditor.PackageManager.UI.Internal.PageRefreshHandler";
        internal const string PackageStatusBarTypeName =
            "UnityEditor.PackageManager.UI.Internal.PackageStatusBar";
        internal const string ListAreaTypeName =
            "UnityEditor.PackageManager.UI.Internal.ListArea";
        internal const string PackageManagerWindowRootTypeName =
            "UnityEditor.PackageManager.UI.Internal.PackageManagerWindowRoot";
        internal const string PackageVersionInterfaceTypeName =
            "UnityEditor.PackageManager.UI.Internal.IPackageVersion";
        internal const string PageInterfaceTypeName =
            "UnityEditor.PackageManager.UI.Internal.IPage";
        internal const string SimplePageWithPackagesTypeName =
            "UnityEditor.PackageManager.UI.Internal.SimplePageWithPackages";
        internal const string LegacyExtensionPageTypeName =
            "UnityEditor.PackageManager.UI.Internal.ExtensionPage";
        internal const string LegacyFiltersWindowTypeName =
            "UnityEditor.PackageManager.UI.Internal.PackageManagerFiltersWindow";
        internal const string LegacyUpmFiltersWindowTypeName =
            "UnityEditor.PackageManager.UI.Internal.UpmFiltersWindow";
        internal const string LegacyDetailsTabTypeName =
            "UnityEditor.PackageManager.UI.Internal.PackageDetailsDescriptionTab";
        internal const string DetailsInformationCardsContainerName =
            "detailInformationCardsContainer";
        internal const string LegacyDetailsInformationCardsContainerName =
            "git-submodule-manager-legacy-information-cards";
        internal const string LegacyVisibilityFoldoutName =
            "git-submodule-manager-legacy-visibility-filters";
        internal const string LegacyOrganizationFoldoutName =
            "git-submodule-manager-legacy-organization-filters";
        internal const string LicenseInformationCardName =
            "git-submodule-manager-license-information-card";
        internal const string DefaultBranchInformationCardName =
            "git-submodule-manager-default-branch-information-card";
        internal const string InformationCardClassName = "informationCard";
        internal const string InformationCardSmallClassName = "small";
        internal const string InformationCardTitleClassName =
            "informationCardTitle";
        internal const string InformationCardContentClassName =
            "informationCardContent";

        private const BindingFlags AnyInstance =
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private const BindingFlags AnyStatic =
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        private static Harmony harmony;
        private static readonly object PageVisibilityFilterContractGate =
            new object();
        private static readonly object LegacyPageFilterContractGate =
            new object();
        private static volatile PageVisibilityFilterContract
            pageVisibilityFilterContract;
        private static volatile LegacyPageFilterContract legacyPageFilterContract;
        private static bool shuttingDown;
        private static bool presentationRefreshQueued;
        private static bool presentationRebuildQueued;
        private static bool forcedPresentationRebuildQueued;
        private static bool allPackageManagerPagesRefreshQueued;
        private static IReadOnlyList<PackageManagerGitHubRepository>
            observedRepositories =
                PackageManagerGitHubDiscovery.Current.Repositories;
        static PackageManagerGitHubNativePresentationPatch()
        {
            if (!PackageManagerUnityVersionSupport.IsCurrentVersionSupported)
                return;

            PackageManagerGitHubDiscovery.SnapshotChanged +=
                OnDiscoverySnapshotChanged;
            PackageManagerSubmoduleSnapshot.SnapshotChanged +=
                OnSubmoduleSnapshotChanged;
            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;
            EditorApplication.delayCall += TryPatchDelayed;
        }

        internal static bool TryPatch()
        {
            if (shuttingDown ||
                !PackageManagerUnityVersionSupport.IsCurrentVersionSupported)
                return false;

            try
            {
                harmony ??= new Harmony(HarmonyId);
                bool foundTarget = false;
                foreach (MethodInfo target in GetTechnicalNameTargets())
                {
                    foundTarget = true;
                    PatchPostfixIfNeeded(target, GetTechnicalNamePostfix());
                }

                foreach (MethodInfo target in GetAuthorTargets())
                {
                    foundTarget = true;
                    PatchPostfixIfNeeded(target, GetAuthorPostfix());
                }

                MethodInfo dependenciesGetter = GetDependenciesGetterTarget();
                if (dependenciesGetter != null)
                {
                    foundTarget = true;
                    PatchPostfixIfNeeded(
                        dependenciesGetter,
                        GetDependenciesGetterPostfix());
                }

                foreach (MethodInfo target in GetDependenciesTabValidityTargets())
                {
                    foundTarget = true;
                    PatchPostfixIfNeeded(
                        target,
                        GetDependenciesTabValidityPostfix());
                }

                MethodInfo detailsInformationCardsTarget =
                    GetDetailsInformationCardsTarget();
                if (detailsInformationCardsTarget != null)
                {
                    foundTarget = true;
                    PatchPostfixIfNeeded(
                        detailsInformationCardsTarget,
                        GetDetailsInformationCardsPostfix());
                }

                foreach (MethodInfo target in GetPackageLinkTargets())
                {
                    foundTarget = true;
                    PatchPostfixIfNeeded(target, GetPackageLinkPostfix());
                }

                foreach (MethodInfo target in GetPageRefreshTargets())
                {
                    foundTarget = true;
                    PatchPostfixIfNeeded(target, GetPageRefreshPostfix());
                }

                foreach (MethodInfo target in GetPageActivationTargets())
                {
                    foundTarget = true;
                    PatchPostfixIfNeeded(target, GetPageActivationPostfix());
                }

                foreach (MethodInfo target in GetPageLoadingTargets())
                {
                    foundTarget = true;
                    PatchPostfixIfNeeded(target, GetPageLoadingPostfix());
                }

                TryGetPageVisibilityFilterContract(
                    out PageVisibilityFilterContract visibilityFilterContract);
                MethodInfo visibilityFilterTarget =
                    visibilityFilterContract?.VisibilityFilterTarget;
                if (visibilityFilterTarget != null)
                {
                    foundTarget = true;
                    PatchPostfixIfNeeded(
                        visibilityFilterTarget,
                        GetPageVisibilityFilterPostfix());
                }

                MethodInfo supportedFiltersRefreshTarget =
                    visibilityFilterContract?.SupportedFiltersRefreshTarget ??
                    GetPageSupportedFiltersRefreshTarget();
                if (supportedFiltersRefreshTarget != null)
                {
                    foundTarget = true;
                    PatchPostfixIfNeeded(
                        supportedFiltersRefreshTarget,
                        GetPageSupportedFiltersRefreshPostfix());
                }

                if (TryGetLegacyPageFilterContract(
                        out LegacyPageFilterContract legacyFilterContract))
                {
                    foundTarget = true;
                    PatchPostfixIfNeeded(
                        legacyFilterContract.VisibilityFilterTarget,
                        GetLegacyPageVisibilityFilterPostfix());
                    PatchPostfixIfNeeded(
                        legacyFilterContract.FiltersDisplayTarget,
                        GetLegacyFiltersDisplayPostfix());
                    PatchPostfixIfNeeded(
                        legacyFilterContract.FiltersSizeTarget,
                        GetLegacyFiltersSizePostfix());
                }

                return foundTarget;
            }
            catch
            {
                return false;
            }
        }

        internal static IReadOnlyList<MethodInfo> GetTechnicalNameTargets()
        {
            return FindSingleArgumentRefreshTargets(
                TechnicalNameCardTypeName,
                PackageVersionInterfaceTypeName);
        }

        internal static IReadOnlyList<MethodInfo> GetAuthorTargets()
        {
            return FindSingleArgumentRefreshTargets(
                PackageAuthorLabelTypeName,
                PackageVersionInterfaceTypeName);
        }

        internal static MethodInfo GetDependenciesGetterTarget()
        {
            Type type = PackageManagerSubmoduleHarmonyPatch.FindLoadedType(
                BasePackageVersionTypeName);
            PropertyInfo property = type?.GetProperty("dependencies", AnyInstance);
            MethodInfo getter = property?.GetGetMethod(true);
            return getter != null &&
                   !getter.IsStatic &&
                   getter.GetParameters().Length == 0 &&
                   getter.ReturnType == typeof(DependencyInfo[])
                ? getter
                : null;
        }

        internal static IReadOnlyList<MethodInfo> GetDependenciesTabValidityTargets()
        {
            return FindSingleArgumentTargets(
                PackageDetailsDependenciesTabTypeName,
                "IsValid",
                typeof(bool),
                PackageVersionInterfaceTypeName);
        }

        internal static MethodInfo GetDetailsInformationCardsTarget()
        {
            IReadOnlyList<MethodInfo> targets = FindSingleArgumentTargets(
                PackageDetailsDetailsTabTypeName,
                "RefreshContent",
                typeof(void),
                PackageVersionInterfaceTypeName);
            if (targets.Count == 1 &&
                string.Equals(
                    targets[0].DeclaringType?.FullName,
                    PackageDetailsDetailsTabTypeName,
                    StringComparison.Ordinal))
            {
                return targets[0];
            }

            targets = FindSingleArgumentTargets(
                LegacyDetailsTabTypeName,
                "RefreshContent",
                typeof(void),
                PackageVersionInterfaceTypeName);
            return targets.Count == 1 &&
                   string.Equals(
                       targets[0].DeclaringType?.FullName,
                       LegacyDetailsTabTypeName,
                       StringComparison.Ordinal)
                ? targets[0]
                : null;
        }

        internal static IReadOnlyList<MethodInfo> GetPackageLinkTargets()
        {
            var matches = new List<MethodInfo>();
            Type type = PackageManagerSubmoduleHarmonyPatch.FindLoadedType(
                PackageLinkFactoryTypeName);
            if (type == null)
                return matches;

            var methodNames = new HashSet<string>(StringComparer.Ordinal)
            {
                "CreateUpmDocumentationLink",
                "CreateUpmChangelogLink",
                "CreateUpmLicenseLink"
            };
            foreach (MethodInfo method in type.GetMethods(AnyInstance))
            {
                ParameterInfo[] parameters = method.GetParameters();
                if (methodNames.Contains(method.Name) &&
                    !method.IsStatic &&
                    string.Equals(
                        method.ReturnType.FullName,
                        PackageLinkTypeName,
                        StringComparison.Ordinal) &&
                    parameters.Length == 1 &&
                    string.Equals(
                        parameters[0].ParameterType.FullName,
                        PackageVersionInterfaceTypeName,
                        StringComparison.Ordinal))
                {
                    matches.Add(method);
                }
            }

            return matches;
        }

        internal static IReadOnlyList<MethodInfo> GetPageRefreshTargets()
        {
            return FindSingleArgumentTargets(
                PageRefreshHandlerTypeName,
                "Refresh",
                typeof(void),
                PageInterfaceTypeName);
        }

        internal static IReadOnlyList<MethodInfo> GetPageActivationTargets()
        {
            return FindSingleArgumentTargets(
                PageRefreshHandlerTypeName,
                "OnActivePageChanged",
                typeof(void),
                PageInterfaceTypeName);
        }

        internal static IReadOnlyList<MethodInfo> GetPageLoadingTargets()
        {
            return FindSingleArgumentTargets(
                PageRefreshHandlerTypeName,
                "IsRefreshInProgress",
                typeof(bool),
                PageInterfaceTypeName);
        }

        internal static MethodInfo GetPageVisibilityFilterTarget()
        {
            Type type = PackageManagerSubmoduleHarmonyPatch.FindLoadedType(
                SimplePageWithPackagesTypeName);
            MethodInfo method = type?.GetMethod(
                "MatchesSearchTextAndFilter",
                AnyInstance,
                null,
                new[] { typeof(string) },
                null);
            return method != null &&
                   method.DeclaringType == type &&
                   !method.IsStatic &&
                   method.ReturnType == typeof(bool)
                ? method
                : null;
        }

        internal static MethodInfo GetPageSupportedFiltersRefreshTarget()
        {
            Type type = PackageManagerSubmoduleHarmonyPatch.FindLoadedType(
                PackageManagerSubmoduleNativePage.BasePageTypeName);
            MethodInfo method = type?.GetMethod(
                "UpdateSupportedFiltersAsync",
                AnyInstance,
                null,
                Type.EmptyTypes,
                null);
            return method != null &&
                   method.DeclaringType == type &&
                   !method.IsStatic &&
                   method.ReturnType == typeof(void)
                ? method
                : null;
        }

        internal static FieldInfo GetPagePackageDatabaseField()
        {
            Type type = PackageManagerSubmoduleHarmonyPatch.FindLoadedType(
                SimplePageWithPackagesTypeName);
            for (Type current = type; current != null; current = current.BaseType)
            {
                FieldInfo field = current.GetField(
                    "m_PackageDatabase",
                    AnyInstance | BindingFlags.DeclaredOnly);
                if (field != null && !field.IsStatic)
                    return field;
            }

            return null;
        }

        internal static MethodInfo GetPagePackageLookupMethod()
        {
            Type packageDatabaseType = GetPagePackageDatabaseField()?.FieldType;
            MethodInfo method = packageDatabaseType?.GetMethod(
                "GetPackage",
                AnyInstance,
                null,
                new[] { typeof(string) },
                null);
            return method != null &&
                   !method.IsStatic &&
                   method.ReturnType != typeof(void)
                ? method
                : null;
        }

        internal static PropertyInfo GetPageFiltersProperty()
        {
            Type basePageType = PackageManagerSubmoduleHarmonyPatch.FindLoadedType(
                PackageManagerSubmoduleNativePage.BasePageTypeName);
            PropertyInfo property = basePageType?.GetProperty("filters", AnyInstance);
            return property != null &&
                   property.CanRead &&
                   property.GetIndexParameters().Length == 0
                ? property
                : null;
        }

        internal static PropertyInfo GetPageFilterLabelsProperty()
        {
            Type filtersType = GetPageFiltersProperty()?.PropertyType;
            PropertyInfo property = filtersType?.GetProperty("labels", AnyInstance);
            return property != null &&
                   property.CanRead &&
                   property.GetIndexParameters().Length == 0 &&
                   typeof(IReadOnlyList<string>).IsAssignableFrom(
                       property.PropertyType)
                ? property
                : null;
        }

        internal static PropertyInfo GetPageFilterStatusProperty()
        {
            Type filtersType = GetPageFiltersProperty()?.PropertyType;
            PropertyInfo property = filtersType?.GetProperty("status", AnyInstance);
            return property != null &&
                   property.CanRead &&
                   property.GetIndexParameters().Length == 0 &&
                   property.PropertyType.IsEnum &&
                   Enum.IsDefined(
                       property.PropertyType,
                       PackageManagerSubmoduleNativePage
                           .DownloadedFilterStatusName)
                ? property
                : null;
        }

        internal static PropertyInfo GetPageFilterCategoriesProperty()
        {
            Type filtersType = GetPageFiltersProperty()?.PropertyType;
            PropertyInfo property = filtersType?.GetProperty(
                "categories",
                AnyInstance);
            return property != null &&
                   property.CanRead &&
                   property.GetIndexParameters().Length == 0 &&
                   typeof(IReadOnlyList<string>).IsAssignableFrom(
                       property.PropertyType)
                ? property
                : null;
        }

        internal static PropertyInfo GetPageFilterSupportedCategoriesProperty()
        {
            Type filtersType = GetPageFiltersProperty()?.PropertyType;
            PropertyInfo property = filtersType?.GetProperty(
                "supportedCategories",
                AnyInstance);
            return property != null &&
                   property.CanRead &&
                   property.GetIndexParameters().Length == 0 &&
                   typeof(IReadOnlyList<string>).IsAssignableFrom(
                       property.PropertyType)
                ? property
                : null;
        }

        internal static PropertyInfo GetPageVisualStatesProperty()
        {
            Type basePageType = PackageManagerSubmoduleHarmonyPatch.FindLoadedType(
                PackageManagerSubmoduleNativePage.BasePageTypeName);
            PropertyInfo property = basePageType?.GetProperty(
                "visualStates",
                AnyInstance);
            return property != null &&
                   property.CanRead &&
                   property.GetIndexParameters().Length == 0
                ? property
                : null;
        }

        internal static PropertyInfo GetPageOrderedGroupNamesProperty()
        {
            Type visualStatesType = GetPageVisualStatesProperty()?.PropertyType;
            PropertyInfo property = visualStatesType?.GetProperty(
                "orderedGroupNames",
                AnyInstance);
            return property != null &&
                   property.CanRead &&
                   property.GetIndexParameters().Length == 0 &&
                   typeof(IEnumerable<string>).IsAssignableFrom(
                       property.PropertyType)
                ? property
                : null;
        }

        internal static bool HasPageVisibilityFilterContract()
        {
            return TryGetPageVisibilityFilterContract(out _);
        }

        internal static MethodInfo GetTechnicalNamePostfix()
        {
            return typeof(PackageManagerGitHubNativePresentationPatch).GetMethod(
                nameof(TechnicalNameRefreshPostfix),
                AnyStatic);
        }

        internal static MethodInfo GetAuthorPostfix()
        {
            return typeof(PackageManagerGitHubNativePresentationPatch).GetMethod(
                nameof(AuthorRefreshPostfix),
                AnyStatic);
        }

        internal static MethodInfo GetDependenciesGetterPostfix()
        {
            return typeof(PackageManagerGitHubNativePresentationPatch).GetMethod(
                nameof(DependenciesGetterPostfix),
                AnyStatic);
        }

        internal static MethodInfo GetDependenciesTabValidityPostfix()
        {
            return typeof(PackageManagerGitHubNativePresentationPatch).GetMethod(
                nameof(DependenciesTabValidityPostfix),
                AnyStatic);
        }

        internal static MethodInfo GetDetailsInformationCardsPostfix()
        {
            return typeof(PackageManagerGitHubNativePresentationPatch).GetMethod(
                nameof(DetailsInformationCardsPostfix),
                AnyStatic);
        }

        internal static MethodInfo GetPackageLinkPostfix()
        {
            return typeof(PackageManagerGitHubNativePresentationPatch).GetMethod(
                nameof(PackageLinkPostfix),
                AnyStatic);
        }

        internal static MethodInfo GetPageRefreshPostfix()
        {
            return typeof(PackageManagerGitHubNativePresentationPatch).GetMethod(
                nameof(PageRefreshPostfix),
                AnyStatic);
        }

        internal static MethodInfo GetPageActivationPostfix()
        {
            return typeof(PackageManagerGitHubNativePresentationPatch).GetMethod(
                nameof(PageActivationPostfix),
                AnyStatic);
        }

        internal static MethodInfo GetPageLoadingPostfix()
        {
            return typeof(PackageManagerGitHubNativePresentationPatch).GetMethod(
                nameof(PageLoadingPostfix),
                AnyStatic);
        }

        internal static MethodInfo GetPageVisibilityFilterPostfix()
        {
            return typeof(PackageManagerGitHubNativePresentationPatch).GetMethod(
                nameof(PageVisibilityFilterPostfix),
                AnyStatic);
        }

        internal static MethodInfo GetPageSupportedFiltersRefreshPostfix()
        {
            return typeof(PackageManagerGitHubNativePresentationPatch).GetMethod(
                nameof(PageSupportedFiltersRefreshPostfix),
                AnyStatic);
        }

        internal static MethodInfo GetLegacyPageVisibilityFilterPostfix()
        {
            return typeof(PackageManagerGitHubNativePresentationPatch).GetMethod(
                nameof(LegacyPageVisibilityFilterPostfix),
                AnyStatic);
        }

        internal static MethodInfo GetLegacyFiltersDisplayPostfix()
        {
            return typeof(PackageManagerGitHubNativePresentationPatch).GetMethod(
                nameof(LegacyFiltersDisplayPostfix),
                AnyStatic);
        }

        internal static MethodInfo GetLegacyFiltersSizePostfix()
        {
            return typeof(PackageManagerGitHubNativePresentationPatch).GetMethod(
                nameof(LegacyFiltersSizePostfix),
                AnyStatic);
        }

        internal static MethodInfo GetPackageStatusUpdateMethod()
        {
            return PackageManagerSubmoduleHarmonyPatch
                .FindLoadedType(PackageStatusBarTypeName)
                ?.GetMethod(
                    "UpdateStatusMessage",
                    AnyInstance,
                    null,
                    Type.EmptyTypes,
                    null);
        }

        internal static MethodInfo GetListAreaRebuildMethod()
        {
            Type listAreaType = PackageManagerSubmoduleHarmonyPatch.FindLoadedType(
                ListAreaTypeName);
            Type pageType = PackageManagerSubmoduleHarmonyPatch.FindLoadedType(
                PageInterfaceTypeName);
            return listAreaType == null || pageType == null
                ? null
                : listAreaType.GetMethod(
                    "OnListRebuild",
                    AnyInstance,
                    null,
                    new[] { pageType },
                    null);
        }

        internal static PropertyInfo GetPackageStatusBarProperty()
        {
            Type rootType = PackageManagerSubmoduleHarmonyPatch.FindLoadedType(
                PackageManagerWindowRootTypeName);
            PropertyInfo property = rootType?.GetProperty(
                "packageStatusbar",
                AnyInstance);
            return property != null &&
                   string.Equals(
                       property.PropertyType.FullName,
                       PackageStatusBarTypeName,
                       StringComparison.Ordinal)
                ? property
                : null;
        }

        internal static bool IsPatchApplied(MethodBase target, MethodInfo postfix)
        {
            if (target == null || postfix == null)
                return false;

            try
            {
                Patches patches = Harmony.GetPatchInfo(target);
                if (patches == null)
                    return false;
                foreach (Patch patch in patches.Postfixes)
                {
                    if (patch.owner == HarmonyId && patch.PatchMethod == postfix)
                        return true;
                }
            }
            catch
            {
                return false;
            }

            return false;
        }

        internal static bool ShouldReportDiscoveryLoading(
            string pageId,
            bool nativeIsRefreshing,
            bool discoveryIsLoading)
        {
            return nativeIsRefreshing ||
                   discoveryIsLoading &&
                   string.Equals(
                       pageId,
                       PackageManagerSubmoduleNativePage.ExtensionPageId,
                       StringComparison.Ordinal);
        }

        internal static bool MatchesRepositoryVisibility(
            bool? isPrivate,
            IReadOnlyList<string> selectedLabels)
        {
            bool publicSelected = false;
            bool privateSelected = false;
            if (selectedLabels != null)
            {
                for (int index = 0; index < selectedLabels.Count; index++)
                {
                    string label = selectedLabels[index];
                    publicSelected |= string.Equals(
                        label,
                        PackageManagerSubmodulePresentation.PublicRepositoryTagLabel,
                        StringComparison.Ordinal);
                    privateSelected |= string.Equals(
                        label,
                        PackageManagerSubmodulePresentation.PrivateRepositoryTagLabel,
                        StringComparison.Ordinal);
                }
            }

            if (!publicSelected && !privateSelected)
                return true;
            if (!isPrivate.HasValue)
                return false;
            return isPrivate.Value ? privateSelected : publicSelected;
        }

        internal static bool MatchesRepositoryFilters(
            bool? isPrivate,
            string repositoryOwner,
            IReadOnlyList<string> selectedLabels,
            IReadOnlyList<string> selectedCategories)
        {
            if (!MatchesRepositoryVisibility(isPrivate, selectedLabels))
                return false;

            bool organizationSelected = false;
            string organizationLabel =
                PackageManagerSubmoduleNativePage.CreateOrganizationFilterLabel(
                    repositoryOwner);
            if (selectedCategories != null)
            {
                for (int index = 0; index < selectedCategories.Count; index++)
                {
                    string category = selectedCategories[index];
                    if (!PackageManagerSubmoduleNativePage
                            .IsOrganizationFilterLabel(category))
                    {
                        continue;
                    }

                    organizationSelected = true;
                    if (!string.IsNullOrEmpty(organizationLabel) &&
                        string.Equals(
                            category,
                            organizationLabel,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }

            return !organizationSelected;
        }

        internal static bool MatchesDownloadedFilter(
            string selectedStatusName,
            bool isDownloaded)
        {
            return !string.Equals(
                       selectedStatusName,
                       PackageManagerSubmoduleNativePage
                           .DownloadedFilterStatusName,
                       StringComparison.Ordinal) ||
                   isDownloaded;
        }

        internal static bool TryGetSupportedCategories(
            object page,
            out IReadOnlyList<string> supportedCategories)
        {
            if (TryGetPageVisibilityFilterContract(
                    out PageVisibilityFilterContract contract))
            {
                return TryGetFilterStringList(
                    page,
                    contract.FiltersProperty,
                    contract.SupportedCategoriesProperty,
                    out supportedCategories);
            }

            return TryGetFilterStringList(
                page,
                GetPageFiltersProperty(),
                GetPageFilterSupportedCategoriesProperty(),
                out supportedCategories);
        }

        internal static bool TryGetPageGroupNames(
            object page,
            out IReadOnlyList<string> groupNames)
        {
            groupNames = null;
            PropertyInfo visualStatesProperty;
            PropertyInfo orderedGroupNamesProperty;
            if (TryGetPageVisibilityFilterContract(
                    out PageVisibilityFilterContract contract))
            {
                visualStatesProperty = contract.VisualStatesProperty;
                orderedGroupNamesProperty = contract.OrderedGroupNamesProperty;
            }
            else if (TryGetLegacyPageFilterContract(
                         out LegacyPageFilterContract legacyContract))
            {
                visualStatesProperty = legacyContract.VisualStatesProperty;
                orderedGroupNamesProperty =
                    legacyContract.OrderedGroupNamesProperty;
            }
            else
            {
                visualStatesProperty = GetPageVisualStatesProperty();
                orderedGroupNamesProperty = GetPageOrderedGroupNamesProperty();
            }
            if (page == null ||
                visualStatesProperty == null ||
                orderedGroupNamesProperty == null)
            {
                return false;
            }

            object visualStates = visualStatesProperty.GetValue(page, null);
            if (visualStates == null)
                return false;
            object orderedGroupNames = orderedGroupNamesProperty.GetValue(
                visualStates,
                null);
            if (!(orderedGroupNames is IEnumerable<string> enumerable))
                return false;

            groupNames = new List<string>(enumerable);
            return true;
        }

        internal static bool TryGetPackageRepositoryPrivacy(
            object package,
            PackageManagerGitHubDiscoverySnapshot discoverySnapshot,
            out bool isPrivate)
        {
            isPrivate = false;
            if (package == null)
                return false;

            if (PackageManagerGitHubPackageProjection.TryGetRepository(
                    package,
                    out PackageManagerGitHubRepository repository))
            {
                isPrivate = repository.IsPrivate;
                return true;
            }

            object primaryVersion =
                PackageManagerSubmoduleNativePage.GetPrimaryVersion(package);
            if (PackageManagerSubmodulePresentation.TryGetPresentation(
                    primaryVersion,
                    out PackageManagerSubmoduleInfo submoduleInfo) &&
                PackageManagerSubmodulePresentation.TryGetRepositoryPrivacy(
                    submoduleInfo,
                    discoverySnapshot,
                    out isPrivate))
            {
                return true;
            }

            return PackageManagerReadOnlyGitPackage.TryGetInfo(
                       package,
                       out PackageManagerReadOnlyGitInfo readOnlyInfo) &&
                   PackageManagerSubmodulePresentation.TryGetRepositoryPrivacy(
                       readOnlyInfo.RepositoryUrl,
                       discoverySnapshot,
                       out isPrivate);
        }

        internal static bool HasRequiredDiscoveryLifecycleContract()
        {
            return GetPageRefreshTargets().Count > 0 &&
                   GetPageActivationTargets().Count > 0 &&
                   GetPageLoadingTargets().Count > 0 &&
                   HasPageVisibilityFilterContract() &&
                   GetPackageStatusUpdateMethod() != null &&
                   GetPackageStatusBarProperty() != null;
        }

        internal static bool HasRequiredLegacyDiscoveryLifecycleContract()
        {
            return GetPageRefreshTargets().Count > 0 &&
                   GetPageActivationTargets().Count > 0 &&
                   GetPageLoadingTargets().Count > 0 &&
                   GetDetailsInformationCardsTarget() != null &&
                   TryGetLegacyPageFilterContract(out _) &&
                   GetPackageStatusUpdateMethod() != null &&
                   GetPackageStatusBarProperty() != null;
        }

        internal static bool AreLegacyPageFilterPatchesApplied()
        {
            return TryGetLegacyPageFilterContract(
                       out LegacyPageFilterContract contract) &&
                   IsPatchApplied(
                       contract.VisibilityFilterTarget,
                       GetLegacyPageVisibilityFilterPostfix()) &&
                   IsPatchApplied(
                       contract.FiltersDisplayTarget,
                       GetLegacyFiltersDisplayPostfix()) &&
                   IsPatchApplied(
                       contract.FiltersSizeTarget,
                       GetLegacyFiltersSizePostfix());
        }

        internal static bool TryCreateDependencyInfos(
            PackageManagerGitHubRepository repository,
            out DependencyInfo[] dependencyInfos)
        {
            dependencyInfos = null;
            if (repository == null)
                return false;

            IReadOnlyList<PackageManifestDependency> dependencies =
                repository.Dependencies ?? Array.Empty<PackageManifestDependency>();
            FieldInfo nameField = typeof(DependencyInfo).GetField(
                "m_Name",
                AnyInstance);
            FieldInfo versionField = typeof(DependencyInfo).GetField(
                "m_Version",
                AnyInstance);
            if (nameField == null || versionField == null ||
                nameField.IsStatic || versionField.IsStatic ||
                nameField.FieldType != typeof(string) ||
                versionField.FieldType != typeof(string))
            {
                return false;
            }

            try
            {
                var result = new DependencyInfo[dependencies.Count];
                for (int index = 0; index < dependencies.Count; index++)
                {
                    PackageManifestDependency dependency = dependencies[index];
                    if (dependency == null ||
                        !GitUtility.IsValidUpmPackageName(dependency.Name) ||
                        string.IsNullOrWhiteSpace(dependency.Version))
                    {
                        return false;
                    }

                    object boxed = default(DependencyInfo);
                    nameField.SetValue(boxed, dependency.Name);
                    versionField.SetValue(boxed, dependency.Version);
                    result[index] = (DependencyInfo)boxed;
                }

                dependencyInfos = result;
                return true;
            }
            catch
            {
                dependencyInfos = null;
                return false;
            }
        }

        private static IReadOnlyList<MethodInfo> FindSingleArgumentRefreshTargets(
            string typeName,
            string parameterTypeName)
        {
            return FindSingleArgumentTargets(
                typeName,
                "Refresh",
                typeof(void),
                parameterTypeName);
        }

        private static IReadOnlyList<MethodInfo> FindSingleArgumentTargets(
            string typeName,
            string methodName,
            Type returnType,
            string parameterTypeName)
        {
            var matches = new List<MethodInfo>();
            Type type = PackageManagerSubmoduleHarmonyPatch.FindLoadedType(typeName);
            if (type == null)
                return matches;

            foreach (MethodInfo method in type.GetMethods(AnyInstance))
            {
                ParameterInfo[] parameters = method.GetParameters();
                if (method.Name == methodName &&
                    !method.IsStatic &&
                    method.ReturnType == returnType &&
                    parameters.Length == 1 &&
                    string.Equals(
                        parameters[0].ParameterType.FullName,
                        parameterTypeName,
                        StringComparison.Ordinal))
                {
                    matches.Add(method);
                }
            }

            return matches;
        }

        private static void PatchPostfixIfNeeded(
            MethodInfo target,
            MethodInfo postfix)
        {
            if (target == null || postfix == null || IsPatchApplied(target, postfix))
                return;

            harmony.Patch(target, postfix: new HarmonyMethod(postfix));
        }

        private static void TechnicalNameRefreshPostfix(
            object __instance,
            object __0)
        {
            try
            {
                if (PackageManagerGitHubPackageProjection.TryGetRepository(
                        __0,
                        out PackageManagerGitHubRepository repository))
                {
                    PackageManagerSubmodulePresentation.ApplyTechnicalNameCard(
                        __instance,
                        repository.PackageName);
                }
            }
            catch
            {
                // Preserve Unity's native technical-name card on contract drift.
            }
        }

        private static void AuthorRefreshPostfix(object __instance, object __0)
        {
            try
            {
                if (PackageManagerGitHubPackageProjection.TryGetRepository(
                        __0,
                        out PackageManagerGitHubRepository repository))
                {
                    PackageManagerSubmodulePresentation.ApplyAuthorLabel(
                        __instance,
                        string.IsNullOrWhiteSpace(repository.AuthorName)
                            ? repository.Owner
                            : repository.AuthorName);
                }
            }
            catch
            {
                // Preserve Unity's native author presentation on contract drift.
            }
        }

        private static void DependenciesGetterPostfix(
            object __instance,
            ref DependencyInfo[] __result)
        {
            try
            {
                if (PackageManagerGitHubPackageProjection.TryGetRepository(
                        __instance,
                        out PackageManagerGitHubRepository repository) &&
                    TryCreateDependencyInfos(repository, out DependencyInfo[] dependencies))
                {
                    __result = dependencies;
                }
            }
            catch
            {
                // Preserve Unity's own dependency graph on contract drift.
            }
        }

        private static void DependenciesTabValidityPostfix(
            object __0,
            ref bool __result)
        {
            try
            {
                if (!__result &&
                    PackageManagerGitHubPackageProjection.TryGetRepository(
                        __0,
                        out _))
                {
                    __result = true;
                }
            }
            catch
            {
                // Preserve Unity's native tab visibility on contract drift.
            }
        }

        private static void DetailsInformationCardsPostfix(
            object __instance,
            object __0)
        {
            try
            {
                PackageManagerGitHubPackageProjection.TryGetRepository(
                    __0,
                    out PackageManagerGitHubRepository repository);
                ApplyRepositoryInformationCards(__instance, repository);
            }
            catch
            {
                // Preserve Unity's native Details tab on contract drift.
            }
        }

        internal static bool ApplyRepositoryInformationCards(
            object detailsTab,
            PackageManagerGitHubRepository repository)
        {
            if (!(detailsTab is VisualElement tab))
                return false;

            bool isLegacyDetailsTab = string.Equals(
                tab.GetType().FullName,
                LegacyDetailsTabTypeName,
                StringComparison.Ordinal);
            if (isLegacyDetailsTab && repository == null)
            {
                RemoveOwnedLegacyInformationCardsContainer(tab);
                return true;
            }

            VisualElement container = tab.Q<VisualElement>(
                DetailsInformationCardsContainerName);
            if (container == null && isLegacyDetailsTab)
            {
                container = tab.Q<VisualElement>(
                    LegacyDetailsInformationCardsContainerName);
                if (container == null && repository != null)
                {
                    container = new VisualElement
                    {
                        name = LegacyDetailsInformationCardsContainerName
                    };
                    container.style.flexDirection = FlexDirection.Row;
                    container.style.flexWrap = Wrap.Wrap;
                    container.style.marginTop = 6f;
                    container.style.marginBottom = 4f;
                    tab.Add(container);
                }
            }

            if (container == null)
            {
                tab.Q<VisualElement>(LicenseInformationCardName)
                    ?.RemoveFromHierarchy();
                tab.Q<VisualElement>(DefaultBranchInformationCardName)
                    ?.RemoveFromHierarchy();
                return false;
            }

            RemoveDuplicateOwnedInformationCards(
                container,
                LicenseInformationCardName);
            RemoveDuplicateOwnedInformationCards(
                container,
                DefaultBranchInformationCardName);

            if (repository == null)
            {
                RemoveOwnedInformationCard(
                    container,
                    LicenseInformationCardName);
                RemoveOwnedInformationCard(
                    container,
                    DefaultBranchInformationCardName);
                return true;
            }

            string license = NormalizeInformationCardValue(repository.License);
            UpsertInformationCard(
                container,
                LicenseInformationCardName,
                L10n.Tr("License"),
                license,
                license);
            UpsertInformationCard(
                container,
                DefaultBranchInformationCardName,
                L10n.Tr("Default Branch"),
                NormalizeInformationCardValue(repository.DefaultBranch),
                string.Empty);
            if (isLegacyDetailsTab)
                ApplyLegacyInformationCardLayout(container);
            return true;
        }

        internal static bool RemoveOwnedLegacyInformationCardsContainer(
            VisualElement detailsTab)
        {
            VisualElement container = FindDirectChild(
                detailsTab,
                LegacyDetailsInformationCardsContainerName);
            if (container == null)
                return false;

            container.RemoveFromHierarchy();
            return true;
        }

        private static void ApplyLegacyInformationCardLayout(
            VisualElement container)
        {
            if (container == null)
                return;

            foreach (VisualElement card in container.Children())
            {
                card.style.flexGrow = 1f;
                card.style.flexBasis = 180f;
                card.style.minWidth = 180f;
                card.style.marginRight = 8f;
                card.style.marginBottom = 4f;
                card.style.paddingLeft = 6f;
                card.style.paddingRight = 6f;
                card.style.paddingTop = 4f;
                card.style.paddingBottom = 4f;

                Label title = card.Q<Label>(
                    className: InformationCardTitleClassName);
                if (title != null)
                {
                    title.style.unityFontStyleAndWeight = FontStyle.Bold;
                    title.style.marginBottom = 2f;
                }
            }
        }

        private static void UpsertInformationCard(
            VisualElement container,
            string cardName,
            string titleText,
            string contentText,
            string contentTooltip)
        {
            VisualElement card = FindDirectChild(container, cardName);
            if (card == null)
            {
                card = new VisualElement
                {
                    name = cardName
                };
                card.AddToClassList(InformationCardClassName);
                card.AddToClassList(InformationCardSmallClassName);

                var title = new Label
                {
                    enableRichText = false
                };
                title.AddToClassList(InformationCardTitleClassName);
                card.Add(title);

                var content = new VisualElement();
                content.AddToClassList(InformationCardContentClassName);
                var icon = new VisualElement();
                icon.AddToClassList(
                    PackageManagerSubmodulePresentation
                        .InformationCardIconClassName);
                icon.style.display = DisplayStyle.None;
                content.Add(icon);
                var contentLabel = new Label
                {
                    enableRichText = false
                };
                contentLabel.AddToClassList(
                    PackageManagerSubmodulePresentation
                        .InformationCardTextClassName);
                content.Add(contentLabel);
                card.Add(content);
                container.Add(card);
            }

            Label titleLabel = card.Q<Label>(
                className: InformationCardTitleClassName);
            Label valueLabel = card.Q<Label>(
                className: PackageManagerSubmodulePresentation
                    .InformationCardTextClassName);
            if (titleLabel == null || valueLabel == null)
            {
                card.RemoveFromHierarchy();
                UpsertInformationCard(
                    container,
                    cardName,
                    titleText,
                    contentText,
                    contentTooltip);
                return;
            }

            titleLabel.text = titleText;
            titleLabel.tooltip = titleText;
            valueLabel.text = contentText;
            valueLabel.tooltip = contentTooltip;
            card.style.display = DisplayStyle.Flex;
        }

        private static string NormalizeInformationCardValue(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return L10n.Tr("Not set");

            string normalized = value.Trim();
            if (normalized.Length > 256)
                return L10n.Tr("Not set");

            foreach (char character in normalized)
            {
                if (char.IsControl(character))
                    return L10n.Tr("Not set");
            }

            return normalized;
        }

        private static void RemoveDuplicateOwnedInformationCards(
            VisualElement container,
            string cardName)
        {
            VisualElement retained = null;
            var duplicates = new List<VisualElement>();
            foreach (VisualElement child in container.Children())
            {
                if (!string.Equals(child.name, cardName, StringComparison.Ordinal))
                    continue;

                if (retained == null)
                    retained = child;
                else
                    duplicates.Add(child);
            }

            foreach (VisualElement duplicate in duplicates)
                duplicate.RemoveFromHierarchy();
        }

        private static void RemoveOwnedInformationCard(
            VisualElement container,
            string cardName)
        {
            FindDirectChild(container, cardName)?.RemoveFromHierarchy();
        }

        private static VisualElement FindDirectChild(
            VisualElement container,
            string childName)
        {
            foreach (VisualElement child in container.Children())
            {
                if (string.Equals(
                        child.name,
                        childName,
                        StringComparison.Ordinal))
                {
                    return child;
                }
            }

            return null;
        }

        private static void PackageLinkPostfix(
            MethodBase __originalMethod,
            object __0,
            object __result)
        {
            try
            {
                if (__originalMethod == null || __result == null ||
                    !PackageManagerGitHubPackageProjection.TryGetRepository(
                        __0,
                        out PackageManagerGitHubRepository repository))
                {
                    return;
                }

                string url;
                switch (__originalMethod.Name)
                {
                    case "CreateUpmDocumentationLink":
                        url = repository.DocumentationUrl;
                        break;
                    case "CreateUpmChangelogLink":
                        url = repository.ChangelogUrl;
                        break;
                    case "CreateUpmLicenseLink":
                        url = repository.LicensesUrl;
                        break;
                    default:
                        return;
                }

                if (string.IsNullOrEmpty(url))
                    return;

                PropertyInfo urlProperty = __result.GetType().GetProperty(
                    "url",
                    AnyInstance);
                if (urlProperty != null &&
                    urlProperty.CanWrite &&
                    urlProperty.PropertyType == typeof(string) &&
                    urlProperty.GetIndexParameters().Length == 0)
                {
                    urlProperty.SetValue(__result, url, null);
                }
            }
            catch
            {
                // Preserve Unity's native package links on contract drift.
            }
        }

        private static void PageRefreshPostfix(object __0)
        {
            try
            {
                if (!IsGitHubPage(__0))
                    return;

                PackageManagerSubmoduleNativePage.TryConfigureFilters(__0);
                if (!ShouldForwardDiscoveryRefresh(
                        PackageManagerGitHubPackageProjection.IsUpdatingPackageDatabase))
                    return;

                // A failed branch lookup is intentionally sticky so transient
                // failures cannot create an automatic network retry loop. The
                // native Refresh action is the explicit user gesture that clears
                // and retries only each live details host's selected repository.
                PackageManagerGitHubNativeActions
                    .RetryFailedBranchDiscoveryForSelectedRepositories();

                // Refreshing the remote catalogue must not invalidate or start a
                // reader for the last known-good installed-submodule snapshot.
                // That snapshot already follows package registration, project,
                // repository-generation, and .gitmodules changes. Keeping the
                // two lifecycles independent leaves safe uninstall actions usable
                // throughout a potentially long GitHub discovery refresh.
                PackageManagerGitHubDiscovery.Refresh();
            }
            catch
            {
                // Unity's own refresh remains authoritative.
            }
        }

        internal static bool ShouldForwardDiscoveryRefresh(
            bool packageDatabaseUpdateInProgress)
        {
            return !packageDatabaseUpdateInProgress;
        }

        private static void PageActivationPostfix(object __0)
        {
            try
            {
                if (!IsGitHubPage(__0))
                    return;

                PackageManagerSubmoduleNativePage.TryConfigureFilters(__0);
                // Static snapshot initialization already schedules the first
                // installed-package scan. Re-entering Sources > GitHub should not
                // restart that reader or disable uninstall while repositories load.
                PackageManagerGitHubDiscovery.EnsureStarted();
            }
            catch
            {
                // Page activation remains fully owned by Unity on contract drift.
            }
        }

        private static void PageLoadingPostfix(object __0, ref bool __result)
        {
            try
            {
                __result = ShouldReportDiscoveryLoading(
                    GetPageId(__0),
                    __result,
                    PackageManagerGitHubDiscovery.IsLoading);
            }
            catch
            {
                // Preserve Unity's native refresh result on contract drift.
            }
        }

        private static void PageVisibilityFilterPostfix(
            object __instance,
            string __0,
            ref bool __result)
        {
            try
            {
                // Unity remains authoritative for search and its implemented
                // status predicates. SimplePageWithPackages does not evaluate the
                // native Downloaded status for extension pages, so that status and
                // our repository facets only narrow an otherwise native match.
                if (!__result || !IsGitHubPage(__instance) ||
                    !TryGetPageVisibilityFilterContract(
                        out PageVisibilityFilterContract contract) ||
                    !TryReadSelectedFilters(
                        __instance,
                        contract,
                        out string selectedStatusName,
                        out IReadOnlyList<string> selectedLabels,
                        out IReadOnlyList<string> selectedCategories))
                {
                    return;
                }

                bool repositoryFiltersInactive = MatchesRepositoryFilters(
                    null,
                    string.Empty,
                    selectedLabels,
                    selectedCategories);
                bool downloadedFilterInactive = MatchesDownloadedFilter(
                    selectedStatusName,
                    false);
                if (repositoryFiltersInactive && downloadedFilterInactive)
                    return;

                if (!TryGetPagePackage(
                        __instance,
                        __0,
                        contract,
                        out object package))
                    return;

                if (!MatchesDownloadedFilter(
                        selectedStatusName,
                        PackageManagerSubmoduleNativePage.IsDownloadedPackage(
                            package)))
                {
                    __result = false;
                    return;
                }

                if (repositoryFiltersInactive)
                    return;

                bool? isPrivate = TryGetPackageRepositoryPrivacy(
                    package,
                    PackageManagerGitHubDiscovery.Current,
                    out bool resolvedPrivacy)
                    ? resolvedPrivacy
                    : null;
                __result = MatchesRepositoryFilters(
                    isPrivate,
                    PackageManagerSubmoduleNativePage
                        .GetGitHubRepositoryOwner(package),
                    selectedLabels,
                    selectedCategories);
            }
            catch
            {
                // Reflection drift must never hide packages outside our verified
                // repository predicates or interfere with another Package Manager page.
            }
        }

        private static void LegacyPageVisibilityFilterPostfix(
            object __instance,
            object __0,
            ref bool __result)
        {
            try
            {
                if (!__result || !IsGitHubPage(__instance) || __0 == null ||
                    !TryGetLegacyPageFilterContract(
                        out LegacyPageFilterContract contract) ||
                    !contract.PackageType.IsInstanceOfType(__0) ||
                    !TryReadLegacySelectedFilters(
                        __instance,
                        contract,
                        out string selectedStatusName,
                        out IReadOnlyList<string> selectedLabels,
                        out IReadOnlyList<string> selectedCategories))
                {
                    return;
                }

                if (!MatchesDownloadedFilter(
                        selectedStatusName,
                        PackageManagerSubmoduleNativePage.IsDownloadedPackage(
                            __0)))
                {
                    __result = false;
                    return;
                }

                if (MatchesRepositoryFilters(
                        null,
                        string.Empty,
                        selectedLabels,
                        selectedCategories))
                {
                    return;
                }

                bool? isPrivate = TryGetPackageRepositoryPrivacy(
                    __0,
                    PackageManagerGitHubDiscovery.Current,
                    out bool resolvedPrivacy)
                    ? resolvedPrivacy
                    : null;
                __result = MatchesRepositoryFilters(
                    isPrivate,
                    PackageManagerSubmoduleNativePage
                        .GetGitHubRepositoryOwner(__0),
                    selectedLabels,
                    selectedCategories);
            }
            catch
            {
                // A partial legacy contract must never hide unrelated packages.
            }
        }

        private static void LegacyFiltersDisplayPostfix(
            object __instance,
            object __0)
        {
            try
            {
                if (!IsGitHubPage(__0) ||
                    !TryGetLegacyPageFilterContract(
                        out LegacyPageFilterContract contract) ||
                    !contract.FiltersWindowType.IsInstanceOfType(__instance))
                {
                    return;
                }

                object filters = contract.FiltersField.GetValue(__instance);
                if (filters == null ||
                    !contract.FiltersType.IsInstanceOfType(filters) ||
                    !(contract.ContainerField.GetValue(__instance) is
                        VisualElement container))
                {
                    return;
                }

                FindDirectChild(container, LegacyVisibilityFoldoutName)
                    ?.RemoveFromHierarchy();
                FindDirectChild(container, LegacyOrganizationFoldoutName)
                    ?.RemoveFromHierarchy();

                IReadOnlyList<string> selectedLabels =
                    contract.LabelsProperty.GetValue(filters, null) as
                        IReadOnlyList<string> ?? Array.Empty<string>();
                IReadOnlyList<string> selectedCategories =
                    contract.CategoriesProperty.GetValue(filters, null) as
                        IReadOnlyList<string> ?? Array.Empty<string>();

                Foldout visibilityFoldout = CreateLegacyFilterFoldout(
                    LegacyVisibilityFoldoutName,
                    L10n.Tr("Visibility"),
                    PackageManagerSubmoduleNativePage
                        .GetSupportedVisibilityLabels(),
                    selectedLabels,
                    contract);
                IReadOnlyList<string> supportedOrganizations =
                    PackageManagerSubmoduleNativePage
                        .GetSupportedOrganizationFilters(__0);
                Foldout organizationFoldout = CreateLegacyFilterFoldout(
                    LegacyOrganizationFoldoutName,
                    L10n.Tr("Organization"),
                    supportedOrganizations,
                    selectedCategories,
                    contract);

                RegisterLegacyFilterCallbacks(
                    __instance,
                    visibilityFoldout,
                    organizationFoldout,
                    contract);
                container.Add(visibilityFoldout);
                if (supportedOrganizations.Count > 0)
                    container.Add(organizationFoldout);
            }
            catch
            {
                // Keep Unity's status-only legacy filter popup intact on drift.
            }
        }

        private static void LegacyFiltersSizePostfix(
            object __instance,
            object __0,
            ref Vector2 __result)
        {
            try
            {
                if (!IsGitHubPage(__0) ||
                    !TryGetLegacyPageFilterContract(
                        out LegacyPageFilterContract contract) ||
                    !contract.FiltersWindowType.IsInstanceOfType(__instance) ||
                    !(contract.ContainerField.GetValue(__instance) is
                        VisualElement container))
                {
                    return;
                }

                float addedHeight = GetLegacyFilterFoldoutHeight(
                    FindDirectChild(container, LegacyVisibilityFoldoutName),
                    contract);
                addedHeight += GetLegacyFilterFoldoutHeight(
                    FindDirectChild(container, LegacyOrganizationFoldoutName),
                    contract);
                __result = new Vector2(
                    __result.x,
                    Mathf.Min(__result.y + addedHeight, contract.MaximumHeight));
            }
            catch
            {
                // Preserve Unity's original popup size on contract drift.
            }
        }

        private static Foldout CreateLegacyFilterFoldout(
            string elementName,
            string title,
            IReadOnlyList<string> values,
            IReadOnlyList<string> selectedValues,
            LegacyPageFilterContract contract)
        {
            var foldout = new Foldout
            {
                name = elementName,
                text = title
            };
            foldout.AddToClassList(contract.FoldoutClassName);

            if (values == null)
                return foldout;

            for (int index = 0; index < values.Count; index++)
            {
                string value = values[index];
                if (string.IsNullOrWhiteSpace(value))
                    continue;

                var toggle = new Toggle(L10n.Tr(value))
                {
                    name = value,
                    tooltip = value
                };
                toggle.AddToClassList(contract.ToggleClassName);
                toggle.SetValueWithoutNotify(
                    ContainsFilterValue(selectedValues, value));
                foldout.Add(toggle);
            }

            return foldout;
        }

        private static void RegisterLegacyFilterCallbacks(
            object filterWindow,
            Foldout visibilityFoldout,
            Foldout organizationFoldout,
            LegacyPageFilterContract contract)
        {
            void Register(Foldout foldout)
            {
                if (foldout == null)
                    return;

                foreach (VisualElement child in foldout.Children())
                {
                    if (child is Toggle toggle)
                    {
                        toggle.RegisterValueChangedCallback(_ =>
                            OnLegacyFilterSelectionChanged(
                                filterWindow,
                                visibilityFoldout,
                                organizationFoldout,
                                contract));
                    }
                }
            }

            Register(visibilityFoldout);
            Register(organizationFoldout);
        }

        private static void OnLegacyFilterSelectionChanged(
            object filterWindow,
            Foldout visibilityFoldout,
            Foldout organizationFoldout,
            LegacyPageFilterContract contract)
        {
            try
            {
                if (filterWindow == null || contract == null ||
                    !contract.FiltersWindowType.IsInstanceOfType(filterWindow))
                {
                    return;
                }

                object filters = contract.FiltersField.GetValue(filterWindow);
                object cloned = contract.CloneFiltersMethod.Invoke(filters, null);
                if (cloned == null ||
                    !contract.FiltersType.IsInstanceOfType(cloned))
                {
                    return;
                }

                contract.LabelsProperty.SetValue(
                    cloned,
                    GetSelectedLegacyFilterValues(visibilityFoldout),
                    null);
                contract.CategoriesProperty.SetValue(
                    cloned,
                    GetSelectedLegacyFilterValues(organizationFoldout),
                    null);
                contract.FiltersField.SetValue(filterWindow, cloned);
                contract.NotifyFiltersChangedMethod.Invoke(filterWindow, null);
            }
            catch
            {
                // Leave the last native PageFilters clone unchanged on drift.
            }
        }

        private static List<string> GetSelectedLegacyFilterValues(
            Foldout foldout)
        {
            var values = new List<string>();
            if (foldout == null)
                return values;

            foreach (VisualElement child in foldout.Children())
            {
                if (child is Toggle toggle &&
                    toggle.value &&
                    !string.IsNullOrWhiteSpace(toggle.name))
                {
                    values.Add(toggle.name);
                }
            }

            return values;
        }

        private static float GetLegacyFilterFoldoutHeight(
            VisualElement foldout,
            LegacyPageFilterContract contract)
        {
            if (foldout == null)
                return 0f;

            int toggleCount = 0;
            foreach (VisualElement child in foldout.Children())
            {
                if (child is Toggle)
                    toggleCount++;
            }

            return toggleCount == 0
                ? 0f
                : contract.FoldoutHeight + toggleCount * contract.ToggleHeight;
        }

        private static bool ContainsFilterValue(
            IReadOnlyList<string> values,
            string candidate)
        {
            if (values == null || string.IsNullOrEmpty(candidate))
                return false;

            for (int index = 0; index < values.Count; index++)
            {
                if (string.Equals(
                        values[index],
                        candidate,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static void PageSupportedFiltersRefreshPostfix(object __instance)
        {
            try
            {
                if (IsGitHubPage(__instance))
                {
                    PackageManagerSubmoduleNativePage.TryConfigureFilters(
                        __instance);
                }
            }
            catch
            {
                // Unity's supported filters remain authoritative on contract drift.
            }
        }

        private static bool IsGitHubPage(object page)
        {
            return string.Equals(
                GetPageId(page),
                PackageManagerSubmoduleNativePage.ExtensionPageId,
                StringComparison.Ordinal);
        }

        internal static bool TryGetSelectedCategories(
            object page,
            out IReadOnlyList<string> selectedCategories)
        {
            if (TryGetPageVisibilityFilterContract(
                    out PageVisibilityFilterContract contract))
            {
                return TryGetFilterStringList(
                    page,
                    contract.FiltersProperty,
                    contract.CategoriesProperty,
                    out selectedCategories);
            }

            return TryGetFilterStringList(
                page,
                GetPageFiltersProperty(),
                GetPageFilterCategoriesProperty(),
                out selectedCategories);
        }

        private static bool TryGetFilterStringList(
            object page,
            PropertyInfo filtersProperty,
            PropertyInfo listProperty,
            out IReadOnlyList<string> values)
        {
            values = null;
            if (page == null || filtersProperty == null || listProperty == null)
                return false;

            object filters = filtersProperty.GetValue(page, null);
            if (filters == null)
                return false;
            object list = listProperty.GetValue(filters, null);
            if (!(list is IReadOnlyList<string> readOnlyList))
                return false;

            values = readOnlyList;
            return true;
        }

        private static bool TryReadSelectedFilters(
            object page,
            PageVisibilityFilterContract contract,
            out string selectedStatusName,
            out IReadOnlyList<string> selectedLabels,
            out IReadOnlyList<string> selectedCategories)
        {
            return TryReadSelectedFilterValues(
                page,
                contract?.FiltersProperty,
                contract?.StatusProperty,
                contract?.LabelsProperty,
                contract?.CategoriesProperty,
                out selectedStatusName,
                out selectedLabels,
                out selectedCategories);
        }

        internal static bool TryReadSelectedFilterValues(
            object page,
            PropertyInfo filtersProperty,
            PropertyInfo statusProperty,
            PropertyInfo labelsProperty,
            PropertyInfo categoriesProperty,
            out string selectedStatusName,
            out IReadOnlyList<string> selectedLabels,
            out IReadOnlyList<string> selectedCategories)
        {
            selectedStatusName = string.Empty;
            selectedLabels = null;
            selectedCategories = null;
            if (page == null ||
                filtersProperty == null ||
                statusProperty == null ||
                labelsProperty == null ||
                categoriesProperty == null)
            {
                return false;
            }

            object filters = filtersProperty.GetValue(page, null);
            if (filters == null)
                return false;

            object status = statusProperty.GetValue(filters, null);
            if (status == null ||
                !(labelsProperty.GetValue(filters, null) is
                    IReadOnlyList<string> labels) ||
                !(categoriesProperty.GetValue(filters, null) is
                    IReadOnlyList<string> categories))
            {
                return false;
            }

            selectedStatusName = status.ToString();
            selectedLabels = labels;
            selectedCategories = categories;
            return true;
        }

        private static bool TryGetPagePackage(
            object page,
            string packageUniqueId,
            PageVisibilityFilterContract contract,
            out object package)
        {
            package = null;
            if (page == null ||
                string.IsNullOrEmpty(packageUniqueId) ||
                contract == null)
            {
                return false;
            }

            object packageDatabase = contract.PackageDatabaseField.GetValue(page);
            if (packageDatabase == null)
                return false;

            package = contract.PackageLookupMethod.Invoke(
                packageDatabase,
                new object[] { packageUniqueId });
            return package != null;
        }

        private static bool TryReadLegacySelectedFilters(
            object page,
            LegacyPageFilterContract contract,
            out string selectedStatusName,
            out IReadOnlyList<string> selectedLabels,
            out IReadOnlyList<string> selectedCategories)
        {
            selectedStatusName = string.Empty;
            selectedLabels = null;
            selectedCategories = null;
            if (page == null || contract == null ||
                !contract.ExtensionPageType.IsInstanceOfType(page))
            {
                return false;
            }

            object filters = contract.PageFiltersProperty.GetValue(page, null);
            if (filters == null ||
                !contract.FiltersType.IsInstanceOfType(filters))
            {
                return false;
            }

            object status = contract.StatusField.GetValue(filters);
            if (status == null ||
                !(contract.LabelsProperty.GetValue(filters, null) is
                    IReadOnlyList<string> labels) ||
                !(contract.CategoriesProperty.GetValue(filters, null) is
                    IReadOnlyList<string> categories))
            {
                return false;
            }

            selectedStatusName = status.ToString();
            selectedLabels = labels;
            selectedCategories = categories;
            return true;
        }

        internal static bool TryUpdateLegacyPageFilters(
            object page,
            object currentFilters,
            IReadOnlyList<string> labels,
            IReadOnlyList<string> categories)
        {
            try
            {
                if (!TryGetLegacyPageFilterContract(
                        out LegacyPageFilterContract contract) ||
                    page == null ||
                    !contract.ExtensionPageType.IsInstanceOfType(page) ||
                    currentFilters == null ||
                    !contract.FiltersType.IsInstanceOfType(currentFilters))
                {
                    return false;
                }

                object cloned = contract.CloneFiltersMethod.Invoke(
                    currentFilters,
                    null);
                if (cloned == null ||
                    !contract.FiltersType.IsInstanceOfType(cloned))
                {
                    return false;
                }

                contract.LabelsProperty.SetValue(
                    cloned,
                    labels == null
                        ? new List<string>()
                        : new List<string>(labels),
                    null);
                contract.CategoriesProperty.SetValue(
                    cloned,
                    categories == null
                        ? new List<string>()
                        : new List<string>(categories),
                    null);
                contract.PageUpdateFiltersMethod.Invoke(
                    page,
                    new[] { cloned });
                return true;
            }
            catch
            {
                return false;
            }
        }

        internal static MethodInfo GetLegacyPageVisibilityFilterTarget()
        {
            return TryGetLegacyPageFilterContract(
                out LegacyPageFilterContract contract)
                ? contract.VisibilityFilterTarget
                : null;
        }

        internal static MethodInfo GetLegacyFiltersDisplayTarget()
        {
            return TryGetLegacyPageFilterContract(
                out LegacyPageFilterContract contract)
                ? contract.FiltersDisplayTarget
                : null;
        }

        internal static MethodInfo GetLegacyFiltersSizeTarget()
        {
            return TryGetLegacyPageFilterContract(
                out LegacyPageFilterContract contract)
                ? contract.FiltersSizeTarget
                : null;
        }

        private static bool TryGetLegacyPageFilterContract(
            out LegacyPageFilterContract contract)
        {
            contract = legacyPageFilterContract;
            if (contract != null)
                return true;

            LegacyPageFilterContract candidate =
                LegacyPageFilterContract.TryCreate();
            if (candidate == null)
                return false;

            lock (LegacyPageFilterContractGate)
            {
                if (legacyPageFilterContract == null)
                    legacyPageFilterContract = candidate;
                contract = legacyPageFilterContract;
                return true;
            }
        }

        private sealed class LegacyPageFilterContract
        {
            private LegacyPageFilterContract(
                Type extensionPageType,
                Type packageType,
                Type filtersWindowType,
                Type filtersType,
                MethodInfo visibilityFilterTarget,
                MethodInfo filtersDisplayTarget,
                MethodInfo filtersSizeTarget,
                FieldInfo filtersField,
                FieldInfo containerField,
                FieldInfo statusField,
                PropertyInfo labelsProperty,
                PropertyInfo categoriesProperty,
                PropertyInfo pageFiltersProperty,
                PropertyInfo visualStatesProperty,
                PropertyInfo orderedGroupNamesProperty,
                MethodInfo cloneFiltersMethod,
                MethodInfo pageUpdateFiltersMethod,
                MethodInfo notifyFiltersChangedMethod,
                int maximumHeight,
                int foldoutHeight,
                int toggleHeight,
                string foldoutClassName,
                string toggleClassName)
            {
                ExtensionPageType = extensionPageType;
                PackageType = packageType;
                FiltersWindowType = filtersWindowType;
                FiltersType = filtersType;
                VisibilityFilterTarget = visibilityFilterTarget;
                FiltersDisplayTarget = filtersDisplayTarget;
                FiltersSizeTarget = filtersSizeTarget;
                FiltersField = filtersField;
                ContainerField = containerField;
                StatusField = statusField;
                LabelsProperty = labelsProperty;
                CategoriesProperty = categoriesProperty;
                PageFiltersProperty = pageFiltersProperty;
                VisualStatesProperty = visualStatesProperty;
                OrderedGroupNamesProperty = orderedGroupNamesProperty;
                CloneFiltersMethod = cloneFiltersMethod;
                PageUpdateFiltersMethod = pageUpdateFiltersMethod;
                NotifyFiltersChangedMethod = notifyFiltersChangedMethod;
                MaximumHeight = maximumHeight;
                FoldoutHeight = foldoutHeight;
                ToggleHeight = toggleHeight;
                FoldoutClassName = foldoutClassName;
                ToggleClassName = toggleClassName;
            }

            internal Type ExtensionPageType { get; }
            internal Type PackageType { get; }
            internal Type FiltersWindowType { get; }
            internal Type FiltersType { get; }
            internal MethodInfo VisibilityFilterTarget { get; }
            internal MethodInfo FiltersDisplayTarget { get; }
            internal MethodInfo FiltersSizeTarget { get; }
            internal FieldInfo FiltersField { get; }
            internal FieldInfo ContainerField { get; }
            internal FieldInfo StatusField { get; }
            internal PropertyInfo LabelsProperty { get; }
            internal PropertyInfo CategoriesProperty { get; }
            internal PropertyInfo PageFiltersProperty { get; }
            internal PropertyInfo VisualStatesProperty { get; }
            internal PropertyInfo OrderedGroupNamesProperty { get; }
            internal MethodInfo CloneFiltersMethod { get; }
            internal MethodInfo PageUpdateFiltersMethod { get; }
            internal MethodInfo NotifyFiltersChangedMethod { get; }
            internal int MaximumHeight { get; }
            internal int FoldoutHeight { get; }
            internal int ToggleHeight { get; }
            internal string FoldoutClassName { get; }
            internal string ToggleClassName { get; }

            internal static LegacyPageFilterContract TryCreate()
            {
                try
                {
                    Type extensionPageType = PackageManagerSubmoduleHarmonyPatch
                        .FindLoadedType(LegacyExtensionPageTypeName);
                    Type packageType = PackageManagerSubmoduleHarmonyPatch
                        .FindLoadedType(PackageManagerSubmoduleHarmonyPatch
                            .PackageInterfaceTypeName);
                    Type pageType = PackageManagerSubmoduleHarmonyPatch
                        .FindLoadedType(PageInterfaceTypeName);
                    Type filtersWindowType = PackageManagerSubmoduleHarmonyPatch
                        .FindLoadedType(LegacyUpmFiltersWindowTypeName);
                    Type filtersWindowBaseType =
                        PackageManagerSubmoduleHarmonyPatch.FindLoadedType(
                            LegacyFiltersWindowTypeName);
                    Type basePageType = PackageManagerSubmoduleHarmonyPatch
                        .FindLoadedType(PackageManagerSubmoduleNativePage
                            .BasePageTypeName);
                    if (extensionPageType == null || packageType == null ||
                        pageType == null || filtersWindowType == null ||
                        filtersWindowBaseType == null || basePageType == null ||
                        !filtersWindowBaseType.IsAssignableFrom(filtersWindowType))
                    {
                        return null;
                    }

                    MethodInfo visibilityFilterTarget =
                        extensionPageType.GetMethod(
                            "ShouldInclude",
                            AnyInstance | BindingFlags.DeclaredOnly,
                            null,
                            new[] { packageType },
                            null);
                    MethodInfo filtersDisplayTarget = filtersWindowType.GetMethod(
                        "DoDisplay",
                        AnyInstance | BindingFlags.DeclaredOnly,
                        null,
                        new[] { pageType },
                        null);
                    MethodInfo filtersSizeTarget = filtersWindowType.GetMethod(
                        "GetSize",
                        AnyInstance | BindingFlags.DeclaredOnly,
                        null,
                        new[] { pageType },
                        null);
                    FieldInfo filtersField = FindDeclaredFieldInHierarchy(
                        filtersWindowType,
                        "m_Filters");
                    FieldInfo containerField = FindDeclaredFieldInHierarchy(
                        filtersWindowType,
                        "m_Container");
                    Type filtersType = filtersField?.FieldType;
                    PropertyInfo labelsProperty = filtersType?.GetProperty(
                        "labels",
                        AnyInstance);
                    PropertyInfo categoriesProperty = filtersType?.GetProperty(
                        "categories",
                        AnyInstance);
                    FieldInfo statusField = filtersType?.GetField(
                        "status",
                        AnyInstance | BindingFlags.DeclaredOnly);
                    MethodInfo cloneFiltersMethod = filtersType?.GetMethod(
                        "Clone",
                        AnyInstance | BindingFlags.DeclaredOnly,
                        null,
                        Type.EmptyTypes,
                        null);
                    PropertyInfo pageFiltersProperty = basePageType.GetProperty(
                        "filters",
                        AnyInstance);
                    PropertyInfo visualStatesProperty = basePageType.GetProperty(
                        "visualStates",
                        AnyInstance);
                    PropertyInfo orderedGroupNamesProperty =
                        visualStatesProperty?.PropertyType.GetProperty(
                            "orderedGroups",
                            AnyInstance);
                    MethodInfo updatePageFiltersMethod =
                        basePageType.GetMethod(
                            "UpdateFilters",
                            AnyInstance,
                            null,
                            filtersType == null
                                ? Type.EmptyTypes
                                : new[] { filtersType },
                            null);
                    MethodInfo notifyFiltersChangedMethod =
                        FindDeclaredMethodInHierarchy(
                            filtersWindowType,
                            "UpdatePageFilters",
                            Type.EmptyTypes,
                            typeof(void));
                    bool hasMaximumHeight = TryReadLiteralField(
                        filtersWindowBaseType,
                        "k_MaxHeight",
                        out int maximumHeight);
                    bool hasFoldoutHeight = TryReadLiteralField(
                        filtersWindowBaseType,
                        "k_FoldOutHeight",
                        out int foldoutHeight);
                    bool hasToggleHeight = TryReadLiteralField(
                        filtersWindowBaseType,
                        "k_ToggleHeight",
                        out int toggleHeight);
                    bool hasFoldoutClassName = TryReadStaticReadonlyField(
                        filtersWindowBaseType,
                        "k_FoldoutClass",
                        out string foldoutClassName);
                    bool hasToggleClassName = TryReadStaticReadonlyField(
                        filtersWindowBaseType,
                        "k_ToggleClass",
                        out string toggleClassName);

                    if (visibilityFilterTarget == null ||
                        visibilityFilterTarget.IsStatic ||
                        visibilityFilterTarget.ReturnType != typeof(bool) ||
                        filtersDisplayTarget == null ||
                        filtersDisplayTarget.IsStatic ||
                        filtersDisplayTarget.ReturnType != typeof(void) ||
                        filtersSizeTarget == null ||
                        filtersSizeTarget.IsStatic ||
                        filtersSizeTarget.ReturnType != typeof(Vector2) ||
                        filtersField == null || filtersField.IsStatic ||
                        containerField == null || containerField.IsStatic ||
                        !typeof(VisualElement).IsAssignableFrom(
                            containerField.FieldType) ||
                        filtersType == null ||
                        labelsProperty == null ||
                        !labelsProperty.CanRead || !labelsProperty.CanWrite ||
                        labelsProperty.PropertyType != typeof(List<string>) ||
                        categoriesProperty == null ||
                        !categoriesProperty.CanRead ||
                        !categoriesProperty.CanWrite ||
                        categoriesProperty.PropertyType != typeof(List<string>) ||
                        statusField == null || statusField.IsStatic ||
                        !statusField.FieldType.IsEnum ||
                        !Enum.IsDefined(
                            statusField.FieldType,
                            PackageManagerSubmoduleNativePage
                                .DownloadedFilterStatusName) ||
                        cloneFiltersMethod == null ||
                        cloneFiltersMethod.IsStatic ||
                        cloneFiltersMethod.ReturnType != filtersType ||
                        pageFiltersProperty == null ||
                        !pageFiltersProperty.CanRead ||
                        pageFiltersProperty.PropertyType != filtersType ||
                        visualStatesProperty == null ||
                        !visualStatesProperty.CanRead ||
                        orderedGroupNamesProperty == null ||
                        !orderedGroupNamesProperty.CanRead ||
                        !typeof(IEnumerable<string>).IsAssignableFrom(
                            orderedGroupNamesProperty.PropertyType) ||
                        updatePageFiltersMethod == null ||
                        updatePageFiltersMethod.IsStatic ||
                        updatePageFiltersMethod.ReturnType != typeof(bool) ||
                        notifyFiltersChangedMethod == null ||
                        notifyFiltersChangedMethod.IsStatic ||
                        notifyFiltersChangedMethod.ReturnType != typeof(void) ||
                        !hasMaximumHeight || maximumHeight <= 0 ||
                        !hasFoldoutHeight || foldoutHeight <= 0 ||
                        !hasToggleHeight || toggleHeight <= 0 ||
                        !hasFoldoutClassName ||
                        string.IsNullOrWhiteSpace(foldoutClassName) ||
                        !hasToggleClassName ||
                        string.IsNullOrWhiteSpace(toggleClassName))
                    {
                        return null;
                    }

                    return new LegacyPageFilterContract(
                        extensionPageType,
                        packageType,
                        filtersWindowType,
                        filtersType,
                        visibilityFilterTarget,
                        filtersDisplayTarget,
                        filtersSizeTarget,
                        filtersField,
                        containerField,
                        statusField,
                        labelsProperty,
                        categoriesProperty,
                        pageFiltersProperty,
                        visualStatesProperty,
                        orderedGroupNamesProperty,
                        cloneFiltersMethod,
                        updatePageFiltersMethod,
                        notifyFiltersChangedMethod,
                        maximumHeight,
                        foldoutHeight,
                        toggleHeight,
                        foldoutClassName,
                        toggleClassName);
                }
                catch
                {
                    return null;
                }
            }

            private static FieldInfo FindDeclaredFieldInHierarchy(
                Type type,
                string fieldName)
            {
                for (Type current = type;
                     current != null;
                     current = current.BaseType)
                {
                    FieldInfo field = current.GetField(
                        fieldName,
                        AnyInstance | BindingFlags.DeclaredOnly);
                    if (field != null)
                        return field;
                }

                return null;
            }

            private static MethodInfo FindDeclaredMethodInHierarchy(
                Type type,
                string methodName,
                Type[] parameterTypes,
                Type returnType)
            {
                for (Type current = type;
                     current != null;
                     current = current.BaseType)
                {
                    MethodInfo method = current.GetMethod(
                        methodName,
                        AnyInstance | BindingFlags.DeclaredOnly,
                        null,
                        parameterTypes,
                        null);
                    if (method != null &&
                        !method.IsStatic &&
                        method.ReturnType == returnType)
                    {
                        return method;
                    }
                }

                return null;
            }

            private static bool TryReadLiteralField<T>(
                Type declaringType,
                string fieldName,
                out T value)
            {
                value = default;
                FieldInfo field = declaringType?.GetField(
                    fieldName,
                    AnyStatic | BindingFlags.DeclaredOnly);
                if (field == null ||
                    !field.IsStatic ||
                    !field.IsLiteral ||
                    field.FieldType != typeof(T))
                {
                    return false;
                }

                object rawValue = field.GetRawConstantValue();
                if (!(rawValue is T typedValue))
                    return false;

                value = typedValue;
                return true;
            }

            private static bool TryReadStaticReadonlyField<T>(
                Type declaringType,
                string fieldName,
                out T value)
            {
                value = default;
                FieldInfo field = declaringType?.GetField(
                    fieldName,
                    AnyStatic | BindingFlags.DeclaredOnly);
                if (field == null ||
                    !field.IsStatic ||
                    !field.IsInitOnly ||
                    field.FieldType != typeof(T))
                {
                    return false;
                }

                object rawValue = field.GetValue(null);
                if (!(rawValue is T typedValue))
                    return false;

                value = typedValue;
                return true;
            }
        }

        private static bool TryGetPageVisibilityFilterContract(
            out PageVisibilityFilterContract contract)
        {
            contract = pageVisibilityFilterContract;
            if (contract != null)
                return true;

            // Package Manager assemblies can load after this class during Editor
            // startup. Cache only a complete positive probe so a delayed load can
            // be retried and a partial contract never reaches the filter postfix.
            PageVisibilityFilterContract candidate =
                PageVisibilityFilterContract.TryCreate();
            if (candidate == null)
                return false;

            lock (PageVisibilityFilterContractGate)
            {
                if (pageVisibilityFilterContract == null)
                    pageVisibilityFilterContract = candidate;
                contract = pageVisibilityFilterContract;
                return true;
            }
        }

        private sealed class PageVisibilityFilterContract
        {
            private PageVisibilityFilterContract(
                MethodInfo visibilityFilterTarget,
                FieldInfo packageDatabaseField,
                MethodInfo packageLookupMethod,
                PropertyInfo filtersProperty,
                PropertyInfo statusProperty,
                PropertyInfo labelsProperty,
                PropertyInfo categoriesProperty,
                PropertyInfo supportedCategoriesProperty,
                PropertyInfo visualStatesProperty,
                PropertyInfo orderedGroupNamesProperty,
                MethodInfo supportedFiltersRefreshTarget)
            {
                VisibilityFilterTarget = visibilityFilterTarget;
                PackageDatabaseField = packageDatabaseField;
                PackageLookupMethod = packageLookupMethod;
                FiltersProperty = filtersProperty;
                StatusProperty = statusProperty;
                LabelsProperty = labelsProperty;
                CategoriesProperty = categoriesProperty;
                SupportedCategoriesProperty = supportedCategoriesProperty;
                VisualStatesProperty = visualStatesProperty;
                OrderedGroupNamesProperty = orderedGroupNamesProperty;
                SupportedFiltersRefreshTarget = supportedFiltersRefreshTarget;
            }

            internal MethodInfo VisibilityFilterTarget { get; }
            internal FieldInfo PackageDatabaseField { get; }
            internal MethodInfo PackageLookupMethod { get; }
            internal PropertyInfo FiltersProperty { get; }
            internal PropertyInfo StatusProperty { get; }
            internal PropertyInfo LabelsProperty { get; }
            internal PropertyInfo CategoriesProperty { get; }
            internal PropertyInfo SupportedCategoriesProperty { get; }
            internal PropertyInfo VisualStatesProperty { get; }
            internal PropertyInfo OrderedGroupNamesProperty { get; }
            internal MethodInfo SupportedFiltersRefreshTarget { get; }

            internal static PageVisibilityFilterContract TryCreate()
            {
                MethodInfo visibilityFilterTarget =
                    GetPageVisibilityFilterTarget();
                FieldInfo packageDatabaseField = GetPagePackageDatabaseField();
                MethodInfo packageLookupMethod = GetPagePackageLookupMethod();
                PropertyInfo filtersProperty = GetPageFiltersProperty();
                PropertyInfo statusProperty = GetPageFilterStatusProperty();
                PropertyInfo labelsProperty = GetPageFilterLabelsProperty();
                PropertyInfo categoriesProperty = GetPageFilterCategoriesProperty();
                PropertyInfo supportedCategoriesProperty =
                    GetPageFilterSupportedCategoriesProperty();
                PropertyInfo visualStatesProperty = GetPageVisualStatesProperty();
                PropertyInfo orderedGroupNamesProperty =
                    GetPageOrderedGroupNamesProperty();
                MethodInfo supportedFiltersRefreshTarget =
                    GetPageSupportedFiltersRefreshTarget();
                if (visibilityFilterTarget == null ||
                    packageDatabaseField == null ||
                    packageLookupMethod == null ||
                    filtersProperty == null ||
                    statusProperty == null ||
                    labelsProperty == null ||
                    categoriesProperty == null ||
                    supportedCategoriesProperty == null ||
                    visualStatesProperty == null ||
                    orderedGroupNamesProperty == null ||
                    supportedFiltersRefreshTarget == null)
                {
                    return null;
                }

                return new PageVisibilityFilterContract(
                    visibilityFilterTarget,
                    packageDatabaseField,
                    packageLookupMethod,
                    filtersProperty,
                    statusProperty,
                    labelsProperty,
                    categoriesProperty,
                    supportedCategoriesProperty,
                    visualStatesProperty,
                    orderedGroupNamesProperty,
                    supportedFiltersRefreshTarget);
            }
        }

        private static string GetPageId(object page)
        {
            return page?.GetType().GetProperty("id", AnyInstance)
                ?.GetValue(page, null) as string;
        }

        private static void OnDiscoverySnapshotChanged()
        {
            PackageManagerGitHubDiscoverySnapshot snapshot =
                PackageManagerGitHubDiscovery.Current;
            IReadOnlyList<PackageManagerGitHubRepository> repositories =
                snapshot?.Repositories ??
                PackageManagerGitHubDiscoverySnapshot.Empty.Repositories;
            bool shouldRebuild = ShouldRebuildPageForSnapshot(
                observedRepositories,
                repositories,
                false);
            observedRepositories = repositories;
            QueuePresentationRefresh(shouldRebuild, false, false);
        }

        private static void OnSubmoduleSnapshotChanged()
        {
            QueuePresentationRefresh(true, true, false);
        }

        /// <summary>
        /// Queues one post-event rebuild after package registration state changes.
        /// Unity does not define subscriber order for registeredPackages, so the
        /// native page must refresh after every subscriber has observed the event
        /// rather than relying on our cache invalidation callback running first.
        /// </summary>
        internal static void QueueForcedPackageStateRefresh()
        {
            QueuePresentationRefresh(true, true, true);
        }

        internal static bool ShouldRebuildPageForSnapshot(
            IReadOnlyList<PackageManagerGitHubRepository> previousRepositories,
            IReadOnlyList<PackageManagerGitHubRepository> currentRepositories,
            bool submoduleSnapshotChanged)
        {
            return submoduleSnapshotChanged ||
                   PackageManagerGitHubPackageProjection
                       .IsRepositoryCatalogueChanged(
                           previousRepositories,
                           currentRepositories);
        }

        internal static bool ShouldExplicitlyRebuildPage(
            bool presentationRequiresRebuild,
            bool packageDatabaseAlreadyRebuilt,
            bool forceExplicitRebuild)
        {
            return presentationRequiresRebuild &&
                   (forceExplicitRebuild ||
                    !packageDatabaseAlreadyRebuilt);
        }

        private static void QueuePresentationRefresh(
            bool shouldRebuild,
            bool forceExplicitRebuild,
            bool refreshAllPackageManagerPages)
        {
            if (shuttingDown)
                return;

            presentationRebuildQueued |= shouldRebuild;
            forcedPresentationRebuildQueued |= forceExplicitRebuild;
            allPackageManagerPagesRefreshQueued |=
                refreshAllPackageManagerPages;
            if (presentationRefreshQueued)
                return;

            presentationRefreshQueued = true;
            EditorApplication.delayCall += RefreshOpenPackageManagerPages;
        }

        private static void RefreshOpenPackageManagerPages()
        {
            bool shouldRebuild = presentationRebuildQueued;
            bool forceExplicitRebuild =
                forcedPresentationRebuildQueued;
            bool refreshAllPackageManagerPages =
                allPackageManagerPagesRefreshQueued;
            IReadOnlyList<PackageManagerGitHubRepository> repositories =
                PackageManagerGitHubDiscovery.Current.Repositories;
            bool packageDatabaseAlreadyRebuilt =
                shouldRebuild &&
                !forceExplicitRebuild &&
                PackageManagerGitHubPackageProjection
                    .DidLastReconcileUpdatePackageDatabase(repositories);
            bool shouldExplicitlyRebuild = ShouldExplicitlyRebuildPage(
                shouldRebuild,
                packageDatabaseAlreadyRebuilt,
                forceExplicitRebuild);
            presentationRefreshQueued = false;
            presentationRebuildQueued = false;
            forcedPresentationRebuildQueued = false;
            allPackageManagerPagesRefreshQueued = false;
            if (shuttingDown)
                return;

            try
            {
                Type windowType = PackageManagerSubmoduleHarmonyPatch.FindLoadedType(
                    PackageManagerSubmoduleHarmonyPatch.PackageManagerWindowTypeName);
                MethodInfo statusUpdate = GetPackageStatusUpdateMethod();
                if (windowType == null)
                    return;

                foreach (EditorWindow window in Resources.FindObjectsOfTypeAll<EditorWindow>())
                {
                    if (window == null || window.GetType() != windowType)
                        continue;

                    object root = GetFieldValue(window, "m_Root");
                    object pageManager = GetFieldValue(root, "m_PageManager");
                    object activePage = GetPropertyValue(pageManager, "activePage");
                    bool isGitHubPage = IsGitHubPage(activePage);
                    if (!isGitHubPage && !refreshAllPackageManagerPages)
                        continue;

                    if (shouldRebuild)
                    {
                        if (shouldExplicitlyRebuild)
                        {
                            if (isGitHubPage)
                            {
                                PackageManagerSubmoduleNativePage.TryConfigureFilters(
                                    activePage);
                            }
                            PackageManagerSubmoduleHarmonyPatch
                                .TryRebuildPackageManagerWindow(window);
                        }

                        // PackageDatabase.UpdatePackages and an explicit rebuild both
                        // refresh visualStates. Read the final organization groups as
                        // a fallback for installed packages absent from discovery.
                        if (isGitHubPage)
                        {
                            PackageManagerSubmoduleNativePage.TryConfigureFilters(
                                activePage);
                        }
                    }

                    object statusBar = GetPropertyValue(root, "packageStatusbar");
                    statusUpdate?.Invoke(statusBar, null);
                    window.rootVisualElement?.MarkDirtyRepaint();
                    window.Repaint();
                }
            }
            catch
            {
                // Discovery remains functional even if native presentation drifts.
            }
        }

        private static void EnsureDiscoveryForOpenGitHubPages()
        {
            try
            {
                Type windowType = PackageManagerSubmoduleHarmonyPatch.FindLoadedType(
                    PackageManagerSubmoduleHarmonyPatch.PackageManagerWindowTypeName);
                if (windowType == null)
                    return;

                foreach (EditorWindow window in Resources.FindObjectsOfTypeAll<EditorWindow>())
                {
                    if (window == null || window.GetType() != windowType)
                        continue;

                    object root = GetFieldValue(window, "m_Root");
                    object pageManager = GetFieldValue(root, "m_PageManager");
                    if (!IsGitHubPage(GetPropertyValue(pageManager, "activePage")))
                        continue;

                    // Discovery startup is intentionally independent from the
                    // installed-submodule reader; the latter initialized itself
                    // and watches every installed-state invalidation source.
                    PackageManagerGitHubDiscovery.EnsureStarted();
                }
            }
            catch
            {
                // The activation prefix remains the primary lazy-start seam.
            }
        }

        private static object GetFieldValue(object instance, string fieldName)
        {
            for (Type type = instance?.GetType(); type != null; type = type.BaseType)
            {
                FieldInfo field = type.GetField(fieldName, AnyInstance);
                if (field != null)
                    return field.GetValue(instance);
            }

            return null;
        }

        private static object GetPropertyValue(object instance, string propertyName)
        {
            for (Type type = instance?.GetType(); type != null; type = type.BaseType)
            {
                PropertyInfo property = type.GetProperty(propertyName, AnyInstance);
                if (property != null && property.GetIndexParameters().Length == 0)
                    return property.GetValue(instance, null);
            }

            return null;
        }

        private static void TryPatchDelayed()
        {
            if (TryPatch())
                EnsureDiscoveryForOpenGitHubPages();
        }

        private static void OnBeforeAssemblyReload()
        {
            shuttingDown = true;
            lock (PageVisibilityFilterContractGate)
                pageVisibilityFilterContract = null;
            lock (LegacyPageFilterContractGate)
                legacyPageFilterContract = null;
            EditorApplication.delayCall -= RefreshOpenPackageManagerPages;
            PackageManagerGitHubDiscovery.SnapshotChanged -=
                OnDiscoverySnapshotChanged;
            PackageManagerSubmoduleSnapshot.SnapshotChanged -=
                OnSubmoduleSnapshotChanged;
            AssemblyReloadEvents.beforeAssemblyReload -= OnBeforeAssemblyReload;
            try
            {
                harmony?.UnpatchAll(HarmonyId);
            }
            catch
            {
                // The managed domain is already tearing down.
            }
        }
    }
}
