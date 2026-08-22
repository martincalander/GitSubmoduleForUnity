using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace MartinCalander.GitSubmoduleManager.Editor.Tests
{
    public sealed class GitSubmoduleInstallPopupTests
    {
        [Test]
        public void Popup_HasRoomForFieldsWarningsAndActions()
        {
            Vector2 size = GitSubmoduleInstallPopup.DefaultWindowSize;

            Assert.That(size.x, Is.GreaterThanOrEqualTo(440f));
            Assert.That(size.y, Is.GreaterThanOrEqualTo(260f));
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
        public void BranchChoices_PutDefaultFirstAndSortDistinctValidBranches()
        {
            List<string> choices = GitSubmoduleInstallPopup.BuildBranchChoices(
                "main",
                new[] { "z-release", "main", "Develop", "develop", "", "bad..branch" });

            Assert.That(
                choices,
                Is.EqualTo(new[] { "main", "Develop", "develop", "z-release" }));
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

                Assert.That(packageName, Is.Not.Null);
                Assert.That(branch, Is.Not.Null);
                Assert.That(branchMenu, Is.Not.Null);
                Assert.That(packageName.enabledSelf, Is.False);
                Assert.That(branch.enabledSelf, Is.False);
                Assert.That(branchMenu.enabledSelf, Is.False);

                bool hasDestinationLabel = false;
                bool hasPermanentExplanation = false;
                window.rootVisualElement.Query<Label>().ForEach(label =>
                {
                    hasDestinationLabel |= label.text == "Destination";
                    hasPermanentExplanation |=
                        label.text?.Contains("operation is rolled back") == true;
                });

                Assert.That(hasDestinationLabel, Is.False);
                Assert.That(hasPermanentExplanation, Is.False);
            }
            finally
            {
                Object.DestroyImmediate(window);
            }
        }
    }
}
