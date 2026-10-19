using System;
using System.Text.RegularExpressions;

namespace MartinCalander.GitSubmoduleManager.Editor
{
#if UNITY_6000_5_OR_NEWER
    [Unity.Scripting.LifecycleManagement.NoAutoStaticsCleanup]
#endif
    internal sealed class PackageSourceDetails
    {
        internal static readonly PackageSourceDetails Unavailable = new PackageSourceDetails();
        internal bool IsInstalled { get; private set; }
        internal string ResolvedPath { get; private set; } = string.Empty;
        internal string SourceUrl { get; private set; } = string.Empty;
        internal string RepositoryUrl { get; private set; } = string.Empty;
        internal bool SourceUrlIsSanitized { get; private set; }
        internal string RequestedRevision { get; private set; } = string.Empty;
        internal string InstalledCommit { get; private set; } = string.Empty;
        internal string RevisionDescription { get; private set; } = string.Empty;

        private PackageSourceDetails() { }

        internal static PackageSourceDetails Create(string resolvedPath, string sourceUrl, string revision, string hash,
            bool gitDependency)
        {
            var result = new PackageSourceDetails { IsInstalled = true, ResolvedPath = resolvedPath ?? string.Empty };
            if (TryReadSource(sourceUrl, out string source, out string browser, out bool sanitized))
            {
                result.SourceUrl = source;
                result.RepositoryUrl = browser;
                result.SourceUrlIsSanitized = sanitized;
            }
            if (gitDependency)
            {
                string requested = revision;
                if (string.IsNullOrEmpty(requested) && !string.IsNullOrEmpty(sourceUrl))
                {
                    int fragment = sourceUrl.IndexOf('#');
                    if (fragment >= 0)
                        requested = sourceUrl.Substring(fragment + 1);
                }
                result.RequestedRevision = string.IsNullOrEmpty(requested) ? "default branch" :
                    SafeText(requested) ? requested : "unavailable";
                result.InstalledCommit = hash != null && Regex.IsMatch(hash, "\\A([0-9a-fA-F]{40}|[0-9a-fA-F]{64})\\z") ? hash : string.Empty;
                result.RevisionDescription = result.RequestedRevision + " → " + (result.InstalledCommit.Length == 0
                    ? "installed commit unavailable" : result.InstalledCommit.Substring(0, 7));
            }
            return result;
        }

        private static bool TryReadSource(string value, out string source, out string browser, out bool sanitized)
        {
            source = browser = string.Empty;
            sanitized = false;
            if (!SafeText(value))
                return false;
            string url = value;
            bool gitPrefix = url.StartsWith("git+", StringComparison.OrdinalIgnoreCase);
            if (gitPrefix)
                url = url.Substring(4);
            bool scp = Regex.IsMatch(url, "\\A[^/@:]+@[^/:]+:.+");
            if (scp)
            {
                int colon = url.IndexOf(':');
                url = "ssh://" + url.Substring(0, colon) + "/" + url.Substring(colon + 1);
            }
            if (!Uri.TryCreate(url, UriKind.Absolute, out Uri uri))
            {
                if ((url.StartsWith("../", StringComparison.Ordinal) || url.StartsWith("./", StringComparison.Ordinal)) &&
                    url.IndexOf('?') < 0 && url.IndexOf('#') < 0 && url.IndexOf('\\') < 0)
                {
                    source = url;
                    return true;
                }
                return false;
            }
            if (uri.Scheme != "https" && uri.Scheme != "http" && uri.Scheme != "ssh" && uri.Scheme != "git" && uri.Scheme != "file")
                return false;
            if (uri.Scheme != "file" && string.IsNullOrEmpty(uri.Host))
                return false;

            var clean = new UriBuilder(uri);
            if (clean.UserName.Length != 0 || clean.Password.Length != 0)
            {
                if (uri.Scheme != "ssh" || clean.UserName != "git" || clean.Password.Length != 0)
                {
                    clean.UserName = clean.Password = string.Empty;
                    sanitized = true;
                }
            }
            string pathQuery = ReadPackagePath(uri.Query);
            if (uri.Query.Length != 0 && uri.Query != pathQuery)
                sanitized = true;
            clean.Query = pathQuery;
            source = clean.Uri.AbsoluteUri;
            if (scp && !sanitized)
                source = "git@" + uri.Host + ":" + clean.Uri.PathAndQuery.TrimStart('/') + clean.Fragment;
            else if (gitPrefix)
                source = "git+" + source;

            bool http = uri.Scheme == "https" || uri.Scheme == "http";
            bool knownSshHost = (uri.Scheme == "ssh" || uri.Scheme == "git") && uri.IsDefaultPort &&
                (uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) ||
                 uri.Host.Equals("gitlab.com", StringComparison.OrdinalIgnoreCase) ||
                 uri.Host.Equals("bitbucket.org", StringComparison.OrdinalIgnoreCase));
            if (http || knownSshHost)
            {
                clean.UserName = clean.Password = clean.Query = clean.Fragment = string.Empty;
                if (!http)
                {
                    clean.Scheme = "https";
                    clean.Port = -1;
                }
                clean.Path = clean.Path.TrimEnd('/');
                if (clean.Path.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
                    clean.Path = clean.Path.Substring(0, clean.Path.Length - 4);
                browser = clean.Uri.AbsoluteUri;
            }
            return true;
        }

        private static string ReadPackagePath(string query)
        {
            if (string.IsNullOrEmpty(query))
                return string.Empty;
            string found = string.Empty;
            foreach (string item in query.TrimStart('?').Split('&'))
            {
                if (!item.StartsWith("path=", StringComparison.Ordinal) || found.Length != 0)
                    continue;
                string path = item.Substring(5);
                try
                {
                    if (SafeText(Uri.UnescapeDataString(path)))
                        found = "?path=" + path;
                }
                catch (UriFormatException) { }
            }
            return found;
        }

        private static bool SafeText(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > 4096)
                return false;
            foreach (char character in value)
                if (char.IsControl(character))
                    return false;
            return true;
        }
    }
}
