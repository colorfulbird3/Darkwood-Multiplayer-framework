using System;
using System.Collections.Generic;
using System.Reflection;
using DarkwoodMultiplayerFramework.Core;

namespace DarkwoodMultiplayerFramework.Network;

/// <summary>
/// 0.8.9 第五/六刀：逻辑消息通道。当前 Telepathy 全走可靠 TCP；
/// 分级为未来 Transport（UDP/KCP）预留——Realtime 换不可靠通道时上层不改。
/// </summary>
public enum TransportChannel { Control, ReliableGameplay, Realtime, Bulk }

[System.Flags]
public enum TransportCapabilities { Reliable = 1, Unreliable = 2 }

public interface ITransport : IDisposable
{
    bool IsConnected { get; }
    /// <summary>本传输实际提供的能力（Telepathy：仅 Reliable）。</summary>
    TransportCapabilities Capabilities { get; }
    event Action? Connected;
    event Action<ArraySegment<byte>>? DataReceived;
    event Action? Disconnected;
    void Connect(string address, ushort port);
    /// <summary>
    /// 发送。若请求的通道需要的能力超出本传输（例如 Realtime 需要 Unreliable 而
    /// 本传输只有 Reliable），显式降级为可靠发送并只警告一次——不再假装支持。
    /// </summary>
    void Send(ArraySegment<byte> payload, TransportChannel channel = TransportChannel.ReliableGameplay);
    void Tick(int processLimit = 100);
    void Stop();
}

internal static class TelepathyConfiguration
{
    // The save stream uses 128 KiB application chunks. Leave enough room for
    // the DMF envelope and Telepathy's own length prefix.
    private const int MaximumMessageBytes = 256 * 1024;
    private const int SocketTimeoutMilliseconds = 60 * 1000;

    public static void Apply(object instance, Type type)
    {
        SetField(instance, type, "NoDelay", true);
        SetField(instance, type, "MaxMessageSize", MaximumMessageBytes);
        SetField(instance, type, "SendTimeout", SocketTimeoutMilliseconds);
    }

    private static void SetField(object instance, Type type, string name, object value)
    {
        var field = type.GetField(name, BindingFlags.Public | BindingFlags.Instance);
        if (field != null) field.SetValue(instance, value);
    }
}

// Loads the shipped Telepathy client without coupling protocol code to its concrete version.
public sealed class TelepathyClientTransport : ITransport
{
    private readonly object client;
    private readonly Type type;
    private readonly MethodInfo getNextMessage;
    private readonly Type messageType;
    public TelepathyClientTransport(string assemblyPath)
    {
        var assembly = Assembly.LoadFrom(assemblyPath);
        type = assembly.GetType("Telepathy.Client", true)!;
        messageType = assembly.GetType("Telepathy.Message", true)!;
        client = Activator.CreateInstance(type)!;
        TelepathyConfiguration.Apply(client, type);
        getNextMessage = type.GetMethod("GetNextMessage") ?? throw new MissingMethodException(type.FullName, "GetNextMessage");
    }
    public bool IsConnected => (bool)(type.GetProperty("Connected")?.GetValue(client) ?? false);
    public TransportCapabilities Capabilities => TransportCapabilities.Reliable;
    private bool warnedRealtimeFallback;
    public event Action? Connected;
    public event Action<ArraySegment<byte>>? DataReceived;
    public event Action? Disconnected;
    public void Connect(string address, ushort port) => Invoke("Connect", address, (int)port);
    public void Send(ArraySegment<byte> payload, TransportChannel channel = TransportChannel.ReliableGameplay)
    {
        if (channel == TransportChannel.Realtime && !warnedRealtimeFallback)
        {
            warnedRealtimeFallback = true;
            Console.WriteLine("[DMF] Transport only supports Reliable; Realtime messages fall back to TCP (expected until UDP transport lands).");
        }
        var bytes = new byte[payload.Count];
        Array.Copy(payload.Array!, payload.Offset, bytes, 0, payload.Count);
        Invoke("Send", bytes);
    }
    public void Tick(int processLimit = 100)
    {
        for (var i = 0; i < processLimit; i++)
        {
            object? message = Activator.CreateInstance(messageType);
            var args = new[] { message };
            if (!(bool)getNextMessage.Invoke(client, args)!) break;
            message = args[0];
            var eventName = messageType.GetField("eventType")!.GetValue(message)!.ToString();
            if (eventName == "Connected") Connected?.Invoke();
            else if (eventName == "Disconnected") Disconnected?.Invoke();
            else if (eventName == "Data")
            {
                var data = (byte[])messageType.GetField("data")!.GetValue(message)!;
                DataReceived?.Invoke(new ArraySegment<byte>(data));
            }
        }
    }
    public void Stop() => Invoke("Disconnect");
    public void Dispose() => Stop();
    private MethodInfo Find(string name, int count) => Array.Find(type.GetMethods(), x => x.Name == name && x.GetParameters().Length == count) ?? throw new MissingMethodException(type.FullName, name);
    private object? Invoke(string name, params object[] args) => Find(name, args.Length).Invoke(client, args);
}

public sealed class ConnectionLifecycle
{
    private static readonly IReadOnlyDictionary<ConnectionState, ConnectionState[]> Next = new Dictionary<ConnectionState, ConnectionState[]>
    {
        [ConnectionState.Disconnected] = new[] { ConnectionState.Connecting },
        [ConnectionState.Connecting] = new[] { ConnectionState.VersionChecking, ConnectionState.Failed, ConnectionState.Stopping },
        [ConnectionState.VersionChecking] = new[] { ConnectionState.SaveTransfer, ConnectionState.Failed, ConnectionState.Stopping },
        [ConnectionState.SaveTransfer] = new[] { ConnectionState.LoadingSave, ConnectionState.Failed, ConnectionState.Stopping },
        [ConnectionState.LoadingSave] = new[] { ConnectionState.BuildingRegistry, ConnectionState.Failed, ConnectionState.Stopping },
        [ConnectionState.BuildingRegistry] = new[] { ConnectionState.ApplyingSnapshot, ConnectionState.Failed, ConnectionState.Stopping },
        [ConnectionState.ApplyingSnapshot] = new[] { ConnectionState.Ready, ConnectionState.Failed, ConnectionState.Stopping },
        [ConnectionState.Ready] = new[] { ConnectionState.ApplyingSnapshot, ConnectionState.Stopping, ConnectionState.Failed },
        [ConnectionState.Stopping] = new[] { ConnectionState.Disconnected },
        [ConnectionState.Failed] = new[] { ConnectionState.Disconnected, ConnectionState.Connecting }
    };
    public ConnectionState State { get; private set; } = ConnectionState.Disconnected;
    public bool CanReplicate => State == ConnectionState.Ready;
    public void MoveTo(ConnectionState next)
    {
        if (!Array.Exists(Next[State], x => x == next)) throw new InvalidOperationException($"Invalid connection transition {State} -> {next}.");
        State = next;
    }
}
