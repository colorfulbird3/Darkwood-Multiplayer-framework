using HarmonyLib;
using UnityEngine;

namespace DarkwoodMultiplayerFramework;

[HarmonyPatch(typeof(Door), "getHit")]
internal static class DoorDamageRequestPatch
{
	private static void Prefix(Door __instance, out int __state)
	{
		__state = __instance.health + __instance.barricadeHealth;
	}

	private static void Postfix(Door __instance, Transform attackerTransform, int __state)
	{
		int num = __state - (__instance.health + __instance.barricadeHealth);
		if (num > 0 && attackerTransform != null && attackerTransform.GetComponent<Player>() != null && WorldStateSync.Instance != null)
		{
			WorldStateSync.Instance.RequestAction(__instance, 1, num, boolValue: false, Vector3.zero);
		}
	}
}
