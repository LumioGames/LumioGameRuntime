using System;
using System.Globalization;
using System.Threading.Tasks;
using Lumio.GameRuntime.Ecs;
using Lumio.GameRuntime.Replication.Lifecycle;
using Lumio.GameRuntime.Replication.Mapping;
using Lumio.Gen.ContractTypes;
using Xunit;

namespace Lumio.GameRuntime.Replication.Tests;

public sealed class IdentityMappingPropertyTests
{
    [Fact]
    public void NetAndLocalMappingIsBijectiveWithGenerationSafety()
    {
        var table = new NetEntityMappingTable(Fixtures.WorldId);
        Assert.True(table.TryBind(Fixtures.Net(1), new LocalEntityId(4, 1)));
        Assert.False(table.TryBind(Fixtures.Net(1), new LocalEntityId(5, 1)));
        Assert.False(table.TryBind(Fixtures.Net(2), new LocalEntityId(4, 1)));
        Assert.True(table.TryResolveLocal(Fixtures.Net(1), out LocalEntityId local));
        Assert.Equal(new LocalEntityId(4, 1), local);
    }

    [Fact]
    public void DuplicateNetOrLocalBindingsAreRejectedAndIndexesStayBijective()
    {
        var table = new NetEntityMappingTable(Fixtures.WorldId);
        LocalEntityId first = new(4, 1);
        LocalEntityId second = new(5, 1);

        Assert.True(table.TryBind(Fixtures.Net(1), first));
        Assert.False(table.TryBind(Fixtures.Net(1), second));
        Assert.False(table.TryBind(Fixtures.Net(2), first));
        Assert.True(table.TryResolveLocal(Fixtures.Net(1), out LocalEntityId bound));
        Assert.Equal(first, bound);
        Assert.True(table.TryResolveNet(first, out NetEntityId net));
        Assert.Equal(Fixtures.Net(1), net);
        Assert.False(table.TryResolveLocal(Fixtures.Net(2), out _));
        Assert.False(table.TryResolveNet(second, out _));
        Assert.Equal(1, table.Count);
    }

    [Fact]
    public void GenerationMismatchDoesNotResolveOrBind()
    {
        var table = new NetEntityMappingTable(Fixtures.WorldId);
        Assert.True(table.TryBind(Fixtures.Net(1), new LocalEntityId(4, 2)));
        Assert.False(table.TryResolveLocal(Fixtures.Net(1), 1, out _));
        Assert.True(table.TryResolveLocal(Fixtures.Net(1), 2, out string? local));
        Assert.Equal("4:2", local);

        IdentityStoreToken token = table.CaptureToken();
        EntityIdentity mismatched = new(
            Fixtures.Net(2).Value, "server-a", 7, 15, 9, "4:2",
            EntityIdentityNamespace.Authoritative, EntityIdentityLifecycle.Alive,
            null, null, null, null);
        Assert.False(table.Bind(mismatched, token).Succeeded);
        Assert.Equal(1, table.Count);
    }

    [Fact]
    public void CrossWorldLocalIdsAreRejected()
    {
        var table = new NetEntityMappingTable(Fixtures.WorldId);
        LocalEntityId local = new(4, 1);

        Assert.False(table.TryBind(Fixtures.Net(1), local, Fixtures.OtherWorld));
        Assert.True(table.TryBind(Fixtures.Net(1), local, Fixtures.WorldId));
        Assert.Equal(Fixtures.WorldId, table.WorldId);
        Assert.Equal(1, table.Count);
    }

    [Fact]
    public void AuthoritativeNetEntityIdIsNotReusedAfterUnbind()
    {
        var table = new NetEntityMappingTable(Fixtures.WorldId);
        Assert.True(table.TryBind(Fixtures.Net(1), new LocalEntityId(4, 1)));
        Assert.True(table.Remove(Fixtures.Net(1), table.CaptureToken()));

        Assert.False(table.TryBind(Fixtures.Net(1), new LocalEntityId(4, 2)));
        Assert.False(table.TryResolveLocal(Fixtures.Net(1), out _));
        Assert.True(table.IsTombstoned(Fixtures.Net(1)));
    }

