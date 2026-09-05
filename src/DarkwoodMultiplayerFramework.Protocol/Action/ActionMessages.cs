using System;
using System.IO;
using System.Text;

namespace DarkwoodMultiplayerFramework.Protocol;

public enum ActionKindWire : byte
{
    Pickup = 1,
    ContainerTake = 2,
    ContainerPut = 3,
    Attack = 4,
    DoorInteract = 5,
    WindowInteract = 6,
    ItemActivate = 7,
    DropItem = 8,
    ItemInteract = 9,
    ContainerMove = 10,
    DeployItem = 11,
    ContainerGrab = 12,       // 从共享容器 grab 到鼠标（HeldItem）
    HeldToInventory = 13,     // 鼠标 HeldItem 放回玩家背包
    PlayerGrab = 14,          // 从自己背包/快捷栏 grab 到鼠标（Host HeldItems 权威）
    HeldToContainer = 15,     // 鼠标 HeldItem 放入共享容器（Host 权威：空→放/同类→stack/异类→swap）
    StateObjectInteract = 16, // 世界状态对象交互意图（如发电机 toggle）——Host 执行原版逻辑后即时广播权威状态
    PlayerAction = 17        // 玩家动作事件（本地已执行原版）→ Host Relay 给其他客户端（播放/响应）
}

/// <summary>Drop 来源：决定 Host 从哪个权威状态扣减物品。</summary>
public enum DropOriginWire : byte
{
    /// <summary>玩家自己的背包/快捷栏槽位（Host 从权威影子背包读槽）。</summary>
    PlayerSlot = 0,
    /// <summary>手上物品来自容器（共享容器/尸体/商人）——槽位属于来源容器，Host 从权威容器扣减。</summary>
    SharedContainer = 1,
    /// <summary>鼠标上手持的物品（Host 从该玩家 authoritative HeldItem 扣减）——P0-D/E。</summary>
    HeldItem = 2
}

/// <summary>HeldItem（鼠标手持物品）的权威状态。客户端只据其恢复原版 cursor UI。</summary>
public readonly struct HeldItemStatePayload
{
    public HeldItemStatePayload(string type,int amount,float durability,int quality,bool recipe,int ammo=0)
    {Type=type;Amount=amount;Durability=durability;Quality=quality;Recipe=recipe;Ammo=ammo;}
    public string Type { get; } public int Amount { get; } public float Durability { get; } public int Quality { get; } public bool Recipe { get; } public int Ammo { get; }
    public bool IsEmpty => string.IsNullOrEmpty(Type) || Amount <= 0;
}

public readonly struct ContainerGrabPayload
{
    public ContainerGrabPayload(int slotIndex,int amount){SlotIndex=slotIndex;Amount=amount;}
    public int SlotIndex { get; } public int Amount { get; }
}

/// <summary>P0-3：HeldToInventory 必须带真实目标槽（原版 InvSlot.placeItem 语义：empty→place / 同类可堆叠→stack）。</summary>
public readonly struct HeldToInventoryPayload
{
    public HeldToInventoryPayload(bool fromHotbar,int targetSlot){FromHotbar=fromHotbar;TargetSlot=targetSlot;}
    public bool FromHotbar { get; } public int TargetSlot { get; }
}

/// <summary>P0-E/F：从玩家自己的背包/快捷栏 grab 到鼠标（Host HeldItems 权威）。原版 grab 是整个槽。</summary>
public readonly struct PlayerGrabPayload
{
    public PlayerGrabPayload(bool fromHotbar,int slotIndex){FromHotbar=fromHotbar;SlotIndex=slotIndex;}
    public bool FromHotbar { get; } public int SlotIndex { get; }
}

/// <summary>阶段二：HeldItem 放入共享容器（目标槽）。容器实体 ID 走请求 TargetValue/TargetPersistent——与 HeldToInventory 的权威域不同（世界对象 vs 玩家背包）。</summary>
public readonly struct HeldToContainerPayload
{
    public HeldToContainerPayload(int slotIndex){SlotIndex=slotIndex;}
    public int SlotIndex { get; }
}

/// <summary>阶段二：世界状态对象交互意图（如发电机 toggle）。Host 执行原版逻辑后即时广播权威状态。</summary>
public readonly struct StateObjectIntentPayload
{
    public StateObjectIntentPayload(string interaction){Interaction=interaction??string.Empty;}
    public string Interaction { get; }
}

