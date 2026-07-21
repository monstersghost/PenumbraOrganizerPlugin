# Plugin organizer, Phase 1: live scan/sort/protect/review — Design

**Status:** approved by user, 2026-07-12. Not yet implemented.

## Context

The standalone app (`C:\Repo\PenumbraOrganizer`) organizes Penumbra's virtual mod folders by
reading and writing Penumbra's state files directly, entirely offline. This plugin
(`C:\Repo\PenumbraOrganizer.Plugin`) currently ships a read-only MVP spike: it lists mods and
resolves their current Penumbra paths via Dalamud IPC (`GetModList`, `GetModPath`), with no writes.

This spec covers extending the plugin into a live, in-game equivalent of the app's core organize
loop — Scan, Sort Method, Protect, Review Changes — reading classification signals from Penumbra's
own IPC instead of the app's file-path heuristics. It does not cover Apply, Backup/Rollback,
Workbook export/import, or Folder Cleanup; those are later phases, out of scope here.

## Goal

Prove that a live, IPC-driven organize pipeline (scan → propose → protect → review) works
end-to-end inside the game, before any code writes to Penumbra's state.

## Non-goals (explicitly out of scope for this spec)

- Apply / any call to `SetModPath` or other write IPC — stays disabled/dev-only until a
  separate Phase 2 spec covering backup exists.
- Backup and rollback.
- Workbook export/import (Phase 3).
- Folder Cleanup (Phase 4 — revisit once this phase is live; root cause is Penumbra's own
  persistent folder list, likely independent of write path, but unconfirmed).
- Any change to the standalone app.
- Sharing runtime/binary code between the two repos.

## Phasing within this spec

The work splits into three slices, each independently shippable, to isolate the one genuinely
unproven part (mod-type classification) from everything else:

- **1a — Core pipeline, zero classification risk.** Scan (mod list + current-path resolution,
  extended with Heliosphere detection), Protect (manual + Heliosphere auto-protect), Start
  Manually sort strategy, Review Changes (diff view + validation). No automated classification
  anywhere in this slice.
- **1b — By creator.** Adds the "By creator" sort strategy, using `ModProperty.Author` — a
  structured field, not a parsed string. Low risk, additive to 1a.
- **1c — By mod type.** Adds the "By mod type" sort strategy, gated on the classification-format
  spike below succeeding. If the spike fails, 1a and 1b still ship independently.

## Architecture

All new code lives in `PenumbraOrganizer.Plugin`. No new repos, no shared binary/package
dependency on `PenumbraOrganizer.Core`.

**Code sharing via MSBuild linked source files, where genuinely reusable.** Two artifacts from
`PenumbraOrganizer.Core` are pure (no file I/O, no DI, no app-specific types) and get linked
directly into the plugin's `.csproj` via `<Compile Include="..\..\PenumbraOrganizer\PenumbraOrganizer.Core\..." Link="..." />`,
so both repos compile the same source with no runtime or build coupling:

- `Classification/ModClassificationModels.cs` — specifically the `ModCategory` enum (the shared
  category taxonomy as it exists today: Gear, Weapon, Face, Hair, Body, Skin, NPC, Minion, Mount,
  Pet, Ornament, Furniture, VFX, Sound, Animation, Others). The path-oriented records in the same
  file (`ModTargetClassification`, `CanonicalGameTarget`)
  are **not** linked — they're shaped around file-path classification and don't apply to an
  IPC-derived signal.
- `Services/CreatorCanonicalizer.cs` plus its `ICreatorCanonicalizer` interface — the creator-alias
  merge table (`enni` → `Enni`, etc.) is pure `Dictionary`/`StringComparer` logic with no external
  dependencies. Confirmed dependency-free by inspection before this spec was written.

Everything else — the actual mod-type classification logic that turns Penumbra IPC signals into a
`ModCategory` — is new code written fresh in the plugin, because its input shape (parsed
`GetChangedItems` strings) is fundamentally different from the app's input shape (file paths). This
is not a porting exercise; it's a new implementation reusing only the shared taxonomy and the
creator-alias table.

Linked files require both repos to remain sibling checkouts on the same machine (already true:
`C:\Repo\PenumbraOrganizer` and `C:\Repo\PenumbraOrganizer.Plugin`). If the app repo is ever
retired or moved, the links are replaced with local copies — a trivial, mechanical change.

