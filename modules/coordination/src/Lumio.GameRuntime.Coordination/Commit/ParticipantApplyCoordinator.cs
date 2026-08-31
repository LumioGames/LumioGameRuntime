using System;
using Lumio.GameRuntime.Command;

namespace Lumio.GameRuntime.Coordination;

internal interface ITxnParticipantApplyPort
{
    VoxelCommitParticipantResult ApplyVoxel(in VoxelCommitParticipantRequest request);

    CommandApplyReceipt ApplyEcs(PreparedGameDelta delta);
}

/// <summary>Internal ECS result seam for the participant revision returned by the real ECS adapter.</summary>
internal interface IEcsCommandCommitRevisionPort
{
    SessionRevisionVectorView? ReadResultRevision(TxnRecord record, CommandApplyReceipt receipt);
}

internal readonly record struct EcsParticipantApplyResult(
    CommandApplyReceipt Receipt,
    SessionRevisionVectorView? ResultRevision)
{
    public bool IsApplied => Receipt.IsApplied;
}

internal sealed class ParticipantApplyCoordinator
{
    private readonly IVoxelWorldPort _voxel;
    private readonly CommandModule _command;
    private readonly IEcsCommandCommitRevisionPort? _ecsRevision;

    internal ParticipantApplyCoordinator(IVoxelWorldPort voxel, EcsCommandCommitExecutor ecs)
        : this(voxel, RunningCommand(ecs), null)
    {
    }

    internal ParticipantApplyCoordinator(
        IVoxelWorldPort voxel,
        EcsCommandCommitExecutor ecs,
        IEcsCommandCommitRevisionPort? ecsRevision)
        : this(voxel, RunningCommand(ecs), ecsRevision)
    {
    }

    internal ParticipantApplyCoordinator(
        IVoxelWorldPort voxel,
        CommandModule command,
        IEcsCommandCommitRevisionPort? ecsRevision)
    {
        _voxel = voxel ?? throw new ArgumentNullException(nameof(voxel));
        _command = command ?? throw new ArgumentNullException(nameof(command));
        _ecsRevision = ecsRevision;
    }

    internal VoxelCommitParticipantResult ApplyVoxel(TxnRecord record)
    {
        if (record is null) return VoxelCommitParticipantResult.Faulted("InvalidArgument");
        if (string.IsNullOrWhiteSpace(record.PreparedVoxelToken)) return VoxelCommitParticipantResult.Faulted("InternalInvariant");
        return _voxel.Commit(new VoxelCommitParticipantRequest(
            record.SessionId, record.TxnId, record.TickId, record.PreparedVoxelToken));
    }

    internal CommandApplyReceipt ApplyEcs(TxnRecord record)
    {
        if (record?.PreparedGameDelta is null)
            return new CommandApplyReceipt(CommandApplyStatus.InfrastructureFault, 0UL, Array.Empty<byte>(), 0, "InvalidArgument");
        return _command.Apply(record.PreparedGameDelta);
    }

    internal EcsParticipantApplyResult ApplyEcsResult(TxnRecord record)
    {
        CommandApplyReceipt receipt = ApplyEcs(record);
        SessionRevisionVectorView? revision = null;
        if (receipt.IsApplied && _ecsRevision is not null)
            revision = _ecsRevision.ReadResultRevision(record, receipt);
        return new EcsParticipantApplyResult(receipt, revision);
    }

    internal VoxelCommitParticipantResult ApplyVoxel(in VoxelCommitParticipantRequest request) => _voxel.Commit(in request);

    internal CommandApplyReceipt ApplyEcs(PreparedGameDelta delta) => _command.Apply(delta);

    private static CommandModule RunningCommand(EcsCommandCommitExecutor ecs)
    {
#if NET10_0_OR_GREATER
        ArgumentNullException.ThrowIfNull(ecs);
#else
        if (ecs is null) throw new ArgumentNullException(nameof(ecs));
#endif
        CommandModule module = CommandModule.Create(executor: ecs);
        if (!module.Configure().Succeeded || !module.Start().Succeeded)
            throw new InvalidOperationException("Unable to configure the Command participant module.");
        return module;
    }
}
