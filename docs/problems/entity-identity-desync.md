# Entities "play separately" on both machines

Host and client both loaded the same save, yet entities, enemies, items and doors each ran their own state: an item interaction on the client got ITEM_NOT_FOUND on the host, and world state visibly diverged.

## Why it happened

Both sides independently hashed a signature (SaveableObject.uniqueId + hierarchy path + sibling ordinal + position) into an EntityId and hoped the hashes matched. They did not: the two processes load the same save but the runtime object sets and hierarchy are not fully deterministic (streamed world generation, runtime spawns), so the client's registry (3236 entities) never aligned with the host's (3240). Missing IDs were silently skipped in Apply, the snapshot log printed `entities.Length` instead of what actually applied, and a 90-second delayed registry rebuild on the host papered over the gap without fixing the identity mismatch.

## What we do now

The host is the only EntityId authority. After the world stabilizes it builds an authoritative registry once (no more 90s rebuild) and sends an EntityBindingManifest — a chunked, hashed list of descriptors (id / kind / component type / saveable uid / relative path / name / position) — before the snapshot. The client scans local candidates without generating network ids, then explicitly binds them: uid+type+path first, uid+type+position tolerance second, kind+name+position last. Unconstrained nearest-neighbour matching is forbidden; ambiguous candidates are reported, not guessed. All Apply paths go through the authoritative mapping, ActionRequests resolve local components to host ids, Apply reports real received/applied/missing/stale counts, and the Ready gate refuses to go ready while critical entities (characters) are unmatched.
