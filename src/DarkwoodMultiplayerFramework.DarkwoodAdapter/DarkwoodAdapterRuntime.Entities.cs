using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using DarkwoodMultiplayerFramework.Actions;
using DarkwoodMultiplayerFramework.Core;
using DarkwoodMultiplayerFramework.Entities;
using DarkwoodMultiplayerFramework.Network;
using DarkwoodMultiplayerFramework.Protocol;
using HarmonyLib;
using Steamworks;
using UnityEngine;
using UnityEngine.SceneManagement;


namespace DarkwoodMultiplayerFramework.DarkwoodAdapter;

public sealed partial class DarkwoodAdapterRuntime
{
    public void ReportSharedContainerChanged(Inventory inventory)
    {
        if(clientSession?.Session.Lifecycle.State!=ConnectionState.Ready||inventory==null||replication.ApplyingRemote)return;
        if(inventory.invType!=Inventory.InvType.itemInv&&inventory.invType!=Inventory.InvType.deathDrop)return;
        if(!replication.TryGetId(inventory,out var id))return;
        try
        {
            var state=replication.CaptureAuthoritativeInventory(id);
            clientSession.Send(ProtocolMessageType.InventoryState,ReplicationProtocolCodec.Encode(state));
            log?.LogInfo($"客户端已上报共享容器状态：ID={id.Value:X16}，版本 {state.Revision}，槽位 {state.Slots.Length}。");
        }
        catch(Exception error){log?.LogWarning($"Failed to report client container mutation for {id}: {error.Message}");}
    }

    private void HandleActionRequest(int peer,ActionRequestMessage request)
    {
        if(request.RequestId==Guid.Empty)return;
        if(request.PlayerId!=peer){RejectAction(peer,request,"PLAYER_ID_MISMATCH",0);return;}
        if(!readyPeers.Contains(peer)){RejectAction(peer,request,"PEER_NOT_READY",0);return;}
        if(actionCache.TryGet(request.RequestId,out var cached))
        {
            if(!cachedActionOwners.TryGetValue(request.RequestId,out var owner)||owner!=peer)
            {
                Queue(peer,ProtocolMessageType.ActionRejected,ReplicationProtocolCodec.Encode(new ActionRejectedMessage(request.RequestId,request.Kind,request.TargetValue,request.TargetPersistent,0,"REQUEST_ID_COLLISION")));
                return;
            }
            duplicateActions++;SendCachedAction(peer,request,cached);return;
        }
        switch(request.Kind)
        {
            case ActionKindWire.Pickup: HandlePickupRequest(peer,request);return;
            case ActionKindWire.Attack: HandleAttackRequest(peer,request);return;
            case ActionKindWire.DoorInteract: HandleDoorInteractRequest(peer,request);return;
            case ActionKindWire.WindowInteract: HandleWindowInteractRequest(peer,request);return;
            case ActionKindWire.ItemActivate: HandleItemActivateRequest(peer,request);return;
            case ActionKindWire.DropItem: HandleDropRequest(peer,request);return;
            case ActionKindWire.ContainerTake: HandleContainerTakeRequest(peer,request);return;
            case ActionKindWire.ContainerPut: HandleContainerPutRequest(peer,request);return;
            case ActionKindWire.ContainerGrab: HandleContainerGrabRequest(peer,request);return;
            case ActionKindWire.HeldToInventory: HandleHeldToInventoryRequest(peer,request);return;
            case ActionKindWire.PlayerGrab: HandlePlayerGrabRequest(peer,request);return;
            case ActionKindWire.HeldToContainer: HandleHeldToContainerRequest(peer,request);return;
            case ActionKindWire.StateObjectInteract: HandleStateObjectInteractRequest(peer,request);return;
            case ActionKindWire.PlayerAction: HandlePlayerActionRequest(peer,request);return;
            case ActionKindWire.ItemInteract: HandleItemInteractRequest(peer,request);return;
            default: RejectAction(peer,request,"UNSUPPORTED_ACTION",0);return;
        }
    }

