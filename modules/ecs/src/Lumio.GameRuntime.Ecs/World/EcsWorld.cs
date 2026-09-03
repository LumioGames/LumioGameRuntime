using System;
using System.Collections.Generic;
using System.Globalization;

namespace Lumio.GameRuntime.Ecs;

internal readonly record struct EntityCreateRequest(
    EntityTypeHandle Type,
    ComponentInitBatch InitialValues = default,
    EcsOperationEvidence Evidence = default);

internal readonly record struct EntityCreateResult(
    bool Created,
    LocalEntityId Entity,
    StorageOperationResult Result,
    EntityLifecycleState State = EntityLifecycleState.Reserved,
    EntityMode? Mode = null,
    EcsWorld.WorldEntityTarget? Target = null)
{
    public ErrorIdentity? Error => Result.Error;
    public static EntityCreateResult Rejected(ErrorIdentity error) =>
        new(false, default, new(StorageOperationStatus.Rejected, error));
}

public readonly record struct EcsWorldCreateRequest(WorldId WorldId, EcsBudget Budget)
{
    public bool IsValid => !WorldId.IsDefault && Budget.IsValid;
}

public readonly record struct EcsWorldCreateResult(
    bool Created,
    EcsWorld? World,
    ErrorIdentity? Error)
{
    public static EcsWorldCreateResult Rejected(string code) =>
        new(false, null, new ErrorIdentity(code));
}

/// <summary>World-local ECS owner and lifecycle boundary.</summary>
public sealed partial class EcsWorld
{
    /// <summary>Opaque capability that binds internal operations to one World incarnation.</summary>
    internal abstract class EcsWorldContext
    {
        private protected EcsWorldContext()
        {
        }
    }

    private sealed class IssuedEcsWorldContext : EcsWorldContext
    {
        private readonly EcsWorld _owner;

        public IssuedEcsWorldContext(EcsWorld owner)
        {
            _owner = owner ?? throw new ArgumentNullException(nameof(owner));
        }

        internal bool BelongsTo(EcsWorld world) => ReferenceEquals(_owner, world);
    }

    /// <summary>World-qualified entity capability used by internal commit operations.</summary>
    internal abstract class WorldEntityTarget
    {
        private protected WorldEntityTarget()
        {
        }

        internal abstract EcsWorldContext Origin { get; }
        internal abstract LocalEntityId Entity { get; }
        internal bool IsDefault => Entity.IsDefault;
    }

    private sealed class IssuedWorldEntityTarget : WorldEntityTarget
    {
        public IssuedWorldEntityTarget(EcsWorldContext origin, LocalEntityId entity)
        {
            Origin = origin;
            Entity = entity;
        }

        internal override EcsWorldContext Origin { get; }
        internal override LocalEntityId Entity { get; }
    }

    internal abstract class ComponentRegistrationCapability
    {
        private protected ComponentRegistrationCapability()
        {
        }
    }

    private sealed class IssuedComponentRegistrationCapability : ComponentRegistrationCapability
    {
    }

    private readonly object _lifecycleSync = new();
    private readonly EcsWorldContext _context;
    private readonly ComponentRegistrationCapability _componentRegistrationCapability =
        new IssuedComponentRegistrationCapability();
    private readonly OwnerThreadGuard _ownerThread;
    private readonly EcsFailStopController _failStop;
    private readonly EntitySlotTable _entities;
    private readonly IWorldStorageAdapter _storage;
    private readonly ComponentTypeRegistry _componentTypes;
    private readonly Dictionary<EntityTypeHandle, EntityTypeDefinition> _entityTypes = new();
    private readonly Dictionary<string, ComponentTypeDefinition> _componentsByName = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (EntityTypeHandle Handle, EntityTypeDefinition Definition)> _entityTypesByName =
        new(StringComparer.Ordinal);
    private readonly HashSet<EntityTypeDefinition> _entityTypeDefinitions = new();
    private readonly HashSet<StorageReadSnapshotHandle> _activeSnapshotLeases = new();
    private readonly HashSet<StorageReadSnapshotHandle> _releasedSnapshotLeases = new();
    private readonly EcsBudget _budget;
    private uint _nextEntityTypeHandle;
    private EcsWorldState _state;
    private EcsFaultEvidence? _firstFault;

    internal EcsWorld(in EcsWorldCreateRequest request)
        : this(request, new ReferenceWorldStorageAdapter(
            request.WorldId,
            request.Budget.MaxEntities,
            request.Budget.MaxSnapshotBytes))
    {
    }

    internal EcsWorld(in EcsWorldCreateRequest request, IWorldStorageAdapter storage)
        : this(request, storage, new EntitySlotTable(request.Budget.MaxEntities))
    {
    }

    internal EcsWorld(
        in EcsWorldCreateRequest request,
        IWorldStorageAdapter storage,
        EntitySlotTable entities)
        : this(
            request,
            storage,
            entities,
            ManagedOwnerThreadTokenProvider.Instance,
            NullEcsDurableFailureSink.Instance)
    {
    }

    internal EcsWorld(
        in EcsWorldCreateRequest request,
        IWorldStorageAdapter storage,
        EntitySlotTable entities,
        IOwnerThreadTokenProvider ownerTokens,
        IEcsDurableFailureSink failureSink)
    {
        if (!request.IsValid)
            throw new ArgumentException("World id and ECS budget must be valid.", nameof(request));
#if NET10_0_OR_GREATER
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(entities);
        ArgumentNullException.ThrowIfNull(ownerTokens);
        ArgumentNullException.ThrowIfNull(failureSink);
#else
        if (storage is null) throw new ArgumentNullException(nameof(storage));
        if (entities is null) throw new ArgumentNullException(nameof(entities));
        if (ownerTokens is null) throw new ArgumentNullException(nameof(ownerTokens));
        if (failureSink is null) throw new ArgumentNullException(nameof(failureSink));
#endif
        if (entities.Capacity != request.Budget.MaxEntities)
            throw new ArgumentException("Entity slot capacity must match the World budget.", nameof(entities));

        WorldId = request.WorldId;
        _budget = request.Budget;
        _budget.Validate();
        _context = new IssuedEcsWorldContext(this);
        _componentTypes = new ComponentTypeRegistry(request.WorldId, _context);
        _entities = entities;
        _storage = storage;
        _ownerThread = new OwnerThreadGuard(ownerTokens);
        _failStop = new EcsFailStopController(failureSink);
        _state = EcsWorldState.Created;
    }

