// EntityType 声明：与 PlayerEntity 同一组件集，由 EntityType 决定 bot / player，不另设 Kind 字段。
using Lumio.GameRuntime.Ecs;
using Lumio.GameRuntime.Samples.Username.Components.Chat;
using Lumio.GameRuntime.Samples.Username.Components.Identity;

namespace Lumio.GameRuntime.Samples.Username.EntityTypes;

[EntityType(Mode.CS)]
[Has(typeof(IdentityComponent))]
[Has(typeof(ChatComponent))]
public abstract class BotEntity { }
