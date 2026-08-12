using System;
using System.IO;
using System.Text;

namespace DarkwoodMultiplayerFramework.Protocol;

public readonly struct ProtocolIdentity
{
    public ProtocolIdentity(int protocolVersion, string frameworkVersion, string gameVersion, int saveSchemaVersion, int snapshotSchemaVersion)
    { ProtocolVersion=protocolVersion; FrameworkVersion=frameworkVersion ?? string.Empty; GameVersion=gameVersion ?? string.Empty; SaveSchemaVersion=saveSchemaVersion; SnapshotSchemaVersion=snapshotSchemaVersion; }
    public int ProtocolVersion { get; }
    public string FrameworkVersion { get; }
    public string GameVersion { get; }
    public int SaveSchemaVersion { get; }
    public int SnapshotSchemaVersion { get; }
}

public readonly struct ClientHello
{
    public ClientHello(ProtocolIdentity identity) => Identity = identity;
    public ProtocolIdentity Identity { get; }
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
    public HandshakeResult(bool accepted, string errorCode) { Accepted=accepted; ErrorCode=errorCode; }
    public bool Accepted { get; }
    public string ErrorCode { get; }
}

public static class HandshakeValidator
{
    public static HandshakeResult Validate(ProtocolIdentity host, ProtocolIdentity client)
    {
        if (host.ProtocolVersion != client.ProtocolVersion) return Reject("INCOMPATIBLE_PROTOCOL");
        if (!string.Equals(host.FrameworkVersion, client.FrameworkVersion, StringComparison.Ordinal)) return Reject("INCOMPATIBLE_FRAMEWORK_VERSION");
        if (!string.Equals(host.GameVersion, client.GameVersion, StringComparison.Ordinal)) return Reject("INCOMPATIBLE_GAME_BUILD");
        if (host.SaveSchemaVersion != client.SaveSchemaVersion) return Reject("INCOMPATIBLE_SAVE_SCHEMA");
        if (host.SnapshotSchemaVersion != client.SnapshotSchemaVersion) return Reject("INCOMPATIBLE_SNAPSHOT_SCHEMA");
        return new HandshakeResult(true, string.Empty);
    }
    private static HandshakeResult Reject(string code) => new HandshakeResult(false, code);
}

public static class HandshakeProtocolCodec
{
    private const int MaxStringBytes = 1024;
    public static byte[] Encode(ClientHello message) => EncodeIdentity(message.Identity);
    public static byte[] Encode(ServerHello message) => Write(writer => { WriteIdentity(writer, message.Identity); writer.Write(message.PeerId); });
    public static byte[] Encode(HandshakeReject message) => Write(writer => { WriteString(writer, message.ErrorCode); WriteIdentity(writer, message.HostIdentity); });
    public static ClientHello DecodeClientHello(byte[] payload) => new ClientHello(Read(payload, ReadIdentity));
    public static ServerHello DecodeServerHello(byte[] payload) => Read(payload, reader => new ServerHello(ReadIdentity(reader), reader.ReadInt32()));
    public static HandshakeReject DecodeReject(byte[] payload) => Read(payload, reader => new HandshakeReject(ReadString(reader), ReadIdentity(reader)));
    private static byte[] EncodeIdentity(ProtocolIdentity identity) => Write(writer => WriteIdentity(writer, identity));
    private static byte[] Write(Action<BinaryWriter> write) { using var stream=new MemoryStream(); using var writer=new BinaryWriter(stream,Encoding.UTF8); write(writer); return stream.ToArray(); }
    private static T Read<T>(byte[] payload, Func<BinaryReader,T> read) { using var stream=new MemoryStream(payload ?? Array.Empty<byte>(),false); using var reader=new BinaryReader(stream,Encoding.UTF8); var value=read(reader); if(stream.Position!=stream.Length) throw new InvalidDataException("Handshake payload contains trailing data."); return value; }
    private static void WriteIdentity(BinaryWriter writer, ProtocolIdentity identity) { writer.Write(identity.ProtocolVersion); WriteString(writer,identity.FrameworkVersion); WriteString(writer,identity.GameVersion); writer.Write(identity.SaveSchemaVersion); writer.Write(identity.SnapshotSchemaVersion); }
    private static ProtocolIdentity ReadIdentity(BinaryReader reader) => new ProtocolIdentity(reader.ReadInt32(),ReadString(reader),ReadString(reader),reader.ReadInt32(),reader.ReadInt32());
    private static void WriteString(BinaryWriter writer,string value) { var bytes=Encoding.UTF8.GetBytes(value ?? string.Empty); if(bytes.Length>MaxStringBytes) throw new InvalidOperationException("Handshake string exceeds the configured limit."); writer.Write((ushort)bytes.Length); writer.Write(bytes); }
    private static string ReadString(BinaryReader reader) { var length=reader.ReadUInt16(); if(length>MaxStringBytes) throw new InvalidDataException("Handshake string exceeds the configured limit."); var bytes=reader.ReadBytes(length); if(bytes.Length!=length) throw new EndOfStreamException(); return Encoding.UTF8.GetString(bytes); }
}