    private void HandlePickupRequest(int peer,ActionRequestMessage request)
    {
        var id=new EntityId(request.TargetValue,request.TargetPersistent);
        if(!replication.TryGetItem(id,out var item))
        {
            // P0-F：runtime 实体找不到 binding 时先做 RUNTIME-CHECK 诊断（不静默删），再拒绝。
            if (!id.IsPersistent && RuntimeEntities != null) RuntimeEntities.DumpRuntimeCheck(id.Value);
            RejectAction(peer,request,"ENTITY_NOT_FOUND",0);return;
        }
        if(!replication.TryGetState(id,out var state)){RejectAction(peer,request,"ENTITY_STATE_MISSING",0);return;}
        if(!item.gameObject.activeSelf||item.destroyed||!item.isDroppedItem){RejectAction(peer,request,"NOT_PICKABLE",state.Revision);return;}
        var droppedInventory=DarkwoodDroppedItemAccessor.GetInventory(item);
        if(droppedInventory==null||droppedInventory.slots==null||droppedInventory.slots.Count==0||InvItemClass.isNull(droppedInventory.slots[0].invItem)){RejectAction(peer,request,"ITEM_EMPTY",state.Revision);return;}
        if(!Players.TryGetInventory(peer,out var shadow)){RejectAction(peer,request,"PLAYER_INVENTORY_MISSING",state.Revision);return;}
        var source=droppedInventory.slots[0].invItem;
        // 阶段三：WorldDroppedItem Pickup —— 原版语义直接进背包（不经过 Cursor）。
        // 客户端只发 PickupRequest（+ItemType/Amount 校验），Host 用真实原版 DroppedItem 数据转移到权威 shadow。
        PickupPayload pp;
        try { pp = ReplicationProtocolCodec.DecodePickup(request.Payload); } catch (Exception) { pp = default; }
        if (!InvItemClass.isNull(source) && !string.IsNullOrEmpty(pp.ItemType) && (pp.ItemType != source.type || pp.Amount != source.amount))
        { RejectAction(peer, request, "PICKUP_MISMATCH", state.Revision); return; }
        if (Players.TryGetRemotePosition(peer, out var rpos) && item.transform != null)
        {
            try { if (Vector3.Distance(rpos, item.transform.position) > 4f) { RejectAction(peer, request, "TOO_FAR", state.Revision); return; } } catch (Exception) { }
        }
        if (!shadow.CanFit(source)) { RejectAction(peer, request, "INVENTORY_FULL", state.Revision); return; }
        if (!shadow.AddItem(source)) { RejectAction(peer, request, "INVENTORY_FULL", state.Revision); return; } // 成功内置 Touch（revision++）
        ActionResultMessage result = default;
        var invState = shadow.CaptureState(peer); // InventorySnapshot（带 revision）
        if (!id.IsPersistent && RuntimeEntities != null)
        {
            droppedInventory.slots[0].removeItem();
            droppedInventory.refreshItems();
            ulong rev=0; if (replication.TryGetState(id, out var st)) rev = st.Revision;
            // P0 五顺序：先 ack（携带 InventorySnapshot）后 RuntimeEntityDespawn。
            result=new ActionResultMessage(request.RequestId,request.Kind,id.Value,id.IsPersistent,rev+1,ReplicationProtocolCodec.Encode(invState));
            RemoveEvictedAction(actionCache.Store(new NetworkActionResult(request.RequestId,true,new StateVersion(rev+1),string.Empty)));acceptedActions++;
            cachedActionResults[request.RequestId]=result;
            cachedActionOwners[request.RequestId]=peer;
            Queue(peer,ProtocolMessageType.ActionResult,ReplicationProtocolCodec.Encode(result)); // ★ 先 ack
            serverTick++;
            RuntimeEntities.BroadcastDespawn(id.Value, RuntimeEntityDespawnReason.Collected); // ★ 后 despawn
            replication.UnregisterRuntimeEntity(id);
            if (item != null) try { UnityEngine.Object.Destroy(item.gameObject); } catch (Exception) { }
            log?.LogInfo($"[RUNTIME] pickup id={id} peer={peer} {source.type} x{source.amount} → 直接入背包（原版 transfer 语义）rev={invState.Revision}+RuntimeEntityDespawn+unregister。");
            return;
        }
        droppedInventory.slots[0].removeItem();
        droppedInventory.refreshItems();
        var despawn=replication.MarkDespawned(id);serverTick++;
        result=new ActionResultMessage(request.RequestId,request.Kind,id.Value,id.IsPersistent,despawn.Revision,ReplicationProtocolCodec.Encode(invState));
        RemoveEvictedAction(actionCache.Store(new NetworkActionResult(request.RequestId,true,new StateVersion(despawn.Revision),string.Empty)));acceptedActions++;
        cachedActionResults[request.RequestId]=result;
        cachedActionOwners[request.RequestId]=peer;
        Queue(peer,ProtocolMessageType.ActionResult,ReplicationProtocolCodec.Encode(result));
        var delta=ReplicationProtocolCodec.Encode(new EntityDeltaMessage(CurrentScene,serverTick,Array.Empty<EntityStateWire>(),new[]{despawn}));foreach(var readyPeer in readyPeers.ToArray())Queue(readyPeer,ProtocolMessageType.EntityDelta,delta);
        log?.LogInfo($"[RUNTIME] pickup id={id} peer={peer} {source.type} x{source.amount} → 直接入背包（persistent despawn）rev={invState.Revision}。");
    }

    private void HandleItemInteractRequest(int peer,ActionRequestMessage request)
    {
        var id=new EntityId(request.TargetValue,request.TargetPersistent);
        if(!replication.TryGetItem(id,out var item)){RejectAction(peer,request,"ITEM_NOT_FOUND",0);return;}
        var interact=ReplicationProtocolCodec.DecodeInteract(request.Payload);
        item.searched = interact.ValueA != 0;
        AcceptInteract(peer,request,id,item,0);
        log?.LogInfo($"主机已应用物品交互 {request.RequestId}：玩家 {peer}，物品 {id}，searched={item.searched}。");
    }

    private void HandleContainerTakeRequest(int peer,ActionRequestMessage request)
    {
        ContainerTakePayload payload;
        try{payload=ReplicationProtocolCodec.DecodeContainerTake(request.Payload);}
        catch(Exception error){RejectAction(peer,request,"INVALID_TAKE_PAYLOAD",0);log?.LogWarning($"ContainerTake payload rejected from peer {peer}: {error.Message}");return;}
        var id=new EntityId(request.TargetValue,request.TargetPersistent);
        if(!replication.TryGetInventory(id,out var container)){RejectAction(peer,request,"CONTAINER_NOT_FOUND",0);return;}
        if(!DarkwoodEntityStateAdapter.IsShared(container)){RejectAction(peer,request,"NOT_SHARED_CONTAINER",0);return;}
        if(payload.SlotIndex<0||payload.SlotIndex>=container.slots.Count){RejectAction(peer,request,"SLOT_OUT_OF_RANGE",0);return;}
        var slot=container.slots[payload.SlotIndex];
        if(InvItemClass.isNull(slot.invItem)){RejectAction(peer,request,"SLOT_EMPTY",0);return;}
        var amount=Math.Min(payload.Amount,slot.invItem.amount);
        if(amount<=0){RejectAction(peer,request,"INVALID_AMOUNT",0);return;}
        if(!Players.TryGetInventory(peer,out var shadow)){RejectAction(peer,request,"PLAYER_INVENTORY_MISSING",0);return;}
        var item=new InvItemClass(slot.invItem);
        item.amount=amount; // 只给请求的数量，绝不复制整个槽（复制物品风险）
        if(!shadow.CanAdd(item)){RejectAction(peer,request,"INVENTORY_FULL",0);return;}
        // 权威事务：容器扣 → 玩家 shadow 加
        slot.invItem.amount-=amount;
        if(slot.invItem.amount<=0)slot.removeItem();
        slot.inventory?.refreshItems();
        shadow.Add(item);
        // 立即广播权威容器状态（全部客户端）
        var state=replication.CaptureAuthoritativeInventory(id);
        BroadcastInventory(state);
        var result=new ActionResultMessage(request.RequestId,request.Kind,id.Value,id.IsPersistent,state.Revision,ReplicationProtocolCodec.Encode(shadow.CaptureState(peer)));
        RemoveEvictedAction(actionCache.Store(new NetworkActionResult(request.RequestId,true,new StateVersion(state.Revision),string.Empty)));acceptedActions++;
        cachedActionResults[request.RequestId]=result;cachedActionOwners[request.RequestId]=peer;
        Queue(peer,ProtocolMessageType.ActionResult,ReplicationProtocolCodec.Encode(result));
        log?.LogInfo($"ContainerTake accepted {request.RequestId}: peer {peer}, {item.type} x{amount}, container {id}, slot {payload.SlotIndex}.");
    }

