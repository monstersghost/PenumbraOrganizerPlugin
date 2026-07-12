# Penumbra Organizer Plugin (MVP spike)

A local, dev-only Dalamud plugin. Read-only technical spike, not a replacement for the standalone
[Penumbra Organizer](https://github.com/monstersghost/PenumbraOrganizer) app, which remains the
primary, supported product.

## What this is

Validates whether Penumbra's in-process IPC (`GetModList`, `GetModPath`, `ModAdded`/`ModDeleted`/
`ModMoved`) is worth building on further. It lists installed mods, resolves each one's current
virtual folder path, and shows live add/delete/move events as they happen in Penumbra. It makes no
write calls — `SetModPath`, `ReloadMod`, `InstallMod`, etc. are intentionally not used here.

Design rationale and the full API research behind this (including why Penumbra's external HTTP API
can't reach `SetModPath`, only in-process IPC can) live in the main repo:
[`docs/superpowers/specs/2026-07-12-dalamud-plugin-feasibility-design.md`](https://github.com/monstersghost/PenumbraOrganizer/blob/main/docs/superpowers/specs/2026-07-12-dalamud-plugin-feasibility-design.md).

## Status

Not submitted to any plugin repository (testing or live). Load locally only, via Dalamud's dev-plugin
mechanism, for your own testing.

## Building

Requires the .NET 8 SDK. Open `PenumbraOrganizer.Plugin.sln` or build from the command line:

```
dotnet build
```

The `Dalamud.NET.Sdk` NuGet package resolves the Dalamud API references automatically; no local
XIVLauncher/Dalamud install path needs to be configured to build.

## Loading in-game for testing

1. Build the project (produces `bin/x64/Debug/PenumbraOrganizer.Plugin.json` next to the built DLL).
2. In-game, open the Dalamud settings (`/xlsettings`) → **Experimental** → **Dev Plugin Locations**,
   and add the path to the build output folder containing `PenumbraOrganizer.Plugin.json`.
3. Open the plugin installer (`/xlplugins`) → **Dev Tools** tab, enable **Penumbra Organizer (MVP)**.
4. Use `/porganizer` or the plugin installer's icon to open the window.

Penumbra must be installed and running for the IPC calls to resolve; otherwise **Refresh mod list**
will show a connection error.

## Scope boundary

No writes, no shared code with the standalone app, no public distribution. See the design doc above
for the full list of what's explicitly out of scope for this MVP.
