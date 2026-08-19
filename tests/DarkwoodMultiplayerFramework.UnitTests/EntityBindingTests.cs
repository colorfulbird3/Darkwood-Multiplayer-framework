using System;
using DarkwoodMultiplayerFramework.Protocol;
using Xunit;

namespace DarkwoodMultiplayerFramework.UnitTests;

/// <summary>
/// 0.8.9 identity rework：权威描述符绑定（显式匹配，禁止无约束最近邻；A→B→C 三阶段严格降级）。
/// </summary>
public class EntityBindingTests
{
    private static EntityBindingEntryWire Entry(ulong value, byte kind, string type, long uid, string path, string name, float x, float y, float z) =>
        new EntityBindingEntryWire(value, kind, type, uid, path, name, x, y, z);

    private static LocalEntityCandidate Candidate(string type, long uid, string path, string name, float x, float y, float z) =>
        new LocalEntityCandidate(type, uid, path, name, x, y, z);

    /// <summary>1. Host/Client hierarchy sibling 顺序不同（path 不同），uid+type+position 仍绑定同一实体（B 级 fallback）。</summary>
    [Fact]
    public void Sibling_Order_Differs_Still_Binds()
    {
        var entries = new[] { Entry(100, EntityKindWire.Item, "Item", 42, "Root/Chest#0/Slot#2", "Chest", 10f, 0f, 10f) };
        var local = new[] { Candidate("Item", 42, "Root/Chest#1/Slot#2", "Chest", 10f, 0f, 10f) };
        var outcome = new EntityBindingMatcher().Match(entries, local);
        Assert.Equal(1, outcome.Bound);
        Assert.Empty(outcome.Missing);
        Assert.Empty(outcome.Ambiguous);
        Assert.Equal(0, outcome.Pairs[0].LocalIndex);
    }

    /// <summary>2. A（uid+type+path）与 B（uid+type+position）同时存在时必须选 A，不得 ambiguous。</summary>
    [Fact]
    public void A_Beats_B_When_Both_Present()
    {
        var entries = new[] { Entry(100, EntityKindWire.Item, "Item", 42, "Root/Item#0", "Item", 10f, 0f, 10f) };
        var local = new[]
        {
            Candidate("Item", 42, "Root/Item#0", "Item", 10f, 0f, 10f),   // A 级
            Candidate("Item", 42, "Root/Item#1", "Item", 10.5f, 0f, 10.5f) // B 级（path 不同）
        };
        var outcome = new EntityBindingMatcher().Match(entries, local);
        Assert.Equal(1, outcome.Bound);
        Assert.Equal(0, outcome.Pairs[0].LocalIndex); // 绑定 A 级候选
        Assert.Empty(outcome.Ambiguous);
    }

    /// <summary>3. B 级 entry 与 C 级 entry 争同一候选时，B 先绑（Pass B 先于 Pass C）。</summary>
    [Fact]
    public void B_Beats_C_When_Shared_Candidate()
    {
        var entries = new[]
        {
            Entry(100, EntityKindWire.Item, "Item", 42, "Root/A#1", "Same", 10f, 0f, 10f), // B 级（uid+position，path 不同）
            Entry(101, EntityKindWire.Item, "Item", 0, "", "Same", 10f, 0f, 10f)           // C 级（无 uid+name）
        };
        var local = new[] { Candidate("Item", 42, "Root/A#2", "Same", 10f, 0f, 10f) };
        var outcome = new EntityBindingMatcher().Match(entries, local);
        Assert.Equal(1, outcome.Bound);
        Assert.Equal(100UL, entries[outcome.Pairs[0].EntryIndex].EntityValue); // B 级 entry 先抢到
        Assert.Single(outcome.Missing);
        Assert.Equal(101UL, entries[outcome.Missing[0]].EntityValue); // C 级 entry 空手
    }

