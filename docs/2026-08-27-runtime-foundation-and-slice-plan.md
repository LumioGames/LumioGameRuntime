# Runtime Foundation and First Vertical Slice Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use subagent-driven-development to implement this plan task-by-task (hosts without subagents: its Inline Fallback section). Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 在不改变 `LGE-V1.3-2026-08-27` 公共语义的前提下，建立 LumioGameRuntime 的 .NET 工程基线、generated-contract 边界、最小 Observability/Config/ECS/Command/Coordination/GAS/Replication/Persistence/Simulation 骨架，以及可执行的 Reference Host，使单线程 Foundation 闭环能够调用唯一 `run_tick`、走完固定 13 相，并产出可复现的 Canonical State Hash。

**Architecture:** 逻辑模块先于物理程序集；所有生产依赖遵守既有 DAG，第三方库只存在于 Adapter/internal implementation。`simulation` 唯一编排 Logical Tick；`ecs` 唯一持有 World-local 状态；`command` 在 Prepare 阶段完成全部业务拒绝；`coordination` 在 durable `CommitIntent` 后按 `VoxelCommit -> EcsCommandBufferCommit` 固定顺序推进；`GasAndEventFinalize` 是唯一权威 Tick Commit Point。Reference Host 是 test-only consumer，不得成为任何生产程序集的依赖。

**Tech Stack:** .NET SDK `10.0.11`、C# 14、`net10.0;netstandard2.1` 生产目标、xUnit v3 `4.0.0`、Microsoft Testing Platform `2.3.3`、coverlet.MTP `10.0.1`、CsCheck `4.7.0`、Friflo.Engine.ECS `3.6.0`（Adapter candidate）、MessagePack `3.1.8`（primitive codec Adapter）、BCL `System.Threading.Channels`/`ArrayPool`/`Brotli`；全部版本经中央包管理与 locked restore 固定。

## Global Constraints

- 本计划中的所有 Runtime C# 类型均为**未冻结候选 API**；公共字段、枚举、Schema、ID、ErrorCode、Fixture 和 generated method shape 以架构源生成物为准。
- 不手写 generated contract，不在 Runtime 增加公共 Tick/Txn/Replication/Mapping/Snapshot/Failure Bundle 字段。
- 不把 Host Wall Clock、Socket、Connection、CoreCLR/ALC 创建、Renderer、Release Pool、Voxel 内部 Storage 或具体 Gameplay 内容放入 Runtime。
- 不建立 `Common`、`Utils`、`Globals`、通用 Event Bus 或通用 DI 容器；共享类型放在其语义拥有者或中立 generated assembly。
- 生产代码不得引用 `modules/testing`、Reference Host、xUnit、CsCheck、Coverlet、BenchmarkDotNet 或 fault injection 类型。
- `Diagnostic` 事件允许采样和受控丢弃；`TxnJournal`、`CommandLog`、`WAL`、`CommitIntent` 与 participant marker 不得静默丢失，且不得由 `LoggingEvent` 替代。
- V1 已有字段写入发生异常时执行 Fail-stop，不提供字段级 Undo；`Prepared`/durable `CommitIntent` 后不允许业务拒绝。
- LocalEmbedded 仍走完整 Envelope、Schema、权限、大小、队列、Canonical decode 和 Barrier，只允许省略 Socket。
- 每个任务先写失败测试或失败验证，再写最小实现；提交前运行该任务列出的精确命令。
- 本计划不批准 `RT-D-001..011`。任何候选选型只产出可证伪证据。

---

## 1. 计划范围与退出条件

### 1.1 纳入范围

1. 仓库 SDK、构建、包、SBOM、generated-contract 和 architecture-test 基线。
2. `observability` 的 Event/Metric/Trace Port、producer sequence、有界 Diagnostic 路由、durable evidence 路由和 Failure Bundle 组装。
3. `config` 的 generated artifact validation、固定六层 merge、不可变 `ConfigSnapshot` 和 Tick Barrier activation；不提供 `compile` API。
4. `ecs` 的 World/Entity/Generation、storage Adapter、Query、Read/Write View、ChangeSet、Snapshot View、Owner Thread 与 Fail-stop。
5. `command` 的每 Processor Buffer、Deferred Token、稳定合并、Preflight、`PreparedGameDelta` 与幂等 Apply。
6. `coordination` 的 Revision Vector、CrossWorldTxn、Reservation、durable `CommitIntent`、participant marker、SnapshotCut 与 crash resolution。
7. `gas` 的最小 Type/Handle/Framework Context，且所有权威 Attribute/Tag 仍投影在 ECS。
8. `replication` 的最小 Mapping/Net-Local Identity Context，供 Phase graph 和 hash manifest 有真实模块参与者；六步 Client Apply 留在后续 Vertical Slice 卡。
9. `persistence` 的 Canonical codec 最小面，供 State Hash 和 durable record bytes 使用；完整 Snapshot Activate/Recovery 留在后续卡。
10. `simulation` 的 `SimulationSession`、唯一 `RunTick`、固定 13 相、Processor Plan、Ingress/Native barrier、Determinism、Fail-stop 与 duplicate Tick result。
11. test-only `ReferenceVoxelAuthorityPort`、Reference Host、Replay/Hash/First Difference，以及第一条单线程 Foundation Scenario。

### 1.2 明确不纳入

- 完整 Replication FullSnapshot/Delta/History/Tombstone/六步客户端权威 Apply。
- 完整 GAS Ability/Effect state machine、Modifier evaluation、PredictionFrame。
- Snapshot Staging/Activate、WAL backend、恢复重放与 Voxel Snapshot Adapter。
- Hot Reload 双 Scope、迁移与卸载实现；本阶段只在 project graph 中保留其未来位置，不创建实现源码。
- OpenTelemetry/Microsoft Logging Provider Adapter、外部 durable storage、Host transport、真实 Voxel/native bridge。
- RT-D 性能阈值批准、N/N-1、Transport/Codec 公共决策或公共 Schema 修改。

### 1.3 Foundation 可运行退出条件

执行以下命令后必须全部成功：

```bash
dotnet --version
dotnet restore --locked-mode Lumio.GameRuntime.slnx
dotnet build Lumio.GameRuntime.slnx -c Release --no-restore
dotnet test Lumio.GameRuntime.slnx -c Release --no-build --report-trx --results-directory artifacts/test-results
bash eng/verify-generated-contracts.sh
bash eng/verify-project-graph.sh
bash eng/verify-dependencies.sh
```

机器可验证结果：

- SDK 输出精确为 `10.0.11`。
- Restore/build/test/三个验证脚本退出码均为 `0`。
- 第一条 Reference Host Scenario 运行两个相同 seed 的 64 Tick，两个 run 的每 Tick `StateHash` 全部相同。
- Phase trace 每 Tick 精确包含 13 个且只包含 13 个 Phase；`GasAndEventFinalize` 是唯一 `Committed=true` 的权威 commit transition。
- 对任一 Phase 注入 fatal fault，Session 进入 `Faulted`，后续 `RunTick` 被拒绝，并产出带 `SnapshotId` 或 `noSnapshotReason` 的合法 Failure Bundle。
- Voxel prepare 失败发生在 durable `CommitIntent` 前且不产生可见 ECS/Voxel 写；durable `CommitIntent` 后只出现幂等 `Applied`/`AlreadyApplied` 或基础设施 Fatal。
- 生产 project graph 中不存在任何到 `Lumio.GameRuntime.Testing`、Reference Host 或测试包的边。

---

## 2. 不可改写的运行时基线

### 2.1 固定 13 相

```text
IngressCapture
-> DecodeAndCanonicalize
-> ApplyInputs
-> ProcessorPlan
-> CrossWorldPrepare
-> NativeJobBarrier
-> CommitDecision
-> VoxelCommit
-> EcsCommandBufferCommit
-> GasAndEventFinalize
-> ReplicationProjection
-> SnapshotHashMetrics
-> EgressPublish
```

- Host 每个 Logical Tick 只调用一次 `IRuntimeSession.RunTick`。
- `GasAndEventFinalize` 是唯一权威 Tick Commit Point。
- `ReplicationProjection`、`SnapshotHashMetrics` 与 `EgressPublish` 消费已提交状态，不得重新改变该 Tick 的权威 Game/ECS/GAS/Voxel 结果。
- duplicate `TickId` 返回缓存的幂等 `TickRunResult` 或明确不匹配错误，不重复执行 Processor、Txn 或 Apply。

### 2.2 Command 与 Txn 边界

```text
CommandBuffer: Open -> Sealed -> Merged -> Prepared -> Applied
CrossWorldTxn: Created -> Prepared -> CommitIntent -> Committed
Prepared -> Aborted | Expired
CommitIntent apply failure -> Indeterminate
```

- Buffer merge key 固定为 `Phase + ProcessorId + LocalSequence`。
- `PreparedGameDelta` 建立后不得因业务规则、预算、ID、Schema、权限或容量再次拒绝。
- durable `CommitIntent` 必须先于首个参与者写入。
- Apply 固定为 `VoxelCommit -> EcsCommandBufferCommit`。
- participant marker 使用 `NotStarted / Unknown / Applied / Failed`，禁止 Boolean 缩减。

### 2.3 线程与可见性

- Simulation Owner Thread 是权威 World 唯一写线程。
- worker/native/IO 只读 immutable snapshot 或返回有界 completion；completion 只在声明 Barrier 应用。
- 在 `GasAndEventFinalize` 前，Tick 对外尚未 committed；在其后，后续 Phase 只能读取 committed view。
- 对象地址、集合插入顺序、worker 完成时序、wall clock、diagnostic timestamp 不进入 State Hash。

---

## 3. Foundation 工程与文件地图

以下路径是实施时创建的目标，不代表当前仓库已有代码。

```text
LumioGameRuntime/
├─ global.json
├─ Directory.Build.props
├─ Directory.Build.targets
├─ Directory.Packages.props
├─ NuGet.config
├─ Lumio.GameRuntime.slnx
├─ eng/
│  ├─ dependency-policy.json
│  ├─ generate-contracts.sh
│  ├─ verify-dependencies.sh
│  ├─ verify-generated-contracts.sh
│  ├─ verify-project-graph.sh
│  └─ generate-sbom.sh
├─ src/
│  └─ Lumio.GameRuntime.GeneratedContracts/
│     ├─ Lumio.GameRuntime.GeneratedContracts.csproj
│     ├─ GeneratedContractManifest.cs
│     └─ README.md
├─ modules/
│  ├─ observability/{src,tests}/...
│  ├─ config/{src,tests}/...
│  ├─ ecs/{src,tests}/...
│  ├─ command/{src,tests}/...
│  ├─ coordination/{src,tests}/...
│  ├─ gas/{src,tests}/...
│  ├─ replication/{src,tests}/...
│  ├─ persistence/{src,tests}/...
│  ├─ simulation/{src,tests}/...
│  └─ testing/{src,tests,scenarios}/...
└─ tests/
   ├─ Lumio.GameRuntime.GeneratedContracts.Tests/
   └─ Lumio.GameRuntime.Architecture.Tests/
```

### 3.1 Foundation project 依赖

```text
GeneratedContracts
├─ Observability
├─ Config -> Observability
├─ Ecs -> Config, Observability
├─ Command -> Ecs, Observability
├─ Coordination -> Ecs, Command, Observability, Generated Voxel Authority Contract
├─ Gas -> Ecs, Command, Config, Observability
├─ Replication -> Ecs, Gas, Coordination, Config, Observability, Generated Voxel Replica Contract
├─ Persistence -> Ecs, Gas, Replication, Coordination, Config, Observability, Generated Voxel Snapshot Contract
└─ Simulation -> Ecs, Command, Coordination, Gas, Replication, Persistence, Config, Observability

Testing/ReferenceHost -> all required production modules
Production -X-> Testing/ReferenceHost
```

Foundation 允许 `Replication` 和 `Persistence` 仅有最小可构造 facade；依赖方向保持最终 DAG，不使用反向 callback 绕过 project graph。

---

## 4. 版本锁定与供应链基线

### 4.1 `global.json`

```json
{
  "sdk": {
    "version": "10.0.11",
    "rollForward": "disable",
    "allowPrerelease": false
  }
}
```

### 4.2 中央包版本

```xml
<Project>
  <PropertyGroup>
    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
    <CentralPackageTransitivePinningEnabled>true</CentralPackageTransitivePinningEnabled>
  </PropertyGroup>
  <ItemGroup>
    <PackageVersion Include="xunit.v3" Version="4.0.0" />
    <PackageVersion Include="Microsoft.Testing.Platform" Version="2.3.3" />
    <PackageVersion Include="coverlet.MTP" Version="10.0.1" />
    <PackageVersion Include="CsCheck" Version="4.7.0" />
    <PackageVersion Include="Friflo.Engine.ECS" Version="3.6.0" />
    <PackageVersion Include="MessagePack" Version="3.1.8" />
  </ItemGroup>
</Project>
```

- `Friflo.Engine.ECS` 只能由 `Lumio.GameRuntime.Ecs.Adapters.Friflo` 引用；Foundation 也保留 Adapter-neutral reference storage 以执行语义测试。
- `MessagePack` 只能由 Persistence codec Adapter 引用；禁止 Contractless/Typeless/global resolver，Canonical field order 由 Runtime/架构源定义。
- Test packages 只能进入 `*.Tests`、Reference Host、Fuzz、Benchmark 项目。
- 每次 restore 生成 `packages.lock.json`；CI 使用 `--locked-mode`。

### 4.3 项目模板

生产项目：

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFrameworks>net10.0;netstandard2.1</TargetFrameworks>
    <Nullable>enable</Nullable>
    <ImplicitUsings>disable</ImplicitUsings>
    <LangVersion>14.0</LangVersion>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <Deterministic>true</Deterministic>
    <ContinuousIntegrationBuild>true</ContinuousIntegrationBuild>
    <GenerateDocumentationFile>true</GenerateDocumentationFile>
  </PropertyGroup>
</Project>
```

测试项目：

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <OutputType>Exe</OutputType>
    <IsTestProject>true</IsTestProject>
    <Nullable>enable</Nullable>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="xunit.v3" />
    <PackageReference Include="Microsoft.Testing.Platform" />
    <PackageReference Include="coverlet.MTP" />
  </ItemGroup>
</Project>
```

---

## 5. Generated Contract 消费索引

本仓只引用架构源工具链生成的实际 namespace/type；下表是实现者必须从 `GeneratedContractManifest` 定位的语义，不授权手写替代类型。

| 语义 | 计划中使用的名字 | 所有者 | Foundation 用途 |
|---|---|---|---|
| Release/Session/World/Tick/Processor/Txn/Snapshot ID | `GameReleaseId`, `SessionId`, `WorldId`, `TickId`, `ProcessorId`, `TxnId`, `SnapshotId` | Architecture generated contracts | correlation、幂等键、生命周期绑定 |
| Component/Field/Schema | `ComponentTypeId`, `ComponentFieldId`, `SchemaEpoch`, `GeneratedComponentSchemaView` | Architecture/Game generated contracts | ECS registry、query/write preflight |
| Error/Failure | `ErrorIdentity`, `FailureClassView` | Architecture generated contracts | Rejected/Retryable/Fatal 分类 |
| Input/Envelope | `InputEnvelopeView`, `CanonicalInputView` | Architecture generated contracts | Ingress canonicalization |
| Config | `GeneratedConfigArtifactView`, `GeneratedConfigTableView`, `GeneratedConfigValueView` | Architecture generated contracts | validate、六层 merge、typed reader |
| Logging/Failure Bundle | `LoggingEventView`, `FailureBundleView`, `FailureBundleBuilder` | Architecture generated contracts | diagnostic/durable/failure evidence |
| Revision/Txn Journal | `SessionRevisionVectorContractView`, `TxnJournalRecordView`, `CommandLogRecordView`, `WalRecordEnvelopeView` | Architecture generated contracts | durable CommitIntent、participant marker、recovery |
| Voxel Authority | `VoxelPrepareRequestView`, `VoxelPreparedToken`, `VoxelCommitReceiptView`, `VoxelAbortReceiptView`, `VoxelTxnStatusView` | Generated Voxel Authority Contract | ReferenceVoxelAuthorityPort、coordination |
| Voxel Replica/Snapshot | generated Replica/Snapshot views | Generated Voxel contracts | 仅预留项目依赖；完整实现不在本计划 |
| Mapping/Identity | `GeneratedMappingSetView`, `NetEntityId`, identity/lifecycle views | Architecture/Game generated contracts | minimal Replication Context |

`GeneratedContractManifest` 必须至少暴露：

```csharp
namespace Lumio.GameRuntime.GeneratedContracts;

public static class GeneratedContractManifest
{
    public const string ArchitectureBaselineId = "LGE-V1.3-2026-08-27";
    public static string ArchitectureSourceCommit { get; } = GetRequired("architectureSourceCommit");
    public static string SchemaRegistrySha256 { get; } = GetRequired("schemaRegistrySha256");
    public static string IdRegistrySha256 { get; } = GetRequired("idRegistrySha256");
    public static string FixtureRegistrySha256 { get; } = GetRequired("fixtureRegistrySha256");
    public static string GeneratorVersion { get; } = GetRequired("generatorVersion");

    private static string GetRequired(string key) =>
        GeneratedManifestResource.ReadRequiredString(key);
}
```

这段由既有生成命令产出或封装生成 manifest；实现 Agent 不得在 Runtime 中自行推导 Schema/ID。

---

## 6. Foundation 关键 C# 类型草图

以下草图用于锁定实现形状与任务间接口。所有 public/internal 选择均为未冻结候选，最终 public surface 需由 Runtime API Schema 和 `RT-D-001` 决策门确认。

### 6.1 Observability

```csharp
namespace Lumio.GameRuntime.Observability;

public enum ObservabilityState
{
    Created,
    Configured,
    Running,
    Flushing,
    Closed,
    Degraded,
    Faulted
}

public readonly record struct ProducerSequence(ulong Value)
{
    public ProducerSequence Next()
    {
        checked { return new ProducerSequence(Value + 1UL); }
    }
}

public readonly record struct RuntimeCorrelation(
    SessionId SessionId,
    WorldId WorldId,
    TickId TickId,
    TxnId? TxnId,
    SnapshotId? SnapshotId,
    ProcessorId? ProcessorId);

public readonly record struct MetricPoint(
    string InstrumentName,
    double Value,
    RuntimeCorrelation Correlation,
    ProducerSequence Sequence);

public readonly record struct TraceSpanStart(
    string OperationName,
    RuntimeCorrelation Correlation,
    ProducerSequence Sequence);

public readonly record struct TraceSpanHandle(ulong Value);

public enum DiagnosticWriteStatus
{
    Accepted,
    DroppedBestEffort,
    Backpressured,
    Closed,
    Fatal
}

public readonly record struct DiagnosticWriteResult(
    DiagnosticWriteStatus Status,
    ErrorIdentity? Error);

public enum DurableWriteStatus
{
    Accepted,
    AlreadyAccepted,
    Backpressured,
    Closed,
    Fatal
}

public readonly record struct DurableWriteResult(
    DurableWriteStatus Status,
    ErrorIdentity? Error);

public interface IRuntimeEventPort
{
    DiagnosticWriteResult TryWrite(in LoggingEventView @event);
}

public interface IMetricPort
{
    DiagnosticWriteResult TryRecord(in MetricPoint point);
}

public interface ITracePort
{
    DiagnosticWriteResult TryStart(in TraceSpanStart start, out TraceSpanHandle handle);
    DiagnosticWriteResult TryStop(TraceSpanHandle handle, in RuntimeCorrelation correlation);
}

public interface IDurableEvidencePort
{
    DurableWriteResult Append(in TxnJournalRecordView record);
    DurableWriteResult Append(in CommandLogRecordView record);
    DurableWriteResult Append(in WalRecordEnvelopeView record);
}

public interface IFailureBundlePort
{
    DurableWriteResult Write(in FailureBundleView bundle);
}

public readonly record struct DiagnosticQueueBudget(
    int DiagnosticQueueCapacity,
    int DiagnosticQueueBytes)
{
    public void Validate()
    {
        if (DiagnosticQueueCapacity <= 0) throw new ArgumentOutOfRangeException(nameof(DiagnosticQueueCapacity));
        if (DiagnosticQueueBytes <= 0) throw new ArgumentOutOfRangeException(nameof(DiagnosticQueueBytes));
    }
}

public readonly record struct FailureContextSnapshot(
    GameReleaseId GameReleaseId,
    SessionId SessionId,
    WorldId WorldId,
    TickId LastTickId,
    string BootstrapPhase,
    SessionRevisionVectorContractView LastKnownRevision,
    SnapshotId? SnapshotId,
    string? NoSnapshotReason,
    ReadOnlyMemory<byte> CanonicalEvidenceManifest);

public sealed class FailureBundleAssembler
{
    public FailureBundleView Build(in FailureContextSnapshot context, in ErrorIdentity fatalError)
    {
        bool hasSnapshot = context.SnapshotId.HasValue;
        if (hasSnapshot == string.IsNullOrWhiteSpace(context.NoSnapshotReason))
        {
            throw new InvalidOperationException("Exactly one of SnapshotId and NoSnapshotReason must be present.");
        }

        return FailureBundleBuilder.Build(context, fatalError);
    }
}
```

### 6.2 Config

```csharp
namespace Lumio.GameRuntime.Config;

public enum ConfigLayer
{
    Engine = 0,
    Platform = 1,
    Server = 2,
    Product = 3,
    Environment = 4,
    UserOrSession = 5
}

public enum ConfigArtifactState
{
    Generated,
    Validated,
    Staged,
    Active,
    Rejected,
    Superseded
}

public readonly record struct ConfigArtifactReadResult(
    bool Found,
    GeneratedConfigArtifactView Artifact,
    ErrorIdentity? Error);

public interface IGeneratedConfigArtifactPort
{
    ConfigArtifactReadResult Read(GameReleaseId releaseId, ConfigLayer layer);
}

public readonly record struct ConfigValidationIssue(
    string Path,
    ErrorIdentity Error,
    FailureClassView FailureClass);

public sealed class ConfigValidationReport
{
    private readonly ConfigValidationIssue[] _issues;

    public ConfigValidationReport(ReadOnlySpan<ConfigValidationIssue> issues)
    {
        _issues = issues.ToArray();
    }

    public bool IsValid => _issues.Length == 0;
    public ReadOnlyMemory<ConfigValidationIssue> Issues => _issues;
}

public readonly record struct ConfigSnapshotId(ulong Value);

public interface IConfigSnapshotView
{
    ConfigSnapshotId SnapshotId { get; }
    SchemaEpoch SchemaEpoch { get; }
    bool TryOpenTable(string tableName, out ConfigTableReader reader);
}

public readonly struct ConfigTableReader
{
    private readonly GeneratedConfigTableView _table;

    public ConfigTableReader(GeneratedConfigTableView table) => _table = table;

    public bool TryGet(string key, string column, out GeneratedConfigValueView value) =>
        _table.TryGet(key, column, out value);
}

public sealed class ConfigSnapshot : IConfigSnapshotView
{
    private readonly GeneratedConfigTableView[] _tables;

    public ConfigSnapshot(
        ConfigSnapshotId snapshotId,
        SchemaEpoch schemaEpoch,
        ReadOnlySpan<GeneratedConfigTableView> tables)
    {
        SnapshotId = snapshotId;
        SchemaEpoch = schemaEpoch;
        _tables = tables.ToArray();
    }

    public ConfigSnapshotId SnapshotId { get; }
    public SchemaEpoch SchemaEpoch { get; }

    public bool TryOpenTable(string tableName, out ConfigTableReader reader)
    {
        for (int i = 0; i < _tables.Length; i++)
        {
            if (StringComparer.Ordinal.Equals(_tables[i].Name, tableName))
            {
                reader = new ConfigTableReader(_tables[i]);
                return true;
            }
        }

        reader = default;
        return false;
    }
}

public readonly record struct ConfigStageResult(
    bool Staged,
    ConfigSnapshotId SnapshotId,
    ErrorIdentity? Error);

public readonly record struct ConfigActivationResult(
    bool Activated,
    ConfigSnapshotId ActiveSnapshotId,
    ErrorIdentity? Error);

public sealed class ConfigActivationSlot
{
    private ConfigSnapshot? _active;
    private ConfigSnapshot? _staged;

    public IConfigSnapshotView Active =>
        _active ?? throw new InvalidOperationException("No active ConfigSnapshot.");

    public ConfigStageResult Stage(ConfigSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        _staged = snapshot;
        return new ConfigStageResult(true, snapshot.SnapshotId, null);
    }

    public ConfigActivationResult ActivateAtBarrier(TickId tickId)
    {
        if (_staged is null)
        {
            return new ConfigActivationResult(false, _active?.SnapshotId ?? default, GeneratedErrors.NoStagedConfig);
        }

        _active = _staged;
        _staged = null;
        return new ConfigActivationResult(true, _active.SnapshotId, null);
    }
}
```

