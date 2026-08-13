using HarmonyLib;
using UnityEngine;

namespace DarkwoodMultiplayerFramework;

[HarmonyPatch(typeof(Player), "initiateSpecialAttack")]
internal static class SpecialAttackPatch
{
	private static void Postfix(Player __instance)
	{
		if ((Object)(object)__instance == (Object)(object)Player.Instance && __instance.attacking && (Object)(object)SyncRuntime.Instance != (Object)null)
		{
			SyncRuntime.Instance.SendLocalAttack(2);
		}
	}
}
