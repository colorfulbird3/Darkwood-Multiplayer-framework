using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using DarkwoodMultiplayerFramework.Actions;
using DarkwoodMultiplayerFramework.Core;
using DarkwoodMultiplayerFramework.Entities;
using DarkwoodMultiplayerFramework.Network;
using DarkwoodMultiplayerFramework.Protocol;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace DarkwoodMultiplayerFramework.DarkwoodAdapter;

/// <summary>Darkwood-specific boundary. It owns scene/player discovery while protocol logic stays in src modules.</summary>
public sealed class DarkwoodAdapterRuntime : MonoBehaviour
{
    public static DarkwoodAdapterRuntime? Instance { get; private set; }
    public ConnectionState State { get; private set; } = ConnectionState.Disconnected;
    public string CurrentScene => SceneManager.GetActiveScene().name;
    public Player? LocalPlayer => Player.Instance;
    public int RegistryCount => registry?.Count ?? 0;
    public string RegistryDigest { get; private set; } = string.Empty;
    public bool IsHost => hostSession != null;
    public bool IsClient => clientSession != null;
    public bool HandshakeComplete => clientSession != null && clientSession.HandshakeComplete;
    public string LastNetworkError => clientSession?.LastError ?? string.Empty;
    public string ActiveClientSaveDirectory { get; private set; } = string.Empty;
    public event Action<string>? SceneChanged;
    public event Action<ConnectionState>? StateChanged;

    private readonly DarkwoodEntityScanner scanner = new DarkwoodEntityScanner();
    private EntityRegistry<Component>? registry;
    private ManualLogSource? log;
    private string lastScene = string.Empty;
    private bool registryDirty = true;
    private HostHandshakeSession? hostSession;
    private ClientHandshakeSession? clientSession;
    private string telepathyPath = string.Empty;
    private ConfigEntry<string>? addressConfig;
    private ConfigEntry<int>? portConfig;
    private bool f1WasDown;
    private bool f2WasDown;
    private bool f3WasDown;
    private readonly DarkwoodEntityReplication replication = new DarkwoodEntityReplication();
    private readonly DarkwoodRemotePlayers remotePlayers = new DarkwoodRemotePlayers();
    private readonly Dictionary<int, Queue<OutgoingPacket>> outgoing = new Dictionary<int, Queue<OutgoingPacket>>();
    private readonly HashSet<int> readyPeers = new HashSet<int>();
    private ChunkTransferAssembler? incomingSave;
    private SaveTransferManifest incomingSaveManifest;
    private ChunkTransferAssembler? incomingSnapshot;
    private WorldSnapshotManifest incomingSnapshotManifest;
    private long serverTick;
    private float nextDelta;
    private float nextInventory;
    private float nextPose;
    private uint poseSequence;
    private string sessionError = string.Empty;
    private readonly Dictionary<int,Guid> sentSaves = new Dictionary<int,Guid>();
    private readonly Dictionary<int,Guid> sentSnapshots = new Dictionary<int,Guid>();
    private readonly ActionIdempotencyCache actionCache = new ActionIdempotencyCache();
    // The protocol response is cached alongside the abstract result so a retry can
    // be answered byte-for-byte without applying the game mutation a second time.
    private readonly Dictionary<Guid,ActionResultMessage> cachedActionResults = new Dictionary<Guid,ActionResultMessage>();
    private readonly Dictionary<Guid,ActionRejectedMessage> cachedActionRejections = new Dictionary<Guid,ActionRejectedMessage>();
    private readonly Dictionary<Guid,int> cachedActionOwners = new Dictionary<Guid,int>();
    private readonly Dictionary<Guid,ActionRequestMessage> pendingActions = new Dictionary<Guid,ActionRequestMessage>();
    private readonly Dictionary<int,Vector3> remotePlayerPositions = new Dictionary<int,Vector3>();
    private readonly Dictionary<int,DarkwoodPlayerInventoryShadow> remoteInventories = new Dictionary<int,DarkwoodPlayerInventoryShadow>();
    private long acceptedActions;
    private long rejectedActions;
    private long duplicateActions;

    private sealed class OutgoingPacket { public ProtocolMessageType Type; public byte[] Payload = Array.Empty<byte>(); }

    private ProtocolIdentity Identity => new ProtocolIdentity(1, Plugin.Version, Application.version, 1, 1);

    public void Initialize(ManualLogSource logger)
    {
        log = logger;
        log.LogInfo("Standalone 0.8 Darkwood adapter initialized.");
    }

    public void Configure(ConfigFile config)
    {
        addressConfig = config.Bind("Network", "Address", "127.0.0.1", "Host address used by F2 client connect.");
        portConfig = config.Bind("Network", "Port", 17777, "TCP port used by the standalone DMF 0.8 transport.");
        telepathyPath = Path.Combine(Paths.PluginPath, "Telepathy.dll");
        log?.LogInfo($"Standalone transport configured: {telepathyPath}, TCP {portConfig.Value}.");
    }

