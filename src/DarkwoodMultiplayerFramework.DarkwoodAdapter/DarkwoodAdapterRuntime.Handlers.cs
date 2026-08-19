using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DarkwoodMultiplayerFramework.Core;
using DarkwoodMultiplayerFramework.Entities;
using DarkwoodMultiplayerFramework.Network;
using DarkwoodMultiplayerFramework.Protocol;
using UnityEngine;

namespace DarkwoodMultiplayerFramework.DarkwoodAdapter;

public sealed partial class DarkwoodAdapterRuntime
{
    // ── 0.8.9 第三刀：MessageRouter 处理器注册 ──────────────────────────────
    private readonly NetworkMessageRouter router = new NetworkMessageRouter();

    private void RegisterMessageHandlers()
    {
        router.Register(new HostSaveHandlers(this));
        router.Register(new HostSnapshotHandlers(this));
        router.Register(new HostPlayerHandlers(this));
        router.Register(new HostActionHandlers(this));
        router.Register(new HostInventoryHandlers(this));
        router.Register(new HostRescueHandlers(this));
        router.Register(new ClientSaveHandlers(this));
        router.Register(new ClientSnapshotHandlers(this));
        router.Register(new ClientEntityHandlers(this));
        router.Register(new ClientPlayerHandlers(this));
        router.Register(new ClientLifecycleHandlers(this));
    }

    private bool DispatchToRouter(PeerContext peer, ProtocolEnvelope envelope)
        => router.Dispatch(peer, envelope);

    // ── 主机侧处理器 ──────────────────────────────────────────────────────
    private sealed class HostSaveHandlers : INetworkMessageHandler
    {
        private readonly DarkwoodAdapterRuntime runtime;
        public HostSaveHandlers(DarkwoodAdapterRuntime runtime) => this.runtime = runtime;

        public bool Handles(ProtocolMessageType type) =>
            type == ProtocolMessageType.SaveTransferRequest || type == ProtocolMessageType.SaveTransferApplied;

        public void Handle(PeerContext peer, ProtocolEnvelope envelope)
        {
            if (envelope.MessageType == ProtocolMessageType.SaveTransferRequest)
            {
                ReplicationProtocolCodec.DecodeSaveTransferRequest(envelope.Payload);
                runtime.PrepareSave(peer.PeerId);
            }
            else
            {
                var applied = ReplicationProtocolCodec.DecodeSaveTransferApplied(envelope.Payload);
                if (!runtime.SaveState.TryGetSentSave(peer.PeerId, out var expected) || expected != applied.TransferId)
                    throw new InvalidDataException("Save acknowledgement does not match active transfer.");
                runtime.log?.LogInfo($"Peer {peer.PeerId} installed verified save {applied.TransferId}.");
            }
        }
    }

    private sealed class HostSnapshotHandlers : INetworkMessageHandler
    {
        private readonly DarkwoodAdapterRuntime runtime;
        public HostSnapshotHandlers(DarkwoodAdapterRuntime runtime) => this.runtime = runtime;

        public bool Handles(ProtocolMessageType type) =>
            type == ProtocolMessageType.WorldSnapshotApplied ||
            (type == ProtocolMessageType.Ready && runtime.IsHost);

        public void Handle(PeerContext peer, ProtocolEnvelope envelope)
        {
            if (envelope.MessageType == ProtocolMessageType.Ready)
            {
                runtime.PrepareSnapshot(peer.PeerId, ReplicationProtocolCodec.DecodeReady(envelope.Payload));
                return;
            }
            var applied = ReplicationProtocolCodec.DecodeWorldSnapshotApplied(envelope.Payload);
            if (!runtime.SaveState.TryGetSentSnapshot(peer.PeerId, out var expected) || expected != applied.SnapshotId || applied.Scene != runtime.CurrentScene || applied.RegistryDigest != runtime.RegistryDigest)
                throw new InvalidDataException("Snapshot acknowledgement does not match active snapshot.");
            var firstReady = runtime.readyPeers.Add(peer.PeerId);
            if (firstReady)
            {
                var hostPosition = Player.Instance != null ? Player.Instance.transform.position : Vector3.zero;
                var day = global::Core.currentProfile?.day ?? 0;
                var record = runtime.Players.ResolveGuestProfile(peer.PeerId, runtime.Players.TryGetGuestKey(peer.PeerId, out var guestKey) ? guestKey : null, day, hostPosition, out var spawn);
                runtime.Players.TryGetInventory(peer.PeerId, out var shadow);
                var hostMaxHealth = Player.Instance != null ? Player.Instance.maxHealth : 100f;
                runtime.Combat.RegisterPeer(peer.PeerId, hostMaxHealth); // 血量状态归战斗服务
                runtime.Queue(peer.PeerId, ProtocolMessageType.GuestProfile, ReplicationProtocolCodec.Encode(new GuestProfileMessage(shadow.CaptureState(), spawn.x, spawn.y, spawn.z, record.Day, record.JoinCount, hostMaxHealth, hostMaxHealth, false)));
                runtime.Players.PersistGuestProfile(peer.PeerId);
            }
            runtime.Queue(peer.PeerId, ProtocolMessageType.Ready, ReplicationProtocolCodec.Encode(new ReadyMessage(runtime.CurrentScene, runtime.RegistryDigest)));
            runtime.SendHostPose(peer.PeerId);
            runtime.log?.LogInfo(firstReady ? $"Peer {peer.PeerId} READY after applying snapshot {applied.SnapshotId}, {applied.EntityCount} entities." : $"Peer {peer.PeerId} repeated snapshot acknowledgement {applied.SnapshotId}; Ready confirmation resent.");
        }
    }

