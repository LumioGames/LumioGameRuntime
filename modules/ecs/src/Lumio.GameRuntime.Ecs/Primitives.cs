using System;

namespace Lumio.GameRuntime.Ecs;

public readonly record struct WorldId(ulong Value) : IComparable<WorldId>
{
    public bool IsDefault => Value == 0UL;
    public int CompareTo(WorldId other) => Value.CompareTo(other.Value);
    public static implicit operator WorldId(uint value) => new(value);
    public static implicit operator WorldId(ulong value) => new(value);
    public static bool operator <(WorldId left, WorldId right) => left.Value < right.Value;
    public static bool operator <=(WorldId left, WorldId right) => left.Value <= right.Value;
    public static bool operator >(WorldId left, WorldId right) => left.Value > right.Value;
    public static bool operator >=(WorldId left, WorldId right) => left.Value >= right.Value;
    public override string ToString() => Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

public readonly record struct TickId(ulong Value) : IComparable<TickId>
{
    public bool IsDefault => Value == 0UL;
    public int CompareTo(TickId other) => Value.CompareTo(other.Value);
    public static implicit operator TickId(uint value) => new(value);
    public static implicit operator TickId(ulong value) => new(value);
    public static bool operator <(TickId left, TickId right) => left.Value < right.Value;
    public static bool operator <=(TickId left, TickId right) => left.Value <= right.Value;
    public static bool operator >(TickId left, TickId right) => left.Value > right.Value;
    public static bool operator >=(TickId left, TickId right) => left.Value >= right.Value;
    public override string ToString() => Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

public readonly record struct SnapshotId(ulong Value)
{
    public bool IsDefault => Value == 0UL;
    public static implicit operator SnapshotId(ulong value) => new(value);
}

public readonly record struct ProcessorId(ulong Value) : IComparable<ProcessorId>
{
    public int CompareTo(ProcessorId other) => Value.CompareTo(other.Value);
    public static implicit operator ProcessorId(uint value) => new(value);
    public static bool operator <(ProcessorId left, ProcessorId right) => left.Value < right.Value;
    public static bool operator <=(ProcessorId left, ProcessorId right) => left.Value <= right.Value;
    public static bool operator >(ProcessorId left, ProcessorId right) => left.Value > right.Value;
    public static bool operator >=(ProcessorId left, ProcessorId right) => left.Value >= right.Value;
}

public readonly record struct ComponentTypeId(ulong Value) : IComparable<ComponentTypeId>
{
    public bool IsDefault => Value == 0UL;
    public int CompareTo(ComponentTypeId other) => Value.CompareTo(other.Value);
    public static implicit operator ComponentTypeId(uint value) => new(value);
    public static implicit operator ComponentTypeId(ulong value) => new(value);
    public static bool operator <(ComponentTypeId left, ComponentTypeId right) => left.Value < right.Value;
    public static bool operator <=(ComponentTypeId left, ComponentTypeId right) => left.Value <= right.Value;
    public static bool operator >(ComponentTypeId left, ComponentTypeId right) => left.Value > right.Value;
    public static bool operator >=(ComponentTypeId left, ComponentTypeId right) => left.Value >= right.Value;
    public override string ToString() => Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

public readonly record struct ComponentFieldId(ulong Value) : IComparable<ComponentFieldId>
{
    public bool IsDefault => Value == 0UL;
    public int CompareTo(ComponentFieldId other) => Value.CompareTo(other.Value);
    public static implicit operator ComponentFieldId(uint value) => new(value);
    public static implicit operator ComponentFieldId(ulong value) => new(value);
    public static bool operator <(ComponentFieldId left, ComponentFieldId right) => left.Value < right.Value;
    public static bool operator <=(ComponentFieldId left, ComponentFieldId right) => left.Value <= right.Value;
    public static bool operator >(ComponentFieldId left, ComponentFieldId right) => left.Value > right.Value;
    public static bool operator >=(ComponentFieldId left, ComponentFieldId right) => left.Value >= right.Value;
    public override string ToString() => Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

public readonly record struct SchemaEpoch(uint Value)
{
    public static implicit operator SchemaEpoch(uint value) => new(value);
}

public readonly record struct Revision(ulong Value)
{
    public static implicit operator Revision(ulong value) => new(value);
}

public readonly record struct ErrorIdentity(string Code, int NumericCode = 0)
{
    public override string ToString() => NumericCode == 0 ? Code : $"{Code}({NumericCode})";
}

public readonly record struct FailureContext(
    WorldId WorldId,
    TickId TickId,
    LocalEntityId Entity,
    ComponentTypeId ComponentType,
    ComponentFieldId Field,
    string Operation,
    string? Detail = null);

public enum EntityMode
{
    CrossServer,
    Local
}

public enum EntityLifecycleState
{
    Reserved,
    Alive,
    Disabled,
    Tombstoned,
    Destroyed
}

public enum StorageOperationStatus
{
    Accepted,
    Rejected,
    AlreadyApplied,
    Retryable,
    Fatal,
    Indeterminate
}

public readonly record struct StorageOperationResult(StorageOperationStatus Status, ErrorIdentity? Error = null)
{
    public bool IsSuccess => Status is StorageOperationStatus.Accepted or StorageOperationStatus.AlreadyApplied;
    public static StorageOperationResult Accepted() => new(StorageOperationStatus.Accepted);
    public static StorageOperationResult Rejected(string code, int numericCode = 0) => new(StorageOperationStatus.Rejected, new ErrorIdentity(code, numericCode));
    public static StorageOperationResult Retryable(string code) => new(StorageOperationStatus.Retryable, new ErrorIdentity(code));
    public static StorageOperationResult Fatal(string code) => new(StorageOperationStatus.Fatal, new ErrorIdentity(code));
}

public readonly record struct EcsBudget(
    int MaxEntities,
    int MaxQueryResults,
    int MaxChangeEntries,
    int MaxSnapshotBytes)
{
    public static EcsBudget Default => new(65_536, 65_536, 65_536, 16 * 1024 * 1024);
    public bool IsValid => MaxEntities > 0 && MaxQueryResults > 0 && MaxChangeEntries > 0 && MaxSnapshotBytes > 0;
    public void Validate()
    {
        if (!IsValid) throw new ArgumentOutOfRangeException(nameof(EcsBudget), "All ECS budgets must be positive.");
    }
}

public readonly record struct QueryBudget(int MaxEntities, int MaxBytes)
{
    public static QueryBudget Default => new(65_536, 16 * 1024 * 1024);
    public bool IsValid => MaxEntities > 0 && MaxBytes > 0;
}

public readonly record struct StorageQueryHandle(uint Value);
public readonly record struct StorageReadSnapshotHandle(ulong Value);

public readonly record struct ComponentInitValue(
    ComponentTypeId ComponentType,
    ComponentFieldId Field,
    ReadOnlyMemory<byte> CanonicalValue);

public readonly record struct ComponentInitBatch(ReadOnlyMemory<ComponentInitValue> Values)
{
    public static ComponentInitBatch Empty => new(ReadOnlyMemory<ComponentInitValue>.Empty);
}

public readonly record struct EntityDestroyResult(
    bool Destroyed,
    LocalEntityId Entity,
    StorageOperationResult Result)
{
    public ErrorIdentity? Error => Result.Error;
}

public readonly record struct EntityReference(
    LocalEntityId Local,
    ulong NetworkId,
    uint Generation,
    ReferenceResolutionState State)
{
    public bool IsResolved => State == ReferenceResolutionState.Resolved;
}

public enum ReferenceResolutionState
{
    Unresolved,
    Resolved,
    Debt,
    Tombstone,
    Rejected
}

public enum ReferenceFallback
{
    KeepLastKnown,
    DefaultValue,
    Ignore,
    Reject
}

public readonly record struct ReferenceResolution(
    ReferenceResolutionState State,
    LocalEntityId Entity,
    ulong NetworkId,
    uint Generation,
    ReferenceFallback Fallback = ReferenceFallback.DefaultValue,
    string? Reason = null);
