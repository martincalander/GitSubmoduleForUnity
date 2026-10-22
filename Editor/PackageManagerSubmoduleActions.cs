using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEditor.PackageManager.Requests;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;
using UnityEngine;
using UnityEngine.UIElements;

namespace MartinCalander.GitSubmoduleManager.Editor
{
#if UNITY_6000_5_OR_NEWER
    [Unity.Scripting.LifecycleManagement.NoAutoStaticsCleanup]
#endif
    internal static class PackageManagerSubmoduleActions
    {
        internal const string InstallMenuText = "Install package as Git submodule...";
        private const string NativePrefix = "UnityEditor.PackageManager.UI.Internal.";
        private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private static readonly Lazy<AddMenuContract> Contract = new(() => AddMenuContract.Create());
        private static readonly Dictionary<VisualElement, MenuEntry> Entries = new();
        private static readonly ConditionalWeakTable<VisualElement, MenuEntry> Menus = new();
        private static readonly List<WeakReference<VisualElement>> MenuReferences = new();
        private static ConditionalWeakTable<VisualElement, VisualElement> RootToolbars = new();
        private static OperationResources resources;
        private static Task<string> operation;
        private static ListRequest resolution;
        private static Exception operationError;
        private static bool installing;
        private static double resolutionDeadline;
        private static int progressId = -1;
        private static bool reloadLocked;
        private static bool refreshLocked;
        internal static bool IsBusy => operation != null || resources != null;
        internal static bool HasReloadLock => reloadLocked;
        internal static bool HasRefreshLock => refreshLocked;
        internal static string LastError { get; private set; } = string.Empty;

        private sealed class MenuEntry
        {
            internal object Item;
            internal EventCallback<DetachFromPanelEvent> Detached;
            internal EventCallback<AttachToPanelEvent> Attached;
        }

        private sealed class AddMenuContract
        {
            internal MethodInfo Setup, Add, Remove, ShowInput;
            internal Type MenuType, InputType;
            internal PropertyInfo Text, Action, Enabled, Visible;
            internal FieldInfo Title, Placeholder, Submit, Submitted, Items;

            internal static AddMenuContract Create()
            {
                try { return Discover(); }
                catch { return null; }
            }

            private static AddMenuContract Discover()
            {
                Type Find(string name) => PackageManagerSubmoduleHarmonyPatch.FindType(name);
                var value = new AddMenuContract
                {
                    MenuType = Find(NativePrefix + "ExtendableToolbarMenu"),
                    InputType = Find("UnityEditor.PackageManager.UI.InputDropdownArgs")
                };
                Type toolbar = Find(NativePrefix + "PackageManagerToolbar");
                Type item = Find(NativePrefix + "MenuDropdownItem");
                if (value.MenuType == null || value.InputType == null || toolbar == null || item == null ||
                    !typeof(VisualElement).IsAssignableFrom(value.MenuType))
                    return null;
                value.Setup = toolbar.GetMethod("SetupAddMenu", Instance, null, Type.EmptyTypes, null);
                value.Add = value.MenuType.GetMethod("AddDropdownItem", Instance, null, Type.EmptyTypes, null);
                value.Remove = value.MenuType.GetMethod("Remove", Instance, null, new[] { item }, null);
                value.ShowInput = value.MenuType.GetMethod("ShowInputDropdown", Instance, null, new[] { value.InputType }, null);
                value.Text = item.GetProperty("text", Instance);
                value.Action = item.GetProperty("action", Instance);
                value.Enabled = item.GetProperty("enabled", Instance);
                value.Visible = item.GetProperty("visible", Instance);
                value.Title = value.InputType.GetField("title", Instance);
                value.Placeholder = value.InputType.GetField("placeholderText", Instance);
                value.Submit = value.InputType.GetField("submitButtonText", Instance);
                value.Submitted = value.InputType.GetField("onInputSubmitted", Instance);
                value.Items = value.MenuType.GetField("m_DropdownItems", Instance);
                bool Method(MethodInfo method, Type returns) => method != null && !method.IsGenericMethod && method.ReturnType == returns;
                bool Property(PropertyInfo property, Type type) => property?.PropertyType == type && property.CanWrite && property.GetIndexParameters().Length == 0;
                bool Field(FieldInfo field, Type type) => field?.FieldType == type && !field.IsInitOnly;
                return Method(value.Setup, typeof(void)) && value.Add != null && !value.Add.IsGenericMethod &&
                       value.Add.ReturnType == Find("UnityEditor.PackageManager.UI.IMenuDropdownItem") &&
                       value.Items != null && typeof(IList).IsAssignableFrom(value.Items.FieldType) &&
                       Method(value.Remove, typeof(void)) &&
                       Method(value.ShowInput, typeof(void)) && Property(value.Text, typeof(string)) &&
                       Property(value.Action, typeof(Action)) && Property(value.Enabled, typeof(bool)) &&
                       Property(value.Visible, typeof(bool)) && Field(value.Title, typeof(string)) &&
                       Field(value.Placeholder, typeof(string)) && Field(value.Submit, typeof(string)) &&
                       Field(value.Submitted, typeof(Action<string>)) ? value : null;
            }
        }

