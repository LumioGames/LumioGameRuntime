namespace Lumio.GameRuntime.Simulation.Ingress;

/// <summary>Named projection of the generated bounded ingress configuration.</summary>
public readonly record struct IngressBudget(int Capacity, long MaxBytes)
{
    public const string CapacityParameterName = "IngressQueueCapacity";

    public const string BytesParameterName = "IngressQueueBytes";

    public bool IsValid => Capacity > 0 && MaxBytes > 0;

    public static IngressBudget FromNamedParameters(int IngressQueueCapacity, long IngressQueueBytes) =>
        new(IngressQueueCapacity, IngressQueueBytes);

    public static implicit operator IngressQueueOptions(IngressBudget value) =>
        new(value.Capacity, value.MaxBytes);

    public static implicit operator IngressBudget(IngressQueueOptions value) =>
        new(value.Capacity, value.MaxBytes);
}
