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
		int num = __state - __instance.barricadeHealth;
		if (num > 0 && attackerTransform != null && attackerTransform.GetComponent<Player>() != null && WorldStateSync.Instance != null)
		{
			WorldStateSync.Instance.RequestAction(__instance, 1, num, boolValue: false, Vector3.zero);
		}
	}
}
