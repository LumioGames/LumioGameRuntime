namespace Lumio.GameRuntime.Gas;

/// <summary>Stable generated Ability type identity. Not an instance and not a handle.</summary>
public readonly record struct AbilityTypeId(uint Value)
{
    public bool IsDefault => Value == 0U;
}