### 6.3 ECS

```csharp
namespace Lumio.GameRuntime.Ecs;

public readonly record struct LocalEntityId(uint Index, uint Generation)
{
    public bool IsDefault => Index == 0U && Generation == 0U;
}

public enum EcsWorldState
{
    Created,
    Registering,
    Ready,
    Running,
    Draining,
    Disposed,
    Faulted
}

public readonly record struct EcsBudget(
    int MaxEntities,
    int MaxQueryResults,
    int MaxChangeEntries,
    int MaxSnapshotBytes);

public readonly record struct QuerySpec(
    ReadOnlyMemory<ComponentTypeId> Required,
    ReadOnlyMemory<ComponentTypeId> Excluded,
    ReadOnlyMemory<ComponentFieldId> ReadSet,
    ReadOnlyMemory<ComponentFieldId> WriteSet);

public readonly record struct QueryBudget(int MaxEntities, int MaxBytes);
public readonly record struct StorageQueryHandle(uint Value);
public readonly record struct StorageReadSnapshotHandle(ulong Value);

public readonly record struct ComponentInitValue(
    ComponentTypeId ComponentType,
    ComponentFieldId Field,
    ReadOnlyMemory<byte> CanonicalValue);

public readonly record struct ComponentInitBatch(ReadOnlyMemory<ComponentInitValue> Values);

public enum StorageOperationStatus
{
    Accepted,
    Rejected,
    AlreadyApplied,
    Retryable,
    Fatal
}

public readonly record struct StorageOperationResult(
    StorageOperationStatus Status,
    ErrorIdentity? Error);

internal interface IWorldStorageAdapter : IDisposable
{
    StorageOperationResult Register(in GeneratedComponentSchemaView schema);
    StorageOperationResult Create(LocalEntityId entity, in ComponentInitBatch components);
    StorageOperationResult Destroy(LocalEntityId entity);
    StorageOperationResult CompileQuery(in QuerySpec spec, out StorageQueryHandle handle);
    StorageOperationResult EnumerateOrdered(
        StorageQueryHandle handle,
        Span<LocalEntityId> destination,
        out int written);
    StorageOperationResult ReadField(
        LocalEntityId entity,
        ComponentTypeId componentType,
        ComponentFieldId field,
        Span<byte> destination,
        out int written);
    StorageOperationResult WriteExistingField(
        LocalEntityId entity,
        ComponentTypeId componentType,
        ComponentFieldId field,
        ReadOnlySpan<byte> canonicalValue);
    StorageOperationResult CaptureReadSnapshot(out StorageReadSnapshotHandle handle);
    StorageOperationResult ReleaseReadSnapshot(StorageReadSnapshotHandle handle);
    StorageOperationResult ValidateIntegrity();
}

public readonly record struct ChangeEntry(
    LocalEntityId Entity,
    ComponentTypeId ComponentType,
    ComponentFieldId Field,
    ReadOnlyMemory<byte> CanonicalBefore,
    ReadOnlyMemory<byte> CanonicalAfter);

public sealed class ChangeSet
{
    private readonly ChangeEntry[] _entries;

    public ChangeSet(WorldId worldId, TickId tickId, ReadOnlySpan<ChangeEntry> entries)
    {
        WorldId = worldId;
        TickId = tickId;
        _entries = entries.ToArray();
        Array.Sort(_entries, ChangeEntryCanonicalComparer.Instance);
    }

    public WorldId WorldId { get; }
    public TickId TickId { get; }
    public ReadOnlyMemory<ChangeEntry> Entries => _entries;
}

public readonly ref struct EcsReadView
{
    private readonly EcsWorld _world;
    private readonly TickId _tickId;
    private readonly uint _epoch;

    internal EcsReadView(EcsWorld world, TickId tickId, uint epoch)
    {
        _world = world;
        _tickId = tickId;
        _epoch = epoch;
    }

    public StorageOperationResult TryRead(
        LocalEntityId entity,
        ComponentTypeId componentType,
        ComponentFieldId field,
        Span<byte> destination,
        out int written) =>
        _world.ReadExistingField(_tickId, _epoch, entity, componentType, field, destination, out written);
}

public readonly ref struct EcsWriteView
{
    private readonly EcsWorld _world;
    private readonly TickId _tickId;
    private readonly uint _epoch;
    private readonly ReadOnlySpan<ComponentFieldId> _declaredWriteSet;

    internal EcsWriteView(
        EcsWorld world,
        TickId tickId,
        uint epoch,
        ReadOnlySpan<ComponentFieldId> declaredWriteSet)
    {
        _world = world;
        _tickId = tickId;
        _epoch = epoch;
        _declaredWriteSet = declaredWriteSet;
    }

    public StorageOperationResult Write(
        LocalEntityId entity,
        ComponentTypeId componentType,
        ComponentFieldId field,
        ReadOnlySpan<byte> canonicalValue) =>
        _world.WriteExistingField(
            _tickId,
            _epoch,
            _declaredWriteSet,
            entity,
            componentType,
            field,
            canonicalValue);
}

public readonly record struct EcsWorldCreateRequest(
    WorldId WorldId,
    EcsBudget Budget,
    IConfigSnapshotView ConfigSnapshot);

public readonly record struct EcsWorldCreateResult(
    bool Created,
    EcsWorld? World,
    ErrorIdentity? Error);

public interface IEcsSnapshotProvider
{
    EcsSnapshotCaptureResult Capture(in SnapshotCutView cut);
}

public readonly record struct EcsSnapshotCaptureResult(
    StorageOperationStatus Status,
    EcsWorldReadSnapshot? Snapshot,
    ErrorIdentity? Error);

public sealed class EcsWorldReadSnapshot : IDisposable
{
    internal EcsWorldReadSnapshot(
        WorldId worldId,
        TickId tickId,
        StorageReadSnapshotHandle handle,
        Action<StorageReadSnapshotHandle> release)
    {
        WorldId = worldId;
        TickId = tickId;
        Handle = handle;
        _release = release;
    }

    private readonly Action<StorageReadSnapshotHandle> _release;
    private int _disposed;
    internal StorageReadSnapshotHandle Handle { get; }
    public WorldId WorldId { get; }
    public TickId TickId { get; }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0) _release(Handle);
    }
}
```

### 6.4 Command

```csharp
namespace Lumio.GameRuntime.Command;

public enum CommandBufferState
{
    Open,
    Sealed,
    Merged,
    Prepared,
    Applied
}

public readonly record struct DeferredEntityToken(
    TickId TickId,
    ProcessorId ProcessorId,
    uint LocalSequence);

public readonly record struct CommandSortKey(
    TickPhase Phase,
    ProcessorId ProcessorId,
    uint LocalSequence) : IComparable<CommandSortKey>
{
    public int CompareTo(CommandSortKey other)
    {
        int phase = Phase.CompareTo(other.Phase);
        if (phase != 0) return phase;
        int processor = ProcessorId.CompareTo(other.ProcessorId);
        if (processor != 0) return processor;
        return LocalSequence.CompareTo(other.LocalSequence);
    }
}

public enum GameCommandKind
{
    CreateEntity,
    WriteExistingField,
    DestroyEntity
}

public readonly record struct CommandTarget(
    LocalEntityId? ExistingEntity,
    DeferredEntityToken? DeferredEntity)
{
    public bool HasExactlyOneTarget => ExistingEntity.HasValue ^ DeferredEntity.HasValue;
}

public readonly record struct GameCommand(
    CommandSortKey SortKey,
    GameCommandKind Kind,
    CommandTarget Target,
    ComponentTypeId? ComponentType,
    ComponentFieldId? Field,
    ReadOnlyMemory<byte> CanonicalPayload);

public sealed class SealedCommandBuffer
{
    private readonly GameCommand[] _commands;

    internal SealedCommandBuffer(
        TickId tickId,
        ProcessorId processorId,
        ReadOnlySpan<GameCommand> commands)
    {
        TickId = tickId;
        ProcessorId = processorId;
        _commands = commands.ToArray();
    }

    public TickId TickId { get; }
    public ProcessorId ProcessorId { get; }
    public ReadOnlyMemory<GameCommand> Commands => _commands;
}

public sealed class ProcessorCommandBuffer
{
    private readonly List<GameCommand> _commands = new();
    private uint _localSequence;

    public ProcessorCommandBuffer(TickId tickId, TickPhase phase, ProcessorId processorId)
    {
        TickId = tickId;
        Phase = phase;
        ProcessorId = processorId;
        State = CommandBufferState.Open;
    }

    public TickId TickId { get; }
    public TickPhase Phase { get; }
    public ProcessorId ProcessorId { get; }
    public CommandBufferState State { get; private set; }

    public DeferredEntityToken CreateEntity(in ComponentInitBatch components)
    {
        EnsureOpen();
        uint sequence = _localSequence++;
        var token = new DeferredEntityToken(TickId, ProcessorId, sequence);
        _commands.Add(CommandFactory.CreateEntity(Phase, ProcessorId, sequence, token, components));
        return token;
    }

    public void WriteExistingField(
        in CommandTarget target,
        ComponentTypeId componentType,
        ComponentFieldId field,
        ReadOnlyMemory<byte> canonicalValue)
    {
        EnsureOpen();
        uint sequence = _localSequence++;
        _commands.Add(CommandFactory.WriteField(
            Phase,
            ProcessorId,
            sequence,
            target,
            componentType,
            field,
            canonicalValue));
    }

    public void DestroyEntity(in CommandTarget target)
    {
        EnsureOpen();
        uint sequence = _localSequence++;
        _commands.Add(CommandFactory.Destroy(Phase, ProcessorId, sequence, target));
    }

    public SealedCommandBuffer Seal()
    {
        EnsureOpen();
        State = CommandBufferState.Sealed;
        return new SealedCommandBuffer(TickId, ProcessorId, CollectionsMarshal.AsSpan(_commands));
    }

    private void EnsureOpen()
    {
        if (State != CommandBufferState.Open)
            throw new InvalidOperationException($"CommandBuffer is {State}.");
    }
}

public sealed class MergedCommandBatch
{
    private readonly GameCommand[] _commands;

    public MergedCommandBatch(TickId tickId, ReadOnlySpan<GameCommand> commands)
    {
        TickId = tickId;
        _commands = commands.ToArray();
    }

    public TickId TickId { get; }
    public ReadOnlyMemory<GameCommand> Commands => _commands;
}

public sealed class CommandBufferMerger
{
    public MergedCommandBatch Merge(TickId tickId, ReadOnlySpan<SealedCommandBuffer> buffers)
    {
        var commands = new List<GameCommand>();
        for (int i = 0; i < buffers.Length; i++)
        {
            if (buffers[i].TickId != tickId) throw new InvalidOperationException("Tick mismatch.");
            commands.AddRange(buffers[i].Commands.ToArray());
        }
        commands.Sort(static (left, right) => left.SortKey.CompareTo(right.SortKey));
        return new MergedCommandBatch(tickId, CollectionsMarshal.AsSpan(commands));
    }
}

public readonly record struct CommandReservationSet(
    int ReservedEntitySlots,
    int ReservedChangeEntries,
    int ReservedBytes);

public sealed class PreparedGameDelta
{
    internal PreparedGameDelta(
        TickId tickId,
        MergedCommandBatch batch,
        CommandReservationSet reservations,
        ReadOnlyMemory<byte> canonicalDigest)
    {
        TickId = tickId;
        Batch = batch;
        Reservations = reservations;
        CanonicalDigest = canonicalDigest;
    }

    public TickId TickId { get; }
    public MergedCommandBatch Batch { get; }
    public CommandReservationSet Reservations { get; }
    public ReadOnlyMemory<byte> CanonicalDigest { get; }
}

public enum CommandPrepareStatus
{
    Prepared,
    Rejected,
    Retryable,
    Fatal
}

public readonly record struct CommandPrepareResult(
    CommandPrepareStatus Status,
    PreparedGameDelta? Prepared,
    ErrorIdentity? Error);

public enum CommandApplyStatus
{
    Applied,
    AlreadyApplied,
    InfrastructureFault
}

public readonly record struct CommandApplyReceipt(
    CommandApplyStatus Status,
    TickId TickId,
    ReadOnlyMemory<byte> CanonicalDigest,
    ChangeSet? ChangeSet,
    ErrorIdentity? Error);

public interface ICommandApplyPort
{
    CommandApplyReceipt Apply(PreparedGameDelta prepared);
}
```

### 6.5 Coordination

```csharp
namespace Lumio.GameRuntime.Coordination;

public enum CrossWorldTxnState
{
    Created,
    Prepared,
    CommitIntent,
    Committed,
    Aborted,
    Expired,
    Indeterminate
}

public enum TxnParticipantState
{
    NotStarted,
    Unknown,
    Applied,
    Failed
}

public readonly record struct SessionRevisionVectorView(
    Revision EcsRevision,
    Revision GasRevision,
    Revision VoxelWorldRevision,
    Revision ReplicationRevision,
    SchemaEpoch SchemaEpoch);

public sealed class SessionRevisionVectorStore
{
    private SessionRevisionVectorView _current;

    public SessionRevisionVectorStore(SessionRevisionVectorView initial) => _current = initial;
    public SessionRevisionVectorView Read() => _current;

    internal void AdvanceAfterCommitted(in SessionRevisionVectorView next)
    {
        RevisionVectorRules.RequireMonotonic(_current, next);
        _current = next;
    }
}

public readonly record struct ReservationLeaseId(ulong Value);

public sealed class ReservationLease : IDisposable
{
    private int _disposed;

    internal ReservationLease(ReservationLeaseId leaseId, TickId tickId)
    {
        LeaseId = leaseId;
        TickId = tickId;
    }

    public ReservationLeaseId LeaseId { get; }
    public TickId TickId { get; }
    public bool IsDisposed => Volatile.Read(ref _disposed) != 0;
    public void Dispose() => Interlocked.Exchange(ref _disposed, 1);
}

public readonly record struct CrossWorldPrepareRequest(
    TxnId TxnId,
    TickId TickId,
    SessionRevisionVectorView ExpectedRevision,
    PreparedGameDelta PreparedGameDelta,
    VoxelPrepareRequestView VoxelRequest,
    ulong DeadlineLogicalTicks);

public sealed class CrossWorldPreparedTxn
{
    internal CrossWorldPreparedTxn(
        TxnId txnId,
        TickId tickId,
        PreparedGameDelta gameDelta,
        VoxelPreparedToken voxelToken,
        ReservationLease reservation)
    {
        TxnId = txnId;
        TickId = tickId;
        GameDelta = gameDelta;
        VoxelToken = voxelToken;
        Reservation = reservation;
    }

    public TxnId TxnId { get; }
    public TickId TickId { get; }
    public PreparedGameDelta GameDelta { get; }
    public VoxelPreparedToken VoxelToken { get; }
    public ReservationLease Reservation { get; }
}

public enum TxnPrepareStatus
{
    Prepared,
    Rejected,
    Retryable,
    Fatal
}

public readonly record struct TxnPrepareResult(
    TxnPrepareStatus Status,
    CrossWorldPreparedTxn? Prepared,
    ErrorIdentity? Error);

public enum JournalAppendStatus
{
    Durable,
    AlreadyDurable,
    Backpressured,
    Fatal
}

public readonly record struct JournalAppendResult(
    JournalAppendStatus Status,
    ErrorIdentity? Error);

public interface ITxnJournalPort
{
    JournalAppendResult Append(in TxnJournalRecordView record);
    TxnJournalLookupResult Lookup(TxnId txnId);
}

public readonly record struct TxnJournalLookupResult(
    bool Found,
    TxnJournalRecordView Record,
    ErrorIdentity? Error);

public interface IVoxelAuthorityPort
{
    VoxelPreparePortResult Prepare(in VoxelPrepareRequestView request);
    VoxelCommitPortResult Commit(VoxelPreparedToken token);
    VoxelAbortPortResult Abort(VoxelPreparedToken token);
    VoxelStatusPortResult Query(TxnId txnId);
}

public readonly record struct VoxelPreparePortResult(
    TxnPrepareStatus Status,
    VoxelPreparedToken Token,
    ErrorIdentity? Error);

public readonly record struct VoxelCommitPortResult(
    TxnParticipantState ParticipantState,
    VoxelCommitReceiptView Receipt,
    ErrorIdentity? Error);

public readonly record struct VoxelAbortPortResult(
    bool Aborted,
    VoxelAbortReceiptView Receipt,
    ErrorIdentity? Error);

public readonly record struct VoxelStatusPortResult(
    TxnParticipantState ParticipantState,
    VoxelTxnStatusView Status,
    ErrorIdentity? Error);

public enum TxnCommitStatus
{
    Committed,
    AlreadyCommitted,
    Indeterminate,
    Fatal
}

public readonly record struct TxnCommitResult(
    TxnCommitStatus Status,
    TxnParticipantState VoxelParticipant,
    TxnParticipantState EcsParticipant,
    CommandApplyReceipt EcsReceipt,
    SessionRevisionVectorView Revision,
    ErrorIdentity? Error);

public interface ICrossWorldCoordinator
{
    TxnPrepareResult Prepare(in CrossWorldPrepareRequest request);
    TxnCommitResult Commit(CrossWorldPreparedTxn prepared);
    TxnCommitResult QueryResult(TxnId txnId);
    TxnAbortResult Abort(CrossWorldPreparedTxn prepared);
}

public readonly record struct TxnAbortResult(
    bool Aborted,
    CrossWorldTxnState State,
    ErrorIdentity? Error);

public readonly record struct SnapshotCutView(
    SnapshotId SnapshotId,
    TickId TickId,
    SessionRevisionVectorView RevisionVector,
    SchemaEpoch SchemaEpoch);

public readonly record struct SnapshotCutOpenResult(
    bool Opened,
    SnapshotCutLease? Lease,
    ErrorIdentity? Error);

public sealed class SnapshotCutLease : IDisposable
{
    private int _disposed;

    internal SnapshotCutLease(SnapshotCutView cut, Action<SnapshotCutView> release)
    {
        Cut = cut;
        _release = release;
    }

    private readonly Action<SnapshotCutView> _release;
    public SnapshotCutView Cut { get; }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0) _release(Cut);
    }
}
```

### 6.6 GAS 最小面

```csharp
namespace Lumio.GameRuntime.Gas;

public enum GasFrameworkState
{
    Unloaded,
    Registered,
    Ready,
    Running,
    Draining,
    Faulted
}

public readonly record struct AbilityTypeId(uint Value);
public readonly record struct AbilityInstanceId(ulong Value);
public readonly record struct EffectTypeId(uint Value);
public readonly record struct EffectInstanceId(ulong Value);

public readonly record struct AbilityHandle(
    WorldId WorldId,
    AbilityInstanceId InstanceId,
    uint Generation);

public readonly record struct EffectHandle(
    WorldId WorldId,
    EffectInstanceId InstanceId,
    uint Generation);

public interface IGasEcsProjectionPort
{
    GasProjectionReadResult ReadAuthoritative(LocalEntityId entity, ComponentFieldId field);
    GasProjectionWriteResult WriteAuthoritative(
        LocalEntityId entity,
        ComponentFieldId field,
        ReadOnlySpan<byte> canonicalValue);
}

public readonly record struct GasProjectionReadResult(
    bool Found,
    ReadOnlyMemory<byte> CanonicalValue,
    ErrorIdentity? Error);

public readonly record struct GasProjectionWriteResult(
    bool Written,
    ErrorIdentity? Error);

public sealed class GasWorldContext : IDisposable
{
    private readonly IGasEcsProjectionPort _ecsProjection;
    private uint _abilityGeneration;
    private uint _effectGeneration;

    public GasWorldContext(WorldId worldId, IGasEcsProjectionPort ecsProjection)
    {
        WorldId = worldId;
        _ecsProjection = ecsProjection;
        State = GasFrameworkState.Unloaded;
    }

    public WorldId WorldId { get; }
    public GasFrameworkState State { get; private set; }

    public void Register() => State = Require(State, GasFrameworkState.Unloaded, GasFrameworkState.Registered);
    public void MarkReady() => State = Require(State, GasFrameworkState.Registered, GasFrameworkState.Ready);
    public void Start() => State = Require(State, GasFrameworkState.Ready, GasFrameworkState.Running);
    public void BeginDrain() => State = Require(State, GasFrameworkState.Running, GasFrameworkState.Draining);
    public void Fault() => State = GasFrameworkState.Faulted;
    public void Dispose() => State = GasFrameworkState.Unloaded;

    public AbilityHandle CreateAbilityHandle(AbilityInstanceId id) =>
        new(WorldId, id, checked(++_abilityGeneration));

    public EffectHandle CreateEffectHandle(EffectInstanceId id) =>
        new(WorldId, id, checked(++_effectGeneration));

    private static GasFrameworkState Require(
        GasFrameworkState actual,
        GasFrameworkState expected,
        GasFrameworkState next)
    {
        if (actual != expected) throw new InvalidOperationException($"Expected {expected}, actual {actual}.");
        return next;
    }
}
```

### 6.7 Replication 最小面

```csharp
namespace Lumio.GameRuntime.Replication;

public enum ReplicationContextState
{
    Created,
    Snapshotting,
    AwaitingBaselineAck,
    Active,
    Resyncing,
    Draining,
    Closed,
    Faulted
}

public readonly record struct ReplicationContextId(ulong Value);

public sealed class MappingSetView
{
    internal MappingSetView(
        SchemaEpoch schemaEpoch,
        ReadOnlyMemory<byte> mappingSetHash,
        GeneratedMappingSetView generated)
    {
        SchemaEpoch = schemaEpoch;
        MappingSetHash = mappingSetHash.ToArray();
        Generated = generated;
    }

    public SchemaEpoch SchemaEpoch { get; }
    public ReadOnlyMemory<byte> MappingSetHash { get; }
    internal GeneratedMappingSetView Generated { get; }
}

public interface IGeneratedMappingValidator
{
    MappingValidationResult Validate(in GeneratedMappingSetView mappingSet);
}

public readonly record struct MappingValidationResult(
    bool Valid,
    MappingSetView? View,
    ErrorIdentity? Error);

public sealed class MappingRegistry
{
    private MappingSetView? _active;

    public MappingValidationResult StageAndActivate(
        in GeneratedMappingSetView mappingSet,
        IGeneratedMappingValidator validator)
    {
        MappingValidationResult result = validator.Validate(mappingSet);
        if (!result.Valid || result.View is null) return result;
        _active = result.View;
        return result;
    }

    public MappingSetView Active =>
        _active ?? throw new InvalidOperationException("No active MappingSet.");
}

public sealed class NetEntityMappingTable
{
    private readonly Dictionary<NetEntityId, LocalEntityId> _toLocal = new();
    private readonly Dictionary<LocalEntityId, NetEntityId> _toNet = new();

    public bool TryBind(NetEntityId netEntityId, LocalEntityId localEntityId)
    {
        if (_toLocal.ContainsKey(netEntityId) || _toNet.ContainsKey(localEntityId)) return false;
        _toLocal.Add(netEntityId, localEntityId);
        _toNet.Add(localEntityId, netEntityId);
        return true;
    }

    public bool TryResolveLocal(NetEntityId id, out LocalEntityId local) => _toLocal.TryGetValue(id, out local);
    public bool TryResolveNet(LocalEntityId id, out NetEntityId net) => _toNet.TryGetValue(id, out net);
}

public sealed class ReplicationContext
{
    public ReplicationContext(
        ReplicationContextId contextId,
        WorldId worldId,
        MappingRegistry mappings,
        NetEntityMappingTable identities)
    {
        ContextId = contextId;
        WorldId = worldId;
        Mappings = mappings;
        Identities = identities;
        State = ReplicationContextState.Created;
    }

    public ReplicationContextId ContextId { get; }
    public WorldId WorldId { get; }
    public ReplicationContextState State { get; private set; }
    public MappingRegistry Mappings { get; }
    public NetEntityMappingTable Identities { get; }

    public void Fault() => State = ReplicationContextState.Faulted;
    public void Close() => State = ReplicationContextState.Closed;
}
```

