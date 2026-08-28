using System;

namespace Lumio.GameRuntime.Observability;

public readonly record struct EventSequence(ulong Value);

internal sealed class ProducerSequence
{
    private readonly object _gate = new();
    private ulong _nextEventSeq;

    internal ProducerSequence(ulong initialValue)
    {
        _nextEventSeq = initialValue;
    }

    internal EventSequence Next()
    {
        lock (_gate)
        {
            checked
            {
                _nextEventSeq++;
            }

            return new EventSequence(_nextEventSeq);
        }
    }
}
