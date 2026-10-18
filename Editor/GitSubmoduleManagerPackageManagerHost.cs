using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Callbacks;
using UnityEditor.PackageManager;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;
using UnityEditor.PackageManager.UI;
using UnityEngine;
using UnityEngine.UIElements;

namespace MartinCalander.GitSubmoduleManager.Editor
{
    [InitializeOnLoad]
#if UNITY_6000_5_OR_NEWER
    [Unity.Scripting.LifecycleManagement.NoAutoStaticsCleanup]
#endif
    internal static class GitSubmoduleManagerPackageManagerHost
    {
        internal const string WindowTypeName = "UnityEditor.PackageManager.UI.PackageManagerWindow";
        private static readonly Dictionary<EditorWindow, Session> Sessions = new();
        private static readonly StatusDetails Details = new();
        private static double nextScan;
        private static bool registered;
        internal static string LastError { get; private set; } = string.Empty;

        private sealed class Session : IDisposable
        {
            internal readonly VisualElement Root;
            internal readonly PackageManagerSubmoduleNativePage Page;

            internal Session(VisualElement root)
            {
                Root = root;
                Page = new PackageManagerSubmoduleNativePage(root);
                PackageManagerPackageStatus.Retain();
            }

            public void Dispose()
            {
                Page.Dispose();
                PackageManagerPackageStatus.Release();
            }
        }

        static GitSubmoduleManagerPackageManagerHost() => Start();

        [DidReloadScripts]
        private static void Start()
        {
            PackageManagerPackageStatus.Resume();
            if (!registered)
            {
                PackageManagerExtensions.RegisterExtension(Details);
                registered = true;
            }
            EditorApplication.update -= Update;
            EditorApplication.update += Update;
            AssemblyReloadEvents.beforeAssemblyReload -= Stop;
            AssemblyReloadEvents.beforeAssemblyReload += Stop;
            EditorApplication.quitting -= Stop;
            EditorApplication.quitting += Stop;
            PackageManagerPackageStatus.Changed -= RefreshViews;
            PackageManagerPackageStatus.Changed += RefreshViews;
            PackageManagerSubmoduleHarmonyPatch.Install();
            nextScan = 0;
        }

        private static void Update()
        {
            if (EditorApplication.timeSinceStartup < nextScan)
                return;
            nextScan = EditorApplication.timeSinceStartup + 1;
            var alive = new HashSet<EditorWindow>();
            foreach (EditorWindow window in Resources.FindObjectsOfTypeAll<EditorWindow>())
            {
                if (window == null || window.GetType().FullName != WindowTypeName)
                    continue;
                var root = PackageManagerSubmoduleHarmonyPatch.Read(window, "m_Root") as VisualElement;
                if (root == null || root.panel == null)
                    continue;
                alive.Add(window);
                if (Sessions.TryGetValue(window, out Session old) && !ReferenceEquals(old.Root, root))
                {
                    old.Dispose();
                    Sessions.Remove(window);
                }
                if (!Sessions.TryGetValue(window, out Session session))
                {
                    session = new Session(root);
                    Sessions.Add(window, session);
                }
                session.Page.TryAttach(out string error);
                LastError = error;
            }
            foreach (EditorWindow window in new List<EditorWindow>(Sessions.Keys))
            {
                if (alive.Contains(window))
                    continue;
                Sessions[window].Dispose();
                Sessions.Remove(window);
            }
        }

        private static void RefreshViews()
        {
            Details.Refresh();
            foreach (KeyValuePair<EditorWindow, Session> pair in Sessions)
            {
                pair.Value.Page.Refresh();
                PackageManagerSubmoduleHarmonyPatch.RefreshActivePage(pair.Value.Root);
                if (pair.Key != null)
                    pair.Key.Repaint();
            }
        }

        private static void Stop()
        {
            EditorApplication.update -= Update;
            PackageManagerPackageStatus.Changed -= RefreshViews;
            foreach (Session session in Sessions.Values)
                session.Dispose();
            Sessions.Clear();
            PackageManagerSubmoduleHarmonyPatch.Uninstall();
        }

        private sealed class StatusDetails : IPackageManagerExtension
        {
            private Label label;
            private PackageInfo selected;

            public VisualElement CreateExtensionUI()
            {
                label = new Label { name = "git-submodule-package-status" };
                label.style.whiteSpace = WhiteSpace.Normal;
                label.style.marginTop = 8;
                label.style.marginBottom = 8;
                Refresh();
                return label;
            }

            public void OnPackageSelectionChange(PackageInfo packageInfo)
            {
                selected = packageInfo;
                Refresh();
            }

            public void OnPackageAddedOrUpdated(PackageInfo packageInfo) => PackageManagerPackageStatus.Refresh();
            public void OnPackageRemoved(PackageInfo packageInfo) => PackageManagerPackageStatus.Refresh();

            internal void Refresh()
            {
                if (label == null)
                    return;
                label.style.display = selected == null ? DisplayStyle.None : DisplayStyle.Flex;
                label.text = selected == null ? string.Empty : PackageManagerPackageStatus.Describe(selected);
                label.tooltip = selected?.resolvedPath ?? string.Empty;
            }
        }
    }
}
