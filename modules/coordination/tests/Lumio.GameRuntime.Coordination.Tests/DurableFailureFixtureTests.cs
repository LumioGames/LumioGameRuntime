using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Lumio.GameRuntime.Command;
using Lumio.Gen.ContractTypes;
using Xunit;

namespace Lumio.GameRuntime.Coordination.Tests;

public sealed class DurableFailureFixtureTests
{
    [Fact]
    public void DurableFailureFixturesParseAndReplayThroughRuntime()
    {
        string root = FindFixtureRoot();
        string[] files = Directory.GetFiles(root, "*.json", SearchOption.AllDirectories);

        Assert.Equal(10, files.Length);
        var outcomes = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        foreach (string file in files.OrderBy(path => path, StringComparer.Ordinal))
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(file));
            JsonElement fixture = document.RootElement;
            Assert.Equal(2, fixture.GetProperty("fixtureVersion").GetInt32());
            string fixtureId = RequiredString(fixture, "fixtureId");
            string kind = RequiredString(fixture, "case");
            FixtureIdentity identity = ReadIdentity(fixture);
            ReplayOutcome actual = Replay(kind, fixture, identity);

            Assert.NotEqual(string.Empty, fixtureId);
            Assert.Equal(RequiredString(fixture, "expectedStatus"), actual.Status);
            string? expectedError = OptionalString(fixture, "expectedErrorId");
            if (expectedError is not null) Assert.Equal(expectedError, actual.ErrorId);
            string? forbidden = OptionalString(fixture, "forbiddenStatus");
            if (forbidden is not null) Assert.NotEqual(forbidden, actual.Status);

