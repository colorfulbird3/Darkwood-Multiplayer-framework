using System;
using HarmonyLib;
using UnityEngine;

namespace DarkwoodMultiplayerFramework;

[HarmonyPatch(typeof(Door), "openClose", new Type[] { typeof(Transform) })]
internal static class DoorToggleRequestPatch
{
	private static void Postfix(Door __instance, Transform openerTransform)
	{
		//IL_0036: Unknown result type (might be due to invalid IL or missing references)
		if ((Object)(object)openerTransform != (Object)null && (Object)(object)((Component)openerTransform).GetComponent<Player>() != (Object)null && (Object)(object)WorldStateSync.Instance != (Object)null)
		{
			WorldStateSync.Instance.RequestAction((Component)(object)__instance, 2, 0f, __instance.opened, Vector3.zero);
		}
	}
}
