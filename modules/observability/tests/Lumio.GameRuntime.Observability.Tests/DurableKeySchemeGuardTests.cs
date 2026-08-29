using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Lumio.Gen.ContractTypes;
using Lumio.Gen.LanguageBinding;
using Lumio.GameRuntime.Observability;
using Xunit;

namespace Lumio.GameRuntime.Observability.Tests;

/// <summary>
/// durable 键是 <c>schemaId + ":" + 记录自身的键</c>(WAL 的记录键又是
/// <c>recordSeq + ":" + payloadHash</c>)。这种拼接式编码的单射性**不是编码给的,是数据借的**,
/// 而借来的东西没有守护——守护是为编码写的,不是为借条写的。本组测试就是那张借条的守护。
///
/// <para>单射性的**充分条件只有一条**:每个出现在分隔符**之前**的字段,自身不得含分隔符。
/// 落到本方案是两处:</para>
/// <list type="number">
/// <item>schemaId 不含分隔符 —— 于是「取第一个分隔符之前」唯一还原 schemaId;</item>
/// <item><c>recordSeq</c> 的渲染不含分隔符(<c>ulong</c> 全数字)—— 于是 WAL 记录键里
///       第二个分隔符的位置也唯一确定。</item>
/// </list>
///
/// <para>分隔符**之后**的字段(idempotencyKey、payloadHash)含冒号完全无害,不构成前提——
/// schema 允许 idempotencyKey 含冒号(<c>^[A-Za-z0-9][A-Za-z0-9._:-]{0,127}$</c>),这没问题。</para>
///
/// <para><b>一处更正</b>:本仓早先向 TD 报过「三条前提」,把「没有 schemaId 是另一个的前缀」
/// 也算作必要条件。那是过度声称——给定前提 1,前缀关系与单射性无关(带分隔符的方案按第一个
/// 分隔符切分即可,不做最长前缀匹配)。「互不为前缀」只对**无分隔符的纯拼接**才是必要条件。
/// 保留这段是因为本仓那条 lesson 讲的正是「闭合到哪一步就只声称到哪一步」。</para>
///
/// 前提被破坏时的症状,与 <c>DurableRouteFailureTests</c> 里那条跨 schema 夺舍完全一样:
/// 记录被当成重投永久丢失,却返回 <c>Accepted</c>。上游改 schemaId 时本组必须先红。
/// </summary>
public sealed class DurableKeySchemeGuardTests
{
    private const string Separator = DurableEvidenceRouter.KeySeparator;

    private static readonly string[] PrefixedSchemaIds =
    {
        SchemaIdOf(nameof(TxnJournalRecord)),
        SchemaIdOf(nameof(CommandLogRecord)),
        SchemaIdOf(nameof(WalRecordEnvelope)),
    };

    // 合成输入提为字段而非内联数组实参(CA1861)。
    private static readonly string[] SetWithSeparatorInId = { "txn-journal-record", "bad" + Separator + "id" };
    private static readonly string[] WellFormedSet = { "alpha", "beta", "gamma" };
    private static readonly ulong[] RecordSeqSamples = { 0UL, 1UL, 1234567890UL, ulong.MaxValue };

    // 对抗性记录键:全部**故意**含分隔符,用来证明分隔符之后的字段含冒号确实无害。
    private static readonly string[] AdversarialRecordKeys =
    {
        "abc",
        "a" + Separator + "b",
        Separator + "leading",
        "trailing" + Separator,
        "1" + Separator + "2" + Separator + "3",
        "txn-journal-record" + Separator + "abc",
    };

    // ---- 真实数据上的守护 ----

    [Fact]
    public void PrefixedSchemaIdsContainNoSeparator()
    {
        Assert.Null(FirstViolation(PrefixedSchemaIds));
    }

