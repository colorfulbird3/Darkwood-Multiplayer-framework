using System;
using System.Collections.Generic;
using System.Reflection;
using DarkwoodMultiplayerFramework.Core;
using DarkwoodMultiplayerFramework.Protocol;
using HarmonyLib;
using UnityEngine;

namespace DarkwoodMultiplayerFramework.DarkwoodAdapter;

// ── P2/P3 表现事件域：世界物（爆炸）+ 文字/探索（气泡/地点发现/外部场景/地图标记）──
// host 权威：host 在 vanilla 出口捕获真实发生 → BroadcastPresentation → client 复演同一 vanilla
// 表现函数（游戏性副作用归零：伤害/扣物由 host 世界结算，client 只复现动画/FX/状态）。
// 防回声：client 端这些 vanilla 出口的 host hook 因 IsHostStoryReady=false 不会回发。

public sealed partial class DarkwoodAdapterRuntime
{
    private void ClientReplayPresentation(PresentationEventMessage m)
    {
        try
        {
            switch (m.Kind)
            {
                case PresentationKinds.CharacterMessage: ReplayCharacterMessage(m); break;
                case PresentationKinds.LocationDiscovered: ReplayLocationDiscovered(m); break;
                case PresentationKinds.OutsideLocationEntered: ReplayOutsideEntered(m); break;
                case PresentationKinds.OutsideLocationReturned: ReplayOutsideReturned(m); break;
                case PresentationKinds.MapMarkerShown: ReplayMapMarker(m); break;
                case PresentationKinds.ExplodeActivate: ReplayExplode(m); break;
                default:
                    log?.LogInfo($"[EVENT] client 收到 presentation kind={m.Kind} target={m.Target} data={m.Data}（未注册复演）");
                    break;
            }
        }
        catch (Exception error) { log?.LogWarning($"[EVENT] presentation kind={m.Kind} 复演异常：{error.Message}"); }
    }

    // 气泡：在 target（name@x_z 或空=全局）定位说话者 → Core.displayMessage 复演（单行）
    private void ReplayCharacterMessage(PresentationEventMessage m)
    {
        Transform anchor = ResolveTextAnchor(m.Target);
        if (anchor == null && !string.IsNullOrEmpty(m.Target))
        {
            log?.LogInfo($"[EVENT] 气泡锚 {m.Target} 未定位（可能不在本端已加载区），跳过");
            return;
        }
        replication.BeginRemoteApply();
        try { global::Core.displayMessage(m.Data, anchor, 3f, false); }
        finally { replication.EndRemoteApply(); }
        log?.LogInfo($"[EVENT] client 复演气泡 target={m.Target} text={m.Data}");
    }

    private Transform ResolveTextAnchor(string target)
    {
        if (string.IsNullOrEmpty(target)) return null;
        var parts = target.Split('|');
        if (parts.Length < 3) return null;
        var name = parts[0];
        if (!float.TryParse(parts[1], out var x) || !float.TryParse(parts[2], out var z)) return null;
        Transform best = null; float bestDist = float.MaxValue;
        foreach (var t in UnityEngine.Object.FindObjectsOfType<Transform>(true))
        {
            if (t == null || t.gameObject == null) continue;
            if (!string.Equals(t.name, name, StringComparison.Ordinal)) continue;
            var d = Vector3.Distance(t.position, new Vector3(x, 0f, z));
            if (d < bestDist) { bestDist = d; best = t; }
        }
        return bestDist <= 40f ? best : null;
    }

    private void ReplayLocationDiscovered(PresentationEventMessage m)
    {
        if (string.IsNullOrEmpty(m.Target)) return;
        var wg = Singleton<global::WorldGenerator>.Instance;
        if (wg == null) return;
        var loc = wg.getLocation(m.Target);
        if (loc == null) { log?.LogInfo($"[EVENT] client 复演地点发现 {m.Target} 未找到 Location（区域未加载）→ 跳过"); return; }
        replication.BeginRemoteApply();
        try { loc.discoverMe(true, false, false); }
        finally { replication.EndRemoteApply(); }
        log?.LogInfo($"[EVENT] client 复演地点发现 {m.Target}");
    }

