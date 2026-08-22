using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UIElements;

namespace MartinCalander.GitSubmoduleManager.Editor.Tests
{
    public sealed class PackageManagerSubmodulePresentationTests
    {
        private sealed class FakePackageVersion
        {
            public string name { get; set; }
            public string localPath { get; set; }
            public bool isInstalled { get; set; }
        }

        private sealed class FakePage
        {
            public string id { get; set; }
        }

        private sealed class FakePageManager
        {
            public object activePage { get; set; }
        }

        private sealed class ThrowingPageManager
        {
            public object activePage => throw new InvalidOperationException("contract drift");
        }

        private sealed class FakePackageManagerRoot
        {
            private readonly object m_PageManager;

            internal FakePackageManagerRoot(object pageManager)
            {
                m_PageManager = pageManager;
            }
        }

        [Test]
        public void Snapshot_ExactInstalledPackageSubmodule_IsClassifiedAsGitHub()
        {
            string projectRoot = Path.Combine(Path.GetTempPath(), "GitSubmodulePresentationProject");
            PackageManagerSubmoduleSnapshotData snapshot = CreateSnapshot(
                projectRoot,
                "git@github.com:owner/repository.git");
            string localPath = Path.Combine(
                projectRoot,
                "Packages",
                "com.example.repository");

            bool found = snapshot.TryGet(
                "com.example.repository",
                localPath,
                true,
                out PackageManagerSubmoduleInfo info);

            Assert.That(found, Is.True);
            Assert.That(info.IsGitHub, Is.True);
            Assert.That(info.SourceLabel, Is.EqualTo("GitHub"));
            Assert.That(PackageManagerSubmoduleInfo.TagLabel, Is.EqualTo("Submodule"));
        }

        [Test]
        public void Snapshot_NonGitHubRemote_UsesGenericGitSource()
        {
            string projectRoot = Path.Combine(Path.GetTempPath(), "GitSubmodulePresentationProject");
            PackageManagerSubmoduleSnapshotData snapshot = CreateSnapshot(
                projectRoot,
                "ssh://git@git.example.com/team/repository.git");

            bool found = snapshot.TryGet(
                "com.example.repository",
                string.Empty,
                true,
                out PackageManagerSubmoduleInfo info);

            Assert.That(found, Is.True);
            Assert.That(info.IsGitHub, Is.False);
            Assert.That(info.SourceLabel, Is.EqualTo("Git"));
            Assert.That(snapshot.ContainsGitHubRepository(
                "owner",
                "repository"), Is.False);
        }

        [Test]
        public void Snapshot_GitHubRepositoryIdentity_IsCaseInsensitive()
        {
            string projectRoot = Path.Combine(
                Path.GetTempPath(),
                "GitSubmodulePresentationProject");
            PackageManagerSubmoduleSnapshotData snapshot = CreateSnapshot(
                projectRoot,
                "https://github.com/Owner/Repository.git");

            Assert.That(snapshot.ContainsGitHubRepository(
                "owner",
                "repository"), Is.True);
            Assert.That(snapshot.ContainsGitHubRepository(
                "owner",
                "different"), Is.False);
        }

        [Test]
        public void Snapshot_UninstalledOrDifferentConcretePath_IsNotClassified()
        {
            string projectRoot = Path.Combine(Path.GetTempPath(), "GitSubmodulePresentationProject");
            PackageManagerSubmoduleSnapshotData snapshot = CreateSnapshot(
                projectRoot,
                "https://github.com/owner/repository.git");

            Assert.That(snapshot.TryGet(
                "com.example.repository",
                string.Empty,
                false,
                out _), Is.False);
            Assert.That(snapshot.TryGet(
                "com.example.repository",
                Path.Combine(projectRoot, "Elsewhere", "com.example.repository"),
                true,
                out _), Is.False);
        }

        [Test]
        public void VersionIdentity_ReadsInternalContractShapeWithoutUnityTypeReference()
        {
            var version = new FakePackageVersion
            {
                name = "com.example.repository",
                localPath = "/project/Packages/com.example.repository",
                isInstalled = true
            };

            bool read = PackageManagerSubmodulePresentation.TryGetVersionIdentity(
                version,
                out string name,
                out string localPath,
                out bool isInstalled);

            Assert.That(read, Is.True);
            Assert.That(name, Is.EqualTo(version.name));
            Assert.That(localPath, Is.EqualTo(version.localPath));
            Assert.That(isInstalled, Is.True);
        }

        [Test]
        public void TagMutation_ChangesOnlyTextAndTooltipPresentation()
        {
            var label = new Label("Custom") { tooltip = string.Empty };
            PackageManagerSubmoduleInfo info = CreateInfo(isGitHub: true);

            bool applied = PackageManagerSubmodulePresentation.ApplyTagLabel(label, info);

            Assert.That(applied, Is.True);
            Assert.That(label.text, Is.EqualTo("Submodule"));
            Assert.That(label.tooltip, Does.Contain("Git submodule"));
            Assert.That(label.ClassListContains(
                PackageManagerSubmodulePresentation.CustomTagClassName), Is.True);
            Assert.That(label.ClassListContains(
                PackageManagerSubmodulePresentation.NativeDisableEllipsisClassName), Is.True);
        }

        [Test]
        public void TagMutation_RelaxesOnlyBuiltInTagContainer()
        {
            var container = new VisualElement
            {
                name = PackageManagerSubmodulePresentation.NativeTagContainerName
            };
            var label = new Label("Custom");
            container.Add(label);

            Assert.That(PackageManagerSubmodulePresentation.ApplyTagLabel(
                label,
                CreateInfo(isGitHub: true)), Is.True);

            Assert.That(container.ClassListContains(
                PackageManagerSubmodulePresentation.CustomTagContainerClassName), Is.True);
            Assert.That(container.style.maxWidth.keyword, Is.EqualTo(StyleKeyword.None));
        }

        [Test]
        public void TagReset_RecycledInDevelopmentLabelRestoresBuiltInCustomPresentation()
        {
            var container = new VisualElement
            {
                name = PackageManagerSubmodulePresentation.NativeTagContainerName
            };
            var label = new Label("Custom") { tooltip = string.Empty };
            container.Add(label);
            Assert.That(PackageManagerSubmodulePresentation.ApplyTagLabel(
                label,
                CreateInfo(isGitHub: true)), Is.True);

            PackageManagerSubmodulePresentation.ResetCustomTagLabel(label);

            Assert.That(label.text, Is.EqualTo(UnityEditor.L10n.Tr("Custom")));
            Assert.That(label.tooltip, Is.Empty);
            Assert.That(label.ClassListContains(
                PackageManagerSubmodulePresentation.CustomTagClassName), Is.False);
            Assert.That(label.ClassListContains(
                PackageManagerSubmodulePresentation.NativeDisableEllipsisClassName), Is.False);
            Assert.That(container.ClassListContains(
                PackageManagerSubmodulePresentation.CustomTagContainerClassName), Is.False);
            Assert.That(container.style.maxWidth.keyword, Is.EqualTo(StyleKeyword.Null));
        }

