using Mirror;

namespace DarkwoodMultiplayerFramework;

public struct InventorySnapshotMessage : NetworkMessage
{
	public ulong Id;

	public uint Revision;

	public InventorySlotWire[] Slots;
}
