using DarkwoodMultiplayerFramework.Protocol;

namespace DarkwoodMultiplayerFramework.Network;

/// <summary>
/// 0.8.9 第三刀：消息来源上下文。
/// </summary>
public readonly struct PeerContext
{
    /// <summary>消息来源玩家 ID（主机收到时为客户端的 connectionId；客户端收到时为主机即 0）。</summary>
    public int PeerId { get; }
    /// <summary>消息是否来自主机（客户端视角为 true）。</summary>
    public bool FromHost { get; }

    public PeerContext(int peerId, bool fromHost)
    {
        PeerId = peerId;
        FromHost = fromHost;
    }
}

/// <summary>
/// 0.8.9 第三刀：网络消息处理器。按领域实现，注册进 NetworkMessageRouter，
/// 取代 OnHostMessage / OnClientMessage 的巨型 if-else 链。
/// </summary>
public interface INetworkMessageHandler
{
    /// <summary>本处理器是否认领该消息类型。</summary>
    bool Handles(ProtocolMessageType type);
    /// <summary>处理消息；抛出的异常由调用方统一转换为协议错误。</summary>
    void Handle(PeerContext peer, ProtocolEnvelope envelope);
}
