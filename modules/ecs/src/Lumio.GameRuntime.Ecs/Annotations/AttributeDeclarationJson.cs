using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Lumio.GameRuntime.Ecs.Annotations;

/// <summary>Deterministic C-2 declaration table JSON: sorted keys, LF, trailing newline.</summary>
public static class AttributeDeclarationJson
{
    /// <summary>Formats rows as a JSON array. Object keys are alphabetical; rows are sorted by attributeId.</summary>
    public static string Format(IReadOnlyList<FieldAttributeDeclaration> rows)
    {
        AnnotationGuard.NotNull(rows, nameof(rows));
        var ordered = new List<FieldAttributeDeclaration>(rows.Count);
        for (int i = 0; i < rows.Count; i++) ordered.Add(rows[i]);
        ordered.Sort(static (left, right) => string.CompareOrdinal(left.AttributeId, right.AttributeId));

        var builder = new StringBuilder();
        builder.Append("[\n");
        for (int i = 0; i < ordered.Count; i++)
        {
            FieldAttributeDeclaration row = ordered[i];
            builder.Append("  {\n");
            WriteProperty(builder, "attributeId", row.AttributeId, comma: true);
            WriteProperty(builder, "persistence", row.Persistence, comma: true);
            WriteProperty(builder, "replication", row.Replication, comma: true);
            WriteProperty(builder, "valueType", row.ValueType, comma: true);
            WriteProperty(builder, "visibility", row.Visibility, comma: false);
            builder.Append("  }");
            if (i != ordered.Count - 1) builder.Append(',');
            builder.Append('\n');
        }

        builder.Append("]\n");
        return builder.ToString();
    }

    /// <summary>Parses a table produced by <see cref="Format"/> and re-validates every row.</summary>
    public static IReadOnlyList<FieldAttributeDeclaration> Parse(string json)
    {
        AnnotationGuard.NotNull(json, nameof(json));
        var reader = new JsonReader(json);
        reader.SkipBom();
        reader.SkipWhitespace();
        reader.Expect('[');
        var rows = new List<FieldAttributeDeclaration>();
        reader.SkipWhitespace();
        if (!reader.TryConsume(']'))
        {
            while (true)
            {
                rows.Add(ReadObject(ref reader));
                reader.SkipWhitespace();
                if (reader.TryConsume(','))
                {
                    reader.SkipWhitespace();
                    continue;
                }

                reader.Expect(']');
                break;
            }
        }

        reader.SkipWhitespace();
        if (!reader.AtEnd)
            throw new InvalidOperationException("unexpected trailing JSON content");

        rows.Sort(static (left, right) => string.CompareOrdinal(left.AttributeId, right.AttributeId));
        for (int i = 0; i < rows.Count; i++)
        {
            FieldAttributeDeclaration row = rows[i];
            FieldAnnotationRules.Validate(row.AttributeId, row.Persistence, row.Replication, row.Visibility);
            if (string.IsNullOrEmpty(row.ValueType))
                throw new InvalidOperationException("valueType is required for " + row.AttributeId);
        }

        return rows;
    }

    private static void WriteProperty(StringBuilder builder, string name, string value, bool comma)
    {
        builder.Append("    \"");
        builder.Append(name);
        builder.Append("\": ");
        AppendJsonString(builder, value ?? string.Empty);
        if (comma) builder.Append(',');
        builder.Append('\n');
    }

