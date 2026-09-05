using System;
using System.Reflection;
using DarkwoodMultiplayerFramework.Core;
using DarkwoodMultiplayerFramework.DarkwoodAdapter.World;
using HarmonyLib;
using UnityEngine;

namespace DarkwoodMultiplayerFramework.DarkwoodAdapter;

/// <summary>
/// 捕兽夹触发即时广播（v0.9.6）。
/// 视觉真相在 vanilla Trigger.triggered：夹子被踩/合拢时原版调用 Trigger.switchToTriggered()（切模型/动画）。
/// 该调用只在模拟侧发生（夹子=世界对象 → Host 侧怪物/远端玩家踩踏都在 Host 世界裁决），因此 Host 侧
/// Postfix 一旦命中已绑定夹子就 BroadcastStateNow —— CaptureNow 不受 1Hz 节流，客户端立刻 Apply 并复演
/// switchToTriggered() 刷新镜像视觉。若等周期 typed 捕获（StateThrottledSchemas 1Hz），短时夹住态可能漏发。
/// 幂等/防环：仅 Host+Ready 生效；客户端 Apply 复演不触发本钩子（非 Host 直接 return）。
/// </summary>
[HarmonyPatch]
internal static class DarkwoodTrapTriggerPatch
{
    // Verified signature: public void Trigger.switchToTriggered()（Item.switchTriggerState 内原版调用）。
    private static MethodBase TargetMethod() => AccessTools.Method(typeof(Trigger), "switchToTriggered", Type.EmptyTypes);

    private static void Postfix(Trigger __instance)
    {
        DarkwoodAdapterRuntime runtime;
        try { runtime = DarkwoodAdapterRuntime.Instance; }
        catch (Exception) { return; }
        if (runtime == null || !runtime.IsHost || runtime.State != ConnectionState.Ready || runtime.IsQuitting)
            return;
        if (__instance == null || __instance.gameObject == null) return;
        // 夹子 Item 与 Trigger 同 GO 或 Trigger 挂在子物体（原版 Item.switchTriggerState 用同 GO GetComponent）。
        var item = __instance.GetComponent<Item>() ?? __instance.GetComponentInParent<Item>(true);
        if (item == null || !BearTrapStateAdapter.IsBearTrap(item)) return;
        if (!runtime.replication.TryGetId(item, out var id)) return;
        try
        {
            DarkwoodAdapterRuntime.LogMessage($"[TRAP-TRIGGER] Host 夹子触发即时广播：item={item.name} id={id.Value:X8} persistent={id.IsPersistent}");
            runtime.BroadcastStateNow(id);
        }
        catch (Exception error)
        {
            DarkwoodAdapterRuntime.LogMessage($"[TRAP-TRIGGER] 即时广播失败（不影响原版）：{error.Message}");
        }
    }
}
