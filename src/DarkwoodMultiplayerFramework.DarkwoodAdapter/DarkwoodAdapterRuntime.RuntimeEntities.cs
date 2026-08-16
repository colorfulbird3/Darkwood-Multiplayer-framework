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
    private void BroadcastEntityDelta(Component target)
    {
        var states=replication.CaptureEntities(new[]{target});
        if(states.Length==0)return;
        var delta=ReplicationProtocolCodec.Encode(new EntityDeltaMessage(CurrentScene,serverTick,states,Array.Empty<EntityStateWire>()));
        foreach(var readyPeer in readyPeers.ToArray())Queue(readyPeer,ProtocolMessageType.EntityDelta,delta);
    }

    private static bool TryAddToContainer(Inventory inventory,DarkwoodPlayerInventoryShadow.Item source,int destinationSlotIndex)
    {
        if(inventory?.slots==null||string.IsNullOrEmpty(source.Type)||source.Amount<=0)return false;
        if(destinationSlotIndex>=0)
        {
            if(destinationSlotIndex>=inventory.slots.Count)return false;
            var destination=inventory.slots[destinationSlotIndex];if(destination==null)return false;
            var item=destination.invItem;
            if(item==null||InvItemClass.isNull(item))
            {
                if(source.Stackable&&source.Amount>Math.Max(1,source.MaxAmount))return false;
                destination.createItem(source.Type,source.Amount,source.Durability,(InvItem.ModifierQuality)source.Quality,source.Recipe);return true;
            }
            if(!source.Stackable||item.type!=source.Type||item.baseClass==null||!item.baseClass.stackable)return false;
            var capacity=Math.Max(0,Math.Max(1,item.baseClass.maxAmount)-item.amount);if(capacity<source.Amount)return false;
            var incomingExact=new InvItemClass(source.Type,source.Durability,source.Amount,(InvItem.ModifierQuality)source.Quality,source.Recipe);
            item.durability=InvItemClass.getStackedDurability(item,incomingExact,source.Amount);item.amount+=source.Amount;item.refresh();return true;
        }
        var remaining=source.Amount;
        if(source.Stackable)
        {
            foreach(var slot in inventory.slots)
            {
                var item=slot?.invItem;
                if(item==null||InvItemClass.isNull(item)||item.type!=source.Type||item.baseClass==null||!item.baseClass.stackable)continue;
                remaining-=Math.Max(0,Math.Max(1,item.baseClass.maxAmount)-item.amount);
                if(remaining<=0)break;
            }
        }
        if(remaining>0)
        {
            var empty=0;foreach(var slot in inventory.slots)if(slot!=null&&InvItemClass.isNull(slot.invItem))empty++;
            var perSlot=source.Stackable?Math.Max(1,source.MaxAmount):source.Amount;
            if((long)empty*perSlot<remaining)return false;
        }

        remaining=source.Amount;
        var incoming=new InvItemClass(source.Type,source.Durability,source.Amount,(InvItem.ModifierQuality)source.Quality,source.Recipe);
        if(source.Stackable)
        {
            foreach(var slot in inventory.slots)
            {
                var item=slot?.invItem;
                if(remaining<=0)break;
                if(item==null||InvItemClass.isNull(item)||item.type!=source.Type||item.baseClass==null||!item.baseClass.stackable)continue;
                var amount=Math.Min(remaining,Math.Max(0,Math.Max(1,item.baseClass.maxAmount)-item.amount));
                if(amount<=0)continue;
                item.durability=InvItemClass.getStackedDurability(item,incoming,amount);item.amount+=amount;item.refresh();remaining-=amount;
            }
        }
        foreach(var slot in inventory.slots)
        {
            if(remaining<=0)break;
            if(slot==null||!InvItemClass.isNull(slot.invItem))continue;
            var amount=source.Stackable?Math.Min(remaining,Math.Max(1,source.MaxAmount)):remaining;
            slot.createItem(source.Type,amount,source.Durability,(InvItem.ModifierQuality)source.Quality,source.Recipe);remaining-=amount;
        }
        return remaining==0;
    }

    private void RejectAction(int peer,ActionRequestMessage request,string error,ulong revision)
    {
        var result=new NetworkActionResult(request.RequestId,false,new StateVersion(revision),error);RemoveEvictedAction(actionCache.Store(result));rejectedActions++;
        var rejected=new ActionRejectedMessage(request.RequestId,request.Kind,request.TargetValue,request.TargetPersistent,revision,error);cachedActionRejections[request.RequestId]=rejected;
        cachedActionOwners[request.RequestId]=peer;
        Queue(peer,ProtocolMessageType.ActionRejected,ReplicationProtocolCodec.Encode(rejected));
        log?.LogWarning($"Action rejected {request.RequestId}: peer {peer}, kind {request.Kind}, {error}, revision {revision}.");
    }

    private void SendCachedAction(int peer,ActionRequestMessage request,NetworkActionResult cached)
    {
        if(cached.Accepted && cachedActionResults.TryGetValue(request.RequestId,out var accepted))Queue(peer,ProtocolMessageType.ActionResult,ReplicationProtocolCodec.Encode(accepted));
        else if(!cached.Accepted && cachedActionRejections.TryGetValue(request.RequestId,out var rejected))Queue(peer,ProtocolMessageType.ActionRejected,ReplicationProtocolCodec.Encode(rejected));
        else Queue(peer,ProtocolMessageType.ActionRejected,ReplicationProtocolCodec.Encode(new ActionRejectedMessage(request.RequestId,request.Kind,request.TargetValue,request.TargetPersistent,cached.Version.Value,cached.Accepted?"CACHED_RESPONSE_MISSING":cached.ErrorCode)));
    }

    private void RemoveEvictedAction(Guid requestId)
    {
        if(requestId==Guid.Empty)return;
        cachedActionResults.Remove(requestId);
        cachedActionRejections.Remove(requestId);
        cachedActionOwners.Remove(requestId);
    }

    private void HandleActionResult(ActionResultMessage result)
    {
        if(!pendingActions.Remove(result.RequestId))return;
        if(result.Payload.Length>0)ApplyPlayerInventory(ReplicationProtocolCodec.DecodePlayerInventoryState(result.Payload));
        log?.LogInfo($"已应用主机权威操作结果：请求 {result.RequestId}，类型 {result.Kind}，目标 {result.TargetValue:X16}，版本 {result.Revision}。");
    }

    internal static void ApplyPlayerInventory(PlayerInventoryStatePayload state)
    {
        var player=Player.Instance;if(player?.Inventory==null||player.Hotbar==null)throw new InvalidOperationException("客户端玩家库存不可用。");
        DarkwoodInventoryAdapter.Apply(player.Inventory,ToDarkwoodSlots(state.Backpack));
        DarkwoodInventoryAdapter.Apply(player.Hotbar,ToDarkwoodSlots(state.Hotbar));
        player.refreshRecipes();
    }

    private void ApplyGuestProfile(GuestProfileMessage profile)
    {
        var player=Player.Instance;if(player==null)throw new InvalidOperationException("客户端玩家尚未就绪。");
        player.transform.position=new Vector3(profile.X,profile.Y,profile.Z);
        foreach(var body in player.GetComponentsInChildren<Rigidbody>(true)){body.velocity=Vector3.zero;body.angularVelocity=Vector3.zero;}
        ApplyPlayerInventory(profile.Inventory);
        if(!profile.Downed&&profile.Health>0f)player.setHealth(profile.Health);
        log?.LogInfo($"已应用访客档案：出生点 ({profile.X:F1},{profile.Y:F1},{profile.Z:F1})，第 {profile.Day} 天，第 {profile.JoinCount} 次加入。");
    }

    public bool ApplyPlayerName(string name,out string error)
    {
        error=string.Empty;name=(name??string.Empty).Trim();
        if(name.Length==0){error="玩家名称不能为空。";return false;}
        if(System.Text.Encoding.UTF8.GetByteCount(name)>64){error="玩家名称过长（最多 64 字节）。";return false;}
        if(playerNameConfig==null){error="配置尚未就绪。";return false;}
        playerNameConfig.Value=name;playerNameConfig.ConfigFile.Save();
        log?.LogInfo($"玩家名称已保存：{name}。");
        return true;
    }

    public string ConfiguredPlayerName => playerNameConfig?.Value ?? string.Empty;

    // ---------- 倒地 / 营救（DOWNED-001） ----------

    /// <summary>Called by DarkwoodDownedPatch when the LOCAL player dies while other players are alive.</summary>
}

