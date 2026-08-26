# LumioGameRuntime

> 客户端与服务器共用的稳定 C# ECS Runtime、GameWorld 编排层与 GAS Framework。

## 定位

`LumioGameRuntime` 是托管运行时的通用边界，位于 Native/领域库之上、具体游戏内容之下。它拥有 ECS 存储和 Tick 生命周期，提供 `GameWorld`、Client `ReplicaWorld`、Processor 调度、跨 World Coordinator、稳定 Managed API、GAS Framework 和 Hot Reload Host。

GAS Framework 属于本仓库；具体 Ability、Effect、Formula、Targeting 和玩法规则属于 `LumioGame`。

总架构基线见 [`docs/architecture/LumioGameEngine_Architecture_v0.3.md`](docs/architecture/LumioGameEngine_Architecture_v0.3.md)。

本仓库的稳定 Runtime、GAS Framework、Processor 和 Hot Reload Host 使用 C#；从 `LumioGame` 加载的 Server/Client Gameplay 热更程序集也必须使用 C#。

## 拥有的状态与生命周期

- 每个 Role/World 独立的 ECS Entity、Component Storage、Archetype、Query、CommandBuffer 和 Change Tracking。
- `GameWorld` 与 Client `ReplicaWorld` 的创建、Tick 阶段、结构提交、Snapshot Projection 和销毁生命周期。
- GAS 的 Ability/Effect/Attribute/Tag 状态、Handle、生命周期、状态机、Prediction/Authority/Correction/Rollback 上下文。
- `SimulationSession` 的 Tick Clock、Determinism Context、Typed Channel、Coordinator Transaction 和 Snapshot Metadata。
- Hot Gameplay Assembly 的 Load/Activate/Pause/Migrate/Unload、异常隔离和回滚状态。

Runtime 不保存完整 VoxelWorld；只持有 `IVoxelWorldPort`、Revision 和结果批次。

## 职责

- 提供 ECS Storage、Query、CommandBuffer、Change Tracking、Snapshot Projection、Tick 阶段和结构变更边界。
- 以 `Processor/Handler` 为主要执行抽象；传统 `System` 只是可选的 Processor 实现，不强制使用。
- 提供 `NetEntityId + LocalEntityId` 双层实体身份、Role Capability 和 Replication Mapping 接口。
- 提供 Cross-World Coordinator，在 Tick 内以 Prepare/Commit 协调 GameWorld 与 VoxelWorld 的变更。
- 提供 GAS Framework：Ability/Effect/Attribute/Tag、Handle、状态机、Tick、Snapshot、Replication、Prediction、Correction 和 Rollback 接口。
- 提供 `HostApiV1`、`ManagedApiV1`、Typed Input/Output Batch、稳定错误和能力校验。
- 承载 CoreCLR/AssemblyLoadContext 无关的 Hot Reload 生命周期契约、Migration Hook、旧 ALC 回收监测和诊断。

GAS 只表达 Gameplay 事件和状态语义，不直接操作 Socket 或原始字节；Server/Client Host 负责传输。

## 明确不负责什么

- 不拥有 Voxel Chunk/Block/Revision 权威数据，不直接依赖 `LumioVoxelEngine` 内部实现。
- 不实现 Socket、Connection、Session、端口监听、DS 进程治理或平台 UI/Renderer。
- 不包含具体技能、Buff 数值、任务、经济、关卡、内容资产或产品规则。
- 不把 Server/Client 合并成一个 ECS World；Local 模式也必须创建独立的 Server 和 Client Entity。
- 不要求 Server/Client Component 对称，也不替玩法作者判断版本语义兼容。
- 不允许 Hot Gameplay 长期持有 Native Handle、裸指针、Timer Delegate 或后台 Task。

## 对外产物与契约

- `LumioGameRuntime.<version>.dll`：ECS、Tick、GAS、Coordinator、Role 和 Snapshot API。
- `HostApiV1`/`ManagedApiV1`、`IVoxelWorldPort`、Typed Channel、Error/Capability Schema。
- `RuntimeManifest.json`：Runtime API、GAS Schema、Serialization、Hot Reload 和 Migration 版本。
- Headless Host、Determinism Harness、Replay/State Hash/Metrics API 与测试工具包。

