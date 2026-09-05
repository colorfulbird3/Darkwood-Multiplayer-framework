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
        router.Register(new HostCommitHandlers(this));
        router.Register(new ClientCommitHandlers(this));
        router.Register(new ClientTestHandlers(this));
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
                runtime.Queue(peer.PeerId, ProtocolMessageType.GuestProfile, ReplicationProtocolCodec.Encode(new GuestProfileMessage(shadow.CaptureState(peer.PeerId), spawn.x, spawn.y, spawn.z, record.Day, record.JoinCount, hostMaxHealth, hostMaxHealth, false)));
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
            foreach (var readyPeer in runtime.ReadyPeersSnapshot) if (readyPeer != peer.PeerId) runtime.Queue(readyPeer, ProtocolMessageType.PlayerPose, payload);
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

        public bool Handles(ProtocolMessageType type) => type == ProtocolMessageType.InventoryState || type == ProtocolMessageType.PlayerInventoryState || type == ProtocolMessageType.GuestProfileApplied || type == ProtocolMessageType.ContainerStateReport;

        public void Handle(PeerContext peer, ProtocolEnvelope envelope)
        {
            if (envelope.MessageType == ProtocolMessageType.ContainerStateReport)
            {
                // v0.9 Trusted Client：客户端已本地原版执行容器操作 → Host 应用该容器权威状态并广播（冲突后到优先）。
                InventoryStateMessage msg;
                try { msg = ReplicationProtocolCodec.DecodeInventoryState(envelope.Payload); }
                catch (Exception error) { runtime.log?.LogWarning($"[CONTAINER] 容器快照上报解码失败：{error.Message}"); return; }
                var cid = new EntityId(msg.Value, msg.Persistent);
                if (!runtime.replication.TryGetInventory(cid, out var container) || container == null)
                { runtime.log?.LogWarning($"[CONTAINER] 容器快照上报：实体 {msg.Value:X8} 未绑定，忽略。"); return; }
                try
                {
                    var slots = new DarkwoodInventorySlot[msg.Slots.Length];
                    for (var i = 0; i < slots.Length; i++) { var s = msg.Slots[i]; slots[i] = new DarkwoodInventorySlot { Type = s.Type, Amount = s.Amount, Durability = s.Durability, Quality = s.Quality, Recipe = s.Recipe }; }
                    DarkwoodInventoryAdapter.Apply(container, slots);
                    try { container.refreshItems(); } catch (Exception) { }
                    runtime.BroadcastInventory(runtime.CaptureAuthoritativeInventoryForHost(cid));
                    runtime.log?.LogInfo($"[CONTAINER] recv peer={peer.PeerId} id={cid.Value:X8} slots={msg.Slots.Length} → 已应用并广播权威状态。");
                }
                catch (Exception error) { runtime.log?.LogWarning($"[CONTAINER] 容器快照应用失败（{cid.Value:X8}）：{error.Message}"); }
                return;
            }
            if (envelope.MessageType == ProtocolMessageType.GuestProfileApplied)
            {
                // P0-2：客户端已应用 Host GuestProfile 权威背包 → 开放该 peer 的 inventory 漂移收敛（此后上报内容才允许更新 shadow）。
                runtime.Players.MarkInventoryBootstrapReady(peer.PeerId);
                if (runtime.AutoSelfTest) runtime.RunInventoryBootstrapSelfCheck(peer.PeerId);
                runtime.log?.LogInfo($"[INV-BOOTSTRAP] peer={peer.PeerId} GuestProfile 已应用并 ack → inventory bootstrap complete，此后 drift report 才可更新 shadow 内容。");
                return;
            }
            if (envelope.MessageType == ProtocolMessageType.PlayerInventoryState)
            {
                // 客户端真实背包上报（漂移收敛）：重建该玩家的权威影子背包
                var state = ReplicationProtocolCodec.DecodePlayerInventoryState(envelope.Payload);
                // P0-2：bootstrap 门——Host GuestProfile seed 应用 + 客户端 ack 之前，上报只取真实容量（topology），
                // 绝不允许客户端旧存档/本地单机背包内容覆盖 Host 权威 shadow。
                if (!runtime.Players.IsInventoryBootstrapReady(peer.PeerId))
                {
                    runtime.Players.RebuildInventoryTopologyOnly(peer.PeerId, state);
                    runtime.log?.LogInfo($"[INV-BOOTSTRAP] ignored client inventory content before host seed（peer {peer.PeerId}，仅容量 backpack={state.Backpack.Length}/hotbar={state.Hotbar.Length}）");
                    return;
                }
                if (runtime.Players.RebuildInventory(peer.PeerId, state))
                    runtime.log?.LogInfo($"[INV-BOOTSTRAP] 主机已按客户端真实背包重建影子（gate open）：玩家 {peer.PeerId}。");
                return;
            }
            var inventory = ReplicationProtocolCodec.DecodeInventoryState(envelope.Payload);
            var id = new EntityId(inventory.Value, inventory.Persistent);
            // v0.9.2 P0-9：客户端接收 Host 广播的 InventoryState 时，apply 后 seed 自己的 clientContainerRevisions（防止下次 commit 用错 base）
            if (!runtime.IsHost)
            {
                if (runtime.replication.Apply(inventory))
                {
                    runtime.SeedClientContainerRevision(id, (uint)inventory.Revision);
                    runtime.SyncHealth.IncEntityDeltaRecv();
                }
                return;
            }
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
                foreach (var readyPeer in runtime.ReadyPeersSnapshot) runtime.Queue(readyPeer, ProtocolMessageType.InventoryState, envelope.Payload);
                runtime.log?.LogInfo($"主机已应用客户端容器状态并转发：ID={inventory.Value:X16}，玩家 {peer.PeerId}，版本 {inventory.Revision}，槽位 {inventory.Slots.Length}。");
            }
        }
    }

    private sealed class HostCommitHandlers : INetworkMessageHandler
    {
        private readonly DarkwoodAdapterRuntime runtime;
        public HostCommitHandlers(DarkwoodAdapterRuntime runtime) => this.runtime = runtime;

        public bool Handles(ProtocolMessageType type) =>
            runtime.IsHost && (type == ProtocolMessageType.InventoryCommit || type == ProtocolMessageType.InventoryTransactionCommit || type == ProtocolMessageType.ContainerCommit || type == ProtocolMessageType.PickupCommit || type == ProtocolMessageType.DropCommit || type == ProtocolMessageType.RemoveWorldItem);

        public void Handle(PeerContext peer, ProtocolEnvelope envelope)
        {
            try
            {
                if (envelope.MessageType == ProtocolMessageType.InventoryCommit) HandleInventoryCommit(peer, ReplicationProtocolCodec.DecodeInventoryCommit(envelope.Payload));
                else if (envelope.MessageType == ProtocolMessageType.InventoryTransactionCommit) HandleInventoryTransactionCommit(peer, ReplicationProtocolCodec.DecodeInventoryTransactionCommit(envelope.Payload));
                else if (envelope.MessageType == ProtocolMessageType.ContainerCommit) HandleContainerCommitLegacy(peer, ReplicationProtocolCodec.DecodeContainerCommit(envelope.Payload));
                else if (envelope.MessageType == ProtocolMessageType.PickupCommit) HandlePickupCommit(peer, ReplicationProtocolCodec.DecodePickupCommit(envelope.Payload));
                else if (envelope.MessageType == ProtocolMessageType.DropCommit) HandleDropCommit(peer, ReplicationProtocolCodec.DecodeDropCommit(envelope.Payload));
                else if (envelope.MessageType == ProtocolMessageType.RemoveWorldItem)
                {
                    var m = ReplicationProtocolCodec.DecodeRemoveWorldItem(envelope.Payload);
                    runtime.HandleRemoveWorldItemRequest(peer.PeerId, new Core.EntityId(m.EntityValue, m.Persistent));
                }
            }
            catch (Exception error) { runtime.log?.LogWarning($"[COMMIT] handle failed peer={peer.PeerId} type={envelope.MessageType}: {error.Message}"); }
        }

        private int CanonicalPlayer(int peerId, int claimedPlayerId)
        {
            if (claimedPlayerId != peerId) { /* warning logged in caller */ }
            return peerId;
        }

        private readonly Dictionary<int, int> lastAcceptedPlayerInventoryRevision = new Dictionary<int, int>();
        private int GetLastAcceptedPlayerRev(int pid) { int v; return lastAcceptedPlayerInventoryRevision.TryGetValue(pid, out v) ? v : 0; }

        // 纯玩家背包 Commit：仅 Revision 单调门控；Host Rebuild shadow 并广播其他 Client。
        private void HandleInventoryCommit(PeerContext peer, InventoryCommitMessage msg)
        {
            var pid = CanonicalPlayer(peer.PeerId, msg.PlayerId);
            if (pid != msg.PlayerId) runtime.log?.LogWarning($"[INV-COMMIT] peer={peer.PeerId} msg.PlayerId={msg.PlayerId} 不一致，使用 peer.PeerId={pid}。");
            int last; lastAcceptedPlayerInventoryRevision.TryGetValue(pid, out last);
            if (msg.Revision <= last) { runtime.log?.LogInfo($"[INV-COMMIT] 丢弃迟到旧包 player={pid} staleRev={msg.Revision} lastAccepted={last}"); return; }
            lastAcceptedPlayerInventoryRevision[pid] = msg.Revision;
            runtime.Players.RebuildInventoryFromSnapshot(pid, msg.Backpack, msg.Hotbar, msg.Revision);
            var payloadBytes = ReplicationProtocolCodec.Encode(new PlayerInventoryStatePayload(msg.Backpack, msg.Hotbar, msg.Revision, pid));
            foreach (var rp in runtime.ReadyPeersSnapshot) if (rp != peer.PeerId) runtime.Queue(rp, ProtocolMessageType.PlayerInventoryState, payloadBytes);
            runtime.log?.LogInfo($"[INV-COMMIT] peer={peer.PeerId} player={pid} rev={msg.Revision} accepted → Rebuild shadow + 广播其他 Client。");
        }

        // v0.9.2 P0-6：原子事务 commit。Host 先校验所有 container baseRevision → 全部一致才 Apply；任一 conflict → 整个事务 Reconcile。
        private void HandleInventoryTransactionCommit(PeerContext peer, InventoryTransactionCommitMessage msg)
        {
            var pid = CanonicalPlayer(peer.PeerId, msg.PlayerId);
            if (pid != msg.PlayerId) runtime.log?.LogWarning($"[TX-RECV] peer={peer.PeerId} msg.PlayerId={msg.PlayerId} 不一致，使用 peer.PeerId={pid}。");
            runtime.SyncHealth.IncContainerTxRecv(msg.ContainerMutations.Length);

            // 第一阶段：所有 baseRevision 校验（不 Apply）
            var acceptedContainers = new List<ContainerReconcileMessage>(msg.ContainerMutations.Length);
            var conflicts = new List<ContainerReconcileMessage>();
            var appliedEntities = new List<EntityId>(msg.ContainerMutations.Length);
            for (var i = 0; i < msg.ContainerMutations.Length; i++)
            {
                var cm = msg.ContainerMutations[i];
                var cid = new EntityId(cm.ContainerValue, cm.ContainerPersistent);
                if (!runtime.replication.TryGetInventory(cid, out var container) || container == null)
                {
                    runtime.log?.LogWarning($"[TX-CONFLICT] peer={peer.PeerId} container=0x{cm.ContainerValue:X8} base={cm.BaseRevision} reason=ENTITY_NOT_FOUND");
                    if (!runtime.replication.TryCaptureAuthoritativeInventory(cid, out var canonical)) canonical = new InventoryStateMessage(cm.ContainerValue, cm.ContainerPersistent, 0, Array.Empty<InventorySlotWire>());
                    conflicts.Add(new ContainerReconcileMessage(msg.TransactionId, cm.ContainerValue, cm.ContainerPersistent, (uint)canonical.Revision, canonical.Slots));
                    continue;
                }
                uint hostRev = runtime.replication.GetContainerRevision(cid);
                if (hostRev == 0) { hostRev = runtime.replication.EnsureContainerRevision(cid); }
                if (cm.BaseRevision != hostRev)
                {
                    runtime.log?.LogWarning($"[TX-CONFLICT] peer={peer.PeerId} container=0x{cm.ContainerValue:X8} baseRev={cm.BaseRevision} hostRev={hostRev} reason=REVISION_MISMATCH");
                    if (runtime.replication.TryCaptureAuthoritativeInventory(cid, out var canonical)) conflicts.Add(new ContainerReconcileMessage(msg.TransactionId, cm.ContainerValue, cm.ContainerPersistent, (uint)canonical.Revision, canonical.Slots));
                    else conflicts.Add(new ContainerReconcileMessage(msg.TransactionId, cm.ContainerValue, cm.ContainerPersistent, hostRev, Array.Empty<InventorySlotWire>()));
                    continue;
                }
                acceptedContainers.Add(new ContainerReconcileMessage(msg.TransactionId, cm.ContainerValue, cm.ContainerPersistent, 0, cm.Slots)); // newRevision 稍后写
                appliedEntities.Add(cid);
            }

            if (conflicts.Count > 0)
            {
                // 整事务回滚——不 Apply 任何 container
                runtime.SyncHealth.IncContainerTxConflict(conflicts.Count);
                // 玩家背包也回滚
                if (msg.Backpack != null && msg.Hotbar != null && msg.Backpack.Length > 0)
                {
                    if (msg.PlayerInventoryRevision > GetLastAcceptedPlayerRev(pid))
                    {
                        runtime.Players.RebuildInventoryFromSnapshot(pid, msg.Backpack, msg.Hotbar, msg.PlayerInventoryRevision);
                        lastAcceptedPlayerInventoryRevision[pid] = msg.PlayerInventoryRevision;
                    }
                }
                // 推 TransactionReconcile 给发起方（携带真实 EntityId，不再 EntityId=0）
                var reconcile = new TransactionReconcileMessage(msg.TransactionId, pid, msg.PlayerInventoryRevision, msg.Backpack, msg.Hotbar, conflicts.ToArray());
                runtime.Queue(peer.PeerId, ProtocolMessageType.TransactionReconcile, ReplicationProtocolCodec.Encode(reconcile));
                runtime.log?.LogWarning($"[TX-RECONCILE] peer={peer.PeerId} tid={msg.TransactionId} conflicts={conflicts.Count}/{msg.ContainerMutations.Length} → 整事务回滚，已广播 TransactionReconcile。");
                return;
            }

            // 第二阶段：全部通过 → Apply 玩家背包 + 所有 containers，并发 ContainerCommitAck 给发起方
            if (msg.Backpack != null && msg.Hotbar != null && msg.Backpack.Length > 0)
            {
                if (msg.PlayerInventoryRevision > GetLastAcceptedPlayerRev(pid))
                {
                    lastAcceptedPlayerInventoryRevision[pid] = msg.PlayerInventoryRevision;
                    runtime.Players.RebuildInventoryFromSnapshot(pid, msg.Backpack, msg.Hotbar, msg.PlayerInventoryRevision);
                }
            }
            var containerBroadcasts = new List<InventoryStateMessage>(msg.ContainerMutations.Length);
            var containerAcks = new List<ContainerCommitAckMessage>(msg.ContainerMutations.Length);
            for (var i = 0; i < msg.ContainerMutations.Length; i++)
            {
                var cm = msg.ContainerMutations[i];
                var cid = new EntityId(cm.ContainerValue, cm.ContainerPersistent);
                if (!runtime.replication.TryGetInventory(cid, out var container) || container == null) continue;
                var slots = new DarkwoodInventorySlot[cm.Slots.Length];
                for (var k = 0; k < slots.Length; k++) { var s = cm.Slots[k]; slots[k] = new DarkwoodInventorySlot { Type = s.Type, Amount = s.Amount, Durability = s.Durability, Quality = s.Quality, Recipe = s.Recipe }; }
                try
                {
                    DarkwoodInventoryAdapter.Apply(container, slots);
                    try { container.refreshItems(); } catch (Exception) { }
                    uint newRev = runtime.replication.IncrementContainerRevision(cid);
                    var canonical = runtime.replication.CaptureAuthoritativeInventoryWithRevision(cid, newRev);
                    containerBroadcasts.Add(canonical);
                    containerAcks.Add(new ContainerCommitAckMessage(msg.TransactionId, cm.ContainerValue, cm.ContainerPersistent, newRev));
                    runtime.log?.LogInfo($"[TX-ACCEPT] peer={peer.PeerId} container=0x{cm.ContainerValue:X8} {cm.BaseRevision}→{newRev} source=ClientCommit({pid})");
                }
                catch (Exception error) { runtime.log?.LogWarning($"[TX-APPLY-FAIL] container=0x{cm.ContainerValue:X8} {error.Message}"); }
            }
            // 广播权威 container state 给所有 Client（含发起方——发起方通过 ContainerCommitAck 更新 cache，广播保证一致性）
            foreach (var cs in containerBroadcasts)
            {
                var bytes = ReplicationProtocolCodec.Encode(cs);
                foreach (var rp in runtime.ReadyPeersSnapshot) runtime.Queue(rp, ProtocolMessageType.InventoryState, bytes);
            }
            // ContainerCommitAck 给发起方：每个容器一条 ack，让 clientContainerRevisions 同步
            foreach (var ack in containerAcks) runtime.Queue(peer.PeerId, ProtocolMessageType.ContainerCommitAck, ReplicationProtocolCodec.Encode(ack));
            runtime.SyncHealth.IncContainerTxAccepted(acceptedContainers.Count);
            runtime.log?.LogInfo($"[TX-COMMIT-OK] peer={peer.PeerId} tid={msg.TransactionId} containers={acceptedContainers.Count} → 原子 Apply 完成 + Acks + 广播。");
        }

        // 兼容旧 ContainerCommit（已被 InventoryTransactionCommit 替代；保留 codec 防遗留客户端）
        private void HandleContainerCommitLegacy(PeerContext peer, ContainerCommitMessage msg)
        {
            var pid = CanonicalPlayer(peer.PeerId, msg.PlayerId);
            runtime.log?.LogWarning($"[LEGACY-CONTAINER-COMMIT] peer={peer.PeerId} 仍发送旧 ContainerCommit——应改用 InventoryTransactionCommit。");
            var cid = new EntityId(msg.ContainerValue, msg.ContainerPersistent);
            if (!runtime.replication.TryGetInventory(cid, out var container) || container == null) return;
            uint hostRev = runtime.replication.GetContainerRevision(cid);
            if (hostRev == 0) hostRev = runtime.replication.EnsureContainerRevision(cid);
            if ((uint)msg.BaseContainerRevision != hostRev)
            {
                runtime.log?.LogWarning($"[LEGACY-CONFLICT] peer={peer.PeerId} container=0x{msg.ContainerValue:X8} baseRev={msg.BaseContainerRevision} hostRev={hostRev}");
                if (runtime.replication.TryCaptureAuthoritativeInventory(cid, out var canonical))
                {
                    var recon = new ContainerReconcileMessage(msg.TransactionId, msg.ContainerValue, msg.ContainerPersistent, (uint)canonical.Revision, canonical.Slots);
                    runtime.Queue(peer.PeerId, ProtocolMessageType.ContainerReconcile, ReplicationProtocolCodec.Encode(recon));
                }
                return;
            }
            var slots = new DarkwoodInventorySlot[msg.ContainerSlots.Length];
            for (var i = 0; i < slots.Length; i++) { var s = msg.ContainerSlots[i]; slots[i] = new DarkwoodInventorySlot { Type = s.Type, Amount = s.Amount, Durability = s.Durability, Quality = s.Quality, Recipe = s.Recipe }; }
            DarkwoodInventoryAdapter.Apply(container, slots);
            try { container.refreshItems(); } catch (Exception) { }
            uint newRev = runtime.replication.IncrementContainerRevision(cid);
            var broadcast = runtime.replication.CaptureAuthoritativeInventoryWithRevision(cid, newRev);
            var bytes = ReplicationProtocolCodec.Encode(broadcast);
            foreach (var rp in runtime.ReadyPeersSnapshot) runtime.Queue(rp, ProtocolMessageType.InventoryState, bytes);
            runtime.Queue(peer.PeerId, ProtocolMessageType.ContainerCommitAck, ReplicationProtocolCodec.Encode(new ContainerCommitAckMessage(msg.TransactionId, msg.ContainerValue, msg.ContainerPersistent, newRev)));
            if (msg.BackpackAfter != null && msg.BackpackAfter.Length > 0)
            {
                if (msg.PlayerInventoryRevision > GetLastAcceptedPlayerRev(pid))
                {
                    lastAcceptedPlayerInventoryRevision[pid] = msg.PlayerInventoryRevision;
                    runtime.Players.RebuildInventoryFromSnapshot(pid, msg.BackpackAfter, msg.HotbarAfter, msg.PlayerInventoryRevision);
                }
            }
        }

        private void HandlePickupCommit(PeerContext peer, PickupCommitMessage msg)
        {
            var pid = CanonicalPlayer(peer.PeerId, msg.PlayerId);
            var rid = new EntityId(msg.RuntimeEntityId, msg.Persistent);
            var ent = runtime.RuntimeEntities;
            bool entityExisted = false;
            if (ent != null && ent.TryGetRuntimeEntity(rid, out _)) entityExisted = true;
            runtime.SyncHealth.IncPickupCommitRecv();
            runtime.log?.LogInfo($"[PICKUP-COMMIT-RECV] peer={peer.PeerId} runtime=0x{rid.Value:X8} type={msg.ItemType} x{msg.Amount} rev={msg.PlayerInventoryRevision}");
            if (entityExisted)
            {
                // v0.9.6（r14）修：客户端已把该掉落物取走，权威世界必须同步移除本体——
                // 旧实现只 BroadcastDespawn（registry.Remove + 广播），从不销毁主机场景里的本体 GameObject，
                // 造成「客户端捡走后主机实体永久残留」。仅 runtime 实体（!Persistent）走销毁；
                // persistent 世界物品仍走既有 legacy 移除链路。
                UnityEngine.GameObject hostRoot = null;
                if (!msg.Persistent)
                {
                    try { if (runtime.replication.TryGetBinding(rid, out var b) && b.Root != null) hostRoot = b.Root; } catch (Exception) { }
                }
                ent.BroadcastDespawn(msg.RuntimeEntityId, RuntimeEntityDespawnReason.Collected);
                runtime.replication.UnregisterRuntimeEntity(rid);
                if (hostRoot != null)
                {
                    try { UnityEngine.Object.Destroy(hostRoot); }
                    catch (Exception error) { runtime.log?.LogWarning($"[PICKUP-DESPAWN] 主机本体销毁失败（已 despawn，不影响）：{error.Message}"); }
                }
                if (msg.PlayerInventoryRevision > GetLastAcceptedPlayerRev(pid))
                {
                    lastAcceptedPlayerInventoryRevision[pid] = msg.PlayerInventoryRevision;
                    runtime.Players.RebuildInventoryFromSnapshot(pid, msg.BackpackAfter, msg.HotbarAfter, msg.PlayerInventoryRevision);
                }
                runtime.log?.LogInfo($"[PICKUP-DESPAWN] peer={peer.PeerId} runtime=0x{rid.Value:X8} → Despawn + 主机本体{(hostRoot != null ? "已销毁" : "（无本体/持久物品）")} + Rebuild shadow。");
            }
            else
            {
                runtime.log?.LogWarning($"[PICKUP-RECONCILE] peer={peer.PeerId} runtime=0x{rid.Value:X8} 已不存在（race）→ 推权威玩家背包。");
                if (runtime.Players.TryGetInventory(pid, out var shadow))
                {
                    var snap = shadow.CaptureState(pid);
                    runtime.Queue(peer.PeerId, ProtocolMessageType.PickupReconcile, ReplicationProtocolCodec.Encode(snap));
                }
            }
        }

        private struct DropTokenKey : System.IEquatable<DropTokenKey> { public int PeerId; public ulong Token; public DropTokenKey(int p, ulong t){PeerId=p;Token=t;} public bool Equals(DropTokenKey o)=>PeerId==o.PeerId&&Token==o.Token; public override bool Equals(object o)=>o is DropTokenKey k&&Equals(k); public override int GetHashCode()=>PeerId.GetHashCode()^Token.GetHashCode(); }
        private readonly Dictionary<DropTokenKey, int> localDropTokenSeen = new Dictionary<DropTokenKey, int>();
        private void HandleDropCommit(PeerContext peer, DropCommitMessage msg)
        {
            var pid = CanonicalPlayer(peer.PeerId, msg.PlayerId);
            var key = new DropTokenKey(pid, msg.LocalDropToken);
            int last; if (localDropTokenSeen.TryGetValue(key, out last))
            {
                runtime.log?.LogInfo($"[DROP-COMMIT-DUP] peer={pid} token=0x{msg.LocalDropToken:X8} 已处理过（last rev {last}），忽略重复。");
                return;
            }
            localDropTokenSeen[key] = msg.PlayerInventoryRevision;
            runtime.SyncHealth.IncDropCommitRecv();
            runtime.log?.LogInfo($"[DROP-COMMIT-RECV] peer={pid} token=0x{msg.LocalDropToken:X8} type={msg.ItemType} x{msg.Amount} rev={msg.PlayerInventoryRevision}");
            var ent = runtime.RuntimeEntities;
            var rid = ent != null ? ent.CreateAndRegisterFromDropCommit(msg, pid) : default(EntityId);
            if (rid.Value == 0) return;
            if (msg.BackpackAfter != null && msg.HotbarAfter != null && msg.BackpackAfter.Length > 0)
            {
                if (msg.PlayerInventoryRevision > GetLastAcceptedPlayerRev(pid))
                {
                    lastAcceptedPlayerInventoryRevision[pid] = msg.PlayerInventoryRevision;
                    runtime.Players.RebuildInventoryFromSnapshot(pid, msg.BackpackAfter, msg.HotbarAfter, msg.PlayerInventoryRevision);
                }
            }
            var ack = new DropCommitAckMessage(msg.LocalDropToken, rid.Value, rid.IsPersistent);
            runtime.Queue(peer.PeerId, ProtocolMessageType.DropCommitAck, ReplicationProtocolCodec.Encode(ack));
            runtime.log?.LogInfo($"[DROP-SPAWN] peer={pid} token=0x{msg.LocalDropToken:X8} runtime=0x{rid.Value:X8} → Spawn + DropCommitAck。");
        }
    }

    private sealed class ClientTestHandlers : INetworkMessageHandler
    {
        private readonly DarkwoodAdapterRuntime runtime;
        public ClientTestHandlers(DarkwoodAdapterRuntime runtime) => this.runtime = runtime;

        // TestControl：TestMode 才注册（普通客户端不会收到 TestControl=200）
        public bool Handles(ProtocolMessageType type) => type == ProtocolMessageType.TestControl && runtime.TestAgent != null;

        public void Handle(PeerContext peer, ProtocolEnvelope envelope)
        {
            var agent = runtime.TestAgent;
            if (agent == null) return;
            var msg = ReplicationProtocolCodec.DecodeTestControl(envelope.Payload);
            agent.Trace.Log("TEST-IN", $"kind={msg.Kind} phase={msg.Phase}");
            agent.EnqueueTestControl(msg);
            runtime.TestCoordinator?.OnTestControl(msg);
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
    private sealed class ClientCommitHandlers : INetworkMessageHandler
    {
        private readonly DarkwoodAdapterRuntime runtime;
        public ClientCommitHandlers(DarkwoodAdapterRuntime runtime) => this.runtime = runtime;

        public bool Handles(ProtocolMessageType type) =>
            !runtime.IsHost && (type == ProtocolMessageType.DropCommitAck || type == ProtocolMessageType.PickupReconcile || type == ProtocolMessageType.ContainerCommitAck || type == ProtocolMessageType.ContainerReconcile || type == ProtocolMessageType.TransactionReconcile);

        public void Handle(PeerContext peer, ProtocolEnvelope envelope)
        {
            try
            {
                if (envelope.MessageType == ProtocolMessageType.DropCommitAck)
                {
                    var ack = ReplicationProtocolCodec.DecodeDropCommitAck(envelope.Payload);
                    var inv = runtime.TakePendingLocalDropByToken(ack.LocalDropToken, null);
                    if (inv == null) { runtime.log?.LogWarning($"[DROP-ACK] token=0x{ack.LocalDropToken:X8} 未找到本地待复用对象（可能已超时清理）。"); return; }
                    var rid = new EntityId(ack.RuntimeEntityId, ack.Persistent);
                    var item = inv.GetComponentInChildren<Item>(true);
                    runtime.replication.RegisterBinding(new WorldEntityBinding { Id = rid, Root = inv.gameObject, Primary = inv, Inventory = inv, Item = item, Kind = WorldEntityKind.DroppedItem });
                    runtime.log?.LogInfo($"[DROP-MIRROR-BIND] token=0x{ack.LocalDropToken:X8} → 复用本地对象为 mirror runtime=0x{ack.RuntimeEntityId:X8}");
                    runtime.SyncHealth.IncDropCommitSent(); // 计数 drop commit 流转
                }
                else if (envelope.MessageType == ProtocolMessageType.PickupReconcile)
                {
                    var state = ReplicationProtocolCodec.DecodePlayerInventoryState(envelope.Payload);
                    ApplyPlayerInventory(state);
                    runtime.log?.LogWarning($"[PICKUP-RECONCILE] 收到 Host 权威玩家背包 rev={state.Revision}（race 收敛）。");
                }
                else if (envelope.MessageType == ProtocolMessageType.ContainerCommitAck)
                {
                    var ack = ReplicationProtocolCodec.DecodeContainerCommitAck(envelope.Payload);
                    var cid = new EntityId(ack.ContainerValue, ack.ContainerPersistent);
                    // v0.9.2 P0-1/P0-4：客户端 cache 同步到 Host 权威 newRevision，下次 commit 用它作 base
                    runtime.SeedClientContainerRevision(cid, ack.NewRevision);
                    runtime.log?.LogInfo($"[TX-ACK] container=0x{ack.ContainerValue:X8} rev={ack.NewRevision} → clientContainerRevisions 已同步。");
                    runtime.SyncHealth.IncContainerTxAck();
                }
                else if (envelope.MessageType == ProtocolMessageType.ContainerReconcile)
                {
                    var recon = ReplicationProtocolCodec.DecodeContainerReconcile(envelope.Payload);
                    var cid = new EntityId(recon.ContainerValue, recon.ContainerPersistent);
                    // v0.9.2 P0-3/P0-4：apply 权威容器状态 + 同步 clientContainerRevisions；Apply 进入 ApplyingRemote
                    var state = new InventoryStateMessage(recon.ContainerValue, recon.ContainerPersistent, recon.CanonicalRevision, recon.CanonicalSlots);
                    runtime.replication.Apply(state);
                    runtime.SeedClientContainerRevision(cid, recon.CanonicalRevision);
                    runtime.log?.LogWarning($"[CONTAINER-RECONCILE] container=0x{recon.ContainerValue:X8} canonicalRev={recon.CanonicalRevision} → apply 权威 + cache 同步。");
                    runtime.SyncHealth.IncContainerTxReconcile();
                }
                else if (envelope.MessageType == ProtocolMessageType.TransactionReconcile)
                {
                    var tr = ReplicationProtocolCodec.DecodeTransactionReconcile(envelope.Payload);
                    runtime.log?.LogWarning($"[TRANSACTION-RECONCILE] tid={tr.TransactionId} containers={tr.Containers.Length} playerRev={tr.PlayerInventoryRevision} → 整事务回滚（ApplyingRemote 下 apply）。");
                    foreach (var c in tr.Containers)
                    {
                        var cid = new EntityId(c.ContainerValue, c.ContainerPersistent);
                        var state = new InventoryStateMessage(c.ContainerValue, c.ContainerPersistent, c.CanonicalRevision, c.CanonicalSlots);
                        runtime.replication.Apply(state);
                        runtime.SeedClientContainerRevision(cid, c.CanonicalRevision);
                    }
                    // 玩家背包权威快照：写 PlayerInventoryState（不是 InventoryState）让 ApplyPlayerInventory 走 revision 门控
                    var piState = new PlayerInventoryStatePayload(tr.Backpack, tr.Hotbar, tr.PlayerInventoryRevision, tr.PlayerId);
                    ApplyPlayerInventory(piState);
                    runtime.SyncHealth.IncContainerTxReconcile();
                }
            }
            catch (Exception error) { runtime.log?.LogWarning($"[CLIENT-COMMIT] handle failed type={envelope.MessageType}: {error.Message}"); }
        }
    }

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
            type == ProtocolMessageType.RuntimeEntitySpawn || type == ProtocolMessageType.RuntimeEntityDespawn ||
            type == ProtocolMessageType.ActionExecuted;

        public void Handle(PeerContext peer, ProtocolEnvelope envelope)
        {
            switch (envelope.MessageType)
            {
                case ProtocolMessageType.EntityDelta:
                {
                    try
                    {
                        var delta = ReplicationProtocolCodec.DecodeEntityDelta(envelope.Payload);
                        if (runtime.clientSession?.Session.Lifecycle.State == ConnectionState.Ready && delta.Scene == runtime.CurrentScene)
                        {
                            runtime.replication.Apply(delta.Entities, false);
                            runtime.replication.ApplyDespawns(delta.Despawns);
                        }
                    }
                    catch (Exception error) { runtime.log?.LogError($"客户端 EntityDelta 处理异常（已隔离，P0-3）：{error}"); }
                    break;
                }
                case ProtocolMessageType.InventoryState:
                {
                    try
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
                    }
                    catch (Exception error) { runtime.log?.LogError($"客户端 InventoryState 处理异常（已隔离，P0-3）：{error}"); }
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
                case ProtocolMessageType.ActionExecuted:
                {
                    // v0.9.0 Action Sync：Host 已执行的原版副作用 → 各端 Replay（含去重）。
                    try { runtime.Actions?.HandleReceived(runtime, envelope.Payload); }
                    catch (Exception error) { runtime.log?.LogError($"客户端 ActionExecuted 处理异常（已隔离）：{error}"); }
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
            type == ProtocolMessageType.Ready || type == ProtocolMessageType.Error ||
            type == ProtocolMessageType.PlayerAction;

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
                    // P0-2：应用 Host 权威背包后——清本机残留/stale cursor（含旧存档牵连的 GUI），
                    // 上报真实容量拓扑，然后 ack 告诉 Host"GuestProfile 已应用"（开放 bootstrap 门）。
                    try { DarkwoodAdapterRuntime.ClearHeldItem(); var pl = global::Player.Instance; if (pl != null) pl.refreshRecipes(); } catch (Exception) { }
                    try
                    {
                        var topo = DarkwoodWorldAuthorityService.CaptureLocalPlayerInventory();
                        if (runtime.clientSession != null) runtime.clientSession.Send(ProtocolMessageType.PlayerInventoryState, ReplicationProtocolCodec.Encode(topo));
                        runtime.log?.LogInfo($"[INV-BOOTSTRAP] GuestProfile 应用后上报容量 topology：backpack {topo.Backpack.Length} / hotbar {topo.Hotbar.Length}。");
                    }
                    catch (Exception) { }
                    if (runtime.clientSession != null)
                    {
                        runtime.clientSession.Send(ProtocolMessageType.GuestProfileApplied, Array.Empty<byte>());
                        runtime.log?.LogInfo("[INV-BOOTSTRAP] GuestProfile 已应用并 ack → 本轮联机背包以 Host 权威为准（不携带客户端旧存档）。");
                    }
                    break;
                }
                case ProtocolMessageType.PlayerAction:
                {
                    var pa = ReplicationProtocolCodec.DecodePlayerAction(envelope.Payload);
                    runtime.log?.LogInfo($"[EVENT] playerAction recv: {pa.Action}（其他玩家动作事件——播放钩子在此扩展）");
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
                    // P0-A：进入 Ready 的瞬间补发一次真实背包拓扑（含全槽容量）——保证 Host 在第一笔物品交互前 InventoryTopologyReady=true，
                    // 不再依赖"某次漂移上报碰运气"，杜绝 INVALID_TARGET_SLOT / 猜容量。
                    try {
                        var runtime2 = runtime;
                        var pl = global::Player.Instance;
                        if (runtime2.clientSession != null && pl != null && pl.Inventory != null && pl.Inventory.slots != null && pl.Hotbar != null)
                        {
                            var topo = DarkwoodWorldAuthorityService.CaptureLocalPlayerInventory();
                            runtime2.clientSession.Send(ProtocolMessageType.PlayerInventoryState, ReplicationProtocolCodec.Encode(topo));
                            runtime.log?.LogInfo($"[PLAYER-INV] topology 上报（Ready 门）：backpack {topo.Backpack.Length} / hotbar {topo.Hotbar.Length}。");
                        }
                    } catch (Exception error) { runtime.log?.LogWarning($"[PLAYER-INV] Ready 门拓扑上报失败：{error.Message}"); }
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
