using Mirror;

namespace DarkwoodMultiplayerFramework;

public struct SaveTransferManifest : NetworkMessage
{
	public string TransferId;

	public int ProfileId;

	public string ProfileDescription;

	public int TotalBytes;

	public int ChunkCount;

	public string Sha256;

	public string Error;
}
