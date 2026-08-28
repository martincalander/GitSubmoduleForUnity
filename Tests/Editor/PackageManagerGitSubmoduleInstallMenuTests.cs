using System;
using System.Collections;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine.UIElements;

namespace MartinCalander.GitSubmoduleManager.Editor.Tests
{
    [Parallelizable(ParallelScope.None)]
    public sealed class PackageManagerGitSubmoduleInstallMenuTests
    {
        private const string MenuInterfaceTypeName =
            "UnityEditor.PackageManager.UI.IMenu";
        private const string MenuItemInterfaceTypeName =
            "UnityEditor.PackageManager.UI.IMenuDropdownItem";
        private const string ConcreteMenuTypeName =
            "UnityEditor.PackageManager.UI.Internal.ExtendableToolbarMenu";
        private const string ConcreteMenuItemTypeName =
            "UnityEditor.PackageManager.UI.Internal.MenuDropdownItem";

        private const BindingFlags AnyInstance =
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private const BindingFlags AnyStatic =
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        private sealed class FakeMenuItem
        {
            public Action action { get; set; }
            public string text { get; set; }
            public int priority { get; set; }
            public bool visible { get; set; }
            public bool enabled { get; set; }
            public bool insertSeparatorBefore { get; set; }
            public bool isChecked { get; set; }
        }

        private sealed class IncompleteMenuItem
        {
            public Action action { get; set; }
            public string text { get; set; }
            public bool visible { get; set; }
            public bool enabled { get; set; }
            public bool insertSeparatorBefore { get; set; }
        }

        [Test]
        public void LiveContract_ResolvesTheNativeAddMenuExtensionSurface()
        {
            if (!PackageManagerUnityVersionSupport.IsCurrentVersionSupported)
            {
                Assert.That(
                    PackageManagerGitSubmoduleInstallMenu.IsSupportedContract(),
                    Is.False);
                return;
            }

            Type rootType = PackageManagerSubmoduleHarmonyPatch.FindLoadedType(
                PackageManagerGitSubmoduleInstallMenu
                    .PackageManagerWindowRootTypeName);

            Assert.That(rootType, Is.Not.Null);
            PropertyInfo addMenu = PackageManagerGitSubmoduleInstallMenu
                .FindAddMenuProperty(rootType);
            Assert.That(addMenu, Is.Not.Null);
            Assert.That(addMenu.PropertyType.FullName, Is.EqualTo(MenuInterfaceTypeName));

            MethodInfo addDropdownItem = PackageManagerGitSubmoduleInstallMenu
                .FindAddDropdownItemMethod(addMenu.PropertyType);
            Assert.That(addDropdownItem, Is.Not.Null);
            Assert.That(addDropdownItem.IsStatic, Is.False);
            Assert.That(addDropdownItem.GetParameters(), Is.Empty);
            Assert.That(
                addDropdownItem.ReturnType.FullName,
                Is.EqualTo(MenuItemInterfaceTypeName));
            Assert.That(
                PackageManagerGitSubmoduleInstallMenu.IsSupportedContract(),
                Is.True);
        }

        [Test]
        public void ConfigureItem_SetsNativeMenuPresentationAndInvokesCallback()
        {
            var item = new FakeMenuItem
            {
                action = null,
                text = "Old text",
                priority = -1,
                visible = false,
                enabled = false,
                insertSeparatorBefore = false,
                isChecked = true
            };
            int invocationCount = 0;
            Action callback = () => invocationCount++;

            bool configured = PackageManagerGitSubmoduleInstallMenu
                .TryConfigureItem(
                    item,
                    callback,
                    out PackageManagerGitSubmoduleInstallMenu.ItemProperties properties);

            Assert.That(configured, Is.True);
            Assert.That(properties.IsComplete, Is.True);
            Assert.That(
                item.text,
                Is.EqualTo(
                    L10n.Tr(PackageManagerGitSubmoduleInstallMenu.MenuText)));
            Assert.That(
                PackageManagerGitSubmoduleInstallMenu.MenuText,
                Is.EqualTo("Install package as Git Submodule..."));
            Assert.That(item.priority, Is.EqualTo(100));
            Assert.That(item.insertSeparatorBefore, Is.True);
            Assert.That(item.isChecked, Is.False);
            Assert.That(item.enabled, Is.True);
            Assert.That(item.visible, Is.True);
            Assert.That(item.action, Is.SameAs(callback));

            item.action.Invoke();
            Assert.That(invocationCount, Is.EqualTo(1));
        }

