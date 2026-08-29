using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;

namespace MartinCalander.GitSubmoduleManager.Installer
{
    internal sealed class InstallerManifestPlan
    {
        internal string Path;
        internal byte[] OriginalBytes;
        internal byte[] WrittenBytes;
        internal string OriginalSha256;
        internal string WrittenSha256;
        internal bool AddsRegistry;
        internal bool AddsScope;
        internal bool AddsDependency;

        internal bool ChangesManifest =>
            !InstallerManifest.BytesEqual(OriginalBytes, WrittenBytes);

        internal string Summary
        {
            get
            {
                var changes = new List<string>();
                if (AddsRegistry)
                    changes.Add("add the OpenUPM scoped registry");
                if (AddsScope)
                    changes.Add("add the exact package scope to OpenUPM");
                if (AddsDependency)
                    changes.Add("add the exact package dependency");
                return changes.Count == 0
                    ? "No manifest changes are needed."
                    : "The installer will " + string.Join(", ", changes.ToArray()) + ".";
            }
        }
    }

    internal static class InstallerManifest
    {
        internal const int MaximumManifestByteCount = 2 * 1024 * 1024;
        internal const string PackageName = "com.martincalander.gitsubmodulemanager";
        internal const string RegistryName = "OpenUPM";
        internal const string RegistryUrl = "https://package.openupm.com";

        private const int MaximumRestoreAttempts = 16;
        private static readonly byte[] Utf8Bom = { 0xEF, 0xBB, 0xBF };
        private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);
        private static readonly object MutationGate = new object();

        internal static string ProjectRoot =>
            Path.GetFullPath(Path.Combine(Application.dataPath, ".."));

        internal static string ManifestPath =>
            Path.Combine(ProjectRoot, "Packages", "manifest.json");

        internal static bool TryCreatePlan(
            string version,
            out InstallerManifestPlan plan,
            out string error)
        {
            plan = null;
            error = string.Empty;
            if (!IsExactVersion(version))
            {
                error = "The installer contains an invalid target package version.";
                return false;
            }

            if (!TryReadManifest(
                    ManifestPath,
                    out byte[] originalBytes,
                    out string text,
                    out bool hasBom,
                    out error))
            {
                return false;
            }

            try
            {
                bool addsRegistry = false;
                bool addsScope = false;
                bool addsDependency = false;

                StrictJsonValue root = StrictJsonParser.ParseRootObject(text);
                if (!TryPlanRegistry(
                        text,
                        root,
                        out string registryText,
                        out addsRegistry,
                        out addsScope,
                        out error))
                {
                    return false;
                }

                StrictJsonValue registryRoot = StrictJsonParser.ParseRootObject(registryText);
                if (!TryPlanDependency(
                        registryText,
                        registryRoot,
                        version,
                        out string finalText,
                        out addsDependency,
                        out error))
                {
                    return false;
                }

                StrictJsonParser.ParseRootObject(finalText);
                byte[] writtenBytes = Encode(finalText, hasBom);
                if (writtenBytes.Length > MaximumManifestByteCount)
                {
                    error = "The updated project manifest exceeds the 2 MiB safety limit.";
                    return false;
                }

                plan = new InstallerManifestPlan
                {
                    Path = Path.GetFullPath(ManifestPath),
                    OriginalBytes = originalBytes,
                    WrittenBytes = writtenBytes,
                    OriginalSha256 = Sha256(originalBytes),
                    WrittenSha256 = Sha256(writtenBytes),
                    AddsRegistry = addsRegistry,
                    AddsScope = addsScope,
                    AddsDependency = addsDependency
                };
                return true;
            }
            catch (Exception exception)
            {
                error = "Packages/manifest.json could not be parsed safely: " +
                        exception.Message;
                return false;
            }
        }

        internal static bool TryValidateInstalledManifest(string version, out string error)
        {
            if (!TryCreatePlan(version, out InstallerManifestPlan plan, out error))
                return false;
            if (plan.ChangesManifest)
            {
                error =
                    "Packages/manifest.json no longer contains the exact dependency and OpenUPM scope authorized by the installer.";
                return false;
            }

            return true;
        }

