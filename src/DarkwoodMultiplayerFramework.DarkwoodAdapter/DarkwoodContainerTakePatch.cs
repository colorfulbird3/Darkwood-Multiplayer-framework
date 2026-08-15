using System;
using System.Collections.Generic;
using System.Reflection;
using DarkwoodMultiplayerFramework.Core;
using HarmonyLib;

namespace DarkwoodMultiplayerFramework.DarkwoodAdapter;

/// <summary>
/// FIX-011 信任模式：容器拿取/放入由客户端本地直接执行（不再经主机审批），
/// 执行后把容器新状态上报主机，主机应用到自己的世界并广播给其他端。
/// 主机玩家自己的操作保持原样：本地执行后广播。
/// </summary>
[HarmonyPatch]
internal static class DarkwoodContainerTakePatch
{
    internal const string Mode = "TrustModeSharedContainers";

    private static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(InvSlot), nameof(InvSlot.transferItemToPlayer), Type.EmptyTypes);
        yield return AccessTools.Method(typeof(InvSlot), nameof(InvSlot.transferItemAllToPlayer), Type.EmptyTypes);
    }

    private static void Postfix(InvSlot __instance)
    {
        ReportIfShared(__instance?.inventory);
    }

    internal static void ReportIfShared(Inventory? inventory)
    {
        if (inventory == null) return;
        if (inventory.invType != Inventory.InvType.itemInv && inventory.invType != Inventory.InvType.deathDrop) return;
        var runtime = DarkwoodAdapterRuntime.Instance;
        if (runtime == null || runtime.State != ConnectionState.Ready) return;
        if (runtime.IsHost) runtime.NotifyHostContainerChanged(inventory);
        else runtime.ReportSharedContainerChanged(inventory);
    }
}

/// <summary>Player-to-container transfers run locally and report the container state.</summary>
[HarmonyPatch]
internal static class DarkwoodContainerPutPatch
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(InvSlot), nameof(InvSlot.transferItemTo), new[] { typeof(Inventory) });
        yield return AccessTools.Method(typeof(InvSlot), nameof(InvSlot.transferItemAllTo), new[] { typeof(Inventory) });
    }

    private static void Postfix(Inventory _destInv)
    {
        DarkwoodContainerTakePatch.ReportIfShared(_destInv);
    }
}

/// <summary>Grab/controller-pickup from a shared container runs locally and reports the container state.</summary>
[HarmonyPatch]
internal static class DarkwoodContainerGrabPatch
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(InvSlot), nameof(InvSlot.grabItem), Type.EmptyTypes);
        yield return AccessTools.Method(typeof(InvSlot), nameof(InvSlot.controllerPickUpItem), Type.EmptyTypes);
    }

    private static void Postfix(InvSlot __instance)
    {
        DarkwoodContainerTakePatch.ReportIfShared(__instance?.inventory);
    }
}

/// <summary>
/// FIX-011 信任模式：拖拽/放入路径也本地直接执行并上报。
/// 原来的"按住本地等主机确认"逻辑全部移除。
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

    private static void Prefix(InvSlot __instance, out Inventory? __state)
    {
        // 记录拖拽来源（可能是共享容器），执行后来源容器状态可能已变化，需一并上报。
        var picked = Singleton<Controller>.Instance?.pickedUpItem;
        __state = picked?.slot?.inventory;
    }

    private static void Postfix(InvSlot __instance, Inventory? __state)
    {
        if (__instance?.inventory != null &&
            (__instance.inventory.invType == Inventory.InvType.itemInv || __instance.inventory.invType == Inventory.InvType.deathDrop))
            DarkwoodContainerTakePatch.ReportIfShared(__instance.inventory);
        if (__state != null &&
            (__state.invType == Inventory.InvType.itemInv || __state.invType == Inventory.InvType.deathDrop) &&
            !ReferenceEquals(__state, __instance?.inventory))
            DarkwoodContainerTakePatch.ReportIfShared(__state);
    }
}
