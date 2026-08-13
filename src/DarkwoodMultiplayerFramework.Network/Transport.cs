using System;
using System.Collections.Generic;
using System.Reflection;
using DarkwoodMultiplayerFramework.Core;

namespace DarkwoodMultiplayerFramework.Network;

public enum DeliveryMode { Reliable, UnreliableSequenced }

public interface ITransport : IDisposable
{
    bool IsConnected { get; }
    event Action? Connected;
    event Action<ArraySegment<byte>>? DataReceived;
    event Action? Disconnected;
    void Connect(string address, ushort port);
    void Send(ArraySegment<byte> payload, DeliveryMode mode = DeliveryMode.Reliable);
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
    public event Action? Connected;
    public event Action<ArraySegment<byte>>? DataReceived;
    public event Action? Disconnected;
    public void Connect(string address, ushort port) => Invoke("Connect", address, (int)port);
    public void Send(ArraySegment<byte> payload, DeliveryMode mode = DeliveryMode.Reliable)
    {
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
