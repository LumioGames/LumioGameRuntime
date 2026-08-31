using System;
using System.Linq;
using System.Reflection;
using Xunit;

namespace Lumio.GameRuntime.Coordination.Tests;

public sealed class VoxelAdapterSurfaceTests
{
    [Fact]
    public void SubstituteVoxelContractTypesAreNotExported()
    {
        Assembly[] assemblies =
        {
            Assembly.Load("Lumio.GameRuntime.Coordination"),
            Assembly.Load("Lumio.GameRuntime.Coordination.VoxelAdapters")
        };

        foreach (Assembly assembly in assemblies)
        {
            Type[] exported = assembly.GetExportedTypes();
            Assert.DoesNotContain(exported, type => IsVoxelContractName(type.Name));
            foreach (Type type in exported)
            {
                Assert.DoesNotContain(type.GetInterfaces(), candidate => IsVoxelContractName(candidate.Name));
                foreach (ConstructorInfo constructor in type.GetConstructors())
                {
                    Assert.DoesNotContain(constructor.GetParameters(), parameter =>
                        IsVoxelContractName(parameter.ParameterType.Name));
                }
                foreach (MethodInfo method in type.GetMethods(BindingFlags.Instance | BindingFlags.Static |
                    BindingFlags.Public | BindingFlags.DeclaredOnly))
                {
                    Assert.False(IsVoxelContractName(method.ReturnType.Name));
                    Assert.DoesNotContain(method.GetParameters(), parameter =>
                        IsVoxelContractName(parameter.ParameterType.Name));
                }
                foreach (PropertyInfo property in type.GetProperties(BindingFlags.Instance | BindingFlags.Static |
                    BindingFlags.Public | BindingFlags.DeclaredOnly))
                    Assert.False(IsVoxelContractName(property.PropertyType.Name));
            }
        }
    }

    private static bool IsVoxelContractName(string name) =>
        name == "IVoxelWorldPort" ||
        name.Contains("VoxelPrepare", StringComparison.Ordinal) ||
        name.Contains("VoxelCommit", StringComparison.Ordinal) ||
        name.Contains("VoxelAbort", StringComparison.Ordinal) ||
        name.Contains("VoxelParticipant", StringComparison.Ordinal) ||
        name.StartsWith("GeneratedVoxel", StringComparison.Ordinal);
}
