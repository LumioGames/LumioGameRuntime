using Lumio.GameRuntime.Ecs;

namespace Lumio.GameRuntime.Gas;

/// <summary>World-bound Ability handle. Equality is WorldId + instance + generation, never an object address.</summary>
public readonly record struct AbilityHandle(
    WorldId WorldId,
    AbilityInstanceId InstanceId,
    uint Generation)
{
    public bool IsDefault => WorldId.IsDefault && InstanceId.IsDefault && Generation == 0U;
}

/// <summary>Issue result for a World-bound Ability handle.</summary>
public readonly record struct AbilityHandleResult(
    bool Succeeded,
    AbilityHandle Handle,
    string? GeneratedErrorId)
{
    public static AbilityHandleResult Issued(AbilityHandle handle) => new(true, handle, null);

    public static AbilityHandleResult Failed(string generatedErrorId) => new(false, default, generatedErrorId);
}

/// <summary>Handle resolve result. Failed resolves never return a live instance.</summary>
public readonly record struct GasResolveResult(bool Resolved, string? GeneratedErrorId)
{
    public static GasResolveResult Ok() => new(true, null);

    public static GasResolveResult Failed(string generatedErrorId) => new(false, generatedErrorId);
}

/// <summary>Handle retire result. Stale handles stay permanently invalid.</summary>
public readonly record struct GasRetireResult(bool Succeeded, string? GeneratedErrorId)
{
    public static GasRetireResult Retired() => new(true, null);

    public static GasRetireResult Failed(string generatedErrorId) => new(false, generatedErrorId);
}