    public WorldId WorldId { get; }

    internal EcsWorldContext Context => _context;

    public EcsWorldState State
    {
        get
        {
            lock (_lifecycleSync) return _state;
        }
    }

    public EcsBudget Budget => _budget;

    public int ActiveEntityCount
    {
        get
        {
            lock (_lifecycleSync)
            {
                return _state is EcsWorldState.Faulted or EcsWorldState.Disposed
                    ? 0
                    : _entities.ActiveCount;
            }
        }
    }

    public int OwnerThreadId => _ownerThread.OwnerThreadId;

    internal EcsFaultEvidence? FirstFault
    {
        get
        {
            lock (_lifecycleSync) return _failStop.First ?? _firstFault;
        }
    }

    internal StorageOperationResult BeginRegistration()
    {
        lock (_lifecycleSync)
        {
            if (_state != EcsWorldState.Created) return RejectForState();
            _state = EcsWorldState.Registering;
            return StorageOperationResult.Accepted();
        }
    }

    internal ComponentTypeRegistrationResult RegisterComponentType(
        ComponentRegistrationCapability capability,
        ComponentTypeDefinition definition)
    {
        lock (_lifecycleSync)
        {
            if (!ReferenceEquals(capability, _componentRegistrationCapability))
                return new ComponentTypeRegistrationResult(
                    false,
                    default,
                    StorageOperationResult.Rejected(EcsErrorCodes.WrongContext));
            if (_state != EcsWorldState.Registering)
                return new ComponentTypeRegistrationResult(false, default, RejectForState());
            if (definition is null)
                return new ComponentTypeRegistrationResult(
                    false,
                    default,
                    StorageOperationResult.Rejected(EcsErrorCodes.InvalidArgument));
            if (!_componentTypes.CanRegister(definition))
                return new ComponentTypeRegistrationResult(
                    false,
                    default,
                    StorageOperationResult.Rejected(EcsErrorCodes.DuplicateRegistration));

        StorageOperationResult storageResult;
        try
        {
            storageResult = _storage.Register(definition);
        }
        catch (Exception exception)
        {
            storageResult = CompleteBoundary(
                StorageOperationResult.Fatal(EcsErrorCodes.InvalidState),
                "RegisterComponentType",
                exception,
                componentType: definition.Id);
        }
        storageResult = CompleteBoundary(
            storageResult,
            "RegisterComponentType",
            componentType: definition.Id);
        if (!storageResult.IsSuccess)
            return new ComponentTypeRegistrationResult(false, default, storageResult);
        if (_state != EcsWorldState.Registering)
            return new ComponentTypeRegistrationResult(false, default, RejectForState());

        StorageOperationResult registryResult = _componentTypes.Register(definition, out ComponentTypeHandle handle);
        if (!registryResult.IsSuccess)
            return new ComponentTypeRegistrationResult(
                false,
                default,
                CompleteBoundary(
                    StorageOperationResult.Fatal(registryResult.Error?.Code ?? EcsErrorCodes.InvalidState),
                    "RegisterComponentType",
                    componentType: definition.Id));
            _componentsByName.TryAdd(definition.Name, definition);
            return new ComponentTypeRegistrationResult(true, handle, StorageOperationResult.Accepted());
        }
    }

    internal EntityTypeRegistrationResult RegisterEntityType(EntityTypeDefinition definition)
    {
        lock (_lifecycleSync)
        {
            if (_state != EcsWorldState.Registering)
                return new EntityTypeRegistrationResult(false, default, RejectForState());
            if (definition is null)
                return new EntityTypeRegistrationResult(
                    false,
                    default,
                    StorageOperationResult.Rejected(EcsErrorCodes.InvalidArgument));

        ReadOnlySpan<ComponentTypeHandle> components = definition.ComponentTypes.Span;
        for (int index = 0; index < components.Length; index++)
        {
            if (!_componentTypes.TryGet(components[index], out _))
                return new EntityTypeRegistrationResult(
                    false,
                    default,
                    StorageOperationResult.Rejected(EcsErrorCodes.InvalidType));
        }
        if (!_entityTypeDefinitions.Add(definition))
            return new EntityTypeRegistrationResult(
                false,
                default,
                StorageOperationResult.Rejected(EcsErrorCodes.DuplicateRegistration));
        if (_nextEntityTypeHandle == uint.MaxValue)
        {
            _entityTypeDefinitions.Remove(definition);
            return new EntityTypeRegistrationResult(
                false,
                default,
                CompleteBoundary(StorageOperationResult.Fatal(EcsErrorCodes.InvalidState), "RegisterEntityType"));
        }

        var handle = new EntityTypeHandle(WorldId, ++_nextEntityTypeHandle, _context);
        _entityTypes.Add(handle, definition);
            _entityTypesByName.TryAdd(definition.Name, (handle, definition));
            return new EntityTypeRegistrationResult(true, handle, StorageOperationResult.Accepted());
        }
    }

    internal StorageOperationResult MarkReady()
    {
        lock (_lifecycleSync)
        {
            if (_state != EcsWorldState.Registering) return RejectForState();
            StorageOperationResult integrity;
            try
            {
                integrity = _storage.ValidateIntegrity();
            }
            catch (Exception exception)
            {
                integrity = CompleteBoundary(
                    StorageOperationResult.Fatal(EcsErrorCodes.InvalidState),
                    "ValidateIntegrity",
                    exception);
            }
            integrity = CompleteBoundary(integrity, "ValidateIntegrity");
            if (!integrity.IsSuccess) return integrity;
            if (_state != EcsWorldState.Registering) return RejectForState();
            _state = EcsWorldState.Ready;
            return StorageOperationResult.Accepted();
        }
    }

    public StorageOperationResult Start()
    {
        lock (_lifecycleSync)
        {
            if (_state != EcsWorldState.Ready) return RejectForState();
            StorageOperationResult owner = _ownerThread.BindCurrentThread();
            if (!owner.IsSuccess) return owner;
            _state = EcsWorldState.Running;
            return StorageOperationResult.Accepted();
        }
    }

