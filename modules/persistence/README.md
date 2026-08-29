# persistence 模块

> 提供 Snapshot/WAL/Command Log 的稳定接口、Canonical Encode/Decode、Checkpoint 和恢复编排。

**优先级**：P1
**实施阶段**：Vertical Slice
**架构基线**：`LGE-V1.4-2026-08-27`

## 模块定位与目标

`persistence` 把 Runtime 的内存状态转换为可校验、可重放、可迁移的持久化证据。它消费 `coordination` 固定的 SnapshotCut，协调各状态 Provider 的 Canonical 编码和 WAL/Command Log 恢复；具体文件、数据库或对象存储由 Host Adapter 提供。

## 负责什么

- 接收不可变 `SnapshotCut + SessionRevisionVector`，协调 ECS/GAS/Replication/Config Provider 的一致读取；Voxel 经 Generated Voxel Snapshot Contract 参与——获取不可变引用/Chunk Manifest（内容寻址），Runtime 不复制 Voxel Storage，Host 只提供耐久介质。
- 提供版本化 Canonical `Encode`/`Decode`、Snapshot Header、Hash/Checksum、Compression 和可选加密元数据校验；Snapshot Manifest 记录所有参与者的 Revision、Hash、SchemaEpoch 和 Provider Result。
- 编排 Snapshot Staging、验证、fsync/原子激活、Checkpoint 保留和旧版本读取。
- 消费架构源 `TxnJournalRecord`/`CommandLogRecord` 记录契约（RecordVersion、RecordSeq、Session/Release/Tick/Txn/Command 关联、RecordKind、IdempotencyKey、PreviousHash、PayloadHash、Length、Durability 状态、Checksum），定义 WAL/Command Log/TxnJournal Adapter 的输入输出、恢复顺序和幂等重放边界；`LoggingEvent` 不得替代恢复记录。
- 发现损坏、截断、未知必需字段、重复字段、版本不兼容或解压预算超限时拒绝激活，并保留 Failure Bundle 证据。

## 明确不负责什么

- 不拥有 Revision/Txn 状态机或 SnapshotCut 语义（归 `coordination`）。
- 不定义 ECS、GAS、Voxel、Mapping 或 Config 的领域字段；Schema 由对应所有者和架构源维护。
- 不绑定文件系统、数据库、对象存储、压缩/加密供应商或云平台；全部经 Adapter 隔离。
- 不在 Tick 热路径执行不可控 IO，不把 JSON/文本导出当作权威存储，也不执行输入中的代码。

## 拥有的状态与资源

- Snapshot Capture/Encoding/Staging/Verified/Active/Invalid 状态和 `SnapshotId` 元数据。
- Canonical Codec 上下文、版本/Hash/Checksum 校验结果和有界 Decode 预算。
- WAL/Command Log/TxnJournal 的写入队列、Checkpoint 指针、恢复游标和幂等记录。
- 失败 Staging 目录、损坏原因、重放首差异和 Failure Bundle 引用；不持有 World 可变引用。

## 输入、输出与稳定接口

- **输入**：`SnapshotCut`、Provider Snapshot、Command/Txn 记录、Snapshot Header、目标 Schema/Release 和 Storage Adapter。
- **输出**：Canonical bytes、验证后的 typed Snapshot、Checkpoint/Recovery Result、重放命令批和稳定持久化错误。
- **候选接口**：`capture`、`encode`、`decode`、`stage`、`activate`、`append_log`、`recover`、`verify`；公共字段以架构源 Schema 为准。
- Decode 必须先校验 Magic、SchemaVersion、Length、Hash/Checksum、Compression、分配上限和边界，再 materialize typed 状态。

## 上游与下游依赖

- **上游**：`coordination` 提供 SnapshotCut/Revision（含 Voxel Snapshot Token/Revision），`ecs`/`gas`/`replication`/`config` 提供稳定 Provider，Voxel 经 Generated Voxel Snapshot Contract 提供不可变引用/Chunk Manifest，`observability` 提供证据事件。
- **下游**：Host `PersistenceAdapter` 提供耐久介质；`testing` 消费 Canonical/Recovery 结果；Migration 工具消费 Staging Snapshot。
- `persistence` 不依赖 Host 的具体生命周期实现，也不反向改变 `simulation`、`coordination` 或 Provider 的状态所有权。

## 生命周期与状态机

Snapshot 生命周期：

```text
Idle -> Capturing -> Encoding -> Staged -> Verified -> Active
                                      \-> Invalid
```

恢复生命周期：

```text
Opening -> CheckpointVerified -> LogScanning -> Replaying -> Recovered
                                      \-> RecoveryFailed
```

激活失败保留旧 Active 指针；迁移/恢复不得覆盖唯一有效 Snapshot。

## 线程、队列与并发所有权

