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
            // v0.9.0 Trusted Client：允许原版 spawnDroppedInvItem 本地执行（生成掉落物）——
            // Postfix 捕获本地对象 → 上报 Host（扣减权威 + 分配 EntityId + 广播 Spawn）→ 本地对象复用为 mirror。
            return true;
        }

        return true;
    }

    // v0.9.0：捕获本地原版生成的掉落物并上报（Host 扣减 + EntityId + 广播；本地对象将复用为 mirror）。
    private static void Postfix(InvItemClass _item)
    {
        var runtime = DarkwoodAdapterRuntime.Instance;
        if (runtime == null || !runtime.IsClient || runtime.State != ConnectionState.Ready || InvItemClass.isNull(_item)) return;
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
                    if (runtime.replication.TryGetId(itemObj, out _)) continue; // 已注册（其他来源）
                    var inv = DarkwoodDroppedItemAccessor.GetInventory(itemObj);
                    if (inv == null || inv.slots == null || inv.slots.Count == 0 || InvItemClass.isNull(inv.slots[0].invItem)) continue;
                    if (inv.slots[0].invItem.type != _item.type) continue;
                    if (Vector3.Distance(itemObj.transform.position, origin) > 4f) continue;
                    captured = inv; break;
                }
            }
            catch (Exception) { }
        }
        runtime.SetPendingLocalDrop(captured, _item.type, _item.amount, player != null ? player._transform.position : Vector3.zero);
        var payload = BuildPayload(_item);
        if (payload.Origin == DropOriginWire.PlayerSlot && payload.SlotIndex < 0)
        {
            DarkwoodAdapterRuntime.LogMessage("[HELD] drop-resolve unresolved（本地已生成）：对象将作为未注册 ghost 清理。");
            return;
        }
        if (!runtime.TryRequestDrop(payload))
            DarkwoodAdapterRuntime.LogMessage("[HELD] drop request could not be sent; local object pending cleanup");
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
                // 优先级 2：玩家背包/快捷栏（ownership=InventoryOwned——Drop 只在此判定槽内来源）
                fromHotbar = invType == Inventory.InvType.hotbar;
                slotIndex = slot.inventory.slots.IndexOf(slot);
                DarkwoodAdapterRuntime.LogMessage($"[HELD] drop-resolve: cursorMatch=否 ownership=InventoryOwned slotInventoryType={invType} slotIndex={slotIndex} finalOrigin=PlayerSlot");
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
