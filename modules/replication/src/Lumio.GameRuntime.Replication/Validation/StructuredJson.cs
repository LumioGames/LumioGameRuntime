using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Lumio.Gen.CanonicalSerializer;

namespace Lumio.GameRuntime.Replication.Validation;

internal enum StructuredJsonKind
{
    Object,
    Array,
    String,
    Number,
    True,
    False,
    Null
}

internal readonly struct StructuredJsonProperty
{
    internal StructuredJsonProperty(string name, StructuredJsonValue value)
    {
        Name = name;
        Value = value;
    }

    internal string Name { get; }
    internal StructuredJsonValue Value { get; }
}

internal sealed class StructuredJsonValue
{
    private StructuredJsonValue(
        StructuredJsonKind kind,
        string? text,
        IReadOnlyList<StructuredJsonProperty>? properties,
        IReadOnlyList<StructuredJsonValue>? items)
    {
        Kind = kind;
        Text = text;
        Properties = properties;
        Items = items;
    }

    internal StructuredJsonKind Kind { get; }
    internal string? Text { get; }
    internal IReadOnlyList<StructuredJsonProperty>? Properties { get; }
    internal IReadOnlyList<StructuredJsonValue>? Items { get; }

    internal static StructuredJsonValue Object(IReadOnlyList<StructuredJsonProperty> properties) =>
        new(StructuredJsonKind.Object, null, properties, null);

    internal static StructuredJsonValue Array(IReadOnlyList<StructuredJsonValue> items) =>
        new(StructuredJsonKind.Array, null, null, items);

    internal static StructuredJsonValue String(string value) =>
        new(StructuredJsonKind.String, value, null, null);

    internal static StructuredJsonValue Number(string value) =>
        new(StructuredJsonKind.Number, value, null, null);

    internal static StructuredJsonValue Boolean(bool value) =>
        new(value ? StructuredJsonKind.True : StructuredJsonKind.False, null, null, null);

    internal static StructuredJsonValue Null() =>
        new(StructuredJsonKind.Null, null, null, null);

    internal bool TryGetProperty(string name, out StructuredJsonValue? value)
    {
        if (Properties is not null)
        {
            for (var index = 0; index < Properties.Count; index++)
            {
                if (Properties[index].Name == name)
                {
                    value = Properties[index].Value;
                    return true;
                }
            }
        }

        value = null;
        return false;
    }

    internal bool TryGetUInt64(out ulong value)
    {
        value = 0;
        return Kind == StructuredJsonKind.Number &&
            ulong.TryParse(Text, NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }
}

/// <summary>CanonicalJsonV1 writer shared by every production target.</summary>
internal static class StructuredJsonCanonicalizer
{
    private const string Hex = "0123456789abcdef";

    internal static bool TryCanonicalize(StructuredJsonValue value, out string canonical)
    {
        if (!string.Equals(CanonicalForm.FormId, "CanonicalJsonV1", StringComparison.Ordinal) ||
            !string.Equals(CanonicalForm.Encoding, "AsciiEscaped", StringComparison.Ordinal) ||
            !string.Equals(CanonicalForm.MemberOrder, "CodePointAscending", StringComparison.Ordinal) ||
            !string.Equals(CanonicalForm.ArrayOrder, "DocumentOrder", StringComparison.Ordinal) ||
            !string.Equals(CanonicalForm.Numbers, "IntegerOnly", StringComparison.Ordinal))
        {
            canonical = string.Empty;
            return false;
        }

        var builder = new StringBuilder();
        if (!TryWrite(value, builder))
        {
            canonical = string.Empty;
            return false;
        }

        canonical = builder.ToString();
        return true;
    }

    private static bool TryWrite(StructuredJsonValue value, StringBuilder builder)
    {
        switch (value.Kind)
        {
            case StructuredJsonKind.Object:
                if (value.Properties is null) return false;
                var properties = new List<StructuredJsonProperty>(value.Properties);
                properties.Sort((left, right) => StringComparer.Ordinal.Compare(left.Name, right.Name));
                builder.Append('{');
                var names = new HashSet<string>(StringComparer.Ordinal);
                for (var index = 0; index < properties.Count; index++)
                {
                    StructuredJsonProperty property = properties[index];
                    if (!names.Add(property.Name)) return false;
                    if (index > 0) builder.Append(CanonicalForm.ItemSeparator);
                    WriteString(property.Name, builder);
                    builder.Append(CanonicalForm.KeyValueSeparator);
                    if (!TryWrite(property.Value, builder)) return false;
                }

                builder.Append('}');
                return true;

            case StructuredJsonKind.Array:
                if (value.Items is null) return false;
                builder.Append('[');
                for (var index = 0; index < value.Items.Count; index++)
                {
                    if (index > 0) builder.Append(CanonicalForm.ItemSeparator);
                    if (!TryWrite(value.Items[index], builder)) return false;
                }

                builder.Append(']');
                return true;

            case StructuredJsonKind.String:
                if (value.Text is null) return false;
                WriteString(value.Text, builder);
                return true;

            case StructuredJsonKind.Number:
                if (!TryNormalizeInteger(value.Text, out string? normalized)) return false;
                builder.Append(normalized);
                return true;

            case StructuredJsonKind.True:
                builder.Append("true");
                return true;

            case StructuredJsonKind.False:
                builder.Append("false");
                return true;

            case StructuredJsonKind.Null:
                builder.Append("null");
                return true;

            default:
                return false;
        }
    }

