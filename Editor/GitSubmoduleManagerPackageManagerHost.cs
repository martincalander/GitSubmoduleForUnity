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
        internal const string HarmonyId =
            "com.martincalander.gitsubmodulemanager.package-manager-host";
        internal const string PackageManagerWindowTypeName =
            "UnityEditor.PackageManager.UI.PackageManagerWindow";
        internal const string SidebarPageId =
            PackageManagerSubmoduleNativePage.ExtensionPageId;

        private const string SidebarElementName = "sidebar";
        private const double WindowScanIntervalSeconds = 0.5;
        private const double OpenRequestTimeoutSeconds = 10.0;

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
        private static bool isShuttingDown;
        private static bool openRequested;
        private static double openRequestDeadline;
        private static double nextWindowScanTime;

        static GitSubmoduleManagerPackageManagerHost()
        {
            if (!TryPatch(out _))
                EditorApplication.delayCall += RetryPatch;

            EditorApplication.update += Update;
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

            openRequested = true;
            openRequestDeadline =
                EditorApplication.timeSinceStartup + OpenRequestTimeoutSeconds;
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

        internal static void DeactivateAll()
        {
            // There is no custom embedded view to deactivate.
        }

        internal static void RepaintOpenHosts()
        {
            foreach (HostSession session in Sessions.Values)
                session.RequestRepaint();
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

            AttachExistingWindows();
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
            PackageManagerSubmoduleNativePage.TryRegisterFromServices(out _, out _);
        }

        private static void AfterPackageManagerGuiBuilt(object __instance)
        {
            if (__instance is EditorWindow window)
                EditorApplication.delayCall += () => TryAttachWindow(window);
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

            CleanupDeadSessions();
            foreach (HostSession session in Sessions.Values)
                session.Tick();

            double now = EditorApplication.timeSinceStartup;
            if (openRequested || now >= nextWindowScanTime)
            {
                nextWindowScanTime = now + WindowScanIntervalSeconds;
                AttachExistingWindows();
            }

            if (openRequested && now > openRequestDeadline)
            {
                openRequested = false;
                Debug.LogWarning(
                    "[Git Submodule Manager] Sources > GitHub could not be opened " +
                    "before Unity's Package Manager activation timed out.");
            }
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
                return;

            openRequested = false;
        }

        private static HostSession TryAttachWindow(EditorWindow window)
        {
            if (window == null || isShuttingDown)
                return null;

            if (!Sessions.TryGetValue(window, out HostSession session) ||
                !ReferenceEquals(session.Window, window))
            {
                session?.Dispose();
                session = new HostSession(window);
                Sessions[window] = session;
            }

            return session.TryAttachVisualTree() ? session : null;
        }

        private static void ReleaseWindow(EditorWindow window)
        {
            if (window == null || !Sessions.TryGetValue(window, out HostSession session))
                return;

            Sessions.Remove(window);
            session.Dispose();
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
            DisposeAllSessions();
        }

        private static void BeforeAssemblyReload()
        {
            isShuttingDown = true;
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
            private bool isDisposed;

            internal HostSession(EditorWindow window)
            {
                Window = window;
                windowDetached = OnWindowDetached;
                Window.rootVisualElement.RegisterCallback(windowDetached);
            }

            internal EditorWindow Window { get; }

            internal bool TryAttachVisualTree()
            {
                if (isDisposed || Window == null)
                    return false;

                VisualElement packageManagerRoot =
                    FindPackageManagerRoot(Window.rootVisualElement);
                if (packageManagerRoot == null)
                {
                    ReleaseRoot();
                    return false;
                }

                if (!ReferenceEquals(retainedRoot, packageManagerRoot))
                {
                    ReleaseRoot();
                    retainedRoot = packageManagerRoot;
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

                PackageManagerGitSubmoduleInstallMenu.InstallForRoot(retainedRoot);
                if (!projectionRetained)
                {
                    projectionRetained =
                        PackageManagerGitHubPackageProjection.RetainHost(retainedRoot);
                }

                if (projectionRetained)
                    PackageManagerGitHubNativeActions.InstallForRoot(retainedRoot);

                return projectionRetained;
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

            internal void RequestRepaint()
            {
                if (!isDisposed && Window != null)
                    Window.Repaint();
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
                    return;
                }

                PackageManagerGitSubmoduleInstallMenu.ReleaseForRoot(root);
                PackageManagerGitHubNativeActions.ReleaseForRoot(root);
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
            }

            private void OnWindowDetached(DetachFromPanelEvent evt)
            {
                EditorApplication.delayCall += () =>
                {
                    if (Window == null || Window.rootVisualElement.panel == null)
                        ReleaseWindow(Window);
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
