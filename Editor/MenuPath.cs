using UnityEditor;

namespace MartinCalander.GitPackageManager.Editor
{
    internal static class MenuPath
    {
        [MenuItem("Window/Package Management/Git Package Manager")]
        public static void ShowWindow()
        {
            var window = EditorWindow.GetWindow<GitPackageManagerWindow>("Git Package Manager");
            window.ApplyThemeIcon();
            window.RefreshPackages();
            window.Show();
        }
    }
}