        private static bool TryPlanRegistry(
            string text,
            StrictJsonValue root,
            out string result,
            out bool addsRegistry,
            out bool addsScope,
            out string error)
        {
            result = text;
            addsRegistry = false;
            addsScope = false;
            error = string.Empty;
            StrictJsonProperty registriesProperty = root.FindProperty("scopedRegistries");
            if (registriesProperty == null)
            {
                result = InsertObjectProperty(
                    text,
                    root,
                    "scopedRegistries",
                    "[{\"name\": \"OpenUPM\", \"url\": \"https://package.openupm.com\", \"scopes\": [\"" +
                    PackageName + "\"]}]");
                addsRegistry = true;
                addsScope = true;
                return true;
            }

            StrictJsonValue registries = registriesProperty.Value;
            if (registries.Kind != StrictJsonKind.Array)
            {
                error = "The scopedRegistries manifest property must be an array.";
                return false;
            }

            StrictJsonValue openUpmRegistry = null;
            foreach (StrictJsonValue registry in registries.Items)
            {
                if (registry.Kind != StrictJsonKind.Object)
                {
                    error = "Every scoped registry entry must be an object.";
                    return false;
                }

                StrictJsonProperty nameProperty = registry.FindProperty("name");
                StrictJsonProperty urlProperty = registry.FindProperty("url");
                StrictJsonProperty scopesProperty = registry.FindProperty("scopes");
                if (!TryRequireString(nameProperty, "name", out string name, out error) ||
                    !TryRequireString(urlProperty, "url", out string url, out error) ||
                    scopesProperty == null || scopesProperty.Value.Kind != StrictJsonKind.Array)
                {
                    if (string.IsNullOrEmpty(error))
                        error = "Every scoped registry requires a string name, string URL, and scopes array.";
                    return false;
                }

                bool isOpenUpmEndpoint = IsCanonicalOpenUpmUrl(url);
                if (string.Equals(name, RegistryName, StringComparison.OrdinalIgnoreCase) &&
                    !isOpenUpmEndpoint)
                {
                    error = "A registry named OpenUPM points to an untrusted URL. No changes were made.";
                    return false;
                }

                if (isOpenUpmEndpoint)
                {
                    if (openUpmRegistry != null)
                    {
                        error = "Multiple OpenUPM registry entries make package routing ambiguous.";
                        return false;
                    }

                    openUpmRegistry = registry;
                }

                foreach (StrictJsonValue scopeValue in scopesProperty.Value.Items)
                {
                    if (scopeValue.Kind != StrictJsonKind.String ||
                        string.IsNullOrWhiteSpace(scopeValue.StringValue))
                    {
                        error = "Every scoped registry scope must be a non-empty string.";
                        return false;
                    }

                    if (!isOpenUpmEndpoint &&
                        string.Equals(scopeValue.StringValue, PackageName, StringComparison.Ordinal))
                    {
                        error =
                            "Another registry already owns the exact Git Submodule Manager scope. No changes were made.";
                        return false;
                    }
                }
            }

            if (openUpmRegistry == null)
            {
                result = InsertArrayItem(
                    text,
                    registries,
                    "{\"name\": \"OpenUPM\", \"url\": \"https://package.openupm.com\", \"scopes\": [\"" +
                    PackageName + "\"]}");
                addsRegistry = true;
                addsScope = true;
                return true;
            }

            StrictJsonValue scopes = openUpmRegistry.FindProperty("scopes").Value;
            foreach (StrictJsonValue scope in scopes.Items)
            {
                if (string.Equals(scope.StringValue, PackageName, StringComparison.Ordinal))
                    return true;
            }

            result = InsertArrayItem(text, scopes, Quote(PackageName));
            addsScope = true;
            return true;
        }

        private static bool TryPlanDependency(
            string text,
            StrictJsonValue root,
            string version,
            out string result,
            out bool addsDependency,
            out string error)
        {
            result = text;
            addsDependency = false;
            error = string.Empty;
            StrictJsonProperty dependenciesProperty = root.FindProperty("dependencies");
            if (dependenciesProperty == null ||
                dependenciesProperty.Value.Kind != StrictJsonKind.Object)
            {
                error = "Packages/manifest.json must contain a dependencies object.";
                return false;
            }

            StrictJsonValue dependencies = dependenciesProperty.Value;
            StrictJsonProperty target = dependencies.FindProperty(PackageName);
            if (target != null)
            {
                if (target.Value.Kind != StrictJsonKind.String ||
                    !string.Equals(target.Value.StringValue, version, StringComparison.Ordinal))
                {
                    error =
                        "The project already declares Git Submodule Manager with a different source or version. The installer will not overwrite it.";
                    return false;
                }

                return true;
            }

            result = InsertObjectProperty(text, dependencies, PackageName, Quote(version));
            addsDependency = true;
            return true;
        }

