using HarmonyLib;
using UnityEngine;

namespace DarkwoodMultiplayerFramework;

[HarmonyPatch(typeof(Window), "getHit")]
internal static class WindowDamageRequestPatch
{
	private static void Prefix(Window __instance, out int __state)
	{
		__state = __instance.barricadeHealth;
	}

	private static void Postfix(Window __instance, Transform attackerTransform, int __state)
	{
		//IL_003b: Unknown result type (might be due to invalid IL or missing references)
		int num = __state - __instance.barricadeHealth;
		if (num > 0 && (Object)(object)attackerTransform != (Object)null && (Object)(object)((Component)attackerTransform).GetComponent<Player>() != (Object)null && (Object)(object)WorldStateSync.Instance != (Object)null)
		{
			WorldStateSync.Instance.RequestAction((Component)(object)__instance, 1, num, boolValue: false, Vector3.zero);
		}
	}
}
