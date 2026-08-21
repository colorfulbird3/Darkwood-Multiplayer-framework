using System;
using System.Collections.Generic;
using System.Linq;
using DarkwoodMultiplayerFramework.Core;
using DarkwoodMultiplayerFramework.Entities;
using DarkwoodMultiplayerFramework.Protocol;
using UnityEngine;

namespace DarkwoodMultiplayerFramework.DarkwoodAdapter;

/// <summary>
/// 所有权拆分：运行时实体服务——拥有全部"游戏运行过程中临时生成的对象"
/// （随机事件容器、掉落物、动态敌人）。外部只能调用公开方法，不直接接触内部字典。
/// 主机：扫描登记 + 范围门控单播 + 消失 Despawn；客户端：镜像实例化/销毁。
/// </summary>
public sealed class DarkwoodRuntimeEntityService
{
    private readonly DarkwoodAdapterRuntime runtime;
    private readonly RuntimeEntityRegistry registry = new RuntimeEntityRegistry();
    private readonly Dictionary<Inventory, ulong> hostInventories = new Dictionary<Inventory, ulong>();
    private readonly Dictionary<ulong, Transform> clientInventoryMirrors = new Dictionary<ulong, Transform>();
    private readonly Dictionary<Character, ulong> hostEnemies = new Dictionary<Character, ulong>();
    private readonly Dictionary<ulong, Character> clientEnemyMirrors = new Dictionary<ulong, Character>();
    private readonly Dictionary<ulong, RuntimeEntitySpawnMessage> pendingEvents = new Dictionary<ulong, RuntimeEntitySpawnMessage>();
    private readonly RuntimeEventDispatch dispatch = new RuntimeEventDispatch();
    private float nextScan;
    private float lastPoseWarnAt;
    /// <summary>随机事件动画触发范围（XZ 平面距离，米）。</summary>
    private const float TriggerRange = 35f;

    public DarkwoodRuntimeEntityService(DarkwoodAdapterRuntime runtime) => this.runtime = runtime;

    public RuntimeEntityRegistry Registry => registry;
    public int PendingEventCount => pendingEvents.Count;

    // ---------- 主机侧 ----------

    /// <summary>构建生成消息（分配 ID 并登记）。非主机返回 default。</summary>
    public RuntimeEntitySpawnMessage BuildSpawn(RuntimeEntityKind kind, string prototypeId, Vector3 position, Quaternion rotation, byte[]? initialState = null)
    {
        if (!runtime.Session.IsHost) return default;
        var id = registry.Allocate();
        var message = new RuntimeEntitySpawnMessage(id, kind, prototypeId, runtime.CurrentScene, position.x, position.y, position.z, rotation.x, rotation.y, rotation.z, rotation.w, initialState ?? Array.Empty<byte>(), runtime.serverTick);
        registry.Register(new RuntimeEntityRecord(id, kind, prototypeId, runtime.CurrentScene, runtime.serverTick));
        return message;
    }

    /// <summary>广播生成。返回分配的 ID；非主机返回 0。</summary>
    public ulong BroadcastSpawn(RuntimeEntityKind kind, string prototypeId, Vector3 position, Quaternion rotation, byte[]? initialState = null)
    {
        var message = BuildSpawn(kind, prototypeId, position, rotation, initialState);
        if (message.RuntimeEntityId == 0) return 0;
        // P0-6：BroadcastSpawn 必须登记 recipient bookkeeping（dispatch.TryMark），
        // 否则 BroadcastDespawn 只发 WasSent==true 的 peer 时，收到过 Spawn 的客户端永远收不到 Despawn（ghost）。
        foreach (var readyPeer in runtime.readyPeers.ToArray())
        {
            dispatch.TryMark(message.RuntimeEntityId, readyPeer);
            SendSpawnTo(readyPeer, message);
            runtime.log?.LogInfo($"[RUNTIME] spawn id={message.RuntimeEntityId} peer={readyPeer} kind={kind} proto={prototypeId}");
        }
        runtime.log?.LogInfo($"[RUNTIME] spawn 广播完成：ID {message.RuntimeEntityId}，类型 {kind}，tick {runtime.serverTick}。");
        return message.RuntimeEntityId;
    }

