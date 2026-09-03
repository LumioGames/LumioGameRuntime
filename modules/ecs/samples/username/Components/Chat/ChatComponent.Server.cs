// 兄弟文件：ChatComponent.cs · ChatComponent.Client.cs · ChatComponent.g.cs（生成）
// 服务器文件：只进 *.Server.csproj。
using Lumio.GameRuntime.Ecs;

namespace Lumio.GameRuntime.Samples.Username.Components.Chat;

public sealed partial class ChatComponent
{
    /// <summary>ServerRpc 处理体：校验 → 写字段（直接赋值即记脏）→ 发事件。同一次写只有这一个入口。</summary>
    public partial void SendMessage(string text)
    {
        if (text.Length is 0 or > 512) return;          // 拒绝：不写、不发，客户端什么都收不到

        LastMessageText = text;
        LastMessageTick = World.Tick;

        OnChatMessage(text);                            // 提交相发出；messageId / 序号 / sender / tick 由框架盖章
    }
}
