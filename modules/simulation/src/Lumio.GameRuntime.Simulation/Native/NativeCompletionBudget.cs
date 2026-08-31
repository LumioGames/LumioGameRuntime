using System;

namespace Lumio.GameRuntime.Simulation.Native;

/// <summary>Named projection of the bounded native completion configuration.</summary>
public readonly record struct NativeCompletionBudget(int Capacity, long MaxBytes)
{
    public bool IsValid => Capacity > 0 && MaxBytes > 0;

    public NativeCompletionBudget(int capacity)
        : this(capacity, checked((long)capacity * 4096L))
    {
    }
}
