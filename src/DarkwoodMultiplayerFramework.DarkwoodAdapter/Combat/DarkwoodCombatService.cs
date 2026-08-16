using System;
using System.Collections.Generic;
using System.Linq;
using DarkwoodMultiplayerFramework.Core;
using DarkwoodMultiplayerFramework.Protocol;
using HarmonyLib;
using UnityEngine;

namespace DarkwoodMultiplayerFramework.DarkwoodAdapter;

/// <summary>
/// 0.8.9 所有权拆分：战斗服务——拥有血量 / 倒地 / 怪物伤害 / 攻击锚点 / 无敌时间 / 营救会话。
/// 所有 Combat/Rescue 状态从 Runtime 迁入；外部只能通过公开方法交互。
/// </summary>
public sealed class DarkwoodCombatService
{
    private readonly IMultiplayerRuntimeHost runtime;

    // ── 血量 / 倒地 ──
    private readonly Dictionary<int, float> peerHealths = new Dictionary<int, float>();
    private readonly Dictionary<int, float> peerMaxHealths = new Dictionary<int, float>();
    private readonly Dictionary<int, bool> peerDowned = new Dictionary<int, bool>();
    private readonly Dictionary<int, float> nextGuestHitAllowed = new Dictionary<int, float>();
    private bool hostDownedLocal;
    private bool allDownedHandled;
    private float nextMonsterDamageScan;
    private float nextHealthHeartbeat;
    private float lastBroadcastHostHealth = float.MaxValue;

    // ── 攻击 ──
    private readonly Dictionary<int, float> nextAttackAllowed = new Dictionary<int, float>();
    private readonly Dictionary<int, GameObject> remoteAttackAnchors = new Dictionary<int, GameObject>();
    private float localInvulUntil;

    // ── 营救 ──
    private RescueSession? activeRescue;
    private bool rescueLockedByMe;
    private float nextRescueBroadcast;
    private RescueProgressMessage lastRescueProgress;

    // ── 常量 ──
    private const float RescueDurationSeconds = 3f;
    private const float RescueRange = 4f;
    private const float ReviveHealthFraction = 0.1f;
    private const float MonsterDamageScanInterval = 0.25f;
    private const float MonsterHitCooldown = 0.5f;
    private const float MonsterReach = 1.6f;
    private const float ReviveInvulnerableSeconds = 3f;
    private const float PostDownedEndingDelay = 2f;
    private const float MeleeReach = 1.6f;
    private const float MeleeConeDot = 0.3f;

    private sealed class RescueSession
    {
        public int TargetId;
        public int RescuerId;
        public float StartedAt;
    }

    internal DarkwoodCombatService(IMultiplayerRuntimeHost runtime) => this.runtime = runtime;

    public bool HostDownedLocal => hostDownedLocal;
    public bool IsRescuing(int playerId) => activeRescue != null && activeRescue.RescuerId == playerId;
    public RescueProgressMessage LastRescueProgress => lastRescueProgress;

    // 主机侧对外（peer 就绪初始化 / 存档点）
    public void RegisterPeer(int peer, float maxHealth)
    {
        peerHealths[peer] = maxHealth;
        peerMaxHealths[peer] = maxHealth;
        peerDowned[peer] = false;
    }

    public void SetPeerDowned(int peer) { peerDowned[peer] = true; }
    public void SetPeerMaxHealth(int peer, float maxHealth) => peerMaxHealths[peer] = maxHealth;
    public bool TryGetPeerHealth(int peer, out float health) => peerHealths.TryGetValue(peer, out health);
    public void SetPeerHealth(int peer, float health) => peerHealths[peer] = health;
    public float PeerMaxHealth(int peer) => peerMaxHealths.TryGetValue(peer, out var mh) && mh > 0f ? mh : 100f;

    /// <summary>攻击冷却检查+登记（信任模式限速）。返回 true 表示允许本次攻击。</summary>
    public bool TryConsumeAttack(int peer, float cooldownSeconds)
    {
        if (nextAttackAllowed.TryGetValue(peer, out var allowedAt) && Time.unscaledTime < allowedAt) return false;
        nextAttackAllowed[peer] = Time.unscaledTime + cooldownSeconds;
        return true;
    }

