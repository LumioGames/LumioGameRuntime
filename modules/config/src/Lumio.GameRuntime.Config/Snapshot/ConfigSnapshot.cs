using System;
using System.Collections.Generic;

namespace Lumio.GameRuntime.Config;

/// <summary>One cell used to construct a snapshot. Copied on construct.</summary>
/// <param name="Column">Generated column name.</param>
/// <param name="CanonicalText">Canonical cell text.</param>
public readonly record struct ConfigSnapshotCell(string Column, string CanonicalText);

/// <summary>One row used to construct a snapshot. The cell span is copied.</summary>
public readonly struct ConfigSnapshotRow
{
    private readonly ConfigSnapshotCell[] _cells;

    /// <summary>Copy <paramref name="cells"/> into owned storage.</summary>
    public ConfigSnapshotRow(string key, ReadOnlySpan<ConfigSnapshotCell> cells)
    {
#if NET10_0_OR_GREATER
        ArgumentNullException.ThrowIfNull(key);
        Key = key;
#else
        Key = key ?? throw new ArgumentNullException(nameof(key));
#endif
        _cells = cells.ToArray();
    }

    /// <summary>Row key.</summary>
    public string Key { get; }

    internal ReadOnlySpan<ConfigSnapshotCell> Cells =>
        _cells ?? Array.Empty<ConfigSnapshotCell>();

    internal ConfigSnapshotRow Copy() => new(Key, Cells);
}

/// <summary>One table used to construct a snapshot. Row storage is copied.</summary>
public sealed class ConfigSnapshotTable
{
    private readonly ConfigSnapshotRow[] _rows;

    /// <summary>Copy <paramref name="rows"/> into owned storage.</summary>
    public ConfigSnapshotTable(string tableId, string canonicalBytesHex, ReadOnlySpan<ConfigSnapshotRow> rows)
    {
#if NET10_0_OR_GREATER
        ArgumentNullException.ThrowIfNull(tableId);
        ArgumentNullException.ThrowIfNull(canonicalBytesHex);
        TableId = tableId;
        CanonicalBytesHex = canonicalBytesHex;
#else
        TableId = tableId ?? throw new ArgumentNullException(nameof(tableId));
        CanonicalBytesHex = canonicalBytesHex ?? throw new ArgumentNullException(nameof(canonicalBytesHex));
#endif
        _rows = new ConfigSnapshotRow[rows.Length];
        for (var index = 0; index < rows.Length; index++)
        {
            _rows[index] = rows[index].Copy();
        }
    }

    /// <summary>Generated table id.</summary>
    public string TableId { get; }

    /// <summary>Canonical table digest copied at construct time.</summary>
    public string CanonicalBytesHex { get; }

    internal ReadOnlySpan<ConfigSnapshotRow> Rows => _rows;
}

/// <summary>Owned immutable table storage. Never returned to callers.</summary>
internal sealed class ConfigTableData
{
    internal ConfigTableData(
        string tableId,
        string canonicalBytesHex,
        Dictionary<string, Dictionary<string, string>> cellsByRow)
    {
        TableId = tableId;
        CanonicalBytesHex = canonicalBytesHex;
        CellsByRow = cellsByRow;
    }

    internal string TableId { get; }

    internal string CanonicalBytesHex { get; }

    internal Dictionary<string, Dictionary<string, string>> CellsByRow { get; }
}

/// <summary>
/// Immutable config snapshot. Constructor copies table/row/cell storage so later
/// mutation of caller arrays cannot be observed. Readers are concurrent-safe.
/// </summary>
public sealed class ConfigSnapshot : IConfigSnapshotView
{
    private readonly ConfigTableData[] _tables;

    /// <summary>Copy-on-construct from caller table views.</summary>
    public ConfigSnapshot(
        ConfigSnapshotId snapshotId,
        SchemaEpoch schemaEpoch,
        string outputHashHex,
        ReadOnlySpan<ConfigSnapshotTable> tables)
    {
        SnapshotId = snapshotId;
        SchemaEpoch = schemaEpoch;
#if NET10_0_OR_GREATER
        ArgumentNullException.ThrowIfNull(outputHashHex);
        OutputHashHex = outputHashHex;
#else
        OutputHashHex = outputHashHex ?? throw new ArgumentNullException(nameof(outputHashHex));
#endif
        _tables = new ConfigTableData[tables.Length];
        for (var index = 0; index < tables.Length; index++)
        {
            _tables[index] = CopyTable(tables[index]);
        }
    }

