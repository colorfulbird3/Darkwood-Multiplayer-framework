using System;
using System.Collections.Generic;
using System.Threading;

namespace DarkwoodMultiplayerFramework.Network;

/// <summary>故障注入选项。全部可选；0/空 = 不注入。</summary>
public sealed class FaultOptions
{
    /// <summary>每 N 条发送丢弃 1 条（N=0 关闭）。</summary>
    public int DropEveryN { get; set; }
    /// <summary>每条发送延迟毫秒（0 关闭）。</summary>
    public int DelayMilliseconds { get; set; }
    /// <summary>每 N 条发送重复 1 条（N=0 关闭）。</summary>
    public int DuplicateEveryN { get; set; }
    /// <summary>累计发送 N 条后触发断开（0 关闭）。</summary>
    public int DisconnectAfterMessages { get; set; }
    /// <summary>把下一条发送的 payload 首字节翻转（模拟损坏包）。</summary>
    public bool CorruptNextPacket { get; set; }
}

/// <summary>
/// 0.8.9 可靠性：故障注入传输——包装任意 ITransport，在发送路径注入
/// 丢包/延迟/重复/断开/损坏，用于在真机之前逼出网络故障处理缺陷。
/// </summary>
public sealed class FaultInjectingTransport : ITransport
{
    private readonly ITransport inner;
    private readonly FaultOptions options;
    private long sentCount;
    private bool disconnectFired;

    public FaultInjectingTransport(ITransport inner, FaultOptions options)
    {
        this.inner = inner ?? throw new ArgumentNullException(nameof(inner));
        this.options = options ?? new FaultOptions();
        inner.Connected += () => Connected?.Invoke();
        inner.DataReceived += data => DataReceived?.Invoke(data);
        inner.Disconnected += () => Disconnected?.Invoke();
    }

    public bool IsConnected => inner.IsConnected;
    public TransportCapabilities Capabilities => inner.Capabilities;
    public event Action? Connected;
    public event Action<ArraySegment<byte>>? DataReceived;
    public event Action? Disconnected;
    public long SentCount => Interlocked.Read(ref sentCount);

    public void Connect(string address, ushort port) => inner.Connect(address, port);

    public void Send(ArraySegment<byte> payload, TransportChannel channel = TransportChannel.ReliableGameplay)
    {
        var n = Interlocked.Increment(ref sentCount);

        // 延迟注入
        if (options.DelayMilliseconds > 0) Thread.Sleep(options.DelayMilliseconds);

        // 断开注入（先于发送）
        if (options.DisconnectAfterMessages > 0 && n >= options.DisconnectAfterMessages)
        {
            if (!disconnectFired)
            {
                disconnectFired = true;
                inner.Stop();
                Disconnected?.Invoke();
            }
            return;
        }

        // 丢包注入
        if (options.DropEveryN > 0 && n % options.DropEveryN == 0) return;

        // 损坏注入
        var bytes = new byte[payload.Count];
        Array.Copy(payload.Array!, payload.Offset, bytes, 0, payload.Count);
        if (options.CorruptNextPacket)
        {
            options.CorruptNextPacket = false;
            if (bytes.Length > 0) bytes[0] ^= 0xFF;
        }
        inner.Send(new ArraySegment<byte>(bytes), channel);

        // 重复注入
        if (options.DuplicateEveryN > 0 && n % options.DuplicateEveryN == 0)
            inner.Send(new ArraySegment<byte>(bytes), channel);
    }

    public void Tick(int processLimit = 100) => inner.Tick(processLimit);
    public void Stop() => inner.Stop();
    public void Dispose() => inner.Dispose();
}
