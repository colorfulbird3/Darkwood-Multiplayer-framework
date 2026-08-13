using System;
using System.Collections.Generic;
using System.Reflection;
using DarkwoodMultiplayerFramework.Core;
using HarmonyLib;

namespace DarkwoodMultiplayerFramework.DarkwoodAdapter;

/// <summary>Routes container-to-player transfers through the authoritative host.</summary>
[HarmonyPatch]
internal static class DarkwoodContainerTakePatch
{
    internal const string Mode = "HostAuthoritativeSharedContainers";

    private static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(InvSlot), nameof(InvSlot.transferItemToPlayer), Type.EmptyTypes);
        yield return AccessTools.Method(typeof(InvSlot), nameof(InvSlot.transferItemAllToPlayer), Type.EmptyTypes);
    }

    private static bool Prefix(InvSlot __instance, MethodBase __originalMethod)
    {
        var runtime = DarkwoodAdapterRuntime.Instance;
        if (runtime == null || !runtime.IsClient || runtime.State != ConnectionState.Ready)
            return true;
        if (__instance == null || __instance.inventory == null ||
            (__instance.inventory.invType != Inventory.InvType.itemInv && __instance.inventory.invType != Inventory.InvType.deathDrop))
            return true;

        var takeAll = string.Equals(__originalMethod.Name, nameof(InvSlot.transferItemAllToPlayer), StringComparison.Ordinal);
        runtime.TryRequestContainerTake(__instance, takeAll);
        return false;
    }

    private static void Postfix(InvSlot __instance)
    {
        var runtime = DarkwoodAdapterRuntime.Instance;
        if (runtime == null || !runtime.IsHost || runtime.State != ConnectionState.Ready || __instance?.inventory == null)
            return;
        if (__instance.inventory.invType == Inventory.InvType.itemInv || __instance.inventory.invType == Inventory.InvType.deathDrop)
            runtime.NotifyHostContainerChanged(__instance.inventory);
    }
}

/// <summary>Routes player-to-container transfers through the authoritative host.</summary>
[HarmonyPatch]
internal static class DarkwoodContainerPutPatch
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(InvSlot), nameof(InvSlot.transferItemTo), new[] { typeof(Inventory) });
        yield return AccessTools.Method(typeof(InvSlot), nameof(InvSlot.transferItemAllTo), new[] { typeof(Inventory) });
    }

    private static bool Prefix(InvSlot __instance, Inventory _destInv, MethodBase __originalMethod, ref bool __result)
    {
        var runtime = DarkwoodAdapterRuntime.Instance;
        if (runtime == null || !runtime.IsClient || runtime.State != ConnectionState.Ready)
            return true;
        if (__instance?.inventory == null || _destInv == null ||
            (__instance.inventory.invType != Inventory.InvType.playerInv && __instance.inventory.invType != Inventory.InvType.hotbar) ||
            _destInv.invType != Inventory.InvType.itemInv)
            return true;

        var putAll = string.Equals(__originalMethod.Name, nameof(InvSlot.transferItemAllTo), StringComparison.Ordinal);
        __result = runtime.TryRequestContainerPut(__instance, _destInv, putAll);
        return false;
    }

    private static void Postfix(Inventory _destInv)
    {
        var runtime = DarkwoodAdapterRuntime.Instance;
        if (runtime == null || !runtime.IsHost || runtime.State != ConnectionState.Ready || _destInv == null)
            return;
        if (_destInv.invType == Inventory.InvType.itemInv)
            runtime.NotifyHostContainerChanged(_destInv);
    }
}

/// <summary>
/// Converts mouse/controller grabs from a shared container into an atomic
/// container-to-player request.  The original method must not clear the local
/// slot before the host has accepted the transaction.
/// </summary>
[HarmonyPatch]
internal static class DarkwoodContainerGrabPatch
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(InvSlot), nameof(InvSlot.grabItem), Type.EmptyTypes);
        yield return AccessTools.Method(typeof(InvSlot), nameof(InvSlot.controllerPickUpItem), Type.EmptyTypes);
    }

    private static bool Prefix(InvSlot __instance)
    {
        var runtime = DarkwoodAdapterRuntime.Instance;
        if (runtime == null || !runtime.IsClient || runtime.State != ConnectionState.Ready)
            return true;
        if (__instance == null || !IsShared(__instance.inventory))
            return true;

        runtime.TryRequestContainerTake(__instance, true);
        return false;
    }

    private static void Postfix(InvSlot __instance)
    {
        var runtime = DarkwoodAdapterRuntime.Instance;
        if (runtime == null || !runtime.IsHost || runtime.State != ConnectionState.Ready || __instance == null || !IsShared(__instance.inventory))
            return;
        runtime.NotifyHostContainerChanged(__instance.inventory);
    }

    private static bool IsShared(Inventory? inventory) => inventory != null &&
        (inventory.invType == Inventory.InvType.itemInv || inventory.invType == Inventory.InvType.deathDrop);
}

