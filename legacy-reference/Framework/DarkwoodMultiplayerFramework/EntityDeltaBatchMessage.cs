using Mirror;

namespace DarkwoodMultiplayerFramework;

public struct EntityDeltaBatchMessage : NetworkMessage
{
	public uint Epoch;

	public uint ServerTick;

	public string Scene;

	public EntityDeltaWire[] Entities;
}
