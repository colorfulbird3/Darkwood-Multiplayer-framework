using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace DarkwoodMultiplayerFramework;

[HarmonyPatch]
internal static class InventoryMutationPatch
{
	private static IEnumerable<MethodBase> TargetMethods()
	{
		yield return AccessTools.Method(typeof(InvSlot), "transferItemToPlayer", (Type[])null, (Type[])null);
		yield return AccessTools.Method(typeof(InvSlot), "transferItemAllToPlayer", (Type[])null, (Type[])null);
		yield return AccessTools.Method(typeof(InvSlot), "transferItemToOpenedInv", (Type[])null, (Type[])null);
		yield return AccessTools.Method(typeof(InvSlot), "transferItemAllToOpenedInv", (Type[])null, (Type[])null);
	}

	private static void Postfix(InvSlot __instance)
	{
		if (__instance != null && __instance.inventory != null && WorldStateSync.Instance != null)
		{
			WorldStateSync.Instance.NotifyInventoryChanged(__instance.inventory);
		}
	}
}
