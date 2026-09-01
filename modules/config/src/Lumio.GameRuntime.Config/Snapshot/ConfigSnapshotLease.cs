using System;
using System.Threading;

namespace Lumio.GameRuntime.Config;

/// <summary>
/// Tick-pinned access to one immutable snapshot. Dispose is the use-after-return
/// fence: further Snapshot/reader access throws <see cref="ObjectDisposedException"/>.
/// Replacing Active does not affect an already acquired lease.
/// </summary>
public sealed class ConfigSnapshotLease : IDisposable
{
    private ConfigSnapshot? _snapshot;
    private readonly TickId _tickId;

    internal ConfigSnapshotLease(ConfigSnapshot snapshot, TickId tickId)
    {
#if NET10_0_OR_GREATER
        ArgumentNullException.ThrowIfNull(snapshot);
        _snapshot = snapshot;
#else
        _snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
#endif
        _tickId = tickId;
    }

    /// <summary>Pinned snapshot. Throws after <see cref="Dispose"/>.</summary>
    public ConfigSnapshot Snapshot
    {
        get
        {
            ConfigSnapshot? snapshot = Volatile.Read(ref _snapshot);
#if NET10_0_OR_GREATER
            ObjectDisposedException.ThrowIf(snapshot is null, this);
            return snapshot;
#else
            if (snapshot is null)
            {
                throw new ObjectDisposedException(nameof(ConfigSnapshotLease));
            }

            return snapshot;
#endif
        }
    }

    /// <summary>Tick the lease was acquired for. Identity only; it does not switch snapshots.</summary>
    public TickId TickId => _tickId;

    internal bool IsDisposed => Volatile.Read(ref _snapshot) is null;

    /// <summary>Open a table reader bound to this lease's lifetime.</summary>
    public bool TryOpenTable(string tableName, out ConfigTableReader reader)
    {
        ConfigSnapshot snapshot = Snapshot;
        if (!snapshot.TryOpenTable(tableName, out reader))
        {
            return false;
        }

        reader = reader.Bind(this);
        return true;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Volatile.Write(ref _snapshot, null);
    }
}
