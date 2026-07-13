using UnityEditor;
using UnityEngine;

namespace MartinCalander.GitPackageManager.Editor
{
    public partial class GitPackageManagerWindow
    {
        private string lastAutoDerivedPackageName = string.Empty;

        private void ShowAddFromUrlPopup(Rect buttonRect)
        {
            addStatus = string.Empty;
            addStatusType = MessageType.None;
            operationStatus = string.Empty;
            operationStatusType = MessageType.None;
            activeAddPopup = new AddFromUrlPopup(this);
            PopupWindow.Show(buttonRect, activeAddPopup);
        }

        private void DrawAddByUrl()
        {
            EditorGUILayout.Space(8);
            GUILayout.Label("Add Submodule", Styles.TitleLabel);
            EditorGUILayout.Space(8);

            EditorGUI.BeginChangeCheck();
            EditorGUILayout.BeginHorizontal();
            GUILayout.Label("URL", Styles.InfoLabel, GUILayout.Width(80));
            addUrl = EditorGUILayout.TextField(addUrl);
            EditorGUILayout.EndHorizontal();
            bool urlChanged = EditorGUI.EndChangeCheck();
            if (TryDerivePackageNameFromUrl(addUrl, out string derivedName))
            {
                addPackageName = ResolvePackageNameAfterUrlEdit(
                    addPackageName,
                    urlChanged,
                    lastAutoDerivedPackageName,
                    derivedName);
                if (urlChanged)
                    lastAutoDerivedPackageName = derivedName;
            }

            EditorGUILayout.BeginHorizontal();
            GUILayout.Label("Branch", Styles.InfoLabel, GUILayout.Width(80));
            addBranch = EditorGUILayout.TextField(addBranch);
            EditorGUILayout.EndHorizontal();
            GUILayout.Label("Leave Branch empty to use the repository's default branch.", Styles.FooterLabel);

            EditorGUILayout.BeginHorizontal();
            GUILayout.Label("Package Name", Styles.InfoLabel, GUILayout.Width(80));
            addPackageName = EditorGUILayout.TextField(addPackageName);
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(8);
            string validationError = ValidatePackageInput(addUrl, addPackageName, addBranch);
            if (!string.IsNullOrWhiteSpace(validationError))
                EditorGUILayout.HelpBox(validationError, MessageType.Warning);
            else
                GUILayout.Label(PackageNameRule, Styles.FooterLabel);

            if (!string.IsNullOrWhiteSpace(addStatus))
                EditorGUILayout.HelpBox(addStatus, addStatusType);

            EditorGUILayout.Space(8);

            using (new EditorGUI.DisabledScope(
                       !gitAvailable ||
                       IsRepositoryOperationBusy ||
                       !string.IsNullOrWhiteSpace(validationError)))
            {
                if (GUILayout.Button("Add", GUILayout.Height(24)))
                    TryAddSubmodule(addUrl, addBranch, addPackageName);
            }
        }

        internal static string ResolvePackageNameAfterUrlEdit(
            string currentPackageName,
            bool urlChanged,
            string previousDerivedPackageName,
            string newDerivedPackageName)
        {
            bool isStillAutomatic = string.IsNullOrWhiteSpace(currentPackageName) ||
                                    string.Equals(
                                        currentPackageName,
                                        previousDerivedPackageName,
                                        System.StringComparison.Ordinal);
            return urlChanged &&
                   isStillAutomatic &&
                   !string.IsNullOrWhiteSpace(newDerivedPackageName)
                ? newDerivedPackageName
                : currentPackageName;
        }

        private sealed class AddFromUrlPopup : PopupWindowContent
        {
            private readonly GitPackageManagerWindow owner;
            private Vector2 scrollPosition;

            public AddFromUrlPopup(GitPackageManagerWindow owner)
            {
                this.owner = owner;
            }

            public override Vector2 GetWindowSize()
            {
                return GetAddFromUrlPopupSize();
            }

            public override void OnGUI(Rect rect)
            {
                Styles.Initialize();
                scrollPosition = EditorGUILayout.BeginScrollView(scrollPosition);
                owner.DrawAddByUrl();
                EditorGUILayout.EndScrollView();
            }

            public override void OnClose()
            {
                owner.activeAddPopup = null;
            }

            public void ClosePopup()
            {
                editorWindow?.Close();
            }

            public void RepaintPopup()
            {
                editorWindow?.Repaint();
            }
        }

        internal static Vector2 GetAddFromUrlPopupSize()
        {
            return new Vector2(440f, 320f);
        }
    }
}
