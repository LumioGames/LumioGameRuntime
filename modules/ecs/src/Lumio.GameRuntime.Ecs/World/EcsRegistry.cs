using System;
using System.Collections.Generic;
using Lumio.GameRuntime.Ecs.Annotations;

namespace Lumio.GameRuntime.Ecs;

/// <summary>Which assembly the generated registry was produced for.</summary>
public enum RegistrySide
{
    /// <summary>Server gameplay assembly (<c>LUMIO_SERVER</c>).</summary>
    Server = 0,

    /// <summary>Client gameplay assembly (<c>LUMIO_CLIENT</c>).</summary>
    Client = 1,
}

/// <summary>
/// Generated component / entity-type registry. Gameplay assemblies expose
/// <c>GeneratedRegistry.Instance</c> as a subclass of this type.
/// </summary>
public abstract class EcsRegistry
{
    /// <summary>Most recently constructed generated registry in this process.</summary>
    public static EcsRegistry? Current { get; set; }

    /// <summary>Which side this registry describes. The registry carries the side; callers do not pass a mode flag.</summary>
    public abstract RegistrySide Side { get; }

    /// <summary>CLR type of the unique <c>World = true</c> entity type.</summary>
    public abstract Type WorldEntityType { get; }

    /// <summary>C-2 declaration rows produced with this registry.</summary>
    public abstract IReadOnlyList<FieldAttributeDeclaration> AttributeDeclarations { get; }

    /// <summary>Creates a fresh component bag for <paramref name="entityType"/>.</summary>
    public abstract Component[] CreateComponents(Type entityType);

    /// <summary>Wire name (<c>player</c> / <c>bot</c> / declared name) for an entity CLR type.</summary>
    public abstract string WireName(Type entityType);

    /// <summary>Resolves a declared entity type by wire name or CLR name.</summary>
    public abstract bool TryResolveEntityType(string name, out Type entityType);

    /// <summary>True when <paramref name="query"/> is this type or a declared parent.</summary>
    public abstract bool IsEntityType(Type concrete, Type query);

    /// <summary>Binds generated <see cref="Sync{T}"/> fields and RPC dispatch on <paramref name="component"/>.</summary>
    internal virtual void BindComponent(Component component, ISyncHost host) =>
        Generated(component)?.BindFields(host);

    /// <summary>Looks up a generated component dispatcher when the instance implements it.</summary>
    public static IGeneratedComponent? Generated(Component component) => component as IGeneratedComponent;
}
