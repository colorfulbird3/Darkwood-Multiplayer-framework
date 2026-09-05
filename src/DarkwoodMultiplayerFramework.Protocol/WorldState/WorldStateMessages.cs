namespace DarkwoodMultiplayerFramework.Protocol;

// ── v0.9.5（P3 WorldState 域）世界级事件/状态消息 ──
// 语义：Host 单点决定（时钟/天气/夜事件/flag/任务），广播事件；Client 只读镜像、按幂等规则应用。
// 借鉴 Coop 的事件广播域（NightEvent/Flag/WorldEvent…）的组织，但保持 DMF 的 Host 权威 + 无审批信任纪律。

public readonly struct WorldEventFiredMessage
{
    public WorldEventFiredMessage(string eventName, string payload, long serverTick)
    { EventName = eventName ?? string.Empty; Payload = payload ?? string.Empty; ServerTick = serverTick; }
    /// <summary>事件名（≤64B），如 "night_scenario_start"/"quest_trigger:xxx"。</summary>
    public string EventName { get; }
    /// <summary>事件参数（≤2048B，JSON 或键值文本；未来可改 typed）。</summary>
    public string Payload { get; }
    public long ServerTick { get; }
}

public readonly struct FlagBoolChangedMessage
{
    public FlagBoolChangedMessage(string flag, bool value) { Flag = flag ?? string.Empty; Value = value; }
    public string Flag { get; }
    public bool Value { get; }
}

public readonly struct FlagIntChangedMessage
{
    public FlagIntChangedMessage(string flag, int value) { Flag = flag ?? string.Empty; Value = value; }
    public string Flag { get; }
    public int Value { get; }
}

public readonly struct ClockStateMessage
{
    public ClockStateMessage(float hourOfDay, int day, bool paused)
    { HourOfDay = hourOfDay; Day = day; Paused = paused; }
    /// <summary>当日小时（0..24，Host 权威）。</summary>
    public float HourOfDay { get; }
    public int Day { get; }
    public bool Paused { get; }
}

public readonly struct RainStateMessage
{
    public RainStateMessage(bool active, float intensity)
    { Active = active; Intensity = intensity; }
    public bool Active { get; }
    public float Intensity { get; }
}