        internal static MethodInfo GetAddMenuTarget() => Contract.Value?.Setup;

        internal static void AttachToolbar(object toolbar)
        {
            if (!PackageManagerUnityVersionSupport.IsCurrentVersionSupported ||
                toolbar?.GetType().FullName != NativePrefix + "PackageManagerToolbar")
                return;
            AttachMenu(PackageManagerSubmoduleHarmonyPatch.Read(toolbar, "addMenu") as VisualElement);
        }

        internal static bool AttachMenu(VisualElement menu)
        {
            AddMenuContract contract = Contract.Value;
            if (menu == null || contract == null || !contract.MenuType.IsInstanceOfType(menu))
                return false;
            object item = null;
            try
            {
                if (Entries.TryGetValue(menu, out MenuEntry current))
                {
                    if (contract.Items.GetValue(menu) is IList items && items.Contains(current.Item))
                        return true;
                    SuspendMenu(menu, current);
                }
                item = contract.Add.Invoke(menu, null);
                contract.Text.SetValue(item, InstallMenuText);
                object ownedItem = item;
                contract.Action.SetValue(item, (Action)(() =>
                {
                    if (Entries.TryGetValue(menu, out MenuEntry active) && ReferenceEquals(active.Item, ownedItem))
                        ShowInstallInput(menu);
                }));
                contract.Enabled.SetValue(item, !IsBusy);
                contract.Visible.SetValue(item, true);
                if (!Menus.TryGetValue(menu, out MenuEntry entry))
                {
                    entry = new MenuEntry();
                    entry.Detached = evt =>
                    {
                        if (ReferenceEquals(evt.target, menu))
                            SuspendMenu(menu, entry);
                    };
                    entry.Attached = evt =>
                    {
                        if (ReferenceEquals(evt.target, menu))
                            AttachMenu(menu);
                    };
                    menu.RegisterCallback(entry.Detached);
                    menu.RegisterCallback(entry.Attached);
                    Menus.Add(menu, entry);
                    MenuReferences.RemoveAll(reference => !reference.TryGetTarget(out _));
                    MenuReferences.Add(new WeakReference<VisualElement>(menu));
                }
                entry.Item = item;
                Entries.Add(menu, entry);
                return true;
            }
            catch (Exception exception)
            {
                LastError = exception.GetBaseException().Message;
                RemoveItem(menu, item);
                return false;
            }
        }

        internal static void AttachInRoot(VisualElement root)
        {
            if (root == null)
                return;
            if (RootToolbars.TryGetValue(root, out VisualElement toolbar))
            {
                if (root.Contains(toolbar))
                {
                    AttachToolbar(toolbar);
                    return;
                }
                RootToolbars.Remove(root);
            }
            toolbar = FindToolbar(root);
            if (toolbar == null)
                return;
            RootToolbars.Add(root, toolbar);
            AttachToolbar(toolbar);
        }

