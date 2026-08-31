using System;
using System.Collections.Generic;
using System.Linq;
using Lumio.GameRuntime.Ecs;
using Xunit;

namespace Lumio.GameRuntime.Ecs.Tests;

public sealed class StorageAdapterConformanceTests
{
    [Fact]
    public void RegisterCreateReadWriteQueryDestroyAndIntegrityHaveStableResults()
    {
        using var storage = new ReferenceWorldStorageAdapter(new WorldId(500), 4);
        ComponentTypeDefinition component = PositionComponent();
        Assert.Equal(StorageOperationStatus.Accepted, Register(storage, component).Status);

        LocalEntityId first = new(1, 1);
        LocalEntityId second = new(2, 1);
        ComponentInitBatch initial = new(new[]
        {
            new ComponentInitValue(
                component.Id,
                new ComponentFieldId(1),
                new byte[] { 1, 2, 3, 4 })
        });
        Assert.Equal(StorageOperationStatus.Accepted, storage.Create(first, in initial).Status);
        Assert.Equal(StorageOperationStatus.Accepted, storage.Create(second, in initial).Status);

        var destination = new byte[4];
        StorageOperationResult read = storage.ReadField(
            first, component.Id, new ComponentFieldId(1), destination, out int written);
        Assert.Equal(StorageOperationStatus.Accepted, read.Status);
        Assert.Equal(4, written);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, destination);

        Assert.Equal(StorageOperationStatus.Accepted, storage.WriteExistingField(
            first, component.Id, new ComponentFieldId(1), new byte[] { 4, 3, 2, 1 }).Status);
        Assert.Equal(StorageOperationStatus.Accepted, storage.ReadField(
            first, component.Id, new ComponentFieldId(1), destination, out _).Status);
        Assert.Equal(new byte[] { 4, 3, 2, 1 }, destination);

        QuerySpec query = new(
            new[] { component.Id },
            Array.Empty<ComponentTypeId>(),
            new[] { new ComponentFieldId(1) },
            Array.Empty<ComponentFieldId>());
        Assert.Equal(StorageOperationStatus.Accepted, storage.CompileQuery(in query, out StorageQueryHandle handle).Status);
        var ids = new LocalEntityId[2];
        Assert.Equal(StorageOperationStatus.Accepted, storage.EnumerateOrdered(handle, ids, out int count).Status);
        Assert.Equal(2, count);
        Assert.Equal(new[] { first, second }, ids.Take(count).ToArray());

