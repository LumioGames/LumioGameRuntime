// 兄弟文件：IdentityComponent.cs · IdentityComponent.Server.cs · IdentityComponent.g.cs（生成）
// 客户端文件：只进 *.Client.csproj。
using Lumio.GameRuntime.Ecs;

namespace Lumio.GameRuntime.Samples.Username.Components.Identity;

public sealed partial class IdentityComponent
{
    /// <summary>客户端本地缓存（未标注 = 本端本地值：不上网、不存档）。</summary>
    public string DisplayName = "";

    /// <summary>客户端：Awake 之后、Start 之前，框架已把创建记录里的服务器字段值写入。据此重建派生缓存。</summary>
    partial void PostAttribute() => DisplayName = Name;   // Sync<T> 读时隐式转换
}
