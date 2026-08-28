namespace Lumio.GameRuntime.Observability;

internal static class FailureBundleAssembler
{
    internal static FailureAssemblyResult Assemble(in FailureContextSnapshot context)
    {
        if (!context.IsWellFormed)
        {
            return new FailureAssemblyResult(FailureAssemblyStatus.Rejected, null, "ManifestMalformed");
        }

        return new FailureAssemblyResult(FailureAssemblyStatus.Assembled, new FailureBundleView(context), null);
    }

    internal static FailureAssemblyResult Verify(FailureBundleView bundle)
    {
        if (bundle is null)
        {
            return new FailureAssemblyResult(FailureAssemblyStatus.Rejected, null, "ManifestMalformed");
        }

        var context = new FailureContextSnapshot(
            bundle.FailureId,
            bundle.ReasonCode,
            bundle.IncidentKind,
            bundle.CreatedAt,
            bundle.Correlation,
            bundle.ManifestHash,
            bundle.SnapshotId,
            bundle.NoSnapshotReason,
            bundle.BootstrapPhase,
            bundle.LastKnownRevision,
            bundle.LastKnownManifest,
            bundle.Artifacts,
            bundle.Reproducible,
            bundle.ReplayCommand);
        return context.IsWellFormed
            ? new FailureAssemblyResult(FailureAssemblyStatus.Assembled, bundle, null)
            : new FailureAssemblyResult(FailureAssemblyStatus.Fatal, null, "ManifestDigestMismatch");
    }
}