        private static VisualElement FindToolbar(VisualElement root)
        {
            if (root.GetType().FullName == NativePrefix + "PackageManagerToolbar")
                return root;
            foreach (VisualElement child in root.Children())
            {
                VisualElement toolbar = FindToolbar(child);
                if (toolbar != null)
                    return toolbar;
            }
            return null;
        }

        private static void SuspendMenu(VisualElement menu, MenuEntry entry)
        {
            Entries.Remove(menu);
            object item = entry.Item;
            entry.Item = null;
            RemoveItem(menu, item);
        }

        private static void RemoveItem(VisualElement menu, object item)
        {
            if (item == null)
                return;
            try { Contract.Value.Action.SetValue(item, null); }
            catch { }
            try { Contract.Value.Remove.Invoke(menu, new[] { item }); }
            catch { }
        }

        internal static void RemoveMenu(VisualElement menu)
        {
            if (menu == null || !Menus.TryGetValue(menu, out MenuEntry entry))
                return;
            menu.UnregisterCallback(entry.Detached);
            menu.UnregisterCallback(entry.Attached);
            Menus.Remove(menu);
            SuspendMenu(menu, entry);
            MenuReferences.RemoveAll(reference => !reference.TryGetTarget(out VisualElement target) || ReferenceEquals(target, menu));
        }

        internal static void RemoveMenus()
        {
            foreach (WeakReference<VisualElement> reference in MenuReferences.ToArray())
                if (reference.TryGetTarget(out VisualElement menu))
                    RemoveMenu(menu);
            MenuReferences.Clear();
            RootToolbars = new ConditionalWeakTable<VisualElement, VisualElement>();
        }

        internal static object CreateInputArguments(Action<string> submitted)
        {
            AddMenuContract contract = Contract.Value;
            if (contract == null)
                return null;
            object args = Activator.CreateInstance(contract.InputType);
            contract.Title.SetValue(args, "Install package as Git submodule");
            contract.Placeholder.SetValue(args, "https://host/owner/repository.git");
            contract.Submit.SetValue(args, "Install");
            contract.Submitted.SetValue(args, submitted);
            return args;
        }

        private static void ShowInstallInput(VisualElement menu)
        {
            if (!CanStart())
                return;
            try { Contract.Value.ShowInput.Invoke(menu, new[] { CreateInputArguments(StartInstall) }); }
            catch (Exception exception) { ShowError(exception.GetBaseException().Message); }
        }

        private static bool CanStart()
        {
            string reason = IsBusy ? "Wait for the current submodule operation to finish." :
                EditorApplication.isPlayingOrWillChangePlaymode ? "Exit Play mode before changing package submodules." :
                EditorApplication.isCompiling || EditorApplication.isUpdating ? "Wait for Unity to finish importing and compiling." : null;
            if (reason == null)
                return true;
            EditorUtility.DisplayDialog("Git submodule package", reason, "OK");
            return false;
        }

        private static void StartInstall(string source)
        {
            if (!CanStart())
                return;
            var service = new SubmodulePackageOperations(ProjectRoot, installedPackageNames:
                PackageInfo.GetAllRegisteredPackages().Select(package => package.name));
            installing = true;
            StartOperation("Installing submodule package", () => service.Install(source));
        }

        internal static bool RequestRemoval(object version)
        {
            if (PackageManagerSubmoduleHarmonyPatch.Read(version, "isInstalled") is not bool installed || !installed)
                return false;
            string name = PackageManagerSubmoduleHarmonyPatch.Read(version, "name") as string;
            string path = PackageManagerSubmoduleHarmonyPatch.Read(version, "localPath") as string;
            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(path))
                return false;
            if (!CanStart())
                return true;
            string[] dependents = FindDependents(name, PackageInfo.GetAllRegisteredPackages());
            if (dependents.Length != 0)
            {
                ShowError("Other installed packages depend on " + name + ": " + string.Join(", ", dependents) +
                    ". Remove those dependencies before removing this package.");
                return true;
            }
            if (!EditorUtility.DisplayDialog("Remove submodule package", "Remove " + name +
                " from the project as both a package and a Git submodule?\n\n" +
                "Local changes or unpublished commits will block removal.", "Remove", "Cancel"))
                return true;
            var service = new SubmodulePackageOperations(ProjectRoot);
            installing = false;
            StartOperation("Removing " + name, () => { service.Remove(name, path); return name; });
            return true;
        }

