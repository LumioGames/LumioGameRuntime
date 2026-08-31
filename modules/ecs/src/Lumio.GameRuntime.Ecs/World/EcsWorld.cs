using System;
using System.Collections.Generic;

namespace Lumio.GameRuntime.Ecs;

public readonly record struct EntityCreateRequest(
    EntityTypeDefinition Type,
    ComponentInitBatch InitialValues = default,
    EntityMode Mode = EntityMode.CrossServer);

public readonly record struct EntityCreateResult(
    bool Created,
    LocalEntityId Entity,
    StorageOperationResult Result,
    EntityLifecycleState State = EntityLifecycleState.Reserved)
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
public sealed class EcsWorld : IDisposable
{
    private readonly OwnerThreadGuard _ownerThread = new();
    private readonly EntitySlotTable _entities;
    private readonly IWorldStorageAdapter _storage;
    private readonly ComponentTypeRegistry _componentTypes = new();
    private readonly EcsBudget _budget;
    private EcsWorldState _state;
    private EcsFaultEvidence? _firstFault;

    public EcsWorld(in EcsWorldCreateRequest request)
        : this(request, new ReferenceWorldStorageAdapter(request.Budget.MaxEntities))
    {
    }

    internal EcsWorld(in EcsWorldCreateRequest request, IWorldStorageAdapter storage)
    {
        if (!request.IsValid)
            throw new ArgumentException("World id and ECS budget must be valid.", nameof(request));
#if NET10_0_OR_GREATER
        ArgumentNullException.ThrowIfNull(storage);
#else
        if (storage is null) throw new ArgumentNullException(nameof(storage));
#endif

        WorldId = request.WorldId;
        _budget = request.Budget;
        _budget.Validate();
        _entities = new EntitySlotTable(request.Budget.MaxEntities);
        _storage = storage;
        _state = EcsWorldState.Created;
    }

    public WorldId WorldId { get; }

    public EcsWorldState State => _state;

    public EcsBudget Budget => _budget;

    public int ActiveEntityCount => _entities.ActiveCount;

    public int OwnerThreadId => _ownerThread.OwnerThreadId;

    public EcsFaultEvidence? FirstFault => _firstFault;

    public StorageOperationResult BeginRegistration()
    {
        StorageOperationResult owner = ValidateOwnerForLifecycle();
        if (!owner.IsSuccess) return owner;
        if (_state != EcsWorldState.Created) return RejectForState();
        _state = EcsWorldState.Registering;
        return StorageOperationResult.Accepted();
    }

    public StorageOperationResult RegisterTypes(GeneratedComponentSchemaView schema)
    {
        StorageOperationResult owner = ValidateOwnerForLifecycle();
        if (!owner.IsSuccess) return owner;
        if (_state != EcsWorldState.Registering) return RejectForState();
        if (schema is null)
            return StorageOperationResult.Rejected(EcsErrorCodes.InvalidArgument);
        if (!_componentTypes.CanRegister(schema))
            return StorageOperationResult.Rejected(EcsErrorCodes.DuplicateRegistration);

        StorageOperationResult storageResult = _storage.Register(in schema);
        if (!storageResult.IsSuccess) return storageResult;

        StorageOperationResult registryResult = _componentTypes.Register(schema);
        if (!registryResult.IsSuccess) return FailStop(registryResult, "RegisterTypes");
        return StorageOperationResult.Accepted();
    }

    public StorageOperationResult RegisterComponentType(ComponentTypeDefinition definition)
    {
        StorageOperationResult owner = ValidateOwnerForLifecycle();
        if (!owner.IsSuccess) return owner;
        if (_state != EcsWorldState.Registering) return RejectForState();
        if (definition is null)
            return StorageOperationResult.Rejected(EcsErrorCodes.InvalidArgument);
        if (!_componentTypes.CanRegister(definition))
            return StorageOperationResult.Rejected(EcsErrorCodes.DuplicateRegistration);

        GeneratedComponentSchemaView schema = new(new SchemaEpoch(1), new[] { definition });
        StorageOperationResult storageResult = _storage.Register(in schema);
        if (!storageResult.IsSuccess) return storageResult;

        StorageOperationResult registryResult = _componentTypes.Register(definition);
        if (!registryResult.IsSuccess) return FailStop(registryResult, "RegisterComponentType");
        return StorageOperationResult.Accepted();
    }