### 6.8 Persistence Canonical codec 最小面

```csharp
namespace Lumio.GameRuntime.Persistence;

public readonly record struct CanonicalEncodeBudget(int MaxBytes);
public readonly record struct CanonicalDecodeBudget(int MaxInputBytes, int MaxOutputBytes, int MaxDepth);

public enum CanonicalCodecStatus
{
    Encoded,
    Decoded,
    Rejected,
    Retryable,
    Fatal
}

public readonly record struct CanonicalEncodeResult(
    CanonicalCodecStatus Status,
    ReadOnlyMemory<byte> Bytes,
    ErrorIdentity? Error);

public readonly record struct CanonicalDecodeResult<T>(
    CanonicalCodecStatus Status,
    T? Value,
    int ConsumedBytes,
    ErrorIdentity? Error);

public interface ICanonicalRecord
{
    void WriteCanonical(ref CanonicalRecordWriter writer);
}

public ref struct CanonicalRecordWriter
{
    private IBufferWriter<byte> _destination;
    private readonly CanonicalEncodeBudget _budget;
    private int _written;

    public CanonicalRecordWriter(IBufferWriter<byte> destination, CanonicalEncodeBudget budget)
    {
        _destination = destination;
        _budget = budget;
        _written = 0;
    }

    public int Written => _written;

    public void WriteUInt64(ulong value) => CanonicalPrimitiveWriter.WriteUInt64(ref this, value);
    public void WriteBytes(ReadOnlySpan<byte> value) => CanonicalPrimitiveWriter.WriteBytes(ref this, value);
    public void WriteUtf8(string value) => CanonicalPrimitiveWriter.WriteUtf8(ref this, value);

    internal Span<byte> GetSpan(int size)
    {
        if (size < 0 || _written + size > _budget.MaxBytes)
            throw new CanonicalBudgetExceededException(_budget.MaxBytes);
        return _destination.GetSpan(size);
    }

    internal void Advance(int count)
    {
        _destination.Advance(count);
        _written += count;
    }
}

public ref struct CanonicalRecordReader
{
    private ReadOnlySpan<byte> _source;
    private readonly CanonicalDecodeBudget _budget;
    private int _consumed;

    public CanonicalRecordReader(ReadOnlySpan<byte> source, CanonicalDecodeBudget budget)
    {
        if (source.Length > budget.MaxInputBytes)
            throw new CanonicalBudgetExceededException(budget.MaxInputBytes);
        _source = source;
        _budget = budget;
        _consumed = 0;
    }

    public int Consumed => _consumed;
    public ulong ReadUInt64() => CanonicalPrimitiveReader.ReadUInt64(ref this);
    public ReadOnlySpan<byte> ReadBytes() => CanonicalPrimitiveReader.ReadBytes(ref this);
    public string ReadUtf8() => CanonicalPrimitiveReader.ReadUtf8(ref this);

    internal ReadOnlySpan<byte> Take(int count)
    {
        if (count < 0 || _consumed + count > _source.Length || _consumed + count > _budget.MaxOutputBytes)
            throw new CanonicalBudgetExceededException(_budget.MaxOutputBytes);
        ReadOnlySpan<byte> value = _source.Slice(_consumed, count);
        _consumed += count;
        return value;
    }
}

public interface ICanonicalCodec
{
    CanonicalEncodeResult Encode<T>(in T value, in CanonicalEncodeBudget budget)
        where T : ICanonicalRecord;

    CanonicalDecodeResult<T> Decode<T>(
        ReadOnlySpan<byte> bytes,
        in CanonicalDecodeBudget budget,
        CanonicalDecoder<T> decoder);
}

public delegate T CanonicalDecoder<T>(ref CanonicalRecordReader reader);
```

### 6.9 Simulation

```csharp
namespace Lumio.GameRuntime.Simulation;

public enum TickPhase
{
    IngressCapture = 0,
    DecodeAndCanonicalize = 1,
    ApplyInputs = 2,
    ProcessorPlan = 3,
    CrossWorldPrepare = 4,
    NativeJobBarrier = 5,
    CommitDecision = 6,
    VoxelCommit = 7,
    EcsCommandBufferCommit = 8,
    GasAndEventFinalize = 9,
    ReplicationProjection = 10,
    SnapshotHashMetrics = 11,
    EgressPublish = 12
}

public enum SimulationSessionState
{
    Created,
    Initialized,
    Ready,
    Running,
    Paused,
    Draining,
    Snapshotted,
    Disposed,
    Faulted
}

public readonly record struct DeterminismContext(
    GameReleaseId GameReleaseId,
    SessionId SessionId,
    WorldId WorldId,
    TickId TickId,
    ulong Seed,
    SchemaEpoch SchemaEpoch,
    ConfigSnapshotId ConfigSnapshotId);

public readonly record struct TickInput(
    TickId TickId,
    ReadOnlyMemory<InputEnvelopeView> Envelopes,
    ulong DeterministicSeed);

public readonly record struct ProcessorInvocation(
    ProcessorId ProcessorId,
    TickPhase Phase,
    ReadOnlyMemory<ComponentFieldId> ReadSet,
    ReadOnlyMemory<ComponentFieldId> WriteSet,
    int Budget);

public sealed class ProcessorPlan
{
    private readonly ProcessorInvocation[] _invocations;

    public ProcessorPlan(ReadOnlySpan<ProcessorInvocation> invocations)
    {
        _invocations = invocations.ToArray();
    }

    public ReadOnlyMemory<ProcessorInvocation> Invocations => _invocations;
}

public readonly record struct PhaseExecutionRecord(
    TickPhase Phase,
    bool Entered,
    bool Completed,
    bool AuthoritativeCommitPoint,
    ErrorIdentity? Error);

public enum TickRunStatus
{
    Committed,
    AlreadyCommitted,
    Rejected,
    Retryable,
    Faulted
}

public sealed class TickRunResult
{
    private readonly PhaseExecutionRecord[] _phases;

    public TickRunResult(
        TickRunStatus status,
        TickId tickId,
        ReadOnlyMemory<byte> stateHash,
        SessionRevisionVectorView revision,
        ReadOnlySpan<PhaseExecutionRecord> phases,
        ErrorIdentity? error)
    {
        Status = status;
        TickId = tickId;
        StateHash = stateHash.ToArray();
        Revision = revision;
        _phases = phases.ToArray();
        Error = error;
    }

    public TickRunStatus Status { get; }
    public TickId TickId { get; }
    public ReadOnlyMemory<byte> StateHash { get; }
    public SessionRevisionVectorView Revision { get; }
    public ReadOnlyMemory<PhaseExecutionRecord> Phases => _phases;
    public ErrorIdentity? Error { get; }
}

public interface IRuntimeSession : IDisposable
{
    SessionId SessionId { get; }
    WorldId WorldId { get; }
    SimulationSessionState State { get; }
    TickRunResult RunTick(in TickInput input);
}

public interface IProcessor
{
    ProcessorId Id { get; }
    ProcessorExecutionResult Execute(
        in DeterminismContext context,
        in EcsReadView readView,
        ref EcsWriteView writeView,
        ProcessorCommandBuffer commandBuffer);
}

public readonly record struct ProcessorExecutionResult(
    bool Completed,
    ErrorIdentity? Error);

public interface IStateHashContributor
{
    string ContributorId { get; }
    void WriteCanonical(ref CanonicalRecordWriter writer);
}

public sealed class StateHashCoordinator
{
    private readonly IStateHashContributor[] _contributors;

    public StateHashCoordinator(ReadOnlySpan<IStateHashContributor> contributors)
    {
        _contributors = contributors.ToArray();
        Array.Sort(_contributors, static (left, right) =>
            StringComparer.Ordinal.Compare(left.ContributorId, right.ContributorId));
    }

    public ReadOnlyMemory<byte> Compute(in CanonicalEncodeBudget budget)
    {
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new CanonicalRecordWriter(buffer, budget);
        for (int i = 0; i < _contributors.Length; i++)
        {
            writer.WriteUtf8(_contributors[i].ContributorId);
            _contributors[i].WriteCanonical(ref writer);
        }
        return SHA256.HashData(buffer.WrittenSpan);
    }
}
```

### 6.10 Testing / Reference Host

```csharp
namespace Lumio.GameRuntime.Testing;

public enum ReferenceVoxelTxnState
{
    Missing,
    Prepared,
    Committed,
    Aborted
}

public sealed class ReferenceVoxelAuthorityPort : IVoxelAuthorityPort
{
    private readonly Dictionary<TxnId, ReferenceVoxelTxnRecord> _transactions = new();
    private ulong _nextToken;

    public VoxelPreparePortResult Prepare(in VoxelPrepareRequestView request)
    {
        if (_transactions.TryGetValue(request.TxnId, out ReferenceVoxelTxnRecord existing))
            return existing.ToPrepareResult();

        var token = VoxelPreparedToken.FromUInt64(checked(++_nextToken));
        var record = ReferenceVoxelTxnRecord.Prepared(request.TxnId, token, request);
        _transactions.Add(request.TxnId, record);
        return record.ToPrepareResult();
    }

    public VoxelCommitPortResult Commit(VoxelPreparedToken token)
    {
        ReferenceVoxelTxnRecord record = FindByToken(token);
        record = record.CommitIdempotently();
        _transactions[record.TxnId] = record;
        return record.ToCommitResult();
    }

    public VoxelAbortPortResult Abort(VoxelPreparedToken token)
    {
        ReferenceVoxelTxnRecord record = FindByToken(token);
        record = record.AbortBeforeCommit();
        _transactions[record.TxnId] = record;
        return record.ToAbortResult();
    }

    public VoxelStatusPortResult Query(TxnId txnId)
    {
        return _transactions.TryGetValue(txnId, out ReferenceVoxelTxnRecord record)
            ? record.ToStatusResult()
            : ReferenceVoxelTxnRecord.Missing(txnId).ToStatusResult();
    }

    private ReferenceVoxelTxnRecord FindByToken(VoxelPreparedToken token)
    {
        foreach (ReferenceVoxelTxnRecord record in _transactions.Values)
            if (record.Token == token) return record;
        throw new KeyNotFoundException("Unknown VoxelPreparedToken.");
    }
}

public readonly record struct ReferenceScenario(
    string Name,
    int TickCount,
    ulong Seed,
    ReadOnlyMemory<ReferenceInputAtTick> Inputs,
    ReadOnlyMemory<ReferenceFaultAtBoundary> Faults);

public sealed class ReferenceHost : IDisposable
{
    private readonly IRuntimeSession _session;

    public ReferenceHost(IRuntimeSession session) => _session = session;

    public ReferenceRunResult Run(in ReferenceScenario scenario)
    {
        var results = new TickRunResult[scenario.TickCount];
        for (int i = 0; i < scenario.TickCount; i++)
        {
            var tickId = TickId.FromUInt64((ulong)(i + 1));
            TickInput input = ReferenceInputBuilder.Build(tickId, scenario.Seed, scenario.Inputs.Span);
            results[i] = _session.RunTick(input);
            if (results[i].Status == TickRunStatus.Faulted) break;
        }
        return new ReferenceRunResult(scenario.Name, results);
    }

    public void Dispose() => _session.Dispose();
}

public sealed class ReplayRunner
{
    private readonly Func<IRuntimeSession> _sessionFactory;

    public ReplayRunner(Func<IRuntimeSession> sessionFactory) => _sessionFactory = sessionFactory;

    public ReplayComparison RunTwice(in ReferenceScenario scenario)
    {
        using var firstHost = new ReferenceHost(_sessionFactory());
        using var secondHost = new ReferenceHost(_sessionFactory());
        ReferenceRunResult first = firstHost.Run(scenario);
        ReferenceRunResult second = secondHost.Run(scenario);
        return FirstDifferenceFinder.Compare(first, second);
    }
}
```

---

## 7. Wave 与工作包映射

| Wave | 可并行工作包 | 完成门 |
|---|---|---|
| 1 | Task 1 | SDK/build 属性可验证 |
| 2 | Task 2 | locked restore、license/SBOM policy 可验证 |
| 3 | Task 3 | generated manifest/hash gate 可验证 |
| 4 | Task 4、Task 6、Task 8、Task 18、Task 19、Task 20 | 模块首项目和最小 Port/类型建立；文件集不重叠 |
| 5 | Task 5、Task 7、Task 9、Task 11、Task 14 | 各模块内部语义闭环；无跨模块实现文件重叠 |
| 6 | Task 10、Task 12、Task 15、Task 17 | Prepare 前验证、Owner Thread/Fail-stop、SnapshotCut |
| 7 | Task 13、Task 16 | Command durable evidence 与 durable CommitIntent/恢复闭环 |
| 8 | Task 21、Task 22、Task 23、Task 24 | Simulation 入口、Phase、Plan、Ingress/Native barrier |
| 9 | Task 25、Task 26 | Determinism/State Hash、Fail-stop/Tick Result |
| 10 | Task 27 | test-only Voxel Authority participant |
| 11 | Task 28 | Reference Host 第一条 Foundation scenario |
| 12 | Task 29 | Replay/First Difference |
| 13 | Task 30 | solution/project graph/public surface/isolation 总门 |

同 Wave 内任务不得修改对方文件。发现必须共享文件时，将共享变更移动到前一 Wave 的拥有任务，不在两个分支同时编辑。

---

## 8. 实施任务

### Task 1: `repo-dotnet-baseline` — 固定 SDK、编译属性与多目标规则

**对应设计卡：** `repo-dotnet-baseline`

**Files:**
- Create: `global.json`
- Create: `Directory.Build.props`
- Create: `Directory.Build.targets`
- Create: `.editorconfig`
- Create: `eng/verify-sdk.sh`
- Create: `eng/verify-sdk.ps1`

**Interfaces:**

- **Consumes:** `.spec` 代码风格、目标平台约束、SDK `10.0.11`。
- **Produces:** 所有后续项目继承的 nullable、warnings-as-errors、deterministic、多目标与 SDK gate。

**明确不做：** 不创建模块项目，不选择程序集数量，不引入 NuGet 业务包。

- [ ] **Step 1: 写 SDK 失败验证。** 在 `eng/verify-sdk.sh` 中读取 `dotnet --version`，版本不是 `10.0.11` 时输出 `SDK_MISMATCH expected=10.0.11 actual=<value>` 并退出 `21`；PowerShell 脚本实现相同语义。

```bash
#!/usr/bin/env bash
set -euo pipefail
expected="10.0.11"
actual="$(dotnet --version)"
if [[ "$actual" != "$expected" ]]; then
  echo "SDK_MISMATCH expected=$expected actual=$actual" >&2
  exit 21
fi
echo "SDK_OK version=$actual"
```

- [ ] **Step 2: 在没有 `global.json` 时运行验证。**

```bash
bash eng/verify-sdk.sh
```

Expected: 当前机器若未解析到 `10.0.11`，退出码 `21` 且只出现 `SDK_MISMATCH`；若机器恰好已有该 SDK，则临时把 expected 改为 `0.0.0-negative-fixture` 运行一次并观察同一失败，再恢复。

- [ ] **Step 3: 创建 `global.json` 与统一 MSBuild 属性。** `Directory.Build.props` 至少设置 `Nullable=enable`、`TreatWarningsAsErrors=true`、`Deterministic=true`、`ContinuousIntegrationBuild=true`、`LangVersion=14.0`；生产项目通过属性 `RuntimeProductionProject=true` 获得 `net10.0;netstandard2.1`，测试项目通过 `RuntimeTestProject=true` 获得 `net10.0`。

```xml
<Project>
  <PropertyGroup>
    <Nullable>enable</Nullable>
    <ImplicitUsings>disable</ImplicitUsings>
    <LangVersion>14.0</LangVersion>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <Deterministic>true</Deterministic>
    <ContinuousIntegrationBuild>true</ContinuousIntegrationBuild>
  </PropertyGroup>
  <PropertyGroup Condition="'$(RuntimeProductionProject)' == 'true'">
    <TargetFrameworks>net10.0;netstandard2.1</TargetFrameworks>
    <GenerateDocumentationFile>true</GenerateDocumentationFile>
  </PropertyGroup>
  <PropertyGroup Condition="'$(RuntimeTestProject)' == 'true'">
    <TargetFramework>net10.0</TargetFramework>
    <IsTestProject>true</IsTestProject>
  </PropertyGroup>
</Project>
```

- [ ] **Step 4: 在 `Directory.Build.targets` 添加非法目标组合 gate。** 生产项目出现测试包、测试项目多目标 `netstandard2.1` 或 `LangVersion` 被局部降级时，目标 `ValidateRuntimeBuildProfile` 必须在 `BeforeTargets=PrepareForBuild` 报错。

- [ ] **Step 5: 运行正向验证。**

```bash
dotnet --version
bash eng/verify-sdk.sh
```

Expected: 两行均报告 `10.0.11`，脚本输出 `SDK_OK version=10.0.11`，退出码 `0`。

- [ ] **Step 6: 验证 warnings-as-errors。** 创建临时目录 `/tmp/lumio-sdk-gate`，用继承仓库 props 的最小项目放入一个未使用局部变量警告；`dotnet build` 必须失败。删除临时目录，不把 fixture 提交到仓库。

- [ ] **Step 7: 提交。**

```bash
git add global.json Directory.Build.props Directory.Build.targets .editorconfig eng/verify-sdk.sh eng/verify-sdk.ps1
git commit -m "build: pin runtime dotnet baseline"
```

### Task 2: `repo-supply-chain-policy` — 建立中央包、locked restore、许可证和 SBOM 准入

**对应设计卡：** `repo-supply-chain-policy`

**Files:**
- Create: `Directory.Packages.props`
- Create: `NuGet.config`
- Create: `eng/dependency-policy.json`
- Create: `eng/verify-dependencies.sh`
- Create: `eng/verify-dependencies.ps1`
- Create: `eng/generate-sbom.sh`
- Create: `eng/generate-sbom.ps1`
- Create: `THIRD_PARTY_NOTICES.md`

**Interfaces:**

- **Consumes:** Task 1 的 MSBuild 基线；已选 package/version/license 清单。
- **Produces:** 中央版本、锁文件策略、Adapter-only 包规则、license/vulnerability/SBOM 机器验证。

**明确不做：** 不自动升级包，不批准 Friflo 或 MessagePack 成为稳定 API，不把 SBOM 工具打入生产程序集。

- [ ] **Step 1: 写失败的依赖策略 fixture。** `eng/dependency-policy.json` 定义允许许可证 `MIT, Apache-2.0, BSD-2-Clause, BSD-3-Clause, Zlib`，拒绝模式 `GPL, AGPL, LGPL`，并定义包作用域：`Friflo.Engine.ECS` 仅 `*.Ecs.Adapters.*`，`MessagePack` 仅 `*.Persistence*Adapter*`，测试包仅 `*.Tests`/`ReferenceHost`。

```json
{
  "allowedLicenses": ["MIT", "Apache-2.0", "BSD-2-Clause", "BSD-3-Clause", "Zlib"],
  "requiresLegalReview": ["GPL-2.0", "GPL-3.0", "AGPL-3.0", "LGPL-3.0"],
  "packageScopes": {
    "Friflo.Engine.ECS": ["Lumio.GameRuntime.Ecs.Adapters.Friflo"],
    "MessagePack": ["Lumio.GameRuntime.Persistence"],
    "xunit.v3": ["*.Tests", "Lumio.GameRuntime.ReferenceHost"],
    "CsCheck": ["*.Tests"],
    "coverlet.MTP": ["*.Tests"]
  },
  "forbidFloatingVersions": true,
  "requireLockFiles": true
}
```

- [ ] **Step 2: 先运行空实现脚本并确认失败。**

```bash
bash eng/verify-dependencies.sh
```

Expected: 退出非零并输出 `DEPENDENCY_POLICY_NOT_IMPLEMENTED` 或缺文件错误；不得误报成功。

- [ ] **Step 3: 创建中央包版本。** 精确写入 `xunit.v3=4.0.0`、`Microsoft.Testing.Platform=2.3.3`、`coverlet.MTP=10.0.1`、`CsCheck=4.7.0`、`Friflo.Engine.ECS=3.6.0`、`MessagePack=3.1.8`；禁止 `*`、版本区间和 prerelease。

- [ ] **Step 4: 实现 package graph 检查。** 脚本运行 `dotnet list package --include-transitive --format json`，检查中央版本、项目作用域、license evidence、已知漏洞输出；任何无法确认 license 的依赖返回 `DEPENDENCY_LICENSE_UNKNOWN`，不按允许处理。

- [ ] **Step 5: 实现 SBOM wrapper。** `eng/generate-sbom.sh` 将输出固定到 `artifacts/sbom/`，记录 tool version、package hash 和 project graph；脚本不得修改源码或 package references。

- [ ] **Step 6: 创建负向临时项目。** 在 `/tmp/lumio-dependency-negative` 引入浮动版本 `xunit.v3 Version="*"`，运行验证必须输出 `FLOATING_VERSION_FORBIDDEN`。删除临时项目。

- [ ] **Step 7: 运行正向验证。** 此时仓库尚无项目时，脚本应输出 `DEPENDENCY_POLICY_OK projects=0`；Task 3 以后重新运行时必须检查实际 graph。

```bash
bash eng/verify-dependencies.sh
```

Expected: `DEPENDENCY_POLICY_OK projects=0`，退出码 `0`。

- [ ] **Step 8: 提交。**

```bash
git add Directory.Packages.props NuGet.config eng/dependency-policy.json eng/verify-dependencies.sh eng/verify-dependencies.ps1 eng/generate-sbom.sh eng/generate-sbom.ps1 THIRD_PARTY_NOTICES.md
git commit -m "build: add runtime dependency and sbom policy"
```

### Task 3: `repo-generated-contract-boundary` — 建立架构源 generated-contract 只读边界

**对应设计卡：** `repo-generated-contract-boundary`

**Files:**
- Create: `src/Lumio.GameRuntime.GeneratedContracts/Lumio.GameRuntime.GeneratedContracts.csproj`
- Create: `src/Lumio.GameRuntime.GeneratedContracts/GeneratedContractManifest.cs`
- Create: `src/Lumio.GameRuntime.GeneratedContracts/README.md`
- Create: `eng/generate-contracts.sh`
- Create: `eng/generate-contracts.ps1`
- Create: `eng/verify-generated-contracts.sh`
- Create: `eng/verify-generated-contracts.ps1`
- Create: `tests/Lumio.GameRuntime.GeneratedContracts.Tests/Lumio.GameRuntime.GeneratedContracts.Tests.csproj`
- Create: `tests/Lumio.GameRuntime.GeneratedContracts.Tests/GeneratedContractBaselineTests.cs`

**Interfaces:**

- **Consumes:** Task 1–2；架构源已发布 contract toolchain、Schema/ID/Fixture registry。
- **Produces:** 无业务依赖的 generated assembly、baseline manifest、hash/dirty gate 与 manifest tests。

**明确不做：** 不重新实现生成器，不手写缺失 Schema，不为让测试通过而复制 README 类型。

- [ ] **Step 1: 先写 baseline 失败测试。** 测试断言 `GeneratedContractManifest.ArchitectureBaselineId == "LGE-V1.3-2026-08-27"`，四个 hash/source/version 字段非空，并能按 manifest 定位 Txn Journal、Command Log、WAL、Voxel Authority/Replica/Snapshot、Replication、Config、Logging/Failure contract。

