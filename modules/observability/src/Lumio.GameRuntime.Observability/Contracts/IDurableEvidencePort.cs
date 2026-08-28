using System;

namespace Lumio.GameRuntime.Observability;

public interface IDurableEvidencePort
{
    DurableEnqueueResult Enqueue(in DurableRecordView record);
    DurableQueryResult Query(string idempotencyKey);
}

public readonly record struct DurableRecordView(
    string IdempotencyKey,
    string RecordType,
    ReadOnlyMemory<byte> Payload,
    CorrelationView Correlation)
{
    public bool IsWellFormed =>
        !string.IsNullOrWhiteSpace(IdempotencyKey) &&
        !string.IsNullOrWhiteSpace(RecordType) &&
        Correlation.IsComplete;
}

public enum DurableEnqueueStatus
{
    Accepted,
    Backpressured,
    Rejected,
    Closed
}

public readonly record struct DurableEnqueueResult(
    DurableEnqueueStatus Status,
    ulong RecordSequence,
    bool AlreadyPresent,
    string? GeneratedErrorId)
{
    public bool IsAccepted => Status == DurableEnqueueStatus.Accepted;
}

public enum DurableQueryStatus
{
    Found,
    NotFound,
    Rejected,
    Closed
}

public readonly record struct DurableQueryResult(
    DurableQueryStatus Status,
    DurableRecordView? Record,
    string? GeneratedErrorId);
