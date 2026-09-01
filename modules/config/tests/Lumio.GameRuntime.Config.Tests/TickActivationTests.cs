using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using Lumio.GameRuntime.Config;
using Lumio.Gen.ContractTypes;
using Xunit;

namespace Lumio.GameRuntime.Config.Tests;

/// <summary>
/// T07.S02 / S05 / S06: Tick-barrier activation, owner-thread guard, superseded
/// transition, and ConfigServices surface constraints.
/// </summary>
public sealed class TickActivationTests
{
    [Fact]
    public void StagedSnapshotBecomesVisibleOnlyAtTickBarrier()
    {
        var slot = ConfigSnapshotFixtures.ActiveSlot(snapshotId: 1UL);
        ConfigSnapshotLease tickN = slot.AcquireForTick(TickId.FromUInt64(10UL));
        slot.Stage(ConfigSnapshotFixtures.Snapshot(snapshotId: 2UL));
        Assert.Equal(1UL, tickN.Snapshot.SnapshotId.Value);
        Assert.True(slot.ActivateAtBarrier(TickId.FromUInt64(10UL)).Activated);
        using ConfigSnapshotLease tickNext = slot.AcquireForTick(TickId.FromUInt64(11UL));
        Assert.Equal(2UL, tickNext.Snapshot.SnapshotId.Value);
    }

    [Fact]
    public void ReadsDuringTickNStayOnAAfterStageBAndABecomesSupersededAtBarrier()
    {
        var slot = ConfigSnapshotFixtures.ActiveSlot(1UL);
        using ConfigSnapshotLease tickN = slot.AcquireForTick(TickId.FromUInt64(10UL));
        Assert.True(tickN.TryOpenTable("gameplay", out ConfigTableReader readerN));
        Assert.True(readerN.TryGet("k", "v", out ConfigValueView valueN));
        Assert.Equal("1", valueN.CanonicalText);

        var staged = slot.Stage(ConfigSnapshotFixtures.Snapshot(2UL));
        Assert.True(staged.Staged);
        Assert.Equal(ConfigVersionState.Staged, slot.GetVersionState(new ConfigSnapshotId(2UL)));
        Assert.Equal(ConfigVersionState.Active, slot.GetVersionState(new ConfigSnapshotId(1UL)));
        Assert.Equal(1UL, slot.Active.SnapshotId.Value);
        Assert.True(readerN.TryGet("k", "v", out ConfigValueView stillA));
        Assert.Equal("1", stillA.CanonicalText);
        Assert.Equal(1UL, tickN.Snapshot.SnapshotId.Value);

        var activated = slot.ActivateAtBarrier(TickId.FromUInt64(10UL));
        Assert.True(activated.Activated);
        Assert.Equal(2UL, activated.ActiveSnapshotId.Value);
        Assert.Equal(ConfigVersionState.Superseded, slot.GetVersionState(new ConfigSnapshotId(1UL)));
        Assert.Equal(ConfigVersionState.Active, slot.GetVersionState(new ConfigSnapshotId(2UL)));
        Assert.Equal(1UL, tickN.Snapshot.SnapshotId.Value);

        using ConfigSnapshotLease tickNext = slot.AcquireForTick(TickId.FromUInt64(11UL));
        Assert.True(tickNext.TryOpenTable("gameplay", out ConfigTableReader readerNext));
        Assert.True(readerNext.TryGet("k", "v", out ConfigValueView valueNext));
        Assert.Equal("2", valueNext.CanonicalText);
        Assert.Equal(2UL, slot.Active.SnapshotId.Value);
    }

    [Fact]
    public void ActivateWithoutStagedSnapshotIsRejectedAndDoesNotChangeActive()
    {
        var slot = ConfigSnapshotFixtures.ActiveSlot(1UL);
        var before = slot.Active.SnapshotId;
        var result = slot.ActivateAtBarrier(TickId.FromUInt64(10UL));

        Assert.False(result.Activated);
        Assert.Equal(before, result.ActiveSnapshotId);
        Assert.Equal("InvalidArgument", result.ErrorId);
        Assert.Equal(1UL, slot.Active.SnapshotId.Value);
        Assert.Equal(ConfigVersionState.Active, slot.GetVersionState(before));
    }

    [Fact]
    public void AcquireForTickPinsSnapshotIdWhenActiveIsReplaced()
    {
        var slot = ConfigSnapshotFixtures.ActiveSlot(1UL);
        using ConfigSnapshotLease pinned = slot.AcquireForTick(TickId.FromUInt64(3UL));
        slot.Stage(ConfigSnapshotFixtures.Snapshot(9UL));
        Assert.True(slot.ActivateAtBarrier(TickId.FromUInt64(3UL)).Activated);
        Assert.Equal(1UL, pinned.Snapshot.SnapshotId.Value);
        Assert.Equal(9UL, slot.Active.SnapshotId.Value);
        Assert.True(pinned.TryOpenTable("gameplay", out ConfigTableReader reader));
        Assert.True(reader.TryGet("k", "v", out ConfigValueView value));
        Assert.Equal("1", value.CanonicalText);
    }

