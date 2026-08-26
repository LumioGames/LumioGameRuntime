# LumioGameRuntime

> LumioGameEngine v0.2 架构中的 C# Shared ECS、稳定托管运行时与 Gameplay 热更宿主。

## 定位

`LumioGameRuntime` 是客户端与服务器共享的 C# 稳定层。它拥有 ECS 和 World 的业务生命周期，提供对 Rust Core Engine 的稳定抽象，并负责加载、替换、回滚和卸载 `LumioGame` 产出的热更 Gameplay 模块。

热更机制属于本仓库；具体可热更玩法实现属于 `LumioGame`。

## 职责

- ECS World、Entity、Component Storage、Query、System Scheduler 和 CommandBuffer。
- Component Registry、Change Tracking、Snapshot Projection 和确定性辅助设施。
- `ILumioCoreEngine`、`IVoxelEngine`、空间、碰撞、寻路和 Snapshot 计算抽象。
- Stable Managed Bridge、`HostApiV1`、`ManagedApiV1` 与 ABI 能力校验。
- `AssemblyLoadContext`、Gameplay Module 生命周期、状态迁移、异常隔离、热更回滚和旧 ALC 回收监测。
- 通用 Input/Output Batch、Replication/Replica 所需的稳定运行时契约。

## 依赖关系

### 上游依赖

- [`LumioNativeCore`](https://github.com/LumioGames/LumioNativeCore)：消费版本化 Native 基础契约。
- [`LumioVoxelEngine`](https://github.com/LumioGames/LumioVoxelEngine)：消费 Voxel Batch 与 Handle 契约，不拥有其内部数据。

### 下游使用者

- [`LumioServer`](https://github.com/LumioGames/LumioServer)：启动 Stable Managed Runtime 并驱动权威 ECS Tick。
- [`LumioClient`](https://github.com/LumioGames/LumioClient)：复用 ECS、Replica 和 Core Engine 抽象。
- [`LumioGame`](https://github.com/LumioGames/LumioGame)：实现共享 Gameplay、服务器热更模块和客户端游戏逻辑。

```text
LumioNativeCore + LumioVoxelEngine contracts
                 └─> LumioGameRuntime
                     ├─> LumioServer
                     ├─> LumioClient
                     └─> LumioGame
```

## 契约所有权

本仓库是 ECS 公共契约、Stable Managed API、Gameplay Module 接口、热更生命周期和通用同步结构的唯一事实源。

## 禁止事项

- 禁止保存完整 Voxel World 或复制 Rust Chunk 权威数据。
- 禁止让 Rust 直接遍历或修改 C# Component Storage。
- 禁止包含具体技能、任务、经济、关卡、UI 或产品内容。
- 禁止承担 Socket、Connection、Session、端口监听和服务器进程治理。
- 禁止让 Hot Gameplay 长期持有 Native Handle、裸指针、Timer Delegate 或后台 Task。
- 禁止依赖 `LumioServer`、`LumioClient` 或 `LumioGame` 的实现代码。

## 当前状态

`v0.1.0` 仅冻结仓库职责与依赖边界；C# 基线为 .NET 10 LTS，尚未发布代码或软件包。

