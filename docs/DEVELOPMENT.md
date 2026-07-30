# Development

Build instructions and other contributor-facing details that don't belong in the top-level
README.

## Building from source

Open `PenumbraOrganizer.Plugin.sln`, or build from the command line:

```
dotnet build
```

The `Dalamud.NET.Sdk` NuGet package resolves the Dalamud API references and target framework on
its own. No local XIVLauncher or Dalamud install path needs to be configured to build.

## Loading a local build in-game

1. Build the project. This produces `PenumbraOrganizer.Plugin.json` next to the built DLL, under
   `PenumbraOrganizer.Plugin/bin/Debug/`. The Dalamud SDK writes the output flat, with no
   target-framework subfolder.
2. In-game, open Dalamud settings (`/xlsettings`), go to Experimental, then Dev Plugin Locations,
   and add the path to that build output folder. If you build from a git worktree, point this at
   that worktree's own `bin/Debug` and change the existing entry rather than adding a second one —
   two checkouts produce two builds under the same plugin name, and Dalamud will load whichever
   path is registered.
3. Open the plugin installer (`/xlplugins`), go to the Dev Tools tab, and enable Penumbra
   Organizer.
4. Open the window with `/porganizer` or the plugin installer's icon.

To update: rebuild, then disable/re-enable the plugin (or restart the game) to pick up the new
DLL.

Penumbra has to be installed and running for the IPC calls to resolve. If it isn't, "Refresh mod
list" shows a connection error instead of a mod list.

## Relationship to the standalone app

This plugin shares part of its classification and workbook logic with the standalone
[Penumbra Organizer](https://github.com/monstersghost/PenumbraOrganizer) app through cross-repo
file linking. It's a complete tool on its own, though, not a trimmed-down companion.

## Further reading

- [docs/USER_GUIDE.md](USER_GUIDE.md): how to use the plugin, tab by tab.
- [docs/TECHNICAL_SPEC.md](TECHNICAL_SPEC.md): architecture, module map, build and test.
- [docs/HOW_SORTING_WORKS.md](HOW_SORTING_WORKS.md): the classification and sort-strategy logic.
- [docs/ROADMAP.md](ROADMAP.md): feature status and open items.
- [docs/TESTING_GUIDE.md](TESTING_GUIDE.md): the closed-testing checklist (also bundled as a
  PDF/HTML guide in each testing release zip under `testing/`).

The design rationale and the original IPC feasibility research, including why Penumbra's external
HTTP API can't reach `SetModPath` and only in-process IPC can, live in the standalone app repo:
[`docs/superpowers/specs/2026-07-12-dalamud-plugin-feasibility-design.md`](https://github.com/monstersghost/PenumbraOrganizer/blob/main/docs/superpowers/specs/2026-07-12-dalamud-plugin-feasibility-design.md).

## Scope boundary

Every write the plugin makes was its own explicit scope decision, and each was kept narrow on
purpose. See "Filesystem crossings" in [docs/TECHNICAL_SPEC.md](TECHNICAL_SPEC.md) for the exact
list. Out of scope until there's a fresh, explicit decision to change it: any write IPC call beyond
`SetModPath`, a self-update pipeline beyond the self-hosted repo mechanism, and submitting to the
official Dalamud plugin repository.
