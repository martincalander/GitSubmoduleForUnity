using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEngine.UIElements;

namespace MartinCalander.GitSubmoduleManager.Editor
{
    internal enum PackageManagerManagedPackageKind
    {
        None,
        Submodule,
        ReadOnlyGit
    }

    /// <summary>
    /// Extends Unity's existing Package Manager Manage dropdown for verified
    /// submodules and eligible direct read-only Git dependencies. The native menu
    /// is rebuilt by Unity on every toolbar refresh; submodules replace Unity's
    /// embedded Remove entry with a guarded uninstall, while read-only packages
    /// retain Unity's native actions and receive only the conversion command.
    /// </summary>
    internal static class PackageManagerSubmoduleManageMenu
    {
        internal const string ManageDropdownTypeName =
            "UnityEditor.PackageManager.UI.Internal.ManageDropdownButton";
        internal const string ManageDropdownElementName = "manageDropdown";
        internal const string RemoveActionTypeName =
            "UnityEditor.PackageManager.UI.Internal.RemoveAction";
        internal const string RemoveCustomActionTypeName =
            "UnityEditor.PackageManager.UI.Internal.RemoveCustomAction";
        internal const string VersionFieldName = "m_Version";
        internal const string ActionsFieldName = "m_Actions";
        internal const string MenuPropertyName = "menu";
        internal const string MenuItemsFieldName = "m_Items";
        internal const string MenuItemNameFieldName = "name";
        internal const string MenuItemElementFieldName = "element";
        internal const string MenuItemActionFieldName = "action";
        internal const string MenuItemUserDataActionFieldName = "actionUserData";
        internal const string ConvertToReadOnlyText =
            "Convert to Read-Only Package";
        internal const string ConvertToSubmoduleText = "Convert to Submodule";
        // Compatibility alias for existing callers and localized tests.
        internal const string ConvertText = ConvertToReadOnlyText;
        internal const string UninstallText = "Uninstall Submodule";

        private const BindingFlags AnyInstance =
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        internal static bool IsSupportedContract()
        {
            if (!PackageManagerUnityVersionSupport.IsCurrentVersionSupported)
                return false;

            Type type = PackageManagerSubmoduleHarmonyPatch.FindLoadedType(
                ManageDropdownTypeName);
            if (type == null || !typeof(VisualElement).IsAssignableFrom(type))
                return false;

            FieldInfo versionField = FindField(type, VersionFieldName);
            FieldInfo actionsField = FindField(type, ActionsFieldName);
            PropertyInfo menuProperty = FindProperty(type, MenuPropertyName);
            FieldInfo itemsField = typeof(GenericDropdownMenu).GetField(
                MenuItemsFieldName,
                AnyInstance);
            Type itemType = itemsField?.FieldType.IsGenericType == true
                ? itemsField.FieldType.GetGenericArguments()[0]
                : null;
            Type removeActionType =
                PackageManagerSubmoduleHarmonyPatch.FindLoadedType(
                    RemoveCustomActionTypeName) ??
                PackageManagerSubmoduleHarmonyPatch.FindLoadedType(
                    RemoveActionTypeName);
            return versionField != null &&
                   actionsField != null &&
                   menuProperty?.PropertyType == typeof(GenericDropdownMenu) &&
                   itemsField != null &&
                   itemType?.GetField(MenuItemNameFieldName, AnyInstance) != null &&
                   itemType.GetField(MenuItemElementFieldName, AnyInstance) != null &&
                   itemType.GetField(MenuItemActionFieldName, AnyInstance) != null &&
                   itemType.GetField(
                       MenuItemUserDataActionFieldName,
                       AnyInstance) != null &&
                   FindRefreshMethod(type, versionField.FieldType) != null &&
                   removeActionType != null &&
                   FindCompatibleMethod(
                       removeActionType,
                       "IsVisible",
                       versionField.FieldType,
                       typeof(bool)) != null &&
                   FindCompatibleMethod(
                       removeActionType,
                       "GetText",
                       versionField.FieldType,
                       typeof(string),
                       typeof(bool)) != null;
        }

