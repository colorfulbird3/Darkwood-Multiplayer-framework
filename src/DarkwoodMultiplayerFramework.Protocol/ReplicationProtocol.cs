using System;
using System.IO;
using System.Text;

namespace DarkwoodMultiplayerFramework.Protocol;

public readonly struct SaveTransferRequest
{
    public SaveTransferRequest(Guid requestId) { RequestId = requestId; }
    public Guid RequestId { get; }
}

public readonly struct SaveTransferManifest
{
    public SaveTransferManifest(Guid transferId, int profileId, long totalBytes, int chunkCount, byte[] sha256, string description)
    { TransferId=transferId; ProfileId=profileId; TotalBytes=totalBytes; ChunkCount=chunkCount; Sha256=sha256 ?? Array.Empty<byte>(); Description=description ?? string.Empty; }
    public Guid TransferId { get; }
    public int ProfileId { get; }
    public long TotalBytes { get; }
    public int ChunkCount { get; }
    public byte[] Sha256 { get; }
    public string Description { get; }
}

public readonly struct SaveTransferChunk
{
    public SaveTransferChunk(Guid transferId, int index, int total, byte[] data, byte[] hash)
    { TransferId=transferId; Index=index; Total=total; Data=data ?? Array.Empty<byte>(); Hash=hash ?? Array.Empty<byte>(); }
    public Guid TransferId { get; }
    public int Index { get; }
    public int Total { get; }
    public byte[] Data { get; }
    public byte[] Hash { get; }
}

public readonly struct SaveTransferApplied
{
    public SaveTransferApplied(Guid transferId, int profileId, string saveDirectory)
    { TransferId=transferId; ProfileId=profileId; SaveDirectory=saveDirectory??string.Empty; }
    public Guid TransferId { get; } public int ProfileId { get; } public string SaveDirectory { get; }
}


public readonly struct WorldSnapshotManifest
{
    public WorldSnapshotManifest(Guid snapshotId, long totalBytes, int chunkCount, byte[] sha256, string scene, string registryDigest, long serverTick)
    { SnapshotId=snapshotId; TotalBytes=totalBytes; ChunkCount=chunkCount; Sha256=sha256 ?? Array.Empty<byte>(); Scene=scene ?? string.Empty; RegistryDigest=registryDigest ?? string.Empty; ServerTick=serverTick; }
    public Guid SnapshotId { get; }
    public long TotalBytes { get; }
    public int ChunkCount { get; }
    public byte[] Sha256 { get; }
    public string Scene { get; }
    public string RegistryDigest { get; }
    public long ServerTick { get; }
}

public readonly struct WorldSnapshotChunk
{
    public WorldSnapshotChunk(Guid snapshotId, int index, int total, byte[] data, byte[] hash)
    { SnapshotId=snapshotId; Index=index; Total=total; Data=data ?? Array.Empty<byte>(); Hash=hash ?? Array.Empty<byte>(); }
    public Guid SnapshotId { get; }
    public int Index { get; }
    public int Total { get; }
    public byte[] Data { get; }
    public byte[] Hash { get; }
}

public readonly struct WorldSnapshotApplied
{
    public WorldSnapshotApplied(Guid snapshotId, string scene, string registryDigest, long serverTick, int entityCount)
    { SnapshotId=snapshotId; Scene=scene??string.Empty; RegistryDigest=registryDigest??string.Empty; ServerTick=serverTick; EntityCount=entityCount; }
    public Guid SnapshotId { get; } public string Scene { get; } public string RegistryDigest { get; } public long ServerTick { get; } public int EntityCount { get; }
}

public readonly struct EntityStateWire
{
    public EntityStateWire(ulong value, bool persistent, byte kind, float x, float y, float z, float qx, float qy, float qz, float qw, float health, int stateA, int stateB, byte flags, string animation, int frame, ulong revision)
    { Value=value; Persistent=persistent; Kind=kind; X=x; Y=y; Z=z; Qx=qx; Qy=qy; Qz=qz; Qw=qw; Health=health; StateA=stateA; StateB=stateB; Flags=flags; Animation=animation ?? string.Empty; Frame=frame; Revision=revision; }
    public ulong Value { get; } public bool Persistent { get; } public byte Kind { get; }
    public float X { get; } public float Y { get; } public float Z { get; }
    public float Qx { get; } public float Qy { get; } public float Qz { get; } public float Qw { get; }
    public float Health { get; } public int StateA { get; } public int StateB { get; } public byte Flags { get; }
    public string Animation { get; } public int Frame { get; } public ulong Revision { get; }
}

public readonly struct EntityDeltaMessage
{
    public EntityDeltaMessage(string scene, long serverTick, EntityStateWire[] entities, EntityStateWire[] despawns)
    { Scene=scene ?? string.Empty; ServerTick=serverTick; Entities=entities ?? Array.Empty<EntityStateWire>(); Despawns=despawns ?? Array.Empty<EntityStateWire>(); }
    public string Scene { get; } public long ServerTick { get; } public EntityStateWire[] Entities { get; } public EntityStateWire[] Despawns { get; }
}

public readonly struct ReadyMessage
{
    public ReadyMessage(string scene,string registryDigest){Scene=scene??string.Empty;RegistryDigest=registryDigest??string.Empty;}
    public string Scene {get;} public string RegistryDigest {get;}
}

public readonly struct ProtocolErrorMessage
{
    public ProtocolErrorMessage(string code,string detail){Code=code??string.Empty;Detail=detail??string.Empty;}
    public string Code {get;} public string Detail {get;}
}

