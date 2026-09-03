// 服务器宿主侧用法（② 建世界 · ③ 创建 · ⑦ 存档与恢复）。只进 *.Server.csproj。
using System.Threading;
using Lumio.GameRuntime.Ecs;
using Lumio.GameRuntime.Samples.Username.Components.Identity;
using Lumio.GameRuntime.Samples.Username.EntityTypes;

namespace Lumio.GameRuntime.Samples.Username.Host;

public static class ServerBootstrap
{
    /// <summary>② 建世界：一进程一个 WorldManager；WorldEntity 随世界诞生。</summary>
    public static WorldManager Boot(ulong hostGivenInstanceId)
    {
        var manager = WorldManager.Create(GeneratedRegistry.Instance, instanceId: hostGivenInstanceId);
        manager.Start(ownerThread: Thread.CurrentThread);      // 记线程归属；之后所有入口校验，网络线程只能 Enqueue
        _ = manager.World.Single<WorldSaveComponent>();        // WorldEntity 已存在，按类型取单例
        return manager;
    }

    /// <summary>③ 创建：准入服务（Manager 的服务之一）在 ApplyInputs 相下单。NetEntityId 在提交相由世界发（实例 ID + 计数器）。</summary>
    public static void AdmitPlayer(WorldManager manager, string accountId)
    {
        var order = manager.World.Commands.Create(PlayerEntity.Type);   // 模板拷贝
        var identity = order.Get<IdentityComponent>();
        identity.AccountId = accountId;                                 // 出生初值（私有字段）
        identity.Connected = true;
        identity.Kind.Value = EntityKind.Player;                        // Sync 字段：提交后按 Scope.Room 广播
        // 提交相：发号 → 亮相 → Awake → Start；ReplicationProjection 打成「创建记录」下发；
        // 客户端 World Manager 收到后按同一 PlayerEntity 模板建 → Awake → PostAttribute → Start。
    }

    /// <summary>⑦ 存档：对 WorldEntity 的 ServerRpc；存档系统在提交相消费，写文件走 outbox。</summary>
    public static void Save(WorldManager manager, string slot)
        => manager.World.Single<WorldSaveComponent>().Save(slot);

    /// <summary>⑦ 恢复：从快照建新世界（与 Create 同一条路）；只跑 OnHydrate；未标 [Persist] 的字段取声明默认值（Connected 回来是 false）。</summary>
    public static WorldManager Restore(byte[] snapshotBytes)
        => WorldManager.CreateFromSnapshot(snapshotBytes);
}