        internal static bool Apply(
            VisualElement toolbar,
            PackageManagerManagedPackageKind packageKind,
            bool conversionEnabled,
            string conversionTooltip,
            Action conversionRequested,
            bool uninstallEnabled,
            string uninstallTooltip,
            Action uninstallRequested)
        {
            VisualElement manageDropdown = toolbar?.Q<VisualElement>(
                ManageDropdownElementName);
            if (manageDropdown == null ||
                !string.Equals(
                    manageDropdown.GetType().FullName,
                    ManageDropdownTypeName,
                    StringComparison.Ordinal))
            {
                return false;
            }

            if (packageKind == PackageManagerManagedPackageKind.None)
            {
                GenericDropdownMenu currentMenu = GetPropertyValue(
                    manageDropdown,
                    MenuPropertyName) as GenericDropdownMenu;
                if (currentMenu == null)
                    return false;
                return RestoreNativeMenuIfOwnedItemsRemain(
                    manageDropdown,
                    currentMenu);
            }

            if (conversionRequested == null ||
                packageKind == PackageManagerManagedPackageKind.Submodule &&
                uninstallRequested == null)
                return false;

            // Rebuild first so every custom refresh starts from Unity's current
            // native action state. This avoids stale selection callbacks and also
            // preserves Unity's own disabled policy for the Remove action.
            if (!RefreshNativeMenu(manageDropdown))
                return false;

            GenericDropdownMenu menu = GetPropertyValue(
                manageDropdown,
                MenuPropertyName) as GenericDropdownMenu;
            if (menu == null)
                return false;

            if (packageKind == PackageManagerManagedPackageKind.ReadOnlyGit)
            {
                return ApplyReadOnlyToMenu(
                    menu,
                    conversionEnabled,
                    conversionTooltip,
                    conversionRequested);
            }

            HashSet<string> removalTexts = GetVisibleRemovalTexts(manageDropdown);
            return ApplyToMenu(
                menu,
                removalTexts,
                conversionEnabled,
                conversionTooltip,
                conversionRequested,
                uninstallEnabled,
                uninstallTooltip,
                uninstallRequested);
        }

        internal static bool ApplyReadOnlyToMenu(
            GenericDropdownMenu menu,
            bool conversionEnabled,
            string conversionTooltip,
            Action conversionRequested)
        {
            if (menu == null || conversionRequested == null ||
                !TryRemoveItems(menu, IsConversionItem) ||
                !TryRemoveItems(menu, IsUninstallItem))
            {
                return false;
            }

            // A read-only Git package remains a normal UPM dependency. Preserve
            // Unity's own Remove/Update actions and add only the conversion.
            AddItem(
                menu,
                L10n.Tr(ConvertToSubmoduleText),
                conversionEnabled,
                conversionTooltip,
                conversionRequested);
            return true;
        }

        internal static bool ApplyToMenu(
            GenericDropdownMenu menu,
            ISet<string> nativeRemovalTexts,
            bool conversionEnabled,
            string conversionTooltip,
            Action conversionRequested,
            bool uninstallEnabled,
            string uninstallTooltip,
            Action uninstallRequested)
        {
            if (menu == null ||
                conversionRequested == null ||
                uninstallRequested == null)
            {
                return false;
            }

            if (!TryRemoveItems(
                    menu,
                    IsConversionItem))
            {
                return false;
            }

            if (!TryRemoveItems(menu, IsUninstallItem))
            {
                return false;
            }

            if (!TryRemoveNativeRemovalItems(
                menu,
                nativeRemovalTexts,
                out bool nativeRemovalFound,
                out bool nativeRemovalEnabled,
                out string nativeRemovalTooltip))
            {
                return false;
            }

            bool effectiveUninstallEnabled = uninstallEnabled &&
                                             (!nativeRemovalFound ||
                                              nativeRemovalEnabled);
            string effectiveUninstallTooltip = uninstallEnabled &&
                                               nativeRemovalFound &&
                                               !nativeRemovalEnabled
                ? string.IsNullOrWhiteSpace(nativeRemovalTooltip)
                    ? L10n.Tr(
                        "Unity Package Manager currently disables this action.")
                    : nativeRemovalTooltip
                : uninstallTooltip;
            AddItem(
                menu,
                L10n.Tr(UninstallText),
                effectiveUninstallEnabled,
                effectiveUninstallTooltip,
                uninstallRequested);
            AddItem(
                menu,
                L10n.Tr(ConvertToReadOnlyText),
                conversionEnabled,
                conversionTooltip,
                conversionRequested);
            return true;
        }