    /// <summary>4. 两个同等级 A 候选 → ambiguous，一个都不绑。</summary>
    [Fact]
    public void Two_Exact_Matches_Are_Ambiguous()
    {
        var entries = new[] { Entry(100, EntityKindWire.Item, "Item", 5, "Root/X#0", "X", 10f, 0f, 10f) };
        var local = new[]
        {
            Candidate("Item", 5, "Root/X#0", "X", 10f, 0f, 10f),
            Candidate("Item", 5, "Root/X#0", "X", 10f, 0f, 10f)
        };
        var outcome = new EntityBindingMatcher().Match(entries, local);
        Assert.Equal(0, outcome.Bound);
        Assert.Single(outcome.Ambiguous);
        Assert.Empty(outcome.Missing);
    }

    /// <summary>5. Host 比 Client 多 runtime object：多出的条目统计为 missing，不误绑。</summary>
    [Fact]
    public void Host_Has_More_Objects_Than_Client()
    {
        var entries = new[]
        {
            Entry(100, EntityKindWire.Item, "Item", 1, "A", "Chair", 1f, 0f, 1f),
            Entry(101, EntityKindWire.Item, "Item", 2, "A", "Table", 2f, 0f, 2f),
            Entry(102, EntityKindWire.Inventory, "Inventory", 3, "A", "Chest", 3f, 0f, 3f)
        };
        var local = new[]
        {
            Candidate("Item", 1, "A", "Chair", 1f, 0f, 1f),
            Candidate("Item", 2, "A", "Table", 2f, 0f, 2f)
        };
        var outcome = new EntityBindingMatcher().Match(entries, local);
        Assert.Equal(2, outcome.Bound);
        Assert.Single(outcome.Missing);
        Assert.Equal(102UL, entries[outcome.Missing[0]].EntityValue);
    }

    /// <summary>6. Client 比 Host 少对象且缺口在中间：后续条目仍正确绑定。</summary>
    [Fact]
    public void Client_Missing_Middle_Object_Still_Binds_Others()
    {
        var entries = new[]
        {
            Entry(100, EntityKindWire.Door, "Door", 1, "A", "DoorA", 1f, 0f, 1f),
            Entry(101, EntityKindWire.Window, "Window", 2, "A", "WindowB", 2f, 0f, 2f),
            Entry(102, EntityKindWire.Item, "Item", 3, "A", "Lamp", 3f, 0f, 3f)
        };
        var local = new[]
        {
            Candidate("Door", 1, "A", "DoorA", 1f, 0f, 1f),
            Candidate("Item", 3, "A", "Lamp", 3f, 0f, 3f)
        };
        var outcome = new EntityBindingMatcher().Match(entries, local);
        Assert.Equal(2, outcome.Bound);
        Assert.Single(outcome.Missing);
        Assert.Equal(101UL, entries[outcome.Missing[0]].EntityValue);
        Assert.Equal(0, outcome.Pairs[0].LocalIndex);
        Assert.Equal(1, outcome.Pairs[1].LocalIndex);
    }

    /// <summary>7. 同名多个对象：靠 uid 区分，不误绑。</summary>
    [Fact]
    public void Same_Name_Multiple_Not_Misbound()
    {
        var entries = new[]
        {
            Entry(100, EntityKindWire.Inventory, "Inventory", 11, "A", "Chest", 1f, 0f, 1f),
            Entry(101, EntityKindWire.Inventory, "Inventory", 12, "A", "Chest", 50f, 0f, 50f)
        };
        var local = new[]
        {
            Candidate("Inventory", 12, "A", "Chest", 50f, 0f, 50f),
            Candidate("Inventory", 11, "A", "Chest", 1f, 0f, 1f)
        };
        var outcome = new EntityBindingMatcher().Match(entries, local);
        Assert.Equal(2, outcome.Bound);
        Assert.Empty(outcome.Missing);
        Assert.Empty(outcome.Ambiguous);
        Assert.Equal(1, outcome.Pairs[0].LocalIndex); // entry 100 (uid=11) → local[1]
        Assert.Equal(0, outcome.Pairs[1].LocalIndex); // entry 101 (uid=12) → local[0]
    }

