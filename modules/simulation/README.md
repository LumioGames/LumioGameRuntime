# simulation 模块

> 拥有 Logical TickId、13 相 Phase Graph、Processor Scheduler 和 Determinism Context。

**优先级**：P0
**实施阶段**：Architecture Gate / Foundation
**架构基线**：`LGE-V1.0-2026-08-27`

## 模块定位与目标

`simulation` 是 Runtime 的 Tick 语义和执行编排边界。它接收 Host 触发的 Tick 入口，按固定阶段建立 Processor 计划、消费有界输入、在 Barrier 应用结果，并输出可重放、可诊断的逻辑结果；它不拥有 Host 的 Wall Clock。

## 负责什么

- 创建和推进 `SimulationSession` 的 Logical `TickId`、Phase Graph、暂停/排空状态。
- 校验并排序 `ProcessorDescriptor` 的 Query、ReadSet、WriteSet、StructuralWrites、Dependencies、DeterminismClass、Budget 和 DiagnosticName。
- 执行架构源定义的 13 相：

  ```text
  IngressCapture -> DecodeAndCanonicalize -> ApplyInputs
  -> ProcessorPlan -> CrossWorldPrepare -> NativeJobBarrier
  -> CommitDecision -> VoxelCommit -> EcsCommandBufferCommit
  -> GasAndEventFinalize -> ReplicationProjection
  -> SnapshotHashMetrics -> EgressPublish
  ```

- 固定输入归一化、迟到分类、Processor 依赖和有序归并规则，维护 Level 1/Level 2 Determinism 证据。
- 在规定 Barrier 调用 `coordination`、`gas`、`replication`、`persistence` 和观测 Provider。

## 明确不负责什么

- 不读取或驱动 Host Wall Clock、Socket、Renderer、进程信号或 Release Pool。
- 不直接拥有 ECS Storage、CommandBuffer 内容、Voxel 内部状态或网络传输队列。
- 不让 Native Worker、IO 回调或 Gameplay 线程直接改变 World；所有异步结果必须经有界队列和 Barrier。
- 不定义具体 Ability、Formula、Mapping、配置业务含义或平台模式分支。

## 拥有的状态与资源

- `SimulationSession` 状态、Logical `TickId`、Phase/Processor 注册表和已验证的执行计划。
- Determinism Context（RNG Stream、时间单位、事件排序和 Hash 输入声明）。
- 当前 Tick 的输入批次、预算计数、Barrier 状态、取消/暂停标记和 Tick 结果摘要。
- Processor 级耗时、命令数、队列水位和失败证据索引。

对外 `SimulationSession` 由本模块提供生命周期 Facade；Revision Vector、Txn 和 SnapshotCut 的实际状态由 `coordination` 持有，本模块只在 Phase/Barrier 中调用其接口。

## 输入、输出与稳定接口

- **输入**：Host Tick 请求、已入队的 Ingress Batch、ProcessorDescriptor、ConfigSnapshot、Capability 和 Native Completion Batch。
- **输出**：Tick Result、Revision/Hash 摘要、Replication/Egress Batch、Diagnostic/Failure 事件和稳定 Tick 错误。
- **候选接口**：`initialize_session`、`plan_processors`、`run_tick`、`pause`、`resume`、`drain`；具体签名须与 Runtime API Schema 一起冻结。
- Host 调用单一 Tick 入口；本模块不能通过额外入口绕过 Phase Graph。

## 上游与下游依赖

- **上游**：Host 提供调用时机、Role/Capability 和输入批次；`config` 提供不可变 Tick Snapshot；`observability` 提供事件端口。
- **下游**：`ecs` 执行 Query、`command` 产生结构命令、`coordination` 处理事务、`gas` 处理框架阶段、`replication` 投影结果。
- `simulation` 可以编排 `persistence` 的 Snapshot Provider，但 `persistence` 不得反向依赖 Tick 调度实现。

## 生命周期与状态机

```text
Created -> Initialized -> Ready -> Running <-> Paused
Running/Paused -> Draining -> Snapshotted -> Disposed
任一活动状态 -> Faulted
```

