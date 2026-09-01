using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Lumio.Gen.CanonicalSerializer;
using Lumio.Gen.ContractTypes;

namespace Lumio.GameRuntime.Config;

internal enum ConfigMergeStatus
{
    Merged,
    Failed,
}

internal sealed record MergedConfigRow(string Key, string CanonicalValuesJson);

internal sealed record MergedConfigTable(
    string TableId,
    IReadOnlyList<ConfigTableColumnsItem> Columns,
    IReadOnlyList<MergedConfigRow> Rows,
    string CanonicalBytesHex);

internal sealed class ConfigMergeResult
{
    private readonly Dictionary<string, SortedDictionary<string, SortedDictionary<string, CanonicalJsonValue>>>? mergedRowsByTable;

    internal ConfigMergeResult(
        ConfigMergeStatus status,
        IReadOnlyList<MergedConfigTable> tables,
        IReadOnlyList<string> errors,
        string? outputHashHex,
        Dictionary<string, SortedDictionary<string, SortedDictionary<string, CanonicalJsonValue>>>? mergedRowsByTable)
    {
        Status = status;
        Tables = tables;
        Errors = errors;
        OutputHashHex = outputHashHex;
        this.mergedRowsByTable = mergedRowsByTable;
    }

    internal ConfigMergeStatus Status { get; }

    internal IReadOnlyList<MergedConfigTable> Tables { get; }

    internal IReadOnlyList<string> Errors { get; }

    internal string? OutputHashHex { get; }

    internal string? LookupValue(string tableId, string rowKey, string column)
    {
        if (mergedRowsByTable is null ||
            !mergedRowsByTable.TryGetValue(tableId, out var rows) ||
            !rows.TryGetValue(rowKey, out var members) ||
            !members.TryGetValue(column, out var value))
        {
            return null;
        }

        return value.Kind switch
        {
            CanonicalJsonKind.Number => value.Literal,
            CanonicalJsonKind.String => value.Text,
            CanonicalJsonKind.True => "true",
            CanonicalJsonKind.False => "false",
            CanonicalJsonKind.Null => "null",
            _ => CanonicalJson.Write(value),
        };
    }
}

