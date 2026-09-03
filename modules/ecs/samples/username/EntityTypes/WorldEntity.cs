// 世界实体声明：和 PlayerEntity 同一种写法，由游戏声明；World = true 的类型在注册表里必须恰好一个（缺或多 = 生成报错）。
// World Manager 建世界时按它建单例，两端 world.Single<T>() 按组件类型取；客户端不自建，它是第一条创建记录。
// 引擎只提供世界级组件（WorldSaveComponent：Save(slot) 是对它的 ServerRpc）；游戏的世界级状态（对局阶段、比分…）再加 [Has] 挂上来。
using Lumio.GameRuntime.Ecs;

namespace Lumio.GameRuntime.Samples.Username.EntityTypes;

[EntityType(Mode.CS, World = true)]
[Has(typeof(WorldSaveComponent))]
public abstract class WorldEntity { }
