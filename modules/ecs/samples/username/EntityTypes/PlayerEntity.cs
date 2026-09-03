// EntityType 声明：声明式 static class，不可实例化，玩法代码不经它读数据。一份声明，两端共用。
// 生成器从这里产出内部模板类（实体 + 组件相邻分配、整块入池）与注册表。
using Lumio.GameRuntime.Ecs;
using Lumio.GameRuntime.Samples.Username.Components.Chat;
using Lumio.GameRuntime.Samples.Username.Components.Identity;

namespace Lumio.GameRuntime.Samples.Username.EntityTypes;

[EntityType(Mode.CS)]
[Has(typeof(IdentityComponent))]
[Has(typeof(ChatComponent))]
public static class PlayerEntity { }
