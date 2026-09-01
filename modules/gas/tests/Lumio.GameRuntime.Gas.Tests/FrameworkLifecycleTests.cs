using System;
using Lumio.GameRuntime.Ecs;
using Lumio.GameRuntime.Gas;
using Lumio.Gen.ContractTypes;
using Xunit;

namespace Lumio.GameRuntime.Gas.Tests;

public sealed class FrameworkLifecycleTests
{
    private static readonly string[] CanonicalFrameworkStates =
    {
        "Unloaded", "Registered", "Ready", "Running", "Draining", "Faulted",
    };

    [Fact]
    public void CanonicalPathIsUnloadedRegisteredReadyRunningDrainingUnloaded()
    {
        using GasWorldContext context = GasTestHarness.CreateContext(1UL);

        Assert.Equal(GasFrameworkState.Unloaded, context.State);
        Assert.True(context.Register().Succeeded);
        Assert.Equal(GasFrameworkState.Registered, context.State);
        Assert.True(context.MarkReady().Succeeded);
        Assert.Equal(GasFrameworkState.Ready, context.State);
        Assert.True(context.Start().Succeeded);
        Assert.Equal(GasFrameworkState.Running, context.State);
        Assert.True(context.BeginDrain().Succeeded);
        Assert.Equal(GasFrameworkState.Draining, context.State);
        Assert.True(context.DisposeContext().Succeeded);
        Assert.Equal(GasFrameworkState.Unloaded, context.State);
    }

    [Theory]
    [InlineData(GasFrameworkState.Registered)]
    [InlineData(GasFrameworkState.Ready)]
    [InlineData(GasFrameworkState.Running)]
    [InlineData(GasFrameworkState.Draining)]
    public void ActiveStateCanEnterFaulted(GasFrameworkState active)
    {
        using GasWorldContext context = GasTestHarness.ContextAt(2UL + (ulong)active, active);

        GasLifecycleResult fault = context.Fault("InternalInvariant");

        Assert.True(fault.Succeeded);
        Assert.Equal(GasFrameworkState.Faulted, context.State);
        Assert.Equal(GasFrameworkState.Faulted, fault.State);
        Assert.Contains("InternalInvariant", Catalog.StableErrorIds, StringComparer.Ordinal);
    }

    [Theory]
    [InlineData(GasFrameworkState.Unloaded, nameof(GasWorldContext.MarkReady), "InternalInvariant")]
    [InlineData(GasFrameworkState.Unloaded, nameof(GasWorldContext.Start), "InternalInvariant")]
    [InlineData(GasFrameworkState.Unloaded, nameof(GasWorldContext.BeginDrain), "InternalInvariant")]
    [InlineData(GasFrameworkState.Unloaded, nameof(GasWorldContext.DisposeContext), "InternalInvariant")]
    [InlineData(GasFrameworkState.Registered, nameof(GasWorldContext.Register), "InternalInvariant")]
    [InlineData(GasFrameworkState.Registered, nameof(GasWorldContext.Start), "InternalInvariant")]
    [InlineData(GasFrameworkState.Registered, nameof(GasWorldContext.BeginDrain), "InternalInvariant")]
    [InlineData(GasFrameworkState.Registered, nameof(GasWorldContext.DisposeContext), "InternalInvariant")]
    [InlineData(GasFrameworkState.Ready, nameof(GasWorldContext.Register), "InternalInvariant")]
    [InlineData(GasFrameworkState.Ready, nameof(GasWorldContext.MarkReady), "InternalInvariant")]
    [InlineData(GasFrameworkState.Ready, nameof(GasWorldContext.BeginDrain), "InternalInvariant")]
    [InlineData(GasFrameworkState.Ready, nameof(GasWorldContext.DisposeContext), "InternalInvariant")]
    [InlineData(GasFrameworkState.Running, nameof(GasWorldContext.Register), "InternalInvariant")]
    [InlineData(GasFrameworkState.Running, nameof(GasWorldContext.MarkReady), "InternalInvariant")]
    [InlineData(GasFrameworkState.Running, nameof(GasWorldContext.Start), "InternalInvariant")]
    [InlineData(GasFrameworkState.Running, nameof(GasWorldContext.DisposeContext), "InternalInvariant")]
    [InlineData(GasFrameworkState.Draining, nameof(GasWorldContext.Register), "ContextClosing")]
    [InlineData(GasFrameworkState.Draining, nameof(GasWorldContext.MarkReady), "ContextClosing")]
    [InlineData(GasFrameworkState.Draining, nameof(GasWorldContext.Start), "ContextClosing")]
    [InlineData(GasFrameworkState.Draining, nameof(GasWorldContext.BeginDrain), "ContextClosing")]
    [InlineData(GasFrameworkState.Faulted, nameof(GasWorldContext.Register), "InternalInvariant")]
    [InlineData(GasFrameworkState.Faulted, nameof(GasWorldContext.MarkReady), "InternalInvariant")]
    [InlineData(GasFrameworkState.Faulted, nameof(GasWorldContext.Start), "InternalInvariant")]
    [InlineData(GasFrameworkState.Faulted, nameof(GasWorldContext.BeginDrain), "InternalInvariant")]
    public void IllegalTransitionIsRejectedWithoutChangingState(
        GasFrameworkState state,
        string operation,
        string errorId)
    {
        using GasWorldContext context = GasTestHarness.ContextAt(40UL + (ulong)state, state);

        GasLifecycleResult result = GasTestHarness.Invoke(context, operation);

        Assert.False(result.Succeeded);
        Assert.Equal(errorId, result.GeneratedErrorId);
        Assert.Equal(state, context.State);
        Assert.Contains(errorId, Catalog.StableErrorIds, StringComparer.Ordinal);
    }

