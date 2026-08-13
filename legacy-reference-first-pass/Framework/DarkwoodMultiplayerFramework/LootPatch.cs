using System;
using System.Collections.Generic;
using HarmonyLib;
using Mirror;
using UnityEngine;

namespace DarkwoodMultiplayerFramework;

[HarmonyPatch(typeof(InventoryRandom), "spawnItems")]
internal static class LootPatch
{
	internal sealed class State
	{
		public Inventory Inventory;

		public HashSet<InvItemClass> Existing;
	}

	private static readonly FieldRef<InventoryRandom, Inventory> InventoryField = AccessTools.FieldRefAccess<InventoryRandom, Inventory>("inventory");

	private static void Prefix(InventoryRandom __instance, out State __state)
	{
		__state = null;
		if ((NetworkClient.active && !NetworkServer.active) || !Plugin.ScaleLoot.Value || Plugin.Players.Value <= 1)
		{
			return;
		}
		Inventory val = InventoryField.Invoke(__instance);
		if ((Object)(object)val == (Object)null || val.slots == null)
		{
			return;
		}
		HashSet<InvItemClass> hashSet = new HashSet<InvItemClass>();
		foreach (InvSlot slot in val.slots)
		{
			if (slot != null && slot.invItem != null)
			{
				hashSet.Add(slot.invItem);
			}
		}
		__state = new State
		{
			Inventory = val,
			Existing = hashSet
		};
	}

	private static void Postfix(State __state)
	{
		if (__state == null || (Object)(object)__state.Inventory == (Object)null || __state.Inventory.slots == null)
		{
			return;
		}
		List<InvItemClass> list = new List<InvItemClass>();
		foreach (InvSlot slot in __state.Inventory.slots)
		{
			if (slot != null && slot.invItem != null && !__state.Existing.Contains(slot.invItem))
			{
				list.Add(slot.invItem);
			}
		}
		foreach (InvItemClass item in list)
		{
			Scale(__state.Inventory, item, Plugin.Players.Value);
		}
	}

	private static void Scale(Inventory inv, InvItemClass item, int players)
	{
		if (item == null || (Object)(object)item.baseClass == (Object)null || players <= 1)
		{
			return;
		}
		int num = Math.Max(1, item.amount);
		if (!item.baseClass.stackable)
		{
			for (int i = 1; i < players; i++)
			{
				Copy(inv, item, num);
			}
			Plugin.Log.LogDebug((object)$"Scaled non-stackable {item.type} x{players}.");
			return;
		}
		long num2 = (long)num * (long)players;
		int num3 = (int)((num2 > int.MaxValue) ? int.MaxValue : num2);
		int val = ((item.baseClass.maxAmount > 0) ? item.baseClass.maxAmount : num3);
		item.amount = Math.Min(num3, val);
		item.refresh();
		int num4 = num3 - item.amount;
		while (num4 > 0)
		{
			int num5 = Math.Min(num4, val);
			if (!Copy(inv, item, num5))
			{
				item.amount += num4;
				item.refresh();
				Plugin.Log.LogWarning((object)$"No slot for {item.type}; kept {num4} extra in one stack.");
				break;
			}
			num4 -= num5;
		}
		Plugin.Log.LogDebug((object)$"Scaled {item.type}: {num} -> {num3}.");
	}

	private static bool Copy(Inventory inv, InvItemClass source, int amount)
	{
		InvSlot nextFreeSlot = inv.getNextFreeSlot();
		if (nextFreeSlot == null)
		{
			return false;
		}
		nextFreeSlot.createItem(source, amount);
		return true;
	}
}
