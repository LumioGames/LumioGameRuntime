using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;

namespace Lumio.GameRuntime.Ecs;

/// <summary>
/// Authoritative logical pose. Position and rotation are the only mutable truth;
/// world values, matrices and child lists are derived from them.
/// </summary>
[EcsComponent]
public sealed class LogicTransform : Component, IGeneratedComponent, IGeneratedSyncMetadata
{
    private readonly List<LogicTransform> _children = new();
    private Vector3 _localPosition;
    private Quaternion _localRotation = Quaternion.Identity;
    private LogicTransform? _parent;
    private TransformController? _controller;
    private TransformController? _activeWriteController;
    private TransformTeleportId? _lastTeleport;
    private NetEntityId _pendingParentId;
    private bool _pendingParentSet;

    private string SerializedLocalPosition => Format(_localPosition);
    private string SerializedLocalRotation => Format(_localRotation);
    private string SerializedParent => _parent?.Entity.ToHex() ?? string.Empty;
    private string SerializedTeleport => _lastTeleport?.Value.ToString(CultureInfo.InvariantCulture) ?? string.Empty;

    /// <summary>Local position in metres, with Y up and XZ horizontal.</summary>
    public Vector3 LocalPosition => _localPosition;

    /// <summary>Local unit quaternion in (x,y,z,w) order.</summary>
    public Quaternion LocalRotation => _localRotation;

    /// <summary>Logic scale is intentionally fixed to one.</summary>
    public Vector3 LocalScale => Vector3.One;

    /// <summary>Parent relation currently committed by the ECS.</summary>
    public LogicTransform? Parent => _parent;

    /// <summary>Topmost parent, or this component when detached.</summary>
    public LogicTransform Root
    {
        get
        {
            LogicTransform result = this;
            while (result._parent is not null) result = result._parent;
            return result;
        }
    }

    /// <summary>Direct children in stable attach order.</summary>
    public IReadOnlyList<LogicTransform> Children => _children;

    public int ChildCount => _children.Count;

    public LogicTransform GetChild(int index) => _children[index];

    public Vector3 WorldPosition => WorldPose.Position;

    public Quaternion WorldRotation => WorldPose.Rotation;

    public Pose LocalPose => new(_localPosition, _localRotation);

    public Pose WorldPose => _parent is null ? LocalPose : TransformMath.Combine(_parent.WorldPose, LocalPose);

    public Matrix4x4 LocalMatrix => TransformMath.Matrix(new TransformTrs(_localPosition, _localRotation, Vector3.One));

    public Matrix4x4 WorldMatrix =>
        TransformMath.Matrix(new TransformTrs(WorldPosition, WorldRotation, Vector3.One));

    public TransformTrs LocalTRS => new(_localPosition, _localRotation, Vector3.One);

    public TransformTrs WorldTRS => new(WorldPosition, WorldRotation, Vector3.One);

    public Vector3 LossyWorldScale => Vector3.One;

    public Vector3 Forward => Vector3.Transform(Vector3.UnitZ, WorldRotation);

    public Vector3 Up => Vector3.Transform(Vector3.UnitY, WorldRotation);

    public Vector3 Right => Vector3.Transform(Vector3.UnitX, WorldRotation);

    /// <summary>Most recent explicit discontinuity, retained across ordinary movement.</summary>
    public TransformTeleportId? LastTeleport => _lastTeleport;

    /// <summary>Current bound writer, if the world has registered one.</summary>
    public TransformController? Controller => _controller;

    /// <summary>Starts a write scope after checking the registered controller.</summary>
    public IDisposable BeginWrite(TransformController controller)
    {
        EnsureController(controller);
        if (_activeWriteController is not null) throw new TransformWriteException("A transform write scope is already active.");
        _activeWriteController = controller;
        return new TransformWriteScope(this, controller);
    }

    public void SetLocalPosition(Vector3 position)
    {
        EnsureWrite();
        ValidatePosition(position);
        string old = SerializedLocalPosition;
        _localPosition = position;
        RecordChange("localPosition", old);
    }

