using System;
using System.Buffers.Binary;
using System.Globalization;
using Lumio.GameRuntime.Ecs;
using Lumio.GameRuntime.Ecs.Ingress;
using Xunit;

namespace Lumio.GameRuntime.Ecs.Tests;

public sealed class ChatIngressPersistTests
{
    private const int LiveEntityCount = 101;
    private const int LastMessageTickSizeBytes = 8;
    private const int Adr032HeaderBytes = 8 + 8 + 8 + (4 + 64) + (4 + 64);

    [Fact]
    public void CapturePersistOf101LiveChatEntitiesRestoresLastMessageAndOmitsHistory()
    {
        using ChatIngressWorld source = ChatIngressWorld.Create();
        Assert.True(source.World.Budget.MaxEntities >= LiveEntityCount);
        int persistBytesPerEntity = 4 + ChatIngressWorld.LastMessageTextMaxUtf8Bytes + LastMessageTickSizeBytes;
        Assert.Equal(
            persistBytesPerEntity * source.World.Budget.MaxEntities + Adr032HeaderBytes,
            source.World.Budget.MaxSnapshotBytes);
        Assert.True(source.World.Budget.MaxSnapshotBytes >= persistBytesPerEntity * LiveEntityCount + Adr032HeaderBytes);

        var expected = new ExpectedLastMessage[LiveEntityCount];
        for (int i = 0; i < LiveEntityCount; i++)
        {
            string netEntityId = ((ulong)(i + 1)).ToString("x32", CultureInfo.InvariantCulture);
            Assert.DoesNotContain("nent_", netEntityId, StringComparison.Ordinal);
            Assert.True(source.TryCreateEntity(netEntityId, out LocalEntityId entity), netEntityId);
            string text = "m" + i.ToString(CultureInfo.InvariantCulture);
            ulong tick = (ulong)(i + 1);
            Assert.Equal(StorageOperationStatus.Accepted, source.TryWriteLastMessage(entity, text, tick).Status);
            expected[i] = new ExpectedLastMessage(entity, text, tick);
        }

        StorageOperationResult captured = EcsPersistSnapshotPipeline.CapturePersist(source.World, out byte[]? bytes);
        Assert.Equal(StorageOperationStatus.Accepted, captured.Status);
        Assert.NotNull(bytes);
        Assert.NotEmpty(bytes);

        PersistSnapshotTestSchema.DecodedHeader header = PersistSnapshotTestSchema.ReadHeader(bytes);
        Assert.True(header.Payload.Length >= 4);
        uint entityCount = BinaryPrimitives.ReadUInt32LittleEndian(header.Payload);
        Assert.Equal((uint)LiveEntityCount, entityCount);
        PersistSnapshotTestSchema.AssertPayloadOmitsHistory(bytes);
        AssertPersistPayloadHasOnlyLastMessageFields(header.Payload, LiveEntityCount);

        using ChatIngressWorld destination = ChatIngressWorld.Create();
        StorageOperationResult restored = EcsPersistSnapshotPipeline.RestorePersist(destination.World, bytes);
        Assert.Equal(StorageOperationStatus.Accepted, restored.Status);
        Assert.Equal(LiveEntityCount, destination.World.ActiveEntityCount);

        for (int i = 0; i < expected.Length; i++)
        {
            ExpectedLastMessage item = expected[i];
            Assert.True(destination.TryReadLastMessage(item.Entity, out string text, out ulong tick));
            Assert.Equal(item.Text, text);
            Assert.Equal(item.Tick, tick);
        }
    }

    private static void AssertPersistPayloadHasOnlyLastMessageFields(byte[] payload, int expectedEntities)
    {
        int offset = 4;
        for (int entityIndex = 0; entityIndex < expectedEntities; entityIndex++)
        {
            offset += 8;
            uint fieldCount = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(offset, 4));
            offset += 4;
            Assert.Equal(2U, fieldCount);
            for (int fieldIndex = 0; fieldIndex < 2; fieldIndex++)
            {
                ulong componentType = BinaryPrimitives.ReadUInt64LittleEndian(payload.AsSpan(offset, 8));
                offset += 8;
                ulong field = BinaryPrimitives.ReadUInt64LittleEndian(payload.AsSpan(offset, 8));
                offset += 8;
                uint valueLength = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(offset, 4));
                offset += 4 + (int)valueLength;
                Assert.Equal(ChatIngressWorld.ChatComponentType.Value, componentType);
                Assert.True(
                    field == ChatIngressWorld.LastMessageTextField.Value ||
                    field == ChatIngressWorld.LastMessageTickField.Value);
            }
        }

        Assert.Equal(payload.Length, offset);
    }

    private readonly record struct ExpectedLastMessage(LocalEntityId Entity, string Text, ulong Tick);
}
