using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace DarkwoodMultiplayerFramework.Protocol;

/// <summary>
/// 实体身份/绑定（0.8.9 identity rework）：
/// Host 是唯一 EntityId authority。Host 在 World Stable 后构建权威实体描述清单
/// （AuthoritativeEntityDescriptor 的 wire 形式），随 BindingManifest 分块发给客户端；
/// 客户端扫描本地候选后按描述符显式绑定，不再独立 hash 生成网络 ID。
/// </summary>
public static class EntityKindWire
{
    // 与 DarkwoodEntityStateAdapter.Kind 对齐（Adapter 层解释，本层只透传）
    public const byte Character = 1;
    public const byte Door = 2;
    public const byte Window = 3;
    public const byte Item = 4;
    public const byte Inventory = 5;
}

/// <summary>权威实体描述符（wire）。Host 构建，Client 按此绑定本地组件。</summary>
public readonly struct EntityBindingEntryWire
{
    public EntityBindingEntryWire(ulong entityValue, byte kind, string componentType, long saveableUid, string relativePath, string objectName, float x, float y, float z)
    { EntityValue = entityValue; Kind = kind; ComponentType = componentType ?? string.Empty; SaveableUid = saveableUid; RelativePath = relativePath ?? string.Empty; ObjectName = objectName ?? string.Empty; X = x; Y = y; Z = z; }

    public ulong EntityValue { get; }
    public byte Kind { get; }
    public string ComponentType { get; }
    /// <summary>SaveableObject.uniqueId；0 表示无（需要走 kind+name+position 匹配）。</summary>
    public long SaveableUid { get; }
    /// <summary>组件相对 SaveableObject 的路径（含 #ordinal；两端一致时最精确，否则由 position 兜底）。</summary>
    public string RelativePath { get; }
    public string ObjectName { get; }
    public float X { get; } public float Y { get; } public float Z { get; }

    public string Describe() => $"{ComponentType} uid={SaveableUid} name={ObjectName} path={RelativePath} at ({X:F1},{Y:F1},{Z:F1})";
}

/// <summary>BindingManifest 头：分块装配参数（复用 ChunkTransferAssembler）。</summary>
public readonly struct EntityBindingManifest
{
    public EntityBindingManifest(Guid transferId, long totalBytes, int chunkCount, byte[] sha256, string scene, int generation, int entityCount)
    { TransferId = transferId; TotalBytes = totalBytes; ChunkCount = chunkCount; Sha256 = sha256 ?? Array.Empty<byte>(); Scene = scene ?? string.Empty; Generation = generation; EntityCount = entityCount; }
    public Guid TransferId { get; }
    public long TotalBytes { get; }
    public int ChunkCount { get; }
    public byte[] Sha256 { get; }
    public string Scene { get; }
    /// <summary>注册表代际：主机重建权威注册表时递增；客户端收到不同代际必须清空旧映射重绑。</summary>
    public int Generation { get; }
    public int EntityCount { get; }
}

public readonly struct EntityBindingChunk
{
    public EntityBindingChunk(Guid transferId, int index, int total, byte[] data, byte[] hash)
    { TransferId = transferId; Index = index; Total = total; Data = data ?? Array.Empty<byte>(); Hash = hash ?? Array.Empty<byte>(); }
    public Guid TransferId { get; }
    public int Index { get; }
    public int Total { get; }
    public byte[] Data { get; }
    public byte[] Hash { get; }
}

/// <summary>客户端本地候选实体（无网络身份，仅描述符）。</summary>
public readonly struct LocalEntityCandidate
{
    public LocalEntityCandidate(string componentType, long saveableUid, string relativePath, string objectName, float x, float y, float z)
    { ComponentType = componentType ?? string.Empty; SaveableUid = saveableUid; RelativePath = relativePath ?? string.Empty; ObjectName = objectName ?? string.Empty; X = x; Y = y; Z = z; }
    public string ComponentType { get; }
    public long SaveableUid { get; }
    public string RelativePath { get; }
    public string ObjectName { get; }
    public float X { get; } public float Y { get; } public float Z { get; }
}

/// <summary>Replication Apply 统计（禁止 silent continue）。</summary>
public sealed class ApplyStats
{
    public int Received;
    public int Applied;
    public int Missing;
    public int Stale;
    public int Ambiguous;
    public readonly List<string> MissingDetails = new List<string>();
    public readonly List<string> AmbiguousDetails = new List<string>();
    public readonly Dictionary<byte,int> MissingByKind = new Dictionary<byte,int>();

    public const int MaxRecorded = 20;
    public void RecordMissing(string detail, byte kind) { Missing++; MissingByKind.TryGetValue(kind, out var n); MissingByKind[kind] = n + 1; if (MissingDetails.Count < MaxRecorded) MissingDetails.Add(detail); }
    public void RecordAmbiguous(string detail) { Ambiguous++; if (AmbiguousDetails.Count < MaxRecorded) AmbiguousDetails.Add(detail); }
    public string Summary() => $"received={Received} applied={Applied} missing={Missing} stale={Stale} ambiguous={Ambiguous}";
}

