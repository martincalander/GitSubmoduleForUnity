using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using UnityEngine;

namespace MartinCalander.GitPackageManager.Editor
{
    [Serializable]
    internal sealed class PackageJsonMetadata
    {
        public string name;
        public string version;
    }

    internal sealed class SubmoduleRemovalAssessment
    {
        internal string Path = string.Empty;
        internal bool IsInitialized;
        internal bool HasWorkingTreeChanges;
        internal bool HasConflicts;
        internal bool HasLocalOnlyCommits;
        internal bool HasParentChanges;
        internal bool HasUnverifiedWorktreeContents;
        internal int LocalOnlyCommitCount;
        internal string HeadCommit = string.Empty;
        internal string ParentStatus = string.Empty;
        internal string WorktreeStatus = string.Empty;

        internal bool IsSafe =>
            !HasWorkingTreeChanges &&
            !HasConflicts &&
            !HasLocalOnlyCommits &&
            !HasParentChanges;

        internal string BuildWarning()
        {
            var risks = new List<string>();
            if (HasConflicts)
                risks.Add("unresolved merge conflicts");
            if (HasWorkingTreeChanges)
                risks.Add("modified, untracked, or ignored files");
            if (HasUnverifiedWorktreeContents)
                risks.Add("files in a package directory that is not an initialized submodule worktree");
            if (HasLocalOnlyCommits)
                risks.Add($"{Math.Max(1, LocalOnlyCommitCount)} commit(s) not present on any remote");
            if (HasParentChanges)
                risks.Add("an uncommitted or staged submodule revision in the parent repository");

            return risks.Count == 0
                ? string.Empty
                : "Removing this package would discard " + string.Join(", ", risks) + ".";
        }
    }

    internal sealed class SubmoduleUpdatePlan
    {
        internal string Path = string.Empty;
        internal string StartingCommit = string.Empty;
        internal string ExpectedTargetCommit = string.Empty;
        internal string ExpectedRepositoryUrl = string.Empty;
        internal string ExpectedBranch = string.Empty;
    }

    internal sealed class AddSubmodulePlan
    {
        internal string Path = string.Empty;
        internal string ExpectedUrl = string.Empty;
        internal string ExpectedBranch = string.Empty;
        internal string ExpectedSubmoduleName = string.Empty;
        internal bool ReuseExistingMetadata;
        internal bool GitModulesExisted;
        internal byte[] GitModulesContents = Array.Empty<byte>();
    }

