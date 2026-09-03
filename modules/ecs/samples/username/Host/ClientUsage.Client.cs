// 客户端侧用法（④ 写 · ⑥ 读）。只进 *.Client.csproj。
using Lumio.GameRuntime.Ecs;
using Lumio.GameRuntime.Samples.Username.Components.Chat;
using Lumio.GameRuntime.Samples.Username.Components.Identity;

namespace Lumio.GameRuntime.Samples.Username.Host;

public static class ClientUsage
{
    /// <summary>④ 写：Owner 字段改 .Value 自动上行 → 服务器 OnClientWrite 校验 → 写入 → 记脏 → Scope.Room 广播。不写任何消息代码。</summary>
    public static void Rename(World world, string newName)
        => world.Self.Get<IdentityComponent>().Name.Value = newName;

    /// <summary>④ 写：动作走 ServerRpc。调用即发送；服务器在 ApplyInputs 相执行方法体。</summary>
    public static void Say(World world, string text)
        => world.Self.Get<ChatComponent>().SendMessage(text);

    /// <summary>⑥ 读：一种写法，不用知道对方是什么实体类型。Sync<T> 读时隐式转换。</summary>
    public static string NameOf(World world, NetEntityId other)
        => world.Get<IdentityComponent>(other).Name;

    /// <summary>⑥ 读：系统遍历。</summary>
    public static int CountChatters(World world)
    {
        int n = 0;
        foreach (var chat in world.Each<ChatComponent>()) n += chat.LastMessageText.Length > 0 ? 1 : 0;
        return n;
    }
}
