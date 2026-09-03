// 兄弟文件：IdentityComponent.cs · IdentityComponent.Client.cs · IdentityComponent.g.cs（生成）
// 服务器文件：只进 *.Server.csproj；客户端程序集里没有这些成员。
using Lumio.GameRuntime.Ecs;

namespace Lumio.GameRuntime.Samples.Username.Components.Identity;

public sealed partial class IdentityComponent
{
    /// <summary>持久业务身份。私有字段：客户端读不到、不上网；[Persist] 进快照。</summary>
    [Persist] public string AccountId = "";

    // 连接态：不存档（重启即离线）。绑定 = 实体字段，没有独立绑定表。
    public bool Connected;
    public ulong ConnectionGeneration;
    public ulong DisconnectedAtTick;

    /// <summary>
    /// owner 客户端的字段上行到达时（ApplyInputs 相，同 Tick 按发送者 NetEntityId 排序后）调用。
    /// 返回 false = 拒绝，框架把权威值推回客户端（权威纠正）。不写这个钩子 = 直接接受。
    /// </summary>
    partial bool OnClientWrite(in SyncWrite w)
        => w.Is(Name) && w.Value<string>().Length is > 0 and <= 16;
}