public readonly struct InventorySlotWire
{
    public InventorySlotWire(string type,int amount,float durability,int quality,bool recipe){Type=type??string.Empty;Amount=amount;Durability=durability;Quality=quality;Recipe=recipe;}
    public string Type {get;} public int Amount {get;} public float Durability {get;} public int Quality {get;} public bool Recipe {get;}
}

public readonly struct InventoryStateMessage
{
    public InventoryStateMessage(ulong value,bool persistent,ulong revision,InventorySlotWire[] slots)
        : this(value,persistent,revision,string.Empty,0,0,0,-1,slots){}
    public InventoryStateMessage(ulong value,bool persistent,ulong revision,string name,float x,float y,float z,int inventoryType,InventorySlotWire[] slots){Value=value;Persistent=persistent;Revision=revision;Name=name??string.Empty;X=x;Y=y;Z=z;InventoryType=inventoryType;Slots=slots??Array.Empty<InventorySlotWire>();}
    public ulong Value {get;} public bool Persistent {get;} public ulong Revision {get;} public string Name {get;} public float X {get;} public float Y {get;} public float Z {get;} public int InventoryType {get;} public InventorySlotWire[] Slots {get;}
}

public readonly struct PlayerPoseMessage
{
    public PlayerPoseMessage(int playerId,uint sequence,string scene,float x,float y,float z,float qx,float qy,float qz,float qw,float maxHealth,byte flags,string torsoClip,int torsoFrame,string legsClip,int legsFrame)
    { PlayerId=playerId;Sequence=sequence;Scene=scene??string.Empty;X=x;Y=y;Z=z;Qx=qx;Qy=qy;Qz=qz;Qw=qw;MaxHealth=maxHealth;Flags=flags;TorsoClip=torsoClip??string.Empty;TorsoFrame=torsoFrame;LegsClip=legsClip??string.Empty;LegsFrame=legsFrame; }
    public int PlayerId {get;} public uint Sequence {get;} public string Scene {get;} public float X {get;} public float Y {get;} public float Z {get;} public float Qx {get;} public float Qy {get;} public float Qz {get;} public float Qw {get;}
    /// <summary>Sender's maximum health, used by the host for revive scaling (downed flag is bit 4 of Flags).</summary>
    public float MaxHealth {get;} public byte Flags {get;} public string TorsoClip {get;} public int TorsoFrame {get;} public string LegsClip {get;} public int LegsFrame {get;}
}

/// <summary>Player flags carried in PlayerPoseMessage.</summary>
public static class PlayerPoseFlags
{
    public const byte Walking = 1;
    public const byte Running = 2;
    public const byte Aiming = 4;
    public const byte Attacking = 8;
    public const byte Downed = 16;
}

public enum ActionKindWire : byte
{
    Pickup = 1,
    ContainerTake = 2,
    ContainerPut = 3,
    Attack = 4,
    DoorInteract = 5,
    WindowInteract = 6,
    ItemActivate = 7
}

public readonly struct ActionRequestMessage
{
    public ActionRequestMessage(Guid requestId,int playerId,ActionKindWire kind,ulong targetValue,bool targetPersistent,ulong expectedRevision,byte[] payload)
    {RequestId=requestId;PlayerId=playerId;Kind=kind;TargetValue=targetValue;TargetPersistent=targetPersistent;ExpectedRevision=expectedRevision;Payload=payload??Array.Empty<byte>();}
    public Guid RequestId{get;} public int PlayerId{get;} public ActionKindWire Kind{get;} public ulong TargetValue{get;} public bool TargetPersistent{get;} public ulong ExpectedRevision{get;} public byte[] Payload{get;}
}

public readonly struct ActionResultMessage
{
    public ActionResultMessage(Guid requestId,ActionKindWire kind,ulong targetValue,bool targetPersistent,ulong revision,byte[] payload)
    {RequestId=requestId;Kind=kind;TargetValue=targetValue;TargetPersistent=targetPersistent;Revision=revision;Payload=payload??Array.Empty<byte>();}
    public Guid RequestId{get;} public ActionKindWire Kind{get;} public ulong TargetValue{get;} public bool TargetPersistent{get;} public ulong Revision{get;} public byte[] Payload{get;}
}

public readonly struct ActionRejectedMessage
{
    public ActionRejectedMessage(Guid requestId,ActionKindWire kind,ulong targetValue,bool targetPersistent,ulong currentRevision,string errorCode)
    {RequestId=requestId;Kind=kind;TargetValue=targetValue;TargetPersistent=targetPersistent;CurrentRevision=currentRevision;ErrorCode=errorCode??string.Empty;}
    public Guid RequestId{get;} public ActionKindWire Kind{get;} public ulong TargetValue{get;} public bool TargetPersistent{get;} public ulong CurrentRevision{get;} public string ErrorCode{get;}
}

public readonly struct PickupResultPayload
{
    public PickupResultPayload(string itemType,int amount,float durability,int quality,bool recipe)
    {ItemType=itemType??string.Empty;Amount=amount;Durability=durability;Quality=quality;Recipe=recipe;}
    public string ItemType{get;} public int Amount{get;} public float Durability{get;} public int Quality{get;} public bool Recipe{get;}
}

public readonly struct ContainerTakePayload
{
    public ContainerTakePayload(int slotIndex, int amount)
    { SlotIndex=slotIndex; Amount=amount; }
    public int SlotIndex {get;}
    /// <summary>Requested amount. A negative value means the complete stack.</summary>
    public int Amount {get;}
}

