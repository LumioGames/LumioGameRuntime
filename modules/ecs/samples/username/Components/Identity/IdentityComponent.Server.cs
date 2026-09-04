// 兄弟文件：IdentityComponent.cs · IdentityComponent.Client.cs · IdentityComponent.g.cs（生成）
// 服务器文件：只进 *.Server.csproj；客户端程序集里没有这些成员。文件后缀就是归属声明，不打标注。
using Lumio.GameRuntime.Ecs;

namespace Lumio.GameRuntime.Samples.Username.Components.Identity;

public sealed partial class IdentityComponent
{
    /// <summary>持久业务身份。服务器私有字段：客户端读不到、不上网；[Persist] 进快照。</summary>
    [Persist] public string AccountId = "";

    /// <summary>
    /// owner 客户端的字段上行到达时（ApplyInputs 相，同 Tick 按发送者 NetEntityId 排序后）调用。
    /// accept 进来是 true；置 false = 拒绝，框架把权威值推回客户端（权威纠正）。不写这个钩子 = 直接接受。
    /// （partial void + ref：带返回值的 partial 在 C# 里必须有实现，做不到「不写就没有」。）
    /// </summary>
    partial void OnClientWrite(in SyncWrite w, ref bool accept)
        => accept = w.Is(Name) && w.Value<string>().Length is > 0 and <= 16;
}
