using UnityEditor;
using UnityEngine;

namespace GitPackageManager.Editor
{
    public partial class GitPackageManagerWindow
    {
        private void ShowAddFromUrlPopup(Rect buttonRect, PackageSourceType sourceType)
        {
            addStatus = string.Empty;
            addStatusType = MessageType.None;
            addSourceType = sourceType;
            activeAddPopup = new AddFromUrlPopup(this);
            PopupWindow.Show(buttonRect, activeAddPopup);
        }

        private void DrawAddByUrl()
        {
            string title = addSourceType == PackageSourceType.Subtree ? "Add Subtree" : "Add Submodule";
            EditorGUILayout.Space(8);
            GUILayout.Label(title, Styles.TitleLabel);
            EditorGUILayout.Space(8);

            EditorGUI.BeginChangeCheck();
            EditorGUILayout.BeginHorizontal();
            GUILayout.Label("URL", Styles.InfoLabel, GUILayout.Width(80));
            addUrl = EditorGUILayout.TextField(addUrl);
            EditorGUILayout.EndHorizontal();
            if (EditorGUI.EndChangeCheck() && TryDerivePackageNameFromUrl(addUrl, out string derivedName))
                addPackageName = derivedName;

            EditorGUILayout.BeginHorizontal();
            GUILayout.Label("Branch", Styles.InfoLabel, GUILayout.Width(80));
            addBranch = EditorGUILayout.TextField(addBranch);
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            GUILayout.Label("Package Name", Styles.InfoLabel, GUILayout.Width(80));
            addPackageName = EditorGUILayout.TextField(addPackageName);
            EditorGUILayout.EndHorizontal();

            if (TryDerivePackageNameFromUrl(addUrl, out string autoName) &&
                (string.IsNullOrWhiteSpace(addPackageName) || !GitUtility.IsValidPackageName(addPackageName)))
            {
                addPackageName = autoName;
            }

            EditorGUILayout.Space(8);
            string validationError = ValidatePackageInput(addUrl, addPackageName);
            if (!string.IsNullOrWhiteSpace(validationError))
                EditorGUILayout.HelpBox(validationError, MessageType.Warning);
            else
                GUILayout.Label(PackageNameRule, Styles.FooterLabel);

            if (!string.IsNullOrWhiteSpace(addStatus))
                EditorGUILayout.HelpBox(addStatus, addStatusType);

            EditorGUILayout.Space(8);

            using (new EditorGUI.DisabledScope(!gitAvailable || !string.IsNullOrWhiteSpace(validationError)))
            {
                if (GUILayout.Button("Add", GUILayout.Height(24)))
                    TryAddPackage(addUrl, addBranch, addPackageName, addSourceType);
            }
        }

        private sealed class AddFromUrlPopup : PopupWindowContent
        {
            private readonly GitPackageManagerWindow owner;

            public AddFromUrlPopup(GitPackageManagerWindow owner)
            {
                this.owner = owner;
            }

            public override Vector2 GetWindowSize()
            {
                return new Vector2(400f, 230f);
            }

            public override void OnGUI(Rect rect)
            {
                Styles.Initialize();
                owner.DrawAddByUrl();
            }

            public override void OnClose()
            {
                owner.activeAddPopup = null;
            }

            public void ClosePopup()
            {
                editorWindow?.Close();
            }
        }
    }
}
