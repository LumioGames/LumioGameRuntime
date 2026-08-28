namespace Lumio.GameRuntime.Observability;

internal enum ObservabilityFailureClass
{
    Rejected,
    Retryable,
    Fatal,
    FatalInvariant
}

internal readonly record struct ObservabilityFailure(
    ObservabilityFailureClass Class,
    string GeneratedErrorId,
    string? EvidenceReference);