    /// <summary>玩家断线：清理其血量/倒地/攻击/营救状态，并复查全员倒地。</summary>
    public void OnPeerDisconnected(int peer)
    {
        if (activeRescue != null && (activeRescue.TargetId == peer || activeRescue.RescuerId == peer))
        {
            var rescueTarget = activeRescue.TargetId;
            var rescueRescuer = activeRescue.RescuerId;
            activeRescue = null;
            BroadcastRescueProgress(rescueTarget, rescueRescuer, 0f, false);
        }
        nextAttackAllowed.Remove(peer);
        peerHealths.Remove(peer);
        peerMaxHealths.Remove(peer);
        peerDowned.Remove(peer);
        nextGuestHitAllowed.Remove(peer);
        if (remoteAttackAnchors.TryGetValue(peer, out var anchor))
        {
            if (anchor != null) UnityEngine.Object.Destroy(anchor);
            remoteAttackAnchors.Remove(peer);
        }
        CheckAllDowned();
    }

    // ── 周期 ──
    public void TickHost()
    {
        ScanMonsterDamage();
        SyncHostHealth();
        TickRescue();
        ExpireLocalInvulnerability();
    }

    public void TickClient() => ExpireLocalInvulnerability();

    private void ExpireLocalInvulnerability()
    {
        if (localInvulUntil > 0f && Time.unscaledTime >= localInvulUntil)
        {
            localInvulUntil = 0f;
            var player = Player.Instance;
            if (player != null) player.invulnerable = false;
        }
    }

    // ── 血量心跳 / 怪物伤害 ──
    private void ScanMonsterDamage()
    {
        if (!runtime.Session.IsHost || runtime.ReadyPeers.Count == 0 || Time.unscaledTime < nextMonsterDamageScan) return;
        nextMonsterDamageScan = Time.unscaledTime + MonsterDamageScanInterval;
        foreach (var pair in runtime.Replication.AllEntities)
        {
            var monster = pair.Value as Character;
            if (monster == null || !monster.alive || !monster.gameObject.activeSelf || monster.aggressiveness == Aggressiveness.neutral) continue;
            var monsterPosition = monster.transform.position;
            foreach (var peer in runtime.ReadyPeers.ToArray())
            {
                if (peerDowned.TryGetValue(peer, out var downed) && downed) continue;
                if (!runtime.Players.TryGetRemotePosition(peer, out var guestPosition)) continue;
                if (SqrDistance(monsterPosition, guestPosition) > MonsterReach * MonsterReach) continue;
                if (Time.unscaledTime < (nextGuestHitAllowed.TryGetValue(peer, out var allowed) ? allowed : 0f)) continue;
                nextGuestHitAllowed[peer] = Time.unscaledTime + MonsterHitCooldown;
                var monsterDamage = monster.sensorTypes != null && monster.sensorTypes.Count > 0 ? Mathf.Max(1, monster.sensorTypes[0].damage) : 5;
                var health = Mathf.Max(0f, peerHealths.TryGetValue(peer, out var current) ? current : 100f) - monsterDamage;
                peerHealths[peer] = health;
                var maxHealth = PeerMaxHealth(peer);
                if (health <= 0f)
                {
                    peerDowned[peer] = true;
                    BroadcastHealth(peer, 0f, maxHealth, true);
                    runtime.LogWarning($"玩家 {peer} 被怪物击倒。");
                    CheckAllDowned();
                }
                else BroadcastHealth(peer, health, maxHealth, false);
            }
        }
    }

    private void SyncHostHealth()
    {
        if (!runtime.Session.IsHost || runtime.ReadyPeers.Count == 0) return;
        var player = Player.Instance;
        if (player == null) return;
        if (Mathf.Abs(lastBroadcastHostHealth - player.health) > 0.01f || Time.unscaledTime >= nextHealthHeartbeat)
        {
            lastBroadcastHostHealth = player.health;
            nextHealthHeartbeat = Time.unscaledTime + 1f;
            BroadcastHealth(0, player.health, player.maxHealth, hostDownedLocal);
        }
    }

    private void BroadcastHealth(int playerId, float health, float maxHealth, bool downed)
    {
        if (!runtime.Session.IsHost) return;
        var payload = ReplicationProtocolCodec.Encode(new PlayerHealthMessage(playerId, health, maxHealth, downed));
        foreach (var readyPeer in runtime.ReadyPeers.ToArray()) runtime.Queue(readyPeer, ProtocolMessageType.PlayerHealth, payload);
    }

    // ── 倒地 / 复活 ──
    public void OnLocalPlayerDowned()
    {
        if (DarkwoodDownedPatch.LocalDowned) return;
        DarkwoodDownedPatch.EnterLocalDowned();
        runtime.LogWarning("本地玩家倒地！等待队友营救……");
        if (runtime.Session.IsHost)
        {
            hostDownedLocal = true;
            var player = Player.Instance;
            BroadcastHealth(0, player != null ? player.health : 0f, player != null ? player.maxHealth : 100f, true);
            CheckAllDowned();
        }
    }

