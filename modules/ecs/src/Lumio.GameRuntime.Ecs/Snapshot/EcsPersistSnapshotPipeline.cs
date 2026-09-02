using System;
using System.Collections.Generic;
using System.IO;
using Lumio.GameRuntime.GeneratedContracts;

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

/// <summary>
/// Component-level persist snapshot and restore. Record bytes are an ADR-032 header
/// (<c>recordVersion</c>, <c>recordSeq</c>, <c>schemaEpoch</c>, <c>payloadHash</c>, <c>checksum</c>)
/// followed by a LumioBinV1 payload of fields marked persistent. Chat history is not stored.
/// </summary>
public static class EcsPersistSnapshotPipeline
{
    /// <summary>
    /// Captures persistent fields from <paramref name="world"/> into ADR-032 + LumioBinV1 bytes.
    /// </summary>
    /// <param name="world">Running world that owns the persist-only component fields.</param>
    /// <param name="bytes">Canonical persist record, or <see langword="null"/> when capture is rejected.</param>
    public static StorageOperationResult CapturePersist(EcsWorld world, out byte[]? bytes)
    {
        bytes = null;
#if NET10_0_OR_GREATER
        ArgumentNullException.ThrowIfNull(world);
#else
        if (world is null) throw new ArgumentNullException(nameof(world));
#endif
        ulong schemaEpoch = (ulong)GeneratedContractManifest.SchemaEpoch;
        if (schemaEpoch == 0UL)
            return StorageOperationResult.Rejected(EcsErrorCodes.InvalidArgument);

        var provider = new EcsWorldSnapshotProvider(world);
        var cut = new EcsSnapshotCutView(1UL, 1UL, 1UL, schemaEpoch);
        EcsSnapshotCaptureResult captured = provider.Capture(in cut);
        if (captured.Snapshot is null)
            return new StorageOperationResult(captured.Status, captured.Error);

        using EcsWorldReadSnapshot snapshot = captured.Snapshot;
        StorageOperationResult persist = Capture(
            snapshot,
            world.PersistRegisteredComponentTypes,
            world.Budget.MaxEntities,
            out EcsPersistSnapshotMaterial? material);
        if (!persist.IsSuccess || material is null)
            return persist;

        bytes = EcsPersistSnapshotRecord.Encode(material);
        return StorageOperationResult.Accepted();
    }

    /// <summary>
    /// Captures persistent fields from <paramref name="world"/> and atomically writes them to <paramref name="path"/>.
    /// </summary>
    /// <param name="world">Running world that owns the persist-only component fields.</param>
    /// <param name="path">Caller-chosen destination file. Written via a temp file and rename.</param>
    public static StorageOperationResult CapturePersist(EcsWorld world, string path)
    {
        StorageOperationResult captured = CapturePersist(world, out byte[]? bytes);
        if (!captured.IsSuccess || bytes is null)
            return captured;
        return WriteAtomic(path, bytes);
    }

    /// <summary>
    /// Restores persistent fields from ADR-032 + LumioBinV1 <paramref name="bytes"/> into <paramref name="world"/>.
    /// Checksum or schemaEpoch mismatch is rejected and the destination is left unchanged.
    /// </summary>
    /// <param name="world">Running world that already has the destination component schema registered.</param>
    /// <param name="bytes">Persist record previously produced by <see cref="CapturePersist(EcsWorld, out byte[])"/>.</param>
    public static StorageOperationResult RestorePersist(EcsWorld world, ReadOnlyMemory<byte> bytes)
    {
#if NET10_0_OR_GREATER
        ArgumentNullException.ThrowIfNull(world);
#else
        if (world is null) throw new ArgumentNullException(nameof(world));
#endif
        ulong schemaEpoch = (ulong)GeneratedContractManifest.SchemaEpoch;
        StorageOperationResult decoded = EcsPersistSnapshotRecord.TryDecode(bytes.Span, out EcsPersistSnapshotMaterial? material);
        if (!decoded.IsSuccess || material is null)
            return decoded;
        if (material.SchemaEpoch != schemaEpoch)
            return StorageOperationResult.Rejected(EcsErrorCodes.InvalidType);
        return Restore(world.PersistStorage, material, schemaEpoch, world.Budget.MaxEntities);
    }

    /// <summary>
    /// Reads <paramref name="path"/> and restores persistent fields into <paramref name="world"/>.
    /// </summary>
    /// <param name="world">Running world that already has the destination component schema registered.</param>
    /// <param name="path">Caller-chosen persist file.</param>
    public static StorageOperationResult RestorePersist(EcsWorld world, string path)
    {
#if NET10_0_OR_GREATER
        ArgumentNullException.ThrowIfNull(world);
#else
        if (world is null) throw new ArgumentNullException(nameof(world));
#endif
        if (string.IsNullOrWhiteSpace(path))
            return StorageOperationResult.Rejected(EcsErrorCodes.InvalidArgument);
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (IOException)
        {
            return StorageOperationResult.Rejected(EcsErrorCodes.InvalidArgument);
        }
        catch (UnauthorizedAccessException)
        {
            return StorageOperationResult.Rejected(EcsErrorCodes.InvalidArgument);
        }

        return RestorePersist(world, bytes);
    }

    private static StorageOperationResult WriteAtomic(string path, byte[] bytes)
    {
        if (string.IsNullOrWhiteSpace(path))
            return StorageOperationResult.Rejected(EcsErrorCodes.InvalidArgument);

        string? directory = Path.GetDirectoryName(path);
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.None))
            {
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(true);
            }
#if NET10_0_OR_GREATER
            File.Move(temp, path, overwrite: true);
#else
            if (File.Exists(path)) File.Replace(temp, path, destinationBackupFileName: null);
            else File.Move(temp, path);
#endif
            return StorageOperationResult.Accepted();
        }
        catch (IOException)
        {
            TryDelete(temp);
            return StorageOperationResult.Rejected(EcsErrorCodes.InvalidArgument);
        }
        catch (UnauthorizedAccessException)
        {
            TryDelete(temp);
            return StorageOperationResult.Rejected(EcsErrorCodes.InvalidArgument);
        }
        catch (ArgumentException)
        {
            TryDelete(temp);
            return StorageOperationResult.Rejected(EcsErrorCodes.InvalidArgument);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    internal static StorageOperationResult Capture(
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

    internal static StorageOperationResult Capture(
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

    internal static StorageOperationResult Restore(
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
