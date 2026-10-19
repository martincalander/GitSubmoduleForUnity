using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine.UIElements;

namespace MartinCalander.GitSubmoduleManager.Editor
{
#if UNITY_6000_5_OR_NEWER
    [Unity.Scripting.LifecycleManagement.NoAutoStaticsCleanup]
#endif
    internal sealed class PackageManagerSubmoduleFilter : IDisposable
    {
        internal const string ToggleElementName = "git-submodule-manager-submodules-only";
        internal const string LegacyPageId = "Extension/git-submodule-manager-submodules";
        private const string Prefix = "UnityEditor.PackageManager.UI.Internal.";
        private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private const string ActiveClass = "git-submodule-filter-active";
        private const string HiddenClass = "git-submodule-filter-hidden";
        private static readonly Lazy<NativeContract> Contract = new(ResolveContract);
        private static readonly Lazy<StyleSheet> Styles = new(() => AssetDatabase.LoadAssetAtPath<StyleSheet>(
            "Packages/com.martincalander.gitsubmodulemanager/Editor/PackageManagerSubmoduleFilter.uss"));
        private static readonly HashSet<PackageManagerSubmoduleFilter> Sessions = new();
        private static readonly Dictionary<object, FilterState> States = new(new IdentityComparer());
        private readonly VisualElement root;
        private object manager;
        private object page;
        private VisualElement searchBar;
        private ToolbarToggle toggle;
        private FilterState state;
        private bool disposed;

        internal event Action AttachmentInvalidated;
        internal bool IsAttached => !disposed && toggle != null && searchBar != null &&
            ReferenceEquals(toggle.parent, searchBar) && root.Contains(searchBar);
        internal bool IsEnabled => IsAttached && state?.Enabled == true;

        private sealed class FilterState
        {
            internal readonly HashSet<PackageManagerSubmoduleFilter> Owners = new();
            internal bool Enabled;
            internal bool Rebuilding;
            internal string Error;
        }

        private sealed class IdentityComparer : IEqualityComparer<object>
        {
            public new bool Equals(object left, object right) => ReferenceEquals(left, right);
            public int GetHashCode(object value) => RuntimeHelpers.GetHashCode(value);
        }

        private sealed class NativeContract
        {
            internal string Error;
            internal Type PageType;
            internal Type SearchBarType;
            internal MethodInfo ShouldInclude;
            internal MethodInfo SearchPageChanged;
            internal MethodInfo GetPage;
            internal PropertyInfo ActivePage;
            internal MethodInfo Rebuild;
            internal bool RebuildTakesArgument;
            internal MethodInfo ReconcileSelection;
            internal MethodInfo SelectionChanged;
        }

        internal PackageManagerSubmoduleFilter(VisualElement root)
        {
            this.root = root ?? throw new ArgumentNullException(nameof(root));
            Sessions.Add(this);
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
            {
                Detach();
                return false;
            }
            NativeContract contract = Contract.Value;
            if (!PackageManagerSubmoduleHarmonyPatch.IsPatched(contract.ShouldInclude) ||
                !PackageManagerSubmoduleHarmonyPatch.IsPatched(contract.SearchPageChanged))
            {
                error = "Unity's Package Manager filter hooks are unavailable.";
                Detach();
                return false;
            }
            try
            {
                object currentManager = Read(root, "m_PageManager") ?? Read(root, "pageManager");
                if (currentManager == null)
                    throw new InvalidOperationException("Package Manager is still loading.");
                object currentPage = contract.GetPage.Invoke(currentManager, new object[] { "InProject" });
                if (!IsAllPackagesPage(currentPage))
                    throw new InvalidOperationException("Unity's All Packages page is unavailable.");
                if (ReferenceEquals(manager, currentManager) && ReferenceEquals(page, currentPage) && IsAttached)
                {
                    if (!string.IsNullOrEmpty(state.Error))
                        throw new InvalidOperationException(state.Error);
                    UpdateToggle();
                    return true;
                }

                Detach();
                manager = currentManager;
                page = currentPage;
                searchBar = FindSearchBar(root, contract.SearchBarType);
                if (searchBar == null || searchBar.Q<ToolbarSearchField>() == null)
                    throw new InvalidOperationException("The Package Manager search bar is still loading.");
                if (Styles.Value == null)
                    throw new InvalidOperationException("The submodule filter stylesheet is unavailable.");
                searchBar.styleSheets.Add(Styles.Value);
                if (!States.TryGetValue(page, out state))
                    States.Add(page, state = new FilterState());
                state.Owners.Add(this);
                toggle = new ToolbarToggle
                {
                    name = ToggleElementName,
                    text = "Submodules only",
                    tooltip = "Show installed packages verified as Git submodules, from any host."
                };
                toggle.RegisterValueChangedCallback(OnValueChanged);
                toggle.RegisterCallback<DetachFromPanelEvent>(OnToggleDetached);
                searchBar.Add(toggle);
                if (string.Equals(Read(contract.ActivePage.GetValue(manager), "id") as string,
                    LegacyPageId, StringComparison.Ordinal))
                    contract.ActivePage.SetValue(manager, page);
                UpdateToggle();
                return true;
            }
            catch (Exception exception)
            {
                error = exception.GetBaseException().Message;
                Detach();
                return false;
            }
        }

