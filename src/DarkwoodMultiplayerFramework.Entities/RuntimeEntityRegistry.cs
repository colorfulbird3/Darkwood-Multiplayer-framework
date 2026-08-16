using System.Collections.Generic;
using DarkwoodMultiplayerFramework.Protocol;

namespace DarkwoodMultiplayerFramework.Entities;

/// <summary>0.8.9 第八刀：运行时实体生命周期状态。</summary>
public enum RuntimeEntityLifecycleState
{
    /// <summary>已登记待触发（范围门控事件，客户端尚未收到 Spawn）。</summary>
    Pending = 0,
    /// <summary>已生成（客户端已实例化镜像）。</summary>
    Spawned = 1,
    /// <summary>已移除。</summary>
    Despawned = 2
}

/// <summary>
/// 0.8.8-alpha.2：Runtime Entity 注册表（与 Persistent Registry 分离）。
/// ID 纪律：Allocate() 在整个会话内单调递增、绝不复用——即使实体已被移除，
/// 它的 ID 也不会分配给新对象，因此晚到的 Despawn 包永远不会误杀新生实体。
/// ClearAlive() 仅清空存活集合（场景切换用），计数器继续递增。
/// </summary>
public sealed class RuntimeEntityRegistry
{
    private ulong _nextId = 1;
    private readonly Dictionary<ulong, RuntimeEntityRecord> _alive = new Dictionary<ulong, RuntimeEntityRecord>();

    public int Count => _alive.Count;
    public ulong NextId => _nextId;
    public IEnumerable<RuntimeEntityRecord> Alive => _alive.Values;

    /// <summary>分配一个新的运行时实体 ID。只能由 Host 调用。</summary>
    public ulong Allocate() => _nextId++;

    /// <summary>注册一个存活实体。重复 ID 返回 false（duplicate spawn 容错），不抛异常。</summary>
    public bool Register(RuntimeEntityRecord record)
    {
        if (_alive.ContainsKey(record.Id)) return false;
        _alive.Add(record.Id, record);
        return true;
    }

    public bool TryGet(ulong id, out RuntimeEntityRecord record) => _alive.TryGetValue(id, out record);

    /// <summary>移除（Despawn）。ID 不存在时返回 false（despawn unknown id 容错）。</summary>
    public bool Remove(ulong id) => _alive.Remove(id);

    /// <summary>0.8.9：更新生命周期状态（Pending→Spawned→Despawned）。</summary>
    public bool UpdateState(ulong id, RuntimeEntityLifecycleState state)
    {
        if (!_alive.TryGetValue(id, out var record)) return false;
        _alive[id] = record.WithState(state);
        return true;
    }

    /// <summary>0.8.9：绑定本地实例（客户端镜像 / 主机源对象）。</summary>
    public bool TryAttachInstance(ulong id, object instance)
    {
        if (!_alive.TryGetValue(id, out var record)) return false;
        _alive[id] = record.WithInstance(instance);
        return true;
    }

    /// <summary>场景切换清理：清空存活集合，但 ID 计数器继续单调递增（不复用）。</summary>
    public void ClearAlive() => _alive.Clear();
}

/// <summary>存活中的运行时实体记录（0.8.9：统一领域对象，含生命周期状态与本地实例）。</summary>
public readonly struct RuntimeEntityRecord
{
    public RuntimeEntityRecord(ulong id, RuntimeEntityKind kind, string prototypeId, string scene, long serverTick,
        RuntimeEntityLifecycleState state = RuntimeEntityLifecycleState.Pending, object? localInstance = null)
    {
        Id = id;
        Kind = kind;
        PrototypeId = prototypeId ?? string.Empty;
        Scene = scene ?? string.Empty;
        ServerTick = serverTick;
        State = state;
        LocalInstance = localInstance;
    }

    public ulong Id { get; }
    public RuntimeEntityKind Kind { get; }
    public string PrototypeId { get; }
    public string Scene { get; }
    public long ServerTick { get; }
    /// <summary>0.8.9：生命周期状态。</summary>
    public RuntimeEntityLifecycleState State { get; }
    /// <summary>0.8.9：本地实例（客户端镜像 Transform/Character，或主机源组件）。</summary>
    public object? LocalInstance { get; }

    public RuntimeEntityRecord WithState(RuntimeEntityLifecycleState state) =>
        new RuntimeEntityRecord(Id, Kind, PrototypeId, Scene, ServerTick, state, LocalInstance);

    public RuntimeEntityRecord WithInstance(object? instance) =>
        new RuntimeEntityRecord(Id, Kind, PrototypeId, Scene, ServerTick, State, instance);
}
