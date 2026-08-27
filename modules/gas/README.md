# gas 模块

> 提供宿主无关的 Ability、Effect、Attribute、Tag 生命周期、Handle 和 Prediction Context Framework。

**优先级**：P1
**实施阶段**：Vertical Slice
**架构基线**：`LGE-V1.3-2026-08-27`

## 模块定位与目标

`gas` 是 Runtime 的通用 Gameplay Ability System Framework。它管理可预测、可快照、可回滚的生命周期和执行上下文，并把权威可复制字段投影到 ECS；具体玩法内容由 `LumioGame` 提供。

## 负责什么

- 定义 Ability/Effect/Attribute/Tag 的 Framework 生命周期、TypeId/InstanceId/Handle 区别和资源配额。
- 执行 Stack、Duration、Cancel、Modifier 求值顺序的框架约束，并提供确定性 Evaluation Hook。
- 管理 PredictionKey、PredictionFrame、Authority Confirmation、拒绝校正、Rollback 和 Snapshot/Restore 上下文。
- 通过 `command` 产生 ECS 结构/字段变化，保持 ECS 为权威可复制状态的单一真相。
- 为 Replication、Persistence 和 Hot Reload 提供 GAS 投影、状态 Hash 和迁移接口。

## 明确不负责什么

- 不定义具体 Ability、Formula、Cost、Cooldown、Targeting、Permission、经济或表现事件（归 Game）。
- 不创建第二份独立于 ECS 的权威属性存储，不直接修改 VoxelWorld 或 Connection。
- 不拥有 Host Clock、网络协议、Renderer、脚本 VM 或跨 Ability 的复杂求解器（P2）。
- 不允许未知 TypeId、未登记资源、任意反射或外部脚本绕过生命周期校验。

## 拥有的状态与资源

- Framework Type Registry、Ability/Effect/Tag Handle 索引和实例关联。
- Modifier/Duration/Stack 的瞬时执行上下文、PredictionFrame 和确认窗口。
- ECS 投影描述、Snapshot/Restore 临时状态、取消令牌和每 Tick 预算。
- Game 内容注册的只读引用；不拥有内容程序集的 ALC、Timer 或 Task（归 `hot-reload` Scope）。

## 输入、输出与稳定接口

- **输入**：已验证的 Game Content Descriptor、Activation Request、Target/Cost 输入、PredictionKey、ConfigSnapshot 和 ECS View。
- **输出**：Activation/Effect Result、ECS CommandBuffer 命令、Framework Event、Prediction Correction、Snapshot/State Hash 投影。
- **候选接口**：`register_type`、`activate`、`apply_effect`、`cancel`、`tick_effects`、`capture_snapshot`、`restore`；具体签名与内容 Schema 由架构源/Game Contract 共同冻结。
- 失败结果必须说明未知类型、无效 Handle、配额、Formula/权限拒绝或预测冲突，不返回部分应用的效果。

## 上游与下游依赖

- **上游**：`ecs` 的权威 Component View、`command` 的结构/字段命令、`config` 的不可变数值表、Game 生成的 Content Schema。
- **下游**：`simulation` 在 `GasAndEventFinalize` 调用；`replication` 投影确认状态；`persistence` Snapshot/Restore；`hot-reload` 管理内容 Scope。
- `gas` 不依赖网络/Host 实现或具体 Game 程序集，内容通过版本化注册接口注入。

## 生命周期与状态机

Framework 生命周期：

```text
Unloaded -> Registered -> Ready -> Running -> Draining -> Unloaded
任一状态 -> Faulted
```

Ability/Effect 使用 Runtime 冻结的 V1 通用状态机——状态名集合冻结，转移表细节以架构源 ADR-008 为准，Game 内容不得增删状态名。

Ability 实例：

```text
Requested -> Activated -> Executing -> Completed
Requested/Activated -> Rejected
任一非终态 -> Cancelled
Executing -> Expired
预测实例被权威拒绝 -> RolledBack
```

终态（`Completed/Rejected/Cancelled/Expired/RolledBack`）即 Handle 失效。

Effect 实例：

```text
Pending -> Active -> Expired | Removed
Pending -> Rejected
预测回滚 -> RolledBack
```

Stack/Duration/Refresh 是 `Active` 内事件，不是独立生命周期状态。

Game 内容只能在 `Executing`/`Active` 内定义业务子状态，不得改变通用转移、终止语义、回滚窗口和 Handle 失效规则。Hot Reload 时先停止新激活，再处理在途 Frame。

