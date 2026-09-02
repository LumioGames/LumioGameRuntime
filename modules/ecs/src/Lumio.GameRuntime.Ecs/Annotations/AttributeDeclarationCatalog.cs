using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;

namespace Lumio.GameRuntime.Ecs.Annotations;

/// <summary>Loads the unique generated declaration table. There is no second handwritten table.</summary>
public static class AttributeDeclarationCatalog
{
    /// <summary>Manifest name of the generated JSON embedded in this assembly.</summary>
    public const string EmbeddedResourceName = "Lumio.GameRuntime.Ecs.generated.attribute-declarations.json";

    /// <summary>Parses a C-2 declaration table JSON document.</summary>
    public static IReadOnlyList<FieldAttributeDeclaration> Parse(string json) =>
        AttributeDeclarationJson.Parse(json);

    /// <summary>Loads the generated table from the embedded resource that is that file.</summary>
    public static IReadOnlyList<FieldAttributeDeclaration> LoadEmbedded()
    {
        Assembly assembly = typeof(AttributeDeclarationCatalog).Assembly;
        Stream? stream = assembly.GetManifestResourceStream(EmbeddedResourceName);
        if (stream is null)
        {
            throw new InvalidOperationException(
                "embedded attribute declaration table is missing: " + EmbeddedResourceName);
        }

        using (stream)
        using (var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
        {
            return Parse(reader.ReadToEnd());
        }
    }
}
