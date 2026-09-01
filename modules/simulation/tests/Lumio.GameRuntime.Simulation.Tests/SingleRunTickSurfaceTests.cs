using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Lumio.GameRuntime.Ecs;
using Lumio.GameRuntime.Simulation.Session;
using Lumio.GameRuntime.Simulation.Tick;
using Xunit;

namespace Lumio.GameRuntime.Simulation.Tests;

public sealed class SingleRunTickSurfaceTests
{
    [Fact]
    public void Runtime_session_exposes_one_tick_entry_only()
    {
        MethodInfo[] methods = typeof(IRuntimeSession).GetMethods();
        Assert.Single(methods.Where(method => method.Name == nameof(IRuntimeSession.RunTick)));
        Assert.DoesNotContain(methods, method => method.Name.Contains("Phase", StringComparison.Ordinal));
        Assert.DoesNotContain(methods, method => method.Name.Contains("Clock", StringComparison.Ordinal));
        Assert.DoesNotContain(methods, method => method.Name.Contains("Revision", StringComparison.Ordinal));
    }

    [Fact]
    public void RuntimeSessionPublicSurfaceIsSessionIdentityStateRunTickAndDispose()
    {
        Type type = typeof(IRuntimeSession);
        MethodInfo[] methods = type.GetMethods();
        string[] names = methods
            .Select(method => method.Name)
            .Where(name => name != nameof(IDisposable.Dispose))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(new[] { "RunTick", "get_SessionId", "get_State", "get_WorldId" }, names);
        Assert.True(typeof(IDisposable).IsAssignableFrom(type));
        Assert.True(typeof(IRuntimeSession).IsAssignableFrom(typeof(SimulationSession)));
        Assert.DoesNotContain(methods, method => method.Name is "RunPhase" or "AdvanceClock" or "SetRevision" or "CommitVoxel");

        MethodInfo runTick = Assert.Single(methods, method => method.Name == nameof(IRuntimeSession.RunTick));
        ParameterInfo parameter = Assert.Single(runTick.GetParameters());
        Assert.True(parameter.GetRequiredCustomModifiers().Length == 0 || parameter.IsIn);
        Assert.Equal(typeof(TickInput).MakeByRefType(), parameter.ParameterType);
        Assert.True(parameter.IsIn);
        Assert.Equal(typeof(TickRunResult), runTick.ReturnType);

        Assert.Equal(typeof(string), type.GetProperty(nameof(IRuntimeSession.SessionId))!.PropertyType);
        Assert.Equal(typeof(WorldId), type.GetProperty(nameof(IRuntimeSession.WorldId))!.PropertyType);
        Assert.Equal(typeof(SimulationSessionState), type.GetProperty(nameof(IRuntimeSession.State))!.PropertyType);
    }

    [Fact]
    public void SimulationSessionDoesNotExposePhaseByPhasePublicTickMethods()
    {
        MethodInfo[] methods = typeof(SimulationSession).GetMethods(
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly);

        Assert.Single(methods, method => method.Name == nameof(SimulationSession.RunTick));
        Assert.DoesNotContain(methods, method => method.Name is "RunPhase" or "AdvanceClock" or "SetRevision" or "CommitVoxel");
        Assert.DoesNotContain(methods, method => method.Name.Contains("Phase", StringComparison.Ordinal));
        Assert.DoesNotContain(methods, method => method.Name.Contains("Clock", StringComparison.Ordinal));
    }

    [Fact]
    public void TickInputWrapsHostTickRequestWithoutASecondProtocol()
    {
        var request = new HostTickRequest(7, 1, Array.Empty<OpaqueIngressView>());
        var input = new TickInput(request);

        Assert.Equal(request, input.Request);
        Assert.Equal(request.TickId, input.Request.TickId);
        TickInput converted = request;
        Assert.Equal(request, converted.Request);
    }

    [Fact]
    public void PublicSurfaceAndProjectGraphHaveNoHostWallClockTypes()
    {
        Assembly assembly = typeof(IRuntimeSession).Assembly;
        Assert.DoesNotContain(
            assembly.GetReferencedAssemblies(),
            name => name.Name is not null &&
                (name.Name.Contains("WallClock", StringComparison.OrdinalIgnoreCase) ||
                 name.Name.Contains("HostClock", StringComparison.OrdinalIgnoreCase) ||
                 name.Name.Equals("Lumio.GameRuntime.Host", StringComparison.Ordinal)));

        foreach (Type type in assembly.GetExportedTypes())
        {
            Assert.DoesNotContain("WallClock", type.Name, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("DateTime", type.Name, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Stopwatch", type.Name, StringComparison.OrdinalIgnoreCase);
            foreach (MethodInfo method in type.GetMethods(
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                Assert.DoesNotContain("Clock", method.Name, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("DateTime", method.Name, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("WallClock", method.Name, StringComparison.OrdinalIgnoreCase);
            }
        }

        string csproj = LocateSimulationCsproj();
        string text = File.ReadAllText(csproj);
        Assert.DoesNotContain("WallClock", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Lumio.GameRuntime.Host", text, StringComparison.Ordinal);
    }

    [Fact]
    public void SimulationProjectReferencesRequiredRuntimeModulesAndOmitsMissingPersistence()
    {
        string csproj = LocateSimulationCsproj();
        string text = File.ReadAllText(csproj);
        string[] required =
        {
            "Lumio.GameRuntime.Ecs",
            "Lumio.GameRuntime.Command",
            "Lumio.GameRuntime.Coordination",
            "Lumio.GameRuntime.Gas",
            "Lumio.GameRuntime.Replication",
            "Lumio.GameRuntime.Config",
            "Lumio.GameRuntime.Observability",
            "Lumio.GameRuntime.GeneratedContracts"
        };
        foreach (string name in required)
            Assert.Contains(name, text, StringComparison.Ordinal);

        string persistenceCsproj = Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(csproj)!,
            "..",
            "..",
            "..",
            "persistence",
            "src",
            "Lumio.GameRuntime.Persistence",
            "Lumio.GameRuntime.Persistence.csproj"));
        if (File.Exists(persistenceCsproj))
            Assert.Contains("Lumio.GameRuntime.Persistence", text, StringComparison.Ordinal);
        else
            Assert.DoesNotContain("Lumio.GameRuntime.Persistence", text, StringComparison.Ordinal);
    }

    private static string LocateSimulationCsproj()
    {
        string path = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            "..",
            "src",
            "Lumio.GameRuntime.Simulation",
            "Lumio.GameRuntime.Simulation.csproj"));
        Assert.True(File.Exists(path), "Simulation production csproj must be locatable from the test output directory.");
        return path;
    }
}
