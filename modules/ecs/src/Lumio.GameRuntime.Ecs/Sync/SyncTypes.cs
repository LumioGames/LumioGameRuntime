using System;
using System.Collections.Generic;

namespace Lumio.GameRuntime.Ecs;

/// <summary>Who may observe a <see cref="Sync{T}"/> field.</summary>
public enum Scope
{
    /// <summary>Every observer in the Game instance.</summary>
    Room = 0,

    /// <summary>Observers whose AOI contains the entity.</summary>
    Aoi = 1,

    /// <summary>The bound owner connection only.</summary>
    Owner = 2,

    /// <summary>Observers that hold the matching claim.</summary>
    Claim = 3,
}

/// <summary>Who may write a <see cref="Sync{T}"/> field.</summary>
public enum Authority
{
    /// <summary>Only the server world may write; client writes are rejected.</summary>
    Server = 0,

    /// <summary>The bound owner connection may write; the write is auto-uploaded.</summary>
    Owner = 1,
}

/// <summary>When generated field hooks fire.</summary>
public enum Notify
{
    /// <summary>Default: only remote (peer or correction) changes fire hooks.</summary>
    Remote = 0,

    /// <summary>Local writes also fire hooks with <see cref="ChangeReason.Local"/>.</summary>
    All = 1,
}

/// <summary>Why a generated field hook fired.</summary>
public enum ChangeReason
{
    /// <summary>A peer value arrived through the world-change stream.</summary>
    Sync = 0,

    /// <summary>An owner write was rejected and the authority value was pushed back.</summary>
    Correction = 1,

    /// <summary>A local <c>.Value</c> assignment (only when <see cref="Notify.All"/>).</summary>
    Local = 2,
}

/// <summary>Container op for <see cref="SyncList{T}"/> hooks.</summary>
public enum ListOp
{
    /// <summary>Replace the value at <see cref="ListChange{T}.Index"/>.</summary>
    Set = 0,

    /// <summary>Insert at <see cref="ListChange{T}.Index"/>.</summary>
    Insert = 1,

    /// <summary>Remove at <see cref="ListChange{T}.Index"/>.</summary>
    Remove = 2,

    /// <summary>Remove every entry.</summary>
    Clear = 3,
}

/// <summary>Container op for <see cref="SyncDict{TKey,TValue}"/> hooks.</summary>
public enum DictOp
{
    /// <summary>Set or replace <see cref="DictChange{TKey,TValue}.Key"/>.</summary>
    Set = 0,

    /// <summary>Remove <see cref="DictChange{TKey,TValue}.Key"/>.</summary>
    Remove = 1,

    /// <summary>Remove every entry.</summary>
    Clear = 2,
}

/// <summary>One <see cref="SyncList{T}"/> mutation delivered to a generated hook.</summary>
public readonly struct ListChange<T>
{
    /// <summary>Creates a list change payload.</summary>
    public ListChange(ListOp op, int index, T? old, T? @new, ChangeReason reason)
    {
        Op = op;
        Index = index;
        Old = old;
        New = @new;
        Reason = reason;
    }

    /// <summary>Mutation kind.</summary>
    public ListOp Op { get; }

    /// <summary>Affected index, or <c>-1</c> for <see cref="ListOp.Clear"/>.</summary>
    public int Index { get; }

    /// <summary>Previous value when applicable.</summary>
    public T? Old { get; }

    /// <summary>New value when applicable.</summary>
    public T? New { get; }

    /// <summary>Why the mutation is observed.</summary>
    public ChangeReason Reason { get; }
}

/// <summary>One <see cref="SyncDict{TKey,TValue}"/> mutation delivered to a generated hook.</summary>
public readonly struct DictChange<TKey, TValue>
{
    /// <summary>Creates a dictionary change payload.</summary>
    public DictChange(DictOp op, TKey? key, TValue? old, TValue? @new, ChangeReason reason)
    {
        Op = op;
        Key = key;
        Old = old;
        New = @new;
        Reason = reason;
    }

    /// <summary>Mutation kind.</summary>
    public DictOp Op { get; }

    /// <summary>Affected key, default for <see cref="DictOp.Clear"/>.</summary>
    public TKey? Key { get; }

    /// <summary>Previous value when applicable.</summary>
    public TValue? Old { get; }

    /// <summary>New value when applicable.</summary>
    public TValue? New { get; }

    /// <summary>Why the mutation is observed.</summary>
    public ChangeReason Reason { get; }
}

