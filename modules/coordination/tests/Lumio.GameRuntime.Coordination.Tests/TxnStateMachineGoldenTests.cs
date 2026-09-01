using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Lumio.Gen.ContractTypes;
using Xunit;

namespace Lumio.GameRuntime.Coordination.Tests;

public sealed class TxnStateMachineGoldenTests
{
    private static readonly string[] SchemaParticipantNames = { "NotStarted", "Unknown", "Applied", "Failed" };

    [Fact]
    public void OnlyFrozenTransactionTransitionsAreAccepted()
    {
        Transition[] table = StateTransitionTable.All
            .Where(row => string.Equals(row.Machine, "CrossWorldTxn", StringComparison.Ordinal))
            .ToArray();
        Assert.Contains(table, row => row.From == "Prepared" && row.To == "CommitIntent");
        Assert.Contains(table, row => row.From == "CommitIntent" && row.To == "Committed");
        Assert.Contains(table, row => row.From == "Indeterminate" && row.To == "Committed");
        Assert.DoesNotContain(table, row => row.From == "Prepared" && row.To == "Indeterminate");
        Assert.DoesNotContain(table, row => row.From == "CommitIntent" && row.To == "Aborted");

        TxnRecord happy = Record("txn-happy");
        Assert.True(happy.TryTransition(CrossWorldTxnState.Prepared).Succeeded);
        Assert.Equal(CrossWorldTxnState.Prepared, happy.State);
        TxnTransitionResult unauthenticatedIntent = happy.TryTransition(CrossWorldTxnState.CommitIntent);
        Assert.False(unauthenticatedIntent.Succeeded);
        Assert.Equal("CapabilityMissing", unauthenticatedIntent.Failure?.GeneratedErrorId);
        Assert.Equal(CrossWorldTxnState.Prepared, happy.State);
        TxnAuthorityTestData.MarkIntent(happy);
        Assert.Equal(CrossWorldTxnState.CommitIntent, happy.State);
        TxnTransitionResult unauthenticatedCommit = happy.TryTransition(CrossWorldTxnState.Committed);
        Assert.False(unauthenticatedCommit.Succeeded);
        Assert.Equal("CapabilityMissing", unauthenticatedCommit.Failure?.GeneratedErrorId);
        Assert.Equal(CrossWorldTxnState.CommitIntent, happy.State);
        Assert.True(PublishCommitted(happy).Succeeded);
        Assert.Equal(CrossWorldTxnState.Committed, happy.State);

        TxnRecord aborted = Record("txn-aborted");
        Assert.True(aborted.TryTransition(CrossWorldTxnState.Prepared).Succeeded);
        Assert.True(aborted.Abort("ValidationFailed").Succeeded);
        Assert.Equal(CrossWorldTxnState.Aborted, aborted.State);

        TxnRecord expired = Record("txn-expired");
        Assert.True(expired.TryTransition(CrossWorldTxnState.Prepared).Succeeded);
        Assert.True(expired.Expire().Succeeded);
        Assert.Equal(CrossWorldTxnState.Expired, expired.State);

        TxnRecord indeterminate = Record("txn-indeterminate");
        Assert.True(indeterminate.TryTransition(CrossWorldTxnState.Prepared).Succeeded);
        TxnAuthorityTestData.MarkIntent(indeterminate);
        Assert.True(indeterminate.TryTransition(CrossWorldTxnState.Indeterminate).Succeeded);
        Assert.Equal(CrossWorldTxnState.Indeterminate, indeterminate.State);
        Assert.True(PublishCommitted(indeterminate).Succeeded);
        Assert.Equal(CrossWorldTxnState.Committed, indeterminate.State);

        TxnRecord invalid = Record("txn-invalid");
        Assert.True(invalid.TryTransition(CrossWorldTxnState.Prepared).Succeeded);
        TxnTransitionResult preparedToIndeterminate = invalid.TryTransition(CrossWorldTxnState.Indeterminate);
        Assert.False(preparedToIndeterminate.Succeeded);
        Assert.Equal("InvalidArgument", preparedToIndeterminate.Failure?.GeneratedErrorId);
        Assert.Equal(CrossWorldTxnState.Prepared, invalid.State);

        TxnRecord intentAbort = Record("txn-intent-abort");
        Assert.True(intentAbort.TryTransition(CrossWorldTxnState.Prepared).Succeeded);
        TxnAuthorityTestData.MarkIntent(intentAbort);
        TxnTransitionResult abortAfterIntent = intentAbort.Abort("ValidationFailed");
        Assert.False(abortAfterIntent.Succeeded);
        Assert.Equal("InvalidArgument", abortAfterIntent.Failure?.GeneratedErrorId);
        Assert.Equal(CrossWorldTxnState.CommitIntent, intentAbort.State);

        Assert.False(happy.TryTransition(CrossWorldTxnState.Prepared).Succeeded);
        Assert.Equal("InvalidArgument", happy.TryTransition(CrossWorldTxnState.Created).Failure?.GeneratedErrorId);
        Assert.Equal(CrossWorldTxnState.Committed, happy.State);
        Assert.Equal("InvalidArgument", aborted.TryTransition(CrossWorldTxnState.Prepared).Failure?.GeneratedErrorId);
        Assert.Equal("InvalidArgument", expired.TryTransition(CrossWorldTxnState.Created).Failure?.GeneratedErrorId);
    }

