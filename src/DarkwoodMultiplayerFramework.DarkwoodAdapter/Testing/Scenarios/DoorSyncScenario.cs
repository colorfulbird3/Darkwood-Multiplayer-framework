using System.Collections.Generic;

namespace DarkwoodMultiplayerFramework.DarkwoodAdapter.Testing.Scenarios;

/// <summary>Baseline regression：Door 双向同步（Client → Host → Client）。
/// 不依赖具体地图：找测试范围内的 Door；找不到则 fail。</summary>
public sealed class DoorSyncScenario : TestScenario
{
    public DoorSyncScenario(DualInstanceTestAgent agent) : base(agent) { Description = "Door 双向 state 同步（baseline regression）"; }

    public override IReadOnlyList<ScenarioPhase> Phases => new List<ScenarioPhase>
    {
        new ScenarioPhase(1, "Client toggles nearest Door", "client-execute", "Host reads Door.isOpen changed for the same entity"),
        new ScenarioPhase(2, "Host toggles same Door", "host-verify", "Client reads Door.isOpen reflects Host change"),
        new ScenarioPhase(3, "Idempotency: 2 rapid toggles", "client-execute", "Host final state matches Client final state (no torn writes)"),
    };
}
