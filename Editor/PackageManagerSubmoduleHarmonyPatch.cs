using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace MartinCalander.GitSubmoduleManager.Editor
{
    [InitializeOnLoad]
    internal static class PackageManagerSubmoduleHarmonyPatch
    {
        internal const string HarmonyId =
            "com.martincalander.gitsubmodulemanager.package-manager-presentation";
        internal const string PackageVersionInterfaceTypeName =
            "UnityEditor.PackageManager.UI.Internal.IPackageVersion";
        internal const string DynamicTagLabelTypeName =
            "UnityEditor.PackageManager.UI.Internal.PackageDynamicTagLabel";
        internal const string LegacyTagLabelTypeName =
            "UnityEditor.PackageManager.UI.Internal.PackageTagLabel";
        internal const string SourceInfoCardTypeName =
            "UnityEditor.PackageManager.UI.Internal.SourceInfoCard";
        internal const string PackageToolbarTypeName =
            "UnityEditor.PackageManager.UI.Internal.PackageToolbar";
        internal const string RemoveCustomActionTypeName =
            "UnityEditor.PackageManager.UI.Internal.RemoveCustomAction";
        internal const string PackageOperationDispatcherTypeName =
            "UnityEditor.PackageManager.UI.Internal.PackageOperationDispatcher";
        internal const string PackageInterfaceTypeName =
            "UnityEditor.PackageManager.UI.Internal.IPackage";
        internal const string PackageManagerWindowTypeName =
            "UnityEditor.PackageManager.UI.PackageManagerWindow";
        internal const string RefreshMethodName = "Refresh";
        internal const string LegacyCreateTagLabelMethodName = "CreateTagLabel";
        internal const string TriggerActionImplementationMethodName =
            "TriggerActionImplementation";
        internal const string RemoveEmbeddedMethodName = "RemoveEmbedded";
        internal const string PackageManagerRootFieldName = "m_Root";
        internal const string PageManagerFieldName = "m_PageManager";
        internal const string PageManagerPropertyName = "pageManager";
        internal const string ActivePagePropertyName = "activePage";
        internal const string PageIdPropertyName = "id";

        private const BindingFlags AnyInstance =
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private const BindingFlags AnyStatic =
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        private static Harmony harmony;
        private static bool shuttingDown;
        private static string lastPatchError = string.Empty;
        private static readonly ConditionalWeakTable<VisualElement, DeferredTagState>
            DeferredTags = new ConditionalWeakTable<VisualElement, DeferredTagState>();

        private sealed class DeferredTagState
        {
            internal object PackageVersion;
            internal bool IsRegistered;
        }

        static PackageManagerSubmoduleHarmonyPatch()
        {
            PackageManagerSubmoduleSnapshot.SnapshotChanged += OnSnapshotChanged;
            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;
            EditorApplication.delayCall += TryPatchAndRefresh;
        }

        internal static string LastPatchError => lastPatchError;

        internal static bool TryPatch()
        {
            if (shuttingDown)
                return false;

            try
            {
                harmony = harmony ?? new Harmony(HarmonyId);
                MethodInfo tagRefreshPostfix = GetTagRefreshPostfixMethod();
                MethodInfo tagFactoryPostfix = GetTagFactoryPostfixMethod();

                foreach (MethodInfo target in GetTagTargetMethods())
                {
                    MethodInfo postfix = target.IsStatic
                        ? tagFactoryPostfix
                        : tagRefreshPostfix;
                    PatchPostfixIfNeeded(target, postfix);
                }

                MethodInfo sourceTarget = GetSourceTargetMethod();
                if (sourceTarget != null)
                    PatchPostfixIfNeeded(sourceTarget, GetSourceRefreshPostfixMethod());

                MethodInfo toolbarPostfix = GetPackageToolbarRefreshPostfixMethod();
                foreach (MethodInfo toolbarTarget in GetPackageToolbarTargetMethods())
                    PatchPostfixIfNeeded(toolbarTarget, toolbarPostfix);

                MethodInfo removeCustomActionTarget =
                    GetRemoveCustomActionTargetMethod();
                if (removeCustomActionTarget != null)
                {
                    PatchPrefixIfNeeded(
                        removeCustomActionTarget,
                        GetRemoveCustomActionPrefixMethod());
                }

                MethodInfo removeEmbeddedTarget =
                    GetPackageOperationDispatcherRemoveEmbeddedTargetMethod();
                if (removeEmbeddedTarget != null)
                {
                    PatchPrefixIfNeeded(
                        removeEmbeddedTarget,
                        GetPackageOperationDispatcherRemoveEmbeddedPrefixMethod());
                }

                bool tagPatchApplied = IsAnyTagPatchApplied();
                if (tagPatchApplied)
                    lastPatchError = string.Empty;
                return tagPatchApplied;
            }
            catch (Exception exception)
            {
                lastPatchError = GitHubUtility.SanitizeUiDiagnostic(exception.Message);
                return false;
            }
        }

        internal static IReadOnlyList<MethodInfo> GetTagTargetMethods()
        {
            var methods = new List<MethodInfo>();
            Type dynamicType = FindLoadedType(DynamicTagLabelTypeName);
            AddMatchingInstanceRefresh(dynamicType, methods);

            Type legacyType = FindLoadedType(LegacyTagLabelTypeName);
            AddMatchingInstanceRefresh(legacyType, methods);
            if (legacyType != null)
            {
                foreach (MethodInfo method in legacyType.GetMethods(AnyStatic))
                {
                    ParameterInfo[] parameters = method.GetParameters();
                    if (method.Name == LegacyCreateTagLabelMethodName &&
                        HasSupportedTagParameters(parameters))
                    {
                        methods.Add(method);
                    }
                }
            }

            return methods;
        }

        internal static MethodInfo GetSourceTargetMethod()
        {
            Type sourceType = FindLoadedType(SourceInfoCardTypeName);
            if (sourceType == null)
                return null;

            foreach (MethodInfo method in sourceType.GetMethods(AnyInstance))
            {
                ParameterInfo[] parameters = method.GetParameters();
                if (method.Name == RefreshMethodName &&
                    method.ReturnType == typeof(void) &&
                    parameters.Length == 1 &&
                    IsPackageVersionParameter(parameters[0]))
                {
                    return method;
                }
            }

            return null;
        }

        internal static IReadOnlyList<MethodInfo> GetPackageToolbarTargetMethods()
        {
            var methods = new List<MethodInfo>();
            Type toolbarType = FindLoadedType(PackageToolbarTypeName);
            if (toolbarType == null)
                return methods;

            foreach (MethodInfo method in toolbarType.GetMethods(AnyInstance))
            {
                ParameterInfo[] parameters = method.GetParameters();
                if (method.Name != RefreshMethodName ||
                    method.IsStatic ||
                    method.ReturnType != typeof(void) ||
                    parameters.Length < 1 ||
                    parameters.Length > 2 ||
                    parameters[0].ParameterType.FullName != PackageInterfaceTypeName ||
                    (parameters.Length == 2 &&
                     parameters[1].ParameterType.FullName !=
                     PackageVersionInterfaceTypeName))
                {
                    continue;
                }

                methods.Add(method);
            }

            return methods;
        }

        internal static MethodInfo GetRemoveCustomActionTargetMethod()
        {
            Type actionType = FindLoadedType(RemoveCustomActionTypeName);
            if (actionType == null)
                return null;

            foreach (MethodInfo method in actionType.GetMethods(
                         AnyInstance | BindingFlags.DeclaredOnly))
            {
                ParameterInfo[] parameters = method.GetParameters();
                if (method.Name == TriggerActionImplementationMethodName &&
                    !method.IsStatic &&
                    method.ReturnType == typeof(bool) &&
                    parameters.Length == 1 &&
                    IsPackageVersionParameter(parameters[0]))
                {
                    return method;
                }
            }

            return null;
        }

        internal static MethodInfo
            GetPackageOperationDispatcherRemoveEmbeddedTargetMethod()
        {
            Type dispatcherType = FindLoadedType(PackageOperationDispatcherTypeName);
            if (dispatcherType == null)
                return null;

            foreach (MethodInfo method in dispatcherType.GetMethods(
                         AnyInstance | BindingFlags.DeclaredOnly))
            {
                ParameterInfo[] parameters = method.GetParameters();
                if (method.Name == RemoveEmbeddedMethodName &&
                    !method.IsStatic &&
                    method.ReturnType == typeof(void) &&
                    parameters.Length == 1 &&
                    parameters[0].ParameterType.FullName ==
                    PackageInterfaceTypeName)
                {
                    return method;
                }
            }

            return null;
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
                    // Continue through optional and partially loaded Editor modules.
                }
            }

            return null;
        }

        internal static MethodInfo GetTagRefreshPostfixMethod()
        {
            return typeof(PackageManagerSubmoduleHarmonyPatch).GetMethod(
                nameof(TagRefreshPostfix),
                AnyStatic);
        }

        internal static MethodInfo GetTagFactoryPostfixMethod()
        {
            return typeof(PackageManagerSubmoduleHarmonyPatch).GetMethod(
                nameof(TagFactoryPostfix),
                AnyStatic);
        }

        internal static MethodInfo GetSourceRefreshPostfixMethod()
        {
            return typeof(PackageManagerSubmoduleHarmonyPatch).GetMethod(
                nameof(SourceRefreshPostfix),
                AnyStatic);
        }

        internal static MethodInfo GetPackageToolbarRefreshPostfixMethod()
        {
            return typeof(PackageManagerSubmoduleHarmonyPatch).GetMethod(
                nameof(PackageToolbarRefreshPostfix),
                AnyStatic);
        }

        internal static MethodInfo GetRemoveCustomActionPrefixMethod()
        {
            return typeof(PackageManagerSubmoduleHarmonyPatch).GetMethod(
                nameof(RemoveCustomActionPrefix),
                AnyStatic);
        }

        internal static MethodInfo
            GetPackageOperationDispatcherRemoveEmbeddedPrefixMethod()
        {
            return typeof(PackageManagerSubmoduleHarmonyPatch).GetMethod(
                nameof(PackageOperationDispatcherRemoveEmbeddedPrefix),
                AnyStatic);
        }

        internal static bool IsAnyTagPatchApplied()
        {
            MethodInfo refreshPostfix = GetTagRefreshPostfixMethod();
            MethodInfo factoryPostfix = GetTagFactoryPostfixMethod();
            foreach (MethodInfo target in GetTagTargetMethods())
            {
                if (IsPatchApplied(
                        target,
                        target.IsStatic ? factoryPostfix : refreshPostfix))
                {
                    return true;
                }
            }

            return false;
        }

        internal static bool IsSourcePatchApplied()
        {
            return IsPatchApplied(
                GetSourceTargetMethod(),
                GetSourceRefreshPostfixMethod());
        }

        internal static bool IsPackageToolbarPatchApplied()
        {
            MethodInfo postfix = GetPackageToolbarRefreshPostfixMethod();
            foreach (MethodInfo target in GetPackageToolbarTargetMethods())
            {
                if (IsPatchApplied(target, postfix))
                    return true;
            }

            return false;
        }

        internal static bool IsRemoveCustomActionPatchApplied()
        {
            return IsPrefixApplied(
                GetRemoveCustomActionTargetMethod(),
                GetRemoveCustomActionPrefixMethod());
        }

        internal static bool
            IsPackageOperationDispatcherRemoveEmbeddedPatchApplied()
        {
            return IsPrefixApplied(
                GetPackageOperationDispatcherRemoveEmbeddedTargetMethod(),
                GetPackageOperationDispatcherRemoveEmbeddedPrefixMethod());
        }

        internal static bool IsPatchApplied(MethodBase target, MethodInfo postfix)
        {
            if (target == null || postfix == null)
                return false;

            try
            {
                Patches patchInfo = Harmony.GetPatchInfo(target);
                if (patchInfo == null)
                    return false;

                foreach (Patch patch in patchInfo.Postfixes)
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
                Patches patchInfo = Harmony.GetPatchInfo(target);
                if (patchInfo == null)
                    return false;

                foreach (Patch patch in patchInfo.Prefixes)
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

        internal static void RefreshOpenPackageManagerWindows()
        {
            Type packageManagerWindowType = FindLoadedType(PackageManagerWindowTypeName);
            if (packageManagerWindowType == null)
                return;

            foreach (EditorWindow window in Resources.FindObjectsOfTypeAll<EditorWindow>())
            {
                if (window == null || window.GetType() != packageManagerWindowType)
                    continue;

                TryRebuildPackageManagerWindow(window);
                window.rootVisualElement?.MarkDirtyRepaint();
                window.Repaint();
            }
        }

        internal static bool TryRebuildPackageManagerWindow(EditorWindow window)
        {
            if (window == null ||
                window.GetType().FullName != PackageManagerWindowTypeName)
            {
                return false;
            }

            try
            {
                object root = GetFieldValue(
                    window,
                    PackageManagerRootFieldName);
                object pageManager = GetFieldValue(
                    root,
                    PageManagerFieldName);
                if (pageManager == null)
                    return false;

                object activePage =
                    GetPropertyValue(pageManager, ActivePagePropertyName) ??
                    InvokeOptionalCurrentPageGetter(pageManager);
                if (activePage == null)
                    return false;

                bool rebuilt = InvokePageMethod(activePage, "Rebuild", true);
                // Selection notifications refresh the details body and its Source
                // card without starting a UPM network request.
                InvokePageMethod(activePage, "TriggerOnSelectionChanged", false);
                return rebuilt;
            }
            catch
            {
                return false;
            }
        }

        private static void AddMatchingInstanceRefresh(
            Type type,
            ICollection<MethodInfo> methods)
        {
            if (type == null)
                return;

            foreach (MethodInfo method in type.GetMethods(AnyInstance))
            {
                ParameterInfo[] parameters = method.GetParameters();
                if (method.Name == RefreshMethodName &&
                    method.ReturnType == typeof(void) &&
                    HasSupportedTagParameters(parameters))
                {
                    methods.Add(method);
                }
            }
        }

        private static bool HasSupportedTagParameters(ParameterInfo[] parameters)
        {
            return parameters != null &&
                   parameters.Length >= 1 &&
                   parameters.Length <= 2 &&
                   IsPackageVersionParameter(parameters[0]) &&
                   (parameters.Length == 1 ||
                    parameters[1].ParameterType == typeof(bool));
        }

        private static bool IsPackageVersionParameter(ParameterInfo parameter)
        {
            return parameter != null &&
                   parameter.ParameterType.FullName == PackageVersionInterfaceTypeName;
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

        private static bool TryGetFieldValue(
            object instance,
            string fieldName,
            out object value)
        {
            value = null;
            if (instance == null)
                return false;

            try
            {
                for (Type type = instance.GetType(); type != null; type = type.BaseType)
                {
                    FieldInfo field = type.GetField(fieldName, AnyInstance);
                    if (field == null)
                        continue;

                    value = field.GetValue(instance);
                    return true;
                }
            }
            catch
            {
                return false;
            }

            return false;
        }

        private static bool TryGetPropertyValue(
            object instance,
            string propertyName,
            out object value)
        {
            value = null;
            if (instance == null)
                return false;

            try
            {
                for (Type type = instance.GetType(); type != null; type = type.BaseType)
                {
                    PropertyInfo property = type.GetProperty(
                        propertyName,
                        AnyInstance);
                    if (property == null || property.GetIndexParameters().Length != 0)
                        continue;

                    value = property.GetValue(instance, null);
                    return true;
                }
            }
            catch
            {
                return false;
            }

            return false;
        }

        private static object InvokeOptionalCurrentPageGetter(object pageManager)
        {
            foreach (MethodInfo method in pageManager.GetType().GetMethods(AnyInstance))
            {
                if (method.Name != "GetPageFromTab" || method.ContainsGenericParameters)
                    continue;

                ParameterInfo[] parameters = method.GetParameters();
                if (parameters.Length == 0)
                    return method.Invoke(pageManager, null);
                if (parameters.Length == 1 &&
                    (parameters[0].IsOptional ||
                     Nullable.GetUnderlyingType(parameters[0].ParameterType) != null))
                {
                    return method.Invoke(pageManager, new object[] { null });
                }
            }

            return null;
        }

        private static bool InvokePageMethod(
            object page,
            string methodName,
            bool booleanArgument)
        {
            for (Type type = page.GetType(); type != null; type = type.BaseType)
            {
                foreach (MethodInfo method in type.GetMethods(AnyInstance | BindingFlags.DeclaredOnly))
                {
                    if (method.Name != methodName)
                        continue;

                    ParameterInfo[] parameters = method.GetParameters();
                    if (parameters.Length == 0)
                    {
                        method.Invoke(page, null);
                        return true;
                    }

                    if (parameters.Length == 1 &&
                        parameters[0].ParameterType == typeof(bool))
                    {
                        method.Invoke(page, new object[] { booleanArgument });
                        return true;
                    }
                }
            }

            return false;
        }

        private static void PatchPostfixIfNeeded(MethodInfo target, MethodInfo postfix)
        {
            if (target == null || postfix == null || IsPatchApplied(target, postfix))
                return;

            harmony.Patch(target, postfix: new HarmonyMethod(postfix));
        }

        private static void PatchPrefixIfNeeded(MethodInfo target, MethodInfo prefix)
        {
            if (target == null || prefix == null || IsPrefixApplied(target, prefix))
                return;

            harmony.Patch(target, prefix: new HarmonyMethod(prefix));
        }

        private static void TagRefreshPostfix(object __instance, object __0)
        {
            ApplyTagPresentation(__instance, __0);
        }

        private static void TagFactoryPostfix(object __0, object __result)
        {
            ApplyTagPresentation(__result, __0);
        }

        internal static void ApplyTagPresentation(object tagElement, object packageVersion)
        {
            try
            {
                // Refresh can rebind a detached/recycled label before its
                // AttachToPanel callback runs. The newest invocation always
                // owns the binding; unrecognized packages cancel it entirely.
                CancelDeferredTagPresentation(tagElement);
                bool pageResolved = TryGetGitHubPageStateForTag(
                    tagElement,
                    out bool isGitHubPage);
                if (PackageManagerGitHubPackageProjection.TryGetRepository(
                        packageVersion,
                        out PackageManagerGitHubRepository repository))
                {
                    PackageManagerSubmodulePresentation.ResetTagLabelPresentation(
                        tagElement);
                    if (pageResolved && isGitHubPage)
                    {
                        PackageManagerSubmodulePresentation
                            .ApplyRepositoryVisibilityTag(
                                tagElement,
                                repository.IsPrivate);
                    }

                    if (!pageResolved)
                        DeferTagPresentationUntilAttached(tagElement, packageVersion);
                    return;
                }

                if (PackageManagerSubmodulePresentation.TryGetPresentation(
                        packageVersion,
                        out PackageManagerSubmoduleInfo info))
                {
                    PackageManagerSubmodulePresentation.ApplyInstalledTagLabel(
                        tagElement,
                        info,
                        pageResolved && isGitHubPage,
                        PackageManagerGitHubDiscovery.Current);
                    if (!pageResolved)
                        DeferTagPresentationUntilAttached(tagElement, packageVersion);
                    return;
                }

                PackageManagerSubmodulePresentation.ResetTagLabelPresentation(
                    tagElement);
            }
            catch
            {
                // Unity's Package Manager remains authoritative when its internals change.
            }
        }

        internal static bool TryGetGitHubPageState(
            object pageManager,
            out bool isGitHubPage)
        {
            return TryGetGitHubPageState(
                pageManager,
                out isGitHubPage,
                out _);
        }

        internal static bool TryGetGitHubPageState(
            object pageManager,
            out bool isGitHubPage,
            out string compatibilityDiagnostic)
        {
            isGitHubPage = false;
            compatibilityDiagnostic = string.Empty;
            if (pageManager == null)
            {
                compatibilityDiagnostic = "Package Manager page manager is null.";
                return false;
            }

            try
            {
                if (!TryGetPropertyValue(
                        pageManager,
                        ActivePagePropertyName,
                        out object activePage))
                {
                    compatibilityDiagnostic =
                        $"Package Manager property '{ActivePagePropertyName}' is unavailable.";
                    return false;
                }

                if (activePage == null)
                {
                    compatibilityDiagnostic = "Package Manager active page is null.";
                    return false;
                }

                if (!TryGetPropertyValue(
                        activePage,
                        PageIdPropertyName,
                        out object pageIdValue))
                {
                    compatibilityDiagnostic =
                        $"Package Manager active-page property '{PageIdPropertyName}' is unavailable.";
                    return false;
                }

                string pageId = pageIdValue as string;
                if (string.IsNullOrWhiteSpace(pageId))
                {
                    compatibilityDiagnostic = "Package Manager active-page id is empty.";
                    return false;
                }

                isGitHubPage = string.Equals(
                    pageId,
                    PackageManagerSubmoduleNativePage.ExtensionPageId,
                    StringComparison.Ordinal);
                return true;
            }
            catch (Exception exception)
            {
                compatibilityDiagnostic =
                    "Package Manager active-page contract failed: " +
                    GitHubUtility.SanitizeUiDiagnostic(exception.Message);
                return false;
            }
        }

        internal static bool TryGetGitHubPageStateFromRoot(
            object packageManagerRoot,
            out bool isGitHubPage,
            out string compatibilityDiagnostic)
        {
            isGitHubPage = false;
            compatibilityDiagnostic = string.Empty;
            if (packageManagerRoot == null)
            {
                compatibilityDiagnostic = "Package Manager root is null.";
                return false;
            }

            bool fieldRead = TryGetFieldValue(
                packageManagerRoot,
                PageManagerFieldName,
                out object pageManager);
            if (pageManager == null)
            {
                bool propertyRead = TryGetPropertyValue(
                    packageManagerRoot,
                    PageManagerPropertyName,
                    out pageManager);
                if (!fieldRead && !propertyRead)
                {
                    compatibilityDiagnostic =
                        $"Package Manager field '{PageManagerFieldName}' and property " +
                        $"'{PageManagerPropertyName}' are unavailable.";
                    return false;
                }
            }

            if (pageManager == null)
            {
                compatibilityDiagnostic = "Package Manager page manager is null.";
                return false;
            }

            return TryGetGitHubPageState(
                pageManager,
                out isGitHubPage,
                out compatibilityDiagnostic);
        }

        internal static bool TryGetGitHubPageStateFromWindow(
            object packageManagerWindow,
            out bool isGitHubPage,
            out string compatibilityDiagnostic)
        {
            isGitHubPage = false;
            compatibilityDiagnostic = string.Empty;
            if (packageManagerWindow == null)
            {
                compatibilityDiagnostic = "Package Manager window is null.";
                return false;
            }

            if (!string.Equals(
                    packageManagerWindow.GetType().FullName,
                    PackageManagerWindowTypeName,
                    StringComparison.Ordinal))
            {
                compatibilityDiagnostic =
                    "Object is not the expected Package Manager window type.";
                return false;
            }

            if (!TryGetFieldValue(
                    packageManagerWindow,
                    PackageManagerRootFieldName,
                    out object root))
            {
                compatibilityDiagnostic =
                    $"Package Manager field '{PackageManagerRootFieldName}' is unavailable.";
                return false;
            }

            if (root == null)
            {
                compatibilityDiagnostic = "Package Manager root is null.";
                return false;
            }

            return TryGetGitHubPageStateFromRoot(
                root,
                out isGitHubPage,
                out compatibilityDiagnostic);
        }

        internal static bool TryGetGitHubPageStateForTag(
            object tagElement,
            out bool isGitHubPage)
        {
            isGitHubPage = false;
            if (!(tagElement is VisualElement element))
                return false;

            try
            {
                Type windowType = FindLoadedType(PackageManagerWindowTypeName);
                if (windowType == null)
                    return false;

                foreach (EditorWindow window in Resources.FindObjectsOfTypeAll<EditorWindow>())
                {
                    if (window == null ||
                        window.GetType() != windowType ||
                        !IsDescendantOrSelf(window.rootVisualElement, element))
                    {
                        continue;
                    }

                    return TryGetGitHubPageStateFromWindow(
                        window,
                        out isGitHubPage,
                        out _);
                }
            }
            catch
            {
                // A detached label or a changed window contract is unresolved,
                // never evidence that it belongs to the GitHub extension page.
            }

            return false;
        }

        private static bool IsDescendantOrSelf(
            VisualElement root,
            VisualElement element)
        {
            if (root == null || element == null)
                return false;

            for (VisualElement current = element;
                 current != null;
                 current = current.parent)
            {
                if (ReferenceEquals(current, root))
                    return true;
            }

            return false;
        }

        internal static void CancelDeferredTagPresentation(object tagElement)
        {
            if (!(tagElement is VisualElement element) ||
                !DeferredTags.TryGetValue(element, out DeferredTagState state))
            {
                return;
            }

            state.PackageVersion = null;
            if (state.IsRegistered)
            {
                element.UnregisterCallback<AttachToPanelEvent>(
                    OnDeferredTagAttached);
                state.IsRegistered = false;
            }

            DeferredTags.Remove(element);
        }

        internal static bool HasDeferredTagPresentation(
            object tagElement,
            object packageVersion)
        {
            return tagElement is VisualElement element &&
                   DeferredTags.TryGetValue(element, out DeferredTagState state) &&
                   state.IsRegistered &&
                   ReferenceEquals(state.PackageVersion, packageVersion);
        }

        internal static void DeferTagPresentationUntilAttached(
            object tagElement,
            object packageVersion)
        {
            if (!(tagElement is VisualElement element) ||
                element.panel != null ||
                packageVersion == null)
            {
                return;
            }

            DeferredTagState state = DeferredTags.GetValue(
                element,
                _ => new DeferredTagState());
            state.PackageVersion = packageVersion;
            if (state.IsRegistered)
                return;

            state.IsRegistered = true;
            element.RegisterCallback<AttachToPanelEvent>(OnDeferredTagAttached);
        }

        private static void OnDeferredTagAttached(AttachToPanelEvent attachEvent)
        {
            ApplyDeferredTagPresentationOnAttach(attachEvent.currentTarget);
        }

        internal static void ApplyDeferredTagPresentationOnAttach(object tagElement)
        {
            if (!(tagElement is VisualElement element))
                return;

            element.UnregisterCallback<AttachToPanelEvent>(OnDeferredTagAttached);
            if (!DeferredTags.TryGetValue(element, out DeferredTagState state))
                return;

            state.IsRegistered = false;
            object packageVersion = state.PackageVersion;
            state.PackageVersion = null;
            if (packageVersion != null)
                ApplyTagPresentation(element, packageVersion);
        }

        private static void SourceRefreshPostfix(object __instance, object __0)
        {
            try
            {
                PackageManagerSubmodulePresentation.ResetCustomSourceIcon(__instance);
                if (PackageManagerSubmodulePresentation.TryGetPresentation(
                        __0,
                        out PackageManagerSubmoduleInfo info))
                {
                    PackageManagerSubmodulePresentation.ApplySourceCard(
                        __instance,
                        info,
                        info.IsGitHub ? GitSubmoduleManagerIcons.GitIcon : null);
                    return;
                }

                if (PackageManagerGitHubPackageProjection.TryGetRepository(
                        __0,
                        out PackageManagerGitHubRepository repository))
                {
                    var discoveredInfo = new PackageManagerSubmoduleInfo(
                        repository.PackageName,
                        string.Empty,
                        string.Empty,
                        repository.Url,
                        true);
                    PackageManagerSubmodulePresentation.ApplySourceCard(
                        __instance,
                        discoveredInfo,
                        GitSubmoduleManagerIcons.GitIcon);
                }
            }
            catch
            {
                // Fail open so the built-in Source card keeps rendering.
            }
        }

        private static void PackageToolbarRefreshPostfix(
            object __instance,
            object __0)
        {
            try
            {
                PackageManagerGitHubNativeActions.RefreshForToolbar(
                    __instance,
                    __0);
            }
            catch
            {
                // Fail open so Unity's built-in toolbar remains authoritative.
            }
        }

        private static bool RemoveCustomActionPrefix(object __0, ref bool __result)
        {
            try
            {
                if (!PackageManagerGitHubNativeActions.TryHandleRemoveCustomAction(
                        __0,
                        out bool actionResult))
                {
                    return true;
                }

                __result = actionResult;
                return false;
            }
            catch
            {
                // A changed or unrelated Package Manager contract stays native.
                // Known submodules fail closed so Unity cannot delete their
                // checkout without updating the parent repository metadata.
                try
                {
                    if (PackageManagerSubmodulePresentation.TryGetPresentation(
                            __0,
                            out _))
                    {
                        __result = false;
                        return false;
                    }
                }
                catch
                {
                    // Fall through only when this is not recognizably a submodule.
                }

                return true;
            }
        }

        private static bool PackageOperationDispatcherRemoveEmbeddedPrefix(
            object __0)
        {
            try
            {
                object versions = GetPropertyValue(__0, "versions");
                object installedVersion = GetPropertyValue(versions, "installed");
                if (installedVersion == null)
                    return true;

                string packageName = GetPropertyValue(__0, "name") as string;
                return !PackageManagerGitHubNativeActions
                    .ShouldBlockNativeEmbeddedRemoval(packageName);
            }
            catch
            {
                // The interactive RemoveCustomAction prefix handles loading and
                // diagnostics. This lower-level guard blocks only a proven
                // snapshot match and otherwise preserves Unity's native behavior.
                return true;
            }
        }

        private static void OnSnapshotChanged()
        {
            RefreshOpenPackageManagerWindows();
        }

        private static void TryPatchAndRefresh()
        {
            TryPatch();
            RefreshOpenPackageManagerWindows();
        }

        private static void OnBeforeAssemblyReload()
        {
            shuttingDown = true;
            PackageManagerSubmoduleSnapshot.SnapshotChanged -= OnSnapshotChanged;
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
