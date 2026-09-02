namespace Lumio.GameRuntime.Ecs.Annotations;

/// <summary>One C-2 <c>attributeDeclarations.structure</c> row.</summary>
public readonly record struct FieldAttributeDeclaration(
    string AttributeId,
    string ValueType,
    string Persistence,
    string Replication,
    string Visibility);
