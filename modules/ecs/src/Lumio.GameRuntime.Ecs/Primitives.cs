using System;
using Lumio.Gen.ContractTypes;

namespace Lumio.GameRuntime.Ecs;

public readonly record struct WorldId(ulong Value) : IComparable<WorldId>
{
    public bool IsDefault => Value == 0;
    public int CompareTo(WorldId other) => Value.CompareTo(other.Value);
    public static implicit operator WorldId(uint value) => new(value);
    public static implicit operator WorldId(ulong value) => new(value);
    public static bool operator <(WorldId left, WorldId right) => left.Value < right.Value;
    public static bool operator >(WorldId left, WorldId right) => left.Value > right.Value;
    public static bool operator <=(WorldId left, WorldId right) => left.Value <= right.Value;
    public static bool operator >=(WorldId left, WorldId right) => left.Value >= right.Value;
}

internal readonly record struct TickId(ulong Value) : IComparable<TickId> { public int CompareTo(TickId other) => Value.CompareTo(other.Value); }
internal readonly record struct SnapshotId(ulong Value) : IComparable<SnapshotId> { public int CompareTo(SnapshotId other) => Value.CompareTo(other.Value); }
internal readonly record struct ProcessorId(ulong Value) : IComparable<ProcessorId> { public int CompareTo(ProcessorId other) => Value.CompareTo(other.Value); }
internal readonly record struct ComponentTypeId(ulong Value) : IComparable<ComponentTypeId> { public int CompareTo(ComponentTypeId other) => Value.CompareTo(other.Value); }
internal readonly record struct ComponentFieldId(ulong Value) : IComparable<ComponentFieldId> { public int CompareTo(ComponentFieldId other) => Value.CompareTo(other.Value); }
internal readonly record struct Revision(ulong Value) : IComparable<Revision> { public int CompareTo(Revision other) => Value.CompareTo(other.Value); }

public readonly record struct ErrorIdentity
{
    public ErrorIdentity(string code)
    {
        if (string.IsNullOrWhiteSpace(code) || !EcsBoundaryErrors.IsGeneratedStableError(code)) throw new ArgumentOutOfRangeException(nameof(code), code, "Error identity must come from generated stable error catalog.");
        Code = code;
    }
    public string Code { get; }
    public override string ToString() => Code ?? string.Empty;
}

public enum StorageOperationStatus { Accepted, Rejected, AlreadyApplied, Retryable, Fatal, Indeterminate }
public readonly record struct StorageOperationResult(StorageOperationStatus Status, ErrorIdentity? Error = null)
{
    public bool IsSuccess => Status is StorageOperationStatus.Accepted or StorageOperationStatus.AlreadyApplied;
    public static StorageOperationResult Accepted() => new(StorageOperationStatus.Accepted);
    public static StorageOperationResult Rejected(string code) => new(StorageOperationStatus.Rejected, new ErrorIdentity(code));
    public static StorageOperationResult Retryable(string code) => new(StorageOperationStatus.Retryable, new ErrorIdentity(code));
    public static StorageOperationResult Fatal(string code) => new(StorageOperationStatus.Fatal, new ErrorIdentity(code));
}

public readonly record struct EcsBudget(int MaxEntities, int MaxQueryResults, int MaxChangeEntries, int MaxSnapshotBytes)
{
    public static EcsBudget Default => new(65_536, 65_536, 65_536, 16 * 1024 * 1024);
    public bool IsValid => MaxEntities > 0 && MaxQueryResults > 0 && MaxChangeEntries > 0 && MaxSnapshotBytes > 0;
}

internal readonly record struct FailureContext(WorldId WorldId, TickId TickId, ProcessorId? ProcessorId, LocalEntityId Entity, ComponentTypeId ComponentType, ComponentFieldId Field, string Operation, string? EvidenceIdentity = null, string? Detail = null);
internal readonly record struct EcsOperationEvidence(TickId TickId, ProcessorId? ProcessorId, string? EvidenceIdentity);