    public StorageOperationResult BeginDrain()
    {
        lock (_lifecycleSync)
        {
            if (_state != EcsWorldState.Running) return RejectForState();
            StorageOperationResult owner = _ownerThread.ValidateCurrentThread();
            if (!owner.IsSuccess) return owner;
            _state = EcsWorldState.Draining;
            return StorageOperationResult.Accepted();
        }
    }

    public StorageOperationResult Fault(ErrorIdentity error)
    {
        lock (_lifecycleSync)
        {
            if (string.IsNullOrWhiteSpace(error.Code))
                return StorageOperationResult.Rejected(EcsErrorCodes.InvalidArgument);
            if (_state == EcsWorldState.Disposed)
                return StorageOperationResult.Rejected(EcsErrorCodes.WorldDisposed);
            if (_ownerThread.IsBound)
            {
                StorageOperationResult owner = _ownerThread.ValidateCurrentThread();
                if (!owner.IsSuccess) return owner;
            }
            if (_state != EcsWorldState.Faulted)
            {
                _failStop.CaptureAdapterFailure(
                    StorageOperationResult.Fatal(error.Code),
                    ref _state,
                    new FailureContext(WorldId, default, null, default, default, default, "Fault"),
                    0);
                _firstFault = _failStop.First;
            }
            return StorageOperationResult.Accepted();
        }
    }

    public StorageOperationResult DisposeWorld()
    {
        lock (_lifecycleSync)
        {
            if (_state == EcsWorldState.Disposed) return StorageOperationResult.Accepted();
            if (_state is not EcsWorldState.Draining and not EcsWorldState.Faulted)
                return RejectForState();
            if (_ownerThread.IsBound)
            {
                StorageOperationResult owner = _ownerThread.ValidateCurrentThread();
                if (!owner.IsSuccess) return owner;
            }
            try
            {
                _storage.Dispose();
            }
            catch (Exception exception)
            {
                return CompleteBoundary(
                    StorageOperationResult.Fatal(EcsErrorCodes.InvalidState),
                    "Dispose",
                    exception);
            }

            _activeSnapshotLeases.Clear();
            _releasedSnapshotLeases.Clear();
            _state = EcsWorldState.Disposed;
            return StorageOperationResult.Accepted();
        }
    }

    /// <summary>Applies a structural create at the command commit boundary.</summary>
    internal EntityCreateResult CreateEntityForCommit(EcsWorldContext context, in EntityCreateRequest request)
    {
        if (!ReferenceEquals(context, _context))
            return EntityCreateResult.Rejected(new ErrorIdentity(EcsErrorCodes.CrossWorld));
        lock (_lifecycleSync)
        {
        StorageOperationResult state = EnsureWritableState();
        if (!state.IsSuccess) return new EntityCreateResult(false, default, state);
        StorageOperationResult owner = ValidateOwnerForWrite();
        if (!owner.IsSuccess) return new EntityCreateResult(false, default, owner);
        if (request.Type.IsDefault || request.Type.WorldId != WorldId ||
            !_entityTypes.TryGetValue(request.Type, out EntityTypeDefinition? definition))
            return EntityCreateResult.Rejected(new ErrorIdentity(EcsErrorCodes.InvalidType));

        StorageOperationResult typeResult = ValidateEntityType(definition, request.InitialValues);
        if (!typeResult.IsSuccess)
            return EntityCreateResult.Rejected(typeResult.Error!.Value);

        if (!_entities.TryAllocate(request.Type, definition.DefaultMode, out LocalEntityId entity, out StorageOperationResult allocation))
            return new EntityCreateResult(
                false,
                default,
                CompleteBoundary(
                    allocation,
                    "Allocate",
                    tickId: request.Evidence.TickId,
                    processorId: request.Evidence.ProcessorId,
                    evidenceIdentity: request.Evidence.EvidenceIdentity));

        ComponentInitBatch initialValues = ExpandInitialValues(definition, request.InitialValues);
        ComponentTypeId evidenceComponent = default;
        ComponentFieldId evidenceField = default;
        if (initialValues.Values.Length == 1)
        {
            ComponentInitValue value = initialValues.Values.Span[0];
            evidenceComponent = value.ComponentType;
            evidenceField = value.Field;
        }
        StorageOperationResult created;
        try
        {
            created = _storage.Create(entity, in initialValues);
        }
        catch (Exception exception)
        {
            _entities.TryRetire(entity);
            return new EntityCreateResult(
                false,
                entity,
                CompleteBoundary(
                    StorageOperationResult.Fatal(EcsErrorCodes.PostWriteFailure),
                    "Create",
                    exception,
                    request.Evidence.TickId,
                    request.Evidence.ProcessorId,
                    entity,
                    evidenceComponent,
                    evidenceField,
                    partialChangeCount: 1,
                    evidenceIdentity: request.Evidence.EvidenceIdentity),
                EntityLifecycleState.Destroyed);
        }

        created = CompleteBoundary(
            created,
            "Create",
            tickId: request.Evidence.TickId,
            processorId: request.Evidence.ProcessorId,
            entity: entity,
            componentType: evidenceComponent,
            field: evidenceField,
            partialChangeCount: created.Status == StorageOperationStatus.Indeterminate ||
                created.Error?.Code == EcsErrorCodes.PostWriteFailure ? 1 : 0,
            evidenceIdentity: request.Evidence.EvidenceIdentity);

        if (!created.IsSuccess)
        {
            _entities.TryRetire(entity);
            if (created.Status is StorageOperationStatus.Fatal or StorageOperationStatus.Indeterminate)
            {
                return new EntityCreateResult(
                    false,
                    entity,
                    CompleteBoundary(
                        created,
                        "Create",
                        tickId: request.Evidence.TickId,
                        processorId: request.Evidence.ProcessorId,
                        entity: entity,
                        componentType: evidenceComponent,
                        field: evidenceField,
                        partialChangeCount: created.Status == StorageOperationStatus.Indeterminate ||
                            created.Error?.Code == EcsErrorCodes.PostWriteFailure ? 1 : 0,
                        evidenceIdentity: request.Evidence.EvidenceIdentity),
                    EntityLifecycleState.Destroyed);
            }
            return new EntityCreateResult(false, default, created);
        }

        if (_state != EcsWorldState.Running)
        {
            RollbackProvisionalEntity(entity);
            _entities.TryRetire(entity);
            return new EntityCreateResult(
                false,
                entity,
                RejectForState(),
                EntityLifecycleState.Destroyed);
        }

        if (!_entities.TrySetState(entity, EntityLifecycleState.Alive))
        {
            return new EntityCreateResult(
                false,
                entity,
                CompleteBoundary(
                    StorageOperationResult.Fatal(EcsErrorCodes.InvalidState),
                    "Create",
                    tickId: request.Evidence.TickId,
                    processorId: request.Evidence.ProcessorId,
                    entity: entity,
                    componentType: evidenceComponent,
                    field: evidenceField,
                    partialChangeCount: 1,
                    evidenceIdentity: request.Evidence.EvidenceIdentity),
                EntityLifecycleState.Destroyed);
        }
        return new EntityCreateResult(
            true,
            entity,
            StorageOperationResult.Accepted(),
            EntityLifecycleState.Alive,
            definition.DefaultMode,
            new IssuedWorldEntityTarget(_context, entity));
        }
    }

