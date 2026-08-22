using System.Collections.Generic;
using NUnit.Framework;

namespace MartinCalander.GitSubmoduleManager.Editor.Tests
{
    public sealed class PackageManagerSubmoduleConfirmationPolicyTests
    {
        private const string PackageName = "com.example.tool";
        private const string PackagePath = "Packages/com.example.tool";

        [TestCase((int)PackageManagerSubmoduleDestructiveAction.Uninstall)]
        [TestCase((int)PackageManagerSubmoduleDestructiveAction.ConvertToReadOnly)]
        public void CleanAssessment_RequiresRoutinePromptByDefault(
            int actionValue)
        {
            var action = (PackageManagerSubmoduleDestructiveAction)actionValue;
            PackageManagerSubmoduleConfirmationDecision decision = Evaluate(
                action,
                CreateCleanAssessment(),
                suppressRoutinePrompt: false);

            Assert.That(
                decision.Requirement,
                Is.EqualTo(
                    PackageManagerSubmoduleConfirmationRequirement.RoutinePrompt));
            Assert.That(decision.RequiresPrompt, Is.True);
            Assert.That(decision.CanProceedWithoutPrompt, Is.False);
            Assert.That(decision.DiscardLocalWorkIfAccepted, Is.False);
            Assert.That(decision.IsBlocked, Is.False);
        }

        [TestCase((int)PackageManagerSubmoduleDestructiveAction.Uninstall)]
        [TestCase((int)PackageManagerSubmoduleDestructiveAction.ConvertToReadOnly)]
        public void CleanAssessment_SuppressionSkipsOnlyRoutinePrompt(
            int actionValue)
        {
            var action = (PackageManagerSubmoduleDestructiveAction)actionValue;
            PackageManagerSubmoduleConfirmationDecision decision = Evaluate(
                action,
                CreateCleanAssessment(),
                suppressRoutinePrompt: true);

            Assert.That(
                decision.Requirement,
                Is.EqualTo(
                    PackageManagerSubmoduleConfirmationRequirement
                        .ProceedWithoutPrompt));
            Assert.That(decision.CanProceedWithoutPrompt, Is.True);
            Assert.That(decision.RequiresPrompt, Is.False);
            Assert.That(decision.DiscardLocalWorkIfAccepted, Is.False);
            Assert.That(decision.IsBlocked, Is.False);
        }

