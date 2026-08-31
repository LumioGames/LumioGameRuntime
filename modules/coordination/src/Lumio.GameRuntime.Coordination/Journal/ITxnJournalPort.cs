using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Lumio.Gen.ContractTypes;

namespace Lumio.GameRuntime.Coordination;

public enum TxnJournalAppendStatus
{
    Durable,
    Backpressured,
    Rejected,
    Fatal
}

public readonly record struct TxnJournalAppendResult(
    TxnJournalAppendStatus Status,
    ulong RecordSequence,
    bool AlreadyPresent,
    string? GeneratedErrorId,
    string? RecordChecksum = null,
    string? PreviousHash = null)
{
    public bool IsDurable => Status == TxnJournalAppendStatus.Durable;
}

public enum TxnJournalQueryStatus
{
    Found,
    NotFound,
    Retryable,
    Fatal
}

public readonly record struct TxnJournalQueryResult(
    TxnJournalQueryStatus Status,
    IReadOnlyList<TxnJournalRecord> Records,
    string? GeneratedErrorId)
{
    public bool IsFound => Status == TxnJournalQueryStatus.Found;
}

public enum TxnJournalTailStatus
{
    Available,
    Retryable,
    Fatal
}

public readonly record struct TxnJournalTailResult(
    TxnJournalTailStatus Status,
    ulong RecordSequence,
    string? Checksum,
    string? GeneratedErrorId)
{
    public bool IsAvailable => Status == TxnJournalTailStatus.Available && Checksum is not null;
}

/// <summary>Durable journal boundary owned by the caller (normally Persistence).</summary>
public interface ITxnJournalPort
{
    TxnJournalAppendResult Append(in TxnJournalRecord record);

    TxnJournalQueryResult Query(string sessionId, string txnId);

    TxnJournalTailResult ReadTail() =>
        new(TxnJournalTailStatus.Fatal, 0UL, null, "CapabilityMissing");
}

/// <summary>Small bounded reference journal for deterministic tests; it is never a production default.</summary>
public sealed class InMemoryTxnJournalPort : ITxnJournalPort
{
    private readonly object _gate = new();
    private readonly int _capacity;
    private readonly List<TxnJournalRecord> _records = new();
    private readonly Dictionary<string, ulong> _keys = new(StringComparer.Ordinal);
    private ulong _nextSequence;
    private bool _fatal;

    public InMemoryTxnJournalPort(int capacity = 4096)
    {
#if NET10_0_OR_GREATER
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
#else
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
#endif
        _capacity = capacity;
    }

    public bool IsFatal
    {
        get { lock (_gate) return _fatal; }
    }

    public int Count
    {
        get { lock (_gate) return _records.Count; }
    }

    public IReadOnlyList<TxnJournalRecord> Records
    {
        get { lock (_gate) return new List<TxnJournalRecord>(_records).AsReadOnly(); }
    }

