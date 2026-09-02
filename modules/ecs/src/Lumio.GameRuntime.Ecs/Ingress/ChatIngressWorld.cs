using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Reflection;
using System.Text;

namespace Lumio.GameRuntime.Ecs.Ingress;

/// <summary>
/// Owner-thread ChatComponent world used by the unified command path.
/// Registers persist-only last-message fields and creates live entities for chat.input.
/// </summary>
public sealed class ChatIngressWorld : IDisposable
{
    /// <summary>Registered component type name consumed by CommandBuffer writes.</summary>
    public const string ComponentName = "ChatComponent";

    /// <summary>Registered entity type name.</summary>
    public const string EntityTypeName = "Chatter";

    /// <summary>CommandBuffer field id for lastMessageText.</summary>
    public const string LastMessageTextFieldId = "1";

    /// <summary>CommandBuffer field id for lastMessageTick.</summary>
    public const string LastMessageTickFieldId = "2";

    /// <summary>UTF-8 cap matching C-1 chat.component.lastMessageText.</summary>
    public const int LastMessageTextMaxUtf8Bytes = 512;

    internal static readonly ComponentTypeId ChatComponentType = new(40);
    internal static readonly ComponentFieldId LastMessageTextField = new(1);
    internal static readonly ComponentFieldId LastMessageTickField = new(2);

    private const int LastMessageTextSizeBytes = 4 + LastMessageTextMaxUtf8Bytes;
    private const int LastMessageTickSizeBytes = 8;
    // ADR-032: recordVersion + recordSeq + schemaEpoch + two length-prefixed sha256 hex hashes.
    private const int PersistRecordHeaderBytes = 8 + 8 + 8 + (4 + 64) + (4 + 64);

    private readonly object _gate = new();
    private readonly ReferenceWorldStorageAdapter _storage;
    private readonly EcsWorld _world;
    private readonly EntityTypeHandle _entityType;
    private readonly Dictionary<string, LocalEntityId> _entities = new(StringComparer.Ordinal);
    private readonly Dictionary<LocalEntityId, string> _netByLocal = new();
    private bool _disposed;

    private ChatIngressWorld(
        ReferenceWorldStorageAdapter storage,
        EcsWorld world,
        EntityTypeHandle entityType)
    {
        _storage = storage;
        _world = world;
        _entityType = entityType;
    }

    /// <summary>The authoritative ECS world that CommandBuffer commit writes into.</summary>
    public EcsWorld World => _world;

    /// <summary>Creates a running ChatComponent world bound to the calling thread.</summary>
    public static ChatIngressWorld Create(int maxEntities = 128)
    {
        var worldId = new WorldId(370);
        var budget = new EcsBudget(maxEntities, 128, 128, PersistSnapshotBudgetBytes(maxEntities));
        var request = new EcsWorldCreateRequest(worldId, budget);
        var storage = new ReferenceWorldStorageAdapter(worldId, maxEntities, budget.MaxSnapshotBytes);
        var world = new EcsWorld(in request, storage);
        if (world.BeginRegistration().Status != StorageOperationStatus.Accepted)
            throw new InvalidOperationException("Chat world failed to enter registration.");

        ComponentTypeRegistrationResult component = RegisterComponent(world, ChatComponentDefinition());
        if (!component.Registered)
            throw new InvalidOperationException("ChatComponent registration failed.");

        EntityTypeRegistrationResult entityType = world.RegisterEntityType(
            new EntityTypeDefinition(EntityTypeName, new[] { component.Handle }));
        if (!entityType.Registered)
            throw new InvalidOperationException("Chatter entity type registration failed.");

        if (world.MarkReady().Status != StorageOperationStatus.Accepted ||
            world.Start().Status != StorageOperationStatus.Accepted)
            throw new InvalidOperationException("Chat world failed to enter Running on the owner thread.");

        return new ChatIngressWorld(storage, world, entityType.Handle);
    }