    /// <summary>广播移除。未登记的 ID 直接返回 false（不广播）。只发给触发过该事件的客户端（beta.5）。</summary>
    public bool BroadcastDespawn(ulong runtimeEntityId, RuntimeEntityDespawnReason reason)
    {
        if (!runtime.Session.IsHost || !registry.Remove(runtimeEntityId)) return false;
        var payload = ReplicationProtocolCodec.Encode(new RuntimeEntityDespawnMessage(runtimeEntityId, runtime.serverTick, reason));
        foreach (var readyPeer in runtime.readyPeers.ToArray())
        {
            var wasSent = dispatch.WasSent(runtimeEntityId, readyPeer);
            if (wasSent) runtime.Queue(readyPeer, ProtocolMessageType.RuntimeEntityDespawn, payload);
            runtime.log?.LogInfo($"[RUNTIME] despawn id={runtimeEntityId} peer={readyPeer} 原因={reason} dispatchWasSent={wasSent}");
        }
        return true;
    }

    private void SendSpawnTo(int peer, RuntimeEntitySpawnMessage message)
        => runtime.Queue(peer, ProtocolMessageType.RuntimeEntitySpawn, ReplicationProtocolCodec.Encode(message));

    /// <summary>P0-E/F：原子注册并广播一个由 Host 主动创建的掉落物——分配 ID → registry → replication binding → 生命周期监视(hostInventories) → recipient bookkeeping → spawn 发送。
    /// 绝不允许出现"客户端已收到 Spawn 而 Host binding/registry 尚未登记"（否则客户端永久保留 mirror、Host 却 ENTITY_NOT_FOUND）。</summary>
    public ulong RegisterAndBroadcastDroppedItem(Inventory dropped, Vector3 position, Quaternion rotation, byte[]? initialState = null)
    {
        if (!runtime.Session.IsHost || dropped == null) return 0;
        var message = BuildSpawn(RuntimeEntityKind.DroppedItem, "Items/DroppedItem", position, rotation, initialState);
        if (message.RuntimeEntityId == 0) return 0;
        var id = message.RuntimeEntityId;
        // 1) registry      2) replication binding      3) 生命周期监视
        registry.Register(new RuntimeEntityRecord(id, RuntimeEntityKind.DroppedItem, "Items/DroppedItem", runtime.CurrentScene, runtime.serverTick));
        runtime.replication.RegisterBinding(new WorldEntityBinding { Id = new EntityId(id, false), Root = dropped.gameObject, Primary = dropped, Inventory = dropped, Item = dropped.GetComponentInChildren<Item>(), Kind = WorldEntityKind.DroppedItem });
        hostInventories[dropped] = id; // ★ 从此进入 TickHost 生命周期监视（stale → authoritative despawn）
        // 4) recipient bookkeeping  5) spawn 发送（登记完成后才可发）
        foreach (var readyPeer in runtime.readyPeers.ToArray())
        {
            dispatch.TryMark(id, readyPeer);
            SendSpawnTo(readyPeer, message);
            runtime.log?.LogInfo($"[RUNTIME] spawn id={id} peer={readyPeer} kind=DroppedItem proto=Items/DroppedItem");
        }
        runtime.log?.LogInfo($"[RUNTIME] RegisterAndBroadcastDroppedItem 完成：ID {id}，registry+binding+生命周期监视+广播 一步到位。");
        return id;
    }

