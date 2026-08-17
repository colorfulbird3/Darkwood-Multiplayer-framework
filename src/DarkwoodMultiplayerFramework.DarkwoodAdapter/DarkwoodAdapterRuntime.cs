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
public sealed partial class DarkwoodAdapterRuntime : MonoBehaviour, IMultiplayerRuntimeHost
{
    // ── IMultiplayerRuntimeHost：Service 最小依赖面（0.8.9 收口）──
    IReadOnlyCollection<int> IMultiplayerRuntimeHost.ReadyPeers => readyPeers;
    long IMultiplayerRuntimeHost.ServerTick => serverTick;
    DarkwoodEntityReplication IMultiplayerRuntimeHost.Replication => replication;
    DarkwoodPlayerService IMultiplayerRuntimeHost.Players => Players;
    int IMultiplayerRuntimeHost.LocalPeerId => Session.LocalPeerId;
    void IMultiplayerRuntimeHost.Queue(int peer, ProtocolMessageType type, byte[] payload) => Queue(peer, type, payload);
    void IMultiplayerRuntimeHost.SendToHost(ProtocolMessageType type, byte[] payload) { if (clientSession != null) clientSession.Send(type, payload); }
    void IMultiplayerRuntimeHost.ScheduleStop(float delay) => ScheduleStop(delay);
    void IMultiplayerRuntimeHost.LogInfo(string message) => log?.LogInfo(message);
    void IMultiplayerRuntimeHost.LogWarning(string message) => log?.LogWarning(message);
    public static DarkwoodAdapterRuntime? Instance { get; private set; }
    public bool ClientSaveLoadPending { get => SaveState.ClientSaveLoadPending; set => SaveState.ClientSaveLoadPending = value; }
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
        public string TransferProgress => SaveState.TransferProgressValue; // 状态归服务，只读
    public bool IsMultiplayerActive => hostSession?.IsActive == true || clientSession?.HandshakeComplete == true;
    public bool AllDowned => DarkwoodDownedPatch.AllDowned;
    public RescueProgressMessage LastRescueProgress => Combat?.LastRescueProgress ?? default;
    public bool TryGetKnownPlayerPosition(int playerId, out Vector3 position)
    {
        var myId = IsHost ? 0 : (clientSession?.PeerId ?? -1);
        if (playerId == myId) { var player = Player.Instance; if (player != null) { position = player.transform.position; return true; } position = Vector3.zero; return false; }
        if (IsHost && Players.TryGetRemotePosition(playerId, out position)) return true;
        if (Players.RemotePlayers.TryGetPosition(playerId, out position)) return true;
        position = Vector3.zero;
        return false;
    }

    internal readonly DarkwoodEntityScanner scanner = new DarkwoodEntityScanner();
    /// <summary>第二刀：会话上下文（角色/状态/身份/场景的权威状态源）。</summary>
    public SessionContext Session { get; } = new SessionContext();
    /// <summary>运行时实体服务（所有权拆分——所有临时生成对象的唯一入口）。</summary>
    internal DarkwoodRuntimeEntityService RuntimeEntities { get; private set; } = null!;
    /// <summary>战斗服务（血量/倒地/怪物伤害/攻击锚点/无敌/营救会话的唯一入口）。</summary>
    internal DarkwoodCombatService Combat { get; private set; } = null!;
    /// <summary>玩家服务（远端坐标/背包影子/Guest 档案的唯一入口）。</summary>
    internal DarkwoodPlayerService Players { get; private set; } = null!;
    /// <summary>存档/快照传输服务（传输状态与就绪标志的唯一入口）。</summary>
    internal DarkwoodSaveTransferService SaveState { get; private set; } = null!;
    internal EntityRegistry<Component>? registry;
    internal ManualLogSource? log;
    private string lastScene = string.Empty;
    private bool registryDirty = true;
    private readonly HashSet<string> scaledHostInventoryKeys = new HashSet<string>(StringComparer.Ordinal);
    private bool hostLootScaleScanComplete;
    private bool hostLootScaleScanStarted;
    private Coroutine? hostLootScaleCoroutine;
    private string lootScaleLedgerPath = string.Empty;
    internal HostHandshakeSession? hostSession;
    internal ClientHandshakeSession? clientSession;
    private string telepathyPath = string.Empty;
    /// <summary>自测：Telepathy 传输 DLL 路径（供回环自测客户端复用）。</summary>
    public string TelepathyPath => telepathyPath;
    private bool f1WasDown;
    private bool f2WasDown;
    private bool f3WasDown;
    internal readonly DarkwoodEntityReplication replication = new DarkwoodEntityReplication();
    /// <summary>实体 ID 反查（容器并发补偿用）。</summary>
    public bool TryGetEntityId(Component component,out EntityId id)=>replication.TryGetId(component,out id);

    private readonly Dictionary<int, Queue<OutgoingPacket>> outgoing = new Dictionary<int, Queue<OutgoingPacket>>();
    internal readonly HashSet<int> readyPeers = new HashSet<int>();