    /// <summary>Attaches an empty ChatComponent to a live NetEntityId. Owner thread only.</summary>
    public bool TryCreateEntity(string netEntityId, out LocalEntityId localId)
    {
        localId = default;
        if (string.IsNullOrEmpty(netEntityId)) return false;
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_entities.TryGetValue(netEntityId, out localId))
                return true;

            EntityCreateResult created = _world.CreateEntityForCommit(
                _world.Context,
                new EntityCreateRequest(_entityType));
            if (!created.Created) return false;
            localId = created.Entity;
            _entities[netEntityId] = localId;
            _netByLocal[localId] = netEntityId;
            return true;
        }
    }

    /// <summary>Resolves a previously created NetEntityId.</summary>
    public bool TryGetEntity(string netEntityId, out LocalEntityId localId)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            return _entities.TryGetValue(netEntityId ?? string.Empty, out localId);
        }
    }

    /// <summary>Resolves the NetEntityId for a live local entity.</summary>
    public bool TryGetNetEntityId(LocalEntityId localId, out string netEntityId)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            return _netByLocal.TryGetValue(localId, out netEntityId!);
        }
    }

    /// <summary>Live NetEntityIds in canonical local-id order.</summary>
    public IReadOnlyList<string> LiveNetEntityIds
    {
        get
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                var destination = new LocalEntityId[_world.Budget.MaxEntities];
                StorageOperationResult enumerated = _world.EnumerateActiveEntities(_world, destination, out int written);
                if (!enumerated.IsSuccess) return Array.Empty<string>();
                var ids = new List<string>(written);
                for (int i = 0; i < written; i++)
                {
                    if (_netByLocal.TryGetValue(destination[i], out string? net))
                        ids.Add(net);
                }

                ids.Sort(StringComparer.Ordinal);
                return ids;
            }
        }
    }

    /// <summary>Destroys the entity and retires its NetEntityId. Owner thread only.</summary>
    public bool DestroyEntity(string netEntityId)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            string id = netEntityId ?? string.Empty;
            if (!_entities.TryGetValue(id, out LocalEntityId localId))
                return false;
            if (!_world.TryGetCommitTarget(localId, out EcsWorld.WorldEntityTarget target))
                return false;
            EntityDestroyResult destroyed = _world.DestroyEntityForCommit(target);
            if (!destroyed.Destroyed) return false;
            _entities.Remove(id);
            _netByLocal.Remove(localId);
            return true;
        }
    }

    /// <summary>Reads persist-only last-message fields for a live entity.</summary>
    public bool TryReadLastMessage(LocalEntityId entity, out string text, out ulong tick)
    {
        text = string.Empty;
        tick = 0UL;
        var textBytes = new byte[LastMessageTextSizeBytes];
        var tickBytes = new byte[LastMessageTickSizeBytes];
        StorageOperationResult textRead = _storage.ReadField(
            entity, ChatComponentType, LastMessageTextField, textBytes, out int textWritten);
        StorageOperationResult tickRead = _storage.ReadField(
            entity, ChatComponentType, LastMessageTickField, tickBytes, out int tickWritten);
        if (!textRead.IsSuccess || !tickRead.IsSuccess) return false;
        if (textWritten != LastMessageTextSizeBytes || tickWritten != LastMessageTickSizeBytes) return false;
        if (!TryDecodeText(textBytes, out text)) return false;
        tick = BinaryPrimitives.ReadUInt64LittleEndian(tickBytes);
        return true;
    }

    /// <summary>
    /// Owner-thread last-message write used by fail-stop probes.
    /// Off-thread calls fault the world with zero field mutation.
    /// </summary>
    public StorageOperationResult TryWriteLastMessage(LocalEntityId entity, string text, ulong tick)
    {
#if NET10_0_OR_GREATER
        ArgumentNullException.ThrowIfNull(text);
#else
        if (text is null) throw new ArgumentNullException(nameof(text));
#endif
        byte[] textBytes = EncodeText(text);
        byte[] tickBytes = EncodeTick(tick);
        var discard = DiscardingChangeSet.Instance;
        StorageOperationResult writtenText = _world.WriteExistingField(
            new EcsFieldWrite(entity, ChatComponentType, LastMessageTextField, textBytes),
            discard);
        if (!writtenText.IsSuccess) return writtenText;
        return _world.WriteExistingField(
            new EcsFieldWrite(entity, ChatComponentType, LastMessageTickField, tickBytes),
            discard);
    }

    /// <summary>Canonical lastMessageText bytes for a CommandBuffer write.</summary>
    public static byte[] EncodeText(string text)
    {
        byte[] utf8 = Encoding.UTF8.GetBytes(text ?? string.Empty);
        if (utf8.Length > LastMessageTextMaxUtf8Bytes)
            throw new ArgumentOutOfRangeException(nameof(text));
        var field = new byte[LastMessageTextSizeBytes];
        BinaryPrimitives.WriteUInt32LittleEndian(field, (uint)utf8.Length);
        utf8.CopyTo(field.AsSpan(4));
        return field;
    }

    /// <summary>Canonical lastMessageTick bytes for a CommandBuffer write.</summary>
    public static byte[] EncodeTick(ulong tick)
    {
        var field = new byte[LastMessageTickSizeBytes];
        BinaryPrimitives.WriteUInt64LittleEndian(field, tick);
        return field;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }

        _world.ForceCleanup();
    }

    private void ThrowIfDisposed()
    {
#if NET10_0_OR_GREATER
        ObjectDisposedException.ThrowIf(_disposed, this);
#else
        if (_disposed) throw new ObjectDisposedException(nameof(ChatIngressWorld));
#endif
    }

    private static ComponentTypeRegistrationResult RegisterComponent(
        EcsWorld world,
        ComponentTypeDefinition definition)
    {
        FieldInfo capabilityField = typeof(EcsWorld).GetField(
            "_componentRegistrationCapability",
            BindingFlags.Instance | BindingFlags.NonPublic) ??
            throw new InvalidOperationException("World component-registration capability is missing.");
        var capability = (EcsWorld.ComponentRegistrationCapability)(capabilityField.GetValue(world) ??
            throw new InvalidOperationException("World component-registration capability is null."));
        return world.RegisterComponentType(capability, definition);
    }

    private static ComponentTypeDefinition ChatComponentDefinition() => new(
        ChatComponentType,
        ComponentName,
        new[]
        {
            new ComponentFieldDefinition(
                LastMessageTextField,
                LastMessageTextSizeBytes,
                ComponentFieldPersistence.PersistOnly),
            new ComponentFieldDefinition(
                LastMessageTickField,
                LastMessageTickSizeBytes,
                ComponentFieldPersistence.PersistOnly)
        });

    private static int PersistSnapshotBudgetBytes(int maxEntities)
    {
        int persistBytesPerEntity = LastMessageTextSizeBytes + LastMessageTickSizeBytes;
        return checked(persistBytesPerEntity * maxEntities + PersistRecordHeaderBytes);
    }

    private static bool TryDecodeText(byte[] field, out string text)
    {
        text = string.Empty;
        if (field.Length < 4) return false;
        uint length = BinaryPrimitives.ReadUInt32LittleEndian(field);
        if (length > LastMessageTextMaxUtf8Bytes || 4 + length > (uint)field.Length) return false;
        text = Encoding.UTF8.GetString(field, 4, (int)length);
        return true;
    }

    private sealed class DiscardingChangeSet : IEcsChangeSetAppend
    {
        internal static readonly DiscardingChangeSet Instance = new();

        public StorageOperationResult TryAppend(in ChangeEntry entry) => StorageOperationResult.Accepted();
    }
}