    internal static class GitUtility
    {
        private const int MaxPackageJsonLength = 1024 * 1024;
        private const int MaxPackageNameLength = 214;
        private const int MaxBranchNameLength = 1024;
        private const int MaxRepositoryUrlLength = 4096;
        private const int MaxDisplayedRepositoryUrlLength = 160;
        private static readonly Encoding StrictUtf8Encoding = new UTF8Encoding(false, true);
        private static readonly Regex PackageNameRegex = new Regex(@"^[a-z0-9]+(?:[._-][a-z0-9]+)+$", RegexOptions.Compiled);
        private static readonly Regex BranchNameRegex = new Regex(@"^[A-Za-z0-9][A-Za-z0-9._/-]*$", RegexOptions.Compiled);
        private static readonly Regex SubmoduleStatusRegex = new Regex(@"^[ +\-U]?([0-9a-f]{7,64})\s+([^\s]+)", RegexOptions.Multiline | RegexOptions.Compiled);
        private static readonly Regex UriUserInfoRegex = new Regex(@"(?<scheme>[a-z][a-z0-9+.-]*://)[^\s/@]+@", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex SensitiveParameterRegex = new Regex(
            @"(?<key>(?:access[_-]?token|auth|authorization|credential|key|password|passwd|secret|token)=)[^&#\s'""]+",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex BearerTokenRegex = new Regex(
            @"(?<prefix>\bBearer\s+)[A-Za-z0-9._~+/=-]+",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex ScpRepositoryRegex = new Regex(
            @"^(?:[A-Za-z0-9._-]+@)?[A-Za-z0-9.-]+:[^\s:][^\s]*$",
            RegexOptions.Compiled);

        private static string cachedProjectRoot;
        private static string projectRootOverride;
        private static string gitExecutableOverride;
        [ThreadStatic] private static bool commandTerminationUnconfirmed;

        internal static string ProjectRoot
        {
            get
            {
                if (!string.IsNullOrEmpty(projectRootOverride))
                    return projectRootOverride;

                if (cachedProjectRoot == null)
                {
                    var parent = Directory.GetParent(Application.dataPath);
                    cachedProjectRoot = parent != null ? parent.FullName : Environment.CurrentDirectory;
                }

                return cachedProjectRoot;
            }
        }

        internal static string GitExecutable => gitExecutableOverride ?? "git";

        internal static IDisposable OverrideProjectRootForTests(string projectRoot)
        {
            string previous = projectRootOverride;
            projectRootOverride = string.IsNullOrWhiteSpace(projectRoot)
                ? null
                : Path.GetFullPath(projectRoot);
            return new DisposableAction(() => projectRootOverride = previous);
        }

        internal static bool IsValidPackageName(string packageName)
        {
            return packageName != null &&
                   packageName.Length <= MaxPackageNameLength &&
                   !string.IsNullOrWhiteSpace(packageName) &&
                   string.Equals(packageName, packageName.Trim(), StringComparison.Ordinal) &&
                   PackageNameRegex.IsMatch(packageName);
        }

        internal static bool IsValidUpmPackageName(string packageName)
        {
            if (!IsValidPackageName(packageName))
                return false;

            // Unity packages use reverse-domain notation, for example
            // com.company.package. Keep the general package-name validator useful
            // for parsing, but require all three components for managed paths and
            // discovered UPM package manifests.
            return packageName.Split('.').Length >= 3;
        }

        internal static bool IsValidBranchName(string branch)
        {
            if (branch != null && branch.Length > MaxBranchNameLength)
                return false;

            if (string.IsNullOrWhiteSpace(branch))
                return true;

            string value = branch.Trim();
            // Git reserves `.` for submodules that follow the current branch
            // name of the superproject. It is not a normal ref name, but it is
            // a valid value for submodule.<name>.branch.
            if (string.Equals(value, ".", StringComparison.Ordinal))
                return true;

            if (!BranchNameRegex.IsMatch(value) ||
                value.Contains("..") ||
                value.Contains("@{") ||
                value.Contains("//") ||
                value.EndsWith(".", StringComparison.Ordinal) ||
                value.EndsWith("/", StringComparison.Ordinal))
            {
                return false;
            }

            foreach (string segment in value.Split('/'))
            {
                if (segment.StartsWith(".", StringComparison.Ordinal) ||
                    segment.EndsWith(".lock", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }

            return true;
        }

        internal static bool IsValidRepositoryUrl(string url)
        {
            if (url == null ||
                url.Length > MaxRepositoryUrlLength ||
                string.IsNullOrWhiteSpace(url))
                return false;

            string value = url.Trim();
            if (value.StartsWith("-", StringComparison.Ordinal) ||
                value.IndexOfAny(new[] { '\0', '\r', '\n', '"' }) >= 0)
            {
                return false;
            }

            if (IsLocalRepositoryUrl(value))
                return true;

            if (value.IndexOf("://", StringComparison.Ordinal) < 0 && ScpRepositoryRegex.IsMatch(value))
                return true;

            if (!Uri.TryCreate(value, UriKind.Absolute, out Uri uri) ||
                !string.IsNullOrEmpty(uri.Query) ||
                !string.IsNullOrEmpty(uri.Fragment) ||
                string.IsNullOrEmpty(uri.Host))
                return false;

            bool isHttps = uri.Scheme == Uri.UriSchemeHttps;
            bool isSsh = string.Equals(uri.Scheme, "ssh", StringComparison.OrdinalIgnoreCase);
            // Plain HTTP and the unauthenticated git:// protocol are vulnerable
            // to interception and repository substitution in transit.
            if (!isHttps && !isSsh)
                return false;

            if (isHttps)
                return string.IsNullOrEmpty(uri.UserInfo);

            // SSH user names (normally `git`) are part of addressing. Passwords are not.
            return string.IsNullOrEmpty(uri.UserInfo) || uri.UserInfo.IndexOf(':') < 0;
        }

        internal static bool TryGetRepositoryWebUrl(string repositoryUrl, out string webUrl)
        {
            webUrl = string.Empty;
            if (!IsValidRepositoryUrl(repositoryUrl))
                return false;

            string value = repositoryUrl.Trim();
            if (Uri.TryCreate(value, UriKind.Absolute, out Uri uri) &&
                (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            {
                webUrl = uri.GetLeftPart(UriPartial.Path);
                if (webUrl.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
                    webUrl = webUrl.Substring(0, webUrl.Length - 4);
                return true;
            }

            if (GitHubUtility.TryParseGitHubRepo(value, out string owner, out string repository))
            {
                webUrl = $"https://github.com/{owner}/{repository}";
                return true;
            }

            return false;
        }

        internal static string FormatRepositoryUrlForDisplay(string repositoryUrl)
        {
            string display = RedactCredentials(repositoryUrl ?? string.Empty)
                .Replace('\r', ' ')
                .Replace('\n', ' ')
                .Trim();
            if (display.Length <= MaxDisplayedRepositoryUrlLength)
                return display;

            return display.Substring(0, MaxDisplayedRepositoryUrlLength - 1) + "…";
        }

        internal static bool IsPackagePath(string path)
        {
            if (path == null || path.Length > "Packages/".Length + MaxPackageNameLength)
                return false;

            string normalized = NormalizePath(path);
            if (!normalized.StartsWith("Packages/", StringComparison.Ordinal))
                return false;

            string packageName = normalized.Substring("Packages/".Length);
            return packageName.IndexOf('/') < 0 && IsValidUpmPackageName(packageName);
        }

        internal static bool TryReadPackageName(string packageJsonPath, out string packageName)
        {
            packageName = string.Empty;
            if (!File.Exists(packageJsonPath))
            {
                return false;
            }

            try
            {
                string content = File.ReadAllText(packageJsonPath);
                return TryReadPackageNameFromJson(content, out packageName);
            }
            catch
            {
                return false;
            }
        }

        internal static bool TryReadValidPackageManifest(
            string packageJsonPath,
            out string packageName,
            out string error,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            packageName = string.Empty;
            error = string.Empty;
            if (string.IsNullOrWhiteSpace(packageJsonPath))
            {
                error = "package.json path is missing.";
                return false;
            }

            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var fileInfo = new FileInfo(packageJsonPath);
                if (!fileInfo.Exists)
                {
                    error = "package.json does not exist.";
                    return false;
                }

                FileAttributes attributes = fileInfo.Attributes;
                if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                {
                    error =
                        "package.json must be a regular file inside the package, not a symbolic link, junction, or other reparse point.";
                    return false;
                }

                if (fileInfo.Length > MaxPackageJsonLength)
                {
                    error = "package.json exceeds the 1 MiB validation limit.";
                    return false;
                }

                var content = new StringBuilder((int)Math.Min(fileInfo.Length, MaxPackageJsonLength));
                using (var stream = new FileStream(
                           packageJsonPath,
                           FileMode.Open,
                           FileAccess.Read,
                           FileShare.Read))
                using (var reader = new StreamReader(stream, StrictUtf8Encoding, true, 4096, false))
                {
                    var buffer = new char[4096];
                    int read;
                    while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (content.Length + read > MaxPackageJsonLength)
                        {
                            error = "package.json exceeds the 1 MiB validation limit.";
                            return false;
                        }

                        content.Append(buffer, 0, read);
                    }
                }

                cancellationToken.ThrowIfCancellationRequested();
                return TryReadValidPackageManifestFromJson(content.ToString(), out packageName, out error);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                error = GitHubUtility.SanitizeUiDiagnostic(
                    "package.json could not be read safely: " + exception.Message);
                return false;
            }
        }

        internal static bool TryReadPackageNameFromJson(string packageJsonContent, out string packageName)
        {
            packageName = string.Empty;
            if (string.IsNullOrWhiteSpace(packageJsonContent))
            {
                return false;
            }

            try
            {
                var metadata = JsonUtility.FromJson<PackageJsonMetadata>(packageJsonContent);
                if (metadata == null || string.IsNullOrWhiteSpace(metadata.name))
                {
                    return false;
                }

                packageName = metadata.name.Trim();
                return !string.IsNullOrEmpty(packageName);
            }
            catch
            {
                return false;
            }
        }

        internal static bool TryReadValidPackageManifestFromJson(
            string content,
            out string packageName,
            out string error)
        {
            packageName = string.Empty;
            error = string.Empty;

            if (content == null)
            {
                error = "package.json is empty.";
                return false;
            }

            if (content.Length > MaxPackageJsonLength)
            {
                error = "package.json exceeds the 1 MiB validation limit.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(content))
            {
                error = "package.json is empty.";
                return false;
            }

            string trimmedContent = content.Trim();
            if (trimmedContent.Length < 2 ||
                trimmedContent[0] != '{' ||
                trimmedContent[trimmedContent.Length - 1] != '}')
            {
                error = "package.json root must be a JSON object.";
                return false;
            }

            PackageJsonMetadata metadata;
            try
            {
                metadata = JsonUtility.FromJson<PackageJsonMetadata>(trimmedContent);
            }
            catch (Exception exception)
            {
                error = GitHubUtility.SanitizeUiDiagnostic(
                    "package.json could not be parsed: " + exception.Message);
                return false;
            }

            if (metadata == null)
            {
                error = "package.json could not be parsed as a package manifest.";
                return false;
            }

            if (!IsValidUpmPackageName(metadata.name))
            {
                error = "package.json must contain a valid reverse-domain UPM package name.";
                return false;
            }

            if (!IsValidSemanticVersion(metadata.version))
            {
                error = "package.json must contain a valid SemVer 2.0 version.";
                return false;
            }

            packageName = metadata.name;
            return true;
        }

        internal static bool IsValidSemanticVersion(string version)
        {
            if (string.IsNullOrWhiteSpace(version) ||
                !string.Equals(version, version.Trim(), StringComparison.Ordinal))
            {
                return false;
            }

            string versionWithoutBuild = version;
            string buildMetadata = null;
            int buildSeparator = version.IndexOf('+');
            if (buildSeparator >= 0)
            {
                if (version.IndexOf('+', buildSeparator + 1) >= 0)
                    return false;

                versionWithoutBuild = version.Substring(0, buildSeparator);
                buildMetadata = version.Substring(buildSeparator + 1);
                if (!AreValidSemanticVersionIdentifiers(buildMetadata, false))
                    return false;
            }

            string coreVersion = versionWithoutBuild;
            string prerelease = null;
            int prereleaseSeparator = versionWithoutBuild.IndexOf('-');
            if (prereleaseSeparator >= 0)
            {
                coreVersion = versionWithoutBuild.Substring(0, prereleaseSeparator);
                prerelease = versionWithoutBuild.Substring(prereleaseSeparator + 1);
                if (!AreValidSemanticVersionIdentifiers(prerelease, true))
                    return false;
            }

            string[] coreIdentifiers = coreVersion.Split('.');
            return coreIdentifiers.Length == 3 &&
                   IsValidNumericIdentifier(coreIdentifiers[0]) &&
                   IsValidNumericIdentifier(coreIdentifiers[1]) &&
                   IsValidNumericIdentifier(coreIdentifiers[2]);
        }

        private static bool AreValidSemanticVersionIdentifiers(string value, bool rejectLeadingZeroNumericIdentifiers)
        {
            if (string.IsNullOrEmpty(value))
                return false;

            foreach (string identifier in value.Split('.'))
            {
                if (string.IsNullOrEmpty(identifier))
                    return false;

                bool isNumeric = true;
                foreach (char character in identifier)
                {
                    bool isDigit = character >= '0' && character <= '9';
                    bool isLetter = (character >= 'a' && character <= 'z') ||
                                    (character >= 'A' && character <= 'Z');
                    if (!isDigit && !isLetter && character != '-')
                        return false;

                    if (!isDigit)
                        isNumeric = false;
                }

                if (rejectLeadingZeroNumericIdentifiers &&
                    isNumeric &&
                    identifier.Length > 1 &&
                    identifier[0] == '0')
                {
                    return false;
                }
            }

            return true;
        }

        private static bool IsValidNumericIdentifier(string identifier)
        {
            if (string.IsNullOrEmpty(identifier) ||
                (identifier.Length > 1 && identifier[0] == '0'))
            {
                return false;
            }

            foreach (char character in identifier)
            {
                if (character < '0' || character > '9')
                    return false;
            }

            return true;
        }

        internal static bool IsGitAvailable(out string version, out string error)
        {
            return IsGitAvailable(out version, out error, CancellationToken.None);
        }

        internal static bool IsGitAvailable(
            out string version,
            out string error,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            gitExecutableOverride = null;
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX) &&
                CliCommandRunner.TryResolveCommand("git", out string resolvedGitPath) &&
                string.Equals(resolvedGitPath, "/usr/bin/git", StringComparison.Ordinal))
            {
                var developerToolsResult = CliCommandRunner.Run(
                    "xcode-select",
                    "-p",
                    ProjectRoot,
                    5000,
                    cancellationToken);
                if (!developerToolsResult.IsSuccess)
                {
                    foreach (string alternateGitPath in new[] { "/opt/homebrew/bin/git", "/usr/local/bin/git" })
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (!File.Exists(alternateGitPath))
                            continue;

                        var alternateResult = CliCommandRunner.Run(
                            alternateGitPath,
                            "--version",
                            ProjectRoot,
                            5000,
                            cancellationToken);
                        if (!alternateResult.IsSuccess ||
                            !TryRequireCompleteStructuralOutput(
                                alternateResult,
                                "Git version detection",
                                out _))
                            continue;

                        gitExecutableOverride = alternateGitPath;
                        version = alternateResult.StdOut.Trim();
                        error = string.Empty;
                        return true;
                    }

                    version = string.Empty;
                    error = "Apple Command Line Tools are not installed. Use the Install Git button to open the macOS installer.";
                    return false;
                }
            }

            var result = RunGit(
                "--version",
                ProjectRoot,
                CliCommandRunner.DefaultTimeoutMs,
                cancellationToken);
            string versionOutputError = string.Empty;
            if (result.IsSuccess &&
                TryRequireCompleteStructuralOutput(
                    result,
                    "Git version detection",
                    out versionOutputError))
            {
                version = result.StdOut.Trim();
                error = string.Empty;
                return true;
            }

            version = string.Empty;
            error = !string.IsNullOrWhiteSpace(versionOutputError)
                ? versionOutputError
                : string.IsNullOrWhiteSpace(result.StdErr) ? result.StdOut : result.StdErr;
            return false;
        }

        // ── Submodule Operations ──

        internal static bool TryGetSubmodules(out List<GitPackageInfo> submodules, out string error)
        {
            return TryGetSubmodules(out submodules, out error, CancellationToken.None);
        }

        internal static bool TryGetSubmodules(
            out List<GitPackageInfo> submodules,
            out string error,
            CancellationToken cancellationToken)
        {
            submodules = new List<GitPackageInfo>();
            error = string.Empty;

            cancellationToken.ThrowIfCancellationRequested();
            string root = ProjectRoot;
            if (!TryValidateProjectGitRoot(out error, cancellationToken))
                return false;

            string gitModulesPath = Path.Combine(root, ".gitmodules");
            if (!File.Exists(gitModulesPath))
            {
                return true;
            }

            var statusResult = RunGit(
                "submodule status",
                root,
                CliCommandRunner.DefaultTimeoutMs,
                cancellationToken);
            if (!statusResult.IsSuccess)
            {
                error = BuildCommandError("Failed to read submodule status", statusResult);
                return false;
            }

            if (!TryRequireCompleteStructuralOutput(
                    statusResult,
                    "Submodule status inspection",
                    out error))
                return false;

            string statusOutput = statusResult.StdOut;

            var configResult = RunGit(
                "config --null --file .gitmodules --list",
                root,
                CliCommandRunner.DefaultTimeoutMs,
                cancellationToken);
            if (!TryRequireCompleteStructuralOutput(
                    configResult,
                    ".gitmodules inspection",
                    out error))
                return false;

            if (!configResult.IsSuccess)
            {
                if (configResult.ExitCode == 1 && string.IsNullOrWhiteSpace(configResult.StdErr))
                {
                    return true;
                }

                error = BuildCommandError("Failed to read .gitmodules", configResult);
                return false;
            }

            if (!TryParseNullConfigList(configResult.StdOut, out var config, out error))
                return false;

            var commitMap = ParseSubmoduleCommitMap(statusOutput);
            var names = ExtractSubmoduleNamesFromConfig(config);

            foreach (string name in names)
            {
                cancellationToken.ThrowIfCancellationRequested();
                config.TryGetValue($"submodule.{name}.path", out string rawPath);
                config.TryGetValue($"submodule.{name}.url", out string url);
                config.TryGetValue($"submodule.{name}.branch", out string branch);
                string path = NormalizePath(rawPath ?? string.Empty);
                if (!IsPackagePath(path))
                    continue;

                var info = new GitPackageInfo
                {
                    Name = name,
                    Path = path,
                    Url = url ?? string.Empty,
                    Branch = branch ?? string.Empty,
                    CommitHash = commitMap.TryGetValue(path, out string commit) ? commit : string.Empty,
                    IsInitialized = IsSubmoduleInitialized(statusOutput, path)
                };

                string packageJsonPath = Path.Combine(root, path, "package.json");
                info.HasPackageJson = File.Exists(packageJsonPath);
                if (info.HasPackageJson &&
                    TryReadValidPackageManifest(
                        packageJsonPath,
                        out string packageName,
                        out _,
                        cancellationToken))
                {
                    info.PackageName = packageName;
                }
                else if (path.Replace("\\", "/").StartsWith("Packages/", StringComparison.Ordinal))
                {
                    info.PackageName = Path.GetFileName(path);
                }

                submodules.Add(info);
            }

            return true;
        }

        internal static bool TryAddSubmodule(
            string url,
            string path,
            string branch,
            out string error,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryPrepareAddSubmodule(
                    url,
                    path,
                    out AddSubmodulePlan plan,
                    out error,
                    cancellationToken))
                return false;

            if (!TryBuildAddSubmoduleArguments(url, path, branch, plan.ReuseExistingMetadata, out string args, out error))
                return false;

            plan.ExpectedBranch = branch?.Trim() ?? string.Empty;

            var result = RunGit(args, ProjectRoot, 120000, cancellationToken);
            if (!result.IsSuccess)
            {
                error = BuildCommandError("Failed to add submodule", result);
                if (result.TerminationConfirmed &&
                    !TryCleanupFailedAdd(plan, out string cleanupWarning, cancellationToken) &&
                    !string.IsNullOrWhiteSpace(cleanupWarning))
                {
                    error += " Cleanup warning: " + cleanupWarning;
                }
                return false;
            }

            if (TryVerifyAddedSubmodule(plan, url, branch, out error, cancellationToken))
                return true;

            if (!TryCleanupFailedAdd(plan, out string rollbackWarning, cancellationToken) &&
                !string.IsNullOrWhiteSpace(rollbackWarning))
            {
                error += " Rollback warning: " + rollbackWarning;
            }
            return false;
        }

        internal static bool TryPrepareAddSubmodule(
            string url,
            string path,
            out bool reuseExistingMetadata,
            out string error,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            bool success = TryPrepareAddSubmodule(
                url,
                path,
                out AddSubmodulePlan plan,
                out error,
                cancellationToken);
            reuseExistingMetadata = success && plan != null && plan.ReuseExistingMetadata;
            return success;
        }

        internal static bool TryPrepareAddSubmodule(
            string url,
            string path,
            out AddSubmodulePlan plan,
            out string error,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            plan = null;
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryValidateExistingRepositoryUrl(url, "The repository URL selected for this add operation", out error))
                return false;

            if (!TryPrepareAddSubmodule(path, out error, cancellationToken))
                return false;

            string normalizedPath = NormalizePath(path);
            string gitModulesPath = Path.Combine(ProjectRoot, ".gitmodules");
            AddSubmodulePlan preparedPlan;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                bool gitModulesExisted = File.Exists(gitModulesPath);
                preparedPlan = new AddSubmodulePlan
                {
                    Path = normalizedPath,
                    ExpectedUrl = url?.Trim() ?? string.Empty,
                    ExpectedSubmoduleName = normalizedPath,
                    GitModulesExisted = gitModulesExisted,
                    GitModulesContents = gitModulesExisted
                        ? File.ReadAllBytes(gitModulesPath)
                        : Array.Empty<byte>()
                };
                cancellationToken.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                error = $"Failed to capture the pre-add .gitmodules state: {ex.Message}";
                return false;
            }
            if (!TryResolveSubmoduleGitDir(
                    normalizedPath,
                    out string moduleGitDir,
                    out error,
                    cancellationToken))
                return false;

            if (!TryValidateSubmoduleMetadataPath(moduleGitDir, out error, cancellationToken))
                return false;

            var localUrlResult = RunGit(
                $"config --get {Quote("submodule." + normalizedPath + ".url")}",
                ProjectRoot,
                5000,
                cancellationToken);
            if (!TryRequireCompleteStructuralOutput(
                    localUrlResult,
                    "Existing local submodule URL inspection",
                    out error))
                return false;

            if (localUrlResult.IsSuccess)
            {
                string localUrl = localUrlResult.StdOut?.Trim() ?? string.Empty;
                if (!TryValidateExistingRepositoryUrl(
                        localUrl,
                        "The existing local submodule URL",
                        out error) ||
                    !AreRepositoryUrlsEquivalent(localUrl, url))
                {
                    if (string.IsNullOrWhiteSpace(error))
                    {
                        error =
                            $"Existing local Git configuration for {normalizedPath} points to a different repository. " +
                            "Repair or remove the stale submodule URL before adding this package.";
                    }
                    return false;
                }
            }
            else if (localUrlResult.ExitCode != 1)
            {
                error = BuildCommandError("Failed to inspect existing local submodule URL configuration", localUrlResult);
                return false;
            }

            if (!Directory.Exists(moduleGitDir))
            {
                plan = preparedPlan;
                error = string.Empty;
                return true;
            }

            var originResult = RunGit(
                $"--git-dir {Quote(moduleGitDir)} --work-tree {Quote(Path.Combine(ProjectRoot, normalizedPath))} config --get remote.origin.url",
                ProjectRoot,
                5000,
                cancellationToken);
            if (!TryRequireCompleteStructuralOutput(
                    originResult,
                    "Recoverable submodule origin inspection",
                    out error))
                return false;

            if (!originResult.IsSuccess || string.IsNullOrWhiteSpace(originResult.StdOut))
            {
                error =
                    $"Recoverable Git metadata already exists for {normalizedPath}, but its origin could not be verified. " +
                    "Move or recover that metadata manually before adding a different repository.";
                return false;
            }

            string existingOrigin = originResult.StdOut.Trim();
            if (!TryValidateExistingRepositoryUrl(
                    existingOrigin,
                    "The recoverable submodule metadata origin URL",
                    out error))
                return false;

            if (!AreRepositoryUrlsEquivalent(existingOrigin, url))
            {
                error =
                    $"Recoverable Git metadata for {normalizedPath} belongs to a different repository " +
                    $"({RedactCredentials(existingOrigin)}). Restore it or move it out of the Git modules directory before retrying.";
                return false;
            }

            preparedPlan.ReuseExistingMetadata = true;
            plan = preparedPlan;
            error = string.Empty;
            return true;
        }

        internal static bool TryPrepareAddSubmodule(
            string path,
            out string error,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            string normalizedPath = NormalizePath(path);
            if (!IsPackagePath(normalizedPath))
            {
                error = "Submodules managed by this tool must use a direct, valid Packages/<package-name> path.";
                return false;
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (!TryEnsureParentMutationStateIsSafe(out error, cancellationToken))
                return false;

            if (!TryFindSubmoduleNameForPath(
                    normalizedPath,
                    out _,
                    out bool isRegistered,
                    out error,
                    cancellationToken))
                return false;
            if (isRegistered)
            {
                error = $".gitmodules already registers {normalizedPath}. Repair or remove that existing entry before adding it again.";
                return false;
            }

            var trackedResult = RunGit(
                $"ls-files --error-unmatch -- {Quote(normalizedPath)}",
                ProjectRoot,
                CliCommandRunner.DefaultTimeoutMs,
                cancellationToken);
            if (trackedResult.IsSuccess)
            {
                error = $"The parent repository already tracks {normalizedPath}.";
                return false;
            }

            if (trackedResult.ExitCode != 1)
            {
                error = BuildCommandError("Failed to verify the destination in the parent Git index", trackedResult);
                return false;
            }

            string fullPath = Path.Combine(ProjectRoot, normalizedPath);
            if (Directory.Exists(fullPath) || File.Exists(fullPath))
            {
                error = $"Package path already exists: {normalizedPath}";
                return false;
            }

            error = string.Empty;
            return true;
        }

        internal static bool TryBuildAddSubmoduleArguments(
            string url,
            string path,
            string branch,
            out string arguments,
            out string error)
        {
            return TryBuildAddSubmoduleArguments(url, path, branch, false, out arguments, out error);
        }

        internal static bool TryBuildAddSubmoduleArguments(
            string url,
            string path,
            string branch,
            bool reuseExistingMetadata,
            out string arguments,
            out string error)
        {
            arguments = string.Empty;
            error = string.Empty;
            if (!IsValidRepositoryUrl(url))
            {
                error =
                    "Repository URL is invalid or uses an unsupported transport. Use HTTPS, SSH, or an explicit local path without embedded credentials; plaintext HTTP and Git transports are blocked.";
                return false;
            }

            if (!IsPackagePath(path))
            {
                error = "Submodules managed by this tool must use a direct, valid Packages/<package-name> path.";
                return false;
            }

            if (!IsValidBranchName(branch))
            {
                error = "Branch name is invalid.";
                return false;
            }

            string trimmedUrl = url.Trim();
            string normalizedPath = NormalizePath(path);
            string trimmedBranch = branch?.Trim() ?? string.Empty;
            string prefix = IsLocalRepositoryUrl(trimmedUrl)
                ? "-c protocol.file.allow=always "
                : string.Empty;
            string force = reuseExistingMetadata ? " --force" : string.Empty;
            arguments = string.IsNullOrWhiteSpace(trimmedBranch)
                ? $"{prefix}submodule add{force} {Quote(trimmedUrl)} {Quote(normalizedPath)}"
                : $"{prefix}submodule add{force} -b {Quote(trimmedBranch)} {Quote(trimmedUrl)} {Quote(normalizedPath)}";

            return true;
        }

        internal static bool TryAssessSubmoduleRemoval(
            string path,
            out SubmoduleRemovalAssessment assessment,
            out string error,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            string normalizedPath = NormalizePath(path);
            assessment = new SubmoduleRemovalAssessment { Path = normalizedPath };
            error = string.Empty;
            if (!IsPackagePath(normalizedPath))
            {
                error = "Refusing to inspect a path outside the exact Packages/<package-name> root.";
                return false;
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (!TryEnsureParentMutationStateIsSafe(out error, cancellationToken))
                return false;

            var parentStatus = RunGit(
                $"status --porcelain=v2 --untracked-files=all -- {Quote(normalizedPath)}",
                ProjectRoot,
                CliCommandRunner.DefaultTimeoutMs,
                cancellationToken);
            if (!parentStatus.IsSuccess)
            {
                error = BuildCommandError("Failed to inspect the package in the parent repository", parentStatus);
                return false;
            }

            if (!TryRequireCompleteStructuralOutput(
                    parentStatus,
                    "Parent package status inspection",
                    out error))
                return false;

            assessment.HasParentChanges = !string.IsNullOrWhiteSpace(parentStatus.StdOut);
            assessment.ParentStatus = parentStatus.StdOut ?? string.Empty;

            string packagePath = Path.Combine(ProjectRoot, normalizedPath);
            if (!Directory.Exists(packagePath))
                return true;

            if (!TryInspectExactSubmoduleWorktree(
                    normalizedPath,
                    out bool isExactWorktree,
                    out error,
                    cancellationToken))
                return false;

            assessment.IsInitialized = isExactWorktree;
            if (!assessment.IsInitialized)
            {
                try
                {
                    string[] residualEntries = Directory.GetFileSystemEntries(packagePath);
                    if (residualEntries.Length > 0)
                    {
                        assessment.HasWorkingTreeChanges = true;
                        assessment.HasUnverifiedWorktreeContents = true;
                        assessment.WorktreeStatus = string.Join(
                            "\n",
                            residualEntries
                                .Select(Path.GetFileName)
                                .OrderBy(value => value, StringComparer.Ordinal)) + "\n";
                    }
                }
                catch (Exception ex)
                {
                    error = $"Failed to inspect the deinitialized package directory safely: {ex.Message}";
                    return false;
                }

                return true;
            }

            // Ignored files are still user data. `git rm -f` removes the entire
            // submodule worktree, including ignored build output and local files,
            // so they must participate in both the warning and the post-dialog
            // race check just like tracked and untracked changes.
            var statusResult = RunGit(
                $"-C {Quote(normalizedPath)} status --porcelain=v2 --untracked-files=all --ignored=matching",
                ProjectRoot,
                CliCommandRunner.DefaultTimeoutMs,
                cancellationToken);
            if (!statusResult.IsSuccess)
            {
                error = BuildCommandError("Failed to inspect local package changes", statusResult);
                return false;
            }

            if (!TryRequireCompleteStructuralOutput(
                    statusResult,
                    "Package worktree status inspection",
                    out error))
                return false;

            string status = statusResult.StdOut ?? string.Empty;
            assessment.WorktreeStatus = status;
            assessment.HasWorkingTreeChanges = !string.IsNullOrWhiteSpace(status);
            assessment.HasConflicts = status.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Any(line => line.StartsWith("u ", StringComparison.Ordinal));

            var localCommitsResult = RunGit(
                $"-C {Quote(normalizedPath)} rev-list --count HEAD --not --remotes",
                ProjectRoot,
                CliCommandRunner.DefaultTimeoutMs,
                cancellationToken);
            if (!localCommitsResult.IsSuccess)
            {
                error = BuildCommandError("Failed to check for commits that are not present on a remote", localCommitsResult);
                return false;
            }

            if (!TryRequireCompleteStructuralOutput(
                    localCommitsResult,
                    "Local-only commit inspection",
                    out error))
                return false;

            if (!int.TryParse(localCommitsResult.StdOut.Trim(), out int localOnlyCommitCount))
            {
                error = "Git returned an unexpected result while checking for local-only commits.";
                return false;
            }

            assessment.LocalOnlyCommitCount = localOnlyCommitCount;
            assessment.HasLocalOnlyCommits = localOnlyCommitCount > 0;

            var headResult = RunGit(
                $"-C {Quote(normalizedPath)} rev-parse --verify HEAD",
                ProjectRoot,
                5000,
                cancellationToken);
            if (!headResult.IsSuccess || string.IsNullOrWhiteSpace(headResult.StdOut))
            {
                error = BuildCommandError("Failed to record the package commit before removal", headResult);
                return false;
            }

            if (!TryRequireCompleteStructuralOutput(
                    headResult,
                    "Package commit inspection",
                    out error))
                return false;

            assessment.HeadCommit = headResult.StdOut.Trim();
            return true;
        }

        internal static bool TryRemoveSubmodule(string path, out string error)
        {
            return TryRemoveSubmodule(path, false, out error, out _);
        }

        internal static bool TryRemoveSubmodule(
            string path,
            out string error,
            out GitOperationCompletionOutcome outcome)
        {
            return TryRemoveSubmodule(path, false, out error, out outcome);
        }

        internal static bool TryRemoveSubmodule(string path, bool discardLocalWork, out string error)
        {
            return TryRemoveSubmodule(path, null, discardLocalWork, out error, out _);
        }

        internal static bool TryRemoveSubmodule(
            string path,
            bool discardLocalWork,
            out string error,
            out GitOperationCompletionOutcome outcome)
        {
            return TryRemoveSubmodule(path, null, discardLocalWork, out error, out outcome);
        }

        internal static bool TryRemoveSubmodule(
            string path,
            SubmoduleRemovalAssessment confirmedAssessment,
            bool discardLocalWork,
            out string error,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            return TryRemoveSubmodule(
                path,
                confirmedAssessment,
                discardLocalWork,
                out error,
                out _,
                cancellationToken);
        }

        internal static bool TryRemoveSubmodule(
            string path,
            SubmoduleRemovalAssessment confirmedAssessment,
            bool discardLocalWork,
            out string error,
            out GitOperationCompletionOutcome outcome,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            error = string.Empty;
            outcome = GitOperationCompletionOutcome.FailedButRolledBack;
            string root = ProjectRoot;
            string normalizedPath = NormalizePath(path);
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryAssessSubmoduleRemoval(
                    normalizedPath,
                    out SubmoduleRemovalAssessment assessment,
                    out error,
                    cancellationToken))
                return false;

            if (confirmedAssessment != null && !RemovalAssessmentMatches(confirmedAssessment, assessment))
            {
                error =
                    "The package or parent repository changed after the removal warning was shown. Nothing was removed; review the current state and confirm again.";
                return false;
            }

            if (!assessment.IsSafe && !discardLocalWork)
            {
                error = assessment.BuildWarning() + " Removal was blocked to protect your work.";
                return false;
            }

            if (assessment.HasUnverifiedWorktreeContents)
            {
                error =
                    "The package directory contains files but is not an initialized submodule worktree. " +
                    "Move those files to safety and leave the directory empty before removing the gitlink; the Unity UI will not discard unverified contents.";
                return false;
            }

            cancellationToken.ThrowIfCancellationRequested();
            string force = discardLocalWork ? "-f " : string.Empty;
            outcome = GitOperationCompletionOutcome.FailedUnsafe;
            var rmResult = RunGit(
                $"rm {force}-- {Quote(normalizedPath)}",
                root,
                CliCommandRunner.DefaultTimeoutMs,
                cancellationToken);
            if (!rmResult.IsSuccess)
            {
                error = BuildCommandError(
                    "Git did not complete the removal. No manual metadata deletion was attempted; inspect the repository before retrying",
                    rmResult);
                return false;
            }

            cancellationToken.ThrowIfCancellationRequested();
            var trackedResult = RunGit(
                $"ls-files --error-unmatch -- {Quote(normalizedPath)}",
                root,
                CliCommandRunner.DefaultTimeoutMs,
                cancellationToken);
            if (trackedResult.IsSuccess)
            {
                error = "Git reported success, but the submodule is still present in the parent index. Recovery metadata was preserved; inspect the repository before retrying.";
                return false;
            }

            if (trackedResult.ExitCode != 1)
            {
                error = BuildCommandError(
                    "The submodule was removed, but its parent-index postcondition could not be verified. Recovery metadata was preserved",
                    trackedResult);
                return false;
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (!TryFindSubmoduleNameForPath(
                    normalizedPath,
                    out _,
                    out bool isStillRegistered,
                    out string configError,
                    cancellationToken))
            {
                error = configError;
                return false;
            }

            if (isStillRegistered)
            {
                error = "The parent index was updated, but .gitmodules still registers the package. Recovery metadata was preserved; inspect the repository before retrying.";
                return false;
            }

            string removedWorktreePath = Path.Combine(root, normalizedPath);
            if (!TryVerifyFileSystemEntryAbsent(
                    removedWorktreePath,
                    out string absenceError,
                    cancellationToken))
            {
                error =
                    "Git removed the parent registration, but package worktree absence could not be verified. " +
                    "It was preserved for manual review instead of reporting a completed removal. " +
                    absenceError;
                return false;
            }

            outcome = GitOperationCompletionOutcome.Succeeded;
            return true;
        }

        private static bool RemovalAssessmentMatches(
            SubmoduleRemovalAssessment expected,
            SubmoduleRemovalAssessment current)
        {
            return expected != null && current != null &&
                   string.Equals(expected.Path, current.Path, StringComparison.Ordinal) &&
                   expected.IsInitialized == current.IsInitialized &&
                   expected.HasWorkingTreeChanges == current.HasWorkingTreeChanges &&
                   expected.HasConflicts == current.HasConflicts &&
                   expected.HasLocalOnlyCommits == current.HasLocalOnlyCommits &&
                   expected.HasParentChanges == current.HasParentChanges &&
                   expected.HasUnverifiedWorktreeContents == current.HasUnverifiedWorktreeContents &&
                   expected.LocalOnlyCommitCount == current.LocalOnlyCommitCount &&
                   string.Equals(expected.HeadCommit, current.HeadCommit, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(expected.ParentStatus, current.ParentStatus, StringComparison.Ordinal) &&
                   string.Equals(expected.WorktreeStatus, current.WorktreeStatus, StringComparison.Ordinal);
        }

        internal static bool TryCleanupFailedAdd(
            AddSubmodulePlan plan,
            out string warning,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            warning = string.Empty;
            if (plan == null)
            {
                warning = "The add recovery plan is missing. No cleanup was attempted.";
                return false;
            }

            if (!TryValidateExistingRepositoryUrl(
                    plan.ExpectedUrl,
                    "The repository URL approved for failed-add cleanup",
                    out warning))
                return false;

            string normalizedPath = NormalizePath(plan.Path);
            if (!IsPackagePath(normalizedPath))
            {
                warning = "Refusing to clean a path outside the exact Packages/<package-name> root.";
                return false;
            }

            cancellationToken.ThrowIfCancellationRequested();
            string root = ProjectRoot;
            var trackedResult = RunGit(
                $"ls-files --error-unmatch -- {Quote(normalizedPath)}",
                root,
                CliCommandRunner.DefaultTimeoutMs,
                cancellationToken);
            if (trackedResult.IsSuccess)
            {
                if (!TryVerifyFailedAddRegistrationOwnership(plan, out warning, cancellationToken))
                    return false;
                return TryRollbackAddedSubmodule(plan, out warning, cancellationToken);
            }

            if (trackedResult.ExitCode != 1)
            {
                warning = BuildCommandError(
                    "Cleanup stopped because the parent Git index could not be inspected safely",
                    trackedResult);
                return false;
            }

            if (!TryFindSubmoduleNameForPath(
                    normalizedPath,
                    out string submoduleName,
                    out bool isRegistered,
                    out warning,
                    cancellationToken))
                return false;

            if (isRegistered)
            {
                if (!TryVerifyFailedAddRegistrationOwnership(plan, out warning, cancellationToken))
                    return false;
                return TryRollbackAddedSubmodule(plan, out warning, cancellationToken);
            }

            if (!TryVerifyUnregisteredFailedAddOwnership(plan, out warning, cancellationToken))
                return false;

            cancellationToken.ThrowIfCancellationRequested();
            if (!TryQuarantineFailedWorktree(
                    normalizedPath,
                    out string worktreeNotice,
                    cancellationToken))
            {
                warning = worktreeNotice;
                return false;
            }

            // The default submodule name is its path. Preserve any partially-created
            // object database in Library instead of recursively deleting recoverable data.
            string metadataName = string.IsNullOrEmpty(submoduleName) ? normalizedPath : submoduleName;
            if (!TryQuarantineSubmoduleMetadata(
                    metadataName,
                    out string metadataNotice,
                    cancellationToken))
            {
                warning = JoinRecoveryNotices(worktreeNotice, metadataNotice);
                return false;
            }

            warning = JoinRecoveryNotices(worktreeNotice, metadataNotice);
            return true;
        }

        private static bool TryVerifyFailedAddRegistrationOwnership(
            AddSubmodulePlan plan,
            out string error,
            CancellationToken cancellationToken)
        {
            error = string.Empty;
            string normalizedPath = NormalizePath(plan.Path);
            if (string.IsNullOrWhiteSpace(plan.ExpectedUrl) ||
                string.IsNullOrWhiteSpace(plan.ExpectedSubmoduleName))
            {
                error = "Failed-add cleanup has no approved repository identity. Existing registration was preserved for manual review.";
                return false;
            }

            if (!TryValidateExistingRepositoryUrl(
                    plan.ExpectedUrl,
                    "The repository URL approved for failed-add cleanup",
                    out error))
                return false;

            if (!TryFindSubmoduleNameForPath(
                    normalizedPath,
                    out string submoduleName,
                    out bool found,
                    out error,
                    cancellationToken) ||
                !found ||
                !string.Equals(submoduleName, plan.ExpectedSubmoduleName, StringComparison.Ordinal))
            {
                if (string.IsNullOrWhiteSpace(error))
                    error = "The submodule registration does not match this add operation. It was preserved for manual review.";
                return false;
            }

            var urlResult = RunGit(
                $"config --file .gitmodules --get {Quote("submodule." + submoduleName + ".url")}",
                ProjectRoot,
                5000,
                cancellationToken);
            if (!TryRequireCompleteStructuralOutput(
                    urlResult,
                    "Failed-add registered URL inspection",
                    out error))
                return false;

            string registeredUrl = urlResult.StdOut?.Trim() ?? string.Empty;
            if (!urlResult.IsSuccess ||
                !TryValidateExistingRepositoryUrl(
                    registeredUrl,
                    "The registered submodule URL",
                    out error) ||
                !AreRepositoryUrlsEquivalent(registeredUrl, plan.ExpectedUrl))
            {
                if (string.IsNullOrWhiteSpace(error))
                    error = "The registered submodule URL does not match the repository approved for this add operation. Nothing was cleaned up.";
                return false;
            }

            var branchResult = RunGit(
                $"config --file .gitmodules --get {Quote("submodule." + submoduleName + ".branch")}",
                ProjectRoot,
                5000,
                cancellationToken);
            if (!TryRequireCompleteStructuralOutput(
                    branchResult,
                    "Failed-add branch inspection",
                    out error))
                return false;

            string expectedBranch = plan.ExpectedBranch?.Trim() ?? string.Empty;
            if (!string.IsNullOrEmpty(expectedBranch))
            {
                if (!branchResult.IsSuccess ||
                    !string.Equals(branchResult.StdOut.Trim(), expectedBranch, StringComparison.Ordinal))
                {
                    error = "The registered branch does not match the branch approved for this add operation. Nothing was cleaned up.";
                    return false;
                }
            }
            else if (branchResult.IsSuccess && !string.IsNullOrWhiteSpace(branchResult.StdOut))
            {
                error = "An unexpected tracked branch appeared during add. Nothing was cleaned up.";
                return false;
            }
            else if (!branchResult.IsSuccess && branchResult.ExitCode != 1)
            {
                error = BuildCommandError("Failed to verify the registered branch before cleanup", branchResult);
                return false;
            }

            var indexResult = RunGit(
                $"ls-files --stage -- {Quote(normalizedPath)}",
                ProjectRoot,
                5000,
                cancellationToken);
            if (!TryParseGitlink(indexResult, normalizedPath, out _, out error))
                return false;

            if (!TryValidateExactSubmoduleWorktree(normalizedPath, out error, cancellationToken))
                return false;

            if (!TryResolveApprovedInitializedUrl(plan, out string approvedOriginUrl, out error, cancellationToken))
                return false;

            var originResult = RunGit(
                $"-C {Quote(normalizedPath)} remote get-url origin",
                ProjectRoot,
                5000,
                cancellationToken);
            if (!TryRequireCompleteStructuralOutput(
                    originResult,
                    "Failed-add worktree origin inspection",
                    out error))
                return false;

            string originUrl = originResult.StdOut?.Trim() ?? string.Empty;
            if (!originResult.IsSuccess ||
                !TryValidateExistingRepositoryUrl(
                    originUrl,
                    "The failed-add worktree origin URL",
                    out error) ||
                !AreRepositoryUrlsEquivalent(originUrl, approvedOriginUrl))
            {
                if (string.IsNullOrWhiteSpace(error))
                    error = "The package worktree origin does not match this add operation. Nothing was cleaned up.";
                return false;
            }

            return true;
        }

        private static bool TryVerifyUnregisteredFailedAddOwnership(
            AddSubmodulePlan plan,
            out string error,
            CancellationToken cancellationToken)
        {
            error = string.Empty;
            string normalizedPath = NormalizePath(plan.Path);
            string packagePath = Path.Combine(ProjectRoot, normalizedPath);
            bool hasWorktree = Directory.Exists(packagePath) || File.Exists(packagePath);
            if (hasWorktree)
            {
                if (!Directory.Exists(packagePath) ||
                    !TryValidateExactSubmoduleWorktree(normalizedPath, out error, cancellationToken))
                {
                    if (string.IsNullOrWhiteSpace(error))
                        error = "An unregistered destination appeared during add and cannot be proven to belong to this operation. It was preserved.";
                    return false;
                }

                if (!TryResolveApprovedInitializedUrl(plan, out string approvedOriginUrl, out error, cancellationToken))
                    return false;

                var originResult = RunGit(
                    $"-C {Quote(normalizedPath)} remote get-url origin",
                    ProjectRoot,
                    5000,
                    cancellationToken);
                if (!TryRequireCompleteStructuralOutput(
                        originResult,
                        "Unregistered failed-add origin inspection",
                        out error))
                    return false;

                string originUrl = originResult.StdOut?.Trim() ?? string.Empty;
                if (!originResult.IsSuccess ||
                    !TryValidateExistingRepositoryUrl(
                        originUrl,
                        "The unregistered worktree origin URL",
                        out error) ||
                    !AreRepositoryUrlsEquivalent(originUrl, approvedOriginUrl))
                {
                    if (string.IsNullOrWhiteSpace(error))
                        error = "The unregistered worktree origin does not match this add operation. It was preserved.";
                    return false;
                }
            }

            if (!TryResolveSubmoduleGitDir(
                    normalizedPath,
                    out string moduleGitDir,
                    out error,
                    cancellationToken) ||
                !TryValidateSubmoduleMetadataPath(moduleGitDir, out error, cancellationToken))
                return false;

            if (Directory.Exists(moduleGitDir))
            {
                if (!TryResolveApprovedInitializedUrl(
                        plan,
                        out string approvedMetadataOrigin,
                        out error,
                        cancellationToken))
                    return false;

                var metadataOrigin = RunGit(
                    $"--git-dir {Quote(moduleGitDir)} --work-tree {Quote(packagePath)} config --get remote.origin.url",
                    ProjectRoot,
                    5000,
                    cancellationToken);
                if (!TryRequireCompleteStructuralOutput(
                        metadataOrigin,
                        "Recoverable failed-add metadata origin inspection",
                        out error))
                    return false;

                string metadataOriginUrl = metadataOrigin.StdOut?.Trim() ?? string.Empty;
                if (!metadataOrigin.IsSuccess ||
                    !TryValidateExistingRepositoryUrl(
                        metadataOriginUrl,
                        "The recoverable submodule metadata origin URL",
                        out error) ||
                    !AreRepositoryUrlsEquivalent(metadataOriginUrl, approvedMetadataOrigin))
                {
                    if (string.IsNullOrWhiteSpace(error))
                        error = "Recoverable submodule metadata does not match this add operation. It was preserved.";
                    return false;
                }
            }

            return true;
        }

        private static bool TryResolveApprovedInitializedUrl(
            AddSubmodulePlan plan,
            out string approvedUrl,
            out string error,
            CancellationToken cancellationToken)
        {
            approvedUrl = plan?.ExpectedUrl?.Trim() ?? string.Empty;
            error = string.Empty;
            if (!IsRelativeLocalRepositoryUrl(approvedUrl))
                return !string.IsNullOrWhiteSpace(approvedUrl);

            string submoduleName = string.IsNullOrWhiteSpace(plan.ExpectedSubmoduleName)
                ? NormalizePath(plan.Path)
                : plan.ExpectedSubmoduleName;
            var localConfigResult = RunGit(
                $"config --get {Quote("submodule." + submoduleName + ".url")}",
                ProjectRoot,
                5000,
                cancellationToken);
            if (!TryRequireCompleteStructuralOutput(
                    localConfigResult,
                    "Resolved local repository URL inspection",
                    out error))
                return false;

            if (!localConfigResult.IsSuccess || string.IsNullOrWhiteSpace(localConfigResult.StdOut))
            {
                error =
                    "Git did not retain a resolved local URL for the relative repository approved by this add operation. Cleanup was skipped for manual review.";
                return false;
            }

            approvedUrl = localConfigResult.StdOut.Trim();
            return TryValidateExistingRepositoryUrl(
                approvedUrl,
                "The resolved local repository URL",
                out error);
        }

        internal static bool TryVerifyAddedSubmodule(
            AddSubmodulePlan plan,
            string expectedUrl,
            string expectedBranch,
            out string error,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            error = string.Empty;
            if (plan == null || !IsPackagePath(plan.Path))
            {
                error = "The add verification plan is invalid.";
                return false;
            }

            if (!TryValidateExistingRepositoryUrl(
                    expectedUrl,
                    "The repository URL approved for this add operation",
                    out error))
                return false;

            string normalizedPath = NormalizePath(plan.Path);
            if (!TryFindSubmoduleNameForPath(
                    normalizedPath,
                    out string submoduleName,
                    out bool isRegistered,
                    out error,
                    cancellationToken))
                return false;
            if (!isRegistered || string.IsNullOrWhiteSpace(submoduleName))
            {
                error = ".gitmodules does not contain the expected package registration after Git reported success.";
                return false;
            }

            var urlResult = RunGit(
                $"config --file .gitmodules --get {Quote("submodule." + submoduleName + ".url")}",
                ProjectRoot,
                5000,
                cancellationToken);
            if (!TryRequireCompleteStructuralOutput(
                    urlResult,
                    "Added submodule URL verification",
                    out error))
                return false;

            string registeredUrl = urlResult.StdOut?.Trim() ?? string.Empty;
            if (!urlResult.IsSuccess ||
                !TryValidateExistingRepositoryUrl(
                    registeredUrl,
                    "The added submodule URL",
                    out error) ||
                !AreRepositoryUrlsEquivalent(registeredUrl, expectedUrl))
            {
                if (string.IsNullOrWhiteSpace(error))
                    error = "The added submodule URL does not match the repository that was approved.";
                return false;
            }

            string branch = expectedBranch?.Trim() ?? string.Empty;
            var branchResult = RunGit(
                $"config --file .gitmodules --get {Quote("submodule." + submoduleName + ".branch")}",
                ProjectRoot,
                5000,
                cancellationToken);
            if (!TryRequireCompleteStructuralOutput(
                    branchResult,
                    "Added submodule branch verification",
                    out error))
                return false;

            if (!string.IsNullOrEmpty(branch))
            {
                if (!branchResult.IsSuccess ||
                    !string.Equals(branchResult.StdOut.Trim(), branch, StringComparison.Ordinal))
                {
                    error = "The added submodule branch does not match the branch that was approved.";
                    return false;
                }
            }
            else if (branchResult.IsSuccess && !string.IsNullOrWhiteSpace(branchResult.StdOut))
            {
                error = "The added submodule unexpectedly registered a branch when no branch was approved.";
                return false;
            }
            else if (!branchResult.IsSuccess && branchResult.ExitCode != 1)
            {
                error = BuildCommandError("Failed to verify that the added submodule has no tracked branch", branchResult);
                return false;
            }

            var indexResult = RunGit(
                $"ls-files --stage -- {Quote(normalizedPath)}",
                ProjectRoot,
                5000,
                cancellationToken);
            if (!TryRequireCompleteStructuralOutput(
                    indexResult,
                    "Added package gitlink verification",
                    out error))
                return false;

            if (!indexResult.IsSuccess || string.IsNullOrWhiteSpace(indexResult.StdOut))
            {
                error = BuildCommandError("The added package gitlink is missing from the parent index", indexResult);
                return false;
            }

            Match indexMatch = Regex.Match(
                indexResult.StdOut.Trim(),
                @"^160000\s+([0-9a-fA-F]{40,64})\s+0\t(.+)$",
                RegexOptions.CultureInvariant);
            if (!indexMatch.Success ||
                !string.Equals(NormalizePath(indexMatch.Groups[2].Value), normalizedPath, StringComparison.Ordinal))
            {
                error = "The parent index entry for the added package is not a valid submodule gitlink.";
                return false;
            }

            var headResult = RunGit(
                $"-C {Quote(normalizedPath)} rev-parse --verify HEAD",
                ProjectRoot,
                5000,
                cancellationToken);
            if (!TryRequireCompleteStructuralOutput(
                    headResult,
                    "Added package commit verification",
                    out error))
                return false;

            if (!headResult.IsSuccess ||
                !string.Equals(
                    headResult.StdOut.Trim(),
                    indexMatch.Groups[1].Value,
                    StringComparison.OrdinalIgnoreCase))
            {
                error = "The package worktree HEAD does not match the commit staged in the parent gitlink.";
                return false;
            }

            if (!TryGetInterruptedRepositoryOperation(
                    normalizedPath,
                    out string interruptedOperation,
                    out error,
                    cancellationToken))
                return false;
            if (!string.IsNullOrEmpty(interruptedOperation))
            {
                error = $"The added package contains an unfinished {interruptedOperation} operation.";
                return false;
            }

            return TryVerifySubmoduleClean(normalizedPath, out error, cancellationToken);
        }

        private static bool TryRollbackAddedSubmodule(
            AddSubmodulePlan plan,
            out string warning,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            warning = string.Empty;
            string normalizedPath = NormalizePath(plan.Path);
            if (!TryFindSubmoduleNameForPath(
                    normalizedPath,
                    out string submoduleName,
                    out _,
                    out warning,
                    cancellationToken))
                return false;

            cancellationToken.ThrowIfCancellationRequested();
            if (!TryQuarantineFailedWorktree(
                    normalizedPath,
                    out string worktreeNotice,
                    cancellationToken))
            {
                warning = worktreeNotice;
                return false;
            }

            cancellationToken.ThrowIfCancellationRequested();
            var removeResult = RunGit(
                $"rm -f -- {Quote(normalizedPath)}",
                ProjectRoot,
                CliCommandRunner.DefaultTimeoutMs,
                cancellationToken);
            if (!removeResult.IsSuccess)
            {
                warning = BuildCommandError(
                    "Failed to roll back the submodule registration. The worktree was preserved for recovery",
                    removeResult) + " " + worktreeNotice;
                return false;
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (!TryRestoreGitModulesBaseline(plan, out string baselineError, cancellationToken))
            {
                warning = JoinRecoveryNotices(worktreeNotice, baselineError);
                return false;
            }

            var pathStatus = RunGit(
                $"status --porcelain=v2 --untracked-files=all -- {Quote(normalizedPath)}",
                ProjectRoot,
                CliCommandRunner.DefaultTimeoutMs,
                cancellationToken);
            if (!TryRequireCompleteStructuralOutput(
                    pathStatus,
                    "Failed-add rollback path inspection",
                    out warning))
            {
                warning = JoinRecoveryNotices(worktreeNotice, warning);
                return false;
            }

            if (!pathStatus.IsSuccess || !string.IsNullOrWhiteSpace(pathStatus.StdOut))
            {
                warning = !pathStatus.IsSuccess
                    ? BuildCommandError("The add rollback could not verify the parent index", pathStatus)
                    : "The add rollback left an unexpected parent-index or worktree change at the package path.";
                warning = JoinRecoveryNotices(worktreeNotice, warning);
                return false;
            }

            string metadataName = string.IsNullOrEmpty(submoduleName) ? normalizedPath : submoduleName;
            if (!TryQuarantineSubmoduleMetadata(
                    metadataName,
                    out string metadataNotice,
                    cancellationToken))
            {
                warning = JoinRecoveryNotices(worktreeNotice, metadataNotice);
                return false;
            }

            warning = JoinRecoveryNotices(worktreeNotice, metadataNotice);
            return true;
        }

        private static bool TryRestoreGitModulesBaseline(
            AddSubmodulePlan plan,
            out string error,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            error = string.Empty;
            string gitModulesPath = Path.Combine(ProjectRoot, ".gitmodules");
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (plan.GitModulesExisted)
                {
                    if (!File.Exists(gitModulesPath) ||
                        !File.ReadAllBytes(gitModulesPath).SequenceEqual(plan.GitModulesContents ?? Array.Empty<byte>()))
                    {
                        error =
                            ".gitmodules no longer matches its pre-add contents. It may contain a concurrent edit, so it was not overwritten.";
                        return false;
                    }
                }
                else if (File.Exists(gitModulesPath))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    byte[] current = File.ReadAllBytes(gitModulesPath);
                    if (current.Any(value => !char.IsWhiteSpace((char)value)))
                    {
                        error =
                            ".gitmodules was created with unexpected non-empty content during rollback. It was preserved for manual review.";
                        return false;
                    }

                    File.Delete(gitModulesPath);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                error = $"Failed to verify the pre-add .gitmodules state: {ex.Message}";
                return false;
            }

            cancellationToken.ThrowIfCancellationRequested();
            var resetResult = RunGit(
                "reset -- .gitmodules",
                ProjectRoot,
                CliCommandRunner.DefaultTimeoutMs,
                cancellationToken);
            if (!resetResult.IsSuccess)
            {
                error = BuildCommandError("Failed to restore the pre-add .gitmodules index state", resetResult);
                return false;
            }

            var statusResult = RunGit(
                "status --porcelain=v2 --untracked-files=all -- .gitmodules",
                ProjectRoot,
                CliCommandRunner.DefaultTimeoutMs,
                cancellationToken);
            if (!statusResult.IsSuccess)
            {
                error = BuildCommandError("Failed to verify the restored .gitmodules state", statusResult);
                return false;
            }

            if (!TryRequireCompleteStructuralOutput(
                    statusResult,
                    "Restored .gitmodules status inspection",
                    out error))
                return false;

            if (!string.IsNullOrWhiteSpace(statusResult.StdOut))
            {
                error = ".gitmodules did not return to its exact pre-add repository state.";
                return false;
            }

            return true;
        }

        private static bool TryQuarantineFailedWorktree(
            string normalizedPath,
            out string notice,
            CancellationToken cancellationToken)
        {
            notice = string.Empty;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                string packagePath = Path.GetFullPath(Path.Combine(ProjectRoot, normalizedPath));
                string packagesRoot = Path.GetFullPath(Path.Combine(ProjectRoot, "Packages"))
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                StringComparison comparison = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal;
                if (!packagePath.StartsWith(packagesRoot + Path.DirectorySeparatorChar, comparison))
                {
                    notice = "Refusing to move a failed-add worktree that resolves outside the project's Packages directory.";
                    return false;
                }

                if (HasReparsePointBetween(packagesRoot, packagePath))
                {
                    notice =
                        "Refusing to move a failed-add worktree through a symbolic link or junction. Preserve and inspect it manually.";
                    return false;
                }

                if (!Directory.Exists(packagePath) && !File.Exists(packagePath))
                    return true;

                cancellationToken.ThrowIfCancellationRequested();
                string recoveryRoot = Path.Combine(ProjectRoot, "Library", "GitPackageManager", "Recovery");
                string projectRoot = Path.GetFullPath(ProjectRoot);
                string fullRecoveryRoot = Path.GetFullPath(recoveryRoot);
                if (HasReparsePointBetween(projectRoot, fullRecoveryRoot))
                {
                    notice = "Refusing to write recovery data through a symbolic link or junction under Library.";
                    return false;
                }
                Directory.CreateDirectory(recoveryRoot);
                string safeName = Regex.Replace(normalizedPath, @"[^A-Za-z0-9._-]+", "-").Trim('-');
                if (string.IsNullOrEmpty(safeName))
                    safeName = "submodule";
                string destination = Path.Combine(
                    recoveryRoot,
                    $"{DateTime.UtcNow:yyyyMMdd-HHmmssfff}-{safeName}-{Guid.NewGuid():N}-worktree");
                cancellationToken.ThrowIfCancellationRequested();
                if (Directory.Exists(packagePath))
                    Directory.Move(packagePath, destination);
                else
                    File.Move(packagePath, destination);

                notice = $"The failed-add worktree was preserved at {destination}.";
                return true;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                notice = $"The failed-add worktree could not be preserved safely: {ex.Message}";
                return false;
            }
        }

        private static string JoinRecoveryNotices(string first, string second)
        {
            if (string.IsNullOrWhiteSpace(first))
                return second ?? string.Empty;
            if (string.IsNullOrWhiteSpace(second))
                return first;
            return first.Trim() + " " + second.Trim();
        }

        private static bool HasReparsePointBetween(string rootPath, string candidatePath)
        {
            string root = Path.GetFullPath(rootPath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string candidate = Path.GetFullPath(candidatePath);
            StringComparison comparison = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            if (!string.Equals(root, candidate, comparison) &&
                !candidate.StartsWith(root + Path.DirectorySeparatorChar, comparison))
                return true;

            string current = root;
            if (PathExists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                return true;

            string relative = candidate.Length == root.Length
                ? string.Empty
                : candidate.Substring(root.Length + 1);
            foreach (string segment in relative.Split(
                         new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                         StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, segment);
                if (PathExists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    return true;
            }

            return false;
        }

        private static bool PathExists(string path)
        {
            return Directory.Exists(path) || File.Exists(path);
        }

        private static bool TryVerifyFileSystemEntryAbsent(
            string path,
            out string error,
            CancellationToken cancellationToken)
        {
            error = string.Empty;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                string fullPath = Path.GetFullPath(path);
                string parent = Path.GetDirectoryName(fullPath);
                if (string.IsNullOrEmpty(parent) || !Directory.Exists(parent))
                    return true;

                StringComparison comparison = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal;
                foreach (string entry in Directory.GetFileSystemEntries(parent))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!string.Equals(Path.GetFullPath(entry), fullPath, comparison))
                        continue;

                    error = "A filesystem entry remains at the managed package path.";
                    return false;
                }

                return true;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                error = "The package path could not be inspected completely: " + exception.Message;
                return false;
            }
        }

        internal static bool TryValidateProjectOwnedPath(string candidatePath, out string error)
        {
            error = string.Empty;
            try
            {
                string projectRoot = Path.GetFullPath(ProjectRoot);
                string candidate = Path.GetFullPath(candidatePath);
                StringComparison comparison = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal;
                string prefix = projectRoot.TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (!candidate.StartsWith(prefix, comparison))
                {
                    error = "The recovery path resolves outside the Unity project root.";
                    return false;
                }

                if (HasReparsePointBetween(projectRoot, candidate))
                {
                    error =
                        "The recovery path contains a symbolic link, junction, or other reparse point. " +
                        "Repository operations are blocked until Library/GitPackageManager is a normal project-local directory.";
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                error = $"Failed to validate the project recovery path: {ex.Message}";
                return false;
            }
        }

        private static bool TryQuarantineSubmoduleMetadata(
            string submoduleName,
            out string warning,
            CancellationToken cancellationToken)
        {
            warning = string.Empty;
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryResolveSubmoduleGitDir(
                    submoduleName,
                    out string moduleGitDir,
                    out string resolveError,
                    cancellationToken))
            {
                warning = resolveError;
                return false;
            }

            if (!Directory.Exists(moduleGitDir))
                return true;

            if (!TryValidateSubmoduleMetadataPath(moduleGitDir, out warning, cancellationToken))
                return false;

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var modulesRootResult = RunGit(
                    "rev-parse --git-path modules",
                    ProjectRoot,
                    5000,
                    cancellationToken);
                if (!modulesRootResult.IsSuccess ||
                    string.IsNullOrWhiteSpace(modulesRootResult.StdOut) ||
                    !TryRequireCompleteStructuralOutput(
                        modulesRootResult,
                        "Git modules directory inspection",
                        out warning))
                {
                    if (string.IsNullOrWhiteSpace(warning))
                        warning = BuildCommandError("Failed to locate the Git modules directory", modulesRootResult);
                    return false;
                }

                string modulesRootValue = modulesRootResult.StdOut.Trim();
                string modulesRoot = Path.GetFullPath(
                    Path.IsPathRooted(modulesRootValue)
                        ? modulesRootValue
                        : Path.Combine(ProjectRoot, modulesRootValue));
                string modulesPrefix = modulesRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
                                       Path.DirectorySeparatorChar;
                var pathComparison = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal;
                if (!moduleGitDir.StartsWith(modulesPrefix, pathComparison))
                {
                    warning = "Refusing to move submodule metadata that resolves outside Git's modules directory.";
                    return false;
                }

                if (HasReparsePointBetween(modulesRoot, moduleGitDir))
                {
                    warning =
                        "Refusing to move submodule metadata through a symbolic link or junction in Git's modules directory.";
                    return false;
                }

                string recoveryRoot = Path.Combine(ProjectRoot, "Library", "GitPackageManager", "Recovery");
                string fullProjectRoot = Path.GetFullPath(ProjectRoot);
                string fullRecoveryRoot = Path.GetFullPath(recoveryRoot);
                if (HasReparsePointBetween(fullProjectRoot, fullRecoveryRoot))
                {
                    warning = "Refusing to write recovery metadata through a symbolic link or junction under Library.";
                    return false;
                }
                Directory.CreateDirectory(recoveryRoot);
                string safeName = Regex.Replace(submoduleName, @"[^A-Za-z0-9._-]+", "-").Trim('-');
                if (string.IsNullOrEmpty(safeName))
                    safeName = "submodule";
                string destination = Path.Combine(
                    recoveryRoot,
                    $"{DateTime.UtcNow:yyyyMMdd-HHmmssfff}-{safeName}");
                cancellationToken.ThrowIfCancellationRequested();
                Directory.Move(moduleGitDir, destination);
                return true;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                warning = $"The failed add was unregistered, but its recoverable Git metadata could not be quarantined: {ex.Message}";
                return false;
            }
        }

        private static bool TryValidateSubmoduleMetadataPath(
            string moduleGitDir,
            out string error,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            error = string.Empty;
            cancellationToken.ThrowIfCancellationRequested();
            var modulesRootResult = RunGit(
                "rev-parse --git-path modules",
                ProjectRoot,
                5000,
                cancellationToken);
            if (!TryRequireCompleteStructuralOutput(
                    modulesRootResult,
                    "Git modules directory inspection",
                    out error))
                return false;

            if (!modulesRootResult.IsSuccess || string.IsNullOrWhiteSpace(modulesRootResult.StdOut))
            {
                error = BuildCommandError("Failed to validate the Git modules directory", modulesRootResult);
                return false;
            }

            try
            {
                string modulesRootValue = modulesRootResult.StdOut.Trim();
                string modulesRoot = Path.GetFullPath(
                    Path.IsPathRooted(modulesRootValue)
                        ? modulesRootValue
                        : Path.Combine(ProjectRoot, modulesRootValue));
                string candidate = Path.GetFullPath(moduleGitDir);
                string prefix = modulesRoot.TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
                StringComparison comparison = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal;
                if (!candidate.StartsWith(prefix, comparison))
                {
                    error = "The submodule metadata path resolves outside Git's modules directory.";
                    return false;
                }

                if (HasReparsePointBetween(modulesRoot, candidate))
                {
                    error =
                        "The submodule metadata path contains a symbolic link, junction, or other reparse point. " +
                        "External metadata is never reused or mutated automatically.";
                    return false;
                }
            }
            catch (Exception ex)
            {
                error = $"Failed to validate the submodule metadata path: {ex.Message}";
                return false;
            }

            return true;
        }

        internal static bool TryResolveSubmoduleGitDir(
            string submoduleName,
            out string gitDir,
            out string error,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            gitDir = string.Empty;
            error = string.Empty;
            if (string.IsNullOrWhiteSpace(submoduleName) ||
                submoduleName.IndexOfAny(new[] { '\0', '\r', '\n' }) >= 0)
            {
                error = "The submodule name is invalid.";
                return false;
            }

            cancellationToken.ThrowIfCancellationRequested();
            var result = RunGit(
                $"rev-parse --git-path {Quote("modules/" + submoduleName)}",
                ProjectRoot,
                5000,
                cancellationToken);
            if (!TryRequireCompleteStructuralOutput(
                    result,
                    "Submodule Git-directory inspection",
                    out error))
                return false;

            if (!result.IsSuccess || string.IsNullOrWhiteSpace(result.StdOut))
            {
                error = BuildCommandError("Failed to resolve the submodule Git directory", result);
                return false;
            }

            try
            {
                string value = result.StdOut.Trim();
                gitDir = Path.GetFullPath(Path.IsPathRooted(value) ? value : Path.Combine(ProjectRoot, value));
                return true;
            }
            catch (Exception ex)
            {
                error = $"Git returned an invalid submodule metadata path: {ex.Message}";
                return false;
            }
        }

        internal static bool TryValidateExactSubmoduleWorktree(
            string path,
            out string error,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            if (!TryInspectExactSubmoduleWorktree(
                    path,
                    out bool isExactWorktree,
                    out error,
                    cancellationToken))
                return false;

            if (isExactWorktree)
                return true;

            error =
                $"{NormalizePath(path)} is not an initialized Git worktree rooted at that exact package directory. " +
                "The operation was blocked so Git cannot walk upward and act on the parent Unity repository.";
            return false;
        }

        private static bool TryInspectExactSubmoduleWorktree(
            string path,
            out bool isExactWorktree,
            out string error,
            CancellationToken cancellationToken)
        {
            isExactWorktree = false;
            error = string.Empty;
            string normalizedPath = NormalizePath(path);
            if (!IsPackagePath(normalizedPath))
            {
                error = "Package path is invalid.";
                return false;
            }

            string packagesRoot;
            string expectedRoot;
            try
            {
                packagesRoot = Path.GetFullPath(Path.Combine(ProjectRoot, "Packages"));
                expectedRoot = Path.GetFullPath(Path.Combine(ProjectRoot, normalizedPath));
                if (!Directory.Exists(expectedRoot))
                    return true;

                if (HasReparsePointBetween(packagesRoot, expectedRoot))
                {
                    error =
                        "The package worktree path contains a symbolic link, junction, or other reparse point. " +
                        "Repository mutations through aliased package paths are not supported.";
                    return false;
                }
            }
            catch (Exception ex)
            {
                error = $"Failed to validate the package worktree path: {ex.Message}";
                return false;
            }

            string gitMarkerPath = Path.Combine(expectedRoot, ".git");
            if (!File.Exists(gitMarkerPath) && !Directory.Exists(gitMarkerPath))
            {
                // No child repository marker means Git would only be able to
                // walk upward into the parent repository. Treat this as a
                // deinitialized package without launching a child Git command.
                return true;
            }

            if (!TryValidateSubmoduleWorktreeOwnership(
                    normalizedPath,
                    out error,
                    cancellationToken))
                return false;

            var rootResult = RunGit(
                $"-C {Quote(normalizedPath)} rev-parse --show-prefix",
                ProjectRoot,
                5000,
                cancellationToken);
            if (!TryRequireCompleteStructuralOutput(
                    rootResult,
                    "Package worktree-root inspection",
                    out error))
                return false;

            if (!rootResult.IsSuccess)
            {
                if (rootResult.Cancelled || rootResult.TimedOut || !rootResult.TerminationConfirmed)
                {
                    error = BuildCommandError("Failed to validate the package worktree root", rootResult);
                    return false;
                }

                // A normal `not a git repository` result represents a
                // deinitialized worktree. Callers such as removal can handle an
                // empty destination without ever issuing another child `-C` call.
                return true;
            }

            var insideWorktreeResult = RunGit(
                $"-C {Quote(normalizedPath)} rev-parse --is-inside-work-tree",
                ProjectRoot,
                5000,
                cancellationToken);
            if (!TryRequireCompleteStructuralOutput(
                    insideWorktreeResult,
                    "Package worktree inspection",
                    out error))
                return false;

            if (!insideWorktreeResult.IsSuccess)
            {
                error = BuildCommandError(
                    "Failed to verify that the package directory is inside its resolved Git worktree",
                    insideWorktreeResult);
                return false;
            }

            if (!string.Equals(
                    insideWorktreeResult.StdOut.Trim(),
                    "true",
                    StringComparison.Ordinal))
            {
                error =
                    "The package Git directory resolves to a worktree outside the exact package path. " +
                    "Repository mutations through redirected core.worktree configuration are blocked.";
                return false;
            }

            // An empty prefix proves that the exact `-C` directory is the
            // repository root. A non-empty prefix means Git walked upward into
            // the parent Unity repository. This avoids false mismatches for
            // equivalent filesystem aliases such as /var and /private/var.
            isExactWorktree = string.IsNullOrWhiteSpace(rootResult.StdOut);
            return true;
        }

        private static bool TryValidateSubmoduleWorktreeOwnership(
            string path,
            out string error,
            CancellationToken cancellationToken)
        {
            error = string.Empty;
            string normalizedPath = NormalizePath(path);
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryValidateProjectGitRoot(out error, cancellationToken))
                return false;

            if (!TryFindSubmoduleNameForPath(
                    normalizedPath,
                    out string submoduleName,
                    out bool isRegistered,
                    out error,
                    cancellationToken))
                return false;

            // Git uses the path as the default submodule name. Failed-add
            // recovery may run after registration has already disappeared, so
            // that known default is the only metadata location it may own.
            if (!isRegistered)
                submoduleName = normalizedPath;

            if (!TryResolveSubmoduleGitDir(
                    submoduleName,
                    out string expectedGitDir,
                    out error,
                    cancellationToken) ||
                !TryValidateSubmoduleMetadataPath(expectedGitDir, out error, cancellationToken))
                return false;

            if (!Directory.Exists(expectedGitDir))
            {
                error =
                    "The package's registered submodule metadata directory is missing. " +
                    "The worktree was preserved because its Git directory ownership cannot be proven.";
                return false;
            }

            string packageRoot;
            string actualGitDir;
            try
            {
                packageRoot = Path.GetFullPath(Path.Combine(ProjectRoot, normalizedPath));
                string markerPath = Path.Combine(packageRoot, ".git");
                FileAttributes attributes = File.GetAttributes(markerPath);
                if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                {
                    error =
                        "The package .git marker is not a normal Git indirection file. " +
                        "Embedded, symbolic-link, or junction Git directories are never mutated automatically.";
                    return false;
                }

                var markerInfo = new FileInfo(markerPath);
                if (markerInfo.Length <= 0 || markerInfo.Length > 4096)
                {
                    error = "The package .git indirection file has an invalid size.";
                    return false;
                }

                string marker = File.ReadAllText(markerPath);
                FileAttributes attributesAfterRead = File.GetAttributes(markerPath);
                if ((attributesAfterRead & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                {
                    error = "The package .git indirection changed while it was being verified.";
                    return false;
                }

                string markerLine = marker.TrimEnd('\r', '\n');
                const string markerPrefix = "gitdir:";
                if (!markerLine.StartsWith(markerPrefix, StringComparison.Ordinal) ||
                    markerLine.IndexOfAny(new[] { '\0', '\r', '\n' }) >= 0)
                {
                    error = "The package .git indirection file is malformed.";
                    return false;
                }

                string markerValue = markerLine.Substring(markerPrefix.Length).Trim();
                if (string.IsNullOrWhiteSpace(markerValue))
                {
                    error = "The package .git indirection file has no metadata path.";
                    return false;
                }

                actualGitDir = Path.GetFullPath(
                    Path.IsPathRooted(markerValue)
                        ? markerValue
                        : Path.Combine(packageRoot, markerValue));
            }
            catch (Exception ex)
            {
                error = $"Failed to inspect the package .git indirection safely: {ex.Message}";
                return false;
            }

            try
            {
                if (!ProcessCommandRunner.TryCanonicalizeExistingPath(
                        expectedGitDir,
                        out string expected) ||
                    !ProcessCommandRunner.TryCanonicalizeExistingPath(
                        actualGitDir,
                        out string actual))
                {
                    error =
                        "The package Git metadata paths could not be resolved to existing filesystem locations.";
                    return false;
                }

                expected = expected.TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar);
                actual = actual.TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar);
                StringComparison comparison = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal;
                if (!string.Equals(expected, actual, comparison))
                {
                    error =
                        "The package worktree points to a Git directory other than its registered submodule metadata. " +
                        "External or replaced Git directories are never mutated automatically.";
                    return false;
                }
            }
            catch (Exception ex)
            {
                error = $"Git returned an invalid package metadata path: {ex.Message}";
                return false;
            }

            return true;
        }

        internal static bool TryValidateSubmoduleUpdateSource(
            string path,
            string expectedUrl,
            out string error,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            error = string.Empty;
            string normalizedPath = NormalizePath(path);
            if (!TryValidateExactSubmoduleWorktree(normalizedPath, out error, cancellationToken))
                return false;

            if (!TryFindSubmoduleNameForPath(
                    normalizedPath,
                    out string submoduleName,
                    out bool found,
                    out error,
                    cancellationToken) ||
                !found ||
                string.IsNullOrWhiteSpace(submoduleName))
            {
                if (string.IsNullOrWhiteSpace(error))
                    error = ".gitmodules no longer registers the package path being updated.";
                return false;
            }

            var configuredResult = RunGit(
                $"config --file .gitmodules --get {Quote("submodule." + submoduleName + ".url")}",
                ProjectRoot,
                5000,
                cancellationToken);
            if (!TryRequireCompleteStructuralOutput(
                    configuredResult,
                    ".gitmodules URL inspection",
                    out error))
                return false;

            if (!configuredResult.IsSuccess || string.IsNullOrWhiteSpace(configuredResult.StdOut))
            {
                error = BuildCommandError("Failed to read the package URL from .gitmodules", configuredResult);
                return false;
            }

            string configuredUrl = configuredResult.StdOut.Trim();
            if (!TryValidateExistingRepositoryUrl(
                    configuredUrl,
                    "The repository URL stored in .gitmodules",
                    out error))
                return false;

            if (!string.IsNullOrWhiteSpace(expectedUrl) &&
                !TryValidateExistingRepositoryUrl(
                    expectedUrl,
                    "The repository URL loaded for this package",
                    out error))
                return false;

            if (!string.IsNullOrWhiteSpace(expectedUrl) &&
                !AreRepositoryUrlsEquivalent(configuredUrl, expectedUrl))
            {
                error =
                    "The package URL in .gitmodules changed after the package list was loaded. Refresh and review the package before updating.";
                return false;
            }

            var localConfigResult = RunGit(
                $"config --get {Quote("submodule." + submoduleName + ".url")}",
                ProjectRoot,
                5000,
                cancellationToken);
            if (!TryRequireCompleteStructuralOutput(
                    localConfigResult,
                    "Initialized submodule URL inspection",
                    out error))
                return false;

            if (!localConfigResult.IsSuccess || string.IsNullOrWhiteSpace(localConfigResult.StdOut))
            {
                error = BuildCommandError("Failed to verify the initialized submodule URL", localConfigResult);
                return false;
            }

            var originResult = RunGit(
                $"-C {Quote(normalizedPath)} remote get-url origin",
                ProjectRoot,
                5000,
                cancellationToken);
            if (!TryRequireCompleteStructuralOutput(
                    originResult,
                    "Submodule origin URL inspection",
                    out error))
                return false;

            if (!originResult.IsSuccess || string.IsNullOrWhiteSpace(originResult.StdOut))
            {
                error = BuildCommandError("Failed to verify the package origin URL", originResult);
                return false;
            }

            string initializedUrl = localConfigResult.StdOut.Trim();
            string originUrl = originResult.StdOut.Trim();
            if (!TryValidateExistingRepositoryUrl(
                    initializedUrl,
                    "The initialized submodule URL stored in the parent repository",
                    out error) ||
                !TryValidateExistingRepositoryUrl(
                    originUrl,
                    "The package worktree origin URL",
                    out error))
                return false;

            if (!AreRepositoryUrlsEquivalent(initializedUrl, originUrl))
            {
                error =
                    "The package worktree's origin URL does not match the URL recorded for this initialized submodule. " +
                    "Run Git submodule sync or repair the remote before updating.";
                return false;
            }

            // Relative .gitmodules URLs are expanded by Git into the parent
            // repository's local submodule config. For all other URL forms,
            // require that expansion to agree with the committed declaration too.
            if (!IsRelativeLocalRepositoryUrl(configuredUrl) &&
                !AreRepositoryUrlsEquivalent(configuredUrl, initializedUrl))
            {
                error =
                    "The initialized submodule URL does not match the repository declared in .gitmodules. " +
                    "Run Git submodule sync or repair the submodule before updating.";
                return false;
            }

            return true;
        }

        internal static bool TryPrepareSubmoduleUpdate(string path, out SubmoduleUpdatePlan plan, out string error)
        {
            return TryPrepareSubmoduleUpdate(path, string.Empty, null, out plan, out error);
        }

        internal static bool TryPrepareSubmoduleUpdate(
            string path,
            string expectedUrl,
            out SubmoduleUpdatePlan plan,
            out string error,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            return TryPrepareSubmoduleUpdate(
                path,
                expectedUrl,
                null,
                out plan,
                out error,
                cancellationToken);
        }

        internal static bool TryPrepareSubmoduleUpdate(
            string path,
            string expectedUrl,
            string expectedBranch,
            out SubmoduleUpdatePlan plan,
            out string error,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            plan = null;
            error = string.Empty;
            string normalizedPath = NormalizePath(path);
            if (!IsPackagePath(normalizedPath))
            {
                error = "Package path is invalid.";
                return false;
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (!TryEnsureParentMutationStateIsSafe(out error, cancellationToken))
                return false;

            if (!TryValidateSubmoduleUpdateSource(
                    normalizedPath,
                    expectedUrl,
                    out error,
                    cancellationToken))
                return false;

            if (!TryValidateSubmoduleConfiguredBranch(
                    normalizedPath,
                    expectedBranch,
                    out string configuredBranch,
                    out error,
                    cancellationToken))
                return false;

            var parentStatus = RunGit(
                $"status --porcelain=v2 --untracked-files=all -- {Quote(normalizedPath)}",
                ProjectRoot,
                CliCommandRunner.DefaultTimeoutMs,
                cancellationToken);
            if (!parentStatus.IsSuccess)
            {
                error = BuildCommandError("Failed to inspect the parent gitlink before updating", parentStatus);
                return false;
            }

            if (!TryRequireCompleteStructuralOutput(
                    parentStatus,
                    "Parent gitlink status inspection",
                    out error))
                return false;

            if (!string.IsNullOrWhiteSpace(parentStatus.StdOut))
            {
                error =
                    "Update was blocked because the parent repository already has a staged or unstaged change for this submodule revision.";
                return false;
            }

            var statusResult = RunGit(
                $"-C {Quote(normalizedPath)} status --porcelain=v2 --untracked-files=all --ignored=matching",
                ProjectRoot,
                CliCommandRunner.DefaultTimeoutMs,
                cancellationToken);
            if (!statusResult.IsSuccess)
            {
                error = BuildCommandError("Failed to inspect the package before updating", statusResult);
                return false;
            }

            if (!TryRequireCompleteStructuralOutput(
                    statusResult,
                    "Package pre-update status inspection",
                    out error))
                return false;

            if (!string.IsNullOrWhiteSpace(statusResult.StdOut))
            {
                error = "Update was blocked because the package has modified, untracked, ignored, or conflicted files.";
                return false;
            }

            if (!TryGetInterruptedRepositoryOperation(
                    normalizedPath,
                    out string interruptedOperation,
                    out error,
                    cancellationToken))
                return false;
            if (!string.IsNullOrEmpty(interruptedOperation))
            {
                error = $"Update was blocked because the package has an unfinished {interruptedOperation} operation.";
                return false;
            }

            var headResult = RunGit(
                $"-C {Quote(normalizedPath)} rev-parse --verify HEAD",
                ProjectRoot,
                CliCommandRunner.DefaultTimeoutMs,
                cancellationToken);
            if (!headResult.IsSuccess || string.IsNullOrWhiteSpace(headResult.StdOut))
            {
                error = BuildCommandError("Failed to record the package's current commit", headResult);
                return false;
            }

            if (!TryRequireCompleteStructuralOutput(
                    headResult,
                    "Package pre-update commit inspection",
                    out error))
                return false;

            var localCommitResult = RunGit(
                $"-C {Quote(normalizedPath)} rev-list --count HEAD --not --remotes",
                ProjectRoot,
                CliCommandRunner.DefaultTimeoutMs,
                cancellationToken);
            if (!localCommitResult.IsSuccess)
            {
                error = BuildCommandError("Failed to check the package for unpushed commits", localCommitResult);
                return false;
            }

            if (!TryRequireCompleteStructuralOutput(
                    localCommitResult,
                    "Package unpushed-commit inspection",
                    out error))
                return false;

            if (!int.TryParse(localCommitResult.StdOut.Trim(), out int localCommitCount))
            {
                error = "Git returned an unexpected result while checking the package for unpushed commits.";
                return false;
            }

            if (localCommitCount > 0)
            {
                error =
                    $"Update was blocked because the package's current commit has {localCommitCount} commit(s) not reachable from any remote-tracking ref.";
                return false;
            }

            plan = new SubmoduleUpdatePlan
            {
                Path = normalizedPath,
                StartingCommit = headResult.StdOut.Trim(),
                ExpectedRepositoryUrl = expectedUrl?.Trim() ?? string.Empty,
                ExpectedBranch = configuredBranch
            };
            return true;
        }

        internal static bool TryValidateSubmoduleConfiguredBranch(
            string path,
            string expectedBranch,
            out string configuredBranch,
            out string error,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            configuredBranch = string.Empty;
            error = string.Empty;
            string normalizedPath = NormalizePath(path);
            if (!TryFindSubmoduleNameForPath(
                    normalizedPath,
                    out string submoduleName,
                    out bool found,
                    out error,
                    cancellationToken) ||
                !found)
            {
                if (string.IsNullOrWhiteSpace(error))
                    error = ".gitmodules no longer registers the package branch being validated.";
                return false;
            }

            var branchResult = RunGit(
                $"config --file .gitmodules --get {Quote("submodule." + submoduleName + ".branch")}",
                ProjectRoot,
                5000,
                cancellationToken);
            if (!TryRequireCompleteStructuralOutput(
                    branchResult,
                    "Configured submodule branch inspection",
                    out error))
                return false;

            if (branchResult.IsSuccess)
                configuredBranch = branchResult.StdOut.Trim();
            else if (branchResult.ExitCode != 1)
            {
                error = BuildCommandError("Failed to inspect the configured submodule branch", branchResult);
                return false;
            }

            if (!string.IsNullOrEmpty(configuredBranch) &&
                !IsValidBranchName(configuredBranch))
            {
                error = "The tracked submodule branch in .gitmodules is invalid.";
                return false;
            }

            if (expectedBranch != null &&
                !string.Equals(
                    configuredBranch,
                    expectedBranch.Trim(),
                    StringComparison.Ordinal))
            {
                error =
                    "The tracked submodule branch changed after the package list or update preview was loaded. Refresh and review the update again.";
                return false;
            }

            return true;
        }

        internal static string BuildInitializeSubmoduleArguments(string path)
        {
            return $"submodule update --init --checkout -- {Quote(NormalizePath(path))}";
        }

        internal static bool TryPrepareSubmoduleInitialization(
            string path,
            out string error,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            return TryPrepareSubmoduleInitialization(
                path,
                string.Empty,
                out error,
                cancellationToken);
        }

        internal static bool TryPrepareSubmoduleInitialization(
            string path,
            string expectedUrl,
            out string error,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            error = string.Empty;
            string normalizedPath = NormalizePath(path);
            if (!IsPackagePath(normalizedPath))
            {
                error = "Package path is invalid.";
                return false;
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (!TryEnsureParentMutationStateIsSafe(out error, cancellationToken))
                return false;

            if (!TryFindSubmoduleNameForPath(
                    normalizedPath,
                    out string submoduleName,
                    out bool found,
                    out error,
                    cancellationToken) ||
                !found)
            {
                if (string.IsNullOrWhiteSpace(error))
                    error = ".gitmodules does not register the package being initialized.";
                return false;
            }

            var configuredUrlResult = RunGit(
                $"config --file .gitmodules --get {Quote("submodule." + submoduleName + ".url")}",
                ProjectRoot,
                5000,
                cancellationToken);
            if (!TryRequireCompleteStructuralOutput(
                    configuredUrlResult,
                    "Registered submodule URL inspection",
                    out error))
                return false;

            if (!configuredUrlResult.IsSuccess || string.IsNullOrWhiteSpace(configuredUrlResult.StdOut))
            {
                error = BuildCommandError("Failed to read the registered package URL", configuredUrlResult);
                return false;
            }

            string configuredUrl = configuredUrlResult.StdOut.Trim();
            if (!TryValidateExistingRepositoryUrl(
                    configuredUrl,
                    "The repository URL stored in .gitmodules",
                    out error))
                return false;

            if (!string.IsNullOrWhiteSpace(expectedUrl) &&
                !TryValidateExistingRepositoryUrl(
                    expectedUrl,
                    "The repository URL loaded for this package",
                    out error))
                return false;

            if (!string.IsNullOrWhiteSpace(expectedUrl) &&
                !AreRepositoryUrlsEquivalent(configuredUrl, expectedUrl))
            {
                error =
                    "The package URL changed after the package list was loaded. Refresh and review the registration before initializing.";
                return false;
            }

            var localUrlResult = RunGit(
                $"config --get {Quote("submodule." + submoduleName + ".url")}",
                ProjectRoot,
                5000,
                cancellationToken);
            if (!TryRequireCompleteStructuralOutput(
                    localUrlResult,
                    "Initialized submodule URL inspection",
                    out error))
                return false;

            bool hasLocalUrl = localUrlResult.IsSuccess && !string.IsNullOrWhiteSpace(localUrlResult.StdOut);
            if (!hasLocalUrl && localUrlResult.ExitCode != 1)
            {
                error = BuildCommandError("Failed to inspect the initialized submodule URL", localUrlResult);
                return false;
            }

            string approvedInitializedUrl = hasLocalUrl
                ? localUrlResult.StdOut.Trim()
                : configuredUrl;
            if (hasLocalUrl &&
                !TryValidateExistingRepositoryUrl(
                    approvedInitializedUrl,
                    "The initialized submodule URL stored in the parent repository",
                    out error))
                return false;

            if (hasLocalUrl &&
                !IsRelativeLocalRepositoryUrl(configuredUrl) &&
                !AreRepositoryUrlsEquivalent(configuredUrl, approvedInitializedUrl))
            {
                error =
                    "The local initialized submodule URL does not match .gitmodules. Run Git submodule sync or repair it before initializing.";
                return false;
            }

            if (!TryResolveSubmoduleGitDir(
                    submoduleName,
                    out string moduleGitDir,
                    out error,
                    cancellationToken) ||
                !TryValidateSubmoduleMetadataPath(moduleGitDir, out error, cancellationToken))
                return false;
            if (Directory.Exists(moduleGitDir))
            {
                if (!hasLocalUrl && IsRelativeLocalRepositoryUrl(configuredUrl))
                {
                    error =
                        "Stale Git metadata exists for a relative submodule URL, but its resolved origin cannot be proven before initialization. Run Git submodule sync/init manually and refresh.";
                    return false;
                }

                var metadataOrigin = RunGit(
                    $"--git-dir {Quote(moduleGitDir)} --work-tree {Quote(Path.Combine(ProjectRoot, normalizedPath))} config --get remote.origin.url",
                    ProjectRoot,
                    5000,
                    cancellationToken);
                if (!TryRequireCompleteStructuralOutput(
                        metadataOrigin,
                        "Existing submodule metadata origin inspection",
                        out error))
                    return false;

                string metadataOriginUrl = metadataOrigin.StdOut?.Trim() ?? string.Empty;
                if (!metadataOrigin.IsSuccess ||
                    !TryValidateExistingRepositoryUrl(
                        metadataOriginUrl,
                        "The existing submodule metadata origin URL",
                        out error) ||
                    !AreRepositoryUrlsEquivalent(metadataOriginUrl, approvedInitializedUrl))
                {
                    if (string.IsNullOrWhiteSpace(error))
                    {
                        error =
                            "Existing submodule metadata points to a different origin. It was preserved; repair or move it before initializing.";
                    }
                    return false;
                }
            }

            var indexResult = RunGit(
                $"ls-files --stage -- {Quote(normalizedPath)}",
                ProjectRoot,
                5000,
                cancellationToken);
            if (!TryParseGitlink(indexResult, normalizedPath, out _, out error))
                return false;

            string packagePath = Path.Combine(ProjectRoot, normalizedPath);
            if (File.Exists(packagePath) && !Directory.Exists(packagePath))
            {
                error = "The package destination is a file. Move it away before initializing the submodule.";
                return false;
            }

            if (!Directory.Exists(packagePath))
                return true;

            if (!TryInspectExactSubmoduleWorktree(
                    normalizedPath,
                    out bool isExactWorktree,
                    out error,
                    cancellationToken))
                return false;

            if (isExactWorktree)
            {
                error =
                    "The package is already an initialized Git worktree. Refresh the package list and review its current commit; initialization was not run against stale UI state.";
                return false;
            }

            try
            {
                if (Directory.GetFileSystemEntries(packagePath).Length > 0)
                {
                    error =
                        "The deinitialized package destination contains files. Move them to safety and leave the directory empty before initializing; Git checkout was not started.";
                    return false;
                }
            }
            catch (Exception ex)
            {
                error = $"Failed to inspect the package destination before initialization: {ex.Message}";
                return false;
            }

            return true;
        }

        internal static bool TryVerifyInitializedSubmodule(
            string path,
            out string error,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            return TryVerifyInitializedSubmodule(
                path,
                string.Empty,
                out error,
                cancellationToken);
        }

        internal static bool TryVerifyInitializedSubmodule(
            string path,
            string expectedUrl,
            out string error,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            error = string.Empty;
            string normalizedPath = NormalizePath(path);
            if (!TryValidateSubmoduleUpdateSource(
                    normalizedPath,
                    expectedUrl,
                    out error,
                    cancellationToken))
                return false;
            if (!TryVerifySubmoduleClean(normalizedPath, out error, cancellationToken))
                return false;

            var indexResult = RunGit(
                $"ls-files --stage -- {Quote(normalizedPath)}",
                ProjectRoot,
                5000,
                cancellationToken);
            if (!TryParseGitlink(indexResult, normalizedPath, out string pinnedCommit, out error))
                return false;

            var headResult = RunGit(
                $"-C {Quote(normalizedPath)} rev-parse --verify HEAD",
                ProjectRoot,
                5000,
                cancellationToken);
            if (!TryRequireCompleteStructuralOutput(
                    headResult,
                    "Initialized package commit verification",
                    out error))
                return false;

            if (!headResult.IsSuccess ||
                !string.Equals(
                    headResult.StdOut.Trim(),
                    pinnedCommit,
                    StringComparison.OrdinalIgnoreCase))
            {
                error = "The initialized package HEAD does not match the commit pinned by the parent gitlink.";
                return false;
            }

            return true;
        }

        private static bool TryParseGitlink(
            CommandResult indexResult,
            string normalizedPath,
            out string commit,
            out string error)
        {
            commit = string.Empty;
            error = string.Empty;
            if (!TryRequireCompleteStructuralOutput(
                    indexResult,
                    "Parent gitlink inspection",
                    out error))
                return false;

            if (indexResult == null || !indexResult.IsSuccess || string.IsNullOrWhiteSpace(indexResult.StdOut))
            {
                error = BuildCommandError("The parent index does not contain the expected package gitlink", indexResult);
                return false;
            }

            Match match = Regex.Match(
                indexResult.StdOut.Trim(),
                @"^160000\s+([0-9a-fA-F]{40,64})\s+0\t(.+)$",
                RegexOptions.CultureInvariant);
            if (!match.Success ||
                !string.Equals(
                    NormalizePath(match.Groups[2].Value),
                    normalizedPath,
                    StringComparison.Ordinal))
            {
                error = "The parent index entry for the package is not a valid submodule gitlink.";
                return false;
            }

            commit = match.Groups[1].Value;
            return true;
        }

        internal static string BuildFetchSubmoduleArguments(string path)
        {
            return $"-C {Quote(NormalizePath(path))} fetch --prune --no-tags origin";
        }

        internal static string BuildCheckoutSubmoduleArguments(string path, string commit)
        {
            return $"-C {Quote(NormalizePath(path))} checkout --no-overwrite-ignore --detach {Quote(commit?.Trim() ?? string.Empty)}";
        }

        internal static bool TryResolveSubmoduleRemoteTarget(
            string path,
            string configuredBranch,
            out string targetCommit,
            out string targetLabel,
            out string error)
        {
            return TryResolveSubmoduleRemoteTarget(
                path,
                configuredBranch,
                string.Empty,
                out targetCommit,
                out targetLabel,
                out error);
        }

        internal static bool TryResolveSubmoduleRemoteTarget(
            string path,
            string configuredBranch,
            string expectedUrl,
            out string targetCommit,
            out string targetLabel,
            out string error,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            targetCommit = string.Empty;
            targetLabel = string.Empty;
            error = string.Empty;
            string normalizedPath = NormalizePath(path);
            if (!IsPackagePath(normalizedPath))
            {
                error = "Package path is invalid.";
                return false;
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (!TryValidateSubmoduleUpdateSource(
                    normalizedPath,
                    expectedUrl,
                    out error,
                    cancellationToken))
                return false;

            string branch = configuredBranch?.Trim() ?? string.Empty;
            string targetRef;
            if (string.IsNullOrEmpty(branch))
            {
                var headResult = RunGit(
                    $"-C {Quote(normalizedPath)} symbolic-ref refs/remotes/origin/HEAD",
                    ProjectRoot,
                    5000,
                    cancellationToken);
                if (!TryRequireCompleteStructuralOutput(
                        headResult,
                        "Remote default-branch inspection",
                        out error))
                    return false;

                if (!headResult.IsSuccess || string.IsNullOrWhiteSpace(headResult.StdOut))
                {
                    error =
                        "The remote default branch could not be determined. Configure an explicit branch before updating this package.";
                    return false;
                }

                targetRef = headResult.StdOut.Trim();
                const string remotePrefix = "refs/remotes/origin/";
                if (!targetRef.StartsWith(remotePrefix, StringComparison.Ordinal) ||
                    targetRef.Length <= remotePrefix.Length)
                {
                    error =
                        "The remote default-branch symbolic ref points outside refs/remotes/origin/. Configure an explicit trusted branch before updating.";
                    return false;
                }

                targetLabel = targetRef.Substring(remotePrefix.Length);
                if (!IsValidBranchName(targetLabel))
                {
                    error = "The remote default-branch symbolic ref contains an invalid branch name.";
                    return false;
                }
            }
            else if (string.Equals(branch, ".", StringComparison.Ordinal))
            {
                var parentBranchResult = RunGit(
                    "symbolic-ref --quiet --short HEAD",
                    ProjectRoot,
                    5000,
                    cancellationToken);
                if (!TryRequireCompleteStructuralOutput(
                        parentBranchResult,
                        "Parent branch inspection for branch = .",
                        out error))
                    return false;

                if (!parentBranchResult.IsSuccess || string.IsNullOrWhiteSpace(parentBranchResult.StdOut))
                {
                    error =
                        "This submodule uses branch = ., but the parent repository is detached or has no named branch. " +
                        "Check out a named parent branch or configure an explicit submodule branch before updating.";
                    return false;
                }

                targetLabel = parentBranchResult.StdOut.Trim();
                if (!IsValidBranchName(targetLabel) ||
                    string.Equals(targetLabel, ".", StringComparison.Ordinal))
                {
                    error = "Git returned an invalid parent branch name while resolving branch = .";
                    return false;
                }

                targetRef = "refs/remotes/origin/" + targetLabel;
            }
            else
            {
                if (!IsValidBranchName(branch))
                {
                    error = "The configured submodule branch is invalid.";
                    return false;
                }

                targetLabel = branch;
                targetRef = "refs/remotes/origin/" + branch;
            }

            var targetResult = RunGit(
                $"-C {Quote(normalizedPath)} rev-parse --verify {Quote(targetRef + "^{commit}")}",
                ProjectRoot,
                5000,
                cancellationToken);
            if (!TryRequireCompleteStructuralOutput(
                    targetResult,
                    "Fetched branch target inspection",
                    out error))
                return false;

            if (!targetResult.IsSuccess || string.IsNullOrWhiteSpace(targetResult.StdOut))
            {
                error = BuildCommandError($"Failed to resolve the fetched {targetLabel} branch", targetResult);
                return false;
            }

            targetCommit = targetResult.StdOut.Trim();
            return true;
        }

        internal static bool TryRecoverFailedSubmoduleUpdate(
            SubmoduleUpdatePlan plan,
            out string error,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            error = string.Empty;
            if (plan == null || !IsPackagePath(plan.Path) || string.IsNullOrWhiteSpace(plan.StartingCommit))
            {
                error = "The update recovery plan is invalid.";
                return false;
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (!TryValidateSubmoduleUpdateSource(
                    plan.Path,
                    plan.ExpectedRepositoryUrl,
                    out error,
                    cancellationToken))
                return false;

            if (!TryGetInterruptedRepositoryOperation(
                    plan.Path,
                    out string interruptedOperation,
                    out error,
                    cancellationToken))
                return false;
            if (!string.IsNullOrEmpty(interruptedOperation))
            {
                error =
                    $"Automatic recovery was skipped because a {interruptedOperation} operation appeared after the update began. No abort or reset was attempted.";
                return false;
            }

            if (!TryVerifySubmoduleClean(plan.Path, out error, cancellationToken))
            {
                error =
                    "Automatic recovery was skipped because the package changed after the update began. " + error;
                return false;
            }

            var currentHeadResult = RunGit(
                $"-C {Quote(plan.Path)} rev-parse --verify HEAD",
                ProjectRoot,
                5000,
                cancellationToken);
            if (!TryRequireCompleteStructuralOutput(
                    currentHeadResult,
                    "Update-recovery commit inspection",
                    out error))
                return false;

            if (!currentHeadResult.IsSuccess || string.IsNullOrWhiteSpace(currentHeadResult.StdOut))
            {
                error = BuildCommandError("Failed to inspect the package before update recovery", currentHeadResult);
                return false;
            }

            string currentHead = currentHeadResult.StdOut.Trim();
            bool isStartingCommit = string.Equals(currentHead, plan.StartingCommit, StringComparison.OrdinalIgnoreCase);
            bool isExpectedTarget = !string.IsNullOrWhiteSpace(plan.ExpectedTargetCommit) &&
                                    string.Equals(currentHead, plan.ExpectedTargetCommit, StringComparison.OrdinalIgnoreCase);
            if (!isStartingCommit && !isExpectedTarget)
            {
                error =
                    "Automatic recovery was skipped because HEAD moved to an unexpected commit after the update began. The commit was preserved.";
                return false;
            }

            if (!isStartingCommit)
            {
                var checkoutResult = RunGit(
                    $"-C {Quote(plan.Path)} checkout --no-overwrite-ignore --detach {Quote(plan.StartingCommit)}",
                    ProjectRoot,
                    CliCommandRunner.DefaultTimeoutMs,
                    cancellationToken);
                if (!checkoutResult.IsSuccess)
                {
                    error = BuildCommandError("Failed to restore the package's starting commit", checkoutResult);
                    return false;
                }
            }

            if (!TryVerifySubmoduleClean(plan.Path, out error, cancellationToken))
                return false;

            var restoredHead = RunGit(
                $"-C {Quote(plan.Path)} rev-parse --verify HEAD",
                ProjectRoot,
                5000,
                cancellationToken);
            if (!TryRequireCompleteStructuralOutput(
                    restoredHead,
                    "Restored package commit verification",
                    out error))
                return false;

            if (!restoredHead.IsSuccess ||
                !string.Equals(restoredHead.StdOut.Trim(), plan.StartingCommit, StringComparison.OrdinalIgnoreCase))
            {
                error = "Git did not restore the package to the exact commit recorded before the update.";
                return false;
            }

            return true;
        }

        internal static bool TryVerifySubmoduleClean(
            string path,
            out string error,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            error = string.Empty;
            string normalizedPath = NormalizePath(path);
            if (!TryValidateExactSubmoduleWorktree(normalizedPath, out error, cancellationToken))
                return false;
            var statusResult = RunGit(
                $"-C {Quote(normalizedPath)} status --porcelain=v2 --untracked-files=all --ignored=matching",
                ProjectRoot,
                CliCommandRunner.DefaultTimeoutMs,
                cancellationToken);
            if (!statusResult.IsSuccess)
            {
                error = BuildCommandError("Failed to verify the package state", statusResult);
                return false;
            }

            if (!TryRequireCompleteStructuralOutput(
                    statusResult,
                    "Package clean-state verification",
                    out error))
                return false;

            if (!string.IsNullOrWhiteSpace(statusResult.StdOut))
            {
                error = "The package still contains local changes, ignored files, or conflicts, so Unity assets were not refreshed.";
                return false;
            }

            return true;
        }

        private static bool TryGetInterruptedRepositoryOperation(
            string normalizedPath,
            out string operation,
            out string error,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            operation = string.Empty;
            error = string.Empty;
            var markers = new[]
            {
                new KeyValuePair<string, string>("MERGE_HEAD", "merge"),
                new KeyValuePair<string, string>("rebase-merge", "rebase"),
                new KeyValuePair<string, string>("rebase-apply", "rebase"),
                new KeyValuePair<string, string>("CHERRY_PICK_HEAD", "cherry-pick"),
                new KeyValuePair<string, string>("REVERT_HEAD", "revert")
            };

            foreach (var marker in markers)
            {
                var result = RunGit(
                    $"-C {Quote(normalizedPath)} rev-parse --git-path {Quote(marker.Key)}",
                    ProjectRoot,
                    5000,
                    cancellationToken);
                if (!TryRequireCompleteStructuralOutput(
                        result,
                        "Interrupted Git-operation inspection",
                        out error))
                    return false;

                if (!result.IsSuccess || string.IsNullOrWhiteSpace(result.StdOut))
                {
                    error = BuildCommandError(
                        "Failed to inspect the package for interrupted Git operations",
                        result);
                    return false;
                }

                try
                {
                    string value = result.StdOut.Trim();
                    string markerPath = Path.GetFullPath(
                        Path.IsPathRooted(value)
                            ? value
                            : Path.Combine(ProjectRoot, normalizedPath, value));
                    if (!File.Exists(markerPath) && !Directory.Exists(markerPath))
                        continue;

                    operation = marker.Value;
                    return true;
                }
                catch (Exception ex)
                {
                    error = $"Git returned an invalid interrupted-operation marker path: {ex.Message}";
                    return false;
                }
            }

            return true;
        }

        internal static bool TrySetSubmoduleBranch(
            string path,
            string branch,
            out string error,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            return TrySetSubmoduleBranch(
                path,
                branch,
                out error,
                out _,
                cancellationToken);
        }

        internal static bool TrySetSubmoduleBranch(
            string path,
            string branch,
            out string error,
            out GitOperationCompletionOutcome outcome,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            error = string.Empty;
            outcome = GitOperationCompletionOutcome.FailedButRolledBack;
            if (string.IsNullOrWhiteSpace(branch))
            {
                error = "Branch name is required.";
                return false;
            }

            string root = ProjectRoot;
            string normalizedPath = NormalizePath(path);
            string trimmedBranch = branch.Trim();
            if (!IsPackagePath(normalizedPath) || !IsValidBranchName(trimmedBranch))
            {
                error = "Package path or branch name is invalid.";
                return false;
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (!TryEnsureParentMutationStateIsSafe(out error, cancellationToken))
                return false;

            if (!TryFindSubmoduleNameForPath(
                    normalizedPath,
                    out string expectedSubmoduleName,
                    out bool isRegistered,
                    out error,
                    cancellationToken))
                return false;

            if (!isRegistered || string.IsNullOrWhiteSpace(expectedSubmoduleName))
            {
                error = ".gitmodules does not register the selected package path.";
                return false;
            }

            cancellationToken.ThrowIfCancellationRequested();
            outcome = GitOperationCompletionOutcome.FailedUnsafe;
            var result = RunGit(
                $"submodule set-branch --branch {Quote(trimmedBranch)} -- {Quote(normalizedPath)}",
                root,
                CliCommandRunner.DefaultTimeoutMs,
                cancellationToken);
            if (!result.IsSuccess)
            {
                error = BuildCommandError("Failed to change submodule branch", result);
                return false;
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (!TryFindSubmoduleNameForPath(
                    normalizedPath,
                    out string currentSubmoduleName,
                    out bool isStillRegistered,
                    out error,
                    cancellationToken))
                return false;

            if (!isStillRegistered ||
                !string.Equals(
                    expectedSubmoduleName,
                    currentSubmoduleName,
                    StringComparison.Ordinal))
            {
                error =
                    "Git changed .gitmodules, but the package registration no longer matches the verified submodule. " +
                    "Inspect the repository before retrying.";
                return false;
            }

            var branchResult = RunGit(
                $"config --file .gitmodules --get {Quote("submodule." + currentSubmoduleName + ".branch")}",
                root,
                5000,
                cancellationToken);
            if (!TryRequireCompleteStructuralOutput(
                    branchResult,
                    ".gitmodules branch postcondition",
                    out error))
                return false;

            if (!branchResult.IsSuccess ||
                !string.Equals(
                    branchResult.StdOut?.Trim(),
                    trimmedBranch,
                    StringComparison.Ordinal))
            {
                error = branchResult.IsSuccess
                    ? "Git reported success, but the .gitmodules branch postcondition does not contain the requested tracked branch. Inspect the repository before retrying."
                    : BuildCommandError(
                        "Git changed the tracked branch, but the .gitmodules postcondition could not be verified",
                        branchResult);
                return false;
            }

            outcome = GitOperationCompletionOutcome.Succeeded;
            return true;
        }

        private static bool TryEnsureParentMutationStateIsSafe(
            out string error,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            error = string.Empty;

            cancellationToken.ThrowIfCancellationRequested();
            if (!TryValidateProjectGitRoot(out error, cancellationToken))
                return false;

            if (!TryValidateGitModulesFile(out error, cancellationToken))
                return false;

            if (!TryRunQuietCheck(
                    "diff --quiet -- .gitmodules",
                    "The working copy of .gitmodules has unrelated changes. Commit or stash them before changing submodules.",
                    out error,
                    cancellationToken))
                return false;

            if (!TryRunQuietCheck(
                    "diff --cached --quiet -- .gitmodules",
                    "The staged copy of .gitmodules has unrelated changes. Commit or stash them before changing submodules.",
                    out error,
                    cancellationToken))
                return false;

            var conflictResult = RunGit(
                "diff --name-only --diff-filter=U",
                ProjectRoot,
                CliCommandRunner.DefaultTimeoutMs,
                cancellationToken);
            if (!conflictResult.IsSuccess)
            {
                error = BuildCommandError("Failed to check the parent repository for conflicts", conflictResult);
                return false;
            }

            if (!TryRequireCompleteStructuralOutput(
                    conflictResult,
                    "Parent conflict inspection",
                    out error))
                return false;

            if (!string.IsNullOrWhiteSpace(conflictResult.StdOut))
            {
                error = "The parent repository has unresolved conflicts. Resolve them before changing submodules.";
                return false;
            }

            var indexResult = RunGit(
                "rev-parse --git-path index",
                ProjectRoot,
                CliCommandRunner.DefaultTimeoutMs,
                cancellationToken);
            if (!indexResult.IsSuccess || string.IsNullOrWhiteSpace(indexResult.StdOut))
            {
                error = BuildCommandError("Failed to locate the parent Git index", indexResult);
                return false;
            }

            if (!TryRequireCompleteStructuralOutput(
                    indexResult,
                    "Parent index-path inspection",
                    out error))
                return false;

            try
            {
                string value = indexResult.StdOut.Trim();
                string indexPath = Path.GetFullPath(
                    Path.IsPathRooted(value) ? value : Path.Combine(ProjectRoot, value));
                if (File.Exists(indexPath + ".lock"))
                {
                    error = "Another Git operation is using the parent repository index. Wait for it to finish before retrying.";
                    return false;
                }
            }
            catch (Exception ex)
            {
                error = $"Git returned an invalid parent-index path: {ex.Message}";
                return false;
            }

            return true;
        }

        private static bool TryValidateProjectGitRoot(
            out string error,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            error = string.Empty;
            var rootResult = RunGit(
                "rev-parse --show-toplevel",
                ProjectRoot,
                5000,
                cancellationToken);
            if (!TryRequireCompleteStructuralOutput(
                    rootResult,
                    "Project Git-root inspection",
                    out error))
                return false;

            if (!rootResult.IsSuccess || string.IsNullOrWhiteSpace(rootResult.StdOut))
            {
                error = BuildCommandError(
                    "The Unity project root is not an initialized Git repository",
                    rootResult);
                return false;
            }

            var insideWorktreeResult = RunGit(
                "rev-parse --is-inside-work-tree",
                ProjectRoot,
                5000,
                cancellationToken);
            if (!TryRequireCompleteStructuralOutput(
                    insideWorktreeResult,
                    "Project worktree inspection",
                    out error))
                return false;

            if (!insideWorktreeResult.IsSuccess)
            {
                error = BuildCommandError(
                    "Failed to verify that the Unity project is inside the resolved Git worktree",
                    insideWorktreeResult);
                return false;
            }

            if (!string.Equals(
                    insideWorktreeResult.StdOut.Trim(),
                    "true",
                    StringComparison.Ordinal))
            {
                error =
                    "The Unity project Git directory resolves to a worktree outside the project. " +
                    "Repository mutations through redirected core.worktree configuration are blocked.";
                return false;
            }

            // Comparing textual paths is not sufficient here: macOS commonly
            // exposes /var through the /private/var filesystem alias, and users
            // may open an otherwise valid repository through an equivalent
            // filesystem path. Git's prefix is empty only when the supplied
            // working directory itself is the worktree root, while remaining
            // non-empty for a Unity project nested inside an ancestor repo.
            var prefixResult = RunGit(
                "rev-parse --show-prefix",
                ProjectRoot,
                5000,
                cancellationToken);
            if (!TryRequireCompleteStructuralOutput(
                    prefixResult,
                    "Project worktree-prefix inspection",
                    out error))
                return false;

            if (!prefixResult.IsSuccess)
            {
                error = BuildCommandError(
                    "Failed to verify that the Unity project is the Git repository root",
                    prefixResult);
                return false;
            }

            if (!string.IsNullOrWhiteSpace(prefixResult.StdOut))
            {
                error =
                    "Git Package Manager requires the Unity project root to be the Git repository root. " +
                    $"Git resolved {RedactCredentials(rootResult.StdOut.Trim())} instead, so the operation was blocked to avoid changing an ancestor repository.";
                return false;
            }

            return true;
        }

        private static bool TryValidateGitModulesFile(
            out string error,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            error = string.Empty;
            string gitModulesPath = Path.Combine(ProjectRoot, ".gitmodules");

            var indexEntryResult = RunGit(
                "ls-files --stage -- .gitmodules",
                ProjectRoot,
                CliCommandRunner.DefaultTimeoutMs,
                cancellationToken);
            if (!TryRequireCompleteStructuralOutput(
                    indexEntryResult,
                    ".gitmodules index inspection",
                    out error))
                return false;

            if (!indexEntryResult.IsSuccess)
            {
                error = BuildCommandError("Failed to inspect the .gitmodules index entry", indexEntryResult);
                return false;
            }

            string indexEntry = (indexEntryResult.StdOut ?? string.Empty).Trim();
            bool isTracked = !string.IsNullOrEmpty(indexEntry);
            if (isTracked &&
                !indexEntry.StartsWith("100644 ", StringComparison.Ordinal) &&
                !indexEntry.StartsWith("100755 ", StringComparison.Ordinal))
            {
                error = ".gitmodules is not a regular file in the parent Git index. Replace it with a tracked regular file before changing submodules.";
                return false;
            }

            // Include ignored entries so an ignored or dangling .gitmodules
            // symlink cannot be treated as an absent file and then written
            // through by `git submodule add`.
            var untrackedResult = RunGit(
                "status --porcelain=v2 --untracked-files=all --ignored=matching -- .gitmodules",
                ProjectRoot,
                CliCommandRunner.DefaultTimeoutMs,
                cancellationToken);
            if (!TryRequireCompleteStructuralOutput(
                    untrackedResult,
                    ".gitmodules worktree inspection",
                    out error))
                return false;

            if (!untrackedResult.IsSuccess)
            {
                error = BuildCommandError("Failed to inspect the .gitmodules worktree entry", untrackedResult);
                return false;
            }

            string worktreeStatus = untrackedResult.StdOut ?? string.Empty;
            if (!isTracked && !string.IsNullOrWhiteSpace(worktreeStatus))
            {
                error = "An untracked or ignored .gitmodules entry already exists. Move it away or add and commit a regular .gitmodules file before changing submodules.";
                return false;
            }

            bool pathExists = File.Exists(gitModulesPath) || Directory.Exists(gitModulesPath);
            if (!isTracked && pathExists)
            {
                error = "An untracked .gitmodules entry already exists. Move it away or add and commit a regular .gitmodules file before changing submodules.";
                return false;
            }

            if (!pathExists)
                return true;

            try
            {
                FileAttributes attributes = File.GetAttributes(gitModulesPath);
                if ((attributes & FileAttributes.Directory) != 0 ||
                    (attributes & FileAttributes.ReparsePoint) != 0)
                {
                    error = ".gitmodules must be a regular file, not a directory, symbolic link, junction, or other reparse point.";
                    return false;
                }
            }
            catch (Exception ex)
            {
                error = $"Failed to verify that .gitmodules is a regular file: {ex.Message}";
                return false;
            }

            return true;
        }

        private static bool TryRunQuietCheck(
            string arguments,
            string changedMessage,
            out string error,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            error = string.Empty;
            var result = RunGit(
                arguments,
                ProjectRoot,
                CliCommandRunner.DefaultTimeoutMs,
                cancellationToken);
            if (result.IsSuccess)
                return true;

            if (result.ExitCode == 1)
            {
                error = changedMessage;
                return false;
            }

            error = BuildCommandError("Failed to verify the parent repository state", result);
            return false;
        }

        private static bool TryFindSubmoduleNameForPath(
            string normalizedPath,
            out string submoduleName,
            out bool found,
            out string error,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            submoduleName = string.Empty;
            found = false;
            error = string.Empty;
            string gitModulesPath = Path.Combine(ProjectRoot, ".gitmodules");
            if (!File.Exists(gitModulesPath))
                return true;

            var configResult = RunGit(
                "config --null --file .gitmodules --list",
                ProjectRoot,
                CliCommandRunner.DefaultTimeoutMs,
                cancellationToken);
            if (!TryRequireCompleteStructuralOutput(
                    configResult,
                    ".gitmodules registration inspection",
                    out error))
                return false;

            if (!configResult.IsSuccess)
            {
                if (configResult.ExitCode == 1 && string.IsNullOrWhiteSpace(configResult.StdErr))
                    return true;

                error = BuildCommandError("Failed to inspect .gitmodules during cleanup", configResult);
                return false;
            }

            if (!TryParseNullConfigList(configResult.StdOut, out var config, out error))
                return false;

            foreach (string name in ExtractSubmoduleNamesFromConfig(config))
            {
                if (!config.TryGetValue($"submodule.{name}.path", out string configuredPath))
                    continue;

                if (!string.Equals(NormalizePath(configuredPath), normalizedPath, StringComparison.Ordinal))
                    continue;

                if (found)
                {
                    submoduleName = string.Empty;
                    error =
                        ".gitmodules registers more than one submodule for the same package path. " +
                        "Resolve the ambiguous registrations before modifying the package.";
                    return false;
                }

                submoduleName = name;
                found = true;
            }

            return true;
        }

        // ── Internals ──

        private static bool TryRequireCompleteStructuralOutput(
            CommandResult result,
            string description,
            out string error)
        {
            error = string.Empty;
            if (result == null || !result.StdOutTruncated)
                return true;

            error =
                $"{description} returned truncated Git output. The operation was blocked because the repository state could not be verified completely.";
            return false;
        }

        private static bool TryValidateExistingRepositoryUrl(
            string repositoryUrl,
            string description,
            out string error)
        {
            error = string.Empty;
            if (IsValidRepositoryUrl(repositoryUrl))
                return true;

            error =
                $"{description} is invalid or uses an unsafe transport. Use HTTPS, SSH, or an explicit local path without embedded credentials; plaintext HTTP and Git transports are blocked.";
            return false;
        }

        internal static CommandResult RunGit(string arguments, string workingDir, int timeoutMs = CliCommandRunner.DefaultTimeoutMs)
        {
            CommandResult result = CliCommandRunner.Run(GitExecutable, arguments, workingDir, timeoutMs);
            TrackCommandTermination(result);
            return result;
        }

        internal static CommandResult RunGit(
            string arguments,
            string workingDir,
            int timeoutMs,
            CancellationToken cancellationToken)
        {
            CommandResult result = CliCommandRunner.Run(
                GitExecutable,
                arguments,
                workingDir,
                timeoutMs,
                cancellationToken);
            TrackCommandTermination(result);
            return result;
        }

        internal static void ResetCommandSafetyState()
        {
            commandTerminationUnconfirmed = false;
        }

        internal static bool ConsumeUnconfirmedCommandTermination()
        {
            bool value = commandTerminationUnconfirmed;
            commandTerminationUnconfirmed = false;
            return value;
        }

        private static void TrackCommandTermination(CommandResult result)
        {
            if (result != null && !result.TerminationConfirmed)
                commandTerminationUnconfirmed = true;
        }

        private static bool TryParseNullConfigList(
            string output,
            out Dictionary<string, string> config,
            out string error)
        {
            config = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            error = string.Empty;
            if (string.IsNullOrEmpty(output))
                return true;

            int recordStart = 0;
            while (recordStart < output.Length)
            {
                int recordEnd = output.IndexOf('\0', recordStart);
                if (recordEnd < 0 || recordEnd == recordStart)
                {
                    error =
                        "Git returned malformed NUL-delimited .gitmodules configuration output. " +
                        "The operation was blocked because the repository registration could not be verified.";
                    config.Clear();
                    return false;
                }

                string record = output.Substring(recordStart, recordEnd - recordStart);
                int separator = record.IndexOf('\n');
                if (separator <= 0)
                {
                    error =
                        "Git returned malformed NUL-delimited .gitmodules configuration output. " +
                        "The operation was blocked because the repository registration could not be verified.";
                    config.Clear();
                    return false;
                }

                string key = record.Substring(0, separator);
                if (key.Length > MaxRepositoryUrlLength ||
                    key.IndexOf('\r') >= 0 ||
                    !string.Equals(key, key.Trim(), StringComparison.Ordinal))
                {
                    error =
                        "Git returned an invalid .gitmodules configuration key. " +
                        "The operation was blocked because the repository registration could not be verified.";
                    config.Clear();
                    return false;
                }

                string value = record.Substring(separator + 1);
                if (config.ContainsKey(key))
                {
                    error =
                        ".gitmodules contains a duplicate configuration key. " +
                        "Resolve the ambiguous registration before modifying packages.";
                    config.Clear();
                    return false;
                }

                config.Add(key, value);
                recordStart = recordEnd + 1;
            }

            return true;
        }

        private static List<string> ExtractSubmoduleNamesFromConfig(Dictionary<string, string> config)
        {
            var names = new List<string>();
            const string prefix = "submodule.";
            const string suffix = ".path";

            foreach (var key in config.Keys)
            {
                if (key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                    key.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                {
                    string name = key.Substring(prefix.Length, key.Length - prefix.Length - suffix.Length);
                    if (!string.IsNullOrEmpty(name))
                        names.Add(name);
                }
            }

            return names;
        }

        internal static Dictionary<string, string> ParseSubmoduleCommitMap(string submoduleStatusOutput)
        {
            var commits = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(submoduleStatusOutput))
            {
                return commits;
            }

            foreach (Match match in SubmoduleStatusRegex.Matches(submoduleStatusOutput))
            {
                string commit = match.Groups[1].Value;
                string path = NormalizePath(match.Groups[2].Value);
                if (!string.IsNullOrEmpty(path))
                {
                    commits[path] = commit;
                }
            }

            return commits;
        }

        internal static List<string> ParseRemoteBranches(string lsRemoteOutput)
        {
            var branches = new List<string>();
            if (string.IsNullOrWhiteSpace(lsRemoteOutput))
            {
                return branches;
            }

            string[] lines = lsRemoteOutput.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (string line in lines)
            {
                int refsIndex = line.IndexOf("refs/heads/", StringComparison.Ordinal);
                if (refsIndex < 0)
                {
                    continue;
                }

                string branch = line.Substring(refsIndex + "refs/heads/".Length).Trim();
                if (!string.IsNullOrEmpty(branch))
                {
                    branches.Add(branch);
                }
            }

            branches.Sort(StringComparer.OrdinalIgnoreCase);
            return branches;
        }

        internal static string NormalizePath(string path)
        {
            return (path ?? string.Empty).Replace("\\", "/").Trim();
        }

        private static bool IsSubmoduleInitialized(string statusOutput, string path)
        {
            if (string.IsNullOrWhiteSpace(statusOutput))
                return false;

            string normalizedPath = NormalizePath(path);
            string[] lines = statusOutput.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (string line in lines)
            {
                string trimmed = line.TrimStart(' ', '+', '-', 'U');
                int separator = trimmed.IndexOf(' ');
                if (separator < 0)
                    continue;

                string remainder = trimmed.Substring(separator + 1);
                int pathEnd = remainder.IndexOf(' ');
                string candidatePath = NormalizePath(pathEnd >= 0 ? remainder.Substring(0, pathEnd) : remainder);
                if (string.Equals(candidatePath, normalizedPath, StringComparison.Ordinal))
                    return line.Length > 0 && line[0] != '-';
            }

            return false;
        }

        internal static string Quote(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return "\"\"";
            }

            var quoted = new StringBuilder(value.Length + 2);
            quoted.Append('"');
            int backslashCount = 0;
            foreach (char character in value)
            {
                if (character == '\\')
                {
                    backslashCount++;
                    continue;
                }

                if (character == '"')
                {
                    quoted.Append('\\', (backslashCount * 2) + 1);
                    quoted.Append('"');
                    backslashCount = 0;
                    continue;
                }

                if (backslashCount > 0)
                {
                    quoted.Append('\\', backslashCount);
                    backslashCount = 0;
                }

                quoted.Append(character);
            }

            // Backslashes immediately before the closing quote must be doubled
            // under Windows command-line parsing. This encoding is also accepted
            // by Unity's Mono Process implementation on macOS and Linux.
            if (backslashCount > 0)
                quoted.Append('\\', backslashCount * 2);
            quoted.Append('"');
            return quoted.ToString();
        }

        internal static string BuildCommandError(string message, CommandResult result)
        {
            if (result == null)
                return message;

            string detail = string.IsNullOrWhiteSpace(result.StdErr) ? result.StdOut : result.StdErr;
            detail = RedactCredentials(detail);
            if (string.IsNullOrWhiteSpace(detail))
                return result.ExitCode == 0 ? message : $"{message} (exit code {result.ExitCode}).";

            const int maxDetailLength = 4000;
            if (detail.Length > maxDetailLength)
                detail = detail.Substring(0, maxDetailLength) + "… [truncated]";

            return $"{message}: {detail.Trim()}";
        }

        internal static string RedactCredentials(string value)
        {
            if (string.IsNullOrEmpty(value))
                return value ?? string.Empty;

            string redacted = UriUserInfoRegex.Replace(value, match => $"{match.Groups["scheme"].Value}***@");
            redacted = SensitiveParameterRegex.Replace(redacted, match => $"{match.Groups["key"].Value}***");
            return BearerTokenRegex.Replace(redacted, match => $"{match.Groups["prefix"].Value}***");
        }

        private static bool IsLocalRepositoryUrl(string value)
        {
            if (Uri.TryCreate(value, UriKind.Absolute, out Uri uri) && uri.IsFile)
            {
                return string.IsNullOrEmpty(uri.UserInfo) &&
                       string.IsNullOrEmpty(uri.Query) &&
                       string.IsNullOrEmpty(uri.Fragment);
            }

            return Path.IsPathRooted(value) ||
                   value.StartsWith("./", StringComparison.Ordinal) ||
                   value.StartsWith("../", StringComparison.Ordinal) ||
                   value.StartsWith(@".\", StringComparison.Ordinal) ||
                   value.StartsWith(@"..\", StringComparison.Ordinal);
        }

        private static bool IsRelativeLocalRepositoryUrl(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || Path.IsPathRooted(value))
                return false;

            return value.StartsWith("./", StringComparison.Ordinal) ||
                   value.StartsWith("../", StringComparison.Ordinal) ||
                   value.StartsWith(@".\", StringComparison.Ordinal) ||
                   value.StartsWith(@"..\", StringComparison.Ordinal);
        }

        private static bool AreRepositoryUrlsEquivalent(string first, string second)
        {
            if (string.IsNullOrWhiteSpace(first) || string.IsNullOrWhiteSpace(second))
                return false;

            if (GitHubUtility.TryParseGitHubRepo(first, out string firstOwner, out string firstRepository) &&
                GitHubUtility.TryParseGitHubRepo(second, out string secondOwner, out string secondRepository))
            {
                return string.Equals(firstOwner, secondOwner, StringComparison.OrdinalIgnoreCase) &&
                       string.Equals(firstRepository, secondRepository, StringComparison.OrdinalIgnoreCase);
            }

            if (TryGetCanonicalLocalRepositoryPath(first, out string firstPath) &&
                TryGetCanonicalLocalRepositoryPath(second, out string secondPath))
            {
                var comparison = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal;
                return string.Equals(firstPath, secondPath, comparison);
            }

            if (Uri.TryCreate(first, UriKind.Absolute, out Uri firstUri) &&
                Uri.TryCreate(second, UriKind.Absolute, out Uri secondUri))
            {
                return string.Equals(firstUri.Scheme, secondUri.Scheme, StringComparison.OrdinalIgnoreCase) &&
                       string.Equals(firstUri.Host, secondUri.Host, StringComparison.OrdinalIgnoreCase) &&
                       firstUri.Port == secondUri.Port &&
                       string.Equals(firstUri.AbsolutePath.TrimEnd('/'), secondUri.AbsolutePath.TrimEnd('/'), StringComparison.Ordinal);
            }

            return string.Equals(first.Trim(), second.Trim(), StringComparison.Ordinal);
        }

        private static bool TryGetCanonicalLocalRepositoryPath(string value, out string path)
        {
            path = string.Empty;
            if (!IsLocalRepositoryUrl(value))
                return false;

            try
            {
                if (Uri.TryCreate(value, UriKind.Absolute, out Uri uri) && uri.IsFile)
                    path = Path.GetFullPath(uri.LocalPath);
                else
                    path = Path.GetFullPath(Path.IsPathRooted(value) ? value : Path.Combine(ProjectRoot, value));
                return true;
            }
            catch
            {
                return false;
            }
        }

        private sealed class DisposableAction : IDisposable
        {
            private Action action;

            internal DisposableAction(Action action)
            {
                this.action = action;
            }

            public void Dispose()
            {
                Interlocked.Exchange(ref action, null)?.Invoke();
            }
        }
    }
}
