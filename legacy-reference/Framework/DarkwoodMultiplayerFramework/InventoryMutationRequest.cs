using Mirror;

namespace DarkwoodMultiplayerFramework;

public struct InventoryMutationRequest : NetworkMessage
{
	public ulong Id;

	public InventorySlotWire[] Slots;
}
