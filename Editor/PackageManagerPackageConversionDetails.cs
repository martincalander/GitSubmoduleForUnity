using System;
using UnityEditor;
using UnityEngine.UIElements;

namespace MartinCalander.GitSubmoduleManager.Editor
{
    /// <summary>
    /// Immutable selection data passed from the native Package Manager view to
    /// the conversion coordinator. Keeping the UI callback bound to this value
    /// prevents a recycled details view from converting a newer selection.
    /// </summary>
    internal sealed class PackageManagerPackageConversionTarget
    {
        internal PackageManagerPackageConversionTarget(
            GitPackageConversionDirection direction,
            string packageName,
            string packagePath,
            string repositoryUrl,
            string branch)
        {
            Direction = direction;
            PackageName = packageName?.Trim() ?? string.Empty;
            PackagePath = GitUtility.NormalizePath(packagePath ?? string.Empty);
            RepositoryUrl = repositoryUrl?.Trim() ?? string.Empty;
            Branch = branch?.Trim() ?? string.Empty;
            SelectionIdentity = BuildSelectionIdentity();
        }

        internal GitPackageConversionDirection Direction { get; }
        internal string PackageName { get; }
        internal string PackagePath { get; }
        internal string RepositoryUrl { get; }
        internal string Branch { get; }
        internal string SelectionIdentity { get; }

        private string BuildSelectionIdentity()
        {
            return ((int)Direction).ToString() + "|" +
                   Encode(PackageName) + Encode(PackagePath) +
                   Encode(RepositoryUrl) + Encode(Branch);
        }

        private static string Encode(string value)
        {
            string safeValue = value ?? string.Empty;
            return safeValue.Length + ":" + safeValue;
        }
    }

    /// <summary>
    /// Adds one conversion action to Unity's native Package Manager details
    /// toolbar. Confirmation and diagnostics remain inline so no modal state can
    /// outlive a recycled package selection.
    /// </summary>
    internal sealed class PackageManagerPackageConversionDetails : IDisposable
    {
        internal const string ControlsElementName =
            "git-submodule-manager-conversion-primary-actions";
        internal const string ConvertActionElementName =
            "git-submodule-manager-convert-action";
        internal const string CancelActionElementName =
            "git-submodule-manager-cancel-convert-action";
        internal const string FeedbackElementName =
            "git-submodule-manager-convert-feedback";
        internal const string ConvertToReadOnlyText =
            "Convert to Read-Only Package";
        internal const string ConvertToSubmoduleText = "Convert to Submodule";
        internal const string ConfirmConversionText = "Confirm Conversion";
        internal const string ConfirmDiscardText =
            "Discard Changes and Convert";
        internal const string InspectingText = "Inspecting...";
        internal const string ConvertingText = "Converting...";
        internal const string RetryConversionText = "Retry Conversion";
        internal const string ConvertedText = "Converted";

        private const string OwnedFeedbackContainerName =
            "git-submodule-manager-convert-feedback-container";

        private readonly VisualElement primaryActionsContainer;
        private readonly VisualElement detailsLinksContainer;
        private readonly VisualElement controls;
        private readonly Button convertButton;
        private readonly Button cancelButton;
        private readonly HelpBox feedback;
        private readonly Action<PackageManagerPackageConversionTarget>
            conversionRequested;

        private VisualElement ownedFeedbackContainer;
        private PackageManagerPackageConversionTarget currentTarget;
        private SubmoduleRemovalAssessment confirmedAssessment;
        private string currentIdentity = string.Empty;
        private string confirmationIdentity = string.Empty;
        private string availabilityTooltip = string.Empty;
        private bool actionEnabled;
        private bool discardLocalWork;
        private bool hasTarget;
        private bool isDisposed;
        private ConversionUiState state;

        private PackageManagerPackageConversionDetails(
            VisualElement primaryActionsContainer,
            VisualElement detailsLinksContainer,
            Action<PackageManagerPackageConversionTarget> conversionRequested)
        {
            this.primaryActionsContainer = primaryActionsContainer;
            this.detailsLinksContainer = detailsLinksContainer;
            this.conversionRequested = conversionRequested;

            controls = new VisualElement { name = ControlsElementName };
            controls.style.flexDirection = FlexDirection.Row;
            controls.style.alignItems = Align.Center;

            convertButton = new Button(OnConvertClicked)
            {
                name = ConvertActionElementName
            };
            controls.Add(convertButton);

            cancelButton = new Button(CancelConfirmation)
            {
                name = CancelActionElementName,
                text = L10n.Tr("Cancel")
            };
            cancelButton.style.marginLeft = 4f;
            controls.Add(cancelButton);

            feedback = new HelpBox(string.Empty, HelpBoxMessageType.Info)
            {
                name = FeedbackElementName
            };
            Label feedbackLabel = feedback.Q<Label>(
                className: HelpBox.labelUssClassName);
            if (feedbackLabel != null)
                feedbackLabel.enableRichText = false;

            EnsureMounted();
            SetVisible(false);
        }

