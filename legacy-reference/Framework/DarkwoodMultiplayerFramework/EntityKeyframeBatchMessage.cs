using Mirror;

namespace DarkwoodMultiplayerFramework;

public struct EntityKeyframeBatchMessage : NetworkMessage
{
	public uint Epoch;

	public uint ServerTick;

	public string Scene;

	public EntityDeltaWire[] Entities;
}
