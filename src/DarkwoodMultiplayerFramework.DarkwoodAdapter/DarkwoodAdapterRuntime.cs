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
using DarkwoodMultiplayerFramework.DarkwoodAdapter.World;
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
    // P0-D/E：权威 HeldItem 读写（World/Entities 经接口访问）。
    void IMultiplayerRuntimeHost.SetHeldItem(int peer, HeldItemStatePayload? held) { if (held is null) HeldItems.Remove(peer); else HeldItems[peer] = held.Value; }
    bool IMultiplayerRuntimeHost.TryGetHeldItem(int peer, out HeldItemStatePayload held) => HeldItems.TryGetValue(peer, out held);
    public static DarkwoodAdapterRuntime? Instance { get; private set; }
    public bool ClientSaveLoadPending { get => SaveState.ClientSaveLoadPending; set => SaveState.ClientSaveLoadPending = value; }
    public static void LogMessage(string message) => Instance?.log?.LogInfo(message);
    public ConnectionState State { get; private set; } = ConnectionState.Disconnected;
    // FIX-019：当前 clientSession 是否已用"本次下载的存档"加载出新鲜世界。
    // true=本会话刚加载（重连时可安全跳过重载）；false=上次会话失败留下的残留世界或从未加载（必须重载）。
    private bool clientWorldFreshForSession;
    // FIX-020：本进程内是否曾成功用 DMF 下载存档加载出世界。用于区分"干净单机世界（重载安全，28 号实证）
    // vs DMF 残留脏世界（重载会破坏 Darkwood 常驻单例 → NRE 洪水，需提示重启）"。
    private bool everLoadedDmfWorld;
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
    public DarkwoodPlayerService Players { get; private set; } = null!;
    // P0-D/E：每玩家的权威鼠标手持物品（远端玩家由 Host 维护；Host 本机走原版 pickedUpItem，不需这里）。
    public readonly Dictionary<int, HeldItemStatePayload> HeldItems = new Dictionary<int, HeldItemStatePayload>();
    // P0-2：ContainerGrab 请求发送前保存原始 InvItemClass 快照（copy constructor 保留 UIInvItem/slot），
    //        ack 后据此恢复原版 cursor 吸附（绝不重建残缺 InvItemClass）。
    private readonly Dictionary<Guid, InvItemClass> pendingGrabSnapshots = new Dictionary<Guid, InvItemClass>();
    // P0-I：AuthorityReplayScope —— 客户端在 Host Accepted 后，在该作用域内直接执行 Darkwood 原版交互方法。
    // Scope 内：replication.ApplyingRemote=true + ReplayingAuthoritativeAction=true —— 所有 Interaction Patch 据此放行原版且绝不二次发 Intent。
    private int replayScopeDepth;
    public bool ReplayingAuthoritativeAction => replayScopeDepth > 0;
    public AuthorityReplayScopeHandle BeginAuthorityReplay()
    {
        replayScopeDepth++;
        replication.BeginRemoteApply();
        return new AuthorityReplayScopeHandle(this);
    }
    public sealed class AuthorityReplayScopeHandle : IDisposable
    {
        private readonly DarkwoodAdapterRuntime runtime;
        public AuthorityReplayScopeHandle(DarkwoodAdapterRuntime runtime) { this.runtime = runtime; }
        public void Dispose()
        {
            if (runtime.replayScopeDepth > 0) runtime.replayScopeDepth--;
            if (runtime.replayScopeDepth <= 0) { runtime.replayScopeDepth = 0; runtime.replication.EndRemoteApply(); }
        }
    }
    /// <summary>存档/快照传输服务（传输状态与就绪标志的唯一入口）。</summary>
    internal DarkwoodSaveTransferService SaveState { get; private set; } = null!;
    internal DarkwoodWorldAuthorityService World { get; private set; } = null!;
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
    private float scheduledStopAt;
    private float nextRegistryAudit;
    private float nextLoadDiag;
    private float nextSyncDiag;
    private float nextGhostScan;
    private float nextRegistryRebuildWarnAt;
    /// <summary>权威注册表代际：主机每次重建递增；客户端按代际决定是否清空旧映射重绑。</summary>
    private int registryGeneration;
    /// <summary>主机权威描述符清单（构建 BindingManifest 用，World Stable 后一次生成）。</summary>
    private EntityBindingEntryWire[] authoritativeDescriptors = Array.Empty<EntityBindingEntryWire>();
    private byte[] bindingManifestBytes = Array.Empty<byte>();
    private Guid bindingTransferId;
    private Coroutine? hostRegistryStabilizer;
    /// <summary>主机 World Stable 是否已达成（StabilizeHostRegistry 提交权威注册表后才为 true）。</summary>
    private bool hostRegistryStable;
    /// <summary>客户端：本地候选就绪标志（稳定化循环完成后置位，替代本地 hash 注册表握手）。</summary>
    private bool clientCandidatesReady;
    private int clientCandidatesCount;
    private string clientCandidateDigest = string.Empty;
    private int lastBindingGeneration = -1;
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
        hostRegistryStable = false;
        hostRegistryStabilizer = StartCoroutine(StabilizeHostRegistry()); // World Stable → Build generation → Binding → Snapshot → Ready
    }

    public void ConnectClient()
    {
        if (clientSession != null && (clientSession.Session.Lifecycle.State == ConnectionState.SaveTransfer
            || clientSession.Session.Lifecycle.State == ConnectionState.LoadingSave
            || clientSession.Session.Lifecycle.State == ConnectionState.BuildingRegistry
            || clientSession.Session.Lifecycle.State == ConnectionState.ApplyingSnapshot))
        {
            // 真机教训：加载存档期间重复连接会触发多次 LoadScene，打断正在进行的
            // WorldGenerator 生成 → SaveManager 永远不初始化 → 重连死循环（SaveManager 不可用）。
            log?.LogWarning("客户端正在加载主机存档/等待快照，忽略新的连接请求（请等待当前流程完成后再按 F2）。");
            return;
        }
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
        clientWorldFreshForSession=false;
        if (hostSession != null) foreach (var peer in readyPeers.ToArray()) Players.PersistGuestProfile(peer);
        if (clientSession != null) { clientSession.Dispose(); clientSession = null; }
        if (hostSession != null) { hostSession.Dispose(); hostSession = null; }
        if(hostLootScaleCoroutine!=null){StopCoroutine(hostLootScaleCoroutine);hostLootScaleCoroutine=null;}
        outgoing.Clear(); readyPeers.Clear(); pendingActions.Clear(); resyncedDropRequests.Clear();Players.Reset();SaveState.Reset();actionCache.Clear();cachedActionResults.Clear();cachedActionRejections.Clear();cachedActionOwners.Clear();missingEntities.Clear(); nextInventoryDelta=0f; nextProfileAutosave=0f; hostLootScaleScanComplete=false; hostLootScaleScanStarted=false; replication.RestoreSimulation(); replication.ResetDeltaDiagnostics(); clientCandidatesReady=false; clientCandidatesCount=0; clientCandidateDigest=string.Empty; lastBindingGeneration=-1; bindingAssembler=null;  ActiveClientSaveDirectory=string.Empty; sessionError=string.Empty; scheduledStopAt=0f; Combat?.Reset();
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
        SaveState = new DarkwoodSaveTransferService(this);
        World = new DarkwoodWorldAuthorityService(this, RuntimeEntities);
        Players.RemotePlayers.Logger = message => log?.LogInfo(message);
        lastScene = CurrentScene;
        RegisterMessageHandlers(); // 消息路由处理器注册
        SceneManager.sceneLoaded += OnSceneLoaded;
        // P0（World State Adapter）：注册首批 typed 业务状态适配器。具体类型先注册（最具体优先匹配）。
        replication.Adapters.Register(new World.CharacterStateAdapter());
        replication.Adapters.Register(new World.BearTrapStateAdapter());
        replication.Adapters.Register(new World.DoorStateAdapter());
        replication.Adapters.Register(new World.WindowStateAdapter());
        replication.Adapters.Register(new World.GenericItemStateAdapter());
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
        // 主机注册表只由 StabilizeHostRegistry 提交（World Stable 后才允许）；Update 不再抢先 RebuildRegistry。
        if (registryDirty && Session.IsHost && readyPeers.Count > 0 && Time.unscaledTime >= nextRegistryRebuildWarnAt)
        {
            // 联机 Ready 后禁止无协议 Clear + Rebuild 整个注册表（场景切换会先通知客户端重连）
            nextRegistryRebuildWarnAt = Time.unscaledTime + 10f;
            log?.LogWarning("注册表需要重建但已有客户端就绪：禁止无协议重建（等待客户端全部断开或场景切换重连）。");
        }
        // 第七刀：主机/客户端周期逻辑分离（单一入口，不再 if/else 交错）。
        if (Session.IsHost) TickHost();
        else if (Session.IsClient) TickClient();
        Combat.PollRescueHotkey();
        if(scheduledStopAt>0f&&Time.unscaledTime>=scheduledStopAt){scheduledStopAt=0f;StopNetwork();}
        Combat.TickClient();
    }

    internal DarkwoodMultiplayerFramework.Protocol.InventoryStateMessage CaptureAuthoritativeInventoryForHost(DarkwoodMultiplayerFramework.Core.EntityId id) => replication.CaptureAuthoritativeInventory(id);

    /// <summary>主机权威注册表稳定化：每秒真实扫描 Scene，按 Component type+InstanceID 指纹判定 World Stable（连续 3 次一致），
        /// 然后把最后一次扫描结果一次性提交为权威注册表（registry 与 replication 同源）。</summary>
        private IEnumerator StabilizeHostRegistry()
        {
            yield return new WaitForSecondsRealtime(2f);
        if (hostSession == null) yield break;
        var deadline = Time.realtimeSinceStartup + 150f;
        var previousFingerprint = string.Empty;
        var stableChecks = 0;
        var lastScanned = Array.Empty<Component>();
        while (Time.realtimeSinceStartup < deadline)
        {
            if (hostSession == null) yield break;
            lastScanned = scanner.ScanScene().ToArray();
            var fingerprint = ScanFingerprint(lastScanned);
            if (fingerprint == previousFingerprint) { stableChecks++; if (stableChecks >= 3) break; }
            else { stableChecks = 0; previousFingerprint = fingerprint; }
            yield return new WaitForSecondsRealtime(1f);
        }
        if (hostSession == null) yield break;
        if (stableChecks < 3)
        {
            if (lastScanned.Length == 0) { log?.LogWarning("主机注册表稳定化超时（150 秒）且无可用扫描，客户端将无法加入。"); yield break; }
            log?.LogWarning("主机注册表稳定化超时（150 秒），以最后一次扫描结果提交权威注册表。");
        }
        CommitAuthoritativeRegistry(lastScanned);
        hostRegistryStable = true;
        log?.LogInfo($"主机权威注册表已稳定并提交：第 {registryGeneration} 代，{registry.Count} 实体，{authoritativeDescriptors.Length} 描述符，指纹 {previousFingerprint}，场景 {CurrentScene}。");
        // 稳定器就绪前挂起的快照请求现在补发（Manifest → Snapshot → Ready）
        foreach (var request in SaveState.DrainPendingSnapshotRequests())
            if (hostSession != null) PrepareSnapshot(request.Key, request.Value);
    }

    private static string ScanFingerprint(Component[] components)
    {
        var identities = new List<EntityScanFingerprint.ScanIdentity>(components.Length);
        foreach (var c in components) identities.Add(new EntityScanFingerprint.ScanIdentity(c.GetType().Name, c.transform.GetInstanceID()));
        return EntityScanFingerprint.Compute(identities);
    }

    private float nextWorldAudit;
    /// <summary>P0 大世界扫描审计：统计注册表内对象上全部 MonoBehaviour 类型，标出尚未被任何
    /// WorldStateAdapter 覆盖（无 typed 同步）的类型——即"还有哪些大世界对象状态不进入同步"。
    /// 排除基础视觉/物理/音效组件与 Inventory（专用协议）。</summary>
    private static readonly HashSet<string> AuditedCoreTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Renderer","SpriteRenderer","Transform","RectTransform","CanvasRenderer","MeshRenderer","SkinnedMeshRenderer",
        "AudioSource","AudioListener","ParticleSystem","Light","Camera","Collider","Collider2D",
        "BoxCollider","BoxCollider2D","CircleCollider2D","PolygonCollider2D","CapsuleCollider2D","SphereCollider",
        "Rigidbody","Rigidbody2D","tk2dSprite","tk2dSpriteAnimator","Animator","Animation","TrailRenderer","LineRenderer",
    };
    /// <summary>P0-H：客户端周期诊断——扫描世界掉落物（itemInv/deathDrop），未注册且在 runtime mirror/replication 之外 = ghost（联机下必须为 0）。</summary>
    private void ScanGhostDroppedItems()
    {
        if (clientSession?.Session.Lifecycle.State != ConnectionState.Ready) return;
        var ghosts = new List<string>();
        foreach (var inv in UnityEngine.Object.FindObjectsOfType<Inventory>())
        {
            if (inv == null) continue;
            if (inv.invType != Inventory.InvType.itemInv && inv.invType != Inventory.InvType.deathDrop) continue;
            if (RuntimeEntities.IsKnownDroppedMirror(inv)) continue;
            if (replication.TryGetId(inv, out _)) continue;
            var it = inv.slots != null && inv.slots.Count > 0 ? inv.slots[0].invItem : null;
            ghosts.Add($"{inv.name}@{inv.transform.position} {(it != null ? it.type : "?")}");
            if (ghosts.Count >= 5) break;
        }
        if (ghosts.Count > 0) log?.LogWarning($"[RUNTIME-GHOST] 发现 {ghosts.Count} 个本地未注册掉落物（联机下必须为 0）：{string.Join(" | ", ghosts)}");
    }

    private void RunWorldAudit()
    {
        if (registry == null || registry.Count == 0) return;
        int ch = 0, dr = 0, wn = 0, it = 0, inv = 0;
        var unreplicated = new Dictionary<string, (int Count, string Example)>(StringComparer.Ordinal);
        foreach (var pair in replication.EntitySnapshot())
        {
            var component = pair.Value;
            if (component == null || component.gameObject == null) continue;
            if (component is Character) ch++; else if (component is Door) dr++; else if (component is Window) wn++; else if (component is Item) it++; else if (component is Inventory) inv++;
            var gameObject = component.gameObject;
            foreach (var mb in gameObject.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (mb == null) continue;
                var typeName = mb.GetType().Name;
                if (typeName == component.GetType().Name) continue;             // 主组件本身（有 adapter）
                if (mb is Inventory) continue;                                   // 专用协议
                if (AuditedCoreTypes.Contains(typeName)) continue;               // 基础视觉/物理/音效
                if (replication.Adapters.Resolve(mb) != null) continue;          // 已有 adapter
                var example = "";
                if (!unreplicated.TryGetValue(typeName, out var rec)) { rec = (0, gameObject.name); }
                rec.Count++; unreplicated[typeName] = rec;
            }
        }
        log?.LogInfo($"[WORLD-AUDIT] Character={ch} Door={dr} Window={wn} Item={it} Inventory={inv} (合计 {ch + dr + wn + it + inv})");
        var top = unreplicated.OrderByDescending(kv => kv.Value.Count).Take(30).ToArray();
        if (top.Length == 0) { log?.LogInfo("[WORLD-AUDIT] 无从属未覆盖组件类型。"); return; }
        foreach (var kv in top)
            log?.LogInfo($"[WORLD-AUDIT] unreplicated: {kv.Key} x{kv.Value.Count}（例：{kv.Value.Example}）");
        if (unreplicated.Count > top.Length) log?.LogInfo($"[WORLD-AUDIT] 其余未列类型 {unreplicated.Count - top.Length} 个。");
    }

    private void TickHost()
    {
        // 诊断：30 秒注册表巡检（确认主机世界是否仍在加载/实体数是否增长）
        if (Time.unscaledTime >= nextRegistryAudit)
        {
            nextRegistryAudit = Time.unscaledTime + 30f;
            log?.LogInfo($"主机注册表巡检：{registry?.Count ?? 0} 实体 / 共享容器 {replication.SharedInventoryCount} / 运行时实体 {RuntimeEntities.PendingCount}。");
        }
        if (Time.unscaledTime >= nextWorldAudit)
        {
            nextWorldAudit = Time.unscaledTime + 60f;
            RunWorldAudit();
        }
        if (Time.unscaledTime >= nextSyncDiag)
        {
            nextSyncDiag = Time.unscaledTime + 10f;
            log?.LogInfo($"[SYNC] host generation={registryGeneration} entities={registry?.Count ?? 0} deltaChanged={replication.LastDeltaChangedCount} deltaSent={replication.LastDeltaSentCount}");
            // P1：per-kind 同步统计（定位怪物/门/窗/Item 哪类不同步）
            log?.LogInfo($"[SYNC-KIND] Character changed={replication.KindChanged[1]} sent={replication.KindSent[1]} | Door changed={replication.KindChanged[2]} sent={replication.KindSent[2]} | Window changed={replication.KindChanged[3]} sent={replication.KindSent[3]} | Item changed={replication.KindChanged[4]} sent={replication.KindSent[4]} | Inventory changed={replication.KindChanged[5]} sent={replication.KindSent[5]}");
            replication.ResetKindDiagnostics();
        }
        if(hostSession!=null&&!registryDirty&&registry!=null)
            try{EnsureHostExistingLootScaled();}catch(Exception error){log?.LogError($"TickHost.LootScale 子系统异常（已隔离）：{error}");}
        // P0-3：TickHost 各子系统隔离——单个坏世界对象/异常不能让整个网络主循环掉帧。各自 log error + 继续。
        if (hostSession != null && readyPeers.Count>0 && !registryDirty && Time.unscaledTime>=nextDelta)
        {
            nextDelta=Time.unscaledTime+(1f/15f); serverTick++;
            try
            {
                var delta=replication.CaptureDeltas();
                var despawns=replication.TakePendingAuthoritativeDespawns();
                if(delta.Length>0||despawns.Length>0)
                {
                    var payload=ReplicationProtocolCodec.Encode(new EntityDeltaMessage(CurrentScene,serverTick,delta,despawns));
                    foreach(var peer in readyPeers.ToArray())Queue(peer,ProtocolMessageType.EntityDelta,payload);
                    if(despawns.Length>0)log?.LogInfo($"[WORLD-LIFE] persistent despawn 广播 {despawns.Length} 个（tick {serverTick}）。");
                }
            }
            catch(Exception error){log?.LogError($"TickHost.Delta 子系统异常（已隔离）：{error}");}
        }
        if (hostSession != null && readyPeers.Count>0 && !registryDirty && Time.unscaledTime>=nextInventoryDelta)
        {
            nextInventoryDelta=Time.unscaledTime+0.25f;
            try
            {
                foreach(var inventory in replication.CaptureInventoryDeltas()) BroadcastInventory(inventory);
            }
            catch(Exception error){log?.LogError($"TickHost.Inventory 子系统异常（已隔离）：{error}");}
        }
        if(hostSession!=null&&readyPeers.Count>0&&Time.unscaledTime>=nextPose)
        {
            nextPose=Time.unscaledTime+(1f/15f);
            try{SendHostPose();}catch(Exception error){log?.LogError($"TickHost.Pose 子系统异常（已隔离）：{error}");}
        }
        if(hostSession!=null&&readyPeers.Count>0&&Time.unscaledTime>=nextProfileAutosave)
        {
            nextProfileAutosave=Time.unscaledTime+ProfileAutosaveSeconds;
            try{foreach(var peer in readyPeers.ToArray())Players.PersistGuestProfile(peer);}catch(Exception error){log?.LogError($"TickHost.ProfileAutosave 子系统异常（已隔离）：{error}");}
        }
        if(hostSession!=null)
        {
            try{Combat.TickHost();}catch(Exception error){log?.LogError($"TickHost.Combat 子系统异常（已隔离）：{error}");}
            try{RuntimeEntities.TickHost();}catch(Exception error){log?.LogError($"TickHost.RuntimeEntities 子系统异常（已隔离）：{error}");}
        }
    }

    private void TickClient()
    {
        if(clientSession?.Session.Lifecycle.State==ConnectionState.Ready)
            try{replication.Interpolate(Time.unscaledDeltaTime*12f);}catch(Exception error){log?.LogError($"TickClient.Interpolate 异常（已隔离）：{error}");}
        if (Time.unscaledTime >= nextSyncDiag)
        {
            nextSyncDiag = Time.unscaledTime + 10f;
            log?.LogInfo($"[SYNC] client generation={replication.RegistryGeneration} bound={replication.BoundEntityCount} delta received={replication.DeltaReceived} applied={replication.DeltaApplied} missing={replication.DeltaMissing}");
            log?.LogInfo($"[SYNC-KIND] Character received={replication.KindReceived[1]} applied={replication.KindApplied[1]} missing={replication.KindMissing[1]} | Door received={replication.KindReceived[2]} applied={replication.KindApplied[2]} missing={replication.KindMissing[2]} | Window received={replication.KindReceived[3]} applied={replication.KindApplied[3]} missing={replication.KindMissing[3]} | Item received={replication.KindReceived[4]} applied={replication.KindApplied[4]} missing={replication.KindMissing[4]} | Inventory received={replication.KindReceived[5]} applied={replication.KindApplied[5]} missing={replication.KindMissing[5]}");
            replication.ResetKindDiagnostics();
        }
        if (Time.unscaledTime >= nextGhostScan)
        {
            nextGhostScan = Time.unscaledTime + 30f;
            ScanGhostDroppedItems();
        }
        TrySendClientRegistryReady();
        RetrySnapshotAcknowledgement();
        if(clientSession?.Session.Lifecycle.State==ConnectionState.Ready&&Time.unscaledTime>=nextPose){nextPose=Time.unscaledTime+(1f/15f);SendLocalPose();}
        if(clientSession!=null&&clientSession.Session.Lifecycle.State==ConnectionState.LoadingSave&&SaveState.LoadStartedAt>0f&&Time.unscaledTime-SaveState.LoadStartedAt>900f)FailClient("SAVE_LOAD_TIMEOUT",new TimeoutException("存档加载超时（900 秒未完成）。主机存档过大或客户端过慢时请观察加载进度是否持续前进。"));
        if(clientSession!=null&&clientSession.Session.Lifecycle.State==ConnectionState.LoadingSave){var loadSeconds=Time.unscaledTime-SaveState.LoadStartedAt;if(loadSeconds>180f){FailClient("LOAD_TIMEOUT",new TimeoutException("存档加载超过 180 秒无完成，判定超时。"));return;}var worldGen=Singleton<WorldGenerator>.Instance;if(worldGen==null){if(Time.unscaledTime>=nextLoadDiag){nextLoadDiag=Time.unscaledTime+5f;LogMessage($"加载诊断：WorldGenerator 实例为空（场景生成未启动），已等待 {loadSeconds:F0} 秒。");}return;}var percent=(int)worldGen.percentLoaded;SaveState.SetProgress($"正在加载存档…({percent}%)");if(Time.unscaledTime>=nextLoadDiag){nextLoadDiag=Time.unscaledTime+5f;LogMessage($"加载诊断：WorldGenerator.percentLoaded={percent}%（已用 {loadSeconds:F0} 秒）。");}var bucket=percent/10;if(bucket>SaveState.LastLoadBucket){SaveState.MarkLoadBucket(bucket);LogMessage($"存档加载进度 {percent}%（已用 {loadSeconds:F0} 秒）。");}}
        if(autoReconnectAt>0f&&Time.unscaledTime>=autoReconnectAt){autoReconnectAt=0f;log?.LogInfo("场景切换自动重连：正在重新连接主机……");ConnectClient();}
    }

    private void TrySendClientRegistryReady()
    {
        if (SaveState.IsSnapshotReady || SaveState.IsSnapshotManifestReceived || clientSession == null || !clientSession.HandshakeComplete || registryDirty || !clientCandidatesReady || Player.Instance == null) return;
        if (!SaveState.IsRegistryStabilized) return; // 等待本地候选稳定化循环完成（世界流式加载）
        var lifecycle = clientSession.Session.Lifecycle;
        if (lifecycle.State == ConnectionState.LoadingSave) lifecycle.MoveTo(ConnectionState.BuildingRegistry);
        if (lifecycle.State != ConnectionState.BuildingRegistry && lifecycle.State != ConnectionState.ApplyingSnapshot) return;
        if (SaveState.IsRegistryRequestSent && Time.realtimeSinceStartup < SaveState.NextRegistryRequestRetry) return;
        SaveState.MarkRegistryRequestSent();
        SaveState.MarkRegistryRequestRetry(Time.realtimeSinceStartup + 5f);
        SaveState.SetProgress("正在请求世界快照");
        log?.LogInfo($"客户端已发送注册表握手：{clientCandidatesCount} 个本地候选，摘要 {clientCandidateDigest}，场景 {CurrentScene}。");
        try
        {
            clientSession.Send(ProtocolMessageType.Ready, ReplicationProtocolCodec.Encode(new ReadyMessage(CurrentScene, clientCandidateDigest)));
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
        hostRegistryStable = false;
        hostLootScaleScanComplete = false;
        if (hostSession != null)
        {
            if (hostRegistryStabilizer != null) StopCoroutine(hostRegistryStabilizer);
            hostRegistryStabilizer = StartCoroutine(StabilizeHostRegistry()); // 新场景 → 重新稳定化并提交新代注册表
        }
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

    /// <summary>一次性提交权威注册表：registry 与 replication 使用同一份已捕获扫描（组件→ID 只算一次）。</summary>
    private void CommitAuthoritativeRegistry(Component[] components)
    {
        var next = new EntityRegistry<Component>();
        var pairs = new List<KeyValuePair<EntityId, Component>>();
        var collisions = 0;
        foreach (var component in components)
        {
            var id = scanner.ToPersistentId(component);
            try { next.Register(id, component); pairs.Add(new KeyValuePair<EntityId, Component>(id, component)); }
            catch (InvalidOperationException)
            {
                collisions++;
                log?.LogWarning($"Duplicate Darkwood entity id {id} for {component.GetType().Name} at {component.transform.name}.");
            }
        }
        registry = next;
        replication.Rebuild(pairs);
        registryGeneration++;
        authoritativeDescriptors = scanner.BuildAuthoritativeDescriptors(pairs);
        bindingManifestBytes = ReplicationProtocolCodec.Encode(authoritativeDescriptors);
        bindingTransferId = Guid.NewGuid();
        RegistryDigest = next.ComputeDigest();
        registryDirty = false;
        log?.LogInfo($"实体注册表已就绪（第 {registryGeneration} 代）：{next.Count} 个实体，{collisions} 个 ID 冲突，描述符 {authoritativeDescriptors.Length} 个，摘要 {RegistryDigest}，场景 {CurrentScene}。");
    }
}
