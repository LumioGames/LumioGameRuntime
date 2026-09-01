using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace Lumio.GameRuntime.Replication.Binding;

internal static class AttributeIdClassifier
{
    internal const int MaxAttributeIdBytes = 128;

    private static readonly Regex Grammar = new(
        "^[A-Z][A-Za-z0-9]*\\.[a-z][A-Za-z0-9]*$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled | RegexOptions.ExplicitCapture);

    internal static bool IsStorageAddressing(string? attributeId)
    {
        if (string.IsNullOrEmpty(attributeId)) return false;
        if (attributeId.Contains('(')) return true;
        if (attributeId.StartsWith("Storage.", StringComparison.Ordinal)) return true;
        return attributeId.Contains('/') || attributeId.Contains('\\');
    }

    internal static string? Classify(string? attributeId, IReadOnlyDictionary<string, AttributeDeclaration> declarations)
    {
        if (IsStorageAddressing(attributeId)) return "storage_access_forbidden";
        if (string.IsNullOrEmpty(attributeId) || Encoding.UTF8.GetByteCount(attributeId) > MaxAttributeIdBytes)
            return "invalid_attribute_id";
        if (!Grammar.IsMatch(attributeId)) return "invalid_attribute_id";
        return declarations.ContainsKey(attributeId) ? null : "undeclared_attribute";
    }
}
