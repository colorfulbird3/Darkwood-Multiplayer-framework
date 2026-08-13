using Mirror;

namespace DarkwoodMultiplayerFramework;

public struct ReplicationWelcomeMessage : NetworkMessage
{
	public int Protocol;

	public uint Epoch;

	public uint ServerTick;

	public string Scene;

	public string Error;
}