    [Fact]
    public void ParticipantStateIsFourValuedAndNeverBoolean()
    {
        Assert.Equal(SchemaParticipantNames, Enum.GetNames<TxnParticipantState>());
        for (int index = 0; index < SchemaParticipantNames.Length; index++)
            Assert.Equal(index, (int)Enum.Parse<TxnParticipantState>(SchemaParticipantNames[index]));

        foreach (string name in SchemaParticipantNames)
        {
            Assert.True(TxnParticipantStateWire.TryParse(name, out TxnParticipantState state));
            Assert.Equal(name, TxnParticipantStateWire.Value(state));
            Assert.Equal(name, Enum.GetName(state));
        }

        RoundTripArchitectureMarkers(
            """{"voxelCommit":"Applied","ecsCommandBufferCommit":"Applied"}""",
            TxnParticipantState.Applied,
            TxnParticipantState.Applied);
        RoundTripArchitectureMarkers(
            """{"voxelCommit":"NotStarted","ecsCommandBufferCommit":"NotStarted"}""",
            TxnParticipantState.NotStarted,
            TxnParticipantState.NotStarted);
        RoundTripArchitectureMarkers(
            """{"voxelCommit":"Unknown","ecsCommandBufferCommit":"Unknown"}""",
            TxnParticipantState.Unknown,
            TxnParticipantState.Unknown);
        RoundTripArchitectureMarkers(
            """{"voxelCommit":"Failed","ecsCommandBufferCommit":"Failed"}""",
            TxnParticipantState.Failed,
            TxnParticipantState.Failed);
        RoundTripArchitectureMarkers(
            """{"voxelCommit":"Applied","ecsCommandBufferCommit":"NotStarted"}""",
            TxnParticipantState.Applied,
            TxnParticipantState.NotStarted);

        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        var illegal = new List<string>();
        foreach (Type type in typeof(TxnRecord).Assembly.GetTypes())
        {
            foreach (FieldInfo field in type.GetFields(flags))
            {
                if (field.FieldType == typeof(bool) && IsParticipantMarkerName(field.Name))
                    illegal.Add(string.Concat(type.FullName, ".", field.Name));
            }

            foreach (PropertyInfo property in type.GetProperties(flags))
            {
                if (property.PropertyType == typeof(bool) && IsParticipantMarkerName(property.Name))
                    illegal.Add(string.Concat(type.FullName, ".", property.Name));
            }
        }

        Assert.Empty(illegal);
    }

