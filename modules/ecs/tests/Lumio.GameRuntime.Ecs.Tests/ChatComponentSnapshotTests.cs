using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;
using Lumio.GameRuntime.Ecs;
using Xunit;

namespace Lumio.GameRuntime.Ecs.Tests;

public sealed class ChatComponentSnapshotTests
{
    private const ulong SchemaEpoch = 1UL;
    private const int MaxEntities = 8;
    private const int LastMessageTextMaxUtf8Bytes = 512;
    private const int LastMessageTextSizeBytes = 4 + LastMessageTextMaxUtf8Bytes;
    private const int LastMessageTickSizeBytes = 8;
    private const int LiveWindowFieldSizeBytes = 32;

    private static readonly ComponentTypeId ChatComponentType = new(40);
    private static readonly ComponentFieldId LastMessageTextField = new(1);
    private static readonly ComponentFieldId LastMessageTickField = new(2);
    private static readonly ComponentTypeId ChatLiveWindowType = new(41);
    private static readonly ComponentFieldId ChatEventHistoryField = new(1);
    private static readonly ComponentFieldId DisplayedWindowField = new(2);

    [Fact]
    public void PersistSnapshotContainsLastMessageFieldsForEligibleEntities()
    {
        using SourceWorld source = CreatePopulatedSource(new WorldId(900));
        StorageOperationResult captured = EcsPersistSnapshotPipeline.Capture(
            source.Storage,
            source.Snapshot,
            SchemaEpoch,
            PersistSchema(),
            MaxEntities,
            out EcsPersistSnapshotMaterial? material);

        Assert.Equal(StorageOperationStatus.Accepted, captured.Status);
        Assert.NotNull(material);
        Assert.Equal(SchemaEpoch, material.SchemaEpoch);
        EcsPersistEntityRecord entity = Assert.Single(material.Entities);
        Assert.Equal(source.Entity, entity.Entity);
        Assert.Equal(2, entity.Fields.Count);
        Assert.Equal(ChatComponentType, entity.Fields[0].ComponentType);
        Assert.Equal(LastMessageTextField, entity.Fields[0].Field);
        Assert.Equal(EncodeText("gg"), entity.Fields[0].CanonicalValue.ToArray());
        Assert.Equal(ChatComponentType, entity.Fields[1].ComponentType);
        Assert.Equal(LastMessageTickField, entity.Fields[1].Field);
        Assert.Equal(EncodeTick(7), entity.Fields[1].CanonicalValue.ToArray());
        Assert.DoesNotContain(entity.Fields, static field => field.ComponentType == ChatLiveWindowType);

        var liveWindow = new byte[LiveWindowFieldSizeBytes];
        Assert.Equal(StorageOperationStatus.Accepted, source.Storage.ReadSnapshotField(
            source.Snapshot,
            source.Entity,
            ChatLiveWindowType,
            ChatEventHistoryField,
            liveWindow,
            out int written).Status);
        Assert.Equal(LiveWindowFieldSizeBytes, written);
        Assert.Equal(EncodeAscii32("event-1"), liveWindow);
    }

    [Fact]
    public void RestoreReproducesLastMessageFieldsIntoAFreshWorld()
    {
        using SourceWorld source = CreatePopulatedSource(new WorldId(901));
        Assert.Equal(StorageOperationStatus.Accepted, EcsPersistSnapshotPipeline.Capture(
            source.Storage,
            source.Snapshot,
            SchemaEpoch,
            PersistSchema(),
            MaxEntities,
            out EcsPersistSnapshotMaterial? material).Status);
        using var destination = new ReferenceWorldStorageAdapter(new WorldId(911), MaxEntities, 4096);
        RegisterChatSchema(destination);

        StorageOperationResult restored = EcsPersistSnapshotPipeline.Restore(
            destination,
            material!,
            SchemaEpoch,
            MaxEntities);

        Assert.Equal(StorageOperationStatus.Accepted, restored.Status);
        AssertLastMessage(destination, source.Entity, "gg", 7UL);
    }

