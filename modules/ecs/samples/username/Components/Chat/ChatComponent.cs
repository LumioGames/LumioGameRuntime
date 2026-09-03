// 兄弟文件：ChatComponent.Server.cs · ChatComponent.Client.cs · ChatComponent.g.cs（生成）
// 共享文件：服务器与客户端都编译。
using Lumio.GameRuntime.Ecs;

namespace Lumio.GameRuntime.Samples.Username.Components.Chat;

[EcsComponent]
public sealed partial class ChatComponent : Component
{
    /// <summary>最后一句话：私有、存档、不同步（长文本不走 Sync）。</summary>
    [Persist] public string LastMessageText = "";

    /// <summary>最后一句话落在哪个 Tick：私有、存档。</summary>
    [Persist] public ulong LastMessageTick;

    /// <summary>客户端 → 服务器的意图。方法体在服务器 ApplyInputs 相执行（见 .Server.cs）。</summary>
    [ServerRpc] public partial void SendMessage(string text);

    /// <summary>服务器 → 房间内客户端的一次性通知（事件）。提交相发出，与字段变化同一 Tick 包下发，不存不回放。</summary>
    [ClientRpc(Scope.Room)] public partial void OnChatMessage(string text);
}
