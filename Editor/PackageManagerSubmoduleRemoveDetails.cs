using System;
using UnityEditor;
using UnityEngine.UIElements;

namespace MartinCalander.GitSubmoduleManager.Editor
{
    /// <summary>
    /// Native Package Manager controls for a verified installed submodule.
    /// The native action coordinator owns modal confirmation. This view stores
    /// the exact assessed state passed to the removal service and keeps progress
    /// and diagnostics selection-bound inside Package Manager.
    /// </summary>
    internal sealed class PackageManagerSubmoduleRemoveDetails : IDisposable
    {
        internal const string ControlsElementName =
            "git-submodule-manager-remove-primary-actions";
        internal const string RemoveActionElementName =
            "git-submodule-manager-remove-action";
        internal const string CancelActionElementName =
            "git-submodule-manager-cancel-remove-action";
        internal const string FeedbackElementName =
            "git-submodule-manager-remove-feedback";
        internal const string RemoveText = "Uninstall Submodule";
        internal const string ConfirmRemoveText = "Confirm Uninstall";
        internal const string ConfirmDiscardText =
            "Discard Changes and Uninstall";
        internal const string InspectingText = "Inspecting...";
        internal const string RemovingText = "Removing...";
        internal const string RetryRemoveText = "Retry Remove";

        private const string OwnedFeedbackContainerName =
            "git-submodule-manager-remove-feedback-container";

        private readonly VisualElement primaryActionsContainer;
        private readonly VisualElement detailsLinksContainer;
        private readonly VisualElement controls;
        private readonly Button removeButton;
        private readonly Button cancelButton;
        private readonly HelpBox feedback;
        private readonly Action<PackageManagerSubmoduleInfo> removeRequested;

        private VisualElement ownedFeedbackContainer;
        private PackageManagerSubmoduleInfo currentInfo;
        private SubmoduleRemovalAssessment confirmedAssessment;
        private string currentIdentity = string.Empty;
        private string availabilityTooltip = string.Empty;
        private bool actionEnabled;
        private bool discardLocalWork;
        private bool hasSelection;
        private bool isDisposed;
        private RemoveUiState state;

