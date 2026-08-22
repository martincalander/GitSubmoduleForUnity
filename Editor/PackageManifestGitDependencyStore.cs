using System;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MartinCalander.GitSubmoduleManager.Editor
{
    /// <summary>
    /// A direct, root-package Git dependency declared in Packages/manifest.json.
    /// The original manifest value is kept verbatim so conversion preconditions
    /// can compare it exactly instead of relying on a normalized URL.
    /// </summary>
    internal sealed class PackageManifestGitDependency
    {
        internal PackageManifestGitDependency(
            string packageName,
            string spec,
            string repositoryUrl,
            string revision,
            string packageSubfolder)
        {
            PackageName = packageName ?? string.Empty;
            Spec = spec ?? string.Empty;
            RepositoryUrl = repositoryUrl ?? string.Empty;
            Revision = revision ?? string.Empty;
            PackageSubfolder = packageSubfolder ?? string.Empty;
        }

        internal string PackageName { get; }
        internal string Spec { get; }
        internal string RepositoryUrl { get; }
        internal string Revision { get; }
        internal string PackageSubfolder { get; }
        internal bool IsRepositoryRootPackage =>
            string.IsNullOrEmpty(PackageSubfolder);
    }

    /// <summary>
    /// Compare-and-swap receipt for one successful manifest mutation. Rollback
    /// restores the exact original bytes only while the manifest still contains
    /// the exact bytes written by this mutation. Unrelated subsequent edits are
    /// never overwritten.
    /// </summary>
    internal sealed class PackageManifestDependencyMutation
    {
        private readonly string manifestPath;
        private readonly byte[] originalBytes;
        private readonly byte[] writtenBytes;
        private bool rolledBack;

        internal PackageManifestDependencyMutation(
            string manifestPath,
            byte[] originalBytes,
            byte[] writtenBytes,
            bool changed)
        {
            this.manifestPath = manifestPath ?? string.Empty;
            this.originalBytes = Clone(originalBytes);
            this.writtenBytes = Clone(writtenBytes);
            Changed = changed;
        }

        internal string ManifestPath => manifestPath;
        internal bool Changed { get; }

        internal bool TryRollback(out string error)
        {
            error = string.Empty;
            if (!Changed || rolledBack)
                return true;

            if (!PackageManifestGitDependencyStore.TryCompareAndSwapBytes(
                    manifestPath,
                    writtenBytes,
                    originalBytes,
                    out _,
                    out error))
            {
                return false;
            }

            rolledBack = true;
            return true;
        }

        private static byte[] Clone(byte[] value)
        {
            return value == null || value.Length == 0
                ? Array.Empty<byte>()
                : (byte[])value.Clone();
        }
    }

    /// <summary>
    /// Bounded, strict-JSON access to direct Git dependencies in the project
    /// manifest. This class deliberately knows nothing about packages-lock.json:
    /// lockfile resolution remains exclusively owned by Unity Package Manager.
    /// </summary>
    internal static class PackageManifestGitDependencyStore
    {
        internal const int MaximumManifestByteCount = 2 * 1024 * 1024;
        internal const int MaximumDependencyCount = 2048;
        internal const int MaximumGitSpecLength = 6144;
        internal const int MaximumRevisionLength = 1024;

        private const int MaximumJsonDepth = 32;
        private const int MaximumRestoreAttempts = 16;
        private const string DependenciesPropertyName = "dependencies";
        private static readonly byte[] Utf8Bom = { 0xEF, 0xBB, 0xBF };
        private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);
        private static readonly object MutationGate = new object();

        // Tests use this to make a manifest edit after the optimistic read but
        // immediately before the atomic replace. Production code never assigns
        // this hook.
        internal static Action<string> BeforeInitialAtomicReplaceForTests { get; set; }

        internal static string ManifestPath =>
            Path.Combine(GitUtility.ProjectRoot, "Packages", "manifest.json");

        internal static bool TryGetProjectDependency(
            string packageName,
            out PackageManifestGitDependency dependency,
            out string error)
        {
            return TryGetDependencyAtPath(
                ManifestPath,
                packageName,
                out dependency,
                out error);
        }

        internal static bool TryGetDependencyAtPath(
            string manifestPath,
            string packageName,
            out PackageManifestGitDependency dependency,
            out string error)
        {
            dependency = null;
            error = ValidatePackageName(packageName);
            if (!string.IsNullOrEmpty(error))
                return false;

            if (!TryReadDocument(manifestPath, out ManifestDocument document, out error))
                return false;

            if (!TryGetDependencies(document.Root, false, out JObject dependencies, out error))
                return false;

            JProperty property = dependencies.Property(packageName, StringComparison.Ordinal);
            if (property == null)
            {
                error = $"{packageName} is not declared as a direct project dependency.";
                return false;
            }

            if (property.Value.Type != JTokenType.String)
            {
                error = $"The direct dependency entry for {packageName} is not a string.";
                return false;
            }

            string spec = property.Value.Value<string>() ?? string.Empty;
            if (!TryParseGitSpec(
                    spec,
                    out string repositoryUrl,
                    out string revision,
                    out string packageSubfolder,
                    out error))
            {
                error = $"The direct dependency entry for {packageName} is not a supported Git package: {error}";
                return false;
            }

            dependency = new PackageManifestGitDependency(
                packageName,
                spec,
                repositoryUrl,
                revision,
                packageSubfolder);
            return true;
        }

        internal static bool TryAddDependency(
            string packageName,
            string spec,
            out PackageManifestDependencyMutation mutation,
            out string error)
        {
            return TryAddDependencyAtPath(
                ManifestPath,
                packageName,
                spec,
                out mutation,
                out error);
        }

        internal static bool TryAddDependencyAtPath(
            string manifestPath,
            string packageName,
            string spec,
            out PackageManifestDependencyMutation mutation,
            out string error)
        {
            mutation = null;
            error = ValidatePackageName(packageName);
            if (!string.IsNullOrEmpty(error))
                return false;

            if (!TryParseGitSpec(spec, out _, out _, out error))
            {
                error = "The read-only package specification is invalid: " + error;
                return false;
            }

            if (!TryReadDocument(manifestPath, out ManifestDocument document, out error))
                return false;

            if (!TryGetDependencies(document.Root, true, out JObject dependencies, out error))
                return false;

            JProperty existing = dependencies.Property(packageName, StringComparison.Ordinal);
            if (existing != null)
            {
                if (existing.Value.Type == JTokenType.String &&
                    string.Equals(existing.Value.Value<string>(), spec, StringComparison.Ordinal))
                {
                    mutation = new PackageManifestDependencyMutation(
                        document.Path,
                        document.Bytes,
                        document.Bytes,
                        false);
                    return true;
                }

                error = $"The project manifest already declares a different dependency for {packageName}.";
                return false;
            }

            if (dependencies.Count >= MaximumDependencyCount)
            {
                error = $"The project manifest exceeds the {MaximumDependencyCount} dependency safety limit.";
                return false;
            }

            dependencies.Add(packageName, spec);
            if (!TrySerialize(document, out byte[] replacementBytes, out error))
                return false;

            if (!TryCompareAndSwapBytes(
                    document.Path,
                    document.Bytes,
                    replacementBytes,
                    out _,
                    out error))
            {
                return false;
            }

            mutation = new PackageManifestDependencyMutation(
                document.Path,
                document.Bytes,
                replacementBytes,
                true);
            return true;
        }

        internal static bool TryRemoveDependency(
            string packageName,
            string expectedSpec,
            out PackageManifestDependencyMutation mutation,
            out string error)
        {
            return TryRemoveDependencyAtPath(
                ManifestPath,
                packageName,
                expectedSpec,
                out mutation,
                out error);
        }

        internal static bool TryRemoveDependencyAtPath(
            string manifestPath,
            string packageName,
            string expectedSpec,
            out PackageManifestDependencyMutation mutation,
            out string error)
        {
            mutation = null;
            error = ValidatePackageName(packageName);
            if (!string.IsNullOrEmpty(error))
                return false;

            if (!TryParseGitSpec(expectedSpec, out _, out _, out error))
            {
                error = "The expected read-only package specification is invalid: " + error;
                return false;
            }

            if (!TryReadDocument(manifestPath, out ManifestDocument document, out error))
                return false;

            if (!TryGetDependencies(document.Root, false, out JObject dependencies, out error))
                return false;

            JProperty existing = dependencies.Property(packageName, StringComparison.Ordinal);
            if (existing == null)
            {
                error = $"The project manifest no longer declares {packageName}.";
                return false;
            }

            if (existing.Value.Type != JTokenType.String ||
                !string.Equals(existing.Value.Value<string>(), expectedSpec, StringComparison.Ordinal))
            {
                error = $"The direct dependency for {packageName} changed before it could be removed.";
                return false;
            }

            existing.Remove();
            if (!TrySerialize(document, out byte[] replacementBytes, out error))
                return false;

            if (!TryCompareAndSwapBytes(
                    document.Path,
                    document.Bytes,
                    replacementBytes,
                    out _,
                    out error))
            {
                return false;
            }

            mutation = new PackageManifestDependencyMutation(
                document.Path,
                document.Bytes,
                replacementBytes,
                true);
            return true;
        }

        internal static bool TryBuildGitSpec(
            string repositoryUrl,
            string revision,
            out string spec,
            out string error)
        {
            spec = string.Empty;
            error = string.Empty;
            string url = repositoryUrl?.Trim() ?? string.Empty;
            string requestedRevision = revision?.Trim() ?? string.Empty;

            if (!GitUtility.IsValidRepositoryUrl(url))
            {
                error =
                    "Use a secure HTTPS, SSH, or explicit local repository URL without embedded credentials.";
                return false;
            }

            if (!IsValidGitRevision(requestedRevision))
            {
                error = "The Git revision is invalid.";
                return false;
            }

            spec = string.IsNullOrEmpty(requestedRevision)
                ? url
                : url + "#" + requestedRevision;
            if (spec.Length > MaximumGitSpecLength)
            {
                spec = string.Empty;
                error = "The Git package specification exceeds the safety limit.";
                return false;
            }

            return true;
        }

        internal static bool TryParseGitSpec(
            string spec,
            out string repositoryUrl,
            out string revision,
            out string error)
        {
            return TryParseGitSpec(
                spec,
                out repositoryUrl,
                out revision,
                out _,
                out error);
        }

        internal static bool TryParseGitSpec(
            string spec,
            out string repositoryUrl,
            out string revision,
            out string packageSubfolder,
            out string error)
        {
            repositoryUrl = string.Empty;
            revision = string.Empty;
            packageSubfolder = string.Empty;
            error = string.Empty;
            if (spec == null ||
                spec.Length == 0 ||
                spec.Length > MaximumGitSpecLength ||
                !string.Equals(spec, spec.Trim(), StringComparison.Ordinal))
            {
                error = "The Git package specification is empty, padded, or too long.";
                return false;
            }

            int fragmentIndex = spec.IndexOf('#');
            if (fragmentIndex >= 0 && spec.IndexOf('#', fragmentIndex + 1) >= 0)
            {
                error = "The Git package specification contains more than one revision fragment.";
                return false;
            }

            string repositoryAndPath = fragmentIndex < 0
                ? spec
                : spec.Substring(0, fragmentIndex);
            revision = fragmentIndex < 0
                ? string.Empty
                : spec.Substring(fragmentIndex + 1);

            if (fragmentIndex >= 0 && revision.Length == 0)
            {
                error = "The revision fragment is empty.";
                return false;
            }

            int queryIndex = repositoryAndPath.IndexOf('?');
            if (queryIndex >= 0)
            {
                if (repositoryAndPath.IndexOf('?', queryIndex + 1) >= 0)
                {
                    revision = string.Empty;
                    error = "The Git package specification contains more than one query delimiter.";
                    return false;
                }

                string query = repositoryAndPath.Substring(queryIndex + 1);
                const string pathPrefix = "path=";
                if (!query.StartsWith(pathPrefix, StringComparison.Ordinal) ||
                    query.IndexOf('&') >= 0)
                {
                    revision = string.Empty;
                    error = "Only Unity's single path query parameter is supported for Git packages.";
                    return false;
                }

                string encodedSubfolder = query.Substring(pathPrefix.Length);
                if (!TryNormalizePackageSubfolder(
                        encodedSubfolder,
                        out packageSubfolder,
                        out error))
                {
                    revision = string.Empty;
                    return false;
                }

                repositoryUrl = repositoryAndPath.Substring(0, queryIndex);
            }
            else
            {
                repositoryUrl = repositoryAndPath;
            }

            if (!GitUtility.IsValidRepositoryUrl(repositoryUrl))
            {
                repositoryUrl = string.Empty;
                revision = string.Empty;
                packageSubfolder = string.Empty;
                error = "The repository URL is unsupported or uses an unsafe transport.";
                return false;
            }

            if (fragmentIndex >= 0 && !IsValidGitRevision(revision))
            {
                repositoryUrl = string.Empty;
                revision = string.Empty;
                packageSubfolder = string.Empty;
                error = "The revision fragment is invalid.";
                return false;
            }

            return true;
        }

        private static bool TryNormalizePackageSubfolder(
            string encodedSubfolder,
            out string packageSubfolder,
            out string error)
        {
            packageSubfolder = string.Empty;
            error = string.Empty;
            if (string.IsNullOrEmpty(encodedSubfolder) ||
                encodedSubfolder.Length > MaximumRevisionLength ||
                !encodedSubfolder.StartsWith("/", StringComparison.Ordinal) ||
                encodedSubfolder.IndexOfAny(new[] { '\0', '\r', '\n', '\\', '?', '#', '&' }) >= 0)
            {
                error = "The Unity package subfolder path is empty, malformed, or too long.";
                return false;
            }

            for (int index = 0; index < encodedSubfolder.Length; index++)
            {
                if (encodedSubfolder[index] != '%')
                    continue;
                if (index + 2 >= encodedSubfolder.Length ||
                    !Uri.IsHexDigit(encodedSubfolder[index + 1]) ||
                    !Uri.IsHexDigit(encodedSubfolder[index + 2]))
                {
                    error = "The Unity package subfolder path contains invalid percent encoding.";
                    return false;
                }

                index += 2;
            }

            string decoded;
            try
            {
                decoded = Uri.UnescapeDataString(encodedSubfolder);
            }
            catch (Exception exception)
            {
                error = "The Unity package subfolder path could not be decoded: " +
                        exception.Message;
                return false;
            }

            if (!decoded.StartsWith("/", StringComparison.Ordinal) ||
                decoded.IndexOfAny(new[] { '\0', '\r', '\n', '\\', '?', '#' }) >= 0)
            {
                error = "The Unity package subfolder path is malformed.";
                return false;
            }

            foreach (string segment in decoded.Split('/'))
            {
                if (string.Equals(segment, ".", StringComparison.Ordinal) ||
                    string.Equals(segment, "..", StringComparison.Ordinal))
                {
                    error = "The Unity package subfolder path cannot contain dot segments.";
                    return false;
                }
            }

            // ?path=/ is semantically the repository root. Normalize it so
            // conversion eligibility follows the package location rather than
            // the presence of redundant URL syntax.
            packageSubfolder = string.Equals(decoded, "/", StringComparison.Ordinal)
                ? string.Empty
                : decoded;
            return true;
        }

        internal static bool TryCompareAndSwapBytes(
            string manifestPath,
            byte[] expectedBytes,
            byte[] replacementBytes,
            out bool alreadyReplaced,
            out string error)
        {
            alreadyReplaced = false;
            error = string.Empty;
            if (!TryResolveManifestPath(manifestPath, out string fullPath, out error))
                return false;

            expectedBytes = expectedBytes ?? Array.Empty<byte>();
            replacementBytes = replacementBytes ?? Array.Empty<byte>();
            if (replacementBytes.Length > MaximumManifestByteCount)
            {
                error = "The replacement manifest exceeds the 2 MiB safety limit.";
                return false;
            }

            if (!TryCreateSiblingOperationPath(
                    fullPath,
                    "replacement",
                    out string temporaryPath,
                    out error) ||
                !TryCreateSiblingOperationPath(
                    fullPath,
                    "displaced",
                    out string displacedPath,
                    out error))
            {
                return false;
            }

            bool initialReplaceCompleted = false;
            bool displacedFileCanBeDeleted = false;
            try
            {
                lock (MutationGate)
                {
                    if (!TryReadRawBytes(fullPath, out byte[] currentBytes, out error))
                        return false;

                    if (BytesEqual(currentBytes, replacementBytes))
                    {
                        alreadyReplaced = true;
                        return true;
                    }

                    if (!BytesEqual(currentBytes, expectedBytes))
                    {
                        error =
                            "Packages/manifest.json changed after it was inspected. No project dependency was overwritten.";
                        return false;
                    }

                    using (var stream = new FileStream(
                               temporaryPath,
                               FileMode.CreateNew,
                               FileAccess.Write,
                               FileShare.None,
                               4096,
                               FileOptions.WriteThrough))
                    {
                        stream.Write(replacementBytes, 0, replacementBytes.Length);
                        stream.Flush(true);
                    }

                    // File.Replace atomically captures the exact manifest it
                    // displaced. That closes the read-to-replace race: even if
                    // another process writes at this boundary, its bytes land
                    // in displacedPath and are compared before success.
                    BeforeInitialAtomicReplaceForTests?.Invoke(fullPath);
                    File.Replace(temporaryPath, fullPath, displacedPath, true);
                    initialReplaceCompleted = true;

                    if (!TryReadRawBytes(
                            displacedPath,
                            out byte[] displacedBytes,
                            out string displacedReadError))
                    {
                        error =
                            "Packages/manifest.json was replaced, but the exact displaced manifest " +
                            "could not be verified. Its recovery bytes were preserved at " +
                            displacedPath + ". Manual recovery is required. " +
                            displacedReadError;
                        return false;
                    }

                    if (BytesEqual(displacedBytes, expectedBytes))
                    {
                        displacedFileCanBeDeleted = true;
                        return true;
                    }

                    // The swap displaced somebody else's edit. Put those exact
                    // bytes back, while each restore atomically captures the
                    // file it displaces. If another writer races restoration,
                    // that newer file becomes the next restore candidate rather
                    // than being overwritten or deleted.
                    return TryRestoreDisplacedManifest(
                        fullPath,
                        displacedPath,
                        displacedBytes,
                        replacementBytes,
                        out error);
                }
            }
            catch (Exception exception)
            {
                error = SanitizeFileError(
                    "Packages/manifest.json could not be updated atomically: ",
                    exception);

                // A backup appearing despite an exception may contain the exact
                // file displaced by a partially reported platform operation.
                // Never delete it; surface its location for manual recovery.
                if (File.Exists(displacedPath))
                {
                    error +=
                        " The exact displaced manifest may be preserved at " +
                        displacedPath + ".";
                }

                return false;
            }
            finally
            {
                // The replacement file contains only bytes created by this
                // operation. File.Replace consumes it on success.
                TryDeleteKnownFile(temporaryPath, replacementBytes);

                // Delete the displaced copy only after it was proven to be the
                // caller's expected manifest. On every uncertain/failure path
                // it is retained as recovery data.
                if (initialReplaceCompleted && displacedFileCanBeDeleted)
                    TryDeleteKnownFile(displacedPath, expectedBytes);
            }
        }

        private static bool TryRestoreDisplacedManifest(
            string fullPath,
            string initialCandidatePath,
            byte[] initialCandidateBytes,
            byte[] bytesInstalledByInitialSwap,
            out string error)
        {
            string candidatePath = initialCandidatePath;
            byte[] candidateBytes = initialCandidateBytes;
            byte[] bytesExpectedAtDestination = bytesInstalledByInitialSwap;

            for (int attempt = 0; attempt < MaximumRestoreAttempts; attempt++)
            {
                if (!TryCreateSiblingOperationPath(
                        fullPath,
                        "recovery",
                        out string capturedPath,
                        out string pathError))
                {
                    error = BuildUnsafeRecoveryError(candidatePath, pathError);
                    return false;
                }

                try
                {
                    File.Replace(candidatePath, fullPath, capturedPath, true);
                }
                catch (Exception exception)
                {
                    string preservedPath = File.Exists(capturedPath)
                        ? capturedPath
                        : candidatePath;
                    error = BuildUnsafeRecoveryError(
                        preservedPath,
                        SanitizeFileError(
                            "The displaced manifest could not be restored atomically: ",
                            exception));
                    return false;
                }

                if (!TryReadRawBytes(
                        capturedPath,
                        out byte[] capturedBytes,
                        out string capturedReadError))
                {
                    error = BuildUnsafeRecoveryError(capturedPath, capturedReadError);
                    return false;
                }

                if (BytesEqual(capturedBytes, bytesExpectedAtDestination))
                {
                    // Restoration displaced exactly the bytes installed by the
                    // preceding operation, so no later writer was overwritten.
                    TryDeleteKnownFile(capturedPath, bytesExpectedAtDestination);
                    error =
                        "Packages/manifest.json changed after it was inspected. " +
                        "The external edit was restored and no project dependency was overwritten.";
                    return false;
                }

                // A newer writer changed the destination before restoration.
                // Its exact bytes are in capturedPath. Restore those next and
                // prove that the destination still contains candidateBytes.
                bytesExpectedAtDestination = candidateBytes;
                candidatePath = capturedPath;
                candidateBytes = capturedBytes;
            }

            error = BuildUnsafeRecoveryError(
                candidatePath,
                "Packages/manifest.json kept changing during atomic recovery.");
            return false;
        }

        private static string BuildUnsafeRecoveryError(
            string recoveryPath,
            string detail)
        {
            string prefix = string.IsNullOrWhiteSpace(detail)
                ? string.Empty
                : detail.TrimEnd() + " ";
            return prefix +
                   "Automatic recovery could not be proven safe. The most recently " +
                   "displaced manifest bytes were preserved at " + recoveryPath +
                   ". Manual recovery is required; the file was not deleted.";
        }

        private static bool TryCreateSiblingOperationPath(
            string fullPath,
            string role,
            out string operationPath,
            out string error)
        {
            operationPath = string.Empty;
            error = string.Empty;
            try
            {
                string directory = Path.GetDirectoryName(fullPath);
                string fileName = Path.GetFileName(fullPath);
                if (string.IsNullOrEmpty(directory) ||
                    string.IsNullOrEmpty(fileName) ||
                    string.IsNullOrEmpty(role) ||
                    role.Any(character =>
                        !char.IsLetterOrDigit(character) && character != '-'))
                {
                    error = "A safe sibling path for the manifest operation could not be created.";
                    return false;
                }

                string fullDirectory = Path.GetFullPath(directory);
                for (int attempt = 0; attempt < 16; attempt++)
                {
                    string leafName =
                        fileName + "." + role + "." +
                        Guid.NewGuid().ToString("N") + ".tmp";
                    string candidate = Path.GetFullPath(
                        Path.Combine(fullDirectory, leafName));
                    if (!string.Equals(
                            Path.GetDirectoryName(candidate),
                            fullDirectory,
                            PathComparison) ||
                        !string.Equals(
                            Path.GetFileName(candidate),
                            leafName,
                            StringComparison.Ordinal) ||
                        File.Exists(candidate) ||
                        Directory.Exists(candidate))
                    {
                        continue;
                    }

                    operationPath = candidate;
                    return true;
                }

                error = "A unique sibling path for the manifest operation could not be created.";
                return false;
            }
            catch (Exception exception)
            {
                error = SanitizeFileError(
                    "A safe sibling path for the manifest operation could not be created: ",
                    exception);
                return false;
            }
        }

        private static StringComparison PathComparison =>
            Path.DirectorySeparatorChar == '\\'
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;

        private static bool TryReadDocument(
            string manifestPath,
            out ManifestDocument document,
            out string error)
        {
            document = null;
            if (!TryResolveManifestPath(manifestPath, out string fullPath, out error) ||
                !TryReadRawBytes(fullPath, out byte[] bytes, out error))
            {
                return false;
            }

            bool hasBom = HasUtf8Bom(bytes);
            int textOffset = hasBom ? Utf8Bom.Length : 0;
            string json;
            try
            {
                json = StrictUtf8.GetString(bytes, textOffset, bytes.Length - textOffset);
            }
            catch (Exception exception)
            {
                error = SanitizeFileError(
                    "Packages/manifest.json must use valid UTF-8: ",
                    exception);
                return false;
            }

            if (string.IsNullOrWhiteSpace(json))
            {
                error = "Packages/manifest.json is empty.";
                return false;
            }

            JObject root;
            try
            {
                using (var stringReader = new StringReader(json))
                using (var jsonReader = new JsonTextReader(stringReader)
                       {
                           DateParseHandling = DateParseHandling.None,
                           MaxDepth = MaximumJsonDepth
                       })
                {
                    root = JObject.Load(
                        jsonReader,
                        new JsonLoadSettings
                        {
                            CommentHandling = CommentHandling.Ignore,
                            DuplicatePropertyNameHandling =
                                DuplicatePropertyNameHandling.Error,
                            LineInfoHandling = LineInfoHandling.Ignore
                        });

                    while (jsonReader.Read())
                    {
                        if (jsonReader.TokenType != JsonToken.Comment)
                        {
                            error =
                                "Packages/manifest.json contains content after its root object.";
                            return false;
                        }
                    }
                }
            }
            catch (Exception exception)
            {
                error = SanitizeFileError(
                    "Packages/manifest.json could not be parsed safely: ",
                    exception);
                return false;
            }

            if (root == null)
            {
                error = "Packages/manifest.json root must be a JSON object.";
                return false;
            }

            DetectTextStyle(
                json,
                out string newline,
                out bool hasFinalNewline,
                out bool indented,
                out char indentCharacter,
                out int indentation);
            document = new ManifestDocument(
                fullPath,
                bytes,
                root,
                hasBom,
                newline,
                hasFinalNewline,
                indented,
                indentCharacter,
                indentation);
            return true;
        }

        private static bool TryGetDependencies(
            JObject root,
            bool createIfMissing,
            out JObject dependencies,
            out string error)
        {
            dependencies = null;
            error = string.Empty;
            JProperty property = root.Property(
                DependenciesPropertyName,
                StringComparison.Ordinal);
            if (property == null)
            {
                if (!createIfMissing)
                {
                    error = "Packages/manifest.json has no dependencies object.";
                    return false;
                }

                dependencies = new JObject();
                root.AddFirst(new JProperty(DependenciesPropertyName, dependencies));
                return true;
            }

            dependencies = property.Value as JObject;
            if (dependencies == null)
            {
                error = "Packages/manifest.json dependencies must be a JSON object.";
                return false;
            }

            if (dependencies.Count > MaximumDependencyCount)
            {
                error = $"The project manifest exceeds the {MaximumDependencyCount} dependency safety limit.";
                return false;
            }

            if (dependencies.Properties().Any(item => item.Value.Type != JTokenType.String))
            {
                error = "Every Packages/manifest.json dependency value must be a string.";
                return false;
            }

            return true;
        }

        private static bool TrySerialize(
            ManifestDocument document,
            out byte[] bytes,
            out string error)
        {
            bytes = Array.Empty<byte>();
            error = string.Empty;
            try
            {
                var builder = new StringBuilder(document.Bytes.Length + 256);
                using (var writer = new StringWriter(builder))
                {
                    writer.NewLine = document.Newline;
                    using (var jsonWriter = new JsonTextWriter(writer)
                           {
                               Formatting = document.Indented
                                   ? Formatting.Indented
                                   : Formatting.None,
                               IndentChar = document.IndentCharacter,
                               Indentation = document.Indentation,
                               StringEscapeHandling = StringEscapeHandling.Default
                           })
                    {
                        document.Root.WriteTo(jsonWriter);
                    }
                }

                if (document.HasFinalNewline)
                    builder.Append(document.Newline);

                byte[] content = StrictUtf8.GetBytes(builder.ToString());
                if (content.Length + (document.HasBom ? Utf8Bom.Length : 0) >
                    MaximumManifestByteCount)
                {
                    error = "The updated project manifest exceeds the 2 MiB safety limit.";
                    return false;
                }

                if (!document.HasBom)
                {
                    bytes = content;
                    return true;
                }

                bytes = new byte[Utf8Bom.Length + content.Length];
                Buffer.BlockCopy(Utf8Bom, 0, bytes, 0, Utf8Bom.Length);
                Buffer.BlockCopy(content, 0, bytes, Utf8Bom.Length, content.Length);
                return true;
            }
            catch (Exception exception)
            {
                error = SanitizeFileError(
                    "Packages/manifest.json could not be serialized safely: ",
                    exception);
                return false;
            }
        }

        private static bool TryResolveManifestPath(
            string manifestPath,
            out string fullPath,
            out string error)
        {
            fullPath = string.Empty;
            error = string.Empty;
            if (string.IsNullOrWhiteSpace(manifestPath))
            {
                error = "The project manifest path is missing.";
                return false;
            }

            try
            {
                fullPath = Path.GetFullPath(manifestPath);
                var fileInfo = new FileInfo(fullPath);
                if (!fileInfo.Exists)
                {
                    error = "Packages/manifest.json does not exist.";
                    return false;
                }

                FileAttributes attributes = fileInfo.Attributes;
                if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                {
                    error =
                        "Packages/manifest.json must be a regular file, not a symbolic link, junction, or other reparse point.";
                    return false;
                }

                if (fileInfo.Length > MaximumManifestByteCount)
                {
                    error = "Packages/manifest.json exceeds the 2 MiB safety limit.";
                    return false;
                }

                return true;
            }
            catch (Exception exception)
            {
                error = SanitizeFileError(
                    "Packages/manifest.json could not be inspected safely: ",
                    exception);
                return false;
            }
        }

        private static bool TryReadRawBytes(
            string fullPath,
            out byte[] bytes,
            out string error)
        {
            bytes = Array.Empty<byte>();
            error = string.Empty;
            try
            {
                var fileInfo = new FileInfo(fullPath);
                if (!fileInfo.Exists || fileInfo.Length > MaximumManifestByteCount)
                {
                    error = !fileInfo.Exists
                        ? "Packages/manifest.json does not exist."
                        : "Packages/manifest.json exceeds the 2 MiB safety limit.";
                    return false;
                }

                using (var stream = new FileStream(
                           fullPath,
                           FileMode.Open,
                           FileAccess.Read,
                           FileShare.Read))
                {
                    if (stream.Length > MaximumManifestByteCount)
                    {
                        error = "Packages/manifest.json exceeds the 2 MiB safety limit.";
                        return false;
                    }

                    bytes = new byte[(int)stream.Length];
                    int offset = 0;
                    while (offset < bytes.Length)
                    {
                        int read = stream.Read(bytes, offset, bytes.Length - offset);
                        if (read <= 0)
                        {
                            error = "Packages/manifest.json changed while it was being read.";
                            bytes = Array.Empty<byte>();
                            return false;
                        }

                        offset += read;
                    }

                    if (stream.ReadByte() >= 0)
                    {
                        error = "Packages/manifest.json changed while it was being read.";
                        bytes = Array.Empty<byte>();
                        return false;
                    }
                }

                return true;
            }
            catch (Exception exception)
            {
                error = SanitizeFileError(
                    "Packages/manifest.json could not be read safely: ",
                    exception);
                return false;
            }
        }

        private static bool IsValidGitRevision(string revision)
        {
            if (revision == null || revision.Length > MaximumRevisionLength)
                return false;
            if (revision.Length == 0)
                return true;
            if (!string.Equals(revision, revision.Trim(), StringComparison.Ordinal) ||
                revision.StartsWith("-", StringComparison.Ordinal) ||
                revision.Contains("..") ||
                revision.Contains("@{") ||
                revision.Contains("//") ||
                revision.EndsWith(".", StringComparison.Ordinal) ||
                revision.EndsWith("/", StringComparison.Ordinal))
            {
                return false;
            }

            const string forbidden = "~^:?*[\\#";
            foreach (char character in revision)
            {
                if (character <= 0x20 ||
                    character == 0x7F ||
                    forbidden.IndexOf(character) >= 0)
                {
                    return false;
                }
            }

            foreach (string segment in revision.Split('/'))
            {
                if (segment.Length == 0 ||
                    segment.StartsWith(".", StringComparison.Ordinal) ||
                    segment.EndsWith(".lock", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }

            return true;
        }

        private static string ValidatePackageName(string packageName)
        {
            return GitUtility.IsValidUpmPackageName(packageName)
                ? string.Empty
                : "A valid reverse-domain UPM package name is required.";
        }

        private static void DetectTextStyle(
            string text,
            out string newline,
            out bool hasFinalNewline,
            out bool indented,
            out char indentCharacter,
            out int indentation)
        {
            int lineFeed = text.IndexOf('\n');
            int carriageReturn = text.IndexOf('\r');
            if (lineFeed >= 0 && (carriageReturn < 0 || lineFeed < carriageReturn))
                newline = lineFeed > 0 && text[lineFeed - 1] == '\r' ? "\r\n" : "\n";
            else if (carriageReturn >= 0)
                newline = carriageReturn + 1 < text.Length && text[carriageReturn + 1] == '\n'
                    ? "\r\n"
                    : "\r";
            else
                newline = Environment.NewLine;

            hasFinalNewline = text.EndsWith("\n", StringComparison.Ordinal) ||
                              text.EndsWith("\r", StringComparison.Ordinal);
            indented = lineFeed >= 0 || carriageReturn >= 0;
            indentCharacter = ' ';
            indentation = 2;
            if (!indented)
                return;

            string[] lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            foreach (string line in lines)
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                int count = 0;
                while (count < line.Length && (line[count] == ' ' || line[count] == '\t'))
                    count++;
                if (count == 0)
                    continue;

                indentCharacter = line[0] == '\t' ? '\t' : ' ';
                indentation = indentCharacter == '\t' ? 1 : Math.Max(1, count);
                return;
            }
        }

        private static bool HasUtf8Bom(byte[] bytes)
        {
            return bytes != null &&
                   bytes.Length >= Utf8Bom.Length &&
                   bytes[0] == Utf8Bom[0] &&
                   bytes[1] == Utf8Bom[1] &&
                   bytes[2] == Utf8Bom[2];
        }

        private static bool BytesEqual(byte[] first, byte[] second)
        {
            if (ReferenceEquals(first, second))
                return true;
            if (first == null || second == null || first.Length != second.Length)
                return false;
            for (int index = 0; index < first.Length; index++)
            {
                if (first[index] != second[index])
                    return false;
            }

            return true;
        }

        private static string SanitizeFileError(string prefix, Exception exception)
        {
            string detail = exception?.Message ?? "Unknown file error.";
            return GitHubUtility.SanitizeUiDiagnostic(prefix + detail);
        }

        private static void TryDeleteKnownFile(string path, byte[] expectedBytes)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                    return;

                // Operation paths are unique siblings, but still verify their
                // contents before cleanup so recovery/external data is never
                // silently deleted after an unexpected platform result.
                if (TryReadRawBytes(path, out byte[] actualBytes, out _) &&
                    BytesEqual(actualBytes, expectedBytes))
                {
                    File.Delete(path);
                }
            }
            catch
            {
                // Cleanup failure must not hide the original compare-and-swap
                // result. Leaving a verified operation file is safer than
                // broadening deletion behavior.
            }
        }

        private sealed class ManifestDocument
        {
            internal ManifestDocument(
                string path,
                byte[] bytes,
                JObject root,
                bool hasBom,
                string newline,
                bool hasFinalNewline,
                bool indented,
                char indentCharacter,
                int indentation)
            {
                Path = path;
                Bytes = bytes;
                Root = root;
                HasBom = hasBom;
                Newline = newline;
                HasFinalNewline = hasFinalNewline;
                Indented = indented;
                IndentCharacter = indentCharacter;
                Indentation = indentation;
            }

            internal string Path { get; }
            internal byte[] Bytes { get; }
            internal JObject Root { get; }
            internal bool HasBom { get; }
            internal string Newline { get; }
            internal bool HasFinalNewline { get; }
            internal bool Indented { get; }
            internal char IndentCharacter { get; }
            internal int Indentation { get; }
        }
    }
}
