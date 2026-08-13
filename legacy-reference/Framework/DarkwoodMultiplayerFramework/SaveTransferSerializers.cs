using Mirror;

namespace DarkwoodMultiplayerFramework;

internal static class SaveTransferSerializers
{
	public static void Install()
	{
		Writer<SaveTransferRequest>.write = delegate(NetworkWriter w, SaveTransferRequest m)
		{
			w.WriteInt(m.Protocol);
		};
		Reader<SaveTransferRequest>.read = delegate(NetworkReader r)
		{
			SaveTransferRequest result = default(SaveTransferRequest);
			result.Protocol = r.ReadInt();
			return result;
		};
		Writer<SaveTransferManifest>.write = WriteManifest;
		Reader<SaveTransferManifest>.read = ReadManifest;
		Writer<SaveTransferChunk>.write = WriteChunk;
		Reader<SaveTransferChunk>.read = ReadChunk;
	}

	private static void WriteManifest(NetworkWriter w, SaveTransferManifest m)
	{
		w.WriteString(m.TransferId);
		w.WriteInt(m.ProfileId);
		w.WriteString(m.ProfileDescription);
		w.WriteInt(m.TotalBytes);
		w.WriteInt(m.ChunkCount);
		w.WriteString(m.Sha256);
		w.WriteString(m.Error);
	}

	private static SaveTransferManifest ReadManifest(NetworkReader r)
	{
		SaveTransferManifest result = default(SaveTransferManifest);
		result.TransferId = r.ReadString();
		result.ProfileId = r.ReadInt();
		result.ProfileDescription = r.ReadString();
		result.TotalBytes = r.ReadInt();
		result.ChunkCount = r.ReadInt();
		result.Sha256 = r.ReadString();
		result.Error = r.ReadString();
		return result;
	}

	private static void WriteChunk(NetworkWriter w, SaveTransferChunk m)
	{
		w.WriteString(m.TransferId);
		w.WriteInt(m.Index);
		w.WriteBytesAndSize(m.Data);
	}

	private static SaveTransferChunk ReadChunk(NetworkReader r)
	{
		SaveTransferChunk result = default(SaveTransferChunk);
		result.TransferId = r.ReadString();
		result.Index = r.ReadInt();
		result.Data = r.ReadBytesAndSize();
		return result;
	}
}