        internal VisualElement Controls => controls;
        internal Button ConvertButton => convertButton;
        internal Button CancelButton => cancelButton;
        internal HelpBox Feedback => feedback;
        internal PackageManagerPackageConversionTarget CurrentTarget =>
            currentTarget;
        internal bool IsConfirmationPending =>
            state == ConversionUiState.Confirming;
        internal bool IsConverting => state == ConversionUiState.Converting;
        internal bool IsCompleted => state == ConversionUiState.Completed;
        internal bool IsActionEnabled => actionEnabled;
        internal string AvailabilityTooltip => availabilityTooltip;
        internal SubmoduleRemovalAssessment ConfirmedAssessment =>
            confirmedAssessment;
        internal bool DiscardLocalWork => discardLocalWork;

        internal static bool TryCreate(
            VisualElement primaryActionsContainer,
            VisualElement detailsLinksContainer,
            Action<PackageManagerPackageConversionTarget> conversionRequested,
            out PackageManagerPackageConversionDetails details)
        {
            details = null;
            if (primaryActionsContainer == null ||
                detailsLinksContainer == null ||
                conversionRequested == null ||
                !string.Equals(
                    detailsLinksContainer.name,
                    PackageManagerGitHubDetails.NativeDetailsLinksContainerName,
                    StringComparison.Ordinal))
            {
                return false;
            }

            try
            {
                details = new PackageManagerPackageConversionDetails(
                    primaryActionsContainer,
                    detailsLinksContainer,
                    conversionRequested);
                return true;
            }
            catch
            {
                details?.Dispose();
                details = null;
                return false;
            }
        }

        internal void Refresh(PackageManagerPackageConversionTarget target)
        {
            if (isDisposed)
                return;

            EnsureMounted();
            if (target == null)
            {
                currentTarget = null;
                currentIdentity = string.Empty;
                ResetState();
                SetVisible(false);
                return;
            }

            if (!string.Equals(
                    target.SelectionIdentity,
                    currentIdentity,
                    StringComparison.Ordinal))
            {
                actionEnabled = false;
                availabilityTooltip = string.Empty;
                ResetState();
            }

            currentTarget = target;
            currentIdentity = target.SelectionIdentity;
            SetVisible(true);
            ApplyState();
        }

        internal void SetActionState(bool enabled, string tooltip)
        {
            if (isDisposed)
                return;

            actionEnabled = enabled;
            availabilityTooltip = tooltip ?? string.Empty;
            ApplyState();
        }

        internal bool SetActionState(
            PackageManagerPackageConversionTarget target,
            bool enabled,
            string tooltip)
        {
            if (!MatchesCurrentTarget(target))
                return false;

            SetActionState(enabled, tooltip);
            return true;
        }

        internal void TriggerConversion()
        {
            OnConvertClicked();
        }

        internal void CancelConfirmation()
        {
            if (isDisposed || state != ConversionUiState.Confirming)
                return;

            ResetState();
        }

        internal void ShowConfirmation()
        {
            ShowConfirmation(null);
        }

        internal bool ShowConfirmation(
            SubmoduleRemovalAssessment assessment)
        {
            if (isDisposed ||
                currentTarget == null ||
                !actionEnabled ||
                state == ConversionUiState.Converting ||
                state == ConversionUiState.Completed)
            {
                return false;
            }

            if (assessment != null &&
                currentTarget.Direction !=
                GitPackageConversionDirection.SubmoduleToReadOnly)
            {
                return false;
            }

            if (assessment?.HasUnverifiedWorktreeContents == true)
            {
                ShowError(
                    currentTarget,
                    "The package directory contains files but is not an " +
                    "initialized submodule worktree. Move those files to safety " +
                    "and leave the directory empty before converting. Git " +
                    "Submodule Manager will not discard unverified files.");
                return false;
            }

            confirmedAssessment = assessment?.CreateSnapshot();
            discardLocalWork = assessment != null && !assessment.IsSafe;
            state = ConversionUiState.Confirming;
            confirmationIdentity = currentIdentity;
            ShowFeedback(
                BuildConfirmationMessage(currentTarget, assessment),
                HelpBoxMessageType.Warning);
            ApplyState();
            return true;
        }

        internal bool ShowInspecting(
            PackageManagerPackageConversionTarget target,
            string message)
        {
            if (!MatchesCurrentTarget(target))
                return false;

            state = ConversionUiState.Inspecting;
            ShowFeedback(
                string.IsNullOrWhiteSpace(message)
                    ? L10n.Tr("Inspecting the Git submodule for local work...")
                    : message,
                HelpBoxMessageType.Info);
            ApplyState();
            return true;
        }

