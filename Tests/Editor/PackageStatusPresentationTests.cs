using System.Linq;
using NUnit.Framework;
using UnityEngine.UIElements;

namespace MartinCalander.GitSubmoduleManager.Editor.Tests
{
    [Category("PackageSourceFeatures")]
    public sealed class PackageStatusPresentationTests
    {
        [Test]
        public void RecycledSubmoduleTag_RestoresTheNativeCustomTag()
        {
            var container = new VisualElement { name = "tagContainer" };
            var label = new Label("Custom") { tooltip = "Native tooltip" };
            container.Add(label);
            PackageManagerSubmoduleHarmonyPatch.ApplyTag(label, true);
            Assert.That(label.text, Is.EqualTo("Submodule"));
            Assert.That(container.style.maxWidth.keyword, Is.EqualTo(StyleKeyword.None));
            PackageManagerSubmoduleHarmonyPatch.ApplyTag(label, false);
            Assert.That(label.text, Is.EqualTo("Custom"));
            Assert.That(label.tooltip, Is.EqualTo("Native tooltip"));
            Assert.That(label.ClassListContains("disable-ellipsis"), Is.False);
            Assert.That(container.style.maxWidth.keyword, Is.EqualTo(StyleKeyword.Null));
        }

        [Test]
        public void RecycledTag_PreservesANewNativePresentation()
        {
            var label = new Label("Custom");
            PackageManagerSubmoduleHarmonyPatch.ApplyTag(label, true);
            label.text = "Git";
            label.tooltip = "From Unity";
            label.AddToClassList("disable-ellipsis");
            PackageManagerSubmoduleHarmonyPatch.ApplyTag(label, false);
            Assert.That(label.text, Is.EqualTo("Git"));
            Assert.That(label.tooltip, Is.EqualTo("From Unity"));
            Assert.That(label.ClassListContains("disable-ellipsis"), Is.True);
        }

        [Test]
        public void RepeatedTagRefresh_DoesNotLoseTheOriginalLabel()
        {
            var label = new Label("Custom");
            PackageManagerSubmoduleHarmonyPatch.ApplyTag(label, true);
            PackageManagerSubmoduleHarmonyPatch.ApplyTag(label, true);
            PackageManagerSubmoduleHarmonyPatch.ApplyTag(label, false);
            Assert.That(label.text, Is.EqualTo("Custom"));
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
        public void NativeStatusAndRemovalContracts_ArePatchedOnThisEditor()
        {
            PackageManagerSubmoduleHarmonyPatch.Install();
            Assert.That(PackageManagerSubmoduleHarmonyPatch.LastError, Is.Empty);
            var tags = PackageManagerSubmoduleHarmonyPatch.TagTargets().ToArray();
            Assert.That(tags, Is.Not.Empty);
            foreach (var method in tags)
                Assert.That(PackageManagerSubmoduleHarmonyPatch.IsPatched(method), Is.True, method.ToString());
            Assert.That(PackageManagerSubmoduleHarmonyPatch.IsPatched(
                PackageManagerSubmoduleHarmonyPatch.FindMethod("SourceInfoCard", "Refresh", typeof(void), "IPackageVersion")), Is.True);
            Assert.That(PackageManagerSubmoduleHarmonyPatch.IsPatched(
                PackageManagerSubmoduleHarmonyPatch.FindMethod("RemoveCustomAction", "TriggerActionImplementation", typeof(bool), "IPackageVersion")), Is.True);
            Assert.That(PackageManagerSubmoduleHarmonyPatch.IsPatched(
                PackageManagerSubmoduleHarmonyPatch.FindMethod("PackageOperationDispatcher", "RemoveEmbedded", typeof(void), "IPackage")), Is.True);
            Assert.That(PackageManagerSubmoduleHarmonyPatch.IsPatched(PackageManagerSubmoduleNativePage.GetNativeRefreshTarget()), Is.True);
            Assert.That(PackageManagerSubmoduleHarmonyPatch.IsPatched(PackageRepositoryLink.GetRefreshTarget()), Is.True);
        }

        private sealed class VersionStub
        {
            public string name { get; set; }
            public string localPath { get; set; }
        }
    }
}
