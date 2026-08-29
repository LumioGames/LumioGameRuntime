using System;
using Lumio.Gen.ContractTypes;
using Lumio.GameRuntime.Observability;
using Xunit;

namespace Lumio.GameRuntime.Observability.Tests;

public sealed class DurableRouteFailureTests
{
    [Fact]
    public void DurableQueueFullReturnsBackpressureAndNeverBestEffortDrop()
    {
        var router = new DurableEvidenceRouter(1);
        var first = Record("key-1");
        var second = Record("key-2");

        var accepted = router.Enqueue(in first);
        var backpressured = router.Enqueue(in second);
        var retry = router.Enqueue(in first);

        Assert.Equal(DurableEnqueueStatus.Accepted, accepted.Status);
        Assert.Equal(DurableEnqueueStatus.Backpressured, backpressured.Status);
        Assert.Equal("QueueFull", backpressured.GeneratedErrorId);
        Assert.Equal(DurableEnqueueStatus.Accepted, retry.Status);
        Assert.True(retry.AlreadyPresent);
        Assert.Equal(accepted.RecordSequence, retry.RecordSequence);
        Assert.Equal(DurableQueryStatus.Found, router.Query("key-1").Status);
    }

    [Fact]
    public void DurableRouteIsClosedExplicitly()
    {
        var router = new DurableEvidenceRouter(1);
        router.Complete();
        var value = Record("key-1");

        Assert.Equal(DurableEnqueueStatus.Closed, router.Enqueue(in value).Status);
        Assert.Equal(DurableQueryStatus.Closed, router.Query("key-1").Status);
    }

    // ---- T05.S06:Txn / Command / WAL 三个 generated overload ----
    // 记录本体来自架构源 generated 面(ADR-048 八类闭合契约),不是本仓手写的替身类型。

    [Fact]
    public void TxnJournalOverloadKeysOnGeneratedIdempotencyKey()
    {
        var router = new DurableEvidenceRouter(4);
        var record = TxnRecord(recordSeq: 1UL, idempotencyKey: "txn-idem-1");

        var accepted = router.Enqueue(record, Payload, Correlation);
        var replay = router.Enqueue(record, Payload, Correlation);

        Assert.Equal(DurableEnqueueStatus.Accepted, accepted.Status);
        Assert.False(accepted.AlreadyPresent);
        Assert.True(replay.AlreadyPresent);
        Assert.Equal(accepted.RecordSequence, replay.RecordSequence);

        var stored = router.Query("txn-idem-1");
        Assert.Equal(DurableQueryStatus.Found, stored.Status);
        Assert.Equal("txn-journal-record", stored.Record!.Value.RecordType);
    }

    [Fact]
    public void CommandLogOverloadKeysOnGeneratedIdempotencyKey()
    {
        var router = new DurableEvidenceRouter(4);
        var record = CommandRecord(recordSeq: 1UL, idempotencyKey: "cmd-idem-1");

        var accepted = router.Enqueue(record, Payload, Correlation);

        Assert.Equal(DurableEnqueueStatus.Accepted, accepted.Status);
        var stored = router.Query("cmd-idem-1");
        Assert.Equal(DurableQueryStatus.Found, stored.Status);
        Assert.Equal("command-log-record", stored.Record!.Value.RecordType);
    }

    [Fact]
    public void WalEnvelopeOverloadDerivesStableKeyBecauseSchemaDeclaresNone()
    {
        // wal-record-envelope 的 schema 没有 idempotencyKey 字段(Txn/Command 两类有)。
        // 键必须由记录自身的链身份确定性导出:同一记录重投必须命中同一条,不同记录不得互相夺舍。
        var router = new DurableEvidenceRouter(4);
        var record = WalRecord(recordSeq: 7UL, payloadHash: "hash-a");
        var sameAgain = WalRecord(recordSeq: 7UL, payloadHash: "hash-a");
        var different = WalRecord(recordSeq: 8UL, payloadHash: "hash-b");

        var first = router.Enqueue(record, Payload, Correlation);
        var replay = router.Enqueue(sameAgain, Payload, Correlation);
        var other = router.Enqueue(different, Payload, Correlation);

        Assert.Equal(DurableEnqueueStatus.Accepted, first.Status);
        Assert.True(replay.AlreadyPresent);
        Assert.Equal(first.RecordSequence, replay.RecordSequence);
        Assert.False(other.AlreadyPresent);
        Assert.NotEqual(first.RecordSequence, other.RecordSequence);
    }

    [Fact]
    public void GeneratedOverloadsPreserveProducerOrderAcrossRecordKinds()
    {
        var router = new DurableEvidenceRouter(8);

        var txn = router.Enqueue(TxnRecord(1UL, "order-txn"), Payload, Correlation);
        var command = router.Enqueue(CommandRecord(2UL, "order-cmd"), Payload, Correlation);
        var wal = router.Enqueue(WalRecord(3UL, "order-wal"), Payload, Correlation);

        Assert.True(txn.RecordSequence < command.RecordSequence);
        Assert.True(command.RecordSequence < wal.RecordSequence);
    }

    [Fact]
    public void GeneratedOverloadSurfacesBackpressureVerbatimWhenFull()
    {
        var router = new DurableEvidenceRouter(1);
        router.Enqueue(TxnRecord(1UL, "first"), Payload, Correlation);

        var backpressured = router.Enqueue(CommandRecord(2UL, "second"), Payload, Correlation);

        // 满载的可靠 record 只能背压:不得被改写成 Accepted,也不得落库。
        Assert.Equal(DurableEnqueueStatus.Backpressured, backpressured.Status);
        Assert.Equal("QueueFull", backpressured.GeneratedErrorId);
        Assert.False(backpressured.AlreadyPresent);
        Assert.Equal(DurableQueryStatus.NotFound, router.Query("second").Status);
    }

    private static readonly ReadOnlyMemory<byte> Payload = new byte[] { 1, 2, 3 };

    private static readonly CorrelationView Correlation =
        new("Txn", "product", "release", "session", "world", "trace", "producer", 1UL);

    private static TxnJournalRecord TxnRecord(ulong recordSeq, string idempotencyKey) => new(
        1UL, recordSeq, "prev", "payload", 3UL, "checksum",
        TxnJournalRecordCommitState.Committed, TxnJournalRecordDurabilityState.Durable,
        "session", "release", 42UL, "txn-1", null,
        TxnJournalRecordRecordKind.Committed, idempotencyKey);

    private static CommandLogRecord CommandRecord(ulong recordSeq, string idempotencyKey) => new(
        1UL, recordSeq, "prev", "payload", 3UL, "checksum",
        CommandLogRecordCommitState.Committed, CommandLogRecordDurabilityState.Durable,
        "session", "release", 42UL, null, "command-1",
        CommandLogRecordRecordKind.Confirmed, idempotencyKey);

    private static WalRecordEnvelope WalRecord(ulong recordSeq, string payloadHash) => new(
        1UL, recordSeq, "prev", payloadHash, 3UL, "checksum",
        WalRecordEnvelopeInnerKind.TxnJournal, new OpaqueJson("{}"));

    private static DurableRecordView Record(string key) => new(
        key,
        "TxnJournal",
        new byte[] { 1, 2, 3 },
        new CorrelationView("Txn", "product", "release", "session", "world", "trace", "producer", 1UL));
}
