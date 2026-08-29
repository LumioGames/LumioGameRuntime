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
    //
    // **durable 键一律是 `schemaId + ":" + 记录自身的键`。** schemaId 前缀不是装饰:
    // idempotencyKey 的 schema 约束只有 `^[A-Za-z0-9][A-Za-z0-9._:-]{0,127}$`,跨 schema
    // 不保证唯一,而 _records 只按键查找、RecordType 不参与索引。少了前缀,同一次请求派生的
    // Txn 与 Command 复用同一 key 时,第二条会被当作重投永久丢弃并返回 Accepted——
    // 静默丢 durable 记录 + 伪成功。三类必须用同一套加前缀规则,不能只给其中一类加。
    internal DurableEnqueueResult Enqueue(
        TxnJournalRecord record,
        ReadOnlyMemory<byte> payload,
        in CorrelationView correlation)
    {
        if (record is null) return MalformedRecord();
        return EnqueueGenerated(
            SchemaIdOf(nameof(TxnJournalRecord)), record.IdempotencyKey, payload, in correlation);
    }

    internal DurableEnqueueResult Enqueue(
        CommandLogRecord record,
        ReadOnlyMemory<byte> payload,
        in CorrelationView correlation)
    {
        if (record is null) return MalformedRecord();
        return EnqueueGenerated(
            SchemaIdOf(nameof(CommandLogRecord)), record.IdempotencyKey, payload, in correlation);
    }

    internal DurableEnqueueResult Enqueue(
        WalRecordEnvelope record,
        ReadOnlyMemory<byte> payload,
        in CorrelationView correlation)
    {
        if (record is null) return MalformedRecord();

        // wal-record-envelope 的 schema 不声明 idempotencyKey(txn/command 两类声明了),
        // 所以记录自身的键只能由链身份导出:recordSeq 定位它在 WAL 链中的位置,payloadHash
        // 区分同一位置上内容不同的记录——两者一起才能既让重投幂等命中,又不让内容不同的
        // 记录互相夺舍。payloadHash 缺失时键会退化成 "<seq>:",夺舍风险回归,故在下面
        // 与另外两类一样按空值拒绝。这是本仓 durable port 的内部键,不是公共契约字段。
        if (string.IsNullOrWhiteSpace(record.PayloadHash)) return MalformedRecord();
        string chainIdentity = string.Concat(
            record.RecordSeq.ToString(CultureInfo.InvariantCulture), ":", record.PayloadHash);
        return EnqueueGenerated(
            SchemaIdOf(nameof(WalRecordEnvelope)), chainIdentity, payload, in correlation);
    }

    private DurableEnqueueResult EnqueueGenerated(
        string schemaId,
        string recordKey,
        ReadOnlyMemory<byte> payload,
        in CorrelationView correlation)
    {
        // 必须在加前缀**之前**判空:加完前缀键就永远非空,DurableRecordView.IsWellFormed
        // 的空值守卫会被前缀架空,空键记录会一路走到落库。
        if (string.IsNullOrWhiteSpace(recordKey)) return MalformedRecord();

        var view = new DurableRecordView(
            string.Concat(schemaId, ":", recordKey), schemaId, payload, correlation);
        return Enqueue(in view);
    }

    private static DurableEnqueueResult MalformedRecord() =>
        new DurableEnqueueResult(DurableEnqueueStatus.Rejected, 0UL, false, "ManifestMalformed");

    // RecordType 取 generated 绑定表里的 schemaId,不写死字面量:类型名与 schemaId 的对应
    // 关系由架构源发布,改名时这里会 Fail-stop 而不是继续写入一个陈旧的字符串。
    //
    // 惰性解析而非 static 字段初始化器:后者会把查表失败升级成 TypeInitializationException,
    // 连不依赖 generated 绑定的 Enqueue(in DurableRecordView) 既有路径一并炸掉。Fail-stop
    // 应当只覆盖真正踩到缺失绑定的那条调用路径。
    private static readonly Dictionary<string, string> SchemaIdCache = new(StringComparer.Ordinal);

    private static string SchemaIdOf(string generatedTypeName)
    {
        lock (SchemaIdCache)
        {
            if (SchemaIdCache.TryGetValue(generatedTypeName, out string? cached)) return cached;

            foreach (Binding binding in Bindings.All)
            {
                if (string.Equals(binding.CsharpType, generatedTypeName, StringComparison.Ordinal))
                {
                    SchemaIdCache.Add(generatedTypeName, binding.SchemaId);
                    return binding.SchemaId;
                }
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
