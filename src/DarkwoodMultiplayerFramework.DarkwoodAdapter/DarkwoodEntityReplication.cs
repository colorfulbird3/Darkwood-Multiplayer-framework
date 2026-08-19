using System;
using System.Collections.Generic;
using System.Linq;
using DarkwoodMultiplayerFramework.Core;
using DarkwoodMultiplayerFramework.Protocol;
using UnityEngine;

namespace DarkwoodMultiplayerFramework.DarkwoodAdapter;

public sealed class DarkwoodEntityReplication
{
    private readonly Dictionary<EntityId,Component> entities=new Dictionary<EntityId,Component>(); private readonly Dictionary<EntityId,WorldEntityBinding> bindings=new Dictionary<EntityId,WorldEntityBinding>(); private readonly Dictionary<EntityId,EntityStateWire> last=new Dictionary<EntityId,EntityStateWire>(); private readonly Dictionary<EntityId,EntityStateWire> targets=new Dictionary<EntityId,EntityStateWire>(); private readonly Dictionary<EntityId,InventoryStateMessage> lastInventories=new Dictionary<EntityId,InventoryStateMessage>(); private readonly HashSet<Character> frozen=new HashSet<Character>(); private readonly HashSet<Character> deadCharacters=new HashSet<Character>(); private ulong revision; public bool ApplyingRemote {get;private set;}
    // P0-2/P0-3：实体已销毁（Unity destroyed/missing）的限频诊断（每实体每 10s 打一次，避免刷屏）。
    private readonly Dictionary<EntityId,float> staleLoggedAt=new Dictionary<EntityId,float>();
    // P1：per-kind 同步统计（0=未知 1=Character 2=Door 3=Window 4=Item 5=Inventory）。Host 用 Changed/Sent；Client 用 Received/Applied/Missing。
    public readonly long[] KindChanged=new long[6], KindSent=new long[6], KindReceived=new long[6], KindApplied=new long[6], KindMissing=new long[6];
    public void ResetKindDiagnostics(){for(var i=0;i<6;i++){KindChanged[i]=0;KindSent[i]=0;KindReceived[i]=0;KindApplied[i]=0;KindMissing[i]=0;}}
    /// <summary>只读快照枚举，允许调用期间 unregister/ForceDespawn（P0-2：禁止在遍历中删除字典）。</summary>
    public KeyValuePair<EntityId,Component>[] EntitySnapshot()=>entities.ToArray();
    private bool IsStale(Component component)=>component==null||component.gameObject==null;
    /// <summary>限频 stale/异常诊断。componentName 可为 null（对象已无法解析）。</summary>
    private void LogStale(EntityId id,Component component,string context,Exception? error,string? componentName=null,string? componentType=null)
    {
        if(Time.realtimeSinceStartup < (staleLoggedAt.TryGetValue(id,out var at)?at:0f))return;
        staleLoggedAt[id]=Time.realtimeSinceStartup+10f;
        var name=componentName??(component!=null?component.name:"?");
        var type=componentType??(component!=null?component.GetType().Name:"?");
        var logger=DarkwoodAdapterRuntime.Instance?.log;
        if(error!=null)logger?.LogWarning($"[SYNC] {context} 异常（限频）：id={id} type={type} name={name} persistent={id.IsPersistent} {error.GetType().Name}: {error.Message}");
        else logger?.LogWarning($"[SYNC] stale capture：id={id} type={type} name={name} persistent={id.IsPersistent}");
    }
    /// <summary>统一清理 stale 实体（从全部字典移除）。不在遍历中调用。</summary>
    private void PurgeStale()
    {
        if(stalePending.Count==0)return;
        foreach(var id in stalePending){entities.Remove(id);bindings.Remove(id);last.Remove(id);targets.Remove(id);lastInventories.Remove(id);}
        stalePending.Clear();
    }
    private readonly HashSet<EntityId> stalePending=new HashSet<EntityId>();
    /// <summary>只读枚举（Combat 等调用方用；遍历时不得修改集合——用 EntitySnapshot()）。</summary>
    public IEnumerable<KeyValuePair<EntityId,Component>> AllEntities=>entities;
    public void Rebuild(DarkwoodEntityScanner scanner){RestoreSimulation();entities.Clear();bindings.Clear();last.Clear();targets.Clear();lastInventories.Clear();deadCharacters.Clear();foreach(var c in scanner.ScanScene()){var id=scanner.ToPersistentId(c);if(!entities.ContainsKey(id))entities[id]=c;}}
    /// <summary>用主机权威注册表同一份扫描结果构建复制状态（避免 registry 与 replication 各扫一次）。</summary>
    public void Rebuild(System.Collections.Generic.IEnumerable<System.Collections.Generic.KeyValuePair<EntityId, Component>> pairs){RestoreSimulation();entities.Clear();bindings.Clear();last.Clear();targets.Clear();lastInventories.Clear();deadCharacters.Clear();foreach(var pair in pairs)if(!entities.ContainsKey(pair.Key))entities[pair.Key]=pair.Value;}
    public EntityStateWire[] CaptureAll(){var a=new List<EntityStateWire>();foreach(var pair in entities.ToArray()){var component=pair.Value;if(IsStale(component)){LogStale(pair.Key,component,"CaptureAll",null);stalePending.Add(pair.Key);continue;}EntityStateWire state;try{state=DarkwoodEntityStateAdapter.Capture(pair.Key,component,++revision);}catch(Exception error){LogStale(pair.Key,component,"CaptureAll",error);continue;}a.Add(state);last[pair.Key]=state;}PurgeStale();return a.ToArray();}
    public EntityStateWire[] CaptureDeltas(){var a=new List<EntityStateWire>();var changed=0;foreach(var pair in entities.ToArray()){var component=pair.Value;if(IsStale(component)){LogStale(pair.Key,component,"CaptureDeltas",null);stalePending.Add(pair.Key);continue;}EntityStateWire state;try{state=DarkwoodEntityStateAdapter.Capture(pair.Key,component,last.TryGetValue(pair.Key,out var old)?old.Revision+1:++revision);}catch(Exception error){LogStale(pair.Key,component,"CaptureDeltas",error);stalePending.Add(pair.Key);continue;}if(!last.TryGetValue(pair.Key,out var old2)||Changed(old2,state)){a.Add(state);last[pair.Key]=state;changed++;KindSent[state.Kind]++;KindChanged[state.Kind]++;}}PurgeStale();LastDeltaChangedCount=changed;LastDeltaSentCount=a.Count;return a.ToArray();}
    public void Apply(IEnumerable<EntityStateWire> states,bool immediate){Apply(states,immediate,out _);}
    public ApplyStats Apply(IEnumerable<EntityStateWire> states,bool immediate,out ApplyStats stats)
    {
        stats=new ApplyStats();ApplyingRemote=true;
        try
        {
            foreach(var s in states)
            {
                stats.Received++;DeltaReceived++;KindReceived[s.Kind]++;
                var id=new EntityId(s.Value,s.Persistent);
                if(!entities.TryGetValue(id,out var component)){stats.RecordMissing(DescribeMissing(id,s),s.Kind);DeltaMissing++;KindMissing[s.Kind]++;continue;}
                if(last.TryGetValue(id,out var old)&&s.Revision<old.Revision){stats.Stale++;continue;}
                try{DarkwoodEntityStateAdapter.Apply(component,s,immediate,frozen,deadCharacters);}
                catch(Exception error){LogStale(id,component,"Apply",error);continue;}
                targets[id]=s;last[id]=s;stats.Applied++;DeltaApplied++;KindApplied[s.Kind]++;
            }
        }
        finally{ApplyingRemote=false;}
        return stats;
    }
    private string DescribeMissing(EntityId id,EntityStateWire s)
    {
        if(descriptorByEntity.TryGetValue(id,out var entry))return $"{id} kind={s.Kind} name={entry.ObjectName} {entry.Describe()}";
        return $"{id} kind={s.Kind} (no binding descriptor)";
    }
    public long DeltaReceived {get;private set;}
    public long DeltaApplied {get;private set;}
    public long DeltaMissing {get;private set;}
    public int LastDeltaChangedCount {get;private set;}
    public int LastDeltaSentCount {get;private set;}
    public void ResetDeltaDiagnostics(){DeltaReceived=0;DeltaApplied=0;DeltaMissing=0;}
    private readonly Dictionary<EntityId,EntityBindingEntryWire> descriptorByEntity=new Dictionary<EntityId,EntityBindingEntryWire>();
    public void Interpolate(float factor){foreach(var pair in targets){if(!entities.TryGetValue(pair.Key,out var c)||!(c is Character))continue;var s=pair.Value;c.transform.position=Vector3.Lerp(c.transform.position,new Vector3(s.X,s.Y,s.Z),Mathf.Clamp01(factor));c.transform.rotation=Quaternion.Slerp(c.transform.rotation,new Quaternion(s.Qx,s.Qy,s.Qz,s.Qw),Mathf.Clamp01(factor));}}
    public void RestoreSimulation(){foreach(var c in frozen)if(c!=null){c.enabled=true;if(c.AIpath!=null)c.AIpath.enabled=true;}frozen.Clear();foreach(var c in deadCharacters)if(c!=null)c.enabled=true;deadCharacters.Clear();}
    public EntityStateWire[] Snapshot()=>CaptureAll();
    public InventoryStateMessage[] CaptureInventorySnapshot(){var list=new List<InventoryStateMessage>();foreach(var pair in entities.ToArray()){var component=pair.Value;if(IsStale(component)){LogStale(pair.Key,component,"CaptureInventorySnapshot",null);stalePending.Add(pair.Key);continue;}if(!(component is Inventory inventory)||!DarkwoodEntityStateAdapter.IsShared(inventory))continue;InventoryStateMessage message;try{message=DarkwoodEntityStateAdapter.CaptureInventory(pair.Key,inventory,++revision);}catch(Exception error){LogStale(pair.Key,inventory,"CaptureInventorySnapshot",error);continue;}list.Add(message);lastInventories[pair.Key]=message;}PurgeStale();return list.ToArray();}
    public InventoryStateMessage[] CaptureInventoryDeltas(){var list=new List<InventoryStateMessage>();foreach(var pair in entities.ToArray()){var component=pair.Value;if(IsStale(component)){LogStale(pair.Key,component,"CaptureInventoryDeltas",null);stalePending.Add(pair.Key);continue;}if(!(component is Inventory inventory)||!DarkwoodEntityStateAdapter.IsShared(inventory))continue;InventoryStateMessage previous=default!;bool hadPrevious=false;InventoryStateMessage next;try{hadPrevious=lastInventories.TryGetValue(pair.Key,out previous);next=DarkwoodEntityStateAdapter.CaptureInventory(pair.Key,inventory,hadPrevious?previous.Revision+1:++revision);}catch(Exception error){LogStale(pair.Key,inventory,"CaptureInventoryDeltas",error);continue;}if(!hadPrevious||!DarkwoodEntityStateAdapter.SlotsEqual(previous.Slots,next.Slots)){list.Add(next);lastInventories[pair.Key]=next;}}PurgeStale();return list.ToArray();}
    public InventoryStateMessage[] CaptureInventories()=>CaptureInventorySnapshot();
    public bool Apply(InventoryStateMessage message){var id=new EntityId(message.Value,message.Persistent);if(!entities.TryGetValue(id,out var component)||!(component is Inventory inventory)||!DarkwoodEntityStateAdapter.IsShared(inventory))return false;if(lastInventories.TryGetValue(id,out var previous)&&message.Revision<previous.Revision)return true;var slots=new DarkwoodInventorySlot[message.Slots.Length];for(var i=0;i<slots.Length;i++){var s=message.Slots[i];slots[i]=new DarkwoodInventorySlot{Type=s.Type,Amount=s.Amount,Durability=s.Durability,Quality=s.Quality,Recipe=s.Recipe};}ApplyingRemote=true;try{DarkwoodInventoryAdapter.Apply(inventory,slots);lastInventories[id]=message;return true;}catch(Exception error){LogStale(id,inventory,"Apply(Inventory)",error);return false;}finally{ApplyingRemote=false;}}
    public bool TryGetInventoryState(EntityId id,out InventoryStateMessage state){if(entities.TryGetValue(id,out var component)&&component is Inventory inventory){state=DarkwoodEntityStateAdapter.CaptureInventory(id,inventory,lastInventories.TryGetValue(id,out var known)?known.Revision:0);return true;}state=default;return false;}
    public InventoryStateMessage CaptureAuthoritativeInventory(EntityId id){if(!entities.TryGetValue(id,out var component)||!(component is Inventory inventory))throw new InvalidOperationException("Inventory entity does not exist.");var message=DarkwoodEntityStateAdapter.CaptureInventory(id,inventory,lastInventories.TryGetValue(id,out var known)?known.Revision+1:++revision);lastInventories[id]=message;return message;}

