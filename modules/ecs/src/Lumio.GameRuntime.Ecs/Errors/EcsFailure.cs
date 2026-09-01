using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using Lumio.Gen.ContractTypes;

namespace Lumio.GameRuntime.Ecs;

internal enum EcsDiagnosticReason
{
    InvalidArgument,
    WrongContext,
    StaleEntity,
    CrossWorld,
    UnknownComponent,
    UnknownField,
    DuplicateRegistration,
    CapacityExceeded,
    BudgetExceeded,
    QueryBoundary,
    ViewExpired,
    WorldNotReady,
    WorldDraining,
    WorldDisposed,
    WorldFaulted,
    OwnerThreadViolation,
    StructuralChangeInView,
    CycleDetected,
    DependencyMissing,
    MutualExclusion,
    PostWriteFailure,
    SnapshotUnavailable,
    SnapshotReleased,
    SnapshotDoubleRelease,
    InvalidType,
    InvalidState
}

internal static class EcsBoundaryErrors
{
    private static readonly HashSet<string> StableErrors = new(Catalog.StableErrorIds, StringComparer.Ordinal);

    internal static string For(EcsDiagnosticReason reason) => reason switch
    {
        EcsDiagnosticReason.WrongContext or
        EcsDiagnosticReason.CrossWorld or
        EcsDiagnosticReason.QueryBoundary or
        EcsDiagnosticReason.WorldNotReady or
        EcsDiagnosticReason.OwnerThreadViolation or
        EcsDiagnosticReason.StructuralChangeInView => "WrongContext",
        EcsDiagnosticReason.CapacityExceeded => "CapacityExceeded",
        EcsDiagnosticReason.BudgetExceeded => "BudgetExceeded",
        EcsDiagnosticReason.WorldDraining => "ContextClosing",
        EcsDiagnosticReason.WorldDisposed => "ContextDestroyed",
        EcsDiagnosticReason.StaleEntity or
        EcsDiagnosticReason.ViewExpired or
        EcsDiagnosticReason.SnapshotReleased => "InvalidHandle",
        EcsDiagnosticReason.SnapshotDoubleRelease => "HandleDoubleRelease",
        EcsDiagnosticReason.SnapshotUnavailable => "TargetRevisionUnavailable",
        EcsDiagnosticReason.WorldFaulted or
        EcsDiagnosticReason.PostWriteFailure or
        EcsDiagnosticReason.InvalidState => "InternalInvariant",
        _ => "InvalidArgument"
    };

    internal static bool IsGeneratedStableError(string code) => StableErrors.Contains(code);
}

internal static class EcsErrorCodes
{
    internal static readonly string InvalidArgument = EcsBoundaryErrors.For(EcsDiagnosticReason.InvalidArgument);
    internal static readonly string WrongContext = EcsBoundaryErrors.For(EcsDiagnosticReason.WrongContext);
    internal static readonly string StaleEntity = EcsBoundaryErrors.For(EcsDiagnosticReason.StaleEntity);
    internal static readonly string CrossWorld = EcsBoundaryErrors.For(EcsDiagnosticReason.CrossWorld);
    internal static readonly string UnknownComponent = EcsBoundaryErrors.For(EcsDiagnosticReason.UnknownComponent);
    internal static readonly string UnknownField = EcsBoundaryErrors.For(EcsDiagnosticReason.UnknownField);
    internal static readonly string DuplicateRegistration = EcsBoundaryErrors.For(EcsDiagnosticReason.DuplicateRegistration);
    internal static readonly string CapacityExceeded = EcsBoundaryErrors.For(EcsDiagnosticReason.CapacityExceeded);
    internal static readonly string BudgetExceeded = EcsBoundaryErrors.For(EcsDiagnosticReason.BudgetExceeded);
    internal static readonly string QueryBoundary = EcsBoundaryErrors.For(EcsDiagnosticReason.QueryBoundary);
    internal static readonly string ViewExpired = EcsBoundaryErrors.For(EcsDiagnosticReason.ViewExpired);
    internal static readonly string WorldNotReady = EcsBoundaryErrors.For(EcsDiagnosticReason.WorldNotReady);
    internal static readonly string WorldDraining = EcsBoundaryErrors.For(EcsDiagnosticReason.WorldDraining);
    internal static readonly string WorldDisposed = EcsBoundaryErrors.For(EcsDiagnosticReason.WorldDisposed);
    internal static readonly string WorldFaulted = EcsBoundaryErrors.For(EcsDiagnosticReason.WorldFaulted);
    internal static readonly string OwnerThreadViolation = EcsBoundaryErrors.For(EcsDiagnosticReason.OwnerThreadViolation);
    internal static readonly string StructuralChangeInView = EcsBoundaryErrors.For(EcsDiagnosticReason.StructuralChangeInView);
    internal static readonly string CycleDetected = EcsBoundaryErrors.For(EcsDiagnosticReason.CycleDetected);
    internal static readonly string DependencyMissing = EcsBoundaryErrors.For(EcsDiagnosticReason.DependencyMissing);
    internal static readonly string MutualExclusion = EcsBoundaryErrors.For(EcsDiagnosticReason.MutualExclusion);
    internal static readonly string PostWriteFailure = EcsBoundaryErrors.For(EcsDiagnosticReason.PostWriteFailure);
    internal static readonly string SnapshotUnavailable = EcsBoundaryErrors.For(EcsDiagnosticReason.SnapshotUnavailable);
    internal static readonly string SnapshotReleased = EcsBoundaryErrors.For(EcsDiagnosticReason.SnapshotReleased);
    internal static readonly string SnapshotDoubleRelease = EcsBoundaryErrors.For(EcsDiagnosticReason.SnapshotDoubleRelease);
    internal static readonly string InvalidType = EcsBoundaryErrors.For(EcsDiagnosticReason.InvalidType);
    internal static readonly string InvalidState = EcsBoundaryErrors.For(EcsDiagnosticReason.InvalidState);
}

