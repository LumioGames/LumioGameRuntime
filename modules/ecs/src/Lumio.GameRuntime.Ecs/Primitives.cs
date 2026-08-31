using System;
using System.Collections.Generic;

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

internal readonly record struct TickId(ulong Value) : IComparable<TickId>
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

internal readonly record struct SnapshotId(ulong Value)
{
    public bool IsDefault => Value == 0UL;
    public static implicit operator SnapshotId(ulong value) => new(value);
}

internal readonly record struct ProcessorId(ulong Value) : IComparable<ProcessorId>
{
    public int CompareTo(ProcessorId other) => Value.CompareTo(other.Value);
    public static implicit operator ProcessorId(uint value) => new(value);
    public static bool operator <(ProcessorId left, ProcessorId right) => left.Value < right.Value;
    public static bool operator <=(ProcessorId left, ProcessorId right) => left.Value <= right.Value;
    public static bool operator >(ProcessorId left, ProcessorId right) => left.Value > right.Value;
    public static bool operator >=(ProcessorId left, ProcessorId right) => left.Value >= right.Value;
}

internal readonly record struct ComponentTypeId(ulong Value) : IComparable<ComponentTypeId>
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

internal readonly record struct ComponentFieldId(ulong Value) : IComparable<ComponentFieldId>
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

internal readonly record struct Revision(ulong Value)
{
    public bool IsDefault => Value == 0UL;
    public static implicit operator Revision(ulong value) => new(value);
}

public readonly record struct ErrorIdentity
{
    public ErrorIdentity(string code)
    {
        if (string.IsNullOrWhiteSpace(code) || !EcsBoundaryErrors.IsGeneratedStableError(code))
            throw new ArgumentOutOfRangeException(nameof(code), code, "Error identity must come from the generated stable error catalog.");
        Code = code;
    }

    public string Code { get; }

    public override string ToString() => Code ?? string.Empty;
}

internal readonly record struct FailureContext(
    WorldId WorldId,
    TickId TickId,
    ProcessorId? ProcessorId,
    LocalEntityId Entity,
    ComponentTypeId ComponentType,
    ComponentFieldId Field,
    string Operation,
    string? EvidenceIdentity = null,
    string? Detail = null);

internal readonly record struct EcsOperationEvidence(
    TickId TickId,
    ProcessorId? ProcessorId,
    string? EvidenceIdentity);

internal enum EntityMode
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
    public static StorageOperationResult Rejected(string code) => new(StorageOperationStatus.Rejected, new ErrorIdentity(code));
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

internal readonly record struct QueryBudget(int MaxEntities, int MaxBytes)
{
    public static QueryBudget Default => new(65_536, 16 * 1024 * 1024);
    public bool IsValid => MaxEntities > 0 && MaxBytes > 0;
}

internal readonly record struct StorageQueryHandle(uint Value);

internal readonly record struct StorageSnapshotContext(
    WorldId WorldId,
    SnapshotId SnapshotId,
    Revision Revision,
    EcsWorld.EcsWorldContext? Origin = null)
{
    public bool IsValid => !WorldId.IsDefault && !SnapshotId.IsDefault && !Revision.IsDefault;
}

internal readonly record struct StorageReadSnapshotHandle(
    ulong Value,
    StorageSnapshotContext Context,
    EcsWorld.EcsWorldContext? Origin = null)
{
    public bool IsDefault => Value == 0UL || !Context.IsValid;
}

internal readonly record struct ComponentInitValue(
    ComponentTypeId ComponentType,
    ComponentFieldId Field,
    ReadOnlyMemory<byte> CanonicalValue);

internal readonly record struct ComponentInitBatch(
    ReadOnlyMemory<ComponentTypeId> Components,
    ReadOnlyMemory<ComponentInitValue> Values)
{
    public ComponentInitBatch(ReadOnlyMemory<ComponentInitValue> values)
        : this(InferComponents(values), values)
    {
    }

    public static ComponentInitBatch Empty => new(
        ReadOnlyMemory<ComponentTypeId>.Empty,
        ReadOnlyMemory<ComponentInitValue>.Empty);

    private static ReadOnlyMemory<ComponentTypeId> InferComponents(ReadOnlyMemory<ComponentInitValue> values)
    {
        var components = new List<ComponentTypeId>();
        ReadOnlySpan<ComponentInitValue> span = values.Span;
        for (int index = 0; index < span.Length; index++)
        {
            if (!components.Contains(span[index].ComponentType))
                components.Add(span[index].ComponentType);
        }
        return components.ToArray();
    }
}

internal readonly record struct EntityDestroyResult(
    bool Destroyed,
    LocalEntityId Entity,
    StorageOperationResult Result)
{
    public ErrorIdentity? Error => Result.Error;
}
