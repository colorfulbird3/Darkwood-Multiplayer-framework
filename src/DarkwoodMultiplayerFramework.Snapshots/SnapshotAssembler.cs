using System;
using System.Collections.Generic;
using System.Linq;
using DarkwoodMultiplayerFramework.Core;

namespace DarkwoodMultiplayerFramework.Snapshots;

public readonly struct SnapshotChunk
{
    public SnapshotChunk(Guid snapshotId, SnapshotPhase phase, int index, int total, byte[] payload)
    { SnapshotId = snapshotId; Phase = phase; Index = index; Total = total; Payload = payload; }
    public Guid SnapshotId { get; }
    public SnapshotPhase Phase { get; }
    public int Index { get; }
    public int Total { get; }
    public byte[] Payload { get; }
}

public sealed class SnapshotAssembler
{
    private readonly SortedDictionary<int, byte[]> chunks = new SortedDictionary<int, byte[]>();
    private Guid id;
    private int total;
    public bool IsComplete => total > 0 && chunks.Count == total;
    public void Add(SnapshotChunk chunk)
    {
        if (chunk.Total <= 0 || chunk.Index < 0 || chunk.Index >= chunk.Total) throw new ArgumentOutOfRangeException(nameof(chunk));
        if (chunks.Count == 0) { id = chunk.SnapshotId; total = chunk.Total; }
        if (chunk.SnapshotId != id || chunk.Total != total) throw new InvalidOperationException("Snapshot chunk belongs to another transfer.");
        chunks[chunk.Index] = chunk.Payload ?? throw new ArgumentNullException(nameof(chunk.Payload));
    }
    public byte[] Build()
    {
        if (!IsComplete) throw new InvalidOperationException("Snapshot is incomplete.");
        var length = chunks.Values.Sum(x => x.Length); var result = new byte[length]; var offset = 0;
        foreach (var part in chunks.Values) { Buffer.BlockCopy(part, 0, result, offset, part.Length); offset += part.Length; }
        return result;
    }
}
