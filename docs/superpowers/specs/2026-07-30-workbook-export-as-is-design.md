# Workbook export: keep current folders (as-is)

Date: 2026-07-30

## Problem

The Review Changes tab exports an Excel workbook whose Destination column is a computed suggestion.
The user picks one of four sorting strategies from a dropdown and every row gets a proposed folder
derived from that strategy.

There is no way to export the library as it actually stands. A user who wants to hand-write the
whole layout in Excel has to first pick a strategy they do not want, then delete every suggestion it
produced. The standalone app offered this as an export mode and the plugin does not.

## What already exists

`WorkbookWorkflowService` is a linked file shared with the standalone app repo
(`PenumbraOrganizer.Infrastructure/Exports/WorkbookWorkflowService.cs`). It already handles this
case:

- `BuildSuggestedDestination` maps `OrganizationStrategy.PreserveAndClean` to
  `mod.CurrentVirtualFolder`.
- `StrategyLabel` maps it to `"keep current"`, which flows into the export summary string and the
  `_Metadata` sheet.
- Import ignores the recorded strategy entirely, so no import path needs to learn about this.

The plugin never offers the option. `MainWindow.WorkbookStrategyOptions` lists only the four sorting
strategies.

## Design

### The change

Append a fifth entry to `MainWindow.WorkbookStrategyOptions`:

```csharp
("Keep current folders (as-is)", OrganizationStrategy.PreserveAndClean)
```

Appended rather than inserted, so the existing `_workbookStrategyIndex = 2` default still selects
"By Type Then Creator". That field is a plain in-memory field that resets each session; there is no
persisted index to migrate.

Relabel the combo from `"Workbook suggestion strategy"` to `"Workbook destinations"`. An as-is
export suggests nothing, so the old label misdescribes the new entry.

No other production file changes. `Plugin.ExportWorkbook` already accepts a strategy and passes it
straight through to the service. **The shared linked file is not modified**, so the plugin and the
standalone app cannot diverge over this feature.

### Behaviour

Every unprotected mod's Destination is its current folder in Penumbra.

Two kinds of row get a blank Destination:

- **Protected mods.** `BuildSuggestedDestination` returns `string.Empty` for protected mods under
  every strategy, and as-is is not special-cased. This was a deliberate choice: special-casing it
  would mean editing the shared file.
- **Root-level mods.** Their `CurrentVirtualFolder` is empty, so the cell is empty. Unavoidable and
  already true of `PreserveAndClean` in the standalone app.

Blank is not a data loss. `TryResolveDestination` returns a null resolved destination for blank
input, and `WorkbookAdapter.ApplyImportResult` skips `AssignManual` when the resolved destination is
null. Blank therefore means "leave this mod where it is", which is the correct as-is semantic for
both cases.

The property that follows: **an as-is export, imported back unedited, changes nothing.** Every row's
`ProposedPath` still equals its `CurrentPath` afterwards.

### What as-is does not do

It reads the live library, not the staged proposals. If the user has run a Sort and is looking at
proposed paths on Review Changes, an as-is export ignores those and exports current folders.
Exporting does not clear or otherwise disturb the staged sort.

## Testing

One new test in `PenumbraOrganizer.Plugin.Tests/Organizer/WorkbookInteropTests.cs`, following the
existing export/edit/import pattern in that file.

`AsIsExport_ImportedUnedited_LeavesEveryProposedPathUnchanged`:

- Build an `OrganizerState` with three mods: one nested in a folder, one protected, one at the
  library root.
- Export with `OrganizationStrategy.PreserveAndClean`.
- Assert the nested mod's Destination cell equals its current folder. Without this the test would
  still pass if the dropdown entry were wired to the wrong strategy, since a round-trip through any
  strategy the user did not edit would still be internally consistent.
- Import the workbook unedited.
- Assert no errors, and that every mod's `ProposedPath` equals its `CurrentPath`.

## Documentation

- `docs/USER_GUIDE.md`, Review Changes section: explain that "Keep current folders (as-is)" exports
  the library as it stands, and that this is the mode to pick when hand-writing a layout from
  scratch rather than starting from a suggestion.
- `docs/RELEASE_NOTES_0.5.2.0.md`: a short "Added" entry.

## Known wrinkle

`PreserveAndClean` is named for a standalone-app concept, preserving folders and cleaning up empty
ones. Nothing in the plugin acts on the name; the value is only ever passed to the export. If a
future plugin sort strategy uses that enum value for its own meaning, the two will collide. Not
worth solving now.
