namespace GitPackageManager.Editor
{
    internal enum PackageSourceType
    {
        Submodule,
        Subtree
    }

    internal sealed class GitPackageInfo
    {
        public PackageSourceType SourceType;
        public string Name;
        public string Path;
        public string Url;
        public string Branch;
        public string CommitHash;
        public bool HasPackageJson;
        public string PackageName;
    }
}
