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
using HarmonyLib;
using Steamworks;
using UnityEngine;
using UnityEngine.SceneManagement;


namespace DarkwoodMultiplayerFramework.DarkwoodAdapter;


/// <summary>Darkwood-specific boundary. It owns scene/player discovery while protocol logic stays in src modules.</summary>
public sealed partial class DarkwoodAdapterRuntime : MonoBehaviour
{
    public static DarkwoodAdapterRuntime? Instance { get; private set; }
    public bool ClientSaveLoadPending { get; private set; }
    public static void LogMessage(string message) => Instance?.log?.LogInfo(message);
    public ConnectionState State { get; private set; } = ConnectionState.Disconnected;
    public string StateDisplay => StateText(State);
    public string CurrentScene => SceneManager.GetActiveScene().name;
    public Player? LocalPlayer => Player.Instance;
    public int RegistryCount => registry?.Count ?? 0;
    public string RegistryDigest { get; private set; } = string.Empty;
    public bool IsHost => Session.IsHost;
    public bool IsClient => Session.IsClient;
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
    public bool IsMultiplayerActive => hostSession?.IsActive == true || clientSession?.HandshakeComplete == true;
    public bool AllDowned => DarkwoodDownedPatch.AllDowned;
    public RescueProgressMessage LastRescueProgress => lastRescueProgress;
    public bool TryGetKnownPlayerPosition(int playerId, out Vector3 position)
    {
        var myId = IsHost ? 0 : (clientSession?.PeerId ?? -1);
        if (playerId == myId) { var player = Player.Instance; if (player != null) { position = player.transform.position; return true; } position = Vector3.zero; return false; }
        if (IsHost && remotePlayerPositions.TryGetValue(playerId, out position)) return true;
        if (remotePlayers.TryGetPosition(playerId, out position)) return true;
        position = Vector3.zero;
        return false;
    }
    public event Action<string>? SceneChanged;
    public event Action<ConnectionState>? StateChanged;

