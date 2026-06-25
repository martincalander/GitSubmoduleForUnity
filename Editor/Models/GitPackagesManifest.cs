using System;
using System.Collections.Generic;

namespace Essentials.GitPackageManager.Editor
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
