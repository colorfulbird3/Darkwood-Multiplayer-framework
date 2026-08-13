using HarmonyLib;
using UnityEngine;

namespace DarkwoodMultiplayerFramework;

[HarmonyPatch(typeof(Item), "activate")]
internal static class ItemToggleRequestPatch
{
	private static void Postfix(Item __instance)
	{
		//IL_001f: Unknown result type (might be due to invalid IL or missing references)
		if ((Object)(object)WorldStateSync.Instance != (Object)null)
		{
			WorldStateSync.Instance.RequestAction((Component)(object)__instance, 2, 0f, __instance.isOn, Vector3.zero);
		}
	}
}
