using UnityEditor;

namespace Calander.SubmodulePackageManager.Editor
{
    internal static class MenuPath
    {
        [MenuItem("Window/Package Management/Git Submodules Manager")]
        public static void ShowWindow()
        {
            var window = EditorWindow.GetWindow<GitSubmodulesWindow>("Git Submodules");
            window.RefreshSubmodules();
            window.Show();
        }
    }
}