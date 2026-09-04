using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace Lumio.Tools.GenDeclarations;

internal enum FileSide
{
    Shared = 0,
    Server = 1,
    Client = 2,
}

internal sealed class FieldModel
{
    internal string Name = "";
    internal string ClrType = "";
    internal bool Persist;
    internal bool IsSync;
    internal string Scope = "Room";
    internal string Authority = "Server";
    internal string Notify = "Remote";
    internal string? ClaimBy;
    internal bool IsContainer;
}

internal sealed class RpcModel
{
    internal string Name = "";
    internal string Kind = ""; // ServerRpc / ClientRpc
    internal string Scope = "Room";
    internal string Signature = ""; // parameter list inside parens
    internal string[] ParamTypes = Array.Empty<string>();
    internal string[] ParamNames = Array.Empty<string>();
    internal bool HasUserBody;
}

internal sealed class ComponentModel
{
    internal string Namespace = "";
    internal string Name = "";
    internal readonly List<FieldModel> Fields = new();
    internal readonly List<RpcModel> Rpcs = new();
}

internal sealed class EntityTypeModel
{
    internal string Namespace = "";
    internal string Name = "";
    internal string? BaseName;
    internal bool World;
    internal bool IsAbstract;
    internal bool HasMembers;
    internal readonly List<string> HasTypes = new();
}

internal sealed class SourceModel
{
    internal string DefaultNamespace = "Lumio.GameRuntime.Samples.Username";
    internal readonly List<ComponentModel> Components = new();
    internal readonly List<EntityTypeModel> EntityTypes = new();
    internal readonly List<string> LintErrors = new();
}

internal static class SourceScanner
{
    private static readonly Regex NamespaceRx = new(@"namespace\s+([A-Za-z0-9_.]+)", RegexOptions.Compiled);
    private static readonly Regex ClassRx = new(@"public\s+(?:sealed\s+)?(?:partial\s+)?(?:abstract\s+)?class\s+([A-Za-z0-9_]+)(?:\s*:\s*([A-Za-z0-9_.<>,\s]+))?", RegexOptions.Compiled);
    private static readonly Regex HasRx = new(@"Has\(typeof\(([A-Za-z0-9_]+)\)\)", RegexOptions.Compiled);
    private static readonly Regex EntityTypeRx = new(@"EntityType\(Mode\.([A-Za-z]+)(?:\s*,\s*World\s*=\s*(true|false))?\)", RegexOptions.Compiled);
    private static readonly Regex RpcRx = new(@"\[(ServerRpc|ClientRpc)(?:\(Scope\.([A-Za-z]+)\))?\]\s*public\s+partial\s+void\s+([A-Za-z0-9_]+)\s*\(([^)]*)\)", RegexOptions.Compiled);
    private static readonly Regex BareListRx = new(@"public\s+(List|Dictionary)<", RegexOptions.Compiled);

    internal static SourceModel Scan(IReadOnlyList<string> files, FileSide keep)
    {
        var model = new SourceModel();
        var components = new Dictionary<string, ComponentModel>(StringComparer.Ordinal);
        var entities = new Dictionary<string, EntityTypeModel>(StringComparer.Ordinal);

        for (int i = 0; i < files.Count; i++)
        {
            string path = files[i];
            FileSide side = Classify(path);
            if (side == FileSide.Client && keep == FileSide.Server) continue;
            if (side == FileSide.Server && keep == FileSide.Client) continue;
            string text = StripLineComments(File.ReadAllText(path));
            string ns = Match(NamespaceRx, text) ?? model.DefaultNamespace;
            bool isComponentFile = text.Contains("[EcsComponent]", StringComparison.Ordinal);
            bool isEntityFile = text.Contains("[EntityType(", StringComparison.Ordinal);

            if (isEntityFile)
                ParseEntity(path, text, ns, entities, model);
            if (isComponentFile || text.Contains("sealed partial class", StringComparison.Ordinal))
                ParseComponent(path, text, ns, side, components, model);
        }

        model.Components.AddRange(components.Values);
        model.EntityTypes.AddRange(entities.Values);
        Validate(model);
        return model;
    }

    internal static FileSide Classify(string path)
    {
        if (path.EndsWith(".Server.cs", StringComparison.OrdinalIgnoreCase)) return FileSide.Server;
        if (path.EndsWith(".Client.cs", StringComparison.OrdinalIgnoreCase)) return FileSide.Client;
        return FileSide.Shared;
    }

