using System;
using DarkwoodMultiplayerFramework.Core;
using DarkwoodMultiplayerFramework.Network;
using DarkwoodMultiplayerFramework.Protocol;
using Xunit;

namespace DarkwoodMultiplayerFramework.UnitTests;

/// <summary>0.8.9 第十刀（渐进）：ChunkTransfer 分片/装配/校验（从 SelfTests 迁移）。</summary>
public class ChunkTransferTests
{
    [Fact]
    public void Split_And_Assemble_Roundtrip()
    {
        var data = new byte[300 * 1024];
        new Random(42).NextBytes(data);
        var chunks = ChunkTransferAssembler.Split(data, 128 * 1024);
        Assert.True(chunks.Length >= 2);
        var transferId = Guid.NewGuid();
        var assembler = new ChunkTransferAssembler(transferId, data.Length, chunks.Length, ChunkTransferAssembler.Hash(data));
        for (var i = 0; i < chunks.Length; i++)
            assembler.Add(transferId, i, chunks.Length, chunks[i], ChunkTransferAssembler.Hash(chunks[i]));
        Assert.True(assembler.IsComplete);
        Assert.Equal(data, assembler.Build());
    }

    [Fact]
    public void Wrong_Hash_Throws()
    {
        var data = new byte[64 * 1024];
        var chunks = ChunkTransferAssembler.Split(data, 32 * 1024);
        var transferId = Guid.NewGuid();
        var assembler = new ChunkTransferAssembler(transferId, data.Length, chunks.Length, ChunkTransferAssembler.Hash(data));
        var tampered = new byte[chunks[1].Length];
        Assert.ThrowsAny<Exception>(() => assembler.Add(transferId, 1, chunks.Length, tampered, ChunkTransferAssembler.Hash(new byte[] { 1, 2, 3 })));
    }
}

/// <summary>0.8.9：ConnectionLifecycle 状态机约束（迁移）。</summary>
public class ConnectionLifecycleTests
{
    [Fact]
    public void Valid_Transitions()
    {
        var lifecycle = new ConnectionLifecycle();
        lifecycle.MoveTo(ConnectionState.Connecting);
        lifecycle.MoveTo(ConnectionState.VersionChecking);
        lifecycle.MoveTo(ConnectionState.SaveTransfer);
        lifecycle.MoveTo(ConnectionState.LoadingSave);
        lifecycle.MoveTo(ConnectionState.BuildingRegistry);
        lifecycle.MoveTo(ConnectionState.ApplyingSnapshot);
        lifecycle.MoveTo(ConnectionState.Ready);
        Assert.True(lifecycle.CanReplicate);
    }

    [Fact]
    public void Invalid_Transition_Throws()
    {
        var lifecycle = new ConnectionLifecycle();
        Assert.Throws<InvalidOperationException>(() => lifecycle.MoveTo(ConnectionState.Ready));
    }
}

/// <summary>0.8.9：更多 codec roundtrip（迁移）。</summary>
public class MoreCodecTests
{
    [Fact]
    public void PlayerPose_Roundtrip()
    {
        var message = new PlayerPoseMessage(3, 42, "chapter1", 1f, 2f, 3f, 0f, 0f, 0f, 1f, 100f, (byte)0, "clip", 5, "legs", 7);
        var decoded = ReplicationProtocolCodec.DecodePlayerPose(ReplicationProtocolCodec.Encode(message));
        Assert.Equal(message.PlayerId, decoded.PlayerId);
        Assert.Equal(message.Sequence, decoded.Sequence);
        Assert.Equal(message.Scene, decoded.Scene);
        Assert.Equal(message.MaxHealth, decoded.MaxHealth);
        Assert.Equal(message.Flags, decoded.Flags);
        Assert.Equal(message.TorsoClip, decoded.TorsoClip);
    }

    [Fact]
    public void InventoryState_Roundtrip()
    {
        var message = new InventoryStateMessage(99, true, 7, "chest", 1f, 2f, 3f, 0, new[] { new InventorySlotWire("wood", 5, 1f, 0, false) });
        var decoded = ReplicationProtocolCodec.DecodeInventoryState(ReplicationProtocolCodec.Encode(message));
        Assert.Equal(message.Value, decoded.Value);
        Assert.Equal(message.Persistent, decoded.Persistent);
        Assert.Equal(message.Revision, decoded.Revision);
        Assert.Equal(message.Name, decoded.Name);
        Assert.Single(decoded.Slots);
        Assert.Equal("wood", decoded.Slots[0].Type);
        Assert.Equal(5, decoded.Slots[0].Amount);
    }

    [Fact]
    public void TransportChannel_Mapping_Covers_Realtime_And_Bulk()
    {
        // ChannelFor 的映射语义由 Adapter 内部持有；这里只验证通道枚举与能力组合可用。
        var capabilities = TransportCapabilities.Reliable | TransportCapabilities.Unreliable;
        Assert.True(capabilities.HasFlag(TransportCapabilities.Reliable));
        Assert.True(capabilities.HasFlag(TransportCapabilities.Unreliable));
        Assert.Equal(4, Enum.GetNames(typeof(TransportChannel)).Length); // Control/ReliableGameplay/Realtime/Bulk
    }
}

/// <summary>0.8.9-beta.6：DropItemPayload 来源语义 roundtrip。</summary>
public class DropItemPayloadTests
{
    [Fact]
    public void PlayerSlot_Roundtrip()
    {
        var payload = new DropItemPayload(true, 3, 2, 1f, 2f, 3f, 0f, 0f, 0f, 1f);
        var decoded = ReplicationProtocolCodec.DecodeDropItem(ReplicationProtocolCodec.Encode(payload));
        Assert.Equal(DropOriginWire.PlayerSlot, decoded.Origin);
        Assert.True(decoded.FromHotbar);
        Assert.Equal(3, decoded.SlotIndex);
        Assert.Equal(2, decoded.Amount);
        Assert.Equal(0UL, decoded.ContainerValue);
        Assert.False(decoded.ContainerPersistent);
    }

    [Fact]
    public void SharedContainer_Roundtrip()
    {
        var payload = new DropItemPayload(false, 7, 1, 0f, 0f, 0f, 0f, 0f, 0f, 1f, DropOriginWire.SharedContainer, 12345, true);
        var decoded = ReplicationProtocolCodec.DecodeDropItem(ReplicationProtocolCodec.Encode(payload));
        Assert.Equal(DropOriginWire.SharedContainer, decoded.Origin);
        Assert.False(decoded.FromHotbar);
        Assert.Equal(7, decoded.SlotIndex);
        Assert.Equal(1, decoded.Amount);
        Assert.Equal(12345UL, decoded.ContainerValue);
        Assert.True(decoded.ContainerPersistent);
    }
}
