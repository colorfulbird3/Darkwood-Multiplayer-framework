using System;
using System.Collections.Generic;
using System.Reflection;
using DarkwoodMultiplayerFramework.Core;
using DarkwoodMultiplayerFramework.Protocol;
using HarmonyLib;
using UnityEngine;

namespace DarkwoodMultiplayerFramework.DarkwoodAdapter;

// ── P1 一次性同步工程：世界事件/flag/时钟/雨 + 通用表现事件（PresentationEvent）──
// 原则（吸收 coop 思想、不复制其架构）：host 是唯一剧情模拟器——host 在 vanilla 出口 hook
// （Events.fireWorldEvent / Flags.setFlag）捕获"真实发生"→ 可靠广播 → client 收到后在本端
// ApplyingRemote 语义下调用同一 vanilla 函数复演。client 端不轮询、不向 host 广播剧情。
// 现有协议消息复用：77 WorldEventFired、78/79 FlagBool/FlagIntChanged、80 ClockState、81 RainState
// （结构/codec 早已存在）；85 PresentationEvent（本批新增）承载气泡/地点发现/外部场景/标记等字符串事件。

public static class PresentationKinds
{
    // PresentationEvent.Kind（byte）语义表——按 kind 扩展无需改 wire。
    public const byte CharacterMessage = 1;      // Data: "line"（单行）；Target: 角色锚或空
    public const byte LocationDiscovered = 2;    // Target: trueLocationName
    public const byte OutsideLocationEntered = 3;// Target: locationName
    public const byte OutsideLocationReturned = 4;// Data: "b,b"
    public const byte MapMarkerShown = 5;        // Target: elementName
}

public sealed partial class DarkwoodAdapterRuntime
{
    // ── host→client 事件广播 helper（host 权威单源；client 端不调用）──
    private void QueueToReadyPeers(ProtocolMessageType type, byte[] payload)
    {
        foreach (var rp in ReadyPeersSnapshot) Queue(rp, type, payload);
    }

    /// <summary>通用表现事件（85）：气泡/地点发现/外部场景/地图标记等一次性的 host→client 复演载体。</summary>
    public void BroadcastPresentation(byte kind, string target, string data)
    {
        if (!Session.IsHost || quitting) return;
        var p = ReplicationProtocolCodec.Encode(new PresentationEventMessage(kind, target ?? string.Empty, data ?? string.Empty));
        QueueToReadyPeers(ProtocolMessageType.PresentationEvent, p);
        log?.LogInfo($"[EVENT] presentation kind={kind} target={target} data={data} → peers={ReadyPeersSnapshot.Length}");
    }

    /// <summary>世界剧情事件（77，Events.fireWorldEvent）。host 侧在 vanilla 出口捕获后调用。</summary>
    public void BroadcastWorldEventFired(string eventName)
    {
        if (!Session.IsHost || quitting || string.IsNullOrEmpty(eventName)) return;
        QueueToReadyPeers(ProtocolMessageType.WorldEventFired, ReplicationProtocolCodec.Encode(new WorldEventFiredMessage(eventName, string.Empty, serverTick)));
        log?.LogInfo($"[EVENT] worldEvent fired={eventName} → peers={ReadyPeersSnapshot.Length}");
    }

    /// <summary>剧情 flag（78/79）。host 侧在 Flags.setFlag 捕获后调用（传生效终值）。</summary>
    public void BroadcastFlagChanged(string flagName, bool value)
    {
        if (!Session.IsHost || quitting || string.IsNullOrEmpty(flagName)) return;
        QueueToReadyPeers(ProtocolMessageType.FlagBoolChanged, ReplicationProtocolCodec.Encode(new FlagBoolChangedMessage(flagName, value)));
        log?.LogInfo($"[EVENT] flag bool {flagName}={value} → peers={ReadyPeersSnapshot.Length}");
    }
    public void BroadcastFlagChanged(string flagName, int value)
    {
        if (!Session.IsHost || quitting || string.IsNullOrEmpty(flagName)) return;
        QueueToReadyPeers(ProtocolMessageType.FlagIntChanged, ReplicationProtocolCodec.Encode(new FlagIntChangedMessage(flagName, value)));
        log?.LogInfo($"[EVENT] flag int {flagName}={value} → peers={ReadyPeersSnapshot.Length}");
    }

