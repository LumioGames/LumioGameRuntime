# observability 模块

> 定义 Runtime Event、Metrics、Trace、Audit 和 Failure Bundle 的统一关联与有界输出抽象。

**优先级**：P1
**实施阶段**：Vertical Slice / Production Hardening
**架构基线**：`LGE-V1.4-2026-08-27`

## 模块定位与目标

`observability` 让 ECS、Tick、Txn、Replication、GAS、Persistence 和 Hot Reload 输出可关联、可采样、可恢复的证据，而不让 Simulation Thread 等待外部 Sink。它定义事件承载和队列策略，最终文件/控制台/外部平台由 Host Adapter 提供。

## 负责什么

- 消费架构源 `LoggingEvent`/`FailureBundle` Schema，组装统一 Event、Metrics、Trace 和 Failure Bundle 片段。
- 维护 Diagnostic、Audit、TxnJournal、CommandLog、Metrics、Trace、FailureBundle 的类别、级别、Durability 和关联字段；Txn/Command 的恢复游标与耐久提交由 `persistence` 负责。
- 提供有界异步 Diagnostic 队列、独立持久队列的接口、采样/丢弃原因和 Error/Fatal 应急路径。
- 关联 `ProductId`、`GameReleaseId`、`SessionId`、`WorldId`、`TickId`、`TxnId`、`NetEntityId`、`PredictionKey`、`SnapshotId`、`TraceId`、`ProducerId`、`EventSeq`。
- 生成可校验的 Failure Bundle 元数据，引用 Manifest、Snapshot、Artifact Hash 和 Replay 命令；无有效 Snapshot 的故障以 `noSnapshotReason + bootstrapPhase + lastKnownRevision/manifest` 表达。

## 明确不负责什么

- 不拥有具体日志 Sink、轮转、保留、外部 SDK、权限或最终 PII 策略；这些由 Host/部署 Adapter 决定。
- 不替代 `persistence` 的 Snapshot/WAL/TxnJournal/CommandLog 恢复语义，也不把 Diagnostic 当作权威状态。
- 不决定 Session、World、Pool 或进程级故障处置，不阻塞 Simulation Thread 等待远端写入。
- 不在事件正文中复制 Secret、密钥、完整用户数据或未脱敏外部输入。

## 拥有的状态与资源

- Producer 注册、EventSeq、关联上下文、采样策略和类别路由。
- 有界 Diagnostic/Trace/Metrics 批队列，以及 Audit/Txn/Command 持久队列的适配状态。
- Failure Bundle 组装上下文、Artifact/Snapshot 引用、Hash/Checksum 和导出状态。
- 队列水位、丢弃计数、Sink 错误、应急落盘标记和 Flush/Shutdown 状态。

## 输入、输出与稳定接口

- **输入**：模块事件、Metrics 样本、Trace Span、Failure Context、关联字段和 Host Sink Adapter。
- **输出**：版本化 Event Batch、指标/Trace 批、Failure Bundle、Queue/Flush Result 和稳定背压错误。
- **候选接口**：`emit`、`record_metric`、`start_span`、`append_failure_artifact`、`flush`、`snapshot_context`；具体载荷以架构源 Schema 为准。
- 每个 Producer 的 EventSeq 必须单调；跨线程不承诺实时全局顺序，重建依赖 Producer/EventSeq + Tick 关联。

## 上游与下游依赖

- **上游**：所有 Runtime 模块产生结构化事件；`config` 提供采样/容量快照；Host 提供 Sink/权限/脱敏策略。
- **下游**：Host 文件/控制台/外部 Sink、`testing` 的 Failure Bundle/Replay 工具、运维分析系统。
- `observability` 是只读/数据输出基础，不依赖任何业务模块，不回调 ECS、Tick 或 Gameplay 改变状态。

## 生命周期与状态机

```text
Created -> Configured -> Running -> Flushing -> Closed
Running -> Degraded（采样/丢弃）-> Running
任一状态 -> Faulted
```

持久队列在 `Flushing` 完成或明确失败后才能关闭；Diagnostic 队列可按策略丢弃，但必须记录丢弃原因和计数。

## 线程、队列与并发所有权

