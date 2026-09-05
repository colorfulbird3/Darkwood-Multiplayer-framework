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

    // v0.9.5：客户端原版 Drop 后经 __result 直取刚生成的掉落物（反编译确认 spawnDroppedInvItem 返回其 Transform；
    // 之前用 FindObjectsOfType 扫描捕获，扔掷物会因距离/标志错过 → DropCommit 0 条、对方看不见）。
    private static void Postfix(InvItemClass _item, Transform __result)
    {
        var runtime = DarkwoodAdapterRuntime.Instance;
        if (runtime == null || runtime.State != ConnectionState.Ready || InvItemClass.isNull(_item)) return;
        if (runtime.IsHost)
        {
            // Host 本地丢弃：原版对象由 Host 运行时扫描注册并广播（RUNTIME-CHECK 生命周期）。绝不二次创建。
            return;
        }
        var player = Player.Instance;
        if (player == null || __result == null) return;
        var inv = __result.GetComponent<Inventory>();
        if (inv == null || inv.slots == null || inv.slots.Count == 0 || InvItemClass.isNull(inv.slots[0].invItem))
        {
            DarkwoodAdapterRuntime.LogMessage("[DROP] __result 掉落物 Inventory 无效，跳过 DropCommit。");
            return;
        }
        if (inv.slots[0].invItem.type != _item.type)
        {
            DarkwoodAdapterRuntime.LogMessage($"[DROP] __result 类型不符（{inv.slots[0].invItem.type} vs {_item.type}），跳过 DropCommit。");
            return;
        }
        runtime.SubmitDropCommit(inv, _item, player);
    }
    }

    /// <summary>扫描刚由 spawnDroppedInvItem 生成的本地掉落物（未入网、同类型、距玩家 ≤4m）。</summary>
    internal static bool TryCaptureSpawnedDropped(InvItemClass _item, Player player, out Inventory captured)
    {
        captured = null;
        if (InvItemClass.isNull(_item) || player == null) return false;
        var runtime = DarkwoodAdapterRuntime.Instance;
        if (runtime == null) return false;
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
                if (Vector3.Distance(itemObj.transform.position, origin) > 12f) continue; // 扔掷会飞出较远；4m 会漏
                captured = inv; return true;
            }
        }
        catch (Exception) { }
        return false;
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

