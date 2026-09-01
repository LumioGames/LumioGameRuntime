using System;
using Lumio.GameRuntime.Replication.Mapping;
using Lumio.Gen.ContractTypes;
using Xunit;

namespace Lumio.GameRuntime.Replication.Tests;

public sealed class IdentityTombstoneRegressionTests
{
    private static readonly NetEntityId Id = NetEntityId.Parse("00000000000000010000000000000001");
    private static readonly NetEntityId Id2 = NetEntityId.Parse("00000000000000010000000000000002");
    private static readonly TombstoneHorizonResult KnownHorizon = new(true, 21);

    [Fact]
    public void InvalidSuppliedHorizonRetainsAConservativeTombstone()
    {
        var table = new NetEntityMappingTable();
        IdentityStoreToken token = table.CaptureToken();
        Assert.True(table.Bind(Id, "4:2", token).Succeeded);

        Assert.True(table.Remove(Id, destroyRevision: 10, tombstoneUntilRevision: 0, token));
        Assert.True(table.IsTombstoned(Id));
        Assert.False(table.Bind(Id, "4:3", currentRevision: 11, token).Succeeded);
        Assert.Equal(0, table.Count);
    }

    [Fact]
    public void UnknownHorizonDoesNotPermitDelayedReuse()
    {
        var table = new NetEntityMappingTable();
        IdentityStoreToken token = table.CaptureToken();
        Assert.True(table.Bind(Id, "4:2", token).Succeeded);

        Assert.True(table.Remove(Id, destroyRevision: 10, horizon: new TombstoneHorizonResult(false, 0), token));
        Assert.False(table.Bind(Id, "4:3", currentRevision: 500, token).Succeeded);
    }

    [Fact]
    public void KnownHorizonAtOrBeforeDestroyRevisionIsNeverClampedToDestroy()
    {
        var table = new NetEntityMappingTable();
        IdentityStoreToken token = table.CaptureToken();
        Assert.True(table.Bind(Id, "4:2", token).Succeeded);

        Assert.True(table.Remove(Id, destroyRevision: 10, horizon: new TombstoneHorizonResult(true, 5), token));
        Assert.False(table.Bind(Id, "4:3", currentRevision: 11, token).Succeeded);
        Assert.Equal(ulong.MaxValue, table.Tombstones[Id]);
    }

    [Fact]
    public void HorizonCollectionIsStrictAtTheBoundary()
    {
        var registry = new TombstoneRegistry();
        IdentityStoreToken token = registry.CaptureToken();
        Assert.True(registry.Add(Id, 20, token));

        Assert.Equal(0, registry.Collect(20, KnownHorizon, token));
        Assert.True(registry.Contains(Id, 20));
        Assert.Equal(0, registry.Collect(21, KnownHorizon, token));
        Assert.True(registry.Snapshot().ContainsKey(Id));
        Assert.Equal(1, registry.Collect(22, KnownHorizon, token));
        Assert.False(registry.Contains(Id, 22));
    }

    [Fact]
    public void GeneratedDestroyedAndTombstonedIdentitiesNeverBecomeLiveBindings()
    {
        var table = new NetEntityMappingTable();
        IdentityStoreToken token = table.CaptureToken();
        EntityIdentity destroyed = Identity(EntityIdentityLifecycle.Destroyed, null, 2);
        EntityIdentity tombstoned = Identity(EntityIdentityLifecycle.Tombstoned, 48, 3);

        Assert.False(table.Bind(destroyed, token).Succeeded);
        Assert.False(table.Bind(tombstoned, token).Succeeded);
        Assert.Equal(0, table.Count);
        Assert.True(table.IsTombstoned(Id));
        Assert.True(table.IsTombstoned(Id2));
    }

