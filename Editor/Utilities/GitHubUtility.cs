using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using UnityEngine;

namespace MartinCalander.GitSubmoduleManager.Editor
{
    internal static class GitHubUtility
    {
        internal const string GitHubHost = "github.com";
        internal const int MaxUiDiagnosticCharacters = 4096;
        internal const string AuthenticationDisplayCommand =
            "gh auth login --hostname github.com --git-protocol https --web --clipboard";
        internal const string AuthenticationTerminalDisplayCommand =
            "gh auth login --hostname github.com --web";
        internal const string AuthenticationGuideUrl =
            "https://cli.github.com/manual/gh_auth_login";
        internal const string AuthenticationDeviceUrl =
            "https://github.com/login/device";
        private const string DiagnosticTruncationNotice = "… [truncated]";
        private static readonly Version MinimumClipboardAuthenticationVersion = new Version(2, 79, 0);

        private static readonly Regex GitHubRepoRegex = new Regex(
            @"^(?:(?:https?|git|ssh)://(?:[^/@]+@)?(?:www\.)?github\.com/|git@github\.com:)(?<owner>[^/\s]+)/(?<repo>[^/\s]+?)(?:\.git)?/?$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex ScpRepositoryRegex = new Regex(
            @"^(?<user>[^@\s/:]+)@(?<host>[^:\s/]+):(?<path>.+)$",
            RegexOptions.Compiled);
        private static readonly Regex GhVersionRegex = new Regex(
            @"(?im)^\s*gh\s+version\s+(?<version>\d+\.\d+\.\d+)(?:\s|$)",
            RegexOptions.Compiled);

        internal static bool IsGhAvailable(out string version, out string error)
        {
            return IsGhAvailable(CancellationToken.None, out version, out error);
        }

        internal static bool IsGhAvailable(
            CancellationToken cancellationToken,
            out string version,
            out string error)
        {
            return IsGhAvailable(
                cancellationToken,
                out version,
                out error,
                out _);
        }

        internal static bool IsGhAvailable(
            CancellationToken cancellationToken,
            out string version,
            out string error,
            out bool deferredByAuthentication)
        {
            var result = CliCommandRunner.Run(
                "gh",
                "--version",
                GitUtility.ProjectRoot,
                CliCommandRunner.DefaultTimeoutMs,
                cancellationToken);
            deferredByAuthentication = result.BlockedByGitHubAuthentication;
            if (result.IsSuccess)
            {
                version = result.StdOut.Trim();
                error = string.Empty;
                return true;
            }

            version = string.Empty;
            error = SanitizeUiDiagnostic(
                string.IsNullOrWhiteSpace(result.StdErr) ? result.StdOut : result.StdErr);
            return false;
        }

        internal static bool IsAuthenticated(out string error)
        {
            return IsAuthenticated(CancellationToken.None, out error);
        }

        internal static bool IsAuthenticated(CancellationToken cancellationToken, out string error)
        {
            return IsAuthenticated(
                cancellationToken,
                out error,
                out _);
        }

        internal static bool IsAuthenticated(
            CancellationToken cancellationToken,
            out string error,
            out bool deferredByAuthentication)
        {
            var result = CliCommandRunner.Run(
                "gh",
                BuildAuthenticationStatusArguments(),
                GitUtility.ProjectRoot,
                CliCommandRunner.DefaultTimeoutMs,
                cancellationToken);
            deferredByAuthentication = result.BlockedByGitHubAuthentication;
            if (result.IsSuccess)
            {
                error = string.Empty;
                return true;
            }

            error = SanitizeUiDiagnostic(
                string.IsNullOrWhiteSpace(result.StdErr) ? result.StdOut : result.StdErr);
            return false;
        }

        internal static IReadOnlyList<string> BuildAuthenticationArguments()
        {
            return new[]
            {
                "auth",
                "login",
                "--hostname",
                GitHubHost,
                "--git-protocol",
                "https",
                "--web",
                "--clipboard"
            };
        }

        internal static IReadOnlyList<string> BuildAuthenticationStatusArguments()
        {
            return new[]
            {
                "api",
                "user",
                "--hostname",
                GitHubHost,
                "--jq",
                ".login"
            };
        }

        internal static bool SupportsClipboardAuthentication(string versionOutput)
        {
            if (string.IsNullOrWhiteSpace(versionOutput))
                return false;

            Match match = GhVersionRegex.Match(versionOutput);
            return match.Success &&
                   Version.TryParse(match.Groups["version"].Value, out Version version) &&
                   version >= MinimumClipboardAuthenticationVersion;
        }

        internal static string GetAuthenticatedUsername()
        {
            var result = CliCommandRunner.Run(
                "gh",
                BuildApiArguments("user --jq .login"),
                GitUtility.ProjectRoot);
            return result.IsSuccess ? result.StdOut.Trim() : string.Empty;
        }