    public void SetWorldPosition(Vector3 position)
    {
        EnsureWrite();
        ValidatePosition(position);
        string old = SerializedLocalPosition;
        _localPosition = _parent is null ? position : TransformMath.InverseCombine(_parent.WorldPose, new Pose(position, WorldRotation)).Position;
        RecordChange("localPosition", old);
    }

    public void SetLocalRotation(Quaternion rotation)
    {
        EnsureWrite();
        string old = SerializedLocalRotation;
        _localRotation = TransformMath.Normalize(rotation, nameof(rotation));
        RecordChange("localRotation", old);
    }

    public void SetWorldRotation(Quaternion rotation)
    {
        EnsureWrite();
        Quaternion normalized = TransformMath.Normalize(rotation, nameof(rotation));
        string old = SerializedLocalRotation;
        _localRotation = _parent is null
            ? normalized
            : TransformMath.InverseCombine(_parent.WorldPose, new Pose(WorldPosition, normalized)).Rotation;
        RecordChange("localRotation", old);
    }

    public void SetLocalPose(Pose pose)
    {
        EnsureWrite();
        TransformMath.ValidatePose(pose, nameof(pose));
        string oldPosition = SerializedLocalPosition;
        string oldRotation = SerializedLocalRotation;
        _localPosition = pose.Position;
        _localRotation = Quaternion.Normalize(pose.Rotation);
        RecordChange("localPosition", oldPosition);
        RecordChange("localRotation", oldRotation);
    }

    public void SetWorldPose(Pose pose)
    {
        EnsureWrite();
        TransformMath.ValidatePose(pose, nameof(pose));
        string oldPosition = SerializedLocalPosition;
        string oldRotation = SerializedLocalRotation;
        Pose local = _parent is null ? pose : TransformMath.InverseCombine(_parent.WorldPose, pose);
        _localPosition = local.Position;
        _localRotation = local.Rotation;
        RecordChange("localPosition", oldPosition);
        RecordChange("localRotation", oldRotation);
    }

    public void SetLocalTRS(TransformTrs trs)
    {
        EnsureWrite();
        TransformMath.ValidatePose(trs.Pose, nameof(trs));
        TransformMath.ValidateScale(trs.Scale, nameof(trs), true);
        SetLocalPose(trs.Pose);
    }

    public void SetWorldTRS(TransformTrs trs)
    {
        EnsureWrite();
        TransformMath.ValidatePose(trs.Pose, nameof(trs));
        TransformMath.ValidateScale(trs.Scale, nameof(trs), true);
        SetWorldPose(trs.Pose);
    }

    public void SetWorldEulerDegrees(Vector3 degrees) => SetWorldRotation(QuaternionFromEulerDegrees(degrees));

    public Vector3 WorldEulerDegrees => ToEulerDegrees(WorldRotation);

    public void Translate(Vector3 delta, TransformSpace space = TransformSpace.World)
    {
        EnsureWrite();
        ValidatePosition(delta);
        if (space == TransformSpace.Local) SetLocalPosition(_localPosition + delta);
        else SetWorldPosition(WorldPosition + delta);
    }

    public void Rotate(Vector3 eulerDegrees, TransformSpace space = TransformSpace.World)
    {
        EnsureWrite();
        Quaternion delta = QuaternionFromEulerDegrees(eulerDegrees);
        if (space == TransformSpace.Local) SetLocalRotation(Quaternion.Normalize(_localRotation * delta));
        else SetWorldRotation(Quaternion.Normalize(delta * WorldRotation));
    }

    public void RotateAround(Vector3 pivot, Vector3 axis, float degrees)
    {
        EnsureWrite();
        ValidatePosition(pivot);
        if (!TransformMath.IsFinite(axis) || axis.LengthSquared() <= TransformMath.Epsilon * TransformMath.Epsilon)
            throw new ArgumentException("Rotation axis must be finite and non-zero.", nameof(axis));
        Quaternion delta = Quaternion.CreateFromAxisAngle(Vector3.Normalize(axis), degrees * (MathF.PI / 180f));
        Vector3 position = pivot + Vector3.Transform(WorldPosition - pivot, delta);
        SetWorldPose(new Pose(position, Quaternion.Normalize(delta * WorldRotation)));
    }

