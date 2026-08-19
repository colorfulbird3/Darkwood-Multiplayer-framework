using System;
using System.IO;
using System.Text;

namespace DarkwoodMultiplayerFramework.Protocol;

public readonly struct EntityStateWire
{
    public EntityStateWire(ulong value, bool persistent, byte kind, float x, float y, float z, float qx, float qy, float qz, float qw, float health, int stateA, int stateB, byte flags, string animation, int frame, ulong revision)
        : this(value, persistent, kind, x, y, z, qx, qy, qz, qw, health, stateA, stateB, flags, animation, frame, revision, 0, System.Array.Empty<byte>()) { }
    public EntityStateWire(ulong value, bool persistent, byte kind, float x, float y, float z, float qx, float qy, float qz, float qw, float health, int stateA, int stateB, byte flags, string animation, int frame, ulong revision, ushort stateSchema, byte[] extraState)
    { Value=value; Persistent=persistent; Kind=kind; X=x; Y=y; Z=z; Qx=qx; Qy=qy; Qz=qz; Qw=qw; Health=health; StateA=stateA; StateB=stateB; Flags=flags; Animation=animation ?? string.Empty; Frame=frame; Revision=revision; StateSchema=stateSchema; ExtraState=extraState ?? System.Array.Empty<byte>(); }
    public ulong Value { get; } public bool Persistent { get; } public byte Kind { get; }
    public float X { get; } public float Y { get; } public float Z { get; }
    public float Qx { get; } public float Qy { get; } public float Qz { get; } public float Qw { get; }
    public float Health { get; } public int StateA { get; } public int StateB { get; } public byte Flags { get; }
    public string Animation { get; } public int Frame { get; } public ulong Revision { get; }
    public ushort StateSchema { get; } public byte[] ExtraState { get; }
    public EntityStateWire WithSchema(ushort schema, byte[] extra) => new EntityStateWire(Value,Persistent,Kind,X,Y,Z,Qx,Qy,Qz,Qw,Health,StateA,StateB,Flags,Animation,Frame,Revision,schema,extra);
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

public static class ProtocolVersions
{
    /// <summary>Envelope framing version (ProtocolEnvelope header). Constant within the framework line.</summary>
    public const int EnvelopeProtocol = 3;
    public const string Framework = "0.8.9-beta.8";
}

public static class ReplicationProtocolCodec
{
    private const int MaxChunks = 4096, MaxEntities = 4096, MaxString = 4096, MaxHash = 64;
    private const int MaxExtraState = 4096; // 单实体 typed adapter 状态上限（trap/door/character 远小于此）
    private const int MaxBindingEntries = 20000;
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
    public static byte[] Encode(EntityBindingManifest m) => Write(w => { w.Write(m.TransferId.ToByteArray()); w.Write(m.TotalBytes); w.Write(m.ChunkCount); WriteBytes(w,m.Sha256,MaxHash); WriteString(w,m.Scene); w.Write(m.Generation); w.Write(m.EntityCount); });
    public static EntityBindingManifest DecodeEntityBindingManifest(byte[] p) => Read(p,r => new EntityBindingManifest(new Guid(ReadExact(r,16)),r.ReadInt64(),ReadCount(r,MaxChunks),ReadBytes(r,MaxHash),ReadString(r),r.ReadInt32(),r.ReadInt32()));
    public static byte[] Encode(EntityBindingChunk m) => Write(w => { w.Write(m.TransferId.ToByteArray()); w.Write(m.Index); w.Write(m.Total); WriteBytes(w,m.Hash,MaxHash); WriteBytes(w,m.Data,SnapshotMax); });
    public static EntityBindingChunk DecodeEntityBindingChunk(byte[] p) => Read(p,r => { var id=new Guid(ReadExact(r,16));var index=r.ReadInt32();var total=ReadCount(r,MaxChunks);var hash=ReadBytes(r,MaxHash);var data=ReadBytes(r,SnapshotMax);return new EntityBindingChunk(id,index,total,data,hash); });
    public static byte[] Encode(EntityBindingEntryWire[] entries) => Write(w => { w.Write(entries.Length); foreach (var m in entries) { w.Write(m.EntityValue); w.Write(m.Kind); WriteString(w,m.ComponentType); w.Write(m.SaveableUid); WriteString(w,m.RelativePath); WriteString(w,m.ObjectName); w.Write(m.X); w.Write(m.Y); w.Write(m.Z); } });
    public static EntityBindingEntryWire[] DecodeEntityBindingEntries(byte[] data) => Read(data,r => { var count=ReadCount(r,MaxBindingEntries); var result=new EntityBindingEntryWire[count]; for(var i=0;i<count;i++) result[i]=new EntityBindingEntryWire(r.ReadUInt64(),r.ReadByte(),ReadString(r),r.ReadInt64(),ReadString(r),ReadString(r),r.ReadSingle(),r.ReadSingle(),r.ReadSingle()); return result; });
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
    public static byte[] Encode(DropItemPayload m)=>Write(w=>{w.Write(m.FromHotbar);w.Write(m.SlotIndex);w.Write(m.Amount);w.Write(m.X);w.Write(m.Y);w.Write(m.Z);w.Write(m.Qx);w.Write(m.Qy);w.Write(m.Qz);w.Write(m.Qw);w.Write((byte)m.Origin);w.Write(m.ContainerValue);w.Write(m.ContainerPersistent);});
    public static DropItemPayload DecodeDropItem(byte[] p)=>Read(p,r=>new DropItemPayload(r.ReadBoolean(),r.ReadInt32(),r.ReadInt32(),r.ReadSingle(),r.ReadSingle(),r.ReadSingle(),r.ReadSingle(),r.ReadSingle(),r.ReadSingle(),r.ReadSingle(),(DropOriginWire)r.ReadByte(),r.ReadUInt64(),r.ReadBoolean()));
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
    private static void WriteEntities(BinaryWriter w, EntityStateWire[] a) { if(a.Length>MaxEntities) throw new InvalidOperationException("Too many entities."); w.Write(a.Length); foreach(var e in a){w.Write(e.Value);w.Write(e.Persistent);w.Write(e.Kind);w.Write(e.X);w.Write(e.Y);w.Write(e.Z);w.Write(e.Qx);w.Write(e.Qy);w.Write(e.Qz);w.Write(e.Qw);w.Write(e.Health);w.Write(e.StateA);w.Write(e.StateB);w.Write(e.Flags);WriteString(w,e.Animation);w.Write(e.Frame);w.Write(e.Revision);w.Write(e.StateSchema);WriteBytes(w,e.ExtraState,MaxExtraState);} }
    private static EntityStateWire[] ReadEntities(BinaryReader r) { var n=ReadCount(r,MaxEntities); var a=new EntityStateWire[n]; for(var i=0;i<n;i++) a[i]=new EntityStateWire(r.ReadUInt64(),r.ReadBoolean(),r.ReadByte(),r.ReadSingle(),r.ReadSingle(),r.ReadSingle(),r.ReadSingle(),r.ReadSingle(),r.ReadSingle(),r.ReadSingle(),r.ReadSingle(),r.ReadInt32(),r.ReadInt32(),r.ReadByte(),ReadString(r),r.ReadInt32(),r.ReadUInt64(),r.ReadUInt16(),ReadBytes(r,MaxExtraState)); return a; }
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