    private static void ParseEntity(string path, string text, string ns, Dictionary<string, EntityTypeModel> entities, SourceModel model)
    {
        Match classMatch = ClassRx.Match(text);
        if (!classMatch.Success) return;
        string name = classMatch.Groups[1].Value;
        if (!entities.TryGetValue(name, out EntityTypeModel? entity))
        {
            entity = new EntityTypeModel { Name = name, Namespace = ns };
            entities[name] = entity;
        }

        entity.IsAbstract = text.Contains("abstract class " + name, StringComparison.Ordinal);
        Match et = EntityTypeRx.Match(text);
        if (et.Success && string.Equals(et.Groups[2].Value, "true", StringComparison.OrdinalIgnoreCase))
            entity.World = true;
        if (classMatch.Groups[2].Success)
        {
            string bases = classMatch.Groups[2].Value;
            int comma = bases.IndexOf(',');
            string first = (comma < 0 ? bases : bases.Substring(0, comma)).Trim();
            if (!string.Equals(first, "Component", StringComparison.Ordinal))
                entity.BaseName = first;
        }

        foreach (Match has in HasRx.Matches(text))
            entity.HasTypes.Add(has.Groups[1].Value);

        int brace = text.LastIndexOf('{');
        int close = text.LastIndexOf('}');
        if (brace >= 0 && close > brace)
        {
            string body = text.Substring(brace + 1, close - brace - 1).Trim();
            entity.HasMembers = body.Length > 0 && !string.IsNullOrWhiteSpace(body.Replace("//", "", StringComparison.Ordinal));
            if (entity.HasMembers && body.Contains("public ", StringComparison.Ordinal))
                model.LintErrors.Add(path + ": EntityType declaration class must have no members");
        }

        if (!entity.IsAbstract)
            model.LintErrors.Add(path + ": EntityType declaration class must be abstract");
    }

    private static void ParseComponent(string path, string text, string ns, FileSide side, Dictionary<string, ComponentModel> components, SourceModel model)
    {
        Match classMatch = ClassRx.Match(text);
        if (!classMatch.Success) return;
        string name = classMatch.Groups[1].Value;
        if (!components.TryGetValue(name, out ComponentModel? component))
        {
            component = new ComponentModel { Name = name, Namespace = ns };
            components[name] = component;
        }

        if (side == FileSide.Shared)
        {
            foreach (Match list in BareListRx.Matches(text))
                model.LintErrors.Add(path + ": bare " + list.Groups[1].Value + " field is illegal; use SyncList/SyncDict");

            // Shared files may only hold Sync fields, RPC declarations, and shared logic.
            MatchCollection persistBare = Regex.Matches(text, @"public\s+(string|bool|ulong|int|uint)\s+([A-Za-z0-9_]+)");
            foreach (Match bare in persistBare)
            {
                model.LintErrors.Add(path + ": non-Sync state field " + bare.Groups[2].Value + " must live in .Server.cs / .Client.cs");
            }
        }

        foreach (Match field in Regex.Matches(text, @"(?:\[Persist\]\s*)?public\s+Sync<([^>]+)>\s+([A-Za-z0-9_]+)\s*=\s*new\s*\(\s*Scope\.([A-Za-z]+)(?:\s*,\s*Authority\.([A-Za-z]+))?(?:\s*,\s*Notify\.([A-Za-z]+))?(?:\s*,\s*claimBy\s*:\s*nameof\(([A-Za-z0-9_]+)\))?\s*\)"))
        {
            bool persist = field.Value.Contains("[Persist]", StringComparison.Ordinal) || IndexHasPersist(text, field.Index);
            component.Fields.Add(new FieldModel
            {
                Name = field.Groups[2].Value,
                ClrType = field.Groups[1].Value.Trim(),
                Persist = persist,
                IsSync = true,
                Scope = field.Groups[3].Value,
                Authority = field.Groups[4].Success && field.Groups[4].Length > 0 ? field.Groups[4].Value : "Server",
                Notify = field.Groups[5].Success && field.Groups[5].Length > 0 ? field.Groups[5].Value : "Remote",
                ClaimBy = field.Groups[6].Success && field.Groups[6].Length > 0 ? field.Groups[6].Value : null,
            });
        }

        foreach (Match field in Regex.Matches(text, @"(?:\[Persist\]\s*)?public\s+(SyncList<[^>]+>|SyncDict<[^>]+>)\s+([A-Za-z0-9_]+)\s*=\s*new\s*\(\s*Scope\.([A-Za-z]+)(?:\s*,\s*Authority\.([A-Za-z]+))?(?:\s*,\s*Notify\.([A-Za-z]+))?(?:\s*,\s*claimBy\s*:\s*nameof\(([A-Za-z0-9_]+)\))?\s*\)"))
        {
            component.Fields.Add(new FieldModel
            {
                Name = field.Groups[2].Value,
                ClrType = field.Groups[1].Value.Trim(),
                Persist = field.Value.Contains("[Persist]", StringComparison.Ordinal) || IndexHasPersist(text, field.Index),
                IsSync = true,
                IsContainer = true,
                Scope = field.Groups[3].Value,
                Authority = field.Groups[4].Success && field.Groups[4].Length > 0 ? field.Groups[4].Value : "Server",
                Notify = field.Groups[5].Success && field.Groups[5].Length > 0 ? field.Groups[5].Value : "Remote",
                ClaimBy = field.Groups[6].Success && field.Groups[6].Length > 0 ? field.Groups[6].Value : null,
            });
        }

        foreach (Match field in Regex.Matches(text, @"(\[Persist\]\s*)?public\s+(string|bool|ulong|int|uint)\s+([A-Za-z0-9_]+)\s*(?:=|;)"))
        {
            if (side == FileSide.Shared) continue;
            component.Fields.Add(new FieldModel
            {
                Name = field.Groups[3].Value,
                ClrType = field.Groups[2].Value,
                Persist = field.Groups[1].Success && field.Groups[1].Length > 0,
                IsSync = false,
            });
        }

        foreach (Match rpc in RpcRx.Matches(text))
        {
            string args = rpc.Groups[4].Value.Trim();
            var types = new List<string>();
            var names = new List<string>();
            if (args.Length > 0)
            {
                string[] parts = args.Split(',');
                for (int p = 0; p < parts.Length; p++)
                {
                    string[] tokens = parts[p].Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                    if (tokens.Length >= 2)
                    {
                        types.Add(tokens[0]);
                        names.Add(tokens[tokens.Length - 1]);
                    }
                }
            }

            bool hasBody = text.IndexOf(rpc.Groups[3].Value + "(", StringComparison.Ordinal) >= 0 &&
                           text.Contains("{", StringComparison.Ordinal) &&
                           !text.Substring(rpc.Index, Math.Min(text.Length - rpc.Index, 200)).Contains(";");
            // User body: method is followed by '{' rather than ';'
            string after = text.Substring(rpc.Index, Math.Min(text.Length - rpc.Index, 400));
            int parenEnd = after.IndexOf(')');
            hasBody = parenEnd >= 0 && after.IndexOf('{', parenEnd) >= 0 && after.IndexOf('{', parenEnd) < after.IndexOf(';', parenEnd) + 50;
            int braceAt = after.IndexOf('{', parenEnd);
            int semiAt = after.IndexOf(';', parenEnd);
            hasBody = braceAt >= 0 && (semiAt < 0 || braceAt < semiAt);

            component.Rpcs.Add(new RpcModel
            {
                Name = rpc.Groups[3].Value,
                Kind = rpc.Groups[1].Value,
                Scope = rpc.Groups[2].Success && rpc.Groups[2].Length > 0 ? rpc.Groups[2].Value : "Room",
                Signature = args,
                ParamTypes = types.ToArray(),
                ParamNames = names.ToArray(),
                HasUserBody = hasBody,
            });
        }
    }

