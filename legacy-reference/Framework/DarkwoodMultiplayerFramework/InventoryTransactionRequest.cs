using Mirror;

namespace DarkwoodMultiplayerFramework;

public struct InventoryTransactionRequest : NetworkMessage
{
	public uint Epoch;

	public uint NetworkId;

	public uint ExpectedRevision;

	public uint OperationId;

	public InventorySlotWire[] Slots;
}