```csharp
[Fact]
public void Manifest_matches_runtime_baseline_and_required_contracts()
{
    Assert.Equal("LGE-V1.3-2026-08-27", GeneratedContractManifest.ArchitectureBaselineId);
    Assert.False(string.IsNullOrWhiteSpace(GeneratedContractManifest.ArchitectureSourceCommit));
    Assert.False(string.IsNullOrWhiteSpace(GeneratedContractManifest.SchemaRegistrySha256));
    Assert.False(string.IsNullOrWhiteSpace(GeneratedContractManifest.IdRegistrySha256));
    Assert.False(string.IsNullOrWhiteSpace(GeneratedContractManifest.FixtureRegistrySha256));
    Assert.All(RequiredContractNames.All, name => Assert.True(GeneratedContractCatalog.Contains(name), name));
}
```

- [ ] **Step 2: 运行测试并确认缺项目/类型失败。**

```bash
dotnet test tests/Lumio.GameRuntime.GeneratedContracts.Tests/Lumio.GameRuntime.GeneratedContracts.Tests.csproj
```

Expected: `MSB1009` 或 `CS0246`；不得先创建手写 stub 让测试绿。

- [ ] **Step 3: 创建项目和生成 wrapper。** generated project 设置 `RuntimeProductionProject=true`，但不得引用任何 Runtime 模块。`generate-contracts.sh` 只调用架构源工具链，并把架构源路径从 `LUMIO_ARCHITECTURE_ROOT` 读取；变量缺失时退出 `31` 并输出 `ARCHITECTURE_ROOT_MISSING`。

```bash
#!/usr/bin/env bash
set -euo pipefail
: "${LUMIO_ARCHITECTURE_ROOT:?ARCHITECTURE_ROOT_MISSING}"
python3 "$LUMIO_ARCHITECTURE_ROOT/tools/lumio_contract.py" generate-runtime \
  --baseline LGE-V1.3-2026-08-27 \
  --output "src/Lumio.GameRuntime.GeneratedContracts/Generated"
```

- [ ] **Step 4: 生成并锁定 manifest。** 运行生成器后，manifest 记录架构源 commit、registry hashes、generator version；生成目录加显式 header `Generated; do not edit`，README 说明更新命令和只读规则。

- [ ] **Step 5: 实现 dirty/hash gate。** `verify-generated-contracts.sh` 在临时目录重生一次，并比较 manifest/hash/文件清单；差异输出 `GENERATED_CONTRACT_DRIFT` 和相对路径，退出 `32`。

- [ ] **Step 6: 运行生成和测试。**

```bash
bash eng/generate-contracts.sh
bash eng/verify-generated-contracts.sh
dotnet test tests/Lumio.GameRuntime.GeneratedContracts.Tests/Lumio.GameRuntime.GeneratedContracts.Tests.csproj
```

Expected: 两个脚本退出 `0`；测试 `Failed: 0`，baseline 精确为 V1.3。

- [ ] **Step 7: 验证无反向依赖。**

```bash
dotnet list src/Lumio.GameRuntime.GeneratedContracts/Lumio.GameRuntime.GeneratedContracts.csproj reference
```

Expected: `There are no Project to Project references in project`。

- [ ] **Step 8: 提交。** 提交 generated manifest 和架构源允许提交的 generated C#；若仓库规范只提交 manifest，则脚本必须在 restore/build 前自动生成且 CI 有架构源 artifact。

```bash
git add src/Lumio.GameRuntime.GeneratedContracts tests/Lumio.GameRuntime.GeneratedContracts.Tests eng/generate-contracts.sh eng/generate-contracts.ps1 eng/verify-generated-contracts.sh eng/verify-generated-contracts.ps1
git commit -m "build: add generated runtime contract boundary"
```

### Task 4: `obs-event-ports-and-context` — 建立 Observability Port、状态与 producer sequence

**对应设计卡：** `obs-event-ports-and-context`

**Files:**
- Create: `modules/observability/src/Lumio.GameRuntime.Observability/Lumio.GameRuntime.Observability.csproj`
- Create: `modules/observability/tests/Lumio.GameRuntime.Observability.Tests/Lumio.GameRuntime.Observability.Tests.csproj`
- Create: `modules/observability/src/Lumio.GameRuntime.Observability/ObservabilityModule.cs`
- Create: `modules/observability/src/Lumio.GameRuntime.Observability/ObservabilityServices.cs`
- Create: `modules/observability/src/Lumio.GameRuntime.Observability/Lifecycle/ObservabilityState.cs`
- Create: `modules/observability/src/Lumio.GameRuntime.Observability/Contracts/IRuntimeEventPort.cs`
- Create: `modules/observability/src/Lumio.GameRuntime.Observability/Contracts/IMetricPort.cs`
- Create: `modules/observability/src/Lumio.GameRuntime.Observability/Contracts/ITracePort.cs`
- Create: `modules/observability/src/Lumio.GameRuntime.Observability/Context/ProducerSequence.cs`
- Create: `modules/observability/tests/Lumio.GameRuntime.Observability.Tests/ProducerSequenceTests.cs`

**Interfaces:**

- **Consumes:** generated `LoggingEventView`、IDs/Error；Task 3。
- **Produces:** 第三方无关的 Event/Metric/Trace Port、exact lifecycle、单 producer monotonic sequence。

**明确不做：** 不引入 MEL/OTel Provider，不实现 durable journal，不缓存权威 World 状态。

- [ ] **Step 1: 写 producer sequence 失败测试。** 覆盖 `0 -> 1`、连续 10,000 次严格单调、`ulong.MaxValue` checked overflow、两个 producer instance 不共享状态。

```csharp
[Fact]
public void Producer_sequence_is_monotonic_and_overflow_is_visible()
{
    var sequence = new ProducerSequence(0UL);
    for (ulong expected = 1UL; expected <= 10_000UL; expected++)
    {
        sequence = sequence.Next();
        Assert.Equal(expected, sequence.Value);
    }
    Assert.Throws<OverflowException>(() => new ProducerSequence(ulong.MaxValue).Next());
}
```

- [ ] **Step 2: 运行测试确认项目缺失。**

```bash
dotnet test modules/observability/tests/Lumio.GameRuntime.Observability.Tests/Lumio.GameRuntime.Observability.Tests.csproj
```

Expected: `MSB1009`。

- [ ] **Step 3: 创建生产/测试项目。** 生产项目只引用 `GeneratedContracts`；测试项目引用生产项目和 xUnit/MTP/Coverlet。不得引用 `Microsoft.Extensions.Logging` 或 OpenTelemetry。

- [ ] **Step 4: 实现 exact lifecycle 和 Port。** 状态只允许 `Created -> Configured -> Running -> Flushing -> Closed`、`Running -> Degraded -> Running`、任一 active state -> `Faulted`；所有 Port 签名与 §6.1 一致。

- [ ] **Step 5: 实现 `ObservabilityModule`。** 构造时接收 Port implementations，返回不可变 `ObservabilityServices`；services 不暴露 queue/channel/provider concrete type。

- [ ] **Step 6: 运行测试与 public API grep。**

```bash
dotnet test modules/observability/tests/Lumio.GameRuntime.Observability.Tests/Lumio.GameRuntime.Observability.Tests.csproj
grep -R "Microsoft.Extensions.Logging\|OpenTelemetry\|Channel<" modules/observability/src/Lumio.GameRuntime.Observability --include='*.cs'
```

Expected: tests `Failed: 0`；grep 无输出。

- [ ] **Step 7: 提交。**

```bash
git add modules/observability
git commit -m "feat(observability): add runtime ports and correlation sequence"
```

### Task 5: `obs-foundation-routing-and-failure` — 实现有界 Diagnostic、durable 路由和 Failure Bundle

**对应设计卡：** `obs-bounded-diagnostic-routing`、`obs-durable-route-and-emergency-path`、`obs-failure-bundle-assembly`

**Files:**
- Create: `modules/observability/src/Lumio.GameRuntime.Observability/Queues/DiagnosticEventQueue.cs`
- Create: `modules/observability/src/Lumio.GameRuntime.Observability/Queues/DiagnosticQueueBudget.cs`
- Create: `modules/observability/src/Lumio.GameRuntime.Observability/Routing/EventRouter.cs`
- Create: `modules/observability/src/Lumio.GameRuntime.Observability/Contracts/IDurableEvidencePort.cs`
- Create: `modules/observability/src/Lumio.GameRuntime.Observability/Routing/DurableEvidenceRouter.cs`
- Create: `modules/observability/src/Lumio.GameRuntime.Observability/Failure/FailureBundleAssembler.cs`
- Create: `modules/observability/src/Lumio.GameRuntime.Observability/Failure/FailureContextSnapshot.cs`
- Create: `modules/observability/src/Lumio.GameRuntime.Observability/Errors/ObservabilityFailure.cs`
- Create: `modules/observability/tests/Lumio.GameRuntime.Observability.Tests/DiagnosticBackpressureTests.cs`
- Create: `modules/observability/tests/Lumio.GameRuntime.Observability.Tests/DurableRouteFailureTests.cs`
- Create: `modules/observability/tests/Lumio.GameRuntime.Observability.Tests/FailureBundleGoldenTests.cs`

**Interfaces:**

- **Consumes:** Task 4 Port；generated Logging/Failure/Journal records；`DiagnosticQueueCapacity`、`DiagnosticQueueBytes`、`DurableLogQueueCapacity`。
- **Produces:** 有界 Diagnostic full action、不可静默丢的 durable route、snapshot/no-snapshot 合法 Failure Bundle。

**明确不做：** 不把 LoggingEvent 当 Journal，不实现 disk/fsync，不让 Diagnostic queue 满导致无界阻塞 Owner Thread。

- [ ] **Step 1: 写 Diagnostic 满载测试。** 容量 2，写入三条 BestEffort event；前两条 `Accepted`，第三条 `DroppedBestEffort`，depth 仍为 2，metric `runtime.diagnostic.dropped_total` 增 1。

```csharp
[Fact]
public void Best_effort_event_is_dropped_with_metric_when_queue_is_full()
{
    var queue = DiagnosticEventQueue.Create(new DiagnosticQueueBudget(2, 4096));
    Assert.Equal(DiagnosticWriteStatus.Accepted, queue.TryWrite(Fixtures.Event(1)).Status);
    Assert.Equal(DiagnosticWriteStatus.Accepted, queue.TryWrite(Fixtures.Event(2)).Status);
    Assert.Equal(DiagnosticWriteStatus.DroppedBestEffort, queue.TryWrite(Fixtures.Event(3)).Status);
    Assert.Equal(2, queue.Count);
    Assert.Equal(1, queue.DroppedCount);
}
```

- [ ] **Step 2: 写 durable 满载测试。** 可靠 record 的队列满必须返回 `Backpressured`，record 仍可用同一 idempotency key 重试；不得返回 `DroppedBestEffort`。

- [ ] **Step 3: 写 Failure Bundle 双路径 Golden。** 一例有 `SnapshotId` 且 `NoSnapshotReason=null`；一例无 Snapshot 且 `NoSnapshotReason="BeforeFirstValidSnapshot"`、`BootstrapPhase` 和 last revision 均存在。两者都经 generated validator 通过；两者同时存在或同时缺失必须失败。

- [ ] **Step 4: 运行失败测试。**

```bash
dotnet test modules/observability/tests/Lumio.GameRuntime.Observability.Tests/Lumio.GameRuntime.Observability.Tests.csproj --filter "DiagnosticBackpressureTests|DurableRouteFailureTests|FailureBundleGoldenTests"
```

Expected: `CS0246` 或测试失败，指出 queue/router/assembler 尚未存在。

- [ ] **Step 5: 用 BCL bounded Channel 实现内部队列。** 只在 internal `DiagnosticEventQueue` 封装 `Channel<LoggingEventView>`；full action 由 event durability 分类决定，Channel 类型不得出 public Port。

```csharp
internal DiagnosticWriteResult TryWriteBestEffort(in LoggingEventView value)
{
    if (_closed) return new(DiagnosticWriteStatus.Closed, GeneratedErrors.ObservabilityClosed);
    if (_writer.TryWrite(value)) return new(DiagnosticWriteStatus.Accepted, null);
    Interlocked.Increment(ref _droppedCount);
    _metrics.Increment("runtime.diagnostic.dropped_total");
    return new(DiagnosticWriteStatus.DroppedBestEffort, null);
}
```

- [ ] **Step 6: 实现 durable router。** 对 Txn/Command/WAL 三个 generated overload 保持 producer order 与 idempotency key；Port 返回 `Backpressured` 时不得改写为 success，不得转入 Diagnostic queue。

- [ ] **Step 7: 实现 Failure Bundle assembler。** 使用 generated builder/validator；校验 snapshot/noSnapshotReason XOR，不新增字段；写 bundle 失败升级为 `ObservabilityFailure.FatalEvidenceWrite`。

- [ ] **Step 8: 运行全部 Observability 测试。**

```bash
dotnet test modules/observability/tests/Lumio.GameRuntime.Observability.Tests/Lumio.GameRuntime.Observability.Tests.csproj
```

Expected: `Failed: 0`；full queue tests 不挂起，durable record count 与输入一致。

- [ ] **Step 9: 提交。**

```bash
git add modules/observability
git commit -m "feat(observability): add bounded and durable evidence routing"
```

### Task 6: `cfg-validation-and-six-layer-merge` — 验证 generated 配置并执行固定六层合并

**对应设计卡：** `cfg-generated-table-validation`、`cfg-six-layer-merge`

**Files:**
- Create: `modules/config/src/Lumio.GameRuntime.Config/Lumio.GameRuntime.Config.csproj`
- Create: `modules/config/tests/Lumio.GameRuntime.Config.Tests/Lumio.GameRuntime.Config.Tests.csproj`
- Create: `modules/config/src/Lumio.GameRuntime.Config/Contracts/IGeneratedConfigArtifactPort.cs`
- Create: `modules/config/src/Lumio.GameRuntime.Config/Validation/GeneratedConfigValidator.cs`
- Create: `modules/config/src/Lumio.GameRuntime.Config/Validation/ConfigValidationReport.cs`
- Create: `modules/config/src/Lumio.GameRuntime.Config/Merge/ConfigLayer.cs`
- Create: `modules/config/src/Lumio.GameRuntime.Config/Merge/ConfigLayerMerger.cs`
- Create: `modules/config/tests/Lumio.GameRuntime.Config.Tests/GeneratedArtifactValidationTests.cs`
- Create: `modules/config/tests/Lumio.GameRuntime.Config.Tests/SixLayerMergeGoldenTests.cs`

**Interfaces:**

- **Consumes:** generated Config artifacts/validator；Task 4 Observability Port。
- **Produces:** 无 compile API 的 artifact validator、exact six-layer precedence 与 deterministic merged table sequence。

**明确不做：** 不解析人类源配置，不监听文件，不发明 defaults/import/reference 规则，不允许调用方重排层级。

- [ ] **Step 1: 写 public-surface 失败测试。** 反射扫描 `Lumio.GameRuntime.Config`，任何 public method 名含 `Compile`、参数含 source text/path/stream 时失败。

```csharp
[Fact]
public void Runtime_config_surface_has_no_compile_api()
{
    var offenders = typeof(ConfigSnapshot).Assembly.ExportedTypes
        .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
        .Where(method => method.Name.Contains("Compile", StringComparison.OrdinalIgnoreCase))
        .Select(method => $"{method.DeclaringType!.FullName}.{method.Name}")
        .ToArray();
    Assert.Empty(offenders);
}
```

- [ ] **Step 2: 写 six-layer Golden。** 同一 key/column 在六层分别给值 `0..5`，最终值必须为 `5`；删除 `UserOrSession` 后为 `4`。同一 logical input 用不同 artifact enumeration order 仍产生相同 canonical bytes。

- [ ] **Step 3: 运行失败测试。**

```bash
dotnet test modules/config/tests/Lumio.GameRuntime.Config.Tests/Lumio.GameRuntime.Config.Tests.csproj
```

Expected: `MSB1009`。

- [ ] **Step 4: 创建项目与 validator。** 生产项目引用 GeneratedContracts 和 Observability；`GeneratedConfigValidator.Validate` 调用 generated schema/fixture validator并返回所有 issues，不能捕获后按 valid 处理。

- [ ] **Step 5: 实现不可变的层枚举。** `ConfigLayer` 数值顺序精确为 Engine/Platform/Server/Product/Environment/UserOrSession，merger 内使用固定数组，不接收自定义 precedence comparer。

```csharp
private static readonly ConfigLayer[] Precedence =
{
    ConfigLayer.Engine,
    ConfigLayer.Platform,
    ConfigLayer.Server,
    ConfigLayer.Product,
    ConfigLayer.Environment,
    ConfigLayer.UserOrSession
};
```

- [ ] **Step 6: 实现 merge。** 对每层先按 generated table/key/column canonical order 读取，后层覆盖前层；unknown table/column/type/range/ref 由 generated validator 拒绝，merger 不自行宽容转换。

- [ ] **Step 7: 运行测试。**

```bash
dotnet test modules/config/tests/Lumio.GameRuntime.Config.Tests/Lumio.GameRuntime.Config.Tests.csproj --filter "GeneratedArtifactValidationTests|SixLayerMergeGoldenTests"
```

Expected: `Failed: 0`；Golden bytes/hash固定。

- [ ] **Step 8: 提交。**

```bash
git add modules/config
git commit -m "feat(config): validate and merge generated config layers"
```

### Task 7: `cfg-snapshot-and-tick-activation` — 提供不可变 ConfigSnapshot 与 Barrier 激活

**对应设计卡：** `cfg-immutable-snapshot-reader`、`cfg-tick-boundary-activation`

**Files:**
- Create: `modules/config/src/Lumio.GameRuntime.Config/Snapshot/ConfigSnapshot.cs`
- Create: `modules/config/src/Lumio.GameRuntime.Config/Snapshot/ConfigSnapshotLease.cs`
- Create: `modules/config/src/Lumio.GameRuntime.Config/Snapshot/ConfigTableReader.cs`
- Create: `modules/config/src/Lumio.GameRuntime.Config/Contracts/IConfigSnapshotView.cs`
- Create: `modules/config/src/Lumio.GameRuntime.Config/ConfigModule.cs`
- Create: `modules/config/src/Lumio.GameRuntime.Config/ConfigServices.cs`
- Create: `modules/config/src/Lumio.GameRuntime.Config/Activation/ConfigActivationSlot.cs`
- Create: `modules/config/src/Lumio.GameRuntime.Config/Activation/ConfigActivator.cs`
- Create: `modules/config/tests/Lumio.GameRuntime.Config.Tests/SnapshotReaderPropertyTests.cs`
- Create: `modules/config/tests/Lumio.GameRuntime.Config.Tests/TickActivationTests.cs`

**Interfaces:**

- **Consumes:** Task 6 validated merged tables；Tick Barrier/TickId。
- **Produces:** 只读 typed reader、lease、Staged/Active/Superseded transition、Tick 内 snapshot identity stable。

**明确不做：** 不在 Tick 中途切换，不返回 mutable dictionary/array，不使 Dev path 绕过 validation/staging。

- [ ] **Step 1: 写 immutable property test。** 构造 Snapshot 后修改输入数组，Snapshot 读取结果不得变化；并发读取相同 key 10,000 次均返回相同 generated value/hash。

- [ ] **Step 2: 写 Tick 激活测试。** Tick N 开始前 Active=A；在 N 执行中 Stage(B)，所有 N read 仍为 A；Barrier 激活后 Tick N+1 全部为 B，A 进入 Superseded。无 staged snapshot 激活返回明确 Rejected，不改变 A。

```csharp
[Fact]
public void Staged_snapshot_becomes_visible_only_at_tick_barrier()
{
    var slot = Fixtures.ActiveSlot(snapshotId: 1UL);
    ConfigSnapshotLease tickN = slot.AcquireForTick(TickId.FromUInt64(10UL));
    slot.Stage(Fixtures.Snapshot(snapshotId: 2UL));
    Assert.Equal(1UL, tickN.Snapshot.SnapshotId.Value);
    Assert.True(slot.ActivateAtBarrier(TickId.FromUInt64(10UL)).Activated);
    using ConfigSnapshotLease tickNext = slot.AcquireForTick(TickId.FromUInt64(11UL));
    Assert.Equal(2UL, tickNext.Snapshot.SnapshotId.Value);
}
```

- [ ] **Step 3: 运行失败测试。**

```bash
dotnet test modules/config/tests/Lumio.GameRuntime.Config.Tests/Lumio.GameRuntime.Config.Tests.csproj --filter "SnapshotReaderPropertyTests|TickActivationTests"
```

Expected: 缺 `ConfigSnapshot`/`ConfigActivationSlot` 类型。

- [ ] **Step 4: 实现 copy-on-construct Snapshot 与 lease。** 对 table view 的 owner/byte storage 建立不可变 lifetime；lease Dispose 后读取返回/抛出明确 disposed contract，不允许 use-after-return。

- [ ] **Step 5: 实现 `ConfigActivationSlot`。** 只有 Simulation Owner Thread 在 Barrier 调用 `ActivateAtBarrier`；`AcquireForTick` 固定 SnapshotId 到 lease，Active 替换不影响旧 lease。

- [ ] **Step 6: 实现 Module/Services。** `ConfigServices` 只暴露 validator、stage/activation 和 `IConfigSnapshotView`；没有 source compiler、file watcher、DI container 类型。

- [ ] **Step 7: 运行全模块测试。**

```bash
dotnet test modules/config/tests/Lumio.GameRuntime.Config.Tests/Lumio.GameRuntime.Config.Tests.csproj
```

Expected: `Failed: 0`，property runs 无 data race/变化。

- [ ] **Step 8: 提交。**

```bash
git add modules/config
git commit -m "feat(config): add immutable snapshots and barrier activation"
```

### Task 8: `ecs-identity-and-storage-adapter` — 建立 World、LocalEntityId/Generation 与 storage Adapter

**对应设计卡：** `ecs-world-and-entity-identity`、`ecs-storage-adapter-contract`

**Files:**
- Create: `modules/ecs/src/Lumio.GameRuntime.Ecs/Lumio.GameRuntime.Ecs.csproj`
- Create: `modules/ecs/tests/Lumio.GameRuntime.Ecs.Tests/Lumio.GameRuntime.Ecs.Tests.csproj`
- Create: `modules/ecs/src/Lumio.GameRuntime.Ecs/EcsModule.cs`
- Create: `modules/ecs/src/Lumio.GameRuntime.Ecs/EcsServices.cs`
- Create: `modules/ecs/src/Lumio.GameRuntime.Ecs/World/EcsWorld.cs`
- Create: `modules/ecs/src/Lumio.GameRuntime.Ecs/World/EcsWorldState.cs`
- Create: `modules/ecs/src/Lumio.GameRuntime.Ecs/Entity/LocalEntityId.cs`
- Create: `modules/ecs/src/Lumio.GameRuntime.Ecs/Entity/EntitySlotTable.cs`
- Create: `modules/ecs/src/Lumio.GameRuntime.Ecs/Storage/IWorldStorageAdapter.cs`
- Create: `modules/ecs/src/Lumio.GameRuntime.Ecs/Storage/ComponentTypeRegistry.cs`
- Create: `modules/ecs/src/Lumio.GameRuntime.Ecs/Storage/ReferenceWorldStorageAdapter.cs`
- Create: `modules/ecs/tests/Lumio.GameRuntime.Ecs.Tests/EntityGenerationPropertyTests.cs`
- Create: `modules/ecs/tests/Lumio.GameRuntime.Ecs.Tests/WorldIsolationTests.cs`
- Create: `modules/ecs/tests/Lumio.GameRuntime.Ecs.Tests/StorageAdapterConformanceTests.cs`

**Interfaces:**

- **Consumes:** Task 3 generated component metadata；Task 7 ConfigSnapshot；Task 4 Observability。
- **Produces:** World-local ID/Generation、slot table、Adapter-neutral storage Port、test/reference storage conformance。

**明确不做：** 不实现通用 ECS 引擎，不暴露 Friflo 类型，不允许跨 World 解析 LocalEntityId。