        Assert.Equal(StorageOperationStatus.Accepted, storage.Destroy(first).Status);
        Assert.Equal(StorageOperationStatus.Rejected, storage.Destroy(first).Status);
        Assert.Equal("InvalidHandle", storage.LastError?.Code);
        Assert.Equal(StorageOperationStatus.Accepted, storage.ValidateIntegrity().Status);
    }

    [Fact]
    public void ComponentRegistrationRejectsDuplicatesWithoutBlockingOtherTypes()
    {
        using var storage = new ReferenceWorldStorageAdapter(new WorldId(501), 4);
        ComponentTypeDefinition first = PositionComponent();
        ComponentTypeDefinition second = new(
            new ComponentTypeId(11),
            "Velocity",
            new[] { new ComponentFieldDefinition(new ComponentFieldId(1), 4) });
        Assert.Equal(StorageOperationStatus.Accepted, storage.Register(first).Status);
        Assert.Equal(StorageOperationStatus.Accepted, storage.Register(second).Status);
        Assert.Equal(StorageOperationStatus.Rejected, storage.Register(first).Status);
        Assert.Equal(EcsErrorCodes.DuplicateRegistration, storage.LastError?.Code);
        Assert.Equal(StorageOperationStatus.Accepted, Register(storage, new ComponentTypeDefinition(
            new ComponentTypeId(12), "Health", Array.Empty<ComponentFieldDefinition>())).Status);
    }

    [Fact]
    public void UnknownFieldsCapacityAndSnapshotReleaseAreExplicitlyRejected()
    {
        using var storage = new ReferenceWorldStorageAdapter(new WorldId(502), 1);
        ComponentTypeDefinition component = PositionComponent();
        Assert.Equal(StorageOperationStatus.Accepted, Register(storage, component).Status);
        LocalEntityId entity = new(1, 1);
        Assert.Equal(StorageOperationStatus.Accepted, storage.Create(entity, ComponentInitBatch.Empty).Status);
        Assert.Equal(StorageOperationStatus.Rejected, storage.Create(new LocalEntityId(2, 1), ComponentInitBatch.Empty).Status);
        Assert.Equal(EcsErrorCodes.CapacityExceeded, storage.LastError?.Code);

        Assert.Equal(StorageOperationStatus.Rejected, storage.ReadField(
            entity, component.Id, new ComponentFieldId(99), new byte[4], out _).Status);
        Assert.Equal(EcsErrorCodes.UnknownField, storage.LastError?.Code);

        var snapshotContext = new StorageSnapshotContext(
            new WorldId(502),
            new SnapshotId(1),
            new Revision(1));
        Assert.Equal(StorageOperationStatus.Accepted,
            storage.CaptureReadSnapshot(in snapshotContext, out StorageReadSnapshotHandle handle).Status);
        Assert.Equal(StorageOperationStatus.Accepted, storage.ReleaseReadSnapshot(handle).Status);
        Assert.Equal(StorageOperationStatus.Rejected, storage.ReleaseReadSnapshot(handle).Status);
        Assert.Equal("HandleDoubleRelease", storage.LastError?.Code);

        storage.Dispose();
        Assert.Equal(StorageOperationStatus.Rejected, storage.ValidateIntegrity().Status);
        Assert.Equal(EcsErrorCodes.WorldDisposed, storage.LastError?.Code);
    }

    [Fact]
    public void QueryBudgetDoesNotReturnPartialResults()
    {
        using var storage = new ReferenceWorldStorageAdapter(new WorldId(503), 4);
        ComponentTypeDefinition component = PositionComponent();
        Register(storage, component);
        ComponentInitBatch initial = new(new[]
        {
            new ComponentInitValue(
                component.Id,
                new ComponentFieldId(1),
                new byte[] { 0, 0, 0, 0 })
        });
        storage.Create(new LocalEntityId(1, 1), in initial);
        storage.Create(new LocalEntityId(2, 1), in initial);
        QuerySpec query = new(
            new[] { component.Id },
            Array.Empty<ComponentTypeId>(),
            Array.Empty<ComponentFieldId>(),
            Array.Empty<ComponentFieldId>());
        storage.CompileQuery(in query, out StorageQueryHandle handle);
        var destination = new LocalEntityId[1];

        StorageOperationResult result = storage.EnumerateOrdered(handle, destination, out int written);

        Assert.Equal(StorageOperationStatus.Rejected, result.Status);
        Assert.Equal(EcsErrorCodes.BudgetExceeded, result.Error?.Code);
        Assert.Equal(0, written);
    }

    [Fact]
    public void WriteExistingFieldDoesNotCreateMissingComponent()
    {
        using var storage = new ReferenceWorldStorageAdapter(new WorldId(504), 4);
        ComponentTypeDefinition component = PositionComponent();
        Register(storage, component);
        LocalEntityId entity = new(1, 1);
        Assert.Equal(StorageOperationStatus.Accepted, storage.Create(entity, ComponentInitBatch.Empty).Status);

        StorageOperationResult result = storage.WriteExistingField(
            entity,
            component.Id,
            new ComponentFieldId(1),
            new byte[] { 1, 2, 3, 4 });

        Assert.Equal(StorageOperationStatus.Rejected, result.Status);
        Assert.Equal(EcsErrorCodes.UnknownField, result.Error?.Code);
        QuerySpec query = new(
            new[] { component.Id },
            Array.Empty<ComponentTypeId>(),
            Array.Empty<ComponentFieldId>(),
            Array.Empty<ComponentFieldId>());
        Assert.Equal(StorageOperationStatus.Accepted, storage.CompileQuery(in query, out StorageQueryHandle handle).Status);
        var ids = new LocalEntityId[1];
        Assert.Equal(StorageOperationStatus.Accepted, storage.EnumerateOrdered(handle, ids, out int count).Status);
        Assert.Equal(0, count);
    }

    [Fact]
    public void CompileQueryRejectsUnknownTypesAndFields()
    {
        using var storage = new ReferenceWorldStorageAdapter(new WorldId(505), 4);
        ComponentTypeDefinition component = PositionComponent();
        Register(storage, component);

        QuerySpec unknownType = new(
            new[] { new ComponentTypeId(99) },
            Array.Empty<ComponentTypeId>(),
            Array.Empty<ComponentFieldId>(),
            Array.Empty<ComponentFieldId>());
        StorageOperationResult unknownTypeResult = storage.CompileQuery(in unknownType, out _);
        Assert.Equal(StorageOperationStatus.Rejected, unknownTypeResult.Status);
        Assert.Equal(EcsErrorCodes.UnknownComponent, unknownTypeResult.Error?.Code);

        QuerySpec unknownField = new(
            new[] { component.Id },
            Array.Empty<ComponentTypeId>(),
            new[] { new ComponentFieldId(99) },
            Array.Empty<ComponentFieldId>());
        StorageOperationResult unknownFieldResult = storage.CompileQuery(in unknownField, out _);
        Assert.Equal(StorageOperationStatus.Rejected, unknownFieldResult.Status);
        Assert.Equal(EcsErrorCodes.UnknownField, unknownFieldResult.Error?.Code);
    }

    [Fact]
    public void CompiledQueryOwnsItsSpecificationMemory()
    {
        using var storage = new ReferenceWorldStorageAdapter(new WorldId(506), 4);
        ComponentTypeDefinition component = PositionComponent();
        Register(storage, component);
        ComponentInitBatch initial = new(new[]
        {
            new ComponentInitValue(component.Id, new ComponentFieldId(1), new byte[] { 0, 0, 0, 0 })
        });
        storage.Create(new LocalEntityId(1, 1), in initial);

        var required = new[] { component.Id };
        QuerySpec query = new(
            required,
            Array.Empty<ComponentTypeId>(),
            Array.Empty<ComponentFieldId>(),
            Array.Empty<ComponentFieldId>());
        Assert.Equal(StorageOperationStatus.Accepted, storage.CompileQuery(in query, out StorageQueryHandle handle).Status);
        required[0] = new ComponentTypeId(99);

        var ids = new LocalEntityId[1];
        Assert.Equal(StorageOperationStatus.Accepted, storage.EnumerateOrdered(handle, ids, out int count).Status);
        Assert.Equal(1, count);
    }

    [Fact]
    public void ComponentDefinitionOwnsItsFieldDeclarationMemory()
    {
        ComponentTypeDefinition position = PositionComponent();
        var replacement = new ComponentFieldDefinition(new ComponentFieldId(2), 8);
        var supplied = new[] { new ComponentFieldDefinition(new ComponentFieldId(1), 4), replacement };
        var component = new ComponentTypeDefinition(new ComponentTypeId(11), "Velocity", supplied);

        supplied[0] = replacement;

        Assert.Equal(new ComponentFieldId(1), component.Fields.Span[0].Id);
    }

    private static ComponentTypeDefinition PositionComponent() => new(
        new ComponentTypeId(10),
        "Position",
        new[] { new ComponentFieldDefinition(new ComponentFieldId(1), 4) });

    private static StorageOperationResult Register(
        ReferenceWorldStorageAdapter storage,
        ComponentTypeDefinition component)
    {
        return storage.Register(component);
    }
}