    /// <summary>RUNTIME-CHECK 诊断：Host 返回 ENTITY_NOT_FOUND（对 runtime 实体）之前调用，定位是"binding 丢失"还是"对象真死"。</summary>
    public void DumpRuntimeCheck(ulong runtimeId)
    {
        if (!runtime.Session.IsHost) return;
        var id = new EntityId(runtimeId, false);
        var bindingAlive = runtime.replication.TryGetBinding(id, out var binding);
        string rootAlive = "?" , invAlive = "?", itemAlive = "?";
        if (bindingAlive)
        {
            try { rootAlive = (binding.Root != null && (UnityEngine.Object)binding.Root != null) ? "True" : "False"; } catch (Exception) { rootAlive = "False"; }
            try { invAlive = (binding.Inventory != null && (UnityEngine.Object)binding.Inventory != null) ? "True" : "False"; } catch (Exception) { invAlive = "False"; }
            try { itemAlive = (binding.Item != null && (UnityEngine.Object)binding.Item != null) ? "True" : "False"; } catch (Exception) { itemAlive = "False"; }
        }
        bool tracked = false;
        foreach (var kv in hostInventories) if (kv.Value == runtimeId) { tracked = true; break; }
        runtime.log?.LogInfo($"[RUNTIME-CHECK] id={runtimeId} bindingAlive={(bindingAlive ? "True" : "False")} rootAlive={rootAlive} inventoryAlive={invAlive} itemAlive={itemAlive} trackedByLifecycle={tracked}");
    }