    /// <summary>Applies a structural destroy at the command commit boundary.</summary>
    internal EntityDestroyResult DestroyEntityForCommit(WorldEntityTarget? target)
    {
        if (target is not IssuedWorldEntityTarget ||
            target.IsDefault ||
            !ReferenceEquals(target.Origin, _context))
            return new EntityDestroyResult(false, target?.Entity ?? default, StorageOperationResult.Rejected(EcsErrorCodes.CrossWorld));
        LocalEntityId entity = target.Entity;
        lock (_lifecycleSync)
        {
        StorageOperationResult state = EnsureWritableState();
        if (!state.IsSuccess) return new EntityDestroyResult(false, entity, state);
        StorageOperationResult owner = ValidateOwnerForWrite();
        if (!owner.IsSuccess) return new EntityDestroyResult(false, entity, owner);
        if (!_entities.TryResolve(entity, out EntityLifecycleState current, out _, out _))
            return new EntityDestroyResult(false, entity, StorageOperationResult.Rejected(EcsErrorCodes.StaleEntity));
        if (current is EntityLifecycleState.Tombstoned or EntityLifecycleState.Destroyed)
            return new EntityDestroyResult(false, entity, StorageOperationResult.Rejected(EcsErrorCodes.StaleEntity));

        StorageOperationResult destroyed;
        try
        {
            destroyed = _storage.Destroy(entity);
        }
        catch (Exception exception)
        {
            return new EntityDestroyResult(
                false,
                entity,
                CompleteBoundary(
                    StorageOperationResult.Fatal(EcsErrorCodes.PostWriteFailure),
                    "Destroy",
                    exception,
                    entity: entity,
                    partialChangeCount: 1));
        }
        destroyed = CompleteBoundary(
            destroyed,
            "Destroy",
            entity: entity,
            partialChangeCount: destroyed.Status == StorageOperationStatus.Indeterminate ||
                destroyed.Error?.Code == EcsErrorCodes.PostWriteFailure ? 1 : 0);
        if (!destroyed.IsSuccess)
        {
            if (destroyed.Status is StorageOperationStatus.Fatal or StorageOperationStatus.Indeterminate)
            {
                return new EntityDestroyResult(
                    false,
                    entity,
                    CompleteBoundary(
                        destroyed,
                        "Destroy",
                        entity: entity,
                        partialChangeCount: destroyed.Status == StorageOperationStatus.Indeterminate ||
                            destroyed.Error?.Code == EcsErrorCodes.PostWriteFailure ? 1 : 0));
            }
            return new EntityDestroyResult(false, entity, destroyed);
        }
        if (_state != EcsWorldState.Running)
        {
            _entities.TryRetire(entity);
            return new EntityDestroyResult(false, entity, RejectForState());
        }

        if (!_entities.TryRetire(entity))
        {
            return new EntityDestroyResult(
                false,
                entity,
                CompleteBoundary(
                    StorageOperationResult.Fatal(EcsErrorCodes.InvalidState),
                    "Destroy",
                    entity: entity,
                    partialChangeCount: 1));
        }
        return new EntityDestroyResult(true, entity, StorageOperationResult.Accepted());
        }
    }

