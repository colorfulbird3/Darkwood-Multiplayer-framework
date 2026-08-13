using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using DarkwoodMultiplayerFramework.Core;
using DarkwoodMultiplayerFramework.Actions;
using DarkwoodMultiplayerFramework.Entities;
using DarkwoodMultiplayerFramework.Network;
using DarkwoodMultiplayerFramework.Protocol;
using DarkwoodMultiplayerFramework.Snapshots;

var tests = new (string Name, Action Run)[]
{
    ("compatible handshake", CompatibleHandshake),
    ("game version mismatch", GameVersionMismatch),
    ("protocol envelope roundtrip", EnvelopeRoundtrip),
    ("protocol envelope bad magic", EnvelopeBadMagic),
    ("protocol envelope truncated", EnvelopeTruncated),
    ("client hello roundtrip", ClientHelloRoundtrip),
    ("save protocol roundtrip", SaveProtocolRoundtrip),
    ("chunk transfer reorder", ChunkTransferReorder),
    ("chunk transfer corrupt", ChunkTransferCorrupt),
    ("entity delta roundtrip", EntityDeltaRoundtrip),
    ("inventory state roundtrip", InventoryStateRoundtrip),
    ("player pose roundtrip", PlayerPoseRoundtrip),
    ("action request roundtrip", ActionRequestRoundtrip),
    ("action result roundtrip", ActionResultRoundtrip),
    ("action rejected roundtrip", ActionRejectedRoundtrip),
    ("pickup result roundtrip", PickupResultRoundtrip),
    ("container take roundtrip", ContainerTakeRoundtrip),
    ("container put roundtrip", ContainerPutRoundtrip),
    ("player inventory state roundtrip", PlayerInventoryStateRoundtrip),
    ("action empty request id", ActionEmptyRequestId),
    ("action unknown kind", ActionUnknownKind),
    ("action idempotency", ActionIdempotency),
    ("action stale revision", ActionStaleRevision),
    ("action distance boundary", ActionDistanceBoundary),
    ("save applied roundtrip", SaveAppliedRoundtrip),
    ("snapshot applied roundtrip", SnapshotAppliedRoundtrip),
    ("world snapshot wire roundtrip", WorldSnapshotWireRoundtrip),
    ("world snapshot wire corruption", WorldSnapshotWireCorruption),
    ("telepathy loopback handshake", TelepathyLoopbackHandshake),
    ("telepathy 128 KiB payload", TelepathyLargePayload),
    ("telepathy loopback rejection", TelepathyLoopbackRejection),
    ("registry digest", RegistryDigest),
    ("snapshot reorder", SnapshotReorder),
    ("snapshot phase mismatch", SnapshotPhaseMismatch),
    ("snapshot hash mismatch", SnapshotHashMismatch),
    ("attack payload roundtrip", AttackPayloadRoundtrip),
    ("attack kind invalid", AttackKindInvalid),
    ("attack slot index invalid", AttackSlotInvalid),
    ("interact payload roundtrip", InteractPayloadRoundtrip),
    ("attack action roundtrip", AttackActionRoundtrip),
    ("door interact action roundtrip", DoorInteractActionRoundtrip),
    ("item activate action roundtrip", ItemActivateActionRoundtrip),
    ("framework version mismatch", FrameworkVersionMismatch),
    ("client hello guest key", ClientHelloGuestKey),
    ("guest profile message roundtrip", GuestProfileMessageRoundtrip),
    ("guest profile record roundtrip", GuestProfileRecordRoundtrip),
    ("guest profile record version", GuestProfileRecordVersion),
    ("session full rejection", SessionFullRejection),
    ("session capacity", SessionCapacity),
    ("guest key loopback", GuestKeyLoopback)
};
var failed = 0;
foreach (var test in tests)
{
    try { test.Run(); Console.WriteLine("PASS " + test.Name); }
    catch (Exception error) { failed++; Console.WriteLine("FAIL " + test.Name + ": " + error.Message); }
}
return failed == 0 ? 0 : 1;

