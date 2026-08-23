using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace MartinCalander.GitSubmoduleManager.Editor
{
    /// <summary>
    /// Installs the native Sources &gt; GitHub extension into Unity's Package
    /// Manager. This host never replaces or hides Unity's Package Manager UI.
    /// </summary>
    [InitializeOnLoad]
    internal static class GitSubmoduleManagerPackageManagerHost
    {
        internal enum DetachedWindowAction
        {
            Repair,
            Release
        }

        internal const string HarmonyId =
            "com.martincalander.gitsubmodulemanager.package-manager-host";
        internal const string PackageManagerWindowTypeName =
            "UnityEditor.PackageManager.UI.PackageManagerWindow";
        private const string SidebarElementName = "sidebar";
        private const double WindowScanIntervalSeconds = 0.5;
        private const double OpenRequestTimeoutSeconds = 10.0;
        private const double PatchRetryIntervalSeconds = 0.5;
        private const double PatchRetryWindowSeconds = 10.0;
        private const double SessionRepairTimeoutSeconds = 10.0;

        private static readonly string[] LifecycleMethodNames =
        {
            "BuildGUI",
            "CreateGUI",
            "OnEnable"
        };

        private static readonly Dictionary<EditorWindow, HostSession> Sessions = new();
        private static readonly List<EditorWindow> DeadSessionWindows = new();

        private static Harmony harmony;
        private static bool isPatched;
        private static bool updateSubscribed;
        private static bool patchRetryActive;
        private static bool isShuttingDown;
        private static bool openRequested;
        private static double openRequestDeadline;
        private static double nextWindowScanTime;
        private static double patchRetryDeadline;
        private static double nextPatchRetryTime;

        static GitSubmoduleManagerPackageManagerHost()
        {
            if (!TryPatch(out _))
            {
                BeginPatchRetryWindow();
                EditorApplication.delayCall += RetryPatch;
            }

            EditorApplication.delayCall += AttachExistingWindows;
            AssemblyReloadEvents.beforeAssemblyReload += BeforeAssemblyReload;
            EditorApplication.quitting += BeforeEditorQuits;
        }

        internal static void Open()
        {
            OpenGitHubSource();
        }

        internal static void Open(bool openWelcome)
        {
            if (openWelcome)
            {
                GitSubmoduleManagerWelcomeWindow.OpenWindow();
                return;
            }

            OpenGitHubSource();
        }

        internal static void OpenGitHubSource()
        {
            if (isShuttingDown)
                return;

            if (!isPatched && !TryPatch(out _))
                BeginPatchRetryWindow();
            openRequested = true;
            nextWindowScanTime = 0d;
            openRequestDeadline =
                EditorApplication.timeSinceStartup + OpenRequestTimeoutSeconds;
            SubscribeUpdate();
            TryOpenPackageManagerThroughReflection();
            EditorApplication.delayCall += AttachExistingWindows;
        }

        internal static bool TryPatch()
        {
            return TryPatch(out _);
        }

        internal static bool TryPatch(out string error)
        {
            error = string.Empty;
            if (isPatched)
                return true;

            try
            {
                Type windowType = GetPackageManagerWindowType();
                if (windowType == null)
                {
                    error = $"Could not find {PackageManagerWindowTypeName}.";
                    return false;
                }

                List<MethodInfo> lifecycleMethods =
                    GetSupportedLifecycleMethods(windowType);
                if (lifecycleMethods.Count == 0)
                {
                    error = $"Could not find BuildGUI or CreateGUI on {PackageManagerWindowTypeName}.";
                    return false;
                }

                MethodInfo postfix = typeof(GitSubmoduleManagerPackageManagerHost)
                    .GetMethod(
                        nameof(AfterPackageManagerGuiBuilt),
                        BindingFlags.Static | BindingFlags.NonPublic);
                MethodInfo prefix = typeof(GitSubmoduleManagerPackageManagerHost)
                    .GetMethod(
                        nameof(BeforePackageManagerGuiBuilt),
                        BindingFlags.Static | BindingFlags.NonPublic);
                if (postfix == null || prefix == null)
                {
                    error = "The Package Manager host patches could not be resolved.";
                    return false;
                }

                harmony ??= new Harmony(HarmonyId);
                foreach (MethodInfo lifecycleMethod in lifecycleMethods)
                {
                    if (!HasPrefix(lifecycleMethod, prefix))
                        harmony.Patch(lifecycleMethod, prefix: new HarmonyMethod(prefix));
                    if (!HasPostfix(lifecycleMethod, postfix))
                        harmony.Patch(lifecycleMethod, postfix: new HarmonyMethod(postfix));
                }

                isPatched = true;
                foreach (MethodInfo lifecycleMethod in lifecycleMethods)
                {
                    isPatched &= HasPrefix(lifecycleMethod, prefix) &&
                                 HasPostfix(lifecycleMethod, postfix);
                }

                if (PackageManagerSubmoduleNativePage.IsSupportedContract())
                    isPatched &= TryPatchSidebarExtensionRefresh();

                if (!isPatched)
                {
                    error = "Harmony did not register every supported Package Manager host hook.";
                }

                return isPatched;
            }
            catch (Exception exception)
            {
                isPatched = false;
                error = GitHubUtility.SanitizeUiDiagnostic(exception.Message);
                return false;
            }
        }

        internal static Type GetPackageManagerWindowType()
        {
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type type = assembly.GetType(PackageManagerWindowTypeName, false);
                if (type != null && typeof(EditorWindow).IsAssignableFrom(type))
                    return type;
            }

            return null;
        }

        internal static List<MethodInfo> GetSupportedLifecycleMethods(Type windowType)
        {
            var result = new List<MethodInfo>();
            if (windowType == null)
                return result;

            const BindingFlags flags =
                BindingFlags.Instance |
                BindingFlags.Public |
                BindingFlags.NonPublic |
                BindingFlags.DeclaredOnly;
            foreach (string methodName in LifecycleMethodNames)
            {
                MethodInfo method = windowType.GetMethod(
                    methodName,
                    flags,
                    null,
                    Type.EmptyTypes,
                    null);
                if (method != null && method.ReturnType == typeof(void))
                {
                    result.Add(method);
                    break;
                }
            }

            return result;
        }

        internal static Foldout FindSourcesFoldout(VisualElement root)
        {
            if (root == null)
                return null;

            VisualElement sidebar = FindSidebar(root);
            if (sidebar == null)
                return null;

            MethodInfo getRow = sidebar.GetType().GetMethod(
                "GetRow",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null,
                new[] { typeof(string) },
                null);
            try
            {
                if (getRow?.Invoke(sidebar, new object[] { "MyAssets" }) is
                        VisualElement row &&
                    row.parent is Foldout sources)
                {
                    return sources;
                }
            }
            catch
            {
                // Fall through to the visual-tree lookup.
            }

            var foldouts = new List<Foldout>();
            CollectDescendants(sidebar, foldouts);
            string localizedSources = L10n.Tr("Sources");
            foreach (Foldout foldout in foldouts)
            {
                if (string.Equals(
                        foldout.text,
                        localizedSources,
                        StringComparison.Ordinal) ||
                    string.Equals(
                        foldout.text,
                        "Sources",
                        StringComparison.OrdinalIgnoreCase))
                {
                    return foldout;
                }
            }

            return foldouts.Count > 1 ? foldouts[1] : null;
        }

        internal static bool IsDescendantOrSelf(
            VisualElement ancestor,
            VisualElement candidate)
        {
            if (ancestor == null || candidate == null)
                return false;

            for (VisualElement current = candidate;
                 current != null;
                 current = current.parent)
            {
                if (ReferenceEquals(current, ancestor))
                    return true;
            }

            return false;
        }

        internal static DetachedWindowAction GetDetachedWindowAction(
            bool windowAlive)
        {
            if (!windowAlive)
                return DetachedWindowAction.Release;

            // Root-owned menus/actions also observe DetachFromPanel and retire
            // themselves. Even if Unity has already reattached the panel by our
            // delayed callback, run one repair pass to remount those controls.
            return DetachedWindowAction.Repair;
        }

        internal static bool ShouldPollHostSession(
            bool isDisposed,
            bool windowAlive,
            bool panelAttached,
            bool isAttached,
            bool withinRepairWindow)
        {
            if (isDisposed)
                return false;

            // Poll a destroyed reference once more so CleanupDeadSessions can
            // remove its dictionary entry. A live detached tab must continue
            // being observed even after the bounded visual-repair window.
            if (!windowAlive || !panelAttached)
                return true;

            return !isAttached && withinRepairWindow;
        }

        internal static bool IsSidebarExtensionRefreshPatchApplied()
        {
            Type sidebarType = FindType(PackageManagerSubmoduleNativePage.SidebarTypeName);
            MethodInfo target = PackageManagerSubmoduleNativePage
                .FindSidebarExtensionRowsUpdateMethod(sidebarType);
            MethodInfo postfix = typeof(GitSubmoduleManagerPackageManagerHost)
                .GetMethod(
                    nameof(AfterSidebarExtensionRowsUpdated),
                    BindingFlags.Static | BindingFlags.NonPublic);
            return target != null && postfix != null && HasPostfix(target, postfix);
        }

        internal static bool AreLifecyclePatchesApplied()
        {
            Type windowType = GetPackageManagerWindowType();
            List<MethodInfo> lifecycleMethods = GetSupportedLifecycleMethods(windowType);
            MethodInfo postfix = typeof(GitSubmoduleManagerPackageManagerHost)
                .GetMethod(
                    nameof(AfterPackageManagerGuiBuilt),
                    BindingFlags.Static | BindingFlags.NonPublic);
            MethodInfo prefix = typeof(GitSubmoduleManagerPackageManagerHost)
                .GetMethod(
                    nameof(BeforePackageManagerGuiBuilt),
                    BindingFlags.Static | BindingFlags.NonPublic);
            if (lifecycleMethods.Count == 0 || postfix == null || prefix == null)
                return false;

            foreach (MethodInfo lifecycleMethod in lifecycleMethods)
            {
                if (!HasPrefix(lifecycleMethod, prefix) ||
                    !HasPostfix(lifecycleMethod, postfix))
                {
                    return false;
                }
            }

            return true;
        }

        private static void RetryPatch()
        {
            if (!TryPatch(out string error))
            {
                Debug.LogWarning(
                    "[Git Submodule Manager] The native Package Manager integration " +
                    "could not install its compatibility hook. " + error);
            }
            else
            {
                patchRetryActive = false;
            }

            AttachExistingWindows();
            UpdateSubscription();
        }

        private static bool HasPostfix(MethodInfo original, MethodInfo postfix)
        {
            Patches patches = Harmony.GetPatchInfo(original);
            if (patches == null)
                return false;

            foreach (Patch patch in patches.Postfixes)
            {
                if (patch.owner == HarmonyId && patch.PatchMethod == postfix)
                    return true;
            }

            return false;
        }

        private static bool HasPrefix(MethodInfo original, MethodInfo prefix)
        {
            Patches patches = Harmony.GetPatchInfo(original);
            if (patches == null)
                return false;

            foreach (Patch patch in patches.Prefixes)
            {
                if (patch.owner == HarmonyId && patch.PatchMethod == prefix)
                    return true;
            }

            return false;
        }

        private static bool TryPatchSidebarExtensionRefresh()
        {
            Type sidebarType = FindType(PackageManagerSubmoduleNativePage.SidebarTypeName);
            MethodInfo target = PackageManagerSubmoduleNativePage
                .FindSidebarExtensionRowsUpdateMethod(sidebarType);
            MethodInfo postfix = typeof(GitSubmoduleManagerPackageManagerHost)
                .GetMethod(
                    nameof(AfterSidebarExtensionRowsUpdated),
                    BindingFlags.Static | BindingFlags.NonPublic);
            if (target == null || postfix == null)
                return false;

            if (!HasPostfix(target, postfix))
                harmony.Patch(target, postfix: new HarmonyMethod(postfix));
            return HasPostfix(target, postfix);
        }

        private static void BeforePackageManagerGuiBuilt()
        {
            PackageManagerSubmoduleHarmonyPatch.TryPatch();
            PackageManagerGitHubNativePresentationPatch.TryPatch();
            PackageManagerSubmoduleNativePage.TryRegisterFromServices(out _, out _);
        }

        private static void AfterPackageManagerGuiBuilt(object __instance)
        {
            if (__instance is EditorWindow window)
            {
                EditorApplication.delayCall += () =>
                    TryAttachWindow(window, restartRepairWindow: true);
            }
        }

        private static void AfterSidebarExtensionRowsUpdated(object __instance)
        {
            if (__instance is VisualElement sidebar)
                PackageManagerSubmoduleNativePage.TryRelocateSidebarRow(sidebar, out _);
        }

        private static void Update()
        {
            if (isShuttingDown)
                return;

            double now = EditorApplication.timeSinceStartup;
            if (patchRetryActive)
            {
                if (isPatched || now >= patchRetryDeadline)
                {
                    patchRetryActive = false;
                }
                else if (now >= nextPatchRetryTime)
                {
                    nextPatchRetryTime = now + PatchRetryIntervalSeconds;
                    if (TryPatch(out _))
                        patchRetryActive = false;
                }
            }

            if (now >= nextWindowScanTime)
            {
                nextWindowScanTime = now + WindowScanIntervalSeconds;
                CleanupDeadSessions();
                foreach (HostSession session in Sessions.Values)
                    session.Tick();
                AttachExistingWindows();
            }

            if (openRequested && now > openRequestDeadline)
            {
                openRequested = false;
                Debug.LogWarning(
                    "[Git Submodule Manager] Sources > GitHub could not be opened " +
                    "before Unity's Package Manager activation timed out.");
            }

            UpdateSubscription();
        }

        private static void AttachExistingWindows()
        {
            if (isShuttingDown)
                return;

            Type windowType = GetPackageManagerWindowType();
            if (windowType == null)
                return;

            UnityEngine.Object[] windows = Resources.FindObjectsOfTypeAll(windowType);
            HostSession preferred = null;
            foreach (UnityEngine.Object candidate in windows)
            {
                if (!(candidate is EditorWindow window))
                    continue;

                HostSession session = TryAttachWindow(window);
                if (session == null)
                    continue;

                if (ReferenceEquals(EditorWindow.focusedWindow, window))
                    preferred = session;
                else
                    preferred ??= session;
            }

            if (!openRequested || preferred == null || !preferred.Activate())
            {
                UpdateSubscription();
                return;
            }

            openRequested = false;
            UpdateSubscription();
        }

        private static HostSession TryAttachWindow(
            EditorWindow window,
            bool restartRepairWindow = false)
        {
            if (window == null || isShuttingDown)
                return null;

            if (!Sessions.TryGetValue(window, out HostSession session) ||
                !ReferenceEquals(session.Window, window))
            {
                session?.Dispose();
                session = new HostSession(window);
                Sessions[window] = session;
                SubscribeUpdate();
            }
            else if (restartRepairWindow)
            {
                session.RestartRepairWindow();
            }

            bool attached = session.TryAttachVisualTree();
            UpdateSubscription();
            return attached ? session : null;
        }

        private static void CleanupDeadSessions()
        {
            DeadSessionWindows.Clear();
            foreach (KeyValuePair<EditorWindow, HostSession> pair in Sessions)
            {
                if (pair.Value.Window == null)
                    DeadSessionWindows.Add(pair.Key);
            }

            foreach (EditorWindow window in DeadSessionWindows)
            {
                HostSession session = Sessions[window];
                Sessions.Remove(window);
                session.Dispose();
            }
        }

        private static void TryOpenPackageManagerThroughReflection()
        {
            Type windowType = GetPackageManagerWindowType();
            if (windowType == null)
                return;

            try
            {
                Type publicWindowApi = FindType("UnityEditor.PackageManager.UI.Window");
                MethodInfo open = publicWindowApi?.GetMethod(
                    "Open",
                    BindingFlags.Static | BindingFlags.Public,
                    null,
                    new[] { typeof(string) },
                    null);
                if (open != null)
                {
                    open.Invoke(null, new object[] { null });
                    return;
                }

                const BindingFlags flags = BindingFlags.Static | BindingFlags.Public;
                MethodInfo getWindow = typeof(EditorWindow).GetMethod(
                    nameof(EditorWindow.GetWindow),
                    flags,
                    null,
                    new[] { typeof(Type), typeof(bool), typeof(string), typeof(bool) },
                    null);
                if (getWindow?.Invoke(
                        null,
                        new object[] { windowType, false, "Package Manager", true }) is
                        EditorWindow window)
                {
                    window.Show();
                    window.Focus();
                }
            }
            catch (Exception exception)
            {
                Debug.LogWarning(
                    "[Git Submodule Manager] Unity's Package Manager window " +
                    "could not be opened: " +
                    GitHubUtility.SanitizeUiDiagnostic(exception.Message));
            }
        }

        private static void BeforeEditorQuits()
        {
            isShuttingDown = true;
            UnsubscribeUpdate();
            DisposeAllSessions();
        }

        private static void BeforeAssemblyReload()
        {
            isShuttingDown = true;
            UnsubscribeUpdate();
            DisposeAllSessions();
            try
            {
                harmony?.UnpatchAll(HarmonyId);
            }
            catch
            {
                // Unity is tearing down the AppDomain; fail open.
            }
            finally
            {
                isPatched = false;
            }
        }

        private static void DisposeAllSessions()
        {
            foreach (HostSession session in Sessions.Values)
                session.Dispose();
            Sessions.Clear();
        }

        private static void UpdateSubscription()
        {
            if (!isShuttingDown &&
                (patchRetryActive ||
                 openRequested ||
                 HasSessionRequiringPolling()))
            {
                SubscribeUpdate();
            }
            else
            {
                UnsubscribeUpdate();
            }
        }

        private static bool HasSessionRequiringPolling()
        {
            foreach (HostSession session in Sessions.Values)
            {
                if (session.RequiresPolling)
                    return true;
            }

            return false;
        }

        private static void BeginPatchRetryWindow()
        {
            if (isShuttingDown)
                return;

            double now = EditorApplication.timeSinceStartup;
            patchRetryActive = true;
            patchRetryDeadline = now + PatchRetryWindowSeconds;
            nextPatchRetryTime = now;
            SubscribeUpdate();
        }

        private static void SubscribeUpdate()
        {
            if (updateSubscribed || isShuttingDown)
                return;

            updateSubscribed = true;
            EditorApplication.update += Update;
        }

        private static void UnsubscribeUpdate()
        {
            if (!updateSubscribed)
                return;

            updateSubscribed = false;
            EditorApplication.update -= Update;
        }

        private static void CollectDescendants<T>(VisualElement root, List<T> result)
            where T : VisualElement
        {
            if (root == null)
                return;

            foreach (VisualElement child in root.Children())
            {
                if (child is T match)
                    result.Add(match);
                CollectDescendants(child, result);
            }
        }

        private static VisualElement FindElementByNameRecursive(
            VisualElement root,
            string elementName)
        {
            if (root == null || string.IsNullOrEmpty(elementName))
                return null;
            if (string.Equals(root.name, elementName, StringComparison.Ordinal))
                return root;

            foreach (VisualElement child in root.Children())
            {
                VisualElement match =
                    FindElementByNameRecursive(child, elementName);
                if (match != null)
                    return match;
            }

            return null;
        }

        private static VisualElement FindSidebar(VisualElement root)
        {
            VisualElement named =
                FindElementByNameRecursive(root, SidebarElementName);
            if (string.Equals(
                    named?.GetType().FullName,
                    PackageManagerSubmoduleNativePage.SidebarTypeName,
                    StringComparison.Ordinal))
            {
                return named;
            }

            return FindElementByTypeNameRecursive(
                root,
                PackageManagerSubmoduleNativePage.SidebarTypeName);
        }

        private static VisualElement FindElementByTypeNameRecursive(
            VisualElement root,
            string fullTypeName)
        {
            if (root == null || string.IsNullOrEmpty(fullTypeName))
                return null;
            if (string.Equals(
                    root.GetType().FullName,
                    fullTypeName,
                    StringComparison.Ordinal))
            {
                return root;
            }

            foreach (VisualElement child in root.Children())
            {
                VisualElement match =
                    FindElementByTypeNameRecursive(child, fullTypeName);
                if (match != null)
                    return match;
            }

            return null;
        }

        private static VisualElement FindPackageManagerRoot(VisualElement root)
        {
            return FindElementByTypeNameRecursive(
                root,
                PackageManagerGitHubNativeActions.PackageManagerWindowRootTypeName);
        }

        private sealed class HostSession : IDisposable
        {
            private readonly EventCallback<DetachFromPanelEvent> windowDetached;
            private VisualElement retainedRoot;
            private bool projectionRetained;
            private bool installMenuMounted;
            private bool nativeActionsMounted;
            private bool isDisposed;
            private double repairDeadline;

            internal HostSession(EditorWindow window)
            {
                Window = window;
                repairDeadline = EditorApplication.timeSinceStartup +
                                 SessionRepairTimeoutSeconds;
                windowDetached = OnWindowDetached;
                Window.rootVisualElement.RegisterCallback(windowDetached);
            }

            internal EditorWindow Window { get; }
            internal bool IsAttached =>
                !isDisposed &&
                Window != null &&
                Window.rootVisualElement.panel != null &&
                retainedRoot != null &&
                projectionRetained &&
                installMenuMounted &&
                nativeActionsMounted;
            internal bool RequiresPolling
            {
                get
                {
                    bool windowAlive = Window != null;
                    return ShouldPollHostSession(
                        isDisposed,
                        windowAlive,
                        windowAlive && Window.rootVisualElement.panel != null,
                        IsAttached,
                        EditorApplication.timeSinceStartup < repairDeadline);
                }
            }

            internal void RestartRepairWindow()
            {
                repairDeadline = EditorApplication.timeSinceStartup +
                                 SessionRepairTimeoutSeconds;
                SubscribeUpdate();
            }

            internal bool TryAttachVisualTree()
            {
                if (isDisposed || Window == null)
                    return false;

                // An inactive dock tab can temporarily have no panel while its
                // existing visual tree is still the correct host. Do not probe
                // or release that tree until Unity attaches it again.
                if (Window.rootVisualElement.panel == null)
                    return false;

                VisualElement packageManagerRoot =
                    FindPackageManagerRoot(Window.rootVisualElement);
                if (packageManagerRoot == null)
                    return false;

                if (!ReferenceEquals(retainedRoot, packageManagerRoot))
                {
                    // Retain the replacement first. Releasing the previous root
                    // then cannot look like a final-host teardown or briefly
                    // withdraw the projected GitHub rows.
                    bool projectionTransferred = projectionRetained &&
                        PackageManagerGitHubPackageProjection.RetainHost(
                            packageManagerRoot);
                    ReleaseRoot();
                    retainedRoot = packageManagerRoot;
                    projectionRetained = projectionTransferred;
                }

                if (!PackageManagerSubmoduleNativePage.TryRegisterForRoot(
                        retainedRoot,
                        out _,
                        out _))
                {
                    return false;
                }

                VisualElement sidebar = FindSidebar(Window.rootVisualElement);
                if (sidebar == null ||
                    !PackageManagerSubmoduleNativePage.TryRelocateSidebarRow(
                        sidebar,
                        out _))
                {
                    return false;
                }

                installMenuMounted =
                    PackageManagerGitSubmoduleInstallMenu.InstallForRoot(
                        retainedRoot);
                if (!projectionRetained)
                {
                    projectionRetained =
                        PackageManagerGitHubPackageProjection.RetainHost(retainedRoot);
                }

                nativeActionsMounted = projectionRetained &&
                    PackageManagerGitHubNativeActions.InstallForRoot(retainedRoot);

                return IsAttached;
            }

            internal bool Activate()
            {
                if (!TryAttachVisualTree() ||
                    !PackageManagerSubmoduleNativePage.TryRegisterForRoot(
                        retainedRoot,
                        out object pageManager,
                        out object page) ||
                    !PackageManagerSubmoduleNativePage.TryActivate(pageManager, page))
                {
                    return false;
                }

                Foldout sources = FindSourcesFoldout(Window.rootVisualElement);
                if (sources != null)
                    sources.value = true;
                Window.Show();
                Window.Focus();
                GitSubmoduleManagerWelcomeWindow.OpenIfNeeded();
                return true;
            }

            internal void Tick()
            {
                if (!isDisposed)
                    TryAttachVisualTree();
            }

            public void Dispose()
            {
                if (isDisposed)
                    return;

                isDisposed = true;
                if (Window != null)
                {
                    try
                    {
                        Window.rootVisualElement.UnregisterCallback(windowDetached);
                    }
                    catch
                    {
                        // The visual tree may already be tearing down.
                    }
                }

                ReleaseRoot();
            }

            private void ReleaseRoot()
            {
                VisualElement root = retainedRoot;
                retainedRoot = null;
                if (root == null)
                {
                    projectionRetained = false;
                    installMenuMounted = false;
                    nativeActionsMounted = false;
                    return;
                }

                try
                {
                    PackageManagerGitSubmoduleInstallMenu.ReleaseForRoot(root);
                }
                catch
                {
                    // A stale root must not interrupt projection-host transfer.
                }

                try
                {
                    PackageManagerGitHubNativeActions.ReleaseForRoot(root);
                }
                catch
                {
                    // Native controls may already be tearing down with the panel.
                }

                if (projectionRetained)
                {
                    try
                    {
                        PackageManagerGitHubPackageProjection.ReleaseHost(root);
                    }
                    catch
                    {
                        // Package Manager may already be tearing down.
                    }
                }

                projectionRetained = false;
                installMenuMounted = false;
                nativeActionsMounted = false;
            }

            private void OnWindowDetached(DetachFromPanelEvent evt)
            {
                EditorApplication.delayCall += () =>
                {
                    if (isDisposed)
                        return;

                    bool windowAlive = Window != null;
                    DetachedWindowAction action = GetDetachedWindowAction(
                        windowAlive);
                    if (action == DetachedWindowAction.Release)
                    {
                        CleanupDeadSessions();
                        return;
                    }

                    // Dock/tab switches temporarily detach a live EditorWindow's
                    // panel. Keep its projection host; the low-frequency session
                    // scan observes either reattachment or genuine destruction.
                    if (action == DetachedWindowAction.Repair)
                        RestartRepairWindow();
                };
            }
        }

        private static Type FindType(string fullName)
        {
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type type = assembly.GetType(fullName, false);
                if (type != null)
                    return type;
            }

            return null;
        }
    }
}
