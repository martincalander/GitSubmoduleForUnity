using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace MartinCalander.GitSubmoduleManager.Editor
{
    /// <summary>
    /// Adds the discovered-repository action through Unity's own Package Manager
    /// extension contract. Every reflected dependency is optional so a changed
    /// Editor contract falls back to the compatibility host without affecting UPM.
    /// </summary>
    internal static class PackageManagerGitHubNativeActions
    {
        internal const string PackageManagerWindowRootTypeName =
            "UnityEditor.PackageManager.UI.Internal.PackageManagerWindowRoot";
        internal const string PackageToolbarTypeName =
            "UnityEditor.PackageManager.UI.Internal.PackageToolbar";
        internal const string PackageActionButtonInterfaceTypeName =
            "UnityEditor.PackageManager.UI.IPackageActionButton";
        internal const string PackageSelectionArgsTypeName =
            "UnityEditor.PackageManager.UI.PackageSelectionArgs";
        internal const string ActionButtonElementName =
            "git-submodule-manager-add-submodule-action";

        private const string AddPackageActionButtonMethodName =
            "AddPackageActionButton";
        private const string ActionText = "Add as Submodule";
        private const int ActionPriority = 100;

        private const BindingFlags AnyInstance =
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private const BindingFlags AnyStatic =
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        private static readonly Dictionary<object, NativeActionEntry> EntriesByRoot =
            new(ReferenceComparer.Instance);
        private static readonly Dictionary<object, NativeActionEntry> EntriesByToolbar =
            new(ReferenceComparer.Instance);

        internal static bool InstallForRoot(object packageManagerRoot)
        {
            if (!(packageManagerRoot is VisualElement root) ||
                !string.Equals(
                    root.GetType().FullName,
                    PackageManagerWindowRootTypeName,
                    StringComparison.Ordinal))
            {
                return false;
            }

            try
            {
                object toolbar = FindPackageToolbar(root);
                if (toolbar == null)
                    return false;

                if (EntriesByRoot.TryGetValue(root, out NativeActionEntry current))
                {
                    if (ReferenceEquals(current.Toolbar, toolbar))
                        return true;

                    ReleaseForRoot(root);
                }

                MethodInfo addAction = FindAddPackageActionButtonMethod(root.GetType());
                if (addAction == null)
                    return false;

                object action = addAction.Invoke(root, null);
                if (!TryCreateEntry(root, toolbar, action, out NativeActionEntry entry))
                {
                    HideUntrackedAction(action);
                    return false;
                }

                EntriesByRoot[root] = entry;
                EntriesByToolbar[toolbar] = entry;
                RefreshForToolbar(toolbar, GetFieldValue(toolbar, "m_Package"));
                return true;
            }
            catch
            {
                return false;
            }
        }

        internal static void ReleaseForRoot(object packageManagerRoot)
        {
            if (packageManagerRoot == null ||
                !EntriesByRoot.TryGetValue(
                    packageManagerRoot,
                    out NativeActionEntry entry))
            {
                return;
            }

            EntriesByRoot.Remove(packageManagerRoot);
            EntriesByToolbar.Remove(entry.Toolbar);
            try
            {
                entry.VisibleProperty.SetValue(entry.Action, false, null);
                entry.EnabledProperty.SetValue(entry.Action, false, null);
                entry.ActionProperty.SetValue(entry.Action, null, null);
            }
            catch
            {
                // The Package Manager tree may already be tearing down.
            }
        }

        internal static void RefreshForToolbar(object toolbar, object package)
        {
            if (toolbar == null ||
                !EntriesByToolbar.TryGetValue(toolbar, out NativeActionEntry entry))
            {
                return;
            }

            try
            {
                bool onGitHubPage = IsGitHubPage(toolbar);
                PackageManagerGitHubRepository repository = null;
                bool isProjectedRepository =
                    onGitHubPage &&
                    PackageManagerGitHubPackageProjection.TryGetRepository(
                        package,
                        out repository);
                if (!isProjectedRepository)
                {
                    SetActionState(entry, false, false, string.Empty);
                    return;
                }

                string validationError = GitSubmoduleAddService.ValidateInput(
                    repository.Url,
                    repository.PackageName,
                    repository.DefaultBranch);
                bool enabled =
                    string.IsNullOrWhiteSpace(validationError) &&
                    GitSubmoduleAddService.CanStart;
                string tooltip = enabled
                    ? BuildEnabledTooltip(repository)
                    : BuildDisabledTooltip(validationError);
                SetActionState(entry, true, enabled, tooltip);
            }
            catch
            {
                SetActionState(entry, false, false, string.Empty);
            }
        }

        private static bool TryCreateEntry(
            VisualElement root,
            object toolbar,
            object action,
            out NativeActionEntry entry)
        {
            entry = null;
            if (action == null)
                return false;

            Type actionType = action.GetType();
            PropertyInfo actionProperty = actionType.GetProperty("action", AnyInstance);
            PropertyInfo textProperty = actionType.GetProperty("text", AnyInstance);
            PropertyInfo tooltipProperty = actionType.GetProperty("tooltip", AnyInstance);
            PropertyInfo priorityProperty = actionType.GetProperty("priority", AnyInstance);
            PropertyInfo iconProperty = actionType.GetProperty("icon", AnyInstance);
            PropertyInfo visibleProperty = actionType.GetProperty("visible", AnyInstance);
            PropertyInfo enabledProperty = actionType.GetProperty("enabled", AnyInstance);
            PropertyInfo dropdownButtonProperty = actionType.GetProperty(
                "dropdownButton",
                AnyInstance);

            if (!IsActionDelegateProperty(actionProperty) ||
                !IsWritableProperty(textProperty, typeof(string)) ||
                !IsWritableProperty(tooltipProperty, typeof(string)) ||
                !IsWritableProperty(priorityProperty, typeof(int)) ||
                !IsWritableProperty(iconProperty, typeof(Texture2D)) ||
                !IsWritableProperty(visibleProperty, typeof(bool)) ||
                !IsWritableProperty(enabledProperty, typeof(bool)))
            {
                return false;
            }

            Delegate callback = CreateActionDelegate(actionProperty.PropertyType);
            if (callback == null)
                return false;

            visibleProperty.SetValue(action, false, null);
            enabledProperty.SetValue(action, false, null);
            textProperty.SetValue(action, ActionText, null);
            tooltipProperty.SetValue(action, string.Empty, null);
            priorityProperty.SetValue(action, ActionPriority, null);
            iconProperty.SetValue(action, GitSubmoduleManagerIcons.GitIcon, null);
            if (dropdownButtonProperty?.GetValue(action, null) is VisualElement button)
                button.name = ActionButtonElementName;

            // PackageExtensionAction registers its internal click handler every
            // time this property receives a non-null delegate. Assign exactly once.
            actionProperty.SetValue(action, callback, null);
            entry = new NativeActionEntry(
                root,
                toolbar,
                action,
                actionProperty,
                textProperty,
                tooltipProperty,
                visibleProperty,
                enabledProperty);
            return true;
        }

        private static Delegate CreateActionDelegate(Type delegateType)
        {
            if (delegateType == null ||
                !delegateType.IsGenericType ||
                delegateType.GetGenericTypeDefinition() != typeof(Action<>))
            {
                return null;
            }

            Type selectionType = delegateType.GetGenericArguments()[0];
            if (!string.Equals(
                    selectionType.FullName,
                    PackageSelectionArgsTypeName,
                    StringComparison.Ordinal))
            {
                return null;
            }

            MethodInfo bridge = typeof(PackageManagerGitHubNativeActions).GetMethod(
                nameof(OnNativeActionInvoked),
                AnyStatic);
            if (bridge == null)
                return null;

            ParameterExpression selection = Expression.Parameter(
                selectionType,
                "selection");
            MethodCallExpression body = Expression.Call(
                bridge,
                Expression.Convert(selection, typeof(object)));
            return Expression.Lambda(delegateType, body, selection).Compile();
        }

        private static void OnNativeActionInvoked(object selection)
        {
            try
            {
                object window = GetPropertyValue(selection, "window");
                if (!IsGitHubPageForRoot(window))
                    return;

                object selected = GetPropertyValue(selection, "package") ??
                                  GetPropertyValue(selection, "packageVersion");
                if (!PackageManagerGitHubPackageProjection.TryGetRepository(
                        selected,
                        out PackageManagerGitHubRepository repository))
                {
                    ShowError(
                        "Cannot Add Git Package",
                        "This discovered repository is no longer available. " +
                        "Refresh Sources > GitHub and try again.");
                    RefreshAllEntries();
                    return;
                }

                string validationError = GitSubmoduleAddService.ValidateInput(
                    repository.Url,
                    repository.PackageName,
                    repository.DefaultBranch);
                if (!string.IsNullOrWhiteSpace(validationError))
                {
                    ShowError("Cannot Add Git Package", validationError);
                    RefreshAllEntries();
                    return;
                }

                if (!GitSubmoduleAddService.CanStart)
                {
                    ShowError(
                        "Cannot Add Git Package",
                        BuildDisabledTooltip(string.Empty));
                    RefreshAllEntries();
                    return;
                }

                string packagePath = GitSubmoduleAddService.GetPackagePath(
                    repository.PackageName);
                string branchDescription =
                    string.IsNullOrWhiteSpace(repository.DefaultBranch)
                        ? "the repository default"
                        : repository.DefaultBranch.Trim();
                string safeUrl = GitUtility.RedactCredentials(
                    repository.Url?.Trim() ?? string.Empty);
                if (!EditorUtility.DisplayDialog(
                        "Add Git Package?",
                        $"Repository:\n{safeUrl}\n\n" +
                        $"Branch: {branchDescription}\n" +
                        $"Destination: {packagePath}\n\n" +
                        "Unity packages can contain Editor code that executes " +
                        "inside the Unity Editor. Only install repositories you trust.",
                        "Clone and Add",
                        "Cancel"))
                {
                    return;
                }

                bool started = GitSubmoduleAddService.TryStart(
                    repository.Url,
                    repository.DefaultBranch,
                    repository.PackageName,
                    OnAddCompleted,
                    out string startError);
                if (!started)
                {
                    ShowError(
                        "Could Not Start Add",
                        string.IsNullOrWhiteSpace(startError)
                            ? "The Git package operation could not be started."
                            : startError);
                }

                RefreshAllEntries();
            }
            catch (Exception exception)
            {
                ShowError(
                    "Could Not Start Add",
                    "The Git package operation could not be started: " +
                    exception.Message);
                RefreshAllEntries();
            }
        }

        private static void OnAddCompleted(GitSubmoduleAddCompletion completion)
        {
            if (completion == null || !completion.Success)
            {
                ShowError(
                    "Could Not Add Git Package",
                    string.IsNullOrWhiteSpace(completion?.Message)
                        ? "The Git package operation did not complete successfully."
                        : completion.Message);
                RefreshAllEntries();
                return;
            }

            try
            {
                PackageManagerSubmoduleSnapshot.Refresh();
                PackageManagerGitHubPackageProjection.Reconcile(
                    PackageManagerGitHubDiscovery.Current);
                PackageManagerSubmoduleHarmonyPatch.RefreshOpenPackageManagerWindows();
            }
            catch (Exception exception)
            {
                ShowError(
                    "Package Manager Refresh Failed",
                    "The package was added, but Package Manager could not refresh: " +
                    exception.Message);
            }

            RefreshAllEntries();
        }

        private static void RefreshAllEntries()
        {
            var entries = new List<NativeActionEntry>(EntriesByToolbar.Values);
            foreach (NativeActionEntry entry in entries)
            {
                if (entry?.Toolbar == null)
                    continue;

                RefreshForToolbar(
                    entry.Toolbar,
                    GetFieldValue(entry.Toolbar, "m_Package"));
            }
        }

        private static void SetActionState(
            NativeActionEntry entry,
            bool visible,
            bool enabled,
            string tooltip)
        {
            if (entry == null)
                return;

            try
            {
                entry.TextProperty.SetValue(entry.Action, ActionText, null);
                entry.TooltipProperty.SetValue(
                    entry.Action,
                    tooltip ?? string.Empty,
                    null);
                entry.EnabledProperty.SetValue(entry.Action, enabled, null);
                entry.VisibleProperty.SetValue(entry.Action, visible, null);
            }
            catch
            {
                // A rebuilt Package Manager tree will install a fresh action.
            }
        }

        private static string BuildEnabledTooltip(
            PackageManagerGitHubRepository repository)
        {
            string destination = GitSubmoduleAddService.GetPackagePath(
                repository?.PackageName ?? string.Empty);
            return string.IsNullOrWhiteSpace(destination)
                ? "Add this GitHub package as a Git submodule."
                : $"Add this GitHub package as a Git submodule at {destination}.";
        }

        private static string BuildDisabledTooltip(string validationError)
        {
            if (!string.IsNullOrWhiteSpace(validationError))
                return validationError.Trim();

            string recoveryWarning = GitOperationService.RecoveryWarning;
            if (!string.IsNullOrWhiteSpace(recoveryWarning))
                return recoveryWarning.Trim();

            string activeLabel = GitOperationService.ActiveLabel;
            if (GitOperationService.IsBusy && !string.IsNullOrWhiteSpace(activeLabel))
                return $"Wait for {activeLabel.Trim()} to finish.";

            return "Wait for current package scans and repository operations to finish.";
        }

        private static bool IsGitHubPage(object toolbar)
        {
            object pageManager = GetFieldValue(toolbar, "m_PageManager");
            return IsGitHubPageManager(pageManager);
        }

        private static bool IsGitHubPageForRoot(object root)
        {
            object pageManager = GetFieldValue(root, "m_PageManager") ??
                                 GetPropertyValue(root, "pageManager");
            return IsGitHubPageManager(pageManager);
        }

        private static bool IsGitHubPageManager(object pageManager)
        {
            object activePage = GetPropertyValue(pageManager, "activePage");
            return string.Equals(
                GetPropertyValue(activePage, "id") as string,
                PackageManagerSubmoduleNativePage.ExtensionPageId,
                StringComparison.Ordinal);
        }

        private static MethodInfo FindAddPackageActionButtonMethod(Type rootType)
        {
            MethodInfo match = null;
            foreach (MethodInfo method in rootType.GetMethods(AnyInstance))
            {
                if (method.Name != AddPackageActionButtonMethodName ||
                    method.IsStatic ||
                    method.GetParameters().Length != 0 ||
                    !string.Equals(
                        method.ReturnType.FullName,
                        PackageActionButtonInterfaceTypeName,
                        StringComparison.Ordinal))
                {
                    continue;
                }

                if (match != null)
                    return null;
                match = method;
            }

            return match;
        }

        private static object FindPackageToolbar(VisualElement root)
        {
            if (root == null)
                return null;
            if (string.Equals(
                    root.GetType().FullName,
                    PackageToolbarTypeName,
                    StringComparison.Ordinal))
            {
                return root;
            }

            foreach (VisualElement child in root.Children())
            {
                object match = FindPackageToolbar(child);
                if (match != null)
                    return match;
            }

            return null;
        }

        private static bool IsActionDelegateProperty(PropertyInfo property)
        {
            if (property == null || !property.CanWrite)
                return false;

            Type propertyType = property.PropertyType;
            return propertyType.IsGenericType &&
                   propertyType.GetGenericTypeDefinition() == typeof(Action<>) &&
                   string.Equals(
                       propertyType.GetGenericArguments()[0].FullName,
                       PackageSelectionArgsTypeName,
                       StringComparison.Ordinal);
        }

        private static bool IsWritableProperty(
            PropertyInfo property,
            Type expectedType)
        {
            return property != null &&
                   property.CanWrite &&
                   property.PropertyType == expectedType;
        }

        private static object GetFieldValue(object instance, string fieldName)
        {
            if (instance == null)
                return null;

            for (Type type = instance.GetType(); type != null; type = type.BaseType)
            {
                FieldInfo field = type.GetField(fieldName, AnyInstance);
                if (field != null)
                    return field.GetValue(instance);
            }

            return null;
        }

        private static object GetPropertyValue(object instance, string propertyName)
        {
            if (instance == null)
                return null;

            for (Type type = instance.GetType(); type != null; type = type.BaseType)
            {
                PropertyInfo property = type.GetProperty(propertyName, AnyInstance);
                if (property != null && property.GetIndexParameters().Length == 0)
                    return property.GetValue(instance, null);
            }

            return null;
        }

        private static void HideUntrackedAction(object action)
        {
            if (action == null)
                return;

            try
            {
                PropertyInfo visible = action.GetType().GetProperty(
                    "visible",
                    AnyInstance);
                if (IsWritableProperty(visible, typeof(bool)))
                    visible.SetValue(action, false, null);

                PropertyInfo dropdownButton = action.GetType().GetProperty(
                    "dropdownButton",
                    AnyInstance);
                if (dropdownButton?.GetValue(action, null) is VisualElement button)
                    button.style.display = DisplayStyle.None;
            }
            catch
            {
                // A missing optional extension must not disturb Package Manager.
            }
        }

        private static void ShowError(string title, string message)
        {
            string safeMessage = GitHubUtility.SanitizeUiDiagnostic(message);
            EditorUtility.DisplayDialog(
                string.IsNullOrWhiteSpace(title) ? "Git Package Error" : title,
                string.IsNullOrWhiteSpace(safeMessage)
                    ? "The Git package operation could not be completed."
                    : safeMessage,
                "OK");
        }

        private sealed class NativeActionEntry
        {
            internal NativeActionEntry(
                object root,
                object toolbar,
                object action,
                PropertyInfo actionProperty,
                PropertyInfo textProperty,
                PropertyInfo tooltipProperty,
                PropertyInfo visibleProperty,
                PropertyInfo enabledProperty)
            {
                Root = root;
                Toolbar = toolbar;
                Action = action;
                ActionProperty = actionProperty;
                TextProperty = textProperty;
                TooltipProperty = tooltipProperty;
                VisibleProperty = visibleProperty;
                EnabledProperty = enabledProperty;
            }

            internal object Root { get; }
            internal object Toolbar { get; }
            internal object Action { get; }
            internal PropertyInfo ActionProperty { get; }
            internal PropertyInfo TextProperty { get; }
            internal PropertyInfo TooltipProperty { get; }
            internal PropertyInfo VisibleProperty { get; }
            internal PropertyInfo EnabledProperty { get; }
        }

        private sealed class ReferenceComparer : IEqualityComparer<object>
        {
            internal static ReferenceComparer Instance { get; } = new();

            public new bool Equals(object left, object right)
            {
                return ReferenceEquals(left, right);
            }

            public int GetHashCode(object instance)
            {
                return instance == null
                    ? 0
                    : System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(instance);
            }
        }
    }
}