    public void StartHost()
    {
        StopNetwork();
        hostSession = new HostHandshakeSession(new TelepathyServerTransport(telepathyPath), Identity);
        hostSession.PeerAccepted += OnPeerAccepted;
        hostSession.PeerRejected += OnPeerRejected;
        hostSession.PeerDisconnected += OnPeerDisconnected;
        hostSession.MessageReceived += OnHostMessage;
        hostSession.Start(Port);
        log?.LogInfo($"Standalone DMF host listening on TCP {Port}.");
    }

    public void ConnectClient()
    {
        StopNetwork();
        clientSession = new ClientHandshakeSession(new TelepathyClientTransport(telepathyPath), Identity);
        clientSession.HandshakeSucceeded += OnHandshakeSucceeded;
        clientSession.HandshakeFailed += OnHandshakeFailed;
        clientSession.MessageReceived += OnClientMessage;
        clientSession.Connect(addressConfig?.Value ?? "127.0.0.1", Port);
        log?.LogInfo($"Standalone DMF client connecting to {addressConfig?.Value ?? "127.0.0.1"}:{Port}.");
    }

    public void StopNetwork()
    {
        if (clientSession != null) { clientSession.Dispose(); clientSession = null; }
        if (hostSession != null) { hostSession.Dispose(); hostSession = null; }
        outgoing.Clear(); readyPeers.Clear(); sentSaves.Clear(); sentSnapshots.Clear(); pendingActions.Clear();remotePlayerPositions.Clear();remoteInventories.Clear();actionCache.Clear();cachedActionResults.Clear();cachedActionRejections.Clear();cachedActionOwners.Clear();incomingSave=null; incomingSnapshot=null; replication.RestoreSimulation(); remotePlayers.Clear(); ActiveClientSaveDirectory=string.Empty; sessionError=string.Empty;
        SetState(ConnectionState.Disconnected);
    }

