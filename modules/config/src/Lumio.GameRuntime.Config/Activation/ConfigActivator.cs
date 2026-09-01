using System;

namespace Lumio.GameRuntime.Config;

/// <summary>
/// Tick Barrier activation facade. Does not own staging; it only publishes the
/// staged snapshot at the owner-thread barrier.
/// </summary>
public sealed class ConfigActivator
{
    private readonly ConfigActivationSlot _slot;

    /// <summary>Bind to an existing activation slot.</summary>
    public ConfigActivator(ConfigActivationSlot slot)
    {
#if NET10_0_OR_GREATER
        ArgumentNullException.ThrowIfNull(slot);
#else
        if (slot is null)
        {
            throw new ArgumentNullException(nameof(slot));
        }
#endif
        _slot = slot;
    }

    /// <summary>Activate the staged snapshot at the Tick Barrier.</summary>
    public ConfigActivationResult ActivateAtBarrier(TickId tickId) =>
        _slot.ActivateAtBarrier(tickId);
}
