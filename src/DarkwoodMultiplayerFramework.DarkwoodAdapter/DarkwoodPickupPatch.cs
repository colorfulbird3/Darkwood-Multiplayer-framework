using HarmonyLib;

namespace DarkwoodMultiplayerFramework.DarkwoodAdapter;

/// <summary>Clients submit a host-authoritative pickup request instead of running Darkwood's local mutation.</summary>
[HarmonyPatch(typeof(Item), nameof(Item.getDroppedItem))]
internal static class DarkwoodPickupPatch
{
    private static bool Prefix(Item __instance)
    {
        var runtime = DarkwoodAdapterRuntime.Instance;
        if (runtime == null || !runtime.IsClient || runtime.State != DarkwoodMultiplayerFramework.Core.ConnectionState.Ready)
            return true;

        // Always suppress the native client-side mutation once the session is READY.
        // TryRequestPickup logs and rejects an unregistered target without changing
        // the authoritative world.
        runtime.TryRequestPickup(__instance);
        return false;
    }
}
