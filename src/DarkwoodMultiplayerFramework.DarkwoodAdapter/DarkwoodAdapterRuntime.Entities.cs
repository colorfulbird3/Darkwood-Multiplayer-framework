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
            default: RejectAction(peer,request,"UNSUPPORTED_ACTION",0);return;
        }
    }

    private void HandlePickupRequest(int peer,ActionRequestMessage request)
    {
        var id=new EntityId(request.TargetValue,request.TargetPersistent);
        if(!replication.TryGetComponent(id,out var component)||!(component is Item item)){RejectAction(peer,request,"ENTITY_NOT_FOUND",0);return;}
        if(!replication.TryGetState(id,out var state)){RejectAction(peer,request,"ENTITY_STATE_MISSING",0);return;}
        if(!item.gameObject.activeSelf||item.destroyed||!item.isDroppedItem){RejectAction(peer,request,"NOT_PICKABLE",state.Revision);return;}
        var droppedInventory=DarkwoodDroppedItemAccessor.GetInventory(item);
        if(droppedInventory==null||droppedInventory.slots==null||droppedInventory.slots.Count==0||InvItemClass.isNull(droppedInventory.slots[0].invItem)){RejectAction(peer,request,"ITEM_EMPTY",state.Revision);return;}
        if(!Players.TryGetInventory(peer,out var shadow)){RejectAction(peer,request,"PLAYER_INVENTORY_MISSING",state.Revision);return;}
        var source=droppedInventory.slots[0].invItem;var pickup=new PickupResultPayload(source.type,source.amount,source.durability,(int)source.modifierQuality,source.isRecipe);
        if(!shadow.CanAdd(source)){RejectAction(peer,request,"INVENTORY_FULL",state.Revision);return;}
        // The remote player's inventory is represented by a host-side shadow until
        // the Inventory Transaction protocol is introduced. Never mutate Host's
        // local Player inventory while applying a remote request.
        shadow.Add(source);
        droppedInventory.slots[0].removeItem();
        droppedInventory.refreshItems();
        var despawn=replication.MarkDespawned(id);serverTick++;
        var result=new ActionResultMessage(request.RequestId,request.Kind,id.Value,id.IsPersistent,despawn.Revision,ReplicationProtocolCodec.Encode(shadow.CaptureState()));
        RemoveEvictedAction(actionCache.Store(new NetworkActionResult(request.RequestId,true,new StateVersion(despawn.Revision),string.Empty)));acceptedActions++;
        cachedActionResults[request.RequestId]=result;
        cachedActionOwners[request.RequestId]=peer;
        Queue(peer,ProtocolMessageType.ActionResult,ReplicationProtocolCodec.Encode(result));
        var delta=ReplicationProtocolCodec.Encode(new EntityDeltaMessage(CurrentScene,serverTick,Array.Empty<EntityStateWire>(),new[]{despawn}));foreach(var readyPeer in readyPeers.ToArray())Queue(readyPeer,ProtocolMessageType.EntityDelta,delta);
        log?.LogInfo($"Pickup accepted {request.RequestId}: peer {peer}, {pickup.ItemType} x{pickup.Amount}, target {id}, revision {despawn.Revision}.");
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
        if(!replication.TryGetComponent(id,out var component)||!(component is Item item)){RejectAction(peer,request,"ITEM_NOT_FOUND",0);return;}
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