/// <summary>阶段二/三：玩家动作事件（本地已执行原版 —— 翻窗/交互/动画等）→ Host Relay 到其他客户端播放。</summary>
public readonly struct PlayerActionPayload
{
    public PlayerActionPayload(string action){Action=action??string.Empty;}
    public string Action { get; }
}

/// <summary>Drop 意图：客户端只发槽位与落点，物品属性由 Host 从权威背包读取。</summary>
public readonly struct DropItemPayload
{
    public DropItemPayload(bool fromHotbar,int slotIndex,int amount,float x,float y,float z,float qx,float qy,float qz,float qw,
        DropOriginWire origin = DropOriginWire.PlayerSlot, ulong containerValue = 0, bool containerPersistent = false)
    {FromHotbar=fromHotbar;SlotIndex=slotIndex;Amount=amount;X=x;Y=y;Z=z;Qx=qx;Qy=qy;Qz=qz;Qw=qw;Origin=origin;ContainerValue=containerValue;ContainerPersistent=containerPersistent;}
    public bool FromHotbar { get; }
    public int SlotIndex { get; }
    public int Amount { get; }
    public float X { get; } public float Y { get; } public float Z { get; }
    public float Qx { get; } public float Qy { get; } public float Qz { get; } public float Qw { get; }
    public DropOriginWire Origin { get; }
    /// <summary>SharedContainer 来源：来源容器实体 ID（主机权威扣减目标）。</summary>
    public ulong ContainerValue { get; }
    public bool ContainerPersistent { get; }
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

/// <summary>阶段三：WorldDroppedItem Pickup 直接进背包（原版语义）。entityId 走请求 TargetValue；payload 携带类型/数量供 Host 校验。</summary>
public readonly struct PickupPayload
{
    public PickupPayload(string itemType,int amount){ItemType=itemType??string.Empty;Amount=amount;}
    public string ItemType {get;} public int Amount {get;}
}

/// <summary>WorldDroppedItem → HeldItem（旧版 cursor-only 拾取结果；阶段三起不再使用，保留 codec 兼容）。</summary>
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
        : this(backpack, hotbar, 0, 0) { }
    public PlayerInventoryStatePayload(InventorySlotWire[] backpack, InventorySlotWire[] hotbar, int revision, int playerId)
    { Backpack=backpack??Array.Empty<InventorySlotWire>(); Hotbar=hotbar??Array.Empty<InventorySlotWire>(); Revision=revision; PlayerId=playerId; }
    public InventorySlotWire[] Backpack {get;}
    public InventorySlotWire[] Hotbar {get;}
    /// <summary>P0-Authority-Drift：权威背包版本号——Host 每次修改某玩家影子背包后递增；客户端拒绝旧 revision 包覆盖新状态。</summary>
    public int Revision {get;}
    public int PlayerId {get;}
}

/// <summary>v0.9.2：玩家背包 Commit。revision 由客户端 Client 单向递增（peer 自有 Owner），Host 拒绝旧 revision。</summary>
public readonly struct InventoryCommitMessage
{
    public InventoryCommitMessage(int playerId, int revision, InventorySlotWire[] backpack, InventorySlotWire[] hotbar)
    { PlayerId=playerId; Revision=revision; Backpack=backpack??Array.Empty<InventorySlotWire>(); Hotbar=hotbar??Array.Empty<InventorySlotWire>(); }
    public int PlayerId {get;} public int Revision {get;} public InventorySlotWire[] Backpack {get;} public InventorySlotWire[] Hotbar {get;}
}

/// <summary>v0.9.2：共享容器 Commit（原子事务：baseContainerRevision + 容器状态 + 玩家背包同一上链）。</summary>
public readonly struct ContainerCommitMessage
{
    public ContainerCommitMessage(Guid transactionId, ulong containerValue, bool containerPersistent, int baseContainerRevision, InventorySlotWire[] containerSlots, int playerId, int playerInventoryRevision, InventorySlotWire[] backpackAfter, InventorySlotWire[] hotbarAfter)
    { TransactionId=transactionId; ContainerValue=containerValue; ContainerPersistent=containerPersistent; BaseContainerRevision=baseContainerRevision; ContainerSlots=containerSlots??Array.Empty<InventorySlotWire>(); PlayerId=playerId; PlayerInventoryRevision=playerInventoryRevision; BackpackAfter=backpackAfter??Array.Empty<InventorySlotWire>(); HotbarAfter=hotbarAfter??Array.Empty<InventorySlotWire>(); }
    public Guid TransactionId {get;} public ulong ContainerValue {get;} public bool ContainerPersistent {get;} public int BaseContainerRevision {get;} public InventorySlotWire[] ContainerSlots {get;} public int PlayerId {get;} public int PlayerInventoryRevision {get;} public InventorySlotWire[] Backpack {get;} public InventorySlotWire[] Hotbar {get;} public InventorySlotWire[] BackpackAfter {get;} public InventorySlotWire[] HotbarAfter {get;}
}

