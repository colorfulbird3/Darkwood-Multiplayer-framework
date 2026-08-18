# Bear trap state out of sync on the client

Host toggles a bear trap; the client's trap keeps its old visual even though the replicated state says `isOn` changed.

## Why it happened

The visual toggle (`switchMe()` — model/animation swap) is only called by the local `activate()` path. Replicated state applied the `isOn` data field directly, without triggering the visual refresh. The client trap looked unchanged while the authoritative state had already flipped.

## What we do now

When applying an Item state, if `isOn` differs from the current value, we call `switchMe()` first to refresh the visual, then force-write the host's authoritative value.
