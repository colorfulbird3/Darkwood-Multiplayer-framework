using Mirror;

namespace DarkwoodMultiplayerFramework;

public struct ReplicationReadyMessage : NetworkMessage
{
	public uint Epoch;

	public string Scene;

	public ulong RegistryDigest;
}
