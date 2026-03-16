using UnityEditor;

namespace GitPackageManager.Editor
{
    internal static class MenuPath
    {
        [MenuItem("Window/Package Management/Git Package Manager")]
        public static void ShowWindow()
        {
            var window = EditorWindow.GetWindow<GitPackageManagerWindow>("Git Packages");
            window.RefreshPackages();
            window.Show();
        }
    }
}
