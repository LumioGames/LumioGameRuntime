using System;
using System.Linq;
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
}
