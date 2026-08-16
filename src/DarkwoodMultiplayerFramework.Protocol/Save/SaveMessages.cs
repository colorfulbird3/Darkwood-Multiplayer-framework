using System;
using System.IO;
using System.Text;

namespace DarkwoodMultiplayerFramework.Protocol;

public readonly struct SaveTransferRequest
{
    public SaveTransferRequest(Guid requestId) { RequestId = requestId; }
    public Guid RequestId { get; }
}

public readonly struct SaveTransferManifest
{
    public SaveTransferManifest(Guid transferId, int profileId, long totalBytes, int chunkCount, byte[] sha256, string description)
    { TransferId=transferId; ProfileId=profileId; TotalBytes=totalBytes; ChunkCount=chunkCount; Sha256=sha256 ?? Array.Empty<byte>(); Description=description ?? string.Empty; }
    public Guid TransferId { get; }
    public int ProfileId { get; }
    public long TotalBytes { get; }
    public int ChunkCount { get; }
    public byte[] Sha256 { get; }
    public string Description { get; }
}

public readonly struct SaveTransferChunk
{
    public SaveTransferChunk(Guid transferId, int index, int total, byte[] data, byte[] hash)
    { TransferId=transferId; Index=index; Total=total; Data=data ?? Array.Empty<byte>(); Hash=hash ?? Array.Empty<byte>(); }
    public Guid TransferId { get; }
    public int Index { get; }
    public int Total { get; }
    public byte[] Data { get; }
    public byte[] Hash { get; }
}

public readonly struct SaveTransferApplied
{
    public SaveTransferApplied(Guid transferId, int profileId, string saveDirectory)
    { TransferId=transferId; ProfileId=profileId; SaveDirectory=saveDirectory??string.Empty; }
    public Guid TransferId { get; } public int ProfileId { get; } public string SaveDirectory { get; }
}