    private static bool TryNormalizeInteger(string? text, out string? normalized)
    {
        normalized = null;
        if (string.IsNullOrEmpty(text)) return false;
        var start = text[0] == '-' ? 1 : 0;
        if (start == text.Length) return false;
        for (var index = start; index < text.Length; index++)
            if (text[index] is < '0' or > '9') return false;

        while (start < text.Length - 1 && text[start] == '0') start++;
        if (start == text.Length - 1 && text[start] == '0')
        {
            normalized = "0";
            return true;
        }

        var builder = new StringBuilder(text.Length - start + (text[0] == '-' ? 1 : 0));
        if (text[0] == '-') builder.Append('-');
        builder.Append(text, start, text.Length - start);
        normalized = builder.ToString();
        return true;
    }

    private static void WriteString(string value, StringBuilder builder)
    {
        builder.Append('"');
        foreach (char item in value)
        {
            switch (item)
            {
                case '"': builder.Append("\\\""); break;
                case '\\': builder.Append("\\\\"); break;
                case '\b': builder.Append("\\b"); break;
                case '\f': builder.Append("\\f"); break;
                case '\n': builder.Append("\\n"); break;
                case '\r': builder.Append("\\r"); break;
                case '\t': builder.Append("\\t"); break;
                default:
                    if (item < 0x20 || item > 0x7e)
                    {
                        builder.Append("\\u");
                        builder.Append(Hex[(item >> 12) & 0xf]);
                        builder.Append(Hex[(item >> 8) & 0xf]);
                        builder.Append(Hex[(item >> 4) & 0xf]);
                        builder.Append(Hex[item & 0xf]);
                    }
                    else
                    {
                        builder.Append(item);
                    }

                    break;
            }
        }

        builder.Append('"');
    }
}

internal static class StructuredJsonParser
{
    private const int MaxDepth = 128;

    internal static bool TryParse(string? text, out StructuredJsonValue? value)
    {
        value = null;
        if (text is null) return false;
        var parser = new Parser(text);
        return parser.TryParse(out value);
    }

    private sealed class Parser
    {
        private readonly string _text;
        private int _position;

        internal Parser(string text) => _text = text;

        internal bool TryParse(out StructuredJsonValue? value)
        {
            value = null;
            SkipWhitespace();
            if (!TryParseValue(0, out value)) return false;
            SkipWhitespace();
            return _position == _text.Length;
        }

        private bool TryParseValue(int depth, out StructuredJsonValue? value)
        {
            value = null;
            if (depth > MaxDepth || _position >= _text.Length) return false;
            char current = _text[_position];
            if (current == '{') return TryParseObject(depth, out value);
            if (current == '[') return TryParseArray(depth, out value);
            if (current == '"')
            {
                return TryParseString(out string? text) &&
                    (value = StructuredJsonValue.String(text!)) is not null;
            }

            if (current == 't' && TryConsume("true"))
            {
                value = StructuredJsonValue.Boolean(true);
                return true;
            }

            if (current == 'f' && TryConsume("false"))
            {
                value = StructuredJsonValue.Boolean(false);
                return true;
            }

            if (current == 'n' && TryConsume("null"))
            {
                value = StructuredJsonValue.Null();
                return true;
            }

            if (current == '-' || (current >= '0' && current <= '9'))
                return TryParseNumber(out value);
            return false;
        }

        private bool TryParseObject(int depth, out StructuredJsonValue? value)
        {
            value = null;
            _position++;
            SkipWhitespace();
            var properties = new List<StructuredJsonProperty>();
            if (TryConsume('}'))
            {
                value = StructuredJsonValue.Object(properties);
                return true;
            }

            while (true)
            {
                SkipWhitespace();
                if (!TryParseString(out string? name)) return false;
                SkipWhitespace();
                if (!TryConsume(':')) return false;
                SkipWhitespace();
                if (!TryParseValue(depth + 1, out StructuredJsonValue? propertyValue)) return false;
                properties.Add(new StructuredJsonProperty(name!, propertyValue!));
                SkipWhitespace();
                if (TryConsume('}'))
                {
                    value = StructuredJsonValue.Object(properties);
                    return true;
                }

                if (!TryConsume(',')) return false;
                SkipWhitespace();
                if (_position >= _text.Length || _text[_position] == '}') return false;
            }
        }

