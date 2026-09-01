using System;
using System.Collections.Generic;

namespace Lumio.GameRuntime.Ecs;

internal readonly struct EcsPersistFieldRecord
{
    private readonly byte[] _canonicalValue;

    public EcsPersistFieldRecord(
        ComponentTypeId componentType,
        ComponentFieldId field,
        ReadOnlySpan<byte> canonicalValue)
    {
        ComponentType = componentType;
        Field = field;
        _canonicalValue = canonicalValue.ToArray();
    }

    public ComponentTypeId ComponentType { get; }

    public ComponentFieldId Field { get; }

    public ReadOnlyMemory<byte> CanonicalValue => _canonicalValue ?? Array.Empty<byte>();
}

internal readonly struct EcsPersistEntityRecord
{
    public EcsPersistEntityRecord(
        LocalEntityId entity,
        IReadOnlyList<EcsPersistFieldRecord> fields)
    {
        Entity = entity;
        Fields = fields;
    }

    public LocalEntityId Entity { get; }

    public IReadOnlyList<EcsPersistFieldRecord> Fields { get; }
}

internal sealed class EcsPersistSnapshotMaterial
{
    public EcsPersistSnapshotMaterial(
        ulong schemaEpoch,
        IReadOnlyList<EcsPersistEntityRecord> entities)
    {
        SchemaEpoch = schemaEpoch;
        Entities = entities;
    }

    public ulong SchemaEpoch { get; }

    public IReadOnlyList<EcsPersistEntityRecord> Entities { get; }
}

internal static class EcsPersistSnapshotPipeline
{
    public static StorageOperationResult Capture(
        IWorldStorageAdapter storage,
        StorageReadSnapshotHandle handle,
        ulong schemaEpoch,
        IReadOnlyList<ComponentTypeDefinition> registeredTypes,
        int maxEntities,
        out EcsPersistSnapshotMaterial? material)
    {
        material = null;
#if NET10_0_OR_GREATER
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(registeredTypes);
#else
        if (storage is null) throw new ArgumentNullException(nameof(storage));
        if (registeredTypes is null) throw new ArgumentNullException(nameof(registeredTypes));
#endif
        if (schemaEpoch == 0UL || maxEntities <= 0)
            return StorageOperationResult.Rejected(EcsErrorCodes.InvalidArgument);

        var entities = new LocalEntityId[maxEntities];
        StorageOperationResult enumerated = storage.EnumerateSnapshotOrdered(handle, entities, out int written);
        if (!enumerated.IsSuccess)
            return enumerated;

        return CaptureFromReads(
            schemaEpoch,
            registeredTypes,
            entities,
            written,
            storage.ReadSnapshotField,
            handle,
            out material);
    }

    public static StorageOperationResult Capture(
        EcsWorldReadSnapshot snapshot,
        IReadOnlyList<ComponentTypeDefinition> registeredTypes,
        int maxEntities,
        out EcsPersistSnapshotMaterial? material)
    {
        material = null;
#if NET10_0_OR_GREATER
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(registeredTypes);
#else
        if (snapshot is null) throw new ArgumentNullException(nameof(snapshot));
        if (registeredTypes is null) throw new ArgumentNullException(nameof(registeredTypes));
#endif
        if (snapshot.IsDisposed)
            return StorageOperationResult.Rejected(EcsErrorCodes.SnapshotReleased);
        if (snapshot.SchemaEpoch == 0UL || maxEntities <= 0)
            return StorageOperationResult.Rejected(EcsErrorCodes.InvalidArgument);

        var entities = new LocalEntityId[maxEntities];
        StorageOperationResult enumerated = snapshot.EnumerateEntities(entities, out int written);
        if (!enumerated.IsSuccess)
            return enumerated;

        return CaptureFromReads(
            snapshot.SchemaEpoch,
            registeredTypes,
            entities,
            written,
            ReadFromWorldSnapshot,
            default,
            out material);

        StorageOperationResult ReadFromWorldSnapshot(
            StorageReadSnapshotHandle _,
            LocalEntityId entity,
            ComponentTypeId componentType,
            ComponentFieldId field,
            Span<byte> destination,
            out int fieldWritten) =>
            snapshot.ReadField(entity, componentType, field, destination, out fieldWritten);
    }