只有 Host/所属 Session Owner 可以发起生命周期迁移；Processor 不能自行暂停、销毁或改变 Host 状态。

## 线程、队列与并发所有权

- V1 每个权威 WorldSlot 一个 Simulation Owner Thread，负责计划、写入和 Barrier 提交。
- Network、IO、Native Worker、平台回调只写有界 Queue/Batch；`NativeJobBarrier` 之前不得应用其结果。
- 仅当 WriteSet 不重叠且存在稳定归并顺序时允许并行；Worker 数量和调度算法不属于公共契约。
- Tick 超预算、队列满载和取消必须在当前阶段停止或按声明策略降级，不能部分应用结构写入。

## 正常数据流与失败路径

1. `IngressCapture` 固定输入批次和 ArrivalClass，`DecodeAndCanonicalize` 校验生成契约。
2. `ApplyInputs` 后生成 Processor Plan；读写冲突、依赖环或未知阶段在执行前拒绝。
3. Processor 写入自己的 CommandBuffer，跨 World 请求进入 `CrossWorldPrepare`；Native 结果在 Barrier 汇合。
4. 依次提交 Voxel/ECS、Finalize GAS/事件、Projection、Snapshot/Hash/Metrics，最后发布 Egress。

迟到输入明确归入当前 Tick、下一 Tick 或拒绝；计划失败、Native Completion 超时、Tick Deadline 超限和 World 已销毁都必须留下首个失败阶段和关联证据。

## 错误分类、恢复与降级

- **可拒绝**：Processor Schema 不完整、依赖环、Read/Write 冲突、错误 Role/Phase、迟到输入不可接受。
- **可重试**：Native/IO Completion 暂未就绪、可恢复的 Queue 暂满；按 Deadline 重新排程而不重复提交。
- **可致命**：Determinism 不变量、Barrier 顺序或 Session 状态损坏；进入 `Faulted`，由 Host 恢复/重建。
- Budget overrun 只报告和按 Host 策略处置，不把未提交的结构操作伪装成成功。

## 配置、Capability 与安全约束

- TickRate/Deadline/预算来自 Host 与 `config` 的不可变快照；Wall Clock 只由 Host 读取。
- Processor、Role 和 Required Capability 必须在激活前匹配；Gameplay 不读取 `IsOffline`/`IsLocal`。
- 输入先经过版本、长度、权限和资源限制校验；本模块不执行外部脚本或未经签名的 Processor。

## 日志、Metrics、Trace 与 Audit

每 Tick 记录 `TickId`、Phase、ProcessorId、预算、队列水位、Determinism Level、State Hash 和首个失败点。Diagnostic 可采样；Txn/Command/Audit 证据由 `observability`/`persistence` 独立承载，不以普通日志代替。

## 测试面、故障矩阵与性能指标

- **测试面**：Phase 顺序、Processor 计划、Read/Write 冲突、依赖环、迟到输入、取消/暂停/恢复、确定性 Replay 和首差异定位。
- **故障矩阵**：QueueFull、Native Completion 超时、Tick 超预算、World 销毁竞态、重复 Tick、Processor 异常和 Hash 差异。
- **性能指标**：Tick p50/p95/p99/max、各 Phase/Processor 耗时、命令数、Barrier 等待、队列深度、GC/分配和 Hash 成本。

## 对应 ADR、Schema 与 Fixture

- 架构源 `docs/adr/ADR-001-session-lifecycle.md`、`docs/adr/ADR-002-tick-determinism.md`。
- `schemas/processor-descriptor.schema.json`：正例 `fixtures/valid/processor-place-voxel.json`，反例 `fixtures/invalid/processor-read-write-conflict.json`。
- Logical Tick/Phase 和 Determinism 的公共字段只以架构源 Baseline 生成物为准。

## 尚未批准的决策门

- **RT-D-001**：Tick 编排 API 与 C# 程序集拆分方式；必须保持单一 Host Tick 入口。
- **RT-D-003**：并行 Worker、预算统计和超限处置的实现参数；只允许不改变可观察顺序的优化。
- **RT-D-011**：Reference Host 与 Native/真实 Host 的差异报告格式；需通过 Replay/Differential 验证后确认。
