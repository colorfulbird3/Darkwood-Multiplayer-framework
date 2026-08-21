using System;
using System.Collections.Generic;
using DarkwoodMultiplayerFramework.Core;
using DarkwoodMultiplayerFramework.Network;
using DarkwoodMultiplayerFramework.Protocol;
using UnityEngine;

namespace DarkwoodMultiplayerFramework.DarkwoodAdapter;

/// <summary>
/// 进程内回环自测客户端。在一个游戏实例内（主机开服后）按 F7 启动，
/// 用 Telepathy 连接 127.0.0.1 回环到本机主机，重放完整联机协议链：
/// 握手 → 存档传输（真实主机存档，SHA-256 校验后丢弃，不写盘不加载游戏）
/// → 访客档案 → 注册表/快照（权威快照，校验后丢弃）→ READY。
/// 用于本地验证主机侧的打包/传输/快照全链路，不依赖 Steam 双开。
/// F8 停止。
/// </summary>
public sealed class DarkwoodSelfTestClient : MonoBehaviour
{
    private ClientHandshakeSession? session;
    private ChunkTransferAssembler? saveAssembler;
    private ChunkTransferAssembler? snapshotAssembler;
    private SaveTransferManifest saveManifest;
    private WorldSnapshotManifest snapshotManifest;
    private bool handshook;
    private bool guestProfileReceived;
    private bool readySent;
    private bool snapshotAppliedSent;
    private bool passed;
    private float nextKeyPoll;
    private bool waitForWorld;
    private string pendingHost = string.Empty;
    private ushort pendingPort;
    private string pendingIdentity = "self-test";
    private bool autoMode;
    private int autoPhase;
    private float autoPhaseAt;
    private float autoStartedAt;

    public static DarkwoodSelfTestClient? Active { get; private set; }
    public bool Running => session != null;
    public bool Passed => passed;

    private void OnEnable() => Active = this;
    private void OnDisable() { if (ReferenceEquals(Active, this)) Active = null; }

    private void Update()
    {
        try { session?.Tick(); } catch (Exception error) { SelfTestLog($"✗ 自测会话 Tick 失败：{error.Message}"); session?.Dispose(); session = null; }
        if (Time.unscaledTime >= nextKeyPoll)
        {
            nextKeyPoll = Time.unscaledTime + 0.2f;
            if (Input.GetKeyDown(KeyCode.F7)) Begin("127.0.0.1", 17777, "self-test");
            else if (Input.GetKeyDown(KeyCode.F8)) Stop("手动停止");
        }
        if (waitForWorld)
        {
            var runtime = DarkwoodAdapterRuntime.Instance;
            if (runtime != null && runtime.IsHost && runtime.State == ConnectionState.Ready)
            {
                waitForWorld = false;
                SelfTestLog("主机世界就绪（注册表/存档可打包），启动回环自测客户端……");
                DoConnect(pendingHost, pendingPort, pendingIdentity);
            }
        }
        TickAutoMode();
    }

    /// <summary>自测：自动回环自测（配置 SelfTestAuto=true 时由 Plugin 调用）。
    /// 流程：3 秒后开主机 → 自动读档 → 主机 READY → 回环客户端全链路 → 判定。</summary>
    public void AutoStart()
    {
        autoMode = true; autoPhase = 0; autoPhaseAt = Time.unscaledTime + 3f; autoStartedAt = Time.unscaledTime;
        SelfTestLog("[自动自测] 启动：3 秒后自动创建主机……");
    }

    private void TickAutoMode()
    {
        if (!autoMode || Time.unscaledTime < autoPhaseAt) return;
        if (Time.unscaledTime - autoStartedAt > 150f) { SelfTestLog("[自动自测] ✗ 总超时（150 秒），放弃。"); autoMode = false; return; }
        var runtime = DarkwoodAdapterRuntime.Instance;
        if (runtime == null) return;
        switch (autoPhase)
        {
            case 0:
                if (!MainMenuReady()) { autoPhaseAt = Time.unscaledTime + 2f; break; } // 等主菜单 UI 就绪（过早调用会死机）
                SelfTestLog("[自动自测] 正在创建主机……");
                try { runtime.StartHost(); } catch (Exception error) { SelfTestLog($"✗ 开主机失败：{error.Message}"); autoMode = false; return; }
                autoPhase = 1; autoPhaseAt = Time.unscaledTime + 2f;
                break;
            case 1:
                if (!MainMenuReady()) { autoPhaseAt = Time.unscaledTime + 2f; break; }
                if (UnityEngine.SceneManagement.SceneManager.GetActiveScene().name == "Darkwood")
                {
                    SelfTestLog("[自动自测] 自动读档进入主机世界（quickLoadGame）……");
                    try { Singleton<UI>.Instance.quickLoadGame(); }
                    catch (Exception error) { SelfTestLog($"✗ 自动读档失败：{error.Message}"); autoMode = false; return; }
                    autoPhase = 2; autoPhaseAt = Time.unscaledTime + 1f;
                }
                else autoPhase = 2; // 已在游戏世界内
                break;
            case 2:
                if (runtime.IsHost && runtime.State == ConnectionState.Ready)
                {
                    SelfTestLog("[自动自测] 主机世界 READY，启动回环客户端……");
                    DoConnect("127.0.0.1", (ushort)17777, "self-test");
                    autoPhase = 3; autoPhaseAt = Time.unscaledTime + 90f;
                }
                break;
            case 3:
                if (passed) { SelfTestLog("[自动自测] ✓✓ 回环自测全链路通过。"); autoMode = false; }
                else if (session == null) { SelfTestLog("[自动自测] ✗ 90 秒内未通过，自测结束。"); autoMode = false; }
                else autoPhaseAt = Time.unscaledTime + 90f; // 继续等
                break;
        }
    }