static ProtocolIdentity Identity() => new(ProtocolVersions.Framework, "darkwood-build");
static void CompatibleHandshake() => Require(HandshakeValidator.Validate(Identity(), Identity()).Accepted);
static void GameVersionMismatch() { var result=HandshakeValidator.Validate(Identity(), new ProtocolIdentity(ProtocolVersions.Framework, "other-build")); Require(!result.Accepted && result.ErrorCode=="INCOMPATIBLE_GAME_BUILD"); }
static void EnvelopeRoundtrip()
{
    var sessionId=Guid.NewGuid(); var payload=Encoding.UTF8.GetBytes("hello");
    var decoded=ProtocolEnvelopeCodec.Decode(new ArraySegment<byte>(ProtocolEnvelopeCodec.Encode(new ProtocolEnvelope(1,ProtocolMessageType.ClientHello,ProtocolFlags.Reliable,7,sessionId,payload))));
    Require(decoded.ProtocolVersion==1 && decoded.MessageType==ProtocolMessageType.ClientHello && decoded.Flags==ProtocolFlags.Reliable && decoded.Sequence==7 && decoded.SessionId==sessionId && Encoding.UTF8.GetString(decoded.Payload)=="hello");
}
static void EnvelopeBadMagic()
{
    var packet=ProtocolEnvelopeCodec.Encode(new ProtocolEnvelope(1,ProtocolMessageType.ClientHello,ProtocolFlags.Reliable,1,Guid.NewGuid(),Array.Empty<byte>())); packet[0]^=0xff;
    ExpectFailure(()=>ProtocolEnvelopeCodec.Decode(new ArraySegment<byte>(packet)));
}
static void EnvelopeTruncated()
{
    var packet=ProtocolEnvelopeCodec.Encode(new ProtocolEnvelope(1,ProtocolMessageType.ClientHello,ProtocolFlags.Reliable,1,Guid.NewGuid(),new byte[]{1,2,3}));
    ExpectFailure(()=>ProtocolEnvelopeCodec.Decode(new ArraySegment<byte>(packet,0,ProtocolEnvelope.HeaderSize-1)));
}
static void ClientHelloRoundtrip()
{
    var decoded=HandshakeProtocolCodec.DecodeClientHello(HandshakeProtocolCodec.Encode(new ClientHello(Identity(),"夜色猎人")));
    Require(decoded.Identity.FrameworkVersion==ProtocolVersions.Framework && decoded.Identity.GameVersion=="darkwood-build" && decoded.GuestKey=="夜色猎人");
}
static void ClientHelloGuestKey()
{
    var decoded=HandshakeProtocolCodec.DecodeClientHello(HandshakeProtocolCodec.Encode(new ClientHello(Identity(),string.Empty)));
    Require(decoded.GuestKey.Length==0);
    ExpectFailure(()=>HandshakeProtocolCodec.Encode(new ClientHello(Identity(),new string('汉',40))));
}
static void SaveProtocolRoundtrip()
{
    var id=Guid.NewGuid();var hash=new byte[32];hash[0]=9;var manifest=ReplicationProtocolCodec.DecodeSaveTransferManifest(ReplicationProtocolCodec.Encode(new SaveTransferManifest(id,2,1234,3,hash,"day 4")));Require(manifest.TransferId==id&&manifest.ProfileId==2&&manifest.TotalBytes==1234&&manifest.ChunkCount==3&&manifest.Sha256[0]==9&&manifest.Description=="day 4");var data=new byte[]{3,4,5};var chunk=ReplicationProtocolCodec.DecodeSaveTransferChunk(ReplicationProtocolCodec.Encode(new SaveTransferChunk(id,1,3,data,ChunkTransferAssembler.Hash(data))));Require(chunk.Index==1&&chunk.Total==3&&chunk.Data[0]==3&&chunk.Hash.Length==32);
}
static void ChunkTransferReorder()
{
    var data=Encoding.UTF8.GetBytes(new string('x',300000)+"done");var chunks=ChunkTransferAssembler.Split(data);var id=Guid.NewGuid();var assembler=new ChunkTransferAssembler(id,data.Length,chunks.Length,ChunkTransferAssembler.Hash(data));for(var i=chunks.Length-1;i>=0;i--)assembler.Add(id,i,chunks.Length,chunks[i],ChunkTransferAssembler.Hash(chunks[i]));Require(Encoding.UTF8.GetString(assembler.Build()).EndsWith("done"));
}
static void ChunkTransferCorrupt()
{
    var data=Encoding.UTF8.GetBytes("save");var id=Guid.NewGuid();var assembler=new ChunkTransferAssembler(id,data.Length,1,ChunkTransferAssembler.Hash(data));ExpectFailure(()=>assembler.Add(id,0,1,data,new byte[32]));
}
static void EntityDeltaRoundtrip()
{
    var entity=new EntityStateWire(77,true,2,1,2,3,0,0,0,1,50,4,5,3,"open",7,9);var decoded=ReplicationProtocolCodec.DecodeEntityDelta(ReplicationProtocolCodec.Encode(new EntityDeltaMessage("scene",42,new[]{entity},Array.Empty<EntityStateWire>())));Require(decoded.Scene=="scene"&&decoded.ServerTick==42&&decoded.Entities.Length==1&&decoded.Entities[0].Value==77&&decoded.Entities[0].Revision==9);
}
static void InventoryStateRoundtrip()
{
    var decoded=ReplicationProtocolCodec.DecodeInventoryState(ReplicationProtocolCodec.Encode(new InventoryStateMessage(8,true,4,"Wardrobe",1.5f,2.5f,3.5f,7,new[]{new InventorySlotWire("Wood",3,.5f,2,false)})));Require(decoded.Value==8&&decoded.Persistent&&decoded.Revision==4&&decoded.Name=="Wardrobe"&&Math.Abs(decoded.X-1.5f)<.001f&&Math.Abs(decoded.Y-2.5f)<.001f&&Math.Abs(decoded.Z-3.5f)<.001f&&decoded.InventoryType==7&&decoded.Slots.Length==1&&decoded.Slots[0].Type=="Wood"&&decoded.Slots[0].Amount==3&&Math.Abs(decoded.Slots[0].Durability-.5f)<.001f);
}
static void PlayerPoseRoundtrip()
{
    var decoded=ReplicationProtocolCodec.DecodePlayerPose(ReplicationProtocolCodec.Encode(new PlayerPoseMessage(3,9,"forest",1,2,3,0,0,0,1,5,"walk",4,"legs",2)));Require(decoded.PlayerId==3&&decoded.Sequence==9&&decoded.Scene=="forest"&&decoded.X==1&&decoded.Flags==5&&decoded.TorsoClip=="walk"&&decoded.LegsFrame==2);
}
static void ActionRequestRoundtrip()
{
    var id=Guid.NewGuid();var decoded=ReplicationProtocolCodec.DecodeActionRequest(ReplicationProtocolCodec.Encode(new ActionRequestMessage(id,3,ActionKindWire.Pickup,77,true,9,new byte[]{1,2,3})));Require(decoded.RequestId==id&&decoded.PlayerId==3&&decoded.Kind==ActionKindWire.Pickup&&decoded.TargetValue==77&&decoded.TargetPersistent&&decoded.ExpectedRevision==9&&decoded.Payload.Length==3);
}
static void ActionResultRoundtrip()
{
    var id=Guid.NewGuid();var decoded=ReplicationProtocolCodec.DecodeActionResult(ReplicationProtocolCodec.Encode(new ActionResultMessage(id,ActionKindWire.Pickup,88,false,10,new byte[]{4,5})));Require(decoded.RequestId==id&&decoded.Kind==ActionKindWire.Pickup&&decoded.TargetValue==88&&!decoded.TargetPersistent&&decoded.Revision==10&&decoded.Payload[1]==5);
}
static void ActionRejectedRoundtrip()
{
    var id=Guid.NewGuid();var decoded=ReplicationProtocolCodec.DecodeActionRejected(ReplicationProtocolCodec.Encode(new ActionRejectedMessage(id,ActionKindWire.Pickup,9,true,11,"STALE_REVISION")));Require(decoded.RequestId==id&&decoded.CurrentRevision==11&&decoded.ErrorCode=="STALE_REVISION");
}
static void PickupResultRoundtrip()
{
    var decoded=ReplicationProtocolCodec.DecodePickupResult(ReplicationProtocolCodec.Encode(new PickupResultPayload("Wood",2,.75f,3,true)));Require(decoded.ItemType=="Wood"&&decoded.Amount==2&&Math.Abs(decoded.Durability-.75f)<.001f&&decoded.Quality==3&&decoded.Recipe);
}
static void ContainerTakeRoundtrip()
{
    var decoded=ReplicationProtocolCodec.DecodeContainerTake(ReplicationProtocolCodec.Encode(new ContainerTakePayload(7,-1)));Require(decoded.SlotIndex==7&&decoded.Amount==-1);
    var request=ReplicationProtocolCodec.DecodeActionRequest(ReplicationProtocolCodec.Encode(new ActionRequestMessage(Guid.NewGuid(),2,ActionKindWire.ContainerTake,99,true,4,ReplicationProtocolCodec.Encode(new ContainerTakePayload(3,1)))));Require(request.Kind==ActionKindWire.ContainerTake&&ReplicationProtocolCodec.DecodeContainerTake(request.Payload).SlotIndex==3);
}
static void ContainerPutRoundtrip()
{
    var decoded=ReplicationProtocolCodec.DecodeContainerPut(ReplicationProtocolCodec.Encode(new ContainerPutPayload(true,4,9,-1)));Require(decoded.Hotbar&&decoded.SlotIndex==4&&decoded.DestinationSlotIndex==9&&decoded.Amount==-1);
    var request=ReplicationProtocolCodec.DecodeActionRequest(ReplicationProtocolCodec.Encode(new ActionRequestMessage(Guid.NewGuid(),2,ActionKindWire.ContainerPut,101,true,6,ReplicationProtocolCodec.Encode(new ContainerPutPayload(false,2,5,1)))));var put=ReplicationProtocolCodec.DecodeContainerPut(request.Payload);Require(request.Kind==ActionKindWire.ContainerPut&&!put.Hotbar&&put.SlotIndex==2&&put.DestinationSlotIndex==5&&put.Amount==1);
}
static void PlayerInventoryStateRoundtrip()
{
    var payload=new PlayerInventoryStatePayload(new[]{new InventorySlotWire("Wood",2,.8f,1,false)},new[]{new InventorySlotWire("Knife",1,.4f,2,false)});
    var decoded=ReplicationProtocolCodec.DecodePlayerInventoryState(ReplicationProtocolCodec.Encode(payload));Require(decoded.Backpack.Length==1&&decoded.Backpack[0].Type=="Wood"&&decoded.Backpack[0].Amount==2&&decoded.Hotbar.Length==1&&decoded.Hotbar[0].Type=="Knife"&&Math.Abs(decoded.Hotbar[0].Durability-.4f)<.001f);
}
static void ActionEmptyRequestId()=>ExpectFailure(()=>ReplicationProtocolCodec.Encode(new ActionRequestMessage(Guid.Empty,1,ActionKindWire.Pickup,1,true,0,Array.Empty<byte>())));
static void ActionUnknownKind()
{
    var bytes=ReplicationProtocolCodec.Encode(new ActionRequestMessage(Guid.NewGuid(),1,ActionKindWire.Pickup,1,true,0,Array.Empty<byte>()));bytes[20]=255;ExpectFailure(()=>ReplicationProtocolCodec.DecodeActionRequest(bytes));
}
static void ActionIdempotency()
{
    var cache=new ActionIdempotencyCache(2);var id=Guid.NewGuid();var applies=0;if(!cache.TryGet(id,out _)){applies++;cache.Store(new NetworkActionResult(id,true,new StateVersion(4),string.Empty));}if(!cache.TryGet(id,out _)){applies++;cache.Store(new NetworkActionResult(id,true,new StateVersion(5),string.Empty));}Require(applies==1&&cache.TryGet(id,out var cached)&&cached.Version.Value==4);var id2=Guid.NewGuid();var id3=Guid.NewGuid();cache.Store(new NetworkActionResult(id2,false,new StateVersion(1),"NO"));Require(cache.Store(new NetworkActionResult(id3,true,new StateVersion(2),string.Empty))==id&&!cache.TryGet(id,out _));
}
static void ActionStaleRevision(){Require(ActionValidation.RevisionMatches(8,8)&&!ActionValidation.RevisionMatches(8,7));}
static void ActionDistanceBoundary(){Require(ActionValidation.WithinDistance(0,0,0,3,4,0,5)&&!ActionValidation.WithinDistance(0,0,0,3.01f,4,0,5));}
static void SaveAppliedRoundtrip()
{
    var id=Guid.NewGuid();var decoded=ReplicationProtocolCodec.DecodeSaveTransferApplied(ReplicationProtocolCodec.Encode(new SaveTransferApplied(id,2,"isolated")));Require(decoded.TransferId==id&&decoded.ProfileId==2&&decoded.SaveDirectory=="isolated");
}
static void SnapshotAppliedRoundtrip()
{
    var id=Guid.NewGuid();var decoded=ReplicationProtocolCodec.DecodeWorldSnapshotApplied(ReplicationProtocolCodec.Encode(new WorldSnapshotApplied(id,"forest","ABC",55,123)));Require(decoded.SnapshotId==id&&decoded.Scene=="forest"&&decoded.RegistryDigest=="ABC"&&decoded.ServerTick==55&&decoded.EntityCount==123);
}
static void WorldSnapshotWireRoundtrip()
{
    var bytes=WorldSnapshotWireCodec.Encode(new WorldSnapshotWire("forest","DIGEST",42,new[]{new byte[]{1,2}},new[]{new byte[]{3,4,5}}));
    var decoded=WorldSnapshotWireCodec.Decode(bytes);
    Require(decoded.Scene=="forest"&&decoded.RegistryDigest=="DIGEST"&&decoded.ServerTick==42&&decoded.EntityRecords.Length==1&&decoded.EntityRecords[0][1]==2&&decoded.InventoryRecords.Length==1&&decoded.InventoryRecords[0][2]==5);
}
static void WorldSnapshotWireCorruption()
{
    var bytes=WorldSnapshotWireCodec.Encode(new WorldSnapshotWire("forest","DIGEST",1,Array.Empty<byte[]>(),Array.Empty<byte[]>()));bytes[0]^=0xff;ExpectFailure(()=>WorldSnapshotWireCodec.Decode(bytes));
}
static void TelepathyLoopbackHandshake() => RunTelepathyLoopback(true);
static void TelepathyLargePayload()
{
    var telepathy=FindTelepathy();var port=FindFreePort();var peer=-1;var received=0;
    using var host=new HostHandshakeSession(new TelepathyServerTransport(telepathy),Identity());
    using var client=new ClientHandshakeSession(new TelepathyClientTransport(telepathy),Identity());
    host.PeerAccepted += id => peer=id;
    host.MessageReceived += (_,message) => received=message.Payload.Length;
    host.Start(port);client.Connect("127.0.0.1",port);
    var timeout=Stopwatch.StartNew();
    while(timeout.Elapsed<TimeSpan.FromSeconds(5)&&(!client.HandshakeComplete||peer<0)){host.Tick();client.Tick();Thread.Sleep(1);}
    Require(client.HandshakeComplete&&peer>=0);
    client.Send(ProtocolMessageType.SaveTransferChunk,new byte[128*1024+256]);
    timeout.Restart();
    while(timeout.Elapsed<TimeSpan.FromSeconds(5)&&received==0){host.Tick();client.Tick();Thread.Sleep(1);}
    Require(received==128*1024+256);
}
static void TelepathyLoopbackRejection() => RunTelepathyLoopback(false);
static void RunTelepathyLoopback(bool compatible)
{
    var telepathy=FindTelepathy(); var port=FindFreePort(); var accepted=false; var rejected=string.Empty;
    using var host=new HostHandshakeSession(new TelepathyServerTransport(telepathy),Identity());
    using var client=new ClientHandshakeSession(new TelepathyClientTransport(telepathy),compatible ? Identity() : new ProtocolIdentity("0.8.7-alpha.9","darkwood-build"));
    host.PeerAccepted += _ => accepted=true; host.PeerRejected += (_,error) => rejected=error;
    host.Start(port); client.Connect("127.0.0.1",port);
    var timeout=Stopwatch.StartNew();
    while(timeout.Elapsed < TimeSpan.FromSeconds(5))
    {
        host.Tick(); client.Tick();
        if(compatible && client.HandshakeComplete && host.ReadyPeerCount==1) break;
        if(!compatible && client.Session.Lifecycle.State==ConnectionState.Failed && rejected.Length>0) break;
        Thread.Sleep(1);
    }
    if(compatible) Require(accepted && client.HandshakeComplete && client.PeerId>=0 && client.HostSessionId==host.SessionId && client.Session.Lifecycle.State==ConnectionState.SaveTransfer && host.ReadyPeerCount==1);
    else Require(!accepted && rejected=="INCOMPATIBLE_FRAMEWORK_VERSION" && client.LastError=="INCOMPATIBLE_FRAMEWORK_VERSION" && client.Session.Lifecycle.State==ConnectionState.Failed && host.ReadyPeerCount==0);
}
static string FindTelepathy()
{
    var configured=Environment.GetEnvironmentVariable("DMF_TELEPATHY_PATH"); if(!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)) return Path.GetFullPath(configured);
    var local=Path.Combine(AppContext.BaseDirectory,"Telepathy.dll"); if(File.Exists(local)) return local;
    var directory=new DirectoryInfo(Directory.GetCurrentDirectory());
    while(directory!=null)
    {
        var candidates=new[]{Path.Combine(directory.FullName,"Payload","BepInEx","plugins","Telepathy.dll"),Path.Combine(directory.FullName,"Darkwood Multiplayer framework","Payload","BepInEx","plugins","Telepathy.dll")};
        foreach(var candidate in candidates) if(File.Exists(candidate)) return candidate;
        directory=directory.Parent;
    }
    throw new FileNotFoundException("Set DMF_TELEPATHY_PATH to run TCP loopback tests.");
}
static ushort FindFreePort()
{
    var listener=new TcpListener(IPAddress.Loopback,0); listener.Start(); var port=(ushort)((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop(); return port;
}
static void RegistryDigest() { var a=new EntityRegistry<object>(); var b=new EntityRegistry<object>(); a.Register(new EntityId(7,true),new object()); b.Register(new EntityId(7,true),new object()); Require(a.ComputeDigest()==b.ComputeDigest()); }
static void SnapshotReorder() { var id=Guid.NewGuid(); var a=Encoding.UTF8.GetBytes("hello "); var b=Encoding.UTF8.GetBytes("world"); var x=new SnapshotAssembler(); x.Add(new SnapshotChunk(id,SnapshotPhase.World,1,2,b)); x.Add(new SnapshotChunk(id,SnapshotPhase.World,0,2,a)); Require(Encoding.UTF8.GetString(x.Build())=="hello world"); }
static void SnapshotPhaseMismatch() { var id=Guid.NewGuid(); var x=new SnapshotAssembler(); x.Add(new SnapshotChunk(id,SnapshotPhase.World,0,2,new byte[]{1})); ExpectFailure(()=>x.Add(new SnapshotChunk(id,SnapshotPhase.Entities,1,2,new byte[]{2}))); }
static void SnapshotHashMismatch() { var x=new SnapshotAssembler(); ExpectFailure(()=>x.Add(new SnapshotChunk(Guid.NewGuid(),SnapshotPhase.World,0,1,new byte[]{1},new byte[32]))); }
static void AttackPayloadRoundtrip()
{
    var decoded=ReplicationProtocolCodec.DecodeAttack(ReplicationProtocolCodec.Encode(new AttackPayload(2,true,4,0.6f,-0.8f,12.5f,3.25f)));
    Require(decoded.AttackKind==2&&decoded.FromHotbar&&decoded.SlotIndex==4&&Math.Abs(decoded.DirX-0.6f)<.001f&&Math.Abs(decoded.DirZ+0.8f)<.001f&&Math.Abs(decoded.PosX-12.5f)<.001f&&Math.Abs(decoded.PosZ-3.25f)<.001f);
}
static void AttackKindInvalid()
{
    var bytes=ReplicationProtocolCodec.Encode(new AttackPayload(1,false,0,0,1,0,0));bytes[0]=9;ExpectFailure(()=>ReplicationProtocolCodec.DecodeAttack(bytes));
}
static void AttackSlotInvalid()
{
    var patched=ReplicationProtocolCodec.Encode(new AttackPayload(1,false,0,0,1,0,0));
    // Payload layout: kind(1) + fromHotbar(1) + slotIndex(4, little-endian). Overwrite the slot index with 256.
    patched[2]=0; patched[3]=1; patched[4]=0; patched[5]=0;
    ExpectFailure(()=>ReplicationProtocolCodec.DecodeAttack(patched));
}
static void InteractPayloadRoundtrip()
{
    var decoded=ReplicationProtocolCodec.DecodeInteract(ReplicationProtocolCodec.Encode(new InteractPayload(250)));
    Require(decoded.ValueA==250);
}
static void AttackActionRoundtrip()
{
    var id=Guid.NewGuid();
    var decoded=ReplicationProtocolCodec.DecodeActionRequest(ReplicationProtocolCodec.Encode(new ActionRequestMessage(id,3,ActionKindWire.Attack,0,false,0,ReplicationProtocolCodec.Encode(new AttackPayload(1,true,2,0,1,4,5)))));
    Require(decoded.Kind==ActionKindWire.Attack&&decoded.RequestId==id&&decoded.PlayerId==3&&decoded.TargetValue==0&&!decoded.TargetPersistent&&decoded.ExpectedRevision==0);
    var attack=ReplicationProtocolCodec.DecodeAttack(decoded.Payload);Require(attack.AttackKind==1&&attack.FromHotbar&&attack.SlotIndex==2);
}
static void DoorInteractActionRoundtrip()
{
    var id=Guid.NewGuid();
    var decoded=ReplicationProtocolCodec.DecodeActionRequest(ReplicationProtocolCodec.Encode(new ActionRequestMessage(id,2,ActionKindWire.DoorInteract,77,true,9,ReplicationProtocolCodec.Encode(new InteractPayload(0)))));
    Require(decoded.Kind==ActionKindWire.DoorInteract&&decoded.TargetValue==77&&decoded.TargetPersistent&&decoded.ExpectedRevision==9);
}
static void ItemActivateActionRoundtrip()
{
    var id=Guid.NewGuid();
    var decoded=ReplicationProtocolCodec.DecodeActionRequest(ReplicationProtocolCodec.Encode(new ActionRequestMessage(id,1,ActionKindWire.ItemActivate,55,true,0,Array.Empty<byte>())));
    Require(decoded.Kind==ActionKindWire.ItemActivate&&decoded.TargetValue==55&&decoded.TargetPersistent);
}
static void FrameworkVersionMismatch()
{
    var host=Identity();
    var client=new ProtocolIdentity("0.8.7-alpha.9","darkwood-build");
    var result=HandshakeValidator.Validate(host,client);
    Require(!result.Accepted && result.ErrorCode=="INCOMPATIBLE_FRAMEWORK_VERSION");
}
static void GuestProfileMessageRoundtrip()
{
    var inventory=new PlayerInventoryStatePayload(new[]{new InventorySlotWire("Wood",2,.8f,1,false)},new[]{new InventorySlotWire("Knife",1,.4f,2,false)});
    var decoded=ReplicationProtocolCodec.DecodeGuestProfile(ReplicationProtocolCodec.Encode(new GuestProfileMessage(inventory,12.5f,3.25f,-8.5f,4,2)));
    Require(decoded.Inventory.Backpack.Length==1&&decoded.Inventory.Backpack[0].Type=="Wood"&&decoded.Inventory.Hotbar[0].Type=="Knife"&&Math.Abs(decoded.X-12.5f)<.001f&&Math.Abs(decoded.Y-3.25f)<.001f&&Math.Abs(decoded.Z+8.5f)<.001f&&decoded.Day==4&&decoded.JoinCount==2);
}
static void GuestProfileRecordRoundtrip()
{
    var record=new GuestProfileRecord("夜色猎人",5,3,1.5f,2.5f,3.5f,new[]{new InventorySlotWire("Wood",2,.8f,1,false)},new[]{new InventorySlotWire("Knife",1,.4f,2,false)},638273511234567890);
    var decoded=ReplicationProtocolCodec.DecodeGuestProfileRecord(ReplicationProtocolCodec.Encode(record));
    Require(decoded.GuestKey=="夜色猎人"&&decoded.Day==5&&decoded.JoinCount==3&&Math.Abs(decoded.X-1.5f)<.001f&&decoded.Backpack.Length==1&&decoded.Backpack[0].Amount==2&&decoded.Hotbar[0].Type=="Knife"&&decoded.LastSeenUtcTicks==638273511234567890&&decoded.HasPosition);
}
static void GuestProfileRecordVersion()
{
    var bytes=ReplicationProtocolCodec.Encode(new GuestProfileRecord("guest",1,1,0,0,0,Array.Empty<InventorySlotWire>(),Array.Empty<InventorySlotWire>(),0));
    bytes[0]=9;ExpectFailure(()=>ReplicationProtocolCodec.DecodeGuestProfileRecord(bytes));
}
static void SessionFullRejection()
{
    var telepathy=FindTelepathy();var port=FindFreePort();var accepted=0;var rejected=string.Empty;
    using var host=new HostHandshakeSession(new TelepathyServerTransport(telepathy),Identity());
    using var client=new ClientHandshakeSession(new TelepathyClientTransport(telepathy),Identity());
    host.MaxPeers=0;
    host.PeerAccepted+=_=>accepted++;
    host.PeerRejected+=(_,error)=>rejected=error;
    host.Start(port);client.Connect("127.0.0.1",port);
    var timeout=Stopwatch.StartNew();
    while(timeout.Elapsed<TimeSpan.FromSeconds(5)){host.Tick();client.Tick();if(client.Session.Lifecycle.State==ConnectionState.Failed&&rejected.Length>0)break;Thread.Sleep(1);}
    Require(accepted==0&&rejected=="SESSION_FULL"&&client.LastError=="SESSION_FULL"&&client.Session.Lifecycle.State==ConnectionState.Failed&&host.ReadyPeerCount==0);
}
static void SessionCapacity()
{
    var telepathy=FindTelepathy();var port=FindFreePort();var accepted=0;var rejected=string.Empty;
    using var host=new HostHandshakeSession(new TelepathyServerTransport(telepathy),Identity());
    host.MaxPeers=1;
    host.PeerAccepted+=_=>accepted++;
    host.PeerRejected+=(_,error)=>rejected=error;
    host.Start(port);
    using var first=new ClientHandshakeSession(new TelepathyClientTransport(telepathy),Identity());
    using var second=new ClientHandshakeSession(new TelepathyClientTransport(telepathy),Identity());
    first.Connect("127.0.0.1",port);second.Connect("127.0.0.1",port);
    var timeout=Stopwatch.StartNew();
    while(timeout.Elapsed<TimeSpan.FromSeconds(5)){host.Tick();first.Tick();second.Tick();if(first.HandshakeComplete&&second.Session.Lifecycle.State==ConnectionState.Failed&&rejected=="SESSION_FULL")break;Thread.Sleep(1);}
    Require(first.HandshakeComplete&&accepted==1&&rejected=="SESSION_FULL"&&second.LastError=="SESSION_FULL"&&host.ReadyPeerCount==1);
}
static void GuestKeyLoopback()
{
    var telepathy=FindTelepathy();var port=FindFreePort();var peerId=-1;
    using var host=new HostHandshakeSession(new TelepathyServerTransport(telepathy),Identity());
    using var client=new ClientHandshakeSession(new TelepathyClientTransport(telepathy),Identity());
    host.PeerAccepted+=id=>peerId=id;
    client.GuestKey="夜色猎人";
    host.Start(port);client.Connect("127.0.0.1",port);
    var timeout=Stopwatch.StartNew();
    while(timeout.Elapsed<TimeSpan.FromSeconds(5)&&(!client.HandshakeComplete||peerId<0)){host.Tick();client.Tick();Thread.Sleep(1);}
    Require(client.HandshakeComplete&&peerId>=0&&host.TryGetPeerGuestKey(peerId,out var key)&&key=="夜色猎人");
}
static void Require(bool value) { if(!value) throw new InvalidOperationException("assertion failed"); }
static void ExpectFailure(Action action) { try { action(); } catch(Exception error) when(error is InvalidOperationException || error is InvalidDataException || error is EndOfStreamException) { return; } throw new InvalidOperationException("expected failure"); }
