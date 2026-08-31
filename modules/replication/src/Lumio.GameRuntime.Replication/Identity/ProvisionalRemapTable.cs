using System;
using System.Collections.Generic;

namespace Lumio.GameRuntime.Replication.Mapping;

public readonly record struct ProvisionalRemapResult(bool Succeeded, NetEntityId? AuthoritativeId, string? GeneratedErrorId)
{
    public static ProvisionalRemapResult Accepted(NetEntityId id) => new(true, id, null);

    public static ProvisionalRemapResult Rejected(string errorId) => new(false, null, errorId);
}

public sealed class ProvisionalRemapTable
{
    private readonly object _gate = new();
    private readonly Dictionary<NetEntityId, NetEntityId> _remaps = new();

    public ProvisionalRemapResult Add(NetEntityId provisional, NetEntityId authoritative)
    {
        if (!provisional.IsValid || !authoritative.IsValid || provisional == authoritative)
            return ProvisionalRemapResult.Rejected("InvalidArgument");
        lock (_gate)
        {
            if (_remaps.TryGetValue(provisional, out NetEntityId existing))
                return existing == authoritative ? ProvisionalRemapResult.Accepted(existing) : ProvisionalRemapResult.Rejected("RevisionConflict");
            _remaps.Add(provisional, authoritative);
            return ProvisionalRemapResult.Accepted(authoritative);
        }
    }

    public bool TryResolve(NetEntityId provisional, out NetEntityId authoritative)
    {
        lock (_gate) return _remaps.TryGetValue(provisional, out authoritative);
    }

    public IReadOnlyDictionary<NetEntityId, NetEntityId> Snapshot()
    {
        lock (_gate) return new Dictionary<NetEntityId, NetEntityId>(_remaps);
    }
}