    /// <summary>启动自测客户端（连接本机主机回环）。若在主菜单，先自动读档进入主机世界。</summary>
    public void Begin(string host, int port, string identity)
    {
        if (UnityEngine.SceneManagement.SceneManager.GetActiveScene().name == "Darkwood")
        {
            SelfTestLog("当前在主菜单：自动读档进入主机世界（等待主机 READY 后自动启动回环连接）……");
            pendingHost = host; pendingPort = (ushort)port; pendingIdentity = identity;
            waitForWorld = true;
            try { Singleton<UI>.Instance.StartCoroutine(Singleton<UI>.Instance.initLoadGame()); }
            catch (Exception error) { SelfTestLog($"✗ 自动读档失败：{error.Message}"); waitForWorld = false; }
            return;
        }
        DoConnect(host, (ushort)port, identity);
    }

    private void DoConnect(string host, ushort port, string identity)
    {
        Stop("重启");
        passed = false; handshook = false; guestProfileReceived = false; readySent = false; snapshotAppliedSent = false;
        saveAssembler = null; snapshotAssembler = null;
        try
        {
            session = new ClientHandshakeSession(new TelepathyClientTransport(DarkwoodAdapterRuntime.Instance?.TelepathyPath ?? "BepInEx/plugins/Telepathy.dll"), new ProtocolIdentity(ProtocolVersions.Framework, Application.version));
            session.HandshakeSucceeded += OnHandshakeSucceeded;
            session.HandshakeFailed += error => SelfTestLog($"✗ 握手失败：{error}");
            session.MessageReceived += OnMessage;
            session.GuestKey = NormalizeGuestKey(identity);
            session.Connect(host, port);
            SelfTestLog($"自测客户端正在连接 {host}:{port}（身份 {identity}）……");
        }
        catch (Exception error) { SelfTestLog($"✗ 自测启动失败：{error.Message}"); session = null; }
    }

    public void Stop(string reason)
    {
        if (session == null) { if (!string.IsNullOrEmpty(reason)) SelfTestLog($"自测会话未在运行（{reason}）。"); return; }
        try { session.Dispose(); } catch { }
        session = null;
        if (!passed) SelfTestLog($"自测已停止（{reason}）。");
    }

    private void OnHandshakeSucceeded()
    {
        handshook = true;
        SelfTestLog($"✓ 握手成功。玩家 ID：{session?.PeerId}，主机会话：{session?.HostSessionId}。");
        session?.Send(ProtocolMessageType.SaveTransferRequest, ReplicationProtocolCodec.Encode(new SaveTransferRequest(Guid.NewGuid())));
    }

