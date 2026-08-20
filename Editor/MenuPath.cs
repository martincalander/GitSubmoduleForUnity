using UnityEditor;

namespace MartinCalander.GitSubmoduleManager.Editor
{
    internal static class MenuPath
    {
        internal const string ItemPath = "Window/Package Management/Git Submodule Manager";
        internal const string DisplayPath = "Window > Package Management > Git Submodule Manager";

        [MenuItem(ItemPath)]
        public static void ShowWindow()
        {
            var window = EditorWindow.GetWindow<GitSubmoduleManagerWindow>("Git Submodule Manager");
            window.ApplyThemeIcon();
            window.Show();
            window.Focus();
        }
    }
}
