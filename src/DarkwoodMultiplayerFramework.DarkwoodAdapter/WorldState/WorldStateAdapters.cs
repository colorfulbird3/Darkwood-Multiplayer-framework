using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

namespace DarkwoodMultiplayerFramework.DarkwoodAdapter.World;

/// <summary>把 Item 的完整已知业务状态打包成 typed payload；Apply 幂等（禁止 switchMe toggle 语义）。
/// 视觉刷新优先靠原版 Item.Update 读 isOn 等状态；trap 等特殊追加在 BearTrapStateAdapter。</summary>
public sealed class GenericItemStateAdapter : IWorldStateAdapter
{
    public ushort SchemaId => WorldStateSchemas.GenericItem;
    public bool CanHandle(Component component) => component is not Character && component is not Door && component is not Window && component is Item;
    public byte[] Capture(Component component)
    {
        var item = (Item)component;
        using var s = new MemoryStream(); using var w = new BinaryWriter(s);
        w.Write(item.destroyed); w.Write(item.isOn); w.Write(item.hasPower); w.Write(item.searched);
        w.Write(item.health); w.Write(item.invItemAmount); w.Write(item.enabled);
        return s.ToArray();
    }
    public bool HasChanged(byte[] o, byte[] n) { if (o == null || n == null || o.Length != n.Length) return true; for (var i = 0; i < o.Length; i++) if (o[i] != n[i]) return true; return false; }
    public void Apply(Component component, byte[] state)
    {
        if (state == null || state.Length < 7 || component is not Item item) return;
        using var r = new BinaryReader(new MemoryStream(state));
        var destroyed = r.ReadBoolean(); var isOn = r.ReadBoolean(); var hasPower = r.ReadBoolean(); var searched = r.ReadBoolean();
        item.destroyed = destroyed; item.hasPower = hasPower; item.searched = searched;
        item.health = Mathf.RoundToInt(r.ReadSingle()); item.invItemAmount = r.ReadInt32(); item.enabled = r.ReadBoolean();
        // 幂等 isOn：绝不调用 switchMe()（toggle 语义）；直接赋值 + 让原版 Item.Update 读状态驱动视觉。
        if (item.isOn != isOn) item.isOn = isOn;
    }
    public void EnterClientProxyMode(Component component) { }
    public void ExitClientProxyMode(Component component) { }
}

/// <summary>BearTrap 专用：在 GenericItem 基础上，先保证 isOn/destroyed 幂等 + 不触发 switchMe。
/// 真实字段（armed/triggered/occupied 及视觉组件）待 [WORLD-AUDIT] 反编译确认后扩展。</summary>
public sealed class BearTrapStateAdapter : IWorldStateAdapter
{
    private const string MarkerName = "beartrap";
    public ushort SchemaId => WorldStateSchemas.BearTrap;
    public bool CanHandle(Component component) => component is Item item && item.name.ToLowerInvariant().Contains(MarkerName);
    // 捕兽夹状态：isOn(armed) / destroyed / 目标是否被夹住（用 traps 上的 Scene 内 Character.inBearTrap 间接，但这里先打包 Item 侧）
    public byte[] Capture(Component component)
    {
        var item = (Item)component;
        using var s = new MemoryStream(); using var w = new BinaryWriter(s);
        w.Write(item.isOn); w.Write(item.destroyed); w.Write(item.health);
        return s.ToArray();
    }
    public bool HasChanged(byte[] o, byte[] n) { if (o == null || n == null || o.Length != n.Length) return true; for (var i = 0; i < o.Length; i++) if (o[i] != n[i]) return true; return false; }
    public void Apply(Component component, byte[] state)
    {
        if (state == null || state.Length < 6 || component is not Item item) return;
        using var r = new BinaryReader(new MemoryStream(state));
        var armed = r.ReadBoolean(); var destroyed = r.ReadBoolean();
        var health = r.ReadSingle();
        item.health = Mathf.RoundToInt(health);
        // 幂等赋值：绝不 toggle（switchMe 会翻转视觉造成两端反复）。
        if (item.isOn != armed) item.isOn = armed;
        if (item.destroyed != destroyed) item.destroyed = destroyed;
        if (item.gameObject.activeSelf != !destroyed) item.gameObject.SetActive(!destroyed);
        // 阶段二：TriggerBlocker（踩踏碰撞/下陷视觉）跟随 armed&&!destroyed 幂等启停。
        var blocker = item.GetComponent<TriggerBlocker>();
        if (blocker != null && blocker.enabled != (armed && !destroyed)) blocker.enabled = armed && !destroyed;
        DarkwoodAdapterRuntime.LogMessage($"[BEARTRAP] id={item.name} armed={armed} triggered={(destroyed ? "n/a" : item.isOn.ToString())} broken={destroyed} source=Host");
    }
    public void EnterClientProxyMode(Component component) { }
    public void ExitClientProxyMode(Component component) { }
}

