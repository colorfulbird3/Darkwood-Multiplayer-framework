using Mirror;

namespace DarkwoodMultiplayerFramework;

public struct ReplicationDigestMessage : NetworkMessage
{
	public uint Epoch;

	public uint ServerTick;

	public string Scene;

	public int Count;

	public ulong Digest;
}