        private static bool TryRequireString(
            StrictJsonProperty property,
            string name,
            out string value,
            out string error)
        {
            value = string.Empty;
            error = string.Empty;
            if (property == null || property.Value.Kind != StrictJsonKind.String)
            {
                error = "A scoped registry has a missing or non-string " + name + " property.";
                return false;
            }

            value = property.Value.StringValue;
            return true;
        }

        private static bool IsCanonicalOpenUpmUrl(string value)
        {
            if (!Uri.TryCreate(value, UriKind.Absolute, out Uri uri))
                return false;
            return string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(uri.Host, "package.openupm.com", StringComparison.OrdinalIgnoreCase) &&
                   uri.IsDefaultPort &&
                   string.IsNullOrEmpty(uri.UserInfo) &&
                   (string.Equals(uri.AbsolutePath, "/", StringComparison.Ordinal) ||
                    string.IsNullOrEmpty(uri.AbsolutePath)) &&
                   string.IsNullOrEmpty(uri.Query) &&
                   string.IsNullOrEmpty(uri.Fragment);
        }

        private static string InsertObjectProperty(
            string text,
            StrictJsonValue owner,
            string name,
            string serializedValue)
        {
            string fragment = Quote(name) + ": " + serializedValue;
            if (owner.Properties.Count == 0)
                return InsertIntoEmptyContainer(text, owner.Start + 1, owner.End - 1, fragment);

            StrictJsonProperty last = owner.Properties[owner.Properties.Count - 1];
            bool multiline = ContainsNewline(text, owner.Start, owner.End);
            string separator = multiline
                ? "," + DetectNewline(text) + GetLineIndent(text, last.KeyStart)
                : ", ";
            return text.Insert(last.Value.End, separator + fragment);
        }

        private static string InsertArrayItem(
            string text,
            StrictJsonValue owner,
            string serializedValue)
        {
            if (owner.Items.Count == 0)
                return InsertIntoEmptyContainer(text, owner.Start + 1, owner.End - 1, serializedValue);

            StrictJsonValue last = owner.Items[owner.Items.Count - 1];
            bool multiline = ContainsNewline(text, owner.Start, owner.End);
            string indent = GetLineIndent(text, last.Start);
            string separator = multiline ? "," + DetectNewline(text) + indent : ", ";
            return text.Insert(last.End, separator + serializedValue);
        }

        private static string InsertIntoEmptyContainer(
            string text,
            int innerStart,
            int innerEnd,
            string fragment)
        {
            bool multiline = ContainsNewline(text, innerStart, innerEnd);
            if (!multiline)
                return text.Substring(0, innerStart) + fragment + text.Substring(innerEnd);

            string newline = DetectNewline(text);
            string closeIndent = GetLineIndent(text, innerEnd);
            string childIndent = closeIndent + DetectIndentUnit(text);
            string replacement = newline + childIndent + fragment + newline + closeIndent;
            return text.Substring(0, innerStart) + replacement + text.Substring(innerEnd);
        }

        private static string Quote(string value)
        {
            var builder = new StringBuilder(value.Length + 2);
            builder.Append('"');
            foreach (char character in value)
            {
                switch (character)
                {
                    case '"': builder.Append("\\\""); break;
                    case '\\': builder.Append("\\\\"); break;
                    case '\b': builder.Append("\\b"); break;
                    case '\f': builder.Append("\\f"); break;
                    case '\n': builder.Append("\\n"); break;
                    case '\r': builder.Append("\\r"); break;
                    case '\t': builder.Append("\\t"); break;
                    default:
                        if (character < 0x20)
                            builder.Append("\\u" + ((int)character).ToString("x4"));
                        else
                            builder.Append(character);
                        break;
                }
            }

            builder.Append('"');
            return builder.ToString();
        }

        private static bool ContainsNewline(string text, int start, int end)
        {
            for (int offset = Math.Max(0, start); offset < Math.Min(text.Length, end); offset++)
            {
                if (text[offset] == '\r' || text[offset] == '\n')
                    return true;
            }

            return false;
        }

