using System;
using System.Reflection;
using Lumio.GameRuntime.Ecs;
using Xunit;

namespace Lumio.GameRuntime.Ecs.Tests;

public sealed class SnapshotContractTests
{
    [Fact]
    public void SnapshotContractCarriesWorldAndRevisionContextAndSupportsDataReads()
    {
        Assembly assembly = typeof(EcsWorld).Assembly;
        Type contextType = Assert.Single(
            assembly.GetTypes(),
            static type => type.Name == "StorageSnapshotContext");
        MethodInfo capture = Assert.Single(
            typeof(IWorldStorageAdapter).GetMethods(),
            static method => method.Name == "CaptureReadSnapshot");
        MethodInfo read = Assert.Single(
            typeof(IWorldStorageAdapter).GetMethods(),
            static method => method.Name == "ReadSnapshotField");
        PropertyInfo context = Assert.Single(
            typeof(StorageReadSnapshotHandle).GetProperties(),
            static property => property.Name == "Context");

        Assert.Equal(2, capture.GetParameters().Length);
        Assert.Equal(contextType.MakeByRefType(), capture.GetParameters()[0].ParameterType);
        Assert.Equal(contextType, context.PropertyType);
        Assert.Equal(typeof(StorageReadSnapshotHandle), read.GetParameters()[0].ParameterType);
    }