    public StorageOperationResult MarkReady()
    {
        StorageOperationResult owner = ValidateOwnerForLifecycle();
        if (!owner.IsSuccess) return owner;
        if (_state != EcsWorldState.Registering) return RejectForState();
        StorageOperationResult integrity = _storage.ValidateIntegrity();
        if (!integrity.IsSuccess) return integrity;
        _state = EcsWorldState.Ready;
        return StorageOperationResult.Accepted();
    }

    public StorageOperationResult Start()
    {
        StorageOperationResult owner = ValidateOwnerForLifecycle();
        if (!owner.IsSuccess) return owner;
        if (_state != EcsWorldState.Ready) return RejectForState();
        _state = EcsWorldState.Running;
        return StorageOperationResult.Accepted();
    }

    public StorageOperationResult BeginDrain()
    {
        StorageOperationResult owner = ValidateOwnerForLifecycle();
        if (!owner.IsSuccess) return owner;
        if (_state != EcsWorldState.Running) return RejectForState();
        _state = EcsWorldState.Draining;
        return StorageOperationResult.Accepted();
    }

    public StorageOperationResult Fault(ErrorIdentity error)
    {
        if (string.IsNullOrWhiteSpace(error.Code))
            return StorageOperationResult.Rejected(EcsErrorCodes.InvalidArgument);
        if (_state == EcsWorldState.Disposed)
            return StorageOperationResult.Rejected(EcsErrorCodes.WorldDisposed);
        if (_state != EcsWorldState.Faulted)
        {
            _state = EcsWorldState.Faulted;
            _firstFault = new EcsFaultEvidence(
                error,
                new FailureContext(WorldId, default, default, default, default, "Fault"),
                0);
        }
        return StorageOperationResult.Accepted();
    }

    public StorageOperationResult DisposeWorld()
    {
        if (_state == EcsWorldState.Disposed) return StorageOperationResult.Accepted();
        try
        {
            _storage.Dispose();
        }
        catch (Exception exception)
        {
            return FailStop(
                StorageOperationResult.Fatal(EcsErrorCodes.InvalidState),
                "Dispose",
                exception);
        }

        _state = EcsWorldState.Disposed;
        return StorageOperationResult.Accepted();
    }

