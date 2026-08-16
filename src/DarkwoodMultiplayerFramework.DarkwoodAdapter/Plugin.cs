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
    public const string PluginVersion = "0.8.8.10";
    public const string DisplayVersion = "0.8.8-beta.1";
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
        Logger.LogInfo("Darkwood adapter 0.8.8-beta.1 loaded; single-version handshake gate; trust model (FIX-011/012); runtime entity registry; runtime loot containers + enemy proxies; default-spawn-point birth (FIX-013); scene-change auto-reconnect (SceneChange message, client reloads new scene save); loopback self-test (SelfTestAuto full chain, F7/F8 manual); segmented load-start diagnostics; stable loot-scale ledger (FIX-008); FIX-003..007 load/snapshot fixes + registry stabilization before Ready.");
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
