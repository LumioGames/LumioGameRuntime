# replication 模块

> 管理非对称 Mapping、Snapshot/Delta Projection、Baseline/History、Dirty Set 和权威 Apply/Resync 语义。

**优先级**：P0
**实施阶段**：Architecture Gate / Foundation
**架构基线**：`LGE-V1.0-2026-08-27`

## 模块定位与目标

`replication` 是 Runtime 的复制语义所有者。它把独立 Server/Client World 投影为版本化 FullSnapshot/Delta，并在 Client 侧把权威 ECS、GAS 和 Voxel Overlay 作为一个确认/回滚单元应用；网络字节传输由 Host Adapter 负责。

## 负责什么

- 消费 Game 生成的 Mapping，校验 Source/Target Component/Field、Role、Owner、Visibility、Delivery、Lifecycle 和 Prediction。
- 管理 NetEntityId 与 LocalEntityId 的映射、Tombstone、provisional remap 和生命周期窗口。
- 生成 Projection、FullSnapshot、Delta、Baseline Ack、History 和 Dirty Set；按 Revision 截取稳定视图。
- 校验 Baseline/Revision/Sequence，处理 Gap、Unknown Baseline、History Exhaustion 和 Full Resync。
- 在 Client Apply 时恢复 Confirmed PredictionFrame，原子应用 ECS/GAS/Voxel 权威状态，删除已确认命令并按序重放未确认命令。

## 明确不负责什么

- 不实现 Socket、TLS、Connection、Transport ACK、分片或网络 Reactor（归 Host）。
- 不定义具体 Component、GAS Formula、权限策略或 AOI 业务规则；只执行已生成 Mapping 的声明。
- 不拥有 Server/Client 的全部 World Storage，不把 LocalEntityId 当作网络身份。
- 不绕过 Schema、Envelope、大小限制或权限路径，即使在 LocalEmbedded 中也一样。

## 拥有的状态与资源

- 每个 Role/World 的 Mapping Registry、Net/Local 映射表、Tombstone 保留窗口和 provisional remap 表。
- 每个 ReplicationContext 的 SnapshotId、Baseline、Revision History、Dirty Set、Ack 状态和 Resync 原因。
- Projection/Apply 的临时批次、Prediction Confirmed Frame 引用和有界历史缓存。
- 复制统计、Gap/Resync 计数和已确认命令序号；不持有 Transport 句柄。

## 输入、输出与稳定接口

- **输入**：稳定 World/GAS/Voxel Projection、Mapping 生成物、Revision Vector、Baseline Ack、Delta/Resync 请求和 Prediction History。
- **输出**：FullSnapshot/Delta Projection、Apply Result、Baseline/Delta Ack、Resync Request、Mapping/Entity 错误和表现差异。
- **候选接口**：`build_snapshot`、`build_delta`、`apply_envelope`、`ack_baseline`、`request_resync`、`remap_entity`；Envelope 字段以架构源 Schema 为准。
- Projection 与 Apply 都返回 `SnapshotId`/Revision 关联；未知或过期输入不得静默接受。

## 上游与下游依赖

- **上游**：`ecs`/`gas` 的投影 Provider、`coordination` 的 Revision/SnapshotCut、`config` 的不可变快照和 Game Mapping Contract。
- **下游**：Host 的网络/Local Transport Adapter、Client Presentation Adapter、`persistence` 的 Replay/Snapshot Provider。
- `replication` 可以消费 `command` 的确认序号，但不依赖 Host Connection 或 `testing`；Transport ACK 与 Baseline ACK 分开。

## 生命周期与状态机

单个 ReplicationContext：

```text
Created -> Snapshotting -> AwaitingBaselineAck -> Active
Active -> Resyncing -> Active
Active/Resyncing -> Draining -> Closed
任一状态 -> Faulted
```

Server/Client 绑定不同 World 和 LocalEntityId；Context 销毁后迟到 Delta、旧 Baseline 和旧映射全部失效。

## 线程、队列与并发所有权

- Projection 和权威 Apply 在 Simulation Owner Thread 的规定 Phase/Barrier 执行，保证 ECS/GAS/Voxel 原子确认。
- Transport/IO 线程只把已校验的 Envelope 放入有界 Ingress；不能直接调用 Apply。
- History、Dirty Set、Projection Batch 有数量和字节上限；达到上限进入显式 Full Resync/断开策略。
- Snapshot Cut 读取可以由后台编码，但 Completion 只能在声明 Barrier 发布，且不得污染后续 Revision。