        internal static string BuildApiArguments(string arguments)
        {
            string value = arguments?.Trim() ?? string.Empty;
            return string.IsNullOrEmpty(value)
                ? $"api --hostname {GitHubHost}"
                : $"api {value} --hostname {GitHubHost}";
        }

        internal static string GetRepositoryCacheIdentity(string location)
        {
            if (string.IsNullOrWhiteSpace(location))
                return string.Empty;

            string value = location.Trim();
            if (TryGetLocalRepositoryIdentity(value, out string localIdentity))
                return "path:" + localIdentity;

            Match scpMatch = ScpRepositoryRegex.Match(value);
            if (scpMatch.Success)
            {
                string user = scpMatch.Groups["user"].Value;
                string host = scpMatch.Groups["host"].Value.ToLowerInvariant();
                string path = scpMatch.Groups["path"].Value;
                return $"scp:{user}@{host}:{path}";
            }

            if (Uri.TryCreate(value, UriKind.Absolute, out Uri uri) &&
                !uri.IsFile &&
                !string.IsNullOrWhiteSpace(uri.Host))
            {
                string scheme = uri.Scheme.ToLowerInvariant();
                string host = uri.IdnHost.ToLowerInvariant();
                string userInfo = string.IsNullOrEmpty(uri.UserInfo) ? string.Empty : uri.UserInfo + "@";
                string port = uri.IsDefaultPort ? string.Empty : ":" + uri.Port;
                string remainder = uri.GetComponents(
                    UriComponents.PathAndQuery | UriComponents.Fragment,
                    UriFormat.UriEscaped);
                return $"uri:{scheme}://{userInfo}{host}{port}{remainder}";
            }

            // Unknown repository syntaxes remain case-sensitive. Treating them as
            // filesystem paths or case-folding them could merge distinct remotes.
            return "literal:" + value;
        }

