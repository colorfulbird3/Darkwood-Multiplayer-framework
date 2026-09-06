namespace DarkwoodMultiplayerFramework.Protocol;

// ── v0.9.0（P1 一次性同步工程）通用瞬时表现事件（Host→All）──
// 承载无法用 typed/EntityDelta 表达的一次性视听/剧情现象：文字气泡、地点发现、外部场景进出、
// 地图标记揭示、受击动画、声音等。semantic kind 由 Adapter 侧定义（byte，见 WorldEvent/PresentationKinds），
// Target = 目标锚（EntityId 字符串 / 本地自解释 key / 空=全局），Data = 按 kind 约定的分隔参数串。
// 复演纪律：client 收到后在 ApplyingRemote guard 内调用与 host 相同的 vanilla 表现函数/写同字段；幂等去重按
// (kind,target,data) 短窗。任何 kind 扩展不改 wire。
public readonly struct PresentationEventMessage
{
    public PresentationEventMessage(byte kind, string target, string data)
    { Kind = kind; Target = target ?? string.Empty; Data = data ?? string.Empty; }
    public byte Kind { get; }
    public string Target { get; }
    public string Data { get; }
}
