using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace MartinCalander.GitSubmoduleManager.Editor
{
    [InitializeOnLoad]
    internal static class GitSubmoduleManagerPackageManagerHost
    {
        internal const string HarmonyId =
            "com.martincalander.gitsubmodulemanager.package-manager-host";
        internal const string PackageManagerWindowTypeName =
            "UnityEditor.PackageManager.UI.PackageManagerWindow";
        internal const string ContentElementName =
            "git-submodule-manager-content";
        internal const string SidebarRowElementName =
            "git-submodule-manager-sidebar-row";
        internal const string SidebarPageId =
            "GitSubmoduleManager.GitHub";

        private const string ActiveSessionKey =
            "MartinCalander.GitSubmoduleManager.PackageManagerHost.Active";
        private const string MainContainerElementName = "mainContainer";
        private const string MainContainerSplitterElementName = "mainContainerSplitter";
        private const string MainContainerOverlayElementName = "mainContainerOverlay";
        private const string ToolbarElementName = "topMenuToolbar";
        private const string SidebarElementName = "sidebar";
        private const string SidebarRowTypeName =
            "UnityEditor.PackageManager.UI.Internal.SidebarRow";
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
        private static bool welcomeRequested;
        private static bool restoreActiveRequested;
        private static double openRequestDeadline;
        private static double nextWindowScanTime;

        static GitSubmoduleManagerPackageManagerHost()
        {
            restoreActiveRequested = SessionState.GetBool(ActiveSessionKey, false);
            if (!TryPatch(out _))
                EditorApplication.delayCall += RetryPatch;

            EditorApplication.update += Update;
            EditorApplication.delayCall += AttachExistingWindows;
            AssemblyReloadEvents.beforeAssemblyReload += BeforeAssemblyReload;
            EditorApplication.quitting += BeforeEditorQuits;
        }

        internal static void Open(bool openWelcome = false)
        {
            if (isShuttingDown)
                return;

            openRequested = true;
            welcomeRequested |= openWelcome;
            openRequestDeadline = EditorApplication.timeSinceStartup + OpenRequestTimeoutSeconds;

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

                List<MethodInfo> lifecycleMethods = GetSupportedLifecycleMethods(windowType);
                if (lifecycleMethods.Count == 0)
                {
                    error = $"Could not find BuildGUI or CreateGUI on {PackageManagerWindowTypeName}.";
                    return false;
                }

                MethodInfo postfix = typeof(GitSubmoduleManagerPackageManagerHost).GetMethod(
                    nameof(AfterPackageManagerGuiBuilt),
                    BindingFlags.Static | BindingFlags.NonPublic);
                MethodInfo prefix = typeof(GitSubmoduleManagerPackageManagerHost).GetMethod(
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
                    {
                        harmony.Patch(
                            lifecycleMethod,
                            prefix: new HarmonyMethod(prefix));
                    }
                    if (!HasPostfix(lifecycleMethod, postfix))
                    {
                        harmony.Patch(
                            lifecycleMethod,
                            postfix: new HarmonyMethod(postfix));
                    }
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
                    error = "Harmony did not register every supported Package Manager host hook.";
                return isPatched;
            }
            catch (Exception exception)
            {
                isPatched = false;
                error = exception.Message;
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
                if (getRow?.Invoke(sidebar, new object[] { "MyAssets" }) is VisualElement row &&
                    row.parent is Foldout sources)
                {
                    return sources;
                }
            }
            catch
            {
                // Fall through to the visual-tree lookup within the verified sidebar.
            }

            var foldouts = new List<Foldout>();
            CollectDescendants(sidebar, foldouts);
            string localizedSources = L10n.Tr("Sources");
            foreach (Foldout foldout in foldouts)
            {
                if (string.Equals(foldout.text, localizedSources, StringComparison.Ordinal) ||
                    string.Equals(foldout.text, "Sources", StringComparison.OrdinalIgnoreCase))
                {
                    return foldout;
                }
            }

            // Unity 6 currently creates Project, Sources, Cloud, and registry
            // foldouts in that order. The index fallback keeps the integration
            // usable when the Editor localizes the label differently.
            return foldouts.Count > 1 ? foldouts[1] : null;
        }

        internal static bool IsDescendantOrSelf(
            VisualElement ancestor,
            VisualElement candidate)
        {
            if (ancestor == null || candidate == null)
                return false;

            for (VisualElement current = candidate; current != null; current = current.parent)
            {
                if (ReferenceEquals(current, ancestor))
                    return true;
            }

            return false;
        }

        internal static void DeactivateAll()
        {
            foreach (HostSession session in Sessions.Values)
                session.Deactivate();
            SessionState.SetBool(ActiveSessionKey, false);
        }

        internal static void RepaintOpenHosts()
        {
            foreach (HostSession session in Sessions.Values)
                session.RequestRepaint();
        }

        private static void RetryPatch()
        {
            if (!TryPatch(out string error))
            {
                Debug.LogWarning(
                    "[Git Submodule Manager] The embedded Package Manager host " +
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
            Type sidebarType = FindType(
                PackageManagerSubmoduleNativePage.SidebarTypeName);
            MethodInfo target = PackageManagerSubmoduleNativePage
                .FindSidebarExtensionRowsUpdateMethod(sidebarType);
            MethodInfo postfix = typeof(GitSubmoduleManagerPackageManagerHost).GetMethod(
                nameof(AfterSidebarExtensionRowsUpdated),
                BindingFlags.Static | BindingFlags.NonPublic);
            if (target == null || postfix == null)
                return false;

            if (!HasPostfix(target, postfix))
                harmony.Patch(target, postfix: new HarmonyMethod(postfix));
            return HasPostfix(target, postfix);
        }

        internal static bool IsSidebarExtensionRefreshPatchApplied()
        {
            Type sidebarType = FindType(
                PackageManagerSubmoduleNativePage.SidebarTypeName);
            MethodInfo target = PackageManagerSubmoduleNativePage
                .FindSidebarExtensionRowsUpdateMethod(sidebarType);
            MethodInfo postfix = typeof(GitSubmoduleManagerPackageManagerHost).GetMethod(
                nameof(AfterSidebarExtensionRowsUpdated),
                BindingFlags.Static | BindingFlags.NonPublic);
            return target != null && postfix != null && HasPostfix(target, postfix);
        }

        /// <summary>
        /// Exposes a side-effect-free compatibility diagnostic for the EditMode
        /// suite. Keeping the ownership check beside the patch implementation
        /// makes failures identify Unity contract drift instead of merely proving
        /// that a similarly named lifecycle method still exists.
        /// </summary>
        internal static bool AreLifecyclePatchesApplied()
        {
            Type windowType = GetPackageManagerWindowType();
            List<MethodInfo> lifecycleMethods =
                GetSupportedLifecycleMethods(windowType);
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

        private static void BeforePackageManagerGuiBuilt()
        {
            PackageManagerSubmoduleNativePage.TryRegisterFromServices(
                out _,
                out _);
        }

        private static void AfterPackageManagerGuiBuilt(object __instance)
        {
            if (!(__instance is EditorWindow window))
                return;

            EditorApplication.delayCall += () => TryAttachWindow(window);
        }

        private static void AfterSidebarExtensionRowsUpdated(object __instance)
        {
            if (__instance is VisualElement sidebar)
            {
                PackageManagerSubmoduleNativePage.TryRelocateSidebarRow(
                    sidebar,
                    out _);
            }
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
                welcomeRequested = false;
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

            bool shouldActivate = openRequested || restoreActiveRequested;
            if (!shouldActivate || preferred == null)
                return;

            bool showWelcome = openRequested && welcomeRequested;
            if (!preferred.Activate(showWelcome))
                return;

            openRequested = false;
            welcomeRequested = false;
            restoreActiveRequested = false;
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
            if (window == null)
                return;

            if (!Sessions.TryGetValue(window, out HostSession session))
                return;

            bool removedActiveSession = session.IsActive;
            Sessions.Remove(window);
            session.Dispose();
            if (removedActiveSession)
                PersistAnyActiveSessionState();
        }

        private static void CleanupDeadSessions()
        {
            DeadSessionWindows.Clear();
            foreach (KeyValuePair<EditorWindow, HostSession> pair in Sessions)
            {
                if (pair.Value.Window == null)
                    DeadSessionWindows.Add(pair.Key);
            }

            bool removedActiveSession = false;
            foreach (EditorWindow window in DeadSessionWindows)
            {
                HostSession session = Sessions[window];
                removedActiveSession |= session.IsActive;
                Sessions.Remove(window);
                session.Dispose();
            }

            if (removedActiveSession)
                PersistAnyActiveSessionState();
        }

        private static void PersistAnyActiveSessionState()
        {
            foreach (HostSession session in Sessions.Values)
            {
                if (session.IsActive)
                {
                    SessionState.SetBool(ActiveSessionKey, true);
                    return;
                }
            }

            SessionState.SetBool(ActiveSessionKey, false);
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
                        new object[] { windowType, false, "Package Manager", true }) is EditorWindow window)
                {
                    window.Show();
                    window.Focus();
                }
            }
            catch (Exception exception)
            {
                Debug.LogWarning(
                    "[Git Submodule Manager] Unity's Package Manager window " +
                    "could not be opened: " + exception.Message);
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
                VisualElement match = FindElementByNameRecursive(child, elementName);
                if (match != null)
                    return match;
            }

            return null;
        }

        private static VisualElement FindSidebar(VisualElement root)
        {
            VisualElement named = FindElementByNameRecursive(root, SidebarElementName);
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
            if (string.Equals(root.GetType().FullName, fullTypeName, StringComparison.Ordinal))
                return root;

            foreach (VisualElement child in root.Children())
            {
                VisualElement match = FindElementByTypeNameRecursive(child, fullTypeName);
                if (match != null)
                    return match;
            }

            return null;
        }

        private sealed class HostSession : IDisposable
        {
            private readonly EventCallback<DetachFromPanelEvent> windowDetached;
            private readonly EventCallback<PointerDownEvent> sidebarPointerDown;
            private readonly EventCallback<PointerDownEvent> customRowPointerDown;
            private readonly EventCallback<KeyDownEvent> customRowKeyDown;
            private readonly List<VisualElement> selectedBuiltInRowsAtActivation = new();

            private VisualElement attachedContentParent;
            private VisualElement sidebar;
            private VisualElement mainContainer;
            private VisualElement legacyMainContainer;
            private VisualElement mainContainerSplitter;
            private VisualElement mainContainerOverlay;
            private VisualElement toolbar;
            private VisualElement customContent;
            private VisualElement customRow;
            private VisualElement nativeRow;
            private VisualElement retainedInstallMenuRoot;
            private VisualElement retainedNativePackageManagerRoot;
            private VisualElement legacyHeader;
            private IMGUIContainer imguiContainer;
            private GitSubmoduleManagerView view;
            private StyleEnum<DisplayStyle> previousSplitterDisplay;
            private StyleEnum<DisplayStyle> previousLegacyMainDisplay;
            private StyleEnum<DisplayStyle> previousOverlayDisplay;
            private StyleEnum<DisplayStyle> previousToolbarDisplay;
            private Vector2 previousMinimumSize;
            private bool displayStateCaptured;
            private bool isActive;
            private bool isDisposed;

            internal HostSession(EditorWindow window)
            {
                Window = window;
                windowDetached = OnWindowDetached;
                sidebarPointerDown = OnSidebarPointerDown;
                customRowPointerDown = OnCustomRowPointerDown;
                customRowKeyDown = OnCustomRowKeyDown;
                Window.rootVisualElement.RegisterCallback(windowDetached);
            }

            internal EditorWindow Window { get; }
            internal bool IsActive => isActive && !isDisposed;

            internal bool TryAttachVisualTree()
            {
                if (isDisposed || Window == null)
                    return false;

                VisualElement windowRoot = Window.rootVisualElement;
                if (windowRoot == null || windowRoot.childCount == 0)
                    return false;

                RetainInstallMenuRoot(FindPackageManagerRoot(windowRoot));

                VisualElement nextMainContainer =
                    FindElementByName(windowRoot, MainContainerElementName);
                VisualElement nextMainContainerSplitter =
                    FindElementByName(windowRoot, MainContainerSplitterElementName);
                bool hasModernMainContainer =
                    nextMainContainer != null && nextMainContainerSplitter != null;
                VisualElement nextContentParent = hasModernMainContainer
                    ? nextMainContainer
                    : (nextMainContainer?.parent ??
                       FindPackageManagerRoot(windowRoot) ??
                       windowRoot);

                bool contentIsCurrent =
                    customContent != null &&
                    ReferenceEquals(customContent.parent, nextContentParent);
                if (!contentIsCurrent)
                {
                    // Restore the old tree before replacing cached elements so
                    // the new splitter/toolbar state is captured independently.
                    RestorePackageManagerPresentation();
                    RebuildContentHost(nextContentParent, hasModernMainContainer);
                }

                mainContainer = hasModernMainContainer ? nextMainContainer : null;
                legacyMainContainer = hasModernMainContainer ? null : nextMainContainer;
                mainContainerSplitter = nextMainContainerSplitter;
                mainContainerOverlay =
                    FindElementByName(windowRoot, MainContainerOverlayElementName);
                toolbar = FindElementByName(windowRoot, ToolbarElementName);
                VisualElement previousSidebar = sidebar;
                bool nativeContractAvailable =
                    PackageManagerSubmoduleNativePage.IsSupportedContract();
                bool sidebarReady = nativeContractAvailable &&
                                    InstallNativeSidebarRow(windowRoot);
                if (!sidebarReady)
                    sidebarReady = InstallSidebarRow(windowRoot);

                if (isActive &&
                    (!contentIsCurrent || !ReferenceEquals(previousSidebar, sidebar)))
                {
                    CaptureSelectedBuiltInRows();
                    SetCapturedBuiltInRowsSelected(false);
                }

                if (isActive)
                    ApplyActivePresentation();
                return customContent != null && sidebarReady;
            }

            internal bool Activate(bool openWelcome)
            {
                if (isDisposed || !TryAttachVisualTree())
                    return false;

                EnsureView(openWelcome);
                if (view == null)
                    return false;

                if (!isActive)
                {
                    CaptureSelectedBuiltInRows();
                    SetCapturedBuiltInRowsSelected(false);
                }
                isActive = true;
                if (customRow?.parent is Foldout sources)
                    sources.value = true;
                ApplyActivePresentation();
                SetCustomRowSelected(true);
                SessionState.SetBool(ActiveSessionKey, true);
                Window.Focus();
                RequestRepaint();
                return true;
            }

            internal void Deactivate()
            {
                Deactivate(restoreCapturedSelection: true);
            }

            private void Deactivate(bool restoreCapturedSelection)
            {
                if (isDisposed)
                    return;

                isActive = false;
                RestorePackageManagerPresentation();
                SetCustomRowSelected(false);
                if (restoreCapturedSelection)
                    SetCapturedBuiltInRowsSelected(true);
                selectedBuiltInRowsAtActivation.Clear();
                PersistAnyActiveSessionState();
                RequestRepaint();
            }

            internal void Tick()
            {
                if (isDisposed || view == null || !view.IsAttached)
                    return;

                if (isActive && HasNewlySelectedBuiltInSidebarRow())
                    Deactivate(restoreCapturedSelection: false);

                view.Tick();
            }

            internal void RequestRepaint()
            {
                if (isDisposed)
                    return;

                imguiContainer?.MarkDirtyRepaint();
                Window?.Repaint();
            }

            public void Dispose()
            {
                if (isDisposed)
                    return;

                if (isActive)
                {
                    SetCustomRowSelected(false);
                    SetCapturedBuiltInRowsSelected(true);
                    isActive = false;
                }
                selectedBuiltInRowsAtActivation.Clear();
                isDisposed = true;
                ReleaseInstallMenuRoot();
                ReleaseRetainedNativePackageManagerRoot();
                RestorePackageManagerPresentation();
                if (Window != null)
                    Window.rootVisualElement.UnregisterCallback(windowDetached);
                if (sidebar != null)
                    sidebar.UnregisterCallback(sidebarPointerDown, TrickleDown.TrickleDown);
                if (customRow != null)
                {
                    customRow.UnregisterCallback(customRowPointerDown, TrickleDown.TrickleDown);
                    customRow.UnregisterCallback(customRowKeyDown);
                    customRow.RemoveFromHierarchy();
                }

                customContent?.RemoveFromHierarchy();
                DisposeView();
                customContent = null;
                customRow = null;
                nativeRow = null;
                legacyHeader = null;
                imguiContainer = null;
                sidebar = null;
                attachedContentParent = null;
            }

            private void RebuildContentHost(
                VisualElement contentParent,
                bool isInsideMainContainer)
            {
                customContent?.RemoveFromHierarchy();
                attachedContentParent = contentParent;
                customContent = new VisualElement
                {
                    name = ContentElementName,
                    pickingMode = PickingMode.Position
                };
                customContent.style.display = DisplayStyle.None;
                customContent.style.flexGrow = 1f;
                customContent.style.flexShrink = 1f;
                customContent.style.backgroundColor = new StyleColor(
                    EditorGUIUtility.isProSkin
                        ? (Color)new Color32(56, 56, 56, 255)
                        : new Color32(194, 194, 194, 255));

                if (!isInsideMainContainer)
                {
                    customContent.style.position = Position.Absolute;
                    customContent.style.left = 0f;
                    customContent.style.right = 0f;
                    customContent.style.top = 0f;
                    customContent.style.bottom = 0f;

                    legacyHeader = new VisualElement
                    {
                        name = "git-submodule-manager-legacy-header"
                    };
                    legacyHeader.style.flexDirection = FlexDirection.Row;
                    legacyHeader.style.flexShrink = 0f;
                    legacyHeader.style.paddingLeft = 6f;
                    legacyHeader.style.paddingRight = 6f;
                    legacyHeader.style.paddingTop = 4f;
                    legacyHeader.style.paddingBottom = 4f;
                    legacyHeader.style.borderBottomWidth = 1f;
                    legacyHeader.style.borderBottomColor = new StyleColor(
                        EditorGUIUtility.isProSkin
                            ? (Color)new Color32(35, 35, 35, 255)
                            : new Color32(140, 140, 140, 255));
                    var backButton = new Button(Deactivate)
                    {
                        name = "git-submodule-manager-back-button",
                        text = "Back to Package Manager",
                        tooltip = "Return to Unity's normal Package Manager page"
                    };
                    legacyHeader.Add(backButton);
                    customContent.Add(legacyHeader);
                }
                else
                {
                    legacyHeader = null;
                }

                if (imguiContainer == null)
                {
                    imguiContainer = new IMGUIContainer(DrawView)
                    {
                        name = "git-submodule-manager-imgui",
                        focusable = true
                    };
                    imguiContainer.style.flexGrow = 1f;
                    imguiContainer.style.flexShrink = 1f;
                }
                else
                {
                    imguiContainer.RemoveFromHierarchy();
                }

                customContent.Add(imguiContainer);
                contentParent.Add(customContent);
            }

            private void RetainInstallMenuRoot(VisualElement packageManagerRoot)
            {
                if (packageManagerRoot == null)
                {
                    ReleaseInstallMenuRoot();
                    return;
                }

                if (ReferenceEquals(retainedInstallMenuRoot, packageManagerRoot))
                {
                    PackageManagerGitSubmoduleInstallMenu.InstallForRoot(
                        packageManagerRoot);
                    return;
                }

                VisualElement previousRoot = retainedInstallMenuRoot;
                retainedInstallMenuRoot = null;
                if (previousRoot != null)
                {
                    PackageManagerGitSubmoduleInstallMenu.ReleaseForRoot(
                        previousRoot);
                }

                if (PackageManagerGitSubmoduleInstallMenu.InstallForRoot(
                        packageManagerRoot))
                {
                    retainedInstallMenuRoot = packageManagerRoot;
                }
            }

            private void ReleaseInstallMenuRoot()
            {
                VisualElement packageManagerRoot = retainedInstallMenuRoot;
                retainedInstallMenuRoot = null;
                if (packageManagerRoot != null)
                {
                    PackageManagerGitSubmoduleInstallMenu.ReleaseForRoot(
                        packageManagerRoot);
                }
            }

            private bool InstallNativeSidebarRow(VisualElement windowRoot)
            {
                VisualElement packageManagerRoot = FindPackageManagerRoot(windowRoot);
                if (!PackageManagerSubmoduleNativePage.TryRegisterForRoot(
                        packageManagerRoot,
                        out _,
                        out _))
                {
                    return false;
                }

                VisualElement nextSidebar = FindSidebar(windowRoot);
                if (nextSidebar == null)
                    return false;

                UpdateSidebarReference(nextSidebar);
                if (!PackageManagerSubmoduleNativePage.TryRelocateSidebarRow(
                        sidebar,
                        out nativeRow))
                {
                    return false;
                }

                if (!RetainNativePackageManagerRoot(packageManagerRoot))
                    return false;

                // Keep the native page available as a read-only discovery view
                // if a future Editor changes only the optional action contract.
                PackageManagerGitHubNativeActions.InstallForRoot(
                    packageManagerRoot);

                if (customRow != null)
                {
                    customRow.UnregisterCallback(
                        customRowPointerDown,
                        TrickleDown.TrickleDown);
                    customRow.UnregisterCallback(customRowKeyDown);
                    customRow.RemoveFromHierarchy();
                    customRow = null;
                }

                return true;
            }

            private bool RetainNativePackageManagerRoot(VisualElement packageManagerRoot)
            {
                if (packageManagerRoot == null)
                    return false;
                if (ReferenceEquals(
                        retainedNativePackageManagerRoot,
                        packageManagerRoot))
                {
                    return true;
                }

                try
                {
                    if (!PackageManagerGitHubPackageProjection.RetainHost(
                            packageManagerRoot))
                    {
                        PackageManagerGitHubPackageProjection.ReleaseHost(
                            packageManagerRoot);
                        return false;
                    }
                }
                catch
                {
                    try
                    {
                        PackageManagerGitHubPackageProjection.ReleaseHost(
                            packageManagerRoot);
                    }
                    catch
                    {
                        // Best-effort rollback of a partially retained host.
                    }

                    return false;
                }

                VisualElement previousRoot = retainedNativePackageManagerRoot;
                retainedNativePackageManagerRoot = packageManagerRoot;
                if (previousRoot != null)
                {
                    PackageManagerGitHubNativeActions.ReleaseForRoot(previousRoot);
                    try
                    {
                        PackageManagerGitHubPackageProjection.ReleaseHost(
                            previousRoot);
                    }
                    catch
                    {
                        // Keep the newly retained native host operational.
                    }
                }

                return true;
            }

            private void ReleaseRetainedNativePackageManagerRoot()
            {
                VisualElement packageManagerRoot = retainedNativePackageManagerRoot;
                retainedNativePackageManagerRoot = null;
                if (packageManagerRoot == null)
                    return;

                PackageManagerGitHubNativeActions.ReleaseForRoot(packageManagerRoot);
                try
                {
                    PackageManagerGitHubPackageProjection.ReleaseHost(
                        packageManagerRoot);
                }
                catch
                {
                    // Package Manager may already be tearing down.
                }
            }

            private bool InstallSidebarRow(VisualElement windowRoot)
            {
                nativeRow = null;
                Foldout sources = FindSourcesFoldout(windowRoot);
                if (sources == null)
                    return true;

                VisualElement nextSidebar = FindSidebar(windowRoot) ?? sources.parent;
                UpdateSidebarReference(nextSidebar);

                if (customRow != null && ReferenceEquals(customRow.parent, sources))
                {
                    ApplySidebarIcon(customRow);
                    return true;
                }

                if (customRow != null)
                {
                    customRow.UnregisterCallback(
                        customRowPointerDown,
                        TrickleDown.TrickleDown);
                    customRow.UnregisterCallback(customRowKeyDown);
                    customRow.RemoveFromHierarchy();
                }

                customRow = CreateSidebarRow() ?? CreateFallbackSidebarRow();
                customRow.name = SidebarRowElementName;
                customRow.tooltip = "Browse and manage Git submodule packages";
                customRow.focusable = true;
                customRow.RegisterCallback(
                    customRowPointerDown,
                    TrickleDown.TrickleDown);
                customRow.RegisterCallback(customRowKeyDown);
                ApplySidebarIcon(customRow);
                sources.Add(customRow);
                SetCustomRowSelected(isActive);
                return true;
            }

            private void UpdateSidebarReference(VisualElement nextSidebar)
            {
                if (ReferenceEquals(sidebar, nextSidebar))
                    return;

                if (sidebar != null)
                {
                    sidebar.UnregisterCallback(
                        sidebarPointerDown,
                        TrickleDown.TrickleDown);
                }

                sidebar = nextSidebar;
                sidebar?.RegisterCallback(
                    sidebarPointerDown,
                    TrickleDown.TrickleDown);
            }

            private static VisualElement CreateSidebarRow()
            {
                Type rowType = FindType("UnityEditor.PackageManager.UI.Internal.SidebarRow");
                if (rowType == null || !typeof(VisualElement).IsAssignableFrom(rowType))
                    return null;

                const BindingFlags flags =
                    BindingFlags.Instance |
                    BindingFlags.Public |
                    BindingFlags.NonPublic;
                foreach (ConstructorInfo constructor in rowType.GetConstructors(flags))
                {
                    ParameterInfo[] parameters = constructor.GetParameters();
                    if (parameters.Length != 3 ||
                        parameters[0].ParameterType != typeof(string) ||
                        parameters[1].ParameterType != typeof(string) ||
                        !parameters[2].ParameterType.IsEnum)
                    {
                        continue;
                    }

                    try
                    {
                        object iconNone = Enum.IsDefined(parameters[2].ParameterType, "None")
                            ? Enum.Parse(parameters[2].ParameterType, "None")
                            : Activator.CreateInstance(parameters[2].ParameterType);
                        return constructor.Invoke(
                            new[] { (object)SidebarPageId, "GitHub", iconNone }) as VisualElement;
                    }
                    catch
                    {
                        // Try another compatible constructor before falling back.
                    }
                }

                return null;
            }

            private static VisualElement CreateFallbackSidebarRow()
            {
                var row = new VisualElement();
                row.AddToClassList("sidebarRow");
                var icon = new VisualElement { name = "sidebarIcon" };
                icon.AddToClassList("sidebarIcon");
                row.Add(icon);
                var title = new Label("GitHub");
                title.AddToClassList("sidebarTitle");
                row.Add(title);
                return row;
            }

            private static void ApplySidebarIcon(VisualElement row)
            {
                if (row == null)
                    return;

                VisualElement iconElement = FindElementByName(row, "sidebarIcon") ??
                    FindElementByClass(row, "sidebarIcon");
                Texture2D icon = GitSubmoduleManagerIcons.GitIcon;
                if (iconElement != null && icon != null)
                    iconElement.style.backgroundImage = new StyleBackground(icon);
            }

            private void ApplyActivePresentation()
            {
                if (!displayStateCaptured)
                {
                    if (mainContainerSplitter != null)
                        previousSplitterDisplay = mainContainerSplitter.style.display;
                    if (legacyMainContainer != null)
                        previousLegacyMainDisplay = legacyMainContainer.style.display;
                    if (mainContainerOverlay != null)
                        previousOverlayDisplay = mainContainerOverlay.style.display;
                    if (toolbar != null)
                        previousToolbarDisplay = toolbar.style.display;
                    if (Window != null)
                        previousMinimumSize = Window.minSize;
                    displayStateCaptured = true;
                }

                if (mainContainerSplitter != null)
                    mainContainerSplitter.style.display = DisplayStyle.None;
                if (legacyMainContainer != null)
                    legacyMainContainer.style.display = DisplayStyle.None;
                if (mainContainerOverlay != null)
                    mainContainerOverlay.style.display = DisplayStyle.None;
                if (toolbar != null)
                    toolbar.style.display = DisplayStyle.None;
                if (customContent != null)
                    customContent.style.display = DisplayStyle.Flex;
                if (Window != null && view != null)
                {
                    Vector2 requested = view.MinimumSize;
                    Window.minSize = new Vector2(
                        Mathf.Max(previousMinimumSize.x, requested.x),
                        Mathf.Max(previousMinimumSize.y, requested.y));
                }
            }

            private void RestorePackageManagerPresentation()
            {
                if (customContent != null)
                    customContent.style.display = DisplayStyle.None;
                if (!displayStateCaptured)
                    return;

                if (mainContainerSplitter != null)
                    mainContainerSplitter.style.display = previousSplitterDisplay;
                if (legacyMainContainer != null)
                    legacyMainContainer.style.display = previousLegacyMainDisplay;
                if (mainContainerOverlay != null)
                    mainContainerOverlay.style.display = previousOverlayDisplay;
                if (toolbar != null)
                    toolbar.style.display = previousToolbarDisplay;
                if (Window != null)
                    Window.minSize = previousMinimumSize;
                displayStateCaptured = false;
            }

            private void EnsureView(bool openWelcome)
            {
                if (view == null)
                {
                    view = ScriptableObject.CreateInstance<GitSubmoduleManagerView>();
                    view.hideFlags = HideFlags.HideAndDontSave;
                }

                view.AttachToHost(
                    RequestRepaint,
                    CloseEmbeddedView,
                    GetContentRect(),
                    openWelcome);
            }

            private void DisposeView()
            {
                if (view == null)
                    return;

                view.DetachFromHost();
                UnityEngine.Object.DestroyImmediate(view);
                view = null;
            }

            private void DrawView()
            {
                if (!isActive || view == null)
                    return;

                view.Render(GetContentRect());
            }

            private Rect GetContentRect()
            {
                float width = imguiContainer?.contentRect.width ?? 0f;
                float height = imguiContainer?.contentRect.height ?? 0f;
                if (!(width > 0f) || float.IsNaN(width))
                    width = Mathf.Max(720f, Window?.position.width ?? 720f);
                if (!(height > 0f) || float.IsNaN(height))
                    height = Mathf.Max(420f, Window?.position.height ?? 420f);
                return new Rect(0f, 0f, width, height);
            }

            private void CloseEmbeddedView()
            {
                Deactivate();
                DisposeView();
            }

            private void SetCustomRowSelected(bool selected)
            {
                if (customRow == null)
                    return;

                SetSidebarRowSelected(customRow, selected);
            }

            private static void SetSidebarRowSelected(
                VisualElement row,
                bool selected)
            {
                if (row == null)
                    return;

                MethodInfo setSelected = row.GetType().GetMethod(
                    "SetSelected",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    null,
                    new[] { typeof(bool) },
                    null);
                if (setSelected != null)
                {
                    try
                    {
                        setSelected.Invoke(row, new object[] { selected });
                        return;
                    }
                    catch
                    {
                        // The CSS fallback below remains safe.
                    }
                }

                row.EnableInClassList("selected", selected);
            }

            private void CaptureSelectedBuiltInRows()
            {
                selectedBuiltInRowsAtActivation.Clear();
                if (sidebar == null)
                    return;

                var elements = new List<VisualElement>();
                CollectDescendants(sidebar, elements);
                foreach (VisualElement element in elements)
                {
                    if (IsSelectedBuiltInSidebarRow(element))
                    {
                        selectedBuiltInRowsAtActivation.Add(element);
                    }
                }
            }

            private bool HasNewlySelectedBuiltInSidebarRow()
            {
                if (sidebar == null)
                    return false;

                var elements = new List<VisualElement>();
                CollectDescendants(sidebar, elements);
                foreach (VisualElement element in elements)
                {
                    if (IsSelectedBuiltInSidebarRow(element) &&
                        !WasSelectedAtActivation(element))
                    {
                        return true;
                    }
                }

                return false;
            }

            private void SetCapturedBuiltInRowsSelected(bool selected)
            {
                foreach (VisualElement row in selectedBuiltInRowsAtActivation)
                    SetSidebarRowSelected(row, selected);
            }

            private bool IsSelectedBuiltInSidebarRow(VisualElement element)
            {
                return element != null &&
                       !ReferenceEquals(element, customRow) &&
                       string.Equals(
                           element.GetType().FullName,
                           SidebarRowTypeName,
                           StringComparison.Ordinal) &&
                       element.ClassListContains("selected");
            }

            private bool WasSelectedAtActivation(VisualElement element)
            {
                foreach (VisualElement selected in selectedBuiltInRowsAtActivation)
                {
                    if (ReferenceEquals(selected, element))
                        return true;
                }

                return false;
            }

            private void OnCustomRowPointerDown(PointerDownEvent evt)
            {
                if (evt.button != 0)
                    return;

                Activate(openWelcome: false);
                evt.StopImmediatePropagation();
            }

            private void OnCustomRowKeyDown(KeyDownEvent evt)
            {
                if (evt.keyCode != KeyCode.Return &&
                    evt.keyCode != KeyCode.KeypadEnter &&
                    evt.keyCode != KeyCode.Space)
                {
                    return;
                }

                Activate(openWelcome: false);
                evt.StopImmediatePropagation();
            }

            private void OnSidebarPointerDown(PointerDownEvent evt)
            {
                if (!isActive ||
                    IsDescendantOrSelf(customRow, evt.target as VisualElement))
                {
                    return;
                }

                Deactivate();
            }

            private void OnWindowDetached(DetachFromPanelEvent evt)
            {
                EditorApplication.delayCall += () =>
                {
                    if (Window == null || Window.rootVisualElement.panel == null)
                        ReleaseWindow(Window);
                };
            }

            private static VisualElement FindPackageManagerRoot(VisualElement root)
            {
                if (root == null)
                    return null;
                if (string.Equals(
                        root.GetType().FullName,
                        "UnityEditor.PackageManager.UI.Internal.PackageManagerWindowRoot",
                        StringComparison.Ordinal))
                {
                    return root;
                }

                foreach (VisualElement child in root.Children())
                {
                    VisualElement match = FindPackageManagerRoot(child);
                    if (match != null)
                        return match;
                }

                return null;
            }

            private static VisualElement FindElementByName(
                VisualElement root,
                string elementName)
            {
                if (root == null || string.IsNullOrEmpty(elementName))
                    return null;
                if (string.Equals(root.name, elementName, StringComparison.Ordinal))
                    return root;

                foreach (VisualElement child in root.Children())
                {
                    VisualElement match = FindElementByName(child, elementName);
                    if (match != null)
                        return match;
                }

                return null;
            }

            private static VisualElement FindElementByClass(
                VisualElement root,
                string className)
            {
                if (root == null || string.IsNullOrEmpty(className))
                    return null;
                if (root.ClassListContains(className))
                    return root;

                foreach (VisualElement child in root.Children())
                {
                    VisualElement match = FindElementByClass(child, className);
                    if (match != null)
                        return match;
                }

                return null;
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
