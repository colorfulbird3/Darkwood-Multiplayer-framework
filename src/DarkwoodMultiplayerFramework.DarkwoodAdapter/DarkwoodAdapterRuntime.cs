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
public sealed class DarkwoodAdapterRuntime : MonoBehaviour
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
    private const float RescueRange = 2.5f;
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
        if(clientSession!=null&&clientSession.Session.Lifecycle.State==ConnectionState.LoadingSave&&loadStartedAt>0f&&Time.unscaledTime-loadStartedAt>300f)FailClient("SAVE_LOAD_TIMEOUT",new TimeoutException("存档加载超时（300 秒未完成）。请检查主机存档是否损坏。"));
        if(clientSession!=null&&clientSession.Session.Lifecycle.State==ConnectionState.LoadingSave){var worldGen=Singleton<WorldGenerator>.Instance;if(worldGen!=null){var percent=(int)worldGen.percentLoaded;TransferProgress=$"正在加载存档…({percent}%)";var bucket=percent/10;if(bucket>lastLoadBucket){lastLoadBucket=bucket;LogMessage($"存档加载进度 {percent}%（已用 {Time.unscaledTime-loadStartedAt:F0} 秒）。");}}}
        if(hostSession!=null&&readyPeers.Count>0&&Time.unscaledTime>=nextProfileAutosave){nextProfileAutosave=Time.unscaledTime+ProfileAutosaveSeconds;foreach(var peer in readyPeers.ToArray())PersistGuestProfile(peer);}
        PollRescueHotkey();
        if(hostSession!=null){ScanMonsterDamage();SyncHostHealth();TickRescue();ScanRuntimeLootContainers();}
        if(scheduledStopAt>0f&&Time.unscaledTime>=scheduledStopAt){scheduledStopAt=0f;StopNetwork();}
        if(autoReconnectAt>0f&&Time.unscaledTime>=autoReconnectAt){autoReconnectAt=0f;log?.LogInfo("场景切换自动重连：正在重新连接主机……");ConnectClient();}
        if(localInvulUntil>0f&&Time.unscaledTime>=localInvulUntil){localInvulUntil=0f;var player=Player.Instance;if(player!=null)player.invulnerable=false;}
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
    private void OnPeerAccepted(int connectionId)
    {
        var key = "player";
        if (hostSession != null && hostSession.TryGetPeerGuestKey(connectionId, out var guestKey) && !string.IsNullOrWhiteSpace(guestKey)) key = NormalizeGuestKey(guestKey);
        peerGuestKeys[connectionId] = key;
        log?.LogInfo($"已接受玩家连接：{connectionId}（身份 {key}）。");
    }
    private void OnPeerRejected(int connectionId, string error) => log?.LogWarning($"已拒绝玩家连接 {connectionId}：{error}");
    private void OnPeerDisconnected(int connectionId)
    {
        PersistGuestProfile(connectionId);
        if(activeRescue!=null&&(activeRescue.TargetId==connectionId||activeRescue.RescuerId==connectionId)){var rescueTarget=activeRescue.TargetId;var rescueRescuer=activeRescue.RescuerId;activeRescue=null;BroadcastRescueProgress(rescueTarget,rescueRescuer,0f,false);}
        outgoing.Remove(connectionId);readyPeers.Remove(connectionId);sentSaves.Remove(connectionId);sentSnapshots.Remove(connectionId);pendingSnapshotRequests.Remove(connectionId);remotePlayerPositions.Remove(connectionId);remoteInventories.Remove(connectionId);remotePlayers.Remove(connectionId);nextAttackAllowed.Remove(connectionId);peerGuestKeys.Remove(connectionId);peerGuestRecords.Remove(connectionId);peerHealths.Remove(connectionId);peerMaxHealths.Remove(connectionId);peerDowned.Remove(connectionId);nextGuestHitAllowed.Remove(connectionId);if(remoteAttackAnchors.TryGetValue(connectionId,out var anchor)){if(anchor!=null)UnityEngine.Object.Destroy(anchor);remoteAttackAnchors.Remove(connectionId);}
        CheckAllDowned();
    }

    private void OnHostMessage(int peer,ProtocolEnvelope envelope)
    {
        try
        {
            if(envelope.MessageType==ProtocolMessageType.SaveTransferRequest){ReplicationProtocolCodec.DecodeSaveTransferRequest(envelope.Payload);PrepareSave(peer);}
            else if(envelope.MessageType==ProtocolMessageType.SaveTransferApplied){var applied=ReplicationProtocolCodec.DecodeSaveTransferApplied(envelope.Payload);if(!sentSaves.TryGetValue(peer,out var expected)||expected!=applied.TransferId)throw new InvalidDataException("Save acknowledgement does not match active transfer.");log?.LogInfo($"Peer {peer} installed verified save {applied.TransferId}.");}
            else if(envelope.MessageType==ProtocolMessageType.Ready){var ready=ReplicationProtocolCodec.DecodeReady(envelope.Payload);PrepareSnapshot(peer,ready);}
            else if(envelope.MessageType==ProtocolMessageType.WorldSnapshotApplied)
            {
                var applied=ReplicationProtocolCodec.DecodeWorldSnapshotApplied(envelope.Payload);
                if(!sentSnapshots.TryGetValue(peer,out var expected)||expected!=applied.SnapshotId||applied.Scene!=CurrentScene||applied.RegistryDigest!=RegistryDigest)throw new InvalidDataException("Snapshot acknowledgement does not match active snapshot.");
                var firstReady=readyPeers.Add(peer);
                if(firstReady)
                {
                    // Every joining peer gets its own shadow inventory restored from its guest profile (hot join).
                    var key=peerGuestKeys.TryGetValue(peer,out var guestKey)?guestKey:"player";
                    var hostPosition=Player.Instance!=null?Player.Instance.transform.position:Vector3.zero;
                    var day=global::Core.currentProfile?.day??0;
                    var record=new GuestProfileRecord(key,day,1,0f,0f,0f,Array.Empty<InventorySlotWire>(),Array.Empty<InventorySlotWire>(),DateTime.UtcNow.Ticks);
                    var spawn=hostPosition;
                    if(guestProfiles!=null)record=guestProfiles.Resolve(HostSaveToken(),key,day,hostPosition,out spawn);
                    // 0.8.8-alpha.5：无论档案记录如何，客户端始终在游戏默认出生点（playerBase 的 playerSpawn）出生，不在主机位置出生。
                    spawn=DefaultSpawnPoint();
                    var shadow=DarkwoodPlayerInventoryShadow.FromRecord(record,message=>log?.LogWarning(message));
                    if(record.JoinCount==1)shadow.AddStarterKit(guestProfiles?.KitForDay(day),message=>log?.LogWarning(message));
                    remoteInventories[peer]=shadow;
                    peerGuestRecords[peer]=record;
                    var hostMaxHealth=Player.Instance!=null?Player.Instance.maxHealth:100f;
                    peerHealths[peer]=hostMaxHealth;peerMaxHealths[peer]=hostMaxHealth;peerDowned[peer]=false;
                    Queue(peer,ProtocolMessageType.GuestProfile,ReplicationProtocolCodec.Encode(new GuestProfileMessage(shadow.CaptureState(),spawn.x,spawn.y,spawn.z,record.Day,record.JoinCount,hostMaxHealth,hostMaxHealth,false)));
                    PersistGuestProfile(peer);
                    log?.LogInfo($"Peer {peer} guest profile resolved: {key}, day {record.Day}, join {record.JoinCount}, spawn ({spawn.x:F1},{spawn.y:F1},{spawn.z:F1}).");
                }
                Queue(peer,ProtocolMessageType.Ready,ReplicationProtocolCodec.Encode(new ReadyMessage(CurrentScene,RegistryDigest)));
                SendHostPose(peer);
                log?.LogInfo(firstReady?$"Peer {peer} READY after applying snapshot {applied.SnapshotId}, {applied.EntityCount} entities.":$"Peer {peer} repeated snapshot acknowledgement {applied.SnapshotId}; Ready confirmation resent.");
            }
            else if(envelope.MessageType==ProtocolMessageType.PlayerPose){var pose=ReplicationProtocolCodec.DecodePlayerPose(envelope.Payload);if(!readyPeers.Contains(peer)||pose.Scene!=CurrentScene)return;peerMaxHealths[peer]=pose.MaxHealth;pose=new PlayerPoseMessage(peer,pose.Sequence,CurrentScene,pose.X,pose.Y,pose.Z,pose.Qx,pose.Qy,pose.Qz,pose.Qw,pose.MaxHealth,pose.Flags,pose.TorsoClip,pose.TorsoFrame,pose.LegsClip,pose.LegsFrame);remotePlayerPositions[peer]=new Vector3(pose.X,pose.Y,pose.Z);remotePlayers.Apply(pose,0);var payload=ReplicationProtocolCodec.Encode(pose);foreach(var readyPeer in readyPeers.ToArray())if(readyPeer!=peer)Queue(readyPeer,ProtocolMessageType.PlayerPose,payload);}
            else if(envelope.MessageType==ProtocolMessageType.ActionRequest)HandleActionRequest(peer,ReplicationProtocolCodec.DecodeActionRequest(envelope.Payload));
            else if(envelope.MessageType==ProtocolMessageType.InventoryState){var inventory=ReplicationProtocolCodec.DecodeInventoryState(envelope.Payload);if(!replication.Apply(inventory)){missingEntities.Add(new EntityId(inventory.Value,inventory.Persistent));log?.LogWarning($"忽略缺失实体的容器状态：ID={inventory.Value:X16}，名称={inventory.Name}（主机运行时生成物，等待 Spawn 生命周期补发）。");}else{foreach(var readyPeer in readyPeers.ToArray())if(readyPeer!=peer)Queue(readyPeer,ProtocolMessageType.InventoryState,envelope.Payload);log?.LogInfo($"主机已应用客户端容器状态并转发：ID={inventory.Value:X16}，玩家 {peer}，版本 {inventory.Revision}，槽位 {inventory.Slots.Length}。");}}
            else if(envelope.MessageType==ProtocolMessageType.RescueRequest){var rescue=ReplicationProtocolCodec.DecodeRescueRequest(envelope.Payload);if(rescue.PlayerId!=peer)throw new InvalidDataException("Rescue request player id mismatch.");HandleRescueIntent(peer,rescue.Cancel);}
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
            else if(envelope.MessageType==ProtocolMessageType.InventoryState){var inventory=ReplicationProtocolCodec.DecodeInventoryState(envelope.Payload);if(!replication.Apply(inventory)){missingEntities.Add(new EntityId(inventory.Value,inventory.Persistent));log?.LogWarning($"忽略缺失实体的容器状态：ID={inventory.Value:X16}，名称={inventory.Name}（主机运行时生成物，等待 Spawn 生命周期补发）。");}}
            else if(envelope.MessageType==ProtocolMessageType.RuntimeEntitySpawn){var spawn=ReplicationProtocolCodec.DecodeRuntimeEntitySpawn(envelope.Payload);if(clientSession?.Session.Lifecycle.State==ConnectionState.Ready&&spawn.Scene==CurrentScene){runtimeRegistry.Register(new RuntimeEntityRecord(spawn.RuntimeEntityId,spawn.Kind,spawn.PrototypeId,spawn.Scene,spawn.ServerTick));log?.LogInfo($"客户端已登记运行时实体：ID {spawn.RuntimeEntityId}，类型 {spawn.Kind}，原型 {spawn.PrototypeId}。");if(spawn.Kind==RuntimeEntityKind.LootContainer)SpawnRuntimeLootContainerMirror(spawn);else if(spawn.Kind==RuntimeEntityKind.Enemy)SpawnRuntimeEnemyMirror(spawn);}}
            else if(envelope.MessageType==ProtocolMessageType.RuntimeEntityDespawn){var despawn=ReplicationProtocolCodec.DecodeRuntimeEntityDespawn(envelope.Payload);if(clientSession?.Session.Lifecycle.State==ConnectionState.Ready){if(runtimeRegistry.Remove(despawn.RuntimeEntityId)){if(runtimeInventoryMirrors.TryGetValue(despawn.RuntimeEntityId,out var mirror)){runtimeInventoryMirrors.Remove(despawn.RuntimeEntityId);if(mirror!=null)UnityEngine.Object.Destroy(mirror.gameObject);}if(runtimeEnemyMirrors.TryGetValue(despawn.RuntimeEntityId,out var enemy)){runtimeEnemyMirrors.Remove(despawn.RuntimeEntityId);replication.UnregisterRuntimeEntity(new EntityId(despawn.RuntimeEntityId,false));if(enemy!=null)UnityEngine.Object.Destroy(enemy.gameObject);}log?.LogInfo($"客户端已移除运行时实体：ID {despawn.RuntimeEntityId}，原因 {despawn.Reason}。");}else log?.LogWarning($"客户端收到未知运行时实体移除：ID {despawn.RuntimeEntityId}（晚到/重复包，已忽略）。");}}
            else if(envelope.MessageType==ProtocolMessageType.SceneChange){var change=ReplicationProtocolCodec.DecodeSceneChange(envelope.Payload);log?.LogInfo($"主机场景已切换到 {change.Scene}，客户端将在 3 秒后自动重连并重新加载新场景存档。");autoReconnectAt=Time.unscaledTime+3f;}
            else if(envelope.MessageType==ProtocolMessageType.PlayerPose)remotePlayers.Apply(ReplicationProtocolCodec.DecodePlayerPose(envelope.Payload),clientSession?.PeerId??-1);
            else if(envelope.MessageType==ProtocolMessageType.ActionResult)HandleActionResult(ReplicationProtocolCodec.DecodeActionResult(envelope.Payload));
            else if(envelope.MessageType==ProtocolMessageType.ActionRejected)HandleActionRejected(ReplicationProtocolCodec.DecodeActionRejected(envelope.Payload));
            else if(envelope.MessageType==ProtocolMessageType.PlayerHealth)ApplyIncomingPlayerHealth(ReplicationProtocolCodec.DecodePlayerHealth(envelope.Payload));
            else if(envelope.MessageType==ProtocolMessageType.RescueProgress)HandleRescueProgress(ReplicationProtocolCodec.DecodeRescueProgress(envelope.Payload));
            else if(envelope.MessageType==ProtocolMessageType.AllDowned)HandleAllDowned();
            else if(envelope.MessageType==ProtocolMessageType.GuestProfile)
            {
                var profile=ReplicationProtocolCodec.DecodeGuestProfile(envelope.Payload);
                var lifecycle=clientSession?.Session.Lifecycle.State;
                if(lifecycle!=ConnectionState.ApplyingSnapshot&&lifecycle!=ConnectionState.Ready)throw new InvalidDataException("Guest profile arrived outside the joining phase.");
                ApplyGuestProfile(profile);
            }
            else if(envelope.MessageType==ProtocolMessageType.Ready){var ready=ReplicationProtocolCodec.DecodeReady(envelope.Payload);if(ready.Scene!=CurrentScene||ready.RegistryDigest!=incomingSnapshotManifest.RegistryDigest)throw new InvalidDataException("主机就绪确认与已应用的世界快照不一致。");clientSnapshotReady=true;lastSnapshotApplied=null;snapshotAckRetryCount=0;TransferProgress="联机已就绪";if(clientSession?.Session.Lifecycle.State==ConnectionState.ApplyingSnapshot)clientSession.Session.Lifecycle.MoveTo(ConnectionState.Ready);log?.LogInfo($"客户端联机已就绪：场景 {ready.Scene}，注册表摘要 {ready.RegistryDigest}。");}
            else if(envelope.MessageType==ProtocolMessageType.Error){var error=ReplicationProtocolCodec.DecodeError(envelope.Payload);throw new InvalidDataException($"Host error {error.Code}: {error.Detail}");}
        }
        catch(Exception error){FailClient("CLIENT_PROTOCOL_FAILED",error);}
    }

    private void PrepareSave(int peer)
    {
        var manager=Singleton<SaveManager>.Instance;if(manager==null)throw new InvalidOperationException("SaveManager is unavailable.");
        var profile=global::Core.currentProfile;
        if(profile==null||!profile.Active)
        {
            // 0.8.8 自测：quickLoadGame 等路径不会激活档案——从档案列表恢复（主机侧通用健壮性）。
            var state=manager.loadGameProfiles();
            if(state?.profiles!=null){global::Core.profiles=state.profiles;profile=state.profiles.FirstOrDefault(p=>p!=null&&p.Active);if(profile!=null){global::Core.currentProfile=profile;manager.updateFilePaths();log?.LogWarning("主机档案未激活，已从档案列表恢复当前档案。");}}
        }
        if(profile==null||!profile.Active)throw new InvalidOperationException("Host has no active save profile.");
        manager.Save(false,true,true,false,false,true,false);manager.saveProfilesFile();var bundle=DarkwoodSaveBundle.BuildForClient(manager.baseSaveDirectory,profile.id);var id=Guid.NewGuid();sentSaves[peer]=id;var chunks=ChunkTransferAssembler.Split(bundle,128*1024);Queue(peer,ProtocolMessageType.SaveTransferManifest,ReplicationProtocolCodec.Encode(new SaveTransferManifest(id,profile.id,bundle.LongLength,chunks.Length,ChunkTransferAssembler.Hash(bundle),$"Day {profile.day}, chapter {profile.chapter}")));for(var i=0;i<chunks.Length;i++)Queue(peer,ProtocolMessageType.SaveTransferChunk,ReplicationProtocolCodec.Encode(new SaveTransferChunk(id,i,chunks.Length,chunks[i],ChunkTransferAssembler.Hash(chunks[i]))),"存档",i,chunks.Length);log?.LogInfo($"已为玩家 {peer} 准备实时存档：传输 {id}，{bundle.Length} 字节，{chunks.Length} 个数据块。");
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
        yield return null;try{if(clientSession==null||!clientSession.HandshakeComplete||clientSession.Session.Lifecycle.State==ConnectionState.Failed)throw new InvalidOperationException("客户端连接已断开，取消存档加载。");var manager=Singleton<SaveManager>.Instance;if(manager==null)throw new InvalidOperationException("SaveManager 不可用。");var state=manager.loadGameProfiles();if(state?.profiles==null)throw new InvalidDataException("下载的存档档案信息不可用。");var profile=state.profiles.FirstOrDefault(p=>p!=null&&p.id==profileId&&p.Active);if(profile==null)throw new InvalidDataException("下载的存档档案信息不可用。");global::Core.profiles=state.profiles;global::Core.currentProfile=profile;manager.updateFilePaths();if(clientSession.Session.Lifecycle.State==ConnectionState.SaveTransfer)clientSession.Session.Lifecycle.MoveTo(ConnectionState.LoadingSave);
        // FIX-006：不在此处挂 onFinishedLoading——SaveManager 是场景内单例（非 DontDestroyOnLoad），
        // 此处挂到的是主菜单场景的实例，LoadScene 后随场景销毁；Load() 跑在 chapter1 场景的
        // 新实例上，回调永远不触发（实测：加载卡 92%、timeScale 无人恢复、界面永不隐藏）。
        // 改由 DarkwoodLoadFinishedPatch 在 SaveManager.Load 入口挂到 __instance。
        TransferProgress="正在加载存档";loadStartedAt=Time.unscaledTime;
        // FIX-002：initLoadGame() 内部先跑 initNewGame()，会把 Core.loadingGame 重置为 false，
        // WorldGenerator.Start 因此走“生成新世界”分支（教学梦境，约 8 个实体），且
        // SaveManager.onFinishedLoading 不触发（客户端永远卡在加载界面）。
        // 正确路径：保持 loadingGame=true 直接加载章节场景，WorldGenerator.Start
        // 会走 SaveManager.Load() 恢复主机存档世界，完成后回调 onFinishedLoading。
        global::Core.loadingGame=true;global::Core.loadedGame=true;global::Core.forbidInputs=true;var controller=Singleton<Controller>.Instance;if(controller!=null)controller.buttonsDisabled=true;ClientSaveLoadPending=true;LogMessage($"正在切换到章节场景 {(profile.chapter>=2?"chapter2":"chapter1")} 并启动存档恢复（约 2 秒后 WorldGenerator.Start 调度 SaveManager.Load）。");UnityEngine.SceneManagement.SceneManager.LoadScene(profile.chapter>=2?"chapter2":"chapter1");global::Core.mainMenu=false;}catch(Exception error){FailClient("SAVE_LOAD_FAILED",error);}
    }

    private void OnDownloadedSaveFinished()
    {
        ClientSaveLoadPending=false;LogMessage($"存档加载完成回调已触发（用时 {(loadStartedAt>0f?Time.unscaledTime-loadStartedAt:0f):F1} 秒）。");
        if(Time.timeScale<=0.01f){Time.timeScale=1f;LogMessage("已强制恢复 timeScale=1（加载期间曾被冻结）。");}
        var manager=Singleton<SaveManager>.Instance;if(manager!=null)manager.onFinishedLoading=(saveDelegate)Delegate.Remove(manager.onFinishedLoading,new saveDelegate(OnDownloadedSaveFinished));if(clientSession==null||!clientSession.HandshakeComplete||clientSession.Session.Lifecycle.State==ConnectionState.Failed){log?.LogWarning("客户端连接已断开，忽略已完成的存档加载回调。");return;}if(clientSession.Session.Lifecycle.State==ConnectionState.LoadingSave)clientSession.Session.Lifecycle.MoveTo(ConnectionState.BuildingRegistry);registryDirty=true;StartCoroutine(WaitForRegistryThenReady());
    }

    /// <summary>FIX-006：把完成回调幂等挂到“真正执行 Load 的 SaveManager 实例”上。
    /// SaveManager 是场景内单例（无 DontDestroyOnLoad），主菜单场景的实例会在
    /// LoadScene 后销毁；只有当前场景实例上挂的回调才会被 Load() 触发。</summary>
    internal static void AttachLoadFinishedCallback(SaveManager manager)
    {
        var runtime = Instance;
        if (runtime == null) return;
        manager.onFinishedLoading = (saveDelegate)Delegate.Remove(manager.onFinishedLoading, new saveDelegate(runtime.OnDownloadedSaveFinished));
        manager.onFinishedLoading = (saveDelegate)Delegate.Combine(manager.onFinishedLoading, new saveDelegate(runtime.OnDownloadedSaveFinished));
    }

    private IEnumerator WaitForRegistryThenReady()
    {
        var deadline=Time.realtimeSinceStartup+90f;
        // 世界在存档加载后仍可能流式生成：Player.Instance 出现得很早，但场景对象
        // 会继续分帧实例化。反复强制重建注册表，直到实体数连续 3 次稳定，
        // 确保客户端注册表覆盖主机世界的完整对象集（否则快照库存无法绑定）。
        var previousCount=-1;
        var stableChecks=0;
        while(Time.realtimeSinceStartup<deadline)
        {
            if(Player.Instance==null){yield return null;continue;}
            registryDirty=true;
            yield return null;
            if(registry==null){yield return null;continue;}
            var count=registry.Count;
            if(count==previousCount)
            {
                stableChecks++;
                if(stableChecks>=3)break;
            }
            else
            {
                stableChecks=0;
                previousCount=count;
            }
            yield return new WaitForSeconds(1f);
        }
        try
        {
            if(Player.Instance==null||registry==null)throw new InvalidOperationException("实体注册表在 90 秒内未就绪。");
            if(stableChecks<3)log?.LogWarning($"注册表在超时前未能稳定（最后实体数 {registry.Count}），继续尝试就绪。");
            log?.LogInfo($"客户端注册表已稳定：{registry.Count} 个实体，摘要 {RegistryDigest}。");
            clientRegistryStabilized=true;
            TrySendClientRegistryReady();
        }
        catch(Exception error){FailClient("REGISTRY_BUILD_FAILED",error);}
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
        // FIX-008：不再用 savs.dat 的文件创建时间——游戏保存会重写该文件，创建时间随之变化，
        // 扩容账本因此全部失效，导致每次开服对共享柜子重复翻倍物品，最终溢出损坏主机存档。
        // 改用稳定身份：存档目录名 + profile id。新档的 uniqueId 会全新分配，账本不会误命中。
        try
        {
            var manager=Singleton<SaveManager>.Instance;
            var dir=manager!=null&&!string.IsNullOrEmpty(manager.staticFile)?Path.GetDirectoryName(manager.staticFile):string.Empty;
            if(!string.IsNullOrEmpty(dir))return Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar))+"|"+(global::Core.currentProfile?.id??-1);
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
        if(incomingSnapshot==null)throw new InvalidDataException("World snapshot chunk arrived before manifest.");incomingSnapshot.Add(chunk.SnapshotId,chunk.Index,chunk.Total,chunk.Data,chunk.Hash);TransferProgress=$"正在接收世界快照：{incomingSnapshot.ReceivedChunks}/{incomingSnapshot.ChunkCount}（{(int)(incomingSnapshot.ReceivedChunks*100f/incomingSnapshot.ChunkCount)}%）";if(!incomingSnapshot.IsComplete)return;var bytes=incomingSnapshot.Build();incomingSnapshot=null;var snapshot=DarkwoodWorldSnapshotCodec.Decode(bytes);if(snapshot.Scene!=CurrentScene||snapshot.Scene!=incomingSnapshotManifest.Scene)throw new InvalidDataException("世界快照场景不一致。");if(snapshot.RegistryDigest!=incomingSnapshotManifest.RegistryDigest)throw new InvalidDataException($"快照摘要不一致：payload={snapshot.RegistryDigest}，manifest={incomingSnapshotManifest.RegistryDigest}。");if(snapshot.ServerTick!=incomingSnapshotManifest.ServerTick)throw new InvalidDataException("世界快照 tick 不一致。");replication.Apply(snapshot.Entities,true);var appliedInventories=0;var failedInventories=0;var loggedFailures=0;foreach(var inventory in snapshot.Inventories){if(replication.Apply(inventory))appliedInventories++;else{failedInventories++;missingEntities.Add(new EntityId(inventory.Value,inventory.Persistent));if(loggedFailures<8){loggedFailures++;log?.LogError($"共享容器快照无法绑定：ID={inventory.Value:X16}，名称={inventory.Name}，位置=({inventory.X:F1},{inventory.Y:F1},{inventory.Z:F1})，类型={inventory.InventoryType}。客户端候选：{replication.DescribeNearestInventory(inventory)}");}}}if(failedInventories>0){if(!DarkwoodMultiplayerFramework.Core.SnapshotTolerance.Tolerate(failedInventories,snapshot.Inventories.Length))throw new InvalidDataException($"有 {failedInventories} 个共享容器无法应用主机权威快照（客户端共享容器 {replication.SharedInventoryCount} 个），已阻止客户端误进入就绪状态。");log?.LogWarning($"FIX-007：{failedInventories}/{snapshot.Inventories.Length} 个共享容器在客户端世界中缺失（主机运行时生成物，如乌鸦/动物尸体），已跳过并继续就绪；等待 0.8.8 Spawn 生命周期补发。");}lastSnapshotApplied=new WorldSnapshotApplied(incomingSnapshotManifest.SnapshotId,snapshot.Scene,snapshot.RegistryDigest,snapshot.ServerTick,snapshot.Entities.Length);snapshotAckRetryCount=0;SendSnapshotAcknowledgement();log?.LogInfo($"世界快照应用完成：{snapshot.Entities.Length} 个实体，共享容器 {appliedInventories}/{snapshot.Inventories.Length}，tick {snapshot.ServerTick}；等待主机确认。");
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
        // FIX-011：报告本地执行后的 isOn 状态，主机直接应用（信任模型），不调用 activate()。
        var request=new ActionRequestMessage(Guid.NewGuid(),clientSession.PeerId,ActionKindWire.ItemActivate,id.Value,id.IsPersistent,expectedRevision,ReplicationProtocolCodec.Encode(new InteractPayload(item.isOn?1:0)));
        pendingActions[request.RequestId]=request;
        clientSession.Send(ProtocolMessageType.ActionRequest,ReplicationProtocolCodec.Encode(request));
        log?.LogInfo($"Item activate request {request.RequestId} sent for {id}, isOn {(item.isOn?1:0)}, revision {expectedRevision}.");
        return true;
    }

    public void NotifyHostContainerChanged(Inventory inventory)
    {
        if(hostSession==null||readyPeers.Count==0||inventory==null||replication.ApplyingRemote)return;
        if(!replication.TryGetId(inventory,out var id))return;
        try{BroadcastInventory(replication.CaptureAuthoritativeInventory(id));}
        catch(Exception error){log?.LogWarning($"Failed to publish host container mutation for {id}: {error.Message}");}
    }

    /// <summary>0.8.8-alpha.5：游戏默认出生点（playerBase 的 playerSpawn，与单机新游戏出生一致）。取不到时回退主机玩家位置。</summary>
    private Vector3 DefaultSpawnPoint()
    {
        try
        {
            var worldGen=Singleton<WorldGenerator>.Instance;
            if(worldGen!=null&&worldGen.playerBase!=null)
            {
                var location=worldGen.playerBase.GetComponent<Location>();
                if(location!=null&&location.playerSpawn!=null)return location.playerSpawn.transform.position;
            }
        }
        catch(Exception error){log?.LogWarning($"读取默认出生点失败：{error.Message}");}
        var player=Player.Instance;
        return player!=null?player.transform.position:Vector3.zero;
    }

    /// <summary>0.8.8-alpha.3：Host 构建运行时实体生成消息（分配 ID 并登记）。非主机返回 default。</summary>
    public RuntimeEntitySpawnMessage BuildRuntimeEntitySpawn(RuntimeEntityKind kind,string prototypeId,Vector3 position,Quaternion rotation,byte[]? initialState=null)
    {
        if(hostSession==null)return default;
        var id=runtimeRegistry.Allocate();
        var message=new RuntimeEntitySpawnMessage(id,kind,prototypeId,CurrentScene,position.x,position.y,position.z,rotation.x,rotation.y,rotation.z,rotation.w,initialState??Array.Empty<byte>(),serverTick);
        runtimeRegistry.Register(new RuntimeEntityRecord(id,kind,prototypeId,CurrentScene,serverTick));
        return message;
    }

    /// <summary>0.8.8-alpha.3：向单个玩家发送 Spawn（随机事件范围门控的单播通道）。</summary>
    private void SendRuntimeEntitySpawnTo(int peer,RuntimeEntitySpawnMessage message)=>Queue(peer,ProtocolMessageType.RuntimeEntitySpawn,ReplicationProtocolCodec.Encode(message));

    /// <summary>0.8.8-alpha.3：Host 广播运行时实体生成。分配 ID（单调递增）、登记并发送给所有就绪玩家。返回分配的 ID；非主机返回 0。</summary>
    public ulong BroadcastRuntimeEntitySpawn(RuntimeEntityKind kind,string prototypeId,Vector3 position,Quaternion rotation,byte[]? initialState=null)
    {
        var message=BuildRuntimeEntitySpawn(kind,prototypeId,position,rotation,initialState);
        if(message.RuntimeEntityId==0)return 0;
        foreach(var readyPeer in readyPeers.ToArray())SendRuntimeEntitySpawnTo(readyPeer,message);
        log?.LogInfo($"主机已广播运行时实体生成：ID {message.RuntimeEntityId}，类型 {kind}，原型 {prototypeId}，tick {serverTick}。");
        return message.RuntimeEntityId;
    }

    /// <summary>0.8.8-alpha.3：Host 广播运行时实体移除。未登记的 ID 直接返回 false（不广播）。</summary>
    public bool BroadcastRuntimeEntityDespawn(ulong runtimeEntityId,RuntimeEntityDespawnReason reason)
    {
        if(hostSession==null||!runtimeRegistry.Remove(runtimeEntityId))return false;
        var payload=ReplicationProtocolCodec.Encode(new RuntimeEntityDespawnMessage(runtimeEntityId,serverTick,reason));
        foreach(var readyPeer in readyPeers.ToArray())Queue(readyPeer,ProtocolMessageType.RuntimeEntityDespawn,payload);
        log?.LogInfo($"主机已广播运行时实体移除：ID {runtimeEntityId}，原因 {reason}，tick {serverTick}。");
        return true;
    }

    /// <summary>
    /// 0.8.8-alpha.3：主机周期扫描运行时生成的可搜刮容器（乌鸦群、动物尸体等 deathDrop 对象，
    /// 不在持久注册表内——它们不写入存档，客户端靠加载存档无法获得）。
    /// 范围门控 + 一次性语义：新容器只登记为"待触发事件"；客户端玩家进入动画范围（XZ 35 米）
    /// 才单播 Spawn；一次性事件（乌鸦等）触发后同一客户端离开再进入不再重播；
    /// 容器消失 → Despawn 广播并清除事件记录。
    /// </summary>
    private void ScanRuntimeLootContainers()
    {
        if(hostSession==null||readyPeers.Count==0||Time.unscaledTime<nextRuntimeScan)return;
        nextRuntimeScan=Time.unscaledTime+2f;
        var seen=new HashSet<Inventory>();
        var seenEnemies=new HashSet<Character>();
        foreach(var component in scanner.ScanScene())
        {
            if(component is Inventory inventory&&inventory.invType==Inventory.InvType.deathDrop)
            {
                if(replication.TryGetId(inventory,out _))continue; // 已在持久注册表（存档内对象），非运行时生成
                seen.Add(inventory);
                if(runtimeInventoryIds.ContainsKey(inventory))continue;
                byte[] initialState;
                try{initialState=ReplicationProtocolCodec.Encode(replication.CaptureInventoryState(inventory,0));}
                catch(Exception error){log?.LogWarning($"捕获运行时容器初始状态失败（{inventory.name}）：{error.Message}");initialState=Array.Empty<byte>();}
                var message=BuildRuntimeEntitySpawn(RuntimeEntityKind.LootContainer,inventory.name,inventory.transform.position,inventory.transform.rotation,initialState);
                if(message.RuntimeEntityId==0)continue;
                runtimeInventoryIds[inventory]=message.RuntimeEntityId;
                pendingRuntimeEvents[message.RuntimeEntityId]=message;
                log?.LogInfo($"主机登记随机事件容器（待客户端进入范围触发）：ID {message.RuntimeEntityId}，prefab {inventory.name}，位置 ({message.X:F0},{message.Y:F0},{message.Z:F0})。");
            }
            else if(component is Character character&&!(character is Player))
            {
                if(replication.TryGetId(character,out _))continue; // 存档内怪物
                seenEnemies.Add(character); // 尸体也算"仍在场"（防误 Despawn，等游戏清理后再广播移除）
                if(!character.alive)continue; // 尸体不 Spawn；若转为 deathDrop 会被上面的容器分支捕获
                if(runtimeEnemyIds.ContainsKey(character))continue;
                var prefabName=character.name.Replace("(Clone)","");
                var enemyMessage=BuildRuntimeEntitySpawn(RuntimeEntityKind.Enemy,prefabName,character.transform.position,character.transform.rotation);
                if(enemyMessage.RuntimeEntityId==0)continue;
                runtimeEnemyIds[character]=enemyMessage.RuntimeEntityId;
                replication.RegisterRuntimeEntity(new EntityId(enemyMessage.RuntimeEntityId,false),character);
                pendingRuntimeEvents[enemyMessage.RuntimeEntityId]=enemyMessage;
                log?.LogInfo($"主机登记运行时敌人（待客户端进入范围触发）：ID {enemyMessage.RuntimeEntityId}，prefab {prefabName}，位置 ({enemyMessage.X:F0},{enemyMessage.Y:F0},{enemyMessage.Z:F0})。");
            }
        }
        foreach(var pair in runtimeInventoryIds.ToArray())
        {
            if(pair.Key==null||!seen.Contains(pair.Key))
            {
                runtimeInventoryIds.Remove(pair.Key);
                pendingRuntimeEvents.Remove(pair.Value);
                runtimeEventDispatch.ClearEvent(pair.Value);
                BroadcastRuntimeEntityDespawn(pair.Value,RuntimeEntityDespawnReason.Collected);
            }
        }
        foreach(var pair in runtimeEnemyIds.ToArray())
        {
            if(pair.Key==null||!seenEnemies.Contains(pair.Key))
            {
                runtimeEnemyIds.Remove(pair.Key);
                pendingRuntimeEvents.Remove(pair.Value);
                runtimeEventDispatch.ClearEvent(pair.Value);
                replication.UnregisterRuntimeEntity(new EntityId(pair.Value,false));
                BroadcastRuntimeEntityDespawn(pair.Value,RuntimeEntityDespawnReason.Died);
            }
        }
        foreach(var pair in pendingRuntimeEvents.ToArray())
        {
            var message=pair.Value;
            foreach(var readyPeer in readyPeers.ToArray())
            {
                if(!remotePlayerPositions.TryGetValue(readyPeer,out var pose))continue;
                var dx=pose.x-message.X;var dz=pose.z-message.Z;
                if(dx*dx+dz*dz>RuntimeEventTriggerRange*RuntimeEventTriggerRange)continue; // 距离过远：不触发
                if(!runtimeEventDispatch.TryMark(message.RuntimeEntityId,readyPeer))continue; // 一次性：已触发过不重播
                SendRuntimeEntitySpawnTo(readyPeer,message);
                log?.LogInfo($"客户端 {readyPeer} 进入随机事件范围，触发动画：ID {message.RuntimeEntityId}，prefab {message.PrototypeId}。");
            }
        }
    }

    /// <summary>0.8.8-alpha.3：客户端实例化运行时容器镜像（禁交互，防物品复制；库存内容用 InitialState 填充）。</summary>
    private void SpawnRuntimeLootContainerMirror(RuntimeEntitySpawnMessage spawn)
    {
        try
        {
            var go=global::Core.AddPrefab(spawn.PrototypeId,new Vector3(spawn.X,spawn.Y,spawn.Z),new Quaternion(spawn.Qx,spawn.Qy,spawn.Qz,spawn.Qw),global::Core.ItemContainer);
            if(go==null){log?.LogWarning($"客户端无法实例化运行时容器：prefab {spawn.PrototypeId} 不存在或不可用。");return;}
            var inventory=go.GetComponent<Inventory>();
            if(inventory!=null&&spawn.InitialState.Length>0)
            {
                var state=ReplicationProtocolCodec.DecodeInventoryState(spawn.InitialState);
                var slots=new DarkwoodInventorySlot[state.Slots.Length];
                for(var i=0;i<slots.Length;i++){var s=state.Slots[i];slots[i]=new DarkwoodInventorySlot{Type=s.Type,Amount=s.Amount,Durability=s.Durability,Quality=s.Quality,Recipe=s.Recipe};}
                DarkwoodInventoryAdapter.Apply(inventory,slots);
            }
            foreach(var col in go.GetComponentsInChildren<Collider>(true))col.enabled=false;
            runtimeInventoryMirrors[spawn.RuntimeEntityId]=go.transform;
            log?.LogInfo($"客户端已实例化运行时容器镜像：ID {spawn.RuntimeEntityId}，prefab {spawn.PrototypeId}，槽位 {(inventory!=null?inventory.slots.Count:0)}。");
        }
        catch(Exception error){log?.LogWarning($"实例化运行时容器失败（{spawn.PrototypeId}）：{error.Message}");}
    }

    /// <summary>0.8.8-alpha.4：客户端实例化运行时敌人代理。AI 冻结（远端代理），注册进 entities 以接收 15Hz delta（位置/血量/动画/死亡）。</summary>
    private void SpawnRuntimeEnemyMirror(RuntimeEntitySpawnMessage spawn)
    {
        try
        {
            var go=global::Core.AddPrefab(spawn.PrototypeId,new Vector3(spawn.X,spawn.Y,spawn.Z),new Quaternion(spawn.Qx,spawn.Qy,spawn.Qz,spawn.Qw),global::Core.ItemContainer);
            if(go==null){log?.LogWarning($"客户端无法实例化运行时敌人：prefab {spawn.PrototypeId} 不存在或不可用。");return;}
            var character=go.GetComponent<Character>();
            if(character==null){UnityEngine.Object.Destroy(go);log?.LogWarning($"运行时敌人实例无 Character 组件：{spawn.PrototypeId}。");return;}
            character.enabled=false; // 远端代理：冻结 AI
            if(character.AIpath!=null)character.AIpath.enabled=false;
            replication.RegisterRuntimeEntity(new EntityId(spawn.RuntimeEntityId,false),character);
            runtimeEnemyMirrors[spawn.RuntimeEntityId]=character;
            log?.LogInfo($"客户端已实例化运行时敌人代理：ID {spawn.RuntimeEntityId}，prefab {spawn.PrototypeId}，血量 {character.health:F0}/{character.maxHealth:F0}。");
        }
        catch(Exception error){log?.LogWarning($"实例化运行时敌人失败（{spawn.PrototypeId}）：{error.Message}");}
    }

    /// <summary>FIX-011 信任模式：客户端容器本地执行后的状态上报（不经主机审批）。</summary>
    public void ReportSharedContainerChanged(Inventory inventory)
    {
        if(clientSession?.Session.Lifecycle.State!=ConnectionState.Ready||inventory==null||replication.ApplyingRemote)return;
        if(inventory.invType!=Inventory.InvType.itemInv&&inventory.invType!=Inventory.InvType.deathDrop)return;
        if(!replication.TryGetId(inventory,out var id))return;
        try
        {
            var state=replication.CaptureAuthoritativeInventory(id);
            clientSession.Send(ProtocolMessageType.InventoryState,ReplicationProtocolCodec.Encode(state));
            log?.LogInfo($"客户端已上报共享容器状态：ID={id.Value:X16}，版本 {state.Revision}，槽位 {state.Slots.Length}。");
        }
        catch(Exception error){log?.LogWarning($"Failed to report client container mutation for {id}: {error.Message}");}
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
        if(!item.gameObject.activeSelf||item.destroyed||!item.isDroppedItem){RejectAction(peer,request,"NOT_PICKABLE",state.Revision);return;}
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

    private void HandleAttackRequest(int peer,ActionRequestMessage request)
    {
        AttackPayload attack;
        try{attack=ReplicationProtocolCodec.DecodeAttack(request.Payload);}
        catch(Exception error){RejectAction(peer,request,"INVALID_ATTACK_PAYLOAD",0);log?.LogWarning($"Attack payload rejected from peer {peer}: {error.Message}");return;}
        if(!remotePlayerPositions.TryGetValue(peer,out var pose)){RejectAction(peer,request,"PLAYER_POSE_MISSING",0);return;}
        if(nextAttackAllowed.TryGetValue(peer,out var allowedAt)&&Time.unscaledTime<allowedAt){RejectAction(peer,request,"RATE_LIMITED",0);return;}
        nextAttackAllowed[peer]=Time.unscaledTime+AttackCooldownSeconds;
        // FIX-011：信任模型——不再校验攻击位置与追踪姿势的距离；目标仍按客户端报告的方向解析。
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
        // FIX-011：信任模型——距离/版本/封板判断全部移除，客户端本地已执行，主机直接执行并广播。
        door.openClose(GetAttackAnchor(peer,pose).transform);
        AcceptInteract(peer,request,id,door,0);
        log?.LogInfo($"主机已批准开关门 {request.RequestId}：玩家 {peer}，门 {id}。");
    }

    private void HandleWindowInteractRequest(int peer,ActionRequestMessage request)
    {
        var id=new EntityId(request.TargetValue,request.TargetPersistent);
        if(!replication.TryGetComponent(id,out var component)||!(component is Window window)){RejectAction(peer,request,"WINDOW_NOT_FOUND",0);return;}
        // FIX-011：信任模型——距离/版本判断移除，客户端本地已执行，主机直接应用并广播。
        var interact=ReplicationProtocolCodec.DecodeInteract(request.Payload);
        window.barricade(interact.ValueA,true);
        AcceptInteract(peer,request,id,window,0);
        log?.LogInfo($"主机已应用封窗 {request.RequestId}：玩家 {peer}，窗 {id}，目标耐久 {interact.ValueA}。");
    }

    private void HandleItemActivateRequest(int peer,ActionRequestMessage request)
    {
        var id=new EntityId(request.TargetValue,request.TargetPersistent);
        if(!replication.TryGetComponent(id,out var component)||!(component is Item item)){RejectAction(peer,request,"ITEM_NOT_FOUND",0);return;}
        // FIX-011：信任模型——客户端本地已执行 activate() 并报告 isOn 结果状态；
        // 主机直接应用该状态（不调用 activate()，避免在主机弹出容器 UI）并广播。
        var interact=ReplicationProtocolCodec.DecodeInteract(request.Payload);
        item.isOn = interact.ValueA != 0;
        AcceptInteract(peer,request,id,item,0);
        log?.LogInfo($"主机已应用物品开关 {request.RequestId}：玩家 {peer}，物品 {id}，isOn={item.isOn}。");
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

    private void ApplyGuestProfile(GuestProfileMessage profile)
    {
        var player=Player.Instance;if(player==null)throw new InvalidOperationException("客户端玩家尚未就绪。");
        player.transform.position=new Vector3(profile.X,profile.Y,profile.Z);
        foreach(var body in player.GetComponentsInChildren<Rigidbody>(true)){body.velocity=Vector3.zero;body.angularVelocity=Vector3.zero;}
        ApplyPlayerInventory(profile.Inventory);
        if(!profile.Downed&&profile.Health>0f)player.setHealth(profile.Health);
        log?.LogInfo($"已应用访客档案：出生点 ({profile.X:F1},{profile.Y:F1},{profile.Z:F1})，第 {profile.Day} 天，第 {profile.JoinCount} 次加入。");
    }

    private void PersistGuestProfile(int peer)
    {
        if(guestProfiles==null)return;
        if(!peerGuestKeys.TryGetValue(peer,out var key)||!peerGuestRecords.TryGetValue(peer,out var record)||!remoteInventories.TryGetValue(peer,out var shadow))return;
        var position=remotePlayerPositions.TryGetValue(peer,out var pose)?pose:new Vector3(record.X,record.Y,record.Z);
        var state=shadow.CaptureState();
        var updated=new GuestProfileRecord(record.GuestKey,record.Day,record.JoinCount,position.x,position.y,position.z,state.Backpack,state.Hotbar,DateTime.UtcNow.Ticks);
        guestProfiles.Save(HostSaveToken(),updated);
    }

    private static string NormalizeGuestKey(string? value)
    {
        var key=(value??string.Empty).Trim();
        if(key.Length==0)return "player";
        while(System.Text.Encoding.UTF8.GetByteCount(key)>64&&key.Length>1)key=key.Substring(0,key.Length-1);
        return key;
    }

    public bool ApplyPlayerName(string name,out string error)
    {
        error=string.Empty;name=(name??string.Empty).Trim();
        if(name.Length==0){error="玩家名称不能为空。";return false;}
        if(System.Text.Encoding.UTF8.GetByteCount(name)>64){error="玩家名称过长（最多 64 字节）。";return false;}
        if(playerNameConfig==null){error="配置尚未就绪。";return false;}
        playerNameConfig.Value=name;playerNameConfig.ConfigFile.Save();
        log?.LogInfo($"玩家名称已保存：{name}。");
        return true;
    }

    public string ConfiguredPlayerName => playerNameConfig?.Value ?? string.Empty;

    // ---------- 倒地 / 营救（DOWNED-001） ----------

    /// <summary>Called by DarkwoodDownedPatch when the LOCAL player dies while other players are alive.</summary>
    public void OnLocalPlayerDowned()
    {
        if (DarkwoodDownedPatch.LocalDowned) return;
        DarkwoodDownedPatch.EnterLocalDowned();
        log?.LogWarning("本地玩家倒地！等待队友营救……");
        if (IsHost)
        {
            hostDownedLocal = true;
            var player = Player.Instance;
            BroadcastHealth(0, player != null ? player.health : 0f, player != null ? player.maxHealth : 100f, true);
            CheckAllDowned();
        }
    }

    private void ApplyIncomingPlayerHealth(PlayerHealthMessage health)
    {
        var myId = clientSession?.PeerId ?? -1;
        if (health.PlayerId != myId || clientSession?.Session.Lifecycle.State != ConnectionState.Ready) return;
        var player = Player.Instance;
        if (player == null) return;
        if (health.Downed || health.Health <= 0f)
        {
            if (!DarkwoodDownedPatch.LocalDowned)
            {
                try { player.die(); } catch { DarkwoodDownedPatch.EnterLocalDowned(); }
            }
        }
        else
        {
            if (DarkwoodDownedPatch.LocalDowned) ReviveLocal(health.Health);
            else player.setHealth(health.Health);
        }
    }

    private void ReviveLocal(float health)
    {
        var player = Player.Instance;
        if (player == null) return;
        DarkwoodDownedPatch.ReviveLocalPlayer(health, player.maxStamina);
        player.invulnerable = true;
        localInvulUntil = Time.unscaledTime + ReviveInvulnerableSeconds;
        if (IsHost) hostDownedLocal = false;
        log?.LogInfo($"已复活：生命 {health:F0}，体力回满，{ReviveInvulnerableSeconds:F0} 秒保护。");
    }

    private void HandleRescueProgress(RescueProgressMessage progress)
    {
        lastRescueProgress = progress;
        var myId = clientSession?.PeerId ?? -1;
        if (progress.RescuerId != myId) return;
        if (progress.Active && !rescueLockedByMe)
        {
            rescueLockedByMe = true;
            global::Core.forbidInputs = true;
        }
        else if (!progress.Active && rescueLockedByMe)
        {
            rescueLockedByMe = false;
            if (!DarkwoodDownedPatch.LocalDowned) global::Core.forbidInputs = false;
        }
    }

    private void HandleAllDowned()
    {
        if (DarkwoodDownedPatch.AllDowned) return;
        DarkwoodDownedPatch.AllDowned = true;
        log?.LogWarning("全员倒地——触发原版结局并结束联机会话。");
        RunLocalVanillaEnding();
        scheduledStopAt = Time.unscaledTime + PostDownedEndingDelay;
    }

    private void RunLocalVanillaEnding()
    {
        var player = Player.Instance;
        if (player == null) return;
        DarkwoodDownedPatch.AllDowned = true;
        try
        {
            player.die();
            var method = AccessTools.Method(typeof(Player), "onDeath");
            if (method != null)
            {
                var enumerator = method.Invoke(player, null) as System.Collections.IEnumerator;
                if (enumerator != null) player.StartCoroutine(enumerator);
            }
        }
        catch (Exception error) { log?.LogWarning("原版结局触发失败：" + error.Message); }
    }

    private void CheckAllDowned()
    {
        if (hostSession == null || allDownedHandled) return;
        if (!hostDownedLocal) return;
        foreach (var peer in readyPeers.ToArray())
        {
            if (!peerDowned.TryGetValue(peer, out var downed) || !downed) return;
        }
        allDownedHandled = true;
        DarkwoodDownedPatch.AllDowned = true;
        log?.LogWarning("全员倒地——触发原版结局并结束联机会话。");
        var payload = ReplicationProtocolCodec.Encode(new AllDownedMessage());
        foreach (var readyPeer in readyPeers.ToArray()) Queue(readyPeer, ProtocolMessageType.AllDowned, payload);
        RunLocalVanillaEnding();
        scheduledStopAt = Time.unscaledTime + PostDownedEndingDelay;
    }

    private void PollRescueHotkey()
    {
        if (!IsMultiplayerActive || DarkwoodDownedPatch.AllDowned) return;
        if (DarkwoodDownedPatch.LocalDowned) return;
        if (!Input.GetKeyDown(KeyCode.F4)) return;
        if (IsHost) HandleRescueIntent(0, IsRescuing(0));
        else if (clientSession != null) clientSession.Send(ProtocolMessageType.RescueRequest, ReplicationProtocolCodec.Encode(new RescueRequestMessage(clientSession.PeerId, IsRescuing(clientSession.PeerId))));
    }

    private bool IsRescuing(int playerId) => activeRescue != null && activeRescue.RescuerId == playerId;

    private void HandleRescueIntent(int rescuerId, bool cancel)
    {
        if (hostSession == null) return;
        if (cancel)
        {
            if (activeRescue != null && activeRescue.RescuerId == rescuerId) CancelRescue();
            return;
        }
        if (activeRescue != null) return; // 同一时间只允许一个营救
        if (rescuerId == 0)
        {
            if (hostDownedLocal) return;
        }
        else
        {
            if (!readyPeers.Contains(rescuerId)) return;
            if (peerDowned.TryGetValue(rescuerId, out var rescuerDowned) && rescuerDowned) return;
        }
        var rescuerPosition = GetPlayerPosition(rescuerId);
        var bestTarget = -1;
        var bestSq = RescueRange * RescueRange;
        if (hostDownedLocal && rescuerId != 0)
        {
            var sq = SqrDistance(rescuerPosition, GetPlayerPosition(0));
            if (sq <= bestSq) { bestSq = sq; bestTarget = 0; }
        }
        foreach (var peer in readyPeers.ToArray())
        {
            if (peer == rescuerId) continue;
            if (!peerDowned.TryGetValue(peer, out var downed) || !downed) continue;
            if (!remotePlayerPositions.TryGetValue(peer, out var position)) continue;
            var sq = SqrDistance(rescuerPosition, position);
            if (sq <= bestSq) { bestSq = sq; bestTarget = peer; }
        }
        if (bestTarget < 0)
        {
            log?.LogInfo($"营救请求被拒绝：玩家 {rescuerId} 附近没有倒地的队友。");
            Queue(rescuerId, ProtocolMessageType.RescueProgress, ReplicationProtocolCodec.Encode(new RescueProgressMessage(rescuerId, rescuerId, 0f, false)));
            return;
        }
        activeRescue = new RescueSession { TargetId = bestTarget, RescuerId = rescuerId, StartedAt = Time.unscaledTime };
        nextRescueBroadcast = 0f;
        if (rescuerId == 0 && !DarkwoodDownedPatch.LocalDowned) { rescueLockedByMe = true; global::Core.forbidInputs = true; }
        BroadcastRescueProgress(bestTarget, rescuerId, 0f, true);
        log?.LogInfo($"营救开始：玩家 {rescuerId} → 倒地玩家 {bestTarget}（{RescueDurationSeconds:F0} 秒）。");
    }

    private void TickRescue()
    {
        if (activeRescue == null) return;
        if (ShouldCancelRescue()) { CancelRescue(); return; }
        var progress = Mathf.Clamp01((Time.unscaledTime - activeRescue.StartedAt) / RescueDurationSeconds);
        if (progress >= 1f) { CompleteRescue(); return; }
        if (Time.unscaledTime >= nextRescueBroadcast)
        {
            nextRescueBroadcast = Time.unscaledTime + 0.1f;
            BroadcastRescueProgress(activeRescue.TargetId, activeRescue.RescuerId, progress, true);
        }
    }

    private bool ShouldCancelRescue()
    {
        if (activeRescue == null) return true;
        var rescuer = activeRescue.RescuerId;
        var target = activeRescue.TargetId;
        if (rescuer == 0)
        {
            if (hostDownedLocal) return true;
        }
        else
        {
            if (!readyPeers.Contains(rescuer)) return true;
            if (peerDowned.TryGetValue(rescuer, out var rescuerDowned) && rescuerDowned) return true;
        }
        if (target == 0)
        {
            if (!hostDownedLocal) return true;
        }
        else if (!(peerDowned.TryGetValue(target, out var targetDowned) && targetDowned)) return true;
        if (SqrDistance(GetPlayerPosition(rescuer), GetPlayerPosition(target)) > RescueRange * RescueRange) return true;
        return false;
    }

    private void CancelRescue()
    {
        if (activeRescue == null) return;
        var target = activeRescue.TargetId;
        var rescuer = activeRescue.RescuerId;
        activeRescue = null;
        if (rescuer == 0) { rescueLockedByMe = false; if (!DarkwoodDownedPatch.LocalDowned) global::Core.forbidInputs = false; }
        BroadcastRescueProgress(target, rescuer, 0f, false);
        log?.LogInfo("营救取消（进度归零，双方解锁）。");
    }

    private void CompleteRescue()
    {
        if (activeRescue == null) return;
        var target = activeRescue.TargetId;
        var rescuer = activeRescue.RescuerId;
        activeRescue = null;
        if (rescuer == 0) { rescueLockedByMe = false; if (!DarkwoodDownedPatch.LocalDowned) global::Core.forbidInputs = false; }
        if (target == 0)
        {
            var player = Player.Instance;
            var maxHealth = player != null ? player.maxHealth : 100f;
            var health = maxHealth * ReviveHealthFraction;
            if (player != null) ReviveLocal(health);
            BroadcastHealth(0, health, maxHealth, false);
        }
        else
        {
            var maxHealth = peerMaxHealths.TryGetValue(target, out var mh) && mh > 0f ? mh : 100f;
            var health = maxHealth * ReviveHealthFraction;
            peerHealths[target] = health;
            peerDowned[target] = false;
            BroadcastHealth(target, health, maxHealth, false);
        }
        BroadcastRescueProgress(target, rescuer, 1f, false);
        log?.LogInfo($"营救完成：玩家 {target} 复活（生命上限的 10%，体力回满）。");
    }

    private void BroadcastRescueProgress(int targetId, int rescuerId, float progress, bool active)
    {
        var message = new RescueProgressMessage(targetId, rescuerId, progress, active);
        var payload = ReplicationProtocolCodec.Encode(message);
        foreach (var readyPeer in readyPeers.ToArray()) Queue(readyPeer, ProtocolMessageType.RescueProgress, payload);
        if (IsHost && active) lastRescueProgress = message;
    }

    private void BroadcastHealth(int playerId, float health, float maxHealth, bool downed)
    {
        if (hostSession == null) return;
        var payload = ReplicationProtocolCodec.Encode(new PlayerHealthMessage(playerId, health, maxHealth, downed));
        foreach (var readyPeer in readyPeers.ToArray()) Queue(readyPeer, ProtocolMessageType.PlayerHealth, payload);
    }

    private Vector3 GetPlayerPosition(int playerId)
    {
        if (playerId == 0) { var player = Player.Instance; return player != null ? player.transform.position : Vector3.zero; }
        if (remotePlayerPositions.TryGetValue(playerId, out var position)) return position;
        return Vector3.zero;
    }

    private static float SqrDistance(Vector3 a, Vector3 b) { var dx = a.x - b.x; var dy = a.y - b.y; var dz = a.z - b.z; return dx * dx + dy * dy + dz * dz; }

    private void ScanMonsterDamage()
    {
        if (hostSession == null || readyPeers.Count == 0 || registry == null || Time.unscaledTime < nextMonsterDamageScan) return;
        nextMonsterDamageScan = Time.unscaledTime + MonsterDamageScanInterval;
        foreach (var pair in replication.AllEntities)
        {
            var monster = pair.Value as Character;
            if (monster == null || !monster.alive || !monster.gameObject.activeSelf || monster.aggressiveness == Aggressiveness.neutral) continue;
            var monsterPosition = monster.transform.position;
            foreach (var peer in readyPeers.ToArray())
            {
                if (peerDowned.TryGetValue(peer, out var downed) && downed) continue;
                if (!remotePlayerPositions.TryGetValue(peer, out var guestPosition)) continue;
                if (SqrDistance(monsterPosition, guestPosition) > MonsterReach * MonsterReach) continue;
                if (Time.unscaledTime < (nextGuestHitAllowed.TryGetValue(peer, out var allowed) ? allowed : 0f)) continue;
                nextGuestHitAllowed[peer] = Time.unscaledTime + MonsterHitCooldown;
                var monsterDamage = monster.sensorTypes != null && monster.sensorTypes.Count > 0 ? Mathf.Max(1, monster.sensorTypes[0].damage) : 5;
                var health = Mathf.Max(0f, peerHealths.TryGetValue(peer, out var current) ? current : 100f) - monsterDamage;
                peerHealths[peer] = health;
                var maxHealth = peerMaxHealths.TryGetValue(peer, out var mh) && mh > 0f ? mh : 100f;
                if (health <= 0f)
                {
                    peerDowned[peer] = true;
                    BroadcastHealth(peer, 0f, maxHealth, true);
                    log?.LogWarning($"玩家 {peer} 被怪物击倒。");
                    CheckAllDowned();
                }
                else BroadcastHealth(peer, health, maxHealth, false);
            }
        }
    }

    private void SyncHostHealth()
    {
        if (hostSession == null || readyPeers.Count == 0) return;
        var player = Player.Instance;
        if (player == null) return;
        if (Mathf.Abs(lastBroadcastHostHealth - player.health) > 0.01f || Time.unscaledTime >= nextHealthHeartbeat)
        {
            lastBroadcastHostHealth = player.health;
            nextHealthHeartbeat = Time.unscaledTime + 1f;
            BroadcastHealth(0, player.health, player.maxHealth, hostDownedLocal);
        }
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
            if(queue.Count==0){outgoing.Remove(peer);}
        }
    }
    private void SendLocalPose(){if(!DarkwoodPlayerAdapter.TryCapture(out var p)||clientSession==null)return;var player=Player.Instance;var maxHealth=player!=null?player.maxHealth:100f;var flags=p.Flags;if(DarkwoodDownedPatch.LocalDowned)flags|=PlayerPoseFlags.Downed;clientSession.Send(ProtocolMessageType.PlayerPose,ReplicationProtocolCodec.Encode(new PlayerPoseMessage(clientSession.PeerId,++poseSequence,p.Scene,p.Position.x,p.Position.y,p.Position.z,p.Rotation.x,p.Rotation.y,p.Rotation.z,p.Rotation.w,maxHealth,flags,p.TorsoClip,p.TorsoFrame,p.LegsClip,p.LegsFrame)));}
    private void SendHostPose(int peer){if(!DarkwoodPlayerAdapter.TryCapture(out var p))return;var player=Player.Instance;var maxHealth=player!=null?player.maxHealth:100f;var flags=p.Flags;if(DarkwoodDownedPatch.LocalDowned)flags|=PlayerPoseFlags.Downed;Queue(peer,ProtocolMessageType.PlayerPose,ReplicationProtocolCodec.Encode(new PlayerPoseMessage(0,++poseSequence,p.Scene,p.Position.x,p.Position.y,p.Position.z,p.Rotation.x,p.Rotation.y,p.Rotation.z,p.Rotation.w,maxHealth,flags,p.TorsoClip,p.TorsoFrame,p.LegsClip,p.LegsFrame)));}
    private void SendHostPose(){foreach(var peer in readyPeers.ToArray())SendHostPose(peer);}
    private void FailClient(string code,Exception error){if(sessionError.Length>0)return;sessionError=code+": "+error.Message;log?.LogError($"Standalone DMF session failed [{code}]: {error}");try{clientSession?.Fail(sessionError);}catch{}SetState(ConnectionState.Failed);}
    private string HostKey(){using var sha=System.Security.Cryptography.SHA256.Create();var value=(addressConfig?.Value??"host")+":"+Port;return BitConverter.ToString(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(value)),0,8).Replace("-","");}
}
