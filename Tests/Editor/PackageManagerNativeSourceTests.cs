using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace MartinCalander.GitSubmoduleManager.Editor.Tests
{
    [TestFixture]
    [Category("PackageManagerCompatibility")]
    [Category("PackageSourceFeatures")]
    public sealed class PackageManagerNativeSourceTests
    {
        [TestCase("6000.3.22f1", true)]
        [TestCase("6000.5.0f1", true)]
        [TestCase("6000.5.10f1", true)]
        [TestCase("6000.3.21f1", false)]
        [TestCase("6000.3.23f1", false)]
        [TestCase("6000.4.0f1", false)]
        [TestCase("6000.6.0f1", true)]
        [TestCase("6000.6.1f1", false)]
        [TestCase("6000.6.2f1", true)]
        [TestCase("6000.6.3f1", false)]
        [TestCase("6000.6.2b1", false)]
        [TestCase("6000.6.2f2", false)]
        [TestCase("6000.6.02f1", false)]
        [TestCase("6000.6.2f1c1", false)]
        [TestCase("6000.6.0b1", false)]
        [TestCase("6000.6.0f2", false)]
        [TestCase("6000.7.0a4", false)]
        [TestCase("6000.7.0b1", true)]
        [TestCase("6000.7.0b0", false)]
        [TestCase("6000.7.0b2", true)]
        [TestCase("6000.7.0b3", false)]
        [TestCase("6000.7.0b02", false)]
        [TestCase("6000.7.0b2c1", false)]
        [TestCase(" 6000.7.0b2", false)]
        [TestCase("6000.7.0b2 ", false)]
        [TestCase("6000.7.1b1", false)]
        [TestCase("6000.7.0b01", false)]
        [TestCase("6000.07.0b1", false)]
        [TestCase("6000.7.00b1", false)]
        [TestCase("6000.7.0b1c1", false)]
        [TestCase(" 6000.7.0b1", false)]
        [TestCase("6000.7.0b1 ", false)]
        [TestCase("6000.7.0f1", false)]
        [TestCase("7000.0.0a7", true)]
        [TestCase("7000.0.0a6", false)]
        [TestCase("7000.0.0a8", true)]
        [TestCase("7000.0.0a9", true)]
        [TestCase("7000.0.0a10", true)]
        [TestCase("7000.0.0a11", false)]
        [TestCase("7000.0.0a010", false)]
        [TestCase("7000.0.0a10c1", false)]
        [TestCase(" 7000.0.0a10", false)]
        [TestCase("7000.0.0a10 ", false)]
        [TestCase("7000.0.0a09", false)]
        [TestCase("7000.0.0a9c1", false)]
        [TestCase(" 7000.0.0a9", false)]
        [TestCase("7000.0.0a9 ", false)]
        [TestCase("7000.0.0a08", false)]
        [TestCase("7000.0.0a8c1", false)]
        [TestCase(" 7000.0.0a8", false)]
        [TestCase("7000.0.0a8 ", false)]
        [TestCase("7000.0.1a7", false)]
        [TestCase("7000.1.0a7", false)]
        [TestCase("7000.0.0b7", false)]
        [TestCase("7000.0.0f1", false)]
        [TestCase("7000.0.0a07", false)]
        [TestCase("07000.0.0a7", false)]
        [TestCase("7000.00.0a7", false)]
        [TestCase("7000.0.00a7", false)]
        [TestCase("7000.0.0a7c1", false)]
        [TestCase("7000.0.0A7", false)]
        [TestCase(" 7000.0.0a7", false)]
        [TestCase("7000.0.0a7 ", false)]
        [TestCase("6000.5.0f2", false)]
        [TestCase("6000.5.0f01", false)]
        [TestCase("06000.5.0f1", false)]
        [TestCase("6000.5.0b1", false)]
        [TestCase("6000.5.0f1c1", false)]
        [TestCase(" 6000.5.0f1", false)]
        [TestCase("6000.5f1", false)]
        [TestCase("", false)]
        [TestCase(null, false)]
        public void RuntimeSupportGate_AcceptsOnlyValidatedEditors(
            string unityVersion,
            bool expected)
        {
            Assert.That(
                PackageManagerUnityVersionSupport.IsSupported(unityVersion),
                Is.EqualTo(expected));
        }

        [Test]
        public void InstalledGitHubSource_UsesTheCurrentNativePageContract()
        {
            Assert.That(PackageManagerUnityVersionSupport.IsCurrentVersionSupported,
                Is.True, "This test run is diagnostic on Unity " + Application.unityVersion);
            Assert.That(PackageManagerSubmoduleNativePage.IsSupportedContract(out string error),
                Is.True, error);
            Assert.That(PackageManagerSubmoduleNativePage.GetSidebarRefreshTarget(), Is.Not.Null);
            Assert.That(PackageManagerSubmoduleNativePage.GetNativeRefreshTarget(), Is.Not.Null);
            Assert.That(PackageManagerSubmoduleHarmonyPatch.IsPatched(
                PackageManagerSubmoduleNativePage.GetSidebarRefreshTarget()), Is.True);
            Assert.That(PackageManagerSubmoduleHarmonyPatch.IsSidebarRepairInstalled, Is.True);
        }

        [Test]
        public void NativePageArguments_UseInstalledMembershipWithoutDiscoveryFilters()
        {
            object args = PackageManagerSubmoduleNativePage.CreateExtensionPageArgs();
            Assert.That(ReadField(args, "name"),
                Is.EqualTo(PackageManagerSubmoduleNativePage.ExtensionPageName));
            Assert.That(ReadField(args, "displayName"), Is.EqualTo("GitHub"));
            Assert.That(ReadField(args, "supportedStatusFilters"), Is.Empty);
            Assert.That(ReadField(args, "supportedSortOptions"), Has.Length.EqualTo(2));
            Assert.That(ReadField(args, "refreshOptions").ToString(), Is.EqualTo("UpmList"));

            var predicate = (Delegate)ReadField(args, "filter");
            Assert.That(predicate.Method.DeclaringType, Is.EqualTo(typeof(PackageManagerPackageStatus)));
            Assert.That(predicate.Method.Name, Is.EqualTo("IsGitHubPackage"));
            Assert.That(predicate.DynamicInvoke(new object[] { null }), Is.False);
        }

        [Test]
        public void NativePageArguments_ReuseDelegatesButDoNotShareMutableArguments()
        {
            object first = PackageManagerSubmoduleNativePage.CreateExtensionPageArgs();
            object second = PackageManagerSubmoduleNativePage.CreateExtensionPageArgs();
            var firstSort = (Array)ReadField(first, "supportedSortOptions");
            var secondSort = (Array)ReadField(second, "supportedSortOptions");
            firstSort.SetValue(firstSort.GetValue(1), 0);
            first.GetType().GetField("name").SetValue(first, "another-extension");

            Assert.That(second, Is.Not.SameAs(first));
            Assert.That(secondSort, Is.Not.SameAs(firstSort));
            Assert.That(secondSort.GetValue(0).ToString(), Is.EqualTo("NameAsc"));
            Assert.That(ReadField(second, "name"),
                Is.EqualTo(PackageManagerSubmoduleNativePage.ExtensionPageName));
            Assert.That(ReadField(second, "filter"), Is.SameAs(ReadField(first, "filter")));
            Assert.That(ReadField(second, "getGroupName"), Is.SameAs(ReadField(first, "getGroupName")));
        }

        [Test]
        public void ValidatedNativeContract_DoesNotAllocateArgumentsOnRepeatedChecks()
        {
            Assert.That(PackageManagerSubmoduleNativePage.IsSupportedContract(out string error), Is.True, error);
            long start = GC.GetAllocatedBytesForCurrentThread();
            bool supported = true;
            for (int index = 0; index < 100; index++)
                supported &= PackageManagerSubmoduleNativePage.IsSupportedContract(out error);
            long bytes = GC.GetAllocatedBytesForCurrentThread() - start;

            Assert.That(supported, Is.True, error);
            Assert.That(bytes, Is.LessThan(4096), "Warm compatibility checks must not recreate native arguments.");
        }

        [Test]
        public void AttachedNativePage_ReusesValidAttachmentWithoutRebuildingTheSidebar()
        {
            var fixture = new AttachmentFixture();
            using (fixture.Page)
            {
                Assert.That(fixture.Page.TryAttach(out string error), Is.True, error);
                long start = GC.GetAllocatedBytesForCurrentThread();
                bool attached = true;
                for (int index = 0; index < 100; index++)
                    attached &= fixture.Page.TryAttach(out error);
                long bytes = GC.GetAllocatedBytesForCurrentThread() - start;

                Assert.That(attached, Is.True, error);
                Assert.That(fixture.Row.name, Is.EqualTo("existing-row"));
                Assert.That(fixture.Row.parent, Is.SameAs(fixture.Sources));
                Assert.That(bytes, Is.LessThan(16384), "An attached page must not recreate native arguments or traverse the sidebar.");
            }
        }

        [TestCase("row")]
        [TestCase("sidebar")]
        [TestCase("manager")]
        [TestCase("page")]
        public void RecycledNativeState_DoesNotAcceptThePreviousAttachment(string recycled)
        {
            var fixture = new AttachmentFixture();
            using (fixture.Page)
            {
                if (recycled == "row")
                    fixture.Row.RemoveFromHierarchy();
                else if (recycled == "sidebar")
                    fixture.Sidebar.RemoveFromHierarchy();
                else if (recycled == "manager")
                    fixture.Root.pageManager = new NativeManagerStub();
                else
                    fixture.Root.pageManager.orderedExtensionPages = new[] { new NativePageStub(
                        PackageManagerSubmoduleNativePage.ExtensionPageId) };

                Assert.That(fixture.Page.TryAttach(out string error), Is.False);
                Assert.That(error, Is.Not.Empty);
            }
        }

        [UnityTest]
        public IEnumerator DetachedNativeRow_RequestsRepairAndDisposalRemovesTheCallback()
        {
            var fixture = new AttachmentFixture();
            var window = ScriptableObject.CreateInstance<NativeSourceTestWindow>();
            int invalidations = 0;
            fixture.Page.AttachmentInvalidated += () => invalidations++;
            try
            {
                window.ShowUtility();
                window.rootVisualElement.Add(fixture.Root);
                yield return null;
                Assert.That(fixture.Root.panel, Is.Not.Null);

                fixture.Row.RemoveFromHierarchy();
                Assert.That(invalidations, Is.EqualTo(1));
                fixture.Sources.Add(fixture.Row);
                fixture.TrackRow();
                fixture.Page.Dispose();
                fixture.Row.RemoveFromHierarchy();
                Assert.That(invalidations, Is.EqualTo(1), "Closed sessions must not schedule repair.");
                Assert.That(fixture.Page.TryAttach(out _), Is.False);
            }
            finally
            {
                fixture.Page.Dispose();
                window.Close();
            }
        }

        [Test]
        public void NativeSidebarRefresh_InvalidatesAnAttachedRowOnlyForItsOwnSidebar()
        {
            var fixture = new AttachmentFixture();
            int invalidations = 0;
            fixture.Page.AttachmentInvalidated += () => invalidations++;
            using (fixture.Page)
            {
                PackageManagerSubmoduleNativePage.NotifySidebarRefreshed(new VisualElement());
                Assert.That(invalidations, Is.Zero);
                Assert.That(fixture.Page.TryAttach(out string error), Is.True, error);
                Assert.That(fixture.Page.IsAttached, Is.True);

                fixture.Sidebar.Add(new VisualElement { name = "new-native-extension-row" });
                PackageManagerSubmoduleNativePage.NotifySidebarRefreshed(fixture.Sidebar);
                Assert.That(invalidations, Is.EqualTo(1));
                Assert.That(fixture.Page.IsAttached, Is.False,
                    "Recreating native rows must invalidate the previous attachment.");
                Assert.That(fixture.Row.parent, Is.SameAs(fixture.Sources));
                Assert.That(fixture.Page.TryAttach(out _), Is.False,
                    "A still-attached row must not bypass repair after native row recreation.");
            }
            PackageManagerSubmoduleNativePage.NotifySidebarRefreshed(fixture.Sidebar);
            Assert.That(invalidations, Is.EqualTo(1), "Closed sessions must unsubscribe from sidebar notifications.");
        }

        [Test]
        public void NativeSidebarRefresh_RearmsAttachmentBeforeTheFirstRowExists()
        {
            var root = new VisualElement();
            using var page = new PackageManagerSubmoduleNativePage(root);
            int invalidations = 0;
            page.AttachmentInvalidated += () => invalidations++;
            var sidebar = new VisualElement();

            PackageManagerSubmoduleNativePage.NotifySidebarRefreshed(sidebar);
            Assert.That(invalidations, Is.Zero, "A different window must not start retries.");
            root.Add(sidebar);
            PackageManagerSubmoduleNativePage.NotifySidebarRefreshed(sidebar);
            Assert.That(invalidations, Is.EqualTo(1),
                "Late sidebar construction must start retries even before the first attachment.");
        }

        [Test]
        public void NativeSidebarRefresh_RearmsMissingRowsAndRecreatedSidebars()
        {
            var fixture = new AttachmentFixture();
            using (fixture.Page)
            {
                int invalidations = 0;
                fixture.Page.AttachmentInvalidated += () => invalidations++;
                fixture.StopTrackingRow();
                PackageManagerSubmoduleNativePage.NotifySidebarRefreshed(fixture.Sidebar);
                Assert.That(invalidations, Is.EqualTo(1),
                    "An assigned sidebar must restart retries when no row is tracked.");

                fixture.Sidebar.RemoveFromHierarchy();
                var replacement = new VisualElement();
                fixture.Root.Add(replacement);
                PackageManagerSubmoduleNativePage.NotifySidebarRefreshed(replacement);
                Assert.That(invalidations, Is.EqualTo(2),
                    "A new sidebar in the same root must restart retries after recreation.");
            }
        }

        [UnityTest]
        public IEnumerator NativeSidebarRecreation_RepairsGitHubSourceWithoutDuplicateOrSubmodulesRows()
        {
            Type windowType = typeof(UnityEditor.PackageManager.UI.IPackageManagerExtension).Assembly
                .GetType(GitSubmoduleManagerPackageManagerHost.WindowTypeName);
            EditorWindow window = null;
            foreach (EditorWindow candidate in Resources.FindObjectsOfTypeAll<EditorWindow>())
                if (candidate.GetType() == windowType)
                    window = candidate;
            bool ownsWindow = window == null;
            window = window != null ? window : EditorWindow.GetWindow(windowType);
            PackageManagerSubmoduleNativePage page = null;
            try
            {
                VisualElement root = null;
                for (int attempt = 0; attempt < 20 && root == null; attempt++)
                {
                    yield return null;
                    root = PackageManagerSubmoduleHarmonyPatch.Read(window, "m_Root") as VisualElement;
                }
                Assert.That(root, Is.Not.Null);
                page = new PackageManagerSubmoduleNativePage(root);
                Assert.That(page.TryAttach(out string error), Is.True, error);
                Assert.That(page.IsAttached, Is.True);
                VisualElement sidebar = FindElement(root, PackageManagerSubmoduleNativePage.SidebarTypeName);
                Assert.That(sidebar, Is.Not.Null);
                var rows = new List<VisualElement>();
                CollectRows(sidebar, rows, PackageManagerSubmoduleNativePage.ExtensionPageId);
                Assert.That(rows, Has.Count.EqualTo(1));
                bool repairRequested = false;
                page.AttachmentInvalidated += () => repairRequested = true;

                PackageManagerSubmoduleNativePage.GetSidebarRefreshTarget().Invoke(sidebar, null);
                Assert.That(repairRequested, Is.True);
                VisualElement currentSources = (sidebar.GetType().GetMethod("GetRow",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    null, new[] { typeof(string) }, null).Invoke(sidebar, new object[] { "MyAssets" }) as VisualElement)?.parent;
                Assert.That(currentSources, Is.Not.Null);
                Assert.That(page.TryAttach(out error), Is.True, error);
                rows.Clear();
                CollectRows(sidebar, rows, PackageManagerSubmoduleNativePage.ExtensionPageId);
                Assert.That(rows, Has.Count.EqualTo(1));
                Assert.That(rows[0].parent, Is.SameAs(currentSources));
                rows.Clear();
                CollectRows(sidebar, rows, "Extension/git-submodule-manager-submodules");
                Assert.That(rows, Is.Empty, "Submodules filtering must not create a sidebar page.");
            }
            finally
            {
                page?.Dispose();
                if (ownsWindow && window != null)
                    window.Close();
            }
        }

        [UnityTest]
        public IEnumerator NativeSidebarAttach_ObservesWindowWithoutSinglePackageSelection()
        {
            const BindingFlags instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            Type windowType = typeof(UnityEditor.PackageManager.UI.IPackageManagerExtension).Assembly
                .GetType(GitSubmoduleManagerPackageManagerHost.WindowTypeName);
            EditorWindow window = null;
            foreach (EditorWindow candidate in Resources.FindObjectsOfTypeAll<EditorWindow>())
                if (candidate.GetType() == windowType)
                    window = candidate;
            bool ownsWindow = window == null;
            window = window != null ? window : EditorWindow.GetWindow(windowType);
            VisualElement root = null;
            VisualElement parent = null;
            object manager = null;
            object originalPage = null;
            object inProject = null;
            string originalSearch = null;
            string[] originalSelection = null;
            try
            {
                for (int attempt = 0; attempt < 20 && root == null; attempt++)
                {
                    yield return null;
                    root = PackageManagerSubmoduleHarmonyPatch.Read(window, "m_Root") as VisualElement;
                }
                Assert.That(root, Is.Not.Null);
                manager = PackageManagerSubmoduleHarmonyPatch.Read(root, "m_PageManager");
                Assert.That(manager, Is.Not.Null);
                originalPage = PackageManagerSubmoduleHarmonyPatch.Read(manager, "activePage");
                foreach (MethodInfo method in manager.GetType().GetMethods(instance))
                {
                    ParameterInfo[] parameters = method.GetParameters();
                    if (method.Name == "GetPage" && !method.IsGenericMethod &&
                        parameters.Length == 1 && parameters[0].ParameterType == typeof(string))
                    {
                        inProject = method.Invoke(manager, new object[] { "InProject" });
                        break;
                    }
                }
                Assert.That(inProject, Is.Not.Null);
                originalSearch = (string)PackageManagerSubmoduleHarmonyPatch.Read(inProject, "searchText");
                originalSelection = ReadSelection(inProject);
                PropertyInfo activePage = manager.GetType().GetProperty("activePage", instance);
                PropertyInfo searchText = inProject.GetType().GetProperty("searchText", instance);
                activePage.SetValue(manager, inProject);
                GitSubmoduleManagerPackageManagerHost.ObserveRoot(root);

                for (int selectionCount = 0; selectionCount <= 2; selectionCount += 2)
                {
                    searchText.SetValue(inProject, selectionCount == 0 ?
                        "no-package-matches-" + Guid.NewGuid().ToString("N") : string.Empty);
                    var selection = new List<string>();
                    if (selectionCount == 2)
                    {
                        foreach (object state in (IEnumerable)PackageManagerSubmoduleHarmonyPatch.Read(inProject, "visualStates"))
                        {
                            string id = PackageManagerSubmoduleHarmonyPatch.Read(state, "itemUniqueId") as string ??
                                        PackageManagerSubmoduleHarmonyPatch.Read(state, "packageUniqueId") as string;
                            if (!string.IsNullOrEmpty(id))
                                selection.Add(id);
                            if (selection.Count == 2)
                                break;
                        }
                        Assert.That(selection, Has.Count.EqualTo(2), "The project needs two installed packages for multi-selection coverage.");
                    }
                    SetSelection(inProject, selection.ToArray());
                    Assert.That(ReadSelection(inProject), Has.Length.EqualTo(selectionCount));
                    Assert.That(root.Q<Label>("git-submodule-package-status"), Is.Null,
                        "The legacy details label must be detached for this regression.");
                    int sessions = GitSubmoduleManagerPackageManagerHost.SessionCount;
                    parent = root.parent;
                    int position = parent.IndexOf(root);
                    root.RemoveFromHierarchy();
                    Assert.That(GitSubmoduleManagerPackageManagerHost.SessionCount, Is.EqualTo(sessions - 1));
                    parent.Insert(position, root);
                    Assert.That(GitSubmoduleManagerPackageManagerHost.SessionCount, Is.EqualTo(sessions),
                        "Native sidebar attachment must observe a window even without a single selected package.");
                    VisualElement sidebar = FindElement(root, PackageManagerSubmoduleNativePage.SidebarTypeName);
                    PackageManagerSubmoduleNativePage.GetSidebarRefreshTarget().Invoke(sidebar, null);
                    Assert.That(GitSubmoduleManagerPackageManagerHost.SessionCount, Is.EqualTo(sessions),
                        "A second sidebar refresh must reuse the existing session.");
                    typeof(PackageManagerSubmoduleHarmonyPatch).GetMethod("SidebarRefreshPostfix",
                        BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new object[] { new VisualElement() });
                    Assert.That(GitSubmoduleManagerPackageManagerHost.SessionCount, Is.EqualTo(sessions),
                        "An unrelated sidebar must not create a session.");
                }
            }
            finally
            {
                if (root != null && root.parent == null && parent != null)
                    parent.Add(root);
                if (inProject != null && originalSelection != null)
                {
                    inProject.GetType().GetProperty("searchText", instance).SetValue(inProject, originalSearch);
                    SetSelection(inProject, originalSelection);
                }
                if (manager != null && originalPage != null)
                    manager.GetType().GetProperty("activePage", instance).SetValue(manager, originalPage);
                if (ownsWindow && window != null)
                    window.Close();
                else if (root != null)
                    GitSubmoduleManagerPackageManagerHost.ObserveRoot(root);
            }
        }

        private static string[] ReadSelection(object page)
        {
            object selection = page.GetType().GetMethod("GetSelection",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).Invoke(page, null);
            var result = new List<string>();
            foreach (object id in (IEnumerable)selection)
                result.Add((string)id);
            return result.ToArray();
        }

        private static void SetSelection(object page, string[] ids)
        {
            page.GetType().GetMethod("SetNewSelection",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null, new[] { typeof(IEnumerable<string>), typeof(bool) }, null)
                .Invoke(page, new object[] { ids, true });
        }

        [TestCase("Extension/git-submodule-manager", true)]
        [TestCase("Extension/git-submodule-manager-submodules", false)]
        [TestCase("Extension/another-package", false)]
        [TestCase("InProject", false)]
        [TestCase("Extension/Git-Submodule-Manager", false)]
        [TestCase(null, false)]
        public void GitHubPageIdentity_DoesNotClaimAnotherNativePage(string id, bool expected)
        {
            var page = new NativePageStub(id);
            Assert.That(PackageManagerSubmoduleNativePage.IsGitHubPage(page), Is.EqualTo(expected));
            Assert.That(PackageManagerSubmoduleNativePage.IsOwnedPage(page), Is.EqualTo(expected));
            Assert.That(PackageManagerSubmoduleNativePage.IsGitHubPage(null), Is.False);
            Assert.That(PackageManagerSubmoduleNativePage.IsOwnedPage(null), Is.False);
        }

        private static VisualElement FindElement(VisualElement element, string typeName)
        {
            if (element.GetType().FullName == typeName)
                return element;
            foreach (VisualElement child in element.Children())
            {
                VisualElement found = FindElement(child, typeName);
                if (found != null)
                    return found;
            }
            return null;
        }

        private static void CollectRows(VisualElement element, List<VisualElement> rows, string pageId)
        {
            if (element.GetType().FullName == PackageManagerSubmoduleNativePage.SidebarRowTypeName &&
                (string)PackageManagerSubmoduleHarmonyPatch.Read(element, "pageId") ==
                pageId)
                rows.Add(element);
            foreach (VisualElement child in element.Children())
                CollectRows(child, rows, pageId);
        }

        private static object ReadField(object instance, string name)
        {
            FieldInfo field = instance.GetType().GetField(name,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null, "Missing native page argument " + name);
            return field.GetValue(instance);
        }

        private sealed class NativeRootStub : VisualElement
        {
            public NativeManagerStub pageManager { get; set; } = new();
        }

        private sealed class NativeManagerStub
        {
            public NativePageStub[] orderedExtensionPages { get; set; } =
                new[] { new NativePageStub(PackageManagerSubmoduleNativePage.ExtensionPageId) };
        }

        private sealed class AttachmentFixture
        {
            internal readonly NativeRootStub Root = new();
            internal readonly VisualElement Sidebar = new();
            internal readonly VisualElement Sources = new();
            internal readonly VisualElement Row = new() { name = "existing-row" };
            internal readonly PackageManagerSubmoduleNativePage Page;

            internal AttachmentFixture()
            {
                Root.Add(Sidebar);
                Sidebar.Add(Sources);
                Sources.Add(Row);
                Page = new PackageManagerSubmoduleNativePage(Root);
                Set("pageManager", Root.pageManager);
                Set("page", Root.pageManager.orderedExtensionPages[0]);
                Set("sidebar", Sidebar);
                Set("group", Sources);
                TrackRow();
            }

            internal void TrackRow() => typeof(PackageManagerSubmoduleNativePage)
                .GetMethod("TrackRow", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(Page, new object[] { Row });

            internal void StopTrackingRow() => typeof(PackageManagerSubmoduleNativePage)
                .GetMethod("StopTrackingRow", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(Page, null);

            private void Set(string name, object value) => typeof(PackageManagerSubmoduleNativePage)
                .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(Page, value);
        }

        public sealed class NativeSourceTestWindow : EditorWindow { }

        private sealed class NativePageStub
        {
            public string id { get; }
            internal NativePageStub(string id) { this.id = id; }
        }
    }
}
