using Mirror;

namespace DarkwoodMultiplayerFramework;

public struct HostSceneMessage : NetworkMessage
{
	public uint Revision;

	public string Scene;

	public int BuildIndex;
}
