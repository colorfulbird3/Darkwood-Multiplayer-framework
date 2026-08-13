using Mirror;

namespace DarkwoodMultiplayerFramework;

public struct InventoryStateMessage : NetworkMessage
{
	public uint Epoch;

	public uint ServerTick;

	public uint NetworkId;

	public uint Revision;

	public uint OperationId;

	public bool Accepted;

	public InventorySlotWire[] Slots;
}
