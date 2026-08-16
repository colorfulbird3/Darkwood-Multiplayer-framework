using System;
using DarkwoodMultiplayerFramework.Core;
using UnityEngine;

namespace DarkwoodMultiplayerFramework.DarkwoodAdapter;

public sealed partial class DarkwoodAdapterRuntime
{
    public event Action<string>? SceneChanged;
    public event Action<ConnectionState>? StateChanged;
    private void SetState(ConnectionState next)
    {
        if (State == next) return;
        State = next;
        Session.State = next; // 0.8.9：SessionContext 同步
        log?.LogInfo($"联机状态：{StateText(next)}。");
        StateChanged?.Invoke(next);
    }
    private ConnectionState DetectState()
    {
        if (hostSession == null && clientSession == null) return ConnectionState.Disconnected;
        if (hostSession != null) return hostSession.IsActive ? ConnectionState.Ready : ConnectionState.Connecting;
        if (clientSession == null) return ConnectionState.Disconnected;
        if (sessionError.Length>0||clientSession.Session.Lifecycle.State == ConnectionState.Failed) return ConnectionState.Failed;
        if (!clientSession.HandshakeComplete) return clientSession.Session.Lifecycle.State;
        if (Player.Instance == null) return ConnectionState.LoadingSave;
        if (clientSession != null && !SaveState.ClientSnapshotReady) return clientSession.Session.Lifecycle.State;
        if (registryDirty) return ConnectionState.BuildingRegistry;
        return ConnectionState.Ready;
    }
    private static string StateText(ConnectionState state) => state switch
    {
        ConnectionState.Disconnected => "未连接",
        ConnectionState.Connecting => "连接中",
        ConnectionState.VersionChecking => "版本检查",
        ConnectionState.SaveTransfer => "准备存档",
        ConnectionState.LoadingSave => "加载存档",
        ConnectionState.BuildingRegistry => "建立实体注册表",
        ConnectionState.ApplyingSnapshot => "应用世界快照",
        ConnectionState.Ready => "已就绪",
        ConnectionState.Failed => "失败",
        ConnectionState.Stopping => "停止中",
        _ => state.ToString()
    };
    private bool IsNetworkConnected() => hostSession?.IsActive == true || clientSession?.HandshakeComplete == true;
    private ushort Port => (ushort)Mathf.Clamp(portConfig?.Value ?? 17777, 1, 65535);
}
