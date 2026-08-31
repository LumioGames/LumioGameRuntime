using System;
using System.Collections.Generic;
using System.Reflection;
using Lumio.GameRuntime.Ecs;
using Lumio.Gen.ContractTypes;
using Xunit;

namespace Lumio.GameRuntime.Ecs.Tests;

public sealed class ContractSurfaceTests
{
    [Fact]
    public void EveryEcsDiagnosticMapsToAGeneratedStableError()
    {
        Assembly assembly = typeof(EcsModule).Assembly;
        Type diagnosticType = Assert.Single(
            assembly.GetTypes(), static type => type.Name == "EcsDiagnosticReason");
        Type boundaryType = Assert.Single(
            assembly.GetTypes(), static type => type.Name == "EcsBoundaryErrors");
        MethodInfo map = Assert.Single(
            boundaryType.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic),
            static method => method.Name == "For" && method.GetParameters().Length == 1);
        var stableErrors = new HashSet<string>(Catalog.StableErrorIds, StringComparer.Ordinal);

        foreach (object diagnostic in Enum.GetValues(diagnosticType))
        {
            string stableError = Assert.IsType<string>(map.Invoke(null, new[] { diagnostic }));
            Assert.Contains(stableError, stableErrors);
        }
    }

    [Fact]
    public void ErrorIdentityRejectsCodesOutsideTheGeneratedCatalog()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ErrorIdentity("EcsInventedError"));
    }

    [Fact]
    public void PublicSurfaceDoesNotExportPlaceholderContractVocabulary()
    {
        string[] forbiddenTypes =
        {
            "ComponentFieldDefinition",
            "ComponentFieldId",
            "ComponentTypeDefinition",
            "ComponentTypeId",
            "ComponentTypeRegistry",
            "EcsErrorCodes",
            "EntityReference",
            "EntityTypeDefinition",
            "GeneratedComponentSchemaView",
            "ReferenceFallback",
            "ReferenceResolution",
            "ReferenceResolutionState",
            "SchemaEpoch"
        };
        Type[] exportedTypes = typeof(EcsModule).Assembly.GetExportedTypes();

        foreach (string forbiddenType in forbiddenTypes)
            Assert.DoesNotContain(exportedTypes, type => type.Name == forbiddenType);
    }

    [Fact]
    public void PublicSurfaceDoesNotExposeEcsOwnedNetworkIdentity()
    {
        foreach (Type type in typeof(EcsModule).Assembly.GetExportedTypes())
        {
            Assert.DoesNotContain("NetworkId", type.Name, StringComparison.Ordinal);
            Assert.DoesNotContain(type.GetMembers(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public),
                static member => member.Name.Contains("NetworkId", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void PublicWorldDoesNotAcceptArbitrarySchemaRegistration()
    {
        MethodInfo[] publicMethods = typeof(EcsWorld).GetMethods(BindingFlags.Instance | BindingFlags.Public);

        Assert.DoesNotContain(publicMethods,
            static method => method.Name.StartsWith("Register", StringComparison.Ordinal));
    }

    [Fact]
    public void ReferenceStorageAdapterIsNotExportedAsProductionApi()
    {
        Assert.DoesNotContain(typeof(EcsModule).Assembly.GetExportedTypes(),
            static type => type.Name == "ReferenceWorldStorageAdapter");
    }
}
