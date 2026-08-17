using System;
using System.Collections.Generic;
using DarkwoodMultiplayerFramework.Core;
using DarkwoodMultiplayerFramework.Protocol;

namespace DarkwoodMultiplayerFramework.DarkwoodAdapter;

/// <summary>
/// 收口：Service 对 Runtime 的最小依赖面。
/// 领域服务只允许通过本接口访问会话/就绪玩家/发送/日志——
/// 不再直接持有整个 DarkwoodAdapterRuntime。
/// </summary>
internal interface IMultiplayerRuntimeHost
{
    SessionContext Session { get; }
    IReadOnlyCollection<int> ReadyPeers { get; }
    long ServerTick { get; }
    string CurrentScene { get; }
    /// <summary>实体复制管理器（Combat 的近战解析/怪物伤害需要读实体表）。</summary>
    DarkwoodEntityReplication Replication { get; }
    /// <summary>玩家服务（Combat 的倒地/营救需要读远端坐标）。</summary>
    DarkwoodPlayerService Players { get; }
    /// <summary>本机玩家 ID（客户端 = 握手分配的 PeerId，主机 = 0，未连接 = -1）。</summary>
    int LocalPeerId { get; }

    void Queue(int peer, ProtocolMessageType type, byte[] payload);
    /// <summary>客户端 → 主机发送（非客户端时为空操作）。</summary>
    void SendToHost(ProtocolMessageType type, byte[] payload);
    /// <summary>延迟停服（全员倒地结局回调）。</summary>
    void ScheduleStop(float delay);

    void LogInfo(string message);
    void LogWarning(string message);
}
