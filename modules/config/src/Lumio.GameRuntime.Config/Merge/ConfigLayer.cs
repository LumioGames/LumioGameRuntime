namespace Lumio.GameRuntime.Config;

/// <summary>
/// Fixed six-layer precedence. Larger ordinals merge later and win.
/// Order is Engine, Platform, Server, Product, Environment, UserOrSession.
/// Callers cannot reorder or add a seventh layer.
/// </summary>
public enum ConfigLayer
{
    /// <summary>Engine defaults (lowest precedence).</summary>
    Engine = 0,

    /// <summary>Platform layer.</summary>
    Platform = 1,

    /// <summary>Server layer.</summary>
    Server = 2,

    /// <summary>Product layer.</summary>
    Product = 3,

    /// <summary>Environment layer.</summary>
    Environment = 4,

    /// <summary>User or session layer (highest precedence).</summary>
    UserOrSession = 5,
}

/// <summary>One validated layer input. <see cref="ConfigLayerMerger"/> accepts only this type.</summary>
internal readonly record struct ValidatedConfigLayer(
    ConfigLayer Layer,
    GeneratedConfigArtifactView Artifact);
