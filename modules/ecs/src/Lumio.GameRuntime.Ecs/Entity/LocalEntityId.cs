using System;

namespace Lumio.GameRuntime.Ecs;

public readonly record struct LocalEntityId(uint Index, uint Generation) : IComparable<LocalEntityId>
{
    public bool IsDefault => Index == 0U && Generation == 0U;
    public int CompareTo(LocalEntityId other)
    {
        int index = Index.CompareTo(other.Index);
        return index == 0 ? Generation.CompareTo(other.Generation) : index;
    }

    public static bool operator <(LocalEntityId left, LocalEntityId right) => left.CompareTo(right) < 0;
    public static bool operator <=(LocalEntityId left, LocalEntityId right) => left.CompareTo(right) <= 0;
    public static bool operator >(LocalEntityId left, LocalEntityId right) => left.CompareTo(right) > 0;
    public static bool operator >=(LocalEntityId left, LocalEntityId right) => left.CompareTo(right) >= 0;

    public override string ToString() => $"{Index}:{Generation}";
    public static bool TryParse(string? value, out LocalEntityId id)
    {
        id = default;
        if (string.IsNullOrWhiteSpace(value)) return false;
        int separator = value.IndexOf(':');
        if (separator <= 0 || separator == value.Length - 1) return false;
        if (!uint.TryParse(value.AsSpan(0, separator), out uint index)) return false;
        if (!uint.TryParse(value.AsSpan(separator + 1), out uint generation)) return false;
        id = new LocalEntityId(index, generation);
        return true;
    }
}
