using System;
using System.Collections.Generic;
using System.IO;
using DarkwoodMultiplayerFramework.Core;
using DarkwoodMultiplayerFramework.Network;
using DarkwoodMultiplayerFramework.Protocol;

namespace DarkwoodMultiplayerFramework.DarkwoodAdapter;

/// <summary>
/// 所有权拆分 + 收口：存档/快照传输服务——拥有传输状态与就绪标志，
/// 并以行为方法暴露（Begin/Accept/Finish/Mark），禁止外部直接改字段。
/// </summary>
public sealed class DarkwoodSaveTransferService
{
    private readonly DarkwoodAdapterRuntime runtime;

    private readonly Dictionary<int, Guid> sentSaves = new Dictionary<int, Guid>();
    private readonly Dictionary<int, Guid> sentSnapshots = new Dictionary<int, Guid>();
    private readonly Dictionary<int, ReadyMessage> pendingSnapshotRequests = new Dictionary<int, ReadyMessage>();
    private ChunkTransferAssembler? incomingSave;
    private SaveTransferManifest incomingSaveManifest;
    private ChunkTransferAssembler? incomingSnapshot;
    private WorldSnapshotManifest incomingSnapshotManifest;
    private bool clientSnapshotReady;
    private bool clientRegistryRequestSent;
    private bool clientSnapshotManifestReceived;
    private bool clientRegistryStabilized;
    private float loadStartedAt;
    private int lastLoadBucket;
    private bool clientSaveLoadPending;
    private float nextRegistryRequestRetry;
    private WorldSnapshotApplied? lastSnapshotApplied;
    private float nextSnapshotAckRetry;
    private int snapshotAckRetryCount;
    private string transferProgressValue = string.Empty;

    public DarkwoodSaveTransferService(DarkwoodAdapterRuntime runtime) => this.runtime = runtime;

    public string TransferProgressValue => transferProgressValue;
    public void SetProgress(string value) => transferProgressValue = value;

    // ── 主机：发送登记 ──
    public void SetSentSave(int peer, Guid transferId) => sentSaves[peer] = transferId;
    public bool TryGetSentSave(int peer, out Guid transferId) => sentSaves.TryGetValue(peer, out transferId);
    public void SetSentSnapshot(int peer, Guid snapshotId) => sentSnapshots[peer] = snapshotId;
    public bool TryGetSentSnapshot(int peer, out Guid snapshotId) => sentSnapshots.TryGetValue(peer, out snapshotId);
    public void SetPendingSnapshotRequest(int peer, ReadyMessage ready) => pendingSnapshotRequests[peer] = ready;
    public bool TryGetPendingSnapshotRequest(int peer, out ReadyMessage ready) => pendingSnapshotRequests.TryGetValue(peer, out ready);
    public KeyValuePair<int, ReadyMessage>[] DrainPendingSnapshotRequests()
    {
        var array = new KeyValuePair<int, ReadyMessage>[pendingSnapshotRequests.Count];
        ((ICollection<KeyValuePair<int, ReadyMessage>>)pendingSnapshotRequests).CopyTo(array, 0);
        pendingSnapshotRequests.Clear();
        return array;
    }
    public int PendingSnapshotRequestCount => pendingSnapshotRequests.Count;

    // ── 客户端：存档接收 ──
    public void BeginSaveReceive(SaveTransferManifest manifest)
    {
        incomingSaveManifest = manifest;
        incomingSave = new ChunkTransferAssembler(manifest.TransferId, manifest.TotalBytes, manifest.ChunkCount, manifest.Sha256);
        transferProgressValue = $"正在接收存档：0/{manifest.ChunkCount}（0%）";
    }

    /// <summary>接收一个数据块。返回 true 表示装配完成（调用方应 FinishSaveReceive）。</summary>
    public bool AcceptSaveChunk(SaveTransferChunk chunk)
    {
        if (incomingSave == null) throw new InvalidDataException("存档数据块早于存档清单到达。");
        incomingSave.Add(chunk.TransferId, chunk.Index, chunk.Total, chunk.Data, chunk.Hash);
        transferProgressValue = $"正在接收存档：{incomingSave.ReceivedChunks}/{incomingSave.ChunkCount}（{(int)(incomingSave.ReceivedChunks * 100f / incomingSave.ChunkCount)}%）";
        return incomingSave.IsComplete;
    }

    public byte[] FinishSaveReceive()
    {
        transferProgressValue = "正在校验并安装存档";
        var data = incomingSave!.Build();
        incomingSave = null;
        return data;
    }

    public SaveTransferManifest PendingSaveManifest => incomingSaveManifest;

