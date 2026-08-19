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
    internal void BroadcastInventory(InventoryStateMessage inventory){var payload=ReplicationProtocolCodec.Encode(inventory);foreach(var readyPeer in readyPeers.ToArray())Queue(readyPeer,ProtocolMessageType.InventoryState,payload);}

    private static DarkwoodInventorySlot[] ToDarkwoodSlots(InventorySlotWire[] slots)
    {
        var result=new DarkwoodInventorySlot[slots.Length];for(var i=0;i<slots.Length;i++){var s=slots[i];result[i]=new DarkwoodInventorySlot{Type=s.Type,Amount=s.Amount,Durability=s.Durability,Quality=s.Quality,Recipe=s.Recipe};}return result;
    }

    /// <summary>已做过漂移收敛重试的请求（防止无限重试循环）。</summary>
    private readonly HashSet<Guid> resyncedDropRequests = new HashSet<Guid>();

    private void HandleActionRejected(ActionRejectedMessage rejected)
    {
        if(!pendingActions.TryGetValue(rejected.RequestId,out var request))return;
        pendingActions.Remove(rejected.RequestId);
        log?.LogWarning($"主机拒绝联机操作 {rejected.RequestId}：{rejected.ErrorCode}，主机版本 {rejected.CurrentRevision}。");
        Player.Instance?.displayMessage("联机操作被主机拒绝："+rejected.ErrorCode);

        // 背包漂移收敛：槽位类拒绝说明主机影子背包与客户端真实背包不一致
        //（客户端本地合成/搜尸体等增益未走意图路径）。上报真实背包 → 主机重建影子 → 重试一次。
        if (IsInventoryDriftError(rejected.ErrorCode) && !resyncedDropRequests.Contains(rejected.RequestId)
            && (request.Kind == ActionKindWire.DropItem || request.Kind == ActionKindWire.ContainerTake
                || request.Kind == ActionKindWire.ContainerPut || request.Kind == ActionKindWire.Pickup))
        {
            try
            {
                var state = DarkwoodWorldAuthorityService.CaptureLocalPlayerInventory();
                clientSession?.Send(ProtocolMessageType.PlayerInventoryState, ReplicationProtocolCodec.Encode(state));
                pendingActions[rejected.RequestId] = request;
                clientSession?.Send(ProtocolMessageType.ActionRequest, ReplicationProtocolCodec.Encode(request));
                resyncedDropRequests.Add(rejected.RequestId);
                log?.LogInfo($"背包漂移收敛：已上报真实背包并重试 {request.Kind}（{rejected.RequestId}）。");
            }
            catch (Exception error)
            {
                log?.LogWarning($"背包漂移收敛失败：{error.Message}");
            }
        }
    }

    private static bool IsInventoryDriftError(string code) =>
        code == "PLAYER_SLOT_EMPTY" || code == "INSUFFICIENT_AMOUNT" || code == "SLOT_OUT_OF_RANGE"
        || code == "INVALID_AMOUNT" || code == "INVENTORY_FULL";

    private void SendInventory(int peer,InventoryStateMessage inventory)=>Queue(peer,ProtocolMessageType.InventoryState,ReplicationProtocolCodec.Encode(inventory));

    internal void Queue(int peer,ProtocolMessageType type,byte[] payload,string transferLabel="",int chunkIndex=-1,int chunkCount=0){if(!outgoing.TryGetValue(peer,out var queue))outgoing[peer]=queue=new Queue<OutgoingPacket>();queue.Enqueue(new OutgoingPacket{Type=type,Payload=payload,TransferLabel=transferLabel,ChunkIndex=chunkIndex,ChunkCount=chunkCount});}

    /// <summary>第六刀：消息类型 → 逻辑通道。当前 Transport 仅可靠，分级为未来 UDP/KCP 预留。</summary>
    private static TransportChannel ChannelFor(ProtocolMessageType type)
    {
        switch(type)
        {
            case ProtocolMessageType.PlayerPose:
            case ProtocolMessageType.EntityDelta:
                return TransportChannel.Realtime;
            case ProtocolMessageType.SaveTransferManifest:
            case ProtocolMessageType.SaveTransferChunk:
            case ProtocolMessageType.WorldSnapshotManifest:
            case ProtocolMessageType.WorldSnapshotChunk:
                return TransportChannel.Bulk;
            case ProtocolMessageType.ClientHello:
            case ProtocolMessageType.HandshakeReject:
            case ProtocolMessageType.SceneChange:
            case ProtocolMessageType.Error:
                return TransportChannel.Control;
            default:
                return TransportChannel.ReliableGameplay;
        }
    }
    private void PumpOutgoing()
    {
        if(hostSession==null)return;
        foreach(var peer in outgoing.Keys.ToArray())
        {
            var queue=outgoing[peer];var normalBudget=16;var bulkSent=false;
            while(queue.Count>0&&normalBudget>0)
            {
                var next=queue.Peek();
                // Send at most one save/snapshot block per rendered frame. This
                // keeps Telepathy's writer queue bounded on slower Radmin links.
                if(next.IsBulk&&bulkSent)break;
                var p=queue.Dequeue();
                try
                {
                    hostSession.SendMessage(peer,p.Type,p.Payload,ChannelFor(p.Type));
                }
                catch(Exception error)
                {
                    // TCP 半开/写超时后主动清理（此前僵尸连接导致主机持续重发并卡广播）。
                    log?.LogWarning($"向玩家 {peer} 发送失败（连接不可用）：{error.Message}——清理该玩家会话。");
                    outgoing.Remove(peer);
                    OnPeerDisconnected(peer);
                    break;
                }
                if(p.IsBulk)
                {
                    bulkSent=true;
                    var sent=p.ChunkIndex+1;var percent=(int)(sent*100f/p.ChunkCount);
                    SaveState.SetProgress($"正在向玩家 {peer} 发送{p.TransferLabel}：{sent}/{p.ChunkCount}（{percent}%）");
                    var interval=Math.Max(1,p.ChunkCount/10);
                    if(sent==1||sent==p.ChunkCount||(sent%interval)==0)log?.LogInfo(TransferProgress);
                }
                else normalBudget--;
            }
            if(queue.Count==0){outgoing.Remove(peer);}
        }
    }
    private void SendLocalPose(){if(!DarkwoodPlayerAdapter.TryCapture(out var p)||clientSession==null)return;var player=Player.Instance;var maxHealth=player!=null?player.maxHealth:100f;var flags=p.Flags;if(DarkwoodDownedPatch.LocalDowned)flags|=PlayerPoseFlags.Downed;clientSession.Send(ProtocolMessageType.PlayerPose,ReplicationProtocolCodec.Encode(new PlayerPoseMessage(clientSession.PeerId,++poseSequence,p.Scene,p.Position.x,p.Position.y,p.Position.z,p.Rotation.x,p.Rotation.y,p.Rotation.z,p.Rotation.w,maxHealth,flags,p.TorsoClip,p.TorsoFrame,p.LegsClip,p.LegsFrame)),TransportChannel.Realtime);}
    private void SendHostPose(int peer){if(!DarkwoodPlayerAdapter.TryCapture(out var p))return;var player=Player.Instance;var maxHealth=player!=null?player.maxHealth:100f;var flags=p.Flags;if(DarkwoodDownedPatch.LocalDowned)flags|=PlayerPoseFlags.Downed;Queue(peer,ProtocolMessageType.PlayerPose,ReplicationProtocolCodec.Encode(new PlayerPoseMessage(0,++poseSequence,p.Scene,p.Position.x,p.Position.y,p.Position.z,p.Rotation.x,p.Rotation.y,p.Rotation.z,p.Rotation.w,maxHealth,flags,p.TorsoClip,p.TorsoFrame,p.LegsClip,p.LegsFrame)));}
    private void SendHostPose(){foreach(var peer in readyPeers.ToArray())SendHostPose(peer);}
    private void FailClient(string code,Exception error){if(sessionError.Length>0)return;clientWorldFreshForSession=false;sessionError=code+": "+error.Message;Session.Error=sessionError;log?.LogError($"Standalone DMF session failed [{code}]: {error}");try{clientSession?.Fail(sessionError);}catch{}SetState(ConnectionState.Failed);}
    private string HostKey(){using var sha=System.Security.Cryptography.SHA256.Create();var value=(addressConfig?.Value??"host")+":"+Port;return BitConverter.ToString(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(value)),0,8).Replace("-","");}
}