    [Fact]
    public void ActivateAtBarrierFromNonOwnerThreadIsRejected()
    {
        var slot = ConfigSnapshotFixtures.ActiveSlot(1UL);
        slot.Stage(ConfigSnapshotFixtures.Snapshot(2UL));
        ConfigActivationResult? remote = null;
        var thread = new Thread(() => remote = slot.ActivateAtBarrier(TickId.FromUInt64(10UL)));
        thread.Start();
        thread.Join();

        Assert.NotNull(remote);
        Assert.False(remote.Value.Activated);
        Assert.Equal("WrongContext", remote.Value.ErrorId);
        Assert.Equal(1UL, slot.Active.SnapshotId.Value);
        Assert.Equal(ConfigVersionState.Staged, slot.GetVersionState(new ConfigSnapshotId(2UL)));
    }

    [Fact]
    public void StagingTheSameSnapshotHashIsIdempotent()
    {
        var slot = ConfigSnapshotFixtures.ActiveSlot(1UL);
        var candidate = ConfigSnapshotFixtures.Snapshot(4UL);
        var first = slot.Stage(candidate);
        var second = slot.Stage(ConfigSnapshotFixtures.Snapshot(4UL));
        Assert.True(first.Staged);
        Assert.True(second.Staged);
        Assert.Equal(first.SnapshotId, second.SnapshotId);
        Assert.Equal(1UL, slot.Active.SnapshotId.Value);
        Assert.True(slot.ActivateAtBarrier(TickId.FromUInt64(1UL)).Activated);
        var repeat = slot.Stage(ConfigSnapshotFixtures.Snapshot(4UL));
        Assert.True(repeat.Staged);
        var again = slot.ActivateAtBarrier(TickId.FromUInt64(2UL));
        Assert.True(again.Activated);
        Assert.Equal(4UL, again.ActiveSnapshotId.Value);
        Assert.Equal(ConfigVersionState.Active, slot.GetVersionState(new ConfigSnapshotId(4UL)));
    }

    [Fact]
    public void ConfigActivatorDelegatesBarrierActivationAndDoesNotSwitchMidTick()
    {
        var slot = ConfigSnapshotFixtures.ActiveSlot(1UL);
        var activator = new ConfigActivator(slot);
        using ConfigSnapshotLease tickN = slot.AcquireForTick(TickId.FromUInt64(7UL));
        slot.Stage(ConfigSnapshotFixtures.Snapshot(2UL));
        Assert.Equal(1UL, tickN.Snapshot.SnapshotId.Value);
        Assert.True(activator.ActivateAtBarrier(TickId.FromUInt64(7UL)).Activated);
        Assert.Equal(2UL, slot.Active.SnapshotId.Value);
        Assert.Equal(1UL, tickN.Snapshot.SnapshotId.Value);
    }

    [Fact]
    public void ConfigServicesExposesValidatorStageActivationAndSnapshotViewOnly()
    {
        var module = ConfigModule.Create();
        var submit = module.Submit(ValidEngineArtifact());
        Assert.Equal(ConfigSubmitStatus.Accepted, submit.Status);
        Assert.Throws<InvalidOperationException>(() => _ = module.Services.ActiveSnapshot);

        var validate = module.Services.Validate(ValidEngineArtifact());
        Assert.Equal(ConfigSubmitStatus.Accepted, validate.Status);

        var staged = module.Services.StageMerged(new ConfigSnapshotId(3UL));
        Assert.True(staged.Staged);
        Assert.Throws<InvalidOperationException>(() => _ = module.Services.ActiveSnapshot);

        var activated = module.Services.ActivateAtBarrier(TickId.FromUInt64(1UL));
        Assert.True(activated.Activated);
        IConfigSnapshotView view = module.Services.ActiveSnapshot;
        Assert.Equal(3UL, view.SnapshotId.Value);
        using ConfigSnapshotLease lease = module.Services.AcquireForTick(TickId.FromUInt64(1UL));
        Assert.Equal(3UL, lease.Snapshot.SnapshotId.Value);

        var invalid = module.Services.Validate(InvalidArtifact());
        Assert.Equal(ConfigSubmitStatus.Rejected, invalid.Status);
        Assert.Equal(3UL, module.Services.ActiveSnapshot.SnapshotId.Value);
    }

    [Fact]
    public void SubmitDoesNotActivateAndInvalidArtifactNeverBecomesActive()
    {
        var module = ConfigModule.Create();
        var rejected = module.Submit(InvalidArtifact());
        Assert.Equal(ConfigSubmitStatus.Rejected, rejected.Status);
        Assert.Throws<InvalidOperationException>(() => _ = module.Services.ActiveSnapshot);
        var merged = module.Services.StageMerged(new ConfigSnapshotId(1UL));
        Assert.False(merged.Staged);

        Assert.Equal(ConfigSubmitStatus.Accepted, module.Submit(ValidEngineArtifact()).Status);
        Assert.True(module.Services.StageMerged(new ConfigSnapshotId(8UL)).Staged);
        Assert.True(module.Services.ActivateAtBarrier(TickId.FromUInt64(1UL)).Activated);

        Assert.Equal(ConfigSubmitStatus.Rejected, module.Submit(InvalidArtifact()).Status);
        Assert.Equal(8UL, module.Services.ActiveSnapshot.SnapshotId.Value);
    }

