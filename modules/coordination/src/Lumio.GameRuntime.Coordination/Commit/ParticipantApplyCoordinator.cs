using System;
using Lumio.GameRuntime.Command;

namespace Lumio.GameRuntime.Coordination;

public interface ITxnParticipantApplyPort
{
    VoxelCommitParticipantResult ApplyVoxel(in VoxelCommitParticipantRequest request);

    CommandApplyReceipt ApplyEcs(PreparedGameDelta delta);
}

public sealed class ParticipantApplyCoordinator
{
    private readonly IVoxelWorldPort _voxel;
    private readonly EcsCommandCommitExecutor _ecs;

    public ParticipantApplyCoordinator(IVoxelWorldPort voxel, EcsCommandCommitExecutor ecs)
    {
        _voxel = voxel ?? throw new ArgumentNullException(nameof(voxel));
        _ecs = ecs ?? throw new ArgumentNullException(nameof(ecs));
    }

    public VoxelCommitParticipantResult ApplyVoxel(TxnRecord record)
    {
        if (record is null) return VoxelCommitParticipantResult.Faulted("InvalidArgument");
        if (string.IsNullOrWhiteSpace(record.PreparedVoxelToken)) return VoxelCommitParticipantResult.Faulted("InternalInvariant");
        return _voxel.Commit(new VoxelCommitParticipantRequest(
            record.SessionId, record.TxnId, record.TickId, record.PreparedVoxelToken));
    }

    public CommandApplyReceipt ApplyEcs(TxnRecord record)
    {
        if (record?.PreparedGameDelta is null)
            return new CommandApplyReceipt(CommandApplyStatus.InfrastructureFault, 0UL, Array.Empty<byte>(), 0, "InvalidArgument");
        return _ecs.Apply(record.PreparedGameDelta);
    }

    public VoxelCommitParticipantResult ApplyVoxel(in VoxelCommitParticipantRequest request) => _voxel.Commit(in request);

    public CommandApplyReceipt ApplyEcs(PreparedGameDelta delta) => _ecs.Apply(delta);
}
