namespace Essentials.GitPackageManager.Editor
{
    internal sealed class GitHubRepo
    {
        public string Name;
        public string Owner;
        public string Url;
        public string DefaultBranch;
        public bool IsPrivate;
        public string Description;
        public string UpdatedAt;
        public bool IsInstalled;
        public bool HasPackageJson;
        public bool PackageJsonChecked;
        public string PackageJsonError;
    }
}
