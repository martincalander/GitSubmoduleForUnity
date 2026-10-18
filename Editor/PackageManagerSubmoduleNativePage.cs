using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.UIElements;

namespace MartinCalander.GitSubmoduleManager.Editor
{
    /// <summary>
    /// Exact runtime gate for the Package Manager reflection contract. UPM can
    /// express only the minimum package version, so every native integration
    /// entry point must independently reject Editors outside the validated,
    /// non-contiguous support set.
    /// </summary>
    internal static class PackageManagerUnityVersionSupport
    {
        internal static bool IsCurrentVersionSupported =>
            IsSupported(Application.unityVersion);

        internal static bool IsSupported(string unityVersion)
        {
            if (string.Equals(unityVersion, "6000.7.0b1", StringComparison.Ordinal) ||
                string.Equals(unityVersion, "6000.7.0b2", StringComparison.Ordinal) ||
                string.Equals(unityVersion, "7000.0.0a7", StringComparison.Ordinal) ||
                string.Equals(unityVersion, "7000.0.0a8", StringComparison.Ordinal) ||
                string.Equals(unityVersion, "7000.0.0a9", StringComparison.Ordinal) ||
                string.Equals(unityVersion, "7000.0.0a10", StringComparison.Ordinal))
                return true;

            if (string.IsNullOrEmpty(unityVersion))
                return false;

            int finalMarker = unityVersion.IndexOf('f');
            if (finalMarker <= 0 ||
                finalMarker != unityVersion.LastIndexOf('f') ||
                !TryParseDigits(
                    unityVersion,
                    finalMarker + 1,
                    unityVersion.Length - finalMarker - 1,
                    out int finalRevision) ||
                finalRevision != 1)
            {
                return false;
            }

            string[] versionParts = unityVersion
                .Substring(0, finalMarker)
                .Split('.');
            if (versionParts.Length != 3 ||
                !TryParseDigits(versionParts[0], out int major) ||
                !TryParseDigits(versionParts[1], out int minor) ||
                !TryParseDigits(versionParts[2], out int patch))
            {
                return false;
            }

            return major == 6000 &&
                   (minor == 3 && patch == 22 || minor == 5 ||
                    minor == 6 && (patch == 0 || patch == 2));
        }

        private static bool TryParseDigits(string value, out int parsed)
        {
            parsed = 0;
            return value != null &&
                   TryParseDigits(value, 0, value.Length, out parsed);
        }

        private static bool TryParseDigits(
            string value,
            int startIndex,
            int length,
            out int parsed)
        {
            parsed = 0;
            if (value == null || length <= 0 || startIndex < 0 ||
                startIndex > value.Length - length ||
                length > 1 && value[startIndex] == '0')
            {
                return false;
            }

            for (int index = startIndex; index < startIndex + length; index++)
            {
                char character = value[index];
                if (character < '0' || character > '9')
                    return false;

                try
                {
                    parsed = checked(parsed * 10 + character - '0');
                }
                catch (OverflowException)
                {
                    parsed = 0;
                    return false;
                }
            }

            return true;
        }
    }

    /// <summary>
    /// Adds an installed-package view to the native Package Manager sidebar.
    /// Unity owns the page, package models, selection, and ordinary package actions.
    /// </summary>
#if UNITY_6000_5_OR_NEWER
    [Unity.Scripting.LifecycleManagement.NoAutoStaticsCleanup]
#endif
    internal sealed class PackageManagerSubmoduleNativePage : IDisposable
    {
        internal const string ExtensionPageName = "git-submodule-manager";
        internal const string ExtensionPageId = "Extension/" + ExtensionPageName;
        internal const string ExtensionPageDisplayName = "GitHub";
        internal const string ExtensionPageArgsTypeName =
            "UnityEditor.PackageManager.UI.Internal.ExtensionPageArgs";
        internal const string PageManagerTypeName =
            "UnityEditor.PackageManager.UI.Internal.PageManager";
        internal const string SidebarTypeName =
            "UnityEditor.PackageManager.UI.Internal.Sidebar";
        internal const string SidebarRowTypeName =
            "UnityEditor.PackageManager.UI.Internal.SidebarRow";
        internal const string PageInterfaceTypeName =
            "UnityEditor.PackageManager.UI.Internal.IPage";
        internal const string NativeSidebarRowElementName =
            "git-submodule-manager-native-sidebar-row";