        internal bool ShowProgress(
            PackageManagerPackageConversionTarget target,
            string message)
        {
            if (!MatchesCurrentTarget(target))
                return false;

            SetProgress(message);
            return true;
        }

        internal bool ShowError(
            PackageManagerPackageConversionTarget target,
            string message)
        {
            if (!MatchesCurrentTarget(target))
                return false;

            state = ConversionUiState.Error;
            confirmationIdentity = string.Empty;
            string safeMessage = GitHubUtility.SanitizeUiDiagnostic(message);
            ShowFeedback(
                string.IsNullOrWhiteSpace(safeMessage)
                    ? L10n.Tr("The package could not be converted safely.")
                    : safeMessage,
                HelpBoxMessageType.Error);
            ApplyState();
            return true;
        }

        internal bool ShowCompleted(
            PackageManagerPackageConversionTarget target,
            string message)
        {
            if (!MatchesCurrentTarget(target))
                return false;

            state = ConversionUiState.Completed;
            confirmationIdentity = string.Empty;
            ShowFeedback(
                string.IsNullOrWhiteSpace(message)
                    ? L10n.Tr(
                        "Package converted. Unity is refreshing packages...")
                    : message,
                HelpBoxMessageType.Info);
            ApplyState();
            return true;
        }

        internal void EnsureControlsMounted()
        {
            EnsureMounted();
        }

        public void Dispose()
        {
            if (isDisposed)
                return;

            isDisposed = true;
            controls.RemoveFromHierarchy();
            feedback.RemoveFromHierarchy();
            ownedFeedbackContainer?.RemoveFromHierarchy();
            currentTarget = null;
            confirmedAssessment = null;
            currentIdentity = string.Empty;
            confirmationIdentity = string.Empty;
        }

        internal static string BuildConfirmationMessage(
            PackageManagerPackageConversionTarget target)
        {
            return BuildConfirmationMessage(target, null);
        }

        internal static string BuildConfirmationMessage(
            PackageManagerPackageConversionTarget target,
            SubmoduleRemovalAssessment assessment)
        {
            string packageName = string.IsNullOrWhiteSpace(target?.PackageName)
                ? "this package"
                : target.PackageName;
            if (target?.Direction ==
                GitPackageConversionDirection.ReadOnlyToSubmodule)
            {
                string path = string.IsNullOrWhiteSpace(target.PackagePath)
                    ? "Packages/" + packageName
                    : target.PackagePath;
                return $"Convert {packageName} from a read-only Package Manager " +
                       $"Git dependency to an editable Git submodule at {path}? " +
                       "The manifest dependency is only removed after the " +
                       "submodule checkout and package identity are verified.";
            }

            if (assessment != null && !assessment.IsSafe)
            {
                return assessment.BuildWarning() + " " +
                       $"Convert {packageName} anyway? The read-only dependency " +
                       "pins the current committed HEAD; modified, untracked, " +
                       "ignored, conflicted, and parent-gitlink changes are not " +
                       "included and will be discarded. This cannot be undone " +
                       "from the Unity UI.";
            }

            return $"Convert {packageName} from an editable Git submodule to a " +
                   "read-only Package Manager Git dependency? The dependency is " +
                   "recorded before the verified submodule worktree is removed.";
        }

        private void OnConvertClicked()
        {
            if (isDisposed ||
                currentTarget == null ||
                !actionEnabled ||
                state == ConversionUiState.Converting ||
                state == ConversionUiState.Completed)
            {
                return;
            }

            if (state != ConversionUiState.Confirming ||
                !string.Equals(
                    confirmationIdentity,
                    currentIdentity,
                    StringComparison.Ordinal))
            {
                ShowConfirmation();
                return;
            }

            PackageManagerPackageConversionTarget target = currentTarget;
            SetProgress(BuildProgressMessage(target));
            conversionRequested(target);
        }

        private void SetProgress(string message)
        {
            state = ConversionUiState.Converting;
            confirmationIdentity = string.Empty;
            ShowFeedback(
                string.IsNullOrWhiteSpace(message)
                    ? L10n.Tr("Converting package...")
                    : message,
                HelpBoxMessageType.Info);
            ApplyState();
        }

        private static string BuildProgressMessage(
            PackageManagerPackageConversionTarget target)
        {
            string packageName = target?.PackageName ?? string.Empty;
            return target?.Direction ==
                   GitPackageConversionDirection.ReadOnlyToSubmodule
                ? $"Converting {packageName} to an editable Git submodule..."
                : $"Converting {packageName} to a read-only Package Manager Git dependency...";
        }