    public static StorageOperationResult Restore(
        IWorldStorageAdapter destination,
        EcsPersistSnapshotMaterial material,
        ulong destinationSchemaEpoch,
        int maxEntities)
    {
#if NET10_0_OR_GREATER
        ArgumentNullException.ThrowIfNull(destination);
#else
        if (destination is null) throw new ArgumentNullException(nameof(destination));
#endif
        if (material is null ||
            destinationSchemaEpoch == 0UL ||
            material.SchemaEpoch == 0UL ||
            maxEntities <= 0 ||
            material.Entities is null)
        {
            return StorageOperationResult.Rejected(EcsErrorCodes.InvalidArgument);
        }

        if (material.SchemaEpoch != destinationSchemaEpoch)
            return StorageOperationResult.Rejected(EcsErrorCodes.InvalidType);

        StorageOperationResult validated = ValidateMaterial(material.Entities);
        if (!validated.IsSuccess)
            return validated;

        var destIds = new LocalEntityId[maxEntities];
        StorageOperationResult enumerated = destination.EnumerateOrdered(default, destIds, out int destCount);
        if (!enumerated.IsSuccess)
            return enumerated;

        var existing = new bool[material.Entities.Count];
        for (int entityIndex = 0; entityIndex < material.Entities.Count; entityIndex++)
        {
            LocalEntityId persistEntity = material.Entities[entityIndex].Entity;
            for (int destIndex = 0; destIndex < destCount; destIndex++)
            {
                LocalEntityId destEntity = destIds[destIndex];
                if (destEntity.Index != persistEntity.Index) continue;
                if (destEntity.Generation != persistEntity.Generation)
                    return StorageOperationResult.Rejected(EcsErrorCodes.StaleEntity);
                existing[entityIndex] = true;
            }
        }

        for (int entityIndex = 0; entityIndex < material.Entities.Count; entityIndex++)
        {
            EcsPersistEntityRecord record = material.Entities[entityIndex];
            StorageOperationResult applied = existing[entityIndex]
                ? WritePersistFields(destination, record)
                : CreatePersistEntity(destination, record);
            if (!applied.IsSuccess)
                return applied;
        }

        return StorageOperationResult.Accepted();
    }

    private delegate StorageOperationResult SnapshotFieldReader(
        StorageReadSnapshotHandle handle,
        LocalEntityId entity,
        ComponentTypeId componentType,
        ComponentFieldId field,
        Span<byte> destination,
        out int written);

    private static StorageOperationResult CaptureFromReads(
        ulong schemaEpoch,
        IReadOnlyList<ComponentTypeDefinition> registeredTypes,
        LocalEntityId[] entities,
        int written,
        SnapshotFieldReader readField,
        StorageReadSnapshotHandle handle,
        out EcsPersistSnapshotMaterial? material)
    {
        material = null;
        for (int i = 0; i < registeredTypes.Count; i++)
        {
            if (registeredTypes[i] is null)
                return StorageOperationResult.Rejected(EcsErrorCodes.InvalidArgument);
        }

        ComponentTypeDefinition[] persistTypes = CollectPersistTypes(registeredTypes);

        var captured = new List<EcsPersistEntityRecord>(written);
        for (int entityIndex = 0; entityIndex < written; entityIndex++)
        {
            LocalEntityId entity = entities[entityIndex];
            if (entity.IsDefault)
                return StorageOperationResult.Rejected(EcsErrorCodes.InvalidArgument);

            var fields = new List<EcsPersistFieldRecord>();
            for (int typeIndex = 0; typeIndex < persistTypes.Length; typeIndex++)
            {
                ComponentTypeDefinition type = persistTypes[typeIndex];
                ReadOnlySpan<ComponentFieldDefinition> typeFields = type.Fields.Span;
                for (int fieldIndex = 0; fieldIndex < typeFields.Length; fieldIndex++)
                {
                    ComponentFieldDefinition field = typeFields[fieldIndex];
                    if (field.Persistence != ComponentFieldPersistence.PersistOnly)
                        continue;

                    var destination = new byte[field.SizeBytes];
                    StorageOperationResult read = readField(
                        handle,
                        entity,
                        type.Id,
                        field.Id,
                        destination,
                        out int fieldWritten);
                    if (!read.IsSuccess)
                    {
                        if (read.Error?.Code == EcsErrorCodes.UnknownField)
                            continue;
                        return read;
                    }

                    if (fieldWritten != field.SizeBytes)
                        return StorageOperationResult.Rejected(EcsErrorCodes.InvalidArgument);
                    fields.Add(new EcsPersistFieldRecord(type.Id, field.Id, destination));
                }
            }

            if (fields.Count > 0)
                captured.Add(new EcsPersistEntityRecord(entity, fields.ToArray()));
        }

        material = new EcsPersistSnapshotMaterial(schemaEpoch, captured.ToArray());
        return StorageOperationResult.Accepted();
    }

