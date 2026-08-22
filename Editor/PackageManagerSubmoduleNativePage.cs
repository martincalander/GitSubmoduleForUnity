using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace MartinCalander.GitSubmoduleManager.Editor
{
    /// <summary>
    /// Bridges installed GitHub submodules into Unity's internal extension-page
    /// contract. The bridge is entirely optional: Editors without this contract
    /// continue to use the embedded compatibility host.
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
        internal const string SidebarTypeName =
            "UnityEditor.PackageManager.UI.Internal.Sidebar";
        internal const string SidebarExtensionRowsUpdateMethodName =
            "UpdateExtensionPageRelatedRows";

        private const string SidebarRowTypeName =
            "UnityEditor.PackageManager.UI.Internal.SidebarRow";
        private const string LegacySidebarPageId =
            "GitSubmoduleManager.GitHub";
        private const string MyAssetsPageId = "MyAssets";
        private const string AddExtensionPageMethodName = "AddExtensionPage";
        private const string GetPageMethodName = "GetPage";

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
                    return true;

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
                return page != null;
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
                SetEmptyEnumArray(args, "supportedStatusFilters");
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
            return PackageManagerSubmodulePresentation.TryGetPresentation(
                       primaryVersion,
                       out PackageManagerSubmoduleInfo info) &&
                   info.IsGitHub;
        }

        internal static string GetGroupName(object package)
        {
            if (PackageManagerGitHubPackageProjection.TryGetRepository(
                    package,
                    out PackageManagerGitHubRepository repository))
            {
                return string.IsNullOrWhiteSpace(repository.Owner)
                    ? L10n.Tr("Organization")
                    : string.Format(
                        L10n.Tr("Organization - {0}"),
                        repository.Owner.Trim());
            }

            object primaryVersion = GetPrimaryVersion(package);
            object author = GetPropertyValue(primaryVersion, "author");
            string authorName = GetPropertyValue(author, "name") as string;
            return string.IsNullOrWhiteSpace(authorName)
                ? L10n.Tr("Organization")
                : string.Format(
                    L10n.Tr("Organization - {0}"),
                    authorName.Trim());
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

            return true;
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

        private static void SetEmptyEnumArray(object instance, string fieldName)
        {
            SetEnumArray(instance, fieldName, Array.Empty<string>());
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
