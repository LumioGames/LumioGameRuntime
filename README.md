# LumioGameRuntime

> Server 与 Client 共用的稳定 C# ECS Runtime、逻辑 Tick、Coordinator、Replication 语义和 GAS Framework。

<!-- lumio-community:start -->
<div align="center">
<table>
<tr>
<td align="center" width="50%" valign="top">
<a href="https://qm.qq.com/q/PGkXh4tCyQ"><img src="https://raw.githubusercontent.com/LumioGames/.github/main/profile/assets/qr-qq.svg" width="170" alt="QQ 交流群 972220164"></a><br>
<a href="https://qm.qq.com/q/PGkXh4tCyQ"><img src="https://img.shields.io/badge/QQ%20%E4%BA%A4%E6%B5%81%E7%BE%A4-972220164-6171F0?style=for-the-badge&logo=tencentqq&logoColor=white" alt="QQ 交流群 972220164"></a><br>
<sub>什么都能聊</sub>
</td>
<td align="center" width="50%" valign="top">
<a href="https://applink.feishu.cn/client/chat/chatter/add_by_link?link_token=fffn1ae7-fd83-4315-96ac-6fa3aba3968e"><img src="https://raw.githubusercontent.com/LumioGames/.github/main/profile/assets/qr-engine.svg" width="170" alt="LumioEngine 开发者社区"></a><br>
<a href="https://applink.feishu.cn/client/chat/chatter/add_by_link?link_token=fffn1ae7-fd83-4315-96ac-6fa3aba3968e"><img src="https://img.shields.io/badge/%E9%A3%9E%E4%B9%A6%E7%BE%A4-LumioEngine%20%E5%BC%80%E5%8F%91%E8%80%85%E7%A4%BE%E5%8C%BA-5DE2C6?style=for-the-badge&logoColor=1E2A3A" alt="LumioEngine 开发者社区"></a><br>
<sub>飞书话题群 · Rust / C# 引擎层</sub>
</td>
</tr>
</table>
<sub>先进群再看代码。其它群和整体介绍见 <a href="https://github.com/LumioGames">LumioGames 主页</a>。</sub>
</div>
<!-- lumio-community:end -->

## 架构基线

- Baseline：`LGE-V1.4-2026-08-27`
- 唯一架构源：`LumioGameEngineArchitecture`
- 本地镜像：[`docs/architecture/LumioGameEngine_Architecture_v1.4.md`](docs/architecture/LumioGameEngine_Architecture_v1.4.md)

Runtime 位于 Native/领域库之上、具体游戏内容之下。它拥有逻辑模拟语义和 ECS World，但不拥有 Server 进程时钟、Socket、Voxel 内部或具体玩法。稳定 Runtime 与从 `LumioGame` 加载的 Role-specific Gameplay Assembly 必须分开。

## Architecture Gate

Tick/Processor、Revision、Txn、Replication、Mapping、Snapshot 和 Failure Bundle Schema 只维护在 `LumioGameEngineArchitecture`。Runtime API 或 Phase 变更必须带状态机、正向/失败 Fixture 和 SchemaEpoch 说明，并在架构源执行 `python3 tools/lumio_contract.py validate`；实现仓库不得自行扩展公共字段。

## 拥有的状态与生命周期

- 每个 Role/World 独立的 Entity、Component Storage、Query、CommandBuffer 和 Change Tracking。
- `GameWorld`、客户端 World 的逻辑创建、Tick Phase、结构提交、Snapshot Projection 和销毁。
- `SimulationSession` 对外聚合/暴露 Logical Tick 与 Coordinator Facade：`simulation` 唯一拥有 Logical TickId、Phase 与 Determinism Context；Revision、Txn、Reservation、SnapshotCut 唯一归 `coordination`；Facade 只转发查询或命令，不缓存第二份可变状态。
- GAS Ability/Effect/Attribute/Tag 状态、Handle、Prediction Context、Snapshot/Restore。
- Replication Projection/History/Apply 的通用语义和 Hot Gameplay ModuleScope 契约。

Server/Client Host 负责 Wall Clock、进程和连接；Runtime 不声明 Tick Clock 的驱动所有权。Game 只提供初始化、内容注册和语义 Migration Hook。

## 子模块