    /// <summary>主机周期：扫描新实体 + 销毁检测 + 范围门控单播（每 5 秒）。</summary>
    public void TickHost()
    {
        if (!runtime.Session.IsHost || runtime.readyPeers.Count == 0 || Time.unscaledTime < nextScan) return;
        nextScan = Time.unscaledTime + 10f; // 审计器：10 秒一轮（原 5 秒，扫描约 100ms 会卡主线程）
        nextScan = Time.unscaledTime + 5f; // beta.4：2s→5s 降低全场景扫描开销
        var scanStopwatch = System.Diagnostics.Stopwatch.StartNew();
        var seen = new HashSet<Inventory>();
        var seenEnemies = new HashSet<Character>();
        var seenDropped = new HashSet<Inventory>();
        foreach (var component in runtime.scanner.ScanScene())
        {
            if (component is Inventory inventory && inventory.invType == Inventory.InvType.deathDrop)
            {
                if (runtime.replication.TryGetId(inventory, out _)) continue; // 持久注册表内的存档对象
                seen.Add(inventory);
                if (hostInventories.ContainsKey(inventory)) continue;
                byte[] initialState;
                try { initialState = ReplicationProtocolCodec.Encode(runtime.replication.CaptureInventoryState(inventory, 0)); }
                catch (Exception error) { runtime.log?.LogWarning($"捕获运行时容器初始状态失败（{inventory.name}）：{error.Message}"); initialState = Array.Empty<byte>(); }
                var message = BuildSpawn(RuntimeEntityKind.LootContainer, inventory.name, inventory.transform.position, inventory.transform.rotation, initialState);
                if (message.RuntimeEntityId == 0) continue;
                hostInventories[inventory] = message.RuntimeEntityId;
                runtime.replication.RegisterBinding(new WorldEntityBinding{Id=new EntityId(message.RuntimeEntityId,false),Root=inventory.gameObject,Primary=inventory,Inventory=inventory,Item=inventory.GetComponentInChildren<Item>(),Kind=WorldEntityKind.LootContainer});
                pendingEvents[message.RuntimeEntityId] = message;
                runtime.log?.LogInfo($"主机登记随机事件容器（待客户端进入范围触发）：ID {message.RuntimeEntityId}，prefab {inventory.name}，位置 ({message.X:F0},{message.Y:F0},{message.Z:F0})。");
            }
            else if (component is Inventory dropped && dropped.invType == Inventory.InvType.itemInv
                && !runtime.replication.TryGetId(dropped, out _))
            {
                seenDropped.Add(dropped);
                if (hostInventories.ContainsKey(dropped)) continue;
                byte[] droppedInitialState;
                try { droppedInitialState = ReplicationProtocolCodec.Encode(runtime.replication.CaptureInventoryState(dropped, 0)); }
                catch (Exception error) { runtime.log?.LogWarning($"捕获掉落物初始状态失败（{dropped.name}）：{error.Message}"); droppedInitialState = Array.Empty<byte>(); }
                var droppedMessage = BuildSpawn(RuntimeEntityKind.DroppedItem, dropped.name, dropped.transform.position, dropped.transform.rotation, droppedInitialState);
                if (droppedMessage.RuntimeEntityId == 0) continue;
                hostInventories[dropped] = droppedMessage.RuntimeEntityId;
                runtime.replication.RegisterBinding(new WorldEntityBinding{Id=new EntityId(droppedMessage.RuntimeEntityId,false),Root=dropped.gameObject,Primary=dropped,Inventory=dropped,Item=dropped.GetComponentInChildren<Item>(),Kind=WorldEntityKind.DroppedItem});
                pendingEvents[droppedMessage.RuntimeEntityId] = droppedMessage;
                runtime.log?.LogInfo($"主机登记掉落物（待客户端进入范围触发）：ID {droppedMessage.RuntimeEntityId}，位置 ({droppedMessage.X:F0},{droppedMessage.Y:F0},{droppedMessage.Z:F0})。");
            }
            else if (component is Character character && !(character is Player))
            {
                if (runtime.replication.TryGetId(character, out _)) continue; // 存档内怪物
                seenEnemies.Add(character); // 尸体也算"仍在场"（防误 Despawn）
                if (!character.alive) continue;
                if (hostEnemies.ContainsKey(character)) continue;
                var prefabName = character.name.Replace("(Clone)", "");
                var enemyMessage = BuildSpawn(RuntimeEntityKind.Enemy, prefabName, character.transform.position, character.transform.rotation);
                if (enemyMessage.RuntimeEntityId == 0) continue;
                hostEnemies[character] = enemyMessage.RuntimeEntityId;
                runtime.replication.RegisterBinding(new WorldEntityBinding{Id=new EntityId(enemyMessage.RuntimeEntityId,false),Root=character.gameObject,Primary=character,Character=character,Item=character.GetComponentInChildren<Item>(),Kind=WorldEntityKind.Enemy});
                pendingEvents[enemyMessage.RuntimeEntityId] = enemyMessage;
                runtime.log?.LogInfo($"主机登记运行时敌人（待客户端进入范围触发）：ID {enemyMessage.RuntimeEntityId}，prefab {prefabName}，位置 ({enemyMessage.X:F0},{enemyMessage.Y:F0},{enemyMessage.Z:F0})。");
            }
        }
        scanStopwatch.Stop();
        if (scanStopwatch.ElapsedMilliseconds > 150) runtime.log?.LogWarning($"运行时实体扫描耗时 {scanStopwatch.ElapsedMilliseconds} ms（每 10 秒一次，若持续偏高会导致主机卡顿）。");
        // beta.5：检测被游戏销毁的持久实体（夹子拆除等）→ Despawn 广播
        // P0-2：两阶段快照——绝不在遍历 replication.Entities()（惰性迭代器）时 ForceDespawn（会删字典 → Collection modified）。
        var despawnWires = new List<EntityStateWire>();
        foreach (var id in runtime.replication.EntitySnapshot().Where(pair => pair.Key.IsPersistent && pair.Value == null).Select(pair => pair.Key).ToArray())
            despawnWires.Add(runtime.replication.ForceDespawn(id));
        if (despawnWires.Count > 0)
        {
            var deltaPayload = ReplicationProtocolCodec.Encode(new EntityDeltaMessage(runtime.CurrentScene, runtime.serverTick, Array.Empty<EntityStateWire>(), despawnWires.ToArray()));
            foreach (var readyPeer in runtime.readyPeers.ToArray()) runtime.Queue(readyPeer, ProtocolMessageType.EntityDelta, deltaPayload);
            runtime.log?.LogInfo($"主机检测到 {despawnWires.Count} 个持久实体被销毁，已广播移除（夹子/物品等世界状态同步）。");
        }
        // P0-F：显式注册（原子 API / 扫描发现）的掉落物生命周期监视——只按"Unity 对象真消失"清理并广播 Despawn，
        // 绝不能因"本轮扫描没再遇到它"就误判销毁（主动 Drop 的实体已进 hostInventories，扫描 TryGetId 命中会 continue，天然不进 seenDropped）。
        foreach (var pair in hostInventories.ToArray())
        {
            var inv = pair.Key;
            bool dead = false;
            if (inv == null) dead = true;
            else try { dead = inv.gameObject == null || !inv.gameObject.activeInHierarchy; } catch (Exception) { dead = true; }
            if (dead)
            {
                hostInventories.Remove(pair.Key);
                pendingEvents.Remove(pair.Value);
                // ★ 先广播 Despawn（依赖 dispatch.WasSent 判断收件人），再清 dispatch，否则 Despawn 发不出去。
                BroadcastDespawn(pair.Value, RuntimeEntityDespawnReason.Collected);
                dispatch.ClearEvent(pair.Value);
                runtime.replication.UnregisterRuntimeEntity(new EntityId(pair.Value, false));
                runtime.log?.LogInfo($"[RUNTIME-CHECK] id={pair.Value} 生命周期：Host Unity 掉落物真消失 → 已广播 Despawn + unregister（Client 必须随之清理 mirror）。");
            }
        }
        foreach (var pair in hostEnemies.ToArray())
        {
            if (pair.Key == null || !seenEnemies.Contains(pair.Key))
            {
                hostEnemies.Remove(pair.Key);
                pendingEvents.Remove(pair.Value);
                dispatch.ClearEvent(pair.Value);
                runtime.replication.UnregisterRuntimeEntity(new EntityId(pair.Value, false));
                BroadcastDespawn(pair.Value, RuntimeEntityDespawnReason.Died);
            }
        }
        foreach (var pair in pendingEvents.ToArray())
        {
            var message = pair.Value;
            foreach (var readyPeer in runtime.readyPeers.ToArray())
            {
                if (!runtime.Players.TryGetRemotePosition(readyPeer, out var pose))
                {
                    // 诊断（限频 10 秒）：客户端位置缺失 = Spawn 广播静默跳过
                    if (Time.unscaledTime - lastPoseWarnAt > 10f)
                    {
                        lastPoseWarnAt = Time.unscaledTime;
                        runtime.log?.LogWarning($"玩家 {readyPeer} 位置未知（主机未收到其姿态），跳过运行时实体 Spawn 广播：ID {message.RuntimeEntityId}，类型 {message.Kind}。");
                    }
                    continue;
                }
                var dx = pose.x - message.X; var dz = pose.z - message.Z;
                if (dx * dx + dz * dz > TriggerRange * TriggerRange) continue;
                if (!dispatch.TryMark(message.RuntimeEntityId, readyPeer)) continue;
                SendSpawnTo(readyPeer, message);
                runtime.log?.LogInfo($"已向客户端 {readyPeer} 发送运行时实体：ID {message.RuntimeEntityId}，类型 {message.Kind}，prefab {message.PrototypeId}，距离 {(float)Math.Sqrt(dx*dx+dz*dz):F1} 米。");
            }
        }
    }