        internal static string[] FindDependents(string name, IEnumerable<PackageInfo> packages) =>
            packages.Where(package => package.name != name && package.dependencies != null &&
                package.dependencies.Any(dependency => dependency.name == name))
                .Select(package => package.name).Distinct().OrderBy(value => value, StringComparer.Ordinal).ToArray();

        private static string ProjectRoot => Path.GetFullPath(Path.Combine(Application.dataPath, ".."));

        private static void StartOperation(string title, Func<string> work)
        {
            if (IsBusy)
                throw new InvalidOperationException("A submodule operation is already running.");
            try
            {
                LastError = string.Empty;
                operationError = null;
                resolution = null;
                resources = new OperationResources(SetReloadLocked, SetRefreshLocked, SetCallbacks);
                resources.Acquire();
                progressId = Progress.Start(title, "Checking package and Git state...", Progress.Options.Indefinite);
                operation = Task.Run(work);
                SetMenuEnabled(false);
            }
            catch (Exception exception)
            {
                FinishOperation(exception);
            }
        }

        private static void SetReloadLocked(bool locked)
        {
            if (locked)
            {
                EditorApplication.LockReloadAssemblies();
                reloadLocked = true;
            }
            else
            {
                reloadLocked = false;
                EditorApplication.UnlockReloadAssemblies();
            }
        }

        private static void SetRefreshLocked(bool locked)
        {
            if (locked)
            {
                AssetDatabase.DisallowAutoRefresh();
                refreshLocked = true;
            }
            else
            {
                refreshLocked = false;
                AssetDatabase.AllowAutoRefresh();
            }
        }

        private static void SetCallbacks(bool subscribe)
        {
            EditorApplication.update -= PollOperation;
            EditorApplication.wantsToQuit -= CanQuit;
            if (subscribe)
            {
                EditorApplication.update += PollOperation;
                EditorApplication.wantsToQuit += CanQuit;
            }
        }

        internal sealed class OperationResources : IDisposable
        {
            private readonly Action<bool> reload, refresh, callbacks;
            private bool reloadOwned, refreshOwned, callbacksOwned, acquired;

            internal OperationResources(Action<bool> reload, Action<bool> refresh, Action<bool> callbacks)
            {
                this.reload = reload;
                this.refresh = refresh;
                this.callbacks = callbacks;
            }

            internal void Acquire()
            {
                if (acquired)
                    throw new InvalidOperationException("Operation resources have already been acquired.");
                acquired = true;
                try
                {
                    reload(true);
                    reloadOwned = true;
                    refresh(true);
                    refreshOwned = true;
                    // A subscription can fail after registering its first callback.
                    callbacksOwned = true;
                    callbacks(true);
                }
                catch
                {
                    Dispose();
                    throw;
                }
            }

            public void Dispose()
            {
                try
                {
                    if (callbacksOwned)
                    {
                        callbacksOwned = false;
                        callbacks(false);
                    }
                }
                finally
                {
                    try
                    {
                        if (refreshOwned)
                        {
                            refreshOwned = false;
                            refresh(false);
                        }
                    }
                    finally
                    {
                        if (reloadOwned)
                        {
                            reloadOwned = false;
                            reload(false);
                        }
                    }
                }
            }
        }

        private static bool CanQuit()
        {
            if (!IsBusy)
                return true;
            EditorUtility.DisplayDialog("Git submodule package", "Wait for the submodule operation to finish before closing Unity.", "OK");
            return false;
        }

        private static void PollOperation()
        {
            try { PollOperationCore(); }
            catch (Exception exception) { FinishOperation(exception); }
        }

