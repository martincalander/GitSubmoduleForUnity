using System;
using System.Collections.Generic;

namespace GitPackageManager.Editor
{
    [Serializable]
    internal sealed class GitPackagesManifest
    {
        public List<GitPackagesManifestEntry> subtrees = new();
    }

    [Serializable]
    internal sealed class GitPackagesManifestEntry
    {
        public string path;
        public string url;
        public string branch;
    }
}
