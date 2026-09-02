using System;

namespace Lumio.GameRuntime.Ecs.Annotations;

internal static class AnnotationGuard
{
    internal static void NotNull(object? value, string name)
    {
#if NET10_0_OR_GREATER
        ArgumentNullException.ThrowIfNull(value, name);
#else
        if (value is null) throw new ArgumentNullException(name);
#endif
    }
}
