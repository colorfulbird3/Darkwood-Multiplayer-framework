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

/// <summary>Player-initiated door toggles become authoritative requests.</summary>
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
        // Only intercept the local player's interaction; enemy/npc door pushes keep the vanilla path.
        if (openerTransform == null || openerTransform.GetComponent<Player>() != Player.Instance)
            return true;

        runtime.TryRequestDoorToggle(__instance);
        return false;
    }
}

/// <summary>Player-initiated window barricades become authoritative requests.</summary>
[HarmonyPatch]
internal static class DarkwoodWindowBarricadePatch
{
    // Verified signature: public void Window.barricade(int destHealth = 0, bool byPlayer = false).
    private static MethodBase TargetMethod() => AccessTools.Method(typeof(Window), "barricade", new[] { typeof(int), typeof(bool) });

    private static bool Prefix(Window __instance, int destHealth, bool byPlayer)
    {
        var runtime = DarkwoodAdapterRuntime.Instance;
        if (runtime == null || !runtime.IsClient || runtime.State != ConnectionState.Ready)
            return true;
        // The vanilla flow calls barricade with byPlayer=false from ConstructionMenu;
        // the selectedObject check isolates the player-initiated call.
        if (!byPlayer && (Player.Instance == null || Player.Instance.selectedObject != __instance.transform))
            return true;

        runtime.TryRequestWindowBarricade(__instance, destHealth);
        return false;
    }
}

/// <summary>Player-initiated item toggles (lamps, machines) become authoritative requests.</summary>
[HarmonyPatch]
internal static class DarkwoodItemActivatePatch
{
    // Verified signature: public bool Item.activate().
    private static MethodBase TargetMethod() => AccessTools.Method(typeof(Item), "activate", Type.EmptyTypes);

    private static bool Prefix(Item __instance, ref bool __result)
    {
        var runtime = DarkwoodAdapterRuntime.Instance;
        if (runtime == null || !runtime.IsClient || runtime.State != ConnectionState.Ready)
            return true;
        if (Player.Instance == null || Player.Instance.selectedObject != __instance.transform)
            return true;

        if (runtime.TryRequestItemActivate(__instance))
        {
            // Report success to the vanilla caller so its prompt flow completes; the
            // authoritative state arrives with the ActionResult/delta.
            __result = true;
            return false;
        }
        return true;
    }
}