    public int PendingCount => pendingEvents.Count;

    /// <summary>场景切换：清空全部运行时实体状态（ID 计数器继续单调递增）。</summary>
    public void OnSceneChanged()
    {
        pendingEvents.Clear();
        hostInventories.Clear();
        hostEnemies.Clear();
        dispatch.Clear();
        registry.ClearAlive();
    }

    /// <summary>连接停止：完整复位。</summary>
    public void Reset() => OnSceneChanged();

    // ---------- 客户端侧 ----------

    /// <summary>客户端处理 Spawn：登记 + 按类型实例化镜像。</summary>
    public void HandleSpawn(RuntimeEntitySpawnMessage spawn)
    {
        registry.Register(new RuntimeEntityRecord(spawn.RuntimeEntityId, spawn.Kind, spawn.PrototypeId, spawn.Scene, spawn.ServerTick, RuntimeEntityLifecycleState.Spawned));
        runtime.log?.LogInfo($"客户端已登记运行时实体：ID {spawn.RuntimeEntityId}，类型 {spawn.Kind}，原型 {spawn.PrototypeId}。");
        if (spawn.Kind == RuntimeEntityKind.LootContainer) SpawnLootContainerMirror(spawn);
        else if (spawn.Kind == RuntimeEntityKind.DroppedItem) SpawnDroppedItemMirror(spawn);
        else if (spawn.Kind == RuntimeEntityKind.Enemy) SpawnEnemyMirror(spawn);
    }