- [ ] **Step 1: 写 Generation property。** 任意 create/destroy/create 序列后，同 Index 的新 ID Generation 必须递增，旧 ID resolve 必须为 stale；generation overflow 必须 Fatal，不能回到 0。

```csharp
[Fact]
public void Retired_slot_never_resolves_previous_generation()
{
    var slots = new EntitySlotTable(maxSlots: 4);
    LocalEntityId first = slots.Allocate();
    Assert.True(slots.Retire(first));
    LocalEntityId second = slots.Allocate();
    Assert.Equal(first.Index, second.Index);
    Assert.NotEqual(first.Generation, second.Generation);
    Assert.False(slots.TryResolve(first, out _));
    Assert.True(slots.TryResolve(second, out _));
}
```

- [ ] **Step 2: 写双 World 隔离测试。** 两个 World 分配同 `(Index, Generation)`，将 A 的 ID 交给 B 的 view 必须因 World context/epoch 拒绝，而不是命中 B 的实体。

- [ ] **Step 3: 写 Adapter conformance。** Register/Create/Read/Write/Query/Destroy/Snapshot/Integrity 的结果分类一致；create/destroy 只由 commit-facing internal API 调用。

- [ ] **Step 4: 运行失败测试。**

```bash
dotnet test modules/ecs/tests/Lumio.GameRuntime.Ecs.Tests/Lumio.GameRuntime.Ecs.Tests.csproj
```

Expected: `MSB1009`。

- [ ] **Step 5: 创建项目与 ID/slot table。** 生产项目引用 GeneratedContracts、Config、Observability；`LocalEntityId` 只含 `Index + Generation`，不含 object reference、World pointer 或 NetEntityId。

- [ ] **Step 6: 实现 `IWorldStorageAdapter` exact 方法。** 使用 §6.3 签名；reference implementation 只服务测试和早期 Foundation，不进入 stable surface。Friflo Adapter 本任务只创建独立未来项目位置，不在核心项目引用包。

- [ ] **Step 7: 实现 `EcsWorld` 构造与 lifecycle 前半。** Created/Registering/Ready/Running；结构 create/destroy methods internal，并检查 Owner Thread token 与 WorldId context。

- [ ] **Step 8: 运行测试。**

```bash
dotnet test modules/ecs/tests/Lumio.GameRuntime.Ecs.Tests/Lumio.GameRuntime.Ecs.Tests.csproj --filter "EntityGenerationPropertyTests|WorldIsolationTests|StorageAdapterConformanceTests"
```

Expected: `Failed: 0`；property test seed 输出在失败消息中。

- [ ] **Step 9: 提交。**

```bash
git add modules/ecs
git commit -m "feat(ecs): add world identity and storage adapter contract"
```

### Task 9: `ecs-query-views-and-changes` — 实现 Query、Read/Write View、ChangeSet 与 Snapshot View

**对应设计卡：** `ecs-query-read-write-views`、`ecs-change-set-and-snapshot-view`

**Files:**
- Create: `modules/ecs/src/Lumio.GameRuntime.Ecs/Query/QuerySpec.cs`
- Create: `modules/ecs/src/Lumio.GameRuntime.Ecs/Query/QueryPlan.cs`
- Create: `modules/ecs/src/Lumio.GameRuntime.Ecs/Query/QueryBatch.cs`
- Create: `modules/ecs/src/Lumio.GameRuntime.Ecs/Query/EcsReadView.cs`
- Create: `modules/ecs/src/Lumio.GameRuntime.Ecs/Query/EcsWriteView.cs`
- Create: `modules/ecs/src/Lumio.GameRuntime.Ecs/ChangeTracking/ChangeSet.cs`
- Create: `modules/ecs/src/Lumio.GameRuntime.Ecs/ChangeTracking/ChangeSetBuilder.cs`
- Create: `modules/ecs/src/Lumio.GameRuntime.Ecs/Snapshot/IEcsSnapshotProvider.cs`
- Create: `modules/ecs/src/Lumio.GameRuntime.Ecs/Snapshot/EcsWorldReadSnapshot.cs`
- Create: `modules/ecs/tests/Lumio.GameRuntime.Ecs.Tests/QueryViewBoundaryTests.cs`
- Create: `modules/ecs/tests/Lumio.GameRuntime.Ecs.Tests/ChangeSetGoldenTests.cs`

**Interfaces:**

- **Consumes:** Task 8 EcsWorld/storage；generated Processor read/write metadata；SnapshotCut view。
- **Produces:** canonical QuerySpec、epoch-bound views、existing-field write path、stable ChangeSet、immutable snapshot lease。

**明确不做：** 不允许 view 跨 await/heap/Tick，不直接做 structural mutation，不按 Dictionary 或 storage address 决定顺序。

- [ ] **Step 1: 写 Query boundary tests。** unknown component/field、ReadSet 外读、WriteSet 外写、stale epoch、跨 Tick view、query budget 超限全部在写前 Rejected。

- [ ] **Step 2: 写 ChangeSet Golden。** 同一三条 field change 以六种输入顺序构造，`Entries` canonical order 和 canonical bytes/hash 完全相同。

```csharp
[Theory]
[MemberData(nameof(Permutations))]
public void Change_set_is_canonical_for_all_insertion_orders(ChangeEntry[] changes)
{
    var set = new ChangeSet(Fixtures.WorldId, Fixtures.Tick(7), changes);
    Assert.Equal(Fixtures.ExpectedChangeOrder, set.Entries.ToArray());
    Assert.Equal(Fixtures.ExpectedChangeHash, CanonicalHash.Of(set));
}
```

- [ ] **Step 3: 运行失败测试。**

```bash
dotnet test modules/ecs/tests/Lumio.GameRuntime.Ecs.Tests/Lumio.GameRuntime.Ecs.Tests.csproj --filter "QueryViewBoundaryTests|ChangeSetGoldenTests"
```

Expected: 缺 Query/View/ChangeSet 类型或断言失败。

- [ ] **Step 4: 实现 QuerySpec canonicalization。** Required/Excluded/ReadSet/WriteSet 在构造时复制、去重、按 generated ID ordinal 排序；Required 与 Excluded 冲突直接 Rejected。

- [ ] **Step 5: 实现 `EcsReadView`/`EcsWriteView`。** view 绑定 WorldId、TickId、epoch、declared sets；Write 只调 `WriteExistingField` 并在成功后立即 append ChangeSetBuilder。结构操作没有 public entry。

- [ ] **Step 6: 实现 ChangeSet 与 Snapshot lease。** ChangeSet 发布后只读；snapshot capture pin adapter snapshot handle，Dispose 幂等 release，Disposed 后读取返回明确 stale lease error。

- [ ] **Step 7: 运行测试与 allocation smoke。**

```bash
dotnet test modules/ecs/tests/Lumio.GameRuntime.Ecs.Tests/Lumio.GameRuntime.Ecs.Tests.csproj --filter "QueryViewBoundaryTests|ChangeSetGoldenTests"
```

Expected: `Failed: 0`；同一 fixture 的 hash 稳定。

- [ ] **Step 8: 提交。**

```bash
git add modules/ecs
git commit -m "feat(ecs): add bounded views and canonical change sets"
```

### Task 10: `ecs-lifecycle-owner-thread-and-fail-stop` — 锁定 Owner Thread、Disposed/Faulted 与字段写 Fail-stop

**对应设计卡：** `ecs-world-lifecycle-fail-stop`

**Files:**
- Create: `modules/ecs/src/Lumio.GameRuntime.Ecs/World/OwnerThreadGuard.cs`
- Create: `modules/ecs/src/Lumio.GameRuntime.Ecs/Errors/EcsFailure.cs`
- Create: `modules/ecs/tests/Lumio.GameRuntime.Ecs.Tests/FailStopWriteTests.cs`
- Create: `modules/ecs/tests/Lumio.GameRuntime.Ecs.Tests/WorldLifecycleTests.cs`

**Interfaces:**

- **Consumes:** Task 8–9 EcsWorld、views、ChangeSet；Observability fatal Port。
- **Produces:** Owner-thread write guard、exact lifecycle、post-write failure to Faulted、evidence capture、Disposed rejection。

**明确不做：** 不实现字段级 Undo，不在 fatal 后继续 Processor/Tick，不把异常文本当稳定错误码。

- [ ] **Step 1: 写 lifecycle table test。** 只接受 `Created -> Registering -> Ready -> Running -> Draining -> Disposed`；active state 可进 Faulted；Faulted 只允许 evidence capture/Dispose；Disposed 全部 operation 拒绝。

- [ ] **Step 2: 写 post-write failure test。** fake storage 完成 field bytes 写入后在 ChangeSet append 抛出受控异常；EcsWorld 必须进入 Faulted、返回 Fatal、记录 Tick/Processor/entity/field/evidence hash，且不尝试写回 before bytes。

```csharp
[Fact]
public void Failure_after_existing_field_write_faults_world_without_field_rollback()
{
    var world = Fixtures.WorldWithPostWriteFailure();
    StorageOperationResult result = world.WriteExistingFieldForTest(Fixtures.ValidWrite);
    Assert.Equal(StorageOperationStatus.Fatal, result.Status);
    Assert.Equal(EcsWorldState.Faulted, world.State);
    Assert.Equal(Fixtures.AfterBytes, world.StorageBytes(Fixtures.ValidWrite));
    Assert.Equal(0, world.StorageUndoCalls);
}
```

- [ ] **Step 3: 写 owner-thread violation test。** 在另一个 managed thread 调用 write，必须在触碰 storage 前 Fatal/Faulted；read-only immutable snapshot 可在 worker 读。

- [ ] **Step 4: 运行失败测试。**

```bash
dotnet test modules/ecs/tests/Lumio.GameRuntime.Ecs.Tests/Lumio.GameRuntime.Ecs.Tests.csproj --filter "FailStopWriteTests|WorldLifecycleTests"
```

Expected: 断言失败或缺 guard/failure 类型。

- [ ] **Step 5: 实现 `OwnerThreadGuard`。** 在 World Start 捕获 owner thread token；每个权威写入口先验证。测试注入 token provider，不读取 wall clock。

- [ ] **Step 6: 实现 Fail-stop controller。** 捕获 Adapter exception 后转成 generated Fatal error identity，World 单向进入 Faulted，发 durable failure evidence；不得 catch-and-continue。

- [ ] **Step 7: 运行全 ECS 测试。**

```bash
dotnet test modules/ecs/tests/Lumio.GameRuntime.Ecs.Tests/Lumio.GameRuntime.Ecs.Tests.csproj
```

Expected: `Failed: 0`，fatal tests 没有 undo call。

- [ ] **Step 8: 提交。**

```bash
git add modules/ecs
git commit -m "feat(ecs): enforce owner thread and fail-stop writes"
```

### Task 11: `cmd-buffer-deferred-and-stable-merge` — 实现每 Processor Buffer、Deferred Token 与稳定合并

**对应设计卡：** `cmd-buffer-and-deferred-token`、`cmd-seal-and-stable-merge`

**Files:**
- Create: `modules/command/src/Lumio.GameRuntime.Command/Lumio.GameRuntime.Command.csproj`
- Create: `modules/command/tests/Lumio.GameRuntime.Command.Tests/Lumio.GameRuntime.Command.Tests.csproj`
- Create: `modules/command/src/Lumio.GameRuntime.Command/CommandModule.cs`
- Create: `modules/command/src/Lumio.GameRuntime.Command/CommandServices.cs`
- Create: `modules/command/src/Lumio.GameRuntime.Command/Lifecycle/CommandBufferState.cs`
- Create: `modules/command/src/Lumio.GameRuntime.Command/Buffers/ProcessorCommandBuffer.cs`
- Create: `modules/command/src/Lumio.GameRuntime.Command/Buffers/CommandBufferWriter.cs`
- Create: `modules/command/src/Lumio.GameRuntime.Command/Buffers/SealedCommandBuffer.cs`
- Create: `modules/command/src/Lumio.GameRuntime.Command/Tokens/DeferredEntityToken.cs`
- Create: `modules/command/src/Lumio.GameRuntime.Command/Commands/CommandSortKey.cs`
- Create: `modules/command/src/Lumio.GameRuntime.Command/Merge/CommandBufferMerger.cs`
- Create: `modules/command/src/Lumio.GameRuntime.Command/Merge/MergedCommandBatch.cs`
- Create: `modules/command/tests/Lumio.GameRuntime.Command.Tests/BufferStateMachineTests.cs`
- Create: `modules/command/tests/Lumio.GameRuntime.Command.Tests/DeferredTokenGoldenTests.cs`
- Create: `modules/command/tests/Lumio.GameRuntime.Command.Tests/StableMergePropertyTests.cs`

**Interfaces:**

- **Consumes:** Task 8–10 ECS types；generated Tick/Phase/Processor IDs；Observability。
- **Produces:** 每 Processor 独立 Buffer、`Open -> Sealed`、Deferred token、`Phase + ProcessorId + LocalSequence` total order 和 Merged batch。

**明确不做：** 不直接改 ECS 结构，不共享一个全局 Buffer，不在 worker completion order 上排序。

- [ ] **Step 1: 写 exact state test。** Open 可 append/Seal；Seal 之后任何 append/二次 Seal 明确拒绝；Merged/Prepared/Applied 只由后续拥有者推进，不能由 Gameplay writer 设置。

- [ ] **Step 2: 写 Deferred Token Golden。** Token canonical fields 精确为 TickId、ProcessorId、LocalSequence；两个 Processor 的相同 LocalSequence 不冲突；旧 Tick token 不得在新 Tick resolve。

- [ ] **Step 3: 写 stable merge property。** 随机打乱 2–16 个 sealed buffers 的输入顺序，逻辑相同 commands 的输出始终按 Phase、ProcessorId、LocalSequence 升序且 canonical hash 相同。

```csharp
[Fact]
public void Merge_order_does_not_depend_on_buffer_arrival_order()
{
    SealedCommandBuffer[] left = Fixtures.SealedBuffers(order: new[] { 2, 0, 1 });
    SealedCommandBuffer[] right = Fixtures.SealedBuffers(order: new[] { 1, 2, 0 });
    var merger = new CommandBufferMerger();
    MergedCommandBatch a = merger.Merge(Fixtures.TickId, left);
    MergedCommandBatch b = merger.Merge(Fixtures.TickId, right);
    Assert.Equal(a.Commands.ToArray(), b.Commands.ToArray());
    Assert.Equal(CanonicalHash.Of(a), CanonicalHash.Of(b));
}
```

- [ ] **Step 4: 运行失败测试。**

```bash
dotnet test modules/command/tests/Lumio.GameRuntime.Command.Tests/Lumio.GameRuntime.Command.Tests.csproj
```

Expected: `MSB1009`。

- [ ] **Step 5: 创建项目和 command types。** 生产项目引用 ECS、Observability、GeneratedContracts；`ProcessorCommandBuffer` 只在一个 Processor invocation 中使用，不线程安全；`CommandSortKey.CompareTo` 实现 §6.4 的三段比较。

- [ ] **Step 6: 实现 merge。** 先验证所有 buffer TickId 一致、每个 ProcessorId 至多一个 sealed buffer、每个 buffer 内 LocalSequence 严格递增；验证失败在 Merged 前 Rejected。

- [ ] **Step 7: 运行测试。**

```bash
dotnet test modules/command/tests/Lumio.GameRuntime.Command.Tests/Lumio.GameRuntime.Command.Tests.csproj --filter "BufferStateMachineTests|DeferredTokenGoldenTests|StableMergePropertyTests"
```

Expected: `Failed: 0`，随机 seed 出现在 test output metadata。

- [ ] **Step 8: 提交。**

```bash
git add modules/command
git commit -m "feat(command): add processor buffers and stable merge"
```

### Task 12: `cmd-preflight-prepared-and-apply` — 前置全部业务拒绝并实现不可再拒绝的 Apply

**对应设计卡：** `cmd-preflight-and-prepared-delta`、`cmd-apply-to-ecs`

**Files:**
- Create: `modules/command/src/Lumio.GameRuntime.Command/Prepare/CommandPreflightValidator.cs`
- Create: `modules/command/src/Lumio.GameRuntime.Command/Prepare/CommandReservationSet.cs`
- Create: `modules/command/src/Lumio.GameRuntime.Command/Prepare/PreparedGameDelta.cs`
- Create: `modules/command/src/Lumio.GameRuntime.Command/Apply/EcsCommandCommitExecutor.cs`
- Create: `modules/command/src/Lumio.GameRuntime.Command/Apply/CommandApplyReceipt.cs`
- Create: `modules/command/src/Lumio.GameRuntime.Command/Tokens/DeferredEntityMap.cs`
- Create: `modules/command/tests/Lumio.GameRuntime.Command.Tests/PreparedBoundaryTests.cs`
- Create: `modules/command/tests/Lumio.GameRuntime.Command.Tests/EcsApplyFaultTests.cs`

**Interfaces:**

- **Consumes:** Task 11 Merged batch；Task 8–10 ECS capacity/schema/ID/write/structure commit 面；generated permissions/errors。
- **Produces:** Preflight validation/reservation、immutable `PreparedGameDelta`、Deferred resolution、Applied/AlreadyApplied/InfrastructureFault only。

**明确不做：** 不在 Apply 重新做业务规则，不在 Voxel 已提交后返回业务 Rejected，不做字段级撤销。

- [ ] **Step 1: 写 Prepared boundary matrix。** unknown type/field、stale entity、invalid Deferred target、create/write/destroy conflict、permission、budget、entity slot、change bytes 全部在 Prepare 返回 Rejected，ECS mutation count 为 0。

- [ ] **Step 2: 写 post-Prepared contract test。** 对同一 PreparedGameDelta 首次 Apply=`Applied`，重复 Apply=`AlreadyApplied` 且同 receipt/hash；fake ECS structural infrastructure fault 只能返回 `InfrastructureFault` 并使 World Faulted，不能返回 Rejected。

```csharp
[Fact]
public void Prepared_delta_has_no_business_rejection_path_during_apply()
{
    PreparedGameDelta prepared = Fixtures.ValidPreparedDelta();
    CommandApplyReceipt first = Fixtures.Executor.Apply(prepared);
    CommandApplyReceipt duplicate = Fixtures.Executor.Apply(prepared);
    Assert.Equal(CommandApplyStatus.Applied, first.Status);
    Assert.Equal(CommandApplyStatus.AlreadyApplied, duplicate.Status);
    Assert.Equal(first.CanonicalDigest.ToArray(), duplicate.CanonicalDigest.ToArray());
    Assert.DoesNotContain(Enum.GetNames<CommandApplyStatus>(), name => name.Contains("Reject", StringComparison.Ordinal));
}
```

- [ ] **Step 3: 运行失败测试。**

```bash
dotnet test modules/command/tests/Lumio.GameRuntime.Command.Tests/Lumio.GameRuntime.Command.Tests.csproj --filter "PreparedBoundaryTests|EcsApplyFaultTests"
```

Expected: 缺 Prepare/Apply 类型或断言失败。

- [ ] **Step 4: 实现 `CommandPreflightValidator`.** 以 canonical command order 顺序验证全部 command，预留 entity slots/change entries/bytes，构造 Deferred resolution plan；任何一项失败释放全部 reservation 并返回无副作用 Rejected/Retryable/Fatal。

- [ ] **Step 5: 实现 `PreparedGameDelta`.** 对 batch、reservation、schema epoch、world/tick、canonical digest 做 defensive copy；构造函数 internal，只能由 validator 成功路径调用。

- [ ] **Step 6: 实现 Ecs commit executor。** 幂等索引键使用 TickId + canonical digest；按 stable order create/resolve/write/destroy；第一个 storage apply 后异常进入 Fail-stop 并返回 InfrastructureFault。

- [ ] **Step 7: 运行测试。**

```bash
dotnet test modules/command/tests/Lumio.GameRuntime.Command.Tests/Lumio.GameRuntime.Command.Tests.csproj --filter "PreparedBoundaryTests|EcsApplyFaultTests"
```

Expected: `Failed: 0`；业务拒绝 fixture 的 ECS/Voxel mutation count 均为 0。

- [ ] **Step 8: 提交。**

```bash
git add modules/command
git commit -m "feat(command): prepare and apply immutable game deltas"
```

### Task 13: `cmd-budget-durable-evidence-and-conflicts` — 实现 CommandBuffer 预算、durable record 路由与冲突证据

**对应设计卡：** `cmd-capacity-and-durable-record-route`、`cmd-conflict-golden-property`

**Files:**
- Create: `modules/command/src/Lumio.GameRuntime.Command/Budgets/CommandBufferBudget.cs`
- Create: `modules/command/src/Lumio.GameRuntime.Command/Evidence/ICommandEvidencePort.cs`
- Create: `modules/command/src/Lumio.GameRuntime.Command/Errors/CommandFailure.cs`
- Create: `modules/command/tests/Lumio.GameRuntime.Command.Tests/CommandBudgetTests.cs`
- Create: `modules/command/tests/Lumio.GameRuntime.Command.Tests/CommandConflictGoldenTests.cs`
- Create: `modules/command/tests/Lumio.GameRuntime.Command.Tests/CommandReplayPropertyTests.cs`

**Interfaces:**

- **Consumes:** Task 5 durable evidence Port；Task 11–12 commands/prepared/apply；`ProcessorDescriptor.Budget`、`CommandBufferMaxCommands`、`CommandBufferMaxBytes`。
- **Produces:** 有单位的 command budget、full action、generated CommandLog record、冲突/重放 Golden 与 Property。

**明确不做：** 不硬编码未经测量容量，不把 command record 写进 Diagnostic queue，不在 budget 超限后返回截断 batch。

- [ ] **Step 1: 写三个预算边界 test。** exact max commands/bytes accepted；加一 command/byte 在 append 前 Rejected；同一 Processor budget 与全局 budget 取更严格值。

- [ ] **Step 2: 写冲突 Golden。** same Tick create/write/destroy、duplicate destroy、write-after-destroy、Deferred target escape、两个 Processor 写同 field 的每个已冻结规则都输出 generated error identity 和 canonical conflict evidence。

- [ ] **Step 3: 写 replay property。** 同一 sealed buffers 重放 100 次，Prepared digest、CommandLog canonical bytes、Apply receipt 和 ChangeSet hash 相同。

- [ ] **Step 4: 运行失败测试。**

```bash
dotnet test modules/command/tests/Lumio.GameRuntime.Command.Tests/Lumio.GameRuntime.Command.Tests.csproj --filter "CommandBudgetTests|CommandConflictGoldenTests|CommandReplayPropertyTests"
```

Expected: 缺 budget/evidence/failure 类型或断言失败。

- [ ] **Step 5: 实现 budget。** 每次 append 前用 checked arithmetic 计算 commands/bytes；超限不改变 buffer；不从 pool/object address 推导 bytes。

- [ ] **Step 6: 实现 `ICommandEvidencePort`.** 接收 generated `CommandLogRecordView`，向 Observability `IDurableEvidencePort.Append(CommandLogRecordView)` 委托；Backpressured 传播为 Retryable，Fatal 传播至 Session，不转 Diagnostic。

- [ ] **Step 7: 运行全部 Command 测试。**

```bash
dotnet test modules/command/tests/Lumio.GameRuntime.Command.Tests/Lumio.GameRuntime.Command.Tests.csproj
```

Expected: `Failed: 0`；budget test 无 partial append，durable record count 等于 prepared/applied transition count。

- [ ] **Step 8: 提交。**

```bash
git add modules/command
git commit -m "feat(command): enforce budgets and durable command evidence"
```

### Task 14: `coord-revision-and-txn-state` — 建立 Revision Vector、Txn 状态与幂等索引

**对应设计卡：** `coord-revision-vector-view`、`coord-txn-state-and-idempotency`