/// <summary>
/// Fixed six-layer merger. Precedence is a compile-time array; no custom comparer.
/// Later layers override earlier layers at key/column granularity.
/// Conflicting column declarations fail; the merger does not convert types.
/// </summary>
internal sealed class ConfigLayerMerger
{
    private static readonly ConfigLayer[] Precedence =
    {
        ConfigLayer.Engine,
        ConfigLayer.Platform,
        ConfigLayer.Server,
        ConfigLayer.Product,
        ConfigLayer.Environment,
        ConfigLayer.UserOrSession,
    };

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Performance",
        "CA1822",
        Justification = "Instance form is the module contract (worker-safe, no static global cache).")]
    public ConfigMergeResult Merge(ReadOnlySpan<ValidatedConfigLayer> layers)
    {
        var errors = new List<string>();
        if (layers.Length == 0)
        {
            errors.Add("no config layers to merge");
            return Failed(errors);
        }

        var ordered = layers.ToArray();
        Array.Sort(ordered, (left, right) => ((int)left.Layer).CompareTo((int)right.Layer));

        for (var index = 1; index < ordered.Length; index++)
        {
            if (ordered[index].Layer == ordered[index - 1].Layer)
            {
                errors.Add($"duplicate config layer {ordered[index].Layer}");
            }
        }

        var lowest = (int)Precedence[0];
        var highest = (int)Precedence[Precedence.Length - 1];
        foreach (var layer in ordered)
        {
            var value = (int)layer.Layer;
            if (value < lowest || value > highest)
            {
                errors.Add($"config layer value {value} is outside the fixed six-layer domain");
            }
        }

        if (errors.Count > 0)
        {
            return Failed(errors);
        }

        var tableOrder = new List<string>();
        var columnsByTable = new Dictionary<string, IReadOnlyList<ConfigTableColumnsItem>>(StringComparer.Ordinal);
        var signatureByTable = new Dictionary<string, string>(StringComparer.Ordinal);
        var rowsByTable = new Dictionary<string, SortedDictionary<string, SortedDictionary<string, CanonicalJsonValue>>>(StringComparer.Ordinal);

        foreach (var layer in ordered)
        {
            var seenInArtifact = new HashSet<string>(StringComparer.Ordinal);
            foreach (var table in layer.Artifact.Tables)
            {
                if (!seenInArtifact.Add(table.TableId))
                {
                    errors.Add($"duplicate tableId '{table.TableId}' in layer {layer.Layer} artifact");
                    continue;
                }

                var signature = ColumnSignature(table.Columns);
                if (!signatureByTable.TryGetValue(table.TableId, out var existing))
                {
                    tableOrder.Add(table.TableId);
                    signatureByTable[table.TableId] = signature;
                    columnsByTable[table.TableId] = table.Columns;
                    rowsByTable[table.TableId] = new SortedDictionary<string, SortedDictionary<string, CanonicalJsonValue>>(CodePointComparer.Instance);
                }
                else if (!string.Equals(existing, signature, StringComparison.Ordinal))
                {
                    errors.Add(
                        $"table '{table.TableId}' declares conflicting columns in layer {layer.Layer}; the merger performs no lenient conversion");
                    continue;
                }

                var targetRows = rowsByTable[table.TableId];
                foreach (var row in table.Rows)
                {
                    var parsed = CanonicalJson.Parse(row.Values.Json);
                    if (!parsed.IsOk || parsed.Value!.Kind != CanonicalJsonKind.Object)
                    {
                        errors.Add($"row '{row.Key}' of table '{table.TableId}' in layer {layer.Layer} is not a valid JSON object");
                        continue;
                    }

                    foreach (var duplicate in parsed.DuplicateMembers)
                    {
                        errors.Add($"duplicate JSON member '{duplicate}' in row '{row.Key}' of table '{table.TableId}' in layer {layer.Layer}");
                    }

                    if (!targetRows.TryGetValue(row.Key, out var members))
                    {
                        members = new SortedDictionary<string, CanonicalJsonValue>(CodePointComparer.Instance);
                        targetRows[row.Key] = members;
                    }

                    foreach (var member in parsed.Value.Members!)
                    {
                        members[member.Key] = member.Value;
                    }
                }
            }
        }

        if (errors.Count > 0)
        {
            return Failed(errors);
        }

        return Merged(tableOrder, columnsByTable, rowsByTable);
    }

    private static ConfigMergeResult Merged(
        List<string> tableOrder,
        Dictionary<string, IReadOnlyList<ConfigTableColumnsItem>> columnsByTable,
        Dictionary<string, SortedDictionary<string, SortedDictionary<string, CanonicalJsonValue>>> rowsByTable)
    {
        tableOrder.Sort(CodePointComparer.Instance);
        var tables = new List<MergedConfigTable>(tableOrder.Count);
        var canonicalOutput = new StringBuilder();
        foreach (var tableId in tableOrder)
        {
            var canonicalTable = new StringBuilder();
            canonicalTable.Append("{\"tableId\":");
            CanonicalJson.WriteString(canonicalTable, tableId);
            canonicalTable.Append(",\"rows\":[");
            var rows = new List<MergedConfigRow>(rowsByTable[tableId].Count);
            var firstRow = true;
            foreach (var row in rowsByTable[tableId])
            {
                if (!firstRow)
                {
                    canonicalTable.Append(',');
                }

                firstRow = false;
                var canonicalValues = CanonicalJson.WriteMembers(row.Value);
                rows.Add(new MergedConfigRow(row.Key, canonicalValues));
                canonicalTable.Append("{\"key\":");
                CanonicalJson.WriteString(canonicalTable, row.Key);
                canonicalTable.Append(",\"values\":");
                canonicalTable.Append(canonicalValues);
                canonicalTable.Append('}');
            }

            canonicalTable.Append("]}");
            var canonicalText = canonicalTable.ToString();
            tables.Add(new MergedConfigTable(
                tableId,
                columnsByTable[tableId],
                rows,
                Sha256Hex(canonicalText)));
            canonicalOutput.Append(canonicalText);
        }

        return new ConfigMergeResult(
            ConfigMergeStatus.Merged,
            tables,
            Array.Empty<string>(),
            Sha256Hex(canonicalOutput.ToString()),
            rowsByTable);
    }

    private static ConfigMergeResult Failed(IReadOnlyList<string> errors) =>
        new(ConfigMergeStatus.Failed, Array.Empty<MergedConfigTable>(), errors, null, null);

    private static string ColumnSignature(IReadOnlyList<ConfigTableColumnsItem> columns)
    {
        var builder = new StringBuilder();
        foreach (var column in columns)
        {
            builder.Append(column.Name)
                .Append(':')
                .Append(ConfigTableColumnsItemTypeWire.Value(column.Type))
                .Append(':')
                .Append(column.Required ? '1' : '0')
                .Append(':')
                .Append(column.Minimum?.ToString(CultureInfo.InvariantCulture) ?? string.Empty)
                .Append(':')
                .Append(column.Maximum?.ToString(CultureInfo.InvariantCulture) ?? string.Empty)
                .Append(':')
                .Append(column.EnumValues is null ? string.Empty : string.Join(",", column.EnumValues))
                .Append(':')
                .Append(column.RefTarget ?? string.Empty)
                .Append(';');
        }

        return builder.ToString();
    }

    private static string Sha256Hex(string canonicalText)
    {
        if (!string.Equals(CanonicalForm.DigestAlgorithm, "SHA-256", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("generated CanonicalForm.DigestAlgorithm is not SHA-256");
        }

        var bytes = Encoding.ASCII.GetBytes(canonicalText);
#if NET10_0_OR_GREATER
        var digest = SHA256.HashData(bytes);
#else
        using var sha = SHA256.Create();
        var digest = sha.ComputeHash(bytes);
#endif
        return ToHex(digest);
    }

    private static string ToHex(byte[] digest)
    {
        var builder = new StringBuilder(digest.Length * 2);
        foreach (var value in digest)
        {
            builder.Append(value.ToString("x2", CultureInfo.InvariantCulture));
        }

        return builder.ToString();
    }
}

