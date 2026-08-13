using System;
using System.Reflection;

namespace DarkwoodMultiplayerFramework.Network;

public sealed class TelepathyServerTransport : IDisposable
{
    private readonly object server;
    private readonly Type serverType;
    private readonly Type messageType;
    private readonly MethodInfo getNextMessage;
    public TelepathyServerTransport(string assemblyPath)
    {
        var assembly = Assembly.LoadFrom(assemblyPath);
        serverType = assembly.GetType("Telepathy.Server", true)!;
        messageType = assembly.GetType("Telepathy.Message", true)!;
        server = Activator.CreateInstance(serverType)!;
        TelepathyConfiguration.Apply(server, serverType);
        getNextMessage = serverType.GetMethod("GetNextMessage")!;
    }
    public bool IsActive => (bool)(serverType.GetProperty("Active")?.GetValue(server) ?? false);
    public event Action<int>? Connected;
    public event Action<int, ArraySegment<byte>>? DataReceived;
    public event Action<int>? Disconnected;
    public void Start(ushort port)
    {
        var result = serverType.GetMethod("Start")!.Invoke(server, new object[] { (int)port });
        if (result is bool started && !started) throw new InvalidOperationException("Telepathy server is already active.");
    }
    public void Send(int connectionId, ArraySegment<byte> payload)
    {
        var bytes = new byte[payload.Count]; Array.Copy(payload.Array!, payload.Offset, bytes, 0, payload.Count);
        serverType.GetMethod("Send")!.Invoke(server, new object[] { connectionId, bytes });
    }
    public void Tick(int processLimit = 100)
    {
        for (var i = 0; i < processLimit; i++)
        {
            object? message = Activator.CreateInstance(messageType); var args = new[] { message };
            if (!(bool)getNextMessage.Invoke(server, args)!) break;
            message = args[0];
            var id = (int)messageType.GetField("connectionId")!.GetValue(message)!;
            var eventName = messageType.GetField("eventType")!.GetValue(message)!.ToString();
            if (eventName == "Connected") Connected?.Invoke(id);
            else if (eventName == "Disconnected") Disconnected?.Invoke(id);
            else if (eventName == "Data") DataReceived?.Invoke(id, new ArraySegment<byte>((byte[])messageType.GetField("data")!.GetValue(message)!));
        }
    }
    public void Stop() => serverType.GetMethod("Stop")!.Invoke(server, Array.Empty<object>());
    public void Disconnect(int connectionId) => serverType.GetMethod("Disconnect")!.Invoke(server, new object[] { connectionId });
    public void Dispose() => Stop();
}
