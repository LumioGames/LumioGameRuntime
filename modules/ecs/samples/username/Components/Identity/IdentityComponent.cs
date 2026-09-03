// 兄弟文件：IdentityComponent.Server.cs · IdentityComponent.Client.cs · IdentityComponent.g.cs（生成）
// 共享文件：服务器与客户端都编译。只放 Sync 字段与两端共用的逻辑。
using Lumio.GameRuntime.Ecs;

namespace Lumio.GameRuntime.Samples.Username.Components.Identity;

[EcsComponent]
public sealed partial class IdentityComponent : Component
{
    /// <summary>用户名：房间内公开；owner 客户端可改（改 .Value 自动上行）；进快照。</summary>
    [Persist] public Sync<string> Name = new(Scope.Room, Authority.Owner);

    /// <summary>实体种类（player / bot）：房间内公开；只有服务器写。</summary>
    public Sync<EntityKind> Kind = new(Scope.Room);
}
