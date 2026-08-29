using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace MartinCalander.GitSubmoduleManager.Installer
{
    internal enum StrictJsonKind
    {
        Object,
        Array,
        String,
        Number,
        True,
        False,
        Null
    }

    internal sealed class StrictJsonProperty
    {
        internal StrictJsonProperty(string name, int keyStart, StrictJsonValue value)
        {
            Name = name;
            KeyStart = keyStart;
            Value = value;
        }

        internal string Name { get; }
        internal int KeyStart { get; }
        internal StrictJsonValue Value { get; }
    }

    internal sealed class StrictJsonValue
    {
        internal StrictJsonValue(StrictJsonKind kind, int start)
        {
            Kind = kind;
            Start = start;
            Properties = new List<StrictJsonProperty>();
            Items = new List<StrictJsonValue>();
        }

        internal StrictJsonKind Kind { get; }
        internal int Start { get; }
        internal int End { get; set; }
        internal string StringValue { get; set; }
        internal List<StrictJsonProperty> Properties { get; }
        internal List<StrictJsonValue> Items { get; }

        internal StrictJsonProperty FindProperty(string name)
        {
            foreach (StrictJsonProperty property in Properties)
            {
                if (string.Equals(property.Name, name, StringComparison.Ordinal))
                    return property;
            }

            return null;
        }
    }

    /// <summary>
    /// A small, bounded JSON parser used before the package and its Newtonsoft
    /// dependency exist. It rejects duplicate keys and malformed Unicode.
    /// </summary>
    internal sealed class StrictJsonParser
    {
        private const int MaximumDepth = 32;
        private const int MaximumNodeCount = 8192;

        private readonly string text;
        private int index;
        private int nodeCount;

        private StrictJsonParser(string text)
        {
            this.text = text ?? string.Empty;
        }

        internal static StrictJsonValue ParseRootObject(string text)
        {
            var parser = new StrictJsonParser(text);
            parser.SkipWhitespace();
            StrictJsonValue value = parser.ParseValue(0);
            parser.SkipWhitespace();
            if (parser.index != parser.text.Length)
                parser.Fail("JSON contains content after the root value.");
            if (value.Kind != StrictJsonKind.Object)
                parser.Fail("The JSON root must be an object.");
            return value;
        }

        private StrictJsonValue ParseValue(int depth)
        {
            if (depth > MaximumDepth)
                Fail("JSON exceeds the maximum nesting depth of 32.");
            if (++nodeCount > MaximumNodeCount)
                Fail("JSON contains too many values.");
            if (index >= text.Length)
                Fail("JSON ended before a value was complete.");

            switch (text[index])
            {
                case '{':
                    return ParseObject(depth);
                case '[':
                    return ParseArray(depth);
                case '"':
                    return ParseString();
                case 't':
                    return ParseLiteral("true", StrictJsonKind.True);
                case 'f':
                    return ParseLiteral("false", StrictJsonKind.False);
                case 'n':
                    return ParseLiteral("null", StrictJsonKind.Null);
                default:
                    if (text[index] == '-' || IsDigit(text[index]))
                        return ParseNumber();
                    Fail("JSON contains an unexpected token.");
                    return null;
            }
        }

        private StrictJsonValue ParseObject(int depth)
        {
            int start = index++;
            var value = new StrictJsonValue(StrictJsonKind.Object, start);
            var names = new HashSet<string>(StringComparer.Ordinal);
            SkipWhitespace();
            if (Consume('}'))
            {
                value.End = index;
                return value;
            }

            while (true)
            {
                SkipWhitespace();
                if (index >= text.Length || text[index] != '"')
                    Fail("An object property name must be a JSON string.");

                StrictJsonValue key = ParseString();
                if (!names.Add(key.StringValue))
                    Fail("JSON contains the duplicate object key '" + key.StringValue + "'.");

                SkipWhitespace();
                Require(':', "An object property name must be followed by ':'.");
                SkipWhitespace();
                StrictJsonValue propertyValue = ParseValue(depth + 1);
                value.Properties.Add(
                    new StrictJsonProperty(key.StringValue, key.Start, propertyValue));

                SkipWhitespace();
                if (Consume('}'))
                {
                    value.End = index;
                    return value;
                }

                Require(',', "Object properties must be separated by ','.");
            }
        }

        private StrictJsonValue ParseArray(int depth)
        {
            int start = index++;
            var value = new StrictJsonValue(StrictJsonKind.Array, start);
            SkipWhitespace();
            if (Consume(']'))
            {
                value.End = index;
                return value;
            }

            while (true)
            {
                SkipWhitespace();
                value.Items.Add(ParseValue(depth + 1));
                SkipWhitespace();
                if (Consume(']'))
                {
                    value.End = index;
                    return value;
                }

                Require(',', "Array values must be separated by ','.");
            }
        }

        private StrictJsonValue ParseString()
        {
            int start = index++;
            var builder = new StringBuilder();
            while (index < text.Length)
            {
                char character = text[index++];
                if (character == '"')
                {
                    return new StrictJsonValue(StrictJsonKind.String, start)
                    {
                        End = index,
                        StringValue = builder.ToString()
                    };
                }

                if (character < 0x20)
                    Fail("A JSON string contains an unescaped control character.");

                if (character == '\\')
                {
                    if (index >= text.Length)
                        Fail("A JSON string ends with an incomplete escape.");
                    char escape = text[index++];
                    switch (escape)
                    {
                        case '"': builder.Append('"'); break;
                        case '\\': builder.Append('\\'); break;
                        case '/': builder.Append('/'); break;
                        case 'b': builder.Append('\b'); break;
                        case 'f': builder.Append('\f'); break;
                        case 'n': builder.Append('\n'); break;
                        case 'r': builder.Append('\r'); break;
                        case 't': builder.Append('\t'); break;
                        case 'u': AppendEscapedUnicode(builder); break;
                        default: Fail("A JSON string contains an invalid escape."); break;
                    }

                    continue;
                }

                if (char.IsHighSurrogate(character))
                {
                    if (index >= text.Length || !char.IsLowSurrogate(text[index]))
                        Fail("A JSON string contains an unpaired high surrogate.");
                    builder.Append(character);
                    builder.Append(text[index++]);
                    continue;
                }

                if (char.IsLowSurrogate(character))
                    Fail("A JSON string contains an unpaired low surrogate.");
                builder.Append(character);
            }

            Fail("A JSON string is not terminated.");
            return null;
        }

        private void AppendEscapedUnicode(StringBuilder builder)
        {
            char first = ReadHexCharacter();
            if (char.IsHighSurrogate(first))
            {
                if (index + 1 >= text.Length || text[index] != '\\' || text[index + 1] != 'u')
                    Fail("A JSON Unicode escape contains an unpaired high surrogate.");
                index += 2;
                char second = ReadHexCharacter();
                if (!char.IsLowSurrogate(second))
                    Fail("A JSON Unicode escape contains an invalid surrogate pair.");
                builder.Append(first);
                builder.Append(second);
                return;
            }

            if (char.IsLowSurrogate(first))
                Fail("A JSON Unicode escape contains an unpaired low surrogate.");
            builder.Append(first);
        }

        private char ReadHexCharacter()
        {
            if (index + 4 > text.Length)
                Fail("A JSON Unicode escape is incomplete.");
            int value = 0;
            for (int offset = 0; offset < 4; offset++)
            {
                int digit = HexValue(text[index++]);
                if (digit < 0)
                    Fail("A JSON Unicode escape contains a non-hexadecimal character.");
                value = (value << 4) | digit;
            }

            return (char)value;
        }

        private StrictJsonValue ParseNumber()
        {
            int start = index;
            Consume('-');
            if (Consume('0'))
            {
                if (index < text.Length && IsDigit(text[index]))
                    Fail("A JSON number cannot contain a leading zero.");
            }
            else
            {
                RequireDigit("A JSON number requires an integer part.");
                while (index < text.Length && IsDigit(text[index]))
                    index++;
            }

            if (Consume('.'))
            {
                RequireDigit("A JSON fraction requires at least one digit.");
                while (index < text.Length && IsDigit(text[index]))
                    index++;
            }

            if (index < text.Length && (text[index] == 'e' || text[index] == 'E'))
            {
                index++;
                if (index < text.Length && (text[index] == '+' || text[index] == '-'))
                    index++;
                RequireDigit("A JSON exponent requires at least one digit.");
                while (index < text.Length && IsDigit(text[index]))
                    index++;
            }

            return new StrictJsonValue(StrictJsonKind.Number, start) { End = index };
        }

        private StrictJsonValue ParseLiteral(string literal, StrictJsonKind kind)
        {
            int start = index;
            if (index + literal.Length > text.Length ||
                !string.Equals(
                    text.Substring(index, literal.Length),
                    literal,
                    StringComparison.Ordinal))
            {
                Fail("JSON contains an invalid literal.");
            }

            index += literal.Length;
            return new StrictJsonValue(kind, start) { End = index };
        }

        private void SkipWhitespace()
        {
            while (index < text.Length)
            {
                char character = text[index];
                if (character != ' ' && character != '\t' &&
                    character != '\r' && character != '\n')
                {
                    return;
                }

                index++;
            }
        }

        private bool Consume(char expected)
        {
            if (index >= text.Length || text[index] != expected)
                return false;
            index++;
            return true;
        }

        private void Require(char expected, string message)
        {
            if (!Consume(expected))
                Fail(message);
        }

        private void RequireDigit(string message)
        {
            if (index >= text.Length || !IsDigit(text[index]))
                Fail(message);
        }

        private static bool IsDigit(char value)
        {
            return value >= '0' && value <= '9';
        }

        private static int HexValue(char value)
        {
            if (value >= '0' && value <= '9')
                return value - '0';
            if (value >= 'a' && value <= 'f')
                return value - 'a' + 10;
            if (value >= 'A' && value <= 'F')
                return value - 'A' + 10;
            return -1;
        }

        private void Fail(string message)
        {
            throw new FormatException(
                string.Format(CultureInfo.InvariantCulture, "{0} Position {1}.", message, index));
        }
    }
}
