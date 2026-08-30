using System;
using System.Collections.Generic;

namespace Lumio.GameRuntime.Simulation.Tick;

public sealed class TickResultCache
{
    private readonly int _capacity;
    private readonly Dictionary<ulong, TickRunResult> _results = new();
    private readonly Queue<ulong> _order = new();

    public TickResultCache(int capacity = 256)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        _capacity = capacity;
    }

    public int Count => _results.Count;

    public bool TryGet(ulong tickId, out TickRunResult? result) => _results.TryGetValue(tickId, out result);

    public void Add(TickRunResult result)
    {
        if (result is null) throw new ArgumentNullException(nameof(result));
        if (_results.ContainsKey(result.TickId)) return;
        _results.Add(result.TickId, result);
        _order.Enqueue(result.TickId);
        while (_order.Count > _capacity)
        {
            ulong oldest = _order.Dequeue();
            _results.Remove(oldest);
        }
    }
}
