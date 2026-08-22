using UnityEditor;

namespace MartinCalander.GitSubmoduleManager.Editor
{
    internal static class MenuPath
    {
        internal const string ItemPath = "Window/Package Management/Git Submodule Manager";
        internal const string DisplayPath =
            "Window > Package Management > Git Submodule Manager";

        [MenuItem(ItemPath)]
        public static void ShowWindow()
        {
            GitSubmoduleManagerPackageManagerHost.Open();
        }
    }
}
