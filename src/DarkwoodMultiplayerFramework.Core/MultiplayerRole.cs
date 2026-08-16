namespace DarkwoodMultiplayerFramework.Core;

/// <summary>本机在多人会话中的角色（0.8.9 RuntimeContext 的会话权威字段）。</summary>
public enum MultiplayerRole
{
    /// <summary>未加入任何会话。</summary>
    Disconnected = 0,
    /// <summary>主机：权威世界，广播状态，接受连接。</summary>
    Host = 1,
    /// <summary>客户端：接收权威状态，本地执行 + 上报。</summary>
    Client = 2
}
