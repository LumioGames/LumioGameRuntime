using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Lumio.Gen.ContractTypes;

namespace Lumio.GameRuntime.Coordination;

internal enum TxnJournalStage
{
    CommitIntent,
    VoxelMarker,
    EcsMarker,
    Committed
}

internal sealed record TxnJournalProof(
    TxnIdentity Identity,
    TxnJournalStage Stage,
    ulong RecordSequence,
    string PreviousHash,
    string PayloadHash,
    string Checksum,
    string IdempotencyKey);

internal sealed record TxnCommitCertificate(
    TxnAuthorityOperation Operation,
    TxnJournalProof Intent,
    TxnJournalProof VoxelMarker,
    TxnJournalProof EcsMarker,
    TxnJournalProof Terminal,
    TxnResultEvidence Evidence,
    SessionRevisionVectorView ResultRevision);

internal static class TxnJournalAuthority
{
    internal static string Key(TxnIdentity identity, TxnJournalStage stage) =>
        string.Concat(identity.DigestHex, ".", StageCode(stage));

    internal static TxnJournalRecord Create(
        TxnIdentity identity,
        TxnJournalStage stage,
        ulong sequence,
        string previousHash,
        params string[] links)
    {
        ReadOnlyMemory<byte> payload = Payload(identity, stage, links);
        return TxnJournalRecordFactory.Create(
            identity.SessionId,
            identity.GameReleaseId,
            identity.TickId,
            identity.TxnId,
            Kind(stage),
            Key(identity, stage),
            CommitState(stage),
            TxnJournalRecordDurabilityState.Durable,
            identity.CommandId,
            sequence,
            previousHash,
            payload);
    }

    internal static bool TryValidateRecordSet(
        IReadOnlyList<TxnJournalRecord> records,
        TxnIdentity identity,
        out CoordinationFailure? failure)
    {
        foreach (TxnJournalRecord record in records)
        {
            if (record is null)
            {
                failure = CoordinationFailure.Fatal("EvidenceDigestMismatch", "Journal query returned a null record.");
                return false;
            }
            if (record.RecordKind is not (
                    TxnJournalRecordRecordKind.CommitIntent or
                    TxnJournalRecordRecordKind.ParticipantMarker or
                    TxnJournalRecordRecordKind.Committed))
                continue;

            if (!string.Equals(record.SessionId, identity.SessionId, StringComparison.Ordinal) ||
                !string.Equals(record.GameReleaseId, identity.GameReleaseId, StringComparison.Ordinal) ||
                !string.Equals(record.TxnId, identity.TxnId, StringComparison.Ordinal) ||
                !string.Equals(record.CommandId, identity.CommandId, StringComparison.Ordinal) ||
                record.TickId != identity.TickId)
            {
                failure = CoordinationFailure.Fatal(
                    "EvidenceDigestMismatch",
                    "A proof-bearing journal record belongs to another transaction identity.");
                return false;
            }

            TxnJournalStage? stage = StageFromKey(record.IdempotencyKey, identity);
            if (stage is null || record.RecordKind != Kind(stage.Value))
            {
                failure = CoordinationFailure.Fatal(
                    "EvidenceDigestMismatch",
                    "A proof-bearing journal record does not use an exact closed stage key.");
                return false;
            }
        }

        failure = null;
        return true;
    }

    internal static bool TryFind(
        IReadOnlyList<TxnJournalRecord> records,
        TxnIdentity identity,
        TxnJournalStage stage,
        string[] links,
        out TxnJournalProof? proof,
        out CoordinationFailure? failure)
    {
        proof = null;
        failure = null;
        string key = Key(identity, stage);
        foreach (TxnJournalRecord record in records)
        {
            if (!string.Equals(record.IdempotencyKey, key, StringComparison.Ordinal)) continue;
            if (proof is not null)
            {
                failure = CoordinationFailure.Fatal("InternalInvariant", "A journal stage appears more than once.");
                return false;
            }

            if (!TryValidate(record, identity, stage, links, out TxnJournalProof? candidate, out failure))
                return false;
            proof = candidate;
        }

        return true;
    }

