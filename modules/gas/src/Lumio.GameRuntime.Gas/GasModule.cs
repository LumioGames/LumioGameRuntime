using System;
using System.Runtime.CompilerServices;
using Lumio.GameRuntime.Ecs;
using Lumio.Gen.ContractTypes;

[assembly: InternalsVisibleTo("Lumio.GameRuntime.Gas.Tests")]

namespace Lumio.GameRuntime.Gas;

internal static class GasErrorIds
{
    internal const string InvalidArgument = "InvalidArgument";
    internal const string InvalidHandle = "InvalidHandle";
    internal const string WrongContext = "WrongContext";
    internal const string ContextClosing = "ContextClosing";
    internal const string ContextDestroyed = "ContextDestroyed";
    internal const string InternalInvariant = "InternalInvariant";
    internal const string PackageIdentityConflict = "PackageIdentityConflict";
    internal const string StaleEpoch = "StaleEpoch";

    internal static bool IsGenerated(string value)
    {
        foreach (string errorId in Catalog.StableErrorIds)
        {
            if (string.Equals(errorId, value, StringComparison.Ordinal))
                return true;
        }

        return false;
    }
}

/// <summary>GAS module facade. World contexts are independent and do not share handle tables.</summary>
public sealed class GasModule
{
    private readonly GasTypeRegistry _types = new();
    private readonly GasServices _services;

    private GasModule()
    {
        _services = new GasServices(this);
    }

    public static GasModule Create() => new();

    public GasServices Services => _services;

    public GasTypeRegistry Types => _types;

    public GasWorldContext CreateWorldContext(WorldId worldId, IGasEcsProjectionPort projection) =>
        new(worldId, projection, _types);
}