    /// <summary>客户端处理 Despawn：移除登记 + 销毁镜像 + 从 replication 卸载（P0-A：dropped item 不能留 ghost EntityId）。未登记的 ID 静默忽略（beta.5）。</summary>
    public void HandleDespawn(RuntimeEntityDespawnMessage despawn)
    {
        if (!registry.Remove(despawn.RuntimeEntityId)) return;
        if (clientInventoryMirrors.TryGetValue(despawn.RuntimeEntityId, out var mirror))
        {
            clientInventoryMirrors.Remove(despawn.RuntimeEntityId);
            runtime.replication.UnregisterRuntimeEntity(new EntityId(despawn.RuntimeEntityId, false));
            if (mirror != null) UnityEngine.Object.Destroy(mirror.gameObject);
        }
        if (clientEnemyMirrors.TryGetValue(despawn.RuntimeEntityId, out var enemy))
        {
            clientEnemyMirrors.Remove(despawn.RuntimeEntityId);
            runtime.replication.UnregisterRuntimeEntity(new EntityId(despawn.RuntimeEntityId, false));
            if (enemy != null) UnityEngine.Object.Destroy(enemy.gameObject);
        }
        runtime.log?.LogInfo($"[RUNTIME] despawn recv id={despawn.RuntimeEntityId} 原因={despawn.Reason} mirror 已销毁，replication 已卸载。");
        // P0-D：World pickup 镜像销毁后验证 cursor 完全独立于 runtime mirror（pickedSlotInventoryAlive 必须 True，UI 存活）。
        // 绝不出现 slotPresent=True / slotInventoryAlive=False 的 dangling。
        try
        {
            var c = Singleton<Controller>.Instance;
            if (c != null && !InvItemClass.isNull(c.pickedUpItem))
            {
                var it = c.pickedUpItem;
                bool slotAlive = false; string slotInv = "无";
                try { if (it.slot != null && it.slot.inventory != null) { slotAlive = it.slot.inventory.gameObject != null && it.slot.inventory.gameObject.activeInHierarchy; slotInv = it.slot.inventory.invType.ToString(); } } catch (Exception) { slotAlive = false; }
                bool uiAlive = false; string spr = "无";
                try { if (it.UIInvItem != null) { uiAlive = it.UIInvItem.gameObject != null && it.UIInvItem.gameObject.activeInHierarchy; if (it.UIInvItem.sprite != null) spr = it.UIInvItem.sprite.spriteId.ToString(); } } catch (Exception) { }
                runtime.log?.LogInfo($"[CURSOR-WORLD-AFTER-DESPAWN] type={it.type} amount={it.amount} pickedUpItem=有 slotInventoryAlive={slotAlive} slotInventoryType={slotInv} uiAlive={uiAlive} spriteId={spr}");
            }
        }
        catch (Exception) { }
    }