    [Fact]
    public void GeneratedDestroyRemovesAnExistingLiveBindingBeforeFencingTheIdentity()
    {
        var table = new NetEntityMappingTable();
        IdentityStoreToken token = table.CaptureToken();
        EntityIdentity alive = new(
            Id.Value, "server-a", 7, 15, 2, "4:2",
            EntityIdentityNamespace.Authoritative, EntityIdentityLifecycle.Alive,
            null, null, null, null);
        EntityIdentity destroyed = Identity(EntityIdentityLifecycle.Destroyed, null, 2);

        Assert.True(table.Bind(alive, token).Succeeded);
        Assert.False(table.Bind(destroyed, token).Succeeded);
        Assert.Equal(0, table.Count);
        Assert.False(table.TryResolveLocal(Id, 2, out _));
    }

    [Fact]
    public void GeneratedBindingRequiresAuthoritativeNamespaceAndMatchingLocalGeneration()
    {
        var table = new NetEntityMappingTable();
        IdentityStoreToken token = table.CaptureToken();
        EntityIdentity provisional = new(
            Id.Value, "client-provisional", 7, 15, 2, "4:2",
            EntityIdentityNamespace.Provisional, EntityIdentityLifecycle.Alive,
            null, null, null, null);
        EntityIdentity wrongGeneration = new(
            Id2.Value, "server-a", 7, 15, 9, "4:2",
            EntityIdentityNamespace.Authoritative, EntityIdentityLifecycle.Alive,
            null, null, null, null);

        Assert.False(table.Bind(provisional, token).Succeeded);
        Assert.False(table.Bind(wrongGeneration, token).Succeeded);
        Assert.Equal(0, table.Count);
    }

    [Fact]
    public void GeneratedAliveAuthoritativeIdentityBindsOnlyAfterGenerationValidation()
    {
        var table = new NetEntityMappingTable();
        IdentityStoreToken token = table.CaptureToken();
        EntityIdentity alive = new(
            Id.Value, "server-a", 7, 15, 2, "4:2",
            EntityIdentityNamespace.Authoritative, EntityIdentityLifecycle.Alive,
            null, null, null, null);

        MappingBindingResult result = table.Bind(alive, token);

        Assert.True(result.Succeeded, result.Detail);
        Assert.True(table.TryResolveLocal(Id, 2, out string? local));
        Assert.Equal("4:2", local);
    }

    [Fact]
    public void GeneratedAliveIdentityCanRebindOnlyAfterItsTombstoneHorizon()
    {
        var table = new NetEntityMappingTable();
        IdentityStoreToken token = table.CaptureToken();
        Assert.True(table.Bind(Id, "4:2", token).Succeeded);
        Assert.True(table.Remove(Id, 5, new TombstoneHorizonResult(true, 10), token));
        EntityIdentity alive = new(
            Id.Value, "server-a", 7, 15, 3, "4:3",
            EntityIdentityNamespace.Authoritative, EntityIdentityLifecycle.Alive,
            null, null, null, null);

        Assert.False(table.Bind(alive, 10, token).Succeeded);
        Assert.True(table.Bind(alive, 11, token).Succeeded);
    }

    [Fact]
    public void ResetAdvancesGenerationAndFencesRetainedTokens()
    {
        var table = new NetEntityMappingTable();
        IdentityStoreToken oldToken = table.CaptureToken();
        Assert.True(table.Bind(Id, "4:2", oldToken).Succeeded);

        Assert.True(table.Reset(oldToken, 2));
        Assert.False(table.Bind(Id, "4:2", oldToken).Succeeded);
        Assert.Equal("StaleConnectionGeneration", table.Bind(Id, "4:2", oldToken).GeneratedErrorId);
        Assert.False(table.TryResolveLocal(Id, 2, out _, oldToken));
        Assert.Empty(table.Snapshot(oldToken));

        IdentityStoreToken currentToken = table.CaptureToken();
        Assert.True(table.Bind(Id, "4:2", currentToken).Succeeded);
        Assert.True(table.TryResolveLocal(Id, 2, out string? local, currentToken));
        Assert.Equal("4:2", local);
    }