internal sealed class CodePointComparer : IComparer<string>
{
    private CodePointComparer()
    {
    }

    internal static CodePointComparer Instance { get; } = new();

    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y))
        {
            return 0;
        }

        if (x is null)
        {
            return -1;
        }

        if (y is null)
        {
            return 1;
        }

        var left = 0;
        var right = 0;
        while (left < x.Length && right < y.Length)
        {
            var leftCodePoint = char.ConvertToUtf32(x, left);
            var rightCodePoint = char.ConvertToUtf32(y, right);
            if (leftCodePoint != rightCodePoint)
            {
                return leftCodePoint < rightCodePoint ? -1 : 1;
            }

            left += char.IsSurrogatePair(x, left) ? 2 : 1;
            right += char.IsSurrogatePair(y, right) ? 2 : 1;
        }

        return (x.Length - left).CompareTo(y.Length - right);
    }
}

internal sealed class CanonicalJsonValue
{
    internal CanonicalJsonValue(CanonicalJsonKind kind)
    {
        Kind = kind;
    }

    internal CanonicalJsonKind Kind { get; }

    internal string? Literal { get; init; }

    internal string? Text { get; init; }

    internal Dictionary<string, CanonicalJsonValue>? Members { get; init; }

    internal List<CanonicalJsonValue>? Items { get; init; }
}

internal enum CanonicalJsonKind
{
    Object,
    Array,
    String,
    Number,
    True,
    False,
    Null,
}

internal sealed class CanonicalJsonParseResult
{
    private CanonicalJsonParseResult(CanonicalJsonValue? value, List<string> duplicateMembers, string? error)
    {
        Value = value;
        DuplicateMembers = duplicateMembers;
        Error = error;
    }

    internal bool IsOk => Error is null;

    internal CanonicalJsonValue? Value { get; }

    internal IReadOnlyList<string> DuplicateMembers { get; }

    internal string? Error { get; }

    internal static CanonicalJsonParseResult Ok(CanonicalJsonValue value, List<string> duplicateMembers) =>
        new(value, duplicateMembers, null);

    internal static CanonicalJsonParseResult Fail(string error) => new(null, new List<string>(), error);
}

/// <summary>
/// Strict JSON parse/write following generated CanonicalForm: code-point member
/// order, ASCII escaping, no whitespace, duplicate-member detection. Number
/// literals are passed through; the artifact contract is toolchain canonical text.
/// </summary>
internal static class CanonicalJson
{
    private const int MaxDepth = 64;

    internal static CanonicalJsonParseResult Parse(string text)
    {
        var position = 0;
        var duplicateMembers = new List<string>();
        var value = ParseValue(text, ref position, 0, duplicateMembers);
        if (value is null)
        {
            return CanonicalJsonParseResult.Fail($"malformed JSON at position {position}");
        }

        SkipWhitespace(text, ref position);
        if (position != text.Length)
        {
            return CanonicalJsonParseResult.Fail($"trailing content at position {position}");
        }

        return CanonicalJsonParseResult.Ok(value, duplicateMembers);
    }