    // P0-D/E：共享容器 grab → 该玩家权威 HeldItem（鼠标手持）。客户端据此恢复原版 cursor UX。
    private void HandleContainerGrabRequest(int peer,ActionRequestMessage request)
    {
        ContainerGrabPayload payload;
        try{payload=ReplicationProtocolCodec.DecodeContainerGrab(request.Payload);}
        catch(Exception error){RejectAction(peer,request,"INVALID_GRAB_PAYLOAD",0);log?.LogWarning($"ContainerGrab payload rejected from peer {peer}: {error.Message}");return;}
        var id=new EntityId(request.TargetValue,request.TargetPersistent);
        if(!replication.TryGetInventory(id,out var container)){RejectAction(peer,request,"CONTAINER_NOT_FOUND",0);return;}
        if(!DarkwoodEntityStateAdapter.IsShared(container)){RejectAction(peer,request,"NOT_SHARED_CONTAINER",0);return;}
        if(payload.SlotIndex<0||payload.SlotIndex>=container.slots.Count){RejectAction(peer,request,"SLOT_OUT_OF_RANGE",0);return;}
        var slot = container.slots[payload.SlotIndex];
        if (InvItemClass.isNull(slot.invItem)) { RejectAction(peer, request, "SLOT_EMPTY", 0); return; }
        // P0-1：原版 grabItem 拿整个 InvItemClass/整堆；数量以权威槽为准（不信任客户端 amount）。
        var amount = Math.Max(1, slot.invItem.amount);
        // 已有 HeldItem 未清 → 拒绝（防覆盖丢物品）
        if(HeldItems.ContainsKey(peer)){RejectAction(peer,request,"ALREADY_HOLDING",0);return;}
        // 事务：容器扣 → HeldItem
        var held=new HeldItemStatePayload(slot.invItem.type,amount,slot.invItem.durability,(int)slot.invItem.modifierQuality,slot.invItem.isRecipe,slot.invItem.ammo);
        slot.invItem.amount-=amount;
        if(slot.invItem.amount<=0)slot.removeItem();
        slot.inventory?.refreshItems();
        HeldItems[peer]=held;
        var state=replication.CaptureAuthoritativeInventory(id);
        // P0 五（消息顺序）：发起 Client 必须先收到 ActionResult（其本地 source slot 尚未被清 → 可 Replay grabItem），
        // 再收到权威容器状态（reconcile）。绝不先广播 InventoryState 清 source 让 Replay 无物可抓。
        var result=new ActionResultMessage(request.RequestId,request.Kind,id.Value,id.IsPersistent,state.Revision,ReplicationProtocolCodec.Encode(held));
        RemoveEvictedAction(actionCache.Store(new NetworkActionResult(request.RequestId,true,new StateVersion(state.Revision),string.Empty)));acceptedActions++;
        cachedActionResults[request.RequestId]=result;cachedActionOwners[request.RequestId]=peer;
        Queue(peer,ProtocolMessageType.ActionResult,ReplicationProtocolCodec.Encode(result)); // ★ 先 ack
        BroadcastInventory(state); // ★ 后权威容器广播（含发起者的 reconcile）
        log?.LogInfo($"[RUNTIME] ContainerGrab accepted {request.RequestId}: peer {peer}, {held.Type} x{held.Amount} → HeldItem（cursor），容器 {id} 槽 {payload.SlotIndex}。");
    }

    // P0-D/E + P0-3：鼠标 HeldItem 放回玩家背包指定槽（原版 placeItem 语义：empty→place / 同类→stack）。
    private void HandleHeldToInventoryRequest(int peer,ActionRequestMessage request)
    {
        HeldToInventoryPayload payload;
        try{payload=ReplicationProtocolCodec.DecodeHeldToInventory(request.Payload);}
        catch(Exception error){RejectAction(peer,request,"INVALID_HELD_PLACE_PAYLOAD",0);log?.LogWarning($"HeldToInventory payload rejected from peer {peer}: {error.Message}");return;}
        if(!HeldItems.TryGetValue(peer,out var held)||held.IsEmpty){RejectAction(peer,request,"NOT_HOLDING",0);return;}
        if(!Players.TryGetInventory(peer,out var shadow)){RejectAction(peer,request,"PLAYER_INVENTORY_MISSING",0);return;}
        // P0-A：拓扑强门——Host 必须在第一笔物品交互前拿到客户端真实背包容量，否则不得用 INVALID_TARGET_SLOT 误拒，也绝不凭空猜容量。
        if(!shadow.TopologyReady){RejectAction(peer,request,"PLAYER_INVENTORY_NOT_READY",0);return;}
        // P0-C：place-validate 完整诊断（target 是否在真实 capacity 内由 PlaceAt 正确判定）。
        log?.LogInfo($"[HELD] place-validate peer={peer} target={(payload.FromHotbar?"hotbar":"playerInv")}:{payload.TargetSlot} backpackCount={shadow.BackpackCountOut} hotbarCount={shadow.HotbarCountOut} backpackCapacity={shadow.BackpackCapacity} hotbarCapacity={shadow.HotbarCapacity} heldType={held.Type} heldAmount={held.Amount}");
        var item=new InvItemClass(held.Type,held.Durability,held.Amount,(InvItem.ModifierQuality)held.Quality,held.Recipe);
        var place=shadow.PlaceAt(payload.FromHotbar,payload.TargetSlot,item);
        if(place!=DarkwoodPlayerInventoryShadow.HeldPlaceResult.Placed&&place!=DarkwoodPlayerInventoryShadow.HeldPlaceResult.Stacked)
        { RejectAction(peer,request,place==DarkwoodPlayerInventoryShadow.HeldPlaceResult.Occupied?"SLOT_OCCUPIED":"INVALID_TARGET_SLOT",0);
          log?.LogInfo($"[HELD] place-rejected peer={peer} {held.Type} x{held.Amount} 目标={(payload.FromHotbar?"hotbar":"playerInv")}:{payload.TargetSlot} result={place}。"); return; }
        HeldItems.Remove(peer);
        var result=new ActionResultMessage(request.RequestId,request.Kind,0,false,0,ReplicationProtocolCodec.Encode(shadow.CaptureState(peer)));
        RemoveEvictedAction(actionCache.Store(new NetworkActionResult(request.RequestId,true,new StateVersion(0),string.Empty)));acceptedActions++;
        cachedActionResults[request.RequestId]=result;cachedActionOwners[request.RequestId]=peer;
        Queue(peer,ProtocolMessageType.ActionResult,ReplicationProtocolCodec.Encode(result));
        log?.LogInfo($"[HELD] place-accepted peer={peer} {held.Type} x{held.Amount} 目标={(payload.FromHotbar?"hotbar":"playerInv")}:{payload.TargetSlot} result={place}。");
    }

