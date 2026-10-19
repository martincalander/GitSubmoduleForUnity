using System;
using System.Text.RegularExpressions;

namespace MartinCalander.GitSubmoduleManager.Editor
{
    internal static class PackageSourceDetails
    {
        internal static string GetRepositoryUrl(string sourceUrl)
        {
            if (!SafeText(sourceUrl))
                return string.Empty;
            string url = sourceUrl;
            if (url.StartsWith("git+", StringComparison.OrdinalIgnoreCase))
                url = url.Substring(4);
            if (Regex.IsMatch(url, "\\A[^/@:]+@[^/:]+:.+"))
            {
                int colon = url.IndexOf(':');
                url = "ssh://" + url.Substring(0, colon) + "/" + url.Substring(colon + 1);
            }
            if (!Uri.TryCreate(url, UriKind.Absolute, out Uri uri) || string.IsNullOrEmpty(uri.Host))
                return string.Empty;

            bool http = uri.Scheme == "https" || uri.Scheme == "http";
            bool knownSshHost = (uri.Scheme == "ssh" || uri.Scheme == "git") && uri.IsDefaultPort &&
                (uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) ||
                 uri.Host.Equals("gitlab.com", StringComparison.OrdinalIgnoreCase) ||
                 uri.Host.Equals("bitbucket.org", StringComparison.OrdinalIgnoreCase));
            if (!http && !knownSshHost)
                return string.Empty;

            try
            {
                var clean = new UriBuilder(uri)
                {
                    UserName = string.Empty,
                    Password = string.Empty,
                    Query = string.Empty,
                    Fragment = string.Empty
                };
                if (!http)
                {
                    clean.Scheme = "https";
                    clean.Port = -1;
                }
                clean.Path = clean.Path.TrimEnd('/');
                if (clean.Path.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
                    clean.Path = clean.Path.Substring(0, clean.Path.Length - 4);
                return clean.Uri.AbsoluteUri;
            }
            catch (UriFormatException) { return string.Empty; }
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
