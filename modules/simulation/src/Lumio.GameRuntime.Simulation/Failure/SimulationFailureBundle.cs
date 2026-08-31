using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using Lumio.GameRuntime.Simulation.Determinism;
using Lumio.GameRuntime.Simulation.Phases;
using Lumio.GameRuntime.Simulation.Tick;

namespace Lumio.GameRuntime.Simulation.Tick;

public enum DurableFailureEvidenceStatus
{
    NotRequired,
    Durable,
    Unavailable,
    PersistenceFailed,
    Corrupt
}

internal enum FailureBundleWriteStatus
{
    Durable,
    Unavailable,
    Rejected,
    Corrupt
}

internal enum FailureBundleReadStatus
{
    Missing,
    Found,
    Unavailable,
    Corrupt
}

internal readonly record struct FailureBundleReadResult(
    FailureBundleReadStatus Status,
    SimulationFailureBundle? Bundle);

internal interface ISimulationFailureBundlePort
{
    bool IsAvailable { get; }

    FailureBundleWriteStatus Persist(SimulationFailureBundle bundle);

    FailureBundleReadResult Read(string evidenceId);
}

internal sealed class SimulationFailureBundle
{
    private readonly IReadOnlyList<string> _preparedTokens;
    private readonly IReadOnlyList<string> _participantTokens;

    private SimulationFailureBundle(
        string sessionId,
        ulong epoch,
        PhaseFailureRecord failure,
        TickPhase? lastCompletedPhase,
        AuthoritativeTickStateSnapshot authority)
    {
        SessionId = sessionId;
        TickId = failure.TickId;
        Epoch = epoch;
        Phase = failure.Phase;
        LastCompletedPhase = lastCompletedPhase;
        GeneratedErrorId = failure.GeneratedErrorId;
        Detail = failure.Detail;
        FaultAction = "FailStop";
        CommitPointReached = failure.CommitPointReached;
        GameReleaseId = authority.GameReleaseId;
        WorldId = authority.WorldId;
        ConfigSnapshotId = authority.ConfigSnapshotId;
        ManifestHashHex = authority.ManifestHashHex;
        Revisions = authority.Revisions.Snapshot();
        _preparedTokens = Copy(authority.PreparedTokens);
        _participantTokens = Copy(authority.ParticipantTokens);
        SnapshotId = authority.SnapshotId;
        NoSnapshotReason = authority.NoSnapshotReason;
        BootstrapPhase = NoSnapshotReason is null ? null : failure.Phase.ToString();
        EvidenceId = BuildEvidenceId(this);
    }

    internal string EvidenceId { get; }

    internal string SessionId { get; }

    internal ulong TickId { get; }

    internal ulong Epoch { get; }

    internal TickPhase Phase { get; }

    internal TickPhase? LastCompletedPhase { get; }

    internal string GeneratedErrorId { get; }

    internal string Detail { get; }

    internal string FaultAction { get; }

    internal bool CommitPointReached { get; }

    internal string GameReleaseId { get; }

    internal string WorldId { get; }

    internal string ConfigSnapshotId { get; }

    internal string ManifestHashHex { get; }

    internal SimulationRevisionSnapshot Revisions { get; }

    internal IReadOnlyList<string> PreparedTokens => _preparedTokens;

    internal IReadOnlyList<string> ParticipantTokens => _participantTokens;

    internal string? SnapshotId { get; }

    internal string? NoSnapshotReason { get; }

    internal string? BootstrapPhase { get; }

    internal static SimulationFailureBundle Create(
        string sessionId,
        ulong epoch,
        PhaseFailureRecord failure,
        TickPhase? lastCompletedPhase,
        AuthoritativeTickStateSnapshot authority) =>
        new(sessionId, epoch, failure, lastCompletedPhase, authority.Snapshot());

