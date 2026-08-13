using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

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
		if (__instance != null && (Object)(object)__instance.inventory != (Object)null && (Object)(object)WorldStateSync.Instance != (Object)null)
		{
			WorldStateSync.Instance.NotifyInventoryChanged(__instance.inventory);
		}
	}
}
