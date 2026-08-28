using System;

namespace Lumio.GameRuntime.Observability;

public readonly record struct DiagnosticQueueBudget(int Capacity, long MaxBytes)
{
    public bool IsValid => Capacity > 0 && MaxBytes > 0;
}

internal enum DiagnosticWriteStatus
{
    Accepted,
    DroppedBestEffort,
    Rejected,
    Closed
}

internal readonly record struct DiagnosticWriteResult(DiagnosticWriteStatus Status, string? GeneratedErrorId)
{
    public static DiagnosticWriteResult Accepted() => new(DiagnosticWriteStatus.Accepted, null);

    public static DiagnosticWriteResult DroppedBestEffort() =>
        new(DiagnosticWriteStatus.DroppedBestEffort, null);

    public static DiagnosticWriteResult Rejected(string generatedErrorId) =>
        new(DiagnosticWriteStatus.Rejected, generatedErrorId);

    public static DiagnosticWriteResult Closed() => new(DiagnosticWriteStatus.Closed, "ManifestMalformed");
}

public readonly record struct DiagnosticDropSummary(long DroppedCount, long DroppedBytes, string Reason);
