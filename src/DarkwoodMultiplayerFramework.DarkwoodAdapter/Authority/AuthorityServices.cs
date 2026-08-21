using DarkwoodMultiplayerFramework.Core;
using DarkwoodMultiplayerFramework.DarkwoodAdapter.Network;
using DarkwoodMultiplayerFramework.Protocol;
using UnityEngine;

namespace DarkwoodMultiplayerFramework.DarkwoodAdapter.Authority;

/// <summary>
/// v0.9.0 —— Client Authority（Trusted Client）。
/// 客户端负责玩家自身体验与本地原版逻辑：移动/动画/输入/背包/快捷栏/拾取/丢弃/使用物品/翻窗/开门动画。
/// 这些 Darkwood 原版已实现，框架不再替客户端执行；网络只同步状态与事件。
/// </summary>
public static class ClientAuthority
{
    /// <summary>当前是否处于"客户端本地原版可执行"语境（联机已就绪、非 Replay 中、非 Host 权威路径）。</summary>
    public static bool CanLocallyExecute(DarkwoodAdapterRuntime runtime)
        => runtime != null && runtime.IsClient && runtime.State == ConnectionState.Ready
           && !runtime.ReplayingAuthoritativeAction;

    /// <summary>交互目标是否属于本地玩家（门/窗/物品选中对象的发起者判定）。</summary>
    public static bool IsLocalPlayerInteraction(Component target)
        => target != null && Player.Instance != null && target.GetComponent<Player>() == Player.Instance;

    /// <summary>客户端动作已在本地执行完成——通过事件通道通知其他玩家（Host Relay）。</summary>
    public static bool AnnounceLocalAction(DarkwoodAdapterRuntime runtime, string action)
        => EventSync.SendPlayerAction(runtime, action);
}

/// <summary>
/// v0.9.0 —— Host World Authority。
/// 主机维护世界真相：世界/地图/建筑/机器/事件/NPC/随机事件/保存数据/Entity 生命周期；
/// 不做"替客户端执行原版交互"，只做校验、状态保存、修订与广播。
/// </summary>
public static class HostAuthority
{
    public static bool IsHost(DarkwoodAdapterRuntime runtime) => runtime != null && runtime.Session.IsHost;

    /// <summary>提交客户端 InventorySnapshot（bootstrap 门开放后才允许内容重建；否则仅拓扑）。</summary>
    public static bool CommitInventorySnapshot(DarkwoodAdapterRuntime runtime, int peer, PlayerInventoryStatePayload snapshot)
    {
        if (runtime == null || !runtime.Session.IsHost) return false;
        if (!runtime.Players.IsInventoryBootstrapReady(peer)) return false;
        return runtime.Players.RebuildInventory(peer, snapshot);
    }

    /// <summary>持久化玩家档案（Host 保存数据权威）。</summary>
    public static bool SavePlayerState(DarkwoodAdapterRuntime runtime, int peer)
    {
        if (runtime == null || !runtime.Session.IsHost) return false;
        runtime.Players.PersistGuestProfile(peer);
        return true;
    }

    /// <summary>世界状态对象即时广播（状态对象交互/行为落地后）。</summary>
    public static bool BroadcastWorldState(DarkwoodAdapterRuntime runtime, EntityId id)
        => WorldStateSync.BroadcastStateNow(runtime, id);
}