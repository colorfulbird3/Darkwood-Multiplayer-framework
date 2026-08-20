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

        // P0-5：绝不提前清 cursor / pickedUpItem——Drop 是乐观保底语义，失败时物品必须留在手上。
        // 清除动作只在 Host Accepted（RuntimeEntitySpawn + ActionResult）后由 ack 路径执行；Rejected 保持原样。

        var player = Player.Instance;
        if (player != null) { try { player.refreshRecipes(); } catch (Exception) { } }

        if (runtime.IsHost)
        {
            runtime.World.DropItem(0, payload, default, (_, _, _, _) => { });
            return false;
        }

        if (runtime.IsClient)
            // P0-C：来源只解析一次（Prefix 里已有 payload），绝不二次 BuildPayload（否则 UI/握持状态已变 → 解析失败 → Drop 被拒）。
            return runtime.TryRequestDrop(payload);

        return true;
    }

    internal static DropItemPayload BuildPayload(InvItemClass item)
    {
        var player = Player.Instance;
        var slot = item.slot;
        var runtime = DarkwoodAdapterRuntime.Instance;
        // P0-7：鼠标手持物品（原版"Cursor held item"）不区分端——Host/Client 都用 Controller.pickedUpItem 识别。
        // 绝不能因为它仍指向已被 grabItem 清空的源容器槽而误判为 SharedContainer（→ SLOT_EMPTY → Drop 失败）。
        if (slot == null || slot.inventory == null)
        {
            var controller = Singleton<Controller>.Instance;
            if (controller != null && !InvItemClass.isNull(controller.pickedUpItem) && ReferenceEquals(controller.pickedUpItem, item))
            {
                var pos0 = player != null ? player._transform.position : Vector3.zero;
                var rot0 = player != null ? player._transform.rotation : Quaternion.identity;
                return new DropItemPayload(false, -1, Math.Max(1, item.amount), pos0.x, pos0.y, pos0.z, rot0.x, rot0.y, rot0.z, rot0.w, DropOriginWire.HeldItem);
            }
        }
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
