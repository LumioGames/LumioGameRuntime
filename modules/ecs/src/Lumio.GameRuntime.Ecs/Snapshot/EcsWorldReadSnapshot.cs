using System;
using System.Threading;

namespace Lumio.GameRuntime.Ecs;

internal sealed class EcsWorldReadSnapshot : IDisposable
{
    private readonly EcsWorld _world;
    private readonly StorageReadSnapshotHandle _handle;
    private int _disposed;

    internal EcsWorldReadSnapshot(
        EcsWorld world,
        StorageReadSnapshotHandle handle,
        in EcsSnapshotCutView cut)
    {
        _world = world ?? throw new ArgumentNullException(nameof(world));
        _handle = handle;
        WorldId = world.WorldId;
        TickId = new TickId(cut.TickId);
        Revision = new Revision(cut.Revision);
        SchemaEpoch = cut.SchemaEpoch;
    }

    public WorldId WorldId { get; }

    public TickId TickId { get; }

    public Revision Revision { get; }

    public ulong SchemaEpoch { get; }

    public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    public StorageOperationResult ReadField(
        LocalEntityId entity,
        ComponentTypeId componentType,
        ComponentFieldId field,
        Span<byte> destination,
        out int written)
    {
        written = 0;
        if (IsDisposed) return StorageOperationResult.Rejected(EcsErrorCodes.SnapshotReleased);
        return _world.ReadSnapshotField(_handle, entity, componentType, field, destination, out written);
    }

    public StorageOperationResult EnumerateEntities(Span<LocalEntityId> destination, out int written)
    {
        written = 0;
        if (IsDisposed) return StorageOperationResult.Rejected(EcsErrorCodes.SnapshotReleased);
        return _world.EnumerateSnapshotEntities(_handle, destination, out written);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _world.ReleaseReadSnapshot(_handle);
    }
}
