# testing 模块

> 提供 Reference Host、Replay/Hash、Scenario Adapter、契约 Fixture Runner 和确定性故障注入支持。

**优先级**：P1
**实施阶段**：Foundation / Vertical Slice
**架构基线**：`LGE-V1.3-2026-08-27`

## 模块定位与目标

`testing` 是 Runtime 的测试支持边界，不是生产运行时模块。它用 Reference Host 复现 Tick/World 语义，用 Replay/Hash 定位首个差异，用 Scenario/Fault Adapter 覆盖 PureHeadless、NativeHeadless、LocalEmbedded 和 LocalSplitProcess 的契约保真度。

## 负责什么

- 组装无网络、可控时钟的 Reference Host、GameWorld/ReplicaWorld 和 `ReferenceVoxelPort`。
- 执行 Golden、Property、Fuzz、Stress/Soak、Differential、Fault 和 Replay 测试，并保存可重放输入。
- 计算 Canonical State Hash、比较 Server/Client/Replay 结果，报告首个差异 Tick/Phase/Processor。
- 提供 Scenario Adapter 的 Required/Provided Capability 匹配、确定性 Seed、故障注入和 Workload 记录。
- 校验架构源 Schema/Fixture、生成契约和 Failure Bundle 的可重建性。

## 明确不负责什么

- 不成为任何 Runtime 生产模块的编译或运行时依赖，不在生产开启测试后门、调试开关或固定种子。
- 不修改架构源 Schema/ID/Fixture，不替代实现模块的单元测试或 Host 的真实网络/运维测试。
- 不把 Reference Host 的简化 Storage、Clock 或 Transport 当作生产实现，也不掩盖与 Native/真实 Host 的差异。
- 不读取真实密钥、用户数据或未脱敏的外部服务凭据。

## 拥有的状态与资源

- Test Session、Scenario、Capability Matrix、Determinism Seed、Workload 和 Replay 输入/输出。
- Reference Host 的 Logical Tick、World/Port Adapter、Fault Profile、Hash/首差异记录。
- Fixture Runner 的通过/拒绝结果、Failure Bundle、原始样本和 Benchmark 元数据。
- 测试临时目录、资源配额和超时；测试结束必须释放所有 World/Handle/Scope。

## 输入、输出与稳定接口

- **输入**：Scenario 描述、RequiredCapabilities、Command/Input Stream、Fixture、Fault Profile、Workload 和被测模块接口。
- **输出**：Test/Replay/Benchmark Result、State Hash、首差异报告、Failure Bundle、Scenario 不匹配原因和机器可读退出结果。
- **候选接口**：`run_scenario`、`replay`、`compare_hash`、`inject_fault`、`validate_fixture`、`run_workload`；具体命令映射以 Tooling Contract 冻结。
- 测试结果必须记录 Release/Schema/Manifest Hash、Host Profile、平台、编译器、Seed、TickRate、持续时间和失败证据。

## 上游与下游依赖

- **上游**：消费所有 Runtime 模块的公开接口、架构源 Schema/Fixture、Native/Voxel Reference/真实 Adapter 和 Host Capability。
- **下游**：CI、开发者 CLI、性能报告和 Failure Bundle 分析工具；生产 Runtime 不依赖本模块。
- 测试可以替换 Clock/Transport/Voxel Adapter，但必须经过同一 Envelope、Serializer、权限、大小限制和有界队列路径。

## 生命周期与状态机

```text
Created -> Prepared -> Running -> Collecting -> Passed/Failed
Prepared/Running -> Cancelled/TimedOut
任一状态 -> InfrastructureFault
```

失败测试保留最小可重放输入和 Failure Bundle；清理过程仍需销毁 World、Scope、Handle 和临时资源。

## 线程、队列与并发所有权

- 默认单线程 Reference Host 保证语义可追踪；Stress/Soak 可启用受控 Worker，但所有权威写入仍在模拟 Owner Thread。
- Fault Adapter 的延迟、抖动、丢包、乱序、重复、断线、重连和 QueueFull 使用确定性 Seed，并通过有界队列注入。
- Replay/Hash 比较按 Tick/Phase/Processor 顺序读取，不把线程时序、对象地址或缓存状态纳入权威 Hash。
- 测试资源有超时、最大样本和内存预算；超限返回 InfrastructureFault，不无限等待。

