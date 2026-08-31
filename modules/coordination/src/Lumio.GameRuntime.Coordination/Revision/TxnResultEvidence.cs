using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

namespace Lumio.GameRuntime.Coordination;

/// <summary>Identity of a transaction result written to the Runtime persistence seam.</summary>
public readonly record struct TxnResultEvidenceIdentity(
    string SessionId,
    string TxnId,
    string CommandId,
    ulong TickId,
    string RequestDigest,
    string ExpectedRevisionDigest,
    string GameReleaseId)
{
    public TxnResultEvidenceIdentity(
        string sessionId,
        string txnId,
        string commandId,
        ulong tickId,
        string requestDigest,
        string expectedRevisionDigest)
        : this(sessionId, txnId, commandId, tickId, requestDigest, expectedRevisionDigest, "runtime")
    {
    }
}

/// <summary>
/// Durable proof that one exact request produced one exact revision vector.
/// This is a Runtime persistence seam, not a generated wire contract.
/// </summary>
public sealed class TxnResultEvidence : IEquatable<TxnResultEvidence>
{
    public TxnResultEvidence(TxnRecord record, SessionRevisionVectorView resultRevision)
        : this(
            record?.SessionId ?? throw new ArgumentNullException(nameof(record)),
            record.TxnId,
            record.CommandId,
            record.TickId,
            record.RequestDigest,
            record.ExpectedRevision,
            resultRevision,
            record.GameReleaseId)
    {
    }

    public TxnResultEvidence(
        string sessionId,
        string txnId,
        string commandId,
        ulong tickId,
        string requestDigest,
        SessionRevisionVectorView expectedRevision,
        SessionRevisionVectorView resultRevision,
        string gameReleaseId = "runtime")
    {
        if (string.IsNullOrWhiteSpace(sessionId)) throw new ArgumentException("A session ID is required.", nameof(sessionId));
        if (string.IsNullOrWhiteSpace(txnId)) throw new ArgumentException("A transaction ID is required.", nameof(txnId));
        if (string.IsNullOrWhiteSpace(commandId)) throw new ArgumentException("A command ID is required.", nameof(commandId));
        if (string.IsNullOrWhiteSpace(requestDigest)) throw new ArgumentException("A request digest is required.", nameof(requestDigest));
        if (string.IsNullOrWhiteSpace(gameReleaseId)) throw new ArgumentException("A game release ID is required.", nameof(gameReleaseId));
        ExpectedRevision = expectedRevision ?? throw new ArgumentNullException(nameof(expectedRevision));
        ResultRevision = resultRevision ?? throw new ArgumentNullException(nameof(resultRevision));
        if (resultRevision.TickId != tickId)
            throw new ArgumentException("Result revision TickId must equal the transaction TickId.", nameof(resultRevision));
        SessionId = sessionId;
        TxnId = txnId;
        CommandId = commandId;
        TickId = tickId;
        RequestDigest = requestDigest;
        GameReleaseId = gameReleaseId;
        Identity = new TxnResultEvidenceIdentity(
            sessionId,
            txnId,
            commandId,
            tickId,
            requestDigest,
            expectedRevision.CanonicalDigestHex,
            gameReleaseId);
    }

    public string SessionId { get; }

    public string TxnId { get; }

    public string CommandId { get; }

    public ulong TickId { get; }

    public string RequestDigest { get; }

    public string GameReleaseId { get; }

    public SessionRevisionVectorView ExpectedRevision { get; }

    public SessionRevisionVectorView ResultRevision { get; }

    public TxnResultEvidenceIdentity Identity { get; }

    public string ResultRevisionDigest => ResultRevision.CanonicalDigestHex;

    public string CanonicalDigestHex => InMemoryTxnResultEvidencePort.Digest(this);

    public bool Matches(TxnRecord record)
    {
        if (record is null) return false;
        return string.Equals(SessionId, record.SessionId, StringComparison.Ordinal) &&
            string.Equals(TxnId, record.TxnId, StringComparison.Ordinal) &&
            string.Equals(CommandId, record.CommandId, StringComparison.Ordinal) &&
            TickId == record.TickId &&
            string.Equals(RequestDigest, record.RequestDigest, StringComparison.Ordinal) &&
            string.Equals(GameReleaseId, record.GameReleaseId, StringComparison.Ordinal) &&
            ExpectedRevision.Equals(record.ExpectedRevision);
    }