/// <summary>
/// Owner-write envelope delivered to <c>OnClientWrite</c>.
/// <c>accept</c> enters true; set false to reject and trigger a correction.
/// </summary>
public readonly struct SyncWrite
{
    private readonly ISyncField? _field;
    private readonly object? _value;

    internal SyncWrite(ISyncField field, object? value)
    {
        _field = field;
        _value = value;
    }

    /// <summary>True when <paramref name="field"/> is the field being written.</summary>
    public bool Is<T>(in Sync<T> field) =>
        _field is not null && field.Identity is not null &&
        string.Equals(_field.AttributeId, field.AttributeId, StringComparison.Ordinal);

    /// <summary>Payload decoded as <typeparamref name="T"/>.</summary>
    public T Value<T>() => (T)_value!;
}

/// <summary>Bound <see cref="Sync{T}"/> location used by generated dispatch.</summary>
public interface ISyncField
{
    int Ordinal { get; }
    string AttributeId { get; }
    Scope Scope { get; }
    Authority Authority { get; }
    Notify Notify { get; }
    string? ClaimBy { get; }
    Type ValueType { get; }
    object? BoxedValue { get; }
    Component Owner { get; }
    void AssignFromRemote(object? value);
}

/// <summary>Metadata shared by replicated container fields.</summary>
public interface ISyncContainer
{
    Scope Scope { get; }
    Authority Authority { get; }
    Notify Notify { get; }
    string? ClaimBy { get; }
    void ResetForReuse();
}

/// <summary>World-facing dirty sink used by <see cref="Sync{T}"/> setters.</summary>
public interface ISyncHost
{
    bool IsServer { get; }
    bool IsApplyingRemote { get; }
    WorldManager Manager { get; }
    World World { get; }
    void OnLocalWrite(Component owner, ISyncField field, object? oldValue, object? newValue);
}

/// <summary>
/// Replicated scalar. Write <see cref="Value"/>; read via implicit conversion.
/// Must remain a struct (ADR-058 §7 / §9).
/// </summary>
public struct Sync<T> : ISyncField
{
    private T _value;
    private ISyncHost? _host;
    private Component? _owner;
    private int _ordinal;
    private string? _attributeId;
    private Scope _scope;
    private Authority _authority;
    private Notify _notify;
    private string? _claimBy;

    /// <summary>Declares a replicated field. Default authority is server; default notify is remote.</summary>
    public Sync(Scope scope, Authority authority = Authority.Server, Notify notify = Notify.Remote, string? claimBy = null)
    {
        _value = default!;
        _host = null;
        _owner = null;
        _ordinal = -1;
        _attributeId = null;
        _scope = scope;
        _authority = authority;
        _notify = notify;
        _claimBy = claimBy;
    }

    /// <summary>Current value. Setter marks dirty and, on an owner client, auto-uploads.</summary>
    public T Value
    {
        get => _value;
        set
        {
            T old = _value;
            if (EqualityComparer<T>.Default.Equals(old, value))
            {
                _value = value;
                return;
            }

            _value = value;
            ISyncHost? host = _host;
            if (host is not null && !host.IsApplyingRemote)
                host.OnLocalWrite(_owner!, this, old, value);
        }
    }

    /// <summary>Reads the current value.</summary>
    public static implicit operator T(Sync<T> field) => field.Value;

    internal ISyncField? Identity => _attributeId is null ? null : this;

    internal Scope DeclaredScope => _scope;
    internal Authority DeclaredAuthority => _authority;
    internal Notify DeclaredNotify => _notify;
    public string? ClaimBy => _claimBy;
    internal string AttributeId => _attributeId ?? string.Empty;

    /// <summary>Binds this field to a world slot. Called from generated <c>BindFields</c>.</summary>
    public Sync<T> Bound(ISyncHost host, Component owner, int ordinal, string attributeId)
    {
        Sync<T> copy = this;
        copy._host = host;
        copy._owner = owner;
        copy._ordinal = ordinal;
        copy._attributeId = attributeId;
        return copy;
    }

    /// <summary>Writes without dirty/upload. Used for downlink and snapshot restore.</summary>
    public void SetSilent(T value)
    {
        _value = value;
    }

