using NUnit.Framework;
using UnityEngine.UIElements;

namespace MartinCalander.GitSubmoduleManager.Editor.Tests
{
    [Parallelizable(ParallelScope.None)]
    public sealed class PackageManagerAssessedActionDetailsTests
    {
        [Test]
        public void TriggerAssessedRemoval_RequiresInspectingExactPathAndRiskAuthorization()
        {
            var primaryActions = new VisualElement();
            VisualElement detailsLinks = CreateDetailsLinks();
            PackageManagerSubmoduleRemoveDetails details = null;
            PackageManagerSubmoduleInfo requestedInfo = null;
            SubmoduleRemovalAssessment callbackAssessment = null;
            bool callbackDiscard = false;
            int requestCount = 0;
            Assert.That(
                PackageManagerSubmoduleRemoveDetails.TryCreate(
                    primaryActions,
                    detailsLinks,
                    info =>
                    {
                        requestCount++;
                        requestedInfo = info;
                        callbackAssessment = details.ConfirmedAssessment;
                        callbackDiscard = details.DiscardLocalWork;
                    },
                    out details),
                Is.True);

            using (details)
            {
                PackageManagerSubmoduleInfo info = CreateInfo(
                    "com.example.tool");
                SubmoduleRemovalAssessment dirty = CreateDirtyAssessment(
                    info.PackagePath);
                details.Refresh(info);
                details.SetRemoveState(true, "Ready");

                Assert.That(
                    details.TriggerAssessedRemoval(dirty, true),
                    Is.False,
                    "Only the active inspection may bind an assessment.");
                details.ShowInspecting("Inspecting...");
                Assert.That(details.IsInspecting, Is.True);

                SubmoduleRemovalAssessment wrongPath = CreateDirtyAssessment(
                    "Packages/com.example.other");
                Assert.That(
                    details.TriggerAssessedRemoval(wrongPath, true),
                    Is.False);
                Assert.That(
                    details.TriggerAssessedRemoval(dirty, false),
                    Is.False,
                    "Dirty work cannot be bound without discard authorization.");
                Assert.That(
                    details.TriggerAssessedRemoval(
                        CreateCleanAssessment(info.PackagePath),
                        true),
                    Is.False,
                    "A clean assessment cannot inherit discard authorization.");
                Assert.That(
                    details.TriggerAssessedRemoval(
                        CreateUnverifiedAssessment(info.PackagePath),
                        true),
                    Is.False);
                Assert.That(requestCount, Is.Zero);
                Assert.That(details.ConfirmedAssessment, Is.Null);
                Assert.That(details.DiscardLocalWork, Is.False);
                Assert.That(details.IsInspecting, Is.True);

                Assert.That(
                    details.TriggerAssessedRemoval(dirty, true),
                    Is.True);

                Assert.That(requestCount, Is.EqualTo(1));
                Assert.That(requestedInfo, Is.SameAs(info));
                Assert.That(callbackAssessment, Is.Not.SameAs(dirty));
                Assert.That(
                    GitUtility.RemovalAssessmentMatches(
                        dirty,
                        callbackAssessment),
                    Is.True);
                Assert.That(callbackDiscard, Is.True);
                Assert.That(details.IsRemoving, Is.True);
                Assert.That(details.IsInspecting, Is.False);
            }
        }

        [Test]
        public void TriggerAssessedRemoval_CancelResetsAndCleanRetryDoesNotDiscard()
        {
            VisualElement detailsLinks = CreateDetailsLinks();
            int requestCount = 0;
            Assert.That(
                PackageManagerSubmoduleRemoveDetails.TryCreate(
                    new VisualElement(),
                    detailsLinks,
                    _ => requestCount++,
                    out PackageManagerSubmoduleRemoveDetails details),
                Is.True);

            using (details)
            {
                PackageManagerSubmoduleInfo info = CreateInfo(
                    "com.example.clean");
                SubmoduleRemovalAssessment clean = CreateCleanAssessment(
                    info.PackagePath);
                details.Refresh(info);
                details.SetRemoveState(true, "Ready");
                details.ShowInspecting("Inspecting clean package...");

                details.CancelInspection();

                Assert.That(details.IsInspecting, Is.False);
                Assert.That(details.Feedback.text, Is.Empty);
                Assert.That(details.ConfirmedAssessment, Is.Null);
                Assert.That(details.DiscardLocalWork, Is.False);
                Assert.That(
                    details.TriggerAssessedRemoval(clean, false),
                    Is.False,
                    "A cancelled assessment cannot be started later.");

                details.ShowInspecting("Inspecting clean package again...");
                Assert.That(
                    details.TriggerAssessedRemoval(clean, false),
                    Is.True);
                Assert.That(requestCount, Is.EqualTo(1));
                Assert.That(details.DiscardLocalWork, Is.False);
            }
        }