/// <summary>v0.9.2 P0-8：DropCommit 原子事务——drop 结果 + 玩家背包同一上链。</summary>
public readonly struct DropCommitMessage
{
    public DropCommitMessage(Guid transactionId, ulong localDropToken, string itemType, int amount, float durability, int quality, bool recipe, float x, float y, float z, float qx, float qy, float qz, float qw, int playerId, int playerInventoryRevision, InventorySlotWire[] backpackAfter, InventorySlotWire[] hotbarAfter)
    { TransactionId=transactionId; LocalDropToken=localDropToken; ItemType=itemType??string.Empty; Amount=amount; Durability=durability; Quality=quality; Recipe=recipe; X=x; Y=y; Z=z; Qx=qx; Qy=qy; Qz=qz; Qw=qw; PlayerId=playerId; PlayerInventoryRevision=playerInventoryRevision; BackpackAfter=backpackAfter??Array.Empty<InventorySlotWire>(); HotbarAfter=hotbarAfter??Array.Empty<InventorySlotWire>(); }
    public Guid TransactionId {get;} public ulong LocalDropToken {get;} public string ItemType {get;} public int Amount {get;} public float Durability {get;} public int Quality {get;} public bool Recipe {get;} public float X {get;} public float Y {get;} public float Z {get;} public float Qx {get;} public float Qy {get;} public float Qz {get;} public float Qw {get;} public int PlayerId {get;} public int PlayerInventoryRevision {get;} public InventorySlotWire[] BackpackAfter {get;} public InventorySlotWire[] HotbarAfter {get;}
}

/// <summary>v0.9.2 P0-8：PickupCommit 原子事务——拾取结果 + 玩家背包同一上链。</summary>
public readonly struct PickupCommitMessage
{
    public PickupCommitMessage(Guid transactionId, ulong runtimeEntityId, bool persistent, string itemType, int amount, int playerId, int playerInventoryRevision, InventorySlotWire[] backpackAfter, InventorySlotWire[] hotbarAfter)
    { TransactionId=transactionId; RuntimeEntityId=runtimeEntityId; Persistent=persistent; ItemType=itemType??string.Empty; Amount=amount; PlayerId=playerId; PlayerInventoryRevision=playerInventoryRevision; BackpackAfter=backpackAfter??Array.Empty<InventorySlotWire>(); HotbarAfter=hotbarAfter??Array.Empty<InventorySlotWire>(); }
    public Guid TransactionId {get;} public ulong RuntimeEntityId {get;} public bool Persistent {get;} public string ItemType {get;} public int Amount {get;} public int PlayerId {get;} public int PlayerInventoryRevision {get;} public InventorySlotWire[] BackpackAfter {get;} public InventorySlotWire[] HotbarAfter {get;}
}

/// <summary>v0.9.2 P0-9：Host → 发起 Client 的 DropCommit Ack，告诉 Client 哪个 RuntimeEntityId 复用了它的本地对象（防双份 mirror）。</summary>
public readonly struct DropCommitAckMessage
{
    public DropCommitAckMessage(ulong localDropToken, ulong runtimeEntityId, bool persistent)
    { LocalDropToken=localDropToken; RuntimeEntityId=runtimeEntityId; Persistent=persistent; }
    public ulong LocalDropToken {get;} public ulong RuntimeEntityId {get;} public bool Persistent {get;}
}

/// <summary>v0.9.2 P0-4：Host → 客户端原子确认（ContainerCommitAck）。</summary>
public readonly struct ContainerCommitAckMessage
{
    public ContainerCommitAckMessage(Guid transactionId, ulong containerValue, bool containerPersistent, uint newRevision)
    { TransactionId=transactionId; ContainerValue=containerValue; ContainerPersistent=containerPersistent; NewRevision=newRevision; }
    public Guid TransactionId {get;} public ulong ContainerValue {get;} public bool ContainerPersistent {get;} public uint NewRevision {get;}
}

