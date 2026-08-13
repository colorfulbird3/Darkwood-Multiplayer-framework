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
    public string StateDisplay => StateText(State);
    public string CurrentScene => SceneManager.GetActiveScene().name;
    public Player? LocalPlayer => Player.Instance;
    public int RegistryCount => registry?.Count ?? 0;
    public string RegistryDigest { get; private set; } = string.Empty;
    public bool IsHost => hostSession != null;
    public bool IsClient => clientSession != null;
    public bool HandshakeComplete => clientSession != null && clientSession.HandshakeComplete;
    public string LastNetworkError => clientSession?.LastError ?? string.Empty;
    public string ActiveClientSaveDirectory { get; private set; } = string.Empty;
    public string ConfiguredAddress => addressConfig?.Value ?? "127.0.0.1";
    public int ConfiguredPort => portConfig?.Value ?? 17777;
    public int ConfiguredPlayerCount => playerCountConfig?.Value ?? 2;
    public int ReadyPeerCount => readyPeers.Count;
    public int LocalPeerId => clientSession?.PeerId ?? (IsHost ? 0 : -1);
    public long AcceptedActionCount => acceptedActions;
    public long RejectedActionCount => rejectedActions;
    public long DuplicateActionCount => duplicateActions;
    public bool ApplyingAuthoritativeInventory => replication.ApplyingRemote;
    public string SessionError => sessionError.Length > 0 ? sessionError : LastNetworkError;
    public string TransferProgress { get; private set; } = string.Empty;
    public event Action<string>? SceneChanged;
    public event Action<ConnectionState>? StateChanged;

    private readonly DarkwoodEntityScanner scanner = new DarkwoodEntityScanner();
    private EntityRegistry<Component>? registry;
    private ManualLogSource? log;
    private string lastScene = string.Empty;
    private bool registryDirty = true;
    private readonly HashSet<string> scaledHostInventoryKeys = new HashSet<string>(StringComparer.Ordinal);
    private bool hostLootScaleScanComplete;
    private bool hostLootScaleScanStarted;
    private Coroutine? hostLootScaleCoroutine;
    private readonly Dictionary<int,ReadyMessage> pendingSnapshotRequests = new Dictionary<int,ReadyMessage>();
    private string lootScaleLedgerPath = string.Empty;
    private HostHandshakeSession? hostSession;
    private ClientHandshakeSession? clientSession;
    private string telepathyPath = string.Empty;
    private ConfigEntry<string>? addressConfig;
    private ConfigEntry<int>? portConfig;
    private ConfigEntry<int>? playerCountConfig;
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
    private float nextInventoryDelta;
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
    private bool clientSnapshotReady;
    private bool clientRegistryRequestSent;
    private bool clientSnapshotManifestReceived;
    private float nextRegistryRequestRetry;
    private WorldSnapshotApplied? lastSnapshotApplied;
    private float nextSnapshotAckRetry;
    private int snapshotAckRetryCount;
    private readonly Dictionary<int,float> nextAttackAllowed = new Dictionary<int,float>();
    private readonly Dictionary<int,GameObject> remoteAttackAnchors = new Dictionary<int,GameObject>();
    private const float AttackCooldownSeconds = 0.35f;
    private const float AttackPoseTolerance = 2f;
    private const float MeleeReach = 1.6f;
    private const float MeleeConeDot = 0.3f;
    private const float InteractDistance = 6f;

    private sealed class OutgoingPacket
    {
        public ProtocolMessageType Type;
        public byte[] Payload = Array.Empty<byte>();
        public string TransferLabel = string.Empty;
        public int ChunkIndex = -1;
        public int ChunkCount;
        public bool IsBulk => ChunkIndex >= 0 && ChunkCount > 0;
    }

    private ProtocolIdentity Identity => new ProtocolIdentity(ProtocolVersions.Protocol, Plugin.Version, Application.version, ProtocolVersions.SaveSchema, ProtocolVersions.SnapshotSchema);

    public void Initialize(ManualLogSource logger)
    {
        log = logger;
        remotePlayers.Logger = message => log?.LogInfo(message);
        log.LogInfo("Darkwood 联机适配层已初始化（0.8）。");
    }

    public void Configure(ConfigFile config)
    {
        addressConfig = config.Bind("Network", "Address", "127.0.0.1", "Host address used by F2 client connect.");
        portConfig = config.Bind("Network", "Port", 17777, "TCP port used by the standalone DMF 0.8 transport.");
        playerCountConfig = config.Bind("Gameplay", "PlayerCount", 2, "Target player count and new shared-container loot multiplier.");
        playerCountConfig.Value = Mathf.Clamp(playerCountConfig.Value, 1, 8);
        lootScaleLedgerPath = Path.Combine(Paths.ConfigPath, "DarkwoodMultiplayerFramework.loot-scale-ledger.txt");
        LoadLootScaleLedger();
        telepathyPath = Path.Combine(Paths.PluginPath, "Telepathy.dll");
        log?.LogInfo($"联机传输已配置：{telepathyPath}，TCP 端口 {portConfig.Value}。");
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
        log?.LogInfo($"主机正在监听 TCP 端口 {Port}。");
    }

    public void ConnectClient()
    {
        StopNetwork();
        clientSession = new ClientHandshakeSession(new TelepathyClientTransport(telepathyPath), Identity);
        clientSession.HandshakeSucceeded += OnHandshakeSucceeded;
        clientSession.HandshakeFailed += OnHandshakeFailed;
        clientSession.MessageReceived += OnClientMessage;
        clientSession.Connect(addressConfig?.Value ?? "127.0.0.1", Port);
        log?.LogInfo($"客户端正在连接 {addressConfig?.Value ?? "127.0.0.1"}:{Port}。");
    }

    public bool ApplyNetworkConfiguration(string address, string portText, out string error)
    {
        error = string.Empty;
        address = (address ?? string.Empty).Trim();
        if (address.Length == 0) { error = "Host address is required."; return false; }
        if (!int.TryParse((portText ?? string.Empty).Trim(), out var port) || port < 1 || port > 65535)
        { error = "Port must be between 1 and 65535."; return false; }
        if (addressConfig == null || portConfig == null) { error = "Configuration is not ready."; return false; }
        addressConfig.Value = address;
        portConfig.Value = port;
        addressConfig.ConfigFile.Save();
        log?.LogInfo($"网络配置已保存：{address}:{port}。");
        return true;
    }

    public void SetPlayerCount(int players)
    {
        if(playerCountConfig==null)return;
        playerCountConfig.Value=Mathf.Clamp(players,1,8);
        playerCountConfig.ConfigFile.Save();
        log?.LogInfo($"联机人数及新容器战利品倍率已设置为 {playerCountConfig.Value}。");
    }

    public void StopNetwork()
    {
        if (clientSession != null) { clientSession.Dispose(); clientSession = null; }
        if (hostSession != null) { hostSession.Dispose(); hostSession = null; }
        if(hostLootScaleCoroutine!=null){StopCoroutine(hostLootScaleCoroutine);hostLootScaleCoroutine=null;}
        outgoing.Clear(); readyPeers.Clear(); sentSaves.Clear(); sentSnapshots.Clear(); pendingSnapshotRequests.Clear(); pendingActions.Clear();remotePlayerPositions.Clear();remoteInventories.Clear();actionCache.Clear();cachedActionResults.Clear();cachedActionRejections.Clear();cachedActionOwners.Clear();incomingSave=null; incomingSnapshot=null; TransferProgress=string.Empty; clientSnapshotReady=false; clientRegistryRequestSent=false; clientSnapshotManifestReceived=false; nextRegistryRequestRetry=0f; lastSnapshotApplied=null; nextSnapshotAckRetry=0f; snapshotAckRetryCount=0; nextInventoryDelta=0f; hostLootScaleScanComplete=false; hostLootScaleScanStarted=false; nextAttackAllowed.Clear(); DestroyAttackAnchors(); replication.RestoreSimulation(); remotePlayers.Clear(); ActiveClientSaveDirectory=string.Empty; sessionError=string.Empty;
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
        TrySendClientRegistryReady();
        RetrySnapshotAcknowledgement();
        if(hostSession!=null&&!registryDirty&&registry!=null)EnsureHostExistingLootScaled();
        if (hostSession != null && readyPeers.Count>0 && !registryDirty && Time.unscaledTime>=nextDelta)
        {
            nextDelta=Time.unscaledTime+(1f/15f); serverTick++; var delta=replication.CaptureDeltas();
            if(delta.Length>0){var payload=ReplicationProtocolCodec.Encode(new EntityDeltaMessage(CurrentScene,serverTick,delta,Array.Empty<EntityStateWire>()));foreach(var peer in readyPeers.ToArray())Queue(peer,ProtocolMessageType.EntityDelta,payload);}
        }
        if (hostSession != null && readyPeers.Count>0 && !registryDirty && Time.unscaledTime>=nextInventoryDelta)
        {
            nextInventoryDelta=Time.unscaledTime+0.25f;
            foreach(var inventory in replication.CaptureInventoryDeltas()) BroadcastInventory(inventory);
        }
        if(hostSession!=null&&readyPeers.Count>0&&Time.unscaledTime>=nextPose){nextPose=Time.unscaledTime+(1f/15f);SendHostPose();}
        else if(clientSession?.Session.Lifecycle.State==ConnectionState.Ready&&Time.unscaledTime>=nextPose){nextPose=Time.unscaledTime+(1f/15f);SendLocalPose();}
    }

    private void TrySendClientRegistryReady()
    {
        if (clientSnapshotReady || clientSnapshotManifestReceived || clientSession == null || !clientSession.HandshakeComplete || registryDirty || registry == null || Player.Instance == null) return;
        var lifecycle = clientSession.Session.Lifecycle;
        if (lifecycle.State == ConnectionState.LoadingSave) lifecycle.MoveTo(ConnectionState.BuildingRegistry);
        if (lifecycle.State != ConnectionState.BuildingRegistry && lifecycle.State != ConnectionState.ApplyingSnapshot) return;
        if (clientRegistryRequestSent && Time.realtimeSinceStartup < nextRegistryRequestRetry) return;
        clientRegistryRequestSent = true;
        nextRegistryRequestRetry = Time.realtimeSinceStartup + 5f;
        TransferProgress = "正在请求世界快照";
        log?.LogInfo($"客户端已发送注册表握手：{registry.Count} 个实体，摘要 {RegistryDigest}，场景 {CurrentScene}。");
        try
        {
            clientSession.Send(ProtocolMessageType.Ready, ReplicationProtocolCodec.Encode(new ReadyMessage(CurrentScene, RegistryDigest)));
            if (lifecycle.State == ConnectionState.BuildingRegistry) lifecycle.MoveTo(ConnectionState.ApplyingSnapshot);
        }
        catch (Exception error) { clientRegistryRequestSent = false; FailClient("REGISTRY_REQUEST_FAILED", error); }
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
        hostLootScaleScanComplete = false;
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
        log?.LogInfo($"实体注册表已就绪：{next.Count} 个实体，{collisions} 个 ID 冲突，摘要 {RegistryDigest}，场景 {CurrentScene}。");
    }

    private void SetState(ConnectionState next)
    {
        if (State == next) return;
        State = next;
        log?.LogInfo($"联机状态：{StateText(next)}。");
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
        if (clientSession != null && !clientSnapshotReady) return clientSession.Session.Lifecycle.State;
        if (registryDirty) return ConnectionState.BuildingRegistry;
        return ConnectionState.Ready;
    }

    private static string StateText(ConnectionState state) => state switch
    {
        ConnectionState.Disconnected => "未连接",
        ConnectionState.Connecting => "连接中",
        ConnectionState.VersionChecking => "版本检查",
        ConnectionState.SaveTransfer => "准备存档",
        ConnectionState.LoadingSave => "加载存档",
        ConnectionState.BuildingRegistry => "建立实体注册表",
        ConnectionState.ApplyingSnapshot => "应用世界快照",
        ConnectionState.Ready => "已就绪",
        ConnectionState.Failed => "失败",
        ConnectionState.Stopping => "停止中",
        _ => state.ToString()
    };

    private bool IsNetworkConnected() => hostSession?.IsActive == true || clientSession?.HandshakeComplete == true;
    private ushort Port => (ushort)Mathf.Clamp(portConfig?.Value ?? 17777, 1, 65535);

    private void PollHotkeys()
    {
        var f1 = Input.GetKey(KeyCode.F1); var f2 = Input.GetKey(KeyCode.F2); var f3 = Input.GetKey(KeyCode.F3);
        try
        {
            if (f1 && !f1WasDown) StartHost();
            if (f2 && !f2WasDown) ConnectClient();
            if (f3 && !f3WasDown) { StopNetwork(); log?.LogInfo("联机会话已停止。"); }
        }
        catch (Exception error) { log?.LogError($"Standalone network command failed: {error}"); }
        finally { f1WasDown = f1; f2WasDown = f2; f3WasDown = f3; }
    }

    private void OnHandshakeSucceeded()
    {
        log?.LogInfo($"握手成功。玩家 ID：{clientSession?.PeerId}，主机会话：{clientSession?.HostSessionId}。");
        clientSession?.Send(ProtocolMessageType.SaveTransferRequest,ReplicationProtocolCodec.Encode(new SaveTransferRequest(Guid.NewGuid())));
    }
    private void OnHandshakeFailed(string error) => log?.LogError($"握手失败：{error}");
    private void OnPeerAccepted(int connectionId) => log?.LogInfo($"已接受玩家连接：{connectionId}。");
    private void OnPeerRejected(int connectionId, string error) => log?.LogWarning($"已拒绝玩家连接 {connectionId}：{error}");
    private void OnPeerDisconnected(int connectionId){outgoing.Remove(connectionId);readyPeers.Remove(connectionId);sentSaves.Remove(connectionId);sentSnapshots.Remove(connectionId);pendingSnapshotRequests.Remove(connectionId);remotePlayerPositions.Remove(connectionId);remoteInventories.Remove(connectionId);remotePlayers.Remove(connectionId);nextAttackAllowed.Remove(connectionId);if(remoteAttackAnchors.TryGetValue(connectionId,out var anchor)){if(anchor!=null)UnityEngine.Object.Destroy(anchor);remoteAttackAnchors.Remove(connectionId);}}

    private void OnHostMessage(int peer,ProtocolEnvelope envelope)
    {
        try
        {
            if(envelope.MessageType==ProtocolMessageType.SaveTransferRequest){ReplicationProtocolCodec.DecodeSaveTransferRequest(envelope.Payload);PrepareSave(peer);}
            else if(envelope.MessageType==ProtocolMessageType.SaveTransferApplied){var applied=ReplicationProtocolCodec.DecodeSaveTransferApplied(envelope.Payload);if(!sentSaves.TryGetValue(peer,out var expected)||expected!=applied.TransferId)throw new InvalidDataException("Save acknowledgement does not match active transfer.");log?.LogInfo($"Peer {peer} installed verified save {applied.TransferId}.");}
            else if(envelope.MessageType==ProtocolMessageType.Ready){var ready=ReplicationProtocolCodec.DecodeReady(envelope.Payload);PrepareSnapshot(peer,ready);}
            else if(envelope.MessageType==ProtocolMessageType.WorldSnapshotApplied){var applied=ReplicationProtocolCodec.DecodeWorldSnapshotApplied(envelope.Payload);if(!sentSnapshots.TryGetValue(peer,out var expected)||expected!=applied.SnapshotId||applied.Scene!=CurrentScene||applied.RegistryDigest!=RegistryDigest)throw new InvalidDataException("Snapshot acknowledgement does not match active snapshot.");var firstReady=readyPeers.Add(peer);if(firstReady)remoteInventories[peer]=DarkwoodPlayerInventoryShadow.CaptureInitial();Queue(peer,ProtocolMessageType.Ready,ReplicationProtocolCodec.Encode(new ReadyMessage(CurrentScene,RegistryDigest)));SendHostPose(peer);log?.LogInfo(firstReady?$"Peer {peer} READY after applying snapshot {applied.SnapshotId}, {applied.EntityCount} entities.":$"Peer {peer} repeated snapshot acknowledgement {applied.SnapshotId}; Ready confirmation resent.");}
            else if(envelope.MessageType==ProtocolMessageType.PlayerPose){var pose=ReplicationProtocolCodec.DecodePlayerPose(envelope.Payload);if(!readyPeers.Contains(peer)||pose.Scene!=CurrentScene)return;pose=new PlayerPoseMessage(peer,pose.Sequence,CurrentScene,pose.X,pose.Y,pose.Z,pose.Qx,pose.Qy,pose.Qz,pose.Qw,pose.Flags,pose.TorsoClip,pose.TorsoFrame,pose.LegsClip,pose.LegsFrame);remotePlayerPositions[peer]=new Vector3(pose.X,pose.Y,pose.Z);remotePlayers.Apply(pose,0);var payload=ReplicationProtocolCodec.Encode(pose);foreach(var readyPeer in readyPeers.ToArray())if(readyPeer!=peer)Queue(readyPeer,ProtocolMessageType.PlayerPose,payload);}
            else if(envelope.MessageType==ProtocolMessageType.ActionRequest)HandleActionRequest(peer,ReplicationProtocolCodec.DecodeActionRequest(envelope.Payload));
        }
        catch(Exception error){log?.LogError($"Host protocol handler failed for peer {peer}: {error}");Queue(peer,ProtocolMessageType.Error,ReplicationProtocolCodec.Encode(new ProtocolErrorMessage("HOST_HANDLER_FAILED",error.Message)));}
    }

    private void OnClientMessage(ProtocolEnvelope envelope)
    {
        try
        {
            if(envelope.MessageType==ProtocolMessageType.SaveTransferManifest){incomingSaveManifest=ReplicationProtocolCodec.DecodeSaveTransferManifest(envelope.Payload);incomingSave=new ChunkTransferAssembler(incomingSaveManifest.TransferId,incomingSaveManifest.TotalBytes,incomingSaveManifest.ChunkCount,incomingSaveManifest.Sha256);TransferProgress=$"正在接收存档：0/{incomingSave.ChunkCount}（0%）";log?.LogInfo($"开始接收存档：{incomingSaveManifest.TotalBytes} 字节，{incomingSaveManifest.ChunkCount} 个数据块。");}
            else if(envelope.MessageType==ProtocolMessageType.SaveTransferChunk)ReceiveSaveChunk(ReplicationProtocolCodec.DecodeSaveTransferChunk(envelope.Payload));
            else if(envelope.MessageType==ProtocolMessageType.WorldSnapshotManifest){var manifest=ReplicationProtocolCodec.DecodeWorldSnapshotManifest(envelope.Payload);if(clientSnapshotManifestReceived)return;incomingSnapshotManifest=manifest;incomingSnapshot=new ChunkTransferAssembler(incomingSnapshotManifest.SnapshotId,incomingSnapshotManifest.TotalBytes,incomingSnapshotManifest.ChunkCount,incomingSnapshotManifest.Sha256,64L*1024*1024);clientRegistryRequestSent=true;clientSnapshotManifestReceived=true;TransferProgress=$"正在接收世界快照：0/{incomingSnapshot.ChunkCount}（0%）";log?.LogInfo($"开始接收世界快照：{incomingSnapshotManifest.TotalBytes} 字节，{incomingSnapshotManifest.ChunkCount} 个数据块。");}
            else if(envelope.MessageType==ProtocolMessageType.WorldSnapshotChunk)ReceiveSnapshotChunk(ReplicationProtocolCodec.DecodeWorldSnapshotChunk(envelope.Payload));
            else if(envelope.MessageType==ProtocolMessageType.EntityDelta){var delta=ReplicationProtocolCodec.DecodeEntityDelta(envelope.Payload);if(clientSession?.Session.Lifecycle.State==ConnectionState.Ready&&delta.Scene==CurrentScene){replication.Apply(delta.Entities,false);replication.ApplyDespawns(delta.Despawns);}}
            else if(envelope.MessageType==ProtocolMessageType.InventoryState){var inventory=ReplicationProtocolCodec.DecodeInventoryState(envelope.Payload);if(!replication.Apply(inventory))throw new InvalidDataException($"无法应用主机容器状态：ID={inventory.Value:X16}，名称={inventory.Name}，位置=({inventory.X:F1},{inventory.Y:F1},{inventory.Z:F1})。");}
            else if(envelope.MessageType==ProtocolMessageType.PlayerPose)remotePlayers.Apply(ReplicationProtocolCodec.DecodePlayerPose(envelope.Payload),clientSession?.PeerId??-1);
            else if(envelope.MessageType==ProtocolMessageType.ActionResult)HandleActionResult(ReplicationProtocolCodec.DecodeActionResult(envelope.Payload));
            else if(envelope.MessageType==ProtocolMessageType.ActionRejected)HandleActionRejected(ReplicationProtocolCodec.DecodeActionRejected(envelope.Payload));
            else if(envelope.MessageType==ProtocolMessageType.Ready){var ready=ReplicationProtocolCodec.DecodeReady(envelope.Payload);if(ready.Scene!=CurrentScene||ready.RegistryDigest!=incomingSnapshotManifest.RegistryDigest)throw new InvalidDataException("主机就绪确认与已应用的世界快照不一致。");clientSnapshotReady=true;lastSnapshotApplied=null;snapshotAckRetryCount=0;TransferProgress="联机已就绪";if(clientSession?.Session.Lifecycle.State==ConnectionState.ApplyingSnapshot)clientSession.Session.Lifecycle.MoveTo(ConnectionState.Ready);log?.LogInfo($"客户端联机已就绪：场景 {ready.Scene}，注册表摘要 {ready.RegistryDigest}。");}
            else if(envelope.MessageType==ProtocolMessageType.Error){var error=ReplicationProtocolCodec.DecodeError(envelope.Payload);throw new InvalidDataException($"Host error {error.Code}: {error.Detail}");}
        }
        catch(Exception error){FailClient("CLIENT_PROTOCOL_FAILED",error);}
    }

    private void PrepareSave(int peer)
    {
        var profile=global::Core.currentProfile;if(profile==null||!profile.Active)throw new InvalidOperationException("Host has no active save profile.");var manager=Singleton<SaveManager>.Instance;if(manager==null)throw new InvalidOperationException("SaveManager is unavailable.");manager.Save(false,true,true,false,false,true,false);manager.saveProfilesFile();var bundle=DarkwoodSaveBundle.Build(manager.baseSaveDirectory,profile.id);var id=Guid.NewGuid();sentSaves[peer]=id;var chunks=ChunkTransferAssembler.Split(bundle,128*1024);Queue(peer,ProtocolMessageType.SaveTransferManifest,ReplicationProtocolCodec.Encode(new SaveTransferManifest(id,profile.id,bundle.LongLength,chunks.Length,ChunkTransferAssembler.Hash(bundle),$"Day {profile.day}, chapter {profile.chapter}")));for(var i=0;i<chunks.Length;i++)Queue(peer,ProtocolMessageType.SaveTransferChunk,ReplicationProtocolCodec.Encode(new SaveTransferChunk(id,i,chunks.Length,chunks[i],ChunkTransferAssembler.Hash(chunks[i]))),"存档",i,chunks.Length);log?.LogInfo($"已为玩家 {peer} 准备实时存档：传输 {id}，{bundle.Length} 字节，{chunks.Length} 个数据块。");
    }

    private void ReceiveSaveChunk(SaveTransferChunk chunk)
    {
        if(incomingSave==null)throw new InvalidDataException("存档数据块早于存档清单到达。");incomingSave.Add(chunk.TransferId,chunk.Index,chunk.Total,chunk.Data,chunk.Hash);TransferProgress=$"正在接收存档：{incomingSave.ReceivedChunks}/{incomingSave.ChunkCount}（{(int)(incomingSave.ReceivedChunks*100f/incomingSave.ChunkCount)}%）";if(!incomingSave.IsComplete)return;TransferProgress="正在校验并安装存档";var data=incomingSave.Build();incomingSave=null;InstallDownloadedSave(data,incomingSaveManifest.ProfileId);clientSession?.Send(ProtocolMessageType.SaveTransferApplied,ReplicationProtocolCodec.Encode(new SaveTransferApplied(incomingSaveManifest.TransferId,incomingSaveManifest.ProfileId,"isolated-client-save")));StartCoroutine(LoadDownloadedSave(incomingSaveManifest.ProfileId));
    }

    private void InstallDownloadedSave(byte[] data,int profile)
    {
        var key=HostKey();var root=Path.Combine(Paths.BepInExRootPath,"DarkwoodMPClientSaves",key);var target=Path.Combine(root,"1_4Save");var staging=Path.Combine(root,".incoming-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(staging);try{var extracted=DarkwoodSaveBundle.Extract(data,staging);if(extracted!=profile)throw new InvalidDataException("下载的存档档案 ID 不一致。");Directory.CreateDirectory(root);if(Directory.Exists(target))Directory.Move(target,Path.Combine(root,"previous-"+DateTime.UtcNow.ToString("yyyyMMddHHmmssfff")));Directory.Move(staging,target);}catch{try{if(Directory.Exists(staging))Directory.Delete(staging,true);}catch{}throw;}ActiveClientSaveDirectory=target;log?.LogInfo("下载的存档已安装到独立目录："+target);
    }

    private IEnumerator LoadDownloadedSave(int profileId)
    {
        yield return null;try{if(clientSession==null||!clientSession.HandshakeComplete||clientSession.Session.Lifecycle.State==ConnectionState.Failed)throw new InvalidOperationException("客户端连接已断开，取消存档加载。");if(!global::Core.mainMenu)throw new InvalidOperationException("客户端必须从主菜单加载主机存档。");var manager=Singleton<SaveManager>.Instance;if(manager==null)throw new InvalidOperationException("SaveManager 不可用。");var state=manager.loadGameProfiles();if(state?.profiles==null)throw new InvalidDataException("下载的存档档案信息不可用。");var profile=state.profiles.FirstOrDefault(p=>p!=null&&p.id==profileId&&p.Active);if(profile==null)throw new InvalidDataException("下载的存档档案信息不可用。");global::Core.profiles=state.profiles;global::Core.currentProfile=profile;manager.updateFilePaths();if(clientSession.Session.Lifecycle.State==ConnectionState.SaveTransfer)clientSession.Session.Lifecycle.MoveTo(ConnectionState.LoadingSave);manager.onFinishedLoading=(saveDelegate)Delegate.Remove(manager.onFinishedLoading,new saveDelegate(OnDownloadedSaveFinished));manager.onFinishedLoading=(saveDelegate)Delegate.Combine(manager.onFinishedLoading,new saveDelegate(OnDownloadedSaveFinished));TransferProgress="正在加载存档";Singleton<UI>.Instance.StartCoroutine(Singleton<UI>.Instance.initLoadGame());}catch(Exception error){FailClient("SAVE_LOAD_FAILED",error);}
    }

    private void OnDownloadedSaveFinished()
    {
        var manager=Singleton<SaveManager>.Instance;if(manager!=null)manager.onFinishedLoading=(saveDelegate)Delegate.Remove(manager.onFinishedLoading,new saveDelegate(OnDownloadedSaveFinished));if(clientSession==null||!clientSession.HandshakeComplete||clientSession.Session.Lifecycle.State==ConnectionState.Failed){log?.LogWarning("客户端连接已断开，忽略已完成的存档加载回调。");return;}if(clientSession.Session.Lifecycle.State==ConnectionState.LoadingSave)clientSession.Session.Lifecycle.MoveTo(ConnectionState.BuildingRegistry);registryDirty=true;StartCoroutine(WaitForRegistryThenReady());
    }

    private IEnumerator WaitForRegistryThenReady()
    {
        var deadline=Time.realtimeSinceStartup+60f;while((registryDirty||Player.Instance==null||registry==null)&&Time.realtimeSinceStartup<deadline)yield return null;try{if(registryDirty||Player.Instance==null||registry==null)throw new InvalidOperationException("实体注册表在 60 秒内未就绪。");TrySendClientRegistryReady();}catch(Exception error){FailClient("REGISTRY_BUILD_FAILED",error);}
    }

    private void PrepareSnapshot(int peer,ReadyMessage ready)
    {
        if(registryDirty)RebuildRegistry();
        if(!string.Equals(ready.Scene,CurrentScene,StringComparison.Ordinal)){Queue(peer,ProtocolMessageType.Error,ReplicationProtocolCodec.Encode(new ProtocolErrorMessage("SCENE_MISMATCH",$"host={CurrentScene};client={ready.Scene}")));return;}
        EnsureHostExistingLootScaled();
        if(!hostLootScaleScanComplete)
        {
            pendingSnapshotRequests[peer]=ready;
            TransferProgress="正在按联机人数准备共享柜子";
            log?.LogInfo($"Peer {peer} handshake is valid; delaying world snapshot until shared-container preparation completes.");
            return;
        }
        if(!string.Equals(ready.RegistryDigest,RegistryDigest,StringComparison.Ordinal))log?.LogWarning($"Peer {peer} registry digest differs (host={RegistryDigest}, client={ready.RegistryDigest}); sending authoritative snapshot.");
        if(sentSnapshots.ContainsKey(peer))return;var entities=replication.Snapshot();var inventories=replication.CaptureInventorySnapshot();var state=DarkwoodWorldSnapshotCodec.Encode(CurrentScene,RegistryDigest,serverTick,entities,inventories);var id=Guid.NewGuid();sentSnapshots[peer]=id;var chunks=ChunkTransferAssembler.Split(state,64*1024);Queue(peer,ProtocolMessageType.WorldSnapshotManifest,ReplicationProtocolCodec.Encode(new WorldSnapshotManifest(id,state.LongLength,chunks.Length,ChunkTransferAssembler.Hash(state),CurrentScene,RegistryDigest,serverTick)));for(var i=0;i<chunks.Length;i++)Queue(peer,ProtocolMessageType.WorldSnapshotChunk,ReplicationProtocolCodec.Encode(new WorldSnapshotChunk(id,i,chunks.Length,chunks[i],ChunkTransferAssembler.Hash(chunks[i]))),"世界快照",i,chunks.Length);log?.LogInfo($"已为玩家 {peer} 准备世界快照 {id}：{entities.Length} 个实体，{inventories.Length} 个库存，{state.Length} 字节，注册表 {RegistryDigest}。");
    }

    private void EnsureHostExistingLootScaled()
    {
        if(hostSession==null||hostLootScaleScanStarted||hostLootScaleScanComplete||ConfiguredPlayerCount<=1||registry==null)return;
        hostLootScaleScanStarted=true;
        hostLootScaleCoroutine=StartCoroutine(ScaleExistingLootCoroutine());
    }

    private IEnumerator ScaleExistingLootCoroutine()
    {
        var inventories=new List<Inventory>();
        foreach(var component in scanner.ScanScene())
        {
            if(component is Inventory inventory && (inventory.invType==Inventory.InvType.itemInv||inventory.invType==Inventory.InvType.deathDrop)) inventories.Add(inventory);
            if((inventories.Count%32)==0)yield return null;
        }
        var hostToken=HostSaveToken();var scenePrefix=hostToken+"|"+CurrentScene+"|";var legacyLedger=scaledHostInventoryKeys.Any(value=>value.StartsWith(scenePrefix,StringComparison.Ordinal));var scaled=0;var migrated=0;var processed=0;
        foreach(var inventory in inventories)
        {
            var id=scanner.ToPersistentId(inventory);var key=hostToken+"|"+CurrentScene+"|"+id.Value.ToString("X16");
            if(scaledHostInventoryKeys.Add(key))
            {
                // alpha.8 used the pre-indexed EntityId. If its ledger already
                // contains this save/scene, keep the existing scaled quantities
                // and migrate the entries instead of multiplying them again.
                if(legacyLedger){migrated++;}
                else{DarkwoodLootScalingPatch.ScaleExistingInventory(inventory,ConfiguredPlayerCount);scaled++;}
            }
            processed++;
            if((processed%8)==0){TransferProgress=$"正在准备共享柜子：{processed}/{inventories.Count}";yield return null;}
        }
        hostLootScaleScanComplete=true;hostLootScaleScanStarted=false;hostLootScaleCoroutine=null;
        if(scaled>0)SaveLootScaleLedger();
        TransferProgress=string.Empty;
        log?.LogInfo($"已按 {ConfiguredPlayerCount} 人完成共享柜子准备：扫描 {inventories.Count} 个，扩容 {scaled} 个，迁移旧账本 {migrated} 个。");
        foreach(var request in pendingSnapshotRequests.ToArray())
        {
            pendingSnapshotRequests.Remove(request.Key);
            if(hostSession!=null)PrepareSnapshot(request.Key,request.Value);
        }
    }

    private string HostSaveToken()
    {
        try
        {
            var manager=Singleton<SaveManager>.Instance;
            var path=manager?.staticFile;
            if(!string.IsNullOrEmpty(path)&&File.Exists(path))return Path.GetFullPath(path)+"|"+File.GetCreationTimeUtc(path).Ticks;
        }
        catch{}
        return "profile:"+(global::Core.currentProfile?.id??-1);
    }

    private void LoadLootScaleLedger()
    {
        scaledHostInventoryKeys.Clear();
        try{if(File.Exists(lootScaleLedgerPath))foreach(var line in File.ReadAllLines(lootScaleLedgerPath))if(!string.IsNullOrWhiteSpace(line))scaledHostInventoryKeys.Add(line.Trim());}
        catch(Exception error){log?.LogWarning("读取柜子扩容账本失败："+error.Message);}
    }

    private void SaveLootScaleLedger()
    {
        try{Directory.CreateDirectory(Path.GetDirectoryName(lootScaleLedgerPath));File.WriteAllLines(lootScaleLedgerPath,scaledHostInventoryKeys.OrderBy(value=>value).ToArray());}
        catch(Exception error){log?.LogWarning("保存柜子扩容账本失败："+error.Message);}
    }

    private void ReceiveSnapshotChunk(WorldSnapshotChunk chunk)
    {
        if(incomingSnapshot==null)throw new InvalidDataException("World snapshot chunk arrived before manifest.");incomingSnapshot.Add(chunk.SnapshotId,chunk.Index,chunk.Total,chunk.Data,chunk.Hash);TransferProgress=$"正在接收世界快照：{incomingSnapshot.ReceivedChunks}/{incomingSnapshot.ChunkCount}（{(int)(incomingSnapshot.ReceivedChunks*100f/incomingSnapshot.ChunkCount)}%）";if(!incomingSnapshot.IsComplete)return;var bytes=incomingSnapshot.Build();incomingSnapshot=null;var snapshot=DarkwoodWorldSnapshotCodec.Decode(bytes);if(snapshot.Scene!=CurrentScene||snapshot.Scene!=incomingSnapshotManifest.Scene)throw new InvalidDataException("世界快照场景不一致。");if(snapshot.RegistryDigest!=incomingSnapshotManifest.RegistryDigest)throw new InvalidDataException($"快照摘要不一致：payload={snapshot.RegistryDigest}，manifest={incomingSnapshotManifest.RegistryDigest}。");if(snapshot.ServerTick!=incomingSnapshotManifest.ServerTick)throw new InvalidDataException("世界快照 tick 不一致。");replication.Apply(snapshot.Entities,true);var appliedInventories=0;var failedInventories=0;foreach(var inventory in snapshot.Inventories){if(replication.Apply(inventory))appliedInventories++;else{failedInventories++;log?.LogError($"共享容器快照无法绑定：ID={inventory.Value:X16}，名称={inventory.Name}，位置=({inventory.X:F1},{inventory.Y:F1},{inventory.Z:F1})，类型={inventory.InventoryType}。");}}if(failedInventories>0)throw new InvalidDataException($"有 {failedInventories} 个共享容器无法应用主机权威快照，已阻止客户端误进入就绪状态。");lastSnapshotApplied=new WorldSnapshotApplied(incomingSnapshotManifest.SnapshotId,snapshot.Scene,snapshot.RegistryDigest,snapshot.ServerTick,snapshot.Entities.Length);snapshotAckRetryCount=0;SendSnapshotAcknowledgement();log?.LogInfo($"世界快照应用完成：{snapshot.Entities.Length} 个实体，共享容器 {appliedInventories}/{snapshot.Inventories.Length}，tick {snapshot.ServerTick}；等待主机确认。");
    }

    private void RetrySnapshotAcknowledgement()
    {
        if(clientSnapshotReady||lastSnapshotApplied==null||clientSession?.HandshakeComplete!=true||clientSession.Session.Lifecycle.State!=ConnectionState.ApplyingSnapshot||Time.realtimeSinceStartup<nextSnapshotAckRetry)return;
        SendSnapshotAcknowledgement();
    }

    private void SendSnapshotAcknowledgement()
    {
        if(lastSnapshotApplied==null||clientSession==null)return;
        snapshotAckRetryCount++;
        clientSession.Send(ProtocolMessageType.WorldSnapshotApplied,ReplicationProtocolCodec.Encode(lastSnapshotApplied.Value));
        nextSnapshotAckRetry=Time.realtimeSinceStartup+2f;
        TransferProgress=$"快照已应用，等待主机确认（第 {snapshotAckRetryCount} 次）";
        if(snapshotAckRetryCount>1)log?.LogWarning($"主机 Ready 确认尚未到达，正在重发快照应用确认：第 {snapshotAckRetryCount} 次。");
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

    public bool TryRequestContainerTake(InvSlot slot, bool takeAll)
    {
        if(clientSession?.Session.Lifecycle.State!=ConnectionState.Ready||slot==null||slot.inventory==null)return false;
        if(InvItemClass.isNull(slot.invItem))return true;
        if(!replication.TryGetId(slot.inventory,out var id)||!replication.TryGetInventoryState(id,out var state))
        {
            log?.LogWarning("Container take was not sent because the inventory has no registered EntityId.");
            return true;
        }
        var slotIndex=slot.inventory.slots.IndexOf(slot);
        if(slotIndex<0)return true;
        var payload=ReplicationProtocolCodec.Encode(new ContainerTakePayload(slotIndex,takeAll?-1:1));
        var request=new ActionRequestMessage(Guid.NewGuid(),clientSession.PeerId,ActionKindWire.ContainerTake,id.Value,id.IsPersistent,state.Revision,payload);
        pendingActions[request.RequestId]=request;
        clientSession.Send(ProtocolMessageType.ActionRequest,ReplicationProtocolCodec.Encode(request));
        log?.LogInfo($"Container take request {request.RequestId} sent for {id}, slot {slotIndex}, amount {(takeAll?"all":"1")}, revision {state.Revision}.");
        return true;
    }

    public bool TryRequestContainerPut(InvSlot sourceSlot, Inventory destination, bool putAll) =>
        TryRequestContainerPut(sourceSlot, destination, -1, putAll);

    public bool TryRequestContainerPut(InvSlot sourceSlot, Inventory destination, int destinationSlotIndex, bool putAll)
    {
        if(clientSession?.Session.Lifecycle.State!=ConnectionState.Ready||sourceSlot?.inventory==null||destination==null)return false;
        if(sourceSlot.inventory.invType!=Inventory.InvType.playerInv&&sourceSlot.inventory.invType!=Inventory.InvType.hotbar)return true;
        if(!replication.TryGetId(destination,out var id)||!replication.TryGetInventoryState(id,out var state))
        {
            log?.LogWarning("物品未放入：目标容器没有已注册的 EntityId。");
            return true;
        }
        var hotbar=sourceSlot.inventory.invType==Inventory.InvType.hotbar;
        var slotIndex=sourceSlot.inventory.slots.IndexOf(sourceSlot);
        if(slotIndex<0)return true;
        foreach(var pending in pendingActions.Values)if(pending.Kind==ActionKindWire.ContainerPut){var existing=ReplicationProtocolCodec.DecodeContainerPut(pending.Payload);if(existing.Hotbar==hotbar&&existing.SlotIndex==slotIndex){log?.LogInfo($"该玩家槽位已有等待主机确认的放入请求：{(hotbar?"快捷栏":"背包")} {slotIndex}。");return true;}}
        var payload=ReplicationProtocolCodec.Encode(new ContainerPutPayload(hotbar,slotIndex,destinationSlotIndex,putAll?-1:1));
        var request=new ActionRequestMessage(Guid.NewGuid(),clientSession.PeerId,ActionKindWire.ContainerPut,id.Value,id.IsPersistent,state.Revision,payload);
        pendingActions[request.RequestId]=request;
        clientSession.Send(ProtocolMessageType.ActionRequest,ReplicationProtocolCodec.Encode(request));
        log?.LogInfo($"已发送容器放入请求 {request.RequestId}：容器 {id}，目标槽位 {destinationSlotIndex}，{(hotbar?"快捷栏":"背包")}槽位 {slotIndex}，数量 {(putAll?"全部":"1")}，容器版本 {state.Revision}。");
        return true;
    }

    public bool TryRequestMeleeAttack(Player player,bool special)
    {
        if(clientSession?.Session.Lifecycle.State!=ConnectionState.Ready||player==null)return false;
        if(InvItemClass.isNull(player.currentItem))return false;
        var item=player.currentItem;
        var hotbar=TryFindItemSlot(player.Hotbar,item,out var slotIndex);
        if(!hotbar&&!TryFindItemSlot(player.Inventory,item,out slotIndex)){log?.LogWarning("Melee attack was not sent because the active item has no inventory slot.");return false;}
        // Darkwood fires melee hits along transform.up; send the horizontal aim direction.
        var aim=player.transform.up;var pos=player.transform.position;
        var payload=ReplicationProtocolCodec.Encode(new AttackPayload(special?(byte)2:(byte)1,hotbar,slotIndex,aim.x,aim.z,pos.x,pos.z));
        var request=new ActionRequestMessage(Guid.NewGuid(),clientSession.PeerId,ActionKindWire.Attack,0,false,0,payload);
        pendingActions[request.RequestId]=request;
        clientSession.Send(ProtocolMessageType.ActionRequest,ReplicationProtocolCodec.Encode(request));
        log?.LogInfo($"Melee attack request {request.RequestId} sent: {(special?"special":"normal")}, {(hotbar?"hotbar":"backpack")} slot {slotIndex}.");
        return true;
    }

    private static bool TryFindItemSlot(Inventory inventory,InvItemClass item,out int slotIndex)
    {
        slotIndex=-1;
        if(inventory?.slots==null||InvItemClass.isNull(item))return false;
        for(var i=0;i<inventory.slots.Count;i++)
        {
            var slot=inventory.slots[i];
            if(slot!=null&&!InvItemClass.isNull(slot.invItem)&&ReferenceEquals(slot.invItem,item)){slotIndex=i;return true;}
        }
        return false;
    }

    public bool TryRequestDoorToggle(Door door)
    {
        if(clientSession?.Session.Lifecycle.State!=ConnectionState.Ready||door==null)return false;
        if(!replication.TryGetId(door,out var id)){log?.LogWarning("Door toggle was not sent because the door has no registered EntityId.");return false;}
        ulong expectedRevision=0;
        if(replication.TryGetState(id,out var state))expectedRevision=state.Revision;
        var request=new ActionRequestMessage(Guid.NewGuid(),clientSession.PeerId,ActionKindWire.DoorInteract,id.Value,id.IsPersistent,expectedRevision,ReplicationProtocolCodec.Encode(new InteractPayload(0)));
        pendingActions[request.RequestId]=request;
        clientSession.Send(ProtocolMessageType.ActionRequest,ReplicationProtocolCodec.Encode(request));
        log?.LogInfo($"Door interact request {request.RequestId} sent for {id}, revision {expectedRevision}.");
        return true;
    }

    public bool TryRequestWindowBarricade(Window window,int destHealth)
    {
        if(clientSession?.Session.Lifecycle.State!=ConnectionState.Ready||window==null)return false;
        if(!replication.TryGetId(window,out var id)){log?.LogWarning("Window barricade was not sent because the window has no registered EntityId.");return false;}
        ulong expectedRevision=0;
        if(replication.TryGetState(id,out var state))expectedRevision=state.Revision;
        var request=new ActionRequestMessage(Guid.NewGuid(),clientSession.PeerId,ActionKindWire.WindowInteract,id.Value,id.IsPersistent,expectedRevision,ReplicationProtocolCodec.Encode(new InteractPayload(destHealth)));
        pendingActions[request.RequestId]=request;
        clientSession.Send(ProtocolMessageType.ActionRequest,ReplicationProtocolCodec.Encode(request));
        log?.LogInfo($"Window barricade request {request.RequestId} sent for {id}, destHealth {destHealth}, revision {expectedRevision}.");
        return true;
    }

    public bool TryRequestItemActivate(Item item)
    {
        if(clientSession?.Session.Lifecycle.State!=ConnectionState.Ready||item==null)return false;
        if(!replication.TryGetId(item,out var id)){log?.LogWarning("Item activate was not sent because the item has no registered EntityId.");return false;}
        ulong expectedRevision=0;
        if(replication.TryGetState(id,out var state))expectedRevision=state.Revision;
        var request=new ActionRequestMessage(Guid.NewGuid(),clientSession.PeerId,ActionKindWire.ItemActivate,id.Value,id.IsPersistent,expectedRevision,Array.Empty<byte>());
        pendingActions[request.RequestId]=request;
        clientSession.Send(ProtocolMessageType.ActionRequest,ReplicationProtocolCodec.Encode(request));
        log?.LogInfo($"Item activate request {request.RequestId} sent for {id}, revision {expectedRevision}.");
        return true;
    }

    public void NotifyHostContainerChanged(Inventory inventory)
    {
        if(hostSession==null||readyPeers.Count==0||inventory==null||replication.ApplyingRemote)return;
        if(!replication.TryGetId(inventory,out var id))return;
        try{BroadcastInventory(replication.CaptureAuthoritativeInventory(id));}
        catch(Exception error){log?.LogWarning($"Failed to publish host container mutation for {id}: {error.Message}");}
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
        switch(request.Kind)
        {
            case ActionKindWire.Pickup: HandlePickupRequest(peer,request);return;
            case ActionKindWire.ContainerTake: HandleContainerTakeRequest(peer,request);return;
            case ActionKindWire.ContainerPut: HandleContainerPutRequest(peer,request);return;
            case ActionKindWire.Attack: HandleAttackRequest(peer,request);return;
            case ActionKindWire.DoorInteract: HandleDoorInteractRequest(peer,request);return;
            case ActionKindWire.WindowInteract: HandleWindowInteractRequest(peer,request);return;
            case ActionKindWire.ItemActivate: HandleItemActivateRequest(peer,request);return;
            default: RejectAction(peer,request,"UNSUPPORTED_ACTION",0);return;
        }
    }

    private void HandlePickupRequest(int peer,ActionRequestMessage request)
    {
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
        var result=new ActionResultMessage(request.RequestId,request.Kind,id.Value,id.IsPersistent,despawn.Revision,ReplicationProtocolCodec.Encode(shadow.CaptureState()));
        RemoveEvictedAction(actionCache.Store(new NetworkActionResult(request.RequestId,true,new StateVersion(despawn.Revision),string.Empty)));acceptedActions++;
        cachedActionResults[request.RequestId]=result;
        cachedActionOwners[request.RequestId]=peer;
        Queue(peer,ProtocolMessageType.ActionResult,ReplicationProtocolCodec.Encode(result));
        var delta=ReplicationProtocolCodec.Encode(new EntityDeltaMessage(CurrentScene,serverTick,Array.Empty<EntityStateWire>(),new[]{despawn}));foreach(var readyPeer in readyPeers.ToArray())Queue(readyPeer,ProtocolMessageType.EntityDelta,delta);
        log?.LogInfo($"Pickup accepted {request.RequestId}: peer {peer}, {pickup.ItemType} x{pickup.Amount}, target {id}, revision {despawn.Revision}.");
    }

    private void HandleContainerTakeRequest(int peer,ActionRequestMessage request)
    {
        var id=new EntityId(request.TargetValue,request.TargetPersistent);
        if(!replication.TryGetComponent(id,out var component)||!(component is Inventory inventory)){RejectAction(peer,request,"CONTAINER_NOT_FOUND",0);return;}
        if(inventory.invType!=Inventory.InvType.itemInv&&inventory.invType!=Inventory.InvType.deathDrop){RejectAction(peer,request,"NOT_SHARED_CONTAINER",0);return;}
        if(!replication.TryGetInventoryState(id,out var state)){RejectAction(peer,request,"CONTAINER_STATE_MISSING",0);return;}
        if(!ActionValidation.RevisionMatches(state.Revision,request.ExpectedRevision)){RejectAction(peer,request,"STALE_REVISION",state.Revision);SendInventory(peer,state);return;}
        if(!remotePlayerPositions.TryGetValue(peer,out var position)){RejectAction(peer,request,"PLAYER_POSE_MISSING",state.Revision);return;}
        if(!ActionValidation.WithinDistance(position.x,position.y,position.z,inventory.transform.position.x,inventory.transform.position.y,inventory.transform.position.z,8f)){RejectAction(peer,request,"TOO_FAR",state.Revision);return;}
        var take=ReplicationProtocolCodec.DecodeContainerTake(request.Payload);
        if(take.SlotIndex<0||take.SlotIndex>=inventory.slots.Count){RejectAction(peer,request,"SLOT_NOT_FOUND",state.Revision);SendInventory(peer,state);return;}
        var sourceSlot=inventory.slots[take.SlotIndex];
        if(sourceSlot==null||InvItemClass.isNull(sourceSlot.invItem)){RejectAction(peer,request,"ITEM_EMPTY",state.Revision);SendInventory(peer,state);return;}
        if(!remoteInventories.TryGetValue(peer,out var shadow)){RejectAction(peer,request,"PLAYER_INVENTORY_MISSING",state.Revision);return;}
        var source=sourceSlot.invItem;
        var amount=take.Amount<0?source.amount:Math.Min(Math.Max(1,take.Amount),source.amount);
        var award=new InvItemClass(source);award.amount=amount;
        if(!shadow.CanAdd(award)){RejectAction(peer,request,"INVENTORY_FULL",state.Revision);return;}
        shadow.Add(award);
        source.removeAmount(amount);
        inventory.refreshItems();
        var authoritative=replication.CaptureAuthoritativeInventory(id);
        var result=new ActionResultMessage(request.RequestId,request.Kind,id.Value,id.IsPersistent,authoritative.Revision,ReplicationProtocolCodec.Encode(shadow.CaptureState()));
        RemoveEvictedAction(actionCache.Store(new NetworkActionResult(request.RequestId,true,new StateVersion(authoritative.Revision),string.Empty)));acceptedActions++;
        cachedActionResults[request.RequestId]=result;cachedActionOwners[request.RequestId]=peer;
        Queue(peer,ProtocolMessageType.ActionResult,ReplicationProtocolCodec.Encode(result));
        BroadcastInventory(authoritative);
        log?.LogInfo($"主机已批准容器取出 {request.RequestId}：玩家 {peer}，{award.type} x{award.amount}，容器 {id}，槽位 {take.SlotIndex}，版本 {authoritative.Revision}。");
    }

    private void HandleContainerPutRequest(int peer,ActionRequestMessage request)
    {
        var id=new EntityId(request.TargetValue,request.TargetPersistent);
        if(!replication.TryGetComponent(id,out var component)||!(component is Inventory inventory)){RejectAction(peer,request,"CONTAINER_NOT_FOUND",0);return;}
        if(inventory.invType!=Inventory.InvType.itemInv){RejectAction(peer,request,"NOT_SHARED_CONTAINER",0);return;}
        if(!replication.TryGetInventoryState(id,out var state)){RejectAction(peer,request,"CONTAINER_STATE_MISSING",0);return;}
        if(!ActionValidation.RevisionMatches(state.Revision,request.ExpectedRevision)){RejectAction(peer,request,"STALE_REVISION",state.Revision);SendInventory(peer,state);return;}
        if(!remotePlayerPositions.TryGetValue(peer,out var position)){RejectAction(peer,request,"PLAYER_POSE_MISSING",state.Revision);return;}
        if(!ActionValidation.WithinDistance(position.x,position.y,position.z,inventory.transform.position.x,inventory.transform.position.y,inventory.transform.position.z,8f)){RejectAction(peer,request,"TOO_FAR",state.Revision);return;}
        if(!remoteInventories.TryGetValue(peer,out var shadow)){RejectAction(peer,request,"PLAYER_INVENTORY_MISSING",state.Revision);return;}
        var put=ReplicationProtocolCodec.DecodeContainerPut(request.Payload);
        if(!shadow.TryPeek(put.Hotbar,put.SlotIndex,put.Amount,out var item)){RejectAction(peer,request,"PLAYER_SLOT_EMPTY",state.Revision);SendInventory(peer,state);return;}
        var swapped=false;
        if(put.DestinationSlotIndex>=0&&put.DestinationSlotIndex<inventory.slots.Count)
        {
            var destination=inventory.slots[put.DestinationSlotIndex];var existing=destination?.invItem;
            if(destination!=null&&existing!=null&&!InvItemClass.isNull(existing)&&existing.type!=item.Type)
            {
                if(!shadow.CanSwap(put.Hotbar,put.SlotIndex,item)||existing.baseClass==null){RejectAction(peer,request,"SWAP_SOURCE_CHANGED",state.Revision);return;}
                var replacement=new DarkwoodPlayerInventoryShadow.Item(existing.type,existing.amount,existing.durability,(int)existing.modifierQuality,existing.isRecipe,Math.Max(1,existing.baseClass.maxAmount),existing.baseClass.stackable);
                destination.removeItem();destination.createItem(item.Type,item.Amount,item.Durability,(InvItem.ModifierQuality)item.Quality,item.Recipe);shadow.Swap(put.Hotbar,put.SlotIndex,replacement);swapped=true;
            }
        }
        if(!swapped)
        {
            if(!TryAddToContainer(inventory,item,put.DestinationSlotIndex)){RejectAction(peer,request,put.DestinationSlotIndex>=0?"DESTINATION_OCCUPIED":"CONTAINER_FULL",state.Revision);SendInventory(peer,state);return;}
            if(!shadow.Remove(put.Hotbar,put.SlotIndex,item.Amount))throw new InvalidOperationException("Host inventory shadow changed during container transaction.");
        }
        inventory.refreshItems();
        var authoritative=replication.CaptureAuthoritativeInventory(id);
        var result=new ActionResultMessage(request.RequestId,request.Kind,id.Value,id.IsPersistent,authoritative.Revision,ReplicationProtocolCodec.Encode(shadow.CaptureState()));
        RemoveEvictedAction(actionCache.Store(new NetworkActionResult(request.RequestId,true,new StateVersion(authoritative.Revision),string.Empty)));acceptedActions++;
        cachedActionResults[request.RequestId]=result;cachedActionOwners[request.RequestId]=peer;
        Queue(peer,ProtocolMessageType.ActionResult,ReplicationProtocolCodec.Encode(result));
        BroadcastInventory(authoritative);
        log?.LogInfo($"主机已批准容器{(swapped?"交换":"放入")} {request.RequestId}：玩家 {peer}，{item.Type} x{item.Amount}，容器 {id}，目标槽位 {put.DestinationSlotIndex}，来源 {(put.Hotbar?"快捷栏":"背包")}槽位 {put.SlotIndex}，版本 {authoritative.Revision}。");
    }

    private void HandleAttackRequest(int peer,ActionRequestMessage request)
    {
        AttackPayload attack;
        try{attack=ReplicationProtocolCodec.DecodeAttack(request.Payload);}
        catch(Exception error){RejectAction(peer,request,"INVALID_ATTACK_PAYLOAD",0);log?.LogWarning($"Attack payload rejected from peer {peer}: {error.Message}");return;}
        if(!remotePlayerPositions.TryGetValue(peer,out var pose)){RejectAction(peer,request,"PLAYER_POSE_MISSING",0);return;}
        if(nextAttackAllowed.TryGetValue(peer,out var allowedAt)&&Time.unscaledTime<allowedAt){RejectAction(peer,request,"RATE_LIMITED",0);return;}
        nextAttackAllowed[peer]=Time.unscaledTime+AttackCooldownSeconds;
        // The payload position is client-claimed; the host only accepts it near the tracked pose.
        if(!ActionValidation.WithinDistance(pose.x,pose.y,pose.z,attack.PosX,pose.y,attack.PosZ,AttackPoseTolerance)){RejectAction(peer,request,"POSE_MISMATCH",0);return;}
        if(!remoteInventories.TryGetValue(peer,out var shadow)){RejectAction(peer,request,"PLAYER_INVENTORY_MISSING",0);return;}
        if(!shadow.TryPeek(attack.FromHotbar,attack.SlotIndex,-1,out var weapon)){RejectAction(peer,request,"PLAYER_SLOT_EMPTY",0);return;}
        // Damage is derived from the HOST's game data for the shadow weapon type; the client never sends damage values.
        InvItemClass weaponClass;
        try{weaponClass=new InvItemClass(weapon.Type,weapon.Durability,weapon.Amount,(InvItem.ModifierQuality)weapon.Quality,weapon.Recipe);}
        catch(Exception){RejectAction(peer,request,"UNKNOWN_ITEM_TYPE",0);return;}
        if(weaponClass==null||weaponClass.baseClass==null||!weaponClass.baseClass.isMelee){RejectAction(peer,request,"NOT_MELEE",0);return;}
        var special=attack.AttackKind==2;
        var damage=weaponClass.getModdedDamage(special?weaponClass.baseClass.specialDamage:weaponClass.baseClass.damage);
        var barricadeDamage=weaponClass.getModdedDamage(special?weaponClass.baseClass.specialBarricadeDamage:weaponClass.baseClass.barricadeDamage);
        var durabilityDrain=weaponClass.getModdedDurabilityDrain(special?weaponClass.baseClass.specialDamageDurabilityDrain:weaponClass.baseClass.damageDurabilityDrain);
        if(damage<=0&&barricadeDamage<=0){RejectAction(peer,request,"NO_DAMAGE",0);return;}
        var dir=new Vector3(attack.DirX,0f,attack.DirZ);
        if(dir.sqrMagnitude<0.0001f)dir=Vector3.forward;else dir.Normalize();
        var target=ResolveMeleeTarget(pose,dir);
        if(target!=null)ApplyMeleeDamage(target,GetAttackAnchor(peer,pose).transform,damage,barricadeDamage,weaponClass.baseClass.canCutInHalf);
        if(durabilityDrain>0)shadow.DrainDurability(attack.FromHotbar,attack.SlotIndex,durabilityDrain);
        ulong resultValue=0;var resultPersistent=false;
        if(target!=null&&replication.TryGetId(target,out var hitId)){resultValue=hitId.Value;resultPersistent=hitId.IsPersistent;}
        var result=new ActionResultMessage(request.RequestId,request.Kind,resultValue,resultPersistent,0,ReplicationProtocolCodec.Encode(shadow.CaptureState()));
        RemoveEvictedAction(actionCache.Store(new NetworkActionResult(request.RequestId,true,new StateVersion(0),string.Empty)));acceptedActions++;
        cachedActionResults[request.RequestId]=result;cachedActionOwners[request.RequestId]=peer;
        Queue(peer,ProtocolMessageType.ActionResult,ReplicationProtocolCodec.Encode(result));
        if(target!=null){serverTick++;BroadcastEntityDelta(target);}
        log?.LogInfo($"主机已批准攻击 {request.RequestId}：玩家 {peer}，{(special?"特殊":"普通")}近战 {weapon.Type}，目标 {(target!=null?target.GetType().Name:"无")}，伤害 {damage}，消耗耐久 {durabilityDrain}。");
    }

    private void HandleDoorInteractRequest(int peer,ActionRequestMessage request)
    {
        var id=new EntityId(request.TargetValue,request.TargetPersistent);
        if(!replication.TryGetComponent(id,out var component)||!(component is Door door)){RejectAction(peer,request,"DOOR_NOT_FOUND",0);return;}
        if(!remotePlayerPositions.TryGetValue(peer,out var pose)){RejectAction(peer,request,"PLAYER_POSE_MISSING",0);return;}
        if(!ActionValidation.WithinDistance(pose.x,pose.y,pose.z,door.transform.position.x,door.transform.position.y,door.transform.position.z,InteractDistance)){RejectAction(peer,request,"TOO_FAR",0);return;}
        if(request.ExpectedRevision!=0&&replication.TryGetState(id,out var state)&&!ActionValidation.RevisionMatches(state.Revision,request.ExpectedRevision)){RejectAction(peer,request,"STALE_REVISION",state.Revision);SendEntityState(peer,door);return;}
        if(door.barricaded){RejectAction(peer,request,"DOOR_BARRICADED",0);return;}
        door.openClose(GetAttackAnchor(peer,pose).transform);
        AcceptInteract(peer,request,id,door,0);
        log?.LogInfo($"主机已批准开关门 {request.RequestId}：玩家 {peer}，门 {id}。");
    }

    private void HandleWindowInteractRequest(int peer,ActionRequestMessage request)
    {
        var id=new EntityId(request.TargetValue,request.TargetPersistent);
        if(!replication.TryGetComponent(id,out var component)||!(component is Window window)){RejectAction(peer,request,"WINDOW_NOT_FOUND",0);return;}
        if(!remotePlayerPositions.TryGetValue(peer,out var pose)){RejectAction(peer,request,"PLAYER_POSE_MISSING",0);return;}
        if(!ActionValidation.WithinDistance(pose.x,pose.y,pose.z,window.transform.position.x,window.transform.position.y,window.transform.position.z,InteractDistance)){RejectAction(peer,request,"TOO_FAR",0);return;}
        if(request.ExpectedRevision!=0&&replication.TryGetState(id,out var state)&&!ActionValidation.RevisionMatches(state.Revision,request.ExpectedRevision)){RejectAction(peer,request,"STALE_REVISION",state.Revision);SendEntityState(peer,window);return;}
        var interact=ReplicationProtocolCodec.DecodeInteract(request.Payload);
        window.barricade(interact.ValueA,true);
        AcceptInteract(peer,request,id,window,0);
        log?.LogInfo($"主机已批准封窗 {request.RequestId}：玩家 {peer}，窗 {id}，目标耐久 {interact.ValueA}。");
    }

    private void HandleItemActivateRequest(int peer,ActionRequestMessage request)
    {
        var id=new EntityId(request.TargetValue,request.TargetPersistent);
        if(!replication.TryGetComponent(id,out var component)||!(component is Item item)){RejectAction(peer,request,"ITEM_NOT_FOUND",0);return;}
        if(!remotePlayerPositions.TryGetValue(peer,out var pose)){RejectAction(peer,request,"PLAYER_POSE_MISSING",0);return;}
        if(!ActionValidation.WithinDistance(pose.x,pose.y,pose.z,item.transform.position.x,item.transform.position.y,item.transform.position.z,InteractDistance)){RejectAction(peer,request,"TOO_FAR",0);return;}
        if(request.ExpectedRevision!=0&&replication.TryGetState(id,out var state)&&!ActionValidation.RevisionMatches(state.Revision,request.ExpectedRevision)){RejectAction(peer,request,"STALE_REVISION",state.Revision);SendEntityState(peer,item);return;}
        if(item.destroyed||!item.gameObject.activeSelf){RejectAction(peer,request,"ITEM_UNAVAILABLE",0);return;}
        item.activate();
        AcceptInteract(peer,request,id,item,0);
        log?.LogInfo($"主机已批准物品开关 {request.RequestId}：玩家 {peer}，物品 {id}。");
    }

    private void AcceptInteract(int peer,ActionRequestMessage request,EntityId id,Component target,ulong revision)
    {
        var result=new ActionResultMessage(request.RequestId,request.Kind,id.Value,id.IsPersistent,revision,Array.Empty<byte>());
        RemoveEvictedAction(actionCache.Store(new NetworkActionResult(request.RequestId,true,new StateVersion(revision),string.Empty)));acceptedActions++;
        cachedActionResults[request.RequestId]=result;cachedActionOwners[request.RequestId]=peer;
        Queue(peer,ProtocolMessageType.ActionResult,ReplicationProtocolCodec.Encode(result));
        serverTick++;BroadcastEntityDelta(target);
    }

    private void SendEntityState(int peer,Component target)
    {
        var states=replication.CaptureEntities(new[]{target});
        if(states.Length==0)return;
        var delta=ReplicationProtocolCodec.Encode(new EntityDeltaMessage(CurrentScene,serverTick,states,Array.Empty<EntityStateWire>()));
        Queue(peer,ProtocolMessageType.EntityDelta,delta);
    }

    private void BroadcastEntityDelta(Component target)
    {
        var states=replication.CaptureEntities(new[]{target});
        if(states.Length==0)return;
        var delta=ReplicationProtocolCodec.Encode(new EntityDeltaMessage(CurrentScene,serverTick,states,Array.Empty<EntityStateWire>()));
        foreach(var readyPeer in readyPeers.ToArray())Queue(readyPeer,ProtocolMessageType.EntityDelta,delta);
    }

    private GameObject GetAttackAnchor(int peer,Vector3 pose)
    {
        if(!remoteAttackAnchors.TryGetValue(peer,out var anchor)||anchor==null)
        {
            anchor=new GameObject("RemoteAttackAnchor_"+peer);
            UnityEngine.Object.DontDestroyOnLoad(anchor);
            remoteAttackAnchors[peer]=anchor;
        }
        anchor.transform.position=pose;
        return anchor;
    }

    private void DestroyAttackAnchors()
    {
        foreach(var anchor in remoteAttackAnchors.Values)if(anchor!=null)UnityEngine.Object.Destroy(anchor);
        remoteAttackAnchors.Clear();
    }

    /// <summary>Approximates the game's MeleeSensor arc: nearest registered live entity within reach and in the facing half-cone.</summary>
    private Component? ResolveMeleeTarget(Vector3 pose,Vector3 dir)
    {
        Component? best=null;var bestScore=float.MaxValue;var reachSq=MeleeReach*MeleeReach;
        foreach(var pair in replication.AllEntities)
        {
            var c=pair.Value;if(c==null)continue;
            var p=c.transform.position;
            var dx=p.x-pose.x;var dy=p.y-pose.y;var dz=p.z-pose.z;
            var distSq=dx*dx+dy*dy+dz*dz;
            if(distSq>reachSq)continue;
            var flatLength=Mathf.Sqrt(dx*dx+dz*dz);
            var dot=flatLength>0.001f?(dir.x*dx+dir.z*dz)/flatLength:1f;
            if(dot<MeleeConeDot)continue;
            if(c is Character ch){if(!ch.alive||!ch.gameObject.activeSelf)continue;}
            else if(c is Item item){if(item.destroyed||!item.gameObject.activeSelf)continue;}
            else if(c is Door door){if(door.destroyed||!door.gameObject.activeSelf)continue;}
            else if(c is Window window){if(!window.gameObject.activeSelf)continue;}
            else continue;
            var score=distSq-dot*0.5f;
            if(score<bestScore){bestScore=score;best=c;}
        }
        return best;
    }

    private void ApplyMeleeDamage(Component target,Transform attacker,int damage,int barricadeDamage,bool canCutInHalf)
    {
        try
        {
            if(target is Character character)character.getHit(damage,attacker,canCutInHalf,true,true);
            else if(target is Door door)door.getHit(barricadeDamage,attacker);
            else if(target is Window window)window.getHit(barricadeDamage,attacker);
            else if(target is Item item)item.getHit(barricadeDamage,attacker);
        }
        catch(Exception error){log?.LogWarning($"Authoritative melee damage application failed for {target.GetType().Name}: {error.Message}");}
    }

    private static bool TryAddToContainer(Inventory inventory,DarkwoodPlayerInventoryShadow.Item source,int destinationSlotIndex)
    {
        if(inventory?.slots==null||string.IsNullOrEmpty(source.Type)||source.Amount<=0)return false;
        if(destinationSlotIndex>=0)
        {
            if(destinationSlotIndex>=inventory.slots.Count)return false;
            var destination=inventory.slots[destinationSlotIndex];if(destination==null)return false;
            var item=destination.invItem;
            if(item==null||InvItemClass.isNull(item))
            {
                if(source.Stackable&&source.Amount>Math.Max(1,source.MaxAmount))return false;
                destination.createItem(source.Type,source.Amount,source.Durability,(InvItem.ModifierQuality)source.Quality,source.Recipe);return true;
            }
            if(!source.Stackable||item.type!=source.Type||item.baseClass==null||!item.baseClass.stackable)return false;
            var capacity=Math.Max(0,Math.Max(1,item.baseClass.maxAmount)-item.amount);if(capacity<source.Amount)return false;
            var incomingExact=new InvItemClass(source.Type,source.Durability,source.Amount,(InvItem.ModifierQuality)source.Quality,source.Recipe);
            item.durability=InvItemClass.getStackedDurability(item,incomingExact,source.Amount);item.amount+=source.Amount;item.refresh();return true;
        }
        var remaining=source.Amount;
        if(source.Stackable)
        {
            foreach(var slot in inventory.slots)
            {
                var item=slot?.invItem;
                if(item==null||InvItemClass.isNull(item)||item.type!=source.Type||item.baseClass==null||!item.baseClass.stackable)continue;
                remaining-=Math.Max(0,Math.Max(1,item.baseClass.maxAmount)-item.amount);
                if(remaining<=0)break;
            }
        }
        if(remaining>0)
        {
            var empty=0;foreach(var slot in inventory.slots)if(slot!=null&&InvItemClass.isNull(slot.invItem))empty++;
            var perSlot=source.Stackable?Math.Max(1,source.MaxAmount):source.Amount;
            if((long)empty*perSlot<remaining)return false;
        }

        remaining=source.Amount;
        var incoming=new InvItemClass(source.Type,source.Durability,source.Amount,(InvItem.ModifierQuality)source.Quality,source.Recipe);
        if(source.Stackable)
        {
            foreach(var slot in inventory.slots)
            {
                var item=slot?.invItem;
                if(remaining<=0)break;
                if(item==null||InvItemClass.isNull(item)||item.type!=source.Type||item.baseClass==null||!item.baseClass.stackable)continue;
                var amount=Math.Min(remaining,Math.Max(0,Math.Max(1,item.baseClass.maxAmount)-item.amount));
                if(amount<=0)continue;
                item.durability=InvItemClass.getStackedDurability(item,incoming,amount);item.amount+=amount;item.refresh();remaining-=amount;
            }
        }
        foreach(var slot in inventory.slots)
        {
            if(remaining<=0)break;
            if(slot==null||!InvItemClass.isNull(slot.invItem))continue;
            var amount=source.Stackable?Math.Min(remaining,Math.Max(1,source.MaxAmount)):remaining;
            slot.createItem(source.Type,amount,source.Durability,(InvItem.ModifierQuality)source.Quality,source.Recipe);remaining-=amount;
        }
        return remaining==0;
    }

    private void RejectAction(int peer,ActionRequestMessage request,string error,ulong revision)
    {
        var result=new NetworkActionResult(request.RequestId,false,new StateVersion(revision),error);RemoveEvictedAction(actionCache.Store(result));rejectedActions++;
        var rejected=new ActionRejectedMessage(request.RequestId,request.Kind,request.TargetValue,request.TargetPersistent,revision,error);cachedActionRejections[request.RequestId]=rejected;
        cachedActionOwners[request.RequestId]=peer;
        Queue(peer,ProtocolMessageType.ActionRejected,ReplicationProtocolCodec.Encode(rejected));
        log?.LogWarning($"Action rejected {request.RequestId}: peer {peer}, kind {request.Kind}, {error}, revision {revision}.");
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
        if(result.Payload.Length>0)ApplyPlayerInventory(ReplicationProtocolCodec.DecodePlayerInventoryState(result.Payload));
        log?.LogInfo($"已应用主机权威操作结果：请求 {result.RequestId}，类型 {result.Kind}，目标 {result.TargetValue:X16}，版本 {result.Revision}。");
    }

    private static void ApplyPlayerInventory(PlayerInventoryStatePayload state)
    {
        var player=Player.Instance;if(player?.Inventory==null||player.Hotbar==null)throw new InvalidOperationException("客户端玩家库存不可用。");
        DarkwoodInventoryAdapter.Apply(player.Inventory,ToDarkwoodSlots(state.Backpack));
        DarkwoodInventoryAdapter.Apply(player.Hotbar,ToDarkwoodSlots(state.Hotbar));
        player.refreshRecipes();
    }

    private static DarkwoodInventorySlot[] ToDarkwoodSlots(InventorySlotWire[] slots)
    {
        var result=new DarkwoodInventorySlot[slots.Length];for(var i=0;i<slots.Length;i++){var s=slots[i];result[i]=new DarkwoodInventorySlot{Type=s.Type,Amount=s.Amount,Durability=s.Durability,Quality=s.Quality,Recipe=s.Recipe};}return result;
    }

    private void HandleActionRejected(ActionRejectedMessage rejected)
    {
        if(!pendingActions.Remove(rejected.RequestId))return;
        log?.LogWarning($"主机拒绝联机操作 {rejected.RequestId}：{rejected.ErrorCode}，主机版本 {rejected.CurrentRevision}。");
        Player.Instance?.displayMessage("联机操作被主机拒绝："+rejected.ErrorCode);
    }

    private void SendInventory(int peer,InventoryStateMessage inventory)=>Queue(peer,ProtocolMessageType.InventoryState,ReplicationProtocolCodec.Encode(inventory));
    private void BroadcastInventory(InventoryStateMessage inventory){var payload=ReplicationProtocolCodec.Encode(inventory);foreach(var readyPeer in readyPeers.ToArray())Queue(readyPeer,ProtocolMessageType.InventoryState,payload);}

    private void Queue(int peer,ProtocolMessageType type,byte[] payload,string transferLabel="",int chunkIndex=-1,int chunkCount=0){if(!outgoing.TryGetValue(peer,out var queue))outgoing[peer]=queue=new Queue<OutgoingPacket>();queue.Enqueue(new OutgoingPacket{Type=type,Payload=payload,TransferLabel=transferLabel,ChunkIndex=chunkIndex,ChunkCount=chunkCount});}
    private void PumpOutgoing()
    {
        if(hostSession==null)return;
        foreach(var peer in outgoing.Keys.ToArray())
        {
            var queue=outgoing[peer];var normalBudget=16;var bulkSent=false;
            while(queue.Count>0&&normalBudget>0)
            {
                var next=queue.Peek();
                // Send at most one save/snapshot block per rendered frame. This
                // keeps Telepathy's writer queue bounded on slower Radmin links.
                if(next.IsBulk&&bulkSent)break;
                var p=queue.Dequeue();hostSession.SendMessage(peer,p.Type,p.Payload);
                if(p.IsBulk)
                {
                    bulkSent=true;
                    var sent=p.ChunkIndex+1;var percent=(int)(sent*100f/p.ChunkCount);
                    TransferProgress=$"正在向玩家 {peer} 发送{p.TransferLabel}：{sent}/{p.ChunkCount}（{percent}%）";
                    var interval=Math.Max(1,p.ChunkCount/10);
                    if(sent==1||sent==p.ChunkCount||(sent%interval)==0)log?.LogInfo(TransferProgress);
                }
                else normalBudget--;
            }
            if(queue.Count==0){outgoing.Remove(peer);log?.LogInfo($"已完成向玩家 {peer} 发送排队数据。");}
        }
    }
    private void SendLocalPose(){if(!DarkwoodPlayerAdapter.TryCapture(out var p)||clientSession==null)return;clientSession.Send(ProtocolMessageType.PlayerPose,ReplicationProtocolCodec.Encode(new PlayerPoseMessage(clientSession.PeerId,++poseSequence,p.Scene,p.Position.x,p.Position.y,p.Position.z,p.Rotation.x,p.Rotation.y,p.Rotation.z,p.Rotation.w,p.Flags,p.TorsoClip,p.TorsoFrame,p.LegsClip,p.LegsFrame)));}
    private void SendHostPose(int peer){if(!DarkwoodPlayerAdapter.TryCapture(out var p))return;Queue(peer,ProtocolMessageType.PlayerPose,ReplicationProtocolCodec.Encode(new PlayerPoseMessage(0,++poseSequence,p.Scene,p.Position.x,p.Position.y,p.Position.z,p.Rotation.x,p.Rotation.y,p.Rotation.z,p.Rotation.w,p.Flags,p.TorsoClip,p.TorsoFrame,p.LegsClip,p.LegsFrame)));}
    private void SendHostPose(){foreach(var peer in readyPeers.ToArray())SendHostPose(peer);}
    private void FailClient(string code,Exception error){if(sessionError.Length>0)return;sessionError=code+": "+error.Message;log?.LogError($"Standalone DMF session failed [{code}]: {error}");try{clientSession?.Fail(sessionError);}catch{}SetState(ConnectionState.Failed);}
    private string HostKey(){using var sha=System.Security.Cryptography.SHA256.Create();var value=(addressConfig?.Value??"host")+":"+Port;return BitConverter.ToString(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(value)),0,8).Replace("-","");}
}
