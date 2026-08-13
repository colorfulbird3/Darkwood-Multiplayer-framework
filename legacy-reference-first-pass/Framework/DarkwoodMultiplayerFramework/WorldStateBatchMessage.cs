using Mirror;

namespace DarkwoodMultiplayerFramework;

public struct WorldStateBatchMessage : NetworkMessage
{
	public uint Revision;

	public string Scene;

	public WorldEntityState[] States;
}
