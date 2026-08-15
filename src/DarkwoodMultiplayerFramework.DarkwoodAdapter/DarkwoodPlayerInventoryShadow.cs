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

    public static DarkwoodPlayerInventoryShadow FromRecord(GuestProfileRecord record, Action<string>? warn = null)
    {
        var shadow = new DarkwoodPlayerInventoryShadow();
        Restore(record.Backpack, shadow.backpack, warn);
        Restore(record.Hotbar, shadow.hotbar, warn);
        return shadow;
    }

    /// <summary>Grants config starter items. Invalid or unknown item types are skipped with a warning.</summary>
    public void AddStarterKit(IReadOnlyList<GuestStarterEntry>? kit, Action<string>? warn = null)
    {
        if (kit == null) return;
        foreach (var entry in kit)
        {
            try
            {
                var item = new InvItemClass(entry.Type, 100f, entry.Amount, (InvItem.ModifierQuality)0, false);
                if (item == null || item.baseClass == null) throw new InvalidOperationException("未知物品类型");
                if (!CanAdd(item)) { warn?.Invoke($"访客初始装备已满，跳过 {entry.Type}"); continue; }
                Add(item);
            }
            catch (Exception error) { warn?.Invoke($"访客初始装备无效，跳过 {entry.Type}：" + error.Message); }
        }
    }

    private static void Restore(InventorySlotWire[] wire, List<Slot> destination, Action<string>? warn)
    {
        foreach (var s in wire)
        {
            if (string.IsNullOrEmpty(s.Type) || s.Amount <= 0) { destination.Add(new Slot()); continue; }
            try
            {
                var item = new InvItemClass(s.Type, s.Durability, s.Amount, (InvItem.ModifierQuality)s.Quality, s.Recipe);
                var baseClass = item?.baseClass;
                if (baseClass == null) throw new InvalidOperationException("未知物品类型");
                destination.Add(new Slot { Type = s.Type, Amount = s.Amount, Durability = s.Durability, Quality = s.Quality, Recipe = s.Recipe, MaxAmount = Math.Max(1, baseClass.maxAmount), Stackable = baseClass.stackable });
            }
            catch (Exception error)
            {
                warn?.Invoke($"跳过无法恢复的物品 {s.Type}：" + error.Message);
                destination.Add(new Slot());
            }
        }
    }

    public bool CanAdd(InvItemClass source)
    {
        // FIX-011 信任模式：影子库存只是主机侧的校验辅助，客户端实际背包槽位
        // 可能比影子记录更多。除非法输入外一律允许；Add 放不下时会动态扩槽。
        return !InvItemClass.isNull(source)&&source.amount>0&&source.baseClass!=null;
    }

    public void Add(InvItemClass source)
    {
        if(!CanAdd(source)||source.baseClass==null)throw new InvalidOperationException("Remote inventory has no capacity.");var remaining=source.amount;
        if(source.baseClass.stackable)foreach(var slot in AllSlots())if(slot.Type==source.type&&slot.Stackable&&slot.Amount<slot.MaxAmount){var add=Math.Min(remaining,slot.MaxAmount-slot.Amount);slot.Durability=InvItemClass.getStackedDurability(ToItemClass(slot),source,add);slot.Amount+=add;remaining-=add;if(remaining==0)return;}
        foreach(var slot in AllSlots())if(string.IsNullOrEmpty(slot.Type)){slot.Type=source.type;slot.Stackable=source.baseClass.stackable;slot.MaxAmount=Math.Max(1,source.baseClass.maxAmount);slot.Amount=source.baseClass.stackable?Math.Min(remaining,slot.MaxAmount):1;slot.Durability=source.durability;slot.Quality=(int)source.modifierQuality;slot.Recipe=source.isRecipe;remaining-=slot.Amount;if(remaining==0)return;}
        // FIX-011：现有槽位放不下时动态追加空槽（客户端实际背包容量通常大于影子记录）。
        while(remaining>0)
        {
            var slot=new Slot();backpack.Add(slot);
            slot.Type=source.type;slot.Stackable=source.baseClass.stackable;slot.MaxAmount=Math.Max(1,source.baseClass.maxAmount);slot.Amount=source.baseClass.stackable?Math.Min(remaining,slot.MaxAmount):1;slot.Durability=source.durability;slot.Quality=(int)source.modifierQuality;slot.Recipe=source.isRecipe;remaining-=slot.Amount;
        }
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
}
