// 兄弟文件：ChatComponent.Server.cs · ChatComponent.Client.cs · ChatComponent.g.cs（生成）
// 共享文件：服务器与客户端都编译。只放 Sync 字段、RPC 声明与两端共用的逻辑；服务器私有状态在 .Server.cs。
using Lumio.GameRuntime.Ecs;

namespace Lumio.GameRuntime.Samples.Username.Components.Chat;

[EcsComponent]
public sealed partial class ChatComponent : Component
{
    /// <summary>客户端 → 服务器的意图。方法体在服务器 ApplyInputs 相执行（见 .Server.cs）。</summary>
    [ServerRpc] public partial void SendMessage(string text);

    /// <summary>服务器 → 房间内客户端的一次性通知（事件）。line = 名字 + 内容，由服务器拼好；线上就是 C-1 chat.event 的 text 字段，契约不加字段。提交相发出，与字段变化同一 Tick 包下发，不存不回放。</summary>
    [ClientRpc(Scope.Room)] public partial void OnChatMessage(string line);
}