        internal void Refresh()
        {
            UpdateToggle();
        }

        public void Dispose()
        {
            if (disposed)
                return;
            disposed = true;
            Sessions.Remove(this);
            Detach();
            AttachmentInvalidated = null;
        }

        private void OnValueChanged(ChangeEvent<bool> evt)
        {
            if (!IsAttached || state == null || !ReferenceEquals(Contract.Value.ActivePage.GetValue(manager), page))
                return;
            state.Enabled = evt.newValue;
            foreach (PackageManagerSubmoduleFilter owner in state.Owners)
                owner.UpdateToggle();
            RebuildPage(page, state);
        }

        private void UpdateToggle()
        {
            if (!IsAttached)
                return;
            toggle.SetValueWithoutNotify(state?.Enabled == true);
            bool visible = ReferenceEquals(Read(manager, "activePage"), page);
            toggle.EnableInClassList(HiddenClass, !visible);
            searchBar.EnableInClassList(ActiveClass, visible);
        }

        private void OnToggleDetached(DetachFromPanelEvent evt)
        {
            if (disposed || !ReferenceEquals(evt.target, toggle))
                return;
            Detach();
            AttachmentInvalidated?.Invoke();
        }

        private void Detach()
        {
            if (searchBar != null)
            {
                searchBar.RemoveFromClassList(ActiveClass);
                if (Styles.IsValueCreated && Styles.Value != null)
                    searchBar.styleSheets.Remove(Styles.Value);
            }
            if (toggle != null)
            {
                toggle.UnregisterValueChangedCallback(OnValueChanged);
                toggle.UnregisterCallback<DetachFromPanelEvent>(OnToggleDetached);
                toggle.RemoveFromHierarchy();
                toggle = null;
            }
            if (state != null)
            {
                state.Owners.Remove(this);
                if (state.Owners.Count == 0)
                {
                    States.Remove(page);
                    bool wasEnabled = state.Enabled;
                    state.Enabled = false;
                    if (wasEnabled)
                        RebuildPage(page, state);
                }
            }
            state = null;
            page = null;
            manager = null;
            searchBar = null;
        }

        private static void RebuildPage(object page, FilterState state)
        {
            if (page == null || state.Rebuilding)
                return;
            state.Rebuilding = true;
            try
            {
                NativeContract contract = Contract.Value;
                contract.Rebuild.Invoke(page, contract.RebuildTakesArgument ? new object[] { true } : null);
                ReconcileSelection(page);
            }
            catch (Exception exception)
            {
                state.Enabled = false;
                state.Error = exception.GetBaseException().Message;
                foreach (PackageManagerSubmoduleFilter owner in state.Owners)
                    owner.UpdateToggle();
                // Restore native membership even if a selection callback failed after the first rebuild.
                try
                {
                    NativeContract contract = Contract.Value;
                    contract.Rebuild.Invoke(page, contract.RebuildTakesArgument ? new object[] { true } : null);
                    ReconcileSelection(page);
                }
                catch { }
                foreach (PackageManagerSubmoduleFilter owner in new List<PackageManagerSubmoduleFilter>(state.Owners))
                    owner.AttachmentInvalidated?.Invoke();
            }
            finally { state.Rebuilding = false; }
        }

        internal static void ReconcileSelection(object candidatePage)
        {
            if (IsAllPackagesPage(candidatePage))
            {
                NativeContract contract = Contract.Value;
                if (contract.ReconcileSelection.Invoke(candidatePage, null) is false)
                    contract.SelectionChanged.Invoke(candidatePage, new object[] { false });
            }
        }

        internal static bool TryRefreshAllPackages(object candidatePage)
        {
            if (!IsSupportedContract(out _) || !IsAllPackagesPage(candidatePage))
                return false;
            States.TryGetValue(candidatePage, out FilterState current);
            RebuildPage(candidatePage, current ?? new FilterState());
            return true;
        }

        internal static bool ApplyNativeFilter(object candidatePage, object package, bool nativeResult)
        {
            if (!nativeResult || !IsEnabledFor(candidatePage))
                return nativeResult;
            return PackageManagerPackageStatus.IsSubmodulePackage(package);
        }