    internal static string Write(CanonicalJsonValue value)
    {
        var builder = new StringBuilder();
        WriteValue(builder, value);
        return builder.ToString();
    }

    internal static string WriteMembers(IEnumerable<KeyValuePair<string, CanonicalJsonValue>> members)
    {
        var sorted = new SortedDictionary<string, CanonicalJsonValue>(CodePointComparer.Instance);
        foreach (var member in members)
        {
            sorted[member.Key] = member.Value;
        }

        var builder = new StringBuilder();
        builder.Append('{');
        var first = true;
        foreach (var member in sorted)
        {
            if (!first)
            {
                builder.Append(',');
            }

            first = false;
            WriteString(builder, member.Key);
            builder.Append(':');
            WriteValue(builder, member.Value);
        }

        builder.Append('}');
        return builder.ToString();
    }

    internal static void WriteString(StringBuilder builder, string value)
    {
        builder.Append('"');
        foreach (var character in value)
        {
            switch (character)
            {
                case '"': builder.Append("\\\""); break;
                case '\\': builder.Append("\\\\"); break;
                case '\n': builder.Append("\\n"); break;
                case '\r': builder.Append("\\r"); break;
                case '\t': builder.Append("\\t"); break;
                case '\b': builder.Append("\\b"); break;
                case '\f': builder.Append("\\f"); break;
                default:
                    if (character < ' ' || character > '~')
                    {
                        builder.Append("\\u").Append(((int)character).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        builder.Append(character);
                    }

                    break;
            }
        }

        builder.Append('"');
    }

    private static void WriteValue(StringBuilder builder, CanonicalJsonValue value)
    {
        switch (value.Kind)
        {
            case CanonicalJsonKind.Object:
                builder.Append(WriteMembers(value.Members ?? new Dictionary<string, CanonicalJsonValue>()));
                break;
            case CanonicalJsonKind.Array:
                builder.Append('[');
                if (value.Items is not null)
                {
                    var first = true;
                    foreach (var item in value.Items)
                    {
                        if (!first)
                        {
                            builder.Append(',');
                        }

                        first = false;
                        WriteValue(builder, item);
                    }
                }

                builder.Append(']');
                break;
            case CanonicalJsonKind.String:
                WriteString(builder, value.Text ?? string.Empty);
                break;
            case CanonicalJsonKind.Number:
                builder.Append(value.Literal ?? "0");
                break;
            case CanonicalJsonKind.True:
                builder.Append("true");
                break;
            case CanonicalJsonKind.False:
                builder.Append("false");
                break;
            default:
                builder.Append("null");
                break;
        }
    }

    private static CanonicalJsonValue? ParseValue(string text, ref int position, int depth, List<string> duplicateMembers)
    {
        if (depth > MaxDepth)
        {
            return null;
        }

        SkipWhitespace(text, ref position);
        if (position >= text.Length)
        {
            return null;
        }

        var character = text[position];
        switch (character)
        {
            case '{':
                return ParseObject(text, ref position, depth, duplicateMembers);
            case '[':
                return ParseArray(text, ref position, depth, duplicateMembers);
            case '"':
                return ParseString(text, ref position);
            case 't':
                return ParseKeyword(text, ref position, "true", new CanonicalJsonValue(CanonicalJsonKind.True));
            case 'f':
                return ParseKeyword(text, ref position, "false", new CanonicalJsonValue(CanonicalJsonKind.False));
            case 'n':
                return ParseKeyword(text, ref position, "null", new CanonicalJsonValue(CanonicalJsonKind.Null));
            default:
                if (character == '-' || (character >= '0' && character <= '9'))
                {
                    return ParseNumber(text, ref position);
                }

                return null;
        }
    }

    private static CanonicalJsonValue? ParseObject(string text, ref int position, int depth, List<string> duplicateMembers)
    {
        position++;
        var members = new Dictionary<string, CanonicalJsonValue>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        SkipWhitespace(text, ref position);
        if (position < text.Length && text[position] == '}')
        {
            position++;
            return new CanonicalJsonValue(CanonicalJsonKind.Object) { Members = members };
        }

        while (true)
        {
            SkipWhitespace(text, ref position);
            if (position >= text.Length || text[position] != '"')
            {
                return null;
            }

            var key = ParseString(text, ref position);
            if (key is null)
            {
                return null;
            }

            if (!seen.Add(key.Text!))
            {
                duplicateMembers.Add(key.Text!);
            }

            SkipWhitespace(text, ref position);
            if (position >= text.Length || text[position] != ':')
            {
                return null;
            }

            position++;
            var value = ParseValue(text, ref position, depth + 1, duplicateMembers);
            if (value is null)
            {
                return null;
            }

            members[key.Text!] = value;
            SkipWhitespace(text, ref position);
            if (position >= text.Length)
            {
                return null;
            }

            if (text[position] == ',')
            {
                position++;
                continue;
            }

            if (text[position] == '}')
            {
                position++;
                return new CanonicalJsonValue(CanonicalJsonKind.Object) { Members = members };
            }

            return null;
        }
    }

    private static CanonicalJsonValue? ParseArray(string text, ref int position, int depth, List<string> duplicateMembers)
    {
        position++;
        var items = new List<CanonicalJsonValue>();
        SkipWhitespace(text, ref position);
        if (position < text.Length && text[position] == ']')
        {
            position++;
            return new CanonicalJsonValue(CanonicalJsonKind.Array) { Items = items };
        }

        while (true)
        {
            var value = ParseValue(text, ref position, depth + 1, duplicateMembers);
            if (value is null)
            {
                return null;
            }

            items.Add(value);
            SkipWhitespace(text, ref position);
            if (position >= text.Length)
            {
                return null;
            }

            if (text[position] == ',')
            {
                position++;
                continue;
            }

            if (text[position] == ']')
            {
                position++;
                return new CanonicalJsonValue(CanonicalJsonKind.Array) { Items = items };
            }

            return null;
        }
    }

    private static CanonicalJsonValue? ParseString(string text, ref int position)
    {
        position++;
        var builder = new StringBuilder();
        while (position < text.Length)
        {
            var character = text[position];
            if (character == '"')
            {
                position++;
                return new CanonicalJsonValue(CanonicalJsonKind.String) { Text = builder.ToString() };
            }

            if (character < ' ')
            {
                return null;
            }

            if (character == '\\')
            {
                position++;
                if (position >= text.Length)
                {
                    return null;
                }

                var escape = text[position];
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
                    case 'u':
                        if (position + 4 >= text.Length)
                        {
                            return null;
                        }

                        var hex = text.Substring(position + 1, 4);
                        if (!ushort.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code))
                        {
                            return null;
                        }

                        builder.Append((char)code);
                        position += 4;
                        break;
                    default:
                        return null;
                }

                position++;
                continue;
            }

            builder.Append(character);
            position++;
        }

