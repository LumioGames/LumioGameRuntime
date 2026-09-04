// 兄弟文件：ChatComponent.cs · ChatComponent.Client.cs · ChatComponent.g.cs（生成）
// 服务器文件：只进 *.Server.csproj；客户端程序集里没有这些成员。
using System;
using System.Text;
using Lumio.GameRuntime.Ecs;
using Lumio.GameRuntime.Samples.Username.Components.Identity;

namespace Lumio.GameRuntime.Samples.Username.Components.Chat;

public sealed partial class ChatComponent
{
    /// <summary>最后一句话：服务器私有、存档、不同步（长文本不走 Sync）。放在 .Server.cs = 客户端程序集里不存在这个字段。</summary>
    [Persist] public string LastMessageText = "";

    /// <summary>最后一句话落在哪个 Tick：服务器私有、存档。</summary>
    [Persist] public ulong LastMessageTick;

    /// <summary>ServerRpc 处理体：取同一实体的名字 → 拼行 → 校验 → 写字段（直接赋值即记脏）→ 发事件。同一次写只有这一个入口。</summary>
    public partial void SendMessage(string text)
    {
        if (text.Length == 0) return;                   // 拒绝：不写、不发，客户端什么都收不到

        string name = Get<IdentityComponent>().Name;    // 同一实体上的另一个组件：Get<T>() 没参数 = 自己
        string line = $"{name}: {text}";                // 名字 + 内容拼成一行
        if (Encoding.UTF8.GetByteCount(line) > 512) return;   // 按拼好的行限制 UTF-8 字节数

        Console.WriteLine($"[server] {name} says: {text}");

        LastMessageText = text;
        LastMessageTick = World.Tick;

        OnChatMessage(line);                            // 提交相发出；messageId / 序号 / sender / tick 由框架盖章
    }
}