    private void OnMessage(ProtocolEnvelope envelope)
    {
        try
        {
            if (envelope.MessageType == ProtocolMessageType.SaveTransferManifest)
            {
                saveManifest = ReplicationProtocolCodec.DecodeSaveTransferManifest(envelope.Payload);
                saveAssembler = new ChunkTransferAssembler(saveManifest.TransferId, saveManifest.TotalBytes, saveManifest.ChunkCount, saveManifest.Sha256);
                SelfTestLog($"开始接收存档：{saveManifest.TotalBytes} 字节，{saveManifest.ChunkCount} 块（{saveManifest.Description}）。");
            }
            else if (envelope.MessageType == ProtocolMessageType.SaveTransferChunk)
            {
                var chunk = ReplicationProtocolCodec.DecodeSaveTransferChunk(envelope.Payload);
                saveAssembler?.Add(chunk.TransferId, chunk.Index, chunk.Total, chunk.Data, chunk.Hash);
                if (saveAssembler != null && saveAssembler.IsComplete)
                {
                    _ = saveAssembler.Build(); // SHA-256 全量校验；校验通过即丢弃（自测不写盘、不加载游戏）
                    SelfTestLog($"✓ 存档接收并校验完成（{saveManifest.TotalBytes} 字节，SHA-256 匹配）。");
                    session?.Send(ProtocolMessageType.SaveTransferApplied, ReplicationProtocolCodec.Encode(new SaveTransferApplied(saveManifest.TransferId, saveManifest.ProfileId, "self-test-memory")));
                    if (!readySent)
                    {
                        // 真实客户端在加载存档后主动发 Ready（不依赖 GuestProfile；主机在快照 Applied 后才回 GuestProfile）。
                        readySent = true;
                        session?.Send(ProtocolMessageType.Ready, ReplicationProtocolCodec.Encode(new ReadyMessage(DarkwoodAdapterRuntime.Instance?.CurrentScene ?? string.Empty, string.Empty)));
                    }
                }
            }
            else if (envelope.MessageType == ProtocolMessageType.GuestProfile)
            {
                var profile = ReplicationProtocolCodec.DecodeGuestProfile(envelope.Payload);
                guestProfileReceived = true;
                SelfTestLog($"✓ 收到访客档案：出生点 ({profile.X:F1},{profile.Y:F1},{profile.Z:F1})，第 {profile.Day} 天，加入 {profile.JoinCount} 次。");
                // P0-2：模拟真实客户端——应用 Host 权威档案后 ack，Host 开放 inventory bootstrap 门（B 回归在此触发）。
                try { session?.Send(ProtocolMessageType.GuestProfileApplied, Array.Empty<byte>()); SelfTestLog("✓ GuestProfile 已应用并 ack（bootstrap gate open）。"); } catch (Exception) { }
            }
            else if (envelope.MessageType == ProtocolMessageType.WorldSnapshotManifest)
            {
                snapshotManifest = ReplicationProtocolCodec.DecodeWorldSnapshotManifest(envelope.Payload);
                snapshotAssembler = new ChunkTransferAssembler(snapshotManifest.SnapshotId, snapshotManifest.TotalBytes, snapshotManifest.ChunkCount, snapshotManifest.Sha256);
                SelfTestLog($"开始接收世界快照：{snapshotManifest.TotalBytes} 字节，{snapshotManifest.ChunkCount} 块，注册表 {snapshotManifest.RegistryDigest}。");
            }
            else if (envelope.MessageType == ProtocolMessageType.WorldSnapshotChunk)
            {
                var chunk = ReplicationProtocolCodec.DecodeWorldSnapshotChunk(envelope.Payload);
                snapshotAssembler?.Add(chunk.SnapshotId, chunk.Index, chunk.Total, chunk.Data, chunk.Hash);
                if (snapshotAssembler != null && snapshotAssembler.IsComplete && !snapshotAppliedSent)
                {
                    _ = snapshotAssembler.Build();
                    snapshotAppliedSent = true;
                    SelfTestLog($"✓ 世界快照接收并校验完成（{snapshotManifest.TotalBytes} 字节，SHA-256 匹配）。");
                    session?.Send(ProtocolMessageType.WorldSnapshotApplied, ReplicationProtocolCodec.Encode(new WorldSnapshotApplied(snapshotManifest.SnapshotId, snapshotManifest.Scene, snapshotManifest.RegistryDigest, snapshotManifest.ServerTick, 0)));
                }
            }
            else if (envelope.MessageType == ProtocolMessageType.Ready)
            {
                var ready = ReplicationProtocolCodec.DecodeReady(envelope.Payload);
                if (readySent && snapshotAppliedSent && !passed)
                {
                    passed = true;
                    SelfTestLog($"✓✓ 自测通过：回环联机全链路完成（握手 → 存档 {saveManifest.TotalBytes} 字节 → 档案 → 快照 {snapshotManifest.TotalBytes} 字节 → READY，场景 {ready.Scene}）。");
                }
            }
        }
        catch (Exception error) { SelfTestLog($"✗ 自测消息处理失败：{error.Message}"); }
    }

    /// <summary>主菜单 UI 是否已就绪（过早访问 Singleton&lt;MainMenu/UI&gt; 会导致游戏死机）。</summary>
    private static bool MainMenuReady()
    {
        try { return Singleton<MainMenu>.Instance != null && Singleton<UI>.Instance != null; }
        catch { return false; }
    }

    private static void SelfTestLog(string message) => DarkwoodAdapterRuntime.LogMessage($"[自测 {Time.unscaledTime:F0}s] {message}");

    private static string NormalizeGuestKey(string key)
    {
        var normalized = new List<char>();
        foreach (var c in (key ?? string.Empty).Trim())
        {
            if (char.IsLetterOrDigit(c) || c == '-' || c == '_') normalized.Add(c);
            else normalized.Add('_');
        }
        return normalized.Count == 0 ? "self-test" : new string(normalized.ToArray());
    }
}