    /// <summary>时钟权威状态（80）。host 每 5s 心跳 + 沿（日推进/恢复走动）即时补发由调用方决定。</summary>
    public void BroadcastClockState()
    {
        if (!Session.IsHost || quitting) return;
        try
        {
            var c = Singleton<global::Controller>.Instance;
            if (c == null) return;
            QueueToReadyPeers(ProtocolMessageType.ClockState, ReplicationProtocolCodec.Encode(new ClockStateMessage(c.CurrentTime, c.day, !c.DoUpdateTime)));
        }
        catch (Exception error) { log?.LogWarning($"[EVENT] clock 心跳失败：{error.Message}"); }
    }

    /// <summary>雨/雾权威状态（81）。host 每 5s 心跳。</summary>
    public void BroadcastRainState()
    {
        if (!Session.IsHost || quitting) return;
        try
        {
            var r = Singleton<global::Rain>.Instance;
            if (r == null) return;
            QueueToReadyPeers(ProtocolMessageType.RainState, ReplicationProtocolCodec.Encode(new RainStateMessage(r.rainToday, r.timeToStart)));
        }
        catch (Exception error) { log?.LogWarning($"[EVENT] rain 心跳失败：{error.Message}"); }
    }

    // ── host 侧剧情出口 hooks ──
    internal static bool IsHostStoryReady()
    {
        var r = Instance;
        return r != null && r.Session.IsHost && !r.quitting && r.State == ConnectionState.Ready;
    }

    // ── host 时钟/雨权威心跳（每 5s 全量 + 沿即时），client 端只收不发 ──
    private float nextStoryHeartbeatAt;
    private int lastStoryDay = -1;
    private bool lastStoryTimeWalking = true;
    public void TickStoryHeartbeat()
    {
        if (!Session.IsHost || quitting || readyPeers.Count == 0) return;
        var c = Singleton<global::Controller>.Instance;
        if (c == null) return;
        var walking = c.DoUpdateTime;
        bool immediate = false;
        if (c.day != lastStoryDay) { lastStoryDay = c.day; immediate = true; }
        if (walking != lastStoryTimeWalking) { lastStoryTimeWalking = walking; immediate = true; } // 恢复走动/冻结沿
        if (Time.unscaledTime < nextStoryHeartbeatAt && !immediate) return;
        nextStoryHeartbeatAt = Time.unscaledTime + 5f;
        try { BroadcastClockState(); } catch (Exception error) { log?.LogWarning($"[EVENT] clock 广播异常：{error.Message}"); }
        try { BroadcastRainState(); } catch (Exception error) { log?.LogWarning($"[EVENT] rain 广播异常：{error.Message}"); }
    }

