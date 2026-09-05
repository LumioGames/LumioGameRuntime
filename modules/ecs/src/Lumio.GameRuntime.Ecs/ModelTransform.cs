using System;
using System.Collections.Generic;
using System.Numerics;

namespace Lumio.GameRuntime.Ecs;

/// <summary>
/// Client-side presentation pose for the owning ECS entity. It consumes complete
/// immutable samples and never writes the entity's LogicTransform.
/// </summary>
[EcsComponent]
public sealed class ModelTransform : Component
{
    private const int MaxSamples = 32;
    private const double MaxExtrapolationSeconds = 0.1;
    private readonly List<TransformSample> _samples = new();
    private Pose _displayPose = Pose.Identity;
    private TransformTeleportId? _consumedTeleport;
    private bool _hasDisplay;
    private ulong _lastTick;
    private double _lastSampleTime;
    private Vector3 _velocity;
    private Quaternion _angularFrom = Quaternion.Identity;
    private Quaternion _angularTo = Quaternion.Identity;
    private TransformSampleReference _reference = TransformSampleReference.World;
    private NetEntityId _sampleParent;

    public Vector3 LocalPosition => LocalPose.Position;
    public Quaternion LocalRotation => LocalPose.Rotation;
    public Vector3 LocalScale { get; private set; } = Vector3.One;
    public Vector3 WorldPosition => ResolveWorldPose().Position;
    public Quaternion WorldRotation => ResolveWorldPose().Rotation;
    public Pose LocalPose => ResolveLocalPose();
    public Pose WorldPose => ResolveWorldPose();
    public LogicTransform? Parent => (WorldInternal?.NamedComponent(Entity, nameof(LogicTransform)) as LogicTransform)?.Parent;
    public Matrix4x4 LocalMatrix => TransformMath.Matrix(new TransformTrs(LocalPosition, LocalRotation, LocalScale));
    public Matrix4x4 WorldMatrix => TransformMath.Matrix(new TransformTrs(WorldPosition, WorldRotation, LocalScale));
    public TransformTrs LocalTRS => new(LocalPosition, LocalRotation, LocalScale);
    public TransformTrs WorldTRS => new(WorldPosition, WorldRotation, LocalScale);
    public Vector3 LossyWorldScale => LocalScale;
    public Vector3 Forward => Vector3.Transform(Vector3.UnitZ, WorldRotation);
    public Vector3 Up => Vector3.Transform(Vector3.UnitY, WorldRotation);
    public Vector3 Right => Vector3.Transform(Vector3.UnitX, WorldRotation);
    public TransformTeleportId? ConsumedTeleport => _consumedTeleport;
    public int BufferedSampleCount => _samples.Count;

    public void SetLocalScale(Vector3 scale)
    {
        TransformMath.ValidateScale(scale, nameof(scale), false);
        LocalScale = scale;
    }

    /// <summary>Adds a complete, immutable sample; stale identities are ignored.</summary>
    public void PushSample(TransformSample sample)
    {
        if (sample.Entity != Entity) throw new InvalidOperationException("Transform sample belongs to a different entity.");
        if (!sample.IsFinite || !double.IsFinite(sample.TimeSeconds)) throw new ArgumentException("Transform sample must contain finite values.", nameof(sample));
        sample = sample with { Pose = new Pose(sample.Pose.Position, TransformMath.Normalize(sample.Pose.Rotation, nameof(sample))) };
        if (sample.Tick < _lastTick || sample.TimeSeconds < _lastSampleTime) return;
        if (sample.Teleport is TransformTeleportId teleport && !teleport.IsDefault &&
            (!_consumedTeleport.HasValue || _consumedTeleport.Value != teleport))
        {
            _samples.Clear();
            _displayPose = sample.Pose;
            _hasDisplay = true;
            _consumedTeleport = teleport;
            _reference = sample.Reference;
            _sampleParent = sample.Parent;
            _lastTick = sample.Tick;
            _lastSampleTime = sample.TimeSeconds;
            _velocity = Vector3.Zero;
            return;
        }

        if (sample.Tick == _lastTick && sample.TimeSeconds == _lastSampleTime) return;
        if (_samples.Count > 0)
        {
            TransformSample previous = _samples[_samples.Count - 1];
            double dt = sample.TimeSeconds - previous.TimeSeconds;
            if (dt > 0) _velocity = (sample.Pose.Position - previous.Pose.Position) / (float)dt;
        }
        _samples.Add(sample);
        _reference = sample.Reference;
        _sampleParent = sample.Parent;
        while (_samples.Count > MaxSamples) _samples.RemoveAt(0);
        _lastTick = sample.Tick;
        _lastSampleTime = sample.TimeSeconds;
        if (!_hasDisplay)
        {
            _displayPose = sample.Pose;
            _hasDisplay = true;
        }
    }

