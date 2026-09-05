using System;
using DarkwoodMultiplayerFramework.Core;
using DarkwoodMultiplayerFramework.Protocol;
using UnityEngine;

namespace DarkwoodMultiplayerFramework.DarkwoodAdapter.Actions;

/// <summary>
/// v0.9.0（A1/A4）世界对象动作的 Replay 实现。Host 一律 inline 执行原版后经
/// ActionSyncManager.BroadcastExecuted 广播；本类只负责各端 ApplyingRemote 内的副作用重放。
/// </summary>
public static class WorldActions
{
    public static void RegisterAll(ActionSyncManager manager)
    {
        manager?.Register(new GeneratorToggleAction());
        manager?.Register(new DoorToggleAction());
    }

    private static Generator? FindGenerator(Component target)
    {
        if (target == null) return null;
        try { return target.GetComponent<Generator>() ?? target.GetComponentInChildren<Generator>(true); }
        catch { return null; }
    }
    private static Door? FindDoor(Component target)
    {
        if (target == null) return null;
        try { return target as Door ?? target.GetComponentInChildren<Door>(true); }
        catch { return null; }
    }

    /// <summary>A1：发电机开/关——param[0]=1 on / 0 off。客户端重放原版 turnOn/turnOff，
    /// 让本地电源网络给本地灯 restorePower（灯由本地电源自愈）。</summary>
    public sealed class GeneratorToggleAction : ActionSyncManager.IWorldObjectAction
    {
        public byte ActionKey => ActionSyncManager.Keys.GeneratorToggle;
        public void ExecuteHost(DarkwoodAdapterRuntime runtime, EntityId id, byte[] param) { /* Host inline 执行，见 StateObjectInteract handler */ }
        public void Replay(DarkwoodAdapterRuntime runtime, Component target, byte[] param)
        {
            var g = FindGenerator(target);
            if (g == null) { runtime?.log?.LogWarning("[ACTION] gen replay: 目标无 Generator 组件"); return; }
            var on = param != null && param.Length > 0 && param[0] == 1;
            try
            {
                if (on && !g.isOn) g.turnOn();
                else if (!on && g.isOn) g.turnOff();
            }
            catch (Exception error) { runtime?.log?.LogWarning($"[ACTION] gen replay 执行失败：{error.Message}"); }
            // 灯可见性镜像（v0.9.1 修正）：即时 SetActive，不碰 powerUp/powerDown 渐变；不改灯 isOn 语义。
            try
            {
                var items = g.powerItems;
                if (items != null)
                {
                    for (var i = 0; i < items.Count; i++)
                    {
                        var it = items[i];
                        if (it == null || !it.isLight) continue;
                        var il = it.itemLight;
                        try
                        {
                            if (on && it.isOn)
                            {
                                it.restorePower();
                                if (il != null)
                                {
                                    il.destLightIntensity = il.lightIntensity;
                                    if (il.light != null)
                                    {
                                        il.light.enabled = true;
                                        if (!il.light.gameObject.activeSelf) il.light.gameObject.SetActive(true);
                                    }
                                }
                            }
                            else if (!on)
                            {
                                if (il != null)
                                {
                                    il.destLightIntensity = 0f;
                                    if (il.light != null)
                                    {
                                        il.light.enabled = false;
                                        if (il.light.gameObject.activeSelf) il.light.gameObject.SetActive(false);
                                    }
                                }
                            }
                        }
                        catch (Exception) { }
                    }
                    try { if (Singleton<Controller>.Instance != null) Singleton<Controller>.Instance.updateLogicLights(); } catch (Exception) { }
                }
            }
            catch (Exception) { }
            // 详查：重放后本地电源网络状态（灯是否由本地 restorePower 点亮）。
            try
            {
                var items = g.powerItems;
                var lit = 0;
                if (items != null)
                {
                    var names = new System.Text.StringBuilder();
                    for (var i = 0; i < items.Count; i++)
                    {
                        var it = items[i];
                        if (it == null) continue;
                        var light = it.GetComponentInChildren<ItemLight>(true);
                        if (names.Length > 0) names.Append(',');
                        names.Append(it.name);
                        if (light != null && light.light != null && light.light.enabled) lit++;
                    }
                    runtime?.log?.LogInfo($"[ACTION] gen replay 后 isOn={g.isOn} powerItems={items.Count} litItemLight={lit} names=[{names}]");
                }
                else runtime?.log?.LogInfo("[ACTION] gen replay 后 isOn=" + g.isOn + " powerItems=(null)");
            }
            catch (Exception error) { runtime?.log?.LogWarning($"[ACTION] gen replay 诊断失败：{error.Message}"); }
        }
    }

    /// <summary>A4：门开/关——param[0]=1 应开 / 0 应关。客户端 Replay 原版 openClose 校正视觉（幂等：仅不一致时）。</summary>
    public sealed class DoorToggleAction : ActionSyncManager.IWorldObjectAction
    {
        public byte ActionKey => ActionSyncManager.Keys.DoorToggle;
        public void ExecuteHost(DarkwoodAdapterRuntime runtime, EntityId id, byte[] param) { /* Host inline 执行，见 DoorInteract handler */ }
        public void Replay(DarkwoodAdapterRuntime runtime, Component target, byte[] param)
        {
            var d = FindDoor(target);
            if (d == null) return;
            var shouldOpen = param != null && param.Length > 0 && param[0] == 1;
            if (d.opened == shouldOpen) return; // 幂等：已一致不重复执行
            try
            {
                var opener = Player.Instance != null ? Player.Instance._transform : null;
                d.openClose(opener);
            }
            catch (Exception) { }
        }
    }
}
