using System.IO;
using UnityEditor;
using UnityEngine;

namespace GitPackageManager.Editor
{
    [InitializeOnLoad]
    internal static class GitPackageBootstrap
    {
        private static bool hasChecked;

        static GitPackageBootstrap()
        {
            EditorApplication.delayCall += EnsurePackagesReady;
        }

        private static void EnsurePackagesReady()
        {
            if (hasChecked)
            {
                return;
            }

            hasChecked = true;

            if (!GitUtility.IsGitAvailable(out _, out _))
            {
                return;
            }

            bool refreshNeeded = false;

            if (!GitUtility.TryEnsureSubmodulesInitialized(out bool initializedAny, out string error))
            {
                if (!string.IsNullOrWhiteSpace(error))
                {
                    Debug.LogWarning($"[Git Package Manager] {error}. If needed, run: git submodule update --init --recursive");
                }
            }
            else if (initializedAny)
            {
                Debug.Log("[Git Package Manager] Initialized missing git submodules from .gitmodules.");
                refreshNeeded = true;
            }

            string manifestPath = Path.Combine(GitUtility.ProjectRoot, ".gitpackages");
            if (File.Exists(manifestPath))
            {
                if (GitUtility.TryGetSubtrees(out var subtrees, out _))
                {
                    bool cleaned = false;
                    foreach (var subtree in subtrees)
                    {
                        string fullPath = Path.Combine(GitUtility.ProjectRoot, subtree.Path);
                        if (!Directory.Exists(fullPath))
                        {
                            GitPackagesManifestUtility.RemoveEntry(subtree.Path);
                            cleaned = true;
                        }
                    }

                    if (cleaned)
                    {
                        Debug.Log("[Git Package Manager] Cleaned up stale subtree entries from .gitpackages.");
                    }
                }
            }

            if (refreshNeeded)
            {
                AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate);
            }
        }
    }
}