    int ISyncField.Ordinal => _ordinal;
    string ISyncField.AttributeId => _attributeId ?? string.Empty;
    Scope ISyncField.Scope => _scope;
    Authority ISyncField.Authority => _authority;
    Notify ISyncField.Notify => _notify;
    string? ISyncField.ClaimBy => _claimBy;
    Type ISyncField.ValueType => typeof(T);
    object? ISyncField.BoxedValue => _value;
    Component ISyncField.Owner => _owner!;
    void ISyncField.AssignFromRemote(object? value)
    {
        if (value is T typed) _value = typed;
    }
}

/// <summary>Replicated list. Mutations are reported per entry.</summary>
public sealed class SyncList<T> : ISyncContainer
{
    private readonly List<T> _items = new();

    /// <summary>Declares a replicated list.</summary>
    public SyncList(Scope scope, Authority authority = Authority.Server, Notify notify = Notify.Remote, string? claimBy = null)
    {
        Scope = scope;
        Authority = authority;
        Notify = notify;
        ClaimBy = claimBy;
    }

    /// <summary>Visibility of the list.</summary>
    public Scope Scope { get; }

    /// <summary>Write authority of the list.</summary>
    public Authority Authority { get; }

    /// <summary>Hook notify mode.</summary>
    public Notify Notify { get; }

    /// <summary>Same-component Sync container that supplies claims for this field.</summary>
    public string? ClaimBy { get; }

    /// <summary>Number of entries.</summary>
    public int Count => _items.Count;

    /// <summary>Entry at <paramref name="index"/>.</summary>
    public T this[int index]
    {
        get => _items[index];
        set => _items[index] = value;
    }

    /// <summary>Appends <paramref name="item"/>.</summary>
    public void Add(T item) => _items.Add(item);

    /// <summary>Inserts <paramref name="item"/> at <paramref name="index"/>.</summary>
    public void Insert(int index, T item) => _items.Insert(index, item);

    /// <summary>Removes the entry at <paramref name="index"/>.</summary>
    public void RemoveAt(int index) => _items.RemoveAt(index);

    /// <summary>Removes every entry.</summary>
    public void Clear() => _items.Clear();

    /// <summary>Returns true when the list contains <paramref name="item"/>.</summary>
    public bool Contains(T item) => _items.Contains(item);

    /// <summary>Enumerates the current entries.</summary>
    public IReadOnlyList<T> Values => _items;

    void ISyncContainer.ResetForReuse() => _items.Clear();
}

/// <summary>Replicated dictionary. Mutations are reported per key.</summary>
public sealed class SyncDict<TKey, TValue> : ISyncContainer where TKey : notnull
{
    private readonly Dictionary<TKey, TValue> _items = new();

    /// <summary>Declares a replicated dictionary.</summary>
    public SyncDict(Scope scope, Authority authority = Authority.Server, Notify notify = Notify.Remote, string? claimBy = null)
    {
        Scope = scope;
        Authority = authority;
        Notify = notify;
        ClaimBy = claimBy;
    }

    /// <summary>Visibility of the dictionary.</summary>
    public Scope Scope { get; }

    /// <summary>Write authority of the dictionary.</summary>
    public Authority Authority { get; }

    /// <summary>Hook notify mode.</summary>
    public Notify Notify { get; }

    /// <summary>Same-component Sync container that supplies claims for this field.</summary>
    public string? ClaimBy { get; }

    /// <summary>Number of entries.</summary>
    public int Count => _items.Count;

    /// <summary>Value for <paramref name="key"/>.</summary>
    public TValue this[TKey key]
    {
        get => _items[key];
        set => _items[key] = value;
    }

    /// <summary>Removes <paramref name="key"/>.</summary>
    public bool Remove(TKey key) => _items.Remove(key);

    /// <summary>Removes every entry.</summary>
    public void Clear() => _items.Clear();

    /// <summary>Returns true when the dictionary contains <paramref name="key"/>.</summary>
    public bool ContainsKey(TKey key) => _items.ContainsKey(key);

    /// <summary>Enumerates the current entries.</summary>
    public IReadOnlyDictionary<TKey, TValue> Values => _items;

    void ISyncContainer.ResetForReuse() => _items.Clear();
}