        [Test]
        public void TriggerAssessedConversion_RequiresExactTargetPathAndRiskAuthorization()
        {
            VisualElement detailsLinks = CreateDetailsLinks();
            PackageManagerPackageConversionDetails details = null;
            PackageManagerPackageConversionTarget requestedTarget = null;
            SubmoduleRemovalAssessment callbackAssessment = null;
            bool callbackDiscard = false;
            int requestCount = 0;
            Assert.That(
                PackageManagerPackageConversionDetails.TryCreate(
                    new VisualElement(),
                    detailsLinks,
                    target =>
                    {
                        requestCount++;
                        requestedTarget = target;
                        callbackAssessment = details.ConfirmedAssessment;
                        callbackDiscard = details.DiscardLocalWork;
                    },
                    out details),
                Is.True);

            using (details)
            {
                PackageManagerPackageConversionTarget target = CreateTarget(
                    GitPackageConversionDirection.SubmoduleToReadOnly,
                    "com.example.tool");
                SubmoduleRemovalAssessment dirty = CreateDirtyAssessment(
                    target.PackagePath);
                details.Refresh(target);
                details.SetActionState(true, "Ready");

                Assert.That(
                    details.TriggerAssessedConversion(target, dirty, true),
                    Is.False,
                    "Only the active inspection may bind an assessment.");
                Assert.That(details.ShowInspecting(target, "Inspecting..."), Is.True);
                Assert.That(details.IsInspecting, Is.True);

                Assert.That(
                    details.TriggerAssessedConversion(
                        CreateTarget(
                            GitPackageConversionDirection.SubmoduleToReadOnly,
                            "com.example.other"),
                        dirty,
                        true),
                    Is.False);
                Assert.That(
                    details.TriggerAssessedConversion(
                        target,
                        CreateDirtyAssessment("Packages/com.example.other"),
                        true),
                    Is.False);
                Assert.That(
                    details.TriggerAssessedConversion(target, dirty, false),
                    Is.False,
                    "Dirty work cannot be bound without discard authorization.");
                Assert.That(
                    details.TriggerAssessedConversion(
                        target,
                        CreateCleanAssessment(target.PackagePath),
                        true),
                    Is.False,
                    "A clean assessment cannot inherit discard authorization.");
                Assert.That(
                    details.TriggerAssessedConversion(
                        target,
                        CreateUnverifiedAssessment(target.PackagePath),
                        true),
                    Is.False);
                Assert.That(requestCount, Is.Zero);
                Assert.That(details.ConfirmedAssessment, Is.Null);
                Assert.That(details.DiscardLocalWork, Is.False);
                Assert.That(details.IsInspecting, Is.True);

                Assert.That(
                    details.TriggerAssessedConversion(target, dirty, true),
                    Is.True);

                Assert.That(requestCount, Is.EqualTo(1));
                Assert.That(requestedTarget, Is.SameAs(target));
                Assert.That(callbackAssessment, Is.Not.SameAs(dirty));
                Assert.That(
                    GitUtility.RemovalAssessmentMatches(
                        dirty,
                        callbackAssessment),
                    Is.True);
                Assert.That(callbackDiscard, Is.True);
                Assert.That(details.IsConverting, Is.True);
                Assert.That(details.IsInspecting, Is.False);
            }
        }

        [Test]
        public void TriggerAssessedConversion_CancelIsTargetBoundAndCleanRetryDoesNotDiscard()
        {
            VisualElement detailsLinks = CreateDetailsLinks();
            int requestCount = 0;
            Assert.That(
                PackageManagerPackageConversionDetails.TryCreate(
                    new VisualElement(),
                    detailsLinks,
                    _ => requestCount++,
                    out PackageManagerPackageConversionDetails details),
                Is.True);

            using (details)
            {
                PackageManagerPackageConversionTarget target = CreateTarget(
                    GitPackageConversionDirection.SubmoduleToReadOnly,
                    "com.example.clean");
                PackageManagerPackageConversionTarget other = CreateTarget(
                    GitPackageConversionDirection.SubmoduleToReadOnly,
                    "com.example.other");
                SubmoduleRemovalAssessment clean = CreateCleanAssessment(
                    target.PackagePath);
                details.Refresh(target);
                details.SetActionState(true, "Ready");
                Assert.That(
                    details.ShowInspecting(target, "Inspecting clean package..."),
                    Is.True);

                details.CancelInspection(other);
                Assert.That(details.IsInspecting, Is.True);
                details.CancelInspection(target);

                Assert.That(details.IsInspecting, Is.False);
                Assert.That(details.Feedback.text, Is.Empty);
                Assert.That(details.ConfirmedAssessment, Is.Null);
                Assert.That(details.DiscardLocalWork, Is.False);
                Assert.That(
                    details.TriggerAssessedConversion(target, clean, false),
                    Is.False,
                    "A cancelled assessment cannot be started later.");

                Assert.That(
                    details.ShowInspecting(
                        target,
                        "Inspecting clean package again..."),
                    Is.True);
                Assert.That(
                    details.TriggerAssessedConversion(target, clean, false),
                    Is.True);
                Assert.That(requestCount, Is.EqualTo(1));
                Assert.That(details.DiscardLocalWork, Is.False);
            }
        }

        private static VisualElement CreateDetailsLinks()
        {
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
            return detailsLinks;
        }

        private static PackageManagerSubmoduleInfo CreateInfo(string packageName)
        {
            return new PackageManagerSubmoduleInfo(
                packageName,
                "Packages/" + packageName,
                "/project/Packages/" + packageName,
                "https://github.com/example/tool.git",
                true);
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

        private static SubmoduleRemovalAssessment CreateCleanAssessment(
            string path)
        {
            return new SubmoduleRemovalAssessment
            {
                Path = path,
                IsInitialized = true,
                HeadCommit = new string('a', 40)
            };
        }

        private static SubmoduleRemovalAssessment CreateDirtyAssessment(
            string path)
        {
            return new SubmoduleRemovalAssessment
            {
                Path = path,
                IsInitialized = true,
                HasWorkingTreeChanges = true,
                HeadCommit = new string('b', 40),
                WorktreeStatus = "? local.txt\n"
            };
        }

        private static SubmoduleRemovalAssessment CreateUnverifiedAssessment(
            string path)
        {
            return new SubmoduleRemovalAssessment
            {
                Path = path,
                HasWorkingTreeChanges = true,
                HasUnverifiedWorktreeContents = true,
                WorktreeStatus = "orphaned.txt\n"
            };
        }
    }
}