    [Fact]
    public void RestoreDoesNotIncludeChatEventOrDisplayedWindowHistory()
    {
        using SourceWorld source = CreatePopulatedSource(new WorldId(902));
        Assert.Equal(StorageOperationStatus.Accepted, EcsPersistSnapshotPipeline.Capture(
            source.Storage,
            source.Snapshot,
            SchemaEpoch,
            PersistSchema(),
            MaxEntities,
            out EcsPersistSnapshotMaterial? material).Status);
        using var destination = new ReferenceWorldStorageAdapter(new WorldId(912), MaxEntities, 4096);
        RegisterChatSchema(destination);

        Assert.Equal(StorageOperationStatus.Accepted, EcsPersistSnapshotPipeline.Restore(
            destination,
            material!,
            SchemaEpoch,
            MaxEntities).Status);

        var history = new byte[LiveWindowFieldSizeBytes];
        StorageOperationResult historyRead = destination.ReadField(
            source.Entity,
            ChatLiveWindowType,
            ChatEventHistoryField,
            history,
            out int historyWritten);
        var window = new byte[LiveWindowFieldSizeBytes];
        StorageOperationResult windowRead = destination.ReadField(
            source.Entity,
            ChatLiveWindowType,
            DisplayedWindowField,
            window,
            out int windowWritten);

        AssertLastMessage(destination, source.Entity, "gg", 7UL);
        Assert.Equal(StorageOperationStatus.Rejected, historyRead.Status);
        Assert.Equal(EcsErrorCodes.UnknownField, historyRead.Error?.Code);
        Assert.Equal(0, historyWritten);
        Assert.Equal(StorageOperationStatus.Rejected, windowRead.Status);
        Assert.Equal(EcsErrorCodes.UnknownField, windowRead.Error?.Code);
        Assert.Equal(0, windowWritten);
        Assert.Equal(new byte[LiveWindowFieldSizeBytes], history);
        Assert.Equal(new byte[LiveWindowFieldSizeBytes], window);
    }

    [Fact]
    public void TwoIdenticalSnapshotRestoreRoundtripsAreDeterministic()
    {
        byte[] first = RunRoundtrip(new WorldId(903), new WorldId(913));
        byte[] second = RunRoundtrip(new WorldId(904), new WorldId(914));

        Assert.Equal(first, second);
    }

    [Fact]
    public void ReleasedSnapshotRejectsPersistCaptureWithoutPublishingMaterial()
    {
        using SourceWorld source = CreatePopulatedSource(new WorldId(905));
        Assert.Equal(StorageOperationStatus.Accepted, source.Storage.ReleaseReadSnapshot(source.Snapshot).Status);

        StorageOperationResult captured = EcsPersistSnapshotPipeline.Capture(
            source.Storage,
            source.Snapshot,
            SchemaEpoch,
            PersistSchema(),
            MaxEntities,
            out EcsPersistSnapshotMaterial? material);

        Assert.Equal(StorageOperationStatus.Rejected, captured.Status);
        Assert.Equal(EcsErrorCodes.SnapshotReleased, captured.Error?.Code);
        Assert.Null(material);
    }

    [Fact]
    public void MalformedPersistSnapshotIsRejectedAndDestinationIsUnchanged()
    {
        using var destination = new ReferenceWorldStorageAdapter(new WorldId(915), MaxEntities, 4096);
        RegisterChatSchema(destination);
        var malformed = new EcsPersistSnapshotMaterial(
            SchemaEpoch,
            new[]
            {
                new EcsPersistEntityRecord(
                    default,
                    new[]
                    {
                        new EcsPersistFieldRecord(ChatComponentType, LastMessageTickField, EncodeTick(7))
                    })
            });

        StorageOperationResult restored = EcsPersistSnapshotPipeline.Restore(
            destination,
            malformed,
            SchemaEpoch,
            MaxEntities);

        Assert.Equal(StorageOperationStatus.Rejected, restored.Status);
        Assert.Equal(EcsErrorCodes.InvalidArgument, restored.Error?.Code);
        Assert.Equal(0, destination.EntityCount);
    }