    [Fact]
    public void NoPublishedSchemaIdContainsTheSeparator()
    {
        // 只比三个不够:上游任何一个已发布 id 含分隔符,都意味着这套方案随时会被下一个
        // 用它做前缀的调用点带塌。拿全部已发布 schemaId 一起守。
        Assert.Null(FirstViolation(PrefixedSchemaIds.Concat(Catalog.SchemaIds).Distinct().ToArray()));
    }

    [Fact]
    public void RecordSeqRendersWithoutTheSeparator()
    {
        // WAL 记录键是 recordSeq:payloadHash。若 recordSeq 的渲染里能出现分隔符(上游把它从
        // ulong 换成字符串,或引入分组符),第二个分隔符的位置就不再唯一确定。
        Assert.Equal(
            typeof(ulong),
            typeof(WalRecordEnvelope).GetProperty(nameof(WalRecordEnvelope.RecordSeq))!.PropertyType);

        foreach (ulong value in RecordSeqSamples)
        {
            string rendered = value.ToString(CultureInfo.InvariantCulture);
            Assert.False(
                rendered.Contains(Separator, StringComparison.Ordinal),
                $"前提 2 已塌:recordSeq {value} 渲染为 \"{rendered}\",含分隔符 \"{Separator}\"。");
        }
    }

    // ---- 单射性本身的性质测试(不是只查前提,而是查结论)----

    [Fact]
    public void ComposedKeysAreInjectiveEvenWhenRecordKeysContainSeparators()
    {
        var composed = new Dictionary<string, (string SchemaId, string RecordKey)>(StringComparer.Ordinal);

        foreach (string schemaId in PrefixedSchemaIds)
        {
            foreach (string recordKey in AdversarialRecordKeys)
            {
                string key = string.Concat(schemaId, Separator, recordKey);

                Assert.False(
                    composed.TryGetValue(key, out var clash),
                    $"拼接键不再单射:({schemaId}, {recordKey}) 与 ({clash.SchemaId}, {clash.RecordKey}) " +
                    $"都拼成 \"{key}\"。");
                composed.Add(key, (schemaId, recordKey));

                // 反向可解析:取第一个分隔符之前,必须原样还原 schemaId。
                int cut = key.IndexOf(Separator, StringComparison.Ordinal);
                Assert.Equal(schemaId, key.Substring(0, cut));
                Assert.Equal(recordKey, key.Substring(cut + Separator.Length));
            }
        }

        Assert.Equal(PrefixedSchemaIds.Length * AdversarialRecordKeys.Length, composed.Count);
    }

    // ---- 检查器自身的对照:合成违规输入必须被点名 ----

    [Fact]
    public void DetectorNamesTheSeparatorViolationAndTheOffendingId()
    {
        string? violation = FirstViolation(SetWithSeparatorInId);

        Assert.NotNull(violation);
        Assert.Contains("前提 1", violation, StringComparison.Ordinal);
        Assert.Contains("bad" + Separator + "id", violation, StringComparison.Ordinal);
    }

    [Fact]
    public void DetectorAcceptsAWellFormedSet()
    {
        Assert.Null(FirstViolation(WellFormedSet));
    }

    /// <summary>
    /// 返回被破坏的前提及其证据;成立时返回 null。失败信息必须点名**是哪一条**塌了、**是谁**塌的,
    /// 否则将来它红的时候,读到的人还得把这套推理重新推一遍。
    /// </summary>
    private static string? FirstViolation(IReadOnlyList<string> schemaIds)
    {
        foreach (string id in schemaIds)
        {
            if (id.Contains(Separator, StringComparison.Ordinal))
            {
                return $"前提 1 已塌:schemaId \"{id}\" 含分隔符 \"{Separator}\"。" +
                       $"拼接键 schemaId{Separator}<记录键> 因此无法按第一个分隔符还原 schemaId," +
                       "不同记录可互相夺舍(症状:被当成重投丢弃并返回 Accepted)。";
            }
        }

        return null;
    }

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
}
