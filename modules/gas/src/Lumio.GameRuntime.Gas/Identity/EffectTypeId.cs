namespace Lumio.GameRuntime.Gas;

/// <summary>Stable generated Effect type identity. Not an instance and not a handle.</summary>
public readonly record struct EffectTypeId(uint Value)
{
    public bool IsDefault => Value == 0U;
}