        private static string DetectNewline(string text)
        {
            int lineFeed = text.IndexOf('\n');
            if (lineFeed > 0 && text[lineFeed - 1] == '\r')
                return "\r\n";
            if (lineFeed >= 0)
                return "\n";
            return text.IndexOf('\r') >= 0 ? "\r" : Environment.NewLine;
        }

        private static string DetectIndentUnit(string text)
        {
            string normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
            foreach (string line in normalized.Split('\n'))
            {
                int count = 0;
                while (count < line.Length && (line[count] == ' ' || line[count] == '\t'))
                    count++;
                if (count == 0 || count == line.Length)
                    continue;
                return line[0] == '\t' ? "\t" : new string(' ', count);
            }

            return "  ";
        }

        private static string GetLineIndent(string text, int position)
        {
            int lineStart = position;
            while (lineStart > 0 && text[lineStart - 1] != '\r' && text[lineStart - 1] != '\n')
                lineStart--;
            int offset = lineStart;
            while (offset < position && (text[offset] == ' ' || text[offset] == '\t'))
                offset++;
            return text.Substring(lineStart, offset - lineStart);
        }

        private static byte[] Encode(string text, bool hasBom)
        {
            byte[] content = StrictUtf8.GetBytes(text);
            if (!hasBom)
                return content;
            byte[] result = new byte[Utf8Bom.Length + content.Length];
            Buffer.BlockCopy(Utf8Bom, 0, result, 0, Utf8Bom.Length);
            Buffer.BlockCopy(content, 0, result, Utf8Bom.Length, content.Length);
            return result;
        }

        internal static bool TryCompareAndSwap(
            string manifestPath,
            byte[] expectedBytes,
            byte[] replacementBytes,
            string preparedDisplacedPath,
            out bool alreadyReplaced,
            out string displacedPath,
            out string error)
        {
            alreadyReplaced = false;
            displacedPath = string.Empty;
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

            if (!TryCreateSiblingPath(fullPath, "replacement", out string temporaryPath, out error))
            {
                return false;
            }

            try
            {
                displacedPath = Path.GetFullPath(preparedDisplacedPath ?? string.Empty);
            }
            catch (Exception exception)
            {
                error = "The prepared manifest recovery path is invalid: " + exception.Message;
                return false;
            }

            if (!TryValidateOperationPaths(fullPath, out error, displacedPath))
                return false;

            bool replaceCompleted = false;
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
                            "Packages/manifest.json changed after it was inspected. Nothing was overwritten.";
                        return false;
                    }

                    if (File.Exists(displacedPath) || Directory.Exists(displacedPath))
                    {
                        error =
                            "The prepared manifest recovery path is already occupied. Nothing was overwritten.";
                        return false;
                    }

                    if (!TryValidateOperationPaths(fullPath, out error, temporaryPath, displacedPath))
                        return false;
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

                    if (!TryValidateOperationPaths(fullPath, out error, temporaryPath, displacedPath))
                        return false;
                    File.Replace(temporaryPath, fullPath, displacedPath, true);
                    replaceCompleted = true;

                    if (!TryValidateOperationPaths(fullPath, out string validationError, displacedPath))
                    {
                        error = BuildRecoveryError(displacedPath, validationError);
                        return false;
                    }

                    if (!TryReadRawBytes(displacedPath, out byte[] displacedBytes, out string readError))
                    {
                        error = BuildRecoveryError(displacedPath, readError);
                        return false;
                    }

                    if (BytesEqual(displacedBytes, expectedBytes))
                        return true;