    public void LookAt(Vector3 target, Vector3 up = default)
    {
        EnsureWrite();
        Vector3 direction = target - WorldPosition;
        if (!TransformMath.IsFinite(target) || direction.LengthSquared() <= TransformMath.Epsilon * TransformMath.Epsilon)
            throw new ArgumentException("LookAt target must differ from the current position.", nameof(target));
        if (up == default) up = Vector3.UnitY;
        if (!TransformMath.IsFinite(up) || up.LengthSquared() <= TransformMath.Epsilon * TransformMath.Epsilon)
            throw new ArgumentException("LookAt up must be finite and non-zero.", nameof(up));
        direction = Vector3.Normalize(direction);
        Vector3 right = Vector3.Cross(up, direction);
        if (right.LengthSquared() <= TransformMath.Epsilon * TransformMath.Epsilon)
            throw new ArgumentException("LookAt up cannot be parallel to the look direction.", nameof(up));
        right = Vector3.Normalize(right);
        Vector3 correctedUp = Vector3.Cross(direction, right);
        Matrix4x4 matrix = new(
            right.X, right.Y, right.Z, 0,
            correctedUp.X, correctedUp.Y, correctedUp.Z, 0,
            direction.X, direction.Y, direction.Z, 0,
            0, 0, 0, 1);
        SetWorldRotation(Quaternion.CreateFromRotationMatrix(matrix));
    }

    public void SetParent(LogicTransform? parent, ParentPoseMode mode = ParentPoseMode.KeepWorld)
    {
        EnsureWrite();
        if (ReferenceEquals(parent, this)) throw new TransformWriteException("A transform cannot parent itself.");
        if (parent is not null)
        {
            if (WorldInternal is null || parent.WorldInternal != WorldInternal)
                throw new TransformWriteException("Parent must belong to the same World.");
            for (LogicTransform? cursor = parent; cursor is not null; cursor = cursor._parent)
                if (ReferenceEquals(cursor, this)) throw new TransformWriteException("Parenting would create a cycle.");
        }

        Pose world = WorldPose;
        string oldParent = SerializedParent;
        string oldPosition = SerializedLocalPosition;
        string oldRotation = SerializedLocalRotation;
        if (_parent is not null) _parent._children.Remove(this);
        _parent = parent;
        if (_parent is not null && !_parent._children.Contains(this)) _parent._children.Add(this);
        if (mode == ParentPoseMode.KeepWorld)
        {
            Pose local = _parent is null ? world : TransformMath.InverseCombine(_parent.WorldPose, world);
            _localPosition = local.Position;
            _localRotation = local.Rotation;
        }
        else if (mode == ParentPoseMode.SnapToParent)
        {
            _localPosition = Vector3.Zero;
            _localRotation = Quaternion.Identity;
        }
        RecordChange("parent", oldParent);
        RecordChange("localPosition", oldPosition);
        RecordChange("localRotation", oldRotation);
    }

    public void Detach() => SetParent(null, ParentPoseMode.KeepWorld);

    public bool IsChildOf(LogicTransform candidate)
    {
        for (LogicTransform? cursor = _parent; cursor is not null; cursor = cursor._parent)
            if (ReferenceEquals(cursor, candidate)) return true;
        return false;
    }

    public void TeleportWorld(Vector3 position, TransformTeleportId teleport)
    {
        EnsureWrite();
        ValidatePosition(position);
        if (teleport.IsDefault) throw new ArgumentException("Teleport id must be non-zero.", nameof(teleport));
        SetWorldPosition(position);
        string old = SerializedTeleport;
        _lastTeleport = teleport;
        RecordChange("teleportId", old);
        PropagateTeleport(teleport);
    }