        private static void PollOperationCore()
        {
            if (operation == null || !operation.IsCompleted)
                return;
            if (resolution == null)
            {
                operationError = GetOperationError(operation);
                try
                {
                    // Resolve partial failures too, so preserved recovery checkouts have current Unity state.
                    Client.Resolve();
                    resolution = Client.List(false, true);
                    resolutionDeadline = EditorApplication.timeSinceStartup + 120;
                    Progress.Report(progressId, 0.9f, "Refreshing Unity packages...");
                }
                catch (Exception exception)
                {
                    FinishOperation(operationError ?? exception);
                }
                return;
            }
            if (!resolution.IsCompleted)
            {
                if (EditorApplication.timeSinceStartup >= resolutionDeadline)
                    FinishOperation(operationError ?? new TimeoutException(
                        "The Git operation finished, but Unity package resolution did not finish within two minutes. Check Package Manager for errors."));
                return;
            }
            Exception error = operationError;
            if (resolution.Status == StatusCode.Failure)
                error ??= new InvalidOperationException("The Git operation finished, but Unity could not resolve packages: " + resolution.Error?.message);
            else if (error == null)
                error = ValidateResolvedPackage(operation.Result, installing, ProjectRoot, resolution.Result);
            FinishOperation(error);
        }

        internal static Exception GetOperationError(Task<string> task) => task.IsFaulted
            ? task.Exception?.InnerException
            : task.IsCanceled ? new OperationCanceledException("The submodule operation was cancelled. Review the repository before retrying.") : null;

        internal static Exception ValidateResolvedPackage(string name, bool install, string projectRoot, IEnumerable<PackageInfo> packages)
        {
            PackageInfo installed = packages.FirstOrDefault(package => package.name == name);
            if ((installed != null) != install)
                return new InvalidOperationException(install
                    ? "Git installed " + name + ", but Unity did not register the package. Check its package.json and Package Manager errors."
                    : "Git removed the submodule, but Unity still resolves " + name + ". Check whether another package depends on it.");
            if (!install)
                return null;
            if (installed.errors != null && installed.errors.Length != 0)
                return new InvalidOperationException("Git installed " + name + ", but Unity reported package errors: " +
                    string.Join("\n", installed.errors.Select(packageError => packageError.message)));
            bool samePath = false;
            try
            {
                samePath = string.Equals(Path.GetFullPath(installed.resolvedPath), Path.GetFullPath(Path.Combine(projectRoot, "Packages", name)),
                    Path.DirectorySeparatorChar == '\\' ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
            }
            catch { }
            return installed.source == PackageSource.Embedded && samePath ? null : new InvalidOperationException(
                "Git installed " + name + ", but Unity resolved another package source. Refresh Package Manager and inspect its resolved path.");
        }

        private static void FinishOperation(Exception error)
        {
            bool started = operation != null;
            operation = null;
            resolution = null;
            operationError = null;
            try
            {
                if (progressId >= 0)
                {
                    try { Progress.Finish(progressId, error == null ? Progress.Status.Succeeded : Progress.Status.Failed); }
                    catch { }
                }
                progressId = -1;
                SetMenuEnabled(true);
                if (error != null)
                    ShowError(error.Message);
                if (started)
                {
                    PackageManagerPackageStatus.Refresh();
                    AssetDatabase.Refresh();
                }
            }
            catch (Exception exception)
            {
                ShowError("The Git operation finished, but Unity could not refresh packages: " + exception.GetBaseException().Message);
            }
            finally
            {
                OperationResources owned = resources;
                try { owned?.Dispose(); }
                catch (Exception exception) { ShowError("Unity could not release operation resources: " + exception.Message); }
                finally { resources = null; }
            }
        }

        private static void SetMenuEnabled(bool enabled)
        {
            foreach (MenuEntry entry in Entries.Values)
            {
                try { Contract.Value.Enabled.SetValue(entry.Item, enabled); }
                catch { }
            }
        }

        private static void ShowError(string message)
        {
            LastError = message;
            EditorUtility.DisplayDialog("Git submodule package", message, "OK");
        }
    }
}
