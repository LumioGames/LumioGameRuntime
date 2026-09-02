using System;
using System.Collections.Generic;

namespace Lumio.GameRuntime.Ecs;

public sealed partial class EcsWorld
{
    internal IWorldStorageAdapter PersistStorage => _storage;

    internal IReadOnlyList<ComponentTypeDefinition> PersistRegisteredComponentTypes
    {
        get
        {
            lock (_lifecycleSync)
            {
                if (_componentsByName.Count == 0)
                    return Array.Empty<ComponentTypeDefinition>();
                var types = new ComponentTypeDefinition[_componentsByName.Count];
                _componentsByName.Values.CopyTo(types, 0);
                return types;
            }
        }
    }

    internal StorageOperationResult RestorePersistMaterial(EcsPersistSnapshotMaterial material, ulong destinationSchemaEpoch)
    {
#if NET10_0_OR_GREATER
        ArgumentNullException.ThrowIfNull(material);
#else
        if (material is null) throw new ArgumentNullException(nameof(material));
#endif
        lock (_lifecycleSync)
        {
            StorageOperationResult state = EnsureWritableStateUnsafe();
            if (!state.IsSuccess) return state;
            StorageOperationResult owner = ValidateOwnerForWrite();
            if (!owner.IsSuccess) return owner;
            if (destinationSchemaEpoch == 0UL ||
                material.SchemaEpoch == 0UL ||
                material.Entities is null)
            {
                return StorageOperationResult.Rejected(EcsErrorCodes.InvalidArgument);
            }

            if (material.SchemaEpoch != destinationSchemaEpoch)
                return StorageOperationResult.Rejected(EcsErrorCodes.InvalidType);
            if (material.Entities.Count > _budget.MaxEntities)
                return StorageOperationResult.Rejected(EcsErrorCodes.BudgetExceeded);

            StorageOperationResult validated = EcsPersistSnapshotPipeline.ValidateMaterial(material.Entities);
            if (!validated.IsSuccess)
                return validated;

            var adopt = new bool[material.Entities.Count];
            for (int entityIndex = 0; entityIndex < material.Entities.Count; entityIndex++)
            {
                LocalEntityId entity = material.Entities[entityIndex].Entity;
                if (_entities.TryResolve(entity, out EntityLifecycleState existingState, out _, out _))
                {
                    if (existingState is EntityLifecycleState.Tombstoned or EntityLifecycleState.Destroyed)
                        return StorageOperationResult.Rejected(EcsErrorCodes.StaleEntity);
                    adopt[entityIndex] = false;
                    continue;
                }

                if (IndexIsOccupiedByOtherGeneration(entity))
                    return StorageOperationResult.Rejected(EcsErrorCodes.StaleEntity);
                adopt[entityIndex] = true;
            }

            for (int entityIndex = 0; entityIndex < material.Entities.Count; entityIndex++)
            {
                EcsPersistEntityRecord record = material.Entities[entityIndex];
                if (!adopt[entityIndex])
                {
                    StorageOperationResult written = EcsPersistSnapshotPipeline.WritePersistFields(_storage, in record);
                    if (!written.IsSuccess)
                        return CompleteBoundary(written, "RestorePersist", entity: record.Entity);
                    continue;
                }

                if (!TrySelectPersistEntityType(record, out EntityTypeHandle type, out EntityTypeDefinition definition))
                    return StorageOperationResult.Rejected(EcsErrorCodes.InvalidType);
                if (!_entities.TryAdopt(record.Entity, type, definition.DefaultMode, out StorageOperationResult adopted))
                    return adopted;

                StorageOperationResult created = EcsPersistSnapshotPipeline.CreatePersistEntity(_storage, in record);
                created = CompleteBoundary(created, "RestorePersist", entity: record.Entity);
                if (!created.IsSuccess)
                {
                    _entities.TryRetire(record.Entity);
                    return created;
                }

                if (!_entities.TrySetState(record.Entity, EntityLifecycleState.Alive))
                {
                    _storage.Destroy(record.Entity);
                    _entities.TryRetire(record.Entity);
                    return CompleteBoundary(
                        StorageOperationResult.Fatal(EcsErrorCodes.InvalidState),
                        "RestorePersist",
                        entity: record.Entity);
                }
            }

            return StorageOperationResult.Accepted();
        }
    }

    private bool IndexIsOccupiedByOtherGeneration(LocalEntityId entity)
    {
        foreach ((LocalEntityId id, _, _, _) in _entities.EnumerateActiveOrdered())
        {
            if (id.Index == entity.Index && id.Generation != entity.Generation)
                return true;
        }

        return false;
    }

    private bool TrySelectPersistEntityType(
        in EcsPersistEntityRecord record,
        out EntityTypeHandle handle,
        out EntityTypeDefinition definition)
    {
        handle = default;
        definition = null!;
        int bestComponents = int.MaxValue;
        foreach (KeyValuePair<EntityTypeHandle, EntityTypeDefinition> pair in _entityTypes)
        {
            if (!EntityTypeCoversPersistFields(pair.Value, record.Fields))
                continue;
            int componentCount = pair.Value.ComponentTypes.Length;
            if (handle.IsDefault ||
                componentCount < bestComponents ||
                (componentCount == bestComponents && pair.Key.Value < handle.Value))
            {
                handle = pair.Key;
                definition = pair.Value;
                bestComponents = componentCount;
            }
        }

        return !handle.IsDefault;
    }

    private bool EntityTypeCoversPersistFields(
        EntityTypeDefinition entityType,
        IReadOnlyList<EcsPersistFieldRecord> fields)
    {
        for (int fieldIndex = 0; fieldIndex < fields.Count; fieldIndex++)
        {
            ComponentTypeId componentType = fields[fieldIndex].ComponentType;
            bool covered = false;
            ReadOnlySpan<ComponentTypeHandle> components = entityType.ComponentTypes.Span;
            for (int componentIndex = 0; componentIndex < components.Length; componentIndex++)
            {
                if (_componentTypes.TryGet(components[componentIndex], out ComponentTypeDefinition? registered) &&
                    registered.Id == componentType)
                {
                    covered = true;
                    break;
                }
            }

            if (!covered) return false;
        }

        return true;
    }
}
