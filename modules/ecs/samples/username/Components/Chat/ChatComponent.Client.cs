// 兄弟文件：ChatComponent.cs · ChatComponent.Server.cs · ChatComponent.g.cs（生成）
// 客户端文件：只进 *.Client.csproj。
using System;
using Lumio.GameRuntime.Ecs;
using Lumio.GameRuntime.Samples.Username.Components.Identity;

namespace Lumio.GameRuntime.Samples.Username.Components.Chat;

public sealed partial class ChatComponent
{
    /// <summary>客户端说话：先取自己实体的名字打 log，再调 ServerRpc（调用即发送）。</summary>
    public void Say(string text)
    {
        string name = Get<IdentityComponent>().Name;    // 同一实体上的另一个组件：Get<T>() 没参数 = 自己
        Console.WriteLine($"[client] {name} says: {text}");
        SendMessage(text);
    }

    /// <summary>ClientRpc 到达：在发送者实体的 ChatComponent 上执行，line 已是「名字: 内容」。事件不存——聊天窗口是 UI 层的事，这里只打 log。</summary>
    public partial void OnChatMessage(string line)
        => Console.WriteLine($"[client] {line}");
}