    internal StorageOperationResult WriteExistingField(in EcsFieldWrite write, IEcsChangeSetAppend changeSet)
    {
#if NET10_0_OR_GREATER
        ArgumentNullException.ThrowIfNull(changeSet);
#else
        if (changeSet is null) throw new ArgumentNullException(nameof(changeSet));
#endif
        lock (_lifecycleSync)
        {
            StorageOperationResult state = EnsureWritableStateUnsafe();
            if (!state.IsSuccess) return state;
            StorageOperationResult owner = _ownerThread.ValidateCurrentThread();
            if (!owner.IsSuccess)
            {
                return CompleteBoundary(
                    StorageOperationResult.Fatal(owner.Error?.Code ?? EcsErrorCodes.OwnerThreadViolation),
                    "WriteExistingField",
                    tickId: write.Evidence.TickId,
                    processorId: write.Evidence.ProcessorId,
                    entity: write.Entity,
                    componentType: write.ComponentType,
                    field: write.Field,
                    evidenceIdentity: write.Evidence.EvidenceIdentity);
            }

            ReadOnlySpan<byte> after = write.CanonicalValue.Span;
            var before = new byte[after.Length];
            StorageOperationResult read;
            try
            {
                read = _storage.ReadField(
                    write.Entity,
                    write.ComponentType,
                    write.Field,
                    before,
                    out int readWritten);
                if (!read.IsSuccess) return CompleteBoundaryIfFatal(read, write, 0);
                if (readWritten != after.Length)
                    return StorageOperationResult.Rejected(EcsErrorCodes.InvalidArgument);
            }
            catch (Exception exception)
            {
                return CompleteBoundary(
                    StorageOperationResult.Fatal(EcsErrorCodes.PostWriteFailure),
                    "WriteExistingField",
                    exception,
                    write.Evidence.TickId,
                    write.Evidence.ProcessorId,
                    write.Entity,
                    write.ComponentType,
                    write.Field,
                    evidenceIdentity: write.Evidence.EvidenceIdentity);
            }

            StorageOperationResult written;
            try
            {
                written = _storage.WriteExistingField(
                    write.Entity,
                    write.ComponentType,
                    write.Field,
                    after);
            }
            catch (Exception exception)
            {
                return CompleteBoundary(
                    StorageOperationResult.Fatal(EcsErrorCodes.PostWriteFailure),
                    "WriteExistingField",
                    exception,
                    write.Evidence.TickId,
                    write.Evidence.ProcessorId,
                    write.Entity,
                    write.ComponentType,
                    write.Field,
                    partialChangeCount: 1,
                    evidenceIdentity: write.Evidence.EvidenceIdentity);
            }

            if (!written.IsSuccess)
                return CompleteBoundaryIfFatal(written, write, written.Status is StorageOperationStatus.Fatal or StorageOperationStatus.Indeterminate ? 1 : 0);

            try
            {
                StorageOperationResult appended = changeSet.TryAppend(new ChangeEntry(
                    write.Entity,
                    write.ComponentType,
                    write.Field,
                    before,
                    after.ToArray()));
                if (appended.IsSuccess) return written;
                return CompleteBoundary(
                    StorageOperationResult.Fatal(EcsErrorCodes.PostWriteFailure),
                    "WriteExistingField",
                    tickId: write.Evidence.TickId,
                    processorId: write.Evidence.ProcessorId,
                    entity: write.Entity,
                    componentType: write.ComponentType,
                    field: write.Field,
                    partialChangeCount: 1,
                    evidenceIdentity: write.Evidence.EvidenceIdentity);
            }
            catch (Exception exception)
            {
                return CompleteBoundary(
                    StorageOperationResult.Fatal(EcsErrorCodes.PostWriteFailure),
                    "WriteExistingField",
                    exception,
                    write.Evidence.TickId,
                    write.Evidence.ProcessorId,
                    write.Entity,
                    write.ComponentType,
                    write.Field,
                    partialChangeCount: 1,
                    evidenceIdentity: write.Evidence.EvidenceIdentity);
            }
        }
    }

    public bool TryResolve(EcsWorld contextWorld, LocalEntityId entity, out EntityLifecycleState state)
    {
        lock (_lifecycleSync)
        {
            state = EntityLifecycleState.Destroyed;
            StorageOperationResult access = ValidateLiveReadAccessUnsafe(contextWorld);
            if (!access.IsSuccess) return false;
            return _entities.TryResolve(entity, out state, out _, out _);
        }
    }

    public StorageOperationResult ValidateEntityContext(EcsWorld contextWorld, LocalEntityId entity)
    {
        lock (_lifecycleSync)
        {
            StorageOperationResult access = ValidateLiveReadAccessUnsafe(contextWorld);
            if (!access.IsSuccess) return access;
            return _entities.TryResolve(entity, out _, out _, out _)
                ? StorageOperationResult.Accepted()
                : StorageOperationResult.Rejected(EcsErrorCodes.StaleEntity);
        }
    }

    internal bool TryGetRegisteredComponent(string nameOrId, out ComponentTypeDefinition definition)
    {
        definition = null!;
        if (string.IsNullOrWhiteSpace(nameOrId)) return false;
        lock (_lifecycleSync)
        {
            if (ulong.TryParse(nameOrId, NumberStyles.None, CultureInfo.InvariantCulture, out ulong id) &&
                id != 0UL &&
                _componentTypes.TryGet(new ComponentTypeId(id), out definition))
            {
                return true;
            }

            return _componentsByName.TryGetValue(nameOrId, out definition!);
        }
    }

    internal bool TryGetRegisteredField(
        string componentType,
        string fieldName,
        out ComponentTypeDefinition component,
        out ComponentFieldDefinition field)
    {
        field = default;
        if (!TryGetRegisteredComponent(componentType, out component) || string.IsNullOrWhiteSpace(fieldName))
            return false;
        if (ulong.TryParse(fieldName, NumberStyles.None, CultureInfo.InvariantCulture, out ulong fieldId) &&
            fieldId != 0UL)
        {
            return component.TryGetField(new ComponentFieldId(fieldId), out field);
        }

        if (component.Fields.Length == 1 &&
            string.Equals(fieldName, component.Name, StringComparison.Ordinal))
        {
            field = component.Fields.Span[0];
            return true;
        }

        return false;
    }

    internal bool TryGetRegisteredEntityType(
        string name,
        out EntityTypeHandle handle,
        out EntityTypeDefinition definition)
    {
        handle = default;
        definition = null!;
        if (string.IsNullOrWhiteSpace(name)) return false;
        lock (_lifecycleSync)
        {
            if (!_entityTypesByName.TryGetValue(name, out (EntityTypeHandle Handle, EntityTypeDefinition Definition) found))
                return false;
            handle = found.Handle;
            definition = found.Definition;
            return true;
        }
    }

    internal bool EntityIsAlive(LocalEntityId entity)
    {
        lock (_lifecycleSync)
        {
            if (_state is EcsWorldState.Faulted or EcsWorldState.Disposed) return false;
            if (!_entities.TryResolve(entity, out EntityLifecycleState state, out _, out _)) return false;
            return state is EntityLifecycleState.Alive or EntityLifecycleState.Disabled;
        }
    }

    internal bool TryGetCommitTarget(LocalEntityId entity, out WorldEntityTarget target)
    {
        target = null!;
        lock (_lifecycleSync)
        {
            if (!_entities.TryResolve(entity, out EntityLifecycleState state, out _, out _)) return false;
            if (state is EntityLifecycleState.Tombstoned or EntityLifecycleState.Destroyed) return false;
            target = new IssuedWorldEntityTarget(_context, entity);
            return true;
        }
    }

