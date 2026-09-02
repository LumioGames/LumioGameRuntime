using System;

namespace Lumio.GameRuntime.Ecs.Annotations;

/// <summary>C-2 persistence dimension. Unmarked fields are <see cref="Ephemeral"/>.</summary>
public enum PersistenceKind
{
    /// <summary>Not entered into ECS snapshot/restore.</summary>
    Ephemeral = 0,

    /// <summary>Entered into the existing ECS snapshot/restore path.</summary>
    Persistent = 1,
}

/// <summary>C-2 replication dimension. Unmarked fields are <see cref="NotReplicated"/>.</summary>
public enum ReplicationKind
{
    /// <summary>Never placed on the replica stream.</summary>
    NotReplicated = 0,

    /// <summary>May be copied into a ReplicaWorld.</summary>
    Replicated = 1,
}

/// <summary>C-2 visibility dimension. Unmarked fields are <see cref="ServerOnly"/>.</summary>
public enum VisibilityKind
{
    /// <summary>Visible only to server-authoritative callers.</summary>
    ServerOnly = 0,

    /// <summary>Visible to every replica in the room.</summary>
    RoomPublic = 1,

    /// <summary>Visible only to observers whose AOI contains the entity.</summary>
    AoiScoped = 2,

    /// <summary>Visible only to connections that hold the matching claim.</summary>
    ClaimScoped = 3,
}

/// <summary>Marks a type whose public instance fields and properties may be scanned into the declaration table.</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, Inherited = false, AllowMultiple = false)]
public sealed class EcsComponentAttribute : Attribute
{
}

/// <summary>Overrides persistence for an annotated field. Bare <c>[Persist]</c> means persistent.</summary>
[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, Inherited = false, AllowMultiple = false)]
public sealed class PersistAttribute : Attribute
{
    /// <summary>Marks the member persistent.</summary>
    public PersistAttribute() : this(PersistenceKind.Persistent)
    {
    }

    /// <summary>Marks the member with an explicit persistence kind.</summary>
    public PersistAttribute(PersistenceKind kind)
    {
        Kind = kind;
    }

    /// <summary>Selected persistence kind.</summary>
    public PersistenceKind Kind { get; }
}

/// <summary>Overrides replication for an annotated field. Bare <c>[Replicate]</c> means replicated.</summary>
[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, Inherited = false, AllowMultiple = false)]
public sealed class ReplicateAttribute : Attribute
{
    /// <summary>Marks the member replicated.</summary>
    public ReplicateAttribute() : this(ReplicationKind.Replicated)
    {
    }

    /// <summary>Marks the member with an explicit replication kind.</summary>
    public ReplicateAttribute(ReplicationKind kind)
    {
        Kind = kind;
    }

    /// <summary>Selected replication kind.</summary>
    public ReplicationKind Kind { get; }
}

/// <summary>Overrides visibility for an annotated field. Omission means server-only.</summary>
[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, Inherited = false, AllowMultiple = false)]
public sealed class VisibilityAttribute : Attribute
{
    /// <summary>Marks the member with an explicit visibility kind.</summary>
    public VisibilityAttribute(VisibilityKind kind)
    {
        Kind = kind;
    }

    /// <summary>Selected visibility kind.</summary>
    public VisibilityKind Kind { get; }
}

/// <summary>Overrides the C-2 <c>valueType</c> token when it cannot be inferred from the CLR type.</summary>
[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, Inherited = false, AllowMultiple = false)]
public sealed class AttributeValueTypeAttribute : Attribute
{
    /// <summary>Sets the C-2 value type token (for example <c>enum:entityType</c>).</summary>
    public AttributeValueTypeAttribute(string valueType)
    {
        AnnotationGuard.NotNull(valueType, nameof(valueType));
        ValueType = valueType;
    }

    /// <summary>C-2 value type token written into the declaration table.</summary>
    public string ValueType { get; }
}
