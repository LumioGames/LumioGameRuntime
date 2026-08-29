using System;
using System.Collections.Generic;
using System.Globalization;
using Lumio.Gen.ContractTypes;
using Lumio.Gen.LanguageBinding;

namespace Lumio.GameRuntime.Observability;

internal sealed class DurableEvidenceRouter : IDurableEvidencePort
{
    private readonly object _gate = new();
    private readonly int _capacity;
    private readonly Dictionary<string, DurableRecordView> _records = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ulong> _recordSequences = new(StringComparer.Ordinal);
    private ulong _nextRecordSequence;
    private bool _closed;

    internal DurableEvidenceRouter(int capacity)
    {
#if NET10_0_OR_GREATER
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
#else
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
#endif
        _capacity = capacity;
    }

    public DurableEnqueueResult Enqueue(in DurableRecordView record)
    {
        if (!record.IsWellFormed)
        {
            return new DurableEnqueueResult(DurableEnqueueStatus.Rejected, 0UL, false, "ManifestMalformed");
        }

        lock (_gate)
        {
            if (_closed)
            {
                return new DurableEnqueueResult(DurableEnqueueStatus.Closed, 0UL, false, "ManifestMalformed");
            }

            if (_records.TryGetValue(record.IdempotencyKey, out DurableRecordView existing))
            {
                return new DurableEnqueueResult(
                    DurableEnqueueStatus.Accepted,
                    _recordSequences[existing.IdempotencyKey],
                    true,
                    null);
            }

            if (_records.Count >= _capacity)
            {
                return new DurableEnqueueResult(DurableEnqueueStatus.Backpressured, 0UL, false, "QueueFull");
            }

            var copy = record with { Payload = record.Payload.ToArray() };
            _records.Add(copy.IdempotencyKey, copy);
            _nextRecordSequence = checked(_nextRecordSequence + 1UL);
            _recordSequences.Add(copy.IdempotencyKey, _nextRecordSequence);
            return new DurableEnqueueResult(DurableEnqueueStatus.Accepted, _nextRecordSequence, false, null);
        }
    }

    public DurableQueryResult Query(string idempotencyKey)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            return new DurableQueryResult(DurableQueryStatus.Rejected, null, "ManifestMalformed");
        }

        lock (_gate)
        {
            if (_closed)
            {
                return new DurableQueryResult(DurableQueryStatus.Closed, null, "ManifestMalformed");
            }

            return _records.TryGetValue(idempotencyKey, out DurableRecordView record)
                ? new DurableQueryResult(DurableQueryStatus.Found, record with { Payload = record.Payload.ToArray() }, null)
                : new DurableQueryResult(DurableQueryStatus.NotFound, null, null);
        }
    }

    // ---- T05.S06:Txn / Command / WAL 三个 generated overload ----
    //
    // 三个重载只做「把 generated 记录折成 durable 证据条目」这一件事,随后一律走上面同一个
    // Enqueue(in DurableRecordView):producer order 由该方法在锁内递增的 _nextRecordSequence
    // 保证,Backpressured 原样透出,不在此层改写成 success,也不转投 Diagnostic queue。
    //
    // payload 由调用方传入,不在此合成:架构源的 canonical-serializer artifact 目前只发布
    // 形式声明(CanonicalForm / LumioBinForm 常量与 golden 向量),没有可执行编码器。此处自行
    // 拼字节等于自造 canonical bytes,属卡面「必须升级确认」项,故不做。
    internal DurableEnqueueResult Enqueue(
        TxnJournalRecord record,
        ReadOnlyMemory<byte> payload,
        in CorrelationView correlation)
    {
        if (record is null) return MalformedRecord();
        return EnqueueGenerated(TxnJournalSchemaId, record.IdempotencyKey, payload, in correlation);
    }

    internal DurableEnqueueResult Enqueue(
        CommandLogRecord record,
        ReadOnlyMemory<byte> payload,
        in CorrelationView correlation)
    {
        if (record is null) return MalformedRecord();
        return EnqueueGenerated(CommandLogSchemaId, record.IdempotencyKey, payload, in correlation);
    }

    internal DurableEnqueueResult Enqueue(
        WalRecordEnvelope record,
        ReadOnlyMemory<byte> payload,
        in CorrelationView correlation)
    {
        if (record is null) return MalformedRecord();

        // wal-record-envelope 的 schema 不声明 idempotencyKey(txn/command 两类声明了),
        // 所以键只能由记录自身的链身份确定性导出。recordSeq 定位它在 WAL 链中的位置,
        // payloadHash 区分同一位置上内容不同的记录——两者一起才能既让重投幂等命中,
        // 又不让内容不同的记录互相夺舍。这是本仓 durable port 的内部键,不是公共契约字段。
        string key = string.Concat(
            WalEnvelopeSchemaId,
            ":",
            record.RecordSeq.ToString(CultureInfo.InvariantCulture),
            ":",
            record.PayloadHash);
        return EnqueueGenerated(WalEnvelopeSchemaId, key, payload, in correlation);
    }

    private DurableEnqueueResult EnqueueGenerated(
        string schemaId,
        string idempotencyKey,
        ReadOnlyMemory<byte> payload,
        in CorrelationView correlation)
    {
        var view = new DurableRecordView(idempotencyKey, schemaId, payload, correlation);
        return Enqueue(in view);
    }

    private static DurableEnqueueResult MalformedRecord() =>
        new DurableEnqueueResult(DurableEnqueueStatus.Rejected, 0UL, false, "ManifestMalformed");

    // RecordType 取 generated 绑定表里的 schemaId,不写死字面量:类型名与 schemaId 的对应
    // 关系由架构源发布,改名时这里会 Fail-stop 而不是继续写入一个陈旧的字符串。
    private static readonly string TxnJournalSchemaId = SchemaIdOf(nameof(TxnJournalRecord));
    private static readonly string CommandLogSchemaId = SchemaIdOf(nameof(CommandLogRecord));
    private static readonly string WalEnvelopeSchemaId = SchemaIdOf(nameof(WalRecordEnvelope));

    private static string SchemaIdOf(string generatedTypeName)
    {
        foreach (Binding binding in Bindings.All)
        {
            if (string.Equals(binding.CsharpType, generatedTypeName, StringComparison.Ordinal))
            {
                return binding.SchemaId;
            }
        }

        throw new InvalidOperationException(
            "generated binding table has no schemaId for " + generatedTypeName);
    }

    internal void Complete()
    {
        lock (_gate) _closed = true;
    }

}
