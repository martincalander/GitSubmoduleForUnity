using UnityEditor;
using UnityEngine;

namespace MartinCalander.GitSubmoduleManager.Editor
{
    internal static class GitSubmoduleManagerIcons
    {
        internal const string DarkThemeIconFileName = "GitEditorWindowIcon.png";
        internal const string LightThemeIconFileName = "GitEditorWindowIconLight.png";

        private const string FallbackPackagePath =
            "Packages/com.martincalander.gitsubmodulemanager";

        private static Texture2D darkThemeIcon;
        private static Texture2D lightThemeIcon;

        internal static Texture2D GitIcon => GetGitIcon(EditorGUIUtility.isProSkin);

        internal static Texture2D GetGitIcon(bool useDarkTheme)
        {
            if (useDarkTheme)
            {
                if (darkThemeIcon == null)
                    darkThemeIcon = LoadIcon(DarkThemeIconFileName);
                return darkThemeIcon;
            }

            if (lightThemeIcon == null)
                lightThemeIcon = LoadIcon(LightThemeIconFileName);
            return lightThemeIcon;
        }

        private static Texture2D LoadIcon(string iconFileName)
        {
            return AssetDatabase.LoadAssetAtPath<Texture2D>(
                $"{ResolvePackageAssetPath()}/Editor/{iconFileName}");
        }

        private static string ResolvePackageAssetPath()
        {
            try
            {
                UnityEditor.PackageManager.PackageInfo package =
                    UnityEditor.PackageManager.PackageInfo.FindForAssembly(
                        typeof(GitSubmoduleManagerIcons).Assembly);
                if (!string.IsNullOrWhiteSpace(package?.assetPath))
                    return GitUtility.NormalizePath(package.assetPath).TrimEnd('/');
            }
            catch
            {
                // Package Manager may still be initializing during an assembly reload.
            }

            return FallbackPackagePath;
        }
    }
}
