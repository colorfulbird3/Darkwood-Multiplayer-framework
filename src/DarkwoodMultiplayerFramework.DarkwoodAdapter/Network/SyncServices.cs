using System;
using DarkwoodMultiplayerFramework.Core;
using DarkwoodMultiplayerFramework.Protocol;
using UnityEngine;

namespace DarkwoodMultiplayerFramework.DarkwoodAdapter.Network;

/// <summary>
/// v0.9.0 同步系统拆分 —— PlayerSync。
/// 职责：玩家状态中继（Position/Rotation/动画/动作状态/生命/装备），模式 Client→Host→Others，10-20Hz。
/// 客户端运行原版 PlayerController，生成状态 → 发送；Host Relay（Handlers 内）；其他客户端 Apply。
/// </summary>
public static class PlayerSync
{
    /// <summary>客户端：捕获当前玩家状态并发送（10-20Hz 定时驱动；委托原发送实现）。</summary>
    public static bool SendPlayerState(DarkwoodAdapterRuntime runtime)
    {
        if (runtime == null) return false;
        runtime.PlayerSyncSendPose();
        return true;
    }
}

/// <summary>
/// v0.9.0 —— InventorySync。
/// 职责：玩家背包/快捷栏快照 + revision 门；拾取直进背包（原版 transfer 语义）；丢弃由 Cursor 唯一创建。
/// 数据格式 InventorySnapshot{playerId, revision, slots[]}；规则：revision 小拒绝覆盖、大接受。
/// </summary>
public static class InventorySync
{
    /// <summary>客户端：应用权威快照（revision 门 + 原版 createItem 级 UI 初始化）。</summary>
    public static void ApplySnapshot(PlayerInventoryStatePayload snapshot)
        => DarkwoodAdapterRuntime.ApplyPlayerInventory(snapshot);

    /// <summary>Host：拾取地面物品直进权威背包（原版 DroppedItem 数据 → shadow 自动堆叠/空槽）。</summary>
    public static bool PickupToInventory(DarkwoodPlayerInventoryShadow shadow, InvItemClass sourceItem)
        => shadow != null && shadow.AddItem(sourceItem);

    /// <summary>Host：权威背包修订号（每次权威修改后递增，随快照下发）。</summary>
    public static int NextRevision(DarkwoodPlayerInventoryShadow shadow) => shadow?.Revision ?? 0;
}

/// <summary>
/// v0.9.0 —— EntitySync。
/// 职责：对象存在/生成/销毁/ID 绑定（不含内部状态）；Runtime 实体生命周期（Spawn/Despawn 原子注册）。
/// </summary>
public static class EntitySync
{
    public static bool TryGetInventory(DarkwoodAdapterRuntime runtime, EntityId id, out Inventory inventory)
    {
        inventory = null!;
        return runtime != null && runtime.TryGetBoundInventory(id, out inventory);
    }
}

/// <summary>
/// v0.9.0 —— WorldStateSync。
/// 职责：世界状态对象 typed 同步（Generator/Light/BearTrap/Door…）：host 捕获/校验状态 → 广播；客户端幂等 Apply。
/// 只同步 State（标量），不同步 GameObject / Method Call。
/// </summary>
public static class WorldStateSync
{
    /// <summary>事件即时广播：对单个实体捕获并广播权威状态（Host 交互/行为落地后立即调用）。</summary>
    public static bool BroadcastStateNow(DarkwoodAdapterRuntime runtime, EntityId id)
    {
        if (runtime == null || !runtime.Session.IsHost) return false;
        runtime.BroadcastStateNow(id);
        return true;
    }
}

/// <summary>
/// v0.9.0 —— EventSync。
/// 职责：即时事件中继（玩家动作/翻窗/交互动画/剧情事件）：本地已执行 → 事件 → Host Relay → 其他客户端播放。
/// </summary>
public static class EventSync
{
    /// <summary>客户端：动作已本地执行，发送动作事件（Host Relay）。</summary>
    public static bool SendPlayerAction(DarkwoodAdapterRuntime runtime, string action)
        => runtime != null && runtime.TryRequestPlayerAction(action);

    /// <summary>Host 收到动作事件后的中继（由 HandlePlayerActionRequest 调用：广播给其他 ready peers）。</summary>
    public static void RelayPlayerAction(DarkwoodAdapterRuntime runtime, int sourcePeer, PlayerActionPayload payload)
        => runtime.RelayPlayerAction(sourcePeer, payload);
}

/// <summary>
/// v0.9.0 —— SnapshotSync。
/// 职责：加入时的全量世界快照（注册表 + Entity 状态 + 共享容器），客户端应用后进入 Ready。
/// </summary>
public static class SnapshotSync
{
    public static bool IsSnapshotComplete(DarkwoodAdapterRuntime runtime) => runtime != null && runtime.SnapshotTransferComplete;
}