    public void ApplyIncomingPlayerHealth(PlayerHealthMessage health)
    {
        var myId = runtime.LocalPeerId;
        if (health.PlayerId != myId || runtime.Session.State != ConnectionState.Ready) return;
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
        if (runtime.Session.IsHost) hostDownedLocal = false;
        runtime.LogInfo($"已复活：生命 {health:F0}，体力回满，{ReviveInvulnerableSeconds:F0} 秒保护。");
    }

    private void CheckAllDowned()
    {
        if (!runtime.Session.IsHost || allDownedHandled) return;
        if (!hostDownedLocal) return;
        foreach (var peer in runtime.ReadyPeers.ToArray())
        {
            if (!peerDowned.TryGetValue(peer, out var downed) || !downed) return;
        }
        allDownedHandled = true;
        DarkwoodDownedPatch.AllDowned = true;
        runtime.LogWarning("全员倒地——触发原版结局并结束联机会话。");
        var payload = ReplicationProtocolCodec.Encode(new AllDownedMessage());
        foreach (var readyPeer in runtime.ReadyPeers.ToArray()) runtime.Queue(readyPeer, ProtocolMessageType.AllDowned, payload);
        RunLocalVanillaEnding();
        runtime.ScheduleStop(PostDownedEndingDelay);
    }

    public void HandleAllDowned()
    {
        if (DarkwoodDownedPatch.AllDowned) return;
        DarkwoodDownedPatch.AllDowned = true;
        runtime.LogWarning("全员倒地——触发原版结局并结束联机会话。");
        RunLocalVanillaEnding();
        runtime.ScheduleStop(PostDownedEndingDelay);
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
        catch (Exception error) { runtime.LogWarning("原版结局触发失败：" + error.Message); }
    }

    // ── 营救 ──
    public void PollRescueHotkey()
    {
        if (!runtime.Session.IsActive || DarkwoodDownedPatch.AllDowned) return;
        if (DarkwoodDownedPatch.LocalDowned) return;
        if (!Input.GetKeyDown(KeyCode.F4)) return;
        if (runtime.Session.IsHost) HandleRescueIntent(0, IsRescuing(0));
        else runtime.SendToHost(ProtocolMessageType.RescueRequest, ReplicationProtocolCodec.Encode(new RescueRequestMessage(runtime.LocalPeerId, IsRescuing(runtime.LocalPeerId))));
    }

