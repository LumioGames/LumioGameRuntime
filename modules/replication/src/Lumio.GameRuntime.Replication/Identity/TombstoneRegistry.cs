using System;
using System.Collections.Generic;

namespace Lumio.GameRuntime.Replication.Mapping;

public readonly record struct TombstoneView(NetEntityId NetEntityId, ulong UntilRevision);

public sealed class TombstoneRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<NetEntityId, ulong> _values = new();

    public bool Add(NetEntityId id, ulong untilRevision)
    {
        if (!id.IsValid) return false;
        lock (_gate)
        {
            if (_values.TryGetValue(id, out ulong existing) && untilRevision < existing) return false;
            _values[id] = untilRevision;
            return true;
        }
    }

    public bool Contains(NetEntityId id, ulong revision)
    {
        lock (_gate) return _values.TryGetValue(id, out ulong until) && revision <= until;
    }

    public int Collect(ulong revision, in TombstoneHorizonResult horizon)
    {
        if (!horizon.Known || revision <= horizon.Horizon) return 0;
        var removed = 0;
        lock (_gate)
        {
            var ids = new List<NetEntityId>();
            foreach (KeyValuePair<NetEntityId, ulong> item in _values)
                if (item.Value < revision && item.Value <= horizon.Horizon) ids.Add(item.Key);
            foreach (NetEntityId id in ids) if (_values.Remove(id)) removed++;
        }
        return removed;
    }

    public bool CanCollect(NetEntityId id, in TombstoneHorizonInputs inputs, ulong currentRevision)
    {
        TombstoneHorizonResult horizon = TombstoneHorizonCalculator.Calculate(inputs);
        lock (_gate)
        {
            return _values.TryGetValue(id, out ulong until) && horizon.CanCollect(until, currentRevision);
        }
    }

    public bool Remove(NetEntityId id)
    {
        lock (_gate) return _values.Remove(id);
    }

    public IReadOnlyDictionary<NetEntityId, ulong> Snapshot()
    {
        lock (_gate) return new Dictionary<NetEntityId, ulong>(_values);
    }
}
