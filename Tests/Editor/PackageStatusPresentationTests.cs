using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEngine.UIElements;

namespace MartinCalander.GitSubmoduleManager.Editor.Tests
{
    [Category("PackageSourceFeatures")]
    public sealed class PackageStatusPresentationTests
    {
        [Test]
        public void CachedMemberReadsPreserveLookupModesAndInheritedMembers()
        {
            var value = new DerivedMemberStub();
            for (int read = 0; read < 2; read++)
            {
                Assert.That(PackageManagerSubmoduleHarmonyPatch.Read(value, "shadowed"), Is.EqualTo("derived property"));
                Assert.That(PackageManagerSubmoduleHarmonyPatch.ReadProperty(value, "shadowed"), Is.EqualTo("derived property"));
                Assert.That(PackageManagerSubmoduleHarmonyPatch.ReadProperty(value, "field"), Is.Null);
                Assert.That(PackageManagerSubmoduleHarmonyPatch.Read(value, "field"), Is.EqualTo("field value"));
                Assert.That(PackageManagerSubmoduleHarmonyPatch.ReadProperty(value, "privateBase"), Is.Null);
                Assert.That(PackageManagerSubmoduleHarmonyPatch.Read(value, "privateBase"), Is.EqualTo("base value"));
                Assert.That(PackageManagerSubmoduleHarmonyPatch.Read(value, "missing"), Is.Null);
                Assert.That(PackageManagerSubmoduleHarmonyPatch.ReadProperty(value, "missing"), Is.Null);
                Assert.That(PackageManagerSubmoduleHarmonyPatch.Read(value, "Item"), Is.Null);
                Assert.That(PackageManagerSubmoduleHarmonyPatch.ReadProperty(value, "Item"), Is.Null);
                Assert.That(PackageManagerSubmoduleHarmonyPatch.Read(new BaseMemberStub(), "shadowed"), Is.EqualTo("base field"));
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void CachedMemberMetadataStillReadsCurrentValuesAndRecoversAfterGetterFailure(bool propertyOnly)
        {
            var value = new DerivedMemberStub { Fails = true };
            Func<object, string, object> read = propertyOnly
                ? PackageManagerSubmoduleHarmonyPatch.ReadProperty : PackageManagerSubmoduleHarmonyPatch.Read;
            Assert.That(read(value, "current"), Is.Null);
            value.Fails = false;
            Assert.That(read(value, "current"), Is.EqualTo("first"));
            value.Value = "second";
            Assert.That(read(value, "current"), Is.EqualTo("second"));
            value.Fails = true;
            Assert.That(read(value, "current"), Is.Null);
            Assert.That(read(null, "current"), Is.Null);
        }

        [Test]
        public void NativePageMemberReadsKeepGetterFailuresVisibleToTheirGuard()
        {
            var value = new DerivedMemberStub { Fails = true };
            Assert.That(PackageManagerSubmoduleHarmonyPatch.Read(value, "current"), Is.Null);
            Assert.Throws<TargetInvocationException>(() => PackageManagerSubmoduleHarmonyPatch.ReadMember(value, "current"));
            value.Fails = false;
            Assert.That(PackageManagerSubmoduleHarmonyPatch.ReadMember(value, "current"), Is.EqualTo("first"));
        }

        [TestCase(0)]
        [TestCase(1)]
        public void RecycledSubmoduleTag_RestoresTheNativeCustomTag(int repeatedRefreshes)
        {
            var container = new VisualElement { name = "tagContainer" };
            var label = new Label("Custom") { tooltip = "Native tooltip" };
            container.Add(label);
            PackageManagerSubmoduleHarmonyPatch.ApplyTag(label, true);
            Assert.That(label.text, Is.EqualTo("Submodule"));
            Assert.That(container.style.maxWidth.keyword, Is.EqualTo(StyleKeyword.None));
            for (int refresh = 0; refresh < repeatedRefreshes; refresh++)
                PackageManagerSubmoduleHarmonyPatch.ApplyTag(label, true);
            PackageManagerSubmoduleHarmonyPatch.ApplyTag(label, false);
            Assert.That(label.text, Is.EqualTo("Custom"));
            Assert.That(label.tooltip, Is.EqualTo("Native tooltip"));
            Assert.That(label.ClassListContains("disable-ellipsis"), Is.False);
            Assert.That(container.style.maxWidth.keyword, Is.EqualTo(StyleKeyword.Null));
        }

        [TestCase(0)]
        [TestCase(2)]
        public void RecycledTag_PreservesANewNativePresentation(int repeatedRefreshes)
        {
            var label = new Label("Custom") { tooltip = "Original tooltip" };
            PackageManagerSubmoduleHarmonyPatch.ApplyTag(label, true);
            label.text = "Git";
            label.tooltip = "From Unity";
            label.AddToClassList("disable-ellipsis");
            for (int refresh = 0; refresh < repeatedRefreshes; refresh++)
                PackageManagerSubmoduleHarmonyPatch.ApplyTag(label, true);
            PackageManagerSubmoduleHarmonyPatch.ApplyTag(label, false);
            Assert.That(label.text, Is.EqualTo("Git"));
            Assert.That(label.tooltip, Is.EqualTo("From Unity"));
            Assert.That(label.ClassListContains("disable-ellipsis"), Is.True);
        }

        [Test]
        public void RepeatedTagRefresh_PreservesANativeTooltipChange()
        {
            var label = new Label("Custom") { tooltip = "Original tooltip" };
            PackageManagerSubmoduleHarmonyPatch.ApplyTag(label, true);
            label.tooltip = "Updated native tooltip";

            PackageManagerSubmoduleHarmonyPatch.ApplyTag(label, true);
            PackageManagerSubmoduleHarmonyPatch.ApplyTag(label, false);

            Assert.That(label.text, Is.EqualTo("Custom"));
            Assert.That(label.tooltip, Is.EqualTo("Updated native tooltip"));
            Assert.That(label.ClassListContains("disable-ellipsis"), Is.False);
        }

        [Test]
        public void ReparentedTag_ReleasesThePreviousContainerAndFitsTheNewOne()
        {
            var previous = new VisualElement { name = "tagContainer" };
            var current = new VisualElement { name = "tagContainer" };
            var label = new Label("Custom");
            previous.Add(label);
            PackageManagerSubmoduleHarmonyPatch.ApplyTag(label, true);

            current.Add(label);
            PackageManagerSubmoduleHarmonyPatch.ApplyTag(label, true);

            Assert.That(previous.style.maxWidth.keyword, Is.EqualTo(StyleKeyword.Null));
            Assert.That(current.style.maxWidth.keyword, Is.EqualTo(StyleKeyword.None));
            PackageManagerSubmoduleHarmonyPatch.ApplyTag(label, false);
            Assert.That(current.style.maxWidth.keyword, Is.EqualTo(StyleKeyword.Null));
            Assert.That(label.text, Is.EqualTo("Custom"));
        }

        [Test]
        public void ReparentedTag_PreservesExplicitNativeContainerWidths()
        {
            var previous = new VisualElement { name = "tagContainer" };
            var current = new VisualElement { name = "tagContainer" };
            var label = new Label("Custom");
            previous.Add(label);
            PackageManagerSubmoduleHarmonyPatch.ApplyTag(label, true);
            previous.style.maxWidth = 123;
            current.style.maxWidth = 456;

            current.Add(label);
            PackageManagerSubmoduleHarmonyPatch.ApplyTag(label, true);
            PackageManagerSubmoduleHarmonyPatch.ApplyTag(label, false);

            Assert.That(previous.style.maxWidth.value.value, Is.EqualTo(123));
            Assert.That(current.style.maxWidth.value.value, Is.EqualTo(456));
        }

        [TestCase("InProject")]
        [TestCase("UnityRegistry")]
        [TestCase("MyAssets")]
        [TestCase("Extension/another-package")]
        public void ChangedStatus_UpdatesTrackedLabelsWithoutRebuildingOtherNativePages(string pageId)
        {
            var root = new NativeRootStub();
            root.pageManager.activePage = new NativePageStub { id = pageId };
            var container = new VisualElement { name = "tagContainer" };
            var label = new Label("Custom") { tooltip = "Native tooltip" };
            var unrelated = new Label("Submodule");
            root.Add(container);
            container.Add(label);
            root.Add(unrelated);
            ObserveTag(label, new VersionStub());
            PackageManagerSubmoduleHarmonyPatch.ApplyTag(label, true);

            PackageManagerSubmoduleHarmonyPatch.RefreshPresentation(root);

            Assert.That(label.text, Is.EqualTo("Custom"));
            Assert.That(label.tooltip, Is.EqualTo("Native tooltip"));
            Assert.That(container.style.maxWidth.keyword, Is.EqualTo(StyleKeyword.Null));
            Assert.That(unrelated.text, Is.EqualTo("Submodule"), "Only native labels observed by our hook may change.");
            Assert.That(root.pageManager.activePage.Rebuilds, Is.Zero);
            Assert.That(root.pageManager.activePage.SelectionChanges, Is.Zero);
        }

        [Test]
        public void ChangedStatus_RecycledLabelUsesOnlyItsLatestObservedVersion()
        {
            var root = new VisualElement();
            var label = new Label("Custom");
            root.Add(label);
            var previous = new ObservedVersionStub();
            var current = new ObservedVersionStub();
            ObserveTag(label, previous);
            ObserveTag(label, current);
            int previousReads = previous.Reads;
            int currentReads = current.Reads;
            PackageManagerSubmoduleHarmonyPatch.ApplyTag(label, true);

            PackageManagerSubmoduleHarmonyPatch.RefreshPresentation(root);

            Assert.That(previous.Reads, Is.EqualTo(previousReads));
            Assert.That(current.Reads, Is.EqualTo(currentReads + 1));
            Assert.That(label.text, Is.EqualTo("Custom"));
        }

        [Test]
        public void NativeCatchUp_RefreshesAnExistingPageBeforePresentationOnlyUpdates()
        {
            var root = new NativeRootStub();
            root.pageManager.activePage = new NativePageStub { id = "InProject" };
            const BindingFlags instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            System.Type sessionType = typeof(GitSubmoduleManagerPackageManagerHost)
                .GetNestedType("Session", BindingFlags.NonPublic);
            object session = System.Activator.CreateInstance(sessionType, instance, null, new object[] { root }, null);
            try
            {
                MethodInfo refresh = sessionType.GetMethod("RefreshViews", instance);
                refresh.Invoke(session, null);
                refresh.Invoke(session, null);
                refresh.Invoke(session, null);

                Assert.That(root.pageManager.activePage.Rebuilds, Is.EqualTo(1));
                Assert.That(root.pageManager.activePage.SelectionChanges, Is.EqualTo(1));
            }
            finally
            {
                // This isolated session never retained a status observer.
                ((System.IDisposable)sessionType.GetField("Page", instance).GetValue(session)).Dispose();
            }
        }

        [TestCase(true)]
        [TestCase(false)]
        public void EmbeddedRemoval_UsesTheExactSelectedIdentity(bool protectedCheckout)
        {
            var selected = new VersionStub { name = "com.example.package", localPath = "/project/Packages/com.example.package" };
            Assert.That(PackageManagerSubmoduleHarmonyPatch.MustBlockRemoval(selected, (name, path) =>
            {
                Assert.That(name, Is.EqualTo(selected.name));
                Assert.That(path, Is.EqualTo(selected.localPath));
                return protectedCheckout;
            }), Is.EqualTo(protectedCheckout));
        }

        [Test]
        public void EmbeddedRemoval_UnknownIdentityFailsClosed()
        {
            Assert.That(PackageManagerSubmoduleHarmonyPatch.MustBlockRemoval(null, (_, _) => false), Is.True);
            Assert.That(PackageManagerSubmoduleHarmonyPatch.MustBlockRemoval(new VersionStub(), (_, _) => false), Is.True);
        }

        [Test]
        [Category("PackageManagerCompatibility")]
        public void RepeatedInstallationAndReinstallation_KeepOneCopyOfEachOwnedPatch()
        {
            PackageManagerSubmoduleHarmonyPatch.Install();
            Assert.That(PackageManagerSubmoduleHarmonyPatch.LastError, Is.Empty);
            MethodInfo[] tags = PackageManagerSubmoduleHarmonyPatch.TagTargets().ToArray();
            Assert.That(tags, Is.Not.Empty);
            MethodInfo[] targets = tags.Concat(new[]
            {
                PackageManagerSubmoduleHarmonyPatch.FindMethod("SourceInfoCard", "Refresh", typeof(void), "IPackageVersion"),
                PackageManagerSubmoduleHarmonyPatch.FindMethod("RemoveCustomAction", "TriggerActionImplementation", typeof(bool), "IPackageVersion"),
                PackageManagerSubmoduleHarmonyPatch.FindMethod("PackageOperationDispatcher", "RemoveEmbedded", typeof(void), "IPackage"),
                PackageManagerSubmoduleNativePage.GetNativeRefreshTarget(),
                PackageManagerSubmoduleNativePage.GetSidebarRefreshTarget(),
                PackageRepositoryLink.GetRefreshTarget()
            }).ToArray();
            Assert.That(targets, Has.None.Null);
            object harmony = typeof(PackageManagerSubmoduleHarmonyPatch)
                .GetField("harmony", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
            MethodInfo getPatchInfo = harmony.GetType().GetMethod("GetPatchInfo", BindingFlags.Public | BindingFlags.Static);
            int CountOwnedPatches(MethodInfo target)
            {
                object info = getPatchInfo.Invoke(null, new object[] { target });
                if (info == null)
                    return 0;
                return new[] { "Prefixes", "Postfixes", "Transpilers", "Finalizers" }.Sum(kind =>
                    ((IEnumerable)PackageManagerSubmoduleHarmonyPatch.Read(info, kind)).Cast<object>()
                    .Count(patch => (string)PackageManagerSubmoduleHarmonyPatch.Read(patch, "owner") ==
                        PackageManagerSubmoduleHarmonyPatch.HarmonyId));
            }

            try
            {
                Assert.That(targets.Select(CountOwnedPatches), Is.All.EqualTo(1));
                for (int attempt = 0; attempt < 3; attempt++)
                    PackageManagerSubmoduleHarmonyPatch.Install();
                Assert.That(targets.Select(CountOwnedPatches), Is.All.EqualTo(1));

                PackageManagerSubmoduleHarmonyPatch.Uninstall();
                Assert.That(targets.Select(CountOwnedPatches), Is.All.Zero);
                Assert.That(PackageManagerSubmoduleHarmonyPatch.IsSidebarRepairInstalled, Is.False);
                PackageManagerSubmoduleHarmonyPatch.Install();
                Assert.That(PackageManagerSubmoduleHarmonyPatch.LastError, Is.Empty);
                Assert.That(PackageManagerSubmoduleHarmonyPatch.IsSidebarRepairInstalled, Is.True);
                Assert.That(targets.Select(CountOwnedPatches), Is.All.EqualTo(1));
            }
            finally
            {
                PackageManagerSubmoduleHarmonyPatch.Install();
            }
        }

        private sealed class VersionStub
        {
            public string name { get; set; }
            public string localPath { get; set; }
            public bool isInstalled { get; set; }
        }

        private class BaseMemberStub
        {
#pragma warning disable CS0414
            private readonly string privateBase = "base value";
#pragma warning restore CS0414
            public string shadowed = "base field";
        }

        private sealed class DerivedMemberStub : BaseMemberStub
        {
            public new string shadowed => "derived property";
            public string field = "field value";
            internal bool Fails;
            internal string Value = "first";
            public string current => Fails ? throw new InvalidOperationException("Getter is temporarily unavailable.") : Value;
            public string this[int index] => index.ToString();
        }

        private static void ObserveTag(TextElement label, object version) =>
            typeof(PackageManagerSubmoduleHarmonyPatch)
                .GetMethod("TagPostfix", BindingFlags.Static | BindingFlags.NonPublic)
                .Invoke(null, new[] { (object)label, version });

        private sealed class ObservedVersionStub
        {
            internal int Reads { get; private set; }
            public bool isInstalled { get { Reads++; return false; } }
        }

        private sealed class NativeRootStub : VisualElement
        {
            public NativeManagerStub pageManager { get; } = new();
        }

        private sealed class NativeManagerStub
        {
            public NativePageStub activePage { get; set; }
        }

        private sealed class NativePageStub
        {
            public string id { get; set; }
            internal int Rebuilds { get; private set; }
            internal int SelectionChanges { get; private set; }
            public void Rebuild(bool force) => Rebuilds++;
            public void TriggerOnSelectionChanged(bool direct) => SelectionChanges++;
        }
    }
}