    internal StorageOperationResult FaultFromParticipant(string generatedErrorId)
    {
        string code = generatedErrorId;
        if (string.IsNullOrWhiteSpace(code) || !EcsBoundaryErrors.IsGeneratedStableError(code))
            code = EcsErrorCodes.InvalidState;
        return Fault(new ErrorIdentity(code));
    }

    public StorageOperationResult EnumerateActiveEntities(
        EcsWorld contextWorld,
        LocalEntityId[] destination,
        out int written)
    {
        lock (_lifecycleSync)
        {
            written = 0;
            StorageOperationResult access = ValidateLiveReadAccessUnsafe(contextWorld);
            if (!access.IsSuccess) return access;
            if (destination is null)
                return StorageOperationResult.Rejected(EcsErrorCodes.InvalidArgument);

            var values = new List<LocalEntityId>();
            foreach ((LocalEntityId id, _, _, _) in _entities.EnumerateActiveOrdered()) values.Add(id);
            if (values.Count > destination.Length)
                return StorageOperationResult.Rejected(EcsErrorCodes.BudgetExceeded);
            for (int index = 0; index < values.Count; index++) destination[index] = values[index];
            written = values.Count;
            return StorageOperationResult.Accepted();
        }
    }

    internal IReadOnlyList<LocalEntityId> EnumerateActiveEntities(EcsWorldContext context)
    {
        if (!ReferenceEquals(context, _context)) return Array.Empty<LocalEntityId>();
        lock (_lifecycleSync)
        {
            if (_state is EcsWorldState.Disposed or EcsWorldState.Faulted)
                return Array.Empty<LocalEntityId>();
            var values = new List<LocalEntityId>();
            foreach ((LocalEntityId id, _, _, _) in _entities.EnumerateActiveOrdered()) values.Add(id);
            return values.ToArray();
        }
    }

    internal StorageOperationResult CaptureReadSnapshot(
        SnapshotId snapshotId,
        Revision revision,
        out StorageReadSnapshotHandle handle)
    {
        lock (_lifecycleSync)
        {
            handle = default;
            StorageOperationResult state = EnsureWritableState();
            if (!state.IsSuccess) return state;
            StorageOperationResult owner = ValidateOwnerForWrite();
            if (!owner.IsSuccess) return owner;
            var context = new StorageSnapshotContext(WorldId, snapshotId, revision, _context);
            if (!context.IsValid)
                return StorageOperationResult.Rejected(EcsErrorCodes.InvalidArgument);

            StorageOperationResult result;
            try
            {
                result = _storage.CaptureReadSnapshot(in context, out handle);
            }
            catch (Exception exception)
            {
                result = CompleteBoundary(
                    StorageOperationResult.Fatal(EcsErrorCodes.PostWriteFailure),
                    "CaptureReadSnapshot",
                    exception);
            }
            result = CompleteBoundary(result, "CaptureReadSnapshot");
            if (!result.IsSuccess)
            {
                ReleaseUnpublishedSnapshot(handle);
                handle = default;
                return result;
            }
            if (handle.IsDefault)
            {
                result = CompleteBoundary(
                    StorageOperationResult.Fatal(EcsErrorCodes.InvalidState),
                    "CaptureReadSnapshot");
                handle = default;
                return result;
            }
            if (handle.Context != context)
            {
                result = CompleteBoundary(
                    StorageOperationResult.Fatal(EcsErrorCodes.InvalidState),
                    "CaptureReadSnapshot");
                ReleaseUnpublishedSnapshot(handle);
                handle = default;
                return result;
            }
            handle = new StorageReadSnapshotHandle(handle.Value, handle.Context, _context);
            if (_state is EcsWorldState.Faulted or EcsWorldState.Disposed)
            {
                ReleaseUnpublishedSnapshot(handle);
                handle = default;
                return RejectForState();
            }
            if (_releasedSnapshotLeases.Contains(handle) || !_activeSnapshotLeases.Add(handle))
            {
                ReleaseUnpublishedSnapshot(handle);
                handle = default;
                return CompleteBoundary(
                    StorageOperationResult.Fatal(EcsErrorCodes.InvalidState),
                    "CaptureReadSnapshot");
            }
            return result;
        }
    }

    internal StorageOperationResult EnumerateSnapshotEntities(
        StorageReadSnapshotHandle handle,
        Span<LocalEntityId> destination,
        out int written)
    {
        lock (_lifecycleSync)
        {
            written = 0;
            StorageOperationResult state = ValidateSnapshotReadUnsafe(handle);
            if (!state.IsSuccess) return state;
            LocalEntityId[] original = destination.ToArray();
            StorageOperationResult result;
            try
            {
                result = _storage.EnumerateSnapshotOrdered(handle, destination, out written);
            }
            catch (Exception exception)
            {
                result = CompleteBoundary(
                    StorageOperationResult.Fatal(EcsErrorCodes.PostWriteFailure),
                    "EnumerateSnapshotEntities",
                    exception,
                    partialChangeCount: Math.Max(written, 0));
            }
            result = CompleteBoundary(result, "EnumerateSnapshotEntities");
            if (!result.IsSuccess || (_state is EcsWorldState.Faulted or EcsWorldState.Disposed))
            {
                original.AsSpan().CopyTo(destination);
                written = 0;
                if (result.IsSuccess) result = RejectForState();
            }
            return result;
        }
    }

    internal StorageOperationResult ReadSnapshotField(
        StorageReadSnapshotHandle handle,
        LocalEntityId entity,
        ComponentTypeId componentType,
        ComponentFieldId field,
        Span<byte> destination,
        out int written)
    {
        lock (_lifecycleSync)
        {
            written = 0;
            StorageOperationResult state = ValidateSnapshotReadUnsafe(handle);
            if (!state.IsSuccess) return state;
            byte[] original = destination.ToArray();
            StorageOperationResult result;
            try
            {
                result = _storage.ReadSnapshotField(
                    handle,
                    entity,
                    componentType,
                    field,
                    destination,
                    out written);
            }
            catch (Exception exception)
            {
                result = CompleteBoundary(
                    StorageOperationResult.Fatal(EcsErrorCodes.PostWriteFailure),
                    "ReadSnapshotField",
                    exception,
                    entity: entity,
                    componentType: componentType,
                    field: field,
                    partialChangeCount: Math.Max(written, 0));
            }
            result = CompleteBoundary(result, "ReadSnapshotField");
            if (!result.IsSuccess || (_state is EcsWorldState.Faulted or EcsWorldState.Disposed))
            {
                original.AsSpan().CopyTo(destination);
                written = 0;
                if (result.IsSuccess) result = RejectForState();
            }
            return result;
        }
    }

