using System.Collections.Generic;

namespace DarkwoodMultiplayerFramework.DarkwoodAdapter.Testing.Scenarios;

/// <summary>DropRoundTrip：Client 原版 Drop → Host 注册 RuntimeEntity → Host 原版 Pickup → Despawn。
/// 设计目标：能立即发现 [DROP] "本地原版 spawnDroppedInvItem 后未捕获到对象" 这类回归。</summary>
public sealed class DropRoundTripScenario : TestScenario
{
    public DropRoundTripScenario(DualInstanceTestAgent agent) : base(agent) { Description = "Drop round trip Client→Host→Pickup→Despawn"; }

    public override IReadOnlyList<ScenarioPhase> Phases => new List<ScenarioPhase>
    {
        new ScenarioPhase(1, "Setup: ensure Client has a rope in Backpack (test helper)", "client-execute", "Client Backpack contains rope ≥ 1"),
        new ScenarioPhase(2, "DropLocalOriginal: Client invokes Darkwood Player.spawnDroppedInvItem (real call)", "client-execute", "[DROP-LOCAL] log appears; [DROP-COMMIT-SEND] log appears within 1s"),
        new ScenarioPhase(3, "HostRuntimeSpawn: Host registers RuntimeEntity for the drop", "host-verify", "[DROP-COMMIT-RECV] log appears; [DROP-SPAWN] log appears; Host world GameObject exists"),
        new ScenarioPhase(4, "HostPickup: Host invokes Darkwood pickup on the entity", "host-verify", "[PICKUP-COMMIT-RECV] log appears; RuntimeEntityDespawn broadcast sent"),
        new ScenarioPhase(5, "ClientInventoryConservation: item conservation check", "both", "Client rope count = (initial - 1) after pickup"),
    };
}