    // ── client 侧复演（收 host 权威事件 → 本端 ApplyingRemote 语义调同一 vanilla API / 写同字段）──
    private void ClientApplyWorldEventFired(WorldEventFiredMessage m)
    {
        if (!IsClient || clientSession?.Session.Lifecycle.State != ConnectionState.Ready) return;
        try
        {
            var e = Singleton<Events>.Instance;
            if (e == null) { log?.LogWarning($"[EVENT] client 无 Events 单例，丢弃 worldEvent {m.EventName}"); return; }
            replication.BeginRemoteApply();
            try { e.fireWorldEvent(m.EventName); }
            finally { replication.EndRemoteApply(); }
            log?.LogInfo($"[EVENT] client 复演 worldEvent fired={m.EventName}");
        }
        catch (Exception error) { log?.LogWarning($"[EVENT] client 复演 worldEvent 失败：{error.Message}"); }
    }
    private void ClientApplyFlag(FlagBoolChangedMessage m)
    {
        if (!IsClient || clientSession?.Session.Lifecycle.State != ConnectionState.Ready) return;
        try
        {
            var f = Singleton<Flags>.Instance;
            if (f == null) return;
            replication.BeginRemoteApply();
            try { f.setFlag(m.Flag, m.Value); }
            finally { replication.EndRemoteApply(); }
            log?.LogInfo($"[EVENT] client 复演 flag bool {m.Flag}={m.Value}");
        }
        catch (Exception error) { log?.LogWarning($"[EVENT] client 复演 flag 失败：{error.Message}"); }
    }
    private void ClientApplyFlag(FlagIntChangedMessage m)
    {
        if (!IsClient || clientSession?.Session.Lifecycle.State != ConnectionState.Ready) return;
        try
        {
            var f = Singleton<Flags>.Instance;
            if (f == null) return;
            replication.BeginRemoteApply();
            try { f.setFlag(m.Flag, m.Value); }
            finally { replication.EndRemoteApply(); }
            log?.LogInfo($"[EVENT] client 复演 flag int {m.Flag}={m.Value}");
        }
        catch (Exception error) { log?.LogWarning($"[EVENT] client 复演 flag 失败：{error.Message}"); }
    }
    private void ClientApplyClock(ClockStateMessage m)
    {
        if (!IsClient || clientSession?.Session.Lifecycle.State != ConnectionState.Ready) return;
        try
        {
            var c = Singleton<global::Controller>.Instance;
            if (c == null) return;
            // 只搬标量让 vanilla 自己跑（refreshTimeNoLogic 语义）；日更替走无副作用刷新。
            if (m.Day > c.day)
            {
                c.day = m.Day;
                c.CurrentTime = (int)m.HourOfDay;
                try { c.refreshTime(false); } catch (Exception) { }
            }
            else if (Math.Abs(c.CurrentTime - m.HourOfDay) > 2f)
            {
                c.CurrentTime = (int)m.HourOfDay;
            }
            var wantWalking = !m.Paused;
            if (c.DoUpdateTime != wantWalking)
            {
                try { c.DoUpdateTime = wantWalking; } catch (Exception) { }
            }
            log?.LogInfo($"[EVENT] client 复演 clock day={m.Day} hour={m.HourOfDay:F1} paused={m.Paused}");
        }
        catch (Exception error) { log?.LogWarning($"[EVENT] client 复演 clock 失败：{error.Message}"); }
    }
    private void ClientApplyRain(RainStateMessage m)
    {
        if (!IsClient || clientSession?.Session.Lifecycle.State != ConnectionState.Ready) return;
        try
        {
            var r = Singleton<global::Rain>.Instance;
            if (r == null) return;
            r.rainToday = m.Active;
            r.timeToStart = m.Intensity;
            log?.LogInfo($"[EVENT] client 复演 rain active={m.Active} timeToStart={m.Intensity}");
        }
        catch (Exception error) { log?.LogWarning($"[EVENT] client 复演 rain 失败：{error.Message}"); }
    }
    private void ClientApplyPresentation(PresentationEventMessage m)
    {
        if (!IsClient || clientSession?.Session.Lifecycle.State != ConnectionState.Ready) return;
        // kind 分派：P1 仅登记；气泡/发现/外部场景/标记的具体复演随 P3 扩展，避免死代码。
        switch (m.Kind)
        {
            default:
                log?.LogInfo($"[EVENT] client 收到 presentation kind={m.Kind} target={m.Target} data={m.Data}（复演待接入）");
                break;
        }
    }
}

/// <summary>Events.fireWorldEvent(string) —— vanilla 世界剧情事件统一出口（coop WorldEventSync 同 hook 点）。</summary>
[HarmonyPatch]
internal static class DarkwoodWorldEventHook
{
    private static MethodBase TargetMethod() => AccessTools.Method(typeof(Events), "fireWorldEvent", new[] { typeof(string) });
    private static void Postfix(string type)
    {
        if (!DarkwoodAdapterRuntime.IsHostStoryReady()) return;
        try { DarkwoodAdapterRuntime.Instance?.BroadcastWorldEventFired(type); }
        catch (Exception) { }
    }
}

/// <summary>Flags.setFlag —— 剧情/任务 flag（bool/int 两重载）。只同步"生效终值"（避免 setter modifier 语义丢失）。</summary>
[HarmonyPatch]
internal static class DarkwoodFlagHookBool
{
    private static MethodBase TargetMethod() => AccessTools.Method(typeof(Flags), "setFlag", new[] { typeof(string), typeof(bool) });
    private static void Postfix(string flag)
    {
        if (!DarkwoodAdapterRuntime.IsHostStoryReady()) return;
        try
        {
            var f = Singleton<Flags>.Instance;
            if (f == null) return;
            DarkwoodAdapterRuntime.Instance?.BroadcastFlagChanged(flag, f.isFlagTrue(flag, 0, (global::EqualsEnum)30));
        }
        catch (Exception) { }
    }
}

[HarmonyPatch]
internal static class DarkwoodFlagHookInt
{
    private static MethodBase TargetMethod() => AccessTools.Method(typeof(Flags), "setFlag", new[] { typeof(string), typeof(int) });
    private static void Postfix(string flag)
    {
        if (!DarkwoodAdapterRuntime.IsHostStoryReady()) return;
        try
        {
            var f = Singleton<Flags>.Instance;
            if (f == null) return;
            DarkwoodAdapterRuntime.Instance?.BroadcastFlagChanged(flag, f.getFlagInt(flag));
        }
        catch (Exception) { }
    }
}