    public void TeleportLocal(Pose pose, TransformTeleportId teleport)
    {
        EnsureWrite();
        TransformMath.ValidatePose(pose, nameof(pose));
        if (teleport.IsDefault) throw new ArgumentException("Teleport id must be non-zero.", nameof(teleport));
        SetLocalPose(pose);
        string old = SerializedTeleport;
        _lastTeleport = teleport;
        RecordChange("teleportId", old);
        PropagateTeleport(teleport);
    }

    public Vector3 TransformPoint(Vector3 point) => Vector3.Transform(point, WorldMatrix);

    public Vector3 InverseTransformPoint(Vector3 point)
    {
        if (!Matrix4x4.Invert(WorldMatrix, out Matrix4x4 inverse)) throw new InvalidOperationException("World matrix is not invertible.");
        return Vector3.Transform(point, inverse);
    }

    public Vector3 TransformVector(Vector3 vector) => Vector3.TransformNormal(vector, WorldMatrix);

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

    internal void ClaimController(TransformController controller)
    {
        if (_controller is null)
        {
            _controller = controller;
            return;
        }
        if (!ReferenceEquals(_controller, controller))
            throw new TransformWriteException($"LogicTransform on {Entity.ToHex()} already has controller '{_controller.Source}', cannot add '{controller.Source}'.");
    }

    internal void HandleOwnerDestroyed()
    {
        Pose world = WorldPose;
        LogicTransform[] children = _children.ToArray();
        for (int i = 0; i < children.Length; i++)
        {
            LogicTransform child = children[i];
            Pose childWorld = child.WorldPose;
            child._parent = null;
            child._localPosition = childWorld.Position;
            child._localRotation = childWorld.Rotation;
        }
        _children.Clear();
        _parent?._children.Remove(this);
        _parent = null;
        _localPosition = world.Position;
        _localRotation = world.Rotation;
    }

    internal void ResetForReuse()
    {
        _children.Clear();
        _localPosition = Vector3.Zero;
        _localRotation = Quaternion.Identity;
        _parent = null;
        _controller = null;
        _activeWriteController = null;
        _lastTeleport = null;
        _pendingParentId = default;
        _pendingParentSet = false;
    }

    bool IGeneratedSyncMetadata.TryGetSyncField(string fieldId, out ISyncField field)
    {
        ISyncField? candidate = GetSyncField(fieldId);
        field = candidate!;
        return candidate is not null;
    }

    void IGeneratedComponent.BindFields(ISyncHost host)
    {
    }

    void IGeneratedComponent.InvokePostAttribute()
    {
    }

    void IGeneratedComponent.InvokeFieldChanging(int ordinal, object? oldValue, object? newValue, ChangeReason reason)
    {
    }

    void IGeneratedComponent.InvokeFieldChanged(int ordinal, object? oldValue, object? newValue, ChangeReason reason)
    {
    }

    bool IGeneratedComponent.DispatchClientWrite(in SyncWrite write) => false;
    void IGeneratedComponent.DispatchServerRpc(string method, object?[] args) { }
    void IGeneratedComponent.DispatchClientRpc(string method, object?[] args) { }

    void IGeneratedComponent.CapturePersist(IPersistWriter writer)
    {
        writer.WriteString("LogicTransform.localPosition", SerializedLocalPosition);
        writer.WriteString("LogicTransform.localRotation", SerializedLocalRotation);
        writer.WriteString("LogicTransform.parent", SerializedParent);
        writer.WriteString("LogicTransform.teleportId", SerializedTeleport);
    }

    void IGeneratedComponent.CaptureSync(IPersistWriter writer)
    {
        writer.WriteString("LogicTransform.localPosition", SerializedLocalPosition);
        writer.WriteString("LogicTransform.localRotation", SerializedLocalRotation);
        writer.WriteString("LogicTransform.parent", SerializedParent);
        writer.WriteString("LogicTransform.teleportId", SerializedTeleport);
    }

