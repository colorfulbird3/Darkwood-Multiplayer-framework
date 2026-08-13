using Mirror;

namespace DarkwoodMultiplayerFramework;

public struct EntitySpawnBatchMessage : NetworkMessage
{
	public uint Epoch;

	public uint ServerTick;

	public string Scene;

	public EntitySpawnWire[] Entities;
}