    public void HandleRescueIntent(int rescuerId, bool cancel)
    {
        if (!runtime.Session.IsHost) return;
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
            if (!runtime.ReadyPeers.Contains(rescuerId)) return;
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
        foreach (var peer in runtime.ReadyPeers.ToArray())
        {
            if (peer == rescuerId) continue;
            if (!peerDowned.TryGetValue(peer, out var downed) || !downed) continue;
            if (!runtime.Players.TryGetRemotePosition(peer, out var position)) continue;
            var sq = SqrDistance(rescuerPosition, position);
            if (sq <= bestSq) { bestSq = sq; bestTarget = peer; }
        }
        if (bestTarget < 0)
        {
            var nearestSq = float.MaxValue;
            if (hostDownedLocal && rescuerId != 0) nearestSq = SqrDistance(rescuerPosition, GetPlayerPosition(0));
            runtime.LogInfo($"营救请求被拒绝：玩家 {rescuerId} 附近没有倒地的队友（最近倒地距离 {Mathf.Sqrt(nearestSq):F1} 米，有效范围 {RescueRange:F0} 米）。");
            runtime.Queue(rescuerId, ProtocolMessageType.RescueProgress, ReplicationProtocolCodec.Encode(new RescueProgressMessage(rescuerId, rescuerId, 0f, false)));
            return;
        }
        activeRescue = new RescueSession { TargetId = bestTarget, RescuerId = rescuerId, StartedAt = Time.unscaledTime };
        nextRescueBroadcast = 0f;
        if (rescuerId == 0 && !DarkwoodDownedPatch.LocalDowned) { rescueLockedByMe = true; global::Core.forbidInputs = true; }
        BroadcastRescueProgress(bestTarget, rescuerId, 0f, true);
        runtime.LogInfo($"营救开始：玩家 {rescuerId} → 倒地玩家 {bestTarget}（{RescueDurationSeconds:F0} 秒）。");
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
            if (!runtime.ReadyPeers.Contains(rescuer)) return true;
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
        runtime.LogInfo("营救取消（进度归零，双方解锁）。");
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
            var maxHealth = PeerMaxHealth(target);
            var health = maxHealth * ReviveHealthFraction;
            peerHealths[target] = health;
            peerDowned[target] = false;
            BroadcastHealth(target, health, maxHealth, false);
        }
        BroadcastRescueProgress(target, rescuer, 1f, false);
        runtime.LogInfo($"营救完成：玩家 {target} 复活（生命上限的 10%，体力回满）。");
    }

    public void HandleRescueProgress(RescueProgressMessage progress)
    {
        lastRescueProgress = progress;
        var myId = runtime.LocalPeerId;
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

    private void BroadcastRescueProgress(int targetId, int rescuerId, float progress, bool active)
    {
        var message = new RescueProgressMessage(targetId, rescuerId, progress, active);
        var payload = ReplicationProtocolCodec.Encode(message);
        foreach (var readyPeer in runtime.ReadyPeers.ToArray()) runtime.Queue(readyPeer, ProtocolMessageType.RescueProgress, payload);
        if (runtime.Session.IsHost && active) lastRescueProgress = message;
    }

    // ── 攻击锚点 / 近战 ──
    public GameObject GetAttackAnchor(int peer, Vector3 pose)
    {
        if (!remoteAttackAnchors.TryGetValue(peer, out var anchor) || anchor == null)
        {
            anchor = new GameObject("RemoteAttackAnchor_" + peer);
            UnityEngine.Object.DontDestroyOnLoad(anchor);
            remoteAttackAnchors[peer] = anchor;
        }
        anchor.transform.position = pose;
        return anchor;
    }

    /// <summary>Approximates the game's MeleeSensor arc: nearest registered live entity within reach and in the facing half-cone.</summary>
    public Component? ResolveMeleeTarget(Vector3 pose, Vector3 dir)
    {
        Component? best = null; var bestScore = float.MaxValue; var reachSq = MeleeReach * MeleeReach;
        foreach (var pair in runtime.Replication.AllEntities)
        {
            var c = pair.Value; if (c == null) continue;
            var p = c.transform.position;
            var dx = p.x - pose.x; var dy = p.y - pose.y; var dz = p.z - pose.z;
            var distSq = dx * dx + dy * dy + dz * dz;
            if (distSq > reachSq) continue;
            var flatLength = Mathf.Sqrt(dx * dx + dz * dz);
            var dot = flatLength > 0.001f ? (dir.x * dx + dir.z * dz) / flatLength : 1f;
            if (dot < MeleeConeDot) continue;
            if (c is Character ch) { if (!ch.alive || !ch.gameObject.activeSelf) continue; }
            else if (c is Item item) { if (item.destroyed || !item.gameObject.activeSelf) continue; }
            else if (c is Door door) { if (door.destroyed || !door.gameObject.activeSelf) continue; }
            else if (c is Window window) { if (!window.gameObject.activeSelf) continue; }
            else continue;
            var score = distSq - dot * 0.5f;
            if (score < bestScore) { bestScore = score; best = c; }
        }
        return best;
    }

    public void ApplyMeleeDamage(Component target, Transform attacker, int damage, int barricadeDamage, bool canCutInHalf)
    {
        try
        {
            if (target is Character character) character.getHit(damage, attacker, canCutInHalf, true, true);
            else if (target is Door door) door.getHit(barricadeDamage, attacker);
            else if (target is Window window) window.getHit(barricadeDamage, attacker);
            else if (target is Item item) item.getHit(barricadeDamage, attacker);
        }
        catch (Exception error) { runtime.LogWarning($"Authoritative melee damage application failed for {target.GetType().Name}: {error.Message}"); }
    }

    // ── 工具 ──
    private Vector3 GetPlayerPosition(int playerId)
    {
        if (playerId == 0) { var player = Player.Instance; return player != null ? player.transform.position : Vector3.zero; }
        if (runtime.Players.TryGetRemotePosition(playerId, out var position)) return position;
        return Vector3.zero;
    }

    private static float SqrDistance(Vector3 a, Vector3 b) { var dx = a.x - b.x; var dy = a.y - b.y; var dz = a.z - b.z; return dx * dx + dy * dy + dz * dz; }

    public void Reset()
    {
        peerHealths.Clear(); peerMaxHealths.Clear(); peerDowned.Clear(); nextGuestHitAllowed.Clear(); nextAttackAllowed.Clear();
        foreach (var anchor in remoteAttackAnchors.Values) if (anchor != null) UnityEngine.Object.Destroy(anchor);
        remoteAttackAnchors.Clear();
        localInvulUntil = 0f; activeRescue = null; hostDownedLocal = false; allDownedHandled = false;
        nextMonsterDamageScan = 0f; nextHealthHeartbeat = 0f; lastBroadcastHostHealth = float.MaxValue;
        nextRescueBroadcast = 0f; rescueLockedByMe = false; lastRescueProgress = default;
        DarkwoodDownedPatch.Reset();
    }
}