    internal StorageOperationResult ReleaseReadSnapshot(StorageReadSnapshotHandle handle)
    {
        lock (_lifecycleSync)
        {
            StorageOperationResult origin = ValidateSnapshotOriginUnsafe(handle);
            if (!origin.IsSuccess) return origin;
            StorageOperationResult state = _state switch
            {
                EcsWorldState.Disposed => StorageOperationResult.Rejected(EcsErrorCodes.WorldDisposed),
                EcsWorldState.Running or EcsWorldState.Draining or EcsWorldState.Faulted =>
                    StorageOperationResult.Accepted(),
                _ => StorageOperationResult.Rejected(EcsErrorCodes.WorldNotReady)
            };
            if (!state.IsSuccess) return state;
            if (_releasedSnapshotLeases.Contains(handle))
                return StorageOperationResult.Rejected(EcsErrorCodes.SnapshotDoubleRelease);
            if (!_activeSnapshotLeases.Remove(handle))
                return StorageOperationResult.Rejected(EcsErrorCodes.SnapshotReleased);
            _releasedSnapshotLeases.Add(handle);
            StorageOperationResult result;
            try
            {
                result = _storage.ReleaseReadSnapshot(handle);
            }
            catch (Exception exception)
            {
                result = CompleteBoundary(
                    StorageOperationResult.Fatal(EcsErrorCodes.PostWriteFailure),
                    "ReleaseReadSnapshot",
                    exception,
                    partialChangeCount: 1);
            }
            result = CompleteBoundary(result, "ReleaseReadSnapshot");
            return result;
        }
    }

    internal void ForceCleanup()
    {
        lock (_lifecycleSync)
        {
            if (_state == EcsWorldState.Disposed) return;
            if (_state == EcsWorldState.Running) _state = EcsWorldState.Draining;
            try
            {
                _storage.Dispose();
            }
            catch (Exception exception)
            {
                if (_firstFault is null)
                {
                    _firstFault = new EcsFaultEvidence(
                        new ErrorIdentity(EcsErrorCodes.InvalidState),
                        new FailureContext(
                            WorldId,
                            default,
                            null,
                            default,
                            default,
                            default,
                            "ForceCleanup",
                            Detail: exception.Message),
                        0,
                        exception.GetType().FullName);
                }
            }
            _activeSnapshotLeases.Clear();
            _releasedSnapshotLeases.Clear();
            _state = EcsWorldState.Disposed;
        }
    }

    private StorageOperationResult ValidateOwnerForWrite()
    {
        StorageOperationResult result = _ownerThread.ValidateCurrentThread();
        return result.IsSuccess
            ? result
            : CompleteBoundary(StorageOperationResult.Fatal(result.Error?.Code ?? EcsErrorCodes.WrongContext), "OwnerThread");
    }

    private StorageOperationResult EnsureWritableState()
    {
        lock (_lifecycleSync) return EnsureWritableStateUnsafe();
    }

    private void ReleaseUnpublishedSnapshot(StorageReadSnapshotHandle handle)
    {
        if (handle.IsDefault) return;
        try
        {
            StorageOperationResult result = _storage.ReleaseReadSnapshot(handle);
            CompleteBoundary(result, "ReleaseUnpublishedSnapshot");
        }
        catch (Exception exception)
        {
            CompleteBoundary(
                StorageOperationResult.Fatal(EcsErrorCodes.PostWriteFailure),
                "ReleaseUnpublishedSnapshot",
                exception);
        }
    }

    private void RollbackProvisionalEntity(LocalEntityId entity)
    {
        try
        {
            _storage.Destroy(entity);
        }
        catch (Exception exception)
        {
            CompleteBoundary(
                StorageOperationResult.Fatal(EcsErrorCodes.PostWriteFailure),
                "RollbackCreate",
                exception,
                entity: entity,
                partialChangeCount: 1);
        }
    }

    private StorageOperationResult EnsureWritableStateUnsafe() => _state switch
    {
        EcsWorldState.Running => StorageOperationResult.Accepted(),
        EcsWorldState.Draining => StorageOperationResult.Rejected(EcsErrorCodes.WorldDraining),
        EcsWorldState.Disposed => StorageOperationResult.Rejected(EcsErrorCodes.WorldDisposed),
        EcsWorldState.Faulted => StorageOperationResult.Rejected(EcsErrorCodes.WorldFaulted),
        _ => StorageOperationResult.Rejected(EcsErrorCodes.WorldNotReady)
    };

    private StorageOperationResult ValidateSnapshotReadUnsafe(StorageReadSnapshotHandle handle)
    {
        StorageOperationResult origin = ValidateSnapshotOriginUnsafe(handle);
        if (!origin.IsSuccess) return origin;
        StorageOperationResult state = _state switch
        {
            EcsWorldState.Disposed => StorageOperationResult.Rejected(EcsErrorCodes.WorldDisposed),
            EcsWorldState.Faulted => StorageOperationResult.Rejected(EcsErrorCodes.WorldFaulted),
            EcsWorldState.Running or EcsWorldState.Draining => StorageOperationResult.Accepted(),
            _ => StorageOperationResult.Rejected(EcsErrorCodes.WorldNotReady)
        };
        if (!state.IsSuccess) return state;
        return _activeSnapshotLeases.Contains(handle)
            ? StorageOperationResult.Accepted()
            : StorageOperationResult.Rejected(EcsErrorCodes.SnapshotReleased);
    }

    private StorageOperationResult ValidateSnapshotOriginUnsafe(StorageReadSnapshotHandle handle) =>
        handle.IsDefault ||
        handle.Context.WorldId != WorldId ||
        !ReferenceEquals(handle.Context.Origin, _context) ||
        !ReferenceEquals(handle.Origin, _context)
            ? StorageOperationResult.Rejected(EcsErrorCodes.CrossWorld)
            : StorageOperationResult.Accepted();