    [Fact]
    public void DisposedContextFailStopsFurtherWork()
    {
        using GasWorldContext context = GasTestHarness.ContextAt(80UL, GasFrameworkState.Draining);
        Assert.True(context.DisposeContext().Succeeded);
        Assert.Equal(GasFrameworkState.Unloaded, context.State);

        GasLifecycleResult register = context.Register();
        AbilityHandleResult handle = context.CreateAbilityHandle(new AbilityInstanceId(1UL));
        GasProjectionWriteResult write = context.WriteAuthoritative(
            new LocalEntityId(1, 1),
            GasAuthoritativeField.Attribute("health"),
            new byte[] { 1 });

        Assert.False(register.Succeeded);
        Assert.Equal("ContextDestroyed", register.GeneratedErrorId);
        Assert.False(handle.Succeeded);
        Assert.Equal("ContextDestroyed", handle.GeneratedErrorId);
        Assert.False(write.Written);
        Assert.Equal("ContextDestroyed", write.GeneratedErrorId);
        Assert.Equal(GasFrameworkState.Unloaded, context.State);
        Assert.True(context.DisposeContext().Succeeded);
    }

    [Fact]
    public void InitialUnloadedRejectsHandleCreationAsFailStop()
    {
        using GasWorldContext context = GasTestHarness.CreateContext(81UL);

        AbilityHandleResult ability = context.CreateAbilityHandle(new AbilityInstanceId(3UL));
        EffectHandleResult effect = context.CreateEffectHandle(new EffectInstanceId(4UL));

        Assert.False(ability.Succeeded);
        Assert.Equal("ContextDestroyed", ability.GeneratedErrorId);
        Assert.False(effect.Succeeded);
        Assert.Equal("ContextDestroyed", effect.GeneratedErrorId);
        Assert.Equal(GasFrameworkState.Unloaded, context.State);
    }

    [Fact]
    public void FaultedContextAllowsDisposeOnly()
    {
        using GasWorldContext context = GasTestHarness.ContextAt(82UL, GasFrameworkState.Running);
        Assert.True(context.Fault("InternalInvariant").Succeeded);

        AbilityHandleResult issued = context.CreateAbilityHandle(new AbilityInstanceId(8UL));
        GasLifecycleResult start = context.Start();
        GasLifecycleResult drain = context.BeginDrain();
        GasLifecycleResult laterFault = context.Fault("InvalidArgument");

        Assert.False(issued.Succeeded);
        Assert.Equal("InternalInvariant", issued.GeneratedErrorId);
        Assert.False(start.Succeeded);
        Assert.False(drain.Succeeded);
        Assert.False(laterFault.Succeeded);
        Assert.Equal(GasFrameworkState.Faulted, context.State);
        Assert.True(context.DisposeContext().Succeeded);
        Assert.Equal(GasFrameworkState.Unloaded, context.State);
    }

    [Fact]
    public void FaultRequiresGeneratedCatalogErrorId()
    {
        using GasWorldContext context = GasTestHarness.ContextAt(83UL, GasFrameworkState.Ready);

        Assert.Throws<ArgumentException>(() => context.Fault("AlreadyRegistered"));
        Assert.Throws<ArgumentException>(() => context.Fault(" "));
        Assert.Equal(GasFrameworkState.Ready, context.State);
    }