    public void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;
        lastScene = CurrentScene;
        SceneManager.sceneLoaded += OnSceneLoaded;
    }

    public void Update()
    {
        PollHotkeys();
        try { hostSession?.Tick(); clientSession?.Tick(); }
        catch (Exception error) { FailClient("TRANSPORT_TICK_FAILED",error); }
        PumpOutgoing();
        if(clientSession?.Session.Lifecycle.State==ConnectionState.Ready)replication.Interpolate(Time.unscaledDeltaTime*12f);
        remotePlayers.Tick();
        var scene = CurrentScene;
        if (!string.Equals(scene, lastScene, StringComparison.Ordinal)) MarkSceneChanged(scene);

        SetState(DetectState());
        if (registryDirty && IsNetworkConnected() && Player.Instance != null)
        {
            RebuildRegistry();
            SetState(DetectState());
        }
        if (hostSession != null && readyPeers.Count>0 && !registryDirty && Time.unscaledTime>=nextDelta)
        {
            nextDelta=Time.unscaledTime+(1f/15f); serverTick++; var delta=replication.CaptureDeltas();
            if(delta.Length>0){var payload=ReplicationProtocolCodec.Encode(new EntityDeltaMessage(CurrentScene,serverTick,delta,Array.Empty<EntityStateWire>()));foreach(var peer in readyPeers.ToArray())Queue(peer,ProtocolMessageType.EntityDelta,payload);}
        }
        if(hostSession!=null&&readyPeers.Count>0&&!registryDirty&&Time.unscaledTime>=nextInventory){nextInventory=Time.unscaledTime+.5f;foreach(var inventory in replication.CaptureInventoryDeltas()){var payload=ReplicationProtocolCodec.Encode(inventory);foreach(var peer in readyPeers.ToArray())Queue(peer,ProtocolMessageType.InventoryState,payload);}}
        if(hostSession!=null&&readyPeers.Count>0&&Time.unscaledTime>=nextPose){nextPose=Time.unscaledTime+(1f/15f);SendHostPose();}
        else if(clientSession?.Session.Lifecycle.State==ConnectionState.Ready&&Time.unscaledTime>=nextPose){nextPose=Time.unscaledTime+(1f/15f);SendLocalPose();}
    }

    public void OnDestroy()
    {
        SceneManager.sceneLoaded -= OnSceneLoaded;
        StopNetwork();
        if (ReferenceEquals(Instance, this)) Instance = null;
    }

    private void OnSceneLoaded(Scene scene, LoadSceneMode mode) => MarkSceneChanged(scene.name);

    private void MarkSceneChanged(string scene)
    {
        lastScene = scene;
        registryDirty = true;
        registry = null;
        RegistryDigest = string.Empty;
        SceneChanged?.Invoke(scene);
    }

    private void RebuildRegistry()
    {
        var next = new EntityRegistry<Component>();
        var collisions = 0;
        foreach (var component in scanner.ScanScene())
        {
            var id = scanner.ToPersistentId(component);
            try { next.Register(id, component); }
            catch (InvalidOperationException)
            {
                collisions++;
                log?.LogWarning($"Duplicate Darkwood entity id {id} for {component.GetType().Name} at {component.transform.name}.");
            }
        }
        registry = next;
        replication.Rebuild(scanner);
        RegistryDigest = next.ComputeDigest();
        registryDirty = false;
        log?.LogInfo($"Darkwood adapter registry ready: {next.Count} entities, {collisions} collisions, digest {RegistryDigest}, scene {CurrentScene}.");
    }

    private void SetState(ConnectionState next)
    {
        if (State == next) return;
        State = next;
        log?.LogInfo($"Darkwood adapter state: {next}.");
        StateChanged?.Invoke(next);
    }

    private ConnectionState DetectState()
    {
        if (hostSession == null && clientSession == null) return ConnectionState.Disconnected;
        if (hostSession != null) return hostSession.IsActive ? ConnectionState.Ready : ConnectionState.Connecting;
        if (clientSession == null) return ConnectionState.Disconnected;
        if (sessionError.Length>0||clientSession.Session.Lifecycle.State == ConnectionState.Failed) return ConnectionState.Failed;
        if (!clientSession.HandshakeComplete) return clientSession.Session.Lifecycle.State;
        if (Player.Instance == null) return ConnectionState.LoadingSave;
        if (registryDirty) return ConnectionState.BuildingRegistry;
        return ConnectionState.Ready;
    }

    private bool IsNetworkConnected() => hostSession?.IsActive == true || clientSession?.HandshakeComplete == true;
    private ushort Port => (ushort)Mathf.Clamp(portConfig?.Value ?? 17777, 1, 65535);

    private void PollHotkeys()
    {
        var f1 = Input.GetKey(KeyCode.F1); var f2 = Input.GetKey(KeyCode.F2); var f3 = Input.GetKey(KeyCode.F3);
        try
        {
            if (f1 && !f1WasDown) StartHost();
            if (f2 && !f2WasDown) ConnectClient();
            if (f3 && !f3WasDown) { StopNetwork(); log?.LogInfo("Standalone DMF session stopped."); }
        }
        catch (Exception error) { log?.LogError($"Standalone network command failed: {error}"); }
        finally { f1WasDown = f1; f2WasDown = f2; f3WasDown = f3; }
    }

    private void OnHandshakeSucceeded()
    {
        log?.LogInfo($"Standalone DMF handshake complete. PeerId={clientSession?.PeerId}, HostSessionId={clientSession?.HostSessionId}.");
        clientSession?.Send(ProtocolMessageType.SaveTransferRequest,ReplicationProtocolCodec.Encode(new SaveTransferRequest(Guid.NewGuid())));
    }
    private void OnHandshakeFailed(string error) => log?.LogError($"Standalone DMF handshake failed: {error}");
    private void OnPeerAccepted(int connectionId) => log?.LogInfo($"Standalone DMF accepted peer connection {connectionId}.");
    private void OnPeerRejected(int connectionId, string error) => log?.LogWarning($"Standalone DMF rejected peer connection {connectionId}: {error}");
    private void OnPeerDisconnected(int connectionId){outgoing.Remove(connectionId);readyPeers.Remove(connectionId);sentSaves.Remove(connectionId);sentSnapshots.Remove(connectionId);remotePlayerPositions.Remove(connectionId);remoteInventories.Remove(connectionId);remotePlayers.Remove(connectionId);}

    private void OnHostMessage(int peer,ProtocolEnvelope envelope)
    {
        try
        {
            if(envelope.MessageType==ProtocolMessageType.SaveTransferRequest){ReplicationProtocolCodec.DecodeSaveTransferRequest(envelope.Payload);PrepareSave(peer);}
            else if(envelope.MessageType==ProtocolMessageType.SaveTransferApplied){var applied=ReplicationProtocolCodec.DecodeSaveTransferApplied(envelope.Payload);if(!sentSaves.TryGetValue(peer,out var expected)||expected!=applied.TransferId)throw new InvalidDataException("Save acknowledgement does not match active transfer.");log?.LogInfo($"Peer {peer} installed verified save {applied.TransferId}.");}
            else if(envelope.MessageType==ProtocolMessageType.Ready){var ready=ReplicationProtocolCodec.DecodeReady(envelope.Payload);PrepareSnapshot(peer,ready);}
            else if(envelope.MessageType==ProtocolMessageType.WorldSnapshotApplied){var applied=ReplicationProtocolCodec.DecodeWorldSnapshotApplied(envelope.Payload);if(!sentSnapshots.TryGetValue(peer,out var expected)||expected!=applied.SnapshotId||applied.Scene!=CurrentScene||applied.RegistryDigest!=RegistryDigest)throw new InvalidDataException("Snapshot acknowledgement does not match active snapshot.");remoteInventories[peer]=DarkwoodPlayerInventoryShadow.CaptureInitial();readyPeers.Add(peer);Queue(peer,ProtocolMessageType.Ready,ReplicationProtocolCodec.Encode(new ReadyMessage(CurrentScene,RegistryDigest)));SendHostPose(peer);log?.LogInfo($"Peer {peer} READY after applying snapshot {applied.SnapshotId}, {applied.EntityCount} entities.");}
            else if(envelope.MessageType==ProtocolMessageType.PlayerPose){var pose=ReplicationProtocolCodec.DecodePlayerPose(envelope.Payload);if(!readyPeers.Contains(peer)||pose.Scene!=CurrentScene)return;pose=new PlayerPoseMessage(peer,pose.Sequence,CurrentScene,pose.X,pose.Y,pose.Z,pose.Qx,pose.Qy,pose.Qz,pose.Qw,pose.Flags,pose.TorsoClip,pose.TorsoFrame,pose.LegsClip,pose.LegsFrame);remotePlayerPositions[peer]=new Vector3(pose.X,pose.Y,pose.Z);remotePlayers.Apply(pose,0);var payload=ReplicationProtocolCodec.Encode(pose);foreach(var readyPeer in readyPeers.ToArray())if(readyPeer!=peer)Queue(readyPeer,ProtocolMessageType.PlayerPose,payload);}
            else if(envelope.MessageType==ProtocolMessageType.ActionRequest)HandleActionRequest(peer,ReplicationProtocolCodec.DecodeActionRequest(envelope.Payload));
        }
        catch(Exception error){log?.LogError($"Host protocol handler failed for peer {peer}: {error}");Queue(peer,ProtocolMessageType.Error,ReplicationProtocolCodec.Encode(new ProtocolErrorMessage("HOST_HANDLER_FAILED",error.Message)));}
    }

    private void OnClientMessage(ProtocolEnvelope envelope)
    {
        try
        {
            if(envelope.MessageType==ProtocolMessageType.SaveTransferManifest){incomingSaveManifest=ReplicationProtocolCodec.DecodeSaveTransferManifest(envelope.Payload);incomingSave=new ChunkTransferAssembler(incomingSaveManifest.TransferId,incomingSaveManifest.TotalBytes,incomingSaveManifest.ChunkCount,incomingSaveManifest.Sha256);log?.LogInfo($"Receiving save {incomingSaveManifest.TotalBytes} bytes in {incomingSaveManifest.ChunkCount} chunks.");}
            else if(envelope.MessageType==ProtocolMessageType.SaveTransferChunk)ReceiveSaveChunk(ReplicationProtocolCodec.DecodeSaveTransferChunk(envelope.Payload));
            else if(envelope.MessageType==ProtocolMessageType.WorldSnapshotManifest){incomingSnapshotManifest=ReplicationProtocolCodec.DecodeWorldSnapshotManifest(envelope.Payload);incomingSnapshot=new ChunkTransferAssembler(incomingSnapshotManifest.SnapshotId,incomingSnapshotManifest.TotalBytes,incomingSnapshotManifest.ChunkCount,incomingSnapshotManifest.Sha256,64L*1024*1024);}
            else if(envelope.MessageType==ProtocolMessageType.WorldSnapshotChunk)ReceiveSnapshotChunk(ReplicationProtocolCodec.DecodeWorldSnapshotChunk(envelope.Payload));
            else if(envelope.MessageType==ProtocolMessageType.EntityDelta){var delta=ReplicationProtocolCodec.DecodeEntityDelta(envelope.Payload);if(clientSession?.Session.Lifecycle.State==ConnectionState.Ready&&delta.Scene==CurrentScene){replication.Apply(delta.Entities,false);replication.ApplyDespawns(delta.Despawns);}}
            else if(envelope.MessageType==ProtocolMessageType.InventoryState)replication.Apply(ReplicationProtocolCodec.DecodeInventoryState(envelope.Payload));
            else if(envelope.MessageType==ProtocolMessageType.PlayerPose)remotePlayers.Apply(ReplicationProtocolCodec.DecodePlayerPose(envelope.Payload),clientSession?.PeerId??-1);
            else if(envelope.MessageType==ProtocolMessageType.ActionResult)HandleActionResult(ReplicationProtocolCodec.DecodeActionResult(envelope.Payload));
            else if(envelope.MessageType==ProtocolMessageType.ActionRejected)HandleActionRejected(ReplicationProtocolCodec.DecodeActionRejected(envelope.Payload));
            else if(envelope.MessageType==ProtocolMessageType.Ready){var ready=ReplicationProtocolCodec.DecodeReady(envelope.Payload);if(ready.Scene!=CurrentScene||ready.RegistryDigest!=RegistryDigest)throw new InvalidDataException("Host ready acknowledgement does not match local world.");if(clientSession?.Session.Lifecycle.State==ConnectionState.ApplyingSnapshot)clientSession.Session.Lifecycle.MoveTo(ConnectionState.Ready);log?.LogInfo($"Standalone join READY in {ready.Scene}, registry {ready.RegistryDigest}.");}
            else if(envelope.MessageType==ProtocolMessageType.Error){var error=ReplicationProtocolCodec.DecodeError(envelope.Payload);log?.LogError($"Host error {error.Code}: {error.Detail}");}
        }
        catch(Exception error){FailClient("CLIENT_PROTOCOL_FAILED",error);}
    }

    private void PrepareSave(int peer)
    {
        var profile=global::Core.currentProfile;if(profile==null||!profile.Active)throw new InvalidOperationException("Host has no active save profile.");var manager=Singleton<SaveManager>.Instance;if(manager==null)throw new InvalidOperationException("SaveManager is unavailable.");manager.Save(false,true,true,false,false,true,false);manager.saveProfilesFile();var bundle=DarkwoodSaveBundle.Build(manager.baseSaveDirectory,profile.id);var id=Guid.NewGuid();sentSaves[peer]=id;var chunks=ChunkTransferAssembler.Split(bundle);Queue(peer,ProtocolMessageType.SaveTransferManifest,ReplicationProtocolCodec.Encode(new SaveTransferManifest(id,profile.id,bundle.LongLength,chunks.Length,ChunkTransferAssembler.Hash(bundle),$"Day {profile.day}, chapter {profile.chapter}")));for(var i=0;i<chunks.Length;i++)Queue(peer,ProtocolMessageType.SaveTransferChunk,ReplicationProtocolCodec.Encode(new SaveTransferChunk(id,i,chunks.Length,chunks[i],ChunkTransferAssembler.Hash(chunks[i]))));log?.LogInfo($"Prepared live save for peer {peer}: transfer {id}, {bundle.Length} bytes, {chunks.Length} chunks.");
    }

    private void ReceiveSaveChunk(SaveTransferChunk chunk)
    {
        if(incomingSave==null)throw new InvalidDataException("Save chunk arrived before manifest.");incomingSave.Add(chunk.TransferId,chunk.Index,chunk.Total,chunk.Data,chunk.Hash);if(!incomingSave.IsComplete)return;var data=incomingSave.Build();incomingSave=null;InstallDownloadedSave(data,incomingSaveManifest.ProfileId);clientSession?.Send(ProtocolMessageType.SaveTransferApplied,ReplicationProtocolCodec.Encode(new SaveTransferApplied(incomingSaveManifest.TransferId,incomingSaveManifest.ProfileId,"isolated-client-save")));StartCoroutine(LoadDownloadedSave(incomingSaveManifest.ProfileId));
    }

    private void InstallDownloadedSave(byte[] data,int profile)
    {
        var key=HostKey();var root=Path.Combine(Paths.BepInExRootPath,"DarkwoodMPClientSaves",key);var target=Path.Combine(root,"1_4Save");var staging=Path.Combine(root,".incoming-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(staging);try{var extracted=DarkwoodSaveBundle.Extract(data,staging);if(extracted!=profile)throw new InvalidDataException("Downloaded profile id mismatch.");Directory.CreateDirectory(root);if(Directory.Exists(target))Directory.Move(target,Path.Combine(root,"previous-"+DateTime.UtcNow.ToString("yyyyMMddHHmmssfff")));Directory.Move(staging,target);}catch{try{if(Directory.Exists(staging))Directory.Delete(staging,true);}catch{}throw;}ActiveClientSaveDirectory=target;log?.LogInfo("Installed downloaded save into isolated directory: "+target);
    }

    private IEnumerator LoadDownloadedSave(int profileId)
    {
        yield return null;try{if(!global::Core.mainMenu)throw new InvalidOperationException("Client must connect from the main menu to load the host save.");var manager=Singleton<SaveManager>.Instance;if(manager==null)throw new InvalidOperationException("SaveManager is unavailable.");var state=manager.loadGameProfiles();if(state?.profiles==null)throw new InvalidDataException("Downloaded profile metadata is unavailable.");var profile=state.profiles.FirstOrDefault(p=>p!=null&&p.id==profileId&&p.Active);if(profile==null)throw new InvalidDataException("Downloaded profile metadata is unavailable.");global::Core.profiles=state.profiles;global::Core.currentProfile=profile;manager.updateFilePaths();clientSession?.Session.Lifecycle.MoveTo(ConnectionState.LoadingSave);manager.onFinishedLoading=(saveDelegate)Delegate.Remove(manager.onFinishedLoading,new saveDelegate(OnDownloadedSaveFinished));manager.onFinishedLoading=(saveDelegate)Delegate.Combine(manager.onFinishedLoading,new saveDelegate(OnDownloadedSaveFinished));Singleton<UI>.Instance.StartCoroutine(Singleton<UI>.Instance.initLoadGame());}catch(Exception error){FailClient("SAVE_LOAD_FAILED",error);}
    }

    private void OnDownloadedSaveFinished()
    {
        var manager=Singleton<SaveManager>.Instance;if(manager!=null)manager.onFinishedLoading=(saveDelegate)Delegate.Remove(manager.onFinishedLoading,new saveDelegate(OnDownloadedSaveFinished));if(clientSession?.Session.Lifecycle.State==ConnectionState.LoadingSave)clientSession.Session.Lifecycle.MoveTo(ConnectionState.BuildingRegistry);registryDirty=true;StartCoroutine(WaitForRegistryThenReady());
    }

    private IEnumerator WaitForRegistryThenReady()
    {
        var deadline=Time.unscaledTime+60f;while((registryDirty||Player.Instance==null)&&Time.unscaledTime<deadline)yield return null;try{if(registryDirty||Player.Instance==null)throw new InvalidOperationException("Entity registry did not become ready within 60 seconds.");clientSession?.Send(ProtocolMessageType.Ready,ReplicationProtocolCodec.Encode(new ReadyMessage(CurrentScene,RegistryDigest)));if(clientSession?.Session.Lifecycle.State==ConnectionState.BuildingRegistry)clientSession.Session.Lifecycle.MoveTo(ConnectionState.ApplyingSnapshot);}catch(Exception error){FailClient("REGISTRY_BUILD_FAILED",error);}
    }

    private void PrepareSnapshot(int peer,ReadyMessage ready)
    {
        if(registryDirty)RebuildRegistry();if(!string.Equals(ready.Scene,CurrentScene,StringComparison.Ordinal)||!string.Equals(ready.RegistryDigest,RegistryDigest,StringComparison.Ordinal)){Queue(peer,ProtocolMessageType.Error,ReplicationProtocolCodec.Encode(new ProtocolErrorMessage("REGISTRY_DIGEST_MISMATCH",$"host={RegistryDigest};client={ready.RegistryDigest}")));return;}var entities=replication.Snapshot();var inventories=replication.CaptureInventorySnapshot();var state=DarkwoodWorldSnapshotCodec.Encode(CurrentScene,RegistryDigest,serverTick,entities,inventories);var id=Guid.NewGuid();sentSnapshots[peer]=id;var chunks=ChunkTransferAssembler.Split(state);Queue(peer,ProtocolMessageType.WorldSnapshotManifest,ReplicationProtocolCodec.Encode(new WorldSnapshotManifest(id,state.LongLength,chunks.Length,ChunkTransferAssembler.Hash(state),CurrentScene,RegistryDigest,serverTick)));for(var i=0;i<chunks.Length;i++)Queue(peer,ProtocolMessageType.WorldSnapshotChunk,ReplicationProtocolCodec.Encode(new WorldSnapshotChunk(id,i,chunks.Length,chunks[i],ChunkTransferAssembler.Hash(chunks[i]))));log?.LogInfo($"Prepared world snapshot {id} for peer {peer}: {entities.Length} entities, {inventories.Length} inventories, {state.Length} bytes, registry {RegistryDigest}.");
    }

    private void ReceiveSnapshotChunk(WorldSnapshotChunk chunk)
    {
        if(incomingSnapshot==null)throw new InvalidDataException("World snapshot chunk arrived before manifest.");incomingSnapshot.Add(chunk.SnapshotId,chunk.Index,chunk.Total,chunk.Data,chunk.Hash);if(!incomingSnapshot.IsComplete)return;var bytes=incomingSnapshot.Build();incomingSnapshot=null;var snapshot=DarkwoodWorldSnapshotCodec.Decode(bytes);if(snapshot.Scene!=CurrentScene||snapshot.Scene!=incomingSnapshotManifest.Scene)throw new InvalidDataException("World snapshot scene mismatch.");if(snapshot.RegistryDigest!=RegistryDigest||snapshot.RegistryDigest!=incomingSnapshotManifest.RegistryDigest)throw new InvalidDataException($"Registry digest mismatch: local {RegistryDigest}, host {incomingSnapshotManifest.RegistryDigest}.");if(snapshot.ServerTick!=incomingSnapshotManifest.ServerTick)throw new InvalidDataException("World snapshot tick mismatch.");replication.Apply(snapshot.Entities,true);foreach(var inventory in snapshot.Inventories)replication.Apply(inventory);clientSession?.Send(ProtocolMessageType.WorldSnapshotApplied,ReplicationProtocolCodec.Encode(new WorldSnapshotApplied(incomingSnapshotManifest.SnapshotId,snapshot.Scene,snapshot.RegistryDigest,snapshot.ServerTick,snapshot.Entities.Length)));log?.LogInfo($"World snapshot applied: {snapshot.Entities.Length} entities, {snapshot.Inventories.Length} inventories, tick {snapshot.ServerTick}; awaiting host Ready.");
    }

    public bool TryRequestPickup(Item item)
    {
        if(clientSession?.Session.Lifecycle.State!=ConnectionState.Ready||item==null)return false;
        if(!replication.TryGetId(item,out var id)||!replication.TryGetState(id,out var state)){log?.LogWarning("Pickup was not sent because the target has no registered EntityId.");return true;}
        var request=new ActionRequestMessage(Guid.NewGuid(),clientSession.PeerId,ActionKindWire.Pickup,id.Value,id.IsPersistent,state.Revision,Array.Empty<byte>());
        pendingActions[request.RequestId]=request;
        clientSession.Send(ProtocolMessageType.ActionRequest,ReplicationProtocolCodec.Encode(request));
        log?.LogInfo($"Pickup request {request.RequestId} sent for {id} revision {state.Revision}.");
        return true;
    }

    private void HandleActionRequest(int peer,ActionRequestMessage request)
    {
        if(request.RequestId==Guid.Empty)return;
        if(request.PlayerId!=peer){RejectAction(peer,request,"PLAYER_ID_MISMATCH",0);return;}
        if(!readyPeers.Contains(peer)){RejectAction(peer,request,"PEER_NOT_READY",0);return;}
        if(actionCache.TryGet(request.RequestId,out var cached))
        {
            if(!cachedActionOwners.TryGetValue(request.RequestId,out var owner)||owner!=peer)
            {
                Queue(peer,ProtocolMessageType.ActionRejected,ReplicationProtocolCodec.Encode(new ActionRejectedMessage(request.RequestId,request.Kind,request.TargetValue,request.TargetPersistent,0,"REQUEST_ID_COLLISION")));
                return;
            }
            duplicateActions++;SendCachedAction(peer,request,cached);return;
        }
        if(request.Kind!=ActionKindWire.Pickup){RejectAction(peer,request,"UNSUPPORTED_ACTION",0);return;}
        var id=new EntityId(request.TargetValue,request.TargetPersistent);
        if(!replication.TryGetComponent(id,out var component)||!(component is Item item)){RejectAction(peer,request,"ENTITY_NOT_FOUND",0);return;}
        if(!replication.TryGetState(id,out var state)){RejectAction(peer,request,"ENTITY_STATE_MISSING",0);return;}
        if(!ActionValidation.RevisionMatches(state.Revision,request.ExpectedRevision)){RejectAction(peer,request,"STALE_REVISION",state.Revision);return;}
        if(!item.gameObject.activeSelf||item.destroyed||!item.isDroppedItem){RejectAction(peer,request,"NOT_PICKABLE",state.Revision);return;}
        if(!remotePlayerPositions.TryGetValue(peer,out var position)){RejectAction(peer,request,"PLAYER_POSE_MISSING",state.Revision);return;}
        if(!ActionValidation.WithinDistance(position.x,position.y,position.z,item.transform.position.x,item.transform.position.y,item.transform.position.z,4.5f)){RejectAction(peer,request,"TOO_FAR",state.Revision);return;}
        var droppedInventory=DarkwoodDroppedItemAccessor.GetInventory(item);
        if(droppedInventory==null||droppedInventory.slots==null||droppedInventory.slots.Count==0||InvItemClass.isNull(droppedInventory.slots[0].invItem)){RejectAction(peer,request,"ITEM_EMPTY",state.Revision);return;}
        if(!remoteInventories.TryGetValue(peer,out var shadow)){RejectAction(peer,request,"PLAYER_INVENTORY_MISSING",state.Revision);return;}
        var source=droppedInventory.slots[0].invItem;var pickup=new PickupResultPayload(source.type,source.amount,source.durability,(int)source.modifierQuality,source.isRecipe);
        if(!shadow.CanAdd(source)){RejectAction(peer,request,"INVENTORY_FULL",state.Revision);return;}
        // The remote player's inventory is represented by a host-side shadow until
        // the Inventory Transaction protocol is introduced. Never mutate Host's
        // local Player inventory while applying a remote request.
        shadow.Add(source);
        droppedInventory.slots[0].removeItem();
        droppedInventory.refreshItems();
        var despawn=replication.MarkDespawned(id);serverTick++;
        var result=new ActionResultMessage(request.RequestId,request.Kind,id.Value,id.IsPersistent,despawn.Revision,ReplicationProtocolCodec.Encode(pickup));
        RemoveEvictedAction(actionCache.Store(new NetworkActionResult(request.RequestId,true,new StateVersion(despawn.Revision),string.Empty)));acceptedActions++;
        cachedActionResults[request.RequestId]=result;
        cachedActionOwners[request.RequestId]=peer;
        Queue(peer,ProtocolMessageType.ActionResult,ReplicationProtocolCodec.Encode(result));
        var delta=ReplicationProtocolCodec.Encode(new EntityDeltaMessage(CurrentScene,serverTick,Array.Empty<EntityStateWire>(),new[]{despawn}));foreach(var readyPeer in readyPeers.ToArray())Queue(readyPeer,ProtocolMessageType.EntityDelta,delta);
        log?.LogInfo($"Pickup accepted {request.RequestId}: peer {peer}, {pickup.ItemType} x{pickup.Amount}, target {id}, revision {despawn.Revision}.");
    }

    private void RejectAction(int peer,ActionRequestMessage request,string error,ulong revision)
    {
        var result=new NetworkActionResult(request.RequestId,false,new StateVersion(revision),error);RemoveEvictedAction(actionCache.Store(result));rejectedActions++;
        var rejected=new ActionRejectedMessage(request.RequestId,request.Kind,request.TargetValue,request.TargetPersistent,revision,error);cachedActionRejections[request.RequestId]=rejected;
        cachedActionOwners[request.RequestId]=peer;
        Queue(peer,ProtocolMessageType.ActionRejected,ReplicationProtocolCodec.Encode(rejected));
        log?.LogWarning($"Pickup rejected {request.RequestId}: peer {peer}, {error}, revision {revision}.");
    }

    private void SendCachedAction(int peer,ActionRequestMessage request,NetworkActionResult cached)
    {
        if(cached.Accepted && cachedActionResults.TryGetValue(request.RequestId,out var accepted))Queue(peer,ProtocolMessageType.ActionResult,ReplicationProtocolCodec.Encode(accepted));
        else if(!cached.Accepted && cachedActionRejections.TryGetValue(request.RequestId,out var rejected))Queue(peer,ProtocolMessageType.ActionRejected,ReplicationProtocolCodec.Encode(rejected));
        else Queue(peer,ProtocolMessageType.ActionRejected,ReplicationProtocolCodec.Encode(new ActionRejectedMessage(request.RequestId,request.Kind,request.TargetValue,request.TargetPersistent,cached.Version.Value,cached.Accepted?"CACHED_RESPONSE_MISSING":cached.ErrorCode)));
    }

    private void RemoveEvictedAction(Guid requestId)
    {
        if(requestId==Guid.Empty)return;
        cachedActionResults.Remove(requestId);
        cachedActionRejections.Remove(requestId);
        cachedActionOwners.Remove(requestId);
    }

    private void HandleActionResult(ActionResultMessage result)
    {
        if(!pendingActions.Remove(result.RequestId))return;
        if(result.Kind!=ActionKindWire.Pickup)return;
        if(result.Payload.Length>0){var pickup=ReplicationProtocolCodec.DecodePickupResult(result.Payload);var player=Player.Instance;if(player?.Inventory==null)throw new InvalidOperationException("Player inventory is unavailable for pickup result.");var added=player.Inventory.addItemTypeToPlayer(pickup.ItemType,pickup.Amount);if(InvItemClass.isNull(added))throw new InvalidOperationException("Host accepted pickup but client inventory has no space.");added.durability=pickup.Durability;added.modifierQuality=(InvItem.ModifierQuality)pickup.Quality;added.isRecipe=pickup.Recipe;added.refresh();player.refreshRecipes();}
        log?.LogInfo($"Pickup result applied {result.RequestId}, target {result.TargetValue:X16}, revision {result.Revision}.");
    }

    private void HandleActionRejected(ActionRejectedMessage rejected)
    {
        if(!pendingActions.Remove(rejected.RequestId))return;
        log?.LogWarning($"Pickup request {rejected.RequestId} rejected: {rejected.ErrorCode}, host revision {rejected.CurrentRevision}.");
        Player.Instance?.displayMessage("Multiplayer pickup rejected: "+rejected.ErrorCode);
    }

    private void Queue(int peer,ProtocolMessageType type,byte[] payload){if(!outgoing.TryGetValue(peer,out var queue))outgoing[peer]=queue=new Queue<OutgoingPacket>();queue.Enqueue(new OutgoingPacket{Type=type,Payload=payload});}
    private void PumpOutgoing(){if(hostSession==null)return;foreach(var peer in outgoing.Keys.ToArray()){var queue=outgoing[peer];var limit=2;while(queue.Count>0&&limit-->0){var p=queue.Dequeue();hostSession.SendMessage(peer,p.Type,p.Payload);}if(queue.Count==0)outgoing.Remove(peer);}}
    private void SendLocalPose(){if(!DarkwoodPlayerAdapter.TryCapture(out var p)||clientSession==null)return;clientSession.Send(ProtocolMessageType.PlayerPose,ReplicationProtocolCodec.Encode(new PlayerPoseMessage(clientSession.PeerId,++poseSequence,p.Scene,p.Position.x,p.Position.y,p.Position.z,p.Rotation.x,p.Rotation.y,p.Rotation.z,p.Rotation.w,p.Flags,p.TorsoClip,p.TorsoFrame,p.LegsClip,p.LegsFrame)));}
    private void SendHostPose(int peer){if(!DarkwoodPlayerAdapter.TryCapture(out var p))return;Queue(peer,ProtocolMessageType.PlayerPose,ReplicationProtocolCodec.Encode(new PlayerPoseMessage(0,++poseSequence,p.Scene,p.Position.x,p.Position.y,p.Position.z,p.Rotation.x,p.Rotation.y,p.Rotation.z,p.Rotation.w,p.Flags,p.TorsoClip,p.TorsoFrame,p.LegsClip,p.LegsFrame)));}
    private void SendHostPose(){foreach(var peer in readyPeers.ToArray())SendHostPose(peer);}
    private void FailClient(string code,Exception error){if(sessionError.Length>0)return;sessionError=code+": "+error.Message;log?.LogError($"Standalone DMF session failed [{code}]: {error}");try{clientSession?.Fail(sessionError);}catch{}SetState(ConnectionState.Failed);}
    private string HostKey(){using var sha=System.Security.Cryptography.SHA256.Create();var value=(addressConfig?.Value??"host")+":"+Port;return BitConverter.ToString(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(value)),0,8).Replace("-","");}
}