        [TestCaseSource(nameof(DestructiveAssessments))]
        public void LocalWork_AlwaysRequiresDiscardPrompt(
            object assessmentValue)
        {
            var assessment = (SubmoduleRemovalAssessment)assessmentValue;
            foreach (bool suppressRoutinePrompt in new[] { false, true })
            {
                foreach (PackageManagerSubmoduleDestructiveAction action in
                         new[]
                         {
                             PackageManagerSubmoduleDestructiveAction.Uninstall,
                             PackageManagerSubmoduleDestructiveAction.ConvertToReadOnly
                         })
                {
                    PackageManagerSubmoduleConfirmationDecision decision = Evaluate(
                        action,
                        assessment,
                        suppressRoutinePrompt);

                    Assert.That(
                        decision.Requirement,
                        Is.EqualTo(
                            PackageManagerSubmoduleConfirmationRequirement
                                .DiscardPrompt));
                    Assert.That(decision.RequiresPrompt, Is.True);
                    Assert.That(decision.CanProceedWithoutPrompt, Is.False);
                    Assert.That(decision.DiscardLocalWorkIfAccepted, Is.True);
                    Assert.That(decision.IsBlocked, Is.False);
                    Assert.That(decision.Message, Does.Contain("would discard"));
                }
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void UnverifiedWorktreeContents_AlwaysBlock(bool suppressRoutinePrompt)
        {
            var assessment = new SubmoduleRemovalAssessment
            {
                Path = PackagePath,
                HasWorkingTreeChanges = true,
                HasUnverifiedWorktreeContents = true,
                WorktreeStatus = "orphaned.txt\n"
            };

            foreach (PackageManagerSubmoduleDestructiveAction action in
                     new[]
                     {
                         PackageManagerSubmoduleDestructiveAction.Uninstall,
                         PackageManagerSubmoduleDestructiveAction.ConvertToReadOnly
                     })
            {
                PackageManagerSubmoduleConfirmationDecision decision = Evaluate(
                    action,
                    assessment,
                    suppressRoutinePrompt);

                Assert.That(decision.IsBlocked, Is.True);
                Assert.That(decision.RequiresPrompt, Is.False);
                Assert.That(decision.CanProceedWithoutPrompt, Is.False);
                Assert.That(decision.DiscardLocalWorkIfAccepted, Is.False);
                Assert.That(decision.Message, Does.Contain("unverified files"));
                Assert.That(decision.Title, Is.Empty);
                Assert.That(decision.AcceptText, Is.Empty);
                Assert.That(decision.CancelText, Is.Empty);
            }
        }

        [TestCase((int)PackageManagerSubmoduleDestructiveAction.Uninstall)]
        [TestCase((int)PackageManagerSubmoduleDestructiveAction.ConvertToReadOnly)]
        public void MissingAssessment_BlocksBeforeAnyPrompt(
            int actionValue)
        {
            var action = (PackageManagerSubmoduleDestructiveAction)actionValue;
            PackageManagerSubmoduleConfirmationDecision decision = Evaluate(
                action,
                null,
                suppressRoutinePrompt: true);

            Assert.That(decision.IsBlocked, Is.True);
            Assert.That(decision.Message, Does.Contain("must be inspected"));
            Assert.That(decision.RequiresPrompt, Is.False);
        }

        [Test]
        public void UnsupportedAction_FailsClosed()
        {
            PackageManagerSubmoduleConfirmationDecision decision =
                PackageManagerSubmoduleConfirmationPolicy.Evaluate(
                    (PackageManagerSubmoduleDestructiveAction)999,
                    PackageName,
                    PackagePath,
                    CreateCleanAssessment(),
                    suppressRoutinePrompt: true);

            Assert.That(decision.IsBlocked, Is.True);
            Assert.That(decision.RequiresPrompt, Is.False);
            Assert.That(decision.Message, Does.Contain("not supported"));
        }

        [Test]
        public void RoutinePrompts_UseDistinctExactActionWording()
        {
            PackageManagerSubmoduleConfirmationDecision uninstall = Evaluate(
                PackageManagerSubmoduleDestructiveAction.Uninstall,
                CreateCleanAssessment(),
                suppressRoutinePrompt: false);
            PackageManagerSubmoduleConfirmationDecision convert = Evaluate(
                PackageManagerSubmoduleDestructiveAction.ConvertToReadOnly,
                CreateCleanAssessment(),
                suppressRoutinePrompt: false);

            Assert.That(
                uninstall.Title,
                Is.EqualTo(
                    PackageManagerSubmoduleConfirmationPolicy.UninstallTitle));
            Assert.That(
                uninstall.AcceptText,
                Is.EqualTo(
                    PackageManagerSubmoduleConfirmationPolicy.UninstallAcceptText));
            Assert.That(
                uninstall.CancelText,
                Is.EqualTo(PackageManagerSubmoduleConfirmationPolicy.CancelText));
            Assert.That(uninstall.Message,
                Does.Contain($"Uninstall {PackageName} at {PackagePath}"));
            Assert.That(uninstall.Message, Does.Contain("tracked registration"));

            Assert.That(
                convert.Title,
                Is.EqualTo(PackageManagerSubmoduleConfirmationPolicy.ConvertTitle));
            Assert.That(
                convert.AcceptText,
                Is.EqualTo(
                    PackageManagerSubmoduleConfirmationPolicy.ConvertAcceptText));
            Assert.That(
                convert.CancelText,
                Is.EqualTo(PackageManagerSubmoduleConfirmationPolicy.CancelText));
            Assert.That(convert.Message, Does.Contain($"Convert {PackageName}"));
            Assert.That(convert.Message,
                Does.Contain("read-only Package Manager Git dependency"));
            Assert.That(convert.Message, Does.Not.Contain("Uninstall"));
        }

        [Test]
        public void DiscardPrompts_UseDistinctExactActionWording()
        {
            var assessment = new SubmoduleRemovalAssessment
            {
                Path = PackagePath,
                IsInitialized = true,
                HasWorkingTreeChanges = true,
                WorktreeStatus = "? local.txt\n"
            };
            PackageManagerSubmoduleConfirmationDecision uninstall = Evaluate(
                PackageManagerSubmoduleDestructiveAction.Uninstall,
                assessment,
                suppressRoutinePrompt: true);
            PackageManagerSubmoduleConfirmationDecision convert = Evaluate(
                PackageManagerSubmoduleDestructiveAction.ConvertToReadOnly,
                assessment,
                suppressRoutinePrompt: true);

            Assert.That(
                uninstall.Title,
                Is.EqualTo(PackageManagerSubmoduleConfirmationPolicy.DiscardTitle));
            Assert.That(
                uninstall.AcceptText,
                Is.EqualTo(
                    PackageManagerSubmoduleConfirmationPolicy
                        .DiscardUninstallAcceptText));
            Assert.That(
                uninstall.CancelText,
                Is.EqualTo(PackageManagerSubmoduleConfirmationPolicy.KeepPackageText));
            Assert.That(uninstall.Message,
                Does.Contain($"Uninstall {PackageName} at {PackagePath} anyway?"));

            Assert.That(
                convert.Title,
                Is.EqualTo(PackageManagerSubmoduleConfirmationPolicy.DiscardTitle));
            Assert.That(
                convert.AcceptText,
                Is.EqualTo(
                    PackageManagerSubmoduleConfirmationPolicy
                        .DiscardConvertAcceptText));
            Assert.That(
                convert.CancelText,
                Is.EqualTo(PackageManagerSubmoduleConfirmationPolicy.KeepSubmoduleText));
            Assert.That(convert.Message,
                Does.Contain($"Convert {PackageName} to a read-only"));
            Assert.That(convert.Message, Does.Contain("not included"));
        }

        [Test]
        public void LocalOnlyCommit_ConversionDefersRemoteProofToService()
        {
            var assessment = new SubmoduleRemovalAssessment
            {
                Path = PackagePath,
                IsInitialized = true,
                HasLocalOnlyCommits = true,
                LocalOnlyCommitCount = 1,
                HeadCommit = new string('c', 40)
            };

            PackageManagerSubmoduleConfirmationDecision decision = Evaluate(
                PackageManagerSubmoduleDestructiveAction.ConvertToReadOnly,
                assessment,
                suppressRoutinePrompt: true);

            Assert.That(decision.IsBlocked, Is.False);
            Assert.That(
                decision.Requirement,
                Is.EqualTo(
                    PackageManagerSubmoduleConfirmationRequirement.DiscardPrompt));
            Assert.That(decision.DiscardLocalWorkIfAccepted, Is.True);
            Assert.That(decision.Message, Does.Contain("HEAD is fetchable"));
            Assert.That(decision.Message, Does.Contain("not published"));
            Assert.That(decision.Message, Does.Contain("remains untouched"));
        }

        [Test]
        public void MissingDisplayValues_UseDeterministicSafeFallbacks()
        {
            PackageManagerSubmoduleConfirmationDecision decision =
                PackageManagerSubmoduleConfirmationPolicy.Evaluate(
                    PackageManagerSubmoduleDestructiveAction.Uninstall,
                    "  ",
                    null,
                    CreateCleanAssessment(),
                    suppressRoutinePrompt: false);

            Assert.That(decision.Message, Does.Contain("this package"));
            Assert.That(decision.Message,
                Does.Contain("the selected package path"));
        }

        private static PackageManagerSubmoduleConfirmationDecision Evaluate(
            PackageManagerSubmoduleDestructiveAction action,
            SubmoduleRemovalAssessment assessment,
            bool suppressRoutinePrompt)
        {
            return PackageManagerSubmoduleConfirmationPolicy.Evaluate(
                action,
                PackageName,
                PackagePath,
                assessment,
                suppressRoutinePrompt);
        }

        private static SubmoduleRemovalAssessment CreateCleanAssessment()
        {
            return new SubmoduleRemovalAssessment
            {
                Path = PackagePath,
                IsInitialized = true,
                HeadCommit = new string('a', 40)
            };
        }

        private static IEnumerable<TestCaseData> DestructiveAssessments()
        {
            yield return new TestCaseData(new SubmoduleRemovalAssessment
                {
                    Path = PackagePath,
                    IsInitialized = true,
                    HasWorkingTreeChanges = true,
                    WorktreeStatus = " M modified.txt\n? untracked.txt\n!! ignored.txt\n"
                })
                .SetName("WorkingTree_ModifiedUntrackedOrIgnored");
            yield return new TestCaseData(new SubmoduleRemovalAssessment
                {
                    Path = PackagePath,
                    IsInitialized = true,
                    HasConflicts = true,
                    WorktreeStatus = "UU conflicted.txt\n"
                })
                .SetName("WorkingTree_Conflicts");
            yield return new TestCaseData(new SubmoduleRemovalAssessment
                {
                    Path = PackagePath,
                    IsInitialized = true,
                    HasLocalOnlyCommits = true,
                    LocalOnlyCommitCount = 2
                })
                .SetName("History_LocalOnlyCommits");
            yield return new TestCaseData(new SubmoduleRemovalAssessment
                {
                    Path = PackagePath,
                    IsInitialized = true,
                    HasOnlyParentGitlinkChanges = true,
                    ParentStatus = "M  " + PackagePath + "\n"
                })
                .SetName("ParentRepository_GitlinkChangesFailClosed");
            yield return new TestCaseData(new SubmoduleRemovalAssessment
                {
                    Path = PackagePath,
                    IsInitialized = true,
                    HasGitModulesTargetChanges = true,
                    GitModulesTargetStatus = "M  .gitmodules\n"
                })
                .SetName("ParentRepository_GitModulesChangesFailClosed");
            yield return new TestCaseData(new SubmoduleRemovalAssessment
                {
                    Path = PackagePath,
                    IsInitialized = true,
                    HasParentChanges = true,
                    ParentStatus = "M  " + PackagePath + "\n"
                })
                .SetName("ParentRepository_OtherTrackedChanges");
        }
    }
}
