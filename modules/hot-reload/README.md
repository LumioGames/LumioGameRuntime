# hot-reload 模块

> 管理 GameplayModuleScope、资源登记、Quiesce/Cancel/Drain/Dispose/ValidateRoots/Unload 协议和迁移 Hook。

**优先级**：P1
**实施阶段**：Vertical Slice / Production Hardening
**架构基线**：`LGE-V1.3-2026-08-27`

## 模块定位与目标

`hot-reload` 为 Role-specific Gameplay Assembly 提供可回收的资源边界。它把 Timer、Task、Subscription、Native Lease 和 Channel Registration 纳入 `GameplayModuleScope`，在热更、Session 销毁或失败时按固定顺序排空并验证根引用，避免旧代码继续改变 Runtime 状态。

## 负责什么

- 创建/登记每个 Gameplay Assembly 的 `GameplayModuleScope` 和 Resource Lease。
- 跟踪 Timer、Task、Subscription、Native Handle/Lease、Channel Registration、回调和取消令牌。
- 执行 `Quiesce -> Cancel -> Drain -> Dispose -> ValidateRoots -> Unload`，报告超时、泄漏和未完成任务。
- 维护热更的双 Scope 原子切换：新 Scope 在 Staging 创建、验证、迁移，旧 Scope 在 BarrierSwitch 之后才排空卸载；切换点前后失败各有唯一恢复动作。
- 在 Schema/Release 允许时调用 Game 提供的语义 Migration Hook；保留旧 Scope 证据和失败回滚入口。
- 为 Host/CoreCLR ALC 提供稳定的 Scope 状态、Unload Result、Root 验证和 Failure Bundle 片段。

## 明确不负责什么

- 不创建 CoreCLR/ALC、进程、WorldSlot、Wall Clock 或生产滚动更新（归 Host）。
- 不热更稳定 Runtime、ECS Storage、公共 ABI、Wire/Serialization Schema 或 NativeCore 包。
- 不替 Game 决定业务迁移、Formula、权限、资源补偿或 Release 兼容；只执行已声明 Hook 和约束。
- 不允许 Gameplay 长期持有未经登记的 Native 裸指针、Timer Delegate、Task、Socket 或跨 Scope 引用。

## 拥有的状态与资源

- Scope Registry、Assembly/Release 关联、资源种类、所有者、取消令牌和 Lease 过期状态。
- Quiesce/Cancel/Drain/Dispose/Root Validation/Unload 的状态与 Deadline。
- 迁移输入/输出 Snapshot 引用、旧 Scope 保留标记、泄漏清单和失败证据。
- Unload 后的失效句柄和拒绝表；不拥有 ALC 外部的 Host 资源。

## 输入、输出与稳定接口

- **输入**：已验证的 Gameplay Assembly/Manifest、Scope 注册请求、Resource Lease、Quiesce/Unload 命令、Migration Hook 和 Config/Capability。
- **输出**：Scope 状态、注册/释放结果、Drain/Root Validation 报告、Migration Result、稳定超时/泄漏错误。
- **候选接口**：`create_scope`、`register_resource`、`quiesce`、`cancel`、`drain`、`validate_roots`、`unload`、`migrate`；具体参数需与 Host/Runtime Contract 一起冻结。
- 每个异步结果都带 ScopeId、Generation 和 Release 关联；Unload 后回调只能得到稳定的 `ScopeClosed`/`StaleLease` 结果。

## 上游与下游依赖

- **上游**：Host/CoreCLR Host 提供 ALC 和生命周期命令，`simulation` 提供 Tick/Quiesce 边界，`gas`/Game 提供内容 Hook，`observability` 记录证据。
- **下游**：Gameplay Assembly、Native/Voxel Adapter 和测试工具消费 Scope/Lease API；`persistence` 只消费迁移结果，不依赖卸载实现。
- `hot-reload` 不依赖具体 Game 代码、网络或 Storage；Native ABI 通过稳定 Managed Adapter 使用。

## 生命周期与状态机

单个 Scope 生命周期：

```text
Created -> Loaded -> Active
Active -> Quiescing -> Cancelling -> Draining
Draining -> Disposing -> ValidatingRoots -> Unloaded
任一阶段 -> Faulted
```

热更采用双 Scope 原子切换状态机：

```text
OldActive + NewStaging -> NewValidated -> BarrierSwitch -> OldQuiescing -> OldUnloaded
```

- `BarrierSwitch` 前任一失败（加载、验证、迁移）：丢弃 NewStaging，OldActive 不变，唯一恢复动作是保持旧 Scope 继续服务。
- `BarrierSwitch` 后失败：不得重新激活已排空/Dispose 的旧 Scope；Session 进入 `Faulted`，从有效 Snapshot/Release 恢复。
- Migration 只读不可变 Snapshot，在 Staging 生成结果，不修改 OldActive 的在用状态。
- 所有入口、订阅、Timer、Task 和 Native Lease 在切换点按 Scope Generation 线性化；旧 Generation 的迟到结果被拒绝。

