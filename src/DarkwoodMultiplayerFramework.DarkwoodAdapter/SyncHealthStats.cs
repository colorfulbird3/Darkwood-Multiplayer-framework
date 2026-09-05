using System;
using System.Threading;

namespace DarkwoodMultiplayerFramework.DarkwoodAdapter;

/// <summary>
/// v0.9.2 P0-SYNC-HEALTH：每 5 秒输出双方同步计数器，区分 transport/router 阻塞与 revision 阻塞。
/// Host/Client 字段分别累加；日志自动区分当前角色（Host=true vs Host=false）。
/// </summary>
public sealed class SyncHealthStats
{
    // Host side counters
    private long entityDeltaSent;
    private long playerCommitRecv;
    private long containerTxRecv;
    private long containerTxAccepted;
    private long containerTxConflict;
    private long pickupCommitRecv;
    private long dropCommitRecv;
    // Client side counters
    private long playerCommitSent;
    private long containerTxSent;
    private long containerTxAck;
    private long containerTxReconcile;
    private long entityDeltaRecv;
    private long pickupCommitSent;
    private long dropCommitSent;
    // Tick
    private float nextReportAt;
    private readonly DarkwoodAdapterRuntime runtime;

    public SyncHealthStats(DarkwoodAdapterRuntime runtime) { this.runtime = runtime; }

    public void IncEntityDeltaSent() { Interlocked.Increment(ref entityDeltaSent); }
    public void IncEntityDeltaRecv() { Interlocked.Increment(ref entityDeltaRecv); }
    public void IncPlayerCommitRecv() { Interlocked.Increment(ref playerCommitRecv); }
    public void IncPlayerCommitSent() { Interlocked.Increment(ref playerCommitSent); }
    public void IncContainerTxRecv(int n = 1) { Interlocked.Add(ref containerTxRecv, n); }
    public void IncContainerTxSent(int n = 1) { Interlocked.Add(ref containerTxSent, n); }
    public void IncContainerTxAccepted(int n = 1) { Interlocked.Add(ref containerTxAccepted, n); }
    public void IncContainerTxConflict(int n = 1) { Interlocked.Add(ref containerTxConflict, n); }
    public void IncContainerTxAck() { Interlocked.Increment(ref containerTxAck); }
    public void IncContainerTxReconcile() { Interlocked.Increment(ref containerTxReconcile); }
    public void IncPickupCommitRecv() { Interlocked.Increment(ref pickupCommitRecv); }
    public void IncPickupCommitSent() { Interlocked.Increment(ref pickupCommitSent); }
    public void IncDropCommitRecv() { Interlocked.Increment(ref dropCommitRecv); }
    public void IncDropCommitSent() { Interlocked.Increment(ref dropCommitSent); }

    public void Tick()
    {
        if (UnityEngine.Time.unscaledTime < nextReportAt) return;
        nextReportAt = UnityEngine.Time.unscaledTime + 5f;
        var snapshot = new System.Text.StringBuilder();
        snapshot.Append("[SYNC-HEALTH] role=").Append(runtime.IsHost ? "Host" : "Client");
        snapshot.Append(" readyPeers=").Append(runtime.readyPeers?.Count ?? 0);
        if (runtime.IsHost)
        {
            snapshot.Append(" entityDeltaSent=").Append(Interlocked.Read(ref entityDeltaSent));
            snapshot.Append(" playerCommitRecv=").Append(Interlocked.Read(ref playerCommitRecv));
            snapshot.Append(" containerTxRecv=").Append(Interlocked.Read(ref containerTxRecv));
            snapshot.Append(" containerTxAccepted=").Append(Interlocked.Read(ref containerTxAccepted));
            snapshot.Append(" containerTxConflict=").Append(Interlocked.Read(ref containerTxConflict));
            snapshot.Append(" pickupCommitRecv=").Append(Interlocked.Read(ref pickupCommitRecv));
            snapshot.Append(" dropCommitRecv=").Append(Interlocked.Read(ref dropCommitRecv));
        }
        else
        {
            snapshot.Append(" entityDeltaRecv=").Append(Interlocked.Read(ref entityDeltaRecv));
            snapshot.Append(" playerCommitSent=").Append(Interlocked.Read(ref playerCommitSent));
            snapshot.Append(" containerTxSent=").Append(Interlocked.Read(ref containerTxSent));
            snapshot.Append(" containerTxAck=").Append(Interlocked.Read(ref containerTxAck));
            snapshot.Append(" containerTxReconcile=").Append(Interlocked.Read(ref containerTxReconcile));
            snapshot.Append(" pickupCommitSent=").Append(Interlocked.Read(ref pickupCommitSent));
            snapshot.Append(" dropCommitSent=").Append(Interlocked.Read(ref dropCommitSent));
        }
        runtime.log?.LogInfo(snapshot.ToString());
    }
}