        private const BindingFlags Instance =
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private static readonly Dictionary<(Type, string), MemberInfo> Members = new();
        private static readonly Lazy<NativeContract> Contract = new(ResolveContract);
        private static readonly Lazy<MethodInfo> NativeRefresh = new(() =>
            GetVoidMethod(FindType("UnityEditor.PackageManager.UI.Internal.PageRefreshHandler"),
                "Refresh", FindType(PageInterfaceTypeName)));
        private static readonly Lazy<MethodInfo> SidebarRefresh = new(() =>
            GetVoidMethod(FindType(SidebarTypeName), "UpdateExtensionPageRelatedRows") ??
            GetVoidMethod(FindType(SidebarTypeName), "CreateRows"));
        private readonly VisualElement root;
        private object pageManager;
        private object page;
        private VisualElement sidebar;
        private VisualElement sources;
        private VisualElement row;
        private MethodInfo rebuild;
        private MethodInfo selectionChanged;
        private bool rebuildHasForceArgument;
        private bool disposed;

        private static event Action<VisualElement> SidebarRefreshed;
        internal event Action AttachmentInvalidated;

        private sealed class NativeContract
        {
            internal string Error;
            internal Type ArgsType;
            internal MethodInfo AddPage;
            internal MethodInfo GetRow;
            internal FieldInfo[] ArgumentFields;
            internal object[] ArgumentValues;
        }

        internal PackageManagerSubmoduleNativePage(VisualElement root)
        {
            this.root = root ?? throw new ArgumentNullException(nameof(root));
            SidebarRefreshed += OnSidebarRefreshed;
        }

        internal bool TryAttach(out string error)
        {
            error = string.Empty;
            if (disposed)
            {
                error = "This Package Manager view has closed.";
                return false;
            }
            if (!IsSupportedContract(out error))
                return false;
            if (!PackageManagerSubmoduleHarmonyPatch.IsSidebarRepairInstalled)
            {
                error = "Unity's Sources sidebar lifecycle hook is unavailable.";
                return false;
            }

            try
            {
                object currentManager = ReadMember(root, "m_PageManager") ??
                                        ReadMember(root, "pageManager");
                if (ReferenceEquals(pageManager, currentManager) &&
                    page != null && ReferenceEquals(page, FindRegisteredPage(currentManager)) &&
                    row != null && ReferenceEquals(row.parent, sources) &&
                    sidebar != null && sidebar.Contains(sources) && root.Contains(sidebar))
                    return true;

                StopTrackingRow();
                pageManager = currentManager;
                if (pageManager == null)
                    throw new InvalidOperationException("Package Manager is still loading.");

                page = FindRegisteredPage(pageManager);
                if (page == null)
                {
                    object args = CreateExtensionPageArgs();
                    Contract.Value.AddPage.Invoke(pageManager, new[] { args });
                    page = FindRegisteredPage(pageManager);
                }
                if (page == null)
                    throw new InvalidOperationException("The GitHub package source could not be registered.");

                sidebar = FindElement(root, SidebarTypeName);
                if (sidebar == null)
                    throw new InvalidOperationException("The Package Manager sidebar is still loading.");
                sources = (Contract.Value.GetRow.Invoke(sidebar,
                    new object[] { "MyAssets" }) as VisualElement)?.parent;
                if (sources == null)
                    throw new InvalidOperationException("The Package Manager Sources group is unavailable.");

                var rows = new List<VisualElement>();
                FindSidebarRows(sidebar, rows);
                if (rows.Count == 0)
                {
                    GetSidebarRefreshTarget()?.Invoke(sidebar, null);
                    FindSidebarRows(sidebar, rows);
                }
                if (rows.Count == 0)
                    throw new InvalidOperationException("The GitHub source row is still loading.");

                VisualElement attachedRow = rows.Find(candidate => ReferenceEquals(candidate.parent, sources)) ?? rows[0];
                foreach (VisualElement duplicate in rows)
                    if (!ReferenceEquals(duplicate, attachedRow))
                        duplicate.RemoveFromHierarchy();
                if (!ReferenceEquals(attachedRow.parent, sources))
                    sources.Add(attachedRow);
                attachedRow.name = NativeSidebarRowElementName;
                attachedRow.tooltip = "Installed packages hosted on GitHub.";
                VisualElement icon = attachedRow.Q("sidebarIcon") ?? attachedRow.Q(className: "sidebarIcon");
                if (icon != null && GitSubmoduleManagerIcons.GitIcon != null)
                    icon.style.backgroundImage = new StyleBackground(GitSubmoduleManagerIcons.GitIcon);
                rebuild = GetVoidMethod(page.GetType(), "Rebuild", typeof(bool));
                rebuildHasForceArgument = rebuild != null;
                rebuild ??= GetVoidMethod(page.GetType(), "RebuildVisualStatesAndUpdateVisibilityWithSearchText");
                selectionChanged = GetVoidMethod(page.GetType(), "TriggerOnSelectionChanged", typeof(bool));
                TrackRow(attachedRow);
                return true;
            }
            catch (Exception exception)
            {
                error = exception.GetBaseException().Message;
                return false;
            }
        }

