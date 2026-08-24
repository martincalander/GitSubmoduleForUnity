using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace MartinCalander.GitSubmoduleManager.Editor.Tests
{
    [Parallelizable(ParallelScope.None)]
    public sealed class PackageManagerGitHubNativeActionsTests
    {
        private const BindingFlags AnyInstance =
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        [Test]
        public void LiveContract_ResolvesNativePrimaryActionSurface()
        {
#if !UNITY_2023_2_OR_NEWER
            Assert.Ignore(
                "The native Sources > GitHub page uses the Unity 2023.2+ Package Manager extension-page contract; older Editors use the compatibility host.");
#else
            Type rootType = PackageManagerSubmoduleHarmonyPatch.FindLoadedType(
                PackageManagerGitHubNativeActions.PackageManagerWindowRootTypeName);
            Type toolbarType = PackageManagerSubmoduleHarmonyPatch.FindLoadedType(
                PackageManagerGitHubNativeActions.PackageToolbarTypeName);
            Type linksType = PackageManagerSubmoduleHarmonyPatch.FindLoadedType(
                PackageManagerGitHubNativeActions.PackageDetailsLinksTypeName);
            Type headerType = PackageManagerSubmoduleHarmonyPatch.FindLoadedType(
                PackageManagerGitHubNativeActions.PackageDetailsHeaderTypeName);

            Assert.That(rootType, Is.Not.Null);
            Assert.That(typeof(VisualElement).IsAssignableFrom(rootType), Is.True);
            Assert.That(toolbarType, Is.Not.Null);
            Assert.That(typeof(VisualElement).IsAssignableFrom(toolbarType), Is.True);
            Assert.That(linksType, Is.Not.Null);
            Assert.That(typeof(VisualElement).IsAssignableFrom(linksType), Is.True);
            Assert.That(headerType, Is.Not.Null);
            Assert.That(typeof(VisualElement).IsAssignableFrom(headerType), Is.True);

            FieldInfo primaryActions = toolbarType.GetField(
                PackageManagerGitHubNativeActions.BuiltInActionsFieldName,
                AnyInstance);
            Assert.That(primaryActions, Is.Not.Null);
            Assert.That(primaryActions.IsStatic, Is.False);
            Assert.That(
                typeof(VisualElement).IsAssignableFrom(primaryActions.FieldType),
                Is.True);
            PropertyInfo detailsLinks = headerType.GetProperty(
                PackageManagerGitHubNativeActions.DetailsLinksPropertyName,
                AnyInstance);
            Assert.That(detailsLinks, Is.Not.Null);
            Assert.That(detailsLinks.GetIndexParameters(), Is.Empty);
            Assert.That(detailsLinks.PropertyType, Is.EqualTo(linksType));
            Assert.That(
                PackageManagerGitHubNativeActions.HasSupportedLiveContract(),
                Is.True);
            Assert.That(
                PackageManagerGitHubNativeActions.HasSupportedSelectionContract(),
                Is.True,
                "The active-page selection seam must resolve independently of " +
                "the primary-actions mounting contract.");
#endif
        }

        [Test]
        public void RefreshPackage_PrefersAuthoritativeSelectionAndFailsClosedOnInvalidSelection()
        {
            var staleToolbarPackage = new object();
            var authoritativePackage = new object();

            Assert.That(
                PackageManagerGitHubNativeActions.SelectPackageForRefresh(
                    staleToolbarPackage,
                    true,
                    true,
                    authoritativePackage),
                Is.SameAs(authoritativePackage));
            Assert.That(
                PackageManagerGitHubNativeActions.SelectPackageForRefresh(
                    staleToolbarPackage,
                    true,
                    false,
                    null),
                Is.Null,
                "An exact but zero/multi/missing selection must not reuse stale " +
                "toolbar state.");
            Assert.That(
                PackageManagerGitHubNativeActions.SelectPackageForRefresh(
                    staleToolbarPackage,
                    false,
                    false,
                    null),
                Is.SameAs(staleToolbarPackage),
                "Harmony's explicit package remains the presentation fallback " +
                "when the optional selection seam is unavailable.");
        }

        [Test]
        public void ExactSingleSelection_ResolvesOnlyMatchingDatabasePackage()
        {
            var package = new SelectionPackageFixture(
                "com.example.package",
                "com.example.package");

            Assert.That(
                PackageManagerGitHubNativeActions.TryResolveExactSingleSelection(
                    1,
                    new[] { "com.example.package" },
                    id => string.Equals(
                            id,
                            package.UniqueId,
                            StringComparison.Ordinal)
                        ? package
                        : null,
                    candidate => ((SelectionPackageFixture)candidate).UniqueId,
                    candidate => ((SelectionPackageFixture)candidate).Name,
                    out object resolved),
                Is.True);
            Assert.That(resolved, Is.SameAs(package));
        }

        [Test]
        public void ExactSingleSelection_FailsClosedOnCountLookupAndIdentityDrift()
        {
            var package = new SelectionPackageFixture(
                "com.example.package",
                "com.example.package");
            Func<string, object> lookup = _ => package;
            Func<object, string> uniqueId = candidate =>
                ((SelectionPackageFixture)candidate).UniqueId;
            Func<object, string> name = candidate =>
                ((SelectionPackageFixture)candidate).Name;

            Assert.That(
                PackageManagerGitHubNativeActions.TryResolveExactSingleSelection(
                    0,
                    Array.Empty<string>(),
                    lookup,
                    uniqueId,
                    name,
                    out _),
                Is.False);
            Assert.That(
                PackageManagerGitHubNativeActions.TryResolveExactSingleSelection(
                    1,
                    new[] { "first", "second" },
                    lookup,
                    uniqueId,
                    name,
                    out _),
                Is.False,
                "The reported count and actual enumeration must agree exactly.");
            Assert.That(
                PackageManagerGitHubNativeActions.TryResolveExactSingleSelection(
                    1,
                    new[] { "com.example.package" },
                    _ => null,
                    uniqueId,
                    name,
                    out _),
                Is.False,
                "A package database miss must not fall back to toolbar state.");
            Assert.That(
                PackageManagerGitHubNativeActions.TryResolveExactSingleSelection(
                    1,
                    new[] { "com.example.other" },
                    lookup,
                    uniqueId,
                    name,
                    out _),
                Is.False,
                "The resolved package must retain the exact selected identity.");
            Assert.That(
                PackageManagerGitHubNativeActions.TryResolveExactSingleSelection(
                    1,
                    new[] { "com.example.package" },
                    _ => throw new InvalidOperationException("database drift"),
                    uniqueId,
                    name,
                    out _),
                Is.False,
                "Reflection or database failures must remain contained.");
        }

        [Test]
        public void PrimaryActionsField_AcceptsUnnamedContainer()
        {
            var fixture = new PrimaryActionsFieldFixture();
            FieldInfo field = typeof(PrimaryActionsFieldFixture).GetField(
                PackageManagerGitHubNativeActions.BuiltInActionsFieldName,
                AnyInstance);

            VisualElement unnamedPrimaryActions =
                PackageManagerGitHubNativeActions
                    .ReadVerifiedPrimaryActionsContainer(fixture, field);

            Assert.That(unnamedPrimaryActions, Is.SameAs(fixture.PrimaryActions));
            Assert.That(unnamedPrimaryActions.name, Is.Null.Or.Empty);

            var correctLinks = new VisualElement
            {
                name = PackageManagerGitHubDetails.NativeDetailsLinksContainerName
            };

            using PackageManagerGitHubDetails details = CreateDetails(
                unnamedPrimaryActions,
                correctLinks);

            Assert.That(details.Controls.parent, Is.SameAs(unnamedPrimaryActions));
        }

        [Test]
        public void Create_RequiresExactDetailsLinksContainerAndFailsClosed()
        {
            var primaryActions = new VisualElement();
            var wrongLinks = new VisualElement { name = "details" };
            var correctLinks = new VisualElement
            {
                name = PackageManagerGitHubDetails.NativeDetailsLinksContainerName
            };

            Assert.That(
                PackageManagerGitHubDetails.TryCreate(
                    null,
                    correctLinks,
                    (_, _) => { },
                    _ => { },
                    false,
                    out _),
                Is.False);
            Assert.That(
                PackageManagerGitHubDetails.TryCreate(
                    primaryActions,
                    wrongLinks,
                    (_, _) => { },
                    _ => { },
                    false,
                    out _),
                Is.False);
        }

        [Test]
        public void LegacyTryCreate_RemainsSubmoduleOnlyAndInvokesOldCallback()
        {
            var primaryActions = new VisualElement();
            var detailsLinks = new VisualElement
            {
                name = PackageManagerGitHubDetails.NativeDetailsLinksContainerName
            };
            PackageManagerGitHubRepository installedRepository = null;
            string installedBranch = string.Empty;

            Assert.That(
                PackageManagerGitHubDetails.TryCreate(
                    primaryActions,
                    detailsLinks,
                    (repository, branch) =>
                    {
                        installedRepository = repository;
                        installedBranch = branch;
                    },
                    _ => { },
                    false,
                    out PackageManagerGitHubDetails details),
                Is.True);
            using (details)
            {
                PackageManagerGitHubRepository repository = CreateRepository(
                    "repository",
                    "main");
                details.Refresh(repository);
                details.SetInstallState(true, true, "Ready");

                Assert.That(
                    details.SelectedInstallMode,
                    Is.EqualTo(PackageManagerGitInstallMode.GitSubmodule));
                Assert.That(
                    details.InstallMenu.style.display.value,
                    Is.EqualTo(DisplayStyle.None));
                Assert.That(
                    details.InstallButton.style.display.value,
                    Is.EqualTo(DisplayStyle.Flex));
                Assert.That(
                    details.InstallButton.text,
                    Is.EqualTo(L10n.Tr(
                        PackageManagerGitHubDetails.InstallText)));

                details.TriggerInstall();
                details.TriggerInstall();

                Assert.That(installedRepository, Is.SameAs(repository));
                Assert.That(installedBranch, Is.EqualTo("main"));
            }
        }

        [Test]
        public void PrimaryActionsResolver_RejectsWrongToolbarType()
        {
            Assert.That(
                PackageManagerGitHubNativeActions.ResolvePrimaryActionsContainer(
                    new VisualElement()),
                Is.Null);

            var wrongFieldFixture = new WrongPrimaryActionsFieldFixture();
            FieldInfo wrongTypeField = typeof(WrongPrimaryActionsFieldFixture).GetField(
                PackageManagerGitHubNativeActions.BuiltInActionsFieldName,
                AnyInstance);
            Assert.That(
                PackageManagerGitHubNativeActions
                    .ReadVerifiedPrimaryActionsContainer(
                        wrongFieldFixture,
                        wrongTypeField),
                Is.Null);
            Assert.That(
                PackageManagerGitHubNativeActions
                    .ReadVerifiedPrimaryActionsContainer(
                        new PrimaryActionsFieldFixture(),
                        typeof(UnrelatedPrimaryActionsFieldFixture).GetField(
                            PackageManagerGitHubNativeActions.BuiltInActionsFieldName,
                            AnyInstance)),
                Is.Null);
        }

        [Test]
        public void PrimaryAction_IsSingleInstallDropdownAndNeverUsesExtensionsOverflow()
        {
            using PackageManagerGitHubDetails details = CreateDetails(
                out VisualElement primaryActions,
                out VisualElement extensionItems,
                out _,
                (_, _) => { },
                _ => { });
            details.Refresh(CreateRepository("alpha", "main"));
            details.SetInstallState(true, true, "Ready");

            Assert.That(PackageManagerGitHubDetails.InstallText, Is.EqualTo("Install"));
            Assert.That(
                details.InstallMenu.text,
                Is.EqualTo(PackageManagerGitHubDetails.InstallText));
            Assert.That(details.InstallMenu.parent, Is.SameAs(details.Controls));
            Assert.That(details.Controls.parent, Is.SameAs(primaryActions));
            var controlsInOrder = new List<VisualElement>(
                details.Controls.Children());
            Assert.That(
                controlsInOrder.IndexOf(details.BranchField),
                Is.LessThan(controlsInOrder.IndexOf(details.InstallMenu)));
            Assert.That(
                controlsInOrder.IndexOf(details.InstallMenu),
                Is.LessThan(controlsInOrder.IndexOf(details.InstallButton)));
            Assert.That(
                details.Controls.Query<DropdownField>().ToList().Count,
                Is.EqualTo(1));
            IReadOnlyList<DropdownMenuItem> menuItems =
                details.InstallMenu.menu.MenuItems();
            Assert.That(
                menuItems.Count,
                Is.EqualTo(2));
            var firstMenuAction = menuItems[0] as DropdownMenuAction;
            var secondMenuAction = menuItems[1] as DropdownMenuAction;
            Assert.That(firstMenuAction, Is.Not.Null);
            Assert.That(secondMenuAction, Is.Not.Null);
            firstMenuAction.UpdateActionStatus(null);
            secondMenuAction.UpdateActionStatus(null);
            Assert.That(
                new[] { firstMenuAction.name, secondMenuAction.name },
                Is.EqualTo(new[]
                {
                    L10n.Tr(PackageManagerGitHubDetails
                        .InstallAsGitSubmoduleText),
                    L10n.Tr(PackageManagerGitHubDetails
                        .InstallAsReadOnlyPackageText)
                }));
            Assert.That(
                firstMenuAction.status,
                Is.EqualTo(DropdownMenuAction.Status.Normal));
            Assert.That(
                secondMenuAction.status,
                Is.EqualTo(DropdownMenuAction.Status.Normal));
            Assert.That(
                details.SelectedInstallMode,
                Is.EqualTo(PackageManagerGitInstallMode.GitSubmodule));
            Assert.That(
                details.InstallMenu.style.display.value,
                Is.EqualTo(DisplayStyle.Flex));
            Assert.That(
                details.InstallButton.style.display.value,
                Is.EqualTo(DisplayStyle.None));
            Assert.That(
                primaryActions.Q<ToolbarMenu>(
                    PackageManagerGitHubDetails.InstallActionElementName),
                Is.SameAs(details.InstallMenu));
            Assert.That(
                extensionItems.Q<ToolbarMenu>(
                    PackageManagerGitHubDetails.InstallActionElementName),
                Is.Null);
            Assert.That(
                details.CancelInstallButton.style.display.value,
                Is.EqualTo(DisplayStyle.None));
            Assert.That(
                details.InstallFeedback.style.display.value,
                Is.EqualTo(DisplayStyle.None));
            Assert.That(
                details.InstallFeedback.parent?.name,
                Is.EqualTo(PackageManagerGitHubDetails.NativeHelpBoxContainerName));
        }

        [Test]
        public void InstallDropdown_DisablesOnlyTheUnavailableInstallMode()
        {
            int installCount = 0;
            using PackageManagerGitHubDetails details = CreateDetails(
                out _,
                out _,
                out _,
                (_, _, _) => installCount++,
                _ => { });
            details.Refresh(CreateRepository("repository", "main"));
            details.SetInstallState(
                true,
                false,
                "Git submodules are unavailable.",
                true,
                "Read-only install is ready.");

            Assert.That(details.InstallMenu.enabledSelf, Is.True);
            DropdownMenuAction gitSubmoduleAction = FindInstallMenuAction(
                details,
                PackageManagerGitInstallMode.GitSubmodule);
            DropdownMenuAction readOnlyPackageAction = FindInstallMenuAction(
                details,
                PackageManagerGitInstallMode.ReadOnlyPackage);
            gitSubmoduleAction.UpdateActionStatus(null);
            readOnlyPackageAction.UpdateActionStatus(null);

            Assert.That(
                gitSubmoduleAction.status,
                Is.EqualTo(DropdownMenuAction.Status.Disabled));
            Assert.That(
                readOnlyPackageAction.status,
                Is.EqualTo(DropdownMenuAction.Status.Normal));

            gitSubmoduleAction.Execute();

            Assert.That(installCount, Is.Zero);
            Assert.That(details.IsInstallConfirmationPending, Is.False);
            Assert.That(
                details.InstallFeedback.style.display.value,
                Is.EqualTo(DisplayStyle.None));

            readOnlyPackageAction.Execute();

            Assert.That(details.IsInstallConfirmationPending, Is.True);
            Assert.That(
                details.SelectedInstallMode,
                Is.EqualTo(PackageManagerGitInstallMode.ReadOnlyPackage));
            Assert.That(
                details.InstallFeedback.text,
                Does.Contain("read-only Package Manager Git dependency"));
        }

        [TestCase(
            "git@github.com:example/repository.git",
            "https://github.com/example/repository")]
        [TestCase(
            "ssh://git@github.com/example/repository.git",
            "https://github.com/example/repository")]
        [TestCase(
            "https://github.com/example/repository.git",
            "https://github.com/example/repository")]
        public void RepositoryWebUrl_ConvertsSupportedCloneUrls(
            string cloneUrl,
            string expectedWebUrl)
        {
            Assert.That(
                GitUtility.TryGetRepositoryWebUrl(cloneUrl, out string webUrl),
                Is.True);
            Assert.That(webUrl, Is.EqualTo(expectedWebUrl));
        }

        [Test]
        public void RepositoryLink_IsVisibleAndOpensSanitizedWebsite()
        {
            string openedUrl = string.Empty;
            using PackageManagerGitHubDetails details = CreateDetails(
                out _,
                out _,
                out VisualElement links,
                (_, _) => { },
                url => openedUrl = url);
            details.Refresh(CreateRepository(
                "repository",
                "main",
                "git@github.com:example/repository.git"));

            Assert.That(
                PackageManagerGitHubDetails.RepositoryLinkText,
                Is.EqualTo("Repository"));
            Assert.That(
                details.RepositoryLinkButton.text,
                Is.EqualTo(L10n.Tr("Repository")));
            Assert.That(details.RepositoryLinkButton.ClassListContains("link"), Is.True);
            Assert.That(details.RepositoryLinkButton.parent, Is.Not.Null);
            Assert.That(links.Contains(details.RepositoryLinkButton), Is.True);
            Assert.That(
                links.style.display.value,
                Is.EqualTo(DisplayStyle.Flex));
            Assert.That(details.OpenRepositoryWebsite(), Is.True);
            Assert.That(
                openedUrl,
                Is.EqualTo("https://github.com/example/repository"));
        }

        [Test]
        public void RepositoryLink_ReappearsAfterNativeLinksAreRecycled()
        {
            using PackageManagerGitHubDetails details = CreateDetails(
                out _,
                out _,
                out VisualElement links,
                (_, _) => { },
                _ => { });
            PackageManagerGitHubRepository repository = CreateRepository(
                "repository",
                "main");
            details.Refresh(repository);
            Assert.That(links.Contains(details.RepositoryLinkButton), Is.True);

            links.Clear();
            Assert.That(links.Contains(details.RepositoryLinkButton), Is.False);
            details.Refresh(repository);

            Assert.That(links.Contains(details.RepositoryLinkButton), Is.True);
        }

        [Test]
        public void RepositoryLink_RecycleThenHideRestoresOriginalLinksDisplay()
        {
            using PackageManagerGitHubDetails details = CreateDetails(
                out _,
                out _,
                out VisualElement links,
                (_, _) => { },
                _ => { });
            links.style.display = DisplayStyle.None;
            PackageManagerGitHubRepository repository = CreateRepository(
                "repository",
                "main");

            details.Refresh(repository);
            Assert.That(links.style.display.value, Is.EqualTo(DisplayStyle.Flex));
            links.Clear();
            details.Refresh(repository);
            Assert.That(links.style.display.value, Is.EqualTo(DisplayStyle.Flex));

            details.Refresh(null);

            Assert.That(links.style.display.value, Is.EqualTo(DisplayStyle.None));
        }

        [Test]
        public void BranchChoices_PutMainBeforeRepositoryDefaultAndRejectInvalidOrDuplicateRefs()
        {
            List<string> choices = PackageManagerGitHubDetails.BuildBranchChoices(
                "agents/verdaccio",
                new[]
                {
                    "release",
                    " main ",
                    "feature/new-ui",
                    "bad..branch",
                    string.Empty,
                    "release"
                });

            Assert.That(
                choices,
                Is.EqualTo(new[]
                {
                    "main",
                    "agents/verdaccio",
                    "release",
                    "feature/new-ui"
                }));
        }

        [Test]
        public void BranchSelector_DefaultsToMainBeforeDiscoveryCompletes()
        {
            using PackageManagerGitHubDetails details = CreateDetails(
                out _,
                out _,
                out _,
                (_, _) => { },
                _ => { });

            details.Refresh(CreateRepository("repository", "agents/verdaccio"));

            Assert.That(details.SelectedBranch, Is.EqualTo("main"));
            Assert.That(details.BranchField.value, Is.EqualTo("main"));
            Assert.That(
                details.BranchField.choices,
                Is.EqualTo(new[] { "main", "agents/verdaccio" }));
        }

        [Test]
        public void BranchSelector_FallsBackWhenAuthoritativeBranchesDoNotContainMain()
        {
            using PackageManagerGitHubDetails details = CreateDetails(
                out _,
                out _,
                out _,
                (_, _) => { },
                _ => { });

            details.Refresh(CreateRepository("repository", "trunk"));
            Assert.That(details.SelectedBranch, Is.EqualTo("main"));

            details.ApplyAvailableBranchesForTests(
                new[] { "trunk", "release" });

            Assert.That(details.SelectedBranch, Is.EqualTo("trunk"));
            Assert.That(details.BranchField.value, Is.EqualTo("trunk"));
            Assert.That(
                details.BranchField.choices,
                Is.EqualTo(new[] { "trunk", "release" }));
        }

        [UnityTest]
        public IEnumerator InstallAction_PassesTheSelectedDiscoveredBranch()
        {
            PackageManagerGitHubRepository installedRepository = null;
            string installedBranch = string.Empty;
            PackageManagerGitHubDetails details = CreateDetails(
                out _,
                out VisualElement extensionItems,
                out _,
                (repository, branch) =>
                {
                    installedRepository = repository;
                    installedBranch = branch;
                },
                _ => { });
            var host = ScriptableObject.CreateInstance<NativeActionsHostWindow>();
            try
            {
                VisualElement fixtureRoot = extensionItems.parent?.parent;
                Assert.That(fixtureRoot, Is.Not.Null);
                host.position = new Rect(100f, 100f, 500f, 200f);
                host.Show();
                host.rootVisualElement.Add(fixtureRoot);
                yield return null;

                PackageManagerGitHubRepository repository = CreateRepository(
                    "repository",
                    "main");
                details.Refresh(repository);
                details.ApplyAvailableBranchesForTests(
                    new[] { "main", "release", "feature/native-ui" });
                details.SetInstallState(true, true, "Ready");

                details.BranchField.value = "release";
                host.Repaint();
                yield return null;

                Assert.That(details.SelectedBranch, Is.EqualTo("release"));
                details.InstallMenu.Focus();
                ExecuteInstallMenuAction(
                    details,
                    PackageManagerGitInstallMode.GitSubmodule);
                // Focus is verified on a later editor update after event
                // propagation, so allow the bounded retry to settle.
                for (int frame = 0;
                     frame < 10 &&
                     !ReferenceEquals(
                         host.rootVisualElement.focusController.focusedElement,
                         details.InstallButton);
                     frame++)
                {
                    yield return null;
                }

                Assert.That(installedRepository, Is.Null);
                Assert.That(installedBranch, Is.Empty);
                Assert.That(details.IsInstallConfirmationPending, Is.True);
                Assert.That(
                    details.InstallButton.text,
                    Is.EqualTo(L10n.Tr(
                        PackageManagerGitHubDetails.ConfirmInstallText)));
                Assert.That(
                    details.CancelInstallButton.style.display.value,
                    Is.EqualTo(DisplayStyle.Flex));
                Assert.That(
                    details.InstallFeedback.style.display.value,
                    Is.EqualTo(DisplayStyle.Flex));
                Assert.That(
                    details.InstallFeedback.messageType,
                    Is.EqualTo(HelpBoxMessageType.Warning));
                Label feedbackLabel = details.InstallFeedback.Q<Label>(
                    className: HelpBox.labelUssClassName);
                Assert.That(feedbackLabel, Is.Not.Null);
                Assert.That(feedbackLabel.enableRichText, Is.False);
                Assert.That(details.BranchField.enabledSelf, Is.False);
                Assert.That(
                    details.InstallMenu.style.display.value,
                    Is.EqualTo(DisplayStyle.None));
                Assert.That(
                    details.InstallButton.style.display.value,
                    Is.EqualTo(DisplayStyle.Flex));
                Assert.That(
                    host.rootVisualElement.focusController.focusedElement,
                    Is.SameAs(details.InstallButton));

                SendNavigationSubmit(details.InstallButton);

                Assert.That(installedRepository, Is.SameAs(repository));
                Assert.That(installedBranch, Is.EqualTo("release"));
                Assert.That(details.IsInstalling, Is.True);
                Assert.That(
                    details.InstallButton.text,
                    Is.EqualTo(L10n.Tr(
                        PackageManagerGitHubDetails.InstallingText)));
                Assert.That(details.InstallButton.enabledSelf, Is.False);
            }
            finally
            {
                details.Dispose();
                if (host != null)
                    host.Close();
            }
        }

        [Test]
        public void InstallAction_PassesReadOnlyModeAndUsesModeBoundCopy()
        {
            PackageManagerGitHubRepository installedRepository = null;
            string installedBranch = string.Empty;
            PackageManagerGitInstallMode installedMode =
                PackageManagerGitInstallMode.GitSubmodule;
            using PackageManagerGitHubDetails details = CreateDetails(
                out _,
                out _,
                out _,
                (repository, branch, installMode) =>
                {
                    installedRepository = repository;
                    installedBranch = branch;
                    installedMode = installMode;
                },
                _ => { });
            PackageManagerGitHubRepository repository = CreateRepository(
                "repository",
                "main");
            details.Refresh(repository);
            details.SetInstallState(true, true, "Ready");

            ExecuteInstallMenuAction(
                details,
                PackageManagerGitInstallMode.ReadOnlyPackage);

            Assert.That(
                details.SelectedInstallMode,
                Is.EqualTo(PackageManagerGitInstallMode.ReadOnlyPackage));

            Assert.That(details.IsInstallConfirmationPending, Is.True);
            Assert.That(
                details.InstallMenu.style.display.value,
                Is.EqualTo(DisplayStyle.None));
            Assert.That(
                details.InstallButton.style.display.value,
                Is.EqualTo(DisplayStyle.Flex));
            Assert.That(
                details.InstallFeedback.text,
                Does.Contain("read-only Package Manager Git dependency"));
            Assert.That(
                details.InstallFeedback.text,
                Does.Not.Contain("Packages/com.example.repository"));
            Assert.That(installedRepository, Is.Null);

            details.TriggerInstall();

            Assert.That(installedRepository, Is.SameAs(repository));
            Assert.That(installedBranch, Is.EqualTo("main"));
            Assert.That(
                installedMode,
                Is.EqualTo(PackageManagerGitInstallMode.ReadOnlyPackage));
            Assert.That(details.IsInstalling, Is.True);
            Assert.That(
                details.InstallFeedback.text,
                Does.Contain("as a read-only Package Manager package"));
            Assert.That(details.InstallMenu.enabledSelf, Is.False);

            details.ShowInstallCompleted(string.Empty);

            Assert.That(
                details.InstallFeedback.text,
                Does.Contain("Read-only package installed"));
        }

        [Test]
        public void InstallMode_DefaultsToSubmoduleForEachRepositorySelection()
        {
            using PackageManagerGitHubDetails details = CreateDetails(
                out _,
                out _,
                out _,
                (_, _, _) => { },
                _ => { });
            details.Refresh(CreateRepository("first", "main"));
            details.SetInstallState(true, true, "Ready");
            details.SelectInstallModeForTests(
                PackageManagerGitInstallMode.ReadOnlyPackage);
            Assert.That(
                details.SelectedInstallMode,
                Is.EqualTo(PackageManagerGitInstallMode.ReadOnlyPackage));

            details.Refresh(CreateRepository("second", "main"));

            Assert.That(
                details.SelectedInstallMode,
                Is.EqualTo(PackageManagerGitInstallMode.GitSubmodule));
            Assert.That(
                details.InstallMenu.style.display.value,
                Is.EqualTo(DisplayStyle.Flex));
            Assert.That(
                details.InstallMenu.text,
                Is.EqualTo(L10n.Tr(
                    PackageManagerGitHubDetails.InstallText)));
        }

        [UnityTest]
        public IEnumerator InstallConfirmation_CancelRestoresIdleWithoutInstalling()
        {
            int installCount = 0;
            PackageManagerGitHubDetails details = CreateDetails(
                out _,
                out VisualElement extensionItems,
                out _,
                (_, _) => installCount++,
                _ => { });
            var host = ScriptableObject.CreateInstance<NativeActionsHostWindow>();
            try
            {
                VisualElement fixtureRoot = extensionItems.parent?.parent;
                Assert.That(fixtureRoot, Is.Not.Null);
                host.position = new Rect(100f, 100f, 500f, 200f);
                host.Show();
                host.rootVisualElement.Add(fixtureRoot);
                yield return null;

                details.Refresh(CreateRepository("repository", "main"));
                details.SetInstallState(true, true, "Ready");
                ExecuteInstallMenuAction(
                    details,
                    PackageManagerGitInstallMode.GitSubmodule);
                yield return null;
                Assert.That(details.IsInstallConfirmationPending, Is.True);

                SendNavigationSubmit(details.CancelInstallButton);
                for (int frame = 0;
                     frame < 10 &&
                     !ReferenceEquals(
                         host.rootVisualElement.focusController.focusedElement,
                         details.InstallMenu);
                     frame++)
                {
                    yield return null;
                }

                Assert.That(installCount, Is.Zero);
                Assert.That(details.IsInstallConfirmationPending, Is.False);
                Assert.That(
                    details.InstallMenu.text,
                    Is.EqualTo(L10n.Tr(
                        PackageManagerGitHubDetails.InstallText)));
                Assert.That(details.InstallMenu.enabledSelf, Is.True);
                Assert.That(
                    details.InstallMenu.style.display.value,
                    Is.EqualTo(DisplayStyle.Flex));
                Assert.That(
                    details.InstallButton.style.display.value,
                    Is.EqualTo(DisplayStyle.None));
                Assert.That(details.BranchField.enabledSelf, Is.True);
                Assert.That(
                    details.CancelInstallButton.style.display.value,
                    Is.EqualTo(DisplayStyle.None));
                Assert.That(
                    details.InstallFeedback.style.display.value,
                    Is.EqualTo(DisplayStyle.None));
                Assert.That(
                    host.rootVisualElement.focusController.focusedElement,
                    Is.SameAs(details.InstallMenu));
            }
            finally
            {
                details.Dispose();
                if (host != null)
                    host.Close();
            }
        }

        [UnityTest]
        public IEnumerator DeferredFocus_DoesNotStealFromAnotherLiveControl()
        {
            PackageManagerGitHubDetails details = CreateDetails(
                out _,
                out VisualElement extensionItems,
                out _,
                (_, _) => { },
                _ => { });
            var host = ScriptableObject.CreateInstance<NativeActionsHostWindow>();
            try
            {
                VisualElement fixtureRoot = extensionItems.parent?.parent;
                Assert.That(fixtureRoot, Is.Not.Null);
                host.position = new Rect(100f, 100f, 500f, 200f);
                host.Show();
                host.rootVisualElement.Add(fixtureRoot);
                yield return null;

                details.Refresh(CreateRepository("repository", "main"));
                details.SetInstallState(true, true, "Ready");
                ExecuteInstallMenuAction(
                    details,
                    PackageManagerGitInstallMode.GitSubmodule);

                for (int frame = 0;
                     frame < 10 &&
                     !ReferenceEquals(
                         host.rootVisualElement.focusController.focusedElement,
                         details.InstallButton);
                     frame++)
                {
                    yield return null;
                }

                Assert.That(
                    host.rootVisualElement.focusController.focusedElement,
                    Is.SameAs(details.InstallButton));
                Assert.That(details.HasDeferredFocusRequest, Is.True);

                details.CancelInstallButton.Focus();
                Assert.That(
                    host.rootVisualElement.focusController.focusedElement,
                    Is.SameAs(details.CancelInstallButton));
                yield return null;

                Assert.That(
                    host.rootVisualElement.focusController.focusedElement,
                    Is.SameAs(details.CancelInstallButton));
                Assert.That(details.HasDeferredFocusRequest, Is.False);
            }
            finally
            {
                details.Dispose();
                if (host != null)
                    host.Close();
            }
        }

        [Test]
        public void RecycledSelection_ResetsToMainInsteadOfTheRepositoryDefault()
        {
            var installRequests = new List<string>();
            using PackageManagerGitHubDetails details = CreateDetails(
                out _,
                out _,
                out _,
                (repository, branch) =>
                    installRequests.Add(repository.Name + ":" + branch),
                _ => { });
            details.Refresh(CreateRepository("first", "main"));
            details.ApplyAvailableBranchesForTests(new[] { "main", "release" });
            details.SetInstallState(true, true, "Ready");
            details.BranchField.value = "release";
            details.TriggerInstall();

            Assert.That(details.IsInstallConfirmationPending, Is.True);
            Assert.That(installRequests, Is.Empty);

            PackageManagerGitHubRepository second = CreateRepository(
                "second",
                "trunk");
            details.Refresh(second);
            details.ApplyAvailableBranchesForTests(
                new[] { "trunk", "develop", "main" });
            details.SetInstallState(true, true, "Ready");

            Assert.That(details.IsInstallConfirmationPending, Is.False);
            Assert.That(
                details.InstallMenu.text,
                Is.EqualTo(L10n.Tr(PackageManagerGitHubDetails.InstallText)));
            Assert.That(
                details.InstallMenu.style.display.value,
                Is.EqualTo(DisplayStyle.Flex));
            Assert.That(installRequests, Is.Empty);

            details.TriggerInstall();
            details.TriggerInstall();

            Assert.That(details.CurrentRepository, Is.SameAs(second));
            Assert.That(details.SelectedBranch, Is.EqualTo("main"));
            Assert.That(installRequests, Is.EqualTo(new[] { "second:main" }));
        }

        [Test]
        public void Confirmation_ResetsWhenSameRepositoryChangesInstallInputs()
        {
            using PackageManagerGitHubDetails details = CreateDetails(
                out _,
                out _,
                out _,
                (_, _) => { },
                _ => { });
            details.Refresh(CreateRepository(
                "repository",
                "main",
                "https://github.com/example/repository.git"));
            details.SetInstallState(true, true, "Ready");
            details.TriggerInstall();
            Assert.That(details.IsInstallConfirmationPending, Is.True);

            details.Refresh(CreateRepository(
                "repository",
                "main",
                "https://github.com/example/repository-renamed.git"));

            Assert.That(details.IsInstallConfirmationPending, Is.False);
            Assert.That(
                details.InstallMenu.text,
                Is.EqualTo(L10n.Tr(PackageManagerGitHubDetails.InstallText)));
            Assert.That(
                details.InstallMenu.style.display.value,
                Is.EqualTo(DisplayStyle.Flex));
            Assert.That(
                details.InstallFeedback.style.display.value,
                Is.EqualTo(DisplayStyle.None));
        }

        [UnityTest]
        public IEnumerator Confirmation_ResetsWhenBranchDiscoveryChangesSelection()
        {
            PackageManagerGitHubDetails details = CreateDetails(
                out _,
                out VisualElement extensionItems,
                out _,
                (_, _) => { },
                _ => { });
            var host = ScriptableObject.CreateInstance<NativeActionsHostWindow>();
            try
            {
                VisualElement fixtureRoot = extensionItems.parent?.parent;
                Assert.That(fixtureRoot, Is.Not.Null);
                host.position = new Rect(100f, 100f, 500f, 200f);
                host.Show();
                host.rootVisualElement.Add(fixtureRoot);
                yield return null;

                details.Refresh(CreateRepository("repository", "main"));
                details.ApplyAvailableBranchesForTests(
                    new[] { "main", "release" });
                details.SetInstallState(true, true, "Ready");
                details.BranchField.value = "release";
                Assert.That(details.SelectedBranch, Is.EqualTo("release"));
                details.TriggerInstall();
                Assert.That(details.IsInstallConfirmationPending, Is.True);

                details.ApplyAvailableBranchesForTests(new[] { "main" });

                Assert.That(details.SelectedBranch, Is.EqualTo("main"));
                Assert.That(details.IsInstallConfirmationPending, Is.False);
                Assert.That(
                    details.InstallMenu.text,
                    Is.EqualTo(L10n.Tr(
                        PackageManagerGitHubDetails.InstallText)));
                Assert.That(
                    details.InstallMenu.style.display.value,
                    Is.EqualTo(DisplayStyle.Flex));
            }
            finally
            {
                details.Dispose();
                if (host != null)
                    host.Close();
            }
        }

        [Test]
        public void InstallConfirmation_UsesSanitizedRepositoryDetails()
        {
            PackageManagerGitHubRepository repository = CreateRepository(
                "repository",
                "main",
                "https://user:super-secret@github.com/example/repository.git");

            string message = PackageManagerGitHubDetails
                .BuildTrustConfirmationMessage(repository, "release");

            Assert.That(message, Does.Contain("github.com/example/repository.git"));
            Assert.That(message, Does.Contain("release"));
            Assert.That(message, Does.Contain("Packages/com.example.repository"));
            Assert.That(message, Does.Contain("Confirm Install"));
            Assert.That(message, Does.Not.Contain("super-secret"));
            Assert.That(message, Does.Not.Contain("user:"));
        }

        [Test]
        public void InstallConfirmation_FailsClosedWithoutRepositoryIdentity()
        {
            bool installRequested = false;
            using PackageManagerGitHubDetails details = CreateDetails(
                out _,
                out _,
                out _,
                (_, _) => installRequested = true,
                _ => { });
            details.Refresh(CreateRepository("repository", "main", string.Empty));
            details.SetInstallState(true, true, "Ready");

            details.TriggerInstall();
            details.TriggerInstall();

            Assert.That(details.IsInstallConfirmationPending, Is.False);
            Assert.That(installRequested, Is.False);
            Assert.That(
                details.InstallFeedback.messageType,
                Is.EqualTo(HelpBoxMessageType.Error));
            Assert.That(
                details.InstallFeedback.text,
                Does.Contain("safe confirmation"));
        }

        [Test]
        public void InstallIdentity_BindsRepositoryInputsAndBranchWithoutCredentials()
        {
            PackageManagerGitHubRepository repository = CreateRepository(
                "repository",
                "main",
                "https://user:super-secret@github.com/example/repository.git");
            PackageManagerGitHubRepository changedUrl = CreateRepository(
                "repository",
                "main",
                "https://github.com/example/other-repository.git");
            PackageManagerGitHubRepository changedSshUser = CreateRepository(
                "repository",
                "main",
                "ssh://deploy@github.com/example/repository.git");
            PackageManagerGitHubRepository originalSshUser = CreateRepository(
                "repository",
                "main",
                "ssh://git@github.com/example/repository.git");

            string repositoryIdentity = PackageManagerGitHubDetails
                .GetInstallRepositoryIdentity(repository);
            string mainSelection = PackageManagerGitHubDetails
                .GetInstallSelectionIdentity(repository, "main");
            string releaseSelection = PackageManagerGitHubDetails
                .GetInstallSelectionIdentity(repository, "release");
            string readOnlySelection = PackageManagerGitHubDetails
                .GetInstallSelectionIdentity(
                    repository,
                    "main",
                    PackageManagerGitInstallMode.ReadOnlyPackage);

            Assert.That(mainSelection, Does.StartWith(repositoryIdentity));
            Assert.That(mainSelection, Is.Not.EqualTo(releaseSelection));
            Assert.That(mainSelection, Is.Not.EqualTo(readOnlySelection));
            Assert.That(
                repositoryIdentity,
                Is.Not.EqualTo(PackageManagerGitHubDetails
                    .GetInstallRepositoryIdentity(changedUrl)));
            Assert.That(
                PackageManagerGitHubDetails.GetInstallRepositoryIdentity(
                    originalSshUser),
                Is.Not.EqualTo(
                    PackageManagerGitHubDetails.GetInstallRepositoryIdentity(
                        changedSshUser)));
            Assert.That(repositoryIdentity, Does.Not.Contain("super-secret"));
            Assert.That(repositoryIdentity, Does.Not.Contain("user:"));
        }

        [Test]
        public void RecoveredDependencyCompletion_MatchesOnlyItsNativeDetails()
        {
            PackageManagerGitHubRepository repository = CreateRepository(
                "repository",
                "main",
                "https://github.com/example/repository.git");
            var matching = new PackageDependencyInstallPipelineCompletion(
                true,
                false,
                "Installed.",
                "git@github.com:example/repository.git",
                "main",
                "com.example.repository",
                PackageManagerGitInstallMode.GitSubmodule);
            var wrongPackage = new PackageDependencyInstallPipelineCompletion(
                true,
                false,
                "Installed.",
                repository.Url,
                "main",
                "com.example.other",
                PackageManagerGitInstallMode.GitSubmodule);
            var wrongRepository = new PackageDependencyInstallPipelineCompletion(
                true,
                false,
                "Installed.",
                "https://github.com/example/other.git",
                "main",
                repository.PackageName,
                PackageManagerGitInstallMode.GitSubmodule);

            Assert.That(
                PackageManagerGitHubNativeActions
                    .MatchesDependencyInstallCompletion(repository, matching),
                Is.True);
            Assert.That(
                PackageManagerGitHubNativeActions
                    .MatchesDependencyInstallCompletion(repository, wrongPackage),
                Is.False);
            Assert.That(
                PackageManagerGitHubNativeActions
                    .MatchesDependencyInstallCompletion(repository, wrongRepository),
                Is.False);
            Assert.That(
                PackageManagerGitHubNativeActions
                    .MatchesDependencyInstallCompletion(null, matching),
                Is.False);
        }

        [Test]
        public void DependencyCompletion_SchedulesFallbackOnlyWhenRetainedAndUnshown()
        {
            var live = new PackageDependencyInstallPipelineCompletion(
                true,
                false,
                "Installed.",
                "https://github.com/example/repository.git",
                "main",
                "com.example.repository",
                PackageManagerGitInstallMode.GitSubmodule);
            var recovered = new PackageDependencyInstallPipelineCompletion(
                true,
                false,
                "Installed.",
                "https://github.com/example/repository.git",
                "main",
                "com.example.repository",
                PackageManagerGitInstallMode.GitSubmodule,
                true);

            Assert.That(
                PackageManagerGitHubNativeActions
                    .ShouldScheduleRecoveredCompletion(live, false, false),
                Is.False,
                "A live popup callback that consumed its result must not gain " +
                "a second global dialog.");
            Assert.That(
                PackageManagerGitHubNativeActions
                    .ShouldScheduleRecoveredCompletion(live, false, true),
                Is.True,
                "A closed popup leaves the retained result for immediate " +
                "global presentation even without an assembly reload.");
            Assert.That(
                PackageManagerGitHubNativeActions
                    .ShouldScheduleRecoveredCompletion(recovered, true),
                Is.False,
                "Matching native details already presented the result inline.");
            Assert.That(
                PackageManagerGitHubNativeActions
                    .ShouldScheduleRecoveredCompletion(recovered, false),
                Is.True,
                "A callback lost to reload needs the global recovery path.");
        }

        [Test]
        public void CoordinatedReadOnlyPrimitive_IsNeverPresentedAsStandaloneAfterCoordinatorClears()
        {
            string operationId = System.Guid.NewGuid().ToString("N");
            var leaf = new ReadOnlyGitPackageInstallCompletion(
                false,
                "Dependency failed.",
                "com.example.dependency",
                null,
                operationId);
            var root = new ReadOnlyGitPackageInstallCompletion(
                false,
                "Root failed.",
                "com.example.root",
                null,
                operationId);
            var standalone = new ReadOnlyGitPackageInstallCompletion(
                false,
                "Standalone failed.",
                "com.example.standalone",
                null);

            Assert.That(
                PackageManagerGitHubNativeActions
                    .ShouldPresentReadOnlyCompletionAsStandalone(
                        leaf,
                        false,
                        false),
                Is.False);
            Assert.That(
                PackageManagerGitHubNativeActions
                    .ShouldPresentReadOnlyCompletionAsStandalone(
                        root,
                        false,
                        false),
                Is.False);
            Assert.That(
                PackageManagerGitHubNativeActions
                    .ShouldPresentReadOnlyCompletionAsStandalone(
                        standalone,
                        false,
                        false),
                Is.True);
            Assert.That(
                PackageManagerGitHubNativeActions
                    .ShouldPresentReadOnlyCompletionAsStandalone(
                        standalone,
                        true,
                        false),
                Is.False);
        }

        [Test]
        public void InstallFeedback_ShowsProgressAndRecoverableInlineError()
        {
            using PackageManagerGitHubDetails details = CreateDetails(
                out _,
                out _,
                out _,
                (_, _) => { },
                _ => { });
            details.Refresh(CreateRepository("repository", "main"));
            details.SetInstallState(true, true, "Ready");

            details.ShowInstalling("Installing safely...");

            Assert.That(details.IsInstalling, Is.True);
            Assert.That(details.InstallFeedback.text, Is.EqualTo("Installing safely..."));
            Assert.That(
                details.InstallFeedback.style.display.value,
                Is.EqualTo(DisplayStyle.Flex));
            Assert.That(
                details.InstallFeedback.messageType,
                Is.EqualTo(HelpBoxMessageType.Info));
            Assert.That(details.InstallButton.enabledSelf, Is.False);
            Assert.That(details.BranchField.enabledSelf, Is.False);
            Assert.That(details.InstallMenu.enabledSelf, Is.False);
            Assert.That(
                details.InstallMenu.style.display.value,
                Is.EqualTo(DisplayStyle.None));
            Assert.That(
                details.InstallButton.style.display.value,
                Is.EqualTo(DisplayStyle.Flex));
            Assert.That(
                details.CancelInstallButton.style.display.value,
                Is.EqualTo(DisplayStyle.None));

            details.ShowInstallCompleted(
                "Git submodule installed. Refreshing Package Manager...");

            Assert.That(details.IsInstallCompleted, Is.True);
            Assert.That(
                details.InstallButton.text,
                Is.EqualTo(L10n.Tr(PackageManagerGitHubDetails.InstalledText)));
            Assert.That(details.InstallButton.enabledSelf, Is.False);
            Assert.That(details.BranchField.enabledSelf, Is.False);
            Assert.That(details.InstallMenu.enabledSelf, Is.False);
            Assert.That(
                details.InstallFeedback.messageType,
                Is.EqualTo(HelpBoxMessageType.Info));

            details.ShowInstallError(
                "Clone failed for file:///tmp/repository<size=0>.git");

            Assert.That(details.IsInstalling, Is.False);
            Label feedbackLabel = details.InstallFeedback.Q<Label>(
                className: HelpBox.labelUssClassName);
            Assert.That(feedbackLabel, Is.Not.Null);
            Assert.That(feedbackLabel.enableRichText, Is.False);
            Assert.That(details.InstallFeedback.text, Does.Contain("<size=0>"));
            Assert.That(
                details.InstallMenu.text,
                Is.EqualTo(L10n.Tr(PackageManagerGitHubDetails.RetryInstallText)));
            Assert.That(details.InstallMenu.enabledSelf, Is.True);
            Assert.That(
                details.InstallMenu.style.display.value,
                Is.EqualTo(DisplayStyle.Flex));
            Assert.That(
                details.InstallButton.style.display.value,
                Is.EqualTo(DisplayStyle.None));
            Assert.That(details.BranchField.enabledSelf, Is.True);
            Assert.That(
                details.InstallFeedback.messageType,
                Is.EqualTo(HelpBoxMessageType.Error));
            Assert.That(details.InstallFeedback.text, Does.Contain("Clone failed"));
            Assert.That(details.InstallFeedback.text, Does.Not.Contain("secret"));
            Assert.That(details.InstallFeedback.text, Does.Not.Contain("user:"));
        }

        [Test]
        public void InstallFeedback_ReappearsAfterNativeContainerIsRecycled()
        {
            using PackageManagerGitHubDetails details = CreateDetails(
                out _,
                out _,
                out _,
                (_, _) => { },
                _ => { });
            PackageManagerGitHubRepository repository = CreateRepository(
                "repository",
                "main");
            details.Refresh(repository);
            details.SetInstallState(true, true, "Ready");
            details.TriggerInstall();
            VisualElement feedbackContainer = details.InstallFeedback.parent;
            Assert.That(feedbackContainer, Is.Not.Null);

            feedbackContainer.Clear();
            Assert.That(details.InstallFeedback.parent, Is.Null);
            details.Refresh(repository);

            Assert.That(details.InstallFeedback.parent, Is.SameAs(feedbackContainer));
            Assert.That(details.IsInstallConfirmationPending, Is.True);
            Assert.That(
                details.InstallFeedback.style.display.value,
                Is.EqualTo(DisplayStyle.Flex));
        }

        [Test]
        public void HiddenSelection_RemovesOwnedLinkAndDisablesPrimaryAction()
        {
            using PackageManagerGitHubDetails details = CreateDetails(
                out _,
                out _,
                out VisualElement links,
                (_, _) => { },
                _ => { });
            details.Refresh(CreateRepository("repository", "main"));
            details.SetInstallState(true, true, "Ready");
            Assert.That(links.Contains(details.RepositoryLinkButton), Is.True);
            Assert.That(details.InstallMenu.enabledSelf, Is.True);

            details.Refresh(null);

            Assert.That(details.CurrentRepository, Is.Null);
            Assert.That(details.SelectedBranch, Is.Empty);
            Assert.That(details.RepositoryLinkButton.parent, Is.Null);
            Assert.That(details.InstallMenu.enabledSelf, Is.False);
            Assert.That(details.InstallButton.enabledSelf, Is.False);
            Assert.That(
                details.Controls.style.display.value,
                Is.EqualTo(DisplayStyle.None));
        }

        [Test]
        public void RemovedControls_AreRemountedWithoutDuplication()
        {
            using PackageManagerGitHubDetails details = CreateDetails(
                out VisualElement primaryActions,
                out _,
                out _,
                (_, _) => { },
                _ => { });
            details.Refresh(CreateRepository("repository", "main"));
            details.Controls.RemoveFromHierarchy();

            details.Refresh(CreateRepository("repository", "main"));

            Assert.That(details.Controls.parent, Is.SameAs(primaryActions));
            Assert.That(
                primaryActions.Query<VisualElement>(
                        name: PackageManagerGitHubDetails.ControlsElementName)
                    .ToList()
                    .Count,
                Is.EqualTo(1));
        }

        [Test]
        public void Dispose_RemovesControlsLinksAndPreventsStaleInstallCallbacks()
        {
            int installCount = 0;
            PackageManagerGitHubDetails details = CreateDetails(
                out _,
                out _,
                out _,
                (_, _) => installCount++,
                _ => { });
            details.Refresh(CreateRepository("repository", "main"));
            details.SetInstallState(true, true, "Ready");
            ExecuteInstallMenuAction(
                details,
                PackageManagerGitInstallMode.GitSubmodule);

            Assert.That(details.HasDeferredFocusRequest, Is.True);

            details.Dispose();
            details.Dispose();
            details.TriggerInstall();

            Assert.That(details.IsDisposed, Is.True);
            Assert.That(details.HasDeferredFocusRequest, Is.False);
            Assert.That(details.Controls.parent, Is.Null);
            Assert.That(details.RepositoryLinkButton.parent, Is.Null);
            Assert.That(installCount, Is.Zero);
        }

        [Test]
        public void UnsupportedRoot_InstallAndReleaseRemainIdempotentNoOps()
        {
            int before = PackageManagerGitHubNativeActions.InstalledRootCount;
            var unsupportedRoot = new VisualElement();

            Assert.That(
                PackageManagerGitHubNativeActions.InstallForRoot(unsupportedRoot),
                Is.False);
            Assert.That(
                PackageManagerGitHubNativeActions.InstallForRoot(unsupportedRoot),
                Is.False);
            PackageManagerGitHubNativeActions.ReleaseForRoot(unsupportedRoot);
            PackageManagerGitHubNativeActions.ReleaseForRoot(unsupportedRoot);

            Assert.That(
                PackageManagerGitHubNativeActions.InstalledRootCount,
                Is.EqualTo(before));
        }

        [Test]
        public void RemoveDetails_UsesInlineConfirmationBeforeStartingGit()
        {
            var root = new VisualElement();
            var primaryActions = new VisualElement();
            var detailsHeader = new VisualElement();
            var detailsLinks = new VisualElement
            {
                name = PackageManagerGitHubDetails.NativeDetailsLinksContainerName
            };
            var helpBoxes = new VisualElement
            {
                name = PackageManagerGitHubDetails.NativeHelpBoxContainerName
            };
            root.Add(primaryActions);
            detailsHeader.Add(detailsLinks);
            detailsHeader.Add(helpBoxes);
            root.Add(detailsHeader);

            int removeCount = 0;
            Assert.That(
                PackageManagerSubmoduleRemoveDetails.TryCreate(
                    primaryActions,
                    detailsLinks,
                    _ => removeCount++,
                    out PackageManagerSubmoduleRemoveDetails details),
                Is.True);
            using (details)
            {
                var info = new PackageManagerSubmoduleInfo(
                    "com.example.package",
                    "Packages/com.example.package",
                    "/project/Packages/com.example.package",
                    "https://github.com/example/package.git",
                    true);
                details.Refresh(info);
                details.SetRemoveState(true, "Ready");
                Assert.That(
                    details.RemoveButton.parent.style.display.value,
                    Is.EqualTo(DisplayStyle.None),
                    "Uninstall must start from Unity's Manage menu.");

                // Native PackageAction requests can be broadcast to more than
                // one Package Manager host. Mirroring confirmation must remain
                // idempotent and must never start the Git operation itself.
                details.ShowConfirmation();
                details.ShowConfirmation();

                Assert.That(details.IsConfirmationPending, Is.True);
                Assert.That(removeCount, Is.Zero);
                Assert.That(
                    details.RemoveButton.text,
                    Is.EqualTo(L10n.Tr(
                        PackageManagerSubmoduleRemoveDetails.ConfirmRemoveText)));
                Assert.That(
                    details.Feedback.messageType,
                    Is.EqualTo(HelpBoxMessageType.Warning));
                Assert.That(details.Feedback.text,
                    Does.Contain("state has not changed"));
                Assert.That(
                    details.RemoveButton.parent.style.display.value,
                    Is.EqualTo(DisplayStyle.Flex));

                details.TriggerRemove();

                Assert.That(removeCount, Is.EqualTo(1));
                Assert.That(details.IsRemoving, Is.True);
                Assert.That(details.RemoveButton.enabledSelf, Is.False);
            }
        }

        [Test]
        public void RemoveDetails_DirtyAssessmentRequiresExplicitDiscard()
        {
            var primaryActions = new VisualElement();
            var detailsHeader = new VisualElement();
            var detailsLinks = new VisualElement
            {
                name = PackageManagerGitHubDetails.NativeDetailsLinksContainerName
            };
            detailsHeader.Add(detailsLinks);
            detailsHeader.Add(new VisualElement
            {
                name = PackageManagerGitHubDetails.NativeHelpBoxContainerName
            });
            PackageManagerSubmoduleInfo requestedInfo = null;
            Assert.That(
                PackageManagerSubmoduleRemoveDetails.TryCreate(
                    primaryActions,
                    detailsLinks,
                    info => requestedInfo = info,
                    out PackageManagerSubmoduleRemoveDetails details),
                Is.True);
            using (details)
            {
                var info = new PackageManagerSubmoduleInfo(
                    "com.example.package",
                    "Packages/com.example.package",
                    "/project/Packages/com.example.package",
                    "https://github.com/example/package.git",
                    true);
                var assessment = new SubmoduleRemovalAssessment
                {
                    Path = info.PackagePath,
                    IsInitialized = true,
                    HasWorkingTreeChanges = true,
                    HeadCommit = new string('a', 40),
                    WorktreeStatus = "? local.txt\n"
                };
                details.Refresh(info);
                details.SetRemoveState(true, "Ready");

                Assert.That(details.ShowConfirmation(assessment), Is.True);

                Assert.That(details.ConfirmedAssessment, Is.Not.SameAs(assessment));
                Assert.That(
                    GitUtility.RemovalAssessmentMatches(
                        assessment,
                        details.ConfirmedAssessment),
                    Is.True);
                Assert.That(details.DiscardLocalWork, Is.True);
                Assert.That(
                    details.RemoveButton.text,
                    Is.EqualTo(L10n.Tr(
                        PackageManagerSubmoduleRemoveDetails.ConfirmDiscardText)));
                Assert.That(details.Feedback.text, Does.Contain("would discard"));
                Assert.That(requestedInfo, Is.Null);

                details.TriggerRemove();

                Assert.That(requestedInfo, Is.SameAs(info));
            }
        }

        [Test]
        public void RemoveDetails_DifferentSelectionClearsConfirmedAssessment()
        {
            var primaryActions = new VisualElement();
            var detailsHeader = new VisualElement();
            var detailsLinks = new VisualElement
            {
                name = PackageManagerGitHubDetails.NativeDetailsLinksContainerName
            };
            detailsHeader.Add(detailsLinks);
            detailsHeader.Add(new VisualElement
            {
                name = PackageManagerGitHubDetails.NativeHelpBoxContainerName
            });
            Assert.That(
                PackageManagerSubmoduleRemoveDetails.TryCreate(
                    primaryActions,
                    detailsLinks,
                    _ => { },
                    out PackageManagerSubmoduleRemoveDetails details),
                Is.True);
            using (details)
            {
                var first = new PackageManagerSubmoduleInfo(
                    "com.example.first",
                    "Packages/com.example.first",
                    "/project/Packages/com.example.first",
                    "https://github.com/example/first.git",
                    true);
                var second = new PackageManagerSubmoduleInfo(
                    "com.example.second",
                    "Packages/com.example.second",
                    "/project/Packages/com.example.second",
                    "https://github.com/example/second.git",
                    true);
                var assessment = new SubmoduleRemovalAssessment
                {
                    Path = first.PackagePath,
                    IsInitialized = true,
                    HasWorkingTreeChanges = true,
                    HeadCommit = new string('d', 40),
                    WorktreeStatus = "? local.txt\n"
                };
                details.Refresh(first);
                details.SetRemoveState(true, "Ready");
                Assert.That(details.ShowConfirmation(assessment), Is.True);
                Assert.That(details.ConfirmedAssessment, Is.Not.Null);
                Assert.That(details.DiscardLocalWork, Is.True);

                details.Refresh(second);

                Assert.That(details.CurrentInfo, Is.SameAs(second));
                Assert.That(details.IsConfirmationPending, Is.False);
                Assert.That(details.ConfirmedAssessment, Is.Null);
                Assert.That(details.DiscardLocalWork, Is.False);
            }
        }

        [Test]
        public void RemoveDetails_UnverifiedResidualContentsCannotBeConfirmed()
        {
            var primaryActions = new VisualElement();
            var detailsHeader = new VisualElement();
            var detailsLinks = new VisualElement
            {
                name = PackageManagerGitHubDetails.NativeDetailsLinksContainerName
            };
            detailsHeader.Add(detailsLinks);
            detailsHeader.Add(new VisualElement
            {
                name = PackageManagerGitHubDetails.NativeHelpBoxContainerName
            });
            Assert.That(
                PackageManagerSubmoduleRemoveDetails.TryCreate(
                    primaryActions,
                    detailsLinks,
                    _ => Assert.Fail("Unverified files must never be discarded."),
                    out PackageManagerSubmoduleRemoveDetails details),
                Is.True);
            using (details)
            {
                var info = new PackageManagerSubmoduleInfo(
                    "com.example.package",
                    "Packages/com.example.package",
                    "/project/Packages/com.example.package",
                    "https://github.com/example/package.git",
                    true);
                var assessment = new SubmoduleRemovalAssessment
                {
                    Path = info.PackagePath,
                    HasWorkingTreeChanges = true,
                    HasUnverifiedWorktreeContents = true,
                    WorktreeStatus = "orphaned.txt\n"
                };
                details.Refresh(info);
                details.SetRemoveState(true, "Ready");

                Assert.That(details.ShowConfirmation(assessment), Is.False);

                Assert.That(details.IsConfirmationPending, Is.False);
                Assert.That(details.ConfirmedAssessment, Is.Null);
                Assert.That(details.DiscardLocalWork, Is.False);
                Assert.That(
                    details.Feedback.messageType,
                    Is.EqualTo(HelpBoxMessageType.Error));
                Assert.That(details.Feedback.text, Does.Contain("unverified"));
            }
        }

        [UnityTest]
        public IEnumerator RemoveDetails_CancelAndErrorsRemainInlineAndSanitized()
        {
            var root = new VisualElement();
            var primaryActions = new VisualElement();
            var detailsHeader = new VisualElement();
            var detailsLinks = new VisualElement
            {
                name = PackageManagerGitHubDetails.NativeDetailsLinksContainerName
            };
            var helpBoxes = new VisualElement
            {
                name = PackageManagerGitHubDetails.NativeHelpBoxContainerName
            };
            root.Add(primaryActions);
            detailsHeader.Add(detailsLinks);
            detailsHeader.Add(helpBoxes);
            root.Add(detailsHeader);

            PackageManagerSubmoduleRemoveDetails details = null;
            var host = ScriptableObject.CreateInstance<NativeActionsHostWindow>();
            try
            {
                Assert.That(
                    PackageManagerSubmoduleRemoveDetails.TryCreate(
                        primaryActions,
                        detailsLinks,
                        _ => Assert.Fail("Removal should not start."),
                        out details),
                    Is.True);
                host.position = new Rect(100f, 100f, 500f, 200f);
                host.Show();
                host.rootVisualElement.Add(root);
                yield return null;

                details.Refresh(new PackageManagerSubmoduleInfo(
                    "com.example.package",
                    "Packages/com.example.package",
                    "/project/Packages/com.example.package",
                    "git@github.com:example/package.git",
                    true));
                details.SetRemoveState(true, "Ready");
                details.TriggerRemove();
                SendNavigationSubmit(details.CancelButton);

                Assert.That(details.IsConfirmationPending, Is.False);
                Assert.That(
                    details.RemoveButton.text,
                    Is.EqualTo(L10n.Tr(
                        PackageManagerSubmoduleRemoveDetails.RemoveText)));

                details.ShowError(
                    "Removal blocked for https://user:secret@example.com/repo.git");

                Assert.That(
                    details.Feedback.messageType,
                    Is.EqualTo(HelpBoxMessageType.Error));
                Assert.That(details.Feedback.text, Does.Not.Contain("secret"));
                Assert.That(details.Feedback.text, Does.Not.Contain("user:"));
                Assert.That(
                    details.RemoveButton.text,
                    Is.EqualTo(L10n.Tr(
                        PackageManagerSubmoduleRemoveDetails.RetryRemoveText)));
            }
            finally
            {
                details?.Dispose();
                if (host != null)
                    host.Close();
            }
        }

        [Test]
        public void RemoveService_RequiresExactPackageIdentityAndPath()
        {
            Assert.That(
                GitSubmoduleRemoveService.ValidateInput(
                    new PackageManagerSubmoduleInfo(
                        "com.example.package",
                        "Packages/com.example.package",
                        "/project/Packages/com.example.package",
                        "https://github.com/example/package.git",
                        true)),
                Is.Empty);
            Assert.That(
                GitSubmoduleRemoveService.ValidateInput(
                    new PackageManagerSubmoduleInfo(
                        "com.example.package",
                        "Packages/com.example.different",
                        "/project/Packages/com.example.different",
                        "https://github.com/example/package.git",
                        true)),
                Does.Contain("identity"));
        }

        private static PackageManagerGitHubDetails CreateDetails(
            VisualElement primaryActions,
            VisualElement detailsLinks)
        {
            Assert.That(
                PackageManagerGitHubDetails.TryCreate(
                    primaryActions,
                    detailsLinks,
                    (_, _, _) => { },
                    _ => { },
                    false,
                    out PackageManagerGitHubDetails details),
                Is.True);
            return details;
        }

        private static PackageManagerGitHubDetails CreateDetails(
            out VisualElement primaryActions,
            out VisualElement extensionItems,
            out VisualElement detailsLinks,
            Action<PackageManagerGitHubRepository, string> install,
            Action<string> openUrl)
        {
            return CreateDetails(
                out primaryActions,
                out extensionItems,
                out detailsLinks,
                (repository, branch, _) => install(repository, branch),
                openUrl);
        }

        private static PackageManagerGitHubDetails CreateDetails(
            out VisualElement primaryActions,
            out VisualElement extensionItems,
            out VisualElement detailsLinks,
            Action<PackageManagerGitHubRepository, string,
                PackageManagerGitInstallMode> install,
            Action<string> openUrl)
        {
            var root = new VisualElement();
            var toolbar = new VisualElement();
            var detailsHeader = new VisualElement();
            extensionItems = new VisualElement
            {
                name = PackageManagerGitHubDetails.NativeExtensionActionsContainerName
            };
            primaryActions = new VisualElement();
            detailsLinks = new VisualElement
            {
                name = PackageManagerGitHubDetails.NativeDetailsLinksContainerName
            };
            var helpBoxContainer = new VisualElement
            {
                name = PackageManagerGitHubDetails.NativeHelpBoxContainerName
            };
            toolbar.Add(extensionItems);
            toolbar.Add(primaryActions);
            root.Add(toolbar);
            detailsHeader.Add(detailsLinks);
            detailsHeader.Add(helpBoxContainer);
            root.Add(detailsHeader);

            Assert.That(
                PackageManagerGitHubDetails.TryCreate(
                    primaryActions,
                    detailsLinks,
                    install,
                    openUrl,
                    false,
                    out PackageManagerGitHubDetails details),
                Is.True);
            return details;
        }

        private static void SendNavigationSubmit(VisualElement target)
        {
            NavigationSubmitEvent submit = NavigationSubmitEvent.GetPooled();
            try
            {
                submit.target = target;
                target.SendEvent(submit);
            }
            finally
            {
                submit.Dispose();
            }
        }

        private static void ExecuteInstallMenuAction(
            PackageManagerGitHubDetails details,
            PackageManagerGitInstallMode installMode)
        {
            FindInstallMenuAction(details, installMode).Execute();
        }

        private static DropdownMenuAction FindInstallMenuAction(
            PackageManagerGitHubDetails details,
            PackageManagerGitInstallMode installMode)
        {
            string actionName = PackageManagerGitHubDetails
                .GetInstallMenuActionText(installMode);
            DropdownMenuAction action = null;
            foreach (DropdownMenuItem item in details.InstallMenu.menu.MenuItems())
            {
                if (item is DropdownMenuAction candidate &&
                    string.Equals(
                        candidate.name,
                        actionName,
                        StringComparison.Ordinal))
                {
                    action = candidate;
                    break;
                }
            }

            Assert.That(action, Is.Not.Null);
            return action;
        }

        private static PackageManagerGitHubRepository CreateRepository(
            string name,
            string defaultBranch,
            string url = null)
        {
            return new PackageManagerGitHubRepository(new GitHubRepo
            {
                NodeId = "node-" + name,
                Name = name,
                Owner = "example",
                Url = url ?? $"https://github.com/example/{name}.git",
                DefaultBranch = defaultBranch,
                ManifestState = PackageManifestState.Valid,
                DeclaredPackageName = "com.example." + name.Replace('-', '.'),
                DeclaredDisplayName = name,
                DeclaredVersion = "1.0.0"
            });
        }

        private sealed class PrimaryActionsFieldFixture
        {
            private readonly VisualElement m_BuiltInActionsContainer = new();

            internal VisualElement PrimaryActions => m_BuiltInActionsContainer;
        }

        private sealed class WrongPrimaryActionsFieldFixture
        {
#pragma warning disable CS0414
            private readonly string m_BuiltInActionsContainer = string.Empty;
#pragma warning restore CS0414
        }

        private sealed class UnrelatedPrimaryActionsFieldFixture
        {
            private readonly VisualElement m_BuiltInActionsContainer = new();
        }

        private sealed class SelectionPackageFixture
        {
            internal SelectionPackageFixture(string uniqueId, string name)
            {
                UniqueId = uniqueId;
                Name = name;
            }

            internal string UniqueId { get; }
            internal string Name { get; }
        }

        private sealed class NativeActionsHostWindow : EditorWindow
        {
        }
    }
}
