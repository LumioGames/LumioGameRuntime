using System;

namespace Lumio.GameRuntime.Ecs;

public readonly record struct QuerySpec(
    ReadOnlyMemory<ComponentTypeId> Required,
    ReadOnlyMemory<ComponentTypeId> Excluded,
    ReadOnlyMemory<ComponentFieldId> ReadSet,
    ReadOnlyMemory<ComponentFieldId> WriteSet)
{
    public static QuerySpec Empty => new(
        ReadOnlyMemory<ComponentTypeId>.Empty,
        ReadOnlyMemory<ComponentTypeId>.Empty,
        ReadOnlyMemory<ComponentFieldId>.Empty,
        ReadOnlyMemory<ComponentFieldId>.Empty);

    public bool IsWellFormed => AreSortedUnique(Required) && AreSortedUnique(Excluded) &&
                                 AreSortedUnique(ReadSet) && AreSortedUnique(WriteSet) &&
                                 !Intersects(Required, Excluded);

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

/// <summary>
/// Internal storage boundary. Concrete ECS engines must not escape this assembly.
/// </summary>
internal interface IWorldStorageAdapter : IDisposable
{
    StorageOperationResult Register(in GeneratedComponentSchemaView schema);

    StorageOperationResult Create(LocalEntityId entity, in ComponentInitBatch components);

    StorageOperationResult Destroy(LocalEntityId entity);

    StorageOperationResult CompileQuery(in QuerySpec spec, out StorageQueryHandle handle);

    StorageOperationResult EnumerateOrdered(
        StorageQueryHandle handle,
        Span<LocalEntityId> destination,
        out int written);

    StorageOperationResult ReadField(
        LocalEntityId entity,
        ComponentTypeId componentType,
        ComponentFieldId field,
        Span<byte> destination,
        out int written);

    StorageOperationResult WriteExistingField(
        LocalEntityId entity,
        ComponentTypeId componentType,
        ComponentFieldId field,
        ReadOnlySpan<byte> canonicalValue);

    StorageOperationResult CaptureReadSnapshot(out StorageReadSnapshotHandle handle);

    StorageOperationResult ReleaseReadSnapshot(StorageReadSnapshotHandle handle);

    StorageOperationResult ValidateIntegrity();
}