    internal static bool TryValidate(
        TxnJournalRecord record,
        TxnIdentity identity,
        TxnJournalStage stage,
        string[] links,
        out TxnJournalProof? proof,
        out CoordinationFailure? failure)
    {
        proof = null;
        if (record is null || record.RecordSeq == 0UL ||
            !IsHash(record.PreviousHash) || !IsHash(record.PayloadHash) || !IsHash(record.Checksum) ||
            record.RecordKind != Kind(stage) || record.CommitState != CommitState(stage) ||
            record.DurabilityState != TxnJournalRecordDurabilityState.Durable ||
            !string.Equals(record.SessionId, identity.SessionId, StringComparison.Ordinal) ||
            !string.Equals(record.GameReleaseId, identity.GameReleaseId, StringComparison.Ordinal) ||
            !string.Equals(record.TxnId, identity.TxnId, StringComparison.Ordinal) ||
            !string.Equals(record.CommandId, identity.CommandId, StringComparison.Ordinal) ||
            record.TickId != identity.TickId ||
            !string.Equals(record.IdempotencyKey, Key(identity, stage), StringComparison.Ordinal))
        {
            failure = CoordinationFailure.Fatal("EvidenceDigestMismatch", "Journal stage identity does not match the transaction.");
            return false;
        }

        TxnJournalRecord expected = Create(identity, stage, record.RecordSeq, record.PreviousHash, links);
        string checksum = TxnJournalRecordFactory.ComputeChecksum(
            record.RecordSeq,
            record.PreviousHash,
            record.PayloadHash,
            record.IdempotencyKey);
        if (record.RecordVersion != expected.RecordVersion || record.Length != expected.Length ||
            !string.Equals(record.PayloadHash, expected.PayloadHash, StringComparison.Ordinal) ||
            !string.Equals(record.Checksum, checksum, StringComparison.Ordinal))
        {
            failure = CoordinationFailure.Fatal("EvidenceDigestMismatch", "Journal stage payload or checksum is invalid.");
            return false;
        }

        proof = new TxnJournalProof(identity, stage, record.RecordSeq, record.PreviousHash,
            record.PayloadHash, record.Checksum, record.IdempotencyKey);
        failure = null;
        return true;
    }

    private static bool IsHash(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length != 64) return false;
        foreach (char character in value)
            if (!(character is >= '0' and <= '9' or >= 'a' and <= 'f')) return false;
        return true;
    }

    internal static byte[] Payload(TxnIdentity identity, TxnJournalStage stage, params string[] links)
    {
        using var stream = new MemoryStream();
        byte[] identityBytes = identity.CanonicalBytes.ToArray();
        Write(stream, identityBytes);
        stream.WriteByte((byte)stage);
        byte[] linkCount = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(linkCount, links.Length);
        Write(stream, linkCount);
        foreach (string link in links) Write(stream, Encoding.UTF8.GetBytes(link ?? string.Empty));
        return stream.ToArray();
    }

    private static void Write(Stream stream, byte[] bytes)
    {
        byte[] length = BitConverter.GetBytes(bytes.Length);
        if (BitConverter.IsLittleEndian) Array.Reverse(length);
        stream.Write(length, 0, length.Length);
        stream.Write(bytes, 0, bytes.Length);
    }

    private static string StageCode(TxnJournalStage stage) => stage switch
    {
        TxnJournalStage.CommitIntent => "ci",
        TxnJournalStage.VoxelMarker => "vm",
        TxnJournalStage.EcsMarker => "em",
        TxnJournalStage.Committed => "co",
        _ => throw new ArgumentOutOfRangeException(nameof(stage))
    };

    private static TxnJournalStage? StageFromKey(string key, TxnIdentity identity)
    {
        if (string.Equals(key, Key(identity, TxnJournalStage.CommitIntent), StringComparison.Ordinal))
            return TxnJournalStage.CommitIntent;
        if (string.Equals(key, Key(identity, TxnJournalStage.VoxelMarker), StringComparison.Ordinal))
            return TxnJournalStage.VoxelMarker;
        if (string.Equals(key, Key(identity, TxnJournalStage.EcsMarker), StringComparison.Ordinal))
            return TxnJournalStage.EcsMarker;
        if (string.Equals(key, Key(identity, TxnJournalStage.Committed), StringComparison.Ordinal))
            return TxnJournalStage.Committed;
        return null;
    }

    private static TxnJournalRecordRecordKind Kind(TxnJournalStage stage) => stage switch
    {
        TxnJournalStage.CommitIntent => TxnJournalRecordRecordKind.CommitIntent,
        TxnJournalStage.VoxelMarker or TxnJournalStage.EcsMarker => TxnJournalRecordRecordKind.ParticipantMarker,
        TxnJournalStage.Committed => TxnJournalRecordRecordKind.Committed,
        _ => throw new ArgumentOutOfRangeException(nameof(stage))
    };

    private static TxnJournalRecordCommitState CommitState(TxnJournalStage stage) =>
        stage == TxnJournalStage.Committed
            ? TxnJournalRecordCommitState.Committed
            : TxnJournalRecordCommitState.Pending;
}
