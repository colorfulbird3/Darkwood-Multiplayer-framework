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
    ContainerStateReport = 65,
    InventoryCommit = 66,    // v0.9.2：玩家背包 Commit（revision 单向递增，Client 拥有）
    ContainerCommit = 67,    // v0.9.2：共享容器 Commit（baseContainerRevision + 玩家 revision 双锚定）
    PickupCommit = 68,       // v0.9.2：地面拾取 Commit（RuntimeEntityId + InventorySnapshot）
    DropCommit = 69,         // v0.9.2：丢弃 Commit（localDropToken + DroppedItemState + InventorySnapshot）
    DropCommitAck = 70,      // v0.9.2 P0-9：Host → 发起 Client 的 Ack（RuntimeEntityId 复用本地对象）
    PickupReconcile = 71,    // v0.9.2 P0-6：race 时 Host 推权威玩家背包快照让客户端拒绝旧 revision
    LegacyAuthAction = 72,   // v0.9.2：客户端仍发旧 authority Action 的兼容通道（Host 记 [LEGACY-AUTH] 报警）
    ContainerCommitAck = 73,        // v0.9.2 P0-4：Host → 客户端原子确认（newRevision + transactionId）
    ContainerReconcile = 74,        // v0.9.2 P0-4：Host → 客户端冲突权威回滚（含 transactionId + 真实 EntityId + slots）
    TransactionReconcile = 75,      // v0.9.2 P0-6：Host → 客户端整事务回滚（含 PlayerInventory + 所有 ContainerMutations 权威快照）
    InventoryTransactionCommit = 76,// v0.9.2 P0-6：原子提交 玩家背包 + N 个容器 mutation（全有/全无）
    // v0.9.5（P3 WorldState 域）：世界级事件/状态——Host 单点决定，事件广播，Client 只读镜像。
    WorldEventFired = 77,    // 世界事件触发（夜事件/任务/剧情因果广播；Host 单点）
    FlagBoolChanged = 78,    // 剧情/任务 bool flag 变化
    FlagIntChanged = 79,     // 剧情/任务 int flag 变化
    ClockState = 80,         // 世界时钟（小时/天/暂停）——Host 权威
    RainState = 81,          // 天气（雨）状态
    ActionExecuted = 82,     // v0.9.0 Action Sync：Host 已执行某原版副作用 → 各端 Replay 同一函数（EntityId+ActionKey+Param+Tick）
    RemoveWorldItem = 83,    // v0.9.0 A3：客户端本地移除的持久世界物（拆夹子等）→ 通知 Host 销毁并广播 despawn
    TrapTriggered = 84,      // v0.9.0（r17）：客户端本地夹子触发（合拢）→ 上报 Host → Host 本体合拢 + 权威广播（双向夹子视觉同步）
    PresentationEvent = 85,  // v0.9.0（P1）：Host→All 通用瞬时表现事件（气泡/地点发现/外部场景/地图标记/受击音等）——
                             // {Kind(byte), TargetId(str), Data(str)}，按 kind 扩展语义，无需再改 wire
    TestControl = 200,               // v0.9.2 TestHarness：仅 TestMode 启用，不影响正式协议兼容
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
