using System;
using HarmonyLib;
using DarkwoodMultiplayerFramework.Core;
using DarkwoodMultiplayerFramework.Protocol;
using UnityEngine;

namespace DarkwoodMultiplayerFramework.DarkwoodAdapter;

/// <summary>
/// 0.8.9 第 2 刀：Drop 全 Host Authority。
/// 拦截原版所有扔物品的汇聚点 Player.spawnDroppedInvItem(InvItemClass)：
/// - 客户端：拦截原版 mutation → DropRequest → Host 执行
/// - 主机：拦截原版 mutation → WorldAuthority.DropItem(0, ...) 本地执行
/// </summary>
[HarmonyPatch(typeof(Player), "spawnDroppedInvItem")]
internal static class DarkwoodDropPatch
{
    private static bool Prefix(InvItemClass _item)
    {
        var runtime = DarkwoodAdapterRuntime.Instance;
        if (runtime == null || !runtime.IsMultiplayerActive || runtime.State != ConnectionState.Ready)
            return true;
        if (InvItemClass.isNull(_item))
            return true;

        var payload = BuildPayload(_item);
        if (payload.SlotIndex < 0 && payload.Origin == DropOriginWire.PlayerSlot)
            return true;

        // 原版 spawnDroppedInvItem 被拦截后不会执行 pickedUpItem 复位（80885-80892），
        // 否则拖拽/丢弃光标会卡在"丢弃"状态。这里手动执行等价清理。
        try
        {
            var controller = Singleton<Controller>.Instance;
            if (controller != null && controller.pickedUpItem == _item)
            {
                var ui = controller.pickedUpItem.UIInvItem;
                if (ui != null && ui.transform != null) ui.despawn();
                controller.pickedUpItem = null;
            }
        }
        catch (Exception) { /* UI 清理失败不阻断丢弃 */ }
        var player = Player.Instance;
        if (player != null) { try { player.refreshRecipes(); } catch (Exception) { } }

        if (runtime.IsHost)
        {
            runtime.World.DropItem(0, payload, default, (_, _, _, _) => { });
            return false;
        }

        if (runtime.IsClient)
        {
            runtime.TryRequestDrop(_item);
            return false;
        }

        return true;
    }

    internal static DropItemPayload BuildPayload(InvItemClass item)
    {
        var player = Player.Instance;
        var slot = item.slot;
        var runtime = DarkwoodAdapterRuntime.Instance;
        var fromHotbar = false;
        var slotIndex = -1;
        var origin = DropOriginWire.PlayerSlot;
        ulong containerValue = 0;
        var containerPersistent = false;
        if (slot != null && slot.inventory != null)
        {
            var invType = slot.inventory.invType;
            if (invType == Inventory.InvType.hotbar || invType == Inventory.InvType.playerInv)
            {
                fromHotbar = invType == Inventory.InvType.hotbar;
                slotIndex = slot.inventory.slots.IndexOf(slot);
            }
            else
            {
                // 手上物品的来源是容器（共享容器/尸体/商人等）：槽位属于来源容器，
                // 不能按玩家背包槽位解读——带上容器 ID 让 Host 从权威容器扣减。
                origin = DropOriginWire.SharedContainer;
                slotIndex = slot.inventory.slots.IndexOf(slot);
                if (runtime != null && runtime.TryGetEntityId(slot.inventory, out var containerId))
                {
                    containerValue = containerId.Value;
                    containerPersistent = containerId.IsPersistent;
                }
            }
        }
        var amount = item.amount;
        var pos = player != null ? player._transform.position : Vector3.zero;
        var rot = player != null ? player._transform.rotation : Quaternion.identity;
        return new DropItemPayload(fromHotbar, slotIndex, amount, pos.x, pos.y, pos.z, rot.x, rot.y, rot.z, rot.w, origin, containerValue, containerPersistent);
    }
}
