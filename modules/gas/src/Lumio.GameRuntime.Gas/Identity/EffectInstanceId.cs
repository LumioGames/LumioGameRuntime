namespace Lumio.GameRuntime.Gas;

/// <summary>ECS instance-row identity for one Effect. Not a type and not a handle.</summary>
public readonly record struct EffectInstanceId(ulong Value)
{
    public bool IsDefault => Value == 0UL;
}
