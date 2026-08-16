using System;
using System.IO;
using System.Text;

namespace DarkwoodMultiplayerFramework.Protocol;

public enum RuntimeEntityKind : byte
{
    /// <summary>非法/未知，线路上不应出现。</summary>
    Unknown = 0,
    /// <summary>运行时生成的可拾取物品（0.8.8-alpha.3 首个验证目标）。</summary>
    DroppedItem = 1,
    /// <summary>运行时生成的敌人（0.8.8-alpha.4）。</summary>
    Enemy = 2,
    /// <summary>敌人死亡产生的尸体。</summary>
    Corpse = 3,
    /// <summary>运行时生成的可搜刮容器（乌鸦群、动物尸体等 deathDrop 类对象）。</summary>
    LootContainer = 4,
}

/// <summary>0.8.8-alpha.1：运行时实体移除原因。</summary>
public enum RuntimeEntityDespawnReason : byte
{
    Unknown = 0,
    /// <summary>被拾取/收集（物品进背包）。</summary>
    Collected = 1,
    /// <summary>死亡（敌人）。</summary>
    Died = 2,
    /// <summary>被玩家破坏/摧毁。</summary>
    Destroyed = 3,
    /// <summary>其他（场景切换清理等）。</summary>
    Other = 255,
}

/// <summary>
/// 0.8.8-alpha.1：Runtime Entity 生成广播。RuntimeEntityId 只能由 Host 分配，
/// 会话内单调递增、绝不复用（销毁的 ID 不再分配给新对象）。
/// InitialState 预留给 alpha.3+ 的实体专属初始状态（当前可为空）。
/// </summary>
public readonly struct RuntimeEntitySpawnMessage
{
    public RuntimeEntitySpawnMessage(ulong runtimeEntityId,RuntimeEntityKind kind,string prototypeId,string scene,float x,float y,float z,float qx,float qy,float qz,float qw,byte[] initialState,long serverTick)
    {RuntimeEntityId=runtimeEntityId;Kind=kind;PrototypeId=prototypeId;Scene=scene;X=x;Y=y;Z=z;Qx=qx;Qy=qy;Qz=qz;Qw=qw;InitialState=initialState??Array.Empty<byte>();ServerTick=serverTick;}
    public ulong RuntimeEntityId {get;}
    public RuntimeEntityKind Kind {get;}
    public string PrototypeId {get;}
    public string Scene {get;}
    public float X {get;} public float Y {get;} public float Z {get;}
    public float Qx {get;} public float Qy {get;} public float Qz {get;} public float Qw {get;}
    public byte[] InitialState {get;}
    public long ServerTick {get;}
}

/// <summary>0.8.8-alpha.1：Runtime Entity 移除广播。</summary>
public readonly struct RuntimeEntityDespawnMessage
{
    public RuntimeEntityDespawnMessage(ulong runtimeEntityId,long serverTick,RuntimeEntityDespawnReason reason)
    {RuntimeEntityId=runtimeEntityId;ServerTick=serverTick;Reason=reason;}
    public ulong RuntimeEntityId {get;}
    public long ServerTick {get;}
    public RuntimeEntityDespawnReason Reason {get;}
}

/// <summary>0.8.8-alpha.6：主机场景切换通知（客户端收到后自动重连并重新加载新场景存档）。</summary>
