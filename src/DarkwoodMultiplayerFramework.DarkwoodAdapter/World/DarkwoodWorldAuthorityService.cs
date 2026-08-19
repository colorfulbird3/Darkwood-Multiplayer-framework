using System;
using System.Linq;
using DarkwoodMultiplayerFramework.Core;
using DarkwoodMultiplayerFramework.Protocol;
using UnityEngine;

namespace DarkwoodMultiplayerFramework.DarkwoodAdapter;

/// <summary>
/// 世界层权威执行器（0.8.9 第 2 刀：Drop + Pickup）。
/// 所有世界 mutation 只能经此入口：Harmony Patch 只构造 Intent 并调用本服务。
/// peer==0 表示 Host 本地玩家（真实背包）；peer>0 表示远端玩家（Host 侧权威 shadow）。
/// </summary>
public sealed class DarkwoodWorldAuthorityService
{
    private readonly IMultiplayerRuntimeHost runtime;
    private readonly DarkwoodRuntimeEntityService runtimeEntities;

    internal DarkwoodWorldAuthorityService(IMultiplayerRuntimeHost runtime, DarkwoodRuntimeEntityService runtimeEntities)
    {
        this.runtime = runtime;
        this.runtimeEntities = runtimeEntities;
    }

    /// <summary>
    /// Host 执行 Drop。成功返回 ActionResult 背包状态 payload；失败返回 null 并已记录原因。
    /// </summary>
    public ActionResultMessage? DropItem(int peer, DropItemPayload payload, ActionRequestMessage request, Action<int, ActionRequestMessage, string, ulong> reject)
    {
        InvItemClass item;
        Inventory? sourceContainer = null;
        var originContainer = default(EntityId);
        if (payload.Origin == DropOriginWire.SharedContainer)
        {
            // 手上物品来自容器（共享容器/尸体/商人）：从权威容器扣减，影子背包不动。
            if (payload.ContainerValue == 0) { reject(peer, request, "CONTAINER_NOT_FOUND", 0); return null; }
            originContainer = new EntityId(payload.ContainerValue, payload.ContainerPersistent);
            if (!runtime.Replication.TryGetInventory(originContainer, out sourceContainer)) { reject(peer, request, "CONTAINER_NOT_FOUND", 0); return null; }
            if (payload.SlotIndex < 0 || payload.SlotIndex >= sourceContainer.slots.Count) { reject(peer, request, "SLOT_OUT_OF_RANGE", 0); return null; }
            var sourceSlot = sourceContainer.slots[payload.SlotIndex];
            if (sourceSlot == null || InvItemClass.isNull(sourceSlot.invItem)) { reject(peer, request, "SLOT_EMPTY", 0); return null; }
            if (sourceSlot.invItem.amount < payload.Amount) { reject(peer, request, "INSUFFICIENT_AMOUNT", 0); return null; }
            item = new InvItemClass(sourceSlot.invItem);
            item.amount = payload.Amount; // 只拿请求的数量
        }
        else if (peer == 0)
        {
            // Host 本地玩家：真实背包槽位（先读后扣，事务化）
            var player = Player.Instance;
            if (player?.Inventory == null || player.Hotbar == null) { reject(peer, request, "PLAYER_INVENTORY_MISSING", 0); return null; }
            var slots = payload.FromHotbar ? player.Hotbar.slots : player.Inventory.slots;
            if (payload.SlotIndex < 0 || payload.SlotIndex >= slots.Count) { reject(peer, request, "SLOT_OUT_OF_RANGE", 0); return null; }
            var slot = slots[payload.SlotIndex];
            if (slot == null || InvItemClass.isNull(slot.invItem)) { reject(peer, request, "PLAYER_SLOT_EMPTY", 0); return null; }
            item = new InvItemClass(slot.invItem);
            if (item.amount < payload.Amount) { reject(peer, request, "INSUFFICIENT_AMOUNT", 0); return null; }
        }
        else
        {
            // 远端玩家：Host 侧权威 shadow（客户端不传物品属性，Host 自己读）
            if (!runtime.Players.TryGetInventory(peer, out var shadow)) { reject(peer, request, "PLAYER_INVENTORY_MISSING", 0); return null; }
            if (!shadow.TryPeek(payload.FromHotbar, payload.SlotIndex, payload.Amount, out var source)) { reject(peer, request, "PLAYER_SLOT_EMPTY", 0); return null; }
            item = new InvItemClass(source.Type, source.Durability, source.Amount, (InvItem.ModifierQuality)source.Quality, source.Recipe);
        }

        var position = new Vector3(payload.X, payload.Y, payload.Z);
        var rotation = new Quaternion(payload.Qx, payload.Qy, payload.Qz, payload.Qw);

        // 事务顺序：先创建世界对象 + 分配 ID，全部成功后才扣库存（失败不丢物品）
        var dropped = CreateDroppedItem(item, position, rotation);
        if (dropped == null) { reject(peer, request, "DROP_CREATE_FAILED", 0); return null; }

        var runtimeId = runtimeEntities.BroadcastSpawn(RuntimeEntityKind.DroppedItem, "Items/DroppedItem", position, rotation, ReplicationProtocolCodec.Encode(runtime.Replication.CaptureInventoryState(dropped, 0)));
        if (runtimeId == 0)
        {
            UnityEngine.Object.Destroy(dropped.gameObject);
            reject(peer, request, "DROP_ID_ALLOC_FAILED", 0);
            return null;
        }

        // 创建成功 → 按来源扣减（扣减失败防御性回滚：销毁已创建的掉落物）
        if (payload.Origin == DropOriginWire.SharedContainer)
        {
            var sourceSlot = sourceContainer!.slots[payload.SlotIndex];
            sourceSlot.invItem.amount -= payload.Amount;
            if (sourceSlot.invItem.amount <= 0) sourceSlot.removeItem();
            sourceSlot.inventory?.refreshItems();
        }
        else if (peer == 0)
        {
            var player = Player.Instance;
            var slots = payload.FromHotbar ? player.Hotbar.slots : player.Inventory.slots;
            var slot = slots[payload.SlotIndex];
            slot.invItem.amount -= payload.Amount;
            if (slot.invItem.amount <= 0) slot.removeItem();
            slot.inventory?.refreshItems();
        }
        else
        {
            if (!runtime.Players.TryGetInventory(peer, out var shadow) || !shadow.Remove(payload.FromHotbar, payload.SlotIndex, payload.Amount))
            {
                UnityEngine.Object.Destroy(dropped.gameObject);
                runtimeEntities.BroadcastDespawn(runtimeId, RuntimeEntityDespawnReason.Destroyed);
                reject(peer, request, "INSUFFICIENT_AMOUNT", 0);
                return null;
            }
        }
        runtime.Replication.RegisterBinding(new WorldEntityBinding
        {
            Id = new EntityId(runtimeId, false),
            Root = dropped.gameObject,
            Primary = dropped,
            Inventory = dropped,
            Item = dropped.GetComponentInChildren<Item>(),
            Kind = WorldEntityKind.DroppedItem
        });

        runtime.LogInfo($"Drop accepted: peer {peer}, {item.type} x{payload.Amount}, origin {payload.Origin}, slot {payload.SlotIndex}, runtime id {runtimeId}.");

        if (payload.Origin == DropOriginWire.SharedContainer)
        {
            // 容器权威状态广播（全部客户端），背包无变化 → ActionResult 不带背包 payload
            try
            {
                var containerState = runtime.Replication.CaptureAuthoritativeInventory(originContainer);
                var stateBytes = ReplicationProtocolCodec.Encode(containerState);
                foreach (var readyPeer in runtime.ReadyPeers.ToArray())
                    runtime.Queue(readyPeer, ProtocolMessageType.InventoryState, stateBytes);
            }
            catch (Exception error) { runtime.LogWarning($"丢弃后广播容器状态失败：{error.Message}"); }
            return new ActionResultMessage(request.RequestId, request.Kind, runtimeId, false, 1, Array.Empty<byte>());
        }
        if (peer == 0)
        {
            // Host 本地：广播权威背包给所有客户端
            var hostState = CaptureLocalPlayerInventory();
            var payloadBytes = ReplicationProtocolCodec.Encode(hostState);
            foreach (var readyPeer in runtime.ReadyPeers.ToArray())
                runtime.Queue(readyPeer, ProtocolMessageType.PlayerInventoryState, payloadBytes);
            return new ActionResultMessage(request.RequestId, request.Kind, runtimeId, false, 1, payloadBytes);
        }
        else
        {
            if (!runtime.Players.TryGetInventory(peer, out var shadow)) return null;
            return new ActionResultMessage(request.RequestId, request.Kind, runtimeId, false, 1, ReplicationProtocolCodec.Encode(shadow.CaptureState()));
        }
    }

