using System;
using HarmonyLib;
using UnityEngine;

namespace DarkwoodMultiplayerFramework;

[HarmonyPatch(typeof(Door), "openClose", new Type[] { typeof(Transform) })]
internal static class DoorToggleRequestPatch
{
	private static void Postfix(Door __instance, Transform openerTransform)
	{
		if (openerTransform != null && openerTransform.GetComponent<Player>() != null && WorldStateSync.Instance != null)
		{
			WorldStateSync.Instance.RequestAction(__instance, 2, 0f, __instance.opened, Vector3.zero);
		}
	}
}
