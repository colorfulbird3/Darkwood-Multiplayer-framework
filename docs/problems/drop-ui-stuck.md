# Drop cursor stuck after taking from a container

Player takes an item from a shared container, then drops it. The item stays glued to the cursor, the drop cursor icon never goes away, and the drop never happens on the world side.

## Why it happened

The drop interception (Harmony Prefix returning false) blocked the original method to keep world state host-authoritative. But the original drop spawn method also resets the UI drag state (`Controller.pickedUpItem` + icon despawn + `refreshRecipes`). Blocking it left the UI believing a drag was still in progress, so every subsequent drop attempt was rejected.

## What we do now

The drop patch hooks `Player.spawnDroppedInvItem` — the single convergence point for every drop path — and, when intercepted, manually performs the equivalent UI cleanup before returning false. The drop itself still completes through the host-authority path.