    public bool Equals(TxnResultEvidence? other)
    {
        if (ReferenceEquals(this, other)) return true;
        return other is not null &&
            string.Equals(SessionId, other.SessionId, StringComparison.Ordinal) &&
            string.Equals(TxnId, other.TxnId, StringComparison.Ordinal) &&
            string.Equals(CommandId, other.CommandId, StringComparison.Ordinal) &&
            TickId == other.TickId &&
            string.Equals(RequestDigest, other.RequestDigest, StringComparison.Ordinal) &&
            string.Equals(GameReleaseId, other.GameReleaseId, StringComparison.Ordinal) &&
            ExpectedRevision.Equals(other.ExpectedRevision) &&
            ResultRevision.Equals(other.ResultRevision);
    }

    public override bool Equals(object? obj) => obj is TxnResultEvidence other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(
        SessionId,
        TxnId,
        CommandId,
        TickId,
        RequestDigest,
        GameReleaseId,
        ExpectedRevision.CanonicalDigestHex,
        ResultRevision.CanonicalDigestHex);
}

public enum TxnResultEvidenceWriteStatus
{
    Durable,
    AlreadyPresent,
    Retryable,
    Rejected,
    Fatal
}

public readonly record struct TxnResultEvidenceWriteResult(
    TxnResultEvidenceWriteStatus Status,
    string? GeneratedErrorId)
{
    public bool IsDurable => Status is TxnResultEvidenceWriteStatus.Durable or TxnResultEvidenceWriteStatus.AlreadyPresent;

    public static TxnResultEvidenceWriteResult Durable(bool alreadyPresent = false) =>
        new(alreadyPresent ? TxnResultEvidenceWriteStatus.AlreadyPresent : TxnResultEvidenceWriteStatus.Durable, null);

    public static TxnResultEvidenceWriteResult Failure(string errorId, bool fatal = false) =>
        new(fatal ? TxnResultEvidenceWriteStatus.Fatal : TxnResultEvidenceWriteStatus.Rejected, errorId);
}

public enum TxnResultEvidenceReadStatus
{
    Found,
    NotFound,
    Retryable,
    Rejected,
    Fatal
}

public readonly record struct TxnResultEvidenceReadResult(
    TxnResultEvidenceReadStatus Status,
    TxnResultEvidence? Evidence,
    string? GeneratedErrorId)
{
    public bool IsFound => Status == TxnResultEvidenceReadStatus.Found && Evidence is not null;
}

/// <summary>Persistence boundary for the exact result vector of one transaction.</summary>
public interface ITxnResultEvidencePort
{
    TxnResultEvidenceWriteResult Write(in TxnResultEvidence evidence);

    TxnResultEvidenceReadResult Read(string sessionId, string txnId);
}

/// <summary>Optional identity-aware capability for adapters that can key reads atomically.</summary>
public interface IIdentityAwareTxnResultEvidencePort : ITxnResultEvidencePort
{
    TxnResultEvidenceReadResult Read(in TxnResultEvidenceIdentity identity);
}

/// <summary>
/// Deterministic bounded reference implementation for tests. A production
/// persistence adapter must explicitly implement <see cref="ITxnResultEvidencePort"/>
/// and retain the same identity and corruption checks.
/// </summary>
public sealed class InMemoryTxnResultEvidencePort : IIdentityAwareTxnResultEvidencePort
{
    private readonly object _gate = new();
    private readonly int _capacity;
    private readonly Dictionary<string, TxnResultEvidence> _records = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _digests = new(StringComparer.Ordinal);
    private bool _fatal;

    public InMemoryTxnResultEvidencePort(int capacity = 4096)
    {
#if NET10_0_OR_GREATER
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
#else
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
#endif
        _capacity = capacity;
    }

    public int Count
    {
        get { lock (_gate) return _records.Count; }
    }

    public TxnResultEvidenceWriteResult Write(in TxnResultEvidence evidence)
    {
        if (!IsWellFormed(evidence))
            return new TxnResultEvidenceWriteResult(TxnResultEvidenceWriteStatus.Rejected, "ManifestMalformed");
        if (evidence.ExpectedRevision.SchemaEpoch != evidence.ResultRevision.SchemaEpoch)
            return new TxnResultEvidenceWriteResult(TxnResultEvidenceWriteStatus.Rejected, "ManifestUnsupportedVersion");
        if (evidence.ExpectedRevision.Equals(evidence.ResultRevision) ||
            !evidence.ResultRevision.IsMonotonicFrom(evidence.ExpectedRevision))
            return new TxnResultEvidenceWriteResult(TxnResultEvidenceWriteStatus.Rejected, "RevisionConflict");

        string key = Key(evidence.Identity);
        string digest = Digest(evidence);
        lock (_gate)
        {
            if (_fatal)
                return new TxnResultEvidenceWriteResult(TxnResultEvidenceWriteStatus.Fatal, "PanicBoundary");
            if (_records.TryGetValue(key, out TxnResultEvidence? existing))
            {
                return string.Equals(_digests[key], digest, StringComparison.Ordinal)
                    ? TxnResultEvidenceWriteResult.Durable(true)
                    : new TxnResultEvidenceWriteResult(TxnResultEvidenceWriteStatus.Fatal, "EvidenceDigestMismatch");
            }

            if (_records.Count >= _capacity)
                return new TxnResultEvidenceWriteResult(TxnResultEvidenceWriteStatus.Retryable, "QueueFull");

            // Reconstruct through the public constructor so callers never get
            // a mutable reference to the persisted revision dictionaries.
            TxnResultEvidence copy = Copy(evidence);
            _records.Add(key, copy);
            _digests.Add(key, digest);
            return TxnResultEvidenceWriteResult.Durable();
        }
    }

