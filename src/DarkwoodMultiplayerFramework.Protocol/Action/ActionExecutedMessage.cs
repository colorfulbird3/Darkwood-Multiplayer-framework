namespace DarkwoodMultiplayerFramework.Protocol;

// ── v0.9.0（Action Sync）Host→All：已执行的原版副作用回放 ──
// 三分法同步里 Action 层的事件载体：Host 唯一执行原版函数后广播；
// 各端在 ApplyingRemote 内 Replay 同一函数，使视觉/电源/破坏等副作用两端一致。
// 字段语义：EntityId(value+persistent) + ActionKey(byte，管理器注册表) + Param(≤2KB) + Tick(去重) + ActorId。
public readonly struct ActionExecutedMessage
{
    public ActionExecutedMessage(ulong entityValue, bool persistent, byte actionKey, byte[] param, long tick, int actorId)
    {
        EntityValue = entityValue; Persistent = persistent; ActionKey = actionKey;
        Param = param ?? System.Array.Empty<byte>(); Tick = tick; ActorId = actorId;
    }
    public ulong EntityValue { get; }
    public bool Persistent { get; }
    public byte ActionKey { get; }
    public byte[] Param { get; }
    public long Tick { get; }
    public int ActorId { get; }
}
