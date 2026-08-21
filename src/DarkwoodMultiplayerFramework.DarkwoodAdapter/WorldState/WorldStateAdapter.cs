using System;
using System.Collections.Generic;
using UnityEngine;

namespace DarkwoodMultiplayerFramework.DarkwoodAdapter.World;

/// <summary>
/// World State Adapter 架构（P0）：把"某个大世界对象类型"的完整业务状态
/// 打包成 typed payload，走 EntityStateWire.ExtraState 传输，替代在通用
/// EntityStateWire 里无限塞 flag（用户定调：容器的 InventoryStateMessage 成功经验
/// 复制到整个世界）。Host Capture → typed payload → Client typed Apply。
/// 通用字段（transform/health/active）仍由外层复制管理负责。
/// </summary>
public interface IWorldStateAdapter
{
    /// <summary>是否处理该组件（具体类型的 adapter 优先注册、最具体优先匹配）。</summary>
    bool CanHandle(Component component);
    /// <summary>唯一 schema id（写入 EntityStateWire.StateSchema）。</summary>
    ushort SchemaId { get; }
    /// <summary>捕获 typed 业务状态（不含 transform）。返回空数组表示无附加状态。</summary>
    byte[] Capture(Component component);
    /// <summary>判定 typed 状态是否变化（用于 delta 过滤）。</summary>
    bool HasChanged(byte[] oldState, byte[] newState);
    /// <summary>应用 typed 状态。必须是幂等赋值（Apply(S) Apply(S) ⇒ 二次无变化），禁止 toggle 语义。</summary>
    void Apply(Component component, byte[] state);
    /// <summary>客户端进入 Ready 后：把组件修为"纯视觉代理"（关闭 simulation owner，Host 仍是唯一权威）。</summary>
    void EnterClientProxyMode(Component component);
    /// <summary>退出代理模式（断开/场景卸载时还原，避免残留在客户端单机世界）。</summary>
    void ExitClientProxyMode(Component component);
}

/// <summary>标准 schema id 分配（新 adapter 在此登记，避免冲突）。</summary>
public static class WorldStateSchemas
{
    public const ushort Legacy = 0;       // 无 typed payload（旧字段语义）
    public const ushort Character = 1;
    public const ushort Door = 2;
    public const ushort Window = 3;
    public const ushort GenericItem = 4;
    public const ushort BearTrap = 5;
    public const ushort DroppedItem = 6;
    public const ushort Generator = 7;
    public const ushort Light = 8;
}

/// <summary>按注册顺序优先匹配（具体 adapter 先注册，通用适配器放最后；逐个取第一个 CanHandle）。</summary>
public sealed class WorldStateAdapterRegistry
{
    private readonly List<IWorldStateAdapter> adapters = new List<IWorldStateAdapter>();
    public void Register(IWorldStateAdapter adapter)
    {
        if (adapter == null) return;
        adapters.RemoveAll(a => a.SchemaId == adapter.SchemaId);
        adapters.Add(adapter);
    }
    /// <summary>最具体优先：遍历顺序 = 注册顺序（具体先注册）。找不到返回 null。</summary>
    public IWorldStateAdapter Resolve(Component component)
    {
        if (component == null) return null;
        foreach (var a in adapters) if (a.CanHandle(component)) return a;
        return null;
    }
    public IEnumerable<IWorldStateAdapter> All => adapters;
}
