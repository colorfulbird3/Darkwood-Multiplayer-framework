# Darkwood Multiplayer Framework

A multiplayer framework experiment for Darkwood.

Current status: beta / experimental.

## What it does

- Host / client multiplayer over TCP (Telepathy)
- Player synchronization (15 Hz poses, interpolation)
- Save and world snapshot transfer (SHA-256 chunked)
- Runtime entity replication (loot containers, dropped items, night event enemies)
- Shared container / inventory sync with optimistic revision checks
- Basic combat / downed / rescue sync

## Current limitations

- Real two-machine testing is still incomplete.
- Some runtime entities still rely on tolerant snapshot handling.
- Transport is TCP only for now.
- No wire compatibility: host and client must run the same version.
- The codebase is still being reorganized.

## Installation

Grab the latest ZIP from Releases, unzip into `BepInEx/plugins/`, then start the game.

Hotkeys:

| Key | Action |
|---|---|
| F6 | multiplayer panel |
| F1 | host |
| F2 | join |
| F3 | stop |
| F4 | rescue a downed player |

Host and client need the same version. Steam online required (saves carry achievement data).

## Development

BepInEx 5 · Harmony · Telepathy · .NET / C# (net472 / netstandard2.0)

Building needs a local Darkwood install: the projects reference `Darkwood_Data/Managed/` assemblies, so clone the repo inside the game directory (e.g. `.../steamapps/common/Darkwood/Darkwood Multiplayer framework`).

```
dotnet build '.\src\DarkwoodMultiplayerFramework.sln' -c Release -m:1 -p:MSBuildEnableWorkloadResolver=false
dotnet test '.\tests\DarkwoodMultiplayerFramework.UnitTests\DarkwoodMultiplayerFramework.UnitTests.csproj'
```

Unit tests and SelfTests pass; counts are recorded from the actual run at each release in the matching `RELEASE-NOTES-<version>.md` (latest: 50 unit tests / 85 SelfTests). The in-game loopback test covers handshake → save transfer → snapshot → READY.

See `docs/` for architecture notes and problem write-ups. See `CONTRIBUTING.md` for development rules.

## License

MIT. Third-party notices in `THIRD-PARTY-NOTICES.md`.
