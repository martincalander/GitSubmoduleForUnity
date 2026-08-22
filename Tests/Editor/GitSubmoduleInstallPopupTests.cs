using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using NUnit.Framework;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace MartinCalander.GitSubmoduleManager.Editor.Tests
{
    public sealed class GitSubmoduleInstallPopupTests
    {
        [Test]
        public void Popup_UsesCompactPackageManagerSizedBounds()
        {
            Vector2 size = GitSubmoduleInstallPopup.DefaultWindowSize;

            Assert.That(size, Is.EqualTo(new Vector2(420f, 132f)));
            Assert.That(
                GitSubmoduleInstallPopup.StatusViewportHeight,
                Is.EqualTo(28f));
        }

        [Test]
        public void PopupSize_IsFixedBeforeUnityFitsItToTheMonitor()
        {
            Assert.That(
                GitSubmoduleInstallPopup.GetWindowSize(null),
                Is.EqualTo(GitSubmoduleInstallPopup.DefaultWindowSize));
            Assert.That(
                GitSubmoduleInstallPopup.GetWindowSize(new VisualElement()),
                Is.EqualTo(GitSubmoduleInstallPopup.DefaultWindowSize));
        }

        [Test]
        public void LiveContract_ResolvesUnitysPackageManagerScreenConversion()
        {
            MethodInfo method = GitSubmoduleInstallPopup
                .FindGuiToScreenRectMethod();

            if (method == null)
            {
                Assert.Pass(
                    "This Unity version uses the guarded GUIUtility fallback.");
                return;
            }

            Assert.That(method.IsStatic, Is.True);
            Assert.That(method.ReturnType, Is.EqualTo(typeof(Rect)));
            ParameterInfo[] parameters = method.GetParameters();
            Assert.That(parameters, Has.Length.EqualTo(2));
            Assert.That(parameters[0].ParameterType, Is.EqualTo(typeof(VisualElement)));
            Assert.That(parameters[1].ParameterType, Is.EqualTo(typeof(Rect)));
        }

        [Test]
        public void LegacyScreenCorrection_MatchesTheSupportedEditorGeneration()
        {
            var screenRect = new Rect(100f, 200f, 29f, 19f);
            var panelRect = new Rect(1f, 24f, 29f, 19f);

            Rect corrected = GitSubmoduleInstallPopup
                .ApplyLegacyScreenRectCorrection(screenRect, panelRect);

#if UNITY_2023_2_OR_NEWER && !UNITY_6000_0_OR_NEWER
            Assert.That(corrected.y, Is.EqualTo(screenRect.y - panelRect.yMax));
#else
            Assert.That(corrected, Is.EqualTo(screenRect));
#endif
        }

        [Test]
        public void DetachedActivator_CannotProduceAScreenAnchor()
        {
            var detached = new VisualElement();

            Assert.That(
                GitSubmoduleInstallPopup.TryGetActivatorScreenRect(
                    detached,
                    out _),
                Is.False);
        }

        [Test]
        public void DetachedActivator_UsesStableFallbackWindowSize()
        {
            Vector2 size = GitSubmoduleInstallPopup.GetWindowSize(
                new VisualElement());

            Assert.That(size, Is.EqualTo(GitSubmoduleInstallPopup.DefaultWindowSize));
        }

        [Test]
        public void MetadataFields_RequireAValidUrlAndCompletedProbe()
        {
            Assert.That(
                GitSubmoduleInstallPopup.AreMetadataFieldsEnabled(false, false),
                Is.False);
            Assert.That(
                GitSubmoduleInstallPopup.AreMetadataFieldsEnabled(false, true),
                Is.False);
            Assert.That(
                GitSubmoduleInstallPopup.AreMetadataFieldsEnabled(true, false),
                Is.False);
            Assert.That(
                GitSubmoduleInstallPopup.AreMetadataFieldsEnabled(true, true),
                Is.True);
        }

        [TestCase("", "Git URL is required.", false)]
        [TestCase("   ", "Git URL is required.", false)]
        [TestCase("https://github.com/example/package.git", "", false)]
        [TestCase("not-a-repository", "Use a valid Git URL.", true)]
        public void InlineValidation_LeavesPristineFormQuiet(
            string repositoryUrl,
            string validationError,
            bool expected)
        {
            Assert.That(
                GitSubmoduleInstallPopup.ShouldShowValidationError(
                    repositoryUrl,
                    validationError),
                Is.EqualTo(expected));
        }

        [Test]
        public void PristinePresentation_DoesNotWeakenSubmitValidation()
        {
            Assert.That(
                GitSubmoduleInstallPopup.GetValidationError(
                    string.Empty,
                    string.Empty,
                    string.Empty),
                Is.EqualTo("Git URL is required."));
        }

        [UnityTest]
        public IEnumerator InvalidRepositoryInput_ShowsValidationWithoutEnablingMetadata()
        {
            var window = ScriptableObject.CreateInstance<GitSubmoduleInstallPopup>();
            try
            {
                ShowAttachedWindow(window);
                yield return null;
                window.CreateGUI();
                yield return null;
                TextField url = window.rootVisualElement.Q<TextField>(
                    "git-submodule-url");
                TextField packageName = window.rootVisualElement.Q<TextField>(
                    "git-submodule-package-name");
                HelpBox status = window.rootVisualElement.Q<HelpBox>(
                    "git-submodule-status");
                Button submit = window.rootVisualElement.Q<Button>(
                    "git-submodule-submit");

                url.value = "not-a-repository";
                window.Repaint();
                yield return null;

                Assert.That(status.style.display.value, Is.EqualTo(DisplayStyle.Flex));
                Assert.That(status.text, Does.Contain("secure HTTPS"));
                Assert.That(packageName.enabledSelf, Is.False);
                Assert.That(submit.enabledSelf, Is.False);
            }
            finally
            {
                if (window != null)
                    window.Close();
            }
        }

        [UnityTest]
        public IEnumerator ValidRepositoryInput_ShowsProbeStateBeforeMetadataUnlocks()
        {
            var window = ScriptableObject.CreateInstance<GitSubmoduleInstallPopup>();
            try
            {
                ShowAttachedWindow(window);
                yield return null;
                window.CreateGUI();
                yield return null;
                TextField url = window.rootVisualElement.Q<TextField>(
                    "git-submodule-url");
                TextField packageName = window.rootVisualElement.Q<TextField>(
                    "git-submodule-package-name");
                ToolbarMenu branchMenu = window.rootVisualElement.Q<ToolbarMenu>(
                    "git-submodule-branch-menu");
                HelpBox status = window.rootVisualElement.Q<HelpBox>(
                    "git-submodule-status");
                Button submit = window.rootVisualElement.Q<Button>(
                    "git-submodule-submit");

                url.value = "https://github.com/example/package.git";
                window.Repaint();
                yield return null;

                Assert.That(status.style.display.value, Is.EqualTo(DisplayStyle.Flex));
                Assert.That(status.text, Is.EqualTo("Inspecting repository with Git..."));
                Assert.That(packageName.enabledSelf, Is.False);
                Assert.That(branchMenu.enabledSelf, Is.False);
                Assert.That(submit.enabledSelf, Is.False);
            }
            finally
            {
                if (window != null)
                    window.Close();
            }
        }

        [UnityTest]
        public IEnumerator ValidSubmission_FirstClickShowsInlineConfirmationWithoutStartingGit()
        {
            var window = ScriptableObject.CreateInstance<GitSubmoduleInstallPopup>();
            try
            {
                ShowAttachedWindow(window);
                yield return null;
                window.CreateGUI();
                yield return null;

                TextField url = window.rootVisualElement.Q<TextField>(
                    "git-submodule-url");
                TextField packageName = window.rootVisualElement.Q<TextField>(
                    "git-submodule-package-name");
                TextField branch = window.rootVisualElement.Q<TextField>(
                    "git-submodule-branch");
                HelpBox status = window.rootVisualElement.Q<HelpBox>(
                    "git-submodule-status");
                Button submit = window.rootVisualElement.Q<Button>(
                    "git-submodule-submit");

                url.SetValueWithoutNotify(
                    "https://github.com/example/package.git");
                packageName.SetValueWithoutNotify("com.example.package");
                branch.SetValueWithoutNotify("main");
                submit.SetEnabled(true);
                Assert.That(GitOperationService.IsBusy, Is.False);

                SendNavigationSubmit(submit);

                Assert.That(GitOperationService.IsBusy, Is.False);
                Assert.That(
                    submit.text,
                    Is.EqualTo(GitSubmoduleInstallPopup.ConfirmSubmitText));
                Assert.That(submit.enabledSelf, Is.True);
                Assert.That(url.enabledSelf, Is.False);
                Assert.That(packageName.enabledSelf, Is.False);
                Assert.That(branch.enabledSelf, Is.False);
                Assert.That(status.messageType, Is.EqualTo(HelpBoxMessageType.Warning));
                Assert.That(status.text, Does.Contain("Only install repositories you trust"));
                Label statusLabel = status.Q<Label>(
                    className: HelpBox.labelUssClassName);
                Assert.That(statusLabel, Is.Not.Null);
                Assert.That(statusLabel.enableRichText, Is.False);
                status.text = GitSubmoduleInstallPopup.BuildTrustConfirmationMessage(
                    "file:///tmp/repository<size=0>.git",
                    "com.example.package",
                    "main");
                Assert.That(status.text, Does.Contain("<size=0>"));
                Assert.That(
                    status.style.display.value,
                    Is.EqualTo(DisplayStyle.Flex));

                url.value = "https://github.com/example/changed.git";

                Assert.That(
                    submit.text,
                    Is.EqualTo(GitSubmoduleInstallPopup.SubmitText));
                Assert.That(GitOperationService.IsBusy, Is.False);
            }
            finally
            {
                if (window != null)
                    window.Close();
            }
        }

        [UnityTest]
        public IEnumerator LongStatus_UsesBoundedScrollingAndKeepsActionsInsideWindow()
        {
            var window = ScriptableObject.CreateInstance<GitSubmoduleInstallPopup>();
            try
            {
                ShowAttachedWindow(window);
                yield return null;
                window.CreateGUI();
                yield return null;
                ScrollView viewport = window.rootVisualElement.Q<ScrollView>(
                    "git-submodule-status-viewport");
                HelpBox status = window.rootVisualElement.Q<HelpBox>(
                    "git-submodule-status");
                VisualElement actions = window.rootVisualElement.Q<VisualElement>(
                    "git-submodule-actions");
                Rect positionBeforeStatus = window.position;

                status.text = BuildLongStatus();
                status.style.display = DisplayStyle.Flex;
                window.UpdateStatusViewport();
                window.Repaint();
                yield return null;
                yield return null;

                Assert.That(viewport.style.display.value, Is.EqualTo(DisplayStyle.Flex));
                Assert.That(
                    viewport.style.visibility.value,
                    Is.EqualTo(Visibility.Visible));
                Assert.That(
                    viewport.resolvedStyle.height,
                    Is.EqualTo(GitSubmoduleInstallPopup.StatusViewportHeight)
                        .Within(0.5f));
                Assert.That(
                    viewport.verticalScrollerVisibility,
                    Is.EqualTo(ScrollerVisibility.Auto));
                Assert.That(
                    viewport.verticalScroller.highValue,
                    Is.GreaterThan(0f));
                Assert.That(viewport.focusable, Is.True);
                Assert.That(viewport.tooltip, Is.EqualTo(status.text));
                Assert.That(
                    window.minSize,
                    Is.EqualTo(GitSubmoduleInstallPopup.DefaultWindowSize));
                Assert.That(
                    window.maxSize,
                    Is.EqualTo(GitSubmoduleInstallPopup.DefaultWindowSize));
                Assert.That(window.position, Is.EqualTo(positionBeforeStatus));
                Assert.That(viewport.Contains(actions), Is.False);
                Assert.That(actions.parent, Is.SameAs(window.rootVisualElement));
                Assert.That(
                    actions.resolvedStyle.display,
                    Is.EqualTo(DisplayStyle.Flex));
                Assert.That(actions.style.flexShrink.value, Is.EqualTo(0f));

                Rect rootBounds = window.rootVisualElement.worldBound;
                Rect actionBounds = actions.worldBound;
                Assert.That(rootBounds.height, Is.GreaterThan(0f));
                Assert.That(actionBounds.height, Is.GreaterThan(0f));
                Assert.That(actionBounds.xMin, Is.GreaterThanOrEqualTo(rootBounds.xMin));
                Assert.That(actionBounds.yMin, Is.GreaterThanOrEqualTo(rootBounds.yMin));
                Assert.That(actionBounds.xMax, Is.LessThanOrEqualTo(rootBounds.xMax));
                Assert.That(actionBounds.yMax, Is.LessThanOrEqualTo(rootBounds.yMax));
            }
            finally
            {
                if (window != null)
                    window.Close();
            }
        }

        [TestCase("", false, "", "com.example.package", "com.example.package")]
        [TestCase("com.example.old", false, "com.example.old", "com.example.next", "com.example.next")]
        [TestCase("com.custom.package", true, "com.example.old", "com.example.next", "com.custom.package")]
        [TestCase("", true, "com.example.old", "com.example.next", "")]
        public void ProbedValues_ReplaceOnlyAutomaticInput(
            string current,
            bool editedByUser,
            string previousAutomatic,
            string nextAutomatic,
            string expected)
        {
            Assert.That(
                GitSubmoduleInstallPopup.ResolveProbedValue(
                    current,
                    editedByUser,
                    previousAutomatic,
                    nextAutomatic),
                Is.EqualTo(expected));
        }

        [Test]
        public void BranchChoices_PreferMainAndSortDistinctValidBranches()
        {
            List<string> choices = GitSubmoduleInstallPopup.BuildBranchChoices(
                "agents/verdaccio",
                new[] { "z-release", "main", "Develop", "develop", "", "bad..branch" });

            Assert.That(
                choices,
                Is.EqualTo(new[]
                {
                    "main",
                    "agents/verdaccio",
                    "Develop",
                    "develop",
                    "z-release"
                }));
        }

        [Test]
        public void PreferredAutomaticBranch_FallsBackWhenMainDoesNotExist()
        {
            Assert.That(
                GitSubmoduleInstallPopup.GetPreferredAutomaticBranch(
                    "trunk",
                    new[] { "release", "trunk" }),
                Is.EqualTo("trunk"));
            Assert.That(
                GitSubmoduleInstallPopup.GetPreferredAutomaticBranch(
                    string.Empty,
                    new[] { "release", "trunk" }),
                Is.EqualTo("release"));
            Assert.That(
                GitSubmoduleInstallPopup.GetPreferredAutomaticBranch(
                    "agents/verdaccio",
                    new[] { "agents/verdaccio", "main" }),
                Is.EqualTo("main"));
        }

        [TestCase("com.example.package", "Packages/com.example.package")]
        [TestCase("not a package", "Packages/<package-name>")]
        [TestCase("", "Packages/<package-name>")]
        public void DestinationPreview_UsesOnlyValidPackageNames(
            string packageName,
            string expected)
        {
            Assert.That(
                GitSubmoduleInstallPopup.BuildDestinationPreview(packageName),
                Is.EqualTo(expected));
        }

        [Test]
        public void TrustConfirmation_RedactsCredentialsAndShowsOperationScope()
        {
            string message = GitSubmoduleInstallPopup.BuildTrustConfirmationMessage(
                "https://secret-token@github.com/example/package.git",
                "com.example.package",
                string.Empty);

            Assert.That(message, Does.Not.Contain("secret-token"));
            Assert.That(message, Does.Contain("https://***@github.com/example/package.git"));
            Assert.That(message, Does.Contain("Branch: the repository default"));
            Assert.That(message, Does.Contain("Destination: Packages/com.example.package"));
            Assert.That(message, Does.Contain("Only install repositories you trust"));
        }

        [Test]
        public void SubmissionIdentity_IsNonReversibleAndDistinguishesSshUsers()
        {
            string first = GitSubmoduleInstallPopup.BuildSubmissionIdentity(
                "ssh://git@github.com/example/package.git",
                "com.example.package",
                "main");
            string second = GitSubmoduleInstallPopup.BuildSubmissionIdentity(
                "ssh://deploy@github.com/example/package.git",
                "com.example.package",
                "main");

            Assert.That(first, Is.Not.Empty);
            Assert.That(first, Is.Not.EqualTo(second));
            Assert.That(first, Does.Not.Contain("github.com"));
            Assert.That(first, Does.Not.Contain("git@"));
        }

        [TestCase("", true, true)]
        [TestCase(null, true, true)]
        [TestCase("Input is invalid.", true, false)]
        [TestCase("", false, false)]
        public void CanSubmit_RequiresValidInputAndAnIdleService(
            string validationError,
            bool canStart,
            bool expected)
        {
            Assert.That(
                GitSubmoduleInstallPopup.CanSubmit(validationError, canStart),
                Is.EqualTo(expected));
        }

        [Test]
        public void CanSubmit_IsDisabledWhileGitProbeIsRunning()
        {
            Assert.That(
                GitSubmoduleInstallPopup.CanSubmit(string.Empty, true, true),
                Is.False);
            Assert.That(
                GitSubmoduleInstallPopup.CanSubmit(string.Empty, true, false),
                Is.True);
        }

        [TestCase("main", "main", false, false)]
        [TestCase("release", "main", false, true)]
        [TestCase("release", "main", true, false)]
        [TestCase("", "main", false, false)]
        public void ManifestBranchWarning_OnlyAppliesToAutomaticStaleMetadata(
            string selectedBranch,
            string inspectedBranch,
            bool packageNameEditedByUser,
            bool expected)
        {
            Assert.That(
                GitSubmoduleInstallPopup.ShouldWarnAboutManifestBranch(
                    selectedBranch,
                    inspectedBranch,
                    packageNameEditedByUser),
                Is.EqualTo(expected));
        }

        [Test]
        public void InitialUi_DisablesMetadataAndOmitsRedundantExplanations()
        {
            var window = ScriptableObject.CreateInstance<GitSubmoduleInstallPopup>();
            try
            {
                window.CreateGUI();
                TextField packageName = window.rootVisualElement.Q<TextField>(
                    "git-submodule-package-name");
                TextField branch = window.rootVisualElement.Q<TextField>(
                    "git-submodule-branch");
                ToolbarMenu branchMenu = window.rootVisualElement.Q<ToolbarMenu>(
                    "git-submodule-branch-menu");
                ScrollView statusViewport = window.rootVisualElement.Q<ScrollView>(
                    "git-submodule-status-viewport");
                HelpBox status = window.rootVisualElement.Q<HelpBox>(
                    "git-submodule-status");
                Button cancel = window.rootVisualElement.Q<Button>(
                    "git-submodule-cancel");
                Button submit = window.rootVisualElement.Q<Button>(
                    "git-submodule-submit");

                Assert.That(packageName, Is.Not.Null);
                Assert.That(branch, Is.Not.Null);
                Assert.That(branchMenu, Is.Not.Null);
                Assert.That(statusViewport, Is.Not.Null);
                Assert.That(status, Is.Not.Null);
                Assert.That(cancel, Is.Not.Null);
                Assert.That(submit, Is.Not.Null);
                Assert.That(packageName.enabledSelf, Is.False);
                Assert.That(branch.enabledSelf, Is.False);
                Assert.That(branchMenu.enabledSelf, Is.False);
                Assert.That(submit.enabledSelf, Is.False);
                Assert.That(status.text, Is.Empty);
                Assert.That(
                    status.style.display.value,
                    Is.EqualTo(DisplayStyle.None));
                Assert.That(
                    statusViewport.style.display.value,
                    Is.EqualTo(DisplayStyle.Flex));
                Assert.That(
                    statusViewport.style.visibility.value,
                    Is.EqualTo(Visibility.Hidden));
                Assert.That(
                    statusViewport.style.height.value.value,
                    Is.EqualTo(GitSubmoduleInstallPopup.StatusViewportHeight));

                VisualElement root = window.rootVisualElement;
                Assert.That(root.childCount, Is.EqualTo(5));
                Assert.That(
                    root.ElementAt(0).name,
                    Is.EqualTo("git-submodule-url"));
                Assert.That(
                    root.ElementAt(3).name,
                    Is.EqualTo("git-submodule-status-viewport"));
                Assert.That(
                    root.ElementAt(4).name,
                    Is.EqualTo("git-submodule-actions"));
                for (int index = 0; index < root.childCount; index++)
                {
                    Assert.That(
                        root.ElementAt(index).style.flexGrow.value,
                        Is.LessThanOrEqualTo(0f),
                        $"Root child {index} must not create expandable lower space.");
                }

                TextField url = root.Q<TextField>("git-submodule-url");
                Assert.That(
                    root.style.paddingLeft.value.value,
                    Is.LessThanOrEqualTo(8f));
                Assert.That(
                    root.style.paddingBottom.value.value,
                    Is.LessThanOrEqualTo(6f));
                Assert.That(
                    url.labelElement.style.minWidth.value.value,
                    Is.LessThanOrEqualTo(90f));
                Assert.That(
                    cancel.style.minWidth.value.value,
                    Is.LessThanOrEqualTo(72f));
                Assert.That(
                    submit.style.minWidth.value.value,
                    Is.LessThanOrEqualTo(96f));

                bool hasDestinationLabel = false;
                bool hasPermanentExplanation = false;
                bool hasRedundantHeader = false;
                window.rootVisualElement.Query<Label>().ForEach(label =>
                {
                    hasDestinationLabel |= label.text == "Destination";
                    hasPermanentExplanation |=
                        label.text?.Contains("operation is rolled back") == true;
                    hasRedundantHeader |= label.text ==
                                          GitSubmoduleInstallPopup.WindowTitle;
                });

                Assert.That(hasDestinationLabel, Is.False);
                Assert.That(hasPermanentExplanation, Is.False);
                Assert.That(hasRedundantHeader, Is.False);
            }
            finally
            {
                Object.DestroyImmediate(window);
            }
        }

        private static void ShowAttachedWindow(GitSubmoduleInstallPopup window)
        {
            Vector2 size = GitSubmoduleInstallPopup.DefaultWindowSize;
            window.minSize = size;
            window.maxSize = size;
            window.position = new Rect(100f, 100f, size.x, size.y);
            window.Show();
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

        private static string BuildLongStatus()
        {
            var builder = new StringBuilder();
            for (int index = 0; index < 40; index++)
            {
                builder.Append("Diagnostic line ")
                    .Append(index + 1)
                    .AppendLine(": repository inspection detail.");
            }

            return builder.ToString();
        }
    }
}
