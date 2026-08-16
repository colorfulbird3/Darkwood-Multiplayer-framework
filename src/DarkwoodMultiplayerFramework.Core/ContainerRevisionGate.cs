namespace DarkwoodMultiplayerFramework.Core;

/// <summary>
/// 0.8.9-alpha.1：容器并发乐观锁（Container Revision）。
/// 客户端上报容器状态时携带"基于的主机版本"（expected）；主机只在
/// expected == 当前权威版本 + 1 时接受，否则判定并发冲突并回权威状态。
/// 语义：客户端 CaptureAuthoritativeInventory 的 Revision = 本地已知版本 + 1。
/// </summary>
public static class ContainerRevisionGate
{
    /// <summary>客户端上报（expected）是否落在主机当前版本（current）之上。接受时返回下一版本号。</summary>
    public static bool TryAdvance(ulong expected, ulong current, out ulong next)
    {
        next = current;
        if (expected != current + 1) return false;
        next = current + 1;
        return true;
    }
}
