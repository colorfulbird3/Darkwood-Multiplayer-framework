using DarkwoodMultiplayerFramework.Core;
using UnityEngine;

namespace DarkwoodMultiplayerFramework.DarkwoodAdapter.World;

/// <summary>
/// P0（DMF-WorldEntity-Core）：世界对象的**只读统一视图**（facade over 现有 registry/binding）。
/// 不引入新的 id 体系、不新增协议：EntityId(Core) 仍是唯一网络身份，持久=world:N、运行时=runtime:N。
/// 用途：让上层（事件路由/WorldState/UI 诊断）用统一方式查询「EntityId ↔ 对象」而不直接摸内部字典。
/// </summary>
public readonly struct WorldEntityView
{
    public readonly EntityId Id;
    public readonly Component? Primary;    // primary/绑定组件（可能已销毁 → 用 IsAlive 判）
    public readonly GameObject? RootObject; // 绑定根 GameObject（可为 primary.gameObject）
    public readonly bool HasBinding;
    public readonly byte Kind;           // 与 DarkwoodEntityStateAdapter.Kind 同语义：1 Character/2 Door/3 Window/4 Item/5 Inventory/0 unknown

    private WorldEntityView(EntityId id, Component? primary, GameObject? rootObject, bool hasBinding, byte kind)
    {
        Id = id; Primary = primary; RootObject = rootObject; HasBinding = hasBinding; Kind = kind;
    }

    public bool IsRuntime => !Id.IsPersistent;
    public bool IsAlive => RootObject != null || (Primary != null && Primary.gameObject != null);

    /// <summary>按权威 EntityId 解析视图（实体已 unregister 返回 false）。</summary>
    public static bool TryCreate(DarkwoodEntityReplication replication, EntityId id, out WorldEntityView view)
    {
        view = default;
        if (replication == null) return false;
        Component? primary = null;
        GameObject? rootObject = null;
        var hasBinding = false;
        if (replication.TryGetComponent(id, out var component))
        {
            primary = component;
        }
        else if (replication.TryGetBinding(id, out var binding))
        {
            hasBinding = true;
            primary = binding.Primary;
            rootObject = binding.Root;
        }
        if (primary == null && rootObject == null) return false;
        if (rootObject == null && primary != null) { try { rootObject = primary.gameObject; } catch { } }
        byte kind = 0;
        if (primary != null) { try { kind = DarkwoodEntityStateAdapter.Kind(primary); } catch { } }
        view = new WorldEntityView(id, primary, rootObject, hasBinding || replication.TryGetBinding(id, out _), kind);
        return true;
    }

    /// <summary>按本地组件反查视图（组件未注册/无 EntityId 返回 false）。</summary>
    public static bool TryQuery(DarkwoodEntityReplication replication, Component component, out WorldEntityView view)
    {
        view = default;
        if (replication == null || component == null) return false;
        if (!replication.TryGetId(component, out var id)) return false;
        return TryCreate(replication, id, out view);
    }
}