    internal bool IsWellFormed()
    {
        bool snapshotChoiceIsValid = SnapshotId is null
            ? (NoSnapshotReason is "PreFirstSnapshot" or "BootstrapFault" or "LoaderFailed") &&
                !string.IsNullOrWhiteSpace(BootstrapPhase) &&
                BootstrapPhase.Length <= 128 &&
                string.Equals(BootstrapPhase, Phase.ToString(), StringComparison.Ordinal)
            : NoSnapshotReason is null && BootstrapPhase is null && SimulationValidation.IsIdentifier(SnapshotId);
        return SimulationValidation.IsIdentifier(EvidenceId) &&
            EvidenceId.Length == 72 &&
            string.Equals(EvidenceId, BuildEvidenceId(this), StringComparison.Ordinal) &&
            SimulationValidation.IsIdentifier(SessionId) &&
            TickId != 0 &&
            Epoch != 0 &&
            Enum.IsDefined(typeof(TickPhase), Phase) &&
            (LastCompletedPhase is null ||
                Enum.IsDefined(typeof(TickPhase), LastCompletedPhase.Value) &&
                (int)LastCompletedPhase.Value < (int)Phase) &&
            SimulationValidation.IsStableErrorId(GeneratedErrorId) &&
            !string.IsNullOrWhiteSpace(Detail) &&
            string.Equals(FaultAction, "FailStop", StringComparison.Ordinal) &&
            SimulationValidation.IsIdentifier(GameReleaseId) &&
            SimulationValidation.IsIdentifier(WorldId) &&
            SimulationValidation.IsIdentifier(ConfigSnapshotId) &&
            SimulationValidation.IsHash256(ManifestHashHex) &&
            Revisions is not null &&
            snapshotChoiceIsValid;
    }

    private static IReadOnlyList<string> Copy(IReadOnlyList<string> values)
    {
        var copy = new string[values.Count];
        for (var index = 0; index < values.Count; index++) copy[index] = values[index];
        return new ReadOnlyCollection<string>(copy);
    }

    private static string BuildEvidenceId(SimulationFailureBundle bundle)
    {
        using var stream = new MemoryStream();
        SimulationRevisionSnapshot.WriteString(stream, bundle.SessionId);
        SimulationRevisionSnapshot.WriteUInt64(stream, bundle.TickId);
        SimulationRevisionSnapshot.WriteUInt64(stream, bundle.Epoch);
        SimulationRevisionSnapshot.WriteString(stream, bundle.Phase.ToString());
        SimulationRevisionSnapshot.WriteString(stream, bundle.LastCompletedPhase?.ToString() ?? string.Empty);
        SimulationRevisionSnapshot.WriteString(stream, bundle.GeneratedErrorId);
        SimulationRevisionSnapshot.WriteString(stream, bundle.Detail);
        SimulationRevisionSnapshot.WriteString(stream, bundle.FaultAction);
        stream.WriteByte(bundle.CommitPointReached ? (byte)1 : (byte)0);
        SimulationRevisionSnapshot.WriteString(stream, bundle.GameReleaseId);
        SimulationRevisionSnapshot.WriteString(stream, bundle.WorldId);
        SimulationRevisionSnapshot.WriteString(stream, bundle.ConfigSnapshotId);
        SimulationRevisionSnapshot.WriteString(stream, bundle.ManifestHashHex);
        SimulationRevisionSnapshot.WriteString(stream, bundle.Revisions.CanonicalValue);
        WriteStrings(stream, bundle._preparedTokens);
        WriteStrings(stream, bundle._participantTokens);
        SimulationRevisionSnapshot.WriteString(stream, bundle.SnapshotId ?? string.Empty);
        SimulationRevisionSnapshot.WriteString(stream, bundle.NoSnapshotReason ?? string.Empty);
        SimulationRevisionSnapshot.WriteString(stream, bundle.BootstrapPhase ?? string.Empty);
        return "failure-" + SimulationHash.Sha256Hex(stream.ToArray());
    }

    private static void WriteStrings(Stream stream, IReadOnlyList<string> values)
    {
        SimulationRevisionSnapshot.WriteUInt64(stream, (ulong)values.Count);
        foreach (string value in values) SimulationRevisionSnapshot.WriteString(stream, value);
    }
}

internal readonly record struct FailureEvidenceReceipt(
    DurableFailureEvidenceStatus Status,
    string? EvidenceId,
    SimulationFailureBundle? Bundle)
{
    internal static FailureEvidenceReceipt Unavailable =>
        new(DurableFailureEvidenceStatus.Unavailable, null, null);
}
