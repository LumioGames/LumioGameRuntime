// 兄弟文件：ChatComponent.cs · ChatComponent.Server.cs · ChatComponent.g.cs（生成）
// 客户端文件：只进 *.Client.csproj。
using System.Collections.Generic;
using Lumio.GameRuntime.Ecs;

namespace Lumio.GameRuntime.Samples.Username.Components.Chat;

public sealed partial class ChatComponent
{
    /// <summary>本地玩家的聊天窗口：客户端本地值（未标注 = 不上网、不存档），服务器不保留任何窗口副本。</summary>
    public List<(NetEntityId Sender, string Text)> Window = new();

    /// <summary>ClientRpc 到达：在发送者实体的 ChatComponent 上执行；追加到本地玩家自己实体的窗口。</summary>
    public partial void OnChatMessage(string text)
        => World.Self.Get<ChatComponent>().Window.Add((Rpc.Sender, text));
}
