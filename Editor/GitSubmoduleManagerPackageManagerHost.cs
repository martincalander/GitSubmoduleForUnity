using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Callbacks;
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
        private const string RootTypeName = "UnityEditor.PackageManager.UI.Internal.PackageManagerWindowRoot";
        private static readonly Dictionary<VisualElement, Session> Sessions = new();
        private static readonly StatusDetails Details = new();
        private static bool registered;
        internal static string LastError { get; private set; } = string.Empty;
        internal static int SessionCount => Sessions.Count;
        internal static int PendingAttachmentCount
        {
            get
            {
                int count = 0;
                foreach (Session session in Sessions.Values)
                    if (session.HasPendingAttachment)
                        count++;
                return count;
            }
        }

        private sealed class Session : IDisposable
        {
            internal readonly VisualElement Root;
            internal readonly PackageManagerSubmoduleNativePage Page;
            internal readonly PackageManagerSubmoduleFilter Filter;
            private IVisualElementScheduledItem retry;
            private int attempts;
            private bool attached;
            private bool disposed;
            internal bool HasPendingAttachment => retry != null;

            internal Session(VisualElement root)
            {
                Root = root;
                Page = new PackageManagerSubmoduleNativePage(root);
                Filter = new PackageManagerSubmoduleFilter(root);
                Root.RegisterCallback<DetachFromPanelEvent>(OnDetached);
                Page.AttachmentInvalidated += OnAttachmentInvalidated;
                Filter.AttachmentInvalidated += OnAttachmentInvalidated;
            }

            internal void Start()
            {
                PackageManagerPackageStatus.Retain();
                ScheduleAttachment();
            }

            private void OnDetached(DetachFromPanelEvent evt)
            {
                if (!ReferenceEquals(evt.target, Root))
                    return;
                Sessions.Remove(Root);
                Dispose();
            }

            private void OnAttachmentInvalidated()
            {
                attached = false;
                ScheduleAttachment();
            }

            internal void ScheduleAttachment()
            {
                if (disposed || attached || retry != null || Root.panel == null)
                    return;
                bool sourceSupported = PackageManagerSubmoduleNativePage.IsSupportedContract(out string sourceError);
                bool filterSupported = PackageManagerSubmoduleFilter.IsSupportedContract(out string filterError);
                if (!sourceSupported && !filterSupported)
                {
                    LastError = sourceError + " " + filterError;
                    return;
                }
                attempts = 0;
                // Retry native UI construction briefly, then sleep until a new lifecycle event.
                retry = Root.schedule.Execute(Attach).Every(100);
            }

            private void Attach()
            {
                if (disposed)
                    return;
                bool sourceAttached = Page.TryAttach(out string sourceError);
                bool filterAttached = Filter.TryAttach(out string filterError);
                attached = sourceAttached && filterAttached && Page.IsAttached && Filter.IsAttached;
                LastError = string.IsNullOrEmpty(sourceError) ? filterError :
                    string.IsNullOrEmpty(filterError) ? sourceError : sourceError + " " + filterError;
                if (attached || ++attempts >= 50)
                {
                    retry?.Pause();
                    retry = null;
                }
            }

            public void Dispose()
            {
                if (disposed)
                    return;
                disposed = true;
                retry?.Pause();
                retry = null;
                Root.UnregisterCallback<DetachFromPanelEvent>(OnDetached);
                Page.AttachmentInvalidated -= OnAttachmentInvalidated;
                Filter.AttachmentInvalidated -= OnAttachmentInvalidated;
                Page.Dispose();
                Filter.Dispose();
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
            AssemblyReloadEvents.beforeAssemblyReload -= BeforeReload;
            AssemblyReloadEvents.beforeAssemblyReload += BeforeReload;
            EditorApplication.quitting -= Stop;
            EditorApplication.quitting += Stop;
            PackageManagerPackageStatus.Changed -= RefreshViews;
            PackageManagerPackageStatus.Changed += RefreshViews;
            PackageManagerPackageStatus.ReadinessChanged -= Details.Refresh;
            PackageManagerPackageStatus.ReadinessChanged += Details.Refresh;
            PackageManagerSubmoduleHarmonyPatch.Install();
            // A window can already exist when scripts reload. This is a single catch-up pass.
            EditorApplication.delayCall -= AttachExistingWindows;
            EditorApplication.delayCall += AttachExistingWindows;
        }

        private static void AttachExistingWindows()
        {
            foreach (EditorWindow window in Resources.FindObjectsOfTypeAll<EditorWindow>())
            {
                if (window != null && window.GetType().FullName == WindowTypeName)
                    ObserveRoot(PackageManagerSubmoduleHarmonyPatch.Read(window, "m_Root") as VisualElement);
            }
        }

        internal static void ObserveRoot(VisualElement root)
        {
            if (root == null || root.panel == null)
                return;
            if (Sessions.TryGetValue(root, out Session existing))
            {
                existing.ScheduleAttachment();
                return;
            }
            var session = new Session(root);
            Sessions.Add(root, session);
            session.Start();
        }

        internal static void ObserveAncestors(VisualElement element)
        {
            for (VisualElement parent = element.parent; parent != null; parent = parent.parent)
            {
                if (parent.GetType().FullName != RootTypeName)
                    continue;
                ObserveRoot(parent);
                return;
            }
        }

        private static void RefreshViews()
        {
            Details.Refresh();
            foreach (Session session in Sessions.Values)
            {
                session.ScheduleAttachment();
                session.Page.Refresh();
                session.Filter.Refresh();
                PackageManagerSubmoduleHarmonyPatch.RefreshActivePage(session.Root);
            }
        }

        private static void BeforeReload()
        {
            Stop();
            // Unity clears the legacy extension registry on code reload, even on CoreCLR.
            registered = false;
        }

        private static void Stop()
        {
            EditorApplication.delayCall -= AttachExistingWindows;
            PackageManagerPackageStatus.Changed -= RefreshViews;
            PackageManagerPackageStatus.ReadinessChanged -= Details.Refresh;
            foreach (Session session in Sessions.Values)
                session.Dispose();
            Sessions.Clear();
            Details.Clear();
            PackageManagerSubmoduleHarmonyPatch.Uninstall();
        }

        private sealed class StatusDetails : IPackageManagerExtension
        {
            private readonly HashSet<PackageStatusDetailsView> views = new();
            private PackageInfo selected;

            public VisualElement CreateExtensionUI()
            {
                var view = new PackageStatusDetailsView();
                view.RegisterCallback<AttachToPanelEvent>(_ =>
                {
                    views.Add(view);
                    ObserveAncestors(view);
                    view.Refresh(selected);
                });
                view.RegisterCallback<DetachFromPanelEvent>(_ => views.Remove(view));
                view.Refresh(selected);
                return view;
            }

            public void OnPackageSelectionChange(PackageInfo packageInfo)
            {
                selected = packageInfo;
                Refresh();
            }

            // Registered-package events already provide one coalesced status refresh.
            public void OnPackageAddedOrUpdated(PackageInfo packageInfo) { }
            public void OnPackageRemoved(PackageInfo packageInfo) { }

            internal void Refresh()
            {
                foreach (PackageStatusDetailsView view in views)
                    view.Refresh(selected);
            }

            internal void Clear()
            {
                foreach (PackageStatusDetailsView view in views)
                    view.Refresh(null);
                views.Clear();
                selected = null;
            }
        }
    }
}