    [Fact]
    public void IncompatibleSchemaEpochIsRejected()
    {
        using SourceWorld source = CreatePopulatedSource(new WorldId(906));
        Assert.Equal(StorageOperationStatus.Accepted, EcsPersistSnapshotPipeline.Capture(
            source.Storage,
            source.Snapshot,
            SchemaEpoch,
            PersistSchema(),
            MaxEntities,
            out EcsPersistSnapshotMaterial? material).Status);
        using var destination = new ReferenceWorldStorageAdapter(new WorldId(916), MaxEntities, 4096);
        RegisterChatSchema(destination);

        StorageOperationResult restored = EcsPersistSnapshotPipeline.Restore(
            destination,
            material!,
            SchemaEpoch + 1UL,
            MaxEntities);

        Assert.Equal(StorageOperationStatus.Rejected, restored.Status);
        Assert.Equal(EcsErrorCodes.InvalidType, restored.Error?.Code);
        Assert.Equal(0, destination.EntityCount);
    }

    [Fact]
    public void RestoreDoesNotResurrectDestroyedEntityIntoReplacement()
    {
        using SourceWorld source = CreatePopulatedSource(new WorldId(907));
        Assert.Equal(StorageOperationStatus.Accepted, EcsPersistSnapshotPipeline.Capture(
            source.Storage,
            source.Snapshot,
            SchemaEpoch,
            PersistSchema(),
            MaxEntities,
            out EcsPersistSnapshotMaterial? material).Status);
        using var destination = new ReferenceWorldStorageAdapter(new WorldId(917), MaxEntities, 4096);
        RegisterChatSchema(destination);
        var replacement = new LocalEntityId(source.Entity.Index, source.Entity.Generation + 1U);
        Assert.Equal(StorageOperationStatus.Accepted, destination.Create(
            replacement,
            ChatOnlyBatch(EncodeText(string.Empty), EncodeTick(0))).Status);

        StorageOperationResult restored = EcsPersistSnapshotPipeline.Restore(
            destination,
            material!,
            SchemaEpoch,
            MaxEntities);

        Assert.Equal(StorageOperationStatus.Rejected, restored.Status);
        Assert.Equal(EcsErrorCodes.StaleEntity, restored.Error?.Code);
        AssertLastMessage(destination, replacement, string.Empty, 0UL);
        var resurrected = new byte[LastMessageTickSizeBytes];
        StorageOperationResult readDestroyed = destination.ReadField(
            source.Entity,
            ChatComponentType,
            LastMessageTickField,
            resurrected,
            out int written);
        Assert.Equal(StorageOperationStatus.Rejected, readDestroyed.Status);
        Assert.Equal(EcsErrorCodes.StaleEntity, readDestroyed.Error?.Code);
        Assert.Equal(0, written);
    }

    [Fact]
    public void WorldReadSnapshotPersistCaptureUsesExistingSnapshotPipeline()
    {
        using var module = new EcsModule();
        EcsWorld world = NewRunningChatWorld(module, 920, out LocalEntityId entity);
        var provider = new EcsWorldSnapshotProvider(world);
        var cut = new EcsSnapshotCutView(31, 9, 5, SchemaEpoch);

        EcsSnapshotCaptureResult captured = provider.Capture(in cut);
        Assert.Equal(StorageOperationStatus.Accepted, captured.Status);
        EcsWorldReadSnapshot snapshot = Assert.IsType<EcsWorldReadSnapshot>(captured.Snapshot);

        StorageOperationResult persist = EcsPersistSnapshotPipeline.Capture(
            snapshot,
            PersistSchema(),
            MaxEntities,
            out EcsPersistSnapshotMaterial? material);

        Assert.Equal(StorageOperationStatus.Accepted, persist.Status);
        Assert.NotNull(material);
        Assert.Equal(SchemaEpoch, material.SchemaEpoch);
        EcsPersistEntityRecord record = Assert.Single(material.Entities);
        Assert.Equal(entity, record.Entity);
        Assert.Equal(EncodeText("hello"), record.Fields[0].CanonicalValue.ToArray());
        Assert.Equal(EncodeTick(11), record.Fields[1].CanonicalValue.ToArray());
        Assert.DoesNotContain(record.Fields, static field => field.ComponentType == ChatLiveWindowType);
    }

