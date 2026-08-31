using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine.UIElements;

namespace MartinCalander.GitSubmoduleManager.Editor.Tests
{
    [Parallelizable(ParallelScope.None)]
    public sealed class PackageManagerSubmoduleManageMenuTests
    {
        [Test]
        public void LiveContract_ResolvesUnityManageDropdownAndMenuStorage()
        {
            if (!PackageManagerUnityVersionSupport.IsCurrentVersionSupported)
            {
                Assert.That(
                    PackageManagerSubmoduleManageMenu.IsSupportedContract(),
                    Is.False);
                return;
            }

            Assert.That(
                PackageManagerSubmoduleManageMenu.IsSupportedContract(),
                Is.True);
        }

        [Test]
        public void Apply_PreservesNativeRemoveAndAddsConversionExactlyOnce()
        {
            var menu = new GenericDropdownMenu();
            int nativeRemoveCount = 0;
            int uninstallCount = 0;
            int conversionCount = 0;
            menu.AddItem("Open Manifest", false, () => { });
            menu.AddItem("Remove", false, () => nativeRemoveCount++);
            menu.AddItem("Update", false, () => { });
            var removalTexts = new HashSet<string> { "Remove" };

            for (int index = 0; index < 2; index++)
            {
                Assert.That(
                    PackageManagerSubmoduleManageMenu.ApplyToMenu(
                        menu,
                        removalTexts,
                        true,
                        "Convert safely",
                        () => conversionCount++,
                        true,
                        "Uninstall safely",
                        () => uninstallCount++),
                    Is.True);
            }

            Assert.That(
                PackageManagerSubmoduleManageMenu.GetItemNamesForTests(menu),
                Is.EqualTo(new[]
                {
                    "Open Manifest",
                    "Remove",
                    "Update",
                    L10n.Tr(PackageManagerSubmoduleManageMenu.ConvertText)
                }));
            Assert.That(
                PackageManagerSubmoduleManageMenu.InvokeItemForTests(
                    menu,
                    "Remove"),
                Is.True);
            Assert.That(nativeRemoveCount, Is.EqualTo(1));
            Assert.That(uninstallCount, Is.Zero);
            Assert.That(
                PackageManagerSubmoduleManageMenu.InvokeItemForTests(
                    menu,
                    L10n.Tr(PackageManagerSubmoduleManageMenu.ConvertText)),
                Is.True);
            Assert.That(conversionCount, Is.EqualTo(1));
        }

        [Test]
        public void Apply_PreservesUnityDisabledRemoveState()
        {
            var menu = new GenericDropdownMenu();
            menu.AddDisabledItem("Remove", false);

            Assert.That(
                PackageManagerSubmoduleManageMenu.ApplyToMenu(
                    menu,
                    new HashSet<string> { "Remove" },
                    true,
                    "Ready",
                    () => { },
                    true,
                    "Ready",
                    () => Assert.Fail("Disabled uninstall invoked.")),
                Is.True);

            Assert.That(
                PackageManagerSubmoduleManageMenu.IsItemEnabledForTests(
                    menu,
                    "Remove"),
                Is.False);
            Assert.That(
                PackageManagerSubmoduleManageMenu.GetItemNamesForTests(menu),
                Does.Not.Contain(
                    L10n.Tr(PackageManagerSubmoduleManageMenu.UninstallText)));
        }

        [Test]
        public void Apply_FallbackThenNativeTransitionLeavesOneNativeRemove()
        {
            var menu = new GenericDropdownMenu();
            int oldSelectionCount = 0;
            int currentSelectionCount = 0;

            Assert.That(
                PackageManagerSubmoduleManageMenu.ApplyToMenu(
                    menu,
                    new HashSet<string>(),
                    true,
                    "Ready",
                    () => { },
                    true,
                    "Ready",
                    () => oldSelectionCount++),
                Is.True);
            menu.AddItem("Remove", false, () => currentSelectionCount++);

            Assert.That(
                PackageManagerSubmoduleManageMenu.ApplyToMenu(
                    menu,
                    new HashSet<string> { "Remove" },
                    true,
                    "Ready",
                    () => { },
                    true,
                    "Ready",
                    () => currentSelectionCount++),
                Is.True);

            IReadOnlyList<string> names =
                PackageManagerSubmoduleManageMenu.GetItemNamesForTests(menu);
            Assert.That(
                names.Count(name => string.Equals(
                    name,
                    "Remove",
                    System.StringComparison.Ordinal)),
                Is.EqualTo(1));
            Assert.That(
                names,
                Does.Not.Contain(
                    L10n.Tr(PackageManagerSubmoduleManageMenu.UninstallText)));
            Assert.That(
                PackageManagerSubmoduleManageMenu.InvokeItemForTests(
                    menu,
                    "Remove"),
                Is.True);
            Assert.That(oldSelectionCount, Is.Zero);
            Assert.That(currentSelectionCount, Is.EqualTo(1));
        }

