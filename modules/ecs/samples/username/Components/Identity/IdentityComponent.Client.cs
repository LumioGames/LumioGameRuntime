// 兄弟文件：IdentityComponent.cs · IdentityComponent.Server.cs · IdentityComponent.g.cs（生成）
// 客户端文件：只进 *.Client.csproj。
using System;
using Lumio.GameRuntime.Ecs;

namespace Lumio.GameRuntime.Samples.Username.Components.Identity;

public sealed partial class IdentityComponent
{
    /// <summary>客户端：Awake 之后、Start 之前，框架已把创建记录里的服务器字段值写入——此时 Name 已可读。</summary>
    partial void PostAttribute()
        => Console.WriteLine($"[client] entity arrived: name={Name.Value}");   // 赋给 string 时走隐式转换；插值里显式 .Value

    /// <summary>
    /// 生成器为每个 Sync 字段产一对可选钩子 OnXChanging / OnXChanged（声明在 .g.cs 里；不写 = 不监听）。
    /// 默认只收对端来的变化：reason = Sync（别人改名同步到达）/ Correction（自己改名被拒，服务器推回旧值）。
    /// 自己写 Name.Value 不触发；要收自己写的，字段声明加第三个参数 Notify.All（此时 reason = Local）。
    /// </summary>
    partial void OnNameChanged(string old, string @new, ChangeReason reason)
        => Console.WriteLine($"[client] name {old} -> {@new} ({reason})");
}
