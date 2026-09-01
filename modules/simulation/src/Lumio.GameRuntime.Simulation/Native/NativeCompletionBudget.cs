namespace Lumio.GameRuntime.Simulation.Native;

/// <summary>Named projection of the bounded native completion configuration.</summary>
public readonly record struct NativeCompletionBudget(int Capacity, long MaxBytes)
{
    public const string CapacityParameterName = "NativeCompletionQueueCapacity";

    public bool IsValid => Capacity > 0 && MaxBytes > 0;

    public NativeCompletionBudget(int NativeCompletionQueueCapacity)
        : this(NativeCompletionQueueCapacity, checked((long)NativeCompletionQueueCapacity * 4096L))
    {
    }

    public static NativeCompletionBudget FromNamedParameter(int NativeCompletionQueueCapacity) =>
        new(NativeCompletionQueueCapacity);
}
