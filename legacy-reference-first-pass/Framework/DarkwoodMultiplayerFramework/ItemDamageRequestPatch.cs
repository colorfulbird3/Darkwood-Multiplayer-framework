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
		//IL_003b: Unknown result type (might be due to invalid IL or missing references)
		int num = __state - __instance.health;
		if (num > 0 && (Object)(object)attackerTransform != (Object)null && (Object)(object)((Component)attackerTransform).GetComponent<Player>() != (Object)null && (Object)(object)WorldStateSync.Instance != (Object)null)
		{
			WorldStateSync.Instance.RequestAction((Component)(object)__instance, 1, num, boolValue: false, Vector3.zero);
		}
	}
}
