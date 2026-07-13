namespace MartinCalander.GitPackageManager.Editor
{
    internal enum PackageManifestState
    {
        Unknown,
        Checking,
        Valid,
        Missing,
        Invalid,
        Unavailable
    }

    internal sealed class GitHubRepo
    {
        public string NodeId;
        public string Name;
        public string Owner;
        public string Url;
        public string DefaultBranch;
        public bool IsPrivate;
        public string Description;
        public string UpdatedAt;
        public bool IsInstalled;
        public PackageManifestState ManifestState;
        public string PackageManifestMessage;
        public string DeclaredPackageName;
        public string PackageManifestBlobOid;

        public bool HasPackageJson => ManifestState == PackageManifestState.Valid;

        public bool PackageJsonChecked =>
            ManifestState == PackageManifestState.Valid ||
            ManifestState == PackageManifestState.Missing ||
            ManifestState == PackageManifestState.Invalid;

        public string PackageJsonError =>
            ManifestState == PackageManifestState.Unavailable
                ? PackageManifestMessage
                : string.Empty;
    }
}