- SnapshotCut 固定和 Provider 读取在 Simulation Barrier 发起；编码、校验和 IO 可在有界 Worker 上执行。
- WAL/TxnJournal/CommandLog 使用独立的有界持久队列；队列满载时停止新权威接入或进入维护，不能静默丢失。
- Decode/解压必须有最大消息、分配和压缩比限制；异步结果只以不可变批次返回。
- 任何 Worker 都不能直接写 ECS/GAS/Voxel 或调用 Hot Gameplay；恢复应用在所属 Barrier/重建阶段完成。

## 正常数据流与失败路径

1. `coordination` 在 Barrier 固定 SnapshotCut，Provider 返回带 Revision 的不可变视图。
2. `persistence` 生成 Header 和 Canonical bytes，执行长度、Hash/Checksum、压缩和资源上限校验。
3. 写入 Staging，完成 fsync/原子替换和 Checkpoint 保留后才把指针标记 Active。
4. 崩溃恢复从最近有效 Checkpoint 开始，只重放带提交标记的 WAL/Command/Txn 记录；`Indeterminate` 按 Journal 查询解决。
5. 迁移在独立 Staging 输入上运行，失败保留源 Snapshot 和节点证据，不能半激活。

## 错误分类、恢复与降级

- **可拒绝**：Magic/Schema/Length/Hash/Checksum 错误、未知必需字段、重复字段、版本不兼容、超分配或解压比。
- **可重试**：暂时 IO 错误、队列暂满、可恢复的 Checkpoint 读取失败；重试不能重复激活或重复应用命令。
- **可致命**：所有有效 Checkpoint 损坏、Canonical 不变量破坏或恢复证据不一致；进入 RecoveryFailed，交由 Host 维护/重建。
- 无法证明提交状态时保持 `Indeterminate`，不得把“日志缺失”当作成功或失败的猜测。

## 配置、Capability 与安全约束

- Snapshot/WAL 频率、保留、压缩、加密、最大分配和磁盘预算来自签名 Config/Host Capability；临时参数不写入公共 Schema。
- 外部输入先做元数据和边界校验，再分配和反序列化；不执行脚本、不跟随未验证路径或资源引用。
- Secret/密钥材料不进入普通 Snapshot、日志或 Failure Bundle；加密由 Host Adapter 按 Release 策略提供。

## 日志、Metrics、Trace 与 Audit

记录 SnapshotId、Revision Vector、编码/解码耗时、字节/压缩比、Checkpoint、队列深度、fsync 延迟、恢复游标、重放首差异和失败原因。TxnJournal/CommandLog 属持久证据；Diagnostic 只引用其 ID 和 Hash。

## 测试面、故障矩阵与性能指标

- **测试面**：Canonical Round-trip、旧版本读取、Snapshot Header、Hash/Checksum、压缩、损坏/截断、原子激活、WAL 重放、Migration Staging。
- **故障矩阵**：Length mismatch、未知必需字段、重复字段、解压炸弹、磁盘满、fsync/rename 失败、Checkpoint 损坏、崩溃于每个 Journal 边界、重复重放。
- **性能指标**：Encode/Decode p50/p95/p99、吞吐、压缩比、分配、fsync 延迟、恢复时长、队列深度和 Simulation Thread 额外阻塞。

## 对应 ADR、Schema 与 Fixture

- 架构源 `docs/adr/ADR-010-persistence-config.md`、`docs/adr/ADR-003-cross-world-txn.md`、`docs/adr/ADR-013-migration-dag.md`。
- `schemas/snapshot-header.schema.json`、`schemas/common.schema.json`、`schemas/cross-world-txn.schema.json`、`schemas/txn-journal-record.schema.json`、`schemas/command-log-record.schema.json`、`schemas/wal-record-envelope.schema.json`（V1.3 已发布，记录布局以架构源 Schema 为准）；Voxel 快照参与面见 `schemas/voxel-world-port.schema.json`（`capture`/`restore`）与 `schemas/voxel-chunk-page.schema.json`（内容寻址 Chunk Manifest）。
- 正例：`fixtures/valid/snapshot-active.json`、`fixtures/valid/cross-world-txn-committed.json`；反例：`fixtures/invalid/snapshot-length-mismatch.json`、`fixtures/invalid/cross-world-txn-partial-commit.json`。

## 尚未批准的决策门

- **RT-D-007**：本地文件/目录、WAL group-commit/sync、Checkpoint 周期和保留强度；必须结合架构源 D-005 的恢复与性能证据确认。
- Canonical Codec/Compression 供应商由 Adapter 评估，不能让实现选型改变 Snapshot/WAL 字节语义。
- 任何改变 SchemaVersion、Hash、Commit Marker 或恢复顺序的变更都必须回到架构源并提供旧/新 Fixture。
