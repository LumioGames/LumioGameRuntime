using System;

namespace Lumio.GameRuntime.Coordination;

public enum CoordinationFailureClass
{
    Rejected,
    Retryable,
    Fatal,
    InfrastructureFault
}

public sealed record CoordinationFailure(
    CoordinationFailureClass Class,
    string GeneratedErrorId,
    string Detail,
    string? Evidence = null)
{
    public bool HasStableErrorId => errorIdIsStable(GeneratedErrorId);
    public static CoordinationFailure Rejected(string errorId, string detail, string? evidence = null) =>
        new(CoordinationFailureClass.Rejected, errorId, detail, evidence);

    public static CoordinationFailure Retryable(string errorId, string detail, string? evidence = null) =>
        new(CoordinationFailureClass.Retryable, errorId, detail, evidence);

    public static CoordinationFailure Fatal(string errorId, string detail, string? evidence = null) =>
        new(CoordinationFailureClass.Fatal, errorId, detail, evidence);

    public static CoordinationFailure Infrastructure(string errorId, string detail, string? evidence = null) =>
        new(CoordinationFailureClass.InfrastructureFault, errorId, detail, evidence);

    private static bool errorIdIsStable(string value) => value switch
    {
        "RevisionConflict" or "MaintenanceKick" or "ReleaseMismatch" or "NativeAbiMismatch" or "StaleEpoch" or
        "FencingTokenStale" or "ManifestMalformed" or "ManifestUnsupportedVersion" or "ArtifactMissing" or
        "ArtifactDigestMismatch" or "SignatureMissing" or "SignatureInvalid" or "TrustRootUnknown" or
        "TrustPolicyRejected" or "KeyRevoked" or "EvidenceMissing" or "EvidenceDigestMismatch" or
        "TargetProfileMismatch" or "CapabilityMissing" or "SymbolMissing" or "SymbolCollision" or
        "PackageIdentityConflict" or "WorkerPoolDuplicate" or "LoaderTimeout" or "LoaderCancelled" or
        "LoaderOutOfMemory" or "PartialLoadRolledBack" or "InvalidHandle" or "HandleDoubleRelease" or
        "MessagePermissionDenied" or "StaleConnectionGeneration" or "ChunkUnavailable" or
        "TargetRevisionUnavailable" or "BudgetExceeded" or "QueueFull" or "CoordinateOutOfBounds" or
        "DirtyChunkNotDurable" or "SnapshotBaseMismatch" or "SessionMismatch" or "RoleMismatch" or
        "ClaimNotGranted" or "SessionAntiReplay" or "InvalidArgument" or "WrongContext" or
        "BufferTooSmall" or "CapacityExceeded" or "Cancelled" or "TimedOut" or "ContextClosing" or
        "ContextDestroyed" or "PanicBoundary" or "InternalInvariant" => true,
        _ => false
    };
}
