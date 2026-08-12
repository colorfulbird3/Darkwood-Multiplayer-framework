using HarmonyLib;

namespace DarkwoodMultiplayerFramework.DarkwoodAdapter;

[HarmonyPatch(typeof(SaveManager),"get_baseSaveDirectory")]
internal static class DarkwoodSaveDirectoryPatch
{
    private static void Postfix(ref string __result)
    {
        var runtime=DarkwoodAdapterRuntime.Instance;
        if(runtime!=null&&runtime.IsClient&&!string.IsNullOrEmpty(runtime.ActiveClientSaveDirectory))__result=runtime.ActiveClientSaveDirectory;
    }
}
