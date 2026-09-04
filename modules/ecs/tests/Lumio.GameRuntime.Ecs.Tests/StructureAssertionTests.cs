using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace Lumio.GameRuntime.Ecs.Tests;

public sealed class StructureAssertionTests
{
    [Fact]
    public void ProductionSourceHasASingleCreateWorldPathAndNoBannedTokens()
    {
        string root = FindRepoRoot();
        var files = new List<string>();
        string modules = Path.Combine(root, "modules");
        foreach (string module in Directory.GetDirectories(modules))
            Collect(Path.Combine(module, "src"), files);
        Collect(Path.Combine(root, "modules", "ecs", "samples", "username"), files);

        var createWorld = new List<string>();
        var banned = new List<string>();
        string[] tokens =
        {
            "_values",
            "_liveConnectionByAccount",
            "_session",
            "_eventsByRoomTick",
            "_displayed",
            "ChatIngressWorld",
            "WorldId(1)",
            "WorldId(2)",
            "WorldId(370)",
            "GetField(\"_componentRegistrationCapability\"",
            "EcsWorld",
            "SyncSlot",
            "GrantClaim",
            "TryParseLoose",
            "GetField(",
            "[Replicate]",
            "[Visibility(",
        };

        for (int i = 0; i < files.Count; i++)
        {
            string path = files[i];
            if (path.Contains($"{Path.DirectorySeparatorChar}generated{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                continue;
            string text = File.ReadAllText(path);
            if (path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) &&
                Regex.IsMatch(text, @"\.CreateWorld\s*\("))
                createWorld.Add(Path.GetRelativePath(root, path));
            for (int t = 0; t < tokens.Length; t++)
            {
                if (tokens[t] == "_session" && Regex.IsMatch(text, @"(?<![A-Za-z0-9])_session(?![A-Za-z0-9])"))
                    banned.Add(Path.GetRelativePath(root, path) + ": " + tokens[t]);
                else if (tokens[t] != "_session" && text.Contains(tokens[t], StringComparison.Ordinal))
                    banned.Add(Path.GetRelativePath(root, path) + ": " + tokens[t]);
            }
        }

        Assert.True(createWorld.Count <= 1, "CreateWorld call sites: " + string.Join(", ", createWorld));
        Assert.True(banned.Count == 0, "banned tokens: " + string.Join(", ", banned));
    }

    [Fact]
    public void SyncTIsAStruct()
    {
        Assert.True(typeof(Sync<string>).IsValueType);
        Assert.False(typeof(Sync<string>).IsEnum);
    }

    private static void Collect(string directory, List<string> files)
    {
        if (!Directory.Exists(directory)) return;
        files.AddRange(Directory.GetFiles(directory, "*.cs", SearchOption.AllDirectories));
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

        throw new InvalidOperationException("Repository root was not found.");
    }
}
