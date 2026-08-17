# Contributing

Small project, loose rules. A few things matter:

## Before changing the Darkwood adapter

- Check the actual game assemblies first (`Darkwood_Data/Managed/`). Do not assume undocumented APIs exist.
- The adapter talks to the game through Harmony patches and direct calls; both are fragile. Keep changes minimal.

## Before changing the protocol

- Wire format is not versioned for compatibility: host and client must match exactly.
- If you change a message layout, bump `ProtocolVersions.Framework` and the plugin version.
- Keep codec changes in the domain folder that owns the message (`src/DarkwoodMultiplayerFramework.Protocol/<domain>/`).

## Tests

- `dotnet test` — unit tests (Core / Protocol / Entities / Network). Should be green.
- In-game loopback test — set `SelfTestAuto=true` in `BepInEx/config/com.darkwood.multiplayer.framework.rebuilt.adapter.cfg`, start the game, wait for `✓✓ 回环自测全链路通过`, reset to `false`. Run this when touching networking.
- SelfTests console project — `dotnet run --project src/DarkwoodMultiplayerFramework.SelfTests` (no Unity needed).

## Notes

- Do a clean build (delete `obj/`/`bin/`) before assuming a build is green — incremental builds can hide errors in the adapter project.
- If something is untested on real machines, say so in the PR.
- Release notes are written for actual releases, not for every commit.
