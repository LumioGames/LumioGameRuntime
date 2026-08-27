# LumioGameRuntime 11 模块实现设架与任务拆解

- 文档日期：2026-08-27
- 架构基线：`LGE-V1.3-2026-08-27`
- 设计状态：实现前框架设计；公共 API 候选均未冻结
- 适用仓库：`LumioGames/LumioGameRuntime`
- 交付范围：模块骨架、类型边界、Port/Adapter、依赖与线程、失败与恢复、测试面、任务卡与波次
- 排除范围：生产 C#、`.csproj`/`.sln`、NuGet 引入、公共 Schema/ID/ErrorCode 修改、模块 README 修改

> 本文把既有 11 个逻辑模块设架到实现 Agent 可以直接开工的精度。公共字段、状态机、Phase、Schema、ID 与错误分类以 `LumioGameEngineArchitecture` 为唯一来源；本文出现的 C# 类型名属于 Runtime 投影候选，除明确标注 generated contract 外均为“未冻结”。

## 0. 约束解释与证据优先级

### 0.1 冲突处理

1. 公共字段、状态机、Schema、Phase 矩阵、ID Registry：`LumioGameEngineArchitecture` 赢。
2. 模块边界、状态所有权、编译 DAG、Queue Contract Matrix、`RT-D-001..011`：本仓 `modules/README.md` 赢。
3. 开发流程、任务卡格式、测试和审查：本仓 `.spec/` 赢。
4. 本文不把历史 V1.0 Review 中已经被 V1.3 吸收的问题重新当作开放缺陷。
5. 本文不批准任何 RT-D。任务只收集证据，门的批准仍由仓库决策流程完成。

### 0.2 不可穿透边界

- Runtime 不拥有 Host Wall Clock、进程生命周期、Socket、Connection、Renderer、Release Pool、CoreCLR/ALC 创建、Voxel 内部 Storage、具体 Gameplay 内容。
- Server 与 Client 各自持有独立 World；LocalEmbedded 也不得共享可变对象引用。
- `testing` 只能单向引用生产模块；生产模块不得引用 `testing`。
- Diagnostic 事件不得代替 WAL、TxnJournal 或 CommandLog。
- 第三方类型只能存在于 Adapter 内部；稳定候选 API 只暴露本仓类型或 generated contract。
- V1 已有字段写入失败采用 Fail-stop；不增加字段级 Undo 或继续运行语义。
- `Prepared`/`CommitIntent` 之后禁止业务拒绝；只允许幂等 `Applied`/`AlreadyApplied` 或基础设施故障升级。

## 1. 第一步：边界校准表

| 模块 | 一句话职责 | 唯一拥有的状态 | 明确不拥有 | 编译依赖 | 运行时调用点 | 首批 | 对应 RT-D |
|---|---|---|---|---|---|---|---|
| `observability` | 将 Runtime 事件、Metric、Trace 与失败证据路由到 Diagnostic 或 Durable 出口 | 有界 Diagnostic 队列、事件批次、Metric/Trace 聚合、Failure Bundle 组装状态 | 权威业务状态、WAL/TxnJournal/CommandLog 存储、Host sink 生命周期 | generated contracts；BCL；Adapter 项目可依赖 MEL/OTel | 全 Phase 可写 Diagnostic；`SnapshotHashMetrics` 汇总；Fault 边界组装 Bundle | P1，但 Foundation 先落最小 Port | RT-D-009 |
| `config` | 验证已编译配置产物，执行固定六层合并并提供 Tick 内不可变 `ConfigSnapshot` | 已验证 table、六层合并结果、Staged/Active 快照引用 | 编译器、导入/defaults/content ref 解析、文件监听线程、玩法配置作者权 | generated config contracts、observability | Session 初始化；Tick Barrier 原子激活；各 Processor Tick 内只读 | P1，但 Foundation 先落不可变读取面 | RT-D-008 |
| `ecs` | 持有 World-local Entity/Component/Query/ChangeSet 的唯一真相 | `LocalEntityId`、Generation、Storage、Query view、ChangeSet、World 状态机 | Tick 调度、CrossWorldTxn、NetEntityId 生命周期、Socket、GAS 第二存储 | config、observability、generated contracts；内部 storage Adapter | `ApplyInputs`/Processor 执行读写已有字段；`EcsCommandBufferCommit` 结构提交；Snapshot 只读切面 | P0 | RT-D-002 |
| `command` | 为每个 Processor 提供独立 Buffer，稳定合并并形成不可再业务拒绝的 `PreparedGameDelta` | Buffer、Deferred Token、结构命令、合并序列、Prepared/Applied 结果 | Processor 调度、World 生命周期、Voxel commit、TxnJournal 存储 | ecs、observability、generated contracts | Processor Phase 生成；`CrossWorldPrepare` 前 Sealed/Merged/Prepared；`EcsCommandBufferCommit` Apply | P0 | RT-D-003 |
| `simulation` | 唯一拥有 Logical Tick、固定 13 Phase、Processor Plan 与 Determinism，并暴露单一 `run_tick` | `TickId`、Phase cursor、Plan、DeterminismContext、Session 生命周期 facade | Host Wall Clock、Revision/Txn 真相、Socket、ALC 创建、存储后端 | ecs、command、coordination、gas、replication、persistence、config、observability | Host 每逻辑 Tick 调用一次 `run_tick`；内部依固定 Phase 顺序调用各模块 | P0 | RT-D-001、RT-D-011 |
| `coordination` | 唯一实现 `CrossWorldTxnV1`、Reservation、Revision Vector 与 `SnapshotCut` | Txn、Reservation、`SessionRevisionVector`、`CommitIntent`、participant marker、SnapshotCut | WAL/Journal 介质、Voxel 内部存储、Tick 调度、Socket | ecs、command、generated voxel contract、observability | `CrossWorldPrepare`、`CommitDecision`、`VoxelCommit`、`EcsCommandBufferCommit`、Snapshot Barrier | P0 | RT-D-004 |
| `replication` | 持有 Mapping、Net/Local 映射、Baseline/Delta/History/Tombstone，并执行客户端六步 Apply | `ReplicationContext`、Mapping Registry、History、DirtySet、Tombstone、Apply 状态 | Socket/Connection、Renderer、Server/Client 合并 World、Voxel 内部 storage | ecs、gas、coordination、config、observability、Generated Voxel Replica Contract；command sequence 走中立 generated contract | `ReplicationProjection`；客户端 Barrier 执行六步 authoritative apply；`EgressPublish` 交 Host | P0 | RT-D-005 |
| `gas` | 提供通用 Ability/Effect 状态机、句柄、求值、预测与 ECS 投影 | Type/Instance/Handle registry、执行上下文、PredictionFrame 索引 | 第二份权威 Attribute/Tag 存储、具体公式/Cost/Targeting、网络连接 | ecs、command、config、observability | Processor Plan 中执行；`GasAndEventFinalize` 收敛；客户端 Apply/Replay 参与原子单元 | P1 | RT-D-006 |
| `persistence` | 对同一 Revision Cut 做 Canonical encode/decode、Staging/Activate 与恢复记录 Adapter | 编解码状态、Snapshot staging/activation、Recovery cursor、durable adapter 状态 | Host 存储介质实现、Diagnostic sink、Voxel storage、Txn 决策 | ecs、gas、replication、coordination、config、observability、Generated Voxel Snapshot Contract | Snapshot Barrier 捕获；`SnapshotHashMetrics` 编码/Hash；启动 Recovery；Durable route 消费 | P1 | RT-D-007 |
| `hot-reload` | 管理 `GameplayModuleScope`、六步卸载和双 Scope `BarrierSwitch` | Scope、Lease、Drain/Root validation、Old/New scope 切换状态 | CoreCLR/ALC 创建、玩法实现、Host 进程生命周期、公共兼容策略 | simulation、gas、config、observability | Tick Barrier 前验证 NewStaging；Barrier 原子切换；旧 Scope 六步卸载 | P1 | RT-D-010 |
| `testing` | 提供不进生产依赖的 Reference Host、ReferenceVoxelPort、Replay/Hash、Scenario/Fault Adapter | 测试时钟、输入脚本、故障脚本、Replay evidence、Workload 结果 | 生产权威状态、旁路 Envelope、生产 Sink、发布 ABI | 单向依赖全部生产模块与 generated fixtures | 测试进程构造；逐 Tick 驱动；故障边界捕获；Differential/Replay/Soak | P1；Reference Host 骨架可在 Foundation 提前 | RT-D-011 |

### 1.1 三视图一致性

**编译视图。** 生产模块只沿上述 DAG 单向引用。Generated Voxel Replica/Snapshot Contract 与中立 generated-contract 程序集不是新的逻辑模块；它们是外部契约产物。`persistence` 可以实现 `coordination` 所声明的 `ITxnJournalPort`，因为依赖方向是 `persistence -> coordination`，不会形成环。

**运行时调用视图。** `simulation` 是 13 相编排者，不因此拥有被调用模块的状态。`SimulationSession` 只聚合 facade；它不得缓存 Revision、Txn、Mapping 或 GAS 权威字段的第二份可变副本。

**状态所有权视图。** 每个可变状态只有一个模块拥有者。跨模块读取通过 immutable view、snapshot、prepared token 或 generated contract；跨线程只传不可变批次或 bounded completion。

## 2. 第二步：全仓成熟方案选型

### 2.1 决策阶梯

每项能力依次执行：标准库 → 成熟 OSS → 组合/配置/上游修复 → Adapter 内最小补丁 → 领域专属最小实现。任何领域专属实现都必须有可替换 Port、Golden/Property/Benchmark 证据和退出路径。

### 2.2 全仓固定工程策略

- 首次工程卡固定 `.NET SDK 10.0.11`、C# 14、nullable enabled、warnings-as-errors、deterministic build。
- 生产程序集建议多目标 `net10.0;netstandard2.1`；Server/Reference Host 运行 `net10.0`，Unity 消费 `netstandard2.1` 投影。无法在 `netstandard2.1` 编译的 Adapter 独立成 Host-only 项目。
- 测试、Benchmark、Fuzz 工程只目标 `net10.0`；另有 NativeAOT publish smoke 与 Unity/IL2CPP compile harness。
- 中央包管理使用 `Directory.Packages.props`，所有包精确锁定稳定 patch；启用 `packages.lock.json` 和 locked restore。升级由独立依赖卡完成，不使用浮动版本。
- 发布流水线生成 CycloneDX SBOM、执行 transitive vulnerability audit、保存许可证清单和包哈希。SBOM 工具不进入生产依赖。
- Generated contract 只由架构源工具链生成；生成物目录只读，禁止手改。

### 2.3 选型总表

| 能力 | 候选 | 采用结论 | 包/工具 | 许可证 | Adapter/边界 | 稳定 API 可见 | 主要否决或风险 |
|---|---|---|---|---|---|---|---|
| SDK/语言/nullable | .NET 10 LTS；.NET 9 STS；旧 LTS | .NET 10.0.11 + C# 14；稳定 surface 保持 `netstandard2.1` 子集 | SDK/BCL | MIT | `RuntimeTargetProfile` 仅为构建配置，不进业务 API | 只见 BCL/Runtime 类型 | Unity 不直接运行 .NET Core；必须用 `netstandard2.1` + IL2CPP harness 证明 |
| 构建/测试/覆盖率 | `dotnet test` + xUnit v3；NUnit；MSTest | xUnit v3 + Microsoft Testing Platform + Coverlet MTP | `xunit.v3`、`xunit.analyzers`、`Microsoft.Testing.Platform`、`coverlet.MTP` | Apache-2.0/MIT | 仅测试工程 | 否 | xUnit v3 目标 .NET 8+，故测试不多目标 Unity |
| Property/Fuzz | CsCheck；FsCheck；SharpFuzz | CsCheck 为默认 Property；SharpFuzz 仅 Hardening | `CsCheck`、`SharpFuzz` | Apache-2.0/MIT | Test/Fuzz harness | 否 | FsCheck 引入 FSharp.Core 且 C# 维护成本更高；递归 generator 需 AOT 限制 |
| Benchmark | BenchmarkDotNet；Stopwatch 自测 | BenchmarkDotNet | `BenchmarkDotNet` | MIT | 独立 benchmarks 项目 | 否 | 未运行的数字不得写进承诺；固定 workload/hardware/TickBudget 才有决策效力 |
| 日志门面 | MEL；Serilog；NLog | `Microsoft.Extensions.Logging.Abstractions`，仅由 Adapter 接入 Provider | `Microsoft.Extensions.Logging.Abstractions` | MIT | `MicrosoftLoggingAdapter` | 否；稳定面只见 `RuntimeEvent`/generated `LoggingEvent` | Serilog/NLog 是 Provider 选择，不由 Runtime 固定；日志不得替代 durable record |
| Metrics/Trace | OpenTelemetry；EventSource；供应商 SDK | OpenTelemetry API/SDK，exporter 由 Host profile 选择 | `OpenTelemetry.Api`、`OpenTelemetry`；OTLP exporter 仅 Host adapter | Apache-2.0 | `OpenTelemetryMetricsAdapter`、`OpenTelemetryTraceAdapter` | 否 | Diagnostic 可采样且不得进入 State Hash；自动探针不用于 Unity/NativeAOT 核心路径 |
| 有界队列 | `System.Threading.Channels`；TPL Dataflow；自研 ring | `System.Threading.Channels` | BCL/`System.Threading.Channels` | MIT | `BoundedChannelAdapter<T>` | 否；API 只见 Port 和预算 | FullMode 必须由 Queue Contract Matrix 显式映射，禁止使用默认行为 |
| 对象池/无分配 | `ArrayPool<T>`/`MemoryPool<T>`；ObjectPool；CommunityToolkit | 首选 BCL 池；只有 Benchmark 证明后才用 `Microsoft.Extensions.ObjectPool` | BCL；可选 `Microsoft.Extensions.ObjectPool` | MIT | `BufferLeasePool`/`ObjectPoolAdapter<T>` | 否 | 池对象地址、租借顺序、容量不得进入 Hash；Return 后引用必须失效 |
| Immutable collections | BCL immutable；自研 persistent collection | `System.Collections.Immutable` 用于快照索引；热路径按 Benchmark 选择数组/Span | `System.Collections.Immutable` | MIT | 无第三方；模块内部 | BCL 可见，但稳定面优先 `IReadOnlyList`/自有 view | 分配和遍历顺序需测；Dictionary 迭代顺序不可成为 canonical 顺序 |
| ECS storage | Friflo；Arch；DefaultEcs；最小自研 storage | `IWorldStorageAdapter` 固定；Friflo 为首选候选，Arch 为比较 Adapter；RT-D-002 由同一套测试/Benchmark 批准 | `Friflo.Engine.ECS`；benchmark-only `Arch` | MIT/Apache-2.0 | `FrifloWorldStorageAdapter`、`ArchWorldStorageBenchmarkAdapter` | 否 | 第三方 Entity/Query/World 全部隐藏；遍历顺序、AOT、Generation、结构变更和 NativeAOT issue 必须实测 |
| Canonical binary codec | MessagePack；MemoryPack；protobuf-net；手写全栈 | MessagePack primitive reader/writer 作底层；Runtime 自己只实现 schema 顺序、标量归一化和长度预算 | `MessagePack` | MIT | `MessagePackCanonicalCodecAdapter` | 否 | 禁止 Typeless/Contractless/global resolver；MemoryPack 的 C# 内存布局和 Unity 差异不适合作为公共 canonical 格式 |
| 压缩 | Brotli；ZstdSharp；LZ4 | BCL Brotli 为首个 Adapter；算法 ID 必须来自 generated contract；Zstd/LZ4 只作后续证据候选 | `System.IO.Compression` | MIT | `BrotliCompressionAdapter` | 否 | 解压比、输出上限和 CPU 预算必须配置；不得从输入长度推断安全分配 |
| JSON Schema/契约校验 | 既有 Contract Toolchain；Corvus；NJsonSchema；JsonSchema.Net | 生产 Runtime 不加 JSON Schema 引擎；工具/测试采用 Corvus sourcegen/CLI 镜像验证 | `Corvus.Text.Json.SourceGenerator`/CLI | Apache-2.0 | `ContractFixtureValidatorAdapter` | 否 | JsonSchema.Net 当前二进制 EULA 对营收组织有额外义务，排除；NJsonSchema 依赖 Json.NET/反射，保留替代 |
| JSON 诊断 | `System.Text.Json`；Newtonsoft.Json | `System.Text.Json` 只用于人类可读诊断、fixture 和工具 | BCL | MIT | `DiagnosticJsonAdapter` | 否 | 文本 JSON 不进权威热路径，不作为 Snapshot/WAL canonical bytes |
| DI/Composition | 手写 root；MS DI；Autofac | Runtime 使用显式手写 Composition Root；Host 可在边界使用 MS DI | 可选 `Microsoft.Extensions.DependencyInjection.Abstractions`（Host-only） | MIT | `RuntimeCompositionRoot` | 否 | 不自研容器；不允许 ServiceLocator、ambient static 或容器类型泄漏 |
| Source Generator | Roslyn incremental；T4；运行时反射 | 既有 Contract Toolchain + Roslyn incremental generator | `Microsoft.CodeAnalysis.CSharp`、`Microsoft.CodeAnalysis.Analyzers`（tool-only） | MIT | `GeneratedContracts` 只读输出 | generated 自有类型可见 | 生成顺序和输出 hash 必须稳定；运行时禁止 Reflection.Emit fallback |
| Hash | `System.IO.Hashing`；SHA-2；xxHash OSS | 算法由公共 Schema/ID 决定；BCL 实现经 `ICanonicalHashAdapter` | BCL | MIT | `CanonicalHashAdapter` | 否 | 不在 Runtime 私造算法 ID；对象地址、缓存、队列时序、Dictionary 顺序不进 Hash |
| SBOM/供应链 | CycloneDX；Microsoft sbom-tool | CI 使用 CycloneDX .NET tool；SPDX 可由 release 流程另产 | `CycloneDX` dotnet tool | Apache-2.0 | Build tool only | 否 | 锁定 tool 版本；SBOM 不是运行时依赖；许可证解析失败必须阻断发布 |
| DIAGNOSTIC Provider | Console；OTLP；文件；供应商 | Runtime 不固定 Provider；Reference Host 用 InMemory，生产由 Host 选择 | Host packages | 各 Provider 单独审查 | `IEventSinkAdapter` | 否 | 外部 sink 属公共 D-008/RT-D-009 证据，不在此偷渡批准 |

### 2.4 自研最小范围与退出路径

| 领域专属语义 | 允许自研的最小文件范围 | 为什么成熟库不能直接给出 | 退出路径 |
|---|---|---|---|
| 13 相 Tick | `simulation/Phases/*`、`TickRunner.cs`、`DeterminismContext.cs` | 通用 scheduler 不知道公共 Phase、可见性和 Fail-stop 矩阵 | Phase engine 只依 Port；未来可替换执行器，不改变 public phase contract |
| CommandBuffer 语义 | `command/Buffers/*`、`Merge/*`、`Prepare/*` | 第三方 ECS CommandBuffer 不保证本仓排序键与 Prepared 后不可拒绝 | 存储适配在 `IWorldStorageAdapter`；语义层保持不变 |
| CrossWorldTxnV1 | `coordination/Transactions/*`、`Reservations/*` | 通用 XA/2PC 过重且不能表达固定 Voxel→ECS、participant marker 与 Revision | Journal/participant 均为 Port，可替换介质和 Voxel binding |
| Canonical record ordering | `persistence/Codec/CanonicalRecordWriter.cs`、`CanonicalRecordReader.cs` | 通用 codec 不拥有架构 Schema 的字段顺序、版本和 Hash cut | 底层 primitive codec 可换；Golden bytes 固定行为 |
| Tombstone horizon | `replication/Tombstones/*` | 公式跨 Baseline/History/Reconnect/Prediction/Migration pin，属本仓语义 | 各 horizon 以只读 Port 提供，可替换 storage |
| GAS/ECS 单一真相 | `gas/Projection/*`、`Prediction/*` | 通用 GAS 往往拥有自己的属性存储；与本仓单一真相冲突 | 状态机和 projection Port 分离；可换 evaluator，不换 ECS authority |
| SnapshotCut | `coordination/Snapshots/*` | 通用 snapshot 库不知道跨 ECS/GAS/Voxel Revision cut | Provider 集合可扩展但字段来自 generated contract |
| Hot Reload 双 Scope | `hot-reload/Scopes/*`、`Barrier/*` | ALC API 只提供加载机制，不提供六步卸载和切换后的唯一恢复动作 | Host load/unload 是 Port；Scope state machine 保持 |

### 2.5 依赖准入清单

每个实现 PR 必须附：精确版本、许可证 SPDX、直接/传递依赖、AOT/Unity profile 结果、漏洞扫描、SBOM diff、determinism 影响、性能证据、Adapter 边界和卸载/替换方法。GPL/AGPL/SSPL 或含营收限制的 binary EULA 默认拒绝；例外必须单独法务审核并给出 permissive 替代。

---

## 3.1. `observability` 模块设架

### 0. 模块身份证

- 目录：`modules/observability/`
- 建议程序集：`Lumio.GameRuntime.Observability`；Provider 放 `Lumio.GameRuntime.Observability.Adapters`
- 建议命名空间：`Lumio.GameRuntime.Observability`
- 优先级与阶段：P1，Vertical Slice / Production Hardening；最小 Port 与队列在 Foundation 先落
- 唯一职责：在不阻塞 Simulation Owner Thread 的前提下，为所有模块提供可关联、可采样、可耐久分流且可重放的证据出口。
- RT-D-001 纪律：以上是物理映射建议；即使首期合并程序集，也必须保持命名空间、internal 可见性和依赖测试，不得形成反向引用。

### 1. 它做什么

- `RuntimeCompositionRoot` 构造一个 `ObservabilityModule`；Producer 注册和队列随 Runtime 实例存活，`FailureBundleAssembler` 按 Session/World 建立轻量上下文。Host 持有最终 Sink Adapter 并负责 Flush/Shutdown。
- 任意 Runtime 线程可以提交已经复制成受限值对象的 Event/Metric/Trace；模块为每个 Producer 分配单调 `EventSeq`，做长度、Correlation、PII 策略和类别校验，再路由到 Diagnostic 或 Durable 通道。
- 每个 Tick 可产生多条事件，但 `SnapshotHashMetrics` 只读取无副作用的指标快照；Metric、Trace、时间戳、队列水位和 Sink 延迟均不进入权威 State Hash。
- Diagnostic 队列满载时按采样策略丢弃并发出聚合 DropSummary；Audit/Txn/Command durable route 满载时返回不可忽略的背压结果，调用方停止 authoritative ingress 或进入维护。
- 发生 Fault 时，Assembler 从 immutable context、最近有效 Snapshot/Manifest、durable cursor 和 artifact hash 组装 Failure Bundle；首个 Snapshot 前使用公共 `noSnapshotReason + bootstrapPhase + lastKnownRevision/manifest` 表达，不伪造 `SnapshotId`。
- 成功意味着事件被有效分类并取得确定的 `ProducerId + EventSeq`，或明确返回已采样/已拒绝；Failure Bundle 成功意味着全部引用/hash 通过校验并被可靠出口接受。

### 2. 它明确不做什么

- 不实现文件、控制台、OTLP、云平台或供应商 Sink；由 Host Adapter 负责。
- 不拥有日志轮转、保留、上传凭据和最终 PII 政策；由部署与 Host 安全层负责。
- 不把 Diagnostic 队列或 `LoggingEvent` 当作 WAL、TxnJournal、CommandLog；耐久恢复语义归 `persistence`。
- 不决定 World/Session/Process 的故障升级动作；只输出分类证据，处置归 `simulation`/Host。
- 不等待远端 Sink，不在持有 World、Native 或 Storage 锁时同步 Flush。
- 不把 Stopwatch、wall-clock 或线程调度写入 authoritative hash。
- 不回调 ECS、GAS、Txn 或 Gameplay 改变业务状态。

### 3. 代码骨架

#### 3.1 建议目录树

```text
modules/observability/
├─ src/Lumio.GameRuntime.Observability/
│  ├─ ObservabilityModule.cs                 # 模块门面与生命周期，不持有业务状态
│  ├─ ObservabilityServices.cs               # 只读 Port 集合与 producer factory
│  ├─ Lifecycle/ObservabilityState.cs        # Created/Configured/Running/Degraded/Flushing/Closed/Faulted
│  ├─ Contracts/IRuntimeEventPort.cs         # Event 入队候选 API
│  ├─ Contracts/IMetricPort.cs               # Metric 记录 Port
│  ├─ Contracts/ITracePort.cs                # Trace/span Port
│  ├─ Contracts/IDurableEvidencePort.cs      # durable record 路由，不定义存储
│  ├─ Context/EventCorrelationView.cs        # generated correlation 的只读投影
│  ├─ Context/ProducerSequence.cs             # ProducerId/EventSeq 单调分配
│  ├─ Queues/DiagnosticEventQueue.cs          # Diagnostic bounded queue
│  ├─ Queues/DiagnosticQueueBudget.cs         # 容量/批/字节预算
│  ├─ Routing/EventRouter.cs                  # category/durability 路由
│  ├─ Routing/DurableEvidenceRouter.cs        # durable route adapter facade
│  ├─ Failure/FailureBundleAssembler.cs       # Failure Bundle 组装和引用校验
│  ├─ Failure/FailureContextSnapshot.cs       # 无可变 World 引用的故障切面
│  └─ Errors/ObservabilityFailure.cs          # Rejected/Retryable/Fatal 内部分类
├─ src/Lumio.GameRuntime.Observability.Adapters/
│  ├─ MicrosoftLoggingAdapter.cs              # MEL provider 包装
│  ├─ OpenTelemetryMetricsAdapter.cs          # OTel Metric 包装
│  ├─ OpenTelemetryTraceAdapter.cs            # OTel Trace 包装
│  └─ BoundedChannelAdapter.cs                # Channel<T> 包装
└─ tests/Lumio.GameRuntime.Observability.Tests/
   ├─ ProducerSequenceTests.cs
   ├─ DiagnosticBackpressureTests.cs
   ├─ DurableRouteFailureTests.cs
   ├─ FailureBundleGoldenTests.cs
   └─ ShutdownRaceTests.cs
```

#### 3.2 核心类型

| 类型/可见性 | 职责与关键字段/不变式 | 关键方法草图与失败表达 | 线程与生命周期 |
|---|---|---|---|
| `ObservabilityModule` / `sealed class` / internal constructor | 拥有 `State`、producer registry、Diagnostic queue、durable router 和 assembler；不缓存 ECS/Txn 状态。 | `Configure(ObservabilityOptionsView)`、`Start()`、`BeginFlush(FlushDeadline)`、`Close()`；返回 `ObservabilityResult`，非法迁移为 Rejected。 | 多 producer；状态迁移由 Host control thread 串行；Close 后所有 emit 拒绝。 |
| `ObservabilityServices` / `sealed class` / 稳定候选，未冻结 | 只读聚合 `Events`、`Metrics`、`Traces`、`FailureBundles`；字段初始化后不可替换。 | 仅 getter；不暴露 queue/provider 实例。 | 与 module 同寿命；并发只读。 |
| `IRuntimeEventPort` / `interface` / 稳定候选，未冻结 | 接收本仓 `RuntimeEventView`；Correlation 必须完整，payload 已受限。 | `EventEnqueueResult Emit(in RuntimeEventView value)`；结果区分 Accepted/Sampled/Rejected/Backpressured，枚举最终由 generated contract 投影。 | 任意线程；不得阻塞远端 I/O。 |
| `IMetricPort` / `interface` / Port | 接收预注册 MetricId 与标量；标签只允许 generated key。 | `MetricRecordResult Record(in MetricSampleView sample)`、`MetricSnapshot CaptureSnapshot()`。 | 并发生产；snapshot 只读，Dispose 后拒绝。 |
| `ITracePort` / `interface` / Port | 创建 Runtime 自有 `TraceScope`，内部映射 OTel Activity。 | `TraceScope Start(in TraceStartView start)`；Dispose scope 完成 span。 | 任意线程；TraceScope 不得跨 Scope/World 生命周期。 |
| `ProducerSequence` / `sealed class` / internal | 字段 `ProducerId`、`long nextEventSeq`；每 Producer 单调且不回退。 | `EventSequence Next()` 使用 checked increment；溢出为 Fatal。 | 并发安全；module Close 时销毁。 |
| `DiagnosticEventQueue` / `sealed class` / internal | BCL bounded channel；保存 immutable `DiagnosticEnvelope`，记录 bytes/depth/drop summary。 | `TryWrite`、`ReadBatchAsync`、`Complete`；full action 显式映射。 | 多生产/单消费；Flushing 后不接收新项。 |
| `IDurableEvidencePort` / `interface` / Port | 只接 generated `DurableRecordEnvelope` 与 IdempotencyKey；不定义介质。 | `DurableEnqueueResult Enqueue(in DurableRecordView record)`、`DurableQueryResult Query(IdempotencyKey key)`。 | 调用方可多线程；实现须幂等；由 persistence/Host dispose。 |
| `FailureBundleAssembler` / `sealed class` / internal | 字段为 immutable manifest/snapshot/artifact refs、lastKnownRevision、durable cursors；禁止可变 World。 | `FailureAssemblyResult Assemble(in FailureContextSnapshot context)`、`Verify(in FailureBundleView bundle)`。 | Fault path 单 writer；可并发读取 artifact bytes；导出完成后释放 leases。 |
| `ObservabilityFailure` / `readonly record struct` / internal | `FailureClass`、generated `ContractErrorRef`、retry/deadline/evidence ref；不新增公共错误码。 | 工厂 `Rejected`/`Retryable`/`Fatal`；映射到 generated result。 | 不可变值对象。 |

#### 3.3 稳定候选 API 与内部边界

- 稳定候选接口只接本仓 view/generated contract；`ILogger`、`Activity`、OTel Meter、Channel、Provider 类型均不可见。
- `emit`/`record_metric`/`start_span`/`append_failure_artifact`/`flush`/`snapshot_context` 是语义候选；C# PascalCase 只是语言投影，不发布第二套字段。
- Error/Fatal emergency path只允许写本地受限 sink 或 durable port，不得在 World lock 下等待。

```csharp
// 设计草图；未冻结。所有 *View 的字段来自 generated contract 或本仓内部投影。
public interface IRuntimeEventPort
{
    EventEnqueueResult Emit(in RuntimeEventView value);
}

public interface IMetricPort
{
    MetricRecordResult Record(in MetricSampleView sample);
    MetricSnapshot CaptureSnapshot();
}

public interface IFailureBundlePort
{
    FailureAssemblyResult Assemble(in FailureContextSnapshot context);
    DurableEnqueueResult Export(in FailureBundleView bundle);
}
```

### 4. 与成熟方案怎么接

| 能力 | 采用 | 不采用 | Adapter | 稳定 API 可见第三方类型 | 许可证 | 风险/控制 |
|---|---|---|---|---|---|---|
| 日志门面 | Microsoft.Extensions.Logging.Abstractions | 直接暴露 ILogger；Runtime 固定 Serilog/NLog | MicrosoftLoggingAdapter | 否 | MIT | Adapter 只消费 RuntimeEvent；Provider 与 sink 由 Host 注入。 |
| Metric/Trace | OpenTelemetry .NET API/SDK | 供应商专有 SDK 进入 core | OpenTelemetryMetricsAdapter / OpenTelemetryTraceAdapter | 否 | Apache-2.0 | 仅 Diagnostic；sampling/exporter 状态不进 Hash。 |
| 有界队列 | System.Threading.Channels | 自研通用 ring buffer/TPL Dataflow | BoundedChannelAdapter<T> | 否 | MIT | 显式 SingleReader/FullMode/容量/完成语义；AOT smoke。 |
| 批缓冲 | ArrayPool<T> / MemoryPool<T> | 长期缓存任意 pooled object | BufferLeasePool | 否 | MIT | Return 后引用失效；池状态不进 Hash。 |
| Failure Bundle | 公共 Schema + 本仓 assembler | 把普通 log zip 当 bundle | FailureBundleAssembler | 否 | 纯领域语义 | Assembler 只组装/校验，不实现文件系统。 |

**自研最小范围。** 仅实现 EventSeq、类别/耐久路由、Failure Bundle 公共语义和 bounded policy 映射；通用日志 SDK、Metric SDK、Trace SDK、队列和池全部复用成熟方案。替换 Provider 不改变 Port。

### 5. 输入 / 输出 / 依赖

**Consumes**

- 所有模块产生的 `RuntimeEventView`、`MetricSampleView`、`TraceStartView`。
- `ConfigSnapshot` 中的采样/容量/批量/Flush 策略只读视图（启动最小配置可由 Host bootstrap capability 提供）。
- Host 提供的 `IEventSinkAdapter`、`IDurableEvidencePort`、`IPiiPolicyAdapter`。
- Fault 边界的 `FailureContextSnapshot`，包含 generated correlation、Snapshot/Manifest 引用或 `noSnapshotReason`。

**Produces**

- 给 Host 的 `DiagnosticEventBatch`、`MetricBatch`、`TraceBatch`。
- 给 persistence/Host durable route 的 generated `DurableRecordEnvelope`。
- 给 testing/运维的 generated `FailureBundleView` 与验证结果。
- 给所有调用方的 `EventEnqueueResult`、`DurableEnqueueResult` 和 queue snapshot。

**编译依赖**

- 中立 generated-contract 程序集。
- BCL；Adapter 项目单向依赖 MEL/OpenTelemetry。
- 不依赖其他业务模块；Config 通过启动时只读 options/value projection 注入，避免反向依赖。

**禁止依赖**

- `ecs`、`simulation`、`coordination`、`gas`、`replication`、`persistence` 实现程序集。
- Host 的具体 sink/provider 包。
- `testing`。
- 文件系统/Socket/Connection 具体实现。

### 6. 队列、预算、线程

| 实现对象 | 元素/资源 | 容量/预算来源 | 满载/超限动作 | 顺序/幂等键 | Metric |
|---|---|---|---|---|---|
| `DiagnosticEventQueue` | `DiagnosticEnvelope`/字节 | `DiagnosticQueueCapacity` | 按已批准采样策略丢弃；必须增加 DropSummary，Fatal 可走 emergency port | `ProducerId + EventSeq` | `runtime.diagnostic.queue.depth`、`runtime.diagnostic.dropped` |
| `DurableEvidenceRouter` | Audit/Txn/Command generated durable record | `DurableLogQueueCapacity` | 不丢弃；返回 Backpressured，调用方停止 ingress/进入维护 | `IdempotencyKey`、`RecordSeq` | `runtime.durable.queue.depth`、`runtime.durable.backpressure` |
| `FailureBundleArtifactBudget` | artifact refs/bytes | 公共 FailureBundle limits + Host capability | 拒绝新增非必要 artifact；核心 manifest/hash 失败升级 Fatal | `ArtifactHash` | `runtime.failure_bundle.bytes`、`runtime.failure_bundle.verify_failures` |

- Producer 可并发，输入在入队前复制为值对象；禁止保存外部可变 buffer。
- Diagnostic consumer 和 exporter worker 不得直接写 World；Flush 由 Host control thread 编排。
- Error/Fatal 同步应急路径必须设 deadline，不能持有 World/Native 锁。

### 7. 失败与恢复

| 分类 | 触发例 | 返回/可观察结果 | World/Session | Failure Bundle | Journal/证据查询 |
|---|---|---|---|---|---|
| 可拒绝 | 缺失 generated Correlation、非法类别、字段超长 | `Rejected(ContractErrorRef)`；不分配 EventSeq 或记录拒绝计数 | 不 Fault | 通常不生成；安全事件可生成最小证据 | 查询 reject counter 与 producer diagnostic |
| 可拒绝 | PII 策略拒绝未脱敏字段 | `Rejected`，字段不入队 | 不 Fault | 必要时最小安全 Bundle fragment | PII policy audit ref |
| 可拒绝 | Closed 后 emit/非法 lifecycle transition | `RejectedClosed`/状态错误 | 不 Fault | 否 | module lifecycle event |
| 可重试 | Diagnostic sink 瞬时失败 | 保持 `ProducerId + EventSeq`，批次按 retry policy 重试或采样 | 不 Fault；达到 deadline 后 Degraded | 仅持续失败时 | sink attempt log + batch id |
| 可重试 | Durable adapter 暂时 Backpressured | 返回 Backpressured；保留 IdempotencyKey | Session 暂停 ingress；未立刻 Fault | 升级时需要 | durable port Query(IdempotencyKey) |
| 可重试 | Failure artifact 尚未可读 | Assembler 返回 Retryable 和 deadline | 不 Fault；原 Fault 状态由调用方保持 | 正在组装 | artifact ref/query result |
| 可致命 | Audit/Txn/Command durable route 无法保证耐久 | `Fatal`；禁止确认成功 | Session/Host 进入维护或 Faulted | 必须 | durable cursor/IdempotencyKey/last successful RecordSeq |
| 可致命 | EventSeq 回退/重复或内部路由不变量破坏 | `FatalInvariant` | Runtime 实例 Faulted | 必须 | producer registry snapshot |
| 可致命 | Failure Bundle hash/manifest 自相矛盾 | Export 失败并保留原始证据 | 相关 Session Faulted；不得伪造可重放性 | 必须产生 emergency evidence | artifact hash、manifest verify report |

### 8. 测试面

**本模块测试工程—单元**

- 状态机只允许 `Created -> Configured -> Running -> Flushing -> Closed` 与规定 Degraded/Faulted 路径。
- 每 Producer 并发 Emit 后 EventSeq 单调、唯一且关闭后拒绝。
- Diagnostic full action 产生准确 DropSummary，不影响 Durable route。

**本模块测试工程—Golden**

- 架构源 logging-event 正/反例；字段名、category、durability、correlation 不被 Adapter 改写。
- 有/无有效 Snapshot 的 FailureBundle Golden；`noSnapshotReason` 路径不得出现伪造 SnapshotId。

**本模块测试工程—Property**

- 任意 producer interleaving 下每个 Producer 序列单调；跨 producer 不要求全局顺序。
- 任意采样策略下 Accepted + Sampled + Rejected + DroppedSummary 与输入计数守恒。

**本模块测试工程—故障**

- Diagnostic QueueFull、Durable QueueFull、sink timeout、disk full adapter、shutdown race、artifact hash mismatch。

**`testing` Reference Host**

- InMemory sink 捕获完整 Tick/Txn correlation；Failure Bundle 能驱动 Replay Runner 到首个差异。

### 9. 本模块任务拆解

#### `obs-event-ports-and-context`

- **一句话目标**：落成无第三方类型泄漏的 Event/Metric/Trace Port、Correlation view 与 Producer sequence 语义。
- **涉及文件集**：
  - `modules/observability/src/Lumio.GameRuntime.Observability/Lumio.GameRuntime.Observability.csproj`
  - `modules/observability/tests/Lumio.GameRuntime.Observability.Tests/Lumio.GameRuntime.Observability.Tests.csproj`
  - `modules/observability/src/Lumio.GameRuntime.Observability/ObservabilityModule.cs`
  - `modules/observability/src/Lumio.GameRuntime.Observability/ObservabilityServices.cs`
  - `modules/observability/src/Lumio.GameRuntime.Observability/Contracts/IRuntimeEventPort.cs`
  - `modules/observability/src/Lumio.GameRuntime.Observability/Contracts/IMetricPort.cs`
  - `modules/observability/src/Lumio.GameRuntime.Observability/Contracts/ITracePort.cs`
  - `modules/observability/src/Lumio.GameRuntime.Observability/Context/ProducerSequence.cs`
  - `modules/observability/tests/Lumio.GameRuntime.Observability.Tests/ProducerSequenceTests.cs`
- **验收标准**：
  - [ ] 公共候选签名中搜索不到 `ILogger`、`Activity`、OpenTelemetry 或 Channel 类型。
  - [ ] 并发 Property 测试证明每 Producer EventSeq 单调且唯一。
  - [ ] 缺失必需 Correlation 的事件返回 generated error ref，不入队。
  - [ ] 生命周期测试覆盖 Running、Flushing、Closed 与 Faulted。
- **依赖**：`repo-generated-contract-boundary`
- **Consumes**：generated `LoggingEvent`/Correlation 类型。
- **Produces**：`IRuntimeEventPort`、`IMetricPort`、`ITracePort`、`ObservabilityServices`。
- **成熟方案**：BCL 并发原语；无第三方 API。
- **明确不做**：不接任何具体 Provider，不定义公共字段或错误码。

#### `obs-bounded-diagnostic-routing`

- **一句话目标**：实现 Queue Contract Matrix 对应的有界 Diagnostic 路由与可核对的采样/丢弃摘要。
- **涉及文件集**：
  - `modules/observability/src/Lumio.GameRuntime.Observability/Queues/DiagnosticEventQueue.cs`
  - `modules/observability/src/Lumio.GameRuntime.Observability/Queues/DiagnosticQueueBudget.cs`
  - `modules/observability/src/Lumio.GameRuntime.Observability/Routing/EventRouter.cs`
  - `modules/observability/src/Lumio.GameRuntime.Observability.Adapters/BoundedChannelAdapter.cs`
  - `modules/observability/tests/Lumio.GameRuntime.Observability.Tests/DiagnosticBackpressureTests.cs`
- **验收标准**：
  - [ ] 容量只从 `DiagnosticQueueCapacity` 投影读取，代码中没有未经批准的 magic capacity。
  - [ ] QueueFull 只影响 Diagnostic，并产生含原因/数量的 DropSummary。
  - [ ] 压力测试中队列深度不超过容量且 producer 不等待 sink I/O。
  - [ ] 关闭竞态没有丢失 Accepted 事件而不计数的路径。
- **依赖**：`obs-event-ports-and-context`
- **Consumes**：`RuntimeEventView`、`DiagnosticQueueCapacity`。
- **Produces**：`DiagnosticEventBatch`、queue metrics、DropSummary。
- **成熟方案**：System.Threading.Channels。
- **明确不做**：不实现 durable queue，不实现通用 event bus。

#### `obs-durable-route-and-emergency-path`

- **一句话目标**：建立与 Diagnostic 完全分离的 durable evidence Port、背压和 Fatal emergency 路径。
- **涉及文件集**：
  - `modules/observability/src/Lumio.GameRuntime.Observability/Contracts/IDurableEvidencePort.cs`
  - `modules/observability/src/Lumio.GameRuntime.Observability/Routing/DurableEvidenceRouter.cs`
  - `modules/observability/src/Lumio.GameRuntime.Observability/Errors/ObservabilityFailure.cs`
  - `modules/observability/tests/Lumio.GameRuntime.Observability.Tests/DurableRouteFailureTests.cs`
- **验收标准**：
  - [ ] Audit/Txn/Command QueueFull 从不返回 Accepted 或 SilentDrop。
  - [ ] 重复 IdempotencyKey 的查询/重试得到同一 durable result。
  - [ ] Fatal emergency path 有 deadline 且测试证明不持有 World lock。
  - [ ] Diagnostic provider 失败不改变 durable route 状态。
- **依赖**：`obs-event-ports-and-context`
- **Consumes**：generated `DurableRecordEnvelope`、`DurableLogQueueCapacity`。
- **Produces**：`IDurableEvidencePort`、`DurableEnqueueResult`。
- **成熟方案**：Port 语义；介质由 persistence/Host 实现。
- **明确不做**：不实现 WAL/Journal 存储与 fsync 策略。

#### `obs-failure-bundle-assembly`

- **一句话目标**：按 V1.3 Schema 组装、验证并导出有 Snapshot 与无 Snapshot 两种 Failure Bundle。
- **涉及文件集**：
  - `modules/observability/src/Lumio.GameRuntime.Observability/Failure/FailureBundleAssembler.cs`
  - `modules/observability/src/Lumio.GameRuntime.Observability/Failure/FailureContextSnapshot.cs`
  - `modules/observability/tests/Lumio.GameRuntime.Observability.Tests/FailureBundleGoldenTests.cs`
- **验收标准**：
  - [ ] 正例 Bundle 与架构源 fixture canonical hash 一致。
  - [ ] 首个 Snapshot 前的 Bundle 含 `noSnapshotReason + bootstrapPhase + lastKnownRevision/manifest`，不伪造 SnapshotId。
  - [ ] 任一 artifact hash/manifest mismatch 产生 Fatal verify report。
  - [ ] Assembler 输入不包含可变 ECS/Voxel 引用。
- **依赖**：`obs-event-ports-and-context`、`obs-durable-route-and-emergency-path`
- **Consumes**：`FailureContextSnapshot`、generated FailureBundle schema。
- **Produces**：`FailureBundleView`、`FailureAssemblyResult`。
- **成熟方案**：公共 Schema + System.Security.Cryptography/BCL hash adapter。
- **明确不做**：不决定文件目录、上传地址或恢复动作。

#### `obs-otel-and-microsoft-logging-adapters`

- **一句话目标**：用独立 Adapter 接入 MEL 与 OpenTelemetry，并证明第三方对象不进入核心 API、Hash 或 Unity 目标。
- **涉及文件集**：
  - `modules/observability/src/Lumio.GameRuntime.Observability.Adapters/Lumio.GameRuntime.Observability.Adapters.csproj`
  - `modules/observability/src/Lumio.GameRuntime.Observability.Adapters/MicrosoftLoggingAdapter.cs`
  - `modules/observability/src/Lumio.GameRuntime.Observability.Adapters/OpenTelemetryMetricsAdapter.cs`
  - `modules/observability/src/Lumio.GameRuntime.Observability.Adapters/OpenTelemetryTraceAdapter.cs`
  - `modules/observability/tests/Lumio.GameRuntime.Observability.Tests/AdapterBoundaryTests.cs`
- **验收标准**：
  - [ ] Adapter contract test 验证所有 generated correlation 字段无丢失映射。
  - [ ] 稳定候选 assembly public API scan 不出现第三方类型。
  - [ ] OTel sampling/exporter 开关不改变 canonical state hash。
  - [ ] Host-only Adapter 可被裁剪，core assembly 仍可 NativeAOT/Unity profile 编译。
- **依赖**：`obs-event-ports-and-context`、`obs-bounded-diagnostic-routing`
- **Consumes**：`RuntimeEventView`、`MetricSampleView`、`TraceStartView`。
- **Produces**：MEL/OTel provider 调用。
- **成熟方案**：Microsoft.Extensions.Logging.Abstractions + OpenTelemetry .NET。
- **明确不做**：不固定 Serilog、NLog、OTLP endpoint 或供应商后端。


## 3.2. `config` 模块设架

### 0. 模块身份证

- 目录：`modules/config/`
- 建议程序集：`Lumio.GameRuntime.Config`；Dev bridge 放 `Lumio.GameRuntime.Config.DevAdapters`
- 建议命名空间：`Lumio.GameRuntime.Config`
- 优先级与阶段：P1，Vertical Slice；Foundation 先落 immutable bootstrap snapshot
- 唯一职责：只验证、合并、Staging 并在 Tick Barrier 激活 Game/Toolchain 已编译的配置产物。
- RT-D-001 纪律：以上是物理映射建议；即使首期合并程序集，也必须保持命名空间、internal 可见性和依赖测试，不得形成反向引用。

### 1. 它做什么

- Game/Toolchain 产生 `Generated` artifact；Host 将 artifact、层级上下文、签名/hash 和 Capability 交给 `ConfigModule`。Runtime 从 `validate` 开始接管，绝不提供稳定 `compile` API。
- 后台 validator 校验 TableId、SchemaVersion、ConfigRevision、列类型、Key 唯一、范围、引用、大小、签名/hash；`ConfigLayerMerger` 只按固定 `Engine -> Platform -> Server -> Product -> Environment -> User/Session` 顺序合并。
- 成功的合并结果成为 immutable `ConfigSnapshot`，先进入 `Staged`；Simulation Owner Thread 仅在指定 Tick Barrier 调用 `activate_at_tick`，一个 Tick 内读者绑定同一 Revision。
- Reader 只返回已经类型校验的 row/value；缺失必需值、错误类型、越界或未知必需列返回显式 Rejected，不返回默认零值。
- Dev hot load 通过独立 Capability Adapter 接受新的 Generated artifact；仍完整经过 `Validated -> Staged -> Active`，失败只保留当前 Active Snapshot。
- 成功意味着 snapshot 来源、层级、输出 hash、ConfigRevision 和激活 Tick 可审计；失败不得改变当前 Active 引用。

### 2. 它明确不做什么

- 不解析源文件、默认值、导入、宏、内容引用或生成 typed binary；归 Game/Toolchain compiler。
- 不暴露 `compile`、`compile_file` 或任意运行时编译稳定接口。
- 不定义 Ability/Component/Voxel/Network 业务默认值；归 Game content/schema。
- 不保存 Secret、密钥或访问凭据；归 Host Secret Provider。
- 不在 Tick 中途替换 Active Snapshot，不通过环境变量隐式开 Capability。
- 不实现通用文件监听器或配置中心客户端；Dev/Host Adapter 负责输入。
- 不执行 Gameplay delegate 或把可变 Dictionary 暴露给调用者。

### 3. 代码骨架

#### 3.1 建议目录树

```text
modules/config/
├─ src/Lumio.GameRuntime.Config/
│  ├─ ConfigModule.cs                         # 模块门面和版本生命周期
│  ├─ ConfigServices.cs                       # Reader/activation 只读聚合
│  ├─ Lifecycle/ConfigVersionState.cs         # Generated/Validated/Staged/Active/Rejected/Superseded
│  ├─ Contracts/IGeneratedConfigArtifactPort.cs # Host/Dev 提交 generated artifact
│  ├─ Contracts/IConfigSnapshotView.cs        # Tick 内不可变读取面
│  ├─ Validation/GeneratedConfigValidator.cs  # schema/hash/signature/budget 校验
│  ├─ Validation/ConfigValidationReport.cs    # 结构化结果，不定义新公共码
│  ├─ Merge/ConfigLayer.cs                    # 固定六层内部投影
│  ├─ Merge/ConfigLayerMerger.cs              # 固定顺序和冲突规则
│  ├─ Snapshot/ConfigSnapshot.cs              # immutable table/index/revision
│  ├─ Snapshot/ConfigSnapshotLease.cs         # 旧快照引用寿命
│  ├─ Snapshot/ConfigTableReader.cs           # typed row/value reader
│  ├─ Activation/ConfigActivationSlot.cs      # 单一 Staged slot
│  ├─ Activation/ConfigActivator.cs           # Tick Barrier 原子切换
│  ├─ Diff/ConfigSnapshotDiffer.cs             # 诊断/Audit 差异
│  └─ Errors/ConfigFailure.cs                 # Rejected/Retryable/Fatal
├─ src/Lumio.GameRuntime.Config.DevAdapters/
│  └─ DevGeneratedArtifactAdapter.cs          # Dev Capability 输入；无 compiler API
└─ tests/Lumio.GameRuntime.Config.Tests/
   ├─ GeneratedArtifactValidationTests.cs
   ├─ SixLayerMergeGoldenTests.cs
   ├─ SnapshotReaderPropertyTests.cs
   ├─ TickActivationTests.cs
   └─ DevAdapterFidelityTests.cs
```

#### 3.2 核心类型

| 类型/可见性 | 职责与关键字段/不变式 | 关键方法草图与失败表达 | 线程与生命周期 |
|---|---|---|---|
| `ConfigModule` / `sealed class` | 拥有 version registry、staging slot、active lease root；不拥有 source/compiler。 | `Validate`、`Stage`、`ActivateAtTick`、`Diff`；返回 `ConfigOperationResult`。 | 验证可 worker；状态迁移由 control/owner thread；Dispose 后 reader/lease 规则生效。 |
| `ConfigServices` / `sealed class` / 稳定候选，未冻结 | 只读暴露 `ActiveSnapshot`、`ReaderFactory`、`ActivationPort`。 | getter 与 `AcquireActiveSnapshot()`。 | 并发只读；同 module 寿命。 |
| `ConfigVersionState` / `enum` / internal projection | 只允许 `Generated -> Validated -> Staged -> Active -> Superseded`；校验失败到 Rejected。 | 由 state machine guard 转移；不得从 Rejected/ Superseded 激活。 | Owner/control thread。 |
| `IGeneratedConfigArtifactPort` / `interface` / Port | Host/Dev 交付 generated artifact bytes + generated metadata view；不接文件路径。 | `ConfigSubmitResult Submit(in GeneratedConfigArtifactView artifact)`。 | 并发提交可排队到单 staging pipeline；输入 bytes 复制/lease。 |
| `GeneratedConfigValidator` / `sealed class` / internal | 校验 schema/type/key/range/ref/hash/signature/size；报告确定排序。 | `ConfigValidationReport Validate(in GeneratedConfigArtifactView artifact, in ConfigValidationLimits limits)`。 | 纯函数/worker-safe；无全局 cache。 |
| `ConfigLayerMerger` / `sealed class` / internal | 固定六层；字段 `LayerOrder` 编译时常量投影；冲突必须按公共规则。 | `ConfigMergeResult Merge(ReadOnlySpan<ValidatedConfigLayer> layers)`。 | worker-safe；输出 immutable。 |
| `ConfigSnapshot` / `sealed class` / 稳定候选，未冻结 | 字段 `ConfigRevision`、source/output hash、immutable table index、activation tick；构造后不变。 | `TryOpenTable<TSchema>`、`AcquireLease`；错误显式。 | 并发只读；最后 lease 释放后回收。 |
| `ConfigTableReader<TSchema>` / `readonly struct` / 稳定候选 | 绑定单 snapshot revision/table schema；不返回 mutable row。 | `TryGet(RowKey key, out ConfigRowView<TSchema>)`、`EnumerateOrdered()`。 | 并发只读；snapshot lease 失效后拒绝。 |
| `ConfigActivationSlot` / `sealed class` / internal | 至多一个已验证 Staged candidate；重复同 hash 幂等。 | `TryStage`、`Peek`、`TakeForTick`、`Reject`。 | worker 提交/owner 取；不无限排队。 |
| `DevGeneratedArtifactAdapter` / `sealed class` / Adapter | 只在声明 Dev Capability 时提交 generated artifact；不执行 compiler。 | `OnArtifactProduced(in GeneratedConfigArtifactView artifact)`。 | Host/Dev thread；Capability 撤销后关闭。 |

#### 3.3 稳定候选 API 与内部边界

- 稳定候选 API 只有 `validate`、`stage`、`activate_at_tick`、`read_table`、`diff`；不存在任何 `compile` 方法。
- `ConfigLayer` 的公共值/顺序若来自 generated contract则直接使用；本仓内部投影不得扩展第七层。
- Reader 不暴露第三方 JSON/MessagePack、Dictionary、文件句柄或 mutable byte buffer。

```csharp
// 设计草图；未冻结。artifact 是 Toolchain 已编译产物。
public interface IGeneratedConfigArtifactPort
{
    ConfigSubmitResult Submit(in GeneratedConfigArtifactView artifact);
}

public interface IConfigSnapshotView
{
    ConfigRevision Revision { get; }
    ConfigTableOpenResult<TSchema> OpenTable<TSchema>(GeneratedTableId tableId)
        where TSchema : IGeneratedConfigTableSchema;
}

public interface IConfigActivationPort
{
    ConfigActivationResult ActivateAtTick(ConfigRevision revision, TickId tickId);
}
```

### 4. 与成熟方案怎么接

| 能力 | 采用 | 不采用 | Adapter | 稳定 API 可见第三方类型 | 许可证 | 风险/控制 |
|---|---|---|---|---|---|---|
| Immutable storage | System.Collections.Immutable + frozen ordered arrays | 可变 Dictionary 直接共享 | ImmutableConfigStorageAdapter | BCL 可见但不泄漏具体容器 | MIT | 热路径最终布局由 RT-D-008 Benchmark；canonical enumeration 明确排序。 |
| 二进制 primitive decode | MessagePack reader 或 generated toolchain reader | 运行时 JSON 作为权威表 | GeneratedConfigBinaryAdapter | 否 | MIT/工具链许可 | 格式必须由架构源生成；Adapter 不定义字段。 |
| 签名/hash | BCL cryptography/hash | 自研密码算法 | ConfigArtifactIntegrityAdapter | 否 | MIT | 算法 ID 只来自 generated contract。 |
| Schema validation | 既有 Contract Toolchain；测试镜像 Corvus | JsonSchema.Net binary EULA、NJsonSchema runtime reflection | ContractFixtureValidatorAdapter | 否 | Apache-2.0 | 仅工具/测试；生产热路径只读 generated typed metadata。 |
| Dev hot load | Host Dev Capability Adapter | Runtime compiler/通用文件 watcher | DevGeneratedArtifactAdapter | 否 | 纯 Port | Dev 与 Production 都走同一 validate/stage/activate。 |

**自研最小范围。** 只实现固定六层合并、typed reader、snapshot lease 与 Tick Barrier 激活；编译、解析、文件监听、通用 schema engine 和密码算法均不自研。若 generated toolchain 提供更完整 reader，可替换 `GeneratedConfigBinaryAdapter`。

### 5. 输入 / 输出 / 依赖

**Consumes**

- Game/Toolchain 生成的 `GeneratedConfigArtifactView` 与 generated table metadata。
- Host Capability、Release/Schema version、签名/hash verification Port。
- `TickId`/activation request（由 simulation 在 Barrier 提供）。
- Observability event port（仅输出，不反向读取业务状态）。

**Produces**

- `ConfigSnapshot`/`IConfigSnapshotView` 给 simulation、ecs、gas、replication、persistence、hot-reload。
- `ConfigValidationReport`、`ConfigDiffReport`、activation audit。
- `ConfigRevision` 与 source/output hash 关联给 snapshot/hash/Failure Bundle。

**编译依赖**

- generated config contract。
- observability Port。
- BCL immutable/crypto；Dev Adapter 可依 Host capability abstraction。

**禁止依赖**

- Game compiler/source parser 实现。
- simulation 实现（只消费中立 TickId/activation Port）。
- ecs/gas/replication/persistence/hot-reload 实现。
- Secret store、文件 watcher、配置中心 SDK。
- testing。

### 6. 队列、预算、线程

| 实现对象 | 元素/资源 | 容量/预算来源 | 满载/超限动作 | 顺序/幂等键 | Metric |
|---|---|---|---|---|---|
| `ConfigActivationSlot` | 一个 `ValidatedConfigCandidate` | 语义容量为单一 Staged candidate；artifact byte limit 来自 generated limits/Host capability | 新不同 revision 到来时明确 Reject/Replace policy，绝不覆盖 Active；同 hash 幂等 | `ConfigRevision + SourceHash` | `runtime.config.staged`、`runtime.config.activation_rejected` |
| `ConfigValidationWorkBudget` | artifact bytes/table/rows/refs | generated `ConfigArtifactLimits` 与 Host capability | 返回 Rejected 或 Retryable；不得无界分配 | `SourceHash` | `runtime.config.validation.bytes`、`runtime.config.validation.duration` |
| `ConfigSnapshotLeaseSet` | 旧 snapshot lease | `ConfigSnapshotRetention` 若该参数由 generated config 声明；否则按全部 lease 释放 | 不强制回收仍被 Tick 读取的 snapshot；拒绝超预算新 staging | `ConfigRevision` | `runtime.config.snapshot.live_count` |

- 验证/合并/diff 可在 worker 上处理 immutable input；Active pointer 只由 Simulation Owner Thread 在 Tick Barrier 交换。
- Reader 并发只读且绑定 snapshot lease；不得读取全局 current pointer 后跨 Tick 使用。
- Dev adapter 与生产 adapter 只在输入来源不同，验证和激活路径完全相同。

### 7. 失败与恢复

| 分类 | 触发例 | 返回/可观察结果 | World/Session | Failure Bundle | Journal/证据查询 |
|---|---|---|---|---|---|
| 可拒绝 | 重复 Key、缺列、类型/范围/引用错误 | `ConfigValidationReport` + generated error refs；candidate 到 Rejected | 不 Fault，继续使用 Active | 生产签名/安全拒绝可记最小 bundle fragment | source hash + validation report |
| 可拒绝 | 签名/hash/Capability/Release 不匹配 | Rejected；不进入 Staged | 不 Fault；生产可由 Host进入维护 | 按安全策略 | artifact audit + signer ref |
| 可拒绝 | 非 Barrier 激活或 revision 非当前 Staged | `ConfigActivationResult.Rejected` | 不 Fault | 否 | activation audit |
| 可重试 | artifact source 暂不可读/lease 未取得 | Retryable，保持同 SourceHash | 不 Fault | 否 | source adapter attempt |
| 可重试 | validation worker/budget 暂满 | Retryable/Backpressured；不改 Active | 不 Fault | 持续超时才记录 | work queue metric |
| 可重试 | 旧 snapshot lease 阻止回收 | 保留旧快照；推迟新 staging 或返回 capacity retry | 不 Fault | 否 | lease owner diagnostic |
| 可致命 | Active Snapshot 内部索引/hash 不变量损坏 | Fatal；停止提供可能错误 reader | Session Faulted/维护 | 必须 | active revision/hash + verifier report |
| 可致命 | 原子 Active pointer 出现 torn/双 active | FatalInvariant | Session Faulted | 必须 | activation state snapshot |
| 可致命 | 签名 verifier/生成契约报告互相矛盾 | FatalContractMismatch | 启动或 Session Faulted | 必须 | artifact、schema epoch、toolchain hash |

### 8. 测试面

**本模块测试工程—单元**

- 状态机 exact transitions；Rejected/Superseded 不可激活。
- 六层合并顺序固定且缺层不改变其他层优先级。
- Reader 对缺失必需值不返回 default。

**本模块测试工程—Golden**

- 架构源 config-table valid/invalid fixtures；生成 artifact hash 与预期一致。
- 同输入六层任意提交顺序最终输出必须按固定层顺序一致。

**本模块测试工程—Property**

- 任意合法 layer 集合的 merge 幂等；相同 SourceHash 重复 stage/activate 结果幂等。
- 任意 Tick interleaving 中一个 reader 只观察一个 ConfigRevision。

**本模块测试工程—故障**

- 损坏表、签名失败、超大表、验证 budget 满、Barrier 竞态、旧 lease 持有。

**`testing` Reference Host**

- 在连续 run_tick 中指定 Tick 原子切换；前后 hash 只在 Barrier 改变；Dev adapter 不旁路。

### 9. 本模块任务拆解

#### `cfg-generated-table-validation`

- **一句话目标**：实现 generated artifact 的 typed metadata、完整性、大小和引用验证，不引入编译入口。
- **涉及文件集**：
  - `modules/config/src/Lumio.GameRuntime.Config/Lumio.GameRuntime.Config.csproj`
  - `modules/config/tests/Lumio.GameRuntime.Config.Tests/Lumio.GameRuntime.Config.Tests.csproj`
  - `modules/config/src/Lumio.GameRuntime.Config/Contracts/IGeneratedConfigArtifactPort.cs`
  - `modules/config/src/Lumio.GameRuntime.Config/Validation/GeneratedConfigValidator.cs`
  - `modules/config/src/Lumio.GameRuntime.Config/Validation/ConfigValidationReport.cs`
  - `modules/config/tests/Lumio.GameRuntime.Config.Tests/GeneratedArtifactValidationTests.cs`
- **验收标准**：
  - [ ] 公开 API scan 不存在包含 `Compile` 语义的方法。
  - [ ] valid/invalid config fixtures 得到与架构工具链一致的结果。
  - [ ] 重复 Key、缺列、类型/范围/引用、hash/signature、大小超限各有失败测试。
  - [ ] validation report 按 table/key/field 稳定排序。
- **依赖**：`obs-event-ports-and-context`
- **Consumes**：`GeneratedConfigArtifactView`、generated limits、integrity Port。
- **Produces**：`ValidatedConfigArtifact` 或 `ConfigValidationReport`。
- **成熟方案**：Generated Contract Toolchain + BCL crypto；Corvus 仅测试镜像。
- **明确不做**：不解析源文件、default/import/content ref，不实现 compiler。

#### `cfg-six-layer-merge`

- **一句话目标**：把验证后的层严格按六层固定顺序合并并输出可复现 immutable table。
- **涉及文件集**：
  - `modules/config/src/Lumio.GameRuntime.Config/Merge/ConfigLayer.cs`
  - `modules/config/src/Lumio.GameRuntime.Config/Merge/ConfigLayerMerger.cs`
  - `modules/config/tests/Lumio.GameRuntime.Config.Tests/SixLayerMergeGoldenTests.cs`
- **验收标准**：
  - [ ] 测试枚举全部六层并证明没有第七层入口。
  - [ ] 相同层集合不同输入排列输出 canonical hash 相同。
  - [ ] 冲突/未知必需列按 generated policy 拒绝，不静默覆盖。
  - [ ] 合并输出不持有输入 mutable buffer。
- **依赖**：`cfg-generated-table-validation`
- **Consumes**：`ValidatedConfigLayer`。
- **Produces**：`MergedConfigArtifact`。
- **成熟方案**：System.Collections.Immutable/ordered arrays。
- **明确不做**：不定义业务默认值或层级顺序变体。

#### `cfg-immutable-snapshot-reader`

- **一句话目标**：提供绑定 ConfigRevision 的 immutable `ConfigSnapshot`、typed reader 与 lease。
- **涉及文件集**：
  - `modules/config/src/Lumio.GameRuntime.Config/Snapshot/ConfigSnapshot.cs`
  - `modules/config/src/Lumio.GameRuntime.Config/Snapshot/ConfigSnapshotLease.cs`
  - `modules/config/src/Lumio.GameRuntime.Config/Snapshot/ConfigTableReader.cs`
  - `modules/config/src/Lumio.GameRuntime.Config/Contracts/IConfigSnapshotView.cs`
  - `modules/config/tests/Lumio.GameRuntime.Config.Tests/SnapshotReaderPropertyTests.cs`
- **验收标准**：
  - [ ] Reader 不能返回 mutable collection、Span 指向可回收 buffer 或第三方类型。
  - [ ] lease 存活期间 Superseded snapshot 仍可读且 revision 不变。
  - [ ] 缺失/错误 row 返回显式 result，不返回默认值。
  - [ ] 并发 Property 测试无跨 revision 混读。
- **依赖**：`cfg-six-layer-merge`
- **Consumes**：`MergedConfigArtifact`。
- **Produces**：`ConfigSnapshot`、`IConfigSnapshotView`、typed reader。
- **成熟方案**：System.Collections.Immutable + readonly views。
- **明确不做**：不做 active pointer 切换或文件缓存。

#### `cfg-tick-boundary-activation`

- **一句话目标**：实现单 Staged slot 与 Owner Thread Tick Barrier 原子激活。
- **涉及文件集**：
  - `modules/config/src/Lumio.GameRuntime.Config/ConfigModule.cs`
  - `modules/config/src/Lumio.GameRuntime.Config/ConfigServices.cs`
  - `modules/config/src/Lumio.GameRuntime.Config/Activation/ConfigActivationSlot.cs`
  - `modules/config/src/Lumio.GameRuntime.Config/Activation/ConfigActivator.cs`
  - `modules/config/tests/Lumio.GameRuntime.Config.Tests/TickActivationTests.cs`
- **验收标准**：
  - [ ] 非 Barrier 调用明确拒绝。
  - [ ] 一个 Tick 的所有 reader 均看到同一 ConfigRevision。
  - [ ] 失败激活不修改 Active pointer。
  - [ ] 重复同 revision/hash 激活幂等，审计记录唯一。
- **依赖**：`cfg-immutable-snapshot-reader`
- **Consumes**：`ConfigSnapshot`、`TickId`、OwnerThread guard。
- **Produces**：Active `ConfigSnapshot`、`ConfigActivationResult`。
- **成熟方案**：Interlocked/Volatile BCL + owner-thread serialization。
- **明确不做**：不拥有 Tick 调度，不自动回退到未批准版本。

#### `cfg-dev-capability-adapter`

- **一句话目标**：让开发热载只提交 generated artifact，并证明与生产使用同一验证/Staging/Barrier 路径。
- **涉及文件集**：
  - `modules/config/src/Lumio.GameRuntime.Config.DevAdapters/DevGeneratedArtifactAdapter.cs`
  - `modules/config/tests/Lumio.GameRuntime.Config.Tests/DevAdapterFidelityTests.cs`
- **验收标准**：
  - [ ] 未声明 Dev Capability 时 adapter 构造/提交被拒绝。
  - [ ] adapter API 不含 compile/source path/default resolver。
  - [ ] 相同 artifact 经 Dev 与 Production adapter 产生同一 validation/merge hash。
  - [ ] 热载失败保持上一 Active snapshot。
- **依赖**：`cfg-generated-table-validation`、`cfg-tick-boundary-activation`
- **Consumes**：Host Dev Capability、`GeneratedConfigArtifactView`。
- **Produces**：对 `IGeneratedConfigArtifactPort.Submit` 的调用。
- **成熟方案**：Host Adapter；无 compiler。
- **明确不做**：不实现文件 watcher、编译器或生产配置中心。


## 3.3. `ecs` 模块设架

### 0. 模块身份证

- 目录：`modules/ecs/`
- 建议程序集：`Lumio.GameRuntime.Ecs`；候选引擎放 `Lumio.GameRuntime.Ecs.Adapters.Friflo`；Benchmark 比较 Adapter 独立
- 建议命名空间：`Lumio.GameRuntime.Ecs`
- 优先级与阶段：P0，Foundation
- 唯一职责：为每个独立 World 持有 World-local Entity/Component/Query/ChangeSet 的唯一可变真相。
- RT-D-001 纪律：以上是物理映射建议；即使首期合并程序集，也必须保持命名空间、internal 可见性和依赖测试，不得形成反向引用。

### 1. 它做什么

- `RuntimeCompositionRoot` 为 Server GameWorld、Client ReplicaWorld 或 ReplayWorld 分别构造独立 `EcsWorld`；`simulation` 持有其 facade，只有对应 Simulation Owner Thread 能推进权威写状态。
- World 创建后按 `Created -> Registering -> Ready -> Running -> Draining -> Disposed`；任一状态可进入 `Faulted -> Disposed`。Registering 只消费已验证 generated Component TypeId/field schema。
- `LocalEntityId` 由 `Index + Generation` 组成，只在所属 World 有效；slot 回收必须递增 Generation，stale ID、跨 World ID 和已 Dispose view 都显式拒绝。
- Processor 通过声明过 ReadSet/WriteSet 的 Query 获取 `EcsReadView`/`EcsWriteView`；已有字段写入在 owner thread 执行并记录 ChangeSet。V1 写入过程中发生异常或不变量破坏时 Fail-stop，不提供字段级回滚。
- Create/Add/Remove/Destroy 等结构变化不由普通 View 直接执行，而是经 `command` 在 `EcsCommandBufferCommit` 统一应用；成功后生成确定顺序的 `ChangeSet`。
- 快照、Replication 和 Persistence 只读取带 World/Tick/Revision/SchemaEpoch 的 immutable view；后台线程不得持有 mutable component ref。成功意味着所有 view/ID/ChangeSet 都能证明所属 World、有效范围和预算。

### 2. 它明确不做什么

- 不拥有 Logical Tick、13 Phase、Processor 调度或并行策略；归 `simulation`。
- 不直接接受普通 Processor 的结构变化；归 `command` Buffer/Commit。
- 不拥有 `NetEntityId`、Mapping、Baseline、Tombstone；归 `replication`。
- 不拥有 Ability/Effect/Attribute 的第二存储；GAS 权威字段仍在 ECS，通用状态机归 `gas`。
- 不拥有 CrossWorldTxn、Revision Vector、Reservation 或 SnapshotCut；归 `coordination`。
- 不暴露第三方 Archetype、World、Entity、Query、Column、裸指针或对象地址。
- 不共享 Server/Client/Replay World 的 cache、LocalEntityId、view 或锁。
- 不创建 worker pool、Socket、Native job 或持久化介质。

### 3. 代码骨架

#### 3.1 建议目录树

```text
modules/ecs/
├─ src/Lumio.GameRuntime.Ecs/
│  ├─ EcsModule.cs                            # 模块注册与 storage factory
│  ├─ EcsServices.cs                          # World factory/只读 providers
│  ├─ World/EcsWorld.cs                       # World facade 与 state owner
│  ├─ World/EcsWorldState.cs                  # Created/Registering/Ready/Running/Draining/Disposed/Faulted
│  ├─ World/OwnerThreadGuard.cs               # V1 单写线程断言
│  ├─ Entity/LocalEntityId.cs                 # Index + Generation 值类型
│  ├─ Entity/EntitySlotTable.cs               # slot/generation/active 状态
│  ├─ Entity/EntityLifecycleResult.cs         # create/destroy/stale 结果
│  ├─ Storage/IWorldStorageAdapter.cs         # 可替换 storage Port
│  ├─ Storage/ComponentTypeRegistry.cs        # generated TypeId/field schema 注册
│  ├─ Query/QuerySpec.cs                      # 本仓 query 描述
│  ├─ Query/QueryPlan.cs                      # adapter-neutral compiled query
│  ├─ Query/EcsReadView.cs                    # 带 World/Tick/Revision 的只读 view
│  ├─ Query/EcsWriteView.cs                   # 声明 WriteSet 内字段写入
│  ├─ Query/QueryBatch.cs                     # bounded ordered batch
│  ├─ ChangeTracking/ChangeSet.cs             # 确定顺序的变化结果
│  ├─ ChangeTracking/ChangeSetBuilder.cs      # Tick 内 owner-only 收集
│  ├─ Snapshot/IEcsSnapshotProvider.cs        # immutable snapshot provider
│  ├─ Snapshot/EcsWorldReadSnapshot.cs        # 编码/Hash/worker 安全切面
│  ├─ Budgets/EcsBudget.cs                    # entity/query/bytes limits
│  └─ Errors/EcsFailure.cs                    # 分类与 generated error ref
├─ src/Lumio.GameRuntime.Ecs.Adapters.Friflo/
│  └─ FrifloWorldStorageAdapter.cs            # 首选候选内部引擎
├─ benchmarks/Lumio.GameRuntime.Ecs.Benchmarks/
│  ├─ ArchWorldStorageBenchmarkAdapter.cs     # RT-D-002 比较，不进生产 API
│  └─ EcsWorkloads.cs                         # 固定 workload
└─ tests/Lumio.GameRuntime.Ecs.Tests/
   ├─ EntityGenerationPropertyTests.cs
   ├─ QueryViewBoundaryTests.cs
   ├─ ChangeSetGoldenTests.cs
   ├─ WorldIsolationTests.cs
   ├─ FailStopWriteTests.cs
   └─ StorageAdapterConformanceTests.cs
```

#### 3.2 核心类型

| 类型/可见性 | 职责与关键字段/不变式 | 关键方法草图与失败表达 | 线程与生命周期 |
|---|---|---|---|
| `EcsModule` / `sealed class` | 持有 storage factory、component registry factory、snapshot provider factory；不是多 World 状态容器。 | `CreateWorld(in EcsWorldCreateRequest)` 返回 `EcsWorldCreateResult`。 | Composition Root 构造；module dispose 前必须无 active world。 |
| `EcsServices` / `sealed class` / 稳定候选，未冻结 | 只读暴露 `WorldFactory` 与 schema registry view。 | `CreateWorld`；不暴露 adapter。 | 并发创建由 Host/Simulation 串行约束。 |
| `EcsWorld` / `sealed class` / 稳定候选，未冻结 | 字段 `WorldId`、State、slot table、storage adapter、change builder、active view epoch；唯一真相。 | `RegisterTypes`、`Start`、`CreateEntityForCommit`、`DestroyEntityForCommit`、`Query`、`ReadView`、`WriteView`、`CaptureSnapshot`、`Drain`、`Dispose`；所有结果分类。 | 权威写仅 Owner Thread；snapshot 并发只读；Disposed 后全部旧 token 拒绝。 |
| `EcsWorldState` / `enum` | exact states：Created/Registering/Ready/Running/Draining/Disposed/Faulted。 | guarded transition；Faulted 只允许证据捕获与 Dispose。 | Owner Thread。 |
| `LocalEntityId` / `readonly record struct` / 稳定候选 | `uint Index`、`uint Generation`；不得编码对象地址；World 身份由调用 context/view 校验。 | `IsDefault`；无自行 resolve。 | 值对象；World Dispose 后语义失效。 |
| `EntitySlotTable` / `sealed class` / internal | slot active/generation/free-list；回收前 ChangeSet/tombstone consumer 已获得结果。 | `Allocate`、`Resolve`、`Retire`；checked Generation increment；wrap 为 Fatal/容量耗尽。 | Owner Thread；World 生命周期。 |
| `IWorldStorageAdapter` / `interface` / internal Port | 抽象 component storage/query/field access/structural batch；第三方类型封闭。 | `Register`、`Create`、`Destroy`、`SetField`、`CompileQuery`、`EnumerateOrdered`、`CaptureReadSnapshot`、`ValidateIntegrity`。 | Owner Thread 写；immutable snapshot 可 worker 读；Dispose 幂等。 |
| `QuerySpec` / `readonly record struct` / 稳定候选 | generated TypeId 集合、filter、ReadSet/WriteSet、budget；集合先 canonical sort。 | `ValidateAgainst(ProcessorDescriptorView)`。 | 不可变；可缓存 hash。 |
| `EcsReadView` / `readonly ref struct` 或受限 lease / internal | 绑定 WorldId/TickId/view epoch/query batch；只读。 | `TryRead<TField>`；跨 entity/type/epoch 返回 Rejected。 | Owner Thread 或 immutable snapshot；不得跨 await/heap。 |
| `EcsWriteView` / `readonly ref struct` / internal | 绑定 declared WriteSet 和 ChangeSetBuilder；只写已有字段。 | `Write<TField>`；异常/partial write 触发 Fail-stop。 | Owner Thread、单 Processor invocation；不得缓存。 |
| `ChangeSet` / `sealed immutable class` / 稳定候选 | 按 generated entity/type/field key 稳定排序；含 valid Tick/World/Revision，不含 object address。 | `EnumerateOrdered`、`ComputeCanonicalHash`。 | Commit 后 immutable；lease 释放后回收。 |
| `IEcsSnapshotProvider` / `interface` / Port | 为同一 SnapshotCut 提供 immutable projection/manifest result。 | `Capture(in SnapshotCutView cut)` 返回 provider result/token。 | Owner Thread pin；worker 编码；release 必须显式。 |

#### 3.3 稳定候选 API 与内部边界

- 模块 README 候选 `create_entity`、`destroy_entity`、`query`、`read_view`、`change_set` 保留语义；结构 create/destroy 只允许 command commit 内部调用，普通 Gameplay 只拿 Deferred Token。
- 稳定候选 API 不出现 Friflo/Arch/DefaultEcs 类型、Span 指向第三方 storage、Archetype/Column 或对象地址。
- 已有字段写入是 V1 Fail-stop；返回可拒绝只发生在写入前的 ID/schema/权限/预算验证。

```csharp
// 设计草图；未冻结。结构方法只由 command commit adapter 调用。
public interface IEcsWorldView
{
    WorldId WorldId { get; }
    EcsWorldStateView State { get; }
    QueryOpenResult OpenQuery(in QuerySpec spec, in QueryBudget budget);
    SnapshotCaptureResult Capture(in SnapshotCutView cut);
}

internal interface IWorldStorageAdapter : IDisposable
{
    StorageRegisterResult Register(in GeneratedComponentSchemaView schema);
    StorageCreateResult Create(LocalEntityId entity, in ComponentInitBatch components);
    StorageDestroyResult Destroy(LocalEntityId entity);
    StorageQueryResult CompileQuery(in QuerySpec spec);
    StorageIntegrityResult ValidateIntegrity();
}
```

### 4. 与成熟方案怎么接

| 能力 | 采用 | 不采用 | Adapter | 稳定 API 可见第三方类型 | 许可证 | 风险/控制 |
|---|---|---|---|---|---|---|
| ECS storage | Friflo.Engine.ECS 首选候选 | 直接采用其 public Entity/World/Query；提前冻结布局 | FrifloWorldStorageAdapter | 否 | MIT | NativeAOT/Unity/Generation/ordered enumeration 必须 conformance；已知历史 AOT issue 纳入 smoke。 |
| 比较证据 | Arch benchmark adapter | 同时发布两套生产 engine | ArchWorldStorageBenchmarkAdapter | 否 | Apache-2.0 | 只在 benchmark 项目；其 API/安全差异不能渗入语义。 |
| 替代候选 | DefaultEcs | 作为首选：0.x API 与结构模型证据较弱 | 无生产 Adapter，保留调查记录 | 否 | MIT-0 | 仅当前两候选失败再进入 ADR。 |
| 池化 | ArrayPool/MemoryPool | 自研通用对象池 | StorageScratchPool | 否 | MIT | 只池临时 buffer，不池跨 Tick view。 |
| Property/Benchmark | CsCheck + BenchmarkDotNet | 手写随机测试/Stopwatch 数字 | test/benchmark harness | 否 | Apache-2.0/MIT | 相同 workload 比较；无测量不批准 RT-D-002。 |

**自研最小范围。** 自研仅限 World-local ID/Generation、Adapter-neutral Query/View、ChangeSet、Fail-stop 边界和 snapshot projection；archetype/column/sparse storage 交成熟 ECS。若候选都不能满足 semantics，先以 test-only reference storage 证明契约并另起 ADR，不直接写产品 storage。

### 5. 输入 / 输出 / 依赖

**Consumes**

- generated Component Schema/TypeId/field metadata、World Context、ConfigSnapshot 中的 EcsBudget。
- `ProcessorDescriptorView` 的 ReadSet/WriteSet（中立 generated contract）。
- command 在 `EcsCommandBufferCommit` 交付的 `PreparedGameDelta`/structural batch。
- coordination 的 SnapshotCut/Revision view 只读投影。

**Produces**

- `LocalEntityId`/lifecycle result、`QueryBatch`、`EcsReadView`/`EcsWriteView`。
- `ChangeSet` 给 command/simulation/replication/persistence。
- `EcsWorldReadSnapshot`/`IEcsSnapshotProvider` 给 persistence/testing。
- Integrity/failure evidence 给 observability/FailureBundle。

**编译依赖**

- generated contracts。
- config（只读 budget）与 observability Port。
- 内部 Friflo adapter；benchmark-only Arch。

**禁止依赖**

- simulation、command、coordination、replication、gas、persistence 实现程序集。
- Host/Socket/Connection/ALC。
- Voxel engine 内部 storage。
- testing。

### 6. 队列、预算、线程

| 实现对象 | 元素/资源 | 容量/预算来源 | 满载/超限动作 | 顺序/幂等键 | Metric |
|---|---|---|---|---|---|
| `QueryBatchBudget` | entity/field/bytes | `ProcessorDescriptor.Budget` 与 ConfigSnapshot ECS query limits | 查询前 Rejected；不返回无标记 partial batch | `WorldId + TickId + QuerySpecHash` | `runtime.ecs.query.entities`、`runtime.ecs.query.rejected` |
| `ChangeSetBudget` | field/structural changes/bytes | CommandBuffer/processor budget 与 ECS change-set limit | Prepared 前 Rejected；字段写后超限为 Fail-stop | `TickId + ChangeSequence` | `runtime.ecs.changeset.bytes` |
| `SnapshotViewBudget` | pinned pages/entities/bytes | SnapshotCut/Host capability/generated limits | Retryable if immutable cut temporarily unavailable；不得复制无界 World | `SnapshotId + Revision` | `runtime.ecs.snapshot.pinned_bytes` |
| 内部队列 | 无 | ECS 不拥有异步权威队列 | 调用方队列满由所属模块处理 | 不适用 | `runtime.ecs.owner_thread_violations` |

- 所有权威 storage/slot/change builder 写入由单一 Simulation Owner Thread 完成。
- worker 只能读取 `EcsWorldReadSnapshot` 或明确 pin 的 immutable pages；completion 不能直接写 World。
- 每个 World 使用独立 adapter instance、slot table、query cache 和 lease epoch；LocalEmbedded 两 World 不共享。

### 7. 失败与恢复

| 分类 | 触发例 | 返回/可观察结果 | World/Session | Failure Bundle | Journal/证据查询 |
|---|---|---|---|---|---|
| 可拒绝 | unknown TypeId/field、stale Generation、跨 World ID | `EcsRejected` + generated error ref；写入前无副作用 | 不 Fault | 通常否 | entity/type rejection event |
| 可拒绝 | ReadSet/WriteSet 越界、非法结构操作 | 拒绝 view/command preflight | 不 Fault | 安全/作弊场景可附 fragment | processor descriptor + request |
| 可拒绝 | Query/structural budget 在预检阶段超限 | 明确 BudgetExceeded；不返回未标记 partial | 不 Fault | 否 | budget metric + request hash |
| 可重试 | immutable snapshot/pin 暂不可用 | Retryable with same SnapshotCut | 不 Fault | 持续超时才需要 | snapshot provider status |
| 可重试 | 临时 scratch/pool 资源不足且尚未写权威字段 | Retryable/Backpressured | 不 Fault | 否 | pool/allocator metric |
| 可重试 | Draining 中新非权威 Query | Retryable/Rejected by lifecycle；旧 valid snapshot 可完成 | 不 Fault | 否 | world lifecycle event |
| 可致命 | 字段写入后异常/ChangeSet 记录失败 | Fail-stop；不尝试字段级 undo | World/Session Faulted | 必须 | last Tick/Processor/field + partial ChangeSet evidence |
| 可致命 | storage integrity/slot generation/free-list 不变量破坏 | FatalIntegrity | World Faulted | 必须 | adapter integrity report |
| 可致命 | Owner Thread 违规写或跨 World mutable view | FatalInvariant | World/Session Faulted | 必须 | owner thread token、view epoch、call site correlation |

### 8. 测试面

**本模块测试工程—单元**

- exact World state machine；Disposed 后 ID/view/token 全拒绝。
- create/destroy/reuse 的 Generation 递增与 stale reject。
- Query filter/read/write boundary 和同 Tick ChangeSet 顺序。

**本模块测试工程—Golden**

- generated entity/component fixtures；ChangeSet canonical bytes/hash。
- 每个 storage adapter 对同 workload 产生相同 ordered projection。

**本模块测试工程—Property**

- 任意 create/destroy sequence 不解析 stale ID；跨 World ID 永不命中。
- 任意插入顺序的逻辑相同 World 产生相同 canonical snapshot/hash。

**本模块测试工程—故障**

- unknown type、stale entity、跨 World view、duplicate destroy、budget、storage corruption、post-write exception。

**`testing` Reference Host**

- 双 World 隔离、Replay Hash、crash at `EcsCommandBufferCommit` boundary。

**Benchmark**

- 固定 archetype density/query mix/structural mix 在 Friflo/Arch 上比较 p50/p95/p99、alloc、memory；结果只作为 RT-D-002 evidence。

### 9. 本模块任务拆解

#### `ecs-world-and-entity-identity`

- **一句话目标**：实现 exact World 状态机、`LocalEntityId(Index, Generation)` 和跨 World/stale 失效边界。
- **涉及文件集**：
  - `modules/ecs/src/Lumio.GameRuntime.Ecs/Lumio.GameRuntime.Ecs.csproj`
  - `modules/ecs/tests/Lumio.GameRuntime.Ecs.Tests/Lumio.GameRuntime.Ecs.Tests.csproj`
  - `modules/ecs/src/Lumio.GameRuntime.Ecs/EcsModule.cs`
  - `modules/ecs/src/Lumio.GameRuntime.Ecs/EcsServices.cs`
  - `modules/ecs/src/Lumio.GameRuntime.Ecs/World/EcsWorld.cs`
  - `modules/ecs/src/Lumio.GameRuntime.Ecs/World/EcsWorldState.cs`
  - `modules/ecs/src/Lumio.GameRuntime.Ecs/Entity/LocalEntityId.cs`
  - `modules/ecs/src/Lumio.GameRuntime.Ecs/Entity/EntitySlotTable.cs`
  - `modules/ecs/tests/Lumio.GameRuntime.Ecs.Tests/EntityGenerationPropertyTests.cs`
  - `modules/ecs/tests/Lumio.GameRuntime.Ecs.Tests/WorldIsolationTests.cs`
- **验收标准**：
  - [ ] 状态迁移只匹配 README exact names。
  - [ ] Property 测试覆盖长序列 create/destroy/reuse，旧 Generation 永不 resolve。
  - [ ] 同 Index/Generation 在不同 World context 被拒绝。
  - [ ] Disposed/Faulted 后旧 ID 和 view 全部拒绝。
- **依赖**：`cfg-immutable-snapshot-reader`、`obs-event-ports-and-context`
- **Consumes**：WorldId、generated entity limits、ConfigSnapshot。
- **Produces**：`EcsWorld`、`LocalEntityId`、lifecycle results。
- **成熟方案**：领域语义 + BCL；无第三方 storage 暴露。
- **明确不做**：不实现 component storage/query/command commit。

#### `ecs-storage-adapter-contract`

- **一句话目标**：冻结内部 `IWorldStorageAdapter` conformance 面并接入首选 Friflo Adapter。
- **涉及文件集**：
  - `modules/ecs/src/Lumio.GameRuntime.Ecs/Storage/IWorldStorageAdapter.cs`
  - `modules/ecs/src/Lumio.GameRuntime.Ecs/Storage/ComponentTypeRegistry.cs`
  - `modules/ecs/src/Lumio.GameRuntime.Ecs.Adapters.Friflo/FrifloWorldStorageAdapter.cs`
  - `modules/ecs/tests/Lumio.GameRuntime.Ecs.Tests/StorageAdapterConformanceTests.cs`
- **验收标准**：
  - [ ] 公开 API scan 不出现 Friflo 类型。
  - [ ] register/create/destroy/field/query/snapshot/integrity 全部有 conformance test。
  - [ ] unknown generated TypeId 与 duplicate registration 显式拒绝。
  - [ ] NativeAOT publish 与 netstandard2.1/Unity compile harness 记录结果。
- **依赖**：`ecs-world-and-entity-identity`
- **Consumes**：generated component schema、`LocalEntityId`。
- **Produces**：`IWorldStorageAdapter`、Friflo implementation。
- **成熟方案**：Friflo.Engine.ECS behind Adapter。
- **明确不做**：不批准 RT-D-002，不暴露 archetype/layout。

#### `ecs-query-read-write-views`

- **一句话目标**：实现 adapter-neutral QuerySpec/Plan/Batch 与声明约束的 ReadView/WriteView。
- **涉及文件集**：
  - `modules/ecs/src/Lumio.GameRuntime.Ecs/Query/QuerySpec.cs`
  - `modules/ecs/src/Lumio.GameRuntime.Ecs/Query/QueryPlan.cs`
  - `modules/ecs/src/Lumio.GameRuntime.Ecs/Query/QueryBatch.cs`
  - `modules/ecs/src/Lumio.GameRuntime.Ecs/Query/EcsReadView.cs`
  - `modules/ecs/src/Lumio.GameRuntime.Ecs/Query/EcsWriteView.cs`
  - `modules/ecs/tests/Lumio.GameRuntime.Ecs.Tests/QueryViewBoundaryTests.cs`
- **验收标准**：
  - [ ] ReadSet/WriteSet 越界在首写前拒绝。
  - [ ] Batch 声明 World/Tick/view epoch/budget，并按 canonical entity key 迭代。
  - [ ] view 无法跨 await/heap 或过期 epoch 使用。
  - [ ] 已有字段写测试证明 ChangeSet hook 在写前注册；hook 失败触发 Fail-stop。
- **依赖**：`ecs-storage-adapter-contract`
- **Consumes**：`QuerySpec`、ProcessorDescriptor view、EcsBudget。
- **Produces**：QueryPlan/Batch、ReadView/WriteView。
- **成熟方案**：Friflo query wrapped by Runtime views。
- **明确不做**：不调度 Processor，不允许结构变化。

#### `ecs-change-set-and-snapshot-view`

- **一句话目标**：实现确定顺序 ChangeSet 与同 SnapshotCut 的 immutable ECS snapshot provider。
- **涉及文件集**：
  - `modules/ecs/src/Lumio.GameRuntime.Ecs/ChangeTracking/ChangeSet.cs`
  - `modules/ecs/src/Lumio.GameRuntime.Ecs/ChangeTracking/ChangeSetBuilder.cs`
  - `modules/ecs/src/Lumio.GameRuntime.Ecs/Snapshot/IEcsSnapshotProvider.cs`
  - `modules/ecs/src/Lumio.GameRuntime.Ecs/Snapshot/EcsWorldReadSnapshot.cs`
  - `modules/ecs/tests/Lumio.GameRuntime.Ecs.Tests/ChangeSetGoldenTests.cs`
- **验收标准**：
  - [ ] 逻辑相同变更不同执行容器顺序得到相同 ChangeSet bytes/hash。
  - [ ] Snapshot view 含 World/Tick/Revision/SchemaEpoch 并无 mutable refs。
  - [ ] pin/release 生命周期有泄漏和 use-after-release 测试。
  - [ ] 超预算在捕获前 Retryable，不产生伪完整 snapshot。
- **依赖**：`ecs-query-read-write-views`
- **Consumes**：field/structural change hooks、SnapshotCutView。
- **Produces**：`ChangeSet`、`IEcsSnapshotProvider`。
- **成熟方案**：Runtime domain ordering + BCL pooled buffers。
- **明确不做**：不编码最终 Snapshot 文件，不调用 Voxel provider。

#### `ecs-world-lifecycle-fail-stop`

- **一句话目标**：把 owner-thread guard、Draining/Disposed、post-write failure 的唯一 Fail-stop 动作闭合。
- **涉及文件集**：
  - `modules/ecs/src/Lumio.GameRuntime.Ecs/World/OwnerThreadGuard.cs`
  - `modules/ecs/src/Lumio.GameRuntime.Ecs/Errors/EcsFailure.cs`
  - `modules/ecs/tests/Lumio.GameRuntime.Ecs.Tests/FailStopWriteTests.cs`
- **验收标准**：
  - [ ] 首写后注入异常必然令 World Faulted，后续 Tick/Processor 被拒绝。
  - [ ] 不包含 field undo journal 或继续执行分支。
  - [ ] Fault evidence 含 Tick/Processor/entity/type/field/partial ChangeSet ref。
  - [ ] Draining 允许已 pin snapshot 完成但拒绝新写。
- **依赖**：`ecs-change-set-and-snapshot-view`、`obs-failure-bundle-assembly`
- **Consumes**：OwnerThread token、write evidence、observability failure port。
- **Produces**：Fail-stop transition 与 `EcsFailure`。
- **成熟方案**：纯领域语义。
- **明确不做**：不决定进程退出、Session 重建或用户提示。

#### `ecs-storage-candidate-benchmarks`

- **一句话目标**：用同一 conformance/property/golden/workload 比较 Friflo 与 Arch，为 RT-D-002 产证。
- **涉及文件集**：
  - `modules/ecs/benchmarks/Lumio.GameRuntime.Ecs.Benchmarks/Lumio.GameRuntime.Ecs.Benchmarks.csproj`
  - `modules/ecs/benchmarks/Lumio.GameRuntime.Ecs.Benchmarks/ArchWorldStorageBenchmarkAdapter.cs`
  - `modules/ecs/benchmarks/Lumio.GameRuntime.Ecs.Benchmarks/EcsWorkloads.cs`
  - `modules/ecs/benchmarks/Lumio.GameRuntime.Ecs.Benchmarks/EcsStorageBenchmarks.cs`
  - `modules/ecs/benchmarks/Lumio.GameRuntime.Ecs.Benchmarks/README.md`
- **验收标准**：
  - [ ] 两 Adapter 执行相同生成 workload 和 state hash oracle。
  - [ ] 报告固定硬件/runtime/config/entity density/query mix，不把数字写入契约。
  - [ ] 包含 NativeAOT/Unity compile、alloc、memory、query、structural commit 指标。
  - [ ] 报告明确支持/否决证据与替换成本，不标 RT-D-002 已批准。
- **依赖**：`ecs-storage-adapter-contract`、`ecs-query-read-write-views`、`ecs-change-set-and-snapshot-view`
- **Consumes**：`IWorldStorageAdapter` conformance suite、固定 workloads。
- **Produces**：RT-D-002 evidence report。
- **成熟方案**：BenchmarkDotNet + Arch comparison adapter。
- **明确不做**：不让 Arch 进入生产依赖，不自动选择 winner。


## 3.4. `command` 模块设架

### 0. 模块身份证

- 目录：`modules/command/`
- 建议程序集：`Lumio.GameRuntime.Command`；不把第三方 ECS CommandBuffer 作为公共实现
- 建议命名空间：`Lumio.GameRuntime.Command`
- 优先级与阶段：P0，Foundation
- 唯一职责：为每个 Processor 提供独立的结构命令缓冲，按稳定键合并并在 Prepare 后形成不可业务拒绝的 `PreparedGameDelta`。
- RT-D-001 纪律：以上是物理映射建议；即使首期合并程序集，也必须保持命名空间、internal 可见性和依赖测试，不得形成反向引用。

### 1. 它做什么

- `simulation` 在每次 Processor invocation 前从 `CommandModule` 打开一份只属于 `TickId + Phase + ProcessorId` 的 `ProcessorCommandBuffer`；同一 Processor 不共享 buffer，跨 Processor 可以并发生成。
- Buffer 只接受 generated command kind 或 Runtime 内部结构操作；每次 append 分配单调 `LocalSequence`，排序键固定为 `Phase + ProcessorId + LocalSequence`。
- 状态机严格为 `Open -> Sealed -> Merged -> Prepared -> Applied`。`Open/Sealed` 可在写入可见状态前 Discard；`Prepared` 后不能因业务规则、容量、Generation、权限或冲突再拒绝。
- `DeferredEntityToken` 只在同一 Tick/World/commit domain 内有效。Create 后写入、Destroy 后写入、重复 Destroy 和跨 buffer token 引用在 Merge/Prepare 以确定规则处理。
- `CrossWorldPrepare` 前，`CommandPreflightValidator` 对 Generation、目标存在性、组件容量、命令冲突、权限和预算做全量校验并预留资源，生成 immutable `PreparedGameDelta`。
- `EcsCommandBufferCommit` 只消费已 Prepared delta，调用 ECS commit-only API 幂等 Apply；允许结果仅 `Applied`/`AlreadyApplied` 或基础设施级 `Indeterminate`/`Faulted`。成功发布 Deferred Token→LocalEntityId 映射与 ChangeSet。

### 2. 它明确不做什么

- 不拥有 Component Storage、Entity Slot/Generation、Query plan；归 `ecs`。
- 不决定 13 Phase、Processor 依赖或并行调度；归 `simulation`。
- 不写 Voxel、不决定 CrossWorldTxn 状态或 Commit 顺序；归 `coordination`。
- 不实现 WAL、TxnJournal、CommandLog 介质；只通过 durable record Port 输出 generated record。
- 不允许 Processor 绕过 Buffer 直接 Create/Add/Remove/Destroy。
- 不把 Deferred Token 当作 LocalEntityId/NetEntityId，也不允许跨 Tick/World/Scope 使用。
- 不使用第三方 ECS 自带 CommandBuffer 语义替代本仓状态机。
- 不在 `Prepared` 后执行新的业务校验或按优先级丢弃权威命令。

### 3. 代码骨架

#### 3.1 建议目录树

```text
modules/command/
├─ src/Lumio.GameRuntime.Command/
│  ├─ CommandModule.cs                         # buffer factory/merge/prepare/apply services
│  ├─ CommandServices.cs                       # 下游只读 facade
│  ├─ Lifecycle/CommandBufferState.cs          # Open/Sealed/Merged/Prepared/Applied + pre-prepare Discard/Fault
│  ├─ Buffers/ProcessorCommandBuffer.cs         # 每 Processor 独立写入
│  ├─ Buffers/CommandBufferWriter.cs            # 受声明约束 append API
│  ├─ Buffers/SealedCommandBuffer.cs            # immutable owner transfer
│  ├─ Commands/StructuralCommand.cs             # Runtime 内部 tagged value，不新增 wire ID
│  ├─ Commands/CommandSortKey.cs                # Phase+ProcessorId+LocalSequence
│  ├─ Tokens/DeferredEntityToken.cs             # Tick/World/Buffer generation 域
│  ├─ Tokens/DeferredEntityMap.cs               # Apply 后 token mapping
│  ├─ Merge/CommandBufferMerger.cs              # stable k-way merge
│  ├─ Merge/MergedCommandBatch.cs               # canonical ordered immutable batch
│  ├─ Prepare/CommandPreflightValidator.cs       # 全业务校验前置
│  ├─ Prepare/CommandReservationSet.cs           # ECS 容量/target reservation
│  ├─ Prepare/PreparedGameDelta.cs               # immutable Prepared participant payload
│  ├─ Apply/EcsCommandCommitExecutor.cs          # commit-only idempotent Apply
│  ├─ Apply/CommandApplyReceipt.cs               # mapping/ChangeSet/idempotent result
│  ├─ Evidence/ICommandEvidencePort.cs           # generated CommandLogRecord route
│  ├─ Budgets/CommandBufferBudget.cs             # max buffers/commands/bytes
│  └─ Errors/CommandFailure.cs                  # 分类与 first failing command
└─ tests/Lumio.GameRuntime.Command.Tests/
   ├─ BufferStateMachineTests.cs
   ├─ StableMergePropertyTests.cs
   ├─ DeferredTokenGoldenTests.cs
   ├─ PreparedBoundaryTests.cs
   ├─ EcsApplyFaultTests.cs
   └─ CommandBudgetTests.cs
```

#### 3.2 核心类型

| 类型/可见性 | 职责与关键字段/不变式 | 关键方法草图与失败表达 | 线程与生命周期 |
|---|---|---|---|
| `CommandModule` / `sealed class` | 持有 buffer factory、merger、preflight、commit executor；不保存跨 Tick 未登记 buffer。 | `OpenBuffer`、`Merge`、`Prepare`、`Apply`；所有操作返回 typed result。 | Composition Root 创建；Apply 由 Owner Thread；Dispose 前不得有 in-flight Prepared delta。 |
| `CommandServices` / `sealed class` / 稳定候选，未冻结 | 只读聚合 `Buffers`、`PreparePort`、`ApplyPort`；不暴露 ECS adapter。 | getter；不允许任意 commit。 | 并发只读。 |
| `CommandBufferState` / `enum` / internal projection | exact path `Open/Sealed/Merged/Prepared/Applied`；Discard 只在 Prepared 前；Fault 保留证据。 | guarded transition；Prepared/Applied 禁止返回业务拒绝。 | Buffer owner/Owner Thread。 |
| `ProcessorCommandBuffer` / `sealed class` / internal | 字段 TickId、Phase、ProcessorId、WorldId、LocalSequence、budget、generation；只由一个 invocation 写。 | `Append(in StructuralCommand)`、`AllocateDeferredEntity()`、`Seal()`、`Discard()`。 | 单 writer；Seal 后 immutable，调用方失去 writer。 |
| `DeferredEntityToken` / `readonly record struct` / 稳定候选 | 字段 BufferGeneration/LocalTokenSequence 的 opaque 投影；不得伪装 ID。 | 只比较 equality；resolve 只能经 apply receipt。 | 值对象；Applied/Discarded/Tick end 后失效。 |
| `CommandSortKey` / `readonly record struct` | Phase ordinal、ProcessorId canonical bytes、LocalSequence；禁止 thread arrival order。 | `CompareTo` total order；checked sequence。 | 纯值；Property test。 |
| `CommandBufferMerger` / `sealed class` / internal | 对 Sealed buffers canonical k-way merge，检测同 target/token 依赖与 duplicate。 | `CommandMergeResult Merge(ReadOnlySpan<SealedCommandBuffer>)`。 | Owner Thread；输入 immutable。 |
| `CommandPreflightValidator` / `sealed class` / internal | 读取 ECS只读 state、Processor descriptor、permission/capacity/budget；建立 reservation。 | `CommandPrepareResult Prepare(in MergedCommandBatch, in PrepareContext)`。 | Owner Thread at CrossWorldPrepare；Prepared 前可拒绝。 |
| `PreparedGameDelta` / `sealed immutable class` / stable cross-module candidate | 字段只引用 generated IDs、canonical commands、reservation token、expected revision、hash；所有业务校验已完成。 | 无 mutation；`VerifyForApply` 只校验基础设施 token/integrity，不重新业务判断。 | Owner Thread 创建；可传 coordination；完成/abort 后释放 reservation。 |
| `EcsCommandCommitExecutor` / `sealed class` / internal | 以 Txn/Command idempotency key 调 ECS commit-only methods，顺序即 merged order。 | `CommandApplyResult Apply(in PreparedGameDelta delta)`；只返回 Applied/AlreadyApplied/Indeterminate/Faulted。 | Owner Thread，NotCancellable。 |
| `ICommandEvidencePort` / `interface` / Port | 发送 generated `CommandLogRecord`/hash，不依赖 persistence。 | `DurableEnqueueResult Append(in CommandLogRecordView record)`。 | 幂等，多次 append/query；介质由下游。 |

#### 3.3 稳定候选 API 与内部边界

- `open_buffer`、`append`、`seal`、`merge`、`prepare`、`commit/apply`、`discard` 是候选语义；公共状态必须包含 `Prepared`。
- `PreparedGameDelta` 是 coordination 消费的 immutable participant payload，不是新公共 wire Schema；跨仓持久化字段只使用 generated `CommandLogRecord`。
- 任何第三方 ECS CommandBuffer、Entity、World 或 Query 类型不得出现于 public surface。

```csharp
// 设计草图；未冻结。
public interface ICommandBufferFactory
{
    BufferOpenResult Open(in ProcessorInvocationKey key, in CommandBufferBudget budget);
}

public interface ICommandPreparePort
{
    CommandMergeResult Merge(ReadOnlySpan<SealedCommandBufferView> buffers);
    CommandPrepareResult Prepare(in MergedCommandBatchView batch, in CommandPrepareContext context);
}

internal interface ICommandApplyPort
{
    CommandApplyResult Apply(in PreparedGameDelta delta);
}
```

### 4. 与成熟方案怎么接

| 能力 | 采用 | 不采用 | Adapter | 稳定 API 可见第三方类型 | 许可证 | 风险/控制 |
|---|---|---|---|---|---|---|
| Buffer backing | ArrayPool<T>/MemoryPool<T> | 自研通用 object pool | PooledCommandStorageAdapter | 否 | MIT | Return 后 writer/token 失效；池顺序不进 hash。 |
| 稳定合并 | BCL sort/priority primitives + 本仓 key | 依赖线程到达顺序；第三方 event bus | CommandBufferMerger | 否 | MIT/领域语义 | 排序必须 total order；比较器 Golden/Property。 |
| ECS apply | IWorldStorageAdapter/ecs commit-only API | 第三方 ECS CommandBuffer 直接 Apply | EcsCommandCommitExecutor | 否 | 按 ECS 引擎许可 | 所有第三方类型封装。 |
| Durable evidence | generated CommandLogRecord + neutral port | LoggingEvent 代替 CommandLog | ICommandEvidencePort | 否 | 纯 Port | persistence/Host 实现；无反向依赖。 |
| Property testing | CsCheck | 仅 example tests | StableMergePropertyTests | 否 | Apache-2.0 | 长序列/冲突/token graph 生成器。 |

**自研最小范围。** 只实现状态机、Deferred Token 域、稳定 merge、Preflight/Reservation 和 Prepared 后 commit-only 语义。通用队列、池、排序 primitive、ECS storage 和 durable介质均复用/适配。未来更换 ECS 引擎只替换 commit adapter。

### 5. 输入 / 输出 / 依赖

**Consumes**

- `ProcessorInvocationKey(TickId, Phase, ProcessorId, WorldId)`、ProcessorDescriptor structural/budget view。
- `EcsWorld` read/preflight/commit-only ports、Generation/target/capacity view。
- permission/capability 只读 view 与 coordination 提供的 expected revision/Txn idempotency context。
- generated command sequence/CommandId 与 durable record contract。

**Produces**

- 每 Processor `ProcessorCommandBuffer`、`SealedCommandBuffer`。
- `MergedCommandBatch`、immutable `PreparedGameDelta` 给 coordination。
- `CommandApplyReceipt`、DeferredEntityMap、`ChangeSet` ref 给 simulation/coordination/replication。
- generated `CommandLogRecord` 给 durable evidence Port。

**编译依赖**

- ecs、observability、generated contracts。
- 不直接依赖 simulation：Tick/Phase/ProcessorId 以中立值对象传入。

**禁止依赖**

- simulation 实现。
- coordination/replication/gas/persistence/hot-reload 实现。
- 第三方 ECS public types。
- testing。

### 6. 队列、预算、线程

| 实现对象 | 元素/资源 | 容量/预算来源 | 满载/超限动作 | 顺序/幂等键 | Metric |
|---|---|---|---|---|---|
| `ProcessorCommandBuffer` | buffer/command/bytes | `ProcessorDescriptor.Budget`、`CommandBufferMaxCommands`、`CommandBufferMaxBytes` | Prepared 前显式拒绝；不得 silent drop；Prepared 后不再容量拒绝 | `Phase + ProcessorId + LocalSequence`；`CommandId` | `runtime.command.count`、`runtime.command.bytes`、`runtime.command.rejected` |
| `CommandReservationSet` | entity slots/component capacity/token refs | 同一 CommandBuffer/Prepare budget | Prepare 失败时全量释放；成功后锁定至 Apply/Abort | `TxnId + PreparedDeltaHash` | `runtime.command.reservations` |
| `CommandEvidenceRoute` | generated CommandLogRecord | `DurableLogQueueCapacity` | 不丢弃；Backpressured 令 prepare/ingress 停止或维护 | `IdempotencyKey`/RecordSeq | `runtime.command.durable_backpressure` |

- 每 Buffer 单 writer；不同 Buffer 可并行生成，但它们不能直接改变 ECS。
- Merge/Prepare/Apply 由 Simulation Owner Thread 在固定 Barrier 线性化。
- Prepared 后 NotCancellable；取消只在 Prepared 前转 Discard/Abort。

### 7. 失败与恢复

| 分类 | 触发例 | 返回/可观察结果 | World/Session | Failure Bundle | Journal/证据查询 |
|---|---|---|---|---|---|
| 可拒绝 | unknown command kind、StructuralWrites 未声明、跨 World/Tick token | Append/Seal/Prepare 返回 Rejected；无可见写 | 不 Fault | 通常否 | buffer evidence + failing sequence |
| 可拒绝 | Generation/target/permission/capacity/conflict/预算预检失败 | Prepare Rejected，释放全部 reservation | 不 Fault；CrossWorldTxn 可 Aborted | 事务安全场景可附 fragment | Prepared前 command diagnostic/Txn record |
| 可拒绝 | 重复 Destroy/Create-write-destroy 非法图 | Merge/Prepare 以稳定 first error 拒绝 | 不 Fault | 否 | merged batch hash + conflict report |
| 可重试 | 同 Barrier 的 Deferred dependency 尚未解析且输入 buffer 未全部 Sealed | Merge Retryable；不得跨 Tick等待 | 不 Fault | 否 | missing buffer/token id |
| 可重试 | durable CommandLog route 暂时 Backpressured 且尚未 Prepared | Retryable/stop ingress | 不 Fault；deadline 升级维护 | 升级时需要 | IdempotencyKey query |
| 可重试 | ECS reservation 暂时不足但未做任何写 | Retryable only if policy明确；同 TxnId | 不 Fault | 否 | reservation attempt |
| 可致命 | stable comparer 非全序/相同输入 merge hash 不同 | FatalDeterminism | World/Session Faulted | 必须 | all sealed buffers + comparer version |
| 可致命 | Prepared 后 ECS 返回业务拒绝 | FatalContractViolation | World/Session Faulted | 必须 | Prepared delta + apply result + Txn journal |
| 可致命 | Apply 中 storage/ChangeSet 失败或 partial structural write | Indeterminate/Faulted，绝不继续 | World/Session Faulted | 必须 | CommandLog/TxnJournal/partial apply receipt |

### 8. 测试面

**本模块测试工程—单元**

- exact `Open -> Sealed -> Merged -> Prepared -> Applied`；Prepared 后 discard/append/业务拒绝不可达。
- Deferred Token 同 Tick/World/buffer generation 有效性。
- Create/Add/Set/Destroy 组合的确定 conflict rule。

**本模块测试工程—Golden**

- 同 Tick Create/Write/Destroy、重复 destroy、invalid target、cross-world token fixtures。
- PreparedGameDelta canonical hash/command order。

**本模块测试工程—Property**

- 任意 buffer arrival/interleaving 只要内容相同，merge output相同。
- 任意 token dependency DAG 在合法时唯一解析，非法 cycle/forward ref 稳定拒绝。

**本模块测试工程—故障**

- Buffer tamper、QueueFull、Processor exception before seal、durable backpressure、Apply storage fault、duplicate replay。

**`testing` Reference Host**

- 在 CrossWorldPrepare 注入每个失败点；Prepared 后只出现 Applied/AlreadyApplied/Indeterminate/Faulted。

### 9. 本模块任务拆解

#### `cmd-buffer-and-deferred-token`

- **一句话目标**：实现每 Processor 独立 Open buffer、LocalSequence 和 Tick/World 域 Deferred Token。
- **涉及文件集**：
  - `modules/command/src/Lumio.GameRuntime.Command/Lumio.GameRuntime.Command.csproj`
  - `modules/command/tests/Lumio.GameRuntime.Command.Tests/Lumio.GameRuntime.Command.Tests.csproj`
  - `modules/command/src/Lumio.GameRuntime.Command/CommandModule.cs`
  - `modules/command/src/Lumio.GameRuntime.Command/CommandServices.cs`
  - `modules/command/src/Lumio.GameRuntime.Command/Lifecycle/CommandBufferState.cs`
  - `modules/command/src/Lumio.GameRuntime.Command/Buffers/ProcessorCommandBuffer.cs`
  - `modules/command/src/Lumio.GameRuntime.Command/Buffers/CommandBufferWriter.cs`
  - `modules/command/src/Lumio.GameRuntime.Command/Tokens/DeferredEntityToken.cs`
  - `modules/command/tests/Lumio.GameRuntime.Command.Tests/BufferStateMachineTests.cs`
  - `modules/command/tests/Lumio.GameRuntime.Command.Tests/DeferredTokenGoldenTests.cs`
- **验收标准**：
  - [ ] exact 五态路径含 Prepared；Discard 仅 Prepared 前。
  - [ ] 一个 Buffer 只绑定一个 Tick/Phase/Processor/World。
  - [ ] LocalSequence checked 单调且不依赖线程时序。
  - [ ] 跨 Tick/World/buffer generation token 全拒绝。
- **依赖**：`ecs-world-and-entity-identity`、`obs-event-ports-and-context`
- **Consumes**：ProcessorInvocationKey、CommandBufferBudget。
- **Produces**：Open/Sealed buffer、DeferredEntityToken。
- **成熟方案**：ArrayPool-backed internal storage + domain state machine。
- **明确不做**：不 merge、不访问 ECS storage、不发布 command wire schema。

#### `cmd-seal-and-stable-merge`

- **一句话目标**：实现 owner transfer 的 Seal 与按 `Phase + ProcessorId + LocalSequence` 的 total-order merge。
- **涉及文件集**：
  - `modules/command/src/Lumio.GameRuntime.Command/Commands/CommandSortKey.cs`
  - `modules/command/src/Lumio.GameRuntime.Command/Buffers/SealedCommandBuffer.cs`
  - `modules/command/src/Lumio.GameRuntime.Command/Merge/CommandBufferMerger.cs`
  - `modules/command/src/Lumio.GameRuntime.Command/Merge/MergedCommandBatch.cs`
  - `modules/command/tests/Lumio.GameRuntime.Command.Tests/StableMergePropertyTests.cs`
- **验收标准**：
  - [ ] Seal 后 writer 所有 append 都拒绝。
  - [ ] Property 测试覆盖 buffer 到达乱序/并发，输出 bytes/hash 相同。
  - [ ] 比较器满足反对称、传递、全序。
  - [ ] duplicate/conflict report 以 first canonical command 稳定定位。
- **依赖**：`cmd-buffer-and-deferred-token`
- **Consumes**：SealedCommandBuffer 集合。
- **Produces**：MergedCommandBatch、CommandMergeResult。
- **成熟方案**：BCL sort/k-way merge + CsCheck。
- **明确不做**：不执行业务预检或 ECS Apply。

#### `cmd-preflight-and-prepared-delta`

- **一句话目标**：在 CrossWorldPrepare 完成全部业务校验、资源 Reservation 并产出 immutable `PreparedGameDelta`。
- **涉及文件集**：
  - `modules/command/src/Lumio.GameRuntime.Command/Prepare/CommandPreflightValidator.cs`
  - `modules/command/src/Lumio.GameRuntime.Command/Prepare/CommandReservationSet.cs`
  - `modules/command/src/Lumio.GameRuntime.Command/Prepare/PreparedGameDelta.cs`
  - `modules/command/tests/Lumio.GameRuntime.Command.Tests/PreparedBoundaryTests.cs`
- **验收标准**：
  - [ ] Generation/target/component capacity/conflict/permission/budget 每项都有 preflight失败测试。
  - [ ] 任一失败释放全部 reservation 且无 ECS 可见副作用。
  - [ ] Prepared delta immutable、含 expected revision/hash/idempotency context。
  - [ ] 测试扫描 Prepared 后代码路径不存在业务拒绝结果。
- **依赖**：`cmd-seal-and-stable-merge`、`ecs-query-read-write-views`
- **Consumes**：MergedCommandBatch、ECS read/preflight view、permission/budget。
- **Produces**：PreparedGameDelta 或 pre-prepare rejection。
- **成熟方案**：领域语义；ECS capacity via Port。
- **明确不做**：不持久化 CommitIntent，不 Apply。

#### `cmd-apply-to-ecs`

- **一句话目标**：在 `EcsCommandBufferCommit` 对 Prepared delta 执行幂等 commit-only Apply。
- **涉及文件集**：
  - `modules/command/src/Lumio.GameRuntime.Command/Apply/EcsCommandCommitExecutor.cs`
  - `modules/command/src/Lumio.GameRuntime.Command/Apply/CommandApplyReceipt.cs`
  - `modules/command/src/Lumio.GameRuntime.Command/Tokens/DeferredEntityMap.cs`
  - `modules/command/tests/Lumio.GameRuntime.Command.Tests/EcsApplyFaultTests.cs`
- **验收标准**：
  - [ ] 只接受 Prepared state；未 Prepared 输入 Fatal contract violation。
  - [ ] 允许结果只含 Applied/AlreadyApplied/Indeterminate/Faulted。
  - [ ] 重复同 IdempotencyKey 不重复 create/destroy。
  - [ ] 任意 partial storage fault 导致 fail-stop并保留 receipt/evidence。
- **依赖**：`cmd-preflight-and-prepared-delta`、`ecs-change-set-and-snapshot-view`、`ecs-world-lifecycle-fail-stop`
- **Consumes**：PreparedGameDelta、ECS commit-only Port。
- **Produces**：CommandApplyReceipt、DeferredEntityMap、ChangeSet ref。
- **成熟方案**：ECS Adapter commit primitives。
- **明确不做**：不决定 Voxel→ECS 顺序，不捕获业务拒绝。

#### `cmd-capacity-and-durable-record-route`

- **一句话目标**：落实 CommandBuffer 三维预算与 generated CommandLogRecord durable route。
- **涉及文件集**：
  - `modules/command/src/Lumio.GameRuntime.Command/Budgets/CommandBufferBudget.cs`
  - `modules/command/src/Lumio.GameRuntime.Command/Evidence/ICommandEvidencePort.cs`
  - `modules/command/src/Lumio.GameRuntime.Command/Errors/CommandFailure.cs`
  - `modules/command/tests/Lumio.GameRuntime.Command.Tests/CommandBudgetTests.cs`
- **验收标准**：
  - [ ] 容量仅来自三个矩阵参数，零 magic number。
  - [ ] Prepared 前超限显式拒绝；Prepared 后不再执行容量分支。
  - [ ] CommandLog route full 不 silent drop，保留 IdempotencyKey。
  - [ ] Diagnostic event 与 CommandLogRecord 使用不同 Port。
- **依赖**：`cmd-buffer-and-deferred-token`、`obs-durable-route-and-emergency-path`
- **Consumes**：ProcessorDescriptor.Budget、CommandBufferMax*、generated CommandLogRecord。
- **Produces**：CommandBufferBudget、ICommandEvidencePort、metrics。
- **成熟方案**：BCL checked arithmetic + neutral durable Port。
- **明确不做**：不实现 durable backend/fsync/retention。

#### `cmd-conflict-golden-property`

- **一句话目标**：建立同 Tick命令图、冲突、重放和 Prepared 边界的 Golden/Property 证据集。
- **涉及文件集**：
  - `modules/command/tests/Lumio.GameRuntime.Command.Tests/CommandConflictGoldenTests.cs`
  - `modules/command/tests/Lumio.GameRuntime.Command.Tests/CommandReplayPropertyTests.cs`
  - `modules/command/tests/fixtures/command/README.md`
- **验收标准**：
  - [ ] 覆盖 create/write/destroy、duplicate、cross-buffer token、budget、tamper、duplicate replay。
  - [ ] Golden fixture 每项包含输入 buffers、expected merged order、prepare result、apply receipt/hash。
  - [ ] Property 对随机合法/非法图定位稳定 first failure。
  - [ ] fixtures 不新增公共 command kind/ID。
- **依赖**：`cmd-seal-and-stable-merge`、`cmd-preflight-and-prepared-delta`、`cmd-apply-to-ecs`
- **Consumes**：Command module public/internal test seams。
- **Produces**：RT-D-003 evidence fixtures。
- **成熟方案**：xUnit v3 + CsCheck。
- **明确不做**：不批准 RT-D-003，不形成 wire schema。


## 3.5. `simulation` 模块设架

### 0. 模块身份证

- 目录：`modules/simulation/`
- 建议程序集：`Lumio.GameRuntime.Simulation`；13 相 contract verifier 可在 `Lumio.GameRuntime.Simulation.Contracts` 内部项目
- 建议命名空间：`Lumio.GameRuntime.Simulation`
- 优先级与阶段：P0，Architecture Gate / Foundation
- 唯一职责：暴露唯一 `run_tick` 入口并拥有 Logical Tick、固定 13 相、Processor Plan、Determinism 与 Fail-stop Session 编排。
- RT-D-001 纪律：以上是物理映射建议；即使首期合并程序集，也必须保持命名空间、internal 可见性和依赖测试，不得形成反向引用。

### 1. 它做什么

- Host 决定何时调用一次 Tick，但只能调用 `run_tick`；`SimulationSession.RunTick` 是 C# 投影，不存在能绕过 Phase Graph 的第二入口。Host wall clock、sleep/pacing 不进入模块。
- `SimulationSession` exact lifecycle 为 `Created -> Initialized -> Ready -> Running <-> Paused -> Draining -> Snapshotted -> Disposed`，任一活动状态可进 Faulted。Revision/Txn/SnapshotCut 状态委托给 coordination，不缓存第二份。
- 每个 Tick 固定执行 13 相：`IngressCapture -> DecodeAndCanonicalize -> ApplyInputs -> ProcessorPlan -> CrossWorldPrepare -> NativeJobBarrier -> CommitDecision -> VoxelCommit -> EcsCommandBufferCommit -> GasAndEventFinalize -> ReplicationProjection -> SnapshotHashMetrics -> EgressPublish`。
- `ProcessorPlanBuilder` 在执行前验证 generated `ProcessorDescriptor` 的 Role、Phase、Query、ReadSet、WriteSet、mayEmitStructuralCommands、Before/After、DeterminismClass、Budget、DiagnosticName；只检查 Processor 之间冲突，自读自写合法。
- 输入按 `SessionId + ClientCommandSeq + ArrivalClass` canonicalize；Ingress 和 Native Completion 有界。Worker 只能返回 immutable completion，在 `NativeJobBarrier` stable merge 后才可应用。
- V1 唯一 authoritative Tick Commit Point 是 `GasAndEventFinalize`。此前写入仅 WithinTickPrivate；此前任何 Processor exception/cancel/overbudget 触发 World/Session Fail-stop并从 Tick前 Snapshot+Journal恢复，不提供字段撤销。
- Commit Point 后 `ReplicationProjection`/Snapshot/Hash/Egress 不可取消；重复同 Tick 返回 `IdempotentSame` 和同一 Tick Result。成功结果含 TickId、Revision/Hash摘要、egress refs与 phase metrics。

### 2. 它明确不做什么

- 不读取/驱动 Host wall clock、Timer、sleep、pacing、进程信号。
- 不拥有 ECS storage、CommandBuffer 内容、Txn/Revision 真相、Voxel storage、Replication history。
- 不允许 Native/IO/Transport callback 或 Gameplay thread 直接写 World。
- 不定义具体 Ability、Formula、Mapping、Config业务含义或 platform mode boolean。
- 不固定通用线程池、worker count或 work-stealing 算法；并行只是可证伪优化。
- 不把 Diagnostic log 当 Tick Result/Journal。
- 不在 `GasAndEventFinalize` 之外另设 commit point。
- 不在 Faulted 后继续下一 Tick或尝试字段级 rollback。

### 3. 代码骨架

#### 3.1 建议目录树

```text
modules/simulation/
├─ src/Lumio.GameRuntime.Simulation/
│  ├─ SimulationModule.cs                      # session factory/composition
│  ├─ SimulationServices.cs                    # Host 稳定候选 facade
│  ├─ Session/SimulationSession.cs             # 单一 run_tick/lifecycle facade
│  ├─ Session/SimulationSessionState.cs        # exact public lifecycle projection
│  ├─ Session/SimulationOwnerThread.cs         # owner token/epoch
│  ├─ Tick/TickRunner.cs                       # 唯一 13 相执行器
│  ├─ Tick/TickExecutionContext.cs             # Tick 内私有状态
│  ├─ Tick/TickRunResult.cs                    # success/reject/fault/idempotent result
│  ├─ Tick/TickResultCache.cs                  # duplicate Tick IdempotentSame
│  ├─ Phases/TickPhase.cs                      # generated 13 phase projection
│  ├─ Phases/PhaseGraph.cs                     # fixed graph/order
│  ├─ Phases/PhaseContractTable.cs             # write/failure/cancel/visibility/commit matrix
│  ├─ Planning/ProcessorPlanBuilder.cs         # descriptor validation/toposort
│  ├─ Planning/ProcessorPlan.cs                # immutable ordered plan
│  ├─ Planning/ProcessorInvocation.cs          # query/view/buffer invocation
│  ├─ Ingress/IngressQueue.cs                  # bounded batch
│  ├─ Ingress/InputCanonicalizer.cs             # ArrivalClass/sequence/order
│  ├─ Native/NativeCompletionQueue.cs          # bounded reliable completion
│  ├─ Native/NativeCompletionMerger.cs         # JobId/token stable order
│  ├─ Determinism/DeterminismContext.cs         # RNG streams/time unit/hash declarations
│  ├─ Determinism/StateHashCoordinator.cs       # provider hash aggregation
│  ├─ Failure/FailStopController.cs            # first failure/Session Faulted
│  ├─ Failure/PhaseFailureRecord.cs             # phase/processor/evidence
│  └─ Errors/SimulationFailure.cs              # result classification
└─ tests/Lumio.GameRuntime.Simulation.Tests/
   ├─ PhaseGraphGoldenTests.cs
   ├─ ProcessorPlanPropertyTests.cs
   ├─ IngressCanonicalizationTests.cs
   ├─ NativeBarrierFaultTests.cs
   ├─ FailStopCommitPointTests.cs
   ├─ DuplicateTickTests.cs
   └─ DeterminismReplayTests.cs
```

#### 3.2 核心类型

| 类型/可见性 | 职责与关键字段/不变式 | 关键方法草图与失败表达 | 线程与生命周期 |
|---|---|---|---|
| `SimulationModule` / `sealed class` | 持有 session factory与下游 module facade references；不持有 active Tick状态。 | `CreateSession(in SimulationSessionCreateRequest)`。 | Composition Root；dispose 前无 active session。 |
| `SimulationSession` / `sealed class` / stable candidate | 字段 SessionId、state、next TickId、phase graph、plan registry、determinism、downstream ports、result cache；不缓存 revision state。 | `Initialize`、`PlanProcessors`、`run_tick`/`RunTick`、`Pause`、`Resume`、`Drain`、`Dispose`。 | 单 Session Owner/Simulation Thread 推进；只读 snapshot并发。 |
| `SimulationSessionState` / generated/public projection | exact states；Stale lifecycle epoch拒绝。 | guarded transitions；Faulted只允许 evidence/snapshot recovery handoff/dispose。 | Host/Session owner。 |
| `TickRunner` / `sealed class` / internal | 唯一执行器；字段 fixed phase handlers array和 phase contract table。 | `TickRunResult Run(in HostTickRequestView request)`；每相显式 enter/exit/failure。 | Owner Thread；不可重入；Fault后停止。 |
| `PhaseGraph` / `sealed immutable class` | exact 13 phases；ordinal来自 generated contract；不允许 runtime plugin插相。 | `ValidateAgainstGeneratedContract`、`GetNext`。 | startup构建后只读。 |
| `PhaseContractTable` / `sealed immutable class` | 每相可写域、失败类、取消点、overbudget、visibility、commit flag；唯一 commit at GasAndEventFinalize。 | `ValidateTransition`、`CanCancel`、`VisibilityAfter`。 | startup只读；Golden。 |
| `ProcessorPlanBuilder` / `sealed class` | 验证 descriptor/role/capability/phase/read-write/dependency/budget；stable topo order。 | `ProcessorPlanBuildResult Build(ReadOnlySpan<ProcessorDescriptorView>)`。 | activation/control thread；计划 immutable。 |
| `ProcessorPlan` / `sealed immutable class` | 按 Phase/Dependency/ProcessorId total order；包含 invocation budgets和parallel-safe groups。 | `GetPhasePlan(TickPhase)`。 | 跨 Tick只读；Config/Scope切换生成新 plan。 |
| `IngressQueue` / `sealed class` | bounded immutable envelopes；capture freezes batch/arrival metadata。 | `TryEnqueue`、`CaptureForTick`；full action per ArrivalClass。 | Host producers/Owner consumer。 |
| `NativeCompletionQueue` / `sealed class` | reliable bounded completion；JobId/token/idempotency/stale marker。 | `TryPublish`、`DrainAtBarrier`、`StopDispatchSignal`。 | worker producers/Owner consumer。 |
| `DeterminismContext` / `sealed class` / stable candidate view | RNG Seed/Stream、logical time unit、event ordering、hash registry/version；无 wall clock。 | `OpenRngStream`、`RegisterHashInput`、`CaptureEvidence`。 | Owner Thread分配；Processor拿 scoped stream。 |
| `FailStopController` / `sealed class` | 只记录首个 failure，冻结 phase/processor/tick/evidence，令 ECS/Session Faulted。 | `FailStop(in PhaseFailureRecord failure)` 幂等；后续失败附加但不改first。 | Owner Thread；Fault后只读。 |

#### 3.3 稳定候选 API 与内部边界

- Host 只见单一 `run_tick` 语义；`RunTick` 是 C# naming projection，不是另一条路径。
- Phase 枚举、状态、Tick Result字段和错误分类直接消费 generated contract，本文不重声明数值。
- `SimulationSession` 对 Revision/Txn/SnapshotCut 仅持 coordination facade/只读 view，不保存 mutable副本。

```csharp
// 设计草图；未冻结。run_tick 是唯一语义入口。
public interface ISimulationSession : IDisposable
{
    SimulationSessionStateView State { get; }
    TickRunResult RunTick(in HostTickRequestView request);
    LifecycleResult Pause(in SessionEpoch epoch);
    LifecycleResult Resume(in SessionEpoch epoch);
    LifecycleResult Drain(in SessionEpoch epoch);
}

internal interface IPhaseHandler
{
    TickPhase Phase { get; }
    PhaseRunResult Execute(ref TickExecutionContext context);
}
```

### 4. 与成熟方案怎么接

| 能力 | 采用 | 不采用 | Adapter | 稳定 API 可见第三方类型 | 许可证 | 风险/控制 |
|---|---|---|---|---|---|---|
| 有界队列 | System.Threading.Channels | 自研通用 lock-free queue | IngressQueue/NativeCompletionQueue adapter | 否 | MIT | full action显式；Channel ordering不作为跨来源 canonical order。 |
| 计划排序 | BCL collections/priority queue + stable comparer | 通用 job scheduler替代 phase contract | ProcessorPlanBuilder | 否 | MIT/领域语义 | stable topo tie-breaker ProcessorId；cycle path确定。 |
| 线程池 | Host/.NET ThreadPool 或 Native job Port | Runtime自研通用线程池 | INativeJobDispatchPort | 否 | MIT/Host | V1 correctness不依赖worker count；只收 immutable completion。 |
| Hash/RNG | BCL算法按 generated IDs | Random.Shared/对象hash/自研算法ID | DeterminismAdapters | 否 | MIT | seed/stream/algorithm/version冻结；跨平台Level2规则。 |
| Replay/property | CsCheck + xUnit | 只做示例 Tick测试 | DeterminismReplayTests | 否 | Apache-2.0 | 输入/worker completion乱序仍相同结果。 |

**自研最小范围。** 仅自研 13 相 graph/contract executor、Processor descriptor验证/stable plan、determinism context、commit-point可见性和 Fail-stop orchestration。通用线程池、Channel、Hash/RNG primitive与测试 runner复用成熟方案。

### 5. 输入 / 输出 / 依赖

**Consumes**

- Host `HostTickRequestView`、bounded Ingress、Host Capability、immutable ConfigSnapshot。
- generated ProcessorDescriptor/Phase contract、Gameplay processor factory/hooks。
- ecs query/write/snapshot facade、command buffer/prepare/apply facade。
- coordination、gas、replication、persistence、observability 的调用 Port。
- Native/IO `NativeCompletionBatch`。

**Produces**

- `TickRunResult`、`IdempotentSame` duplicate result、first failure evidence。
- 按 Phase 调用下游产生的 ChangeSet/Revision/Replication/Egress refs。
- `DeterminismEvidence`、StateHash summary、phase/processor metrics。
- 给 Host 的 egress publish batch引用与 lifecycle ack。

**编译依赖**

- ecs、command、coordination、gas、replication、persistence、config、observability。
- generated contracts/processor descriptors/phase matrix。

**禁止依赖**

- Host wall-clock/pacing/network具体实现。
- Voxel内部实现。
- Gameplay product assembly具体类型（只用generated contract/factory Port）。
- testing。
- 全局 service locator或反向 callback singleton。

### 6. 队列、预算、线程

| 实现对象 | 元素/资源 | 容量/预算来源 | 满载/超限动作 | 顺序/幂等键 | Metric |
|---|---|---|---|---|---|
| `IngressQueue` | validated envelope/input bytes | `IngressQueueCapacity`、`IngressQueueBytes` | 按 ArrivalClass当前/下一Tick/拒绝并计数；持续满报告Host | `CommandId`/input sequence；同来源FIFO，跨来源canonical sort | `runtime.ingress.depth`、`runtime.ingress.rejected` |
| `NativeCompletionQueue` | `NativeCompletionEnvelope` | `NativeCompletionQueueCapacity` | 停止派发新job/backpressure；不silent drop | `JobId`/Token；barrier stable merge | `runtime.native_completion.depth`、`runtime.native_barrier.wait` |
| `TickExecutionBudget` | phase/processor logical work/commands/bytes | ProcessorDescriptor.Budget、Host Tick budget、ConfigSnapshot | pre-commit overrun Fail-stop；post-commit phase failure Fault | `TickId + Phase + ProcessorId` | `runtime.tick.duration`、`runtime.phase.budget_overrun` |
| `TickResultCache` | 最近已提交 Tick result | generated idempotency/replay window；不自造wire字段 | 重复同Tick返回 IdempotentSame；超窗口由 recovery/resync策略 | `SessionId + TickId` | `runtime.tick.duplicate` |

- V1 每 active WorldSlot一个 Simulation Owner Thread；run_tick不可重入。
- 只有不共享写集且有稳定 merge的工作可并行；completion在NativeJobBarrier前不可应用。
- Commit Point前后取消语义取自 phase matrix：VoxelCommit起 NotCancellable；GasAndEventFinalize唯一提交点。

### 7. 失败与恢复

| 分类 | 触发例 | 返回/可观察结果 | World/Session | Failure Bundle | Journal/证据查询 |
|---|---|---|---|---|---|
| 可拒绝 | descriptor缺字段/错误role/unknown phase/dependency cycle/inter-processor conflict | plan build Rejected，Session不进入Running或保持旧plan | 不Fault active world；激活失败 | 安全/架构失配可附 | descriptor hash + validation report |
| 可拒绝 | input schema/permission/length/ArrivalClass不可接受 | Decode/ApplyInputs BusinessReject；不进入processor写 | 不Fault | 通常否 | CommandId/input seq rejection |
| 可拒绝 | Pause/Drain状态下新 Tick或stale epoch | Lifecycle/Tick Rejected | 不Fault | 否 | lifecycle audit |
| 可重试 | Native completion未就绪且deadline未到 | Tick/phase Retryable only before any authoritative write；同Tick不重复提交 | 不Fault或保持paused | 持续超时升级 | JobId/token query |
| 可重试 | Ingress capture资源暂满可按ArrivalClass下一Tick | 明确 deferred-to-next-tick result | 不Fault | 否 | input sequence/arrival record |
| 可重试 | Snapshot provider在post-commit异步阶段暂不可用 | Tick已提交；记录post-commit pending并按契约查询 | Session是否暂停由Host policy | 升级时需要 | SnapshotId/provider status |
| 可致命 | Processor exception/cancel/overbudget在commit前且已有字段写 | Fail-stop first failure；当前World不可继续 | World/Session Faulted | 必须 | Tick前snapshot + command/txn journal + phase failure |
| 可致命 | phase顺序/visibility/commit point不变量破坏 | FatalContractViolation | Session Faulted | 必须 | phase trace + contract version |
| 可致命 | duplicate同Tick得到不同result/hash或barrier completion丢失 | FatalDeterminism/ProcessFault | Session/可能Process Faulted | 必须 | 两次result、input hash、completion batch |

### 8. 测试面

**本模块测试工程—单元**

- exact 13 phase order/contract matrix/唯一 GasAndEventFinalize commit point。
- exact SimulationSession lifecycle和stale epoch reject。
- Processor之间冲突/cycle；自读自写合法。

**本模块测试工程—Golden**

- tick-phase-contract fixture；ProcessorDescriptor valid/invalid。
- 固定输入的 phase trace、TickResult、StateHash。

**本模块测试工程—Property**

- descriptor输入排列不影响plan；合法DAG stable topo。
- Ingress跨来源/worker completion乱序不改变canonical batch/result。
- duplicate same Tick一律IdempotentSame。

**本模块测试工程—故障**

- QueueFull、Native timeout、budget overrun、Processor exception、world dispose race、hash mismatch、post-commit snapshot failure。

**`testing` Reference Host**

- Level1 bit replay；Level2 first difference；crash at each phase boundary；LocalEmbedded真实Envelope路径。

### 9. 本模块任务拆解

#### `sim-session-and-run-tick-entry`

- **一句话目标**：实现 exact Session lifecycle与唯一 `run_tick` facade/owner-thread/reentrancy边界。
- **涉及文件集**：
  - `modules/simulation/src/Lumio.GameRuntime.Simulation/Lumio.GameRuntime.Simulation.csproj`
  - `modules/simulation/tests/Lumio.GameRuntime.Simulation.Tests/Lumio.GameRuntime.Simulation.Tests.csproj`
  - `modules/simulation/src/Lumio.GameRuntime.Simulation/SimulationModule.cs`
  - `modules/simulation/src/Lumio.GameRuntime.Simulation/SimulationServices.cs`
  - `modules/simulation/src/Lumio.GameRuntime.Simulation/Session/SimulationSession.cs`
  - `modules/simulation/src/Lumio.GameRuntime.Simulation/Session/SimulationSessionState.cs`
  - `modules/simulation/src/Lumio.GameRuntime.Simulation/Session/SimulationOwnerThread.cs`
  - `modules/simulation/tests/Lumio.GameRuntime.Simulation.Tests/SessionLifecycleTests.cs`
- **验收标准**：
  - [ ] exact lifecycle names/transitions与Faulted路径。
  - [ ] public API只有一个RunTick语义入口；API scan无alternate phase runner。
  - [ ] run_tick跨线程/重入/stale session epoch拒绝。
  - [ ] SimulationSession不包含mutable Revision/Txn/SnapshotCut字段。
- **依赖**：`ecs-world-lifecycle-fail-stop`、`cmd-buffer-and-deferred-token`、`cfg-tick-boundary-activation`
- **Consumes**：Session create context、module facades、HostTickRequest。
- **Produces**：SimulationSession/ISimulationSession。
- **成熟方案**：领域state machine + owner-thread guard。
- **明确不做**：不实现phase handlers、wall clock或pacing。

#### `sim-phase-graph-13`

- **一句话目标**：把generated tick-phase contract投影为不可变exact 13相graph/contract table。
- **涉及文件集**：
  - `modules/simulation/src/Lumio.GameRuntime.Simulation/Phases/TickPhase.cs`
  - `modules/simulation/src/Lumio.GameRuntime.Simulation/Phases/PhaseGraph.cs`
  - `modules/simulation/src/Lumio.GameRuntime.Simulation/Phases/PhaseContractTable.cs`
  - `modules/simulation/tests/Lumio.GameRuntime.Simulation.Tests/PhaseGraphGoldenTests.cs`
- **验收标准**：
  - [ ] 顺序逐项匹配13相，无插入/删除/重命名。
  - [ ] 只有GasAndEventFinalize CommitPoint=true。
  - [ ] VoxelCommit起NotCancellable；visibility与generated fixture一致。
  - [ ] schema/graph不一致时startup Fatal，不猜测修复。
- **依赖**：`sim-session-and-run-tick-entry`
- **Consumes**：generated tick-phase-contract。
- **Produces**：PhaseGraph、PhaseContractTable。
- **成熟方案**：generated contract + immutable arrays。
- **明确不做**：不执行processor或定义新phase。

#### `sim-processor-plan-validator`

- **一句话目标**：实现generated ProcessorDescriptor验证、stable topo与并行安全组。
- **涉及文件集**：
  - `modules/simulation/src/Lumio.GameRuntime.Simulation/Planning/ProcessorPlanBuilder.cs`
  - `modules/simulation/src/Lumio.GameRuntime.Simulation/Planning/ProcessorPlan.cs`
  - `modules/simulation/src/Lumio.GameRuntime.Simulation/Planning/ProcessorInvocation.cs`
  - `modules/simulation/tests/Lumio.GameRuntime.Simulation.Tests/ProcessorPlanPropertyTests.cs`
- **验收标准**：
  - [ ] 验证全部 descriptor字段和Capability/Role/phase。
  - [ ] 只拒绝Processor之间冲突，自ReadSet∩WriteSet合法。
  - [ ] 任意descriptor排列得到同一plan/hash。
  - [ ] cycle报告canonical最小cycle路径。
- **依赖**：`sim-phase-graph-13`、`ecs-query-read-write-views`、`cmd-buffer-and-deferred-token`
- **Consumes**：ProcessorDescriptorView、ConfigSnapshot、Capability。
- **Produces**：immutable ProcessorPlan。
- **成熟方案**：BCL graph/toposort + CsCheck。
- **明确不做**：不固定worker count或thread pool。

#### `sim-ingress-and-native-completion`

- **一句话目标**：实现Queue Contract Matrix的Ingress与reliable Native Completion及barrier stable merge。
- **涉及文件集**：
  - `modules/simulation/src/Lumio.GameRuntime.Simulation/Ingress/IngressQueue.cs`
  - `modules/simulation/src/Lumio.GameRuntime.Simulation/Ingress/InputCanonicalizer.cs`
  - `modules/simulation/src/Lumio.GameRuntime.Simulation/Native/NativeCompletionQueue.cs`
  - `modules/simulation/src/Lumio.GameRuntime.Simulation/Native/NativeCompletionMerger.cs`
  - `modules/simulation/tests/Lumio.GameRuntime.Simulation.Tests/IngressCanonicalizationTests.cs`
  - `modules/simulation/tests/Lumio.GameRuntime.Simulation.Tests/NativeBarrierFaultTests.cs`
- **验收标准**：
  - [ ] 只用矩阵容量参数，无无界collection。
  - [ ] ArrivalClass当前/下一Tick/拒绝分支全部可观测。
  - [ ] Native full停止新job且不silent drop。
  - [ ] completion到达顺序不改变barrier batch/hash。
- **依赖**：`sim-session-and-run-tick-entry`、`obs-bounded-diagnostic-routing`
- **Consumes**：Host input envelopes、NativeCompletionEnvelope。
- **Produces**：CanonicalInputBatch、NativeCompletionBatch。
- **成熟方案**：System.Threading.Channels + domain canonicalizer。
- **明确不做**：不拥有Socket/native worker或重试线程池。

#### `sim-determinism-context-and-state-hash`

- **一句话目标**：实现scoped RNG/time/order/hash registry并聚合各provider canonical hash。
- **涉及文件集**：
  - `modules/simulation/src/Lumio.GameRuntime.Simulation/Determinism/DeterminismContext.cs`
  - `modules/simulation/src/Lumio.GameRuntime.Simulation/Determinism/StateHashCoordinator.cs`
  - `modules/simulation/tests/Lumio.GameRuntime.Simulation.Tests/DeterminismReplayTests.cs`
- **验收标准**：
  - [ ] 同seed/stream/input得到相同序列；Processor stream相互独立。
  - [ ] wall clock/thread id/object hash/queue watermarks不在state hash registry。
  - [ ] provider以stable ID顺序聚合，不依赖registration order。
  - [ ] Level1 replay bit一致；Level2产生first-difference结构。
- **依赖**：`sim-processor-plan-validator`、`ecs-change-set-and-snapshot-view`
- **Consumes**：generated hash/RNG IDs、module hash providers。
- **Produces**：DeterminismContext、StateHashSummary。
- **成熟方案**：BCL algorithms按generated IDs适配。
- **明确不做**：不承诺x86/ARM浮点bit一致，不自造算法ID。

#### `sim-fail-stop-and-tick-result`

- **一句话目标**：实现13相执行器、唯一commit point、first-failure Fail-stop和duplicate Tick结果缓存。
- **涉及文件集**：
  - `modules/simulation/src/Lumio.GameRuntime.Simulation/Tick/TickRunner.cs`
  - `modules/simulation/src/Lumio.GameRuntime.Simulation/Tick/TickExecutionContext.cs`
  - `modules/simulation/src/Lumio.GameRuntime.Simulation/Tick/TickRunResult.cs`
  - `modules/simulation/src/Lumio.GameRuntime.Simulation/Tick/TickResultCache.cs`
  - `modules/simulation/src/Lumio.GameRuntime.Simulation/Failure/FailStopController.cs`
  - `modules/simulation/src/Lumio.GameRuntime.Simulation/Failure/PhaseFailureRecord.cs`
  - `modules/simulation/src/Lumio.GameRuntime.Simulation/Errors/SimulationFailure.cs`
  - `modules/simulation/tests/Lumio.GameRuntime.Simulation.Tests/FailStopCommitPointTests.cs`
  - `modules/simulation/tests/Lumio.GameRuntime.Simulation.Tests/DuplicateTickTests.cs`
- **验收标准**：
  - [ ] phase trace固定13相；各handler只能写contract允许domain。
  - [ ] commit point前任一已写故障令ECS/Session Faulted，无field undo。
  - [ ] commit point后重复同Tick返回IdempotentSame/同result/hash。
  - [ ] first failure固定，后续错误不覆盖；Failure Bundle context完整。
- **依赖**：`sim-phase-graph-13`、`sim-processor-plan-validator`、`sim-ingress-and-native-completion`、`sim-determinism-context-and-state-hash`、`cmd-apply-to-ecs`、`obs-failure-bundle-assembly`
- **Consumes**：全部phase ports、CanonicalInputBatch、ProcessorPlan、DeterminismContext。
- **Produces**：TickRunResult、PhaseFailureRecord、TickResult cache。
- **成熟方案**：领域phase executor。
- **明确不做**：不实现Host recovery、snapshot backend或egress transport。


## 3.6. `coordination` 模块设架

### 0. 模块身份证

- 目录：`modules/coordination/`
- 建议程序集：`Lumio.GameRuntime.Coordination`；Generated Voxel adapter 放 `Lumio.GameRuntime.Coordination.VoxelAdapters`
- 建议命名空间：`Lumio.GameRuntime.Coordination`
- 优先级与阶段：P0，Architecture Gate / Foundation
- 唯一职责：唯一拥有 `SessionRevisionVector`、CrossWorldTxnV1、Reservation、participant marker与 `SnapshotCut`，按 Voxel→ECS 固定顺序恢复性提交。
- RT-D-001 纪律：以上是物理映射建议；即使首期合并程序集，也必须保持命名空间、internal 可见性和依赖测试，不得形成反向引用。

### 1. 它做什么

- 每个 SimulationSession 构造一个 `CrossWorldCoordinator`，生命周期 exact `Created -> Ready -> Running -> Draining -> Disposed`，任一状态可 Faulted；simulation 只持 facade。
- Coordinator 维护 `SessionRevisionVector` 唯一状态和只读 view。`begin_snapshot_cut` 在 Tick Barrier 固定完整 vector并从 generated Voxel Snapshot Contract取得 immutable token/revision；Runtime不复制Voxel storage。
- 单笔 Txn exact `Created -> Prepared -> CommitIntent -> Committed`；`Prepared -> Aborted/Expired`；只有已持久化 `CommitIntent` 的 Apply阶段可 `Indeterminate`。participant marker使用 generated enum `NotStarted/Unknown/Applied/Failed`。
- `prepare_txn` 在可见写前校验 expected revisions、deadline tick、权限/capacity、ECS `PreparedGameDelta`与Voxel reservation。Prepare只有验证/租约，无可见副作用。
- 所有参与者准备完成后，先通过 `ITxnJournalPort` 持久确认 `CommitIntent`，再固定 `VoxelCommit -> EcsCommandBufferCommit`。每步按 TxnId/participant token幂等，完成后追加marker；双方完成才写Committed和新Revision。
- 如果参与者Apply成功但marker未持久化，状态为Unknown，恢复必须通过 `ITxnParticipantQueryPort` 幂等查询收敛，不猜测。Lost result/duplicate request返回原结果，不重复扣费或写。
- 进入Draining停止新Prepare；在途Txn必须Committed、pre-intent Abort/Expire，或留下可查询的Indeterminate证据。成功结果总带TxnId/SessionId和ResultRevisionVector。

### 2. 它明确不做什么

- 不拥有 Voxel chunk/block/revision storage或依赖VoxelEngine源码；只依 generated contract。
- 不拥有 ECS storage/CommandBuffer内容；只消费 Prepared delta和commit Port。
- 不实现 WAL/file/database/fsync/group-commit backend；归 persistence/Host。
- 不引入通用 XA/跨进程 durable 2PC或默认补偿事务。
- 不读取 wall clock；deadline和lease只用Logical Tick/声明Capability。
- 不定义 Gameplay具体权限/Formula；只消费已计算的permission/precondition result。
- 不在 Native/Rust锁内调用 C#，不接受worker直接回调Gameplay。
- 不允许调用方选择 ECS→Voxel顺序或使用Boolean participant marker。

### 3. 代码骨架

#### 3.1 建议目录树

```text
modules/coordination/
├─ src/Lumio.GameRuntime.Coordination/
│  ├─ CoordinationModule.cs                    # coordinator factory/ports
│  ├─ CoordinationServices.cs                  # revision/txn/snapshot facades
│  ├─ Lifecycle/CoordinatorState.cs            # Created/Ready/Running/Draining/Disposed/Faulted
│  ├─ Revision/SessionRevisionVectorStore.cs   # 唯一 mutable vector owner
│  ├─ Revision/SessionRevisionVectorView.cs    # generated vector只读投影
│  ├─ Transactions/CrossWorldCoordinator.cs    # 事务门面与状态索引
│  ├─ Transactions/CrossWorldTxnState.cs       # generated state投影
│  ├─ Transactions/TxnParticipantState.cs      # generated enum view，禁止bool
│  ├─ Transactions/TxnRecord.cs                # Txn metadata/result/index
│  ├─ Transactions/TxnIdempotencyIndex.cs      # duplicate/lost-result lookup
│  ├─ Prepare/TxnPrepareCoordinator.cs          # revisions/preconditions/reservations
│  ├─ Prepare/PreparedVoxelTokenLease.cs        # generated token lease wrapper
│  ├─ Reservations/ReservationLease.cs          # owner/deadline/release
│  ├─ Commit/CommitIntentCoordinator.cs         # durable intent first
│  ├─ Commit/ParticipantApplyCoordinator.cs     # fixed Voxel→ECS order
│  ├─ Commit/ITxnParticipantQueryPort.cs        # Unknown resolution
│  ├─ Journal/ITxnJournalPort.cs                # caller-owned durable Port
│  ├─ Snapshot/SnapshotCutCoordinator.cs        # complete vector + provider tokens
│  ├─ Snapshot/SnapshotCutLease.cs              # pin/release
│  ├─ Budgets/CoordinationBudget.cs             # txn/reservation/cut limits
│  └─ Errors/CoordinationFailure.cs             # rejection/retry/fatal
├─ src/Lumio.GameRuntime.Coordination.VoxelAdapters/
│  └─ GeneratedVoxelWorldPortAdapter.cs         # generated Authority/Snapshot contract wrapper
└─ tests/Lumio.GameRuntime.Coordination.Tests/
   ├─ RevisionVectorPropertyTests.cs
   ├─ TxnStateMachineGoldenTests.cs
   ├─ PrepareNoSideEffectTests.cs
   ├─ CommitIntentOrderingTests.cs
   ├─ CrashBoundaryRecoveryTests.cs
   ├─ SnapshotCutConsistencyTests.cs
   └─ DuplicateLostResultTests.cs
```

#### 3.2 核心类型

| 类型/可见性 | 职责与关键字段/不变式 | 关键方法草图与失败表达 | 线程与生命周期 |
|---|---|---|---|
| `CoordinationModule` / `sealed class` | 持有 coordinator factory与Port registration；不持久化介质。 | `CreateCoordinator(in CoordinationCreateRequest)`。 | Composition Root；active coordinator清零后dispose。 |
| `CrossWorldCoordinator` / `sealed class` / stable candidate | 字段 SessionId、state、revision store、txn index、journal/voxel/ecs ports、snapshot coordinator。 | `ReadRevision`、`PrepareTxn`、`CommitTxn`、`AbortTxn`、`ResolveTxn`、`BeginSnapshotCut`。 | Owner Thread状态迁移；async participant只经completion。 |
| `SessionRevisionVectorStore` / `sealed class` / internal | 唯一 mutable vector；各domain revision单调，更新只在Committed/Config/Snapshot contract点。 | `ReadView`、`CompareExpected`、`AdvanceCommitted`；negative/regression Fatal。 | Owner Thread写，并发只读 immutable copy。 |
| `SessionRevisionVectorView` / `readonly record struct` / generated projection | 字段严格来自公共schema：Tick/Game/Voxel/Chunk/Replication/Config/SchemaEpoch。 | comparison/hash delegates；不新增field。 | 不可变；跨模块读取。 |
| `TxnRecord` / `sealed class` / internal | TxnId/session/tick/command/prediction/expected/observed/deadline/prepared refs/intent/participant enum/result。 | guarded transitions；duplicate request returns stored outcome。 | Owner Thread；durable snapshot/recovery重建。 |
| `ReservationLease` / `sealed class` / internal | owner TxnId、participant token、DeadlineTick、state；不得后台静默续期。 | `Commit`、`Release`、`ExpireAt(TickId)`；幂等。 | Owner Thread/participant ack；结束释放。 |
| `ITxnJournalPort` / `interface` / Port owned by coordination | 追加/查询 generated TxnJournalRecord；必须durable ack CommitIntent。 | `Append`、`QueryTxn`、`QueryRecord`；IdempotencyKey。 | 实现由persistence/Host；调用可同步logical ack或bounded completion。 |
| `CommitIntentCoordinator` / `sealed class` | 验证所有Prepared，然后Append+durable ack intent；失败前不Apply。 | `CommitDecisionResult PersistIntent(in PreparedTxnView)`。 | Owner Thread at CommitDecision。 |
| `ParticipantApplyCoordinator` / `sealed class` | 固定 Voxel→ECS；更新participant marker；Prepared后无业务拒绝。 | `ApplyResult Apply(in CommitIntentView)`；查询Unknown。 | Owner Thread at VoxelCommit/EcsCommit；NotCancellable。 |
| `ITxnParticipantQueryPort` / `interface` | 按 TxnId/token查询Applied/NotApplied/Unknown基础设施结果，并映射generated participant state。 | `ParticipantQueryResult Query(in ParticipantQueryKey key)`。 | 可worker/IO；结果在Barrier消费。 |
| `SnapshotCutCoordinator` / `sealed class` | 在Barrier冻结完整revision vector并收集ECS/GAS/Replication/Voxel provider token。 | `SnapshotCutResult Begin(in SnapshotCutRequest)`、`Release`。 | Owner Thread pin；worker consume immutable tokens。 |
| `SnapshotCutLease` / `sealed class` | SnapshotId/vector/provider tokens/expiry/lease count；不可替换token。 | `AcquireProvider`、`Release`、`Invalidate`。 | 多reader只读；最后释放后unpin。 |

#### 3.3 稳定候选 API 与内部边界

- 候选 `read_revision`、`begin_snapshot_cut`、`prepare_txn`、`commit_txn`、`abort_txn`、`resolve_txn` 保持；参数/enum/fields直接使用generated contract。
- `ITxnJournalPort` 放在调用方 coordination assembly；persistence因 `persistence -> coordination` 可实现，不形成 `coordination -> persistence`。
- Participant marker只能是generated `NotStarted/Unknown/Applied/Failed`；任何 bool adapter在编译/API scan中拒绝。

```csharp
// 设计草图；未冻结。
public interface ICoordinationServices
{
    SessionRevisionVectorView ReadRevision();
    TxnPrepareResult PrepareTxn(in CrossWorldTxnRequestView request);
    TxnCommitResult CommitTxn(TxnId txnId);
    TxnResolutionResult ResolveTxn(TxnId txnId);
    SnapshotCutResult BeginSnapshotCut(in SnapshotCutRequestView request);
}

public interface ITxnJournalPort
{
    DurableAppendResult Append(in TxnJournalRecordView record);
    TxnJournalQueryResult Query(TxnId txnId);
}
```

### 4. 与成熟方案怎么接

| 能力 | 采用 | 不采用 | Adapter | 稳定 API 可见第三方类型 | 许可证 | 风险/控制 |
|---|---|---|---|---|---|---|
| 事务协议 | 本仓CrossWorldTxnV1 | System.Transactions/XA/通用2PC | CrossWorldCoordinator | 否 | 领域语义 | 固定本地双参与者/Revision/marker，通用方案语义不匹配且过重。 |
| durable journal | generated records + Port | LoggingEvent/数据库SDK直入coordinator | ITxnJournalPort | 否 | Port | backend/durability由persistence/Host。 |
| Voxel | Generated Voxel World Port | 依赖VoxelEngine源码/内部handle | GeneratedVoxelWorldPortAdapter | 否 | 外部契约许可 | 只见generated token/revision/result。 |
| 有界completion | System.Threading.Channels由simulation/persistence拥有 | coordinator内无界Task list | participant completion Port | 否 | MIT | 只在Barrier消费，deadline用Tick。 |
| Property/model test | CsCheck state-machine model | 只做happy path | Txn model harness | 否 | Apache-2.0 | crash at every durable/apply/marker boundary。 |

**自研最小范围。** 只实现Revision vector owner、双参与者Prepare/Reservation、durable CommitIntent优先、固定apply顺序、enum marker/Unknown查询与SnapshotCut。Journal介质、Voxel/ECS storage、线程池和时钟不自研。未来backend/participant更换不改变状态机。

### 5. 输入 / 输出 / 依赖

**Consumes**

- command `PreparedGameDelta`/apply Port，ecs snapshot/revision view。
- Generated Voxel Authority/Snapshot Contract 的 prepare/commit/query/capture tokens。
- Host/Simulation提供Session/World/Tick/Deadline/Barrier context与permission/capability preconditions。
- `ITxnJournalPort`实现、observability event/durable correlation。

**Produces**

- `SessionRevisionVectorView` 给 simulation/replication/persistence/testing。
- Txn prepare/commit/abort/resolve result与participant enum。
- `SnapshotCutLease`/complete provider tokens给persistence。
- generated TxnJournalRecord/durable evidence和FailureContext。

**编译依赖**

- ecs、command、observability、generated Voxel contracts/neutral contracts。
- 不依赖persistence implementation；Journal Port由caller ownership。

**禁止依赖**

- persistence backend/project。
- VoxelEngine源码/内部storage。
- simulation implementation（Barrier/Tick以value context传入）。
- Host wall clock/Socket。
- testing。

### 6. 队列、预算、线程

| 实现对象 | 元素/资源 | 容量/预算来源 | 满载/超限动作 | 顺序/幂等键 | Metric |
|---|---|---|---|---|---|
| `TxnJournalRoute` | TxnJournalRecord | `DurableLogQueueCapacity` | 不丢弃；CommitIntent未durable ack前禁止首写；full停ingress/维护 | RecordSeq/IdempotencyKey/PreviousHash | `runtime.txn.journal_latency`、`runtime.txn.journal_backpressure` |
| `ReservationSet` | ECS/Voxel reservation leases | CoordinationBudget/Host capability/DeadlineTick | Prepared前拒绝或Retry；到期Abort/Expire；不得静默延长 | `TxnId + ParticipantId` | `runtime.txn.reservations`、`runtime.txn.expired` |
| `ParticipantCompletion` | prepare/apply/query result | NativeCompletionQueueCapacity（由simulation queue承载） | stop dispatch/backpressure；timeout转query/Indeterminate | TxnId/participant token | `runtime.txn.participant_wait` |
| `SnapshotCutLeaseBudget` | provider tokens/pinned bytes | Snapshot provider limits/Persistence capability | Retryable pre-cut；cut成功后任一provider失败令cut Invalid，不伪完整 | SnapshotId + RevisionVectorHash | `runtime.snapshot_cut.pinned` |

- Revision/Txn transitions/commit order由Simulation Owner Thread在指定Barrier执行。
- Voxel/IO异步只返回bounded token/result；无worker直接更新TxnRecord。
- CommitIntent后VoxelCommit与EcsCommit NotCancellable；participant query可以async但结果在Barrier收敛。

### 7. 失败与恢复

| 分类 | 触发例 | 返回/可观察结果 | World/Session | Failure Bundle | Journal/证据查询 |
|---|---|---|---|---|---|
| 可拒绝 | ExpectedRevision conflict、deadline已过、permission/capacity/chunk unavailable | Prepare Rejected，pre-intent Aborted/Expired，无可见副作用 | 不Fault | 按安全/txn需要 | TxnJournal pre-intent record/prepare report |
| 可拒绝 | 非法/过期 reservation token、duplicate不一致payload | Rejected；同TxnId同payload返回原结果，不同payload拒绝 | 不Fault | 可附 | idempotency index + request hash |
| 可拒绝 | Draining后新Prepare/调用方指定错误顺序 | Rejected lifecycle/contract | 不Fault | 否 | coordinator lifecycle audit |
| 可重试 | reservation暂未取得且deadline未到 | Retryable同TxnId；无副作用 | 不Fault | 否 | participant prepare query |
| 可重试 | participant/query result暂不可用 | Retryable/Unknown；同token查询 | CommitIntent后Session保持pending/可能暂停 | 升级/超时需要 | TxnJournal + participant query |
| 可重试 | journal队列Backpressured且CommitIntent未确认 | Retryable/stop ingress；绝不Apply | 不Fault或维护 | 升级时必须 | IdempotencyKey/last RecordSeq |
| 可致命 | CommitIntent durable ack后任一participant基础设施失败 | Indeterminate/Faulted，记录Unknown/Failed，不猜测 | Session Faulted/恢复 | 必须 | TxnJournal + participant query + Prepared delta |
| 可致命 | ECS/Voxel apply返回业务拒绝或顺序被倒置 | FatalContractViolation | Session Faulted | 必须 | phase trace/receipts/journal |
| 可致命 | Journal hash链/Revision regression/txn index损坏 | FatalIntegrity | Session/Host维护或Faulted | 必须 | journal scan/report/snapshot vector |

### 8. 测试面

**本模块测试工程—单元**

- exact coordinator/txn state machines；pre-intent只能Abort/Expire。
- participant enum四态，无Boolean序列化/API。
- Revision vector单调与expected comparison。

**本模块测试工程—Golden**

- cross-world committed/aborted/partial/lost result fixtures。
- TxnJournal record chain与SnapshotCut complete vector。

**本模块测试工程—Property**

- 任意duplicate/retry/lost response不重复Apply。
- 任意crash boundary恢复最终Committed/Aborted/Expired/Indeterminate合法收敛。

**本模块测试工程—故障**

- revision conflict、chunk unavailable、journal full/corrupt、crash after intent/voxel/ecs/before marker、query unavailable。

**`testing` Reference Host**

- ReferenceVoxelPort注入每个participant边界；SnapshotCut同Revision Hash；Replay recovery。

### 9. 本模块任务拆解

#### `coord-revision-vector-view`

- **一句话目标**：实现唯一 mutable `SessionRevisionVectorStore` 与generated read/compare/hash view。
- **涉及文件集**：
  - `modules/coordination/src/Lumio.GameRuntime.Coordination/Lumio.GameRuntime.Coordination.csproj`
  - `modules/coordination/tests/Lumio.GameRuntime.Coordination.Tests/Lumio.GameRuntime.Coordination.Tests.csproj`
  - `modules/coordination/src/Lumio.GameRuntime.Coordination/CoordinationModule.cs`
  - `modules/coordination/src/Lumio.GameRuntime.Coordination/CoordinationServices.cs`
  - `modules/coordination/src/Lumio.GameRuntime.Coordination/Revision/SessionRevisionVectorStore.cs`
  - `modules/coordination/src/Lumio.GameRuntime.Coordination/Revision/SessionRevisionVectorView.cs`
  - `modules/coordination/tests/Lumio.GameRuntime.Coordination.Tests/RevisionVectorPropertyTests.cs`
- **验收标准**：
  - [ ] fields逐项来自generated schema，无本地扩展。
  - [ ] 任意advance保持各revision非负/单调/SchemaEpoch规则。
  - [ ] SimulationSession无第二mutable副本。
  - [ ] canonical hash不依赖chunk set insertion order。
- **依赖**：`ecs-change-set-and-snapshot-view`、`obs-event-ports-and-context`
- **Consumes**：generated initial vector/domain advance receipts。
- **Produces**：ReadRevision/compare/advance API。
- **成熟方案**：领域store + immutable generated view。
- **明确不做**：不实现Txn、Snapshot编码或Voxel storage。

#### `coord-txn-state-and-idempotency`

- **一句话目标**：实现exact CrossWorldTxn状态、四态participant marker和duplicate/lost-result索引。
- **涉及文件集**：
  - `modules/coordination/src/Lumio.GameRuntime.Coordination/Lifecycle/CoordinatorState.cs`
  - `modules/coordination/src/Lumio.GameRuntime.Coordination/Transactions/CrossWorldCoordinator.cs`
  - `modules/coordination/src/Lumio.GameRuntime.Coordination/Transactions/CrossWorldTxnState.cs`
  - `modules/coordination/src/Lumio.GameRuntime.Coordination/Transactions/TxnParticipantState.cs`
  - `modules/coordination/src/Lumio.GameRuntime.Coordination/Transactions/TxnRecord.cs`
  - `modules/coordination/src/Lumio.GameRuntime.Coordination/Transactions/TxnIdempotencyIndex.cs`
  - `modules/coordination/tests/Lumio.GameRuntime.Coordination.Tests/TxnStateMachineGoldenTests.cs`
  - `modules/coordination/tests/Lumio.GameRuntime.Coordination.Tests/DuplicateLostResultTests.cs`
- **验收标准**：
  - [ ] 状态图exact；Indeterminate只在durable intent后Apply阶段。
  - [ ] participant type/API/serialization无bool。
  - [ ] 同TxnId同request重复返回原结果；不同request hash拒绝。
  - [ ] Draining停止新Prepare并列出所有in-flight。
- **依赖**：`coord-revision-vector-view`
- **Consumes**：Txn request/generated states。
- **Produces**：TxnRecord/idempotency query。
- **成熟方案**：领域state machine + dictionary keyed by generated TxnId。
- **明确不做**：不做participant prepare/apply或journal IO。

#### `coord-prepare-and-reservation`

- **一句话目标**：编排expected revision、ECS PreparedGameDelta、Voxel reservation和DeadlineTick的无副作用Prepare。
- **涉及文件集**：
  - `modules/coordination/src/Lumio.GameRuntime.Coordination.VoxelAdapters/Lumio.GameRuntime.Coordination.VoxelAdapters.csproj`
  - `modules/coordination/src/Lumio.GameRuntime.Coordination/Prepare/TxnPrepareCoordinator.cs`
  - `modules/coordination/src/Lumio.GameRuntime.Coordination/Prepare/PreparedVoxelTokenLease.cs`
  - `modules/coordination/src/Lumio.GameRuntime.Coordination/Reservations/ReservationLease.cs`
  - `modules/coordination/src/Lumio.GameRuntime.Coordination.VoxelAdapters/GeneratedVoxelWorldPortAdapter.cs`
  - `modules/coordination/tests/Lumio.GameRuntime.Coordination.Tests/PrepareNoSideEffectTests.cs`
- **验收标准**：
  - [ ] 全部业务/容量/chunk/permission失败在Prepared前。
  - [ ] 失败释放两participant reservations且state Aborted/Expired。
  - [ ] prepare失败前后ECS/Voxel visible revision相同。
  - [ ] lease只按DeadlineTick/owner推进，不读wall clock。
- **依赖**：`coord-txn-state-and-idempotency`、`cmd-preflight-and-prepared-delta`
- **Consumes**：Txn request、PreparedGameDelta、generated Voxel prepare/token。
- **Produces**：Prepared Txn/ReservationLease。
- **成熟方案**：Generated Voxel Adapter + domain lease。
- **明确不做**：不写CommitIntent、不Apply。

#### `coord-commit-intent-and-apply-order`

- **一句话目标**：先durable CommitIntent，再按Voxel→ECS幂等Apply并写四态marker/Committed。
- **涉及文件集**：
  - `modules/coordination/src/Lumio.GameRuntime.Coordination/Journal/ITxnJournalPort.cs`
  - `modules/coordination/src/Lumio.GameRuntime.Coordination/Commit/CommitIntentCoordinator.cs`
  - `modules/coordination/src/Lumio.GameRuntime.Coordination/Commit/ParticipantApplyCoordinator.cs`
  - `modules/coordination/src/Lumio.GameRuntime.Coordination/Commit/ITxnParticipantQueryPort.cs`
  - `modules/coordination/tests/Lumio.GameRuntime.Coordination.Tests/CommitIntentOrderingTests.cs`
- **验收标准**：
  - [ ] 测试证明journal durable ack严格先于首个Voxel写。
  - [ ] phase trace严格VoxelCommit再EcsCommit。
  - [ ] Prepared后业务拒绝触发Fatal contract violation。
  - [ ] 每participant marker/Committed追加顺序与PreviousHash链正确。
- **依赖**：`coord-prepare-and-reservation`、`cmd-apply-to-ecs`、`obs-durable-route-and-emergency-path`
- **Consumes**：Prepared Txn、ITxnJournalPort、Voxel/ECS apply/query Ports。
- **Produces**：CommitIntent/participant records/Committed result/new vector。
- **成熟方案**：generated durable records + caller-owned Port。
- **明确不做**：不实现backend/fsync/group commit，不允许ECS先写。

#### `coord-snapshot-cut`

- **一句话目标**：在Tick Barrier固定完整RevisionVector及ECS/GAS/Replication/Voxel immutable provider tokens。
- **涉及文件集**：
  - `modules/coordination/src/Lumio.GameRuntime.Coordination/Snapshot/SnapshotCutCoordinator.cs`
  - `modules/coordination/src/Lumio.GameRuntime.Coordination/Snapshot/SnapshotCutLease.cs`
  - `modules/coordination/tests/Lumio.GameRuntime.Coordination.Tests/SnapshotCutConsistencyTests.cs`
- **验收标准**：
  - [ ] 所有provider token标同SnapshotId/vector/hash。
  - [ ] Voxel token来自Generated Voxel Snapshot Contract，Runtime不复制chunk storage。
  - [ ] 任一provider缺失/错误使cut失败，不产伪完整manifest。
  - [ ] lease release/unpin无泄漏/use-after-release。
- **依赖**：`coord-revision-vector-view`、`coord-txn-state-and-idempotency`、`ecs-change-set-and-snapshot-view`
- **Consumes**：SnapshotCutRequest、provider capture Ports。
- **Produces**：SnapshotCutLease/complete provider manifest。
- **成熟方案**：领域cut coordinator + generated provider tokens。
- **明确不做**：不编码/写盘/激活Snapshot。

#### `coord-crash-resolution-and-journal-port`

- **一句话目标**：覆盖CommitIntent后每个crash窗口、Unknown participant查询与Journal恢复收敛。
- **涉及文件集**：
  - `modules/coordination/src/Lumio.GameRuntime.Coordination/Recovery/TxnRecoveryResolver.cs`
  - `modules/coordination/src/Lumio.GameRuntime.Coordination/Errors/CoordinationFailure.cs`
  - `modules/coordination/tests/Lumio.GameRuntime.Coordination.Tests/CrashBoundaryRecoveryTests.cs`
- **验收标准**：
  - [ ] 注入crash after intent/voxel apply/before marker/ecs apply/before marker/committed。
  - [ ] Unknown必须query participant，不以缺marker推断NotStarted。
  - [ ] 恢复重复Apply只能Applied/AlreadyApplied。
  - [ ] journal corrupt/ambiguous结果Fatal并产FailureBundle context。
- **依赖**：`coord-commit-intent-and-apply-order`、`coord-snapshot-cut`、`obs-failure-bundle-assembly`
- **Consumes**：TxnJournal scan、participant query、Prepared artifacts。
- **Produces**：resolved Txn state/repair records/failure evidence。
- **成熟方案**：领域recovery algorithm。
- **明确不做**：不实现journal backend或进程重启。


## 3.7. `replication` 模块设架

### 0. 模块身份证

- 目录：`modules/replication/`
- 建议程序集：`Lumio.GameRuntime.Replication`；Voxel 适配可放 `Lumio.GameRuntime.Replication.VoxelAdapters`，仅依赖 generated Voxel Replica Contract
- 建议命名空间：`Lumio.GameRuntime.Replication`
- 优先级与阶段：P0 / Foundation；Prediction、Presentation Diff 与容量 Hardening 进入 Vertical Slice
- 唯一职责：唯一拥有非对称 Mapping、Net/Local Identity Mapping、Tombstone、Baseline/Delta/History、Dirty Set，以及 Server Projection 与 Client 权威 Apply/Resync 语义。
- RT-D-001 纪律：以上是物理映射建议；即使首期合并程序集，也必须保持命名空间、internal 可见性和依赖测试，不得形成反向引用。

### 1. 它做什么

- `RuntimeCompositionRoot` 以已验证 MappingSet、`ConfigSnapshot`、World-local ECS/GAS Views、`ICoordinationReadPort` 和 `IVoxelReplicaPort` 构造 `ReplicationModule`；每个 Server World/Client Replica World 创建独立 `ReplicationContext`，Context 随对应 World 存活。
- 在 Server 侧，`ReplicationProjection` 只在 `ReplicationProjection` Phase 读取冻结于当前 Tick/Revision 的不可变 ECS/GAS/Voxel 视图，产生 `FullSnapshotProjection` 或 `DeltaProjection`；编码、分片、Socket 发送归 Host。
- 在 Client 侧，`ClientAuthorityApplyCoordinator` 只由 Simulation Owner Thread 在声明的 Apply Barrier 调用一次；它按六步流程校验、恢复 Confirmed `PredictionFrame`、原子准备并应用 ECS/GAS/Voxel 权威状态、删除已确认命令、按原序重放未确认命令、产生 `PresentationDiff`。
- 唯一可变真相包括：`MappingRegistry`、`NetEntityMappingTable`、`TombstoneRegistry`、每 Context 的 Baseline/Ack/History/DirtySet/Resync 状态；它不缓存 ECS/GAS/Voxel 的第二份权威字段。
- 成功结果必须携带 generated `SnapshotId`、Revision 关联、MappingSet Hash 与明确的 Ack/Apply/Resync 结果；未知、过期、乱序、Gap、Tombstone 冲突和 History Exhaustion 均返回有分类结果，不能尽力接受。
- Context 生命周期严格为 `Created -> Snapshotting -> AwaitingBaselineAck -> Active`、`Active -> Resyncing -> Active`、`Active/Resyncing -> Draining -> Closed`，任一状态可转 `Faulted`；Closed/Faulted 后所有旧 Baseline、映射和迟到 Delta 被拒绝。

### 2. 它明确不做什么

- 不实现 Socket、TLS、Connection、Transport ACK、分片、重传 Reactor 或带宽调度；这些归 Host/Transport Adapter。
- 不定义 Game Component、Ability Formula、AOI 业务判断、权限内容或具体 Mapping；这些由 Game/Contract Toolchain 生成，本模块只验证和执行。
- 不把 Server World 与 Client Replica World 合并，不共享对象引用，也不要求双方 Component 对称；World 与 LocalEntityId 始终独立。
- 不把 `LocalEntityId` 写入 Wire 或持久身份；`NetEntityId`、provisional namespace、Ownership/Authority revision 均消费 generated contract。
- 不拥有 ECS/GAS/Voxel Storage；它通过只读 Projection Provider 与 `IVoxelReplicaPort` 参与原子 Apply。
- 不通过 Diagnostic Log 重建 Baseline/History，也不让 Observability 事件成为复制状态真相。
- 不依赖 Host Connection、`testing` 或具体 VoxelEngine 实现；Voxel 只经版本化 generated Replica Contract。
- 不在 LocalEmbedded 绕过 Envelope、Serializer、Schema、权限、大小限制、有界 Ingress 或 Tick 交付；可省略的仅是 Socket/TLS。

### 3. 代码骨架

#### 3.1 建议目录树

```text
modules/replication/
├─ src/
│  ├─ Lumio.GameRuntime.Replication/
│  │  ├─ ReplicationModule.cs                 # Composition 门面；创建/销毁 Context，不保存第二份 World 状态
│  │  ├─ ReplicationServices.cs               # 显式依赖集合与生命周期句柄
│  │  ├─ Lifecycle/ReplicationContextState.cs # V1.3 Context 状态名与迁移表
│  │  ├─ Lifecycle/ReplicationContext.cs      # 单 Role/World 的 Mapping/Baseline/History 所有者
│  │  ├─ Mapping/MappingRegistry.cs           # 校验并索引 generated 非对称 Mapping
│  │  ├─ Mapping/MappingSetView.cs            # 不可变、Hash 绑定的只读 Mapping 视图
│  │  ├─ Identity/NetEntityMappingTable.cs    # NetEntityId <-> World-local LocalEntityId
│  │  ├─ Identity/ProvisionalRemapTable.cs    # provisional -> authoritative 映射与代际校验
│  │  ├─ Identity/TombstoneRegistry.cs        # DestroyRevision 与 Context 引用 pin
│  │  ├─ Identity/TombstoneHorizonCalculator.cs # 冻结的 max(...) 下界纯函数
│  │  ├─ Projection/ReplicationProjection.cs  # Server FullSnapshot/Delta 投影编排
│  │  ├─ Projection/DirtySet.cs               # Mapping-aware dirty key 集合
│  │  ├─ Projection/ProjectionBatch.cs        # 有界不可变投影批
│  │  ├─ History/BaselineStore.cs             # Baseline、独立 BaselineAck 与引用 pin
│  │  ├─ History/DeltaHistory.cs              # 有界 From/To Revision 历史
│  │  ├─ History/ReplicationBudget.cs          # History/批次/实体/字节预算值对象
│  │  ├─ Apply/ClientAuthorityApplyCoordinator.cs # 精确六步 Client Apply
│  │  ├─ Apply/AuthorityApplyPlan.cs           # ECS/GAS/Voxel 三域 Prepared 计划
│  │  ├─ Apply/ConfirmedCommandSequence.cs     # 中立 generated 命令确认序号包装
│  │  ├─ Apply/PresentationDiff.cs             # 非权威表现差异输出
│  │  ├─ Resync/ResyncCoordinator.cs           # Gap/Unknown/Exhaustion 的唯一恢复分支
│  │  ├─ Ports/IEcsReplicationView.cs          # ECS 只读 Projection/Apply Port
│  │  ├─ Ports/IGasReplicationView.cs          # GAS 只读 Projection/Prediction Port
│  │  ├─ Ports/ICoordinationReadPort.cs        # Revision/SnapshotCut 只读 Port
│  │  ├─ Ports/IReplicationEnvelopeCodecPort.cs # Host/codec Port，稳定 API 不见第三方类型
│  │  └─ Errors/ReplicationFailure.cs          # Rejected/Retryable/Fatal 分类
│  └─ Lumio.GameRuntime.Replication.VoxelAdapters/
│     └─ GeneratedVoxelReplicaPortAdapter.cs   # 映射 generated capture/restore/token/idempotent result
└─ tests/
   ├─ Lumio.GameRuntime.Replication.Tests/
   │  ├─ MappingRegistryGoldenTests.cs
   │  ├─ IdentityMappingPropertyTests.cs
   │  ├─ TombstoneHorizonPropertyTests.cs
   │  ├─ BaselineDeltaHistoryTests.cs
   │  ├─ ClientSixStepApplyTests.cs
   │  ├─ ResyncFaultMatrixTests.cs
   │  └─ LocalEmbeddedPipelineTests.cs
   └─ Lumio.GameRuntime.Replication.Benchmarks/
      └─ ProjectionApplyBenchmarks.cs
```

#### 3.2 核心类型

| 类型/可见性 | 职责与关键字段/不变式 | 关键方法草图与失败表达 | 线程与生命周期 |
|---|---|---|---|
| `ReplicationModule` / `sealed class` / stable candidate, 未冻结 | 构造 Context 的窄门面。字段：`ReplicationServices Services`；不缓存 Revision/World。 | `CreateContext(in ReplicationContextDescriptor) -> ReplicationResult<ReplicationContextHandle>`；`CloseContext(handle)`；重复创建以 ContextId/Role/WorldId 判定。 | Composition Root 创建并 dispose；可被 Host 控制面调用，状态写入委托 Owner Thread。Disposed 后拒绝新 Context。 |
| `ReplicationContext` / `sealed class` / internal | 单 Role/World 复制状态所有者。字段：`ContextId`、`Role`、`WorldId`、`ReplicationContextState`、`MappingRegistry`、`BaselineStore`、`DeltaHistory`、`TombstoneRegistry`；Role/World 终生不变。 | `BeginSnapshot`、`AcceptBaselineAck`、`EnterActive`、`BeginResync`、`Drain`、`Fault`；非法迁移返回 `ReplicationFailure.InvalidState`。 | 仅 Simulation Owner Thread 写；Snapshot encoder 可持 immutable lease。Closed/Faulted 后旧输入拒绝。 |
| `ReplicationContextState` / `enum` / stable candidate, 未冻结 | 只含 README 原状态：`Created, Snapshotting, AwaitingBaselineAck, Active, Resyncing, Draining, Closed, Faulted`。 | 状态迁移由 `ReplicationContext` 内部方法控制，无 public setter。 | 值对象；不自行 dispose。 |
| `MappingRegistry` / `sealed class` / internal | 校验并索引 generated MappingSet。字段：`MappingSetId`、`SchemaEpoch`、`MappingSetHash`、按 source/role/visibility 的确定序索引；Hash 与输入字节绑定。 | `ValidateAndLoad(ReadOnlyMemory<byte>, IGeneratedMappingValidator) -> ReplicationResult<MappingSetView>`；`Resolve(MappingKey)`；未知必需字段/空 Field 映射拒绝。 | 加载可在 Worker；激活/替换在 Tick Barrier。View 并发只读，旧引用释放后回收。 |
| `NetEntityMappingTable` / `sealed class` / internal | 维护 `NetEntityId -> LocalEntityId` 与反向索引；每表绑定单 World/Role；Local generation mismatch 永不命中。 | `TryBind`、`TryResolveLocal`、`TryResolveNet`、`DestroyAndTombstone`、`RemapProvisional`；重复 authoritative ID 或跨 World local ID 返回拒绝/致命不变量。 | Owner Thread 写，并发只读只能经 immutable snapshot。Context Close 时清空并使 generation 失效。 |
| `TombstoneRegistry` / `sealed class` / internal | 保存 `NetEntityId, DestroyRevision` 与仍引用它的 Baseline/History/Reconnect/Prediction/Migration pins。GC 只能在所有 pins 越过 horizon 后。 | `Add`、`Pin`、`ReleasePin`、`CanCollect(in TombstoneHorizonInputs)`、`CollectEligible`；未知 pin 或负引用计数为 Fatal。 | Owner Thread 写；Snapshot/History lease 可并发持 pin。随 Context 销毁。 |
| `TombstoneHorizonCalculator` / `static class` / internal | 实现冻结公式：`max(outstandingBaseline, retainedDeltaHistory, reconnectWindow, predictionRollbackWindow, migrationReplayPin)`；不读 wall clock。 | `Compute(in TombstoneHorizonInputs) -> RevisionHorizon`；任一输入缺失返回显式不可回收结果，不猜测零。 | 纯函数、线程安全、无生命周期。 |
| `BaselineStore` / `sealed class` / internal | 保存 FullSnapshot baseline、独立 BaselineAck、引用 pin 和字节预算；Transport ACK 不改变它。 | `Stage`、`Acknowledge`、`TryGet`、`Expire`、`Pin/Release`；未知/旧 Ack 返回 Rejected 或幂等原结果。 | Owner Thread 写；immutable baseline lease 可在 encoder/transport worker 读取。Close 释放全部 lease。 |
| `DeltaHistory` / `sealed class` / internal | 按 `BaseSnapshotId + FromRevision + ToRevision` 保存有界不可变 Delta。迭代顺序由 Revision/Sequence 固定。 | `Append`、`TryBuildRepairRange`、`TrimToBudget`；不能满足 Gap 时返回 `HistoryExhausted`，由 Resync 处理。 | Owner Thread 发布；编码 worker 只读 lease。Context Dispose 释放 pooled buffers。 |
| `ReplicationProjection` / `sealed class` / internal | Server 侧从冻结 Views 与 Mapping 生成有界 Full/Delta projection；字段遍历使用 generated canonical order。 | `BuildFullSnapshot(in ProjectionInput) -> ReplicationResult<FullSnapshotProjection>`；`BuildDelta(in DeltaProjectionInput)`；预算超限不返回截断权威批。 | 仅在 `ReplicationProjection` Phase 调用；可把 immutable batch 交给 encoder。Module dispose 后拒绝。 |
| `ClientAuthorityApplyCoordinator` / `sealed class` / internal | 实现精确六步 Apply；先全部 Prepare，再以一个 Owner Thread 原子可见单元应用 ECS/GAS/Voxel，最后重放命令并产表现差异。字段：ECS/GAS/Voxel Ports、PredictionHistory、ReplayPort。 | `ApplyEnvelope(in ValidatedEnvelope, in ApplyContext) -> ReplicationApplyResult`；任一 Prepare 可拒绝；首个 Apply 后业务拒绝视为 Fatal contract violation。 | 只由 Client Simulation Owner Thread 在声明 Barrier 调用。Context Fault/Close 后拒绝。 |
| `IVoxelReplicaPort` / `interface` / generated Port façade | 端口类型由 generated Voxel Replica Contract 提供；Adapter 只暴露 Revision、Snapshot/Mutation Token、幂等结果，不暴露 Chunk/裸指针。 | 候选语义映射到 generated `capture/restore` 等操作：prepare authority overlay、apply prepared、rollback frame、capture revision；具体方法名以生成物为准。 | 调用方 replication 持 Port；Replica World Owner Thread 发起，Native completion 只能回 Barrier。Host/Adapter dispose 后调用返回 PortClosed。 |
| `ReplicationBudget` / `readonly record struct` / internal | 字段来自 `ReplicationHistoryWindow`、`ReplicationHistoryBytes` 及 Mapping/Capability 的 batch/entity/byte 限制；所有值非负且有单位。 | `Validate`、`CanAppend`、`CanMaterialize`；不含未经测量的默认数字。 | 不可变、线程安全、由 ConfigSnapshot 构造。 |
| `ReplicationFailure` / `readonly record struct` / stable candidate, 未冻结 | 包含 generated error identity、`FailureClass`、Context/Snapshot/Revision/Mapping 关联与安全 detail code；不暴露异常作为契约。 | factory: `Rejected`、`Retryable`、`Fatal`；异常只在 Adapter 边界转换为分类结果并保留 evidence hash。 | 不可变；跨线程可传。Fatal 由 caller 触发 Context/Session Fault 与 Failure Bundle。 |

#### 3.3 稳定候选 API 与内部边界

- 稳定候选面只暴露 generated ID/Revision/Envelope 视图、本仓 `ReplicationResult<T>` 和不可变 `PresentationDiff`；第三方 ECS、MessagePack、Channel、集合实现不得出现。
- `IVoxelReplicaPort` 的契约由 generated neutral assembly 所有；`GeneratedVoxelReplicaPortAdapter` 属本模块内部装配，不把 VoxelEngine Storage 或 native pointer 暴露给 Runtime consumer。
- `ConfirmedCommandSequence` 使用中立 generated command-sequence contract，避免形成 `replication -> command` 物理程序集边；Simulation 负责实际 replay 入口。
- FullSnapshot/Delta typed body、Envelope 字段、message type、resync reason 和错误码全由架构源生成，本设计不新增字段。

```csharp
// 稳定候选，未冻结；ID/Revision/Envelope 类型来自 generated contracts。
public interface IReplicationRuntime
{
    ReplicationResult<ReplicationContextHandle> CreateContext(
        in ReplicationContextDescriptor descriptor);
    ReplicationResult CloseContext(ReplicationContextHandle handle);
}

internal interface IEcsReplicationView
{
    EcsProjectionLease CaptureProjection(in SnapshotCutView cut);
    PreparedEcsAuthorityApply PrepareAuthority(in EcsAuthorityDelta delta);
    IdempotentApplyResult ApplyPrepared(in PreparedEcsAuthorityApply prepared);
}

internal interface IGasReplicationView
{
    GasProjectionLease CaptureProjection(in SnapshotCutView cut);
    PredictionFrame RestoreConfirmed(PredictionFrameId frameId);
    PreparedGasAuthorityApply PrepareAuthority(in GasAuthorityDelta delta);
    IdempotentApplyResult ApplyPrepared(in PreparedGasAuthorityApply prepared);
}

// 实际成员由 Generated Voxel Replica Contract 生成；这里仅锁定调用语义。
internal interface IVoxelReplicaPortAdapter
{
    VoxelPrepareResult PrepareAuthoritativeOverlay(in VoxelAuthorityDelta delta);
    IdempotentApplyResult ApplyPreparedOverlay(in VoxelPreparedToken token);
    VoxelRollbackResult RollbackToPredictionFrame(PredictionFrameId frameId);
    VoxelRevisionStamp CaptureReplicaRevision();
}
```

### 4. 与成熟方案怎么接

| 能力 | 采用 | 不采用 | Adapter | 稳定 API 可见第三方类型 | 许可证 | 风险/控制 |
|---|---|---|---|---|---|---|
| 有界 History/Ingress | `System.Threading.Channels` 仅由 Host/Simulation 队列 Adapter 使用；History 本体是有界 immutable lease store | 自研 lock-free ring/event bus；无限 `ConcurrentQueue` | `BoundedIngressAdapter`、`DeltaHistory` | 否 | BCL MIT | Channel full mode 不足以表达 Full Resync；领域动作由 `ResyncCoordinator` 决定。 |
| 内存复用 | `ArrayPool<byte>` / `MemoryPool<byte>` 包装 Projection/History lease | 按对象地址排序；全局通用对象池 | `ProjectionBufferPoolAdapter` | 否 | BCL MIT | lease/use-after-return 由 generation 与 tests 守护；地址不进 Hash。 |
| Envelope primitive codec | 仓库统一 `MessagePackCanonicalCodecAdapter` 或 Host codec Port，只读 generated字段顺序 | Typeless/Contractless/global resolver；模块私有 Wire 格式 | `IReplicationEnvelopeCodecPort` | 否 | MIT | 字节语义由架构源/Canonical writer 冻结，库仅提供 primitive。 |
| Voxel Replica | Generated Voxel Replica Contract + 窄 Adapter | 直接引用 VoxelEngine 内部 crate/storage；Host 偷渡业务 Apply | `GeneratedVoxelReplicaPortAdapter` | 否 | 项目生成契约 | 契约 method name/version mismatch 在 Architecture Gate 拒绝。 |
| ECS Storage | 消费 `IEcsReplicationView`，不绑定 Friflo/Arch | 直接查询第三方 world/archetype 类型 | ECS-owned view adapter | 否 | 随 ECS Adapter | Projection 只能读取冻结 view，不能跨 Tick 持 mutable ref。 |
| 属性/故障测试 | xUnit v3 + CsCheck + SharpFuzz；架构源 fixtures | 自研 runner/fuzzer | 测试工程 fixture adapters | 否 | Apache-2.0 / MIT | Fuzz 样本必须保存 envelope/mapping/hash，不能只记录异常。 |

**自研最小范围。** 只自研 Mapping 执行、Identity/Tombstone、Baseline/Delta/History、六步 Apply、Resync 与原子可见语义。Channel、buffer pool、primitive codec、测试框架、Voxel/ECS 实现均复用成熟方案或生成契约。替换 ECS/codec/Voxel 实现时，只需更换对应 Adapter 并重跑 Golden/Property/Differential/Benchmark。

### 5. 输入 / 输出 / 依赖

**Consumes**

- 来自 `ecs` 的 `EcsProjectionLease`、`PreparedEcsAuthorityApply`、`IdempotentApplyResult`，在 `ReplicationProjection` 或 Client Apply Barrier 使用。
- 来自 `gas` 的 `GasProjectionLease`、`PredictionFrame`、`PreparedGasAuthorityApply`。
- 来自 `coordination` 的 `SessionRevisionVectorView`、`SnapshotCutView` 与 authoritative revision receipt。
- 来自 `config` 的不可变 `ConfigSnapshot` 与 Mapping/History/byte budgets。
- 来自 generated Game Contract 的 `MappingSet`、`NetEntityId`、typed FullSnapshot/Delta/Envelope body。
- 来自 Generated Voxel Replica Contract 的 replica capture/restore/mutation tokens 与 revision stamps。
- 来自 Host/Simulation Ingress 的已做 framing/size/auth envelope，以及 BaselineAck/ResyncRequest。

**Produces**

- 给 Host Transport Adapter：`FullSnapshotProjection`、`DeltaProjection`、`BaselineAck`/`DeltaAck`/`ResyncRequest` 的 typed body。
- 给 Client Presentation Adapter：`PresentationDiff`；它不是权威 World 状态。
- 给 `persistence`：`ReplicationSnapshotProvider`、Baseline/History manifest/hash 与 replay state。
- 给 `simulation`：`ReplicationApplyResult`、`ConfirmedCommandSequence`、显式 Resync/ContextFault action。
- 给 `observability`：结构化 Snapshot/Delta/Ack/Gap/Resync/Tombstone/Prediction evidence。

**编译依赖**

- 允许：`ecs`、`gas`、`coordination`、`config`、`observability`、generated Game/Replication contracts、Generated Voxel Replica Contract。
- 逻辑上消费中立 `ConfirmedCommandSequence`；不得因此新增到 `command` 的程序集依赖。
- 实现引用方向与 `modules/README.md` DAG 一致；Host 只实现 Port，不被本模块源码引用。

**禁止依赖**

- Host Socket/Connection/Transport implementation、Renderer/Presentation implementation。
- 具体 Game gameplay assembly、Mapping generator implementation。
- VoxelEngine internal storage/native pointer、第三方 ECS concrete types。
- `testing` 程序集、`persistence` 实现、Hot Reload implementation。
- 任何 bypass Envelope/Serializer/权限/大小/queue 的 LocalEmbedded shortcut。

### 6. 队列、预算、线程

| 实现对象 | 元素/资源 | 容量/预算来源 | 满载/超限动作 | 顺序/幂等键 | Metric |
|---|---|---|---|---|---|
| `ReplicationHistoryStore` | `BaselineLease`、`DeltaLease`、Tombstone/context pins | `ReplicationHistoryWindow`、`ReplicationHistoryBytes` | 无法 trim 且新权威历史不能安全保存时，Context 进入 Full Resync；按 Host policy 断开，不丢中间 Delta 冒充连续 | `SnapshotId + FromRevision + ToRevision + Sequence` | `runtime.replication.history.items`、`.bytes`、`.exhausted_total` |
| `ProjectionBatchBudget` | 实体/字段/字节 projection entries | Mapping/Config/Host Capability 中的 batch/entity/byte 限制 | Build 返回 `BudgetExceeded`；FullSnapshot 由调用者重新分批/拒绝，Delta 不返回截断权威结果 | `SnapshotId + Revision + MappingSetHash` | `runtime.replication.projection.bytes`、`.budget_reject_total` |
| Host-owned `IngressQueue` | validated replication envelope/input | `IngressQueueCapacity`、`IngressQueueBytes` | 按 Queue Matrix 拒绝/背压/断开；本模块不直接读 transport thread | Envelope sequence / CommandId/input sequence | `runtime.ingress.depth`、`runtime.ingress.full_total` |
| `VoxelReplicaCompletion` through Simulation Native Completion | 不可变 generated Voxel completion | `NativeCompletionQueueCapacity` | 可靠结果不可丢；满载使相关 Context/Session Fault 或停止接入 | `JobId/Token`、Voxel prepared token | `runtime.native_completion.full_total`、`runtime.replication.voxel_completion_latency` |

- 所有 Mapping 激活、Context 状态、Identity/Tombstone、Baseline/Ack/History 与权威 Apply 仅由 Simulation Owner Thread 写。
- Transport/IO/codec worker 只能处理不可变 Envelope/Projection lease；Completion 通过有界队列回到声明 Barrier。
- Client 三域 Apply 先完成全部 Prepare；首个 Apply 后不再允许业务拒绝。任何 partial visibility/invariant violation触发 Context/Session Fault。
- 对象地址、Dictionary insertion order、worker completion timing、transport ACK 与 diagnostic timestamp 不进入权威 Hash。

### 7. 失败与恢复

| 分类 | 触发例 | 返回/可观察结果 | World/Session | Failure Bundle | Journal/证据查询 |
|---|---|---|---|---|---|
| 可拒绝 | Envelope 长度/完整性/Schema/Mapping Hash 错误 | `ReplicationFailure.Rejected(MalformedEnvelope\|MappingMismatch)`；分配前拒绝 | 不 Fault；恶意/重复阈值可由 Host 断开 | 通常否；计数/样本可入 Diagnostic | 查询 Envelope hash、ContextId、MappingSetHash、rejection code |
| 可拒绝 | 旧 Revision、未知 Local mapping、Tombstone 冲突 | Rejected；不改变 Context/Baseline/World | 不 Fault；Gap 路径可转 Resyncing | 必要时 Failure Bundle fragment，仅当不变量疑似损坏 | 查询 Tombstone、mapping snapshot、last accepted revision |
| 可拒绝 | provisional remap namespace/authority domain 不匹配 | Rejected；保持 provisional/authoritative tables 原样 | 不 Fault | 否；安全 Audit | 查询 remap request hash 与 entity identity fixture result |
| 可重试 | BaselineAck 重复或仍在等待 | 返回原 Ack result / `AwaitingBaselineAck`，保持幂等 | 不 Fault | 否 | 查询 BaselineStore idempotency record |
| 可重试 | History 可覆盖的 Gap/重复 Delta | 返回 repair range 或 AlreadyApplied，不重复改 World | 不 Fault | 否 | 查询 DeltaHistory sequence/revision chain |
| 可重试 | Voxel/codec/transport 暂时 unavailable 且未开始权威 Apply | Retryable；保留相同 token/sequence/deadline | Context 保持原状态；超 deadline 转 Resync/Fault policy | 连续失败可含 bundle fragment | 查询 Port completion、queue depth、token idempotency |
| 可致命 | ECS/GAS/Voxel Prepare 成功后首个 Apply 之后出现业务拒绝 | Fatal `PostPrepareBusinessRejection` | Context 与 Session Faulted；禁止继续 Tick/重试业务分支 | 是，含六步阶段、tokens、last revision、snapshot/noSnapshotReason | 查询 participant apply receipts、state hash、durable evidence references |
| 可致命 | Mapping/Identity/Tombstone 内部双向索引或 pin 计数损坏 | Fatal invariant violation | Context Faulted；视范围升级 Session | 是 | 查询 immutable registry dump/hash、first divergent operation |
| 可致命 | Authority Apply 已部分可见且无法证明幂等收敛 | Fatal atomicity violation | Session Faulted；从有效 Snapshot/Release 恢复 | 是 | 查询 ECS/GAS/Voxel receipts、PredictionFrame、replay input、Failure Bundle |

### 8. 测试面

**单元（本模块测试工程）**

- Context exact 状态迁移、Closed/Faulted 后拒绝、BaselineAck 与 Transport ACK 独立。
- Mapping Registry 角色/Owner/Visibility/Delivery/Lifecycle/Prediction 索引与 canonical order。
- 六步 Apply 的步骤 trace、Prepare-before-Apply、confirmed delete 与 unconfirmed original-order replay。
- History budget、Gap repair、Unknown Baseline、Resync reason 与 PresentationDiff 非权威性。

**Golden（本模块测试工程）**

- 运行架构源 FullSnapshot、Delta、Mapping、Entity Identity、Gap-without-resync、empty-field、reused-tombstone 正反 fixtures。
- Generated Voxel Replica contract 的 capture/restore/revision/mutation receipt 正反 fixtures；具体字段只由生成 validator读取。

**Property（本模块测试工程）**

- 任意 bind/destroy/remap 序列保持 Net<->Local 双射、Generation 安全和 NetEntityId 不复用。
- 任意 Context pin 集合下，`CanCollect` 仅在所有五类 horizon 越过 DestroyRevision 后为真。
- Delta 乱序/重复/丢失组合只能收敛到相同状态或显式 Resync，不能产生第三种 World hash。

**故障 / Differential（Reference Host）**

- 丢包、乱序、重复、断线/重连、History Exhaustion、旧 Mapping、Apply 每步崩溃；保存可重放 envelope stream。
- LocalEmbedded 与 LocalSplitProcess 走同 Envelope/Serializer/permissions/size/queue 后比较 Server/Client/Replay hash。
- Friflo/Arch ECS Adapter 与 ReferenceVoxelPort 对同 authority stream 产生相同 apply result/hash。

**Benchmark / Soak（testing 模块驱动）**

- 固定 1/10/25/50/100/150/200 Bot workload，记录 projection/apply p50/p95/p99/max、history bytes、resync rate；无测量数字不写成承诺。
- 长时重连/Prediction/Tombstone GC Soak 验证 RT-D-005，并保留 workload/hardware/config metadata。

### 9. 本模块任务拆解

#### `repl-mapping-registry-and-identity`

- **一句话目标**：实现 generated 非对称 Mapping 校验索引、Net/Local 双向映射和 provisional remap，不泄漏第三方 ECS 类型。
- **涉及文件集**：
  - `modules/replication/src/Lumio.GameRuntime.Replication/Lumio.GameRuntime.Replication.csproj`
  - `modules/replication/tests/Lumio.GameRuntime.Replication.Tests/Lumio.GameRuntime.Replication.Tests.csproj`
  - `modules/replication/src/Lumio.GameRuntime.Replication/ReplicationModule.cs`
  - `modules/replication/src/Lumio.GameRuntime.Replication/ReplicationServices.cs`
  - `modules/replication/src/Lumio.GameRuntime.Replication/Mapping/MappingRegistry.cs`
  - `modules/replication/src/Lumio.GameRuntime.Replication/Mapping/MappingSetView.cs`
  - `modules/replication/src/Lumio.GameRuntime.Replication/Identity/NetEntityMappingTable.cs`
  - `modules/replication/src/Lumio.GameRuntime.Replication/Identity/ProvisionalRemapTable.cs`
  - `modules/replication/tests/Lumio.GameRuntime.Replication.Tests/MappingRegistryGoldenTests.cs`
  - `modules/replication/tests/Lumio.GameRuntime.Replication.Tests/IdentityMappingPropertyTests.cs`
- **验收标准**：
  - [ ] 架构源 Mapping/Identity 正例全部加载，登记反例以稳定 rejection code 拒绝。
  - [ ] Property 证明 Net<->Local 双射、Generation mismatch 不命中、跨 World local ID 不绑定。
  - [ ] MappingSetView 的 hash/iteration 不依赖输入 Dictionary insertion order。
  - [ ] public/stable candidate API 的程序集签名扫描不含第三方 ECS/codec 类型。
- **依赖**：`ecs-world-and-entity-identity`、`cfg-immutable-snapshot-reader`、`obs-event-ports-and-context`
- **Consumes**：generated MappingSet/EntityIdentity、ECS LocalEntityId view、ConfigSnapshot。
- **Produces**：MappingSetView、NetEntityMappingTable、ProvisionalRemapResult。
- **成熟方案**：generated validator + BCL immutable/index collections behind module types。
- **明确不做**：不生成 Mapping，不做 Baseline/Projection/Tombstone。

#### `repl-tombstone-horizon`

- **一句话目标**：实现冻结 max(...) 保留下界、Context pin 与安全 GC。
- **涉及文件集**：
  - `modules/replication/src/Lumio.GameRuntime.Replication/Identity/TombstoneRegistry.cs`
  - `modules/replication/src/Lumio.GameRuntime.Replication/Identity/TombstoneHorizonCalculator.cs`
  - `modules/replication/tests/Lumio.GameRuntime.Replication.Tests/TombstoneHorizonPropertyTests.cs`
- **验收标准**：
  - [ ] 纯函数逐项纳入未确认 Baseline、Delta History、Reconnect、Prediction、Migration/Replay pin。
  - [ ] 任一 horizon unknown 时拒绝 GC，不把 unknown 当零。
  - [ ] Property 在随机 pin/release/GC 序列下不复活已销毁 ID且引用计数不为负。
  - [ ] entity-reused-tombstone 反例被拒绝并产生结构化 evidence。
- **依赖**：`repl-mapping-registry-and-identity`
- **Consumes**：DestroyRevision、各 ReplicationContext pins/horizon inputs。
- **Produces**：TombstoneLease、RevisionHorizon、eligible GC set。
- **成熟方案**：无，纯领域公式与有界 registry。
- **明确不做**：不决定窗口数字，不复用 NetEntityId。

#### `repl-baseline-delta-history`

- **一句话目标**：实现 Context 生命周期、FullSnapshot Baseline、独立 Ack、Delta History/DirtySet 和显式 History Exhaustion。
- **涉及文件集**：
  - `modules/replication/src/Lumio.GameRuntime.Replication/Lifecycle/ReplicationContextState.cs`
  - `modules/replication/src/Lumio.GameRuntime.Replication/Lifecycle/ReplicationContext.cs`
  - `modules/replication/src/Lumio.GameRuntime.Replication/Projection/DirtySet.cs`
  - `modules/replication/src/Lumio.GameRuntime.Replication/History/BaselineStore.cs`
  - `modules/replication/src/Lumio.GameRuntime.Replication/History/DeltaHistory.cs`
  - `modules/replication/src/Lumio.GameRuntime.Replication/History/ReplicationBudget.cs`
  - `modules/replication/tests/Lumio.GameRuntime.Replication.Tests/BaselineDeltaHistoryTests.cs`
- **验收标准**：
  - [ ] 状态机 exact，非法迁移/Closed late input 被拒绝。
  - [ ] Transport ACK 不改变 BaselineStore；BaselineAck 幂等。
  - [ ] Delta key 精确包含 BaseSnapshotId/From/To/Sequence，Gap repair 只返回连续链。
  - [ ] 达到 ReplicationHistoryWindow/Bytes 时只产生 FullResync/断开 action，不静默丢历史。
- **依赖**：`repl-tombstone-horizon`、`coord-revision-vector-view`
- **Consumes**：SnapshotId/Revision、Dirty change keys、Config budget、BaselineAck。
- **Produces**：BaselineLease、DeltaHistory lease、repair/resync decision。
- **成熟方案**：ArrayPool/MemoryPool lease + domain bounded stores。
- **明确不做**：不编码 Envelope、不实现 Socket。

#### `repl-client-six-step-apply`

- **一句话目标**：实现 Client 权威 ECS/GAS/Voxel 六步 Prepare/Apply/Replay 原子流程与 PresentationDiff。
- **涉及文件集**：
  - `modules/replication/src/Lumio.GameRuntime.Replication/Apply/ClientAuthorityApplyCoordinator.cs`
  - `modules/replication/src/Lumio.GameRuntime.Replication/Apply/AuthorityApplyPlan.cs`
  - `modules/replication/src/Lumio.GameRuntime.Replication/Apply/ConfirmedCommandSequence.cs`
  - `modules/replication/src/Lumio.GameRuntime.Replication/Apply/PresentationDiff.cs`
  - `modules/replication/src/Lumio.GameRuntime.Replication/Ports/IEcsReplicationView.cs`
  - `modules/replication/src/Lumio.GameRuntime.Replication/Ports/IGasReplicationView.cs`
  - `modules/replication/tests/Lumio.GameRuntime.Replication.Tests/ClientSixStepApplyTests.cs`
- **验收标准**：
  - [ ] step trace exact 1–6，且所有三域 Prepare 在首个 Apply 前。
  - [ ] 任一 Prepare reject 不改变 ECS/GAS/Voxel/Predict history。
  - [ ] 首个 Apply 后业务 reject 转 Fatal，Context/Session action 可断言。
  - [ ] confirmed commands删除后，unconfirmed严格按 original sequence replay；PresentationDiff 不进入权威 hash。
- **依赖**：`repl-baseline-delta-history`、`gas-prediction-frame`、`cmd-conflict-golden-property`
- **Consumes**：ValidatedEnvelope、PredictionFrame、ECS/GAS authority deltas、中立 ConfirmedCommandSequence。
- **Produces**：ReplicationApplyResult、PresentationDiff、ReplayRequest。
- **成熟方案**：无，纯 V1.3 领域语义。
- **明确不做**：不渲染表现、不创建 Transport ACK。

#### `repl-voxel-replica-adapter`

- **一句话目标**：把 Generated Voxel Replica Contract 映射为可 Prepare/幂等 Apply/Rollback/Capture Revision 的窄 Port。
- **涉及文件集**：
  - `modules/replication/src/Lumio.GameRuntime.Replication.VoxelAdapters/Lumio.GameRuntime.Replication.VoxelAdapters.csproj`
  - `modules/replication/src/Lumio.GameRuntime.Replication.VoxelAdapters/GeneratedVoxelReplicaPortAdapter.cs`
  - `modules/replication/tests/Lumio.GameRuntime.Replication.Tests/VoxelReplicaContractTests.cs`
- **验收标准**：
  - [ ] Adapter 的每个语义操作映射到已生成 capture/restore/revision/mutation token/result，不新增公共字段。
  - [ ] 签名扫描证明不暴露 Chunk Storage、native pointer 或具体 VoxelEngine 类型。
  - [ ] stale/duplicate token 返回 generated stable result，Completion 只经 NativeCompletion barrier。
  - [ ] ReferenceVoxelPort 与 generated adapter 对同 fixture 产生相同 revision/apply result。
- **依赖**：`repl-client-six-step-apply`、`sim-ingress-and-native-completion`
- **Consumes**：Generated Voxel Replica Contract types、NativeCompletion publication。
- **Produces**：IVoxelReplicaPortAdapter results/receipts。
- **成熟方案**：Generated contract Adapter；无自研 Voxel storage。
- **明确不做**：不实现 Voxel mutation/storage、Host FFI lifecycle。

#### `repl-resync-and-fault-matrix`

- **一句话目标**：实现 Projection、Gap/Unknown/Exhaustion Resync 与 LocalEmbedded 全协议故障矩阵。
- **涉及文件集**：
  - `modules/replication/benchmarks/Lumio.GameRuntime.Replication.Benchmarks/Lumio.GameRuntime.Replication.Benchmarks.csproj`
  - `modules/replication/benchmarks/Lumio.GameRuntime.Replication.Benchmarks/ProjectionApplyBenchmarks.cs`
  - `modules/replication/src/Lumio.GameRuntime.Replication/Projection/ReplicationProjection.cs`
  - `modules/replication/src/Lumio.GameRuntime.Replication/Projection/ProjectionBatch.cs`
  - `modules/replication/src/Lumio.GameRuntime.Replication/Resync/ResyncCoordinator.cs`
  - `modules/replication/src/Lumio.GameRuntime.Replication/Ports/IReplicationEnvelopeCodecPort.cs`
  - `modules/replication/src/Lumio.GameRuntime.Replication/Errors/ReplicationFailure.cs`
  - `modules/replication/tests/Lumio.GameRuntime.Replication.Tests/ResyncFaultMatrixTests.cs`
  - `modules/replication/tests/Lumio.GameRuntime.Replication.Tests/LocalEmbeddedPipelineTests.cs`
- **验收标准**：
  - [ ] Full/Delta Projection 仅含 Mapping 可见字段且按 canonical order。
  - [ ] Gap、Unknown Baseline、History Exhaustion、Schema/Mapping mismatch、Tombstone conflict 只进入规定 Resync/Reject path。
  - [ ] LocalEmbedded 测试证明 Envelope/Serializer/permission/size/bounded queue/Tick delivery 均执行。
  - [ ] malformed/oversized 输入在 materialize 前拒绝；fuzz corpus 保存最小 replay sample。
- **依赖**：`repl-voxel-replica-adapter`、`repl-baseline-delta-history`、`obs-failure-bundle-assembly`
- **Consumes**：immutable ECS/GAS/Voxel projection views、MappingSet、Revision、validated ingress envelope。
- **Produces**：FullSnapshotProjection、DeltaProjection、ResyncRequest、Failure evidence。
- **成熟方案**：统一 MessagePack primitive adapter + xUnit/CsCheck/SharpFuzz。
- **明确不做**：不实现具体 Host codec/socket/fragmentation policy。


## 3.8. `gas` 模块设架

### 0. 模块身份证

- 目录：`modules/gas/`
- 建议程序集：`Lumio.GameRuntime.Gas`；Game 公式/内容 Port 接口放调用方程序集，Game 实现由发行组合注入
- 建议命名空间：`Lumio.GameRuntime.Gas`
- 优先级与阶段：P1 / Vertical Slice；Type/Handle、状态机、ECS 投影先行，复杂求解器留在后续产品能力
- 唯一职责：唯一拥有宿主无关 Ability/Effect/Attribute/Tag Framework 生命周期、Type/Instance/Handle、Modifier 确定序、PredictionFrame 与 ECS 单一真相投影语义。
- RT-D-001 纪律：以上是物理映射建议；即使首期合并程序集，也必须保持命名空间、internal 可见性和依赖测试，不得形成反向引用。

### 1. 它做什么

- `RuntimeCompositionRoot` 使用 generated GAS type registry、只读 `ConfigSnapshot`、`IEcsGasProjectionPort`、`ICommandBufferFactory`、Game 提供的 `IGameplayFormulaPort` 构造 `GasModule`；每个 World 创建独立 `GasWorldContext`，不跨 Server/Client World 共享。
- Framework 生命周期严格为 `Unloaded -> Registered -> Ready -> Running -> Draining -> Unloaded`，任一状态可转 `Faulted`；Running 期间每 Tick 在 Processor Plan 声明的阶段读取 ECS/config/prediction context并把变化写入该 Processor 的 CommandBuffer。
- Ability 状态严格为 `Requested -> Activated -> Executing -> Completed`、`Requested/Activated -> Rejected`、任一非终态到 `Cancelled`、`Executing -> Expired`、predicted authority reject 到 `RolledBack`；Effect 状态严格为 `Pending -> Active -> Expired|Removed`、`Pending -> Rejected`、predicted rollback 到 `RolledBack`。
- Stack、Duration、Refresh 是 Active 状态上的事件/更新，不另造状态；状态迁移与 Modifier 求值在 `DeterminismContext` 下使用 generated type order 与明确 tie-breaker。
- ECS 是 Attribute、Tag、Active Effect 投影和可复制字段的唯一权威存储；GAS Context 只保存 Handle registry、执行/预测/临时求值上下文与可重建索引，禁止第二份权威属性表。
- 成功包括稳定 Handle/transition result、prepared ECS commands、PredictionFrame、GAS snapshot/hash/migration view；失败必须区分业务拒绝、暂时资源/依赖不可用与框架不变量破坏。

### 2. 它明确不做什么

- 不定义具体 Ability、Formula、Cost、Cooldown、Targeting、Permission、经济或表现内容；这些归 LumioGame。
- 不创建独立于 ECS 的权威 Attribute/Effect/Tag Storage；任何缓存都必须可从 ECS+Config+Prediction 重建。
- 不直接改 VoxelWorld、Socket、Connection、Renderer、Host Wall Clock 或 Release Pool。
- 不实现通用脚本 VM、任意反射、动态代码执行或跨 Ability 的全局复杂求解器。
- 不决定 Replication Envelope/Baseline/History；只提供投影与 PredictionFrame Port。
- 不拥有 Tick 调度与 Processor Plan；这些归 `simulation`，结构变化通过 `command`。
- 不加载/卸载 Gameplay Assembly；Scope/迁移治理归 `hot-reload`。
- 不依赖具体 Game 实现、第三方 DI 容器或 `testing` 程序集。

### 3. 代码骨架

#### 3.1 建议目录树

```text
modules/gas/
├─ src/Lumio.GameRuntime.Gas/
│  ├─ GasModule.cs                         # Framework 门面与 World Context 工厂
│  ├─ GasServices.cs                       # 显式 Port/registry/config 依赖
│  ├─ Lifecycle/GasFrameworkState.cs       # exact Framework 状态
│  ├─ Lifecycle/GasWorldContext.cs         # World-local handle/prediction/evaluation owner
│  ├─ Identity/AbilityTypeId.cs            # generated ID wrapper/validation
│  ├─ Identity/AbilityInstanceId.cs        # 单次实例 identity
│  ├─ Identity/AbilityHandle.cs            # index+generation opaque handle
│  ├─ Identity/EffectTypeId.cs
│  ├─ Identity/EffectInstanceId.cs
│  ├─ Identity/EffectHandle.cs
│  ├─ Identity/GasTypeRegistry.cs          # generated type metadata 与 deterministic order
│  ├─ Ability/AbilityState.cs              # exact Ability states
│  ├─ Ability/AbilityStateMachine.cs       # transition guard/result
│  ├─ Effect/EffectState.cs                # exact Effect states
│  ├─ Effect/EffectStateMachine.cs         # Stack/Duration/Refresh as events
│  ├─ Evaluation/ModifierEvaluator.cs      # 明确 order/tie-breaker/overflow policy
│  ├─ Evaluation/ModifierEvaluationPlan.cs # immutable compiled evaluation plan
│  ├─ Evaluation/IGameplayFormulaPort.cs   # Game-owned pure formula Port
│  ├─ Execution/GasExecutionContext.cs     # Tick/World/Actor/Prediction/config immutable context
│  ├─ Execution/GasCommandEmitter.cs       # 只写 caller processor CommandBuffer
│  ├─ Projection/IEcsGasProjectionPort.cs  # ECS authoritative read/write projection
│  ├─ Projection/GasEcsProjection.cs       # typed attribute/tag/effect projection mapping
│  ├─ Prediction/PredictionFrame.cs        # frame id/input/ECS+GAS+Voxel references
│  ├─ Prediction/PredictionHistory.cs      # bounded confirmed/unconfirmed frames
│  ├─ Prediction/AuthorityConfirmation.cs  # generated confirmation/reject view
│  ├─ Snapshot/IGasSnapshotProvider.cs     # immutable snapshot/hash provider
│  ├─ Snapshot/GasSnapshotLease.cs         # handle/prediction/index snapshot，不复制 ECS state
│  ├─ Migration/GasMigrationView.cs        # versioned read-only migration input/output descriptor
│  ├─ Budget/GasBudget.cs                  # instances/effects/commands/prediction limits
│  └─ Errors/GasFailure.cs                 # Rejected/Retryable/Fatal
└─ tests/
   ├─ Lumio.GameRuntime.Gas.Tests/
   │  ├─ TypeHandlePropertyTests.cs
   │  ├─ AbilityStateMachineGoldenTests.cs
   │  ├─ EffectStateMachineGoldenTests.cs
   │  ├─ ModifierDeterminismPropertyTests.cs
   │  ├─ EcsSingleTruthTests.cs
   │  ├─ PredictionFrameTests.cs
   │  └─ GasSnapshotHashTests.cs
   └─ Lumio.GameRuntime.Gas.Benchmarks/
      └─ ModifierProjectionBenchmarks.cs
```

#### 3.2 核心类型

| 类型/可见性 | 职责与关键字段/不变式 | 关键方法草图与失败表达 | 线程与生命周期 |
|---|---|---|---|
| `GasModule` / `sealed class` / stable candidate, 未冻结 | World-local GAS Framework 工厂。字段仅 `GasServices` 与 registry version；不保存 World mutable state。 | `CreateWorldContext(in GasWorldDescriptor) -> GasResult<GasWorldHandle>`；`Drain/Unload`；非法 release/schema 返回分类失败。 | Composition Root 创建；每 World Context 由 SimulationSession 持有并 dispose。Module Closed 后拒绝。 |
| `GasWorldContext` / `sealed class` / internal | 唯一拥有 World-local handle tables、PredictionHistory、evaluation caches 与 `GasFrameworkState`。字段绑定 `WorldId/Role/SchemaEpoch`。 | `Register`、`Ready`、`BeginRunning`、`EvaluateProcessor`、`BeginDrain`、`Unload`、`Fault`；状态迁移 exact。 | 仅 Simulation Owner Thread 写；Formula worker 只能纯函数读取 immutable input。Dispose 后 handle stale。 |
| `GasFrameworkState` / `enum` / stable candidate, 未冻结 | 只含 `Unloaded, Registered, Ready, Running, Draining, Faulted`。 | 无 public setter；Context 内部迁移表。 | 值对象。 |
| `AbilityHandle` / `readonly record struct` / stable candidate, 未冻结 | 包含 index+generation 的 World-local opaque handle；与 `AbilityTypeId`、`AbilityInstanceId` 分离。默认/zero 不代表有效。 | `TryResolve` 由 Context 完成；序列化只存 stable InstanceId/TypeId，不持久化 process-local index。 | 不可变；World dispose/generation advance 后 stale。 |
| `EffectHandle` / `readonly record struct` / stable candidate, 未冻结 | 同 AbilityHandle；只定位 Context 内 effect instance。 | `TryResolve`、`IsStale`；跨 World/Role 使用返回 Rejected。 | 不可变。 |
| `GasTypeRegistry` / `sealed class` / internal | 消费 generated Ability/Effect/Attribute/Tag metadata，提供确定性 TypeId 顺序与 schema/hash validation。 | `Load`、`ResolveAbility`、`ResolveEffect`、`EnumerateCanonical`；unknown TypeId 拒绝。 | 加载可 Worker；激活在 Barrier；View 并发只读。 |
| `AbilityStateMachine` / `sealed class` / internal | 实现 exact Ability 状态与事件 guard；业务 predicate 在 `Requested/Activated` 前拒绝，未获许可不得进入 Executing。 | `Transition(in AbilityEvent, in GasExecutionContext) -> AbilityTransitionResult`；非法 transition Rejected，内部 impossible state Fatal。 | Owner Thread；实例终态后只能查询/回收，不能复活。 |
| `EffectStateMachine` / `sealed class` / internal | 实现 exact Effect 状态；Stack/Duration/Refresh 为 Active events，必须保留 deterministic revision。 | `Transition(in EffectEvent, in GasExecutionContext)`；Pending reject 无 ECS side effect；rollback 只用于 predicted frame。 | Owner Thread。终态按 retention/prediction pin 回收。 |
| `ModifierEvaluator` / `sealed class` / internal | 按 generated group/priority/source/tie-breaker 与数值策略执行纯 deterministic modifier plan。字段不持有 mutable ECS。 | `Evaluate(in ModifierEvaluationPlan, in AttributeInput, IGameplayFormulaPort) -> GasResult<EvaluatedAttributeSet>`；overflow/NaN/unknown formula 分类。 | 可在 Owner Thread或受控纯 worker；结果仅 Barrier 应用。无 dispose。 |
| `IGameplayFormulaPort` / `interface` / stable Port, 未冻结 | 由 Game 实现的纯 Formula Hook；只接受值对象与 Config reader，不得访问 World/Socket/Wall Clock。 | `Evaluate(FormulaId, in FormulaInput, in DeterminismContext) -> FormulaResult`；必须显式失败/预算，不抛业务异常。 | 调用可并发但实现必须纯；Scope 由 Hot Reload 管理，generation mismatch 拒绝。 |
| `GasEcsProjection` / `sealed class` / internal | 把 Ability/Effect transition 转为 typed ECS reads/commands；ECS 是唯一权威 Attribute/Tag/ActiveEffect store。 | `ReadActorState`、`PrepareProjection`、`EmitCommands(ProcessorCommandBuffer)`；禁止直接 mutation storage。 | Owner Thread在声明 Processor Phase；CommandBuffer Apply 决定可见性。 |
| `PredictionFrame` / `readonly record struct` / stable candidate, 未冻结 | 包含 generated `PredictionFrameId/PredictionKey/TickId`、confirmed command sequence、ECS/GAS/Voxel snapshot references/hash；不复制任意对象图。 | `Validate`、`WithAuthorityConfirmation`；hash 不含对象地址/时钟。 | 不可变，可跨线程读取；lease/pin 由 PredictionHistory 管理。 |
| `PredictionHistory` / `sealed class` / internal | 有界保存 confirmed/unconfirmed frames、pins 与 original command order；窗口由 Config/Replication budget。 | `Append`、`Confirm`、`RejectAndSelectRollback`、`Trim`、`AcquireLease`；无法 rollback 返回 Resync required。 | Owner Thread写；immutable lease可读。Context dispose 释放。 |
| `IGasSnapshotProvider` / `interface` / caller-facing Port | 给 Coordination/Persistence 提供带 Revision/SchemaEpoch/Hash 的 immutable GAS index/prediction snapshot；ECS field state仍由 ECS provider负责。 | `Capture(in SnapshotCutView) -> GasSnapshotResult<GasSnapshotLease>`；`Release` idempotent。 | Barrier capture，Worker encode；dispose/release后访问拒绝。 |
| `GasBudget` / `readonly record struct` / internal | 实例、effect、modifier ops、commands、prediction frames/bytes 等来自 Config/Capability；单位明确。 | `Validate`、`CanAllocate`；无隐式无限值。 | 不可变。 |
| `GasFailure` / `readonly record struct` / stable candidate, 未冻结 | generated error identity + failure class + actor/type/instance/prediction correlation。 | `Rejected`、`Retryable`、`Fatal` factories；Adapter exceptions 分类并保留 evidence hash。 | 不可变。Fatal 由 caller Fault World/Session并组 bundle。 |

#### 3.3 稳定候选 API 与内部边界

- 稳定候选 API 使用 generated TypeId/InstanceId/Prediction 类型与本仓 opaque Handle；不暴露 ECS concrete storage、third-party collections 或 DI container。
- `IGameplayFormulaPort` 契约归调用方 GAS 模块，Game 只实现纯函数；Hot Reload Scope generation 作为调用验证输入，避免 `gas -> hot-reload` 反向依赖。
- GAS Snapshot 只保存 GAS-owned handle/prediction/index state；Attribute/Tag/Effect authoritative projection由 ECS Snapshot 提供，防止双真相。
- 状态名与迁移属于 V1.3 Framework 语义；不得被 Game content 新增同义状态。

```csharp
public interface IGasRuntime
{
    GasResult<GasWorldHandle> CreateWorld(in GasWorldDescriptor descriptor);
    GasResult Drain(GasWorldHandle world);
    GasResult Unload(GasWorldHandle world);
}

public interface IGameplayFormulaPort
{
    FormulaResult Evaluate(
        FormulaId formulaId,
        in FormulaInput input,
        in DeterminismContext determinism);
}

internal interface IEcsGasProjectionPort
{
    GasActorStateView ReadActor(LocalEntityId actor, in EcsReadView view);
    PreparedGasProjection Prepare(in GasProjectionDelta delta, in EcsReadView view);
    void Emit(in PreparedGasProjection projection, ProcessorCommandBuffer commands);
}

public interface IGasSnapshotProvider
{
    GasSnapshotResult<GasSnapshotLease> Capture(in SnapshotCutView cut);
}
```

### 4. 与成熟方案怎么接

| 能力 | 采用 | 不采用 | Adapter | 稳定 API 可见第三方类型 | 许可证 | 风险/控制 |
|---|---|---|---|---|---|---|
| 状态/Handle storage | BCL arrays/dictionaries + generation table；ECS authoritative fields 经 ECS Adapter | 引入第二个完整 GAS/ECS 引擎；反射对象图 | `GasWorldContext`/`GasEcsProjection` | 否 | BCL MIT | 内部 layout由 RT-D-006/benchmark 证伪，stable handle不含引用。 |
| 数值/公式 | Game pure Port + generated FormulaId；必要时 BCL numeric primitives | 动态脚本 VM、Roslyn runtime compile、任意 reflection | `IGameplayFormulaPort` | 否 | 项目契约 | 浮点/定点策略必须来自架构/Config，不由 Adapter私定。 |
| Command emission | 复用 `command` 每 Processor Buffer/PreparedDelta | 直接结构写 ECS；GAS 自建 command queue | `GasCommandEmitter` | 否 | 项目模块 | Prepared 后不可业务拒绝。 |
| Prediction storage | `ArrayPool`/immutable value records + bounded history | 序列化完整对象图；无限 frame list | `PredictionHistory` | 否 | BCL MIT | lease/pin/byte budget必须测试；地址不进Hash。 |
| 测试 | xUnit v3、CsCheck、BenchmarkDotNet | 自研 runner/property/benchmark | 模块测试工程 | 否 | Apache-2.0 / MIT | 生成随机 transition/event序列并保存 seed/replay。 |

**自研最小范围。** 只自研 Lumio GAS 的 exact Framework/Ability/Effect 状态机、Type/Instance/Handle 不变量、Modifier 确定序、PredictionFrame 与 ECS 投影。现成 ECS、队列、池、测试/Benchmark、序列化均经既有 Adapter/模块复用。未来更换 storage/evaluator implementation 时，stable Port 和 ECS 单一真相不变。

### 5. 输入 / 输出 / 依赖

**Consumes**

- 来自 `ecs` 的 `EcsReadView`、`EcsWriteView`/projection Port、LocalEntityId/Generation 与 snapshot view。
- 来自 `command` 的 `ProcessorCommandBuffer`、Deferred token 与 prepared/apply result。
- 来自 `config` 的 immutable `ConfigSnapshot`/typed table readers。
- 来自 `simulation`（运行时调用，不形成反向编译依赖）的 `DeterminismContext`、Tick/Phase/Processor execution context；中立 context 类型应位于低层 generated/shared contract。
- 来自 Game 的 generated GAS type metadata 与纯 `IGameplayFormulaPort` 实现。
- 来自 `observability` 的 event/metric/trace Ports。

**Produces**

- 给 `simulation`/`command`：prepared GAS/ECS commands、transition results、budget/failure。
- 给 `replication`：`GasProjectionLease`、`PredictionFrame`、authority apply Port。
- 给 `persistence`：`IGasSnapshotProvider`、canonical hash/migration view。
- 给 `hot-reload`：scope-neutral migration input/output descriptors and drain result。
- 给 `observability`：Ability/Effect/Prediction/Modifier structured evidence。

**编译依赖**

- 允许：`ecs`、`command`、`config`、`observability`、generated Game/GAS contracts。
- 不以程序集依赖 `simulation`；execution context 放中立 contract或由调用参数传入，避免 `simulation <-> gas` 环。
- Game implementation只由 Composition Root 注入 `IGameplayFormulaPort`。

**禁止依赖**

- 具体 LumioGame gameplay implementation/assembly。
- `replication`、`persistence`、`hot-reload` 实现（这些消费 GAS Ports）。
- Host Wall Clock、Socket/Connection、Renderer、Voxel storage。
- 第三方 ECS concrete API、dynamic script compiler/VM。
- `testing` 程序集。

### 6. 队列、预算、线程

| 实现对象 | 元素/资源 | 容量/预算来源 | 满载/超限动作 | 顺序/幂等键 | Metric |
|---|---|---|---|---|---|
| 每 Processor `ProcessorCommandBuffer`（command-owned） | GAS emitted ECS field/structural commands | `ProcessorDescriptor.Budget`、`CommandBufferMaxCommands`、`CommandBufferMaxBytes` | 在 Prepare 前按优先级/预算拒绝；Prepared 后不可业务拒绝 | `Phase + ProcessorId + LocalSequence` | `runtime.gas.commands.emitted`、`runtime.command.buffer.full_total` |
| `PredictionHistory` | immutable `PredictionFrameLease` | Replication/Config 的 prediction rollback window/bytes | 无法保留 required confirmed frame 时请求 Full Resync；不静默 trim active pin | `PredictionFrameId + PredictionKey + ClientCommandSeq` | `runtime.gas.prediction.frames`、`.bytes`、`.resync_total` |
| Simulation-owned Native Completion（可选纯计算） | immutable formula/native result | `NativeCompletionQueueCapacity` 与 Processor budget | 可靠 completion 不丢；full/late result按 Simulation policy拒绝或 Fault | `JobId/Token` | `runtime.gas.evaluation.completion_latency`、`runtime.native_completion.full_total` |

- GAS World authoritative transition、handle registry、PredictionHistory 与 ECS command emission只由 Simulation Owner Thread写。
- Formula/Modifier worker必须纯：输入不可变、输出有界，不访问 World/clock/IO；结果只在声明 Barrier消费。
- 所有结构/字段权威变化通过 CommandBuffer，V1字段写入一旦开始失败即 Fail-stop，不提供 GAS 自建 undo。
- Server 与 Client Replica 各有独立 GAS Context；PredictionFrame通过值/lease关联，不共享可变对象。

### 7. 失败与恢复

| 分类 | 触发例 | 返回/可观察结果 | World/Session | Failure Bundle | Journal/证据查询 |
|---|---|---|---|---|---|
| 可拒绝 | unknown TypeId、stale Handle、跨 World Handle | `GasFailure.Rejected(UnknownType\|StaleHandle\|WrongWorld)`；无 ECS command | World/Session不 Fault | 通常否；可 Diagnostic/Audit | 查询 registry version、handle generation、request hash |
| 可拒绝 | Ability cost/permission/target predicate 不通过 | Transition到 exact `Rejected`，返回业务 rejection code | 不 Fault | 否 | 查询 Game formula result/ConfigRevision/input correlation |
| 可拒绝 | 非法状态事件，如 Completed 后 Activate | Rejected `InvalidTransition`；终态不变 | 不 Fault；重复恶意可诊断 | 否 | 查询 state/event sequence |
| 可重试 | Processor/CommandBuffer预算暂满且仍在 Prepare 前 | Retryable或明确 budget reject；不产生 partial projection | 不 Fault；由 Simulation决定延后/拒绝 | 否 | 查询 Processor budget、buffer count/bytes |
| 可重试 | 纯 Formula/Native completion 未到且 deadline未过 | Retryable Pending；保持同 token/seed/input hash | 不 Fault；deadline后按 descriptor policy | 连续超时可 bundle fragment | 查询 JobId/Token/completion queue |
| 可重试 | Prediction confirmation重复或 AlreadyApplied | 返回原 confirmation/result，不重复 transition/command | 不 Fault | 否 | 查询 PredictionHistory idempotency index |
| 可致命 | ECS authoritative projection与 GAS handle/index不可重建一致 | Fatal `SingleTruthInvariantViolation` | World/Session Faulted | 是 | 查询 ECS snapshot/hash、GAS index snapshot、first divergent transition |
| 可致命 | Prepared 后 Formula/业务拒绝或 command apply partial failure | Fatal post-prepare/fail-stop | Session Faulted；禁止继续 Tick | 是 | 查询 PreparedDelta、command receipts、Tick/Phase/Processor |
| 可致命 | Modifier order/non-finite/overflow策略在相同输入下不确定 | Fatal determinism violation | World Faulted，Reference Host报告首差异 | 是 | 查询 formula/type registry/config/input/seed/hash |

### 8. 测试面

**单元（本模块测试工程）**

- Framework、Ability、Effect exact状态迁移；Stack/Duration/Refresh断言为Active events而非状态。
- TypeId/InstanceId/Handle分离、generation reuse/stale、World/Role隔离。
- Formula Port purity guard、Modifier order/tie-breaker/overflow/nonfinite处理。
- GasEcsProjection只产生CommandBuffer，不直接改storage。

**Golden（本模块测试工程）**

- 消费架构源 GAS/Prediction/Config/Command fixtures；每条 transition生成固定result/hash。
- Authority Confirm/Reject/RolledBack序列与 Snapshot restore golden。

**Property（本模块测试工程）**

- 任意合法/非法事件序列不会从终态复活；handle generation永不解析到错误实例。
- 随机 modifier输入排列在canonical排序后结果相同。
- ECS snapshot + GAS-owned index/prediction snapshot可重建同状态/hash，证明无第二权威属性表。

**故障 / Reference Host**

- CommandBuffer full、formula timeout/throw、stale scope generation、prediction window exhaustion、authority reject at each step。
- Server/Client/Replay对同input、ConfigRevision、seed产生相同 transition/hash；差异定位到Tick/Phase/Processor。

**Benchmark / Soak**

- 固定 actor/effect/modifier distributions量测evaluate/projection p50/p95/p99、allocation、command count；不预设承诺。
- Prediction confirm/reject/rollback长时Soak，验证frame pin和handle recycle无泄漏。

### 9. 本模块任务拆解

#### `gas-type-handle-registry`

- **一句话目标**：实现 generated GAS Type registry、Instance identity 与 generation-safe Ability/Effect Handles。
- **涉及文件集**：
  - `modules/gas/src/Lumio.GameRuntime.Gas/Lumio.GameRuntime.Gas.csproj`
  - `modules/gas/tests/Lumio.GameRuntime.Gas.Tests/Lumio.GameRuntime.Gas.Tests.csproj`
  - `modules/gas/src/Lumio.GameRuntime.Gas/GasModule.cs`
  - `modules/gas/src/Lumio.GameRuntime.Gas/GasServices.cs`
  - `modules/gas/src/Lumio.GameRuntime.Gas/Lifecycle/GasFrameworkState.cs`
  - `modules/gas/src/Lumio.GameRuntime.Gas/Lifecycle/GasWorldContext.cs`
  - `modules/gas/src/Lumio.GameRuntime.Gas/Identity/AbilityTypeId.cs`
  - `modules/gas/src/Lumio.GameRuntime.Gas/Identity/AbilityInstanceId.cs`
  - `modules/gas/src/Lumio.GameRuntime.Gas/Identity/AbilityHandle.cs`
  - `modules/gas/src/Lumio.GameRuntime.Gas/Identity/EffectTypeId.cs`
  - `modules/gas/src/Lumio.GameRuntime.Gas/Identity/EffectInstanceId.cs`
  - `modules/gas/src/Lumio.GameRuntime.Gas/Identity/EffectHandle.cs`
  - `modules/gas/src/Lumio.GameRuntime.Gas/Identity/GasTypeRegistry.cs`
  - `modules/gas/tests/Lumio.GameRuntime.Gas.Tests/TypeHandlePropertyTests.cs`
- **验收标准**：
  - [ ] Framework state exact且非法迁移拒绝。
  - [ ] TypeId/InstanceId/Handle三类不可隐式互换。
  - [ ] Property覆盖allocate/release/reuse/stale/cross-world，generation不解析到新实例。
  - [ ] registry canonical enumeration/hash不依赖输入顺序。
- **依赖**：`ecs-world-and-entity-identity`、`cfg-immutable-snapshot-reader`、`obs-event-ports-and-context`
- **Consumes**：generated GAS type metadata、WorldId/Role/SchemaEpoch、ConfigSnapshot。
- **Produces**：GasWorldContext、registry view、generation-safe handles。
- **成熟方案**：BCL arrays/dictionaries behind opaque handle types。
- **明确不做**：不实现状态机/evaluation/ECS projection。

#### `gas-ability-effect-state-machines`

- **一句话目标**：实现 V1.3 Ability/Effect exact状态机以及Stack/Duration/Refresh事件语义。
- **涉及文件集**：
  - `modules/gas/src/Lumio.GameRuntime.Gas/Ability/AbilityState.cs`
  - `modules/gas/src/Lumio.GameRuntime.Gas/Ability/AbilityStateMachine.cs`
  - `modules/gas/src/Lumio.GameRuntime.Gas/Effect/EffectState.cs`
  - `modules/gas/src/Lumio.GameRuntime.Gas/Effect/EffectStateMachine.cs`
  - `modules/gas/tests/Lumio.GameRuntime.Gas.Tests/AbilityStateMachineGoldenTests.cs`
  - `modules/gas/tests/Lumio.GameRuntime.Gas.Tests/EffectStateMachineGoldenTests.cs`
- **验收标准**：
  - [ ] 枚举只含冻结状态；Stack/Duration/Refresh不出现在state enum。
  - [ ] 每条合法边有Golden，每条非法边返回稳定Rejected且state不变。
  - [ ] predicted authority reject只转RolledBack，server authoritative实例不误用。
  - [ ] 终态在任意事件序列下不复活。
- **依赖**：`gas-type-handle-registry`
- **Consumes**：Ability/Effect events、GasExecutionContext。
- **Produces**：transition result/events，不直接改ECS。
- **成熟方案**：无，纯领域状态机。
- **明确不做**：不实现业务Formula/Cost/Targeting。

#### `gas-modifier-evaluation`

- **一句话目标**：实现generated order驱动的纯Modifier plan与Game Formula Port。
- **涉及文件集**：
  - `modules/gas/src/Lumio.GameRuntime.Gas/Evaluation/ModifierEvaluator.cs`
  - `modules/gas/src/Lumio.GameRuntime.Gas/Evaluation/ModifierEvaluationPlan.cs`
  - `modules/gas/src/Lumio.GameRuntime.Gas/Evaluation/IGameplayFormulaPort.cs`
  - `modules/gas/src/Lumio.GameRuntime.Gas/Execution/GasExecutionContext.cs`
  - `modules/gas/tests/Lumio.GameRuntime.Gas.Tests/ModifierDeterminismPropertyTests.cs`
- **验收标准**：
  - [ ] 输入无World/clock/IO引用，Formula API只接受immutable values/DeterminismContext。
  - [ ] 随机modifier insertion order经canonical plan后结果/hash一致。
  - [ ] unknown formula、budget、overflow/nonfinite按固定failure class返回。
  - [ ] 同seed/config/input重复1000次字节级结果相同。
- **依赖**：`gas-ability-effect-state-machines`、`sim-determinism-context-and-state-hash`
- **Consumes**：generated modifier metadata、Config reader、IGameplayFormulaPort、DeterminismContext。
- **Produces**：EvaluatedAttributeSet、evaluation evidence。
- **成熟方案**：BCL numeric primitives + Game pure Port；无动态脚本。
- **明确不做**：不冻结具体游戏Formula或数值模型。

#### `gas-ecs-authoritative-projection`

- **一句话目标**：把GAS transition/evaluation变为每Processor CommandBuffer，证明ECS是唯一权威属性/效果投影。
- **涉及文件集**：
  - `modules/gas/src/Lumio.GameRuntime.Gas/Execution/GasCommandEmitter.cs`
  - `modules/gas/src/Lumio.GameRuntime.Gas/Projection/IEcsGasProjectionPort.cs`
  - `modules/gas/src/Lumio.GameRuntime.Gas/Projection/GasEcsProjection.cs`
  - `modules/gas/src/Lumio.GameRuntime.Gas/Budget/GasBudget.cs`
  - `modules/gas/src/Lumio.GameRuntime.Gas/Errors/GasFailure.cs`
  - `modules/gas/tests/Lumio.GameRuntime.Gas.Tests/EcsSingleTruthTests.cs`
- **验收标准**：
  - [ ] GAS source扫描无直接storage mutation API；所有写经ProcessorCommandBuffer。
  - [ ] snapshot/restore后删除GAS cache仍可从ECS+registry重建同authoritative view/hash。
  - [ ] Prepare前预算/permission/target失败无ECS变化；Prepared后拒绝测试转Fatal。
  - [ ] Server/Client各自World Context不共享mutable projection。
- **依赖**：`gas-modifier-evaluation`、`cmd-preflight-and-prepared-delta`、`ecs-change-set-and-snapshot-view`
- **Consumes**：EcsReadView、transition/evaluation result、ProcessorCommandBuffer。
- **Produces**：PreparedGasProjection、ECS commands、GasFailure。
- **成熟方案**：复用command/ecs Ports。
- **明确不做**：不实现Command apply或复制协议。

#### `gas-prediction-frame`

- **一句话目标**：实现bounded PredictionFrame/History、Authority confirmation/reject与rollback选择。
- **涉及文件集**：
  - `modules/gas/src/Lumio.GameRuntime.Gas/Prediction/PredictionFrame.cs`
  - `modules/gas/src/Lumio.GameRuntime.Gas/Prediction/PredictionHistory.cs`
  - `modules/gas/src/Lumio.GameRuntime.Gas/Prediction/AuthorityConfirmation.cs`
  - `modules/gas/tests/Lumio.GameRuntime.Gas.Tests/PredictionFrameTests.cs`
- **验收标准**：
  - [ ] Frame只含generated IDs、hash和ECS/GAS/Voxel references，不含对象图/地址。
  - [ ] Confirm/Reject重复幂等；unconfirmed command sequence保持original order。
  - [ ] required frame被pin时trim拒绝；窗口耗尽返回FullResync required。
  - [ ] rollback选择与authority revision/confirmation fixture一致。
- **依赖**：`gas-ecs-authoritative-projection`
- **Consumes**：PredictionKey/FrameId、command sequence、ECS/GAS/Voxel snapshot refs、Config budget。
- **Produces**：PredictionFrame/lease、rollback selection、confirmation result。
- **成熟方案**：ArrayPool/immutable records + bounded history。
- **明确不做**：不执行Replication六步Apply或Voxel rollback。

#### `gas-snapshot-hash-and-migration`

- **一句话目标**：提供不复制ECS权威字段的GAS Snapshot/Hash/Migration View，并覆盖恢复不变量。
- **涉及文件集**：
  - `modules/gas/benchmarks/Lumio.GameRuntime.Gas.Benchmarks/Lumio.GameRuntime.Gas.Benchmarks.csproj`
  - `modules/gas/src/Lumio.GameRuntime.Gas/Snapshot/IGasSnapshotProvider.cs`
  - `modules/gas/src/Lumio.GameRuntime.Gas/Snapshot/GasSnapshotLease.cs`
  - `modules/gas/src/Lumio.GameRuntime.Gas/Migration/GasMigrationView.cs`
  - `modules/gas/tests/Lumio.GameRuntime.Gas.Tests/GasSnapshotHashTests.cs`
  - `modules/gas/benchmarks/Lumio.GameRuntime.Gas.Benchmarks/ModifierProjectionBenchmarks.cs`
- **验收标准**：
  - [ ] Snapshot只含GAS-owned registry/handles/prediction/index，与ECS provider manifest可明确组合。
  - [ ] 相同logical state在不同allocation/insertion order下canonical hash一致。
  - [ ] Migration view只读旧snapshot，在staging生成versioned result，不改active context。
  - [ ] Benchmark记录workload/hardware/config/SchemaEpoch，不写未经测量阈值。
- **依赖**：`gas-prediction-frame`、`coord-snapshot-cut`
- **Consumes**：SnapshotCutView、GasWorldContext immutable capture、ECS snapshot reference。
- **Produces**：GasSnapshotLease/hash/provider result、GasMigrationView。
- **成熟方案**：统一 Canonical codec Port由persistence消费；本卡只定义typed immutable view。
- **明确不做**：不编码/写盘/激活Snapshot，不执行Hot Reload切换。


## 3.9. `persistence` 模块设架

### 0. 模块身份证

- 目录：`modules/persistence/`
- 建议程序集：`Lumio.GameRuntime.Persistence`；具体存储/压缩/加密适配可放 `Lumio.GameRuntime.Persistence.Adapters`，Port 仍由调用方/中立契约拥有
- 建议命名空间：`Lumio.GameRuntime.Persistence`
- 优先级与阶段：P1 / Vertical Slice；Canonical、Staging/Activate 与 Durable Record Port 是进入恢复切片的前置
- 唯一职责：唯一拥有 Snapshot 编码/校验/Staging/Activate、Checkpoint/Recovery 编排以及 WAL/TxnJournal/CommandLog Adapter 状态；不拥有 Revision/Txn/领域状态。
- RT-D-001 纪律：以上是物理映射建议；即使首期合并程序集，也必须保持命名空间、internal 可见性和依赖测试，不得形成反向引用。

### 1. 它做什么

- `RuntimeCompositionRoot` 注入 `ISnapshotStoragePort`、`IDurableRecordStoragePort`、各领域 snapshot providers、Generated Voxel Snapshot Contract Adapter、Canonical codec/Compression adapters 与 `IObservabilityPort` 构造 `PersistenceModule`。
- `coordination` 在 Tick Barrier 固定 `SnapshotCut + SessionRevisionVector`；本模块只消费不可变 provider leases。Capture/manifest 固定由 Owner Thread 发起，编码、校验和 IO 可在有界 Worker 完成，Completion 在声明 Barrier/Host控制面发布。
- Snapshot 状态严格为 `Idle -> Capturing -> Encoding -> Staged -> Verified -> Active`，Staged 验证失败到 `Invalid`；恢复严格为 `Opening -> CheckpointVerified -> LogScanning -> Replaying -> Recovered`，失败到 `RecoveryFailed`。
- Canonical writer按架构源字段顺序、显式数值/字节序、长度与 Hash/Checksum规则生成 bytes；MessagePack/Brotli 仅是 primitive/压缩实现，不拥有字段顺序或 Snapshot 语义。
- Staging 写入、校验、durability ack、原子 Active pointer 切换分离；任何失败保留上一 Active Snapshot。WAL/TxnJournal/CommandLog 使用各自 generated Record Envelope，Diagnostic `LoggingEvent` 只引用 record id/hash。
- 恢复从最后有效 Checkpoint开始，只重放可验证、带正确 commit/durability状态且幂等键未应用的记录；`Indeterminate` 必须回到 coordination participant query，不凭缺失日志猜测。

### 2. 它明确不做什么

- 不拥有 `SessionRevisionVector`、CrossWorldTxn、Reservation、CommitIntent 或 SnapshotCut；这些归 `coordination`。
- 不定义 ECS/GAS/Replication/Config/Voxel 的领域字段，不复制 Voxel Storage；只消费 provider leases/content-addressed manifest。
- 不绑定本地文件、数据库、对象存储、云 SDK、fsync/group-commit 具体策略；这些由 Host Adapter 与 RT-D-007/D-005 决策。
- 不把 JSON/文本导出作为权威存储，不执行数据中的脚本/类型名/反射构造。
- 不把 `LoggingEvent` 当 WAL/TxnJournal/CommandLog，也不让 Diagnostic queue ack 代替 durable ack。
- 不在 Tick 热路径做不可控 IO，不从 worker直接写 ECS/GAS/Voxel。
- 不拥有 Secret/密钥；可选 encryption metadata校验和 crypto Port由 Host安全设施提供。
- 不依赖具体 Host lifecycle、Game implementation、Hot Reload implementation或 `testing`。

### 3. 代码骨架

#### 3.1 建议目录树

```text
modules/persistence/
├─ src/
│  ├─ Lumio.GameRuntime.Persistence/
│  │  ├─ PersistenceModule.cs                  # Capture/Recover 门面与显式服务组合
│  │  ├─ PersistenceServices.cs                # Providers/Storage/Codec/Compression Ports
│  │  ├─ Lifecycle/SnapshotState.cs            # exact Snapshot lifecycle
│  │  ├─ Lifecycle/RecoveryState.cs            # exact Recovery lifecycle
│  │  ├─ Lifecycle/PersistenceSession.cs        # active/staging/checkpoint/recovery owner
│  │  ├─ Canonical/ICanonicalCodec.cs           # typed incremental encode/decode Port
│  │  ├─ Canonical/CanonicalRecordWriter.cs     # generated field order、length/hash/checksum
│  │  ├─ Canonical/CanonicalRecordReader.cs     # allocation-before-boundary guard
│  │  ├─ Canonical/MessagePackCanonicalCodecAdapter.cs # primitive reader/writer only
│  │  ├─ Compression/ICompressionAdapter.cs     # bounded compress/decompress Port
│  │  ├─ Compression/BrotliCompressionAdapter.cs # BCL Brotli Adapter
│  │  ├─ Compression/DecodeBudget.cs            # input/output/ratio/allocation/depth limits
│  │  ├─ Snapshot/SnapshotCoordinator.cs        # SnapshotCut provider capture/manifest
│  │  ├─ Snapshot/SnapshotManifestBuilder.cs    # participant revision/hash/schema/result
│  │  ├─ Snapshot/SnapshotStagingStore.cs       # Staged/Verified/Active pointer protocol
│  │  ├─ Snapshot/ISnapshotStoragePort.cs       # Host durable medium Port
│  │  ├─ Snapshot/ISnapshotProvider.cs          # neutral provider contract
│  │  ├─ Snapshot/CheckpointManager.cs          # verified checkpoint/retention pointer
│  │  ├─ Durable/IDurableRecordStoragePort.cs   # append/read/flush durable medium Port
│  │  ├─ Durable/TxnJournalAdapter.cs            # 实现 coordination-owned ITxnJournalPort
│  │  ├─ Durable/CommandLogAdapter.cs            # generated CommandLogRecord Adapter
│  │  ├─ Durable/WalAdapter.cs                   # generated WalRecordEnvelope Adapter
│  │  ├─ Durable/DurableRecordVerifier.cs        # seq/hash/idempotency/commit/durability
│  │  ├─ Recovery/RecoveryCoordinator.cs        # checkpoint scan/replay orchestration
│  │  ├─ Recovery/RecoveryCursor.cs              # immutable seq/hash/provider cursors
│  │  ├─ Recovery/IRecoveryApplyPort.cs          # owner-specific staged replay Port
│  │  └─ Errors/PersistenceFailure.cs            # Rejected/Retryable/Fatal
│  └─ Lumio.GameRuntime.Persistence.Adapters/
│     └─ GeneratedVoxelSnapshotPortAdapter.cs    # capture/restore/content-addressed manifest
└─ tests/
   ├─ Lumio.GameRuntime.Persistence.Tests/
   │  ├─ CanonicalRoundTripGoldenTests.cs
   │  ├─ CanonicalPropertyTests.cs
   │  ├─ DecodeBudgetFuzzTests.cs
   │  ├─ SnapshotActivationCrashTests.cs
   │  ├─ DurableRecordOrderingTests.cs
   │  ├─ RecoveryReplayTests.cs
   │  └─ VoxelSnapshotContractTests.cs
   └─ Lumio.GameRuntime.Persistence.Benchmarks/
      └─ CodecCompressionRecoveryBenchmarks.cs
```

#### 3.2 核心类型

| 类型/可见性 | 职责与关键字段/不变式 | 关键方法草图与失败表达 | 线程与生命周期 |
|---|---|---|---|
| `PersistenceModule` / `sealed class` / stable candidate, 未冻结 | 窄门面；创建 `PersistenceSession`、发起 capture/recover，不暴露 storage provider类型。字段：`PersistenceServices`。 | `CreateSession`、`Capture(in SnapshotCutView)`、`Recover(in RecoveryRequest)`、`Dispose`；并发 capture/recover按state拒绝。 | Composition Root创建；Host控制面/Simulation Barrier调用。Dispose后拒绝。 |
| `PersistenceSession` / `sealed class` / internal | 唯一拥有 Snapshot/Recovery state、active/staging/checkpoint pointers、queue/cursors。字段绑定 Session/Release/Schema。 | `BeginCapture`、`MarkStaged`、`Verify`、`Activate`、`BeginRecovery`、`FailRecovery`；exact状态。 | Owner控制面写；worker只持immutable job。Dispose释放leases/queues。 |
| `SnapshotState` / `enum` / stable candidate, 未冻结 | `Idle, Capturing, Encoding, Staged, Verified, Active, Invalid`；不新增同义状态。 | 内部 transition table；Active pointer only after Verified+durability ack。 | 值对象。 |
| `RecoveryState` / `enum` / stable candidate, 未冻结 | `Opening, CheckpointVerified, LogScanning, Replaying, Recovered, RecoveryFailed`。 | 内部 transition table；RecoveryFailed不可继续apply。 | 值对象。 |
| `ICanonicalCodec` / `interface` / internal Port | 按 generated descriptor编码/解码 typed record；库实现只提供 primitive，不决定字段顺序/默认值。 | `Encode<T>(in T, in CanonicalSchema, IBufferWriter<byte>) -> CodecResult`；`Decode<T>(ReadOnlySequence<byte>, in CanonicalSchema, in DecodeBudget)`；unknown required/duplicate/length错误拒绝。 | 无 mutable global resolver；实例可并发。Composition Root dispose adapter。 |
| `CanonicalRecordWriter` / `ref struct` / internal | 严格按 generated field ordinal/type/endianness写入；累计 length/hash/checksum，禁止对象地址/collection insertion order。 | `WriteHeader`、typed `Write*`、`WriteCollectionCanonical`、`Complete`；重复/越序字段 Fatal developer contract。 | stack-only；单 encode job。 |
| `CanonicalRecordReader` / `ref struct` / internal | 先验证 Magic/version/declared length/compression/allocation，再 materialize；记录 consumed bytes/depth。 | `ReadHeader`、`TryRead*`、`SkipKnownOptional`、`Complete`；trailing/duplicate/unknown required拒绝。 | stack-only；单 decode job。 |
| `DecodeBudget` / `readonly record struct` / stable candidate, 未冻结 | 包含 compressed/uncompressed length、ratio、allocation、collection count、depth等配置值；全部有单位。 | `ValidateHeader`、`Reserve`、`ObserveExpansion`；任何超限在分配前返回 Rejected。 | 不可变。 |
| `SnapshotCoordinator` / `sealed class` / internal | 消费 `SnapshotCut` 并捕获 ECS/GAS/Replication/Config/Voxel provider leases；生成完整 manifest，不复制mutable state。 | `CaptureProviders`、`Encode`、`Stage`、`Verify`、`Activate`；任一 provider失败不产伪完整 Snapshot。 | Barrier capture；encode/IO worker；activation由控制线程。 |
| `SnapshotManifestBuilder` / `sealed class` / internal | 逐 participant记录 Revision、Hash、SchemaEpoch、ProviderResult/content reference；固定排序。 | `AddProviderResult`、`Complete`；duplicate/missing required provider拒绝。 | 单 capture job；complete后immutable。 |
| `ISnapshotStoragePort` / `interface` / Host Port | 抽象 staging write/read/verify/durable commit/atomic active pointer，不暴露 path/database SDK type。 | `OpenStaging`、`WriteChunk`、`FlushDurable`、`VerifyStored`、`Activate`、`OpenActive`；每个操作返回 idempotency/durability evidence。 | Host实现；worker使用。Dispose/closed返回 PortClosed。 |
| `TxnJournalAdapter` / `sealed class` / internal | 实现 coordination-owned `ITxnJournalPort`，把 generated TxnJournalRecord追加到 durable storage并返回 durable ack。 | `AppendCommitIntent`、`AppendParticipantState`、`AppendCommitted`、`Scan`；RecordSeq/PreviousHash/idempotency校验。 | Owner/worker经有界 durable queue；ack只在Port durability条件满足后。 |
| `CommandLogAdapter` / `sealed class` / internal | 追加/扫描 generated CommandLogRecord；不使用 LoggingEvent。 | `Append`、`Flush`、`ScanCommitted`；duplicate key返回原结果。 | 有界 durable queue；dispose前明确flush或failure。 |
| `WalAdapter` / `sealed class` / internal | 追加/扫描 generated WalRecordEnvelope，维护 checkpoint关联与提交标记。 | `Append`、`ReadFrom`、`VerifyChain`；截断/未知record kind分类。 | 同上。 |
| `RecoveryCoordinator` / `sealed class` / internal | 从verified checkpoint扫描记录、构建幂等 replay plan、通过owner Ports在重建Barrier应用；Indeterminate委托coordination查询。 | `Open`、`VerifyCheckpoint`、`ScanLogs`、`BuildReplayPlan`、`Replay`；首差异/证据不一致到 RecoveryFailed。 | 恢复控制线程写；解析可worker；apply只在owner reconstruction context。 |
| `GeneratedVoxelSnapshotPortAdapter` / `sealed class` / internal Adapter | 映射 generated Voxel capture/restore/content-addressed chunk manifest；不复制Chunk Storage。 | `Capture`、`Restore`、`VerifyManifest`；method/field以生成物为准。 | Native/Voxel completion经有界 queue；token/lease release幂等。 |
| `PersistenceFailure` / `readonly record struct` / stable candidate, 未冻结 | generated error identity + class + Snapshot/Record/Provider/cursor/hash context。 | Rejected/Retryable/Fatal factories；不把异常文本当稳定码。 | 不可变；Fatal生成Failure Bundle并交Host维护。 |

#### 3.3 稳定候选 API 与内部边界

- 稳定候选 API只暴露 generated Snapshot/Record/Revision IDs、本仓 immutable result/lease/Port；不得暴露 FileStream、database client、MessagePack/Brotli具体类型。
- `ITxnJournalPort`契约由调用方 `coordination` 所有；`TxnJournalAdapter`在 persistence 实现它，从而避免 `coordination -> persistence` 编译环。
- Canonical bytes的字段序、数值表示、Hash/Checksum/commit marker来自架构源；第三方 codec/压缩只是可替换机制。
- Voxel Snapshot契约只见 generated capture/restore/token/content refs，不见Chunk内部 storage。

```csharp
public interface IPersistenceRuntime
{
    PersistenceResult<SnapshotCaptureHandle> Capture(in SnapshotCutView cut);
    PersistenceResult<RecoveryHandle> Recover(in RecoveryRequest request);
}

internal interface ICanonicalCodec
{
    CodecResult Encode<T>(
        in T value,
        in CanonicalSchema schema,
        IBufferWriter<byte> destination);

    CodecResult<T> Decode<T>(
        in ReadOnlySequence<byte> source,
        in CanonicalSchema schema,
        in DecodeBudget budget);
}

public interface ISnapshotStoragePort
{
    StorageResult<StagingHandle> OpenStaging(in SnapshotHeaderView header);
    StorageResult Write(StagingHandle staging, in ReadOnlySequence<byte> bytes);
    StorageResult<DurabilityReceipt> FlushDurable(StagingHandle staging);
    StorageResult Activate(StagingHandle staging, in DurabilityReceipt receipt);
}

// 归 coordination 所有，persistence 只提供实现。
internal sealed class TxnJournalAdapter : ITxnJournalPort
{
    public ValueTask<JournalAppendResult> AppendAsync(
        in TxnJournalRecordView record,
        CancellationToken cancellationToken);
}
```

### 4. 与成熟方案怎么接

| 能力 | 采用 | 不采用 | Adapter | 稳定 API 可见第三方类型 | 许可证 | 风险/控制 |
|---|---|---|---|---|---|---|
| Canonical primitive codec | MessagePack-CSharp `MessagePackReader/Writer` 仅作primitive，经source-generated/static configuration | Typeless/Contractless/global resolver；MemoryPack直接定义跨运行时canonical格式；自研完整serializer | `MessagePackCanonicalCodecAdapter` | 否 | MIT | 字段序/endianness/duplicates/unknown/Hash由Canonical layer控制；Unity/AOT需compile fixture。 |
| 压缩 | BCL Brotli首选；ZstdSharp作为Benchmark候选且必须经Adapter | 自研压缩算法；把压缩bytes直接当canonical hash输入而不含元数据 | `BrotliCompressionAdapter` / `ZstdCompressionAdapter`候选 | 否 | BCL MIT / ZstdSharp MIT | 解压ratio/output/allocation限额；供应商差异不得改变未压缩canonical bytes。 |
| 缓冲 | `ArrayPool<byte>`、`MemoryPool<byte>`、`IBufferWriter<byte>` | 无界MemoryStream复制；全局object pool | Canonical/Storage buffer adapters | 否 | BCL MIT | lease release/use-after-return与zeroing policy测试。 |
| 存储 | Host-owned `ISnapshotStoragePort`/`IDurableRecordStoragePort` | 绑定FileStream/SQLite/S3/Azure SDK到稳定Runtime | Host adapters | 否 | 由Host选型 | durability语义必须由receipt/capability表达，RT-D-007前不宣称fsync策略。 |
| 测试/Fuzz | xUnit v3、CsCheck、SharpFuzz、BenchmarkDotNet | 自研runner/fuzzer/benchmark | 测试工程 | 否 | Apache-2.0 / MIT | decompression bomb/length/duplicate/unknown/truncation corpus必须保存。 |

**自研最小范围。** 只自研 Canonical字段序/边界检查、Snapshot manifest和Staging/Activate状态机、generated durable record验证与恢复编排。这些是Lumio语义；primitive codec、压缩、buffer pool、storage backend、测试工具均复用成熟方案或Host Port。Codec/Compression/Storage可替换，前提是Golden bytes、恢复结果和Benchmark门同时通过。

### 5. 输入 / 输出 / 依赖

**Consumes**

- 来自 `coordination` 的 `SnapshotCutView`、`SessionRevisionVectorView`、`ITxnJournalPort` contract 与 Indeterminate query Port。
- 来自 `ecs`、`gas`、`replication`、`config` 的 immutable snapshot providers。
- 来自 Generated Voxel Snapshot Contract 的 capture/restore/content-addressed manifest/revision tokens。
- 来自 generated architecture contracts 的 SnapshotHeader、TxnJournalRecord、CommandLogRecord、WalRecordEnvelope。
- 来自 Host 的 `ISnapshotStoragePort`、`IDurableRecordStoragePort`、可选crypto Port与durability capability。
- 来自 `observability` 的 evidence Port（但 durable records不经Diagnostic queue）。

**Produces**

- 给 Host/运维：verified Active Snapshot pointer、Checkpoint、RecoveryResult与维护动作。
- 给 `coordination`：TxnJournal durable append/query results（通过其Port）。
- 给 `simulation`/reconstruction：typed staged replay batches与recovery cursor；应用由owner context完成。
- 给 `testing`：Canonical bytes/hash、corrupt/truncation evidence、replay inputs。
- 给 `observability`：Snapshot/record IDs、hashes、queue/latency/failure evidence，不泄漏secret。

**编译依赖**

- 允许：`ecs`、`gas`、`replication`、`coordination`、`config`、`observability`、Generated Voxel Snapshot Contract、generated persistence schemas。
- 具体 Storage/Crypto/Compression供应商只通过Adapter/Port，不形成stable API依赖。
- `coordination`不反向引用本程序集；Journal Port定义在coordination。

**禁止依赖**

- Host lifecycle/文件/数据库/cloud implementation types。
- VoxelEngine storage/Chunk internals、mutable World refs。
- 具体 Game content/hot gameplay assembly。
- `simulation`或`hot-reload`实现的反向依赖。
- `testing`程序集、Diagnostic LoggingEvent作为durable record。

### 6. 队列、预算、线程

| 实现对象 | 元素/资源 | 容量/预算来源 | 满载/超限动作 | 顺序/幂等键 | Metric |
|---|---|---|---|---|---|
| `PersistenceWorkQueue` | immutable capture/encode/stage/verify jobs | `PersistenceQueueCapacity` / Host capability | 满载停止新权威 ingress或进入维护；不得丢失已接收权威工作 | SnapshotId/JobId | `runtime.persistence.queue.depth`、`.full_total` |
| `DurableRecordQueue` | TxnJournalRecord/CommandLogRecord/WalRecordEnvelope | `DurableLogQueueCapacity` | 不允许drop；满载停止ingress/maintenance，已durable ack前不报告成功 | `IdempotencyKey + RecordSeq + PreviousHash` | `runtime.durable_log.queue.depth`、`.full_total`、`.append_latency` |
| `CodecAllocationBudget` | compressed/uncompressed bytes、collections、depth | `DecodeBudget` fields from Config/Capability | 超限在materialize前Rejected；不自动扩大 | SnapshotId/RecordSeq + PayloadHash | `runtime.persistence.decode.rejected_total`、`.allocated_bytes` |
| Generated Voxel Snapshot completion via Native Completion | immutable token/manifest/restore result | `NativeCompletionQueueCapacity` | 可靠completion不丢；full使capture/recovery失败并升级 | JobId/Token | `runtime.persistence.voxel_completion_latency`、`runtime.native_completion.full_total` |

- SnapshotCut/provider capture由Simulation Owner Thread在Barrier固定；worker不得持mutable World引用。
- 编码、压缩、hash、storage IO可worker执行，输出immutable result；activation pointer由Persistence控制线程串行切换。
- Durable record success只有在Storage Port返回所需durability receipt后；Diagnostic sink success与之无关。
- Recovery apply只在重建/owner Barrier，任何字段写入失败按V1 Fail-stop；不能在worker悄悄修补。

### 7. 失败与恢复

| 分类 | 触发例 | 返回/可观察结果 | World/Session | Failure Bundle | Journal/证据查询 |
|---|---|---|---|---|---|
| 可拒绝 | Magic/SchemaVersion/Length/Hash/Checksum/unknown required/duplicate field错误 | `PersistenceFailure.Rejected`；在materialize/activate前停止 | 运行Session不Fault，保留旧Active；恢复请求可失败 | 损坏输入需bundle fragment/样本hash | 查询header、schema、byte offset、payload hash |
| 可拒绝 | Decode allocation/decompression ratio/depth超限 | Rejected `DecodeBudgetExceeded`；不分配超限对象 | 不Fault；可安全拒绝外部snapshot | 恶意/恢复阻塞时保存bundle | 查询DecodeBudget与observed expansion |
| 可拒绝 | Snapshot manifest缺participant/revision/hash不一致 | Rejected/Invalid；Staging不激活 | 不改变当前Active；若当前无有效snapshot由Host决定维护 | 是，尤其恢复/发布流程 | 查询provider results、SnapshotCut、manifest hash |
| 可重试 | 暂时Storage IO/queue full且未durable ack | Retryable，保持同idempotency key/staging handle | 新authority ingress暂停；当前World按policy继续或维护 | 持续失败需bundle | 查询queue、port result、staging status |
| 可重试 | 重复append/activate/replay | 返回原durable/AlreadyActive/AlreadyApplied result | 不Fault | 否 | 查询idempotency index/RecordSeq/active pointer |
| 可重试 | Checkpoint读取暂时不可用但旧verified副本存在 | Retryable/选择另一个已验证checkpoint | Recovery保持Opening/CheckpointVerified前 | 必要时 | 查询checkpoint catalog/hash/attempt |
| 可致命 | 所有有效Checkpoint损坏或PreviousHash/commit证据互相矛盾 | Fatal `RecoveryEvidenceInconsistent` -> RecoveryFailed | Session不启动/进入维护；不得猜测 | 是 | 查询所有checkpoint/log scan/first divergent record |
| 可致命 | Canonical同输入产生不同bytes/hash或decoder materialize不一致 | Fatal deterministic codec violation | 进程/Release隔离，禁止继续写同格式 | 是 | 查询Golden bytes、codec version、platform/AOT metadata |
| 可致命 | 已开始owner replay后字段写入失败/partial visibility | Fatal fail-stop | Reconstruction Session Faulted，从其他有效Snapshot/Release恢复 | 是 | 查询replay cursor、record receipts、owner state hash |

### 8. 测试面

**单元（本模块测试工程）**

- Snapshot/Recovery exact state machine、Active pointer只在Verified+durability receipt后切换。
- Canonical primitive/collection order、duplicate/unknown/trailing/length/endianness验证。
- DurableRecord seq/PreviousHash/payload hash/idempotency/commit/durability校验。
- Recovery plan只包含eligible committed records并保持owner/order边界。

**Golden（本模块测试工程）**

- 架构源 SnapshotHeader、active snapshot、TxnJournal/CommandLog/WAL/cross-world txn正反fixtures逐字节/逐结果运行。
- 同logical object在CoreCLR/NativeAOT/Unity-compatible target产同canonical bytes（目标可用时）；不可用target记录为未执行而非通过。

**Property / Fuzz（本模块测试工程）**

- 随机typed value round-trip，encode(decode(bytes))只对canonical valid bytes相等；任意truncation不得越界读取。
- 随机duplicate/unknown/length/ratio/depth输入必须在budget内终止；保存SharpFuzz corpus。
- 任意crash point下Active pointer始终指向完整verified snapshot或旧有效snapshot。

**故障 / Reference Host**

- 磁盘满、short write、flush/rename/activate失败、crash at each staging/durable record boundary、duplicate replay、Indeterminate query。
- Voxel provider missing/corrupt token、ECS/GAS/Replication provider revision mismatch；不得产完整manifest。

**Benchmark / Soak**

- 固定snapshot shapes/record rates量测encode/decode/compress/stage/recover p50/p95/p99、allocation、ratio、owner-thread block。
- 不同codec/compression/storage Adapter只作为RT-D-007证据，不改变Golden语义。

### 9. 本模块任务拆解

#### `persist-canonical-codec`

- **一句话目标**：实现generated字段序驱动的Canonical writer/reader和MessagePack primitive Adapter。
- **涉及文件集**：
  - `modules/persistence/src/Lumio.GameRuntime.Persistence/Lumio.GameRuntime.Persistence.csproj`
  - `modules/persistence/tests/Lumio.GameRuntime.Persistence.Tests/Lumio.GameRuntime.Persistence.Tests.csproj`
  - `modules/persistence/src/Lumio.GameRuntime.Persistence/Canonical/ICanonicalCodec.cs`
  - `modules/persistence/src/Lumio.GameRuntime.Persistence/Canonical/CanonicalRecordWriter.cs`
  - `modules/persistence/src/Lumio.GameRuntime.Persistence/Canonical/CanonicalRecordReader.cs`
  - `modules/persistence/src/Lumio.GameRuntime.Persistence/Canonical/MessagePackCanonicalCodecAdapter.cs`
  - `modules/persistence/tests/Lumio.GameRuntime.Persistence.Tests/CanonicalRoundTripGoldenTests.cs`
  - `modules/persistence/tests/Lumio.GameRuntime.Persistence.Tests/CanonicalPropertyTests.cs`
- **验收标准**：
  - [ ] Golden Snapshot/record types产生固定bytes/hash；重复运行和collection insertion order不改变。
  - [ ] Typeless/Contractless/global resolver在依赖/API扫描中不存在。
  - [ ] duplicate/unknown required/trailing/length/endianness错误被分类拒绝。
  - [ ] public签名不含MessagePack类型；primitive Adapter可替换。
- **依赖**：`cfg-generated-table-validation`、`obs-event-ports-and-context`
- **Consumes**：generated CanonicalSchema/typed records、IBufferWriter。
- **Produces**：canonical bytes/typed decode result/hash/checksum evidence。
- **成熟方案**：MessagePack-CSharp primitive reader/writer behind custom canonical layer。
- **明确不做**：不定义新Schema字段、不压缩/写盘。

#### `persist-compression-and-decode-budget`

- **一句话目标**：实现Brotli Adapter与分配前的压缩比/输出/集合/深度预算保护。
- **涉及文件集**：
  - `modules/persistence/src/Lumio.GameRuntime.Persistence/Compression/ICompressionAdapter.cs`
  - `modules/persistence/src/Lumio.GameRuntime.Persistence/Compression/BrotliCompressionAdapter.cs`
  - `modules/persistence/src/Lumio.GameRuntime.Persistence/Compression/DecodeBudget.cs`
  - `modules/persistence/tests/Lumio.GameRuntime.Persistence.Tests/DecodeBudgetFuzzTests.cs`
- **验收标准**：
  - [ ] 每个budget字段有单位并从Config/Capability显式构造。
  - [ ] malformed/ratio bomb/declared length mismatch在超分配前拒绝。
  - [ ] fuzz在固定memory/time cap内终止并保存最小corpus。
  - [ ] 压缩开关不改变未压缩canonical bytes/hash语义。
- **依赖**：`persist-canonical-codec`、`cfg-immutable-snapshot-reader`
- **Consumes**：canonical bytes、compression metadata、DecodeBudget。
- **Produces**：bounded compressed/decompressed lease或Rejected。
- **成熟方案**：BCL Brotli + ArrayPool/MemoryPool。
- **明确不做**：不自研压缩/加密，不选择生产压缩阈值。

#### `persist-snapshot-staging-activation`

- **一句话目标**：实现Snapshot provider manifest、Staging/Verify/durable Activate与旧Active保留。
- **涉及文件集**：
  - `modules/persistence/src/Lumio.GameRuntime.Persistence/PersistenceModule.cs`
  - `modules/persistence/src/Lumio.GameRuntime.Persistence/PersistenceServices.cs`
  - `modules/persistence/src/Lumio.GameRuntime.Persistence/Lifecycle/SnapshotState.cs`
  - `modules/persistence/src/Lumio.GameRuntime.Persistence/Lifecycle/PersistenceSession.cs`
  - `modules/persistence/src/Lumio.GameRuntime.Persistence/Snapshot/SnapshotCoordinator.cs`
  - `modules/persistence/src/Lumio.GameRuntime.Persistence/Snapshot/SnapshotManifestBuilder.cs`
  - `modules/persistence/src/Lumio.GameRuntime.Persistence/Snapshot/SnapshotStagingStore.cs`
  - `modules/persistence/src/Lumio.GameRuntime.Persistence/Snapshot/ISnapshotStoragePort.cs`
  - `modules/persistence/src/Lumio.GameRuntime.Persistence/Snapshot/ISnapshotProvider.cs`
  - `modules/persistence/src/Lumio.GameRuntime.Persistence/Snapshot/CheckpointManager.cs`
  - `modules/persistence/tests/Lumio.GameRuntime.Persistence.Tests/SnapshotActivationCrashTests.cs`
- **验收标准**：
  - [ ] Snapshot state exact；Active pointer只有Verified+durability receipt后变化。
  - [ ] manifest每个required provider含revision/hash/SchemaEpoch/result，排序固定。
  - [ ] 任一provider/storage/crash失败后旧Active仍可打开且Staging标Invalid/可清理。
  - [ ] 每个activation crash point property都不出现半Active。
- **依赖**：`persist-compression-and-decode-budget`、`coord-snapshot-cut`、`gas-snapshot-hash-and-migration`、`repl-resync-and-fault-matrix`
- **Consumes**：SnapshotCutView、ECS/GAS/Replication/Config providers、ISnapshotStoragePort。
- **Produces**：verified Snapshot manifest/Active pointer/Checkpoint。
- **成熟方案**：Host storage Port + domain staging state machine。
- **明确不做**：不实现具体filesystem/database/fsync policy。

#### `persist-durable-record-adapters`

- **一句话目标**：实现TxnJournal/CommandLog/WAL generated record验证、独立有界durable route和幂等append。
- **涉及文件集**：
  - `modules/persistence/src/Lumio.GameRuntime.Persistence/Durable/IDurableRecordStoragePort.cs`
  - `modules/persistence/src/Lumio.GameRuntime.Persistence/Durable/TxnJournalAdapter.cs`
  - `modules/persistence/src/Lumio.GameRuntime.Persistence/Durable/CommandLogAdapter.cs`
  - `modules/persistence/src/Lumio.GameRuntime.Persistence/Durable/WalAdapter.cs`
  - `modules/persistence/src/Lumio.GameRuntime.Persistence/Durable/DurableRecordVerifier.cs`
  - `modules/persistence/tests/Lumio.GameRuntime.Persistence.Tests/DurableRecordOrderingTests.cs`
- **验收标准**：
  - [ ] 三种record使用各自generated envelope，不使用LoggingEvent payload。
  - [ ] RecordSeq/PreviousHash/PayloadHash/Length/Checksum/IdempotencyKey/commit/durability逐项验证。
  - [ ] durable ack严格来自Storage Port receipt；Diagnostic sink结果不影响。
  - [ ] queue full不drop，返回stop-ingress/maintenance action；duplicate返回原append result。
- **依赖**：`persist-canonical-codec`、`obs-durable-route-and-emergency-path`、`coord-commit-intent-and-apply-order`
- **Consumes**：generated durable records、IDurableRecordStoragePort、coordination ITxnJournalPort contract。
- **Produces**：durable append/query/scan results、backpressure action。
- **成熟方案**：System.Threading.Channels bounded adapter + Host durable Port。
- **明确不做**：不冻结fsync/group-commit/backend，不拥有Txn state。

#### `persist-recovery-replay`

- **一句话目标**：从verified checkpoint扫描durable records、建立幂等replay plan并处理Indeterminate/首差异。
- **涉及文件集**：
  - `modules/persistence/src/Lumio.GameRuntime.Persistence/Lifecycle/RecoveryState.cs`
  - `modules/persistence/src/Lumio.GameRuntime.Persistence/Recovery/RecoveryCoordinator.cs`
  - `modules/persistence/src/Lumio.GameRuntime.Persistence/Recovery/RecoveryCursor.cs`
  - `modules/persistence/src/Lumio.GameRuntime.Persistence/Recovery/IRecoveryApplyPort.cs`
  - `modules/persistence/src/Lumio.GameRuntime.Persistence/Errors/PersistenceFailure.cs`
  - `modules/persistence/tests/Lumio.GameRuntime.Persistence.Tests/RecoveryReplayTests.cs`
- **验收标准**：
  - [ ] Recovery state exact，RecoveryFailed后无继续Apply。
  - [ ] 只重放verified/eligible/committed records且RecordSeq/owner/order稳定。
  - [ ] Indeterminate调用coordination participant query，不以missing marker猜测。
  - [ ] crash/duplicate replay收敛AlreadyApplied；证据矛盾Fatal并产首差异bundle。
- **依赖**：`persist-snapshot-staging-activation`、`persist-durable-record-adapters`、`coord-crash-resolution-and-journal-port`
- **Consumes**：Active checkpoint、durable scans、owner IRecoveryApplyPort、coordination query。
- **Produces**：RecoveryResult/replay cursor/Failure evidence。
- **成熟方案**：domain replay coordinator + generated records。
- **明确不做**：不启动Host/process，不私自修复领域数据。

#### `persist-voxel-snapshot-adapter`

- **一句话目标**：实现Generated Voxel Snapshot capture/restore/content-addressed manifest Adapter及Differential tests。
- **涉及文件集**：
  - `modules/persistence/src/Lumio.GameRuntime.Persistence.Adapters/Lumio.GameRuntime.Persistence.Adapters.csproj`
  - `modules/persistence/benchmarks/Lumio.GameRuntime.Persistence.Benchmarks/Lumio.GameRuntime.Persistence.Benchmarks.csproj`
  - `modules/persistence/src/Lumio.GameRuntime.Persistence.Adapters/GeneratedVoxelSnapshotPortAdapter.cs`
  - `modules/persistence/tests/Lumio.GameRuntime.Persistence.Tests/VoxelSnapshotContractTests.cs`
  - `modules/persistence/benchmarks/Lumio.GameRuntime.Persistence.Benchmarks/CodecCompressionRecoveryBenchmarks.cs`
- **验收标准**：
  - [ ] Adapter仅暴露generated token/revision/content reference/result，不复制Chunk Storage。
  - [ ] Voxel provider result进入SnapshotManifest并与SnapshotCut revision一致。
  - [ ] stale/corrupt/missing content ref被拒绝，restore重复幂等。
  - [ ] Benchmark包含workload/platform/config/provider bytes和owner-thread block，不承诺未测数字。
- **依赖**：`persist-recovery-replay`、`sim-ingress-and-native-completion`
- **Consumes**：Generated Voxel Snapshot Contract、NativeCompletion、SnapshotCut。
- **Produces**：Voxel provider lease/manifest/restore result。
- **成熟方案**：Generated contract Adapter；BCL/selected compression only for manifest payload。
- **明确不做**：不实现Voxel存储、content store或Host FFI lifecycle。


## 3.10. `hot-reload` 模块设架

### 0. 模块身份证

- 目录：`modules/hot-reload/`
- 建议程序集：`Lumio.GameRuntime.HotReload`；Host/CoreCLR/ALC Adapter 由 Host 实现，Runtime 只发布 Scope/Lease/Root-Validation Port
- 建议命名空间：`Lumio.GameRuntime.HotReload`
- 优先级与阶段：P1 / Vertical Slice 与 Production Hardening；基础 Scope/资源登记可在 Foundation 后并行
- 唯一职责：唯一拥有 `GameplayModuleScope`、资源登记/Lease、六步卸载、双 Scope `BarrierSwitch` 和迁移 Staging 的 Runtime 语义。
- RT-D-001 纪律：以上是物理映射建议；即使首期合并程序集，也必须保持命名空间、internal 可见性和依赖测试，不得形成反向引用。

### 1. 它做什么

- Host 在验证 Gameplay Assembly/Manifest/Capability 后，通过 `HotReloadModule.CreateScope` 创建 `GameplayModuleScope`；Runtime Session 持有当前 Active Scope handle，Host 持 ALC/进程资源，二者通过 generated ScopeId/Generation关联而不互相吞并所有权。
- Active Gameplay 取得 Timer、Task、Subscription、Native Lease、Channel Registration、callback等资源前必须登记到 Scope；每项资源带 ScopeId、Generation、ResourceId、kind、owner、取消/完成状态和 Deadline。
- 单 Scope 状态严格为 `Created -> Loaded -> Active -> Quiescing -> Cancelling -> Draining -> Disposing -> ValidatingRoots -> Unloaded`，任一阶段可到 `Faulted`；卸载步骤严格 `Quiesce -> Cancel -> Drain -> Dispose -> ValidateRoots -> Unload`，不得跳步。
- 热更使用双 Scope：`OldActive + NewStaging -> NewValidated -> BarrierSwitch -> OldQuiescing -> OldUnloaded`。NewStaging只读 immutable Snapshot执行 Game Migration Hook并验证，`BarrierSwitch`只在声明 Tick Barrier由 Simulation Owner Thread线性化入口、订阅和Generation。
- 切换前加载/验证/迁移失败的唯一恢复动作是丢弃 NewStaging并让 OldActive继续；切换后失败不得重新激活旧 Scope，Session转 `Faulted`，从有效 Snapshot/Release恢复。
- 成功必须产 `ScopeActivationResult`、`DrainReport`、`RootValidationReport`、`UnloadRequest`和迁移证据；迟到completion/stale lease/closed scope均返回稳定拒绝，不让旧代码继续改World。

### 2. 它明确不做什么

- 不创建或卸载 CoreCLR/ALC、进程、WorldSlot，不拥有 Host Wall Clock、滚动发布或进程重启；这些归 Host。
- 不热更稳定 Runtime、ECS Storage、公共 ABI/Wire/Schema、NativeCore/VoxelEngine binary。
- 不定义 Game 业务 Migration、Formula、Permission、资源补偿或 Release compatibility；只执行已声明 Hook/contract。
- 不允许 Gameplay 直接持有未登记 Timer/Task/Subscription/Socket/native pointer/lease/callback或跨 Scope mutable ref。
- 不把 `BarrierSwitch` 放在任意 worker回调，不在 Tick 中途切换入口。
- 不以“保留旧 ALC/回调继续服务”掩盖 Root leak 或 post-switch失败。
- 不拥有 Snapshot编码/激活；只消费 `persistence`/`coordination` 提供的 immutable migration input reference。
- 不依赖具体 Game assembly、Host ALC implementation、Renderer/Network或 `testing`。

### 3. 代码骨架

#### 3.1 建议目录树

```text
modules/hot-reload/
├─ src/Lumio.GameRuntime.HotReload/
│  ├─ HotReloadModule.cs                     # Scope/Reload 门面与依赖组合
│  ├─ HotReloadServices.cs                   # Simulation barrier、Config、Gas、Host Ports
│  ├─ Scope/GameplayModuleScope.cs           # 单 Scope 状态/registry/generation owner
│  ├─ Scope/GameplayModuleScopeState.cs      # exact single-scope lifecycle
│  ├─ Scope/GameplayModuleScopeHandle.cs     # ScopeId+Generation opaque handle
│  ├─ Scope/ScopeRegistry.cs                 # Active/Staging/retired Scope registry
│  ├─ Resources/ScopeResourceKind.cs         # Timer/Task/Subscription/NativeLease/Channel/Callback
│  ├─ Resources/ScopeResourceLease.cs        # ResourceId/generation/deadline/status
│  ├─ Resources/ScopeResourceRegistry.cs     # register/release/cancel/complete linearization
│  ├─ Resources/ResourceCompletion.cs        # immutable worker completion
│  ├─ Unload/ScopeUnloadCoordinator.cs       # six-step protocol
│  ├─ Unload/DrainCoordinator.cs             # deadline/status/late completion handling
│  ├─ Unload/DrainReport.cs                  # remaining resources per kind/owner
│  ├─ Unload/IHostUnloadPort.cs              # Host ALC unload request/result Port
│  ├─ Roots/IRootValidationPort.cs            # Host/runtime root enumeration/validation Port
│  ├─ Roots/RootValidationReport.cs           # stable leak evidence，不含 raw object graph
│  ├─ Reload/ReloadSession.cs                 # OldActive/NewStaging/NewValidated state owner
│  ├─ Reload/ReloadSessionState.cs            # exact dual-scope states/BarrierSwitch
│  ├─ Reload/BarrierSwitchCoordinator.cs      # Tick barrier atomic entry/subscription swap
│  ├─ Reload/IScopeSwitchPort.cs              # Simulation-owned switch Port
│  ├─ Migration/IGameplayMigrationPort.cs     # Game pure migration Hook Port
│  ├─ Migration/MigrationStagingContext.cs    # immutable old snapshot + new staging output
│  ├─ Migration/MigrationResult.cs            # version/hash/schema/result
│  ├─ Budget/HotReloadBudget.cs               # resource/deadline/completion caps
│  └─ Errors/HotReloadFailure.cs              # Rejected/Retryable/Fatal + recovery action
└─ tests/
   ├─ Lumio.GameRuntime.HotReload.Tests/
   │  ├─ ScopeLifecycleGoldenTests.cs
   │  ├─ ResourceLeasePropertyTests.cs
   │  ├─ SixStepUnloadTests.cs
   │  ├─ DualScopeBarrierSwitchTests.cs
   │  ├─ MigrationStagingTests.cs
   │  └─ RootLeakFaultTests.cs
   └─ Lumio.GameRuntime.HotReload.Soaks/
      └─ HundredReloadSoakTests.cs
```

#### 3.2 核心类型

| 类型/可见性 | 职责与关键字段/不变式 | 关键方法草图与失败表达 | 线程与生命周期 |
|---|---|---|---|
| `HotReloadModule` / `sealed class` / stable candidate, 未冻结 | 窄门面；组合 ScopeRegistry、switch/root/unload/migration Ports。字段：`HotReloadServices`，不持 Host ALC。 | `CreateScope`、`BeginReload`、`UnloadScope`、`GetScopeStatus`；输入必须带 generated Release/Manifest/Capability。 | Composition Root创建，Session/Host控制面调用。Dispose时对未关闭 Scope返回显式报告。 |
| `GameplayModuleScope` / `sealed class` / stable candidate, 未冻结 | 单 Gameplay Assembly的 Runtime资源边界。字段：`ScopeId`、`Generation`、`ReleaseId`、`ManifestHash`、`GameplayModuleScopeState`、`ScopeResourceRegistry`；identity终生不变。 | `MarkLoaded`、`Activate`、`Quiesce`、`Cancel`、`Drain`、`DisposeResources`、`ValidateRoots`、`MarkUnloaded`、`Fault`；非法迁移分类。 | 状态由 Host/Simulation Owner Thread写；worker只提交completion。Unloaded后全部API返回ScopeClosed/StaleLease。 |
| `GameplayModuleScopeState` / `enum` / stable candidate, 未冻结 | 只含 `Created, Loaded, Active, Quiescing, Cancelling, Draining, Disposing, ValidatingRoots, Unloaded, Faulted`。 | 无 public setter；Scope内部transition table。 | 值对象。 |
| `GameplayModuleScopeHandle` / `readonly record struct` / stable candidate, 未冻结 | `ScopeId + Generation` opaque handle；防旧回调命中新 Scope。 | `ValidateAgainst(scope)`；default/unknown/stale返回Rejected。 | 不可变，可跨线程。 |
| `ScopeResourceLease` / `readonly record struct` / stable candidate, 未冻结 | 包含 Scope/Generation/ResourceId/Kind/Deadline/status token；不包含 raw Timer/Task/native pointer。 | `Release`/`Complete`通过registry；重复返回原结果，generation mismatch拒绝。 | 不可变；实际资源由对应 Adapter/Host持有。 |
| `ScopeResourceRegistry` / `sealed class` / internal | 唯一拥有每Scope资源登记、owner、cancel/complete/dispose状态与late-completion拒绝表。 | `Register`、`Release`、`BeginQuiesce`、`MarkCancelled`、`AcceptCompletion`、`SnapshotOutstanding`；Quiescing后注册拒绝。 | Owner Thread写；completion queue consumer在Owner Thread调用。Scope Unloaded后清理并保留有界stale generation拒绝信息。 |
| `ScopeUnloadCoordinator` / `sealed class` / internal | 严格执行六步，只有前一步成功/明确fault结果后进入下一步；生成每步证据。 | `RunStep(in UnloadCommand)`、`ContinueWithCompletion`、`AbortBeforeUnload`；不能跳过ValidateRoots。 | Host/Simulation控制线程；等待异步资源通过queue。完成后向Host发Unload request。 |
| `DrainCoordinator` / `sealed class` / internal | 按逻辑/Host capability deadline跟踪Task/Timer/Lease/callback完成，生成 remaining report。 | `Begin`、`AcceptCompletion`、`Poll`、`Expire`；Deadline内未完返回Pending可重试，超时Fatal。 | Owner Thread；不busy wait、不持World/native锁。 |
| `ReloadSession` / `sealed class` / internal | 双Scope状态所有者。字段：OldActive/NewStaging handles、migration result、switch barrier/tick、post-switch flag。 | `StageNew`、`ValidateNew`、`ScheduleBarrierSwitch`、`MarkSwitched`、`QuiesceOld`、`Complete`、`Fail`；恢复动作由pre/post switch唯一决定。 | Owner Thread。完成/失败后释放staging/old证据lease。 |
| `ReloadSessionState` / `enum` / internal | 表示 `OldActiveNewStaging, NewValidated, BarrierSwitch, OldQuiescing, OldUnloaded, Faulted`；不替代单Scope状态。 | 由ReloadSession迁移。 | 值对象。 |
| `BarrierSwitchCoordinator` / `sealed class` / internal | 在Simulation声明barrier原子替换Gameplay入口、Processor/Formula/Subscription generation；成功点是不可逆的线性化点。 | `PrepareSwitch`、`CommitAtBarrier`、`GetSwitchReceipt`；preflight拒绝在switch前，commit后failure为Fatal。 | 只由Simulation Owner Thread在barrier调用。无worker调用。 |
| `IScopeSwitchPort` / `interface` / simulation-owned Port | 由simulation提供对当前Gameplay entry set的prepare/atomic switch，不使hot-reload反向拥有Tick。 | `Prepare(in ScopeSwitchPlan) -> ScopeSwitchPrepareResult`；`Commit(in PreparedScopeSwitch) -> ScopeSwitchReceipt`。 | Owner Thread/Barrier only；dispose/paused状态按contract返回。 |
| `IGameplayMigrationPort` / `interface` / stable Port, 未冻结 | Game实现的版本化纯Migration Hook；只读旧 immutable snapshot，在NewStaging输出新typed staging state。 | `Migrate(in MigrationStagingContext) -> MigrationResult`；不能直接改OldActive/World/Host资源。 | 可worker执行但输入/输出immutable、有budget；Scope generation验证。 |
| `IRootValidationPort` / `interface` / Host/Runtime Port | 检查旧Scope可回收根：registered resource、delegate/subscription/native lease/ALC external root；只返回安全摘要/hash。 | `Validate(in RootValidationRequest) -> RootValidationReport`；unknown/uninspectable必显式。 | Host实现可能需要GC/ALC协作；Runtime不创建ALC。 |
| `IHostUnloadPort` / `interface` / Host Port | 收到已通过六步与root validation的Scope unload request，执行ALC unload并返回证据。 | `UnloadAsync(in HostUnloadRequest, CancellationToken) -> HostUnloadResult`。 | Host线程/worker；completion经bounded queue回Owner Thread。 |
| `HotReloadBudget` / `readonly record struct` / internal | 资源总量/每kind、completion queue、drain/root validation/migration deadline与bytes上限；来自Config/Capability。 | `Validate`、`CanRegister`；不含硬编码容量承诺。 | 不可变。 |
| `HotReloadFailure` / `readonly record struct` / stable candidate, 未冻结 | generated error identity + class + Scope/Generation/Resource/step + exact recovery action (`KeepOldActive`或`FaultAndRecoverSnapshotRelease`)。 | Rejected/Retryable/Fatal factories；post-switch不可返回KeepOldActive。 | 不可变；Fatal生成FailureBundle并通知Host/Simulation。 |

#### 3.3 稳定候选 API 与内部边界

- 稳定候选API只暴露 generated Scope/Release/Manifest IDs、本仓opaque leases/reports/results；不暴露 `AssemblyLoadContext`、Task/Timer concrete handles、native pointers。
- `IScopeSwitchPort`由调用方 simulation 所有或中立contract承载；HotReload调用它，避免simulation因reload反向依赖。
- `IGameplayMigrationPort`由本模块声明、Game实现；Migration只读Snapshot，在Staging生成结果，不能访问OldActive mutable state。
- pre/post `BarrierSwitch` 恢复动作写入类型不变量，避免调用方随意选择旧Scope回退。

```csharp
public interface IHotReloadRuntime
{
    HotReloadResult<GameplayModuleScopeHandle> CreateScope(
        in GameplayScopeDescriptor descriptor);
    HotReloadResult<ReloadSessionHandle> BeginReload(
        GameplayModuleScopeHandle oldActive,
        in GameplayScopeDescriptor newScope,
        in MigrationInputReference migrationInput);
}

public interface IGameplayMigrationPort
{
    MigrationResult Migrate(in MigrationStagingContext context);
}

internal interface IScopeSwitchPort
{
    ScopeSwitchPrepareResult Prepare(in ScopeSwitchPlan plan);
    ScopeSwitchReceipt CommitAtBarrier(in PreparedScopeSwitch prepared);
}

public interface IHostUnloadPort
{
    ValueTask<HostUnloadResult> UnloadAsync(
        in HostUnloadRequest request,
        CancellationToken cancellationToken);
}
```

### 4. 与成熟方案怎么接

| 能力 | 采用 | 不采用 | Adapter | 稳定 API 可见第三方类型 | 许可证 | 风险/控制 |
|---|---|---|---|---|---|---|
| 异步完成队列 | `System.Threading.Channels` bounded channel，经Scope generation/idempotency包装 | 自研lock-free event bus；unbounded task continuation list | `HotReloadCompletionQueueAdapter` | 否 | BCL MIT | full action不是drop；Reject new/Count，Drain超时升级。 |
| 取消/超时 | BCL `CancellationTokenSource`作为Adapter机制；Deadline来自Config/Host capability | 用Wall Clock作为Gameplay确定性输入；Thread.Abort | `ScopeCancellationAdapter` | 否 | BCL MIT | Host deadline仅治理卸载，不进入authority hash。 |
| 资源登记 | BCL collections + opaque lease/generation | 通用第三方DI/container/service locator；raw Task/Timer/native pointer稳定API | `ScopeResourceRegistry` | 否 | BCL MIT | 必须有per-kind quota和root validation differential。 |
| ALC卸载 | Host `IHostUnloadPort` + .NET `AssemblyLoadContext`仅Host实现 | Runtime创建CoreCLR/ALC；HybridCLR绑入stable core | Host adapter | 否 | BCL MIT / platform-specific | ALC/HybridCLR能力由Host capability；Runtime只消费result。 |
| 测试/Soak | xUnit v3、CsCheck、受控GC/root probes、Benchmark/100-cycle soak | 自研test runner；把一次GC成功当保证 | 测试工程/Reference Host | 否 | Apache-2.0 / MIT | 多次GC与root evidence只作可回收证据，最终Host unload result仍权威。 |

**自研最小范围。** 只自研 Scope/Generation/Resource Lease、六步卸载、双Scope线性化切换、pre/post-switch恢复与Migration Staging语义。Channel、Cancellation、ALC、GC/root inspection、测试框架均由BCL/Host Adapter/成熟工具提供。将来替换Host ALC或平台热更实现时，Scope Port和唯一恢复动作保持不变。

### 5. 输入 / 输出 / 依赖

**Consumes**

- 来自 `simulation` 的 Tick Quiesce/Barrier与 `IScopeSwitchPort`实现；HotReload不拥有Tick。
- 来自 `gas` 的可drain framework/migration view和Game Formula/Hook generation关联。
- 来自 `config` 的 `ConfigSnapshot`、resource/deadline/capability budgets。
- 来自 `persistence`/`coordination`（运行时Port/immutable reference，不新增反向assembly）的有效Migration Snapshot reference；具体依赖方向需经中立generated contract/Composition Root连接。
- 来自 Host 的已验证 Assembly/Manifest/Capability、`IRootValidationPort`、`IHostUnloadPort`。
- 来自 Game 的 `IGameplayMigrationPort`实现。
- 来自 `observability` 的evidence/failure ports。

**Produces**

- 给 Host：Scope state、Drain/RootValidation report、validated unload request、failure recovery action。
- 给 `simulation`：prepared scope switch、switch receipt、SessionFault action。
- 给 `gas`/Game调用边界：current ScopeId/Generation validation与stale rejection。
- 给 `persistence`：versioned MigrationResult/staging reference（不直接写盘）。
- 给 `observability`：resource lifecycle、step durations、leak/timeout/migration/switch evidence。

**编译依赖**

- 允许：`simulation`、`gas`、`config`、`observability`，以及中立generated release/scope/capability/migration contracts。
- 与 persistence/coordination 的snapshot引用经中立contract/Composition Root传入，不建立反向源码依赖；若当前DAG未列边，则本程序集只见opaque generated reference。
- Host/Game只实现Port，不成为源码依赖。

**禁止依赖**

- Host CoreCLR/ALC implementation、process/world-slot/network/storage implementation。
- 具体 Game assembly/content implementation。
- Voxel/native internal handles/pointers（只允许registered opaque lease）。
- `persistence`/`coordination` concrete implementation若不在DAG。
- `testing`程序集、Renderer/Release Pool。

### 6. 队列、预算、线程

| 实现对象 | 元素/资源 | 容量/预算来源 | 满载/超限动作 | 顺序/幂等键 | Metric |
|---|---|---|---|---|---|
| `HotReloadCompletionQueue` | `ResourceCompletion`、HostUnloadResult、RootValidationResult | `HotReloadCompletionQueueCapacity` | 满载拒绝新completion registration并计数；可靠已发任务结果不得drop；Drain deadline超限 -> Faulted/Session action | `ScopeId + Generation + ResourceId` | `runtime.hot_reload.completion.depth`、`.full_total` |
| `ScopeResourceRegistry` | registered resources/leases by kind | `HotReloadBudget` per-kind/total limits | Active时quota超限Rejected；Quiescing后所有new registration拒绝 | `ScopeId + Generation + ResourceId` | `runtime.hot_reload.resources.active`、`.register_reject_total` |
| `MigrationStagingBudget` | immutable snapshot refs、migration output bytes/nodes | Config/Capability migration bytes/node/deadline | pre-switch超限丢弃NewStaging并KeepOldActive；不partial switch | `ReloadSessionId + NewScopeGeneration + MigrationNodeId` | `runtime.hot_reload.migration.bytes`、`.failure_total` |

- Scope/Reload状态与resource registry写入由Host/Simulation Owner Thread串行；worker completion只经bounded queue。
- Quiescing后拒绝新资源；cancel/complete/release竞态以ScopeId+Generation+ResourceId线性化，重复幂等。
- `BarrierSwitch`只在声明Tick barrier执行，成功即不可逆；旧generation迟到回调全部拒绝。
- Drain不在持有World/native锁时等待；Host timeout/GC观测只进治理证据，不进authority hash。

### 7. 失败与恢复

| 分类 | 触发例 | 返回/可观察结果 | World/Session | Failure Bundle | Journal/证据查询 |
|---|---|---|---|---|---|
| 可拒绝 | Manifest/ABI/Capability/Release/Schema不兼容 | `HotReloadFailure.Rejected(IncompatibleScope)`；不创建/激活scope | Session继续OldActive或不启动新scope | Audit/必要时bundle fragment | 查询manifest/hash/capability validator result |
| 可拒绝 | Scope/Generation mismatch、Unload后调用、stale lease | Rejected `ScopeClosed\|StaleLease`；无World/registry change | 不Fault；频繁旧回调可升级诊断 | 否 | 查询scope registry、generation、resource id |
| 可拒绝 | Quiescing后注册资源或quota超限 | Rejected；registry不新增 | 不Fault | 否 | 查询budget/active counts/step |
| 可重试 | Drain未完成且在Deadline内 | `Pending`/Retryable；同resource状态/idempotency保持 | Scope保持Draining，Session按计划quiesced | 超时前通常否 | 查询outstanding resource snapshot/completion queue |
| 可重试 | Root validation/Host unload暂时不可用且尚未越过switch/step deadline | Retryable相同步骤，不重复Dispose已完成资源 | pre-switch可KeepOldActive；old unload阶段继续quiesced | 连续失败bundle fragment | 查询step receipts/host port results |
| 可重试 | 重复Cancel/Release/Completion/Unload命令 | 返回原结果/AlreadyClosed，不重复执行副作用 | 不Fault | 否 | 查询resource/step idempotency index |
| 可致命 | Drain deadline超时、未登记Task/Timer/native lease或Root leak | Fatal；Scope Faulted，不标Unloaded | pre-switch丢NewStaging保Old；post-switch Session Faulted | 是 | 查询DrainReport/RootValidationReport/resource evidence |
| 可致命 | BarrierSwitch成功后new scope/old unload/migration activation失败 | Fatal `PostSwitchFailure`；恢复action只能FaultAndRecoverSnapshotRelease | Session Faulted；禁止重新激活旧Scope | 是 | 查询switch receipt、old/new scope states、snapshot/release refs |
| 可致命 | Host报告ALC无法卸载/Native lease状态矛盾 | Fatal HostUnload/LeaseInvariant | Session或process按Host policy隔离；Scope不伪标Unloaded | 是 | 查询HostUnloadResult、root/lease hashes、failure bundle |

### 8. 测试面

**单元（本模块测试工程）**

- 单Scope exact状态、六步顺序、Quiescing后注册拒绝、Unloaded stale generation。
- cancel/complete/release并发序列的幂等与resource count不为负。
- 双Scope pre/post switch exact恢复action类型不变量。
- MigrationStaging只读old snapshot，不能取得active world/host resources。

**Golden（本模块测试工程）**

- Native ABI/Host Capability/FailureBundle正反fixtures驱动scope加载/拒绝。
- 固定资源/步骤序列产相同DrainReport/RootValidation summary/hash。

**Property（本模块测试工程）**

- 随机register/release/cancel/complete/quiesce序列下，Unloaded后无旧generation完成可被接受。
- 任何pre-switch failure保持OldActive；任何post-switch failure从类型上无法产生ReactivateOld action。

**故障 / Reference Host**

- 未登记Task/Timer/Handle、late callback、Native lease失效、Drain/root/unload timeout、migration throw/OOM、duplicate unload。
- 注入每个六步边界与BarrierSwitch前/后崩溃，验证Session/Scope状态和FailureBundle可replay。

**Soak / 平台验证**

- 连续100次stage/validate/switch/drain/unload，记录managed/native/root/queue/GC与Host unload result；用于RT-D-010。
- 在CoreCLR/目标平台capability上运行；无法运行的平台明确记录未验证，不能推断通过。

### 9. 本模块任务拆解

#### `reload-scope-and-resource-leases`

- **一句话目标**：实现GameplayModuleScope exact生命周期、generation-safe资源登记/Lease和stale completion拒绝。
- **涉及文件集**：
  - `modules/hot-reload/src/Lumio.GameRuntime.HotReload/Lumio.GameRuntime.HotReload.csproj`
  - `modules/hot-reload/tests/Lumio.GameRuntime.HotReload.Tests/Lumio.GameRuntime.HotReload.Tests.csproj`
  - `modules/hot-reload/src/Lumio.GameRuntime.HotReload/HotReloadModule.cs`
  - `modules/hot-reload/src/Lumio.GameRuntime.HotReload/HotReloadServices.cs`
  - `modules/hot-reload/src/Lumio.GameRuntime.HotReload/Scope/GameplayModuleScope.cs`
  - `modules/hot-reload/src/Lumio.GameRuntime.HotReload/Scope/GameplayModuleScopeState.cs`
  - `modules/hot-reload/src/Lumio.GameRuntime.HotReload/Scope/GameplayModuleScopeHandle.cs`
  - `modules/hot-reload/src/Lumio.GameRuntime.HotReload/Scope/ScopeRegistry.cs`
  - `modules/hot-reload/src/Lumio.GameRuntime.HotReload/Resources/ScopeResourceKind.cs`
  - `modules/hot-reload/src/Lumio.GameRuntime.HotReload/Resources/ScopeResourceLease.cs`
  - `modules/hot-reload/src/Lumio.GameRuntime.HotReload/Resources/ScopeResourceRegistry.cs`
  - `modules/hot-reload/src/Lumio.GameRuntime.HotReload/Resources/ResourceCompletion.cs`
  - `modules/hot-reload/tests/Lumio.GameRuntime.HotReload.Tests/ScopeLifecycleGoldenTests.cs`
  - `modules/hot-reload/tests/Lumio.GameRuntime.HotReload.Tests/ResourceLeasePropertyTests.cs`
- **验收标准**：
  - [ ] Scope state/enum exact，无parallel同义状态。
  - [ ] Quiescing后new registration一律Rejected；Unload后调用ScopeClosed。
  - [ ] Property覆盖register/release/cancel/complete/reuse，count不为负且stale generation不命中新scope。
  - [ ] stable API扫描不含Task/Timer/ALC/native pointer具体类型。
- **依赖**：`sim-session-and-run-tick-entry`、`cfg-immutable-snapshot-reader`、`gas-type-handle-registry`、`obs-event-ports-and-context`
- **Consumes**：validated scope descriptor/manifest/capability、Config budget。
- **Produces**：GameplayModuleScopeHandle、ScopeResourceLease/registry status。
- **成熟方案**：BCL collections/Cancellation behind opaque leases。
- **明确不做**：不执行drain/root/unload或BarrierSwitch。

#### `reload-six-step-unload`

- **一句话目标**：实现Quiesce→Cancel→Drain→Dispose→ValidateRoots→Unload严格协议与bounded completion queue。
- **涉及文件集**：
  - `modules/hot-reload/src/Lumio.GameRuntime.HotReload/Unload/ScopeUnloadCoordinator.cs`
  - `modules/hot-reload/src/Lumio.GameRuntime.HotReload/Unload/DrainCoordinator.cs`
  - `modules/hot-reload/src/Lumio.GameRuntime.HotReload/Unload/DrainReport.cs`
  - `modules/hot-reload/src/Lumio.GameRuntime.HotReload/Unload/IHostUnloadPort.cs`
  - `modules/hot-reload/src/Lumio.GameRuntime.HotReload/Roots/IRootValidationPort.cs`
  - `modules/hot-reload/src/Lumio.GameRuntime.HotReload/Roots/RootValidationReport.cs`
  - `modules/hot-reload/src/Lumio.GameRuntime.HotReload/Budget/HotReloadBudget.cs`
  - `modules/hot-reload/src/Lumio.GameRuntime.HotReload/Errors/HotReloadFailure.cs`
  - `modules/hot-reload/tests/Lumio.GameRuntime.HotReload.Tests/SixStepUnloadTests.cs`
  - `modules/hot-reload/tests/Lumio.GameRuntime.HotReload.Tests/RootLeakFaultTests.cs`
- **验收标准**：
  - [ ] step trace exact，测试无法跳过ValidateRoots/Dispose直接Unload。
  - [ ] completion capacity使用HotReloadCompletionQueueCapacity；full不drop可靠结果。
  - [ ] Deadline内Pending可重试，超时Scope Faulted并含outstanding report。
  - [ ] Host unload只在root validation成功后调用；失败不伪标Unloaded。
- **依赖**：`reload-scope-and-resource-leases`、`obs-failure-bundle-assembly`
- **Consumes**：Scope registry、resource completions、IRootValidationPort、IHostUnloadPort、budget。
- **Produces**：DrainReport、RootValidationReport、HostUnloadRequest/result。
- **成熟方案**：System.Threading.Channels + BCL cancellation + Host Ports。
- **明确不做**：不创建/卸载ALC，不决定process恢复策略。

#### `reload-dual-scope-barrier-switch`

- **一句话目标**：实现OldActive/NewStaging/NewValidated/BarrierSwitch/OldQuiescing/OldUnloaded与唯一pre/post恢复动作。
- **涉及文件集**：
  - `modules/hot-reload/src/Lumio.GameRuntime.HotReload/Reload/ReloadSession.cs`
  - `modules/hot-reload/src/Lumio.GameRuntime.HotReload/Reload/ReloadSessionState.cs`
  - `modules/hot-reload/src/Lumio.GameRuntime.HotReload/Reload/BarrierSwitchCoordinator.cs`
  - `modules/hot-reload/src/Lumio.GameRuntime.HotReload/Reload/IScopeSwitchPort.cs`
  - `modules/hot-reload/tests/Lumio.GameRuntime.HotReload.Tests/DualScopeBarrierSwitchTests.cs`
- **验收标准**：
  - [ ] dual state trace exact且switch只由Simulation barrier test port提交。
  - [ ] 所有pre-switch fault返回KeepOldActive并丢NewStaging。
  - [ ] 所有post-switch fault只能返回FaultAndRecoverSnapshotRelease；API无ReactivateOld。
  - [ ] old generation所有late callback/processor/formula/subscription被拒绝。
- **依赖**：`reload-six-step-unload`、`sim-phase-graph-13`
- **Consumes**：Old/New scope handles、IScopeSwitchPort、barrier/Tick context。
- **Produces**：PreparedScopeSwitch、ScopeSwitchReceipt、typed recovery action。
- **成熟方案**：无，纯V1.3双Scope语义。
- **明确不做**：不调度Tick、不恢复Snapshot/Release。

#### `reload-migration-staging`

- **一句话目标**：实现Game纯Migration Port、immutable input与NewStaging version/hash验证。
- **涉及文件集**：
  - `modules/hot-reload/src/Lumio.GameRuntime.HotReload/Migration/IGameplayMigrationPort.cs`
  - `modules/hot-reload/src/Lumio.GameRuntime.HotReload/Migration/MigrationStagingContext.cs`
  - `modules/hot-reload/src/Lumio.GameRuntime.HotReload/Migration/MigrationResult.cs`
  - `modules/hot-reload/tests/Lumio.GameRuntime.HotReload.Tests/MigrationStagingTests.cs`
- **验收标准**：
  - [ ] Migration input只含immutable Snapshot/reference/config/release/schema，不含active World/Scope mutable ref。
  - [ ] output带new release/schema/hash/provider result并在switch前验证。
  - [ ] throw/timeout/budget/schema mismatch均pre-switch丢NewStaging、OldActive不变。
  - [ ] 重复migration同input/idempotency key返回同result/hash。
- **依赖**：`reload-dual-scope-barrier-switch`、`persist-snapshot-staging-activation`
- **Consumes**：valid Snapshot/MigrationInputReference、IGameplayMigrationPort、NewStaging scope。
- **Produces**：validated MigrationResult/Switch eligibility。
- **成熟方案**：Game pure Port + bounded worker/result。
- **明确不做**：不定义业务migration、不激活Snapshot或修改OldActive。

#### `reload-root-validation-soak`

- **一句话目标**：建立root/lease/leak故障矩阵与100次双Scope热更Soak，为RT-D-010收集证据。
- **涉及文件集**：
  - `modules/hot-reload/tests/Lumio.GameRuntime.HotReload.Soaks/Lumio.GameRuntime.HotReload.Soaks.csproj`
  - `modules/hot-reload/tests/Lumio.GameRuntime.HotReload.Tests/RootLeakFaultTests.cs`
  - `modules/hot-reload/tests/Lumio.GameRuntime.HotReload.Soaks/HundredReloadSoakTests.cs`
- **验收标准**：
  - [ ] 覆盖未登记Task/Timer/Subscription/NativeLease/Channel/callback和late completion。
  - [ ] 每个六步边界、switch前后、HostUnload失败均有可重放fault case。
  - [ ] 100次报告含platform/runtime/release/config/resource/GC/native/queue元数据。
  - [ ] 任何leak/timeout不通过增大无限deadline或跳过validation掩盖。
- **依赖**：`reload-migration-staging`、`test-reference-host-shell`、`test-scenario-capability-and-faults`
- **Consumes**：Reference Host、fault adapters、Host root/unload probes。
- **Produces**：RT-D-010 evidence report/FailureBundles。
- **成熟方案**：xUnit v3 + Reference Host + platform ALC/GC probes。
- **明确不做**：不批准具体timeout/escalation数字，不把单平台结果外推。


## 3.11. `testing` 模块设架

### 0. 模块身份证

- 目录：`modules/testing/`
- 建议程序集：`Lumio.GameRuntime.Testing`、`Lumio.GameRuntime.ReferenceHost`、各模块 `.Tests/.Benchmarks/.Soaks`；所有产物仅测试配置引用
- 建议命名空间：`Lumio.GameRuntime.Testing`
- 优先级与阶段：P1 测试支持；`Reference Host` 最小骨架在 Foundation 提前，完整 Fault/Workload 在 Vertical Slice
- 唯一职责：唯一提供非生产 Reference Host、ReferenceVoxelPort、Replay/Hash、Scenario/Capability/Fault Adapter、Fixture Runner与工作负载证据。
- RT-D-001 纪律：以上是物理映射建议；即使首期合并程序集，也必须保持命名空间、internal 可见性和依赖测试，不得形成反向引用。

### 1. 它做什么

- 测试 Composition Root根据 `ScenarioDescriptor`、Required/Provided Capabilities、Release/Schema/Manifest Hash、ConfigSnapshot、Determinism Seed和ResourceBudget创建 `ReferenceHostSession`；它组装真实生产模块公开接口，但生产模块从不引用测试程序集。
- Reference Host默认单Owner Thread，提供可控Host time/tick trigger、World/ReplicaWorld、ReferenceVoxelPort、in-memory durable Ports、full Envelope/Serializer/permission/size/bounded queue pipeline；简化只限Adapter实现，不改变契约。
- 每个Scenario执行 `Created -> Prepared -> Running -> Collecting -> Passed/Failed`，Prepared/Running可到 `Cancelled/TimedOut`，任一状态可到 `InfrastructureFault`；失败仍执行资源清理并单独报告cleanup failure。
- Replay保存输入流、Config/Release/Schema/Mapping/Manifest hashes、Seed、Host profile与fault schedule；按Tick/Phase/Processor重新运行并比较Canonical State Hash，报告首个差异及最小输入窗口。
- Fault Adapter用确定Seed注入delay/jitter/drop/reorder/duplicate/disconnect/reconnect/QueueFull、Txn crash、Snapshot corrupt、Storage fail、Scope leak等，并通过真实bounded queue/Port路径，不直接改World。
- Fixture Runner只消费架构源Schema/fixture/generated contracts；测试结果、scenario/workload JSON属于本仓测试产物，不能反向扩展公共Schema。

### 2. 它明确不做什么

- 不成为任何生产模块的编译/运行时依赖，不在生产构建开启测试后门、固定seed、fake clock或fault injection。
- 不把Reference Host的内存Storage、Clock、Transport、Voxel实现宣传为生产实现。
- 不绕过Envelope、Serializer、Schema、permission、size、bounded queue、Tick barrier，即使PureHeadless/LocalEmbedded。
- 不修改架构源Schema/ID/fixture，不替代模块自身单元测试或Host真实网络/运维测试。
- 不读取真实Secret/用户数据/生产endpoint，不把敏感样本写入Replay/FailureBundle。
- 不通过放宽断言、无限timeout、关闭fault或改变seed来修复失败。
- 不把Benchmark结果在缺硬件/platform/config/workload metadata时用于容量承诺。
- 不拥有产品Game content、真实Renderer、Release Pool或生产ALC lifecycle。

### 3. 代码骨架

#### 3.1 建议目录树

```text
modules/testing/
├─ src/
│  ├─ Lumio.GameRuntime.Testing/
│  │  ├─ TestingModule.cs                      # Scenario/Replay/Fixture/Workload 门面
│  │  ├─ Scenario/ScenarioDescriptor.cs        # profile/capability/seed/workload/fault/budget
│  │  ├─ Scenario/TestScenarioState.cs          # exact testing lifecycle
│  │  ├─ Scenario/ScenarioRunner.cs             # prepare/run/collect/cleanup state owner
│  │  ├─ Scenario/CapabilityMatcher.cs          # Required vs Provided exact matching
│  │  ├─ Scenario/ScenarioResult.cs             # machine-readable pass/fail/infra/cleanup
│  │  ├─ Faults/FaultProfile.cs                 # deterministic fault schedule
│  │  ├─ Faults/FaultInjectionAdapter.cs        # queue/Port-level delay/drop/reorder/crash
│  │  ├─ Faults/FaultEvent.cs                   # immutable tick/phase/target/action
│  │  ├─ Replay/ReplayInput.cs                  # immutable commands/envelopes/config/hashes
│  │  ├─ Replay/ReplayRunner.cs                 # run same input/seed and collect hashes
│  │  ├─ Replay/CanonicalStateHasher.cs         # owner-provided canonical domain hash combiner
│  │  ├─ Replay/FirstDifferenceFinder.cs        # Tick/Phase/Processor/field-path evidence
│  │  ├─ Replay/ReplayResult.cs                 # hash timeline/minimal window/bundle ref
│  │  ├─ Fixtures/ContractFixtureRunner.cs      # architecture schema/valid/invalid fixtures
│  │  ├─ Fixtures/FixtureResult.cs              # accepted/rejected/schema/error metadata
│  │  ├─ Workloads/WorkloadDescriptor.cs        # bots/ticks/shapes/rates/platform metadata
│  │  ├─ Workloads/WorkloadRunner.cs            # metrics/bench/stress/soak orchestration
│  │  ├─ Workloads/WorkloadResult.cs             # raw immutable samples + summary
│  │  └─ Isolation/ProductionDependencyGuard.cs # static project/assembly dependency check
│  └─ Lumio.GameRuntime.ReferenceHost/
│     ├─ ReferenceHost.cs                       # real Runtime composition with fake Host Ports
│     ├─ ReferenceHostSession.cs                # controlled lifecycle/owner thread/run_tick
│     ├─ ReferenceClockPort.cs                   # test trigger only; Runtime sees logical inputs
│     ├─ ReferenceEnvelopePipeline.cs            # full serializer/schema/permission/size/queue
│     ├─ ReferencePersistencePorts.cs             # in-memory durable receipts/crash injection
│     ├─ ReferenceObservabilitySink.cs            # bounded capture/emergency evidence
│     ├─ ReferenceVoxelAuthorityPort.cs           # Foundation authority prepare/commit/abort/status subset
│     ├─ ReferenceVoxelPort.cs                    # generated authority/replica/snapshot contract
│     └─ ReferenceGameplayModule.cs               # minimal deterministic Processor/Formula hooks
├─ tests/Lumio.GameRuntime.Testing.Tests/
│  ├─ ReferenceHostFoundationSliceTests.cs
│  ├─ ReferenceEnvelopeFidelityTests.cs
│  ├─ ReferenceVoxelAuthorityTxnTests.cs
│  ├─ ReferenceVoxelDifferentialTests.cs
│  ├─ ReplayFirstDifferenceTests.cs
│  ├─ ScenarioFaultDeterminismTests.cs
│  ├─ ContractFixtureRunnerTests.cs
│  └─ ProductionIsolationTests.cs
└─ workloads/
   ├─ foundation-single-world.json
   ├─ replication-loss-reorder.json
   ├─ persistence-crash-boundaries.json
   └─ hot-reload-100-cycles.json
```

#### 3.2 核心类型

| 类型/可见性 | 职责与关键字段/不变式 | 关键方法草图与失败表达 | 线程与生命周期 |
|---|---|---|---|
| `TestingModule` / `sealed class` / test-only | 测试门面；组合Scenario/Replay/Fixture/Workload services。绝不被production assembly引用。 | `RunScenario`、`Replay`、`ValidateFixtures`、`RunWorkload`；返回 machine-readable `ScenarioResult`。 | test runner创建/dispose；并行scenario必须隔离目录/ports/seeds。 |
| `ScenarioDescriptor` / `sealed record` / test-only | 字段：ScenarioId、HostProfile、RequiredCapabilities、ProvidedCapabilities、Release/Schema/Manifest hashes、Seed、TickCount、ConfigRef、FaultProfile、Workload、ResourceBudget；全部显式。 | `Validate`；缺元数据/预算/不匹配返回Rejected，不隐式默认生产语义。 | 不可变。 |
| `TestScenarioState` / `enum` / test-only | `Created, Prepared, Running, Collecting, Passed, Failed, Cancelled, TimedOut, InfrastructureFault`。 | 内部transition table。 | 值对象。 |
| `ScenarioRunner` / `sealed class` / test-only | 拥有Test Session、Reference Host、fault schedule、artifacts、cleanup结果。 | `Prepare`、`RunTicks`、`Collect`、`Finish`、`Cleanup`；原始test failure与cleanup failure分开。 | test control thread；authority写仍在Reference owner thread。Dispose强制cleanup。 |
| `ReferenceHost` / `sealed class` / test-only | 组装真实Runtime modules与reference Host Ports，不复制模块语义。字段：CompositionRoot、EnvelopePipeline、Voxel/Storage/Obs Ports。 | `CreateSession`、`RunTick`、`CaptureState`、`DisposeSession`；每个入口走production候选API。 | 默认单Owner Thread；stress可受控worker。Session dispose验证所有handles/scopes/leases。 |
| `ReferenceHostSession` / `sealed class` / test-only | 持SimulationSession、World/ReplicaWorld、logical tick trigger、input queues与artifacts。 | `EnqueueEnvelope`、`RunTick`、`Pause/Drain`、`CaptureHash`、`Dispose`。 | 单Owner Thread写。 |
| `ReferenceVoxelPort` / `sealed class` / test-only Adapter | 实现Generated Voxel authority/replica/snapshot contracts，保留Revision/prepare/commit/idempotency/fault语义；内部可简化storage。 | 按generated method实现capture/restore/query/prepare/apply/abort/status；支持crash/late/duplicate注入。 | Owner Thread状态；worker completion经NativeCompletion queue。Session dispose释放。 |
| `ReferenceEnvelopePipeline` / `sealed class` / test-only | 用同codec/schema/permission/size/bounded queue路径模拟LocalEmbedded/LocalSplitProcess；不直接调用Apply shortcut。 | `Encode`、`ValidatePermissionAndSize`、`EnqueueIngress`、`DecodeAtBarrier`；fault adapter作用在queue/envelope。 | producer/consumer线程按scenario；world mutation只Owner Thread。 |
| `ReplayInput` / `sealed record` / test artifact | 不可变保存command/envelope stream、config/release/schema/mapping/manifest hashes、seed、fault events与expected metadata；不含secret。 | `Validate`、`SliceAround(firstDifference)`、`CanonicalHash`。 | 不可变，可持久化。 |
| `ReplayRunner` / `sealed class` / test-only | 以同scenario/seed/input创建全新ReferenceHost并生成每Tick/Phase/Processor hash timeline。 | `Replay(in ReplayInput) -> ReplayResult`；不能复用原World对象。 | test runner线程；每次全新session并cleanup。 |
| `CanonicalStateHasher` / `sealed class` / test-only combiner | 组合各production provider的canonical domain hashes/Revision；排除对象地址、wall clock、queue timing/cache/diagnostic。 | `Hash(in StateHashInputs) -> StateHash`；provider缺失返回InfrastructureFault或test failure。 | 纯函数/immutable inputs。 |
| `FirstDifferenceFinder` / `sealed class` / test-only | 二分/顺序定位首个Tick，再用phase/processor/domain manifests定位差异；不假装知道未暴露field。 | `Find(expected, actual) -> FirstDifferenceReport`。 | 纯函数。 |
| `FaultInjectionAdapter` / `sealed class` / test-only | 按seed和explicit schedule在Port/queue边界注入delay/jitter/drop/reorder/duplicate/disconnect/QueueFull/crash/corruption。 | `Apply(in FaultEvent, IFaultTarget)`；同seed/input schedule确定。 | 不直接写World；target adapter lifecycle内有效。 |
| `ContractFixtureRunner` / `sealed class` / test-only | 运行architecture generated validators与登记valid/invalid fixtures；记录expected accept/reject。 | `ValidateFixture`、`ValidateRegistryCompleteness`；不修改fixture。 | 可并行只读。 |
| `WorkloadRunner` / `sealed class` / test-only | 执行固定bot/tick/shape/rate/stress/soak，采集raw samples与完整platform metadata。 | `Run(in WorkloadDescriptor) -> WorkloadResult`；缺metadata结果标InvalidEvidence。 | 独立process/runner优先，按测试预算取消。 |
| `ProductionDependencyGuard` / `static class` / test-only | 检查production project/assembly graph没有引用Testing/ReferenceHost/test package；检查测试后门符号不进release。 | `Verify(projectGraph, assemblies) -> GuardResult`。 | CI/build-time。 |

#### 3.3 稳定候选 API 与内部边界

- 本模块没有生产稳定API；其公开类型只供测试/CI/工具，必须在project/assembly graph中与production隔离。
- Reference Host依赖所有production modules是允许方向；任何production module -> testing/reference-host引用由DependencyGuard失败。
- ReferenceVoxelPort和ReferenceEnvelopePipeline实现真实generated/Port contracts，不能新增测试专用捷径到production interfaces。
- Replay/Scenario/Workload输出格式是本仓测试产物；公共字段需要扩展时回到架构源/Tooling contract，不在Runtime偷偷发布。

```csharp
public sealed record ScenarioDescriptor(
    ScenarioId ScenarioId,
    HostProfile HostProfile,
    RequiredCapabilities Required,
    ProvidedCapabilities Provided,
    ReleaseEvidence Release,
    ulong Seed,
    TickCount TickCount,
    ConfigArtifactReference Config,
    FaultProfile Faults,
    WorkloadDescriptor Workload,
    TestResourceBudget Budget);

public interface IScenarioRunner
{
    ValueTask<ScenarioResult> RunAsync(
        ScenarioDescriptor scenario,
        CancellationToken cancellationToken);
}

public interface IReplayRunner
{
    ReplayResult Replay(in ReplayInput input);
}

// Test-only implementation of production generated contract.
internal sealed class ReferenceVoxelPort : IGeneratedVoxelWorldPort,
    IGeneratedVoxelReplicaPort,
    IGeneratedVoxelSnapshotPort
{
    // Members are generated; test adapter may not add bypass methods to production consumers.
}
```

### 4. 与成熟方案怎么接

| 能力 | 采用 | 不采用 | Adapter | 稳定 API 可见第三方类型 | 许可证 | 风险/控制 |
|---|---|---|---|---|---|---|
| 测试runner | `dotnet test` + xUnit v3 | 自研runner；NUnit并行引入第二stack | xUnit test projects | 不适用（test-only） | Apache-2.0 | Unity-specific tests可用Unity Test Framework独立Adapter，但不能替代核心contract tests。 |
| 覆盖率 | Microsoft Testing Platform + Coverlet | IDE-only coverage；生产instrumentation | test/build adapters | 否 | MIT | AOT/Unity覆盖需平台可用性证据。 |
| Property | CsCheck | 自研random loops；FsCheck引入F# runtime作为首选 | property test adapters | 否 | MIT | seed/shrink/replay必须保存。 |
| Fuzz | SharpFuzz + architecture corpus | 只catch exceptions；无限random payload | fuzz adapters | 否 | MIT | managed fuzzer覆盖codec/validator；native/Voxel另做differential。 |
| Benchmark | BenchmarkDotNet + explicit Workload metadata | Stopwatch microbench作为容量承诺 | Benchmark projects | 否 | MIT | Unity/NativeAOT场景另用平台runner，结果不可无metadata合并。 |
| Schema fixture | 架构源generated validator；工具侧Corvus JsonSchema可用于独立cross-check | Runtime热路径JSON Schema；JsonSchema.Net商业EULA风险 | `ContractFixtureRunner` | 否 | 项目生成物 / Corvus Apache-2.0 | cross-check不是权威validator替代。 |
| 测试double | 手写窄Reference Host/Ports与BCL bounded channels | 通用mock framework渗入domain semantics；直接mutate World | Reference adapters | 否 | 项目/BCL MIT | Reference实现必须接受differential/fidelity tests。 |

**自研最小范围。** 只自研Reference Host/Ports、Replay首差异、Scenario/Capability/Fault scheduling与Workload metadata，因为它们承载Lumio合同保真和确定性语义。测试runner、coverage、property、fuzz、benchmark、channels、schema cross-check均复用成熟工具。Reference Adapter随真实Host/Voxel演进可替换，但必须保持同Port、fixture和differential tests。

### 5. 输入 / 输出 / 依赖

**Consumes**

- 所有production模块的稳定候选公开接口与generated contracts；这是单向test dependency。
- 架构源Schema/ID/fixtures与baseline metadata。
- 显式Scenario、Config artifact、Host Profile/Capability、Seed、Fault Profile、Workload。
- 真实或Reference Voxel/Native/Storage/Envelope/Observability/HotReload Host Ports。

**Produces**

- 给CI/开发者：ScenarioResult、ReplayResult、FirstDifferenceReport、FixtureResult、WorkloadResult、machine-readable exit。
- 给架构/RT-D评审：Golden/Property/Fuzz/Differential/Benchmark/Soak证据及完整metadata。
- 给FailureBundle工具：最小replay input、hash timeline、artifact refs和cleanup result。
- 不向production Runtime产生任何运行时依赖或状态。

**编译依赖**

- 允许：所有11个production逻辑模块、generated contracts、测试/Benchmark/Fuzz packages。
- ReferenceHost/Test projects引用production assemblies；production projects的ProjectReference/PackageReference图不得反向。
- 可连接真实Host/Voxel Adapter做Differential，但其失败需分类为product failure或test infrastructure failure。

**禁止依赖**

- 任何production module反向依赖 `Lumio.GameRuntime.Testing`/`ReferenceHost`。
- 生产Secret、user data、endpoint或未脱敏bundle。
- 绕过production Envelope/Serializer/permission/size/queue/Port的shortcut。
- 把Reference storage/clock/transport/Voxel作为production package发布。
- 缺metadata的benchmark阈值写进Config/公共Schema。

### 6. 队列、预算、线程

| 实现对象 | 元素/资源 | 容量/预算来源 | 满载/超限动作 | 顺序/幂等键 | Metric |
|---|---|---|---|---|---|
| Reference `IngressQueue` | 真实typed envelope/input | `IngressQueueCapacity`、`IngressQueueBytes` from scenario/config | 使用同production full action；fault profile可确定注入full | CommandId/input sequence/envelope sequence | 同production metrics + `test.fault.ingress_full_total` |
| Reference `NativeCompletionQueue` | ReferenceVoxel/native immutable completions | `NativeCompletionQueueCapacity` | 可靠completion不drop；按scenario触发Fault/timeout | JobId/Token | 同production + `test.fault.native_completion_total` |
| Reference `DiagnosticQueue` | LoggingEvent/metric/trace batches | `DiagnosticQueueCapacity` | 同production sample/drop summary；原始Replay input不走此队列 | ProducerId+EventSeq | 同production + captured summary |
| Reference Durable Queues | Txn/Command/WAL records | `DurableLogQueueCapacity` / `PersistenceQueueCapacity` | 不drop；full停止authority ingress/maintenance；fault schedule可crash at receipt boundary | IdempotencyKey/RecordSeq | 同production durable metrics |
| Fault Schedule | immutable `FaultEvent` ordered by Tick/Phase/Target/Sequence | Scenario `MaxFaultEvents`/memory budget | 超限Scenario在Prepared拒绝或InfrastructureFault，不截断 | ScenarioId+FaultEventSeq | `test.fault.events_applied`、`.rejected_total` |

- 默认Reference Host单Owner Thread，Stress/Soak worker仍不能写authority World；所有completion走真实barrier queue。
- Fault注入只作用Port/queue/time trigger，不直接改World或绕过state machine。
- Replay每次创建全新World/Scope/Ports并使用同seed/input；线程调度差异不进authority hash。
- Test timeout/host wall time仅治理runner；Logical Tick/DeadlineTick仍走production semantics。

### 7. 失败与恢复

| 分类 | 触发例 | 返回/可观察结果 | World/Session | Failure Bundle | Journal/证据查询 |
|---|---|---|---|---|---|
| 可拒绝 | Required/Provided Capability不匹配、Release/Schema/Manifest hash不符 | ScenarioResult Rejected，状态不进入Running | 无production World或已清理 | 通常否；记录fixture/scenario evidence | 查询CapabilityMatcher/ReleaseEvidence |
| 可拒绝 | fixture schema错误或expected outcome与registry不一致 | FixtureResult Rejected/Failed | 不Fault production | 是测试artifact，不是production bundle | 查询fixture path/hash/generated validator result |
| 可拒绝 | scenario/workload预算不足或缺必需metadata | Rejected `InvalidEvidence` | 不启动或安全清理 | 否 | 查询descriptor/budget/metadata validation |
| 可重试 | 真实Native/IO/平台test dependency暂不可用 | InfrastructureRetryable；保持同seed/input/version | production World已清理 | 必要时infra bundle | 查询adapter availability/platform details |
| 可重试 | runner取消/timeout前可安全重启同scenario | Cancelled/TimedOut；原始输入保存 | 全新session重试，不能复用partial World | 保存replay artifact | 查询scenario state/cleanup result |
| 可重试 | artifact sink暂时失败但内存/本地immutable result仍在budget内 | Retryable export，测试结论不改 | 不影响production | 否/导出失败证据 | 查询artifact hash/export idempotency |
| 可致命 | Reference Host违反production invariant或绕过pipeline | InfrastructureFault，测试结果无效 | 销毁全部World/Scope/Handle | 是测试infra bundle | 查询composition/dependency/fidelity guard |
| 可致命 | Replay/Canonical hash同输入不稳定或FirstDifference算法自相矛盾 | InfrastructureFault | 不把结果归咎production | 是 | 查询重复replay timelines/seeds/platform |
| 可致命 | cleanup泄漏World/Scope/Native handle/temp resource | 保留原failure并追加CleanupFailure/InfrastructureFault | 测试进程隔离/终止，不能继续污染后续test | 是 | 查询resource registry/root validation/temp dir report |

### 8. 测试面

**单元（testing模块自身）**

- Scenario exact lifecycle、Capability matcher、fault schedule canonical order、cleanup failure不覆盖original failure。
- Replay input hash/slicing、FirstDifference Tick/Phase/Processor定位、StateHasher排除非权威字段。
- ProductionDependencyGuard项目/assembly graph正反例。

**Golden / Contract**

- 架构源全部登记valid/invalid Runtime fixtures运行，registry遗漏/expected mismatch失败。
- Foundation单World输入 -> 13 phase trace -> hash golden；重复run字节级相同。

**Property / Fuzz**

- 同seed/input/fault schedule产生同事件序列；shrink后仍可replay。
- Envelope/codec/config/snapshot随机输入只经真实validator/queue；不出现unbounded allocation。

**Differential / Fault**

- ReferenceVoxelPort与真实generated Voxel Adapter对query/prepare/commit/abort/capture/restore结果/hash比较。
- PureHeadless、NativeHeadless、LocalEmbedded、LocalSplitProcess在同contract input下比较状态/复制/恢复结果。

**Stress / Soak / Benchmark**

- 1/10/25/50/100/150/200 Bot workload记录Tick p50/p95/p99/max、CPU/RSS/GC/native heap/queues/bytes/FFI/log/durable latency。
- HotReload 100-cycle、Replication loss/reorder/reconnect、Persistence crash-at-boundary、Txn lost-result矩阵。

**生产隔离**

- Release project graph/assembly metadata不含Testing/ReferenceHost/test packages；production binary字符串/符号无fault backdoor/fixed seed。

### 9. 本模块任务拆解

#### `test-reference-host-shell`

- **一句话目标**：建立test-only Reference Host/Session，组装真实production modules并跑单Owner Thread run_tick。
- **涉及文件集**：
  - `modules/testing/src/Lumio.GameRuntime.Testing/Lumio.GameRuntime.Testing.csproj`
  - `modules/testing/src/Lumio.GameRuntime.ReferenceHost/Lumio.GameRuntime.ReferenceHost.csproj`
  - `modules/testing/tests/Lumio.GameRuntime.Testing.Tests/Lumio.GameRuntime.Testing.Tests.csproj`
  - `modules/testing/src/Lumio.GameRuntime.Testing/TestingModule.cs`
  - `modules/testing/src/Lumio.GameRuntime.ReferenceHost/ReferenceHost.cs`
  - `modules/testing/src/Lumio.GameRuntime.ReferenceHost/ReferenceHostSession.cs`
  - `modules/testing/src/Lumio.GameRuntime.ReferenceHost/ReferenceClockPort.cs`
  - `modules/testing/src/Lumio.GameRuntime.ReferenceHost/ReferenceObservabilitySink.cs`
  - `modules/testing/src/Lumio.GameRuntime.ReferenceHost/ReferenceGameplayModule.cs`
  - `modules/testing/tests/Lumio.GameRuntime.Testing.Tests/ReferenceHostFoundationSliceTests.cs`
- **验收标准**：
  - [ ] ReferenceHost通过production候选API组装，不访问module internals。
  - [ ] 单输入运行exact 13 phases并产TickResult/Revision/StateHash。
  - [ ] 重复100次同seed/input/config得到相同phase trace/hash。
  - [ ] Dispose验证World/Scope/leases/queues全部关闭；cleanup failure单列。
- **依赖**：`sim-fail-stop-and-tick-result`、`coord-crash-resolution-and-journal-port`、`obs-failure-bundle-assembly`、`cfg-tick-boundary-activation`、`test-reference-voxel-authority-port`
- **Consumes**：production module APIs、Reference Host Ports、minimal generated contracts。
- **Produces**：ReferenceHostSession、Foundation slice result/artifacts。
- **成熟方案**：xUnit v3 + hand-written narrow reference Ports。
- **明确不做**：不实现真实network/storage/ALC，不被production引用。

#### `test-reference-voxel-authority-port`

- **一句话目标**：实现 Foundation 所需的 Generated Voxel Authority prepare/commit/abort/status deterministic Reference Port。
- **涉及文件集**：
  - `modules/testing/src/Lumio.GameRuntime.ReferenceHost/ReferenceVoxelAuthorityPort.cs`
  - `modules/testing/tests/Lumio.GameRuntime.Testing.Tests/ReferenceVoxelAuthorityTxnTests.cs`
- **验收标准**：
  - [ ] `prepare` 只建立 reservation/token，不改变 visible Voxel revision 或内容。
  - [ ] `commit`、`abort`、`status` 对相同 token/TxnId 幂等，重复返回原结果或 `AlreadyApplied`。
  - [ ] 支持 lost result、crash after apply/before marker 与 participant query，且不使用 Boolean participant marker。
  - [ ] Completion 只经 `NativeCompletionQueueCapacity` 对应的 Reference completion queue 回到声明 Barrier。
  - [ ] stable/generated Port 之外的测试 helper 全为 internal，不进入 production contract。
- **依赖**：`repo-generated-contract-boundary`、`coord-prepare-and-reservation`、`coord-commit-intent-and-apply-order`
- **Consumes**：Generated Voxel Authority Contract、Txn/Revision/token types、deterministic fault schedule。
- **Produces**：`ReferenceVoxelAuthorityPort`、prepare/apply/query receipts，供 Foundation Reference Host 注入。
- **成熟方案**：手写最小 deterministic reference state + generated contract；BCL bounded Channel。
- **明确不做**：不实现 Voxel storage 性能、Replica/Prediction、Snapshot content manifest 或真实 FFI。

#### `test-reference-voxel-port`

- **一句话目标**：实现Generated Voxel authority/replica/snapshot contract的deterministic ReferenceVoxelPort及差分fixture。
- **涉及文件集**：
  - `modules/testing/src/Lumio.GameRuntime.ReferenceHost/ReferenceVoxelPort.cs`
  - `modules/testing/tests/Lumio.GameRuntime.Testing.Tests/ReferenceVoxelDifferentialTests.cs`
- **验收标准**：
  - [ ] 实现generated required members，测试专用helper仅internal且不进入production contract。
  - [ ] prepare无visible side effect，commit/abort/status/duplicate/lost result/revision exact。
  - [ ] replica apply/rollback/snapshot capture/restore幂等且可故障注入。
  - [ ] 与真实Adapter可用时运行Differential；不可用明确标Skipped-Unavailable，不宣称通过。
- **依赖**：`test-reference-host-shell`、`test-reference-voxel-authority-port`、`repl-voxel-replica-adapter`、`persist-voxel-snapshot-adapter`
- **Consumes**：Generated Voxel contracts/fixtures、fault schedule。
- **Produces**：ReferenceVoxelPort、differential results。
- **成熟方案**：手写最小reference state + generated contracts。
- **明确不做**：不复刻VoxelEngine storage/performance。

#### `test-replay-and-first-difference`

- **一句话目标**：实现ReplayInput/Runner、canonical domain hash组合与首个Tick/Phase/Processor差异报告。
- **涉及文件集**：
  - `modules/testing/src/Lumio.GameRuntime.Testing/Replay/ReplayInput.cs`
  - `modules/testing/src/Lumio.GameRuntime.Testing/Replay/ReplayRunner.cs`
  - `modules/testing/src/Lumio.GameRuntime.Testing/Replay/CanonicalStateHasher.cs`
  - `modules/testing/src/Lumio.GameRuntime.Testing/Replay/FirstDifferenceFinder.cs`
  - `modules/testing/src/Lumio.GameRuntime.Testing/Replay/ReplayResult.cs`
  - `modules/testing/tests/Lumio.GameRuntime.Testing.Tests/ReplayFirstDifferenceTests.cs`
- **验收标准**：
  - [ ] ReplayInput包含release/schema/manifest/config/mapping hashes、seed、full ordered input/fault schedule。
  - [ ] 每次Replay创建全新session且同input生成同timeline。
  - [ ] 注入单Phase差异时报告精确首Tick/Phase/Processor/domain，不受diagnostic/timing/address变化影响。
  - [ ] 失败保存最小窗口与FailureBundle reference，可再次replay。
- **依赖**：`test-reference-host-shell`、`sim-determinism-context-and-state-hash`
- **Consumes**：ReferenceHost、production StateHash providers、ReplayInput。
- **Produces**：ReplayResult/hash timeline/FirstDifferenceReport。
- **成熟方案**：BCL hashing primitive via repository canonical adapter + xUnit。
- **明确不做**：不发明公共FailureBundle字段或序列化格式。

#### `test-scenario-capability-and-faults`

- **一句话目标**：实现Scenario lifecycle、Capability匹配与确定性Port/queue fault injection。
- **涉及文件集**：
  - `modules/testing/src/Lumio.GameRuntime.Testing/Scenario/ScenarioDescriptor.cs`
  - `modules/testing/src/Lumio.GameRuntime.Testing/Scenario/TestScenarioState.cs`
  - `modules/testing/src/Lumio.GameRuntime.Testing/Scenario/ScenarioRunner.cs`
  - `modules/testing/src/Lumio.GameRuntime.Testing/Scenario/CapabilityMatcher.cs`
  - `modules/testing/src/Lumio.GameRuntime.Testing/Scenario/ScenarioResult.cs`
  - `modules/testing/src/Lumio.GameRuntime.Testing/Faults/FaultProfile.cs`
  - `modules/testing/src/Lumio.GameRuntime.Testing/Faults/FaultInjectionAdapter.cs`
  - `modules/testing/src/Lumio.GameRuntime.Testing/Faults/FaultEvent.cs`
  - `modules/testing/tests/Lumio.GameRuntime.Testing.Tests/ScenarioFaultDeterminismTests.cs`
- **验收标准**：
  - [ ] Scenario state exact，Rejected/Failed/InfrastructureFault/CleanupFailure可区分。
  - [ ] Required/Provided mismatch在Prepared拒绝，不隐式补capability。
  - [ ] 同seed/profile生成同fault event order，fault只作用Port/queue。
  - [ ] 覆盖delay/drop/reorder/duplicate/disconnect/QueueFull/crash/corruption且保存replay。
- **依赖**：`test-reference-host-shell`、`test-replay-and-first-difference`
- **Consumes**：ScenarioDescriptor、Host capability、production queue/Port adapters。
- **Produces**：ScenarioResult、deterministic fault schedule/replay artifacts。
- **成熟方案**：CsCheck + bounded Channel adapters。
- **明确不做**：不直接mutate World、不使用真实secret/endpoint。

#### `test-contract-fixture-runner`

- **一句话目标**：运行架构源全部登记Schema/ID/valid/invalid fixtures并提供Fuzz corpus入口。
- **涉及文件集**：
  - `modules/testing/src/Lumio.GameRuntime.Testing/Fixtures/ContractFixtureRunner.cs`
  - `modules/testing/src/Lumio.GameRuntime.Testing/Fixtures/FixtureResult.cs`
  - `modules/testing/tests/Lumio.GameRuntime.Testing.Tests/ContractFixtureRunnerTests.cs`
- **验收标准**：
  - [ ] registry所有Runtime相关fixtures均有result，遗漏使test失败。
  - [ ] valid必须accepted、invalid必须以期望stable category拒绝。
  - [ ] runner只读源文件/生成validator，不写回architecture。
  - [ ] codec/schema cross-check差异报告为contract mismatch，不悄悄选择一方。
- **依赖**：`repo-generated-contract-boundary`、`persist-canonical-codec`、`repl-resync-and-fault-matrix`
- **Consumes**：architecture schema/fixture registries、generated validators、codec adapters。
- **Produces**：FixtureResult matrix/fuzz seeds。
- **成熟方案**：generated validator权威 + Corvus JsonSchema test-only cross-check + SharpFuzz。
- **明确不做**：不在Runtime热路径引入JSON Schema，不修改fixtures。

#### `test-production-isolation-and-workloads`

- **一句话目标**：验证production无Testing依赖，并建立带完整metadata的Foundation/Replication/Persistence/HotReload工作负载。
- **涉及文件集**：
  - `modules/testing/src/Lumio.GameRuntime.Testing/Workloads/WorkloadDescriptor.cs`
  - `modules/testing/src/Lumio.GameRuntime.Testing/Workloads/WorkloadRunner.cs`
  - `modules/testing/src/Lumio.GameRuntime.Testing/Workloads/WorkloadResult.cs`
  - `modules/testing/src/Lumio.GameRuntime.Testing/Isolation/ProductionDependencyGuard.cs`
  - `modules/testing/tests/Lumio.GameRuntime.Testing.Tests/ProductionIsolationTests.cs`
  - `modules/testing/workloads/foundation-single-world.json`
  - `modules/testing/workloads/replication-loss-reorder.json`
  - `modules/testing/workloads/persistence-crash-boundaries.json`
  - `modules/testing/workloads/hot-reload-100-cycles.json`
- **验收标准**：
  - [ ] project/assembly graph任一production->Testing/ReferenceHost/test package边都会失败。
  - [ ] Release artifact符号/依赖扫描无fault backdoor/fixed seed/reference adapter。
  - [ ] 每个workload含platform/hardware/runtime/compiler/release/schema/config/seed/ticks/shapes/budgets。
  - [ ] 结果保留raw samples和InvalidEvidence状态；不把未经测量数字写成capacity承诺。
- **依赖**：`test-scenario-capability-and-faults`、`test-contract-fixture-runner`、`reload-root-validation-soak`、`persist-recovery-replay`
- **Consumes**：project graph/assemblies、Scenario runner、BenchmarkDotNet、module metrics。
- **Produces**：isolation guard result、workload raw/summary evidence、RT-D reports。
- **成熟方案**：dotnet test/xUnit v3/Coverlet/BenchmarkDotNet/CycloneDX or Microsoft SBOM at build layer。
- **明确不做**：不批准RT-D阈值、不发布ReferenceHost为production包。



## 4. 仓库级前置任务卡

这些卡只建立构建、生成契约、供应链和依赖图验证面，不实现任何 Runtime 领域功能，也不替代各模块首卡中的项目脚手架。执行时可按 `.spec/tasks/README.md` 落成单卡文件；本文仅保存可派工内容，不在当前设计阶段写入过程卡目录。

#### `repo-dotnet-baseline`

- **一句话目标**：固定 .NET 10 LTS、C#、nullable、deterministic build、analyzer 和多目标编译的仓库级基线。
- **涉及文件集**：
  - `global.json`
  - `Directory.Build.props`
  - `Directory.Build.targets`
  - `.editorconfig`
  - `eng/verify-sdk.sh`
  - `eng/verify-sdk.ps1`
- **验收标准**：
  - [ ] `global.json` 精确固定 `10.0.11`，roll-forward 策略显式且 CI/本地命令一致。
  - [ ] nullable、warnings-as-errors、deterministic、continuous integration build 和 analyzer 规则对所有生产/测试项目生效。
  - [ ] 生产项目模板允许 `net10.0;netstandard2.1`；测试/Benchmark/Fuzz 只目标 `net10.0`。
  - [ ] 验证脚本在错误 SDK、nullable 警告和非确定构建属性下返回非零退出码。
- **依赖**：无
- **Consumes**：仓库标准、目标平台与 `.spec` 代码风格约束。
- **Produces**：统一 SDK/build/analyzer 约束，供所有模块项目消费。
- **成熟方案**：官方 .NET SDK/MSBuild/Roslyn analyzers。
- **明确不做**：不创建模块实现、不决定 RT-D-001 的最终程序集合并方案、不引入业务包。

#### `repo-supply-chain-policy`

- **一句话目标**：建立中央包版本、locked restore、许可证/SBOM/漏洞审计和 Adapter-only 第三方依赖准入。
- **涉及文件集**：
  - `Directory.Packages.props`
  - `NuGet.config`
  - `eng/dependency-policy.json`
  - `eng/verify-dependencies.sh`
  - `eng/verify-dependencies.ps1`
  - `eng/generate-sbom.sh`
  - `eng/generate-sbom.ps1`
  - `THIRD_PARTY_NOTICES.md`
- **验收标准**：
  - [ ] 所有 NuGet 版本由中央文件精确固定；项目中出现浮动版本或重复显式版本时验证失败。
  - [ ] restore 生成并校验 `packages.lock.json`；locked restore 变更必须显式更新锁文件。
  - [ ] 验证脚本拒绝 GPL/AGPL 等未获法务批准许可证，并输出 transitive package、license、hash 和 vulnerability evidence。
  - [ ] CycloneDX 或 Microsoft SBOM 工具只在构建/发布层运行，不进入 production assembly 依赖图。
  - [ ] 依赖策略检查第三方类型只能出现在 Adapter/internal 实现，稳定候选 public surface 泄漏时失败。
- **依赖**：`repo-dotnet-baseline`
- **Consumes**：本文 §2 选型、许可证策略和未来项目 package graph。
- **Produces**：中央 package/lock/SBOM/license/vulnerability policy 与机器可读验证结果。
- **成熟方案**：NuGet Central Package Management、locked restore、CycloneDX .NET 或 Microsoft SBOM tool。
- **明确不做**：不批准具体 ECS/codec 决策门，不自动升级包，不把 SBOM 工具打入 Runtime。

#### `repo-generated-contract-boundary`

- **一句话目标**：建立架构源生成契约的只读导入边界、manifest/hash 验证与禁止手改规则。
- **涉及文件集**：
  - `src/Lumio.GameRuntime.GeneratedContracts/Lumio.GameRuntime.GeneratedContracts.csproj`
  - `src/Lumio.GameRuntime.GeneratedContracts/GeneratedContractManifest.cs`
  - `src/Lumio.GameRuntime.GeneratedContracts/README.md`
  - `eng/generate-contracts.sh`
  - `eng/generate-contracts.ps1`
  - `eng/verify-generated-contracts.sh`
  - `eng/verify-generated-contracts.ps1`
  - `tests/Lumio.GameRuntime.GeneratedContracts.Tests/Lumio.GameRuntime.GeneratedContracts.Tests.csproj`
  - `tests/Lumio.GameRuntime.GeneratedContracts.Tests/GeneratedContractBaselineTests.cs`
- **验收标准**：
  - [ ] 生成命令只调用架构源已发布工具链；本仓不重新实现 Schema/ID compiler。
  - [ ] Manifest 记录 `ArchitectureBaselineId`、架构源 commit/hash、Schema/ID/Fixture registry hash 和生成器版本。
  - [ ] 生成目录手工修改、未登记生成物、baseline/hash不匹配或缺少生成器时验证失败；缺架构源 checkout 时返回明确环境错误。
  - [ ] generated assembly 不引用任何 Runtime 业务模块，允许所有生产模块单向消费。
  - [ ] 测试证明 `TxnJournalRecord`、`CommandLogRecord`、`WalRecordEnvelope`、Voxel Authority/Replica/Snapshot、Replication、Config、Logging/Failure contracts 均可从 manifest 定位。
- **依赖**：`repo-dotnet-baseline`、`repo-supply-chain-policy`
- **Consumes**：`LumioGameEngineArchitecture` 的 contract toolchain、Schema/ID/Fixture registry 与 baseline metadata。
- **Produces**：中立 generated-contract assembly、生成/验证命令和只读 baseline evidence。
- **成熟方案**：既有 Contract Toolchain + 官方 MSBuild；不自研第二套生成器。
- **明确不做**：不修改架构源、不新增字段/错误码/枚举、不把模块 README 候选接口生成成公共 ABI。

#### `repo-solution-graph-and-architecture-tests`

- **一句话目标**：在各模块首卡建立项目后组装 solution，并用机器测试锁定生产 DAG、test-only 方向和第三方 API 隔离。
- **涉及文件集**：
  - `Lumio.GameRuntime.slnx`
  - `tests/Lumio.GameRuntime.Architecture.Tests/Lumio.GameRuntime.Architecture.Tests.csproj`
  - `tests/Lumio.GameRuntime.Architecture.Tests/ProjectDependencyGraphTests.cs`
  - `tests/Lumio.GameRuntime.Architecture.Tests/PublicSurfaceIsolationTests.cs`
  - `tests/Lumio.GameRuntime.Architecture.Tests/GeneratedContractDirectionTests.cs`
  - `tests/Lumio.GameRuntime.Architecture.Tests/TestingIsolationTests.cs`
  - `eng/verify-project-graph.sh`
  - `eng/verify-project-graph.ps1`
- **验收标准**：
  - [ ] solution 包含所有已建立 production/test/benchmark/reference projects，build order 与 `modules/README.md` DAG 一致。
  - [ ] 任何 production -> `testing`/`ReferenceHost`、`coordination -> persistence`、业务模块 -> Host implementation 或 Voxel internal edge 均使测试失败。
  - [ ] `replication` 的 confirmed command sequence 只来自中立 generated contract，不因逻辑消费增加 `replication -> command` assembly edge。
  - [ ] public surface 扫描拒绝 `ILogger`、OpenTelemetry、MessagePack、Channel、Friflo、Arch、FileStream、AssemblyLoadContext 等第三方/Host类型泄漏。
  - [ ] `dotnet build`、`dotnet test` 与项目图验证均产生机器可读结果；该卡不把任一 RT-D 标记 approved。
- **依赖**：`repo-generated-contract-boundary`、`obs-event-ports-and-context`、`cfg-generated-table-validation`、`ecs-world-and-entity-identity`、`cmd-buffer-and-deferred-token`、`sim-session-and-run-tick-entry`、`coord-revision-vector-view`、`repl-mapping-registry-and-identity`、`gas-type-handle-registry`、`persist-canonical-codec`、`reload-scope-and-resource-leases`、`test-reference-host-shell`
- **Consumes**：所有项目文件、generated manifest 与本文编译 DAG。
- **Produces**：solution、architecture tests、dependency/public-surface guard。
- **成熟方案**：MSBuild project graph、reflection/metadata inspection 和 xUnit v3；不引入通用 DI/architecture framework作为稳定依赖。
- **明确不做**：不合并/拆分逻辑模块、不批准 RT-D-001、不修改模块源码来迁就错误依赖。


## 5. 模块间接口总表

| Produce 方 | Produce 类型/语义 | Consume 方 | Port/契约归属 | 稳定面可见 | 环与泄漏防线 |
|---|---|---|---|---|---|
| Architecture generated contracts | Schema/ID/enum/record/Voxel/Replication/Config/Logging/Failure 类型与 validator | 全部生产模块、testing | 架构源 Contract Toolchain；本仓 generated assembly 只读 | 无；唯一公共事实源 | 不得由 README/C# 手写复制；manifest/hash gate |
| observability | `IRuntimeEventPort`、`IMetricPort`、`ITracePort`、`IFailureBundlePort`、`IDurableEvidencePort` | 全部模块；Host sink；testing | observability（Port）；durable storage implementation 在 persistence/Host | generated event/failure view + Runtime result | MEL/OTel/Channel 只在 Adapter，Diagnostic 不替代 durable record |
| config | `ConfigSnapshot`、`IConfigSnapshotProvider`、typed table/row/value readers、activation result | ecs/command/simulation/coordination/replication/gas/persistence/hot-reload | config | 不可变 Runtime view；generated table identity | 无 `compile` API；六层顺序固定，Dev 也走 validate/stage/activate |
| ecs | `LocalEntityId`、Generation、`EcsReadView`、`EcsWriteView`、`ChangeSet`、`IEcsSnapshotProvider`、`IWorldStorageAdapter` | command/simulation/coordination/replication/gas/persistence | ecs；storage adapter interface 归 ecs | World-local Runtime types | 第三方 Entity/World/Query 不可见；字段写失败 Fail-stop |
| command | `ICommandBufferFactory`、`ProcessorCommandBuffer`、`DeferredEntityToken`、`MergedCommandBatch`、`PreparedGameDelta`、`ICommandApplyPort` | simulation/coordination/gas；replication只消费中立 confirmed sequence | command；confirmed sequence contract归中立 generated assembly | Runtime command types + generated command identity | 每 Processor 一 Buffer；Open→Sealed→Merged→Prepared→Applied；Prepared 后不拒绝 |
| simulation | `IRuntimeSession`/单一 `RunTick`、`TickExecutionContext`、`DeterminismContext`、`ProcessorPlan`、`IScopeSwitchPort` implementation | Host、hot-reload、testing | simulation；Host只触发logical tick | Runtime immutable inputs/results | 唯一13相编排与 `GasAndEventFinalize` commit point；不拥有wall clock/revision |
| coordination | `ICrossWorldCoordinator`、`ICoordinationReadPort`、`SessionRevisionVectorView`、`PreparedGameDelta`消费面、`ITxnJournalPort`、`SnapshotCut` | simulation/replication/persistence/testing；persistence实现journal port | coordination（Port与状态）；persistence仅实现介质Adapter | generated txn/revision/token + Runtime view | 固定 durable CommitIntent→Voxel→ECS；participant四态，Indeterminate只在intent后 |
| Generated Voxel Authority Contract | prepare/commit/abort/status/revision/reservation token | coordination、ReferenceVoxelAuthorityPort | LumioGameEngineArchitecture/Voxel contract publisher | generated types only | 不暴露Voxel storage；completion经barrier |
| replication | `IReplicationRuntime`、`ReplicationContext` handle、Mapping/History views、`IEcsReplicationView`、`IGasReplicationView`、`IVoxelReplicaPortAdapter`、`PresentationDiff` | simulation/Host/persistence/testing | replication；Voxel concrete adapter在replication adapter assembly | generated envelope/mapping/identity + Runtime results | Client六步Apply；LocalEmbedded仍走完整pipeline；command sequence中立化 |
| gas | `IGasRuntime`、Type/Instance/Handle、`IGameplayFormulaPort`、`IGasSnapshotProvider`、`PredictionFrame`、ECS projection port | simulation/replication/persistence/hot-reload/testing；Game实现formula/migration hook | gas（Port）；Game只实现纯Hook | generated TypeId/Prediction + Runtime opaque handle | ECS为唯一权威属性/效果投影，不建立第二storage |
| persistence | `IPersistenceRuntime`、`ICanonicalCodec` internal port、`ISnapshotStoragePort`、`IDurableRecordStoragePort`、`IRecoveryApplyPort`、coordination journal实现 | simulation/coordination/Host/testing | persistence；Host拥有具体介质 | generated snapshot/record + Runtime lease/result | Canonical语义不由MessagePack/Brotli决定；Staging/Verify/Activate分离 |
| Generated Voxel Replica/Snapshot Contracts | replica capture/restore/revision/mutation token；snapshot capture/restore/content refs | replication/persistence/testing | 架构源/Voxel contract publisher | generated only | 不暴露Chunk内部；适配器分别归调用模块 |
| hot-reload | `IHotReloadRuntime`、`GameplayModuleScope`/Lease、`IGameplayMigrationPort`、`IRootValidationPort`、`IHostUnloadPort`、调用simulation的scope switch port | Host/simulation/gas/Game/testing | hot-reload；ALC/CoreCLR implementation归Host | Runtime opaque scope/report/result + generated release refs | 六步不可跳；pre-switch KeepOld，post-switch Fault+Snapshot/Release recover |
| testing | `ScenarioDescriptor`、Reference Host/Ports、Replay/Hash、Fault/Fixture/Workload results | CI/开发者工具；不被production消费 | testing | test-only types/artifacts | 生产依赖隔离；Reference Adapter不得旁路production contract |

## 6. 全仓任务总表与波次

### 6.1 任务总表

| slug | 模块 | 阶段 | 依赖 | 文件集 | Wave | 可否并行 |
|---|---|---|---|---|---|---|
| `obs-event-ports-and-context` | observability | Foundation | `repo-generated-contract-boundary` | `modules/observability/src/Lumio.GameRuntime.Observability/Lumio.GameRuntime.Observability.csproj`<br>`modules/observability/tests/Lumio.GameRuntime.Observability.Tests/Lumio.GameRuntime.Observability.Tests.csproj`<br>`modules/observability/src/Lumio.GameRuntime.Observability/ObservabilityModule.cs`<br>`modules/observability/src/Lumio.GameRuntime.Observability/ObservabilityServices.cs`<br>`modules/observability/src/Lumio.GameRuntime.Observability/Contracts/IRuntimeEventPort.cs`<br>`modules/observability/src/Lumio.GameRuntime.Observability/Contracts/IMetricPort.cs`<br>`modules/observability/src/Lumio.GameRuntime.Observability/Contracts/ITracePort.cs`<br>`modules/observability/src/Lumio.GameRuntime.Observability/Context/ProducerSequence.cs`<br>`modules/observability/tests/Lumio.GameRuntime.Observability.Tests/ProducerSequenceTests.cs` | Wave 3 | 该 Wave 单卡 |
| `obs-bounded-diagnostic-routing` | observability | Foundation | `obs-event-ports-and-context` | `modules/observability/src/Lumio.GameRuntime.Observability/Queues/DiagnosticEventQueue.cs`<br>`modules/observability/src/Lumio.GameRuntime.Observability/Queues/DiagnosticQueueBudget.cs`<br>`modules/observability/src/Lumio.GameRuntime.Observability/Routing/EventRouter.cs`<br>`modules/observability/src/Lumio.GameRuntime.Observability.Adapters/BoundedChannelAdapter.cs`<br>`modules/observability/tests/Lumio.GameRuntime.Observability.Tests/DiagnosticBackpressureTests.cs` | Wave 4 | 是：同 Wave 其他卡；依赖已完成且文件无重叠 |
| `obs-durable-route-and-emergency-path` | observability | Foundation | `obs-event-ports-and-context` | `modules/observability/src/Lumio.GameRuntime.Observability/Contracts/IDurableEvidencePort.cs`<br>`modules/observability/src/Lumio.GameRuntime.Observability/Routing/DurableEvidenceRouter.cs`<br>`modules/observability/src/Lumio.GameRuntime.Observability/Errors/ObservabilityFailure.cs`<br>`modules/observability/tests/Lumio.GameRuntime.Observability.Tests/DurableRouteFailureTests.cs` | Wave 4 | 是：同 Wave 其他卡；依赖已完成且文件无重叠 |
| `obs-failure-bundle-assembly` | observability | Foundation | `obs-event-ports-and-context`<br>`obs-durable-route-and-emergency-path` | `modules/observability/src/Lumio.GameRuntime.Observability/Failure/FailureBundleAssembler.cs`<br>`modules/observability/src/Lumio.GameRuntime.Observability/Failure/FailureContextSnapshot.cs`<br>`modules/observability/tests/Lumio.GameRuntime.Observability.Tests/FailureBundleGoldenTests.cs` | Wave 5 | 是：同 Wave 其他卡；依赖已完成且文件无重叠 |
| `obs-otel-and-microsoft-logging-adapters` | observability | Vertical Slice | `obs-event-ports-and-context`<br>`obs-bounded-diagnostic-routing` | `modules/observability/src/Lumio.GameRuntime.Observability.Adapters/Lumio.GameRuntime.Observability.Adapters.csproj`<br>`modules/observability/src/Lumio.GameRuntime.Observability.Adapters/MicrosoftLoggingAdapter.cs`<br>`modules/observability/src/Lumio.GameRuntime.Observability.Adapters/OpenTelemetryMetricsAdapter.cs`<br>`modules/observability/src/Lumio.GameRuntime.Observability.Adapters/OpenTelemetryTraceAdapter.cs`<br>`modules/observability/tests/Lumio.GameRuntime.Observability.Tests/AdapterBoundaryTests.cs` | Wave 5 | 是：同 Wave 其他卡；依赖已完成且文件无重叠 |
| `cfg-generated-table-validation` | config | Foundation | `obs-event-ports-and-context` | `modules/config/src/Lumio.GameRuntime.Config/Lumio.GameRuntime.Config.csproj`<br>`modules/config/tests/Lumio.GameRuntime.Config.Tests/Lumio.GameRuntime.Config.Tests.csproj`<br>`modules/config/src/Lumio.GameRuntime.Config/Contracts/IGeneratedConfigArtifactPort.cs`<br>`modules/config/src/Lumio.GameRuntime.Config/Validation/GeneratedConfigValidator.cs`<br>`modules/config/src/Lumio.GameRuntime.Config/Validation/ConfigValidationReport.cs`<br>`modules/config/tests/Lumio.GameRuntime.Config.Tests/GeneratedArtifactValidationTests.cs` | Wave 4 | 是：同 Wave 其他卡；依赖已完成且文件无重叠 |
| `cfg-six-layer-merge` | config | Foundation | `cfg-generated-table-validation` | `modules/config/src/Lumio.GameRuntime.Config/Merge/ConfigLayer.cs`<br>`modules/config/src/Lumio.GameRuntime.Config/Merge/ConfigLayerMerger.cs`<br>`modules/config/tests/Lumio.GameRuntime.Config.Tests/SixLayerMergeGoldenTests.cs` | Wave 5 | 是：同 Wave 其他卡；依赖已完成且文件无重叠 |
| `cfg-immutable-snapshot-reader` | config | Foundation | `cfg-six-layer-merge` | `modules/config/src/Lumio.GameRuntime.Config/Snapshot/ConfigSnapshot.cs`<br>`modules/config/src/Lumio.GameRuntime.Config/Snapshot/ConfigSnapshotLease.cs`<br>`modules/config/src/Lumio.GameRuntime.Config/Snapshot/ConfigTableReader.cs`<br>`modules/config/src/Lumio.GameRuntime.Config/Contracts/IConfigSnapshotView.cs`<br>`modules/config/tests/Lumio.GameRuntime.Config.Tests/SnapshotReaderPropertyTests.cs` | Wave 6 | 该 Wave 单卡 |
| `cfg-tick-boundary-activation` | config | Foundation | `cfg-immutable-snapshot-reader` | `modules/config/src/Lumio.GameRuntime.Config/ConfigModule.cs`<br>`modules/config/src/Lumio.GameRuntime.Config/ConfigServices.cs`<br>`modules/config/src/Lumio.GameRuntime.Config/Activation/ConfigActivationSlot.cs`<br>`modules/config/src/Lumio.GameRuntime.Config/Activation/ConfigActivator.cs`<br>`modules/config/tests/Lumio.GameRuntime.Config.Tests/TickActivationTests.cs` | Wave 7 | 是：同 Wave 其他卡；依赖已完成且文件无重叠 |
| `cfg-dev-capability-adapter` | config | Vertical Slice | `cfg-generated-table-validation`<br>`cfg-tick-boundary-activation` | `modules/config/src/Lumio.GameRuntime.Config.DevAdapters/DevGeneratedArtifactAdapter.cs`<br>`modules/config/tests/Lumio.GameRuntime.Config.Tests/DevAdapterFidelityTests.cs` | Wave 8 | 是：同 Wave 其他卡；依赖已完成且文件无重叠 |
| `ecs-world-and-entity-identity` | ecs | Foundation | `cfg-immutable-snapshot-reader`<br>`obs-event-ports-and-context` | `modules/ecs/src/Lumio.GameRuntime.Ecs/Lumio.GameRuntime.Ecs.csproj`<br>`modules/ecs/tests/Lumio.GameRuntime.Ecs.Tests/Lumio.GameRuntime.Ecs.Tests.csproj`<br>`modules/ecs/src/Lumio.GameRuntime.Ecs/EcsModule.cs`<br>`modules/ecs/src/Lumio.GameRuntime.Ecs/EcsServices.cs`<br>`modules/ecs/src/Lumio.GameRuntime.Ecs/World/EcsWorld.cs`<br>`modules/ecs/src/Lumio.GameRuntime.Ecs/World/EcsWorldState.cs`<br>`modules/ecs/src/Lumio.GameRuntime.Ecs/Entity/LocalEntityId.cs`<br>`modules/ecs/src/Lumio.GameRuntime.Ecs/Entity/EntitySlotTable.cs`<br>`modules/ecs/tests/Lumio.GameRuntime.Ecs.Tests/EntityGenerationPropertyTests.cs`<br>`modules/ecs/tests/Lumio.GameRuntime.Ecs.Tests/WorldIsolationTests.cs` | Wave 7 | 是：同 Wave 其他卡；依赖已完成且文件无重叠 |
| `ecs-storage-adapter-contract` | ecs | Foundation | `ecs-world-and-entity-identity` | `modules/ecs/src/Lumio.GameRuntime.Ecs/Storage/IWorldStorageAdapter.cs`<br>`modules/ecs/src/Lumio.GameRuntime.Ecs/Storage/ComponentTypeRegistry.cs`<br>`modules/ecs/src/Lumio.GameRuntime.Ecs.Adapters.Friflo/FrifloWorldStorageAdapter.cs`<br>`modules/ecs/tests/Lumio.GameRuntime.Ecs.Tests/StorageAdapterConformanceTests.cs` | Wave 8 | 是：同 Wave 其他卡；依赖已完成且文件无重叠 |
| `ecs-query-read-write-views` | ecs | Foundation | `ecs-storage-adapter-contract` | `modules/ecs/src/Lumio.GameRuntime.Ecs/Query/QuerySpec.cs`<br>`modules/ecs/src/Lumio.GameRuntime.Ecs/Query/QueryPlan.cs`<br>`modules/ecs/src/Lumio.GameRuntime.Ecs/Query/QueryBatch.cs`<br>`modules/ecs/src/Lumio.GameRuntime.Ecs/Query/EcsReadView.cs`<br>`modules/ecs/src/Lumio.GameRuntime.Ecs/Query/EcsWriteView.cs`<br>`modules/ecs/tests/Lumio.GameRuntime.Ecs.Tests/QueryViewBoundaryTests.cs` | Wave 9 | 是：同 Wave 其他卡；依赖已完成且文件无重叠 |
| `ecs-change-set-and-snapshot-view` | ecs | Foundation | `ecs-query-read-write-views` | `modules/ecs/src/Lumio.GameRuntime.Ecs/ChangeTracking/ChangeSet.cs`<br>`modules/ecs/src/Lumio.GameRuntime.Ecs/ChangeTracking/ChangeSetBuilder.cs`<br>`modules/ecs/src/Lumio.GameRuntime.Ecs/Snapshot/IEcsSnapshotProvider.cs`<br>`modules/ecs/src/Lumio.GameRuntime.Ecs/Snapshot/EcsWorldReadSnapshot.cs`<br>`modules/ecs/tests/Lumio.GameRuntime.Ecs.Tests/ChangeSetGoldenTests.cs` | Wave 10 | 是：同 Wave 其他卡；依赖已完成且文件无重叠 |
| `ecs-world-lifecycle-fail-stop` | ecs | Foundation | `ecs-change-set-and-snapshot-view`<br>`obs-failure-bundle-assembly` | `modules/ecs/src/Lumio.GameRuntime.Ecs/World/OwnerThreadGuard.cs`<br>`modules/ecs/src/Lumio.GameRuntime.Ecs/Errors/EcsFailure.cs`<br>`modules/ecs/tests/Lumio.GameRuntime.Ecs.Tests/FailStopWriteTests.cs` | Wave 11 | 是：同 Wave 其他卡；依赖已完成且文件无重叠 |
| `ecs-storage-candidate-benchmarks` | ecs | Hardening/RT-D evidence | `ecs-storage-adapter-contract`<br>`ecs-query-read-write-views`<br>`ecs-change-set-and-snapshot-view` | `modules/ecs/benchmarks/Lumio.GameRuntime.Ecs.Benchmarks/Lumio.GameRuntime.Ecs.Benchmarks.csproj`<br>`modules/ecs/benchmarks/Lumio.GameRuntime.Ecs.Benchmarks/ArchWorldStorageBenchmarkAdapter.cs`<br>`modules/ecs/benchmarks/Lumio.GameRuntime.Ecs.Benchmarks/EcsWorkloads.cs`<br>`modules/ecs/benchmarks/Lumio.GameRuntime.Ecs.Benchmarks/EcsStorageBenchmarks.cs`<br>`modules/ecs/benchmarks/Lumio.GameRuntime.Ecs.Benchmarks/README.md` | Wave 11 | 是：同 Wave 其他卡；依赖已完成且文件无重叠 |
| `cmd-buffer-and-deferred-token` | command | Foundation | `ecs-world-and-entity-identity`<br>`obs-event-ports-and-context` | `modules/command/src/Lumio.GameRuntime.Command/Lumio.GameRuntime.Command.csproj`<br>`modules/command/tests/Lumio.GameRuntime.Command.Tests/Lumio.GameRuntime.Command.Tests.csproj`<br>`modules/command/src/Lumio.GameRuntime.Command/CommandModule.cs`<br>`modules/command/src/Lumio.GameRuntime.Command/CommandServices.cs`<br>`modules/command/src/Lumio.GameRuntime.Command/Lifecycle/CommandBufferState.cs`<br>`modules/command/src/Lumio.GameRuntime.Command/Buffers/ProcessorCommandBuffer.cs`<br>`modules/command/src/Lumio.GameRuntime.Command/Buffers/CommandBufferWriter.cs`<br>`modules/command/src/Lumio.GameRuntime.Command/Tokens/DeferredEntityToken.cs`<br>`modules/command/tests/Lumio.GameRuntime.Command.Tests/BufferStateMachineTests.cs`<br>`modules/command/tests/Lumio.GameRuntime.Command.Tests/DeferredTokenGoldenTests.cs` | Wave 8 | 是：同 Wave 其他卡；依赖已完成且文件无重叠 |
| `cmd-seal-and-stable-merge` | command | Foundation | `cmd-buffer-and-deferred-token` | `modules/command/src/Lumio.GameRuntime.Command/Commands/CommandSortKey.cs`<br>`modules/command/src/Lumio.GameRuntime.Command/Buffers/SealedCommandBuffer.cs`<br>`modules/command/src/Lumio.GameRuntime.Command/Merge/CommandBufferMerger.cs`<br>`modules/command/src/Lumio.GameRuntime.Command/Merge/MergedCommandBatch.cs`<br>`modules/command/tests/Lumio.GameRuntime.Command.Tests/StableMergePropertyTests.cs` | Wave 9 | 是：同 Wave 其他卡；依赖已完成且文件无重叠 |
| `cmd-preflight-and-prepared-delta` | command | Foundation | `cmd-seal-and-stable-merge`<br>`ecs-query-read-write-views` | `modules/command/src/Lumio.GameRuntime.Command/Prepare/CommandPreflightValidator.cs`<br>`modules/command/src/Lumio.GameRuntime.Command/Prepare/CommandReservationSet.cs`<br>`modules/command/src/Lumio.GameRuntime.Command/Prepare/PreparedGameDelta.cs`<br>`modules/command/tests/Lumio.GameRuntime.Command.Tests/PreparedBoundaryTests.cs` | Wave 10 | 是：同 Wave 其他卡；依赖已完成且文件无重叠 |
| `cmd-apply-to-ecs` | command | Foundation | `cmd-preflight-and-prepared-delta`<br>`ecs-change-set-and-snapshot-view`<br>`ecs-world-lifecycle-fail-stop` | `modules/command/src/Lumio.GameRuntime.Command/Apply/EcsCommandCommitExecutor.cs`<br>`modules/command/src/Lumio.GameRuntime.Command/Apply/CommandApplyReceipt.cs`<br>`modules/command/src/Lumio.GameRuntime.Command/Tokens/DeferredEntityMap.cs`<br>`modules/command/tests/Lumio.GameRuntime.Command.Tests/EcsApplyFaultTests.cs` | Wave 12 | 是：同 Wave 其他卡；依赖已完成且文件无重叠 |
| `cmd-capacity-and-durable-record-route` | command | Foundation | `cmd-buffer-and-deferred-token`<br>`obs-durable-route-and-emergency-path` | `modules/command/src/Lumio.GameRuntime.Command/Budgets/CommandBufferBudget.cs`<br>`modules/command/src/Lumio.GameRuntime.Command/Evidence/ICommandEvidencePort.cs`<br>`modules/command/src/Lumio.GameRuntime.Command/Errors/CommandFailure.cs`<br>`modules/command/tests/Lumio.GameRuntime.Command.Tests/CommandBudgetTests.cs` | Wave 9 | 是：同 Wave 其他卡；依赖已完成且文件无重叠 |
| `cmd-conflict-golden-property` | command | Foundation | `cmd-seal-and-stable-merge`<br>`cmd-preflight-and-prepared-delta`<br>`cmd-apply-to-ecs` | `modules/command/tests/Lumio.GameRuntime.Command.Tests/CommandConflictGoldenTests.cs`<br>`modules/command/tests/Lumio.GameRuntime.Command.Tests/CommandReplayPropertyTests.cs`<br>`modules/command/tests/fixtures/command/README.md` | Wave 13 | 是：同 Wave 其他卡；依赖已完成且文件无重叠 |
| `sim-session-and-run-tick-entry` | simulation | Foundation | `ecs-world-lifecycle-fail-stop`<br>`cmd-buffer-and-deferred-token`<br>`cfg-tick-boundary-activation` | `modules/simulation/src/Lumio.GameRuntime.Simulation/Lumio.GameRuntime.Simulation.csproj`<br>`modules/simulation/tests/Lumio.GameRuntime.Simulation.Tests/Lumio.GameRuntime.Simulation.Tests.csproj`<br>`modules/simulation/src/Lumio.GameRuntime.Simulation/SimulationModule.cs`<br>`modules/simulation/src/Lumio.GameRuntime.Simulation/SimulationServices.cs`<br>`modules/simulation/src/Lumio.GameRuntime.Simulation/Session/SimulationSession.cs`<br>`modules/simulation/src/Lumio.GameRuntime.Simulation/Session/SimulationSessionState.cs`<br>`modules/simulation/src/Lumio.GameRuntime.Simulation/Session/SimulationOwnerThread.cs`<br>`modules/simulation/tests/Lumio.GameRuntime.Simulation.Tests/SessionLifecycleTests.cs` | Wave 12 | 是：同 Wave 其他卡；依赖已完成且文件无重叠 |
| `sim-phase-graph-13` | simulation | Foundation | `sim-session-and-run-tick-entry` | `modules/simulation/src/Lumio.GameRuntime.Simulation/Phases/TickPhase.cs`<br>`modules/simulation/src/Lumio.GameRuntime.Simulation/Phases/PhaseGraph.cs`<br>`modules/simulation/src/Lumio.GameRuntime.Simulation/Phases/PhaseContractTable.cs`<br>`modules/simulation/tests/Lumio.GameRuntime.Simulation.Tests/PhaseGraphGoldenTests.cs` | Wave 13 | 是：同 Wave 其他卡；依赖已完成且文件无重叠 |
| `sim-processor-plan-validator` | simulation | Foundation | `sim-phase-graph-13`<br>`ecs-query-read-write-views`<br>`cmd-buffer-and-deferred-token` | `modules/simulation/src/Lumio.GameRuntime.Simulation/Planning/ProcessorPlanBuilder.cs`<br>`modules/simulation/src/Lumio.GameRuntime.Simulation/Planning/ProcessorPlan.cs`<br>`modules/simulation/src/Lumio.GameRuntime.Simulation/Planning/ProcessorInvocation.cs`<br>`modules/simulation/tests/Lumio.GameRuntime.Simulation.Tests/ProcessorPlanPropertyTests.cs` | Wave 14 | 是：同 Wave 其他卡；依赖已完成且文件无重叠 |
| `sim-ingress-and-native-completion` | simulation | Foundation | `sim-session-and-run-tick-entry`<br>`obs-bounded-diagnostic-routing` | `modules/simulation/src/Lumio.GameRuntime.Simulation/Ingress/IngressQueue.cs`<br>`modules/simulation/src/Lumio.GameRuntime.Simulation/Ingress/InputCanonicalizer.cs`<br>`modules/simulation/src/Lumio.GameRuntime.Simulation/Native/NativeCompletionQueue.cs`<br>`modules/simulation/src/Lumio.GameRuntime.Simulation/Native/NativeCompletionMerger.cs`<br>`modules/simulation/tests/Lumio.GameRuntime.Simulation.Tests/IngressCanonicalizationTests.cs`<br>`modules/simulation/tests/Lumio.GameRuntime.Simulation.Tests/NativeBarrierFaultTests.cs` | Wave 13 | 是：同 Wave 其他卡；依赖已完成且文件无重叠 |
| `sim-determinism-context-and-state-hash` | simulation | Foundation | `sim-processor-plan-validator`<br>`ecs-change-set-and-snapshot-view` | `modules/simulation/src/Lumio.GameRuntime.Simulation/Determinism/DeterminismContext.cs`<br>`modules/simulation/src/Lumio.GameRuntime.Simulation/Determinism/StateHashCoordinator.cs`<br>`modules/simulation/tests/Lumio.GameRuntime.Simulation.Tests/DeterminismReplayTests.cs` | Wave 15 | 是：同 Wave 其他卡；依赖已完成且文件无重叠 |
| `sim-fail-stop-and-tick-result` | simulation | Foundation | `sim-phase-graph-13`<br>`sim-processor-plan-validator`<br>`sim-ingress-and-native-completion`<br>`sim-determinism-context-and-state-hash`<br>`cmd-apply-to-ecs`<br>`obs-failure-bundle-assembly` | `modules/simulation/src/Lumio.GameRuntime.Simulation/Tick/TickRunner.cs`<br>`modules/simulation/src/Lumio.GameRuntime.Simulation/Tick/TickExecutionContext.cs`<br>`modules/simulation/src/Lumio.GameRuntime.Simulation/Tick/TickRunResult.cs`<br>`modules/simulation/src/Lumio.GameRuntime.Simulation/Tick/TickResultCache.cs`<br>`modules/simulation/src/Lumio.GameRuntime.Simulation/Failure/FailStopController.cs`<br>`modules/simulation/src/Lumio.GameRuntime.Simulation/Failure/PhaseFailureRecord.cs`<br>`modules/simulation/src/Lumio.GameRuntime.Simulation/Errors/SimulationFailure.cs`<br>`modules/simulation/tests/Lumio.GameRuntime.Simulation.Tests/FailStopCommitPointTests.cs`<br>`modules/simulation/tests/Lumio.GameRuntime.Simulation.Tests/DuplicateTickTests.cs` | Wave 16 | 是：同 Wave 其他卡；依赖已完成且文件无重叠 |
| `coord-revision-vector-view` | coordination | Foundation | `ecs-change-set-and-snapshot-view`<br>`obs-event-ports-and-context` | `modules/coordination/src/Lumio.GameRuntime.Coordination/Lumio.GameRuntime.Coordination.csproj`<br>`modules/coordination/tests/Lumio.GameRuntime.Coordination.Tests/Lumio.GameRuntime.Coordination.Tests.csproj`<br>`modules/coordination/src/Lumio.GameRuntime.Coordination/CoordinationModule.cs`<br>`modules/coordination/src/Lumio.GameRuntime.Coordination/CoordinationServices.cs`<br>`modules/coordination/src/Lumio.GameRuntime.Coordination/Revision/SessionRevisionVectorStore.cs`<br>`modules/coordination/src/Lumio.GameRuntime.Coordination/Revision/SessionRevisionVectorView.cs`<br>`modules/coordination/tests/Lumio.GameRuntime.Coordination.Tests/RevisionVectorPropertyTests.cs` | Wave 11 | 是：同 Wave 其他卡；依赖已完成且文件无重叠 |
| `coord-txn-state-and-idempotency` | coordination | Foundation | `coord-revision-vector-view` | `modules/coordination/src/Lumio.GameRuntime.Coordination/Lifecycle/CoordinatorState.cs`<br>`modules/coordination/src/Lumio.GameRuntime.Coordination/Transactions/CrossWorldCoordinator.cs`<br>`modules/coordination/src/Lumio.GameRuntime.Coordination/Transactions/CrossWorldTxnState.cs`<br>`modules/coordination/src/Lumio.GameRuntime.Coordination/Transactions/TxnParticipantState.cs`<br>`modules/coordination/src/Lumio.GameRuntime.Coordination/Transactions/TxnRecord.cs`<br>`modules/coordination/src/Lumio.GameRuntime.Coordination/Transactions/TxnIdempotencyIndex.cs`<br>`modules/coordination/tests/Lumio.GameRuntime.Coordination.Tests/TxnStateMachineGoldenTests.cs`<br>`modules/coordination/tests/Lumio.GameRuntime.Coordination.Tests/DuplicateLostResultTests.cs` | Wave 12 | 是：同 Wave 其他卡；依赖已完成且文件无重叠 |
| `coord-prepare-and-reservation` | coordination | Foundation | `coord-txn-state-and-idempotency`<br>`cmd-preflight-and-prepared-delta` | `modules/coordination/src/Lumio.GameRuntime.Coordination.VoxelAdapters/Lumio.GameRuntime.Coordination.VoxelAdapters.csproj`<br>`modules/coordination/src/Lumio.GameRuntime.Coordination/Prepare/TxnPrepareCoordinator.cs`<br>`modules/coordination/src/Lumio.GameRuntime.Coordination/Prepare/PreparedVoxelTokenLease.cs`<br>`modules/coordination/src/Lumio.GameRuntime.Coordination/Reservations/ReservationLease.cs`<br>`modules/coordination/src/Lumio.GameRuntime.Coordination.VoxelAdapters/GeneratedVoxelWorldPortAdapter.cs`<br>`modules/coordination/tests/Lumio.GameRuntime.Coordination.Tests/PrepareNoSideEffectTests.cs` | Wave 13 | 是：同 Wave 其他卡；依赖已完成且文件无重叠 |
| `coord-commit-intent-and-apply-order` | coordination | Foundation | `coord-prepare-and-reservation`<br>`cmd-apply-to-ecs`<br>`obs-durable-route-and-emergency-path` | `modules/coordination/src/Lumio.GameRuntime.Coordination/Journal/ITxnJournalPort.cs`<br>`modules/coordination/src/Lumio.GameRuntime.Coordination/Commit/CommitIntentCoordinator.cs`<br>`modules/coordination/src/Lumio.GameRuntime.Coordination/Commit/ParticipantApplyCoordinator.cs`<br>`modules/coordination/src/Lumio.GameRuntime.Coordination/Commit/ITxnParticipantQueryPort.cs`<br>`modules/coordination/tests/Lumio.GameRuntime.Coordination.Tests/CommitIntentOrderingTests.cs` | Wave 14 | 是：同 Wave 其他卡；依赖已完成且文件无重叠 |
| `coord-snapshot-cut` | coordination | Foundation | `coord-revision-vector-view`<br>`coord-txn-state-and-idempotency`<br>`ecs-change-set-and-snapshot-view` | `modules/coordination/src/Lumio.GameRuntime.Coordination/Snapshot/SnapshotCutCoordinator.cs`<br>`modules/coordination/src/Lumio.GameRuntime.Coordination/Snapshot/SnapshotCutLease.cs`<br>`modules/coordination/tests/Lumio.GameRuntime.Coordination.Tests/SnapshotCutConsistencyTests.cs` | Wave 13 | 是：同 Wave 其他卡；依赖已完成且文件无重叠 |
| `coord-crash-resolution-and-journal-port` | coordination | Foundation | `coord-commit-intent-and-apply-order`<br>`coord-snapshot-cut`<br>`obs-failure-bundle-assembly` | `modules/coordination/src/Lumio.GameRuntime.Coordination/Recovery/TxnRecoveryResolver.cs`<br>`modules/coordination/src/Lumio.GameRuntime.Coordination/Errors/CoordinationFailure.cs`<br>`modules/coordination/tests/Lumio.GameRuntime.Coordination.Tests/CrashBoundaryRecoveryTests.cs` | Wave 15 | 是：同 Wave 其他卡；依赖已完成且文件无重叠 |
| `repl-mapping-registry-and-identity` | replication | Foundation | `ecs-world-and-entity-identity`<br>`cfg-immutable-snapshot-reader`<br>`obs-event-ports-and-context` | `modules/replication/src/Lumio.GameRuntime.Replication/Lumio.GameRuntime.Replication.csproj`<br>`modules/replication/tests/Lumio.GameRuntime.Replication.Tests/Lumio.GameRuntime.Replication.Tests.csproj`<br>`modules/replication/src/Lumio.GameRuntime.Replication/ReplicationModule.cs`<br>`modules/replication/src/Lumio.GameRuntime.Replication/ReplicationServices.cs`<br>`modules/replication/src/Lumio.GameRuntime.Replication/Mapping/MappingRegistry.cs`<br>`modules/replication/src/Lumio.GameRuntime.Replication/Mapping/MappingSetView.cs`<br>`modules/replication/src/Lumio.GameRuntime.Replication/Identity/NetEntityMappingTable.cs`<br>`modules/replication/src/Lumio.GameRuntime.Replication/Identity/ProvisionalRemapTable.cs`<br>`modules/replication/tests/Lumio.GameRuntime.Replication.Tests/MappingRegistryGoldenTests.cs`<br>`modules/replication/tests/Lumio.GameRuntime.Replication.Tests/IdentityMappingPropertyTests.cs` | Wave 8 | 是：同 Wave 其他卡；依赖已完成且文件无重叠 |
| `repl-tombstone-horizon` | replication | Foundation | `repl-mapping-registry-and-identity` | `modules/replication/src/Lumio.GameRuntime.Replication/Identity/TombstoneRegistry.cs`<br>`modules/replication/src/Lumio.GameRuntime.Replication/Identity/TombstoneHorizonCalculator.cs`<br>`modules/replication/tests/Lumio.GameRuntime.Replication.Tests/TombstoneHorizonPropertyTests.cs` | Wave 9 | 是：同 Wave 其他卡；依赖已完成且文件无重叠 |
| `repl-baseline-delta-history` | replication | Foundation | `repl-tombstone-horizon`<br>`coord-revision-vector-view` | `modules/replication/src/Lumio.GameRuntime.Replication/Lifecycle/ReplicationContextState.cs`<br>`modules/replication/src/Lumio.GameRuntime.Replication/Lifecycle/ReplicationContext.cs`<br>`modules/replication/src/Lumio.GameRuntime.Replication/Projection/DirtySet.cs`<br>`modules/replication/src/Lumio.GameRuntime.Replication/History/BaselineStore.cs`<br>`modules/replication/src/Lumio.GameRuntime.Replication/History/DeltaHistory.cs`<br>`modules/replication/src/Lumio.GameRuntime.Replication/History/ReplicationBudget.cs`<br>`modules/replication/tests/Lumio.GameRuntime.Replication.Tests/BaselineDeltaHistoryTests.cs` | Wave 12 | 是：同 Wave 其他卡；依赖已完成且文件无重叠 |
| `repl-client-six-step-apply` | replication | Vertical Slice | `repl-baseline-delta-history`<br>`gas-prediction-frame`<br>`cmd-conflict-golden-property` | `modules/replication/src/Lumio.GameRuntime.Replication/Apply/ClientAuthorityApplyCoordinator.cs`<br>`modules/replication/src/Lumio.GameRuntime.Replication/Apply/AuthorityApplyPlan.cs`<br>`modules/replication/src/Lumio.GameRuntime.Replication/Apply/ConfirmedCommandSequence.cs`<br>`modules/replication/src/Lumio.GameRuntime.Replication/Apply/PresentationDiff.cs`<br>`modules/replication/src/Lumio.GameRuntime.Replication/Ports/IEcsReplicationView.cs`<br>`modules/replication/src/Lumio.GameRuntime.Replication/Ports/IGasReplicationView.cs`<br>`modules/replication/tests/Lumio.GameRuntime.Replication.Tests/ClientSixStepApplyTests.cs` | Wave 19 | 是：同 Wave 其他卡；依赖已完成且文件无重叠 |
| `repl-voxel-replica-adapter` | replication | Vertical Slice | `repl-client-six-step-apply`<br>`sim-ingress-and-native-completion` | `modules/replication/src/Lumio.GameRuntime.Replication.VoxelAdapters/Lumio.GameRuntime.Replication.VoxelAdapters.csproj`<br>`modules/replication/src/Lumio.GameRuntime.Replication.VoxelAdapters/GeneratedVoxelReplicaPortAdapter.cs`<br>`modules/replication/tests/Lumio.GameRuntime.Replication.Tests/VoxelReplicaContractTests.cs` | Wave 20 | 该 Wave 单卡 |
| `repl-resync-and-fault-matrix` | replication | Vertical Slice | `repl-voxel-replica-adapter`<br>`repl-baseline-delta-history`<br>`obs-failure-bundle-assembly` | `modules/replication/benchmarks/Lumio.GameRuntime.Replication.Benchmarks/Lumio.GameRuntime.Replication.Benchmarks.csproj`<br>`modules/replication/benchmarks/Lumio.GameRuntime.Replication.Benchmarks/ProjectionApplyBenchmarks.cs`<br>`modules/replication/src/Lumio.GameRuntime.Replication/Projection/ReplicationProjection.cs`<br>`modules/replication/src/Lumio.GameRuntime.Replication/Projection/ProjectionBatch.cs`<br>`modules/replication/src/Lumio.GameRuntime.Replication/Resync/ResyncCoordinator.cs`<br>`modules/replication/src/Lumio.GameRuntime.Replication/Ports/IReplicationEnvelopeCodecPort.cs`<br>`modules/replication/src/Lumio.GameRuntime.Replication/Errors/ReplicationFailure.cs`<br>`modules/replication/tests/Lumio.GameRuntime.Replication.Tests/ResyncFaultMatrixTests.cs`<br>`modules/replication/tests/Lumio.GameRuntime.Replication.Tests/LocalEmbeddedPipelineTests.cs` | Wave 21 | 该 Wave 单卡 |
| `gas-type-handle-registry` | gas | Vertical Slice | `ecs-world-and-entity-identity`<br>`cfg-immutable-snapshot-reader`<br>`obs-event-ports-and-context` | `modules/gas/src/Lumio.GameRuntime.Gas/Lumio.GameRuntime.Gas.csproj`<br>`modules/gas/tests/Lumio.GameRuntime.Gas.Tests/Lumio.GameRuntime.Gas.Tests.csproj`<br>`modules/gas/src/Lumio.GameRuntime.Gas/GasModule.cs`<br>`modules/gas/src/Lumio.GameRuntime.Gas/GasServices.cs`<br>`modules/gas/src/Lumio.GameRuntime.Gas/Lifecycle/GasFrameworkState.cs`<br>`modules/gas/src/Lumio.GameRuntime.Gas/Lifecycle/GasWorldContext.cs`<br>`modules/gas/src/Lumio.GameRuntime.Gas/Identity/AbilityTypeId.cs`<br>`modules/gas/src/Lumio.GameRuntime.Gas/Identity/AbilityInstanceId.cs`<br>`modules/gas/src/Lumio.GameRuntime.Gas/Identity/AbilityHandle.cs`<br>`modules/gas/src/Lumio.GameRuntime.Gas/Identity/EffectTypeId.cs`<br>`modules/gas/src/Lumio.GameRuntime.Gas/Identity/EffectInstanceId.cs`<br>`modules/gas/src/Lumio.GameRuntime.Gas/Identity/EffectHandle.cs`<br>`modules/gas/src/Lumio.GameRuntime.Gas/Identity/GasTypeRegistry.cs`<br>`modules/gas/tests/Lumio.GameRuntime.Gas.Tests/TypeHandlePropertyTests.cs` | Wave 8 | 是：同 Wave 其他卡；依赖已完成且文件无重叠 |
| `gas-ability-effect-state-machines` | gas | Vertical Slice | `gas-type-handle-registry` | `modules/gas/src/Lumio.GameRuntime.Gas/Ability/AbilityState.cs`<br>`modules/gas/src/Lumio.GameRuntime.Gas/Ability/AbilityStateMachine.cs`<br>`modules/gas/src/Lumio.GameRuntime.Gas/Effect/EffectState.cs`<br>`modules/gas/src/Lumio.GameRuntime.Gas/Effect/EffectStateMachine.cs`<br>`modules/gas/tests/Lumio.GameRuntime.Gas.Tests/AbilityStateMachineGoldenTests.cs`<br>`modules/gas/tests/Lumio.GameRuntime.Gas.Tests/EffectStateMachineGoldenTests.cs` | Wave 9 | 是：同 Wave 其他卡；依赖已完成且文件无重叠 |
| `gas-modifier-evaluation` | gas | Vertical Slice | `gas-ability-effect-state-machines`<br>`sim-determinism-context-and-state-hash` | `modules/gas/src/Lumio.GameRuntime.Gas/Evaluation/ModifierEvaluator.cs`<br>`modules/gas/src/Lumio.GameRuntime.Gas/Evaluation/ModifierEvaluationPlan.cs`<br>`modules/gas/src/Lumio.GameRuntime.Gas/Evaluation/IGameplayFormulaPort.cs`<br>`modules/gas/src/Lumio.GameRuntime.Gas/Execution/GasExecutionContext.cs`<br>`modules/gas/tests/Lumio.GameRuntime.Gas.Tests/ModifierDeterminismPropertyTests.cs` | Wave 16 | 是：同 Wave 其他卡；依赖已完成且文件无重叠 |
| `gas-ecs-authoritative-projection` | gas | Vertical Slice | `gas-modifier-evaluation`<br>`cmd-preflight-and-prepared-delta`<br>`ecs-change-set-and-snapshot-view` | `modules/gas/src/Lumio.GameRuntime.Gas/Execution/GasCommandEmitter.cs`<br>`modules/gas/src/Lumio.GameRuntime.Gas/Projection/IEcsGasProjectionPort.cs`<br>`modules/gas/src/Lumio.GameRuntime.Gas/Projection/GasEcsProjection.cs`<br>`modules/gas/src/Lumio.GameRuntime.Gas/Budget/GasBudget.cs`<br>`modules/gas/src/Lumio.GameRuntime.Gas/Errors/GasFailure.cs`<br>`modules/gas/tests/Lumio.GameRuntime.Gas.Tests/EcsSingleTruthTests.cs` | Wave 17 | 是：同 Wave 其他卡；依赖已完成且文件无重叠 |
| `gas-prediction-frame` | gas | Vertical Slice | `gas-ecs-authoritative-projection` | `modules/gas/src/Lumio.GameRuntime.Gas/Prediction/PredictionFrame.cs`<br>`modules/gas/src/Lumio.GameRuntime.Gas/Prediction/PredictionHistory.cs`<br>`modules/gas/src/Lumio.GameRuntime.Gas/Prediction/AuthorityConfirmation.cs`<br>`modules/gas/tests/Lumio.GameRuntime.Gas.Tests/PredictionFrameTests.cs` | Wave 18 | 是：同 Wave 其他卡；依赖已完成且文件无重叠 |
| `gas-snapshot-hash-and-migration` | gas | Vertical Slice | `gas-prediction-frame`<br>`coord-snapshot-cut` | `modules/gas/benchmarks/Lumio.GameRuntime.Gas.Benchmarks/Lumio.GameRuntime.Gas.Benchmarks.csproj`<br>`modules/gas/src/Lumio.GameRuntime.Gas/Snapshot/IGasSnapshotProvider.cs`<br>`modules/gas/src/Lumio.GameRuntime.Gas/Snapshot/GasSnapshotLease.cs`<br>`modules/gas/src/Lumio.GameRuntime.Gas/Migration/GasMigrationView.cs`<br>`modules/gas/tests/Lumio.GameRuntime.Gas.Tests/GasSnapshotHashTests.cs`<br>`modules/gas/benchmarks/Lumio.GameRuntime.Gas.Benchmarks/ModifierProjectionBenchmarks.cs` | Wave 19 | 是：同 Wave 其他卡；依赖已完成且文件无重叠 |
| `persist-canonical-codec` | persistence | Vertical Slice | `cfg-generated-table-validation`<br>`obs-event-ports-and-context` | `modules/persistence/src/Lumio.GameRuntime.Persistence/Lumio.GameRuntime.Persistence.csproj`<br>`modules/persistence/tests/Lumio.GameRuntime.Persistence.Tests/Lumio.GameRuntime.Persistence.Tests.csproj`<br>`modules/persistence/src/Lumio.GameRuntime.Persistence/Canonical/ICanonicalCodec.cs`<br>`modules/persistence/src/Lumio.GameRuntime.Persistence/Canonical/CanonicalRecordWriter.cs`<br>`modules/persistence/src/Lumio.GameRuntime.Persistence/Canonical/CanonicalRecordReader.cs`<br>`modules/persistence/src/Lumio.GameRuntime.Persistence/Canonical/MessagePackCanonicalCodecAdapter.cs`<br>`modules/persistence/tests/Lumio.GameRuntime.Persistence.Tests/CanonicalRoundTripGoldenTests.cs`<br>`modules/persistence/tests/Lumio.GameRuntime.Persistence.Tests/CanonicalPropertyTests.cs` | Wave 5 | 是：同 Wave 其他卡；依赖已完成且文件无重叠 |
| `persist-compression-and-decode-budget` | persistence | Vertical Slice | `persist-canonical-codec`<br>`cfg-immutable-snapshot-reader` | `modules/persistence/src/Lumio.GameRuntime.Persistence/Compression/ICompressionAdapter.cs`<br>`modules/persistence/src/Lumio.GameRuntime.Persistence/Compression/BrotliCompressionAdapter.cs`<br>`modules/persistence/src/Lumio.GameRuntime.Persistence/Compression/DecodeBudget.cs`<br>`modules/persistence/tests/Lumio.GameRuntime.Persistence.Tests/DecodeBudgetFuzzTests.cs` | Wave 7 | 是：同 Wave 其他卡；依赖已完成且文件无重叠 |
| `persist-snapshot-staging-activation` | persistence | Vertical Slice | `persist-compression-and-decode-budget`<br>`coord-snapshot-cut`<br>`gas-snapshot-hash-and-migration`<br>`repl-resync-and-fault-matrix` | `modules/persistence/src/Lumio.GameRuntime.Persistence/PersistenceModule.cs`<br>`modules/persistence/src/Lumio.GameRuntime.Persistence/PersistenceServices.cs`<br>`modules/persistence/src/Lumio.GameRuntime.Persistence/Lifecycle/SnapshotState.cs`<br>`modules/persistence/src/Lumio.GameRuntime.Persistence/Lifecycle/PersistenceSession.cs`<br>`modules/persistence/src/Lumio.GameRuntime.Persistence/Snapshot/SnapshotCoordinator.cs`<br>`modules/persistence/src/Lumio.GameRuntime.Persistence/Snapshot/SnapshotManifestBuilder.cs`<br>`modules/persistence/src/Lumio.GameRuntime.Persistence/Snapshot/SnapshotStagingStore.cs`<br>`modules/persistence/src/Lumio.GameRuntime.Persistence/Snapshot/ISnapshotStoragePort.cs`<br>`modules/persistence/src/Lumio.GameRuntime.Persistence/Snapshot/ISnapshotProvider.cs`<br>`modules/persistence/src/Lumio.GameRuntime.Persistence/Snapshot/CheckpointManager.cs`<br>`modules/persistence/tests/Lumio.GameRuntime.Persistence.Tests/SnapshotActivationCrashTests.cs` | Wave 22 | 是：同 Wave 其他卡；依赖已完成且文件无重叠 |
| `persist-durable-record-adapters` | persistence | Vertical Slice | `persist-canonical-codec`<br>`obs-durable-route-and-emergency-path`<br>`coord-commit-intent-and-apply-order` | `modules/persistence/src/Lumio.GameRuntime.Persistence/Durable/IDurableRecordStoragePort.cs`<br>`modules/persistence/src/Lumio.GameRuntime.Persistence/Durable/TxnJournalAdapter.cs`<br>`modules/persistence/src/Lumio.GameRuntime.Persistence/Durable/CommandLogAdapter.cs`<br>`modules/persistence/src/Lumio.GameRuntime.Persistence/Durable/WalAdapter.cs`<br>`modules/persistence/src/Lumio.GameRuntime.Persistence/Durable/DurableRecordVerifier.cs`<br>`modules/persistence/tests/Lumio.GameRuntime.Persistence.Tests/DurableRecordOrderingTests.cs` | Wave 15 | 是：同 Wave 其他卡；依赖已完成且文件无重叠 |
| `persist-recovery-replay` | persistence | Vertical Slice | `persist-snapshot-staging-activation`<br>`persist-durable-record-adapters`<br>`coord-crash-resolution-and-journal-port` | `modules/persistence/src/Lumio.GameRuntime.Persistence/Lifecycle/RecoveryState.cs`<br>`modules/persistence/src/Lumio.GameRuntime.Persistence/Recovery/RecoveryCoordinator.cs`<br>`modules/persistence/src/Lumio.GameRuntime.Persistence/Recovery/RecoveryCursor.cs`<br>`modules/persistence/src/Lumio.GameRuntime.Persistence/Recovery/IRecoveryApplyPort.cs`<br>`modules/persistence/src/Lumio.GameRuntime.Persistence/Errors/PersistenceFailure.cs`<br>`modules/persistence/tests/Lumio.GameRuntime.Persistence.Tests/RecoveryReplayTests.cs` | Wave 23 | 是：同 Wave 其他卡；依赖已完成且文件无重叠 |
| `persist-voxel-snapshot-adapter` | persistence | Vertical Slice | `persist-recovery-replay`<br>`sim-ingress-and-native-completion` | `modules/persistence/src/Lumio.GameRuntime.Persistence.Adapters/Lumio.GameRuntime.Persistence.Adapters.csproj`<br>`modules/persistence/benchmarks/Lumio.GameRuntime.Persistence.Benchmarks/Lumio.GameRuntime.Persistence.Benchmarks.csproj`<br>`modules/persistence/src/Lumio.GameRuntime.Persistence.Adapters/GeneratedVoxelSnapshotPortAdapter.cs`<br>`modules/persistence/tests/Lumio.GameRuntime.Persistence.Tests/VoxelSnapshotContractTests.cs`<br>`modules/persistence/benchmarks/Lumio.GameRuntime.Persistence.Benchmarks/CodecCompressionRecoveryBenchmarks.cs` | Wave 24 | 是：同 Wave 其他卡；依赖已完成且文件无重叠 |
| `reload-scope-and-resource-leases` | hot-reload | Vertical Slice | `sim-session-and-run-tick-entry`<br>`cfg-immutable-snapshot-reader`<br>`gas-type-handle-registry`<br>`obs-event-ports-and-context` | `modules/hot-reload/src/Lumio.GameRuntime.HotReload/Lumio.GameRuntime.HotReload.csproj`<br>`modules/hot-reload/tests/Lumio.GameRuntime.HotReload.Tests/Lumio.GameRuntime.HotReload.Tests.csproj`<br>`modules/hot-reload/src/Lumio.GameRuntime.HotReload/HotReloadModule.cs`<br>`modules/hot-reload/src/Lumio.GameRuntime.HotReload/HotReloadServices.cs`<br>`modules/hot-reload/src/Lumio.GameRuntime.HotReload/Scope/GameplayModuleScope.cs`<br>`modules/hot-reload/src/Lumio.GameRuntime.HotReload/Scope/GameplayModuleScopeState.cs`<br>`modules/hot-reload/src/Lumio.GameRuntime.HotReload/Scope/GameplayModuleScopeHandle.cs`<br>`modules/hot-reload/src/Lumio.GameRuntime.HotReload/Scope/ScopeRegistry.cs`<br>`modules/hot-reload/src/Lumio.GameRuntime.HotReload/Resources/ScopeResourceKind.cs`<br>`modules/hot-reload/src/Lumio.GameRuntime.HotReload/Resources/ScopeResourceLease.cs`<br>`modules/hot-reload/src/Lumio.GameRuntime.HotReload/Resources/ScopeResourceRegistry.cs`<br>`modules/hot-reload/src/Lumio.GameRuntime.HotReload/Resources/ResourceCompletion.cs`<br>`modules/hot-reload/tests/Lumio.GameRuntime.HotReload.Tests/ScopeLifecycleGoldenTests.cs`<br>`modules/hot-reload/tests/Lumio.GameRuntime.HotReload.Tests/ResourceLeasePropertyTests.cs` | Wave 13 | 是：同 Wave 其他卡；依赖已完成且文件无重叠 |
| `reload-six-step-unload` | hot-reload | Vertical Slice | `reload-scope-and-resource-leases`<br>`obs-failure-bundle-assembly` | `modules/hot-reload/src/Lumio.GameRuntime.HotReload/Unload/ScopeUnloadCoordinator.cs`<br>`modules/hot-reload/src/Lumio.GameRuntime.HotReload/Unload/DrainCoordinator.cs`<br>`modules/hot-reload/src/Lumio.GameRuntime.HotReload/Unload/DrainReport.cs`<br>`modules/hot-reload/src/Lumio.GameRuntime.HotReload/Unload/IHostUnloadPort.cs`<br>`modules/hot-reload/src/Lumio.GameRuntime.HotReload/Roots/IRootValidationPort.cs`<br>`modules/hot-reload/src/Lumio.GameRuntime.HotReload/Roots/RootValidationReport.cs`<br>`modules/hot-reload/src/Lumio.GameRuntime.HotReload/Budget/HotReloadBudget.cs`<br>`modules/hot-reload/src/Lumio.GameRuntime.HotReload/Errors/HotReloadFailure.cs`<br>`modules/hot-reload/tests/Lumio.GameRuntime.HotReload.Tests/SixStepUnloadTests.cs`<br>`modules/hot-reload/tests/Lumio.GameRuntime.HotReload.Tests/RootLeakFaultTests.cs` | Wave 14 | 是：同 Wave 其他卡；依赖已完成且文件无重叠 |
| `reload-dual-scope-barrier-switch` | hot-reload | Vertical Slice | `reload-six-step-unload`<br>`sim-phase-graph-13` | `modules/hot-reload/src/Lumio.GameRuntime.HotReload/Reload/ReloadSession.cs`<br>`modules/hot-reload/src/Lumio.GameRuntime.HotReload/Reload/ReloadSessionState.cs`<br>`modules/hot-reload/src/Lumio.GameRuntime.HotReload/Reload/BarrierSwitchCoordinator.cs`<br>`modules/hot-reload/src/Lumio.GameRuntime.HotReload/Reload/IScopeSwitchPort.cs`<br>`modules/hot-reload/tests/Lumio.GameRuntime.HotReload.Tests/DualScopeBarrierSwitchTests.cs` | Wave 15 | 是：同 Wave 其他卡；依赖已完成且文件无重叠 |
| `reload-migration-staging` | hot-reload | Vertical Slice | `reload-dual-scope-barrier-switch`<br>`persist-snapshot-staging-activation` | `modules/hot-reload/src/Lumio.GameRuntime.HotReload/Migration/IGameplayMigrationPort.cs`<br>`modules/hot-reload/src/Lumio.GameRuntime.HotReload/Migration/MigrationStagingContext.cs`<br>`modules/hot-reload/src/Lumio.GameRuntime.HotReload/Migration/MigrationResult.cs`<br>`modules/hot-reload/tests/Lumio.GameRuntime.HotReload.Tests/MigrationStagingTests.cs` | Wave 23 | 是：同 Wave 其他卡；依赖已完成且文件无重叠 |
| `reload-root-validation-soak` | hot-reload | Hardening/RT-D evidence | `reload-migration-staging`<br>`test-reference-host-shell`<br>`test-scenario-capability-and-faults` | `modules/hot-reload/tests/Lumio.GameRuntime.HotReload.Soaks/Lumio.GameRuntime.HotReload.Soaks.csproj`<br>`modules/hot-reload/tests/Lumio.GameRuntime.HotReload.Tests/RootLeakFaultTests.cs`<br>`modules/hot-reload/tests/Lumio.GameRuntime.HotReload.Soaks/HundredReloadSoakTests.cs` | Wave 24 | 是：同 Wave 其他卡；依赖已完成且文件无重叠 |
| `test-reference-host-shell` | testing | Foundation | `sim-fail-stop-and-tick-result`<br>`coord-crash-resolution-and-journal-port`<br>`obs-failure-bundle-assembly`<br>`cfg-tick-boundary-activation`<br>`test-reference-voxel-authority-port` | `modules/testing/src/Lumio.GameRuntime.Testing/Lumio.GameRuntime.Testing.csproj`<br>`modules/testing/src/Lumio.GameRuntime.ReferenceHost/Lumio.GameRuntime.ReferenceHost.csproj`<br>`modules/testing/tests/Lumio.GameRuntime.Testing.Tests/Lumio.GameRuntime.Testing.Tests.csproj`<br>`modules/testing/src/Lumio.GameRuntime.Testing/TestingModule.cs`<br>`modules/testing/src/Lumio.GameRuntime.ReferenceHost/ReferenceHost.cs`<br>`modules/testing/src/Lumio.GameRuntime.ReferenceHost/ReferenceHostSession.cs`<br>`modules/testing/src/Lumio.GameRuntime.ReferenceHost/ReferenceClockPort.cs`<br>`modules/testing/src/Lumio.GameRuntime.ReferenceHost/ReferenceObservabilitySink.cs`<br>`modules/testing/src/Lumio.GameRuntime.ReferenceHost/ReferenceGameplayModule.cs`<br>`modules/testing/tests/Lumio.GameRuntime.Testing.Tests/ReferenceHostFoundationSliceTests.cs` | Wave 17 | 是：同 Wave 其他卡；依赖已完成且文件无重叠 |
| `test-reference-voxel-authority-port` | testing | Foundation | `repo-generated-contract-boundary`<br>`coord-prepare-and-reservation`<br>`coord-commit-intent-and-apply-order` | `modules/testing/src/Lumio.GameRuntime.ReferenceHost/ReferenceVoxelAuthorityPort.cs`<br>`modules/testing/tests/Lumio.GameRuntime.Testing.Tests/ReferenceVoxelAuthorityTxnTests.cs` | Wave 15 | 是：同 Wave 其他卡；依赖已完成且文件无重叠 |
| `test-reference-voxel-port` | testing | Vertical Slice | `test-reference-host-shell`<br>`test-reference-voxel-authority-port`<br>`repl-voxel-replica-adapter`<br>`persist-voxel-snapshot-adapter` | `modules/testing/src/Lumio.GameRuntime.ReferenceHost/ReferenceVoxelPort.cs`<br>`modules/testing/tests/Lumio.GameRuntime.Testing.Tests/ReferenceVoxelDifferentialTests.cs` | Wave 25 | 是：同 Wave 其他卡；依赖已完成且文件无重叠 |
| `test-replay-and-first-difference` | testing | Foundation | `test-reference-host-shell`<br>`sim-determinism-context-and-state-hash` | `modules/testing/src/Lumio.GameRuntime.Testing/Replay/ReplayInput.cs`<br>`modules/testing/src/Lumio.GameRuntime.Testing/Replay/ReplayRunner.cs`<br>`modules/testing/src/Lumio.GameRuntime.Testing/Replay/CanonicalStateHasher.cs`<br>`modules/testing/src/Lumio.GameRuntime.Testing/Replay/FirstDifferenceFinder.cs`<br>`modules/testing/src/Lumio.GameRuntime.Testing/Replay/ReplayResult.cs`<br>`modules/testing/tests/Lumio.GameRuntime.Testing.Tests/ReplayFirstDifferenceTests.cs` | Wave 18 | 是：同 Wave 其他卡；依赖已完成且文件无重叠 |
| `test-scenario-capability-and-faults` | testing | Vertical Slice | `test-reference-host-shell`<br>`test-replay-and-first-difference` | `modules/testing/src/Lumio.GameRuntime.Testing/Scenario/ScenarioDescriptor.cs`<br>`modules/testing/src/Lumio.GameRuntime.Testing/Scenario/TestScenarioState.cs`<br>`modules/testing/src/Lumio.GameRuntime.Testing/Scenario/ScenarioRunner.cs`<br>`modules/testing/src/Lumio.GameRuntime.Testing/Scenario/CapabilityMatcher.cs`<br>`modules/testing/src/Lumio.GameRuntime.Testing/Scenario/ScenarioResult.cs`<br>`modules/testing/src/Lumio.GameRuntime.Testing/Faults/FaultProfile.cs`<br>`modules/testing/src/Lumio.GameRuntime.Testing/Faults/FaultInjectionAdapter.cs`<br>`modules/testing/src/Lumio.GameRuntime.Testing/Faults/FaultEvent.cs`<br>`modules/testing/tests/Lumio.GameRuntime.Testing.Tests/ScenarioFaultDeterminismTests.cs` | Wave 19 | 是：同 Wave 其他卡；依赖已完成且文件无重叠 |
| `test-contract-fixture-runner` | testing | Vertical Slice | `repo-generated-contract-boundary`<br>`persist-canonical-codec`<br>`repl-resync-and-fault-matrix` | `modules/testing/src/Lumio.GameRuntime.Testing/Fixtures/ContractFixtureRunner.cs`<br>`modules/testing/src/Lumio.GameRuntime.Testing/Fixtures/FixtureResult.cs`<br>`modules/testing/tests/Lumio.GameRuntime.Testing.Tests/ContractFixtureRunnerTests.cs` | Wave 22 | 是：同 Wave 其他卡；依赖已完成且文件无重叠 |
| `test-production-isolation-and-workloads` | testing | Hardening/RT-D evidence | `test-scenario-capability-and-faults`<br>`test-contract-fixture-runner`<br>`reload-root-validation-soak`<br>`persist-recovery-replay` | `modules/testing/src/Lumio.GameRuntime.Testing/Workloads/WorkloadDescriptor.cs`<br>`modules/testing/src/Lumio.GameRuntime.Testing/Workloads/WorkloadRunner.cs`<br>`modules/testing/src/Lumio.GameRuntime.Testing/Workloads/WorkloadResult.cs`<br>`modules/testing/src/Lumio.GameRuntime.Testing/Isolation/ProductionDependencyGuard.cs`<br>`modules/testing/tests/Lumio.GameRuntime.Testing.Tests/ProductionIsolationTests.cs`<br>`modules/testing/workloads/foundation-single-world.json`<br>`modules/testing/workloads/replication-loss-reorder.json`<br>`modules/testing/workloads/persistence-crash-boundaries.json`<br>`modules/testing/workloads/hot-reload-100-cycles.json` | Wave 25 | 是：同 Wave 其他卡；依赖已完成且文件无重叠 |
| `repo-dotnet-baseline` | repository | Foundation | 无 | `global.json`<br>`Directory.Build.props`<br>`Directory.Build.targets`<br>`.editorconfig`<br>`eng/verify-sdk.sh`<br>`eng/verify-sdk.ps1` | Wave 0 | 该 Wave 单卡 |
| `repo-supply-chain-policy` | repository | Foundation | `repo-dotnet-baseline` | `Directory.Packages.props`<br>`NuGet.config`<br>`eng/dependency-policy.json`<br>`eng/verify-dependencies.sh`<br>`eng/verify-dependencies.ps1`<br>`eng/generate-sbom.sh`<br>`eng/generate-sbom.ps1`<br>`THIRD_PARTY_NOTICES.md` | Wave 1 | 该 Wave 单卡 |
| `repo-generated-contract-boundary` | repository | Foundation | `repo-dotnet-baseline`<br>`repo-supply-chain-policy` | `src/Lumio.GameRuntime.GeneratedContracts/Lumio.GameRuntime.GeneratedContracts.csproj`<br>`src/Lumio.GameRuntime.GeneratedContracts/GeneratedContractManifest.cs`<br>`src/Lumio.GameRuntime.GeneratedContracts/README.md`<br>`eng/generate-contracts.sh`<br>`eng/generate-contracts.ps1`<br>`eng/verify-generated-contracts.sh`<br>`eng/verify-generated-contracts.ps1`<br>`tests/Lumio.GameRuntime.GeneratedContracts.Tests/Lumio.GameRuntime.GeneratedContracts.Tests.csproj`<br>`tests/Lumio.GameRuntime.GeneratedContracts.Tests/GeneratedContractBaselineTests.cs` | Wave 2 | 该 Wave 单卡 |
| `repo-solution-graph-and-architecture-tests` | repository | Foundation | `repo-generated-contract-boundary`<br>`obs-event-ports-and-context`<br>`cfg-generated-table-validation`<br>`ecs-world-and-entity-identity`<br>`cmd-buffer-and-deferred-token`<br>`sim-session-and-run-tick-entry`<br>`coord-revision-vector-view`<br>`repl-mapping-registry-and-identity`<br>`gas-type-handle-registry`<br>`persist-canonical-codec`<br>`reload-scope-and-resource-leases`<br>`test-reference-host-shell` | `Lumio.GameRuntime.slnx`<br>`tests/Lumio.GameRuntime.Architecture.Tests/Lumio.GameRuntime.Architecture.Tests.csproj`<br>`tests/Lumio.GameRuntime.Architecture.Tests/ProjectDependencyGraphTests.cs`<br>`tests/Lumio.GameRuntime.Architecture.Tests/PublicSurfaceIsolationTests.cs`<br>`tests/Lumio.GameRuntime.Architecture.Tests/GeneratedContractDirectionTests.cs`<br>`tests/Lumio.GameRuntime.Architecture.Tests/TestingIsolationTests.cs`<br>`eng/verify-project-graph.sh`<br>`eng/verify-project-graph.ps1` | Wave 18 | 是：同 Wave 其他卡；依赖已完成且文件无重叠 |

### 6.2 Wave 划分

- **Wave 0**：`repo-dotnet-baseline`。前置均已在更早 Wave 完成；本 Wave 文件集合经精确路径检查无交集。
- **Wave 1**：`repo-supply-chain-policy`。前置均已在更早 Wave 完成；本 Wave 文件集合经精确路径检查无交集。
- **Wave 2**：`repo-generated-contract-boundary`。前置均已在更早 Wave 完成；本 Wave 文件集合经精确路径检查无交集。
- **Wave 3**：`obs-event-ports-and-context`。前置均已在更早 Wave 完成；本 Wave 文件集合经精确路径检查无交集。
- **Wave 4**：`obs-bounded-diagnostic-routing`、`obs-durable-route-and-emergency-path`、`cfg-generated-table-validation`。前置均已在更早 Wave 完成；本 Wave 文件集合经精确路径检查无交集。
- **Wave 5**：`obs-failure-bundle-assembly`、`obs-otel-and-microsoft-logging-adapters`、`cfg-six-layer-merge`、`persist-canonical-codec`。前置均已在更早 Wave 完成；本 Wave 文件集合经精确路径检查无交集。
- **Wave 6**：`cfg-immutable-snapshot-reader`。前置均已在更早 Wave 完成；本 Wave 文件集合经精确路径检查无交集。
- **Wave 7**：`cfg-tick-boundary-activation`、`ecs-world-and-entity-identity`、`persist-compression-and-decode-budget`。前置均已在更早 Wave 完成；本 Wave 文件集合经精确路径检查无交集。
- **Wave 8**：`cfg-dev-capability-adapter`、`ecs-storage-adapter-contract`、`cmd-buffer-and-deferred-token`、`repl-mapping-registry-and-identity`、`gas-type-handle-registry`。前置均已在更早 Wave 完成；本 Wave 文件集合经精确路径检查无交集。
- **Wave 9**：`ecs-query-read-write-views`、`cmd-seal-and-stable-merge`、`cmd-capacity-and-durable-record-route`、`repl-tombstone-horizon`、`gas-ability-effect-state-machines`。前置均已在更早 Wave 完成；本 Wave 文件集合经精确路径检查无交集。
- **Wave 10**：`ecs-change-set-and-snapshot-view`、`cmd-preflight-and-prepared-delta`。前置均已在更早 Wave 完成；本 Wave 文件集合经精确路径检查无交集。
- **Wave 11**：`ecs-world-lifecycle-fail-stop`、`ecs-storage-candidate-benchmarks`、`coord-revision-vector-view`。前置均已在更早 Wave 完成；本 Wave 文件集合经精确路径检查无交集。
- **Wave 12**：`cmd-apply-to-ecs`、`sim-session-and-run-tick-entry`、`coord-txn-state-and-idempotency`、`repl-baseline-delta-history`。前置均已在更早 Wave 完成；本 Wave 文件集合经精确路径检查无交集。
- **Wave 13**：`cmd-conflict-golden-property`、`sim-phase-graph-13`、`sim-ingress-and-native-completion`、`coord-prepare-and-reservation`、`coord-snapshot-cut`、`reload-scope-and-resource-leases`。前置均已在更早 Wave 完成；本 Wave 文件集合经精确路径检查无交集。
- **Wave 14**：`sim-processor-plan-validator`、`coord-commit-intent-and-apply-order`、`reload-six-step-unload`。前置均已在更早 Wave 完成；本 Wave 文件集合经精确路径检查无交集。
- **Wave 15**：`sim-determinism-context-and-state-hash`、`coord-crash-resolution-and-journal-port`、`persist-durable-record-adapters`、`reload-dual-scope-barrier-switch`、`test-reference-voxel-authority-port`。前置均已在更早 Wave 完成；本 Wave 文件集合经精确路径检查无交集。
- **Wave 16**：`sim-fail-stop-and-tick-result`、`gas-modifier-evaluation`。前置均已在更早 Wave 完成；本 Wave 文件集合经精确路径检查无交集。
- **Wave 17**：`gas-ecs-authoritative-projection`、`test-reference-host-shell`。前置均已在更早 Wave 完成；本 Wave 文件集合经精确路径检查无交集。
- **Wave 18**：`gas-prediction-frame`、`test-replay-and-first-difference`、`repo-solution-graph-and-architecture-tests`。前置均已在更早 Wave 完成；本 Wave 文件集合经精确路径检查无交集。
- **Wave 19**：`repl-client-six-step-apply`、`gas-snapshot-hash-and-migration`、`test-scenario-capability-and-faults`。前置均已在更早 Wave 完成；本 Wave 文件集合经精确路径检查无交集。
- **Wave 20**：`repl-voxel-replica-adapter`。前置均已在更早 Wave 完成；本 Wave 文件集合经精确路径检查无交集。
- **Wave 21**：`repl-resync-and-fault-matrix`。前置均已在更早 Wave 完成；本 Wave 文件集合经精确路径检查无交集。
- **Wave 22**：`persist-snapshot-staging-activation`、`test-contract-fixture-runner`。前置均已在更早 Wave 完成；本 Wave 文件集合经精确路径检查无交集。
- **Wave 23**：`persist-recovery-replay`、`reload-migration-staging`。前置均已在更早 Wave 完成；本 Wave 文件集合经精确路径检查无交集。
- **Wave 24**：`persist-voxel-snapshot-adapter`、`reload-root-validation-soak`。前置均已在更早 Wave 完成；本 Wave 文件集合经精确路径检查无交集。
- **Wave 25**：`test-reference-voxel-port`、`test-production-isolation-and-workloads`。前置均已在更早 Wave 完成；本 Wave 文件集合经精确路径检查无交集。

### 6.3 第一条可运行 Foundation 单线程切片

**目标行为。** Reference Host 创建一个 Authoritative `GameWorld` 与最小 `ReferenceVoxelAuthorityPort`，注入一个已验证 Input/Command，在单 Owner Thread 中调用唯一 `RunTick`。Tick 必须经过全部 13 Phase；Processor 写已有字段并产生结构命令，CrossWorldTxn 在 Prepare 阶段完成全部业务校验，durable `CommitIntent` 先于 `VoxelCommit -> EcsCommandBufferCommit`，`GasAndEventFinalize` 成为唯一 Commit Point，最后输出 `TickResult + SessionRevisionVector + Canonical StateHash`。同一输入、Config、Release、Seed 重复执行必须得到相同 phase trace 与 hash。

**必需任务集合。**

- 仓库/契约：`repo-dotnet-baseline`、`repo-supply-chain-policy`、`repo-generated-contract-boundary`；完成模块项目后执行 `repo-solution-graph-and-architecture-tests`。
- 证据与配置：`obs-event-ports-and-context`、`obs-bounded-diagnostic-routing`、`obs-durable-route-and-emergency-path`、`obs-failure-bundle-assembly`；`cfg-generated-table-validation`、`cfg-six-layer-merge`、`cfg-immutable-snapshot-reader`、`cfg-tick-boundary-activation`。
- ECS：`ecs-world-and-entity-identity`、`ecs-storage-adapter-contract`、`ecs-query-read-write-views`、`ecs-change-set-and-snapshot-view`、`ecs-world-lifecycle-fail-stop`。首切片只需一个通过同一 Adapter contract 的 reference/minimal storage；Friflo/Arch 胜出仍留在 RT-D-002。
- Command：`cmd-buffer-and-deferred-token`、`cmd-seal-and-stable-merge`、`cmd-preflight-and-prepared-delta`、`cmd-apply-to-ecs`、`cmd-capacity-and-durable-record-route`、`cmd-conflict-golden-property`。
- Coordination：`coord-revision-vector-view`、`coord-txn-state-and-idempotency`、`coord-prepare-and-reservation`、`coord-commit-intent-and-apply-order`、`coord-snapshot-cut`、`coord-crash-resolution-and-journal-port`。
- Simulation：`sim-session-and-run-tick-entry`、`sim-phase-graph-13`、`sim-processor-plan-validator`、`sim-ingress-and-native-completion`、`sim-determinism-context-and-state-hash`、`sim-fail-stop-and-tick-result`。
- Reference Host：`test-reference-voxel-authority-port`、`test-reference-host-shell`。P1 模块只需已建立的窄项目/Port面：`gas-type-handle-registry`、`repl-mapping-registry-and-identity`、`persist-canonical-codec`；首切片可在 Capability 中声明无 Client Replica、无 Snapshot request、无 Hot Reload，但不能新增测试后门或绕过真实 Port。

**客观完成条件。**

1. `RunTick` 是唯一公开 Tick 入口；重复 TickId、乱序 Input、QueueFull 和非法 Processor Plan 有稳定结果。
2. Phase trace 与架构源 13 Phase 完全一致，任何跳相/重排测试失败。
3. CrossWorld Prepare 失败前后 ECS/Voxel visible revision不变；`CommitIntent` durable ack 前没有 participant write。
4. `VoxelCommit -> EcsCommandBufferCommit -> GasAndEventFinalize` 顺序可由 trace与receipts断言；Prepared后业务拒绝触发Fail-stop/Session Fault。
5. 相同输入执行两次得到相同 `TickResult`、Revision与StateHash；对象地址、Dictionary插入序、Diagnostic、wall clock不影响hash。
6. 首个Snapshot前故障可组装带 `noSnapshotReason + bootstrapPhase + lastKnownRevision/manifest` 的合法 Failure Bundle。
7. Production project graph不引用Testing/ReferenceHost，Reference Host只通过公开候选API和generated contracts组装。

### 6.4 RT-D-001..011 证据门映射

| 决策门 | 收集证据的任务卡 | 需要提交的证据 | 本设计不预先批准的内容 |
|---|---|---|---|
| `RT-D-001` 逻辑模块到程序集 | `repo-solution-graph-and-architecture-tests`、`sim-session-and-run-tick-entry`、各模块首卡、`test-production-isolation-and-workloads` | project graph、public/internal surface、build time、AOT/Unity compile、包/发布边界 | 最终合并或拆分哪些程序集 |
| `RT-D-002` ECS Storage/Query/ChangeSet | `ecs-storage-adapter-contract`、`ecs-query-read-write-views`、`ecs-change-set-and-snapshot-view`、`ecs-storage-candidate-benchmarks`、`test-reference-host-shell` | Friflo/Arch/reference 同Golden/Property/Differential/Benchmark、NativeAOT/Unity evidence | Friflo或Arch胜出、内部layout数字 |
| `RT-D-003` Command冲突/Token/容量 | `cmd-buffer-and-deferred-token`、`cmd-seal-and-stable-merge`、`cmd-preflight-and-prepared-delta`、`cmd-apply-to-ecs`、`cmd-capacity-and-durable-record-route`、`cmd-conflict-golden-property` | exact状态/排序/冲突矩阵、budget/full、Prepared后故障、Property corpus | 冲突policy与容量值 |
| `RT-D-004` Journal retention/Reservation lease | `coord-txn-state-and-idempotency`、`coord-prepare-and-reservation`、`coord-commit-intent-and-apply-order`、`coord-crash-resolution-and-journal-port`、`persist-durable-record-adapters`、`test-reference-voxel-authority-port` | crash-at-boundary、lost result、四态participant、retention/lease workload | 保留周期、lease deadline、backend durability |
| `RT-D-005` Dirty/History/Baseline内存 | `repl-mapping-registry-and-identity`、`repl-tombstone-horizon`、`repl-baseline-delta-history`、`repl-client-six-step-apply`、`repl-resync-and-fault-matrix`、`test-production-isolation-and-workloads` | loss/reorder/reconnect/prediction/tombstone Soak、bytes/resync rate | 窗口/字节/FullResync阈值 |
| `RT-D-006` GAS projection/modifier | `gas-ability-effect-state-machines`、`gas-modifier-evaluation`、`gas-ecs-authoritative-projection`、`gas-prediction-frame`、`gas-snapshot-hash-and-migration` | 单一真相、order/tie-breaker、rollback、snapshot/hash、workload | numeric model、cache/layout、复杂求解范围 |
| `RT-D-007` Snapshot/WAL backend/durability | `persist-canonical-codec`、`persist-compression-and-decode-budget`、`persist-snapshot-staging-activation`、`persist-durable-record-adapters`、`persist-recovery-replay`、`persist-voxel-snapshot-adapter` | Golden bytes、crash atomicity、durability receipts、recovery time/queue/backend benchmark | 文件/数据库/对象存储、fsync/group-commit、checkpoint周期 |
| `RT-D-008` Config reader/Dev Adapter | `cfg-generated-table-validation`、`cfg-six-layer-merge`、`cfg-immutable-snapshot-reader`、`cfg-tick-boundary-activation`、`cfg-dev-capability-adapter` | typed/range/ref fixtures、layer property、reader perf、Dev full-path validation | reader storage/layout、Dev watch/compile provider |
| `RT-D-009` Sink/PII/backpressure | `obs-bounded-diagnostic-routing`、`obs-durable-route-and-emergency-path`、`obs-failure-bundle-assembly`、`obs-otel-and-microsoft-logging-adapters`、`test-production-isolation-and-workloads` | queue/sink/disk/full/PII/failure bundle Soak、Simulation overhead | 外部sink、容量、PII/retention、durable escalation参数 |
| `RT-D-010` Hot Reload timeout/root validation | `reload-scope-and-resource-leases`、`reload-six-step-unload`、`reload-dual-scope-barrier-switch`、`reload-migration-staging`、`reload-root-validation-soak` | 100-cycle Soak、root/lease/ALC evidence、pre/post switch faults | timeout值、Session/Process escalation、平台实现 |
| `RT-D-011` Reference Host/Replay fidelity | `sim-determinism-context-and-state-hash`、`sim-fail-stop-and-tick-result`、`test-reference-host-shell`、`test-reference-voxel-authority-port`、`test-replay-and-first-difference`、`test-scenario-capability-and-faults`、`test-contract-fixture-runner`、`test-production-isolation-and-workloads` | profile differential、first-difference、fixture coverage、workload repeatability、production isolation | fidelity等级命名、result retention、CI门槛 |

### 6.5 阻塞性待澄清

1. **架构源生成物的实际 checkout/commit 与命令入口。** 当前文档能确认 V1.3 contract名称和语义，但实现卡必须拿到 `LumioGameEngineArchitecture` 的确切 commit、生成命令、输出manifest与C# namespace/member names；缺失时 `repo-generated-contract-boundary` 只能明确失败，不能手写替代。
2. **Generated Voxel Authority/Replica/Snapshot C# 投影的精确签名。** V1.3 已冻结契约面与语义，本设计只给出候选Adapter方法；实现前必须以生成物替换语义草图，任何字段/枚举差异回到架构源处理。
3. **目标 Unity/IL2CPP 与NativeAOT验证矩阵。** `netstandard2.1` 是兼容面，但Unity版本、IL2CPP平台、裁剪配置、Server NativeAOT publish target未在本仓文档冻结；这会改变第三方Adapter能否通过，不改变模块边界。
4. **Host durability capability与D-005的可观察receipt。** Reference Port可以先给确定性durable receipt，但生产 `ISnapshotStoragePort`/`IDurableRecordStoragePort` 需要Host明确sync/group-commit/atomic-activate能力，才能完成RT-D-007证据。
5. **Host root validation/ALC unload的能力等级。** Runtime语义已冻结，但哪些平台能提供root枚举、强/弱证据、ALC unload与HybridCLR能力需Host capability声明；缺少时只能拒绝生产热更，不能跳过ValidateRoots。

## 7. 本仓 ADR 草案附录

### ADR-DRAFT-RT-001：第三方基础设施只经 Adapter 与统一依赖策略进入

- **状态**：Proposed evidence policy；不批准任一RT-D。
- **上下文**：日志、Trace、Channel、池、codec、压缩、ECS、测试工具若由各模块独立选择，会泄漏类型、重复栈并破坏AOT/供应链治理。
- **候选**：模块直接引用；建立共享通用“Utils”；Port/Adapter + 中央包/锁/SBOM。
- **建议**：采用Port/Adapter +中央版本/锁/SBOM；不创建无所有者的Common/Utils业务程序集。第三方public surface扫描为Architecture Test。
- **后果**：多一层适配与测试；换供应商成本下降，License/AOT/Unity/NativeAOT风险可集中验证。
- **退出路径**：若BCL覆盖能力，删除Adapter/package并保持Port；若上游修复满足缺口，移除本地patch。

### ADR-DRAFT-RT-002：ECS内部引擎以同一Adapter证据竞选，不在设计阶段指定胜者

- **状态**：Proposed experiment for `RT-D-002`。
- **上下文**：Friflo与Arch均有可用特征，但Generation、stable iteration、structural command、snapshot/change、Unity/NativeAOT风险必须用本仓语义测试判断。
- **候选**：Friflo首选Adapter；Arch比较Adapter；最小reference storage。
- **建议**：三者实现同 `IWorldStorageAdapter` test surface；用同Golden/Property/Differential/Benchmark与AOT/Unity compile证据评审。Reference storage不进入生产。
- **后果**：Foundation有额外Adapter工作，但避免在未测情况下锁死布局；stable Runtime API不受胜者影响。
- **退出路径**：胜者替换只删除losing Adapter/package；fixture/hash/command/port不变。

### ADR-DRAFT-RT-003：Canonical语义与primitive codec/压缩供应商分离

- **状态**：Proposed for `RT-D-007` evidence；公共字节语义仍归架构源。
- **上下文**：直接使用MessagePack/MemoryPack默认resolver会把库行为当公共格式，并引入反射、字段序和AOT差异。
- **候选**：库默认对象序列化；完全自研serializer；generated field descriptor + custom canonical layer + mature primitive reader/writer。
- **建议**：第三种。MessagePack只处理primitive；Brotli只处理压缩；Canonical writer控制field ordinal、numeric/byte order、length、duplicate/unknown、hash/checksum。
- **后果**：需要Golden bytes和额外writer/reader代码，但不重造通用codec；供应商可替换。
- **退出路径**：替换primitive/压缩Adapter，要求未压缩canonical bytes与恢复结果不变。

### ADR-DRAFT-RT-004：Reference Host保真分层以实际合同路径定义

- **状态**：Proposed evidence model for `RT-D-011`。
- **上下文**：Reference Host若直接调用module方法，会让LocalEmbedded“通过”却绕过Envelope/queue/permission/Port；若完全复制生产Host则维护成本失控。
- **候选**：直接in-process捷径；完整生产Host；窄Reference Adapters但走相同contract pipeline。
- **建议**：第三种。能力Profile显式记录PureHeadless/NativeHeadless/LocalEmbedded/LocalSplitProcess；所有Profile必须走generated contract、serializer、permission、size、bounded queue和barrier，只替换Socket/介质/内部storage。
- **后果**：Reference Adapter需Differential tests；能定位contract与实现差异，且不把测试代码带入生产。
- **退出路径**：某Reference Adapter失真时以真实Adapter替换或提高profile能力，不修改production interface加测试后门。

### ADR-DRAFT-RT-005：初始物理程序集映射只作为RT-D-001试验配置

- **状态**：Proposed experiment；未批准。
- **上下文**：逻辑隔离已冻结，但首期每模块一程序集可能增加构建/发布复杂度，过度合并又会隐藏反向依赖。
- **候选**：一模块一assembly；P0/P1聚合assembly；单Runtime assembly + namespace/internal guards。
- **建议**：项目骨架先按逻辑模块独立，允许solution层在证据后合并发布artifact；Architecture Tests始终按逻辑DAG扫描源码/project/public surface。
- **后果**：能直接发现环和第三方泄漏；RT-D-001可基于build/AOT/Unity/package evidence决定最终artifact布局。
- **退出路径**：只改project/package组合，不移动状态所有权、不改变Port方向和public contract。

## 8. 质量门自检

- [x] 11 个模块均包含 0–9 完整模板；每模块有 5–8 条明确非职责、具体文件/类型/Port、线程/生命周期、队列、9 条分类失败、测试面与 4–12 张任务卡。
- [x] 成熟方案先于自研；每个自研语义有候选/否决、最小范围和替换路径。
- [x] 第三方类型均限制在 Adapter/internal；稳定候选 API 只见Runtime/generated/BCL受限值类型。
- [x] 模块DAG无环；`ITxnJournalPort`归coordination而由persistence实现；replication确认序号通过中立generated contract。
- [x] Host/Voxel/Game职责未吞入Runtime；Generated Voxel Port不暴露storage。
- [x] 未新增公共Schema字段、ID、错误码或枚举；所有C#名字标为候选/投影。
- [x] Config没有stable `compile` API；Testing不进production依赖。
- [x] 68张任务卡依赖完整且无环；Wave按精确文件路径验证同Wave无重叠。
- [x] 验收标准均可转成命令、失败测试、fixture、property、fault或benchmark evidence。
- [x] 所有RT-D仍为证据门；本文不把候选或临时配置写成已批准决策。
- [x] 本文只设计未来文件，不包含生产C#实现、项目文件、NuGet变更或架构镜像修改。
