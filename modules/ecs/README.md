# ecs 模块

> 提供每个 Role/World 独立的 Entity、Component、Query、Storage 和 Change Tracking 语义。

**优先级**：P0
**实施阶段**：Foundation
**架构基线**：`LGE-V1.4-2026-08-27`

## 模块定位与目标

`ecs` 是 Runtime 的 World-local 状态基础。它让 Server `GameWorld`、Client `ReplicaWorld` 和 Replay World 各自拥有独立的实体命名空间、组件存储和查询视图，并为上层模块提供不暴露内部地址的稳定读写边界。

## 负责什么

- 创建、销毁和查询当前 World 内的 `LocalEntityId`；Generation 失效必须可检测。
- 保存 Component Storage、类型注册、字段访问和 Query 结果；Storage 布局保持内部可替换。
- 提供只读 Query View、受声明约束的写入 View 和 Change Tracking/Dirty 结果。
- 定义实体生命周期、同一 Tick 内的可见性和销毁后的 stale 引用拒绝。
- 为 `command`、`simulation`、`gas`、`replication` 和 `persistence` 提供快照/投影 Provider。

## 明确不负责什么

- 不拥有 Host Wall Clock、Logical Tick、Processor 调度或并行策略（归 `simulation`）。
- 不直接执行结构变化；Create/Add/Remove/Destroy 通过 `command` 的 CommandBuffer 延迟提交。
- 不定义 `NetEntityId` 的网络映射、Baseline、Tombstone 保留窗口或传输（归 `replication`）。
- 不定义 GAS Formula、权限、Voxel 状态、Session/Txn 或持久化后端。
- 不向调用方暴露 Archetype、Column、裸指针、对象地址或跨 World Storage 引用。

## 拥有的状态与资源

- World-local Entity Index/Generation 表和活动/销毁状态。
- Component 类型注册、Storage 数据、Query 编译缓存和版本化读写 View。
- Tick 内 Change Set、字段 Dirty 标记和 Snapshot Projection 所需的只读切面。
- World 关闭时使全部 LocalEntityId、View 和异步读取令牌失效。

## 输入、输出与稳定接口

- **输入**：World Context、生成的 Component Schema/TypeId、Query 描述、受授权的读写 View 请求。
- **输出**：Entity 生命周期结果、typed Query Batch、Change Set、Snapshot Provider 和稳定错误类别。
- **候选接口**：`create_entity`、`destroy_entity`、`query`、`read_view`、`change_set`；具体名称和参数在 C# 工程与公共契约冻结前不得视为发布 API。
- 所有返回的 View/Batch 都声明有效 Tick、World 和 Revision；不足或失效时返回诊断结果，不返回部分未标记数据。

## 上游与下游依赖

- **上游**：架构源的 Component/Entity 生成契约、Runtime Context 和 `observability` 的事件承载。
- **下游**：`command` 消费结构提交入口；`simulation` 消费 Query/读写集；`gas`、`replication`、`persistence` 消费投影接口。
- `ecs` 不依赖 `simulation`、`command`、`replication`、`gas` 或 `testing`，避免状态基础反向依赖编排层。

## 生命周期与状态机

World-local ECS 状态机：

```text
Created -> Registering -> Ready -> Running
Running -> Draining -> Disposed
任一状态 -> Faulted -> Disposed
```

只有所属 Simulation Owner Thread 可以推进权威状态；`Disposed` 后任何旧 Entity、View 或异步结果都必须被拒绝。

## 线程、队列与并发所有权

- V1 权威 Storage 由一个 Simulation Owner Thread 写入；Query 读取使用同一线程或明确的只读 Snapshot。
- 后台线程可以消费不可变 Snapshot 做诊断/编码，但不能持有可变 Component 引用或直接发布结构变化。
- Query Batch、Change Set 和 Snapshot View 都有最大实体/字段/字节预算；队列满载由调用方处理，不在 `ecs` 内无限排队。
- LocalEmbedded 的两棵 World 使用不同 Storage 和 LocalEntityId，不能共享缓存、锁或 View。

## 正常数据流与失败路径

1. 生成契约注册 Component 类型并校验 TypeId/字段版本。
2. Processor 通过 Query 获得声明范围内的 View，写入仅作用于已有组件字段。
3. 结构变化写入 `command` Buffer，在 `EcsCommandBufferCommit` 统一应用并生成 Change Set。
4. Snapshot/Replication 从稳定 View 读取；销毁实体先标记不可见，再按提交规则回收。

非法 Generation、未知 Component、Query 预算超限、跨 World View、重复销毁和关闭后的回调都必须在边界返回稳定错误，不得复活实体或写入半提交状态。

## 错误分类、恢复与降级

- **可拒绝**：未知 TypeId、无效 LocalEntityId、读写集越界、重复结构操作、预算超限。
- **可重试**：只读 Snapshot 尚未就绪或 Query 资源暂时不足；调用方可在后续 Tick 重新请求。
- **可致命**：Storage 完整性损坏、World Context 不可恢复或不变量失败；进入 `Faulted`，由 Host/Runtime 决定重建。
- 降级只能减少非权威 Query/Diagnostic 输出，不能静默丢弃权威结构提交或 Change Set。

## 配置、Capability 与安全约束

- Storage/Query 预算来自不可变 ConfigSnapshot；本模块不读取 `IsLocal`/`IsOffline` 等业务模式布尔值。
- 组件字段和 TypeId 只接受已签名/已验证的生成产物；未知字段按 Schema 策略拒绝，不能由反射随意注入。
- 不保存 Secret、Socket、Native 裸指针或 Gameplay Delegate；外部资源通过明确的 Handle/Provider 传入。

## 日志、Metrics、Trace 与 Audit

记录 Entity 创建/销毁计数、Query 数量与耗时、Storage 字节、Change Set 大小、Generation 拒绝和预算拒绝。事件带 `SessionId`、`WorldId`、`TickId`、`NetEntityId`（若有）和 `TraceId`；具体 Sink、保留和脱敏归 `observability`/Host。

## 测试面、故障矩阵与性能指标

- **测试面**：Entity 生命周期、Generation、Component 添加/移除、Query 过滤、Change Tracking、Snapshot View 和双 World 隔离。
- **故障矩阵**：未知类型、stale Entity、跨 World View、重复销毁、Query 超预算、Storage 损坏、关闭后异步回调。
- **性能指标**：Query p50/p95/p99、结构提交耗时、Change Set 字节、每 Tick 分配、峰值内存和不同实体密度下的吞吐。

## 对应 ADR、Schema 与 Fixture

- 架构源 `docs/adr/ADR-004-entity-identity.md`、`schemas/entity-identity.schema.json`：实体命名空间、Tombstone 和 provisional remap 由 `replication` 消费，本模块只实现 World-local 部分。
- 架构源 `schemas/processor-descriptor.schema.json`：Query/ReadSet/WriteSet 由 `simulation` 校验，本模块提供执行 View。
- 正/反例：`fixtures/valid/entity-tombstone.json`、`fixtures/valid/entity-provisional-remap.json`、`fixtures/invalid/entity-reused-tombstone.json`。

## 尚未批准的决策门

- **RT-D-002**：Archetype、Column、Sparse Set 或其他 Storage 表示；只冻结 Entity/Component/Query 语义，选型须通过 Property、Golden 和 Benchmark。
- **RT-D-001**：逻辑 `ecs` 与 C# 程序集/项目的映射；物理拆分不能改变 World-local 所有权。
- Component Schema 的新增公共字段仍须回到架构源 Contract Toolchain，不能在本模块 README 中提前发布。