    internal long serverTick;

    private float nextDelta;
    private float nextInventoryDelta;

    private float nextRuntimeScan;
    /// <summary>场景切换自动重连时刻（>0 表示待重连）。</summary>
    private float autoReconnectAt;

    private float nextPose;
    private uint poseSequence;
    private string sessionError = string.Empty;
    private readonly ActionIdempotencyCache actionCache = new ActionIdempotencyCache();
    // The protocol response is cached alongside the abstract result so a retry can
    // be answered byte-for-byte without applying the game mutation a second time.
    private readonly Dictionary<Guid,ActionResultMessage> cachedActionResults = new Dictionary<Guid,ActionResultMessage>();
    private readonly Dictionary<Guid,ActionRejectedMessage> cachedActionRejections = new Dictionary<Guid,ActionRejectedMessage>();
    private readonly Dictionary<Guid,int> cachedActionOwners = new Dictionary<Guid,int>();
    private readonly Dictionary<Guid,ActionRequestMessage> pendingActions = new Dictionary<Guid,ActionRequestMessage>();

    /// <summary>自测：自动回环自测开关（配置 SelfTestAuto）。</summary>
    public bool AutoSelfTest => autoSelfTestConfig?.Value ?? false;
    private float nextProfileAutosave;
    private const float ProfileAutosaveSeconds = 30f;
    private float scheduledStopAt; // Combat 服务通过 ScheduleStop 设置
    private long acceptedActions;
    private long rejectedActions;
    private long duplicateActions;

    /// <summary>FIX-007：快照/增量中无法绑定的实体 ID（主机运行时生成物，客户端世界无副本）。
    /// 对这些 ID 的后续库存/状态消息静默忽略，等待 0.8.8 的 Spawn 生命周期补发。</summary>
    private readonly HashSet<EntityId> missingEntities = new HashSet<EntityId>();
    private const float AttackCooldownSeconds = 0.35f;


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



    public void StartHost()
    {
        StopNetwork();
        hostSession = new HostHandshakeSession(new TelepathyServerTransport(telepathyPath), Identity);
        hostSession.PeerAccepted += OnPeerAccepted;
        hostSession.PeerRejected += OnPeerRejected;
        hostSession.PeerDisconnected += OnPeerDisconnected;
        hostSession.MessageReceived += OnHostMessage;
        hostSession.MaxPeers = Math.Max(0, ConfiguredPlayerCount - 1);
        // 玩家状态清理归 Players.Reset()
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
        clientSession.GuestKey = DarkwoodPlayerService.NormalizeGuestKey(playerNameConfig?.Value);
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
        if (hostSession != null) foreach (var peer in readyPeers.ToArray()) Players.PersistGuestProfile(peer);
        if (clientSession != null) { clientSession.Dispose(); clientSession = null; }
        if (hostSession != null) { hostSession.Dispose(); hostSession = null; }
        if(hostLootScaleCoroutine!=null){StopCoroutine(hostLootScaleCoroutine);hostLootScaleCoroutine=null;}
        outgoing.Clear(); readyPeers.Clear(); pendingActions.Clear();Players.Reset();SaveState.Reset();actionCache.Clear();cachedActionResults.Clear();cachedActionRejections.Clear();cachedActionOwners.Clear();missingEntities.Clear(); nextInventoryDelta=0f; nextProfileAutosave=0f; hostLootScaleScanComplete=false; hostLootScaleScanStarted=false; replication.RestoreSimulation();  ActiveClientSaveDirectory=string.Empty; sessionError=string.Empty; scheduledStopAt=0f; Combat?.Reset();
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
        RuntimeEntities = new DarkwoodRuntimeEntityService(this); // 所有权拆分
        Combat = new DarkwoodCombatService(this); // 所有权拆分
        Players = new DarkwoodPlayerService(this, new DarkwoodRemotePlayers()); // 所有权拆分
        SaveState = new DarkwoodSaveTransferService(this); // 所有权拆分
        Players.RemotePlayers.Logger = message => log?.LogInfo(message);
        lastScene = CurrentScene;
        RegisterMessageHandlers(); // 消息路由处理器注册
        SceneManager.sceneLoaded += OnSceneLoaded;
    }

    /// <summary>延迟停服（战斗服务的全员倒地结局回调）。</summary>
    internal void ScheduleStop(float delay) => scheduledStopAt = Time.unscaledTime + delay;

