using System.Collections.Generic;

namespace DarkwoodMultiplayerFramework.Core;

/// <summary>
/// 运行时随机事件的一次性派发跟踪（距离门控的"已触发"侧）。
/// 语义：每个事件对每个玩家最多派发一次（乌鸦等一次性动画触发后，
/// 同一客户端离开再进入范围不再触发）；不同玩家互相独立。
/// 事件移除（Despawn）时清掉它的记录。
/// </summary>
public sealed class RuntimeEventDispatch
{
    private readonly Dictionary<ulong, HashSet<int>> _sent = new Dictionary<ulong, HashSet<int>>();

    /// <summary>尝试标记"已向该玩家派发此事件"。返回 true=首次派发；false=已发过（不再触发）。</summary>
    public bool TryMark(ulong eventId, int peer)
    {
        if (!_sent.TryGetValue(eventId, out var set))
        {
            set = new HashSet<int>();
            _sent[eventId] = set;
        }
        return set.Add(peer);
    }

    /// <summary>该事件是否已向该玩家派发过。</summary>
    public bool WasSent(ulong eventId, int peer) => _sent.TryGetValue(eventId, out var set) && set.Contains(peer);

    /// <summary>事件移除（Despawn）时清除它的派发记录。</summary>
    public void ClearEvent(ulong eventId) => _sent.Remove(eventId);

    /// <summary>会话/场景清理。</summary>
    public void Clear() => _sent.Clear();
}
