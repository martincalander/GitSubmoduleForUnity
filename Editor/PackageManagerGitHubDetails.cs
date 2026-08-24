using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace MartinCalander.GitSubmoduleManager.Editor
{
    internal enum PackageManagerGitInstallMode
    {
        GitSubmodule,
        ReadOnlyPackage
    }

    /// <summary>
    /// Owns the small amount of UI added to a native Package Manager details view
    /// for a discovered GitHub repository. The controller never retains Unity's
    /// package placeholder; every toolbar refresh supplies an immutable repository
    /// record so recycled Package Manager views cannot invoke an action for an old
    /// selection.
    /// </summary>
    internal sealed class PackageManagerGitHubDetails : IDisposable
    {
        internal const string NativeDetailsLinksContainerName = "detailLinksContainer";
        internal const string NativeExtensionActionsContainerName = "extensionItems";
        internal const string ControlsElementName =
            "git-submodule-manager-github-primary-actions";
        internal const string BranchFieldElementName =
            "git-submodule-manager-github-branch";
        internal const string InstallActionElementName =
            "git-submodule-manager-install-action";
        internal const string InstallStateActionElementName =
            "git-submodule-manager-install-state-action";
        internal const string CancelInstallActionElementName =
            "git-submodule-manager-cancel-install-action";
        internal const string InstallFeedbackElementName =
            "git-submodule-manager-install-feedback";
        internal const string RepositoryLinkElementName =
            "git-submodule-manager-repository-link";
        internal const string InstallText = "Install";
        internal const string ConfirmInstallText = "Confirm Install";
        internal const string RetryInstallText = "Retry Install";
        internal const string InstallingText = "Installing...";
        internal const string InstalledText = "Installed";
        internal const string RepositoryLinkText = "Repository";
        internal const string InstallAsGitSubmoduleText =
            "Install as Git Submodule";
        internal const string InstallAsReadOnlyPackageText =
            "Install as Read-Only Package";
        internal const string PreferredBranch = "main";
        private const int DeferredFocusAttemptLimit = 10;

        private const string UpmLinksContainerName = "upmLinksContainer";
        private const string AssetStoreLinksContainerName = "assetStoreLinksContainer";
        internal const string NativeHelpBoxContainerName = "helpBoxContainer";
        private const string OwnedLinksContainerName =
            "git-submodule-manager-repository-links";
        private const string OwnedFeedbackContainerName =
            "git-submodule-manager-install-feedback-container";

        private readonly VisualElement primaryActionsContainer;
        private readonly VisualElement detailsLinksContainer;
        private readonly VisualElement controls;
        private readonly DropdownField branchField;
        private readonly ToolbarMenu installMenu;
        private readonly Button installButton;
        private readonly Button cancelInstallButton;
        private readonly HelpBox installFeedback;
        private readonly Label repositoryLinkSeparator;
        private readonly Button repositoryLinkButton;
        private readonly Action<PackageManagerGitHubRepository, string,
            PackageManagerGitInstallMode> installRequested;
        private readonly Action<string> openUrl;
        private readonly RepositoryCoordinator repositoryCoordinator;
        private readonly bool branchDiscoveryEnabled;
        private readonly bool installModeSelectionEnabled;

        private VisualElement ownedLinksContainer;
        private VisualElement ownedFeedbackContainer;
        private VisualElement repositoryLinkTarget;
        private StyleEnum<DisplayStyle> detailsLinksDisplayBeforeMount;
        private bool forcedDetailsLinksDisplay;
        private PackageManagerGitHubRepository currentRepository;
        private string currentRepositoryIdentity = string.Empty;
        private string selectedBranch = string.Empty;
        private string observedDefaultBranch = string.Empty;
        private string confirmationSelectionIdentity = string.Empty;
        private string installAvailabilityTooltip = string.Empty;
        private string gitSubmoduleInstallTooltip = string.Empty;
        private string readOnlyPackageInstallTooltip = string.Empty;
        private PackageManagerGitInstallMode selectedInstallMode =
            PackageManagerGitInstallMode.GitSubmodule;
        private bool userSelectedBranch;
        private bool installActionEnabled;
        private bool gitSubmoduleInstallEnabled;
        private bool readOnlyPackageInstallEnabled;
        private bool installControlsVisible;
        private bool branchUpdateSubscribed;
        private bool isDisposed;
        private InstallUiState installUiState;
        private VisualElement deferredFocusTarget;
        private InstallUiState deferredFocusExpectedState;
        private int deferredFocusAttemptCount;
        private bool deferredFocusQueued;

        private PackageManagerGitHubDetails(
            VisualElement primaryActionsContainer,
            VisualElement detailsLinksContainer,
            Action<PackageManagerGitHubRepository, string,
                PackageManagerGitInstallMode> installRequested,
            Action<string> openUrl,
            bool enableBranchDiscovery,
            bool enableInstallModeSelection)
        {
            this.primaryActionsContainer = primaryActionsContainer;
            this.detailsLinksContainer = detailsLinksContainer;
            this.installRequested = installRequested;
            this.openUrl = openUrl;
            branchDiscoveryEnabled = enableBranchDiscovery;
            installModeSelectionEnabled = enableInstallModeSelection;
            repositoryCoordinator = enableBranchDiscovery
                ? new RepositoryCoordinator()
                : null;

            controls = new VisualElement
            {
                name = ControlsElementName
            };
            controls.style.flexDirection = FlexDirection.Row;
            controls.style.alignItems = Align.Center;
            controls.style.flexGrow = 1f;
            controls.style.flexShrink = 1f;

            var branchLabel = new Label(L10n.Tr("Branch"));
            branchLabel.style.marginRight = 4f;
            controls.Add(branchLabel);

            branchField = new DropdownField
            {
                name = BranchFieldElementName
            };
            branchField.style.minWidth = 90f;
            branchField.style.maxWidth = 180f;
            branchField.style.flexShrink = 1f;
            branchField.style.marginRight = 4f;
            branchField.RegisterValueChangedCallback(OnBranchChanged);
            controls.Add(branchField);

            installMenu = new ToolbarMenu
            {
                name = InstallActionElementName,
                text = L10n.Tr(InstallText),
                variant = ToolbarMenu.Variant.Popup,
                focusable = true,
                tabIndex = 0
            };
            installMenu.style.flexShrink = 0f;
            installMenu.menu.AppendAction(
                GetInstallMenuActionText(
                    PackageManagerGitInstallMode.GitSubmodule),
                _ => ChooseInstallMode(
                    PackageManagerGitInstallMode.GitSubmodule),
                _ => GetInstallMenuActionStatus(
                    PackageManagerGitInstallMode.GitSubmodule));
            installMenu.menu.AppendAction(
                GetInstallMenuActionText(
                    PackageManagerGitInstallMode.ReadOnlyPackage),
                _ => ChooseInstallMode(
                    PackageManagerGitInstallMode.ReadOnlyPackage),
                _ => GetInstallMenuActionStatus(
                    PackageManagerGitInstallMode.ReadOnlyPackage));
            installMenu.RegisterCallback<NavigationSubmitEvent>(
                OnInstallMenuNavigationSubmit);
            controls.Add(installMenu);

            installButton = new Button(OnInstallClicked)
            {
                name = InstallStateActionElementName,
                text = InstallText
            };
            installButton.style.display = DisplayStyle.None;
            controls.Add(installButton);

            cancelInstallButton = new Button(CancelInstallConfirmation)
            {
                name = CancelInstallActionElementName,
                text = L10n.Tr("Cancel")
            };
            cancelInstallButton.style.marginLeft = 4f;
            cancelInstallButton.style.display = DisplayStyle.None;
            controls.Add(cancelInstallButton);

            installFeedback = new HelpBox(
                string.Empty,
                HelpBoxMessageType.Info)
            {
                name = InstallFeedbackElementName
            };
            Label installFeedbackLabel = installFeedback.Q<Label>(
                className: HelpBox.labelUssClassName);
            if (installFeedbackLabel != null)
                installFeedbackLabel.enableRichText = false;
            installFeedback.style.display = DisplayStyle.None;

            repositoryLinkSeparator = new Label("|");
            repositoryLinkSeparator.AddToClassList("separator");
            repositoryLinkButton = new Button(() => OpenRepositoryWebsite())
            {
                name = RepositoryLinkElementName,
                text = L10n.Tr(RepositoryLinkText)
            };
            repositoryLinkButton.AddToClassList("link");

            EnsurePrimaryControlsMounted();
            EnsureInstallFeedbackMounted();
            SetVisible(false);
        }

        internal VisualElement Controls => controls;
        internal DropdownField BranchField => branchField;
        internal ToolbarMenu InstallMenu => installMenu;
        internal Button InstallButton => installButton;
        internal Button CancelInstallButton => cancelInstallButton;
        internal HelpBox InstallFeedback => installFeedback;
        internal Button RepositoryLinkButton => repositoryLinkButton;
        internal PackageManagerGitHubRepository CurrentRepository => currentRepository;
        internal string SelectedBranch => selectedBranch;
        internal PackageManagerGitInstallMode SelectedInstallMode =>
            selectedInstallMode;
        internal bool IsInstallConfirmationPending =>
            installUiState == InstallUiState.Confirming;
        internal bool IsInstalling => installUiState == InstallUiState.Installing;
        internal bool IsInstallCompleted =>
            installUiState == InstallUiState.Completed;
        internal bool IsDisposed => isDisposed;
        internal bool HasDeferredFocusRequest =>
            deferredFocusTarget != null || deferredFocusQueued;
        internal event Action InstallSelectionChanged;

        internal static bool TryCreate(
            VisualElement primaryActionsContainer,
            VisualElement detailsLinksContainer,
            Action<PackageManagerGitHubRepository, string> installRequested,
            Action<string> openUrl,
            bool enableBranchDiscovery,
            out PackageManagerGitHubDetails details)
        {
            if (installRequested == null)
            {
                details = null;
                return false;
            }

            return TryCreate(
                primaryActionsContainer,
                detailsLinksContainer,
                (repository, branch, _) => installRequested(repository, branch),
                openUrl,
                enableBranchDiscovery,
                false,
                out details);
        }

        internal static bool TryCreate(
            VisualElement primaryActionsContainer,
            VisualElement detailsLinksContainer,
            Action<PackageManagerGitHubRepository, string,
                PackageManagerGitInstallMode> installRequested,
            Action<string> openUrl,
            bool enableBranchDiscovery,
            out PackageManagerGitHubDetails details)
        {
            return TryCreate(
                primaryActionsContainer,
                detailsLinksContainer,
                installRequested,
                openUrl,
                enableBranchDiscovery,
                true,
                out details);
        }

        private static bool TryCreate(
            VisualElement primaryActionsContainer,
            VisualElement detailsLinksContainer,
            Action<PackageManagerGitHubRepository, string,
                PackageManagerGitInstallMode> installRequested,
            Action<string> openUrl,
            bool enableBranchDiscovery,
            bool enableInstallModeSelection,
            out PackageManagerGitHubDetails details)
        {
            details = null;
            if (primaryActionsContainer == null ||
                detailsLinksContainer == null ||
                installRequested == null ||
                openUrl == null ||
                !string.Equals(
                    detailsLinksContainer.name,
                    NativeDetailsLinksContainerName,
                    StringComparison.Ordinal))
            {
                return false;
            }

            try
            {
                details = new PackageManagerGitHubDetails(
                    primaryActionsContainer,
                    detailsLinksContainer,
                    installRequested,
                    openUrl,
                    enableBranchDiscovery,
                    enableInstallModeSelection);
                return true;
            }
            catch
            {
                details?.Dispose();
                details = null;
                return false;
            }
        }

        internal void Refresh(PackageManagerGitHubRepository repository)
        {
            if (isDisposed)
                return;

            EnsurePrimaryControlsMounted();
            EnsureInstallFeedbackMounted();
            if (repository == null)
            {
                currentRepository = null;
                currentRepositoryIdentity = string.Empty;
                selectedBranch = string.Empty;
                observedDefaultBranch = string.Empty;
                userSelectedBranch = false;
                ResetInstallModeSelection();
                ClearInstallAvailability();
                ResetInstallUi();
                branchField.choices = new List<string>();
                branchField.SetValueWithoutNotify(string.Empty);
                repositoryCoordinator?.ClearAllBranchCaches();
                RemoveRepositoryLink();
                SetVisible(false);
                return;
            }

            string identity = GetRepositoryIdentity(repository);
            bool changedRepository = !string.Equals(
                currentRepositoryIdentity,
                identity,
                StringComparison.Ordinal);
            string nextDefaultBranch = NormalizeBranch(repository.DefaultBranch);
            bool changedDefaultBranch = !string.Equals(
                observedDefaultBranch,
                nextDefaultBranch,
                StringComparison.Ordinal);

            currentRepository = repository;
            currentRepositoryIdentity = identity;
            if (changedRepository)
            {
                selectedBranch = PreferredBranch;
                userSelectedBranch = false;
                ResetInstallModeSelection();
                ClearInstallAvailability();
                ResetInstallUi();
            }
            else if (changedDefaultBranch && !userSelectedBranch)
            {
                selectedBranch = PreferredBranch;
            }

            observedDefaultBranch = nextDefaultBranch;
            ApplyCurrentBranchChoices(repository.Url);
            if (installUiState == InstallUiState.Confirming &&
                !ConfirmationMatchesCurrentSelection())
            {
                ResetInstallUi();
            }
            MountRepositoryLink(repository);
            SetVisible(true);

            if (branchDiscoveryEnabled)
            {
                repositoryCoordinator.RequestBranches(repository.Url);
                UpdateBranchPolling();
                UpdateBranchTooltip();
            }
        }

        internal void SetInstallState(bool visible, bool enabled, string tooltip)
        {
            SetInstallState(
                visible,
                enabled,
                tooltip,
                enabled,
                tooltip);
        }

        internal void SetInstallState(
            bool visible,
            bool gitSubmoduleEnabled,
            string gitSubmoduleTooltip,
            bool readOnlyPackageEnabled,
            string readOnlyPackageTooltip)
        {
            if (isDisposed)
                return;

            gitSubmoduleInstallEnabled = visible && gitSubmoduleEnabled;
            readOnlyPackageInstallEnabled = visible && readOnlyPackageEnabled;
            gitSubmoduleInstallTooltip = gitSubmoduleTooltip ?? string.Empty;
            readOnlyPackageInstallTooltip =
                readOnlyPackageTooltip ?? string.Empty;
            UpdateSelectedInstallAvailability();
            if (!visible)
                ResetInstallUi();
            SetVisible(visible);
            ApplyInstallUiState();
        }

        internal void EnsurePrimaryControlsMounted()
        {
            if (isDisposed || ReferenceEquals(controls.parent, primaryActionsContainer))
                return;

            controls.RemoveFromHierarchy();
            primaryActionsContainer.Insert(0, controls);
        }

        internal void ShowInstalling(string message)
        {
            if (isDisposed)
                return;

            installUiState = InstallUiState.Installing;
            confirmationSelectionIdentity = string.Empty;
            ShowInstallFeedback(
                string.IsNullOrWhiteSpace(message)
                    ? BuildInstallingMessage(
                        currentRepository,
                        selectedBranch,
                        selectedInstallMode)
                    : message,
                HelpBoxMessageType.Info);
            ApplyInstallUiState();
        }

        internal void ShowInstallError(string message)
        {
            if (isDisposed)
                return;

            installUiState = InstallUiState.Error;
            confirmationSelectionIdentity = string.Empty;
            string safeMessage = GitHubUtility.SanitizeUiDiagnostic(message);
            ShowInstallFeedback(
                string.IsNullOrWhiteSpace(safeMessage)
                    ? L10n.Tr("The Git package could not be installed.")
                    : safeMessage,
                HelpBoxMessageType.Error);
            ApplyInstallUiState();
        }

        internal void ShowInstallCompleted(string message)
        {
            if (isDisposed)
                return;

            installUiState = InstallUiState.Completed;
            confirmationSelectionIdentity = string.Empty;
            ShowInstallFeedback(
                string.IsNullOrWhiteSpace(message)
                    ? BuildInstalledMessage(selectedInstallMode)
                    : message,
                HelpBoxMessageType.Info);
            ApplyInstallUiState();
        }

        internal void ApplyAvailableBranchesForTests(IEnumerable<string> branches)
        {
            ApplyAvailableBranches(branches);
        }

        internal void SelectInstallModeForTests(
            PackageManagerGitInstallMode installMode)
        {
            if (!installModeSelectionEnabled)
                return;

            ApplyInstallModeSelection(installMode);
        }

        internal void RestoreInstallMode(
            PackageManagerGitInstallMode installMode)
        {
            if (isDisposed || !installModeSelectionEnabled)
                return;

            selectedInstallMode = installMode;
            UpdateSelectedInstallAvailability();
            UpdateBranchTooltip();
            ResetInstallUi();
        }

        internal void TriggerInstall()
        {
            OnInstallClicked();
        }

        internal bool OpenRepositoryWebsite()
        {
            if (isDisposed ||
                currentRepository == null ||
                !GitUtility.TryGetRepositoryWebUrl(
                    currentRepository.Url,
                    out string repositoryWebUrl))
            {
                return false;
            }

            try
            {
                openUrl(repositoryWebUrl);
                return true;
            }
            catch
            {
                return false;
            }
        }

        internal static List<string> BuildBranchChoices(
            string defaultBranch,
            IEnumerable<string> discoveredBranches)
        {
            List<string> discovered = discoveredBranches == null
                ? null
                : new List<string>(discoveredBranches);
            bool mainIsAvailable = discovered == null;
            if (discovered != null)
            {
                foreach (string branch in discovered)
                {
                    if (string.Equals(
                            NormalizeBranch(branch),
                            PreferredBranch,
                            StringComparison.Ordinal))
                    {
                        mainIsAvailable = true;
                        break;
                    }
                }
            }

            var choices = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            if (mainIsAvailable)
                AddValidBranchChoice(PreferredBranch, choices, seen);
            AddValidBranchChoice(defaultBranch, choices, seen);
            if (discovered != null)
            {
                foreach (string branch in discovered)
                    AddValidBranchChoice(branch, choices, seen);
            }

            return choices;
        }

        internal static string GetRepositoryIdentity(
            PackageManagerGitHubRepository repository)
        {
            if (repository == null)
                return string.Empty;
            if (!string.IsNullOrWhiteSpace(repository.NodeId))
                return "node:" + repository.NodeId.Trim();

            string locationFingerprint =
                GitUtility.GetRepositoryLocationFingerprint(repository.Url);
            return string.IsNullOrEmpty(locationFingerprint)
                ? string.Empty
                : "location-sha256:" + locationFingerprint;
        }

        internal static string GetInstallSelectionIdentity(
            PackageManagerGitHubRepository repository,
            string branch)
        {
            return GetInstallSelectionIdentity(
                repository,
                branch,
                PackageManagerGitInstallMode.GitSubmodule);
        }

        internal static string GetInstallSelectionIdentity(
            PackageManagerGitHubRepository repository,
            string branch,
            PackageManagerGitInstallMode installMode)
        {
            string repositoryIdentity = GetInstallRepositoryIdentity(repository);
            return string.IsNullOrEmpty(repositoryIdentity)
                ? string.Empty
                : repositoryIdentity + "\n" + NormalizeBranch(branch) +
                  "\ninstall-mode:" + installMode;
        }

        internal static string GetInstallRepositoryIdentity(
            PackageManagerGitHubRepository repository)
        {
            if (repository == null)
                return string.Empty;

            string repositoryIdentity = GetRepositoryIdentity(repository);
            string locationFingerprint =
                GitUtility.GetRepositoryLocationFingerprint(repository.Url);
            if (string.IsNullOrEmpty(repositoryIdentity) ||
                string.IsNullOrEmpty(locationFingerprint))
            {
                return string.Empty;
            }

            return repositoryIdentity + "\nlocation-sha256:" +
                   locationFingerprint +
                   "\n" + (repository.PackageName?.Trim() ?? string.Empty);
        }

        public void Dispose()
        {
            if (isDisposed)
                return;

            isDisposed = true;
            CancelDeferredFocus();
            UnsubscribeBranchPolling();
            branchField.UnregisterValueChangedCallback(OnBranchChanged);
            installMenu.UnregisterCallback<NavigationSubmitEvent>(
                OnInstallMenuNavigationSubmit);
            repositoryCoordinator?.Dispose();
            RemoveRepositoryLink();
            installFeedback.RemoveFromHierarchy();
            ownedFeedbackContainer?.RemoveFromHierarchy();
            controls.RemoveFromHierarchy();
            currentRepository = null;
            currentRepositoryIdentity = string.Empty;
            selectedBranch = string.Empty;
        }

        private void OnEditorUpdate()
        {
            if (isDisposed || repositoryCoordinator == null)
                return;

            bool branchStateChanged = repositoryCoordinator.TickBranchFetch();
            if (currentRepository != null && branchStateChanged)
            {
                ApplyCurrentBranchChoices(currentRepository.Url);
                UpdateBranchTooltip();
            }

            UpdateBranchPolling();
        }

        private void UpdateBranchPolling()
        {
            bool shouldSubscribe = !isDisposed &&
                                   repositoryCoordinator?.HasPendingBranchWork == true;
            if (shouldSubscribe == branchUpdateSubscribed)
                return;

            branchUpdateSubscribed = shouldSubscribe;
            if (shouldSubscribe)
                EditorApplication.update += OnEditorUpdate;
            else
                EditorApplication.update -= OnEditorUpdate;
        }

        private void UnsubscribeBranchPolling()
        {
            if (!branchUpdateSubscribed)
                return;

            branchUpdateSubscribed = false;
            EditorApplication.update -= OnEditorUpdate;
        }

        private void ApplyCurrentBranchChoices(string repositoryUrl)
        {
            IEnumerable<string> branches = GetCachedBranches(repositoryUrl);
            if (branches != null)
            {
                ApplyAvailableBranches(branches);
                return;
            }

            ApplyAvailableBranches(
                repositoryCoordinator != null &&
                repositoryCoordinator.TryGetBranchError(repositoryUrl, out _)
                    ? Array.Empty<string>()
                    : null);
        }

        private IEnumerable<string> GetCachedBranches(string repositoryUrl)
        {
            return repositoryCoordinator != null &&
                   repositoryCoordinator.TryGetCachedBranches(
                       repositoryUrl,
                       out List<string> branches)
                ? branches
                : null;
        }

        private void ApplyAvailableBranches(IEnumerable<string> discoveredBranches)
        {
            List<string> choices = BuildBranchChoices(
                currentRepository?.DefaultBranch,
                discoveredBranches);
            string nextSelection = NormalizeBranch(selectedBranch);
            if (!choices.Contains(nextSelection))
            {
                string normalizedDefault = NormalizeBranch(
                    currentRepository?.DefaultBranch);
                if (choices.Contains(PreferredBranch))
                    nextSelection = PreferredBranch;
                else if (choices.Contains(normalizedDefault))
                    nextSelection = normalizedDefault;
                else
                    nextSelection = choices.Count > 0 ? choices[0] : string.Empty;
                userSelectedBranch = false;
            }

            selectedBranch = nextSelection;
            branchField.choices = choices;
            branchField.SetValueWithoutNotify(selectedBranch);
            if (installUiState == InstallUiState.Confirming &&
                !ConfirmationMatchesCurrentSelection())
            {
                ResetInstallUi();
            }
            else
            {
                ApplyInstallUiState();
            }
        }

        private void UpdateBranchTooltip()
        {
            if (currentRepository == null || repositoryCoordinator == null)
            {
                branchField.tooltip = string.Empty;
                return;
            }

            if (repositoryCoordinator.IsFetchingBranches(currentRepository.Url))
            {
                branchField.tooltip = L10n.Tr("Loading remote branches with Git...");
            }
            else if (repositoryCoordinator.TryGetBranchError(
                         currentRepository.Url,
                         out string error))
            {
                branchField.tooltip = string.IsNullOrWhiteSpace(error)
                    ? L10n.Tr("Remote branches could not be loaded.")
                    : error;
            }
            else
            {
                branchField.tooltip = selectedInstallMode ==
                                      PackageManagerGitInstallMode.GitSubmodule
                    ? L10n.Tr(
                        "Select the branch that will be checked out by the Git submodule.")
                    : L10n.Tr(
                        "Select the branch that Unity Package Manager will resolve for the read-only package.");
            }
        }

        private void OnBranchChanged(ChangeEvent<string> changeEvent)
        {
            string value = NormalizeBranch(changeEvent?.newValue);
            if (string.IsNullOrEmpty(value) ||
                !branchField.choices.Contains(value))
            {
                branchField.SetValueWithoutNotify(selectedBranch);
                return;
            }

            selectedBranch = value;
            userSelectedBranch = true;
            ResetInstallUi();
            InstallSelectionChanged?.Invoke();
        }

        private void OnInstallMenuNavigationSubmit(
            NavigationSubmitEvent submitEvent)
        {
            if (!CanChooseInstallMode())
                return;

            installMenu.ShowMenu();
            submitEvent.StopPropagation();
        }

        private DropdownMenuAction.Status GetInstallMenuActionStatus(
            PackageManagerGitInstallMode installMode)
        {
            if (!CanChooseInstallMode() ||
                !IsInstallModeEnabled(installMode))
            {
                return DropdownMenuAction.Status.Disabled;
            }

            // These entries execute an install choice immediately; they are
            // commands, not a persistent mode selector, so neither is checked.
            return DropdownMenuAction.Status.Normal;
        }

        private bool CanChooseInstallMode()
        {
            return !isDisposed &&
                   installModeSelectionEnabled &&
                   installControlsVisible &&
                   currentRepository != null &&
                   HasAvailableInstallMode() &&
                   (installUiState == InstallUiState.Idle ||
                    installUiState == InstallUiState.Error);
        }

        private void ChooseInstallMode(
            PackageManagerGitInstallMode installMode)
        {
            if (!CanChooseInstallMode() ||
                !IsInstallModeEnabled(installMode))
            {
                return;
            }

            string requestedSelectionIdentity = GetInstallSelectionIdentity(
                currentRepository,
                selectedBranch,
                installMode);
            ApplyInstallModeSelection(installMode);
            if (!CanChooseInstallMode() ||
                selectedInstallMode != installMode ||
                string.IsNullOrEmpty(requestedSelectionIdentity) ||
                !string.Equals(
                    requestedSelectionIdentity,
                    GetInstallSelectionIdentity(
                        currentRepository,
                        selectedBranch,
                        selectedInstallMode),
                    StringComparison.Ordinal))
            {
                return;
            }

            if (!installActionEnabled)
                return;

            BeginInstallConfirmation();
            FocusInstallButtonSoon();
        }

        private void ApplyInstallModeSelection(
            PackageManagerGitInstallMode installMode)
        {
            selectedInstallMode = installMode;
            UpdateSelectedInstallAvailability();
            UpdateBranchTooltip();
            ResetInstallUi();
            InstallSelectionChanged?.Invoke();
        }

        private void OnInstallClicked()
        {
            if (isDisposed || currentRepository == null || !installActionEnabled)
                return;

            if (installUiState != InstallUiState.Confirming ||
                !ConfirmationMatchesCurrentSelection())
            {
                BeginInstallConfirmation();
                return;
            }

            PackageManagerGitHubRepository repository = currentRepository;
            string branch = selectedBranch;
            PackageManagerGitInstallMode installMode = selectedInstallMode;
            ShowInstalling(BuildInstallingMessage(repository, branch, installMode));
            installRequested(repository, branch, installMode);
        }

        private void BeginInstallConfirmation()
        {
            try
            {
                confirmationSelectionIdentity = GetInstallSelectionIdentity(
                    currentRepository,
                    selectedBranch,
                    selectedInstallMode);
                if (string.IsNullOrEmpty(confirmationSelectionIdentity))
                {
                    ShowInstallError(
                        "The selected repository could not be bound to a safe " +
                        "confirmation. Select it again and retry.");
                    return;
                }

                installUiState = InstallUiState.Confirming;
                ShowInstallFeedback(
                    BuildTrustConfirmationMessage(
                        currentRepository,
                        selectedBranch,
                        selectedInstallMode),
                    HelpBoxMessageType.Warning);
                ApplyInstallUiState();
            }
            catch
            {
                ShowInstallError(
                    "Package Manager could not prepare the install confirmation. " +
                    "Select the repository again and retry.");
            }
        }

        private void CancelInstallConfirmation()
        {
            if (isDisposed || installUiState != InstallUiState.Confirming)
                return;

            ResetInstallUi();
            FocusIdleInstallActionSoon();
        }

        private bool ConfirmationMatchesCurrentSelection()
        {
            return currentRepository != null &&
                   !string.IsNullOrEmpty(confirmationSelectionIdentity) &&
                   string.Equals(
                       confirmationSelectionIdentity,
                       GetInstallSelectionIdentity(
                           currentRepository,
                           selectedBranch,
                           selectedInstallMode),
                       StringComparison.Ordinal);
        }

        internal static string BuildTrustConfirmationMessage(
            PackageManagerGitHubRepository repository,
            string branch)
        {
            return BuildTrustConfirmationMessage(
                repository,
                branch,
                PackageManagerGitInstallMode.GitSubmodule);
        }

        internal static string BuildTrustConfirmationMessage(
            PackageManagerGitHubRepository repository,
            string branch,
            PackageManagerGitInstallMode installMode)
        {
            string safeUrl = GitUtility.RedactCredentials(
                repository?.Url?.Trim() ?? string.Empty);
            string selected = string.IsNullOrWhiteSpace(branch)
                ? L10n.Tr("the repository default branch")
                : branch.Trim();
            string destination;
            if (installMode == PackageManagerGitInstallMode.GitSubmodule)
            {
                string packagePath = GitSubmoduleAddService.GetPackagePath(
                    repository?.PackageName ?? string.Empty);
                destination = $"as a Git submodule at {packagePath}";
            }
            else
            {
                destination =
                    "as a read-only Package Manager Git dependency in Unity's Package Cache";
            }

            return $"Install {safeUrl} from {selected} {destination}? " +
                   "Unity packages can contain Editor code that executes inside " +
                   "the Unity Editor. Click Confirm Install only if you trust this repository.";
        }

        private static string BuildInstallingMessage(
            PackageManagerGitHubRepository repository,
            string branch,
            PackageManagerGitInstallMode installMode)
        {
            string selected = string.IsNullOrWhiteSpace(branch)
                ? L10n.Tr("the default branch")
                : branch.Trim();
            string mode = installMode ==
                          PackageManagerGitInstallMode.GitSubmodule
                ? "a Git submodule"
                : "a read-only Package Manager package";
            return $"Installing {repository?.PackageName ?? "Git package"} " +
                   $"from {selected} as {mode}...";
        }

        private static string BuildInstalledMessage(
            PackageManagerGitInstallMode installMode)
        {
            return installMode == PackageManagerGitInstallMode.GitSubmodule
                ? L10n.Tr(
                    "Git submodule installed. Refreshing Package Manager...")
                : L10n.Tr(
                    "Read-only package installed. Refreshing Package Manager...");
        }

        private void ResetInstallUi()
        {
            installUiState = InstallUiState.Idle;
            confirmationSelectionIdentity = string.Empty;
            HideInstallFeedback();
            ApplyInstallUiState();
        }

        private void ApplyInstallUiState()
        {
            if (isDisposed)
                return;

            bool hasBranches = branchField.choices?.Count > 0;
            switch (installUiState)
            {
                case InstallUiState.Confirming:
                    ShowInstallMenu(false);
                    ShowInstallButton(true);
                    installButton.text = L10n.Tr(ConfirmInstallText);
                    installButton.tooltip = installFeedback.text;
                    installButton.SetEnabled(installActionEnabled);
                    cancelInstallButton.style.display = DisplayStyle.Flex;
                    cancelInstallButton.SetEnabled(true);
                    branchField.SetEnabled(false);
                    break;
                case InstallUiState.Installing:
                    ShowInstallMenu(false);
                    ShowInstallButton(true);
                    installButton.text = L10n.Tr(InstallingText);
                    installButton.tooltip = installFeedback.text;
                    installButton.SetEnabled(false);
                    cancelInstallButton.style.display = DisplayStyle.None;
                    branchField.SetEnabled(false);
                    break;
                case InstallUiState.Error:
                    if (installModeSelectionEnabled)
                    {
                        ShowInstallButton(false);
                        ShowInstallMenu(true);
                        installMenu.text = L10n.Tr(RetryInstallText);
                        installMenu.tooltip = installFeedback.text;
                        installMenu.SetEnabled(CanChooseInstallMode());
                    }
                    else
                    {
                        ShowInstallMenu(false);
                        ShowInstallButton(true);
                        installButton.text = L10n.Tr(RetryInstallText);
                        installButton.tooltip = installFeedback.text;
                        installButton.SetEnabled(installActionEnabled);
                    }
                    cancelInstallButton.style.display = DisplayStyle.None;
                    branchField.SetEnabled(hasBranches);
                    break;
                case InstallUiState.Completed:
                    ShowInstallMenu(false);
                    ShowInstallButton(true);
                    installButton.text = L10n.Tr(InstalledText);
                    installButton.tooltip = installFeedback.text;
                    installButton.SetEnabled(false);
                    cancelInstallButton.style.display = DisplayStyle.None;
                    branchField.SetEnabled(false);
                    break;
                default:
                    if (installModeSelectionEnabled)
                    {
                        ShowInstallButton(false);
                        ShowInstallMenu(true);
                        installMenu.text = L10n.Tr(InstallText);
                        installMenu.tooltip = GetInstallMenuTooltip();
                        installMenu.SetEnabled(CanChooseInstallMode());
                    }
                    else
                    {
                        ShowInstallMenu(false);
                        ShowInstallButton(true);
                        installButton.text = L10n.Tr(InstallText);
                        installButton.tooltip = installAvailabilityTooltip;
                        installButton.SetEnabled(installActionEnabled);
                    }
                    cancelInstallButton.style.display = DisplayStyle.None;
                    branchField.SetEnabled(hasBranches);
                    break;
            }
        }

        private void ShowInstallMenu(bool visible)
        {
            installMenu.style.display = visible
                ? DisplayStyle.Flex
                : DisplayStyle.None;
            if (!visible)
                installMenu.SetEnabled(false);
        }

        private void ShowInstallButton(bool visible)
        {
            installButton.style.display = visible
                ? DisplayStyle.Flex
                : DisplayStyle.None;
            if (!visible)
                installButton.SetEnabled(false);
        }

        private bool HasAvailableInstallMode()
        {
            return gitSubmoduleInstallEnabled || readOnlyPackageInstallEnabled;
        }

        private bool IsInstallModeEnabled(
            PackageManagerGitInstallMode installMode)
        {
            return installMode == PackageManagerGitInstallMode.ReadOnlyPackage
                ? readOnlyPackageInstallEnabled
                : gitSubmoduleInstallEnabled;
        }

        private string GetInstallModeTooltip(
            PackageManagerGitInstallMode installMode)
        {
            return installMode == PackageManagerGitInstallMode.ReadOnlyPackage
                ? readOnlyPackageInstallTooltip
                : gitSubmoduleInstallTooltip;
        }

        private string GetInstallMenuTooltip()
        {
            if (installActionEnabled)
                return installAvailabilityTooltip;
            if (gitSubmoduleInstallEnabled)
                return gitSubmoduleInstallTooltip;
            if (readOnlyPackageInstallEnabled)
                return readOnlyPackageInstallTooltip;
            return installAvailabilityTooltip;
        }

        private void UpdateSelectedInstallAvailability()
        {
            installActionEnabled = IsInstallModeEnabled(selectedInstallMode);
            installAvailabilityTooltip =
                GetInstallModeTooltip(selectedInstallMode);
        }

        private void ClearInstallAvailability()
        {
            gitSubmoduleInstallEnabled = false;
            readOnlyPackageInstallEnabled = false;
            gitSubmoduleInstallTooltip = string.Empty;
            readOnlyPackageInstallTooltip = string.Empty;
            UpdateSelectedInstallAvailability();
        }

        private void FocusInstallButtonSoon()
        {
            BeginDeferredFocus(
                installButton,
                InstallUiState.Confirming);
        }

        private void FocusIdleInstallActionSoon()
        {
            VisualElement target = installModeSelectionEnabled
                ? installMenu
                : installButton;
            BeginDeferredFocus(target, InstallUiState.Idle);
        }

        private void BeginDeferredFocus(
            VisualElement target,
            InstallUiState expectedState)
        {
            CancelDeferredFocus();
            if (target == null)
                return;

            deferredFocusTarget = target;
            deferredFocusExpectedState = expectedState;
            deferredFocusAttemptCount = 0;
            QueueDeferredFocusAttempt();
        }

        private void QueueDeferredFocusAttempt()
        {
            if (isDisposed || deferredFocusQueued ||
                deferredFocusTarget == null)
            {
                return;
            }

            deferredFocusQueued = true;
            // Editor update is driven by both graphical Editors and
            // UnityTests. Its invocation snapshot defers a newly queued
            // handler until a later update.
            EditorApplication.update += OnDeferredFocusAttempt;
        }

        private void OnDeferredFocusAttempt()
        {
            EditorApplication.update -= OnDeferredFocusAttempt;
            deferredFocusQueued = false;

            VisualElement target = deferredFocusTarget;
            if (isDisposed ||
                !installControlsVisible ||
                installUiState != deferredFocusExpectedState ||
                target == null ||
                !target.enabledSelf ||
                target.style.display.value != DisplayStyle.Flex)
            {
                CancelDeferredFocus();
                return;
            }

            Focusable focusedElement =
                target.focusController?.focusedElement;
            // A focus change is complete only after it survives a separate
            // editor update. Event default processing can leave null or a
            // hidden/detached stale element, but a different live element is
            // an explicit focus choice and must not be overridden.
            if (ReferenceEquals(focusedElement, target) ||
                ShouldRespectFocusedElement(focusedElement))
            {
                CancelDeferredFocus();
                return;
            }

            if (deferredFocusAttemptCount >= DeferredFocusAttemptLimit)
            {
                CancelDeferredFocus();
                return;
            }

            deferredFocusAttemptCount++;
            if (target.canGrabFocus)
                target.Focus();

            // Always verify on the next editor update, even when Focus()
            // reports immediate success.
            QueueDeferredFocusAttempt();
        }

        private static bool ShouldRespectFocusedElement(
            Focusable focusedElement)
        {
            if (focusedElement == null)
                return false;

            if (!(focusedElement is VisualElement visualElement))
                return true;

            return visualElement.panel != null &&
                   visualElement.enabledInHierarchy &&
                   visualElement.resolvedStyle.display != DisplayStyle.None &&
                   visualElement.resolvedStyle.visibility == Visibility.Visible;
        }

        private void CancelDeferredFocus()
        {
            if (deferredFocusQueued)
                EditorApplication.update -= OnDeferredFocusAttempt;

            deferredFocusQueued = false;
            deferredFocusTarget = null;
            deferredFocusAttemptCount = 0;
        }

        private void ShowInstallFeedback(string message, HelpBoxMessageType type)
        {
            EnsureInstallFeedbackMounted();
            installFeedback.text = message ?? string.Empty;
            installFeedback.tooltip = installFeedback.text;
            installFeedback.messageType = type;
            installFeedback.style.display = DisplayStyle.Flex;
        }

        private void HideInstallFeedback()
        {
            installFeedback.text = string.Empty;
            installFeedback.tooltip = string.Empty;
            installFeedback.style.display = DisplayStyle.None;
        }

        private void EnsureInstallFeedbackMounted()
        {
            if (isDisposed)
                return;

            VisualElement target = detailsLinksContainer.parent?.Q<VisualElement>(
                NativeHelpBoxContainerName);
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

            if (!ReferenceEquals(installFeedback.parent, target))
            {
                installFeedback.RemoveFromHierarchy();
                target.Add(installFeedback);
            }

            if (!ReferenceEquals(target, ownedFeedbackContainer) &&
                ownedFeedbackContainer?.parent != null)
            {
                ownedFeedbackContainer.RemoveFromHierarchy();
            }
        }

        private void MountRepositoryLink(
            PackageManagerGitHubRepository repository)
        {
            if (!GitUtility.TryGetRepositoryWebUrl(
                    repository?.Url,
                    out string repositoryWebUrl))
            {
                RemoveRepositoryLink();
                return;
            }

            repositoryLinkButton.tooltip = repositoryWebUrl;
            VisualElement target = detailsLinksContainer.Q<VisualElement>(
                                       UpmLinksContainerName) ??
                                   detailsLinksContainer.Q<VisualElement>(
                                       AssetStoreLinksContainerName);
            if (target == null)
            {
                if (ownedLinksContainer == null)
                {
                    ownedLinksContainer = new VisualElement
                    {
                        name = OwnedLinksContainerName
                    };
                    ownedLinksContainer.AddToClassList("left");
                }

                if (!ReferenceEquals(
                        ownedLinksContainer.parent,
                        detailsLinksContainer))
                {
                    ownedLinksContainer.RemoveFromHierarchy();
                    detailsLinksContainer.Add(ownedLinksContainer);
                }

                target = ownedLinksContainer;
            }

            if (ReferenceEquals(repositoryLinkButton.parent, target))
            {
                repositoryLinkTarget = target;
                detailsLinksContainer.style.display = DisplayStyle.Flex;
                forcedDetailsLinksDisplay = true;
                return;
            }

            if (!forcedDetailsLinksDisplay)
                detailsLinksDisplayBeforeMount = detailsLinksContainer.style.display;
            repositoryLinkSeparator.RemoveFromHierarchy();
            repositoryLinkButton.RemoveFromHierarchy();
            if (target.childCount > 0)
                target.Add(repositoryLinkSeparator);
            target.Add(repositoryLinkButton);
            repositoryLinkTarget = target;
            detailsLinksContainer.style.display = DisplayStyle.Flex;
            forcedDetailsLinksDisplay = true;
        }

        private void RemoveRepositoryLink()
        {
            VisualElement previousTarget = repositoryLinkButton.parent ??
                                           repositoryLinkTarget;
            bool linkWasStillMounted = repositoryLinkButton.parent != null ||
                                       repositoryLinkSeparator.parent != null ||
                                       ownedLinksContainer?.parent != null;
            repositoryLinkSeparator.RemoveFromHierarchy();
            repositoryLinkButton.RemoveFromHierarchy();
            if (ownedLinksContainer != null)
                ownedLinksContainer.RemoveFromHierarchy();
            if (forcedDetailsLinksDisplay &&
                linkWasStillMounted &&
                (previousTarget == null || previousTarget.childCount == 0))
            {
                detailsLinksContainer.style.display =
                    detailsLinksDisplayBeforeMount;
            }

            repositoryLinkTarget = null;
            forcedDetailsLinksDisplay = false;
        }

        private void SetVisible(bool visible)
        {
            installControlsVisible = visible;
            controls.style.display = visible
                ? DisplayStyle.Flex
                : DisplayStyle.None;
            if (!visible)
            {
                installMenu.SetEnabled(false);
                installButton.SetEnabled(false);
                cancelInstallButton.style.display = DisplayStyle.None;
                HideInstallFeedback();
            }
        }

        private void ResetInstallModeSelection()
        {
            selectedInstallMode = GitSubmoduleManagerUserSettings.Instance
                .DefaultInstallMode;
            UpdateSelectedInstallAvailability();
        }

        internal static string GetInstallMenuActionText(
            PackageManagerGitInstallMode installMode)
        {
            return installMode == PackageManagerGitInstallMode.ReadOnlyPackage
                ? L10n.Tr(InstallAsReadOnlyPackageText)
                : L10n.Tr(InstallAsGitSubmoduleText);
        }

        private static void AddValidBranchChoice(
            string branch,
            ICollection<string> choices,
            ISet<string> seen)
        {
            string value = NormalizeBranch(branch);
            if (string.IsNullOrEmpty(value) ||
                !GitUtility.IsValidBranchName(value) ||
                !seen.Add(value))
            {
                return;
            }

            choices.Add(value);
        }

        private static string NormalizeBranch(string branch)
        {
            return branch?.Trim() ?? string.Empty;
        }

        private enum InstallUiState
        {
            Idle,
            Confirming,
            Installing,
            Error,
            Completed
        }
    }
}