        internal void Refresh()
        {
            if (disposed || page == null)
                return;
            try
            {
                if (!ReferenceEquals(page, FindRegisteredPage(pageManager)))
                    return;
                rebuild?.Invoke(page, rebuildHasForceArgument ? new object[] { true } : null);
                selectionChanged?.Invoke(page, new object[] { false });
                root.MarkDirtyRepaint();
            }
            catch
            {
                // A recycled native page must not interrupt other Package Manager views.
            }
        }

        public void Dispose()
        {
            disposed = true;
            SidebarRefreshed -= OnSidebarRefreshed;
            StopTrackingRow();
            AttachmentInvalidated = null;
            page = null;
            pageManager = null;
            sidebar = null;
            sources = null;
            rebuild = null;
            selectionChanged = null;
            // Unity owns registration. Another window can still use the same page.
        }

        internal static void NotifySidebarRefreshed(VisualElement refreshedSidebar)
        {
            if (refreshedSidebar != null)
                SidebarRefreshed?.Invoke(refreshedSidebar);
        }

        private void OnSidebarRefreshed(VisualElement refreshedSidebar)
        {
            if (disposed ||
                !ReferenceEquals(sidebar, refreshedSidebar) && !root.Contains(refreshedSidebar))
                return;
            // This also rearms attachment when native rows appear after an earlier retry timed out.
            // Unity can recreate its extension rows without removing our row from Sources.
            StopTrackingRow();
            AttachmentInvalidated?.Invoke();
        }

        private void TrackRow(VisualElement attachedRow)
        {
            StopTrackingRow();
            row = attachedRow;
            row.RegisterCallback<DetachFromPanelEvent>(OnRowDetached);
        }

        private void StopTrackingRow()
        {
            row?.UnregisterCallback<DetachFromPanelEvent>(OnRowDetached);
            row = null;
        }

        private void OnRowDetached(DetachFromPanelEvent evt)
        {
            if (disposed || !ReferenceEquals(evt.target, row))
                return;
            StopTrackingRow();
            AttachmentInvalidated?.Invoke();
        }

        internal static bool IsSupportedContract(out string error)
        {
            error = Contract.Value.Error ?? string.Empty;
            return string.IsNullOrEmpty(error);
        }