        [Test]
        public void TagReset_PreservesPresentationChangedByBuiltInRefresh()
        {
            var container = new VisualElement
            {
                name = PackageManagerSubmodulePresentation.NativeTagContainerName
            };
            var label = new Label("Custom") { tooltip = string.Empty };
            container.Add(label);
            Assert.That(PackageManagerSubmodulePresentation.ApplyTagLabel(
                label,
                CreateInfo(isGitHub: true)), Is.True);
            label.text = "Git";
            label.tooltip = "Built-in tag tooltip";

            PackageManagerSubmodulePresentation.ResetCustomTagLabel(label);

            Assert.That(label.text, Is.EqualTo("Git"));
            Assert.That(label.tooltip, Is.EqualTo("Built-in tag tooltip"));
            Assert.That(label.ClassListContains(
                PackageManagerSubmodulePresentation.CustomTagClassName), Is.False);
            Assert.That(label.ClassListContains(
                PackageManagerSubmodulePresentation.NativeDisableEllipsisClassName), Is.True);
            Assert.That(container.ClassListContains(
                PackageManagerSubmodulePresentation.CustomTagContainerClassName), Is.False);
            Assert.That(container.style.maxWidth.keyword, Is.EqualTo(StyleKeyword.Null));
        }

        [TestCase(false, "Public")]
        [TestCase(true, "Private")]
        public void RepositoryVisibilityTag_ShowsGitHubPrivacy(
            bool isPrivate,
            string expectedLabel)
        {
            var label = new Label("Git") { tooltip = string.Empty };

            Assert.That(
                PackageManagerSubmodulePresentation.ApplyRepositoryVisibilityTag(
                    label,
                    isPrivate),
                Is.True);

            Assert.That(label.text, Is.EqualTo(UnityEditor.L10n.Tr(expectedLabel)));
            Assert.That(label.tooltip, Does.Contain(
                isPrivate ? "private" : "public"));
            Assert.That(label.ClassListContains(
                PackageManagerSubmodulePresentation.RepositoryVisibilityTagClassName),
                Is.True);
            Assert.That(label.ClassListContains(
                PackageManagerSubmodulePresentation.NativeDisableEllipsisClassName),
                Is.True);
        }

        [TestCase(false, "Public")]
        [TestCase(true, "Private")]
        public void InstalledTag_GitHubPageUsesMatchingDiscoveryPrivacy(
            bool isPrivate,
            string expectedLabel)
        {
            var label = new Label("Custom") { tooltip = string.Empty };
            PackageManagerSubmoduleInfo info = CreateGitHubInfo(
                "git@github.com:Owner/Repository.git");
            PackageManagerGitHubDiscoverySnapshot discovery =
                CreateDiscoverySnapshot(
                    "owner",
                    "repository",
                    isPrivate);

            Assert.That(
                PackageManagerSubmodulePresentation.ApplyInstalledTagLabel(
                    label,
                    info,
                    true,
                    discovery),
                Is.True);

            Assert.That(label.text, Is.EqualTo(UnityEditor.L10n.Tr(expectedLabel)));
            Assert.That(label.ClassListContains(
                PackageManagerSubmodulePresentation
                    .InstalledRepositoryVisibilityTagClassName),
                Is.True);
            Assert.That(label.ClassListContains(
                PackageManagerSubmodulePresentation.CustomTagClassName),
                Is.False);
            Assert.That(label.ClassListContains(
                PackageManagerSubmodulePresentation.RepositoryVisibilityTagClassName),
                Is.False);
        }

        [Test]
        public void InstalledTag_OutsideGitHubPageRemainsSubmodule()
        {
            var label = new Label("Custom") { tooltip = string.Empty };

            Assert.That(
                PackageManagerSubmodulePresentation.ApplyInstalledTagLabel(
                    label,
                    CreateInfo(isGitHub: true),
                    false,
                    CreateDiscoverySnapshot("owner", "repository", true)),
                Is.True);

            Assert.That(label.text, Is.EqualTo(PackageManagerSubmoduleInfo.TagLabel));
            Assert.That(label.ClassListContains(
                PackageManagerSubmodulePresentation.CustomTagClassName),
                Is.True);
            Assert.That(label.ClassListContains(
                PackageManagerSubmodulePresentation.RepositoryVisibilityTagClassName),
                Is.False);
            Assert.That(label.ClassListContains(
                PackageManagerSubmodulePresentation
                    .InstalledRepositoryVisibilityTagClassName),
                Is.False);
        }

        [Test]
        public void InstalledTag_UnknownPrivacyFallsBackToSubmodule()
        {
            var label = new Label("Custom") { tooltip = string.Empty };

            Assert.That(
                PackageManagerSubmodulePresentation.ApplyInstalledTagLabel(
                    label,
                    CreateInfo(isGitHub: true),
                    true,
                    CreateDiscoverySnapshot("owner", "different-repository", true)),
                Is.True);

            Assert.That(label.text, Is.EqualTo(PackageManagerSubmoduleInfo.TagLabel));
            Assert.That(label.tooltip,
                Is.EqualTo(PackageManagerSubmodulePresentation.TagTooltip));
            Assert.That(label.ClassListContains(
                PackageManagerSubmodulePresentation.RepositoryVisibilityTagClassName),
                Is.False);
            Assert.That(label.ClassListContains(
                PackageManagerSubmodulePresentation
                    .InstalledRepositoryVisibilityTagClassName),
                Is.False);
        }