public readonly struct ContainerPutPayload
{
    public ContainerPutPayload(bool hotbar, int slotIndex, int amount) : this(hotbar,slotIndex,-1,amount){}
    public ContainerPutPayload(bool hotbar, int slotIndex, int destinationSlotIndex, int amount)
    { Hotbar=hotbar; SlotIndex=slotIndex; DestinationSlotIndex=destinationSlotIndex; Amount=amount; }
    public bool Hotbar {get;}
    public int SlotIndex {get;}
    /// <summary>Exact destination slot for drag/drop. A negative value means quick-transfer to any suitable slot.</summary>
    public int DestinationSlotIndex {get;}
    /// <summary>Requested amount. A negative value means the complete stack.</summary>
    public int Amount {get;}
}

/// <summary>The complete host-owned inventory state for the requesting player.</summary>
public readonly struct PlayerInventoryStatePayload
{
    public PlayerInventoryStatePayload(InventorySlotWire[] backpack, InventorySlotWire[] hotbar)
    { Backpack=backpack??Array.Empty<InventorySlotWire>(); Hotbar=hotbar??Array.Empty<InventorySlotWire>(); }
    public InventorySlotWire[] Backpack {get;}
    public InventorySlotWire[] Hotbar {get;}
}

/// <summary>Host-authoritative guest bootstrap: the spawn position and inventory a joining client applies right before Ready.</summary>
public readonly struct GuestProfileMessage
{
    public GuestProfileMessage(PlayerInventoryStatePayload inventory, float x, float y, float z, int day, int joinCount, float health, float maxHealth, bool downed)
    { Inventory=inventory; X=x; Y=y; Z=z; Day=day; JoinCount=joinCount; Health=health; MaxHealth=maxHealth; Downed=downed; }
    public PlayerInventoryStatePayload Inventory {get;}
    public float X {get;} public float Y {get;} public float Z {get;}
    public int Day {get;} public int JoinCount {get;}
    public float Health {get;} public float MaxHealth {get;} public bool Downed {get;}
}

/// <summary>Host-authoritative per-player health state. Clients apply it only for their own player id.</summary>
public readonly struct PlayerHealthMessage
{
    public PlayerHealthMessage(int playerId, float health, float maxHealth, bool downed)
    { PlayerId=playerId; Health=health; MaxHealth=maxHealth; Downed=downed; }
    public int PlayerId {get;} public float Health {get;} public float MaxHealth {get;} public bool Downed {get;}
}

/// <summary>Rescue intent from a living player. The host picks the nearest downed player within range.</summary>
public readonly struct RescueRequestMessage
{
    public RescueRequestMessage(int playerId, bool cancel) { PlayerId=playerId; Cancel=cancel; }
    public int PlayerId {get;} public bool Cancel {get;}
}

/// <summary>Host-authoritative rescue progress (0..1). Active=false is the terminal state after completion or cancellation.</summary>
public readonly struct RescueProgressMessage
{
    public RescueProgressMessage(int targetId, int rescuerId, float progress, bool active)
    { TargetId=targetId; RescuerId=rescuerId; Progress=progress; Active=active; }
    public int TargetId {get;} public int RescuerId {get;} public float Progress {get;} public bool Active {get;}
}

/// <summary>Broadcast when every player is downed: each machine then runs the vanilla death ending locally.</summary>
public readonly struct AllDownedMessage
{
}

/// <summary>Persistent hot-join guest identity record stored by the host beside the save. Binary, format-versioned.</summary>
public readonly struct GuestProfileRecord
{
    public GuestProfileRecord(string guestKey, int day, int joinCount, float x, float y, float z, InventorySlotWire[] backpack, InventorySlotWire[] hotbar, long lastSeenUtcTicks)
    { GuestKey=guestKey??string.Empty; Day=day; JoinCount=joinCount; X=x; Y=y; Z=z; Backpack=backpack??Array.Empty<InventorySlotWire>(); Hotbar=hotbar??Array.Empty<InventorySlotWire>(); LastSeenUtcTicks=lastSeenUtcTicks; }
    public string GuestKey {get;} public int Day {get;} public int JoinCount {get;}
    public float X {get;} public float Y {get;} public float Z {get;}
    public InventorySlotWire[] Backpack {get;} public InventorySlotWire[] Hotbar {get;}
    public long LastSeenUtcTicks {get;}
    public bool HasPosition => X != 0f || Y != 0f || Z != 0f;
}

/// <summary>Client melee attack intent. The host derives damage from its own shadow inventory; the client never sends damage values.</summary>
public readonly struct AttackPayload
{
    public AttackPayload(byte attackKind, bool fromHotbar, int slotIndex, float dirX, float dirZ, float posX, float posZ)
    { AttackKind=attackKind; FromHotbar=fromHotbar; SlotIndex=slotIndex; DirX=dirX; DirZ=dirZ; PosX=posX; PosZ=posZ; }
    /// <summary>1 = melee, 2 = special melee.</summary>
    public byte AttackKind {get;}
    public bool FromHotbar {get;}
    public int SlotIndex {get;}
    /// <summary>Horizontal aim direction (Darkwood uses transform.up as the attack vector; x/z only).</summary>
    public float DirX {get;} public float DirZ {get;}
    /// <summary>Client player position at swing time; the host sanity-checks it against the tracked remote pose.</summary>
    public float PosX {get;} public float PosZ {get;}
}

