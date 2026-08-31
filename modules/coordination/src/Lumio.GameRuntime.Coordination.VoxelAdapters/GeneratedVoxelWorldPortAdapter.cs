using System;
using System.Collections.Generic;
using Lumio.GameRuntime.Coordination;

namespace Lumio.GameRuntime.Coordination.VoxelAdapters;

/// <summary>Generated-contract-shaped adapter boundary; native/storage types never cross it.</summary>
// The pinned generated package exposes the voxel schema and metadata only; it
// does not provide a callable port binding. Keep this substitute seam private
// until the architecture generator publishes that binding.
internal interface IGeneratedVoxelWorldPort
{
    GeneratedVoxelPrepareResult Prepare(in GeneratedVoxelPrepareRequest request);

    GeneratedVoxelCommitResult Commit(in GeneratedVoxelCommitRequest request);

    GeneratedVoxelAbortResult Abort(in GeneratedVoxelAbortRequest request);

    GeneratedVoxelQueryResult Query(string sessionId, string txnId);

    SessionRevisionVectorView ReadRevision();
}

internal readonly record struct GeneratedVoxelPrepareRequest(
    string SessionId,
    string TxnId,
    ulong TickId,
    ulong DeadlineTick,
    ulong ExpectedVoxelRevision,
    IReadOnlyDictionary<string, ulong> ExpectedChunkRevisionSet,
    int SchemaEpoch,
    ReadOnlyMemory<byte> PreparedDeltaDigest);

internal enum GeneratedVoxelPrepareStatus
{
    Prepared,
    Rejected,
    Retryable,
    Fatal
}

internal readonly record struct GeneratedVoxelPrepareResult(
    GeneratedVoxelPrepareStatus Status,
    string? Token,
    ulong LeaseDeadlineTick,
    string? GeneratedErrorId)
{
    internal static GeneratedVoxelPrepareResult Prepared(string token, ulong deadlineTick) =>
        new(GeneratedVoxelPrepareStatus.Prepared, token, deadlineTick, null);
}

internal readonly record struct GeneratedVoxelCommitRequest(string SessionId, string TxnId, ulong TickId, string Token);

internal enum GeneratedVoxelCommitStatus
{
    Applied,
    AlreadyApplied,
    Rejected,
    Indeterminate,
    Faulted
}

internal readonly record struct GeneratedVoxelCommitResult(
    GeneratedVoxelCommitStatus Status,
    SessionRevisionVectorView? ResultRevision,
    string? GeneratedErrorId);

internal readonly record struct GeneratedVoxelAbortRequest(string SessionId, string TxnId, string? Token);

internal readonly record struct GeneratedVoxelAbortResult(bool Succeeded, string? GeneratedErrorId);

internal readonly record struct GeneratedVoxelQueryResult(
    TxnParticipantState State,
    bool Available,
    string? GeneratedErrorId,
    SessionRevisionVectorView? ResultRevision);

internal sealed class GeneratedVoxelWorldPortAdapter : IVoxelWorldPort
{
    private readonly IGeneratedVoxelWorldPort _inner;

    internal GeneratedVoxelWorldPortAdapter(IGeneratedVoxelWorldPort inner)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    }

    VoxelPrepareResult IVoxelWorldPort.Prepare(in VoxelPrepareRequest request)
    {
        GeneratedVoxelPrepareResult result = _inner.Prepare(new GeneratedVoxelPrepareRequest(
            request.SessionId,
            request.TxnId,
            request.TickId,
            request.DeadlineTick,
            request.ExpectedVoxelRevision,
            new Dictionary<string, ulong>(request.ExpectedChunkRevisionSet, StringComparer.Ordinal),
            request.SchemaEpoch,
            request.Delta.CanonicalDigest));

        return result.Status switch
        {
            GeneratedVoxelPrepareStatus.Prepared when result.Token is not null =>
                VoxelPrepareResult.Prepared(result.Token, result.LeaseDeadlineTick),
            GeneratedVoxelPrepareStatus.Retryable => VoxelPrepareResult.Retryable(result.GeneratedErrorId ?? "QueueFull", "Voxel prepare is temporarily unavailable."),
            GeneratedVoxelPrepareStatus.Fatal => VoxelPrepareResult.Fatal(result.GeneratedErrorId ?? "PanicBoundary", "Voxel prepare failed fatally."),
            _ => VoxelPrepareResult.Rejected(result.GeneratedErrorId ?? "InvalidArgument", "Voxel prepare was rejected.")
        };
    }

    VoxelCommitParticipantResult IVoxelWorldPort.Commit(in VoxelCommitParticipantRequest request)
    {
        GeneratedVoxelCommitResult result = _inner.Commit(new GeneratedVoxelCommitRequest(
            request.SessionId, request.TxnId, request.TickId, request.PreparedVoxelToken));
        return result.Status switch
        {
            GeneratedVoxelCommitStatus.Applied => VoxelCommitParticipantResult.Applied(result.ResultRevision),
            GeneratedVoxelCommitStatus.AlreadyApplied => VoxelCommitParticipantResult.AlreadyApplied(result.ResultRevision),
            GeneratedVoxelCommitStatus.Indeterminate => VoxelCommitParticipantResult.Indeterminate(result.GeneratedErrorId ?? "PanicBoundary"),
            GeneratedVoxelCommitStatus.Faulted => VoxelCommitParticipantResult.Faulted(result.GeneratedErrorId ?? "PanicBoundary"),
            _ => VoxelCommitParticipantResult.Rejected(result.GeneratedErrorId ?? "InvalidArgument")
        };
    }

    VoxelAbortParticipantResult IVoxelWorldPort.Abort(in VoxelAbortParticipantRequest request)
    {
        GeneratedVoxelAbortResult result = _inner.Abort(new GeneratedVoxelAbortRequest(request.SessionId, request.TxnId, request.PreparedVoxelToken));
        return new VoxelAbortParticipantResult(result.Succeeded, result.GeneratedErrorId);
    }

    VoxelParticipantQueryResult IVoxelWorldPort.Query(string sessionId, string txnId)
    {
        GeneratedVoxelQueryResult result = _inner.Query(sessionId, txnId);
        return new VoxelParticipantQueryResult(result.State, result.Available, result.GeneratedErrorId, result.ResultRevision);
    }

    SessionRevisionVectorView IVoxelWorldPort.ReadRevision() => _inner.ReadRevision();
}