    /// <summary>8. unknown authoritative id（客户端无对应对象）→ 统计为 missing 并记录详情。</summary>
    [Fact]
    public void Unknown_Authoritative_Id_Is_Missing()
    {
        var entries = new[] { Entry(999, EntityKindWire.Character, "Character", 0, "", "Wolf", 5f, 0f, 5f) };
        var local = new[] { Candidate("Door", 1, "A", "Door", 1f, 0f, 1f) };
        var outcome = new EntityBindingMatcher().Match(entries, local);
        Assert.Equal(0, outcome.Bound);
        Assert.Single(outcome.Missing);
        Assert.Contains("Wolf", outcome.MissingDetails[0]);
    }

    /// <summary>9. registry generation 换代：新 manifest 重绑后旧 generation 的 id 不再出现在映射中。</summary>
    [Fact]
    public void Generation_Change_Requires_Rebind()
    {
        var gen1 = new[]
        {
            Entry(100, EntityKindWire.Item, "Item", 1, "A", "Chair", 1f, 0f, 1f),
            Entry(101, EntityKindWire.Item, "Item", 2, "A", "Table", 2f, 0f, 2f)
        };
        var local = new[]
        {
            Candidate("Item", 1, "A", "Chair", 1f, 0f, 1f),
            Candidate("Item", 2, "A", "Table", 2f, 0f, 2f)
        };
        var first = new EntityBindingMatcher().Match(gen1, local);
        Assert.Equal(2, first.Bound);

        // 第 2 代：主机重建注册表，id 全部重新分配（同一批本地对象，descriptor 不变）
        var gen2 = new[]
        {
            Entry(500, EntityKindWire.Item, "Item", 1, "A", "Chair", 1f, 0f, 1f),
            Entry(501, EntityKindWire.Item, "Item", 2, "A", "Table", 2f, 0f, 2f)
        };
        var second = new EntityBindingMatcher().Match(gen2, local);
        Assert.Equal(2, second.Bound);
        Assert.Equal(500UL, gen2[second.Pairs[0].EntryIndex].EntityValue);
        Assert.Equal(501UL, gen2[second.Pairs[1].EntryIndex].EntityValue);
        foreach (var pair in second.Pairs)
        {
            Assert.NotEqual(100UL, gen2[pair.EntryIndex].EntityValue);
            Assert.NotEqual(101UL, gen2[pair.EntryIndex].EntityValue);
        }
        Assert.Empty(second.Ambiguous);
    }

    /// <summary>generation 随 wire 传输：换代 manifest 与旧 manifest 不同代，客户端据此重建。</summary>
    [Fact]
    public void Generation_Roundtrips_Through_Wire()
    {
        var manifest = new EntityBindingManifest(Guid.NewGuid(), 1234, 3, new byte[] { 1, 2, 3 }, "Darkwood", 7, 42);
        var decoded = ReplicationProtocolCodec.DecodeEntityBindingManifest(ReplicationProtocolCodec.Encode(manifest));
        Assert.Equal(7, decoded.Generation);
        Assert.Equal(42, decoded.EntityCount);
        Assert.Equal("Darkwood", decoded.Scene);
        Assert.Equal(1234L, decoded.TotalBytes);
    }

    /// <summary>Binding entries 数组 codec roundtrip（含 uid/path/name/kind/position）。</summary>
    [Fact]
    public void Binding_Entries_Roundtrip()
    {
        var entries = new[]
        {
            Entry(100, EntityKindWire.Character, "Character", 42, "Root/A#0", "Wolf", 1.5f, 2.5f, 3.5f),
            Entry(101, EntityKindWire.Inventory, "Inventory", 0, "", "Chest", 4f, 0f, 4f)
        };
        var decoded = ReplicationProtocolCodec.DecodeEntityBindingEntries(ReplicationProtocolCodec.Encode(entries));
        Assert.Equal(2, decoded.Length);
        Assert.Equal(42L, decoded[0].SaveableUid);
        Assert.Equal("Character", decoded[0].ComponentType);
        Assert.Equal("Root/A#0", decoded[0].RelativePath);
        Assert.Equal("Wolf", decoded[0].ObjectName);
        Assert.Equal(1.5f, decoded[0].X);
        Assert.Equal(0L, decoded[1].SaveableUid);
    }

