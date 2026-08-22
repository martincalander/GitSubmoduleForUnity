using System;
using System.Linq;
using System.Text;
using UnityEditor;

namespace MartinCalander.GitSubmoduleManager.Editor
{
    internal sealed class PackageDependencyPromptContent
    {
        internal PackageDependencyPromptContent(
            string title,
            string message,
            string acceptText,
            string cancelText,
            bool isBlocking)
        {
            Title = title ?? string.Empty;
            Message = message ?? string.Empty;
            AcceptText = acceptText ?? string.Empty;
            CancelText = cancelText ?? string.Empty;
            IsBlocking = isBlocking;
        }

        internal string Title { get; }
        internal string Message { get; }
        internal string AcceptText { get; }
        internal string CancelText { get; }
        internal bool IsBlocking { get; }
    }

    internal interface IPackageDependencyModalDialog
    {
        bool Confirm(PackageDependencyPromptContent content);
        void ShowBlocking(PackageDependencyPromptContent content);
    }

    internal sealed class UnityPackageDependencyModalDialog :
        IPackageDependencyModalDialog
    {
        internal static UnityPackageDependencyModalDialog Instance { get; } =
            new();

        private UnityPackageDependencyModalDialog()
        {
        }

        public bool Confirm(PackageDependencyPromptContent content)
        {
            return content != null && EditorUtility.DisplayDialog(
                content.Title,
                content.Message,
                content.AcceptText,
                content.CancelText);
        }

        public void ShowBlocking(PackageDependencyPromptContent content)
        {
            if (content == null)
                return;
            EditorUtility.DisplayDialog(
                content.Title,
                content.Message,
                content.AcceptText);
        }
    }

    /// <summary>
    /// Pure formatter and guarded modal presenter for missing dependencies.
    /// Suppression is allowed only for a terminal plan whose every requirement
    /// has exactly one resolved candidate.
    /// </summary>
    internal static class PackageDependencyInstallPrompt
    {
        internal static bool CanInstall(
            PackageDependencyResolutionPlan plan)
        {
            return plan != null &&
                   plan.IsComplete &&
                   !plan.HasBlockingIssues &&
                   plan.Results.All(result =>
                       result.Status == PackageDependencyResolutionStatus.Resolved &&
                       result.SelectedCandidate != null);
        }

        internal static bool CanSkipPrompt(
            PackageDependencyResolutionPlan plan,
            bool installDependenciesWithoutPrompt)
        {
            return installDependenciesWithoutPrompt && CanInstall(plan);
        }

        internal static PackageDependencyPromptContent BuildContent(
            PackageDependencyInstallRequest request,
            PackageDependencyResolutionPlan plan)
        {
            bool blocking = !CanInstall(plan);
            var message = new StringBuilder();
            message.Append("Package: ")
                .Append(Safe(request?.RootPackageName))
                .Append('\n')
                .Append("Repository: ")
                .Append(Safe(request?.RepositoryUrl))
                .Append('\n')
                .Append("Branch: ")
                .Append(Safe(request?.Revision))
                .Append("\n\n");
            message.Append(blocking
                ? "The package cannot be installed until every missing dependency has one safe source."
                : "The following missing dependencies will be installed or resolved before the package:");
            message.Append("\n\n");

            if (plan?.Results == null || plan.Results.Count == 0)
            {
                message.Append("No missing dependencies were found.");
            }
            else
            {
                foreach (PackageDependencyResolutionResult result in plan.Results)
                {
                    message.Append("• ")
                        .Append(Safe(result.Requirement.Name))
                        .Append(" ")
                        .Append(Safe(result.Requirement.Version))
                        .Append(" — ")
                        .Append(BuildSourceLabel(result))
                        .Append('\n');
                }
            }

            if (!string.IsNullOrWhiteSpace(plan?.ErrorMessage))
            {
                message.Append("\n")
                    .Append(Safe(plan.ErrorMessage))
                    .Append('\n');
            }

            if (!blocking)
            {
                string mode = request?.InstallMode ==
                              PackageManagerGitInstallMode.ReadOnlyPackage
                    ? "read-only Git packages"
                    : "Git submodules";
                message.Append("\nGitHub dependencies will use ")
                    .Append(mode)
                    .Append(". Registry dependencies remain transitive and are resolved by Unity Package Manager.");
            }

            return new PackageDependencyPromptContent(
                blocking
                    ? "Missing Dependencies Need Attention"
                    : "Install Missing Dependencies?",
                message.ToString().Trim(),
                blocking ? "OK" : "Install Dependencies & Continue",
                blocking ? string.Empty : "Cancel",
                blocking);
        }

        internal static bool TryConfirm(
            PackageDependencyInstallRequest request,
            PackageDependencyResolutionPlan plan,
            bool installDependenciesWithoutPrompt,
            out string error,
            IPackageDependencyModalDialog dialog = null)
        {
            error = string.Empty;
            if (plan == null || !plan.IsComplete)
            {
                error = "Dependency preflight has not completed.";
                return false;
            }

            if (plan.Results.Count == 0 && !plan.HasBlockingIssues)
                return true;
            if (CanSkipPrompt(plan, installDependenciesWithoutPrompt))
                return true;

            PackageDependencyPromptContent content = BuildContent(request, plan);
            IPackageDependencyModalDialog presenter =
                dialog ?? UnityPackageDependencyModalDialog.Instance;
            if (content.IsBlocking)
            {
                presenter.ShowBlocking(content);
                error = "One or more missing dependencies are unresolved or ambiguous.";
                return false;
            }

            if (presenter.Confirm(content))
                return true;

            error = "Dependency installation was cancelled.";
            return false;
        }

        private static string BuildSourceLabel(
            PackageDependencyResolutionResult result)
        {
            if (result == null)
                return "Unresolved";
            if (result.Status == PackageDependencyResolutionStatus.Ambiguous)
            {
                string choices = string.Join(
                    ", ",
                    result.Candidates.Select(candidate =>
                        Safe(candidate.SourceName)));
                return string.IsNullOrWhiteSpace(choices)
                    ? "Ambiguous"
                    : "Ambiguous: " + choices;
            }
            if (result.Status != PackageDependencyResolutionStatus.Resolved ||
                result.SelectedCandidate == null)
            {
                return string.IsNullOrWhiteSpace(result.Message)
                    ? "Unresolved"
                    : "Unresolved: " + Safe(result.Message);
            }

            PackageDependencyCandidate candidate = result.SelectedCandidate;
            switch (candidate.Source)
            {
                case PackageDependencyCandidateSource.GitHub:
                    return "GitHub (" + Safe(candidate.SourceName) + ")";
                case PackageDependencyCandidateSource.UnityRegistry:
                    return "Unity Registry (" + Safe(candidate.SourceName) + ")";
                case PackageDependencyCandidateSource.CustomRegistry:
                    return "Custom Registry (" + Safe(candidate.SourceName) + ")";
                default:
                    return "Unresolved";
            }
        }

        private static string Safe(string value)
        {
            const int maximumLabelLength = 256;
            string sanitized =
                PackageDependencyResolutionService.SanitizeDiagnostic(value);
            var singleLine = new StringBuilder(sanitized.Length);
            foreach (char character in sanitized)
            {
                singleLine.Append(char.IsControl(character) ? ' ' : character);
            }
            sanitized = singleLine.ToString().Trim();
            if (sanitized.Length <= maximumLabelLength)
                return sanitized;
            return sanitized.Substring(0, maximumLabelLength - 3) + "...";
        }
    }
}
