using Lumio.GameRuntime.Ecs;

namespace Lumio.GameRuntime.Gas;

/// <summary>World-bound Effect handle. Equality is WorldId + instance + generation, never an object address.</summary>
public readonly record struct EffectHandle(
    WorldId WorldId,
    EffectInstanceId InstanceId,
    uint Generation)
{
    public bool IsDefault => WorldId.IsDefault && InstanceId.IsDefault && Generation == 0U;
}

/// <summary>Issue result for a World-bound Effect handle.</summary>
public readonly record struct EffectHandleResult(
    bool Succeeded,
    EffectHandle Handle,
    string? GeneratedErrorId)
{
    public static EffectHandleResult Issued(EffectHandle handle) => new(true, handle, null);

    public static EffectHandleResult Failed(string generatedErrorId) => new(false, default, generatedErrorId);
}
