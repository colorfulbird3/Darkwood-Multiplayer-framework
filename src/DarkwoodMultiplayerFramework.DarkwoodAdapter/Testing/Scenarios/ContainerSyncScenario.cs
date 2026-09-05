using System.Collections.Generic;

namespace DarkwoodMultiplayerFramework.DarkwoodAdapter.Testing.Scenarios;

/// <summary>Container Transaction regression：共享容器双向（Client 拿 → Host 看；Host 放 → Client 看）。
/// 检查 SYNC-HEALTH containerTxConflict=0 且 EntityId != 0。</summary>
public sealed class ContainerSyncScenario : TestScenario
{
    public ContainerSyncScenario(DualInstanceTestAgent agent) : base(agent) { Description = "Container Transaction 双向 + conflict=0 + EntityId!=0"; }

    public override IReadOnlyList<ScenarioPhase> Phases => new List<ScenarioPhase>
    {
        new ScenarioPhase(1, "Client takes 1 test item from shared container", "client-execute", "Host container slot decreased; SYNC-HEALTH containerTxConflict=0; EntityId!=0"),
        new ScenarioPhase(2, "Host puts 1 test item into same container", "host-verify", "Client container slot increased; broadcast received"),
        new ScenarioPhase(3, "Stack test: Client takes another, verify stacking behavior", "client-execute", "Host reflects stacked slot"),
        new ScenarioPhase(4, "Idempotency: 5 rapid take/put pairs", "both", "Final state consistent on both sides; no container conflict"),
    };
}
