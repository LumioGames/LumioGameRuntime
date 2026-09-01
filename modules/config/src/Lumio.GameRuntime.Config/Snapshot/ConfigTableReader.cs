using System;

namespace Lumio.GameRuntime.Config;

/// <summary>One already-validated cell value. Canonical text, not a mutable container.</summary>
/// <param name="CanonicalText">Toolchain canonical cell text copied into the snapshot.</param>
public readonly record struct ConfigValueView(string CanonicalText);

/// <summary>
/// Typed reader over one immutable table. Does not expose dictionaries, arrays,
/// or caller-owned buffers. A reader bound to a lease throws after dispose.
/// </summary>
public readonly struct ConfigTableReader
{
    private readonly ConfigTableData? _table;
    private readonly ConfigSnapshotLease? _lease;

    internal ConfigTableReader(ConfigTableData table, ConfigSnapshotLease? lease)
    {
        _table = table;
        _lease = lease;
    }

    /// <summary>Canonical SHA-256 hex of this table copied at snapshot construct time.</summary>
    public string CanonicalBytesHex
    {
        get
        {
            EnsureAlive();
            return _table is null ? string.Empty : _table.CanonicalBytesHex;
        }
    }

    /// <summary>
    /// Look up one cell. Missing row/column returns false and does not invent a zero default.
    /// </summary>
    public bool TryGet(string key, string column, out ConfigValueView value)
    {
        EnsureAlive();
        value = default;
        if (_table is null || key is null || column is null)
        {
            return false;
        }

        if (!_table.CellsByRow.TryGetValue(key, out var row) ||
            !row.TryGetValue(column, out var text))
        {
            return false;
        }

        value = new ConfigValueView(text);
        return true;
    }

    internal ConfigTableReader Bind(ConfigSnapshotLease lease)
    {
        if (_table is null)
        {
            throw new InvalidOperationException("Cannot bind an empty table reader.");
        }

        return new ConfigTableReader(_table, lease);
    }

    private void EnsureAlive()
    {
#if NET10_0_OR_GREATER
        ObjectDisposedException.ThrowIf(_lease is not null && _lease.IsDisposed, typeof(ConfigSnapshotLease));
#else
        if (_lease is not null && _lease.IsDisposed)
        {
            throw new ObjectDisposedException(nameof(ConfigSnapshotLease));
        }
#endif
    }
}
