using System;
using System.Collections.Generic;
using DarkwoodMultiplayerFramework.Network;
using DarkwoodMultiplayerFramework.Protocol;

namespace DarkwoodMultiplayerFramework.DarkwoodAdapter.Testing;

/// <summary>
/// v0.9.2 TestHarness：每个进程内的测试代理。
/// 普通用户：Enabled=false，不创建。
/// TestMode：创建实例，负责 READY 协调 + 接收/发送 TestControlMessage + 调真实 Darkwood 原版 API。
/// 严禁 Host Agent 直接修改 Client 状态 / Client Agent 直接修改 Host 状态——只能走正式 DMF 网络路径。
/// </summary>
public sealed class DualInstanceTestAgent
{
    /// <summary>TestConfig（静态）字段快照；构造后不可变。</summary>
    public string Role { get; }
    public string RunId { get; }
    public string ScenarioName { get; }
    public string Address { get; }
    public ushort Port { get; }
    public string PlayerName { get; }
    public string TraceDir { get; }
    public string SaveSlot { get; }
    public TestBootstrap? Bootstrap { get; private set; }
    public int ProcessReadyTimeoutSeconds { get; }
    public bool IsHost => string.Equals(Role, "Host", StringComparison.OrdinalIgnoreCase);
    public bool IsClient => string.Equals(Role, "Client", StringComparison.OrdinalIgnoreCase);
    public TestTraceWriter Trace { get; }
    public DualInstanceTestResult Result { get; }
    public DarkwoodAdapterRuntime Runtime { get; }

    public bool IsReady { get; private set; }
    public DateTime ReadyAtUtc { get; private set; }
    public string Scenario { get; private set; } = "";
    private TestScenario? activeScenario;
    private readonly Queue<TestControlMessage> incoming = new Queue<TestControlMessage>();
    private readonly object lockObj = new object();

    public event Action<TestControlMessage>? OnTestControl;

    public DualInstanceTestAgent(DarkwoodAdapterRuntime runtime)
    {
        Runtime = runtime;
        if (!TestConfig.Enabled) throw new InvalidOperationException("DualInstanceTestAgent must only be created when TestConfig.Enabled=true");
        // NOTE: must use local temp — assigning to readonly auto-property 'Role = Role' would CS0200
        var resolvedRole = TestConfig.Role ?? "Unknown";
        var resolvedRunId = TestConfig.RunId ?? Guid.NewGuid().ToString("N");
        var resolvedScenario = TestConfig.Scenario ?? "";
        var resolvedAddress = TestConfig.Address ?? "127.0.0.1";
        var resolvedPlayerName = TestConfig.PlayerName ?? "DMFTest";
        var resolvedTraceDir = TestConfig.TraceDir ?? System.IO.Path.Combine("TestHarness", "output", resolvedRunId);
        var resolvedPort = TestConfig.Port;
        var resolvedReadyTimeout = TestConfig.ProcessReadyTimeoutSeconds;
        Role = resolvedRole;
        RunId = resolvedRunId;
        ScenarioName = resolvedScenario;
        Address = resolvedAddress;
        PlayerName = resolvedPlayerName;
        TraceDir = resolvedTraceDir;
        Port = resolvedPort;
        ProcessReadyTimeoutSeconds = resolvedReadyTimeout;
        SaveSlot = TestConfig.SaveSlot ?? "TestSaveA";
        Trace = new TestTraceWriter(TraceDir, Role, RunId);
        Result = new DualInstanceTestResult
        {
            RunId = RunId,
            Scenario = ScenarioName,
            StartedUtc = DateTime.UtcNow,
            HostTrace = System.IO.Path.Combine(TraceDir, "host.trace.log"),
            ClientTrace = System.IO.Path.Combine(TraceDir, "client.trace.log"),
        };
    }

    public void MarkReady()
    {
        IsReady = true;
        ReadyAtUtc = DateTime.UtcNow;
        Trace.Log("READY", $"role={Role} scenario={ScenarioName}");
        SendTestControl(new TestControlMessage("Ready", ScenarioName, 0, "{}"));
    }