    private readonly DarkwoodEntityScanner scanner = new DarkwoodEntityScanner();
    /// <summary>0.8.9 第二刀：会话上下文（角色/状态/身份/场景的权威状态源）。</summary>
    public SessionContext Session { get; } = new SessionContext();
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
    /// <summary>0.8.8 自测：Telepathy 传输 DLL 路径（供回环自测客户端复用）。</summary>
    public string TelepathyPath => telepathyPath;
    private ConfigEntry<string>? addressConfig;
    private ConfigEntry<int>? portConfig;
    private ConfigEntry<int>? playerCountConfig;
    private bool f1WasDown;
    private bool f2WasDown;
    private bool f3WasDown;
    private readonly DarkwoodEntityReplication replication = new DarkwoodEntityReplication();
    /// <summary>0.8.9-alpha.1：实体 ID 反查（容器并发补偿用）。</summary>
    public bool TryGetEntityId(Component component,out EntityId id)=>replication.TryGetId(component,out id);
    private readonly DarkwoodRemotePlayers remotePlayers = new DarkwoodRemotePlayers();
    private readonly Dictionary<int, Queue<OutgoingPacket>> outgoing = new Dictionary<int, Queue<OutgoingPacket>>();
    private readonly HashSet<int> readyPeers = new HashSet<int>();
    private ChunkTransferAssembler? incomingSave;
    private SaveTransferManifest incomingSaveManifest;
    private ChunkTransferAssembler? incomingSnapshot;
    private WorldSnapshotManifest incomingSnapshotManifest;
    private long serverTick;
    /// <summary>0.8.8-alpha.2：运行时实体注册表（与持久实体注册表分离；ID 会话内单调递增）。</summary>
    private readonly RuntimeEntityRegistry runtimeRegistry = new RuntimeEntityRegistry();
    private float nextDelta;
    private float nextInventoryDelta;
    /// <summary>0.8.8-alpha.3：主机侧运行时可搜刮容器映射（组件 → runtime ID）。</summary>
    private readonly Dictionary<Inventory, ulong> runtimeInventoryIds = new Dictionary<Inventory, ulong>();
    /// <summary>0.8.8-alpha.3：客户端侧运行时容器镜像（runtime ID → 实例化 Transform）。</summary>
    private readonly Dictionary<ulong, Transform> runtimeInventoryMirrors = new Dictionary<ulong, Transform>();
    private float nextRuntimeScan;
    /// <summary>0.8.8-alpha.6：场景切换自动重连时刻（>0 表示待重连）。</summary>
    private float autoReconnectAt;
    /// <summary>0.8.8-alpha.3：待触发的随机事件（runtime ID → Spawn 消息），等客户端进入范围才单播。</summary>
    private readonly Dictionary<ulong, RuntimeEntitySpawnMessage> pendingRuntimeEvents = new Dictionary<ulong, RuntimeEntitySpawnMessage>();
    /// <summary>0.8.8-alpha.4：主机侧运行时敌人映射（Character → runtime ID）。</summary>
    private readonly Dictionary<Character, ulong> runtimeEnemyIds = new Dictionary<Character, ulong>();
    /// <summary>0.8.8-alpha.4：客户端侧运行时敌人代理（runtime ID → Character）。</summary>
    private readonly Dictionary<ulong, Character> runtimeEnemyMirrors = new Dictionary<ulong, Character>();
    /// <summary>0.8.8-alpha.3：随机事件一次性派发跟踪（每个事件对每个玩家最多触发一次）。</summary>
    private readonly RuntimeEventDispatch runtimeEventDispatch = new RuntimeEventDispatch();
    /// <summary>0.8.8-alpha.3：随机事件动画触发范围（XZ 平面距离，米）。客户端进入该范围才触发。</summary>
    private const float RuntimeEventTriggerRange = 35f;
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
    private readonly Dictionary<int,GuestProfileRecord> peerGuestRecords = new Dictionary<int,GuestProfileRecord>();
    private readonly Dictionary<int,string> peerGuestKeys = new Dictionary<int,string>();
    private DarkwoodGuestProfiles? guestProfiles;
    private ConfigEntry<string>? playerNameConfig;
    private ConfigEntry<string>? starterKitTier1Config;
    private ConfigEntry<string>? starterKitTier2Config;
    private ConfigEntry<string>? starterKitTier3Config;
    private ConfigEntry<int>? starterKitTier2DayConfig;
    private ConfigEntry<int>? starterKitTier3DayConfig;
    private ConfigEntry<bool>? autoSelfTestConfig;
    /// <summary>0.8.8 自测：自动回环自测开关（配置 SelfTestAuto）。</summary>
    public bool AutoSelfTest => autoSelfTestConfig?.Value ?? false;
    private float nextProfileAutosave;
    private const float ProfileAutosaveSeconds = 30f;
    private readonly Dictionary<int,float> peerHealths = new Dictionary<int,float>();
    private readonly Dictionary<int,float> peerMaxHealths = new Dictionary<int,float>();
    private readonly Dictionary<int,bool> peerDowned = new Dictionary<int,bool>();
    private readonly Dictionary<int,float> nextGuestHitAllowed = new Dictionary<int,float>();
    private bool hostDownedLocal;
    private bool allDownedHandled;
    private float scheduledStopAt;
    private float nextMonsterDamageScan;
    private float nextHealthHeartbeat;
    private float lastBroadcastHostHealth = float.MaxValue;
    private float nextRescueBroadcast;
    private float localInvulUntil;
    private bool rescueLockedByMe;
    private RescueSession? activeRescue;
    private RescueProgressMessage lastRescueProgress;
    private const float RescueDurationSeconds = 3f;
    private const float RescueRange = 4f;
    private const float ReviveHealthFraction = 0.1f;
    private const float MonsterDamageScanInterval = 0.25f;
    private const float MonsterHitCooldown = 0.5f;
    private const float MonsterReach = 1.6f;
    private const float ReviveInvulnerableSeconds = 3f;
    private const float PostDownedEndingDelay = 2f;