    private sealed class HostPlayerHandlers : INetworkMessageHandler
    {
        private readonly DarkwoodAdapterRuntime runtime;
        public HostPlayerHandlers(DarkwoodAdapterRuntime runtime) => this.runtime = runtime;

        public bool Handles(ProtocolMessageType type) => type == ProtocolMessageType.PlayerPose && runtime.IsHost;

        public void Handle(PeerContext peer, ProtocolEnvelope envelope)
        {
            var pose = ReplicationProtocolCodec.DecodePlayerPose(envelope.Payload);
            if (!runtime.readyPeers.Contains(peer.PeerId) || pose.Scene != runtime.CurrentScene) return;
            runtime.Combat.SetPeerMaxHealth(peer.PeerId, pose.MaxHealth);
            pose = new PlayerPoseMessage(peer.PeerId, pose.Sequence, runtime.CurrentScene, pose.X, pose.Y, pose.Z, pose.Qx, pose.Qy, pose.Qz, pose.Qw, pose.MaxHealth, pose.Flags, pose.TorsoClip, pose.TorsoFrame, pose.LegsClip, pose.LegsFrame);
            runtime.Players.UpdateRemotePosition(peer.PeerId, new Vector3(pose.X, pose.Y, pose.Z));
            runtime.Players.RemotePlayers.Apply(pose, 0);
            var payload = ReplicationProtocolCodec.Encode(pose);
            foreach (var readyPeer in runtime.readyPeers.ToArray()) if (readyPeer != peer.PeerId) runtime.Queue(readyPeer, ProtocolMessageType.PlayerPose, payload);
        }
    }

    private sealed class HostActionHandlers : INetworkMessageHandler
    {
        private readonly DarkwoodAdapterRuntime runtime;
        public HostActionHandlers(DarkwoodAdapterRuntime runtime) => this.runtime = runtime;

        public bool Handles(ProtocolMessageType type) => type == ProtocolMessageType.ActionRequest;

        public void Handle(PeerContext peer, ProtocolEnvelope envelope)
            => runtime.HandleActionRequest(peer.PeerId, ReplicationProtocolCodec.DecodeActionRequest(envelope.Payload));
    }

    private sealed class HostInventoryHandlers : INetworkMessageHandler
    {
        private readonly DarkwoodAdapterRuntime runtime;
        public HostInventoryHandlers(DarkwoodAdapterRuntime runtime) => this.runtime = runtime;

        public bool Handles(ProtocolMessageType type) => runtime.IsHost && (type == ProtocolMessageType.InventoryState || type == ProtocolMessageType.PlayerInventoryState);