    // ── 阶段二：世界状态对象交互（Host 执行原版逻辑 → 即时捕获广播权威状态）──
    public void BroadcastStateNow(EntityId id)
    {
        if (!Session.IsHost) return;
        var wire = replication.CaptureNow(id);
        if (wire == null) return;
        var delta = ReplicationProtocolCodec.Encode(new EntityDeltaMessage(CurrentScene, serverTick, new[] { wire.Value }, Array.Empty<EntityStateWire>()));
        foreach (var rp in readyPeers.ToArray()) Queue(rp, ProtocolMessageType.EntityDelta, delta);
        log?.LogInfo($"[STATE] 即时广播状态：entity={id.Value:X8} schema={wire.Value.StateSchema} rev={wire.Value.Revision} source=Host");
    }

    private void HandleStateObjectInteractRequest(int peer, ActionRequestMessage request)
    {
        StateObjectIntentPayload payload;
        try { payload = ReplicationProtocolCodec.DecodeStateObjectIntent(request.Payload); }
        catch (Exception error) { RejectAction(peer, request, "INVALID_STATE_INTENT", 0); log?.LogWarning($"StateObjectInteract payload rejected from peer {peer}: {error.Message}"); return; }
        var id = new EntityId(request.TargetValue, request.TargetPersistent);
        if (!replication.TryGetBinding(id, out var binding) || binding.Primary == null) { RejectAction(peer, request, "ENTITY_NOT_FOUND", 0); return; }
        var comp = binding.Primary;
        // 类型化原版执行（禁字符串分发；Host 是唯一 authority）：
        if (comp is Generator g)
        {
            if (payload.Interaction == "toggle") { if (g.isOn) g.turnOff(); else g.turnOn(); }
            else if (payload.Interaction == "on") { if (!g.isOn) g.turnOn(); }
            else if (payload.Interaction == "off") { if (g.isOn) g.turnOff(); }
            else { RejectAction(peer, request, "UNKNOWN_INTERACTION", 0); return; }
            log?.LogInfo($"[GENERATOR] id={id.Value:X8} peer={peer} interaction={payload.Interaction} → running={g.isOn} fuel={g.fuel:F0}（Host 原版 turnOn/turnOff 已执行）");
            BroadcastStateNow(id);
        }
        else
        {
            RejectAction(peer, request, "NOT_STATE_OBJECT", 0);
            log?.LogWarning($"[STATE] peer={peer} 请求交互实体 {id.Value:X8} 不是状态对象（{comp.GetType().Name}）。");
            return;
        }
        var ack = new ActionResultMessage(request.RequestId, request.Kind, request.TargetValue, request.TargetPersistent, 0, Array.Empty<byte>());
        RemoveEvictedAction(actionCache.Store(new NetworkActionResult(request.RequestId, true, new StateVersion(0), string.Empty))); acceptedActions++;
        cachedActionResults[request.RequestId] = ack; cachedActionOwners[request.RequestId] = peer;
        Queue(peer, ProtocolMessageType.ActionResult, ReplicationProtocolCodec.Encode(ack));
    }

    // ── v0.9.0 服务层接入点（Network 门面调用的 public accessors）──
    public void PlayerSyncSendPose() => SendLocalPose();
    /// <summary>按 EntityId 取已绑定共享容器（EntitySync 门面）。</summary>
    public bool TryGetBoundInventory(EntityId id, out Inventory inventory) => replication.TryGetInventory(id, out inventory!);
    /// <summary>加入时的快照是否接收完整（SnapshotSync 门面）。</summary>
    public bool SnapshotTransferComplete => bindingAssembler != null && bindingAssembler.IsComplete;

    // ── v0.9.0 EventSync：玩家动作事件（本地已执行原版 → 中继）──
    public bool TryRequestPlayerAction(string action)
    {
        if (clientSession?.Session.Lifecycle.State != ConnectionState.Ready || string.IsNullOrEmpty(action)) return false;
        var payload = ReplicationProtocolCodec.Encode(new PlayerActionPayload(action));
        var request = new ActionRequestMessage(Guid.NewGuid(), clientSession.PeerId, ActionKindWire.PlayerAction, 0, false, 0, payload);
        pendingActions[request.RequestId] = request;
        clientSession.Send(ProtocolMessageType.ActionRequest, ReplicationProtocolCodec.Encode(request));
        log?.LogInfo($"[EVENT] playerAction sent: {action} (本地已执行原版，Host 中继给其他玩家)");
        return true;
    }
    public void RelayPlayerAction(int sourcePeer, PlayerActionPayload payload)
    {
        var bytes = ReplicationProtocolCodec.Encode(payload);
        foreach (var rp in readyPeers.ToArray())
            if (rp != sourcePeer) Queue(rp, ProtocolMessageType.PlayerAction, bytes);
        log?.LogInfo($"[EVENT] playerAction relay: {payload.Action} from={sourcePeer} → others");
    }

