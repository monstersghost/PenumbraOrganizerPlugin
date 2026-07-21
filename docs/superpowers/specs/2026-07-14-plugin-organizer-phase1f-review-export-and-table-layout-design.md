# Plugin organizer, Phase 1f: Review Changes export + table layout fix — Design

**Status:** approved, not yet implemented.

## Context

The Review Changes tab (`MainWindow.cs:163-187`, table rendered by `PathTreeView.cs`) has two
independent, user-observed problems:

1. **No way to get a full, durable record of what a sort produced.** The tab only shows the current
   in-memory state; there's no way to save or share the results outside the plugin window.
2. **The table clips long content with no way to see it.** `PathTreeView.cs:13-15` creates its
   `ImGuiTable` with no sizing policy (`Borders | RowBg | ScrollY` only), so columns don't
   proportionally fill available width or wrap — long `ProposedPath` values get clipped at the
   column/window boundary with no ellipsis or scroll, confirmed via screenshot during this session's
   in-game verification (a long `Animation and VFX/Emotes/...` path cut off mid-string). The window's
   `MinimumSize` (`MainWindow.cs:26`) is `640x480`, narrow enough that this happens even without
   unusually long mod names.

Raised and designed together deliberately (not scope creep) — both are about "can the user actually
see/keep the full proposed-path result," just via different mechanisms (a file vs. the live window).

## Goal

1. Add an **Export** action on the Review Changes tab that writes a complete, human-readable snapshot
   of every mod's full state plus the current `Validate()` result to a text file, so the user has a
   durable, shareable, non-truncated record.
2. Fix the Review Changes table so proportional column sizing and manual resize handles let the user
   actually read long paths in the live UI, without changing what information is shown.

## Non-goals

- A file-save dialog / user-chosen output path. Reuses the existing "fixed filename in the plugin
  config directory, path shown after the action" convention this project already established with the
  since-removed Phase 1c SPIKE dump button.
- Hover tooltips for clipped cells, or restructuring which columns the Review Changes table shows.
  Considered during brainstorming as follow-on options ("B" and "C") if proportional/resizable sizing
  turns out insufficient — not adopted now; revisit only if real use shows a gap.
- Any change to `OrganizerState`, `CollisionDisambiguator`, or any sort strategy. Both pieces of this
  spec are purely additive/presentational — they read already-computed state, they don't produce it.
- Any write IPC — this phase remains read-only; Apply stays disabled.

## Architecture

### Export

A new pure, static class, `Organizer/OrganizerExportFormatter.cs`:

```csharp
public static class OrganizerExportFormatter
{
    public static string Format(IReadOnlyList<OrganizerModRow> mods, ReviewResult validation);
}
```

Takes exactly what `OrganizerState.Mods` and `OrganizerState.Validate()` already produce — no new data
source, no new computation. Pure and independently unit-testable, matching the established pattern
`ModTypeClassifier`/`CollisionDisambiguator` set (small, focused, no dependency on `OrganizerState`'s
internal `Dictionary`).

A new method on `Plugin.cs`, mirroring the removed `DumpChangedItemsSpike()`'s shape:

```csharp
internal string ExportReview()
{
    var content = OrganizerExportFormatter.Format(OrganizerState.Mods, OrganizerState.Validate());
    var path = Path.Combine(PluginInterface.ConfigDirectory.FullName, "organizer-export.txt");
    Directory.CreateDirectory(PluginInterface.ConfigDirectory.FullName);
    File.WriteAllText(path, content);
    return path;
}
```

A new **Export** button in `DrawReviewTab()` calls `_plugin.ExportReview()`, stores the returned path
in a `MainWindow` field, and displays it (e.g. `"Exported to: {path}"`) below the button so the user
can find the file.

### Table layout

Two localized changes, no new files:

- `PathTreeView.cs:14` — table flags change from `ImGuiTableFlags.Borders | RowBg | ScrollY` to
  `ImGuiTableFlags.Borders | RowBg | ScrollY | Resizable | SizingStretchProp`. `SizingStretchProp`
  makes columns share available width proportionally instead of auto-fitting to content and
  overflowing; `Resizable` lets the user manually drag a column wider when one particular value (e.g.
  a long `ProposedPath`) needs more room than its proportional share gives it.