        [Test]
        public void InstalledTag_RecycledAcrossPagesAndRepositoriesResetsMarkers()
        {
            var container = new VisualElement
            {
                name = PackageManagerSubmodulePresentation.NativeTagContainerName
            };
            var label = new Label("Custom") { tooltip = string.Empty };
            container.Add(label);
            PackageManagerSubmoduleInfo info = CreateInfo(isGitHub: true);

            Assert.That(
                PackageManagerSubmodulePresentation.ApplyInstalledTagLabel(
                    label,
                    info,
                    true,
                    CreateDiscoverySnapshot("owner", "repository", true)),
                Is.True);
            Assert.That(label.text, Is.EqualTo(UnityEditor.L10n.Tr("Private")));

            Assert.That(
                PackageManagerSubmodulePresentation.ApplyInstalledTagLabel(
                    label,
                    info,
                    false,
                    CreateDiscoverySnapshot("owner", "repository", true)),
                Is.True);
            Assert.That(label.text, Is.EqualTo(PackageManagerSubmoduleInfo.TagLabel));
            Assert.That(label.ClassListContains(
                PackageManagerSubmodulePresentation
                    .InstalledRepositoryVisibilityTagClassName),
                Is.False);
            Assert.That(label.ClassListContains(
                PackageManagerSubmodulePresentation.CustomTagClassName),
                Is.True);
            Assert.That(container.ClassListContains(
                PackageManagerSubmodulePresentation.CustomTagContainerClassName),
                Is.True);

            Assert.That(
                PackageManagerSubmodulePresentation.ApplyInstalledTagLabel(
                    label,
                    info,
                    true,
                    CreateDiscoverySnapshot("OWNER", "REPOSITORY", false)),
                Is.True);
            Assert.That(label.text, Is.EqualTo(UnityEditor.L10n.Tr("Public")));
            Assert.That(label.ClassListContains(
                PackageManagerSubmodulePresentation.CustomTagClassName),
                Is.False);
            Assert.That(label.ClassListContains(
                PackageManagerSubmodulePresentation
                    .InstalledRepositoryVisibilityTagClassName),
                Is.True);
            Assert.That(container.ClassListContains(
                PackageManagerSubmodulePresentation.CustomTagContainerClassName),
                Is.False);
            Assert.That(container.style.maxWidth.keyword, Is.EqualTo(StyleKeyword.Null));
        }

        [Test]
        public void InstalledVisibility_RecycledToUnclassifiedPackageRestoresCustomBaseline()
        {
            var container = new VisualElement
            {
                name = PackageManagerSubmodulePresentation.NativeTagContainerName
            };
            var label = new Label(UnityEditor.L10n.Tr("Custom"))
            {
                tooltip = string.Empty
            };
            container.Add(label);

            Assert.That(
                PackageManagerSubmodulePresentation.ApplyInstalledTagLabel(
                    label,
                    CreateInfo(isGitHub: true),
                    true,
                    CreateDiscoverySnapshot("owner", "repository", true)),
                Is.True);
            Assert.That(label.text, Is.EqualTo(UnityEditor.L10n.Tr("Private")));
            Assert.That(label.ClassListContains(
                PackageManagerSubmodulePresentation.NativeDisableEllipsisClassName),
                Is.True);

            // Model Unity's native InDevelopment refresh running before our
            // postfix. It restores Custom text but does not know that this
            // package added disable-ellipsis on the prior row binding.
            label.text = UnityEditor.L10n.Tr("Custom");
            label.tooltip = string.Empty;

            PackageManagerSubmoduleHarmonyPatch.ApplyTagPresentation(
                label,
                new object());

            Assert.That(label.text, Is.EqualTo(UnityEditor.L10n.Tr("Custom")));
            Assert.That(label.tooltip, Is.Empty);
            Assert.That(label.ClassListContains(
                PackageManagerSubmodulePresentation
                    .InstalledRepositoryVisibilityTagClassName),
                Is.False);
            Assert.That(label.ClassListContains(
                PackageManagerSubmodulePresentation.RepositoryVisibilityTagClassName),
                Is.False);
            Assert.That(label.ClassListContains(
                PackageManagerSubmodulePresentation.CustomTagClassName),
                Is.False);
            Assert.That(label.ClassListContains(
                PackageManagerSubmodulePresentation.NativeDisableEllipsisClassName),
                Is.False);
            Assert.That(container.style.maxWidth.keyword, Is.EqualTo(StyleKeyword.Null));
        }

        [TestCase("Git")]
        [TestCase("Local")]
        [TestCase("Exp")]
        public void InstalledVisibility_NativeTextTransitionRestoresCapturedStyleBaseline(
            string nativeTag)
        {
            var label = new Label(UnityEditor.L10n.Tr("Custom"))
            {
                tooltip = string.Empty
            };

            Assert.That(
                PackageManagerSubmodulePresentation.ApplyInstalledTagLabel(
                    label,
                    CreateInfo(isGitHub: true),
                    true,
                    CreateDiscoverySnapshot("owner", "repository", true)),
                Is.True);
            Assert.That(label.ClassListContains(
                PackageManagerSubmodulePresentation.NativeDisableEllipsisClassName),
                Is.True);

            label.text = nativeTag;
            label.tooltip = "Native tag tooltip";
            PackageManagerSubmodulePresentation.ResetTagLabelPresentation(label);

            Assert.That(label.text, Is.EqualTo(nativeTag));
            Assert.That(label.tooltip, Is.EqualTo("Native tag tooltip"));
            Assert.That(label.ClassListContains(
                PackageManagerSubmodulePresentation.NativeDisableEllipsisClassName),
                Is.False);
            Assert.That(label.ClassListContains(
                PackageManagerSubmodulePresentation
                    .InstalledRepositoryVisibilityTagClassName),
                Is.False);
            Assert.That(label.ClassListContains(
                PackageManagerSubmodulePresentation
                    .InstalledVisibilityHadDisableEllipsisClassName),
                Is.False);
        }

        [Test]
        public void InstalledVisibility_PreexistingDisableEllipsisIsPreserved()
        {
            var label = new Label(UnityEditor.L10n.Tr("Custom"));
            label.AddToClassList(
                PackageManagerSubmodulePresentation.NativeDisableEllipsisClassName);

            Assert.That(
                PackageManagerSubmodulePresentation.ApplyInstalledTagLabel(
                    label,
                    CreateInfo(isGitHub: true),
                    true,
                    CreateDiscoverySnapshot("owner", "repository", false)),
                Is.True);
            label.text = "Git";

            PackageManagerSubmodulePresentation.ResetTagLabelPresentation(label);

            Assert.That(label.text, Is.EqualTo("Git"));
            Assert.That(label.ClassListContains(
                PackageManagerSubmodulePresentation.NativeDisableEllipsisClassName),
                Is.True);
            Assert.That(label.ClassListContains(
                PackageManagerSubmodulePresentation
                    .InstalledVisibilityHadDisableEllipsisClassName),
                Is.False);
        }

