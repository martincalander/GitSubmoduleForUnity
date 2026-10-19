using System.Linq;
using NUnit.Framework;
using UnityEngine.UIElements;

namespace MartinCalander.GitSubmoduleManager.Editor.Tests
{
    [Category("PackageSourceFeatures")]
    public sealed class PackageStatusPresentationTests
    {
        [Test]
        public void SourceDetails_ClearPreviousGitInformationWhenSelectionChanges()
        {
            var view = new PackageStatusDetailsView();
            var git = PackageSourceDetails.Create("/project/Library/PackageCache/com.example.git",
                "https://github.com/example/package.git#main", "main", new string('a', 40), true);
            view.Render("Git package", git, true);
            Assert.That(view.Q<Label>("git-submodule-git-revision").text, Does.Contain("main → aaaaaaa"));
            Assert.That(view.Q<Button>("git-submodule-copy-commit").enabledSelf, Is.True);
            Assert.That(view.Q<Button>("git-submodule-copy-source").enabledSelf, Is.False,
                "Unity already supplies the Git source URL and its copy action.");

            view.Render("Registry package", PackageSourceDetails.Create("/cache/registry", "", "", "", false), false);
            Assert.That(view.Q<Label>("git-submodule-git-revision").text, Is.Empty);
            Assert.That(view.Q<Label>("git-submodule-git-revision").tooltip, Is.Empty);
            Assert.That(view.Q<Button>("git-submodule-open-repository").enabledSelf, Is.False);
            Assert.That(view.Q<Button>("git-submodule-copy-commit").enabledSelf, Is.False);
            Assert.That(view.Q<Button>("git-submodule-reveal-folder").tooltip, Is.EqualTo("/cache/registry"));

            view.Render("Not installed", PackageSourceDetails.Unavailable, false);
            Assert.That(view.Q<Button>("git-submodule-reveal-folder").enabledSelf, Is.False);
            Assert.That(view.Q<Label>("git-submodule-package-location").text, Is.Empty);
        }

        [Test]
        public void SubmoduleDetails_SupplyMissingSourceActionsWithoutAnInventedCommit()
        {
            var view = new PackageStatusDetailsView();
            var details = PackageSourceDetails.Create("/project/Packages/com.example.package",
                "git@github.com:example/package.git", "", "", false);
            view.Render("Git submodule", details, false);
            Assert.That(view.Q<Label>("git-submodule-source-url").text,
                Is.EqualTo("Source: git@github.com:example/package.git"));
            Assert.That(view.Q<Button>("git-submodule-open-repository").tooltip,
                Is.EqualTo("https://github.com/example/package"));
            Assert.That(view.Q<Button>("git-submodule-copy-source").enabledSelf, Is.True);
            Assert.That(view.Q<Button>("git-submodule-copy-commit").enabledSelf, Is.False);
            Assert.That(view.Q<Label>("git-submodule-git-revision").text, Is.Empty);
        }

        [Test]
        public void UnknownSshHost_CanCopySourceWithoutGuessingAWebPage()
        {
            var view = new PackageStatusDetailsView();
            view.Render("Git submodule", PackageSourceDetails.Create("/project/Packages/com.example.package",
                "git@custom.example:team/repo.git", "", "", false), false);
            Assert.That(view.Q<Button>("git-submodule-open-repository").enabledSelf, Is.False);
            Assert.That(view.Q<Button>("git-submodule-copy-source").enabledSelf, Is.True);
        }

        [Test]
        public void SourceDetails_DoNotInterpretRepositoryTextAsRichText()
        {
            var view = new PackageStatusDetailsView();
            view.Render("Inspect <b>package</b>", PackageSourceDetails.Unavailable, false);
            Assert.That(view.Q<Label>("git-submodule-package-status").enableRichText, Is.False);
            Assert.That(view.Q<Label>("git-submodule-package-status").text, Is.EqualTo("Inspect <b>package</b>"));
        }

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
        }

        private sealed class VersionStub
        {
            public string name { get; set; }
            public string localPath { get; set; }
        }
    }
}
