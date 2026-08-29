using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Lumio.GameRuntime.GeneratedContracts;
using Xunit;

namespace Lumio.GameRuntime.GeneratedContracts.Tests;

public sealed class GeneratedContractBaselineTests
{
    [Fact]
    public void ManifestMatchesPublishedV14ContractBoundary()
    {
        Assert.Equal("LGE-V1.4-2026-08-27", GeneratedContractManifest.ArchitectureBaselineId);
        Assert.Matches("^[0-9a-f]{40}$", GeneratedContractManifest.ArchitectureSourceCommit);
        Assert.Equal(1, GeneratedContractManifest.SchemaEpoch);
        Assert.Matches("^[0-9a-f]{64}$", GeneratedContractManifest.CompilerHash);
        Assert.Matches("^[0-9a-f]{64}$", GeneratedContractManifest.InputHash);
        Assert.Matches("^[0-9a-f]{64}$", GeneratedContractManifest.SchemaRegistrySha256);
        Assert.Matches("^[0-9a-f]{64}$", GeneratedContractManifest.IdRegistrySha256);
        Assert.Matches("^[0-9a-f]{64}$", GeneratedContractManifest.FixtureRegistrySha256);
    }

    [Fact]
    public void ManifestContainsAllPublishedCsharpArtifactsAndHashes()
    {
        Assert.Equal(6, GeneratedContractManifest.ArtifactIds.Count);
        Assert.Equal(6, GeneratedContractManifest.ArtifactKinds.Count);
        Assert.Equal(6, GeneratedContractManifest.ArtifactHashes.Count);
        Assert.All(GeneratedContractManifest.ArtifactIds, artifactId =>
        {
            Assert.True(GeneratedContractManifest.ArtifactHashes.ContainsKey(artifactId));
            Assert.Matches("^[0-9a-f]{64}$", GeneratedContractManifest.ArtifactHashes[artifactId]);
        });
    }

    [Fact]
    public void RequiredRuntimeContractsArePresentInTheSchemaCatalog()
    {
        Assert.NotEmpty(GeneratedContractManifest.SchemaIds);
        Assert.All(RequiredContractNames.All, name => Assert.True(GeneratedContractCatalog.Contains(name), name));
        Assert.Equal(GeneratedContractManifest.RequiredContractNames.OrderBy(value => value), RequiredContractNames.All.OrderBy(value => value));
    }

    // 生成器把同一份 provenance 发射成两个文件:manifest JSON 与 GeneratedContractManifest.cs。
    // 二者只要有一处被手改就会发散,所以这条断言是「生成物不得手改」这条红线的可执行守护——
    // 它不需要 git 或 python,在任何跑得起测试的环境里都成立。
    [Fact]
    public void ManifestJsonAndGeneratedConstantsAgreeOnProvenance()
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(ManifestJsonPath()));
        JsonElement manifest = document.RootElement;

        Assert.Equal(GeneratedContractManifest.ArchitectureBaselineId, manifest.GetProperty("architectureBaselineId").GetString());
        Assert.Equal(GeneratedContractManifest.ArchitectureSourceCommit, manifest.GetProperty("architectureSourceCommit").GetString());
        Assert.Equal(GeneratedContractManifest.SchemaEpoch, manifest.GetProperty("schemaEpoch").GetInt32());
        Assert.Equal(GeneratedContractManifest.CompilerHash, manifest.GetProperty("compilerHash").GetString());
        Assert.Equal(GeneratedContractManifest.InputHash, manifest.GetProperty("inputHash").GetString());
        Assert.Equal(GeneratedContractManifest.SchemaRegistrySha256, manifest.GetProperty("schemaRegistrySha256").GetString());
        Assert.Equal(GeneratedContractManifest.IdRegistrySha256, manifest.GetProperty("idRegistrySha256").GetString());
        Assert.Equal(GeneratedContractManifest.FixtureRegistrySha256, manifest.GetProperty("fixtureRegistrySha256").GetString());

        AssertSequence(GeneratedContractManifest.ArtifactIds, manifest, "artifactIds");
        AssertSequence(GeneratedContractManifest.ArtifactKinds, manifest, "artifactKinds");
        AssertSequence(GeneratedContractManifest.SchemaIds, manifest, "schemaIds");
        AssertSequence(GeneratedContractManifest.RequiredContractNames, manifest, "requiredContractNames");

        // 两个方向都要比:只从 JSON 侧遍历的话,常量里多出的键会漏掉,
        // 而按键取值又会抛 KeyNotFoundException 而不是给出可读的断言失败。
        var jsonHashes = manifest.GetProperty("artifactHashes").EnumerateObject()
            .ToDictionary(entry => entry.Name, entry => entry.Value.GetString(), StringComparer.Ordinal);
        Assert.Equal(
            GeneratedContractManifest.ArtifactHashes.OrderBy(pair => pair.Key, StringComparer.Ordinal),
            jsonHashes.OrderBy(pair => pair.Key, StringComparer.Ordinal));
    }

    private static void AssertSequence(IReadOnlyList<string> constants, JsonElement manifest, string property)
    {
        Assert.Equal(constants, manifest.GetProperty(property).EnumerateArray().Select(value => value.GetString()));
    }

    private static string ManifestJsonPath()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "generated-contract-manifest.json");
        Assert.True(File.Exists(path), $"generated-contract-manifest.json was not copied next to the test assembly: {path}");
        return path;
    }
}
