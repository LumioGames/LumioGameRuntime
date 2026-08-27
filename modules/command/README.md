# command 模块

> 管理 Processor 专属 CommandBuffer、Deferred Entity Token、稳定合并和结构提交。

**优先级**：P0
**实施阶段**：Foundation
**架构基线**：`LGE-V1.3-2026-08-27`

## 模块定位与目标

`command` 把 ECS 结构变化从 Processor 执行阶段延迟到固定 Barrier。它提供可审计、可排序、可取消的结构命令边界，使同一 Tick 的 Create/Write/Destroy 行为在 Server、Client、Replay 和 LocalEmbedded 中保持一致。

## 负责什么

- 为每个 Processor 创建独立的 CommandBuffer，并校验其声明的 `MayEmitStructuralCommands`。
- 表达 Create、Add/Remove Component、Set Field、Destroy 等结构命令和 Deferred Entity Token。
- 按 `Phase + ProcessorId + LocalSequence` 稳定合并，解决跨 Buffer 的可见顺序。
- 在 `EcsCommandBufferCommit` 应用结构变化，返回映射后的 Entity、Change Set 和幂等结果。
- 明确同 Tick Create/Write/Destroy、无效目标、重复命令、取消和容量上限语义。

## 明确不负责什么

- 不直接拥有 Component Storage、Entity Generation 或 Query 计划（归 `ecs`）。
- 不决定 Tick 阶段、Processor 依赖或并行调度（归 `simulation`）。
- 不执行跨 World Commit、Voxel Mutation、网络 RPC、GAS Formula 或持久化写入。
- 不允许 Processor 绕过 Buffer 直接修改结构，也不把未提交命令当作权威状态。

## 拥有的状态与资源

- 当前 Tick/Phase/Processor 的 Open、Sealed、Merged、Prepared、Applied/Discarded Buffer。
- Deferred Token 到实际 LocalEntityId 的临时映射和失效状态。
- 命令序列号、容量/字节预算、合并结果和拒绝原因。
- 结构提交期间的幂等键与诊断摘要；不保存跨 Tick 的未登记可变引用。

## 输入、输出与稳定接口

- **输入**：Processor Context、Query 结果、结构命令、Tick/Phase、目标 Entity Token 和预算。
- **输出**：Sealed Buffer、稳定合并批、Commit Result、Deferred Token 映射、Change Set 和错误。
- **候选接口**：`open_buffer`、`append`、`seal`、`merge`、`prepare`、`commit`、`discard`；具体签名随 ECS/Runtime API Schema 冻结。
- Buffer 的所有权在 `seal` 后转移给 Runtime 提交路径；调用方不能在提交后继续写入或重用旧序列号。

## 上游与下游依赖

- **上游**：`ecs` 提供 Entity/Component View；`simulation` 提供 Phase、ProcessorId 和 Tick Context（以值传入，不形成反向调度依赖）。
- **下游**：`simulation` 在固定 Barrier 调用合并；`coordination` 可引用已提交命令结果；`gas` 使用命令表达框架状态变化。
- `command` 可发事件到 `observability`，但不依赖 `replication`、`persistence`、`hot-reload` 或 `testing`。

## 生命周期与状态机

```text
Open -> Sealed -> Merged -> Prepared -> Applied
Open/Sealed -> Discarded
任一状态 -> Faulted（保持原始 Buffer 证据）
```

一个 Buffer 只属于一个 Tick/Phase/Processor；`Applied` 或 `Discarded` 后 Token 和写入权限失效。`Prepared` 表示 Generation、目标存在性、容量、命令冲突、权限、预算等一切业务校验已全部完成并固定为不可变结果；`Prepared` 之后不再发生业务拒绝。

## 线程、队列与并发所有权

- Processor 只在其执行上下文写自己的 Buffer；不同 Buffer 可以并行生成，但不并行改变 ECS Storage。
- 合并和提交由 Simulation Owner Thread 在声明 Barrier 执行；Native/IO 结果只能先转换为命令批。
- Buffer 数量、命令数和字节数有上限；满载按命令优先级拒绝或取消，并保留原因。
- 不在 Buffer 中保存可变 Component 引用、Native 裸指针、Timer Delegate 或跨 World 对象。

