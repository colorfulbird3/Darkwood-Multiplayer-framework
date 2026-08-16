using System;
using System.IO;
using System.Text;

namespace DarkwoodMultiplayerFramework.Protocol;

public readonly struct InventorySlotWire
{
    public InventorySlotWire(string type,int amount,float durability,int quality,bool recipe){Type=type??string.Empty;Amount=amount;Durability=durability;Quality=quality;Recipe=recipe;}
    public string Type {get;} public int Amount {get;} public float Durability {get;} public int Quality {get;} public bool Recipe {get;}
}

public readonly struct InventoryStateMessage
{
    public InventoryStateMessage(ulong value,bool persistent,ulong revision,InventorySlotWire[] slots)
        : this(value,persistent,revision,string.Empty,0,0,0,-1,slots){}
    public InventoryStateMessage(ulong value,bool persistent,ulong revision,string name,float x,float y,float z,int inventoryType,InventorySlotWire[] slots){Value=value;Persistent=persistent;Revision=revision;Name=name??string.Empty;X=x;Y=y;Z=z;InventoryType=inventoryType;Slots=slots??Array.Empty<InventorySlotWire>();}
    public ulong Value {get;} public bool Persistent {get;} public ulong Revision {get;} public string Name {get;} public float X {get;} public float Y {get;} public float Z {get;} public int InventoryType {get;} public InventorySlotWire[] Slots {get;}
}