**Files:**
- Create: `modules/coordination/src/Lumio.GameRuntime.Coordination/Lumio.GameRuntime.Coordination.csproj`
- Create: `modules/coordination/tests/Lumio.GameRuntime.Coordination.Tests/Lumio.GameRuntime.Coordination.Tests.csproj`
- Create: `modules/coordination/src/Lumio.GameRuntime.Coordination/CoordinationModule.cs`
- Create: `modules/coordination/src/Lumio.GameRuntime.Coordination/CoordinationServices.cs`
- Create: `modules/coordination/src/Lumio.GameRuntime.Coordination/Revision/SessionRevisionVectorStore.cs`
- Create: `modules/coordination/src/Lumio.GameRuntime.Coordination/Revision/SessionRevisionVectorView.cs`
- Create: `modules/coordination/src/Lumio.GameRuntime.Coordination/Lifecycle/CoordinatorState.cs`
- Create: `modules/coordination/src/Lumio.GameRuntime.Coordination/Transactions/CrossWorldCoordinator.cs`
- Create: `modules/coordination/src/Lumio.GameRuntime.Coordination/Transactions/CrossWorldTxnState.cs`
- Create: `modules/coordination/src/Lumio.GameRuntime.Coordination/Transactions/TxnParticipantState.cs`
- Create: `modules/coordination/src/Lumio.GameRuntime.Coordination/Transactions/TxnRecord.cs`
- Create: `modules/coordination/src/Lumio.GameRuntime.Coordination/Transactions/TxnIdempotencyIndex.cs`
- Create: `modules/coordination/tests/Lumio.GameRuntime.Coordination.Tests/RevisionVectorPropertyTests.cs`
- Create: `modules/coordination/tests/Lumio.GameRuntime.Coordination.Tests/TxnStateMachineGoldenTests.cs`
- Create: `modules/coordination/tests/Lumio.GameRuntime.Coordination.Tests/DuplicateLostResultTests.cs`

**Interfaces:**

- **Consumes:** Task 12 PreparedGameDelta/Apply；generated revision/txn/errors；Observability。
- **Produces:** 唯一 Revision store、exact Txn/participant enums、legal transition guard、duplicate/lost result idempotency index。

**明确不做：** 不实现 Journal 介质，不持有 Tick cursor，不用 Boolean participant marker，不把 `Prepared -> Indeterminate` 设为合法。

- [ ] **Step 1: 写 Revision property。** 任意合法 committed sequence 四域 revision 单调不降；任一域回退、SchemaEpoch 非法切换或未 committed advance 必须 Fatal/Rejected 且 store 保持原值。

- [ ] **Step 2: 写 Txn state Golden。** 精确允许 `Created -> Prepared -> CommitIntent -> Committed`、`Prepared -> Aborted/Expired`、`CommitIntent -> Indeterminate`；拒绝 `Prepared -> Indeterminate`、`CommitIntent -> Aborted`、terminal state 重新进入 active。

- [ ] **Step 3: 写 participant enum test。** generated journal round-trip 必须保留 `NotStarted/Unknown/Applied/Failed` 四值，反射/Schema test 禁止 bool 字段替代 participant state。

- [ ] **Step 4: 写 duplicate/lost result test。** 相同 TxnId+same request digest 返回原 result；相同 TxnId+different digest 返回 Fatal idempotency conflict；lost caller result 后 QueryResult 返回 journal/index 中的原状态。

- [ ] **Step 5: 运行失败测试。**

```bash
dotnet test modules/coordination/tests/Lumio.GameRuntime.Coordination.Tests/Lumio.GameRuntime.Coordination.Tests.csproj
```

Expected: `MSB1009`。

- [ ] **Step 6: 创建项目与 exact enums/store/index。** 生产项目引用 ECS、Command、Observability、GeneratedContracts；不引用 Persistence。

- [ ] **Step 7: 实现 guarded transition 和 canonical request digest。** digest 使用 Persistence canonical Port 在后续 Task 20 注入前，可先用 generated canonical record writer Port；不得使用 object hash code。

- [ ] **Step 8: 运行测试。**

```bash
dotnet test modules/coordination/tests/Lumio.GameRuntime.Coordination.Tests/Lumio.GameRuntime.Coordination.Tests.csproj --filter "RevisionVectorPropertyTests|TxnStateMachineGoldenTests|DuplicateLostResultTests"
```

Expected: `Failed: 0`；不存在 bool participant marker public/internal field。

- [ ] **Step 9: 提交。**

```bash
git add modules/coordination
git commit -m "feat(coordination): add revisions and transaction state model"
```

### Task 15: `coord-prepare-and-reservation` — 完成 CrossWorld Prepare、Voxel token 与 Reservation lease

**对应设计卡：** `coord-prepare-and-reservation`

**Files:**
- Create: `modules/coordination/src/Lumio.GameRuntime.Coordination.VoxelAdapters/Lumio.GameRuntime.Coordination.VoxelAdapters.csproj`
- Create: `modules/coordination/src/Lumio.GameRuntime.Coordination/Prepare/TxnPrepareCoordinator.cs`
- Create: `modules/coordination/src/Lumio.GameRuntime.Coordination/Prepare/PreparedVoxelTokenLease.cs`
- Create: `modules/coordination/src/Lumio.GameRuntime.Coordination/Reservations/ReservationLease.cs`
- Create: `modules/coordination/src/Lumio.GameRuntime.Coordination.VoxelAdapters/GeneratedVoxelWorldPortAdapter.cs`
- Create: `modules/coordination/tests/Lumio.GameRuntime.Coordination.Tests/PrepareNoSideEffectTests.cs`

**Interfaces:**

- **Consumes:** Task 14 Txn store；Task 12 PreparedGameDelta；Generated Voxel Authority Contract；expected Revision/permission/deadline/budget。
- **Produces:** 全部 preflight、game/voxel reservations、prepared token lease、Prepared state，无可见副作用。

**明确不做：** 不写 durable CommitIntent，不 commit Voxel/ECS，不暴露 Voxel internal storage/native pointer。

- [ ] **Step 1: 写 Prepare 无副作用 matrix。** expected revision mismatch、permission、deadline、game budget、Voxel capacity、unknown schema、queue full 每项返回 Rejected/Retryable/Fatal 分类；ECS/Voxel visible revision 和 mutation count 均不变。

```csharp
[Theory]
[MemberData(nameof(Fixtures.PrepareFailures))]
public void Prepare_failure_has_no_visible_side_effects(PrepareFailureFixture fixture)
{
    SessionRevisionVectorView before = fixture.Revisions.Read();
    TxnPrepareResult result = fixture.Coordinator.Prepare(fixture.Request);
    Assert.NotEqual(TxnPrepareStatus.Prepared, result.Status);
    Assert.Equal(before, fixture.Revisions.Read());
    Assert.Equal(0, fixture.Ecs.VisibleMutationCount);
    Assert.Equal(0, fixture.Voxel.VisibleMutationCount);
}
```

- [ ] **Step 2: 写 reservation lease test。** Prepared 返回 game + voxel reservation；Abort/Expired/Dispose 各只释放一次；Committed 后 release 不撤销已应用状态；stale lease 无法复用。

- [ ] **Step 3: 运行失败测试。**

```bash
dotnet test modules/coordination/tests/Lumio.GameRuntime.Coordination.Tests/Lumio.GameRuntime.Coordination.Tests.csproj --filter PrepareNoSideEffectTests
```

Expected: 缺 prepare/lease/adapter 类型。

- [ ] **Step 4: 实现 prepare order。** 按 expected revision → schema/permissions/deadline → PreparedGameDelta validity → reserve ECS/game → Voxel prepare → assemble CrossWorldPreparedTxn；任一步失败按反序释放已取得 reservation。

- [ ] **Step 5: 实现 generated Voxel adapter。** 只映射 generated request/token/result；Native completion 不直接改变 coordinator，必须由 Simulation NativeJobBarrier 提交结果。

- [ ] **Step 6: 实现 lease idempotency。** Dispose/Abort/Expire 使用原子 state，重复调用返回原 result，不抛出未分类异常。

- [ ] **Step 7: 运行测试。**

```bash
dotnet test modules/coordination/tests/Lumio.GameRuntime.Coordination.Tests/Lumio.GameRuntime.Coordination.Tests.csproj --filter PrepareNoSideEffectTests
```

Expected: `Failed: 0`；每个失败 fixture visible mutation 为 0，release count 精确。

- [ ] **Step 8: 提交。**

```bash
git add modules/coordination
git commit -m "feat(coordination): prepare cross-world reservations"
```

### Task 16: `coord-commit-intent-apply-and-recovery` — 先持久化 CommitIntent，再固定顺序 Apply 并解析崩溃窗口

**对应设计卡：** `coord-commit-intent-and-apply-order`、`coord-crash-resolution-and-journal-port`

**Files:**
- Create: `modules/coordination/src/Lumio.GameRuntime.Coordination/Journal/ITxnJournalPort.cs`
- Create: `modules/coordination/src/Lumio.GameRuntime.Coordination/Commit/CommitIntentCoordinator.cs`
- Create: `modules/coordination/src/Lumio.GameRuntime.Coordination/Commit/ParticipantApplyCoordinator.cs`
- Create: `modules/coordination/src/Lumio.GameRuntime.Coordination/Commit/ITxnParticipantQueryPort.cs`
- Create: `modules/coordination/src/Lumio.GameRuntime.Coordination/Recovery/TxnRecoveryResolver.cs`
- Create: `modules/coordination/src/Lumio.GameRuntime.Coordination/Errors/CoordinationFailure.cs`
- Create: `modules/coordination/tests/Lumio.GameRuntime.Coordination.Tests/CommitIntentOrderingTests.cs`
- Create: `modules/coordination/tests/Lumio.GameRuntime.Coordination.Tests/CrashBoundaryRecoveryTests.cs`

**Interfaces:**

- **Consumes:** Task 5 durable Port；Task 12 ECS Apply；Task 14–15 prepared Txn/Voxel Port；generated TxnJournal records。
- **Produces:** 调用方拥有的 Journal Port、durable intent gate、Voxel→ECS、四态 marker、Indeterminate/re-query/recovery algorithm。

**明确不做：** 不实现 persistence backend，不在 intent 后 abort/reject，不交换 Apply 顺序，不猜测 Unknown 为 Applied/NotStarted。

- [ ] **Step 1: 写 ordering trace test。** trace 必须精确为 `Journal.CommitIntent.Durable -> Voxel.Apply -> Journal.VoxelMarker.Durable -> ECS.Apply -> Journal.EcsMarker.Durable -> Journal.Committed.Durable -> Revision.Advance`；任意前移/缺失失败。

- [ ] **Step 2: 写 intent backpressure test。** Journal 返回 Backpressured 时不调用 Voxel/ECS，Txn 保持 Prepared，可用同一 idempotency key 重试；Journal Fatal 时 Session Faulted。

- [ ] **Step 3: 写 crash-at-each-boundary matrix。** 在上面 7 个边界分别 crash；恢复读取 journal+participant query。intent 前 Prepared 可 Abort/Expire；intent 后不确定 participant=`Unknown`，只有 query 证明后写 Applied/Failed；不能业务 rollback。

```csharp
[Theory]
[MemberData(nameof(CrashBoundaries.All))]
public void Recovery_never_guesses_unknown_participant(CrashBoundary boundary)
{
    RecoveryFixture fixture = RecoveryFixture.CrashAt(boundary);
    TxnCommitResult result = fixture.Recover();
    if (fixture.VoxelQueryWasUnavailable || fixture.EcsQueryWasUnavailable)
    {
        Assert.Equal(TxnCommitStatus.Indeterminate, result.Status);
        Assert.Contains(TxnParticipantState.Unknown,
            new[] { result.VoxelParticipant, result.EcsParticipant });
    }
}
```

- [ ] **Step 4: 运行失败测试。**

```bash
dotnet test modules/coordination/tests/Lumio.GameRuntime.Coordination.Tests/Lumio.GameRuntime.Coordination.Tests.csproj --filter "CommitIntentOrderingTests|CrashBoundaryRecoveryTests"
```

Expected: 缺 journal/commit/recovery 类型或 trace 不匹配。

- [ ] **Step 5: 实现 `ITxnJournalPort` 在 Coordination。** 方法只接受/返回 generated record/result；Persistence 后续实现 Adapter，因此不产生 `coordination -> persistence` 引用。

- [ ] **Step 6: 实现 commit algorithm。** durable intent 不成功即停止；成功后调用 Voxel Commit，再 durability marker，再 ECS Apply，再 marker/terminal；post-intent participant business rejection 转 Fatal contract violation。

- [ ] **Step 7: 实现 recovery resolver。** 从 journal terminal state 先返回；缺 marker 时 query participant；query Retryable 保持 Unknown/Indeterminate；proof 得到 Applied 后继续幂等收敛到 Committed。

- [ ] **Step 8: 运行测试。**

```bash
dotnet test modules/coordination/tests/Lumio.GameRuntime.Coordination.Tests/Lumio.GameRuntime.Coordination.Tests.csproj --filter "CommitIntentOrderingTests|CrashBoundaryRecoveryTests|DuplicateLostResultTests"
```

Expected: `Failed: 0`；trace order 精确，Unknown 不被布尔化。

- [ ] **Step 9: 提交。**

```bash
git add modules/coordination
git commit -m "feat(coordination): persist intent and recover participant state"
```

### Task 17: `coord-snapshot-cut` — 建立同一 Revision Vector 的 SnapshotCut lease

**对应设计卡：** `coord-snapshot-cut`

**Files:**
- Create: `modules/coordination/src/Lumio.GameRuntime.Coordination/Snapshot/SnapshotCutCoordinator.cs`
- Create: `modules/coordination/src/Lumio.GameRuntime.Coordination/Snapshot/SnapshotCutLease.cs`
- Create: `modules/coordination/tests/Lumio.GameRuntime.Coordination.Tests/SnapshotCutConsistencyTests.cs`

**Interfaces:**

- **Consumes:** Task 14 Revision store；ECS/GAS/Replication/Voxel provider revision views；SnapshotId/TickId/SchemaEpoch。
- **Produces:** Barrier-only SnapshotCut、participant revision equality/pin、idempotent release、provider manifest input。

**明确不做：** 不编码 Snapshot bytes，不实现存储激活，不复制 Voxel storage，不在 Tick 中途开 cut。

- [ ] **Step 1: 写 cut consistency test。** 四域 provider revision 与 store 完全一致时 Opened；任一域 stale/ahead/schema mismatch 时 Rejected/Retryable，不返回 partial lease。

- [ ] **Step 2: 写 pin/release test。** 成功 cut 对每个 provider pin 一次，lease Dispose 反序 release 一次；第二次 Dispose 无副作用；任一 pin 失败释放已 pin participant。

```csharp
[Fact]
public void Snapshot_cut_is_all_or_nothing_across_participants()
{
    SnapshotCutOpenResult result = Fixtures.CoordinatorWithGasPinFailure().TryOpen(Fixtures.CutRequest);
    Assert.False(result.Opened);
    Assert.Null(result.Lease);
    Assert.Equal(1, Fixtures.EcsProvider.PinCalls);
    Assert.Equal(1, Fixtures.EcsProvider.ReleaseCalls);
    Assert.Equal(0, Fixtures.VoxelProvider.LeakedPins);
}
```

- [ ] **Step 3: 运行失败测试。**

```bash
dotnet test modules/coordination/tests/Lumio.GameRuntime.Coordination.Tests/Lumio.GameRuntime.Coordination.Tests.csproj --filter SnapshotCutConsistencyTests
```

Expected: 缺 cut coordinator/lease。

- [ ] **Step 4: 实现 barrier guard 和 all-or-nothing pin。** 请求必须来自 `SnapshotHashMetrics` 声明 Barrier 或 Session pause/snapshot state；按稳定 participant order pin，失败反序 release。

- [ ] **Step 5: 实现 immutable cut view。** 包含架构源已冻结 `SnapshotId + TickId + SessionRevisionVector + SchemaEpoch`；不新增 Host timestamp/path/storage handle。

- [ ] **Step 6: 运行测试并提交。**

```bash
dotnet test modules/coordination/tests/Lumio.GameRuntime.Coordination.Tests/Lumio.GameRuntime.Coordination.Tests.csproj --filter SnapshotCutConsistencyTests
git add modules/coordination
git commit -m "feat(coordination): add consistent snapshot cuts"
```

Expected: `Failed: 0`，无 leaked pin。

### Task 18: `gas-foundation-type-handle-context` — 建立 GAS Type/Handle 与 ECS 单一真相的最小 Context

**对应设计卡：** `gas-type-handle-registry` 的 Foundation 子集

**Files:**
- Create: `modules/gas/src/Lumio.GameRuntime.Gas/Lumio.GameRuntime.Gas.csproj`
- Create: `modules/gas/tests/Lumio.GameRuntime.Gas.Tests/Lumio.GameRuntime.Gas.Tests.csproj`
- Create: `modules/gas/src/Lumio.GameRuntime.Gas/GasModule.cs`
- Create: `modules/gas/src/Lumio.GameRuntime.Gas/GasServices.cs`
- Create: `modules/gas/src/Lumio.GameRuntime.Gas/Lifecycle/GasFrameworkState.cs`
- Create: `modules/gas/src/Lumio.GameRuntime.Gas/Lifecycle/GasWorldContext.cs`
- Create: `modules/gas/src/Lumio.GameRuntime.Gas/Identity/AbilityTypeId.cs`
- Create: `modules/gas/src/Lumio.GameRuntime.Gas/Identity/AbilityInstanceId.cs`
- Create: `modules/gas/src/Lumio.GameRuntime.Gas/Identity/AbilityHandle.cs`
- Create: `modules/gas/src/Lumio.GameRuntime.Gas/Identity/EffectTypeId.cs`
- Create: `modules/gas/src/Lumio.GameRuntime.Gas/Identity/EffectInstanceId.cs`
- Create: `modules/gas/src/Lumio.GameRuntime.Gas/Identity/EffectHandle.cs`
- Create: `modules/gas/src/Lumio.GameRuntime.Gas/Identity/GasTypeRegistry.cs`
- Create: `modules/gas/src/Lumio.GameRuntime.Gas/Projection/IGasEcsProjectionPort.cs`
- Create: `modules/gas/tests/Lumio.GameRuntime.Gas.Tests/TypeHandlePropertyTests.cs`
- Create: `modules/gas/tests/Lumio.GameRuntime.Gas.Tests/FrameworkLifecycleTests.cs`
- Create: `modules/gas/tests/Lumio.GameRuntime.Gas.Tests/EcsSingleTruthTests.cs`

**Interfaces:**

- **Consumes:** Task 8–10 ECS；Task 7 Config；Command/Observability Port；generated type IDs。
- **Produces:** exact framework lifecycle、World-bound generation handles、type registry、ECS projection-only authority。

**明确不做：** 不实现 Ability/Effect full states、Modifier、Prediction，不创建第二份 Attribute/Tag authoritative dictionary。

- [ ] **Step 1: 写 framework lifecycle test。** 精确允许 `Unloaded -> Registered -> Ready -> Running -> Draining -> Unloaded`，任一 active state -> Faulted；Disposed/Unloaded handle creation 被拒绝。

- [ ] **Step 2: 写 handle property。** 不同 World 的相同 instance ID 永不相等/互解；retire/reuse generation 递增；stale handle 读取失败；对象地址不参与 equality/hash。

- [ ] **Step 3: 写 ECS single truth test。** GAS 读写 Attribute/Tag 只调用 `IGasEcsProjectionPort`；反射扫描 `GasWorldContext`/registry 不得有 `Dictionary<LocalEntityId, AttributeValue>` 或等价权威存储字段。

- [ ] **Step 4: 运行失败测试。**

```bash
dotnet test modules/gas/tests/Lumio.GameRuntime.Gas.Tests/Lumio.GameRuntime.Gas.Tests.csproj
```

Expected: `MSB1009`。

- [ ] **Step 5: 创建项目和 exact types。** 生产项目引用 ECS、Command、Config、Observability、GeneratedContracts；实现 §6.6 完整最小面。

- [ ] **Step 6: 实现 type registry。** 注册 generated Ability/Effect schema/type metadata，duplicate compatible 返回 AlreadyRegistered，same ID different schema Fatal；registry 激活后 immutable。

- [ ] **Step 7: 运行测试。**

```bash
dotnet test modules/gas/tests/Lumio.GameRuntime.Gas.Tests/Lumio.GameRuntime.Gas.Tests.csproj --filter "TypeHandlePropertyTests|FrameworkLifecycleTests|EcsSingleTruthTests"
```

Expected: `Failed: 0`；public surface 无 second authority store。

- [ ] **Step 8: 提交。**

```bash
git add modules/gas
git commit -m "feat(gas): add world-bound handles and ecs projection context"
```

### Task 19: `repl-foundation-mapping-and-identity` — 建立最小 Mapping Registry 与 Net/Local Identity Context

**对应设计卡：** `repl-mapping-registry-and-identity` 的 Foundation 子集

**Files:**
- Create: `modules/replication/src/Lumio.GameRuntime.Replication/Lumio.GameRuntime.Replication.csproj`
- Create: `modules/replication/tests/Lumio.GameRuntime.Replication.Tests/Lumio.GameRuntime.Replication.Tests.csproj`
- Create: `modules/replication/src/Lumio.GameRuntime.Replication/ReplicationModule.cs`
- Create: `modules/replication/src/Lumio.GameRuntime.Replication/ReplicationServices.cs`
- Create: `modules/replication/src/Lumio.GameRuntime.Replication/Lifecycle/ReplicationContextState.cs`
- Create: `modules/replication/src/Lumio.GameRuntime.Replication/Lifecycle/ReplicationContext.cs`
- Create: `modules/replication/src/Lumio.GameRuntime.Replication/Mapping/MappingRegistry.cs`
- Create: `modules/replication/src/Lumio.GameRuntime.Replication/Mapping/MappingSetView.cs`
- Create: `modules/replication/src/Lumio.GameRuntime.Replication/Identity/NetEntityMappingTable.cs`
- Create: `modules/replication/src/Lumio.GameRuntime.Replication/Identity/ProvisionalRemapTable.cs`
- Create: `modules/replication/tests/Lumio.GameRuntime.Replication.Tests/MappingRegistryGoldenTests.cs`
- Create: `modules/replication/tests/Lumio.GameRuntime.Replication.Tests/IdentityMappingPropertyTests.cs`

**Interfaces:**

- **Consumes:** Task 8 ECS LocalEntityId；Task 14 Revision view；Task 18 GAS facade；generated Mapping/NetEntity identity；Config/Observability。
- **Produces:** exact Context lifecycle、generated mapping validation/activation、per-World Net↔Local bijection 与 provisional remap boundary。

**明确不做：** 不实现 Socket、Baseline/Delta/History/Tombstone/六步 Apply，不要求 Server/Client component 对称。

- [ ] **Step 1: 写 Mapping Golden。** valid generated fixture 加载后 MappingSetId/SchemaEpoch/hash 与输入一致；empty field、unknown required field、role/visibility/lifecycle invalid fixture 被 generated validator 拒绝。

- [ ] **Step 2: 写 identity property。** 任意 bind/unbind/remap sequence 保持双向索引一致；duplicate NetEntityId、duplicate LocalEntityId、generation mismatch、cross-World local ID 均拒绝；authoritative NetEntityId 不复用。

```csharp
[Fact]
public void Net_and_local_mapping_is_bijective_with_generation_safety()
{
    var table = new NetEntityMappingTable(Fixtures.WorldId);
    Assert.True(table.TryBind(Fixtures.Net(1), new LocalEntityId(4, 1)));
    Assert.False(table.TryBind(Fixtures.Net(1), new LocalEntityId(5, 1)));
    Assert.False(table.TryBind(Fixtures.Net(2), new LocalEntityId(4, 1)));
    Assert.True(table.TryResolveLocal(Fixtures.Net(1), out LocalEntityId local));
    Assert.Equal(new LocalEntityId(4, 1), local);
}
```

- [ ] **Step 3: 写 lifecycle test。** exact states `Created, Snapshotting, AwaitingBaselineAck, Active, Resyncing, Draining, Closed, Faulted`；Foundation 只构造 Created，并允许 Close/Fault，不能发明 `Connected`/`Running` 同义状态。

- [ ] **Step 4: 运行失败测试。**

```bash
dotnet test modules/replication/tests/Lumio.GameRuntime.Replication.Tests/Lumio.GameRuntime.Replication.Tests.csproj
```