    private void HandlePlayerActionRequest(int peer, ActionRequestMessage request)
    {
        PlayerActionPayload payload;
        try { payload = ReplicationProtocolCodec.DecodePlayerAction(request.Payload); } catch (Exception) { payload = default; }
        // Trusted Client：动作已在客户端本地执行——Host 只做事件中继（其他玩家播放），不做世界 mutation。
        RelayPlayerAction(peer, payload);
        var ack = new ActionResultMessage(request.RequestId, request.Kind, request.TargetValue, request.TargetPersistent, 0, Array.Empty<byte>());
        RemoveEvictedAction(actionCache.Store(new NetworkActionResult(request.RequestId, true, new StateVersion(0), string.Empty))); acceptedActions++;
        cachedActionResults[request.RequestId] = ack; cachedActionOwners[request.RequestId] = peer;
        Queue(peer, ProtocolMessageType.ActionResult, ReplicationProtocolCodec.Encode(ack));
    }

    // ── v0.9.0 Trusted Client Drop：本地原版对象 → 复用为 mirror（防双份/ghost），超时未匹配则销毁 ──
    public Inventory? PendingLocalDropInventory;
    public string PendingLocalDropType = "";
    public int PendingLocalDropAmount;
    public Vector3 PendingLocalDropPos;
    public float PendingLocalDropAt;
    public void SetPendingLocalDrop(Inventory? inv, string type, int amount, Vector3 pos)
    { PendingLocalDropInventory = inv; PendingLocalDropType = type ?? ""; PendingLocalDropAmount = amount; PendingLocalDropPos = pos; PendingLocalDropAt = Time.unscaledTime; }
    private void ClearPendingLocalDrop() { PendingLocalDropInventory = null; PendingLocalDropType = ""; PendingLocalDropAmount = 0; }
    public Inventory? TakePendingLocalDrop(RuntimeEntitySpawnMessage spawn)
    {
        var inv = PendingLocalDropInventory;
        if (inv == null || inv.gameObject == null) { ClearPendingLocalDrop(); return null; }
        var s0 = (inv.slots != null && inv.slots.Count > 0) ? inv.slots[0].invItem : null;
        if (s0 == null || InvItemClass.isNull(s0) || s0.type != PendingLocalDropType) { ClearPendingLocalDrop(); return null; }
        var spawnPos = new Vector3(spawn.X, spawn.Y, spawn.Z);
        if (Vector3.Distance(PendingLocalDropPos, spawnPos) > 3f) { ClearPendingLocalDrop(); return null; }
        ClearPendingLocalDrop();
        return inv;
    }
    public void TickPendingLocalDrop()
    {
        var inv = PendingLocalDropInventory;
        if (inv == null) return;
        if (inv.gameObject == null) { ClearPendingLocalDrop(); return; }
        if (Time.unscaledTime - PendingLocalDropAt > 2.5f)
        {
            log?.LogInfo("[TRUST] drop 未匹配到 Host spawn——销毁本地原版掉落物（防 ghost）。");
            try { UnityEngine.Object.Destroy(inv.gameObject); } catch (Exception) { }
            ClearPendingLocalDrop();
        }
    }

    // P0-J：Held 状态机显式 transition 日志（Host 权威）。
    private void LogHeldState(int peer,string oldState,string newState,string source,string item,int amount,string requestId)
        => log?.LogInfo($"[HELD-STATE] peer={peer} old={oldState} new={newState} source={source} item={item} amount={amount} requestId={requestId}");

