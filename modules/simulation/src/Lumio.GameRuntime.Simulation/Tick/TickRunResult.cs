using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using Lumio.GameRuntime.Simulation.Phases;

namespace Lumio.GameRuntime.Simulation.Tick;

public enum TickRunStatus
{
    Succeeded,
    IdempotentSame,
    Rejected,
    Retryable,
    Faulted,
    PostCommitFaulted,
    Committed = Succeeded,
    AlreadyCommitted = IdempotentSame
}

public sealed record SimulationFailure(
    PhaseFailureClass Class,
    string GeneratedErrorId,
    string Detail)
{
    public static SimulationFailure Rejected(string errorId, string detail) => new(PhaseFailureClass.BusinessReject, errorId, detail);

    public static SimulationFailure Fatal(string errorId, string detail) => new(PhaseFailureClass.ProcessFault, errorId, detail);
}

public sealed record PhaseFailureRecord(
    ulong TickId,
    TickPhase Phase,
    string? ProcessorId,
    string GeneratedErrorId,
    string Detail,
    bool CommitPointReached);

public readonly record struct PhaseExecutionRecord(
    TickPhase Phase,
    bool Entered,
    bool Completed,
    bool AuthoritativeCommitPoint,
    PhaseFailureRecord? Error);

public sealed class TickRunResult
{
    internal TickRunResult(
        ulong tickId,
        TickRunStatus status,
        bool committed,
        string requestHashHex,
        string stateHashHex,
        IReadOnlyList<TickPhase> phaseTrace,
        IReadOnlyList<PhaseExecutionRecord> phaseRecords,
        PhaseFailureRecord? firstFailure,
        IReadOnlyList<OpaqueOutputView> outputs,
        string? generatedErrorId,
        TickRunResult? cachedResult,
        DurableFailureEvidenceStatus failureEvidenceStatus = DurableFailureEvidenceStatus.NotRequired,
        string? failureEvidenceId = null,
        SimulationFailureBundle? failureBundle = null)
    {
        TickId = tickId;
        Status = status;
        IsCommitted = committed;
        RequestHashHex = requestHashHex;
        StateHashHex = stateHashHex;
        PhaseTrace = new ReadOnlyCollection<TickPhase>(new List<TickPhase>(phaseTrace));
        PhaseRecords = new ReadOnlyCollection<PhaseExecutionRecord>(new List<PhaseExecutionRecord>(phaseRecords));
        FirstFailure = firstFailure;
        var outputCopies = new List<OpaqueOutputView>(outputs.Count);
        foreach (OpaqueOutputView output in outputs) outputCopies.Add(output.Snapshot());
        Outputs = new ReadOnlyCollection<OpaqueOutputView>(outputCopies);
        GeneratedErrorId = generatedErrorId;
        CachedResult = cachedResult;
        FailureEvidenceStatus = failureEvidenceStatus;
        FailureEvidenceId = failureEvidenceId;
        FailureBundle = failureBundle;
    }

    public ulong TickId { get; }

    public TickRunStatus Status { get; }

    public bool IsCommitted { get; }

    public string RequestHashHex { get; }

    public string StateHashHex { get; }

    public ReadOnlyMemory<byte> StateHash
    {
        get
        {
            if (string.IsNullOrEmpty(StateHashHex)) return ReadOnlyMemory<byte>.Empty;
            var bytes = new byte[StateHashHex.Length / 2];
            for (var i = 0; i < bytes.Length; i++) bytes[i] = Convert.ToByte(StateHashHex.Substring(i * 2, 2), 16);
            return bytes;
        }
    }

    public string RequestDigest => RequestHashHex;

    public IReadOnlyList<TickPhase> PhaseTrace { get; }

    public IReadOnlyList<PhaseExecutionRecord> PhaseRecords { get; }

    public PhaseFailureRecord? FirstFailure { get; }

    public IReadOnlyList<OpaqueOutputView> Outputs { get; }

    public string? GeneratedErrorId { get; }

    public TickRunResult? CachedResult { get; }

    public DurableFailureEvidenceStatus FailureEvidenceStatus { get; }

    public string? FailureEvidenceId { get; }

    internal SimulationFailureBundle? FailureBundle { get; }