    [Fact]
    public void BindUnbindAndRemapSequencesKeepBothIndexesConsistent()
    {
        var table = new NetEntityMappingTable(Fixtures.WorldId);
        var remaps = new ProvisionalRemapTable();
        IdentityStoreToken remapToken = remaps.CaptureToken();
        LocalEntityId first = new(4, 1);
        LocalEntityId second = new(5, 1);

        Assert.True(table.TryBind(Fixtures.Net(1), first));
        Assert.True(table.TryBind(Fixtures.Net(2), second));
        Assert.True(table.Remove(Fixtures.Net(1), table.CaptureToken()));

        Assert.False(table.TryResolveLocal(Fixtures.Net(1), out _));
        Assert.False(table.TryResolveNet(first, out _));
        Assert.True(table.TryResolveLocal(Fixtures.Net(2), out LocalEntityId remaining));
        Assert.Equal(second, remaining);
        Assert.True(table.TryResolveNet(second, out NetEntityId remainingNet));
        Assert.Equal(Fixtures.Net(2), remainingNet);
        Assert.Equal(1, table.Count);

        EntityIdentity provisional = new(
            Fixtures.Net(3).Value, "client-provisional", 7, 15, 1, "6:1",
            EntityIdentityNamespace.Provisional, EntityIdentityLifecycle.Alive,
            null, null, null, null);
        EntityIdentity authoritative = new(
            Fixtures.Net(4).Value, "server-a", 7, 15, 1, "7:1",
            EntityIdentityNamespace.Authoritative, EntityIdentityLifecycle.Alive,
            null, null, null, null);
        EntityIdentity otherProvisional = new(
            Fixtures.Net(5).Value, "client-provisional", 7, 15, 1, "8:1",
            EntityIdentityNamespace.Provisional, EntityIdentityLifecycle.Alive,
            null, null, null, null);

        Assert.True(remaps.Add(provisional, authoritative, remapToken).Succeeded);
        Assert.True(remaps.TryResolve(Fixtures.Net(3), out NetEntityId remapped));
        Assert.Equal(Fixtures.Net(4), remapped);
        Assert.False(remaps.Add(otherProvisional, authoritative, remapToken).Succeeded);
        Assert.False(remaps.Add(authoritative, provisional, remapToken).Succeeded);
        Assert.Equal(1, remaps.Count);
    }

    [Fact]
    public async Task WorldBoundTableRejectsNonOwnerThreadMutation()
    {
        var table = new NetEntityMappingTable(Fixtures.WorldId);
        Assert.True(table.TryBind(Fixtures.Net(1), new LocalEntityId(4, 1)));
        bool boundOnWorker = await Task.Run(() => table.TryBind(Fixtures.Net(2), new LocalEntityId(5, 1)));

        Assert.False(boundOnWorker);
        Assert.Equal(1, table.Count);
        Assert.False(table.TryResolveLocal(Fixtures.Net(2), out _));
    }

    [Fact]
    public void FoundationContextExposesExactLifecycleAndAllowsCloseOrFaultFromCreated()
    {
        string[] names = Enum.GetNames<ReplicationContextState>();
        Assert.Equal(
            new[]
            {
                nameof(ReplicationContextState.Created),
                nameof(ReplicationContextState.Snapshotting),
                nameof(ReplicationContextState.AwaitingBaselineAck),
                nameof(ReplicationContextState.Active),
                nameof(ReplicationContextState.Resyncing),
                nameof(ReplicationContextState.Draining),
                nameof(ReplicationContextState.Closed),
                nameof(ReplicationContextState.Faulted),
            },
            names);
        Assert.Equal(8, names.Length);
        Assert.DoesNotContain(names, name => name is "Connected" or "Running");
        Assert.False(Enum.TryParse<ReplicationContextState>("Connected", out _));
        Assert.False(Enum.TryParse<ReplicationContextState>("Running", out _));

        using ReplicationContext created = CreateContext();
        Assert.Equal(ReplicationContextState.Created, created.State);
        Assert.True(created.Close().Succeeded);
        Assert.Equal(ReplicationContextState.Closed, created.State);

        using ReplicationContext faulted = CreateContext();
        Assert.Equal(ReplicationContextState.Created, faulted.State);
        Assert.True(faulted.Fault().Succeeded);
        Assert.Equal(ReplicationContextState.Faulted, faulted.State);
    }

    private static ReplicationContext CreateContext()
    {
        var mappings = new MappingRegistry();
        Assert.True(mappings.Register(MappingDescriptor.Create("mapping-actor-health", "Health", "current")).Succeeded);
        return new ReplicationContext(
            "session-1", "product", "release-1", mappings.View,
            new ReplicationBudget(16, 8192, 16, 8192));
    }

    private static class Fixtures
    {
        public static WorldId WorldId { get; } = new(1);

        public static WorldId OtherWorld { get; } = new(2);

        public static NetEntityId Net(int value) =>
            NetEntityId.Parse(value.ToString("x32", CultureInfo.InvariantCulture));
    }
}