        internal static bool IsEnabledFor(object candidatePage)
        {
            if (candidatePage == null || !States.TryGetValue(candidatePage, out FilterState current) ||
                !current.Enabled || !IsAllPackagesPage(candidatePage))
                return false;
            foreach (PackageManagerSubmoduleFilter owner in current.Owners)
                if (owner.IsAttached)
                    return true;
            return false;
        }

        internal static void NotifySearchPageChanged(VisualElement changedSearchBar)
        {
            if (changedSearchBar == null)
                return;
            foreach (PackageManagerSubmoduleFilter session in new List<PackageManagerSubmoduleFilter>(Sessions))
            {
                if (!session.root.Contains(changedSearchBar))
                    continue;
                if (ReferenceEquals(session.searchBar, changedSearchBar) && session.IsAttached &&
                    ReferenceEquals(Read(session.root, "m_PageManager") ?? Read(session.root, "pageManager"), session.manager))
                    session.UpdateToggle();
                else
                    session.AttachmentInvalidated?.Invoke();
            }
        }

        internal static bool IsAllPackagesPage(object candidate) => candidate?.GetType() == Contract.Value.PageType &&
            string.Equals(Read(candidate, "id") as string, "InProject", StringComparison.Ordinal);

        internal static MethodInfo GetMembershipTarget() => Contract.Value.ShouldInclude;
        internal static MethodInfo GetSearchPageChangedTarget() => Contract.Value.SearchPageChanged;

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
                contract.Error = "This Unity version has not been validated for the submodule filter.";
                return contract;
            }
            try
            {
                contract.PageType = FindType("InProjectPage");
                contract.SearchBarType = FindType("PackageSearchBar");
                contract.ShouldInclude = PackageManagerSubmoduleHarmonyPatch.FindMethod("InProjectPage", "ShouldInclude", typeof(bool), "IPackage");
                contract.SearchPageChanged = PackageManagerSubmoduleHarmonyPatch.FindMethod("PackageSearchBar", "OnActivePageChanged", typeof(void), "IPage");
                Type managerType = FindType("PageManager");
                contract.ActivePage = managerType?.GetProperty("activePage", Instance);
                if (managerType != null)
                    foreach (MethodInfo method in managerType.GetMethods(Instance))
                        if (method.Name == "GetPage" && !method.IsGenericMethod && method.ReturnType == FindType("IPage") &&
                            method.GetParameters().Length == 1 && method.GetParameters()[0].ParameterType == typeof(string))
                            contract.GetPage = method;
                contract.Rebuild = contract.PageType?.GetMethod("Rebuild", Instance, null, new[] { typeof(bool) }, null);
                contract.RebuildTakesArgument = contract.Rebuild != null;
                contract.Rebuild ??= contract.PageType?.GetMethod("RebuildVisualStatesAndUpdateVisibilityWithSearchText", Instance, null, Type.EmptyTypes, null);
                contract.ReconcileSelection = contract.PageType?.GetMethod("UpdateSelectionIfCurrentSelectionIsInvalid", Instance, null, Type.EmptyTypes, null);
                contract.SelectionChanged = contract.PageType?.GetMethod("TriggerOnSelectionChanged", Instance, null, new[] { typeof(bool) }, null);
                if (contract.PageType == null || contract.SearchBarType == null ||
                    !typeof(VisualElement).IsAssignableFrom(contract.SearchBarType) ||
                    contract.ShouldInclude == null || contract.SearchPageChanged == null || contract.GetPage == null ||
                    contract.ActivePage?.PropertyType != FindType("IPage") || !contract.ActivePage.CanRead || !contract.ActivePage.CanWrite ||
                    contract.Rebuild?.ReturnType != typeof(void) || contract.Rebuild.IsGenericMethod ||
                    contract.ReconcileSelection?.ReturnType != typeof(bool) || contract.ReconcileSelection.IsGenericMethod ||
                    contract.SelectionChanged?.ReturnType != typeof(void) || contract.SelectionChanged.IsGenericMethod)
                    contract.Error = "Unity's All Packages filter contract has changed.";
            }
            catch (Exception exception) { contract.Error = exception.GetBaseException().Message; }
            return contract;
        }

        private static VisualElement FindSearchBar(VisualElement element, Type type)
        {
            if (element.GetType() == type)
                return element;
            foreach (VisualElement child in element.Children())
            {
                VisualElement found = FindSearchBar(child, type);
                if (found != null)
                    return found;
            }
            return null;
        }

        private static Type FindType(string name) => PackageManagerSubmoduleHarmonyPatch.FindType(Prefix + name);
        private static object Read(object instance, string name) => PackageManagerSubmoduleHarmonyPatch.Read(instance, name);
    }
}
