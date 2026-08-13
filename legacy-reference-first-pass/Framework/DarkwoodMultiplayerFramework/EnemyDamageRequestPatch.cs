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
		__state = ((CharBase)__instance).health;
	}

	private static void Postfix(Character __instance, Transform attackerTransform, bool byPlayer, float __state)
	{
		//IL_0041: Unknown result type (might be due to invalid IL or missing references)
		float num = __state - ((CharBase)__instance).health;
		if (num > 0f && (byPlayer || ((Object)(object)attackerTransform != (Object)null && (Object)(object)((Component)attackerTransform).GetComponent<Player>() != (Object)null)) && (Object)(object)WorldStateSync.Instance != (Object)null)
		{
			WorldStateSync.Instance.RequestAction((Component)(object)__instance, 1, num, boolValue: false, Vector3.zero);
		}
	}
}
