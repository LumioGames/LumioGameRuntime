namespace Lumio.GameRuntime.Gas;

/// <summary>Exact V1.4 GAS framework lifecycle names. Game content cannot add synonyms.</summary>
public enum GasFrameworkState
{
    Unloaded,
    Registered,
    Ready,
    Running,
    Draining,
    Faulted
}

/// <summary>Lifecycle mutation result carrying a generated catalog error id on rejection.</summary>
public readonly record struct GasLifecycleResult(
    bool Succeeded,
    GasFrameworkState State,
    string? GeneratedErrorId)
{
    public static GasLifecycleResult Accepted(GasFrameworkState state) => new(true, state, null);

    public static GasLifecycleResult Rejected(GasFrameworkState state, string generatedErrorId) =>
        new(false, state, generatedErrorId);
}