internal enum EcsFailureClass
{
    Rejected,
    Retryable,
    Fatal,
    FatalInvariant
}

[SuppressMessage("Naming", "CA1710", Justification = "EcsFailure is the stable domain failure name.")]
internal sealed class EcsFailure : Exception
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

internal readonly record struct EcsFaultEvidence(
    ErrorIdentity Error,
    FailureContext Context,
    int PartialChangeCount,
    string? Detail = null);

internal readonly record struct EcsFieldWrite(
    LocalEntityId Entity,
    ComponentTypeId ComponentType,
    ComponentFieldId Field,
    ReadOnlyMemory<byte> CanonicalValue,
    EcsOperationEvidence Evidence = default);

internal interface IEcsChangeSetAppend
{
    StorageOperationResult TryAppend(in ChangeEntry entry);
}

internal interface IEcsDurableFailureSink
{
    void Publish(in EcsFaultEvidence evidence);
}

internal sealed class NullEcsDurableFailureSink : IEcsDurableFailureSink
{
    public static NullEcsDurableFailureSink Instance { get; } = new();

    public void Publish(in EcsFaultEvidence evidence)
    {
    }
}

internal sealed class EcsFailStopController
{
    private readonly object _gate = new();
    private readonly IEcsDurableFailureSink _sink;
    private EcsFaultEvidence? _first;

    public EcsFailStopController(IEcsDurableFailureSink? sink = null)
    {
        _sink = sink ?? NullEcsDurableFailureSink.Instance;
    }

    public EcsFaultEvidence? First
    {
        get { lock (_gate) return _first; }
    }

    public static string Hash(
        TickId tickId,
        ProcessorId? processorId,
        LocalEntityId entity,
        ComponentTypeId componentType,
        ComponentFieldId field)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(tickId.Value);
            writer.Write(processorId?.Value ?? 0UL);
            writer.Write(entity.Index);
            writer.Write(entity.Generation);
            writer.Write(componentType.Value);
            writer.Write(field.Value);
            writer.Flush();
        }

        return CanonicalHash.Of(stream.ToArray());
    }

    public static string ToGeneratedFatalCode(string? requestedCode, Exception? exception)
    {
        if (!string.IsNullOrWhiteSpace(requestedCode) &&
            EcsBoundaryErrors.IsGeneratedStableError(requestedCode) &&
            (exception is null || !string.Equals(requestedCode, exception.Message, StringComparison.Ordinal)))
            return requestedCode;
        return EcsErrorCodes.InvalidState;
    }

    public StorageOperationResult CaptureAdapterFailure(
        StorageOperationResult result,
        ref EcsWorldState state,
        in FailureContext context,
        int partialChangeCount,
        Exception? exception = null)
    {
        if (result.Status is not StorageOperationStatus.Fatal and not StorageOperationStatus.Indeterminate)
            return result;
#if NET10_0_OR_GREATER
        ArgumentOutOfRangeException.ThrowIfNegative(partialChangeCount);
#else
        if (partialChangeCount < 0) throw new ArgumentOutOfRangeException(nameof(partialChangeCount));
#endif

        string code = ToGeneratedFatalCode(result.Error?.Code, exception);
        string identity = string.IsNullOrWhiteSpace(context.EvidenceIdentity)
            ? Hash(context.TickId, context.ProcessorId, context.Entity, context.ComponentType, context.Field)
            : context.EvidenceIdentity!;
        var evidence = new EcsFaultEvidence(
            new ErrorIdentity(code),
            context with { EvidenceIdentity = identity },
            partialChangeCount,
            exception?.GetType().FullName);

        lock (_gate)
        {
            if (state != EcsWorldState.Disposed && state != EcsWorldState.Faulted)
            {
                state = EcsWorldState.Faulted;
                _first = evidence;
                _sink.Publish(evidence);
            }
        }

        return StorageOperationResult.Fatal(code);
    }
}