            string polarity = file.Contains(
                Path.DirectorySeparatorChar + "valid" + Path.DirectorySeparatorChar,
                StringComparison.Ordinal)
                ? "valid"
                : "invalid";
            if (!outcomes.TryGetValue(kind, out Dictionary<string, string>? pair))
            {
                pair = new Dictionary<string, string>(StringComparer.Ordinal);
                outcomes.Add(kind, pair);
            }
            pair[polarity] = actual.Status;
        }

        Assert.Equal(5, outcomes.Count);
        foreach (Dictionary<string, string> pair in outcomes.Values)
        {
            Assert.True(pair.ContainsKey("valid") && pair.ContainsKey("invalid"));
            Assert.NotEqual(pair["valid"], pair["invalid"]);
        }
    }

    private static ReplayOutcome Replay(
        string kind,
        JsonElement fixture,
        FixtureIdentity identity) => kind switch
    {
        "duplicate" => ReplayDuplicate(fixture, identity),
        "timeout" => ReplayTimeout(fixture, identity),
        "lost-result" or "partial-commit" or "crash-boundary" => ReplayRecovery(fixture, identity),
        _ => throw new InvalidDataException("Unknown fixture case.")
    };

    private static ReplayOutcome ReplayDuplicate(JsonElement fixture, FixtureIdentity identity)
    {
        string firstDigest = RequiredString(fixture, "firstDigest");
        string replayDigest = RequiredString(fixture, "replayDigest");
        var index = new TxnIdempotencyIndex(4);
        TxnRecord first = Record(identity, firstDigest);
        TxnRecord replay = Record(identity, replayDigest);
        Assert.Equal(TxnLookupStatus.New, index.Register(first).Status);
        TxnLookupResult result = index.Register(replay);

        var replayIdentity = new TxnIdentity(
            identity.SessionId,
            identity.GameReleaseId,
            identity.TxnId,
            identity.CommandId,
            identity.TickId,
            replayDigest,
            identity.ExpectedRevision.CanonicalDigestHex);
        Assert.Equal(RequiredString(fixture, "replayIdentityDigest"), replayIdentity.DigestHex);
        return result.Status == TxnLookupStatus.Duplicate
            ? new ReplayOutcome("Duplicate", null)
            : result.Status == TxnLookupStatus.Conflict
                ? new ReplayOutcome("Conflict", result.Failure?.GeneratedErrorId)
                : new ReplayOutcome(result.Status.ToString(), result.Failure?.GeneratedErrorId);
    }

    private static ReplayOutcome ReplayTimeout(JsonElement fixture, FixtureIdentity identity)
    {
        ulong deadline = fixture.GetProperty("deadlineTick").GetUInt64();
        var coordinator = new TxnPrepareCoordinator(
            new SessionRevisionVectorStore(identity.ExpectedRevision),
            new CrossWorldCoordinator(),
            new FixtureGamePort(),
            new FixtureVoxelPort(identity.ExpectedRevision));
        TxnPrepareRequest request = new(
            identity.SessionId,
            identity.TxnId,
            identity.TickId,
            identity.CommandId,
            identity.ExpectedRevision.GameRevision,
            identity.ExpectedRevision.VoxelWorldRevision,
            identity.ExpectedRevision.ChunkRevisionSet,
            deadline,
            (int)identity.ExpectedRevision.SchemaEpoch,
            PrepareNoSideEffectTests.Prepared(identity.TickId),
            identity.RequestDigest);
        TxnPrepareResult result = coordinator.Prepare(request);
        return new ReplayOutcome(
            result.Status == TxnPrepareStatus.Rejected &&
            result.Failure?.GeneratedErrorId == "TimedOut"
                ? "Rejected"
                : result.Status.ToString(),
            result.Failure?.GeneratedErrorId);
    }

    private static ReplayOutcome ReplayRecovery(JsonElement fixture, FixtureIdentity identity)
    {
        TxnRecord record = PreparedRecord(identity);
        ApplyRecordState(record, RequiredString(fixture, "recordState"));
        EvidenceFixture evidenceFixture = ReadEvidence(fixture.GetProperty("resultEvidence"), identity);
        InMemoryTxnJournalPort journal = ReadJournal(
            fixture.GetProperty("journalRecords"),
            identity,
            evidenceFixture);
        ITxnResultEvidencePort evidence = evidenceFixture.HasAuthorityData
            ? new FixtureEvidencePort(evidenceFixture)
            : new InMemoryTxnResultEvidencePort();
        var revisions = new SessionRevisionVectorStore(identity.ExpectedRevision);
        TxnRecoveryResult result = new TxnRecoveryResolver(journal, revisions, evidence)
            .Recover(record, ReadQueries(fixture));
        return new ReplayOutcome(result.Status.ToString(), result.Failure?.GeneratedErrorId);
    }

    private static FixtureIdentity ReadIdentity(JsonElement fixture)
    {
        string sessionId = RequiredString(fixture, "sessionId");
        string gameReleaseId = RequiredString(fixture, "gameReleaseId");
        string txnId = RequiredString(fixture, "txnId");
        string commandId = RequiredString(fixture, "commandId");
        ulong tickId = fixture.GetProperty("tickId").GetUInt64();
        string requestDigest = fixture.TryGetProperty("requestDigest", out JsonElement request)
            ? request.GetString() ?? throw new InvalidDataException("requestDigest is required.")
            : RequiredString(fixture, "firstDigest");
        SessionRevisionVectorView expected = ReadVector(fixture.GetProperty("expectedRevision"));
        Assert.Equal(RequiredString(fixture, "expectedRevisionDigest"), expected.CanonicalDigestHex);
        var identity = new TxnIdentity(
            sessionId,
            gameReleaseId,
            txnId,
            commandId,
            tickId,
            requestDigest,
            expected.CanonicalDigestHex);
        Assert.Equal(RequiredString(fixture, "identityDigest"), identity.DigestHex);
        Assert.Equal(RequiredString(fixture, "identityCanonicalHex"), Hex(identity.CanonicalBytes.Span));
        return new FixtureIdentity(
            sessionId,
            gameReleaseId,
            txnId,
            commandId,
            tickId,
            requestDigest,
            expected,
            identity);
    }

    private static InMemoryTxnJournalPort ReadJournal(
        JsonElement records,
        FixtureIdentity identity,
        EvidenceFixture evidence)
    {
        var journal = new InMemoryTxnJournalPort(Math.Max(8, records.GetArrayLength() + 4));
        var loaded = new List<LoadedJournalStage>();
        foreach (JsonElement source in records.EnumerateArray())
        {
            TxnJournalStage stage = Enum.Parse<TxnJournalStage>(RequiredString(source, "stage"), false);
            string[] links = source.GetProperty("links")
                .EnumerateArray()
                .Select(item => item.GetString() ?? throw new InvalidDataException("Journal links must be strings."))
                .ToArray();
            byte[] payload = TxnJournalAuthority.Payload(identity.Identity, stage, links);
            Assert.Equal(RequiredString(source, "payloadHex"), Hex(payload));
            var row = new TxnJournalRecord(
                source.GetProperty("recordVersion").GetUInt64(),
                source.GetProperty("recordSeq").GetUInt64(),
                RequiredString(source, "previousHash"),
                RequiredString(source, "payloadHash"),
                source.GetProperty("length").GetUInt64(),
                RequiredString(source, "checksum"),
                Enum.Parse<TxnJournalRecordCommitState>(RequiredString(source, "commitState"), false),
                Enum.Parse<TxnJournalRecordDurabilityState>(RequiredString(source, "durabilityState"), false),
                RequiredString(source, "sessionId"),
                RequiredString(source, "gameReleaseId"),
                source.GetProperty("tickId").GetUInt64(),
                RequiredString(source, "txnId"),
                RequiredString(source, "commandId"),
                Enum.Parse<TxnJournalRecordRecordKind>(RequiredString(source, "recordKind"), false),
                RequiredString(source, "idempotencyKey"));
            Assert.True(TxnJournalAuthority.TryValidate(
                row,
                identity.Identity,
                stage,
                links,
                out TxnJournalProof? proof,
                out CoordinationFailure? failure),
                failure?.Detail);
            TxnJournalAppendResult appended = journal.Append(in row);
            Assert.True(appended.IsDurable);
            Assert.Equal(row.RecordSeq, appended.RecordSequence);
            Assert.Equal(row.Checksum, appended.RecordChecksum);
            Assert.Equal(row.PreviousHash, appended.PreviousHash);
            loaded.Add(new LoadedJournalStage(stage, links, proof!));
        }

        ValidateJournalLinks(loaded, evidence);
        return journal;
    }

    private static void ValidateJournalLinks(
        IReadOnlyList<LoadedJournalStage> stages,
        EvidenceFixture evidence)
    {
        Assert.NotEmpty(stages);
        Assert.Equal(TxnJournalStage.CommitIntent, stages[0].Stage);
        Assert.Empty(stages[0].Links);
        if (stages.Count == 1) return;

        Assert.Equal(4, stages.Count);
        Assert.NotNull(evidence.JournalEvidenceDigest);
        Assert.NotNull(evidence.JournalResultRevisionDigest);
        LoadedJournalStage intent = stages[0];
        LoadedJournalStage voxel = stages[1];
        LoadedJournalStage ecs = stages[2];
        LoadedJournalStage terminal = stages[3];
        Assert.Equal(TxnJournalStage.VoxelMarker, voxel.Stage);
        Assert.Equal(TxnJournalStage.EcsMarker, ecs.Stage);
        Assert.Equal(TxnJournalStage.Committed, terminal.Stage);
        Assert.Equal(
            new[] { intent.Proof.Checksum, evidence.JournalResultRevisionDigest! },
            voxel.Links);
        Assert.Equal(
            new[]
            {
                intent.Proof.Checksum,
                voxel.Proof.Checksum,
                evidence.JournalResultRevisionDigest!,
                evidence.JournalEvidenceDigest!
            },
            ecs.Links);
        Assert.Equal(
            new[]
            {
                intent.Proof.Checksum,
                voxel.Proof.Checksum,
                ecs.Proof.Checksum,
                evidence.JournalEvidenceDigest!,
                evidence.JournalResultRevisionDigest!
            },
            terminal.Links);
    }

    private static EvidenceFixture ReadEvidence(
        JsonElement source,
        FixtureIdentity identity)
    {
        bool present = source.GetProperty("present").GetBoolean();
        string? sessionId = OptionalString(source, "sessionId");
        string? storedDigest = OptionalString(source, "storedDigest");
        string? journalEvidenceDigest = OptionalString(source, "journalEvidenceDigest");
        string? journalResultRevisionDigest = OptionalString(source, "journalResultRevisionDigest");
        if (sessionId is null)
        {
            Assert.False(present);
            Assert.Null(storedDigest);
            return new EvidenceFixture(false, null, null, journalEvidenceDigest, journalResultRevisionDigest);
        }

        SessionRevisionVectorView expected = ReadVector(source.GetProperty("expectedRevision"));
        SessionRevisionVectorView result = ReadVector(source.GetProperty("resultRevision"));
        var evidence = new TxnResultEvidence(
            sessionId,
            RequiredString(source, "txnId"),
            RequiredString(source, "commandId"),
            source.GetProperty("tickId").GetUInt64(),
            RequiredString(source, "requestDigest"),
            expected,
            result,
            RequiredString(source, "gameReleaseId"));
        Assert.Equal(RequiredString(source, "expectedRevisionDigest"), expected.CanonicalDigestHex);
        Assert.Equal(RequiredString(source, "resultRevisionDigest"), result.CanonicalDigestHex);
        Assert.Equal(RequiredString(source, "canonicalDigest"), evidence.CanonicalDigestHex);
        Assert.Equal(identity.ExpectedRevision, expected);
        Assert.True(evidence.Matches(PreparedRecord(identity)));
        return new EvidenceFixture(
            present,
            evidence,
            storedDigest,
            journalEvidenceDigest,
            journalResultRevisionDigest);
    }

    private static FixtureQueryPort ReadQueries(JsonElement fixture) =>
        new FixtureQueryPort(
            fixture.GetProperty("voxelAvailable").GetBoolean(),
            fixture.GetProperty("ecsAvailable").GetBoolean(),
            Enum.Parse<TxnParticipantState>(RequiredString(fixture, "voxelState"), false),
            Enum.Parse<TxnParticipantState>(RequiredString(fixture, "ecsState"), false),
            ReadOptionalVector(fixture.GetProperty("voxelRevision")),
            ReadOptionalVector(fixture.GetProperty("ecsRevision")));

    private static SessionRevisionVectorView? ReadOptionalVector(JsonElement source) =>
        source.ValueKind == JsonValueKind.Null ? null : ReadVector(source);

    private static SessionRevisionVectorView ReadVector(JsonElement source)
    {
        var chunks = new Dictionary<string, ulong>(StringComparer.Ordinal);
        foreach (JsonProperty chunk in source.GetProperty("chunkRevisionSet").EnumerateObject())
            chunks.Add(chunk.Name, chunk.Value.GetUInt64());
        var vector = new SessionRevisionVectorView(
            source.GetProperty("tickId").GetUInt64(),
            source.GetProperty("gameRevision").GetUInt64(),
            source.GetProperty("voxelWorldRevision").GetUInt64(),
            chunks,
            source.GetProperty("replicationRevision").GetUInt64(),
            source.GetProperty("configRevision").GetUInt64(),
            source.GetProperty("schemaEpoch").GetUInt64());
        Assert.Equal(RequiredString(source, "canonicalDigest"), vector.CanonicalDigestHex);
        return vector;
    }

    private static TxnRecord PreparedRecord(FixtureIdentity identity)
    {
        var record = new TxnRecord(
            identity.SessionId,
            identity.TxnId,
            identity.TickId,
            identity.CommandId,
            identity.ExpectedRevision,
            100UL,
            identity.RequestDigest,
            gameReleaseId: identity.GameReleaseId);
        record.AttachPreparedDelta(PrepareNoSideEffectTests.Prepared(identity.TickId), "fixture-token");
        Assert.True(record.TryTransition(CrossWorldTxnState.Prepared).Succeeded);
        return record;
    }

    private static TxnRecord Record(FixtureIdentity identity, string requestDigest) =>
        new(
            identity.SessionId,
            identity.TxnId,
            identity.TickId,
            identity.CommandId,
            identity.ExpectedRevision,
            100UL,
            requestDigest,
            gameReleaseId: identity.GameReleaseId);

    private static void ApplyRecordState(TxnRecord record, string state)
    {
        if (state == "Prepared") return;
        Assert.Equal("CommitIntent", state);
        TxnAuthorityTestData.MarkIntent(record);
    }

    private static string RequiredString(JsonElement source, string property) =>
        source.GetProperty(property).GetString() ??
        throw new InvalidDataException(string.Concat(property, " is required."));

    private static string? OptionalString(JsonElement source, string property)
    {
        if (!source.TryGetProperty(property, out JsonElement value) ||
            value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return null;
        return value.GetString();
    }

    private static string Hex(ReadOnlySpan<byte> bytes)
    {
        var builder = new StringBuilder(bytes.Length * 2);
        foreach (byte value in bytes)
            builder.Append(value.ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
        return builder.ToString();
    }

    private static string FindFixtureRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string candidate = Path.Combine(directory.FullName, "fixtures");
            if (Directory.Exists(Path.Combine(candidate, "valid")) &&
                Directory.Exists(Path.Combine(candidate, "invalid"))) return candidate;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Coordination fixture root was not found.");
    }

    private readonly record struct ReplayOutcome(string Status, string? ErrorId);

    private sealed record FixtureIdentity(
        string SessionId,
        string GameReleaseId,
        string TxnId,
        string CommandId,
        ulong TickId,
        string RequestDigest,
        SessionRevisionVectorView ExpectedRevision,
        TxnIdentity Identity);

    private sealed record EvidenceFixture(
        bool Present,
        TxnResultEvidence? Evidence,
        string? StoredDigest,
        string? JournalEvidenceDigest,
        string? JournalResultRevisionDigest)
    {
        internal bool HasAuthorityData =>
            Evidence is not null || JournalEvidenceDigest is not null || JournalResultRevisionDigest is not null;
    }

    private sealed record LoadedJournalStage(
        TxnJournalStage Stage,
        string[] Links,
        TxnJournalProof Proof);

    private sealed class FixtureEvidencePort : IIdentityAwareTxnResultEvidencePort
    {
        private readonly EvidenceFixture _fixture;

        internal FixtureEvidencePort(EvidenceFixture fixture) => _fixture = fixture;

        public TxnResultEvidenceWriteResult Write(in TxnResultEvidence evidence) =>
            new(TxnResultEvidenceWriteStatus.Rejected, "EvidenceMissing");

        public TxnResultEvidenceReadResult Read(string sessionId, string txnId)
        {
            if (!_fixture.Present || _fixture.Evidence is null)
                return new TxnResultEvidenceReadResult(TxnResultEvidenceReadStatus.NotFound, null, "EvidenceMissing");
            if (!string.Equals(_fixture.Evidence.SessionId, sessionId, StringComparison.Ordinal) ||
                !string.Equals(_fixture.Evidence.TxnId, txnId, StringComparison.Ordinal))
                return new TxnResultEvidenceReadResult(TxnResultEvidenceReadStatus.NotFound, null, "EvidenceMissing");
            return ReadCore();
        }

        public TxnResultEvidenceReadResult Read(in TxnResultEvidenceIdentity identity)
        {
            if (!_fixture.Present || _fixture.Evidence is null)
                return new TxnResultEvidenceReadResult(TxnResultEvidenceReadStatus.NotFound, null, "EvidenceMissing");
            if (!_fixture.Evidence.Identity.Equals(identity))
                return new TxnResultEvidenceReadResult(TxnResultEvidenceReadStatus.Fatal, null, "EvidenceDigestMismatch");
            return ReadCore();
        }

        private TxnResultEvidenceReadResult ReadCore()
        {
            if (!string.Equals(
                    _fixture.StoredDigest,
                    _fixture.Evidence!.CanonicalDigestHex,
                    StringComparison.Ordinal))
            {
                return new TxnResultEvidenceReadResult(
                    TxnResultEvidenceReadStatus.Fatal,
                    null,
                    "EvidenceDigestMismatch");
            }
            return new TxnResultEvidenceReadResult(
                TxnResultEvidenceReadStatus.Found,
                _fixture.Evidence,
                null);
        }
    }

    private sealed class FixtureGamePort : IGameReservationPort
    {
        public GameReservationResult Reserve(in GameReservationRequest request) =>
            new(GameReservationStatus.Reserved, new ReservationLease(request.TxnId), null);
    }

    private sealed class FixtureVoxelPort : IVoxelWorldPort
    {
        private readonly SessionRevisionVectorView _revision;

        internal FixtureVoxelPort(SessionRevisionVectorView revision) => _revision = revision;

        public VoxelPrepareResult Prepare(in VoxelPrepareRequest request) =>
            VoxelPrepareResult.Prepared("fixture-token", request.DeadlineTick);

        public VoxelCommitParticipantResult Commit(in VoxelCommitParticipantRequest request) =>
            VoxelCommitParticipantResult.Applied(_revision);

        public VoxelAbortParticipantResult Abort(in VoxelAbortParticipantRequest request) => new(true, null);

        public VoxelParticipantQueryResult Query(string sessionId, string txnId) =>
            VoxelParticipantQueryResult.Unavailable();

        public SessionRevisionVectorView ReadRevision() => _revision;
    }

    private sealed class FixtureQueryPort : ITxnParticipantQueryPort
    {
        private readonly bool _voxelAvailable;
        private readonly bool _ecsAvailable;
        private readonly TxnParticipantState _voxelState;
        private readonly TxnParticipantState _ecsState;
        private readonly SessionRevisionVectorView? _voxelRevision;
        private readonly SessionRevisionVectorView? _ecsRevision;

        internal FixtureQueryPort(
            bool voxelAvailable,
            bool ecsAvailable,
            TxnParticipantState voxelState,
            TxnParticipantState ecsState,
            SessionRevisionVectorView? voxelRevision,
            SessionRevisionVectorView? ecsRevision)
        {
            _voxelAvailable = voxelAvailable;
            _ecsAvailable = ecsAvailable;
            _voxelState = voxelState;
            _ecsState = ecsState;
            _voxelRevision = voxelRevision;
            _ecsRevision = ecsRevision;
        }

        public TxnParticipantQueryResult Query(
            string sessionId,
            string txnId,
            TxnParticipantKind participant)
        {
            bool available = participant == TxnParticipantKind.VoxelCommit
                ? _voxelAvailable
                : _ecsAvailable;
            if (!available) return TxnParticipantQueryResult.Unknown();
            return participant == TxnParticipantKind.VoxelCommit
                ? new TxnParticipantQueryResult(_voxelState, true, null, _voxelRevision)
                : new TxnParticipantQueryResult(_ecsState, true, null, _ecsRevision);
        }
    }
}
