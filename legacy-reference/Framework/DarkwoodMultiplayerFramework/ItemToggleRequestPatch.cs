using HarmonyLib;
using UnityEngine;

namespace DarkwoodMultiplayerFramework;

[HarmonyPatch(typeof(Item), "activate")]
internal static class ItemToggleRequestPatch
{
	private static void Postfix(Item __instance)
	{
		if (WorldStateSync.Instance != null)
		{
			WorldStateSync.Instance.RequestAction(__instance, 2, 0f, __instance.isOn, Vector3.zero);
		}
	}
}
