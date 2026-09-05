using DarkwoodMultiplayerFramework.Protocol;
using Xunit;

namespace DarkwoodMultiplayerFramework.UnitTests;

/// <summary>
/// 0.8.9.4-pre.1 wire：实体状态多 typed payload（EntityStatePayload[] bundle）回归测试。
/// 对应 SelfTests 的 entity state multi payload / bounds 用例，xunit 快测门禁重复覆盖。
/// </summary>
public class EntityStatePayloadTests
{
    private static EntityStateWire RoundTrip(EntityStateWire entity)
    {
        var encoded = ReplicationProtocolCodec.Encode(new EntityDeltaMessage("scene", 42, new[] { entity }, System.Array.Empty<EntityStateWire>()));
        var decoded = ReplicationProtocolCodec.DecodeEntityDelta(encoded);
        Assert.Single(decoded.Entities);
        return decoded.Entities[0];
    }

    [Fact]
    public void Legacy_NoPayload_Keeps_ZeroSchema_Semantics()
    {
        var legacy = new EntityStateWire(77, true, 2, 1, 2, 3, 0, 0, 0, 1, 50, 4, 5, 3, "open", 7, 9);
        var back = RoundTrip(legacy);
        Assert.Empty(back.Payloads);
        Assert.Equal((ushort)0, back.StateSchema);
        Assert.Empty(back.ExtraState);
        Assert.Equal(9UL, back.Revision);
        Assert.Equal("open", back.Animation);
    }

    [Fact]
    public void Legacy_SingleTypedPayload_IsFirstPayload()
    {
        var typed = new EntityStateWire(7, true, 4, 0, 0, 0, 0, 0, 0, 1, 10, 0, 0, 1, "", 0, 5, 7, new byte[] { 1, 2, 3 });
        var back = RoundTrip(typed);
        Assert.Single(back.Payloads);
        Assert.Equal((ushort)7, back.StateSchema);            // 便捷派生 = 首 payload
        Assert.Equal(new byte[] { 1, 2, 3 }, back.ExtraState);
    }

    [Fact]
    public void MultiPayload_RoundTrips_AllSchemas()
    {
        var p1 = new EntityStatePayload(7, new byte[] { 10, 20 });
        var p2 = new EntityStatePayload(8, new byte[] { 30, 40, 50 });
        var p3 = new EntityStatePayload(5, new byte[] { 1 });
        var entity = new EntityStateWire(9, true, 4, 0, 0, 0, 0, 0, 0, 1, 50, 0, 0, 1, "", 0, 11, new[] { p1, p2, p3 });
        var back = RoundTrip(entity);
        Assert.Equal(3, back.Payloads.Length);
        Assert.Equal((ushort)7, back.Payloads[0].Schema);
        Assert.Equal(new byte[] { 10, 20 }, back.Payloads[0].Data);
        Assert.Equal((ushort)8, back.Payloads[1].Schema);
        Assert.Equal(new byte[] { 30, 40, 50 }, back.Payloads[1].Data);
        Assert.Equal((ushort)5, back.Payloads[2].Schema);
        Assert.Equal(new byte[] { 1 }, back.Payloads[2].Data);
        Assert.Equal((ushort)7, back.StateSchema); // 派生 = 首 payload
    }

    [Fact]
    public void EmptySchema_WithEmptyData_NormalizesToNoPayload()
    {
        var wire = new EntityStateWire(1, true, 1, 0, 0, 0, 0, 0, 0, 1, 1, 0, 0, 0, "", 0, 1, 0, System.Array.Empty<byte>());
        Assert.Empty(wire.Payloads);
        var back = RoundTrip(wire);
        Assert.Empty(back.Payloads);
    }

    [Fact]
    public void TooManyPayloads_Throws()
    {
        var payloads = new EntityStatePayload[9];
        for (var i = 0; i < payloads.Length; i++) payloads[i] = new EntityStatePayload((ushort)(i + 1), new byte[] { 1 });
        var entity = new EntityStateWire(1, true, 1, 0, 0, 0, 0, 0, 0, 1, 1, 0, 0, 0, "", 0, 1, payloads);
        Assert.ThrowsAny<System.Exception>(() => ReplicationProtocolCodec.Encode(new EntityDeltaMessage("s", 1, new[] { entity }, System.Array.Empty<EntityStateWire>())));
    }

    [Fact]
    public void OversizedSinglePayload_Throws()
    {
        var entity = new EntityStateWire(2, true, 1, 0, 0, 0, 0, 0, 0, 1, 1, 0, 0, 0, "", 0, 1, new[] { new EntityStatePayload(7, new byte[4097]) });
        Assert.ThrowsAny<System.Exception>(() => ReplicationProtocolCodec.Encode(new EntityDeltaMessage("s", 1, new[] { entity }, System.Array.Empty<EntityStateWire>())));
    }
}
