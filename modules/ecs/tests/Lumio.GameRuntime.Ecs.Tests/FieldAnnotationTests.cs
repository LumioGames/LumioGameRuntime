using System;
using System.Reflection;
using Lumio.GameRuntime.Ecs.Annotations;
using Xunit;

namespace Lumio.GameRuntime.Ecs.Tests;

public sealed class FieldAnnotationTests
{
    [Fact]
    public void PersistenceKindCoversC2TokensAndDefaultsToEphemeral()
    {
        PersistenceKind[] values = Enum.GetValues<PersistenceKind>();
        Assert.Equal(2, values.Length);
        Assert.Equal("ephemeral", FieldAnnotationRules.Token(PersistenceKind.Ephemeral));
        Assert.Equal("persistent", FieldAnnotationRules.Token(PersistenceKind.Persistent));
        Assert.Equal(PersistenceKind.Ephemeral, default(PersistenceKind));
        Assert.Equal(FieldAnnotationRules.DefaultPersistence, FieldAnnotationRules.Token(default(PersistenceKind)));
    }

    [Fact]
    public void ReplicationKindCoversC2TokensAndDefaultsToNotReplicated()
    {
        ReplicationKind[] values = Enum.GetValues<ReplicationKind>();
        Assert.Equal(2, values.Length);
        Assert.Equal("not-replicated", FieldAnnotationRules.Token(ReplicationKind.NotReplicated));
        Assert.Equal("replicated", FieldAnnotationRules.Token(ReplicationKind.Replicated));
        Assert.Equal(ReplicationKind.NotReplicated, default(ReplicationKind));
        Assert.Equal(FieldAnnotationRules.DefaultReplication, FieldAnnotationRules.Token(default(ReplicationKind)));
    }

    [Fact]
    public void VisibilityKindCoversC2TokensAndDefaultsToServerOnly()
    {
        VisibilityKind[] values = Enum.GetValues<VisibilityKind>();
        Assert.Equal(4, values.Length);
        Assert.Equal("server-only", FieldAnnotationRules.Token(VisibilityKind.ServerOnly));
        Assert.Equal("room-public", FieldAnnotationRules.Token(VisibilityKind.RoomPublic));
        Assert.Equal("aoi-scoped", FieldAnnotationRules.Token(VisibilityKind.AoiScoped));
        Assert.Equal("claim-scoped", FieldAnnotationRules.Token(VisibilityKind.ClaimScoped));
        Assert.Equal(VisibilityKind.ServerOnly, default(VisibilityKind));
        Assert.Equal(FieldAnnotationRules.DefaultVisibility, FieldAnnotationRules.Token(default(VisibilityKind)));
    }

    [Fact]
    public void UnmarkedDefaultsAreNeverOnWireAndNeverPersisted()
    {
        Assert.Equal("ephemeral", FieldAnnotationRules.DefaultPersistence);
        Assert.Equal("not-replicated", FieldAnnotationRules.DefaultReplication);
        Assert.Equal("server-only", FieldAnnotationRules.DefaultVisibility);
    }

    [Fact]
    public void LastMessageFieldsArePersistOnlyServerOnly()
    {
        AssertPersistOnly(typeof(ChatComponent).GetProperty(nameof(ChatComponent.LastMessageText)));
        AssertPersistOnly(typeof(ChatComponent).GetProperty(nameof(ChatComponent.LastMessageTick)));
        AssertPersistOnly(typeof(ChatComponent).GetProperty(nameof(ChatComponent.LastMessagePersistOnly)));
    }

    [Fact]
    public void EntityTypeIsEphemeralReplicatedRoomPublic()
    {
        PropertyInfo? property = typeof(EntityIdentity).GetProperty(nameof(EntityIdentity.EntityType));
        Assert.NotNull(property);
        Assert.Null(property.GetCustomAttribute<PersistAttribute>());
        ReplicateAttribute replicate = Assert.IsType<ReplicateAttribute>(property.GetCustomAttribute<ReplicateAttribute>());
        Assert.Equal(ReplicationKind.Replicated, replicate.Kind);
        VisibilityAttribute visibility = Assert.IsType<VisibilityAttribute>(property.GetCustomAttribute<VisibilityAttribute>());
        Assert.Equal(VisibilityKind.RoomPublic, visibility.Kind);
        AttributeValueTypeAttribute valueType = Assert.IsType<AttributeValueTypeAttribute>(property.GetCustomAttribute<AttributeValueTypeAttribute>());
        Assert.Equal("enum:entityType", valueType.ValueType);
    }

    [Fact]
    public void AccountIdHasNoFieldAnnotations()
    {
        PropertyInfo? property = typeof(EntityIdentity).GetProperty(nameof(EntityIdentity.AccountId));
        Assert.NotNull(property);
        Assert.Null(property.GetCustomAttribute<PersistAttribute>());
        Assert.Null(property.GetCustomAttribute<ReplicateAttribute>());
        Assert.Null(property.GetCustomAttribute<VisibilityAttribute>());
        Assert.Null(property.GetCustomAttribute<AttributeValueTypeAttribute>());
    }

    [Fact]
    public void ReplicatedPlusServerOnlyIsIllegal()
    {
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => FieldAnnotationRules.Validate("Probe.field", "ephemeral", "replicated", "server-only"));
        Assert.Contains("replicated", error.Message, StringComparison.Ordinal);
        Assert.Contains("server-only", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("aoi-scoped")]
    [InlineData("claim-scoped")]
    [InlineData("room-public")]
    public void VisibleButNotReplicatedIsIllegal(string visibility)
    {
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => FieldAnnotationRules.Validate("Probe.field", "ephemeral", "not-replicated", visibility));
        Assert.Contains("not-replicated", error.Message, StringComparison.Ordinal);
        Assert.Contains(visibility, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PersistOnlyServerOnlyIsLegal()
    {
        FieldAnnotationRules.Validate("ChatComponent.lastMessageText", "persistent", "not-replicated", "server-only");
    }

    private static void AssertPersistOnly(PropertyInfo? property)
    {
        Assert.NotNull(property);
        PersistAttribute persist = Assert.IsType<PersistAttribute>(property.GetCustomAttribute<PersistAttribute>());
        Assert.Equal(PersistenceKind.Persistent, persist.Kind);
        Assert.Null(property.GetCustomAttribute<ReplicateAttribute>());
        Assert.Null(property.GetCustomAttribute<VisibilityAttribute>());
    }
}