        [Test]
        public void InstalledVisibility_DoesNotMutateInstalledIndicatorSibling()
        {
            var row = new VisualElement();
            var tagContainer = new VisualElement
            {
                name = PackageManagerSubmodulePresentation.NativeTagContainerName
            };
            var label = new Label(UnityEditor.L10n.Tr("Custom"));
            tagContainer.Add(label);
            var installedIndicator = new Toggle
            {
                name = "installedIndicator",
                value = true,
                userData = new object()
            };
            installedIndicator.AddToClassList("native-installed-state");
            installedIndicator.SetEnabled(false);
            object indicatorState = installedIndicator.userData;
            row.Add(tagContainer);
            row.Add(installedIndicator);

            Assert.That(
                PackageManagerSubmodulePresentation.ApplyInstalledTagLabel(
                    label,
                    CreateInfo(isGitHub: true),
                    true,
                    CreateDiscoverySnapshot("owner", "repository", false)),
                Is.True);

            Assert.That(row[1], Is.SameAs(installedIndicator));
            Assert.That(installedIndicator.parent, Is.SameAs(row));
            Assert.That(installedIndicator.value, Is.True);
            Assert.That(installedIndicator.enabledSelf, Is.False);
            Assert.That(installedIndicator.userData, Is.SameAs(indicatorState));
            Assert.That(installedIndicator.ClassListContains(
                "native-installed-state"), Is.True);
        }

        [Test]
        public void DeferredTag_UnrecognizedRebindBeforeAttachCancelsOldPackage()
        {
            var label = new Label(UnityEditor.L10n.Tr("Custom"));
            var previouslyBoundPackage = new object();
            var unrecognizedReboundPackage = new object();

            PackageManagerSubmoduleHarmonyPatch.DeferTagPresentationUntilAttached(
                label,
                previouslyBoundPackage);
            Assert.That(
                PackageManagerSubmoduleHarmonyPatch.HasDeferredTagPresentation(
                    label,
                    previouslyBoundPackage),
                Is.True);

            PackageManagerSubmoduleHarmonyPatch.ApplyTagPresentation(
                label,
                unrecognizedReboundPackage);
            Assert.That(
                PackageManagerSubmoduleHarmonyPatch.HasDeferredTagPresentation(
                    label,
                    previouslyBoundPackage),
                Is.False);
            Assert.That(
                PackageManagerSubmoduleHarmonyPatch.HasDeferredTagPresentation(
                    label,
                    unrecognizedReboundPackage),
                Is.False);

            // Exercise the callback's attach-time consumer after cancellation.
            // It must remain a no-op instead of replaying the old package.
            PackageManagerSubmoduleHarmonyPatch
                .ApplyDeferredTagPresentationOnAttach(label);
            Assert.That(label.text, Is.EqualTo(UnityEditor.L10n.Tr("Custom")));
        }

        [Test]
        public void DeferredTag_RecognizedRebindReplacesPendingPackage()
        {
            var label = new Label(UnityEditor.L10n.Tr("Custom"));
            var firstPackage = new object();
            var reboundPackage = new object();

            PackageManagerSubmoduleHarmonyPatch.DeferTagPresentationUntilAttached(
                label,
                firstPackage);
            PackageManagerSubmoduleHarmonyPatch.DeferTagPresentationUntilAttached(
                label,
                reboundPackage);

            Assert.That(
                PackageManagerSubmoduleHarmonyPatch.HasDeferredTagPresentation(
                    label,
                    firstPackage),
                Is.False);
            Assert.That(
                PackageManagerSubmoduleHarmonyPatch.HasDeferredTagPresentation(
                    label,
                    reboundPackage),
                Is.True);

            PackageManagerSubmoduleHarmonyPatch.CancelDeferredTagPresentation(label);
        }

        [TestCase(PackageManagerSubmoduleNativePage.ExtensionPageId, true, true)]
        [TestCase("UnityRegistry", true, false)]
        [TestCase("extension/git-submodule-manager", true, false)]
        [TestCase(null, false, false)]
        public void ActivePageDetection_RequiresExactResolvedGitHubPageId(
            string pageId,
            bool expectedResolved,
            bool expectedGitHubPage)
        {
            var pageManager = new FakePageManager
            {
                activePage = new FakePage { id = pageId }
            };

            bool resolved = PackageManagerSubmoduleHarmonyPatch.TryGetGitHubPageState(
                pageManager,
                out bool isGitHubPage);

            Assert.That(resolved, Is.EqualTo(expectedResolved));
            Assert.That(isGitHubPage, Is.EqualTo(expectedGitHubPage));
        }

        [Test]
        public void ActivePageDetection_ReflectionFailureIsUnresolved()
        {
            Assert.That(
                PackageManagerSubmoduleHarmonyPatch.TryGetGitHubPageState(
                    new ThrowingPageManager(),
                    out bool isGitHubPage),
                Is.False);
            Assert.That(isGitHubPage, Is.False);
            Assert.That(
                PackageManagerSubmoduleHarmonyPatch.TryGetGitHubPageState(
                    new object(),
                    out isGitHubPage),
                Is.False);
            Assert.That(isGitHubPage, Is.False);
        }

        [Test]
        public void ActivePageRootContract_TraversesPageManagerAndReportsDrift()
        {
            var root = new FakePackageManagerRoot(
                new FakePageManager
                {
                    activePage = new FakePage
                    {
                        id = PackageManagerSubmoduleNativePage.ExtensionPageId
                    }
                });

            Assert.That(
                PackageManagerSubmoduleHarmonyPatch.TryGetGitHubPageStateFromRoot(
                    root,
                    out bool isGitHubPage,
                    out string diagnostic),
                Is.True,
                diagnostic);
            Assert.That(isGitHubPage, Is.True);
            Assert.That(diagnostic, Is.Empty);

            Assert.That(
                PackageManagerSubmoduleHarmonyPatch.TryGetGitHubPageStateFromRoot(
                    new object(),
                    out isGitHubPage,
                    out diagnostic),
                Is.False);
            Assert.That(isGitHubPage, Is.False);
            Assert.That(diagnostic, Does.Contain(
                PackageManagerSubmoduleHarmonyPatch.PageManagerFieldName));
            Assert.That(diagnostic, Does.Contain(
                PackageManagerSubmoduleHarmonyPatch.PageManagerPropertyName));
        }

