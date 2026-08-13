using HarmonyLib;
using Mirror;

namespace DarkwoodMultiplayerFramework;

[HarmonyPatch(typeof(SaveManager), "get_baseSaveDirectory")]
internal static class MultiplayerSaveDirectoryPatch
{
	private static void Postfix(ref string __result)
	{
		if (!string.IsNullOrEmpty(SaveTransferRuntime.ActiveClientSaveDirectory) && !NetworkServer.active)
		{
			__result = SaveTransferRuntime.ActiveClientSaveDirectory;
		}
	}
}