        internal static bool ContainsOwnedItems(GenericDropdownMenu menu)
        {
            if (!TryGetMenuItems(menu, out IList items))
                return false;

            foreach (object item in items)
            {
                if (IsOwnedItem(GetMenuItemName(item)))
                    return true;
            }

            return false;
        }

        internal static IReadOnlyList<string> GetItemNamesForTests(
            GenericDropdownMenu menu)
        {
            var names = new List<string>();
            if (!TryGetMenuItems(menu, out IList items))
                return names;

            foreach (object item in items)
                names.Add(GetMenuItemName(item));
            return names;
        }

        internal static bool InvokeItemForTests(
            GenericDropdownMenu menu,
            string itemName)
        {
            if (!TryGetMenuItems(menu, out IList items))
                return false;

            foreach (object item in items)
            {
                if (!string.Equals(
                        GetMenuItemName(item),
                        itemName,
                        StringComparison.Ordinal))
                {
                    continue;
                }

                FieldInfo actionField = item.GetType().GetField(
                    "action",
                    AnyInstance);
                if (actionField?.GetValue(item) is Action action)
                {
                    action();
                    return true;
                }

                return false;
            }

            return false;
        }

        internal static bool IsItemEnabledForTests(
            GenericDropdownMenu menu,
            string itemName)
        {
            if (!TryGetMenuItems(menu, out IList items))
                return false;

            foreach (object item in items)
            {
                if (!string.Equals(
                        GetMenuItemName(item),
                        itemName,
                        StringComparison.Ordinal))
                {
                    continue;
                }

                FieldInfo elementField = item.GetType().GetField(
                    MenuItemElementFieldName,
                    AnyInstance);
                return elementField?.GetValue(item) is VisualElement element &&
                       element.enabledSelf;
            }

            return false;
        }

        private static bool RestoreNativeMenuIfOwnedItemsRemain(
            object manageDropdown,
            GenericDropdownMenu menu)
        {
            if (!ContainsOwnedItems(menu))
                return true;

            return RefreshNativeMenu(manageDropdown);
        }

