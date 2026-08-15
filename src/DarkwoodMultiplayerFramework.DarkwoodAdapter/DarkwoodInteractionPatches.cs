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

/// <summary>Player-initiated door toggles run locally (trust model) and notify the host.</summary>
[HarmonyPatch]
internal static class DarkwoodDoorTogglePatch
{
    // Verified signature: public void Door.openClose(Transform openerTransform).
    private static MethodBase TargetMethod() => AccessTools.Method(typeof(Door), "openClose", new[] { typeof(Transform) });

    // FIX-011：联机信任模型（用户要求，类 MC）——客户端操作本地直接执行，
    // 不再等待主机批准；请求仅作为主机侧状态同步与广播的凭据。
    private static void Postfix(Door __instance, Transform openerTransform)
    {
        var runtime = DarkwoodAdapterRuntime.Instance;
        if (runtime == null || !runtime.IsClient || runtime.State != ConnectionState.Ready)
            return;
        if (openerTransform == null || openerTransform.GetComponent<Player>() != Player.Instance)
            return;
        runtime.TryRequestDoorToggle(__instance);
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
    private static void Prefix(Item __instance, ref bool __state)
    {
        var runtime = DarkwoodAdapterRuntime.Instance;
        __state = runtime != null && runtime.IsClient && runtime.State == ConnectionState.Ready
                  && Player.Instance != null && Player.Instance.selectedObject == __instance.transform;
    }

    private static void Postfix(Item __instance, bool __state)
    {
        if (!__state) return;
        DarkwoodAdapterRuntime.Instance?.TryRequestItemActivate(__instance);
    }
}
