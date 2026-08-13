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
		//IL_0042: Unknown result type (might be due to invalid IL or missing references)
		int num = __state - (__instance.health + __instance.barricadeHealth);
		if (num > 0 && (Object)(object)attackerTransform != (Object)null && (Object)(object)((Component)attackerTransform).GetComponent<Player>() != (Object)null && (Object)(object)WorldStateSync.Instance != (Object)null)
		{
			WorldStateSync.Instance.RequestAction((Component)(object)__instance, 1, num, boolValue: false, Vector3.zero);
		}
	}
}
