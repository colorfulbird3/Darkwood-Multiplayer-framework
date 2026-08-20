using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace DarkwoodMultiplayerFramework.DarkwoodAdapter;

/// <summary>
/// P0-I（决策：CLIENT INTENT → HOST AUTHORITY → CLIENT REPLAY ORIGINAL DARKWOOD LOGIC → RECONCILE）。
/// 客户端在 AuthorityReplayScope 内复演原版 grabItem/placeItem 时会再次调用 Core.sendTriggerInfo(onTake/onPlace/onDrop)，
/// 这会与 Host 已触发的世界事件重复 → 事件双发/世界不同步。
/// 在此抑制这三类 Replay 副作用（Host 权威事件保持唯一）。
/// </summary>
[HarmonyPatch]
internal static class DarkwoodReplayTriggerGuard
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(global::Core), nameof(global::Core.sendTriggerInfo), new[] { typeof(GameObject), typeof(EventTrigger.Type), typeof(string), typeof(bool) });
        yield return AccessTools.Method(typeof(global::Core), nameof(global::Core.sendTriggerInfo), new[] { typeof(GameObject), typeof(EventTrigger.Type), typeof(bool) });
    }

    // Prefix 用多个签名统一拦截：Replay 内抑制 onTake/onPlace（grab/place 的重复世界事件）。
    private static bool Prefix(object[] __args)
    {
        var runtime = DarkwoodAdapterRuntime.Instance;
        if (runtime == null || !runtime.ReplayingAuthoritativeAction) return true;
        if (__args == null || __args.Length < 2) return true;
        if (__args[1] is EventTrigger.Type type &&
            (type == EventTrigger.Type.onTakeInvItem || type == EventTrigger.Type.onPlaceItem))
        {
            DarkwoodAdapterRuntime.LogMessage($"[REPLAY] 抑制重复世界事件：sendTriggerInfo({type})（Host 权威已触发）。");
            return false;
        }
        return true;
    }
}
