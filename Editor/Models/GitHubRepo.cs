using System;

namespace MartinCalander.GitSubmoduleManager.Editor
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
        public PackageManifestState ManifestState;
        public string PackageManifestMessage;
        public string DeclaredPackageName;
        public string DeclaredDisplayName;
        public string DeclaredVersion;
        public string DeclaredDescription;
        public string DeclaredMinimumUnityVersion;
        public string DeclaredAuthorName;
        public string DeclaredLicense;
        public string DeclaredDocumentationUrl;
        public string DeclaredChangelogUrl;
        public string DeclaredLicensesUrl;
        public PackageManifestDependency[] DeclaredDependencies =
            Array.Empty<PackageManifestDependency>();
        public string PackageManifestCommitOid;
        public string PackageManifestBlobOid;
        public string PackageManifestMetaBlobOid;
        public string PackageManifestMetaGuid;

        public bool PackageJsonChecked =>
            ManifestState == PackageManifestState.Valid ||
            ManifestState == PackageManifestState.Missing ||
            ManifestState == PackageManifestState.Invalid;

    }
}
