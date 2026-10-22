using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Callbacks;
using UnityEngine;
using UnityEngine.UIElements;

namespace MartinCalander.GitSubmoduleManager.Editor
{
#if UNITY_6000_5_OR_NEWER
    // Package Manager sessions belong to the Editor, including while Play mode is active.
    [Unity.Scripting.LifecycleManagement.NoAutoStaticsCleanup]
#else
    [InitializeOnLoad]
#endif
    internal static partial class GitSubmoduleManagerPackageManagerHost
    {
        internal const string WindowTypeName = "UnityEditor.PackageManager.UI.PackageManagerWindow";
        private const string RootTypeName = "UnityEditor.PackageManager.UI.Internal.PackageManagerWindowRoot";
        private static readonly Dictionary<VisualElement, Session> Sessions = new();
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
            private IVisualElementScheduledItem retry;
            private int attempts;
            private bool attached;
            private bool presentationObserved;
            private bool disposed;
            internal bool HasPendingAttachment => retry != null;

            internal Session(VisualElement root)
            {
                Root = root;
                Page = new PackageManagerSubmoduleNativePage(root);
                Root.RegisterCallback<DetachFromPanelEvent>(OnDetached);
                Page.AttachmentInvalidated += OnAttachmentInvalidated;
            }

            internal void Start()
            {
                PackageManagerPackageStatus.Retain();
                ScheduleAttachment();
                PackageRepositoryLink.RefreshInRoot(Root);
                PackageManagerSubmoduleActions.AttachInRoot(Root);
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
                if (!PackageManagerSubmoduleNativePage.IsSupportedContract(out string error))
                {
                    LastError = error;
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
                attached = Page.TryAttach(out string error) && Page.IsAttached;
                LastError = error;
                if (attached && !presentationObserved)
                {
                    // Seed hooks for native elements that existed before this session attached.
                    RefreshViews();
                }
                if (attached || ++attempts >= 50)
                {
                    retry?.Pause();
                    retry = null;
                }
            }

            internal void RefreshViews()
            {
                PackageManagerSubmoduleActions.AttachInRoot(Root);
                ScheduleAttachment();
                if (Page.Refresh())
                    presentationObserved = true;
                else
                {
                    if (!presentationObserved)
                        presentationObserved = PackageManagerSubmoduleHarmonyPatch.RefreshActivePage(Root);
                    PackageManagerSubmoduleHarmonyPatch.RefreshPresentation(Root);
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
                Page.Dispose();
                PackageManagerPackageStatus.Release();
            }
        }

#if UNITY_6000_5_OR_NEWER
        [Unity.Scripting.LifecycleManagement.OnCodeInitializing]
#else
        static GitSubmoduleManagerPackageManagerHost() => Start();
        [DidReloadScripts]
#endif
        private static void Start()
        {
            PackageManagerPackageStatus.Resume();
#if !UNITY_6000_5_OR_NEWER
            AssemblyReloadEvents.beforeAssemblyReload -= Stop;
            AssemblyReloadEvents.beforeAssemblyReload += Stop;
#endif
            EditorApplication.quitting -= Stop;
            EditorApplication.quitting += Stop;
            PackageManagerPackageStatus.Changed -= RefreshViews;
            PackageManagerPackageStatus.Changed += RefreshViews;
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
                PackageManagerSubmoduleActions.AttachInRoot(root);
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
            foreach (Session session in Sessions.Values)
                session.RefreshViews();
        }

#if UNITY_6000_5_OR_NEWER
        [Unity.Scripting.LifecycleManagement.OnCodeUnloading]
#endif
        private static void Stop()
        {
#if !UNITY_6000_5_OR_NEWER
            AssemblyReloadEvents.beforeAssemblyReload -= Stop;
#endif
            EditorApplication.quitting -= Stop;
            EditorApplication.delayCall -= AttachExistingWindows;
            PackageManagerPackageStatus.Changed -= RefreshViews;
            foreach (Session session in Sessions.Values)
            {
                PackageRepositoryLink.Remove(session.Root);
                session.Dispose();
            }
            Sessions.Clear();
            PackageManagerSubmoduleHarmonyPatch.Uninstall();
        }

    }
}
