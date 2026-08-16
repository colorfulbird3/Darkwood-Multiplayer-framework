using System;
using System.Collections.Generic;
using DarkwoodMultiplayerFramework.Network;
using Xunit;

namespace DarkwoodMultiplayerFramework.UnitTests;

/// <summary>测试用假传输：记录发送的 payload，可手动触发事件。</summary>
public sealed class FakeTransport : ITransport
{
    public readonly List<byte[]> Sent = new List<byte[]>();
    public bool IsConnectedValue = true;
    public bool IsConnected => IsConnectedValue;
    public TransportCapabilities Capabilities => TransportCapabilities.Reliable;
    public event Action? Connected;
    public event Action<ArraySegment<byte>>? DataReceived;
    public event Action? Disconnected;

    public void Connect(string address, ushort port) => IsConnectedValue = true;
    public void Send(ArraySegment<byte> payload, TransportChannel channel = TransportChannel.ReliableGameplay)
    {
        var copy = new byte[payload.Count];
        Array.Copy(payload.Array!, payload.Offset, copy, 0, payload.Count);
        Sent.Add(copy);
    }
    public void Tick(int processLimit = 100) { }
    public void Stop() => IsConnectedValue = false;
    public void Dispose() => IsConnectedValue = false;
    public void RaiseData(byte[] data) => DataReceived?.Invoke(new ArraySegment<byte>(data));
    public void RaiseDisconnected() => Disconnected?.Invoke();
}

/// <summary>0.8.9 可靠性：FaultInjectingTransport 注入语义测试。</summary>
public class FaultInjectingTransportTests
{
    private static FaultInjectingTransport Wrap(FaultOptions options, out FakeTransport fake)
    {
        fake = new FakeTransport();
        return new FaultInjectingTransport(fake, options);
    }

    private static ArraySegment<byte> Payload(byte value = 0xAB) => new ArraySegment<byte>(new byte[] { value, 0, 1 });

    [Fact]
    public void NoFaults_PassesEverything()
    {
        var transport = Wrap(new FaultOptions(), out var fake);
        for (var i = 0; i < 5; i++) transport.Send(Payload((byte)i));
        Assert.Equal(5, fake.Sent.Count);
    }

    [Fact]
    public void DropEveryN_DropsMatchingMessage()
    {
        var transport = Wrap(new FaultOptions { DropEveryN = 2 }, out var fake);
        for (var i = 1; i <= 4; i++) transport.Send(Payload((byte)i));
        // 第 2、4 条被丢
        Assert.Equal(2, fake.Sent.Count);
        Assert.Equal(1, fake.Sent[0][0]);
        Assert.Equal(3, fake.Sent[1][0]);
    }

    [Fact]
    public void DuplicateEveryN_SendsTwice()
    {
        var transport = Wrap(new FaultOptions { DuplicateEveryN = 3 }, out var fake);
        for (var i = 1; i <= 3; i++) transport.Send(Payload((byte)i));
        Assert.Equal(4, fake.Sent.Count); // 第 3 条发两次
        Assert.Equal(3, fake.Sent[2][0]);
        Assert.Equal(3, fake.Sent[3][0]);
    }

    [Fact]
    public void DisconnectAfterMessages_FiresAndStops()
    {
        var disconnected = 0;
        var transport = Wrap(new FaultOptions { DisconnectAfterMessages = 2 }, out var fake);
        transport.Disconnected += () => disconnected++;
        transport.Send(Payload(1));
        transport.Send(Payload(2));
        Assert.Equal(1, disconnected);
        Assert.False(fake.IsConnected);
        // 之后不再发送
        var sentAfter = fake.Sent.Count;
        transport.Send(Payload(3));
        Assert.Equal(sentAfter, fake.Sent.Count);
    }

    [Fact]
    public void CorruptNextPacket_FlipsFirstByte()
    {
        var transport = Wrap(new FaultOptions { CorruptNextPacket = true }, out var fake);
        transport.Send(Payload(0xAB));
        transport.Send(Payload(0xCD));
        Assert.Equal(0xAB ^ 0xFF, fake.Sent[0][0]);
        Assert.Equal(0xCD, fake.Sent[1][0]); // 只损坏一次
    }

    [Fact]
    public void DelayMilliseconds_DoesNotChangeContent()
    {
        var transport = Wrap(new FaultOptions { DelayMilliseconds = 5 }, out var fake);
        transport.Send(Payload(7));
        Assert.Single(fake.Sent);
        Assert.Equal(7, fake.Sent[0][0]);
    }

    [Fact]
    public void Events_Forwarded()
    {
        var transport = Wrap(new FaultOptions(), out var fake);
        var received = 0;
        transport.DataReceived += _ => received++;
        fake.RaiseData(new byte[] { 1 });
        Assert.Equal(1, received);
    }

    [Fact]
    public void Disconnected_FiresOnlyOnce_EvenIfInnerFiresTwice()
    {
        var transport = Wrap(new FaultOptions(), out var fake);
        var disconnected = 0;
        transport.Disconnected += () => disconnected++;
        fake.RaiseDisconnected(); // inner Stop 路径
        fake.RaiseDisconnected(); // Telepathy Tick 里的后续事件
        Assert.Equal(1, disconnected);
    }

    [Fact]
    public void Reconnect_ReArmsDisconnectedGuard()
    {
        var transport = Wrap(new FaultOptions(), out var fake);
        var disconnected = 0;
        transport.Disconnected += () => disconnected++;
        fake.RaiseDisconnected();
        transport.Connect("127.0.0.1", 17777);
        fake.RaiseDisconnected();
        Assert.Equal(2, disconnected);
    }
}
