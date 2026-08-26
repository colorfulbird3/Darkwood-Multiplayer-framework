using System;
using HarmonyLib;
using DarkwoodMultiplayerFramework.Core;
using DarkwoodMultiplayerFramework.Protocol;
using UnityEngine;

namespace DarkwoodMultiplayerFramework.DarkwoodAdapter;

/// <summary>
/// v0.9.2 Trusted Client Drop：客户端允许原版 spawnDroppedInvItem 完整本地执行（生成 DroppedItem + 扣减背包）。
/// Host 端只接收 DropCommit（不再受理旧 DropItem Action）——不查 Cursor、不判 SLOT_EMPTY / NOT_HOLDING。
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
        // v0.9.2：Host 本地玩家走 Host 自有 drop（不需网络）；Client 一律本地原版执行，Postfix 上报 DropCommit。
        return true;
    }

    // v0.9.2：客户端本地原版 Drop 完成后 → 生成 localDropToken + 捕获本地 DroppedItem + 上报 DropCommit（revision 单调）。
    private static void Postfix(InvItemClass _item)
    {
        var runtime = DarkwoodAdapterRuntime.Instance;
        if (runtime == null || runtime.State != ConnectionState.Ready || InvItemClass.isNull(_item)) return;
        var player = Player.Instance;
        Inventory? captured = null;
        if (player != null)
        {
            var origin = player._transform.position;
            try
            {
                foreach (var itemObj in UnityEngine.Object.FindObjectsOfType<Item>(false))
                {
                    if (itemObj == null || !itemObj.isDroppedItem) continue;
                    if (runtime.replication.TryGetId(itemObj, out _)) continue;
                    var inv = DarkwoodDroppedItemAccessor.GetInventory(itemObj);
                    if (inv == null || inv.slots == null || inv.slots.Count == 0 || InvItemClass.isNull(inv.slots[0].invItem)) continue;
                    if (inv.slots[0].invItem.type != _item.type) continue;
                    if (Vector3.Distance(itemObj.transform.position, origin) > 4f) continue;
                    captured = inv; break;
                }
            }
            catch (Exception) { }
        }
        if (captured == null)
        {
            DarkwoodAdapterRuntime.LogMessage("[DROP] 本地原版 spawnDroppedInvItem 后未捕获到对象（诡异）；跳过 DropCommit。");
            return;
        }
        if (runtime.IsHost)
        {
            var payload = BuildPayload(_item);
            if (payload.Origin != DropOriginWire.PlayerSlot || payload.SlotIndex >= 0)
                runtime.World.DropItem(0, payload, default, (_, _, _, _) => { });
            return;
        }
        // Client 玩家：发 DropCommit
        runtime.SubmitDropCommit(captured, _item, player);
    }

    internal static DropItemPayload BuildPayload(InvItemClass item)
    {
        var player = Player.Instance;
        var runtime = DarkwoodAdapterRuntime.Instance;
        var controller = Singleton<Controller>.Instance;
        var cursorMatch = controller != null && !InvItemClass.isNull(controller.pickedUpItem) && ReferenceEquals(controller.pickedUpItem, item);
        if (cursorMatch)
        {
            var pos0 = player != null ? player._transform.position : Vector3.zero;
            var rot0 = player != null ? player._transform.rotation : Quaternion.identity;
            DarkwoodAdapterRuntime.LogMessage($"[HELD] drop-resolve: cursorMatch=是 slotPresent={(item.slot != null ? "是" : "否")} slotInventoryType={(item.slot?.inventory != null ? item.slot.inventory.invType.ToString() : "无")} ownership=CursorOwned finalOrigin=HeldItem");
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
                fromHotbar = invType == Inventory.InvType.hotbar;
                slotIndex = slot.inventory.slots.IndexOf(slot);
                origin = DropOriginWire.PlayerSlot;
            }
            else
            {
                containerValue = 0;
                slotIndex = -1;
                origin = DropOriginWire.PlayerSlot;
            }
        }
        var pos = player != null ? player._transform.position : Vector3.zero;
        var rot = player != null ? player._transform.rotation : Quaternion.identity;
        return new DropItemPayload(fromHotbar, slotIndex, Math.Max(1, item.amount), pos.x, pos.y, pos.z, rot.x, rot.y, rot.z, rot.w, origin, containerValue, containerPersistent);
    }
}

