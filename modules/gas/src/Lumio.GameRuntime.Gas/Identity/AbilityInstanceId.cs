namespace Lumio.GameRuntime.Gas;

/// <summary>ECS instance-row identity for one Ability. Not a type and not a handle.</summary>
public readonly record struct AbilityInstanceId(ulong Value)
{
    public bool IsDefault => Value == 0UL;
}
