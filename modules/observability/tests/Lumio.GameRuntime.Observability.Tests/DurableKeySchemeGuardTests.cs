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
/// durable 键是 <c>schemaId + KeySeparator + 记录键</c>(WAL 的记录键又是
/// <c>recordSeq + KeySeparator + payloadHash</c>)。这种拼接式编码的单射性**不是编码给的,
/// 是数据借的**,而借来的东西没有守护——守护是为编码写的,不是为借条写的。本组就是那张借条的守护。
///
/// <para>单射需要**两条**前提同时成立:</para>
/// <list type="number">
/// <item><b>前提 0:分隔符无自重叠</b>——不存在真后缀等于真前缀。否则「分隔符之前的字段」的
///       尾部可以与分隔符的头部拼出一个更早的分隔符,首次出现的位置就不再是字段边界。</item>
/// <item><b>前提 1:出现在分隔符之前的字段不含分隔符</b>——落到本方案是 schemaId,
///       以及 WAL 记录键里的 <c>recordSeq</c> 渲染。</item>
/// </list>
///
/// <para>分隔符**之后**的字段(<c>idempotencyKey</c>、<c>payloadHash</c>)含分隔符完全无害,
/// 不构成前提;schema 本来就允许 <c>idempotencyKey</c> 含冒号。</para>
///
/// <para><b>两处更正记录</b>(留着,因为本仓那条 lesson 讲的正是「闭合到哪一步就只声称到哪一步」):</para>
/// <list type="bullet">
/// <item>最初报「三条前提」,把「没有 schemaId 是另一个的前缀」当作必要条件。错——带分隔符的
///       方案按首次出现切分即可,前缀关系无关;那是**无分隔符纯拼接**的条件。</item>
/// <item>改口后又把前提 1 单独当作**充分**条件。仍错,反例(已实跑):分隔符 <c>"::"</c> 时,
///       <c>("a", ":b")</c> 与 <c>("a:", "b")</c> 都拼成 <c>"a:::b"</c>,而 <c>"a"</c> 与
///       <c>"a:"</c> 都不含 <c>"::"</c>。缺的正是前提 0。</item>
/// </list>
///
/// <para>因此本组不满足于断言前提,而是直接**搜索碰撞**:<see cref="FirstCollision"/> 在给定的
/// (分隔符, schemaId 集合, 记录键集合) 上穷举拼接结果找重复。它对违规组合必须真的能红——
/// 下面的负向用例就是拿来证明这一点的。</para>
/// </summary>
public sealed class DurableKeySchemeGuardTests
{
    private const string Separator = DurableEvidenceRouter.KeySeparator;

    /// <summary>生产实际用来加前缀的三个 schemaId。</summary>
    private static readonly string[] PrefixedSchemaIds =
    {
        SchemaIdOf(nameof(TxnJournalRecord)),
        SchemaIdOf(nameof(CommandLogRecord)),
        SchemaIdOf(nameof(WalRecordEnvelope)),
    };

    /// <summary>
    /// 记录键样本。全部**故意**含分隔符:分隔符之后的维度不影响单射,这组样本是可执行的文档。
    /// 真正能破坏单射的两个维度(分隔符自重叠、schemaId 含分隔符)由下面的负向用例覆盖。
    /// </summary>
    private static readonly string[] RecordKeys =
    {
        "abc", "a" + Separator + "b", Separator + "leading", "trailing" + Separator,
        "1" + Separator + "2" + Separator + "3", "txn-journal-record" + Separator + "abc",
    };

    private static readonly ulong[] RecordSeqSamples = { 0UL, 1UL, 1234567890UL, ulong.MaxValue };

    // 负向样本:提为字段而非内联数组实参(CA1861)。
    private static readonly string[] IdsWithSeparator = { "txn-journal-record", "bad" + Separator + "id" };
    private static readonly string[] TwoPlainIds = { "a", "b" };
    private static readonly string[] KeysAroundSeparator = { ":b", "b" };
    private static readonly string[] IdsWithTrailingColon = { "a", "a:" };
    private static readonly string[] IdsWhereOneContainsSeparator = { "a", "a:b" };
    private static readonly string[] KeysStraddlingSeparator = { "b:c", "c" };

    // ---- 前提 0:分隔符无自重叠 ----

    [Fact]
    public void SeparatorHasNoSelfOverlap()
    {
        Assert.False(
            HasSelfOverlap(Separator),
            $"前提 0 已塌:分隔符 \"{Separator}\" 自重叠(某真后缀等于某真前缀)。" +
            "「分隔符之前的字段」的尾部可与分隔符的头部拼出更早的分隔符,首次出现位置不再是字段边界," +
            $"于是不同记录可拼出同一个键。实例:分隔符 \"::\" 时 (\"a\", \":b\") 与 (\"a:\", \"b\") 都拼成 \"a:::b\"。");
    }

    [Theory]
    [InlineData(":", false)]
    [InlineData("ab", false)]   // 多字符但无自重叠 —— 宽度本身不是判据
    [InlineData("::", true)]
    [InlineData(":::", true)]
    [InlineData("aba", true)]
    public void SelfOverlapDetectorIsCorrect(string separator, bool expected)
    {
        Assert.Equal(expected, HasSelfOverlap(separator));
    }

