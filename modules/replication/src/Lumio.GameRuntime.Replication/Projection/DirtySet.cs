using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace Lumio.GameRuntime.Replication.Projection;

public enum DirtySetStatus
{
    Added,
    Duplicate,
    QueueFull,
    Invalid
}

public sealed class DirtySet
{
    private readonly int _capacity;
    private readonly HashSet<string> _keys = new(StringComparer.Ordinal);

    public DirtySet(int capacity)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        _capacity = capacity;
    }

    public int Count => _keys.Count;

    public DirtySetStatus Add(string key)
    {
        if (!ReplicationValidation.IsIdentifier(key)) return DirtySetStatus.Invalid;
        if (_keys.Contains(key)) return DirtySetStatus.Duplicate;
        if (_keys.Count >= _capacity) return DirtySetStatus.QueueFull;
        _keys.Add(key);
        return DirtySetStatus.Added;
    }

    public IReadOnlyList<string> Snapshot() => new ReadOnlyCollection<string>(_keys.OrderBy(value => value, StringComparer.Ordinal).ToArray());

    public void Clear() => _keys.Clear();
}