排空阶段的超时或 Root 泄漏进入 `Faulted`；不得把未验证的旧 Scope 标记为 Unloaded，也不得在 Faulted 状态继续接收 Gameplay 调用。

## 线程、队列与并发所有权

- Scope 状态迁移由 Host/Simulation Owner Thread 发起；Task/Timer/IO Worker 只能报告完成/取消事件到有界队列。
- `Drain` 等待的任务、Native Lease 和回调都有 Deadline；回调执行期间不能取得会阻塞卸载的 Runtime/Native 锁。
- 新资源在 `Quiescing` 后拒绝注册；取消与完成竞态按 Generation/Scope 状态线性化。
- Scope 不把可变 World 引用放入长生命周期对象；Unload 后所有旧异步 Completion 都被拒绝。

## 正常数据流与失败路径

1. Host 校验 Manifest/ABI/Capability 后创建 Scope，Gameplay 注册所有可持有资源。
2. Active 期间资源通过 Scope 申请/释放，未登记的资源在诊断或根验证中报告。
3. 热更先在 Staging 创建并加载新 Scope，OldActive 保持服务；Migration Hook 只读不可变 Snapshot，在 Staging 生成新 Scope 状态；全部验证通过后进入 NewValidated。
4. 在声明的 Tick Barrier 原子切换入口/订阅到新 Scope（BarrierSwitch），随后旧 Scope 按 `Quiesce -> Cancel -> Drain -> Dispose -> ValidateRoots` 排空，验证通过后交给 Host 卸载 ALC。
5. `BarrierSwitch` 前失败或超时丢弃 NewStaging、保留 OldActive 与证据；切换后失败不重新激活旧 Scope，Session 进入 Faulted 并按 Host 策略从有效 Snapshot/Release 恢复；Session 销毁（非热更）直接对当前 Scope 执行排空协议。

## 错误分类、恢复与降级

- **可拒绝**：未登记资源、Scope/Generation 不匹配、Unload 后调用、Manifest/ABI/Capability 不兼容。
- **可重试**：Drain 尚未完成且仍在 Deadline 内；重试不得重新注册或重复释放已完成资源。
- **可致命**：Root 泄漏、ALC 无法卸载、Native Lease 失效不一致、迁移产物校验失败；进入 Faulted 并由 Host 处置。
- 不能通过跳过 Root Validation 或强制保留旧回调来“降级”继续运行；只允许回退到最近有效 Scope/Release。

## 配置、Capability 与安全约束

- Scope/Drain Deadline、资源配额、允许的 Native Capability 和 Migration 版本来自签名 Manifest/ConfigSnapshot。
- Gameplay 只能访问 Role、Capability、Port 和登记的 API；不提供 Socket、文件系统、进程或任意反射权限。
- 生产热更必须验证 Assembly/Manifest Hash、签名、SchemaEpoch 和资源预算；开发 Hot Load 也不能绕过生命周期协议。

## 日志、Metrics、Trace 与 Audit

记录 Scope/Assembly/Release、资源注册/释放、Quiesce/Drain 时长、未完成 Task/Timer/Lease、Root 数量、Migration 节点和 Unload 结果。泄漏、超时、回滚和强制重启写 Audit/Failure Bundle；普通状态写 Diagnostic。

## 测试面、故障矩阵与性能指标

- **测试面**：Scope 生命周期、资源登记/释放、取消竞态、Drain Deadline、Root Validation、ALC 回收、Migration Hook 和异常隔离。
- **故障矩阵**：未登记 Task/Timer/Handle、回调晚到、Native Lease 失效、Drain 超时、ALC 残留、Migration 崩溃、重复 Unload 和 OOM。
- **性能指标**：Scope 创建/卸载 p50/p95/p99、Drain 时间、资源数量、ALC/Managed Heap、GC、泄漏计数和连续 100 次热更稳定性。

## 对应 ADR、Schema 与 Fixture

- 架构源 `docs/adr/ADR-006-native-managed-abi.md`、`docs/adr/ADR-014-platform-capability.md`、架构源 §13/§14。
- `schemas/native-managed-abi.schema.json`、`schemas/host-capability.schema.json`、`schemas/failure-bundle.schema.json`。
- 结合 `fixtures/valid/native-managed-abi.json`、`fixtures/invalid/native-managed-abi-pointer-width.json`、`fixtures/valid/host-capability.json` 做加载/拒绝验证；热更泄漏 Fixture 属 Runtime 测试产物。

## 尚未批准的决策门

- **RT-D-010**：Drain/Root Validation 超时参数与 Session/Process 故障升级策略（BarrierSwitch 前后失败的唯一恢复动作已在生命周期节冻结）；必须通过 100 次 Soak 和 ALC/资源泄漏证据。
- HybridCLR/平台 AOT 仅作为 Capability 适配，不是 Runtime 稳定核心的实现依赖；Server HybridCLR 仍受架构源 D-006 约束。
- 改变卸载顺序、Scope 资源种类或 Migration Hook 兼容语义必须新增 ADR 并更新 Release/Schema 说明。