    /// <summary>P0-H：诊断用——该组件是否属于已知的 runtime dropped 镜像（Host 分配过 ID 的合法掉落物）。</summary>
    public bool IsKnownDroppedMirror(Component c)
    {
        if (c == null) return false;
        foreach (var kv in clientInventoryMirrors)
            if (kv.Value != null && ReferenceEquals(kv.Value, c)) return true;
        return false;
    }

    private void SpawnLootContainerMirror(RuntimeEntitySpawnMessage spawn)
    {
        try
        {
            var go = global::Core.AddPrefab(spawn.PrototypeId, new Vector3(spawn.X, spawn.Y, spawn.Z), new Quaternion(spawn.Qx, spawn.Qy, spawn.Qz, spawn.Qw), global::Core.ItemContainer);
            if (go == null) { runtime.log?.LogWarning($"客户端无法实例化运行时容器：prefab {spawn.PrototypeId} 不存在或不可用。"); return; }
            var inventory = go.GetComponent<Inventory>();
            if (inventory != null && spawn.InitialState.Length > 0)
            {
                var state = ReplicationProtocolCodec.DecodeInventoryState(spawn.InitialState);
                var slots = new DarkwoodInventorySlot[state.Slots.Length];
                for (var i = 0; i < slots.Length; i++) { var s = state.Slots[i]; slots[i] = new DarkwoodInventorySlot { Type = s.Type, Amount = s.Amount, Durability = s.Durability, Quality = s.Quality, Recipe = s.Recipe }; }
                DarkwoodInventoryAdapter.Apply(inventory, slots);
            }
            foreach (var col in go.GetComponentsInChildren<Collider>(true)) col.enabled = false;
            runtime.replication.RegisterBinding(new WorldEntityBinding{Id=new EntityId(spawn.RuntimeEntityId,false),Root=go,Primary=inventory,Inventory=inventory,Item=go.GetComponentInChildren<Item>(),Kind=spawn.Kind==RuntimeEntityKind.Enemy?WorldEntityKind.Enemy:(spawn.Kind==RuntimeEntityKind.DroppedItem?WorldEntityKind.DroppedItem:WorldEntityKind.LootContainer)});
            clientInventoryMirrors[spawn.RuntimeEntityId] = go.transform;
            runtime.log?.LogInfo($"客户端已实例化运行时容器镜像：ID {spawn.RuntimeEntityId}，prefab {spawn.PrototypeId}，槽位 {(inventory != null ? inventory.slots.Count : 0)}。");
        }
        catch (Exception error) { runtime.log?.LogWarning($"实例化运行时容器失败（{spawn.PrototypeId}）：{error.Message}"); }
    }