    void IGeneratedComponent.RestorePersist(IPersistReader reader)
    {
        if (reader.TryReadString("LogicTransform.localPosition", out string position)) ApplySerialized("localPosition", position);
        if (reader.TryReadString("LogicTransform.localRotation", out string rotation)) ApplySerialized("localRotation", rotation);
        if (reader.TryReadString("LogicTransform.teleportId", out string teleport)) ApplySerialized("teleportId", teleport);
    }

    object? IGeneratedComponent.ReadField(string fieldId) => fieldId switch
    {
        "localPosition" => SerializedLocalPosition,
        "localRotation" => SerializedLocalRotation,
        "parent" => SerializedParent,
        "teleportId" => SerializedTeleport,
        _ => null,
    };

    void IGeneratedComponent.WriteField(string fieldId, object? value, bool silent) => ApplySerialized(fieldId, value?.ToString() ?? string.Empty);

    private ISyncField? GetSyncField(string fieldId) => fieldId switch
    {
        "localPosition" => new TransformSyncField(this, 0, fieldId, () => SerializedLocalPosition, value => ApplySerialized(fieldId, value)),
        "localRotation" => new TransformSyncField(this, 1, fieldId, () => SerializedLocalRotation, value => ApplySerialized(fieldId, value)),
        "parent" => new TransformSyncField(this, 2, fieldId, () => SerializedParent, value => ApplySerialized(fieldId, value)),
        "teleportId" => new TransformSyncField(this, 3, fieldId, () => SerializedTeleport, value => ApplySerialized(fieldId, value)),
        _ => null,
    };