    /// <summary>Applies a structural create at the command commit boundary.</summary>
    internal EntityCreateResult CreateEntityForCommit(in EntityCreateRequest request)
    {
        StorageOperationResult owner = ValidateOwnerForWrite();
        if (!owner.IsSuccess) return new EntityCreateResult(false, default, owner);
        StorageOperationResult state = EnsureWritableState();
        if (!state.IsSuccess) return new EntityCreateResult(false, default, state);
        if (request.Type is null)
            return EntityCreateResult.Rejected(new ErrorIdentity(EcsErrorCodes.InvalidArgument));
        if (request.Mode is not EntityMode.CrossServer and not EntityMode.Local)
            return EntityCreateResult.Rejected(new ErrorIdentity(EcsErrorCodes.InvalidArgument));

        StorageOperationResult typeResult = ValidateEntityType(request.Type, request.InitialValues);
        if (!typeResult.IsSuccess)
            return EntityCreateResult.Rejected(typeResult.Error!.Value);

        if (!_entities.TryAllocate(request.Type, request.Mode, out LocalEntityId entity, out StorageOperationResult allocation))
            return new EntityCreateResult(false, default, allocation);

        ComponentInitBatch initialValues = ExpandInitialValues(request.Type, request.InitialValues);
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
                FailStop(StorageOperationResult.Fatal(EcsErrorCodes.PostWriteFailure), "Create", exception),
                EntityLifecycleState.Destroyed);
        }

        if (!created.IsSuccess)
        {
            _entities.TryRetire(entity);
            if (created.Status is StorageOperationStatus.Fatal or StorageOperationStatus.Indeterminate)
            {
                return new EntityCreateResult(
                    false,
                    entity,
                    FailStop(created, "Create"),
                    EntityLifecycleState.Destroyed);
            }
            return new EntityCreateResult(false, default, created);
        }

        if (!_entities.TrySetState(entity, EntityLifecycleState.Alive))
        {
            return new EntityCreateResult(
                false,
                entity,
                FailStop(StorageOperationResult.Fatal(EcsErrorCodes.InvalidState), "Create"),
                EntityLifecycleState.Destroyed);
        }
        return new EntityCreateResult(true, entity, StorageOperationResult.Accepted(), EntityLifecycleState.Alive);
    }

    /// <summary>Applies a structural destroy at the command commit boundary.</summary>
    internal EntityDestroyResult DestroyEntityForCommit(LocalEntityId entity)
    {
        StorageOperationResult owner = ValidateOwnerForWrite();
        if (!owner.IsSuccess) return new EntityDestroyResult(false, entity, owner);
        StorageOperationResult state = EnsureWritableState();
        if (!state.IsSuccess) return new EntityDestroyResult(false, entity, state);
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
                FailStop(StorageOperationResult.Fatal(EcsErrorCodes.PostWriteFailure), "Destroy", exception));
        }
        if (!destroyed.IsSuccess)
        {
            if (destroyed.Status is StorageOperationStatus.Fatal or StorageOperationStatus.Indeterminate)
            {
                return new EntityDestroyResult(false, entity, FailStop(destroyed, "Destroy"));
            }
            return new EntityDestroyResult(false, entity, destroyed);
        }

        if (!_entities.TryRetire(entity))
        {
            return new EntityDestroyResult(
                false,
                entity,
                FailStop(StorageOperationResult.Fatal(EcsErrorCodes.InvalidState), "Destroy"));
        }
        return new EntityDestroyResult(true, entity, StorageOperationResult.Accepted());
    }

    public bool TryResolve(LocalEntityId entity, out EntityLifecycleState state)
    {
        state = EntityLifecycleState.Destroyed;
        if (_state is EcsWorldState.Disposed or EcsWorldState.Faulted) return false;
        return _entities.TryResolve(entity, out state, out _, out _);
    }

    public bool TryResolve(WorldId contextWorldId, LocalEntityId entity, out EntityLifecycleState state)
    {
        state = EntityLifecycleState.Destroyed;
        if (contextWorldId != WorldId) return false;
        return TryResolve(entity, out state);
    }

    public StorageOperationResult ValidateEntityContext(WorldId contextWorldId, LocalEntityId entity)
    {
        if (contextWorldId != WorldId)
            return StorageOperationResult.Rejected(EcsErrorCodes.CrossWorld);
        if (_state == EcsWorldState.Disposed)
            return StorageOperationResult.Rejected(EcsErrorCodes.WorldDisposed);
        if (_state == EcsWorldState.Faulted)
            return StorageOperationResult.Rejected(EcsErrorCodes.WorldFaulted);
        return TryResolve(entity, out _)
            ? StorageOperationResult.Accepted()
            : StorageOperationResult.Rejected(EcsErrorCodes.StaleEntity);
    }

    public IReadOnlyList<LocalEntityId> EnumerateActiveEntities()
    {
        var values = new List<LocalEntityId>();
        foreach ((LocalEntityId id, _, _, _) in _entities.EnumerateActiveOrdered()) values.Add(id);
        return values.ToArray();
    }

    public void Dispose() => DisposeWorld();

    private StorageOperationResult ValidateOwnerForLifecycle() => _ownerThread.BindOrValidate();

    private StorageOperationResult ValidateOwnerForWrite()
    {
        StorageOperationResult result = _ownerThread.BindOrValidate();
        return result.IsSuccess ? result : FailStop(result, "OwnerThread");
    }

    private StorageOperationResult EnsureWritableState() => _state switch
    {
        EcsWorldState.Running => StorageOperationResult.Accepted(),
        EcsWorldState.Draining => StorageOperationResult.Rejected(EcsErrorCodes.WorldDraining),
        EcsWorldState.Disposed => StorageOperationResult.Rejected(EcsErrorCodes.WorldDisposed),
        EcsWorldState.Faulted => StorageOperationResult.Rejected(EcsErrorCodes.WorldFaulted),
        _ => StorageOperationResult.Rejected(EcsErrorCodes.WorldNotReady)
    };

    private StorageOperationResult RejectForState() => _state switch
    {
        EcsWorldState.Disposed => StorageOperationResult.Rejected(EcsErrorCodes.WorldDisposed),
        EcsWorldState.Draining => StorageOperationResult.Rejected(EcsErrorCodes.WorldDraining),
        EcsWorldState.Faulted => StorageOperationResult.Rejected(EcsErrorCodes.WorldFaulted),
        _ => StorageOperationResult.Rejected(EcsErrorCodes.InvalidState)
    };

    private StorageOperationResult ValidateEntityType(EntityTypeDefinition type, in ComponentInitBatch initialValues)
    {
        ReadOnlySpan<ComponentTypeId> components = type.ComponentTypes.Span;
        ReadOnlySpan<ComponentInitValue> supplied = initialValues.Values.Span;
        for (int i = 0; i < components.Length; i++)
        {
            if (!_componentTypes.TryGet(components[i], out _))
                return StorageOperationResult.Rejected(EcsErrorCodes.UnknownComponent);
        }
        for (int i = 0; i < supplied.Length; i++)
        {
            if (!type.HasComponent(supplied[i].ComponentType))
                return StorageOperationResult.Rejected(EcsErrorCodes.UnknownComponent);
        }
        return StorageOperationResult.Accepted();
    }

    private ComponentInitBatch ExpandInitialValues(EntityTypeDefinition type, in ComponentInitBatch supplied)
    {
        var values = new List<ComponentInitValue>();
        ReadOnlySpan<ComponentInitValue> suppliedValues = supplied.Values.Span;
        for (int i = 0; i < suppliedValues.Length; i++) values.Add(suppliedValues[i]);
        ReadOnlySpan<ComponentTypeId> components = type.ComponentTypes.Span;
        for (int i = 0; i < components.Length; i++)
        {
            if (!_componentTypes.TryGet(components[i], out ComponentTypeDefinition? definition)) continue;
            ReadOnlySpan<ComponentFieldDefinition> fields = definition.Fields.Span;
            for (int j = 0; j < fields.Length; j++)
            {
                bool provided = false;
                for (int k = 0; k < suppliedValues.Length; k++)
                {
                    if (suppliedValues[k].ComponentType == components[i] && suppliedValues[k].Field == fields[j].Id)
                    {
                        provided = true;
                        break;
                    }
                }
                if (!provided)
                {
                    values.Add(new ComponentInitValue(
                        components[i], fields[j].Id, new byte[fields[j].SizeBytes]));
                }
            }
        }
        return new ComponentInitBatch(values.ToArray());
    }

    private StorageOperationResult FailStop(
        StorageOperationResult result,
        string operation,
        Exception? exception = null)
    {
        if (_state != EcsWorldState.Disposed && _state != EcsWorldState.Faulted)
        {
            _state = EcsWorldState.Faulted;
            _firstFault = new EcsFaultEvidence(
                result.Error ?? new ErrorIdentity(EcsErrorCodes.InvalidState),
                new FailureContext(WorldId, default, default, default, default, operation, exception?.Message),
                0,
                exception?.GetType().FullName);
        }
        return StorageOperationResult.Fatal(result.Error?.Code ?? EcsErrorCodes.InvalidState);
    }
}