        return null;
    }

    private static CanonicalJsonValue? ParseKeyword(string text, ref int position, string keyword, CanonicalJsonValue value)
    {
        if (position + keyword.Length > text.Length ||
            !string.Equals(text.Substring(position, keyword.Length), keyword, StringComparison.Ordinal))
        {
            return null;
        }

        position += keyword.Length;
        return value;
    }

    private static CanonicalJsonValue? ParseNumber(string text, ref int position)
    {
        var start = position;
        if (text[position] == '-')
        {
            position++;
        }

        if (position >= text.Length || text[position] < '0' || text[position] > '9')
        {
            return null;
        }

        while (position < text.Length && text[position] >= '0' && text[position] <= '9')
        {
            position++;
        }

        if (position < text.Length && text[position] == '.')
        {
            position++;
            if (position >= text.Length || text[position] < '0' || text[position] > '9')
            {
                return null;
            }

            while (position < text.Length && text[position] >= '0' && text[position] <= '9')
            {
                position++;
            }
        }

        if (position < text.Length && (text[position] == 'e' || text[position] == 'E'))
        {
            position++;
            if (position < text.Length && (text[position] == '+' || text[position] == '-'))
            {
                position++;
            }

            if (position >= text.Length || text[position] < '0' || text[position] > '9')
            {
                return null;
            }

            while (position < text.Length && text[position] >= '0' && text[position] <= '9')
            {
                position++;
            }
        }

        return new CanonicalJsonValue(CanonicalJsonKind.Number) { Literal = text.Substring(start, position - start) };
    }

    private static void SkipWhitespace(string text, ref int position)
    {
        while (position < text.Length)
        {
            var character = text[position];
            if (character == ' ' || character == '\t' || character == '\n' || character == '\r')
            {
                position++;
            }
            else
            {
                break;
            }
        }
    }
}
