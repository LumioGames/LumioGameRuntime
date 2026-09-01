using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Lumio.GameRuntime.Command;
using Lumio.GameRuntime.Ecs;
using Lumio.GameRuntime.Gas;
using Lumio.Gen.ContractTypes;
using Xunit;

namespace Lumio.GameRuntime.Gas.Tests;

public sealed class EcsSingleTruthTests
{
    private static readonly byte[] HealthBytes = { 7, 9 };
    private static readonly byte[] FxKeyBytes = { 4, 4, 4 };

    [Fact]
    public void AuthoritativeReadsAndWritesGoOnlyThroughTheProjectionPort()
    {
        var port = new RecordingGasEcsProjectionPort();
        using GasWorldContext context = new(new WorldId(300UL), port);
        Assert.True(context.Register().Succeeded);
        Assert.True(context.MarkReady().Succeeded);
        Assert.True(context.Start().Succeeded);
        var entity = new LocalEntityId(4, 1);
        GasAuthoritativeField field = GasAuthoritativeField.Attribute("health");

        GasProjectionWriteResult written = context.WriteAuthoritative(entity, field, HealthBytes);
        GasProjectionReadResult read = context.ReadAuthoritative(entity, field);

        Assert.True(written.Written);
        Assert.Null(written.GeneratedErrorId);
        Assert.Equal(1, port.WriteCalls);
        Assert.Equal(GasEcsComponentTypes.Attribute, port.LastComponentType);
        Assert.True(read.Found);
        Assert.Equal(HealthBytes, read.CanonicalValue.ToArray());
        Assert.Equal(1, port.ReadCalls);
        Assert.False(HasAuthoritativeEntityStore(typeof(GasWorldContext), context));
        Assert.False(HasAuthoritativeEntityStore(typeof(GasTypeRegistry), context.Types));
    }

    [Fact]
    public void FxComponentIsRejectedAndFxKeyBelongsToEffectEntries()
    {
        var port = new RecordingGasEcsProjectionPort();
        using GasWorldContext context = new(new WorldId(301UL), port);
        Assert.True(context.Register().Succeeded);
        Assert.True(context.MarkReady().Succeeded);
        Assert.True(context.Start().Succeeded);
        var entity = new LocalEntityId(8, 2);

        GasProjectionWriteResult fxComponent = context.WriteAuthoritative(
            entity,
            new GasAuthoritativeField("FxComponent", "key"),
            FxKeyBytes);
        Assert.False(fxComponent.Written);
        Assert.Equal("InvalidArgument", fxComponent.GeneratedErrorId);
        Assert.Equal(0, port.WriteCalls);

        GasProjectionWriteResult fxKey = context.WriteAuthoritative(
            entity,
            GasAuthoritativeField.Effect("fx_key"),
            FxKeyBytes);
        Assert.True(fxKey.Written);
        Assert.Equal(1, port.WriteCalls);
        Assert.Equal(GasEcsComponentTypes.Effect, port.LastComponentType);
        Assert.DoesNotContain(
            typeof(GasWorldContext).Assembly.GetExportedTypes(),
            type => type.Name.Contains("FxComponent", StringComparison.Ordinal));
    }

    [Fact]
    public void FourEcsContainersAreTheOnlyAllowedAuthoritativeTargets()
    {
        var port = new RecordingGasEcsProjectionPort();
        using GasWorldContext context = new(new WorldId(302UL), port);
        Assert.True(context.Register().Succeeded);
        Assert.True(context.MarkReady().Succeeded);
        Assert.True(context.Start().Succeeded);
        var entity = new LocalEntityId(1, 1);

        Assert.True(context.WriteAuthoritative(entity, GasAuthoritativeField.Ability("row"), HealthBytes).Written);
        Assert.True(context.WriteAuthoritative(entity, GasAuthoritativeField.Effect("stack"), HealthBytes).Written);
        Assert.True(context.WriteAuthoritative(entity, GasAuthoritativeField.Attribute("mana"), HealthBytes).Written);
        Assert.True(context.WriteAuthoritative(entity, GasAuthoritativeField.Tag("status"), HealthBytes).Written);
        Assert.Equal(4, port.WriteCalls);

        GasProjectionWriteResult unknown = context.WriteAuthoritative(
            entity,
            new GasAuthoritativeField("InventoryComponent", "count"),
            HealthBytes);
        Assert.False(unknown.Written);
        Assert.Equal("InvalidArgument", unknown.GeneratedErrorId);
        Assert.Equal(4, port.WriteCalls);
    }

