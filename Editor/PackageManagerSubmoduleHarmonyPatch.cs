using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEditor;
using UnityEngine.UIElements;

namespace MartinCalander.GitSubmoduleManager.Editor
{
#if UNITY_6000_5_OR_NEWER
    [Unity.Scripting.LifecycleManagement.NoAutoStaticsCleanup]
#endif
    internal static class PackageManagerSubmoduleHarmonyPatch
    {
        internal const string HarmonyId = "com.martincalander.gitsubmodulemanager.package-status";
        private const string Prefix = "UnityEditor.PackageManager.UI.Internal.";
        private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private const BindingFlags Static = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        private static readonly ConditionalWeakTable<TextElement, TagState> Tags = new();
        private static Harmony harmony;
        private static bool installed;
        internal static string LastError { get; private set; } = string.Empty;
        internal static bool IsSidebarRepairInstalled { get; private set; }

        private sealed class TagState
        {
            internal string Text;
            internal string Tooltip;
            internal bool HadEllipsis;
            internal VisualElement Container;
            internal EventCallback<AttachToPanelEvent> Attached;
        }

        internal static void Install()
        {
            if (installed || !PackageManagerUnityVersionSupport.IsCurrentVersionSupported)
                return;
            harmony ??= new Harmony(HarmonyId);
            LastError = string.Empty;
            bool hasTagTarget = false;
            foreach (MethodInfo target in TagTargets())
            {
                hasTagTarget = true;
                Patch(target, target.IsStatic ? nameof(TagFactoryPostfix) : nameof(TagPostfix), false);
            }
            if (!hasTagTarget)
                LastError += "Missing native contract for submodule labels. ";
            Patch(FindMethod("SourceInfoCard", "Refresh", typeof(void), "IPackageVersion"), nameof(SourcePostfix), false);
            Patch(FindMethod("RemoveCustomAction", "TriggerActionImplementation", typeof(bool), "IPackageVersion"), nameof(RemovePrefix), true);
            Patch(FindMethod("PackageOperationDispatcher", "RemoveEmbedded", typeof(void), "IPackage"), nameof(RemoveEmbeddedPrefix), true);
            Patch(PackageManagerSubmoduleNativePage.GetNativeRefreshTarget(), nameof(RefreshPostfix), false);
            MethodInfo sidebarRefresh = PackageManagerSubmoduleNativePage.GetSidebarRefreshTarget();
            Patch(sidebarRefresh, nameof(SidebarRefreshPostfix), false);
            IsSidebarRepairInstalled = IsPatched(sidebarRefresh);
            Patch(PackageRepositoryLink.GetRefreshTarget(), nameof(RepositoryLinksPostfix), false);
            // Failed seams remain eligible for a later attempt; completed installs need no reflection or patch inspection.
            installed = LastError.Length == 0 && IsSidebarRepairInstalled;
        }

        private static void Patch(MethodInfo target, string callback, bool prefix)
        {
            if (target == null)
            {
                LastError += "Missing native contract for " + callback + ". ";
                return;
            }
            try
            {
                if (IsPatched(target))
                    return;
                var patch = new HarmonyMethod(typeof(PackageManagerSubmoduleHarmonyPatch).GetMethod(callback, Static));
                harmony.Patch(target, prefix: prefix ? patch : null, postfix: prefix ? null : patch);
            }
            catch (Exception exception)
            {
                LastError += callback + ": " + exception.GetBaseException().Message + ". ";
            }
        }

        internal static bool IsPatched(MethodInfo method)
        {
            if (method == null)
                return false;
            var patches = Harmony.GetPatchInfo(method);
            return patches != null && patches.Owners.Contains(HarmonyId);
        }

