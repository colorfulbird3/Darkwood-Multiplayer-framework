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
    public const string PluginVersion = "0.8.7.17";
    public const string DisplayVersion = "0.8.7-alpha.17";
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
        Logger.LogInfo("Darkwood adapter 0.8.7-alpha.17 loaded; single-version handshake gate; authoritative container/melee/interaction sync; hot-join guest profiles; downed/rescue system; forced load branch + joinPaths skip on client (FIX-003/004) + registry stabilization before Ready.");
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
