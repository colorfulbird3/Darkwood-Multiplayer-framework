namespace DarkwoodMultiplayerFramework.Protocol;

// ── v0.9.0（A3）客户端本地移除的持久世界物 → Host 销毁请求 ──
// 触发：客户端因原版玩法（如拆除捕兽夹/回收部署物）销毁了一个**持久绑定实体**，
// 而 Host 同实体仍在。客户端上报；Host 销毁自身副本 → 权威 despawn 广播（其余端清理）。
public readonly struct RemoveWorldItemMessage
{
    public RemoveWorldItemMessage(ulong entityValue, bool persistent)
    { EntityValue = entityValue; Persistent = persistent; }
    public ulong EntityValue { get; }
    public bool Persistent { get; }
}
