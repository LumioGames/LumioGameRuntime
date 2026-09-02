using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Lumio.GameRuntime.Ecs.Annotations;
using Xunit;

namespace Lumio.GameRuntime.Ecs.Tests;

public sealed class AttributeDeclarationCatalogTests
{
    [Fact]
    public void ScannerEmitsOnlyAnnotatedFieldsInC2Shape()
    {
        IReadOnlyList<FieldAttributeDeclaration> rows = AttributeDeclarationScanner.Scan(typeof(ChatComponent).Assembly);

        Assert.Equal(6, rows.Count);
        Assert.Equal("ChatComponent.lastMessagePersistOnly", rows[0].AttributeId);
        Assert.Equal("ChatComponent.lastMessageText", rows[1].AttributeId);
        Assert.Equal("ChatComponent.lastMessageTick", rows[2].AttributeId);
        Assert.Equal("EntityIdentity.claimedMark", rows[3].AttributeId);
        Assert.Equal("EntityIdentity.entityType", rows[4].AttributeId);
        Assert.Equal("EntityIdentity.unmappedMark", rows[5].AttributeId);
        Assert.DoesNotContain(rows, static row => row.AttributeId == "EntityIdentity.accountId");
        Assert.Equal(new FieldAttributeDeclaration("ChatComponent.lastMessageText", "utf8-string", "persistent", "not-replicated", "server-only"), rows[1]);
        Assert.Equal(new FieldAttributeDeclaration("ChatComponent.lastMessageTick", "u64", "persistent", "not-replicated", "server-only"), rows[2]);
        Assert.Equal(new FieldAttributeDeclaration("EntityIdentity.entityType", "enum:entityType", "ephemeral", "replicated", "room-public"), rows[4]);
    }

    [Fact]
    public void CanonicalJsonIsDeterministicSortedLfAndTrailingNewline()
    {
        IReadOnlyList<FieldAttributeDeclaration> rows = AttributeDeclarationScanner.Scan(typeof(ChatComponent).Assembly);
        string first = AttributeDeclarationJson.Format(rows);
        string second = AttributeDeclarationJson.Format(rows);

        Assert.Equal(first, second);
        Assert.Equal(Sha256(first), Sha256(second));
        Assert.DoesNotContain('\r', first);
        Assert.EndsWith("\n", first, StringComparison.Ordinal);
        Assert.Equal(ExpectedProductJson, first);
        Assert.True(IsObjectKeysSorted(first));
    }

    [Fact]
    public void ProductFileMatchesScannerOutput()
    {
        string path = Path.Combine(FindRepoRoot(), "modules", "ecs", "generated", "attribute-declarations.json");
        Assert.True(File.Exists(path), path);
        string onDisk = File.ReadAllText(path, Encoding.UTF8).Replace("\r\n", "\n", StringComparison.Ordinal);
        string generated = AttributeDeclarationJson.Format(AttributeDeclarationScanner.Scan(typeof(ChatComponent).Assembly));
        Assert.Equal(generated, onDisk);
        Assert.DoesNotContain('\r', generated);
        Assert.EndsWith("\n", generated, StringComparison.Ordinal);
    }

    [Fact]
    public void EmbeddedCatalogMatchesProductFileAndOmitsAccountId()
    {
        string path = Path.Combine(FindRepoRoot(), "modules", "ecs", "generated", "attribute-declarations.json");
        string product = File.ReadAllText(path, Encoding.UTF8).Replace("\r\n", "\n", StringComparison.Ordinal);
        IReadOnlyList<FieldAttributeDeclaration> fromFile = AttributeDeclarationCatalog.Parse(product);
        IReadOnlyList<FieldAttributeDeclaration> embedded = AttributeDeclarationCatalog.LoadEmbedded();

        Assert.Equal(fromFile, embedded);
        Assert.DoesNotContain(embedded, static row => row.AttributeId.Equals("EntityIdentity.accountId", StringComparison.Ordinal));
        Assert.Equal(AttributeDeclarationScanner.Scan(typeof(ChatComponent).Assembly), embedded);
    }

    private static bool IsObjectKeysSorted(string json)
    {
        string[] keys = { "attributeId", "persistence", "replication", "valueType", "visibility" };
        int last = -1;
        foreach (string key in keys)
        {
            int at = json.IndexOf('"' + key + '"', StringComparison.Ordinal);
            if (at < 0 || at < last) return false;
            last = at;
        }

        return true;
    }

    private static string Sha256(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

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

    private const string ExpectedProductJson =
        "[\n" +
        "  {\n" +
        "    \"attributeId\": \"ChatComponent.lastMessagePersistOnly\",\n" +
        "    \"persistence\": \"persistent\",\n" +
        "    \"replication\": \"not-replicated\",\n" +
        "    \"valueType\": \"utf8-string\",\n" +
        "    \"visibility\": \"server-only\"\n" +
        "  },\n" +
        "  {\n" +
        "    \"attributeId\": \"ChatComponent.lastMessageText\",\n" +
        "    \"persistence\": \"persistent\",\n" +
        "    \"replication\": \"not-replicated\",\n" +
        "    \"valueType\": \"utf8-string\",\n" +
        "    \"visibility\": \"server-only\"\n" +
        "  },\n" +
        "  {\n" +
        "    \"attributeId\": \"ChatComponent.lastMessageTick\",\n" +
        "    \"persistence\": \"persistent\",\n" +
        "    \"replication\": \"not-replicated\",\n" +
        "    \"valueType\": \"u64\",\n" +
        "    \"visibility\": \"server-only\"\n" +
        "  },\n" +
        "  {\n" +
        "    \"attributeId\": \"EntityIdentity.claimedMark\",\n" +
        "    \"persistence\": \"ephemeral\",\n" +
        "    \"replication\": \"replicated\",\n" +
        "    \"valueType\": \"utf8-string\",\n" +
        "    \"visibility\": \"claim-scoped\"\n" +
        "  },\n" +
        "  {\n" +
        "    \"attributeId\": \"EntityIdentity.entityType\",\n" +
        "    \"persistence\": \"ephemeral\",\n" +
        "    \"replication\": \"replicated\",\n" +
        "    \"valueType\": \"enum:entityType\",\n" +
        "    \"visibility\": \"room-public\"\n" +
        "  },\n" +
        "  {\n" +
        "    \"attributeId\": \"EntityIdentity.unmappedMark\",\n" +
        "    \"persistence\": \"ephemeral\",\n" +
        "    \"replication\": \"replicated\",\n" +
        "    \"valueType\": \"utf8-string\",\n" +
        "    \"visibility\": \"room-public\"\n" +
        "  }\n" +
        "]\n";
}
