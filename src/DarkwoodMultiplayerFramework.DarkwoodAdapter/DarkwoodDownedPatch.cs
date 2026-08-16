using HarmonyLib;
using UnityEngine;

namespace DarkwoodMultiplayerFramework.DarkwoodAdapter;

/// <summary>
/// Multiplayer downed state: intercepts the vanilla player death sequence.
/// While any player is still alive, die()/onDeath() are skipped and the local
/// player enters the DOWNED state instead (locked in place, waiting for rescue).
/// When every player is downed, the vanilla flow runs (the original ending).
/// </summary>
[HarmonyPatch]
public static class DarkwoodDownedPatch
{
    public static bool LocalDowned { get; private set; }
    public static bool AllDowned { get; set; }
    private static RigidbodyConstraints previousConstraints;

    public static void Reset()
    {
        LocalDowned = false;
        AllDowned = false;
    }

    private static bool InterceptActive()
    {
        var runtime = DarkwoodAdapterRuntime.Instance;
        return runtime != null && runtime.IsMultiplayerActive && !AllDowned;
    }

    [HarmonyPatch(typeof(Player), nameof(Player.die))]
    [HarmonyPrefix]
    public static bool DiePrefix(Player __instance)
    {
        if (__instance != Player.Instance) return true;
        var runtime = DarkwoodAdapterRuntime.Instance;
        if (runtime == null || !runtime.IsMultiplayerActive) return true;
        if (AllDowned) return true; // vanilla ending is running
        runtime.Combat.OnLocalPlayerDowned(); // 0.8.9：倒地状态归战斗服务
        return false; // skip the vanilla death sequence
    }

    [HarmonyPatch(typeof(Player), "onDeath")]
    [HarmonyPrefix]
    public static bool OnDeathPrefix(Player __instance)
    {
        if (__instance != Player.Instance) return true;
        if (!InterceptActive()) return true;
        // The downed state was already entered through die(); never run the
        // vanilla black-screen/respawn game-over while others can rescue us.
        return false;
    }

    /// <summary>Enters the downed state for the LOCAL player: locked in place, input forbidden, attackers dismissed.</summary>
    public static void EnterLocalDowned()
    {
        if (LocalDowned) return;
        var player = Player.Instance;
        if (player == null) return;
        LocalDowned = true;
        var body = player.GetComponent<Rigidbody>();
        if (body != null)
        {
            previousConstraints = body.constraints;
            body.constraints = RigidbodyConstraints.FreezeAll;
        }
        player.alive = false;
        global::Core.forbidInputs = true;
        try { player.interruptAllActions(); } catch { }
        if (player.charactersAttackingMe != null)
        {
            for (var i = player.charactersAttackingMe.Count - 1; i >= 0; i--)
            {
                var attacker = player.charactersAttackingMe[i];
                if (attacker == null) continue;
                try
                {
                    // 0.8.8-beta.5：倒地时让攻击者逃离（escaping）而不是 idle——此前 idle 让怪物定在原地（用户反馈）。
                    attacker.setBehaviour(Character.Behaviour.escaping, true);
                    attacker.aggressiveness = Aggressiveness.neutral;
                    attacker.removeFromPlayerAttackers();
                }
                catch { }
            }
        }
        try { player.lieDown(); } catch { }
        // Keep health at/below zero while downed so regen cannot wake the player.
        if (player.health > 0f) player.health = 0f;
    }

    /// <summary>Revives the LOCAL player: given health, full stamina, input restored, back on their feet.</summary>
    public static void ReviveLocalPlayer(float health, float stamina)
    {
        var player = Player.Instance;
        if (player == null) return;
        player.alive = true;
        player.setHealth(health);
        player.stamina = stamina;
        try { player.interruptAllActions(); } catch { }
        var body = player.GetComponent<Rigidbody>();
        if (body != null) body.constraints = previousConstraints;
        if (!AllDowned) global::Core.forbidInputs = false;
        LocalDowned = false;
    }
}
