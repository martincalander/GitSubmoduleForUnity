using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace MartinCalander.GitSubmoduleManager.Editor
{
    /// <summary>
    /// Host-neutral installer opened by Package Manager's add menu. It follows
    /// Package Manager's own input-dropdown lifecycle and anchoring while using
    /// public UI Toolkit controls for the extra submodule-specific fields.
    /// Repository mutation remains owned by <see cref="GitSubmoduleAddService"/>.
    /// </summary>
    internal sealed class GitSubmoduleInstallPopup : EditorWindow
    {
        internal const string WindowTitle = "Install package as Git Submodule";
        internal const string EditorMenuExtensionsTypeName =
            "UnityEditor.UIElements.EditorMenuExtensions";

        internal const float StatusViewportHeight = 28f;
        internal static readonly Vector2 DefaultWindowSize = new(420f, 132f);

        private const double ProbeDebounceSeconds = 0.4d;

        private const BindingFlags AnyStatic =
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        private static GitSubmoduleInstallPopup activeWindow;

        private TextField urlField;
        private TextField packageNameField;
        private TextField branchField;
        private ToolbarMenu branchMenu;
        private readonly List<string> availableBranches = new();
        private ScrollView statusViewport;
        private HelpBox validationHelp;
        private Button submitButton;
        private GitSubmoduleInstallProbe installProbe;
        private string pendingProbeUrl = string.Empty;
        private string previousAutomaticPackageName = string.Empty;
        private string previousAutomaticBranch = string.Empty;
        private string packageManifestBranch = string.Empty;
        private double probeNotBefore;
        private int appliedProbeRevision = -1;
        private bool packageNameEditedByUser;
        private bool branchEditedByUser;
        private bool uiBuilt;

        /// <summary>
        /// Opens a dropdown under Package Manager's native add-menu button.
        /// Unity's own GUI-to-screen conversion is used when available so the
        /// placement matches "Install package from git URL..." on Retina and
        /// multi-window layouts.
        /// </summary>
        internal static bool Show(VisualElement activator)
        {
            if (TryOpen(activator))
                return true;

            if (activator?.panel == null)
                return false;

            // Package Manager can rebuild the toolbar and invoke an extension
            // item before layout has produced valid geometry. Unity's own
            // dropdown defers in this case, so retry once on the element's
            // scheduler instead of opening at a fabricated screen position.
            activator.schedule.Execute(() => TryOpen(activator));
            return true;
        }

        private static bool TryOpen(VisualElement activator)
        {
            if (!TryGetActivatorScreenRect(activator, out Rect screenRect))
                return false;

            GitSubmoduleInstallPopup window = null;
            try
            {
                activeWindow?.Close();

                window = CreateInstance<GitSubmoduleInstallPopup>();
                window.hideFlags = HideFlags.DontSave;
                window.titleContent = new GUIContent(WindowTitle);
                Vector2 windowSize = GetWindowSize(activator);
                window.minSize = windowSize;
                window.maxSize = windowSize;
                activeWindow = window;
                window.ShowAsDropDown(screenRect, windowSize);
                window.FocusUrlFieldLater();
                return true;
            }
            catch
            {
                if (ReferenceEquals(activeWindow, window))
                    activeWindow = null;

                if (window != null)
                {
                    try
                    {
                        window.Close();
                    }
                    catch
                    {
                        try
                        {
                            DestroyImmediate(window);
                        }
                        catch
                        {
                            // The partially-created native window is already
                            // being destroyed by Unity.
                        }
                    }
                }

                return false;
            }
        }

        internal static Vector2 GetWindowSize(VisualElement activator)
        {
            // Unity receives the final bounds once in ShowAsDropDown and can
            // fit them to the active monitor. Do not inherit the full Package
            // Manager toolbar width or resize the native window afterwards.
            return DefaultWindowSize;
        }

        internal static MethodInfo FindGuiToScreenRectMethod()
        {
            Type extensionsType = PackageManagerSubmoduleHarmonyPatch.FindLoadedType(
                EditorMenuExtensionsTypeName);
            if (extensionsType == null)
                return null;

            foreach (MethodInfo method in extensionsType.GetMethods(AnyStatic))
            {
                ParameterInfo[] parameters = method.GetParameters();
                if (method.Name == "GUIToScreenRect" &&
                    method.IsStatic &&
                    method.ReturnType == typeof(Rect) &&
                    parameters.Length == 2 &&
                    parameters[0].ParameterType == typeof(VisualElement) &&
                    parameters[1].ParameterType == typeof(Rect))
                {
                    return method;
                }
            }

            return null;
        }

        internal static bool TryGetActivatorScreenRect(
            VisualElement activator,
            out Rect screenRect)
        {
            screenRect = default;
            if (activator?.panel == null)
                return false;

            Rect panelRect = activator.worldBound;
            if (!IsUsableRect(panelRect))
                return false;
            try
            {
                MethodInfo method = FindGuiToScreenRectMethod();
                if (method?.Invoke(
                        null,
                        new object[] { activator, panelRect }) is Rect converted &&
                    IsUsableRect(converted))
                {
                    screenRect = converted;
                    return true;
                }
            }
            catch
            {
                // Use Unity's public GUI conversion as a compatibility fallback.
            }

            try
            {
                Rect converted = GUIUtility.GUIToScreenRect(panelRect);
                converted = ApplyLegacyScreenRectCorrection(
                    converted,
                    panelRect);
                if (!IsUsableRect(converted))
                    return false;

                screenRect = converted;
                return true;
            }
            catch
            {
                return false;
            }
        }

        internal static Rect ApplyLegacyScreenRectCorrection(
            Rect screenRect,
            Rect panelRect)
        {
#if UNITY_2023_2_OR_NEWER && !UNITY_6000_0_OR_NEWER
            // Unity 2023.2's ExtendableToolbarMenu applies this correction
            // after GUIUtility.GUIToScreenRect. Earlier Editors do not, and
            // Unity 6000 replaces both calls with EditorMenuExtensions.
            screenRect.y -= panelRect.yMax;
#endif
            return screenRect;
        }

        private static bool IsUsableRect(Rect rect)
        {
            return IsFinite(rect.x) &&
                   IsFinite(rect.y) &&
                   IsFinite(rect.width) &&
                   IsFinite(rect.height) &&
                   rect.width > 0f &&
                   rect.height > 0f;
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        public void CreateGUI()
        {
            BuildUi();
        }

        private void BuildUi()
        {
            if (uiBuilt)
                return;

            uiBuilt = true;
            VisualElement root = rootVisualElement;
            root.Clear();
            root.style.paddingLeft = 8f;
            root.style.paddingRight = 8f;
            root.style.paddingTop = 4f;
            root.style.paddingBottom = 4f;
            root.style.flexDirection = FlexDirection.Column;

            urlField = CreateTextField(
                "Git URL",
                "Secure HTTPS, SSH, or an explicit local repository URL.");
            packageNameField = CreateTextField(
                "Package Name",
                "The name declared by the repository's root package.json.");
            branchField = CreateTextField(
                "Branch",
                "Enter a branch or choose one reported by the repository.");

            urlField.name = "git-submodule-url";
            packageNameField.name = "git-submodule-package-name";
            branchField.name = "git-submodule-branch";

            root.Add(urlField);
            root.Add(packageNameField);

            var branchRow = new VisualElement
            {
                name = "git-submodule-branch-row"
            };
            branchRow.style.flexDirection = FlexDirection.Row;
            branchRow.style.alignItems = Align.Center;
            branchField.style.flexGrow = 1f;
            branchMenu = new ToolbarMenu
            {
                name = "git-submodule-branch-menu",
                text = "Branches",
                tooltip = "Choose a branch reported by Git.",
                variant = ToolbarMenu.Variant.Popup
            };
            branchMenu.style.marginLeft = 3f;
            branchMenu.style.marginBottom = 1f;
            branchRow.Add(branchField);
            branchRow.Add(branchMenu);
            root.Add(branchRow);

            statusViewport = new ScrollView(ScrollViewMode.Vertical)
            {
                name = "git-submodule-status-viewport",
                horizontalScrollerVisibility = ScrollerVisibility.Hidden,
                verticalScrollerVisibility = ScrollerVisibility.Auto,
                focusable = true
            };
            statusViewport.style.marginTop = 2f;
            statusViewport.style.flexShrink = 0f;
            statusViewport.style.height = StatusViewportHeight;
            statusViewport.style.display = DisplayStyle.Flex;
            statusViewport.style.visibility = Visibility.Hidden;

            validationHelp = new HelpBox(string.Empty, HelpBoxMessageType.Warning);
            validationHelp.name = "git-submodule-status";
            statusViewport.Add(validationHelp);
            root.Add(statusViewport);

            var actions = new VisualElement
            {
                name = "git-submodule-actions"
            };
            actions.style.flexDirection = FlexDirection.Row;
            actions.style.justifyContent = Justify.FlexEnd;
            actions.style.marginTop = 3f;
            actions.style.flexShrink = 0f;

            var cancelButton = new Button(Close)
            {
                name = "git-submodule-cancel",
                text = "Cancel"
            };
            cancelButton.style.minWidth = 72f;
            cancelButton.style.marginRight = 4f;

            submitButton = new Button(Submit)
            {
                name = "git-submodule-submit",
                text = "Clone and Add"
            };
            submitButton.style.minWidth = 96f;

            actions.Add(cancelButton);
            actions.Add(submitButton);
            root.Add(actions);

            installProbe = new GitSubmoduleInstallProbe();
            SetMetadataControlsEnabled(false);
            urlField.RegisterValueChangedCallback(_ => OnRepositoryUrlChanged());
            packageNameField.RegisterValueChangedCallback(_ =>
            {
                packageNameEditedByUser = true;
                UpdatePresentation();
            });
            branchField.RegisterValueChangedCallback(_ =>
            {
                branchEditedByUser = true;
                UpdatePresentation();
            });
            root.RegisterCallback<KeyDownEvent>(OnKeyDown, TrickleDown.TrickleDown);
            root.schedule.Execute(TickProbeAndUpdatePresentation).Every(100L);
            UpdatePresentation();
        }

        private static TextField CreateTextField(string label, string tooltip)
        {
            var field = new TextField(label)
            {
                tooltip = tooltip,
                isDelayed = false
            };
            field.style.marginBottom = 1f;
            field.labelElement.style.minWidth = 90f;
            return field;
        }

        private void OnRepositoryUrlChanged()
        {
            string url = urlField?.value?.Trim() ?? string.Empty;
            pendingProbeUrl = string.Empty;
            appliedProbeRevision = -1;
            ClearDiscoveredBranches();
            ClearPreviousAutomaticValues();
            SetMetadataControlsEnabled(false);

            if (GitUtility.IsValidRepositoryUrl(url))
            {
                pendingProbeUrl = url;
                probeNotBefore = EditorApplication.timeSinceStartup +
                                 ProbeDebounceSeconds;
            }
            else
            {
                installProbe?.Cancel();
            }

            UpdatePresentation();
        }

        private void TickProbeAndUpdatePresentation()
        {
            installProbe?.Tick();

            if (!string.IsNullOrWhiteSpace(pendingProbeUrl) &&
                EditorApplication.timeSinceStartup >= probeNotBefore)
            {
                string currentUrl = urlField?.value?.Trim() ?? string.Empty;
                string requestedUrl = pendingProbeUrl;
                pendingProbeUrl = string.Empty;
                if (string.Equals(
                        currentUrl,
                        requestedUrl,
                        StringComparison.Ordinal) &&
                    GitUtility.IsValidRepositoryUrl(requestedUrl))
                {
                    installProbe?.Request(requestedUrl);
                }
            }

            ApplyCurrentProbeSnapshot();
            UpdatePresentation();
        }

        private void ApplyCurrentProbeSnapshot()
        {
            GitSubmoduleInstallProbeSnapshot snapshot = installProbe?.Current;
            string currentUrl = urlField?.value?.Trim() ?? string.Empty;
            if (!IsCurrentProbeSnapshot(snapshot, currentUrl) ||
                snapshot.Revision == appliedProbeRevision)
            {
                return;
            }

            appliedProbeRevision = snapshot.Revision;
            if (!string.IsNullOrWhiteSpace(snapshot.PackageName))
            {
                string resolved = ResolveProbedValue(
                    packageNameField?.value,
                    packageNameEditedByUser,
                    previousAutomaticPackageName,
                    snapshot.PackageName);
                previousAutomaticPackageName = snapshot.PackageName;
                packageManifestBranch = snapshot.DefaultBranch?.Trim() ?? string.Empty;
                packageNameField?.SetValueWithoutNotify(resolved);
            }

            if (!string.IsNullOrWhiteSpace(snapshot.DefaultBranch))
            {
                string resolved = ResolveProbedValue(
                    branchField?.value,
                    branchEditedByUser,
                    previousAutomaticBranch,
                    snapshot.DefaultBranch);
                previousAutomaticBranch = snapshot.DefaultBranch;
                branchField?.SetValueWithoutNotify(resolved);
            }

            PopulateDiscoveredBranches(
                snapshot.DefaultBranch,
                snapshot.Branches);
            SetMetadataControlsEnabled(snapshot.IsComplete);
        }

        private void ClearPreviousAutomaticValues()
        {
            if (!packageNameEditedByUser &&
                string.Equals(
                    packageNameField?.value,
                    previousAutomaticPackageName,
                    StringComparison.Ordinal))
            {
                packageNameField?.SetValueWithoutNotify(string.Empty);
            }

            if (!branchEditedByUser &&
                string.Equals(
                    branchField?.value,
                    previousAutomaticBranch,
                    StringComparison.Ordinal))
            {
                branchField?.SetValueWithoutNotify(string.Empty);
            }

            previousAutomaticPackageName = string.Empty;
            previousAutomaticBranch = string.Empty;
            packageManifestBranch = string.Empty;
        }

        private void PopulateDiscoveredBranches(
            string defaultBranch,
            IReadOnlyList<string> branches)
        {
            ClearDiscoveredBranches();
            availableBranches.AddRange(BuildBranchChoices(defaultBranch, branches));
            foreach (string branch in availableBranches)
            {
                string capturedBranch = branch;
                branchMenu?.menu.AppendAction(
                    capturedBranch,
                    _ => SelectDiscoveredBranch(capturedBranch));
            }
        }

        private void SelectDiscoveredBranch(string branch)
        {
            if (branchField == null ||
                string.IsNullOrWhiteSpace(branch) ||
                !GitUtility.IsValidBranchName(branch))
            {
                return;
            }

            branchEditedByUser = true;
            branchField.SetValueWithoutNotify(branch);
            UpdatePresentation();
        }

        private void ClearDiscoveredBranches()
        {
            availableBranches.Clear();
            branchMenu?.menu.MenuItems().Clear();
        }

        private void SetMetadataControlsEnabled(bool enabled)
        {
            packageNameField?.SetEnabled(enabled);
            branchField?.SetEnabled(enabled);
            branchMenu?.SetEnabled(enabled && availableBranches.Count > 0);
        }

        private void UpdatePresentation()
        {
            if (urlField == null || packageNameField == null || branchField == null)
                return;

            string currentUrl = urlField.value?.Trim() ?? string.Empty;
            bool validUrl = GitUtility.IsValidRepositoryUrl(currentUrl);
            GitSubmoduleInstallProbeSnapshot snapshot = installProbe?.Current;
            bool hasCurrentSnapshot = IsCurrentProbeSnapshot(snapshot, currentUrl);
            bool isProbing = validUrl &&
                             (!string.IsNullOrWhiteSpace(pendingProbeUrl) ||
                              !hasCurrentSnapshot ||
                              !snapshot.IsComplete);
            bool metadataEnabled = AreMetadataFieldsEnabled(
                validUrl,
                hasCurrentSnapshot && snapshot.IsComplete);
            SetMetadataControlsEnabled(metadataEnabled);

            string validationError = GetValidationError(
                currentUrl,
                packageNameField.value,
                branchField.value);
            string visibleValidationError = ShouldShowValidationError(
                currentUrl,
                validationError)
                ? validationError
                : string.Empty;
            bool canStart = GitSubmoduleAddService.CanStart;
            string probeWarning = string.Empty;
            if (hasCurrentSnapshot && snapshot.IsComplete)
            {
                probeWarning = !string.IsNullOrWhiteSpace(snapshot.ErrorMessage)
                    ? snapshot.ErrorMessage
                    : snapshot.ManifestMessage;
            }
            string branchManifestWarning = ShouldWarnAboutManifestBranch(
                branchField.value,
                packageManifestBranch,
                packageNameEditedByUser)
                ? "Package Name was read from the repository's default branch. " +
                  "Git will verify it again when adding the selected branch."
                : string.Empty;
            string combinedWarning = CombineMessages(
                probeWarning,
                branchManifestWarning);

            if (isProbing)
            {
                validationHelp.text = "Inspecting repository with Git...";
                validationHelp.messageType = HelpBoxMessageType.Info;
                validationHelp.style.display = DisplayStyle.Flex;
            }
            else if (!string.IsNullOrWhiteSpace(combinedWarning))
            {
                validationHelp.text = string.IsNullOrWhiteSpace(visibleValidationError)
                    ? GitHubUtility.SanitizeUiDiagnostic(combinedWarning)
                    : GitHubUtility.SanitizeUiDiagnostic(combinedWarning) +
                      "\n\n" + visibleValidationError;
                validationHelp.messageType = HelpBoxMessageType.Warning;
                validationHelp.style.display = DisplayStyle.Flex;
            }
            else if (!string.IsNullOrWhiteSpace(visibleValidationError))
            {
                validationHelp.text = visibleValidationError;
                validationHelp.messageType = HelpBoxMessageType.Warning;
                validationHelp.style.display = DisplayStyle.Flex;
            }
            else if (!canStart && !string.IsNullOrWhiteSpace(currentUrl))
            {
                validationHelp.text =
                    "Wait for current package scans and repository operations to finish.";
                validationHelp.messageType = HelpBoxMessageType.Info;
                validationHelp.style.display = DisplayStyle.Flex;
            }
            else
            {
                validationHelp.text = string.Empty;
                validationHelp.style.display = DisplayStyle.None;
            }

            UpdateStatusViewport();
            submitButton?.SetEnabled(CanSubmit(
                validationError,
                canStart,
                isProbing));
        }

        internal void UpdateStatusViewport()
        {
            if (statusViewport == null || validationHelp == null)
                return;

            bool statusVisible =
                validationHelp.style.display.value == DisplayStyle.Flex &&
                !string.IsNullOrWhiteSpace(validationHelp.text);
            string statusText = statusVisible
                ? validationHelp.text
                : string.Empty;
            statusViewport.style.display = DisplayStyle.Flex;
            statusViewport.style.visibility = statusVisible
                ? Visibility.Visible
                : Visibility.Hidden;
            statusViewport.style.height = StatusViewportHeight;
            statusViewport.tooltip = statusText;
        }

        private void OnKeyDown(KeyDownEvent evt)
        {
            if (evt.keyCode == KeyCode.Escape)
            {
                evt.StopImmediatePropagation();
                Close();
                return;
            }

            if ((evt.keyCode == KeyCode.Return ||
                 evt.keyCode == KeyCode.KeypadEnter) &&
                IsPackageInputFocused() &&
                submitButton?.enabledSelf == true)
            {
                evt.StopImmediatePropagation();
                Submit();
            }
        }

        private bool IsPackageInputFocused()
        {
            VisualElement focused =
                rootVisualElement.focusController?.focusedElement as VisualElement;
            for (VisualElement element = focused;
                 element != null;
                 element = element.parent)
            {
                if (ReferenceEquals(element, urlField) ||
                    ReferenceEquals(element, packageNameField) ||
                    ReferenceEquals(element, branchField))
                {
                    return true;
                }
            }

            return false;
        }

        private void FocusUrlFieldLater()
        {
            EditorApplication.delayCall -= FocusUrlField;
            EditorApplication.delayCall += FocusUrlField;
        }

        private void FocusUrlField()
        {
            EditorApplication.delayCall -= FocusUrlField;
            if (this == null || urlField == null)
                return;

            Focus();
            urlField.Focus();
        }

        private void Submit()
        {
            string submittedUrl = urlField?.value?.Trim() ?? string.Empty;
            string submittedPackageName =
                packageNameField?.value?.Trim() ?? string.Empty;
            string submittedBranch = branchField?.value?.Trim() ?? string.Empty;
            string validationError = GetValidationError(
                submittedUrl,
                submittedPackageName,
                submittedBranch);
            if (!string.IsNullOrWhiteSpace(validationError))
            {
                ShowError("Cannot Add Git Package", validationError);
                return;
            }

            if (!GitSubmoduleAddService.CanStart)
            {
                ShowError(
                    "Cannot Add Git Package",
                    "Wait for current package scans and repository operations to finish.");
                return;
            }

            if (!EditorUtility.DisplayDialog(
                    "Add Git Package?",
                    BuildTrustConfirmationMessage(
                        submittedUrl,
                        submittedPackageName,
                        submittedBranch),
                    "Clone and Add",
                    "Cancel"))
            {
                return;
            }

            bool started = GitSubmoduleAddService.TryStart(
                submittedUrl,
                submittedBranch,
                submittedPackageName,
                OnAddCompleted,
                out string startError);
            if (!started)
            {
                ShowError(
                    "Could Not Start Add",
                    string.IsNullOrWhiteSpace(startError)
                        ? "The Git package operation could not be started."
                        : startError);
                return;
            }

            Close();
        }

        private void OnDisable()
        {
            EditorApplication.delayCall -= FocusUrlField;
            pendingProbeUrl = string.Empty;
            installProbe?.Dispose();
            installProbe = null;
            if (ReferenceEquals(activeWindow, this))
                activeWindow = null;
        }

        private void OnEnable()
        {
            foreach (GitSubmoduleInstallPopup window in
                     Resources.FindObjectsOfTypeAll<GitSubmoduleInstallPopup>())
            {
                if (window != null && !ReferenceEquals(window, this))
                    window.Close();
            }

            activeWindow = this;
        }

        private static void OnAddCompleted(GitSubmoduleAddCompletion completion)
        {
            if (completion == null || !completion.Success)
            {
                ShowError(
                    "Could Not Add Git Package",
                    string.IsNullOrWhiteSpace(completion?.Message)
                        ? "The Git package operation did not complete successfully."
                        : completion.Message);
                return;
            }

            try
            {
                PackageManagerSubmoduleSnapshot.Refresh();
                PackageManagerGitHubPackageProjection.Reconcile(
                    PackageManagerGitHubDiscovery.Current);
                PackageManagerSubmoduleHarmonyPatch.RefreshOpenPackageManagerWindows();
            }
            catch (Exception exception)
            {
                ShowError(
                    "Package Manager Refresh Failed",
                    "The package was added, but Package Manager could not refresh: " +
                    exception.Message);
            }
        }

        internal static bool CanSubmit(string validationError, bool canStart)
        {
            return CanSubmit(validationError, canStart, false);
        }

        internal static bool CanSubmit(
            string validationError,
            bool canStart,
            bool isProbing)
        {
            return canStart &&
                   !isProbing &&
                   string.IsNullOrWhiteSpace(validationError);
        }

        internal static string GetValidationError(
            string repositoryUrl,
            string declaredPackageName,
            string selectedBranch)
        {
            return GitSubmoduleAddService.ValidateInput(
                repositoryUrl,
                declaredPackageName,
                selectedBranch);
        }

        internal static string BuildDestinationPreview(string declaredPackageName)
        {
            string normalized = declaredPackageName?.Trim() ?? string.Empty;
            return GitUtility.IsValidUpmPackageName(normalized)
                ? GitSubmoduleAddService.GetPackagePath(normalized)
                : "Packages/<package-name>";
        }

        internal static string BuildTrustConfirmationMessage(
            string repositoryUrl,
            string declaredPackageName,
            string selectedBranch)
        {
            string branchDescription = string.IsNullOrWhiteSpace(selectedBranch)
                ? "the repository default"
                : selectedBranch.Trim();
            string destination = BuildDestinationPreview(declaredPackageName);
            string safeUrl = GitUtility.FormatRepositoryUrlForDisplay(
                repositoryUrl?.Trim() ?? string.Empty);
            return
                $"Repository:\n{safeUrl}\n\n" +
                $"Branch: {branchDescription}\n" +
                $"Destination: {destination}\n\n" +
                "Unity packages can contain Editor code that executes inside " +
                "the Unity Editor. Only install repositories you trust.";
        }

        internal static string ResolveProbedValue(
            string currentValue,
            bool editedByUser,
            string previousAutomaticValue,
            string newAutomaticValue)
        {
            if (editedByUser || string.IsNullOrWhiteSpace(newAutomaticValue))
                return currentValue ?? string.Empty;

            bool isAutomatic = string.IsNullOrWhiteSpace(currentValue) ||
                               string.Equals(
                                   currentValue,
                                   previousAutomaticValue,
                                   StringComparison.Ordinal);
            return isAutomatic ? newAutomaticValue.Trim() : currentValue;
        }

        internal static List<string> BuildBranchChoices(
            string defaultBranch,
            IReadOnlyList<string> branches)
        {
            var result = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            string normalizedDefault = defaultBranch?.Trim() ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(normalizedDefault) &&
                GitUtility.IsValidBranchName(normalizedDefault) &&
                seen.Add(normalizedDefault))
            {
                result.Add(normalizedDefault);
            }

            var remaining = new List<string>();
            if (branches != null)
            {
                foreach (string branch in branches)
                {
                    string normalized = branch?.Trim() ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(normalized) ||
                        !GitUtility.IsValidBranchName(normalized) ||
                        !seen.Add(normalized))
                        continue;

                    remaining.Add(normalized);
                }
            }

            remaining.Sort((left, right) =>
            {
                int insensitive = StringComparer.OrdinalIgnoreCase.Compare(left, right);
                return insensitive != 0
                    ? insensitive
                    : StringComparer.Ordinal.Compare(left, right);
            });
            result.AddRange(remaining);
            return result;
        }

        internal static bool AreMetadataFieldsEnabled(
            bool repositoryUrlIsValid,
            bool probeIsComplete)
        {
            return repositoryUrlIsValid && probeIsComplete;
        }

        internal static bool ShouldShowValidationError(
            string repositoryUrl,
            string validationError)
        {
            // An untouched form is already self-explanatory and the disabled
            // action communicates that input is required. Keep validation
            // intact for submission, but only spend vertical space on an
            // inline warning after the user has entered repository input.
            return !string.IsNullOrWhiteSpace(repositoryUrl) &&
                   !string.IsNullOrWhiteSpace(validationError);
        }

        internal static bool ShouldWarnAboutManifestBranch(
            string selectedBranch,
            string inspectedBranch,
            bool packageNameEditedByUser)
        {
            if (packageNameEditedByUser ||
                string.IsNullOrWhiteSpace(selectedBranch) ||
                string.IsNullOrWhiteSpace(inspectedBranch))
            {
                return false;
            }

            return !string.Equals(
                selectedBranch.Trim(),
                inspectedBranch.Trim(),
                StringComparison.Ordinal);
        }

        private static string CombineMessages(string first, string second)
        {
            if (string.IsNullOrWhiteSpace(first))
                return second ?? string.Empty;
            if (string.IsNullOrWhiteSpace(second))
                return first;
            return first.Trim() + "\n\n" + second.Trim();
        }

        private static bool IsCurrentProbeSnapshot(
            GitSubmoduleInstallProbeSnapshot snapshot,
            string currentUrl)
        {
            return snapshot != null &&
                   !string.IsNullOrWhiteSpace(currentUrl) &&
                   string.Equals(
                       snapshot.Url?.Trim(),
                       currentUrl.Trim(),
                       StringComparison.Ordinal);
        }

        private static void ShowError(string title, string message)
        {
            string safeMessage = GitHubUtility.SanitizeUiDiagnostic(message);
            EditorUtility.DisplayDialog(
                string.IsNullOrWhiteSpace(title) ? "Git Package Error" : title,
                string.IsNullOrWhiteSpace(safeMessage)
                    ? "The Git package operation could not be completed."
                    : safeMessage,
                "OK");
        }
    }
}