    // ---- 前提 1:分隔符之前的字段不含分隔符 ----

    [Fact]
    public void NoSchemaIdReachableByTheRouterContainsTheSeparator()
    {
        // 必须守 Bindings.All——那是 DurableEvidenceRouter.SchemaIdOf 实际解析的表。
        // Catalog.SchemaIds 是**另一个独立生成的 artifact**,仓内没有任何断言保证两者集合相等,
        // 只守 Catalog 会让一个仅存在于 Bindings 的违规 id 完全溜过。两者并集一起守。
        string[] reachable = Bindings.All.Select(b => b.SchemaId)
            .Concat(Catalog.SchemaIds)
            .Concat(PrefixedSchemaIds)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        Assert.Null(FirstIdContainingSeparator(Separator, reachable));
    }

    [Fact]
    public void RecordSeqRendersWithoutTheSeparator()
    {
        // WAL 记录键是 recordSeq:payloadHash;recordSeq 处在分隔符之前,同样受前提 1 约束。
        Assert.Equal(
            typeof(ulong),
            typeof(WalRecordEnvelope).GetProperty(nameof(WalRecordEnvelope.RecordSeq))!.PropertyType);

        foreach (ulong value in RecordSeqSamples)
        {
            string rendered = value.ToString(CultureInfo.InvariantCulture);
            Assert.False(
                rendered.Contains(Separator, StringComparison.Ordinal),
                $"前提 1 已塌:recordSeq {value} 渲染为 \"{rendered}\",含分隔符 \"{Separator}\"。");
        }
    }

    // ---- 结论:在生产参数下真的单射 ----

    [Fact]
    public void ProductionKeySchemeIsInjective()
    {
        Assert.Null(FirstCollision(Separator, PrefixedSchemaIds, RecordKeys));
    }

    // ---- 负向:碰撞搜索器对每一种违规形状都必须真的能红 ----

    [Fact]
    public void CollisionSearchCatchesASelfOverlappingSeparator()
    {
        // reviewer 给的反例:("a", ":b") 与 ("a:", "b") 在分隔符 "::" 下同拼成 "a:::b"。
        string? collision = FirstCollision("::", IdsWithTrailingColon, KeysAroundSeparator);

        Assert.NotNull(collision);
        Assert.Contains("a:::b", collision, StringComparison.Ordinal);
    }

    [Fact]
    public void CollisionSearchCatchesASchemaIdContainingTheSeparator()
    {
        // ("a", "b:c") 与 ("a:b", "c") 在分隔符 ":" 下同拼成 "a:b:c"——前提 1 被破坏。
        string? collision = FirstCollision(":", IdsWhereOneContainsSeparator, KeysStraddlingSeparator);

        Assert.NotNull(collision);
        Assert.Contains("a:b:c", collision, StringComparison.Ordinal);
    }

    [Fact]
    public void CollisionSearchIsQuietOnAWellFormedScheme()
    {
        Assert.Null(FirstCollision(":", TwoPlainIds, RecordKeys));
    }

    [Fact]
    public void SeparatorDetectorNamesTheOffendingId()
    {
        string? offender = FirstIdContainingSeparator(Separator, IdsWithSeparator);

        Assert.NotNull(offender);
        Assert.Contains("前提 1", offender, StringComparison.Ordinal);
        Assert.Contains("bad" + Separator + "id", offender, StringComparison.Ordinal);
    }

    // ---- 判定器 ----

    /// <summary>真后缀等于真前缀即为自重叠。单字符分隔符恒为 false。</summary>
    private static bool HasSelfOverlap(string separator)
    {
        for (int i = 1; i < separator.Length; i++)
        {
            if (string.Equals(separator[i..], separator[..^i], StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static string? FirstIdContainingSeparator(string separator, IReadOnlyList<string> schemaIds)
    {
        foreach (string id in schemaIds)
        {
            if (id.Contains(separator, StringComparison.Ordinal))
            {
                return $"前提 1 已塌:schemaId \"{id}\" 含分隔符 \"{separator}\"。" +
                       "拼接键无法按分隔符首次出现还原 schemaId,不同记录可互相夺舍" +
                       "(症状:被当成重投丢弃并返回 Accepted)。";
            }
        }

        return null;
    }

    /// <summary>
    /// 穷举 (schemaId × 记录键) 的拼接结果找重复。找到即返回两组来源与碰撞键;单射则返回 null。
    /// 这是**查结论**而不是查前提——前提清单写错过两次,结论查不了假。
    /// </summary>
    private static string? FirstCollision(
        string separator, IReadOnlyList<string> schemaIds, IReadOnlyList<string> recordKeys)
    {
        var seen = new Dictionary<string, (string SchemaId, string RecordKey)>(StringComparer.Ordinal);

        foreach (string schemaId in schemaIds)
        {
            foreach (string recordKey in recordKeys)
            {
                string key = string.Concat(schemaId, separator, recordKey);
                if (seen.TryGetValue(key, out var previous))
                {
                    return $"拼接键不再单射:({previous.SchemaId}, {previous.RecordKey}) 与 " +
                           $"({schemaId}, {recordKey}) 都拼成 \"{key}\"(分隔符 \"{separator}\")。";
                }

                seen.Add(key, (schemaId, recordKey));
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
