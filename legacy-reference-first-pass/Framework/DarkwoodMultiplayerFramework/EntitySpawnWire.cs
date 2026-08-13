namespace DarkwoodMultiplayerFramework;

public struct EntitySpawnWire
{
	public uint NetworkId;

	public ulong PersistentId;

	public byte Kind;

	public uint StateRevision;

	public uint InventoryRevision;

	public WorldEntityState State;

	public InventorySlotWire[] Inventory;
}
