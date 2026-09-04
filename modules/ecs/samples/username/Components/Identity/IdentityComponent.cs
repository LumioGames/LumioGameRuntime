// 兄弟文件：IdentityComponent.Server.cs · IdentityComponent.Client.cs · IdentityComponent.g.cs（生成）
// 共享文件：服务器与客户端都编译。只放 Sync 字段、RPC 声明与两端共用的逻辑；单端状态放 .Server.cs / .Client.cs。
using Lumio.GameRuntime.Ecs;

namespace Lumio.GameRuntime.Samples.Username.Components.Identity;

[EcsComponent]
public sealed partial class IdentityComponent : Component
{
    /// <summary>
    /// 用户名：房间内公开；owner 客户端可改（改 .Value 自动上行）；进快照。
    /// 实体是 player 还是 bot 看 EntityType（world.TypeOf(id)），不另设字段。
    /// </summary>
    [Persist] public Sync<string> Name = new(Scope.Room, Authority.Owner);

    /// <summary>Owner-maintained observer allow-list used by claim-scoped fields.</summary>
    [Persist] public SyncList<NetEntityId> Friends = new(Scope.Owner, Authority.Owner);

    /// <summary>Claim-scoped identity data; the generator validates the same-component source.</summary>
    [Persist] public Sync<string> RealName = new(Scope.Claim, claimBy: nameof(Friends));
}
