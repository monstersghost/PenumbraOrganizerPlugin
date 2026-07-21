# Handoff: Workbook import/export

Merged to `main` (both repos). This note is for whoever picks up in-game verification or the next
phase of work.

## What's on `main` now

The plugin can export a `.xlsx` workbook and import one back, interoperable with the standalone app's
existing workbook feature, by linking the standalone app's actual `WorkbookWorkflowService` rather than
reimplementing the format.

- Standalone app repo: `PenumbraOrganizer.Core/Identity/ScanIdentity.cs` — extracted
  `BuildScanIdentity`/`BuildInstallationIdentity`/`NormalizeForIdentity` out of
  `OrganizerSessionService`, which now delegates to it. No behavior change to any existing caller.
- Plugin repo: links `WorkbookWorkflowModels.cs`, `WorkbookWorkflowService.cs`, and the new
  `ScanIdentity.cs` via `<Compile Include>` (`PenumbraOrganizer.Plugin.csproj`), extending the same
  pattern already used for `ModCategory.cs`/`CreatorCanonicalizer.cs`.
- `Organizer/WorkbookAdapter.cs` — pure, unit-tested translation between `OrganizerState`/`OrganizerModRow`
  and the linked service's `ScanInventory`/`OrganizerModProposal`/`PenumbraInstallation` shapes.
  `SplitPath`/`JoinPath` bridge the one real schema gap: this plugin's `ProposedPath`/`CurrentPath` are
  full paths including the mod's leaf name, while the standalone app's `CurrentVirtualFolder`/workbook
  `destination` are folder-only.
- `Organizer/PluginLogAdapter.cs` — bridges Dalamud's `IPluginLog` to the `ILogger<T>` the linked service
  requires.
- `Plugin.ExportWorkbook(OrganizationStrategy)`/`Plugin.ImportWorkbook(string)` — new methods, both
  synchronous (block on `.GetAwaiter().GetResult()` over the linked service's `Task.Run`-based methods;
  no async execution model was introduced to this plugin's own code).
- Review Changes tab: new "Export Workbook" button + a strategy dropdown (this plugin has no persisted
  "current sort strategy" concept, so the strategy is an explicit choice at export time).
- Sort tab: new "Import Workbook" button, using `System.Windows.Forms.OpenFileDialog`
  (`<UseWindowsForms>true</UseWindowsForms>`, confirmed to build cleanly under the Dalamud SDK).

Design: `docs/superpowers/specs/2026-07-16-plugin-organizer-workbook-import-export-design.md` — read
this first, it documents an external design review and what was/wasn't adopted (see its "Revision
notes" section).
Plan: `docs/superpowers/plans/2026-07-16-plugin-organizer-workbook-import-export.md`.

Plugin test count: 211 (187 baseline + 24 new: 7 SplitPath/JoinPath, 8 ToScanInventory/ToProposals/
ToOrganizationPreferences, 6 ApplyImportResult, 3 fixture interop). Standalone app test count: 282
(unchanged — the `ScanIdentity` extraction is a pure relocation).

## Key decisions, in case they need revisiting

- The leaf segment of a reconstructed `ProposedPath` always comes from the mod's current `Name`
  (matching every existing sort strategy's `BuildPath` convention), never `Identifier` and never
  whatever leaf happened to be in `CurrentPath` before the import. This was a real correction during
  design review — the first draft assumed `Identifier` based on one export sample where `Name`
  happened to coincidentally equal `Identifier`.
- `WorkbookAdapter.ApplyImportResult` applies `Protected` *before* attempting `AssignManual` for each
  row — reversed, a row that's both unprotected and moved in the same import would have its move
  silently dropped, since `AssignManual` rejects any currently-protected row. Verified correct both by
  a dedicated unit test and by the final whole-branch review (the linked service also independently
  rejects protected+destination-change combinations upstream, so this is defense in depth).
- Export's suggested destinations come from an explicit `OrganizationStrategy` the user picks in the
  UI dropdown, not from this plugin's own already-computed `ProposedPath` values — the linked
  `WorkbookWorkflowService.BuildEditableSheet` never reads a proposal's destination at all, only
  `Protected`.
- **A real gap in the plan surfaced during Task 2's execution:** the plan only listed 3 files to link
  (`WorkbookWorkflowModels.cs`, `WorkbookWorkflowService.cs`, `ScanIdentity.cs`), but the actual
  dependency closure also required linking `DomainModels.cs`, `OrganizerModels.cs`, and
  `ModClassificationModels.cs` (all real, unmodified upstream files — `ModScanResult`,
  `OrganizerModProposal`, `ScanInventory`, `PenumbraInstallation`, `OrganizationPreferences`, and
  `ModTargetClassification` all live there, not in `WorkbookWorkflowModels.cs`), plus a new
  hand-written `PenumbraOrganizer.Plugin/Interfaces/IWorkbookWorkflowService.cs` (namespace
  `PenumbraOrganizer.Core.Interfaces`, verified byte-for-byte against the real upstream interface) so
  the linked `WorkbookWorkflowService : IWorkbookWorkflowService` resolves without pulling in the
  sibling repo's much larger multi-interface `Services.cs` file. This was verified as a necessary,
  minimal fix (not scope creep) by both the task review and the final whole-branch review. If the
  standalone app's `IWorkbookWorkflowService` interface ever changes, the plugin's copy will need a
  matching update — drift shows up as a compile error in the plugin build, not silent breakage.

## What's NOT done yet

**Not yet in-game verified.** Per the design spec's Testing section: export a workbook from a real
library, confirm it opens correctly in Excel; open the same file in the standalone app and confirm
it's recognized as valid for that install; edit destinations, import back into the plugin, confirm
resolved `ProposedPath` values look correct and Apply behaves normally; separately, export from the
standalone app and import into the plugin to confirm the reverse direction; confirm
`installationIdentity` actually matches between the plugin's IPC-derived path and the standalone app's
file-system-discovered path for the same real install (see the design spec's Open risks #2 — this is
the one part of the identity story that couldn't be fully closed by code alone).

The final whole-branch review also flagged, as the top pre-real-use item: **the synchronous execution
model's actual in-game behavior is unverified.** `ExportWorkbook`/`ImportWorkbook` block the ImGui
render thread via `.GetAwaiter().GetResult()`, and `OpenFileDialog.ShowDialog()` runs a modal WinForms
message loop on that same thread. This was a deliberate design choice (matching every other button
handler in this plugin) and the `UseWindowsForms` setting was confirmed to *build* cleanly, but nobody
has yet exercised (a) whether a large mod library's export causes a noticeable stall, or (b) whether a
native modal dialog behaves correctly inside a DirectX-hooked game process. Exercise this before
depending on the feature for a real library.

## Process note

Executed via subagent-driven-development in a dedicated worktree
(`.claude/worktrees/plugin-organizer-workbook-import-export`), 9 tasks + a final whole-branch review,
all clean after one fix (a Minor doc-comment finding from the final review, addressed directly rather
than via another subagent dispatch). No worktree-boundary violations. One real process finding: Task
2's implementer correctly diagnosed and fixed a genuine gap in the plan itself (the linked-file list
was incomplete) rather than working around it or reporting blocked — both the task-level review and
the final whole-branch review independently verified the fix was the minimal necessary one, not
over-linking. This is the kind of cross-task/plan-level issue the final whole-branch review exists to
catch, alongside what individual task reviews (each seeing only one diff) cannot.
