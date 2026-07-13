using UnityEditor;

namespace MartinCalander.GitPackageManager.Editor
{
    internal static class MenuPath
    {
        internal const string ItemPath = "Window/Package Management/Git Package Manager";
        internal const string DisplayPath = "Window > Package Management > Git Package Manager";

        [MenuItem(ItemPath)]
        public static void ShowWindow()
        {
            var window = EditorWindow.GetWindow<GitPackageManagerWindow>("Git Package Manager");
            window.ApplyThemeIcon();
            window.Show();
            window.Focus();
        }
    }
}
