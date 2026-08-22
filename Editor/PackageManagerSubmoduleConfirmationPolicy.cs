namespace MartinCalander.GitSubmoduleManager.Editor
{
    internal enum PackageManagerSubmoduleDestructiveAction
    {
        Uninstall,
        ConvertToReadOnly
    }

    internal enum PackageManagerSubmoduleConfirmationRequirement
    {
        Blocked,
        ProceedWithoutPrompt,
        RoutinePrompt,
        DiscardPrompt
    }

    /// <summary>
    /// Immutable result of evaluating one assessed submodule operation. The
    /// caller still owns selection validation, showing the prompt, and passing
    /// the exact assessment snapshot to the destructive service.
    /// </summary>
    internal sealed class PackageManagerSubmoduleConfirmationDecision
    {
        internal PackageManagerSubmoduleConfirmationDecision(
            PackageManagerSubmoduleDestructiveAction action,
            PackageManagerSubmoduleConfirmationRequirement requirement,
            string title,
            string message,
            string acceptText,
            string cancelText)
        {
            Action = action;
            Requirement = requirement;
            Title = title ?? string.Empty;
            Message = message ?? string.Empty;
            AcceptText = acceptText ?? string.Empty;
            CancelText = cancelText ?? string.Empty;
        }

        internal PackageManagerSubmoduleDestructiveAction Action { get; }
        internal PackageManagerSubmoduleConfirmationRequirement Requirement { get; }
        internal string Title { get; }
        internal string Message { get; }
        internal string AcceptText { get; }
        internal string CancelText { get; }

        internal bool IsBlocked =>
            Requirement == PackageManagerSubmoduleConfirmationRequirement.Blocked;

        internal bool CanProceedWithoutPrompt =>
            Requirement ==
            PackageManagerSubmoduleConfirmationRequirement.ProceedWithoutPrompt;

        internal bool RequiresPrompt =>
            Requirement == PackageManagerSubmoduleConfirmationRequirement.RoutinePrompt ||
            Requirement == PackageManagerSubmoduleConfirmationRequirement.DiscardPrompt;

        internal bool DiscardLocalWorkIfAccepted =>
            Requirement == PackageManagerSubmoduleConfirmationRequirement.DiscardPrompt;
    }

    /// <summary>
    /// Central confirmation policy for operations that remove an installed
    /// submodule worktree. A user preference may suppress only the routine
    /// prompt for a clean assessment; it never authorizes discarding local work.
    /// </summary>
    internal static class PackageManagerSubmoduleConfirmationPolicy
    {
        internal const string UninstallTitle = "Uninstall Git Submodule?";
        internal const string ConvertTitle = "Convert Submodule to Read-Only?";
        internal const string DiscardTitle = "Local Work Would Be Discarded";
        internal const string UninstallAcceptText = "Uninstall";
        internal const string ConvertAcceptText = "Convert";
        internal const string CancelText = "Cancel";
        internal const string DiscardUninstallAcceptText =
            "Discard Changes and Uninstall";
        internal const string DiscardConvertAcceptText =
            "Discard Changes and Convert";
        internal const string KeepPackageText = "Keep Package";
        internal const string KeepSubmoduleText = "Keep Submodule";

        internal static PackageManagerSubmoduleConfirmationDecision Evaluate(
            PackageManagerSubmoduleDestructiveAction action,
            string packageName,
            string packagePath,
            SubmoduleRemovalAssessment assessment,
            bool suppressRoutinePrompt)
        {
            if (action != PackageManagerSubmoduleDestructiveAction.Uninstall &&
                action !=
                PackageManagerSubmoduleDestructiveAction.ConvertToReadOnly)
            {
                return Block(
                    action,
                    "The requested submodule operation is not supported.");
            }

            string displayName = NormalizePackageName(packageName);
            string displayPath = NormalizePackagePath(packagePath, packageName);

            if (assessment == null)
            {
                return Block(
                    action,
                    "The Git submodule must be inspected before this operation can start.");
            }

            if (assessment.HasUnverifiedWorktreeContents)
            {
                return Block(
                    action,
                    "The package directory contains files but is not an initialized " +
                    "submodule worktree. Move those files to safety and leave the " +
                    "directory empty before continuing. Git Submodule Manager will " +
                    "not discard unverified files.");
            }

            if (RequiresDiscardConfirmation(assessment))
            {
                return BuildDiscardPrompt(
                    action,
                    displayName,
                    displayPath,
                    assessment);
            }

            PackageManagerSubmoduleConfirmationRequirement requirement =
                suppressRoutinePrompt
                    ? PackageManagerSubmoduleConfirmationRequirement
                        .ProceedWithoutPrompt
                    : PackageManagerSubmoduleConfirmationRequirement.RoutinePrompt;
            return BuildRoutineDecision(
                action,
                requirement,
                displayName,
                displayPath);
        }

        internal static bool RequiresDiscardConfirmation(
            SubmoduleRemovalAssessment assessment)
        {
            return assessment != null &&
                   (!assessment.IsSafe ||
                    assessment.HasOnlyParentGitlinkChanges ||
                    assessment.HasGitModulesTargetChanges);
        }

        private static PackageManagerSubmoduleConfirmationDecision Block(
            PackageManagerSubmoduleDestructiveAction action,
            string message)
        {
            return new PackageManagerSubmoduleConfirmationDecision(
                action,
                PackageManagerSubmoduleConfirmationRequirement.Blocked,
                string.Empty,
                message,
                string.Empty,
                string.Empty);
        }

        private static PackageManagerSubmoduleConfirmationDecision
            BuildRoutineDecision(
                PackageManagerSubmoduleDestructiveAction action,
                PackageManagerSubmoduleConfirmationRequirement requirement,
                string packageName,
                string packagePath)
        {
            if (action == PackageManagerSubmoduleDestructiveAction.Uninstall)
            {
                return new PackageManagerSubmoduleConfirmationDecision(
                    action,
                    requirement,
                    UninstallTitle,
                    $"Uninstall {packageName} at {packagePath} as a Git submodule? " +
                    "Git will remove the tracked registration and worktree after " +
                    "confirming their state has not changed.",
                    UninstallAcceptText,
                    CancelText);
            }

            return new PackageManagerSubmoduleConfirmationDecision(
                action,
                requirement,
                ConvertTitle,
                $"Convert {packageName} from an editable Git submodule to a " +
                "read-only Package Manager Git dependency? The dependency is " +
                "recorded before the verified submodule worktree is removed.",
                ConvertAcceptText,
                CancelText);
        }

        private static PackageManagerSubmoduleConfirmationDecision
            BuildDiscardPrompt(
                PackageManagerSubmoduleDestructiveAction action,
                string packageName,
                string packagePath,
                SubmoduleRemovalAssessment assessment)
        {
            string warning = assessment.BuildWarning();
            if (string.IsNullOrWhiteSpace(warning))
                warning = "Removing this package would discard local Git work.";

            if (action == PackageManagerSubmoduleDestructiveAction.Uninstall)
            {
                return new PackageManagerSubmoduleConfirmationDecision(
                    action,
                    PackageManagerSubmoduleConfirmationRequirement.DiscardPrompt,
                    DiscardTitle,
                    warning + " " +
                    $"Uninstall {packageName} at {packagePath} anyway? Git will " +
                    "remove the package worktree and parent gitlink changes. This " +
                    "cannot be undone from the Unity UI.",
                    DiscardUninstallAcceptText,
                    KeepPackageText);
            }

            string remoteProofNotice = assessment.HasLocalOnlyCommits
                ? "Conversion will verify that the current HEAD is fetchable " +
                  "from the repository; if it is not published, conversion is " +
                  "blocked and the submodule remains untouched. "
                : string.Empty;
            return new PackageManagerSubmoduleConfirmationDecision(
                action,
                PackageManagerSubmoduleConfirmationRequirement.DiscardPrompt,
                DiscardTitle,
                warning + " " + remoteProofNotice +
                $"Convert {packageName} to a read-only Package Manager Git " +
                "dependency anyway? The dependency pins the current committed " +
                "HEAD; modified, untracked, ignored, conflicted, and parent-gitlink " +
                "changes are not included and will be discarded. This cannot be " +
                "undone from the Unity UI.",
                DiscardConvertAcceptText,
                KeepSubmoduleText);
        }

        private static string NormalizePackageName(string packageName)
        {
            return string.IsNullOrWhiteSpace(packageName)
                ? "this package"
                : packageName.Trim();
        }

        private static string NormalizePackagePath(
            string packagePath,
            string packageName)
        {
            if (!string.IsNullOrWhiteSpace(packagePath))
                return GitUtility.NormalizePath(packagePath.Trim());

            return string.IsNullOrWhiteSpace(packageName)
                ? "the selected package path"
                : "Packages/" + packageName.Trim();
        }
    }
}
