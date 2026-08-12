using BepInEx;
using UnityEngine;

namespace DarkwoodMultiplayerFramework.DarkwoodAdapter;

/// <summary>
/// Local migration entry point. It observes the proven 0.7 runtime and exposes
/// Darkwood objects to the rebuilt modules without starting another transport.
/// </summary>
[BepInPlugin(Guid, Name, Version)]
[BepInDependency(LegacyGuid, BepInDependency.DependencyFlags.SoftDependency)]
public sealed class Plugin : BaseUnityPlugin
{
    public const string Guid = "com.darkwood.multiplayer.framework.rebuilt.adapter";
    public const string LegacyGuid = "com.darkwood.multiplayer.framework.v2";
    public const string Name = "Darkwood Multiplayer Framework - Darkwood Adapter";
    public const string Version = "0.8.0-alpha.2";

    private GameObject? runtimeObject;

    private void Awake()
    {
        runtimeObject = new GameObject("DarkwoodMultiplayerRebuiltAdapter");
        DontDestroyOnLoad(runtimeObject);
        runtimeObject.AddComponent<DarkwoodAdapterRuntime>().Initialize(Logger);
        Logger.LogInfo("Darkwood adapter 0.8.0-alpha.2 loaded in compatibility mode; transport remains owned by 0.7.");
    }

    private void OnDestroy()
    {
        if (runtimeObject != null)
        {
            Destroy(runtimeObject);
            runtimeObject = null;
        }
    }
}