/// <summary>Door typed：状态已由 legacy EntityStateWire（Flags: opened/barricaded/destroyed/blocked + health/StateA/StateB）同步，
/// typed payload 无额外字段（返回空）。保留 dedicated adapter 以便归类/未来扩展 + 幂等字段确认。</summary>
public sealed class DoorStateAdapter : IWorldStateAdapter
{
    public ushort SchemaId => WorldStateSchemas.Door;
    public bool CanHandle(Component component) => component is Door;
    public byte[] Capture(Component component) => Array.Empty<byte>();
    public bool HasChanged(byte[] o, byte[] n) => false;
    public void Apply(Component component, byte[] state) { }
    public void EnterClientProxyMode(Component component) { }
    public void ExitClientProxyMode(Component component) { }
}

/// <summary>Window typed：同 Door——状态由 legacy 同步（Flags/StateA/B），无额外 typed payload。</summary>
public sealed class WindowStateAdapter : IWorldStateAdapter
{
    public ushort SchemaId => WorldStateSchemas.Window;
    public bool CanHandle(Component component) => component is Window;
    public byte[] Capture(Component component) => Array.Empty<byte>();
    public bool HasChanged(byte[] o, byte[] n) => false;
    public void Apply(Component component, byte[] state) { }
    public void EnterClientProxyMode(Component component) { }
    public void ExitClientProxyMode(Component component) { }
}

/// <summary>
/// Character typed：状态走 legacy（animation/flags/transform 已够大部分）；此 adapter 的核心职责是
/// EnterClientProxyMode —— 客户端把 bound 非玩家 Character 修成纯视觉代理：
/// 关闭所有"simulation owner"MonoBehaviour（AI/寻路/决策/攻击/传感器/移动），只保留渲染/动画/必要碰撞；
/// Rigidbody → kinematic。Host 仍是唯一 AI authority。
/// </summary>
public sealed class CharacterStateAdapter : IWorldStateAdapter
{
    // 保留给视觉/交互的组件类型（其它 MonoBehaviour 视为 simulation owner 关闭）。
    private static readonly HashSet<string> VisualComponents = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "Renderer", "SpriteRenderer", "tk2dSprite", "tk2dSpriteAnimator", "Transform", "Collider2D",
        "BoxCollider2D", "CircleCollider2D", "PolygonCollider2D", "CapsuleCollider2D",
        "Rigidbody2D", "Animator", "Character", "CharBase", "Light", "AudioSource", "ParticleSystem",
    };
    public ushort SchemaId => WorldStateSchemas.Character;
    public bool CanHandle(Component component) => component is Character && component is not null && component.GetType() != typeof(Player);
    public byte[] Capture(Component component) => Array.Empty<byte>(); // legacy 已足够，无附加 typed 状态
    public bool HasChanged(byte[] o, byte[] n) => false;
    public void Apply(Component component, byte[] state) { }
    public void EnterClientProxyMode(Component component)
    {
        if (component is not Character ch) return;
        try
        {
            ch.enabled = false; if (ch.AIpath != null) ch.AIpath.enabled = false;
            // AIpath burst：pathfinding 相关脚本（AstarPath 的路径请求由 AI 组件驱动，已关）。
            DisableSimulationBehaviours(ch);
            FreezeRigidbodies(ch);
        }
        catch (Exception) { }
    }
    public void ExitClientProxyMode(Component component)
    {
        if (component is not Character ch) return;
        try { ch.enabled = true; if (ch.AIpath != null) ch.AIpath.enabled = true; } catch (Exception) { }
    }
    private static void DisableSimulationBehaviours(Character ch)
    {
        if (ch == null) return;
        var root = ch.transform;
        foreach (var mb in root.GetComponentsInChildren<MonoBehaviour>(true))
        {
            if (mb == null) continue;
            var t = mb.GetType().Name;
            if (VisualComponents.Contains(t) || (mb is Character chr && ReferenceEquals(chr, ch))) continue;
            try { mb.enabled = false; } catch (Exception) { }
        }
    }
    private static void FreezeRigidbodies(Character ch)
    {
        if (ch == null) return;
        foreach (var rb in ch.GetComponentsInChildren<Rigidbody>(true))
            try { rb.isKinematic = true; rb.velocity = Vector3.zero; rb.angularVelocity = Vector3.zero; } catch (Exception) { }
    }
}

