using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEngine.UIElements;

namespace MartinCalander.GitSubmoduleManager.Editor
{
    /// <summary>
    /// Extends Package Manager's own add menu. Unity owns the toolbar button,
    /// menu rebuild, ordering, keyboard handling, and styling; this class only
    /// registers one reflected extension item and removes it with its host root.
    /// </summary>
    internal static class PackageManagerGitSubmoduleInstallMenu
    {
        internal const string PackageManagerWindowRootTypeName =
            "UnityEditor.PackageManager.UI.Internal.PackageManagerWindowRoot";
        internal const string MenuText =
            "Install package as Git Submodule...";

        private const int MenuPriority = 100;
        private const BindingFlags AnyInstance =
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        private static readonly Dictionary<object, MenuEntry> EntriesByRoot =
            new(ReferenceComparer.Instance);

        internal static int InstalledRootCount => EntriesByRoot.Count;

        internal static bool InstallForRoot(object packageManagerRoot)
        {
            if (!PackageManagerUnityVersionSupport.IsCurrentVersionSupported ||
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
                PropertyInfo addMenuProperty = FindAddMenuProperty(root.GetType());
                object menu = addMenuProperty?.GetValue(root, null);
                if (!(menu is VisualElement))
                    return false;

                if (EntriesByRoot.TryGetValue(root, out MenuEntry existing))
                {
                    if (ReferenceEquals(existing.Menu, menu))
                        return true;

                    ReleaseForRoot(root);
                }

                MethodInfo addDropdownItem = FindAddDropdownItemMethod(menu.GetType());
                object item = addDropdownItem?.Invoke(menu, null);
                if (item == null)
                    return false;

                Action callback = () => ShowInstallPopup(root);
                if (!TryConfigureItem(item, callback, out ItemProperties properties))
                {
                    HideAndRemove(menu, item, properties);
                    return false;
                }

                EntriesByRoot[root] = new MenuEntry(
                    menu,
                    item,
                    properties);
                return true;
            }
            catch
            {
                return false;
            }
        }

        internal static void ReleaseForRoot(object packageManagerRoot)
        {
            if (packageManagerRoot == null ||
                !EntriesByRoot.TryGetValue(packageManagerRoot, out MenuEntry entry))
            {
                return;
            }

            EntriesByRoot.Remove(packageManagerRoot);
            HideAndRemove(entry.Menu, entry.Item, entry.Properties);
        }

        internal static bool IsSupportedContract()
        {
            if (!PackageManagerUnityVersionSupport.IsCurrentVersionSupported)
                return false;

            Type rootType = PackageManagerSubmoduleHarmonyPatch.FindLoadedType(
                PackageManagerWindowRootTypeName);
            PropertyInfo addMenu = FindAddMenuProperty(rootType);
            Type menuType = addMenu?.PropertyType;
            return addMenu != null &&
                   FindAddDropdownItemMethod(menuType) != null;
        }

        internal static PropertyInfo FindAddMenuProperty(Type rootType)
        {
            PropertyInfo property = rootType?.GetProperty("addMenu", AnyInstance);
            return property != null &&
                   property.CanRead &&
                   property.GetIndexParameters().Length == 0
                ? property
                : null;
        }

        internal static MethodInfo FindAddDropdownItemMethod(Type menuType)
        {
            MethodInfo method = menuType?.GetMethod(
                "AddDropdownItem",
                AnyInstance,
                null,
                Type.EmptyTypes,
                null);
            return method != null && method.ReturnType != typeof(void)
                ? method
                : null;
        }

        internal static MethodInfo FindRemoveMethod(Type menuType, Type itemType)
        {
            if (menuType == null || itemType == null)
                return null;

            foreach (MethodInfo method in menuType.GetMethods(AnyInstance))
            {
                ParameterInfo[] parameters = method.GetParameters();
                if (method.Name == "Remove" &&
                    method.ReturnType == typeof(void) &&
                    parameters.Length == 1 &&
                    parameters[0].ParameterType.IsAssignableFrom(itemType))
                {
                    return method;
                }
            }

            return null;
        }

