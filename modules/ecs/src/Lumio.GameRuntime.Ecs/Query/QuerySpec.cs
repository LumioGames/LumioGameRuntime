using System;

namespace Lumio.GameRuntime.Ecs;

internal readonly record struct QuerySpec
{
    public QuerySpec(
        ReadOnlyMemory<ComponentTypeId> required,
        ReadOnlyMemory<ComponentTypeId> excluded,
        ReadOnlyMemory<ComponentFieldId> readSet,
        ReadOnlyMemory<ComponentFieldId> writeSet)
    {
        Required = Canonicalize(required);
        Excluded = Canonicalize(excluded);
        ReadSet = Canonicalize(readSet);
        WriteSet = Canonicalize(writeSet);
        if (Intersects(Required, Excluded))
            throw EcsFailure.Rejected(
                EcsErrorCodes.QueryBoundary,
                "Required and Excluded component sets conflict.");
    }

    public static QuerySpec Empty { get; } = new(
        ReadOnlyMemory<ComponentTypeId>.Empty,
        ReadOnlyMemory<ComponentTypeId>.Empty,
        ReadOnlyMemory<ComponentFieldId>.Empty,
        ReadOnlyMemory<ComponentFieldId>.Empty);

    public ReadOnlyMemory<ComponentTypeId> Required { get; }

    public ReadOnlyMemory<ComponentTypeId> Excluded { get; }

    public ReadOnlyMemory<ComponentFieldId> ReadSet { get; }

    public ReadOnlyMemory<ComponentFieldId> WriteSet { get; }

    public bool IsWellFormed => AreSortedUnique(Required) && AreSortedUnique(Excluded) &&
                                 AreSortedUnique(ReadSet) && AreSortedUnique(WriteSet) &&
                                 !Intersects(Required, Excluded);

    private static ReadOnlyMemory<T> Canonicalize<T>(ReadOnlyMemory<T> values) where T : IComparable<T>
    {
        if (values.Length == 0) return ReadOnlyMemory<T>.Empty;
        T[] copy = values.ToArray();
        Array.Sort(copy);
        int unique = 0;
        for (int index = 0; index < copy.Length; index++)
        {
            if (unique == 0 || copy[unique - 1].CompareTo(copy[index]) != 0)
                copy[unique++] = copy[index];
        }

        if (unique != copy.Length) Array.Resize(ref copy, unique);
        return copy;
    }

    private static bool AreSortedUnique<T>(ReadOnlyMemory<T> values) where T : IComparable<T>
    {
        ReadOnlySpan<T> span = values.Span;
        for (int i = 1; i < span.Length; i++)
        {
            if (span[i - 1].CompareTo(span[i]) >= 0) return false;
        }

        return true;
    }

    private static bool Intersects<T>(ReadOnlyMemory<T> left, ReadOnlyMemory<T> right) where T : IEquatable<T>
    {
        ReadOnlySpan<T> values = left.Span;
        for (int i = 0; i < values.Length; i++)
        {
            if (right.Span.IndexOf(values[i]) >= 0) return true;
        }

        return false;
    }
}
