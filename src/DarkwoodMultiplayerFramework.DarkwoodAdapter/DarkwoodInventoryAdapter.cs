using System;
using System.Collections.Generic;

namespace DarkwoodMultiplayerFramework.DarkwoodAdapter;

public sealed class DarkwoodInventorySlot
{
    public string Type = string.Empty; public int Amount; public float Durability; public int Quality; public bool Recipe;
}

public static class DarkwoodInventoryAdapter
{
    public static DarkwoodInventorySlot[] Capture(Inventory inventory)
    {
        if (inventory == null || inventory.slots == null) return Array.Empty<DarkwoodInventorySlot>();
        var result = new List<DarkwoodInventorySlot>(inventory.slots.Count);
        foreach (var slot in inventory.slots)
        {
            var item = slot?.invItem;
            result.Add(item == null ? new DarkwoodInventorySlot() : new DarkwoodInventorySlot { Type = item.type ?? string.Empty, Amount = item.amount, Durability = item.durability, Quality = (int)item.modifierQuality, Recipe = item.isRecipe });
        }
        return result.ToArray();
    }
    public static void Apply(Inventory inventory, DarkwoodInventorySlot[] slots)
    {
        if (inventory == null || inventory.slots == null || slots == null) return;
        while (inventory.slots.Count < slots.Length) inventory.addSlot();
        for (var i = 0; i < inventory.slots.Count; i++)
        {
            var target = i < slots.Length ? slots[i] : new DarkwoodInventorySlot(); var slot = inventory.slots[i]; if (slot == null) continue;
            if (string.IsNullOrEmpty(target.Type)) { if (slot.invItem != null) slot.removeItem(); continue; }
            if (slot.invItem == null || slot.invItem.type != target.Type) slot.createItem(target.Type, Math.Max(1, target.Amount), target.Durability, (InvItem.ModifierQuality)target.Quality, target.Recipe);
            else { slot.invItem.amount = Math.Max(1, target.Amount); slot.invItem.durability = target.Durability; slot.invItem.modifierQuality = (InvItem.ModifierQuality)target.Quality; slot.invItem.isRecipe = target.Recipe; slot.invItem.refresh(); }
        }
        inventory.refreshItems();
    }
    /// <summary>从玩家背包移除指定物品（容器并发冲突补偿：拿走未生效的物品退回）。</summary>
    public static void RemoveFromPlayerInventory(string type,int amount)
    {
        var player=Player.Instance;
        if(player==null||string.IsNullOrEmpty(type)||amount<=0)return;
        var inventory=player.GetComponent<Inventory>();
        if(inventory==null||inventory.slots==null)return;
        var remaining=amount;
        for(var i=0;i<inventory.slots.Count&&remaining>0;i++)
        {
            var slot=inventory.slots[i];
            if(slot==null||slot.invItem==null||slot.invItem.type!=type)continue;
            var take=Math.Min(remaining,slot.invItem.amount);
            slot.invItem.amount-=take;remaining-=take;
            if(slot.invItem.amount<=0)slot.removeItem();else slot.invItem.refresh();
        }
        inventory.refreshItems();
    }
}