    /// <summary>为运行时容器（不在 entities 注册表内）捕获库存状态，用于 RuntimeEntitySpawn 的 InitialState。</summary>
    public InventoryStateMessage CaptureInventoryState(Inventory inventory,ulong value){if(inventory==null)throw new ArgumentNullException(nameof(inventory));return DarkwoodEntityStateAdapter.CaptureInventory(new EntityId(value,false),inventory,++revision);}

    /// <summary>把运行时实体（敌人代理等）注册进 entities，使其自动纳入 15Hz delta 捕获/应用。</summary>
    public void RegisterRuntimeEntity(EntityId id,Component component){if(entities.ContainsKey(id))return;entities[id]=component;bindings[id]=WorldEntityBinding.FromComponent(id,component);}

    /// <summary>注册完整绑定（掉落物/容器/敌人等组件群）。</summary>
    public void RegisterBinding(WorldEntityBinding binding){if(entities.ContainsKey(binding.Id))return;entities[binding.Id]=binding.Primary;bindings[binding.Id]=binding;}

    /// <summary>统一实体查找：任意 EntityId 返回绑定（持久实体自动构造单组件绑定）。</summary>
    public bool TryGetBinding(EntityId id,out WorldEntityBinding binding){if(bindings.TryGetValue(id,out binding!))return true;if(entities.TryGetValue(id,out var component)){binding=WorldEntityBinding.FromComponent(id,component);return true;}binding=null;return false;}
    public bool TryGetItem(EntityId id,out Item item){if(TryGetBinding(id,out var binding)&&binding.Item!=null){item=binding.Item;return true;}item=null;return false;}
    public bool TryGetInventory(EntityId id,out Inventory inventory){if(TryGetBinding(id,out var binding)&&binding.Inventory!=null){inventory=binding.Inventory;return true;}inventory=null;return false;}
    public bool TryGetCharacter(EntityId id,out Character character){if(TryGetBinding(id,out var binding)&&binding.Character!=null){character=binding.Character;return true;}character=null;return false;}

