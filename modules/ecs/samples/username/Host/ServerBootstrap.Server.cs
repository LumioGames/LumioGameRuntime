// 服务器宿主侧用法（② 建世界 · ③ 创建 · ⑦ 存档与恢复）。只进 *.Server.csproj。
using System.Threading;
using Lumio.GameRuntime.Ecs;
using Lumio.GameRuntime.Samples.Username;
using Lumio.GameRuntime.Samples.Username.Components.Identity;
using Lumio.GameRuntime.Samples.Username.EntityTypes;

namespace Lumio.GameRuntime.Samples.Username.Host;

public static class ServerBootstrap
{
    /// <summary>② 建世界：一进程一个 WorldManager；WorldEntity 按 EntityTypes/WorldEntity.cs 随世界诞生。</summary>
    public static WorldManager Boot(ulong hostGivenInstanceId)
    {
        var manager = WorldManager.Create(GeneratedRegistry.Instance, instanceId: hostGivenInstanceId);   // 服务器发号：实例 ID 由宿主给
        manager.Start(ownerThread: Thread.CurrentThread);      // 记线程归属；之后所有入口校验，网络线程只能 Enqueue
        // 主线程每帧：manager.Tick()——ApplyInputs 相消费 inbox，提交相发号 / 亮相 / 打包下发
        return manager;
    }

    /// <summary>③ 创建：准入服务（Manager 的服务之一）在 ApplyInputs 相下单。NetEntityId 在提交相由世界发（实例 ID + 计数器）。</summary>
    public static void AdmitPlayer(WorldManager manager, string accountId)
    {
        var order = manager.World.Commands.Create<PlayerEntity>();      // 模板拷贝；实体是什么由 EntityType 决定，不另设字段（声明类无成员，用泛型指类型）
        var identity = order.Get<IdentityComponent>();
        identity.AccountId = accountId;                                 // 出生初值（服务器私有字段）
        order.Get<ObserverComponent>().Connected = true;
        order.Get<ObserverComponent>().ConnectionGeneration = 1;
        // 提交相：发号 → 亮相 → Awake → Start；ReplicationProjection 打成「创建记录」（EntityType + NetEntityId + 可见字段当前值）下发；
        // 客户端 World Manager 收到后按同一 PlayerEntity 模板建 → Awake → PostAttribute → Start。
    }

    /// <summary>⑦ 存档：对 WorldEntity 的 ServerRpc；存档系统在提交相消费，写文件走 outbox。</summary>
    public static void Save(WorldManager manager, string slot)
        => manager.World.Single<WorldSaveComponent>().Save(slot);

    /// <summary>⑦ 恢复：从快照建新世界（与 Create 同一条路）；只跑 OnHydrate；未标 [Persist] 的字段取声明默认值（Connected 回来是 false）。</summary>
    public static WorldManager Restore(byte[] snapshotBytes)
    {
        EcsRegistry.Current = GeneratedRegistry.Instance;
        return WorldManager.CreateFromSnapshot(snapshotBytes);
    }
}
