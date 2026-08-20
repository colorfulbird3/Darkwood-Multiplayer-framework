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
        if(!pendingActions.TryGetValue(result.RequestId,out var req))return;
        pendingActions.Remove(result.RequestId);
        pendingGrabSnapshots.Remove(result.RequestId); // 旧“快照重建 UI”路径废弃：改用原版 Replay。
        // P0-I：Host Accepted → 客户端在 AuthorityReplayScope 内复演 Darkwood 原版交互方法，再由权威快照 reconcile。
        switch(result.Kind)
        {
            case ActionKindWire.ContainerGrab: TryReplayContainerGrab(req,result); break;
            case ActionKindWire.PlayerGrab: TryReplayPlayerGrab(req,result); break;
            case ActionKindWire.Pickup: ReplayPickedUpWorldItem(req,result); break;
            case ActionKindWire.HeldToInventory: ReplayPlaceToDestination(req,result); break;
            case ActionKindWire.DropItem: ReplayDropCursorCleanup(); break;
            default:
                if(result.Payload.Length>0) try { ApplyPlayerInventory(ReplicationProtocolCodec.DecodePlayerInventoryState(result.Payload)); } catch (Exception) { }
                break;
        }
        log?.LogInfo($"已应用主机权威操作结果：请求 {result.RequestId}，类型 {result.Kind}，目标 {result.TargetValue:X16}，版本 {result.Revision}。");
    }

    // ---- P0-I：原版 Darkwood Replay（AuthorityReplayScope 内）----
    private bool TryReplayContainerGrab(ActionRequestMessage req, ActionResultMessage result)
    {
        var id=new EntityId(req.TargetValue,req.TargetPersistent);
        var controller=Singleton<Controller>.Instance;
        if(controller==null||!InvItemClass.isNull(controller.pickedUpItem))return false;
        if(!replication.TryGetInventory(id,out var inv)){log?.LogWarning($"[REPLAY] ContainerGrab 找不到本地容器 {id}。");return false;}
        ContainerGrabPayload p; try{p=ReplicationProtocolCodec.DecodeContainerGrab(req.Payload);}catch(Exception){return false;}
        if(p.SlotIndex<0||p.SlotIndex>=inv.slots.Count)return false;
        var slot=inv.slots[p.SlotIndex];
        if(slot==null||InvItemClass.isNull(slot.invItem)){log?.LogWarning($"[REPLAY] ContainerGrab source slot {p.SlotIndex} 已空——消息顺序可能错误（应先 ack 后清 source）。");return false;}
        try{using(BeginAuthorityReplay()){slot.grabItem();}}catch(Exception error){log?.LogWarning($"[REPLAY] ContainerGrab grabItem 失败：{error.Message}");return false;}
        DarkwoodAdapterRuntime.LogMessage($"[REPLAY] action=ContainerGrab source={id}:{p.SlotIndex} method=InvSlot.grabItem result=success");
        DumpCursor(controller.pickedUpItem);
        return true;
    }
    private bool TryReplayPlayerGrab(ActionRequestMessage req, ActionResultMessage result)
    {
        var controller=Singleton<Controller>.Instance;
        if(controller==null||!InvItemClass.isNull(controller.pickedUpItem))return false;
        PlayerGrabPayload p; try{p=ReplicationProtocolCodec.DecodePlayerGrab(req.Payload);}catch(Exception){return false;}
        var inv=p.FromHotbar?Player.Instance.Hotbar:Player.Instance.Inventory;
        if(inv==null||p.SlotIndex<0||p.SlotIndex>=inv.slots.Count)return false;
        var slot=inv.slots[p.SlotIndex];
        if(slot==null||InvItemClass.isNull(slot.invItem)){log?.LogWarning($"[REPLAY] PlayerGrab source slot {(p.FromHotbar?"hotbar":"playerInv")}:{p.SlotIndex} 已空。");return false;}
        try{using(BeginAuthorityReplay()){slot.grabItem();}}catch(Exception error){log?.LogWarning($"[REPLAY] PlayerGrab grabItem 失败：{error.Message}");return false;}
        DarkwoodAdapterRuntime.LogMessage($"[REPLAY] action=PlayerGrab source={(p.FromHotbar?"hotbar":"playerInv")}:{p.SlotIndex} method=InvSlot.grabItem result=success");
        DumpCursor(controller.pickedUpItem);
        return true;
    }
    private bool ReplayPickedUpWorldItem(ActionRequestMessage req, ActionResultMessage result)
    {
        var id=new EntityId(req.TargetValue,req.TargetPersistent);
        var controller=Singleton<Controller>.Instance;
        if(controller==null||!InvItemClass.isNull(controller.pickedUpItem))return false;
        if(!replication.TryGetInventory(id,out var inv)){log?.LogWarning($"[REPLAY] Pickup：本地掉落物反射已清理（despawn 早于 ack？顺序错误）。");return false;}
        if(inv.slots==null||inv.slots.Count==0||InvItemClass.isNull(inv.slots[0].invItem))return false;
        var slot=inv.slots[0];
        try{using(BeginAuthorityReplay()){slot.grabItem();}}catch(Exception error){log?.LogWarning($"[REPLAY] Pickup grabItem 失败：{error.Message}");return false;}
        // 把 cursor UI 从 mirror 根解离——随后 RuntimeEntityDespawn 会销毁 mirror，但不能连带杀 cursor 图标（用户允许的原版+引导路径）。
        var picked=controller.pickedUpItem;
        if(picked!=null&&!InvItemClass.isNull(picked)&&picked.UIInvItem!=null&&picked.UIInvItem.transform!=null)
        {
            try { var uiRoot=Singleton<UI>.Instance; picked.UIInvItem.transform.SetParent(uiRoot!=null?uiRoot.transform:picked.UIInvItem.transform.root,true); } catch(Exception){}
        }
        DarkwoodAdapterRuntime.LogMessage($"[REPLAY] action=Pickup source={id} method=InvSlot.grabItem result=success（cursor-only）");
        DumpCursor(controller.pickedUpItem);
        return true;
    }
    private bool ReplayPlaceToDestination(ActionRequestMessage req, ActionResultMessage result)
    {
        var controller=Singleton<Controller>.Instance;
        if(controller==null||InvItemClass.isNull(controller.pickedUpItem))return false;
        HeldToInventoryPayload p; try{p=ReplicationProtocolCodec.DecodeHeldToInventory(req.Payload);}catch(Exception){return false;}
        var inv=p.FromHotbar?Player.Instance.Hotbar:Player.Instance.Inventory;
        if(inv==null||p.TargetSlot<0||p.TargetSlot>=inv.slots.Count)return false;
        var slot=inv.slots[p.TargetSlot];
        try{using(BeginAuthorityReplay()){slot.placeItem();}}catch(Exception error){log?.LogWarning($"[REPLAY] placeItem 失败：{error.Message}");return false;}
        DarkwoodAdapterRuntime.LogMessage($"[REPLAY] action=HeldToInventory dest={(p.FromHotbar?"hotbar":"playerInv")}:{p.TargetSlot} method=InvSlot.placeItem result=success");
        // Reconcile：权威背包才是最终真相。
        if(result.Payload.Length>0) try { ApplyPlayerInventory(ReplicationProtocolCodec.DecodePlayerInventoryState(result.Payload)); } catch (Exception) { }
        return true;
    }
    private void ReplayDropCursorCleanup()
    {
        var controller=Singleton<Controller>.Instance;
        if(controller==null||InvItemClass.isNull(controller.pickedUpItem))return;
        // 复用原版 Player.spawnDroppedInvItem 的 Cursor cleanup 段（80885-80893）：despawn cursor UI + 清 pickedUpItem + refreshRecipes。
        // 绝不在客户端 Instantiate world DroppedItem（世界对象只能来自 Host RuntimeEntitySpawn）。
        try
        {
            using(BeginAuthorityReplay())
            {
                var item=controller.pickedUpItem;
                if(item!=null&&item.UIInvItem!=null&&item.UIInvItem.transform!=null)try{item.UIInvItem.despawn();}catch(Exception){}
                controller.pickedUpItem=null;
                var player=Player.Instance; if(player!=null)player.refreshRecipes();
            }
            DarkwoodAdapterRuntime.LogMessage("[REPLAY] action=Drop method=spawnDroppedInvItem(cursor-cleanup only) result=success");
        }
        catch(Exception error){DarkwoodAdapterRuntime.LogMessage($"[REPLAY] Drop cursor cleanup 异常：{error.Message}");}
    }

    // P0-D：完整 Cursor UI 诊断（UIInvItem!=null 但 active 失效仍算 FAIL）。
    internal static void DumpCursor(InvItemClass item)
    {
        var controller=Singleton<Controller>.Instance;
        var uiObj=item?.UIInvItem;
        var spr=uiObj!=null?uiObj.sprite:null;
        string spriteName="无";
        try{if(spr!=null)spriteName=spr.spriteId.ToString()+"/"+spr.name;}catch(Exception){}
        string amountLabel="无";
        try{if(uiObj!=null&&uiObj.amount!=null)amountLabel=uiObj.amount.text;}catch(Exception){}
        bool activeHierarchy=false,activeSelf=false;string parent="无";string pos="无";
        try{if(uiObj!=null&&uiObj.transform!=null){activeSelf=uiObj.gameObject.activeSelf;activeHierarchy=uiObj.gameObject.activeInHierarchy;parent=uiObj.transform.parent!=null?uiObj.transform.parent.name:"root";pos=uiObj.transform.position.ToString();}}catch(Exception){}
        DarkwoodAdapterRuntime.LogMessage($"[CURSOR] type={(item!=null?item.type:"?")} amount={(item!=null?item.amount:0)} pickedUpItemPresent={(controller!=null&&!InvItemClass.isNull(controller?.pickedUpItem))} uiPresent={(uiObj!=null)} uiGameObjectPresent={(uiObj!=null&&uiObj.gameObject!=null)} uiActiveSelf={activeSelf} uiActiveInHierarchy={activeHierarchy} uiParent={parent} uiPosition={pos} spriteName={spriteName} amountLabel={amountLabel} hostHeldKnown={(controller!=null?Singleton<Controller>.Instance.pickedUpItem!=null:false)}");
    }

    // P0-L：销毁全新 Cursor UI（防泄漏）+ 清手持状态。
    internal static void ClearHeldItem()
    {
        var controller=Singleton<Controller>.Instance;
        if(controller!=null&&!InvItemClass.isNull(controller.pickedUpItem))
        {
            var ui=controller.pickedUpItem.UIInvItem;
            if(ui!=null&&ui.transform!=null)try{UnityEngine.Object.Destroy(ui.gameObject);}catch(Exception){}
            controller.pickedUpItem=null;
        }
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
        // P0-C：立即向 Host 上报真实背包完整拓扑（含全部空槽 → 长度即容量），
        // 否则 Host 影子槽位数量停留在 GuestProfile 旧值 → placeItem 目标槽被判 INVALID_TARGET_SLOT。
        try
        {
            // P0-C：不管处于 LoadingWorld/Ready 哪个阶段，只要会话活着就上报（Host 随时可按真实容量重建 shadow）。
            if (clientSession != null)
            {
                var localState = DarkwoodWorldAuthorityService.CaptureLocalPlayerInventory();
                clientSession.Send(ProtocolMessageType.PlayerInventoryState, ReplicationProtocolCodec.Encode(localState));
                log?.LogInfo($"[HELD] 已上报真实背包拓扑：backpack {localState.Backpack.Length} 槽 / hotbar {localState.Hotbar.Length} 槽。");
            }
        }
        catch (Exception error) { log?.LogWarning($"上报真实背包拓扑失败：{error.Message}"); }
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

