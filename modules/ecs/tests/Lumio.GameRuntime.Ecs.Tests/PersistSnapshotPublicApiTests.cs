using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Lumio.GameRuntime.Ecs;
using Lumio.GameRuntime.GeneratedContracts;
using Xunit;

namespace Lumio.GameRuntime.Ecs.Tests;

public sealed class PersistSnapshotPublicApiTests
{
    [Fact]
    public void PersistPipelineIsPublicAndExposesCaptureRestorePersist()
    {
        Type pipeline = typeof(EcsPersistSnapshotPipeline);
        Assert.True(pipeline.IsPublic);
        Assert.True(pipeline.IsAbstract && pipeline.IsSealed);

        MethodInfo captureBytes = Assert.Single(
            pipeline.GetMethods(BindingFlags.Public | BindingFlags.Static),
            static method => method.Name == "CapturePersist" &&
                method.GetParameters().Length == 2 &&
                method.GetParameters()[0].ParameterType == typeof(EcsWorld) &&
                method.GetParameters()[1].ParameterType == typeof(byte[]).MakeByRefType());
        MethodInfo capturePath = Assert.Single(
            pipeline.GetMethods(BindingFlags.Public | BindingFlags.Static),
            static method => method.Name == "CapturePersist" &&
                method.GetParameters().Length == 2 &&
                method.GetParameters()[0].ParameterType == typeof(EcsWorld) &&
                method.GetParameters()[1].ParameterType == typeof(string));
        MethodInfo restoreBytes = Assert.Single(
            pipeline.GetMethods(BindingFlags.Public | BindingFlags.Static),
            static method => method.Name == "RestorePersist" &&
                method.GetParameters().Length == 2 &&
                method.GetParameters()[0].ParameterType == typeof(EcsWorld) &&
                method.GetParameters()[1].ParameterType == typeof(ReadOnlyMemory<byte>));
        MethodInfo restorePath = Assert.Single(
            pipeline.GetMethods(BindingFlags.Public | BindingFlags.Static),
            static method => method.Name == "RestorePersist" &&
                method.GetParameters().Length == 2 &&
                method.GetParameters()[0].ParameterType == typeof(EcsWorld) &&
                method.GetParameters()[1].ParameterType == typeof(string));

        Assert.Equal(typeof(StorageOperationResult), captureBytes.ReturnType);
        Assert.Equal(typeof(StorageOperationResult), capturePath.ReturnType);
        Assert.Equal(typeof(StorageOperationResult), restoreBytes.ReturnType);
        Assert.Equal(typeof(StorageOperationResult), restorePath.ReturnType);
        Assert.Null(pipeline.GetMethod(
            "Capture",
            BindingFlags.Public | BindingFlags.Static,
            binder: null,
            types: new[]
            {
                typeof(IWorldStorageAdapter),
                typeof(StorageReadSnapshotHandle),
                typeof(ulong),
                typeof(IReadOnlyList<ComponentTypeDefinition>),
                typeof(int),
                typeof(EcsPersistSnapshotMaterial).MakeByRefType()
            },
            modifiers: null));
    }

    [Fact]
    public void CapturePersistRoundtripRestoresLastMessageAndLeavesHistoryEmpty()
    {
        using SourceWorld source = PersistSnapshotTestSchema.CreatePopulatedWorld(940);
        StorageOperationResult captured = EcsPersistSnapshotPipeline.CapturePersist(source.World, out byte[]? bytes);
        Assert.Equal(StorageOperationStatus.Accepted, captured.Status);
        Assert.NotNull(bytes);
        Assert.NotEmpty(bytes);

        using DestWorld destination = PersistSnapshotTestSchema.CreateEmptyRunningWorld(941);
        StorageOperationResult restored = EcsPersistSnapshotPipeline.RestorePersist(destination.World, bytes);
        Assert.Equal(StorageOperationStatus.Accepted, restored.Status);

        PersistSnapshotTestSchema.AssertLastMessages(destination.Storage, source.Entities);
        int observedHistoryCount = PersistSnapshotTestSchema.ObserveHistoryCount(destination.Storage, source.Entities[0].Entity);
        Assert.Equal(0, observedHistoryCount);
        PersistSnapshotTestSchema.AssertPayloadOmitsHistory(bytes);
    }

