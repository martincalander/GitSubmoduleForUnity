using UnityEditor;
using UnityEngine;

namespace Calander.SubmodulePackageManager.Editor
{
    public partial class GitSubmodulesWindow
    {
        private void ShowAddFromUrlPopup(Rect buttonRect)
        {
            addStatus = string.Empty;
            addStatusType = MessageType.None;
            activeAddPopup = new AddFromUrlPopup(this);
            PopupWindow.Show(buttonRect, activeAddPopup);
        }

        private void DrawAddByUrl()
        {
            EditorGUILayout.Space(8);
            GUILayout.Label("Add package from git URL", Styles.TitleLabel);
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
                    TryAddSubmodule(addUrl, addBranch, addPackageName);
            }
        }

        private sealed class AddFromUrlPopup : PopupWindowContent
        {
            private readonly GitSubmodulesWindow owner;

            public AddFromUrlPopup(GitSubmodulesWindow owner)
            {
                this.owner = owner;
            }

            public override Vector2 GetWindowSize()
            {
                return new Vector2(400f, 220f);
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
