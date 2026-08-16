using System;
using System.Collections.Generic;
using DarkwoodMultiplayerFramework.Network;
using DarkwoodMultiplayerFramework.Protocol;

namespace DarkwoodMultiplayerFramework.DarkwoodAdapter;

/// <summary>
/// 0.8.9 所有权拆分：存档/快照传输服务——拥有 Save/Snapshot 传输的全部状态
/// （发送登记、接收装配器、进度、就绪标志）。Reset 与断线清理自管。
/// </summary>
public sealed class DarkwoodSaveTransferService
{
    private readonly DarkwoodAdapterRuntime runtime;

    internal readonly Dictionary<int, Guid> SentSaves = new Dictionary<int, Guid>();
    internal readonly Dictionary<int, Guid> SentSnapshots = new Dictionary<int, Guid>();
    internal readonly Dictionary<int, ReadyMessage> PendingSnapshotRequests = new Dictionary<int, ReadyMessage>();
    internal ChunkTransferAssembler? IncomingSave;
    internal SaveTransferManifest IncomingSaveManifest;
    internal ChunkTransferAssembler? IncomingSnapshot;
    internal WorldSnapshotManifest IncomingSnapshotManifest;
    internal bool ClientSnapshotReady;
    internal bool ClientRegistryRequestSent;
    internal bool ClientSnapshotManifestReceived;
    internal bool ClientRegistryStabilized;
    internal float LoadStartedAt;
    internal int LastLoadBucket;
    internal bool ClientSaveLoadPending;
    internal float NextRegistryRequestRetry;
    internal WorldSnapshotApplied? LastSnapshotApplied;
    internal float NextSnapshotAckRetry;
    internal int SnapshotAckRetryCount;
    internal string TransferProgressValue = string.Empty;

    public DarkwoodSaveTransferService(DarkwoodAdapterRuntime runtime) => this.runtime = runtime;

    public void OnPeerDisconnected(int peer)
    {
        SentSaves.Remove(peer);
        SentSnapshots.Remove(peer);
        PendingSnapshotRequests.Remove(peer);
    }

    public void Reset()
    {
        SentSaves.Clear();
        SentSnapshots.Clear();
        PendingSnapshotRequests.Clear();
        IncomingSave = null;
        IncomingSnapshot = null;
        TransferProgressValue = string.Empty;
        ClientSnapshotReady = false;
        ClientRegistryRequestSent = false;
        ClientSnapshotManifestReceived = false;
        ClientRegistryStabilized = false;
        LoadStartedAt = 0f;
        LastLoadBucket = 0;
        ClientSaveLoadPending = false;
        NextRegistryRequestRetry = 0f;
        LastSnapshotApplied = null;
        NextSnapshotAckRetry = 0f;
        SnapshotAckRetryCount = 0;
    }
}