    public IReadOnlyList<TickPhase> Phases => PhaseTrace;

    public SimulationFailure? Error => FirstFailure is null ? null : new SimulationFailure(
        Status == TickRunStatus.Rejected
            ? PhaseFailureClass.BusinessReject
            : PhaseFailureClass.ProcessFault,
        FirstFailure.GeneratedErrorId,
        FirstFailure.Detail);

    public bool Succeeded => Status is TickRunStatus.Succeeded or TickRunStatus.IdempotentSame;

    public static TickRunResult Rejected(ulong tickId, string errorId, string detail)
    {
        var failure = new PhaseFailureRecord(tickId, TickPhase.IngressCapture, null, errorId, detail, false);
        return new TickRunResult(tickId, TickRunStatus.Rejected, false, string.Empty, string.Empty, Array.Empty<TickPhase>(), Array.Empty<PhaseExecutionRecord>(), failure, Array.Empty<OpaqueOutputView>(), errorId, null);
    }

    internal static TickRunResult Rejected(TickExecutionContext context, string requestHashHex, PhaseFailureRecord failure) =>
        new(context.Request.TickId, TickRunStatus.Rejected, false, requestHashHex, string.Empty, context.PhaseTrace, context.PhaseRecords, failure, Array.Empty<OpaqueOutputView>(), failure.GeneratedErrorId, null);

    internal static TickRunResult Success(TickExecutionContext context, string requestHashHex, string stateHashHex) =>
        new(context.Request.TickId, TickRunStatus.Succeeded, context.IsCommitted, requestHashHex, stateHashHex, context.PhaseTrace, context.PhaseRecords, null, context.Outputs, null, null);

    internal static TickRunResult Faulted(
        TickExecutionContext context,
        string requestHashHex,
        PhaseFailureRecord failure,
        string stateHashHex,
        FailureEvidenceReceipt evidence) =>
        new(
            context.Request.TickId,
            context.IsCommitted ? TickRunStatus.PostCommitFaulted : TickRunStatus.Faulted,
            context.IsCommitted,
            requestHashHex,
            stateHashHex,
            context.PhaseTrace,
            context.PhaseRecords,
            failure,
            context.IsCommitted ? context.Outputs : Array.Empty<OpaqueOutputView>(),
            failure.GeneratedErrorId,
            null,
            evidence.Status,
            evidence.EvidenceId,
            evidence.Bundle);

    internal static TickRunResult Faulted(TickExecutionContext context, string requestHashHex, PhaseFailureRecord failure) =>
        Faulted(context, requestHashHex, failure, string.Empty, default);

    internal static TickRunResult Faulted(
        ulong tickId,
        string requestHashHex,
        PhaseFailureRecord failure,
        FailureEvidenceReceipt evidence) =>
        new(
            tickId,
            TickRunStatus.Faulted,
            false,
            requestHashHex,
            string.Empty,
            Array.Empty<TickPhase>(),
            Array.Empty<PhaseExecutionRecord>(),
            failure,
            Array.Empty<OpaqueOutputView>(),
            failure.GeneratedErrorId,
            null,
            evidence.Status,
            evidence.EvidenceId,
            evidence.Bundle);

    internal static TickRunResult Faulted(ulong tickId, string requestHashHex, PhaseFailureRecord failure) =>
        Faulted(tickId, requestHashHex, failure, default);

    internal TickRunResult AsIdempotent() =>
        new(
            TickId,
            Status == TickRunStatus.Faulted ? TickRunStatus.Faulted : TickRunStatus.IdempotentSame,
            IsCommitted,
            RequestHashHex,
            StateHashHex,
            PhaseTrace,
            PhaseRecords,
            FirstFailure,
            Outputs,
            GeneratedErrorId,
            this,
            FailureEvidenceStatus,
            FailureEvidenceId,
            FailureBundle);

    internal TickRunResult SnapshotForPersistence() =>
        new(
            TickId,
            Status,
            IsCommitted,
            RequestHashHex,
            StateHashHex,
            PhaseTrace,
            PhaseRecords,
            FirstFailure,
            Outputs,
            GeneratedErrorId,
            null,
            FailureEvidenceStatus,
            FailureEvidenceId,
            FailureBundle);
}
