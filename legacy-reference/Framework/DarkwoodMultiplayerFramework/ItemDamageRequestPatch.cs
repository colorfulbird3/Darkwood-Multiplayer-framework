using HarmonyLib;
using UnityEngine;

namespace DarkwoodMultiplayerFramework;

[HarmonyPatch(typeof(Item), "getHit")]
internal static class ItemDamageRequestPatch
{
	private static void Prefix(Item __instance, out int __state)
	{
		__state = __instance.health;
	}

	private static void Postfix(Item __instance, Transform attackerTransform, int __state)
	{
		int num = __state - __instance.health;
		if (num > 0 && attackerTransform != null && attackerTransform.GetComponent<Player>() != null && WorldStateSync.Instance != null)
		{
			WorldStateSync.Instance.RequestAction(__instance, 1, num, boolValue: false, Vector3.zero);
		}
	}
}