    /// <summary>移除运行时实体的注册（Despawn 后不再参与 delta）。</summary>
    public void UnregisterRuntimeEntity(EntityId id){entities.Remove(id);bindings.Remove(id);last.Remove(id);targets.Remove(id);lastInventories.Remove(id);}
    /// <summary>枚举全部注册实体（持久销毁检测用；调用方不得在遍历时修改）。</summary>
    public IEnumerable<KeyValuePair<EntityId,Component>> Entities(){foreach(var pair in entities)yield return pair;}
    /// <summary>实体已被游戏销毁（夹子拆除/物品拾取等）——构造 Despawn 状态并移出注册表。</summary>
    public EntityStateWire ForceDespawn(EntityId id)
    {
        if(!entities.ContainsKey(id))throw new InvalidOperationException("Entity does not exist.");
        var nextRevision=last.TryGetValue(id,out var known)?known.Revision+1:++revision;
        var wire=new EntityStateWire(id.Value,id.IsPersistent,known.Kind,known.X,known.Y,known.Z,known.Qx,known.Qy,known.Qz,known.Qw,known.Health,known.StateA,known.StateB,(byte)(known.Flags&~16),known.Animation,known.Frame,nextRevision);
        entities.Remove(id);bindings.Remove(id);last.Remove(id);targets.Remove(id);lastInventories.Remove(id);
        return wire;
    }
    public void ApplyDespawns(IEnumerable<EntityStateWire> states){foreach(var s in states){var id=new EntityId(s.Value,s.Persistent);if(!entities.TryGetValue(id,out var component))continue;if(component is Character character)frozen.Remove(character);component.gameObject.SetActive(false);bindings.Remove(id);targets.Remove(id);last.Remove(id);lastInventories.Remove(id);}}
    public bool TryGetComponent(EntityId id,out Component component)=>entities.TryGetValue(id,out component!);
    /// <summary>Captures and commits fresh revisions for the given components (immediate authoritative broadcast helper).</summary>
    public EntityStateWire[] CaptureEntities(IEnumerable<Component> components)
    {
        var result=new List<EntityStateWire>();
        foreach(var component in components)
        {
            if(component==null||component.gameObject==null)continue;
            if(!TryGetId(component,out var id))continue;
            var next=last.TryGetValue(id,out var known)?known.Revision+1:++revision;
            var state=DarkwoodEntityStateAdapter.Capture(id,component,next);
            last[id]=state;targets[id]=state;
            result.Add(state);
        }
        return result.ToArray();
    }
    public bool TryGetId(Component component,out EntityId id)
    {
        foreach(var pair in bindings)
        {
            var b=pair.Value;
            if(ReferenceEquals(b.Primary,component)||ReferenceEquals(b.Item,component)||ReferenceEquals(b.Inventory,component)||ReferenceEquals(b.Character,component)){id=pair.Key;return true;}
        }
        foreach(var pair in entities)if(ReferenceEquals(pair.Value,component)){id=pair.Key;return true;}
        id=default;return false;
    }
    public bool TryGetState(EntityId id,out EntityStateWire state){if(entities.TryGetValue(id,out var component)){state=DarkwoodEntityStateAdapter.Capture(id,component,last.TryGetValue(id,out var known)?known.Revision:0);return true;}state=default;return false;}
    public EntityStateWire MarkDespawned(EntityId id){if(!entities.TryGetValue(id,out var component))throw new InvalidOperationException("Entity does not exist.");var nextRevision=last.TryGetValue(id,out var known)?known.Revision+1:++revision;var state=DarkwoodEntityStateAdapter.Capture(id,component,nextRevision);component.gameObject.SetActive(false);state=new EntityStateWire(state.Value,state.Persistent,state.Kind,state.X,state.Y,state.Z,state.Qx,state.Qy,state.Qz,state.Qw,state.Health,state.StateA,state.StateB,(byte)(state.Flags&~16),state.Animation,state.Frame,nextRevision);last[id]=state;targets.Remove(id);return state;}
    /// <summary>客户端：按主机权威描述符显式绑定本地组件（替代本地 hash 注册表）。
    /// 绑定后 entities/bindings 的键即主机权威 EntityId；ActionRequest 经 TryGetId 自然使用权威 ID。</summary>
    public void BindFromManifest(EntityBindingEntryWire[] entries, EntityBindingOutcome outcome, Component[] localCandidates)
    {
        RestoreSimulation();
        entities.Clear();bindings.Clear();last.Clear();targets.Clear();lastInventories.Clear();deadCharacters.Clear();descriptorByEntity.Clear();
        foreach(var pair in outcome.Pairs)
        {
            var entry=entries[pair.EntryIndex];
            var component=localCandidates[pair.LocalIndex];
            if(component==null||component.gameObject==null)continue;
            var id=new EntityId(entry.EntityValue,true);
            entities[id]=component;
            bindings[id]=WorldEntityBinding.FromComponent(id,component);
            descriptorByEntity[id]=entry;
        }
    }
    /// <summary>客户端：绑定完成后冻结未匹配到任何权威 ID 的本地 Character（禁止静默本地模拟）。</summary>
    public void FreezeUnboundCharacters(Component[] localCandidates)
    {
        foreach(var component in localCandidates)
        {
            if(!(component is Character character)||character is Player)continue;
            if(!TryGetId(character,out _)&&frozen.Add(character)){character.enabled=false;if(character.AIpath!=null)character.AIpath.enabled=false;}
        }
    }
    public int RegistryGeneration {get;private set;}
    public int BoundEntityCount => entities.Count;
    /// <summary>注册表代际换代：清空全部绑定与状态（收到新 generation manifest / 场景切换时）。</summary>
    public void BeginNewGeneration(int generation){RegistryGeneration=generation;RestoreSimulation();entities.Clear();bindings.Clear();last.Clear();targets.Clear();lastInventories.Clear();deadCharacters.Clear();descriptorByEntity.Clear();ResetDeltaDiagnostics();}
    public bool TryGetDescriptor(EntityId id,out EntityBindingEntryWire entry)=>descriptorByEntity.TryGetValue(id,out entry);
    /// <summary>枚举已绑定的权威描述符（主机快照缺失诊断用）。</summary>
    public IEnumerable<EntityBindingEntryWire> BoundDescriptors=>descriptorByEntity.Values;
    /// <summary>Diagnostic: describes the client's best matching candidates for a failed snapshot inventory binding.</summary>
    public string DescribeNearestInventory(InventoryStateMessage message)
    {
        var shared=0;var sameType=0;var sameName=0;var bestType=float.MaxValue;var bestName=float.MaxValue;string? bestTypeName=null;string? bestNameName=null;
        foreach(var pair in entities)
        {
            if(!(pair.Value is Inventory candidate)||!DarkwoodEntityStateAdapter.IsShared(candidate))continue;
            shared++;
            var p=candidate.transform.position;var dx=p.x-message.X;var dy=p.y-message.Y;var dz=p.z-message.Z;var distance=dx*dx+dy*dy+dz*dz;
            if((int)candidate.invType==message.InventoryType){sameType++;if(distance<bestType){bestType=distance;bestTypeName=candidate.name;}}
            if(message.Name.Length>0&&string.Equals(candidate.name,message.Name,StringComparison.Ordinal)){sameName++;if(distance<bestName){bestName=distance;bestNameName=candidate.name;}}
        }
        var typeInfo=sameType>0?$"（最近 {bestTypeName??"-"} {Mathf.Sqrt(bestType):F1}m）":"（无）";
        var nameInfo=sameName>0?$"（最近 {bestNameName??"-"} {Mathf.Sqrt(bestName):F1}m）":"（无）";
        return $"共享={shared}，同类型={sameType}{typeInfo}，同名={sameName}{nameInfo}";
    }
    public int SharedInventoryCount{get{var count=0;foreach(var pair in entities)if(pair.Value is Inventory inventory&&DarkwoodEntityStateAdapter.IsShared(inventory))count++;return count;}}
    private static bool Changed(EntityStateWire a,EntityStateWire b)=>Math.Abs(a.X-b.X)>.01f||Math.Abs(a.Y-b.Y)>.01f||Math.Abs(a.Z-b.Z)>.01f||Math.Abs(a.Qx-b.Qx)>.001f||Math.Abs(a.Qy-b.Qy)>.001f||Math.Abs(a.Qz-b.Qz)>.001f||Math.Abs(a.Qw-b.Qw)>.001f||Math.Abs(a.Health-b.Health)>.01f||a.StateA!=b.StateA||a.StateB!=b.StateB||a.Flags!=b.Flags||a.Frame!=b.Frame||a.Animation!=b.Animation;
}