    /// <inheritdoc />
    public ConfigSnapshotId SnapshotId { get; }

    /// <inheritdoc />
    public SchemaEpoch SchemaEpoch { get; }

    /// <summary>Canonical output digest of the merged tables copied at construct time.</summary>
    public string OutputHashHex { get; }

    /// <inheritdoc />
    public bool TryOpenTable(string tableName, out ConfigTableReader reader)
    {
        reader = default;
        if (string.IsNullOrEmpty(tableName))
        {
            return false;
        }

        for (var index = 0; index < _tables.Length; index++)
        {
            if (string.Equals(_tables[index].TableId, tableName, StringComparison.Ordinal))
            {
                reader = new ConfigTableReader(_tables[index], lease: null);
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Build a snapshot from Task 6 merged tables. Copies row/cell storage; does
    /// not alias merger dictionaries.
    /// </summary>
    internal static ConfigSnapshot FromMergeResult(
        ConfigSnapshotId snapshotId,
        SchemaEpoch schemaEpoch,
        ConfigMergeResult merged)
    {
#if NET10_0_OR_GREATER
        ArgumentNullException.ThrowIfNull(merged);
#else
        if (merged is null)
        {
            throw new ArgumentNullException(nameof(merged));
        }
#endif

        if (merged.Status != ConfigMergeStatus.Merged)
        {
            throw new InvalidOperationException("Cannot construct a snapshot from a failed merge.");
        }

        var tables = new ConfigSnapshotTable[merged.Tables.Count];
        for (var index = 0; index < merged.Tables.Count; index++)
        {
            var table = merged.Tables[index];
            var rows = new ConfigSnapshotRow[table.Rows.Count];
            for (var rowIndex = 0; rowIndex < table.Rows.Count; rowIndex++)
            {
                var row = table.Rows[rowIndex];
                rows[rowIndex] = new ConfigSnapshotRow(row.Key, ParseCells(row.CanonicalValuesJson));
            }

            tables[index] = new ConfigSnapshotTable(table.TableId, table.CanonicalBytesHex, rows);
        }

        return new ConfigSnapshot(
            snapshotId,
            schemaEpoch,
            merged.OutputHashHex ?? string.Empty,
            tables);
    }

    private static ConfigTableData CopyTable(ConfigSnapshotTable table)
    {
        var cellsByRow = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        var rows = table.Rows;
        for (var index = 0; index < rows.Length; index++)
        {
            var row = rows[index];
            var cells = new Dictionary<string, string>(StringComparer.Ordinal);
            var source = row.Cells;
            for (var cellIndex = 0; cellIndex < source.Length; cellIndex++)
            {
                var cell = source[cellIndex];
                if (cell.Column is null)
                {
                    continue;
                }

                cells[cell.Column] = cell.CanonicalText ?? string.Empty;
            }

            cellsByRow[row.Key] = cells;
        }

        return new ConfigTableData(table.TableId, table.CanonicalBytesHex, cellsByRow);
    }

    private static ConfigSnapshotCell[] ParseCells(string canonicalValuesJson)
    {
        var parsed = CanonicalJson.Parse(canonicalValuesJson);
        if (!parsed.IsOk ||
            parsed.Value is null ||
            parsed.Value.Kind != CanonicalJsonKind.Object ||
            parsed.Value.Members is null)
        {
            return Array.Empty<ConfigSnapshotCell>();
        }

        var cells = new ConfigSnapshotCell[parsed.Value.Members.Count];
        var index = 0;
        foreach (var member in parsed.Value.Members)
        {
            cells[index++] = new ConfigSnapshotCell(member.Key, CanonicalTextOf(member.Value));
        }

        return cells;
    }

    private static string CanonicalTextOf(CanonicalJsonValue value) => value.Kind switch
    {
        CanonicalJsonKind.Number => value.Literal ?? "0",
        CanonicalJsonKind.String => value.Text ?? string.Empty,
        CanonicalJsonKind.True => "true",
        CanonicalJsonKind.False => "false",
        CanonicalJsonKind.Null => "null",
        _ => CanonicalJson.Write(value),
    };
}