/// <summary>
/// Covers the UI drag/drop paths which bypass transferItemTo.  Client changes
/// are held locally until the authoritative result arrives from the host.
/// </summary>
[HarmonyPatch]
internal static class DarkwoodContainerDragDestinationPatch
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(InvSlot), nameof(InvSlot.placeItem), new[] { typeof(bool) });
        yield return AccessTools.Method(typeof(InvSlot), nameof(InvSlot.controllerPlaceItem), new[] { typeof(bool) });
        yield return AccessTools.Method(typeof(InvSlot), nameof(InvSlot.addToItem), new[] { typeof(int) });
        yield return AccessTools.Method(typeof(InvSlot), nameof(InvSlot.addToItem), new[] { typeof(InvItemClass) });
        yield return AccessTools.Method(typeof(InvSlot), nameof(InvSlot.swapItems), Type.EmptyTypes);
    }

    private static bool Prefix(InvSlot __instance, out Inventory? __state)
    {
        var controller = Singleton<Controller>.Instance;
        var picked = controller?.pickedUpItem;
        var sourceSlot = picked?.slot;
        __state = sourceSlot?.inventory;

        var runtime = DarkwoodAdapterRuntime.Instance;
        if (runtime == null || runtime.State != ConnectionState.Ready || !runtime.IsClient || runtime.ApplyingAuthoritativeInventory)
            return true;

        var destination = __instance?.inventory;
        if (IsWritableShared(destination))
        {
            // Every client-side shared-container mutation must be traceable to a
            // player inventory slot. Unknown mutation paths are blocked rather
            // than being allowed to fork the container locally.
            if (sourceSlot == null || destination == null || !IsPlayerInventory(sourceSlot.inventory))
                return false;

            var destinationSlotIndex = destination.slots.IndexOf(__instance);
            runtime.TryRequestContainerPut(sourceSlot, destination, destinationSlotIndex, true);
            RestoreSourceAndClearCursor(picked, sourceSlot);
            return false;
        }

        // A stale picked-up item originating in a shared container must never
        // be placed locally. Convert it to the same authoritative take request.
        if (sourceSlot != null && IsShared(sourceSlot.inventory))
        {
            runtime.TryRequestContainerTake(sourceSlot, true);
            RestoreSourceAndClearCursor(picked, sourceSlot);
            return false;
        }

        return true;
    }

    private static void Postfix(InvSlot __instance, Inventory? __state)
    {
        var runtime = DarkwoodAdapterRuntime.Instance;
        if (runtime == null || !runtime.IsHost || runtime.State != ConnectionState.Ready)
            return;
        if (__state != null && IsShared(__state))
            runtime.NotifyHostContainerChanged(__state);
        if (__instance?.inventory != null && IsShared(__instance.inventory) && !ReferenceEquals(__state, __instance.inventory))
            runtime.NotifyHostContainerChanged(__instance.inventory);
    }

    private static void RestoreSourceAndClearCursor(InvItemClass? picked, InvSlot? sourceSlot)
    {
        if (picked == null || sourceSlot == null)
            return;
        if (InvItemClass.isNull(sourceSlot.invItem))
            sourceSlot.createItem(picked.type, Math.Max(1, picked.amount), picked.durability, picked.modifierQuality, picked.isRecipe);
        else
            sourceSlot.invItem.refresh();
        sourceSlot.switchTexture();
        try { picked.UIInvItem?.despawn(); } catch { }
        if (Singleton<Controller>.Instance != null)
            Singleton<Controller>.Instance.pickedUpItem = null;
    }

    private static bool IsPlayerInventory(Inventory? inventory) => inventory != null &&
        (inventory.invType == Inventory.InvType.playerInv || inventory.invType == Inventory.InvType.hotbar);
    private static bool IsWritableShared(Inventory? inventory) => inventory != null && inventory.invType == Inventory.InvType.itemInv;
    private static bool IsShared(Inventory? inventory) => inventory != null &&
        (inventory.invType == Inventory.InvType.itemInv || inventory.invType == Inventory.InvType.deathDrop);
}