                    bool restored = TryRestoreDisplaced(
                        fullPath,
                        displacedPath,
                        displacedBytes,
                        replacementBytes,
                        out error);
                    displacedPath = string.Empty;
                    return restored;
                }
            }
            catch (Exception exception)
            {
                error = "Packages/manifest.json could not be updated atomically: " +
                        exception.Message;
                if (replaceCompleted && File.Exists(displacedPath))
                    error += " Recovery bytes were preserved at " + displacedPath + ".";
                return false;
            }
            finally
            {
                TryDeleteExactOwnedFile(temporaryPath, replacementBytes);
            }
        }

        private static bool TryRestoreDisplaced(
            string fullPath,
            string initialCandidatePath,
            byte[] initialCandidateBytes,
            byte[] bytesInstalledBySwap,
            out string error)
        {
            string candidatePath = initialCandidatePath;
            byte[] candidateBytes = initialCandidateBytes;
            byte[] expectedDestination = bytesInstalledBySwap;
            for (int attempt = 0; attempt < MaximumRestoreAttempts; attempt++)
            {
                if (!TryCreateSiblingPath(
                        fullPath,
                        "recovery",
                        out string capturedPath,
                        out string pathError))
                {
                    error = BuildRecoveryError(candidatePath, pathError);
                    return false;
                }

                if (!TryValidateOperationPaths(
                        fullPath,
                        out string validationError,
                        candidatePath,
                        capturedPath))
                {
                    error = BuildRecoveryError(candidatePath, validationError);
                    return false;
                }

                try
                {
                    File.Replace(candidatePath, fullPath, capturedPath, true);
                }
                catch (Exception exception)
                {
                    error = BuildRecoveryError(
                        File.Exists(capturedPath) ? capturedPath : candidatePath,
                        exception.Message);
                    return false;
                }

                if (!TryValidateOperationPaths(fullPath, out validationError, capturedPath))
                {
                    error = BuildRecoveryError(capturedPath, validationError);
                    return false;
                }

                if (!TryReadRawBytes(
                        capturedPath,
                        out byte[] capturedBytes,
                        out string readError))
                {
                    error = BuildRecoveryError(capturedPath, readError);
                    return false;
                }

                if (BytesEqual(capturedBytes, expectedDestination))
                {
                    TryDeleteExactOwnedFile(capturedPath, expectedDestination);
                    error =
                        "Packages/manifest.json changed at the replacement boundary. The external edit was restored and the install was stopped.";
                    return false;
                }

                expectedDestination = candidateBytes;
                candidatePath = capturedPath;
                candidateBytes = capturedBytes;
            }

            error = BuildRecoveryError(
                candidatePath,
                "Packages/manifest.json kept changing during atomic recovery.");
            return false;
        }

        private static string BuildRecoveryError(string path, string detail)
        {
            return (string.IsNullOrWhiteSpace(detail) ? string.Empty : detail.TrimEnd() + " ") +
                   "Automatic recovery could not be proven safe. Recovery bytes were preserved at " +
                   path + ". Nothing there was deleted.";
        }

        internal static bool TryReadRawBytes(
            string path,
            out byte[] bytes,
            out string error)
        {
            bytes = Array.Empty<byte>();
            error = string.Empty;
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists || info.Length > MaximumManifestByteCount ||
                    (info.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                {
                    error = !info.Exists
                        ? "Packages/manifest.json does not exist."
                        : "Packages/manifest.json is linked, not regular, or exceeds 2 MiB.";
                    return false;
                }

                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
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
                error = "Packages/manifest.json could not be read safely: " + exception.Message;
                return false;
            }
        }

        private static bool TryReadManifest(
            string path,
            out byte[] bytes,
            out string text,
            out bool hasBom,
            out string error)
        {
            bytes = Array.Empty<byte>();
            text = string.Empty;
            hasBom = false;
            if (!TryResolveManifestPath(path, out string fullPath, out error) ||
                !TryReadRawBytes(fullPath, out bytes, out error))
            {
                return false;
            }

            hasBom = HasBom(bytes);
            int offset = hasBom ? Utf8Bom.Length : 0;
            try
            {
                text = StrictUtf8.GetString(bytes, offset, bytes.Length - offset);
            }
            catch (Exception exception)
            {
                error = "Packages/manifest.json must use valid UTF-8: " + exception.Message;
                return false;
            }

            if (string.IsNullOrWhiteSpace(text))
            {
                error = "Packages/manifest.json is empty.";
                return false;
            }

            return true;
        }

        private static bool TryResolveManifestPath(
            string path,
            out string fullPath,
            out string error)
        {
            fullPath = string.Empty;
            error = string.Empty;
            try
            {
                fullPath = Path.GetFullPath(path ?? string.Empty);
                if (!string.Equals(fullPath, Path.GetFullPath(ManifestPath), PathComparison))
                {
                    error = "The installer may edit only this project's Packages/manifest.json.";
                    return false;
                }

                if (!TryValidateProjectOwnedPath(fullPath, out error))
                    return false;
                var info = new FileInfo(fullPath);
                if (!info.Exists || info.Length > MaximumManifestByteCount ||
                    (info.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                {
                    error = "Packages/manifest.json must be a regular project-local file no larger than 2 MiB.";
                    return false;
                }

                return true;
            }
            catch (Exception exception)
            {
                error = "Packages/manifest.json could not be inspected safely: " + exception.Message;
                return false;
            }
        }

        private static bool TryCreateSiblingPath(
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
                for (int attempt = 0; attempt < 16; attempt++)
                {
                    string leaf = fileName + "." + role + "." + Guid.NewGuid().ToString("N") + ".tmp";
                    string candidate = Path.GetFullPath(Path.Combine(directory, leaf));
                    if (!string.Equals(Path.GetDirectoryName(candidate), directory, PathComparison) ||
                        File.Exists(candidate) || Directory.Exists(candidate))
                    {
                        continue;
                    }

                    operationPath = candidate;
                    return true;
                }

                error = "A unique manifest recovery path could not be created.";
                return false;
            }
            catch (Exception exception)
            {
                error = "A manifest recovery path could not be created: " + exception.Message;
                return false;
            }
        }

        internal static bool TryPrepareDisplacedPath(
            string manifestPath,
            string role,
            out string path,
            out string error)
        {
            path = string.Empty;
            if (!TryResolveManifestPath(manifestPath, out string fullPath, out error) ||
                string.IsNullOrEmpty(role))
            {
                return false;
            }

            return TryCreateSiblingPath(fullPath, role, out path, out error) &&
                   TryValidatePreparedSiblingPath(fullPath, path, role, out error);
        }

        internal static bool TryValidatePreparedSiblingPath(
            string manifestPath,
            string candidatePath,
            string role,
            out string error)
        {
            error = string.Empty;
            if (!TryResolveManifestPath(manifestPath, out string fullPath, out error) ||
                string.IsNullOrEmpty(role))
            {
                return false;
            }

            try
            {
                string candidate = Path.GetFullPath(candidatePath ?? string.Empty);
                string prefix = Path.GetFileName(fullPath) + "." + role + ".";
                string leaf = Path.GetFileName(candidate);
                const string suffix = ".tmp";
                if (!string.Equals(
                        Path.GetDirectoryName(candidate),
                        Path.GetDirectoryName(fullPath),
                        PathComparison) ||
                    !leaf.StartsWith(prefix, StringComparison.Ordinal) ||
                    !leaf.EndsWith(suffix, StringComparison.Ordinal))
                {
                    error = "A manifest recovery path does not match its exact installer role.";
                    return false;
                }

                string identifier = leaf.Substring(
                    prefix.Length,
                    leaf.Length - prefix.Length - suffix.Length);
                if (identifier.Length != 32)
                {
                    error = "A manifest recovery path has an invalid operation identifier.";
                    return false;
                }

                foreach (char character in identifier)
                {
                    if (!((character >= '0' && character <= '9') ||
                          (character >= 'a' && character <= 'f')))
                    {
                        error = "A manifest recovery path has an invalid operation identifier.";
                        return false;
                    }
                }

                return TryValidateProjectOwnedPath(candidate, out error);
            }
            catch (Exception exception)
            {
                error = "A manifest recovery path could not be validated: " +
                        exception.Message;
                return false;
            }
        }

        private static bool TryValidateOperationPaths(
            string manifestPath,
            out string error,
            params string[] operationPaths)
        {
            error = string.Empty;
            if (!TryResolveManifestPath(manifestPath, out string resolved, out error) ||
                !string.Equals(resolved, Path.GetFullPath(manifestPath), PathComparison))
            {
                return false;
            }

            string directory = Path.GetDirectoryName(resolved);
            foreach (string operationPath in operationPaths ?? Array.Empty<string>())
            {
                string candidate = Path.GetFullPath(operationPath ?? string.Empty);
                if (!string.Equals(Path.GetDirectoryName(candidate), directory, PathComparison) ||
                    !TryValidateProjectOwnedPath(candidate, out error))
                {
                    if (string.IsNullOrEmpty(error))
                        error = "A manifest operation path is not an exact project-local sibling.";
                    return false;
                }
            }

            return true;
        }

        private static bool TryValidateProjectOwnedPath(string candidatePath, out string error)
        {
            error = string.Empty;
            try
            {
                string root = Path.GetFullPath(ProjectRoot).TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar);
                string candidate = Path.GetFullPath(candidatePath);
                string prefix = root + Path.DirectorySeparatorChar;
                if (!candidate.StartsWith(prefix, PathComparison))
                {
                    error = "The path resolves outside the Unity project root.";
                    return false;
                }

                string pathRoot = Path.GetPathRoot(candidate);
                if (string.IsNullOrEmpty(pathRoot))
                {
                    error = "The project-owned path has no absolute filesystem root.";
                    return false;
                }

                string current = pathRoot;
                string[] parts = candidate.Substring(pathRoot.Length).Split(
                    new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                    StringSplitOptions.RemoveEmptyEntries);
                foreach (string part in parts)
                {
                    current = Path.Combine(current, part);
                    if (!File.Exists(current) && !Directory.Exists(current))
                        continue;
                    FileAttributes attributes = File.GetAttributes(current);
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        error = "The path contains a symbolic link, junction, or other reparse point.";
                        return false;
                    }
                }

                return true;
            }
            catch (Exception exception)
            {
                error = "The project-owned path could not be validated: " + exception.Message;
                return false;
            }
        }

        internal static bool TryValidateOwnedPath(string candidatePath, out string error)
        {
            return TryValidateProjectOwnedPath(candidatePath, out error);
        }

        internal static bool IsTrustedRegistryUrl(string value)
        {
            return IsCanonicalOpenUpmUrl(value);
        }

        private static bool IsExactVersion(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length > 128)
                return false;
            foreach (char character in value)
            {
                if (!(character >= '0' && character <= '9') &&
                    !(character >= 'A' && character <= 'Z') &&
                    !(character >= 'a' && character <= 'z') &&
                    character != '.' && character != '-' && character != '+')
                {
                    return false;
                }
            }

            return value.IndexOf('.') > 0 &&
                   string.Equals(value, value.Trim(), StringComparison.Ordinal);
        }

        internal static string Sha256(byte[] bytes)
        {
            using (SHA256 hash = SHA256.Create())
            {
                byte[] digest = hash.ComputeHash(bytes ?? Array.Empty<byte>());
                var builder = new StringBuilder(digest.Length * 2);
                foreach (byte value in digest)
                    builder.Append(value.ToString("x2"));
                return builder.ToString();
            }
        }

        internal static bool BytesEqual(byte[] first, byte[] second)
        {
            if (ReferenceEquals(first, second))
                return true;
            if (first == null || second == null || first.Length != second.Length)
                return false;
            int difference = 0;
            for (int index = 0; index < first.Length; index++)
                difference |= first[index] ^ second[index];
            return difference == 0;
        }

        internal static bool TryDeleteExactOwnedFile(string path, byte[] expectedBytes)
        {
            string quarantinePath = string.Empty;
            try
            {
                lock (MutationGate)
                {
                    if (string.IsNullOrEmpty(path) || !File.Exists(path) ||
                        !TryValidateProjectOwnedPath(path, out _) ||
                        !TryReadRawBytes(path, out byte[] current, out _) ||
                        !BytesEqual(current, expectedBytes) ||
                        !TryCreateSiblingPath(path, "delete", out quarantinePath, out _))
                    {
                        return false;
                    }

                    File.Move(path, quarantinePath);
                    if (!TryReadRawBytes(quarantinePath, out byte[] claimed, out _) ||
                        !BytesEqual(claimed, expectedBytes))
                    {
                        if (!File.Exists(path) && !Directory.Exists(path))
                        {
                            try
                            {
                                File.Move(quarantinePath, path);
                            }
                            catch
                            {
                            }
                        }

                        return false;
                    }

                    File.Delete(quarantinePath);
                    return !File.Exists(quarantinePath) && !File.Exists(path);
                }
            }
            catch
            {
                return false;
            }
        }

        private static bool HasBom(byte[] bytes)
        {
            return bytes != null && bytes.Length >= 3 &&
                   bytes[0] == Utf8Bom[0] && bytes[1] == Utf8Bom[1] && bytes[2] == Utf8Bom[2];
        }

        private static StringComparison PathComparison =>
            Path.DirectorySeparatorChar == '\\'
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
    }
}