模块架构总入口见 [modules/README.md](modules/README.md)（模块地图、依赖方向、线程/队列拓扑、核心流程与决策门）；各模块边界契约在各自 README。

| 子模块 | 责任 | 首批状态 |
| --- | --- | --- |
| [`ecs`](modules/ecs/README.md) | Entity、Component、Query、Storage、Change Tracking | P0 |
| [`simulation`](modules/simulation/README.md) | Logical TickId、Phase Graph、Processor Scheduler、Determinism | P0 |
| [`command`](modules/command/README.md) | CommandBuffer、Deferred Token、稳定合并和结构提交 | P0 |
| [`coordination`](modules/coordination/README.md) | CrossWorldTxn、Reservation、Revision、Snapshot Cut | P0 |
| [`replication`](modules/replication/README.md) | Projection、Mapping Runtime、Baseline/Delta Apply、Dirty Set | P0 |
| [`gas`](modules/gas/README.md) | Ability/Effect/Attribute/Tag Core 和 Prediction Context | P1 |
| [`persistence`](modules/persistence/README.md) | Snapshot/WAL 接口、Canonical Serializer、恢复协作 | P1 |
| [`config`](modules/config/README.md) | Schema 编译产物、配置层级和不可变 Tick Snapshot | P1 |
| [`observability`](modules/observability/README.md) | Event Schema、Metrics、Trace、Failure Bundle API | P1 |
| [`hot-reload`](modules/hot-reload/README.md) | ModuleScope、Quiesce/Cancel/Drain/Unload 协议 | P1 |
| [`testing`](modules/testing/README.md) | Reference Host、Replay/Hash、Scenario Adapter | P1 |

## 职责

- 提供 ECS 语义、Processor Descriptor、Query、CommandBuffer、Change Tracking 和 Snapshot Projection。
- 拥有 Logical Tick/Phase Graph、确定性排序、Barrier、错误和取消语义。
- 提供 `NetEntityId + LocalEntityId`、生命周期、Tombstone、Ownership Revision 和 Mapping 接口。
- 在 Tick Barrier 通过 `IVoxelWorldPort` 协调 GameWorld/VoxelWorld 的 `CrossWorldTxnV1`。
- 提供宿主无关 GAS Core、PredictionFrame、Authority Confirmation、Rollback 和 Snapshot/Restore。
- 提供持久化、配置、日志/Trace/Failure Bundle 的稳定抽象，不把具体 Sink、数据库或云平台绑定进 Runtime。
- 提供 Hot Reload ModuleScope、资源注册、取消、泄漏验证和 Migration Hook。

## 明确不负责什么

- 不拥有 Voxel Chunk/Block/Revision 权威数据，不依赖 VoxelEngine 内部源码。
- 不实现 Socket、Connection、Endpoint、Server 进程、WorldSlot Host 或平台 Renderer。
- 不驱动 Wall Clock，不拥有 Release Pool 或生产滚动更新编排。
- 不包含具体 Ability、Formula、经济、任务、关卡、UI 或内容资产。
- 不把 Server/Client 合并成一个 World，也不要求 Component 对称。
- 不允许 Hot Gameplay 长期持有 Native Handle、裸指针、Timer Delegate 或未登记 Task。

## World 与 Tick 契约

每个 World 有独立 Storage 和 Entity 命名空间。Host 调用 Runtime 的单一 Tick 入口；Runtime 按以下阶段执行：

```text
IngressCapture -> DecodeAndCanonicalize -> ApplyInputs
-> ProcessorPlan -> CrossWorldPrepare -> NativeJobBarrier
-> CommitDecision -> VoxelCommit -> EcsCommandBufferCommit
-> GasAndEventFinalize -> ReplicationProjection
-> SnapshotHashMetrics -> EgressPublish
```

Processor 必须声明 `ProcessorId/Role/Phase/Query/ReadSet/WriteSet/MayEmitStructuralCommands/Dependencies/DeterminismClass/Budget/DiagnosticName`。声明 `MayEmitStructuralCommands` 的业务 Phase Processor 只发出结构命令；结构变化仅由 Runtime Commit Executor 在 `EcsCommandBufferCommit` 实际应用。V1 权威 World 单线程写入；只有无共享写集和稳定归并规则的任务可并行。所有队列有界，Native Completion 只能在 Barrier 应用。

## Entity 与非对称 Mapping

