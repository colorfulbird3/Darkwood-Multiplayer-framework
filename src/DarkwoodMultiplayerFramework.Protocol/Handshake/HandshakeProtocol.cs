using System;
using System.IO;
using System.Text;

namespace DarkwoodMultiplayerFramework.Protocol;

/// <summary>
/// Handshake identity. The framework has NO backward-compatibility contract:
/// a single FrameworkVersion gate implies every internal wire schema (SaveBundle,
/// WorldSnapshotWire, Action payloads). Only FrameworkVersion and GameVersion are
/// compared; any mismatch rejects the join.
/// </summary>
public readonly struct ProtocolIdentity
{
    public ProtocolIdentity(string frameworkVersion, string gameVersion)
    { FrameworkVersion=frameworkVersion ?? string.Empty; GameVersion=gameVersion ?? string.Empty; }
    public string FrameworkVersion { get; }
    public string GameVersion { get; }
}

public readonly struct ClientHello
{
    public ClientHello(ProtocolIdentity identity, string guestKey)
    { Identity = identity; GuestKey = guestKey ?? string.Empty; }
    public ProtocolIdentity Identity { get; }
    /// <summary>Stable client-chosen player identity used by the host for hot-join profile persistence. Limited to 64 UTF-8 bytes.</summary>
    public string GuestKey { get; }
}

public readonly struct ServerHello
{
    public ServerHello(ProtocolIdentity identity, int peerId) { Identity=identity; PeerId=peerId; }
    public ProtocolIdentity Identity { get; }
    public int PeerId { get; }
}

public readonly struct HandshakeReject
{
    public HandshakeReject(string errorCode, ProtocolIdentity hostIdentity) { ErrorCode=errorCode ?? string.Empty; HostIdentity=hostIdentity; }
    public string ErrorCode { get; }
    public ProtocolIdentity HostIdentity { get; }
}

public readonly struct HandshakeResult
{
    public HandshakeResult(bool accepted, string errorCode, string errorDetail = "") { Accepted=accepted; ErrorCode=errorCode; ErrorDetail=errorDetail; }
    public bool Accepted { get; }
    public string ErrorCode { get; }
    public string ErrorDetail { get; }
}

public static class HandshakeValidator
{
    public static HandshakeResult Validate(ProtocolIdentity host, ProtocolIdentity client)
    {
        if (!string.Equals(host.FrameworkVersion, client.FrameworkVersion, StringComparison.Ordinal)) return Reject("INCOMPATIBLE_FRAMEWORK_VERSION", $"host={host.FrameworkVersion}; client={client.FrameworkVersion}");
        if (!string.Equals(host.GameVersion, client.GameVersion, StringComparison.Ordinal)) return Reject("INCOMPATIBLE_GAME_BUILD", $"host={host.GameVersion}; client={client.GameVersion}");
        return new HandshakeResult(true, string.Empty);
    }
    private static HandshakeResult Reject(string code, string detail = "") => new HandshakeResult(false, code, detail);
}

public static class HandshakeProtocolCodec
{
    private const int MaxStringBytes = 1024;
    private const int MaxGuestKeyBytes = 64;
    public static byte[] Encode(ClientHello message) => Write(writer => { WriteIdentity(writer, message.Identity); WriteLimitedString(writer, message.GuestKey, MaxGuestKeyBytes); });
    public static byte[] Encode(ServerHello message) => Write(writer => { WriteIdentity(writer, message.Identity); writer.Write(message.PeerId); });
    public static byte[] Encode(HandshakeReject message) => Write(writer => { WriteString(writer, message.ErrorCode); WriteIdentity(writer, message.HostIdentity); });
    public static ClientHello DecodeClientHello(byte[] payload) => Read(payload, reader => new ClientHello(ReadIdentity(reader), ReadLimitedString(reader, MaxGuestKeyBytes)));
    public static ServerHello DecodeServerHello(byte[] payload) => Read(payload, reader => new ServerHello(ReadIdentity(reader), reader.ReadInt32()));
    public static HandshakeReject DecodeReject(byte[] payload) => Read(payload, reader => new HandshakeReject(ReadString(reader), ReadIdentity(reader)));
    private static byte[] EncodeIdentity(ProtocolIdentity identity) => Write(writer => WriteIdentity(writer, identity));
    private static byte[] Write(Action<BinaryWriter> write) { using var stream=new MemoryStream(); using var writer=new BinaryWriter(stream,Encoding.UTF8); write(writer); return stream.ToArray(); }
    private static T Read<T>(byte[] payload, Func<BinaryReader,T> read) { using var stream=new MemoryStream(payload ?? Array.Empty<byte>(),false); using var reader=new BinaryReader(stream,Encoding.UTF8); var value=read(reader); if(stream.Position!=stream.Length) throw new InvalidDataException("Handshake payload contains trailing data."); return value; }
    private static void WriteIdentity(BinaryWriter writer, ProtocolIdentity identity) { WriteString(writer,identity.FrameworkVersion); WriteString(writer,identity.GameVersion); }
    private static ProtocolIdentity ReadIdentity(BinaryReader reader) => new ProtocolIdentity(ReadString(reader),ReadString(reader));
    private static void WriteString(BinaryWriter writer,string value) => WriteLimitedString(writer,value,MaxStringBytes);
    private static string ReadString(BinaryReader reader) => ReadLimitedString(reader,MaxStringBytes);
    private static void WriteLimitedString(BinaryWriter writer,string value,int maxBytes) { var bytes=Encoding.UTF8.GetBytes(value ?? string.Empty); if(bytes.Length>maxBytes) throw new InvalidOperationException("Handshake string exceeds the configured limit."); writer.Write((ushort)bytes.Length); writer.Write(bytes); }
    private static string ReadLimitedString(BinaryReader reader,int maxBytes) { var length=reader.ReadUInt16(); if(length>maxBytes) throw new InvalidDataException("Handshake string exceeds the configured limit."); var bytes=reader.ReadBytes(length); if(bytes.Length!=length) throw new EndOfStreamException(); return Encoding.UTF8.GetString(bytes); }
}