    [Fact]
    public void PublicConfigSurfaceHasNoCompilerFileWatcherOrDiContainerTypes()
    {
        var assembly = typeof(ConfigServices).Assembly;
        var exported = assembly.ExportedTypes.ToArray();
        Assert.DoesNotContain(
            exported,
            type =>
                type.Name.Contains("Compile", StringComparison.OrdinalIgnoreCase) ||
                type.Name.Contains("FileWatcher", StringComparison.OrdinalIgnoreCase) ||
                type.Name.Contains("FileSystemWatcher", StringComparison.OrdinalIgnoreCase) ||
                type.Name.Contains("ServiceProvider", StringComparison.OrdinalIgnoreCase) ||
                type.Name.Contains("ServiceCollection", StringComparison.OrdinalIgnoreCase) ||
                type.Name.Contains("IServiceScope", StringComparison.OrdinalIgnoreCase));

        var forbiddenParameterOrReturn = exported
            .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            .Where(method =>
                method.Name.Contains("Compile", StringComparison.OrdinalIgnoreCase) ||
                method.Name.Contains("Watch", StringComparison.OrdinalIgnoreCase) ||
                IsDiOrWatcherType(method.ReturnType) ||
                method.GetParameters().Any(parameter =>
                    IsDiOrWatcherType(parameter.ParameterType) ||
                    IsSourceMaterialParameter(parameter)))
            .Select(method => method.DeclaringType!.FullName + "." + method.Name)
            .ToArray();
        Assert.Empty(forbiddenParameterOrReturn);

        var services = typeof(ConfigServices);
        Assert.NotNull(services.GetMethod(nameof(ConfigServices.Validate)));
        Assert.NotNull(services.GetMethod(nameof(ConfigServices.StageMerged)));
        Assert.NotNull(services.GetMethod(nameof(ConfigServices.ActivateAtBarrier)));
        Assert.Contains(
            services.GetProperties(BindingFlags.Public | BindingFlags.Instance),
            property => typeof(IConfigSnapshotView).IsAssignableFrom(property.PropertyType));
    }

    private static bool IsDiOrWatcherType(Type type)
    {
        var name = type.FullName ?? type.Name;
        return name.Contains("FileSystemWatcher", StringComparison.Ordinal) ||
            name.Contains("IServiceProvider", StringComparison.Ordinal) ||
            name.Contains("IServiceCollection", StringComparison.Ordinal) ||
            name.Contains("IServiceScope", StringComparison.Ordinal) ||
            name.Contains("ServiceCollection", StringComparison.Ordinal);
    }

    private static bool IsSourceMaterialParameter(ParameterInfo parameter)
    {
        var name = parameter.Name ?? string.Empty;
        return parameter.ParameterType == typeof(Stream) ||
            parameter.ParameterType == typeof(TextReader) ||
            name.Contains("path", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("source", StringComparison.OrdinalIgnoreCase);
    }

    private static GeneratedConfigArtifactView ValidEngineArtifact() =>
        TableFactory.Artifact(
            TableFactory.Table(
                "gameplay",
                TableFactory.Cols(TableFactory.Column("v", "i32", required: true)),
                TableFactory.Rows(TableFactory.Row("k", "{\"v\":5}"))));

    private static GeneratedConfigArtifactView InvalidArtifact() =>
        TableFactory.Artifact(
            TableFactory.Table(
                "gameplay",
                TableFactory.Cols(TableFactory.Column("v", "i32", required: true)),
                TableFactory.Rows(TableFactory.Row("k", "{\"v\":\"not-a-number\"}"))));
}

internal static class ConfigSnapshotFixtures
{
    internal static ConfigActivationSlot ActiveSlot(ulong snapshotId)
    {
        var slot = new ConfigActivationSlot();
        var staged = slot.Stage(Snapshot(snapshotId));
        if (!staged.Staged)
        {
            throw new InvalidOperationException("fixture failed to stage the initial snapshot.");
        }

        var activated = slot.ActivateAtBarrier(TickId.FromUInt64(0UL));
        if (!activated.Activated)
        {
            throw new InvalidOperationException("fixture failed to activate the initial snapshot.");
        }

        return slot;
    }

    internal static ConfigSnapshot Snapshot(ulong snapshotId)
    {
        var rows = new[]
        {
            new ConfigSnapshotRow("k", new[] { new ConfigSnapshotCell("v", snapshotId.ToString(CultureInfo.InvariantCulture)) }),
        };
        var tables = new[]
        {
            new ConfigSnapshotTable("gameplay", "table-hash-" + snapshotId, rows),
        };
        return new ConfigSnapshot(
            new ConfigSnapshotId(snapshotId),
            SchemaEpoch.FromGeneratedContracts(),
            "output-hash-" + snapshotId,
            tables);
    }
}
