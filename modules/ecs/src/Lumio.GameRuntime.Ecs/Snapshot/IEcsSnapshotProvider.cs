using System;

namespace Lumio.GameRuntime.Ecs;

internal readonly record struct EcsSnapshotCutView(
    ulong SnapshotId,
    ulong TickId,
    ulong Revision,
    ulong SchemaEpoch);

internal readonly record struct EcsSnapshotCaptureResult(
    StorageOperationStatus Status,
    EcsWorldReadSnapshot? Snapshot,
    ErrorIdentity? Error);

internal interface IEcsSnapshotProvider
{
    EcsSnapshotCaptureResult Capture(in EcsSnapshotCutView cut);
}

internal sealed class EcsWorldSnapshotProvider : IEcsSnapshotProvider
{
    private readonly EcsWorld _world;

    public EcsWorldSnapshotProvider(EcsWorld world)
    {
        _world = world ?? throw new ArgumentNullException(nameof(world));
    }

    public EcsSnapshotCaptureResult Capture(in EcsSnapshotCutView cut)
    {
        if (cut.SnapshotId == 0UL || cut.TickId == 0UL || cut.Revision == 0UL)
            return new EcsSnapshotCaptureResult(
                StorageOperationStatus.Rejected,
                null,
                new ErrorIdentity(EcsErrorCodes.InvalidArgument));

        StorageOperationResult captured = _world.CaptureReadSnapshot(
            new SnapshotId(cut.SnapshotId),
            new Revision(cut.Revision),
            out StorageReadSnapshotHandle handle);
        if (captured.Error?.Code == EcsErrorCodes.BudgetExceeded)
            return new EcsSnapshotCaptureResult(StorageOperationStatus.Retryable, null, captured.Error);
        if (!captured.IsSuccess)
            return new EcsSnapshotCaptureResult(captured.Status, null, captured.Error);
        return new EcsSnapshotCaptureResult(
            StorageOperationStatus.Accepted,
            new EcsWorldReadSnapshot(_world, handle, in cut),
            null);
    }
}
