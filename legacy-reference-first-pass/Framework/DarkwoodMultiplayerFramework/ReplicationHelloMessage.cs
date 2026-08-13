using Mirror;

namespace DarkwoodMultiplayerFramework;

public struct ReplicationHelloMessage : NetworkMessage
{
	public int Protocol;

	public string Version;
}