    private void ApplySerialized(string fieldId, string value)
    {
        switch (fieldId)
        {
            case "localPosition": _localPosition = ParseVector(value); break;
            case "localRotation": _localRotation = TransformMath.Normalize(ParseQuaternion(value), fieldId); break;
            case "teleportId":
                _lastTeleport = ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out ulong id) && id != 0
                    ? new TransformTeleportId(id) : null;
                break;
            case "parent":
                _pendingParentId = NetEntityId.TryParse(value, out NetEntityId parentId) ? parentId : default;
                _pendingParentSet = true;
                break;
        }
    }

    internal NetEntityId PendingParentId => _pendingParentId;
    internal bool PendingParentSet => _pendingParentSet;

    internal void ResolveParent(LogicTransform? parent)
    {
        if (ReferenceEquals(parent, this)) throw new TransformWriteException("A transform cannot parent itself.");
        for (LogicTransform? cursor = parent; cursor is not null; cursor = cursor._parent)
            if (ReferenceEquals(cursor, this)) throw new TransformWriteException("Parenting would create a cycle.");
        if (_parent is not null) _parent._children.Remove(this);
        _parent = parent;
        if (parent is not null && !parent._children.Contains(this)) parent._children.Add(this);
        _pendingParentId = default;
        _pendingParentSet = false;
    }

    private void PropagateTeleport(TransformTeleportId teleport)
    {
        for (int i = 0; i < _children.Count; i++)
        {
            LogicTransform child = _children[i];
            if (child._lastTeleport != teleport)
            {
                string old = child.SerializedTeleport;
                child._lastTeleport = teleport;
                child.RecordChange("teleportId", old);
            }
            child.PropagateTeleport(teleport);
        }
    }

    private void RecordChange(string fieldId, string oldValue)
    {
        if (WorldInternal is null || WorldInternal.ApplyingRemote) return;
        string current = ((IGeneratedComponent)this).ReadField(fieldId)?.ToString() ?? string.Empty;
        if (string.Equals(oldValue, current, StringComparison.Ordinal)) return;
        WorldInternal.MarkTransformChange(this, fieldId, oldValue, current);
    }

    private static string Format(Vector3 value) => string.Join(",", value.X.ToString("R", CultureInfo.InvariantCulture), value.Y.ToString("R", CultureInfo.InvariantCulture), value.Z.ToString("R", CultureInfo.InvariantCulture));
    private static string Format(Quaternion value) => string.Join(",", value.X.ToString("R", CultureInfo.InvariantCulture), value.Y.ToString("R", CultureInfo.InvariantCulture), value.Z.ToString("R", CultureInfo.InvariantCulture), value.W.ToString("R", CultureInfo.InvariantCulture));
    private static Vector3 ParseVector(string value)
    {
        string[] parts = value.Split(',');
        if (parts.Length != 3 || !float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float x) || !float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float y) || !float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float z)) throw new FormatException("LogicTransform vector field is invalid.");
        Vector3 result = new(x, y, z);
        if (!TransformMath.IsFinite(result)) throw new FormatException("LogicTransform vector field is not finite.");
        return result;
    }

    private static Quaternion ParseQuaternion(string value)
    {
        string[] parts = value.Split(',');
        if (parts.Length != 4 || !float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float x) || !float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float y) || !float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float z) || !float.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out float w)) throw new FormatException("LogicTransform quaternion field is invalid.");
        return new Quaternion(x, y, z, w);
    }

    private void EnsureController(TransformController controller)
    {
        if (controller is null || !controller.IsBound || !ReferenceEquals(controller, _controller) || controller.Entity != Entity)
            throw new TransformWriteException($"Transform write rejected for {Entity.ToHex()}: controller is not '{_controller?.Source ?? "unregistered"}'.");
    }

    private void EnsureWrite()
    {
        if (_activeWriteController is null || !ReferenceEquals(_activeWriteController, _controller))
            throw new TransformWriteException($"Transform write rejected for {Entity.ToHex()}: no active controller scope.");
    }

    private static void ValidatePosition(Vector3 value)
    {
        if (!TransformMath.IsFinite(value)) throw new ArgumentException("Position must contain finite values.", nameof(value));
    }

    private static Quaternion QuaternionFromEulerDegrees(Vector3 degrees)
    {
        if (!TransformMath.IsFinite(degrees)) throw new ArgumentException("Euler angles must contain finite values.", nameof(degrees));
        float radians = MathF.PI / 180f;
        Quaternion yaw = Quaternion.CreateFromAxisAngle(Vector3.UnitY, degrees.Y * radians);
        Quaternion pitch = Quaternion.CreateFromAxisAngle(Vector3.UnitX, degrees.X * radians);
        Quaternion roll = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, degrees.Z * radians);
        return Quaternion.Normalize(yaw * pitch * roll);
    }

    private static Vector3 ToEulerDegrees(Quaternion rotation)
    {
        rotation = Quaternion.Normalize(rotation);
        float sinPitch = 2f * (rotation.W * rotation.X - rotation.Z * rotation.Y);
        float pitch = MathF.Abs(sinPitch) >= 1f ? (sinPitch < 0 ? -MathF.PI / 2f : MathF.PI / 2f) : MathF.Asin(sinPitch);
        float yaw = MathF.Atan2(2f * (rotation.W * rotation.Y + rotation.X * rotation.Z), 1f - 2f * (rotation.X * rotation.X + rotation.Y * rotation.Y));
        float roll = MathF.Atan2(2f * (rotation.W * rotation.Z + rotation.X * rotation.Y), 1f - 2f * (rotation.X * rotation.X + rotation.Z * rotation.Z));
        const float degrees = 180f / MathF.PI;
        return new Vector3(pitch * degrees, yaw * degrees, roll * degrees);
    }

    private sealed class TransformWriteScope : IDisposable
    {
        private readonly LogicTransform _owner;
        internal TransformWriteScope(LogicTransform owner, TransformController controller) { _owner = owner; Controller = controller; }
        internal TransformController Controller { get; }
        public void Dispose()
        {
            if (ReferenceEquals(_owner._activeWriteController, Controller)) _owner._activeWriteController = null;
        }
    }
}

public enum TransformSpace
{
    World = 0,
    Local = 1,
}

public enum ParentPoseMode
{
    KeepWorld = 0,
    KeepLocal = 1,
    SnapToParent = 2,
}