    private static void RoundTripArchitectureMarkers(
        string json,
        TxnParticipantState voxel,
        TxnParticipantState ecs)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement markers = document.RootElement;
        string voxelWire = markers.GetProperty("voxelCommit").GetString()!;
        string ecsWire = markers.GetProperty("ecsCommandBufferCommit").GetString()!;
        Assert.True(TxnParticipantStateWire.TryParse(voxelWire, out TxnParticipantState parsedVoxel));
        Assert.True(TxnParticipantStateWire.TryParse(ecsWire, out TxnParticipantState parsedEcs));
        Assert.Equal(voxel, parsedVoxel);
        Assert.Equal(ecs, parsedEcs);
        Assert.Equal(voxelWire, TxnParticipantStateWire.Value(parsedVoxel));
        Assert.Equal(ecsWire, TxnParticipantStateWire.Value(parsedEcs));
        var record = new TxnParticipantMarkers(parsedVoxel, parsedEcs);
        Assert.Equal(voxelWire, TxnParticipantStateWire.Value(record.VoxelCommit));
        Assert.Equal(ecsWire, TxnParticipantStateWire.Value(record.EcsCommandBufferCommit));
    }

    private static bool IsParticipantMarkerName(string name) =>
        name.Contains("Participant", StringComparison.Ordinal) ||
        name.Contains("voxelCommit", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("ecsCommandBufferCommit", StringComparison.OrdinalIgnoreCase);

    private static TxnTransitionResult PublishCommitted(TxnRecord record)
    {
        SessionRevisionVectorView expected = record.ExpectedRevision;
        var result = new SessionRevisionVectorView(
            record.TickId,
            checked(expected.GameRevision + 1UL),
            checked(expected.VoxelWorldRevision + 1UL),
            expected.ChunkRevisionSet,
            checked(expected.ReplicationRevision + 1UL),
            expected.ConfigRevision,
            expected.SchemaEpoch);
        var store = new SessionRevisionVectorStore(expected);
        SessionCoordinationContext context = SessionCoordinationContext.For(store);
        Assert.True(context.TryEnter(TxnIdentity.From(record), out TxnAuthorityOperation operation, out CoordinationFailure? failure),
            failure?.Detail);
        using (operation)
        {
            var journal = new InMemoryTxnJournalPort();
            TxnIdentity identity = operation.Identity;
            TxnJournalProof intent = Append(journal, identity, TxnJournalStage.CommitIntent);
            TxnJournalProof voxel = Append(
                journal,
                identity,
                TxnJournalStage.VoxelMarker,
                intent.Checksum,
                result.CanonicalDigestHex);
            var evidence = new TxnResultEvidence(record, result);
            TxnJournalProof ecs = Append(
                journal,
                identity,
                TxnJournalStage.EcsMarker,
                intent.Checksum,
                voxel.Checksum,
                result.CanonicalDigestHex,
                evidence.CanonicalDigestHex);
            TxnJournalProof terminal = Append(
                journal,
                identity,
                TxnJournalStage.Committed,
                intent.Checksum,
                voxel.Checksum,
                ecs.Checksum,
                evidence.CanonicalDigestHex,
                result.CanonicalDigestHex);
            return record.PublishCommitted(new TxnCommitCertificate(
                operation, intent, voxel, ecs, terminal, evidence, result));
        }
    }

    private static TxnJournalProof Append(
        InMemoryTxnJournalPort journal,
        TxnIdentity identity,
        TxnJournalStage stage,
        params string[] links)
    {
        TxnJournalTailResult tail = journal.ReadTail();
        Assert.True(tail.IsAvailable);
        TxnJournalRecord row = TxnJournalAuthority.Create(
            identity,
            stage,
            checked(tail.RecordSequence + 1UL),
            tail.Checksum!,
            links);
        TxnJournalAppendResult appended = journal.Append(in row);
        Assert.True(appended.IsDurable);
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

    private static TxnRecord Record(string txnId) =>
        new("session", txnId, 1UL, "command", new SessionRevisionVectorView(1UL, 1UL, 1UL,
            new Dictionary<string, ulong>(), 1UL, 1UL, 1UL), 10UL, string.Concat("digest-", txnId));
}
