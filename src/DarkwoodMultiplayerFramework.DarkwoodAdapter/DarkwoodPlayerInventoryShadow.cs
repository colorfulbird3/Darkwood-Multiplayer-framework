using System;
using System.Collections.Generic;

namespace DarkwoodMultiplayerFramework.DarkwoodAdapter;

/// <summary>Host-owned inventory capacity model for a remote player.</summary>
internal sealed class DarkwoodPlayerInventoryShadow
{
    private sealed class Slot { public string Type=string.Empty; public int Amount; public int MaxAmount=1; public bool Stackable; }
    private readonly List<Slot> slots=new List<Slot>();

    public static DarkwoodPlayerInventoryShadow CaptureInitial()
    {
        var shadow=new DarkwoodPlayerInventoryShadow();var player=Player.Instance;if(player==null)return shadow;
        shadow.Capture(player.Inventory);shadow.Capture(player.Hotbar);return shadow;
    }

    public bool CanAdd(InvItemClass source)
    {
        if(InvItemClass.isNull(source)||source.amount<=0||source.baseClass==null)return false;var remaining=source.amount;
        if(source.baseClass.stackable)foreach(var slot in slots)if(slot.Type==source.type&&slot.Stackable){remaining-=Math.Max(0,slot.MaxAmount-slot.Amount);if(remaining<=0)return true;}
        foreach(var slot in slots)if(string.IsNullOrEmpty(slot.Type)){remaining-=source.baseClass.stackable?Math.Max(1,source.baseClass.maxAmount):1;if(remaining<=0)return true;}
        return false;
    }

    public void Add(InvItemClass source)
    {
        if(!CanAdd(source)||source.baseClass==null)throw new InvalidOperationException("Remote inventory has no capacity.");var remaining=source.amount;
        if(source.baseClass.stackable)foreach(var slot in slots)if(slot.Type==source.type&&slot.Stackable&&slot.Amount<slot.MaxAmount){var add=Math.Min(remaining,slot.MaxAmount-slot.Amount);slot.Amount+=add;remaining-=add;if(remaining==0)return;}
        foreach(var slot in slots)if(string.IsNullOrEmpty(slot.Type)){slot.Type=source.type;slot.Stackable=source.baseClass.stackable;slot.MaxAmount=Math.Max(1,source.baseClass.maxAmount);slot.Amount=source.baseClass.stackable?Math.Min(remaining,slot.MaxAmount):1;remaining-=slot.Amount;if(remaining==0)return;}
    }

    private void Capture(Inventory inventory)
    {
        if(inventory?.slots==null)return;
        foreach(var original in inventory.slots)
        {
            var item=original?.invItem;
            var baseClass=item?.baseClass;
            if(InvItemClass.isNull(item)||item==null||baseClass==null)slots.Add(new Slot());
            else slots.Add(new Slot{Type=item.type,Amount=item.amount,MaxAmount=Math.Max(1,baseClass.maxAmount),Stackable=baseClass.stackable});
        }
    }
}
