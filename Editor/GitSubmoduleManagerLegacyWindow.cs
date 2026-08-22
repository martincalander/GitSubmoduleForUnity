using UnityEditor;
using UnityEngine;
using UnityEngine.Scripting.APIUpdating;

namespace MartinCalander.GitSubmoduleManager.Editor
{
    [MovedFrom(
        true,
        sourceNamespace: "MartinCalander.GitPackageManager.Editor",
        sourceAssembly: "MartinCalander.GitPackageManager.Editor",
        sourceClassName: "GitPackageManagerWindow")]
    public sealed class GitSubmoduleManagerWindow : EditorWindow
    {
        private bool redirectQueued;

        internal static bool AreBackgroundLoadsDraining =>
            GitSubmoduleManagerView.AreBackgroundLoadsDraining;

        internal static bool IsSharedGitHubAuthenticationBlocked =>
            GitSubmoduleManagerView.IsSharedGitHubAuthenticationBlocked;

        private void OnEnable()
        {
            titleContent = new GUIContent(
                "Git Submodule Manager",
                GitSubmoduleManagerIcons.GitIcon);
            minSize = new Vector2(420f, 120f);
            QueueRedirect();
        }

        private void OnGUI()
        {
            EditorGUILayout.Space(16f);
            EditorGUILayout.HelpBox(
                "Git Submodule Manager now lives inside Unity's Package Manager under Sources > GitHub.",
                MessageType.Info);
            EditorGUILayout.Space(6f);
            if (GUILayout.Button("Open in Package Manager", GUILayout.Height(28f)))
                QueueRedirect();
        }

        private void QueueRedirect()
        {
            if (redirectQueued)
                return;

            redirectQueued = true;
            EditorApplication.delayCall += RedirectAndClose;
        }

        private void RedirectAndClose()
        {
            if (this == null)
                return;

            redirectQueued = false;
            GitSubmoduleManagerPackageManagerHost.Open();
            Close();
        }
    }
}
