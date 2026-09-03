// EntityType 声明：声明式 abstract class，不可实例化、没有成员，玩法代码不经它读数据。一份声明，两端共用。
// 生成器从这里产出内部模板类（实体 + 组件相邻分配、整块入池）与注册表。
// 继承就是 C# 继承：`public abstract class VipPlayerEntity : PlayerEntity { }` 再加自己的 [Has]，
// 组件集 = 基类的 ∪ 自己的；world.TypeOf(id).Is<PlayerEntity>() 对子类型也为 true。
using Lumio.GameRuntime.Ecs;
using Lumio.GameRuntime.Samples.Username.Components.Chat;
using Lumio.GameRuntime.Samples.Username.Components.Identity;

namespace Lumio.GameRuntime.Samples.Username.EntityTypes;

[EntityType(Mode.CS)]
[Has(typeof(IdentityComponent))]
[Has(typeof(ChatComponent))]
public abstract class PlayerEntity { }
