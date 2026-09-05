using System;
using System.Collections.Generic;
using System.Reflection;
using DarkwoodMultiplayerFramework.Core;
using HarmonyLib;
using UnityEngine;

namespace DarkwoodMultiplayerFramework.DarkwoodAdapter;

/// <summary>
/// Client-side interceptors that convert melee attacks and world interactions into
/// host-authoritative ActionRequests. The original game mutation is skipped locally:
/// the authoritative result is applied when the host confirms.
/// </summary>
[HarmonyPatch]
internal static class DarkwoodMeleeAttackPatch
{
    // Verified signature (Assembly-CSharp): private void Player.attack(float staminaStrengthModifier = 1f).
    // This is where the game spawns the MeleeSensor that applies local damage.
    private static MethodBase TargetMethod() => AccessTools.Method(typeof(Player), "attack", new[] { typeof(float) });

    private static bool Prefix(Player __instance)
    {
        var runtime = DarkwoodAdapterRuntime.Instance;
        if (runtime == null || !runtime.IsClient || runtime.State != ConnectionState.Ready)
            return true;
        if (__instance != Player.Instance)
            return true;
        // Melee only: firearms and throwables stay on the vanilla local path for now
        // (documented limitation: unsynchronized ranged combat).
        if (InvItemClass.isNull(__instance.currentItem) || __instance.currentItem.baseClass == null || !__instance.currentItem.baseClass.isMelee)
            return true;

        // When the request is sent, suppress the local MeleeSensor; otherwise fall back
        // to the vanilla local attack so the input is not silently swallowed.
        return !runtime.TryRequestMeleeAttack(__instance, __instance.specialAttacking);
    }
}

/// <summary>
/// Door 交互（v0.9.5 起 A 方案：Host 权威门）。
/// 原：客户端本地先执行（信任模型）+ Postfix 通知 → 双端各自判定 blocked/播动画，被门内玩家挡时会分叉。
/// 现：客户端不本地执行，只发 DoorInteract 意图；Host 唯一执行原版 openClose（真实碰撞统一判定 blocked）
/// 并即时广播权威状态。Host 判定门被挡时原版自然不关闭 → 两端一致（等同于「门内有人就不操作」规则）。
/// 未入网的门回退本地执行，避免卡交互。
/// </summary>
[HarmonyPatch]
internal static class DarkwoodDoorTogglePatch
{
    // Verified signature: public void Door.openClose(Transform openerTransform).
    private static MethodBase TargetMethod() => AccessTools.Method(typeof(Door), "openClose", new[] { typeof(Transform) });

    private static bool Prefix(Door __instance, Transform openerTransform)
    {
        var runtime = DarkwoodAdapterRuntime.Instance;
        if (runtime == null || !runtime.IsClient || runtime.State != ConnectionState.Ready)
            return true;
        if (runtime.replication.ApplyingRemote) return true; // Action Replay 执行原版 openClose：放行且不再发意图（防 ping-pong）
        if (openerTransform == null || openerTransform.GetComponent<Player>() != Player.Instance)
            return true; // 非本机玩家触发的 openClose（如 AI/动画）保持原版
        // A 方案：本地不执行；发意图由 Host 唯一裁决并广播。
        if (runtime.TryRequestDoorToggle(__instance))
            return false;
        // 未入网（异常门）：回退本地执行避免卡交互。
        DarkwoodAdapterRuntime.LogMessage("[DOOR] 门未注册 EntityId，回退本地执行。");
        return true;
    }
}

/// <summary>Player-initiated window barricades run locally (trust model) and notify the host.</summary>
[HarmonyPatch]
internal static class DarkwoodWindowBarricadePatch
{
    // Verified signature: public void Window.barricade(int destHealth = 0, bool byPlayer = false).
    private static MethodBase TargetMethod() => AccessTools.Method(typeof(Window), "barricade", new[] { typeof(int), typeof(bool) });

    private static void Postfix(Window __instance, int destHealth, bool byPlayer)
    {
        var runtime = DarkwoodAdapterRuntime.Instance;
        if (runtime == null || !runtime.IsClient || runtime.State != ConnectionState.Ready)
            return;
        // The vanilla flow calls barricade with byPlayer=false from ConstructionMenu;
        // the selectedObject check isolates the player-initiated call.
        if (!byPlayer && (Player.Instance == null || Player.Instance.selectedObject != __instance.transform))
            return;
        runtime.TryRequestWindowBarricade(__instance, destHealth);
    }
}

/// <summary>Player-initiated item toggles (lamps, machines, containers) run locally and notify the host.</summary>
[HarmonyPatch]
internal static class DarkwoodItemActivatePatch
{
    // Verified signature: public bool Item.activate().
    private static MethodBase TargetMethod() => AccessTools.Method(typeof(Item), "activate", Type.EmptyTypes);

    // FIX-011：记录是否为本地玩家的交互；activate() 原方法正常执行（本地立即生效），
    // Postfix 把执行后的 isOn 状态报告给主机（主机应用状态并广播，不弹容器 UI）。
    private static bool Prefix(Item __instance, ref bool __state)
    {
        var runtime = DarkwoodAdapterRuntime.Instance;
        __state = runtime != null && runtime.IsClient && runtime.State == ConnectionState.Ready
                  && Player.Instance != null && Player.Instance.selectedObject == __instance.transform;
        if (!__state) return true;
        // 阶段二：发电机——客户端绝不先执行原版 activate（会本地 isOn=true + 本地电源/drain 模拟）。
        // 改为 StateObjectInteract intent → Host 执行原版 turnOn/turnOff → 即时广播权威状态 → 客户端 adapter Apply。
        // 用 GetComponentInChildren 兜底：Generator 组件若挂在 Item 所在 GO 的子物体上，自身上取不到会漏拦截。
        var generator = __instance != null ? (__instance.GetComponent<Generator>() ?? __instance.GetComponentInChildren<Generator>(true)) : null;
        if (generator != null)
        {
            var hasId = runtime.replication.TryGetId(__instance, out var genId);
            DarkwoodAdapterRuntime.LogMessage($"[GEN-INTERCEPT] item={__instance.name} generator={generator.name} hasEntityId={hasId}{(hasId ? $" id={genId}" : "")}");
            if (hasId) runtime.TryRequestStateObjectInteract(genId, "toggle");
            else DarkwoodAdapterRuntime.LogMessage("[GEN-INTERCEPT] 该发电机无注册 EntityId——无法走意图链路（跳过原版，避免本地双写）。");
            __state = false; // 不执行原版、不上报
            return false;
        }
        return true;
    }

    private static void Postfix(Item __instance, bool __state)
    {
        if (!__state) return;
        var runtime = DarkwoodAdapterRuntime.Instance;
        if (runtime == null) return;
        // P1-A：掉落物不产生 ItemActivate——点击掉落物只走 Pickup intent（避免重复 interaction 路由 / Pickup·ItemActivate race）。
        if (__instance != null && __instance.isDroppedItem) return;
        runtime.TryRequestItemActivate(__instance);
    }
}