/// <summary>0.8.8-alpha.1：运行时实体类别。扩展时递增框架版本（无向下兼容）。</summary>
public enum RuntimeEntityKind : byte
{
    /// <summary>非法/未知，线路上不应出现。</summary>
    Unknown = 0,
    /// <summary>运行时生成的可拾取物品（0.8.8-alpha.3 首个验证目标）。</summary>
    DroppedItem = 1,
    /// <summary>运行时生成的敌人（0.8.8-alpha.4）。</summary>
    Enemy = 2,
    /// <summary>敌人死亡产生的尸体。</summary>
    Corpse = 3,
    /// <summary>运行时生成的可搜刮容器（乌鸦群、动物尸体等 deathDrop 类对象）。</summary>
    LootContainer = 4,
}

/// <summary>0.8.8-alpha.1：运行时实体移除原因。</summary>
public enum RuntimeEntityDespawnReason : byte
{
    Unknown = 0,
    /// <summary>被拾取/收集（物品进背包）。</summary>
    Collected = 1,
    /// <summary>死亡（敌人）。</summary>
    Died = 2,
    /// <summary>被玩家破坏/摧毁。</summary>
    Destroyed = 3,
    /// <summary>其他（场景切换清理等）。</summary>
    Other = 255,
}

/// <summary>
/// 0.8.8-alpha.1：Runtime Entity 生成广播。RuntimeEntityId 只能由 Host 分配，
/// 会话内单调递增、绝不复用（销毁的 ID 不再分配给新对象）。
/// InitialState 预留给 alpha.3+ 的实体专属初始状态（当前可为空）。
/// </summary>
public readonly struct RuntimeEntitySpawnMessage
{
    public RuntimeEntitySpawnMessage(ulong runtimeEntityId,RuntimeEntityKind kind,string prototypeId,string scene,float x,float y,float z,float qx,float qy,float qz,float qw,byte[] initialState,long serverTick)
    {RuntimeEntityId=runtimeEntityId;Kind=kind;PrototypeId=prototypeId;Scene=scene;X=x;Y=y;Z=z;Qx=qx;Qy=qy;Qz=qz;Qw=qw;InitialState=initialState??Array.Empty<byte>();ServerTick=serverTick;}
    public ulong RuntimeEntityId {get;}
    public RuntimeEntityKind Kind {get;}
    public string PrototypeId {get;}
    public string Scene {get;}
    public float X {get;} public float Y {get;} public float Z {get;}
    public float Qx {get;} public float Qy {get;} public float Qz {get;} public float Qw {get;}
    public byte[] InitialState {get;}
    public long ServerTick {get;}
}

/// <summary>0.8.8-alpha.1：Runtime Entity 移除广播。</summary>
public readonly struct RuntimeEntityDespawnMessage
{
    public RuntimeEntityDespawnMessage(ulong runtimeEntityId,long serverTick,RuntimeEntityDespawnReason reason)
    {RuntimeEntityId=runtimeEntityId;ServerTick=serverTick;Reason=reason;}
    public ulong RuntimeEntityId {get;}
    public long ServerTick {get;}
    public RuntimeEntityDespawnReason Reason {get;}
}

/// <summary>0.8.8-alpha.6：主机场景切换通知（客户端收到后自动重连并重新加载新场景存档）。</summary>
public readonly struct SceneChangeMessage
{
    public SceneChangeMessage(string scene){Scene=scene;}
    public string Scene {get;}
}

/// <summary>Generic world-interaction payload. ValueA semantics depend on the action kind.</summary>
public readonly struct InteractPayload
{
    public InteractPayload(int valueA) { ValueA = valueA; }
    /// <summary>For WindowInteract: the requested destination barricade health passed to Window.barricade.</summary>
    public int ValueA {get;}
}

/// <summary>Single source of truth for the wire identity.</summary>
/// <remarks>
/// The framework does NOT support backward compatibility: FrameworkVersion is the
/// single version gate (PROTO-001 resolution). The internal SaveBundle wire (3) and
/// WorldSnapshotWire schema (2) headers are implementation details implied by the
/// framework version and are not negotiated separately.
/// </remarks>
public static class ProtocolVersions
{
    /// <summary>Envelope framing version (ProtocolEnvelope header). Constant within the framework line.</summary>
    public const int EnvelopeProtocol = 3;
    public const string Framework = "0.8.8-beta.3";
}