        private static bool TryGetLocalRepositoryIdentity(string value, out string identity)
        {
            identity = string.Empty;
            string path = value;

            if (Uri.TryCreate(value, UriKind.Absolute, out Uri fileUri) && fileUri.IsFile)
            {
                path = fileUri.LocalPath;
            }
            else if (!Path.IsPathRooted(value) &&
                     !value.StartsWith("./", StringComparison.Ordinal) &&
                     !value.StartsWith("../", StringComparison.Ordinal) &&
                     !value.StartsWith(@".\", StringComparison.Ordinal) &&
                     !value.StartsWith(@"..\", StringComparison.Ordinal))
            {
                return false;
            }

            try
            {
                string fullPath = Path.IsPathRooted(path)
                    ? Path.GetFullPath(path)
                    : Path.GetFullPath(Path.Combine(GitUtility.ProjectRoot, path));
                fullPath = TrimTrailingDirectorySeparators(fullPath);

                // Windows paths are case-insensitive by platform contract. Unix
                // paths (including case-sensitive macOS volumes) must retain case.
                identity = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                    ? fullPath.ToUpperInvariant()
                    : fullPath;
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static string TrimTrailingDirectorySeparators(string path)
        {
            string root = Path.GetPathRoot(path) ?? string.Empty;
            int length = path.Length;
            while (length > root.Length &&
                   (path[length - 1] == Path.DirectorySeparatorChar ||
                    path[length - 1] == Path.AltDirectorySeparatorChar))
            {
                length--;
            }

            return length == path.Length ? path : path.Substring(0, length);
        }

        internal static List<GitHubRepo> ParseRepoJson(string json)
        {
            var repos = new List<GitHubRepo>();
            if (string.IsNullOrEmpty(json))
            {
                return repos;
            }

            var wrapper = JsonUtility.FromJson<RepoListWrapper>("{\"items\":" + json + "}");
            if (wrapper?.items == null)
            {
                return repos;
            }

            foreach (var repoJson in wrapper.items)
            {
                repos.Add(new GitHubRepo
                {
                    NodeId = repoJson.node_id,
                    Name = repoJson.name,
                    Owner = repoJson.owner != null ? repoJson.owner.login : string.Empty,
                    Url = GetCloneUrl(repoJson),
                    DefaultBranch = repoJson.defaultBranchRef != null ? repoJson.defaultBranchRef.name : repoJson.default_branch,
                    IsPrivate = repoJson.isPrivate || repoJson.@private,
                    Description = repoJson.description,
                    UpdatedAt = repoJson.updated_at
                });
            }

            return repos;
        }

        internal static List<GitHubRepo> ParseSearchJson(string json)
        {
            var repos = new List<GitHubRepo>();
            if (string.IsNullOrEmpty(json))
            {
                return repos;
            }

            var wrapper = JsonUtility.FromJson<SearchResultWrapper>(json);
            if (wrapper?.items == null)
            {
                return repos;
            }

            foreach (var repoJson in wrapper.items)
            {
                repos.Add(new GitHubRepo
                {
                    NodeId = repoJson.node_id,
                    Name = repoJson.name,
                    Owner = repoJson.owner != null ? repoJson.owner.login : string.Empty,
                    Url = GetCloneUrl(repoJson),
                    DefaultBranch = repoJson.default_branch,
                    IsPrivate = repoJson.@private,
                    Description = repoJson.description,
                    UpdatedAt = repoJson.updated_at
                });
            }

            return repos;
        }

        internal static int ParseSearchTotalCount(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return 0;

            try
            {
                var wrapper = JsonUtility.FromJson<SearchResultWrapper>(json);
                return wrapper?.total_count ?? 0;
            }
            catch
            {
                return 0;
            }
        }

        internal static string BuildRepoListError(string message, CommandResult result)
        {
            return BuildError(message, result);
        }

        internal static bool TryParseGitHubRepo(string url, out string owner, out string repo)
        {
            owner = string.Empty;
            repo = string.Empty;

            if (string.IsNullOrWhiteSpace(url))
            {
                return false;
            }

            var match = GitHubRepoRegex.Match(url.Trim());
            if (!match.Success)
            {
                return false;
            }

            owner = match.Groups["owner"].Value;
            repo = match.Groups["repo"].Value;
            return !string.IsNullOrEmpty(owner) && !string.IsNullOrEmpty(repo);
        }

        internal static string DerivePackageNameSuggestion(string owner, string repoName)
        {
            if (string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(repoName))
            {
                return string.Empty;
            }

            if (GitUtility.IsValidUpmPackageName(repoName))
            {
                return repoName;
            }

            string sanitizedOwner = SanitizeForPackageName(owner);
            string sanitizedRepo = SanitizeForPackageName(repoName);

            if (string.IsNullOrEmpty(sanitizedOwner) || string.IsNullOrEmpty(sanitizedRepo))
            {
                return string.Empty;
            }

            return $"com.{sanitizedOwner}.{sanitizedRepo}";
        }

        private static string SanitizeForPackageName(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
            {
                return string.Empty;
            }

            var sb = new System.Text.StringBuilder();
            foreach (char c in input.ToLowerInvariant())
            {
                if (c >= 'a' && c <= 'z' || c >= '0' && c <= '9')
                {
                    sb.Append(c);
                }
            }
            return sb.ToString();
        }

        internal static bool IsNotFoundResult(CommandResult result)
        {
            if (result == null)
                return false;

            string combined = $"{result.StdOut}\n{result.StdErr}".ToLowerInvariant();
            return combined.Contains("not found") || combined.Contains("404");
        }

        internal static string SanitizeUiDiagnostic(string value)
        {
            string sanitized = GitUtility.RedactCredentials(value ?? string.Empty).Trim();
            if (sanitized.Length <= MaxUiDiagnosticCharacters)
                return sanitized;

            int retainedLength = MaxUiDiagnosticCharacters - DiagnosticTruncationNotice.Length;
            return sanitized.Substring(0, retainedLength) + DiagnosticTruncationNotice;
        }

        private static string GetCloneUrl(RepoJson repo)
        {
            if (!string.IsNullOrWhiteSpace(repo.clone_url))
                return repo.clone_url;

            // Git can clone a repository's normal GitHub page URL. The REST `url`
            // field is deliberately not used because it points at api.github.com.
            return repo.html_url ?? string.Empty;
        }

        private static string BuildError(string message, CommandResult result)
        {
            string summary = string.IsNullOrWhiteSpace(message) ? "GitHub request failed" : message.Trim();
            if (result == null)
                return SanitizeUiDiagnostic(summary);

            string detail = string.IsNullOrWhiteSpace(result.StdErr) ? result.StdOut : result.StdErr;
            detail = SanitizeUiDiagnostic(detail);
            return SanitizeUiDiagnostic(
                string.IsNullOrWhiteSpace(detail) ? summary : $"{summary}: {detail}");
        }

        [Serializable]
        private sealed class RepoListWrapper
        {
            public RepoJson[] items;
        }

        [Serializable]
        private sealed class SearchResultWrapper
        {
            public int total_count;
            public RepoJson[] items;
        }

        [Serializable]
        private sealed class RepoJson
        {
            public string node_id;
            public string name;
            public OwnerJson owner;
            public string url;
            public string html_url;
            public string clone_url;
            public DefaultBranchRefJson defaultBranchRef;
            public bool isPrivate;
            public bool @private;
            public string default_branch;
            public string description;
            public string updated_at;
        }

        [Serializable]
        private sealed class OwnerJson
        {
            public string login;
        }

        [Serializable]
        private sealed class DefaultBranchRefJson
        {
            public string name;
        }
    }
}