        [Test]
        public void ActivePageReflectionContract_RealUnityTypesExposeFullChain()
        {
            if (!PackageManagerSubmoduleNativePage.IsSupportedContract())
            {
                Assert.Ignore(
                    "This Editor uses the guarded legacy Package Manager host; " +
                    "the native active-page seam is intentionally unavailable.");
                return;
            }

            Type windowType = PackageManagerSubmoduleHarmonyPatch.FindLoadedType(
                PackageManagerSubmoduleHarmonyPatch.PackageManagerWindowTypeName);
            Assert.That(windowType, Is.Not.Null);

            FieldInfo rootField = FindInstanceField(
                windowType,
                PackageManagerSubmoduleHarmonyPatch.PackageManagerRootFieldName);
            Assert.That(rootField, Is.Not.Null,
                "PackageManagerWindow.m_Root reflection seam changed.");

            FieldInfo pageManagerField = FindInstanceField(
                rootField.FieldType,
                PackageManagerSubmoduleHarmonyPatch.PageManagerFieldName);
            PropertyInfo pageManagerProperty = FindInstanceProperty(
                rootField.FieldType,
                PackageManagerSubmoduleHarmonyPatch.PageManagerPropertyName);
            Type pageManagerType = pageManagerField?.FieldType ??
                                   pageManagerProperty?.PropertyType;
            Assert.That(pageManagerType, Is.Not.Null,
                "Package Manager root no longer exposes m_PageManager/pageManager.");

            PropertyInfo activePageProperty = FindInstanceProperty(
                pageManagerType,
                PackageManagerSubmoduleHarmonyPatch.ActivePagePropertyName);
            Assert.That(activePageProperty, Is.Not.Null,
                "Package Manager page manager no longer exposes activePage.");
            PropertyInfo pageIdProperty = FindInstanceProperty(
                activePageProperty.PropertyType,
                PackageManagerSubmoduleHarmonyPatch.PageIdPropertyName);
            Assert.That(pageIdProperty, Is.Not.Null,
                "Package Manager active page no longer exposes id.");
            Assert.That(pageIdProperty.PropertyType, Is.EqualTo(typeof(string)));
        }

        [Test]
        public void ActivePageReflectionContract_OpenWindowProvidesDiagnostic()
        {
            if (!PackageManagerSubmoduleNativePage.IsSupportedContract())
            {
                Assert.Ignore(
                    "This Editor uses the guarded legacy Package Manager host; " +
                    "there is no native active-page diagnostic to exercise.");
                return;
            }

            Type windowType = PackageManagerSubmoduleHarmonyPatch.FindLoadedType(
                PackageManagerSubmoduleHarmonyPatch.PackageManagerWindowTypeName);
            UnityEditor.EditorWindow window = Resources
                .FindObjectsOfTypeAll<UnityEditor.EditorWindow>()
                .FirstOrDefault(candidate =>
                    candidate != null && candidate.GetType() == windowType);
            if (window == null)
            {
                Assert.Ignore("No live Package Manager window is open.");
                return;
            }

            Assert.That(
                PackageManagerSubmoduleHarmonyPatch.TryGetGitHubPageStateFromWindow(
                    window,
                    out _,
                    out string diagnostic),
                Is.True,
                diagnostic);
            Assert.That(diagnostic, Is.Empty);
        }

        [Test]
        public void RepositoryVisibilityTag_RecycledGitLabelRestoresNativeBaseline()
        {
            var label = new Label("Git") { tooltip = string.Empty };
            label.AddToClassList(
                PackageManagerSubmodulePresentation.NativeDisableEllipsisClassName);
            Assert.That(
                PackageManagerSubmodulePresentation.ApplyRepositoryVisibilityTag(
                    label,
                    true),
                Is.True);

            PackageManagerSubmodulePresentation.ResetRepositoryVisibilityTag(label);

            Assert.That(label.text, Is.EqualTo(UnityEditor.L10n.Tr("Git")));
            Assert.That(label.tooltip, Is.Empty);
            Assert.That(label.ClassListContains(
                PackageManagerSubmodulePresentation.RepositoryVisibilityTagClassName),
                Is.False);
            Assert.That(label.ClassListContains(
                PackageManagerSubmodulePresentation.NativeDisableEllipsisClassName),
                Is.True);
        }

        [Test]
        public void RepositoryVisibilityTag_PreservesPresentationChangedByUnity()
        {
            var label = new Label("Git") { tooltip = string.Empty };
            Assert.That(
                PackageManagerSubmodulePresentation.ApplyRepositoryVisibilityTag(
                    label,
                    true),
                Is.True);
            label.text = "Exp";
            label.tooltip = "Built-in tag tooltip";

            PackageManagerSubmodulePresentation.ResetRepositoryVisibilityTag(label);

            Assert.That(label.text, Is.EqualTo("Exp"));
            Assert.That(label.tooltip, Is.EqualTo("Built-in tag tooltip"));
            Assert.That(label.ClassListContains(
                PackageManagerSubmodulePresentation.RepositoryVisibilityTagClassName),
                Is.False);
        }

        [Test]
        public void RepositoryVisibilityTag_RecycledPrivateLabelCanBecomePublic()
        {
            var label = new Label("Git") { tooltip = string.Empty };
            Assert.That(
                PackageManagerSubmodulePresentation.ApplyRepositoryVisibilityTag(
                    label,
                    true),
                Is.True);

            PackageManagerSubmodulePresentation.ResetRepositoryVisibilityTag(label);
            Assert.That(
                PackageManagerSubmodulePresentation.ApplyRepositoryVisibilityTag(
                    label,
                    false),
                Is.True);

            Assert.That(label.text, Is.EqualTo(UnityEditor.L10n.Tr("Public")));
            Assert.That(label.tooltip, Does.Contain("public"));
        }

        [Test]
        public void TagMutation_DoesNotOverrideUnrelatedOrExplicitlySizedContainer()
        {
            var unrelatedContainer = new VisualElement { name = "otherContainer" };
            unrelatedContainer.style.maxWidth = 80f;
            var unrelatedLabel = new Label("Custom");
            unrelatedContainer.Add(unrelatedLabel);

            var explicitlySizedTagContainer = new VisualElement
            {
                name = PackageManagerSubmodulePresentation.NativeTagContainerName
            };
            explicitlySizedTagContainer.style.maxWidth = 85f;
            var explicitlySizedLabel = new Label("Custom");
            explicitlySizedTagContainer.Add(explicitlySizedLabel);

            Assert.That(PackageManagerSubmodulePresentation.ApplyTagLabel(
                unrelatedLabel,
                CreateInfo(isGitHub: true)), Is.True);
            Assert.That(PackageManagerSubmodulePresentation.ApplyTagLabel(
                explicitlySizedLabel,
                CreateInfo(isGitHub: true)), Is.True);

            Assert.That(unrelatedContainer.style.maxWidth.value.value, Is.EqualTo(80f));
            Assert.That(explicitlySizedTagContainer.style.maxWidth.value.value, Is.EqualTo(85f));
            Assert.That(unrelatedContainer.ClassListContains(
                PackageManagerSubmodulePresentation.CustomTagContainerClassName), Is.False);
            Assert.That(explicitlySizedTagContainer.ClassListContains(
                PackageManagerSubmodulePresentation.CustomTagContainerClassName), Is.False);
        }