    // ── 阶段二：HeldItem → 共享容器（Host 权威：空→放 / 同类→stack / 异类→swap）──
    private void HandleHeldToContainerRequest(int peer, ActionRequestMessage request)
    {
        HeldToContainerPayload payload;
        try { payload = ReplicationProtocolCodec.DecodeHeldToContainer(request.Payload); }
        catch (Exception error) { RejectAction(peer, request, "INVALID_HELD_TO_CONTAINER", 0); log?.LogWarning($"HeldToContainer payload rejected from peer {peer}: {error.Message}"); return; }
        if (!HeldItems.TryGetValue(peer, out var held) || held.IsEmpty) { RejectAction(peer, request, "NOT_HOLDING", 0); return; }
        var id = new EntityId(request.TargetValue, request.TargetPersistent);
        if (!replication.TryGetInventory(id, out var container)) { RejectAction(peer, request, "CONTAINER_NOT_FOUND", 0); return; }
        if (!DarkwoodEntityStateAdapter.IsShared(container)) { RejectAction(peer, request, "NOT_SHARED_CONTAINER", 0); return; }
        if (payload.SlotIndex < 0 || payload.SlotIndex >= container.slots.Count) { RejectAction(peer, request, "INVALID_TARGET_SLOT", 0); return; }
        var slot = container.slots[payload.SlotIndex];
        var before = (slot.invItem != null && !InvItemClass.isNull(slot.invItem)) ? $"{slot.invItem.type} x{slot.invItem.amount}" : "空";
        var incoming = new InvItemClass(held.Type, held.Durability, held.Amount, (InvItem.ModifierQuality)held.Quality, held.Recipe);
        if (held.Ammo > 0) incoming.ammo = held.Ammo;
        string resultKind;
        var heldAfter = new HeldItemStatePayload(string.Empty, 0, 0f, 0, false);
        if (slot.invItem == null || InvItemClass.isNull(slot.invItem))
        {
            // 空槽：直接放入
            slot.invItem = new InvItemClass(incoming);
            try { slot.invItem.initialize(slot); } catch (Exception) { }
            resultKind = "Placed";
        }
        else if (slot.invItem.baseClass != null && slot.invItem.baseClass.stackable && slot.invItem.type == held.Type)
        {
            // 同类：完整放入才 stack（P1-B no-partial 原则）
            var room = slot.invItem.baseClass.maxAmount - slot.invItem.amount;
            if (held.Amount > room) { RejectAction(peer, request, "SLOT_OCCUPIED", 0); return; }
            slot.invItem.amount += held.Amount;
            try { slot.invItem.refresh(); } catch (Exception) { }
            resultKind = "Stacked";
        }
        else
        {
            // 异类：swap——原槽物成为新 Held，槽接收 held
            var old = slot.invItem;
            heldAfter = new HeldItemStatePayload(old.type, old.amount, old.durability, (int)old.modifierQuality, old.isRecipe, old.ammo);
            slot.invItem = new InvItemClass(incoming);
            try { slot.invItem.initialize(slot); } catch (Exception) { }
            resultKind = "Swapped";
        }
        // 权威 Held 状态更新
        if (heldAfter.IsEmpty) HeldItems.Remove(peer);
        else HeldItems[peer] = heldAfter;
        try { container.refreshItems(); } catch (Exception) { }
        var after = (slot.invItem != null && !InvItemClass.isNull(slot.invItem)) ? $"{slot.invItem.type} x{slot.invItem.amount}" : "空";
        // ★ ack 先入队（发起者先收到 → 其本地容器槽尚未被清 → 可 Replay 原版 placeItem/swapItems），随后广播权威容器。
        var ack = new ActionResultMessage(request.RequestId, request.Kind, request.TargetValue, request.TargetPersistent, 0, ReplicationProtocolCodec.Encode(heldAfter));
        RemoveEvictedAction(actionCache.Store(new NetworkActionResult(request.RequestId, true, new StateVersion(0), string.Empty))); acceptedActions++;
        cachedActionResults[request.RequestId] = ack; cachedActionOwners[request.RequestId] = peer;
        Queue(peer, ProtocolMessageType.ActionResult, ReplicationProtocolCodec.Encode(ack));
        try
        {
            var state = replication.CaptureAuthoritativeInventory(id);
            var stateBytes = ReplicationProtocolCodec.Encode(state);
            foreach (var rp in readyPeers.ToArray()) Queue(rp, ProtocolMessageType.InventoryState, stateBytes);
            log?.LogInfo($"[CONTAINER] action=HeldToContainer peer={peer} containerId={id.Value:X8} slot={payload.SlotIndex} before={before} after={after} result={resultKind} broadcastSlots={state.Slots.Length}");
        }
        catch (Exception error) { log?.LogWarning($"[CONTAINER] HeldToContainer 广播容器状态失败：{error.Message}"); }
        LogHeldState(peer, "Holding", heldAfter.IsEmpty ? "Empty" : "Holding", "SharedContainer", heldAfter.IsEmpty ? held.Type : heldAfter.Type, heldAfter.IsEmpty ? held.Amount : heldAfter.Amount, request.RequestId.ToString());
        log?.LogInfo($"[HELD] held-to-container accepted: peer {peer}, {held.Type} x{held.Amount} → {id.Value:X8}:{payload.SlotIndex} result={resultKind}。");
    }

    // P0-E/F：从玩家自己背包/快捷栏 grab 整槽到鼠标（Host HeldItems 权威）。原版 grab 拿走整个 slot。
    private void HandlePlayerGrabRequest(int peer, ActionRequestMessage request)
    {
        PlayerGrabPayload payload;
        try { payload = ReplicationProtocolCodec.DecodePlayerGrab(request.Payload); }
        catch (Exception error) { RejectAction(peer, request, "INVALID_PLAYER_GRAB", 0); log?.LogWarning($"PlayerGrab payload rejected from peer {peer}: {error.Message}"); return; }
        if (HeldItems.ContainsKey(peer)) { RejectAction(peer, request, "ALREADY_HOLDING", 0); return; }
        if (!Players.TryGetInventory(peer, out var shadow)) { RejectAction(peer, request, "PLAYER_INVENTORY_MISSING", 0); return; }
        // P0-A：拓扑强门（playerInv/hotbar grab 需要真实槽拓扑）。
        if (!shadow.TopologyReady) { RejectAction(peer, request, "PLAYER_INVENTORY_NOT_READY", 0); return; }
        if (!shadow.TryPeek(payload.FromHotbar, payload.SlotIndex, -1, out var source)) { RejectAction(peer, request, "PLAYER_SLOT_EMPTY", 0); return; }
        var held = new HeldItemStatePayload(source.Type, source.Amount, source.Durability, source.Quality, source.Recipe);
        // 事务：shadow 整槽清 → HeldItems（先清槽后登记；失败回滚风险低——TryPeek 已确认金额充足）。
        if (!shadow.Remove(payload.FromHotbar, payload.SlotIndex, source.Amount)) { RejectAction(peer, request, "SLOT_MUTATION_FAILED", 0); return; }
        HeldItems[peer] = held;
        var ack = new ActionResultMessage(request.RequestId, request.Kind, 0, false, 0, ReplicationProtocolCodec.Encode(held));
        cachedActionResults[request.RequestId] = ack; cachedActionOwners[request.RequestId] = peer;
        Queue(peer, ProtocolMessageType.ActionResult, ReplicationProtocolCodec.Encode(ack));
        Queue(peer, ProtocolMessageType.PlayerInventoryState, ReplicationProtocolCodec.Encode(shadow.CaptureState(peer)));
        LogHeldState(peer, "Empty", "Holding", "PlayerInventory", held.Type, held.Amount, request.RequestId.ToString());
        log?.LogInfo($"[HELD] player-grab accepted: peer {peer}, source={(payload.FromHotbar ? "hotbar" : "playerInv")}:{payload.SlotIndex}, {held.Type} x{held.Amount} → HeldItem。");
    }

