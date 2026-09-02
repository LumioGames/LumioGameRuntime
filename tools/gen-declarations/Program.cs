using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using Lumio.GameRuntime.Ecs.Annotations;

namespace Lumio.Tools.GenDeclarations;

/// <summary>CLI for the field-annotation declaration generator.</summary>
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
        string? output = null;
        var assemblyPaths = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            if (arg == "--output")
            {
                output = RequireValue(args, ref i, "--output");
            }
            else if (arg == "--assembly")
            {
                assemblyPaths.Add(RequireValue(args, ref i, "--assembly"));
            }
            else if (arg == "--help" || arg == "-h")
            {
                Console.Out.WriteLine("Usage: gen-declarations [--output PATH] [--assembly PATH]...");
                return 0;
            }
            else
            {
                throw new InvalidOperationException("unknown argument: " + arg);
            }
        }

        var assemblies = new List<Assembly>();
        if (assemblyPaths.Count == 0)
        {
            assemblies.Add(typeof(ChatComponent).Assembly);
        }
        else
        {
            for (int i = 0; i < assemblyPaths.Count; i++)
                assemblies.Add(Assembly.LoadFrom(assemblyPaths[i]));
        }

        IReadOnlyList<FieldAttributeDeclaration> rows = AttributeDeclarationScanner.Scan(assemblies.ToArray());
        string json = AttributeDeclarationJson.Format(rows);
        string dest = output ?? DefaultOutputPath();
        string? directory = Path.GetDirectoryName(dest);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        File.WriteAllText(dest, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return 0;
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
