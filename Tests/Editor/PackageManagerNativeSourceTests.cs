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
                    fixture.Root.pageManager.orderedExtensionPages = new[] { new NativePageStub(PackageManagerSubmoduleNativePage.ExtensionPageId) };

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

                fixture.Sidebar.Add(new VisualElement { name = "new-native-extension-row" });
                PackageManagerSubmoduleNativePage.NotifySidebarRefreshed(fixture.Sidebar);
                Assert.That(invalidations, Is.EqualTo(1));
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
        public IEnumerator NativeSidebarRecreation_RepairsTheSourceWithoutDuplicateRows()
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
                VisualElement sidebar = FindElement(root, PackageManagerSubmoduleNativePage.SidebarTypeName);
                Assert.That(sidebar, Is.Not.Null);
                var rows = new List<VisualElement>();
                CollectGitHubRows(sidebar, rows);
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
                CollectGitHubRows(sidebar, rows);
                Assert.That(rows, Has.Count.EqualTo(1));
                Assert.That(rows[0].parent, Is.SameAs(currentSources));
            }
            finally
            {
                page?.Dispose();
                if (ownsWindow && window != null)
                    window.Close();
            }
        }

        [TestCase("Extension/git-submodule-manager", true)]
        [TestCase("Extension/another-package", false)]
        [TestCase("InProject", false)]
        [TestCase("Extension/Git-Submodule-Manager", false)]
        [TestCase(null, false)]
        public void GitHubPageIdentity_DoesNotClaimAnotherNativePage(string id, bool expected)
        {
            Assert.That(PackageManagerSubmoduleNativePage.IsGitHubPage(new NativePageStub(id)),
                Is.EqualTo(expected));
            Assert.That(PackageManagerSubmoduleNativePage.IsGitHubPage(null), Is.False);
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

        private static void CollectGitHubRows(VisualElement element, List<VisualElement> rows)
        {
            if (element.GetType().FullName == PackageManagerSubmoduleNativePage.SidebarRowTypeName &&
                (string)PackageManagerSubmoduleHarmonyPatch.Read(element, "pageId") ==
                PackageManagerSubmoduleNativePage.ExtensionPageId)
                rows.Add(element);
            foreach (VisualElement child in element.Children())
                CollectGitHubRows(child, rows);
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
                Set("sources", Sources);
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
