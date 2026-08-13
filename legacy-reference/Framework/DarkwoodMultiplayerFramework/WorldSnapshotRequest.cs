using Mirror;

namespace DarkwoodMultiplayerFramework;

public struct WorldSnapshotRequest : NetworkMessage
{
	public string Scene;

	public ulong RegistryDigest;
}
