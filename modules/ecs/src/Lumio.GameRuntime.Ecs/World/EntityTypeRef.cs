using System;

namespace Lumio.GameRuntime.Ecs;

/// <summary>Handle returned by <see cref="World.TypeOf"/>. <see cref="Is{T}"/> is true for declared parents.</summary>
public readonly struct EntityTypeRef
{
    internal EntityTypeRef(Type concrete, EcsRegistry registry)
    {
        Concrete = concrete;
        Registry = registry;
    }

    internal Type Concrete { get; }
    internal EcsRegistry Registry { get; }

    /// <summary>True when the entity is <typeparamref name="T"/> or a declared subtype of it.</summary>
    public bool Is<T>() where T : class
    {
        if (Concrete is null || Registry is null) return false;
        return Registry.IsEntityType(Concrete, typeof(T));
    }

    /// <summary>CLR type of the concrete entity declaration.</summary>
    public Type ClrType => Concrete;
}
