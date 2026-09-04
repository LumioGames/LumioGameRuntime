using Lumio.GameRuntime.Command;

namespace Lumio.GameRuntime.Command.Tests;

internal sealed class AllowAllCommandValidationContext : ICommandValidationContext
{
    public static AllowAllCommandValidationContext Instance { get; } = new();
    public bool IsKnownComponent(string componentType) => true;
    public bool IsKnownField(string componentType, string fieldName) => true;
    public bool EntityExists(string entityId) => true;
    public bool CanWrite(string processorId, Command command) => true;
}