    private void HandleContainerPutRequest(int peer,ActionRequestMessage request)
    {
        ContainerPutPayload payload;
        try{payload=ReplicationProtocolCodec.DecodeContainerPut(request.Payload);}
        catch(Exception error){RejectAction(peer,request,"INVALID_PUT_PAYLOAD",0);log?.LogWarning($"ContainerPut payload rejected from peer {peer}: {error.Message}");return;}
        var id=new EntityId(request.TargetValue,request.TargetPersistent);
        if(!replication.TryGetInventory(id,out var container)){RejectAction(peer,request,"CONTAINER_NOT_FOUND",0);return;}
        if(!DarkwoodEntityStateAdapter.IsShared(container)){RejectAction(peer,request,"NOT_SHARED_CONTAINER",0);return;}
        if(!Players.TryGetInventory(peer,out var shadow)){RejectAction(peer,request,"PLAYER_INVENTORY_MISSING",0);return;}
        if(!shadow.TryPeek(payload.Hotbar,payload.SlotIndex,payload.Amount,out var source)){RejectAction(peer,request,"PLAYER_SLOT_EMPTY",0);return;}
        if(!shadow.Remove(payload.Hotbar,payload.SlotIndex,payload.Amount)){RejectAction(peer,request,"INSUFFICIENT_AMOUNT",0);return;}
        var item=new InvItemClass(source.Type,source.Durability,source.Amount,(InvItem.ModifierQuality)source.Quality,source.Recipe);
        // 放入容器目标槽（同类堆叠，否则覆盖空槽）
        var targetSlot=payload.DestinationSlotIndex;
        if(targetSlot<0||targetSlot>=container.slots.Count){targetSlot=0;}
        var dest=container.slots[targetSlot];
        if(!InvItemClass.isNull(dest.invItem)&&string.Equals(dest.invItem.type,item.type,StringComparison.Ordinal)&&dest.invItem.baseClass!=null&&dest.invItem.baseClass.stackable)
        {
            dest.invItem.amount+=item.amount;dest.invItem.refresh();
        }
        else
        {
            dest.inventory=container;
            dest.createItem(item);
        }
        container.refreshItems();
        var state=replication.CaptureAuthoritativeInventory(id);
        BroadcastInventory(state);
        var result=new ActionResultMessage(request.RequestId,request.Kind,id.Value,id.IsPersistent,state.Revision,ReplicationProtocolCodec.Encode(shadow.CaptureState(peer)));
        RemoveEvictedAction(actionCache.Store(new NetworkActionResult(request.RequestId,true,new StateVersion(state.Revision),string.Empty)));acceptedActions++;
        cachedActionResults[request.RequestId]=result;cachedActionOwners[request.RequestId]=peer;
        Queue(peer,ProtocolMessageType.ActionResult,ReplicationProtocolCodec.Encode(result));
        log?.LogInfo($"ContainerPut accepted {request.RequestId}: peer {peer}, {item.type} x{payload.Amount}, container {id}, slot {targetSlot}.");
    }

    private void HandleDropRequest(int peer,ActionRequestMessage request)
    {
        DropItemPayload payload;
        try{payload=ReplicationProtocolCodec.DecodeDropItem(request.Payload);}
        catch(Exception error){RejectAction(peer,request,"INVALID_DROP_PAYLOAD",0);log?.LogWarning($"Drop payload rejected from peer {peer}: {error.Message}");return;}
        var result=World.DropItem(peer,payload,request,(p,req,code,rev)=>RejectAction(p,req,code,rev));
        if(result==null)return;
        RemoveEvictedAction(actionCache.Store(new NetworkActionResult(request.RequestId,true,new StateVersion(result.Value.Revision),string.Empty)));acceptedActions++;
        cachedActionResults[request.RequestId]=result.Value;cachedActionOwners[request.RequestId]=peer;
        Queue(peer,ProtocolMessageType.ActionResult,ReplicationProtocolCodec.Encode(result.Value));
    }

    private void HandleAttackRequest(int peer,ActionRequestMessage request)
    {
        AttackPayload attack;
        try{attack=ReplicationProtocolCodec.DecodeAttack(request.Payload);}
        catch(Exception error){RejectAction(peer,request,"INVALID_ATTACK_PAYLOAD",0);log?.LogWarning($"Attack payload rejected from peer {peer}: {error.Message}");return;}
        if(!Players.TryGetRemotePosition(peer,out var pose)){RejectAction(peer,request,"PLAYER_POSE_MISSING",0);return;}
        if(!Combat.TryConsumeAttack(peer,AttackCooldownSeconds)){RejectAction(peer,request,"RATE_LIMITED",0);return;}
        // FIX-011：信任模型——不再校验攻击位置与追踪姿势的距离；目标仍按客户端报告的方向解析。
        if(!Players.TryGetInventory(peer,out var shadow)){RejectAction(peer,request,"PLAYER_INVENTORY_MISSING",0);return;}
        if(!shadow.TryPeek(attack.FromHotbar,attack.SlotIndex,-1,out var weapon)){RejectAction(peer,request,"PLAYER_SLOT_EMPTY",0);return;}
        // Damage is derived from the HOST's game data for the shadow weapon type; the client never sends damage values.
        InvItemClass weaponClass;
        try{weaponClass=new InvItemClass(weapon.Type,weapon.Durability,weapon.Amount,(InvItem.ModifierQuality)weapon.Quality,weapon.Recipe);}
        catch(Exception){RejectAction(peer,request,"UNKNOWN_ITEM_TYPE",0);return;}
        if(weaponClass==null||weaponClass.baseClass==null||!weaponClass.baseClass.isMelee){RejectAction(peer,request,"NOT_MELEE",0);return;}
        var special=attack.AttackKind==2;
        var damage=weaponClass.getModdedDamage(special?weaponClass.baseClass.specialDamage:weaponClass.baseClass.damage);
        var barricadeDamage=weaponClass.getModdedDamage(special?weaponClass.baseClass.specialBarricadeDamage:weaponClass.baseClass.barricadeDamage);
        var durabilityDrain=weaponClass.getModdedDurabilityDrain(special?weaponClass.baseClass.specialDamageDurabilityDrain:weaponClass.baseClass.damageDurabilityDrain);
        if(damage<=0&&barricadeDamage<=0){RejectAction(peer,request,"NO_DAMAGE",0);return;}
        var dir=new Vector3(attack.DirX,0f,attack.DirZ);
        if(dir.sqrMagnitude<0.0001f)dir=Vector3.forward;else dir.Normalize();
        var target=Combat.ResolveMeleeTarget(pose,dir);
        if(target!=null)Combat.ApplyMeleeDamage(target,Combat.GetAttackAnchor(peer,pose).transform,damage,barricadeDamage,weaponClass.baseClass.canCutInHalf);
        if(durabilityDrain>0)shadow.DrainDurability(attack.FromHotbar,attack.SlotIndex,durabilityDrain);
        ulong resultValue=0;var resultPersistent=false;
        if(target!=null&&replication.TryGetId(target,out var hitId)){resultValue=hitId.Value;resultPersistent=hitId.IsPersistent;}
        var result=new ActionResultMessage(request.RequestId,request.Kind,resultValue,resultPersistent,0,ReplicationProtocolCodec.Encode(shadow.CaptureState(peer)));
        RemoveEvictedAction(actionCache.Store(new NetworkActionResult(request.RequestId,true,new StateVersion(0),string.Empty)));acceptedActions++;
        cachedActionResults[request.RequestId]=result;cachedActionOwners[request.RequestId]=peer;
        Queue(peer,ProtocolMessageType.ActionResult,ReplicationProtocolCodec.Encode(result));
        if(target!=null){serverTick++;BroadcastEntityDelta(target);}
        log?.LogInfo($"主机已批准攻击 {request.RequestId}：玩家 {peer}，{(special?"特殊":"普通")}近战 {weapon.Type}，目标 {(target!=null?target.GetType().Name:"无")}，伤害 {damage}，消耗耐久 {durabilityDrain}。");
    }

