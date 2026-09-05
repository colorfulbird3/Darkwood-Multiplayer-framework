# Bear trap triggered visual out of sync on the client (FIXED v0.9.6-pre.1)

Symptom: a bear trap springs on the Host (monster/remote player steps in); the Host's trap
shows the sprung/closed visual, but the client's mirror trap keeps the armed look. Removal
(destroy/despawn) was already syncing fine — only the intermediate "sprung" stage never showed.

## Root cause (corrected after RE audit)

The trap's *sprung* visual is **not** driven by `Item.isOn`. A placed trap keeps `Item.isOn`
(== "armed" flag) constant across its whole life; the thing that actually flips when it snaps
shut is the vanilla **`Trigger.triggered`** field, and the visual swap is done by
**`Trigger.switchToTriggered()`** (verified against vanilla `Item.switchTriggerState` and the
coop mod's `TrapSync`, which polls `Trigger.triggered` and replays `switchToTriggered()`
remotely).

Our old `BearTrapStateAdapter` only serialized `Item.isOn/destroyed/health` and wrote `isOn`
idempotently — so the client's authoritative copy carried no signal that the trap had closed,
and no visual refresh happened. That is why the client animation never changed.

### Why NOT the previously documented "switchMe() on isOn change"
- `switchMe()` is a *toggle* and touches `Player.Instance.cursor.actionText` — remote replay
  would flash cursor text.
- `Item.turnOn()/turnOff()` go through `Core.sendTriggerInfo(onTurnOn/onTurnOff)`, firing
  world-event chains on the client (our `DarkwoodReplayTriggerGuard` only suppresses
  onTake/onPlace). Using them for generic typed apply would be an event-spraying patch.
- They also operate on the wrong field anyway (see root cause above).

## Fix (v0.9.6-pre.1)

1. `BearTrapStateAdapter` payload extended with **`Trigger.triggered`** (wire layout change →
   `ProtocolVersions.Framework` bumped to `0.8.9.6-pre.1`).
2. Client `Apply`: when authoritative `triggered == true` and the local
   `Trigger.triggered` is still false (and trap not destroyed), replay
   **`trigger.switchToTriggered()`** to refresh the mirror's model/animation. Idempotent:
   a repeated apply with both sides already `triggered` is a no-op; we never un-trigger
   (vanilla has no reversible API for that state and a trap is not re-armed across a session).
3. Host side new Harmony postfix on `Trigger.switchToTriggered()` (`DarkwoodTrapTriggerPatch`):
   when the Host's own simulation springs a *bound* beartrap, it calls
   `BroadcastStateNow(id)` immediately — `CaptureNow` bypasses the 1 Hz throttled typed
   capture, so the short-lived sprung state is not missed.

## Verification on real machine
- Host: logs `[TRAP-TRIGGER] Host 夹子触发即时广播：…` when the trap springs.
- Client: logs `[BEARTRAP] … triggered=True visualRefreshed=True …` when the delta arrives,
  and the trap model/animation should visibly snap shut, matching the Host until removal.
- `[BEARTRAP] … triggered=False …` lines on idle are normal (armed baseline).
