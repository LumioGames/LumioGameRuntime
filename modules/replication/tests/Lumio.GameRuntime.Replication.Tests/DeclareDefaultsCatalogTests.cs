using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Lumio.GameRuntime.Ecs;
using Lumio.GameRuntime.Ecs.Annotations;
using Lumio.GameRuntime.Replication.Binding;
using Lumio.GameRuntime.Samples.Username;
using Xunit;

namespace Lumio.GameRuntime.Replication.Tests;

public sealed class DeclareDefaultsCatalogTests
{
    public DeclareDefaultsCatalogTests()
    {
        EcsRegistry.Current = GeneratedRegistry.Instance;
    }

    [Fact]
    public void CreateLoadsDeclarationsFromGeneratedCatalog()
    {
        using EntityBindingQuery sut = EntityBindingQuery.Create();
        FieldInfo field = typeof(EntityBindingQuery).GetField("_declarations", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var loaded = Assert.IsAssignableFrom<IReadOnlyDictionary<string, AttributeDeclaration>>(field.GetValue(sut));
        IReadOnlyList<FieldAttributeDeclaration> catalog = sut.Manager.Registry.AttributeDeclarations;
        string jsonPath = Path.Combine(FindRepoRoot(), "modules", "ecs", "generated", "attribute-declarations.json");
        IReadOnlyList<FieldAttributeDeclaration> committed = AttributeDeclarationCatalog.Parse(File.ReadAllText(jsonPath));

        Assert.Equal(catalog.Count, loaded.Count);
        Assert.Equal(catalog.Count, committed.Count);
        foreach (FieldAttributeDeclaration row in catalog)
        {
            Assert.True(loaded.TryGetValue(row.AttributeId, out AttributeDeclaration declaration));
            Assert.Equal(row.ValueType, declaration.ValueType);
            Assert.Equal(row.Persistence, declaration.Persistence);
            Assert.Equal(row.Replication, declaration.Replication);
            Assert.Equal(row.Visibility, declaration.Visibility);
        }

        Assert.True(loaded.ContainsKey("ChatComponent.lastMessageText"));
        Assert.True(loaded.ContainsKey("IdentityComponent.name"));
        Assert.False(loaded.ContainsKey("EntityIdentity.accountId"));
    }

    [Fact]
    public void AccountIdQueryIsUndeclaredAttribute()
    {
        using EntityBindingQuery sut = EntityBindingQuery.Create();
        BindingQueryResult admit = sut.Admit("C1", "acct-07", "room-01", "player");
        Assert.Equal("ok", admit.Outcome);
        Assert.True(admit.Binding.HasValue);

        BindingQueryResult result = sut.QueryAttribute(
            new AttributeQueryRequest
            {
                CallerScope = "server-authoritative",
                RoomId = "room-01",
                NetEntityId = admit.Binding.Value.NetEntityId,
                AttributeId = "EntityIdentity.accountId",
            });
        Assert.Equal("request_error", result.Outcome);
        Assert.Equal("undeclared_attribute", result.Code);
    }

    [Fact]
    public void DeclareDefaultsDoesNotHardcodeAttributeRows()
    {
        string path = Path.Combine(
            FindRepoRoot(),
            "modules",
            "replication",
            "src",
            "Lumio.GameRuntime.Replication",
            "Binding",
            "EntityBindingQuery.cs");
        string source = File.ReadAllText(path);
        Assert.Contains("AttributeDeclarations", source, StringComparison.Ordinal);
        Assert.DoesNotContain("_values", source, StringComparison.Ordinal);
    }

    private static string FindRepoRoot()
    {
        string? current = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(current))
        {
            if (File.Exists(Path.Combine(current, "Directory.Build.props")) &&
                Directory.Exists(Path.Combine(current, "modules")))
                return current;
            current = Directory.GetParent(current)?.FullName;
        }

        throw new InvalidOperationException("Repository root was not found from " + AppContext.BaseDirectory);
    }
}
