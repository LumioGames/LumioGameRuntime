# 样板示例：用户名（ECS 代码的标准写法）

> 状态：**参考样例**（2026-09-03 第二轮，随 [ADR-058](https://github.com/LumioGames/LumioGameEngine/blob/main/.spec/decisions/ADR-058-ecs-world-manager-and-annotation-registry.md) 修订定稿）。
> 这里的 API（`WorldManager`、`Sync<T>`、`[ServerRpc]` / `[ClientRpc]`、`[EntityType]`、`TypeOf`、生成的 `OnXChanged` 钩子…）由 RM-00011 r4 的 R4-05 卡落地；落地时把本目录接进 `Lumio.GameRuntime.Samples.Username.Server.csproj` / `.Client.csproj` 两个工程编过、测过。**以后所有 ECS 代码与讨论都以这套样例为标准。** 设计说明见架构仓 `.spec/knowledge/features/ecs.md` §4.5。

## 最小 Demo

建一个世界 → 世界上建一个 PlayerEntity → 实体有 Identity + Chat 两个组件 → Chat 取到自己实体的名字、发消息，消息 = 名字 + 内容 → 两端 log 验证；改名后下一句话的 log 就是新名字。

## 一条最小完整链路

| 步 | 干什么 | 看哪个文件 |
|---|---|---|
| ① 声明 | 组件类是唯一真源；同步字段 `Sync<T>`，服务器私有放 `.Server.cs`，客户端本地放 `.Client.cs`；EntityType 是 abstract class，可继承；WorldEntity 由游戏声明 | `Components/Identity/*`、`Components/Chat/*`、`EntityTypes/PlayerEntity.cs`、`EntityTypes/WorldEntity.cs` |
| ② 建世界 | 两端同一个 `WorldManager.Create(GeneratedRegistry.Instance)`，服务器多传 `instanceId`；WorldEntity 随世界诞生，客户端收第一条创建记录得到它 | `Host/ServerBootstrap.Server.cs`、`Host/ClientBootstrap.Client.cs` |
| ③ 创建 | 准入下单建 PlayerEntity；提交相发号、亮相、Awake、Start；客户端收「创建记录」用同一模板建 → Awake → PostAttribute → Start | `Host/ServerBootstrap.Server.cs`（服务器侧）；客户端侧是框架行为，`IdentityComponent.Client.cs` 的 `PostAttribute` 打 log |
| ④ 写 | Owner 字段改 `.Value` 本地生效并自动上行（服务器 `OnClientWrite(in w, ref accept)` 校验）；动作走 `[ServerRpc]`；服务器内部直接赋值即记脏 | `Host/ClientUsage.Client.cs`、`Components/Chat/ChatComponent.Client.cs`（`Say`）、`Components/Chat/ChatComponent.Server.cs` |
| ⑤ 同步 | 帧末按 `Scope` × 视野表下发，与本 Tick 的 `[ClientRpc]` 事件同一个包；客户端整包先写入再统一触发 `OnXChanged` | 零行玩法代码；`IdentityComponent.Client.cs` 的 `OnNameChanged` 打 log |
| ⑥ 读 | `Get<T>()` 读自己（组件里取同一实体的另一个组件也是它）、`Get<T>(id)` 读别人、`Each<T>()` 遍历、`TypeOf(id)` 取类型 | `Host/ClientUsage.Client.cs`、两个 ChatComponent 端文件 |
| ⑦ 存档 / 恢复 | 存档 = 对 WorldEntity 的 `[ServerRpc]`；恢复 = `CreateFromSnapshot` 建新世界 | `Host/ServerBootstrap.Server.cs` |

## 约定（lint 会查）

- 一个组件类型、按端拆 partial 文件，按组件聚合一个文件夹：`X.cs`（共享）/ `X.Server.cs` / `X.Client.cs` / `X.g.cs`（生成物，入库不手改）。
- **文件后缀就是归属声明，没有 `[ServerOnly]` / `[ClientOnly]` 标注。** `*.Server.csproj` 排除 `**/*.Client.cs` 并定义 `LUMIO_SERVER`；`*.Client.csproj` 反之。逻辑块与敏感信息用 `#if LUMIO_SERVER` / `#if LUMIO_CLIENT` 物理剔除。
- 共享文件里只许 `Sync<T>` / `SyncList` / `SyncDict` 字段、RPC 声明与两端共用的逻辑；非 Sync 的状态字段必须在 `.Server.cs` / `.Client.cs`（放共享文件 = 另一端多一个永远是默认值的死字段）。没打任何标注的普通字段 = 本端本地值，不上网、不存档。
- EntityType 声明是 abstract class、无成员；继承就是 C# 继承（子类型组件集 = 基类 ∪ 自己）；`World = true` 的类型恰好一个。
- 每个文件首行注释列出兄弟文件。
- 生成命令（build 自动跑）产三件：注册表 + 实体模板类 + 每字段可选钩子声明 + RPC 发送桩（`.g.cs`；`[ServerRpc]` 在客户端、`[ClientRpc]` 在服务器都是 partial 声明，没有用户实现，桩体由生成器产在该端）、同步表、契约声明表（json）；本目录不含 `.g.cs`，由生成器产出。

## 怎么读这段代码

看到 `Sync<T>` = 会上网，`Scope` 说给谁，`Authority` 说谁能写，第三个参数 `Notify` 说本端自己写要不要收回调（默认不收）；看到 `[Persist]` = 进快照；文件名带 `.Server` / `.Client` = 只在那一端存在；`[ServerRpc]` = 客户端喊服务器做事，`[ClientRpc]` = 服务器通知客户端一次（不存不回放，聊天窗口归 UI 层；聊天事件的 line 由服务器拼成「名字: 内容」，C-1 契约不加字段）；`Commands.Create<PlayerEntity>()` = 按类型下单（声明类无成员，没有 `.Type`）；`Get<T>()` 没参数是自己、有参数是别人；`World.Self` = 本连接绑定的实体（由欢迎消息绑定）；`world.TypeOf(id).Is<PlayerEntity>()` = 按 id 判类型，子类型也算；`OnNameChanged(old, new, reason)` = 生成器给每个 Sync 字段产的可选钩子，`reason` 是 `Sync` / `Correction` / `Local`。同进程双端（单机 / 本地联调）= 两个 Manager + 内存环回，代码零差异。
