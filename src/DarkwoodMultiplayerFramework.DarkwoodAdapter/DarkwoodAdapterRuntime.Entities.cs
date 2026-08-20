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
            case ActionKindWire.ItemInteract: HandleItemInteractRequest(peer,request);return;
            default: RejectAction(peer,request,"UNSUPPORTED_ACTION",0);return;
        }
    }

    private void HandlePickupRequest(int peer,ActionRequestMessage request)
    {
        var id=new EntityId(request.TargetValue,request.TargetPersistent);
        if(!replication.TryGetItem(id,out var item)){RejectAction(peer,request,"ENTITY_NOT_FOUND",0);return;}
        if(!replication.TryGetState(id,out var state)){RejectAction(peer,request,"ENTITY_STATE_MISSING",0);return;}
        if(!item.gameObject.activeSelf||item.destroyed||!item.isDroppedItem){RejectAction(peer,request,"NOT_PICKABLE",state.Revision);return;}
        var droppedInventory=DarkwoodDroppedItemAccessor.GetInventory(item);
        if(droppedInventory==null||droppedInventory.slots==null||droppedInventory.slots.Count==0||InvItemClass.isNull(droppedInventory.slots[0].invItem)){RejectAction(peer,request,"ITEM_EMPTY",state.Revision);return;}
        if(!Players.TryGetInventory(peer,out var shadow)){RejectAction(peer,request,"PLAYER_INVENTORY_MISSING",state.Revision);return;}
        var source=droppedInventory.slots[0].invItem;var pickup=new PickupResultPayload(source.type,source.amount,source.durability,(int)source.modifierQuality,source.isRecipe);
        // P0-H：World Pickup → Host HeldItems（鼠标吸附 cursor），不再直接 shadow.Add 进背包。
        // 只有用户明确放回背包（HeldToInventory）才进背包——与 Darkwood 原版 cursor 拿取一致。
        if(HeldItems.ContainsKey(peer)){RejectAction(peer,request,"ALREADY_HOLDING",state.Revision);return;}
        var held=new HeldItemStatePayload(source.type,source.amount,source.durability,(int)source.modifierQuality,source.isRecipe,source.ammo);
        // The remote player's inventory is represented by a host-side shadow until
        // the Inventory Transaction protocol is introduced. Never mutate Host's
        // local Player inventory while applying a remote request.
        // P0-A：成功事务 → 分生命周期。
        //  - Runtime 实体（dropped item，Persistent=false）：走 RuntimeEntityDespawn 专用生命周期，
        //    绝不能只发普通 EntityDelta.Despawn（否则客户端 mirror/registry 不清理，留下 ghost 包袱）。
        //  - Persistent 实体：继续 EntityDelta.Despawns。
        ActionResultMessage result = default;
        HeldItems[peer]=held;
        LogHeldState(peer,"Empty","Holding","WorldDroppedItem",held.Type,held.Amount,request.RequestId.ToString());
        if (!id.IsPersistent && RuntimeEntities != null)
        {
            droppedInventory.slots[0].removeItem();
            droppedInventory.refreshItems();
            ulong rev=0; if (replication.TryGetState(id, out var st)) rev = st.Revision;
            // P0 五：先给发起 Client 发 ack（其本地 mirror 仍在 → Replay grabItem 挂 cursor），
            // 后广播 RuntimeEntityDespawn（销毁 mirror 不连带 cursor 图标——Replay 已把 UI 挂到 UI 根）。
            result=new ActionResultMessage(request.RequestId,request.Kind,id.Value,id.IsPersistent,rev+1,ReplicationProtocolCodec.Encode(held));
            RemoveEvictedAction(actionCache.Store(new NetworkActionResult(request.RequestId,true,new StateVersion(rev+1),string.Empty)));acceptedActions++;
            cachedActionResults[request.RequestId]=result;
            cachedActionOwners[request.RequestId]=peer;
            Queue(peer,ProtocolMessageType.ActionResult,ReplicationProtocolCodec.Encode(result)); // ★ 先 ack
            serverTick++;
            RuntimeEntities.BroadcastDespawn(id.Value, RuntimeEntityDespawnReason.Collected); // ★ 后 despawn
            replication.UnregisterRuntimeEntity(id);
            if (item != null) try { UnityEngine.Object.Destroy(item.gameObject); } catch (Exception) { }
            log?.LogInfo($"[RUNTIME] pickup id={id} peer={peer} {pickup.ItemType} x{pickup.Amount} → HeldItem+RuntimeEntityDespawn(PickedUp)+unregister，包袱销毁。");
            return;
        }
        droppedInventory.slots[0].removeItem();
        droppedInventory.refreshItems();
        var despawn=replication.MarkDespawned(id);serverTick++;
        result=new ActionResultMessage(request.RequestId,request.Kind,id.Value,id.IsPersistent,despawn.Revision,ReplicationProtocolCodec.Encode(held));
        RemoveEvictedAction(actionCache.Store(new NetworkActionResult(request.RequestId,true,new StateVersion(despawn.Revision),string.Empty)));acceptedActions++;
        cachedActionResults[request.RequestId]=result;
        cachedActionOwners[request.RequestId]=peer;
        Queue(peer,ProtocolMessageType.ActionResult,ReplicationProtocolCodec.Encode(result));
        var delta=ReplicationProtocolCodec.Encode(new EntityDeltaMessage(CurrentScene,serverTick,Array.Empty<EntityStateWire>(),new[]{despawn}));foreach(var readyPeer in readyPeers.ToArray())Queue(readyPeer,ProtocolMessageType.EntityDelta,delta);
        log?.LogInfo($"[RUNTIME] pickup id={id} peer={peer} {pickup.ItemType} x{pickup.Amount} → HeldItem（persistent despawn）。");
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
        var result=new ActionResultMessage(request.RequestId,request.Kind,id.Value,id.IsPersistent,state.Revision,ReplicationProtocolCodec.Encode(shadow.CaptureState()));
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
        var item=new InvItemClass(held.Type,held.Durability,held.Amount,(InvItem.ModifierQuality)held.Quality,held.Recipe);
        var place=shadow.PlaceAt(payload.FromHotbar,payload.TargetSlot,item);
        if(place!=DarkwoodPlayerInventoryShadow.HeldPlaceResult.Placed&&place!=DarkwoodPlayerInventoryShadow.HeldPlaceResult.Stacked)
        { RejectAction(peer,request,place==DarkwoodPlayerInventoryShadow.HeldPlaceResult.Occupied?"SLOT_OCCUPIED":"INVALID_TARGET_SLOT",0);
          log?.LogInfo($"[HELD] place-rejected peer={peer} {held.Type} x{held.Amount} 目标={(payload.FromHotbar?"hotbar":"playerInv")}:{payload.TargetSlot} result={place}。"); return; }
        HeldItems.Remove(peer);
        var result=new ActionResultMessage(request.RequestId,request.Kind,0,false,0,ReplicationProtocolCodec.Encode(shadow.CaptureState()));
        RemoveEvictedAction(actionCache.Store(new NetworkActionResult(request.RequestId,true,new StateVersion(0),string.Empty)));acceptedActions++;
        cachedActionResults[request.RequestId]=result;cachedActionOwners[request.RequestId]=peer;
        Queue(peer,ProtocolMessageType.ActionResult,ReplicationProtocolCodec.Encode(result));
        log?.LogInfo($"[HELD] place-accepted peer={peer} {held.Type} x{held.Amount} 目标={(payload.FromHotbar?"hotbar":"playerInv")}:{payload.TargetSlot} result={place}。");
    }

    // P0-J：Held 状态机显式 transition 日志（Host 权威）。
    private void LogHeldState(int peer,string oldState,string newState,string source,string item,int amount,string requestId)
        => log?.LogInfo($"[HELD-STATE] peer={peer} old={oldState} new={newState} source={source} item={item} amount={amount} requestId={requestId}");

    // P0-E/F：从玩家自己背包/快捷栏 grab 整槽到鼠标（Host HeldItems 权威）。原版 grab 拿走整个 slot。
    private void HandlePlayerGrabRequest(int peer,ActionRequestMessage request)
    {
        PlayerGrabPayload payload;
        try { payload = ReplicationProtocolCodec.DecodePlayerGrab(request.Payload); }
        catch (Exception error) { RejectAction(peer, request, "INVALID_PLAYER_GRAB", 0); log?.LogWarning($"PlayerGrab payload rejected from peer {peer}: {error.Message}"); return; }
        if (HeldItems.ContainsKey(peer)) { RejectAction(peer, request, "ALREADY_HOLDING", 0); return; }
        if (!Players.TryGetInventory(peer, out var shadow)) { RejectAction(peer, request, "PLAYER_INVENTORY_MISSING", 0); return; }
        if (!shadow.TryPeek(payload.FromHotbar, payload.SlotIndex, -1, out var source)) { RejectAction(peer, request, "PLAYER_SLOT_EMPTY", 0); return; }
        var held = new HeldItemStatePayload(source.Type, source.Amount, source.Durability, source.Quality, source.Recipe);
        // 事务：shadow 整槽清 → HeldItems（先清槽后登记；失败回滚风险低——TryPeek 已确认金额充足）。
        if (!shadow.Remove(payload.FromHotbar, payload.SlotIndex, source.Amount)) { RejectAction(peer, request, "SLOT_MUTATION_FAILED", 0); return; }
        HeldItems[peer] = held;
        var ack = new ActionResultMessage(request.RequestId, request.Kind, 0, false, 0, ReplicationProtocolCodec.Encode(held));
        cachedActionResults[request.RequestId] = ack; cachedActionOwners[request.RequestId] = peer;
        Queue(peer, ProtocolMessageType.ActionResult, ReplicationProtocolCodec.Encode(ack));
        Queue(peer, ProtocolMessageType.PlayerInventoryState, ReplicationProtocolCodec.Encode(shadow.CaptureState()));
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
        var result=new ActionResultMessage(request.RequestId,request.Kind,id.Value,id.IsPersistent,state.Revision,ReplicationProtocolCodec.Encode(shadow.CaptureState()));
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
        var result=new ActionResultMessage(request.RequestId,request.Kind,resultValue,resultPersistent,0,ReplicationProtocolCodec.Encode(shadow.CaptureState()));
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

