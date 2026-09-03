# 样板示例：用户名（ECS 代码的标准写法）

> 状态：**参考样例**（2026-09-03，随 [ADR-058](https://github.com/LumioGames/LumioGameEngine/blob/main/.spec/decisions/ADR-058-ecs-world-manager-and-annotation-registry.md) 定稿）。
> 这里的 API（`WorldManager`、`Sync<T>`、`[ServerRpc]` / `[ClientRpc]`、`[EntityType]`…）由 RM-00011 r4 的 R4-05 卡落地；落地时把本目录接进 `Lumio.GameRuntime.Samples.Username.Server.csproj` / `.Client.csproj` 两个工程编过、测过。**以后所有 ECS 代码与讨论都以这套样例为标准。** 设计说明见架构仓 `.spec/knowledge/features/ecs.md` §4.5。

## 一条最小完整链路

| 步 | 干什么 | 看哪个文件 |
|---|---|---|
| ① 声明 | 组件类是唯一真源；同步字段 `Sync<T>`，服务器专属放 `.Server.cs`，客户端专属放 `.Client.cs` | `Components/Identity/*`、`Components/Chat/*`、`EntityTypes/PlayerEntity.cs` |
| ② 建世界 | 一进程一个 `WorldManager`；WorldEntity 随世界诞生 | `Host/ServerBootstrap.Server.cs` |
| ③ 创建 | 准入下单建 PlayerEntity；提交相发号、亮相、Awake、Start；客户端收「创建记录」用同一模板建 | `Host/ServerBootstrap.Server.cs`（服务器侧）；客户端侧是框架行为 |
| ④ 写 | Owner 字段改 `.Value` 自动上行；动作走 `[ServerRpc]`；服务器内部直接赋值即记脏 | `Host/ClientUsage.Client.cs`、`Components/Chat/ChatComponent.Server.cs` |
| ⑤ 同步 | 帧末按 `Scope` × 视野表下发，与本 Tick 的 `[ClientRpc]` 事件同一个包 | 零行玩法代码 |
| ⑥ 读 | `Get<T>()` 读自己、`Get<T>(id)` 读别人、`Each<T>()` 遍历 | `Host/ClientUsage.Client.cs` |
| ⑦ 存档 / 恢复 | 存档 = 对 WorldEntity 的 `[ServerRpc]`；恢复 = `CreateFromSnapshot` 建新世界 | `Host/ServerBootstrap.Server.cs` |

## 约定（lint 会查）

- 一个组件类型、按端拆 partial 文件，按组件聚合一个文件夹：`X.cs`（共享）/ `X.Server.cs` / `X.Client.cs` / `X.g.cs`（生成物，入库不手改）。
- `*.Server.csproj` 排除 `**/*.Client.cs` 并定义 `LUMIO_SERVER`；`*.Client.csproj` 反之。逻辑块与敏感信息用 `#if LUMIO_SERVER` / `#if LUMIO_CLIENT` 物理剔除。
- `[ServerOnly]` 只许出现在 `*.Server.cs`，`[ClientOnly]` 只许出现在 `*.Client.cs`；每个文件首行注释列出兄弟文件。
- `Sync<T>` 字段不打 `[ServerOnly]` / `[ClientOnly]`（两端都有，生成期拒）；没打任何标注的普通字段 = 本端本地值，不上网、不存档。
- 生成命令（build 自动跑）产三件：注册表 + 实体模板类（`.g.cs`）、同步表、契约声明表（json）；本目录不含 `.g.cs`，由生成器产出。

## 怎么读这段代码

看到 `Sync<T>` = 会上网，`Scope` 说给谁，`Authority` 说谁能写；看到 `[Persist]` = 进快照；文件名带 `.Server` / `.Client` = 只在那一端存在；`[ServerRpc]` = 客户端喊服务器做事，`[ClientRpc]` = 服务器通知客户端一次（不存不回放）；`Get<T>()` 没参数是自己、有参数是别人；`World.Self` = 本连接绑定的实体。
