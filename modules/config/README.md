# config 模块

> 管理 Schema 编译产物、固定层级合并、typed Table Reader 和不可变 Tick ConfigSnapshot。

**优先级**：P1
**实施阶段**：Vertical Slice
**架构基线**：`LGE-V1.0-2026-08-27`

## 模块定位与目标

`config` 把人类可读配置和生成表转换为已校验、版本化、可复现的运行时快照。它保证一个 Tick 内看到的配置不变，并把开发热载与生产签名切换隔离；它不承载 Secret 或业务规则判断。

## 负责什么

- 读取 Schema 校验后的源配置，合并默认值和层级：`Engine -> Platform -> Server -> Product -> Environment -> User/Session`。
- 消费生成的 typed binary table，校验 TableId、SchemaVersion、ConfigRevision、列类型、行 Key、范围和引用。
- 生成不可变 `ConfigSnapshot`，在 Tick 边界原子激活，并为各模块提供只读 Reader。
- 支持开发环境显式 Hot Load、生产环境带 Hash/签名的版本切换和旧版本保留。
- 输出配置差异、校验结果、来源 Hash 和激活 Revision，供 Simulation/Persistence/Observability 关联。

## 明确不负责什么

- 不定义 Ability、Component、Voxel、Network 或 Host 的业务语义和默认值。
- 不保存 Secret、访问凭据或私有密钥；Secret 与普通配置表必须分离，由 Host/安全设施提供。
- 不在 Tick 中途修改已激活快照，不通过环境变量隐式打开未声明 Capability。
- 不成为通用持久化、热更 Assembly 或 Runtime 状态迁移模块。

## 拥有的状态与资源

- Source/Validated/Compiled/Staged/Active/Rejected 的配置版本和来源 Hash。
- Typed Table 元数据、列/行索引、引用校验结果和不可变 Snapshot 引用计数。
- Tick 边界待激活队列、开发 Hot Load 观察状态和生产签名验证结果。
- 读取预算、最大表大小、未知字段策略和错误证据；不暴露可变底层字节。

## 输入、输出与稳定接口

- **输入**：源配置、生成 `config-table`、层级上下文、签名/Hash、Host Capability 和激活 Tick。
- **输出**：不可变 `ConfigSnapshot`、typed Row/Value Reader、ConfigRevision、差异报告和稳定拒绝原因。
- **候选接口**：`validate`、`compile`、`stage`、`activate_at_tick`、`read_table`、`diff`；具体类型随 Contract Toolchain 冻结。
- Reader 只返回已校验类型；缺失、重复、范围错误和未知必需列必须拒绝而不是返回默认零值。

## 上游与下游依赖

- **上游**：架构源 `config-table` Schema、Game/Host 生成产物、`observability` 事件端口。
- **下游**：`simulation` 获取 Tick Snapshot；`ecs`/`gas`/`replication`/`persistence` 读取各自配置；`hot-reload` 使用版本切换信号。
- `config` 不依赖具体 Gameplay、Storage、Network 或 `testing` 实现；编译器和格式库经 Adapter/Toolchain 接入。

## 生命周期与状态机

```text
Source -> Validated -> Compiled -> Staged -> Active
                                      \-> Rejected
Active -> Superseded
```

开发 Hot Load 也必须经过 `Validated -> Compiled -> Staged -> Active`，不能直接替换当前快照；生产切换只在指定 Tick 边界进行。

## 线程、队列与并发所有权

- 编译、校验和差异可以在后台 Worker 执行；Active Snapshot 只由 Simulation Owner Thread 在 Tick 边界切换。
- Reader 可并发只读不可变快照；旧快照在所有引用释放前不能回收。
- 文件/输入队列有大小、解压和分配上限；配置激活失败不得阻塞或修改正在运行的 Tick。
- `config` 不把可变字典或外部文件句柄传给 Gameplay/Native Worker。

## 正常数据流与失败路径

1. 读取源和生成表，校验列类型、Key 唯一性、引用、默认值和来源 Hash。
2. 按固定层级合并并生成 Typed Table，写入 Staging 版本。
3. 验证签名/Capability/资源预算，在 Tick 边界发布新的不可变 ConfigSnapshot。
4. 任一 Reader 只绑定一个 Snapshot Revision；旧版本继续服务当前 Tick，直到边界完成切换。

重复 Key、层级冲突、缺失必需列、类型/范围/引用错误、签名失败和版本不兼容都进入 Rejected，不以静默覆盖或默认值掩盖。

## 错误分类、恢复与降级

- **可拒绝**：Schema/类型/Key/引用错误、签名/Hash 失败、Capability 不匹配、超大小或超资源预算。
- **可重试**：源文件暂不可读、编译队列暂满；重试仍使用同一来源 Hash，不替换 Active Snapshot。
- **可致命**：Active Snapshot 校验不变量损坏或版本索引不可恢复；Runtime 保留旧证据并由 Host 进入 Faulted/维护。
- 开发环境可以拒绝 Hot Load 并继续使用上一有效快照；生产环境不得自动回退到未经批准的版本。

## 配置、Capability 与安全约束

- 层级顺序和激活方式是固定契约；Room Mode/Role 通过 Capability 输入，不在配置中复制 `IsLocal`/`IsOffline` 分支。
- 生产切换必须带签名、Hash、Release/Schema 版本和审计关联；Secret 走独立安全通道。
- Reader 对字符串、表大小、引用深度和分配量设上限，防止恶意配置造成资源耗尽。

## 日志、Metrics、Trace 与 Audit

记录编译耗时、表/行数、来源/输出 Hash、校验拒绝、激活 Tick、ConfigRevision、Reader 缓存命中和快照存活数。生产切换、签名失败和回退动作写 Audit；普通差异可写 Diagnostic。

## 测试面、故障矩阵与性能指标

- **测试面**：层级优先级、默认值合并、重复 Key、类型/范围/引用、Round-trip、旧版本读取、未知可选字段和 Tick 原子切换。
- **故障矩阵**：损坏表、签名/Hash 失败、缺列、超大表、编译队列满、切换竞态、Reader 在旧快照存活期间读取。
- **性能指标**：编译/校验 p50/p95/p99、Table Reader 延迟、快照切换耗时、内存、分配和启动加载时间。

## 对应 ADR、Schema 与 Fixture

- 架构源 `docs/adr/ADR-010-persistence-config.md`、`docs/adr/ADR-014-platform-capability.md`。
- `schemas/config-table.schema.json`、`schemas/host-capability.schema.json`。
- 正例：`fixtures/valid/config-table.json`、`fixtures/valid/host-capability.json`；反例：`fixtures/invalid/config-duplicate-key.json`、`fixtures/invalid/host-capability-missing-role.json`。

## 尚未批准的决策门

- **RT-D-008**：Schema 编译器、Table Reader 的实现栈、增量编译和开发 Hot Load 观察窗口；必须通过层级/拒绝/性能 Fixture。
- 生产签名、Secret Provider 和外部配置中心属于 Host/Release 部署决策；不得写成 Runtime 的硬依赖。
- 改变层级顺序、ConfigRevision 或 Tick 激活时机必须有新 ADR、SchemaEpoch 和迁移说明。