    public TxnResultEvidenceReadResult Read(string sessionId, string txnId)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(txnId))
            return new TxnResultEvidenceReadResult(TxnResultEvidenceReadStatus.Rejected, null, "InvalidArgument");

        lock (_gate)
        {
            if (_fatal)
                return new TxnResultEvidenceReadResult(TxnResultEvidenceReadStatus.Fatal, null, "PanicBoundary");
            TxnResultEvidence? evidence = null;
            string? evidenceKey = null;
            foreach (KeyValuePair<string, TxnResultEvidence> pair in _records)
            {
                if (!string.Equals(pair.Value.SessionId, sessionId, StringComparison.Ordinal) ||
                    !string.Equals(pair.Value.TxnId, txnId, StringComparison.Ordinal)) continue;
                if (evidence is not null)
                    return new TxnResultEvidenceReadResult(
                        TxnResultEvidenceReadStatus.Fatal,
                        null,
                        "EvidenceDigestMismatch");
                evidence = pair.Value;
                evidenceKey = pair.Key;
            }
            if (evidence is null || evidenceKey is null)
                return new TxnResultEvidenceReadResult(TxnResultEvidenceReadStatus.NotFound, null, "EvidenceMissing");
            if (!_digests.TryGetValue(evidenceKey, out string? digest) || !string.Equals(digest, Digest(evidence), StringComparison.Ordinal))
                return new TxnResultEvidenceReadResult(TxnResultEvidenceReadStatus.Fatal, null, "EvidenceDigestMismatch");
            return new TxnResultEvidenceReadResult(TxnResultEvidenceReadStatus.Found, Copy(evidence), null);
        }
    }

    public TxnResultEvidenceReadResult Read(in TxnResultEvidenceIdentity identity)
    {
        string key;
        try { key = Key(identity); }
        catch (ArgumentException)
        {
            return new TxnResultEvidenceReadResult(TxnResultEvidenceReadStatus.Rejected, null, "InvalidArgument");
        }
        lock (_gate)
        {
            if (_fatal)
                return new TxnResultEvidenceReadResult(TxnResultEvidenceReadStatus.Fatal, null, "PanicBoundary");
            if (!_records.TryGetValue(key, out TxnResultEvidence? evidence))
                return new TxnResultEvidenceReadResult(TxnResultEvidenceReadStatus.NotFound, null, "EvidenceMissing");
            if (!evidence.Identity.Equals(identity) || !_digests.TryGetValue(key, out string? digest) ||
                !string.Equals(digest, Digest(evidence), StringComparison.Ordinal))
                return new TxnResultEvidenceReadResult(TxnResultEvidenceReadStatus.Fatal, null, "EvidenceDigestMismatch");
            return new TxnResultEvidenceReadResult(TxnResultEvidenceReadStatus.Found, Copy(evidence), null);
        }
    }

    /// <summary>Test/host hook for simulating a corrupted durable row.</summary>
    public void Corrupt(string sessionId, string txnId, SessionRevisionVectorView replacement)
    {
#if NET10_0_OR_GREATER
        ArgumentNullException.ThrowIfNull(replacement);
#else
        if (replacement is null) throw new ArgumentNullException(nameof(replacement));
#endif
        lock (_gate)
        {
            string? key = null;
            TxnResultEvidence? evidence = null;
            foreach (KeyValuePair<string, TxnResultEvidence> pair in _records)
            {
                if (string.Equals(pair.Value.SessionId, sessionId, StringComparison.Ordinal) &&
                    string.Equals(pair.Value.TxnId, txnId, StringComparison.Ordinal))
                {
                    key = pair.Key;
                    evidence = pair.Value;
                    break;
                }
            }
            if (key is not null && evidence is not null)
            {
                _records[key] = new TxnResultEvidence(
                    evidence.SessionId,
                    evidence.TxnId,
                    evidence.CommandId,
                    evidence.TickId,
                    evidence.RequestDigest,
                    evidence.ExpectedRevision,
                    replacement,
                    evidence.GameReleaseId);
            }
        }
    }

    public void SetFatal()
    {
        lock (_gate) _fatal = true;
    }

    private static bool IsWellFormed(TxnResultEvidence evidence) =>
        evidence is not null &&
        !string.IsNullOrWhiteSpace(evidence.SessionId) &&
        !string.IsNullOrWhiteSpace(evidence.TxnId) &&
        !string.IsNullOrWhiteSpace(evidence.CommandId) &&
        !string.IsNullOrWhiteSpace(evidence.RequestDigest) &&
        evidence.ExpectedRevision is not null && evidence.ResultRevision is not null;

    private static TxnResultEvidence Copy(TxnResultEvidence evidence) => new(
        evidence.SessionId,
        evidence.TxnId,
        evidence.CommandId,
        evidence.TickId,
        evidence.RequestDigest,
        new SessionRevisionVectorView(
            evidence.ExpectedRevision.TickId,
            evidence.ExpectedRevision.GameRevision,
            evidence.ExpectedRevision.VoxelWorldRevision,
            new Dictionary<string, ulong>(evidence.ExpectedRevision.ChunkRevisionSet, StringComparer.Ordinal),
            evidence.ExpectedRevision.ReplicationRevision,
            evidence.ExpectedRevision.ConfigRevision,
            evidence.ExpectedRevision.SchemaEpoch),
        new SessionRevisionVectorView(
            evidence.ResultRevision.TickId,
            evidence.ResultRevision.GameRevision,
            evidence.ResultRevision.VoxelWorldRevision,
            new Dictionary<string, ulong>(evidence.ResultRevision.ChunkRevisionSet, StringComparer.Ordinal),
            evidence.ResultRevision.ReplicationRevision,
            evidence.ResultRevision.ConfigRevision,
            evidence.ResultRevision.SchemaEpoch),
        evidence.GameReleaseId);

    private static string Key(in TxnResultEvidenceIdentity identity) =>
        new TxnIdentity(
            identity.SessionId,
            identity.GameReleaseId,
            identity.TxnId,
            identity.CommandId,
            identity.TickId,
            identity.RequestDigest,
            identity.ExpectedRevisionDigest).DigestHex;

    internal static string Digest(TxnResultEvidence evidence)
    {
        var builder = new StringBuilder();
        Append(builder, evidence.SessionId);
        Append(builder, evidence.TxnId);
        Append(builder, evidence.CommandId);
        Append(builder, evidence.TickId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Append(builder, evidence.RequestDigest);
        Append(builder, evidence.GameReleaseId);
        Append(builder, evidence.ExpectedRevision.CanonicalDigestHex);
        Append(builder, evidence.ResultRevision.CanonicalDigestHex);
#if NET10_0_OR_GREATER
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
#else
        using SHA256 sha = SHA256.Create();
        byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(builder.ToString()));
#endif
        var hex = new StringBuilder(hash.Length * 2);
        foreach (byte value in hash) hex.Append(value.ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
        return hex.ToString();
    }

    private static void Append(StringBuilder builder, string value) =>
        builder.Append(value.Length).Append(':').Append(value).Append(';');
}

