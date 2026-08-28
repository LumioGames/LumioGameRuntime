namespace Lumio.GameRuntime.Observability;

internal enum ObservabilityFailureClass
{
    Rejected,
    Retryable,
    Fatal,
    FatalInvariant
}

internal readonly record struct ObservabilityFailure(
    ObservabilityFailureClass Class,
    string GeneratedErrorId,
    string? EvidenceReference)
{
    /// <summary>
    /// 证据写入失败的稳定 identity。取自 generated
    /// <c>Lumio.Gen.ContractTypes.Catalog.StableErrorIds</c>,不自造错误码。
    /// </summary>
    internal const string FatalEvidenceWriteErrorId = "EvidenceMissing";

    /// <summary>
    /// Failure Bundle 落盘失败没有降级路径:证据写不下去就等于事故不可复现,
    /// 必须一次性升级为 Fatal,不得 catch-and-continue(T05.S07)。
    /// </summary>
    internal static ObservabilityFailure FatalEvidenceWrite(string evidenceReference) =>
        new(ObservabilityFailureClass.Fatal, FatalEvidenceWriteErrorId, evidenceReference);
}