public static class ReplicationProtocolCodec
{
    private const int MaxChunks = 4096, MaxEntities = 4096, MaxString = 4096, MaxHash = 64;
    public static byte[] Encode(SaveTransferRequest m) => Write(w => w.Write(m.RequestId.ToByteArray()));
    public static SaveTransferRequest DecodeSaveTransferRequest(byte[] p) => Read(p, r => new SaveTransferRequest(new Guid(ReadExact(r,16))));
    public static byte[] Encode(SaveTransferManifest m) => Write(w => { w.Write(m.TransferId.ToByteArray()); w.Write(m.ProfileId); w.Write(m.TotalBytes); w.Write(m.ChunkCount); WriteBytes(w,m.Sha256,MaxHash); WriteString(w,m.Description); });
    public static SaveTransferManifest DecodeSaveTransferManifest(byte[] p) => Read(p,r => new SaveTransferManifest(new Guid(ReadExact(r,16)),r.ReadInt32(),r.ReadInt64(),ReadCount(r,MaxChunks),ReadBytes(r,MaxHash),ReadString(r)));
    public static byte[] Encode(SaveTransferChunk m) => Write(w => { w.Write(m.TransferId.ToByteArray()); w.Write(m.Index); w.Write(m.Total); WriteBytes(w,m.Hash,MaxHash); WriteBytes(w,m.Data,SnapshotMax); });
    public static SaveTransferChunk DecodeSaveTransferChunk(byte[] p) => Read(p,r => { var id=new Guid(ReadExact(r,16));var index=r.ReadInt32();var total=ReadCount(r,MaxChunks);var hash=ReadBytes(r,MaxHash);var data=ReadBytes(r,SnapshotMax);return new SaveTransferChunk(id,index,total,data,hash); });
    public static byte[] Encode(SaveTransferApplied m) => Write(w=>{w.Write(m.TransferId.ToByteArray());w.Write(m.ProfileId);WriteString(w,m.SaveDirectory);});
    public static SaveTransferApplied DecodeSaveTransferApplied(byte[] p)=>Read(p,r=>new SaveTransferApplied(new Guid(ReadExact(r,16)),r.ReadInt32(),ReadString(r)));
    public static byte[] Encode(WorldSnapshotManifest m) => Write(w => { w.Write(m.SnapshotId.ToByteArray()); w.Write(m.TotalBytes); w.Write(m.ChunkCount); WriteBytes(w,m.Sha256,MaxHash); WriteString(w,m.Scene); WriteString(w,m.RegistryDigest); w.Write(m.ServerTick); });
    public static WorldSnapshotManifest DecodeWorldSnapshotManifest(byte[] p) => Read(p,r => new WorldSnapshotManifest(new Guid(ReadExact(r,16)),r.ReadInt64(),ReadCount(r,MaxChunks),ReadBytes(r,MaxHash),ReadString(r),ReadString(r),r.ReadInt64()));
    public static byte[] Encode(WorldSnapshotChunk m) => Write(w => { w.Write(m.SnapshotId.ToByteArray()); w.Write(m.Index); w.Write(m.Total); WriteBytes(w,m.Hash,MaxHash); WriteBytes(w,m.Data,SnapshotMax); });
    public static WorldSnapshotChunk DecodeWorldSnapshotChunk(byte[] p) => Read(p,r => { var id=new Guid(ReadExact(r,16));var index=r.ReadInt32();var total=ReadCount(r,MaxChunks);var hash=ReadBytes(r,MaxHash);var data=ReadBytes(r,SnapshotMax);return new WorldSnapshotChunk(id,index,total,data,hash); });
    public static byte[] Encode(WorldSnapshotApplied m)=>Write(w=>{w.Write(m.SnapshotId.ToByteArray());WriteString(w,m.Scene);WriteString(w,m.RegistryDigest);w.Write(m.ServerTick);w.Write(m.EntityCount);});
    public static WorldSnapshotApplied DecodeWorldSnapshotApplied(byte[] p)=>Read(p,r=>new WorldSnapshotApplied(new Guid(ReadExact(r,16)),ReadString(r),ReadString(r),r.ReadInt64(),r.ReadInt32()));
    public static byte[] Encode(EntityDeltaMessage m) => Write(w => { WriteString(w,m.Scene); w.Write(m.ServerTick); WriteEntities(w,m.Entities); WriteEntities(w,m.Despawns); });
    public static EntityDeltaMessage DecodeEntityDelta(byte[] p) => Read(p,r => new EntityDeltaMessage(ReadString(r),r.ReadInt64(),ReadEntities(r),ReadEntities(r)));
    public static byte[] Encode(ReadyMessage m) => Write(w=>{WriteString(w,m.Scene);WriteString(w,m.RegistryDigest);});
    public static ReadyMessage DecodeReady(byte[] p) => Read(p,r=>new ReadyMessage(ReadString(r),ReadString(r)));
    public static byte[] Encode(ProtocolErrorMessage m) => Write(w=>{WriteString(w,m.Code);WriteString(w,m.Detail);});
    public static ProtocolErrorMessage DecodeError(byte[] p) => Read(p,r=>new ProtocolErrorMessage(ReadString(r),ReadString(r)));
    public static byte[] Encode(InventoryStateMessage m)=>Write(w=>{w.Write(m.Value);w.Write(m.Persistent);w.Write(m.Revision);WriteString(w,m.Name);w.Write(m.X);w.Write(m.Y);w.Write(m.Z);w.Write(m.InventoryType);if(m.Slots.Length>256)throw new InvalidOperationException("Too many inventory slots.");w.Write(m.Slots.Length);foreach(var s in m.Slots){WriteString(w,s.Type);w.Write(s.Amount);w.Write(s.Durability);w.Write(s.Quality);w.Write(s.Recipe);}});
    public static InventoryStateMessage DecodeInventoryState(byte[] p)=>Read(p,r=>{var value=r.ReadUInt64();var persistent=r.ReadBoolean();var revision=r.ReadUInt64();var name=ReadString(r);var x=r.ReadSingle();var y=r.ReadSingle();var z=r.ReadSingle();var inventoryType=r.ReadInt32();var count=ReadCount(r,256);var slots=new InventorySlotWire[count];for(var i=0;i<count;i++)slots[i]=new InventorySlotWire(ReadString(r),r.ReadInt32(),r.ReadSingle(),r.ReadInt32(),r.ReadBoolean());return new InventoryStateMessage(value,persistent,revision,name,x,y,z,inventoryType,slots);});
    public static byte[] Encode(PlayerPoseMessage m)=>Write(w=>{w.Write(m.PlayerId);w.Write(m.Sequence);WriteString(w,m.Scene);w.Write(m.X);w.Write(m.Y);w.Write(m.Z);w.Write(m.Qx);w.Write(m.Qy);w.Write(m.Qz);w.Write(m.Qw);w.Write(m.MaxHealth);w.Write(m.Flags);WriteString(w,m.TorsoClip);w.Write(m.TorsoFrame);WriteString(w,m.LegsClip);w.Write(m.LegsFrame);});
    public static PlayerPoseMessage DecodePlayerPose(byte[] p)=>Read(p,r=>new PlayerPoseMessage(r.ReadInt32(),r.ReadUInt32(),ReadString(r),r.ReadSingle(),r.ReadSingle(),r.ReadSingle(),r.ReadSingle(),r.ReadSingle(),r.ReadSingle(),r.ReadSingle(),r.ReadSingle(),r.ReadByte(),ReadString(r),r.ReadInt32(),ReadString(r),r.ReadInt32()));
    public static byte[] Encode(PlayerHealthMessage m)=>Write(w=>{w.Write(m.PlayerId);w.Write(m.Health);w.Write(m.MaxHealth);w.Write(m.Downed);});
    public static PlayerHealthMessage DecodePlayerHealth(byte[] p)=>Read(p,r=>new PlayerHealthMessage(r.ReadInt32(),r.ReadSingle(),r.ReadSingle(),r.ReadBoolean()));
    public static byte[] Encode(RescueRequestMessage m)=>Write(w=>{w.Write(m.PlayerId);w.Write(m.Cancel);});
    public static RescueRequestMessage DecodeRescueRequest(byte[] p)=>Read(p,r=>new RescueRequestMessage(r.ReadInt32(),r.ReadBoolean()));
    public static byte[] Encode(RescueProgressMessage m)=>Write(w=>{w.Write(m.TargetId);w.Write(m.RescuerId);w.Write(m.Progress);w.Write(m.Active);});
    public static RescueProgressMessage DecodeRescueProgress(byte[] p)=>Read(p,r=>new RescueProgressMessage(r.ReadInt32(),r.ReadInt32(),r.ReadSingle(),r.ReadBoolean()));
    public static byte[] Encode(AllDownedMessage m)=>Array.Empty<byte>();
    public static AllDownedMessage DecodeAllDowned(byte[] p)=>Read(p,r=>new AllDownedMessage());
    public static byte[] Encode(ActionRequestMessage m)=>Write(w=>{RequireActionId(m.RequestId);w.Write(m.RequestId.ToByteArray());w.Write(m.PlayerId);w.Write((byte)m.Kind);w.Write(m.TargetValue);w.Write(m.TargetPersistent);w.Write(m.ExpectedRevision);WriteBytes(w,m.Payload,ActionPayloadMax);});
    public static ActionRequestMessage DecodeActionRequest(byte[] p)=>Read(p,r=>{var id=new Guid(ReadExact(r,16));RequireActionId(id);var player=r.ReadInt32();var kind=ReadActionKind(r);return new ActionRequestMessage(id,player,kind,r.ReadUInt64(),r.ReadBoolean(),r.ReadUInt64(),ReadBytes(r,ActionPayloadMax));});
    public static byte[] Encode(ActionResultMessage m)=>Write(w=>{RequireActionId(m.RequestId);w.Write(m.RequestId.ToByteArray());w.Write((byte)m.Kind);w.Write(m.TargetValue);w.Write(m.TargetPersistent);w.Write(m.Revision);WriteBytes(w,m.Payload,ActionPayloadMax);});
    public static ActionResultMessage DecodeActionResult(byte[] p)=>Read(p,r=>{var id=new Guid(ReadExact(r,16));RequireActionId(id);var kind=ReadActionKind(r);return new ActionResultMessage(id,kind,r.ReadUInt64(),r.ReadBoolean(),r.ReadUInt64(),ReadBytes(r,ActionPayloadMax));});
    public static byte[] Encode(ActionRejectedMessage m)=>Write(w=>{RequireActionId(m.RequestId);w.Write(m.RequestId.ToByteArray());w.Write((byte)m.Kind);w.Write(m.TargetValue);w.Write(m.TargetPersistent);w.Write(m.CurrentRevision);WriteString(w,m.ErrorCode);});
    public static ActionRejectedMessage DecodeActionRejected(byte[] p)=>Read(p,r=>{var id=new Guid(ReadExact(r,16));RequireActionId(id);var kind=ReadActionKind(r);return new ActionRejectedMessage(id,kind,r.ReadUInt64(),r.ReadBoolean(),r.ReadUInt64(),ReadString(r));});
    public static byte[] Encode(PickupResultPayload m)=>Write(w=>{WriteString(w,m.ItemType);w.Write(m.Amount);w.Write(m.Durability);w.Write(m.Quality);w.Write(m.Recipe);});
    public static PickupResultPayload DecodePickupResult(byte[] p)=>Read(p,r=>new PickupResultPayload(ReadString(r),r.ReadInt32(),r.ReadSingle(),r.ReadInt32(),r.ReadBoolean()));
    public static byte[] Encode(ContainerTakePayload m)=>Write(w=>{w.Write(m.SlotIndex);w.Write(m.Amount);});
    public static ContainerTakePayload DecodeContainerTake(byte[] p)=>Read(p,r=>new ContainerTakePayload(r.ReadInt32(),r.ReadInt32()));
    public static byte[] Encode(ContainerPutPayload m)=>Write(w=>{w.Write(m.Hotbar);w.Write(m.SlotIndex);w.Write(m.DestinationSlotIndex);w.Write(m.Amount);});
    public static ContainerPutPayload DecodeContainerPut(byte[] p)=>Read(p,r=>new ContainerPutPayload(r.ReadBoolean(),r.ReadInt32(),r.ReadInt32(),r.ReadInt32()));
    public static byte[] Encode(PlayerInventoryStatePayload m)=>Write(w=>{WriteInventorySlots(w,m.Backpack);WriteInventorySlots(w,m.Hotbar);});
    public static PlayerInventoryStatePayload DecodePlayerInventoryState(byte[] p)=>Read(p,r=>new PlayerInventoryStatePayload(ReadInventorySlots(r),ReadInventorySlots(r)));
    public static byte[] Encode(GuestProfileMessage m)=>Write(w=>{WriteBytes(w,Encode(m.Inventory),GuestProfileMax);w.Write(m.X);w.Write(m.Y);w.Write(m.Z);w.Write(m.Day);w.Write(m.JoinCount);w.Write(m.Health);w.Write(m.MaxHealth);w.Write(m.Downed);});
    public static GuestProfileMessage DecodeGuestProfile(byte[] p)=>Read(p,r=>{var inventory=DecodePlayerInventoryState(ReadBytes(r,GuestProfileMax));return new GuestProfileMessage(inventory,r.ReadSingle(),r.ReadSingle(),r.ReadSingle(),r.ReadInt32(),r.ReadInt32(),r.ReadSingle(),r.ReadSingle(),r.ReadBoolean());});
    public static byte[] Encode(GuestProfileRecord m)=>Write(w=>{w.Write((byte)GuestProfileFormatVersion);WriteString(w,m.GuestKey);w.Write(m.Day);w.Write(m.JoinCount);w.Write(m.X);w.Write(m.Y);w.Write(m.Z);WriteInventorySlots(w,m.Backpack);WriteInventorySlots(w,m.Hotbar);w.Write(m.LastSeenUtcTicks);});
    public static GuestProfileRecord DecodeGuestProfileRecord(byte[] p)=>Read(p,r=>{var version=r.ReadByte();if(version!=GuestProfileFormatVersion)throw new InvalidDataException("Unsupported guest profile format version.");var key=ReadString(r);var day=r.ReadInt32();var joins=r.ReadInt32();var x=r.ReadSingle();var y=r.ReadSingle();var z=r.ReadSingle();var backpack=ReadInventorySlots(r);var hotbar=ReadInventorySlots(r);var seen=r.ReadInt64();return new GuestProfileRecord(key,day,joins,x,y,z,backpack,hotbar,seen);});
    public static byte[] Encode(AttackPayload m)=>Write(w=>{w.Write(m.AttackKind);w.Write(m.FromHotbar);w.Write(m.SlotIndex);w.Write(m.DirX);w.Write(m.DirZ);w.Write(m.PosX);w.Write(m.PosZ);});
    public static AttackPayload DecodeAttack(byte[] p)=>Read(p,r=>{var kind=r.ReadByte();if(kind!=1&&kind!=2)throw new InvalidDataException("Unknown melee attack kind.");var fromHotbar=r.ReadBoolean();var slot=r.ReadInt32();if(slot<0||slot>255)throw new InvalidDataException("Attack slot index exceeds limit.");return new AttackPayload(kind,fromHotbar,slot,r.ReadSingle(),r.ReadSingle(),r.ReadSingle(),r.ReadSingle());});
    public static byte[] Encode(InteractPayload m)=>Write(w=>w.Write(m.ValueA));
    public static InteractPayload DecodeInteract(byte[] p)=>Read(p,r=>new InteractPayload(r.ReadInt32()));
    private const int RuntimeInitialStateMax = 64*1024;
    public static byte[] Encode(RuntimeEntitySpawnMessage m)=>Write(w=>{w.Write(m.RuntimeEntityId);w.Write((byte)m.Kind);WriteString(w,m.PrototypeId);WriteString(w,m.Scene);w.Write(m.X);w.Write(m.Y);w.Write(m.Z);w.Write(m.Qx);w.Write(m.Qy);w.Write(m.Qz);w.Write(m.Qw);WriteBytes(w,m.InitialState,RuntimeInitialStateMax);w.Write(m.ServerTick);});
    public static RuntimeEntitySpawnMessage DecodeRuntimeEntitySpawn(byte[] p)=>Read(p,r=>{var id=r.ReadUInt64();if(id==0)throw new InvalidDataException("Runtime entity id must not be zero.");var kind=ReadRuntimeEntityKind(r);return new RuntimeEntitySpawnMessage(id,kind,ReadString(r),ReadString(r),r.ReadSingle(),r.ReadSingle(),r.ReadSingle(),r.ReadSingle(),r.ReadSingle(),r.ReadSingle(),r.ReadSingle(),ReadBytes(r,RuntimeInitialStateMax),r.ReadInt64());});
    public static byte[] Encode(RuntimeEntityDespawnMessage m)=>Write(w=>{w.Write(m.RuntimeEntityId);w.Write(m.ServerTick);w.Write((byte)m.Reason);});
    public static RuntimeEntityDespawnMessage DecodeRuntimeEntityDespawn(byte[] p)=>Read(p,r=>{var id=r.ReadUInt64();if(id==0)throw new InvalidDataException("Runtime entity id must not be zero.");return new RuntimeEntityDespawnMessage(id,r.ReadInt64(),ReadRuntimeEntityDespawnReason(r));});
    public static byte[] Encode(SceneChangeMessage m)=>Write(w=>WriteString(w,m.Scene));
    public static SceneChangeMessage DecodeSceneChange(byte[] p)=>Read(p,r=>new SceneChangeMessage(ReadString(r)));
    private const int SnapshotMax = 256*1024;
    private const int ActionPayloadMax = 64*1024;
    private const int GuestProfileMax = 1024*1024;
    private const byte GuestProfileFormatVersion = 1;
    private static void WriteInventorySlots(BinaryWriter w,InventorySlotWire[] slots){if(slots.Length>256)throw new InvalidOperationException("Too many player inventory slots.");w.Write(slots.Length);foreach(var s in slots){WriteString(w,s.Type);w.Write(s.Amount);w.Write(s.Durability);w.Write(s.Quality);w.Write(s.Recipe);}}
    private static InventorySlotWire[] ReadInventorySlots(BinaryReader r){var count=ReadCount(r,256);var slots=new InventorySlotWire[count];for(var i=0;i<count;i++)slots[i]=new InventorySlotWire(ReadString(r),r.ReadInt32(),r.ReadSingle(),r.ReadInt32(),r.ReadBoolean());return slots;}
    private static void WriteEntities(BinaryWriter w, EntityStateWire[] a) { if(a.Length>MaxEntities) throw new InvalidOperationException("Too many entities."); w.Write(a.Length); foreach(var e in a){w.Write(e.Value);w.Write(e.Persistent);w.Write(e.Kind);w.Write(e.X);w.Write(e.Y);w.Write(e.Z);w.Write(e.Qx);w.Write(e.Qy);w.Write(e.Qz);w.Write(e.Qw);w.Write(e.Health);w.Write(e.StateA);w.Write(e.StateB);w.Write(e.Flags);WriteString(w,e.Animation);w.Write(e.Frame);w.Write(e.Revision);} }
    private static EntityStateWire[] ReadEntities(BinaryReader r) { var n=ReadCount(r,MaxEntities); var a=new EntityStateWire[n]; for(var i=0;i<n;i++) a[i]=new EntityStateWire(r.ReadUInt64(),r.ReadBoolean(),r.ReadByte(),r.ReadSingle(),r.ReadSingle(),r.ReadSingle(),r.ReadSingle(),r.ReadSingle(),r.ReadSingle(),r.ReadSingle(),r.ReadSingle(),r.ReadInt32(),r.ReadInt32(),r.ReadByte(),ReadString(r),r.ReadInt32(),r.ReadUInt64()); return a; }
    private static byte[] Write(Action<BinaryWriter> a){using var s=new MemoryStream();using var w=new BinaryWriter(s,Encoding.UTF8);a(w);return s.ToArray();}
    private static T Read<T>(byte[] p,Func<BinaryReader,T> a){using var s=new MemoryStream(p??Array.Empty<byte>());using var r=new BinaryReader(s,Encoding.UTF8);var v=a(r);if(s.Position!=s.Length)throw new InvalidDataException("Trailing protocol payload.");return v;}
    private static void WriteString(BinaryWriter w,string s){var b=Encoding.UTF8.GetBytes(s??string.Empty);if(b.Length>MaxString)throw new InvalidOperationException("String too long.");w.Write(b.Length);w.Write(b);}
    private static string ReadString(BinaryReader r){var n=ReadCount(r,MaxString);return Encoding.UTF8.GetString(ReadExact(r,n));}
    private static void WriteBytes(BinaryWriter w,byte[] b,int max){if(b.Length>max)throw new InvalidOperationException("Byte field too large.");w.Write(b.Length);w.Write(b);}
    private static byte[] ReadBytes(BinaryReader r,int max){var n=ReadCount(r,max);return ReadExact(r,n);}
    private static int ReadCount(BinaryReader r,int max){var n=r.ReadInt32();if(n<0||n>max)throw new InvalidDataException("Count exceeds limit.");return n;}
    private static byte[] ReadExact(BinaryReader r,int n){var b=r.ReadBytes(n);if(b.Length!=n)throw new EndOfStreamException();return b;}
    private static ActionKindWire ReadActionKind(BinaryReader r){var kind=(ActionKindWire)r.ReadByte();if(!Enum.IsDefined(typeof(ActionKindWire),kind))throw new InvalidDataException("Unknown action kind.");return kind;}
    private static RuntimeEntityKind ReadRuntimeEntityKind(BinaryReader r){var kind=(RuntimeEntityKind)r.ReadByte();if(!Enum.IsDefined(typeof(RuntimeEntityKind),kind)||kind==RuntimeEntityKind.Unknown)throw new InvalidDataException("Unknown runtime entity kind.");return kind;}
    private static RuntimeEntityDespawnReason ReadRuntimeEntityDespawnReason(BinaryReader r){var reason=(RuntimeEntityDespawnReason)r.ReadByte();if(!Enum.IsDefined(typeof(RuntimeEntityDespawnReason),reason)||reason==RuntimeEntityDespawnReason.Unknown)throw new InvalidDataException("Unknown runtime entity despawn reason.");return reason;}
    private static void RequireActionId(Guid id){if(id==Guid.Empty)throw new InvalidDataException("Action request id must not be empty.");}
}
