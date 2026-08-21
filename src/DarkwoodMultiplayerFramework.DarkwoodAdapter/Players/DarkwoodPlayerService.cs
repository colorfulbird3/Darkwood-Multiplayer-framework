using System;
using System.Collections.Generic;
using DarkwoodMultiplayerFramework.Protocol;
using UnityEngine;

namespace DarkwoodMultiplayerFramework.DarkwoodAdapter;

/// <summary>
/// 所有权拆分：玩家服务——拥有远端玩家在线状态（坐标/背包影子/Guest 身份档案）。
/// Guest Profile 持久化（跨热加入身份数据）与在线状态同归本服务管理。
/// </summary>
public sealed class DarkwoodPlayerService
{
    private readonly DarkwoodAdapterRuntime runtime;

    private readonly Dictionary<int, Vector3> remotePositions = new Dictionary<int, Vector3>();
    private readonly Dictionary<int, DarkwoodPlayerInventoryShadow> remoteInventories = new Dictionary<int, DarkwoodPlayerInventoryShadow>();
    private readonly Dictionary<int, string> peerGuestKeys = new Dictionary<int, string>();
    private readonly Dictionary<int, GuestProfileRecord> peerGuestRecords = new Dictionary<int, GuestProfileRecord>();
    // P0-2：Inventory bootstrap 门——Host GuestProfile seed 应用并收到客户端 ack 前，客户端上报仅取容量、忽略内容。
    private readonly HashSet<int> inventoryBootstrapReady = new HashSet<int>();

    public DarkwoodRemotePlayers RemotePlayers { get; }
    private DarkwoodGuestProfiles? guestProfiles;

    public DarkwoodPlayerService(DarkwoodAdapterRuntime runtime, DarkwoodRemotePlayers remotePlayers)
    {
        this.runtime = runtime;
        RemotePlayers = remotePlayers;
    }

    public void AttachGuestProfiles(DarkwoodGuestProfiles profiles) => guestProfiles = profiles;

    /// <summary>主机：为新就绪玩家解析 Guest 档案（热加入）+ 影子背包 + 出生点。</summary>
    public GuestProfileRecord ResolveGuestProfile(int peer, string? guestKey, int day, Vector3 hostPosition, out Vector3 spawn)
    {
        var key = NormalizeGuestKey(guestKey);
        var record = new GuestProfileRecord(key, day, 1, 0f, 0f, 0f, Array.Empty<InventorySlotWire>(), Array.Empty<InventorySlotWire>(), DateTime.UtcNow.Ticks);
        spawn = hostPosition;
        if (guestProfiles != null) record = guestProfiles.Resolve(runtime.HostSaveToken(), key, day, hostPosition, out spawn);
        spawn = runtime.DefaultSpawnPoint(); // 客户端始终在游戏默认出生点出生
        var shadow = DarkwoodPlayerInventoryShadow.FromRecord(record, message => runtime.log?.LogWarning(message));
        if (record.JoinCount == 1) shadow.AddStarterKit(guestProfiles?.KitForDay(day), message => runtime.log?.LogWarning(message));
        remoteInventories[peer] = shadow;
        peerGuestRecords[peer] = record;
        peerGuestKeys[peer] = key;
        runtime.log?.LogInfo($"Peer {peer} guest profile resolved: {key}, day {record.Day}, join {record.JoinCount}, spawn ({spawn.x:F1},{spawn.y:F1},{spawn.z:F1}).");
        return record;
    }

    public void PersistGuestProfile(int peer)
    {
        if (guestProfiles == null) return;
        if (!peerGuestKeys.TryGetValue(peer, out var key) || !peerGuestRecords.TryGetValue(peer, out var record) || !remoteInventories.TryGetValue(peer, out var shadow)) return;
        var position = remotePositions.TryGetValue(peer, out var pose) ? pose : new Vector3(record.X, record.Y, record.Z);
        var state = shadow.CaptureState();
        var updated = new GuestProfileRecord(record.GuestKey, record.Day, record.JoinCount, position.x, position.y, position.z, state.Backpack, state.Hotbar, DateTime.UtcNow.Ticks);
        guestProfiles.Save(runtime.HostSaveToken(), updated);
    }

