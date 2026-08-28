using System.Text;

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

    /// <summary>
    /// 把已装配的 bundle 作为 durable 证据写出。Port 未接受即证据丢失,
    /// 返回 FatalEvidenceWrite 供调用方 fail-stop;写成功返回 null。
    /// </summary>
    internal static ObservabilityFailure? Write(FailureBundleView bundle, IDurableEvidencePort evidence)
    {
        if (bundle is null || evidence is null)
        {
            return ObservabilityFailure.FatalEvidenceWrite(bundle?.FailureId ?? string.Empty);
        }

        var record = new DurableRecordView(
            bundle.FailureId,
            "FailureBundle",
            Encoding.UTF8.GetBytes(bundle.ManifestHash),
            bundle.Correlation);

        return evidence.Enqueue(in record).IsAccepted
            ? null
            : ObservabilityFailure.FatalEvidenceWrite(bundle.FailureId);
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
