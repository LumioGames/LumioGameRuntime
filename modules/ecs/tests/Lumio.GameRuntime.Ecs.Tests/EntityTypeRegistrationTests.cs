using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using Lumio.GameRuntime.Ecs;
using Xunit;

namespace Lumio.GameRuntime.Ecs.Tests;

public sealed class EntityTypeRegistrationTests
{
    [Fact]
    public void EntityCreationRejectsAnUnregisteredType()
    {
        using var module = new EcsModule();
        EcsWorld world = NewRegisteringWorld(module, 200);
        Assert.Equal(StorageOperationStatus.Accepted, world.MarkReady().Status);
        Assert.Equal(StorageOperationStatus.Accepted, world.Start().Status);

        EntityCreateResult result = world.CreateEntityForCommit(world.Context,
            new EntityCreateRequest(new EntityTypeHandle(world.WorldId, uint.MaxValue)));

        Assert.False(result.Created);
        Assert.Equal(StorageOperationStatus.Rejected, result.Result.Status);
        Assert.Equal(EcsErrorCodes.InvalidType, result.Error?.Code);
        Assert.Equal(0, world.ActiveEntityCount);
    }

    [Fact]
    public void LocalEntityTypeUsesItsRegisteredDefaultMode()
    {
        using var module = new EcsModule();
        EcsWorld world = NewRegisteringWorld(module, 201);
        var type = new EntityTypeDefinition(
            "LocalOnly",
            Array.Empty<ComponentTypeHandle>(),
            EntityMode.Local);
        EntityTypeRegistrationResult registration = world.RegisterEntityType(type);
        Assert.True(registration.Registered);
        Assert.Equal(StorageOperationStatus.Accepted, world.MarkReady().Status);
        Assert.Equal(StorageOperationStatus.Accepted, world.Start().Status);

        EntityCreateResult result = world.CreateEntityForCommit(world.Context, new EntityCreateRequest(registration.Handle));

        Assert.True(result.Created);
        Assert.Equal(EntityMode.Local, result.Mode);
        Assert.Equal(EntityMode.Local, ReadStoredMode(world, result.Entity));
    }

    [Fact]
    public void EntityTypePreservesDeclarationOrderAndCanonicalMembership()
    {
        var declared = new[]
        {
            new ComponentTypeHandle(new WorldId(202), 30),
            new ComponentTypeHandle(new WorldId(202), 10),
            new ComponentTypeHandle(new WorldId(202), 20)
        };
        var type = new EntityTypeDefinition("Ordered", declared, EntityMode.Local);
        var reordered = new EntityTypeDefinition(
            "Ordered",
            new[] { declared[1], declared[2], declared[0] },
            EntityMode.Local);

        Assert.Equal(declared, type.ComponentTypes.ToArray());
        Assert.Equal(new[] { declared[1], declared[2], declared[0] }, type.CanonicalComponentTypes.ToArray());
        Assert.Equal(type, reordered);
        Assert.Equal(type.GetHashCode(), reordered.GetHashCode());
    }

    [Fact]
    public void RegistrationProducesOpaqueComponentAndEntityHandles()
    {
        Assembly assembly = typeof(EcsWorld).Assembly;
        Type componentHandle = Assert.Single(
            assembly.GetTypes(), static type => type.Name == "ComponentTypeHandle");
        Type entityHandle = Assert.Single(
            assembly.GetTypes(), static type => type.Name == "EntityTypeHandle");
        MethodInfo componentRegistration = Assert.Single(
            typeof(EcsWorld).GetMethods(BindingFlags.Instance | BindingFlags.NonPublic),
            static method => method.Name == "RegisterComponentType");
        MethodInfo entityRegistration = Assert.Single(
            typeof(EcsWorld).GetMethods(BindingFlags.Instance | BindingFlags.NonPublic),
            static method => method.Name == "RegisterEntityType");
        ConstructorInfo requestConstructor = Assert.Single(typeof(EntityCreateRequest).GetConstructors());

        Assert.Equal(componentHandle, componentRegistration.ReturnType.GetProperty("Handle")?.PropertyType);
        Assert.Equal(entityHandle, entityRegistration.ReturnType.GetProperty("Handle")?.PropertyType);
        Assert.Equal(entityHandle, requestConstructor.GetParameters()[0].ParameterType);
    }

    [Fact]
    public void EntityTypeRegistrationRejectsForgedAndCrossWorldComponentHandles()
    {
        using var module = new EcsModule();
        EcsWorld world = NewRegisteringWorld(module, 203);
        ComponentTypeRegistrationResult component = EcsTestRegistration.Register(world, new ComponentTypeDefinition(
            new ComponentTypeId(10),
            "Position",
            Array.Empty<ComponentFieldDefinition>()));
        Assert.True(component.Registered);

        EntityTypeRegistrationResult valid = world.RegisterEntityType(new EntityTypeDefinition(
            "Valid",
            new[] { component.Handle },
            EntityMode.Local));
        EntityTypeRegistrationResult forged = world.RegisterEntityType(new EntityTypeDefinition(
            "Forged",
            new[] { new ComponentTypeHandle(world.WorldId, uint.MaxValue) },
            EntityMode.Local));
        EntityTypeRegistrationResult crossWorld = world.RegisterEntityType(new EntityTypeDefinition(
            "CrossWorld",
            new[] { new ComponentTypeHandle(new WorldId(204), component.Handle.Value) },
            EntityMode.Local));

        Assert.True(valid.Registered);
        Assert.False(forged.Registered);
        Assert.Equal(StorageOperationStatus.Rejected, forged.Result.Status);
        Assert.Equal(EcsErrorCodes.InvalidType, forged.Error?.Code);
        Assert.False(crossWorld.Registered);
        Assert.Equal(StorageOperationStatus.Rejected, crossWorld.Result.Status);
        Assert.Equal(EcsErrorCodes.InvalidType, crossWorld.Error?.Code);
    }

