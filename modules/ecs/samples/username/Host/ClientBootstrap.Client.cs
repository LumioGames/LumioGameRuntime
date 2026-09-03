// 客户端宿主侧用法（② 建世界，客户端这一半）。只进 *.Client.csproj。
using System.Threading;
using Lumio.GameRuntime.Ecs;
using Lumio.GameRuntime.Samples.Username;

namespace Lumio.GameRuntime.Samples.Username.Host;

public static class ClientBootstrap
{
    /// <summary>
    /// ② 建世界：和服务器同一个 Create，只差不传 instanceId（客户端不发号；生成的注册表自带端别，不传模式参数）。
    /// 进入游戏 = 建 Manager；退出 / 换房间 = 销毁重建。
    /// </summary>
    public static WorldManager Boot()
    {
        var manager = WorldManager.Create(GeneratedRegistry.Instance);
        manager.Start(ownerThread: Thread.CurrentThread);      // 同服务器：记线程归属，网络线程只能 Enqueue
        // 主线程每帧：manager.Tick()——提交相把本 Tick 收到的整包一次性生效（拼好再生效：先全部写入，再统一触发 OnXChanged）
        return manager;
    }

    /// <summary>
    /// 网络线程收到的每条消息都只做这一件事。连上后按顺序到达：
    ///   1. 欢迎消息（世界实例 ID + 你自己的 NetEntityId）→ Manager 在提交相绑定 World.Self
    ///   2. 创建记录（EntityType + NetEntityId + 可见字段当前值），第一条就是 WorldEntity（客户端不自建它）
    ///   3. 之后每 Tick 一包：创建 / 字段变化 / 销毁记录 + 本 Tick 的 ClientRpc 事件
    /// 同进程双端（单机 / 本地联调）：服务器 Manager 的 outbox 直接投到这里，同一行代码，语义与联网零差异。
    /// </summary>
    public static void OnNetworkMessage(WorldManager manager, WorldMessage message)   // WorldMessage = 欢迎消息 / 世界变化记录（服务器侧还有 InputCommand）
        => manager.Enqueue(message);
}
