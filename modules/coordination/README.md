# coordination 模块

> 统一编排 CrossWorldTxnV1、Reservation、Revision Vector 和 Tick Barrier 的 SnapshotCut。

**优先级**：P0
**实施阶段**：Architecture Gate / Foundation
**架构基线**：`LGE-V1.3-2026-08-27`

## 模块定位与目标

`coordination` 是 Runtime 的跨状态域一致性边界。它协调 GameWorld/ECS 与版本化 `IVoxelWorldPort`，让跨 World 操作在固定 Tick Barrier 中可验证、幂等、可恢复，并为 Replication、Persistence 和 Replay 提供同一套 Revision 观察。对外的 `SimulationSession` 由 `simulation` 组装，本模块是其 Revision/Txn 状态委托。

## 负责什么

- 拥有 `SessionRevisionVector` 的读取、比较和 SnapshotCut 固定语义；SnapshotCut 固定时必须包含 Voxel Snapshot Token/Revision（经 Generated Voxel Snapshot Contract 获取不可变引用），Runtime 不复制 Voxel Storage。
- 实现 `CrossWorldTxnV1` 状态机：`Created -> Prepared -> CommitIntent -> Committed`；`Prepared -> Aborted/Expired`；`Indeterminate` 仅从已持久化 `CommitIntent` 的 Apply 阶段进入。
- 编排 Game/ECS 与 Voxel Port 的 Prepare、租约 Reservation、Commit、Abort 和结果查询。
- 在首个参与者写入前记录 `CommitIntent`，按 `VoxelCommit -> EcsCommandBufferCommit` 固定顺序幂等提交。
- 处理 `TxnId` 重复、Deadline、取消、Revision Conflict、Lost Result 和 Crash Recovery 查询。

## 明确不负责什么

- 不拥有 Voxel Chunk/Block/Revision Storage，不调用 VoxelEngine 内部实现。
- 不拥有 ECS Component Storage 或 CommandBuffer 内容；只消费 `ecs`/`command` 提供的提交接口。
- 不实现 WAL/文件/数据库后端（归 `persistence`），也不把通用 XA/2PC 引入 V1。
- 不读取 Host Wall Clock、Socket、Connection 或 Gameplay 具体权限/Formula。
- 不在 Rust/Native 锁内调用 C#，不让异步 Worker 回调 Hot Gameplay。

## 拥有的状态与资源

- 每个 `SimulationSession` 的 `SessionRevisionVector`、SnapshotCut Pin/Token 和读取有效期。
- Txn 元数据、Expected/Observed Revision、Reservation 租约、参与者 Token 和幂等结果。
- `CommitIntent`、参与者状态标记（枚举 `NotStarted / Unknown / Applied / Failed`，不使用 Boolean）、`Aborted/Expired`/`Indeterminate` 原因和恢复查询索引。
- Barrier 内的 Commit 顺序、Deadline 和取消状态；不复制参与者的领域数据。

## 输入、输出与稳定接口

- **输入**：Session/World Context、CommandId/PredictionKey、Expected Revision、Voxel Port Reservation、ECS CommandBuffer Result 和 DeadlineTick。
- **输出**：Prepare/Commit/Abort Result、`SessionRevisionVector`、参与者状态枚举、SnapshotCut Token、稳定失败原因。
- **候选接口**：`read_revision`、`begin_snapshot_cut`、`prepare_txn`、`commit_txn`、`abort_txn`、`resolve_txn`；参数与持久化载荷以架构源 Schema 为准。
- 所有结果都带 `TxnId`/`SessionId` 关联；重复请求必须返回原结果，不重复扣费或写入。

## 上游与下游依赖

- **上游**：`ecs`/`command` 的提交视图、生成的 `IVoxelWorldPort`、`simulation` 的 Barrier/Logical Tick 和 `observability` 事件端口。
- **下游**：`replication` 消费 Revision/Commit 结果；`persistence` 持久化 Journal、SnapshotCut 和恢复输入；`gas` 通过 Processor 发起跨域请求。
- `coordination` 不依赖 `persistence` 的具体实现；耐久写入通过接口注入，避免事务锁与存储后端耦合。

## 生命周期与状态机

Session 协调器生命周期：

```text
Created -> Ready -> Running -> Draining -> Disposed
任一状态 -> Faulted
```

单笔事务状态以架构源为准：

```text
Created -> Prepared -> CommitIntent -> Committed
Prepared -> Aborted/Expired
已持久化 CommitIntent 的 Apply 阶段 -> Indeterminate
```

`Indeterminate` 只能从已持久化 `CommitIntent` 的 Apply 阶段进入；尚未写入 `CommitIntent` 的事务只能走 `Aborted/Expired`。进入 `Draining` 后停止新 Prepare；在途事务必须完成、明确 Abort 或留下可查询的 `Indeterminate` 证据。

## 线程、队列与并发所有权

