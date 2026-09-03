// 客户端侧用法（④ 写 · ⑥ 读）。只进 *.Client.csproj。
using Lumio.GameRuntime.Ecs;
using Lumio.GameRuntime.Samples.Username.Components.Chat;
using Lumio.GameRuntime.Samples.Username.Components.Identity;
using Lumio.GameRuntime.Samples.Username.EntityTypes;

namespace Lumio.GameRuntime.Samples.Username.Host;

public static class ClientUsage
{
    /// <summary>
    /// ④ 写：Owner 字段改 .Value 本地立刻生效并自动上行 → 服务器 OnClientWrite 校验 → 写入 → 记脏 → Scope.Room 广播；
    /// 被拒则推回旧值、本地回滚（OnNameChanged 收到 reason = Correction）。不写任何消息代码。
    /// </summary>
    public static void Rename(World world, string newName)
        => world.Self.Get<IdentityComponent>().Name.Value = newName;

    /// <summary>④ 写：动作走 ServerRpc。Say 里先取自己的名字打 log 再发送；服务器在 ApplyInputs 相执行方法体。</summary>
    public static void Say(World world, string text)
        => world.Self.Get<ChatComponent>().Say(text);

    /// <summary>⑥ 读：一种写法，不用知道对方是什么实体类型。Sync<T> 读时隐式转换。</summary>
    public static string NameOf(World world, NetEntityId other)
        => world.Get<IdentityComponent>(other).Name;

    /// <summary>⑥ 读：按 id 取类型。子类型（: PlayerEntity）也算 Player。</summary>
    public static bool IsPlayer(World world, NetEntityId id)
        => world.TypeOf(id).Is<PlayerEntity>();

    /// <summary>⑥ 读：系统遍历。</summary>
    public static int CountNamed(World world)
    {
        int n = 0;
        foreach (var identity in world.Each<IdentityComponent>()) n += identity.Name.Value.Length > 0 ? 1 : 0;
        return n;
    }
}
