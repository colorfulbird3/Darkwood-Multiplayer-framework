using System.Reflection;
using HarmonyLib;

namespace DarkwoodMultiplayerFramework.DarkwoodAdapter;

/// <summary>Isolates access to Darkwood's private Item.inventory field.</summary>
internal static class DarkwoodDroppedItemAccessor
{
    private static readonly FieldInfo InventoryField = AccessTools.Field(typeof(Item), "inventory");

    public static Inventory? GetInventory(Item item)
        => item == null || InventoryField == null ? null : InventoryField.GetValue(item) as Inventory;
}
