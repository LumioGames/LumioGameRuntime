using System;
using Lumio.Gen.ContractTypes;
using Xunit;

namespace Lumio.GameRuntime.Coordination.Tests;

internal static class TxnAuthorityTestData
{
    internal static TxnJournalProof AppendIntent(InMemoryTxnJournalPort journal, TxnRecord record) =>
        Append(journal, TxnIdentity.From(record), TxnJournalStage.CommitIntent);

    internal static void MarkIntent(TxnRecord record)
    {
        var journal = new InMemoryTxnJournalPort();
        TxnJournalProof proof = AppendIntent(journal, record);
        Assert.True(record.MarkCommitIntentPersisted(proof).Succeeded);
    }

    internal static void AppendCommittedCertificate(
        InMemoryTxnJournalPort journal,
        TxnRecord record,
        TxnResultEvidence evidence)
    {
        TxnIdentity identity = TxnIdentity.From(record);
        TxnJournalProof intent = Append(journal, identity, TxnJournalStage.CommitIntent);
        TxnJournalProof voxel = Append(
            journal,
            identity,
            TxnJournalStage.VoxelMarker,
            intent.Checksum,
            evidence.ResultRevision.CanonicalDigestHex);
        TxnJournalProof ecs = Append(
            journal,
            identity,
            TxnJournalStage.EcsMarker,
            intent.Checksum,
            voxel.Checksum,
            evidence.ResultRevision.CanonicalDigestHex,
            evidence.CanonicalDigestHex);
        Append(
            journal,
            identity,
            TxnJournalStage.Committed,
            intent.Checksum,
            voxel.Checksum,
            ecs.Checksum,
            evidence.CanonicalDigestHex,
            evidence.ResultRevision.CanonicalDigestHex);
    }

    internal static void AppendIntentAndVoxelMarker(
        InMemoryTxnJournalPort journal,
        TxnRecord record,
        SessionRevisionVectorView resultRevision)
    {
        TxnIdentity identity = TxnIdentity.From(record);
        TxnJournalProof intent = Append(journal, identity, TxnJournalStage.CommitIntent);
        Append(
            journal,
            identity,
            TxnJournalStage.VoxelMarker,
            intent.Checksum,
            resultRevision.CanonicalDigestHex);
    }

    private static TxnJournalProof Append(
        InMemoryTxnJournalPort journal,
        TxnIdentity identity,
        TxnJournalStage stage,
        params string[] links)
    {
        TxnJournalTailResult tail = journal.ReadTail();
        Assert.True(tail.IsAvailable);
        Assert.NotNull(tail.Checksum);
        TxnJournalRecord row = TxnJournalAuthority.Create(
            identity,
            stage,
            checked(tail.RecordSequence + 1UL),
            tail.Checksum!,
            links);
        TxnJournalAppendResult appended = journal.Append(in row);
        Assert.True(appended.IsDurable);
        Assert.Equal(row.RecordSeq, appended.RecordSequence);
        Assert.Equal(row.Checksum, appended.RecordChecksum);
        Assert.Equal(row.PreviousHash, appended.PreviousHash);
        Assert.True(TxnJournalAuthority.TryValidate(
            row,
            identity,
            stage,
            links,
            out TxnJournalProof? proof,
            out CoordinationFailure? failure),
            failure?.Detail);
        return proof ?? throw new InvalidOperationException("Canonical journal proof was not created.");
    }
}