        private void ResetState()
        {
            state = ConversionUiState.Idle;
            confirmationIdentity = string.Empty;
            confirmedAssessment = null;
            discardLocalWork = false;
            HideFeedback();
            ApplyState();
        }

        private void ApplyState()
        {
            if (isDisposed)
                return;

            bool isReadOnlyToSubmodule = currentTarget?.Direction ==
                                         GitPackageConversionDirection
                                             .ReadOnlyToSubmodule;
            controls.style.display = hasTarget &&
                                     (isReadOnlyToSubmodule ||
                                      state == ConversionUiState.Confirming)
                ? DisplayStyle.Flex
                : DisplayStyle.None;

            cancelButton.style.display = state == ConversionUiState.Confirming
                ? DisplayStyle.Flex
                : DisplayStyle.None;
            cancelButton.SetEnabled(state == ConversionUiState.Confirming);

            switch (state)
            {
                case ConversionUiState.Confirming:
                    convertButton.text = L10n.Tr(
                        discardLocalWork
                            ? ConfirmDiscardText
                            : ConfirmConversionText);
                    convertButton.tooltip = feedback.text;
                    convertButton.SetEnabled(actionEnabled);
                    break;
                case ConversionUiState.Converting:
                    convertButton.text = L10n.Tr(ConvertingText);
                    convertButton.tooltip = feedback.text;
                    convertButton.SetEnabled(false);
                    break;
                case ConversionUiState.Inspecting:
                    convertButton.text = L10n.Tr(InspectingText);
                    convertButton.tooltip = feedback.text;
                    convertButton.SetEnabled(false);
                    break;
                case ConversionUiState.Error:
                    convertButton.text = L10n.Tr(RetryConversionText);
                    convertButton.tooltip = feedback.text;
                    convertButton.SetEnabled(actionEnabled);
                    break;
                case ConversionUiState.Completed:
                    convertButton.text = L10n.Tr(ConvertedText);
                    convertButton.tooltip = feedback.text;
                    convertButton.SetEnabled(false);
                    break;
                default:
                    convertButton.text = L10n.Tr(GetActionText(currentTarget));
                    convertButton.tooltip = availabilityTooltip;
                    convertButton.SetEnabled(actionEnabled && currentTarget != null);
                    break;
            }
        }

        private static string GetActionText(
            PackageManagerPackageConversionTarget target)
        {
            return target?.Direction ==
                   GitPackageConversionDirection.ReadOnlyToSubmodule
                ? ConvertToSubmoduleText
                : ConvertToReadOnlyText;
        }

        private bool MatchesCurrentTarget(
            PackageManagerPackageConversionTarget target)
        {
            return !isDisposed &&
                   target != null &&
                   currentTarget != null &&
                   string.Equals(
                       target.SelectionIdentity,
                       currentIdentity,
                       StringComparison.Ordinal);
        }

        private void EnsureMounted()
        {
            if (isDisposed)
                return;

            if (!ReferenceEquals(controls.parent, primaryActionsContainer))
            {
                controls.RemoveFromHierarchy();
                primaryActionsContainer.Insert(0, controls);
            }

            VisualElement target = detailsLinksContainer.parent?.Q<VisualElement>(
                PackageManagerGitHubDetails.NativeHelpBoxContainerName);
            if (target == null)
            {
                VisualElement parent = detailsLinksContainer.parent ??
                                       detailsLinksContainer;
                if (ownedFeedbackContainer == null)
                {
                    ownedFeedbackContainer = new VisualElement
                    {
                        name = OwnedFeedbackContainerName
                    };
                }

                if (!ReferenceEquals(ownedFeedbackContainer.parent, parent))
                {
                    ownedFeedbackContainer.RemoveFromHierarchy();
                    parent.Add(ownedFeedbackContainer);
                }

                target = ownedFeedbackContainer;
            }

            if (!ReferenceEquals(feedback.parent, target))
            {
                feedback.RemoveFromHierarchy();
                target.Add(feedback);
            }
        }

        private void SetVisible(bool visible)
        {
            hasTarget = visible;
            ApplyState();
            if (!visible)
                HideFeedback();
        }

        private void ShowFeedback(string message, HelpBoxMessageType type)
        {
            EnsureMounted();
            feedback.text = message ?? string.Empty;
            feedback.tooltip = feedback.text;
            feedback.messageType = type;
            feedback.style.display = DisplayStyle.Flex;
        }

        private void HideFeedback()
        {
            feedback.text = string.Empty;
            feedback.tooltip = string.Empty;
            feedback.style.display = DisplayStyle.None;
        }

        private enum ConversionUiState
        {
            Idle,
            Inspecting,
            Confirming,
            Converting,
            Error,
            Completed
        }
    }
}
