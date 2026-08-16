using DarkwoodMultiplayerFramework.Core;
using DarkwoodMultiplayerFramework.Entities;
using DarkwoodMultiplayerFramework.Protocol;
using Xunit;

namespace DarkwoodMultiplayerFramework.UnitTests;

/// <summary>0.8.9 第十刀：Container Revision 乐观锁测试（从 SelfTests 迁移）。</summary>
public class ContainerRevisionTests
{
    [Fact]
    public void Match_Advances()
    {
        Assert.True(ContainerRevisionGate.TryAdvance(expected: 6UL, current: 5UL, out var next));
        Assert.Equal(6UL, next);
    }

    [Fact]
    public void Stale_Rejected()
    {
        Assert.False(ContainerRevisionGate.TryAdvance(expected: 6UL, current: 6UL, out _));
    }

    [Fact]
    public void Concurrent_AB_FirstWins()
    {
        // A、B 都基于版本 5 操作；A 先到 → 6；B 晚到 → 拒绝。
        Assert.True(ContainerRevisionGate.TryAdvance(6UL, 5UL, out var afterA));
        Assert.Equal(6UL, afterA);
        Assert.False(ContainerRevisionGate.TryAdvance(6UL, afterA, out _));
    }
}

/// <summary>0.8.9 第十刀：Runtime Entity 注册表纪律测试（迁移）。</summary>
public class RuntimeEntityRegistryTests
{
    [Fact]
    public void Ids_Are_Monotonic_And_Never_Reused()
    {
        var registry = new RuntimeEntityRegistry();
        var a = registry.Allocate();
        var b = registry.Allocate();
        Assert.True(a < b);
        Assert.True(registry.Register(new RuntimeEntityRecord(a, RuntimeEntityKind.DroppedItem, "x", "chapter1", 1)));
        Assert.True(registry.Remove(a));
        var c = registry.Allocate();
        Assert.True(c > b); // 移除后不复用旧 ID
    }

    [Fact]
    public void Lifecycle_Transitions()
    {
        var registry = new RuntimeEntityRegistry();
        var id = registry.Allocate();
        Assert.True(registry.Register(new RuntimeEntityRecord(id, RuntimeEntityKind.DroppedItem, "x", "chapter1", 1)));
        Assert.True(registry.UpdateState(id, RuntimeEntityLifecycleState.Spawned));
        Assert.True(registry.TryGet(id, out var spawned));
        Assert.Equal(RuntimeEntityLifecycleState.Spawned, spawned.State);
        Assert.True(registry.TryAttachInstance(id, "mirror"));
        Assert.True(registry.TryGet(id, out var attached));
        Assert.Equal("mirror", attached.LocalInstance);
        Assert.True(registry.Remove(id));
        Assert.False(registry.TryGet(id, out _));
    }
}

/// <summary>0.8.9 第十刀：协议 codec roundtrip（迁移子集）。</summary>
public class CodecRoundtripTests
{
    [Fact]
    public void RuntimeEntitySpawn_Roundtrip()
    {
        var message = new RuntimeEntitySpawnMessage(7, RuntimeEntityKind.Enemy, "Dog", "chapter1", 1f, 2f, 3f, 0f, 0f, 0f, 1f, new byte[] { 1, 2, 3 }, 42);
        var decoded = ReplicationProtocolCodec.DecodeRuntimeEntitySpawn(ReplicationProtocolCodec.Encode(message));
        Assert.Equal(message.RuntimeEntityId, decoded.RuntimeEntityId);
        Assert.Equal(message.Kind, decoded.Kind);
        Assert.Equal(message.PrototypeId, decoded.PrototypeId);
        Assert.Equal(message.Scene, decoded.Scene);
        Assert.Equal(message.ServerTick, decoded.ServerTick);
        Assert.Equal(message.InitialState, decoded.InitialState);
    }

    [Fact]
    public void SceneChange_Roundtrip()
    {
        var message = new SceneChangeMessage("chapter2");
        var decoded = ReplicationProtocolCodec.DecodeSceneChange(ReplicationProtocolCodec.Encode(message));
        Assert.Equal("chapter2", decoded.Scene);
    }

    [Fact]
    public void SessionContext_Defaults_And_Reset()
    {
        var session = new SessionContext();
        Assert.False(session.IsActive);
        session.Role = MultiplayerRole.Host;
        session.State = ConnectionState.Ready;
        Assert.True(session.IsHost);
        session.Reset();
        Assert.False(session.IsActive);
        Assert.Equal(ConnectionState.Disconnected, session.State);
    }
}
