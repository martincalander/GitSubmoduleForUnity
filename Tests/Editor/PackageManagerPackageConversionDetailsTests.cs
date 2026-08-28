using NUnit.Framework;
using UnityEditor;
using UnityEngine.UIElements;

namespace MartinCalander.GitSubmoduleManager.Editor.Tests
{
    [Parallelizable(ParallelScope.None)]
    public sealed class PackageManagerPackageConversionDetailsTests
    {
        [Test]
        public void Create_RequiresNativeDetailsLinksAndMountsControls()
        {
            var primaryActions = new VisualElement();
            var wrongLinks = new VisualElement { name = "details" };

            Assert.That(
                PackageManagerPackageConversionDetails.TryCreate(
                    primaryActions,
                    wrongLinks,
                    _ => { },
                    out _),
                Is.False);

            VisualElement detailsLinks = CreateDetailsLinks(out VisualElement helpBoxes);
            Assert.That(
                PackageManagerPackageConversionDetails.TryCreate(
                    primaryActions,
                    detailsLinks,
                    _ => { },
                    out PackageManagerPackageConversionDetails details),
                Is.True);
            using (details)
            {
                Assert.That(details.Controls.parent, Is.SameAs(primaryActions));
                Assert.That(details.Feedback.parent, Is.SameAs(helpBoxes));
                Assert.That(
                    details.Controls.style.display.value,
                    Is.EqualTo(DisplayStyle.None));
            }
        }

        [Test]
        public void SubmoduleToReadOnly_ConfirmsThenInvokesBoundTarget()
        {
            VisualElement detailsLinks = CreateDetailsLinks(out _);
            var primaryActions = new VisualElement();
            PackageManagerPackageConversionTarget requested = null;
            int requestCount = 0;
            Assert.That(
                PackageManagerPackageConversionDetails.TryCreate(
                    primaryActions,
                    detailsLinks,
                    target =>
                    {
                        requested = target;
                        requestCount++;
                    },
                    out PackageManagerPackageConversionDetails details),
                Is.True);
            using (details)
            {
                PackageManagerPackageConversionTarget target = CreateTarget(
                    GitPackageConversionDirection.SubmoduleToReadOnly,
                    "com.example.tool");
                details.Refresh(target);
                details.SetActionState(true, "Ready");

                Assert.That(
                    details.Controls.style.display.value,
                    Is.EqualTo(DisplayStyle.None),
                    "Submodule conversion starts from Unity's Manage menu, not a standalone button.");
                Assert.That(
                    details.ConvertButton.text,
                    Is.EqualTo(L10n.Tr(
                        PackageManagerPackageConversionDetails.ConvertToReadOnlyText)));

                details.TriggerConversion();

                Assert.That(details.IsConfirmationPending, Is.True);
                Assert.That(requested, Is.Null);
                Assert.That(
                    details.ConvertButton.text,
                    Is.EqualTo(L10n.Tr(
                        PackageManagerPackageConversionDetails.ConfirmConversionText)));
                Assert.That(details.Feedback.messageType,
                    Is.EqualTo(HelpBoxMessageType.Warning));
                Assert.That(details.Feedback.text, Does.Contain("verified submodule"));
                Assert.That(
                    details.Controls.style.display.value,
                    Is.EqualTo(DisplayStyle.None),
                    "Manage remains the only primary conversion surface during confirmation.");

                details.TriggerConversion();

                Assert.That(requested, Is.SameAs(target));
                Assert.That(details.IsConverting, Is.True);
                Assert.That(details.ConvertButton.enabledSelf, Is.False);
                Assert.That(details.Feedback.text, Does.Contain("read-only"));

                details.TriggerConversion();
                Assert.That(requestCount, Is.EqualTo(1));
            }
        }

        [Test]
        public void ReadOnlyToSubmodule_UsesManageOnlySurfaceAndEditableDestinationLanguage()
        {
            PackageManagerPackageConversionDetails details = CreateDetails(
                out _);
            using (details)
            {
                PackageManagerPackageConversionTarget target = CreateTarget(
                    GitPackageConversionDirection.ReadOnlyToSubmodule,
                    "com.example.tool");
                details.Refresh(target);
                details.SetActionState(true, "Ready");

                Assert.That(
                    details.Controls.style.display.value,
                    Is.EqualTo(DisplayStyle.None),
                    "Read-only conversion is exposed only through Unity's Manage menu.");
                Assert.That(
                    details.ConvertButton.text,
                    Is.EqualTo(L10n.Tr(
                        PackageManagerPackageConversionDetails.ConvertToSubmoduleText)));

                details.ShowConfirmation();

                Assert.That(details.Feedback.text,
                    Does.Contain("editable Git submodule"));
                Assert.That(details.Feedback.text,
                    Does.Contain("Packages/com.example.tool"));
                Assert.That(details.Feedback.text,
                    Does.Contain("only removed after"));
            }
        }