        [Test]
        public void Apply_DisablesConversionButPreservesNativeRemoveWhenBusy()
        {
            var menu = new GenericDropdownMenu();
            menu.AddItem("Remove", false, () => { });

            Assert.That(
                PackageManagerSubmoduleManageMenu.ApplyToMenu(
                    menu,
                    new HashSet<string> { "Remove" },
                    false,
                    "Busy",
                    () => Assert.Fail("Disabled conversion invoked."),
                    false,
                    "Busy",
                    () => Assert.Fail("Disabled uninstall invoked.")),
                Is.True);

            Assert.That(
                PackageManagerSubmoduleManageMenu.IsItemEnabledForTests(
                    menu,
                    "Remove"),
                Is.True);
            Assert.That(
                PackageManagerSubmoduleManageMenu.IsItemEnabledForTests(
                    menu,
                    L10n.Tr(PackageManagerSubmoduleManageMenu.ConvertText)),
                Is.False);
        }

        [Test]
        public void Apply_DoesNotInventRemovalWhenUnityOmitsNativeRemove()
        {
            var menu = new GenericDropdownMenu();
            int uninstallCount = 0;

            Assert.That(
                PackageManagerSubmoduleManageMenu.ApplyToMenu(
                    menu,
                    new HashSet<string>(),
                    true,
                    "Ready",
                    () => { },
                    true,
                    "Ready",
                    () => uninstallCount++),
                Is.True);

            Assert.That(
                PackageManagerSubmoduleManageMenu.GetItemNamesForTests(menu),
                Does.Not.Contain("Remove"));
            Assert.That(
                PackageManagerSubmoduleManageMenu.GetItemNamesForTests(menu),
                Does.Not.Contain(
                    L10n.Tr(PackageManagerSubmoduleManageMenu.UninstallText)));
            Assert.That(uninstallCount, Is.Zero);
        }

        [Test]
        public void ApplyReadOnly_PreservesNativeActionsAndAddsCurrentConversionExactlyOnce()
        {
            var menu = new GenericDropdownMenu();
            int nativeRemoveCount = 0;
            int oldConversionCount = 0;
            int currentConversionCount = 0;
            menu.AddItem("Update", false, () => { });
            menu.AddItem("Remove", false, () => nativeRemoveCount++);

            Assert.That(
                PackageManagerSubmoduleManageMenu.ApplyReadOnlyToMenu(
                    menu,
                    true,
                    "Ready",
                    () => oldConversionCount++),
                Is.True);
            Assert.That(
                PackageManagerSubmoduleManageMenu.ApplyReadOnlyToMenu(
                    menu,
                    true,
                    "Ready",
                    () => currentConversionCount++),
                Is.True);

            Assert.That(
                PackageManagerSubmoduleManageMenu.GetItemNamesForTests(menu),
                Is.EqualTo(new[]
                {
                    "Update",
                    "Remove",
                    L10n.Tr(
                        PackageManagerSubmoduleManageMenu.ConvertToSubmoduleText)
                }));
            Assert.That(
                PackageManagerSubmoduleManageMenu.InvokeItemForTests(
                    menu,
                    "Remove"),
                Is.True);
            Assert.That(nativeRemoveCount, Is.EqualTo(1));
            Assert.That(
                PackageManagerSubmoduleManageMenu.InvokeItemForTests(
                    menu,
                    L10n.Tr(
                        PackageManagerSubmoduleManageMenu.ConvertToSubmoduleText)),
                Is.True);
            Assert.That(oldConversionCount, Is.Zero);
            Assert.That(currentConversionCount, Is.EqualTo(1));
        }
    }
}
