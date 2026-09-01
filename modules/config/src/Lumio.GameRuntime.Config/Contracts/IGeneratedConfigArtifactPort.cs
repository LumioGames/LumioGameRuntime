using System.Collections.Generic;
using Lumio.Gen.ContractTypes;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Lumio.GameRuntime.Config.Tests")]

namespace Lumio.GameRuntime.Config;

/// <summary>
/// Host/Dev entry that delivers a toolchain-generated config artifact.
/// Accepts generated views only; compilation belongs to Game/Toolchain.
/// </summary>
public interface IGeneratedConfigArtifactPort
{
    /// <summary>Submit one generated artifact. Concurrent submits are serialized by staging.</summary>
    /// <param name="artifact">Generated table view plus the layer it declares.</param>
    /// <returns>Accepted, or Rejected with a stable error id.</returns>
    ConfigSubmitResult Submit(in GeneratedConfigArtifactView artifact);
}

/// <summary>
/// Read-only view of one generated artifact: id, declared layer, and
/// generated <see cref="ConfigTable"/> rows whose values are canonical JSON.
/// </summary>
public sealed record GeneratedConfigArtifactView(
    string ArtifactId,
    ConfigLayer DeclaredLayer,
    IReadOnlyList<ConfigTable> Tables)
{
    /// <summary>Non-empty artifact id and at least one table.</summary>
    public bool IsWellFormed =>
        !string.IsNullOrWhiteSpace(ArtifactId) &&
        Tables is { Count: > 0 };
}

/// <summary>Submit outcome.</summary>
public enum ConfigSubmitStatus
{
    /// <summary>Entered the validate/merge pipeline.</summary>
    Accepted,

    /// <summary>Rejected (malformed, over budget, or validation failed).</summary>
    Rejected,
}

/// <summary>Submit result. Rejected carries a stable error id.</summary>
public readonly record struct ConfigSubmitResult(ConfigSubmitStatus Status, string? ErrorId)
{
    /// <summary>Accepted result.</summary>
    public static ConfigSubmitResult Accepted() => new(ConfigSubmitStatus.Accepted, null);

    /// <summary>Rejected result.</summary>
    public static ConfigSubmitResult Rejected(string errorId) => new(ConfigSubmitStatus.Rejected, errorId);
}
