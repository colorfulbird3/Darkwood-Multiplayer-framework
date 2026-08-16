using System.Collections.Generic;
using DarkwoodMultiplayerFramework.Protocol;

namespace DarkwoodMultiplayerFramework.Network;

/// <summary>
/// 0.8.9 第三刀：消息路由器。处理器注册制——新增消息类型时注册新 handler，
/// 不再修改中央 switch。
/// </summary>
public sealed class NetworkMessageRouter
{
    private readonly List<INetworkMessageHandler> handlers = new List<INetworkMessageHandler>();

    public void Register(INetworkMessageHandler handler) => handlers.Add(handler);

    /// <summary>分发消息。返回 false 表示没有任何处理器认领（调用方自行记录）。</summary>
    public bool Dispatch(PeerContext peer, ProtocolEnvelope envelope)
    {
        for (var i = 0; i < handlers.Count; i++)
        {
            if (handlers[i].Handles(envelope.MessageType))
            {
                handlers[i].Handle(peer, envelope);
                return true;
            }
        }
        return false;
    }
}