    private sealed class RescueSession
    {
        public int TargetId;
        public int RescuerId;
        public float StartedAt;
    }
    private long acceptedActions;
    private long rejectedActions;
    private long duplicateActions;
    private bool clientSnapshotReady;
    private bool clientRegistryRequestSent;
    private bool clientSnapshotManifestReceived;
    private bool clientRegistryStabilized;
    private float loadStartedAt;
    private int lastLoadBucket;
    private float nextRegistryRequestRetry;
    private WorldSnapshotApplied? lastSnapshotApplied;
    private float nextSnapshotAckRetry;
    private int snapshotAckRetryCount;
    private readonly Dictionary<int,float> nextAttackAllowed = new Dictionary<int,float>();
    private readonly Dictionary<int,GameObject> remoteAttackAnchors = new Dictionary<int,GameObject>();
    /// <summary>FIX-007：快照/增量中无法绑定的实体 ID（主机运行时生成物，客户端世界无副本）。
    /// 对这些 ID 的后续库存/状态消息静默忽略，等待 0.8.8 的 Spawn 生命周期补发。</summary>
    private readonly HashSet<EntityId> missingEntities = new HashSet<EntityId>();
    private const float AttackCooldownSeconds = 0.35f;
    private const float MeleeReach = 1.6f;
    private const float MeleeConeDot = 0.3f;

    private sealed class OutgoingPacket
    {
        public ProtocolMessageType Type;
        public byte[] Payload = Array.Empty<byte>();
        public string TransferLabel = string.Empty;
        public int ChunkIndex = -1;
        public int ChunkCount;
        public bool IsBulk => ChunkIndex >= 0 && ChunkCount > 0;
    }

