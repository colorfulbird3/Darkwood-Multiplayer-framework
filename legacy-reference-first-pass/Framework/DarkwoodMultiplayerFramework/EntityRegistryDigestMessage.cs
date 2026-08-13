using Mirror;

namespace DarkwoodMultiplayerFramework;

public struct EntityRegistryDigestMessage : NetworkMessage
{
	public string Scene;

	public int Count;

	public ulong Digest;
}
