using Mirror;

namespace DarkwoodMultiplayerFramework;

public struct SaveTransferChunk : NetworkMessage
{
	public string TransferId;

	public int Index;

	public byte[] Data;
}