        public void Handle(PeerContext peer, ProtocolEnvelope envelope)
        {
            if (envelope.MessageType == ProtocolMessageType.PlayerInventoryState)
            {
                // 客户端真实背包上报（漂移收敛）：重建该玩家的权威影子背包
                var state = ReplicationProtocolCodec.DecodePlayerInventoryState(envelope.Payload);
                if (runtime.Players.RebuildInventory(peer.PeerId, state))
                    runtime.log?.LogInfo($"主机已按客户端真实背包重建影子：玩家 {peer.PeerId}。");
                return;
            }
            var inventory = ReplicationProtocolCodec.DecodeInventoryState(envelope.Payload);
            var id = new EntityId(inventory.Value, inventory.Persistent);
            if (runtime.replication.TryGetInventoryState(id, out var current) && !ContainerRevisionGate.TryAdvance(inventory.Revision, current.Revision, out _))
            {
                runtime.log?.LogWarning($"容器并发冲突：ID={inventory.Value:X16}，玩家 {peer.PeerId} 基于版本 {inventory.Revision}，主机当前 {current.Revision}——拒绝该次上报并回权威状态。");
                runtime.Queue(peer.PeerId, ProtocolMessageType.InventoryState, ReplicationProtocolCodec.Encode(current));
            }
            else if (!runtime.replication.Apply(inventory))
            {
                runtime.missingEntities.Add(new EntityId(inventory.Value, inventory.Persistent));
                runtime.log?.LogWarning($"忽略缺失实体的容器状态：ID={inventory.Value:X16}，名称={inventory.Name}（主机运行时生成物，等待 Spawn 生命周期补发）。");
            }
            else
            {
                foreach (var readyPeer in runtime.readyPeers.ToArray()) runtime.Queue(readyPeer, ProtocolMessageType.InventoryState, envelope.Payload);
                runtime.log?.LogInfo($"主机已应用客户端容器状态并转发：ID={inventory.Value:X16}，玩家 {peer.PeerId}，版本 {inventory.Revision}，槽位 {inventory.Slots.Length}。");
            }
        }
    }

    private sealed class HostRescueHandlers : INetworkMessageHandler
    {
        private readonly DarkwoodAdapterRuntime runtime;
        public HostRescueHandlers(DarkwoodAdapterRuntime runtime) => this.runtime = runtime;

        public bool Handles(ProtocolMessageType type) => type == ProtocolMessageType.RescueRequest;

        public void Handle(PeerContext peer, ProtocolEnvelope envelope)
        {
            var rescue = ReplicationProtocolCodec.DecodeRescueRequest(envelope.Payload);
            if (rescue.PlayerId != peer.PeerId) throw new InvalidDataException("Rescue request player id mismatch.");
            runtime.Combat.HandleRescueIntent(peer.PeerId, rescue.Cancel);
        }
    }

    // ── 客户端侧处理器 ────────────────────────────────────────────────────
    private sealed class ClientSaveHandlers : INetworkMessageHandler
    {
        private readonly DarkwoodAdapterRuntime runtime;
        public ClientSaveHandlers(DarkwoodAdapterRuntime runtime) => this.runtime = runtime;

        public bool Handles(ProtocolMessageType type) =>
            type == ProtocolMessageType.SaveTransferManifest || type == ProtocolMessageType.SaveTransferChunk;

        public void Handle(PeerContext peer, ProtocolEnvelope envelope)
        {
            if (envelope.MessageType == ProtocolMessageType.SaveTransferManifest)
            {
                runtime.SaveState.BeginSaveReceive(ReplicationProtocolCodec.DecodeSaveTransferManifest(envelope.Payload));
                runtime.log?.LogInfo($"开始接收存档：{runtime.SaveState.PendingSaveManifest.TotalBytes} 字节，{runtime.SaveState.PendingSaveManifest.ChunkCount} 个数据块。");
            }
            else runtime.ReceiveSaveChunk(ReplicationProtocolCodec.DecodeSaveTransferChunk(envelope.Payload));
        }
    }

    private sealed class ClientSnapshotHandlers : INetworkMessageHandler
    {
        private readonly DarkwoodAdapterRuntime runtime;
        public ClientSnapshotHandlers(DarkwoodAdapterRuntime runtime) => this.runtime = runtime;

        public bool Handles(ProtocolMessageType type) =>
            type == ProtocolMessageType.WorldSnapshotManifest || type == ProtocolMessageType.WorldSnapshotChunk
            || type == ProtocolMessageType.EntityBindingManifest || type == ProtocolMessageType.EntityBindingChunk;