- `MainWindow.cs:26` — `MinimumSize` widens from `new Vector2(640, 480)` to `new Vector2(900, 480)`.
  Height is untouched; this is specifically about the horizontal clipping observed, not a general
  resize.

## Export format

```
=== Penumbra Organizer Export ===
Generated: {timestamp, e.g. 2026-07-14 15:30:45}
Total mods: {count}
Protected: {count}
Collisions: {count}

--- Mods ---
Identifier: {Identifier}
Name: {Name}
Author: {Author}
Category: {Category, or "(none)" if null}
SubCategory: {SubCategory, or "(none)" if null}
HeliosphereManaged: {true|false}
Protected: {true|false}
CurrentPath: {CurrentPath}
ProposedPath: {ProposedPath}

{blank line between mods}

--- Validate() ---
Protected violations: {comma-separated Identifiers, or "(none)"}
Path collisions: {for each: "'{path}': {comma-separated Identifiers}", or "(none)" if empty}
```

Mods are listed in `OrganizerState.Mods`'s existing order (already alphabetical by `Name`, per
`OrganizerState.cs:9-10` — no separate sorting needed in the formatter).

## Data flow

Both pieces are read-only consumers of state that already exists after a scan/sort. `ExportReview()`
reads whatever `OrganizerState.Mods`/`Validate()` currently hold at the moment the button is clicked —
the same data the Review Changes table is already showing, no new scan, no re-sort, no mutation. The
table layout change affects only how `PathTreeView.Draw` renders that same data, not what data it
receives.

## Error handling

No new failure modes for the export. An empty mod list (e.g. before any scan) produces a mostly-empty
file with `Total mods: 0` and empty `--- Mods ---`/`--- Validate() ---` sections — not an error, same
"never guess, never crash on absence of data" spirit as the rest of this plugin. File-write failure
(e.g. permissions) isn't specially handled — the config directory is always writable and Dalamud
creates it, so this isn't a realistic failure mode here, consistent with this project's practice of
not adding defensive handling for scenarios that can't happen. `Directory.CreateDirectory` before the
write mirrors the removed spike button's own defensive pattern (config directory should already exist
by the time a scan has happened, but this costs nothing to keep).

## Testing

`OrganizerExportFormatter` gets direct unit tests (pure function, no running game needed, same pattern
as `ModTypeClassifier`/`CollisionDisambiguator`):

- Empty mod list, empty `ReviewResult` — produces the header with all-zero counts and empty sections,
  no exception.
- A single mod with all fields populated (`Category`/`SubCategory` both non-null, `HeliosphereManaged`
  true, `Protected` true) — every field appears correctly in its labeled line.
- A mod with `Category`/`SubCategory` both null — renders as `(none)`, not a blank line or a crash.
- A `ReviewResult` with a non-empty `ProtectedViolations` list — appears correctly in the `Validate()`
  section.
- A `ReviewResult` with a non-empty `PathCollisions` dictionary (one path, two colliding
  `Identifier`s) — appears correctly, comma-separated.
- Total/protected/collision counts in the header match the actual input (e.g. 3 mods, 1 protected, 1
  collision entry).

The table-flag and window-size changes have no unit-testable behavior (pure ImGui layout, no logic) —
verified in-game only, same as this plugin's other UI-only changes (e.g. the dark theme). `Plugin.ExportReview()`'s
file-write itself is likewise only verifiable in-game (no running Dalamud/Penumbra in the test
project) — the in-game check is: click Export, confirm the reported path exists and its contents match
what `OrganizerExportFormatter`'s own unit tests already establish the format to be.

## Open risks

1. **`organizer-export.txt` is a fixed, overwritten filename.** Each Export click replaces the
   previous file — there's no history of past exports. Explicitly chosen during brainstorming
   (over a timestamped-filename alternative) to avoid accumulating files; revisit only if a real need
   for comparing multiple exports over time surfaces.
2. **`SizingStretchProp` may still not give enough room to the longest paths in a very narrow window.**
   `Resizable` mitigates this (the user can manually widen the column), but if this proves
   insufficient in practice, the brainstorming session's deferred options — hover tooltips for
   clipped cells, or dropping `Author`/`Current Path` from this specific tab — are the next things to
   try, not preemptively built now.