    [Fact]
    public void InvalidateClosesMappingAndProvisionalStoresForRetainedReferences()
    {
        var table = new NetEntityMappingTable();
        var remaps = new ProvisionalRemapTable();
        IdentityStoreToken tableToken = table.CaptureToken();
        IdentityStoreToken remapToken = remaps.CaptureToken();
        Assert.True(table.Bind(Id, "4:2", tableToken).Succeeded);
        Assert.True(remaps.Add(ProvisionalIdentity(Id), AuthoritativeIdentity(Id2), remapToken).Succeeded);

        Assert.True(table.Invalidate(tableToken));
        Assert.True(remaps.Invalidate(remapToken));
        Assert.False(table.Bind(Id, "4:3", tableToken).Succeeded);
        Assert.False(remaps.Add(ProvisionalIdentity(Id), AuthoritativeIdentity(Id2), remapToken).Succeeded);
        Assert.Empty(table.Snapshot());
        Assert.Empty(remaps.Snapshot());
        Assert.Empty(table.Snapshot(tableToken));
        Assert.Empty(remaps.Snapshot(remapToken));
        Assert.False(table.TryResolveLocal(Id, 2, out _, tableToken));
        Assert.False(remaps.TryResolve(Id, out _, remapToken));
    }

    [Fact]
    public void TombstoneRegistryResetFencesReadsAndWrites()
    {
        var registry = new TombstoneRegistry();
        IdentityStoreToken oldToken = registry.CaptureToken();
        Assert.True(registry.Add(Id, 20, oldToken));

        Assert.True(registry.Reset(oldToken, 2));
        Assert.False(registry.Contains(Id, 20, oldToken));
        Assert.False(registry.Add(Id2, 30, oldToken));
        Assert.Empty(registry.Snapshot(oldToken));
    }

    [Fact]
    public void EqualGenerationStandaloneStoresOwnDistinctTokens()
    {
        var mapping = new NetEntityMappingTable();
        var remaps = new ProvisionalRemapTable();
        var tombstones = new TombstoneRegistry();
        IdentityStoreToken mappingToken = mapping.CaptureToken();
        IdentityStoreToken remapToken = remaps.CaptureToken();
        IdentityStoreToken tombstoneToken = tombstones.CaptureToken();

        Assert.True(mapping.Bind(Id, "4:2", mappingToken).Succeeded);
        Assert.False(remaps.Add(ProvisionalIdentity(Id), AuthoritativeIdentity(Id2), mappingToken).Succeeded);
        Assert.False(tombstones.Add(Id, 20, mappingToken));
        Assert.True(remaps.Add(ProvisionalIdentity(Id), AuthoritativeIdentity(Id2), remapToken).Succeeded);
        Assert.True(tombstones.Add(Id, 20, tombstoneToken));
        Assert.False(mapping.IsTokenCurrent(new IdentityStoreToken(1)));
        Assert.True(mapping.Reset(mappingToken, 2));
        Assert.True(remaps.Reset(remapToken, 2));
        Assert.True(tombstones.Reset(tombstoneToken, 2));

        Assert.False(remaps.Add(ProvisionalIdentity(Id), AuthoritativeIdentity(Id2), remapToken).Succeeded);
        Assert.False(tombstones.Contains(Id, 1, tombstoneToken));
    }

    private static EntityIdentity Identity(EntityIdentityLifecycle lifecycle, ulong? until, ulong generation)
    {
        return new EntityIdentity(
            lifecycle == EntityIdentityLifecycle.Destroyed ? Id.Value : Id2.Value,
            "server-a", 7, 15, generation, "4:" + generation,
            EntityIdentityNamespace.Authoritative, lifecycle, until, null, 10, null);
    }

    private static EntityIdentity ProvisionalIdentity(NetEntityId id) =>
        new(id.Value, "client-provisional", 7, 15, 1, "6:1",
            EntityIdentityNamespace.Provisional, EntityIdentityLifecycle.Alive,
            null, null, null, null);

    private static EntityIdentity AuthoritativeIdentity(NetEntityId id) =>
        new(id.Value, "server-a", 7, 15, 1, "7:1",
            EntityIdentityNamespace.Authoritative, EntityIdentityLifecycle.Alive,
            null, null, null, null);
}