## 正常数据流与失败路径

1. Scenario 校验 Required/Provided Capability、Release/Schema/Manifest 和资源预算。
2. 创建 Reference Host/World/Port，注入输入流、Fault Profile 和 ConfigSnapshot，运行固定 Tick。
3. 采集 State Hash、EventSeq、Revision、Replication、Persistence 和性能样本；需要时与 Native/真实 Host 做 Differential。
4. 失败时保存首个差异 Tick、输入窗口、配置/版本 Hash 和最小 Failure Bundle，可直接 Replay。
5. 测试结束执行 Scope/Handle/World 清理，并验证无泄漏；清理失败单独报告，不覆盖原始失败。

## 错误分类、恢复与降级

- **可拒绝**：Scenario Capability 不匹配、Fixture Schema 错误、Release/Manifest 不一致、资源预算不足。
- **可重试**：外部 Native/IO 测试依赖暂时不可用；重试必须保留同一 Seed/输入版本。
- **可致命**：Reference Host 不变量、Replay Hash 算法、Fixture Runner 或测试基础设施损坏；标记 InfrastructureFault。
- 不得通过放宽断言、跳过权限/Envelope/Serializer、增大无限超时或关闭 Fault 注入来“修复”失败。

## 配置、Capability 与安全约束

- Workload、TickRate、Seed、Fault Profile、超时和资源预算来自显式 Scenario/Config；生产构建不得读取测试后门。
- 测试 Fixture 使用占位 Hash/签名或专用密钥，不能把真实凭据、用户数据或生产 Endpoint 写入仓库/日志。
- Capability 匹配必须与 Host/Runtime 真实路径一致；`PureHeadless` 的简化仅限明确的 Reference Adapter。

## 日志、Metrics、Trace 与 Audit

记录 Scenario/Workload、Host Profile、Release/Schema Hash、Seed、Tick/Phase/Processor、State Hash、队列、Failure Bundle 和清理结果。原始样本不可变保存；可采样的 Diagnostic 不能替代 Replay 输入或测试结论。

## 测试面、故障矩阵与性能指标

- **测试面**：ECS/Query/CommandBuffer/Processor/Determinism/Revision/Entity Property/Golden；Txn、GAS、Replication、Persistence、Config、Hot Reload 的正向/失败/恢复路径。
- **故障矩阵**：网络故障全谱系、Txn 冲突/超时/崩溃、Snapshot 损坏、Schema/ABI/Capability mismatch、QueueFull、OOM、磁盘满、ALC/Task/Timer/Handle 泄漏。
- **性能指标**：1/10/25/50/100/150/200 Bot Workload 下 Tick p50/p95/p99/max、CPU、RSS、GC、Native Heap、队列、复制/重传字节、FFI Batch、日志和持久化延迟。

## 对应 ADR、Schema 与 Fixture

- 架构源 §15、`docs/adr/ADR-002-tick-determinism.md`、`docs/adr/ADR-009-local-transport.md`、`docs/adr/ADR-016-benchmark-workload.md`。
- 消费 `schemas/contract-result.schema.json`、`schemas/host-capability.schema.json`、`schemas/failure-bundle.schema.json` 及全部 Runtime 相关 Schema。
- 直接运行架构源 `fixtures/` 的正/反例；Replay/Scenario/Benchmark 结果属于本仓测试产物，不能反向成为公共 Schema。

## 尚未批准的决策门

- **RT-D-011**：Reference Host 与 Native/真实 Host 的保真级别、首差异报告格式和结果保留；必须先建立可重复 Workload 基线。
- Workload 元数据与回归阈值遵循架构源 ADR-016；缺硬件/平台/配置字段的结果无效，不能用于容量承诺。
- 测试工具命令、输出 JSON 和 CI 集成在首次 .NET 工程落地时确定，生产模块不得依赖测试程序集。
