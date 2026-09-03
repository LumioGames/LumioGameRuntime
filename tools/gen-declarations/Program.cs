using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Lumio.GameRuntime.Ecs.Annotations;

namespace Lumio.Tools.GenDeclarations;

/// <summary>CLI for the ECS declaration generator: registry + sync table + C-2 JSON.</summary>
public static class Program
{
    public static int Main(string[] args)
    {
        try
        {
            return Run(args ?? Array.Empty<string>());
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IOException or BadImageFormatException or FileLoadException)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static int Run(string[] args)
    {
        string? outputJson = null;
        string? outputDir = null;
        string? sources = null;
        string side = "server";
        string? ns = null;
        string? assemblyName = null;
        var assemblyPaths = new List<string>();
        bool hashOnly = false;
        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            if (arg == "--output") outputJson = RequireValue(args, ref i, "--output");
            else if (arg == "--output-dir") outputDir = RequireValue(args, ref i, "--output-dir");
            else if (arg == "--sources") sources = RequireValue(args, ref i, "--sources");
            else if (arg == "--side") side = RequireValue(args, ref i, "--side");
            else if (arg == "--namespace") ns = RequireValue(args, ref i, "--namespace");
            else if (arg == "--assembly-name") assemblyName = RequireValue(args, ref i, "--assembly-name");
            else if (arg == "--assembly") assemblyPaths.Add(RequireValue(args, ref i, "--assembly"));
            else if (arg == "--hash") hashOnly = true;
            else if (arg == "--help" || arg == "-h")
            {
                Console.Out.WriteLine("Usage: gen-declarations [--sources DIR] [--side server|client] [--output-dir DIR] [--output JSON] [--assembly PATH]...");
                return 0;
            }
            else throw new InvalidOperationException("unknown argument: " + arg);
        }

        if (!string.IsNullOrEmpty(sources))
        {
            FileSide keep = string.Equals(side, "client", StringComparison.OrdinalIgnoreCase) ? FileSide.Client : FileSide.Server;
            var files = new List<string>();
            foreach (string file in Directory.GetFiles(sources, "*.cs", SearchOption.AllDirectories))
            {
                if (file.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase)) continue;
                files.Add(file);
            }

            files.Sort(StringComparer.Ordinal);
            if (hashOnly)
            {
                Console.Out.WriteLine(HashFiles(files));
                return 0;
            }

            SourceModel model = SourceScanner.Scan(files, keep);
            if (model.LintErrors.Count > 0)
            {
                for (int e = 0; e < model.LintErrors.Count; e++)
                    Console.Error.WriteLine(model.LintErrors[e]);
                return 1;
            }

            string outDir = outputDir ?? Path.Combine(sources, "generated", keep == FileSide.Client ? "client" : "server");
            string json = outputJson ?? (keep == FileSide.Server ? DefaultOutputPath() : Path.Combine(outDir, "attribute-declarations.json"));
            CodeEmitter.Emit(
                model,
                keep,
                assemblyName ?? "Lumio.GameRuntime.Samples.Username",
                ns ?? "Lumio.GameRuntime.Samples.Username",
                outDir,
                json);
            Console.Out.WriteLine("generated " + outDir);
            return 0;
        }

        var assemblies = new List<Assembly>();
        if (assemblyPaths.Count == 0)
        {
            assemblies.Add(typeof(Lumio.GameRuntime.Ecs.EcsComponentAttribute).Assembly);
        }
        else
        {
            for (int i = 0; i < assemblyPaths.Count; i++)
                assemblies.Add(Assembly.LoadFrom(assemblyPaths[i]));
        }

        IReadOnlyList<FieldAttributeDeclaration> rows = AttributeDeclarationScanner.Scan(assemblies.ToArray());
        string jsonText = AttributeDeclarationJson.Format(rows);
        string dest = outputJson ?? DefaultOutputPath();
        string? directory = Path.GetDirectoryName(dest);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        File.WriteAllText(dest, jsonText, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return 0;
    }

    private static string HashFiles(List<string> files)
    {
        using SHA256 sha = SHA256.Create();
        for (int i = 0; i < files.Count; i++)
        {
            byte[] name = Encoding.UTF8.GetBytes(files[i].Replace('\\', '/'));
            sha.TransformBlock(name, 0, name.Length, null, 0);
            byte[] bytes = File.ReadAllBytes(files[i]);
            sha.TransformBlock(bytes, 0, bytes.Length, null, 0);
        }

        sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return Convert.ToHexString(sha.Hash!).ToLowerInvariant();
    }

    private static string RequireValue(string[] args, ref int index, string name)
    {
        if (index + 1 >= args.Length)
            throw new InvalidOperationException(name + " requires a value");
        index++;
        return args[index];
    }

    private static string DefaultOutputPath() =>
        Path.Combine(FindRepoRoot(), "modules", "ecs", "generated", "attribute-declarations.json");

    private static string FindRepoRoot()
    {
        string? current = Directory.GetCurrentDirectory();
        if (TryFindRepoRoot(current, out string? fromCwd)) return fromCwd!;
        if (TryFindRepoRoot(AppContext.BaseDirectory, out string? fromBase)) return fromBase!;
        throw new InvalidOperationException("Repository root was not found from the current directory or tool location.");
    }

    private static bool TryFindRepoRoot(string? start, out string? root)
    {
        string? current = start;
        while (!string.IsNullOrEmpty(current))
        {
            if (File.Exists(Path.Combine(current, "Directory.Build.props")) &&
                Directory.Exists(Path.Combine(current, "modules")))
            {
                root = current;
                return true;
            }

            current = Directory.GetParent(current)?.FullName;
        }

        root = null;
        return false;
    }
}
