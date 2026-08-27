---
name: repository-architecture
description: 仓库边界与架构契约——Runtime World/Tick 所有权和 Architecture Gate;改 ECS、事务或复制语义前查
metadata:
  type: doc
  status: 已交付
---

# 仓库边界与架构契约

## 规范来源与优先级

- Agent 的开发流程、测试政策和交付规则以 `.spec/` 为权威。
- 模块边界以根 [`README.md`](../../../README.md) 为本仓入口；共享架构以 `LumioGameEngineArchitecture` 的 `LGE-V1.0-2026-08-27` 为唯一来源，本仓 [`架构镜像`](../../../docs/architecture/LumioGameEngine_Architecture_v1.0.md) 只读。
- 冲突时不得在 Runtime 自行扩展公共 Tick/Txn/Replication Schema；先在架构源完成 ADR、Fixture 和新 Baseline。

## 所有权边界

- 本仓拥有每个 Role/World 的 ECS Storage、Entity/Component、Query/CommandBuffer、Logical Tick/Phase、Coordinator、Replication、GAS、Snapshot/Config 抽象与 Hot Reload Scope。
- Host 拥有 Wall Clock、进程、连接和 CoreCLR/ALC 创建；VoxelEngine 拥有 Voxel 状态；Game 只提供内容、初始化、Mapping 和 Migration Hook。
- Server/Client World、LocalEntityId 和 Gameplay Assembly 必须隔离；LocalEmbedded 使用两棵独立状态树和完整序列化路径。
- Runtime 只经生成 `IVoxelWorldPort`/Managed Contract 访问 Native/Voxel，不依赖 Server、Client、Game 或 Voxel 实现源码。

## Architecture Gate

- Host 只调用单一 Tick 入口；Phase 顺序、Processor Read/Write Set、结构提交、Native Completion 与 Replication Projection 必须确定且可验证。
- V1 权威 World 单线程写入；只在写集不重叠且有稳定归并规则时并行，所有队列有界，异步完成只在 Barrier 应用。
- Runtime 是 `CrossWorldTxnV1` Coordinator 语义所有者；Prepare 纯验证/预留，CommitIntent 持久化后按固定顺序幂等提交并可查询恢复。
- 对象地址、运行时遍历顺序和缓存状态不得进入 Canonical Hash；Snapshot/WAL/Replay 必须版本化、可校验、可恢复。
- Hot Gameplay 必须经 `GameplayModuleScope` 登记 Task/Timer/Subscription/Native Lease，按 `Quiesce -> Cancel -> Drain -> Dispose -> ValidateRoots -> Unload` 卸载。
