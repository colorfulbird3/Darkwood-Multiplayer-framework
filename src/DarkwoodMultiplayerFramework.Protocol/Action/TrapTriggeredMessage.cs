namespace DarkwoodMultiplayerFramework.Protocol;

// ── v0.9.0（r17）客户端本地夹子触发 → Host 上报 ──
// 触发：夹子合拢发生在玩家本地（可信客户端本地物理：玩家/怪踩中 mirror 夹子 → 原版视觉合拢），
// Host 同实体（本体）未经历该碰撞 → 客户端上报；Host 把本体也合拢（switchToTriggered）→
// BroadcastStateNow 权威广播（triggered=true），其余端 Apply 复演合拢 → 两端夹子视觉一致。
// payload 与 RemoveWorldItem 同构：{EntityValue, Persistent}。
public readonly struct TrapTriggeredMessage
{
    public TrapTriggeredMessage(ulong entityValue, bool persistent)
    { EntityValue = entityValue; Persistent = persistent; }
    public ulong EntityValue { get; }
    public bool Persistent { get; }
}
