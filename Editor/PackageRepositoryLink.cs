using System;
using System.Reflection;
using UnityEngine;
using UnityEngine.UIElements;

namespace MartinCalander.GitSubmoduleManager.Editor
{
#if UNITY_6000_5_OR_NEWER
    [Unity.Scripting.LifecycleManagement.NoAutoStaticsCleanup]
#endif
    internal static class PackageRepositoryLink
    {
        internal const string ElementName = "git-submodule-repository-link";
        private const string SeparatorName = ElementName + "-separator";
        private const string RowName = "upmLinksContainer";
        private const string OwnedRowClass = "git-submodule-repository-row";
        private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private static readonly Lazy<NativeContract> Contract = new(ResolveContract);

        private sealed class NativeContract
        {
            internal Type LinksType;
            internal Type HeaderType;
            internal FieldInfo Version;
            internal MethodInfo Refresh;
        }

        internal static MethodInfo GetRefreshTarget() => Contract.Value.Refresh;

        internal static void Refresh(VisualElement links, object version)
        {
            if (links == null)
                return;
            string url = CurrentUrl(links, version);
            Button existing = links.Q<Button>(ElementName);
            if (!string.IsNullOrEmpty(url) && existing?.tooltip == url &&
                ReferenceEquals(existing.userData, version))
                return;
            Remove(links);
            if (string.IsNullOrEmpty(url))
                return;
            Button button = null;
            button = Prepend(links, url, () =>
            {
                if (button.panel != null && links.Contains(button) &&
                    string.Equals(CurrentUrl(links, version), url, StringComparison.Ordinal))
                    Application.OpenURL(url);
            });
            button.userData = version;
        }

        internal static void RefreshInRoot(VisualElement root)
        {
            root.Query<VisualElement>().ForEach(element =>
            {
                if (element.GetType() == Contract.Value.LinksType)
                    Refresh(element, PackageManagerSubmoduleHarmonyPatch.Read(FindHeader(element), "m_Version"));
            });
        }

        private static VisualElement FindHeader(VisualElement links)
        {
            for (VisualElement parent = links?.parent; parent != null; parent = parent.parent)
                if (parent.GetType() == Contract.Value.HeaderType)
                    return parent;
            return null;
        }

        internal static string CurrentUrl(VisualElement links, object version)
        {
            try
            {
                NativeContract contract = Contract.Value;
                if (contract.Refresh == null || links?.GetType() != contract.LinksType || version == null)
                    return string.Empty;
                VisualElement header = FindHeader(links);
                if (header != null && ReferenceEquals(contract.Version.GetValue(header), version))
                    return PackageManagerPackageStatus.GetRepositoryUrl(version);
            }
            catch { }
            return string.Empty;
        }

        internal static Button Prepend(VisualElement links, string url, Action clicked)
        {
            VisualElement row = links.Q(RowName);
            if (row == null)
            {
                row = new VisualElement { name = RowName };
                row.AddToClassList("left");
                row.AddToClassList(OwnedRowClass);
                links.Add(row);
            }
            if (row.childCount != 0)
            {
                var separator = new Label("|") { name = SeparatorName };
                separator.AddToClassList("separator");
                row.Insert(0, separator);
            }
            var button = new Button(clicked) { name = ElementName, text = "Repository", tooltip = url };
            button.AddToClassList("link");
            row.Insert(0, button);
            links.style.display = DisplayStyle.Flex;
            return button;
        }

        internal static void Remove(VisualElement root)
        {
            foreach (VisualElement link in root.Query(name: ElementName).ToList())
            {
                VisualElement row = link.parent;
                link.RemoveFromHierarchy();
                row?.Q(SeparatorName)?.RemoveFromHierarchy();
                if (row?.childCount == 0 && row.ClassListContains(OwnedRowClass))
                {
                    VisualElement links = row.parent;
                    row.RemoveFromHierarchy();
                    if (links != null && links.childCount == 0)
                        links.style.display = DisplayStyle.None;
                }
            }
        }

        private static NativeContract ResolveContract()
        {
            var contract = new NativeContract();
            if (!PackageManagerUnityVersionSupport.IsCurrentVersionSupported)
                return contract;
            try
            {
                const string prefix = "UnityEditor.PackageManager.UI.Internal.";
                contract.LinksType = PackageManagerSubmoduleHarmonyPatch.FindType(prefix + "PackageDetailsLinks");
                contract.HeaderType = PackageManagerSubmoduleHarmonyPatch.FindType(prefix + "PackageDetailsHeader");
                contract.Version = contract.HeaderType?.GetField("m_Version", Instance);
                if (contract.LinksType != null && typeof(VisualElement).IsAssignableFrom(contract.LinksType) &&
                    contract.HeaderType != null && typeof(VisualElement).IsAssignableFrom(contract.HeaderType) &&
                    contract.Version?.FieldType == PackageManagerSubmoduleHarmonyPatch.FindType(prefix + "IPackageVersion"))
                    contract.Refresh = PackageManagerSubmoduleHarmonyPatch.FindMethod(
                        "PackageDetailsLinks", "Refresh", typeof(void), "IPackageVersion");
            }
            catch { }
            return contract;
        }
    }
}
