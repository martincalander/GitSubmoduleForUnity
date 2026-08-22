using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEditor;
using UnityEngine;

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
        internal const string PackageInterfaceTypeName =
            "UnityEditor.PackageManager.UI.Internal.IPackage";
        internal const string PackageManagerWindowTypeName =
            "UnityEditor.PackageManager.UI.PackageManagerWindow";
        internal const string RefreshMethodName = "Refresh";
        internal const string LegacyCreateTagLabelMethodName = "CreateTagLabel";

        private const BindingFlags AnyInstance =
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private const BindingFlags AnyStatic =
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        private static Harmony harmony;
        private static volatile bool patchRequested = true;
        private static bool shuttingDown;
        private static double nextPatchAttempt;
        private static string lastPatchError = string.Empty;

        static PackageManagerSubmoduleHarmonyPatch()
        {
            PackageManagerSubmoduleSnapshot.SnapshotChanged += OnSnapshotChanged;
            AppDomain.CurrentDomain.AssemblyLoad += OnAssemblyLoad;
            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;
            EditorApplication.update += RetryPatchOnUpdate;
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
                object root = GetFieldValue(window, "m_Root");
                object pageManager = GetFieldValue(root, "m_PageManager");
                if (pageManager == null)
                    return false;

                object activePage = GetPropertyValue(pageManager, "activePage") ??
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

        private static void TagRefreshPostfix(object __instance, object __0)
        {
            ApplyTagPresentation(__instance, __0);
        }

        private static void TagFactoryPostfix(object __0, object __result)
        {
            ApplyTagPresentation(__result, __0);
        }

        private static void ApplyTagPresentation(object tagElement, object packageVersion)
        {
            try
            {
                PackageManagerSubmodulePresentation.ResetRepositoryVisibilityTag(
                    tagElement);
                PackageManagerSubmodulePresentation.ResetCustomTagLabel(tagElement);
                if (PackageManagerGitHubPackageProjection.TryGetRepository(
                        packageVersion,
                        out PackageManagerGitHubRepository repository))
                {
                    PackageManagerSubmodulePresentation
                        .ApplyRepositoryVisibilityTag(
                            tagElement,
                            repository.IsPrivate);
                    return;
                }

                if (PackageManagerSubmodulePresentation.TryGetPresentation(
                        packageVersion,
                        out PackageManagerSubmoduleInfo info))
                {
                    PackageManagerSubmodulePresentation.ApplyTagLabel(tagElement, info);
                }
            }
            catch
            {
                // Unity's Package Manager remains authoritative when its internals change.
            }
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

        private static void OnSnapshotChanged()
        {
            RefreshOpenPackageManagerWindows();
        }

        private static void OnAssemblyLoad(object _, AssemblyLoadEventArgs __)
        {
            patchRequested = true;
        }

        private static void RetryPatchOnUpdate()
        {
            if (shuttingDown ||
                !patchRequested ||
                EditorApplication.timeSinceStartup < nextPatchAttempt)
            {
                return;
            }

            nextPatchAttempt = EditorApplication.timeSinceStartup + 1d;
            patchRequested = !TryPatch();
        }

        private static void TryPatchAndRefresh()
        {
            patchRequested = !TryPatch();
            PackageManagerSubmoduleSnapshot.Refresh();
            RefreshOpenPackageManagerWindows();
        }

        private static void OnBeforeAssemblyReload()
        {
            shuttingDown = true;
            EditorApplication.update -= RetryPatchOnUpdate;
            PackageManagerSubmoduleSnapshot.SnapshotChanged -= OnSnapshotChanged;
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
