using System;
using HarmonyLib;
using UnityEngine;

namespace DarkwoodMultiplayerFramework;

[HarmonyPatch(typeof(Character), "getHit", new Type[]
{
	typeof(float),
	typeof(Transform),
	typeof(bool),
	typeof(bool),
	typeof(bool),
	typeof(bool),
	typeof(bool),
	typeof(bool),
	typeof(bool)
})]
internal static class EnemyDamageRequestPatch
{
	private static void Prefix(Character __instance, out float __state)
	{
		__state = __instance.health;
	}

	private static void Postfix(Character __instance, Transform attackerTransform, bool byPlayer, float __state)
	{
		float num = __state - __instance.health;
		if (num > 0f && (byPlayer || (attackerTransform != null && attackerTransform.GetComponent<Player>() != null)) && WorldStateSync.Instance != null)
		{
			WorldStateSync.Instance.RequestAction(__instance, 1, num, boolValue: false, Vector3.zero);
		}
	}
}