    private static byte[] RunRoundtrip(WorldId sourceId, WorldId destinationId)
    {
        using SourceWorld source = CreatePopulatedSource(sourceId);
        Assert.Equal(StorageOperationStatus.Accepted, EcsPersistSnapshotPipeline.Capture(
            source.Storage,
            source.Snapshot,
            SchemaEpoch,
            PersistSchema(),
            MaxEntities,
            out EcsPersistSnapshotMaterial? material).Status);
        using var destination = new ReferenceWorldStorageAdapter(destinationId, MaxEntities, 4096);
        RegisterChatSchema(destination);
        Assert.Equal(StorageOperationStatus.Accepted, EcsPersistSnapshotPipeline.Restore(
            destination,
            material!,
            SchemaEpoch,
            MaxEntities).Status);

        var text = new byte[LastMessageTextSizeBytes];
        var tick = new byte[LastMessageTickSizeBytes];
        Assert.Equal(StorageOperationStatus.Accepted, destination.ReadField(
            source.Entity,
            ChatComponentType,
            LastMessageTextField,
            text,
            out int textWritten).Status);
        Assert.Equal(StorageOperationStatus.Accepted, destination.ReadField(
            source.Entity,
            ChatComponentType,
            LastMessageTickField,
            tick,
            out int tickWritten).Status);
        Assert.Equal(LastMessageTextSizeBytes, textWritten);
        Assert.Equal(LastMessageTickSizeBytes, tickWritten);

        var canonical = new byte[text.Length + tick.Length];
        text.CopyTo(canonical, 0);
        tick.CopyTo(canonical, text.Length);
        return canonical;
    }

    private static SourceWorld CreatePopulatedSource(WorldId worldId)
    {
        var storage = new ReferenceWorldStorageAdapter(worldId, MaxEntities, 4096);
        RegisterChatSchema(storage);
        var entity = new LocalEntityId(1, 1);
        var values = new[]
        {
            new ComponentInitValue(ChatComponentType, LastMessageTextField, EncodeText("gg")),
            new ComponentInitValue(ChatComponentType, LastMessageTickField, EncodeTick(7)),
            new ComponentInitValue(ChatLiveWindowType, ChatEventHistoryField, EncodeAscii32("event-1")),
            new ComponentInitValue(ChatLiveWindowType, DisplayedWindowField, EncodeAscii32("window-1"))
        };
        Assert.Equal(StorageOperationStatus.Accepted, storage.Create(entity, new ComponentInitBatch(values)).Status);
        var context = new StorageSnapshotContext(worldId, new SnapshotId(1), new Revision(1));
        Assert.Equal(StorageOperationStatus.Accepted, storage.CaptureReadSnapshot(in context, out StorageReadSnapshotHandle snapshot).Status);
        return new SourceWorld(storage, entity, snapshot);
    }

    private static EcsWorld NewRunningChatWorld(EcsModule module, ulong id, out LocalEntityId entity)
    {
        var request = new EcsWorldCreateRequest(new WorldId(id), new EcsBudget(4, 32, 32, 4096));
        EcsWorld world = Assert.IsType<EcsWorld>(module.CreateWorld(in request).World);
        Assert.Equal(StorageOperationStatus.Accepted, world.BeginRegistration().Status);
        ComponentTypeRegistrationResult chat = EcsTestRegistration.Register(world, ChatComponentDefinition());
        ComponentTypeRegistrationResult live = EcsTestRegistration.Register(world, ChatLiveWindowDefinition());
        Assert.True(chat.Registered);
        Assert.True(live.Registered);
        EntityTypeRegistrationResult entityType = world.RegisterEntityType(
            new EntityTypeDefinition("Chatter", new[] { chat.Handle, live.Handle }));
        Assert.True(entityType.Registered);
        Assert.Equal(StorageOperationStatus.Accepted, world.MarkReady().Status);
        Assert.Equal(StorageOperationStatus.Accepted, world.Start().Status);
        EntityCreateResult created = world.CreateEntityForCommit(
            world.Context,
            new EntityCreateRequest(
                entityType.Handle,
                new ComponentInitBatch(new[]
                {
                    new ComponentInitValue(ChatComponentType, LastMessageTextField, EncodeText("hello")),
                    new ComponentInitValue(ChatComponentType, LastMessageTickField, EncodeTick(11)),
                    new ComponentInitValue(ChatLiveWindowType, ChatEventHistoryField, EncodeAscii32("event-9")),
                    new ComponentInitValue(ChatLiveWindowType, DisplayedWindowField, EncodeAscii32("window-9"))
                })));
        Assert.True(created.Created);
        entity = created.Entity;
        return world;
    }

