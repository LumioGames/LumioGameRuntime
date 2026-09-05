using System;
using System.Numerics;

namespace Lumio.GameRuntime.Ecs;

/// <summary>Position and rotation without mutable entity or component references.</summary>
public readonly record struct Pose(Vector3 Position, Quaternion Rotation)
{
    public static Pose Identity => new(Vector3.Zero, Quaternion.Identity);
}

/// <summary>Position, rotation and scale used by the matrix-facing API.</summary>
public readonly record struct TransformTrs(Vector3 Position, Quaternion Rotation, Vector3 Scale)
{
    public Pose Pose => new(Position, Rotation);
}

/// <summary>Stable identity for a discontinuous logical movement.</summary>
public readonly record struct TransformTeleportId(ulong Value)
{
    public bool IsDefault => Value == 0;
}

/// <summary>Reference space carried by a complete Model sample.</summary>
public enum TransformSampleReference
{
    World = 0,
    ParentRelative = 1,
    SupportRelative = 2,
    WorldFallback = 3,
}

/// <summary>Rejects a logical transform write before any state is changed.</summary>
public sealed class TransformWriteException : InvalidOperationException
{
    public TransformWriteException(string message) : base(message)
    {
    }
}

/// <summary>Authenticated-in-process identity issued by the owning World.</summary>
public sealed class TransformController
{
    private readonly object _capability = new();

    private TransformController(NetEntityId entity, string source, bool bound)
    {
        Entity = entity;
        Source = source;
        IsBound = bound;
    }

    /// <summary>Entity whose LogicTransform this controller may write.</summary>
    public NetEntityId Entity { get; }

    /// <summary>Stable diagnostic source name.</summary>
    public string Source { get; }

    /// <summary>False for an intentionally invalid token used by callers to test rejection.</summary>
    public bool IsBound { get; }

    internal object Capability => _capability;

    internal static TransformController Create(NetEntityId entity, string source) =>
        new(entity, source, true);

    /// <summary>Creates a token that is not accepted by any World.</summary>
    public static TransformController Unbound(string source) =>
        new(default, source ?? string.Empty, false);
}

/// <summary>Complete immutable sample consumed by a ModelTransform.</summary>
public readonly record struct TransformSample(
    NetEntityId Entity,
    ulong Tick,
    double TimeSeconds,
    Pose Pose,
    TransformSampleReference Reference,
    TransformTeleportId? Teleport = null,
    NetEntityId Parent = default)
{
    public bool IsFinite => TransformMath.IsFinite(Pose.Position) && TransformMath.IsFinite(Pose.Rotation) && Pose.Rotation.LengthSquared() > TransformMath.Epsilon * TransformMath.Epsilon;
}

internal static class TransformMath
{
    internal const float Epsilon = 1e-6f;

    internal static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    internal static bool IsFinite(Quaternion value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z) && float.IsFinite(value.W);

    internal static Quaternion Normalize(Quaternion value, string parameterName)
    {
        if (!IsFinite(value)) throw new ArgumentException("Quaternion must contain finite values.", parameterName);
        float lengthSquared = value.LengthSquared();
        if (!float.IsFinite(lengthSquared) || lengthSquared <= Epsilon * Epsilon)
            throw new ArgumentException("Quaternion must have a non-zero length.", parameterName);
        return Quaternion.Normalize(value);
    }

    internal static void ValidatePose(Pose pose, string parameterName)
    {
        if (!IsFinite(pose.Position)) throw new ArgumentException("Position must contain finite values.", parameterName);
        _ = Normalize(pose.Rotation, parameterName);
    }

    internal static void ValidateScale(Vector3 scale, string parameterName, bool unitOnly)
    {
        if (!IsFinite(scale) || scale.X <= 0 || scale.Y <= 0 || scale.Z <= 0)
            throw new ArgumentException("Scale must contain positive finite values.", parameterName);
        if (unitOnly && Vector3.DistanceSquared(scale, Vector3.One) > Epsilon * Epsilon)
            throw new ArgumentException("LogicTransform only accepts unit scale.", parameterName);
    }

    internal static Matrix4x4 Matrix(TransformTrs trs) =>
        Matrix4x4.CreateScale(trs.Scale) * Matrix4x4.CreateFromQuaternion(Normalize(trs.Rotation, nameof(trs))) * Matrix4x4.CreateTranslation(trs.Position);

    internal static Pose Combine(Pose parent, Pose local)
    {
        Vector3 position = Vector3.Transform(local.Position, Matrix4x4.CreateFromQuaternion(parent.Rotation) * Matrix4x4.CreateTranslation(parent.Position));
        Quaternion rotation = Normalize(parent.Rotation * local.Rotation, nameof(local));
        return new Pose(position, rotation);
    }

    internal static Pose InverseCombine(Pose parent, Pose world)
    {
        Quaternion inverse = Quaternion.Inverse(Normalize(parent.Rotation, nameof(parent)));
        Vector3 position = Vector3.Transform(world.Position - parent.Position, inverse);
        Quaternion rotation = Normalize(inverse * world.Rotation, nameof(world));
        return new Pose(position, rotation);
    }

    internal static Vector3 Lerp(Vector3 from, Vector3 to, float amount) => Vector3.Lerp(from, to, Math.Clamp(amount, 0f, 1f));

    internal static Quaternion Slerp(Quaternion from, Quaternion to, float amount) => Quaternion.Slerp(from, to, Math.Clamp(amount, 0f, 1f));
}
