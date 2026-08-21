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
            case ActionKindWire.Pickup:
                // 阶段三：WorldDroppedItem → 直接进背包（原版语义）——ack 携带权威 InventorySnapshot，直接 Apply（不 Replay cursor、不进 pickedUpItem）。
                if (result.Payload.Length > 0) try { ApplyPlayerInventory(ReplicationProtocolCodec.DecodePlayerInventoryState(result.Payload)); } catch (Exception) { }
                break;
            case ActionKindWire.HeldToContainer: ReplayHeldToContainer(req,result); break;
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
    // ── 阶段二：HeldToContainer ack → 原版 Replay（Host 已 commit；空/stack→placeItem，swap→swapItems）──
    private bool ReplayHeldToContainer(ActionRequestMessage req, ActionResultMessage result)
    {
        var controller = Singleton<Controller>.Instance;
        if (controller == null || InvItemClass.isNull(controller.pickedUpItem)) return false;
        var id = new EntityId(req.TargetValue, req.TargetPersistent);
        if (!replication.TryGetInventory(id, out var container) || container.slots == null) return false;
        HeldToContainerPayload p; try { p = ReplicationProtocolCodec.DecodeHeldToContainer(req.Payload); } catch (Exception) { return false; }
        if (p.SlotIndex < 0 || p.SlotIndex >= container.slots.Count) return false;
        var slot = container.slots[p.SlotIndex];
        HeldItemStatePayload heldAfter;
        try { heldAfter = ReplicationProtocolCodec.DecodeHeldItemState(result.Payload); } catch (Exception) { heldAfter = default; }
        bool swap = !heldAfter.IsEmpty; // Host 返回 heldAfter=非空 ⇒ swap（原槽物成了新 Held）
        bool replayOk = false;
        try
        {
            using (BeginAuthorityReplay())
            {
                if (swap) { if (slot.invItem != null && !InvItemClass.isNull(slot.invItem)) slot.swapItems(); }
                else slot.placeItem();
            }
            replayOk = true;
        }
        catch (Exception error) { log?.LogWarning($"[RECONCILE] held-to-container replay 失败（{(swap ? "swapItems" : "placeItem")}）：{error.Message}"); }
        DarkwoodAdapterRuntime.LogMessage($"[REPLAY] action=HeldToContainer container={id}:{p.SlotIndex} method={(swap ? "InvSlot.swapItems" : "InvSlot.placeItem")} result={(replayOk ? "success" : "failed")}");
        // reconcile：swap → 原版已把新 held 挂到 cursor（pickedUpItem=原槽物）；非 swap → Host held 已清，本地 cursor 必须清。
        if (swap)
        {
            if (controller == null || InvItemClass.isNull(controller.pickedUpItem))
            {
                // 视觉兜底：原版 swap 未挂上（例如拾取自世界的物品 slot 残缺时）——按 Host heldAfter 重建 cursor。
                var item = new InvItemClass(heldAfter.Type, heldAfter.Durability, heldAfter.Amount, (InvItem.ModifierQuality)heldAfter.Quality, heldAfter.Recipe);
                if (heldAfter.Ammo > 0) item.ammo = heldAfter.Ammo;
                try { var pl = Player.Instance; if (pl != null && pl.Inventory != null && pl.Inventory.slots.Count > 0) item.initialize(pl.Inventory.slots[0]); } catch (Exception) { }
                controller.pickedUpItem = item;
                CreateCursorVisualFallback(item);
            }
        }
        else
        {
            if (controller != null && !InvItemClass.isNull(controller.pickedUpItem))
            {
                DarkwoodAdapterRuntime.LogMessage($"[RECONCILE] action=HeldToContainer replay={(replayOk ? "success" : "failed")} hostHeld=Empty localHeldAfter=Empty");
                ClearHeldItem();
                try { var pl = Player.Instance; if (pl != null) pl.refreshRecipes(); } catch (Exception) { }
            }
        }
        return replayOk;
    }

    // P0-H：原版 InvSlot.createInvItemIcon（Core.AddPrefab "UI/InvItem" + setUISprite + refresh + stackable/ammo 标签）——HeldItem 视觉兜底
    //（如 swap 后原版未挂上 cursor 时重建；sprite 来自真实 baseClass.iconType，不受 slot.inventory.open 约束）。
    internal static bool CreateCursorVisualFallback(InvItemClass item)
    {
        if(item==null||InvItemClass.isNull(item)||item.baseClass==null)return false;
        try
        {
            var uiRoot=Singleton<UI>.Instance;
            var root=uiRoot!=null?uiRoot.transform:null;
            if(root==null)return false;
            var go=global::Core.AddPrefab("UI/InvItem", root.position, Quaternion.Euler(90f,0f,0f), root.gameObject, worldSpace:true);
            if(go==null)return false;
            var ui=go.GetComponent<UIInvItem>();
            if(ui==null){UnityEngine.Object.Destroy(go);return false;}
            item.UIInvItem=ui;
            try{ui.initialize();}catch(Exception){}
            // 原版 setUISprite/refresh 要求 slot.inventory.open 才设图标；此处直接执行 open 路径的核心动作（iconType 来自原生物品定义）。
            try
            {
                if (ui.sprite != null && item.baseClass != null && item.baseClass.iconType != null)
                {
                    ui.sprite.SetSprite(item.baseClass.iconType);
                    ui.sprite.Build();
                }
            }
            catch (Exception) { }
            try{item.setUISprite();}catch(Exception){}
            if(item.baseClass.hasAmmo)
            {
                var a=global::Core.AddPrefab("UI/UIItemAmmo", go.transform.position+new Vector3(0f,-30f,0f), Quaternion.Euler(90f,0f,0f), go, worldSpace:true);
                if(a!=null)ui.ammo=a.GetComponent<tk2dTextMesh>();
            }
            if(item.baseClass.stackable)
            {
                var a=global::Core.AddPrefab("UI/UIItemAmount", go.transform.position, Quaternion.Euler(90f,0f,0f), go, worldSpace:true);
                if(a!=null){a.transform.localScale=Vector3.one;a.transform.localPosition=new Vector3(-25f,8f,-1f);ui.amount=a.GetComponent<tk2dTextMesh>();}
            }
            try{ui.refresh(item);}catch(Exception){}
            return true;
        }
        catch(Exception error){DarkwoodAdapterRuntime.LogMessage($"[REPLAY] createInvItemIcon 兜底失败：{error.Message}");return false;}
    }

    private bool ReplayPlaceToDestination(ActionRequestMessage req, ActionResultMessage result)
    {
        var controller=Singleton<Controller>.Instance;
        if(controller==null||InvItemClass.isNull(controller.pickedUpItem))return false;
        HeldToInventoryPayload p; try{p=ReplicationProtocolCodec.DecodeHeldToInventory(req.Payload);}catch(Exception){return false;}
        var inv=p.FromHotbar?Player.Instance.Hotbar:Player.Instance.Inventory;
        if(inv==null||p.TargetSlot<0||p.TargetSlot>=inv.slots.Count)return false;
        var slot=inv.slots[p.TargetSlot];
        string localHeldBefore = controller.pickedUpItem.type+" x"+controller.pickedUpItem.amount;
        bool replayOk=false;
        try{using(BeginAuthorityReplay()){slot.placeItem();}replayOk=true;}
        catch(Exception error){log?.LogWarning($"[RECONCILE] placeItem replay 失败：{error.Message}");replayOk=false;}
        DarkwoodAdapterRuntime.LogMessage($"[REPLAY] action=HeldToInventory dest={(p.FromHotbar?"hotbar":"playerInv")}:{p.TargetSlot} method=InvSlot.placeItem result={(replayOk?"success":"failed")}");
        // P0-G：Host 已 commit（held 已清、背包已含物品）——无论视觉 Replay 成败都必须强制 reconcile 到权威，
        // 绝不允许出现 Host Empty / Client Holding 的永久软锁。
        if(result.Payload.Length>0) try { ApplyPlayerInventory(ReplicationProtocolCodec.DecodePlayerInventoryState(result.Payload)); } catch (Exception) { }
        if(controller!=null&&!InvItemClass.isNull(controller.pickedUpItem))
        {
            DarkwoodAdapterRuntime.LogMessage($"[RECONCILE] action=HeldToInventory replay={(replayOk?"success":"failed")} hostHeld=Empty localHeldBefore={localHeldBefore} localHeldAfter=Empty inventoryApplied=True");
            ClearHeldItem();
            try{var pl=Player.Instance;if(pl!=null)pl.refreshRecipes();}catch(Exception){}
        }
        return replayOk;
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

    // P0-D：World Cursor 强 invariant（[CURSOR-WORLD]）——pickedSlotInventoryAlive 必须 True，sprite 必须匹配真实物品。
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

    private static readonly System.Collections.Generic.Dictionary<int,int> AppliedInventoryRevisions = new System.Collections.Generic.Dictionary<int,int>();
    internal static void ApplyPlayerInventory(PlayerInventoryStatePayload state)
    {
        var player=Player.Instance;if(player?.Inventory==null||player.Hotbar==null)throw new InvalidOperationException("客户端玩家库存不可用。");
        // P0-Authority-Drift：本人背包权威包（PlayerId=客户端自身 peer id）按 revision 门控——
        // 乱序/迟到的旧 revision 包不得覆盖已应用的更新状态（否则出现"place-accepted 后物品消失"）。
        int selfPeerId = DarkwoodAdapterRuntime.Instance?.clientSession?.PeerId ?? 0;
        if (state.PlayerId != 0 && AppliedInventoryRevisions.TryGetValue(state.PlayerId, out var lastApplied) && state.Revision < lastApplied)
        {
            DarkwoodAdapterRuntime.LogMessage($"[INV-REV] 丢弃迟到旧包 player={state.PlayerId} staleRev={state.Revision} lastApplied={lastApplied}");
            return;
        }
        DarkwoodInventoryAdapter.Apply(player.Inventory,ToDarkwoodSlots(state.Backpack));
        DarkwoodInventoryAdapter.Apply(player.Hotbar,ToDarkwoodSlots(state.Hotbar));
        player.refreshRecipes();
        if (state.PlayerId != 0) AppliedInventoryRevisions[state.PlayerId] = state.Revision;
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

