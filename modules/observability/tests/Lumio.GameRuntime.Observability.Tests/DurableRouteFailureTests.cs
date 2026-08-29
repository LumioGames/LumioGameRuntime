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

        var stored = router.Query("txn-journal-record:txn-idem-1");
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
        var stored = router.Query("command-log-record:cmd-idem-1");
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
    public void SameIdempotencyKeyFromDifferentSchemasDoesNotCollide()
    {
        // idempotencyKey 的 schema 约束只有 `^[A-Za-z0-9][A-Za-z0-9._:-]{0,127}$`,
        // 跨 schema **不保证唯一**。同一次请求派生出一条 Txn 和一条 Command 并复用同一 key
        // 是最自然的写法;若两者共用一个无命名空间的 keyspace,第二条会被当成重投而永久丢失,
        // 且返回 Accepted——静默丢 durable 记录 + 伪成功。
        var router = new DurableEvidenceRouter(4);

        var txn = router.Enqueue(TxnRecord(1UL, "shared-key"), Payload, Correlation);
        var command = router.Enqueue(CommandRecord(2UL, "shared-key"), Payload, Correlation);

        Assert.False(txn.AlreadyPresent);
        Assert.False(command.AlreadyPresent);
        Assert.NotEqual(txn.RecordSequence, command.RecordSequence);

        Assert.Equal("txn-journal-record", router.Query("txn-journal-record:shared-key").Record!.Value.RecordType);
        Assert.Equal("command-log-record", router.Query("command-log-record:shared-key").Record!.Value.RecordType);
    }

    [Fact]
    public void GeneratedRecordWithBlankKeyFieldIsRejectedNotSilentlyAccepted()
    {
        // 键一旦带上 schemaId 前缀就永远非空,DurableRecordView.IsWellFormed 的空值守卫
        // 会被前缀架空。因此必须在**加前缀之前**校验记录自身的键字段。
        var router = new DurableEvidenceRouter(4);

        var blankTxn = router.Enqueue(TxnRecord(1UL, "   "), Payload, Correlation);
        var blankCommand = router.Enqueue(CommandRecord(1UL, ""), Payload, Correlation);
        // WAL 无 idempotencyKey,其键由 payloadHash 参与导出;payloadHash 缺失会让键退化成
        // "wal-record-envelope:7:",于是 recordSeq 相同的两条记录互相夺舍。
        var blankWal = router.Enqueue(WalRecord(7UL, null!), Payload, Correlation);

        Assert.Equal(DurableEnqueueStatus.Rejected, blankTxn.Status);
        Assert.Equal(DurableEnqueueStatus.Rejected, blankCommand.Status);
        Assert.Equal(DurableEnqueueStatus.Rejected, blankWal.Status);
        Assert.Equal("ManifestMalformed", blankWal.GeneratedErrorId);
    }

    [Fact]
    public void CrossRecordTypeKeyCollisionIsRejectedInsteadOfSilentlyDeduped()
    {
        // 公共 Enqueue(in DurableRecordView) 用调用方给的**裸键**索引,generated 重载则带
        // schemaId 前缀,两者写同一个 _records。于是一条 EventId 恰为 "txn-journal-record:abc"
        // 的事件会撞上已存的 txn 记录。此前的行为是当作重投返回 Accepted——静默丢 durable 记录。
        //
        // 键本身不能改(那会改变公共可观测键,属须升级确认),但「同键不同 RecordType」在
        // 证据库里是不可能成立的状态,只能 Fail-stop 报出来,不能假装是重投。
        var router = new DurableEvidenceRouter(4);
        router.Enqueue(TxnRecord(1UL, "abc"), Payload, Correlation);

        var collide = new DurableRecordView(
            "txn-journal-record:abc", "logging-event", new byte[] { 9 }, Correlation);
        var result = router.Enqueue(in collide);

        Assert.Equal(DurableEnqueueStatus.Rejected, result.Status);
        Assert.Equal("InternalInvariant", result.GeneratedErrorId);
        Assert.False(result.AlreadyPresent);

        // 原记录不得被覆盖或改写。
        Assert.Equal("txn-journal-record", router.Query("txn-journal-record:abc").Record!.Value.RecordType);
    }

    [Fact]
    public void SameRecordTypeReplayStillDedupesNormally()
    {
        // 上一条的守护不得误伤正常的同类型重投。
        var router = new DurableEvidenceRouter(4);
        var first = Record("replay-key");
        var again = Record("replay-key");

        var a = router.Enqueue(in first);
        var b = router.Enqueue(in again);

        Assert.Equal(DurableEnqueueStatus.Accepted, a.Status);
        Assert.Equal(DurableEnqueueStatus.Accepted, b.Status);
        Assert.True(b.AlreadyPresent);
        Assert.Equal(a.RecordSequence, b.RecordSequence);
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
        Assert.Equal(DurableQueryStatus.NotFound, router.Query("command-log-record:second").Status);
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