Expected: `MSB1009`。

- [ ] **Step 5: 创建项目和最小类型。** 依赖方向精确为 ECS、GAS、Coordination、Config、Observability、GeneratedContracts/Generated Voxel Replica Contract；不得引用 Command 实现，confirmed command sequence 后续来自中立 generated contract。

- [ ] **Step 6: 实现 Mapping/Identity。** Mapping 激活 defensive copy/hash bind；Identity table 绑定 WorldId，所有 mutation Owner Thread only；provisional remap 只接受 generated namespace/authority rules。

- [ ] **Step 7: 运行测试。**

```bash
dotnet test modules/replication/tests/Lumio.GameRuntime.Replication.Tests/Lumio.GameRuntime.Replication.Tests.csproj --filter "MappingRegistryGoldenTests|IdentityMappingPropertyTests"
```

Expected: `Failed: 0`；同 logical mapping input hash 不受 enumeration order 影响。

- [ ] **Step 8: 提交。**

```bash
git add modules/replication
git commit -m "feat(replication): add mapping and identity foundation"
```

### Task 20: `persist-foundation-canonical-codec` — 建立确定性 Canonical Encode/Decode 与预算

**对应设计卡：** `persist-canonical-codec`

**Files:**
- Create: `modules/persistence/src/Lumio.GameRuntime.Persistence/Lumio.GameRuntime.Persistence.csproj`
- Create: `modules/persistence/tests/Lumio.GameRuntime.Persistence.Tests/Lumio.GameRuntime.Persistence.Tests.csproj`
- Create: `modules/persistence/src/Lumio.GameRuntime.Persistence/Canonical/ICanonicalCodec.cs`
- Create: `modules/persistence/src/Lumio.GameRuntime.Persistence/Canonical/CanonicalRecordWriter.cs`
- Create: `modules/persistence/src/Lumio.GameRuntime.Persistence/Canonical/CanonicalRecordReader.cs`
- Create: `modules/persistence/src/Lumio.GameRuntime.Persistence/Canonical/CanonicalPrimitiveWriter.cs`
- Create: `modules/persistence/src/Lumio.GameRuntime.Persistence/Canonical/CanonicalPrimitiveReader.cs`
- Create: `modules/persistence/src/Lumio.GameRuntime.Persistence/Canonical/MessagePackCanonicalCodecAdapter.cs`
- Create: `modules/persistence/src/Lumio.GameRuntime.Persistence/Canonical/CanonicalBudgetExceededException.cs`
- Create: `modules/persistence/tests/Lumio.GameRuntime.Persistence.Tests/CanonicalRoundTripGoldenTests.cs`
- Create: `modules/persistence/tests/Lumio.GameRuntime.Persistence.Tests/CanonicalPropertyTests.cs`
- Create: `modules/persistence/tests/Lumio.GameRuntime.Persistence.Tests/CanonicalBudgetTests.cs`

**Interfaces:**

- **Consumes:** Task 3 generated canonical record views；Config/Observability；MessagePack primitive package via Adapter。
- **Produces:** 显式 field order/endianness/string/length rules、encode/decode budget、round-trip/canonical hash；第三方 codec 隔离。

**明确不做：** 不使用 Typeless/Contractless/global resolver，不把 Dictionary/object address 写入 bytes，不实现 Snapshot backend。

- [ ] **Step 1: 写 primitive Golden。** 为 UInt64、signed integer、UTF-8、byte array、optional field、ordered record 固定 bytes；在 x64/arm-compatible logical profile 与两次进程运行中相同。

- [ ] **Step 2: 写 property test。** 任意合法 generated ID/revision/command digest round-trip 等值；相同 logical map 先 canonical sort 后 bytes 相同；decode trailing bytes、duplicate field、invalid UTF-8、depth/length 超限明确 Rejected。

- [ ] **Step 3: 写 budget test。** encode max exact accepted、+1 byte before allocation rejected；decode input/output/depth 分别超限；不得先分配声明长度再校验。

```csharp
[Fact]
public void Decoder_rejects_declared_length_before_allocation()
{
    byte[] hostile = Fixtures.LengthPrefix(int.MaxValue);
    CanonicalDecodeResult<FixtureRecord> result = Fixtures.Codec.Decode(
        hostile,
        new CanonicalDecodeBudget(1024, 4096, 8),
        FixtureRecord.Decode);
    Assert.Equal(CanonicalCodecStatus.Rejected, result.Status);
    Assert.Equal(0, Fixtures.LargeAllocationCount);
}
```

- [ ] **Step 4: 运行失败测试。**

```bash
dotnet test modules/persistence/tests/Lumio.GameRuntime.Persistence.Tests/Lumio.GameRuntime.Persistence.Tests.csproj
```

Expected: `MSB1009`。

- [ ] **Step 5: 创建项目和自研最小 canonical layer。** 生产项目依最终 DAG 引用，但本任务只实现 `Canonical/`；field order/endianness/length policy 明示，writer/reader 使用 checked budget。

- [ ] **Step 6: 包装 MessagePack primitive。** Adapter 可使用 integer/string/bin primitive，但 record/member order 由 `ICanonicalRecord.WriteCanonical` 决定；禁止 ContractlessStandardResolver/Typeless。

- [ ] **Step 7: 运行测试与 package surface scan。**

```bash
dotnet test modules/persistence/tests/Lumio.GameRuntime.Persistence.Tests/Lumio.GameRuntime.Persistence.Tests.csproj --filter "CanonicalRoundTripGoldenTests|CanonicalPropertyTests|CanonicalBudgetTests"
grep -R "MessagePack" modules/persistence/src/Lumio.GameRuntime.Persistence --include='*.cs' | grep -v "MessagePackCanonicalCodecAdapter.cs"
```

Expected: tests `Failed: 0`；grep 无输出。

- [ ] **Step 8: 提交。**

```bash
git add modules/persistence
git commit -m "feat(persistence): add bounded canonical codec"
```

### Task 21: `sim-session-and-single-run-tick` — 建立 SimulationSession 生命周期与唯一 RunTick 入口

**对应设计卡：** `sim-session-and-run-tick-entry`

**Files:**
- Create: `modules/simulation/src/Lumio.GameRuntime.Simulation/Lumio.GameRuntime.Simulation.csproj`
- Create: `modules/simulation/tests/Lumio.GameRuntime.Simulation.Tests/Lumio.GameRuntime.Simulation.Tests.csproj`
- Create: `modules/simulation/src/Lumio.GameRuntime.Simulation/SimulationModule.cs`
- Create: `modules/simulation/src/Lumio.GameRuntime.Simulation/SimulationServices.cs`
- Create: `modules/simulation/src/Lumio.GameRuntime.Simulation/Session/SimulationSession.cs`
- Create: `modules/simulation/src/Lumio.GameRuntime.Simulation/Session/SimulationSessionState.cs`
- Create: `modules/simulation/src/Lumio.GameRuntime.Simulation/Session/SimulationOwnerThread.cs`
- Create: `modules/simulation/src/Lumio.GameRuntime.Simulation/Tick/IRuntimeSession.cs`
- Create: `modules/simulation/tests/Lumio.GameRuntime.Simulation.Tests/SessionLifecycleTests.cs`
- Create: `modules/simulation/tests/Lumio.GameRuntime.Simulation.Tests/SingleRunTickSurfaceTests.cs`

**Interfaces:**

- **Consumes:** Task 4–20 production facades/ports；generated IDs/Input/Error；Host 每 Logical Tick 一次调用。
- **Produces:** `IRuntimeSession.RunTick(in TickInput)` 单入口、exact lifecycle、Owner Thread identity、只转发 Revision/Txn view 的 facade。

**明确不做：** 不拥有 Host Wall Clock，不缓存第二份 Revision/Txn/Mapping/GAS 状态，不暴露多个 phase-by-phase public tick methods。

- [ ] **Step 1: 写 public surface test。** `IRuntimeSession` 只能有 SessionId、WorldId、State、`RunTick`、Dispose；任何 public `RunPhase`、`AdvanceClock`、`SetRevision`、`CommitVoxel` 方法失败。

```csharp
[Fact]
public void Runtime_session_exposes_one_tick_entry_only()
{
    MethodInfo[] methods = typeof(IRuntimeSession).GetMethods();
    Assert.Single(methods.Where(method => method.Name == nameof(IRuntimeSession.RunTick)));
    Assert.DoesNotContain(methods, method => method.Name.Contains("Phase", StringComparison.Ordinal));
    Assert.DoesNotContain(methods, method => method.Name.Contains("Clock", StringComparison.Ordinal));
    Assert.DoesNotContain(methods, method => method.Name.Contains("Revision", StringComparison.Ordinal));
}
```

- [ ] **Step 2: 写 lifecycle test。** exact states `Created -> Initialized -> Ready -> Running <-> Paused -> Draining -> Snapshotted -> Disposed`；任一 active state -> Faulted；Disposed/Faulted 后 RunTick 拒绝。

- [ ] **Step 3: 写 owner thread test。** Initialize/RunTick 在 owner thread 成功；另一个 thread 调用 RunTick 在读取/写入 World 前 Fatal/Faulted；Host wall clock 类型不出 project graph/public surface。

- [ ] **Step 4: 运行失败测试。**

```bash
dotnet test modules/simulation/tests/Lumio.GameRuntime.Simulation.Tests/Lumio.GameRuntime.Simulation.Tests.csproj
```

Expected: `MSB1009`。

- [ ] **Step 5: 创建项目和 Module/Services。** Simulation 生产项目依赖 ECS、Command、Coordination、GAS、Replication、Persistence、Config、Observability、GeneratedContracts；Module 仅做 Composition Root，不是状态拥有者。

- [ ] **Step 6: 实现 Session shell。** `RunTick` 只委托 internal TickRunner；Revision/Txn query 每次转发给 Coordination read port，不缓存可变副本；State transition guarded。

- [ ] **Step 7: 运行测试。**

```bash
dotnet test modules/simulation/tests/Lumio.GameRuntime.Simulation.Tests/Lumio.GameRuntime.Simulation.Tests.csproj --filter "SessionLifecycleTests|SingleRunTickSurfaceTests"
```

Expected: `Failed: 0`；reflection public surface 符合单入口。

- [ ] **Step 8: 提交。**

```bash
git add modules/simulation
git commit -m "feat(simulation): add session lifecycle and single tick entry"
```

### Task 22: `sim-exact-13-phase-graph` — 实现固定 13 相 graph 与 Phase contract table

**对应设计卡：** `sim-phase-graph-13`

**Files:**
- Create: `modules/simulation/src/Lumio.GameRuntime.Simulation/Phases/TickPhase.cs`
- Create: `modules/simulation/src/Lumio.GameRuntime.Simulation/Phases/PhaseGraph.cs`
- Create: `modules/simulation/src/Lumio.GameRuntime.Simulation/Phases/PhaseContractTable.cs`
- Create: `modules/simulation/src/Lumio.GameRuntime.Simulation/Phases/PhaseContract.cs`
- Create: `modules/simulation/tests/Lumio.GameRuntime.Simulation.Tests/PhaseGraphGoldenTests.cs`
- Create: `modules/simulation/tests/Lumio.GameRuntime.Simulation.Tests/PhaseContractMatrixTests.cs`

**Interfaces:**

- **Consumes:** Task 21 session shell；架构源 exact Phase/visibility/failure/cancel/commit matrix。
- **Produces:** 13-value enum、total order graph、每相 input/write/failure/cancel/visibility contract、唯一 commit point 标记。

**明确不做：** 不增加 PreTick/PostTick 同义 Phase，不允许配置重排，不把 Host wall-clock deadline 作为 Phase。

- [ ] **Step 1: 写 exact enum Golden。** `Enum.GetNames<TickPhase>()` 精确等于 13 个冻结名字且数值 0–12 连续；少一个、多一个、改名或重排均失败。

```csharp
[Fact]
public void Tick_phase_names_and_order_match_architecture_baseline()
{
    string[] expected =
    {
        "IngressCapture", "DecodeAndCanonicalize", "ApplyInputs", "ProcessorPlan",
        "CrossWorldPrepare", "NativeJobBarrier", "CommitDecision", "VoxelCommit",
        "EcsCommandBufferCommit", "GasAndEventFinalize", "ReplicationProjection",
        "SnapshotHashMetrics", "EgressPublish"
    };
    Assert.Equal(expected, Enum.GetNames<TickPhase>());
    Assert.Equal(Enumerable.Range(0, 13), Enum.GetValues<TickPhase>().Select(value => (int)value));
}
```

- [ ] **Step 2: 写 matrix completeness test。** 每个 Phase 恰有一行，声明 allowed inputs、writable owner、failure action、cancel point、visibility；只有 `GasAndEventFinalize.AuthoritativeCommitPoint=true`。

- [ ] **Step 3: 写 graph edge test。** 每个 Phase 只指向下一个，EgressPublish 无 successor；禁止跳过、回边和动态插入。

- [ ] **Step 4: 运行失败测试。**

```bash
dotnet test modules/simulation/tests/Lumio.GameRuntime.Simulation.Tests/Lumio.GameRuntime.Simulation.Tests.csproj --filter "PhaseGraphGoldenTests|PhaseContractMatrixTests"
```

Expected: 缺 Phase types。

- [ ] **Step 5: 实现 enum/graph/table。** Phase table 从架构源 generated/validated descriptor 投影；若 generated contract 缺行或 baseline mismatch，Session Initialize 失败，不用本仓猜值补齐。

- [ ] **Step 6: 运行测试。**

```bash
dotnet test modules/simulation/tests/Lumio.GameRuntime.Simulation.Tests/Lumio.GameRuntime.Simulation.Tests.csproj --filter "PhaseGraphGoldenTests|PhaseContractMatrixTests"
```

Expected: `Failed: 0`；commit point count 精确为 1。

- [ ] **Step 7: 提交。**

```bash
git add modules/simulation
git commit -m "feat(simulation): add exact thirteen-phase graph"
```

### Task 23: `sim-processor-plan-validator` — 构建确定序 Processor Plan 并验证读写/预算契约

**对应设计卡：** `sim-processor-plan-validator`

**Files:**
- Create: `modules/simulation/src/Lumio.GameRuntime.Simulation/Planning/ProcessorPlanBuilder.cs`
- Create: `modules/simulation/src/Lumio.GameRuntime.Simulation/Planning/ProcessorPlan.cs`
- Create: `modules/simulation/src/Lumio.GameRuntime.Simulation/Planning/ProcessorInvocation.cs`
- Create: `modules/simulation/src/Lumio.GameRuntime.Simulation/Planning/ProcessorPlanFailure.cs`
- Create: `modules/simulation/tests/Lumio.GameRuntime.Simulation.Tests/ProcessorPlanPropertyTests.cs`
- Create: `modules/simulation/tests/Lumio.GameRuntime.Simulation.Tests/ProcessorDescriptorGoldenTests.cs`

**Interfaces:**

- **Consumes:** Task 22 Phase graph；generated ProcessorDescriptor/ReadSet/WriteSet/Budget；ECS/Command ports。
- **Produces:** validated immutable plan、stable ProcessorId order、phase legality、read/write/conflict/budget evidence。

**明确不做：** 不让 reflection discovery order 决定执行，不在 Runtime 发明 gameplay processor，不把 structural writes 误置为 processor phase。

- [ ] **Step 1: 写 permutation property。** 同一 descriptors 以任意输入/assembly reflection order 提供，计划按 Phase + generated priority/order + ProcessorId canonical 排序，canonical plan hash 相同。

- [ ] **Step 2: 写 descriptor Golden。** unknown phase、duplicate ProcessorId、unknown type/field、ReadSet/WriteSet invalid、budget <=0、structural command declaration错误均在 Session Ready 前拒绝。

- [ ] **Step 3: 写 field overlap test。** 同 Processor 允许 read own write field 仅按架构源规则；两个 Processor 冲突由 frozen ordering/command semantics判定，validator 不自行并行化。

- [ ] **Step 4: 运行失败测试。**

```bash
dotnet test modules/simulation/tests/Lumio.GameRuntime.Simulation.Tests/Lumio.GameRuntime.Simulation.Tests.csproj --filter "ProcessorPlanPropertyTests|ProcessorDescriptorGoldenTests"
```

Expected: 缺 plan builder/types。

- [ ] **Step 5: 实现 builder。** 先用 generated validator验证 descriptor，再复制/sort；plan 不持 gameplay object address，只持 ProcessorId、Phase、sets、budget 和调用 Port handle。

- [ ] **Step 6: 实现 invocation lookup。** `GetForPhase(TickPhase)` 返回 readonly contiguous slice；ProcessorPlan immutable，Session Running 后不可修改。

- [ ] **Step 7: 运行测试。**

```bash
dotnet test modules/simulation/tests/Lumio.GameRuntime.Simulation.Tests/Lumio.GameRuntime.Simulation.Tests.csproj --filter "ProcessorPlanPropertyTests|ProcessorDescriptorGoldenTests"
```

Expected: `Failed: 0`；所有 permutation hash 一致。

- [ ] **Step 8: 提交。**

```bash
git add modules/simulation
git commit -m "feat(simulation): validate deterministic processor plans"
```

### Task 24: `sim-ingress-canonicalization-and-native-barrier` — 实现有界 Ingress 与 Native Completion Barrier

**对应设计卡：** `sim-ingress-and-native-completion`

**Files:**
- Create: `modules/simulation/src/Lumio.GameRuntime.Simulation/Ingress/IngressQueue.cs`
- Create: `modules/simulation/src/Lumio.GameRuntime.Simulation/Ingress/IngressBudget.cs`
- Create: `modules/simulation/src/Lumio.GameRuntime.Simulation/Ingress/InputCanonicalizer.cs`
- Create: `modules/simulation/src/Lumio.GameRuntime.Simulation/Native/NativeCompletionQueue.cs`
- Create: `modules/simulation/src/Lumio.GameRuntime.Simulation/Native/NativeCompletionMerger.cs`
- Create: `modules/simulation/src/Lumio.GameRuntime.Simulation/Native/NativeCompletionBudget.cs`
- Create: `modules/simulation/tests/Lumio.GameRuntime.Simulation.Tests/IngressCanonicalizationTests.cs`
- Create: `modules/simulation/tests/Lumio.GameRuntime.Simulation.Tests/NativeBarrierFaultTests.cs`

**Interfaces:**

- **Consumes:** Task 22 Phase graph；generated Envelope/Input/Native completion；`IngressQueueCapacity`、`IngressQueueBytes`、`NativeCompletionQueueCapacity`。
- **Produces:** bounded ingress、Canonical input order、late/duplicate classification、reliable native completion barrier 与 stable merge。

**明确不做：** 不读 Socket/Connection，不按 arrival thread timing 排序，不让 native callback 直接写 World，不丢 reliable completion。

- [ ] **Step 1: 写 Ingress full action test。** exact capacity/bytes accepted；超限按 Queue Matrix 返回 Rejected/Backpressured，队列保持有界；不返回未标记 partial batch。

- [ ] **Step 2: 写 canonicalization property。** 同一 envelope set 以不同 arrival order/worker batch order输入，按 frozen arrival class、sequence、command ID canonical 后相同；duplicate input 返回 original idempotent classification。

- [ ] **Step 3: 写 native barrier test。** callback 在 worker 仅 enqueue immutable completion；直到 `NativeJobBarrier` 前 ECS/Voxel/GAS revision 不变。Barrier stable merge按 Job/Token generated order；队列满可靠结果触发 Fault/stop admission，不 drop。

```csharp
[Fact]
public void Native_completion_is_not_visible_before_barrier()
{
    var fixture = Fixtures.PendingNativeCompletion();
    fixture.WorkerCallback();
    Assert.Equal(fixture.BeforeRevision, fixture.Revisions.Read());
    fixture.RunThrough(TickPhase.NativeJobBarrier);
    Assert.Equal(fixture.AfterRevision, fixture.Revisions.Read());
}
```

- [ ] **Step 4: 运行失败测试。**

```bash
dotnet test modules/simulation/tests/Lumio.GameRuntime.Simulation.Tests/Lumio.GameRuntime.Simulation.Tests.csproj --filter "IngressCanonicalizationTests|NativeBarrierFaultTests"
```

Expected: 缺 queue/canonicalizer/merger types。

- [ ] **Step 5: 实现 internal bounded Channel adapters。** Channel concrete type只在 internal queue；容量来自 ConfigSnapshot/Capability exact parameter names。Ingress element包含 validated envelope+arrival metadata，不含 Connection object。

- [ ] **Step 6: 实现 canonicalizer/merger。** 使用 generated schema/permissions/size/identity validator；排序键不含 timestamp/object hash。late input按 frozen class返回 ApplyNext/Reject/Resync action，不自行扩 enum。

- [ ] **Step 7: 运行测试。**

```bash
dotnet test modules/simulation/tests/Lumio.GameRuntime.Simulation.Tests/Lumio.GameRuntime.Simulation.Tests.csproj --filter "IngressCanonicalizationTests|NativeBarrierFaultTests"
```

Expected: `Failed: 0`；full queue tests bounded，pre-barrier revision不变。

- [ ] **Step 8: 提交。**

```bash
git add modules/simulation
git commit -m "feat(simulation): add bounded ingress and native barrier"
```

### Task 25: `sim-determinism-context-and-state-hash` — 建立 DeterminismContext 与跨模块 Canonical State Hash

**对应设计卡：** `sim-determinism-context-and-state-hash`

**Files:**
- Create: `modules/simulation/src/Lumio.GameRuntime.Simulation/Determinism/DeterminismContext.cs`
- Create: `modules/simulation/src/Lumio.GameRuntime.Simulation/Determinism/IStateHashContributor.cs`
- Create: `modules/simulation/src/Lumio.GameRuntime.Simulation/Determinism/StateHashCoordinator.cs`
- Create: `modules/simulation/src/Lumio.GameRuntime.Simulation/Determinism/StateHashManifest.cs`
- Create: `modules/simulation/tests/Lumio.GameRuntime.Simulation.Tests/DeterminismReplayTests.cs`
- Create: `modules/simulation/tests/Lumio.GameRuntime.Simulation.Tests/StateHashExclusionTests.cs`

**Interfaces:**

- **Consumes:** Task 7 ConfigSnapshotId；ECS/Command/Coordination/GAS/Replication contributors；Task 20 Canonical writer；generated release/session/world/tick/schema IDs。
- **Produces:** per-Tick immutable DeterminismContext、stable contributor order、SHA-256 StateHash/manifest、明确 exclusion tests。

**明确不做：** 不纳入 wall clock、object address、thread ID、Diagnostic、queue depth、pool state、unordered enumeration。

- [ ] **Step 1: 写 same-seed replay test。** 两个独立 World/Session 用同 release/config/schema/seed/input执行，连续 64 Tick state hash逐 Tick相同；改变一个 canonical input byte在首个受影响 Tick产生不同 hash。

- [ ] **Step 2: 写 exclusion test。** 随机改变 Diagnostic timestamp、thread ID、queue scheduling、object allocation address、dictionary insertion order，authoritative hash不变；改变 committed field/revision则变。

- [ ] **Step 3: 写 contributor manifest test。** contributor ID按 ordinal sort且唯一；Foundation 必含 `ecs`, `command`, `coordination`, `gas`, `replication`; optional provider absent必须在 manifest显式状态，不跳过无记录。

```csharp
[Fact]
public void Diagnostic_and_runtime_addresses_are_excluded_from_state_hash()
{
    ReadOnlyMemory<byte> first = Fixtures.ComputeHash(diagnosticTimestamp: 10, allocationSalt: 1);
    ReadOnlyMemory<byte> second = Fixtures.ComputeHash(diagnosticTimestamp: 99, allocationSalt: 8);
    Assert.Equal(first.ToArray(), second.ToArray());
}
```

- [ ] **Step 4: 运行失败测试。**

```bash
dotnet test modules/simulation/tests/Lumio.GameRuntime.Simulation.Tests/Lumio.GameRuntime.Simulation.Tests.csproj --filter "DeterminismReplayTests|StateHashExclusionTests"
```

Expected: 缺 determinism/hash types。