    /// <summary>10. Ready gate：Character 关键类别 unmatched → 禁止；非关键类别可按需指定为关键。</summary>
    [Fact]
    public void Critical_Binding_Missing_Blocks_Ready()
    {
        var stats = new ApplyStats();
        stats.RecordMissing("character missing", EntityKindWire.Character);
        Assert.NotNull(EntityBindingGate.Evaluate(stats, EntityKindWire.Character));

        var doorStats = new ApplyStats();
        doorStats.RecordMissing("door missing", EntityKindWire.Door);
        Assert.Null(EntityBindingGate.Evaluate(doorStats, EntityKindWire.Character)); // 非关键类别不阻断
        Assert.NotNull(EntityBindingGate.Evaluate(doorStats, EntityKindWire.Door)); // 指定为关键则阻断
    }

    /// <summary>11. 非关键类别缺失按类别统计（容差由调用方按需放行）。</summary>
    [Fact]
    public void Tolerant_Gate_Counts_NonCharacter_Kinds()
    {
        var stats = new ApplyStats();
        stats.RecordMissing("d1", EntityKindWire.Door);
        stats.RecordMissing("w1", EntityKindWire.Window);
        stats.RecordMissing("i1", EntityKindWire.Item);
        stats.RecordMissing("i2", EntityKindWire.Item);
        Assert.Equal(4, EntityBindingGate.CountMissing(stats, EntityKindWire.Door, EntityKindWire.Window, EntityKindWire.Item, EntityKindWire.Inventory));
        Assert.Equal(2, EntityBindingGate.CountMissing(stats, EntityKindWire.Item));
        Assert.Equal(0, EntityBindingGate.CountMissing(stats, EntityKindWire.Character));
    }

    /// <summary>12. ApplyStats 报告真实 received/applied/missing/stale：missing 计入 Received 但不计入 Applied。</summary>
    [Fact]
    public void Apply_Stats_Report_Actual_Applied()
    {
        var stats = new ApplyStats { Received = 10 };
        stats.RecordMissing("m1", EntityKindWire.Item);
        stats.RecordMissing("m2", EntityKindWire.Item);
        stats.Applied = 8;
        Assert.Equal(10, stats.Received);
        Assert.Equal(8, stats.Applied);
        Assert.Equal(2, stats.Missing);
        Assert.Equal(2, stats.MissingByKind[EntityKindWire.Item]);
        Assert.Contains("received=10", stats.Summary());
    }

    /// <summary>13. 绑定使用主机权威 id：Pair 映射回 entry 后 EntityValue 即主机分配的 id。</summary>
    [Fact]
    public void Binding_Uses_Host_Authoritative_Ids()
    {
        var entries = new[] { Entry(0x7C8737FBC0A5AB81UL, EntityKindWire.Item, "Item", 77, "A", "ItemX", 9f, 0f, 9f) };
        var local = new[] { Candidate("Item", 77, "A", "ItemX", 9f, 0f, 9f) };
        var outcome = new EntityBindingMatcher().Match(entries, local);
        Assert.Single(outcome.Pairs);
        Assert.Equal(0x7C8737FBC0A5AB81UL, entries[outcome.Pairs[0].EntryIndex].EntityValue);
    }

    /// <summary>两个 B 级候选（同 uid 无 path 匹配且都在容差内）→ ambiguous 显式报告，不随便选一个。</summary>
    [Fact]
    public void Ambiguous_Candidates_Are_Reported_Not_Bound()
    {
        var entries = new[] { Entry(100, EntityKindWire.Item, "Item", 5, "A", "Wood", 10f, 0f, 10f) };
        var local = new[]
        {
            Candidate("Item", 5, "B", "Wood", 10.5f, 0f, 10.5f),
            Candidate("Item", 5, "C", "Wood", 9.5f, 0f, 9.5f)
        };
        var outcome = new EntityBindingMatcher().Match(entries, local);
        Assert.Equal(0, outcome.Bound);
        Assert.Single(outcome.Ambiguous);
        Assert.Empty(outcome.Missing);
    }