        private static bool RefreshNativeMenu(object manageDropdown)
        {
            object version = GetFieldValue(manageDropdown, VersionFieldName);
            if (manageDropdown == null || version == null)
                return false;

            MethodInfo refresh = FindRefreshMethod(
                manageDropdown.GetType(),
                version.GetType());
            if (refresh == null)
                return false;

            try
            {
                refresh.Invoke(manageDropdown, new[] { version });
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static HashSet<string> GetVisibleRemovalTexts(
            object manageDropdown)
        {
            var result = new HashSet<string>(StringComparer.Ordinal);
            bool sawRemovalAction = false;
            object version = GetFieldValue(manageDropdown, VersionFieldName);
            if (version == null ||
                !(GetFieldValue(manageDropdown, ActionsFieldName) is
                    IEnumerable actions))
            {
                return result;
            }

            foreach (object action in actions)
            {
                string typeName = action?.GetType().FullName;
                if (!string.Equals(
                        typeName,
                        RemoveActionTypeName,
                        StringComparison.Ordinal) &&
                    !string.Equals(
                        typeName,
                        RemoveCustomActionTypeName,
                        StringComparison.Ordinal))
                {
                    continue;
                }

                sawRemovalAction = true;

                if (!TryGetActionText(action, version, out string text))
                    continue;
                if (!string.IsNullOrWhiteSpace(text))
                    result.Add(text);
            }

            // English is Unity's fallback locale and provides a fail-safe when a
            // package action is recycled before its version field is available.
            if (sawRemovalAction && result.Count == 0)
                result.Add("Remove");
            return result;
        }

        private static bool TryGetActionText(
            object action,
            object version,
            out string text)
        {
            text = string.Empty;
            if (action == null || version == null)
                return false;

            try
            {
                MethodInfo visibleMethod = FindCompatibleMethod(
                    action.GetType(),
                    "IsVisible",
                    version.GetType(),
                    typeof(bool));
                if (visibleMethod != null &&
                    !(bool)visibleMethod.Invoke(action, new[] { version }))
                {
                    return false;
                }

                MethodInfo textMethod = FindCompatibleMethod(
                    action.GetType(),
                    "GetText",
                    version.GetType(),
                    typeof(string),
                    typeof(bool));
                if (textMethod == null)
                    return false;

                text = textMethod.Invoke(
                    action,
                    new object[] { version, false }) as string ?? string.Empty;
                return true;
            }
            catch
            {
                text = string.Empty;
                return false;
            }
        }

        private static void AddItem(
            GenericDropdownMenu menu,
            string text,
            bool enabled,
            string tooltip,
            Action action)
        {
            string safeText = text ?? string.Empty;
            if (enabled)
            {
                menu.AddItem(
                    safeText,
                    false,
                    () =>
                    {
                        try
                        {
                            action();
                        }
                        catch (Exception exception)
                        {
                            UnityEngine.Debug.LogWarning(
                                "[Git Submodule Manager] Package Manager Manage " +
                                "action failed safely: " +
                                GitHubUtility.SanitizeUiDiagnostic(
                                    exception.Message));
                        }
                    });
            }
            else
            {
                menu.AddDisabledItem(safeText, false);
            }

            TrySetItemTooltip(menu, safeText, tooltip);
        }

        private static bool TryRemoveNativeRemovalItems(
            GenericDropdownMenu menu,
            ISet<string> nativeRemovalTexts,
            out bool found,
            out bool enabled,
            out string tooltip)
        {
            found = false;
            enabled = false;
            tooltip = string.Empty;
            if (!TryGetMenuItems(menu, out IList items))
                return false;

            string localizedRemove = L10n.Tr("Remove");
            try
            {
                for (int index = items.Count - 1; index >= 0; index--)
                {
                    object item = items[index];
                    string itemName = GetMenuItemName(item);
                    bool isRemoval = nativeRemovalTexts?.Contains(itemName) == true ||
                                     string.Equals(
                                         itemName,
                                         localizedRemove,
                                         StringComparison.Ordinal) ||
                                     string.Equals(
                                         itemName,
                                         "Remove",
                                         StringComparison.Ordinal);
                    if (!isRemoval)
                        continue;

                    found = true;
                    FieldInfo elementField = item?.GetType().GetField(
                        MenuItemElementFieldName,
                        AnyInstance);
                    if (elementField?.GetValue(item) is VisualElement element)
                    {
                        bool hasCallback = item.GetType().GetField(
                                               MenuItemActionFieldName,
                                               AnyInstance)?.GetValue(item) != null ||
                                           item.GetType().GetField(
                                               MenuItemUserDataActionFieldName,
                                               AnyInstance)?.GetValue(item) != null;
                        enabled |= element.enabledSelf && hasCallback;
                        if (string.IsNullOrWhiteSpace(tooltip) &&
                            !string.IsNullOrWhiteSpace(element.tooltip))
                        {
                            tooltip = element.tooltip.Trim();
                        }
                        element.RemoveFromHierarchy();
                    }

                    items.RemoveAt(index);
                }

                return true;
            }
            catch
            {
                found = false;
                enabled = false;
                tooltip = string.Empty;
                return false;
            }
        }

        private static void TrySetItemTooltip(
            GenericDropdownMenu menu,
            string itemName,
            string tooltip)
        {
            if (string.IsNullOrWhiteSpace(tooltip) ||
                !TryGetMenuItems(menu, out IList items))
            {
                return;
            }

            for (int index = items.Count - 1; index >= 0; index--)
            {
                object item = items[index];
                if (!string.Equals(
                        GetMenuItemName(item),
                        itemName,
                        StringComparison.Ordinal))
                {
                    continue;
                }

                FieldInfo elementField = item?.GetType().GetField(
                    MenuItemElementFieldName,
                    AnyInstance);
                if (elementField?.GetValue(item) is VisualElement element)
                    element.tooltip = tooltip.Trim();
                return;
            }
        }

        private static bool TryRemoveItems(
            GenericDropdownMenu menu,
            Predicate<string> shouldRemove)
        {
            if (shouldRemove == null ||
                !TryGetMenuItems(menu, out IList items))
            {
                return false;
            }

            try
            {
                for (int index = items.Count - 1; index >= 0; index--)
                {
                    object item = items[index];
                    if (!shouldRemove(GetMenuItemName(item)))
                        continue;

                    FieldInfo elementField = item?.GetType().GetField(
                        MenuItemElementFieldName,
                        AnyInstance);
                    if (elementField?.GetValue(item) is VisualElement element)
                        element.RemoveFromHierarchy();
                    items.RemoveAt(index);
                }

                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryGetMenuItems(
            GenericDropdownMenu menu,
            out IList items)
        {
            items = null;
            if (menu == null)
                return false;

            FieldInfo field = typeof(GenericDropdownMenu).GetField(
                MenuItemsFieldName,
                AnyInstance);
            items = field?.GetValue(menu) as IList;
            return items != null;
        }

        private static string GetMenuItemName(object item)
        {
            return item?.GetType().GetField(
                       MenuItemNameFieldName,
                       AnyInstance)?.GetValue(item) as string ?? string.Empty;
        }

        private static bool IsOwnedItem(string itemName)
        {
            return string.Equals(
                       itemName,
                       L10n.Tr(ConvertToReadOnlyText),
                       StringComparison.Ordinal) ||
                   string.Equals(
                       itemName,
                       L10n.Tr(ConvertToSubmoduleText),
                       StringComparison.Ordinal) ||
                   string.Equals(
                       itemName,
                       L10n.Tr(UninstallText),
                       StringComparison.Ordinal);
        }

        private static bool IsConversionItem(string itemName)
        {
            return string.Equals(
                       itemName,
                       L10n.Tr(ConvertToReadOnlyText),
                       StringComparison.Ordinal) ||
                   string.Equals(
                       itemName,
                       L10n.Tr(ConvertToSubmoduleText),
                       StringComparison.Ordinal);
        }

        private static bool IsUninstallItem(string itemName)
        {
            return string.Equals(
                itemName,
                L10n.Tr(UninstallText),
                StringComparison.Ordinal);
        }

        private static object GetFieldValue(object instance, string name)
        {
            return instance == null
                ? null
                : FindField(instance.GetType(), name)?.GetValue(instance);
        }

        private static object GetPropertyValue(object instance, string name)
        {
            return instance == null
                ? null
                : FindProperty(instance.GetType(), name)?.GetValue(instance, null);
        }

        private static FieldInfo FindField(Type type, string name)
        {
            for (Type current = type; current != null; current = current.BaseType)
            {
                FieldInfo field = current.GetField(
                    name,
                    AnyInstance | BindingFlags.DeclaredOnly);
                if (field != null)
                    return field;
            }

            return null;
        }

        private static PropertyInfo FindProperty(Type type, string name)
        {
            for (Type current = type; current != null; current = current.BaseType)
            {
                PropertyInfo property = current.GetProperty(
                    name,
                    AnyInstance | BindingFlags.DeclaredOnly);
                if (property != null && property.GetIndexParameters().Length == 0)
                    return property;
            }

            return null;
        }

        private static MethodInfo FindRefreshMethod(
            Type type,
            Type versionType)
        {
            foreach (MethodInfo method in type.GetMethods(AnyInstance))
            {
                ParameterInfo[] parameters = method.GetParameters();
                if (method.Name == "Refresh" &&
                    !method.IsStatic &&
                    method.ReturnType == typeof(void) &&
                    parameters.Length == 1 &&
                    parameters[0].ParameterType.IsAssignableFrom(versionType))
                {
                    return method;
                }
            }

            return null;
        }

        private static MethodInfo FindCompatibleMethod(
            Type type,
            string methodName,
            Type firstArgumentType,
            Type returnType,
            params Type[] remainingParameterTypes)
        {
            foreach (MethodInfo method in type.GetMethods(AnyInstance))
            {
                ParameterInfo[] parameters = method.GetParameters();
                if (method.Name != methodName ||
                    method.IsStatic ||
                    method.ReturnType != returnType ||
                    parameters.Length != 1 + remainingParameterTypes.Length ||
                    !parameters[0].ParameterType.IsAssignableFrom(
                        firstArgumentType))
                {
                    continue;
                }

                bool matches = true;
                for (int index = 0;
                     index < remainingParameterTypes.Length;
                     index++)
                {
                    if (parameters[index + 1].ParameterType !=
                        remainingParameterTypes[index])
                    {
                        matches = false;
                        break;
                    }
                }

                if (matches)
                    return method;
            }

            return null;
        }
    }
}