    private void SpawnDroppedItemMirror(RuntimeEntitySpawnMessage spawn)
    {
        try
        {
            // v0.9.0 Trusted Client：drop 发起者本地已用原版 spawnDroppedInvItem 生成了掉落物——
            // 若与本次 Host spawn 匹配（类型/位置），直接复用为 mirror（不重复 Instantiate，杜绝双份/ghost）。
            var pending = runtime.TakePendingLocalDrop(spawn);
            Inventory dropped;
            GameObject go;
            if (pending != null)
            {
                dropped = pending;
                go = dropped.gameObject;
                runtime.log?.LogInfo($"[TRUST] 复用本地原版掉落物为 mirror：ID {spawn.RuntimeEntityId}（Trusted Client 本地创建）");
            }
            else
            {
                go = global::Core.AddPrefab("Items/DroppedItem", new Vector3(spawn.X, spawn.Y, spawn.Z), new Quaternion(spawn.Qx, spawn.Qy, spawn.Qz, spawn.Qw), global::Core.ItemContainer);
                if (go == null) { runtime.log?.LogWarning($"客户端无法实例化掉落物镜像：prefab {spawn.PrototypeId} 不存在或不可用。"); return; }
                dropped = go.GetComponent<Inventory>();
                if (dropped == null || dropped.slots == null || dropped.slots.Count == 0) { UnityEngine.Object.Destroy(go); runtime.log?.LogWarning($"掉落物镜像无容器：{spawn.PrototypeId}。"); return; }
                if (spawn.InitialState.Length > 0)
                {
                    var state = ReplicationProtocolCodec.DecodeInventoryState(spawn.InitialState);
                    if (state.Slots.Length > 0)
                    {
                        var s = state.Slots[0];
                        var slot = dropped.slots[0];
                        slot.inventory = dropped;
                        // P0-1：镜像物品必须经原版按 type 创建链（createItem(string,...) → new InvItemClass(type,...) + initialize），
                        // 绝不复制 DroppedItem prefab 默认 InvItem/UI/baseClass/sprite；InitialState 仅作为权威数据。
                        if (!InvItemClass.isNull(slot.invItem)) slot.invItem.clear();
                        slot.createItem(s.Type, s.Amount, s.Durability, (InvItem.ModifierQuality)s.Quality, s.Recipe);
                        try { dropped.refreshItems(); } catch (Exception) { }
                        runtime.log?.LogInfo($"[DROP-MIRROR] runtimeId={spawn.RuntimeEntityId} authoritativeType={s.Type} slotType={(slot.invItem != null ? slot.invItem.type : "?")} baseClass={(slot.invItem != null && slot.invItem.baseClass != null ? slot.invItem.baseClass.type : "?")} amount={(slot.invItem != null ? slot.invItem.amount : 0)} spriteName=无(世界对象无UI槽，光标UI由Host权威Held重建)");
                    }
                }
            }
            // 保留碰撞器：镜像可被点击拾取（Pickup Patch 拦截并转发 Host）
            var item = go.GetComponentInChildren<Item>();
            if (item != null) item.isDroppedItem = true;
            runtime.replication.RegisterBinding(new WorldEntityBinding{Id=new EntityId(spawn.RuntimeEntityId,false),Root=go,Primary=dropped,Inventory=dropped,Item=item,Kind=WorldEntityKind.DroppedItem});
            clientInventoryMirrors[spawn.RuntimeEntityId] = go.transform;
            runtime.log?.LogInfo($"客户端已实例化掉落物镜像：ID {spawn.RuntimeEntityId}，类型 {spawn.PrototypeId}，可交互。");
        }
        catch (Exception error) { runtime.log?.LogWarning($"实例化掉落物镜像失败（{spawn.PrototypeId}）：{error.Message}"); }
    }

    private void SpawnEnemyMirror(RuntimeEntitySpawnMessage spawn)
    {
        try
        {
            var go = global::Core.AddPrefab(spawn.PrototypeId, new Vector3(spawn.X, spawn.Y, spawn.Z), new Quaternion(spawn.Qx, spawn.Qy, spawn.Qz, spawn.Qw), global::Core.ItemContainer);
            if (go == null) { runtime.log?.LogWarning($"客户端无法实例化运行时敌人：prefab {spawn.PrototypeId} 不存在或不可用。"); return; }
            var character = go.GetComponent<Character>();
            if (character == null) { UnityEngine.Object.Destroy(go); runtime.log?.LogWarning($"运行时敌人实例无 Character 组件：{spawn.PrototypeId}。"); return; }
            character.enabled = false; // 远端代理：冻结 AI
            if (character.AIpath != null) character.AIpath.enabled = false;
            runtime.replication.RegisterRuntimeEntity(new EntityId(spawn.RuntimeEntityId, false), character);
            clientEnemyMirrors[spawn.RuntimeEntityId] = character;
            runtime.log?.LogInfo($"客户端已实例化运行时敌人代理：ID {spawn.RuntimeEntityId}，prefab {spawn.PrototypeId}，血量 {character.health:F0}/{character.maxHealth:F0}。");
        }
        catch (Exception error) { runtime.log?.LogWarning($"实例化运行时敌人失败（{spawn.PrototypeId}）：{error.Message}"); }
    }
}