        private PackageManagerSubmoduleRemoveDetails(
            VisualElement primaryActionsContainer,
            VisualElement detailsLinksContainer,
            Action<PackageManagerSubmoduleInfo> removeRequested)
        {
            this.primaryActionsContainer = primaryActionsContainer;
            this.detailsLinksContainer = detailsLinksContainer;
            this.removeRequested = removeRequested;

            controls = new VisualElement { name = ControlsElementName };
            controls.style.flexDirection = FlexDirection.Row;
            controls.style.alignItems = Align.Center;

            removeButton = new Button(OnRemoveClicked)
            {
                name = RemoveActionElementName,
                text = L10n.Tr(RemoveText)
            };
            controls.Add(removeButton);

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

        internal Button RemoveButton => removeButton;
        internal Button CancelButton => cancelButton;
        internal HelpBox Feedback => feedback;
        internal bool IsConfirmationPending => state == RemoveUiState.Confirming;
        internal bool IsInspecting => state == RemoveUiState.Inspecting;
        internal bool IsRemoving => state == RemoveUiState.Removing;
        internal PackageManagerSubmoduleInfo CurrentInfo => currentInfo;
        internal SubmoduleRemovalAssessment ConfirmedAssessment =>
            confirmedAssessment;
        internal bool DiscardLocalWork => discardLocalWork;
        internal bool IsActionEnabled => actionEnabled;
        internal string AvailabilityTooltip => availabilityTooltip;

        internal static bool TryCreate(
            VisualElement primaryActionsContainer,
            VisualElement detailsLinksContainer,
            Action<PackageManagerSubmoduleInfo> removeRequested,
            out PackageManagerSubmoduleRemoveDetails details)
        {
            details = null;
            if (primaryActionsContainer == null ||
                detailsLinksContainer == null ||
                removeRequested == null ||
                !string.Equals(
                    detailsLinksContainer.name,
                    PackageManagerGitHubDetails.NativeDetailsLinksContainerName,
                    StringComparison.Ordinal))
            {
                return false;
            }

            try
            {
                details = new PackageManagerSubmoduleRemoveDetails(
                    primaryActionsContainer,
                    detailsLinksContainer,
                    removeRequested);
                return true;
            }
            catch
            {
                details?.Dispose();
                details = null;
                return false;
            }
        }

        internal void Refresh(PackageManagerSubmoduleInfo info)
        {
            if (isDisposed)
                return;

            EnsureMounted();
            if (info == null)
            {
                currentInfo = null;
                currentIdentity = string.Empty;
                ResetState();
                SetVisible(false);
                return;
            }

            string identity = BuildIdentity(info);
            if (!string.Equals(identity, currentIdentity, StringComparison.Ordinal))
                ResetState();

            currentInfo = info;
            currentIdentity = identity;
            SetVisible(true);
            ApplyState();
        }

        internal void SetRemoveState(bool enabled, string tooltip)
        {
            if (isDisposed)
                return;

            actionEnabled = enabled;
            availabilityTooltip = tooltip ?? string.Empty;
            ApplyState();
        }

        internal void TriggerRemove()
        {
            OnRemoveClicked();
        }

        internal bool TriggerAssessedRemoval(
            SubmoduleRemovalAssessment assessment,
            bool discardAssessedLocalWork)
        {
            if (isDisposed ||
                currentInfo == null ||
                !actionEnabled ||
                state != RemoveUiState.Inspecting ||
                assessment == null ||
                !string.Equals(
                    GitUtility.NormalizePath(assessment.Path),
                    GitUtility.NormalizePath(currentInfo.PackagePath),
                    StringComparison.Ordinal) ||
                assessment.HasUnverifiedWorktreeContents ||
                discardAssessedLocalWork !=
                PackageManagerSubmoduleConfirmationPolicy
                    .RequiresDiscardConfirmation(assessment))
            {
                return false;
            }

            confirmedAssessment = assessment.CreateSnapshot();
            discardLocalWork = discardAssessedLocalWork;
            PackageManagerSubmoduleInfo info = currentInfo;
            ShowRemoving(
                $"Removing {info.PackageName} through Git and refreshing Unity...");
            removeRequested(info);
            return true;
        }

        internal void CancelInspection()
        {
            if (isDisposed || state != RemoveUiState.Inspecting)
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
            if (isDisposed || currentInfo == null || !actionEnabled)
                return false;

            if (assessment?.HasUnverifiedWorktreeContents == true)
            {
                ShowError(
                    "The package directory contains files but is not an " +
                    "initialized submodule worktree. Move those files to safety " +
                    "and leave the directory empty before uninstalling. Git " +
                    "Submodule Manager will not discard unverified files.");
                return false;
            }

            confirmedAssessment = assessment?.CreateSnapshot();
            discardLocalWork = assessment != null && !assessment.IsSafe;
            state = RemoveUiState.Confirming;
            ShowFeedback(
                BuildConfirmationMessage(currentInfo, assessment),
                HelpBoxMessageType.Warning);
            ApplyState();
            return true;
        }

        internal void ShowInspecting(string message)
        {
            if (isDisposed || currentInfo == null)
                return;

            confirmedAssessment = null;
            discardLocalWork = false;
            state = RemoveUiState.Inspecting;
            ShowFeedback(
                string.IsNullOrWhiteSpace(message)
                    ? L10n.Tr("Inspecting the Git submodule for local work...")
                    : message,
                HelpBoxMessageType.Info);
            ApplyState();
        }

        internal void EnsureControlsMounted()
        {
            EnsureMounted();
        }

        internal void ShowRemoving(string message)
        {
            if (isDisposed)
                return;

            state = RemoveUiState.Removing;
            ShowFeedback(
                string.IsNullOrWhiteSpace(message)
                    ? L10n.Tr("Removing Git submodule...")
                    : message,
                HelpBoxMessageType.Info);
            ApplyState();
        }

        internal void ShowError(string message)
        {
            if (isDisposed)
                return;

            state = RemoveUiState.Error;
            string safeMessage = GitHubUtility.SanitizeUiDiagnostic(message);
            ShowFeedback(
                string.IsNullOrWhiteSpace(safeMessage)
                    ? L10n.Tr("The Git submodule could not be removed safely.")
                    : safeMessage,
                HelpBoxMessageType.Error);
            ApplyState();
        }

        internal void ShowCompleted(string message)
        {
            if (isDisposed)
                return;

            state = RemoveUiState.Completed;
            ShowFeedback(
                string.IsNullOrWhiteSpace(message)
                    ? L10n.Tr("Git submodule removed. Unity is refreshing packages...")
                    : message,
                HelpBoxMessageType.Info);
            ApplyState();
        }

        public void Dispose()
        {
            if (isDisposed)
                return;

            isDisposed = true;
            controls.RemoveFromHierarchy();
            feedback.RemoveFromHierarchy();
            ownedFeedbackContainer?.RemoveFromHierarchy();
            currentInfo = null;
            confirmedAssessment = null;
        }

        private void OnRemoveClicked()
        {
            if (isDisposed || currentInfo == null || !actionEnabled)
                return;

            if (state != RemoveUiState.Confirming)
            {
                ShowConfirmation();
                return;
            }

            PackageManagerSubmoduleInfo info = currentInfo;
            ShowRemoving(
                $"Removing {info.PackageName} through Git and refreshing Unity...");
            removeRequested(info);
        }

        private void CancelConfirmation()
        {
            if (isDisposed || state != RemoveUiState.Confirming)
                return;

            ResetState();
        }

        private void ResetState()
        {
            state = RemoveUiState.Idle;
            confirmedAssessment = null;
            discardLocalWork = false;
            HideFeedback();
            ApplyState();
        }

        private void ApplyState()
        {
            if (isDisposed)
                return;

            // The destructive entry point lives in Unity's native Manage menu.
            // Only the explicit confirmation controls are mounted in the primary
            // action row, so idle/error/progress states never reintroduce a
            // standalone Uninstall button.
            controls.style.display = hasSelection &&
                                     state == RemoveUiState.Confirming
                ? DisplayStyle.Flex
                : DisplayStyle.None;

            cancelButton.style.display = state == RemoveUiState.Confirming
                ? DisplayStyle.Flex
                : DisplayStyle.None;
            cancelButton.SetEnabled(state == RemoveUiState.Confirming);

            switch (state)
            {
                case RemoveUiState.Confirming:
                    removeButton.text = L10n.Tr(
                        discardLocalWork
                            ? ConfirmDiscardText
                            : ConfirmRemoveText);
                    removeButton.tooltip = feedback.text;
                    removeButton.SetEnabled(actionEnabled);
                    break;
                case RemoveUiState.Removing:
                    removeButton.text = L10n.Tr(RemovingText);
                    removeButton.tooltip = feedback.text;
                    removeButton.SetEnabled(false);
                    break;
                case RemoveUiState.Inspecting:
                    removeButton.text = L10n.Tr(InspectingText);
                    removeButton.tooltip = feedback.text;
                    removeButton.SetEnabled(false);
                    break;
                case RemoveUiState.Error:
                    removeButton.text = L10n.Tr(RetryRemoveText);
                    removeButton.tooltip = feedback.text;
                    removeButton.SetEnabled(actionEnabled);
                    break;
                case RemoveUiState.Completed:
                    removeButton.text = L10n.Tr("Removed");
                    removeButton.tooltip = feedback.text;
                    removeButton.SetEnabled(false);
                    break;
                default:
                    removeButton.text = L10n.Tr(RemoveText);
                    removeButton.tooltip = availabilityTooltip;
                    removeButton.SetEnabled(actionEnabled);
                    break;
            }
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
            hasSelection = visible;
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

        internal static string BuildConfirmationMessage(
            PackageManagerSubmoduleInfo info)
        {
            return BuildConfirmationMessage(info, null);
        }

        internal static string BuildConfirmationMessage(
            PackageManagerSubmoduleInfo info,
            SubmoduleRemovalAssessment assessment)
        {
            string path = info?.PackagePath ?? string.Empty;
            string packageName = info?.PackageName ?? "this package";
            if (assessment != null && !assessment.IsSafe)
            {
                return assessment.BuildWarning() + " " +
                       $"Uninstall {packageName} at {path} anyway? Git will " +
                       "remove the package worktree and parent gitlink changes. " +
                       "This cannot be undone from the Unity UI.";
            }

            return $"Uninstall {packageName} at {path} as a Git submodule? " +
                   "Git will remove the tracked registration and worktree after " +
                   "confirming their state has not changed.";
        }

        private static string BuildIdentity(PackageManagerSubmoduleInfo info)
        {
            return (info?.PackageName?.Trim() ?? string.Empty) + "\n" +
                   GitUtility.NormalizePath(info?.PackagePath ?? string.Empty);
        }

        private enum RemoveUiState
        {
            Idle,
            Inspecting,
            Confirming,
            Removing,
            Error,
            Completed
        }
    }
}
