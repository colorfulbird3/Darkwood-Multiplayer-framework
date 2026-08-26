using HarmonyLib;
using DarkwoodMultiplayerFramework.Core;
using DarkwoodMultiplayerFramework.Protocol;
using UnityEngine;

namespace DarkwoodMultiplayerFramework.DarkwoodAdapter;

/// <summary>
/// v0.9.2 P0-5：Trusted Client 拾取——客户端允许 Darkwood 原版 <see cref="Item.getDroppedItem"/> 直接执行（地面物品 → 玩家背包，不经 Cursor），
/// Postfix 上报 PickupCommit（原子事务：RuntimeEntityId + ItemType + Amount + PlayerInventoryRevision + BackpackAfter/HotbarAfter）。
/// 不再走旧 TryRequestPickup + ActionRequest Pickup 路径（P0-11：旧 Action 已被 Host 立即拒绝为 LEGACY_ACTION_DISABLED）。
/// </summary>
[HarmonyPatch(typeof(Item), nameof(Item.getDroppedItem))]
internal static class DarkwoodPickupPatch
{
    // Prefix 期间记录的运行时实体 ID（transfer 完成后 Item 可能被 destroy）
    private static System.Collections.Generic.HashSet<ulong> pendingPickupRuntimeIds
        = new System.Collections.Generic.HashSet<ulong>();

    private static void Prefix(Item __instance)
    {
        var runtime = DarkwoodAdapterRuntime.Instance;
        if (runtime == null || !runtime.IsClient || runtime.State != ConnectionState.Ready || __instance == null) return;
        if (runtime.replication.TryGetId(__instance, out var rid))
        {
            lock (pendingPickupRuntimeIds) pendingPickupRuntimeIds.Add(rid.Value);
        }
    }

    private static void Postfix(Item __instance)
    {
        var runtime = DarkwoodAdapterRuntime.Instance;
        if (runtime == null || !runtime.IsClient || runtime.State != ConnectionState.Ready) return;

        // 尝试直接从 __instance 取 rid
        ulong rid = 0;
        bool persistent = false;
        if (__instance != null && runtime.replication.TryGetId(__instance, out var ridObj))
        {
            rid = ridObj.Value;
            persistent = ridObj.IsPersistent;
        }
        else
        {
            // 从 pending 列表恢复（Item 可能已被原版 destroy）
            lock (pendingPickupRuntimeIds)
            {
                // 取任意一个（最近的拾取）
                foreach (var v in pendingPickupRuntimeIds) { rid = v; break; }
                pendingPickupRuntimeIds.Clear();
            }
        }
        if (rid == 0)
        {
            runtime.log?.LogInfo("[PICKUP] 拾取对象非 Host Runtime Entity（可能是本地对象或已 destroy），跳过 PickupCommit。");
            return;
        }
        // 原版 getDroppedItem 后 Item 内的物品已经入背包（transferItemAllToPlayer）；Item GameObject 可能被 destroy。
        // 我们通过 Player.Instance.Inventory 反查最近加入的物品类型/数量，作为 ItemType/Amount 上报。
        var player = Player.Instance;
        if (player == null || player.Inventory == null) return;
        string itemType = string.Empty;
        int amount = 0;
        // 取第一个非空槽（如果出现新物品就是它）
        for (var i = 0; i < player.Inventory.slots.Count; i++)
        {
            var s = player.Inventory.slots[i];
            if (s == null || s.invItem == null || InvItemClass.isNull(s.invItem)) continue;
            itemType = s.invItem.type ?? string.Empty;
            amount = s.invItem.amount;
            break;
        }
        if (string.IsNullOrEmpty(itemType))
        {
            runtime.log?.LogWarning($"[PICKUP] 已 transfer 但 Player.Inventory 仍为空（异常），runtime=0x{rid:X8} 不上报。");
            return;
        }
        var selfPeer = runtime.clientSession?.PeerId ?? 0;
        if (selfPeer <= 0) return;
        var rev = runtime.NextLocalInventoryRevision(selfPeer);
        var st = DarkwoodWorldAuthorityService.CaptureLocalPlayerInventory();
        var commit = new PickupCommitMessage(
            System.Guid.NewGuid(),
            rid, persistent,
            itemType, amount,
            selfPeer, rev,
            st.Backpack, st.Hotbar);
        runtime.clientSession.Send(ProtocolMessageType.PickupCommit, ReplicationProtocolCodec.Encode(commit));
        runtime.log?.LogInfo($"[PICKUP] pickupCommit sent peer={selfPeer} runtime=0x{rid:X8} type={itemType} x{amount} rev={rev} → 等待 Host Despawn。");
    }
}
