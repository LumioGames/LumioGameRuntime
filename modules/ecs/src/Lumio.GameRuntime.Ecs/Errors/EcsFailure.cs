using System;
using System.Diagnostics.CodeAnalysis;

namespace Lumio.GameRuntime.Ecs;

public static class EcsErrorCodes
{
    public const string InvalidArgument = "InvalidArgument";
    public const string WrongContext = "WrongContext";
    public const string StaleEntity = "StaleEntity";
    public const string CrossWorld = "CrossWorld";
    public const string UnknownComponent = "UnknownComponent";
    public const string UnknownField = "UnknownField";
    public const string DuplicateRegistration = "DuplicateRegistration";
    public const string CapacityExceeded = "CapacityExceeded";
    public const string BudgetExceeded = "BudgetExceeded";
    public const string QueryBoundary = "QueryBoundary";
    public const string ViewExpired = "ViewExpired";
    public const string WorldNotReady = "WorldNotReady";
    public const string WorldDraining = "WorldDraining";
    public const string WorldDisposed = "WorldDisposed";
    public const string WorldFaulted = "WorldFaulted";
    public const string OwnerThreadViolation = "OwnerThreadViolation";
    public const string StructuralChangeInView = "StructuralChangeInView";
    public const string CycleDetected = "CycleDetected";
    public const string DependencyMissing = "DependencyMissing";
    public const string MutualExclusion = "MutualExclusion";
    public const string PostWriteFailure = "PostWriteFailure";
    public const string SnapshotUnavailable = "SnapshotUnavailable";
    public const string SnapshotReleased = "SnapshotReleased";
    public const string InvalidType = "InvalidType";
    public const string InvalidState = "InvalidState";
}

public enum EcsFailureClass
{
    Rejected,
    Retryable,
    Fatal,
    FatalInvariant
}

[SuppressMessage("Naming", "CA1710", Justification = "EcsFailure is the stable domain failure name.")]
public sealed class EcsFailure : Exception
{
    public EcsFailure(
        EcsFailureClass failureClass,
        string code,
        string message,
        FailureContext? context = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Class = failureClass;
        Code = code;
        Context = context;
    }

    public EcsFailureClass Class { get; }
    public string Code { get; }
    public FailureContext? Context { get; }
    public bool IsFatal => Class is EcsFailureClass.Fatal or EcsFailureClass.FatalInvariant;

    public static EcsFailure Rejected(string code, string message) => new(EcsFailureClass.Rejected, code, message);
    public static EcsFailure FatalInvariant(string code, string message, FailureContext? context = null, Exception? inner = null) => new(EcsFailureClass.FatalInvariant, code, message, context, inner);
}

public readonly record struct EcsFaultEvidence(
    ErrorIdentity Error,
    FailureContext Context,
    int PartialChangeCount,
    string? Detail = null);