    public void SetScenario(TestScenario scenario) {
        activeScenario = scenario;
        Scenario = scenario.Name;
        Result.Scenario = scenario.Name;
        Trace.Log("SCENARIO", $"set={scenario.Name}");
        // v0.9.2 BOOT-TEST-2: 初始化 Bootstrap (Host-only)
        if (IsHost && Bootstrap == null) Bootstrap = new TestBootstrap(this);
    }

    /// <summary>每帧驱动 Bootstrap 状态机（Main → Save Load → WorldReady）。</summary>
    public void TickBootstrap()
    {
        Bootstrap?.Tick();
    }

    public bool IsHostWorldReady => Bootstrap?.Phase == TestBootstrap.BootPhase.HostWorldReady;
    public string BootstrapPhaseName => Bootstrap?.Phase.ToString() ?? "Disabled";
    public string BootstrapLastReason => Bootstrap?.LastReason ?? "";

    public TestScenario? ActiveScenario => activeScenario;

    public void EnqueueTestControl(TestControlMessage m)
    {
        lock (lockObj)
        {
            incoming.Enqueue(m);
            Trace.Log("TEST-CONTROL-IN", $"kind={m.Kind} phase={m.Phase} scenario={m.Scenario}");
        }
        OnTestControl?.Invoke(m);
    }

    public bool TryDequeueTestControl(out TestControlMessage m)
    {
        lock (lockObj) { if (incoming.Count > 0) { m = incoming.Dequeue(); return true; } m = default; return false; }
    }

    /// <summary>Host/Client 发送 TestControl 给 peer。TestMode 专用路由：复用现有 DMF 网络层 Send API。</summary>
    public void SendTestControl(TestControlMessage m)
    {
        Trace.Log("TEST-CONTROL-OUT", $"kind={m.Kind} phase={m.Phase} scenario={m.Scenario} payload={m.PayloadJson}");
        try
        {
            var payload = ReplicationProtocolCodec.Encode(m);
            // 反射拿到 clientSession / hostSession（HandshakeSession.Send）发 TestControl
            System.Func<bool> tryHost = () => {
                var hs = Runtime.GetType().GetField("hostSession", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.GetValue(Runtime);
                if (hs == null) return false;
                var m = hs.GetType().GetMethod("Send", new[]{ typeof(ProtocolMessageType), typeof(byte[]), typeof(TransportChannel) });
                if (m == null) return false;
                foreach (var pid in Runtime.ReadyPeersSnapshot) m.Invoke(hs, new object[]{ ProtocolMessageType.TestControl, payload, DarkwoodMultiplayerFramework.Network.TransportChannel.ReliableGameplay });
                return true;
            };
            System.Func<bool> tryClient = () => {
                var cs = Runtime.GetType().GetField("clientSession", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.GetValue(Runtime);
                if (cs == null) return false;
                var m = cs.GetType().GetMethod("Send", new[]{ typeof(ProtocolMessageType), typeof(byte[]), typeof(TransportChannel) });
                if (m == null) return false;
                m.Invoke(cs, new object[]{ ProtocolMessageType.TestControl, payload, DarkwoodMultiplayerFramework.Network.TransportChannel.ReliableGameplay });
                return true;
            };
            if (Runtime.IsHost && !tryHost()) Trace.Log("TEST-CONTROL-ERR", "host send failed");
            else if (Runtime.IsClient && !tryClient()) Trace.Log("TEST-CONTROL-ERR", "client send failed");
        }
        catch (Exception error) { Trace.Log("TEST-CONTROL-ERR", $"send failed: {error.Message}"); }
    }

    public void Finish(string result, string reason)
    {
        Result.Result = result;
        Result.FailReason = reason;
        Result.DurationMs = (long)(DateTime.UtcNow - Result.StartedUtc).TotalMilliseconds;
        Trace.Log("TEST-RESULT", $"result={result} reason={reason} durationMs={Result.DurationMs}");
        // 带括号标记写 BepInEx LogOutput.log —— Run-DualInstance.ps1 的 [TEST-RESULT] 轮询依赖它。
        DarkwoodAdapterRuntime.Instance?.log?.LogInfo($"[TEST-RESULT] result={result} reason={reason} durationMs={Result.DurationMs}");
        SendTestControl(new TestControlMessage("Complete", ScenarioName, -1, $"{{\"result\":\"{result}\",\"reason\":\"{reason.Replace("\"","")}\"}}"));
    }
}
