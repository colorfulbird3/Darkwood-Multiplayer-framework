using Mirror;

namespace DarkwoodMultiplayerFramework;

public struct EntityDespawnBatchMessage : NetworkMessage
{
	public uint Epoch;

	public uint ServerTick;

	public string Scene;

	public EntityDespawnWire[] Entities;
}