        public void Handle(PeerContext peer, ProtocolEnvelope envelope)
        {
            if (envelope.MessageType == ProtocolMessageType.EntityBindingManifest)
            {
                var bindingManifest = ReplicationProtocolCodec.DecodeEntityBindingManifest(envelope.Payload);
                if (!string.Equals(bindingManifest.Scene, runtime.CurrentScene, StringComparison.Ordinal))
                {
                    runtime.FailClient("BINDING_SCENE_MISMATCH", new InvalidDataException($"绑定清单场景不一致：host={bindingManifest.Scene}，client={runtime.CurrentScene}。"));
                    return;
                }
                runtime.BeginBindingReceive(bindingManifest);
            }
            else if (envelope.MessageType == ProtocolMessageType.EntityBindingChunk)
                runtime.ReceiveBindingChunk(ReplicationProtocolCodec.DecodeEntityBindingChunk(envelope.Payload));
            else if (envelope.MessageType == ProtocolMessageType.WorldSnapshotManifest)
            {
                var manifest = ReplicationProtocolCodec.DecodeWorldSnapshotManifest(envelope.Payload);
                runtime.SaveState.BeginSnapshotReceive(manifest);
                runtime.log?.LogInfo($"开始接收世界快照：{manifest.TotalBytes} 字节，{manifest.ChunkCount} 个数据块。");
            }
            else runtime.ReceiveSnapshotChunk(ReplicationProtocolCodec.DecodeWorldSnapshotChunk(envelope.Payload));
        }
    }

    private sealed class ClientEntityHandlers : INetworkMessageHandler
    {
        private readonly DarkwoodAdapterRuntime runtime;
        public ClientEntityHandlers(DarkwoodAdapterRuntime runtime) => this.runtime = runtime;

        public bool Handles(ProtocolMessageType type) =>
            type == ProtocolMessageType.EntityDelta || type == ProtocolMessageType.InventoryState ||
            type == ProtocolMessageType.RuntimeEntitySpawn || type == ProtocolMessageType.RuntimeEntityDespawn;

        public void Handle(PeerContext peer, ProtocolEnvelope envelope)
        {
            switch (envelope.MessageType)
            {
                case ProtocolMessageType.EntityDelta:
                {
                    var delta = ReplicationProtocolCodec.DecodeEntityDelta(envelope.Payload);
                    if (runtime.clientSession?.Session.Lifecycle.State == ConnectionState.Ready && delta.Scene == runtime.CurrentScene)
                    {
                        runtime.replication.Apply(delta.Entities, false);
                        runtime.replication.ApplyDespawns(delta.Despawns);
                    }
                    break;
                }
                case ProtocolMessageType.InventoryState:
                {
                    var inventory = ReplicationProtocolCodec.DecodeInventoryState(envelope.Payload);
                    var id = new EntityId(inventory.Value, inventory.Persistent);
                    if (runtime.replication.TryGetInventoryState(id, out var local))
                    {
                        if (inventory.Revision < local.Revision)
                        {
                            var taken = DarkwoodContainerTakePatch.DrainPendingTakes(id);
                            if (taken.Count > 0)
                            {
                                foreach (var item in taken) DarkwoodInventoryAdapter.RemoveFromPlayerInventory(item.Type, item.Amount);
                                runtime.log?.LogWarning($"容器并发冲突补偿：ID={inventory.Value:X16}，本次拿取未生效，已从背包退回 {taken.Count} 类物品，容器回滚为权威版本 {inventory.Revision}。");
                            }
                        }
                        else DarkwoodContainerTakePatch.ClearPendingTakes(id);
                    }
                    if (!runtime.replication.Apply(inventory))
                    {
                        runtime.missingEntities.Add(new EntityId(inventory.Value, inventory.Persistent));
                        runtime.log?.LogWarning($"忽略缺失实体的容器状态：ID={inventory.Value:X16}，名称={inventory.Name}（主机运行时生成物，等待 Spawn 生命周期补发）。");
                    }
                    break;
                }
                case ProtocolMessageType.RuntimeEntitySpawn:
                {
                    var spawn = ReplicationProtocolCodec.DecodeRuntimeEntitySpawn(envelope.Payload);
                    if (runtime.clientSession?.Session.Lifecycle.State == ConnectionState.Ready && spawn.Scene == runtime.CurrentScene)
                        runtime.RuntimeEntities.HandleSpawn(spawn); // 镜像生命周期归服务
                    break;
                }
                case ProtocolMessageType.RuntimeEntityDespawn:
                {
                    var despawn = ReplicationProtocolCodec.DecodeRuntimeEntityDespawn(envelope.Payload);
                    if (runtime.clientSession?.Session.Lifecycle.State == ConnectionState.Ready)
                        runtime.RuntimeEntities.HandleDespawn(despawn); // 镜像销毁归服务
                    break;
                }
            }
        }
    }

    private sealed class ClientPlayerHandlers : INetworkMessageHandler
    {
        private readonly DarkwoodAdapterRuntime runtime;
        public ClientPlayerHandlers(DarkwoodAdapterRuntime runtime) => this.runtime = runtime;

