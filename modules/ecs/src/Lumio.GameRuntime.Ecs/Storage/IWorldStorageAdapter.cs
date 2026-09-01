using System;

namespace Lumio.GameRuntime.Ecs;

/// <summary>
/// Internal storage boundary. Concrete ECS engines must not escape this assembly.
/// </summary>
internal interface IWorldStorageAdapter : IDisposable
{
    StorageOperationResult Register(ComponentTypeDefinition definition);

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

    StorageOperationResult CaptureReadSnapshot(
        in StorageSnapshotContext context,
        out StorageReadSnapshotHandle handle);

    StorageOperationResult EnumerateSnapshotOrdered(
        StorageReadSnapshotHandle handle,
        Span<LocalEntityId> destination,
        out int written);

    StorageOperationResult ReadSnapshotField(
        StorageReadSnapshotHandle handle,
        LocalEntityId entity,
        ComponentTypeId componentType,
        ComponentFieldId field,
        Span<byte> destination,
        out int written);

    StorageOperationResult ReleaseReadSnapshot(StorageReadSnapshotHandle handle);

    StorageOperationResult ValidateIntegrity();
}