        private static NativeContract ResolveContract()
        {
            var contract = new NativeContract();
            if (!PackageManagerUnityVersionSupport.IsCurrentVersionSupported)
            {
                contract.Error = "This Unity version has not been validated for the GitHub package source.";
                return contract;
            }
            try
            {
                Type argsType = FindType(ExtensionPageArgsTypeName);
                Type managerType = FindType(PageManagerTypeName);
                Type sidebarType = FindType(SidebarTypeName);
                Type rowType = FindType(SidebarRowTypeName);
                if (argsType == null || managerType == null || sidebarType == null ||
                    rowType == null || !typeof(VisualElement).IsAssignableFrom(rowType) ||
                    GetAddPageMethod(managerType, argsType) == null ||
                    managerType.GetProperty("orderedExtensionPages", Instance) == null ||
                    GetRowMethod(sidebarType) == null || GetSidebarRefreshTarget() == null)
                {
                    contract.Error = "Unity's native extension-page or Sources sidebar contract has changed.";
                    return contract;
                }
                object args = Activator.CreateInstance(argsType, true);
                SetField(args, "name", ExtensionPageName);
                SetField(args, "displayName", ExtensionPageDisplayName);
                SetField(args, "priority", 100);
                SetEnum(args, "icon", "None");
                SetEnum(args, "capability", "None");
                SetEnum(args, "refreshOptions", "UpmList");
                SetEnumArray(args, "supportedStatusFilters");
                SetEnumArray(args, "supportedSortOptions", "NameAsc", "NameDesc");
                SetField(args, "filter", new Func<object, bool>(PackageManagerPackageStatus.IsGitHubPackage));
                SetField(args, "getGroupName", new Func<object, string>(_ => string.Empty));
                SetField(args, "compareGroup", new Func<string, string, int>(
                    (left, right) => string.Compare(left, right, StringComparison.OrdinalIgnoreCase)));
                contract.ArgsType = argsType;
                contract.AddPage = GetAddPageMethod(managerType, argsType);
                contract.GetRow = GetRowMethod(sidebarType);
                string[] names = { "name", "displayName", "priority", "icon", "capability",
                    "refreshOptions", "supportedStatusFilters", "supportedSortOptions", "filter",
                    "getGroupName", "compareGroup" };
                contract.ArgumentFields = new FieldInfo[names.Length];
                contract.ArgumentValues = new object[names.Length];
                for (int index = 0; index < names.Length; index++)
                {
                    FieldInfo field = argsType.GetField(names[index], Instance);
                    contract.ArgumentFields[index] = field;
                    contract.ArgumentValues[index] = field.GetValue(args);
                }
            }
            catch (Exception exception)
            {
                contract.Error = exception.GetBaseException().Message;
            }
            return contract;
        }

        internal static object CreateExtensionPageArgs()
        {
            NativeContract contract = Contract.Value;
            if (!IsSupportedContract(out string error))
                throw new InvalidOperationException(error);
            object args = Activator.CreateInstance(contract.ArgsType, true);
            for (int index = 0; index < contract.ArgumentFields.Length; index++)
            {
                object value = contract.ArgumentValues[index];
                contract.ArgumentFields[index].SetValue(args, value is Array array ? array.Clone() : value);
            }
            return args;
        }

        internal static bool IsGitHubPage(object candidate)
        {
            return string.Equals(ReadMember(candidate, "id") as string,
                ExtensionPageId, StringComparison.Ordinal);
        }

        internal static MethodInfo GetNativeRefreshTarget() => NativeRefresh.Value;

        internal static MethodInfo GetSidebarRefreshTarget() => SidebarRefresh.Value;

        private static object FindRegisteredPage(object manager)
        {
            if (!(ReadMember(manager, "orderedExtensionPages") is IEnumerable pages))
                return null;
            foreach (object candidate in pages)
                if (IsGitHubPage(candidate))
                    return candidate;
            return null;
        }

