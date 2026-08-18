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
    public bool TryRequestContainerTake(InvSlot slot)
    {
        if(clientSession?.Session.Lifecycle.State!=ConnectionState.Ready||slot==null||InvItemClass.isNull(slot.invItem))return false;
        if(!TryGetEntityId(slot.inventory,out var containerId))return false;
        var slotIndex=slot.inventory!=null?slot.inventory.slots.IndexOf(slot):-1;
        if(slotIndex<0)return false;
        var payload=new ContainerTakePayload(slotIndex,Math.Max(1,slot.itemAmount));
        var request=new ActionRequestMessage(Guid.NewGuid(),clientSession.PeerId,ActionKindWire.ContainerTake,containerId.Value,containerId.IsPersistent,0,ReplicationProtocolCodec.Encode(payload));
        pendingActions[request.RequestId]=request;
        clientSession.Send(ProtocolMessageType.ActionRequest,ReplicationProtocolCodec.Encode(request));
        log?.LogInfo($"ContainerTake request {request.RequestId} sent: container {containerId}, slot {slotIndex}, amount {payload.Amount}.");
        return true;
    }

    public bool TryRequestContainerPut(InvSlot slot,Inventory targetContainer,int targetSlot)
    {
        if(clientSession?.Session.Lifecycle.State!=ConnectionState.Ready||slot==null||InvItemClass.isNull(slot.invItem))return false;
        if(!TryGetEntityId(targetContainer,out var containerId))return false;
        var fromHotbar=slot.inventory!=null&&slot.inventory.invType==Inventory.InvType.hotbar;
        var sourceSlot=slot.inventory!=null?slot.inventory.slots.IndexOf(slot):-1;
        if(sourceSlot<0)return false;
        var payload=new ContainerPutPayload(fromHotbar,sourceSlot,targetSlot,Math.Max(1,slot.itemAmount));
        var request=new ActionRequestMessage(Guid.NewGuid(),clientSession.PeerId,ActionKindWire.ContainerPut,containerId.Value,containerId.IsPersistent,0,ReplicationProtocolCodec.Encode(payload));
        pendingActions[request.RequestId]=request;
        clientSession.Send(ProtocolMessageType.ActionRequest,ReplicationProtocolCodec.Encode(request));
        log?.LogInfo($"ContainerPut request {request.RequestId} sent: container {containerId}, slot {sourceSlot}->{targetSlot}, amount {payload.Amount}.");
        return true;
    }

    public bool TryRequestDrop(InvItemClass item)
    {
        if(clientSession?.Session.Lifecycle.State!=ConnectionState.Ready||InvItemClass.isNull(item))return false;
        var payload=DarkwoodDropPatch.BuildPayload(item);
        if(payload.SlotIndex<0)return false;
        var request=new ActionRequestMessage(Guid.NewGuid(),clientSession.PeerId,ActionKindWire.DropItem,0,false,0,ReplicationProtocolCodec.Encode(payload));
        pendingActions[request.RequestId]=request;
        clientSession.Send(ProtocolMessageType.ActionRequest,ReplicationProtocolCodec.Encode(request));
        log?.LogInfo($"Drop request {request.RequestId} sent: hotbar={payload.FromHotbar}, slot={payload.SlotIndex}, amount={payload.Amount}.");
        return true;
    }

    public bool TryRequestItemActivate(Item item)
    {
        if(clientSession?.Session.Lifecycle.State!=ConnectionState.Ready||item==null)return false;
        if(!replication.TryGetId(item,out var id)){log?.LogWarning("Item activate was not sent because the item has no registered EntityId.");return false;}
        ulong expectedRevision=0;
        if(replication.TryGetState(id,out var state))expectedRevision=state.Revision;
        // FIX-011：报告本地执行后的 isOn 状态，主机直接应用（信任模型），不调用 activate()。
        var request=new ActionRequestMessage(Guid.NewGuid(),clientSession.PeerId,ActionKindWire.ItemActivate,id.Value,id.IsPersistent,expectedRevision,ReplicationProtocolCodec.Encode(new InteractPayload(item.isOn?1:0)));
        pendingActions[request.RequestId]=request;
        clientSession.Send(ProtocolMessageType.ActionRequest,ReplicationProtocolCodec.Encode(request));
        log?.LogInfo($"Item activate request {request.RequestId} sent for {id}, isOn {(item.isOn?1:0)}, revision {expectedRevision}.");
        return true;
    }

    public void NotifyHostContainerChanged(Inventory inventory)
    {
        if(hostSession==null||readyPeers.Count==0||inventory==null||replication.ApplyingRemote)return;
        if(!replication.TryGetId(inventory,out var id))return;
        try{BroadcastInventory(replication.CaptureAuthoritativeInventory(id));}
        catch(Exception error){log?.LogWarning($"Failed to publish host container mutation for {id}: {error.Message}");}
    }

    /// <summary>游戏默认出生点（playerBase 的 playerSpawn，与单机新游戏出生一致）。取不到时回退主机玩家位置。</summary>
    internal Vector3 DefaultSpawnPoint()
    {
        try
        {
            var worldGen=Singleton<WorldGenerator>.Instance;
            if(worldGen!=null&&worldGen.playerBase!=null)
            {
                var location=worldGen.playerBase.GetComponent<Location>();
                if(location!=null&&location.playerSpawn!=null)return location.playerSpawn.transform.position;
            }
        }
        catch(Exception error){log?.LogWarning($"读取默认出生点失败：{error.Message}");}
        var player=Player.Instance;
        return player!=null?player.transform.position:Vector3.zero;
    }

    /// <summary>客户端实例化运行时敌人代理。AI 冻结（远端代理），注册进 entities 以接收 15Hz delta（位置/血量/动画/死亡）。</summary>
}