- `NetEntityId` 为 128 位不透明组合 ID，预留 AuthorityDomain、WorldEpoch、Sequence、Generation；Session 内不复用。
- `LocalEntityId` 为当前 World 的 Index+Generation，不能作为网络身份。
- Destroy 产生 Tombstone，保留窗口不小于未确认 Baseline 窗口、保留 Delta History、断线重连窗口、Prediction 回滚窗口和 Migration/Replay pin 的最大值（下界公式见 replication 模块）；Respawn 默认新 ID；预测临时 ID 在确认时重映射。
- Mapping 声明 Source/Target Entity、Component、Field、Role、Owner、AOI、Initial/Continuous、Reliability、Quantization、Prediction 和 Add/Remove/Tombstone。

## CrossWorldTxnV1 与 Revision

Runtime 是 Coordinator 语义所有者。事务携带 `SessionId/TxnId/TickId/CommandId/PredictionKey/ExpectedGameRevision/ExpectedVoxelRevision/DeadlineTick`，状态为 `Created -> Prepared -> CommitIntent -> Committed`；`Prepared -> Aborted/Expired`；`Indeterminate` 仅能从已持久化 `CommitIntent` 的 Apply 阶段进入。

Prepare 只做验证和有租约 Reservation：ECS 参与者在 Prepare 完成全部业务校验并生成不可变 `PreparedGameDelta`；`CommitIntent` 之后 ECS Apply 只能返回 `Applied/AlreadyApplied/Indeterminate/Faulted`，不得业务拒绝。Commit 在固定 Barrier 按 `VoxelCommit -> EcsCommandBufferCommit` 顺序幂等 Apply，并在首个 Apply 前写入 `CommitIntent`、每步追加结果、最后写入 `Committed` 标记。`SessionRevisionVector` 同时记录 `TickId、GameRevision、VoxelWorldRevision、ChunkRevisionSet、ReplicationRevision、ConfigRevision、SchemaEpoch`。结果丢失和崩溃由 Txn Journal/状态查询处理，不在 Rust 锁内调用 C#。

## GAS Framework

Runtime 定义 Ability/Effect/Attribute/Tag 生命周期、TypeId/InstanceId/Handle、Stack/Duration/Cancel、Modifier 求值、PredictionKey、确认/拒绝和回滚上下文；Game 定义具体 Formula、Cost、Cooldown、Targeting 和表现事件。GAS 状态与 ECS 的单一真相、Snapshot Projection、State Hash 和热更迁移必须在 API Contract 中明确。复杂 Trigger Graph、Formula VM 和跨 Ability 求解器为 P2。

## Replication、Prediction 与 LocalEmbedded

Runtime 拥有复制语义；Server/Client 拥有传输适配。权威 Delta 处理顺序为验证 Baseline/Revision → 恢复 Confirmed PredictionFrame → 原子应用 ECS/GAS/Voxel → 删除已确认命令 → 重放未确认命令 → 输出表现差异。

LocalEmbedded 的两 Role 使用不同 World、不同 LocalEntityId 和完整序列化/反序列化路径；InMemoryTransport 可绕过 Socket，但不能绕过 Schema、Envelope、权限、大小限制、队列和 Tick 交付。ReferenceVoxelPort 用于 PureHeadless 语义测试。

## 持久化、序列化与配置

- Runtime 定义 Snapshot Cut、Canonical Serializer、WAL/Command Log Adapter、Checkpoint 和恢复接口；领域 Schema 由 Voxel/Game 提供。
- Snapshot/Replay 使用版本、长度、Hash/Checksum、压缩和可选加密元数据；对象地址和运行时遍历顺序不得进入 Hash。
- 配置源经 Schema 编译为 typed binary table；Engine→Platform→Server→Product→Environment→User/Session 优先级固定。
- Tick 使用不可变配置快照；开发可热载，生产通过签名版本显式切换。

## 日志与观测

Runtime 只定义统一 Event Schema 和关联 API；具体 Sink 由 Host 提供。Managed 侧使用成熟日志框架：Diagnostic/Trace/Metrics 使用有界异步队列、批量写入和 Error/Fatal 应急路径；Audit/TxnJournal/CommandLog 使用独立耐久路径，不得静默丢失。所有事件携带 Release、Session、World、Tick、Txn、Entity、Prediction 和 Trace 关联字段。

