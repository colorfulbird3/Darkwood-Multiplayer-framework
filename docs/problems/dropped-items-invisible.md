# Dropped items invisible on the other side

Host dropped an item on the ground; client walked into range and saw nothing.

## Why it happened

The runtime entity scanner looked for containers under a specific parent node. Dropped items are in a different container hierarchy, so the scanner never registered them. The host log had zero "registered dropped item" lines for an entire session — that was the giveaway.

## What we do now

The scanner registers anything with an item inventory that is not already registered, regardless of parent. The 35 m spawn gate is unchanged.

One side effect: dropped items from before the fix would have been invisible forever, because registration happens at spawn time. Not much we can do about existing sessions.