    /// <summary>客户端：应用访客档案（出生点/背包/血量）。</summary>
    public void ApplyGuestProfile(GuestProfileMessage profile)
    {
        var player = Player.Instance;
        if (player == null) throw new InvalidOperationException("客户端玩家尚未就绪。");
        player.transform.position = new Vector3(profile.X, profile.Y, profile.Z);
        foreach (var body in player.GetComponentsInChildren<Rigidbody>(true)) { body.velocity = Vector3.zero; body.angularVelocity = Vector3.zero; }
        DarkwoodAdapterRuntime.ApplyPlayerInventory(profile.Inventory);
        if (!profile.Downed && profile.Health > 0f) player.setHealth(profile.Health);
        runtime.log?.LogInfo($"已应用访客档案：出生点 ({profile.X:F1},{profile.Y:F1},{profile.Z:F1})，第 {profile.Day} 天，第 {profile.JoinCount} 次加入。");
    }

    internal static string NormalizeGuestKey(string? value)
    {
        var key = (value ?? string.Empty).Trim();
        if (key.Length == 0) return "player";
        while (System.Text.Encoding.UTF8.GetByteCount(key) > 64 && key.Length > 1) key = key.Substring(0, key.Length - 1);
        return key;
    }

    // ── 0.8.9 收口：状态访问全部走方法（禁止外部直接摸字典）──
    public IEnumerable<int> ConnectedPeers => remoteInventories.Keys;
    public bool TryGetRemotePosition(int peer, out Vector3 position) => remotePositions.TryGetValue(peer, out position);
    public void UpdateRemotePosition(int peer, Vector3 position) => remotePositions[peer] = position;
    public bool RemoveRemotePosition(int peer) => remotePositions.Remove(peer);

    internal bool TryGetInventory(int peer, out DarkwoodPlayerInventoryShadow inventory) => remoteInventories.TryGetValue(peer, out inventory!);
    internal void SetInventory(int peer, DarkwoodPlayerInventoryShadow inventory) => remoteInventories[peer] = inventory;
    public bool RemoveInventory(int peer) => remoteInventories.Remove(peer);

    /// <summary>客户端上报真实背包后重建影子（本地合成/搜尸体等漂移收敛；仅 bootstrap 门开启后允许改内容）。</summary>
    public bool RebuildInventory(int peer, PlayerInventoryStatePayload state)
    {
        if (!remoteInventories.TryGetValue(peer, out var shadow)) return false;
        shadow.Rebuild(state.Backpack, state.Hotbar, message => runtime.log?.LogWarning(message));
        runtime.log?.LogInfo($"[INV-BOOTSTRAP] Peer {peer} inventory rebuilt from client report (gate open): {state.Backpack.Length} backpack slots, {state.Hotbar.Length} hotbar slots.");
        return true;
    }

    /// <summary>P0-2：bootstrap 门未开时，客户端上报只取真实容量（长度）更新 shadow 拓扑，绝不把客户端旧存档物品内容灌进 Host shadow。</summary>
    public bool RebuildInventoryTopologyOnly(int peer, PlayerInventoryStatePayload state)
    {
        if (!remoteInventories.TryGetValue(peer, out var shadow)) return false;
        shadow.RefreshTopology(state.Backpack?.Length ?? 0, state.Hotbar?.Length ?? 0);
        return true;
    }

    // P0-2：GuestProfile applied ack → 开放该 peer 的 inventory 漂移收敛（内容可上报）。
    public bool MarkInventoryBootstrapReady(int peer) { inventoryBootstrapReady.Add(peer); return true; }
    public bool IsInventoryBootstrapReady(int peer) => inventoryBootstrapReady.Contains(peer);

    public bool TryGetGuestKey(int peer, out string key) => peerGuestKeys.TryGetValue(peer, out key!);
    public void SetGuestKey(int peer, string key) => peerGuestKeys[peer] = key;
    public bool TryGetGuestRecord(int peer, out GuestProfileRecord record) => peerGuestRecords.TryGetValue(peer, out record);
    public void SetGuestRecord(int peer, GuestProfileRecord record) => peerGuestRecords[peer] = record;
    public bool RemoveGuestData(int peer) => peerGuestKeys.Remove(peer) | peerGuestRecords.Remove(peer);

    public void OnPeerDisconnected(int peer)
    {
        remotePositions.Remove(peer);
        remoteInventories.Remove(peer);
        RemotePlayers.Remove(peer);
        peerGuestKeys.Remove(peer);
        peerGuestRecords.Remove(peer);
        inventoryBootstrapReady.Remove(peer);
    }

    public void Reset()
    {
        remotePositions.Clear();
        remoteInventories.Clear();
        peerGuestKeys.Clear();
        peerGuestRecords.Clear();
        RemotePlayers.Clear();
        inventoryBootstrapReady.Clear();
    }
}
