# Two small rescue problems

Two unrelated issues surfaced from the same playtest log.

## 1. Rescue range was too strict

`RescueRange` was 2.5 m. In practice the downed player's body and the rescuer's hitbox made "nearby" much harder than the number suggested — the log showed the rescue request being rejected dozens of times in a row. Raised to 4 m and made the rejection log include the distance to the nearest downed player, so the next iteration is based on data.

## 2. The killer froze next to the body

When a player went down, the attacker was forced to `idle` behaviour with neutral aggressiveness. The intent was to stop the monster from re-attacking a corpse; the actual result was a monster standing still in place until the player died or was rescued.

Changed to `escaping` instead — the monster leaves the area, which is what it would do anyway.

## 3. Despawn broadcast storm

Persistent entities destroyed by the game (e.g. a removed trap) were never despawned on clients, and the periodic scan also respawned/despawned short-lived enemies every few seconds. Clients logged 170+ "unknown entity id" warnings per session.

Despawn messages are now sent only to clients that already received the spawn; others ignore them. The destroyed-entity check is separate from the spawn scan.
