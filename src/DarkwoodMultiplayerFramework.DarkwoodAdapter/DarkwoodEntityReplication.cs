using System;
using System.Collections.Generic;
using DarkwoodMultiplayerFramework.Core;
using DarkwoodMultiplayerFramework.Protocol;
using UnityEngine;

namespace DarkwoodMultiplayerFramework.DarkwoodAdapter;

public sealed class DarkwoodEntityReplication
{
    private readonly Dictionary<EntityId,Component> entities=new Dictionary<EntityId,Component>(); private readonly Dictionary<EntityId,EntityStateWire> last=new Dictionary<EntityId,EntityStateWire>(); private readonly Dictionary<EntityId,EntityStateWire> targets=new Dictionary<EntityId,EntityStateWire>(); private readonly Dictionary<EntityId,InventoryStateMessage> lastInventories=new Dictionary<EntityId,InventoryStateMessage>(); private readonly HashSet<Character> frozen=new HashSet<Character>(); private readonly HashSet<Character> deadCharacters=new HashSet<Character>(); private ulong revision; public bool ApplyingRemote {get;private set;}
    /// <summary>Read-only enumeration of the bound entity map. Used by the host melee resolver; must not be mutated during iteration.</summary>
    public IEnumerable<KeyValuePair<EntityId,Component>> AllEntities=>entities;
    public void Rebuild(DarkwoodEntityScanner scanner){RestoreSimulation();entities.Clear();last.Clear();targets.Clear();lastInventories.Clear();deadCharacters.Clear();foreach(var c in scanner.ScanScene()){var id=scanner.ToPersistentId(c);if(!entities.ContainsKey(id))entities[id]=c;}}
    public EntityStateWire[] CaptureAll(){var a=new List<EntityStateWire>();foreach(var pair in entities){var state=Capture(pair.Key,pair.Value,++revision);a.Add(state);last[pair.Key]=state;}return a.ToArray();}
    public EntityStateWire[] CaptureDeltas(){var a=new List<EntityStateWire>();foreach(var pair in entities){var state=Capture(pair.Key,pair.Value,last.TryGetValue(pair.Key,out var old)?old.Revision+1:++revision);if(!last.TryGetValue(pair.Key,out old)||Changed(old,state)){a.Add(state);last[pair.Key]=state;}}return a.ToArray();}
    public void Apply(IEnumerable<EntityStateWire> states,bool immediate){ApplyingRemote=true;try{foreach(var s in states){var id=new EntityId(s.Value,s.Persistent);if(!entities.TryGetValue(id,out var component))continue;if(last.TryGetValue(id,out var old)&&s.Revision<old.Revision)continue;Apply(component,s,immediate);targets[id]=s;last[id]=s;}}finally{ApplyingRemote=false;}}
    public void Interpolate(float factor){foreach(var pair in targets){if(!entities.TryGetValue(pair.Key,out var c)||!(c is Character))continue;var s=pair.Value;c.transform.position=Vector3.Lerp(c.transform.position,new Vector3(s.X,s.Y,s.Z),Mathf.Clamp01(factor));c.transform.rotation=Quaternion.Slerp(c.transform.rotation,new Quaternion(s.Qx,s.Qy,s.Qz,s.Qw),Mathf.Clamp01(factor));}}
    public void RestoreSimulation(){foreach(var c in frozen)if(c!=null){c.enabled=true;if(c.AIpath!=null)c.AIpath.enabled=true;}frozen.Clear();foreach(var c in deadCharacters)if(c!=null)c.enabled=true;deadCharacters.Clear();}
    public EntityStateWire[] Snapshot()=>CaptureAll();
    public InventoryStateMessage[] CaptureInventorySnapshot(){var list=new List<InventoryStateMessage>();foreach(var pair in entities){if(!(pair.Value is Inventory inventory)||!IsShared(inventory))continue;var message=CaptureInventory(pair.Key,inventory,++revision);list.Add(message);lastInventories[pair.Key]=message;}return list.ToArray();}
    public InventoryStateMessage[] CaptureInventoryDeltas(){var list=new List<InventoryStateMessage>();foreach(var pair in entities){if(!(pair.Value is Inventory inventory)||!IsShared(inventory))continue;var next=CaptureInventory(pair.Key,inventory,lastInventories.TryGetValue(pair.Key,out var previous)?previous.Revision+1:++revision);if(!lastInventories.TryGetValue(pair.Key,out previous)||!SlotsEqual(previous.Slots,next.Slots)){list.Add(next);lastInventories[pair.Key]=next;}}return list.ToArray();}
    public InventoryStateMessage[] CaptureInventories()=>CaptureInventorySnapshot();
    public bool Apply(InventoryStateMessage message){var id=new EntityId(message.Value,message.Persistent);if(!entities.TryGetValue(id,out var component)||!(component is Inventory inventory)||!IsShared(inventory)){if(!TryRebindInventory(id,message,out inventory))return false;}if(lastInventories.TryGetValue(id,out var previous)&&message.Revision<previous.Revision)return true;var slots=new DarkwoodInventorySlot[message.Slots.Length];for(var i=0;i<slots.Length;i++){var s=message.Slots[i];slots[i]=new DarkwoodInventorySlot{Type=s.Type,Amount=s.Amount,Durability=s.Durability,Quality=s.Quality,Recipe=s.Recipe};}ApplyingRemote=true;try{DarkwoodInventoryAdapter.Apply(inventory,slots);lastInventories[id]=message;return true;}finally{ApplyingRemote=false;}}
    public bool TryGetInventoryState(EntityId id,out InventoryStateMessage state){if(entities.TryGetValue(id,out var component)&&component is Inventory inventory){state=CaptureInventory(id,inventory,lastInventories.TryGetValue(id,out var known)?known.Revision:0);return true;}state=default;return false;}
    public InventoryStateMessage CaptureAuthoritativeInventory(EntityId id){if(!entities.TryGetValue(id,out var component)||!(component is Inventory inventory))throw new InvalidOperationException("Inventory entity does not exist.");var message=CaptureInventory(id,inventory,lastInventories.TryGetValue(id,out var known)?known.Revision+1:++revision);lastInventories[id]=message;return message;}
    public void ApplyDespawns(IEnumerable<EntityStateWire> states){foreach(var s in states){var id=new EntityId(s.Value,s.Persistent);if(!entities.TryGetValue(id,out var component))continue;if(component is Character character)frozen.Remove(character);component.gameObject.SetActive(false);targets.Remove(id);last.Remove(id);lastInventories.Remove(id);}}
    public bool TryGetComponent(EntityId id,out Component component)=>entities.TryGetValue(id,out component!);
    /// <summary>Captures and commits fresh revisions for the given components (immediate authoritative broadcast helper).</summary>
    public EntityStateWire[] CaptureEntities(IEnumerable<Component> components)
    {
        var result=new List<EntityStateWire>();
        foreach(var component in components)
        {
            if(component==null)continue;
            if(!TryGetId(component,out var id))continue;
            var next=last.TryGetValue(id,out var known)?known.Revision+1:++revision;
            var state=Capture(id,component,next);
            last[id]=state;targets[id]=state;
            result.Add(state);
        }
        return result.ToArray();
    }
    public bool TryGetId(Component component,out EntityId id){foreach(var pair in entities)if(ReferenceEquals(pair.Value,component)){id=pair.Key;return true;}id=default;return false;}
    public bool TryGetState(EntityId id,out EntityStateWire state){if(entities.TryGetValue(id,out var component)){state=Capture(id,component,last.TryGetValue(id,out var known)?known.Revision:0);return true;}state=default;return false;}
    public EntityStateWire MarkDespawned(EntityId id){if(!entities.TryGetValue(id,out var component))throw new InvalidOperationException("Entity does not exist.");var nextRevision=last.TryGetValue(id,out var known)?known.Revision+1:++revision;var state=Capture(id,component,nextRevision);component.gameObject.SetActive(false);state=new EntityStateWire(state.Value,state.Persistent,state.Kind,state.X,state.Y,state.Z,state.Qx,state.Qy,state.Qz,state.Qw,state.Health,state.StateA,state.StateB,(byte)(state.Flags&~16),state.Animation,state.Frame,nextRevision);last[id]=state;targets.Remove(id);return state;}
    private static InventoryStateMessage CaptureInventory(EntityId id,Inventory inventory,ulong rev){var slots=DarkwoodInventoryAdapter.Capture(inventory);var wire=new InventorySlotWire[slots.Length];for(var i=0;i<slots.Length;i++)wire[i]=new InventorySlotWire(slots[i].Type,slots[i].Amount,slots[i].Durability,slots[i].Quality,slots[i].Recipe);var p=inventory.transform.position;return new InventoryStateMessage(id.Value,id.IsPersistent,rev,inventory.name,p.x,p.y,p.z,(int)inventory.invType,wire);}
    private bool TryRebindInventory(EntityId authoritativeId,InventoryStateMessage message,out Inventory inventory)
    {
        inventory=null!;EntityId localId=default;var best=float.MaxValue;
        foreach(var pair in entities)
        {
            if(!(pair.Value is Inventory candidate)||!IsShared(candidate)||(int)candidate.invType!=message.InventoryType)continue;
            if(message.Name.Length>0&&!string.Equals(candidate.name,message.Name,StringComparison.Ordinal))continue;
            var p=candidate.transform.position;var dx=p.x-message.X;var dy=p.y-message.Y;var dz=p.z-message.Z;var distance=dx*dx+dy*dy+dz*dz;
            if(distance<best){best=distance;inventory=candidate;localId=pair.Key;}
        }
        if(inventory==null||best>1f)return false;
        if(!localId.Equals(authoritativeId)){entities.Remove(localId);last.Remove(localId);targets.Remove(localId);lastInventories.Remove(localId);entities[authoritativeId]=inventory;}
        return true;
    }
    /// <summary>Diagnostic: describes the client's best matching candidates for a failed snapshot inventory binding.</summary>
    public string DescribeNearestInventory(InventoryStateMessage message)
    {
        var shared=0;var sameType=0;var sameName=0;var bestType=float.MaxValue;var bestName=float.MaxValue;string? bestTypeName=null;string? bestNameName=null;
        foreach(var pair in entities)
        {
            if(!(pair.Value is Inventory candidate)||!IsShared(candidate))continue;
            shared++;
            var p=candidate.transform.position;var dx=p.x-message.X;var dy=p.y-message.Y;var dz=p.z-message.Z;var distance=dx*dx+dy*dy+dz*dz;
            if((int)candidate.invType==message.InventoryType){sameType++;if(distance<bestType){bestType=distance;bestTypeName=candidate.name;}}
            if(message.Name.Length>0&&string.Equals(candidate.name,message.Name,StringComparison.Ordinal)){sameName++;if(distance<bestName){bestName=distance;bestNameName=candidate.name;}}
        }
        var typeInfo=sameType>0?$"（最近 {bestTypeName??"-"} {Mathf.Sqrt(bestType):F1}m）":"（无）";
        var nameInfo=sameName>0?$"（最近 {bestNameName??"-"} {Mathf.Sqrt(bestName):F1}m）":"（无）";
        return $"共享={shared}，同类型={sameType}{typeInfo}，同名={sameName}{nameInfo}";
    }
    public int SharedInventoryCount{get{var count=0;foreach(var pair in entities)if(pair.Value is Inventory inventory&&IsShared(inventory))count++;return count;}}
    private static bool IsShared(Inventory inventory)=>inventory.invType==Inventory.InvType.itemInv||inventory.invType==Inventory.InvType.deathDrop;
    private static bool SlotsEqual(InventorySlotWire[] a,InventorySlotWire[] b){if(a.Length!=b.Length)return false;for(var i=0;i<a.Length;i++)if(a[i].Type!=b[i].Type||a[i].Amount!=b[i].Amount||Math.Abs(a[i].Durability-b[i].Durability)>.001f||a[i].Quality!=b[i].Quality||a[i].Recipe!=b[i].Recipe)return false;return true;}
    private static EntityStateWire Capture(EntityId id,Component c,ulong rev){var p=c.transform.position;var q=c.transform.rotation;float h=0;int a=0,b=0;byte f=0;string anim="";int frame=0;byte kind=Kind(c);if(c is Character ch){h=ch.health;f=Flags(ch.alive,ch.gameObject.activeSelf,ch.attacking,ch.walking,ch.running);if(ch.animator!=null&&ch.animator.CurrentClip!=null){anim=ch.animator.CurrentClip.name;frame=ch.animator.CurrentFrame;}}else if(c is Door d){h=d.health;a=d.barricadeHealth;b=d.barricadeState;f=Flags(d.opened,d.barricaded,d.destroyed,d.blocked,d.gameObject.activeSelf);if(d.body!=null){p=d.body.position;q=d.body.rotation;}}else if(c is Window w){h=w.barricadeHealth;a=w.barricadeState;f=Flags(w.barricaded,w.blocked,w.gameObject.activeSelf,false);}else if(c is Item item){h=item.health;a=item.invItemAmount;f=Flags(item.destroyed,item.isOn,item.hasPower,item.searched,item.gameObject.activeSelf);}return new EntityStateWire(id.Value,id.IsPersistent,kind,p.x,p.y,p.z,q.x,q.y,q.z,q.w,h,a,b,f,anim,frame,rev);}
    private void Apply(Component c,EntityStateWire s,bool immediate){var p=new Vector3(s.X,s.Y,s.Z);var q=new Quaternion(s.Qx,s.Qy,s.Qz,s.Qw);if(c is Character ch){
        // Authoritative death mirror: when the host reports alive 1→0, let the game
        // turn the local copy into a corpse (die2, not die(): no onDeath story trigger
        // on the client). The corpse inventory is then corrected by the host's
        // authoritative InventoryState broadcast.
        if(ch.alive&&!Flag(s.Flags,0)&&!deadCharacters.Contains(ch)){deadCharacters.Add(ch);frozen.Remove(ch);ch.enabled=true;try{ch.die2();}catch(Exception){ch.gameObject.SetActive(false);}if(ch.gameObject.activeSelf)ch.enabled=false;}
        if(!deadCharacters.Contains(ch)&&frozen.Add(ch)){ch.enabled=false;if(ch.AIpath!=null)ch.AIpath.enabled=false;}
        ch.health=s.Health;ch.Health=s.Health;ch.alive=Flag(s.Flags,0);ch.walking=Flag(s.Flags,3);ch.running=Flag(s.Flags,4);ch.gameObject.SetActive(Flag(s.Flags,1));if(immediate){ch.transform.position=p;ch.transform.rotation=q;}}
        else if(c is Door d){d.opened=Flag(s.Flags,0);d.barricaded=Flag(s.Flags,1);d.destroyed=Flag(s.Flags,2);d.blocked=Flag(s.Flags,3);d.health=Mathf.RoundToInt(s.Health);d.barricadeHealth=s.StateA;d.barricadeState=s.StateB;if(d.body!=null){d.body.position=p;d.body.rotation=q;}d.gameObject.SetActive(Flag(s.Flags,4));}
        else if(c is Window w){w.barricaded=Flag(s.Flags,0);w.blocked=Flag(s.Flags,1);w.barricadeHealth=Mathf.RoundToInt(s.Health);w.barricadeState=s.StateA;w.gameObject.SetActive(Flag(s.Flags,2));}
        else if(c is Item item){item.destroyed=Flag(s.Flags,0);item.health=Mathf.RoundToInt(s.Health);item.invItemAmount=s.StateA;item.isOn=Flag(s.Flags,1);item.hasPower=Flag(s.Flags,2);item.searched=Flag(s.Flags,3);if(immediate){item.transform.position=p;item.transform.rotation=q;}item.gameObject.SetActive(Flag(s.Flags,4));}}
    private static bool Changed(EntityStateWire a,EntityStateWire b)=>Math.Abs(a.X-b.X)>.01f||Math.Abs(a.Y-b.Y)>.01f||Math.Abs(a.Health-b.Health)>.01f||a.StateA!=b.StateA||a.StateB!=b.StateB||a.Flags!=b.Flags||a.Frame!=b.Frame||a.Animation!=b.Animation;
    private static byte Kind(Component c)=>c is Character?(byte)1:c is Door?(byte)2:c is Window?(byte)3:c is Item?(byte)4:c is Inventory?(byte)5:(byte)0;
    private static byte Flags(bool a,bool b,bool c,bool d,bool e=false)=>(byte)((a?1:0)|(b?2:0)|(c?4:0)|(d?8:0)|(e?16:0)); private static bool Flag(byte f,int bit)=>(f&(1<<bit))!=0;
}