// ── 阶段二：StatefulObjectSync（同步 State，不同步 GameObject）──

/// <summary>Generator typed 状态：isOn/running、fuel（Host-only drain）、lowPower。发电机带 Item（item.isOn 同步本体视觉）。</summary>
public sealed class GeneratorStateAdapter : IWorldStateAdapter
{
    public ushort SchemaId => WorldStateSchemas.Generator;
    public bool CanHandle(Component component) => component is Generator;
    public byte[] Capture(Component component)
    {
        var g = (Generator)component;
        using var s = new MemoryStream(); using var w = new BinaryWriter(s);
        w.Write(g.isOn); w.Write(g.fuel); w.Write(g.lowPower);
        return s.ToArray();
    }
    public bool HasChanged(byte[] o, byte[] n) { if (o == null || n == null || o.Length != n.Length) return true; for (var i = 0; i < o.Length; i++) if (o[i] != n[i]) return true; return false; }
    public void Apply(Component component, byte[] state)
    {
        if (state == null || state.Length < 6 || component is not Generator g) return;
        using var r = new BinaryReader(new MemoryStream(state));
        var isOn = r.ReadBoolean(); var fuel = r.ReadSingle(); var lowPower = r.ReadBoolean();
        bool changed = g.isOn != isOn || Math.Abs(g.fuel - fuel) > 0.01f || g.lowPower != lowPower;
        // 幂等赋值（禁止 toggle）；fuel 为 Host 权威值（客户端绝不 drain）。
        if (g.isOn != isOn) g.isOn = isOn;
        g.fuel = fuel;
        if (g.lowPower != lowPower) g.lowPower = lowPower;
        var item = g.GetComponent<Item>();
        if (item != null && item.isOn != isOn) item.isOn = isOn;
        if (changed) DarkwoodAdapterRuntime.LogMessage($"[GENERATOR] id={component.gameObject.name} running={isOn} fuel={fuel:F0} powered={!lowPower} source=Host");
    }
    public void EnterClientProxyMode(Component component) { /* 客户端仅视觉代理：fuel 由 Host 权威广播，绝不本地 drain */ }
    public void ExitClientProxyMode(Component component) { }
}

/// <summary>Light2D typed 状态：on（light.enabled）、目标亮度、poweringDown/lowPower。灯属于电源网络，由 Host 原版 restorePower 驱动后捕获。</summary>
public sealed class LightStateAdapter : IWorldStateAdapter
{
    public ushort SchemaId => WorldStateSchemas.Light;
    public bool CanHandle(Component component) => component is ItemLight;
    public byte[] Capture(Component component)
    {
        var l = (ItemLight)component;
        using var s = new MemoryStream(); using var w = new BinaryWriter(s);
        bool on = l.light != null && l.light.enabled;
        w.Write(on); w.Write(l.destLightIntensity); w.Write(l.poweringDown); w.Write(l.lowPower);
        return s.ToArray();
    }
    public bool HasChanged(byte[] o, byte[] n) { if (o == null || n == null || o.Length != n.Length) return true; for (var i = 0; i < o.Length; i++) if (o[i] != n[i]) return true; return false; }
    public void Apply(Component component, byte[] state)
    {
        if (state == null || state.Length < 6 || component is not ItemLight l) return;
        using var r = new BinaryReader(new MemoryStream(state));
        var on = r.ReadBoolean(); var dest = r.ReadSingle(); var poweringDown = r.ReadBoolean(); var lowPower = r.ReadBoolean();
        bool changed = (l.light != null && l.light.enabled != on) || Math.Abs(l.destLightIntensity - dest) > 0.001f || l.poweringDown != poweringDown || l.lowPower != lowPower;
        // 幂等赋值（不做本地闪断模拟）
        if (l.light != null && l.light.enabled != on) l.light.enabled = on;
        l.destLightIntensity = dest;
        if (l.poweringDown != poweringDown) l.poweringDown = poweringDown;
        if (l.lowPower != lowPower) l.lowPower = lowPower;
        if (changed) DarkwoodAdapterRuntime.LogMessage($"[STATE] entity={(component.gameObject != null ? component.gameObject.name : "?")} type=Light on={on} dest={dest:F1} poweringDown={poweringDown} source=Host");
    }
    public void EnterClientProxyMode(Component component) { }
    public void ExitClientProxyMode(Component component) { }
}
