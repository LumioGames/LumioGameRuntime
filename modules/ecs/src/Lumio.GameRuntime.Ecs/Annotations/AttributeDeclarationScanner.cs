using System;
using System.Collections.Generic;
using System.Reflection;

namespace Lumio.GameRuntime.Ecs.Annotations;

/// <summary>Walks assemblies for <see cref="EcsComponentAttribute"/> types and emits C-2 declaration rows.</summary>
public static class AttributeDeclarationScanner
{
    /// <summary>
    /// Scans public instance fields and properties. Unannotated members are omitted (default = never on wire, never persisted).
    /// Illegal C-2 combinations throw <see cref="InvalidOperationException"/>.
    /// </summary>
    public static IReadOnlyList<FieldAttributeDeclaration> Scan(params Assembly[] assemblies)
    {
        AnnotationGuard.NotNull(assemblies, nameof(assemblies));

        var rows = new List<FieldAttributeDeclaration>();
        var errors = new List<string>();
        for (int assemblyIndex = 0; assemblyIndex < assemblies.Length; assemblyIndex++)
        {
            Assembly assembly = assemblies[assemblyIndex];
            AnnotationGuard.NotNull(assembly, nameof(assemblies));
            Type[] types = assembly.GetExportedTypes();
            for (int typeIndex = 0; typeIndex < types.Length; typeIndex++)
            {
                Type type = types[typeIndex];
                if (type.GetCustomAttribute<EcsComponentAttribute>() is null) continue;
                ScanType(type, rows, errors);
            }
        }

        if (errors.Count > 0)
            throw new InvalidOperationException(string.Join("\n", errors.ToArray()));

        rows.Sort(static (left, right) => string.CompareOrdinal(left.AttributeId, right.AttributeId));
        for (int i = 1; i < rows.Count; i++)
        {
            if (string.Equals(rows[i].AttributeId, rows[i - 1].AttributeId, StringComparison.Ordinal))
                throw new InvalidOperationException("duplicate attribute id: " + rows[i].AttributeId);
        }

        return rows;
    }

    private static void ScanType(Type type, List<FieldAttributeDeclaration> rows, List<string> errors)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly;
        PropertyInfo[] properties = type.GetProperties(flags);
        for (int i = 0; i < properties.Length; i++)
        {
            PropertyInfo property = properties[i];
            if (property.GetIndexParameters().Length > 0) continue;
            TryAdd(type, property.Name, property.PropertyType, property, rows, errors);
        }

        FieldInfo[] fields = type.GetFields(flags);
        for (int i = 0; i < fields.Length; i++)
        {
            FieldInfo field = fields[i];
            if (field.IsSpecialName) continue;
            TryAdd(type, field.Name, field.FieldType, field, rows, errors);
        }
    }

    private static void TryAdd(
        Type type,
        string memberName,
        Type clrType,
        MemberInfo member,
        List<FieldAttributeDeclaration> rows,
        List<string> errors)
    {
        PersistAttribute? persist = member.GetCustomAttribute<PersistAttribute>();
        ReplicateAttribute? replicate = member.GetCustomAttribute<ReplicateAttribute>();
        VisibilityAttribute? visibility = member.GetCustomAttribute<VisibilityAttribute>();
        AttributeValueTypeAttribute? valueType = member.GetCustomAttribute<AttributeValueTypeAttribute>();
        if (persist is null && replicate is null && visibility is null && valueType is null)
            return;

        string attributeId = type.Name + "." + ToCamelCase(memberName);
        string persistence = persist is null
            ? FieldAnnotationRules.DefaultPersistence
            : FieldAnnotationRules.Token(persist.Kind);
        string replication = replicate is null
            ? FieldAnnotationRules.DefaultReplication
            : FieldAnnotationRules.Token(replicate.Kind);
        string visibilityToken = visibility is null
            ? FieldAnnotationRules.DefaultVisibility
            : FieldAnnotationRules.Token(visibility.Kind);

        try
        {
            FieldAnnotationRules.Validate(attributeId, persistence, replication, visibilityToken);
            string resolvedType = valueType is null ? InferValueType(clrType, attributeId) : valueType.ValueType;
            if (string.IsNullOrEmpty(resolvedType))
                throw new InvalidOperationException("valueType is required for " + attributeId);
            rows.Add(new FieldAttributeDeclaration(attributeId, resolvedType, persistence, replication, visibilityToken));
        }
        catch (InvalidOperationException ex)
        {
            errors.Add(ex.Message);
        }
    }

    private static string InferValueType(Type clrType, string attributeId)
    {
        if (clrType == typeof(string)) return "utf8-string";
        if (clrType == typeof(ulong)) return "u64";
        if (clrType == typeof(long)) return "i64";
        if (clrType == typeof(uint)) return "u32";
        if (clrType == typeof(int)) return "i32";
        if (clrType == typeof(ushort)) return "u16";
        if (clrType == typeof(short)) return "i16";
        if (clrType == typeof(byte)) return "u8";
        if (clrType == typeof(bool)) return "bool";
        throw new InvalidOperationException("unsupported field type " + clrType.FullName + " for " + attributeId);
    }

    private static string ToCamelCase(string name)
    {
        if (string.IsNullOrEmpty(name) || char.IsLower(name[0])) return name;
        return char.ToLowerInvariant(name[0]) + name.Substring(1);
    }
}