## 线程、队列与并发所有权

- 权威 GAS 状态在 Simulation Owner Thread 的 `ApplyInputs`、Processor 和 `GasAndEventFinalize` 阶段读写。
- 预测/回滚历史属于对应 Replica World，不跨 Role 共享；异步 Formula/Native 结果先进入有界 Completion Batch。
- Ability 激活、Effect 堆叠和 Event 数量受 Config/Processor Budget 限制；队列满载必须返回稳定失败。
- Native/Gameplay Worker 不回调正在卸载的 Assembly；所有资源由 `GameplayModuleScope` 登记。

## 正常数据流与失败路径

1. Game 注册稳定 TypeId/版本和输入声明，Runtime 校验 Role、权限所需上下文和资源预算。
2. Server 或 Client 在各自 World 建立 Activation/PredictionFrame；Effect/Attribute 改变先形成 ECS Command。
3. Server 在固定 Barrier 应用并产生权威确认；Client 收到确认后恢复 Confirmed Frame，原子应用 ECS/GAS/Voxel，删除已确认命令并重放余下输入。
4. Snapshot/Hot Reload 读取或迁移 Framework Context；任何异常都产生可重放的 Correction/Failure 证据。

未知类型、无效 Handle、Stack/配额超限、Formula 错误、权限失败和 Prediction 拒绝都不得留下半个 Modifier 或重复扣费。

## 错误分类、恢复与降级

- **可拒绝**：Type/Handle 未知、状态不允许、资源/配额不足、权限/Formula 失败、PredictionKey 过期。
- **可重试**：确认尚未到达或依赖的 Snapshot/Config 尚未激活；重试沿同一 PredictionFrame，不重复执行已提交效果。
- **可致命**：ECS/GAS 单一真相不变量、State Hash 或恢复数据损坏；隔离 Session/World 并保留 Failure Bundle。
- 非权威表现事件可采样/丢弃；权威 Attribute/Effect 结果不能静默丢失。

## 配置、Capability 与安全约束

- Stack 上限、Duration、Prediction 窗口和 Formula 输入预算来自签名 Config/Content；Tick 使用不可变快照。
- Game Formula 只能通过受约束的注册/调用接口进入，不能取得 Socket、Native 裸指针、文件路径或宿主权限。
- Server/Client Role 的权限和确认路径分别校验；LocalEmbedded 不得使用旁路 Gameplay API。

## 日志、Metrics、Trace 与 Audit

记录 Ability/Effect 激活、Stack/Cancel/Expire、Prediction Confirm/Reject、Rollback、Formula 错误、资源消耗和 State Hash。业务 Audit 与 Diagnostic 分离，事件带 `SessionId`、`WorldId`、`TickId`、`NetEntityId`、`PredictionKey` 和 `TraceId`。

## 测试面、故障矩阵与性能指标

- **测试面**：Type/Handle、激活/堆叠/取消/过期、Modifier 顺序、Snapshot/Restore、ECS 单一真相、Prediction Confirm/Reject/Rollback 和热更迁移。
- **故障矩阵**：未知类型、无效 Handle、配额超限、Formula/权限失败、重复确认、History 溢出、Config/Content 不匹配和 Scope 卸载竞态。
- **性能指标**：每 Tick 激活/Effect 数、Evaluation p50/p95/p99、Prediction History 内存、Rollback 重放时间、命令数、GC/分配和 Event 吞吐。

## 对应 ADR、Schema 与 Fixture

- 架构源 `docs/adr/ADR-008-gas-state.md`、`docs/adr/ADR-005-replication-prediction.md`。
- Content/Mapping Schema 由 Game 生成并在 Runtime 注册；公共基础字段引用 `schemas/entity-identity.schema.json`、`schemas/common.schema.json`。
- Foundation/Vertical Slice 至少覆盖 Ability 激活、Prediction 拒绝、Snapshot Restore、权限失败和 Save/Load Golden；具体 Fixture 随 Game Contract 发布。

## 尚未批准的决策门

- **RT-D-006**：GAS Framework Index 与 ECS Component 的具体投影布局、Modifier 求值扩展点和 State Hash 范围；必须通过生命周期/回滚 Fixture。
- 复杂 Trigger Graph、Formula VM 和跨 Ability 求解器保持 P2，不得为 V1 引入隐式脚本依赖。
- 任何改变 Modifier 顺序、TypeId、Prediction 窗口或内容兼容语义的变更必须由 Game Migration 和新 Release 明确承载。
