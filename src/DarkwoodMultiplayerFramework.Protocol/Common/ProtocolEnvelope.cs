using System;
using System.IO;

namespace DarkwoodMultiplayerFramework.Protocol;

public enum ProtocolMessageType : ushort
{
    ClientHello = 1,
    ServerHello = 2,
    HandshakeReject = 3,
    SaveTransferRequest = 10,
    SaveTransferManifest = 11,
    SaveTransferChunk = 12,
    SaveTransferApplied = 13,
    WorldSnapshotManifest = 20,
    WorldSnapshotChunk = 21,
    WorldSnapshotApplied = 22,
    EntityBindingManifest = 23,
    EntityBindingChunk = 24,
    EntityDelta = 30,
    EntityDespawn = 31,
    InventoryState = 32,
    PlayerPose = 33,
    GuestProfile = 34,
    PlayerHealth = 35,
    RescueRequest = 36,
    RescueProgress = 37,
    PlayerInventoryState = 39,
    AllDowned = 38,
    ActionRequest = 50,
    ActionResult = 51,
    ActionRejected = 52,
    Ready = 40,
    RuntimeEntitySpawn = 60,
    RuntimeEntityDespawn = 61,
    SceneChange = 62,
    GuestProfileApplied = 63,
    PlayerAction = 64,
    Error = 255
}

[Flags]
public enum ProtocolFlags : ushort
{
    None = 0,
    Reliable = 1
}

public readonly struct ProtocolEnvelope
{
    public const uint Magic = 0x38464D44; // DMF8 in little endian.
    public const ushort HeaderVersion = 1;
    public const int HeaderSize = 38;
    public const int MaxPayloadSize = 16 * 1024 * 1024;

    public ProtocolEnvelope(int protocolVersion, ProtocolMessageType messageType, ProtocolFlags flags, uint sequence, Guid sessionId, byte[] payload)
    {
        ProtocolVersion = protocolVersion;
        MessageType = messageType;
        Flags = flags;
        Sequence = sequence;
        SessionId = sessionId;
        Payload = payload ?? Array.Empty<byte>();
    }

    public int ProtocolVersion { get; }
    public ProtocolMessageType MessageType { get; }
    public ProtocolFlags Flags { get; }
    public uint Sequence { get; }
    public Guid SessionId { get; }
    public byte[] Payload { get; }
}

public static class ProtocolEnvelopeCodec
{
    public static byte[] Encode(ProtocolEnvelope envelope)
    {
        if (envelope.ProtocolVersion <= 0) throw new InvalidOperationException("Protocol version must be positive.");
        if (!Enum.IsDefined(typeof(ProtocolMessageType), envelope.MessageType)) throw new InvalidOperationException("Protocol message type is invalid.");
        if ((envelope.Flags & ~ProtocolFlags.Reliable) != 0) throw new InvalidOperationException("Protocol flags are invalid.");
        if (envelope.Sequence == 0) throw new InvalidOperationException("Protocol sequence must be positive.");
        if (envelope.SessionId == Guid.Empty) throw new InvalidOperationException("Protocol session id must not be empty.");
        if (envelope.Payload.Length > ProtocolEnvelope.MaxPayloadSize) throw new InvalidOperationException("Protocol payload exceeds the configured limit.");
        using var stream = new MemoryStream(ProtocolEnvelope.HeaderSize + envelope.Payload.Length);
        using var writer = new BinaryWriter(stream);
        writer.Write(ProtocolEnvelope.Magic);
        writer.Write(ProtocolEnvelope.HeaderVersion);
        writer.Write(envelope.ProtocolVersion);
        writer.Write((ushort)envelope.MessageType);
        writer.Write((ushort)envelope.Flags);
        writer.Write(envelope.Sequence);
        writer.Write(envelope.Payload.Length);
        writer.Write(envelope.SessionId.ToByteArray());
        writer.Write(envelope.Payload);
        return stream.ToArray();
    }

    public static ProtocolEnvelope Decode(ArraySegment<byte> packet)
    {
        if (packet.Array == null || packet.Offset < 0 || packet.Count < ProtocolEnvelope.HeaderSize || packet.Offset + packet.Count > packet.Array.Length) throw new InvalidDataException("Protocol packet is truncated.");
        using var stream = new MemoryStream(packet.Array, packet.Offset, packet.Count, false);
        using var reader = new BinaryReader(stream);
        if (reader.ReadUInt32() != ProtocolEnvelope.Magic) throw new InvalidDataException("Protocol magic mismatch.");
        if (reader.ReadUInt16() != ProtocolEnvelope.HeaderVersion) throw new InvalidDataException("Protocol header version mismatch.");
        var protocolVersion = reader.ReadInt32();
        var messageType = (ProtocolMessageType)reader.ReadUInt16();
        var flags = (ProtocolFlags)reader.ReadUInt16();
        var sequence = reader.ReadUInt32();
        var length = reader.ReadInt32();
        var sessionId = new Guid(reader.ReadBytes(16));
        if (protocolVersion <= 0) throw new InvalidDataException("Protocol version is invalid.");
        if (!Enum.IsDefined(typeof(ProtocolMessageType), messageType)) throw new InvalidDataException("Unknown protocol message type.");
        if ((flags & ~ProtocolFlags.Reliable) != 0) throw new InvalidDataException("Protocol flags are invalid.");
        if (sequence == 0) throw new InvalidDataException("Protocol sequence is invalid.");
        if (sessionId == Guid.Empty) throw new InvalidDataException("Protocol session id is invalid.");
        if (length < 0 || length > ProtocolEnvelope.MaxPayloadSize || length != stream.Length - stream.Position) throw new InvalidDataException("Protocol payload length mismatch.");
        var payload = reader.ReadBytes(length);
        return new ProtocolEnvelope(protocolVersion, messageType, flags, sequence, sessionId, payload);
    }
}
