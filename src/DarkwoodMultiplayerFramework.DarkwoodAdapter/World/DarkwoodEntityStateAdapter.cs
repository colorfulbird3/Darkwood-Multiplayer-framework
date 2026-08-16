using System;
using System.Collections.Generic;
using DarkwoodMultiplayerFramework.Core;
using DarkwoodMultiplayerFramework.Protocol;
using UnityEngine;

namespace DarkwoodMultiplayerFramework.DarkwoodAdapter;

/// <summary>
/// 0.8.9 所有权拆分：Darkwood 游戏对象 ↔ 网络状态 的转换适配器。
/// 只回答"Darkwood Character/Door/Window/Item/Inventory 有哪些字段、怎么读写"；
/// 何时 Capture、哪些变化、Revision、Target、插值 由 DarkwoodEntityReplication 负责。
/// </summary>
public static class DarkwoodEntityStateAdapter
{
    public static EntityStateWire Capture(EntityId id, Component c, ulong rev)
    {
        var p = c.transform.position; var q = c.transform.rotation;
        float h = 0; int a = 0, b = 0; byte f = 0; string anim = ""; int frame = 0; byte kind = Kind(c);
        if (c is Character ch)
        {
            h = ch.health; f = Flags(ch.alive, ch.gameObject.activeSelf, ch.attacking, ch.walking, ch.running);
            if (ch.animator != null && ch.animator.CurrentClip != null) { anim = ch.animator.CurrentClip.name; frame = ch.animator.CurrentFrame; }
        }
        else if (c is Door d)
        {
            h = d.health; a = d.barricadeHealth; b = d.barricadeState; f = Flags(d.opened, d.barricaded, d.destroyed, d.blocked, d.gameObject.activeSelf);
            if (d.body != null) { p = d.body.position; q = d.body.rotation; }
        }
        else if (c is Window w)
        {
            h = w.barricadeHealth; a = w.barricadeState; f = Flags(w.barricaded, w.blocked, w.gameObject.activeSelf, false);
        }
        else if (c is Item item)
        {
            h = item.health; a = item.invItemAmount; f = Flags(item.destroyed, item.isOn, item.hasPower, item.searched, item.gameObject.activeSelf);
        }
        return new EntityStateWire(id.Value, id.IsPersistent, kind, p.x, p.y, p.z, q.x, q.y, q.z, q.w, h, a, b, f, anim, frame, rev);
    }

    /// <summary>应用远端状态到本地组件。frozen/deadCharacters 由调用方（复制管理器）维护。</summary>
    public static void Apply(Component c, EntityStateWire s, bool immediate, HashSet<Character> frozen, HashSet<Character> deadCharacters)
    {
        var p = new Vector3(s.X, s.Y, s.Z); var q = new Quaternion(s.Qx, s.Qy, s.Qz, s.Qw);
        if (c is Character ch)
        {
            // Authoritative death mirror: when the host reports alive 1→0, let the game
            // turn the local copy into a corpse (die2, not die(): no onDeath story trigger
            // on the client). The corpse inventory is then corrected by the host's
            // authoritative InventoryState broadcast.
            if (ch.alive && !Flag(s.Flags, 0) && !deadCharacters.Contains(ch)) { deadCharacters.Add(ch); frozen.Remove(ch); ch.enabled = true; try { ch.die2(); } catch (Exception) { ch.gameObject.SetActive(false); } if (ch.gameObject.activeSelf) ch.enabled = false; }
            if (!deadCharacters.Contains(ch) && frozen.Add(ch)) { ch.enabled = false; if (ch.AIpath != null) ch.AIpath.enabled = false; }
            ch.health = s.Health; ch.Health = s.Health; ch.alive = Flag(s.Flags, 0); ch.walking = Flag(s.Flags, 3); ch.running = Flag(s.Flags, 4); ch.gameObject.SetActive(Flag(s.Flags, 1)); if (immediate) { ch.transform.position = p; ch.transform.rotation = q; }
        }
        else if (c is Door d)
        {
            d.opened = Flag(s.Flags, 0); d.barricaded = Flag(s.Flags, 1); d.destroyed = Flag(s.Flags, 2); d.blocked = Flag(s.Flags, 3); d.health = Mathf.RoundToInt(s.Health); d.barricadeHealth = s.StateA; d.barricadeState = s.StateB; if (d.body != null) { d.body.position = p; d.body.rotation = q; } d.gameObject.SetActive(Flag(s.Flags, 4));
        }
        else if (c is Window w)
        {
            w.barricaded = Flag(s.Flags, 0); w.blocked = Flag(s.Flags, 1); w.barricadeHealth = Mathf.RoundToInt(s.Health); w.barricadeState = s.StateA; w.gameObject.SetActive(Flag(s.Flags, 2));
        }
        else if (c is Item item)
        {
            item.destroyed = Flag(s.Flags, 0); item.health = Mathf.RoundToInt(s.Health); item.invItemAmount = s.StateA; item.isOn = Flag(s.Flags, 1); item.hasPower = Flag(s.Flags, 2); item.searched = Flag(s.Flags, 3); if (immediate) { item.transform.position = p; item.transform.rotation = q; } item.gameObject.SetActive(Flag(s.Flags, 4));
        }
    }

    public static InventoryStateMessage CaptureInventory(EntityId id, Inventory inventory, ulong rev)
    {
        var slots = DarkwoodInventoryAdapter.Capture(inventory);
        var wire = new InventorySlotWire[slots.Length];
        for (var i = 0; i < slots.Length; i++) wire[i] = new InventorySlotWire(slots[i].Type, slots[i].Amount, slots[i].Durability, slots[i].Quality, slots[i].Recipe);
        var p = inventory.transform.position;
        return new InventoryStateMessage(id.Value, id.IsPersistent, rev, inventory.name, p.x, p.y, p.z, (int)inventory.invType, wire);
    }

    public static DarkwoodInventorySlot[] ToDarkwoodSlots(InventorySlotWire[] slots)
    {
        var result = new DarkwoodInventorySlot[slots.Length];
        for (var i = 0; i < slots.Length; i++) { var s = slots[i]; result[i] = new DarkwoodInventorySlot { Type = s.Type, Amount = s.Amount, Durability = s.Durability, Quality = s.Quality, Recipe = s.Recipe }; }
        return result;
    }

    public static bool IsShared(Inventory inventory) => inventory.invType == Inventory.InvType.itemInv || inventory.invType == Inventory.InvType.deathDrop;

    public static bool SlotsEqual(InventorySlotWire[] a, InventorySlotWire[] b)
    {
        if (a.Length != b.Length) return false;
        for (var i = 0; i < a.Length; i++) if (a[i].Type != b[i].Type || a[i].Amount != b[i].Amount || Math.Abs(a[i].Durability - b[i].Durability) > .001f || a[i].Quality != b[i].Quality || a[i].Recipe != b[i].Recipe) return false;
        return true;
    }

    public static byte Kind(Component c) => c is Character ? (byte)1 : c is Door ? (byte)2 : c is Window ? (byte)3 : c is Item ? (byte)4 : c is Inventory ? (byte)5 : (byte)0;

    public static byte Flags(bool a, bool b, bool c, bool d, bool e = false) => (byte)((a ? 1 : 0) | (b ? 2 : 0) | (c ? 4 : 0) | (d ? 8 : 0) | (e ? 16 : 0));
    public static bool Flag(byte f, int bit) => (f & (1 << bit)) != 0;
}