**Persistence:** protection state (which mods are protected, Heliosphere auto-protect history) is
stored via Dalamud's own `IPluginConfiguration` + `PluginInterface.SavePluginConfig`/
`GetPluginConfig()`, not a hand-rolled JSON file. Fully separate from the app's
`%LocalAppData%\PenumbraOrganizer\` data — no shared state between app and plugin.

**UI:** single window (already established: `MainWindow` in the existing `WindowSystem`), multiple
ImGui tabs (`ImGui.BeginTabBar`) for Scan / Sort / Protect / Review, rather than separate `Window`
instances. A single reusable tree+table widget (folder tree left, mod table right, parameterized by
which path column(s) it renders) backs both the Scan view and the Review Changes diff view, instead
of separate bespoke implementations per tab.

Worth adopting while touching `MainWindow.cs` regardless of the above: `Dalamud.Interface.Utility.Raii`
for ImGui Begin/End pairs (replacing the current manual `if (ImGui.BeginTable(...)) { ... EndTable(); }`
pattern, which is a latent bug source on early-return paths) and `Dalamud.Interface.Colors` for the
existing hand-rolled error-text `Vector4`.

## Components

### Scan (1a)

Extends the existing `GetModList` + `GetModPath` refresh with:
- Heliosphere detection per mod: directory-name `hs-` prefix (available directly from
  `GetModList`'s keys, no extra I/O) or presence of `heliosphere.json` in the mod's directory
  (one `File.Exists` check per mod, using `GetModDirectory()` + directory name — a plain
  filesystem check the plugin is free to make; it is not restricted to IPC-only data).

Produces the shared tree+table view described above, showing current organization.

### Protection (1a)

Manual mark/unmark per mod or folder, plus automatic Heliosphere protection on every scan —
mirroring the app's existing behavior exactly: mods are auto-protected the moment they're detected,
with a reminder message (singular/plural variants matching the app's existing copy) and a bulk
toggle command to unprotect all Heliosphere-managed mods at once if the user wants to override it.
Persisted via `IPluginConfiguration` as described above.

### Sort strategies

- **Start Manually (1a):** user assigns proposed paths directly; no automated classification
  involved.
- **By creator (1b):** groups by `ModProperty.Author`, applying the linked `CreatorCanonicalizer`
  for alias merging.
- **By mod type (1c):** groups by `ModCategory`, derived from parsing `GetChangedItems` — see
  Classification below.

### Classification (1c only)

`GetChangedItems(modDirectory, modName)` returns `Dictionary<string, object>`. The value is an
opaque, undocumented object not safe to inspect from outside Penumbra's IPC boundary. The key is
Penumbra's own display string for the changed item (the same text shown in Penumbra's "Changed
Items" tab), by community convention formatted `"{Slot/Category}, {Item name}"`.

**This format is an assumption, not a confirmed fact as of this spec.** Before writing any parsing
logic, the implementation must spike: call `GetChangedItems` against ~10 real, varied mods in-game,
log the raw keys, and confirm the slot-prefix convention holds (including under the game client's
current locale). If it holds, classification applies the app's existing slot-priority rule (any
equipment-slot signal in head/body/hand/legs/feet wins regardless of other signals present) against
the parsed prefix. If a key doesn't parse cleanly, the mod's category is `Unknown` — never a guess.
If the spike fails outright (format doesn't hold, or varies by locale in a way that can't be
normalized), 1c is blocked and 1a/1b ship without it; this spec does not prescribe a fallback
classification method in that case.

### Review Changes (1a)

Diff view (current vs. proposed) reusing the shared tree+table widget, with the app's existing
validation checks: protected rows must be unchanged, no collisions. Fully functional even with
Apply disabled — this is how 1a proves the pipeline is correct ahead of any future Phase 2 write
path.

## Data flow

Scan builds the in-memory model (mod → current path, protected flag, Heliosphere flag, classified
category once 1c exists) → sort strategy selection populates `ProposedPath` per mod, skipping
protected rows → Protect marks/unmarks rows, re-validating that sort strategies don't touch
protected items → Review Changes computes and displays the diff. Nothing is written to Penumbra at
any point in this phase. No state persists across plugin reloads except the protection list
(`IPluginConfiguration`) — the proposal itself is rebuilt from scratch on every scan.

## Error handling

IPC failures (Penumbra not running/not loaded) surface via the same inline error pattern already in
`MainWindow.cs`. Classification parse failures fall back to `Unknown`, consistent with the app's
existing "send to Review" behavior for uncertain items — never a silent misclassification.

## Testing

The linked `CreatorCanonicalizer` and any new classification-parsing logic are pure functions
(string/dictionary in, category out) — unit-testable without a running game, same pattern as
`PenumbraOrganizer.Tests`. Scan/IPC integration (Heliosphere detection, `GetChangedItems` format,
end-to-end pipeline) is only verifiable in-game, same as today's MVP.

## Open risks

1. `GetChangedItems` key-string format is unverified — first implementation task for 1c must be
   the empirical spike described above, before any parsing code is written.
2. Linked source files assume both repos stay sibling checkouts on the same machine; this is true
   today but is a real constraint, not a given.