- [ ] **Step 5: 实现 immutable context。** Context由 Session在 Tick入口创建，只含generated IDs、TickId、seed、SchemaEpoch、ConfigSnapshotId；Processor不可修改或访问 Host clock。

- [ ] **Step 6: 实现 hash coordinator。** contributors按 ContributorId ordinal排序，逐个写 canonical bytes；hash algorithm固定 SHA-256，manifest记录baseline/schema/config/contributor IDs和各段hash，数字字段使用canonical writer。

- [ ] **Step 7: 运行测试。**

```bash
dotnet test modules/simulation/tests/Lumio.GameRuntime.Simulation.Tests/Lumio.GameRuntime.Simulation.Tests.csproj --filter "DeterminismReplayTests|StateHashExclusionTests"
```

Expected: `Failed: 0`；64 Tick hash逐项一致，single-byte mutation首差异精确。

- [ ] **Step 8: 提交。**

```bash
git add modules/simulation
git commit -m "feat(simulation): add deterministic context and state hash"
```

### Task 26: `sim-tick-runner-fail-stop-and-result` — 串联 13 相、唯一 Commit Point、duplicate result 与 Fail-stop

**对应设计卡：** `sim-fail-stop-and-tick-result`

**Files:**
- Create: `modules/simulation/src/Lumio.GameRuntime.Simulation/Tick/TickRunner.cs`
- Create: `modules/simulation/src/Lumio.GameRuntime.Simulation/Tick/TickExecutionContext.cs`
- Create: `modules/simulation/src/Lumio.GameRuntime.Simulation/Tick/TickRunResult.cs`
- Create: `modules/simulation/src/Lumio.GameRuntime.Simulation/Tick/TickResultCache.cs`
- Create: `modules/simulation/src/Lumio.GameRuntime.Simulation/Failure/FailStopController.cs`
- Create: `modules/simulation/src/Lumio.GameRuntime.Simulation/Failure/PhaseFailureRecord.cs`
- Create: `modules/simulation/src/Lumio.GameRuntime.Simulation/Errors/SimulationFailure.cs`
- Create: `modules/simulation/tests/Lumio.GameRuntime.Simulation.Tests/FailStopCommitPointTests.cs`
- Create: `modules/simulation/tests/Lumio.GameRuntime.Simulation.Tests/DuplicateTickTests.cs`
- Create: `modules/simulation/tests/Lumio.GameRuntime.Simulation.Tests/FullTickTraceTests.cs`

**Interfaces:**

- **Consumes:** Task 21–25；Config/ECS/Command/Coordination/GAS/Replication/Persistence/Observability facades；FailureBundle port。
- **Produces:** 完整 13相 TickRunner、phase trace、CommitDecision/Voxel/ECS/GAS finalize、single commit、result cache、fatal evidence。

**明确不做：** 不吞异常继续，不在 Egress后回写权威状态，不因 duplicate Tick重复 Apply，不实现 wall-clock pacing。

- [ ] **Step 1: 写 full trace test。** 无输入空 Tick也必须依次进入/完成13相；每相记录一次；`GasAndEventFinalize.AuthoritativeCommitPoint=true`且其余false；result=`Committed`。

- [ ] **Step 2: 写 commit semantics matrix。** Prepare拒绝发生在CrossWorldPrepare/CommitDecision前，返回Rejected且无commit；intent后Voxel/ECS infrastructure failure返回Faulted/Indeterminate；GasAndEventFinalize失败使Session Faulted且Tick不报告Committed。

- [ ] **Step 3: 写 duplicate Tick test。** 同 TickId+same canonical input digest返回缓存`AlreadyCommitted`，phase/processor/voxel/ecs call count不增加；同TickId+different digest返回Fatal idempotency conflict并Fault。

- [ ] **Step 4: 写 phase fault injection test。** 13个Phase各注入fatal；Session Faulted、后续RunTick拒绝、FailureBundle合法；首个snapshot前包含NoSnapshotReason/BootstrapPhase/LastKnownRevision。

```csharp
[Theory]
[MemberData(nameof(TickPhaseFixtures.All))]
public void Fatal_phase_failure_stops_session_and_emits_bundle(TickPhase phase)
{
    var fixture = Fixtures.SessionFaultingAt(phase);
    TickRunResult result = fixture.Session.RunTick(Fixtures.TickInput(1));
    Assert.Equal(TickRunStatus.Faulted, result.Status);
    Assert.Equal(SimulationSessionState.Faulted, fixture.Session.State);
    Assert.Single(fixture.FailureBundles);
    Assert.False(fixture.Session.RunTick(Fixtures.TickInput(2)).Status == TickRunStatus.Committed);
}
```

- [ ] **Step 5: 运行失败测试。**

```bash
dotnet test modules/simulation/tests/Lumio.GameRuntime.Simulation.Tests/Lumio.GameRuntime.Simulation.Tests.csproj --filter "FailStopCommitPointTests|DuplicateTickTests|FullTickTraceTests"
```

Expected: 缺 TickRunner/result/cache/fail-stop types。

- [ ] **Step 6: 实现 phase runner skeleton。** 对13相使用PhaseGraph next顺序；每相调用窄facade，成功才记录Completed；任何分类结果交统一failure policy。不要用catch-all转success。

- [ ] **Step 7: 实现 commit path。** `CrossWorldPrepare -> NativeJobBarrier -> CommitDecision(durable intent) -> VoxelCommit -> EcsCommandBufferCommit -> GasAndEventFinalize`；Finalize成功后才标authoritative committed并允许后续 projection/hash/egress。

- [ ] **Step 8: 实现 result cache。** key=`TickId + canonical input digest + release/config/schema context`；只缓存terminal result；duplicate same返回原state hash/revision/phase trace，different digest Fatal。

- [ ] **Step 9: 实现 Failure Bundle。** FailStopController捕获当前phase、last completed phase、revision、prepared/participant tokens、snapshot或noSnapshotReason，走IFailureBundlePort durable write；bundle write失败仍保持Session Faulted并走emergency evidence path。

- [ ] **Step 10: 运行全部 Simulation tests。**

```bash
dotnet test modules/simulation/tests/Lumio.GameRuntime.Simulation.Tests/Lumio.GameRuntime.Simulation.Tests.csproj
```

Expected: `Failed: 0`；full trace 13行，commit point 1，duplicate无额外side effects。

- [ ] **Step 11: 提交。**

```bash
git add modules/simulation
git commit -m "feat(simulation): run deterministic fail-stop ticks"
```

### Task 27: `test-reference-voxel-authority-port` — 实现 test-only Voxel Authority participant

**对应设计卡：** `test-reference-voxel-authority-port`

**Files:**
- Create: `modules/testing/src/Lumio.GameRuntime.ReferenceHost/ReferenceVoxelAuthorityPort.cs`
- Create: `modules/testing/src/Lumio.GameRuntime.ReferenceHost/ReferenceVoxelTxnRecord.cs`
- Create: `modules/testing/tests/Lumio.GameRuntime.Testing.Tests/ReferenceVoxelAuthorityTxnTests.cs`

**Interfaces:**

- **Consumes:** Task 15–16 IVoxelAuthorityPort/generated tokens/results；test-only project boundary。
- **Produces:** 确定性prepare/commit/abort/query、idempotent token/receipt、fault hooks、可查询participant state。

**明确不做：** 不模拟Voxel内部chunk/storage，不进入production package，不绕过generated request/result validation。

- [ ] **Step 1: 写 happy/idempotency test。** same TxnId/same request Prepare返回same token；Commit两次=`Applied`语义/相同receipt；Query=`Applied`；commit后Abort拒绝且不回滚。

- [ ] **Step 2: 写 abort test。** Prepared前/后合法窗口按contract；Prepared token Abort两次返回原abort receipt；Abort后Commit不应用。

- [ ] **Step 3: 写 fault hooks。** 可在Prepare、Commit-before-visible、Commit-after-visible-before-receipt、Query分别注入Retryable/Fatal；visible mutation与participant state符合边界。

```csharp
[Fact]
public void Lost_commit_result_is_resolved_by_query_without_double_apply()
{
    var port = new ReferenceVoxelAuthorityPort(FaultProfile.LoseFirstCommitResult);
    VoxelPreparePortResult prepared = port.Prepare(Fixtures.VoxelPrepareRequest(1));
    VoxelCommitPortResult first = port.Commit(prepared.Token);
    Assert.Equal(TxnParticipantState.Unknown, first.ParticipantState);
    VoxelStatusPortResult status = port.Query(Fixtures.Txn(1));
    Assert.Equal(TxnParticipantState.Applied, status.ParticipantState);
    Assert.Equal(1, port.VisibleApplyCount);
}
```

- [ ] **Step 4: 运行失败测试。**

```bash
dotnet test modules/testing/tests/Lumio.GameRuntime.Testing.Tests/Lumio.GameRuntime.Testing.Tests.csproj --filter ReferenceVoxelAuthorityTxnTests
```

Expected: project尚未由Task28创建时可先在临时test project编译失败；正式执行顺序可让Task28先创建project shell再回到本卡。文件owner仍是本卡。

- [ ] **Step 5: 实现纯内存participant。** 状态仅Missing/Prepared/Committed/Aborted，revision/token sequence由deterministic counter生成；所有input/output走generated validator，dictionary enumeration不进hash。

- [ ] **Step 6: 运行测试。**

```bash
dotnet test modules/testing/tests/Lumio.GameRuntime.Testing.Tests/Lumio.GameRuntime.Testing.Tests.csproj --filter ReferenceVoxelAuthorityTxnTests
```

Expected: `Failed: 0`；lost result apply count为1。

- [ ] **Step 7: 提交。**

```bash
git add modules/testing/src/Lumio.GameRuntime.ReferenceHost/ReferenceVoxelAuthorityPort.cs modules/testing/src/Lumio.GameRuntime.ReferenceHost/ReferenceVoxelTxnRecord.cs modules/testing/tests/Lumio.GameRuntime.Testing.Tests/ReferenceVoxelAuthorityTxnTests.cs
git commit -m "test: add reference voxel authority participant"
```

### Task 28: `test-reference-host-foundation-slice` — 组装 Reference Host 并跑通第一条 64 Tick Foundation Scenario

**对应设计卡：** `test-reference-host-shell`

**Files:**
- Create: `modules/testing/src/Lumio.GameRuntime.Testing/Lumio.GameRuntime.Testing.csproj`
- Create: `modules/testing/src/Lumio.GameRuntime.ReferenceHost/Lumio.GameRuntime.ReferenceHost.csproj`
- Create: `modules/testing/tests/Lumio.GameRuntime.Testing.Tests/Lumio.GameRuntime.Testing.Tests.csproj`
- Create: `modules/testing/src/Lumio.GameRuntime.Testing/TestingModule.cs`
- Create: `modules/testing/src/Lumio.GameRuntime.ReferenceHost/ReferenceHost.cs`
- Create: `modules/testing/src/Lumio.GameRuntime.ReferenceHost/ReferenceHostSession.cs`
- Create: `modules/testing/src/Lumio.GameRuntime.ReferenceHost/ReferenceClockPort.cs`
- Create: `modules/testing/src/Lumio.GameRuntime.ReferenceHost/ReferenceObservabilitySink.cs`
- Create: `modules/testing/src/Lumio.GameRuntime.ReferenceHost/ReferenceTxnJournalPort.cs`
- Create: `modules/testing/src/Lumio.GameRuntime.ReferenceHost/ReferenceGameplayModule.cs`
- Create: `modules/testing/src/Lumio.GameRuntime.ReferenceHost/ReferenceWorldStorageAdapter.cs`
- Create: `modules/testing/scenarios/foundation-single-world-64-ticks.json`
- Create: `modules/testing/tests/Lumio.GameRuntime.Testing.Tests/ReferenceHostFoundationSliceTests.cs`

**Interfaces:**

- **Consumes:** Task 4–27 modules/ports；generated fixtures；test-only composition。
- **Produces:** Reference Host、deterministic clock/input/journal/voxel/storage/sinks、single-world scenario、per-Tick trace/hash/revision evidence。

**明确不做：** 不发布为production assembly，不共享Server/Client mutable World，不绕过Envelope/Schema/permissions/queue，不模拟Socket为Runtime职责。

- [ ] **Step 1: 创建三个test-only项目。** `Lumio.GameRuntime.Testing`与`ReferenceHost`只目标net10.0；Testing可引用全部production，ReferenceHost引用Testing/production；生产项目没有反向reference。

- [ ] **Step 2: 写 scenario JSON。** 固定`name=foundation-single-world-64-ticks`、`tickCount=64`、`seed=764321`、每8 Tick一个canonical input、每16 Tick一组create/write/destroy command，无fault；JSON只为test scenario，不是权威Runtime存储。

- [ ] **Step 3: 写端到端失败测试。** 构造两套完全独立host/session/world；运行同scenario；每个tick status=Committed、13phase、1commitpoint、hash/revision相等；最后World hash非空。

```csharp
[Fact]
public void Foundation_slice_runs_sixty_four_deterministic_ticks()
{
    ReferenceScenario scenario = ReferenceScenarioLoader.Load("foundation-single-world-64-ticks.json");
    using ReferenceHost first = ReferenceHostFactory.CreateFoundation();
    using ReferenceHost second = ReferenceHostFactory.CreateFoundation();
    ReferenceRunResult a = first.Run(scenario);
    ReferenceRunResult b = second.Run(scenario);
    Assert.Equal(64, a.CompletedTickCount);
    Assert.Equal(64, b.CompletedTickCount);
    for (int i = 0; i < 64; i++)
    {
        Assert.Equal(TickRunStatus.Committed, a.Ticks[i].Status);
        Assert.Equal(13, a.Ticks[i].Phases.Length);
        Assert.Single(a.Ticks[i].Phases.Span.ToArray(), phase => phase.AuthoritativeCommitPoint);
        Assert.Equal(a.Ticks[i].StateHash.ToArray(), b.Ticks[i].StateHash.ToArray());
        Assert.Equal(a.Ticks[i].Revision, b.Ticks[i].Revision);
    }
}
```

- [ ] **Step 4: 运行失败测试。**

```bash
dotnet test modules/testing/tests/Lumio.GameRuntime.Testing.Tests/Lumio.GameRuntime.Testing.Tests.csproj --filter ReferenceHostFoundationSliceTests
```

Expected: 缺projects/composition/types。

- [ ] **Step 5: 实现reference adapters。** Clock只提供deterministic test tick requests，不注入wall time；Journal保存generated records并验证order/idempotency；Storage实现ECS adapter conformance；Observability区分Diagnostic与Durable collections。

- [ ] **Step 6: 实现ReferenceGameplayModule。** 注册最小generated component/processor descriptors；Processor只用Ecs views和per-processor CommandBuffer，不直接结构改World；inputs先封装成generated Envelope再canonicalize。

- [ ] **Step 7: 组装ReferenceHostSession。** 显式new各Module/Services，不引入通用DI；每个World/Session有独立instances；dispose反序释放leases/queues/world。

- [ ] **Step 8: 运行端到端test和single test log。**

```bash
dotnet test modules/testing/tests/Lumio.GameRuntime.Testing.Tests/Lumio.GameRuntime.Testing.Tests.csproj --filter ReferenceHostFoundationSliceTests --report-trx --results-directory artifacts/test-results/reference-foundation
```

Expected: `Failed: 0`；TRX包含64 Tick、每Tick hash/phase summary；无ignored/skipped。

- [ ] **Step 9: 提交。**

```bash
git add modules/testing
git commit -m "test: run reference host foundation slice"
```

### Task 29: `test-replay-and-first-difference` — 实现 Replay、Canonical Hash 比较和首差异定位

**对应设计卡：** `test-replay-and-first-difference`

**Files:**
- Create: `modules/testing/src/Lumio.GameRuntime.Testing/Replay/ReplayInput.cs`
- Create: `modules/testing/src/Lumio.GameRuntime.Testing/Replay/ReplayRunner.cs`
- Create: `modules/testing/src/Lumio.GameRuntime.Testing/Replay/CanonicalStateHasher.cs`
- Create: `modules/testing/src/Lumio.GameRuntime.Testing/Replay/FirstDifferenceFinder.cs`
- Create: `modules/testing/src/Lumio.GameRuntime.Testing/Replay/ReplayResult.cs`
- Create: `modules/testing/tests/Lumio.GameRuntime.Testing.Tests/ReplayFirstDifferenceTests.cs`

**Interfaces:**

- **Consumes:** Task 25 StateHash manifest；Task 28 ReferenceHost/scenario；canonical input stream。
- **Produces:** run-twice replay、per-Tick comparison、first differing Tick/contributor/path/evidence、identical result proof。

**明确不做：** 不比较Diagnostic timestamp，不把first difference算法打入production，不修改输入来掩盖分叉。

- [ ] **Step 1: 写 identical replay test。** 同scenario run twice返回`Equivalent=true`、FirstDifference=null、64 hashes equal。

- [ ] **Step 2: 写 one-byte mutation test。** 第二run仅在Tick17一个component field payload改变1 byte；finder必须报告Tick17、对应contributor ID和canonical path，不得只报告最终hash不同。

- [ ] **Step 3: 写 excluded noise test。** 第二run改变Diagnostic scheduling/timestamps和allocation salt，仍Equivalent。

```csharp
[Fact]
public void First_difference_reports_tick_contributor_and_path()
{
    ReferenceScenario original = Fixtures.FoundationScenario();
    ReferenceScenario mutated = Fixtures.MutateOneCanonicalByte(original, tick: 17);
    ReplayComparison result = Fixtures.RunAndCompare(original, mutated);
    Assert.False(result.Equivalent);
    Assert.Equal(TickId.FromUInt64(17), result.FirstDifference!.TickId);
    Assert.Equal("ecs", result.FirstDifference.ContributorId);
    Assert.Equal("entity/4/component/Position/field/X", result.FirstDifference.CanonicalPath);
}
```

- [ ] **Step 4: 运行失败测试。**

```bash
dotnet test modules/testing/tests/Lumio.GameRuntime.Testing.Tests/Lumio.GameRuntime.Testing.Tests.csproj --filter ReplayFirstDifferenceTests
```

Expected: 缺Replay types。

- [ ] **Step 5: 实现 ReplayInput。** 保存generated canonical envelopes、release/config/schema/seed和scenario capability；不保存object reference或wall clock。

- [ ] **Step 6: 实现 comparison。** 先比较Tick result/hash，再比较contributor segment hash，再按canonical record path定位首差异；任何missing evidence返回`EvidenceIncomplete`，不猜路径。

- [ ] **Step 7: 运行测试。**

```bash
dotnet test modules/testing/tests/Lumio.GameRuntime.Testing.Tests/Lumio.GameRuntime.Testing.Tests.csproj --filter ReplayFirstDifferenceTests
```

Expected: `Failed: 0`；mutation首差异Tick17，noise等价。

- [ ] **Step 8: 提交。**

```bash
git add modules/testing/src/Lumio.GameRuntime.Testing/Replay modules/testing/tests/Lumio.GameRuntime.Testing.Tests/ReplayFirstDifferenceTests.cs
git commit -m "test: add deterministic replay first-difference evidence"
```

### Task 30: `repo-solution-graph-and-foundation-gate` — 组装 solution 并锁定 DAG、public surface 与生产隔离

**对应设计卡：** `repo-solution-graph-and-architecture-tests`

**Files:**
- Create: `Lumio.GameRuntime.slnx`
- Create: `tests/Lumio.GameRuntime.Architecture.Tests/Lumio.GameRuntime.Architecture.Tests.csproj`
- Create: `tests/Lumio.GameRuntime.Architecture.Tests/ProjectDependencyGraphTests.cs`
- Create: `tests/Lumio.GameRuntime.Architecture.Tests/PublicSurfaceIsolationTests.cs`
- Create: `tests/Lumio.GameRuntime.Architecture.Tests/GeneratedContractDirectionTests.cs`
- Create: `tests/Lumio.GameRuntime.Architecture.Tests/TestingIsolationTests.cs`
- Create: `tests/Lumio.GameRuntime.Architecture.Tests/ModuleBoundaryTests.cs`
- Create: `eng/verify-project-graph.sh`
- Create: `eng/verify-project-graph.ps1`

**Interfaces:**

- **Consumes:** Task 1–29 projects/artifacts；`modules/README.md` DAG；dependency policy。
- **Produces:** 完整slnx、machine-readableproject graph、third-party public leak scan、generated direction、testing isolation、Foundation gate report。

**明确不做：** 不批准RT-D-001程序集最终形态，不为通过gate创建反向reference，不把测试assembly包含到release artifact。

- [ ] **Step 1: 生成solution。** 用`dotnet new sln --format slnx`创建并逐个加入已实现production/test/generated/reference projects；不要手工维护重复build-order列表。

- [ ] **Step 2: 写graph negative tests。** 测试明确拒绝：production→Testing/ReferenceHost/test packages、Coordination→Persistence、Replication→Command implementation、GeneratedContracts→任何Runtime module、production→Host/Voxel internal。

```csharp
[Theory]
[InlineData("Lumio.GameRuntime.Coordination", "Lumio.GameRuntime.Persistence")]
[InlineData("Lumio.GameRuntime.Replication", "Lumio.GameRuntime.Command")]
[InlineData("Lumio.GameRuntime.Ecs", "Lumio.GameRuntime.Testing")]
public void Forbidden_project_edge_is_absent(string source, string target)
{
    ProjectGraph graph = ProjectGraphLoader.LoadRepository();
    Assert.False(graph.HasEdge(source, target), $"Forbidden edge: {source} -> {target}");
}
```

- [ ] **Step 3: 写public surface scan。** 生产exported type/member签名出现`ILogger`、OpenTelemetry、MessagePack、Channel、Friflo、Arch、FileStream、AssemblyLoadContext、ReferenceHost、xUnit/CsCheck即失败；generated types和BCL稳定值类型允许。

- [ ] **Step 4: 写module completeness tests。** 生产逻辑模块project presence、namespace prefix、test project单向依赖、generated manifest baseline V1.3、Config无compile API、Simulation单RunTick、TickPhase exact13、participant四态。

- [ ] **Step 5: 先运行architecture tests并确认solution尚缺/graph失败。**

```bash
dotnet test tests/Lumio.GameRuntime.Architecture.Tests/Lumio.GameRuntime.Architecture.Tests.csproj
```

Expected: 在solution/loader完成前失败，错误指向缺project graph或违规边；不得skip。

- [ ] **Step 6: 实现graph loader和shell verifier。** 读取MSBuild静态project graph与assembly metadata；输出JSON到`artifacts/architecture/project-graph.json`和human summary。脚本任一违规退出`41`。

- [ ] **Step 7: 运行locked restore/build/test/gates。**

```bash
dotnet restore --locked-mode Lumio.GameRuntime.slnx
dotnet build Lumio.GameRuntime.slnx -c Release --no-restore
dotnet test Lumio.GameRuntime.slnx -c Release --no-build --report-trx --results-directory artifacts/test-results
bash eng/verify-generated-contracts.sh
bash eng/verify-project-graph.sh
bash eng/verify-dependencies.sh
bash eng/generate-sbom.sh
```

Expected: 全部退出`0`；tests `Failed: 0`；graph JSON无forbidden edges；SBOM仅含合法scope packages。

- [ ] **Step 8: 运行Foundation scenario两次。**

```bash
dotnet test modules/testing/tests/Lumio.GameRuntime.Testing.Tests/Lumio.GameRuntime.Testing.Tests.csproj -c Release --no-build --filter "ReferenceHostFoundationSliceTests|ReplayFirstDifferenceTests"
```

Expected: `Failed: 0`；64 Tick deterministic replay通过，mutation test精确定位首差异。

- [ ] **Step 9: 保存证据。** 将TRX、project graph、public surface report、generated manifest check、dependency report、SBOM、Foundation hash manifest放`artifacts/`；CI上传但不提交大体积binary artifacts。

- [ ] **Step 10: 提交。**

```bash
git add Lumio.GameRuntime.slnx tests/Lumio.GameRuntime.Architecture.Tests eng/verify-project-graph.sh eng/verify-project-graph.ps1
git commit -m "build: gate runtime foundation architecture"
```