        [Test]
        public void TagReset_PreservesContainerSizingChangedAfterOurPresentation()
        {
            var container = new VisualElement
            {
                name = PackageManagerSubmodulePresentation.NativeTagContainerName
            };
            var label = new Label("Custom");
            container.Add(label);
            Assert.That(PackageManagerSubmodulePresentation.ApplyTagLabel(
                label,
                CreateInfo(isGitHub: true)), Is.True);
            container.style.maxWidth = 90f;

            PackageManagerSubmodulePresentation.ResetCustomTagLabel(label);

            Assert.That(container.style.maxWidth.value.value, Is.EqualTo(90f));
            Assert.That(container.ClassListContains(
                PackageManagerSubmodulePresentation.CustomTagContainerClassName), Is.False);
        }

        [Test]
        public void SourceMutation_GitHubUsesGitHubLabelAndThemeIconMarker()
        {
            var card = new VisualElement();
            var icon = new VisualElement();
            icon.AddToClassList(
                PackageManagerSubmodulePresentation.InformationCardIconClassName);
            icon.style.display = DisplayStyle.None;
            var content = new Label("Custom");
            content.AddToClassList(
                PackageManagerSubmodulePresentation.InformationCardTextClassName);
            card.Add(icon);
            card.Add(content);
            var texture = new Texture2D(1, 1) { hideFlags = HideFlags.HideAndDontSave };

            try
            {
                bool applied = PackageManagerSubmodulePresentation.ApplySourceCard(
                    card,
                    CreateInfo(isGitHub: true),
                    texture);

                Assert.That(applied, Is.True);
                Assert.That(content.text, Is.EqualTo("GitHub"));
                Assert.That(icon.ClassListContains(
                    PackageManagerSubmodulePresentation.CustomSourceIconClassName), Is.True);
                Assert.That(icon.style.display.value, Is.EqualTo(DisplayStyle.Flex));
                Assert.That(card.style.display.value, Is.EqualTo(DisplayStyle.Flex));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(texture);
            }
        }

        [Test]
        public void DiscoveryDetails_UseDeclaredTechnicalNameAndGitHubOwner()
        {
            var technicalNameCard = new VisualElement();
            var technicalName = new Label("synthetic-id");
            technicalName.AddToClassList(
                PackageManagerSubmodulePresentation.InformationCardTextClassName);
            technicalNameCard.Add(technicalName);
            var author = new VisualElement();
            author.Add(new Label("Author unknown"));

            Assert.That(
                PackageManagerSubmodulePresentation.ApplyTechnicalNameCard(
                    technicalNameCard,
                    "com.example.repository"),
                Is.True);
            Assert.That(
                PackageManagerSubmodulePresentation.ApplyAuthorLabel(
                    author,
                    "example-owner"),
                Is.True);

            Assert.That(technicalName.text, Is.EqualTo("com.example.repository"));
            Assert.That(technicalNameCard.style.display.value,
                Is.EqualTo(DisplayStyle.Flex));
            Assert.That(author.childCount, Is.EqualTo(2));
            Assert.That((author[0] as Label)?.text, Is.EqualTo(UnityEditor.L10n.Tr("By")));
            Assert.That((author[1] as Label)?.text, Is.EqualTo("example-owner"));
        }

        [Test]
        public void SourceMutation_NonGitHubUsesGitLabelWithoutChangingBuiltInIcon()
        {
            var card = new VisualElement();
            var icon = new VisualElement();
            icon.AddToClassList(
                PackageManagerSubmodulePresentation.InformationCardIconClassName);
            icon.style.display = DisplayStyle.Flex;
            icon.tooltip = "Built-in source tooltip";
            var content = new Label("Custom");
            content.AddToClassList(
                PackageManagerSubmodulePresentation.InformationCardTextClassName);
            card.Add(icon);
            card.Add(content);

            bool applied = PackageManagerSubmodulePresentation.ApplySourceCard(
                card,
                CreateInfo(isGitHub: false),
                null);

            Assert.That(applied, Is.True);
            Assert.That(content.text, Is.EqualTo("Git"));
            Assert.That(icon.ClassListContains(
                PackageManagerSubmodulePresentation.CustomSourceIconClassName), Is.False);
            Assert.That(icon.style.display.value, Is.EqualTo(DisplayStyle.Flex));
            Assert.That(icon.tooltip, Is.EqualTo("Built-in source tooltip"));
        }

        [Test]
        public void SourceReset_PreservesTooltipAssignedByBuiltInRefresh()
        {
            var card = new VisualElement();
            var icon = new VisualElement();
            icon.AddToClassList(
                PackageManagerSubmodulePresentation.InformationCardIconClassName);
            var content = new Label("Custom");
            content.AddToClassList(
                PackageManagerSubmodulePresentation.InformationCardTextClassName);
            card.Add(icon);
            card.Add(content);
            var texture = new Texture2D(1, 1) { hideFlags = HideFlags.HideAndDontSave };

            try
            {
                Assert.That(PackageManagerSubmodulePresentation.ApplySourceCard(
                    card,
                    CreateInfo(isGitHub: true),
                    texture), Is.True);
                icon.style.display = DisplayStyle.None;
                icon.tooltip = "Built-in source tooltip";

                PackageManagerSubmodulePresentation.ResetCustomSourceIcon(card);

                Assert.That(icon.ClassListContains(
                    PackageManagerSubmodulePresentation.CustomSourceIconClassName), Is.False);
                Assert.That(icon.tooltip, Is.EqualTo("Built-in source tooltip"));
                Assert.That(icon.style.display.value, Is.EqualTo(DisplayStyle.None));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(texture);
            }
        }

        [Test]
        public void HarmonyTargets_ResolveModernOrLegacyTagHookWithExpectedVersionArgument()
        {
            IReadOnlyList<MethodInfo> targets =
                PackageManagerSubmoduleHarmonyPatch.GetTagTargetMethods();

            Assert.That(targets, Is.Not.Empty,
                "Neither the modern nor legacy Package Manager tag label hook was found.");
            Assert.That(targets.All(method =>
                method.GetParameters().Length >= 1 &&
                method.GetParameters()[0].ParameterType.FullName ==
                    PackageManagerSubmoduleHarmonyPatch.PackageVersionInterfaceTypeName &&
                (method.GetParameters().Length == 1 ||
                 method.GetParameters()[1].ParameterType == typeof(bool))),
                Is.True);
            Assert.That(targets.Any(method =>
                method.Name == PackageManagerSubmoduleHarmonyPatch.RefreshMethodName ||
                method.Name == PackageManagerSubmoduleHarmonyPatch.LegacyCreateTagLabelMethodName),
                Is.True);
        }

