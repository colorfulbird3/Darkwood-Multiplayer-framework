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

    /// <summary>待确认的拿取记录（乐观锁冲突时用于背包补偿）。</summary>
    internal readonly struct PendingTake
    {
        public PendingTake(EntityId container,string type,int amount){Container=container;Type=type;Amount=amount;}
        public EntityId Container {get;} public string Type {get;} public int Amount {get;}
    }
    private static readonly List<PendingTake> pendingTakes = new List<PendingTake>();
    private static readonly object pendingLock = new object();

    private static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(InvSlot), nameof(InvSlot.transferItemToPlayer), Type.EmptyTypes);
        yield return AccessTools.Method(typeof(InvSlot), nameof(InvSlot.transferItemAllToPlayer), Type.EmptyTypes);
        yield return AccessTools.Method(typeof(InvSlot), nameof(InvSlot.transferItemTo), new[] { typeof(Inventory) });
    }

    private static bool Prefix(InvSlot __instance, out PendingTake __state)
    {
        // 拿取前快照该槽内容（拿取后槽空即视为已拿走）。
        var item = __instance?.invItem;
        var inventory = __instance?.inventory;
        var containerId = default(EntityId);
        if (inventory != null)
        {
            var runtime = DarkwoodAdapterRuntime.Instance;
            if (runtime != null) runtime.TryGetEntityId(inventory, out containerId);
        }
        __state = item != null && !string.IsNullOrEmpty(item.type)
            ? new PendingTake(containerId, item.type, Math.Max(1, __instance.itemAmount))
            : default;

        // 第 3 刀：客户端不再本地执行共享容器操作——改发 ContainerTake/Put Intent，并阻止原版 mutation
        var runtime2 = DarkwoodAdapterRuntime.Instance;
        if (runtime2 == null || runtime2.State != ConnectionState.Ready || !runtime2.IsMultiplayerActive) return true;
        var isShared = inventory != null && DarkwoodEntityStateAdapter.IsShared(inventory);
        if (!isShared) return true;
        if (runtime2.IsClient)
        {
            var targetContainer = Traverse.Create(__instance).Field("_transferTarget").GetValue<Inventory>();
            if (targetContainer == null && __instance.inventory != null && DarkwoodEntityStateAdapter.IsShared(__instance.inventory))
            {
                // transferItemToPlayer：容器→玩家
                runtime2.TryRequestContainerTake(__instance);
            }
            else if (targetContainer != null && DarkwoodEntityStateAdapter.IsShared(targetContainer))
            {
                // transferItemTo：玩家→容器
                runtime2.TryRequestContainerPut(__instance, targetContainer, 0);
            }
            else if (__instance.inventory != null && DarkwoodEntityStateAdapter.IsShared(__instance.inventory))
            {
                runtime2.TryRequestContainerTake(__instance);
            }
            __state = default;
            return false; // 阻止原版本地 mutation——否则客户端拿一份 + 主机再拿一份（复制）
        }
        return true; // Host：原版执行，Postfix 立即广播
    }

    private static void Postfix(InvSlot __instance, PendingTake __state)
    {
        // 第 3 刀：客户端被拦截（Prefix return false 未执行）；Host 本地原版执行后立即广播权威容器状态
        var runtime = DarkwoodAdapterRuntime.Instance;
        if (runtime == null || runtime.State != ConnectionState.Ready) return;
        if (runtime.IsHost && __instance?.inventory != null && DarkwoodEntityStateAdapter.IsShared(__instance.inventory))
        {
            if (runtime.TryGetEntityId(__instance.inventory, out var id))
            {
                try { runtime.BroadcastInventory(runtime.CaptureAuthoritativeInventoryForHost(id)); }
                catch { /* 容器已销毁 */ }
            }
        }
    }

    /// <summary>取出并清空指定容器的待确认拿取记录（冲突补偿用）。</summary>
    internal static List<(string Type,int Amount)> DrainPendingTakes(EntityId container)
    {
        lock (pendingLock)
        {
            var taken = new List<(string,int)>();
            for (var i = pendingTakes.Count - 1; i >= 0; i--)
            {
                if (!pendingTakes[i].Container.Equals(container)) continue;
                taken.Add((pendingTakes[i].Type, pendingTakes[i].Amount));
                pendingTakes.RemoveAt(i);
            }
            return taken;
        }
    }

    /// <summary>主机已确认该容器的操作（正常更新到达）——清掉记录，无需补偿。</summary>
    internal static void ClearPendingTakes(EntityId container)
    {
        lock (pendingLock) pendingTakes.RemoveAll(t => t.Container.Equals(container));
    }

    private static void PostfixReport(InvSlot __instance)
    {
    }

    internal static void ReportIfShared(Inventory? inventory)
    {
        if (inventory == null) return;
        if (inventory.invType != Inventory.InvType.itemInv && inventory.invType != Inventory.InvType.deathDrop) return;
        var runtime = DarkwoodAdapterRuntime.Instance;
        if (runtime == null || runtime.State != ConnectionState.Ready) return;
        if (runtime.IsHost) runtime.NotifyHostContainerChanged(inventory);
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

    private static bool Prefix(InvSlot __instance, Inventory _destInv)
    {
        var runtime = DarkwoodAdapterRuntime.Instance;
        if (runtime == null || runtime.State != ConnectionState.Ready || !runtime.IsMultiplayerActive) return true;
        if (!DarkwoodEntityStateAdapter.IsShared(_destInv)) return true;
        if (runtime.IsClient)
        {
            runtime.TryRequestContainerPut(__instance, _destInv, 0);
            return false;
        }
        return true;
    }

    private static void Postfix(Inventory _destInv)
    {
        DarkwoodContainerTakePatch.ReportIfShared(_destInv);
    }
}

