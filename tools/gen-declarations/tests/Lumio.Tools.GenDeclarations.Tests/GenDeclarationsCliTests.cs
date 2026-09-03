using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace Lumio.Tools.GenDeclarations.Tests;

public sealed class GenDeclarationsCliTests
{
    [Fact]
    public void SourceScanRejectsDuplicateWorldEntity()
    {
        string dir = Path.Combine(Path.GetTempPath(), "lumio-ecs-illegal-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "First.cs"), """
            using Lumio.GameRuntime.Ecs;
            [EntityType(Mode.CS, World = true)]
            [Has(typeof(WorldSaveComponent))]
            public abstract class FirstWorldEntity { }
            """);
        File.WriteAllText(Path.Combine(dir, "Second.cs"), """
            using Lumio.GameRuntime.Ecs;
            [EntityType(Mode.CS, World = true)]
            [Has(typeof(WorldSaveComponent))]
            public abstract class SecondWorldEntity { }
            """);
        try
        {
            RunResult result = RunCli("--sources", dir, "--side", "server", "--output-dir", Path.Combine(dir, "out"));
            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains("World = true", result.StdErr + result.StdOut, StringComparison.Ordinal);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void SourceScanRejectsNonSyncStateInSharedFile()
    {
        string dir = Path.Combine(Path.GetTempPath(), "lumio-ecs-shared-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "WorldEntity.cs"), """
            using Lumio.GameRuntime.Ecs;
            [EntityType(Mode.CS, World = true)]
            [Has(typeof(WorldSaveComponent))]
            public abstract class WorldEntity { }
            """);
        File.WriteAllText(Path.Combine(dir, "ChatComponent.cs"), """
            using Lumio.GameRuntime.Ecs;
            [EcsComponent]
            public sealed partial class ChatComponent : Component
            {
                public string LastMessageText = "";
            }
            """);
        try
        {
            RunResult result = RunCli("--sources", dir, "--side", "server", "--output-dir", Path.Combine(dir, "out"));
            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains("non-Sync state field", result.StdErr + result.StdOut, StringComparison.Ordinal);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void CliWritesIdenticalBytesOnTwoRuns()
    {
        string repo = FindRepoRoot();
        string sources = Path.Combine(repo, "modules", "ecs", "samples", "username");
        string first = Path.Combine(Path.GetTempPath(), "lumio-decl-" + Guid.NewGuid().ToString("N"));
        string second = Path.Combine(Path.GetTempPath(), "lumio-decl-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(first);
        Directory.CreateDirectory(second);
        try
        {
            RunResult run1 = RunCli("--sources", sources, "--side", "server", "--output-dir", first, "--output", Path.Combine(first, "a.json"));
            RunResult run2 = RunCli("--sources", sources, "--side", "server", "--output-dir", second, "--output", Path.Combine(second, "a.json"));
            Assert.Equal(0, run1.ExitCode);
            Assert.Equal(0, run2.ExitCode);
            byte[] bytes1 = File.ReadAllBytes(Path.Combine(first, "a.json"));
            byte[] bytes2 = File.ReadAllBytes(Path.Combine(second, "a.json"));
            Assert.Equal(bytes1, bytes2);
            Assert.DoesNotContain(bytes1, static value => value == (byte)'\r');
            Assert.Equal((byte)'\n', bytes1[^1]);
        }
        finally
        {
            TryDeleteDir(first);
            TryDeleteDir(second);
        }
    }

    private static RunResult RunCli(params string[] args)
    {
        string repoRoot = FindRepoRoot();
        string project = Path.Combine(repoRoot, "tools", "gen-declarations", "Lumio.Tools.GenDeclarations.csproj");
        var start = new ProcessStartInfo
        {
            FileName = "dotnet",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = repoRoot,
        };
        start.ArgumentList.Add("run");
        start.ArgumentList.Add("--project");
        start.ArgumentList.Add(project);
        start.ArgumentList.Add("--");
        foreach (string arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start);
        Assert.NotNull(process);
        string stdout = process.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return new RunResult(process.ExitCode, stdout, stderr);
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

    private static void TryDeleteDir(string path)
    {
        try { Directory.Delete(path, recursive: true); } catch (IOException) { }
    }

    private readonly record struct RunResult(int ExitCode, string StdOut, string StdErr);
}