    /// <summary>Advances the display clock using interpolation or bounded extrapolation.</summary>
    public void UpdatePresentation(double timeSeconds)
    {
        if (!_hasDisplay || !double.IsFinite(timeSeconds)) throw new ArgumentException("Presentation time must be finite.", nameof(timeSeconds));
        if (_samples.Count == 0) return;
        while (_samples.Count >= 2 && _samples[1].TimeSeconds <= timeSeconds)
            _samples.RemoveAt(0);
        if (_samples.Count >= 2)
        {
            TransformSample from = _samples[0];
            TransformSample to = _samples[1];
            double duration = to.TimeSeconds - from.TimeSeconds;
            float amount = duration <= 0 ? 1f : (float)((timeSeconds - from.TimeSeconds) / duration);
            _displayPose = new Pose(TransformMath.Lerp(from.Pose.Position, to.Pose.Position, amount), TransformMath.Slerp(from.Pose.Rotation, to.Pose.Rotation, amount));
            _angularFrom = from.Pose.Rotation;
            _angularTo = to.Pose.Rotation;
            return;
        }

        TransformSample latest = _samples[0];
        double elapsed = Math.Clamp(timeSeconds - latest.TimeSeconds, 0d, MaxExtrapolationSeconds);
        Vector3 position = latest.Pose.Position + _velocity * (float)elapsed;
        _displayPose = new Pose(position, latest.Pose.Rotation);
    }

    public Vector3 TransformPoint(Vector3 point) => Vector3.Transform(point, WorldMatrix);

    public Vector3 TransformVector(Vector3 vector) => Vector3.TransformNormal(vector, WorldMatrix);

    public Vector3 InverseTransformPoint(Vector3 point)
    {
        if (!Matrix4x4.Invert(WorldMatrix, out Matrix4x4 inverse)) throw new InvalidOperationException("World matrix is not invertible.");
        return Vector3.Transform(point, inverse);
    }

    public Vector3 InverseTransformVector(Vector3 vector)
    {
        if (!Matrix4x4.Invert(WorldMatrix, out Matrix4x4 inverse)) throw new InvalidOperationException("World matrix is not invertible.");
        return Vector3.TransformNormal(vector, inverse);
    }

    public Vector3 TransformDirection(Vector3 direction)
    {
        if (!TransformMath.IsFinite(direction) || direction.LengthSquared() <= TransformMath.Epsilon * TransformMath.Epsilon)
            throw new ArgumentException("Direction must be finite and non-zero.", nameof(direction));
        return Vector3.Normalize(Vector3.Transform(direction, WorldRotation));
    }

    public Vector3 InverseTransformDirection(Vector3 direction)
    {
        if (!TransformMath.IsFinite(direction) || direction.LengthSquared() <= TransformMath.Epsilon * TransformMath.Epsilon)
            throw new ArgumentException("Direction must be finite and non-zero.", nameof(direction));
        return Vector3.Normalize(Vector3.Transform(direction, Quaternion.Inverse(WorldRotation)));
    }

    internal void ResetForReuse()
    {
        _samples.Clear();
        _displayPose = Pose.Identity;
        LocalScale = Vector3.One;
        _consumedTeleport = null;
        _hasDisplay = false;
        _lastTick = 0;
        _lastSampleTime = 0;
        _velocity = Vector3.Zero;
        _angularFrom = Quaternion.Identity;
        _angularTo = Quaternion.Identity;
        _reference = TransformSampleReference.World;
        _sampleParent = default;
    }

    private Pose ResolveWorldPose()
    {
        if (WorldInternal is null || World.IsServer || _reference is TransformSampleReference.World or TransformSampleReference.WorldFallback) return _displayPose;
        ModelTransform? parentModel = ResolveParentModel();
        return parentModel is null ? _displayPose : TransformMath.Combine(parentModel.WorldPose, _displayPose);
    }

    private Pose ResolveLocalPose()
    {
        if (_reference is not (TransformSampleReference.World or TransformSampleReference.WorldFallback)) return _displayPose;
        ModelTransform? parentModel = ResolveParentModel();
        return parentModel is null ? _displayPose : TransformMath.InverseCombine(parentModel.WorldPose, _displayPose);
    }

    private ModelTransform? ResolveParentModel()
    {
        if (WorldInternal is null || World.IsServer) return null;
        LogicTransform? logic = World.NamedComponent(Entity, nameof(LogicTransform)) as LogicTransform;
        LogicTransform? parent = logic?.Parent;
        if (!_sampleParent.IsDefault && World.IsLive(_sampleParent))
            parent = World.NamedComponent(_sampleParent, nameof(LogicTransform)) as LogicTransform;
        return parent is null ? null : World.NamedComponent(parent.Entity, nameof(ModelTransform)) as ModelTransform;
    }
}