    [Fact]
    public void CommandBufferAdapterWritesPublicCommandsWithoutASecondStore()
    {
        var buffer = new ProcessorCommandBuffer(
            new ProcessorInvocationKey(1UL, "w1", ProcessorDescriptorPhase.GasAndEventFinalize, "gas"),
            CommandBufferBudget.Unlimited);
        var port = new CommandBufferGasProjectionPort(buffer.Writer);
        using GasWorldContext context = new(new WorldId(303UL), port);
        Assert.True(context.Register().Succeeded);
        Assert.True(context.MarkReady().Succeeded);
        Assert.True(context.Start().Succeeded);
        var entity = new LocalEntityId(2, 3);

        GasProjectionWriteResult written = context.WriteAuthoritative(
            entity,
            GasAuthoritativeField.Attribute("health"),
            HealthBytes);
        GasProjectionReadResult read = context.ReadAuthoritative(entity, GasAuthoritativeField.Attribute("health"));

        Assert.True(written.Written);
        Assert.False(read.Found);
        Lumio.GameRuntime.Command.Command command = Assert.Single(buffer.Commands);
        Assert.Equal(CommandKind.Write, command.Kind);
        Assert.Equal(entity.ToString(), command.TargetEntityId);
        Assert.Equal(GasEcsComponentTypes.Attribute, command.ComponentType);
        Assert.Equal("health", command.FieldName);
        Assert.Equal(HealthBytes, command.Payload.ToArray());
        Assert.False(HasAuthoritativeEntityStore(typeof(CommandBufferGasProjectionPort), port));
        Assert.False(HasAuthoritativeEntityStore(typeof(GasWorldContext), context));
    }

    [Fact]
    public void ProductionSurfaceHasNoRpcWallClockOrTickScheduler()
    {
        Assembly assembly = typeof(GasWorldContext).Assembly;
        string[] exported = assembly.GetExportedTypes().Select(type => type.Name).ToArray();
        Assert.DoesNotContain(exported, name =>
            name.Contains("Rpc", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Timer", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Scheduler", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("WallClock", StringComparison.OrdinalIgnoreCase));

        foreach (Type type in assembly.GetTypes())
        {
            if (type.Namespace is null ||
                !type.Namespace.StartsWith("Lumio.GameRuntime.Gas", StringComparison.Ordinal))
            {
                continue;
            }

            foreach (FieldInfo field in type.GetFields(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
            {
                string fullName = field.FieldType.FullName ?? field.FieldType.Name;
                Assert.DoesNotContain("System.Timers.Timer", fullName, StringComparison.Ordinal);
                Assert.DoesNotContain("System.Threading.Timer", fullName, StringComparison.Ordinal);
                Assert.DoesNotContain("DateTime", fullName, StringComparison.Ordinal);
                Assert.DoesNotContain("Stopwatch", fullName, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void ContextAndRegistryHaveNoEntityKeyedAttributeOrTagAuthority()
    {
        Assert.False(DeclaresForbiddenStore(typeof(GasWorldContext)));
        Assert.False(DeclaresForbiddenStore(typeof(GasTypeRegistry)));
        Assert.False(DeclaresForbiddenStore(typeof(GasModule)));
        Assert.False(DeclaresForbiddenStore(typeof(GasServices)));
    }

    private static bool HasAuthoritativeEntityStore(Type type, object instance)
    {
        for (Type? cursor = type; cursor is not null; cursor = cursor.BaseType)
        {
            foreach (FieldInfo field in cursor.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                if (IsForbiddenStore(field.FieldType))
                    return true;
                object? value = field.GetValue(instance);
                if (value is not null && value.GetType() != type && IsForbiddenStore(value.GetType()))
                    return true;
            }
        }

        return false;
    }

    private static bool DeclaresForbiddenStore(Type type)
    {
        foreach (FieldInfo field in type.GetFields(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
        {
            if (IsForbiddenStore(field.FieldType))
                return true;
        }

        return false;
    }

    private static bool IsForbiddenStore(Type type)
    {
        string name = type.FullName ?? type.Name;
        if (name.Contains("AttributeValue", StringComparison.Ordinal))
            return true;
        if (name.Contains("FxComponent", StringComparison.Ordinal))
            return true;
        if (!type.IsGenericType)
            return false;
        Type definition = type.GetGenericTypeDefinition();
        if (definition != typeof(Dictionary<,>) &&
            definition != typeof(SortedDictionary<,>) &&
            !name.Contains("Dictionary", StringComparison.Ordinal))
        {
            return false;
        }

        Type key = type.GetGenericArguments()[0];
        Type value = type.GetGenericArguments()[1];
        if (key == typeof(LocalEntityId))
            return true;
        string valueName = value.FullName ?? value.Name;
        return valueName.Contains("Attribute", StringComparison.OrdinalIgnoreCase) ||
               valueName.Contains("Tag", StringComparison.OrdinalIgnoreCase);
    }
}