/// <summary>v0.9.2 P0-4：Host → 客户端冲突权威回滚（ContainerReconcile）。显式带 EntityId，不再依赖 Capture 时猜。</summary>
public readonly struct ContainerReconcileMessage
{
    public ContainerReconcileMessage(Guid transactionId, ulong containerValue, bool containerPersistent, uint canonicalRevision, InventorySlotWire[] canonicalSlots)
    { TransactionId=transactionId; ContainerValue=containerValue; ContainerPersistent=containerPersistent; CanonicalRevision=canonicalRevision; CanonicalSlots=canonicalSlots??Array.Empty<InventorySlotWire>(); }
    public Guid TransactionId {get;} public ulong ContainerValue {get;} public bool ContainerPersistent {get;} public uint CanonicalRevision {get;} public InventorySlotWire[] CanonicalSlots {get;}
}

/// <summary>v0.9.2 P0-6：原子事务中单个容器 mutation（与 InventoryTransactionCommit 共用 revision 校验）。</summary>
public readonly struct ContainerMutation
{
    public ContainerMutation(ulong containerValue, bool containerPersistent, uint baseRevision, InventorySlotWire[] slots)
    { ContainerValue=containerValue; ContainerPersistent=containerPersistent; BaseRevision=baseRevision; Slots=slots??Array.Empty<InventorySlotWire>(); }
    public ulong ContainerValue {get;} public bool ContainerPersistent {get;} public uint BaseRevision {get;} public InventorySlotWire[] Slots {get;}
}

/// <summary>v0.9.2 P0-6：原子提交 玩家背包 + N 个容器 mutation（全有/全无）。</summary>
public readonly struct InventoryTransactionCommitMessage
{
    public InventoryTransactionCommitMessage(Guid transactionId, int playerId, int playerInventoryRevision, InventorySlotWire[] backpack, InventorySlotWire[] hotbar, ContainerMutation[] containerMutations)
    { TransactionId=transactionId; PlayerId=playerId; PlayerInventoryRevision=playerInventoryRevision; Backpack=backpack??Array.Empty<InventorySlotWire>(); Hotbar=hotbar??Array.Empty<InventorySlotWire>(); ContainerMutations=containerMutations??Array.Empty<ContainerMutation>(); }
    public Guid TransactionId {get;} public int PlayerId {get;} public int PlayerInventoryRevision {get;} public InventorySlotWire[] Backpack {get;} public InventorySlotWire[] Hotbar {get;} public ContainerMutation[] ContainerMutations {get;}
}

/// <summary>v0.9.2 P0-6：Host → 客户端整事务回滚（包含 PlayerInventory 权威快照 + 所有相关 Container 权威快照）。</summary>
public readonly struct TransactionReconcileMessage
{
    public TransactionReconcileMessage(Guid transactionId, int playerId, int playerInventoryRevision, InventorySlotWire[] backpack, InventorySlotWire[] hotbar, ContainerReconcileMessage[] containers)
    { TransactionId=transactionId; PlayerId=playerId; PlayerInventoryRevision=playerInventoryRevision; Backpack=backpack??Array.Empty<InventorySlotWire>(); Hotbar=hotbar??Array.Empty<InventorySlotWire>(); Containers=containers??Array.Empty<ContainerReconcileMessage>(); }
    public Guid TransactionId {get;} public int PlayerId {get;} public int PlayerInventoryRevision {get;} public InventorySlotWire[] Backpack {get;} public InventorySlotWire[] Hotbar {get;} public ContainerReconcileMessage[] Containers {get;}
}

/// <summary>v0.9.2 TestHarness：Host/Client 之间协调测试阶段的轻量协议（TestMode only）。
/// 不携带游戏状态；只协调"开始哪个 phase / 校验哪个断言 / 提前结束"。
/// 不影响正式协议兼容（普通客户端不会注册该消息类型 handler）。</summary>
public readonly struct TestControlMessage
{
    public TestControlMessage(string kind, string scenario, int phase, string payloadJson)
    { Kind=kind??string.Empty; Scenario=scenario??string.Empty; Phase=phase; PayloadJson=payloadJson??string.Empty; }
    public string Kind {get;}        // "Ready" | "Start" | "PhaseComplete" | "Check" | "Complete" | "Abort"
    public string Scenario {get;}    // scenario 名
    public int Phase {get;}          // phase 序号
    public string PayloadJson {get;} // 自由格式断言/上下文（JSON 字符串）
}

/// <summary>Host-authoritative guest bootstrap: the spawn position and inventory a joining client applies right before Ready.</summary>

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

/// <summary>运行时实体类别。扩展时递增框架版本（无向下兼容）。</summary>

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