- Revision 读取、Prepare 决策和 Commit 顺序由 Simulation Owner Thread 在指定 Barrier 执行。
- Voxel/Native/IO 参与者可以异步准备，但只返回有界、带 Revision/Token 的结果；Completion 在 Barrier 消费。
- Txn Journal 写入通过有界持久队列完成；在 `CommitIntent` 未被确认持久化前不得开始第一个权威写入。
- Reservation 租约只能由协调器和参与者按声明的 Owner/Deadline 释放，不能被后台线程静默延长。

## 正常数据流与失败路径

1. Processor 以 Expected Revision 创建 Txn，协调器校验 Session、权限前置条件、容量和 Deadline。
2. Game/ECS 与 Voxel Port 分别 Prepare：ECS 参与者在 Prepare 完成全部业务校验（Generation、目标存在性、容量、命令冲突、权限、预算），产出不可变 `PreparedGameDelta`；Voxel Port 生成 Reservation Token；均不产生可见副作用。
3. 所有前置检查成功后，在第一步 Apply 前写入 `CommitIntent`，再执行 Voxel Commit。
4. 记录 Voxel 参与者结果后执行 ECS CommandBuffer Commit；`CommitIntent` 后参与者 Apply 不得业务拒绝，只能返回 `Applied/AlreadyApplied` 或基础设施级 `Indeterminate/Faulted`；两步完成后写 `Committed` 和新 Revision Vector。
5. 结果丢失时按 TxnId 查询状态；崩溃恢复按 Journal 标记只重放尚未完成的幂等步骤。参与者 Apply 成功但状态标记尚未落盘的崩溃窗口内，该参与者标记视为 `Unknown`，恢复通过幂等参与者查询确认真实结果后收敛为 `Applied/Failed`，不得猜测。

Revision Conflict、Chunk Unloaded、权限/资源失败、超时、取消和重复 Txn 都必须在可见写入前结束；崩溃发生在两参与者之间时保持 `Indeterminate`，不得猜测成功或失败。

## 错误分类、恢复与降级

- **可拒绝**：Revision 不匹配、Chunk 不可用、权限/资源不足、非法 Token、Deadline 已过。
- **可重试**：暂时未取得 Reservation 或状态查询暂不可用；重试必须使用同一 `TxnId` 并保持幂等。
- **可致命**：Journal 损坏、参与者顺序不变量失败或 Session 状态不可恢复；进入 `Faulted`，由 Host/Runtime 重建。
- 不提供“补偿事务”作为默认降级；只有架构源明确的参与者标记才能决定恢复动作。

## 配置、Capability 与安全约束

- Reservation TTL、Txn/队列预算和 SnapshotCut 资源上限来自 ConfigSnapshot/Host Capability，不使用墙上时间推断逻辑 Tick。
- TxnId、CommandId、PredictionKey 和 Revision 必须经过 Schema/权限校验；不接受调用方自定义提交顺序。
- Journal/Failure Bundle 可能含敏感业务字段，进入 `observability` 前按 Host 脱敏策略处理。

## 日志、Metrics、Trace 与 Audit

记录 Txn 状态迁移、Prepare/Commit 延迟、Reservation 数量、Revision Conflict、Indeterminate、Journal 延迟和恢复重放步数。`TxnJournal`/`CommandLog` 属独立耐久类别；Diagnostic 丢弃不能影响恢复证据。

## 测试面、故障矩阵与性能指标

- **测试面**：Revision 单调性、SnapshotCut 一致读取、Prepare/Commit/Abort、重复 Txn、Deadline、取消和幂等。
- **故障矩阵**：Revision Conflict、Chunk Unloaded、Lost Result、CommitIntent 写后崩溃、两参与者之间崩溃、Journal 损坏和 QueueFull。
- **性能指标**：Prepare/Commit p50/p95/p99、Barrier 等待、Journal 延迟、Reservation 峰值、恢复时间和每 Tick Txn 数。

## 对应 ADR、Schema 与 Fixture

- 架构源 `docs/adr/ADR-003-cross-world-txn.md`。
- `schemas/cross-world-txn.schema.json`、`schemas/session-revision-vector.schema.json` 和 `schemas/common.schema.json`。
- 正例：`fixtures/valid/cross-world-txn-committed.json`、`fixtures/valid/cross-world-txn-aborted.json`；反例：`fixtures/invalid/cross-world-txn-partial-commit.json`、`fixtures/invalid/session-revision-negative.json`。

## 尚未批准的决策门

- **RT-D-004**：TxnJournal 保留窗口、Reservation 租约长度和状态查询存储；必须通过 Partial-Commit/Lost-Result/Crash Fixture。
- **RT-D-007**：Journal 与 Snapshot 的耐久级别由 `persistence`/Host 测量确认，不能在协调器内硬编码后端。
- 任何改变 Commit 顺序、状态名、Revision 含义或幂等规则的变更都必须回到架构源并生成新 Baseline。