        internal static IEnumerable<MethodInfo> TagTargets()
        {
            foreach (string name in new[] { "PackageDynamicTagLabel", "PackageTagLabel" })
            {
                Type type = FindType(Prefix + name);
                if (type == null)
                    continue;
                foreach (MethodInfo method in type.GetMethods(Instance | Static))
                {
                    ParameterInfo[] args = method.GetParameters();
                    bool validArguments = (args.Length == 1 || args.Length == 2 && args[1].ParameterType == typeof(bool)) &&
                                          args[0].ParameterType.FullName == Prefix + "IPackageVersion";
                    bool refresh = !method.IsStatic && method.Name == "Refresh" && method.ReturnType == typeof(void);
                    bool factory = method.IsStatic && method.Name == "CreateTagLabel" &&
                                   typeof(VisualElement).IsAssignableFrom(method.ReturnType);
                    if (!method.IsGenericMethod && validArguments && (refresh || factory))
                        yield return method;
                }
            }
        }

        internal static MethodInfo FindMethod(string type, string method, Type returns, string parameter)
        {
            Type argument = FindType(Prefix + parameter);
            if (argument == null)
                return null;
            MethodInfo result = FindType(Prefix + type)?.GetMethod(method, Instance | BindingFlags.DeclaredOnly,
                null, new[] { argument }, null);
            return result != null && !result.IsGenericMethod && result.ReturnType == returns ? result : null;
        }

        internal static Type FindType(string name)
        {
            return typeof(UnityEditor.PackageManager.UI.IPackageManagerExtension).Assembly.GetType(name, false);
        }

        internal static object Read(object instance, string name)
        {
            if (instance == null)
                return null;
            try
            {
                for (Type type = instance.GetType(); type != null; type = type.BaseType)
                {
                    FieldInfo field = type.GetField(name, Instance | BindingFlags.DeclaredOnly);
                    if (field != null)
                        return field.GetValue(instance);
                    PropertyInfo property = type.GetProperty(name, Instance | BindingFlags.DeclaredOnly);
                    if (property != null && property.GetIndexParameters().Length == 0)
                        return property.GetValue(instance);
                }
            }
            catch { }
            return null;
        }

        private static bool TryGetStatus(object version, out PackageSubmoduleStatus status)
        {
            status = null;
            return Read(version, "isInstalled") is bool installed && installed &&
                   PackageManagerPackageStatus.TryGetSubmodule(Read(version, "name") as string,
                       Read(version, "localPath") as string, out status);
        }

        private static void TagPostfix(object __instance, object __0) => UpdateTag(__instance, __0);
        private static void TagFactoryPostfix(object __result, object __0) => UpdateTag(__result, __0);

        private static void UpdateTag(object element, object version)
        {
            ApplyTag(element as TextElement, TryGetStatus(version, out _));
        }

        internal static void ApplyTag(TextElement label, bool submodule)
        {
            if (label == null)
                return;
            if (Tags.TryGetValue(label, out TagState previous))
            {
                // Native labels are recycled and can skip rebuilding an unchanged Custom tag.
                if (label.text == "Submodule")
                {
                    label.text = previous.Text;
                    label.tooltip = previous.Tooltip;
                    if (!previous.HadEllipsis)
                        label.RemoveFromClassList("disable-ellipsis");
                }
                if (previous.Attached != null)
                    label.UnregisterCallback(previous.Attached);
                if (previous.Container != null && previous.Container.style.maxWidth.keyword == StyleKeyword.None)
                    previous.Container.style.maxWidth = StyleKeyword.Null;
                Tags.Remove(label);
            }
            if (!submodule)
                return;
            var state = new TagState
            {
                Text = label.text,
                Tooltip = label.tooltip,
                HadEllipsis = label.ClassListContains("disable-ellipsis")
            };
            FitTagContainer(label, state);
            state.Attached = _ => FitTagContainer(label, state);
            label.RegisterCallback(state.Attached);
            Tags.Add(label, state);
            label.text = "Submodule";
            label.tooltip = "Installed as a Git submodule under Packages/.";
            label.AddToClassList("disable-ellipsis");
        }