        [Test]
        public void HarmonyPostfixes_KeepSpecialHarmonyArgumentNames()
        {
            ParameterInfo[] refreshParameters =
                PackageManagerSubmoduleHarmonyPatch.GetTagRefreshPostfixMethod().GetParameters();
            ParameterInfo[] factoryParameters =
                PackageManagerSubmoduleHarmonyPatch.GetTagFactoryPostfixMethod().GetParameters();
            ParameterInfo[] sourceParameters =
                PackageManagerSubmoduleHarmonyPatch.GetSourceRefreshPostfixMethod().GetParameters();

            Assert.That(refreshParameters.Select(parameter => parameter.Name),
                Is.EqualTo(new[] { "__instance", "__0" }));
            Assert.That(factoryParameters.Select(parameter => parameter.Name),
                Is.EqualTo(new[] { "__0", "__result" }));
            Assert.That(sourceParameters.Select(parameter => parameter.Name),
                Is.EqualTo(new[] { "__instance", "__0" }));

            ParameterInfo[] toolbarParameters =
                PackageManagerSubmoduleHarmonyPatch
                    .GetPackageToolbarRefreshPostfixMethod()
                    .GetParameters();
            Assert.That(toolbarParameters.Select(parameter => parameter.Name),
                Is.EqualTo(new[] { "__instance", "__0" }));

            ParameterInfo[] activationParameters =
                PackageManagerGitHubNativePresentationPatch
                    .GetPageActivationPrefix()
                    .GetParameters();
            ParameterInfo[] loadingParameters =
                PackageManagerGitHubNativePresentationPatch
                    .GetPageLoadingPostfix()
                    .GetParameters();
            Assert.That(activationParameters.Select(parameter => parameter.Name),
                Is.EqualTo(new[] { "__0" }));
            Assert.That(loadingParameters.Select(parameter => parameter.Name),
                Is.EqualTo(new[] { "__0", "__result" }));
            Assert.That(loadingParameters[1].ParameterType,
                Is.EqualTo(typeof(bool).MakeByRefType()));
        }

        [Test]
        public void HarmonyRuntimeRegistration_ContainsTagPostfix()
        {
            Assert.That(PackageManagerSubmoduleHarmonyPatch.TryPatch(), Is.True,
                PackageManagerSubmoduleHarmonyPatch.LastPatchError);
            Assert.That(
                PackageManagerSubmoduleHarmonyPatch.IsAnyTagPatchApplied(),
                Is.True);
        }

        [Test]
        public void SourceHook_IsOptionalButRegisteredWhenTypeExists()
        {
            Type sourceType = PackageManagerSubmoduleHarmonyPatch.FindLoadedType(
                PackageManagerSubmoduleHarmonyPatch.SourceInfoCardTypeName);
            MethodInfo target = PackageManagerSubmoduleHarmonyPatch.GetSourceTargetMethod();

            if (sourceType == null)
            {
                Assert.That(target, Is.Null,
                    "The optional SourceInfoCard hook must fail closed on older Unity versions.");
                return;
            }

            Assert.That(target, Is.Not.Null,
                "SourceInfoCard exists but its Refresh(IPackageVersion) hook was not resolved.");
            Assert.That(PackageManagerSubmoduleHarmonyPatch.TryPatch(), Is.True);
            Assert.That(PackageManagerSubmoduleHarmonyPatch.IsSourcePatchApplied(), Is.True);
        }

        [Test]
        public void NativeDiscoveryHooks_RegisterForAvailableDetailsAndRefreshContracts()
        {
            IReadOnlyList<MethodInfo> technicalTargets =
                PackageManagerGitHubNativePresentationPatch.GetTechnicalNameTargets();
            IReadOnlyList<MethodInfo> authorTargets =
                PackageManagerGitHubNativePresentationPatch.GetAuthorTargets();
            IReadOnlyList<MethodInfo> refreshTargets =
                PackageManagerGitHubNativePresentationPatch.GetPageRefreshTargets();
            IReadOnlyList<MethodInfo> activationTargets =
                PackageManagerGitHubNativePresentationPatch.GetPageActivationTargets();
            IReadOnlyList<MethodInfo> loadingTargets =
                PackageManagerGitHubNativePresentationPatch.GetPageLoadingTargets();

            Assert.That(technicalTargets, Is.Not.Empty);
            Assert.That(authorTargets, Is.Not.Empty);
            Assert.That(refreshTargets, Is.Not.Empty);
            Assert.That(activationTargets, Is.Not.Empty);
            Assert.That(loadingTargets, Is.Not.Empty);
            Assert.That(
                PackageManagerGitHubNativePresentationPatch
                    .HasRequiredDiscoveryLifecycleContract(),
                Is.True,
                "The native GitHub page must fail over as one unit when its " +
                "activation, refresh, loading, or completion seam drifts.");
            Assert.That(
                PackageManagerGitHubNativePresentationPatch.TryPatch(),
                Is.True);

            foreach (MethodInfo target in technicalTargets)
            {
                Assert.That(
                    PackageManagerGitHubNativePresentationPatch.IsPatchApplied(
                        target,
                        PackageManagerGitHubNativePresentationPatch
                            .GetTechnicalNamePostfix()),
                    Is.True);
            }

            foreach (MethodInfo target in authorTargets)
            {
                Assert.That(
                    PackageManagerGitHubNativePresentationPatch.IsPatchApplied(
                        target,
                        PackageManagerGitHubNativePresentationPatch
                            .GetAuthorPostfix()),
                    Is.True);
            }

            foreach (MethodInfo target in refreshTargets)
            {
                Assert.That(
                    PackageManagerGitHubNativePresentationPatch.IsPatchApplied(
                        target,
                        PackageManagerGitHubNativePresentationPatch
                            .GetPageRefreshPostfix()),
                    Is.True);
            }

            foreach (MethodInfo target in activationTargets)
            {
                Assert.That(
                    PackageManagerGitHubNativePresentationPatch.IsPrefixApplied(
                        target,
                        PackageManagerGitHubNativePresentationPatch
                            .GetPageActivationPrefix()),
                    Is.True);
            }

            foreach (MethodInfo target in loadingTargets)
            {
                Assert.That(
                    PackageManagerGitHubNativePresentationPatch.IsPatchApplied(
                        target,
                        PackageManagerGitHubNativePresentationPatch
                            .GetPageLoadingPostfix()),
                    Is.True);
            }
        }