    /// <summary>创建原版掉落物对象（DroppedItem 预制 + 首个槽位写物品）。失败返回 null。</summary>
    private Inventory CreateDroppedItem(InvItemClass item, Vector3 position, Quaternion rotation)
    {
        try
        {
            var yaw = rotation.eulerAngles.y;
            var groundPos = global::Core.getYPos(position, PosType.items1);
            var go = global::Core.AddPrefab("Items/DroppedItem", groundPos, Quaternion.Euler(90f, yaw, 0f), global::Core.ItemContainer);
            if (go == null) return null;
            var dropped = go.GetComponent<Inventory>();
            if (dropped == null || dropped.slots == null || dropped.slots.Count == 0) { UnityEngine.Object.Destroy(go); return null; }
            var slot = dropped.slots[0];
            slot.inventory = dropped;
            slot.createItem(new InvItemClass(item));
            global::Core.addToSaveable(go, true);
            if (Singleton<WorldGrid>.Instance != null) Singleton<WorldGrid>.Instance.registerToNode(go);
            return dropped;
        }
        catch (Exception error)
        {
            runtime.LogWarning($"创建掉落物失败（{item.type}）：{error.Message}");
            return null;
        }
    }

    /// <summary>捕获本机玩家真实背包（Host 广播权威背包 / 客户端漂移上报共用）。</summary>
    internal static PlayerInventoryStatePayload CaptureLocalPlayerInventory()
    {
        var player = Player.Instance;
        if (player?.Inventory == null || player.Hotbar == null) throw new InvalidOperationException("玩家库存不可用。");
        return new PlayerInventoryStatePayload(
            ToWire(player.Inventory.slots),
            ToWire(player.Hotbar.slots));
    }


    private static InventorySlotWire[] ToWire(System.Collections.Generic.List<InvSlot> slots)
    {
        var result = new InventorySlotWire[slots.Count];
        for (var i = 0; i < slots.Count; i++)
        {
            var s = slots[i];
            if (InvItemClass.isNull(s.invItem)) { result[i] = new InventorySlotWire(string.Empty, 0, 0f, 0, false); continue; }
            result[i] = new InventorySlotWire(s.invItem.type, s.invItem.amount, s.invItem.durability, (int)s.invItem.modifierQuality, s.invItem.isRecipe);
        }
        return result;
    }
}