    private static void RegisterChatSchema(ReferenceWorldStorageAdapter storage)
    {
        Assert.Equal(StorageOperationStatus.Accepted, storage.Register(ChatComponentDefinition()).Status);
        Assert.Equal(StorageOperationStatus.Accepted, storage.Register(ChatLiveWindowDefinition()).Status);
    }

    private static ComponentTypeDefinition[] PersistSchema() =>
        new[] { ChatComponentDefinition(), ChatLiveWindowDefinition() };

    private static ComponentTypeDefinition ChatComponentDefinition() => new(
        ChatComponentType,
        "ChatComponent",
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

    private static ComponentTypeDefinition ChatLiveWindowDefinition() => new(
        ChatLiveWindowType,
        "ChatLiveWindow",
        new[]
        {
            new ComponentFieldDefinition(ChatEventHistoryField, LiveWindowFieldSizeBytes),
            new ComponentFieldDefinition(DisplayedWindowField, LiveWindowFieldSizeBytes)
        });

    private static ComponentInitBatch ChatOnlyBatch(byte[] text, byte[] tick) => new(new[]
    {
        new ComponentInitValue(ChatComponentType, LastMessageTextField, text),
        new ComponentInitValue(ChatComponentType, LastMessageTickField, tick)
    });

    private static void AssertLastMessage(
        ReferenceWorldStorageAdapter storage,
        LocalEntityId entity,
        string text,
        ulong tick)
    {
        var textBytes = new byte[LastMessageTextSizeBytes];
        var tickBytes = new byte[LastMessageTickSizeBytes];
        Assert.Equal(StorageOperationStatus.Accepted, storage.ReadField(
            entity,
            ChatComponentType,
            LastMessageTextField,
            textBytes,
            out int textWritten).Status);
        Assert.Equal(StorageOperationStatus.Accepted, storage.ReadField(
            entity,
            ChatComponentType,
            LastMessageTickField,
            tickBytes,
            out int tickWritten).Status);
        Assert.Equal(LastMessageTextSizeBytes, textWritten);
        Assert.Equal(LastMessageTickSizeBytes, tickWritten);
        Assert.Equal(EncodeText(text), textBytes);
        Assert.Equal(EncodeTick(tick), tickBytes);
    }

    private static byte[] EncodeText(string text)
    {
        byte[] utf8 = Encoding.UTF8.GetBytes(text);
        if (utf8.Length > LastMessageTextMaxUtf8Bytes)
            throw new ArgumentOutOfRangeException(nameof(text));
        var field = new byte[LastMessageTextSizeBytes];
        BinaryPrimitives.WriteUInt32LittleEndian(field, (uint)utf8.Length);
        utf8.CopyTo(field.AsSpan(4));
        return field;
    }

    private static byte[] EncodeTick(ulong tick)
    {
        var field = new byte[LastMessageTickSizeBytes];
        BinaryPrimitives.WriteUInt64LittleEndian(field, tick);
        return field;
    }

    private static byte[] EncodeAscii32(string value)
    {
        byte[] utf8 = Encoding.UTF8.GetBytes(value);
        if (utf8.Length > LiveWindowFieldSizeBytes)
            throw new ArgumentOutOfRangeException(nameof(value));
        var field = new byte[LiveWindowFieldSizeBytes];
        utf8.CopyTo(field, 0);
        return field;
    }

    private sealed class SourceWorld : IDisposable
    {
        public SourceWorld(
            ReferenceWorldStorageAdapter storage,
            LocalEntityId entity,
            StorageReadSnapshotHandle snapshot)
        {
            Storage = storage;
            Entity = entity;
            Snapshot = snapshot;
        }

        public ReferenceWorldStorageAdapter Storage { get; }

        public LocalEntityId Entity { get; }

        public StorageReadSnapshotHandle Snapshot { get; }

        public void Dispose() => Storage.Dispose();
    }
}