## Source / Compile-Time Dependencies

- .NET SDK、C# 编译器和经审核的托管基础包。
- `LumioCoreEngine` 生成的 Native Contract/Manifest，仅通过稳定 Managed Adapter 使用。
- 不得对 `LumioVoxelEngine` Rust 源码、`LumioServer`、`LumioClient` 或 `LumioGame` 实现建立编译期依赖。

## Generated Contract Dependencies

消费 Native/Core/Voxel 生成的 Handle、Batch、Capability 和 ABI Metadata；由 `LumioGame` 生成的 Gameplay Schema、RPC 和 Mapping 通过公开接口注册，不把具体内容编译进 Runtime。

## Runtime Loading Relationships

```text
LumioServer or LumioClient Host
  -> LumioGameRuntime stable host
  -> GameWorld / ReplicaWorld (independent ECS worlds)
  -> ServerGameplay.dll or ClientGameplay.dll
  -> IVoxelWorldPort / GAS / generated contracts
```

同一进程 LocalEmbedded 的两个 Role 仍各自拥有 World、Entity 和 Component Storage，通过 Typed InMemoryTransport 交换命令、事件和快照。

## Release Composition Relationships

Runtime 作为稳定基础包被 `LumioServer`、`LumioClient` 和 `LumioGame` 锁定。生产发布时 Server/Client 使用同一 `GameReleaseId` 的不同 Gameplay Assembly；Runtime 只校验 API/Schema/ABI 和 Migration Hook，不替玩法决定兼容性。

## Room Modes / Host Profiles

Runtime 不暴露 `IsOffline` 分支给 Gameplay，而是由 `RoomMode`、`HostProfile`、`Role` 和 Capability 注入环境：

| RoomMode | Host Profile | Runtime 形态 |
| --- | --- | --- |
| `Online` | `PublicDedicatedServer` / `PlayerHostedDedicatedServer` / `LocalhostDedicatedServer` | 分离 Server/Client 进程，各自 World。 |
| `Singleplayer` | `LocalEmbedded` | 同进程双 Role、双 ECS World、InMemoryTransport。 |

移动端第一阶段同样走完整双 Role 的 LocalEmbedded 或远程 DS，不实现启动 Player-hosted DS。

## Headless Test Surface

- ECS Entity/Component/Query/CommandBuffer/结构变更与 Tick 阶段单测。
- GAS Framework 生命周期、Handle、状态机、预测/校正/回滚、Snapshot 和确定性测试。
- Cross-World Prepare/Commit、Revision、失败回滚和 Replay/State Hash 测试。
- Hot Reload Load/Unload/Migration/ALC 回收和异常隔离测试。
- `PureHeadless`、`NativeHeadless`、`LocalEmbedded`、`LocalSplitProcess`、`RemoteDS`、`MobileLocal` Host Smoke Test。

## Version / Manifest

- Runtime API、GAS Framework、Serialization 和 Host Contract 各自记录版本；破坏性变更提升主版本。
- Manifest 列出 Runtime Commit、API/Schema、依赖 Core Engine、目标平台、Artifact Hash 和能力矩阵。
- Host 启动时拒绝不兼容的 Game Release、Generated Contract 或 Native ABI。

## 开发规范

- 所有结构变更通过 CommandBuffer，在固定 Tick 阶段统一提交；Processor 不直接修改迭代中的 Archetype。
- `NetEntityId` 用于跨端/快照身份，`LocalEntityId` 只在单个 ECS World 内有效；禁止混用。
- Processor 通过 Query/Typed Channel/Port 协作；只有需要注册调度语义时才实现传统 `System`。
- GAS Framework 保持宿主无关；Socket、序列化字节和连接状态留在 Host Adapter。
- 跨 World 事务必须可重试、可记录、可回放，并在 Commit 失败时产生明确结果。
- Hot Gameplay 只能使用受控 API；卸载前必须清理 Handle、订阅、Timer 和异步任务。

## 当前阶段任务

- 冻结 ECS、Role、双层 Entity、Typed Channel、Coordinator 和 GAS Framework v0.3 API。
- 建立两个独立 World 的 LocalEmbedded Headless Host 与确定性回放。
- 提供 Hot Reload/Migration 骨架和 Server/Client Gameplay Assembly 加载校验。