    private StorageOperationResult ValidateLiveReadAccessUnsafe(EcsWorld contextWorld)
    {
        if (!ReferenceEquals(contextWorld, this))
            return StorageOperationResult.Rejected(EcsErrorCodes.CrossWorld);
        StorageOperationResult state = _state switch
        {
            EcsWorldState.Disposed => StorageOperationResult.Rejected(EcsErrorCodes.WorldDisposed),
            EcsWorldState.Faulted => StorageOperationResult.Rejected(EcsErrorCodes.WorldFaulted),
            EcsWorldState.Running or EcsWorldState.Draining => StorageOperationResult.Accepted(),
            _ => StorageOperationResult.Rejected(EcsErrorCodes.WorldNotReady)
        };
        if (!state.IsSuccess) return state;
        return _ownerThread.ValidateCurrentThread();
    }

    private StorageOperationResult RejectForState()
    {
        lock (_lifecycleSync) return RejectForStateUnsafe();
    }

    private StorageOperationResult RejectForStateUnsafe() => _state switch
    {
        EcsWorldState.Disposed => StorageOperationResult.Rejected(EcsErrorCodes.WorldDisposed),
        EcsWorldState.Draining => StorageOperationResult.Rejected(EcsErrorCodes.WorldDraining),
        EcsWorldState.Faulted => StorageOperationResult.Rejected(EcsErrorCodes.WorldFaulted),
        _ => StorageOperationResult.Rejected(EcsErrorCodes.InvalidState)
    };

    private StorageOperationResult ValidateEntityType(EntityTypeDefinition type, in ComponentInitBatch initialValues)
    {
        ReadOnlySpan<ComponentTypeHandle> components = type.ComponentTypes.Span;
        ReadOnlySpan<ComponentTypeId> suppliedComponents = initialValues.Components.Span;
        ReadOnlySpan<ComponentInitValue> supplied = initialValues.Values.Span;
        for (int i = 0; i < components.Length; i++)
        {
            if (!_componentTypes.TryGet(components[i], out _))
                return StorageOperationResult.Rejected(EcsErrorCodes.UnknownComponent);
        }
        for (int i = 0; i < suppliedComponents.Length; i++)
        {
            bool found = false;
            for (int componentIndex = 0; componentIndex < components.Length; componentIndex++)
            {
                if (_componentTypes.TryGet(components[componentIndex], out ComponentTypeDefinition? component) &&
                    component.Id == suppliedComponents[i])
                {
                    found = true;
                    break;
                }
            }
            if (!found)
                return StorageOperationResult.Rejected(EcsErrorCodes.UnknownComponent);
        }
        for (int i = 0; i < supplied.Length; i++)
        {
            bool found = false;
            for (int componentIndex = 0; componentIndex < components.Length; componentIndex++)
            {
                if (_componentTypes.TryGet(components[componentIndex], out ComponentTypeDefinition? component) &&
                    component.Id == supplied[i].ComponentType)
                {
                    found = true;
                    break;
                }
            }
            if (!found)
                return StorageOperationResult.Rejected(EcsErrorCodes.UnknownComponent);
        }
        return StorageOperationResult.Accepted();
    }

    private ComponentInitBatch ExpandInitialValues(EntityTypeDefinition type, in ComponentInitBatch supplied)
    {
        var componentTypes = new List<ComponentTypeId>();
        var values = new List<ComponentInitValue>();
        ReadOnlySpan<ComponentInitValue> suppliedValues = supplied.Values.Span;
        for (int i = 0; i < suppliedValues.Length; i++) values.Add(suppliedValues[i]);
        ReadOnlySpan<ComponentTypeHandle> components = type.ComponentTypes.Span;
        for (int i = 0; i < components.Length; i++)
        {
            if (!_componentTypes.TryGet(components[i], out ComponentTypeDefinition? definition)) continue;
            componentTypes.Add(definition.Id);
            ReadOnlySpan<ComponentFieldDefinition> fields = definition.Fields.Span;
            for (int j = 0; j < fields.Length; j++)
            {
                bool provided = false;
                for (int k = 0; k < suppliedValues.Length; k++)
                {
                    if (suppliedValues[k].ComponentType == definition.Id && suppliedValues[k].Field == fields[j].Id)
                    {
                        provided = true;
                        break;
                    }
                }
                if (!provided)
                {
                    values.Add(new ComponentInitValue(
                        definition.Id, fields[j].Id, new byte[fields[j].SizeBytes]));
                }
            }
        }
        return new ComponentInitBatch(componentTypes.ToArray(), values.ToArray());
    }

    private StorageOperationResult CompleteBoundaryIfFatal(
        StorageOperationResult result,
        in EcsFieldWrite write,
        int partialChangeCount)
    {
        if (result.Status is not StorageOperationStatus.Fatal and not StorageOperationStatus.Indeterminate)
            return result;
        return CompleteBoundary(
            result,
            "WriteExistingField",
            tickId: write.Evidence.TickId,
            processorId: write.Evidence.ProcessorId,
            entity: write.Entity,
            componentType: write.ComponentType,
            field: write.Field,
            partialChangeCount: partialChangeCount,
            evidenceIdentity: write.Evidence.EvidenceIdentity);
    }

    private StorageOperationResult CompleteBoundary(
        StorageOperationResult result,
        string operation,
        Exception? exception = null,
        TickId tickId = default,
        ProcessorId? processorId = null,
        LocalEntityId entity = default,
        ComponentTypeId componentType = default,
        ComponentFieldId field = default,
        int partialChangeCount = 0,
        string? evidenceIdentity = null)
    {
        if (result.Status is not StorageOperationStatus.Fatal and not StorageOperationStatus.Indeterminate)
            return result;
        lock (_lifecycleSync)
        {
            StorageOperationResult captured = _failStop.CaptureAdapterFailure(
                result,
                ref _state,
                new FailureContext(
                    WorldId,
                    tickId,
                    processorId,
                    entity,
                    componentType,
                    field,
                    operation,
                    evidenceIdentity,
                    exception?.Message),
                partialChangeCount,
                exception);
            _firstFault = _failStop.First;
            return captured;
        }
    }
}
