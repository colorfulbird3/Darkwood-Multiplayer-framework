using HarmonyLib;
using System;
using System.IO;

namespace DarkwoodMultiplayerFramework.DarkwoodAdapter;

[HarmonyPatch(typeof(SaveManager),"get_baseSaveDirectory")]
internal static class DarkwoodSaveDirectoryPatch
{
    private static void Postfix(ref string __result)
    {
        var runtime=DarkwoodAdapterRuntime.Instance;
        // v0.9.2 TestHarness: TestMode Host also gets redirected to a per-slot directory under DMF_TestHarness/Saves
        // to keep user 1_4Save untouched.
        if (runtime != null && DarkwoodMultiplayerFramework.DarkwoodAdapter.Testing.TestConfig.Enabled
            && !string.IsNullOrEmpty(DarkwoodMultiplayerFramework.DarkwoodAdapter.Testing.TestConfig.SaveSlot))
        {
            var slot = DarkwoodMultiplayerFramework.DarkwoodAdapter.Testing.TestConfig.SaveSlot;
            var root = Path.Combine(UnityEngine.Application.persistentDataPath, "DMF_TestHarness", "Saves", slot);
            Directory.CreateDirectory(root);
            __result = root;
            return;
        }
        if(runtime!=null&&runtime.IsClient&&!string.IsNullOrEmpty(runtime.ActiveClientSaveDirectory))__result=runtime.ActiveClientSaveDirectory;
    }
}
