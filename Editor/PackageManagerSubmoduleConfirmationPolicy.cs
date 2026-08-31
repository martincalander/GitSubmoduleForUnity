using System;
using System.Collections.Generic;
using System.Text;

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

    internal sealed class PackageManagerSubmoduleBatchConfirmationDecision
    {
        internal PackageManagerSubmoduleBatchConfirmationDecision(
            PackageManagerSubmoduleConfirmationRequirement requirement,
            string title,
            string message,
            string acceptText,
            string cancelText,
            IReadOnlyList<bool> discardLocalWork)
        {
            Requirement = requirement;
            Title = title ?? string.Empty;
            Message = message ?? string.Empty;
            AcceptText = acceptText ?? string.Empty;
            CancelText = cancelText ?? string.Empty;
            DiscardLocalWork = discardLocalWork ?? Array.Empty<bool>();
        }

        internal PackageManagerSubmoduleConfirmationRequirement Requirement { get; }
        internal string Title { get; }
        internal string Message { get; }
        internal string AcceptText { get; }
        internal string CancelText { get; }
        internal IReadOnlyList<bool> DiscardLocalWork { get; }
        internal bool IsBlocked =>
            Requirement == PackageManagerSubmoduleConfirmationRequirement.Blocked;
        internal bool CanProceedWithoutPrompt =>
            Requirement ==
            PackageManagerSubmoduleConfirmationRequirement.ProceedWithoutPrompt;
    }

    /// <summary>
    /// Central confirmation policy for operations that remove an installed
    /// submodule worktree. A user preference may suppress only the routine
    /// prompt for a clean assessment; it never authorizes discarding local work.
    /// </summary>
    internal static class PackageManagerSubmoduleConfirmationPolicy
    {
        internal const string UninstallTitle = "Remove Git Submodule?";
        internal const string ConvertTitle = "Convert Submodule to Read-Only?";
        internal const string DiscardTitle = "Local Work Would Be Discarded";
        internal const string UninstallAcceptText = "Remove";
        internal const string ConvertAcceptText = "Convert";
        internal const string CancelText = "Cancel";
        internal const string DiscardUninstallAcceptText =
            "Discard Changes and Remove";
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

        internal static PackageManagerSubmoduleBatchConfirmationDecision
            EvaluateBatchRemoval(
                IReadOnlyList<PackageManagerSubmoduleInfo> infos,
                IReadOnlyList<SubmoduleRemovalAssessment> assessments,
                IReadOnlyList<string> ordinaryPackageNames,
                bool includesManager,
                bool suppressRoutinePrompt)
        {
            int submoduleCount = infos?.Count ?? 0;
            int ordinaryPackageCount = ordinaryPackageNames?.Count ?? 0;
            if (submoduleCount == 0 || assessments == null ||
                assessments.Count != submoduleCount ||
                ordinaryPackageNames == null)
            {
                return BlockBatch(
                    "Every selected Git submodule must be inspected before removal.",
                    submoduleCount);
            }

            var discardLocalWork = new bool[submoduleCount];
            var warnings = new List<string>();
            bool managerIsSubmodule = false;
            for (int index = 0; index < submoduleCount; index++)
            {
                PackageManagerSubmoduleInfo info = infos[index];
                managerIsSubmodule |= string.Equals(
                    info?.PackageName,
                    GitPackageConversionService.ManagerPackageName,
                    StringComparison.Ordinal);
                PackageManagerSubmoduleConfirmationDecision decision = Evaluate(
                    PackageManagerSubmoduleDestructiveAction.Uninstall,
                    info?.PackageName,
                    info?.PackagePath,
                    assessments[index],
                    false);
                if (decision.IsBlocked)
                {
                    string packageName = NormalizePackageName(info?.PackageName);
                    return BlockBatch(
                        packageName + ": " + decision.Message,
                        submoduleCount);
                }

                discardLocalWork[index] = decision.DiscardLocalWorkIfAccepted;
                if (discardLocalWork[index])
                {
                    string warning = assessments[index]?.BuildWarning();
                    warnings.Add(
                        NormalizePackageName(info?.PackageName) + ": " +
                        (string.IsNullOrWhiteSpace(warning)
                            ? "local Git work would be discarded."
                            : warning.Trim()));
                }
            }

            if (managerIsSubmodule && ordinaryPackageCount > 0)
            {
                return BlockBatch(
                    "Remove Git Submodule Manager separately from ordinary " +
                    "Unity packages. Removing the manager submodule reloads its " +
                    "code before it can safely resume the ordinary-package " +
                    "portion of this selection.",
                    submoduleCount);
            }

            int totalCount = submoduleCount + ordinaryPackageCount;
            bool requiresDiscardPrompt = warnings.Count > 0;
            bool requiresRoutinePrompt =
                ordinaryPackageCount > 0 || includesManager ||
                !suppressRoutinePrompt;
            PackageManagerSubmoduleConfirmationRequirement requirement =
                requiresDiscardPrompt
                    ? PackageManagerSubmoduleConfirmationRequirement.DiscardPrompt
                    : requiresRoutinePrompt
                        ? PackageManagerSubmoduleConfirmationRequirement.RoutinePrompt
                        : PackageManagerSubmoduleConfirmationRequirement
                            .ProceedWithoutPrompt;

            var message = new StringBuilder();
            message.Append("Remove ");
            message.Append(totalCount);
            message.Append(totalCount == 1
                ? " selected package? "
                : " selected packages? ");
            message.Append(submoduleCount);
            message.Append(submoduleCount == 1
                ? " Git submodule will be removed through Git"
                : " Git submodules will be removed through Git");
            if (ordinaryPackageCount > 0)
            {
                message.Append(", and ");
                message.Append(ordinaryPackageCount);
                message.Append(ordinaryPackageCount == 1
                    ? " other package will be removed through Unity Package Manager"
                    : " other packages will be removed through Unity Package Manager");
            }
            message.Append(".");

            message.Append("\n\nSelected packages:");
            for (int index = 0; index < submoduleCount; index++)
            {
                message.Append("\n- ");
                message.Append(NormalizePackageName(infos[index]?.PackageName));
                message.Append(" (Git submodule)");
            }
            for (int index = 0; index < ordinaryPackageCount; index++)
            {
                message.Append("\n- ");
                message.Append(NormalizePackageName(ordinaryPackageNames[index]));
                message.Append(" (Unity package)");
            }

            if (includesManager)
            {
                message.Append(" Removing Git Submodule Manager disables its ");
                message.Append("Package Manager integration and submodule tools ");
                message.Append("after Unity reloads. Other packages and submodules ");
                message.Append("that are not selected remain in place.");
            }

            if (warnings.Count > 0)
            {
                message.Append(" The following local work will be discarded:");
                for (int index = 0; index < warnings.Count; index++)
                {
                    message.Append("\n\n");
                    message.Append(warnings[index]);
                }
                message.Append(" This cannot be undone from the Unity UI.");
            }

            return new PackageManagerSubmoduleBatchConfirmationDecision(
                requirement,
                requiresDiscardPrompt
                    ? DiscardTitle
                    : totalCount == 1
                        ? UninstallTitle
                        : "Remove Selected Packages?",
                message.ToString(),
                requiresDiscardPrompt
                    ? DiscardUninstallAcceptText
                    : UninstallAcceptText,
                totalCount == 1 ? KeepPackageText : "Keep Packages",
                discardLocalWork);
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
                    $"Remove {packageName} at {packagePath} as a Git submodule? " +
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
                    $"Remove {packageName} at {packagePath} anyway? Git will " +
                    "remove the package worktree and parent gitlink changes. This " +
                    "cannot be undone from the Unity UI.",
                    DiscardUninstallAcceptText,
                    KeepPackageText);
            }

            string remoteProofNotice = assessment.HasLocalOnlyCommits
                ? "Conversion will verify that the current HEAD is reachable " +
                  "from a branch or tag currently advertised by the repository; " +
                  "if it is not published, conversion is blocked and the " +
                  "submodule remains untouched. "
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

        private static PackageManagerSubmoduleBatchConfirmationDecision BlockBatch(
            string message,
            int submoduleCount)
        {
            return new PackageManagerSubmoduleBatchConfirmationDecision(
                PackageManagerSubmoduleConfirmationRequirement.Blocked,
                string.Empty,
                message,
                string.Empty,
                string.Empty,
                new bool[Math.Max(0, submoduleCount)]);
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
