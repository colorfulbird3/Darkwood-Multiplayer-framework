using BepInEx;

namespace DarkwoodMultiplayerFramework.Bootstrap;

[BepInPlugin("com.darkwood.multiplayer.framework.rebuilt", "Darkwood Multiplayer Framework Rebuilt", "0.8.7-alpha.1")]
public sealed class Plugin : BaseUnityPlugin
{
    private void Awake() => Logger.LogInfo("Standalone 0.8.6 Action Core bootstrap loaded. Legacy 0.7 runtime is not required.");
}