        [Test]
        public void DirtySubmodule_RequiresAssessmentBoundDiscardConfirmation()
        {
            PackageManagerPackageConversionDetails details = CreateDetails(
                out _);
            using (details)
            {
                PackageManagerPackageConversionTarget target = CreateTarget(
                    GitPackageConversionDirection.SubmoduleToReadOnly,
                    "com.example.tool");
                var assessment = new SubmoduleRemovalAssessment
                {
                    Path = target.PackagePath,
                    IsInitialized = true,
                    HasWorkingTreeChanges = true,
                    HeadCommit = new string('a', 40),
                    WorktreeStatus = "? uncommitted.txt\n"
                };
                details.Refresh(target);
                details.SetActionState(true, "Ready");

                Assert.That(details.ShowConfirmation(assessment), Is.True);

                Assert.That(details.ConfirmedAssessment, Is.Not.SameAs(assessment));
                Assert.That(
                    GitUtility.RemovalAssessmentMatches(
                        assessment,
                        details.ConfirmedAssessment),
                    Is.True);
                Assert.That(details.DiscardLocalWork, Is.True);
                Assert.That(
                    details.ConvertButton.text,
                    Is.EqualTo(L10n.Tr(
                        PackageManagerPackageConversionDetails.ConfirmDiscardText)));
                Assert.That(details.Feedback.text, Does.Contain("would discard"));
                Assert.That(details.Feedback.text, Does.Contain("not included"));
            }
        }

        [Test]
        public void LocallyUnadvertisedCommit_RequiresConfirmationBeforeRemoteProof()
        {
            PackageManagerPackageConversionDetails details = CreateDetails(
                out _);
            using (details)
            {
                PackageManagerPackageConversionTarget target = CreateTarget(
                    GitPackageConversionDirection.SubmoduleToReadOnly,
                    "com.example.tool");
                var assessment = new SubmoduleRemovalAssessment
                {
                    Path = target.PackagePath,
                    IsInitialized = true,
                    HasLocalOnlyCommits = true,
                    LocalOnlyCommitCount = 1,
                    HeadCommit = new string('b', 40)
                };
                details.Refresh(target);
                details.SetActionState(true, "Ready");

                Assert.That(details.ShowConfirmation(assessment), Is.True);

                Assert.That(details.IsConfirmationPending, Is.True);
                Assert.That(details.ConfirmedAssessment, Is.Not.Null);
                Assert.That(details.DiscardLocalWork, Is.True);
                Assert.That(details.Feedback.messageType,
                    Is.EqualTo(HelpBoxMessageType.Warning));
                Assert.That(details.Feedback.text,
                    Does.Contain("not represented by local remote-tracking refs"));
            }
        }

        [Test]
        public void Refresh_DifferentSelectionCancelsConfirmationAndStaleUpdates()
        {
            PackageManagerPackageConversionDetails details = CreateDetails(
                out _);
            using (details)
            {
                PackageManagerPackageConversionTarget first = CreateTarget(
                    GitPackageConversionDirection.SubmoduleToReadOnly,
                    "com.example.first");
                PackageManagerPackageConversionTarget second = CreateTarget(
                    GitPackageConversionDirection.ReadOnlyToSubmodule,
                    "com.example.second");
                var assessment = new SubmoduleRemovalAssessment
                {
                    Path = first.PackagePath,
                    IsInitialized = true,
                    HasWorkingTreeChanges = true,
                    HeadCommit = new string('c', 40),
                    WorktreeStatus = "? local.txt\n"
                };
                details.Refresh(first);
                details.SetActionState(true, "Ready");
                Assert.That(details.ShowConfirmation(assessment), Is.True);
                Assert.That(details.ConfirmedAssessment, Is.Not.Null);

                details.Refresh(second);

                Assert.That(details.IsConfirmationPending, Is.False);
                Assert.That(details.ConfirmedAssessment, Is.Null);
                Assert.That(details.DiscardLocalWork, Is.False);
                Assert.That(details.CurrentTarget, Is.SameAs(second));
                Assert.That(details.ConvertButton.enabledSelf, Is.False);
                Assert.That(
                    details.ConvertButton.text,
                    Is.EqualTo(L10n.Tr(
                        PackageManagerPackageConversionDetails.ConvertToSubmoduleText)));
                Assert.That(details.ShowProgress(first, "Stale progress"), Is.False);
                Assert.That(details.ShowError(first, "Stale error"), Is.False);
                Assert.That(details.ShowCompleted(first, "Stale completion"), Is.False);
                Assert.That(details.IsConverting, Is.False);
                Assert.That(details.IsCompleted, Is.False);
                Assert.That(details.Feedback.text, Is.Empty);
            }
        }