## 正常数据流与失败路径

1. `simulation` 为 Processor 打开 Buffer，Processor 写入字段/结构命令并分配稳定 LocalSequence。
2. `seal` 校验目标、权限范围、预算和结构规则；Deferred Token 只在同一提交域内有效。
3. `merge` 按固定键排序，处理同目标冲突、重复 Destroy 和 Create 后引用。
4. `prepare` 完成 Generation、目标存在性、容量、命令冲突、权限和预算等全部业务校验，生成不可变的已验证批；一切业务拒绝在本步或之前发生。
5. `commit` 调用 `ecs` 应用已 `Prepared` 的结构变化，发布映射/Change Set；参与 CrossWorldTxn 的 Buffer 在 `CommitIntent` 后 Apply 只能返回 `Applied`/`AlreadyApplied` 或基础设施级 `Indeterminate/Faulted`，不得业务拒绝。

无效目标、重复命令、超预算、跨 Tick Token、Buffer 篡改和提交后回调都必须可诊断，不能产生半个结构操作。

## 错误分类、恢复与降级

- **可拒绝（仅 `Prepared` 前生效）**：目标 Generation 不匹配、命令类型未声明、依赖顺序非法、重复或超限；`Prepared` 之后不再出现业务拒绝。
- **可重试**：等待的 Deferred Token 尚未解析且仍在同一 Barrier；不能跨 Tick 静默等待。
- **可致命**：合并排序不稳定、Commit 原子性不变量失败或 ECS Storage 损坏；进入 `Faulted` 并保留 Buffer 证据。Apply 阶段的基础设施故障表达为 `Indeterminate/Faulted`，由 TxnJournal/参与者查询恢复，不表达为业务拒绝。
- 只能丢弃非权威/诊断命令；权威结构命令必须明确拒绝并让上层决定事务失败。

## 配置、Capability 与安全约束

- `maxCommands`、`maxBytes` 和结构权限来自 Processor Descriptor/ConfigSnapshot。
- Command 载荷必须来自已验证 Schema；不执行字符串表达式、脚本或任意反射调用。
- Deferred Token 不能当作 `NetEntityId`，也不能越过 Role/World 边界。

## 日志、Metrics、Trace 与 Audit

记录每 Processor Buffer 数量、命令/字节数、合并耗时、冲突类型、Token 失效和 Commit 结果。命令正文按敏感级别脱敏；需要恢复的 Command Log 由 `persistence` 维护，普通 Diagnostic 不能替代。

## 测试面、故障矩阵与性能指标

- **测试面**：稳定排序、同 Tick Create/Write/Destroy、Deferred Token、重复命令、冲突、取消、容量和提交原子性。
- **故障矩阵**：无效 Entity、跨 World Token、Buffer 篡改、QueueFull、Processor 异常、提交中止和重复重放。
- **性能指标**：Buffer 写入吞吐、合并 p50/p95/p99、命令字节、结构提交耗时、分配和峰值队列。

## 对应 ADR、Schema 与 Fixture

- 架构源 `docs/adr/ADR-002-tick-determinism.md`：`ProcessorId + Phase + LocalSequence` 合并顺序和结构提交边界。
- 架构源 `schemas/processor-descriptor.schema.json`：`mayEmitStructuralCommands`、Budget 和依赖声明。
- CrossWorld 结果关联 `schemas/cross-world-txn.schema.json`；命令本身的内部载荷在 Runtime Contract 冻结前不另立公共 Schema。

## 尚未批准的决策门

- **RT-D-003**：Deferred Token 编码、同目标冲突策略、Buffer 容量、`prepare` 校验批次边界和取消竞态；必须有同 Tick、「`Prepared` 后不可业务拒绝」与 Crash/Replay Fixture。
- **RT-D-001**：`command` 是否独立程序集；无论物理布局如何，Processor 不得绕过本模块。
- 任何新增命令类型若跨仓传输或进入持久化，必须先回到架构源登记 Schema/ID。