    public TxnJournalAppendResult Append(in TxnJournalRecord record)
    {
        if (record is null || string.IsNullOrWhiteSpace(record.TxnId) || string.IsNullOrWhiteSpace(record.IdempotencyKey))
            return new TxnJournalAppendResult(TxnJournalAppendStatus.Rejected, 0UL, false, "ManifestMalformed");

        lock (_gate)
        {
            if (_fatal) return new TxnJournalAppendResult(TxnJournalAppendStatus.Fatal, 0UL, false, "PanicBoundary");
            if (_keys.TryGetValue(record.IdempotencyKey, out ulong existing))
            {
                foreach (TxnJournalRecord prior in _records)
                {
                    if (string.Equals(prior.IdempotencyKey, record.IdempotencyKey, StringComparison.Ordinal) &&
                        // Idempotency keys identify the logical record. A
                        // replay may be reconstructed after a process restart
                        // with a fresh sequence/chain cursor, so chain fields
                        // are deliberately ignored for duplicate comparison;
                        // payload and semantic fields remain exact.
                        !Equivalent(prior, record, true))
                    {
                        _fatal = true;
                        return new TxnJournalAppendResult(TxnJournalAppendStatus.Fatal, 0UL, false, "InternalInvariant");
                    }
                }

                string idempotencyKey = record.IdempotencyKey;
                TxnJournalRecord existingRecord = _records.Find(item =>
                    string.Equals(item.IdempotencyKey, idempotencyKey, StringComparison.Ordinal))!;
                return new TxnJournalAppendResult(
                    TxnJournalAppendStatus.Durable,
                    existing,
                    true,
                    null,
                    existingRecord.Checksum,
                    existingRecord.PreviousHash);
            }
            if (_records.Count >= _capacity)
                return new TxnJournalAppendResult(TxnJournalAppendStatus.Backpressured, 0UL, false, "QueueFull");

            ulong expectedSequence;
            try { expectedSequence = checked(_nextSequence + 1UL); }
            catch (OverflowException)
            {
                _fatal = true;
                return new TxnJournalAppendResult(TxnJournalAppendStatus.Fatal, 0UL, false, "InternalInvariant");
            }

            ulong sequence = record.RecordSeq == 0UL ? expectedSequence : record.RecordSeq;
            if (sequence != expectedSequence)
            {
                if (sequence < expectedSequence)
                    return new TxnJournalAppendResult(
                        TxnJournalAppendStatus.Backpressured,
                        0UL,
                        false,
                        "RevisionConflict");
                _fatal = true;
                return new TxnJournalAppendResult(TxnJournalAppendStatus.Fatal, 0UL, false, "InternalInvariant");
            }

            string expectedPrevious = _records.Count == 0 ? new string('0', 64) : _records[_records.Count - 1].Checksum;
            string previous = record.RecordSeq == 0UL && IsZeroHash(record.PreviousHash)
                ? expectedPrevious
                : record.PreviousHash;
            if (!IsHash(previous) || !string.Equals(previous, expectedPrevious, StringComparison.Ordinal))
            {
                _fatal = true;
                return new TxnJournalAppendResult(TxnJournalAppendStatus.Fatal, 0UL, false, "InternalInvariant");
            }

            if (!IsHash(record.PayloadHash) || !IsHash(record.Checksum) || record.RecordVersion == 0UL ||
                !IsValidId(record.SessionId) || !IsValidId(record.GameReleaseId) || !IsValidId(record.TxnId) ||
                !IsValidId(record.IdempotencyKey))
            {
                return new TxnJournalAppendResult(TxnJournalAppendStatus.Rejected, 0UL, false, "ManifestMalformed");
            }

            string checksum = TxnJournalRecordFactory.ComputeChecksum(sequence, previous, record.PayloadHash, record.IdempotencyKey);
            if (record.RecordSeq != 0UL && !string.Equals(record.Checksum, checksum, StringComparison.Ordinal))
            {
                _fatal = true;
                return new TxnJournalAppendResult(TxnJournalAppendStatus.Fatal, 0UL, false, "InternalInvariant");
            }

            TxnJournalRecord normalized = new(
                record.RecordVersion,
                sequence,
                previous,
                record.PayloadHash,
                record.Length,
                checksum,
                record.CommitState,
                record.DurabilityState,
                record.SessionId,
                record.GameReleaseId,
                record.TickId,
                record.TxnId,
                record.CommandId,
                record.RecordKind,
                record.IdempotencyKey);
            _nextSequence = sequence;
            _records.Add(normalized);
            _keys.Add(normalized.IdempotencyKey, sequence);
            return new TxnJournalAppendResult(
                TxnJournalAppendStatus.Durable,
                sequence,
                false,
                null,
                normalized.Checksum,
                normalized.PreviousHash);
        }
    }