/// <summary>Explicit fail-closed capability used by default composition.</summary>
internal sealed class MissingTxnResultEvidencePort : IIdentityAwareTxnResultEvidencePort
{
    public TxnResultEvidenceWriteResult Write(in TxnResultEvidence evidence) =>
        new(TxnResultEvidenceWriteStatus.Rejected, "EvidenceMissing");

    public TxnResultEvidenceReadResult Read(string sessionId, string txnId) =>
        new(TxnResultEvidenceReadStatus.NotFound, null, "EvidenceMissing");

    public TxnResultEvidenceReadResult Read(in TxnResultEvidenceIdentity identity) =>
        new(TxnResultEvidenceReadStatus.NotFound, null, "EvidenceMissing");
}

/// <summary>Identity-aware read helper for persistence adapters implementing the Runtime seam.</summary>
public static class TxnResultEvidencePortExtensions
{
    public static TxnResultEvidenceReadResult Read(
        this ITxnResultEvidencePort port,
        in TxnResultEvidenceIdentity identity)
    {
        if (port is null)
            return new TxnResultEvidenceReadResult(
                TxnResultEvidenceReadStatus.Rejected,
                null,
                "InvalidArgument");
        if (port is IIdentityAwareTxnResultEvidencePort identityAware)
            return identityAware.Read(in identity);
        TxnResultEvidenceReadResult result = port.Read(identity.SessionId, identity.TxnId);
        if (!result.IsFound || result.Evidence is null) return result;
        return result.Evidence.Identity.Equals(identity)
            ? result
            : new TxnResultEvidenceReadResult(
                TxnResultEvidenceReadStatus.Fatal,
                null,
                "EvidenceDigestMismatch");
    }
}
