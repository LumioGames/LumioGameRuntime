using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Lumio.GameRuntime.Ecs.Annotations;
using Lumio.Tools.GenDeclarations.IllegalFixtures;
using Xunit;

namespace Lumio.Tools.GenDeclarations.Tests;

public sealed class GenDeclarationsCliTests
{
    [Fact]
    public void ScannerRejectsIllegalCombinationsInFixtureAssembly()
    {
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => AttributeDeclarationScanner.Scan(typeof(IllegalReplicatedServerOnly).Assembly));
        Assert.Contains("replicated", error.Message, StringComparison.Ordinal);
        Assert.Contains("server-only", error.Message, StringComparison.Ordinal);
        Assert.Contains("aoi-scoped", error.Message, StringComparison.Ordinal);
        Assert.Contains("claim-scoped", error.Message, StringComparison.Ordinal);
        Assert.Contains("room-public", error.Message, StringComparison.Ordinal);
        Assert.Contains("not-replicated", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CliWritesIdenticalBytesOnTwoRuns()
    {
        string first = Path.Combine(Path.GetTempPath(), "lumio-decl-" + Guid.NewGuid().ToString("N") + ".json");
        string second = Path.Combine(Path.GetTempPath(), "lumio-decl-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            RunResult run1 = RunCli("--output", first);
            RunResult run2 = RunCli("--output", second);
            Assert.Equal(0, run1.ExitCode);
            Assert.Equal(0, run2.ExitCode);
            byte[] bytes1 = File.ReadAllBytes(first);
            byte[] bytes2 = File.ReadAllBytes(second);
            Assert.Equal(bytes1, bytes2);
            Assert.Equal(Sha256(bytes1), Sha256(bytes2));
            Assert.DoesNotContain(bytes1, static value => value == (byte)'\r');
            Assert.Equal((byte)'\n', bytes1[^1]);
        }
        finally
        {
            TryDelete(first);
            TryDelete(second);
        }
    }

    [Fact]
    public void CliRejectsIllegalFixtureAssemblyWithNonZeroExit()
    {
        string output = Path.Combine(Path.GetTempPath(), "lumio-decl-illegal-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            RunResult result = RunCli(
                "--assembly",
                typeof(IllegalReplicatedServerOnly).Assembly.Location,
                "--output",
                output);
            Assert.NotEqual(0, result.ExitCode);
            string text = result.StdErr + result.StdOut;
            Assert.Contains("illegal attribute combination", text, StringComparison.Ordinal);
            Assert.Contains("replicated", text, StringComparison.Ordinal);
            Assert.Contains("server-only", text, StringComparison.Ordinal);
            Assert.False(File.Exists(output));
        }
        finally
        {
            TryDelete(output);
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

    private static string Sha256(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException)
        {
        }
    }

    private readonly record struct RunResult(int ExitCode, string StdOut, string StdErr);
}
