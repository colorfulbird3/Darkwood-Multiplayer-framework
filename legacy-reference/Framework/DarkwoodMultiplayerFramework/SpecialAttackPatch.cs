using HarmonyLib;

namespace DarkwoodMultiplayerFramework;

[HarmonyPatch(typeof(Player), "initiateSpecialAttack")]
internal static class SpecialAttackPatch
{
	private static void Postfix(Player __instance)
	{
		if (__instance == Player.Instance && __instance.attacking && SyncRuntime.Instance != null)
		{
			SyncRuntime.Instance.SendLocalAttack(2);
		}
	}
}
