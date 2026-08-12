using System;
using System.Collections.Generic;
using System.IO;
using DarkwoodMultiplayerFramework.Core;
using DarkwoodMultiplayerFramework.Protocol;

namespace DarkwoodMultiplayerFramework.Network;

public sealed class ClientHandshakeSession : IDisposable
{
    private readonly ITransport transport;
    private uint sequence;
    public ClientHandshakeSession(ITransport transport, ProtocolIdentity identity)
    {
        this.transport = transport ?? throw new ArgumentNullException(nameof(transport));
        Session = new NetworkSession(identity);
        transport.Connected += OnConnected; transport.DataReceived += OnData; transport.Disconnected += OnDisconnected;
    }
    public NetworkSession Session { get; }
    public bool HandshakeComplete { get; private set; }
    public int PeerId { get; private set; } = -1;
    public Guid HostSessionId { get; private set; }
    public string LastError { get; private set; } = string.Empty;
    public event Action? HandshakeSucceeded;
    public event Action<string>? HandshakeFailed;
    public event Action<ProtocolEnvelope>? MessageReceived;
    public void Connect(string address, ushort port)
    {
        if (Session.Lifecycle.State != ConnectionState.Disconnected && Session.Lifecycle.State != ConnectionState.Failed) throw new InvalidOperationException("Client session is already active.");
        if (Session.Lifecycle.State == ConnectionState.Failed) Session.Lifecycle.MoveTo(ConnectionState.Disconnected);
        if (string.IsNullOrWhiteSpace(address)) throw new ArgumentException("Server address is required.", nameof(address));
        if (port == 0) throw new ArgumentOutOfRangeException(nameof(port));
        LastError = string.Empty; HandshakeComplete = false; PeerId = -1; HostSessionId = Guid.Empty; sequence = 0;
        Session.Lifecycle.MoveTo(ConnectionState.Connecting);
        try { transport.Connect(address, port); }
        catch (Exception error) { Fail("TRANSPORT_CONNECT_FAILED: " + error.Message); }
    }
    public void Tick(int processLimit = 100) => transport.Tick(processLimit);
    public void Stop()
    {
        if (Session.Lifecycle.State == ConnectionState.Disconnected) return;
        if (Session.Lifecycle.State == ConnectionState.Failed) { transport.Stop(); Session.Lifecycle.MoveTo(ConnectionState.Disconnected); }
        else { Session.Lifecycle.MoveTo(ConnectionState.Stopping); transport.Stop(); Session.Lifecycle.MoveTo(ConnectionState.Disconnected); }
        HandshakeComplete = false;
    }
    public void Dispose()
    {
        transport.Connected -= OnConnected; transport.DataReceived -= OnData; transport.Disconnected -= OnDisconnected;
        try { Stop(); } catch { transport.Stop(); }
        transport.Dispose();
    }
    private void OnConnected()
    {
        if (Session.Lifecycle.State != ConnectionState.Connecting) return;
        Session.Lifecycle.MoveTo(ConnectionState.VersionChecking);
        Send(ProtocolMessageType.ClientHello, Session.SessionId, HandshakeProtocolCodec.Encode(new ClientHello(Session.Identity)));
    }
    private void OnData(ArraySegment<byte> packet)
    {
        try
        {
            var envelope = ProtocolEnvelopeCodec.Decode(packet);
            if (Session.Lifecycle.State != ConnectionState.VersionChecking)
            {
                if (!HandshakeComplete) throw new InvalidDataException("Protocol packet arrived before handshake completion.");
                if (envelope.SessionId != HostSessionId) throw new InvalidDataException("Protocol packet session id mismatch.");
                MessageReceived?.Invoke(envelope);
                return;
            }
            if (envelope.MessageType == ProtocolMessageType.HandshakeReject)
            {
                var rejection = HandshakeProtocolCodec.DecodeReject(envelope.Payload);
                RequireMatchingEnvelope(envelope, rejection.HostIdentity);
                Fail(rejection.ErrorCode);
                return;
            }
            if (envelope.MessageType != ProtocolMessageType.ServerHello) throw new InvalidDataException("Expected ServerHello.");
            var hello = HandshakeProtocolCodec.DecodeServerHello(envelope.Payload);
            RequireMatchingEnvelope(envelope, hello.Identity);
            var validation = HandshakeValidator.Validate(hello.Identity, Session.Identity);
            if (!validation.Accepted) { Fail(validation.ErrorCode); return; }
            HostSessionId = envelope.SessionId; PeerId = hello.PeerId; HandshakeComplete = true;
            Session.Lifecycle.MoveTo(ConnectionState.SaveTransfer); HandshakeSucceeded?.Invoke();
        }
        catch (Exception error) { Fail("INVALID_HANDSHAKE_PACKET: " + error.Message); }
    }
    private void OnDisconnected()
    {
        HandshakeComplete = false;
        if (Session.Lifecycle.State == ConnectionState.Stopping) Session.Lifecycle.MoveTo(ConnectionState.Disconnected);
        else if (Session.Lifecycle.State != ConnectionState.Disconnected && Session.Lifecycle.State != ConnectionState.Failed) Fail("TRANSPORT_DISCONNECTED");
    }
    private void Send(ProtocolMessageType type, Guid sessionId, byte[] payload)
    {
        var envelope = new ProtocolEnvelope(Session.Identity.ProtocolVersion, type, ProtocolFlags.Reliable, ++sequence, sessionId, payload);
        transport.Send(new ArraySegment<byte>(ProtocolEnvelopeCodec.Encode(envelope)), DeliveryMode.Reliable);
    }
    public void Send(ProtocolMessageType type, byte[] payload)
    {
        if (!HandshakeComplete || HostSessionId == Guid.Empty) throw new InvalidOperationException("Handshake is not complete.");
        Send(type, HostSessionId, payload);
    }
    private static void RequireMatchingEnvelope(ProtocolEnvelope envelope, ProtocolIdentity identity)
    {
        if (envelope.ProtocolVersion != identity.ProtocolVersion) throw new InvalidDataException("Envelope and identity protocol versions differ.");
    }
    public void Fail(string error)
    {
        if (Session.Lifecycle.State == ConnectionState.Failed) return;
        LastError = error; HandshakeComplete = false;
        if (Session.Lifecycle.State != ConnectionState.Disconnected && Session.Lifecycle.State != ConnectionState.Stopping) Session.Lifecycle.MoveTo(ConnectionState.Failed);
        transport.Stop();
        HandshakeFailed?.Invoke(error);
    }
}

