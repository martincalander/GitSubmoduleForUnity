using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace MartinCalander.GitSubmoduleManager.Editor
{
    internal interface ISessionStringStateStore
    {
        string Load();
        void Save(string value);
        void Clear();
    }

    internal sealed class UnitySessionStringStateStore : ISessionStringStateStore
    {
        private readonly string key;

        internal UnitySessionStringStateStore(string key)
        {
            this.key = key ?? string.Empty;
        }

        public string Load()
        {
            return string.IsNullOrEmpty(key)
                ? string.Empty
                : SessionState.GetString(key, string.Empty);
        }

        public void Save(string value)
        {
            if (!string.IsNullOrEmpty(key))
                SessionState.SetString(key, value ?? string.Empty);
        }

        public void Clear()
        {
            if (!string.IsNullOrEmpty(key))
                SessionState.EraseString(key);
        }
    }

    internal static class StrictSessionCacheJson
    {
        internal const int MaximumStructuralTokenCount = 160000;
        private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);

        private sealed class SyntaxReader
        {
            private readonly string json;
            private readonly int maximumDepth;
            private readonly int maximumTokenCount;
            private int index;
            private int tokenCount;

            internal SyntaxReader(
                string json,
                int maximumDepth,
                int maximumTokenCount)
            {
                this.json = json;
                this.maximumDepth = maximumDepth;
                this.maximumTokenCount = maximumTokenCount;
            }

            internal bool TryReadObjectDocument()
            {
                SkipWhitespace();
                if (index >= json.Length || json[index] != '{' ||
                    !TryConsumeToken() ||
                    !TryReadObject(1))
                {
                    return false;
                }

                SkipWhitespace();
                return index == json.Length;
            }

            private bool TryReadValue(int depth)
            {
                if (depth > maximumDepth)
                    return false;

                SkipWhitespace();
                if (index >= json.Length)
                    return false;
                if (!TryConsumeToken())
                    return false;

                switch (json[index])
                {
                    case '{':
                        return TryReadObject(depth);
                    case '[':
                        return TryReadArray(depth);
                    case '"':
                        return TryReadString();
                    case 't':
                        return TryReadLiteral("true");
                    case 'f':
                        return TryReadLiteral("false");
                    case 'n':
                        return TryReadLiteral("null");
                    default:
                        return TryReadNumber();
                }
            }

            private bool TryReadObject(int depth)
            {
                if (depth > maximumDepth || !TryReadCharacter('{'))
                    return false;

                SkipWhitespace();
                if (TryReadCharacter('}'))
                    return true;

                while (true)
                {
                    SkipWhitespace();
                    if (!TryConsumeToken() || !TryReadString())
                        return false;
                    SkipWhitespace();
                    if (!TryReadCharacter(':') || !TryReadValue(depth + 1))
                        return false;
                    SkipWhitespace();
                    if (TryReadCharacter('}'))
                        return true;
                    if (!TryReadCharacter(','))
                        return false;
                    SkipWhitespace();
                    if (index >= json.Length || json[index] == '}')
                        return false;
                }
            }

            private bool TryReadArray(int depth)
            {
                if (depth > maximumDepth || !TryReadCharacter('['))
                    return false;

                SkipWhitespace();
                if (TryReadCharacter(']'))
                    return true;

                while (true)
                {
                    if (!TryReadValue(depth + 1))
                        return false;
                    SkipWhitespace();
                    if (TryReadCharacter(']'))
                        return true;
                    if (!TryReadCharacter(','))
                        return false;
                    SkipWhitespace();
                    if (index >= json.Length || json[index] == ']')
                        return false;
                }
            }

            private bool TryReadString()
            {
                if (!TryReadCharacter('"'))
                    return false;

                while (index < json.Length)
                {
                    char character = json[index++];
                    if (character == '"')
                        return true;
                    if (character < 0x20)
                        return false;

                    if (char.IsHighSurrogate(character))
                    {
                        if (index >= json.Length ||
                            !char.IsLowSurrogate(json[index++]))
                        {
                            return false;
                        }
                        continue;
                    }

                    if (char.IsLowSurrogate(character))
                        return false;
                    if (character != '\\')
                        continue;
                    if (index >= json.Length)
                        return false;

                    char escape = json[index++];
                    if (escape == '"' || escape == '\\' || escape == '/' ||
                        escape == 'b' || escape == 'f' || escape == 'n' ||
                        escape == 'r' || escape == 't')
                    {
                        continue;
                    }

                    if (escape != 'u' ||
                        !TryReadHexCodeUnit(out int codeUnit))
                    {
                        return false;
                    }

                    if (codeUnit >= 0xDC00 && codeUnit <= 0xDFFF)
                        return false;
                    if (codeUnit < 0xD800 || codeUnit > 0xDBFF)
                        continue;

                    if (index + 2 > json.Length ||
                        json[index] != '\\' ||
                        json[index + 1] != 'u')
                    {
                        return false;
                    }

                    index += 2;
                    if (!TryReadHexCodeUnit(out int lowSurrogate) ||
                        lowSurrogate < 0xDC00 || lowSurrogate > 0xDFFF)
                    {
                        return false;
                    }
                }

                return false;
            }

            private bool TryReadHexCodeUnit(out int value)
            {
                value = 0;
                if (index > json.Length - 4)
                    return false;

                for (int offset = 0; offset < 4; offset++)
                {
                    char character = json[index++];
                    int digit = character >= '0' && character <= '9'
                        ? character - '0'
                        : character >= 'a' && character <= 'f'
                            ? character - 'a' + 10
                            : character >= 'A' && character <= 'F'
                                ? character - 'A' + 10
                                : -1;
                    if (digit < 0)
                        return false;
                    value = value * 16 + digit;
                }

                return true;
            }

            private bool TryReadNumber()
            {
                int start = index;
                if (TryReadCharacter('-') && index >= json.Length)
                    return false;

                if (TryReadCharacter('0'))
                {
                    if (index < json.Length && char.IsDigit(json[index]))
                        return false;
                }
                else
                {
                    if (index >= json.Length ||
                        json[index] < '1' || json[index] > '9')
                    {
                        return false;
                    }

                    while (index < json.Length &&
                           json[index] >= '0' && json[index] <= '9')
                    {
                        index++;
                    }
                }

                if (TryReadCharacter('.'))
                {
                    int fractionStart = index;
                    while (index < json.Length &&
                           json[index] >= '0' && json[index] <= '9')
                    {
                        index++;
                    }
                    if (index == fractionStart)
                        return false;
                }

                if (index < json.Length &&
                    (json[index] == 'e' || json[index] == 'E'))
                {
                    index++;
                    if (index < json.Length &&
                        (json[index] == '+' || json[index] == '-'))
                    {
                        index++;
                    }

                    int exponentStart = index;
                    while (index < json.Length &&
                           json[index] >= '0' && json[index] <= '9')
                    {
                        index++;
                    }
                    if (index == exponentStart)
                        return false;
                }

                return index > start;
            }

            private bool TryReadLiteral(string literal)
            {
                if (index > json.Length - literal.Length ||
                    !string.Equals(
                        json.Substring(index, literal.Length),
                        literal,
                        StringComparison.Ordinal))
                {
                    return false;
                }

                index += literal.Length;
                return true;
            }

            private bool TryConsumeToken()
            {
                if (tokenCount >= maximumTokenCount)
                    return false;

                tokenCount++;
                return true;
            }

            private bool TryReadCharacter(char expected)
            {
                if (index >= json.Length || json[index] != expected)
                    return false;
                index++;
                return true;
            }

            private void SkipWhitespace()
            {
                while (index < json.Length)
                {
                    char character = json[index];
                    if (character != ' ' && character != '\t' &&
                        character != '\r' && character != '\n')
                    {
                        return;
                    }
                    index++;
                }
            }
        }

        internal static bool TryParseObject(
            string json,
            int maximumByteCount,
            int maximumDepth,
            out JObject root,
            int maximumTokenCount = MaximumStructuralTokenCount)
        {
            root = null;
            if (!IsStrictObjectDocument(
                    json,
                    maximumByteCount,
                    maximumDepth,
                    maximumTokenCount))
                return false;

            try
            {
                using (var stringReader = new StringReader(json))
                using (var jsonReader = new JsonTextReader(stringReader)
                       {
                           DateParseHandling = DateParseHandling.None,
                           MaxDepth = maximumDepth
                       })
                {
                    if (!jsonReader.Read() ||
                        jsonReader.TokenType != JsonToken.StartObject)
                    {
                        return false;
                    }

                    root = JObject.Load(
                        jsonReader,
                        new JsonLoadSettings
                        {
                            CommentHandling = CommentHandling.Load,
                            DuplicatePropertyNameHandling =
                                DuplicatePropertyNameHandling.Error,
                            LineInfoHandling = LineInfoHandling.Ignore
                        });

                    if (root.DescendantsAndSelf().Any(
                            token => token.Type == JTokenType.Comment))
                    {
                        root = null;
                        return false;
                    }

                    while (jsonReader.Read())
                    {
                        root = null;
                        return false;
                    }
                }

                return root != null;
            }
            catch
            {
                root = null;
                return false;
            }
        }

        internal static bool IsStrictObjectDocument(
            string json,
            int maximumByteCount,
            int maximumDepth,
            int maximumTokenCount = MaximumStructuralTokenCount)
        {
            if (string.IsNullOrWhiteSpace(json) ||
                maximumByteCount <= 0 ||
                maximumDepth <= 0 ||
                maximumTokenCount <= 0 ||
                json.Length > maximumByteCount)
            {
                return false;
            }

            try
            {
                return new SyntaxReader(
                           json,
                           maximumDepth,
                           maximumTokenCount)
                           .TryReadObjectDocument() &&
                       StrictUtf8.GetByteCount(json) <= maximumByteCount;
            }
            catch
            {
                return false;
            }
        }

        internal static bool HasExactProperties(
            JObject value,
            params string[] propertyNames)
        {
            if (value == null || propertyNames == null ||
                value.Count != propertyNames.Length)
            {
                return false;
            }

            var expected = new HashSet<string>(
                propertyNames,
                StringComparer.Ordinal);
            return expected.Count == propertyNames.Length &&
                   value.Properties().All(property => expected.Contains(property.Name));
        }

        internal static bool TryReadString(
            JObject value,
            string propertyName,
            int maximumLength,
            bool allowEmpty,
            out string result)
        {
            result = string.Empty;
            JToken token = value?[propertyName];
            if (token?.Type != JTokenType.String)
                return false;

            result = token.Value<string>() ?? string.Empty;
            return IsBoundedText(
                result,
                maximumLength,
                allowEmpty,
                allowLineBreaks: true);
        }

        internal static bool TryReadBoolean(
            JObject value,
            string propertyName,
            out bool result)
        {
            result = false;
            JToken token = value?[propertyName];
            if (token?.Type != JTokenType.Boolean)
                return false;

            result = token.Value<bool>();
            return true;
        }

        internal static bool TryReadInt64(
            JObject value,
            string propertyName,
            out long result)
        {
            result = 0L;
            JToken token = value?[propertyName];
            if (token?.Type != JTokenType.Integer)
                return false;

            try
            {
                result = token.Value<long>();
                return true;
            }
            catch
            {
                return false;
            }
        }

        internal static bool IsBoundedText(
            string value,
            int maximumLength,
            bool allowEmpty,
            bool allowLineBreaks = false)
        {
            if (value == null || value.Length > maximumLength ||
                (!allowEmpty && value.Length == 0))
            {
                return false;
            }

            for (int index = 0; index < value.Length; index++)
            {
                char character = value[index];
                if (char.IsHighSurrogate(character))
                {
                    if (index + 1 >= value.Length ||
                        !char.IsLowSurrogate(value[index + 1]))
                    {
                        return false;
                    }

                    index++;
                    continue;
                }

                if (char.IsLowSurrogate(character))
                    return false;

                if (character == '\0' ||
                    (char.IsControl(character) &&
                     (!allowLineBreaks ||
                      character != '\r' &&
                      character != '\n' &&
                      character != '\t')))
                {
                    return false;
                }
            }

            return true;
        }
    }

    internal sealed class PackageManagerGitHubCachedCatalogue
    {
        internal PackageManagerGitHubCachedCatalogue(
            string accountId,
            string accountLogin,
            double verifiedAt,
            double expiresAt,
            IReadOnlyList<PackageManagerGitHubRepository> repositories)
        {
            AccountId = accountId ?? string.Empty;
            AccountLogin = accountLogin ?? string.Empty;
            VerifiedAt = verifiedAt;
            ExpiresAt = expiresAt;
            Repositories = repositories ??
                new ReadOnlyCollection<PackageManagerGitHubRepository>(
                    Array.Empty<PackageManagerGitHubRepository>());
        }

        internal string AccountId { get; }
        internal string AccountLogin { get; }
        internal double VerifiedAt { get; }
        internal double ExpiresAt { get; }
        internal IReadOnlyList<PackageManagerGitHubRepository> Repositories { get; }

        internal bool MatchesAccount(string accountId, string accountLogin)
        {
            return string.Equals(AccountId, accountId, StringComparison.Ordinal) &&
                   string.Equals(
                       AccountLogin,
                       accountLogin,
                       StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Session-only stale-while-revalidate catalogue. The payload is presentation
    /// data, never coverage or mutation evidence, and is accepted only after the
    /// current GitHub account has been resolved again by a live request.
    /// </summary>
    internal sealed class PackageManagerGitHubCatalogueSessionCache
    {
        internal const int MaximumPayloadByteCount = 4 * 1024 * 1024;
        internal const int MaximumRepositoryCount = 2048;
        internal const int MaximumDependenciesPerRepository = 512;
        internal const int MaximumTotalDependencyCount = 8192;

        private const int SchemaVersion = 1;
        private const int MaximumJsonDepth = 16;
        private const long LifetimeMilliseconds = 15L * 60L * 1000L;
        private const string KeyPrefix =
            "MartinCalander.GitSubmoduleManager.GitHubCatalogue.v1.";
        private static readonly UTF8Encoding CacheUtf8Encoding =
            new(false, true);

        private sealed class PayloadLimitExceededException : Exception
        {
        }

        private sealed class BoundedMemoryStream : MemoryStream
        {
            private readonly long maximumLength;

            internal BoundedMemoryStream(int maximumLength)
                : base(Math.Min(maximumLength, 64 * 1024))
            {
                this.maximumLength = maximumLength;
            }

            public override void Write(byte[] buffer, int offset, int count)
            {
                if (count > maximumLength - Length)
                    throw new PayloadLimitExceededException();
                base.Write(buffer, offset, count);
            }

            public override void WriteByte(byte value)
            {
                if (Length >= maximumLength)
                    throw new PayloadLimitExceededException();
                base.WriteByte(value);
            }
        }

        private static readonly string[] RootProperties =
        {
            "schemaVersion",
            "projectFingerprint",
            "unityVersion",
            "githubHost",
            "accountId",
            "accountLogin",
            "verifiedAtEditorUptimeMilliseconds",
            "repositories"
        };

        private static readonly string[] RepositoryProperties =
        {
            "nodeId",
            "name",
            "owner",
            "url",
            "defaultBranch",
            "isPrivate",
            "description",
            "updatedAt",
            "packageName",
            "displayName",
            "version",
            "packageDescription",
            "minimumUnityVersion",
            "authorName",
            "license",
            "documentationUrl",
            "changelogUrl",
            "licensesUrl",
            "dependencies",
            "packageManifestCommitOid",
            "packageManifestBlobOid",
            "packageManifestMetaBlobOid",
            "packageManifestMetaGuid"
        };

        private readonly ISessionStringStateStore store;
        private readonly string projectFingerprint;
        private readonly string unityVersion;

        internal PackageManagerGitHubCatalogueSessionCache(
            ISessionStringStateStore store,
            string projectFingerprint,
            string unityVersion)
        {
            this.store = store;
            this.projectFingerprint = projectFingerprint ?? string.Empty;
            this.unityVersion = unityVersion ?? string.Empty;
        }

        internal static PackageManagerGitHubCatalogueSessionCache CreateDefault()
        {
            string fingerprint = GitUtility.GetRepositoryLocationFingerprint(
                GitUtility.ProjectRoot);
            string keySuffix = fingerprint
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');
            return new PackageManagerGitHubCatalogueSessionCache(
                new UnitySessionStringStateStore(KeyPrefix + keySuffix),
                fingerprint,
                Application.unityVersion);
        }

        internal bool TryLoad(
            double currentTime,
            out PackageManagerGitHubCachedCatalogue catalogue)
        {
            catalogue = null;
            string json;
            try
            {
                json = store?.Load() ?? string.Empty;
            }
            catch
            {
                return false;
            }

            if (string.IsNullOrEmpty(json))
                return false;

            if (!TryDecode(json, currentTime, out catalogue))
            {
                Clear();
                return false;
            }

            return true;
        }

        internal bool Save(
            string accountId,
            string accountLogin,
            IReadOnlyList<PackageManagerGitHubRepository> repositories,
            double verifiedAt)
        {
            if (!TryBuildPayload(
                    accountId,
                    accountLogin,
                    repositories,
                    verifiedAt,
                    out string json))
            {
                Clear();
                return false;
            }

            try
            {
                store?.Save(json);
                return store != null;
            }
            catch
            {
                Clear();
                return false;
            }
        }

        internal void Clear()
        {
            try
            {
                store?.Clear();
            }
            catch
            {
                // Session cache cleanup must never interrupt Editor lifecycle.
            }
        }

        private bool TryBuildPayload(
            string accountId,
            string accountLogin,
            IReadOnlyList<PackageManagerGitHubRepository> repositories,
            double verifiedAt,
            out string json)
        {
            json = string.Empty;
            if (!TryNormalizeVerifiedAt(verifiedAt, out long verifiedAtMilliseconds) ||
                !IsValidProjectFingerprint(projectFingerprint) ||
                !IsValidUnityVersion(unityVersion) ||
                !GitHubUtility.TryNormalizeAccountIdentity(
                    accountId,
                    accountLogin,
                    out string normalizedAccountId,
                    out string normalizedLogin) ||
                repositories == null ||
                repositories.Count == 0 ||
                repositories.Count > MaximumRepositoryCount)
            {
                return false;
            }

            int totalDependencies = 0;
            var nodeIds = new HashSet<string>(StringComparer.Ordinal);
            var repositoryIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (PackageManagerGitHubRepository repository in repositories)
            {
                if (!IsValidRepository(
                        repository,
                        nodeIds,
                        repositoryIds,
                        ref totalDependencies))
                {
                    return false;
                }
            }

            try
            {
                using (var stream = new BoundedMemoryStream(
                           MaximumPayloadByteCount))
                using (var textWriter = new StreamWriter(
                           stream,
                           CacheUtf8Encoding,
                           4096,
                           leaveOpen: true))
                using (var writer = new JsonTextWriter(textWriter)
                       {
                           CloseOutput = false,
                           Culture = CultureInfo.InvariantCulture,
                           Formatting = Formatting.None
                       })
                {
                    writer.WriteStartObject();
                    WriteProperty(writer, "schemaVersion", SchemaVersion);
                    WriteProperty(writer, "projectFingerprint", projectFingerprint);
                    WriteProperty(writer, "unityVersion", unityVersion);
                    WriteProperty(writer, "githubHost", GitHubUtility.GitHubHost);
                    WriteProperty(writer, "accountId", normalizedAccountId);
                    WriteProperty(writer, "accountLogin", normalizedLogin);
                    WriteProperty(
                        writer,
                        "verifiedAtEditorUptimeMilliseconds",
                        verifiedAtMilliseconds);
                    writer.WritePropertyName("repositories");
                    writer.WriteStartArray();
                    foreach (PackageManagerGitHubRepository repository in repositories)
                        WriteRepository(writer, repository);
                    writer.WriteEndArray();
                    writer.WriteEndObject();
                    writer.Flush();
                    textWriter.Flush();
                    json = CacheUtf8Encoding.GetString(stream.ToArray());
                }
            }
            catch
            {
                json = string.Empty;
                return false;
            }

            return StrictSessionCacheJson.IsStrictObjectDocument(
                json,
                MaximumPayloadByteCount,
                MaximumJsonDepth);
        }

        private bool TryDecode(
            string json,
            double currentTime,
            out PackageManagerGitHubCachedCatalogue catalogue)
        {
            catalogue = null;
            if (!TryNormalizeVerifiedAt(currentTime, out long currentMilliseconds) ||
                !StrictSessionCacheJson.TryParseObject(
                    json,
                    MaximumPayloadByteCount,
                    MaximumJsonDepth,
                    out JObject root) ||
                !StrictSessionCacheJson.HasExactProperties(root, RootProperties) ||
                !StrictSessionCacheJson.TryReadInt64(
                    root,
                    "schemaVersion",
                    out long schemaVersion) ||
                schemaVersion != SchemaVersion ||
                !StrictSessionCacheJson.TryReadString(
                    root,
                    "projectFingerprint",
                    64,
                    false,
                    out string storedProjectFingerprint) ||
                !string.Equals(
                    storedProjectFingerprint,
                    projectFingerprint,
                    StringComparison.Ordinal) ||
                !IsValidProjectFingerprint(storedProjectFingerprint) ||
                !StrictSessionCacheJson.TryReadString(
                    root,
                    "unityVersion",
                    64,
                    false,
                    out string storedUnityVersion) ||
                !string.Equals(
                    storedUnityVersion,
                    unityVersion,
                    StringComparison.Ordinal) ||
                !IsValidUnityVersion(storedUnityVersion) ||
                !StrictSessionCacheJson.TryReadString(
                    root,
                    "githubHost",
                    64,
                    false,
                    out string githubHost) ||
                !string.Equals(
                    githubHost,
                    GitHubUtility.GitHubHost,
                    StringComparison.OrdinalIgnoreCase) ||
                !StrictSessionCacheJson.TryReadString(
                    root,
                    "accountId",
                    20,
                    false,
                    out string accountId) ||
                !StrictSessionCacheJson.TryReadString(
                    root,
                    "accountLogin",
                    39,
                    false,
                    out string accountLogin) ||
                !GitHubUtility.TryNormalizeAccountIdentity(
                    accountId,
                    accountLogin,
                    out accountId,
                    out accountLogin) ||
                !StrictSessionCacheJson.TryReadInt64(
                    root,
                    "verifiedAtEditorUptimeMilliseconds",
                    out long verifiedAtMilliseconds) ||
                verifiedAtMilliseconds < 0 ||
                verifiedAtMilliseconds > long.MaxValue - LifetimeMilliseconds ||
                verifiedAtMilliseconds > currentMilliseconds ||
                currentMilliseconds >= verifiedAtMilliseconds + LifetimeMilliseconds ||
                !(root["repositories"] is JArray repositoryArray) ||
                repositoryArray.Count == 0 ||
                repositoryArray.Count > MaximumRepositoryCount)
            {
                return false;
            }

            var repositories = new List<PackageManagerGitHubRepository>(
                repositoryArray.Count);
            var nodeIds = new HashSet<string>(StringComparer.Ordinal);
            var repositoryIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int totalDependencies = 0;
            foreach (JToken token in repositoryArray)
            {
                if (!(token is JObject repositoryObject) ||
                    !TryDecodeRepository(
                        repositoryObject,
                        nodeIds,
                        repositoryIds,
                        ref totalDependencies,
                        out PackageManagerGitHubRepository repository))
                {
                    return false;
                }

                repositories.Add(repository);
            }

            repositories.Sort(CompareRepositories);
            catalogue = new PackageManagerGitHubCachedCatalogue(
                accountId,
                accountLogin,
                verifiedAtMilliseconds / 1000d,
                (verifiedAtMilliseconds + LifetimeMilliseconds) / 1000d,
                new ReadOnlyCollection<PackageManagerGitHubRepository>(
                    repositories.ToArray()));
            return true;
        }

        private static void WriteRepository(
            JsonWriter writer,
            PackageManagerGitHubRepository repository)
        {
            writer.WriteStartObject();
            WriteProperty(writer, "nodeId", repository.NodeId);
            WriteProperty(writer, "name", repository.Name);
            WriteProperty(writer, "owner", repository.Owner);
            WriteProperty(writer, "url", repository.Url);
            WriteProperty(writer, "defaultBranch", repository.DefaultBranch);
            WriteProperty(writer, "isPrivate", repository.IsPrivate);
            WriteProperty(writer, "description", repository.Description);
            WriteProperty(writer, "updatedAt", repository.UpdatedAt);
            WriteProperty(writer, "packageName", repository.PackageName);
            WriteProperty(writer, "displayName", repository.DisplayName);
            WriteProperty(writer, "version", repository.Version);
            WriteProperty(
                writer,
                "packageDescription",
                repository.PackageDescription);
            WriteProperty(
                writer,
                "minimumUnityVersion",
                repository.MinimumUnityVersion);
            WriteProperty(writer, "authorName", repository.AuthorName);
            WriteProperty(writer, "license", repository.License);
            WriteProperty(
                writer,
                "documentationUrl",
                repository.DocumentationUrl);
            WriteProperty(writer, "changelogUrl", repository.ChangelogUrl);
            WriteProperty(writer, "licensesUrl", repository.LicensesUrl);
            writer.WritePropertyName("dependencies");
            writer.WriteStartArray();
            foreach (PackageManifestDependency dependency in repository.Dependencies)
            {
                writer.WriteStartObject();
                WriteProperty(writer, "name", dependency.Name);
                WriteProperty(writer, "version", dependency.Version);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            WriteProperty(
                writer,
                "packageManifestCommitOid",
                repository.PackageManifestCommitOid);
            WriteProperty(
                writer,
                "packageManifestBlobOid",
                repository.PackageManifestBlobOid);
            WriteProperty(
                writer,
                "packageManifestMetaBlobOid",
                repository.PackageManifestMetaBlobOid);
            WriteProperty(
                writer,
                "packageManifestMetaGuid",
                repository.PackageManifestMetaGuid);
            writer.WriteEndObject();
        }

        private static void WriteProperty(
            JsonWriter writer,
            string propertyName,
            string value)
        {
            writer.WritePropertyName(propertyName);
            writer.WriteValue(value);
        }

        private static void WriteProperty(
            JsonWriter writer,
            string propertyName,
            bool value)
        {
            writer.WritePropertyName(propertyName);
            writer.WriteValue(value);
        }

        private static void WriteProperty(
            JsonWriter writer,
            string propertyName,
            long value)
        {
            writer.WritePropertyName(propertyName);
            writer.WriteValue(value);
        }

        private static bool TryDecodeRepository(
            JObject value,
            HashSet<string> nodeIds,
            HashSet<string> repositoryIds,
            ref int totalDependencies,
            out PackageManagerGitHubRepository repository)
        {
            repository = null;
            if (!StrictSessionCacheJson.HasExactProperties(
                    value,
                    RepositoryProperties) ||
                !TryReadRepositoryStrings(value, out GitHubRepo mutable) ||
                !StrictSessionCacheJson.TryReadBoolean(
                    value,
                    "isPrivate",
                    out mutable.IsPrivate) ||
                !(value["dependencies"] is JArray dependencyArray) ||
                dependencyArray.Count > MaximumDependenciesPerRepository ||
                totalDependencies > MaximumTotalDependencyCount - dependencyArray.Count)
            {
                return false;
            }

            var dependencies = new List<PackageManifestDependency>(
                dependencyArray.Count);
            string previousDependencyName = string.Empty;
            foreach (JToken token in dependencyArray)
            {
                if (!(token is JObject dependencyObject) ||
                    !StrictSessionCacheJson.HasExactProperties(
                        dependencyObject,
                        "name",
                        "version") ||
                    !StrictSessionCacheJson.TryReadString(
                        dependencyObject,
                        "name",
                        214,
                        false,
                        out string dependencyName) ||
                    !StrictSessionCacheJson.TryReadString(
                        dependencyObject,
                        "version",
                        1024,
                        false,
                        out string dependencyVersion) ||
                    !GitUtility.IsValidUpmPackageName(dependencyName) ||
                    !StrictSessionCacheJson.IsBoundedText(
                        dependencyVersion,
                        1024,
                        false) ||
                    !string.Equals(
                        dependencyVersion,
                        GitUtility.RedactCredentials(dependencyVersion),
                        StringComparison.Ordinal) ||
                    string.Compare(
                        previousDependencyName,
                        dependencyName,
                        StringComparison.Ordinal) >= 0)
                {
                    return false;
                }

                dependencies.Add(new PackageManifestDependency(
                    dependencyName,
                    dependencyVersion));
                previousDependencyName = dependencyName;
            }

            mutable.DeclaredDependencies = dependencies.ToArray();
            mutable.ManifestState = PackageManifestState.Valid;
            repository = new PackageManagerGitHubRepository(mutable);
            if (!IsValidRepository(
                    repository,
                    nodeIds,
                    repositoryIds,
                    ref totalDependencies))
            {
                repository = null;
                return false;
            }

            return true;
        }

        private static bool TryReadRepositoryStrings(
            JObject value,
            out GitHubRepo repository)
        {
            repository = new GitHubRepo();
            return
                StrictSessionCacheJson.TryReadString(value, "nodeId", 256, false, out repository.NodeId) &&
                StrictSessionCacheJson.TryReadString(value, "name", 100, false, out repository.Name) &&
                StrictSessionCacheJson.TryReadString(value, "owner", 100, false, out repository.Owner) &&
                StrictSessionCacheJson.TryReadString(value, "url", 4096, false, out repository.Url) &&
                StrictSessionCacheJson.TryReadString(value, "defaultBranch", 1024, false, out repository.DefaultBranch) &&
                StrictSessionCacheJson.TryReadString(value, "description", 1024, true, out repository.Description) &&
                StrictSessionCacheJson.TryReadString(value, "updatedAt", 64, true, out repository.UpdatedAt) &&
                StrictSessionCacheJson.TryReadString(value, "packageName", 214, false, out repository.DeclaredPackageName) &&
                StrictSessionCacheJson.TryReadString(value, "displayName", 512, true, out repository.DeclaredDisplayName) &&
                StrictSessionCacheJson.TryReadString(value, "version", 1024, false, out repository.DeclaredVersion) &&
                StrictSessionCacheJson.TryReadString(value, "packageDescription", 10000, true, out repository.DeclaredDescription) &&
                StrictSessionCacheJson.TryReadString(value, "minimumUnityVersion", 129, true, out repository.DeclaredMinimumUnityVersion) &&
                StrictSessionCacheJson.TryReadString(value, "authorName", 256, true, out repository.DeclaredAuthorName) &&
                StrictSessionCacheJson.TryReadString(value, "license", 256, true, out repository.DeclaredLicense) &&
                StrictSessionCacheJson.TryReadString(value, "documentationUrl", 4096, true, out repository.DeclaredDocumentationUrl) &&
                StrictSessionCacheJson.TryReadString(value, "changelogUrl", 4096, true, out repository.DeclaredChangelogUrl) &&
                StrictSessionCacheJson.TryReadString(value, "licensesUrl", 4096, true, out repository.DeclaredLicensesUrl) &&
                StrictSessionCacheJson.TryReadString(value, "packageManifestCommitOid", 64, false, out repository.PackageManifestCommitOid) &&
                StrictSessionCacheJson.TryReadString(value, "packageManifestBlobOid", 64, false, out repository.PackageManifestBlobOid) &&
                StrictSessionCacheJson.TryReadString(value, "packageManifestMetaBlobOid", 64, false, out repository.PackageManifestMetaBlobOid) &&
                StrictSessionCacheJson.TryReadString(value, "packageManifestMetaGuid", 32, false, out repository.PackageManifestMetaGuid);
        }

        private static bool IsValidRepository(
            PackageManagerGitHubRepository repository,
            HashSet<string> nodeIds,
            HashSet<string> repositoryIds,
            ref int totalDependencies)
        {
            if (repository == null ||
                !IsValidNodeId(repository.NodeId) ||
                !IsValidGitHubRepositoryName(repository.Name) ||
                !GitHubUtility.TryNormalizeAccountLogin(
                    repository.Owner,
                    out string normalizedOwner) ||
                !string.Equals(
                    normalizedOwner,
                    repository.Owner,
                    StringComparison.Ordinal) ||
                !IsGitHubRepositoryUrl(
                    repository.Url,
                    repository.Owner,
                    repository.Name) ||
                !IsSimpleText(repository.DefaultBranch, 1024, false) ||
                string.Equals(repository.DefaultBranch, ".", StringComparison.Ordinal) ||
                !GitUtility.IsValidBranchName(repository.DefaultBranch) ||
                !StrictSessionCacheJson.IsBoundedText(
                    repository.Description,
                    1024,
                    true,
                    allowLineBreaks: true) ||
                !IsSimpleText(repository.UpdatedAt, 64, true) ||
                !GitUtility.IsValidUpmPackageName(repository.PackageName) ||
                !StrictSessionCacheJson.IsBoundedText(
                    repository.DisplayName,
                    512,
                    true,
                    allowLineBreaks: true) ||
                !GitUtility.IsValidSemanticVersion(repository.Version) ||
                repository.Version.Length > 1024 ||
                !StrictSessionCacheJson.IsBoundedText(
                    repository.PackageDescription,
                    10000,
                    true,
                    allowLineBreaks: true) ||
                !IsSimpleText(repository.MinimumUnityVersion, 129, true) ||
                !IsSimpleText(repository.AuthorName, 256, true) ||
                !IsSimpleText(repository.License, 256, true) ||
                !IsSafeManifestUrl(repository.DocumentationUrl) ||
                !IsSafeManifestUrl(repository.ChangelogUrl) ||
                !IsSafeManifestUrl(repository.LicensesUrl) ||
                !IsCanonicalGitObjectId(repository.PackageManifestCommitOid) ||
                !IsCanonicalGitObjectId(repository.PackageManifestBlobOid) ||
                !IsCanonicalGitObjectId(repository.PackageManifestMetaBlobOid) ||
                !IsValidMetaGuid(repository.PackageManifestMetaGuid) ||
                repository.Dependencies == null ||
                repository.Dependencies.Count > MaximumDependenciesPerRepository ||
                totalDependencies > MaximumTotalDependencyCount - repository.Dependencies.Count ||
                !nodeIds.Add(repository.NodeId) ||
                !repositoryIds.Add(repository.Owner + "/" + repository.Name))
            {
                return false;
            }

            string previousDependencyName = string.Empty;
            foreach (PackageManifestDependency dependency in repository.Dependencies)
            {
                if (dependency == null ||
                    !GitUtility.IsValidUpmPackageName(dependency.Name) ||
                    !StrictSessionCacheJson.IsBoundedText(
                        dependency.Version,
                        1024,
                        false) ||
                    !string.Equals(
                        dependency.Version,
                        GitUtility.RedactCredentials(dependency.Version),
                        StringComparison.Ordinal) ||
                    string.Compare(
                        previousDependencyName,
                        dependency.Name,
                        StringComparison.Ordinal) >= 0)
                {
                    return false;
                }

                previousDependencyName = dependency.Name;
            }

            totalDependencies += repository.Dependencies.Count;
            return true;
        }

        private static bool IsValidGitHubRepositoryName(string value)
        {
            if (!IsSimpleText(value, 100, false) ||
                string.Equals(value, ".", StringComparison.Ordinal) ||
                string.Equals(value, "..", StringComparison.Ordinal))
            {
                return false;
            }

            foreach (char character in value)
            {
                bool valid = character >= 'a' && character <= 'z' ||
                             character >= 'A' && character <= 'Z' ||
                             character >= '0' && character <= '9' ||
                             character == '-' || character == '_' ||
                             character == '.';
                if (!valid)
                    return false;
            }

            return true;
        }

        private static bool IsValidNodeId(string value)
        {
            if (!IsSimpleText(value, 256, false))
                return false;

            foreach (char character in value)
            {
                if (char.IsWhiteSpace(character))
                    return false;
            }

            return true;
        }

        private static bool IsCanonicalGitObjectId(string value)
        {
            return IsSimpleText(value, 64, false) &&
                   GitUtility.IsValidGitObjectId(value);
        }

        private static bool IsGitHubRepositoryUrl(
            string url,
            string expectedOwner,
            string expectedName)
        {
            string canonicalUrl =
                "https://" + GitHubUtility.GitHubHost + "/" +
                expectedOwner + "/" + expectedName;
            bool hasCanonicalSpelling = string.Equals(
                                            url,
                                            canonicalUrl,
                                            StringComparison.OrdinalIgnoreCase) ||
                                        string.Equals(
                                            url,
                                            canonicalUrl + ".git",
                                            StringComparison.OrdinalIgnoreCase);
            return hasCanonicalSpelling &&
                   IsSimpleText(url, 4096, false) &&
                   GitUtility.IsValidRepositoryUrl(url) &&
                   Uri.TryCreate(url, UriKind.Absolute, out Uri uri) &&
                   string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal) &&
                   string.Equals(uri.IdnHost, GitHubUtility.GitHubHost, StringComparison.OrdinalIgnoreCase) &&
                   uri.IsDefaultPort &&
                   string.IsNullOrEmpty(uri.UserInfo) &&
                   string.IsNullOrEmpty(uri.Query) &&
                   string.IsNullOrEmpty(uri.Fragment) &&
                   (string.Equals(
                        uri.AbsolutePath,
                        "/" + expectedOwner + "/" + expectedName,
                        StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(
                        uri.AbsolutePath,
                        "/" + expectedOwner + "/" + expectedName + ".git",
                        StringComparison.OrdinalIgnoreCase));
        }

        private static bool IsSafeManifestUrl(string value)
        {
            if (string.IsNullOrEmpty(value))
                return true;

            return value.Length <= 4096 &&
                   string.Equals(value, value.Trim(), StringComparison.Ordinal) &&
                   Uri.TryCreate(value, UriKind.Absolute, out Uri uri) &&
                   string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal) &&
                   !string.IsNullOrEmpty(uri.Host) &&
                   string.IsNullOrEmpty(uri.UserInfo) &&
                   value.IndexOfAny(new[] { '\0', '\r', '\n' }) < 0 &&
                   string.Equals(
                       value,
                       GitUtility.RedactCredentials(value),
                       StringComparison.Ordinal);
        }

        private static bool IsSimpleText(
            string value,
            int maximumLength,
            bool allowEmpty)
        {
            return StrictSessionCacheJson.IsBoundedText(
                       value,
                       maximumLength,
                       allowEmpty) &&
                   string.Equals(value, value.Trim(), StringComparison.Ordinal);
        }

        private static bool IsValidMetaGuid(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length != 32)
                return false;

            bool hasNonZero = false;
            foreach (char character in value)
            {
                bool isHex = character >= '0' && character <= '9' ||
                             character >= 'a' && character <= 'f' ||
                             character >= 'A' && character <= 'F';
                if (!isHex)
                    return false;
                if (character != '0')
                    hasNonZero = true;
            }

            return hasNonZero;
        }

        private static bool IsValidProjectFingerprint(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length > 64)
                return false;

            try
            {
                byte[] decoded = Convert.FromBase64String(value);
                return decoded.Length == 32 &&
                       string.Equals(
                           Convert.ToBase64String(decoded),
                           value,
                           StringComparison.Ordinal);
            }
            catch
            {
                return false;
            }
        }

        private static bool IsValidUnityVersion(string value)
        {
            return IsSimpleText(value, 64, false);
        }

        private static bool TryNormalizeVerifiedAt(
            double value,
            out long milliseconds)
        {
            milliseconds = 0L;
            if (double.IsNaN(value) || double.IsInfinity(value) || value < 0d ||
                value > (long.MaxValue - LifetimeMilliseconds) / 1000d)
            {
                return false;
            }

            milliseconds = (long)Math.Floor(value * 1000d);
            return true;
        }

        private static int CompareRepositories(
            PackageManagerGitHubRepository left,
            PackageManagerGitHubRepository right)
        {
            int ownerComparison = string.Compare(
                left?.Owner,
                right?.Owner,
                StringComparison.OrdinalIgnoreCase);
            if (ownerComparison != 0)
                return ownerComparison;

            int nameComparison = string.Compare(
                left?.Name,
                right?.Name,
                StringComparison.OrdinalIgnoreCase);
            return nameComparison != 0
                ? nameComparison
                : string.Compare(
                    left?.PackageName,
                    right?.PackageName,
                    StringComparison.Ordinal);
        }
    }

    internal sealed class GitSubmoduleManagerSetupSessionCache
    {
        internal const int MaximumPayloadByteCount = 32 * 1024;

        private const int SchemaVersion = 1;
        private const int MaximumJsonDepth = 8;
        private const int MaximumStructuralTokenCount = 64;
        private const long LifetimeMilliseconds = 30L * 1000L;
        private const string KeyPrefix =
            "MartinCalander.GitSubmoduleManager.SetupStatus.v1.";

        private readonly ISessionStringStateStore store;
        private readonly string projectFingerprint;
        private readonly string unityVersion;

        internal GitSubmoduleManagerSetupSessionCache(
            ISessionStringStateStore store,
            string projectFingerprint,
            string unityVersion)
        {
            this.store = store;
            this.projectFingerprint = projectFingerprint ?? string.Empty;
            this.unityVersion = unityVersion ?? string.Empty;
        }

        internal static GitSubmoduleManagerSetupSessionCache CreateDefault()
        {
            string fingerprint = GitUtility.GetRepositoryLocationFingerprint(
                GitUtility.ProjectRoot);
            string keySuffix = fingerprint
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');
            return new GitSubmoduleManagerSetupSessionCache(
                new UnitySessionStringStateStore(KeyPrefix + keySuffix),
                fingerprint,
                Application.unityVersion);
        }

        internal bool TryLoad(
            double currentTime,
            out GitSubmoduleManagerSetupSnapshot snapshot,
            out double completedAt)
        {
            snapshot = null;
            completedAt = double.NegativeInfinity;
            string json;
            try
            {
                json = store?.Load() ?? string.Empty;
            }
            catch
            {
                return false;
            }

            if (string.IsNullOrEmpty(json))
                return false;

            if (!TryDecode(json, currentTime, out snapshot, out completedAt))
            {
                Clear();
                return false;
            }

            return true;
        }

        internal bool Save(
            GitSubmoduleManagerSetupSnapshot snapshot,
            double completedAt)
        {
            if (!IsCacheable(snapshot) ||
                !TryNormalizeCompletedAt(completedAt, out long completedAtMilliseconds) ||
                string.IsNullOrWhiteSpace(projectFingerprint) ||
                string.IsNullOrWhiteSpace(unityVersion))
            {
                Clear();
                return false;
            }

            string gitVersion = SanitizeVersion(snapshot.GitVersion);
            string gitHubCliVersion = SanitizeVersion(snapshot.GitHubCliVersion);
            if (string.IsNullOrEmpty(gitVersion) ||
                string.IsNullOrEmpty(gitHubCliVersion))
            {
                Clear();
                return false;
            }

            var root = new JObject
            {
                ["schemaVersion"] = SchemaVersion,
                ["projectFingerprint"] = projectFingerprint,
                ["unityVersion"] = unityVersion,
                ["verifiedAtEditorUptimeMilliseconds"] = completedAtMilliseconds,
                ["gitVersion"] = gitVersion,
                ["githubCliVersion"] = gitHubCliVersion,
                ["githubAuthenticated"] = true
            };
            string json = root.ToString(Formatting.None);
            if (!StrictSessionCacheJson.TryParseObject(
                    json,
                    MaximumPayloadByteCount,
                    MaximumJsonDepth,
                    out _,
                    MaximumStructuralTokenCount))
            {
                Clear();
                return false;
            }

            try
            {
                store?.Save(json);
                return store != null;
            }
            catch
            {
                Clear();
                return false;
            }
        }

        internal void Clear()
        {
            try
            {
                store?.Clear();
            }
            catch
            {
                // Setup status caching is optional presentation state.
            }
        }

        private bool TryDecode(
            string json,
            double currentTime,
            out GitSubmoduleManagerSetupSnapshot snapshot,
            out double completedAt)
        {
            snapshot = null;
            completedAt = double.NegativeInfinity;
            if (!TryNormalizeCompletedAt(currentTime, out long currentMilliseconds) ||
                !StrictSessionCacheJson.TryParseObject(
                    json,
                    MaximumPayloadByteCount,
                    MaximumJsonDepth,
                    out JObject root,
                    MaximumStructuralTokenCount) ||
                !StrictSessionCacheJson.HasExactProperties(
                    root,
                    "schemaVersion",
                    "projectFingerprint",
                    "unityVersion",
                    "verifiedAtEditorUptimeMilliseconds",
                    "gitVersion",
                    "githubCliVersion",
                    "githubAuthenticated") ||
                !StrictSessionCacheJson.TryReadInt64(
                    root,
                    "schemaVersion",
                    out long schemaVersion) ||
                schemaVersion != SchemaVersion ||
                !StrictSessionCacheJson.TryReadString(
                    root,
                    "projectFingerprint",
                    64,
                    false,
                    out string storedProjectFingerprint) ||
                !string.Equals(
                    storedProjectFingerprint,
                    projectFingerprint,
                    StringComparison.Ordinal) ||
                !StrictSessionCacheJson.TryReadString(
                    root,
                    "unityVersion",
                    64,
                    false,
                    out string storedUnityVersion) ||
                !string.Equals(
                    storedUnityVersion,
                    unityVersion,
                    StringComparison.Ordinal) ||
                !StrictSessionCacheJson.TryReadInt64(
                    root,
                    "verifiedAtEditorUptimeMilliseconds",
                    out long completedAtMilliseconds) ||
                completedAtMilliseconds < 0 ||
                completedAtMilliseconds > long.MaxValue - LifetimeMilliseconds ||
                completedAtMilliseconds > currentMilliseconds ||
                currentMilliseconds >= completedAtMilliseconds + LifetimeMilliseconds ||
                !StrictSessionCacheJson.TryReadString(
                    root,
                    "gitVersion",
                    4096,
                    false,
                    out string gitVersion) ||
                !StrictSessionCacheJson.TryReadString(
                    root,
                    "githubCliVersion",
                    4096,
                    false,
                    out string gitHubCliVersion) ||
                !StrictSessionCacheJson.TryReadBoolean(
                    root,
                    "githubAuthenticated",
                    out bool authenticated) ||
                !authenticated ||
                !string.Equals(gitVersion, SanitizeVersion(gitVersion), StringComparison.Ordinal) ||
                !string.Equals(
                    gitHubCliVersion,
                    SanitizeVersion(gitHubCliVersion),
                    StringComparison.Ordinal))
            {
                return false;
            }

            completedAt = completedAtMilliseconds / 1000d;
            snapshot = new GitSubmoduleManagerSetupSnapshot(
                false,
                true,
                gitVersion,
                string.Empty,
                true,
                gitHubCliVersion,
                string.Empty,
                GitHubAuthenticationProbeStatus.Authenticated,
                string.Empty,
                false);
            return true;
        }

        private static bool IsCacheable(GitSubmoduleManagerSetupSnapshot snapshot)
        {
            return snapshot != null &&
                   !snapshot.IsChecking &&
                   snapshot.GitAvailable &&
                   snapshot.GitHubCliAvailable &&
                   snapshot.GitHubAuthenticated &&
                   !snapshot.GitHubProbeDeferred &&
                   string.IsNullOrEmpty(snapshot.GitError) &&
                   string.IsNullOrEmpty(snapshot.GitHubCliError) &&
                   string.IsNullOrEmpty(snapshot.GitHubAuthenticationError);
        }

        private static string SanitizeVersion(string value)
        {
            string sanitized = GitHubUtility.SanitizeUiDiagnostic(
                GitUtility.RedactCredentials(value ?? string.Empty)).Trim();
            return sanitized.Length <= 4096 ? sanitized : string.Empty;
        }

        private static bool TryNormalizeCompletedAt(
            double value,
            out long milliseconds)
        {
            milliseconds = 0L;
            if (double.IsNaN(value) || double.IsInfinity(value) || value < 0d ||
                value > (long.MaxValue - LifetimeMilliseconds) / 1000d)
            {
                return false;
            }

            milliseconds = (long)Math.Floor(value * 1000d);
            return true;
        }
    }
}