## Hot Reload 与故障隔离

每个 Gameplay Assembly 使用独立 `GameplayModuleScope` 登记 Timer、Task、Subscription、Native Lease 和 Channel Registration。卸载顺序为 `Quiesce -> Cancel -> Drain -> Dispose -> ValidateRoots -> Unload`，超时进入失败/回滚或 Session 重启。可捕获 Gameplay Exception 可降级为 Session Fault；CoreCLR 崩溃、Stack Overflow、OOM 按进程级故障处理。

## Source / Compile-Time Dependencies

- .NET SDK、C# 编译器和经过供应链审查的托管基础包。
- `LumioEngineSDK` 生成的 Native Contract/BuildInfo，仅经稳定 Managed Adapter 使用。
- Voxel 只通过 `IVoxelWorldPort`/Generated Contract；不依赖 Server、Client 或 Game 实现源码。
- 第三方库通过 Adapter 隔离，不能进入公共 Runtime 类型。

## Generated Contract Dependencies

消费 Native/Voxel ABI、Handle、Batch、Capability 和 Error 元数据；注册 Game 生成的 Component/RPC/Mapping Schema。生成物记录 Compiler、Input Hash、Output Hash，只读且可从干净来源重建。

## Runtime Loading Relationships

```text
Server/Client Host
  -> stable LumioGameRuntime
  -> role-specific GameWorld / 客户端 World
  -> ServerGameplay.dll or ClientGameplay.dll
  -> Voxel Port / GAS / generated contracts
```

Runtime 不实际创建 CoreCLR；Host 负责 CoreCLR/ALC。LocalEmbedded 的 Server/Client Gameplay 使用独立可回收 ALC，共享稳定 Runtime。

## Release Composition Relationships

Runtime 发布 `RuntimeApiSchemaVersion`、GAS/Serialization/HotReload Contract 和 Artifact Hash；Server/Client/Game Manifest 必须锁定这些版本。Runtime 不决定 ProductId、GameRelease 语义或滚动路由。

## Room Modes / Host Profiles

Runtime 只消费正交 Capability：Role、Native、Voxel、Transport、Clock、Replay、Renderer、AOT、Resource Budget。Preset 包括 `PureHeadless`、`NativeHeadless`、`LocalEmbedded`、`LocalSplitProcess`、`RemoteDS`、`MobileLocal`；Gameplay 不读取模式布尔值。

## Headless Test Surface

- ECS/Query/CommandBuffer/Processor/Determinism/Revision/Entity Property 和 Golden Test。
- CrossWorldTxn 的 Duplicate、Timeout、Conflict、Lost Result、Crash Fixture。
- GAS 生命周期、PredictionFrame、Authority Confirm、Rollback、Snapshot/Hash。
- ReferenceVoxelPort 与 Native Voxel Differential、Local Transport Fidelity、Replay 首差异。
- Hot Reload 100 次 Soak、ALC/Task/Timer/Handle 泄漏和异常隔离。
- 日志背压、配置优先级、Snapshot/WAL 恢复和 Failure Bundle 重建。

## Version / Manifest

Manifest 至少包含 Runtime Commit、API/Schema、Serialization、GAS、HotReload、平台、Artifact Hash、Capability 和依赖 CoreEngine。结构不匹配时 Host 拒绝加载；语义兼容由 Game 显式声明。

## 开源优先与供应链

优先复用成熟 ECS 辅助、并发、序列化、日志和测试框架；通过 Adapter、锁定版本/Commit、许可证审查、SBOM、漏洞、AOT、确定性和性能门槛管理。默认优先宽松许可证，不能把未验证依赖写成稳定 API。

## 当前阶段与开发节奏

1. **Architecture Gate**：冻结 ECS/Tick/Entity/Revision/Replication/HotReload 语义和失败矩阵。
2. **Foundation**：实现 `ecs/simulation/command/coordination` 单线程闭环和 Reference Host。
3. **Vertical Slice**：接入 GAS、Voxel Port、LocalEmbedded、Persistence、Config、Replay 和日志证据。
4. **Production Hardening**：预测校正、故障注入、热更 Soak、性能和跨平台 Host Matrix。
5. **P2**：复杂 GAS、可替换 Storage、Mod 挂接和更多并行优化。
