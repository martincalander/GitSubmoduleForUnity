using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace MartinCalander.GitSubmoduleManager.Editor
{
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
        internal const string RepositoryLinkElementName =
            "git-submodule-manager-repository-link";
        internal const string InstallText = "Install";
        internal const string RepositoryLinkText = "Repository";

        private const string UpmLinksContainerName = "upmLinksContainer";
        private const string AssetStoreLinksContainerName = "assetStoreLinksContainer";
        private const string OwnedLinksContainerName =
            "git-submodule-manager-repository-links";

        private readonly VisualElement primaryActionsContainer;
        private readonly VisualElement detailsLinksContainer;
        private readonly VisualElement controls;
        private readonly DropdownField branchField;
        private readonly Button installButton;
        private readonly Label repositoryLinkSeparator;
        private readonly Button repositoryLinkButton;
        private readonly Action<PackageManagerGitHubRepository, string> installRequested;
        private readonly Action<string> openUrl;
        private readonly RepositoryCoordinator repositoryCoordinator;
        private readonly bool branchDiscoveryEnabled;

        private VisualElement ownedLinksContainer;
        private VisualElement repositoryLinkTarget;
        private StyleEnum<DisplayStyle> detailsLinksDisplayBeforeMount;
        private bool forcedDetailsLinksDisplay;
        private PackageManagerGitHubRepository currentRepository;
        private string currentRepositoryIdentity = string.Empty;
        private string selectedBranch = string.Empty;
        private string observedDefaultBranch = string.Empty;
        private bool userSelectedBranch;
        private bool isDisposed;

        private PackageManagerGitHubDetails(
            VisualElement primaryActionsContainer,
            VisualElement detailsLinksContainer,
            Action<PackageManagerGitHubRepository, string> installRequested,
            Action<string> openUrl,
            bool enableBranchDiscovery)
        {
            this.primaryActionsContainer = primaryActionsContainer;
            this.detailsLinksContainer = detailsLinksContainer;
            this.installRequested = installRequested;
            this.openUrl = openUrl;
            branchDiscoveryEnabled = enableBranchDiscovery;
            repositoryCoordinator = enableBranchDiscovery
                ? new RepositoryCoordinator()
                : null;

            controls = new VisualElement
            {
                name = ControlsElementName
            };
            controls.style.flexDirection = FlexDirection.Row;
            controls.style.alignItems = Align.Center;
            controls.style.flexShrink = 0f;

            var branchLabel = new Label(L10n.Tr("Branch"));
            branchLabel.style.marginRight = 4f;
            controls.Add(branchLabel);

            branchField = new DropdownField
            {
                name = BranchFieldElementName
            };
            branchField.style.minWidth = 140f;
            branchField.style.maxWidth = 220f;
            branchField.style.marginRight = 4f;
            branchField.RegisterValueChangedCallback(OnBranchChanged);
            controls.Add(branchField);

            installButton = new Button(OnInstallClicked)
            {
                name = InstallActionElementName,
                text = InstallText
            };
            controls.Add(installButton);

            repositoryLinkSeparator = new Label("|");
            repositoryLinkSeparator.AddToClassList("separator");
            repositoryLinkButton = new Button(() => OpenRepositoryWebsite())
            {
                name = RepositoryLinkElementName,
                text = L10n.Tr(RepositoryLinkText)
            };
            repositoryLinkButton.AddToClassList("link");

            EnsurePrimaryControlsMounted();
            SetVisible(false);
            if (branchDiscoveryEnabled)
                EditorApplication.update += OnEditorUpdate;
        }

        internal VisualElement Controls => controls;
        internal DropdownField BranchField => branchField;
        internal Button InstallButton => installButton;
        internal Button RepositoryLinkButton => repositoryLinkButton;
        internal PackageManagerGitHubRepository CurrentRepository => currentRepository;
        internal string SelectedBranch => selectedBranch;
        internal bool IsDisposed => isDisposed;

        internal static bool TryCreate(
            VisualElement primaryActionsContainer,
            VisualElement detailsLinksContainer,
            Action<PackageManagerGitHubRepository, string> installRequested,
            Action<string> openUrl,
            bool enableBranchDiscovery,
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
                    enableBranchDiscovery);
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
            if (repository == null)
            {
                currentRepository = null;
                currentRepositoryIdentity = string.Empty;
                selectedBranch = string.Empty;
                observedDefaultBranch = string.Empty;
                userSelectedBranch = false;
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
                selectedBranch = nextDefaultBranch;
                userSelectedBranch = false;
            }
            else if (changedDefaultBranch && !userSelectedBranch)
            {
                selectedBranch = nextDefaultBranch;
            }

            observedDefaultBranch = nextDefaultBranch;
            ApplyAvailableBranches(GetCachedBranches(repository.Url));
            MountRepositoryLink(repository);
            SetVisible(true);

            if (branchDiscoveryEnabled)
            {
                repositoryCoordinator.RequestBranches(repository.Url);
                UpdateBranchTooltip();
            }
        }

        internal void SetInstallState(bool visible, bool enabled, string tooltip)
        {
            if (isDisposed)
                return;

            SetVisible(visible);
            installButton.SetEnabled(visible && enabled);
            installButton.tooltip = tooltip ?? string.Empty;
        }

        internal void EnsurePrimaryControlsMounted()
        {
            if (isDisposed || ReferenceEquals(controls.parent, primaryActionsContainer))
                return;

            controls.RemoveFromHierarchy();
            primaryActionsContainer.Insert(0, controls);
        }

        internal void ApplyAvailableBranchesForTests(IEnumerable<string> branches)
        {
            ApplyAvailableBranches(branches);
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
            var choices = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            AddValidBranchChoice(defaultBranch, choices, seen);
            if (discoveredBranches != null)
            {
                foreach (string branch in discoveredBranches)
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

            return GitHubUtility.GetRepositoryCacheIdentity(repository.Url);
        }

        public void Dispose()
        {
            if (isDisposed)
                return;

            isDisposed = true;
            if (branchDiscoveryEnabled)
                EditorApplication.update -= OnEditorUpdate;
            branchField.UnregisterValueChangedCallback(OnBranchChanged);
            repositoryCoordinator?.Dispose();
            RemoveRepositoryLink();
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
                ApplyAvailableBranches(GetCachedBranches(currentRepository.Url));
                UpdateBranchTooltip();
            }
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
                nextSelection = choices.Contains(normalizedDefault)
                    ? normalizedDefault
                    : choices.Count > 0 ? choices[0] : string.Empty;
                userSelectedBranch = false;
            }

            selectedBranch = nextSelection;
            branchField.choices = choices;
            branchField.SetValueWithoutNotify(selectedBranch);
            branchField.SetEnabled(choices.Count > 0);
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
                branchField.tooltip = L10n.Tr(
                    "Select the branch that will be checked out by the Git submodule.");
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
        }

        private void OnInstallClicked()
        {
            if (isDisposed || currentRepository == null)
                return;

            installRequested(currentRepository, selectedBranch);
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
            controls.style.display = visible
                ? DisplayStyle.Flex
                : DisplayStyle.None;
            if (!visible)
                installButton.SetEnabled(false);
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
    }
}