    [Fact]
    public void CapturedSnapshotFreezesComponentBytesAndDoesNotExposeBackingBuffers()
    {
        var worldId = new WorldId(600);
        using var storage = new ReferenceWorldStorageAdapter(worldId, 4, 4096);
        ComponentTypeDefinition component = PositionComponent();
        Assert.Equal(StorageOperationStatus.Accepted, storage.Register(component).Status);
        LocalEntityId entity = new(1, 1);
        var initial = new ComponentInitBatch(new[]
        {
            new ComponentInitValue(
                component.Id,
                new ComponentFieldId(1),
                new byte[] { 1, 2, 3, 4 })
        });
        Assert.Equal(StorageOperationStatus.Accepted, storage.Create(entity, in initial).Status);
        var context = new StorageSnapshotContext(worldId, new SnapshotId(7), new Revision(11));
        Assert.Equal(StorageOperationStatus.Accepted,
            storage.CaptureReadSnapshot(in context, out StorageReadSnapshotHandle snapshot).Status);
        Assert.Equal(context, snapshot.Context);

        Assert.Equal(StorageOperationStatus.Accepted, storage.WriteExistingField(
            entity,
            component.Id,
            new ComponentFieldId(1),
            new byte[] { 9, 8, 7, 6 }).Status);
        var firstRead = new byte[4];
        StorageOperationResult first = storage.ReadSnapshotField(
            snapshot,
            entity,
            component.Id,
            new ComponentFieldId(1),
            firstRead,
            out int firstWritten);
        firstRead[0] = 42;
        var secondRead = new byte[4];
        StorageOperationResult second = storage.ReadSnapshotField(
            snapshot,
            entity,
            component.Id,
            new ComponentFieldId(1),
            secondRead,
            out int secondWritten);
        var liveRead = new byte[4];
        Assert.Equal(StorageOperationStatus.Accepted, storage.ReadField(
            entity,
            component.Id,
            new ComponentFieldId(1),
            liveRead,
            out int liveWritten).Status);

        Assert.Equal(StorageOperationStatus.Accepted, first.Status);
        Assert.Equal(4, firstWritten);
        Assert.Equal(StorageOperationStatus.Accepted, second.Status);
        Assert.Equal(4, secondWritten);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, secondRead);
        Assert.Equal(4, liveWritten);
        Assert.Equal(new byte[] { 9, 8, 7, 6 }, liveRead);
    }

    [Fact]
    public void ReleasedSnapshotRejectsReadsWithoutPublishingBytes()
    {
        var worldId = new WorldId(601);
        using var storage = new ReferenceWorldStorageAdapter(worldId, 4, 4096);
        ComponentTypeDefinition component = PositionComponent();
        Assert.Equal(StorageOperationStatus.Accepted, storage.Register(component).Status);
        LocalEntityId entity = new(1, 1);
        var initial = new ComponentInitBatch(new[]
        {
            new ComponentInitValue(
                component.Id,
                new ComponentFieldId(1),
                new byte[] { 1, 2, 3, 4 })
        });
        Assert.Equal(StorageOperationStatus.Accepted, storage.Create(entity, in initial).Status);
        var context = new StorageSnapshotContext(worldId, new SnapshotId(8), new Revision(12));
        Assert.Equal(StorageOperationStatus.Accepted,
            storage.CaptureReadSnapshot(in context, out StorageReadSnapshotHandle snapshot).Status);
        Assert.Equal(StorageOperationStatus.Accepted, storage.ReleaseReadSnapshot(snapshot).Status);
        var destination = new byte[] { 5, 5, 5, 5 };

        StorageOperationResult read = storage.ReadSnapshotField(
            snapshot,
            entity,
            component.Id,
            new ComponentFieldId(1),
            destination,
            out int written);

        Assert.Equal(StorageOperationStatus.Rejected, read.Status);
        Assert.Equal("InvalidHandle", read.Error?.Code);
        Assert.Equal(0, written);
        Assert.Equal(new byte[] { 5, 5, 5, 5 }, destination);
    }

    [Fact]
    public void SnapshotCaptureRejectsMismatchedWorldContext()
    {
        using var storage = new ReferenceWorldStorageAdapter(new WorldId(602), 4, 4096);
        var wrongContext = new StorageSnapshotContext(
            new WorldId(603),
            new SnapshotId(9),
            new Revision(13));

        StorageOperationResult capture = storage.CaptureReadSnapshot(
            in wrongContext,
            out StorageReadSnapshotHandle snapshot);

        Assert.Equal(StorageOperationStatus.Rejected, capture.Status);
        Assert.Equal(EcsErrorCodes.CrossWorld, capture.Error?.Code);
        Assert.True(snapshot.IsDefault);
    }

    [Fact]
    public void FaultedWorldRejectsSnapshotCaptureAndPublication()
    {
        using var module = new EcsModule();
        var request = new EcsWorldCreateRequest(
            new WorldId(604),
            new EcsBudget(4, 32, 32, 4096));
        EcsWorld world = Assert.IsType<EcsWorld>(module.CreateWorld(in request).World);
        Assert.Equal(StorageOperationStatus.Accepted, world.BeginRegistration().Status);
        Assert.Equal(StorageOperationStatus.Accepted, world.MarkReady().Status);
        Assert.Equal(StorageOperationStatus.Accepted, world.Start().Status);
        Assert.Equal(StorageOperationStatus.Accepted,
            world.CaptureReadSnapshot(new SnapshotId(10), new Revision(14), out StorageReadSnapshotHandle snapshot).Status);
        Assert.Equal(StorageOperationStatus.Accepted,
            world.Fault(new ErrorIdentity(EcsErrorCodes.InvalidState)).Status);
        var entities = new LocalEntityId[1];

        StorageOperationResult read = world.EnumerateSnapshotEntities(snapshot, entities, out int written);
        StorageOperationResult capture = world.CaptureReadSnapshot(
            new SnapshotId(11),
            new Revision(15),
            out StorageReadSnapshotHandle later);

        Assert.Equal(StorageOperationStatus.Rejected, read.Status);
        Assert.Equal(EcsErrorCodes.WorldFaulted, read.Error?.Code);
        Assert.Equal(0, written);
        Assert.Equal(default, entities[0]);
        Assert.Equal(StorageOperationStatus.Rejected, capture.Status);
        Assert.Equal(EcsErrorCodes.WorldFaulted, capture.Error?.Code);
        Assert.True(later.IsDefault);
    }

    private static ComponentTypeDefinition PositionComponent() => new(
        new ComponentTypeId(10),
        "Position",
        new[] { new ComponentFieldDefinition(new ComponentFieldId(1), 4) });
}