    private void ReplayOutsideEntered(PresentationEventMessage m)
    {
        if (string.IsNullOrEmpty(m.Target)) return;
        var o = Singleton<global::OutsideLocations>.Instance;
        if (o == null) return;
        replication.BeginRemoteApply();
        try { o.prepareLocation(m.Target, null); }
        finally { replication.EndRemoteApply(); }
        log?.LogInfo($"[EVENT] client 复演进入外部场景 {m.Target}");
    }

    private void ReplayOutsideReturned(PresentationEventMessage m)
    {
        var o = Singleton<global::OutsideLocations>.Instance;
        if (o == null) return;
        var p = m.Data.Split('|');
        var a = p.Length > 0 && p[0] == "1"; var b = p.Length > 1 && p[1] == "1";
        replication.BeginRemoteApply();
        try { o.returnToWorld(a, b); }
        finally { replication.EndRemoteApply(); }
        log?.LogInfo($"[EVENT] client 复演外部场景返回 tween={b}");
    }

    private void ReplayMapMarker(PresentationEventMessage m)
    {
        if (string.IsNullOrEmpty(m.Target)) return;
        var map = Singleton<global::Map>.Instance;
        if (map == null) return;
        var el = map.getElement(m.Target);
        if (el == null) { log?.LogInfo($"[EVENT] client 复演地图标记 {m.Target} 未找到元素 → 跳过"); return; }
        replication.BeginRemoteApply();
        try { map.showElement(el); }
        finally { replication.EndRemoteApply(); }
        log?.LogInfo($"[EVENT] client 复演地图标记 {m.Target}");
    }

    // 爆炸复演：位置找 Explodes（≤4m）→ 游戏性副作用归零（伤害/力/效果）→ onActivate()（视觉+音效+桶销毁）
    private void ReplayExplode(PresentationEventMessage m)
    {
        if (!TryParseVec3(m.Data, out var pos)) return;
        Explodes target = null; float best = float.MaxValue;
        foreach (var e in UnityEngine.Object.FindObjectsOfType<Explodes>(true))
        {
            if (e == null || e.gameObject == null) continue;
            var d = Vector3.Distance(e.transform.position, pos);
            if (d < best) { best = d; target = e; }
        }
        if (target == null || best > 4f) { log?.LogInfo($"[EVENT] client 爆炸复演 pos={pos} 未找到 Explodes（可能已炸/未加载）→ 跳过"); return; }
        replication.BeginRemoteApply();
        try
        {
            target.damage = 0f; target.affectsPlayer = false; target.force = 0f; target.hasEffect = false;
            target.onActivate();
        }
        finally { replication.EndRemoteApply(); }
        log?.LogInfo($"[EVENT] client 复演爆炸 pos={pos}（副作用归零）");
    }

    private static bool TryParseVec3(string s, out Vector3 v)
    {
        v = Vector3.zero;
        if (string.IsNullOrEmpty(s)) return false;
        var p = s.Split('|');
        if (p.Length < 3) return false;
        return float.TryParse(p[0], out v.x) && float.TryParse(p[1], out v.y) && float.TryParse(p[2], out v.z);
    }
}

// ── host 侧出口 hooks：世界物/文字/探索（全部经 IsHostStoryReady 只在 host 广播）──

/// <summary>Explodes.onActivate() —— 爆炸桶引爆（coop ExplodableSync 同 hook 点）。</summary>
[HarmonyPatch]
internal static class DarkwoodExplodeHook
{
    private static MethodBase TargetMethod() => AccessTools.Method(typeof(Explodes), "onActivate", Type.EmptyTypes);
    private static void Postfix(Explodes __instance)
    {
        if (!DarkwoodAdapterRuntime.IsHostStoryReady()) return;
        try
        {
            var p = __instance != null && __instance.transform != null ? __instance.transform.position : Vector3.zero;
            DarkwoodAdapterRuntime.Instance?.BroadcastPresentation(PresentationKinds.ExplodeActivate, string.Empty, $"{p.x}|{p.y}|{p.z}");
        }
        catch (Exception) { }
    }
}

