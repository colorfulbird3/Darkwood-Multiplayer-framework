using System;
using System.Reflection;
using DarkwoodMultiplayerFramework.Core;
using DarkwoodMultiplayerFramework.DarkwoodAdapter.World;
using HarmonyLib;
using UnityEngine;

namespace DarkwoodMultiplayerFramework.DarkwoodAdapter;

/// <summary>
/// 捕兽夹触发即时传播（v0.9.6 + r17 双向）。
/// 视觉真相在 vanilla Trigger.triggered：夹子合拢时原版调用 Trigger.switchToTriggered()（切模型/动画）。
/// 该调用只发生在「玩家所在端」的本地模拟（可信客户端本地物理：玩家踩中 mirror/本体夹子）。
/// - Host 侧 Postfix：命中已绑定夹子 → BroadcastStateNow（CaptureNow 不受 1Hz 节流）→ 各端 Apply 复演合拢。
/// - Client 侧 Postfix：命中已绑定夹子 → 上报 Host（TrapTriggered）→ Host 把本体也合拢 → 权威广播。
/// 防回环：客户端 Apply 复演（ApplyingRemote）触发的 switchToTriggered 不上报。
/// 兜底：vanilla 触发若不走 switchToTriggered()（未知路径），由 runtime 的夹子 watch 轮询 Trigger.triggered 翻转补上。
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
        if (runtime == null || runtime.IsQuitting || runtime.State != ConnectionState.Ready) return;
        if (runtime.replication.ApplyingRemote) return; // Apply 复演：Host 已权威广播过（防 client 回环上报）
        if (__instance == null || __instance.gameObject == null) return;
        // 夹子 Item 与 Trigger 同 GO 或 Trigger 挂在子物体（原版 Item.switchTriggerState 用同 GO GetComponent）。
        var item = __instance.GetComponent<Item>() ?? __instance.GetComponentInParent<Item>(true);
        if (item == null || !BearTrapStateAdapter.IsBearTrap(item)) return;
        if (!runtime.replication.TryGetId(item, out var id)) return;
        try
        {
            if (runtime.IsHost)
            {
                DarkwoodAdapterRuntime.LogMessage($"[TRAP-TRIGGER] Host 夹子触发即时广播：item={item.name} id={id.Value:X8} persistent={id.IsPersistent}");
                runtime.BroadcastStateNow(id);
            }
            else if (runtime.IsClient)
            {
                runtime.ReportTrapTriggeredLocally(item, id);
            }
        }
        catch (Exception error)
        {
            DarkwoodAdapterRuntime.LogMessage($"[TRAP-TRIGGER] 传播失败（不影响原版）：{error.Message}");
        }
    }
}