        [Test]
        public void ConfigureItem_IncompleteShapeFailsClosed()
        {
            var item = new IncompleteMenuItem();

            bool configured = PackageManagerGitSubmoduleInstallMenu
                .TryConfigureItem(
                    item,
                    () => { },
                    out PackageManagerGitSubmoduleInstallMenu.ItemProperties properties);

            Assert.That(configured, Is.False);
            Assert.That(properties.IsComplete, Is.False);
            Assert.That(item.action, Is.Null);
            Assert.That(item.visible, Is.False);
            Assert.That(item.enabled, Is.False);
        }

        [Test]
        public void LiveContract_ResolvesConcreteRemovalMethod()
        {
            Type menuType = PackageManagerSubmoduleHarmonyPatch.FindLoadedType(
                ConcreteMenuTypeName);
            Type itemType = PackageManagerSubmoduleHarmonyPatch.FindLoadedType(
                ConcreteMenuItemTypeName);

            Assert.That(menuType, Is.Not.Null);
            Assert.That(itemType, Is.Not.Null);
            MethodInfo remove = PackageManagerGitSubmoduleInstallMenu
                .FindRemoveMethod(menuType, itemType);
            Assert.That(remove, Is.Not.Null);
            Assert.That(remove.IsStatic, Is.False);
            Assert.That(remove.ReturnType, Is.EqualTo(typeof(void)));
            Assert.That(remove.GetParameters(), Has.Length.EqualTo(1));
            Assert.That(
                remove.GetParameters()[0].ParameterType,
                Is.EqualTo(itemType));
        }

        [Test]
        public void InstallAndRelease_RepeatedUnsupportedRootRemainNoOps()
        {
            int installedBefore =
                PackageManagerGitSubmoduleInstallMenu.InstalledRootCount;
            var unsupportedRoot = new VisualElement();

            Assert.That(
                PackageManagerGitSubmoduleInstallMenu.InstallForRoot(
                    unsupportedRoot),
                Is.False);
            Assert.That(
                PackageManagerGitSubmoduleInstallMenu.InstallForRoot(
                    unsupportedRoot),
                Is.False);
            PackageManagerGitSubmoduleInstallMenu.ReleaseForRoot(
                unsupportedRoot);
            PackageManagerGitSubmoduleInstallMenu.ReleaseForRoot(
                unsupportedRoot);

            Assert.That(
                PackageManagerGitSubmoduleInstallMenu.InstalledRootCount,
                Is.EqualTo(installedBefore));
        }

        [Test]
        public void InstallForRoot_RepeatedTrackedRootDoesNotDuplicateNativeItem()
        {
            if (!TryGetTrackedEntry(
                    out object root,
                    out object menu,
                    out object item))
            {
                Assert.Ignore(
                    "No Package Manager root is currently tracked by the live host.");
                return;
            }

            int rootsBefore =
                PackageManagerGitSubmoduleInstallMenu.InstalledRootCount;
            int occurrencesBefore = CountItemOccurrences(menu, item);
            Assert.That(occurrencesBefore, Is.EqualTo(1));

            Assert.That(
                PackageManagerGitSubmoduleInstallMenu.InstallForRoot(
                    root),
                Is.True);
            Assert.That(
                PackageManagerGitSubmoduleInstallMenu.InstallForRoot(
                    root),
                Is.True);

            Assert.That(
                PackageManagerGitSubmoduleInstallMenu.InstalledRootCount,
                Is.EqualTo(rootsBefore));
            Assert.That(CountItemOccurrences(menu, item), Is.EqualTo(1));
        }

        private static bool TryGetTrackedEntry(
            out object root,
            out object menu,
            out object item)
        {
            root = null;
            menu = null;
            item = null;

            FieldInfo entriesField = typeof(PackageManagerGitSubmoduleInstallMenu)
                .GetField("EntriesByRoot", AnyStatic);
            if (!(entriesField?.GetValue(null) is IEnumerable entries))
                return false;

            foreach (object pair in entries)
            {
                Type pairType = pair?.GetType();
                root = pairType?.GetProperty("Key", AnyInstance)
                    ?.GetValue(pair, null);
                object entry = pairType?.GetProperty("Value", AnyInstance)
                    ?.GetValue(pair, null);
                Type entryType = entry?.GetType();
                menu = entryType?.GetProperty("Menu", AnyInstance)
                    ?.GetValue(entry, null);
                item = entryType?.GetProperty("Item", AnyInstance)
                    ?.GetValue(entry, null);
                return root != null && menu != null && item != null;
            }

            return false;
        }

        private static int CountItemOccurrences(object menu, object item)
        {
            FieldInfo itemsField = menu?.GetType().GetField(
                "m_DropdownItems",
                AnyInstance);
            if (!(itemsField?.GetValue(menu) is IEnumerable items))
                return -1;

            int count = 0;
            foreach (object candidate in items)
            {
                if (ReferenceEquals(candidate, item))
                    count++;
            }

            return count;
        }
    }
}
