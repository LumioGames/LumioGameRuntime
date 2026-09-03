using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using Lumio.GameRuntime.Ecs.Annotations;

namespace Lumio.Tools.GenDeclarations;

internal static class CodeEmitter
{
    internal static void Emit(
        SourceModel model,
        FileSide side,
        string assemblyName,
        string @namespace,
        string outputDir,
        string? jsonPath)
    {
        Directory.CreateDirectory(outputDir);
        var encoding = new UTF8Encoding(false);
        WriteIfChanged(Path.Combine(outputDir, assemblyName + ".Registry.g.cs"), EmitRegistry(model, side, @namespace), encoding);
        WriteIfChanged(Path.Combine(outputDir, assemblyName + ".Sync.g.cs"), EmitSync(model, side, @namespace), encoding);
        for (int i = 0; i < model.Components.Count; i++)
        {
            ComponentModel component = model.Components[i];
            WriteIfChanged(Path.Combine(outputDir, component.Name + ".g.cs"), EmitComponent(component, side), encoding);
        }

        if (!string.IsNullOrEmpty(jsonPath))
        {
            IReadOnlyList<FieldAttributeDeclaration> rows = BuildRows(model);
            string json = AttributeDeclarationJson.Format(rows);
            string? dir = Path.GetDirectoryName(jsonPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            WriteIfChanged(jsonPath, json, encoding);
        }
    }

    internal static IReadOnlyList<FieldAttributeDeclaration> BuildRows(SourceModel model)
    {
        var rows = new List<FieldAttributeDeclaration>();
        for (int i = 0; i < model.Components.Count; i++)
        {
            ComponentModel component = model.Components[i];
            for (int f = 0; f < component.Fields.Count; f++)
            {
                FieldModel field = component.Fields[f];
                string id = component.Name + "." + Camel(field.Name);
                string persistence = field.Persist ? FieldAnnotationRules.PersistencePersistent : FieldAnnotationRules.PersistenceEphemeral;
                string replication = field.IsSync ? FieldAnnotationRules.ReplicationReplicated : FieldAnnotationRules.ReplicationNotReplicated;
                string visibility = field.IsSync ? Visibility(field.Scope) : FieldAnnotationRules.VisibilityServerOnly;
                string valueType = ValueType(field.ClrType);
                FieldAnnotationRules.Validate(id, persistence, replication, visibility);
                rows.Add(new FieldAttributeDeclaration(id, valueType, persistence, replication, visibility));
            }
        }

        rows.Add(new FieldAttributeDeclaration(
            "EntityIdentity.entityType",
            "enum:entityType",
            FieldAnnotationRules.PersistenceEphemeral,
            FieldAnnotationRules.ReplicationReplicated,
            FieldAnnotationRules.VisibilityRoomPublic));
        rows.Add(new FieldAttributeDeclaration(
            "EntityIdentity.claimedMark",
            "utf8-string",
            FieldAnnotationRules.PersistenceEphemeral,
            FieldAnnotationRules.ReplicationReplicated,
            FieldAnnotationRules.VisibilityClaimScoped));
        rows.Add(new FieldAttributeDeclaration(
            "EntityIdentity.unmappedMark",
            "utf8-string",
            FieldAnnotationRules.PersistenceEphemeral,
            FieldAnnotationRules.ReplicationReplicated,
            FieldAnnotationRules.VisibilityRoomPublic));
        rows.Add(new FieldAttributeDeclaration(
            "ChatComponent.lastMessagePersistOnly",
            "utf8-string",
            FieldAnnotationRules.PersistencePersistent,
            FieldAnnotationRules.ReplicationNotReplicated,
            FieldAnnotationRules.VisibilityServerOnly));
        rows.Sort(static (left, right) => string.CompareOrdinal(left.AttributeId, right.AttributeId));
        var unique = new List<FieldAttributeDeclaration>();
        for (int i = 0; i < rows.Count; i++)
        {
            if (i > 0 && string.Equals(rows[i].AttributeId, rows[i - 1].AttributeId, StringComparison.Ordinal))
                continue;
            unique.Add(rows[i]);
        }

        return unique;
    }

    private static void WriteIfChanged(string path, string content, Encoding encoding)
    {
        byte[] bytes = encoding.GetBytes(content);
        if (File.Exists(path))
        {
            byte[] existing = File.ReadAllBytes(path);
            if (existing.AsSpan().SequenceEqual(bytes)) return;
        }

        for (int attempt = 0; attempt < 8; attempt++)
        {
            try
            {
                string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                File.WriteAllBytes(temp, bytes);
                File.Copy(temp, path, overwrite: true);
                File.Delete(temp);
                return;
            }
            catch (IOException) when (attempt < 7)
            {
                Thread.Sleep(25 * (attempt + 1));
            }
        }
    }

    private static string EmitRegistry(SourceModel model, FileSide side, string @namespace)
    {
        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine("#nullable enable");
        sb.AppendLine("using System;");
        sb.AppendLine("using System.Collections.Generic;");
        sb.AppendLine("using Lumio.GameRuntime.Ecs;");
        sb.AppendLine("using Lumio.GameRuntime.Ecs.Annotations;");
        var nss = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < model.Components.Count; i++) nss.Add(model.Components[i].Namespace);
        for (int i = 0; i < model.EntityTypes.Count; i++) nss.Add(model.EntityTypes[i].Namespace);
        foreach (string ns in nss)
            sb.AppendLine("using " + ns + ";");
        sb.AppendLine();
        sb.AppendLine("namespace " + @namespace + ";");
        sb.AppendLine();
        sb.AppendLine("/// <summary>Generated registry for this gameplay assembly.</summary>");
        sb.AppendLine("public sealed class GeneratedRegistry : EcsRegistry");
        sb.AppendLine("{");
        sb.AppendLine("    /// <summary>Singleton used by WorldManager.Create.</summary>");
        sb.AppendLine("    public static GeneratedRegistry Instance { get; } = new GeneratedRegistry();");
        sb.AppendLine();
        sb.AppendLine("    private GeneratedRegistry()");
        sb.AppendLine("    {");
        sb.AppendLine("        Current = this;");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    /// <inheritdoc />");
        sb.AppendLine("    public override RegistrySide Side => RegistrySide." + (side == FileSide.Client ? "Client" : "Server") + ";");
        EntityTypeModel? world = null;
        for (int i = 0; i < model.EntityTypes.Count; i++)
            if (model.EntityTypes[i].World) world = model.EntityTypes[i];
        sb.AppendLine();
        sb.AppendLine("    /// <inheritdoc />");
        sb.AppendLine("    public override Type WorldEntityType => typeof(" + (world?.Name ?? "WorldEntity") + ");");
        sb.AppendLine();
        sb.AppendLine("    /// <inheritdoc />");
        sb.AppendLine("    public override IReadOnlyList<FieldAttributeDeclaration> AttributeDeclarations { get; } = BuildAttributes();");
        sb.AppendLine();
        sb.AppendLine("    /// <inheritdoc />");
        sb.AppendLine("    public override Component[] CreateComponents(Type entityType)");
        sb.AppendLine("    {");
        sb.AppendLine("        var list = new List<Component>();");
        sb.AppendLine("        AddComponents(entityType, list);");
        sb.AppendLine("        return list.ToArray();");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    private static void AddComponents(Type entityType, List<Component> list)");
        sb.AppendLine("    {");
        for (int i = 0; i < model.EntityTypes.Count; i++)
        {
            EntityTypeModel entity = model.EntityTypes[i];
            sb.AppendLine("        if (entityType == typeof(" + entity.Name + "))");
            sb.AppendLine("        {");
            if (entity.BaseName is not null)
                sb.AppendLine("            AddComponents(typeof(" + entity.BaseName + "), list);");
            for (int h = 0; h < entity.HasTypes.Count; h++)
                sb.AppendLine("            list.Add(new " + entity.HasTypes[h] + "());");
            sb.AppendLine("            return;");
            sb.AppendLine("        }");
        }

        sb.AppendLine("        throw new InvalidOperationException(\"Unknown entity type \" + entityType.Name);");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    /// <inheritdoc />");
        sb.AppendLine("    public override string WireName(Type entityType)");
        sb.AppendLine("    {");
        sb.AppendLine("        if (entityType.Name.EndsWith(\"Entity\", StringComparison.Ordinal) && entityType.Name.Length > 6)");
        sb.AppendLine("        {");
        sb.AppendLine("            string stem = entityType.Name.Substring(0, entityType.Name.Length - 6);");
        sb.AppendLine("            return char.ToLowerInvariant(stem[0]) + stem.Substring(1);");
        sb.AppendLine("        }");
        sb.AppendLine("        return entityType.Name;");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    /// <inheritdoc />");
        sb.AppendLine("    public override bool TryResolveEntityType(string name, out Type entityType)");
        sb.AppendLine("    {");
        sb.AppendLine("        entityType = null!;");
        for (int i = 0; i < model.EntityTypes.Count; i++)
        {
            EntityTypeModel entity = model.EntityTypes[i];
            string wire = WireNameOf(entity.Name);
            sb.AppendLine("        if (string.Equals(name, \"" + entity.Name + "\", StringComparison.Ordinal) || string.Equals(name, \"" + wire + "\", StringComparison.Ordinal))");
            sb.AppendLine("        { entityType = typeof(" + entity.Name + "); return true; }");
        }

        sb.AppendLine("        return false;");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    /// <inheritdoc />");
        sb.AppendLine("    public override bool IsEntityType(Type concrete, Type query)");
        sb.AppendLine("    {");
        sb.AppendLine("        if (query.IsAssignableFrom(concrete)) return true;");
        sb.AppendLine("        Type? current = concrete;");
        sb.AppendLine("        while (current is not null)");
        sb.AppendLine("        {");
        sb.AppendLine("            if (current == query) return true;");
        for (int i = 0; i < model.EntityTypes.Count; i++)
        {
            EntityTypeModel entity = model.EntityTypes[i];
            if (entity.BaseName is null) continue;
            sb.AppendLine("            if (current == typeof(" + entity.Name + ")) current = typeof(" + entity.BaseName + ");");
        }

        sb.AppendLine("            current = current.BaseType;");
        sb.AppendLine("        }");
        sb.AppendLine("        return false;");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    private static IReadOnlyList<FieldAttributeDeclaration> BuildAttributes()");
        sb.AppendLine("    {");
        IReadOnlyList<FieldAttributeDeclaration> rows = BuildRows(model);
        sb.AppendLine("        return new FieldAttributeDeclaration[]");
        sb.AppendLine("        {");
        for (int i = 0; i < rows.Count; i++)
        {
            FieldAttributeDeclaration row = rows[i];
            sb.Append("            new FieldAttributeDeclaration(\"");
            sb.Append(row.AttributeId);
            sb.Append("\", \"");
            sb.Append(row.ValueType);
            sb.Append("\", \"");
            sb.Append(row.Persistence);
            sb.Append("\", \"");
            sb.Append(row.Replication);
            sb.Append("\", \"");
            sb.Append(row.Visibility);
            sb.Append("\")");
            sb.AppendLine(i + 1 == rows.Count ? "" : ",");
        }

        sb.AppendLine("        };");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        return sb.ToString();
    }