        private bool TryParseArray(int depth, out StructuredJsonValue? value)
        {
            value = null;
            _position++;
            SkipWhitespace();
            var items = new List<StructuredJsonValue>();
            if (TryConsume(']'))
            {
                value = StructuredJsonValue.Array(items);
                return true;
            }

            while (true)
            {
                SkipWhitespace();
                if (!TryParseValue(depth + 1, out StructuredJsonValue? item)) return false;
                items.Add(item!);
                SkipWhitespace();
                if (TryConsume(']'))
                {
                    value = StructuredJsonValue.Array(items);
                    return true;
                }

                if (!TryConsume(',')) return false;
                SkipWhitespace();
                if (_position >= _text.Length || _text[_position] == ']') return false;
            }
        }

        private bool TryParseString(out string? value)
        {
            value = null;
            if (!TryConsume('"')) return false;
            var builder = new StringBuilder();
            while (_position < _text.Length)
            {
                char current = _text[_position++];
                if (current == '"')
                {
                    value = builder.ToString();
                    return true;
                }

                if (current < 0x20) return false;
                if (current != '\\')
                {
                    if (char.IsHighSurrogate(current))
                    {
                        if (_position >= _text.Length || !char.IsLowSurrogate(_text[_position])) return false;
                        builder.Append(current);
                        builder.Append(_text[_position++]);
                        continue;
                    }
                    if (char.IsLowSurrogate(current)) return false;
                    builder.Append(current);
                    continue;
                }

                if (_position >= _text.Length) return false;
                char escaped = _text[_position++];
                switch (escaped)
                {
                    case '"': builder.Append('"'); break;
                    case '\\': builder.Append('\\'); break;
                    case '/': builder.Append('/'); break;
                    case 'b': builder.Append('\b'); break;
                    case 'f': builder.Append('\f'); break;
                    case 'n': builder.Append('\n'); break;
                    case 'r': builder.Append('\r'); break;
                    case 't': builder.Append('\t'); break;
                    case 'u':
                        if (!TryParseUnicode(out char codeUnit)) return false;
                        if (char.IsHighSurrogate(codeUnit))
                        {
                            if (!TryConsume('\\') || !TryConsume('u') || !TryParseUnicode(out char low) || !char.IsLowSurrogate(low)) return false;
                            builder.Append(codeUnit);
                            builder.Append(low);
                        }
                        else if (char.IsLowSurrogate(codeUnit))
                        {
                            return false;
                        }
                        else
                        {
                            builder.Append(codeUnit);
                        }
                        break;
                    default: return false;
                }
            }

            return false;
        }

        private bool TryParseUnicode(out char value)
        {
            value = '\0';
            if (_position + 4 > _text.Length) return false;
            var parsed = 0;
            for (var index = 0; index < 4; index++)
            {
                int digit = HexValue(_text[_position++]);
                if (digit < 0) return false;
                parsed = (parsed << 4) | digit;
            }

            value = (char)parsed;
            return true;
        }

        private bool TryParseNumber(out StructuredJsonValue? value)
        {
            value = null;
            int start = _position;
            if (TryConsume('-') && _position >= _text.Length) return false;
            if (_position < _text.Length && _text[_position] == '0')
            {
                _position++;
                if (_position < _text.Length && IsDigit(_text[_position])) return false;
            }
            else
            {
                if (_position >= _text.Length || !IsNonZeroDigit(_text[_position])) return false;
                do { _position++; } while (_position < _text.Length && IsDigit(_text[_position]));
            }

            if (TryConsume('.'))
            {
                if (_position >= _text.Length || !IsDigit(_text[_position])) return false;
                do { _position++; } while (_position < _text.Length && IsDigit(_text[_position]));
            }

            if (_position < _text.Length && (_text[_position] == 'e' || _text[_position] == 'E'))
            {
                _position++;
                if (_position < _text.Length && (_text[_position] == '+' || _text[_position] == '-')) _position++;
                if (_position >= _text.Length || !IsDigit(_text[_position])) return false;
                do { _position++; } while (_position < _text.Length && IsDigit(_text[_position]));
            }

            value = StructuredJsonValue.Number(_text.Substring(start, _position - start));
            return true;
        }

        private void SkipWhitespace()
        {
            while (_position < _text.Length && _text[_position] is ' ' or '\t' or '\r' or '\n') _position++;
        }

        private bool TryConsume(char value)
        {
            if (_position >= _text.Length || _text[_position] != value) return false;
            _position++;
            return true;
        }

        private bool TryConsume(string value)
        {
            if (_position + value.Length > _text.Length ||
                !string.Equals(_text.Substring(_position, value.Length), value, StringComparison.Ordinal)) return false;
            _position += value.Length;
            return true;
        }

        private static bool IsDigit(char value) => value >= '0' && value <= '9';

        private static bool IsNonZeroDigit(char value) => value >= '1' && value <= '9';

        private static int HexValue(char value) =>
            value is >= '0' and <= '9' ? value - '0' :
            value is >= 'a' and <= 'f' ? value - 'a' + 10 :
            value is >= 'A' and <= 'F' ? value - 'A' + 10 : -1;
    }
}
