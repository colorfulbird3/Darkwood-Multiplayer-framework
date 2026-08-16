using BepInEx;
using HarmonyLib;
using UnityEngine;

namespace DarkwoodMultiplayerFramework.DarkwoodAdapter;

/// <summary>
/// Standalone 0.8 entry point. F1 starts Host, F2 starts Client, F3 stops the session.
/// </summary>
[BepInPlugin(Guid, Name, PluginVersion)]
public sealed class Plugin : BaseUnityPlugin
{
    public const string Guid = "com.darkwood.multiplayer.framework.rebuilt.adapter";
    public const string Name = "Darkwood Multiplayer Framework - Darkwood Adapter";
    // BepInEx 5 parses this value as System.Version while scanning plugins.
    // A SemVer prerelease suffix (for example 0.8.7-alpha.1) makes the
    // chainloader silently skip the assembly and report "0 plugins to load".
    public const string PluginVersion = "0.8.9.3";
    public const string DisplayVersion = "0.8.9-beta.3";
    public const string Version = DisplayVersion;

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
        runtimeObject.AddComponent<DarkwoodMultiplayerPanel>();
        runtimeObject.AddComponent<DarkwoodRescueOverlay>();
        var selfTest = runtimeObject.AddComponent<DarkwoodSelfTestClient>();
        if (runtime.AutoSelfTest) selfTest.AutoStart();
        Logger.LogInfo("Darkwood adapter 0.8.9-beta.1 loaded; architecture refactor complete (8 partials + SessionContext + MessageRouter + domain protocol files + real transport channel model + host/client tick split + runtime entity lifecycle model); feature parity with 0.8.8-beta.5 (runtime entities, dropped items, container revision, rescue, despawn targeting); single-version handshake gate; trust model; loopback self-test (SelfTestAuto full chain, F7/F8 manual).");
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
