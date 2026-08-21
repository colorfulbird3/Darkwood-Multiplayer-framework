using DarkwoodMultiplayerFramework.Core;
using UnityEngine;

namespace DarkwoodMultiplayerFramework.DarkwoodAdapter;

/// <summary>世界实体类别（第 1 刀：统一身份）。</summary>
public enum WorldEntityKind
{
    Unknown = 0,
    Item,
    Inventory,
    Character,
    LootContainer,
    DroppedItem,
    Enemy,
    Door,
    Window
}

/// <summary>
/// 世界实体绑定：一个 EntityId 背后的完整组件群。
/// 持久实体（地图自带）通常只有 Primary 单组件；运行时实体（掉落物/敌人/镜像）带完整组件群。
/// </summary>
public sealed class WorldEntityBinding
{
    public EntityId Id { get; set; }
    public GameObject Root { get; set; }
    public Item Item { get; set; }
    public Inventory Inventory { get; set; }
    public Character Character { get; set; }
    public Component Primary { get; set; }
    public WorldEntityKind Kind { get; set; }

    public static WorldEntityKind InferKind(Component component)
    {
        if (component is Character character && !(character is Player)) return WorldEntityKind.Enemy;
        if (component is Player) return WorldEntityKind.Character;
        if (component is Inventory inventory)
        {
            if (inventory.invType == Inventory.InvType.itemInv) return WorldEntityKind.DroppedItem;
            if (inventory.invType == Inventory.InvType.deathDrop) return WorldEntityKind.LootContainer;
            return WorldEntityKind.Inventory;
        }
        if (component is Item) return WorldEntityKind.Item;
        return WorldEntityKind.Unknown;
    }

    /// <summary>从单一组件构造绑定（持久实体路径）。</summary>
    public static WorldEntityBinding FromComponent(EntityId id, Component component)
    {
        return new WorldEntityBinding
        {
            Id = id,
            Root = component != null ? component.gameObject : null,
            Primary = component,
            Item = component as Item,
            Inventory = component as Inventory,
            Character = component as Character,
            Kind = InferKind(component)
        };
    }
}