    /// <summary>14. 稳定指纹：count 相同但对象集合不同必须判不同；顺序无关。</summary>
    [Fact]
    public void Fingerprint_Detects_Set_Change_With_Same_Count()
    {
        var a = new[] { new EntityScanFingerprint.ScanIdentity("Item", 1), new EntityScanFingerprint.ScanIdentity("Door", 2) };
        var b = new[] { new EntityScanFingerprint.ScanIdentity("Item", 1), new EntityScanFingerprint.ScanIdentity("Door", 3) }; // 数量同，对象集合不同
        var c = new[] { new EntityScanFingerprint.ScanIdentity("Door", 2), new EntityScanFingerprint.ScanIdentity("Item", 1) }; // 顺序不同
        Assert.NotEqual(EntityScanFingerprint.Compute(a), EntityScanFingerprint.Compute(b));
        Assert.Equal(EntityScanFingerprint.Compute(a), EntityScanFingerprint.Compute(c));
    }

    /// <summary>15. Client Ready 早到但 hostRegistryStable=false 时，快照必须挂起（不能发 BindingManifest/Snapshot）。</summary>
    [Fact]
    public void Snapshot_Deferred_Until_Registry_Stable()
    {
        Assert.NotNull(EntityBindingGate.SnapshotReady(false)); // 未稳定 → 挂起
        Assert.Null(EntityBindingGate.SnapshotReady(true));     // 已稳定 → 允许发送
    }

    /// <summary>P0 回归：EntityBindingChunk codec roundtrip 后 Data/Hash 必须各自完全一致（不得反转）。</summary>
    [Fact]
    public void BindingChunk_Roundtrip_Preserves_Data_And_Hash()
    {
        var id = Guid.NewGuid();
        var data = new byte[64 * 1024];
        new Random(42).NextBytes(data);
        var hash = DarkwoodMultiplayerFramework.Network.ChunkTransferAssembler.Hash(data);

        var original = new EntityBindingChunk(id, 1, 4, data, hash);
        var decoded = ReplicationProtocolCodec.DecodeEntityBindingChunk(ReplicationProtocolCodec.Encode(original));

        Assert.Equal(id, decoded.TransferId);
        Assert.Equal(1, decoded.Index);
        Assert.Equal(4, decoded.Total);
        Assert.Equal(data, decoded.Data);
        Assert.Equal(hash, decoded.Hash);
    }

    /// <summary>P0 回归：多块 binding chunk 经 encode/decode 后交给 assembler，SHA-256 全量校验必须通过。</summary>
    [Fact]
    public void BindingChunk_Roundtrip_Passes_Hash_Verification()
    {
        var data = new byte[100000];
        new Random(123).NextBytes(data);
        var chunks = DarkwoodMultiplayerFramework.Network.ChunkTransferAssembler.Split(data, 64 * 1024);
        var transferId = Guid.NewGuid();
        var assembler = new DarkwoodMultiplayerFramework.Network.ChunkTransferAssembler(transferId, data.Length, chunks.Length, DarkwoodMultiplayerFramework.Network.ChunkTransferAssembler.Hash(data));

        for (var i = 0; i < chunks.Length; i++)
        {
            var wire = new EntityBindingChunk(transferId, i, chunks.Length, chunks[i], DarkwoodMultiplayerFramework.Network.ChunkTransferAssembler.Hash(chunks[i]));
            var decoded = ReplicationProtocolCodec.DecodeEntityBindingChunk(ReplicationProtocolCodec.Encode(wire));
            assembler.Add(decoded.TransferId, decoded.Index, decoded.Total, decoded.Data, decoded.Hash);
        }

        Assert.True(assembler.IsComplete);
        Assert.Equal(data, assembler.Build());
    }
}