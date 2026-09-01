using System;
using System.Collections.Generic;
using System.Reflection;
using Lumio.GameRuntime.Ecs;
using Xunit;

namespace Lumio.GameRuntime.Ecs.Tests;

public sealed class ChangeSetGoldenTests
{
    [Theory]
    [MemberData(nameof(Permutations))]
    public void ChangeSetIsCanonicalForAllInsertionOrders(object changes)
    {
        var set = new ChangeSet(Fixtures.WorldId, Fixtures.Tick, Assert.IsType<ChangeEntry[]>(changes));

        Assert.Equal(Fixtures.ExpectedChangeOrder, set.Entries.ToArray());
        Assert.Equal(Fixtures.ExpectedChangeHash, CanonicalHash.Of(set));
        Assert.Equal(Fixtures.ExpectedCanonicalBytes, set.CanonicalBytes.ToArray());
    }

    [Fact]
    public void PublishedChangeSetDoesNotReflectLaterMutationOfInput()
    {
        ChangeEntry[] changes = (ChangeEntry[])Fixtures.ThreeChanges.Clone();
        byte[] after = changes[0].CanonicalAfter.ToArray();
        var set = new ChangeSet(Fixtures.WorldId, Fixtures.Tick, changes);
        after[0] = 255;
        changes[0] = new ChangeEntry(
            new LocalEntityId(9, 9),
            new ComponentTypeId(99),
            new ComponentFieldId(99),
            new byte[] { 0 },
            after);

        Assert.Equal(Fixtures.ExpectedChangeOrder, set.Entries.ToArray());
        Assert.Equal(Fixtures.ExpectedChangeHash, CanonicalHash.Of(set));
        Assert.Equal(4, set.Entries.Span[2].CanonicalAfter.Span[0]);
        Assert.Equal(Fixtures.ThreeChanges[0], set.Entries.Span[2]);
    }

    [Fact]
    public void SnapshotCapturePinsAdapterHandleAndDisposeIsIdempotent()
    {
        using var module = new EcsModule();
        EcsWorld world = NewRunningWorldWithPosition(module, 820, out LocalEntityId entity);
        var provider = new EcsWorldSnapshotProvider(world);
        var cut = new EcsSnapshotCutView(21, 7, 3, 1);

        EcsSnapshotCaptureResult captured = provider.Capture(in cut);
        Assert.Equal(StorageOperationStatus.Accepted, captured.Status);
        EcsWorldReadSnapshot snapshot = Assert.IsType<EcsWorldReadSnapshot>(captured.Snapshot);
        Assert.Equal(world.WorldId, snapshot.WorldId);
        Assert.Equal(new TickId(7), snapshot.TickId);
        Assert.Equal(new Revision(3), snapshot.Revision);
        Assert.Equal(1UL, snapshot.SchemaEpoch);

        var first = new byte[4];
        Assert.Equal(StorageOperationStatus.Accepted,
            snapshot.ReadField(entity, new ComponentTypeId(10), new ComponentFieldId(1), first, out int written).Status);
        Assert.Equal(4, written);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, first);

        FieldInfo storageField = typeof(EcsWorld).GetField(
            "_storage",
            BindingFlags.Instance | BindingFlags.NonPublic) ??
            throw new InvalidOperationException("World storage field is missing.");
        var storage = Assert.IsType<ReferenceWorldStorageAdapter>(storageField.GetValue(world));
        Assert.Equal(StorageOperationStatus.Accepted, storage.WriteExistingField(
            entity,
            new ComponentTypeId(10),
            new ComponentFieldId(1),
            new byte[] { 9, 8, 7, 6 }).Status);