/// <summary>绑定结果（纯逻辑，可单测）。</summary>
public sealed class EntityBindingOutcome
{
    public int Total;
    public int Bound;
    public readonly List<EntityBindingPair> Pairs = new List<EntityBindingPair>();
    public readonly List<int> Missing = new List<int>();
    public readonly List<int> Ambiguous = new List<int>();
    public readonly List<string> MissingDetails = new List<string>();
    public readonly List<string> AmbiguousDetails = new List<string>();

    public const int MaxRecorded = 20;
    public void RecordMissing(int entryIndex, string detail) { Missing.Add(entryIndex); if (MissingDetails.Count < MaxRecorded) MissingDetails.Add(detail); }
    public void RecordAmbiguous(int entryIndex, string detail) { Ambiguous.Add(entryIndex); if (AmbiguousDetails.Count < MaxRecorded) AmbiguousDetails.Add(detail); }
}

public readonly struct EntityBindingPair
{
    public EntityBindingPair(int entryIndex, int localIndex) { EntryIndex = entryIndex; LocalIndex = localIndex; }
    public int EntryIndex { get; }
    public int LocalIndex { get; }
}

/// <summary>Ready Gate（纯逻辑）：关键实体类别存在 unmatched 时禁止就绪。</summary>
public static class EntityBindingGate
{
    /// <summary>返回 null 表示可继续；否则返回禁止原因（含各类别 missing 数）。</summary>
    public static string? Evaluate(ApplyStats stats, params byte[] criticalKinds)
    {
        foreach (var kind in criticalKinds)
            if (stats.MissingByKind.TryGetValue(kind, out var n) && n > 0)
                return $"关键实体类别 kind={kind} 绑定缺失 {n} 个，禁止进入就绪。";
        return null;
    }

    /// <summary>统计指定类别的 missing 总数（容差 gate 用）。</summary>
    public static int CountMissing(ApplyStats stats, params byte[] kinds)
    {
        var total = 0;
        foreach (var kind in kinds)
            if (stats.MissingByKind.TryGetValue(kind, out var n))
                total += n;
        return total;
    }
}

/// <summary>
/// 权威描述符 → 本地候选 的显式匹配器。
/// 匹配优先级：
///   A. SaveableUid + ComponentType + RelativePath 完全一致（唯一）
///   B. SaveableUid + ComponentType 一致，path 不同 → position 容差内唯一
///   C. 无 uid：ComponentType + ObjectName + position 容差内唯一
/// 禁止无约束最近邻；多候选一律记 ambiguous 不绑定。
/// </summary>
public sealed class EntityBindingMatcher
{
    public const float PositionTolerance = 2f; // 米

    public EntityBindingOutcome Match(EntityBindingEntryWire[] authoritative, LocalEntityCandidate[] local)
    {
        var outcome = new EntityBindingOutcome { Total = authoritative.Length };
        var usedLocal = new bool[local.Length];
        for (var i = 0; i < authoritative.Length; i++)
        {
            var entry = authoritative[i];
            var matched = -1;
            var ambiguousCount = 0;
            for (var j = 0; j < local.Length; j++)
            {
                if (usedLocal[j]) continue;
                if (!string.Equals(local[j].ComponentType, entry.ComponentType, StringComparison.Ordinal)) continue;
                var level = ScoreMatch(entry, local[j]);
                if (level == MatchLevel.None) continue;
                if (matched < 0) { matched = j; ambiguousCount = 1; }
                else ambiguousCount++;
            }
            if (ambiguousCount > 1)
            {
                outcome.RecordAmbiguous(i, entry.Describe());
                continue;
            }
            if (matched < 0) { outcome.RecordMissing(i, entry.Describe()); continue; }
            usedLocal[matched] = true;
            outcome.Pairs.Add(new EntityBindingPair(i, matched));
            outcome.Bound++;
        }
        return outcome;
    }

    private enum MatchLevel { None, C, B, A }

    private static MatchLevel ScoreMatch(EntityBindingEntryWire entry, LocalEntityCandidate candidate)
    {
        var uidMatch = entry.SaveableUid > 0 && entry.SaveableUid == candidate.SaveableUid;
        var pathMatch = entry.RelativePath.Length > 0 && string.Equals(entry.RelativePath, candidate.RelativePath, StringComparison.Ordinal);
        var nameMatch = entry.ObjectName.Length > 0 && string.Equals(entry.ObjectName, candidate.ObjectName, StringComparison.Ordinal);
        var distance = Distance(entry, candidate);
        var inRange = distance <= PositionTolerance * PositionTolerance;
        if (uidMatch && pathMatch) return MatchLevel.A;
        if (uidMatch && inRange) return MatchLevel.B;
        if (entry.SaveableUid <= 0 && nameMatch && inRange) return MatchLevel.C;
        return MatchLevel.None;
    }

    private static float Distance(EntityBindingEntryWire entry, LocalEntityCandidate candidate)
    {
        var dx = entry.X - candidate.X; var dy = entry.Y - candidate.Y; var dz = entry.Z - candidate.Z;
        return dx * dx + dy * dy + dz * dz;
    }
}
