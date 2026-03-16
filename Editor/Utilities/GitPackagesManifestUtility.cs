using System.IO;
using UnityEngine;

namespace GitPackageManager.Editor
{
    internal static class GitPackagesManifestUtility
    {
        private static string ManifestPath => Path.Combine(GitUtility.ProjectRoot, ".gitpackages");

        internal static GitPackagesManifest Load()
        {
            string path = ManifestPath;
            if (!File.Exists(path))
            {
                return new GitPackagesManifest();
            }

            try
            {
                string json = File.ReadAllText(path);
                var manifest = JsonUtility.FromJson<GitPackagesManifest>(json);
                return manifest ?? new GitPackagesManifest();
            }
            catch
            {
                return new GitPackagesManifest();
            }
        }

        internal static void Save(GitPackagesManifest manifest)
        {
            string json = JsonUtility.ToJson(manifest, true);
            File.WriteAllText(ManifestPath, json);
        }

        internal static void AddEntry(string path, string url, string branch)
        {
            if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(url))
                return;

            var manifest = Load();
            manifest.subtrees.RemoveAll(e => e.path == path);
            manifest.subtrees.Add(new GitPackagesManifestEntry
            {
                path = path,
                url = url,
                branch = branch
            });
            Save(manifest);
        }

        internal static void RemoveEntry(string path)
        {
            var manifest = Load();
            int removed = manifest.subtrees.RemoveAll(e => e.path == path);
            if (removed > 0)
            {
                Save(manifest);
            }
        }

        internal static GitPackagesManifestEntry Find(string path)
        {
            var manifest = Load();
            return manifest.subtrees.Find(e => e.path == path);
        }
    }
}