    public void Update()
    {
        PollHotkeys();
        try { hostSession?.Tick(); clientSession?.Tick(); }
        catch (Exception error) { FailClient("TRANSPORT_TICK_FAILED",error); }
        PumpOutgoing();
        Players.RemotePlayers.Tick();
        var scene = CurrentScene;
        if (!string.Equals(scene, lastScene, StringComparison.Ordinal)) MarkSceneChanged(scene);

        SetState(DetectState());
        if (registryDirty && IsNetworkConnected() && Player.Instance != null)
        {
            RebuildRegistry();
            SetState(DetectState());
        }
        // 第七刀：主机/客户端周期逻辑分离（单一入口，不再 if/else 交错）。
        if (Session.IsHost) TickHost();
        else if (Session.IsClient) TickClient();
        Combat.PollRescueHotkey();
        if(scheduledStopAt>0f&&Time.unscaledTime>=scheduledStopAt){scheduledStopAt=0f;StopNetwork();}
        Combat.TickClient();
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
        if(hostSession!=null&&readyPeers.Count>0&&Time.unscaledTime>=nextProfileAutosave){nextProfileAutosave=Time.unscaledTime+ProfileAutosaveSeconds;foreach(var peer in readyPeers.ToArray())Players.PersistGuestProfile(peer);}
        if(hostSession!=null){Combat.TickHost();RuntimeEntities.TickHost();}
    }

    private void TickClient()
    {
        if(clientSession?.Session.Lifecycle.State==ConnectionState.Ready)replication.Interpolate(Time.unscaledDeltaTime*12f);
        TrySendClientRegistryReady();
        RetrySnapshotAcknowledgement();
        if(clientSession?.Session.Lifecycle.State==ConnectionState.Ready&&Time.unscaledTime>=nextPose){nextPose=Time.unscaledTime+(1f/15f);SendLocalPose();}
        if(clientSession!=null&&clientSession.Session.Lifecycle.State==ConnectionState.LoadingSave&&SaveState.LoadStartedAt>0f&&Time.unscaledTime-SaveState.LoadStartedAt>300f)FailClient("SAVE_LOAD_TIMEOUT",new TimeoutException("存档加载超时（300 秒未完成）。请检查主机存档是否损坏。"));
        if(clientSession!=null&&clientSession.Session.Lifecycle.State==ConnectionState.LoadingSave){var worldGen=Singleton<WorldGenerator>.Instance;if(worldGen!=null){var percent=(int)worldGen.percentLoaded;SaveState.SetProgress($"正在加载存档…({percent}%)");var bucket=percent/10;if(bucket>SaveState.LastLoadBucket){SaveState.MarkLoadBucket(bucket);LogMessage($"存档加载进度 {percent}%（已用 {Time.unscaledTime-SaveState.LoadStartedAt:F0} 秒）。");}}}
        if(autoReconnectAt>0f&&Time.unscaledTime>=autoReconnectAt){autoReconnectAt=0f;log?.LogInfo("场景切换自动重连：正在重新连接主机……");ConnectClient();}
    }

    private void TrySendClientRegistryReady()
    {
        if (SaveState.IsSnapshotReady || SaveState.IsSnapshotManifestReceived || clientSession == null || !clientSession.HandshakeComplete || registryDirty || registry == null || Player.Instance == null) return;
        if (!SaveState.IsRegistryStabilized) return; // 等待注册表稳定化循环完成（世界流式加载）
        var lifecycle = clientSession.Session.Lifecycle;
        if (lifecycle.State == ConnectionState.LoadingSave) lifecycle.MoveTo(ConnectionState.BuildingRegistry);
        if (lifecycle.State != ConnectionState.BuildingRegistry && lifecycle.State != ConnectionState.ApplyingSnapshot) return;
        if (SaveState.IsRegistryRequestSent && Time.realtimeSinceStartup < SaveState.NextRegistryRequestRetry) return;
        SaveState.MarkRegistryRequestSent();
        SaveState.MarkRegistryRequestRetry(Time.realtimeSinceStartup + 5f);
        SaveState.SetProgress("正在请求世界快照");
        log?.LogInfo($"客户端已发送注册表握手：{registry.Count} 个实体，摘要 {RegistryDigest}，场景 {CurrentScene}。");
        try
        {
            clientSession.Send(ProtocolMessageType.Ready, ReplicationProtocolCodec.Encode(new ReadyMessage(CurrentScene, RegistryDigest)));
            if (lifecycle.State == ConnectionState.BuildingRegistry) lifecycle.MoveTo(ConnectionState.ApplyingSnapshot);
        }
        catch (Exception error) { SaveState.ClearRegistryRequestSent(); FailClient("REGISTRY_REQUEST_FAILED", error); }
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
        // 场景切换——主机通知所有客户端自动重连（重连走完整握手+新场景存档加载），
        // 并重置运行时实体状态（新场景是全新的运行时世界；Runtime ID 计数器继续单调递增、绝不复用）。
        if (hostSession != null && (scene.Equals("chapter1", StringComparison.Ordinal) || scene.Equals("chapter2", StringComparison.Ordinal)))
        {
            var payload = ReplicationProtocolCodec.Encode(new SceneChangeMessage(scene));
            var notified = 0;
            foreach (var readyPeer in readyPeers.ToArray()) { Queue(readyPeer, ProtocolMessageType.SceneChange, payload); notified++; }
            RuntimeEntities.OnSceneChanged(); // 所有权拆分——场景切换清理由服务自管
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
}
