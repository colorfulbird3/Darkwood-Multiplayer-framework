using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using DarkwoodMultiplayerFramework.Core;

namespace DarkwoodMultiplayerFramework.Snapshots;

public readonly struct SnapshotChunk
{
    public SnapshotChunk(Guid snapshotId, SnapshotPhase phase, int index, int total, byte[] payload, byte[]? chunkHash = null)
    { SnapshotId = snapshotId; Phase = phase; Index = index; Total = total; Payload = payload; ChunkHash = chunkHash ?? ComputeHash(payload); }
    public Guid SnapshotId { get; }
    public SnapshotPhase Phase { get; }
    public int Index { get; }
    public int Total { get; }
    public byte[] Payload { get; }
    public byte[] ChunkHash { get; }
    private static byte[] ComputeHash(byte[] payload) { using var sha=SHA256.Create(); return sha.ComputeHash(payload ?? Array.Empty<byte>()); }
}

public sealed class SnapshotAssembler
{
    private readonly SortedDictionary<int, byte[]> chunks = new SortedDictionary<int, byte[]>();
    private Guid id;
    private SnapshotPhase phase;
    private int total;
    private long size;
    public int MaxChunkSize { get; set; } = 256 * 1024;
    public long MaxSnapshotSize { get; set; } = 64L * 1024 * 1024;
    public bool IsComplete => total > 0 && chunks.Count == total;
    public void Add(SnapshotChunk chunk)
    {
        if (chunk.Total <= 0 || chunk.Index < 0 || chunk.Index >= chunk.Total) throw new ArgumentOutOfRangeException(nameof(chunk));
        if (chunk.Payload == null) throw new ArgumentNullException(nameof(chunk.Payload));
        if (chunk.Payload.Length > MaxChunkSize) throw new InvalidOperationException("Snapshot chunk exceeds the configured limit.");
        if (!HashMatches(chunk.Payload, chunk.ChunkHash)) throw new InvalidOperationException("Snapshot chunk hash mismatch.");
        if (chunks.Count == 0) { id = chunk.SnapshotId; phase = chunk.Phase; total = chunk.Total; }
        if (chunk.SnapshotId != id || chunk.Phase != phase || chunk.Total != total) throw new InvalidOperationException("Snapshot chunk belongs to another transfer or phase.");
        if (chunks.TryGetValue(chunk.Index, out var previous)) size -= previous.Length;
        size += chunk.Payload.Length;
        if (size > MaxSnapshotSize) throw new InvalidOperationException("Snapshot exceeds the configured limit.");
        chunks[chunk.Index] = chunk.Payload;
    }
    public byte[] Build()
    {
        if (!IsComplete) throw new InvalidOperationException("Snapshot is incomplete.");
        var length = chunks.Values.Sum(x => x.Length); var result = new byte[length]; var offset = 0;
        foreach (var part in chunks.Values) { Buffer.BlockCopy(part, 0, result, offset, part.Length); offset += part.Length; }
        return result;
    }
    private static bool HashMatches(byte[] payload, byte[] expected) { if(expected==null) return false; using var sha=SHA256.Create(); var actual=sha.ComputeHash(payload); return actual.SequenceEqual(expected); }
}