    // ── 客户端：快照接收 ──
    public void BeginSnapshotReceive(WorldSnapshotManifest manifest)
    {
        if (clientSnapshotManifestReceived) return;
        incomingSnapshotManifest = manifest;
        incomingSnapshot = new ChunkTransferAssembler(manifest.SnapshotId, manifest.TotalBytes, manifest.ChunkCount, manifest.Sha256, 64L * 1024 * 1024);
        clientRegistryRequestSent = true;
        clientSnapshotManifestReceived = true;
        transferProgressValue = $"正在接收世界快照：0/{manifest.ChunkCount}（0%）";
    }

    /// <summary>接收一个数据块。返回 true 表示装配完成（调用方应 FinishSnapshotReceive）。</summary>
    public bool AcceptSnapshotChunk(WorldSnapshotChunk chunk)
    {
        if (incomingSnapshot == null) throw new InvalidDataException("World snapshot chunk arrived before manifest.");
        incomingSnapshot.Add(chunk.SnapshotId, chunk.Index, chunk.Total, chunk.Data, chunk.Hash);
        transferProgressValue = $"正在接收世界快照：{incomingSnapshot.ReceivedChunks}/{incomingSnapshot.ChunkCount}（{(int)(incomingSnapshot.ReceivedChunks * 100f / incomingSnapshot.ChunkCount)}%）";
        return incomingSnapshot.IsComplete;
    }

    public byte[] FinishSnapshotReceive()
    {
        var bytes = incomingSnapshot!.Build();
        incomingSnapshot = null;
        return bytes;
    }

    public WorldSnapshotManifest PendingSnapshotManifest => incomingSnapshotManifest;

    // ── 客户端：加载 / 就绪标志 ──
    public void MarkLoadStarted(float time) => loadStartedAt = time;
    public float LoadStartedAt => loadStartedAt;
    public void MarkLoadBucket(int bucket) => lastLoadBucket = bucket;
    public int LastLoadBucket => lastLoadBucket;
    public bool ClientSaveLoadPending { get => clientSaveLoadPending; set => clientSaveLoadPending = value; }

    public void MarkRegistryStabilized() => clientRegistryStabilized = true;
    public void MarkRegistryRequestSent() => clientRegistryRequestSent = true;
    public void ClearRegistryRequestSent() => clientRegistryRequestSent = false;
    public bool IsRegistryStabilized => clientRegistryStabilized;
    public bool IsRegistryRequestSent => clientRegistryRequestSent;
    public bool IsSnapshotManifestReceived => clientSnapshotManifestReceived;

    public void MarkSnapshotReady() => clientSnapshotReady = true;
    public bool IsSnapshotReady => clientSnapshotReady;

    public void RecordSnapshotAckSent(float realtimeNow) { snapshotAckRetryCount++; nextSnapshotAckRetry = realtimeNow + 2f; }
    public void RecordSnapshotApplied(WorldSnapshotApplied? applied)
    {
        lastSnapshotApplied = applied;
        snapshotAckRetryCount = 0;
        nextSnapshotAckRetry = 0f;
    }
    public WorldSnapshotApplied? LastSnapshotApplied => lastSnapshotApplied;
    public void ClearSnapshotApplied() { lastSnapshotApplied = null; snapshotAckRetryCount = 0; nextSnapshotAckRetry = 0f; }
    public bool ShouldRetrySnapshotAck(float realtimeNow)
    {
        if (clientSnapshotReady || lastSnapshotApplied == null || runtime.clientSession?.HandshakeComplete != true) return false;
        if (runtime.clientSession.Session.Lifecycle.State != ConnectionState.ApplyingSnapshot) return false;
        if (realtimeNow < nextSnapshotAckRetry) return false;
        nextSnapshotAckRetry = realtimeNow + 1f;
        snapshotAckRetryCount++;
        return true;
    }
    public int SnapshotAckRetryCount => snapshotAckRetryCount;

    public void MarkRegistryRequestRetry(float time) => nextRegistryRequestRetry = time;
    public float NextRegistryRequestRetry => nextRegistryRequestRetry;

    // ── 清理 ──
    public void OnPeerDisconnected(int peer)
    {
        sentSaves.Remove(peer);
        sentSnapshots.Remove(peer);
        pendingSnapshotRequests.Remove(peer);
    }

    public void Reset()
    {
        sentSaves.Clear();
        sentSnapshots.Clear();
        pendingSnapshotRequests.Clear();
        incomingSave = null;
        incomingSnapshot = null;
        transferProgressValue = string.Empty;
        clientSnapshotReady = false;
        clientRegistryRequestSent = false;
        clientSnapshotManifestReceived = false;
        clientRegistryStabilized = false;
        loadStartedAt = 0f;
        lastLoadBucket = 0;
        clientSaveLoadPending = false;
        nextRegistryRequestRetry = 0f;
        lastSnapshotApplied = null;
        nextSnapshotAckRetry = 0f;
        snapshotAckRetryCount = 0;
    }
}
