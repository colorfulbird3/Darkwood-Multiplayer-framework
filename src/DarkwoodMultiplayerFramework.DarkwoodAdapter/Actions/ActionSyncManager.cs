using System;
using System.Collections.Generic;
using DarkwoodMultiplayerFramework.Core;
using DarkwoodMultiplayerFramework.DarkwoodAdapter.World;
using DarkwoodMultiplayerFramework.Protocol;
using UnityEngine;

namespace DarkwoodMultiplayerFramework.DarkwoodAdapter.Actions;

/// <summary>
/// v0.9.0 Action Sync：把「带原版副作用的对象变化」表达为一次已执行的原版函数回放。
/// 三分法：Action(本层) + State(typed/1Hz/快照兜底) + Event(门等即时)。
/// 纪律：Host 是唯一逻辑执行者；客户端只做副作用 Replay；Replay 幂等 + (id,key,tick) 去重；
/// 运行在 ApplyingRemote 内以防回环（Replay 命中原版 Patch 时不再发 intent）。
/// </summary>
public sealed class ActionSyncManager
{
    /// <summary>ActionKey 分配表（新动作在此登记）。</summary>
    public static class Keys
    {
        public const byte GeneratorToggle = 1; // A1：发电机 on/off（Replay = 原版 turnOn/turnOff，本地电源给灯供电）
        public const byte DoorToggle = 2;      // A4：门开/关（Replay = 原版 openClose）
    }

    /// <summary>一种世界对象动作：Host 唯一执行 + 各端 Replay（副作用重放）。</summary>
    public interface IWorldObjectAction
    {
        byte ActionKey { get; }
        /// <summary>Host 侧：执行原版副作用（唯一一次），随后框架负责广播 ActionExecuted。</summary>
        void ExecuteHost(DarkwoodAdapterRuntime runtime, EntityId id, byte[] param);
        /// <summary>各端（含 Host 自己不需要）：在 ApplyingRemote 内 Replay 同一原版函数。</summary>
        void Replay(DarkwoodAdapterRuntime runtime, Component target, byte[] param);
    }

    private readonly Dictionary<byte, IWorldObjectAction> actions = new Dictionary<byte, IWorldObjectAction>();
    private readonly HashSet<(ulong Value, bool Persistent, byte Key, long Tick)> recent = new HashSet<(ulong, bool, byte, long)>();
    private readonly Queue<(ulong Value, bool Persistent, byte Key, long Tick)> recentOrder = new Queue<(ulong, bool, byte, long)>();
    private const int RecentCapacity = 256;

    public void Register(IWorldObjectAction action)
    {
        if (action == null) return;
        actions[action.ActionKey] = action;
    }

    public bool IsRegistered(byte key) => actions.ContainsKey(key);

    /// <summary>Host 专用：执行已注册动作并广播 ActionExecuted 给所有 ready peer。</summary>
    public bool HostExecuteAndBroadcast(DarkwoodAdapterRuntime runtime, EntityId id, byte actionKey, byte[] param, int actorId = 0)
    {
        if (runtime == null || !runtime.IsHost) return false;
        if (!actions.TryGetValue(actionKey, out var action)) { runtime.log?.LogWarning($"[ACTION] 未注册 ActionKey {actionKey}，Host 拒绝执行。"); return false; }
        try { action.ExecuteHost(runtime, id, param); }
        catch (Exception error) { runtime.log?.LogError($"[ACTION] Host 执行失败 key={actionKey} id={id}: {error}"); return false; }
        var tick = runtime.replication.AllocateRevision();
        var payload = ReplicationProtocolCodec.Encode(new ActionExecutedMessage(id.Value, id.IsPersistent, actionKey, param, (long)tick, actorId));
        foreach (var pid in runtime.ReadyPeersSnapshot) runtime.Queue(pid, ProtocolMessageType.ActionExecuted, payload);
        runtime.log?.LogInfo($"[ACTION] Host 已执行并广播 key={actionKey} id={id} tick={tick} peers={runtime.ReadyPeersSnapshot.Length}");
        return true;
    }

    /// <summary>接收端（Client；Host 不回放自己的广播）：去重 → 定位绑定 → ApplyingRemote 内 Replay。</summary>
    public bool HandleReceived(DarkwoodAdapterRuntime runtime, byte[] payload)
    {
        if (runtime == null) return false;
        ActionExecutedMessage message;
        try { message = ReplicationProtocolCodec.DecodeActionExecuted(payload); }
        catch (Exception error) { runtime.log?.LogWarning($"[ACTION] ActionExecuted 解码失败：{error.Message}"); return false; }
        var dedupKey = (message.EntityValue, message.Persistent, message.ActionKey, message.Tick);
        if (recent.Contains(dedupKey)) return true; // 重复/重放
        Remember(dedupKey);
        if (!actions.TryGetValue(message.ActionKey, out var action))
        {
            // 未注册 key：不视为错误（新 Host 可能比旧 Client 新），等 State 兜底。
            runtime.log?.LogInfo($"[ACTION] 收到未注册 key={message.ActionKey} id=({message.EntityValue:X16},{message.Persistent}) tick={message.Tick}，由 State 兜底。");
            return false;
        }
        var id = new EntityId(message.EntityValue, message.Persistent);
        Component? component = null;
        WorldEntityBinding? binding = null;
        var hasAny = runtime.replication.TryGetComponent(id, out component);
        if (!hasAny) hasAny = runtime.replication.TryGetBinding(id, out binding);
        if (!hasAny)
        {
            runtime.log?.LogWarning($"[ACTION] 目标实体缺失 key={message.ActionKey} id={id}（可能晚加入/未绑定，State 兜底）。");
            return false;
        }
        if (component == null && binding != null) { try { component = binding.Primary; } catch { } }
        if (component == null) return false;
        try
        {
            runtime.replication.BeginRemoteApply();
            action.Replay(runtime, component, message.Param);
        }
        catch (Exception error) { runtime.log?.LogWarning($"[ACTION] Replay 失败 key={message.ActionKey} id={id}: {error.Message}"); }
        finally { runtime.replication.EndRemoteApply(); }
        runtime.log?.LogInfo($"[ACTION] Replay 完成 key={message.ActionKey} id={id} tick={message.Tick}");
        return true;
    }

    /// <summary>Host 已在原地执行完副作用（inline 路径）时，只负责广播 ActionExecuted 让各端 Replay。</summary>
    public bool BroadcastExecuted(DarkwoodAdapterRuntime runtime, EntityId id, byte actionKey, byte[] param, int actorId = 0)
    {
        if (runtime == null || !runtime.IsHost) return false;
        if (!actions.ContainsKey(actionKey)) { runtime.log?.LogWarning($"[ACTION] 广播未注册 key={actionKey}（跳过）。"); return false; }
        var tick = runtime.replication.AllocateRevision();
        var payload = ReplicationProtocolCodec.Encode(new ActionExecutedMessage(id.Value, id.IsPersistent, actionKey, param, (long)tick, actorId));
        foreach (var pid in runtime.ReadyPeersSnapshot) runtime.Queue(pid, ProtocolMessageType.ActionExecuted, payload);
        runtime.log?.LogInfo($"[ACTION] Host 广播 key={actionKey} id={id} tick={tick} peers={runtime.ReadyPeersSnapshot.Length}");
        return true;
    }

    private void Remember((ulong Value, bool Persistent, byte Key, long Tick) key)
    {
        recent.Add(key);
        recentOrder.Enqueue(key);
        while (recentOrder.Count > RecentCapacity)
        {
            var old = recentOrder.Dequeue();
            recent.Remove(old);
        }
    }
}
