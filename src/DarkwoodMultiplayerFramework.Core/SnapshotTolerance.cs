using System;

namespace DarkwoodMultiplayerFramework.Core;

/// <summary>
/// 快照容器绑定失败容忍阈值（FIX-007）。
/// 客户端注册表健康时，少量绑定失败源于主机运行时生成物（乌鸦、动物尸体等，
/// 无 Spawn 生命周期，0.8.8 欠账）——应跳过缺失对象继续就绪；
/// 大量失败说明注册表不完整（alpha.14 灾难：741 个容器中 738 个失败），必须阻断。
/// </summary>
public static class SnapshotTolerance
{
    /// <summary>失败数是否在可容忍范围内：最多 10 个，且不超过总数 5%。</summary>
    public static bool Tolerate(int failed, int total)
        => failed <= Math.Max(10, Math.Max(1, total * 5 / 100));
}