/// <summary>P0-D/E：从共享容器 grab → 主机权威 HeldItem（鼠标吸附 cursor），不再直接进背包。</summary>
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
        if (runtime == null || runtime.State != ConnectionState.Ready || !runtime.IsMultiplayerActive) return true;
        if (__instance?.inventory == null || !DarkwoodEntityStateAdapter.IsShared(__instance.inventory)) return true;
        if (runtime.IsClient)
        {
            runtime.TryRequestContainerGrab(__instance);
            return false;
        }
        return true;
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

    private static bool Prefix(InvSlot __instance, out Inventory? __state)
    {
        // 记录拖拽来源（可能是共享容器），执行后来源容器状态可能已变化，需一并上报。
        var picked = Singleton<Controller>.Instance?.pickedUpItem;
        __state = picked?.slot?.inventory;
        // P0-D/E：鼠标手持物品（HeldItem）放进玩家背包——改发 HeldToInventory（Host shadow.Add），阻止本地放置
        //（原版 placeItem 依赖 pickedUpItem.slot，而生成本地无 slot → 会 NullRef/卡住）。
        var runtime = DarkwoodAdapterRuntime.Instance;
        if (runtime != null && runtime.IsClient && runtime.State == DarkwoodMultiplayerFramework.Core.ConnectionState.Ready && !runtime.replication.ApplyingRemote)
        {
            if (!InvItemClass.isNull(picked) && __instance?.inventory != null)
            {
                var invType = __instance.inventory.invType;
                if (invType == Inventory.InvType.playerInv || invType == Inventory.InvType.hotbar)
                {
                    runtime.TryRequestHeldToInventory();
                    return false;
                }
                if (DarkwoodEntityStateAdapter.IsShared(__instance.inventory))
                {
                    DarkwoodAdapterRuntime.LogMessage("[RUNTIME] held→共享容器暂不支持：请先放回背包或丢到地面。");
                    return false;
                }
            }
        }
        return true;
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