    [Fact]
    public void DefaultWorldIdIsRejectedAtConstruction()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new GasWorldContext(default, new RecordingGasEcsProjectionPort()));
    }

    [Fact]
    public void ModuleServicesCreateIndependentWorldContexts()
    {
        GasModule module = GasModule.Create();
        var firstPort = new RecordingGasEcsProjectionPort();
        var secondPort = new RecordingGasEcsProjectionPort();

        using GasWorldContext first = module.Services.CreateWorldContext(new WorldId(11UL), firstPort);
        using GasWorldContext second = module.CreateWorldContext(new WorldId(12UL), secondPort);

        Assert.NotSame(first, second);
        Assert.NotEqual(first.WorldId, second.WorldId);
        Assert.Equal(GasFrameworkState.Unloaded, first.State);
        Assert.Equal(GasFrameworkState.Unloaded, second.State);
        Assert.NotNull(module.Services.Types);
    }

    [Fact]
    public void FrameworkStateEnumContainsExactV14Names()
    {
        Assert.Equal(CanonicalFrameworkStates, Enum.GetNames<GasFrameworkState>());
    }
}

internal static class GasTestHarness
{
    public static GasWorldContext CreateContext(ulong worldId) =>
        new(new WorldId(worldId), new RecordingGasEcsProjectionPort());

    public static GasWorldContext ContextAt(ulong worldId, GasFrameworkState state)
    {
        GasWorldContext context = CreateContext(worldId);
        if (state == GasFrameworkState.Unloaded)
            return context;

        Assert.True(context.Register().Succeeded);
        if (state == GasFrameworkState.Registered)
            return context;

        Assert.True(context.MarkReady().Succeeded);
        if (state == GasFrameworkState.Ready)
            return context;

        Assert.True(context.Start().Succeeded);
        if (state == GasFrameworkState.Running)
            return context;

        Assert.True(context.BeginDrain().Succeeded);
        if (state == GasFrameworkState.Draining)
            return context;

        Assert.True(context.Fault("InternalInvariant").Succeeded);
        Assert.Equal(GasFrameworkState.Faulted, state);
        return context;
    }

    public static GasWorldContext Running(ulong worldId)
    {
        GasWorldContext context = ContextAt(worldId, GasFrameworkState.Running);
        return context;
    }

    public static GasLifecycleResult Invoke(GasWorldContext context, string operation)
    {
        return operation switch
        {
            nameof(GasWorldContext.Register) => context.Register(),
            nameof(GasWorldContext.MarkReady) => context.MarkReady(),
            nameof(GasWorldContext.Start) => context.Start(),
            nameof(GasWorldContext.BeginDrain) => context.BeginDrain(),
            nameof(GasWorldContext.DisposeContext) => context.DisposeContext(),
            _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, "Unknown lifecycle operation."),
        };
    }

    public static GasTypeDescriptor Descriptor(string schemaId, uint version, byte marker)
    {
        return new GasTypeDescriptor(
            schemaId,
            version,
            Lumio.GameRuntime.GeneratedContracts.GeneratedContractManifest.SchemaEpoch,
            new byte[] { marker });
    }
}

internal sealed class RecordingGasEcsProjectionPort : IGasEcsProjectionPort
{
    private readonly System.Collections.Generic.Dictionary<string, byte[]> _store = new(StringComparer.Ordinal);

    public int ReadCalls { get; private set; }

    public int WriteCalls { get; private set; }

    public string? LastComponentType { get; private set; }

    public GasProjectionReadResult ReadAuthoritative(LocalEntityId entity, in GasAuthoritativeField field)
    {
        ReadCalls++;
        LastComponentType = field.ComponentType;
        if (_store.TryGetValue(Key(entity, field), out byte[]? value))
            return GasProjectionReadResult.Present(value);
        return GasProjectionReadResult.Missing();
    }

    public GasProjectionWriteResult WriteAuthoritative(
        LocalEntityId entity,
        in GasAuthoritativeField field,
        ReadOnlySpan<byte> canonicalValue)
    {
        WriteCalls++;
        LastComponentType = field.ComponentType;
        _store[Key(entity, field)] = canonicalValue.ToArray();
        return GasProjectionWriteResult.Accepted();
    }

    private static string Key(LocalEntityId entity, in GasAuthoritativeField field) =>
        entity.Index.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" +
        entity.Generation.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" +
        field.ComponentType + ":" + field.FieldName;
}