/// <summary>Location.discoverMe(bool,bool,bool) —— 地点发现（coop LocationDiscoverySync 同 hook）。</summary>
[HarmonyPatch]
internal static class DarkwoodLocationDiscoverHook
{
    private static MethodBase TargetMethod() => AccessTools.Method(typeof(global::Location), "discoverMe", new[] { typeof(bool), typeof(bool), typeof(bool) });
    private static void Postfix(global::Location __instance)
    {
        if (!DarkwoodAdapterRuntime.IsHostStoryReady()) return;
        try
        {
            var trueName = global::Core.getTrueLocationName(__instance.gameObject.name);
            if (!string.IsNullOrEmpty(trueName))
                DarkwoodAdapterRuntime.Instance?.BroadcastPresentation(PresentationKinds.LocationDiscovered, trueName, string.Empty);
        }
        catch (Exception) { }
    }
}

/// <summary>OutsideLocations.prepareLocation(string, Transform) / returnToWorld(bool,bool) —— 外部场景进出。</summary>
[HarmonyPatch]
internal static class DarkwoodOutsideEnterHook
{
    private static MethodBase TargetMethod() => AccessTools.Method(typeof(global::OutsideLocations), "prepareLocation", new[] { typeof(string), typeof(Transform) });
    private static void Postfix(string locationName)
    {
        if (!DarkwoodAdapterRuntime.IsHostStoryReady()) return;
        try
        {
            var dreams = Singleton<global::Dreams>.Instance;
            if (dreams != null && dreams.dreamPrepared) return; // 梦境单人行，复用同一传送钩子——排除
            if (!string.IsNullOrEmpty(locationName))
                DarkwoodAdapterRuntime.Instance?.BroadcastPresentation(PresentationKinds.OutsideLocationEntered, locationName, string.Empty);
        }
        catch (Exception) { }
    }
}

[HarmonyPatch]
internal static class DarkwoodOutsideReturnHook
{
    private static MethodBase TargetMethod() => AccessTools.Method(typeof(global::OutsideLocations), "returnToWorld", new[] { typeof(bool), typeof(bool) });
    private static void Postfix(bool teleportToPositionCopy, bool tweenScreens)
    {
        if (!DarkwoodAdapterRuntime.IsHostStoryReady()) return;
        try
        {
            DarkwoodAdapterRuntime.Instance?.BroadcastPresentation(PresentationKinds.OutsideLocationReturned, string.Empty, $"{(teleportToPositionCopy ? 1 : 0)}|{(tweenScreens ? 1 : 0)}");
        }
        catch (Exception) { }
    }
}

/// <summary>Map.showElement(MapElement) —— 地图标记揭示（coop MapMarkerSync 同 hook）。</summary>
[HarmonyPatch]
internal static class DarkwoodMapMarkerHook
{
    private static MethodBase TargetMethod() => AccessTools.Method(typeof(global::Map), "showElement", new[] { typeof(global::MapElement) });
    private static void Postfix(global::MapElement element)
    {
        if (!DarkwoodAdapterRuntime.IsHostStoryReady()) return;
        try
        {
            if (element != null && !string.IsNullOrEmpty(element.elementName))
                DarkwoodAdapterRuntime.Instance?.BroadcastPresentation(PresentationKinds.MapMarkerShown, element.elementName, string.Empty);
        }
        catch (Exception) { }
    }
}

/// <summary>Core.displayMessage —— 非玩家说话者（NPC/敌人/世界物件）的气泡文字（玩家自身提示各端本地）。</summary>
[HarmonyPatch]
internal static class DarkwoodDisplayMessageHook
{
    private static MethodBase TargetMethod() => AccessTools.Method(typeof(global::Core), "displayMessage", new[] { typeof(string), typeof(Transform), typeof(float), typeof(bool) });
    private static void Postfix(string text, Transform destTransform)
    {
        if (!DarkwoodAdapterRuntime.IsHostStoryReady()) return;
        try
        {
            if (destTransform == null) return;                       // 旁白/UI 类气泡暂不跨端（target 语义未定）
            if (destTransform.GetComponent<global::Player>() != null) return; // 玩家自身提示：各端本地
            var t = destTransform.transform != null ? destTransform.transform : destTransform;
            DarkwoodAdapterRuntime.Instance?.BroadcastPresentation(PresentationKinds.CharacterMessage, $"{t.name}|{t.position.x}|{t.position.z}", text);
        }
        catch (Exception) { }
    }
}