    public TxnJournalQueryResult Query(string sessionId, string txnId)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(txnId))
            return new TxnJournalQueryResult(TxnJournalQueryStatus.Fatal, Array.Empty<TxnJournalRecord>(), "InvalidArgument");

        lock (_gate)
        {
            if (_fatal) return new TxnJournalQueryResult(TxnJournalQueryStatus.Fatal, Array.Empty<TxnJournalRecord>(), "PanicBoundary");
            var result = new List<TxnJournalRecord>();
            foreach (TxnJournalRecord record in _records)
            {
                if (string.Equals(record.SessionId, sessionId, StringComparison.Ordinal) &&
                    string.Equals(record.TxnId, txnId, StringComparison.Ordinal)) result.Add(record);
            }

            return new TxnJournalQueryResult(
                result.Count == 0 ? TxnJournalQueryStatus.NotFound : TxnJournalQueryStatus.Found,
                result.AsReadOnly(), null);
        }
    }

    public void SetFatal() { lock (_gate) _fatal = true; }

    public TxnJournalTailResult ReadTail()
    {
        lock (_gate)
        {
            if (_fatal)
                return new TxnJournalTailResult(TxnJournalTailStatus.Fatal, 0UL, null, "PanicBoundary");
            if (_records.Count == 0)
            {
                return new TxnJournalTailResult(
                    TxnJournalTailStatus.Available,
                    0UL,
                    new string('0', 64),
                    null);
            }

            TxnJournalRecord tail = _records[_records.Count - 1];
            return new TxnJournalTailResult(
                TxnJournalTailStatus.Available,
                tail.RecordSeq,
                tail.Checksum,
                null);
        }
    }

    private static bool Equivalent(TxnJournalRecord left, TxnJournalRecord right, bool ignoreChainFields) =>
        left.RecordVersion == right.RecordVersion &&
        (ignoreChainFields || left.RecordSeq == right.RecordSeq) &&
        (ignoreChainFields || string.Equals(left.PreviousHash, right.PreviousHash, StringComparison.Ordinal)) &&
        string.Equals(left.PayloadHash, right.PayloadHash, StringComparison.Ordinal) &&
        left.Length == right.Length &&
        left.CommitState == right.CommitState &&
        left.DurabilityState == right.DurabilityState &&
        string.Equals(left.SessionId, right.SessionId, StringComparison.Ordinal) &&
        string.Equals(left.GameReleaseId, right.GameReleaseId, StringComparison.Ordinal) &&
        left.TickId == right.TickId &&
        string.Equals(left.TxnId, right.TxnId, StringComparison.Ordinal) &&
        string.Equals(left.CommandId, right.CommandId, StringComparison.Ordinal) &&
        left.RecordKind == right.RecordKind &&
        (ignoreChainFields || string.Equals(left.Checksum, right.Checksum, StringComparison.Ordinal));

    private static bool IsZeroHash(string value) => IsHash(value) && value == new string('0', 64);

    private static bool IsHash(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length != 64) return false;
        foreach (char c in value)
        {
            if (!(c is >= '0' and <= '9' or >= 'a' and <= 'f')) return false;
        }

        return true;
    }

    private static bool IsValidId(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 128) return false;
        if (!(value[0] is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9')) return false;
        for (int index = 1; index < value.Length; index++)
        {
            char c = value[index];
            if (!(c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '_' or ':' or '-')) return false;
        }

        return true;
    }
}

/// <summary>Creates generated journal records with deterministic hashes and no object-address data.</summary>
public static class TxnJournalRecordFactory
{
    public static TxnJournalRecord Create(
        string sessionId,
        string gameReleaseId,
        ulong tickId,
        string txnId,
        TxnJournalRecordRecordKind kind,
        string idempotencyKey,
        TxnJournalRecordCommitState commitState = TxnJournalRecordCommitState.Pending,
        TxnJournalRecordDurabilityState durabilityState = TxnJournalRecordDurabilityState.Durable,
        string? commandId = null,
        ulong recordSeq = 0UL,
        string? previousHash = null,
        ReadOnlyMemory<byte> payload = default)
    {
        byte[] payloadHashBytes = Hash(payload.IsEmpty ? Encoding.UTF8.GetBytes(string.Concat(sessionId, "|", txnId, "|", kind)) : payload.ToArray());
        string payloadHash = ToHex(payloadHashBytes);
        string previous = previousHash ?? new string('0', 64);
        string checksum = ComputeChecksum(recordSeq, previous, payloadHash, idempotencyKey);
        return new TxnJournalRecord(
            1UL,
            recordSeq,
            previous,
            payloadHash,
            (ulong)payload.Length,
            checksum,
            commitState,
            durabilityState,
            sessionId,
            gameReleaseId,
            tickId,
            txnId,
            commandId,
            kind,
            idempotencyKey);
    }

    internal static string ComputeChecksum(ulong recordSeq, string previousHash, string payloadHash, string idempotencyKey) =>
        ToHex(Hash(Encoding.UTF8.GetBytes(string.Concat(
            recordSeq.ToString(CultureInfo.InvariantCulture), "|", previousHash, "|", payloadHash, "|", idempotencyKey))));

    private static byte[] Hash(byte[] bytes)
    {
#if NET10_0_OR_GREATER
        return SHA256.HashData(bytes);
#else
        using SHA256 sha = SHA256.Create();
        return sha.ComputeHash(bytes);
#endif
    }

    private static string ToHex(byte[] bytes)
    {
        var builder = new StringBuilder(bytes.Length * 2);
        foreach (byte value in bytes) builder.Append(value.ToString("x2", CultureInfo.InvariantCulture));
        return builder.ToString();
    }
}