## 正常数据流与失败路径

1. Handshake 后生成可靠 FullSnapshot，等待独立 BaselineAck；Transport ACK 不代替 BaselineAck。
2. 活跃期间按 `BaseSnapshotId + FromRevision -> ToRevision` 生成 Delta，Dirty Set 只包含声明 Mapping 可见字段。
3. Client 校验 Envelope/Mapping/Baseline/Revision，恢复最近 Confirmed Frame 后原子应用权威状态。
4. 删除已确认命令，按原序重放未确认命令，输出 Presentation Diff；Gap、未知 Baseline、旧 Revision、Schema 不匹配或 Tombstone 冲突请求 Full Resync。

Malformed/oversized Envelope 必须在分配前拒绝；不能用“尽力合并”掩盖缺包、重复或过期状态。

## 错误分类、恢复与降级

- **可拒绝**：Envelope 格式/长度/完整性错误、Mapping 不匹配、无权限字段、旧 Revision、Tombstone 冲突。
- **可重试**：传输暂不可用、Baseline 尚在等待或 History 可满足的重复 Ack；重试保持序号和 Idempotency。
- **可致命**：Projection/Apply 原子性或映射不变量损坏；进入 `Faulted`，由 Host 重新建 Context。
- History 不足、Gap 无法修补或 Baseline 未知时只允许 Full Resync/重新握手，不静默丢 Delta。

## 配置、Capability 与安全约束

- AOI/Owner/Delivery/History/Batch 上限由生成 Mapping、ConfigSnapshot 和 Host Capability 组合决定。
- 只接受签名/Hash 校验通过的 Mapping 与 Schema；字段过滤发生在 Projection/Apply 边界。
- LocalEmbedded 可以绕过 Socket/TLS，但不得绕过 Envelope、Serializer、权限、大小限制、有界队列和 Tick 交付。

## 日志、Metrics、Trace 与 Audit

记录 Snapshot/Delta 字节、Baseline/Delta Ack、History 命中、Dirty Set 大小、Gap/Resync、重映射和预测校正。事件带 `SessionId`、`WorldId`、`TickId`、`SnapshotId`、`NetEntityId`、`PredictionKey` 和 `TraceId`；网络 Sink 由 Host 提供。

## 测试面、故障矩阵与性能指标

- **测试面**：非对称 Component/Mapping、Spawn/Despawn/Tombstone、provisional remap、FullSnapshot/Delta、Baseline Ack、Prediction Confirm/Rollback 和 Replay。
- **故障矩阵**：丢包、乱序、重复、Gap、Unknown Baseline、旧 Revision、History Exhaustion、Schema/Mapping mismatch、Tombstone 冲突和 Apply 中断。
- **性能指标**：Projection/Apply p50/p95/p99、复制字节、Dirty Set 大小、Resync 率、History 内存、重放命令数和表现差异延迟。

## 对应 ADR、Schema 与 Fixture

- 架构源 `docs/adr/ADR-004-entity-identity.md`、`docs/adr/ADR-005-replication-prediction.md`。
- `schemas/replication-envelope.schema.json`、`schemas/replication-mapping.schema.json`、`schemas/entity-identity.schema.json`。
- 正例：`fixtures/valid/replication-full-snapshot.json`、`fixtures/valid/replication-delta.json`、`fixtures/valid/replication-mapping.json`；反例：`fixtures/invalid/replication-gap-without-resync.json`、`fixtures/invalid/replication-mapping-empty-field.json`、`fixtures/invalid/entity-reused-tombstone.json`。

## 尚未批准的决策门

- **RT-D-005**：Dirty Set 表示、History 保留窗口、Baseline 内存预算和 Full Resync 触发阈值；必须通过丢包/乱序/重连 Soak。
- Mapping 生成器与 Runtime Apply 的程序集边界由 Game Contract Toolchain 冻结；Runtime 不自行增加字段或枚举。
- N/N-1 Release 兼容仍受架构源 D-007 约束，未获新 ADR 前只接受精确 Release 匹配。
