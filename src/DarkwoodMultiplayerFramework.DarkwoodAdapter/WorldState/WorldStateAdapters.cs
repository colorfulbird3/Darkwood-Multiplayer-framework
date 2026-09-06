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
        // 注意：不走 turnOn()/turnOff()——它们经 Core.sendTriggerInfo 发 onTurnOn/onTurnOff 世界事件，
        // 在客户端本地重放会触发事件链（DarkwoodReplayTriggerGuard 只抑制 onTake/onPlace）。视觉型状态
        // （灯/夹子）由各自 typed adapter 或 Action Replay 以无事件方式刷新（SetActive/switchToTriggered）。
        if (item.isOn != isOn) item.isOn = isOn;
    }
    public void EnterClientProxyMode(Component component) { }
    public void ExitClientProxyMode(Component component) { }
}

/// <summary>BearTrap 专用：在 GenericItem 基础上同步 isOn/destroyed/health + Trigger.triggered。
/// 视觉真相（v0.9.6 修正）：夹子"夹住/合拢"由 vanilla Trigger.triggered 驱动（switchToTriggered 刷模型/动画），
/// 不是 Item.isOn（isOn 在放置后通常恒定）。此前只同步 isOn → 客户端夹子动画永远不变。借 coop TrapSync 语义：
/// 只复演 false→true（switchToTriggered），不做反向（原版无可逆 API，夹子生命周期内不解除）。</summary>
public sealed class BearTrapStateAdapter : IWorldStateAdapter
{
    public const string MarkerName = "beartrap";
    public ushort SchemaId => WorldStateSchemas.BearTrap;
    public bool CanHandle(Component component) => component is Item item && item.name.ToLowerInvariant().Contains(MarkerName);
    public static bool IsBearTrap(Item item) => item != null && item.name.ToLowerInvariant().Contains(MarkerName);
    // 捕兽夹状态：isOn(armed) / destroyed / health / triggered(Trigger 合拢视觉真相)。payload 布局变更 → Framework 版本递增。
    public byte[] Capture(Component component)
    {
        var item = (Item)component;
        using var s = new MemoryStream(); using var w = new BinaryWriter(s);
        w.Write(item.isOn); w.Write(item.destroyed); w.Write(item.health);
        w.Write(ResolveTrigger(item)?.triggered ?? false);
        return s.ToArray();
    }
    public bool HasChanged(byte[] o, byte[] n) { if (o == null || n == null || o.Length != n.Length) return true; for (var i = 0; i < o.Length; i++) if (o[i] != n[i]) return true; return false; }
    private static Trigger ResolveTrigger(Item item)
    {
        try { return item.GetComponent<Trigger>() ?? item.GetComponentInChildren<Trigger>(true); }
        catch (Exception) { return null; }
    }
    public void Apply(Component component, byte[] state)
    {
        if (state == null || state.Length < 7 || component is not Item item) return;
        using var r = new BinaryReader(new MemoryStream(state));
        var armed = r.ReadBoolean(); var destroyed = r.ReadBoolean();
        var health = r.ReadSingle(); var triggered = r.ReadBoolean();
        item.health = Mathf.RoundToInt(health);
        // 幂等赋值：绝不 toggle（switchMe 会翻转视觉造成两端反复）。
        if (item.isOn != armed) item.isOn = armed;
        if (item.destroyed != destroyed) item.destroyed = destroyed;
        if (item.gameObject.activeSelf != !destroyed) item.gameObject.SetActive(!destroyed);
        // 阶段二：TriggerBlocker（踩踏碰撞/下陷视觉）跟随 armed&&!destroyed 幂等启停。
        var blocker = item.GetComponent<TriggerBlocker>();
        if (blocker != null && blocker.enabled != (armed && !destroyed)) blocker.enabled = armed && !destroyed;
        // v0.9.6：Trigger 合拢视觉同步——权威 triggered 翻转为 true 且未被摧毁时，复演原版 switchToTriggered()
        // 刷新客户端模型/动画（幂等：本地已 triggered 则跳过；权威 false 不回退——夹子生命周期内不解除）。
        var trigger = ResolveTrigger(item);
        var refreshed = false;
        if (trigger != null && triggered && !destroyed && !trigger.triggered)
        {
            try { trigger.switchToTriggered(); refreshed = true; }
            catch (Exception error) { DarkwoodAdapterRuntime.LogMessage($"[BEARTRAP] switchToTriggered 复演失败：{error.Message}"); }
        }
        DarkwoodAdapterRuntime.LogMessage($"[BEARTRAP] id={item.name} armed={armed} triggered={triggered} localTriggered={trigger?.triggered} broken={destroyed} visualRefreshed={refreshed} source=Host");
        // r17：客户端把该夹子纳入本地触发 watch（踩中上报；本 Apply 只注册不上报）
        try { DarkwoodAdapterRuntime.Instance?.WatchTrap(item); } catch (Exception) { }
        // r21：权威合拢刚发生时（Apply 翻转，天然只播一次）→ 在被夹处播 vanilla 受击血渍 FX，
        // 让远端玩家看到「夹住喷血」视觉（与 Player.getHit 普通受伤同款 Shotsplat_stay）。
        if (refreshed)
        {
            try
            {
                var pos = item.transform != null ? item.transform.position : Vector3.zero;
                Vector3 ground;
                try { ground = global::Core.getYPos(pos, global::PosType.items1); }
                catch (Exception) { ground = pos; }
                var fxGo = global::Core.AddPrefab("FX/Bloodsplats/Shotsplat_stay", ground + new Vector3(0f, 0.08f, 0f), Quaternion.Euler(90f, UnityEngine.Random.Range(0, 360), 0f), null);
                DarkwoodAdapterRuntime.LogMessage($"[BEARTRAP-FX] 出血播发 item={item.name} pos=({pos.x:F0},{pos.y:F0},{pos.z:F0}) ground=({ground.x:F0},{ground.y:F0},{ground.z:F0}) goNull={(fxGo == null ? "是" : "否")}");
            }
            catch (Exception error) { DarkwoodAdapterRuntime.LogMessage($"[BEARTRAP] 出血 FX 播放失败：{error.Message}"); }
        }
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
        // 本地电源镜像（借鉴 Coop GeneratorSync.ApplyChanges）：主机权威 isOn 变化时，客户端也执行原版
        // turnOn()/turnOff()，让「本地电源网络」给本地灯 restorePower/powerDown —— 否则客户端灯永远不会亮。
        var shouldTurnOn = isOn && !g.isOn;
        var shouldTurnOff = !isOn && g.isOn;
        // 幂等赋值（禁止 toggle）；fuel 为 Host 权威值（客户端绝不 drain——但 turnOn 后本地 drain 会被下一次 Host fuel 覆盖）。
        if (g.isOn != isOn) g.isOn = isOn;
        g.fuel = fuel;
        if (g.lowPower != lowPower) g.lowPower = lowPower;
        var item = g.GetComponent<Item>();
        if (item != null && item.isOn != isOn) item.isOn = isOn;
        try
        {
            if (shouldTurnOn) g.turnOn();
            else if (shouldTurnOff) g.turnOff();
        }
        catch (Exception) { /* 镜像电源执行失败不影响权威字段 */ }
        if (changed) DarkwoodAdapterRuntime.LogMessage($"[GENERATOR] id={component.gameObject.name} running={isOn} fuel={fuel:F0} powered={!lowPower} source=Host{(shouldTurnOn ? " +mirror-turnOn" : shouldTurnOff ? " +mirror-turnOff" : "")}");
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