public sealed class HostHandshakeSession : IDisposable
{
    private sealed class Peer { public bool Ready; public ProtocolIdentity Identity; public uint LastSequence; }
    private readonly TelepathyServerTransport transport;
    private readonly Dictionary<int, Peer> peers = new Dictionary<int, Peer>();
    private uint sequence;
    public HostHandshakeSession(TelepathyServerTransport transport, ProtocolIdentity identity)
    {
        this.transport = transport ?? throw new ArgumentNullException(nameof(transport)); Identity = identity; Session = new NetworkSession(identity); SessionId = Session.SessionId;
        transport.Connected += OnConnected; transport.DataReceived += OnData; transport.Disconnected += OnDisconnected;
    }
    public ProtocolIdentity Identity { get; }
    public NetworkSession Session { get; }
    public Guid SessionId { get; }
    public bool IsActive => transport.IsActive;
    public int ReadyPeerCount { get { var count=0; foreach(var peer in peers.Values) if(peer.Ready) count++; return count; } }
    public event Action<int>? PeerAccepted;
    public event Action<int,string>? PeerRejected;
    public event Action<int>? PeerDisconnected;
    public event Action<int,ProtocolEnvelope>? MessageReceived;
    public void Start(ushort port)
    {
        if (port == 0) throw new ArgumentOutOfRangeException(nameof(port));
        peers.Clear(); sequence = 0; transport.Start(port);
    }
    public void Tick(int processLimit = 100) => transport.Tick(processLimit);
    public void Stop() { transport.Stop(); peers.Clear(); }
    public void Dispose()
    {
        transport.Connected -= OnConnected; transport.DataReceived -= OnData; transport.Disconnected -= OnDisconnected;
        Stop(); transport.Dispose();
    }
    private void OnConnected(int connectionId) => peers[connectionId] = new Peer();
    private void OnData(int connectionId, ArraySegment<byte> packet)
    {
        if (!peers.TryGetValue(connectionId, out var peer)) peers[connectionId] = peer = new Peer();
        try
        {
            var envelope = ProtocolEnvelopeCodec.Decode(packet);
            if (peer.Ready)
            {
                if (envelope.SessionId != SessionId) throw new InvalidDataException("Protocol packet session id mismatch.");
                if (envelope.Sequence <= peer.LastSequence) throw new InvalidDataException("Protocol sequence is stale.");
                peer.LastSequence = envelope.Sequence;
                MessageReceived?.Invoke(connectionId,envelope);
                return;
            }
            if (envelope.MessageType != ProtocolMessageType.ClientHello) throw new InvalidDataException("Expected ClientHello.");
            if (envelope.Sequence <= peer.LastSequence) throw new InvalidDataException("Handshake sequence is stale.");
            peer.LastSequence = envelope.Sequence;
            var hello = HandshakeProtocolCodec.DecodeClientHello(envelope.Payload);
            if (envelope.ProtocolVersion != hello.Identity.ProtocolVersion) throw new InvalidDataException("Envelope and identity protocol versions differ.");
            var result = HandshakeValidator.Validate(Identity, hello.Identity);
            if (!result.Accepted) { Reject(connectionId, result.ErrorCode); return; }
            peer.Identity = hello.Identity; peer.Ready = true;
            Send(connectionId, ProtocolMessageType.ServerHello, HandshakeProtocolCodec.Encode(new ServerHello(Identity, connectionId)));
            PeerAccepted?.Invoke(connectionId);
        }
        catch (Exception error) { Reject(connectionId, "INVALID_HANDSHAKE_PACKET: " + error.Message); }
    }
    private void Reject(int connectionId, string error)
    {
        try { Send(connectionId, ProtocolMessageType.HandshakeReject, HandshakeProtocolCodec.Encode(new HandshakeReject(error, Identity))); }
        finally { if (peers.TryGetValue(connectionId, out var peer)) peer.Ready=false; PeerRejected?.Invoke(connectionId,error); }
    }
    private void OnDisconnected(int connectionId) { peers.Remove(connectionId); PeerDisconnected?.Invoke(connectionId); }
    private void Send(int connectionId, ProtocolMessageType type, byte[] payload)
    {
        var envelope = new ProtocolEnvelope(Identity.ProtocolVersion, type, ProtocolFlags.Reliable, ++sequence, SessionId, payload);
        transport.Send(connectionId, new ArraySegment<byte>(ProtocolEnvelopeCodec.Encode(envelope)));
    }
    public void SendMessage(int connectionId, ProtocolMessageType type, byte[] payload)
    {
        if (!peers.TryGetValue(connectionId,out var peer) || !peer.Ready) throw new InvalidOperationException("Peer handshake is not complete.");
        Send(connectionId,type,payload);
    }
}
