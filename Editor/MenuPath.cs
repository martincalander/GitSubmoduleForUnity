using UnityEditor;
using UnityEngine;

namespace Essentials.GitPackageManager.Editor
{
    internal static class MenuPath
    {
        [MenuItem("Window/Package Management/Git Package Manager")]
        public static void ShowWindow()
        {
            var window = EditorWindow.GetWindow<GitPackageManagerWindow>("Submodule Manager");
            var icon = AssetDatabase.LoadAssetAtPath<Texture2D>(
                "Packages/com.essentials.gitpackagemanager/Editor/GitEditorWindowIcon.png");
            if (icon != null)
                window.titleContent = new GUIContent("Submodule Manager", icon);
            window.RefreshPackages();
            window.Show();
        }
    }
}