    private static string EmitSync(SourceModel model, FileSide side, string @namespace)
    {
        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine("#nullable enable");
        sb.AppendLine("namespace " + @namespace + ";");
        sb.AppendLine();
        sb.AppendLine("internal static class GeneratedSyncTable");
        sb.AppendLine("{");
        sb.AppendLine("    internal static readonly string Side = \"" + side + "\";");
        int count = 0;
        for (int i = 0; i < model.Components.Count; i++) count += model.Components[i].Fields.Count;
        sb.AppendLine("    internal static readonly int FieldCount = " + count.ToString(CultureInfo.InvariantCulture) + ";");
        sb.AppendLine("}");
        return sb.ToString();
    }

    private static string EmitComponent(ComponentModel component, FileSide side)
    {
        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine("#nullable enable");
        sb.AppendLine("using System;");
        sb.AppendLine("using Lumio.GameRuntime.Ecs;");
        sb.AppendLine();
        sb.AppendLine("namespace " + component.Namespace + ";");
        sb.AppendLine();
        sb.AppendLine("public sealed partial class " + component.Name + " : IGeneratedComponent");
        sb.AppendLine("{");
        for (int i = 0; i < component.Fields.Count; i++)
        {
            FieldModel field = component.Fields[i];
            if (!field.IsSync) continue;
            sb.AppendLine("    partial void On" + field.Name + "Changing(" + field.ClrType + " old, " + field.ClrType + " @new, ChangeReason reason);");
            sb.AppendLine("    partial void On" + field.Name + "Changed(" + field.ClrType + " old, " + field.ClrType + " @new, ChangeReason reason);");
        }

        sb.AppendLine("    partial void OnClientWrite(in SyncWrite w, ref bool accept);");
        sb.AppendLine();
        for (int i = 0; i < component.Rpcs.Count; i++)
        {
            RpcModel rpc = component.Rpcs[i];
            bool generateStub =
                (rpc.Kind == "ServerRpc" && side == FileSide.Client && !rpc.HasUserBody) ||
                (rpc.Kind == "ClientRpc" && side == FileSide.Server && !rpc.HasUserBody);
            if (!generateStub) continue;
            string emit = rpc.Kind == "ServerRpc" ? "EmitServerRpc" : "EmitClientRpc";
            sb.Append("    public partial void " + rpc.Name + "(" + rpc.Signature + ") => " + emit + "(\"" + rpc.Name + "\"");
            for (int p = 0; p < rpc.ParamNames.Length; p++)
            {
                sb.Append(", ");
                sb.Append(rpc.ParamNames[p]);
            }

            sb.AppendLine(");");
        }

        sb.AppendLine();
        sb.AppendLine("    void IGeneratedComponent.BindFields(ISyncHost host)");
        sb.AppendLine("    {");
        int ordinal = 0;
        for (int i = 0; i < component.Fields.Count; i++)
        {
            FieldModel field = component.Fields[i];
            if (!field.IsSync) continue;
            sb.AppendLine("        " + field.Name + " = " + field.Name + ".Bound(host, this, " + ordinal.ToString(CultureInfo.InvariantCulture) + ", \"" + component.Name + "." + Camel(field.Name) + "\");");
            ordinal++;
        }

        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    void IGeneratedComponent.InvokePostAttribute() => PostAttribute();");
        sb.AppendLine("    partial void PostAttribute();");
        sb.AppendLine();
        sb.AppendLine("    void IGeneratedComponent.InvokeFieldChanging(int ordinal, object? oldValue, object? newValue, ChangeReason reason)");
        sb.AppendLine("    {");
        ordinal = 0;
        for (int i = 0; i < component.Fields.Count; i++)
        {
            FieldModel field = component.Fields[i];
            if (!field.IsSync) continue;
            sb.AppendLine("        if (ordinal == " + ordinal.ToString(CultureInfo.InvariantCulture) + ") On" + field.Name + "Changing((" + field.ClrType + ")oldValue!, (" + field.ClrType + ")newValue!, reason);");
            ordinal++;
        }

        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    void IGeneratedComponent.InvokeFieldChanged(int ordinal, object? oldValue, object? newValue, ChangeReason reason)");
        sb.AppendLine("    {");
        ordinal = 0;
        for (int i = 0; i < component.Fields.Count; i++)
        {
            FieldModel field = component.Fields[i];
            if (!field.IsSync) continue;
            sb.AppendLine("        if (ordinal == " + ordinal.ToString(CultureInfo.InvariantCulture) + ") On" + field.Name + "Changed((" + field.ClrType + ")oldValue!, (" + field.ClrType + ")newValue!, reason);");
            ordinal++;
        }

        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    bool IGeneratedComponent.DispatchClientWrite(in SyncWrite write)");
        sb.AppendLine("    {");
        sb.AppendLine("        bool accept = true;");
        sb.AppendLine("        OnClientWrite(in write, ref accept);");
        sb.AppendLine("        return accept;");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    void IGeneratedComponent.DispatchServerRpc(string method, object?[] args)");
        sb.AppendLine("    {");
        for (int i = 0; i < component.Rpcs.Count; i++)
        {
            RpcModel rpc = component.Rpcs[i];
            if (rpc.Kind != "ServerRpc") continue;
            if (side == FileSide.Client) continue;
            sb.AppendLine("        if (method == \"" + rpc.Name + "\")");
            sb.AppendLine("        {");
            sb.Append("            " + rpc.Name + "(");
            for (int p = 0; p < rpc.ParamTypes.Length; p++)
            {
                if (p > 0) sb.Append(", ");
                sb.Append("(" + rpc.ParamTypes[p] + ")args[" + p.ToString(CultureInfo.InvariantCulture) + "]!");
            }

            sb.AppendLine(");");
            sb.AppendLine("            return;");
            sb.AppendLine("        }");
        }

        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    void IGeneratedComponent.DispatchClientRpc(string method, object?[] args)");
        sb.AppendLine("    {");
        for (int i = 0; i < component.Rpcs.Count; i++)
        {
            RpcModel rpc = component.Rpcs[i];
            if (rpc.Kind != "ClientRpc") continue;
            if (side == FileSide.Server) continue;
            sb.AppendLine("        if (method == \"" + rpc.Name + "\")");
            sb.AppendLine("        {");
            sb.Append("            " + rpc.Name + "(");
            for (int p = 0; p < rpc.ParamTypes.Length; p++)
            {
                if (p > 0) sb.Append(", ");
                sb.Append("(" + rpc.ParamTypes[p] + ")args[" + p.ToString(CultureInfo.InvariantCulture) + "]!");
            }

            sb.AppendLine(");");
            sb.AppendLine("            return;");
            sb.AppendLine("        }");
        }

        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    void IGeneratedComponent.CapturePersist(PersistWriter writer)");
        sb.AppendLine("    {");
        for (int i = 0; i < component.Fields.Count; i++)
        {
            FieldModel field = component.Fields[i];
            if (!field.Persist) continue;
            string id = component.Name + "." + Camel(field.Name);
            string read = field.IsSync ? field.Name + ".Value" : field.Name;
            if (field.ClrType == "string")
                sb.AppendLine("        writer.WriteString(\"" + id + "\", " + read + ");");
            else if (field.ClrType == "ulong")
                sb.AppendLine("        writer.WriteUInt64(\"" + id + "\", " + read + ");");
            else if (field.ClrType == "bool")
                sb.AppendLine("        writer.WriteBoolean(\"" + id + "\", " + read + ");");
        }

        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    void IGeneratedComponent.CaptureSync(PersistWriter writer)");
        sb.AppendLine("    {");
        for (int i = 0; i < component.Fields.Count; i++)
        {
            FieldModel field = component.Fields[i];
            if (!field.IsSync) continue;
            string id = component.Name + "." + Camel(field.Name);
            if (field.ClrType == "string")
                sb.AppendLine("        writer.WriteString(\"" + id + "\", " + field.Name + ".Value);");
            else if (field.ClrType == "ulong")
                sb.AppendLine("        writer.WriteUInt64(\"" + id + "\", " + field.Name + ".Value);");
            else if (field.ClrType == "bool")
                sb.AppendLine("        writer.WriteBoolean(\"" + id + "\", " + field.Name + ".Value);");
        }

        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    void IGeneratedComponent.RestorePersist(PersistReader reader)");
        sb.AppendLine("    {");
        for (int i = 0; i < component.Fields.Count; i++)
        {
            FieldModel field = component.Fields[i];
            if (!field.Persist) continue;
            string id = component.Name + "." + Camel(field.Name);
            if (field.ClrType == "string")
            {
                sb.AppendLine("        if (reader.TryReadString(\"" + id + "\", out string " + Camel(field.Name) + "Restore))");
                sb.AppendLine("            " + (field.IsSync ? field.Name + ".SetSilent(" + Camel(field.Name) + "Restore)" : field.Name + " = " + Camel(field.Name) + "Restore") + ";");
            }
            else if (field.ClrType == "ulong")
            {
                sb.AppendLine("        if (reader.TryReadUInt64(\"" + id + "\", out ulong " + Camel(field.Name) + "Restore))");
                sb.AppendLine("            " + (field.IsSync ? field.Name + ".SetSilent(" + Camel(field.Name) + "Restore)" : field.Name + " = " + Camel(field.Name) + "Restore") + ";");
            }
            else if (field.ClrType == "bool")
            {
                sb.AppendLine("        if (reader.TryReadBoolean(\"" + id + "\", out bool " + Camel(field.Name) + "Restore))");
                sb.AppendLine("            " + field.Name + " = " + Camel(field.Name) + "Restore;");
            }
        }

        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    object? IGeneratedComponent.ReadField(string fieldId)");
        sb.AppendLine("    {");
        for (int i = 0; i < component.Fields.Count; i++)
        {
            FieldModel field = component.Fields[i];
            string camel = Camel(field.Name);
            string read = field.IsSync ? field.Name + ".Value" : field.Name;
            sb.AppendLine("        if (string.Equals(fieldId, \"" + camel + "\", StringComparison.Ordinal) || string.Equals(fieldId, \"" + field.Name + "\", StringComparison.Ordinal)) return " + read + ";");
        }

        sb.AppendLine("        return null;");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    void IGeneratedComponent.WriteField(string fieldId, object? value, bool silent)");
        sb.AppendLine("    {");
        for (int i = 0; i < component.Fields.Count; i++)
        {
            FieldModel field = component.Fields[i];
            string camel = Camel(field.Name);
            sb.AppendLine("        if (string.Equals(fieldId, \"" + camel + "\", StringComparison.Ordinal) || string.Equals(fieldId, \"" + field.Name + "\", StringComparison.Ordinal))");
            sb.AppendLine("        {");
            if (field.IsSync)
            {
                sb.AppendLine("            if (silent) " + field.Name + ".SetSilent((" + field.ClrType + ")value!);");
                sb.AppendLine("            else " + field.Name + ".Value = (" + field.ClrType + ")value!;");
            }
            else
            {
                sb.AppendLine("            " + field.Name + " = (" + field.ClrType + ")value!;");
            }

            sb.AppendLine("            return;");
            sb.AppendLine("        }");
        }

        sb.AppendLine("    }");
        sb.AppendLine("}");
        return sb.ToString();
    }

    private static string Camel(string name)
    {
        if (string.IsNullOrEmpty(name) || char.IsLower(name[0])) return name;
        return char.ToLowerInvariant(name[0]) + name.Substring(1);
    }

    private static string Visibility(string scope) => scope switch
    {
        "Aoi" => FieldAnnotationRules.VisibilityAoiScoped,
        "Owner" => FieldAnnotationRules.VisibilityServerOnly,
        "Claim" => FieldAnnotationRules.VisibilityClaimScoped,
        _ => FieldAnnotationRules.VisibilityRoomPublic,
    };

    private static string ValueType(string clr) => clr switch
    {
        "string" => "utf8-string",
        "ulong" => "u64",
        "bool" => "bool",
        "int" => "i32",
        "uint" => "u32",
        _ => "utf8-string",
    };

    private static string WireNameOf(string entityName)
    {
        if (entityName.EndsWith("Entity", StringComparison.Ordinal) && entityName.Length > 6)
        {
            string stem = entityName.Substring(0, entityName.Length - 6);
            return char.ToLowerInvariant(stem[0]) + stem.Substring(1);
        }

        return entityName;
    }
}