- Producer 可以来自 Simulation、Native Completion、Persistence Worker 和 Host 回调；所有输入先复制为受限值对象。
- Diagnostic/Trace/Metrics 使用有界异步队列；Audit/Txn/Command 使用独立耐久队列的路由/适配状态，实际恢复游标与提交由 `persistence` 负责，满载时停止接入或进入维护，不静默丢失。
- Error/Fatal 具备同步应急路径，但不得在持有 World/Native 锁时等待 Sink。
- Flush/Shutdown 由 Host 编排；关闭后的 Producer 事件被拒绝并带稳定原因。

## 正常数据流与失败路径

1. 模块创建带公共 Correlation 的 Event/Metric/Trace，先做长度、字段和脱敏校验。
2. 根据类别/级别写入对应有界队列，分配 EventSeq；Diagnostic 可采样并记录策略。
3. Sink Worker 批量写出，保存每 Producer 顺序、Tick 关联和失败重试信息。
4. 出现事务/崩溃/恢复时组装 Failure Bundle，验证 Manifest/Snapshot/Artifact Hash 后导出；尚无有效 Snapshot 时（如 ABI/Capability 启动失败、首个 Snapshot 前的故障）携带 `noSnapshotReason + bootstrapPhase + lastKnownRevision/manifest`，不得伪造 SnapshotId。

事件字段缺失、超长、非法关联、队列满、Sink 失败和关闭竞态必须有可观察结果；不能用“日志写成功”代替事务提交。

## 错误分类、恢复与降级

- **可拒绝**：Schema/关联/长度/脱敏校验失败、关闭后写入、非法类别或不可接受敏感字段。
- **可重试**：瞬时 Sink/IO 失败、Diagnostic 批发送失败；重试保持 EventSeq 和幂等标记。
- **可致命**：Audit/Txn/Command 持久队列无法保证耐久、Failure Bundle 校验不一致或内部顺序不变量破坏；通知 Host 进入维护/故障。
- 普通 Diagnostic/Trace 可采样或丢弃；Audit、TxnJournal、CommandLog 和已确认 Failure Bundle 不得静默丢失。

## 配置、Capability 与安全约束

- 队列容量、采样、保留、Sink、Flush Deadline 和脱敏策略来自 Config/Host Capability；不在事件中硬编码环境秘密。
- PII/Secret 在入队前按 Host 策略脱敏；Failure Bundle 只包含必要的 Hash/引用，访问需要权限。
- 网络时间戳、队列状态和 Sink 延迟可进 Diagnostic Hash，不进入权威 Simulation Hash。

## 日志、Metrics、Trace 与 Audit

本模块本身输出队列深度、吞吐、丢弃/采样率、Flush 延迟、Sink 错误、EventSeq Gap、Failure Bundle 大小和重建耗时；业务模块的事件类别、Durability 和 Correlation 由架构源 Schema 约束。

## 测试面、故障矩阵与性能指标

- **测试面**：事件字段/版本/长度、Correlation、EventSeq、采样、并发生产、队列满载、Sink 失败、应急路径和 Failure Bundle 重放。
- **故障矩阵**：Diagnostic QueueFull、Audit/Txn QueueFull、磁盘满、Sink 超时、脱敏失败、Producer 乱序、关闭后写入和 Artifact Hash 错误。
- **性能指标**：事件吞吐、入队/Flush p50/p95/p99、队列等待、批大小、丢弃率、分配、Sink 延迟和 Simulation Thread 额外开销。

## 对应 ADR、Schema 与 Fixture

- 架构源 `docs/adr/ADR-011-observability.md`、`schemas/logging-event.schema.json`、`schemas/failure-bundle.schema.json`。
- 正例：`fixtures/valid/logging-audit.json`、`fixtures/valid/failure-bundle.json`；反例：`fixtures/invalid/logging-audit-missing-correlation.json`、`fixtures/invalid/failure-bundle-bad-hash.json`。

## 尚未批准的决策门

- **RT-D-009**：外部 Sink、PII/保留策略、Diagnostic 队列容量和 Durable Queue 背压；受架构源 D-008 与日志 Soak 结果约束。
- Failure Bundle 的本地目录与上传方式属于 Host/运维部署选择；Runtime 只冻结可校验结构和引用关系。
- 任何改变事件类别、Durability 或公共 Correlation 字段的变更必须回到架构源 Schema/ADR。
