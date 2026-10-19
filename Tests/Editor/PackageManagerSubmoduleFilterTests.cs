using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace MartinCalander.GitSubmoduleManager.Editor.Tests
{
    [Category("PackageSourceFeatures")]
    public sealed class PackageManagerSubmoduleFilterTests
    {
        private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        [Test]
        [Category("PackageManagerCompatibility")]
        public void AllPackagesFilter_ValidatesAndPatchesItsIndependentNativeContracts()
        {
            Assert.That(PackageManagerSubmoduleFilter.IsSupportedContract(out string error), Is.True, error);
            Assert.That(PackageManagerSubmoduleHarmonyPatch.IsPatched(
                PackageManagerSubmoduleFilter.GetMembershipTarget()), Is.True);
            Assert.That(PackageManagerSubmoduleHarmonyPatch.IsPatched(
                PackageManagerSubmoduleFilter.GetSearchPageChangedTarget()), Is.True);
        }

        [Test]
        public void AllPackagesFilter_DefaultsOffAndDoesNotCreateAnotherPage()
        {
            using var fixture = new Fixture();
            Assert.That(fixture.Toggle.text, Is.EqualTo("Submodules only"));
            Assert.That(fixture.Toggle.value, Is.False);
            Assert.That(fixture.Filter.IsEnabled, Is.False);
            Assert.That(((IDictionary)Read(fixture.Manager, "m_Pages")).Count, Is.EqualTo(1));
            Assert.That(fixture.Filter.TryAttach(out string error), Is.True, error);
            Assert.That(fixture.Root.Query<ToolbarToggle>(PackageManagerSubmoduleFilter.ToggleElementName).ToList(), Has.Count.EqualTo(1));
        }

        [Test]
        public void EnabledFilter_CombinesVerifiedStatusWithNativeMembership()
        {
            using var fixture = new Fixture();
            var original = fixture.Members();
            fixture.Toggle.value = true;
            foreach (object package in fixture.Packages)
            {
                string id = Read(package, "uniqueId") as string;
                bool expected = original.Contains(id) && PackageManagerPackageStatus.IsSubmodulePackage(package);
                Assert.That(fixture.ShouldInclude(package), Is.EqualTo(expected), id);
                Assert.That(PackageManagerSubmoduleFilter.ApplyNativeFilter(fixture.Page, package, false), Is.False,
                    "Filtering must never add a package excluded by Unity.");
            }
            Assert.That(fixture.Members(), Is.EquivalentTo(original.Where(id =>
                fixture.Packages.Any(p => (string)Read(p, "uniqueId") == id && PackageManagerPackageStatus.IsSubmodulePackage(p)))));
            fixture.Toggle.value = false;
            Assert.That(fixture.Members(), Is.EquivalentTo(original));
        }

        [Test]
        public void FilterDoesNotAffectUntrackedOrUnrelatedPages()
        {
            using var fixture = new Fixture();
            fixture.Toggle.value = true;
            object package = fixture.Packages.First();
            Assert.That(PackageManagerSubmoduleFilter.ApplyNativeFilter(null, package, true), Is.True);
            Assert.That(PackageManagerSubmoduleFilter.ApplyNativeFilter(new PageStub("InProject"), package, true), Is.True);
            Assert.That(PackageManagerSubmoduleFilter.ApplyNativeFilter(new PageStub("Extension/git-submodule-manager"), package, true), Is.True);
            object otherInstance = Activator.CreateInstance(fixture.Page.GetType(), new[] { Read(fixture.Page, "m_PackageDatabase") });
            Assert.That(PackageManagerSubmoduleFilter.IsAllPackagesPage(otherInstance), Is.True);
            Assert.That(PackageManagerSubmoduleFilter.ApplyNativeFilter(otherInstance, package, true), Is.True);
            Assert.That(PackageManagerSubmoduleFilter.ApplyNativeFilter(otherInstance, package, false), Is.False);
        }

        [Test]
        public void FilterPreservesNativeSearchAndSorting()
        {
            using var fixture = new Fixture();
            fixture.Page.GetType().GetProperty("searchText", Instance).SetValue(fixture.Page, " no-match-submodule-filter ");
            string sorting = Read(Read(fixture.Page, "filters"), "sortOption").ToString();
            fixture.Toggle.value = true;
            Assert.That(Read(fixture.Page, "searchText"), Is.EqualTo(" no-match-submodule-filter "));
            Assert.That(Read(Read(fixture.Page, "filters"), "sortOption").ToString(), Is.EqualTo(sorting));
            Assert.That(fixture.VisibleMembers(), Is.Empty);
            fixture.Toggle.value = false;
            Assert.That(fixture.VisibleMembers(), Is.Empty, "Disabling the filter must still respect native search.");
        }

        [Test]
        public void EnablingFilter_ReconcilesASelectionThatBecomesHidden()
        {
            using var fixture = new Fixture();
            object ordinary = fixture.Packages.First(p => fixture.ShouldInclude(p) && !PackageManagerPackageStatus.IsSubmodulePackage(p));
            string id = (string)Read(ordinary, "uniqueId");
            fixture.Page.GetType().GetMethod("SetNewSelection", Instance, null,
                new[] { typeof(IEnumerable<string>), typeof(bool) }, null).Invoke(fixture.Page, new object[] { new[] { id }, false });
            fixture.Toggle.value = true;
            var selected = ((IEnumerable)fixture.Page.GetType().GetMethod("GetSelection", Instance).Invoke(fixture.Page, null)).Cast<string>().ToArray();
            Assert.That(selected, Does.Not.Contain(id));
            Assert.That(selected.All(fixture.VisibleMembers().Contains), Is.True);
        }

        [Test]
        public void MultipleWindows_ShareStateUntilTheLastOwnerCloses()
        {
            using var first = new Fixture();
            using var second = new Fixture(first.Manager, first.Page);
            var original = first.Members();
            first.Toggle.value = true;
            Assert.That(second.Toggle.value, Is.True);
            first.Filter.Dispose();
            Assert.That(second.Filter.IsEnabled, Is.True);
            second.Filter.Dispose();
            Assert.That(PackageManagerSubmoduleFilter.IsEnabledFor(first.Page), Is.False);
            Assert.That(first.Members(), Is.EquivalentTo(original));
            using var reopened = new Fixture(first.Manager, first.Page);
            Assert.That(reopened.Toggle.value, Is.False);
        }

        [Test]
        public void DetachedControl_DoesNotContinueFilteringWithoutAnAttachedOwner()
        {
            using var fixture = new Fixture();
            fixture.Toggle.value = true;
            fixture.Toggle.RemoveFromHierarchy();
            Assert.That(fixture.Filter.IsAttached, Is.False);
            Assert.That(PackageManagerSubmoduleFilter.IsEnabledFor(fixture.Page), Is.False);
            Assert.That(PackageManagerSubmoduleFilter.ApplyNativeFilter(fixture.Page, null, true), Is.True);
            Assert.That(fixture.Filter.TryAttach(out string error), Is.True, error);
            Assert.That(fixture.Toggle.value, Is.False);
        }

        [Test]
        public void SearchPageChanges_HideTheToggleOutsideAllPackages()
        {
            using var fixture = new Fixture();
            Assert.That(fixture.Toggle.ClassListContains("git-submodule-filter-hidden"), Is.False);
            SetField(fixture.Manager, "m_ActivePage", null);
            // Use another exact native page instance so the manager's interface contract stays intact.
            object otherPage = Activator.CreateInstance(fixture.Page.GetType(), new[] { Read(fixture.Page, "m_PackageDatabase") });
            SetField(fixture.Manager, "m_ActivePage", otherPage);
            PackageManagerSubmoduleFilter.NotifySearchPageChanged(fixture.SearchBar);
            Assert.That(fixture.Toggle.ClassListContains("git-submodule-filter-hidden"), Is.True);
            SetField(fixture.Manager, "m_ActivePage", fixture.Page);
            PackageManagerSubmoduleFilter.NotifySearchPageChanged(fixture.SearchBar);
            Assert.That(fixture.Toggle.ClassListContains("git-submodule-filter-hidden"), Is.False);
        }

        [Test]
        public void DisposedFilterCannotReattach()
        {
            using var fixture = new Fixture();
            fixture.Filter.Dispose();
            Assert.That(fixture.Filter.TryAttach(out string error), Is.False);
            Assert.That(error, Does.Contain("closed"));
            Assert.That(fixture.Root.Q<ToolbarToggle>(PackageManagerSubmoduleFilter.ToggleElementName), Is.Null);
        }

        [TestCase("git-submodule-manager-submodules", true)]
        [TestCase("another-extension", false)]
        public void AttachmentMigratesOnlyTheRetiredSubmodulesPage(string extensionName, bool migrates)
        {
            using var fixture = new Fixture();
            fixture.Filter.Dispose();
            object args = PackageManagerSubmoduleNativePage.CreateExtensionPageArgs();
            SetField(args, "name", extensionName);
            Type extensionType = PackageManagerSubmoduleHarmonyPatch.FindType("UnityEditor.PackageManager.UI.Internal.ExtensionPage");
            object oldPage = Activator.CreateInstance(extensionType, new[] { Read(fixture.Page, "m_PackageDatabase"), args });
            SetField(fixture.Manager, "m_ActivePage", oldPage);
            using var reattached = new PackageManagerSubmoduleFilter(fixture.Root);
            Assert.That(reattached.TryAttach(out string error), Is.True, error);
            Assert.That(Read(fixture.Manager, "activePage"), Is.SameAs(migrates ? fixture.Page : oldPage));
            Assert.That(reattached.IsEnabled, Is.False);
        }

        [Test]
        public void FailedNativeRebuild_RestoresUnfilteredMembershipAndReportsTheFailure()
        {
            using var fixture = new Fixture();
            var original = fixture.Members();
            var failure = new RebuildFailure();
            EventInfo rebuild = fixture.Page.GetType().GetEvent("onListRebuild", Instance);
            Delegate callback = Delegate.CreateDelegate(rebuild.EventHandlerType, failure,
                rebuild.EventHandlerType.GetMethod("Invoke").GetParameters().Length == 0 ?
                    nameof(RebuildFailure.WithoutPage) : nameof(RebuildFailure.WithPage));
            rebuild.AddEventHandler(fixture.Page, callback);
            try
            {
                fixture.Toggle.value = true;
                Assert.That(fixture.Toggle.value, Is.False);
                Assert.That(fixture.Members(), Is.EquivalentTo(original));
                Assert.That(fixture.Filter.TryAttach(out string error), Is.False);
                Assert.That(error, Does.Contain("Simulated native rebuild callback failure"));
                Assert.That(fixture.Filter.IsAttached, Is.False);
            }
            finally { rebuild.RemoveEventHandler(fixture.Page, callback); }
        }

        [UnityTest]
        public IEnumerator NarrowNativeSearchBar_KeepsTheWrappedToggleAboveThePackageList()
        {
            Type windowType = PackageManagerSubmoduleHarmonyPatch.FindType("UnityEditor.PackageManager.UI.PackageManagerWindow");
            EditorWindow window = Resources.FindObjectsOfTypeAll<EditorWindow>().FirstOrDefault(candidate => candidate.GetType() == windowType);
            bool ownsWindow = window == null;
            window = window != null ? window : EditorWindow.GetWindow(windowType);
            object manager = null;
            object previousPage = null;
            VisualElement searchBar = null;
            StyleLength previousWidth = default;
            try
            {
                VisualElement root = null;
                for (int attempt = 0; attempt < 50 && root == null; attempt++)
                {
                    yield return null;
                    root = Read(window, "m_Root") as VisualElement;
                }
                Assert.That(root, Is.Not.Null);
                manager = Read(root, "m_PageManager") ?? Read(root, "pageManager");
                previousPage = Read(manager, "activePage");
                MethodInfo getPage = manager.GetType().GetMethods(Instance).Single(m => m.Name == "GetPage" &&
                    !m.IsGenericMethod && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == typeof(string));
                manager.GetType().GetProperty("activePage", Instance).SetValue(manager, getPage.Invoke(manager, new object[] { "InProject" }));
                GitSubmoduleManagerPackageManagerHost.ObserveRoot(root);
                ToolbarToggle toggle = null;
                for (int attempt = 0; attempt < 50 && toggle == null; attempt++)
                {
                    yield return null;
                    toggle = root.Q<ToolbarToggle>(PackageManagerSubmoduleFilter.ToggleElementName);
                }
                Assert.That(toggle, Is.Not.Null);
                searchBar = toggle.parent;
                previousWidth = searchBar.style.width;
                searchBar.style.width = 200;
                yield return null;
                yield return null;
                ToolbarSearchField search = searchBar.Q<ToolbarSearchField>();
                VisualElement packageList = searchBar.parent.Q("mainArea");
                Assert.That(packageList, Is.Not.Null);
                Assert.That(toggle.resolvedStyle.display, Is.EqualTo(DisplayStyle.Flex));
                Assert.That(toggle.worldBound.yMin, Is.GreaterThanOrEqualTo(search.worldBound.yMax - 0.5f),
                    "The narrow search bar must wrap the toggle onto its own row.");
                Assert.That(toggle.worldBound.yMax, Is.LessThanOrEqualTo(searchBar.worldBound.yMax + 0.5f),
                    "The search bar must grow to contain the full wrapped control.");
                Assert.That(toggle.worldBound.yMax, Is.LessThanOrEqualTo(packageList.worldBound.yMin + 0.5f),
                    "The package list must start below the complete filter control.");
            }
            finally
            {
                if (searchBar != null)
                    searchBar.style.width = previousWidth;
                if (manager != null && previousPage != null)
                    manager.GetType().GetProperty("activePage", Instance).SetValue(manager, previousPage);
                if (ownsWindow && window != null)
                    window.Close();
            }
        }

        private sealed class Fixture : IDisposable
        {
            internal readonly RootStub Root = new();
            internal readonly VisualElement SearchBar;
            internal readonly object Manager;
            internal readonly object Page;
            internal readonly object[] Packages;
            internal readonly PackageManagerSubmoduleFilter Filter;
            private readonly SubmoduleFilterTestWindow window;
            internal ToolbarToggle Toggle => Root.Q<ToolbarToggle>(PackageManagerSubmoduleFilter.ToggleElementName);

            internal Fixture(object manager = null, object page = null)
            {
                Type searchType = PackageManagerSubmoduleHarmonyPatch.FindType("UnityEditor.PackageManager.UI.Internal.PackageSearchBar");
                SearchBar = (VisualElement)Activator.CreateInstance(searchType, true);
                object liveManager = Read(SearchBar, "m_PageManager");
                MethodInfo getPage = liveManager.GetType().GetMethods(Instance).Single(m => m.Name == "GetPage" &&
                    !m.IsGenericMethod && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == typeof(string));
                object livePage = getPage.Invoke(liveManager, new object[] { "InProject" });
                object database = Read(livePage, "m_PackageDatabase");
                Page = page ?? Activator.CreateInstance(livePage.GetType(), new[] { database });
                Packages = ((IEnumerable)Read(database, "allPackages")).Cast<object>().ToArray();
                if (manager == null)
                {
#pragma warning disable SYSLIB0050
                    Manager = FormatterServices.GetUninitializedObject(liveManager.GetType());
#pragma warning restore SYSLIB0050
                    FieldInfo pagesField = liveManager.GetType().GetField("m_Pages", Instance);
                    var pages = (IDictionary)Activator.CreateInstance(pagesField.FieldType);
                    pages.Add("InProject", Page);
                    pagesField.SetValue(Manager, pages);
                    SetField(Manager, "m_ActivePage", Page);
                }
                else
                    Manager = manager;
                Root.pageManager = Manager;
                Root.Add(SearchBar);
                Filter = new PackageManagerSubmoduleFilter(Root);
                Assert.That(Filter.TryAttach(out string error), Is.True, error);
                window = ScriptableObject.CreateInstance<SubmoduleFilterTestWindow>();
                window.ShowUtility();
                window.rootVisualElement.Add(Root);
                Assert.That(Toggle.panel, Is.Not.Null, "Value changes must pass through a live UI Toolkit panel.");
                Rebuild();
            }

            internal bool ShouldInclude(object package) => (bool)PackageManagerSubmoduleFilter.GetMembershipTarget().Invoke(Page, new[] { package });
            internal HashSet<string> Members() => States(false);
            internal HashSet<string> VisibleMembers() => States(true);
            private HashSet<string> States(bool visibleOnly) => new(((IEnumerable)Read(Page, "visualStates")).Cast<object>()
                .Where(s => !visibleOnly || Read(s, "visible") is true)
                .Select(s => (string)(Read(s, "itemUniqueId") ?? Read(s, "packageUniqueId"))));

            private void Rebuild()
            {
                MethodInfo method = Page.GetType().GetMethod("Rebuild", Instance, null, new[] { typeof(bool) }, null);
                if (method != null)
                    method.Invoke(Page, new object[] { false });
                else
                    Page.GetType().GetMethod("RebuildVisualStatesAndUpdateVisibilityWithSearchText", Instance).Invoke(Page, null);
            }

            public void Dispose()
            {
                Filter.Dispose();
                if (window != null)
                    window.Close();
            }
        }

        private sealed class RootStub : VisualElement
        {
            public object pageManager { get; set; }
        }

        private sealed class PageStub
        {
            public string id { get; }
            internal PageStub(string id) { this.id = id; }
        }

        private sealed class RebuildFailure
        {
            private bool failed;
            public void WithPage(object _) => WithoutPage();
            public void WithoutPage()
            {
                if (failed)
                    return;
                failed = true;
                throw new InvalidOperationException("Simulated native rebuild callback failure");
            }
        }

        private static object Read(object target, string name) => PackageManagerSubmoduleHarmonyPatch.Read(target, name);

        private static void SetField(object target, string name, object value) => target.GetType().GetField(name, Instance).SetValue(target, value);
    }

    internal sealed class SubmoduleFilterTestWindow : EditorWindow { }
}
