namespace DarkwoodMultiplayerFramework;

public struct EntityDeltaWire
{
	public uint NetworkId;

	public uint Revision;

	public ushort DirtyMask;

	public WorldEntityState State;
}
