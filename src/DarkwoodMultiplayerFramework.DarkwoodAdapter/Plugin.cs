using BepInEx;
using HarmonyLib;
using UnityEngine;

namespace DarkwoodMultiplayerFramework.DarkwoodAdapter;

/// <summary>
/// Standalone 0.8 entry point. F1 starts Host, F2 starts Client, F3 stops the session.
/// </summary>
[BepInPlugin(Guid, Name, Version)]
public sealed class Plugin : BaseUnityPlugin
{
    public const string Guid = "com.darkwood.multiplayer.framework.rebuilt.adapter";
    public const string Name = "Darkwood Multiplayer Framework - Darkwood Adapter";
    public const string Version = "0.8.0-alpha.5";

    private GameObject? runtimeObject;
    private Harmony? harmony;

    private void Awake()
    {
        harmony = new Harmony(Guid);
        harmony.PatchAll(typeof(Plugin).Assembly);
        runtimeObject = new GameObject("DarkwoodMultiplayerRebuiltAdapter");
        DontDestroyOnLoad(runtimeObject);
        var runtime = runtimeObject.AddComponent<DarkwoodAdapterRuntime>();
        runtime.Initialize(Logger);
        runtime.Configure(Config);
        Logger.LogInfo("Darkwood adapter 0.8.0-alpha.5 loaded; acknowledged save transfer, dedicated world snapshots and bidirectional entity/player replication are ready (F1/F2/F3).");
    }

    private void OnDestroy()
    {
        harmony?.UnpatchSelf();
        harmony = null;
        if (runtimeObject != null)
        {
            Destroy(runtimeObject);
            runtimeObject = null;
        }
    }
}
