using UnityEditor;
using UnityEngine;

namespace Calander.SubmodulePackageManager.Editor
{
    [InitializeOnLoad]
    internal static class SubmoduleBootstrap
    {
        private static bool hasCheckedSubmodules;

        static SubmoduleBootstrap()
        {
            EditorApplication.delayCall += EnsureSubmodulesReady;
        }

        private static void EnsureSubmodulesReady()
        {
            if (hasCheckedSubmodules)
            {
                return;
            }

            hasCheckedSubmodules = true;

            if (!GitUtility.IsGitAvailable(out _, out _))
            {
                return;
            }

            if (!GitUtility.TryEnsureSubmodulesInitialized(out bool initializedAny, out string error))
            {
                if (!string.IsNullOrWhiteSpace(error))
                {
                    Debug.LogWarning($"[Submodule Helper] {error}. If needed, run: git submodule update --init --recursive");
                }
                return;
            }

            if (!initializedAny)
            {
                return;
            }

            Debug.Log("[Submodule Helper] Initialized missing git submodules from .gitmodules.");
            AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate);
        }
    }
}