        var frozen = new byte[4];
        Assert.Equal(StorageOperationStatus.Accepted,
            snapshot.ReadField(entity, new ComponentTypeId(10), new ComponentFieldId(1), frozen, out _).Status);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, frozen);

        snapshot.Dispose();
        snapshot.Dispose();
        var destination = new byte[] { 5, 5, 5, 5 };
        StorageOperationResult stale = snapshot.ReadField(
            entity,
            new ComponentTypeId(10),
            new ComponentFieldId(1),
            destination,
            out int staleWritten);

        Assert.Equal(StorageOperationStatus.Rejected, stale.Status);
        Assert.Equal(EcsErrorCodes.SnapshotReleased, stale.Error?.Code);
        Assert.Equal(0, staleWritten);
        Assert.Equal(new byte[] { 5, 5, 5, 5 }, destination);
    }

    [Fact]
    public void SnapshotBudgetExceededIsRetryableBeforePublishingALease()
    {
        using var module = new EcsModule();
        var request = new EcsWorldCreateRequest(new WorldId(821), new EcsBudget(4, 32, 32, 2));
        EcsWorld world = Assert.IsType<EcsWorld>(module.CreateWorld(in request).World);
        Assert.Equal(StorageOperationStatus.Accepted, world.BeginRegistration().Status);
        ComponentTypeRegistrationResult component = EcsTestRegistration.Register(world, new ComponentTypeDefinition(
            new ComponentTypeId(10),
            "Position",
            new[] { new ComponentFieldDefinition(new ComponentFieldId(1), 4) }));
        Assert.True(component.Registered);
        EntityTypeRegistrationResult entityType = world.RegisterEntityType(
            new EntityTypeDefinition("Posed", new[] { component.Handle }));
        Assert.True(entityType.Registered);
        Assert.Equal(StorageOperationStatus.Accepted, world.MarkReady().Status);
        Assert.Equal(StorageOperationStatus.Accepted, world.Start().Status);
        Assert.True(world.CreateEntityForCommit(
            world.Context,
            new EntityCreateRequest(
                entityType.Handle,
                new ComponentInitBatch(new[]
                {
                    new ComponentInitValue(
                        new ComponentTypeId(10),
                        new ComponentFieldId(1),
                        new byte[] { 1, 2, 3, 4 })
                }))).Created);

        var provider = new EcsWorldSnapshotProvider(world);
        var cut = new EcsSnapshotCutView(22, 8, 4, 1);
        EcsSnapshotCaptureResult captured = provider.Capture(in cut);

        Assert.Equal(StorageOperationStatus.Retryable, captured.Status);
        Assert.Null(captured.Snapshot);
        Assert.Equal(EcsErrorCodes.BudgetExceeded, captured.Error?.Code);
    }

    public static IEnumerable<object[]> Permutations()
    {
        ChangeEntry[] items = Fixtures.ThreeChanges;
        yield return new object[] { new[] { items[0], items[1], items[2] } };
        yield return new object[] { new[] { items[0], items[2], items[1] } };
        yield return new object[] { new[] { items[1], items[0], items[2] } };
        yield return new object[] { new[] { items[1], items[2], items[0] } };
        yield return new object[] { new[] { items[2], items[0], items[1] } };
        yield return new object[] { new[] { items[2], items[1], items[0] } };
    }

    private static EcsWorld NewRunningWorldWithPosition(EcsModule module, ulong id, out LocalEntityId entity)
    {
        var request = new EcsWorldCreateRequest(new WorldId(id), new EcsBudget(4, 32, 32, 4096));
        EcsWorld world = Assert.IsType<EcsWorld>(module.CreateWorld(in request).World);
        Assert.Equal(StorageOperationStatus.Accepted, world.BeginRegistration().Status);
        ComponentTypeRegistrationResult component = EcsTestRegistration.Register(world, new ComponentTypeDefinition(
            new ComponentTypeId(10),
            "Position",
            new[] { new ComponentFieldDefinition(new ComponentFieldId(1), 4) }));
        Assert.True(component.Registered);
        EntityTypeRegistrationResult entityType = world.RegisterEntityType(
            new EntityTypeDefinition("Posed", new[] { component.Handle }));
        Assert.True(entityType.Registered);
        Assert.Equal(StorageOperationStatus.Accepted, world.MarkReady().Status);
        Assert.Equal(StorageOperationStatus.Accepted, world.Start().Status);
        EntityCreateResult created = world.CreateEntityForCommit(
            world.Context,
            new EntityCreateRequest(
                entityType.Handle,
                new ComponentInitBatch(new[]
                {
                    new ComponentInitValue(
                        new ComponentTypeId(10),
                        new ComponentFieldId(1),
                        new byte[] { 1, 2, 3, 4 })
                })));
        Assert.True(created.Created);
        entity = created.Entity;
        return world;
    }

    private static class Fixtures
    {
        public static WorldId WorldId { get; } = new(1);
        public static TickId Tick { get; } = 7;

        public static ChangeEntry[] ThreeChanges { get; } =
        {
            new(
                new LocalEntityId(2, 1),
                new ComponentTypeId(10),
                new ComponentFieldId(1),
                new byte[] { 3, 0, 0, 0 },
                new byte[] { 4, 0, 0, 0 }),
            new(
                new LocalEntityId(1, 1),
                new ComponentTypeId(11),
                new ComponentFieldId(2),
                new byte[] { 5, 0, 0, 0 },
                new byte[] { 6, 0, 0, 0 }),
            new(
                new LocalEntityId(1, 1),
                new ComponentTypeId(10),
                new ComponentFieldId(1),
                new byte[] { 1, 0, 0, 0 },
                new byte[] { 2, 0, 0, 0 })
        };

        public static ChangeEntry[] ExpectedChangeOrder { get; } =
        {
            ThreeChanges[2],
            ThreeChanges[1],
            ThreeChanges[0]
        };

        public static byte[] ExpectedCanonicalBytes { get; } = new ChangeSet(WorldId, Tick, ExpectedChangeOrder).CanonicalBytes.ToArray();

        public static string ExpectedChangeHash { get; } =
            "a670c13dfebc32ba933179a71421f0bd2a76783dd5f369563792f9d50b277b35";
    }
}
