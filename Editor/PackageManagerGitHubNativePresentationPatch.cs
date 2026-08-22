using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEngine;

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

        private const BindingFlags AnyInstance =
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private const BindingFlags AnyStatic =
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        private static Harmony harmony;
        private static bool shuttingDown;
        private static bool patchRequested = true;
        private static bool presentationRefreshQueued;
        private static double nextAttempt;

        static PackageManagerGitHubNativePresentationPatch()
        {
            AppDomain.CurrentDomain.AssemblyLoad += OnAssemblyLoad;
            PackageManagerGitHubDiscovery.SnapshotChanged +=
                OnDiscoverySnapshotChanged;
            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;
            EditorApplication.update += RetryOnUpdate;
            EditorApplication.delayCall += TryPatchDelayed;
        }

        internal static bool TryPatch()
        {
            if (shuttingDown)
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
                    PatchPrefixIfNeeded(target, GetPageActivationPrefix());
                }

                foreach (MethodInfo target in GetPageLoadingTargets())
                {
                    foundTarget = true;
                    PatchPostfixIfNeeded(target, GetPageLoadingPostfix());
                }

                MethodInfo visibilityFilterTarget =
                    GetPageVisibilityFilterTarget();
                if (visibilityFilterTarget != null)
                {
                    foundTarget = true;
                    PatchPostfixIfNeeded(
                        visibilityFilterTarget,
                        GetPageVisibilityFilterPostfix());
                }

                patchRequested = !foundTarget;
                return foundTarget;
            }
            catch
            {
                patchRequested = true;
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

        internal static bool HasPageVisibilityFilterContract()
        {
            return GetPageVisibilityFilterTarget() != null &&
                   GetPagePackageDatabaseField() != null &&
                   GetPagePackageLookupMethod() != null &&
                   GetPageFiltersProperty() != null &&
                   GetPageFilterLabelsProperty() != null;
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

        internal static MethodInfo GetPageActivationPrefix()
        {
            return typeof(PackageManagerGitHubNativePresentationPatch).GetMethod(
                nameof(PageActivationPrefix),
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

        internal static bool IsPrefixApplied(MethodBase target, MethodInfo prefix)
        {
            if (target == null || prefix == null)
                return false;

            try
            {
                Patches patches = Harmony.GetPatchInfo(target);
                if (patches == null)
                    return false;
                foreach (Patch patch in patches.Prefixes)
                {
                    if (patch.owner == HarmonyId && patch.PatchMethod == prefix)
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

        private static void PatchPrefixIfNeeded(
            MethodInfo target,
            MethodInfo prefix)
        {
            if (target == null || prefix == null || IsPrefixApplied(target, prefix))
                return;

            harmony.Patch(target, prefix: new HarmonyMethod(prefix));
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

                PackageManagerSubmoduleSnapshot.Refresh();
                if (!PackageManagerGitHubDiscovery.IsStarted)
                    PackageManagerGitHubDiscovery.EnsureStarted();
                else if (!PackageManagerGitHubDiscovery.IsLoading)
                    PackageManagerGitHubDiscovery.Refresh();
            }
            catch
            {
                // Unity's own refresh remains authoritative.
            }
        }

        private static void PageActivationPrefix(object __0)
        {
            try
            {
                if (!IsGitHubPage(__0))
                    return;

                PackageManagerSubmoduleSnapshot.Refresh();
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
                // Unity remains authoritative for search and its built-in status
                // predicates. Our visibility facet can only narrow a native match.
                if (!__result || !IsGitHubPage(__instance) ||
                    !TryGetSelectedLabels(
                        __instance,
                        out IReadOnlyList<string> selectedLabels) ||
                    MatchesRepositoryVisibility(null, selectedLabels))
                {
                    return;
                }

                if (!TryGetPagePackage(__instance, __0, out object package))
                    return;

                bool? isPrivate = TryGetPackageRepositoryPrivacy(
                    package,
                    PackageManagerGitHubDiscovery.Current,
                    out bool resolvedPrivacy)
                    ? resolvedPrivacy
                    : null;
                __result = MatchesRepositoryVisibility(
                    isPrivate,
                    selectedLabels);
            }
            catch
            {
                // Reflection drift must never hide packages outside our verified
                // visibility predicate or interfere with another Package Manager page.
            }
        }

        private static bool IsGitHubPage(object page)
        {
            return string.Equals(
                GetPageId(page),
                PackageManagerSubmoduleNativePage.ExtensionPageId,
                StringComparison.Ordinal);
        }

        private static bool TryGetSelectedLabels(
            object page,
            out IReadOnlyList<string> selectedLabels)
        {
            selectedLabels = null;
            PropertyInfo filtersProperty = GetPageFiltersProperty();
            PropertyInfo labelsProperty = GetPageFilterLabelsProperty();
            if (filtersProperty == null || labelsProperty == null)
                return false;

            object filters = filtersProperty.GetValue(page, null);
            object labels = labelsProperty.GetValue(filters, null);
            if (!(labels is IEnumerable<string> enumerable))
                return false;

            selectedLabels = new List<string>(enumerable);
            return true;
        }

        private static bool TryGetPagePackage(
            object page,
            string packageUniqueId,
            out object package)
        {
            package = null;
            if (page == null || string.IsNullOrEmpty(packageUniqueId))
                return false;

            FieldInfo packageDatabaseField = GetPagePackageDatabaseField();
            MethodInfo getPackage = GetPagePackageLookupMethod();
            object packageDatabase = packageDatabaseField?.GetValue(page);
            if (packageDatabase == null || getPackage == null)
                return false;

            package = getPackage.Invoke(
                packageDatabase,
                new object[] { packageUniqueId });
            return package != null;
        }

        private static string GetPageId(object page)
        {
            return page?.GetType().GetProperty("id", AnyInstance)
                ?.GetValue(page, null) as string;
        }

        private static void OnDiscoverySnapshotChanged()
        {
            if (shuttingDown || presentationRefreshQueued)
                return;

            presentationRefreshQueued = true;
            EditorApplication.delayCall += RefreshOpenGitHubPages;
        }

        private static void RefreshOpenGitHubPages()
        {
            presentationRefreshQueued = false;
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
                    if (!IsGitHubPage(activePage))
                        continue;

                    PackageManagerSubmoduleHarmonyPatch.TryRebuildPackageManagerWindow(
                        window);
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

                    PackageManagerSubmoduleSnapshot.Refresh();
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

        private static void OnAssemblyLoad(object _, AssemblyLoadEventArgs __)
        {
            patchRequested = true;
        }

        private static void TryPatchDelayed()
        {
            if (TryPatch())
                EnsureDiscoveryForOpenGitHubPages();
        }

        private static void RetryOnUpdate()
        {
            if (shuttingDown ||
                !patchRequested ||
                EditorApplication.timeSinceStartup < nextAttempt)
            {
                return;
            }

            nextAttempt = EditorApplication.timeSinceStartup + 1d;
            if (TryPatch())
                EnsureDiscoveryForOpenGitHubPages();
        }

        private static void OnBeforeAssemblyReload()
        {
            shuttingDown = true;
            EditorApplication.update -= RetryOnUpdate;
            EditorApplication.delayCall -= RefreshOpenGitHubPages;
            PackageManagerGitHubDiscovery.SnapshotChanged -=
                OnDiscoverySnapshotChanged;
            AppDomain.CurrentDomain.AssemblyLoad -= OnAssemblyLoad;
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
