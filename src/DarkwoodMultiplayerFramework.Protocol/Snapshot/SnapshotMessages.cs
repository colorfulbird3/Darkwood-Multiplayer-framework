using System;
using System.IO;
using System.Text;

namespace DarkwoodMultiplayerFramework.Protocol;

public readonly struct WorldSnapshotManifest
{
    public WorldSnapshotManifest(Guid snapshotId, long totalBytes, int chunkCount, byte[] sha256, string scene, string registryDigest, long serverTick)
    { SnapshotId=snapshotId; TotalBytes=totalBytes; ChunkCount=chunkCount; Sha256=sha256 ?? Array.Empty<byte>(); Scene=scene ?? string.Empty; RegistryDigest=registryDigest ?? string.Empty; ServerTick=serverTick; }
    public Guid SnapshotId { get; }
    public long TotalBytes { get; }
    public int ChunkCount { get; }
    public byte[] Sha256 { get; }
    public string Scene { get; }
    public string RegistryDigest { get; }
    public long ServerTick { get; }
}

public readonly struct WorldSnapshotChunk
{
    public WorldSnapshotChunk(Guid snapshotId, int index, int total, byte[] data, byte[] hash)
    { SnapshotId=snapshotId; Index=index; Total=total; Data=data ?? Array.Empty<byte>(); Hash=hash ?? Array.Empty<byte>(); }
    public Guid SnapshotId { get; }
    public int Index { get; }
    public int Total { get; }
    public byte[] Data { get; }
    public byte[] Hash { get; }
}

public readonly struct WorldSnapshotApplied
{
    public WorldSnapshotApplied(Guid snapshotId, string scene, string registryDigest, long serverTick, int entityCount)
    { SnapshotId=snapshotId; Scene=scene??string.Empty; RegistryDigest=registryDigest??string.Empty; ServerTick=serverTick; EntityCount=entityCount; }
    public Guid SnapshotId { get; } public string Scene { get; } public string RegistryDigest { get; } public long ServerTick { get; } public int EntityCount { get; }
}
