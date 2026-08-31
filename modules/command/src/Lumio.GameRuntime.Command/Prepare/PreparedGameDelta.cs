using System;
using System.Collections.Generic;

namespace Lumio.GameRuntime.Command;

/// <summary>Immutable result of all command business validation.</summary>
public sealed class PreparedGameDelta
{
    private readonly IReadOnlyList<Command> _commands;
    private readonly byte[] _canonicalDigest;

    internal PreparedGameDelta(
        MergedCommandBatch batch,
        CommandReservationSet reservations,
        int schemaEpoch,
        DeferredEntityMap resolutionPlan)
    {
        Batch = batch ?? throw new ArgumentNullException(nameof(batch));
        Reservations = reservations ?? throw new ArgumentNullException(nameof(reservations));
        ResolutionPlan = resolutionPlan ?? throw new ArgumentNullException(nameof(resolutionPlan));
        TickId = batch.TickId;
        SchemaEpoch = schemaEpoch;
        _commands = new List<Command>(batch.Commands).AsReadOnly();
        _canonicalDigest = batch.CanonicalDigest.ToArray();
    }

    public MergedCommandBatch Batch { get; }

    public IReadOnlyList<Command> Commands => _commands;

    public CommandReservationSet Reservations { get; }

    public DeferredEntityMap ResolutionPlan { get; }

    public ulong TickId { get; }

    public int SchemaEpoch { get; }

    public ReadOnlyMemory<byte> CanonicalDigest => _canonicalDigest;

    public string CanonicalDigestHex => CommandHashing.ToHex(_canonicalDigest);

    public string IdempotencyKey => string.Concat(TickId.ToString(System.Globalization.CultureInfo.InvariantCulture), ":", CanonicalDigestHex);

    public ulong? ExpectedGameRevision { get; init; }

    public ulong? ExpectedVoxelRevision { get; init; }

    public bool IsValid =>
        Batch.State == CommandBufferState.Prepared &&
        !Reservations.IsReleased &&
        ResolutionPlan.TickId == TickId && string.Equals(ResolutionPlan.WorldId, Batch.WorldId, StringComparison.Ordinal);

    public bool VerifyForApply() => IsValid && _canonicalDigest.Length == 32;

    public CommandBufferState State => Batch.State;

    public static PreparedGameDelta Create(
        MergedCommandBatch batch,
        int schemaEpoch,
        CommandReservationSet? reservations = null,
        DeferredEntityMap? resolutionPlan = null,
        ulong? expectedGameRevision = null,
        ulong? expectedVoxelRevision = null)
    {
#if NET10_0_OR_GREATER
        ArgumentNullException.ThrowIfNull(batch);
#else
        if (batch is null) throw new ArgumentNullException(nameof(batch));
#endif
        reservations ??= new CommandReservationSet(0UL, (ulong)batch.Commands.Count, 0UL);
        resolutionPlan ??= new DeferredEntityMap(batch.TickId, batch.WorldId);
        if (batch.State == CommandBufferState.Merged) batch.MarkPrepared();
        return new PreparedGameDelta(batch, reservations, schemaEpoch, resolutionPlan)
        {
            ExpectedGameRevision = expectedGameRevision,
            ExpectedVoxelRevision = expectedVoxelRevision
        };
    }
}

public enum CommandPreflightStatus
{
    Prepared,
    Rejected,
    Retryable,
    Fatal
}

public readonly record struct CommandPreflightResult(
    CommandPreflightStatus Status,
    PreparedGameDelta? Delta,
    CommandFailure? Failure)
{
    public bool IsPrepared => Status == CommandPreflightStatus.Prepared && Delta is not null;
}