    private ProtocolIdentity Identity => new ProtocolIdentity(ProtocolVersions.Framework, Application.version);

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
        playerNameConfig = config.Bind("Gameplay", "PlayerName", string.Empty, "访客身份（主机用它跨热加入保存你的物品）。留空自动生成唯一随机名。");
        if (string.IsNullOrWhiteSpace(playerNameConfig.Value)) { playerNameConfig.Value = "Guest" + Guid.NewGuid().ToString("N").Substring(0, 4); playerNameConfig.ConfigFile.Save(); }
        starterKitTier2DayConfig = config.Bind("Gameplay", "GuestStarterKitTier2Day", 3, "从第几天起发放第二档访客初始装备。");
        starterKitTier3DayConfig = config.Bind("Gameplay", "GuestStarterKitTier3Day", 7, "从第几天起发放第三档访客初始装备。");
        starterKitTier1Config = config.Bind("Gameplay", "GuestStarterKitTier1", "", "新访客首次加入的初始装备（分号分隔的 物品类型:数量；留空不发装备）。");
        starterKitTier2Config = config.Bind("Gameplay", "GuestStarterKitTier2", "", "第二档访客初始装备（仅首次加入，按天数选档）。");
        starterKitTier3Config = config.Bind("Gameplay", "GuestStarterKitTier3", "", "第三档访客初始装备（仅首次加入，按天数选档）。");
        autoSelfTestConfig = config.Bind("Gameplay", "SelfTestAuto", false, "启动后自动执行回环自测：自动开主机 → 自动读档 → 主机 READY 后自动连接 127.0.0.1 回环客户端并跑完整协议链（本地验证用，正常联机请保持 false）。");
        guestProfiles = new DarkwoodGuestProfiles(log, starterKitTier2DayConfig.Value, starterKitTier3DayConfig.Value, starterKitTier1Config.Value, starterKitTier2Config.Value, starterKitTier3Config.Value);
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
        hostSession.MaxPeers = Math.Max(0, ConfiguredPlayerCount - 1);
        peerGuestKeys.Clear(); peerGuestRecords.Clear();
        hostSession.Start(Port);
        Session.Role = MultiplayerRole.Host;
        Session.SessionId = Guid.NewGuid();
        Session.LocalPeerId = 0;
        Session.Scene = CurrentScene;
        Session.Error = string.Empty;
        Session.IsMultiplayerActive = true;
        log?.LogInfo($"主机正在监听 TCP 端口 {Port}（访客上限 {hostSession.MaxPeers}，联机人数 {ConfiguredPlayerCount}）。");
    }

    public void ConnectClient()
    {
        StopNetwork();
        clientSession = new ClientHandshakeSession(new TelepathyClientTransport(telepathyPath), Identity);
        clientSession.HandshakeSucceeded += OnHandshakeSucceeded;
        clientSession.HandshakeFailed += OnHandshakeFailed;
        clientSession.MessageReceived += OnClientMessage;
        clientSession.GuestKey = NormalizeGuestKey(playerNameConfig?.Value);
        clientSession.Connect(addressConfig?.Value ?? "127.0.0.1", Port);
        Session.Role = MultiplayerRole.Client;
        Session.LocalPeerId = -1;
        Session.Error = string.Empty;
        log?.LogInfo($"客户端正在连接 {addressConfig?.Value ?? "127.0.0.1"}:{Port}（身份 {clientSession.GuestKey}）。");
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
        if (hostSession != null && guestProfiles != null) foreach (var peer in readyPeers.ToArray()) PersistGuestProfile(peer);
        if (clientSession != null) { clientSession.Dispose(); clientSession = null; }
        if (hostSession != null) { hostSession.Dispose(); hostSession = null; }
        if(hostLootScaleCoroutine!=null){StopCoroutine(hostLootScaleCoroutine);hostLootScaleCoroutine=null;}
        outgoing.Clear(); readyPeers.Clear(); sentSaves.Clear(); sentSnapshots.Clear(); pendingSnapshotRequests.Clear(); pendingActions.Clear();remotePlayerPositions.Clear();remoteInventories.Clear();peerGuestKeys.Clear();peerGuestRecords.Clear();peerHealths.Clear();peerMaxHealths.Clear();peerDowned.Clear();nextGuestHitAllowed.Clear();actionCache.Clear();cachedActionResults.Clear();cachedActionRejections.Clear();cachedActionOwners.Clear();missingEntities.Clear();incomingSave=null; incomingSnapshot=null; TransferProgress=string.Empty; clientSnapshotReady=false; clientRegistryRequestSent=false; clientSnapshotManifestReceived=false; clientRegistryStabilized=false; loadStartedAt=0f; lastLoadBucket=0; ClientSaveLoadPending=false; nextRegistryRequestRetry=0f; lastSnapshotApplied=null; nextSnapshotAckRetry=0f; snapshotAckRetryCount=0; nextInventoryDelta=0f; nextProfileAutosave=0f; hostLootScaleScanComplete=false; hostLootScaleScanStarted=false; nextAttackAllowed.Clear(); DestroyAttackAnchors(); replication.RestoreSimulation(); remotePlayers.Clear(); ActiveClientSaveDirectory=string.Empty; sessionError=string.Empty; activeRescue=null; hostDownedLocal=false; allDownedHandled=false; scheduledStopAt=0f; nextMonsterDamageScan=0f; nextHealthHeartbeat=0f; lastBroadcastHostHealth=float.MaxValue; nextRescueBroadcast=0f; localInvulUntil=0f; rescueLockedByMe=false; lastRescueProgress=default; DarkwoodDownedPatch.Reset();
 Session.Reset();
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
        RegisterMessageHandlers(); // 0.8.9：消息路由处理器注册
        SceneManager.sceneLoaded += OnSceneLoaded;
    }

    public void Update()
    {
        PollHotkeys();
        try { hostSession?.Tick(); clientSession?.Tick(); }
        catch (Exception error) { FailClient("TRANSPORT_TICK_FAILED",error); }
        PumpOutgoing();
        remotePlayers.Tick();
        var scene = CurrentScene;
        if (!string.Equals(scene, lastScene, StringComparison.Ordinal)) MarkSceneChanged(scene);

        SetState(DetectState());
        if (registryDirty && IsNetworkConnected() && Player.Instance != null)
        {
            RebuildRegistry();
            SetState(DetectState());
        }
        // 0.8.9 第七刀：主机/客户端周期逻辑分离（单一入口，不再 if/else 交错）。
        if (Session.IsHost) TickHost();
        else if (Session.IsClient) TickClient();
        PollRescueHotkey();
        if(scheduledStopAt>0f&&Time.unscaledTime>=scheduledStopAt){scheduledStopAt=0f;StopNetwork();}
        if(localInvulUntil>0f&&Time.unscaledTime>=localInvulUntil){localInvulUntil=0f;var player=Player.Instance;if(player!=null)player.invulnerable=false;}
    }

    private void TickHost()
    {
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
        if(hostSession!=null&&readyPeers.Count>0&&Time.unscaledTime>=nextProfileAutosave){nextProfileAutosave=Time.unscaledTime+ProfileAutosaveSeconds;foreach(var peer in readyPeers.ToArray())PersistGuestProfile(peer);}
        if(hostSession!=null){ScanMonsterDamage();SyncHostHealth();TickRescue();ScanRuntimeLootContainers();}
    }

    private void TickClient()
    {
        if(clientSession?.Session.Lifecycle.State==ConnectionState.Ready)replication.Interpolate(Time.unscaledDeltaTime*12f);
        TrySendClientRegistryReady();
        RetrySnapshotAcknowledgement();
        if(clientSession?.Session.Lifecycle.State==ConnectionState.Ready&&Time.unscaledTime>=nextPose){nextPose=Time.unscaledTime+(1f/15f);SendLocalPose();}
        if(clientSession!=null&&clientSession.Session.Lifecycle.State==ConnectionState.LoadingSave&&loadStartedAt>0f&&Time.unscaledTime-loadStartedAt>300f)FailClient("SAVE_LOAD_TIMEOUT",new TimeoutException("存档加载超时（300 秒未完成）。请检查主机存档是否损坏。"));
        if(clientSession!=null&&clientSession.Session.Lifecycle.State==ConnectionState.LoadingSave){var worldGen=Singleton<WorldGenerator>.Instance;if(worldGen!=null){var percent=(int)worldGen.percentLoaded;TransferProgress=$"正在加载存档…({percent}%)";var bucket=percent/10;if(bucket>lastLoadBucket){lastLoadBucket=bucket;LogMessage($"存档加载进度 {percent}%（已用 {Time.unscaledTime-loadStartedAt:F0} 秒）。");}}}
        if(autoReconnectAt>0f&&Time.unscaledTime>=autoReconnectAt){autoReconnectAt=0f;log?.LogInfo("场景切换自动重连：正在重新连接主机……");ConnectClient();}
    }

    private void TrySendClientRegistryReady()
    {
        if (clientSnapshotReady || clientSnapshotManifestReceived || clientSession == null || !clientSession.HandshakeComplete || registryDirty || registry == null || Player.Instance == null) return;
        if (!clientRegistryStabilized) return; // 等待注册表稳定化循环完成（世界流式加载）
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
        // 0.8.8-alpha.6：场景切换——主机通知所有客户端自动重连（重连走完整握手+新场景存档加载），
        // 并重置运行时实体状态（新场景是全新的运行时世界；Runtime ID 计数器继续单调递增、绝不复用）。
        if (hostSession != null && (scene.Equals("chapter1", StringComparison.Ordinal) || scene.Equals("chapter2", StringComparison.Ordinal)))
        {
            var payload = ReplicationProtocolCodec.Encode(new SceneChangeMessage(scene));
            var notified = 0;
            foreach (var readyPeer in readyPeers.ToArray()) { Queue(readyPeer, ProtocolMessageType.SceneChange, payload); notified++; }
            pendingRuntimeEvents.Clear();
            runtimeInventoryIds.Clear();
            runtimeEnemyIds.Clear();
            runtimeEventDispatch.Clear();
            runtimeRegistry.ClearAlive();
            if (notified > 0) log?.LogInfo($"主机场景已切换：{scene}，已通知 {notified} 个客户端自动重连。");
        }
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
        Session.State = next; // 0.8.9：SessionContext 同步
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
}