    private static void AppendJsonString(StringBuilder builder, string value)
    {
        builder.Append('"');
        for (int i = 0; i < value.Length; i++)
        {
            char ch = value[i];
            switch (ch)
            {
                case '"': builder.Append("\\\""); break;
                case '\\': builder.Append("\\\\"); break;
                case '\n': builder.Append("\\n"); break;
                case '\r': builder.Append("\\r"); break;
                case '\t': builder.Append("\\t"); break;
                default:
                    if (ch < 0x20)
                    {
                        builder.Append("\\u");
                        builder.Append(((int)ch).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        builder.Append(ch);
                    }

                    break;
            }
        }

        builder.Append('"');
    }

    private static FieldAttributeDeclaration ReadObject(ref JsonReader reader)
    {
        reader.Expect('{');
        string? attributeId = null;
        string? valueType = null;
        string? persistence = null;
        string? replication = null;
        string? visibility = null;
        reader.SkipWhitespace();
        if (!reader.TryConsume('}'))
        {
            while (true)
            {
                string name = reader.ReadString();
                reader.SkipWhitespace();
                reader.Expect(':');
                reader.SkipWhitespace();
                string value = reader.ReadString();
                if (string.Equals(name, "attributeId", StringComparison.Ordinal)) attributeId = value;
                else if (string.Equals(name, "valueType", StringComparison.Ordinal)) valueType = value;
                else if (string.Equals(name, "persistence", StringComparison.Ordinal)) persistence = value;
                else if (string.Equals(name, "replication", StringComparison.Ordinal)) replication = value;
                else if (string.Equals(name, "visibility", StringComparison.Ordinal)) visibility = value;
                else throw new InvalidOperationException("unexpected declaration key: " + name);

                reader.SkipWhitespace();
                if (reader.TryConsume(','))
                {
                    reader.SkipWhitespace();
                    continue;
                }

                reader.Expect('}');
                break;
            }
        }

        if (attributeId is null || valueType is null || persistence is null || replication is null || visibility is null)
            throw new InvalidOperationException("declaration object is missing a required C-2 key");
        return new FieldAttributeDeclaration(attributeId, valueType, persistence, replication, visibility);
    }

    private struct JsonReader
    {
        private readonly string _text;
        private int _index;

        internal JsonReader(string text)
        {
            _text = text;
            _index = 0;
        }

        internal bool AtEnd => _index >= _text.Length;

        internal void SkipBom()
        {
            if (_index < _text.Length && _text[_index] == '\uFEFF') _index++;
        }

        internal void SkipWhitespace()
        {
            while (_index < _text.Length)
            {
                char ch = _text[_index];
                if (ch == ' ' || ch == '\t' || ch == '\n' || ch == '\r') _index++;
                else break;
            }
        }

        internal void Expect(char ch)
        {
            SkipWhitespace();
            if (_index >= _text.Length || _text[_index] != ch)
                throw new InvalidOperationException("expected '" + ch + "' at index " + _index.ToString(CultureInfo.InvariantCulture));
            _index++;
        }

        internal bool TryConsume(char ch)
        {
            if (_index < _text.Length && _text[_index] == ch)
            {
                _index++;
                return true;
            }

            return false;
        }

        internal string ReadString()
        {
            SkipWhitespace();
            Expect('"');
            var builder = new StringBuilder();
            while (_index < _text.Length)
            {
                char ch = _text[_index++];
                if (ch == '"') return builder.ToString();
                if (ch == '\\')
                {
                    if (_index >= _text.Length) throw new InvalidOperationException("unterminated JSON escape");
                    char esc = _text[_index++];
                    switch (esc)
                    {
                        case '"': builder.Append('"'); break;
                        case '\\': builder.Append('\\'); break;
                        case '/': builder.Append('/'); break;
                        case 'n': builder.Append('\n'); break;
                        case 'r': builder.Append('\r'); break;
                        case 't': builder.Append('\t'); break;
                        case 'u':
                            if (_index + 4 > _text.Length) throw new InvalidOperationException("unterminated JSON unicode escape");
                            int code = int.Parse(_text.AsSpan(_index, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                            builder.Append((char)code);
                            _index += 4;
                            break;
                        default:
                            throw new InvalidOperationException("unsupported JSON escape");
                    }
                }
                else
                {
                    builder.Append(ch);
                }
            }

            throw new InvalidOperationException("unterminated JSON string");
        }
    }
}
