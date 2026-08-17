namespace DarkwoodMultiplayerFramework.Core;

/// <summary>
/// 第二刀：会话上下文——会话维度的权威状态（角色/状态/身份/场景）。
/// 取代散落的 hostSession != null / clientSession != null 隐式判断；
/// 各服务一律通过 context.Session.IsHost / IsClient 阅读，不再摸会话对象。
/// </summary>
public sealed class SessionContext
{
    /// <summary>本机角色。</summary>
    public MultiplayerRole Role { get; set; } = MultiplayerRole.Disconnected;

    /// <summary>当前联机状态机状态。</summary>
    public ConnectionState State { get; set; } = ConnectionState.Disconnected;

    /// <summary>本机玩家 ID（主机恒 0；客户端为握手分配的 PeerId）。</summary>
    public int LocalPeerId { get; set; } = -1;

    /// <summary>主机会话 ID（客户端握手获得，主机自身生成）。</summary>
    public System.Guid SessionId { get; set; }

    /// <summary>当前活跃场景名。</summary>
    public string Scene { get; set; } = string.Empty;

    /// <summary>最近一次会话错误（握手失败/掉线原因）。</summary>
    public string Error { get; set; } = string.Empty;

    public bool IsHost => Role == MultiplayerRole.Host;
    public bool IsClient => Role == MultiplayerRole.Client;
    public bool IsActive => Role != MultiplayerRole.Disconnected;

    /// <summary>是否已完成多人初始化（主机已监听 / 客户端已握手）。</summary>
    public bool IsMultiplayerActive { get; set; }

    /// <summary>重置为断开状态。</summary>
    public void Reset()
    {
        Role = MultiplayerRole.Disconnected;
        State = ConnectionState.Disconnected;
        LocalPeerId = -1;
        SessionId = System.Guid.Empty;
        Scene = string.Empty;
        Error = string.Empty;
        IsMultiplayerActive = false;
    }
}
