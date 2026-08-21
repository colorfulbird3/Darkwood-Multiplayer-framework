using System;
using System.Collections.Generic;
using DarkwoodMultiplayerFramework.Protocol;

namespace DarkwoodMultiplayerFramework.DarkwoodAdapter;

/// <summary>Host-owned, slot-exact inventory model for a remote player.</summary>
public sealed class DarkwoodPlayerInventoryShadow
{
    public readonly struct Item
    {
        public Item(string type,int amount,float durability,int quality,bool recipe,int maxAmount,bool stackable)
        {Type=type;Amount=amount;Durability=durability;Quality=quality;Recipe=recipe;MaxAmount=maxAmount;Stackable=stackable;}
        public string Type {get;} public int Amount {get;} public float Durability {get;} public int Quality {get;} public bool Recipe {get;} public int MaxAmount {get;} public bool Stackable {get;}
    }
    internal sealed class Slot { public string Type=string.Empty; public int Amount; public float Durability; public int Quality; public bool Recipe; public int MaxAmount=1; public bool Stackable; }
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
        var slot=source[slotIndex];slot.Amount-=amount;if(slot.Amount==0)Clear(slot);
        Touch(); // P0-Authority-Drift：权威修改 → revision++（客户端旧包门）
        return true;
    }

    public enum HeldPlaceResult : byte { Placed, Stacked, Occupied, Invalid }
    /// <summary>客户端最近一次上报的真实背包/快捷栏容量（P0-C：阴影槽拓扑必须与客户端 UI 对齐）。0 = 尚未上报。</summary>
    public int BackpackCapacity;
    public int HotbarCapacity;
    /// <summary>诊断用：影子当前实际槽数。</summary>
    public int BackpackCountOut => backpack.Count;
    public int HotbarCountOut => hotbar.Count;
    /// <summary>P0-C：阴影槽拓扑必须与客户端 UI 对齐；TopologyReady 为物品交互强门。</summary>
    public bool TopologyReady;
    /// <summary>P0-Authority-Drift：权威背包版本号。Host 每次修改影子（place/remove/rebuild/topology）递增；客户端据其拒绝旧包。</summary>
    public int Revision;
    internal void Touch()=>Revision++;
    /// <summary>P0-3：原版 InvSlot.placeItem 语义按目标槽放置：空 → place；同类可堆叠 → stack（仅当能完整放入）；占用 → Occupied。</summary>
    public HeldPlaceResult PlaceAt(bool fromHotbar,int slotIndex,InvItemClass source)
    {
        var capacity = fromHotbar ? HotbarCapacity : BackpackCapacity;
        if(source==null||InvItemClass.isNull(source)||source.baseClass==null)return HeldPlaceResult.Invalid;
        if(capacity>0&&(slotIndex<0||slotIndex>=capacity))return HeldPlaceResult.Invalid;
        var targetList = fromHotbar ? hotbar : backpack;
        var count = targetList.Count;
        // P0-C：只扩到客户端上报的真实容量；绝不无限扩到任意网络输入索引。
        var limit = capacity>0?capacity:count;
        if(slotIndex<0)return HeldPlaceResult.Invalid;
        if(slotIndex>=limit)return HeldPlaceResult.Invalid;
        while (targetList.Count <= slotIndex) targetList.Add(new Slot());
        var slot=targetList[slotIndex];
        if(string.IsNullOrEmpty(slot.Type))
        {
            slot.Type=source.type;slot.Stackable=source.baseClass.stackable;slot.MaxAmount=Math.Max(1,source.baseClass.maxAmount);
            slot.Amount=source.baseClass.stackable?Math.Min(source.amount,slot.MaxAmount):1;slot.Durability=source.durability;slot.Quality=(int)source.modifierQuality;slot.Recipe=source.isRecipe;
            Touch(); // P0-Authority-Drift
            return HeldPlaceResult.Placed;
        }
        if(slot.Stackable&&slot.Type==source.type&&slot.Amount<slot.MaxAmount)
        {
            // P1-B：partial stack 绝不吞 Held remainder —— 只有能完整放入才 Stacked，否则 Occupied（Held 保留）。
            var space=slot.MaxAmount-slot.Amount;
            if(source.amount>space)return HeldPlaceResult.Occupied;
            slot.Durability=InvItemClass.getStackedDurability(ToItemClass(slot),source,source.amount);
            slot.Amount+=source.amount;
            Touch(); // P0-Authority-Drift
            return HeldPlaceResult.Stacked;
        }
        return HeldPlaceResult.Occupied;
    }

    /// <summary>P0-2：bootstrap 门关闭时只更新客户端上报的真实容量（不改内容）——shadow 拓扑对齐客户端 UI，但绝不被其旧存档/空背包内容污染。</summary>
    public void RefreshTopology(int backpackCapacity, int hotbarCapacity)
    {
        BackpackCapacity = backpackCapacity;
        HotbarCapacity = hotbarCapacity;
        if (BackpackCapacity > 0 || HotbarCapacity > 0) TopologyReady = true;
        Touch();
    }

    /// <summary>客户端上报真实背包后整体重建（本地合成/搜尸体等漂移收敛）。wire 含全部空槽 → 长度即真实容量。</summary>
    public void Rebuild(InventorySlotWire[] backpackWire, InventorySlotWire[] hotbarWire, Action<string>? warn = null)
    {
        backpack.Clear();
        hotbar.Clear();
        BackpackCapacity = backpackWire?.Length ?? 0;
        HotbarCapacity = hotbarWire?.Length ?? 0;
        Restore(backpackWire, backpack, warn);
        Restore(hotbarWire, hotbar, warn);
        TopologyReady = BackpackCapacity > 0 || HotbarCapacity > 0;
        Touch(); // P0-Authority-Drift：重建也是权威修改
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

    /// <summary>阶段三：WorldDroppedItem 拾取直接进背包（原版 transfer 语义）——先查能否容纳（堆叠+空槽），能则安置。</summary>
    public bool CanFit(InvItemClass source)
    {
        if(source==null||InvItemClass.isNull(source)||source.baseClass==null||source.amount<=0)return false;
        var remaining=source.amount;
        foreach(var s in AllSlots())
        {
            if(remaining<=0)break;
            if(s.Stackable&&s.Type==source.type&&s.Amount>0&&s.Amount<s.MaxAmount)remaining-=s.MaxAmount-s.Amount;
            else if(s.Amount<=0)remaining=0;
        }
        return remaining<=0;
    }
    public bool AddItem(InvItemClass source)
    {
        if(source==null||InvItemClass.isNull(source)||source.baseClass==null||source.amount<=0)return false;
        var remaining=source.amount;
        foreach(var s in AllSlots())
        {
            if(remaining<=0)break;
            if(s.Stackable&&s.Type==source.type&&s.Amount>0&&s.Amount<s.MaxAmount){var add=Math.Min(remaining,s.MaxAmount-s.Amount);s.Amount+=add;remaining-=add;}
        }
        if(remaining>0)foreach(var s in AllSlots())
        {
            if(remaining<=0)break;
            if(s.Amount<=0)
            {
                s.Type=source.type;s.Stackable=source.baseClass.stackable;s.MaxAmount=Math.Max(1,source.baseClass.maxAmount);
                s.Amount=source.baseClass.stackable?Math.Min(remaining,s.MaxAmount):1;
                s.Durability=source.durability;s.Quality=(int)source.modifierQuality;s.Recipe=source.isRecipe;
                remaining-=s.Amount;
            }
        }
        if(remaining>0)return false;
        Touch(); // P0-Authority-Drift：拾取入包也是权威修改
        return true;
    }

    public PlayerInventoryStatePayload CaptureState(int playerId=0)=>new PlayerInventoryStatePayload(ToWire(backpack),ToWire(hotbar),Revision,playerId);

    private IEnumerable<Slot> AllSlots(){foreach(var slot in backpack)yield return slot;foreach(var slot in hotbar)yield return slot;}
    private static InvItemClass ToItemClass(Slot slot)=>new InvItemClass(slot.Type,slot.Durability,slot.Amount,(InvItem.ModifierQuality)slot.Quality,slot.Recipe);
    private static void Clear(Slot slot){slot.Type=string.Empty;slot.Amount=0;slot.Durability=0;slot.Quality=0;slot.Recipe=false;slot.MaxAmount=1;slot.Stackable=false;}
    internal static InventorySlotWire[] ToWire(List<Slot> source){var result=new InventorySlotWire[source.Count];for(var i=0;i<source.Count;i++){var s=source[i];result[i]=new InventorySlotWire(s.Type,s.Amount,s.Durability,s.Quality,s.Recipe);}return result;}
}
