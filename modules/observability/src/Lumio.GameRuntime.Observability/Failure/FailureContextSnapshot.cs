using System;
using System.Collections.Generic;

namespace Lumio.GameRuntime.Observability;

public readonly record struct FailureArtifactView(string Name, string Sha256, long Size)
{
    public bool IsWellFormed =>
        !string.IsNullOrWhiteSpace(Name) &&
        !string.IsNullOrWhiteSpace(Sha256) &&
        Sha256.Length == 64 &&
        IsLowerHex(Sha256) &&
        Size >= 0;

    private static bool IsLowerHex(string value)
    {
        foreach (var character in value)
        {
            if (!((character >= '0' && character <= '9') || (character >= 'a' && character <= 'f')))
            {
                return false;
            }
        }

        return true;
    }
}

internal readonly record struct FailureContextSnapshot(
    string FailureId,
    string ReasonCode,
    string IncidentKind,
    DateTimeOffset CreatedAt,
    CorrelationView Correlation,
    string ManifestHash,
    string? SnapshotId,
    string? NoSnapshotReason,
    string? BootstrapPhase,
    string? LastKnownRevision,
    string? LastKnownManifest,
    IReadOnlyList<FailureArtifactView> Artifacts,
    bool Reproducible,
    string? ReplayCommand)
{
    internal bool IsWellFormed
    {
        get
        {
            var hasSnapshot = !string.IsNullOrWhiteSpace(SnapshotId);
            var hasReason = !string.IsNullOrWhiteSpace(NoSnapshotReason);
            if (hasSnapshot == hasReason || !Correlation.IsComplete || Artifacts is null || Artifacts.Count == 0)
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(ManifestHash) || ManifestHash.Length != 64 || !IsLowerHex(ManifestHash)) return false;
            if (!hasSnapshot && (LastKnownManifest is null || LastKnownManifest.Length != 64 || !IsLowerHex(LastKnownManifest))) return false;
            foreach (var artifact in Artifacts)
            {
                if (!artifact.IsWellFormed) return false;
            }

            if (!hasSnapshot && (string.IsNullOrWhiteSpace(BootstrapPhase) ||
                string.IsNullOrWhiteSpace(LastKnownRevision) || string.IsNullOrWhiteSpace(LastKnownManifest)))
            {
                return false;
            }

            return !string.IsNullOrWhiteSpace(FailureId) &&
                IsReasonCode(ReasonCode) &&
                IsIncidentKind(IncidentKind) &&
                CreatedAt.Offset == TimeSpan.Zero &&
                (ReplayCommand is null || ReplayCommand.Length <= 1024) &&
                (BootstrapPhase is null || BootstrapPhase.Length <= 128);
        }
    }

    private static bool IsReasonCode(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 64 || value.Length < 3 ||
            value[0] < 'A' || value[0] > 'Z') return false;
        foreach (var character in value)
        {
            if (!((character >= 'A' && character <= 'Z') || (character >= '0' && character <= '9') || character == '_'))
                return false;
        }
        return true;
    }

    private static bool IsIncidentKind(string value) =>
        value is "Simulation" or "CoreEngineLoad" or "SupplyChain" or "BuildValidation";

    private static bool IsLowerHex(string value)
    {
        foreach (var character in value)
        {
            if (!((character >= '0' && character <= '9') || (character >= 'a' && character <= 'f')))
            {
                return false;
            }
        }

        return true;
    }
}

public sealed class FailureBundleView
{
    internal FailureBundleView(FailureContextSnapshot context)
    {
        FailureId = context.FailureId;
        ReasonCode = context.ReasonCode;
        IncidentKind = context.IncidentKind;
        CreatedAt = context.CreatedAt;
        Correlation = context.Correlation;
        ManifestHash = context.ManifestHash;
        SnapshotId = context.SnapshotId;
        NoSnapshotReason = context.NoSnapshotReason;
        BootstrapPhase = context.BootstrapPhase;
        LastKnownRevision = context.LastKnownRevision;
        LastKnownManifest = context.LastKnownManifest;
        Artifacts = new List<FailureArtifactView>(context.Artifacts).AsReadOnly();
        Reproducible = context.Reproducible;
        ReplayCommand = context.ReplayCommand;
    }

    public string FailureId { get; }
    public string ReasonCode { get; }
    public string IncidentKind { get; }
    public DateTimeOffset CreatedAt { get; }
    public CorrelationView Correlation { get; }
    public string ManifestHash { get; }
    public string? SnapshotId { get; }
    public string? NoSnapshotReason { get; }
    public string? BootstrapPhase { get; }
    public string? LastKnownRevision { get; }
    public string? LastKnownManifest { get; }
    public IReadOnlyList<FailureArtifactView> Artifacts { get; }
    public bool Reproducible { get; }
    public string? ReplayCommand { get; }
}

public enum FailureAssemblyStatus
{
    Assembled,
    Rejected,
    Fatal
}

public readonly record struct FailureAssemblyResult(
    FailureAssemblyStatus Status,
    FailureBundleView? Bundle,
    string? GeneratedErrorId)
{
    public bool IsAssembled => Status == FailureAssemblyStatus.Assembled;
}