    [Fact]
    public void CapturePersistThenRestorePersistBytesAreStableAcrossTwoRounds()
    {
        using SourceWorld source = PersistSnapshotTestSchema.CreatePopulatedWorld(942);
        Assert.Equal(StorageOperationStatus.Accepted,
            EcsPersistSnapshotPipeline.CapturePersist(source.World, out byte[]? first).Status);
        using DestWorld destination = PersistSnapshotTestSchema.CreateEmptyRunningWorld(943);
        Assert.Equal(StorageOperationStatus.Accepted,
            EcsPersistSnapshotPipeline.RestorePersist(destination.World, first!).Status);
        Assert.Equal(StorageOperationStatus.Accepted,
            EcsPersistSnapshotPipeline.CapturePersist(destination.World, out byte[]? second).Status);

        Assert.Equal(first, second);
        Console.WriteLine("ROUND1_SHA256=" + PersistSnapshotTestSchema.Sha256Hex(first!));
        Console.WriteLine("ROUND2_SHA256=" + PersistSnapshotTestSchema.Sha256Hex(second!));
    }

    [Fact]
    public void CapturePersistWritesAtomicallyAndRestorePersistReadsCallerPath()
    {
        string directory = Path.Combine(Path.GetTempPath(), "lumio-ecs-persist-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "room.persist");
        try
        {
            using SourceWorld source = PersistSnapshotTestSchema.CreatePopulatedWorld(944);
            StorageOperationResult written = EcsPersistSnapshotPipeline.CapturePersist(source.World, path);
            Assert.Equal(StorageOperationStatus.Accepted, written.Status);
            Assert.True(File.Exists(path));
            Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
            byte[] onDisk = File.ReadAllBytes(path);
            Assert.Equal(StorageOperationStatus.Accepted,
                EcsPersistSnapshotPipeline.CapturePersist(source.World, out byte[]? inMemory).Status);
            Assert.Equal(inMemory, onDisk);
            Console.WriteLine("FILE_SHA256=" + PersistSnapshotTestSchema.Sha256Hex(onDisk));

            using DestWorld destination = PersistSnapshotTestSchema.CreateEmptyRunningWorld(945);
            StorageOperationResult restored = EcsPersistSnapshotPipeline.RestorePersist(destination.World, path);
            Assert.Equal(StorageOperationStatus.Accepted, restored.Status);
            PersistSnapshotTestSchema.AssertLastMessages(destination.Storage, source.Entities);
            Assert.Equal(0, PersistSnapshotTestSchema.ObserveHistoryCount(destination.Storage, source.Entities[0].Entity));
        }
        finally
        {
            try
            {
                if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    [Fact]
    public void RestorePersistRejectsChecksumMismatchWithoutMutatingDestination()
    {
        using SourceWorld source = PersistSnapshotTestSchema.CreatePopulatedWorld(946);
        Assert.Equal(StorageOperationStatus.Accepted,
            EcsPersistSnapshotPipeline.CapturePersist(source.World, out byte[]? bytes).Status);
        byte[] tampered = (byte[])bytes!.Clone();
        tampered[^1] ^= 0xFF;
        using DestWorld destination = PersistSnapshotTestSchema.CreateEmptyRunningWorld(947);

        StorageOperationResult restored = EcsPersistSnapshotPipeline.RestorePersist(destination.World, tampered);

        Assert.Equal(StorageOperationStatus.Rejected, restored.Status);
        Assert.Equal(EcsErrorCodes.InvalidArgument, restored.Error?.Code);
        Assert.Equal(0, destination.Storage.EntityCount);
    }

    [Fact]
    public void RestorePersistRejectsSchemaEpochMismatchWithoutMutatingDestination()
    {
        using SourceWorld source = PersistSnapshotTestSchema.CreatePopulatedWorld(948);
        Assert.Equal(StorageOperationStatus.Accepted,
            EcsPersistSnapshotPipeline.CapturePersist(source.World, out byte[]? bytes).Status);
        byte[] tampered = PersistSnapshotTestSchema.RewriteSchemaEpoch(bytes!, (ulong)GeneratedContractManifest.SchemaEpoch + 1UL);
        using DestWorld destination = PersistSnapshotTestSchema.CreateEmptyRunningWorld(949);

        StorageOperationResult restored = EcsPersistSnapshotPipeline.RestorePersist(destination.World, tampered);

        Assert.Equal(StorageOperationStatus.Rejected, restored.Status);
        Assert.Equal(EcsErrorCodes.InvalidType, restored.Error?.Code);
        Assert.Equal(0, destination.Storage.EntityCount);
    }

    [Fact]
    public void CapturePersistRecordHeaderMatchesAdr032FieldsAndLumioBinPayload()
    {
        using SourceWorld source = PersistSnapshotTestSchema.CreatePopulatedWorld(950);
        Assert.Equal(StorageOperationStatus.Accepted,
            EcsPersistSnapshotPipeline.CapturePersist(source.World, out byte[]? bytes).Status);

        PersistSnapshotTestSchema.DecodedHeader header = PersistSnapshotTestSchema.ReadHeader(bytes!);
        Assert.Equal(1UL, header.RecordVersion);
        Assert.Equal(1UL, header.RecordSeq);
        Assert.Equal((ulong)GeneratedContractManifest.SchemaEpoch, header.SchemaEpoch);
        Assert.Equal(64, header.PayloadHash.Length);
        Assert.Equal(64, header.Checksum.Length);
        Assert.Equal(header.Checksum, PersistSnapshotTestSchema.Sha256Hex(header.HeaderWithoutChecksum));
        Assert.Equal(header.PayloadHash, PersistSnapshotTestSchema.Sha256Hex(header.Payload));
        Assert.True(header.Payload.Length >= 4);
        uint entityCount = BinaryPrimitives.ReadUInt32LittleEndian(header.Payload);
        Assert.Equal((uint)source.Entities.Length, entityCount);
    }
}

internal readonly record struct SourceEntity(LocalEntityId Entity, string Text, ulong Tick, string PersistOnly);

internal sealed class SourceWorld : IDisposable
{
    public SourceWorld(EcsModule module, EcsWorld world, SourceEntity[] entities)
    {
        Module = module;
        World = world;
        Entities = entities;
        Storage = PersistSnapshotTestSchema.StorageOf(world);
    }

    public EcsModule Module { get; }

    public EcsWorld World { get; }

    public SourceEntity[] Entities { get; }

    public ReferenceWorldStorageAdapter Storage { get; }

    public void Dispose() => Module.Dispose();
}

internal sealed class DestWorld : IDisposable
{
    public DestWorld(EcsModule module, EcsWorld world)
    {
        Module = module;
        World = world;
        Storage = PersistSnapshotTestSchema.StorageOf(world);
    }

    public EcsModule Module { get; }

    public EcsWorld World { get; }

    public ReferenceWorldStorageAdapter Storage { get; }

    public void Dispose() => Module.Dispose();
}

internal static class PersistSnapshotTestSchema
{
    internal const int LastMessageTextMaxUtf8Bytes = 512;
    internal const int LastMessageTextSizeBytes = 4 + LastMessageTextMaxUtf8Bytes;
    internal const int LastMessageTickSizeBytes = 8;
    internal const int HistoryCountSizeBytes = 4;

    internal static readonly ComponentTypeId ChatComponentType = new(40);
    internal static readonly ComponentFieldId LastMessageTextField = new(1);
    internal static readonly ComponentFieldId LastMessageTickField = new(2);
    internal static readonly ComponentFieldId LastMessagePersistOnlyField = new(3);
    internal static readonly ComponentTypeId ChatHistoryType = new(41);
    internal static readonly ComponentFieldId HistoryCountField = new(1);

    internal static SourceWorld CreatePopulatedWorld(ulong worldId)
    {
        var module = new EcsModule();
        EcsWorld world = NewRunningWorld(module, worldId, includeHistory: true, out EntityTypeHandle entityType);
        SourceEntity[] entities =
        {
            new(default, "gg", 7UL, "probe-a"),
            new(default, "hi", 11UL, "probe-b")
        };
        for (int i = 0; i < entities.Length; i++)
        {
            EntityCreateResult created = world.CreateEntityForCommit(
                world.Context,
                new EntityCreateRequest(
                    entityType,
                    new ComponentInitBatch(new[]
                    {
                        new ComponentInitValue(ChatComponentType, LastMessageTextField, EncodeText(entities[i].Text)),
                        new ComponentInitValue(ChatComponentType, LastMessageTickField, EncodeTick(entities[i].Tick)),
                        new ComponentInitValue(ChatComponentType, LastMessagePersistOnlyField, EncodeText(entities[i].PersistOnly)),
                        new ComponentInitValue(ChatHistoryType, HistoryCountField, EncodeHistoryCount(2U))
                    })));
            Assert.True(created.Created);
            entities[i] = entities[i] with { Entity = created.Entity };
        }

        Assert.Equal(2, PersistSnapshotTestSchema.ObserveHistoryCount(StorageOf(world), entities[0].Entity));
        return new SourceWorld(module, world, entities);
    }

    internal static DestWorld CreateEmptyRunningWorld(ulong worldId)
    {
        var module = new EcsModule();
        EcsWorld world = NewRunningWorld(module, worldId, includeHistory: true, out _);
        return new DestWorld(module, world);
    }

    internal static EcsWorld NewRunningWorld(
        EcsModule module,
        ulong worldId,
        bool includeHistory,
        out EntityTypeHandle entityType)
    {
        var request = new EcsWorldCreateRequest(new WorldId(worldId), new EcsBudget(8, 32, 32, 4096));
        EcsWorld world = Assert.IsType<EcsWorld>(module.CreateWorld(in request).World);
        Assert.Equal(StorageOperationStatus.Accepted, world.BeginRegistration().Status);
        ComponentTypeRegistrationResult chat = EcsTestRegistration.Register(world, ChatComponentDefinition());
        Assert.True(chat.Registered);
        var handles = new List<ComponentTypeHandle> { chat.Handle };
        if (includeHistory)
        {
            ComponentTypeRegistrationResult history = EcsTestRegistration.Register(world, ChatHistoryDefinition());
            Assert.True(history.Registered);
            handles.Add(history.Handle);
        }

        EntityTypeRegistrationResult entity = world.RegisterEntityType(new EntityTypeDefinition("Chatter", handles));
        Assert.True(entity.Registered);
        entityType = entity.Handle;
        Assert.Equal(StorageOperationStatus.Accepted, world.MarkReady().Status);
        Assert.Equal(StorageOperationStatus.Accepted, world.Start().Status);
        return world;
    }

    internal static ReferenceWorldStorageAdapter StorageOf(EcsWorld world)
    {
        FieldInfo storageField = typeof(EcsWorld).GetField(
            "_storage",
            BindingFlags.Instance | BindingFlags.NonPublic) ??
            throw new InvalidOperationException("World storage field is missing.");
        return Assert.IsType<ReferenceWorldStorageAdapter>(storageField.GetValue(world));
    }

    internal static void AssertLastMessages(ReferenceWorldStorageAdapter storage, SourceEntity[] entities)
    {
        for (int i = 0; i < entities.Length; i++)
        {
            SourceEntity entity = entities[i];
            Assert.Equal(EncodeText(entity.Text), ReadField(storage, entity.Entity, ChatComponentType, LastMessageTextField, LastMessageTextSizeBytes));
            Assert.Equal(EncodeTick(entity.Tick), ReadField(storage, entity.Entity, ChatComponentType, LastMessageTickField, LastMessageTickSizeBytes));
            Assert.Equal(EncodeText(entity.PersistOnly), ReadField(storage, entity.Entity, ChatComponentType, LastMessagePersistOnlyField, LastMessageTextSizeBytes));
        }
    }

    internal static byte[] ReadRequired(
        ReferenceWorldStorageAdapter storage,
        LocalEntityId entity,
        ComponentTypeId componentType,
        ComponentFieldId field,
        int size)
    {
        var destination = new byte[size];
        StorageOperationResult read = storage.ReadField(entity, componentType, field, destination, out int written);
        if (!read.IsSuccess || written != size)
            throw new InvalidOperationException(read.Error?.Code ?? "field-read-failed");
        return destination;
    }

    internal static int ObserveHistoryCount(ReferenceWorldStorageAdapter storage, LocalEntityId entity)
    {
        var destination = new byte[HistoryCountSizeBytes];
        StorageOperationResult read = storage.ReadField(
            entity,
            ChatHistoryType,
            HistoryCountField,
            destination,
            out int written);
        if (!read.IsSuccess || written != HistoryCountSizeBytes)
            return 0;
        return (int)BinaryPrimitives.ReadUInt32LittleEndian(destination);
    }

    internal static void AssertPayloadOmitsHistory(byte[] record)
    {
        DecodedHeader header = ReadHeader(record);
        ReadOnlySpan<byte> payload = header.Payload;
        int offset = 0;
        uint entityCount = ReadUInt32(payload, ref offset);
        for (uint entityIndex = 0; entityIndex < entityCount; entityIndex++)
        {
            _ = ReadUInt32(payload, ref offset);
            _ = ReadUInt32(payload, ref offset);
            uint fieldCount = ReadUInt32(payload, ref offset);
            for (uint fieldIndex = 0; fieldIndex < fieldCount; fieldIndex++)
            {
                ulong componentType = ReadUInt64(payload, ref offset);
                ulong field = ReadUInt64(payload, ref offset);
                uint valueLength = ReadUInt32(payload, ref offset);
                offset = checked(offset + (int)valueLength);
                Assert.NotEqual(ChatHistoryType.Value, componentType);
                Assert.False(componentType == ChatHistoryType.Value && field == HistoryCountField.Value);
            }
        }
    }

    internal static DecodedHeader ReadHeader(byte[] record)
    {
        int offset = 0;
        ulong recordVersion = ReadUInt64(record, ref offset);
        ulong recordSeq = ReadUInt64(record, ref offset);
        ulong schemaEpoch = ReadUInt64(record, ref offset);
        string payloadHash = ReadString(record, ref offset);
        byte[] headerWithoutChecksum = record.AsSpan(0, offset).ToArray();
        string checksum = ReadString(record, ref offset);
        byte[] payload = record.AsSpan(offset).ToArray();
        return new DecodedHeader(recordVersion, recordSeq, schemaEpoch, payloadHash, checksum, headerWithoutChecksum, payload);
    }

    internal static byte[] RewriteSchemaEpoch(byte[] record, ulong schemaEpoch)
    {
        DecodedHeader header = ReadHeader(record);
        using var stream = new MemoryStream();
        WriteUInt64(stream, header.RecordVersion);
        WriteUInt64(stream, header.RecordSeq);
        WriteUInt64(stream, schemaEpoch);
        WriteString(stream, header.PayloadHash);
        string checksum = Sha256Hex(stream.ToArray());
        WriteString(stream, checksum);
        stream.Write(header.Payload, 0, header.Payload.Length);
        return stream.ToArray();
    }

    internal static string Sha256Hex(byte[] bytes)
    {
        byte[] digest = SHA256.HashData(bytes);
        return Convert.ToHexString(digest).ToLowerInvariant();
    }

    internal static byte[] EncodeText(string text)
    {
        byte[] utf8 = Encoding.UTF8.GetBytes(text);
        if (utf8.Length > LastMessageTextMaxUtf8Bytes)
            throw new ArgumentOutOfRangeException(nameof(text));
        var field = new byte[LastMessageTextSizeBytes];
        BinaryPrimitives.WriteUInt32LittleEndian(field, (uint)utf8.Length);
        utf8.CopyTo(field.AsSpan(4));
        return field;
    }

    internal static byte[] EncodeTick(ulong tick)
    {
        var field = new byte[LastMessageTickSizeBytes];
        BinaryPrimitives.WriteUInt64LittleEndian(field, tick);
        return field;
    }

    internal static byte[] EncodeHistoryCount(uint count)
    {
        var field = new byte[HistoryCountSizeBytes];
        BinaryPrimitives.WriteUInt32LittleEndian(field, count);
        return field;
    }

    private static byte[] ReadField(
        ReferenceWorldStorageAdapter storage,
        LocalEntityId entity,
        ComponentTypeId componentType,
        ComponentFieldId field,
        int size)
    {
        var destination = new byte[size];
        Assert.Equal(StorageOperationStatus.Accepted, storage.ReadField(
            entity,
            componentType,
            field,
            destination,
            out int written).Status);
        Assert.Equal(size, written);
        return destination;
    }

    private static ComponentTypeDefinition ChatComponentDefinition() => new(
        ChatComponentType,
        "ChatComponent",
        new[]
        {
            new ComponentFieldDefinition(LastMessageTextField, LastMessageTextSizeBytes, ComponentFieldPersistence.PersistOnly),
            new ComponentFieldDefinition(LastMessageTickField, LastMessageTickSizeBytes, ComponentFieldPersistence.PersistOnly),
            new ComponentFieldDefinition(LastMessagePersistOnlyField, LastMessageTextSizeBytes, ComponentFieldPersistence.PersistOnly)
        });

    private static ComponentTypeDefinition ChatHistoryDefinition() => new(
        ChatHistoryType,
        "ChatHistory",
        new[]
        {
            new ComponentFieldDefinition(HistoryCountField, HistoryCountSizeBytes)
        });

    private static ulong ReadUInt64(ReadOnlySpan<byte> source, ref int offset)
    {
        ulong value = BinaryPrimitives.ReadUInt64LittleEndian(source.Slice(offset, 8));
        offset += 8;
        return value;
    }

    private static uint ReadUInt32(ReadOnlySpan<byte> source, ref int offset)
    {
        uint value = BinaryPrimitives.ReadUInt32LittleEndian(source.Slice(offset, 4));
        offset += 4;
        return value;
    }

    private static string ReadString(ReadOnlySpan<byte> source, ref int offset)
    {
        uint length = ReadUInt32(source, ref offset);
        string value = Encoding.UTF8.GetString(source.Slice(offset, checked((int)length)));
        offset = checked(offset + (int)length);
        return value;
    }

    private static void WriteUInt64(Stream stream, ulong value)
    {
        Span<byte> buffer = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(buffer, value);
        stream.Write(buffer);
    }

    private static void WriteString(Stream stream, string value)
    {
        byte[] utf8 = Encoding.UTF8.GetBytes(value);
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(length, (uint)utf8.Length);
        stream.Write(length);
        stream.Write(utf8, 0, utf8.Length);
    }

    internal readonly record struct DecodedHeader(
        ulong RecordVersion,
        ulong RecordSeq,
        ulong SchemaEpoch,
        string PayloadHash,
        string Checksum,
        byte[] HeaderWithoutChecksum,
        byte[] Payload);
}