        [TestCase("UnityRegistry", false, false, false)]
        [TestCase("UnityRegistry", false, true, false)]
        [TestCase("Extension/git-submodule-manager", false, false, false)]
        [TestCase("Extension/git-submodule-manager", false, true, true)]
        [TestCase("Extension/git-submodule-manager", true, false, true)]
        public void NativeDiscoveryLoading_OnlyAugmentsTheGitHubPage(
            string pageId,
            bool nativeIsRefreshing,
            bool discoveryIsLoading,
            bool expected)
        {
            Assert.That(
                PackageManagerGitHubNativePresentationPatch
                    .ShouldReportDiscoveryLoading(
                        pageId,
                        nativeIsRefreshing,
                        discoveryIsLoading),
                Is.EqualTo(expected));
        }

        [Test]
        public void NativeDiscoveryLoading_UsesExactNativeStatusContracts()
        {
            if (!PackageManagerSubmoduleNativePage.IsSupportedContract())
                return;

            MethodInfo statusUpdate = PackageManagerGitHubNativePresentationPatch
                .GetPackageStatusUpdateMethod();
            PropertyInfo statusProperty = PackageManagerGitHubNativePresentationPatch
                .GetPackageStatusBarProperty();
            MethodInfo listRebuild = PackageManagerGitHubNativePresentationPatch
                .GetListAreaRebuildMethod();

            Assert.That(statusUpdate, Is.Not.Null);
            Assert.That(statusUpdate.ReturnType, Is.EqualTo(typeof(void)));
            Assert.That(statusUpdate.GetParameters(), Is.Empty);
            Assert.That(statusProperty, Is.Not.Null);
            Assert.That(
                statusProperty.PropertyType.FullName,
                Is.EqualTo(
                    PackageManagerGitHubNativePresentationPatch
                        .PackageStatusBarTypeName));
            Assert.That(listRebuild, Is.Not.Null);
            Assert.That(listRebuild.ReturnType, Is.EqualTo(typeof(void)));
            Assert.That(listRebuild.GetParameters(), Has.Length.EqualTo(1));
            Assert.That(
                listRebuild.GetParameters()[0].ParameterType.FullName,
                Is.EqualTo(
                    PackageManagerGitHubNativePresentationPatch
                        .PageInterfaceTypeName));
        }

        [Test]
        public void NativePackageToolbarHook_IsRegisteredWhenContractExists()
        {
            IReadOnlyList<MethodInfo> targets =
                PackageManagerSubmoduleHarmonyPatch.GetPackageToolbarTargetMethods();
            if (targets.Count == 0)
                return;

            Assert.That(PackageManagerSubmoduleHarmonyPatch.TryPatch(), Is.True);
            Assert.That(
                PackageManagerSubmoduleHarmonyPatch.IsPackageToolbarPatchApplied(),
                Is.True);
        }

        [Test]
        public void ThemeIconHelper_LoadsExistingGitArtwork()
        {
            Assert.That(GitSubmoduleManagerIcons.GetGitIcon(false), Is.Not.Null);
            Assert.That(GitSubmoduleManagerIcons.GetGitIcon(true), Is.Not.Null);
        }

        private static FieldInfo FindInstanceField(Type type, string fieldName)
        {
            const BindingFlags flags = BindingFlags.Instance |
                                       BindingFlags.Public |
                                       BindingFlags.NonPublic;
            for (Type current = type; current != null; current = current.BaseType)
            {
                FieldInfo field = current.GetField(fieldName, flags);
                if (field != null)
                    return field;
            }

            return null;
        }

        private static PropertyInfo FindInstanceProperty(
            Type type,
            string propertyName)
        {
            const BindingFlags flags = BindingFlags.Instance |
                                       BindingFlags.Public |
                                       BindingFlags.NonPublic;
            for (Type current = type; current != null; current = current.BaseType)
            {
                PropertyInfo property = current.GetProperty(propertyName, flags);
                if (property != null && property.GetIndexParameters().Length == 0)
                    return property;
            }

            if (type != null)
            {
                foreach (Type interfaceType in type.GetInterfaces())
                {
                    PropertyInfo property = interfaceType.GetProperty(
                        propertyName,
                        flags);
                    if (property != null &&
                        property.GetIndexParameters().Length == 0)
                    {
                        return property;
                    }
                }
            }

            return null;
        }

        private static PackageManagerSubmoduleSnapshotData CreateSnapshot(
            string projectRoot,
            string repositoryUrl)
        {
            return PackageManagerSubmoduleSnapshotData.Create(
                new[]
                {
                    new GitPackageInfo
                    {
                        Path = "Packages/com.example.repository",
                        PackageName = "com.example.repository",
                        Url = repositoryUrl
                    }
                },
                projectRoot);
        }

        private static PackageManagerSubmoduleInfo CreateInfo(bool isGitHub)
        {
            return new PackageManagerSubmoduleInfo(
                "com.example.repository",
                "Packages/com.example.repository",
                "/project/Packages/com.example.repository",
                isGitHub
                    ? "https://github.com/owner/repository.git"
                    : "ssh://git@git.example.com/team/repository.git",
                isGitHub);
        }

        private static PackageManagerSubmoduleInfo CreateGitHubInfo(
            string repositoryUrl)
        {
            return new PackageManagerSubmoduleInfo(
                "com.example.repository",
                "Packages/com.example.repository",
                "/project/Packages/com.example.repository",
                repositoryUrl,
                true);
        }

        private static PackageManagerGitHubDiscoverySnapshot CreateDiscoverySnapshot(
            string owner,
            string repository,
            bool isPrivate)
        {
            var discoveredRepository = new PackageManagerGitHubRepository(
                new GitHubRepo
                {
                    NodeId = "NODE-" + owner + "-" + repository,
                    Owner = owner,
                    Name = repository,
                    Url = $"https://github.com/{owner}/{repository}.git",
                    DefaultBranch = "main",
                    IsPrivate = isPrivate,
                    ManifestState = PackageManifestState.Valid,
                    DeclaredPackageName = "com.example.repository",
                    DeclaredDisplayName = "Example Repository",
                    DeclaredVersion = "1.0.0"
                });
            return new PackageManagerGitHubDiscoverySnapshot(
                new[] { discoveredRepository },
                false,
                string.Empty,
                string.Empty,
                1,
                1,
                1,
                0,
                1);
        }
    }
}
