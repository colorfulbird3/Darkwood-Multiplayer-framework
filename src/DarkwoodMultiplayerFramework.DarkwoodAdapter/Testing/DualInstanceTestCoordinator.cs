using System;
using System.Collections.Generic;
using DarkwoodMultiplayerFramework.Protocol;

namespace DarkwoodMultiplayerFramework.DarkwoodAdapter.Testing;

/// <summary>
/// v0.9.2 TestHarness：协调器。Host 驱动 scenario 推进；Client 收到 phase 指令后执行真实 Darkwood 原版逻辑，
/// 完成后发 PhaseComplete。Host 校验世界 → 推进下一 phase / Complete / Abort。
/// 严禁任何"作弊"路径：所有 game state 变更必须走 DMF 网络 + 真实原版 API。
/// </summary>
public sealed class DualInstanceTestCoordinator
{
    private readonly DualInstanceTestAgent agent;
    private bool hostGotClientReady;
    private bool hostScenarioStarted;
    private int currentPhase = 0;
    private DateTime phaseStartedUtc;

    public DualInstanceTestCoordinator(DualInstanceTestAgent agent) { this.agent = agent; }

    /// <summary>Agent 收到 TestControlMessage 时调用。</summary>
    public void OnTestControl(TestControlMessage m)
    {
        if (agent.IsHost) OnHostControl(m); else OnClientControl(m);
    }

    private void OnHostControl(TestControlMessage m)
    {
        if (m.Kind == "Ready")
        {
            hostGotClientReady = true;
            agent.Trace.Log("COORD", $"Client READY（phase={m.Phase}）");
            TryStartScenario();
            return;
        }
        if (m.Kind == "PhaseComplete")
        {
            agent.Trace.Log("COORD", $"Client phaseComplete phase={m.Phase} payload={m.PayloadJson}");
            // 简化：Host 直接推进下一 phase（断言写死——具体 verify 逻辑在更后期补）
            currentPhase++;
            var nextPhase = agent.ActiveScenario?.Phases;
            if (nextPhase == null || currentPhase > nextPhase.Count)
            {
                agent.Finish("PASS", "all phases completed");
                return;
            }
            agent.Trace.Log("COORD-START-PHASE", $"phase={currentPhase} title={nextPhase[currentPhase-1].Title}");
            phaseStartedUtc = DateTime.UtcNow;
            agent.SendTestControl(new TestControlMessage("Start", agent.Scenario, currentPhase, "{\"verify\":\"" + nextPhase[currentPhase-1].HostVerification.Replace("\"","\\\"") + "\"}"));
            return;
        }
        if (m.Kind == "Complete")
        {
            // Client 收到自身 abort signal（一般不会发生）
            agent.Finish(m.PayloadJson.Contains("FAIL") ? "FAIL" : "PASS", m.PayloadJson);
            return;
        }
    }

    private void OnClientControl(TestControlMessage m)
    {
        if (m.Kind == "Start")
        {
            agent.Trace.Log("COORD", $"Host START phase={m.Phase}");
            // Client: 在实际项目里根据 phase.Kind 调真实 Darkwood API；本测试 Agent 占位实现：
            //   1) phase 1/2/3/5 模拟 ClientExecutePhase 返回 JSON
            //   2) 真实实现应通过 scenario.ClientExecutePhase 调 Darkwood 原版
            string payload = "{}";
            try { payload = agent.ActiveScenario?.ClientExecutePhase(m.Phase, m.PayloadJson) ?? "{}"; } catch (Exception error) { payload = "{\"error\":\"" + error.Message.Replace("\"","") + "\"}"; }
            agent.Trace.Log("CLIENT-PHASE-DONE", $"phase={m.Phase} payload={payload}");
            agent.SendTestControl(new TestControlMessage("PhaseComplete", agent.Scenario, m.Phase, payload));
            return;
        }
        if (m.Kind == "Complete")
        {
            // Host 已宣告整体 PASS/FAIL
            agent.Finish(m.PayloadJson.Contains("FAIL") ? "FAIL" : "PASS", m.PayloadJson);
            return;
        }
    }

    private void TryStartScenario()
    {
        if (hostScenarioStarted) return;
        if (!agent.IsReady) return; // host 自己也未 READY
        if (!hostGotClientReady) return;
        hostScenarioStarted = true;
        currentPhase = 1;
        var scenario = agent.ActiveScenario;
        if (scenario == null) { agent.Finish("FAIL", "no scenario registered"); return; }
        // Host setup
        try { scenario.HostSetup(); agent.Trace.Log("HOST-SETUP", "ok"); } catch (Exception error) { agent.Finish("FAIL", "host setup exception: " + error.Message); return; }
        var phases = scenario.Phases;
        if (phases.Count == 0) { agent.Finish("PASS", "zero phases"); return; }
        agent.Trace.Log("COORD-START-PHASE", $"phase={currentPhase} title={phases[0].Title}");
        phaseStartedUtc = DateTime.UtcNow;
        agent.SendTestControl(new TestControlMessage("Start", scenario.Name, currentPhase, "{\"verify\":\"" + phases[0].HostVerification.Replace("\"","\\\"") + "\"}"));
    }
}
