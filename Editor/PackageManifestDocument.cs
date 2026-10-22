using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace MartinCalander.GitSubmoduleManager.Editor
{
    // A small strict JSON reader retains source spans so removing one dependency preserves other settings.
    internal sealed class PackageManifestDocument
    {
        private sealed class Node
        {
            internal int End;
            internal object Value;
            internal List<Property> Properties;
        }

        private sealed class Property
        {
            internal string Name;
            internal int Start;
            internal Node Value;
        }

        internal const int MaximumBytes = 1024 * 1024;
        private readonly string text;
        private readonly Node root;
        private int position;
        internal string Source => text;
        private const string SemVer = @"\A(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-(?:(?:0|[1-9][0-9]*|[0-9]*[a-zA-Z-][0-9a-zA-Z-]*))(?:\.(?:0|[1-9][0-9]*|[0-9]*[a-zA-Z-][0-9a-zA-Z-]*))*)?(?:\+[0-9a-zA-Z-]+(?:\.[0-9a-zA-Z-]+)*)?\z";

        internal PackageManifestDocument(string text)
        {
            this.text = text ?? throw new ArgumentNullException(nameof(text));
            if (text.Length == 0 || text.Length > MaximumBytes)
                throw new InvalidDataException("The package manifest is empty or exceeds 1 MiB.");
            position = text[0] == '\uFEFF' ? 1 : 0;
            root = ReadValue(0);
            SkipWhitespace();
            if (position != text.Length || !(root.Value is Dictionary<string, Node>))
                throw Invalid();
        }

        internal static PackageManifestDocument Read(string path) => new(ReadText(path));

        internal static string ReadText(string path, bool allowEmpty = false)
        {
            if (!File.Exists(path) || (File.GetAttributes(path) & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
                throw new InvalidDataException("A regular text file is required: " + path);
            long length = new FileInfo(path).Length;
            if (length == 0 && allowEmpty)
                return string.Empty;
            if (length <= 0 || length > MaximumBytes)
                throw new InvalidDataException("The file is empty or exceeds 1 MiB: " + path);
            // Read a bounded byte stream instead of allowing StreamReader to grow after the size check
            // or silently accept a UTF-16 BOM in a file that Unity expects to be UTF-8.
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var bytes = new MemoryStream((int)length))
            {
                var buffer = new byte[4096];
                int count;
                while ((count = stream.Read(buffer, 0, buffer.Length)) != 0)
                {
                    if (bytes.Length + count > MaximumBytes)
                        throw new InvalidDataException("The file exceeds 1 MiB: " + path);
                    bytes.Write(buffer, 0, count);
                }
                if (bytes.Length == 0 && !allowEmpty)
                    throw new InvalidDataException("The file is empty: " + path);
                return new UTF8Encoding(false, true).GetString(bytes.GetBuffer(), 0, (int)bytes.Length);
            }
        }

        internal string PackageName => String((Dictionary<string, Node>)root.Value, "name");

        internal static bool ValidPackageName(string name) => name != null && name.Length <= 214 &&
            Regex.IsMatch(name, @"\A[a-z][a-z0-9-]*(?:\.[a-z0-9][a-z0-9-]*)+\z");

        internal string ValidatePackage()
        {
            var values = (Dictionary<string, Node>)root.Value;
            string name = String(values, "name");
            if (!ValidPackageName(name))
                throw new InvalidDataException("package.json must declare a lowercase reverse-domain package name, such as com.example.package.");
            string version = String(values, "version");
            if (version == null || !Regex.IsMatch(version, SemVer))
                throw new InvalidDataException("package.json must declare a valid semantic version, such as 1.0.0.");
            Dictionary<string, Node> dependencies = Dependencies();
            if (dependencies != null)
                foreach (Node dependency in dependencies.Values)
                    if (!Regex.IsMatch((string)dependency.Value, SemVer))
                        throw new InvalidDataException("Package dependencies must declare semantic versions. Git and local dependency URLs belong in the project manifest.");
            return name;
        }

        internal bool HasDependency(string name) => Dependencies()?.ContainsKey(name) == true;

        internal string WithoutDependency(string name)
        {
            var values = (Dictionary<string, Node>)root.Value;
            Dictionary<string, Node> dependencies = Dependencies();
            if (dependencies == null || !dependencies.ContainsKey(name))
                return text;
            Node node = values["dependencies"];
            int index = node.Properties.FindIndex(item => item.Name == name);
            Property property = node.Properties[index];
            int start = property.Start;
            int end = property.Value.End;
            if (index + 1 < node.Properties.Count)
                end = node.Properties[index + 1].Start;
            else if (index > 0)
                start = node.Properties[index - 1].Value.End;
            return text.Remove(start, end - start);
        }

        private Dictionary<string, Node> Dependencies()
        {
            var values = (Dictionary<string, Node>)root.Value;
            if (!values.TryGetValue("dependencies", out Node node))
                return null;
            if (!(node.Value is Dictionary<string, Node> dependencies))
                throw new InvalidDataException("The manifest dependencies field must be an object.");
            foreach (var dependency in dependencies)
                if (!ValidPackageName(dependency.Key) || !(dependency.Value.Value is string version) || string.IsNullOrWhiteSpace(version))
                    throw new InvalidDataException("Every manifest dependency must have a valid package name and a nonempty string value.");
            return dependencies;
        }

        private static string String(Dictionary<string, Node> values, string key) =>
            values.TryGetValue(key, out Node value) ? value.Value as string : null;

        private Node ReadValue(int depth)
        {
            if (depth > 64)
                throw Invalid();
            SkipWhitespace();
            var node = new Node();
            if (position >= text.Length)
                throw Invalid();
            char token = text[position];
            if (token == '{')
            {
                position++;
                var values = new Dictionary<string, Node>(StringComparer.Ordinal);
                node.Value = values;
                node.Properties = new List<Property>();
                SkipWhitespace();
                if (!Take('}'))
                {
                    do
                    {
                        SkipWhitespace();
                        int start = position;
                        string key = ReadString();
                        SkipWhitespace();
                        if (!Take(':') || values.ContainsKey(key))
                            throw Invalid();
                        Node value = ReadValue(depth + 1);
                        values.Add(key, value);
                        node.Properties.Add(new Property { Name = key, Start = start, Value = value });
                        SkipWhitespace();
                        if (Take('}'))
                            break;
                        if (!Take(','))
                            throw Invalid();
                    } while (true);
                }
            }
            else if (token == '[')
            {
                position++;
                var values = new List<Node>();
                node.Value = values;
                SkipWhitespace();
                if (!Take(']'))
                {
                    do
                    {
                        values.Add(ReadValue(depth + 1));
                        SkipWhitespace();
                        if (Take(']'))
                            break;
                        if (!Take(','))
                            throw Invalid();
                    } while (true);
                }
            }
            else if (token == '"')
                node.Value = ReadString();
            else
            {
                int start = position;
                while (position < text.Length && text[position] != ',' && text[position] != '}' && text[position] != ']' && !char.IsWhiteSpace(text[position]))
                    position++;
                string literal = text.Substring(start, position - start);
                if (literal == "true" || literal == "false")
                    node.Value = literal == "true";
                else if (literal != "null")
                {
                    if (!Regex.IsMatch(literal, @"\A-?(?:0|[1-9][0-9]*)(?:\.[0-9]+)?(?:[eE][+-]?[0-9]+)?\z"))
                        throw Invalid();
                    node.Value = new object();
                }
            }
            node.End = position;
            return node;
        }

        private string ReadString()
        {
            if (!Take('"'))
                throw Invalid();
            var value = new StringBuilder();
            while (position < text.Length)
            {
                char character = text[position++];
                if (character == '"')
                    return value.ToString();
                if (character < 32)
                    throw Invalid();
                if (character != '\\')
                {
                    value.Append(character);
                    continue;
                }
                if (position >= text.Length)
                    throw Invalid();
                char escape = text[position++];
                switch (escape)
                {
                    case '"': case '\\': case '/': value.Append(escape); break;
                    case 'b': value.Append('\b'); break;
                    case 'f': value.Append('\f'); break;
                    case 'n': value.Append('\n'); break;
                    case 'r': value.Append('\r'); break;
                    case 't': value.Append('\t'); break;
                    case 'u':
                        if (position + 4 > text.Length || !ushort.TryParse(text.Substring(position, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out ushort code))
                            throw Invalid();
                        value.Append((char)code);
                        position += 4;
                        break;
                    default: throw Invalid();
                }
            }
            throw Invalid();
        }

        private bool Take(char character)
        {
            if (position >= text.Length || text[position] != character)
                return false;
            position++;
            return true;
        }

        private void SkipWhitespace()
        {
            while (position < text.Length && (text[position] == ' ' || text[position] == '\t' || text[position] == '\r' || text[position] == '\n'))
                position++;
        }

        private static InvalidDataException Invalid() => new("The manifest must contain valid JSON without duplicate fields.");
    }
}
