using System;
using System.Collections.Generic;

namespace DarkwoodMultiplayerFramework.DarkwoodAdapter.Testing;

/// <summary>
/// Scenario 基类：描述"Phase 序列"。每个 phase 是 (id, description, run-on-host/client, gate-condition)。
/// Host 是 Test Coordinator：phase 顺序 + 验证世界 + 决定下一步。
/// Client 执行 phase 后发 PhaseComplete，Host 校验后进下一 phase。
/// </summary>
public abstract class TestScenario
{
    public string Name { get; }
    public string Description { get; protected set; }
    protected DualInstanceTestAgent Agent { get; }

    protected TestScenario(DualInstanceTestAgent agent) { Agent = agent; Name = GetType().Name; }

    /// <summary>Host 视角：列出 phases 顺序。每个 phase 描述 client 该做什么 + host 该怎么 verify。</summary>
    public abstract IReadOnlyList<ScenarioPhase> Phases { get; }

    /// <summary>每个 scenario 可以预跑 setup（Host 创建/准备世界）—— 在 Ready 之后、Phase 0 之前。</summary>
    public virtual void HostSetup() { }

    /// <summary>每 phase 客户端执行什么。返回 PhaseComplete payload（JSON）。</summary>
    public virtual string ClientExecutePhase(int phase, string hostPayload) => "{}";
}

public readonly struct ScenarioPhase
{
    public int Index { get; }
    public string Title { get; }
    /// <summary>"client-execute" | "host-verify" | "both"</summary>
    public string Kind { get; }
    /// <summary>Host 验证条件描述（人类可读）。实际校验在 DualInstanceTestCoordinator.OnPhaseComplete 中做断言。</summary>
    public string HostVerification { get; }
    public ScenarioPhase(int index, string title, string kind, string hostVerification) { Index=index; Title=title; Kind=kind; HostVerification=hostVerification; }
}