        public bool Handles(ProtocolMessageType type) =>
            type == ProtocolMessageType.PlayerPose || type == ProtocolMessageType.PlayerHealth || type == ProtocolMessageType.PlayerInventoryState;

        public void Handle(PeerContext peer, ProtocolEnvelope envelope)
        {
            if (envelope.MessageType == ProtocolMessageType.PlayerPose)
                runtime.Players.RemotePlayers.Apply(ReplicationProtocolCodec.DecodePlayerPose(envelope.Payload), runtime.clientSession?.PeerId ?? -1);
            else if (envelope.MessageType == ProtocolMessageType.PlayerHealth)
                runtime.Combat.ApplyIncomingPlayerHealth(ReplicationProtocolCodec.DecodePlayerHealth(envelope.Payload));
            else
                ApplyPlayerInventory(ReplicationProtocolCodec.DecodePlayerInventoryState(envelope.Payload));
        }
    }

    private sealed class ClientLifecycleHandlers : INetworkMessageHandler
    {
        private readonly DarkwoodAdapterRuntime runtime;
        public ClientLifecycleHandlers(DarkwoodAdapterRuntime runtime) => this.runtime = runtime;

        public bool Handles(ProtocolMessageType type) =>
            type == ProtocolMessageType.SceneChange || type == ProtocolMessageType.ActionResult ||
            type == ProtocolMessageType.ActionRejected || type == ProtocolMessageType.RescueProgress ||
            type == ProtocolMessageType.AllDowned || type == ProtocolMessageType.GuestProfile ||
            type == ProtocolMessageType.Ready || type == ProtocolMessageType.Error;

        public void Handle(PeerContext peer, ProtocolEnvelope envelope)
        {
            switch (envelope.MessageType)
            {
                case ProtocolMessageType.SceneChange:
                {
                    var change = ReplicationProtocolCodec.DecodeSceneChange(envelope.Payload);
                    runtime.log?.LogInfo($"主机场景已切换到 {change.Scene}，客户端将在 3 秒后自动重连并重新加载新场景存档。");
                    runtime.autoReconnectAt = Time.unscaledTime + 3f;
                    break;
                }
                case ProtocolMessageType.ActionResult: runtime.HandleActionResult(ReplicationProtocolCodec.DecodeActionResult(envelope.Payload)); break;
                case ProtocolMessageType.ActionRejected: runtime.HandleActionRejected(ReplicationProtocolCodec.DecodeActionRejected(envelope.Payload)); break;
                case ProtocolMessageType.RescueProgress: runtime.Combat.HandleRescueProgress(ReplicationProtocolCodec.DecodeRescueProgress(envelope.Payload)); break;
                case ProtocolMessageType.AllDowned: runtime.Combat.HandleAllDowned(); break;
                case ProtocolMessageType.GuestProfile:
                {
                    var profile = ReplicationProtocolCodec.DecodeGuestProfile(envelope.Payload);
                    var lifecycle = runtime.clientSession?.Session.Lifecycle.State;
                    if (lifecycle != ConnectionState.ApplyingSnapshot && lifecycle != ConnectionState.Ready)
                        throw new InvalidDataException("Guest profile arrived outside the joining phase.");
                    runtime.Players.ApplyGuestProfile(profile);
                    break;
                }
                case ProtocolMessageType.Ready:
                {
                    var ready = ReplicationProtocolCodec.DecodeReady(envelope.Payload);
                    if (ready.Scene != runtime.CurrentScene || ready.RegistryDigest != runtime.SaveState.PendingSnapshotManifest.RegistryDigest)
                        throw new InvalidDataException("主机就绪确认与已应用的世界快照不一致。");
                    runtime.SaveState.MarkSnapshotReady();
                    runtime.SaveState.ClearSnapshotApplied();
                    runtime.SaveState.SetProgress("联机已就绪");
                    if (runtime.clientSession?.Session.Lifecycle.State == ConnectionState.ApplyingSnapshot)
                        runtime.clientSession.Session.Lifecycle.MoveTo(ConnectionState.Ready);
                    runtime.log?.LogInfo($"客户端联机已就绪：场景 {ready.Scene}，注册表摘要 {ready.RegistryDigest}。");
                    break;
                }
                case ProtocolMessageType.Error:
                {
                    var error = ReplicationProtocolCodec.DecodeError(envelope.Payload);
                    throw new InvalidDataException($"Host error {error.Code}: {error.Detail}");
                }
            }
        }
    }
}