    private void HandleDoorInteractRequest(int peer,ActionRequestMessage request)
    {
        var id=new EntityId(request.TargetValue,request.TargetPersistent);
        if(!replication.TryGetComponent(id,out var component)||!(component is Door door)){RejectAction(peer,request,"DOOR_NOT_FOUND",0);return;}
        if(!Players.TryGetRemotePosition(peer,out var pose)){RejectAction(peer,request,"PLAYER_POSE_MISSING",0);return;}
        // FIX-011：信任模型——距离/版本/封板判断全部移除，客户端本地已执行，主机直接执行并广播。
        door.openClose(Combat.GetAttackAnchor(peer,pose).transform);
        AcceptInteract(peer,request,id,door,0);
        log?.LogInfo($"主机已批准开关门 {request.RequestId}：玩家 {peer}，门 {id}。");
    }

    private void HandleWindowInteractRequest(int peer,ActionRequestMessage request)
    {
        var id=new EntityId(request.TargetValue,request.TargetPersistent);
        if(!replication.TryGetComponent(id,out var component)||!(component is Window window)){RejectAction(peer,request,"WINDOW_NOT_FOUND",0);return;}
        // FIX-011：信任模型——距离/版本判断移除，客户端本地已执行，主机直接应用并广播。
        var interact=ReplicationProtocolCodec.DecodeInteract(request.Payload);
        window.barricade(interact.ValueA,true);
        AcceptInteract(peer,request,id,window,0);
        log?.LogInfo($"主机已应用封窗 {request.RequestId}：玩家 {peer}，窗 {id}，目标耐久 {interact.ValueA}。");
    }

    private void HandleItemActivateRequest(int peer,ActionRequestMessage request)
    {
        var id=new EntityId(request.TargetValue,request.TargetPersistent);
        if(!replication.TryGetComponent(id,out var component)||!(component is Item item)){
            var itemCount=0;foreach(var pair in replication.Entities())if(pair.Value is Item)itemCount++;
            log?.LogWarning($"ITEM_NOT_FOUND：请求 {id.Value:X16}:{(id.IsPersistent?1:0)}，主机注册表 {registry?.Count ?? 0} 实体（其中 Item {itemCount} 个），kind={request.Kind}，revision={request.ExpectedRevision}。");
            RejectAction(peer,request,"ITEM_NOT_FOUND",0);return;
        }
        // FIX-011：信任模型——客户端本地已执行 activate() 并报告 isOn 结果状态；
        // 主机直接应用该状态（不调用 activate()，避免在主机弹出容器 UI）并广播。
        var interact=ReplicationProtocolCodec.DecodeInteract(request.Payload);
        item.isOn = interact.ValueA != 0;
        AcceptInteract(peer,request,id,item,0);
        log?.LogInfo($"主机已应用物品开关 {request.RequestId}：玩家 {peer}，物品 {id}，isOn={item.isOn}。");
    }

    private void AcceptInteract(int peer,ActionRequestMessage request,EntityId id,Component target,ulong revision)
    {
        var result=new ActionResultMessage(request.RequestId,request.Kind,id.Value,id.IsPersistent,revision,Array.Empty<byte>());
        RemoveEvictedAction(actionCache.Store(new NetworkActionResult(request.RequestId,true,new StateVersion(revision),string.Empty)));acceptedActions++;
        cachedActionResults[request.RequestId]=result;cachedActionOwners[request.RequestId]=peer;
        Queue(peer,ProtocolMessageType.ActionResult,ReplicationProtocolCodec.Encode(result));
        serverTick++;BroadcastEntityDelta(target);
    }

    private void SendEntityState(int peer,Component target)
    {
        var states=replication.CaptureEntities(new[]{target});
        if(states.Length==0)return;
        var delta=ReplicationProtocolCodec.Encode(new EntityDeltaMessage(CurrentScene,serverTick,states,Array.Empty<EntityStateWire>()));
        Queue(peer,ProtocolMessageType.EntityDelta,delta);
    }
}