        private static void FitTagContainer(TextElement label, TagState state)
        {
            if (label.parent?.name == "tagContainer" && label.parent.style.maxWidth.keyword == StyleKeyword.Null)
            {
                state.Container = label.parent;
                state.Container.style.maxWidth = StyleKeyword.None;
            }
        }

        private static void SourcePostfix(object __instance, object __0)
        {
            if (!TryGetStatus(__0, out PackageSubmoduleStatus status) || !(__instance is VisualElement card))
                return;
            TextElement text = card.Q<TextElement>(className: "informationCardText");
            if (text != null)
            {
                text.text = status.IsGitHub ? "GitHub" : "Git";
                text.tooltip = "Git submodule";
            }
        }

        internal static bool MustBlockRemoval(object version, Func<string, string, bool> shouldBlock)
        {
            // This boundary handles Unity's raw embedded-folder deletion only.
            string name = Read(version, "name") as string;
            string path = Read(version, "localPath") as string;
            return string.IsNullOrEmpty(name) || string.IsNullOrEmpty(path) || shouldBlock(name, path);
        }

        private static bool RemovePrefix(object __0, ref bool __result)
        {
            if (!MustBlockRemoval(__0, PackageManagerPackageStatus.ShouldBlockEmbeddedRemoval))
                return true;
            __result = false;
            EditorUtility.DisplayDialog("Manage this checkout with Git",
                "This package is a Git checkout, or its submodule status could not be verified. " +
                "Remove it with Git after reviewing your local work. Package Manager cannot safely delete it as an embedded folder.", "OK");
            return false;
        }

        private static bool RemoveEmbeddedPrefix(object __0)
        {
            object installed = Read(Read(__0, "versions"), "installed");
            return !MustBlockRemoval(installed, PackageManagerPackageStatus.ShouldBlockEmbeddedRemoval);
        }

        private static void SidebarRefreshPostfix(object __instance)
        {
            if (!(__instance is VisualElement sidebar))
                return;
            // Details UI stays detached without a single selection, so sidebar creation also observes the window.
            GitSubmoduleManagerPackageManagerHost.ObserveAncestors(sidebar);
            PackageManagerSubmoduleNativePage.NotifySidebarRefreshed(sidebar);
        }

        private static void RefreshPostfix(object __0)
        {
            if (PackageManagerSubmoduleNativePage.IsOwnedPage(__0))
                PackageManagerPackageStatus.Refresh();
        }

        private static void RepositoryLinksPostfix(object __instance, object __0)
        {
            if (__instance is VisualElement links)
            {
                GitSubmoduleManagerPackageManagerHost.ObserveAncestors(links);
                PackageRepositoryLink.Refresh(links, __0);
            }
        }

        internal static void RefreshActivePage(VisualElement root)
        {
            try
            {
                object manager = Read(root, "m_PageManager") ?? Read(root, "pageManager");
                object page = Read(manager, "activePage");
                if (page == null || PackageManagerSubmoduleNativePage.IsOwnedPage(page))
                    return;
                MethodInfo rebuild = page.GetType().GetMethod("Rebuild", Instance, null, new[] { typeof(bool) }, null);
                if (rebuild != null && rebuild.ReturnType == typeof(void))
                    rebuild.Invoke(page, new object[] { true });
                MethodInfo selection = page.GetType().GetMethod("TriggerOnSelectionChanged", Instance, null, new[] { typeof(bool) }, null);
                selection?.Invoke(page, new object[] { false });
            }
            catch { }
        }

        internal static void Uninstall()
        {
            installed = false;
            IsSidebarRepairInstalled = false;
            if (harmony == null)
                return;
#if ENABLE_CORECLR
            harmony.UnpatchSelf();
#else
            harmony.UnpatchAll(HarmonyId);
#endif
            harmony = null;
        }
    }
}
