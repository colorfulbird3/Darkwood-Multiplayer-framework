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
        // P0-A 优先级 4：来源无法解析时直接阻断原版（绝不静默放行 singleplayer spawn）。
        if (payload.Origin == DropOriginWire.PlayerSlot && payload.SlotIndex < 0)
        {
            DarkwoodAdapterRuntime.LogMessage("[HELD] drop-resolve unresolved：阻断原版 Drop（cursorMatch=否 slot 缺失）");
            return false;
        }

        // P0-5：绝不提前清 cursor / pickedUpItem——Drop 是乐观保底语义，失败时物品必须留在手上。
        var player = Player.Instance;
        if (player != null) { try { player.refreshRecipes(); } catch (Exception) { } }

        if (runtime.IsHost)
        {
            runtime.World.DropItem(0, payload, default, (_, _, _, _) => { });
            return false;
        }

        if (runtime.IsClient)
        {
            // P0-B：READY 联机下无论请求是否发出，原版 spawnDroppedInvItem（客户端本地世界 mutation）都绝不允许运行。
            var sent = runtime.TryRequestDrop(payload);
            if (!sent) DarkwoodAdapterRuntime.LogMessage("[HELD] drop request could not be sent; cursor retained");
            return false;
        }

        return true;
    }

    internal static DropItemPayload BuildPayload(InvItemClass item)
    {
        var player = Player.Instance;
        var runtime = DarkwoodAdapterRuntime.Instance;
        // P0-A：判定优先级 1 —— Controller.pickedUpItem == item → HeldItem。
        // 绝不要求 slot==null：AttachHeldItemFromSnapshot 用 copy constructor 保留旧 slot（指向已清空的容器槽），
        // 若先判 slot 会被误判成 SharedContainer → SLOT_EMPTY → Drop 失败。
        var controller = Singleton<Controller>.Instance;
        var cursorMatch = controller != null && !InvItemClass.isNull(controller.pickedUpItem) && ReferenceEquals(controller.pickedUpItem, item);
        if (cursorMatch)
        {
            var pos0 = player != null ? player._transform.position : Vector3.zero;
            var rot0 = player != null ? player._transform.rotation : Quaternion.identity;
            DarkwoodAdapterRuntime.LogMessage($"[HELD] drop-resolve: cursorMatch=是 slotPresent={(item.slot != null ? "是" : "否")} slotInventoryType={(item.slot?.inventory != null ? item.slot.inventory.invType.ToString() : "无")} finalOrigin=HeldItem");
            return new DropItemPayload(false, -1, Math.Max(1, item.amount), pos0.x, pos0.y, pos0.z, rot0.x, rot0.y, rot0.z, rot0.w, DropOriginWire.HeldItem);
        }

        var slot = item.slot;
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
                // 优先级 2：玩家背包/快捷栏
                fromHotbar = invType == Inventory.InvType.hotbar;
                slotIndex = slot.inventory.slots.IndexOf(slot);
            }
            else
            {
                // 优先级 3：共享容器
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