    private static bool IndexHasPersist(string text, int index)
    {
        int start = Math.Max(0, index - 80);
        return text.Substring(start, index - start).Contains("[Persist]", StringComparison.Ordinal);
    }

    private static void Validate(SourceModel model)
    {
        int worlds = 0;
        for (int i = 0; i < model.EntityTypes.Count; i++)
            if (model.EntityTypes[i].World) worlds++;
        if (worlds != 1)
            model.LintErrors.Add("World = true entity type count must be exactly 1, found " + worlds);
        for (int i = 0; i < model.EntityTypes.Count; i++)
        {
            EntityTypeModel entity = model.EntityTypes[i];
            if (entity.HasTypes.Count == 0 && entity.BaseName is null)
                model.LintErrors.Add("EntityType " + entity.Name + " is missing [Has] components");
            if (!entity.World)
            {
                bool observer = false;
                for (int h = 0; h < entity.HasTypes.Count; h++)
                    if (string.Equals(entity.HasTypes[h], "ObserverComponent", StringComparison.Ordinal)) { observer = true; break; }
                if (!observer) model.LintErrors.Add("EntityType " + entity.Name + " must declare [Has(typeof(ObserverComponent))]");
            }
        }
        for (int i = 0; i < model.Components.Count; i++)
        {
            ComponentModel component = model.Components[i];
            for (int f = 0; f < component.Fields.Count; f++)
            {
                FieldModel field = component.Fields[f];
                if (field.Scope != "Claim") continue;
                if (string.IsNullOrEmpty(field.ClaimBy))
                {
                    model.LintErrors.Add(component.Name + "." + field.Name + ": Scope.Claim requires claimBy");
                    continue;
                }
                FieldModel? source = component.Fields.Find(candidate => string.Equals(candidate.Name, field.ClaimBy, StringComparison.Ordinal));
                if (source is null || !source.IsContainer)
                    model.LintErrors.Add(component.Name + "." + field.Name + ": claimBy must name a SyncList or SyncDict on the same component");
            }
        }
    }

    private static string StripLineComments(string text)
    {
        var builder = new StringBuilder(text.Length);
        using var reader = new StringReader(text);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            int comment = line.IndexOf("//", StringComparison.Ordinal);
            if (comment >= 0) line = line.Substring(0, comment);
            builder.AppendLine(line);
        }

        return builder.ToString();
    }

    private static string? Match(Regex rx, string text)
    {
        Match match = rx.Match(text);
        return match.Success ? match.Groups[1].Value : null;
    }
}


