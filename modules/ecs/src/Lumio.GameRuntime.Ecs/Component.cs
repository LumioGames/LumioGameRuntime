using System;

namespace Lumio.GameRuntime.Ecs;

/// <summary>
/// Gameplay component base. Typed reads use <see cref="Get{T}()"/> (self) or
/// <see cref="Get{T}(NetEntityId)"/> (other). Lifecycle hooks are optional overrides.
/// </summary>
public abstract class Component
{
    internal World? WorldInternal;
    internal NetEntityId EntityId;
    internal object? Record;

    /// <summary>World that owns this component.</summary>
    public World World => WorldInternal ?? throw new InvalidOperationException("Component is not attached to a world.");

    /// <summary>Network identity of the owning entity.</summary>
    public NetEntityId Entity => EntityId;

    /// <summary>Sender and tick of the RPC currently being dispatched, if any.</summary>
    public RpcContext Rpc { get; internal set; }

    /// <summary>Reads another component on the same entity.</summary>
    public T Get<T>() where T : Component
    {
        if (WorldInternal is null) throw new InvalidOperationException("Component is not attached to a world.");
        return WorldInternal.Get<T>(EntityId);
    }

    /// <summary>Reads a component on another entity.</summary>
    public T Get<T>(NetEntityId id) where T : Component
    {
        if (WorldInternal is null) throw new InvalidOperationException("Component is not attached to a world.");
        return WorldInternal.Get<T>(id);
    }

    /// <summary>Called when the entity first appears. Fields may still be defaults on the client.</summary>
    protected internal virtual void Awake()
    {
    }

    /// <summary>Called after appearance (and after generated PostAttribute on the client).</summary>
    protected internal virtual void Start()
    {
    }

    /// <summary>Called when the entity becomes enabled.</summary>
    protected internal virtual void OnEnable()
    {
    }

    /// <summary>Called when the entity becomes disabled.</summary>
    protected internal virtual void OnDisable()
    {
    }

    /// <summary>Called when the entity is destroyed.</summary>
    protected internal virtual void OnDestroy()
    {
    }

    /// <summary>Called on restore from snapshot instead of Awake/Start.</summary>
    protected internal virtual void OnHydrate()
    {
    }

    /// <summary>Called when this entity enters the local observer's AOI.</summary>
    protected internal virtual void OnEnterAOI()
    {
    }

    /// <summary>Called when this entity leaves the local observer's AOI.</summary>
    protected internal virtual void OnLeaveAOI()
    {
    }

    /// <summary>Generated ClientRpc send stub calls this on the server.</summary>
    protected internal void EmitClientRpc(string method, params object?[] args)
        => EmitClientRpc(method, Scope.Room, args);

    protected internal void EmitClientRpc(string method, Scope scope, params object?[] args)
    {
        if (WorldInternal is null) return;
        WorldInternal.EnqueueClientRpc(this, method, scope, args ?? Array.Empty<object?>());
    }

    /// <summary>Generated ServerRpc send stub calls this on the client.</summary>
    protected internal void EmitServerRpc(string method, params object?[] args)
    {
        if (WorldInternal is null) return;
        WorldInternal.EnqueueServerRpc(this, method, args ?? Array.Empty<object?>());
    }
}

/// <summary>RPC dispatch context visible to a component during a handler.</summary>
public readonly struct RpcContext
{
    /// <summary>Creates a dispatch context.</summary>
    public RpcContext(NetEntityId sender, ulong tick)
    {
        Sender = sender;
        Tick = tick;
    }

    /// <summary>Sender entity of the current RPC, if any.</summary>
    public NetEntityId Sender { get; }

    /// <summary>Tick being applied.</summary>
    public ulong Tick { get; }
}

/// <summary>Generated hook dispatcher implemented on component <c>.g.cs</c> files.</summary>
public interface IGeneratedComponent
{
    void BindFields(ISyncHost host);
    void InvokePostAttribute();
    void InvokeFieldChanging(int ordinal, object? oldValue, object? newValue, ChangeReason reason);
    void InvokeFieldChanged(int ordinal, object? oldValue, object? newValue, ChangeReason reason);
    bool DispatchClientWrite(in SyncWrite write);
    void DispatchServerRpc(string method, object?[] args);
    void DispatchClientRpc(string method, object?[] args);
    void CapturePersist(IPersistWriter writer);
    void CaptureSync(IPersistWriter writer);
    void RestorePersist(IPersistReader reader);
    object? ReadField(string fieldId);
    void WriteField(string fieldId, object? value, bool silent);
}

/// <summary>Generated metadata bridge used by the World Manager without reflection.</summary>
public interface IGeneratedSyncMetadata
{
    bool TryGetSyncField(string fieldId, out ISyncField field);
}

/// <summary>Writes persistable members into a snapshot.</summary>
public interface IPersistWriter
{
    void WriteString(string attributeId, string? value);
    void WriteUInt64(string attributeId, ulong value);
    void WriteBoolean(string attributeId, bool value);
    void WriteContainer(string attributeId, object value) { }
}

/// <summary>Optional generated bridge for replicated container snapshots.</summary>
public interface IContainerFieldWriter
{
    void WriteContainer(string attributeId, object value);
}

/// <summary>Reads persistable members from a snapshot.</summary>
public interface IPersistReader
{
    bool TryReadString(string attributeId, out string value);
    bool TryReadUInt64(string attributeId, out ulong value);
    bool TryReadBoolean(string attributeId, out bool value);
    bool TryReadContainer(string attributeId, out object value)
    {
        value = string.Empty;
        return false;
    }
}

internal sealed class TransformSyncField : ISyncField
{
    private readonly LogicTransform _owner;
    private readonly string _fieldId;
    private readonly Func<string> _read;
    private readonly Action<string> _assign;

    internal TransformSyncField(LogicTransform owner, int ordinal, string fieldId, Func<string> read, Action<string> assign)
    {
        _owner = owner;
        Ordinal = ordinal;
        _fieldId = fieldId;
        _read = read;
        _assign = assign;
    }

    public int Ordinal { get; }
    public string AttributeId => "LogicTransform." + _fieldId;
    public Scope Scope => Scope.Room;
    public Authority Authority => Authority.Server;
    public Notify Notify => Notify.Remote;
    public string? ClaimBy => null;
    public Type ValueType => typeof(string);
    public object? BoxedValue => _read();
    public Component Owner => _owner;
    public void AssignFromRemote(object? value) => _assign(value?.ToString() ?? string.Empty);
}
