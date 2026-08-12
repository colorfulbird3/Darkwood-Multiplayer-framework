using BepInEx;
using Mirror;

namespace DarkwoodMultiplayerFramework.Bootstrap;

[BepInPlugin("com.darkwood.multiplayer.framework.rebuilt", "Darkwood Multiplayer Framework Rebuilt", "0.8.0-alpha.1")]
public sealed class Plugin : BaseUnityPlugin
{
    private void Awake() => Logger.LogInfo("Rebuilt 0.8 architecture bootstrap loaded. Legacy 0.7 gameplay hooks are not enabled.");
}

public struct LifecycleMessage : NetworkMessage
{
    public byte State;
    public string ProtocolVersion;
    public string RegistryDigest;
}

public struct SnapshotChunkMessage : NetworkMessage
{
    public string SnapshotId;
    public byte Phase;
    public int Index;
    public int Total;
    public byte[] Payload;
}

public struct ActionRequestMessage : NetworkMessage
{
    public string RequestId;
    public int PlayerId;
    public byte Kind;
    public ulong EntityValue;
    public bool PersistentEntity;
    public ulong ExpectedVersion;
    public byte[] Payload;
}
