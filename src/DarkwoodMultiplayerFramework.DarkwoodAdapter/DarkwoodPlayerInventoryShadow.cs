using System;
using System.Collections.Generic;
using DarkwoodMultiplayerFramework.Protocol;

namespace DarkwoodMultiplayerFramework.DarkwoodAdapter;

/// <summary>Host-owned, slot-exact inventory model for a remote player.</summary>
internal sealed class DarkwoodPlayerInventoryShadow
{
    internal readonly struct Item
    {
        public Item(string type,int amount,float durability,int quality,bool recipe,int maxAmount,bool stackable)
        {Type=type;Amount=amount;Durability=durability;Quality=quality;Recipe=recipe;MaxAmount=maxAmount;Stackable=stackable;}
        public string Type {get;} public int Amount {get;} public float Durability {get;} public int Quality {get;} public bool Recipe {get;} public int MaxAmount {get;} public bool Stackable {get;}
    }
    private sealed class Slot { public string Type=string.Empty; public int Amount; public float Durability; public int Quality; public bool Recipe; public int MaxAmount=1; public bool Stackable; }
    private readonly List<Slot> backpack=new List<Slot>();
    private readonly List<Slot> hotbar=new List<Slot>();

    public static DarkwoodPlayerInventoryShadow CaptureInitial()
    {
        var shadow=new DarkwoodPlayerInventoryShadow();var player=Player.Instance;if(player==null)return shadow;
        shadow.Capture(player.Inventory,shadow.backpack);shadow.Capture(player.Hotbar,shadow.hotbar);return shadow;
    }

    public bool CanAdd(InvItemClass source)
    {
        if(InvItemClass.isNull(source)||source.amount<=0||source.baseClass==null)return false;var remaining=source.amount;
        if(source.baseClass.stackable)foreach(var slot in AllSlots())if(slot.Type==source.type&&slot.Stackable){remaining-=Math.Max(0,slot.MaxAmount-slot.Amount);if(remaining<=0)return true;}
        foreach(var slot in AllSlots())if(string.IsNullOrEmpty(slot.Type)){remaining-=source.baseClass.stackable?Math.Max(1,source.baseClass.maxAmount):1;if(remaining<=0)return true;}
        return false;
    }

    public void Add(InvItemClass source)
    {
        if(!CanAdd(source)||source.baseClass==null)throw new InvalidOperationException("Remote inventory has no capacity.");var remaining=source.amount;
        if(source.baseClass.stackable)foreach(var slot in AllSlots())if(slot.Type==source.type&&slot.Stackable&&slot.Amount<slot.MaxAmount){var add=Math.Min(remaining,slot.MaxAmount-slot.Amount);slot.Durability=InvItemClass.getStackedDurability(ToItemClass(slot),source,add);slot.Amount+=add;remaining-=add;if(remaining==0)return;}
        foreach(var slot in AllSlots())if(string.IsNullOrEmpty(slot.Type)){slot.Type=source.type;slot.Stackable=source.baseClass.stackable;slot.MaxAmount=Math.Max(1,source.baseClass.maxAmount);slot.Amount=source.baseClass.stackable?Math.Min(remaining,slot.MaxAmount):1;slot.Durability=source.durability;slot.Quality=(int)source.modifierQuality;slot.Recipe=source.isRecipe;remaining-=slot.Amount;if(remaining==0)return;}
    }

    public bool TryPeek(bool fromHotbar,int slotIndex,int requestedAmount,out Item item)
    {
        var source=fromHotbar?hotbar:backpack;
        if(slotIndex<0||slotIndex>=source.Count||string.IsNullOrEmpty(source[slotIndex].Type)||source[slotIndex].Amount<=0){item=default;return false;}
        var slot=source[slotIndex];var amount=requestedAmount<0?slot.Amount:Math.Min(Math.Max(1,requestedAmount),slot.Amount);
        item=new Item(slot.Type,amount,slot.Durability,slot.Quality,slot.Recipe,slot.MaxAmount,slot.Stackable);return true;
    }

    public bool Remove(bool fromHotbar,int slotIndex,int amount)
    {
        var source=fromHotbar?hotbar:backpack;
        if(slotIndex<0||slotIndex>=source.Count||amount<=0||source[slotIndex].Amount<amount)return false;
        var slot=source[slotIndex];slot.Amount-=amount;if(slot.Amount==0)Clear(slot);return true;
    }

    /// <summary>Drains weapon durability after an accepted attack. A weapon reduced to zero breaks and is removed. Returns the remaining durability, or -1 when the slot is invalid.</summary>
    public float DrainDurability(bool fromHotbar,int slotIndex,float amount)
    {
        var source=fromHotbar?hotbar:backpack;
        if(slotIndex<0||slotIndex>=source.Count||amount<0||source[slotIndex].Amount<=0)return -1f;
        var slot=source[slotIndex];
        slot.Durability-=amount;
        if(slot.Durability<=0f){Clear(slot);return 0f;}
        return slot.Durability;
    }

    public bool CanSwap(bool fromHotbar,int slotIndex,Item expectedSource)
    {
        var source=fromHotbar?hotbar:backpack;
        if(slotIndex<0||slotIndex>=source.Count)return false;
        var slot=source[slotIndex];
        return slot.Type==expectedSource.Type&&slot.Amount==expectedSource.Amount&&slot.Quality==expectedSource.Quality&&slot.Recipe==expectedSource.Recipe;
    }

    public void Swap(bool fromHotbar,int slotIndex,Item replacement)
    {
        var source=fromHotbar?hotbar:backpack;
        if(slotIndex<0||slotIndex>=source.Count)throw new InvalidOperationException("Remote inventory source slot is invalid.");
        var slot=source[slotIndex];slot.Type=replacement.Type;slot.Amount=replacement.Amount;slot.Durability=replacement.Durability;slot.Quality=replacement.Quality;slot.Recipe=replacement.Recipe;slot.MaxAmount=Math.Max(1,replacement.MaxAmount);slot.Stackable=replacement.Stackable;
    }

    public PlayerInventoryStatePayload CaptureState()=>new PlayerInventoryStatePayload(ToWire(backpack),ToWire(hotbar));

    private IEnumerable<Slot> AllSlots(){foreach(var slot in backpack)yield return slot;foreach(var slot in hotbar)yield return slot;}
    private static InvItemClass ToItemClass(Slot slot)=>new InvItemClass(slot.Type,slot.Durability,slot.Amount,(InvItem.ModifierQuality)slot.Quality,slot.Recipe);
    private static void Clear(Slot slot){slot.Type=string.Empty;slot.Amount=0;slot.Durability=0;slot.Quality=0;slot.Recipe=false;slot.MaxAmount=1;slot.Stackable=false;}
    private static InventorySlotWire[] ToWire(List<Slot> source){var result=new InventorySlotWire[source.Count];for(var i=0;i<source.Count;i++){var s=source[i];result[i]=new InventorySlotWire(s.Type,s.Amount,s.Durability,s.Quality,s.Recipe);}return result;}

    private void Capture(Inventory inventory,List<Slot> destination)
    {
        if(inventory?.slots==null)return;
        foreach(var original in inventory.slots)
        {
            var item=original?.invItem;
            var baseClass=item?.baseClass;
            if(InvItemClass.isNull(item)||item==null||baseClass==null)destination.Add(new Slot());
            else destination.Add(new Slot{Type=item.type,Amount=item.amount,Durability=item.durability,Quality=(int)item.modifierQuality,Recipe=item.isRecipe,MaxAmount=Math.Max(1,baseClass.maxAmount),Stackable=baseClass.stackable});
        }
    }
}