    [Fact]
    public void ZeroFieldComponentMembershipSurvivesWorldCreationAndMatchesARealQuery()
    {
        using var module = new EcsModule();
        EcsWorld world = NewRegisteringWorld(module, 205);
        ComponentTypeRegistrationResult component = EcsTestRegistration.Register(world, new ComponentTypeDefinition(
            new ComponentTypeId(20),
            "Replicated",
            Array.Empty<ComponentFieldDefinition>()));
        Assert.True(component.Registered);
        EntityTypeRegistrationResult entityType = world.RegisterEntityType(new EntityTypeDefinition(
            "ReplicatedEntity",
            new[] { component.Handle }));
        Assert.True(entityType.Registered);
        Assert.Equal(StorageOperationStatus.Accepted, world.MarkReady().Status);
        Assert.Equal(StorageOperationStatus.Accepted, world.Start().Status);
        EntityCreateResult created = world.CreateEntityForCommit(
            world.Context,
            new EntityCreateRequest(entityType.Handle));
        Assert.True(created.Created);
        FieldInfo storageField = typeof(EcsWorld).GetField(
            "_storage",
            BindingFlags.Instance | BindingFlags.NonPublic) ??
            throw new InvalidOperationException("World storage field is missing.");
        var storage = Assert.IsType<ReferenceWorldStorageAdapter>(storageField.GetValue(world));
        QuerySpec query = new(
            new[] { new ComponentTypeId(20) },
            Array.Empty<ComponentTypeId>(),
            Array.Empty<ComponentFieldId>(),
            Array.Empty<ComponentFieldId>());
        Assert.Equal(StorageOperationStatus.Accepted,
            storage.CompileQuery(in query, out StorageQueryHandle queryHandle).Status);
        var entities = new LocalEntityId[1];

        StorageOperationResult enumeration = storage.EnumerateOrdered(queryHandle, entities, out int written);

        Assert.Equal(StorageOperationStatus.Accepted, enumeration.Status);
        Assert.Equal(1, written);
        Assert.Equal(created.Entity, entities[0]);
    }

    [Fact]
    public void ComponentRegistrationRequiresAWorldOwnedUnforgeableCapability()
    {
        MethodInfo registration = EcsTestRegistration.AssertRegistrationMethod();
        ParameterInfo[] parameters = registration.GetParameters();

        Assert.Equal(2, parameters.Length);
        Type capability = parameters[0].ParameterType;
        Assert.Equal("ComponentRegistrationCapability", capability.Name);
        Assert.Equal(typeof(EcsWorld), capability.DeclaringType);
        ConstructorInfo constructor = Assert.Single(
            capability.GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic));
        Assert.True(constructor.IsFamilyAndAssembly);
        Assert.Empty(capability.GetMethods(
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly));
        Assert.Equal(typeof(ComponentTypeDefinition), parameters[1].ParameterType);
    }

    private static EntityMode ReadStoredMode(EcsWorld world, LocalEntityId entity)
    {
        FieldInfo entityTableField = typeof(EcsWorld).GetField(
            "_entities",
            BindingFlags.Instance | BindingFlags.NonPublic) ?? throw new InvalidOperationException("Entity table field is missing.");
        object entityTable = entityTableField.GetValue(world) ?? throw new InvalidOperationException("Entity table is missing.");
        FieldInfo slotsField = entityTable.GetType().GetField(
            "_slots",
            BindingFlags.Instance | BindingFlags.NonPublic) ?? throw new InvalidOperationException("Entity slots field is missing.");
        IList slots = Assert.IsAssignableFrom<IList>(slotsField.GetValue(entityTable));
        object slot = slots[checked((int)entity.Index)] ?? throw new InvalidOperationException("Entity slot is missing.");
        FieldInfo modeField = slot.GetType().GetField(
            "Mode",
            BindingFlags.Instance | BindingFlags.Public) ?? throw new InvalidOperationException("Entity mode field is missing.");
        return Assert.IsType<EntityMode>(modeField.GetValue(slot));
    }

    private static EcsWorld NewRegisteringWorld(EcsModule module, ulong id)
    {
        var request = new EcsWorldCreateRequest(
            new WorldId(id),
            new EcsBudget(4, 32, 32, 4096));
        EcsWorld world = Assert.IsType<EcsWorld>(module.CreateWorld(in request).World);
        Assert.Equal(StorageOperationStatus.Accepted, world.BeginRegistration().Status);
        return world;
    }
}