        private static void FindSidebarRows(VisualElement element, List<VisualElement> rows)
        {
            if (element.GetType().FullName == SidebarRowTypeName &&
                string.Equals(ReadMember(element, "pageId") as string,
                    ExtensionPageId, StringComparison.Ordinal))
                rows.Add(element);
            foreach (VisualElement child in element.Children())
                FindSidebarRows(child, rows);
        }

        private static VisualElement FindElement(VisualElement element, string typeName)
        {
            if (element.GetType().FullName == typeName)
                return element;
            foreach (VisualElement child in element.Children())
            {
                VisualElement found = FindElement(child, typeName);
                if (found != null)
                    return found;
            }
            return null;
        }

        private static Type FindType(string name)
        {
            // Public extensions and the guarded native contracts share Unity's UI assembly.
            return typeof(UnityEditor.PackageManager.UI.IPackageManagerExtension).Assembly.GetType(name, false);
        }

        private static object ReadMember(object instance, string name)
        {
            if (instance == null)
                return null;
            var key = (instance.GetType(), name);
            if (!Members.TryGetValue(key, out MemberInfo member))
            {
                for (Type type = key.Item1; type != null && member == null; type = type.BaseType)
                {
                    member = type.GetField(name, Instance | BindingFlags.DeclaredOnly);
                    if (member == null)
                    {
                        PropertyInfo property = type.GetProperty(name, Instance | BindingFlags.DeclaredOnly);
                        if (property != null && property.GetIndexParameters().Length == 0)
                            member = property;
                    }
                }
                Members.Add(key, member);
            }
            return member is FieldInfo field ? field.GetValue(instance) :
                (member as PropertyInfo)?.GetValue(instance);
        }

        private static MethodInfo GetAddPageMethod(Type managerType, Type argsType)
        {
            return GetVoidMethod(managerType, "AddExtensionPage", argsType);
        }

        private static MethodInfo GetRowMethod(Type sidebarType)
        {
            MethodInfo method = sidebarType?.GetMethod("GetRow", Instance,
                null, new[] { typeof(string) }, null);
            return method != null && !method.IsStatic && !method.IsGenericMethod &&
                   typeof(VisualElement).IsAssignableFrom(method.ReturnType) ? method : null;
        }

        private static MethodInfo GetVoidMethod(Type type, string name, params Type[] arguments)
        {
            if (type == null || Array.Exists(arguments, argument => argument == null))
                return null;
            MethodInfo method = type.GetMethod(name, Instance, null, arguments, null);
            return method != null && !method.IsStatic && !method.IsGenericMethod &&
                   method.ReturnType == typeof(void) ? method : null;
        }

        private static void SetField(object instance, string name, object value)
        {
            FieldInfo field = instance.GetType().GetField(name, Instance);
            if (field == null || !field.FieldType.IsInstanceOfType(value))
                throw new MissingFieldException(instance.GetType().FullName, name);
            field.SetValue(instance, value);
        }

        private static void SetEnum(object instance, string name, string value)
        {
            FieldInfo field = instance.GetType().GetField(name, Instance);
            if (field == null || !field.FieldType.IsEnum || !Enum.IsDefined(field.FieldType, value))
                throw new MissingFieldException(instance.GetType().FullName, name);
            field.SetValue(instance, Enum.Parse(field.FieldType, value));
        }

        private static void SetEnumArray(object instance, string name, params string[] values)
        {
            FieldInfo field = instance.GetType().GetField(name, Instance);
            Type elementType = field?.FieldType.GetElementType();
            if (elementType == null || !elementType.IsEnum)
                throw new MissingFieldException(instance.GetType().FullName, name);
            Array array = Array.CreateInstance(elementType, values.Length);
            for (int index = 0; index < values.Length; index++)
            {
                if (!Enum.IsDefined(elementType, values[index]))
                    throw new MissingFieldException(elementType.FullName, values[index]);
                array.SetValue(Enum.Parse(elementType, values[index]), index);
            }
            field.SetValue(instance, array);
        }
    }
}
