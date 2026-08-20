namespace MartinCalander.GitSubmoduleManager.Editor
{
    internal sealed class GitPackageInfo
    {
        public string Name;
        public string Path;
        public string Url;
        public string Branch;
        public string CommitHash;
        public bool IsInitialized;
        public bool HasPackageJson;
        public string PackageName;
    }
}