        internal static bool TryConfigureItem(
            object item,
            Action callback,
            out ItemProperties properties)
        {
            properties = ItemProperties.Resolve(item?.GetType());
            if (item == null || callback == null || !properties.IsComplete)
                return false;

            try
            {
                properties.Visible.SetValue(item, false, null);
                properties.Enabled.SetValue(item, false, null);
                properties.Text.SetValue(item, L10n.Tr(MenuText), null);
                properties.Priority.SetValue(item, MenuPriority, null);
                properties.InsertSeparatorBefore.SetValue(item, true, null);
                properties.IsChecked?.SetValue(item, false, null);

                // Assign once. Unity's extension item owns the native menu
                // callback and rebuilds the dropdown from this Action.
                properties.Action.SetValue(item, callback, null);
                properties.Enabled.SetValue(item, true, null);
                properties.Visible.SetValue(item, true, null);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static void ShowInstallPopup(object packageManagerRoot)
        {
            if (!EntriesByRoot.TryGetValue(
                    packageManagerRoot,
                    out MenuEntry entry))
            {
                return;
            }

            try
            {
                GitSubmoduleInstallPopup.Show(entry.Menu as VisualElement);
            }
            catch
            {
                // Package Manager may be rebuilding while its menu callback is
                // dispatched. The optional extension must fail closed.
            }
        }

        private static void HideAndRemove(
            object menu,
            object item,
            ItemProperties properties)
        {
            if (item == null)
                return;

            try
            {
                properties.Visible?.SetValue(item, false, null);
                properties.Enabled?.SetValue(item, false, null);
                properties.Action?.SetValue(item, null, null);
            }
            catch
            {
                // Continue to the native removal contract when possible.
            }

            try
            {
                FindRemoveMethod(menu?.GetType(), item.GetType())?.Invoke(
                    menu,
                    new[] { item });
            }
            catch
            {
                // Package Manager may already be tearing down. Hidden and
                // disabled state above remains the safe fallback.
            }
        }

        internal readonly struct ItemProperties
        {
            internal ItemProperties(
                PropertyInfo action,
                PropertyInfo text,
                PropertyInfo priority,
                PropertyInfo visible,
                PropertyInfo enabled,
                PropertyInfo insertSeparatorBefore,
                PropertyInfo isChecked)
            {
                Action = action;
                Text = text;
                Priority = priority;
                Visible = visible;
                Enabled = enabled;
                InsertSeparatorBefore = insertSeparatorBefore;
                IsChecked = isChecked;
            }

            internal PropertyInfo Action { get; }
            internal PropertyInfo Text { get; }
            internal PropertyInfo Priority { get; }
            internal PropertyInfo Visible { get; }
            internal PropertyInfo Enabled { get; }
            internal PropertyInfo InsertSeparatorBefore { get; }
            internal PropertyInfo IsChecked { get; }

            internal bool IsComplete =>
                IsWritable(Action, typeof(Action)) &&
                IsWritable(Text, typeof(string)) &&
                IsWritable(Priority, typeof(int)) &&
                IsWritable(Visible, typeof(bool)) &&
                IsWritable(Enabled, typeof(bool)) &&
                IsWritable(InsertSeparatorBefore, typeof(bool)) &&
                (IsChecked == null || IsWritable(IsChecked, typeof(bool)));

            internal static ItemProperties Resolve(Type itemType)
            {
                return new ItemProperties(
                    itemType?.GetProperty("action", AnyInstance),
                    itemType?.GetProperty("text", AnyInstance),
                    itemType?.GetProperty("priority", AnyInstance),
                    itemType?.GetProperty("visible", AnyInstance),
                    itemType?.GetProperty("enabled", AnyInstance),
                    itemType?.GetProperty("insertSeparatorBefore", AnyInstance),
                    itemType?.GetProperty("isChecked", AnyInstance));
            }

            private static bool IsWritable(PropertyInfo property, Type valueType)
            {
                return property != null &&
                       property.CanWrite &&
                       property.PropertyType == valueType;
            }
        }

        private sealed class MenuEntry
        {
            internal MenuEntry(
                object menu,
                object item,
                ItemProperties properties)
            {
                Menu = menu;
                Item = item;
                Properties = properties;
            }

            internal object Menu { get; }
            internal object Item { get; }
            internal ItemProperties Properties { get; }
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
                    : System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(
                        instance);
            }
        }
    }
}