    private static ComponentTypeDefinition[] CollectPersistTypes(IReadOnlyList<ComponentTypeDefinition> registeredTypes)
    {
        var persist = new List<ComponentTypeDefinition>(registeredTypes.Count);
        for (int i = 0; i < registeredTypes.Count; i++)
        {
            ComponentTypeDefinition type = registeredTypes[i];
            ReadOnlySpan<ComponentFieldDefinition> fields = type.Fields.Span;
            for (int fieldIndex = 0; fieldIndex < fields.Length; fieldIndex++)
            {
                if (fields[fieldIndex].Persistence == ComponentFieldPersistence.PersistOnly)
                {
                    persist.Add(type);
                    break;
                }
            }
        }

        persist.Sort(static (left, right) => left.Id.CompareTo(right.Id));
        return persist.ToArray();
    }

    private static StorageOperationResult ValidateMaterial(IReadOnlyList<EcsPersistEntityRecord> entities)
    {
        var seen = new HashSet<LocalEntityId>();
        for (int entityIndex = 0; entityIndex < entities.Count; entityIndex++)
        {
            EcsPersistEntityRecord record = entities[entityIndex];
            if (record.Entity.IsDefault ||
                record.Fields is null ||
                record.Fields.Count == 0 ||
                !seen.Add(record.Entity))
            {
                return StorageOperationResult.Rejected(EcsErrorCodes.InvalidArgument);
            }

            for (int fieldIndex = 0; fieldIndex < record.Fields.Count; fieldIndex++)
            {
                EcsPersistFieldRecord field = record.Fields[fieldIndex];
                if (field.ComponentType.IsDefault ||
                    field.Field.IsDefault ||
                    field.CanonicalValue.Length == 0)
                {
                    return StorageOperationResult.Rejected(EcsErrorCodes.InvalidArgument);
                }
            }
        }

        return StorageOperationResult.Accepted();
    }

    private static StorageOperationResult WritePersistFields(
        IWorldStorageAdapter destination,
        in EcsPersistEntityRecord record)
    {
        for (int i = 0; i < record.Fields.Count; i++)
        {
            EcsPersistFieldRecord field = record.Fields[i];
            StorageOperationResult written = destination.WriteExistingField(
                record.Entity,
                field.ComponentType,
                field.Field,
                field.CanonicalValue.Span);
            if (!written.IsSuccess)
                return written;
        }

        return StorageOperationResult.Accepted();
    }

    private static StorageOperationResult CreatePersistEntity(
        IWorldStorageAdapter destination,
        in EcsPersistEntityRecord record)
    {
        var values = new ComponentInitValue[record.Fields.Count];
        for (int i = 0; i < record.Fields.Count; i++)
        {
            EcsPersistFieldRecord field = record.Fields[i];
            values[i] = new ComponentInitValue(field.ComponentType, field.Field, field.CanonicalValue);
        }

        var batch = new ComponentInitBatch(values);
        return destination.Create(record.Entity, in batch);
    }
}