        [Test]
        public void States_DisableProgressSanitizeErrorsAndCompleteInline()
        {
            PackageManagerPackageConversionDetails details = CreateDetails(
                out _);
            using (details)
            {
                PackageManagerPackageConversionTarget target = CreateTarget(
                    GitPackageConversionDirection.ReadOnlyToSubmodule,
                    "com.example.tool");
                details.Refresh(target);
                details.SetActionState(false, "Another Git operation is running.");

                Assert.That(details.ConvertButton.enabledSelf, Is.False);
                Assert.That(details.ConvertButton.tooltip,
                    Is.EqualTo("Another Git operation is running."));
                details.ShowConfirmation();
                Assert.That(details.IsConfirmationPending, Is.False);

                details.SetActionState(target, true, "Ready");
                details.ShowConfirmation();
                details.CancelConfirmation();
                Assert.That(details.IsConfirmationPending, Is.False);

                Assert.That(details.ShowProgress(target, "Preparing conversion..."),
                    Is.True);
                Assert.That(details.IsConverting, Is.True);
                Assert.That(details.ConvertButton.enabledSelf, Is.False);

                Assert.That(
                    details.ShowError(
                        target,
                        "Failed for https://user:secret@example.com/repo.git"),
                    Is.True);
                Assert.That(details.Feedback.messageType,
                    Is.EqualTo(HelpBoxMessageType.Error));
                Assert.That(details.Feedback.text, Does.Not.Contain("secret"));
                Assert.That(details.Feedback.text, Does.Not.Contain("user:"));
                Assert.That(
                    details.ConvertButton.text,
                    Is.EqualTo(L10n.Tr(
                        PackageManagerPackageConversionDetails.RetryConversionText)));

                Assert.That(details.ShowCompleted(target, string.Empty), Is.True);
                Assert.That(details.IsCompleted, Is.True);
                Assert.That(details.ConvertButton.enabledSelf, Is.False);
                Assert.That(details.Feedback.text, Does.Contain("refreshing packages"));
            }
        }

        [Test]
        public void RefreshNull_HidesAndClearsSelectionState()
        {
            PackageManagerPackageConversionDetails details = CreateDetails(
                out _);
            using (details)
            {
                PackageManagerPackageConversionTarget target = CreateTarget(
                    GitPackageConversionDirection.SubmoduleToReadOnly,
                    "com.example.tool");
                details.Refresh(target);
                details.SetActionState(true, "Ready");
                details.ShowConfirmation();

                details.Refresh(null);

                Assert.That(details.CurrentTarget, Is.Null);
                Assert.That(details.IsConfirmationPending, Is.False);
                Assert.That(details.ConfirmedAssessment, Is.Null);
                Assert.That(details.DiscardLocalWork, Is.False);
                Assert.That(
                    details.Controls.style.display.value,
                    Is.EqualTo(DisplayStyle.None));
                Assert.That(details.Feedback.text, Is.Empty);
            }
        }

        private static PackageManagerPackageConversionDetails CreateDetails(
            out PackageManagerPackageConversionTarget requested)
        {
            PackageManagerPackageConversionTarget callbackTarget = null;
            VisualElement detailsLinks = CreateDetailsLinks(out _);
            Assert.That(
                PackageManagerPackageConversionDetails.TryCreate(
                    new VisualElement(),
                    detailsLinks,
                    target => callbackTarget = target,
                    out PackageManagerPackageConversionDetails details),
                Is.True);
            requested = callbackTarget;
            return details;
        }

        private static VisualElement CreateDetailsLinks(
            out VisualElement helpBoxes)
        {
            var detailsHeader = new VisualElement();
            var detailsLinks = new VisualElement
            {
                name = PackageManagerGitHubDetails.NativeDetailsLinksContainerName
            };
            helpBoxes = new VisualElement
            {
                name = PackageManagerGitHubDetails.NativeHelpBoxContainerName
            };
            detailsHeader.Add(detailsLinks);
            detailsHeader.Add(helpBoxes);
            return detailsLinks;
        }

        private static PackageManagerPackageConversionTarget CreateTarget(
            GitPackageConversionDirection direction,
            string packageName)
        {
            return new PackageManagerPackageConversionTarget(
                direction,
                packageName,
                "Packages/" + packageName,
                "https://github.com/example/tool.git",
                "main");
        }
    }
}
